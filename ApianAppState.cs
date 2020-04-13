namespace Apian
{
    public interface IApianStateData
    { 
        string ApianSerialized(); 
        // Requires a paired:
        // public static <DerivedClassType> FromApainSerial() factory
    }


}