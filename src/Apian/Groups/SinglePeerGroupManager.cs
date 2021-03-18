using System.Net.Http;
using System;
using System.Collections.Generic;
using P2pNet;
using UniLog;
namespace Apian
{

    public class SinglePeerGroupManager : ApianGroupManagerBase, IApianGroupManager
    {
       // ReSharper disable MemberCanBePrivate.Global,UnusedMember.Global,FieldCanBeMadeReadOnly.Global

        public string MainP2pChannel {get => ApianInst.GameNet.CurrentNetworkId();}
        private readonly Dictionary<string, Action<ApianGroupMessage, string, string>> GroupMsgHandlers;
        private ApianGroupMember _Member {get; set;}
        public const string groupType = "SinglePeerGroup";

        // IApianGroupManager
        public bool Intialized {get => GroupInfo != null; }
        public string GroupType {get => groupType;}
        public string GroupCreatorId {get => GroupInfo.GroupCreatorId;}
        public string LocalPeerId {get => ApianInst.GameNet.LocalP2pId();}
        public ApianGroupMember LocalMember {private set; get;}
        private long NextNewCommandSeqNum;
        public long GetNewCommandSequenceNumber() => NextNewCommandSeqNum++;

        public SinglePeerGroupManager(ApianBase apianInst) : base(apianInst)
        {
            GroupMsgHandlers = new Dictionary<string, Action<ApianGroupMessage, string, string>>() {
                //{ApianGroupMessage.GroupAnnounce, OnGroupAnnounce },
                //{ApianGroupMessage.GroupsRequest, OnGroupsRequest },
                //{ApianGroupMessage.GroupMemberStatus, OnGroupMemberStatus },
                {ApianGroupMessage.GroupMemberJoined, OnGroupMemberJoined },
            };
         }


        public void SetupNewGroup(ApianGroupInfo info)
        {
            Logger.Info($"{this.GetType().Name}.SetupNewGroup(): {info.GroupName}");

            if (!info.GroupType.Equals(GroupType))
                Logger.Error($"SetupNewGroup(): incorrect GroupType: {info.GroupType} in info. Should be: GroupType");

            GroupInfo = info;
            ApianInst.ApianClock.Set(0); // Need to start it running - we're the only peer
        }

        public void SetupExistingGroup(ApianGroupInfo info) => throw new Exception("GroupInfo-based creation not supported");

        public void JoinGroup(string localMemberJson)
        {
            // Note that we aren't sending a request here - just a "Joined" - 'cause there's just this peer
            ApianInst.GameNet.SendApianMessage(GroupId,
                new GroupMemberJoinedMsg(GroupId, LocalPeerId, localMemberJson));
        }

        public void LeaveGroup()
        {
           ApianInst.OnGroupMemberStatusChange(LocalMember, ApianGroupMember.Status.Removed);
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
            ApianInst.GameNet.SendApianMessage(msgChan, msg.ToCommand(GetNewCommandSequenceNumber()));
        }

        public void OnApianObservation(ApianObservation msg, string msgSrc, string msgChan)
        {
            ApianInst.GameNet.SendApianMessage(msgChan, msg.ToCommand(GetNewCommandSequenceNumber()));
        }

        public ApianCommandStatus EvaluateCommand(ApianCommand msg, string msgSrc, string msgChan)
        {
            return ApianCommandStatus.kShouldApply; // TODO: ok, even this one should at least check the source
        }


        private void OnGroupMemberJoined(ApianGroupMessage msg, string msgSrc, string msgChannel)
        {
            // No need to validate source, since it;s local
            GroupMemberJoinedMsg joinedMsg = (msg as GroupMemberJoinedMsg);
            _Member = new ApianGroupMember(joinedMsg.PeerId, joinedMsg.ApianClientPeerJson);
            if (joinedMsg.PeerId==LocalPeerId) // In this case, had BETTER be true;
                LocalMember = _Member;
            _Member.CurStatus = ApianGroupMember.Status.Joining;
            ApianInst.OnGroupMemberJoined(_Member);

            // Go ahead an mark/announce "active"
            _Member.CurStatus = ApianGroupMember.Status.Active;
            ApianInst.OnGroupMemberStatusChange(_Member, ApianGroupMember.Status.Joining);
        }

        public void OnLocalStateCheckpoint(long seqNum, long timeStamp, string stateHash, string serializedState) {} // this GroupMgr doesn;t care

    }
}