using Newtonsoft.Json;

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


    public abstract class ApianCoreState : IApianCoreData
    {
        public long CommandSequenceNumber { get; protected set; } = -1;


        public virtual string ApianSerialized(object args=null)
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


    }


}