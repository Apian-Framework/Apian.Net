
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
        bool IsPaused { get;} //
        long SystemTime { get;}  // local system clock
        long ApianClockOffset {get; } // The current offset from local system time to apian:  localSysClock + offset = apianClock
        void OnNewPeer(string remotePeerAddr);
        void OnPeerLeft(string peerAddr);
        void OnPeerClockSync(string remotePeerAddr, long clockOffsetMs, long syncCount); // local sysClock + offset = peerSysClock
        void OnPeerApianOffset(string remotePeerAddr,  long remoteApianOffset); // remoteSysClock + remoteApianOffset = remoteApianClock
        bool Update(); // loop. Base returns false if no updating should happen
        void Pause();
        void Resume();

    }

}