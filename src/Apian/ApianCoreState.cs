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
        public string EpochStartHash { get; protected set; } = "0000";
        public long CommandSequenceNumber { get; protected set; } = -1;

        // end data to serialize

        public UniLogger Logger { get; protected set;}

        protected ApianCoreState(string sessionId)
        {
            SessionId = sessionId;
            Logger = UniLogger.GetLogger("CoreState");
        }

        public void SetEpochStartHash(string prevHash)
        {
            Logger.Info($"SetPrevHash(): {prevHash}");
            EpochStartHash = prevHash;
            //PrevEpochHash = "123";
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
                EpochStartHash,
                CommandSequenceNumber
            });
        }

        protected void ApplyDeserializedBaseData(string jsonData)
        {
            object[] data = JsonConvert.DeserializeObject<object[]>(jsonData);
            SessionId = (string)data[0];
            EpochStartHash = (string)data[1];
            CommandSequenceNumber = (long)data[2];
        }

        // Derived class (because it's IApianCoreData) needs:

        //public static DerivedCoreStateType FromApianSerialized( string serializedData, <other class-dependent-args>)

        public abstract string ApianSerialized(object args=null);
    }


}