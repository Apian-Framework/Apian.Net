
using System;
using System.Collections.Generic;
using UniLog;
using static UniLog.UniLogger; // for SID()

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


        public override void OnNewPeer(string remotePeerAddr)
        {
            if (!IsIdle && _apian.LocalPeerIsActive)
                SendApianClockOffset(); // tell the peer about us
        }

        public override void OnPeerLeft(string peerAddr)
        {
            if (_sysOffsetsByPeer.ContainsKey(peerAddr))
                _sysOffsetsByPeer.Remove(peerAddr);

            if (_apianOffsetsByPeer.ContainsKey(peerAddr))
                _apianOffsetsByPeer.Remove(peerAddr);

        }

        public override void OnPeerClockSync(string remotePeerAddr, long clockOffsetMs, long syncCount)
        {
            // This is a P2pNet sync ( lag and sys clock offset determination )
            Logger.Verbose($"OnPeerSync() from {SID(remotePeerAddr)}.");
            // save this
            _sysOffsetsByPeer[remotePeerAddr] = clockOffsetMs;
        }

        public override void OnPeerApianOffset(string peerAddr, long remoteApianOffset)
        {
            // peer is reporting it's local ApianClockOffset, and our local Apian instance is reporting
            // P2pNet's best estimate as to the remote peer's sysClock versus ours
            // ( ourSysTime + peerSysOffset = peerSysTime)
            // Using this we can infer what the difference is betwen our ApianClock and theirs.
            // and by "infer" I mean "kinda guess sorta": remoteApianClk = localSysMs + peerSysOffSet + peerApianOffset
            //
            Logger.Verbose($"OnApianClockOffset() from peer {SID(peerAddr)}");
            if (peerAddr == _apian.GroupMgr.LocalPeerAddr)
            {
                Logger.Verbose("OnApianClockOffset(). Ignoring local  message.");
                return;
            }

            // TODO: the idea is that Apian, before calling this, has verified that the sender is an Active group member
            // Maybe we should check anyway?

            _apianOffsetsByPeer[peerAddr] = remoteApianOffset;

            _UpdateForOtherPeers();

            // This is the old way, which set to match the first active peer we get a report from.
            // Instead, lets try waiting until we have reports from 1/2 of the Active members.
            //
            // if (IsIdle) // this is the first update we've gotten. Just set ours to match theirs.
            // {
            //     if (_sysOffsetsByPeer.ContainsKey(peerAddr)) // we need to have a current P2pNet sys clock offset
            //     {
            //         // CurrentTime = sysMs + peerOffset + peerAppOffset;
            //         DoSet( SystemTime + _sysOffsetsByPeer[peerAddr] + remoteApianOffset );
            //         Logger.Verbose($"OnApianClockOffset() - Was idle, set clock to match {SID(peerAddr)}");
            //     }
            // } else {
            //     // Do some complicated stuff to compute the "group" Apian clock value
            //     _UpdateForOtherPeers();
            // }
        }

        // Internals
        private void _UpdateForOtherPeers()
        {
            long peerTimeSum = 0;
            long localErrSum = 0;
            int peerCount = 0;
            foreach (string pid in _sysOffsetsByPeer.Keys)
            {
                try {
                    if (pid != _apian.GroupMgr.LocalPeerAddr)
                    {
                        long peerSysOff = _sysOffsetsByPeer[pid]; // ( ourTime + offset = peerTime)
                        long peerAppOff = _apianOffsetsByPeer[pid]; // will throw/continue if we don't have offset report for this one
                        // localErr = remoteAppClk - localAppClk = (sysMs + peerOffset + peerAppOffset) - CurrentTime
                        peerTimeSum += SystemTime + peerSysOff + peerAppOff; // if we're Idle
                        localErrSum += SystemTime + peerSysOff + peerAppOff - CurrentTime;
                        peerCount++;
                    }
                } catch(KeyNotFoundException) {}
            }

            if (peerCount == 0)
                return;

            if (IsIdle && peerCount < _apian.GroupMgr.ActiveMemberCount / 2)  // Don;t do initial set
            {
                Logger.Info($"_UpdateForOtherPeers(): Only {peerCount} members or the active {_apian.GroupMgr.ActiveMemberCount} have reported.");
                return;
            }

            if (IsIdle)
            {
                long newCurrentTime = peerTimeSum / peerCount;
                DoSet(newCurrentTime);
                Logger.Verbose($"First set ApianTime: {newCurrentTime}");
            } else {
                long localErrMs = localErrSum / peerCount;

                // Try to correct half of the error in kOffsetAnnounceBaseMs
                float newRate = 1.0f + (.5f * localErrMs / OffsetAnnouncementPeriodMs);
                DoSet(CurrentTime, newRate);
                Logger.Verbose($"Update: local error: {localErrMs}, New Rate: {newRate}");
            }

        }

    }

}