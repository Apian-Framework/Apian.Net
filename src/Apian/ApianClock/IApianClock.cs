
using System;
using System.Collections.Generic;
using UniLog;

namespace Apian
{

    public interface IApianClock
    {
        // ReSharper disable UnusedMemberInSuper.Global,UnusedMember.Global
        long CurrentTime { get;} // This is the ApianTime
        void SetTime(long desiredTimeMs, float rate=1.0f );
        bool IsIdle { get;}  // hasn't been set yet
        long SystemTime { get;}  // local system clock
        long SysClockOffset {get; } // The current offset from local system time to apian:  localSysClock + offset = apianClock
        void OnNewPeer(string remotePeerId, long clockOffsetMs, long netLagMs);
        void OnPeerClockSync(string remotePeerId, long clockOffsetMs, long netLagMs); // local sysClock + offset = peerSysClock
        void OnPeerLeft(string peerId);
        void OnPeerApianOffset(string remotePeerId,  long apianOffset); // SysClockOffset on the remote peer
        void Update(); // loop
        // ReSharper enable UnusedMemberInSuper.Global,UnusedMember.Global

    }

}