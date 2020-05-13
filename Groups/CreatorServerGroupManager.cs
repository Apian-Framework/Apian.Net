using System.Linq;
using System.Net.Http;
using System;
using System.Collections.Generic;
using UniLog;
namespace Apian
{

    public class CreatorServerGroupManager : ApianGroupManagerBase, IApianGroupManager
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
        private long NextNewCommandSeqNum;
        public override long GetNewCommandSequenceNumber() => NextNewCommandSeqNum++;
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
            Logger.Info($"{this.GetType().Name}.CreateNewGroup(): {groupId}");
            // Creating a new group
            ApianGroupInfo newGroupInfo = new ApianGroupInfo(CreatorServerGroupType, groupId, LocalPeerId, groupName);
            InitExistingGroup(newGroupInfo);
        }

        public void InitExistingGroup(ApianGroupInfo info)
        {
            Logger.Info($"{this.GetType().Name}.InitExistingGroup(): {info.GroupId}");
            GroupInfo = info;
            ApianInst.GameNet.AddApianInstance(ApianInst, info.GroupId);
        }

        public void JoinGroup(string groupId, string localMemberJson)
        {
            // Local call.
            Logger.Info($"{this.GetType().Name}.JoinGroup(): {groupId}");
            ApianInst.GameNet.AddChannel(GroupId);
            ApianInst.GameNet.SendApianMessage(GroupCreatorId, new GroupJoinRequestMsg(groupId, LocalPeerId, localMemberJson));
        }

        private ApianGroupMember _AddMember(string peerId, string appDataJson)
        {
            Logger.Info($"{this.GetType().Name}._AddMember(): ({(peerId==LocalPeerId?"Local":"Remote")}) {peerId}");
            ApianGroupMember newMember =  new ApianGroupMember(peerId, appDataJson);
            newMember.CurStatus = ApianGroupMember.Status.Joining;
            Members[peerId] = newMember;
            return newMember;
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
                ApianInst.GameNet.SendApianMessage(msgChan, msg.ToCommand(GetNewCommandSequenceNumber()));
        }

        public void OnApianObservation(ApianObservation msg, string msgSrc, string msgChan)
        {
            // Observations from the server are turned into commands by the server
            if ((GroupCreatorId == LocalPeerId) && (msgSrc == LocalPeerId))
                ApianInst.GameNet.SendApianMessage(msgChan, msg.ToCommand(GetNewCommandSequenceNumber()));
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
                Logger.Info($"{this.GetType().Name}.OnGroupJoinRequest(): Affirming {jreq.DestGroupId} from {jreq.PeerId}");

                // Send current members to new joinee - do it bfore sending the new peers join msg
                _SendMemberJoinedMessages(jreq.PeerId);

                // Just approve. Don't add (happens in OnGroupMemberJoined())
                GroupMemberJoinedMsg jmsg = new GroupMemberJoinedMsg(GroupId, jreq.PeerId, jreq.ApianClientPeerJson);
                ApianInst.SendApianMessage(GroupId, jmsg); // tell everyone about the new kid last

                // Now send status updates (from "joined") for any member that has changed status
                _SendMemberStatusUpdates(jreq.PeerId);

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

        // private void _DeclareAllJoinersActive()
        // {
        //     List<GroupMemberStatusMsg> statusMsgs = Members.Values
        //         .Where( (m) => m.CurStatus == ApianGroupMember.Status.Joining)
        //         .Select( (m) =>  new GroupMemberStatusMsg(GroupId, m.PeerId, ApianGroupMember.Status.Active)).ToList();
        //     foreach (GroupMemberStatusMsg msg in statusMsgs)
        //         ApianInst.SendApianMessage(GroupId, msg);
        // }

        private void OnGroupMemberJoined(ApianGroupMessage msg, string msgSrc, string msgChannel)
        {
            // If from GroupCreator then it's valid
            if (msgSrc == GroupCreatorId)
            {
                GroupMemberJoinedMsg joinedMsg = (msg as GroupMemberJoinedMsg);
                Logger.Info($"{this.GetType().Name}.OnGroupMemberJoined() from boss:  {joinedMsg.DestGroupId} adds {joinedMsg.PeerId}");

                ApianGroupMember m = _AddMember(joinedMsg.PeerId, joinedMsg.ApianClientPeerJson);

                // Unless we are the group creator AND this is OUR join message
                if (joinedMsg.PeerId == LocalPeerId && GroupCreatorId == LocalPeerId)
                {
                    // Yes, Which means we're also the first. Declare  *us* "Active" and tell everyone
                    ApianInst.SendApianMessage(GroupId, new GroupMemberStatusMsg(GroupId, LocalPeerId, ApianGroupMember.Status.Active));
                }

                // // Super hack: if we are the Creator, and there a 2 memebers, then mark us both as 'active" and everything starts!
                // if ( GroupCreatorId == LocalPeerId && Members.Count() == 2)
                // {
                //     _DeclareAllJoinersActive();
                // }

            }
        }

        private void OnGroupMemberStatus(ApianGroupMessage msg, string msgSrc, string msgChannel)
        {
            if (msgSrc == GroupCreatorId) // If from GroupCreator then it's valid
            {
                GroupMemberStatusMsg sMsg = (msg as GroupMemberStatusMsg);
                Logger.Info($"{this.GetType().Name}.OnGroupMemberStatus():  {sMsg.PeerId} to {sMsg.MemberStatus}");
                if (Members.Keys.Contains(sMsg.PeerId))
                {
                    ApianGroupMember m = Members[sMsg.PeerId];
                    ApianGroupMember.Status old = m.CurStatus;
                    m.CurStatus = sMsg.MemberStatus;
                    ApianInst.OnGroupMemberStatusChange(m, old);
                }
                else
                    Logger.Error($"{this.GetType().Name}.OnGroupMemberStatus(): Member not present" );
            }
        }

       public ApianMessage DeserializeGroupMessage(string subType, string json)
        {
            return null;
        }

    }
}