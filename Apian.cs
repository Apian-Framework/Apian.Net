
using System.Xml.Linq;
using System.Reflection.Emit;
using System;
using System.Linq;
using System.Collections.Generic;
using GameNet;
using UniLog;

namespace Apian
{
    public interface IApianClient 
    {
        void OnApianAssertion(ApianAssertion aa);
    }

    public abstract class ApianBase
    {
        protected Dictionary<string, Action<string, string, string, long>> ApMsgHandlers;
        public UniLogger logger; 

        public IApianGroupManager ApianGroup  {get; protected set;}    
        public IApianClock ApianClock {get; protected set;}  
        protected IGameNet GameNet {get; private set;}      
        protected long SysMs { get => DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond;}

        public ApianBase(IGameNet gn) {
            GameNet = gn;          
            logger = UniLogger.GetLogger("Apian");             
            ApMsgHandlers = new Dictionary<string, Action<string, string, string, long>>(); 
            // Add any truly generic handlers here          
        }

        public abstract void Update();
        
        // Apian Messages
        public abstract void OnApianMessage(string msgType, string msgJson, string fromId, string toId, long lagMs);         
        public abstract void SendApianMessage(string toChannel, ApianMessage msg);

        // Group-related
        public void AddGroupChannel(string channel) => GameNet.AddChannel(channel); // IApianGroupManager uses this. Maybe it should use GameNet directly?
        public void RemoveGroupChannel(string channel) => GameNet.RemoveChannel(channel);
        public abstract void OnMemberJoinedGroup(string peerId); // Any peer, including local. On getting this check with ApianGroup for details.
  
    }
  
    public enum VoteStatus
        {
        kVoting,
        kWon,
        kLost,  // timed out
        kNotFound  // Vote not found
    }

    public class VoteResult
    {
        public bool wasComplete = false; // if GetStatus is called without "viewOnly" when status was kWon 
                                        //or kLost then it is assumed that the voate has been
                                        // acted upon and this is set to "true"
        public VoteStatus status;
        public int yesVotes;
        public long timeStamp;

        public VoteResult(bool _isComplete, VoteStatus _status, int _yesVotes, long _timeStamp)
        {
            wasComplete = _isComplete;
            status = _status;
            yesVotes = _yesVotes;
            timeStamp = _timeStamp;
        }
    }

    public class ApianVoteMachine<T>
    {
        public const long kDefaultExpireMs = 300;        
        public const long kDefaultCleanupMs = 900;         
        public static long SysMs => DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond;  // don;t use apian time for wait/expire stuff     

        public struct VoteData
        {
            public bool IsComplete {get; private set;}
            public int NeededVotes {get; private set;}
            public long InitialMsgTime {get; private set;} // use this in any timestmped action resulting from the vote
            public long ExpireTs {get; private set;} // vote defaults to "no" after this
            public long CleanupTs {get; private set;} // VoteData gets removed after this
            public VoteStatus Status {get; private set;}
            public List<string> peerIds;
          
            public void UpdateStatus(long nowMs) 
            { 
                if (Status == VoteStatus.kVoting)
                {
                    if (nowMs > ExpireTs)
                        Status = VoteStatus.kLost;
                    else if (peerIds.Count >= NeededVotes)
                        Status = VoteStatus.kWon;
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
                Status = VoteStatus.kVoting;
                peerIds = new List<string>();   
            }

            public void AddVote(string peerId, long msgTime)
            {
                peerIds.Add(peerId);
                InitialMsgTime = msgTime < InitialMsgTime ? msgTime : InitialMsgTime; // use earliest
            }

            public void SetComplete() => IsComplete = true;
        }

        protected virtual int MajorityVotes(int peerCount) => peerCount / 2 + 1;
        protected Dictionary<T, VoteData> voteDict;
        protected long TimeoutMs {get; private set;}
        protected long CleanupMs {get; private set;}        
        public UniLogger logger;

        public ApianVoteMachine(long timeoutMs, long cleanupMs, UniLogger _logger=null) 
        { 
            TimeoutMs = timeoutMs;
            CleanupMs = cleanupMs;
            logger = _logger ?? UniLogger.GetLogger("ApianVoteMachine");
            voteDict = new Dictionary<T, VoteData>();
        }

        protected void UpdateAllStatus()
        {
            // remove old and forgotten ones
            voteDict = voteDict.Where(pair => pair.Value.CleanupTs >= SysMs)
                                 .ToDictionary(pair => pair.Key, pair => pair.Value);          

            // if timed out set status to Lost
            foreach (VoteData vote in voteDict.Values)
                vote.UpdateStatus(SysMs);

        }

        public void AddVote(T candidate, string votingPeer, long msgTime, int totalPeers)
        {
            UpdateAllStatus();
            VoteData vd;
            try {
                vd = voteDict[candidate];
                if (vd.Status == VoteStatus.kVoting)
                {
                    vd.AddVote(votingPeer, msgTime);
                    vd.UpdateStatus(SysMs);
                    voteDict[candidate] = vd; // VoteData is a struct (value) so must be re-added
                    logger.Debug($"Vote.Add: +1 for: {candidate.ToString()}, Votes: {vd.peerIds.Count}");
                }
            } catch (KeyNotFoundException) {
                int majorityCnt = MajorityVotes(totalPeers);                   
                vd = new VoteData(majorityCnt, msgTime, SysMs+TimeoutMs, SysMs+CleanupMs);
                vd.peerIds.Add(votingPeer);
                vd.UpdateStatus(SysMs);                
                voteDict[candidate] = vd;
                logger.Debug($"Vote.Add: New: {candidate.ToString()}, Majority: {majorityCnt}"); 
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
            VoteResult result = new VoteResult(false, VoteStatus.kNotFound, 0, 0);
            try {
                VoteData vd = voteDict[candidate];  
                result = new VoteResult(vd.IsComplete, vd.Status, vd.peerIds.Count, vd.InitialMsgTime);
                if (!justPeeking)
                {
                    if (vd.Status == VoteStatus.kLost || vd.Status == VoteStatus.kWon)
                    {
                        vd.SetComplete();
                        voteDict[candidate] = vd;
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