using System.Linq;
using System;
using System.Collections.Generic;
using UniLog;
using static UniLog.UniLogger; // for SID

namespace Apian
{
    // This is a very basic base class for "Leader decides everything" group managers
    // It does include checkpointing and synchronization.

    public abstract class LeaderDecidesGmBase : ApianGroupManagerBase
    {

        public const int kAllowedSkippedCommands = 2;
        // This is a bit of a hack to deal with small message ordering issues.
        // If a command is missed, allow a couple more to come in (and get stashed) before requesting a resync
        // just in case it's a simple delivery order issue.
        // Admittedly, this shouldn't happen - but it's easy to account for.

        public static  Dictionary<string, string> DefaultConfig = new Dictionary<string, string>()
        {
            {"CheckpointMs", "5000"},  // request a checkpoint this often
            {"CheckpointOffsetMs", "50"},  // Use this to get the checkpoint times NOT to be on roudning boundaries
            {"SyncCompletionWaitMs", "2000"}, // wait this long for a sync completion request reply
            {"StashedCmdsToApplyPerUpdate", "10"}, // CommandSynchronizer -  applying locally received commands that we weren't ready for yet
            {"MaxSyncCmdsToSendPerUpdate", "10"} // CommandSynchronizer - sending commands to another peer to "catch it up"
        };

        public class EpochData
        {
            public long EpochNum;
            public long StartCmdSeqNumber; // first command in the epoch
            public long EndCmdSeqNumber; // This is the seq # of the LAST command - which is a checkpoint request
            public long TimeStamp;  // TODO: Start of end of epoch - rename to make clear (I'm pretty sure end)
            public string EndStateHash;
            public string SerializedStateData;
            // TODO: add this dict and make the stashedStateData member of LeaderOnly a dict
            // and keep a couple of them - tracking how well they (hashes) were agreed with.
            // Maybe when asked it's better to send out the "one before last" if the most recent disagreed
            // with all the other peers?
            // public Dictionary<string, string> ReportedHashes; // keyed by reporting peerId

            public EpochData(long epochNum, long startCmdSeqNum, long endCmdSeqNum)
            {
                EpochNum = epochNum;
                StartCmdSeqNumber = startCmdSeqNum;
                EndCmdSeqNumber = endCmdSeqNum;
                // ReportedHashes = new Dictionary<string, string>();
            }
        }


        // Things only the leader uses. (Used to be in leaderdata but that became a mess)
        private long _nextNewCommandSeqNum; // really should ever access this. Leader should use GetNewCommandSequenceNumber()
        public long GetNewCommandSequenceNumber() => _nextNewCommandSeqNum++;

        protected void SetNextNewCommandSequenceNumber(long newCommandSequenceNumber) {_nextNewCommandSeqNum = newCommandSequenceNumber;}


        public long CurrentEpochNum {get => curEpochData.EpochNum; }
        public EpochData curEpochData;
        public EpochData prevEpochData; // TODO: this needs to be a collection and should be persistent



        private ApianGroupSynchronizer CmdSynchronizer; // used by both source and dest peers for sync

        // IApianGroupManager
        public bool Intialized {get => GroupInfo != null; }

        // State data
        private long DontRequestSyncBeforeMs; // When waiting for a SyncCompletionRequest reply, this is when it's ok to give up and ask again
        private long NextCheckPointMs; // when to ask for the next. UpdateNextCheckPointMs() sets it safely


        // Config params
        protected Dictionary<string,string> ConfigDict;
        private long SyncCompletionWaitMs; // wait this long for a sync completion request reply  &&&&&& Here? Or in Synchronizer?
        private long CheckpointMs; // how often
        private long CheckpointOffsetMs; // small random offset

        public string GroupLeaderId {get; set;}
        public bool LocalPeerIsLeader {get => GroupLeaderId == LocalPeerId;}

        private static long SysMsNow => DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond; // FIXME: replace with testable construct

        public LeaderDecidesGmBase(ApianBase apianInst, Dictionary<string,string> config=null) : base(apianInst)
        {
            config = config ?? DefaultConfig;
            _ParseConfig(config);
            CmdSynchronizer = new ApianGroupSynchronizer(apianInst, config);
            GroupMsgHandlers = new Dictionary<string, Action<ApianGroupMessage, string, string>>() {
                {ApianGroupMessage.GroupsRequest, OnGroupsRequest },
                {ApianGroupMessage.GroupJoinRequest, OnGroupJoinRequest },
                {ApianGroupMessage.GroupJoinFailed, OnGroupJoinFailed },
                {ApianGroupMessage.GroupLeaveRequest, OnGroupLeaveRequest },
                {ApianGroupMessage.GroupMemberJoined, OnGroupMemberJoined },
                {ApianGroupMessage.GroupMemberStatus, OnGroupMemberStatus },
                {ApianGroupMessage.GroupSyncRequest, OnGroupSyncRequest },
                {ApianGroupMessage.GroupSyncData, OnGroupSyncData },
                {ApianGroupMessage.GroupSyncCompletion, OnGroupSyncCompletionMsg },
                {ApianGroupMessage.GroupCheckpointReport, OnGroupCheckpointReport },
             };

            GroupCoreCmdHandlers = new Dictionary<string, Action< long, long, GroupCoreMessage>> {
                {GroupCoreMessage.CheckpointRequest , OnCheckpointRequestCmd },
            };

            // This might have to be overridden by any subclass ctor (this will execute before the subclass ctor)
            groupMgrMsgDeser = new GroupCoreMessageDeserializer();

            InitializeEpochData(0, 0);
        }

        private void _ParseConfig( Dictionary<string,string> config)
        {
            ConfigDict = config; // ugly
            SyncCompletionWaitMs = int.Parse(config["SyncCompletionWaitMs"]);
            CheckpointMs = int.Parse(config["CheckpointMs"]);
            CheckpointOffsetMs = int.Parse(config["CheckpointOffsetMs"]);
        }

        public override void SetupNewGroup(ApianGroupInfo info)
        {
            Logger.Info($"{this.GetType().Name}.SetupNewGroup(): {info.GroupName}");

            if (!info.GroupType.Equals(GroupType, StringComparison.Ordinal))
                Logger.Error($"SetupNewGroup(): incorrect GroupType: {info.GroupType} in info. Should be: {GroupType}");

            // Creating a new groupIfop with us as creator
            GroupInfo = info;

            SetLeader(info.GroupCreatorId); // creater starts out as leader

            // Do this *after* we are a member
            //ApianInst.ApianClock.SetTime(0); // we're the group leader so we need to start our clock
            //NextCheckPointMs = CheckpointMs + CheckpointOffsetMs; // another leader thing
        }

        public override void SetupExistingGroup(ApianGroupInfo info)
        {
            Logger.Info($"{this.GetType().Name}.SetGroupInfo(): {info.GroupId}");
            GroupInfo = info;
            SetLeader( info.GroupCreatorId ); // creator starts as leader
        }

        public override void JoinGroup(string localMemberJson)
        {
            if (GroupInfo == null)
                Logger.Error($"GroupMgr.JoinGroup() - group uninitialized."); // TODO: once again - this should probably throw.

            Logger.Info($"{this.GetType().Name}.JoinGroup(): {GroupInfo?.GroupId} Sending join request to group");
            // Send the request to the entire group (we might not know the leader yet)
            ApianInst.GameNet.SendApianMessage(GroupId, new GroupJoinRequestMsg(GroupId, LocalPeerId, localMemberJson));
        }
        public override void LeaveGroup()
        {
            // Question... do we REALLY want to send a request and wait? My guess is not - apps will will send the request
            // and then just proceed to shut down the group locally. Th rest of the group can deal with the message
            Logger.Info($"{this.GetType().Name}.LeaveGroup(): {GroupInfo?.GroupId}");

            ApianInst.GameNet.SendApianMessage(GroupLeaderId, new GroupLeaveRequestMsg(GroupId, LocalPeerId));

            // ApianInst.OnGroupMemberStatusChange(LocalMember, ApianGroupMember.Status.Removed); If we wait this is what'll eventually happen.
            // Maybe we should just do it here? Nah.
        }

        public override void Update()
        {
            if (LocalPeerIsLeader)
                _LeaderUpdate();

            if (CmdSynchronizer?.ApplyStashedCommands() == true) // always tick this. returns true if we were behind and calling it caught us up.
            {
                if (LocalMember.CurStatus == ApianGroupMember.Status.SyncingState && SysMsNow > DontRequestSyncBeforeMs)
                {
                    Logger.Info($"{this.GetType().Name}.Update(): Sending SyncCompletion request.");
                    ApianInst.SendApianMessage(GroupLeaderId, new GroupSyncCompletionMsg(GroupId, ApianInst.MaxAppliedCmdSeqNum, "hash"));
                    DontRequestSyncBeforeMs = SysMsNow + SyncCompletionWaitMs; // ugly "wait a bit before doing it again" timer
                }
            }
        }

        //
        // Leader stuff
        //

        protected void SetLeader(string newLeaderId)
        {
            Logger.Info($"{this.GetType().Name}.SetLeader() - setting group leader to {SID(newLeaderId)}");
            GroupLeaderId = newLeaderId;

            ApianInst.GameNet.OnNewGroupLeader(newLeaderId, GetMember(newLeaderId));
        }

        private void _LeaderUpdate()
        {
            CmdSynchronizer.SendSyncData();
            _DoCheckpointRequest();
        }

        private void _DoCheckpointRequest()
        {
            if (!ApianInst.ApianClock.IsIdle)
            {
                long apMs = ApianInst.ApianClock.CurrentTime;
                if (NextCheckPointMs >= 0 && apMs > NextCheckPointMs)
                {
                    _SendCheckpointRequestCmd(apMs, NextCheckPointMs);
                    NextCheckPointMs = -1; // disable until it gets reset
                }
            }
        }

        private void UpdateNextCheckPointMs(long prevVal)
        {
            NextCheckPointMs = prevVal + CheckpointMs;
        }

        private void _SendCheckpointRequestCmd(long curApainMs, long checkPointMs)
        {
            CheckpointRequestMsg cpMsg = new CheckpointRequestMsg(checkPointMs);
            ApianCommand cpCmd = new ApianCommand( CurrentEpochNum, GetNewCommandSequenceNumber(), GroupId, cpMsg);
            Logger.Info($"{this.GetType().Name}._SendCheckpointCommand() SeqNum: {cpCmd.SequenceNum}, Timestamp: {checkPointMs} (current time: {curApainMs})");
            ApianInst.SendApianMessage(GroupId, cpCmd);

            // Only the leader formerly kept track of epochs, and started a new one here.

            //StartNewEpoch(cpCmd.SequenceNum); // sending out a checkpoint cmds ends the current epoch for the leader
            // othe rmembers do it upon receipt.  TODO: should everyone do it theN?

            // Also: the prev epoch's data can;t be filled in until the local peer's response comes back. WHich suggests that even
            // the leader should end the epoch when it has serialized the state is and is about to send the checkpoint reply
        }

        public void InitializeEpochData(long newEpoch, long startCmdSeqNum)
        {
            prevEpochData = null;
            curEpochData = new EpochData(newEpoch,startCmdSeqNum,-1); // -1 means it hasn't ended yet
        }

        public virtual void StartNewEpoch(long lastCmdSeqNum)
        {
            // Checkpoint command has been sent, to this epoch is done.
            // But - the epoch state and hash aren;t known until the local peer sends the
            // checkpoint reponse.
            prevEpochData = curEpochData;
            curEpochData = new EpochData(prevEpochData.EpochNum+1, lastCmdSeqNum+1, -1);  // this is where CurEpochNum gets incremented
        }

        public void StoreCompletedEpoch(long lastSeqNum, long timeStamp, string stateHash, string serializedState)
        {
            // TODO: this needs to do something persistent with the data, too
            prevEpochData.EndCmdSeqNumber = lastSeqNum;
            prevEpochData.TimeStamp = timeStamp;
            prevEpochData.EndStateHash = stateHash;
            prevEpochData.SerializedStateData = serializedState;

            // TODO: when this returns, the caller needs to dispatch any ApianGroupMsgs waiting for it (sync requests)
        }


        //
        // End leader stuff
        //

        public override void SendApianRequest( ApianCoreMessage coreMsg )
        {
            ApianInst.GameNet.SendApianMessage(GroupId, new ApianRequest(GroupId, coreMsg));
        }
        public override void SendApianObservation( ApianCoreMessage coreMsg )
        {
            if (LocalPeerIsLeader)
                ApianInst.GameNet.SendApianMessage(GroupId, new ApianObservation(GroupId, coreMsg));
        //    else
        //    {
        //         // This next line is too verbose for even Debug-level
        //         //Logger.Debug($"SendApianObservation() We are not server, so don't send observations.");
        //    }
        }

        public override void OnApianClockOffset(string peerId, long ApianClockOffset)
        {
            // If this comes in from a peer who is Syncing the clock, and we are the leader, make it Active
            base.OnApianClockOffset(peerId, ApianClockOffset);

            if (LocalPeerIsLeader && GetMember(peerId)?.CurStatus == ApianGroupMember.Status.SyncingClock)
            {
                Logger.Debug($"OnApianClockOffset(): Setting peer {SID(peerId)} to Active");
                ApianInst.SendApianMessage(GroupId, new GroupMemberStatusMsg(GroupId, peerId, ApianGroupMember.Status.Active));
           }

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
            // Requests are assumed to be valid as long as source is Active
            if (LocalPeerIsLeader && GetMember(msgSrc)?.CurStatus == ApianGroupMember.Status.Active)
            {
                Logger.Debug($"OnApianRequest(): upgrading {msg.PayloadMsgType} from {SID(msgSrc)} to Command");
                ApianInst.GameNet.SendApianMessage(msgChan, new ApianCommand(CurrentEpochNum, GetNewCommandSequenceNumber(), msg));
            }
        }

        public override void OnApianObservation(ApianObservation msg, string msgSrc, string msgChan)
        {
            // Observations from the leader are turned into commands by the leader
            if (LocalPeerIsLeader && (msgSrc == LocalPeerId))
                ApianInst.GameNet.SendApianMessage(msgChan, new ApianCommand(CurrentEpochNum, GetNewCommandSequenceNumber(), msg));
        }

        public override ApianCommandStatus EvaluateCommand(ApianCommand msg, string msgSrc, long maxAppliedCmdNum)
        {
            // Too early
            if (LocalMember == null)
                return ApianCommandStatus.kLocalPeerNotReady;

            if (msgSrc != GroupLeaderId)
                return ApianCommandStatus.kBadSource;

            // if we are processing the cache (syncing) and get to this then we are done.
            // Keep in mind that after we've requested sync data we'll be getting both LIVE commands
            // (with bigger #s) and "we asked for em" commands.
            long expectedSeqNum = maxAppliedCmdNum + 1;

            // TODO: this is hideous with all the returns and convolution.
            // Once it's working figure out how to make it elegant.
            if (LocalMember.CurStatus == ApianGroupMember.Status.Active)
            {
                if (msg.SequenceNum <= maxAppliedCmdNum)
                    return ApianCommandStatus.kAlreadyReceived;
                else if (msg.SequenceNum > expectedSeqNum)
                {
                    Logger.Warn($"{this.GetType().Name}.EvaluateCommand() Out of sync. Expected Seq#: {expectedSeqNum}, Got: {msg.SequenceNum}");
                    CmdSynchronizer.StashCommand(msg);
                    // Try not to ask for a resync if just waiting for a couple more messages is good enough...
                    if (msg.SequenceNum - expectedSeqNum > kAllowedSkippedCommands)
                    {
                        Logger.Warn($"{this.GetType().Name}.EvaluateCommand() Requesting resync");
                        // Go back to "sync" mode
                        // TODO: resync needs to be fixed:
                        //   server currently will send a serialized data set, even if unneeded.
                        //   switching BACK from sync state to active tries to re-add the player... bad.
                        //   Probably other stuff
                        _RequestSync(msg.SequenceNum, maxAppliedCmdNum);
                    }

                    return ApianCommandStatus.kStashedInQueue;
                }

            } else {
                // Apian.Net issue #37 makes newly joined peers ask immediately for sync data, rather than
                // waiting for a command to arrive. This is necessary in order to support Apian.Net#36 which
                // creates the idea of "Apian Quorum", under which commands are not issued unless previously
                // spcified quorum conditions (# of peers, for instance) are met. Needsless to say, if commands
                // aren;t getting sent out because there is only 1 peers and 3 are required, under the "wait
                // for a command before asking to sync" system no other peer could ever join.
                // TODO: remove all this comment and code...

                // TODO: This is how we enter Sync mode. Maybe it should be more explicit?
                // if (LocalMember.CurStatus == ApianGroupMember.Status.Joining)
                //     _RequestSync(msg.SequenceNum, maxAppliedCmdNum);

                CmdSynchronizer.StashCommand(msg);
                return ApianCommandStatus.kStashedInQueue;
            }
            return ApianCommandStatus.kShouldApply;
        }

        private void _RequestSync(long firstStashedSeqNum, long maxAppliedCmdNum)
        {
            // Here we actually set our own status and short-circuit the MemberStatusMsg
            // process - we WILL recieve that message in a bit, but in the meantime we want to
            // stop processing incoming commands and stash them instead.

            long firstSeqNumWeNeed = maxAppliedCmdNum + 1;  // Since the variable inits to -1, this is 0 if we haven't applied any

            GroupSyncRequestMsg syncRequest = new GroupSyncRequestMsg(GroupId, firstSeqNumWeNeed, firstStashedSeqNum);
            Logger.Info($"{this.GetType().Name}._RequestSync() sending req: start: {syncRequest.ExpectedCmdSeqNum} 1st Stashed: {syncRequest.FirstStashedCmdSeqNum}");
            ApianInst.SendApianMessage(GroupLeaderId, syncRequest);

            // the local short-circuit... so we do the right thing when data arrives
            ApianGroupMember.Status prevStatus = LocalMember.CurStatus;
            LocalMember.CurStatus = ApianGroupMember.Status.SyncingState;
            ApianInst.OnGroupMemberStatusChange(LocalMember, prevStatus);
            // when leader gets the sync request it'll broadcast an identical status change msg
        }

        //  GroupCoreMessage Command Handlers

        protected void OnCheckpointRequestCmd( long epoch, long seqNum, GroupCoreMessage msg)
        {
           // TODO: really seems wrong for this to happen here rather than in the ApianInst
           CheckpointRequestMsg cMsg = msg as CheckpointRequestMsg;
           ApianInst.DoLocalAppCoreCheckpoint(msg.TimeStamp, seqNum);
           UpdateNextCheckPointMs(msg.TimeStamp);
        }

        // ApianGroupMessage handlers

        protected void OnGroupsRequest(ApianGroupMessage msg, string msgSrc, string msgChannel)
        {
            if (LocalPeerIsLeader)
            {
                Logger.Info($"{this.GetType().Name}.OnGroupsRequest() Got GroupsRequest, sending response.");
                GroupAnnounceMsg amsg = new GroupAnnounceMsg(GroupInfo, ApianInst.CurrentGroupStatus());
                ApianInst.SendApianMessage(msgSrc, amsg);
            }
        }

        protected virtual void OnGroupJoinRequest(ApianGroupMessage msg, string msgSrc, string msgChannel)
        {
            // In this implementation the Leader decides
            // Everyone else just ignores this.
            if (LocalPeerIsLeader)
            {
                GroupJoinRequestMsg jreq = msg as GroupJoinRequestMsg;

                Logger.Info($"{this.GetType().Name}.OnGroupJoinRequest(): Affirming {jreq.DestGroupId} req from {SID(jreq.PeerId)}");

                // Send current members to new joinee - do it bfore sending the new peers join msg
                SendMemberJoinedMessages(jreq.PeerId);

                // Don't add (happens in OnGroupMemberJoined())
                GroupMemberJoinedMsg jmsg = new GroupMemberJoinedMsg(GroupId, jreq.PeerId, jreq.ApianClientPeerJson);
                ApianInst.SendApianMessage(GroupId, jmsg); // tell everyone about the new kid last

                // Now send status updates (from "joined") for any member that has changed status
                SendMemberStatusUpdates(jreq.PeerId);
            }
        }

        protected void OnGroupLeaveRequest(ApianGroupMessage msg, string msgSrc, string msgChannel)
        {
            if (LocalPeerIsLeader)
            {
                GroupLeaveRequestMsg jreq = msg as GroupLeaveRequestMsg;
                Logger.Info($"{this.GetType().Name}.OnGroupLeaveRequest()");

                // Just remove.
                ApianInst.SendApianMessage(GroupId, new GroupMemberStatusMsg(GroupId, jreq.PeerId,ApianGroupMember.Status.Removed));
            }
        }

        protected void SendMemberJoinedMessages(string toWhom)
        {
            // Send a newly-approved member all of the Join messages for the other member
            // Create messages first, then send (mostly so Members doesn;t get modified while enumerating it)
            List<GroupMemberJoinedMsg> joinMsgs = Members.Values
                .Where( m => m.PeerId != toWhom)
                .Select( (m) =>  new GroupMemberJoinedMsg(GroupId, m.PeerId, m.AppDataJson)).ToList();
            foreach (GroupMemberJoinedMsg msg in joinMsgs)
                ApianInst.SendApianMessage(toWhom, msg);
        }

        protected void SendMemberStatusUpdates(string toWhom)
        {
            // Send a newly-approved member the status of every non-"Joining" member (since a joinmessage was already sent)
            List<GroupMemberStatusMsg> statusMsgs = Members.Values
                .Where( (m) => m.CurStatus != ApianGroupMember.Status.Joining && m.PeerId != toWhom)
                .Select( (m) =>  new GroupMemberStatusMsg(GroupId, m.PeerId, m.CurStatus)).ToList();
            foreach (GroupMemberStatusMsg msg in statusMsgs)
                ApianInst.SendApianMessage(toWhom, msg);
        }


        protected virtual void OnGroupMemberJoined(ApianGroupMessage msg, string msgSrc, string msgChannel)
        {
            if (msgSrc == GroupLeaderId)
            {
                GroupMemberJoinedMsg joinedMsg = (msg as GroupMemberJoinedMsg);
                Logger.Info($"{this.GetType().Name}.OnGroupMemberJoined() from boss:  {joinedMsg.DestGroupId} adds {SID(joinedMsg.PeerId)}");

                ApianGroupMember m = _AddMember(joinedMsg.PeerId, joinedMsg.ApianClientPeerJson);

                ApianInst.OnGroupMemberJoined(m); // inform local apian

                // Is this OUR join message?
                if (joinedMsg.PeerId == LocalPeerId)
                {
                    if  (LocalPeerIsLeader) // are we the leader?
                    {
                        // Now that we are joined, start the clock
                        ApianInst.ApianClock.SetTime(0); // we're the group leader so we need to start our clock
                        NextCheckPointMs = CheckpointMs + CheckpointOffsetMs; // another leader thing

                        // Yes, Which means we're also the first. Declare  *us* "Active" and tell everyone
                        ApianInst.SendApianMessage(GroupId, new GroupMemberStatusMsg(GroupId, LocalPeerId, ApianGroupMember.Status.Active));
                    } else {
                        // Not leader - request data sync.
                        _RequestSync(-1, -1); // we've neither applied nor stashed any commands
                    }
                }
            }
        }

        protected void OnGroupJoinFailed(ApianGroupMessage msg, string msgSrc, string msgChannel)
        {
            if (msgSrc == GroupLeaderId)
            {
                GroupJoinFailedMsg failedMsg = (msg as GroupJoinFailedMsg);
                Logger.Info($"{this.GetType().Name}.OnGroupJoinFailed() from boss: {failedMsg.DestGroupId} not joined by {SID(failedMsg.PeerId)} because \"{failedMsg.FailureReason}\" ");

                // Should only come to requestor, and needs to be reported to the Application via gamenet
                if (failedMsg.PeerId == LocalPeerId)
                    ApianInst.OnGroupJoinFailed(failedMsg.PeerId, failedMsg.FailureReason); // inform local apian
            }
        }


        protected virtual void OnGroupMemberStatus(ApianGroupMessage msg, string msgSrc, string msgChannel)
        {
            if (msgSrc == GroupLeaderId || msgSrc == LocalPeerId) // might be engerated locally (removed)
            {
                GroupMemberStatusMsg sMsg = (msg as GroupMemberStatusMsg);
                Logger.Info($"{this.GetType().Name}.OnGroupMemberStatus(): {(sMsg.PeerId==LocalPeerId?"Local:":"Remote")}: {sMsg.PeerId} to {sMsg.MemberStatus}");
                if (Members.ContainsKey(sMsg.PeerId))
                {
                    ApianGroupMember m = Members[sMsg.PeerId];
                    ApianGroupMember.Status old = m.CurStatus;
                    m.CurStatus = sMsg.MemberStatus;
                    ApianInst.OnGroupMemberStatusChange(m, old);

                    if (sMsg.MemberStatus == ApianGroupMember.Status.Removed)
                    {
                        Logger.Info($"{this.GetType().Name}.OnGroupMemberStatus(): Removing Member {sMsg.PeerId}");
                        Members.Remove(sMsg.PeerId);
                    }
                }
                else
                    Logger.Warn($"{this.GetType().Name}.OnGroupMemberStatus(): No action.  {sMsg.PeerId} is not a member." );
            }
        }

        protected void OnGroupSyncRequest(ApianGroupMessage msg, string msgSrc, string msgChannel)
        {
            if (LocalPeerIsLeader) // Only leader handles this
            {
                GroupSyncRequestMsg sMsg = (msg as GroupSyncRequestMsg);
                Logger.Info($"{this.GetType().Name}.OnGroupSyncRequest() from {SID(msgSrc)} start: {sMsg.ExpectedCmdSeqNum} 1st stashed: {sMsg.FirstStashedCmdSeqNum}");

                ApianInst.SendApianMessage(GroupId, new GroupMemberStatusMsg(GroupId, msgSrc, ApianGroupMember.Status.SyncingState));

                // Send out most recent state
                long firstCmdToSend = sMsg.ExpectedCmdSeqNum;

                EpochData state = prevEpochData;
                if (state != null)
                {
                    ApianInst.SendApianMessage(msgSrc, new GroupSyncDataMsg(GroupId, state.TimeStamp, state.EpochNum, state.EndCmdSeqNumber, state.EndStateHash, state.SerializedStateData));
                    firstCmdToSend = state.EndCmdSeqNumber + 1;
                    Logger.Info($"{this.GetType().Name}.OnGroupSyncRequest() Sending checkpoint ending with seq# {state.EndCmdSeqNumber}");
                }

                long firstCmdPeerHas = (sMsg.FirstStashedCmdSeqNum >= 0) ? sMsg.FirstStashedCmdSeqNum : ApianInst.MaxAppliedCmdSeqNum;

                CmdSynchronizer.AddSyncingPeer(msgSrc, firstCmdToSend, firstCmdPeerHas );
            }
        }

        protected void OnGroupSyncData(ApianGroupMessage msg, string msgSrc, string msgChannel)
        {
            GroupSyncDataMsg dMsg = (msg as GroupSyncDataMsg);
            CmdSynchronizer.ApplyCheckpointStateData( dMsg.StateEpoch, dMsg.StateSeqNum, dMsg.StateTimeStamp, dMsg.StateHash, dMsg.StateData);
            InitializeEpochData( dMsg.StateEpoch+1, dMsg.StateSeqNum+1);

        }

        protected void OnGroupSyncCompletionMsg(ApianGroupMessage msg, string msgSrc, string msgChannel)
        {
            if (LocalPeerIsLeader)
            {
                GroupSyncCompletionMsg sMsg = (msg as GroupSyncCompletionMsg);
                Logger.Info($"{this.GetType().Name}.OnGroupSyncCompletionMsg() from {msgSrc} SeqNum: {sMsg.CompletionSeqNum} Hash: {sMsg.CompleteionStateHash}");
                ApianGroupMember m = GetMember(msgSrc);

                if (m.ApianClockSynced)
                    ApianInst.SendApianMessage(GroupId, new GroupMemberStatusMsg(GroupId, msgSrc, ApianGroupMember.Status.Active));
                else
                    ApianInst.SendApianMessage(GroupId, new GroupMemberStatusMsg(GroupId, msgSrc, ApianGroupMember.Status.SyncingClock));
            }
        }

        public override void OnLocalStateCheckpoint( long seqNum, long timeStamp, string stateHash, string serializedState)
        {
            Logger.Verbose($"***** {this.GetType().Name}.OnLocalStateCheckpoint() Checkpoint Seq#: {seqNum}, Hash: {stateHash}");

            StartNewEpoch( seqNum);
            StoreCompletedEpoch(seqNum, timeStamp, stateHash, serializedState);

        }

        protected void OnGroupCheckpointReport(ApianGroupMessage msg, string msgSrc, string msgChannel)
        {
            GroupCheckpointReportMsg rMsg = msg as GroupCheckpointReportMsg;

            string isLocal = msgSrc == LocalPeerId ? "Local" : "Remote";
            Logger.Info($"{this.GetType().Name}.OnGroupCheckpointReport() {isLocal} rpt from {SID(msgSrc)} Epoch: {prevEpochData?.EpochNum} Checkpoint Seq#: {rMsg.SeqNum}, Hash: {rMsg.StateHash}");
            if (LocalPeerIsLeader) // Only leader handles this
            {
               Logger.Info($"{this.GetType().Name}.OnGroupCheckpointReport() Epoch: {prevEpochData?.EpochNum}, Checkpoint Seq#: {rMsg.SeqNum}, Hash: {rMsg.StateHash}");

            }

        }

    }

}