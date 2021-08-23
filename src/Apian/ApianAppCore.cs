using System;
using System.Collections.Generic;

namespace Apian
{

    public class NewCoreStateEventArgs : EventArgs {
        public ApianCoreState coreState;
        public NewCoreStateEventArgs(ApianCoreState _coreState) { coreState = _coreState;}
    }

    public interface IApianAppCore
    {
        event EventHandler<NewCoreStateEventArgs> NewCoreStateEvt;

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
        public event EventHandler<NewCoreStateEventArgs> NewCoreStateEvt;
        protected Dictionary<string, Action<ApianCoreMessage, long>> ClientMsgCommandHandlers;

        protected virtual void OnNewCoreState(ApianCoreState newState = null)
        {
            NewCoreStateEvt?.Invoke(this, new NewCoreStateEventArgs(newState));
        }

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