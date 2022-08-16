using System.Collections.Generic;
using System.Linq;
using System;
using Newtonsoft.Json;
using P2pNet;
using UniLog;
using static UniLog.UniLogger; // for SID

namespace Apian
{
    public class ApianGroupInfo
    {
        public string GroupType;
        public P2pNetChannelInfo GroupChannelInfo;
        public string GroupId { get => GroupChannelInfo?.id;} // channel
        public string GroupCreatorId;
        public string GroupName; // TODO: Note that this is not just GroupChannelInfo?.id - decide what it should be and replace this with the explanation
        public Dictionary<string, string> GroupParams;

        public ApianGroupInfo(string groupType, P2pNetChannelInfo groupChannel, string creatorId, string groupName, Dictionary<string, string> grpParams = null)
        {
            GroupType = groupType;
            GroupChannelInfo = groupChannel;
            GroupCreatorId = creatorId;
            GroupName = groupName;
            GroupParams = grpParams ?? new Dictionary<string, string>();
        }

        public ApianGroupInfo(ApianGroupInfo agi)
        {
            // This ctor makes it easier for applications to subclass AGI and add app-specific GroupParams
            // and top-level properties to expose them
            GroupType = agi.GroupType;
            GroupChannelInfo = agi.GroupChannelInfo;
            GroupCreatorId = agi.GroupCreatorId;
            GroupName = agi.GroupName;
            GroupParams = agi.GroupParams;
        }

        public ApianGroupInfo() {} // required by Newtonsoft JSON stuff

        public string Serialized() =>  JsonConvert.SerializeObject(this);
        public static ApianGroupInfo Deserialize(string jsonString) => JsonConvert.DeserializeObject<ApianGroupInfo>(jsonString);
        public bool IsEquivalentTo(ApianGroupInfo agi2)
        {
            return GroupType.Equals(agi2.GroupType, System.StringComparison.Ordinal)
                && GroupChannelInfo.IsEquivalentTo(agi2.GroupChannelInfo)
                && GroupCreatorId.Equals(agi2.GroupCreatorId, System.StringComparison.Ordinal)
                && GroupName.Equals(agi2.GroupName, System.StringComparison.Ordinal)
                && GroupParams.Equals(agi2.GroupParams);
        }
    }

    public class ApianGroupStatus
    {
        // Created via Apian.CurrentGroupStatus()
        public int ActiveMemberCount { get; set;  }

        public Dictionary<string, string> OtherStatus;

        public ApianGroupStatus(int mCnt, Dictionary<string, string> otherStatus = null)
        {
            ActiveMemberCount = mCnt;
            OtherStatus = otherStatus ?? new Dictionary<string, string>();
        }

        public ApianGroupStatus(ApianGroupStatus ags)
        {
            // Subclasses can use this as : base(apianStatus)
            ActiveMemberCount = ags.ActiveMemberCount;
            OtherStatus = ags.OtherStatus;
        }

        public ApianGroupStatus() {} // required by Newtonsoft JSON stuff

        public string Serialized() =>  JsonConvert.SerializeObject(this);
        public static ApianGroupStatus Deserialize(string jsonString) => JsonConvert.DeserializeObject<ApianGroupStatus>(jsonString);
    }

    public class GroupAnnounceResult
    {
        public ApianGroupInfo GroupInfo { get; private set; }
        public ApianGroupStatus GroupStatus { get; private set; }
        public GroupAnnounceResult(ApianGroupInfo info, ApianGroupStatus status) { GroupInfo = info; GroupStatus = status; }
    }

    public class ApianGroupMember
    {
        // ReSharper disable MemberCanBePrivate.Global,UnusedMember.Global,UnusedAutoPropertyAccessor.Global,NotAccessedField.Global
        public enum Status
        {
            New = 0,  // just created
            Joining, // In the process of joining a group (mostly waiting for ApianClock to sync)
            SyncingState, // In the process of syncing app state
            SyncingClock, // In the process of syncing ApianClock (may have happened earlier)
            Active, // part of the gang
            LeaveRequested, // has asked to leave and we've heard about it.
            Gone,   // The idea with "Gone" is that there will be times when a peer has said "I'm gone" over the network
                    // but there still may be Apian cleanup to do, we we might want to the other group members to
                    // keep the data around for a bit until the GouprManager code decieds that it's really gone and to ditribute
                    // a PeerLeftGroup message and everyone can delete it.
        }

        public static string[] StatusName =  { "New", "Joining", "SyncingState", "SyncingClock", "Active", "Removed" };

        public string PeerId {get;}
        public Status CurStatus {get; set;}
        public string CurStatusName {get => StatusName[(int)CurStatus];}
        public bool ApianClockSynced; // means we've gotten an ApianOffset msg
        public string AppDataJson; // This is ApianClient-relevant data. Apian doesn't read it

        public bool IsActive => (CurStatus == Status.Active ); // TODO: in the future this will include both ActivePlayer and ActiveValidator

        public ApianGroupMember(string peerId, string appDataJson)
        {
            CurStatus = Status.New;
            PeerId = peerId;
            AppDataJson = appDataJson;

        }
        // ReSharper enable MemberCanBePrivate.Global,UnusedMember.Global,UnusedAutoPropertyAccessor.Global,NotAccessedField.Global
    }

    public enum ApianCommandStatus {
        kUnknown, // not evaluated
        kLocalPeerNotReady, // local peer hasn;t been accepted into group yet
        kAlreadyReceived, // seqence nuber < that what we were expecting
        kBadSource, // Ignore it and complain about the source
        kStashedInQueue, // Higher seq# than we can apply. Cache it and, if not already, ask to be sync'ed up
        kShouldApply, // It's good. Apply it to the state
        kStashedInQueued, // means that seq# was higher than we can apply. GroupMgr will ask for missing ones
    }

    public interface IApianGroupManager
    {
        // I hate that C# has no way to force derived classes to implement
        // uniformly-named static members or methods.
        // Make sure to add these 2 properties to any derived classes:
        //   public static string GroupType = "typeIdStr";
        //   public static string GroupTypeName = "Friendly Name";


        // ReSharper disable MemberCanBePrivate.Global,UnusedMember.Global,UnusedMemberInSuper.Global        ApianGroupInfo GroupInfo {get;}
        ApianGroupInfo GroupInfo {get; }
        string GroupId {get; }
        string GroupName {get; }
        string GroupType {get;}
        string GroupTypeName {get;}
        string GroupCreatorId {get;}
        string LocalPeerId {get;}
        ApianGroupMember LocalMember {get;}
        ApianGroupMember GetMember(string peerId); // returns null if not there
        Dictionary<string, ApianGroupMember> Members {get;}
        int MemberCount { get; }
        int ActiveMemberCount { get; }

        void SetupNewGroup(ApianGroupInfo info); // does NOT imply join
        void SetupExistingGroup(ApianGroupInfo info);
        void JoinGroup(string localMemberJson);
        void Update();
        void ApplyGroupCoreCommand(long epoch, long seqNum, GroupCoreMessage cmd);
        void SendApianRequest( ApianCoreMessage coreMsg );
        void SendApianObservation( ApianCoreMessage coreMsg );
        void OnApianClockOffset(string peerId, long ApianClockOffset);
        void OnApianGroupMessage(ApianGroupMessage msg, string msgSrc, string msgChan);
        void OnApianRequest(ApianRequest msg, string msgSrc, string msgChan);
        void OnApianObservation(ApianObservation msg, string msgSrc, string msgChan);
        void OnLocalStateCheckpoint(long seqNum, long timeStamp, string stateHash, string serializedState);
        void OnMemberLeftGroupChannel(string peerId); // Called by local P2pNet when the member leaves ot times out.
        ApianCommandStatus EvaluateCommand(ApianCommand msg, string msgSrc, long maxAppliedCmdNum);
        ApianMessage DeserializeCustomApianMessage(string apianMsgTYpe,  string msgJSON); // pass the generically-deserialized msg
        ApianCoreMessage DeserializeGroupMessage(ApianWrappedMessage aMsg);

        // ReSharper enable MemberCanBePrivate.Global,UnusedMember.Global,UnusedMemberInSuper.Global
    }

    public abstract class ApianGroupManagerBase : IApianGroupManager
    {
        public virtual string GroupType {get;}
        public virtual string GroupTypeName {get;}

        public ApianGroupInfo GroupInfo {get; protected set;}
        public string GroupId {get => GroupInfo.GroupId;}
        public string GroupName {get => GroupInfo.GroupName;}
        public string GroupCreatorId {get => GroupInfo.GroupCreatorId;}
        public string LocalPeerId {get => ApianInst.GameNet.LocalP2pId();}
        public ApianGroupMember LocalMember {protected set; get;}
        public int MemberCount {get => Members.Count; }
        public int ActiveMemberCount {get => Members.Values.Where(m => m.CurStatus == ApianGroupMember.Status.Active).Count(); }

        public string MainP2pChannel {get => ApianInst.GameNet.CurrentNetworkId();}
        protected Dictionary<string, Action<ApianGroupMessage, string, string>> GroupMsgHandlers;
        protected Dictionary<string, Action<long, long, GroupCoreMessage>> GroupCoreCmdHandlers;


        public UniLogger Logger;

        protected ApianBase ApianInst {get; }
        public Dictionary<string, ApianGroupMember> Members {get;}

        protected GroupCoreMessageDeserializer groupMgrMsgDeser;

        protected ApianGroupManagerBase(ApianBase apianInst)
        {
            Logger = UniLogger.GetLogger("ApianGroup");
            ApianInst = apianInst;
            Members = new Dictionary<string, ApianGroupMember>();
        }

        public ApianGroupMember GetMember(string peerId)
        {
            if (!Members.ContainsKey(peerId))
                return null;
            return Members[peerId];
        }

        protected ApianGroupMember _AddMember(string peerId, string appMemberDataJson )
        {
            // Calls ApianInstance CreateGroupMember() to allow it to create an app-specific derived class
            Logger.Info($"{this.GetType().Name}._AddMember(): ({(peerId==LocalPeerId?"Local":"Remote")}) {SID(peerId)}");
            ApianGroupMember newMember =  ApianInst.CreateGroupMember(peerId, appMemberDataJson);
            newMember.CurStatus = ApianGroupMember.Status.Joining;
            Members[peerId] = newMember;
            if (peerId==LocalPeerId)
                LocalMember = newMember;
            return newMember;
        }


        // TODO: There may be good default implmentations for some of these methods
        // that ought to just live here

        public abstract void SetupNewGroup(ApianGroupInfo info); // does NOT imply join
        public abstract void SetupExistingGroup(ApianGroupInfo info);
        public abstract void JoinGroup(string localMemberJson); // GroupManager doesn;t have a LeaveGroup() for the local peer
        public abstract void Update();

        public virtual void ApplyGroupCoreCommand(long epoch, long seqNum, GroupCoreMessage cmd)
        {
            GroupCoreCmdHandlers[cmd.MsgType](epoch, seqNum, cmd);
        }
        public abstract void SendApianRequest( ApianCoreMessage coreMsg );
        public abstract void SendApianObservation( ApianCoreMessage coreMsg );

        public virtual void OnApianClockOffset(string peerId, long ApianClockOffset)
        {
            // This should generally be overridden. If the peer involved was in SyncingClock status
            // then it should be made active.
            ApianGroupMember peer = GetMember(peerId);
            if (peer != null) peer.ApianClockSynced = true;
        }

        public virtual void OnMemberLeftGroupChannel(string peerId)
        {
            // This is called when P2pNet sees the peer as gone, and is always local in origin.

            // If anything local needs to be done (say, if it's a leader-based group and the leader has disappeared)
            // it needs to be handled in a type-specific subclass override

            // Set the member locally as Gone, and call the local handlers to inform the app. No messages are sent out,
            // this part is all local.
            OnApianGroupMessage(new GroupMemberStatusMsg(GroupId, peerId, ApianGroupMember.Status.Gone), LocalPeerId, GroupId);

            // Tell everyone we lost the peer - GroupManager implmentation will eventually field the messa and decide what to do.
             ApianInst.SendApianMessage(GroupId, new  GroupMemberLeftMsg (GroupId, peerId));
        }

        public abstract void OnApianGroupMessage(ApianGroupMessage msg, string msgSrc, string msgChan);
        public abstract void OnApianRequest(ApianRequest msg, string msgSrc, string msgChan);
        public abstract void OnApianObservation(ApianObservation msg, string msgSrc, string msgChan);
        public abstract void OnLocalStateCheckpoint(long cmdSeqNum, long timeStamp, string stateHash, string serializedState);
        public abstract ApianCommandStatus EvaluateCommand(ApianCommand msg, string msgSrc, long maxAppliedCmdNum);
        public virtual ApianMessage DeserializeCustomApianMessage(string msgTYpe, string msgJSON) => null; // by default don't deserialize any messages
        public virtual ApianCoreMessage DeserializeGroupMessage(ApianWrappedMessage aMsg)
        {
            return groupMgrMsgDeser.FromJSON(aMsg.PayloadMsgType, aMsg.SerializedPayload);
        }
    }

}