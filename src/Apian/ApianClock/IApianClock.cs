
using System;
using System.Collections.Generic;
using UniLog;
namespace Apian
{

    public interface IApianClock
    {
        long CurrentTime { get;} // This is the ApianTime
        void SetTime(long desiredTimeMs, float rate=1.0f );
        bool IsIdle { get;}  // hasn't been set yet
        long SystemTime { get;}  // local system clock
        long ApianClockOffset {get; } // The current offset from local system time to apian:  localSysClock + offset = apianClock
        void OnNewPeer(string remotePeerId);
         void OnPeerLeft(string peerId);
        void OnPeerClockSync(string remotePeerId, long clockOffsetMs, long syncCount); // local sysClock + offset = peerSysClock
        void OnPeerApianOffset(string remotePeerId,  long remoteApianOffset); // remoteSysClock + remoteApianOffset = remoteApianClock
        void Update(); // loop

    }

}