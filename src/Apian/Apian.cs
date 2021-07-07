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
        void OnPeerClockSync(string peerId, long clockOffsetMs, long netLagMs);
        void OnApianMessage(string fromId, string toId, ApianMessage msg, long lagMs);
    }


    public interface IApianAppCoreServices
    {
        // This is the interface an AppCore sees
        void SendObservation(ApianCoreMessage msg);
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
        protected List<ApianCoreMessage> batchedObservations;

        // Command-related stuff
        public Dictionary<long, ApianCommand> AppliedCommands; // All commands we have applied // TODO: write out/prune periodically?
        public  long MaxAppliedCmdSeqNum {get; private set;} // largest seqNum we have applied, inits to -1
        public  long MaxReceivedCmdSeqNum {get; private set;} // largest seqNum we have *received*, inits to -1

        protected ApianBase(IApianGameNet gn, IApianAppCore cl) {
            GameNet = gn;
            AppCore = cl;
            AppCore.SetApianReference(this);
            Logger = UniLogger.GetLogger("Apian");

            ApMsgHandlers = new Dictionary<string, Action<string, string, ApianMessage, long>>();
            // Add any truly generic handlers here
            // params are:  from, to, apMsg, msSinceSent
            ApMsgHandlers[ApianMessage.CliRequest] = (f,t,m,d) => this.OnApianRequest(f,t,m,d);
            ApMsgHandlers[ApianMessage.CliObservation] = (f,t,m,d) => this.OnApianObservation(f,t,m,d);
            ApMsgHandlers[ApianMessage.CliCommand] = (f,t,m,d) => this.OnApianCommand(f,t,m,d);
            ApMsgHandlers[ApianMessage.GroupMessage] = (f,t,m,d) => this.OnApianGroupMessage(f,t,m,d);
            ApMsgHandlers[ApianMessage.ApianClockOffset] = (f,t,m,d) => this.OnApianClockOffsetMsg(f,t,m,d);

            AppliedCommands = new Dictionary<long, ApianCommand>();
            MaxAppliedCmdSeqNum = -1; // this+1 is what we expect to apply next
            MaxReceivedCmdSeqNum = -1; //  this is the largest we'vee seen - even if we haven't applied it yet

        }

        public abstract bool Update(); // Returns TRUE is local peer is in active state

        // Apian Messages

        public virtual void SendApianMessage(string toChannel, ApianMessage msg)
        {
            Logger.Verbose($"SendApianMsg() To: {toChannel} MsgType: {msg.MsgType} {((msg.MsgType==ApianMessage.GroupMessage)? "GrpMsgTYpe: "+(msg as ApianGroupMessage).GroupMsgType:"")}");
            GameNet.SendApianMessage(toChannel, msg);
        }

        public virtual void OnApianMessage(string fromId, string toId, ApianMessage msg, long lagMs)
        {
            // TODO: throw exception (or warn?) when MsgType isn't in the dictionary
            ApMsgHandlers[msg.MsgType](fromId, toId, msg, lagMs);
        }

        // Default Apian Msg handlers

        protected virtual void OnApianRequest(string fromId, string toId, ApianMessage msg, long delayMs)
        {
            GroupMgr.OnApianRequest(msg as ApianRequest, fromId, toId);
        }
        protected virtual void OnApianObservation(string fromId, string toId, ApianMessage msg, long delayMs)
        {
            GroupMgr.OnApianObservation(msg as ApianObservation, fromId, toId);
        }

       protected virtual void OnApianCommand(string fromId, string toId, ApianMessage msg, long delayMs)
        {
            ApianCommand cmd = msg as ApianCommand;
            ApianCommandStatus cmdStat = GroupMgr.EvaluateCommand(cmd, fromId, MaxAppliedCmdSeqNum);

            switch (cmdStat)
            {
            case ApianCommandStatus.kLocalPeerNotReady:
                Logger.Warn($"ApianBase.OnApianCommand(): Local peer not a group member yet");
                break;
            case ApianCommandStatus.kBadSource:
                Logger.Error($"ApianBase.OnApianCommand(): BAD COMMAND SOURCE: {fromId} Group: {cmd.DestGroupId}, Seq#: {cmd.SequenceNum} Type: {cmd.CoreMsgType }");
                break;
            case ApianCommandStatus.kAlreadyReceived:
                Logger.Error($"ApianBase.OnApianCommand(): Command Already Received: {fromId} Group: {cmd.DestGroupId}, Seq#: {cmd.SequenceNum} Type: {cmd.CoreMsgType}");
                break;

            case ApianCommandStatus.kStashedInQueue:
                Logger.Verbose($"ApianBase.OnApianCommand() Group: {cmd.DestGroupId}, Stashing Seq#: {cmd.SequenceNum} Type: {cmd.CoreMsgType}");
                MaxReceivedCmdSeqNum = Math.Max(cmd.SequenceNum, MaxReceivedCmdSeqNum); // is valid. we just aren;t ready for it
                break;
            case ApianCommandStatus.kShouldApply:
                Logger.Verbose($"ApianBase.OnApianCommand() Group: {cmd.DestGroupId}, Applying Seq#: {cmd.SequenceNum} Type: {cmd.CoreMsgType}");
                MaxReceivedCmdSeqNum = Math.Max(cmd.SequenceNum, MaxReceivedCmdSeqNum);
                ApplyApianCommand(cmd);
                break;

            default:
                Logger.Error($"ApianBase.OnApianCommand(): Unknown command status: {cmdStat}: Group: {cmd.DestGroupId}, Seq#: {cmd.SequenceNum} Type: {cmd.CoreMsgType}");
                break;
            }
        }

        protected void ApplyApianCommand(ApianCommand cmd)
        {
            ApianCoreMessage coreMsg = AppCore.DeserializeCoreMessage(cmd);
            MaxAppliedCmdSeqNum = cmd.SequenceNum;
            AppCore.OnApianCommand(cmd.SequenceNum, coreMsg);
            AppliedCommands[cmd.SequenceNum] = cmd;
        }

        public virtual void OnApianGroupMessage(string fromId, string toId, ApianMessage msg, long lagMs)
        {
            Logger.Debug($"OnApianGroupMessage(): {((msg as ApianGroupMessage).GroupMsgType)}");
            GroupMgr.OnApianGroupMessage(msg as ApianGroupMessage, fromId, toId);
        }

        public virtual void OnApianClockOffsetMsg(string fromId, string toId, ApianMessage msg, long lagMs)
        {
            Logger.Verbose($"OnApianClockOffsetMsg(): from {fromId}");
            ApianClock?.OnPeerApianOffset(fromId, (msg as ApianClockOffsetMsg).ClockOffset);
        }

        // CoreApp -> Apian API
        // TODO: You know, these should be interfaces
        protected virtual void SendRequest(ApianCoreMessage msg)
        {
            // Make sure these only get sent out if we are ACTIVE.
            // It wouldn't cause any trouble, since the groupmgr would not make it into a command
            // after seeing we aren't active - but there's a lot of message traffic between the 2

            // Also - this func can be overridden in any derived Apian class which is able to
            // be even more selctive (in a server-based group, for instance, if you're not the
            // server then you should just return)
            if (GroupMgr.LocalMember?.CurStatus != ApianGroupMember.Status.Active)
            {
                Logger.Debug($"SendRequest() - outgoing message not sent: We are not ACTIVE.");
                return;
            }
            GroupMgr.SendApianRequest(msg);
        }

        // IApianAppCore - only the AppCore calls these

        public virtual void SendObservation(ApianCoreMessage msg) // alwas goes to current group
        {
            // See comments in SendRequest
            if (GroupMgr.LocalMember?.CurStatus != ApianGroupMember.Status.Active)
            {
                Logger.Info($"SendObservation() - outgoing message not sent: We are not ACTIVE.");
                return;
            }

            if (batchedObservations == null)
                GroupMgr.SendApianObservation(msg);
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
            batchedObservations = new List<ApianCoreMessage>();
        }

        public virtual void EndObservationSet()
        {
            if (batchedObservations == null)
            {
                Logger.Warn($"ApianBase.EndObservationSet(): batchedObservations is unititialized. StartObservationSet() not called?");
                return;
            }

            // Sort by timestamp, earliest first
            batchedObservations.Sort( (a,b) => a.TimeStamp.CompareTo(b.TimeStamp));

            // TODO: run conflict resolution!!!
            List<ApianCoreMessage> obsToSend = new List<ApianCoreMessage>();
            foreach (ApianCoreMessage obsUnderTest in batchedObservations)
            {
                bool isValid = true; // TODO: should check against current CoreState
                string reason;
                foreach (ApianCoreMessage prevObs in obsToSend)
                {
                    ApianConflictResult effect = ApianConflictResult.Unaffected;
                    (effect, reason) = AppCore.ValidateCoreMessages(prevObs, obsUnderTest);
                    switch (effect)
                    {
                        case ApianConflictResult.Validated:
                            Logger.Info($"{obsUnderTest.MsgType} Observation Validated by {prevObs.MsgType}: {reason}");
                            isValid = true;
                            break;
                        case ApianConflictResult.Invalidated:
                            Logger.Info($"{obsUnderTest.MsgType} Observation invalidated by {prevObs.MsgType}: {reason}");
                            isValid = false;
                            break;
                        case ApianConflictResult.Unaffected:
                        default:
                            break;
                    }
                }
                // Still valid?
                if (isValid)
                    obsToSend.Add(obsUnderTest);
                else
                    Logger.Info($"{obsUnderTest.MsgType} Observation rejected.");
            }

            //Logger.Warn($"vvvv - Start Obs batch send - vvvv");
            foreach (ApianCoreMessage obs in obsToSend)
            {
                //Logger.Warn($"Type: {obs.ClientMsg.MsgType} TS: {obs.ClientMsg.TimeStamp}");
                GroupMgr.SendApianObservation(obs); // send the in acsending time order
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

        public virtual void ApplyCheckpointStateData(long epoch, long seqNum, long timeStamp, string stateHash, string stateData)
        {
            AppCore.ApplyCheckpointStateData( seqNum,  timeStamp,  stateHash,  stateData);
            MaxAppliedCmdSeqNum = seqNum;
        }

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
                    ApianClock.OnNewPeer(member.PeerId, syncData.clockOffsetMs, syncData.networkLagMs);
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

        public virtual void ApplyStashedApianCommand(ApianCommand cmd)
        {
            Logger.Info($"BeamApian.ApplyApianCommand() Group: {cmd.DestGroupId}, Applying STASHED Seq#: {cmd.SequenceNum} Type: {cmd.CoreMsgType} TS: {cmd.CoreMsgTimeStamp}");

            // If your AppCore includes running-time code that looks for future events in order to report observations
            // then you may want to override this and include a call to something like:
            //_AdvanceStateTimeTo((cmd as ApianWrappedCoreMessage).CoreMsgTimeStamp);

            ApplyApianCommand(cmd);

        }


        // called by AppCore
        public abstract void SendCheckpointState(long timeStamp, long seqNum, string serializedState); // called by client app


        // Other stuff
        public void OnPeerClockSync(string remotePeerId, long clockOffsetMs, long netLagMs) // sys + offset = apian
        {
            // TODO: This is awkward.
            ApianClock?.OnPeerClockSync( remotePeerId,  clockOffsetMs,  netLagMs);
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