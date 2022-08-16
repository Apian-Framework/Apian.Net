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

        protected string NextLeaderId { get; set;}// null == no failover leader defined
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

        protected void SetNextLeader( string nextLeaderId, long nextLeaderEpoch)
        {
            Logger.Info($".SetNextLeader() - setting NEXT group leader to {(nextLeaderId!=null?SID(nextLeaderId):"null")} at epoch {nextLeaderEpoch}");
            if (nextLeaderId == LocalPeerId)
                Logger.Info($"SetNextLeader() ===== Local Peer is now NEXT LEADER (in {nextLeaderEpoch - CurrentEpochNum} epochs)");
            NextLeaderId = nextLeaderId;
            NextLeaderFirstEpoch = nextLeaderEpoch;
        }

        protected string SelectNextLeader(List<string> peerIdsToExclude)
        {
            string nextLeader = null;
            // We're now the leader! Claim it! WHo's next?
            List<string> candidates = Members.Values.Where(m => ( !peerIdsToExclude.Contains(m.PeerId) && m.IsActive ))
                                .Select(m => m.PeerId).ToList();

            if (candidates.Count == 0) {
                // Crap. Just us
                // SendSetLeader( LocalPeerId, 0);
            } else if (candidates.Count == 1) {
                // Only one other. Probably previous leader.
                nextLeader = candidates[0];
            } else {
                // filter out previous leader
                List<string> notPrevLeader = candidates.Where( id => GetMember(id)?.PeerId != GroupLeaderId).ToList();
                int idx = new Random().Next(0, notPrevLeader.Count);
                nextLeader =  candidates[idx];
            }

            return nextLeader;
        }

       public override void StartNewEpoch(long lastCmdSeqNum)
       {
            base.StartNewEpoch(lastCmdSeqNum);

            if (CurrentEpochNum == NextLeaderFirstEpoch)
            {
                if (NextLeaderId != null)
                {
                    SetLeader(NextLeaderId);
                }

                //SetNextLeader(null, 0); // zero it out until new leader sets it

                if (LocalPeerId == NextLeaderId)
                {
                    Logger.Info($"StartNewEpoch() ===== Local Peer is now LEADER!!");
                    SetNextNewCommandSequenceNumber(lastCmdSeqNum+1); // <<=== This is IMPORTANT and kinda obstuse.

                    string nextLeader = SelectNextLeader(new List<string>(){LocalPeerId});
                    if (nextLeader != null)
                        SendSetLeader(GroupId, nextLeader, CurrentEpochNum + LeaderTermLength );
                }
            }
       }

        protected void SendSetLeader(string dest, string newLeaderId, long newLeaderEpoch)
        {
            SetLeaderMsg slMsg = new SetLeaderMsg(GroupId, newLeaderId, newLeaderEpoch);
            Logger.Info($"{this.GetType().Name}.SendSetLeader()  NewLeader: {SID(newLeaderId)}, NewLeaderEpoch: {newLeaderEpoch}");
            ApianInst.SendApianMessage(GroupId, slMsg);
        }

        protected void LocallyPromoteNextLeader()
        {
            SetLeader(NextLeaderId);
            NextLeaderId = null;
            NextLeaderFirstEpoch = 0;
        }

       protected override void OnGroupJoinRequest(ApianGroupMessage msg, string msgSrc, string msgChannel)
        {
            base.OnGroupJoinRequest(msg, msgSrc, msgChannel); // do default behavior first

            // Leader needs to send current/next leader info
            if (LocalPeerIsLeader)
            {
                GroupJoinRequestMsg jreq = msg as GroupJoinRequestMsg;
                if (jreq.PeerId != LocalPeerId)
                {
                    Logger.Info($"{this.GetType().Name}.OnGroupJoinRequest(): Sending current leader (us) info");
                    SendSetLeader(jreq.PeerId, LocalPeerId, 0);

                    if (NextLeaderId != null)
                    {
                        Logger.Info($"{this.GetType().Name}.OnGroupJoinRequest(): Send NextLeader info.");
                        SendSetLeader(jreq.PeerId, NextLeaderId, NextLeaderFirstEpoch);
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

            if (msgSrc == GroupLeaderId && LocalPeerIsLeader) // if we are leader
            {
                ApianGroupMember mbr = GetMember(sMsg.PeerId);

                // If it's an active peer and NextLeader is null
                if (mbr != null && mbr.PeerId != LocalPeerId && mbr.IsActive && NextLeaderId == null )
                {
                    long nextLeaderEpoch = CurrentEpochNum + LeaderTermLength;
                    // it's not us - make them next leader
                    Logger.Info($"{this.GetType().Name}.OnGroupMemberStatus(). No current NextLeader, setting to {SID(mbr.PeerId)} at epoch {nextLeaderEpoch}");
                    SendSetLeader(GroupId, mbr.PeerId, nextLeaderEpoch);
                }
            }
        }

        public override void OnMemberLeftGroupChannel(string peerId)
        {
            // Local, first-notice that member is gone

            // LeaderSez needs to check to see if either the leader or the NextLeader was the member that left
            if (peerId == GroupLeaderId)
            {
                Logger.Info($"{this.GetType().Name}.OnMemberLeftGroupChannel(): GroupLeader is Gone!!!");
                // leader is gone! Failover.
                if (NextLeaderId != null)
                {
                    LocallyPromoteNextLeader();  // nulls nextleader, sets Leader
                    if (LocalPeerIsLeader) // <-- new leader
                    {
                        Logger.Info($"OnMemberLeftGroupChannel() ===== Local Peer taking over as LEADER!!");
                        SetNextNewCommandSequenceNumber(ApianInst.MaxAppliedCmdSeqNum+1); // <<=== This is IMPORTANT and kinda obtuse. And ugly.
                        string nextLeader = SelectNextLeader(new List<string>(){LocalPeerId, peerId});
                        if (nextLeader != null)
                            SendSetLeader(GroupId, nextLeader, CurrentEpochNum + LeaderTermLength );
                    }

                }
                else
                {
                     Logger.Warn($"{this.GetType().Name}.OnMemberLeftGroupChannel(): No Nextleader!!! Yikes!!!");
                }
            }
            else if ( peerId == NextLeaderId)
            {
                if (LocalPeerIsLeader)
                {
                    // set a new next - and extend our term
                    string nextLeader = SelectNextLeader(new List<string>(){LocalPeerId, peerId});
                    if (nextLeader != null)
                        SendSetLeader(GroupId, nextLeader, CurrentEpochNum + LeaderTermLength );
                }
            }

            base.OnMemberLeftGroupChannel(peerId);

        }

         public void OnSetLeaderMsg(ApianGroupMessage msg, string msgSrc, string msgChannel)
        {
            SetLeaderMsg slMsg = msg as SetLeaderMsg;

            Logger.Info($"{this.GetType().Name}.OnSetLeaderCmd() CurEpoch: {CurrentEpochNum}  New Leader: {SID(slMsg.newLeaderId)} at Epoch: {slMsg.newLeaderEpoch}");

            if ( slMsg.newLeaderEpoch <= CurrentEpochNum)
            {
                SetLeader(slMsg.newLeaderId);
            } else {
                SetNextLeader(slMsg.newLeaderId, slMsg.newLeaderEpoch);
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