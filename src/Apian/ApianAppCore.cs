using System;
using System.Collections.Generic;

namespace Apian
{
    // ReSharper disable UnusedType.Global,NotAccessedFIeld.Global,UnusedMember.Global


    public interface IApianAppCore
    {
        // This is generally part of a AppCore definition
        // Most apps will subclass this to include app-relevant data
        // And subclass ApianGroupMember to do the same
        void SetApianReference(ApianBase apian);
        void OnApianCommand(long cmdSeqNum, ApianCoreMessage coreMsg);
        void ApplyCheckpointStateData( long seqNum,  long timeStamp,  string stateHash,  string serializedData);

        ApianCoreMessage DeserializeCoreMessage(ApianWrappedCoreMessage aMsg);

        // Validation
        bool CommandIsValid(ApianCoreMessage cmdMsg);

        // what effect does the previous msg have on the testMsg? Pretty much just for observations coming in a batch
        (ApianConflictResult result, string reason) ValidateCoreMessages(ApianCoreMessage prevMsg, ApianCoreMessage testMsg);
    }

    public abstract class ApianAppCore : IApianAppCore
    {
        protected Dictionary<string, Action<ApianCoreMessage, long>> ClientMsgCommandHandlers;

        public abstract ApianCoreMessage DeserializeCoreMessage(ApianWrappedCoreMessage aMsg);
        public abstract void SetApianReference(ApianBase apian);
        public abstract void OnApianCommand(long seqNum, ApianCoreMessage coreMsg);
        public abstract void OnCheckpointCommand(ApianCheckpointMsg msg, long seqNum);
        public abstract void ApplyCheckpointStateData(long seqNum,  long timeStamp,  string stateHash,  string serializedData);

        // Validation
        public abstract bool CommandIsValid(ApianCoreMessage cmdMsg);

        // what effect does the previous msg have on the testMsg?
        public abstract (ApianConflictResult result, string reason) ValidateCoreMessages(ApianCoreMessage prevMsg, ApianCoreMessage testMsg);
    }

}