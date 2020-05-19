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

        protected class SyncingPeerData
        {
            public string peerId;
            public long nextCommandToSend;
            public long lastCommandToSend;

            public SyncingPeerData(string pid, long first, long last)
            {
                peerId = pid;
                nextCommandToSend = first;
                lastCommandToSend = last;
            }
        }

        protected class ServerOnlyData
        {
            private ApianBase ApianInst {get; }
            private long NextNewCommandSeqNum; // really should ever access this. Creator should use GetNewCommandSequenceNumber()
            public long GetNewCommandSequenceNumber() => NextNewCommandSeqNum++;
            public Dictionary<string, SyncingPeerData> syncingPeers;
            public ServerOnlyData(ApianBase apInst)
            {
                ApianInst = apInst;
                syncingPeers = new Dictionary<string, SyncingPeerData>();
            }

            private SyncingPeerData _UpdateSyncingPeer(SyncingPeerData peerData,  long first, long last)
            {
                peerData.nextCommandToSend = Math.Min(peerData.nextCommandToSend, first);
                peerData.lastCommandToSend = Math.Max(peerData.lastCommandToSend, last);
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
                foreach (SyncingPeerData speer in syncingPeers.Values )
                {
                    if (_SendOneSyncCmd(speer, commandStash) == false)
                        donePeers.Add(speer.peerId);
                }
                foreach (string id in donePeers)
                    syncingPeers.Remove(id);
            }

            private bool _SendOneSyncCmd(SyncingPeerData sPeer,  Dictionary<long, ApianCommand> commandStash)  // true means "keep going"
            {
                long cmdNum = sPeer.nextCommandToSend;
                if (commandStash.ContainsKey(cmdNum))
                {
                    ApianCommand cmd =  commandStash[cmdNum];
                    ApianInst.GameNet.SendApianMessage(sPeer.peerId, cmd);
                    sPeer.nextCommandToSend++;
                    if (sPeer.nextCommandToSend == sPeer.lastCommandToSend)
                    {
                        // &&&& No! We can;t mark the peer active. I has to tell US it is up-to-date
                        // ApianInst.SendApianMessage(ApianInst.GroupId, new GroupMemberStatusMsg(ApianInst.GroupId, sPeer.peerId, ApianGroupMember.Status.Active));
                        return false;
                    }
                }
                return true;
            }
        }


        private ApianBase ApianInst {get; }
        public string MainP2pChannel {get => ApianInst.GameNet.CurrentGameId();}
        public UniLogger Logger;
        private readonly Dictionary<string, Action<ApianGroupMessage, string, string>> GroupMsgHandlers;
        private const string CreatorServerGroupType = "CreatorServerGroup";

        // IApianGroupManager
        public ApianGroupInfo GroupInfo {get; private set;}
        public string GroupType {get => CreatorServerGroupType;}
        public string GroupId {get => GroupInfo.GroupId;}
        public string GroupCreatorId {get => GroupInfo.GroupCreatorId;}
        public string LocalPeerId {get => ApianInst.GameNet.LocalP2pId();}
        public Dictionary<string, ApianGroupMember> Members {get;}
        public bool Intialized {get => GroupInfo != null; }
        public ApianGroupMember LocalMember {private set; get;}
        private Dictionary<long, ApianCommand> CommandStash;
        private long LastProcessedCommandSeqNum; // init to -1

        private bool SyncCompleteRequested; // while this is true commands get handled as if we aren;t syncing (to prevent multiple requests)
        // TODO: &&&& FIX THE ABOVE! Change to a timeout (couple seconds in the future) before which a sync request can't be issued.

        private ServerOnlyData ServerData;
        public bool LocalPeerIsServer {get => ServerData != null;}


        public CreatorServerGroupManager(ApianBase apianInst)
        {
            GroupMsgHandlers = new Dictionary<string, Action<ApianGroupMessage, string, string>>() {
                {ApianGroupMessage.GroupsRequest, OnGroupsRequest },
                {ApianGroupMessage.GroupJoinRequest, OnGroupJoinRequest },
                {ApianGroupMessage.GroupMemberJoined, OnGroupMemberJoined },
                {ApianGroupMessage.GroupMemberStatus, OnGroupMemberStatus },
                {ApianGroupMessage.GroupSyncRequest, OnGroupSyncRequest },
                {ApianGroupMessage.GroupSyncCompletion, OnGroupSyncCompletionMsg },
            };

            Logger = UniLogger.GetLogger("ApianGroup");
            ApianInst = apianInst;
            Members = new Dictionary<string, ApianGroupMember>();
            CommandStash = new Dictionary<long, ApianCommand>();
            LastProcessedCommandSeqNum = -1; // this +1 is what we expect to see enxt
         }

        public void CreateNewGroup(string groupId, string groupName)
        {
            Logger.Info($"{this.GetType().Name}.CreateNewGroup(): {groupId}");

            // Creating a new group
            ServerData = new ServerOnlyData(ApianInst);
            ApianGroupInfo newGroupInfo = new ApianGroupInfo(CreatorServerGroupType, groupId, LocalPeerId, groupName);
            InitExistingGroup(newGroupInfo);
            ApianInst.ApianClock.Set(0); // we're the group leader so we need to start our clock
        }

        public void InitExistingGroup(ApianGroupInfo info)
        {
            Logger.Info($"{this.GetType().Name}.InitExistingGroup(): {info.GroupId}");
            GroupInfo = info;
            ApianInst.GameNet.AddApianInstance(ApianInst, info.GroupId);
        }

        public void JoinGroup(string groupId, string localMemberJson)
        {
            // Local call.
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
                ServerData.SendSyncData(CommandStash);

            if (LocalMember?.CurStatus == ApianGroupMember.Status.Syncing)
                _ApplyStashedCommand();
        }

        private void _ApplyStashedCommand()
        {
            long expectedSeqNum = LastProcessedCommandSeqNum+1;
            if (CommandStash.ContainsKey(expectedSeqNum))
            {
                ApianInst.ApplyApianCommand(CommandStash[expectedSeqNum]);
                LastProcessedCommandSeqNum++;
                CommandStash.Remove(expectedSeqNum);
                if (CommandStash.Count == 0 && !SyncCompleteRequested)
                {
                    Logger.Info($"{this.GetType().Name}._ApplyStashedCommand(): Sending SyncCompletion request.");
                    ApianInst.SendApianMessage(GroupCreatorId, new GroupSyncCompletionMsg(GroupId, expectedSeqNum, "hash"));
                    SyncCompleteRequested = true;
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
            // Creator sends out command
            if (LocalPeerIsServer)
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


            // TODO: this is hideous with all the returns and convolution.
            // Once it's working figure out how to make it elegant.
            if (LocalMember.CurStatus == ApianGroupMember.Status.Active)
            {
                // If we received OnGroupJoined yet (null LocalMmeber) go ahead and anticipate it...
                if (msg.SequenceNum <= LastProcessedCommandSeqNum)
                    return ApianCommandStatus.kAlreadyReceived;
                else if (msg.SequenceNum > LastProcessedCommandSeqNum+1)
                {
                    _RequestSync(msg.SequenceNum);
                    CommandStash[msg.SequenceNum] = msg;
                    return ApianCommandStatus.kStashedForSync;
                }

            } else {
                if (LocalMember.CurStatus != ApianGroupMember.Status.Syncing)
                    _RequestSync(msg.SequenceNum);

                if (!SyncCompleteRequested) // if we've caugt up them apply the command now
                {
                    CommandStash[msg.SequenceNum] = msg;
                    return ApianCommandStatus.kStashedForSync;
                }
            }

            LastProcessedCommandSeqNum++;
            return ApianCommandStatus.kShouldApply;
        }

        private void _RequestSync(long lastCommandWeNeed)
        {
            // Here we actually set our own status and short-circuit the MeberStatusMsg
            // process - we WILL recieve that message in a bit, but in the meantime we want to
            // stop processing incoming commands and stash them instead.

            long firstSeqNumWeNeed = LastProcessedCommandSeqNum + 1;  // Since LPCSN inits to -1, this is 0 if we haven't gotten any

            GroupSyncRequestMsg syncRequest = new GroupSyncRequestMsg(GroupId, firstSeqNumWeNeed, lastCommandWeNeed);
            Logger.Info($"{this.GetType().Name}._RequestSync() sending req: start: {syncRequest.ExpectedCmdSeqNum} end: {syncRequest.FirstStashedCmdSeqNum}");
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

        // private void _DeclareAllJoinersActive()
        // {
        //     List<GroupMemberStatusMsg> statusMsgs = Members.Values
        //         .Where( (m) => m.CurStatus == ApianGroupMember.Status.Joining)
        //         .Select( (m) =>  new GroupMemberStatusMsg(GroupId, m.PeerId, ApianGroupMember.Status.Active)).ToList();
        //     foreach (GroupMemberStatusMsg msg in statusMsgs)
        //         ApianInst.SendApianMessage(GroupId, msg);
        // }

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
                Logger.Info($"{this.GetType().Name}.OnGroupMemberStatus():  {sMsg.PeerId} to {sMsg.MemberStatus}");
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
        private void OnGroupSyncRequest(ApianGroupMessage msg, string msgSrc, string msgChannel)
        {
            if (LocalPeerIsServer) // Only creator handles this
            {
                GroupSyncRequestMsg sMsg = (msg as GroupSyncRequestMsg);
                Logger.Info($"{this.GetType().Name}.OnGroupSyncRequest() from {msgSrc} start: {sMsg.ExpectedCmdSeqNum} end: {sMsg.FirstStashedCmdSeqNum}");

                ServerData.AddSyncingPeer(msgSrc, sMsg.ExpectedCmdSeqNum, sMsg.FirstStashedCmdSeqNum );
                ApianInst.SendApianMessage(GroupId, new GroupMemberStatusMsg(GroupId, msgSrc, ApianGroupMember.Status.Syncing));
            }
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


       public ApianMessage DeserializeGroupMessage(string subType, string json)
        {
            return null;
        }

    }
}