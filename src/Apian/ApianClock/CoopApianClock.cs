
using System;
using System.Collections.Generic;
using UniLog;

namespace Apian
{
    // ReSharper disable once UnusedType.Global
    public class CoopApianClock : ApianClockBase
    {

        public CoopApianClock(ApianBase apian) : base(apian)
        {
            _sysOffsetsByPeer = new Dictionary<string, long>();
            _apianOffsetsByPeer = new Dictionary<string, long>();
        }

        // Keeping track of peers.
        private readonly Dictionary<string, long> _sysOffsetsByPeer;
        private readonly Dictionary<string, long> _apianOffsetsByPeer;


        public override void OnNewPeer(string remotePeerId, long clockOffsetMs, long netLagMs)
        {
            OnPeerClockSync(remotePeerId, clockOffsetMs, netLagMs);
            if (!IsIdle)
                SendApianClockOffset(); // tell the peer about us
        }

        public override void OnPeerClockSync(string remotePeerId, long clockOffsetMs, long netLagMs) // localSys + offset = PeerSys
        {
            // This is a P2pNet sync ( lag and sys clock offset determination )
            Logger.Verbose($"OnPeerSync() from {remotePeerId}.");
            // save this
            _sysOffsetsByPeer[remotePeerId] = clockOffsetMs;
        }

        public override void OnPeerApianOffset(string p2pId,  long remoteApianOffset)
        {
            // peer is reporting it's local apian offset (SysClockOffset to the peer, peerAppOffset to us)
            // By using P2pNet's estimate for that peer's system offset vs our system clock
            // ( ourSysTime + peerOffset = peerSysTime)
            // we can infer what the difference is betwen our ApianClock and theirs.
            // and by "infer" I mean "kinda guess sorta"
            // remoteApianClk = sysMs + peerOffSet + peerApianOffset
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
                    _DoSet( SystemTime + _sysOffsetsByPeer[p2pId] + remoteApianOffset );
                    Logger.Verbose($"OnApianClockOffset() - Set clock to match {p2pId}");
                }
            } else {
                UpdateForOtherPeers();
            }
        }

        public override void OnPeerLeft(string peerId)
        {
            if (_sysOffsetsByPeer.ContainsKey(peerId))
                _sysOffsetsByPeer.Remove(peerId);

            if (_apianOffsetsByPeer.ContainsKey(peerId))
                _apianOffsetsByPeer.Remove(peerId);

            base.OnPeerLeft(peerId);
        }

        // Internals
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
                _DoSet(CurrentTime, newRate);
                Logger.Verbose($"Update: local error: {localErrMs}, New Rate: {newRate}");
            }
            else
            {
                Logger.Verbose("Update: No other peers.");
            }
        }

    }

}