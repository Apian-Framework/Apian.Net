
using System;
using System.Reflection;
using System.Collections.Generic;
using UniLog;
using static UniLog.UniLogger; // for SID

namespace Apian
{
    //ReSharper disable once UnusedType.Global
    public class LeaderApianClock : ApianClockBase
    {
        protected string GroupLeaderId => (_apian.GroupMgr as LeaderDecidesGmBase).GroupLeaderId;

        private bool _leaderClockSynced; // will often not be true before getting an ApianOffset msg
        private bool _leaderApianOffsetReported;
        private long _leaderSysOffset;
        private long  _leaderApianOffset;

        public LeaderApianClock(ApianBase apian) : base(apian)
        {
            // cant test GroupLeaderId here because clock is not creatd with Apian instance
        }

        public override void OnNewPeer(string remotePeerId)
        {
            Logger.Verbose($"OnNewPeer() from {remotePeerId}");
            if (!IsIdle && _apian.LocalPeerIsActive)
            {
                if (_apian.GroupMgr.LocalPeerId == GroupLeaderId)
                    SendApianClockOffset(); // announce our apian offset for the remote peer
            }
        }

        public override void OnPeerLeft(string peerId)
        {
            // Nothing to do, here.
        }

        public override void OnPeerClockSync(string remotePeerId, long clockOffsetMs, long syncCount)
        {
             if (remotePeerId != GroupLeaderId)
            {
                Logger.Debug("OnPeerClockSync(). Message source is not the leader.");
                return;
            }

            Logger.Verbose($"OnPeerClockSync() from leader: {remotePeerId}");
            _leaderSysOffset = clockOffsetMs;
            _leaderClockSynced = true;

            if (IsIdle && _leaderApianOffsetReported) // TODO: why only do this when idle?
                _DoTheUpdate(); //
        }

        public override void OnPeerApianOffset(string p2pId,  long remoteApianOffset)
        {
            // Remote peer is reporting it's local apian offset ( ApianClockOffset to it)
            // But the only peer we field messages from is the leader
            // By using P2pNet's estimate for that peer's system offset vs our system clock
            //   ( ourSysTime + peerOffset = peerSysTime)
            // we can infer what the difference is betwen our ApianClock and theirs.
            // and by "infer" I mean "kinda guess sorta"
            // remoteApianClk = sysMs + peerSysOffSet + peerApianOffset
            //
            if (_apian.GroupMgr.LocalPeerId == GroupLeaderId)
               return; //we're the leader. Our ApianClock is by definition correct

            if (p2pId == _apian.GroupMgr.LocalPeerId)
            {
                Logger.Debug("OnApianClockOffset(). Local ApianClock offset ignored");
                return;
            }

            if (p2pId != GroupLeaderId)
            {
                Logger.Verbose("OnPeerApianOffset(). Message source is not the leader.");
                return;
            }

            Logger.Verbose($"OnPeerApianOffset() from leader: {SID(p2pId)}");

            _leaderApianOffset = remoteApianOffset;
            _leaderApianOffsetReported = true;

            if (_leaderClockSynced)
            {
                _DoTheUpdate();
            }   else { // P2pNet hasnt synced with leader yet
                Logger.Verbose($"OnPeerApianOffset() Leader SysClock not synced. Stashing ApianOffset for later.");
            }
        }

        protected void _DoTheUpdate()
        {
            if (IsIdle) // this is the first we've gotten. Set set ours to match theirs.
            {
                Logger.Verbose($"_DoTheUpdate() Idle. Just set.");
                // CurrentTime = sysMs + peerOffset + peerAppOffset;
                DoSet( SystemTime + _leaderSysOffset + _leaderApianOffset );
                SendApianClockOffset(); // let everyone know
            } else {
                if (_apian.LocalPeerIsActive)
                {
                    // If we are active try to correct half of the error in kOffsetAnnounceBaseMs
                    long localErr = SystemTime + _leaderSysOffset + _leaderApianOffset - CurrentTime;
                    float newRate = 1.0f + (.5f * localErr / OffsetAnnouncementPeriodMs);
                    DoSet(CurrentTime, newRate);
                    Logger.Verbose($"_DoTheUpdate: local error: {localErr}, New Rate: {newRate}");
                } else {
                    // otherwise no need for smoothing
                    Logger.Verbose($"_DoTheUpdate() We are not active. Setting time to match Leader.");
                    DoSet( SystemTime + _leaderSysOffset + _leaderApianOffset );
                }
            }
        }

    }

}