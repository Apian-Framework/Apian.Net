
using System.Text.RegularExpressions;
using System.Reflection.Emit;
using System;
using System.Linq;
using System.Collections.Generic;
using GameNet;
using UniLog;

namespace Apian
{

    public interface IApianClock
    {
        long CurrentTime { get;} // This is the ApianTime 
        void Set(long desiredTimeMs, float rate=1.0f );                
        bool IsIdle { get;}  // hasn't been set yet
        long SystemTime { get;}  // system clock        
        long SysClockOffset {get; } // The current effective offset from system time
        void OnPeerSync(string remotePeerId, long clockOffsetMs, long netLagMs); // sys + offset = apian   
        void SendApianClockOffset(); // Another part of Apian might want us to send this ( when member joins, for instance)
        void OnApianClockOffset(string remotePeerId,  long apianOffset);          
        void Update(); // loop 
    }

    public class DefaultApianClock : IApianClock
    {
        //
        // Clock rate can be adjusted
        //
        // reported time is:
        //  time = (sysMs - sysMsBase) * timeMult + timeOffset
        // where:
        //   sysMs - current system time
        //   sysMsBase - system time when the rate or offset was last set
        //   time - clock rate. 1.0 is real time
        //   timeOffset - the time you wanted it to be last time you set the rate or offset
        //
        //   set rate and offset at the same time.

        // TODO: should get notified of peerleft?

        public UniLogger logger; 

        // Internal vars
        protected ApianBase apian;
        protected long SysMsBase {get; private set;}  // the system time last time the rate was set
        protected long ApianTimeBase {get; private set;}  // this is the ApianTime when the clock wa last set
        protected float CurrentRate {get; private set;} = 0.0f;


        protected const int kOffsetAnnounceBaseMs = 10000; // 
        protected long NewNextOffsetAnnounceTime {get =>  SystemTime + kOffsetAnnounceBaseMs + new Random().Next(kOffsetAnnounceBaseMs/4); } 
        protected long nextOffsetAnnounceTime = 0;

        public DefaultApianClock(ApianBase _apian)
        {
            apian = _apian;
            sysOffsetsByPeer = new Dictionary<string, long>();
            apianOffsetsByPeer = new Dictionary<string, long>();   
            logger = UniLogger.GetLogger("ApianClock");         
        }

        // Keeping track of peers.
        protected Dictionary<string, long> sysOffsetsByPeer;
        protected Dictionary<string, long> apianOffsetsByPeer;    

        // IApianClock public stuff                   
        public bool IsIdle { get => (CurrentRate == 0.0f && ApianTimeBase == 0);}  // hasn't been set yet
        public long SystemTime { get => DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond;}  // system clock        
        public long CurrentTime { get => (long)((SystemTime - SysMsBase) * CurrentRate) + ApianTimeBase;} // This is the ApianTime
        public long SysClockOffset {get => CurrentTime - SystemTime; } // The current effective offset.  

        public void Update()
        {
            if (IsIdle)
                return;

            // Not the most sophisticated way. But easy to understand and implment in many languages.
            long nowMs = SystemTime;

            if (nowMs > nextOffsetAnnounceTime)
                SendApianClockOffset();

        }

        // Set the time
        public void Set(long desiredTimeMs, float rate=1.0f )
        {
            logger.Verbose($"Set()");
            ApianTimeBase = desiredTimeMs;
            SysMsBase = SystemTime;
            CurrentRate = rate;
        }

        public void OnPeerSync(string remotePeerId, long clockOffsetMs, long netLagMs) // sys + offset = apian
        {
            // This is a P2pNet sync ( lag and sys clock offset determination )
            logger.Verbose($"OnPeerSync() from {remotePeerId}.");
            // save this
            sysOffsetsByPeer[remotePeerId] = clockOffsetMs;
        }

        public void OnApianClockOffset(string p2pId,  long remoteApianOffset)
        {
            // peer is reporting it's local apian offset (SysClockOffset to the peer, peerAppOffset to us)
            // By using P2pNet's estimate for that peer's system offset vs our system clock
            // ( ourSysTime + peerOffset = peerSysTime)
            // we can infer what the difference is betwen our ApianClock and theirs.
            // and by "infer" I mean "kinda guess sorta"
            // remoteAppClk = sysMs + peerOffSet + peerAppOffset
            //
            logger.Verbose($"OnApianClockOffset() from peer {p2pId}");   
            if (p2pId == apian.ApianGroup.LocalP2pId)
            {
                logger.Verbose($"OnApianClockOffset(). Oops. It's me. Bailing"); 
                return;
            }

            apianOffsetsByPeer[p2pId] = remoteApianOffset;

            if (IsIdle) // this is the first we've gotten. Set set ours to match theirs.
            {
                if (sysOffsetsByPeer.ContainsKey(p2pId)) // we need to have a current P2pNet sys clock offset
                {
                    // CurrentTime = sysMs + peerOffset + peerAppOffset;
                    Set( SystemTime + sysOffsetsByPeer[p2pId] + remoteApianOffset );
                    logger.Verbose($"OnApianClockOffset() - Set clock to match {p2pId}"); 
                }
            } else {
                UpdateForOtherPeers();
            }
        }        

        // Internals


        public void SendApianClockOffset()
        {
            if (apian.ApianGroup != null) 
            {
                logger.Verbose($"SendApianClockOffset() - Current Time: {CurrentTime}");                
                apian.SendApianMessage(apian.ApianGroup.GroupId, new ApianClockOffsetMsg( apian.ApianGroup.LocalP2pId, SysClockOffset));
            }
            nextOffsetAnnounceTime = NewNextOffsetAnnounceTime;
        }

        protected void UpdateForOtherPeers()
        {
            long localErrSum = 0;
            int peerCount = 0;
            long localApOff = SysClockOffset;
            foreach (string pid in sysOffsetsByPeer.Keys)
            {
                try {
                    if (pid != apian.ApianGroup.LocalP2pId)
                    {
                        long peerSysOff = sysOffsetsByPeer[pid]; // ( ourTime + offset = peerTime)
                        long peerAppOff = apianOffsetsByPeer[pid];
                        // localErr = remoteAppClk - localAppClk = (sysMs + peerOffset + peerAppOffset) - CurrentTime
                        localErrSum += SystemTime + peerSysOff + peerAppOff - CurrentTime;
                        peerCount++;
                    }
                } catch(KeyNotFoundException) {}
            }
            
            if (peerCount > 0)
            {
                long localErrMs = localErrSum / peerCount;
                
                // Try to correct half of the error in kOffsetAnnounceBaseMs 
                float newRate = 1.0f + (.5f * (float)localErrMs / kOffsetAnnounceBaseMs);
                Set(CurrentTime, newRate);
                logger.Verbose($"Update: local error: {localErrMs}, New Rate: {newRate}");                
            } 
            else
            {
                logger.Verbose($"Update: No other peers.");
            }           
        }

    }

}