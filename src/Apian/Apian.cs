
using System;
using System.Collections.Generic;
using P2pNet;
using UniLog;

namespace Apian
{
    // ApianBase must (it IS abstract) be subclassed.
    // The expectation is that it will usuall be subclassed twice.
    // The first SubClass ( : ApianBase ) should provide all of the application-specific
    // behavior and APIs
    // The Second should be GroupManager-implmentation-dependant, and should create one
    // and assign ApianBase.GroupMgr. I should also override virtual methods to provide
    // any GroupManager-specific behavior.

    // TODO: ApianBase should check to make sure GroupMgr is not null.

    public interface IApianClientServices
    {
        // This is the interface that a client (almost certainly a GameNet) sees
        void SetupExistingGroup(ApianGroupInfo groupInfo);
        void SetupNewGroup(ApianGroupInfo groupInfo);
        void JoinGroup(string localGroupData);
        void LeaveGroup();
        void OnGroupMemberLeft(string groupChannelId, string p2pId);
        void OnPeerMissing(string groupChannelId, string p2pId);
        void OnPeerReturned(string groupChannelId, string p2pId);
        void OnP2pPeerSync(string peerId, long clockOffsetMs, long netLagMs);
        void OnApianMessage(string fromId, string toId, ApianMessage msg, long lagMs);
    }


    public interface IApianAppCoreServices
    {
        // This is the interface an AppCore sees
        void SendObservation(ApianObservation msg);
        void StartObservationSet();
        void EndObservationSet();
    }

    public abstract class ApianBase : IApianAppCoreServices, IApianClientServices
    {
		// public API
        protected Dictionary<string, Action<string, string, ApianMessage, long>> ApMsgHandlers;
        // Args are fromId, toId, ApianMsg, msDelay
        public UniLogger Logger;
        public IApianGroupManager GroupMgr  {get; protected set;}  // set in a sublcass ctor
        public IApianClock ApianClock {get; protected set;}
        public IApianGameNet GameNet {get; private set;}
        public IApianAppCore AppCore {get; private set;}

        public string GroupName { get => GroupMgr.GroupName; }
        public string NetworkId { get => GameNet.CurrentNetworkId(); }
        public string GroupId { get => GroupMgr.GroupId; }
        public string GroupType { get => GroupMgr.GroupType; }

        // Observation Sets allow observations that are noticed during a CoreState "loop" (frame)
        // To be batched-up and then ordered and checked for conflict before being sent out.
        protected List<ApianObservation> batchedObservations;

        // Command-related stuff
        // protected Dictionary<long, ApianCommand> PendingCommands; // Commands received but not yet applied to the state
        // protected long ExpectedCommandNum { get; set;} // starts at 0 - there should be no skipping unles a data checkpoint is loaded

        protected ApianBase(IApianGameNet gn, IApianAppCore cl) {
            GameNet = gn;
            AppCore = cl;
            AppCore.SetApianReference(this);
            Logger = UniLogger.GetLogger("Apian");
            ApMsgHandlers = new Dictionary<string, Action<string, string, ApianMessage, long>>();
            // Add any truly generic handlers here
        }

        public abstract bool Update(); // Returns TRUE is local peer is in active state

        // Apian Messages
        public abstract void OnApianMessage(string fromId, string toId, ApianMessage msg, long lagMs);

        public virtual void SendApianMessage(string toChannel, ApianMessage msg)
        {
            Logger.Verbose($"SendApianMsg() To: {toChannel} MsgType: {msg.MsgType} {((msg.MsgType==ApianMessage.GroupMessage)? "GrpMsgTYpe: "+(msg as ApianGroupMessage).GroupMsgType:"")}");
            GameNet.SendApianMessage(toChannel, msg);
        }

        // CoreApp -> Apian API
        // TODO: You know, these should be interfaces
        protected virtual void SendRequest(string destCh, ApianMessage msg)
        {
            // Make sure these only get sent out if we are ACTIVE.
            // It wouldn't cause any trouble, since the groupmgr would not make it into a command
            // after seeing we aren't active - but there's a lot of message traffic between the 2

            // Also - this func can be overridden in any derived Apian class which is able to
            // be even more selctive (in a server-based group, for instance, if you're not the
            // server then you should just return)
            if (GroupMgr.LocalMember?.CurStatus != ApianGroupMember.Status.Active)
            {
                Logger.Info($"SendRequest() - outgoing message not sent: We are not ACTIVE.");
                return;
            }
            GameNet.SendApianMessage(destCh, msg);
        }

        // IApianAppCore - only the AppCore calls these

        public virtual void SendObservation(ApianObservation msg) // alwas goes to current group
        {
            // See comments in SendRequest
            if (GroupMgr.LocalMember?.CurStatus != ApianGroupMember.Status.Active)
            {
                Logger.Info($"SendObservation() - outgoing message not sent: We are not ACTIVE.");
                return;
            }

            if (batchedObservations == null)
                GameNet.SendApianMessage(GroupMgr.GroupId, msg);
            else
                batchedObservations.Add( msg);
        }

        public virtual void StartObservationSet()
        {
            // Is recreating this list every frame wasteful?
            if (batchedObservations != null)
            {
                Logger.Warn($"ApianBase.StartObservationSet(): batchedObservations not null. Clearing it. EndObservationSet() not called?");
                batchedObservations.Clear();
            }
            batchedObservations = new List<ApianObservation>();
        }

        public virtual void EndObservationSet()
        {
            if (batchedObservations == null)
            {
                Logger.Warn($"ApianBase.EndObservationSet(): batchedObservations is unititialized. StartObservationSet() not called?");
                return;
            }

            // Sort by timestamp, earliest first
            batchedObservations.Sort( (a,b) => a.CoreMsg.TimeStamp.CompareTo(b.CoreMsg.TimeStamp));

            // TODO: run conflict resolution!!!
            List<ApianObservation> obsToSend = new List<ApianObservation>();
            foreach (ApianObservation obs in batchedObservations)
            {
                bool isValid = true; // TODO: should check against current CoreState
                string reason;
                foreach (ApianObservation prevObs in obsToSend)
                {
                    ApianConflictResult effect = ApianConflictResult.Unaffected;
                    (effect, reason) = AppCore.ValidateCoreMessages(prevObs.CoreMsg, obs.CoreMsg);
                    switch (effect)
                    {
                        case ApianConflictResult.Validated:
                            Logger.Info($"{obs.CoreMsg.MsgType} Observation Validated by {prevObs.CoreMsg.MsgType}: {reason}");
                            isValid = true;
                            break;
                        case ApianConflictResult.Invalidated:
                            Logger.Info($"{obs.CoreMsg.MsgType} Observation invalidated by {prevObs.CoreMsg.MsgType}: {reason}");
                            isValid = false;
                            break;
                        case ApianConflictResult.Unaffected:
                        default:
                            break;
                    }
                }
                // Still valid?
                if (isValid)
                    obsToSend.Add(obs);
                else
                    Logger.Info($"{obs.CoreMsg.MsgType} Observation rejected.");
            }

            //Logger.Warn($"vvvv - Start Obs batch send - vvvv");
            foreach (ApianObservation obs in obsToSend)
            {
                //Logger.Warn($"Type: {obs.ClientMsg.MsgType} TS: {obs.ClientMsg.TimeStamp}");
                GameNet.SendApianMessage(GroupMgr.GroupId, obs); // send the in acsending time order
            }
            //Logger.Warn($"^^^^ -  End Obs batch send  - ^^^^");

            batchedObservations.Clear();
            batchedObservations = null;

        }

        // Group-related

        public void SetupNewGroup(ApianGroupInfo info) => GroupMgr.SetupNewGroup(info);
        public void SetupExistingGroup(ApianGroupInfo info) => GroupMgr.SetupExistingGroup(info);
        public void JoinGroup(string localMemberJson) => GroupMgr.JoinGroup(localMemberJson);
        public void LeaveGroup() => GroupMgr.LeaveGroup();

         // TODO: ApplyCheckpointStateData() doesn't seem to belong here.
        public abstract void ApplyCheckpointStateData(long epoch, long seqNum, long timeStamp, string stateHash, string stateData);

        // FROM GroupManager
        public virtual void OnGroupMemberJoined(ApianGroupMember member)
        {
            // By default just helps getting ApianClock set up.
            // App-specific Apian instance needs to field this if it cares for any other reason.
            // Note that the local gameinstance usually doesn't care about a remote peer joining a group until a Player Joins the gameInst
            // But it usually DOES care about the LOCAL peer's group membership status.

            if (member.PeerId != GameNet.LocalP2pId() &&  ApianClock != null)
            {
                PeerClockSyncData syncData = GameNet.GetP2pPeerClockSyncData(member.PeerId);
                if (syncData == null)
                    Logger.Warn($"ApianBase.OnGroupMemberJoined(): peer {member.PeerId} has no P2pClockSync data");
                else
                {
                    ApianClock.OnP2pPeerSync(member.PeerId, syncData.clockOffsetMs, syncData.networkLagMs);
                    if (!ApianClock.IsIdle)
                        ApianClock.SendApianClockOffset();

                }
            }
        }

        public virtual void OnGroupMemberLeft(string groupId, string peerId)
        {
            if (ApianClock != null)
                ApianClock.OnPeerLeft(peerId);

            OnApianMessage( GameNet.LocalP2pId(), GroupId, new GroupMemberStatusMsg(GroupId, peerId, ApianGroupMember.Status.Removed), 0);
        }

        public virtual void OnGroupMemberStatusChange(ApianGroupMember member, ApianGroupMember.Status prevStatus)
        {
            // Note that the member status has already been changed when this is called
            Logger.Info($"OnGroupMemberStatusChange(): {UniLogger.SID(member.PeerId)} from {prevStatus} to {member.CurStatus}");
            GameNet.OnApianGroupMemberStatus( GroupId, member.PeerId, member.CurStatus, prevStatus);
        }

        public abstract void ApplyStashedApianCommand(ApianCommand cmd);

        // called by AppCore
        public abstract void SendCheckpointState(long timeStamp, long seqNum, string serializedState); // called by client app



        // Other stuff
        public void OnP2pPeerSync(string remotePeerId, long clockOffsetMs, long netLagMs) // sys + offset = apian
        {
            // TODO: This is awkward.
            ApianClock?.OnP2pPeerSync( remotePeerId,  clockOffsetMs,  netLagMs);
        }

        // "Missing" is a little tricky. It's not like Joining or Leaving a group - it's more of a network-level
        // notification that a peer is late being heard from - but not so badly that it's being dropped. In many
        // applications this can be dealt with by pausing or temporarily disabling stuff. It's VERY app-dependant
        // but when used well can make an otherwise unusable app useful for someone with a droppy connection (or a
        // computer prone to "pausing" for a couple seconds at a time - I've seen them both)
        public virtual void OnPeerMissing(string channelId, string p2pId)
        {
        }

        public virtual void OnPeerReturned(string channelId, string p2pId)
        {
        }
    }


}