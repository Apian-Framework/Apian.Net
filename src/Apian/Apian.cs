using System;
using System.Collections.Generic;
using P2pNet; // TODO: this is just for For PeerClockSyncInfo. Not sure I like the P2pNet dependency here
using UniLog;
using static UniLog.UniLogger; // for SID()

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
        // This is the interface that a client (almost certainly ApianGameNet) sees
        void SetupExistingGroup(ApianGroupInfo groupInfo);
        void SetupNewGroup(ApianGroupInfo groupInfo);
        void JoinGroup(string localGroupData); // There's no LeaveGroup
        void OnPeerLeftGroupChannel(string groupChannelId, string p2pId);
        void OnPeerMissing(string groupChannelId, string p2pId);
        void OnPeerReturned(string groupChannelId, string p2pId);
        void OnPeerClockSync(string remotePeerId, long remoteClockOffset, long syncCount);
        void OnApianMessage(string fromId, string toId, ApianMessage msg, long lagMs);
    }

    public interface IApianAppCoreServices
    {
        // This is the interface an AppCore sees
        void SendObservation(ApianCoreMessage msg);
        void StartObservationSet();
        void EndObservationSet();
    }

    public interface IApianGroupMgrServices
    {
        // This is the interface a group manager calls
        long MaxAppliedCmdSeqNum {get;}
        ApianGroupStatus CurrentGroupStatus();

        void SendApianMessage(string toChannel, ApianMessage msg);
        void DoLocalAppCoreCheckpoint(long chkApianTime, long seqNum);

        // GroupMgr asks Apian to create a pre-join provisional group member
        ApianGroupMember CreateGroupMember(string peerId, string memberJson);

        // Handle reports from Apian Group
        void OnGroupMemberJoined(ApianGroupMember member);
        void OnGroupMemberLeft(ApianGroupMember member);
        void OnGroupJoinFailed(string peerId, string failureReason);
        void OnGroupMemberStatusChange(ApianGroupMember member, ApianGroupMember.Status prevStatus);

    }


    public abstract class ApianBase : IApianAppCoreServices, IApianClientServices, IApianGroupMgrServices
    {
		// public API
        protected Dictionary<string, Action<string, string, ApianMessage, long>> ApMsgHandlers;
        // Args are fromId, toId, ApianMsg, msDelay
        public UniLogger Logger;
        public IApianGroupManager GroupMgr  {get; protected set;}  // set in a sublcass ctor
        public IApianClock ApianClock {get; protected set;}
        public IApianGameNet GameNet {get; private set;}
        public IApianAppCore AppCore {get; private set;}

        public ApianGroupInfo GroupInfo { get => GroupMgr.GroupInfo; }
        public string GroupName { get => GroupMgr.GroupName; }
        public string NetworkId { get => GameNet.CurrentNetworkId(); }
        public string GroupId { get => GroupMgr.GroupId; }
        public string GroupType { get => GroupMgr.GroupType; }

        public bool LocalPeerIsActive { get => GroupMgr.LocalMember?.CurStatus == ApianGroupMember.Status.Active; }

        // Observation Sets allow observations that are noticed during a CoreState "loop" (frame)
        // To be batched-up and then ordered and checked for conflict before being sent out.
        private List<ApianCoreMessage> batchedObservations;

        // Command-related stuff
        public Dictionary<long, ApianCommand> AppliedCommands; // All commands we have applied // TODO: write out/prune periodically?
        public  long MaxAppliedCmdSeqNum {get; private set;} // largest seqNum we have applied, inits to -1
        public  long MaxReceivedCmdSeqNum {get; private set;} // largest seqNum we have *received*, inits to -1

        protected ApianBase(IApianGameNet gn, IApianAppCore cl) {
            GameNet = gn;
            AppCore = cl;
            AppCore.SetApianReference(this);
            Logger = UniLogger.GetLogger("Apian");

            // Add any truly generic handlers here
            // params are:  from, to, apMsg, msSinceSent
            ApMsgHandlers = new Dictionary<string, Action<string, string, ApianMessage, long>>()
            {
                {ApianMessage.CliRequest, (f,t,m,d) => this.OnApianRequest(f,t,m,d) },
                {ApianMessage.CliObservation, (f,t,m,d) => this.OnApianObservation(f,t,m,d) },
                {ApianMessage.CliCommand, (f,t,m,d) => this.OnApianCommand(f,t,m,d) },
                {ApianMessage.GroupMessage, (f,t,m,d) => this.OnApianGroupMessage(f,t,m,d) },
                {ApianMessage.ApianClockOffset, (f,t,m,d) => this.OnApianClockOffsetMsg(f,t,m,d) }
            };

            AppliedCommands = new Dictionary<long, ApianCommand>();
            MaxAppliedCmdSeqNum = -1; // this+1 is what we expect to apply next
            MaxReceivedCmdSeqNum = -1; //  this is the largest we'vee seen - even if we haven't applied it yet

        }

        public abstract void Update();

        public abstract (bool, string) CheckQuorum(); // returns (bIsQuorum, ReasonIfNot)

        // Apian Messages

        public virtual ApianMessage DeserializeCustomApianMessage(string apianMsgType, string msgJson)
        {
            // Custom Apian messages? OVERRIDE THIS!!
            // If you want to add an *application*-dependent Apian message, deserialize it in your
            // <App>Apian subclass by overrideing this method.

            // If, on the other hand, you are writing a GroupManager (an agreement protocol type) and want to
            // define protocol-specific ApianMessages then the place to do it is in the GroupManager itself,
            // via: IApianGroupManager.DeserializeCustomApianMessage

            // First ask the groupManager instance if it wants to decode it...
            // It will return null if it doesn't.
            ApianMessage apMsg =  GroupMgr?.DeserializeCustomApianMessage(apianMsgType, msgJson);

            // This default method implmentation doesn't define any Application-custom messages,
            // so just return whatever the GroupManager returned (quite likely null)
            return apMsg;

        }

        public virtual void SendApianMessage(string toChannel, ApianMessage msg)
        {
            Logger.Verbose($"SendApianMsg() To: {toChannel} MsgType: {msg.MsgType} {((msg.MsgType==ApianMessage.GroupMessage)? "GrpMsgType: "+(msg as ApianGroupMessage).GroupMsgType:"")}");
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
                Logger.Warn($"ApianBase.OnApianCommand(): Local peer not a group member yet.");
                break;
            case ApianCommandStatus.kBadSource:
                Logger.Warn($"ApianBase.OnApianCommand(): BAD COMMAND SOURCE: {fromId} Group: {cmd.DestGroupId}, Seq#: {cmd.SequenceNum} Type: {cmd.PayloadMsgType }");
                break;
            case ApianCommandStatus.kAlreadyReceived:
                Logger.Warn($"ApianBase.OnApianCommand(): Command Already Received: {fromId} Group: {cmd.DestGroupId}, Seq#: {cmd.SequenceNum} Type: {cmd.PayloadMsgType}");
                break;

            case ApianCommandStatus.kStashedInQueue:
                Logger.Verbose($"ApianBase.OnApianCommand() Group: {cmd.DestGroupId}, Stashing Seq#: {cmd.SequenceNum} Type: {cmd.PayloadMsgType}");
                MaxReceivedCmdSeqNum = Math.Max(cmd.SequenceNum, MaxReceivedCmdSeqNum); // is valid. we just aren;t ready for it
                break;
            case ApianCommandStatus.kShouldApply:
                Logger.Verbose($"ApianBase.OnApianCommand() Group: {cmd.DestGroupId}, Applying Seq#: {cmd.SequenceNum} Type: {cmd.PayloadMsgType}");
                MaxReceivedCmdSeqNum = Math.Max(cmd.SequenceNum, MaxReceivedCmdSeqNum);
                ApplyApianCommand(cmd);
                break;

            default:
                Logger.Error($"ApianBase.OnApianCommand(): Unknown command status: {cmdStat}: Group: {cmd.DestGroupId}, Seq#: {cmd.SequenceNum} Type: {cmd.PayloadMsgType}");
                break;
            }
        }

        protected void ApplyApianCommand(ApianCommand cmd)
        {

            switch (cmd.PayloadSubSys)
            {
                case ApianCoreMessage.kAppCore:
                    AppCore.OnApianCommand(cmd.SequenceNum, AppCore.DeserializeCoreMessage(cmd) );
                    break;
                case ApianCoreMessage.kGroupMgr:
                    AppCore.OnApianCommand(cmd.SequenceNum,null);
                    GroupMgr.ApplyGroupCoreCommand(cmd.Epoch, cmd.SequenceNum, GroupMgr.DeserializeGroupMessage(cmd) as GroupCoreMessage);
                    break;
                default:
                    Logger.Warn($"ApplyApianCommand: Unknown command source: {cmd.PayloadSubSys}");
                    break;
            }
            MaxAppliedCmdSeqNum = cmd.SequenceNum;
            AppliedCommands[cmd.SequenceNum] = cmd;
        }

        public virtual void OnApianGroupMessage(string fromId, string toId, ApianMessage msg, long lagMs)
        {
            Logger.Debug($"OnApianGroupMessage(): {((msg as ApianGroupMessage).GroupMsgType)}");
            GroupMgr.OnApianGroupMessage(msg as ApianGroupMessage, fromId, toId);
        }

        public virtual void OnApianClockOffsetMsg(string fromId, string toId, ApianMessage msg, long lagMs)
        {
            //  Make sure the source is an active member of the group before sending to local clock
            bool srcActive =  GroupMgr.GetMember(fromId)?.CurStatus == ApianGroupMember.Status.Active;
            Logger.Verbose($"OnApianClockOffsetMsg(): from {SID(fromId)} Active: {srcActive}");
            if (srcActive)
                ApianClock?.OnPeerApianOffset(fromId, (msg as ApianClockOffsetMsg).ClockOffset);

            // Always send to groupMgr (a first Offset report can result in a status change to Active)
            GroupMgr.OnApianClockOffset(fromId, (msg as ApianClockOffsetMsg).ClockOffset);

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
                Logger.Debug($"SendRequest() - outgoing message not sent: We are not ACTIVE. Status: {GroupMgr.LocalMember?.CurStatusName}");
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
                Logger.Verbose($"SendObservation() - outgoing message not sent: We are not ACTIVE {GroupMgr.LocalMember?.CurStatusName}.");
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
                            Logger.Verbose($"{obsUnderTest.MsgType} Observation Validated by {prevObs.MsgType}: {reason}");
                            isValid = true;
                            break;
                        case ApianConflictResult.Invalidated:
                            Logger.Verbose($"{obsUnderTest.MsgType} Observation invalidated by {prevObs.MsgType}: {reason}");
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
                    Logger.Verbose($"{obsUnderTest.MsgType} Observation rejected.");
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

        // Called by the GroupManager. The absolute minimum for this would be:
        // CreateGroupMember(string peerId, string appMemberDataJson) => new ApianGroupMember(peerId, appMemberDataJson);
        // But the whole point is to subclass ApianGroupMember, so don't do that.
        public abstract ApianGroupMember CreateGroupMember(string peerId, string appMemberDataJson);

        public void SetupNewGroup(ApianGroupInfo info) => GroupMgr.SetupNewGroup(info);
        public void SetupExistingGroup(ApianGroupInfo info) => GroupMgr.SetupExistingGroup(info);
        public void JoinGroup(string localMemberJson) => GroupMgr.JoinGroup(localMemberJson);

        // checkpoints

        public virtual void DoLocalAppCoreCheckpoint(long chkApianTime, long seqNum) // called by groupMgr
        {
            string serializedState = AppCore.DoCheckpointCoreState( seqNum,  chkApianTime);

            string hash = ApianHash.HashString(serializedState);
            Logger.Verbose($"DoLocalAppCoreCheckpoint(): SeqNum: {seqNum}, Hash: {hash}");

            GroupMgr.OnLocalStateCheckpoint(seqNum, chkApianTime, hash, serializedState);

            if ( GroupMgr.LocalMember.CurStatus == ApianGroupMember.Status.Active)
            {
                Logger.Verbose($"DoLocalAppCoreCheckpoint(): Sending GroupCheckpointReportMsg");
                GroupCheckpointReportMsg rpt = new GroupCheckpointReportMsg(GroupMgr.GroupId, seqNum, chkApianTime, hash);
                GameNet.SendApianMessage(GroupMgr.GroupId, rpt);
            }

        }

        public virtual void ApplyCheckpointStateData(long epoch, long seqNum, long timeStamp, string stateHash, string stateData)
        {
            AppCore.ApplyCheckpointStateData( seqNum,  timeStamp,  stateHash,  stateData);
            MaxAppliedCmdSeqNum = seqNum;
        }

        // FROM GroupManager

        public virtual ApianGroupStatus CurrentGroupStatus()
        {
            return new ApianGroupStatus(GroupMgr.ActiveMemberCount);
        }

        // TODO: put this back when I'm actually ready for it.
         // Is there an App reason for refusing? Return NULL to allow, failure reason otherwise
        //abstract public string ValidateJoinRequest( GroupJoinRequestMsg requestMsg);

        //
        // Definitive calls from GroupManager
        //

        public virtual void OnGroupMemberJoined(ApianGroupMember member)
        {
            // Note that this does NOT signal that the new member is Active. Just that it is joining.

            // By default just helps getting ApianClock set up and reports back to GameNet/Client
            // App-specific Apian instance needs to field this if it cares for any other reason.
            // Note that the local gameinstance usually doesn't care about a remote peer joining a group until a Player Joins the gameInst
            // But it usually DOES care about the LOCAL peer's group membership status.

            // TODO: this used to look up the peer's clock sync data and pass it so as not to wait for a sync
            // Is that necessary?

            if (member.PeerId != GameNet.LocalP2pId() )
            {
                if (ApianClock != null)
                {
                    ApianClock.OnNewPeer(member.PeerId); // so our clock can send it the current local apianoffset
                }
            }

            GameNet.OnPeerJoinedGroup( member.PeerId, GroupId, true, null);

        }

        public virtual void OnGroupMemberLeft(ApianGroupMember member)
        {
            // Probably need to override this to do something game-specific

            Logger.Info($"OnGroupMemberLeft(): {UniLogger.SID(member?.PeerId)}");
            if (ApianClock != null)
                ApianClock.OnPeerLeft(member.PeerId); // also happens in OnPeerLeftGroupChannel - whichever happens first
        }

        public virtual void OnGroupJoinFailed(string peerId, string failureReason)
        {
            GameNet.OnPeerJoinedGroup( peerId, GroupId, false,  failureReason );
        }

        public virtual void OnGroupMemberStatusChange(ApianGroupMember member, ApianGroupMember.Status prevStatus)
        {
            // Note that the member status has already been changed when this is called
            Logger.Info($"OnGroupMemberStatusChange(): {UniLogger.SID(member.PeerId)} from {prevStatus} to {member.CurStatus}");
            GameNet.OnApianGroupMemberStatus( GroupId, member.PeerId, member.CurStatus, prevStatus);
        }


        public virtual void OnPeerLeftGroupChannel(string groupId, string peerId)
        {
            Logger.Info($"OnPeerLeftGroupChannel(): {UniLogger.SID(peerId)}");
            // called from gamenet when P2pNet tells it the peer is gone.
            if (ApianClock != null)
                ApianClock.OnPeerLeft(peerId); // the clock is a network thing. So do this here as well as in OnGroupMemberLeft

            GroupMgr.OnMemberLeftGroupChannel(peerId); // will result in member marked Gone (locally handled) and group send of s GroupMemberLeftMsg

        }


        public virtual void ApplyStashedApianCommand(ApianCommand cmd)
        {
            Logger.Verbose($"BeamApian.ApplyApianCommand() Group: {cmd.DestGroupId}, Applying STASHED Seq#: {cmd.SequenceNum} Type: {cmd.PayloadMsgType}"); //&&&& TS: {cmd.PayloadTimeStamp}");

            // If your AppCore includes running-time code that looks for future events in order to report observations
            // then you may want to override this and include a call to something like:
            //_AdvanceStateTimeTo((cmd as ApianWrappedCoreMessage).CoreMsgTimeStamp);

            ApplyApianCommand(cmd);

        }


        // Other stuff

        public void OnPeerClockSync(string remotePeerId,  long remoteClockOffset, long syncCount) // local + offset = remote time
        {
            // TODO++: ApianClocks don;t use this info when passed in. It gets stored until an ApianClockOffset msg
            // comes frmo a peer. Since this data can be fetched at any time from p2pnet it would be simpler
            // to do nothing here, and wait till an APianOffset msg comes in and then fetch it and send all the data to the
            // clock at once.
            ApianClock?.OnPeerClockSync( remotePeerId, remoteClockOffset, syncCount);
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