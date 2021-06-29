using System.Linq;
using System;
using System.Collections.Generic;
using UniLog;
using static UniLog.UniLogger; // for SID

namespace Apian
{
    public class CreatorSezGroupManager : ApianGroupManagerBase
    {
        public const string kGroupType = "CreatorSez";
        public override string GroupType {get => kGroupType; }
        public const string kGroupTypeName = "CreatorSez";
        public override string GroupTypeName {get => kGroupTypeName; }


        public const int kAllowedSkippedCommands = 2;
        // This is a bit of a hack to deal with small message ordering issues.
        // If a command is missed, allow a couple more to come in (and get stashed) before requesting a resync
        // just in case it's a simple delivery order issue.
        // Admittedly, this shouldn't happen - but it's easy to account for.

        // ReSharper disable MemberCanBePrivate.Global,UnusedMember.Global,FieldCanBeMadeReadOnly.Global
        public static  Dictionary<string, string> DefaultConfig = new Dictionary<string, string>()
        {
            {"CheckpointMs", "5000"},  // request a checkpoint this often
            {"CheckpointOffsetMs", "50"},  // Use this to get the checkpoint times NOT to be on roudning boundaries
            {"SyncCompletionWaitMs", "2000"}, // wait this long for a sync completion request reply
        };

        protected class EpochData
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

        protected class LeaderOnlyData
        {
            private ApianBase ApianInst {get; }
            public long CurrentEpochNum {get => curEpochData.EpochNum; }
            private long _nextNewCommandSeqNum; // really should ever access this. Creator should use GetNewCommandSequenceNumber()
            public long GetNewCommandSequenceNumber() => _nextNewCommandSeqNum++;

            public EpochData curEpochData;
            public EpochData prevEpochData; // TODO: this needs to be a collection and should be persistent
            public UniLogger Logger;

            public LeaderOnlyData(ApianBase apInst, Dictionary<string,string> config)
            {
                Logger = UniLogger.GetLogger("ApianGroup");    // re-use same logger (put class name in msgs)
                ApianInst = apInst;
                _LoadConfig(config);
                curEpochData = new EpochData(0,0,-1); // -1 means we aren't done yet
            }

            private void _LoadConfig(Dictionary<string,string> configDict)
            {

            }

            // AppState

            public void StartNewEpoch(long lastCmdSeqNum)
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
        }


        public string MainP2pChannel {get => ApianInst.GameNet.CurrentNetworkId();}
        private readonly Dictionary<string, Action<ApianGroupMessage, string, string>> GroupMsgHandlers;

        private ApianGroupSynchronizer CmdSynchronizer;

        // IApianGroupManager

        public bool Intialized {get => GroupInfo != null; }

        // State data
        private long DontRequestSyncBeforeMs; // When waiting for a SyncCompletionRequest reply, this is when it's ok to give up and ask again
        private long NextCheckPointMs;

        // Config params
        Dictionary<string,string> ConfigDict;
        private long SyncCompletionWaitMs; // wait this long for a sync completion request reply  &&&&&& Here? Or in Synchronizer?
        private long CheckpointMs;
        private long CheckpointOffsetMs;

        private LeaderOnlyData LeaderData;
        public bool LocalPeerIsLeader {get => LeaderData != null;}
        private static long SysMsNow => DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond; // FIXME: replace with testable construct

        public CreatorSezGroupManager(ApianBase apianInst, Dictionary<string,string> config=null) : base(apianInst)
        {
            _ParseConfig(config ?? DefaultConfig);
            CmdSynchronizer = new ApianGroupSynchronizer(apianInst, config);
            GroupMsgHandlers = new Dictionary<string, Action<ApianGroupMessage, string, string>>() {
                {ApianGroupMessage.GroupsRequest, OnGroupsRequest },
                {ApianGroupMessage.GroupJoinRequest, OnGroupJoinRequest },
                {ApianGroupMessage.GroupLeaveRequest, OnGroupLeaveRequest },
                {ApianGroupMessage.GroupMemberJoined, OnGroupMemberJoined },
                {ApianGroupMessage.GroupMemberStatus, OnGroupMemberStatus },
                {ApianGroupMessage.GroupSyncRequest, OnGroupSyncRequest },
                {ApianGroupMessage.GroupSyncData, OnGroupSyncData },
                {ApianGroupMessage.GroupSyncCompletion, OnGroupSyncCompletionMsg },
                {ApianGroupMessage.GroupCheckpointReport, OnGroupCheckpointReport },
             };
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

            if (!info.GroupType.Equals(GroupType))
                Logger.Error($"SetupNewGroup(): incorrect GroupType: {info.GroupType} in info. SHould be: GroupType");

            // Creating a new groupIfop with us as creator
            GroupInfo = info;
            LeaderData = new LeaderOnlyData(ApianInst, ConfigDict);   // since we're leader we need leaderdata
            ApianInst.ApianClock.Set(0); // we're the group leader so we need to start our clock
            NextCheckPointMs = CheckpointMs + CheckpointOffsetMs; // another leader thing
        }

        public override void SetupExistingGroup(ApianGroupInfo info)
        {
            Logger.Info($"{this.GetType().Name}.SetGroupInfo(): {info.GroupId}");
            GroupInfo = info;
        }

        public override void JoinGroup(string localMemberJson)
        {
            if (GroupInfo == null)
                Logger.Error($"GroupMgr.JoinGroup() - group uninitialized."); // TODO: once again - this should probably throw.

            Logger.Info($"{this.GetType().Name}.JoinGroup(): {GroupInfo?.GroupId}");
            // Because of the group type send the request directly to the creator
            ApianInst.GameNet.SendApianMessage(GroupCreatorId, new GroupJoinRequestMsg(GroupId, LocalPeerId, localMemberJson));
        }
        public override void LeaveGroup()
        {
            // Question... do we REALLY want to send a request and wait? My guess is not - apps will will send the request
            // and then just proceed to shut down the group locally. Th rest of the group can deal with the message
            Logger.Info($"{this.GetType().Name}.LeaveGroup(): {GroupInfo?.GroupId}");

            ApianInst.GameNet.SendApianMessage(GroupCreatorId, new GroupLeaveRequestMsg(GroupId, LocalPeerId));

            // ApianInst.OnGroupMemberStatusChange(LocalMember, ApianGroupMember.Status.Removed); If we wait this is what'll eventually happen.
            // Maybe we should just do it here? Nah.
        }

        private ApianGroupMember _AddMember(string peerId, string appDataJson)
        {
            Logger.Info($"{this.GetType().Name}._AddMember(): ({(peerId==LocalPeerId?"Local":"Remote")}) {peerId}");
            ApianGroupMember newMember =  new ApianGroupMember(peerId, appDataJson);
            newMember.CurStatus = ApianGroupMember.Status.Joining;
            Members[peerId] = newMember;
            if (peerId==LocalPeerId)
                LocalMember = newMember;
            return newMember;
        }

        public override void Update()
        {
            if (LocalPeerIsLeader)
                _LeaderUpdate();

            if (CmdSynchronizer?.ApplyStashedCommands() == true) // always tick this. returns true if we were behind and calling it caught us up.
            {
                if (LocalMember.CurStatus == ApianGroupMember.Status.Syncing && SysMsNow > DontRequestSyncBeforeMs)
                {
                    Logger.Info($"{this.GetType().Name}.Update(): Sending SyncCompletion request.");
                    ApianInst.SendApianMessage(GroupCreatorId, new GroupSyncCompletionMsg(GroupId, ApianInst.MaxAppliedCmdSeqNum, "hash"));
                    DontRequestSyncBeforeMs = SysMsNow + SyncCompletionWaitMs; // ugly "wait a bit before doing it again" timer
                }
            }
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
                if (apMs > NextCheckPointMs)
                {
                    _SendCheckpointCommand(apMs);
                    NextCheckPointMs += CheckpointMs;
                }
            }
        }

        private void _SendCheckpointCommand(long curApainMs)
        {
            ApianCheckpointMsg cpMsg = new ApianCheckpointMsg(NextCheckPointMs);
            ApianCommand cpCmd = new ApianCommand( LeaderData.CurrentEpochNum, LeaderData.GetNewCommandSequenceNumber(), GroupId, cpMsg);
            Logger.Info($"{this.GetType().Name}._SendCheckpointCommand() SeqNum: {cpCmd.SequenceNum}, Timestamp: {NextCheckPointMs} at {curApainMs}");
            ApianInst.SendApianMessage(GroupId, cpCmd);
            LeaderData.StartNewEpoch(cpCmd.SequenceNum); // sending out a checkpoint cmds ends the current epoch
            // but the prev epoch's data can;t be filled in until the local peer's response comes back.
        }

        public override void OnApianMessage(ApianMessage msg, string msgSrc, string msgChannel)
        {
            // TODO: rename this to OnApianGroupMessage()
            // Note that Apian only routes GROUP messages here.
            // Commands are also sent when received - but only via EvaluateCommand()
            if (msg != null && msg.MsgType == ApianMessage.GroupMessage)
            {
                ApianGroupMessage gMsg = msg as ApianGroupMessage;
                GroupMsgHandlers[gMsg.GroupMsgType](gMsg, msgSrc, msgChannel);
            }
            else
                Logger.Warn($"OnApianMessage(): unexpected ApianMsg Type: {msg?.MsgType}");
        }

        public override void OnApianRequest(ApianRequest msg, string msgSrc, string msgChan)
        {
            // Requests are assumed to be valid as long as source is Active
            if (LocalPeerIsLeader && GetMember(msgSrc)?.CurStatus == ApianGroupMember.Status.Active)
            {
                Logger.Debug($"OnApianRequest(): upgrading {msg.CoreMsgType} from {SID(msgSrc)} to Command");
                ApianInst.GameNet.SendApianMessage(msgChan, new ApianCommand(LeaderData.CurrentEpochNum, LeaderData.GetNewCommandSequenceNumber(), msg));
            }
        }

        public override void OnApianObservation(ApianObservation msg, string msgSrc, string msgChan)
        {
            // Observations from the leader are turned into commands by the leader
            if (LocalPeerIsLeader && (msgSrc == LocalPeerId))
                ApianInst.GameNet.SendApianMessage(msgChan, new ApianCommand(LeaderData.CurrentEpochNum, LeaderData.GetNewCommandSequenceNumber(), msg));
        }

        public override ApianCommandStatus EvaluateCommand(ApianCommand msg, string msgSrc, long maxAppliedCmdNum)
        {
            // Too early
            if (LocalMember == null)
                return ApianCommandStatus.kLocalPeerNotReady;


            // Valid if from the creator
            if (msgSrc != GroupCreatorId)
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
                // TODO: This is how we enter Sync mode. Maybe it should be more explicit?
                if (LocalMember.CurStatus == ApianGroupMember.Status.Joining)
                    _RequestSync(msg.SequenceNum, maxAppliedCmdNum);
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
            ApianInst.SendApianMessage(GroupCreatorId, syncRequest);

            // the local short-circuit...
            ApianGroupMember.Status prevStatus = LocalMember.CurStatus;
            LocalMember.CurStatus = ApianGroupMember.Status.Syncing;
            ApianInst.OnGroupMemberStatusChange(LocalMember, prevStatus);
            // when creator gets the sync request it'll broadcast an identical status change msg
        }

        protected void OnGroupsRequest(ApianGroupMessage msg, string msgSrc, string msgChannel)
        {
            // Only the creator answers
            if (LocalPeerIsLeader)
            {
                Logger.Info($"{this.GetType().Name}.OnGroupsRequest() Got GroupsRequest, sending response.");
                GroupAnnounceMsg amsg = new GroupAnnounceMsg(GroupInfo);
                ApianInst.SendApianMessage(msgSrc, amsg);
            }
        }

        protected void OnGroupJoinRequest(ApianGroupMessage msg, string msgSrc, string msgChannel)
        {
            // In this implementation the creator decides
            // Everyone else just ignores this.
            if (LocalPeerIsLeader)
            {
                GroupJoinRequestMsg jreq = msg as GroupJoinRequestMsg;
                Logger.Info($"{this.GetType().Name}.OnGroupJoinRequest(): Affirming {jreq.DestGroupId} from {jreq.PeerId}");

                // Send current members to new joinee - do it bfore sending the new peers join msg
                _SendMemberJoinedMessages(jreq.PeerId);

                // Just approve. Don't add (happens in OnGroupMemberJoined())
               GroupMemberJoinedMsg jmsg = new GroupMemberJoinedMsg(GroupId, jreq.PeerId, jreq.ApianClientPeerJson);
                ApianInst.SendApianMessage(GroupId, jmsg); // tell everyone about the new kid last

                // Now send status updates (from "joined") for any member that has changed status
                _SendMemberStatusUpdates(jreq.PeerId);

            }
        }

        protected void OnGroupLeaveRequest(ApianGroupMessage msg, string msgSrc, string msgChannel)
        {
            // In this implementation the creator decides
            // Everyone else just ignores this.
            if (LocalPeerIsLeader)
            {
                GroupLeaveRequestMsg jreq = msg as GroupLeaveRequestMsg;
                Logger.Info($"{this.GetType().Name}.OnGroupLeaveRequest()");

                // Just remove.
                ApianInst.SendApianMessage(GroupId, new GroupMemberStatusMsg(GroupId, jreq.PeerId,ApianGroupMember.Status.Removed));
            }
        }

        protected void _SendMemberJoinedMessages(string toWhom)
        {
            // Send a newly-approved member all of the Join messages for the other member
            // Create messages first, then send (mostly so Members doesn;t get modified while enumerating it)
            List<GroupMemberJoinedMsg> joinMsgs = Members.Values
                .Where( m => m.PeerId != toWhom)
                .Select( (m) =>  new GroupMemberJoinedMsg(GroupId, m.PeerId, m.AppDataJson)).ToList();
            foreach (RatfishMemberJoinedMsg msg in joinMsgs)
                ApianInst.SendApianMessage(toWhom, msg);
        }

        protected void _SendMemberStatusUpdates(string toWhom)
        {
            // Send a newly-approved member the status of every non-"Joining" member (since a joinmessage was already sent)
            List<GroupMemberStatusMsg> statusMsgs = Members.Values
                .Where( (m) => m.CurStatus != ApianGroupMember.Status.Joining && m.PeerId != toWhom)
                .Select( (m) =>  new GroupMemberStatusMsg(GroupId, m.PeerId, m.CurStatus)).ToList();
            foreach (GroupMemberStatusMsg msg in statusMsgs)
                ApianInst.SendApianMessage(toWhom, msg);
        }


            protected void OnGroupMemberJoined(ApianGroupMessage msg, string msgSrc, string msgChannel)
        {
            // If from GroupCreator then it's valid
            if (msgSrc == GroupCreatorId)
            {
                RatfishMemberJoinedMsg joinedMsg = (msg as RatfishMemberJoinedMsg);
                Logger.Info($"{this.GetType().Name}.OnGroupMemberJoined() from boss:  {joinedMsg.DestGroupId} adds {joinedMsg.PeerId}");

                ApianGroupMember m = _AddMember(joinedMsg.PeerId, joinedMsg.ApianClientPeerJson);

                ApianInst.OnGroupMemberJoined(m); // inform local apian

                // Is this OUR join message?
                if (joinedMsg.PeerId == LocalPeerId)
                {
                    if  (LocalPeerIsLeader) // are we the leader?
                    {
                        // Yes, Which means we're also the first. Declare  *us* "Active" and tell everyone
                        ApianInst.SendApianMessage(GroupId, new GroupMemberStatusMsg(GroupId, LocalPeerId, ApianGroupMember.Status.Active));
                    }
                }
            }
        }

        protected void OnGroupMemberStatus(ApianGroupMessage msg, string msgSrc, string msgChannel)
        {
            if (msgSrc == GroupCreatorId) // If from GroupCreator then it's valid
            {
                GroupMemberStatusMsg sMsg = (msg as GroupMemberStatusMsg);
                Logger.Info($"{this.GetType().Name}.OnGroupMemberStatus(): {(sMsg.PeerId==LocalPeerId?"Local:":"Remote")}: {sMsg.PeerId} to {sMsg.MemberStatus}");
                if (Members.Keys.Contains(sMsg.PeerId))
                {
                    ApianGroupMember m = Members[sMsg.PeerId];
                    ApianGroupMember.Status old = m.CurStatus;
                    m.CurStatus = sMsg.MemberStatus;
                    ApianInst.OnGroupMemberStatusChange(m, old);
                }
                else
                    Logger.Warn($"{this.GetType().Name}.OnGroupMemberStatus(): Member {sMsg.PeerId} not present" );
            }
        }

        protected void OnGroupSyncRequest(ApianGroupMessage msg, string msgSrc, string msgChannel)
        {
            if (LocalPeerIsLeader) // Only leader handles this
            {
                GroupSyncRequestMsg sMsg = (msg as GroupSyncRequestMsg);
                Logger.Info($"{this.GetType().Name}.OnGroupSyncRequest() from {msgSrc} start: {sMsg.ExpectedCmdSeqNum} 1st stashed: {sMsg.FirstStashedCmdSeqNum}");

                ApianInst.SendApianMessage(GroupId, new GroupMemberStatusMsg(GroupId, msgSrc, ApianGroupMember.Status.Syncing));

                // Send out most recent state
                long firstCmdToSend = sMsg.ExpectedCmdSeqNum;

                EpochData state = LeaderData.prevEpochData;
                if (state != null)
                {
                    ApianInst.SendApianMessage(msgSrc, new GroupSyncDataMsg(GroupId, state.TimeStamp, state.EpochNum, state.EndCmdSeqNumber, state.EndStateHash, state.SerializedStateData));
                    firstCmdToSend = state.EndCmdSeqNumber + 1;
                    Logger.Info($"{this.GetType().Name}.OnGroupSyncRequest() Sending checkpoint ending with seq# {state.EndCmdSeqNumber}");
                }
                CmdSynchronizer.AddSyncingPeer(msgSrc, firstCmdToSend, sMsg.FirstStashedCmdSeqNum );
            }
        }

        protected void OnGroupSyncData(ApianGroupMessage msg, string msgSrc, string msgChannel)
        {
            GroupSyncDataMsg dMsg = (msg as GroupSyncDataMsg);
            CmdSynchronizer.ApplyCheckpointStateData( dMsg.StateEpoch, dMsg.StateSeqNum, dMsg.StateTimeStamp, dMsg.StateHash, dMsg.StateData);
        }

        protected void OnGroupSyncCompletionMsg(ApianGroupMessage msg, string msgSrc, string msgChannel)
        {
            if (LocalPeerIsLeader) // Only creator handles this
            {
                GroupSyncCompletionMsg sMsg = (msg as GroupSyncCompletionMsg);
                Logger.Info($"{this.GetType().Name}.OnGroupSyncCompletionMsg() from {msgSrc} SeqNum: {sMsg.CompletionSeqNum} Hash: {sMsg.CompleteionStateHash}");
                ApianInst.SendApianMessage(GroupId, new GroupMemberStatusMsg(GroupId, msgSrc, ApianGroupMember.Status.Active));
            }
        }

        public override void OnLocalStateCheckpoint( long seqNum, long timeStamp, string stateHash, string serializedState)
        {
            Logger.Verbose($"***** {this.GetType().Name}.OnLocalStateCheckpoint() Checkpoint Seq#: {seqNum}, Hash: {stateHash}");
            if (LocalPeerIsLeader) // Only leader handles this
            {
                // Note that epoch is not in the params. AppCore doesn't know about epochs
                LeaderData.StoreCompletedEpoch(seqNum, timeStamp, stateHash, serializedState);

                // TODO: dispatch any incoming messages that were waiting for the prev epoch data

                // TODO: decide what to do here. Probably need to build command tries and
                // a serialized command list and save it with the epoch
                // Toss out the old stashed commands
                // TODO:  Hmm. Or maybe not. Seems like there is a need for a persistent record.
                //CommandStash = CommandStash.Values.Where( c => c.SequenceNum > seqNum).ToDictionary(c => c.SequenceNum);
            }
        }

        protected void OnGroupCheckpointReport(ApianGroupMessage msg, string msgSrc, string msgChannel)
        {
            GroupCheckpointReportMsg rMsg = msg as GroupCheckpointReportMsg;

            string isLocal = msgSrc == LocalPeerId ? "Local" : "Remote";
            Logger.Info($"{this.GetType().Name}.OnGroupCheckpointReport() {isLocal} rpt from {SID(msgSrc)} Checkpoint Seq#: {rMsg.SeqNum}, Hash: {rMsg.StateHash}");
            if (LocalPeerIsLeader) // Only leader handles this
            {
               Logger.Info($"{this.GetType().Name}.OnGroupCheckpointReport() Epoch: {LeaderData.prevEpochData.EpochNum}, Checkpoint Seq#: {rMsg.SeqNum}, Hash: {rMsg.StateHash}");


                // TODO: use this to report hashes to check the "quality" of a save state
                // (OTOH: since by definition the leader's version is true, it's probably not a big deal)
                // LeaderData.HandleRemoteState(rMsg.SeqNum, rMsg.StateHash);
            }

        }

        public override ApianMessage DeserializeApianMessage(ApianMessage genMsg, string msgJSON)
        {
            // genMsg is the result of generic ApianMessage.Deser()
            return RatfishMessageDeserializer.FromJSON(genMsg, msgJSON) ?? null;
        }

    }

}