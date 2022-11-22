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
        public string GroupCreatorAddr;
        public string GroupName; // TODO: Note that this is not just GroupChannelInfo?.id - decide what it should be and replace this with the explanation
        public Dictionary<string, string> GroupParams;

        public ApianGroupInfo(string groupType, P2pNetChannelInfo groupChannel, string creatorAddr, string groupName, Dictionary<string, string> grpParams = null)
        {
            GroupType = groupType;
            GroupChannelInfo = groupChannel;
            GroupCreatorAddr = creatorAddr;
            GroupName = groupName;
            GroupParams = grpParams ?? new Dictionary<string, string>();
        }

        public ApianGroupInfo(ApianGroupInfo agi)
        {
            // This ctor makes it easier for applications to subclass AGI and add app-specific GroupParams
            // and top-level properties to expose them
            GroupType = agi.GroupType;
            GroupChannelInfo = agi.GroupChannelInfo;
            GroupCreatorAddr = agi.GroupCreatorAddr;
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
                && GroupCreatorAddr.Equals(agi2.GroupCreatorAddr, System.StringComparison.Ordinal)
                && GroupName.Equals(agi2.GroupName, System.StringComparison.Ordinal)
                && GroupParams.Equals(agi2.GroupParams);
        }
    }

    public class AppCorePauseInfo
    {
        public string PauseId { get; private set; }
        public string Reason { get; private set; }
        public long ApianTime { get; private set; }

        public AppCorePauseInfo(string pauseId, string reason, long apianTime)
        {
            PauseId = pauseId;
            Reason = reason;
            ApianTime = apianTime;
         }
    }

    public class ApianGroupStatus
    {
        // Created via Apian.CurrentGroupStatus()
        public int ActiveMemberCount { get; set;  }

        public bool AppCorePaused; // see groupmgr for active pauseInfos

        public Dictionary<string, string> OtherStatus;

        public ApianGroupStatus(int mCnt, bool corePaused, Dictionary<string, string> otherStatus = null)
        {
            ActiveMemberCount = mCnt;
            AppCorePaused = corePaused;
            OtherStatus = otherStatus ?? new Dictionary<string, string>();
        }

        public ApianGroupStatus(ApianGroupStatus ags)
        {
            // Subclasses can use this as : base(apianStatus)
            ActiveMemberCount = ags.ActiveMemberCount;
            AppCorePaused = ags.AppCorePaused;
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

        public string PeerAddr {get;}
        public Status CurStatus {get; set;}
        public string CurStatusName {get => StatusName[(int)CurStatus];}
        public bool ApianClockSynced; // means we've gotten an ApianOffset msg
        public string AppDataJson; // This is ApianClient-relevant data. Apian doesn't read it

        public bool IsActive => (CurStatus == Status.Active ); // TODO: in the future this will include both ActivePlayer and ActiveValidator

        public ApianGroupMember(string peerAddr, string appDataJson)
        {
            CurStatus = Status.New;
            PeerAddr = peerAddr;
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
        string GroupCreatorAddr {get;}
        string LocalPeerAddr {get;}
        ApianGroupMember LocalMember {get;}
        ApianGroupMember GetMember(string peerAddr); // returns null if not there
        Dictionary<string, ApianGroupMember> Members {get;}
        int MemberCount { get; }
        int ActiveMemberCount { get; }
        bool AppCorePaused {get; }


        void SetupNewGroup(ApianGroupInfo info); // does NOT imply join
        void SetupExistingGroup(ApianGroupInfo info);
        void JoinGroup(string localMemberJson);
        void Update();
        void ApplyGroupCoreCommand(long epoch, long seqNum, GroupCoreMessage cmd);
        void SendApianRequest( ApianCoreMessage coreMsg );
        void SendApianObservation( ApianCoreMessage coreMsg );
        void OnApianClockOffset(string peerAddr, long ApianClockOffset);
        void OnApianGroupMessage(ApianGroupMessage msg, string msgSrc, string msgChan);
        void OnApianRequest(ApianRequest msg, string msgSrc, string msgChan);
        void OnApianObservation(ApianObservation msg, string msgSrc, string msgChan);
        void OnLocalStateCheckpoint(long seqNum, long timeStamp, string stateHash, string serializedState);
        void OnMemberLeftGroupChannel(string peerAddr); // Called by local P2pNet when the member leaves ot times out.
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
        public string GroupCreatorAddr {get => GroupInfo.GroupCreatorAddr;}
        public string LocalPeerAddr {get => ApianInst.GameNet.LocalPeerAddr();}
        public ApianGroupMember LocalMember {protected set; get;}
        public int MemberCount {get => Members.Count; }
        public int ActiveMemberCount {get => Members.Values.Where(m => m.CurStatus == ApianGroupMember.Status.Active).Count(); }
        public bool AppCorePaused {get => ActiveAppCorePauses.Values.Count() > 0; }

        public string MainP2pChannel {get => ApianInst.GameNet.CurrentNetworkId();}
        protected Dictionary<string, Action<ApianGroupMessage, string, string>> GroupMsgHandlers;
        protected Dictionary<string, Action<long, long, GroupCoreMessage>> GroupCoreCmdHandlers;


        public UniLogger Logger;

        protected ApianBase ApianInst {get; }
        public Dictionary<string, ApianGroupMember> Members {get;}
        public Dictionary<string, AppCorePauseInfo> ActiveAppCorePauses {get; }

        protected GroupCoreMessageDeserializer groupMgrMsgDeser;

        protected ApianGroupManagerBase(ApianBase apianInst)
        {
            Logger = UniLogger.GetLogger("ApianGroup");
            ApianInst = apianInst;
            Members = new Dictionary<string, ApianGroupMember>();
            ActiveAppCorePauses = new Dictionary<string,AppCorePauseInfo>();

            GroupCoreCmdHandlers = new Dictionary<string, Action< long, long, GroupCoreMessage>> {
                {GroupCoreMessage.PauseAppCore , OnPauseAppCoreCmd },
                {GroupCoreMessage.ResumeAppCore , OnResumeAppCoreCmd },
            };

            // This might have to be overridden by any subclass ctor (this will execute before the subclass ctor)
            groupMgrMsgDeser = new GroupCoreMessageDeserializer();  // default GroupCoreMessages
        }

        public ApianGroupMember GetMember(string peerAddr)
        {
            if (!Members.ContainsKey(peerAddr))
                return null;
            return Members[peerAddr];
        }

        protected ApianGroupMember _AddMember(string peerAddr, string appMemberDataJson )
        {
            // Calls ApianInstance CreateGroupMember() to allow it to create an app-specific derived class
            Logger.Info($"{this.GetType().Name}._AddMember(): ({(peerAddr==LocalPeerAddr?"Local":"Remote")}) {SID(peerAddr)}");
            ApianGroupMember newMember =  ApianInst.CreateGroupMember(peerAddr, appMemberDataJson);
            newMember.CurStatus = ApianGroupMember.Status.Joining;
            Members[peerAddr] = newMember;
            if (peerAddr==LocalPeerAddr)
                LocalMember = newMember;
            return newMember;
        }

    // TODO: There may be good default implmentations for some of these methods
        // that ought to just live here

        public abstract void SetupNewGroup(ApianGroupInfo info); // does NOT imply join
        public abstract void SetupExistingGroup(ApianGroupInfo info);
        public abstract void JoinGroup(string localMemberJson); // GroupManager doesn;t have a LeaveGroup() for the local peer
        public abstract void Update();
       public abstract void SendApianRequest( ApianCoreMessage coreMsg );
        public abstract void SendApianObservation( ApianCoreMessage coreMsg );


        public virtual void ApplyGroupCoreCommand(long epoch, long seqNum, GroupCoreMessage cmd)
        {
            try {
                GroupCoreCmdHandlers[cmd.MsgType](epoch, seqNum, cmd);
            } catch (NullReferenceException ex) {
                Logger.Error($"ApplyGroupCoreCommand(): No command handler for: '{cmd.MsgType}'");
                throw(ex);
            }
        }

        // Pause/Resume App Core

        protected void OnPauseAppCoreCmd(long epoch, long seqNum, GroupCoreMessage msg)
        {
            PauseAppCoreMsg pMsg = msg as PauseAppCoreMsg;
            Logger.Info($"OnPauseAppCoreCmd(): Pausing AppCore. ID: {pMsg.instanceId}, Reason: {pMsg.reason}");

            if (ActiveAppCorePauses.Keys.Contains(pMsg.instanceId)) {
                Logger.Warn($"OnPauseAppCoreCmd():AppCorePauseRequest ID: {pMsg.instanceId} is already in effect. Ignoring.");
            } else {

                bool wasPaused = AppCorePaused;
                AppCorePauseInfo pInfo = new AppCorePauseInfo(pMsg.instanceId, pMsg.reason, pMsg.TimeStamp);
                ActiveAppCorePauses[pMsg.instanceId] = pInfo;
                if (!wasPaused)
                    ApianInst.OnAppCorePaused(pInfo);

            }
        }

        protected void OnResumeAppCoreCmd(long epoch, long seqNum, GroupCoreMessage msg)
        {
            ResumeAppCoreMsg rMsg = msg as ResumeAppCoreMsg;
            Logger.Info($"OnResumeAppCoreCmd(): Resuming Pause ID: {rMsg.instanceId}");

            if (!ActiveAppCorePauses.Keys.Contains(rMsg.instanceId)) {
                Logger.Warn($"OnResumeAppCoreCmd(): AppCorePause ID: {rMsg.instanceId} is NOT in effect. Ignoring.");
            } else {
                AppCorePauseInfo pInfo = ActiveAppCorePauses[rMsg.instanceId];
                ActiveAppCorePauses.Remove(rMsg.instanceId);

                if (!AppCorePaused)
                    ApianInst.OnAppCoreResumed(pInfo);

            }
        }

        public virtual void OnApianClockOffset(string peerAddr, long ApianClockOffset)
        {
            // This should generally be overridden. If the peer involved was in SyncingClock status
            // then it should be made active.
            ApianGroupMember peer = GetMember(peerAddr);
            if (peer != null) peer.ApianClockSynced = true;
        }

        public virtual void OnMemberLeftGroupChannel(string peerAddr)
        {
            // This is called when P2pNet sees the peer as gone, and is always local in origin.

            // If anything local needs to be done (say, if it's a leader-based group and the leader has disappeared)
            // it needs to be handled in a type-specific subclass override

            // Set the member locally as Gone, and call the local handlers to inform the app. No messages are sent out,
            // this part is all local.
            OnApianGroupMessage(new GroupMemberStatusMsg(GroupId, peerAddr, ApianGroupMember.Status.Gone), LocalPeerAddr, GroupId);

            // Tell everyone we lost the peer - GroupManager implmentation will eventually field the messa and decide what to do.
             ApianInst.SendApianMessage(GroupId, new  GroupMemberLeftMsg (GroupId, peerAddr));
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