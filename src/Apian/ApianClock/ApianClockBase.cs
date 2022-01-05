
using System;
using System.Collections.Generic;
using UniLog;

namespace Apian
{
    public abstract class ApianClockBase : IApianClock
    {
        // IApianClock public stuff
        public bool IsIdle { get => (_currentRate == 0 && _apianTimeBase == 0);}  // hasn't been set yet
        public long SystemTime { get => DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond;}  // system clock
        public long CurrentTime { get => (long)((SystemTime - _sysMsBase) * _currentRate) + _apianTimeBase;} // This is the ApianTime
        public long SysClockOffset {get => CurrentTime - SystemTime; } // The current effective offset.

         public readonly UniLogger Logger;

        // Internal vars
        protected readonly ApianBase _apian;

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
        protected long _sysMsBase;  // the system time last time the rate was set
        protected long _apianTimeBase;  // this is the ApianTime when the clock was last set
        protected float _currentRate; // 0 is stopped, 1 is realtime

        // Local System->Apian offset is announced periodicaly
        protected const int OffsetAnnouncementPeriodMs = 10000; // TODO: make this settable
        protected long NewNextOffsetAnnounceTime {get =>  SystemTime + OffsetAnnouncementPeriodMs + new Random().Next(OffsetAnnouncementPeriodMs/4); }
        protected long _nextOffsetAnnounceTime;

        protected ApianClockBase(ApianBase apian)
        {
            _apian = apian;
            Logger = UniLogger.GetLogger("ApianClock");
        }

        public virtual void Update()
        {
            if (IsIdle)
                return;

            // Not the most sophisticated way. But easy to understand and implment in many languages.
            long nowMs = SystemTime;

            if (nowMs > _nextOffsetAnnounceTime)
            {
                SendApianClockOffset();
                _nextOffsetAnnounceTime = NewNextOffsetAnnounceTime;
            }
        }

        // Set the time
        public void SetTime(long desiredTimeMs, float rate=1.0f )
        {
            Logger.Verbose($"Set({desiredTimeMs}, Rate: {rate})");
            DoSet(desiredTimeMs, rate);
        }

        protected void DoSet(long desiredTimeMs, float rate=1.0f )
        {
            // We only want to send an annoucement if we explicitly set it
            _apianTimeBase = desiredTimeMs;
            _sysMsBase = SystemTime;
            _currentRate = rate;
        }

        protected virtual void SendApianClockOffset()
        {
            // Maybe this should be empty by default?
            if (_apian.GroupMgr != null)
            {
                Logger.Verbose($"SendApianClockOffset() - Current Time: {CurrentTime}");
                _apian.SendApianMessage(_apian.GroupMgr.GroupId, new ApianClockOffsetMsg( _apian.GroupMgr.GroupId, _apian.GroupMgr.LocalPeerId, SysClockOffset));
            }
        }

        // Clocks neeed to implement these
        public abstract void OnNewPeer(string remotePeerId, long clockOffsetMs, long netLagMs);
        public abstract void OnPeerLeft(string peerId);

        public abstract void OnPeerApianOffset(string remotePeerId, long apianOffset); // apian offset is the difference between the reporting
                                                                                        // peer's system and Apian clocks. It does NOT
                                                                                        // assume anything about the local peer's system or
                                                                                        // Apian clocks.
        public abstract void OnPeerClockSync(string remotePeerId, long clockOffsetMs, long netLagMs); // clock offset is the estimated offset
                                                                                                      // between the 2 peers' system clocks
                                                                                                      // as reported by P2pNet


    }

}