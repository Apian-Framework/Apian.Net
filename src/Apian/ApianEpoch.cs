using System;
using System.Collections.Generic;

namespace Apian
{

        public class ApianEpoch
        {
            public long EpochNum;
            public long StartCmdSeqNumber; // first command in the epoch
            public long EndCmdSeqNumber; // This is the seq # of the LAST command - which is a checkpoint request
            public long StartTimeStamp;  //  Start of epoch
            public long EndTimeStamp;  //  Start of epoch
            public string EndStateHash;
            public string SerializedStateData;

            public ApianEpoch(long epochNum, long startCmdSeqNum, long startTimeStamp)
            {
                EpochNum = epochNum;
                StartCmdSeqNumber = startCmdSeqNum;
                StartTimeStamp = startTimeStamp;
            }

            public void CloseEpoch(long lastSeqNum, long checkpointTimeStamp, string stateHash, string serializedState)
            {
                // TODO: this needs to do something persistent with the data, too
                EndCmdSeqNumber = lastSeqNum;
                EndTimeStamp = checkpointTimeStamp;
                EndStateHash = stateHash;
                SerializedStateData = serializedState;
            }
        }







}