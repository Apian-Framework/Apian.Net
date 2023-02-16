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


        // Things only the leader uses. (Used to be in leaderdata but that became a mess)
        private long _nextNewCommandSeqNum; // really should ever access this. Leader should use GetNewCommandSequenceNumber()
        public long GetNewCommandSequenceNumber() => _nextNewCommandSeqNum++;

        protected void SetNextNewCommandSequenceNumber(long newCommandSequenceNumber) {_nextNewCommandSeqNum = newCommandSequenceNumber;}

        protected ApianGroupSynchronizer CmdSynchronizer; // used by both source and dest peers for sync

        // IApianGroupManager
        public bool Intialized {get => GroupInfo != null; }

        // State data
        protected long DontRequestSyncBeforeMs; // When waiting for a SyncCompletionRequest reply, this is when it's ok to give up and ask again
        protected long NextCheckPointMs; // when to ask for the next. UpdateNextCheckPointMs() sets it safely

        // Join params
        protected string LocalMemberJoinData { get; set; }

        // Config params
        protected Dictionary<string,string> ConfigDict;
        protected long SyncCompletionWaitMs; // wait this long for a sync completion request reply  &&&&&& Here? Or in Synchronizer?
        protected long CheckpointMs; // how often
        protected long CheckpointOffsetMs; // small random offset

        public string GroupLeaderAddr {get; set;}
        public bool LocalPeerIsLeader {get => GroupLeaderAddr == LocalPeerAddr;}

        protected static long SysMsNow => DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond; // FIXME: replace with testable construct

        public LeaderDecidesGmBase(ApianBase apianInst, Dictionary<string,string> config=null) : base(apianInst)
        {
            config = config ?? DefaultConfig;
            _ParseConfig(config);
            CmdSynchronizer = new ApianGroupSynchronizer(apianInst, config);
            GroupMsgHandlers = new Dictionary<string, Action<ApianGroupMessage, string, string>>() {
                {ApianGroupMessage.GroupsRequest, OnGroupsRequest },
                {ApianGroupMessage.GroupJoinRequest, OnGroupJoinRequest },
                {ApianGroupMessage.GroupJoinFailed, OnGroupJoinFailed },
                {ApianGroupMessage.GroupMemberJoined, OnGroupMemberJoined },
                {ApianGroupMessage.GroupMemberLeft, OnGroupMemberLeftMsg },
                {ApianGroupMessage.GroupMemberStatus, OnGroupMemberStatus },
                {ApianGroupMessage.GroupSyncRequest, OnGroupSyncRequest },
                {ApianGroupMessage.GroupSyncData, OnGroupSyncData },
                {ApianGroupMessage.GroupSyncCompletion, OnGroupSyncCompletionMsg },
                {ApianGroupMessage.GroupCheckpointReport, OnGroupCheckpointReport },
            };

            // Add to default handlers
            GroupCoreCmdHandlers[GroupCoreMessage.CheckpointRequest] =  OnCheckpointRequestCmd;
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

            SetLeader(info.GroupCreatorAddr); // creater starts out as leader

            // Do this *after* we are a member
            //ApianInst.ApianClock.SetTime(0); // we're the group leader so we need to start our clock
            //NextCheckPointMs = CheckpointMs + CheckpointOffsetMs; // another leader thing
        }

        public override void SetupExistingGroup(ApianGroupInfo info)
        {
            Logger.Info($"{this.GetType().Name}.SetGroupInfo(): {info.GroupFriendlyId}");
            GroupInfo = info;
            SetLeader( info.GroupCreatorAddr ); // creator starts as leader
        }

        public override void JoinGroup(string localMemberJson, bool joinAsValidator)
        {
            if (localMemberJson == null)
                localMemberJson = LocalMemberJoinData;
            else
                LocalMemberJoinData = localMemberJson;

            if (GroupInfo == null)
                Logger.Error($"GroupMgr.JoinGroup() - group uninitialized."); // TODO: once again - this should probably throw.

            Logger.Info($"{this.GetType().Name}.JoinGroup(): {GroupInfo?.GroupId} Sending join request to group");
            // Send the request to the entire group (we might not know the leader yet)
            ApianInst.GameNet.SendApianMessage(GroupId, new GroupJoinRequestMsg(GroupId, LocalPeerAddr, localMemberJson, joinAsValidator));
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
                    ApianInst.SendApianMessage(GroupLeaderAddr, new GroupSyncCompletionMsg(GroupId, ApianInst.MaxAppliedCmdSeqNum, "hash"));
                    DontRequestSyncBeforeMs = SysMsNow + SyncCompletionWaitMs; // ugly "wait a bit before doing it again" timer
                }
            }
        }

        //
        // Leader stuff
        //

        protected virtual void SetLeader(string newLeaderAddr)
        {
            Logger.Info($"{this.GetType().Name}.SetLeader() - setting group leader to {SID(newLeaderAddr)}");

            if ( newLeaderAddr != GroupLeaderAddr)
                ApianInst.GameNet.OnNewGroupLeader(GroupId, newLeaderAddr, GetMember(newLeaderAddr));

            GroupLeaderAddr = newLeaderAddr;

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
            ApianCommand cpCmd = new ApianCommand( ApianInst.CurrentEpochNum, GetNewCommandSequenceNumber(), GroupId, cpMsg);
            Logger.Info($"{this.GetType().Name}._SendCheckpointCommand() SeqNum: {cpCmd.SequenceNum}, Timestamp: {checkPointMs} (current time: {curApainMs})");
            ApianInst.SendApianMessage(GroupId, cpCmd);

            // Only the leader formerly kept track of epochs, and started a new one here.

            //StartNewEpoch(cpCmd.SequenceNum); // sending out a checkpoint cmds ends the current epoch for the leader
            // othe rmembers do it upon receipt.  TODO: should everyone do it theN?

            // Also: the prev epoch's data can;t be filled in until the local peer's response comes back. WHich suggests that even
            // the leader should end the epoch when it has serialized the state is and is about to send the checkpoint reply
        }


        // public void StartNewEpoch(long lastCmdSeqNum, long checkpointTimeStamp, string stateHash, string serializedState)
        // {
        //     // Checkpoint has been done for the current epoch.
        //     prevEpochData = curEpochData;

        //     // this is where CurEpochNum gets incremented
        //     //curEpochData = new EpochData(prevEpochData.EpochNum+1, lastCmdSeqNum+1, checkpointTimeStamp);

        //     prevEpochData.CloseEpoch(lastCmdSeqNum, checkpointTimeStamp, stateHash, serializedState);


        //     // TODO: when this returns, the caller needs to dispatch any ApianGroupMsgs waiting for it (sync requests)
        // }


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

        public override void OnApianClockOffset(string peerAddr, long ApianClockOffset)
        {
            // If this comes in from a peer who is Syncing the clock, and we are the leader, make it Active
            base.OnApianClockOffset(peerAddr, ApianClockOffset);

            if (LocalPeerIsLeader && GetMember(peerAddr)?.CurStatus == ApianGroupMember.Status.SyncingClock)
            {
                Logger.Debug($"OnApianClockOffset(): Setting peer {SID(peerAddr)} to Active");
                ApianInst.SendApianMessage(GroupId, new GroupMemberStatusMsg(GroupId, peerAddr, ApianGroupMember.Status.Active));
           }

        }

        public override void OnApianGroupMessage(ApianGroupMessage msg, string msgSrc, string msgChannel)
        {
            // Note that Apian only routes GROUP messages here.
            if (msg != null )
            {
                try {
                    GroupMsgHandlers[msg.GroupMsgType](msg, msgSrc, msgChannel);
                } catch (NullReferenceException ex) {
                    Logger.Error($"OnApianGroupMessage(): No GroupMsg handler for: '{msg.GroupMsgType}'");
                    throw(ex);
                }
            }
        }

        public override void OnApianRequest(ApianRequest msg, string msgSrc, string msgChan)
        {
            // Requests are assumed to be valid as long as source is Active
            if (LocalPeerIsLeader && GetMember(msgSrc)?.CurStatus == ApianGroupMember.Status.Active)
            {
                if (GetMember(msgSrc).IsValidator)
                {
                    Logger.Warn($"OnApianRequest(): Ignoring ApianRequest({msg.PayloadMsgType}) from VALIDATOR {SID(msgSrc)}");
                } else {
                    Logger.Debug($"OnApianRequest(): upgrading {msg.PayloadMsgType} from {SID(msgSrc)} to Command");
                    ApianInst.GameNet.SendApianMessage(msgChan, new ApianCommand(ApianInst.CurrentEpochNum, GetNewCommandSequenceNumber(), msg));
                }
            }
        }

        public override void OnApianObservation(ApianObservation msg, string msgSrc, string msgChan)
        {
            // Observations from the leader are turned into commands by the leader
            if (LocalPeerIsLeader && (msgSrc == LocalPeerAddr))
                ApianInst.GameNet.SendApianMessage(msgChan, new ApianCommand(ApianInst.CurrentEpochNum, GetNewCommandSequenceNumber(), msg));
        }

        public override ApianCommandStatus EvaluateCommand(ApianCommand msg, string msgSrc, long maxAppliedCmdNum)
        {
            // Too early
            if (LocalMember == null)
                return ApianCommandStatus.kLocalPeerNotReady;

            if (msgSrc != GroupLeaderAddr)
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
                        RequestSync(msg.SequenceNum, maxAppliedCmdNum);
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

        protected void RequestSync(long firstStashedSeqNum, long maxAppliedCmdNum)
        {
            // Here we actually set our own status and short-circuit the MemberStatusMsg
            // process - we WILL recieve that message in a bit, but in the meantime we want to
            // stop processing incoming commands and stash them instead.

            long firstSeqNumWeNeed = maxAppliedCmdNum + 1;  // Since the variable inits to -1, this is 0 if we haven't applied any

            GroupSyncRequestMsg syncRequest = new GroupSyncRequestMsg(GroupId, firstSeqNumWeNeed, firstStashedSeqNum);
            Logger.Info($"{this.GetType().Name}._RequestSync() sending req: start: {syncRequest.ExpectedCmdSeqNum} 1st Stashed: {syncRequest.FirstStashedCmdSeqNum}");
            ApianInst.SendApianMessage(GroupLeaderAddr, syncRequest);

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
            // TODO: This should be rethought. Not clear that it should only be the leader handling this,
            // And alos not clear it needs to be handled at the groupMgr level
            if (LocalPeerIsLeader)
            {
                // GroupRequestMsg doesn't really have any data.

                Logger.Info($"{this.GetType().Name}.OnGroupsRequest() Got GroupsRequest from {SID(msgSrc)}, sending announcement.");
                GroupAnnounceMsg amsg = new GroupAnnounceMsg(GroupInfo, ApianInst.CurrentGroupStatus());
                ApianInst.SendApianMessage(msgChannel, amsg); // broadcast
            }
        }

        protected virtual void OnGroupJoinRequest(ApianGroupMessage msg, string msgSrc, string msgChannel)
        {
            // In this implementation the Leader decides
            // Everyone else just ignores this.
            if (LocalPeerIsLeader)
            {
                GroupJoinRequestMsg jreq = msg as GroupJoinRequestMsg;

                Logger.Info($"{this.GetType().Name}.OnGroupJoinRequest(): Affirming {jreq.DestGroupId} req from {SID(jreq.PeerAddr)}");

                // Send current members to new joinee - do it bfore sending the new peers join msg
                SendMemberJoinedMessages(jreq.PeerAddr);

                // Don't add (happens in OnGroupMemberJoined())
                GroupMemberJoinedMsg jmsg = new GroupMemberJoinedMsg(GroupId, jreq.PeerAddr, jreq.ApianClientPeerJson, jreq.JoinAsValidator);
                ApianInst.SendApianMessage(GroupId, jmsg); // tell everyone about the new kid last

                // Now send status updates (from "joined") for any member that has changed status
                SendMemberStatusUpdates(jreq.PeerAddr);
            }
        }

        protected void SendMemberJoinedMessages(string toWhom)
        {
            // Send a newly-approved member all of the Join messages for the other member
            // Create messages first, then send (mostly so Members doesn;t get modified while enumerating it)
            List<GroupMemberJoinedMsg> joinMsgs = Members.Values
                .Where( m => m.PeerAddr != toWhom)
                .Select( (m) =>  new GroupMemberJoinedMsg(GroupId, m.PeerAddr, m.AppDataJson, m.IsValidator)).ToList();
            foreach (GroupMemberJoinedMsg msg in joinMsgs)
                ApianInst.SendApianMessage(toWhom, msg);
        }

        protected void SendMemberStatusUpdates(string toWhom)
        {
            // Send a newly-approved member the status of every non-"Joining" member (since a joinmessage was already sent)
            List<GroupMemberStatusMsg> statusMsgs = Members.Values
                .Where( (m) => m.CurStatus != ApianGroupMember.Status.Joining && m.PeerAddr != toWhom)
                .Select( (m) =>  new GroupMemberStatusMsg(GroupId, m.PeerAddr, m.CurStatus)).ToList();
            foreach (GroupMemberStatusMsg msg in statusMsgs)
                ApianInst.SendApianMessage(toWhom, msg);
        }


        protected virtual void OnGroupMemberJoined(ApianGroupMessage msg, string msgSrc, string msgChannel)
        {
            if (msgSrc == GroupLeaderAddr)
            {
                GroupMemberJoinedMsg joinedMsg = (msg as GroupMemberJoinedMsg);
                Logger.Info($"{this.GetType().Name}.OnGroupMemberJoined() from boss:  {joinedMsg.DestGroupId} adds {SID(joinedMsg.PeerAddr)}");

                ApianGroupMember m = _AddMember(joinedMsg.PeerAddr, joinedMsg.ApianClientPeerJson, joinedMsg.JoinedAsValidator);

                ApianInst.OnGroupMemberJoined(m); // inform local apian

                // Is this OUR join message?
                if (joinedMsg.PeerAddr == LocalPeerAddr)
                {
                    if  (LocalPeerIsLeader) // are we the leader?
                    {
                        // Now that we are joined, start the clock
                        ApianInst.ApianClock.SetTime(0); // we're the group leader so we need to start our clock
                        NextCheckPointMs = CheckpointMs + CheckpointOffsetMs; // another leader thing

                        // Yes, Which means we're also the first. Declare  *us* "Active" and tell everyone
                        ApianInst.SendApianMessage(GroupId, new GroupMemberStatusMsg(GroupId, LocalPeerAddr, ApianGroupMember.Status.Active));
                    } else {
                        // Not leader - request data sync.
                        RequestSync(-1, -1); // we've neither applied nor stashed any commands
                    }
                }
            }
        }

        protected virtual void OnGroupMemberLeftMsg(ApianGroupMessage msg, string msgSrc, string msgChannel)
        {
            if (msgSrc == GroupLeaderAddr)
            {
                GroupMemberLeftMsg leftMsg = (msg as GroupMemberLeftMsg);
                Logger.Warn($"{this.GetType().Name}.OnGroupMemberLeft()  {SID(leftMsg.PeerAddr)}");

                ApianGroupMember m = GetMember( leftMsg.PeerAddr );
                if (m != null)
                {
                    ApianInst.OnGroupMemberLeft(m); // inform local apian
                    Members.Remove(leftMsg.PeerAddr);
                }
            }
        }

        protected void OnGroupJoinFailed(ApianGroupMessage msg, string msgSrc, string msgChannel)
        {
            if (msgSrc == GroupLeaderAddr)
            {
                GroupJoinFailedMsg failedMsg = (msg as GroupJoinFailedMsg);
                Logger.Info($"{this.GetType().Name}.OnGroupJoinFailed() from boss: {failedMsg.DestGroupId} not joined by {SID(failedMsg.PeerAddr)} because \"{failedMsg.FailureReason}\" ");

                // Should only come to requestor, and needs to be reported to the Application via gamenet
                if (failedMsg.PeerAddr == LocalPeerAddr)
                    ApianInst.OnGroupJoinFailed(failedMsg.PeerAddr, failedMsg.FailureReason); // inform local apian
            }
        }


        protected virtual void OnGroupMemberStatus(ApianGroupMessage msg, string msgSrc, string msgChannel)
        {
            if (msgSrc == GroupLeaderAddr || msgSrc == LocalPeerAddr) // might be engerated locally (removed)
            {
                GroupMemberStatusMsg sMsg = (msg as GroupMemberStatusMsg);
                Logger.Info($"{this.GetType().Name}.OnGroupMemberStatus(): {(sMsg.PeerAddr==LocalPeerAddr?"Local:":"Remote")}: {sMsg.PeerAddr} to {sMsg.MemberStatus}");
                if (Members.ContainsKey(sMsg.PeerAddr))
                {
                    ApianGroupMember m = Members[sMsg.PeerAddr];
                    ApianGroupMember.Status old = m.CurStatus;
                    m.CurStatus = sMsg.MemberStatus;
                    ApianInst.OnGroupMemberStatusChange(m, old);
                    Logger.Info($"{this.GetType().Name}.OnGroupMemberStatus(): MemberCount: {ActiveMemberCount}");

                }
                else
                    Logger.Warn($"{this.GetType().Name}.OnGroupMemberStatus(): No action.  {sMsg.PeerAddr} is not a member." );
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

                ApianEpoch state = ApianInst.PreviousEpoch;
                if (state != null)
                {
                    ApianInst.SendApianMessage(msgSrc, new GroupSyncDataMsg(GroupId, state.StartTimeStamp, state.EpochNum, state.EndCmdSeqNumber, state.EndStateHash, state.SerializedStateData));
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
            ApianInst.InitializeEpochData( dMsg.StateEpoch+1, dMsg.StateSeqNum+1, dMsg.StateTimeStamp, dMsg.StateHash );
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

        protected void OnGroupCheckpointReport(ApianGroupMessage msg, string msgSrc, string msgChannel)
        {
            GroupCheckpointReportMsg rMsg = msg as GroupCheckpointReportMsg;

            string isLocal = msgSrc == LocalPeerAddr ? "Local " : "Remote"; // extra space so console messages line up
            string role = GetMember(msgSrc).IsValidator ? "V" : "P";

            string recovAddr = ApianInst.GameNet.EncodeUTF8AndEcRecover(rMsg.StateHash, rMsg.HashSignature);

            Logger.Info($"{this.GetType().Name}.OnGroupCheckpointReport() {isLocal} rpt from {SID(msgSrc)} ({role}) Epoch: {rMsg?.Epoch} Checkpoint Seq#: {rMsg.SeqNum}, Hash: {rMsg.StateHash}");

            if (recovAddr.ToUpper() != rMsg.PeerAddr.ToUpper() )
               Logger.Warn($"{this.GetType().Name}.OnGroupCheckpointReport(): invalid checkpoint signature. Not from {rMsg.PeerAddr}");

            if (LocalPeerIsLeader) // Only leader handles this
            {
               Logger.Info($"{this.GetType().Name}.OnGroupCheckpointReport() Epoch: {rMsg?.Epoch}, Checkpoint Seq#: {rMsg.SeqNum}, Hash: {rMsg.StateHash}");
            }

        }

    }

}