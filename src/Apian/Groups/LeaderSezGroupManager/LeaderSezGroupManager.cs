using System.Linq;
using System;
using System.Collections.Generic;
using UniLog;
using static UniLog.UniLogger; // for SID

namespace Apian
{
    public class LeaderSezGroupManager : LeaderDecidesGmBase
    {
        public static readonly Dictionary<string, string> LsDefaultConfig = new Dictionary<string, string>()
        {
            {"CheckpointMs", "10000"},  // LeaderDecidesBase -  request a checkpoint this often. Length of an epoch.
            {"CheckpointOffsetMs", "50"},  //  LeaderDecidesBase -Use this to get the checkpoint times NOT to be on roudning boundaries
            {"SyncCompletionWaitMs", "2000"}, //  LeaderDecidesBase -wait this long for a sync completion request reply
            {"StashedCmdsToApplyPerUpdate", "10"}, // CommandSynchronizer -  applying locally received commands that we weren't ready for yet
            {"MaxSyncCmdsToSendPerUpdate", "10"}, // CommandSynchronizer - sending commands to another peer to "catch it up"
            {"LeaderTermLength", "5"}, // LeaderSez - in eopchs
        };


        // Factories and UI stuff needs these static const defs
        public const string kGroupType = "LeaderSez";
        public const string kGroupTypeName = "LeaderSez";

        // IApianGroupManager interface needs these non-static defs
        public override string GroupType {get => kGroupType; }
        public override string GroupTypeName {get => kGroupTypeName; }

        protected string NextLeaderAddr { get; set;}// null == no failover leader defined
        protected long NextLeaderFirstEpoch{ get; set;} // epoch # when nextLeader takes over. 0 means no planned handover.

        protected int LeaderTermLength;

        public LeaderSezGroupManager(ApianBase apianInst, Dictionary<string,string> config = null) : base(apianInst, config ?? LsDefaultConfig)
        {
            _ParseConfig(config ?? LsDefaultConfig); // LeaderDecidesGmBase ctor sets: protected ConfigDict = config

            GroupMsgHandlers[SetLeaderMsg.MsgTypeId] = OnSetLeaderMsg;
        }

        private void _ParseConfig( Dictionary<string,string> config)
        {
            LeaderTermLength = int.Parse(config["LeaderTermLength"]);
        }

        protected void SetNextLeader( string nextLeaderAddr, long nextLeaderEpoch)
        {
            Logger.Info($".SetNextLeader() - setting NEXT group leader to {(nextLeaderAddr!=null?SID(nextLeaderAddr):"null")} at epoch {nextLeaderEpoch}");
            if (nextLeaderAddr == LocalPeerAddr)
                Logger.Info($"SetNextLeader() ===== Local Peer is now NEXT LEADER (in {nextLeaderEpoch - ApianInst.CurrentEpochNum} epochs)");
            NextLeaderAddr = nextLeaderAddr;
            NextLeaderFirstEpoch = nextLeaderEpoch;
        }

        protected string SelectNextLeader(List<string> peerAddrsToExclude)
        {
            string nextLeader = null;
            // We're now the leader! Claim it! WHo's next?
            List<string> candidates = Members.Values.Where(m => ( !peerAddrsToExclude.Contains(m.PeerAddr) && m.IsActive ))
                                .Select(m => m.PeerAddr).ToList();

            if (candidates.Count == 0) {
                // Crap. Just us
                // SendSetLeader( LocalPeerAddr, 0);
            } else if (candidates.Count == 1) {
                // Only one other. Probably previous leader.
                nextLeader = candidates[0];
            } else {
                // filter out previous leader
                List<string> notPrevLeader = candidates.Where( id => GetMember(id)?.PeerAddr != GroupLeaderAddr).ToList();
                int idx = new Random().Next(0, notPrevLeader.Count);
                nextLeader =  candidates[idx];
            }

            return nextLeader;
        }

        protected override void SetLeader(string newLeaderAddr)
        {
            Logger.Info($"{this.GetType().Name}.SetLeader() - setting group leader to {SID(newLeaderAddr)}  Local status: (status: {LocalMember?.CurStatus ?? ApianGroupMember.Status.Joining})");

            if (newLeaderAddr == GroupLeaderAddr)
            {
                Logger.Info($"!!!!!!! HEY! THat's ALREADY the leader!!! Ignoring.");
                return;
            }

            if (LocalPeerIsLeader && newLeaderAddr != LocalPeerAddr)
            {
                // It's not us anymore
                CmdSynchronizer.StopSendingData(); // If we're feeding anyone just stop.
            }

            base.SetLeader(newLeaderAddr);


            // Until a OnGroupMemberJoined arrives saying the local peer is a member there is no LocalMember
            // and status is effectively "New" or "Joining"
            ApianGroupMember.Status localStatus = LocalMember?.CurStatus ?? ApianGroupMember.Status.Joining;

            switch (localStatus)
            {
            case ApianGroupMember.Status.SyncingState:
                // local member is syncing AppState - needs to ask again from new leader
                Logger.Info($"{this.GetType().Name}.SetLeader() - Leader change while we were syncing. Ask new leader.");
                RequestSync(-1, -1);
                break;

            case ApianGroupMember.Status.Joining:
            case ApianGroupMember.Status.New:
                // Local member has requested to join, but not gotten response. Ask new leader.
                Logger.Info($"{this.GetType().Name}.SetLeader() - Leader change while we were waiting to join (status: {localStatus}). Ask new leader.");
                JoinGroup(null, false); // FIXME: &&&&& figure this out
                break;
            }
        }


       public override void OnNewEpoch()
       {
            if (ApianInst.CurrentEpochNum == NextLeaderFirstEpoch)
            {
                if (NextLeaderAddr != null)
                {
                    SetLeader(NextLeaderAddr);
                } else {
                    Logger.Error($"{this.GetType().Name}.OnNewEpoch() - NextLeaderAddr is null. There's no leader.");
                }

                if (LocalPeerAddr == NextLeaderAddr)
                {
                    Logger.Info($"OnNewEpoch() ===== Local Peer is now LEADER!!");
                    SetNextNewCommandSequenceNumber(ApianInst.CurrentEpoch.StartCmdSeqNumber); // <<=== This is IMPORTANT and kinda obstuse.

                    string nextLeader = SelectNextLeader(new List<string>(){LocalPeerAddr});
                    if (nextLeader != null)
                        SendSetLeader(GroupId, nextLeader, ApianInst.CurrentEpochNum + LeaderTermLength );
                }
            }
       }

        protected void SendSetLeader(string dest, string newLeaderAddr, long newLeaderEpoch)
        {
            SetLeaderMsg slMsg = new SetLeaderMsg(GroupId, newLeaderAddr, newLeaderEpoch);
            Logger.Info($"{this.GetType().Name}.SendSetLeader()  NewLeader: {SID(newLeaderAddr)}, NewLeaderEpoch: {newLeaderEpoch}");
            ApianInst.SendApianMessage(GroupId, slMsg);
        }

        protected void LocallyPromoteNextLeader()
        {
            SetLeader(NextLeaderAddr);
            NextLeaderAddr = null;
            NextLeaderFirstEpoch = 0;
        }

       protected override void OnGroupJoinRequest(ApianGroupMessage msg, string msgSrc, string msgChannel)
        {
            base.OnGroupJoinRequest(msg, msgSrc, msgChannel); // do default behavior first

            // Leader needs to send current/next leader info
            if (LocalPeerIsLeader)
            {
                GroupJoinRequestMsg jreq = msg as GroupJoinRequestMsg;
                if (jreq.PeerAddr != LocalPeerAddr)
                {
                    Logger.Info($"{this.GetType().Name}.OnGroupJoinRequest(): Sending current leader (us) info");
                    SendSetLeader(jreq.PeerAddr, LocalPeerAddr, 0);

                    if (NextLeaderAddr != null)
                    {
                        Logger.Info($"{this.GetType().Name}.OnGroupJoinRequest(): Send NextLeader info.");
                        SendSetLeader(jreq.PeerAddr, NextLeaderAddr, NextLeaderFirstEpoch);
                    }
                }
            }

        }


        protected override void OnGroupMemberStatus(ApianGroupMessage msg, string msgSrc, string msgChannel)
        {
            GroupMemberStatusMsg sMsg = (msg as GroupMemberStatusMsg);

            // Do default processing
            base.OnGroupMemberStatus(msg, msgSrc, msgChannel);

            // After default processing:

            if (msgSrc == GroupLeaderAddr && LocalPeerIsLeader) // if we are leader
            {
                ApianGroupMember mbr = GetMember(sMsg.PeerAddr);

                // If it's an active peer and NextLeader is null
                if (mbr != null && mbr.PeerAddr != LocalPeerAddr && mbr.IsActive && NextLeaderAddr == null )
                {
                    long nextLeaderEpoch = ApianInst.CurrentEpochNum + LeaderTermLength;
                    // it's not us - make them next leader
                    Logger.Info($"{this.GetType().Name}.OnGroupMemberStatus(). No current NextLeader, setting to {SID(mbr.PeerAddr)} at epoch {nextLeaderEpoch}");
                    SendSetLeader(GroupId, mbr.PeerAddr, nextLeaderEpoch);
                }
            }
        }

        public override void OnMemberLeftGroupChannel(string peerAddr)
        {
            // Local, first-notice that member is gone

            // LeaderSez needs to check to see if either the leader or the NextLeader was the member that left
            if (peerAddr == GroupLeaderAddr)
            {
                Logger.Info($"{this.GetType().Name}.OnMemberLeftGroupChannel(): GroupLeader is Gone!!!");
                // leader is gone! Failover.
                if (NextLeaderAddr != null)
                {
                    LocallyPromoteNextLeader();  // nulls nextleader, sets Leader
                    if (LocalPeerIsLeader) // <-- new leader
                    {
                        Logger.Info($"OnMemberLeftGroupChannel() ===== Local Peer taking over as LEADER!!");
                        SetNextNewCommandSequenceNumber(ApianInst.MaxAppliedCmdSeqNum+1); // <<=== This is IMPORTANT and kinda obtuse. And ugly.
                        string nextLeader = SelectNextLeader(new List<string>(){LocalPeerAddr, peerAddr});
                        if (nextLeader == null) {
                            // Nobody else? Set ourselves as next, too
                            Logger.Warn($"{this.GetType().Name}.OnMemberLeftGroupChannel(): Failed to select a NextLeader, setting local peer to it too.");
                            nextLeader = LocalPeerAddr;
                        }
                        SendSetLeader(GroupId, nextLeader, ApianInst.CurrentEpochNum + LeaderTermLength );
                    }
                }
                else
                {
                    Logger.Error($"{this.GetType().Name}.OnMemberLeftGroupChannel(): Leader left with no Nextleader!!! Yikes!!!");
                }
            }
            else if ( peerAddr == NextLeaderAddr)
            {
                if (LocalPeerIsLeader)
                {
                    // set a new next - and extend our term
                    string nextLeader = SelectNextLeader(new List<string>(){LocalPeerAddr, peerAddr});
                    if (nextLeader == null) {
                         Logger.Warn($"{this.GetType().Name}.OnMemberLeftGroupChannel(): Failed to select a NextLeader, setting local peer to it too.");
                         nextLeader = LocalPeerAddr;
                    }
                    SendSetLeader(GroupId, nextLeader, ApianInst.CurrentEpochNum + LeaderTermLength );
                }
            }

            base.OnMemberLeftGroupChannel(peerAddr);

        }

         public void OnSetLeaderMsg(ApianGroupMessage msg, string msgSrc, string msgChannel)
        {
            SetLeaderMsg slMsg = msg as SetLeaderMsg;

            Logger.Info($"{this.GetType().Name}.OnSetLeaderCmd() CurEpoch: {ApianInst.CurrentEpochNum}  New Leader: {SID(slMsg.newLeaderAddr)} at Epoch: {slMsg.newLeaderEpoch}");

            if ( slMsg.newLeaderEpoch <= ApianInst.CurrentEpochNum)
            {
                SetLeader(slMsg.newLeaderAddr);
            } else {
                SetNextLeader(slMsg.newLeaderAddr, slMsg.newLeaderEpoch);
            }
        }


        public override ApianMessage DeserializeCustomApianMessage(string msgType, string msgJSON)
        {
            return msgType == ApianMessage.GroupMessage
                ? LeaderSezGroupMessageDeserializer.FromJson(msgType, msgJSON)
                : null;
        }


    }

}