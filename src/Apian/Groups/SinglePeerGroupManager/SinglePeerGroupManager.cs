using System.Net.Http;
using System;
using System.Collections.Generic;
using P2pNet;
using UniLog;
namespace Apian
{

    public class SinglePeerGroupManager : ApianGroupManagerBase
    {
        // I hate that c# doesn;t allow you to require (either using an interface or an abstract base) that a class
        // implement a static member/method/property.
        public const string kGroupType = "SinglePeer";
        public const string kGroupTypeName = "SinglePeer";

        public override string GroupType {get => kGroupType; }
        public override string GroupTypeName {get => kGroupTypeName; }

       // ReSharper disable MemberCanBePrivate.Global,UnusedMember.Global,FieldCanBeMadeReadOnly.Global

        public string MainP2pChannel {get => ApianInst.GameNet.CurrentNetworkId();}
        private readonly Dictionary<string, Action<ApianGroupMessage, string, string>> GroupMsgHandlers;
        private ApianGroupMember _Member {get; set;}

        // IApianGroupManager
        public bool Intialized {get => GroupInfo != null; }
        private long CurEpochNum;
        private long NextNewCommandSeqNum;
        public long GetNewCommandSequenceNumber() => NextNewCommandSeqNum++;
        public void IncrementEpoch() { NextNewCommandSeqNum = 0;  CurEpochNum++;}

        public SinglePeerGroupManager(ApianBase apianInst) : base(apianInst)
        {
            GroupMsgHandlers = new Dictionary<string, Action<ApianGroupMessage, string, string>>() {
                //{ApianGroupMessage.GroupAnnounce, OnGroupAnnounce },
                //{ApianGroupMessage.GroupsRequest, OnGroupsRequest },
                //{ApianGroupMessage.GroupMemberStatus, OnGroupMemberStatus },
                {ApianGroupMessage.GroupMemberJoined, OnGroupMemberJoined },
            };
         }


        public override void SetupNewGroup(ApianGroupInfo info)
        {
            Logger.Info($"{this.GetType().Name}.SetupNewGroup(): {info.GroupName}");

            if (!info.GroupType.Equals(GroupType))
                Logger.Error($"SetupNewGroup(): incorrect GroupType: {info.GroupType} in info. Should be: GroupType");

            GroupInfo = info;
            ApianInst.ApianClock.Set(0); // Need to start it running - we're the only peer
        }

        public override void SetupExistingGroup(ApianGroupInfo info) => throw new Exception("GroupInfo-based creation not supported");

        public override void JoinGroup(string localMemberJson)
        {
            // Note that we aren't sending a request here - just a "Joined" - 'cause there's just this peer
            ApianInst.GameNet.SendApianMessage(GroupId,
                new GroupMemberJoinedMsg(GroupId, LocalPeerId, localMemberJson));
        }

        public override void LeaveGroup()
        {
           ApianInst.OnGroupMemberStatusChange(LocalMember, ApianGroupMember.Status.Removed);
        }

        public override void Update()
        {

        }
        public override void SendApianRequest( ApianCoreMessage coreMsg )
        {
            ApianInst.GameNet.SendApianMessage(GroupId, new ApianRequest(GroupId, coreMsg));
        }
        public override void SendApianObservation( ApianCoreMessage coreMsg )
        {
            ApianInst.GameNet.SendApianMessage(GroupId, new ApianObservation(GroupId, coreMsg));
        }

        public override void OnApianGroupMessage(ApianGroupMessage msg, string msgSrc, string msgChannel)
        {
            // Note that Apian only routes GROUP messages here.
            if (msg != null )
            {
                GroupMsgHandlers[msg.GroupMsgType](msg, msgSrc, msgChannel);
            }
        }

        public override void OnApianRequest(ApianRequest msg, string msgSrc, string msgChan)
        {
            ApianInst.GameNet.SendApianMessage(msgChan, new ApianCommand(CurEpochNum, GetNewCommandSequenceNumber(), msg));
        }

        public override void OnApianObservation(ApianObservation msg, string msgSrc, string msgChan)
        {
            ApianInst.GameNet.SendApianMessage(msgChan, new ApianCommand(CurEpochNum, GetNewCommandSequenceNumber(), msg));
        }

        public override ApianCommandStatus EvaluateCommand(ApianCommand msg, string msgSrc, long maxAppliedCmdNum)
        {
            return ApianCommandStatus.kShouldApply; // TODO: ok, even this one should at least check the source
        }


        protected void OnGroupMemberJoined(ApianGroupMessage msg, string msgSrc, string msgChannel)
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

        public override void OnLocalStateCheckpoint(long cndSeqNum, long timeStamp, string stateHash, string serializedState) {} // this GroupMgr doesn;t care

    }
}