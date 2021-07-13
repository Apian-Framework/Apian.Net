
using System;
using System.Collections.Generic;
using UniLog;

namespace Apian
{
    // ReSharper disable once UnusedType.Global
    public class LeaderApianClock : ApianClockBase
    {
        protected string GroupCreatorId { get => _apian.GroupMgr.GroupCreatorId;}
        private long _leaderSysOffset;
        private long  _leaderApianOffset;

        public LeaderApianClock(ApianBase apian) : base(apian)
        {

        }
        public override void OnNewPeer(string remotePeerId, long clockOffsetMs, long netLagMs)
        {

            Logger.Verbose($"OnNewPeer() from {remotePeerId}");
            if (remotePeerId == _apian.GroupMgr.LocalPeerId)
            {
                Logger.Debug("OnNewPeer(). Oops. It's me. Bailing");
                return;
            }

            OnPeerClockSync(remotePeerId, clockOffsetMs, netLagMs);
            if (!IsIdle)
            {
                if (_apian.GroupMgr.LocalPeerId == GroupCreatorId)
                    SendApianClockOffset(); // announce our apian offset
            }
        }

        public override void OnPeerClockSync(string remotePeerId, long clockOffsetMs, long netLagMs) // localSys + offset = PeerSys
        {
            Logger.Verbose($"OnPeerClockSync() from {remotePeerId}");
            // This is a P2pNet sync ( lag and sys clock offset determination )
            if (remotePeerId == GroupCreatorId)
                 _leaderSysOffset = clockOffsetMs; // save it
        }

        public override void OnPeerApianOffset(string p2pId,  long remoteApianOffset)
        {
            // Remote peer is reporting it's local apian offset (SysClockOffset to the peer, _leaderApianOffset to us)
            // By using P2pNet's estimate for that peer's system offset vs our system clock
            //   ( ourSysTime + peerOffset = peerSysTime)
            // we can infer what the difference is betwen our ApianClock and theirs.
            // and by "infer" I mean "kinda guess sorta"
            // remoteApianClk = sysMs + peerOffSet + peerApianOffset
            //
            if (_apian.GroupMgr.LocalPeerId == GroupCreatorId)
               return; //we're the leader

            if (p2pId == _apian.GroupMgr.LocalPeerId)
            {
                Logger.Debug("OnApianClockOffset(). Oops. It's me. Bailing");
                return;
            }

            if (p2pId != GroupCreatorId)
            {
                Logger.Verbose("OnPeerApianOffset(). Message source is not the creator.");
                return;
            }

            Logger.Verbose($"OnPeerApianOffset() from creator: {p2pId}");

            _leaderApianOffset = remoteApianOffset;

            if (IsIdle) // this is the first we've gotten. Set set ours to match theirs.
            {
                Logger.Verbose($"OnPeerApianOffset() Idle. Just set.");
                // CurrentTime = sysMs + peerOffset + peerAppOffset;
                _DoSet( SystemTime + _leaderSysOffset + _leaderApianOffset );
            } else {

                long localErr = SystemTime + _leaderSysOffset + _leaderApianOffset - CurrentTime;

                // Try to correct half of the error in kOffsetAnnounceBaseMs
                float newRate = 1.0f + (.5f * localErr / OffsetAnnouncementPeriodMs);
                _DoSet(CurrentTime, newRate);
                Logger.Verbose($"Update: local error: {localErr}, New Rate: {newRate}");
            }
        }


    }

}