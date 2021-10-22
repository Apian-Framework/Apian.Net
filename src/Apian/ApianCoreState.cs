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
        public long CommandSequenceNumber { get; protected set; } = -1;

        public UniLogger Logger { get; protected set;}

        protected ApianCoreState()
        {
            Logger = UniLogger.GetLogger("CoreState");
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
                CommandSequenceNumber,
            });
        }

        protected void ApplyDeserializedBaseData(string jsonData)
        {
            object[] data = JsonConvert.DeserializeObject<object[]>(jsonData);

            CommandSequenceNumber = (long)data[0];
        }

        // Derived class (because it's IApianCoreData) needs:

        //public static DerivedCoreStateType FromApianSerialized( string serializedData, <other class-dependent-args>)

        public abstract string ApianSerialized(object args=null);
    }


}