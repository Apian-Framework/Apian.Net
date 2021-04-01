namespace Apian
{
    // ReSharper disable UnusedType.Global,NotAccessedFIeld.Global,UnusedMember.Global


    public interface IApianAppCore
    {
        // This is generally part of a AppCore definition
        // Most apps will subclass this to include app-relevant data
        // And subclass ApianGroupMember to do the same
        void SetApianReference(ApianBase apian);
        void OnApianCommand(ApianCommand cmd);
        void OnCheckpointCommand(long seqNum, long timeStamp);
        void ApplyCheckpointStateData( long seqNum,  long timeStamp,  string stateHash,  string serializedData);

        // Validation
        bool CommandIsValid(ApianCoreMessage cmdMsg);

        // what effect does the previous msg have on the testMsg?
        (ApianConflictResult result, string reason) ValidateCoreMessages(ApianCoreMessage prevMsg, ApianCoreMessage testMsg);
    }

}