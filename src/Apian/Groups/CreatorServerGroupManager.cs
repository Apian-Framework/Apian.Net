using System.Xml.Linq;
using System.Runtime.InteropServices;
using System.Linq;
using System.Net.Http;
using System;
using System.Collections.Generic;
using UniLog;
namespace Apian
{

    public class CreatorServerGroupManager : ApianGroupManagerBase, IApianGroupManager
    {
        // ReSharper disable MemberCanBePrivate.Global,UnusedMember.Global,FieldCanBeMadeReadOnly.Global
        public static  Dictionary<string, string> DefaultConfig = new Dictionary<string, string>()
        {
            {"CheckpointMs", "5000"},  // request a checkpoint this often
            {"CheckpointOffsetMs", "50"},  // Use this to get the checkpoint times NOT to be on roudning boundaries
            {"SyncCompletionWaitMs", "2000"}, // wait this long for a sync completion request reply
            {"StashedCommandsPerUpdate", "10"},
            {"Server.MaxSyncCmdsPerUpdate", "10"},
        };

        protected class SyncingPeerData
        {
            public string peerId;
            public long nextCommandToSend;
            public long firstCommandPeerHas;

            public SyncingPeerData(string pid, long firstNeeded, long firstPeerHas)
            {
                peerId = pid;
                nextCommandToSend = firstNeeded;
                firstCommandPeerHas = firstPeerHas;
            }
        }

        protected class AppStateData
        {
            public long SequenceNumber;
            public long TimeStamp;
            public string StateHash;
            public string SerializedStateData;
            // TODO: add this dict and make the stashedStateData member of ServerOnly a dict
            // and keep a couple of them - tracking how well they (hashes) were agreed with.
            // Maybe when asked it's better to send out the "one before last" if the most recent disagreed
            // with all the other peers?
            // public Dictionary<string, string> ReportedHashes; // keyed by reporting peerId

            public AppStateData(long sequenceNum)
            {
                SequenceNumber = sequenceNum;
                // ReportedHashes = new Dictionary<string, string>();
            }
        }

        protected class ServerOnlyData
        {
            private ApianBase ApianInst {get; }
            private long NextNewCommandSeqNum; // really should ever access this. Creator should use GetNewCommandSequenceNumber()
            public long GetNewCommandSequenceNumber() => NextNewCommandSeqNum++;
            public Dictionary<string, SyncingPeerData> syncingPeers;
            public AppStateData stashedAppState; // Consider making this a dict
            UniLogger Logger;

            private int  MaxSyncCmdsPerUpdate; //
            public ServerOnlyData(ApianBase apInst, Dictionary<string,string> config)
            {
                Logger = UniLogger.GetLogger("ApianGroup");    // re-use same logger (put class name in msgs)
                ApianInst = apInst;
                syncingPeers = new Dictionary<string, SyncingPeerData>();
                _LoadConfig(config);
            }

            private void _LoadConfig(Dictionary<string,string> configDict)
            {
                MaxSyncCmdsPerUpdate = int.Parse(configDict["Server.MaxSyncCmdsPerUpdate"]);
            }

            private SyncingPeerData _UpdateSyncingPeer(SyncingPeerData peerData,  long first, long firstPeerHas)
            {
                // TODO: This should not be able to happen
                peerData.nextCommandToSend = Math.Min(peerData.nextCommandToSend, first);
                peerData.firstCommandPeerHas = Math.Max(peerData.firstCommandPeerHas, firstPeerHas);
                return peerData;
            }

            public void AddSyncingPeer(string peerId, long firstNeededCmd, long firstStashedCmd )
            {
                SyncingPeerData peer = !syncingPeers.ContainsKey(peerId) ? new SyncingPeerData(peerId, firstNeededCmd, firstStashedCmd)
                    : _UpdateSyncingPeer(syncingPeers[peerId], firstNeededCmd, firstStashedCmd);
                syncingPeers[peerId] = peer;
            }

            public void SendSyncData( Dictionary<long, ApianCommand> commandStash)
            {
                List<string> donePeers = new List<string>();
                int msgsLeft = MaxSyncCmdsPerUpdate;
                foreach (SyncingPeerData sPeer in syncingPeers.Values )
                {
                    msgsLeft -= _SendCmdsToOnePeer(sPeer, commandStash, msgsLeft);
                    if (sPeer.nextCommandToSend >= sPeer.firstCommandPeerHas) // might be greater than if checkpoint data ends after firstCommandPeerHas
                        donePeers.Add(sPeer.peerId);
                    if (msgsLeft <= 0)
                        break;
                }

                foreach (string id in donePeers)
                    syncingPeers.Remove(id);
            }

            private int _SendCmdsToOnePeer(SyncingPeerData sPeer,  Dictionary<long, ApianCommand> commandStash, int maxToSend)  // true means "keep going"
            {
                int cmdsSent = 0;
                for (int i=0; i<maxToSend;i++)
                {
                    long cmdNum = sPeer.nextCommandToSend;
                    if (commandStash.ContainsKey(cmdNum))
                    {
                        ApianCommand cmd =  commandStash[cmdNum];
                        ApianInst.GameNet.SendApianMessage(sPeer.peerId, cmd);
                        cmdsSent++;
                        sPeer.nextCommandToSend++;
                        if (sPeer.nextCommandToSend == sPeer.firstCommandPeerHas)
                            break;
                    }
                    else
                        break; // should have access to logger to warn
                }
                return cmdsSent;
            }

            // AppState
            public void StashLocalState(long seqNum, long timeStamp, string stateHash, string serializedState)
            {
                stashedAppState = new AppStateData(seqNum);
                stashedAppState.TimeStamp = timeStamp;
                stashedAppState.StateHash = stateHash;
                stashedAppState.SerializedStateData = serializedState;
            }

        }


        public string MainP2pChannel {get => ApianInst.GameNet.CurrentGameId();}
        private readonly Dictionary<string, Action<ApianGroupMessage, string, string>> GroupMsgHandlers;
        private const string CreatorServerGroupType = "CreatorServerGroup";

        // IApianGroupManager
        public ApianGroupInfo GroupInfo {get; private set;}
        public string GroupType {get => CreatorServerGroupType;}
        public string GroupId {get => GroupInfo.GroupId;}
        public string GroupCreatorId {get => GroupInfo.GroupCreatorId;}
        public string LocalPeerId {get => ApianInst.GameNet.LocalP2pId();}
        public bool Intialized {get => GroupInfo != null; }
        public ApianGroupMember LocalMember {private set; get;}
        private Dictionary<long, ApianCommand> CommandStash;

        // State data
        private long MaxReceivedCmdSeqNum; // inits to -1
        private long MaxAppliedCmdSeqNum; // inits to -1
        private long DontRequestSyncBeforeMs; // When waiting for a SyncCompletionRequest reply, this is when it's ok to give up and ask again
        private long NextCheckPointMs;

        // Config params
        Dictionary<string,string> ConfigDict;
        private long SyncCompletionWaitMs; // wait this long for a sync completion request reply
        private int StashedCommandsPerUpdate;
        private long CheckpointMs;
        private long CheckpointOffsetMs;

        private ServerOnlyData ServerData;
        public bool LocalPeerIsServer {get => ServerData != null;}
        private static long SysMsNow => DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond;

        public CreatorServerGroupManager(ApianBase apianInst, Dictionary<string,string> config=null) : base(apianInst)
        {
            _ParseConfig(config ?? DefaultConfig);

            GroupMsgHandlers = new Dictionary<string, Action<ApianGroupMessage, string, string>>() {
                {ApianGroupMessage.GroupsRequest, OnGroupsRequest },
                {ApianGroupMessage.GroupJoinRequest, OnGroupJoinRequest },
                {ApianGroupMessage.GroupMemberJoined, OnGroupMemberJoined },
                {ApianGroupMessage.GroupMemberStatus, OnGroupMemberStatus },
                {ApianGroupMessage.GroupSyncRequest, OnGroupSyncRequest },
                {ApianGroupMessage.GroupSyncData, OnGroupSyncData },
                {ApianGroupMessage.GroupSyncCompletion, OnGroupSyncCompletionMsg },
                {ApianGroupMessage.GroupCheckpointReport, OnGroupCheckpointReport },
            };

            CommandStash = new Dictionary<long, ApianCommand>();
            MaxAppliedCmdSeqNum = -1; // this +1 is what we expect to see enxt
            MaxReceivedCmdSeqNum = -1; // haven't received one yet

        }

        private void _ParseConfig( Dictionary<string,string> config)
        {
            ConfigDict = config; // ugly
            SyncCompletionWaitMs = int.Parse(config["SyncCompletionWaitMs"]);
            StashedCommandsPerUpdate = int.Parse(config["StashedCommandsPerUpdate"]);
            CheckpointMs = int.Parse(config["CheckpointMs"]);
            CheckpointOffsetMs = int.Parse(config["CheckpointOffsetMs"]);
        }

        public void CreateNewGroup(string groupName)
        {
            Logger.Info($"{this.GetType().Name}.CreateNewGroup(): {groupName}");

            // Creating a new group
            string groupId = $"{ApianInst.GameId}/{groupName}";
            ServerData = new ServerOnlyData(ApianInst, ConfigDict);
            ApianGroupInfo newGroupInfo = new ApianGroupInfo(CreatorServerGroupType, groupId, LocalPeerId, groupName);
            InitExistingGroup(newGroupInfo);
            ApianInst.ApianClock.Set(0); // we're the group leader so we need to start our clock
            NextCheckPointMs = CheckpointMs + CheckpointOffsetMs;
        }

        public void InitExistingGroup(ApianGroupInfo info)
        {
            Logger.Info($"{this.GetType().Name}.InitExistingGroup(): {info.GroupId}");
            GroupInfo = info;
            ApianInst.GameNet.AddApianInstance(ApianInst, info.GroupId);
        }

        public void JoinGroup(string groupName, string localMemberJson)
        {
            // Local call.
            string groupId = $"{ApianInst.GameId}/{groupName}";
            Logger.Info($"{this.GetType().Name}.JoinGroup(): {groupId}");
            ApianInst.GameNet.AddChannel(GroupId);
            ApianInst.GameNet.SendApianMessage(GroupCreatorId, new GroupJoinRequestMsg(groupId, LocalPeerId, localMemberJson));
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

        public void Update()
        {
            if (LocalPeerIsServer)
                _ServerUpdate();

            _ApplyStashedCommands(); // always check the stash
        }

        //
        // TODO: move server stuff to the server class
        //
        private void _ServerUpdate()
        {
            ServerData.SendSyncData(CommandStash);
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
            ApianCheckpointCommand cpCmd = new ApianCheckpointCommand(ServerData.GetNewCommandSequenceNumber(), GroupId, cpMsg);
            Logger.Info($"{this.GetType().Name}._SendCheckpointCommand() SeqNum: {cpCmd.SequenceNum}, Timestamp: {NextCheckPointMs} at {curApainMs}");
            ApianInst.SendApianMessage(GroupId, cpCmd);
        }

        private void _ApplyStashedCommands()
        {
            for(int i=0; i<StashedCommandsPerUpdate;i++)
            {
                long expectedSeqNum = MaxAppliedCmdSeqNum+1;
                if (CommandStash.ContainsKey(expectedSeqNum))
                {
                    ApianInst.ApplyStashedApianCommand(CommandStash[expectedSeqNum]);
                    MaxAppliedCmdSeqNum = expectedSeqNum;
                    CommandStash.Remove(expectedSeqNum); // TODO: remove all comamnds <= expectedSeqNum
                    if (expectedSeqNum >= MaxReceivedCmdSeqNum )
                    {
                        if (LocalMember.CurStatus == ApianGroupMember.Status.Syncing)
                        {
                            Logger.Info($"{this.GetType().Name}._ApplyStashedCommand(): Sending SyncCompletion request.");
                            ApianInst.SendApianMessage(GroupCreatorId, new GroupSyncCompletionMsg(GroupId, expectedSeqNum, "hash"));
                            DontRequestSyncBeforeMs = SysMsNow + SyncCompletionWaitMs;
                        }
                        break; // don;t try any more
                    }
                }
            }
        }


        public void OnApianMessage(ApianMessage msg, string msgSrc, string msgChannel)
        {
            if (msg != null && msg.MsgType == ApianMessage.GroupMessage)
            {
                ApianGroupMessage gMsg = msg as ApianGroupMessage;
                GroupMsgHandlers[gMsg.GroupMsgType](gMsg, msgSrc, msgChannel);
            }
            else
                Logger.Warn($"OnApianMessage(): unexpected APianMsg Type: {msg?.MsgType}");
        }

        public void OnApianRequest(ApianRequest msg, string msgSrc, string msgChan)
        {
            // Requests are assumed to be valid as long as source is Active
            if (LocalPeerIsServer && GetMember(msgSrc)?.CurStatus == ApianGroupMember.Status.Active)
                ApianInst.GameNet.SendApianMessage(msgChan, msg.ToCommand(ServerData.GetNewCommandSequenceNumber()));
        }

        public void OnApianObservation(ApianObservation msg, string msgSrc, string msgChan)
        {
            // Observations from the server are turned into commands by the server
            if (LocalPeerIsServer && (msgSrc == LocalPeerId))
                ApianInst.GameNet.SendApianMessage(msgChan, msg.ToCommand(ServerData.GetNewCommandSequenceNumber()));
        }

        public ApianCommandStatus EvaluateCommand(ApianCommand msg, string msgSrc, string msgChan)
        {
            // Too early
            if (LocalMember == null)
                return ApianCommandStatus.kLocalPeerNotReady;

            // Valid if from the creator
            if (msgSrc != GroupCreatorId)
                return ApianCommandStatus.kBadSource;


            if (LocalPeerIsServer) // Server has to stash everything
                CommandStash[msg.SequenceNum] = msg;// TODO: at least until there is checkpointing

            // if we are processing the cache (syncing) and get to this then we are done.
            // Keep in mind that after we've requested sync data we'll be getting both LIVE commands
            // (with bigger #s) and "we asked for em" commands.
            MaxReceivedCmdSeqNum = Math.Max(MaxReceivedCmdSeqNum, msg.SequenceNum);

            long expectedSeqNum = MaxAppliedCmdSeqNum + 1; //

            // TODO: this is hideous with all the returns and convolution.
            // Once it's working figure out how to make it elegant.
            if (LocalMember.CurStatus == ApianGroupMember.Status.Active)
            {
                if (msg.SequenceNum <= MaxAppliedCmdSeqNum)
                    return ApianCommandStatus.kAlreadyReceived;
                else if (msg.SequenceNum > expectedSeqNum)
                {
                    Logger.Warn($"{this.GetType().Name}.EvaluateCommand() Expected Seq#: {expectedSeqNum}, Got: {msg.SequenceNum}");
                    CommandStash[msg.SequenceNum] = msg; // stash it. Maybe what we're looking for will show up soon.
                    return ApianCommandStatus.kStashedInQueued;
                }

            } else {
                // TODO: This is how we enter Sync mode. Maybe it should be more explicit?
                if (LocalMember.CurStatus == ApianGroupMember.Status.Joining)
                    _RequestSync(msg.SequenceNum);

                CommandStash[msg.SequenceNum] = msg;
                return ApianCommandStatus.kStashedInQueued;
            }

            MaxAppliedCmdSeqNum = msg.SequenceNum;
            return ApianCommandStatus.kShouldApply;
        }

        private void _RequestSync(long firstStashedSeqNum)
        {
            // Here we actually set our own status and short-circuit the MeberStatusMsg
            // process - we WILL recieve that message in a bit, but in the meantime we want to
            // stop processing incoming commands and stash them instead.

            long firstSeqNumWeNeed = MaxAppliedCmdSeqNum + 1;  // Since the variable inits to -1, this is 0 if we haven't applied any

            GroupSyncRequestMsg syncRequest = new GroupSyncRequestMsg(GroupId, firstSeqNumWeNeed, firstStashedSeqNum);
            Logger.Info($"{this.GetType().Name}._RequestSync() sending req: start: {syncRequest.ExpectedCmdSeqNum} 1st Stashed: {syncRequest.FirstStashedCmdSeqNum}");
            ApianInst.SendApianMessage(GroupCreatorId, syncRequest);

            // the local short-circuit...
            ApianGroupMember.Status prevStatus = LocalMember.CurStatus;
            LocalMember.CurStatus = ApianGroupMember.Status.Syncing;
            ApianInst.OnGroupMemberStatusChange(LocalMember, prevStatus);
            // when creator gets the sync request it'll broadcast an identical status change msg
        }

      private void OnGroupsRequest(ApianGroupMessage msg, string msgSrc, string msgChannel)
        {
            // Only the creator answers
            if (LocalPeerIsServer)
            {
                GroupAnnounceMsg amsg = new GroupAnnounceMsg(GroupInfo);
                ApianInst.SendApianMessage(msgSrc, amsg);
            }
        }

        private void OnGroupJoinRequest(ApianGroupMessage msg, string msgSrc, string msgChannel)
        {
            // In this implementation the creator decides
            // Everyone else just ignores this.
            if (LocalPeerIsServer)
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

        private void _SendMemberJoinedMessages(string toWhom)
        {
            // Send a newly-approved member all of the Join messages for the other member
            // Create messages first, then send (mostly so Members doesn;t get modified while enumerating it)
            List<GroupMemberJoinedMsg> joinMsgs = Members.Values
                .Where( m => m.PeerId != toWhom)
                .Select( (m) =>  new GroupMemberJoinedMsg(GroupId, m.PeerId, m.AppDataJson)).ToList();
            foreach (GroupMemberJoinedMsg msg in joinMsgs)
                ApianInst.SendApianMessage(toWhom, msg);
        }

        private void _SendMemberStatusUpdates(string toWhom)
        {
            // Send a newly-approved member the status of every non-"Joining" member (since a joinmessage was already sent)
            List<GroupMemberStatusMsg> statusMsgs = Members.Values
                .Where( (m) => m.CurStatus != ApianGroupMember.Status.Joining && m.PeerId != toWhom)
                .Select( (m) =>  new GroupMemberStatusMsg(GroupId, m.PeerId, m.CurStatus)).ToList();
            foreach (GroupMemberStatusMsg msg in statusMsgs)
                ApianInst.SendApianMessage(toWhom, msg);
        }

        private void OnGroupMemberJoined(ApianGroupMessage msg, string msgSrc, string msgChannel)
        {
            // If from GroupCreator then it's valid
            if (msgSrc == GroupCreatorId)
            {
                GroupMemberJoinedMsg joinedMsg = (msg as GroupMemberJoinedMsg);
                Logger.Info($"{this.GetType().Name}.OnGroupMemberJoined() from boss:  {joinedMsg.DestGroupId} adds {joinedMsg.PeerId}");

                ApianGroupMember m = _AddMember(joinedMsg.PeerId, joinedMsg.ApianClientPeerJson);

                ApianInst.OnGroupMemberJoined(m); // inform local apian

                // Unless we are the group creator AND this is OUR join message
                if (joinedMsg.PeerId == LocalPeerId && LocalPeerIsServer)
                {
                    // Yes, Which means we're also the first. Declare  *us* "Active" and tell everyone
                    ApianInst.SendApianMessage(GroupId, new GroupMemberStatusMsg(GroupId, LocalPeerId, ApianGroupMember.Status.Active));
                }

            }
        }

        private void OnGroupMemberStatus(ApianGroupMessage msg, string msgSrc, string msgChannel)
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
                    Logger.Error($"{this.GetType().Name}.OnGroupMemberStatus(): Member not present" );
            }
        }

        // &&& Old, command-only version
        // private void OnGroupSyncRequest(ApianGroupMessage msg, string msgSrc, string msgChannel)
        // {
        //     if (LocalPeerIsServer) // Only creator handles this
        //     {
        //         GroupSyncRequestMsg sMsg = (msg as GroupSyncRequestMsg);
        //         Logger.Info($"{this.GetType().Name}.OnGroupSyncRequest() from {msgSrc} start: {sMsg.ExpectedCmdSeqNum} 1st stashed: {sMsg.FirstStashedCmdSeqNum}");

        //         ServerData.AddSyncingPeer(msgSrc, sMsg.ExpectedCmdSeqNum, sMsg.FirstStashedCmdSeqNum );
        //         ApianInst.SendApianMessage(GroupId, new GroupMemberStatusMsg(GroupId, msgSrc, ApianGroupMember.Status.Syncing));
        //     }
        // }

        private void OnGroupSyncRequest(ApianGroupMessage msg, string msgSrc, string msgChannel)
        {
            if (LocalPeerIsServer) // Only creator handles this
            {
                GroupSyncRequestMsg sMsg = (msg as GroupSyncRequestMsg);
                Logger.Info($"{this.GetType().Name}.OnGroupSyncRequest() from {msgSrc} start: {sMsg.ExpectedCmdSeqNum} 1st stashed: {sMsg.FirstStashedCmdSeqNum}");

                ApianInst.SendApianMessage(GroupId, new GroupMemberStatusMsg(GroupId, msgSrc, ApianGroupMember.Status.Syncing));

                // Send out most recent state
                long firstCmdToSend = sMsg.ExpectedCmdSeqNum;

                AppStateData state = ServerData.stashedAppState;
                if (state != null)
                {
                    ApianInst.SendApianMessage(msgSrc, new GroupSyncDataMsg(GroupId, state.TimeStamp, state.SequenceNumber, state.StateHash, state.SerializedStateData));
                    firstCmdToSend = state.SequenceNumber + 1;
                    Logger.Info($"{this.GetType().Name}.OnGroupSyncRequest() Sending checkpoint ending with seq# {state.SequenceNumber}");
                }
                Logger.Info($"{this.GetType().Name}.OnGroupSyncRequest() Sending peer stashed commands from {firstCmdToSend} through {sMsg.FirstStashedCmdSeqNum-1}");
                ServerData.AddSyncingPeer(msgSrc, firstCmdToSend, sMsg.FirstStashedCmdSeqNum );
            }
        }

        private void OnGroupSyncData(ApianGroupMessage msg, string msgSrc, string msgChannel)
        {
            GroupSyncDataMsg dMsg = (msg as GroupSyncDataMsg);
            ApianInst.ApplyCheckpointStateData(dMsg.StateSeqNum, dMsg.StateTimeStamp, dMsg.StateHash, dMsg.StateData);
            MaxAppliedCmdSeqNum = dMsg.StateSeqNum;
            MaxReceivedCmdSeqNum = dMsg.StateSeqNum;
        }

        private void OnGroupSyncCompletionMsg(ApianGroupMessage msg, string msgSrc, string msgChannel)
        {
            if (LocalPeerIsServer) // Only creator handles this
            {
                GroupSyncCompletionMsg sMsg = (msg as GroupSyncCompletionMsg);
                Logger.Info($"{this.GetType().Name}.OnGroupSyncCompletionMsg() from {msgSrc} SeqNum: {sMsg.CompletionSeqNum} Hash: {sMsg.CompleteionStateHash}");
                ApianInst.SendApianMessage(GroupId, new GroupMemberStatusMsg(GroupId, msgSrc, ApianGroupMember.Status.Active));
            }
        }

        public void OnLocalStateCheckpoint(long seqNum, long timeStamp, string stateHash, string serializedState)
        {
            Logger.Verbose($"***** {this.GetType().Name}.OnLocalStateCheckpoint() Checkpoint Seq#: {seqNum}, Hash: {stateHash}");
            if (LocalPeerIsServer) // Only server handles this
            {
                ServerData.StashLocalState(seqNum, timeStamp, stateHash, serializedState);

                // Toss out the old stashed commands
                // TODO:  Hmm. Or maybe not. Seems like there is a need for a persistent record.
                //CommandStash = CommandStash.Values.Where( c => c.SequenceNum > seqNum).ToDictionary(c => c.SequenceNum);
            }

        }

        private void OnGroupCheckpointReport(ApianGroupMessage msg, string msgSrc, string msgChannel)
        {
            GroupCheckpointReportMsg rMsg = msg as GroupCheckpointReportMsg;
            Logger.Info($"***** {this.GetType().Name}.OnGroupCheckpointReport() from {msgSrc} Checkpoint Seq#: {rMsg.SeqNum}, Hash: {rMsg.StateHash}");
            if (LocalPeerIsServer) // Only server handles this
            {
                // TODO: use this to report hashes to check the "quality" of a save state
                // (OTOH: since by definition the server's version is true, it's probably not a big deal)
                // ServerData.HandleRemoteState(rMsg.SeqNum, rMsg.StateHash);
            }

        }

    }
}