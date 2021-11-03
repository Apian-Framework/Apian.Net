using System.Linq;
using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using UniLog;
using static UniLog.UniLogger; // for SID

namespace Apian
{

    public class RatfishElection
    {
        public enum RfRole : int {
            kIdle = 0,
            kFollower = 1,
            kCandidate = 2,
            kLeader = 3,
        };

        protected class RfTimer
        {
            protected static System.Random RandInst = new System.Random();
            protected static long RangeRand(long low, long high) {return (long)(RandInst.NextDouble() * (high - low) + low);}

            protected static Func<long> NowMs = () => DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond; // resassign for testing

            public long MinTimeoutMs {get; private set;}
            public long MaxTimeoutMs {get; private set;}
            public long CurExpiryTimeMs {get; private set;}

            public bool IsExpired => CurExpiryTimeMs > 0 && CurExpiryTimeMs < NowMs();

            public RfTimer(long minTimeoutMs, long maxTimeoutMs=0)
            {
                MinTimeoutMs = minTimeoutMs;
                MaxTimeoutMs = maxTimeoutMs == 0 ? minTimeoutMs : maxTimeoutMs;
                CurExpiryTimeMs = 0;
            }

            public void Disable() { CurExpiryTimeMs = 0;}
            public void Start() { CurExpiryTimeMs = NowMs() + RangeRand(MinTimeoutMs, MaxTimeoutMs); }
            public void Reset()
            {
                // Must already have been Started, or it does nothing
                if (CurExpiryTimeMs > 0)
                    CurExpiryTimeMs = NowMs() + RangeRand(MinTimeoutMs, MaxTimeoutMs);
            }

        }

        protected class VotingData
        {
            // data needed while running and election or pre-vote
            public int requiredVotes; // how many votes what was a "majority" when vot Started
            public int yeas; // if either gets to required then it's over
            public int nays;
            protected RfTimer voteTimer; // uses electioTimeout times.

            public VotingData(int _requiredVotes, long minTimeoutMs, long maxTimeoutMs)
            {
                requiredVotes = _requiredVotes;
                voteTimer = new RfTimer(minTimeoutMs, maxTimeoutMs);
            }

            public bool Succeeded {get => yeas >= requiredVotes; }
            public bool Failed {get => (nays >= requiredVotes || (voteTimer.IsExpired && yeas < requiredVotes)); }

            public void SubmitVote(bool yesVote)
            {
                if (!voteTimer.IsExpired)
                {
                    yeas += yesVote ? 1 : 0;
                    nays += yesVote ? 0 : 1;
                }
            }

        }

        public Dictionary<string, string> DefaultConfig = new Dictionary<string, string>()
        {
            {"HeartbeatMs", "250"},  // if local peer is leader and hasn't sent a command or Hb in this long, send a Hb
            {"MinElectionTimeoutMs", "500"}, //  election timeout is random from this to max
            {"MaxElectionTimeoutMs", "750"}, //
        };

        public Dictionary<RfRole, Action> RoleUpdateFuncs;

        private static long SysMsNow => DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond; // FIXME: replace with testable construct

        // raft election state stuff
        public RfRole LocalRole { get; private set;}
        public string CurrentLeaderId { get; private set; } // TODO: In proper RAFT, there is no "LeaderId" state var. Consider removing? It is currently never read.
        public long CurrentTerm { get; private set; }
        protected string VotedFor; // this term

        // Timers
        protected RfTimer HeartbeatTimer; // for Leader only - if expired broadcast a heartbeat
        protected RfTimer ElectionTimer; // if expires, followers should think about becoming candidates


        // env
        protected IApianGroupManager GroupMgr;
        protected ApianBase ApianInst;

        // config
        private long HeartbeatMs;
        private long MinElectionTimeoutMs;
        private long MaxElectionTimeoutMs;

        public UniLogger Logger;

        public RatfishElection(IApianGroupManager _groupMgr, ApianBase _apianInst,  Dictionary<string,string> _config = null)
        {
            Logger = UniLogger.GetLogger("RatfishElection");

            RoleUpdateFuncs = new Dictionary<RfRole, Action>()
            {
                {RfRole.kIdle, () => {} },
                {RfRole.kFollower, FollowerUpdate },
                {RfRole.kLeader, LeaderUpdate },
            };

            GroupMgr = _groupMgr;
            ApianInst = _apianInst;
            _ParseConfig(_config ?? DefaultConfig);

            HeartbeatTimer = new RfTimer(HeartbeatMs); // Timers are initially disabled
            ElectionTimer = new RfTimer(MinElectionTimeoutMs, MaxElectionTimeoutMs);

            CurrentTerm = 0;
            LocalRole = RfRole.kIdle;
            CurrentLeaderId = null;
            VotedFor = null;
        }

        private void _ParseConfig(Dictionary<string,string> configDict)
        {
            HeartbeatMs = int.Parse(configDict["HeartbeatMs"]);  // timeout when leader should SEND
            MinElectionTimeoutMs = int.Parse(configDict["MinElectionTimeoutMs"]);
            MaxElectionTimeoutMs = int.Parse(configDict["MaxElectionTimeoutMs"]);
        }

        protected void StartFollowerRole()
        {
            LocalRole = RfRole.kFollower;
            HeartbeatTimer.Disable();
            ElectionTimer.Start();
        }

        protected void StartCandidateRole()
        {

        }
        protected void StartLeaderRole()
        {
            LocalRole = RfRole.kLeader;
            ElectionTimer.Disable(); // TODO:  is this correct?
            HeartbeatTimer.Start();
            SendHeartbeat(); // starts heartbeat timer
        }


        public void Update()
        {
            RoleUpdateFuncs[LocalRole]();
        }

        public void FollowerUpdate()
        {
            if (ElectionTimer.IsExpired)
            {
                Logger.Warn($"Election timer expired!");
                StartPreVote();
            }
        }

        public void LeaderUpdate()
        {
            if (HeartbeatTimer.IsExpired)
                SendHeartbeat();
        }


        protected void StartPreVote()
        {
            // start an non-binding "election" while still a follower to see if a majority will vote for you

        }


        protected void SendHeartbeat()
        {
            ApianInst.GameNet.SendApianMessage(GroupMgr.GroupId,
                new RatfishHeartBeatMsg(GroupMgr.GroupId, CurrentTerm, ApianInst.MaxReceivedCmdSeqNum) );
        }

        public void OnApianCommand(ApianCommand cmd, string msgSrc)
        {
            RatfishApianCommand rCmd = cmd as RatfishApianCommand;
            Logger.Verbose($"Cmd (heartbeat) from {SID(msgSrc)}");
            OnHeartbeat( msgSrc, rCmd.ElectionTerm, rCmd.SequenceNum );
        }

        public void OnVoteRequestMsg(ApianGroupMessage msg, string msgSrc)
        {

        }
        public void OnVoteReplyMsg(ApianGroupMessage msg, string msgSrc)
        {

        }

        public void OnHeartbeatMsg(ApianGroupMessage msg, string msgSrc)
        {
            RatfishHeartBeatMsg hbMsg = msg as RatfishHeartBeatMsg; // message is From group leader 'cause its ratfish
            Logger.Verbose($"Heartbeat from {SID(msgSrc)}");
            OnHeartbeat( msgSrc, hbMsg.ElectionTerm, hbMsg.LastCmdSeqNum);
        }

        protected void OnHeartbeat(string leaderId, long electionTerm, long lastCmdSeqNum)
        {
            // Important point: In RAFT AppendEntries() is acked with success and the recipient's term.

            // In all cases, if a higher term shows up, store it and become (even if already) a follower
            if (electionTerm > CurrentTerm)
            {
                CurrentTerm = electionTerm;
                CurrentLeaderId = leaderId; // see elsewhere: it's not clear there should even be a CurrentLeader var
                StartFollowerRole();
                return;
            }


            switch (LocalRole)
            {
            case RfRole.kFollower:
                if (electionTerm == CurrentTerm)
                    ElectionTimer.Reset();
                break;

            case RfRole.kLeader:
                HeartbeatTimer.Reset();
                break;
            }
        }

        public void OnGroupJoined(string leader, long electionTerm)
        {
            // received when GroupMemberJoined() acceptance is receivd for local peer
            // Starts everythig up
            CurrentLeaderId = leader;
            VotedFor = leader;
            CurrentTerm = electionTerm;

            if (leader == GroupMgr.LocalPeerId)
                StartLeaderRole(); // For straight RAFT, everyone should start as follower
            else
                StartFollowerRole();

        }

    }

    //
    // Messages
    //
    public class RatfishApianCommand : ApianCommand
    {
        // Like a regular command, but with "ElectionTerm" in it
        public long ElectionTerm;

        public RatfishApianCommand(long term, long ep, long seqNum, string gid, ApianCoreMessage coreMsg) : base(ep, seqNum, gid, coreMsg)
        {
            ElectionTerm = term;
        }
        public RatfishApianCommand(long term, long ep, long seqNum, ApianWrappedMessage wrappedMsg) : base(ep, seqNum, wrappedMsg)
        {
            ElectionTerm = term;
        }
        public RatfishApianCommand() : base() {}   // need this for NewtonSoft.Json to work
    }

    public class RatfishHeartBeatMsg : ApianGroupMessage
    {
        public const string MsgTypeId = "APGrRfHb";
        public long ElectionTerm;
        public long LastCmdSeqNum;
        public RatfishHeartBeatMsg(string gid, long electionTerm, long lastCmdSeqNum) : base(gid, MsgTypeId)
        {
            ElectionTerm = electionTerm;
            LastCmdSeqNum = lastCmdSeqNum;
        }
    }

    public class RatfishVoteRequestMsg : ApianGroupMessage
    {
        public const string MsgTypeId = "APGrRfVq";
        public bool IsPreVote;
        public long ElectionTerm;
        public long LastCmdSeqNum; // TODO: figure out log-matching requirements
        public RatfishVoteRequestMsg(string gid, bool isPre, long electionTerm, long lastCmdSeqNum) : base(gid, MsgTypeId)
        {
            IsPreVote = isPre;
            ElectionTerm = electionTerm;
            LastCmdSeqNum = lastCmdSeqNum;
        }
    }

    public class RatfishVoteReplyMsg : ApianGroupMessage
    {
        public const string MsgTypeId = "APGrRfVr";
        public long VoterTerm;
        public bool VoteGranted;
        public RatfishVoteReplyMsg(string gid, long localTerm, bool isGranted) : base(gid, MsgTypeId)
        {
            VoterTerm = localTerm;
            VoteGranted = isGranted;
        }
    }

    public class RatfishMemberJoinedMsg : GroupMemberJoinedMsg  // override/extension
    {
        public long ElectionTerm;
        public RatfishMemberJoinedMsg(string gid, string pid, string peerData, long electionTerm) : base(gid, pid, peerData)
        {
            ElectionTerm = electionTerm;
        }
    }

    static public class RatfishMessageDeserializer
    {
        public static Dictionary<string, Func<string, ApianMessage>> rfDeserializers = new  Dictionary<string, Func<string, ApianMessage>>()
        {
            {ApianMessage.CliCommand, (s) => JsonConvert.DeserializeObject<RatfishApianCommand>(s) },
        };

        public static Dictionary<string, Func<string, ApianMessage>> rfGroupMsgDeserializers = new  Dictionary<string, Func<string, ApianMessage>>()
        {
            {ApianGroupMessage.GroupMemberJoined, (s) => JsonConvert.DeserializeObject<RatfishMemberJoinedMsg>(s) },  // msg override
            {RatfishHeartBeatMsg.MsgTypeId, (s) => JsonConvert.DeserializeObject<RatfishHeartBeatMsg>(s) },
            {RatfishVoteRequestMsg.MsgTypeId, (s) => JsonConvert.DeserializeObject<RatfishVoteRequestMsg>(s) },
            {RatfishVoteReplyMsg.MsgTypeId, (s) => JsonConvert.DeserializeObject<RatfishVoteReplyMsg>(s) },
        };
        private static ApianMessage _DeserRatfishGroupMsg(ApianGroupMessage genGrpMsg, string msgJson)
        {
            return rfGroupMsgDeserializers.ContainsKey(genGrpMsg.GroupMsgType)
                ? rfGroupMsgDeserializers[genGrpMsg.GroupMsgType](msgJson) as ApianMessage
                    : null;
        }

        public static ApianMessage FromJSON(ApianMessage genMsg, string json)
        {
            ApianMessage res = null;

            if (genMsg.MsgType == ApianMessage.GroupMessage)
                res = _DeserRatfishGroupMsg((genMsg as ApianGroupMessage), json); // check to see if it's a locally-defined GroupMessage

            if (res == null && rfDeserializers.ContainsKey(genMsg.MsgType))
                res = rfDeserializers[genMsg.MsgType](json) as ApianMessage;

            return res ?? genMsg;

        }
    }
}