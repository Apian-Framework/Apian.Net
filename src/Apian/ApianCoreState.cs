using Newtonsoft.Json;
using UniLog;

namespace Apian
{
    // A data element that goes in ApianCoreState must be Apian serializable/deserializable
    // Note: this is NOT the core state (though the core state does implement IApianCoreData)
    public interface IApianCoreData
    {
         string ApianSerialized(object args); // might need class-dependent args
        // Requires a paired:
        // public static <DerivedClassType> FromApainJson(string serializedData, <other class-dependent-srgs>)
    }

    public interface IApianCoreState : IApianCoreData
    {
       long CommandSequenceNumber { get;}
        void UpdateCommandSequenceNumber(long newCmdSeqNumber);
    }


    public abstract class ApianCoreState : IApianCoreState
    {
        // Data to serialize
        public string SessionId { get; protected set; } // TODO: probably want a way to restore a serialized state and then change the session ID (for replay)
        public long EpochNum { get; protected set; }
        public string EpochStartHash { get; protected set; } = "";
        public long CommandSequenceNumber { get; protected set; } = -1;

        // end data to serialize

        public UniLogger Logger { get; protected set;}

        protected ApianCoreState(string sessionId)
        {
            SessionId = sessionId;
            Logger = UniLogger.GetLogger("CoreState");
        }

        public void StartEpoch(long epochNum, string startHash)
        {
            Logger.Info($"StartEpoch(): Epoch: {epochNum}, StartHash: {startHash}");
            EpochNum = epochNum;
            EpochStartHash = startHash;
;
        }

        public void UpdateCommandSequenceNumber(long newCmdSeqNumber)
        {
            Logger.Debug($"UpdateCommandSequenceNumber({newCmdSeqNumber})");
            if (newCmdSeqNumber != CommandSequenceNumber+1)
                Logger.Warn($"New CmdSeqNum ({newCmdSeqNumber}) is not current CmdSeqNum ({CommandSequenceNumber}) plus 1");

            CommandSequenceNumber = newCmdSeqNumber;
        }

        public string ApianSerializedBaseData(object args=null)
        {
            // return the "base part"
            return  JsonConvert.SerializeObject(new object[]{
                SessionId,
                EpochNum,
                EpochStartHash,
                CommandSequenceNumber
            });
        }

        protected void ApplyDeserializedBaseData(string jsonData)
        {
            object[] data = JsonConvert.DeserializeObject<object[]>(jsonData);
            SessionId = (string)data[0];
            EpochNum = (long)data[1];
            EpochStartHash = (string)data[2];
            CommandSequenceNumber = (long)data[3];
        }

        // Derived class (because it's IApianCoreData) needs:

        //public static DerivedCoreStateType FromApianSerialized( string serializedData, <other class-dependent-args>)

        public abstract string ApianSerialized(object args=null);
    }


}