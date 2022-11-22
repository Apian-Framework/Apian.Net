using System;
using System.Linq;
using System.Collections.Generic;
using UniLog;

namespace Apian
{
   public enum VoteStatus
        {
        Voting,
        Won,
        Lost,  // timed out
        NotFound  // Vote not found
    }

    public class VoteResult
    {
        // ReSharper disable MemberCanBePrivate.Global,UnusedMember.Global,NotAccessedField.Global
        public readonly bool WasComplete; // if GetStatus is called without "viewOnly" when status was kWon
                                        //or kLost then it is assumed that the voate has been
                                        // acted upon and this is set to "true"
        public readonly VoteStatus Status;
        public readonly int YesVotes;
        public readonly long TimeStamp;

        public VoteResult(bool isComplete, VoteStatus status, int yesVotes, long timeStamp)
        {
            WasComplete = isComplete;
            Status = status;
            YesVotes = yesVotes;
            TimeStamp = timeStamp;
        }
        // ReSharper enable MemberCanBePrivate.Global,UnusedMember.Global,NotAccessedField.Globala
    }

    public class ApianVoteMachine<T>
    {
        public const long DefaultExpireMs = 300;
        public const long DefaultCleanupMs = 900;
        private static long SysMs => DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond;  // don;t use apian time for wait/expire stuff

        private struct VoteData
        {
            public bool IsComplete {get; private set;}
            public int NeededVotes {get;}
            public long InitialMsgTime {get; private set;} // use this in any timestmped action resulting from the vote
            public long ExpireTs {get;} // vote defaults to "no" after this
            public long CleanupTs {get;} // VoteData gets removed after this
            public VoteStatus Status {get; private set;}
            public readonly List<string> PeerAddrs;

            public void UpdateStatus(long nowMs)
            {
                if (Status == VoteStatus.Voting)
                {
                    if (nowMs > ExpireTs)
                        Status = VoteStatus.Lost;
                    else if (PeerAddrs.Count >= NeededVotes)
                        Status = VoteStatus.Won;
                }
            }

            public VoteData(int voteCnt, long msgTime, long expireTimeMs, long cleanupTimeMs)
            {
                IsComplete = false; // When GetResult() is called and the status is Won or Lost then this is set,
                                    // indicating that if it gets read again (another yes vote comes in) it should
                                    // NOT be acted upon. By the same token, we do not want to *delete* the vote,
                                    // since a late vote will re-add it. We want the vote to be cleaned up automatically
                                    // after a suitable time.
                InitialMsgTime = msgTime;
                NeededVotes = voteCnt;
                ExpireTs = expireTimeMs;
                CleanupTs = cleanupTimeMs;
                Status = VoteStatus.Voting;
                PeerAddrs = new List<string>();
            }

            public void AddVote(string peerAddr, long msgTime)
            {
                PeerAddrs.Add(peerAddr);
                InitialMsgTime = msgTime < InitialMsgTime ? msgTime : InitialMsgTime; // use earliest
            }

            public void SetComplete() => IsComplete = true;
        }

        private int _MajorityVotes(int peerCount) => peerCount / 2 + 1;
        private Dictionary<T, VoteData> _voteDict;
        protected long TimeoutMs {get;}
        protected long CleanupMs {get;}
        public readonly UniLogger Logger;

        public ApianVoteMachine(long timeoutMs, long cleanupMs, UniLogger logger=null)
        {
            TimeoutMs = timeoutMs;
            CleanupMs = cleanupMs;
            Logger = logger ?? UniLogger.GetLogger("ApianVoteMachine");
            _voteDict = new Dictionary<T, VoteData>();
        }

        protected void UpdateAllStatus()
        {
            // remove old and forgotten ones
            _voteDict = _voteDict.Where(pair => pair.Value.CleanupTs >= SysMs)
                                 .ToDictionary(pair => pair.Key, pair => pair.Value);

            // if timed out set status to Lost
            foreach (VoteData vote in _voteDict.Values)
                vote.UpdateStatus(SysMs);

        }

        public void AddVote(T candidate, string votingPeer, long msgTime, int totalPeers)
        {
            UpdateAllStatus();
            VoteData vd;
            try {
                vd = _voteDict[candidate];
                if (vd.Status == VoteStatus.Voting)
                {
                    vd.AddVote(votingPeer, msgTime);
                    vd.UpdateStatus(SysMs);
                    _voteDict[candidate] = vd; // VoteData is a struct (value) so must be re-added
                    Logger.Debug($"Vote.Add: +1 for: {candidate}, Votes: {vd.PeerAddrs.Count}");
                }
            } catch (KeyNotFoundException) {
                int majorityCnt = _MajorityVotes(totalPeers);
                vd = new VoteData(majorityCnt, msgTime, SysMs+TimeoutMs, SysMs+CleanupMs);
                vd.PeerAddrs.Add(votingPeer);
                vd.UpdateStatus(SysMs);
                _voteDict[candidate] = vd;
                Logger.Debug($"Vote.Add: New: {candidate}, Majority: {majorityCnt}");
            }
        }

        // public void DoneWithVote(T candidate)
        // {
        //     try {
        //         voteDict.Remove(candidate);
        //     } catch (KeyNotFoundException) {}
        // }

        public VoteResult GetResult(T candidate, bool justPeeking=false)
        {
            // Have to get to it before it expires
            // If the vote is finished (timed out or won)
            // this will set the status to Done - which means it has been
            // read
            UpdateAllStatus();
            VoteResult result = new VoteResult(false, VoteStatus.NotFound, 0, 0);
            try {
                VoteData vd = _voteDict[candidate];
                result = new VoteResult(vd.IsComplete, vd.Status, vd.PeerAddrs.Count, vd.InitialMsgTime);
                if (!justPeeking)
                {
                    if (vd.Status == VoteStatus.Lost || vd.Status == VoteStatus.Won)
                    {
                        vd.SetComplete();
                        _voteDict[candidate] = vd;
                    }
                }
            } catch (KeyNotFoundException)
            {
                //logger.Warn($"GetStatus: Vote not found");
            }
            return result;
        }

    }
}