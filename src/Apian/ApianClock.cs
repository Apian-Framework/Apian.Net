
using System;
using System.Collections.Generic;
using UniLog;

namespace Apian
{

    public interface IApianClock
    {
        // ReSharper disable UnusedMemberInSuper.Global,UnusedMember.Global
        long CurrentTime { get;} // This is the ApianTime
        void Set(long desiredTimeMs, float rate=1.0f );
        bool IsIdle { get;}  // hasn't been set yet
        long SystemTime { get;}  // system clock
        long SysClockOffset {get; } // The current effective offset from system time
        void OnP2pPeerSync(string remotePeerId, long clockOffsetMs, long netLagMs); // sys + offset = apian
        void SendApianClockOffset(); // Another part of Apian might want us to send this ( when member joins, for instance)
        void OnApianClockOffset(string remotePeerId,  long apianOffset);
        void OnPeerLeft(string peerId);
        void Update(); // loop
        // ReSharper enable UnusedMemberInSuper.Global,UnusedMember.Global

    }

    // ReSharper disable once UnusedType.Global
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

        public readonly UniLogger Logger;

        // Internal vars
        private readonly ApianBase _apian;

        private long _sysMsBase;  // the system time last time the rate was set
        private long _apianTimeBase;  // this is the ApianTime when the clock wa last set
        private float _currentRate;


        private const int OffsetAnnouncementPeriodMs = 10000; //
        private long NewNextOffsetAnnounceTime {get =>  SystemTime + OffsetAnnouncementPeriodMs + new Random().Next(OffsetAnnouncementPeriodMs/4); }
        private long _nextOffsetAnnounceTime;

        public DefaultApianClock(ApianBase apian)
        {
            _apian = apian;
            _sysOffsetsByPeer = new Dictionary<string, long>();
            _apianOffsetsByPeer = new Dictionary<string, long>();
            Logger = UniLogger.GetLogger("ApianClock");
        }

        // Keeping track of peers.
        private readonly Dictionary<string, long> _sysOffsetsByPeer;
        private readonly Dictionary<string, long> _apianOffsetsByPeer;

        // IApianClock public stuff
        public bool IsIdle { get => (_currentRate == 0 && _apianTimeBase == 0);}  // hasn't been set yet
        public long SystemTime { get => DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond;}  // system clock
        public long CurrentTime { get => (long)((SystemTime - _sysMsBase) * _currentRate) + _apianTimeBase;} // This is the ApianTime
        public long SysClockOffset {get => CurrentTime - SystemTime; } // The current effective offset.

        public void Update()
        {
            if (IsIdle)
                return;

            // Not the most sophisticated way. But easy to understand and implment in many languages.
            long nowMs = SystemTime;

            if (nowMs > _nextOffsetAnnounceTime)
                SendApianClockOffset();

        }

        // Set the time
        public void Set(long desiredTimeMs, float rate=1.0f )
        {
            Logger.Verbose("Set()");
            _apianTimeBase = desiredTimeMs;
            _sysMsBase = SystemTime;
            _currentRate = rate;
        }

        public void OnP2pPeerSync(string remotePeerId, long clockOffsetMs, long netLagMs) // sys + offset = apian
        {
            // This is a P2pNet sync ( lag and sys clock offset determination )
            Logger.Verbose($"OnPeerSync() from {remotePeerId}.");
            // save this
            _sysOffsetsByPeer[remotePeerId] = clockOffsetMs;
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
            Logger.Verbose($"OnApianClockOffset() from peer {p2pId}");
            if (p2pId == _apian.GroupMgr.LocalPeerId)
            {
                Logger.Verbose("OnApianClockOffset(). Oops. It's me. Bailing");
                return;
            }

            _apianOffsetsByPeer[p2pId] = remoteApianOffset;

            if (IsIdle) // this is the first we've gotten. Set set ours to match theirs.
            {
                if (_sysOffsetsByPeer.ContainsKey(p2pId)) // we need to have a current P2pNet sys clock offset
                {
                    // CurrentTime = sysMs + peerOffset + peerAppOffset;
                    Set( SystemTime + _sysOffsetsByPeer[p2pId] + remoteApianOffset );
                    Logger.Verbose($"OnApianClockOffset() - Set clock to match {p2pId}");
                }
            } else {
                UpdateForOtherPeers();
            }
        }

        public void OnPeerLeft(string peerId)
        {
            if (_sysOffsetsByPeer.ContainsKey(peerId))
                _sysOffsetsByPeer.Remove(peerId);

            if (_apianOffsetsByPeer.ContainsKey(peerId))
                _apianOffsetsByPeer.Remove(peerId);
        }

        // Internals

        public void SendApianClockOffset()
        {
            if (_apian.GroupMgr != null)
            {
                Logger.Verbose($"SendApianClockOffset() - Current Time: {CurrentTime}");
                _apian.SendApianMessage(_apian.GroupMgr.GroupId, new ApianClockOffsetMsg( _apian.GroupMgr.GroupId, _apian.GroupMgr.LocalPeerId, SysClockOffset));
            }
            _nextOffsetAnnounceTime = NewNextOffsetAnnounceTime;
        }

        private void UpdateForOtherPeers()
        {
            long localErrSum = 0;
            int peerCount = 0;
            foreach (string pid in _sysOffsetsByPeer.Keys)
            {
                try {
                    if (pid != _apian.GroupMgr.LocalPeerId)
                    {
                        long peerSysOff = _sysOffsetsByPeer[pid]; // ( ourTime + offset = peerTime)
                        long peerAppOff = _apianOffsetsByPeer[pid];
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
                float newRate = 1.0f + (.5f * localErrMs / OffsetAnnouncementPeriodMs);
                Set(CurrentTime, newRate);
                Logger.Verbose($"Update: local error: {localErrMs}, New Rate: {newRate}");
            }
            else
            {
                Logger.Verbose("Update: No other peers.");
            }
        }

    }

}