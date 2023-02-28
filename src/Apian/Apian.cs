using System;
using System.Linq;
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
        void JoinGroup(string localGroupData, bool joinAsValidator); // There's no LeaveGroup
        void OnPeerLeftGroupChannel(string groupChannelId, string peerAddr);
        void OnPeerMissing(string groupChannelId, string peerAddr);
        void OnPeerReturned(string groupChannelId, string peerAddr);
        void OnPeerClockSync(string remotePeerAddr, long remoteClockOffset, long syncCount);
        void OnApianMessage(string fromAddr, string toAddr, ApianMessage msg, long lagMs);
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
        ApianGroupMember CreateGroupMember(string peerAddr, string memberJson, bool isValidator);

        // Handle reports from Apian Group
        void OnGroupMemberJoined(ApianGroupMember member);
        void OnGroupMemberLeft(ApianGroupMember member);
        void OnGroupJoinFailed(string peerAddr, string failureReason);
        void OnGroupMemberStatusChange(ApianGroupMember member, ApianGroupMember.Status prevStatus);

    }


    public abstract class ApianBase : IApianAppCoreServices, IApianClientServices, IApianGroupMgrServices
    {
		// public API
        protected Dictionary<string, Action<string, string, ApianMessage, long>> ApMsgHandlers;
        // Args are fromAddr, toAddr, ApianMsg, msDelay
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

        // Epochs
        public List<ApianEpoch> Epochs { get; private set; } // stack might be more efficient, since we usually want the most recent one?
        public ApianEpoch CurrentEpoch { get; private set; }
        public ApianEpoch PreviousEpoch {get => Epochs.Count > 0 ? Epochs.Last() : null; }

        public long CurrentEpochNum => CurrentEpoch != null ? CurrentEpoch.EpochNum : -1;


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

            // Calculate the CoreState's initial state hash:
            string serializedState = AppCore.DoCheckpointCoreState( 0,  0);
            string hash = GameNet.HashString(serializedState);
            //hash = "GenesisHash"; // // For looking at hash mismatch issues
            AppCore.StartEpoch(0, hash);

            InitializeEpochData(0,0,0, hash);
        }

        public void InitializeEpochData(long newEpoch, long startCmdSeqNum, long startTime, string startHash)
        {
            Epochs = new List<ApianEpoch>(); // delete history
            CurrentEpoch = new ApianEpoch(newEpoch, startCmdSeqNum, startTime, startHash);

        }


        public virtual void Update()
        {
            GroupMgr?.Update();
            ApianClock?.Update();
        }

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

        public virtual void OnApianMessage(string fromAddr, string toAddr, ApianMessage msg, long lagMs)
        {
            try {
                ApMsgHandlers[msg.MsgType](fromAddr, toAddr, msg, lagMs);
            } catch (NullReferenceException ex) {
                Logger.Error($"OnApianMessage(): No message handler for: '{msg.MsgType}'");
            }
        }


        public void SendPauseReq(long timeStamp,string reason, string id)
        {
            Logger.Verbose($"SendPauseReq) Reason: {reason}");
            PauseAppCoreMsg msg = new PauseAppCoreMsg(timeStamp, reason, id);
            SendRequest(msg);
        }

        public void SendResumeReq(long timeStamp, string pauseId)
        {
            Logger.Verbose($"SendResumeReq) ID: {pauseId}");
            ResumeAppCoreMsg msg = new ResumeAppCoreMsg(timeStamp, pauseId);
            SendRequest(msg);
        }

        // Default Apian Msg handlers

        protected virtual void OnApianRequest(string fromAddr, string toAddr, ApianMessage msg, long delayMs)
        {
            GroupMgr.OnApianRequest(msg as ApianRequest, fromAddr, toAddr);
        }
        protected virtual void OnApianObservation(string fromAddr, string toAddr, ApianMessage msg, long delayMs)
        {
            GroupMgr.OnApianObservation(msg as ApianObservation, fromAddr, toAddr);
        }

       protected virtual void OnApianCommand(string fromAddr, string toAddr, ApianMessage msg, long delayMs)
        {
            ApianCommand cmd = msg as ApianCommand;
            ApianCommandStatus cmdStat = GroupMgr.EvaluateCommand(cmd, fromAddr, MaxAppliedCmdSeqNum);

            switch (cmdStat)
            {
            case ApianCommandStatus.kLocalPeerNotReady:
                Logger.Warn($"ApianBase.OnApianCommand(): Local peer not a group member yet.");
                break;
            case ApianCommandStatus.kBadSource:
                Logger.Warn($"ApianBase.OnApianCommand(): BAD COMMAND SOURCE: {fromAddr} Group: {cmd.DestGroupId}, Seq#: {cmd.SequenceNum} Type: {cmd.PayloadMsgType }");
                break;
            case ApianCommandStatus.kAlreadyReceived:
                Logger.Warn($"ApianBase.OnApianCommand(): Command Already Received: {fromAddr} Group: {cmd.DestGroupId}, Seq#: {cmd.SequenceNum} Type: {cmd.PayloadMsgType}");
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

            // TODO: Need to hook into epoch ending and create/persist the epochs.
            // Will want to clean up AppliedCommands, too, since it just keeps getning bigger and bigger in memory.
            // What about the serilized state (I guess that's just epoch-related)
            // Think about potentially hoisting GroupMgr-owned stuff up to this level.
            //  ***  Hmm. Epoch-as-a-thing is actually not even at the GroupManagerBase level. THat's probably not good.

        }

        public virtual void OnApianGroupMessage(string fromAddr, string toAddr, ApianMessage msg, long lagMs)
        {
            Logger.Debug($"OnApianGroupMessage(): {((msg as ApianGroupMessage).GroupMsgType)}");
            GroupMgr.OnApianGroupMessage(msg as ApianGroupMessage, fromAddr, toAddr);
        }

        public virtual void OnApianClockOffsetMsg(string fromAddr, string toAddr, ApianMessage msg, long lagMs)
        {
            //  Make sure the source is an active member of the group before sending to local clock
            bool srcActive =  GroupMgr.GetMember(fromAddr)?.CurStatus == ApianGroupMember.Status.Active;
            Logger.Verbose($"OnApianClockOffsetMsg(): from {SID(fromAddr)} Active: {srcActive}");
            if (srcActive)
                ApianClock?.OnPeerApianOffset(fromAddr, (msg as ApianClockOffsetMsg).ClockOffset);

            // Always send to groupMgr (a first Offset report can result in a status change to Active)
            GroupMgr.OnApianClockOffset(fromAddr, (msg as ApianClockOffsetMsg).ClockOffset);

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
        // CreateGroupMember(string peerAddr, string appMemberDataJson) => new ApianGroupMember(peerAddr, appMemberDataJson);
        // But the whole point is to subclass ApianGroupMember, so don't do that.
        public abstract ApianGroupMember CreateGroupMember(string peerAddr, string appMemberDataJson, bool isValidator);

        public void SetupNewGroup(ApianGroupInfo info) => GroupMgr.SetupNewGroup(info);
        public void SetupExistingGroup(ApianGroupInfo info) => GroupMgr.SetupExistingGroup(info);
        public void JoinGroup(string localMemberJson, bool asValidator) => GroupMgr.JoinGroup(localMemberJson, asValidator);

        // checkpoints

        public virtual void DoLocalAppCoreCheckpoint(long chkApianTime, long seqNum) // called by groupMgr
        {
            // The big tricky bit here lies in the fact that an Epoch has a "StartStateHash" property which
            // is, in fact, the hash of the state at the END of the previous epoch. By having this "last epoch's hash"
            // as part of the current state, we end up with the epoch hashes being chained in much the same way that ethereum
            // blocks form a chain that breaks if you try to mess with it after the fact.

            // The tricky part is getting that StartStateHash properly populated. Since this is where the hash is calculated it
            // seems the appropriate place to set it. The hard part is that once we are in this function there are 3 distict
            // situations that we may be in regarding the epoch before the current one.

            // 1) normal, happy path: this func was called for the previous epoch and it is now stored in the Epochs list, and
            //    PerviousEpoch points to it. Easy. The CurrentEpoch's StartHash property was set last time this func was called,
            //    so we don;t need to do anything in particular
            //
            // 2) Fencepost #1: This is the first time the func has been called, and the current Epoch is Epoch #0.
            //    Somebody needs to have hashed the genesis state (after CoreState stor) and called CoreState.StartEpoch(0, hash)
            //    THe core will have been in its gensis state when passed to the Apian factory.ctor, so it's done there
            //
            // 3) First call, but CoreState was loaded from serialized data. Don't need to call anything since epochNum and startHash
            //    were set properly when serialzed adta was applied

            long oldEpochNum = CurrentEpoch.EpochNum;
            long newEpochNum = oldEpochNum + 1;

            string serializedState = AppCore.DoCheckpointCoreState( seqNum,  chkApianTime);

            //ApianCoreState cs = AppCore.GetCoreState(); //  testing
            //Logger.Warn($"++ Hashing Core State - Apian.CurrentEpoch(num,starthash): ({CurrentEpoch.EpochNum}, {CurrentEpoch.StartStateHash})");
            //Logger.Warn($"++ Hashing Core State - Actual core state(num, starthash): ({cs.EpochNum}, {cs.EpochStartHash})" );

            string hash = GameNet.HashString(serializedState);

            //hash = $"EndOfBlock{oldEpochNum}Hash"; // For looking at hash mismatch issues

            string hashSig = GameNet.EncodeUTF8AndSign(GroupMgr.LocalPeerAddr, hash);

            Logger.Verbose($"DoLocalAppCoreCheckpoint(): SeqNum: {seqNum}, Hash: {hash}, sig: {hashSig}");

            CurrentEpoch.CloseEpoch( seqNum, chkApianTime, hash, serializedState);
            Epochs.Add(CurrentEpoch);

            const int MaxStoredEpochs = 10;
            // keep in-mem list from growing forever
            while ( Epochs.Count > MaxStoredEpochs)
                Epochs.RemoveAt(0);

            // Clean out any stored commands older than oldest epoch
            if (Epochs.Count > 0)
            {
                long oldestSeqNum = Epochs[0].StartCmdSeqNumber; // don;t keep anything < this
                AppliedCommands = AppliedCommands.Where(kvp => kvp.Key < oldestSeqNum).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
            }

            CurrentEpoch = new ApianEpoch(newEpochNum, seqNum+1,  chkApianTime, hash);

            AppCore.StartEpoch(CurrentEpoch.EpochNum, hash); // set epochnum and starthash in CoreState

            GroupMgr.OnNewEpoch();

            // This doesn;t work
            //AppCore.SetEpochStartHash(hash); // sets the CoreState's prevStateHash property to "chain" the epochs

            if ( GroupMgr.LocalMember.CurStatus == ApianGroupMember.Status.Active)
            {
                Logger.Verbose($"DoLocalAppCoreCheckpoint(): Sending GroupCheckpointReportMsg");
                GroupCheckpointReportMsg rpt = new GroupCheckpointReportMsg(GroupMgr.LocalPeerAddr, GroupMgr.GroupId, oldEpochNum, seqNum, chkApianTime, hash, hashSig);
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
            return new ApianGroupStatus(GroupMgr.ActivePlayerCount, GroupMgr.ActiveValidatorCount, GroupMgr.ActiveMemberCount, GroupMgr.AppCorePaused);
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

            if (member.PeerAddr != GameNet.LocalPeerAddr() )
            {
                if (ApianClock != null)
                {
                    ApianClock.OnNewPeer(member.PeerAddr); // so our clock can send it the current local apianoffset
                }
            }

            GameNet.OnPeerJoinedGroup( member.PeerAddr, GroupId, member.IsValidator, true, null);

        }

        public virtual void OnGroupMemberLeft(ApianGroupMember member)
        {
            // Probably need to override this to do something game-specific

            Logger.Info($"OnGroupMemberLeft(): {UniLogger.SID(member?.PeerAddr)}");
            if (ApianClock != null)
                ApianClock.OnPeerLeft(member.PeerAddr); // also happens in OnPeerLeftGroupChannel - whichever happens first
        }

        public virtual void OnGroupJoinFailed(string peerAddr, string failureReason)
        {
            GameNet.OnPeerJoinedGroup( peerAddr, GroupId, false, false,  failureReason );
        }

        public virtual void OnGroupMemberStatusChange(ApianGroupMember member, ApianGroupMember.Status prevStatus)
        {
            // Note that the member status has already been changed when this is called
            Logger.Info($"OnGroupMemberStatusChange(): {UniLogger.SID(member.PeerAddr)} from {prevStatus} to {member.CurStatus}");
            GameNet.OnApianGroupMemberStatus( GroupId, member, prevStatus);
        }


        public virtual void OnPeerLeftGroupChannel(string groupId, string peerAddr)
        {
            Logger.Info($"OnPeerLeftGroupChannel(): {UniLogger.SID(peerAddr)}");
            // called from gamenet when P2pNet tells it the peer is gone.
            if (ApianClock != null)
                ApianClock.OnPeerLeft(peerAddr); // the clock is a network thing. So do this here as well as in OnGroupMemberLeft

            GroupMgr.OnMemberLeftGroupChannel(peerAddr); // will result in member marked Gone (locally handled) and group send of s GroupMemberLeftMsg

        }

        public void OnAppCorePaused( AppCorePauseInfo pInfo)
        {
            Logger.Info($"OnAppCorePaused(): Pausing for ID: {pInfo.PauseId}");
            ApianClock?.Pause();
        }

        public void OnAppCoreResumed( AppCorePauseInfo pInfo)
        {
            Logger.Info($"OnAppCoreResumed(): Resmeing from ID: {pInfo.PauseId}");
            ApianClock?.Resume();
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

        public void OnPeerClockSync(string remotePeerAddr,  long remoteClockOffset, long syncCount) // local + offset = remote time
        {
            // TODO++: ApianClocks don;t use this info when passed in. It gets stored until an ApianClockOffset msg
            // comes frmo a peer. Since this data can be fetched at any time from p2pnet it would be simpler
            // to do nothing here, and wait till an APianOffset msg comes in and then fetch it and send all the data to the
            // clock at once.
            ApianClock?.OnPeerClockSync( remotePeerAddr, remoteClockOffset, syncCount);
        }

        // "Missing" is a little tricky. It's not like Joining or Leaving a group - it's more of a network-level
        // notification that a peer is late being heard from - but not so badly that it's being dropped. In many
        // applications this can be dealt with by pausing or temporarily disabling stuff. It's VERY app-dependant
        // but when used well can make an otherwise unusable app useful for someone with a droppy connection (or a
        // computer prone to "pausing" for a couple seconds at a time - I've seen them both)
        public virtual void OnPeerMissing(string channelId, string peerAddr)
        {
        }

        public virtual void OnPeerReturned(string channelId, string peerAddr)
        {
        }
    }


}