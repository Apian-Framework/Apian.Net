
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
        // REQUIRES a GroupMgr instance derived from LeaderDecidesGmBase
        protected string GroupLeaderId => (_apian.GroupMgr as LeaderDecidesGmBase).GroupLeaderId;
        protected string LocalPeerId =>  _apian.GroupMgr.LocalPeerId;

        // Can only update if _leaderClockSynced and _leaderApianOffsetReported are non-null and equal
        private string _leaderClockSynced; // either null (init) or the last leader to sync. Might change.
        private string _leaderApianOffsetReported; // same as above. PeerId of the last leader to report.
        private long _leaderSysClockOffset;
        private long  _leaderApianOffset;

        public LeaderApianClock(ApianBase apian) : base(apian)
        {
            // cant test GroupLeaderId here because clock is not creatd with Apian instance
        }

        public override bool Update()
        {
           if ( !base.Update() )
            return false;

            if ((LocalPeerId == GroupLeaderId) &&  (GroupLeaderId != _leaderClockSynced))
            {
                Logger.Info($"Update() We have become group leader.");
                //we have just become group leader
                _leaderClockSynced = LocalPeerId;
                _leaderSysClockOffset = 0;

                _leaderApianOffsetReported = LocalPeerId;
                _leaderApianOffset = ApianClockOffset;
                SendApianClockOffset();
            }
            return true;
        }


        public override void OnNewPeer(string remotePeerId)
        {
            Logger.Verbose($"OnNewPeer() from {SID(remotePeerId)}");
            if (!IsIdle && _apian.LocalPeerIsActive)
            {
                if (LocalPeerId == GroupLeaderId)
                    SendApianClockOffset(); // announce our apian offset for the remote peer
            }
        }

        public override void OnPeerLeft(string peerId)
        {
            // Nothing to do, here.
        }

        public override void OnPeerClockSync(string remotePeerId, long clockOffsetMs, long syncCount)
        {
            // OnPeerClockSync will never be from the local node.

            if (LocalPeerId == GroupLeaderId)
               return; //we're the leader. Our ApianClock is by definition correct

            if (remotePeerId != GroupLeaderId)
            {
                Logger.Debug($"OnPeerClockSync(). Message source {SID(remotePeerId)} is not the leader.");
                return;
            }

            Logger.Verbose($"OnPeerClockSync() from leader: {SID(remotePeerId)}, offset: {clockOffsetMs}");

            if (_leaderClockSynced != null && remotePeerId != _leaderClockSynced)
                Logger.Info($"OnPeerClockSync() New leader: {SID(remotePeerId)}");

            _leaderClockSynced = remotePeerId;
            _leaderSysClockOffset = clockOffsetMs;

            if (!IsPaused)  // update the sys clock stats, but don't update a paused ApianClock
            {
                if (_leaderApianOffsetReported == remotePeerId)
                    _DoClockUpdate();
                else {
                    Logger.Verbose($"OnPeerClockSync() Waiting for ApianOffset message");
                }
            }
        }

        public override void OnPeerApianOffset(string peerAddr,  long remoteApianOffset)
        {
            // Remote peer is reporting its local apian offset ( ApianClockOffset to it)
            // But the only peer we field messages from is the leader
            // By using P2pNet's estimate for that peer's system offset vs our system clock
            //   ( ourSysTime + peerOffset = peerSysTime)
            // we can infer what the difference is betwen our ApianClock and theirs.
            // and by "infer" I mean "kinda guess sorta"
            // remoteApianClk = sysMs + peerSysOffSet + peerApianOffset
            //

            if (peerAddr != GroupLeaderId)
            {
                Logger.Verbose($"OnPeerApianOffset(). Message source {SID(peerAddr)} is not the leader.");
                return;
            }

            if (LocalPeerId == peerAddr)
               return; //message is from us and we must be the leader. Nothing to do

            if (IsPaused)
                return;

            Logger.Verbose($"OnPeerApianOffset() from leader: {SID(peerAddr)}");

            _leaderApianOffsetReported = peerAddr;
            _leaderApianOffset = remoteApianOffset;


            if (_leaderClockSynced == peerAddr)
            {
                _DoClockUpdate();
            }   else { // P2pNet hasnt synced with leader yet
                Logger.Verbose($"OnPeerApianOffset() Leader SysClock not synced. Stashing ApianOffset for later.");
            }
        }

        protected void _DoClockUpdate()
        {
            if (IsIdle) // this is the first we've gotten. Set set ours to match theirs.
            {
                Logger.Verbose($"_DoTheUpdate() Idle. Just set.");
                // CurrentTime = sysMs + peerOffset + peerAppOffset;
                DoSet( SystemTime + _leaderSysClockOffset + _leaderApianOffset );
                SendApianClockOffset(); // let everyone know
            } else {
                if (_apian.LocalPeerIsActive)
                {
                    // If we are active try to correct half of the error in kOffsetAnnounceBaseMs
                    long localErr = SystemTime + _leaderSysClockOffset + _leaderApianOffset - CurrentTime;
                    float newRate = 1.0f + (.5f * localErr / OffsetAnnouncementPeriodMs);
                    DoSet(CurrentTime, newRate);
                    Logger.Verbose($"_DoTheUpdate: local error: {localErr}, New Rate: {newRate}");
                } else {
                    // otherwise no need for smoothing
                    Logger.Verbose($"_DoTheUpdate() We are not active. Setting time to match Leader.");
                    DoSet( SystemTime + _leaderSysClockOffset + _leaderApianOffset );
                }
            }
        }

    }

}