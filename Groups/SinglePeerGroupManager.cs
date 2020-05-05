using System.Net.Http;
using System;
using System.Collections.Generic;
using UniLog;
namespace Apian
{

    public class SinglePeerGroupManager : IApianGroupManager
    {
       // ReSharper disable MemberCanBePrivate.Global,UnusedMember.Global,FieldCanBeMadeReadOnly.Global

        private ApianBase ApianInst {get; }
        public string MainP2pChannel {get => ApianInst.GameNet.CurrentGameId();}
        public UniLogger Logger;
        private readonly Dictionary<string, Action<ApianGroupMessage, string, string>> GroupMsgHandlers;
        private const string SinglePeerGroupType = "SinglePeerGroup";

        public ApianGroupInfo GroupInfo {get; private set;}
        // IApianGroupManager
        public string GroupType {get => SinglePeerGroupType;}
        public string GroupId {get => GroupInfo.GroupId;}
        public string GroupCreatorId {get => GroupInfo.GroupCreatorId;}
        public string LocalPeerId {get => ApianInst.GameNet.LocalP2pId();}
        public Dictionary<string, ApianGroupMember> Members {get;}

        public SinglePeerGroupManager(ApianBase apianInst)
        {
            GroupMsgHandlers = new Dictionary<string, Action<ApianGroupMessage, string, string>>() {
                //{ApianGroupMessage.GroupAnnounce, OnGroupAnnounce },
                //{ApianGroupMessage.GroupsRequest, OnGroupsRequest },
                //{ApianGroupMessage.GroupMemberStatus, OnGroupMemberStatus },
                {ApianGroupMessage.GroupMemberJoined, OnGroupMemberJoined },
            };

            Logger = UniLogger.GetLogger("ApianGroup");
            ApianInst = apianInst;
            Members = new Dictionary<string, ApianGroupMember>();
         }

        public void CreateGroup(string groupId, string groupName)
        {
            GroupInfo = new ApianGroupInfo(SinglePeerGroupType, groupId, LocalPeerId, groupName);
            ApianInst.GameNet.AddApianInstance(ApianInst, groupId);
        }

        public void CreateGroup(ApianGroupInfo info) => throw new Exception("GroupInfo-based creation not supported");

        public void JoinGroup(string groupId, string localMemberJson)
        {
            ApianInst.GameNet.AddApianInstance(ApianInst, groupId);
            ApianGroupMember LocalMember =  new ApianGroupMember(LocalPeerId);
            LocalMember.CurStatus = ApianGroupMember.Status.Active;
            Members[LocalPeerId] = LocalMember;
            ApianInst.GameNet.AddChannel(GroupId);

            ApianInst.GameNet.SendApianMessage(GroupId,
                new GroupMemberJoinedMsg(groupId, LocalPeerId, localMemberJson));

        }

        public void Update()
        {

        }

        public void OnApianMessage(ApianMessage msg, string msgSrc, string msgChannel)
        {
            if (msg != null && msg.MsgType == ApianMessage.GroupMessage)
            {
                ApianGroupMessage gMsg = msg as ApianGroupMessage;
                try {
                    GroupMsgHandlers[gMsg.GroupMsgType](gMsg, msgSrc, msgChannel);
                } catch (KeyNotFoundException){ }
            }
            else
                Logger.Warn($"OnApianMessage(): unexpected APianMsg Type: {msg?.MsgType}");
        }

        public void OnApianRequest(ApianRequest msg, string msgSrc, string msgChan)
        {
            ApianInst.GameNet.SendApianMessage(msgChan, msg.ToCommand());
        }

        public void OnApianObservation(ApianObservation msg, string msgSrc, string msgChan)
        {
            ApianInst.GameNet.SendApianMessage(msgChan, msg.ToCommand());
        }

        public bool ValidateCommand(ApianCommand msg, string msgSrc, string msgChan)
        {
            return true; // TODO: ok, even this one should at least check the source
        }


        private void OnGroupMemberJoined(ApianGroupMessage msg, string msgSrc, string msgChannel)
        {
            // No need to validate source, since it;s local
            GroupMemberJoinedMsg joinedMsg = (msg as GroupMemberJoinedMsg);
            ApianInst.OnGroupMemberJoined(joinedMsg.ApianClientPeerJson);
        }


       public ApianMessage DeserializeGroupMessage(string subType, string json)
        {
            return null;
        }

    }
}