using System.Collections.Generic;
using System.Linq;
using System;
using Newtonsoft.Json;
using P2pNet;
using UniLog;
using static UniLog.UniLogger; // for SID

namespace Apian
{
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
        public Status CurStatus {get; set;}  // FIXME: make set private and add setter method
        public bool IsValidator {get; private set;}
        public bool ApianClockSynced; // means we've gotten an ApianOffset msg
        public string AppDataJson; // This is ApianClient-relevant data. Apian doesn't read it

        public string CurStatusName {get => StatusName[(int)CurStatus];}
        public bool IsActive => (CurStatus == Status.Active ); // TODO: in the future this will include both ActivePlayer and ActiveValidator

        public ApianGroupMember(string peerAddr, string appDataJson, bool isValidator)
        {
            CurStatus = Status.New;
            PeerAddr = peerAddr;
            AppDataJson = appDataJson;
            IsValidator = isValidator;
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
        int ActiveValidatorCount { get; }
        int ActivePlayerCount { get; }  // TODO: "player" is wrong word, but "participant" is too big... so "player"
        bool AppCorePaused {get; }

        bool LocalPeerShouldPostEpochReports();
        bool LocalPeerShouldRegisterSession();

        void SetupNewGroup(ApianGroupInfo info); // does NOT imply join
        void SetupExistingGroup(ApianGroupInfo info);
        void JoinGroup(string localMemberJson, bool asValidator);
        void Update();
        void ApplyGroupCoreCommand(long epoch, long seqNum, GroupCoreMessage cmd);
        void SendApianRequest( ApianCoreMessage coreMsg );
        void SendApianObservation( ApianCoreMessage coreMsg );
        void OnApianClockOffset(string peerAddr, long ApianClockOffset);
        void OnApianGroupMessage(ApianGroupMessage msg, string msgSrc, string msgChan);
        void OnApianRequest(ApianRequest msg, string msgSrc, string msgChan);
        void OnApianObservation(ApianObservation msg, string msgSrc, string msgChan);
        void OnMemberLeftGroupChannel(string peerAddr); // Called by local P2pNet when the member leaves ot times out.
        void OnNewEpoch();
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
        public int ActiveValidatorCount {get => Members.Values.Where(m => m.CurStatus == ApianGroupMember.Status.Active && m.IsValidator).Count(); }
        public int ActivePlayerCount {get => Members.Values.Where(m => m.CurStatus == ApianGroupMember.Status.Active && !m.IsValidator).Count(); }

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

        protected ApianGroupMember _AddMember(string peerAddr, string appMemberDataJson, bool asValidator )
        {
            // Calls ApianInstance CreateGroupMember() to allow it to create an app-specific derived class
            Logger.Info($"{this.GetType().Name}._AddMember(): ({(peerAddr==LocalPeerAddr?"Local":"Remote")}) {SID(peerAddr)}");
            ApianGroupMember newMember =  ApianInst.CreateGroupMember(peerAddr, appMemberDataJson, asValidator);
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
        public abstract void JoinGroup(string localMemberJson, bool asValidator); // GroupManager doesn;t have a LeaveGroup() for the local peer
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

        // Related to Epoch/Checkpoint/StateHash

        public virtual void OnNewEpoch() {}

        public virtual bool LocalPeerShouldRegisterSession()
        {
            // If it needs reporting it's always the creator
            if ( !string.IsNullOrEmpty(GroupInfo.AnchorAddr) && (LocalPeerAddr == GroupCreatorAddr) && (GroupInfo.AnchorPostAlg != ApianGroupInfo.AnchorPostsNone) )
            {
                Logger.Info($"LocalPeerShouldRegisterSession(): Local peer is Creator");
                return true;
            }
            return false;
        }

        public virtual bool LocalPeerShouldPostEpochReports()
        {
            // For the base GroupManager, the only valid report algoritm is "CreatorPosts"
            // subclasses should override this if they support others (call this to check CreatorPosts)
            if ( !string.IsNullOrEmpty(GroupInfo.AnchorAddr) && (LocalPeerAddr == GroupCreatorAddr) && (GroupInfo.AnchorPostAlg == ApianGroupInfo.AnchorPostsCreator))
            {
                Logger.Info($"LocalPeerShouldPostEpochReports(): Algo is 'CreatorPosts' and local peer is Creator");
                return true;
            }
            return false;
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
        public abstract ApianCommandStatus EvaluateCommand(ApianCommand msg, string msgSrc, long maxAppliedCmdNum);
        public virtual ApianMessage DeserializeCustomApianMessage(string msgTYpe, string msgJSON) => null; // by default don't deserialize any messages
        public virtual ApianCoreMessage DeserializeGroupMessage(ApianWrappedMessage aMsg)
        {
            return groupMgrMsgDeser.FromJSON(aMsg.PayloadMsgType, aMsg.SerializedPayload);
        }
    }

}