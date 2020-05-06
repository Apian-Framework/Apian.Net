using System.Linq;
using System.Net.Http;
using System;
using System.Collections.Generic;
using UniLog;
namespace Apian
{

    public class CreatorServerGroupManager : IApianGroupManager
    {
       // ReSharper disable MemberCanBePrivate.Global,UnusedMember.Global,FieldCanBeMadeReadOnly.Global

        private ApianBase ApianInst {get; }
        public string MainP2pChannel {get => ApianInst.GameNet.CurrentGameId();}
        public UniLogger Logger;
        private readonly Dictionary<string, Action<ApianGroupMessage, string, string>> GroupMsgHandlers;
        private const string CreatorServerGroupType = "CreatorServerGroup";

        // IApianGroupManager
        public ApianGroupInfo GroupInfo {get; private set;}
        public string GroupType {get => CreatorServerGroupType;}
        public string GroupId {get => GroupInfo.GroupId;}
        public string GroupCreatorId {get => GroupInfo.GroupCreatorId;}
        public string LocalPeerId {get => ApianInst.GameNet.LocalP2pId();}
        public Dictionary<string, ApianGroupMember> Members {get;}
        public bool Intialized {get => GroupInfo != null; }


        public CreatorServerGroupManager(ApianBase apianInst)
        {
            GroupMsgHandlers = new Dictionary<string, Action<ApianGroupMessage, string, string>>() {
                {ApianGroupMessage.GroupsRequest, OnGroupsRequest },
                {ApianGroupMessage.GroupJoinRequest, OnGroupJoinRequest },
                {ApianGroupMessage.GroupMemberJoined, OnGroupMemberJoined },
                {ApianGroupMessage.GroupMemberStatus, OnGroupMemberStatus }
            };

            Logger = UniLogger.GetLogger("ApianGroup");
            ApianInst = apianInst;
            Members = new Dictionary<string, ApianGroupMember>();
         }

        public void CreateNewGroup(string groupId, string groupName)
        {
            // Creating a new group
            ApianGroupInfo newGroupInfo = new ApianGroupInfo(CreatorServerGroupType, groupId, LocalPeerId, groupName);
            InitExistingGroup(newGroupInfo);
        }

        public void InitExistingGroup(ApianGroupInfo info)
        {
            GroupInfo = info;
            ApianInst.GameNet.AddApianInstance(ApianInst, info.GroupId);
        }

        public void JoinGroup(string groupId, string localMemberJson)
        {
            // Local call.
            ApianInst.GameNet.AddChannel(GroupId);
            ApianInst.GameNet.SendApianMessage(GroupCreatorId, new GroupJoinRequestMsg(groupId, LocalPeerId, localMemberJson));
        }

        private void _AddMember(string peerId, string appDataJson)
        {
            ApianGroupMember newMember =  new ApianGroupMember(peerId, appDataJson);
            newMember.CurStatus = ApianGroupMember.Status.Joining;
            Members[peerId] = newMember;
        }

        public void Update()
        {

        }

        public void OnApianMessage(ApianMessage msg, string msgSrc, string msgChannel)
        {
            if (msg != null && msg.MsgType == ApianMessage.GroupMessage)
            {
                ApianGroupMessage gMsg = msg as ApianGroupMessage;
                GroupMsgHandlers[gMsg.GroupMsgType](gMsg, msgSrc, msgChannel);
            }
            else
                Logger.Warn($"OnApianMessage(): unexpected APianMsg Type: {msg?.MsgType}");
        }

        public void OnApianRequest(ApianRequest msg, string msgSrc, string msgChan)
        {
            // Creator sends out command
            if (GroupCreatorId == LocalPeerId)
                ApianInst.GameNet.SendApianMessage(msgChan, msg.ToCommand());
        }

        public void OnApianObservation(ApianObservation msg, string msgSrc, string msgChan)
        {
            // Observations from the server are turned into commands by the server
            if ((GroupCreatorId == LocalPeerId) && (msgSrc == LocalPeerId))
                ApianInst.GameNet.SendApianMessage(msgChan, msg.ToCommand());
        }

        public bool ValidateCommand(ApianCommand msg, string msgSrc, string msgChan)
        {
            // Valid if from the creator
            return msgSrc == GroupCreatorId;
        }

      private void OnGroupsRequest(ApianGroupMessage msg, string msgSrc, string msgChannel)
        {
            // Only the creator answers
            if (GroupCreatorId == LocalPeerId)
            {
                GroupAnnounceMsg amsg = new GroupAnnounceMsg(GroupInfo);
                ApianInst.SendApianMessage(msgSrc, amsg);
            }
        }

        private void OnGroupJoinRequest(ApianGroupMessage msg, string msgSrc, string msgChannel)
        {
            // In this implementation the creator decides
            // Everyone else just ignores this.
            if (GroupCreatorId == LocalPeerId)
            {
                GroupJoinRequestMsg jreq = msg as GroupJoinRequestMsg;
                // Send info about current members to new joinee
                _SendMemberJoinedMessages(jreq.PeerId);
                _SendMemberStatusUpdates(jreq.PeerId);

                // Just approve. Don't add (happens in OnGroupMemberJoined())
                GroupMemberJoinedMsg jmsg = new GroupMemberJoinedMsg(GroupId, jreq.PeerId, jreq.ApianClientPeerJson);
                ApianInst.SendApianMessage(GroupId, jmsg); // tell everyone about the new kid last

            }
        }

        private void _SendMemberJoinedMessages(string toWhom)
        {
            // Send a newly-approved member all of the Join messages for the other member
            // Create messages first, then send (mostly so Members doesn;t get modified while enumerating it)
            List<GroupMemberJoinedMsg> joinMsgs = Members.Values
                .Where( m => m.PeerId != toWhom)
                .Select( (m) =>  new GroupMemberJoinedMsg(GroupId, m.PeerId, m.AppDataJson)).ToList();
            foreach (GroupMemberJoinedMsg msg in joinMsgs)
                ApianInst.SendApianMessage(toWhom, msg);
        }

        private void _SendMemberStatusUpdates(string toWhom)
        {
            // Send a newly-approved member the status of every non-"Joining" member (since a joinmessage was already sent)
            List<GroupMemberStatusMsg> statusMsgs = Members.Values
                .Where( (m) => m.CurStatus != ApianGroupMember.Status.Joining && m.PeerId != toWhom)
                .Select( (m) =>  new GroupMemberStatusMsg(GroupId, m.PeerId, m.CurStatus)).ToList();
            foreach (GroupMemberStatusMsg msg in statusMsgs)
                ApianInst.SendApianMessage(toWhom, msg);
        }

        private void OnGroupMemberJoined(ApianGroupMessage msg, string msgSrc, string msgChannel)
        {
            // If from GroupCreator then it's valid
            if (msgSrc == GroupCreatorId)
            {
                GroupMemberJoinedMsg joinedMsg = (msg as GroupMemberJoinedMsg);
               _AddMember(joinedMsg.PeerId, joinedMsg.ApianClientPeerJson);
                ApianInst.OnGroupMemberJoined(joinedMsg.ApianClientPeerJson);

                // Are WE the group creator and is this OUR join message?
                if (joinedMsg.PeerId == LocalPeerId && GroupCreatorId == LocalPeerId)
                {
                    // Yes, Which maens we're also the first. Mark us "Active" and tell our app
                    // TODO: Make this less hackly-seeming.
                    Members[LocalPeerId].CurStatus = ApianGroupMember.Status.Active;
                    ApianInst.OnGroupMemberStatus(LocalPeerId, ApianGroupMember.Status.Active);
                }
            }
        }

        private void OnGroupMemberStatus(ApianGroupMessage msg, string msgSrc, string msgChannel)
        {
            if (msgSrc == GroupCreatorId) // If from GroupCreator then it's valid
            {
                GroupMemberStatusMsg sMsg = (msg as GroupMemberStatusMsg);
                ApianGroupMember m = Members[sMsg.PeerId];
                m.CurStatus = sMsg.MemberStatus;
                ApianInst.OnGroupMemberStatus(m.PeerId, m.CurStatus);
            }
        }

       public ApianMessage DeserializeGroupMessage(string subType, string json)
        {
            return null;
        }

    }
}