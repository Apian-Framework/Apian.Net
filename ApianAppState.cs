namespace Apian
{
    // ReSharper disable UnusedType.Global,NotAccessedFIeld.Global,UnusedMember.Global
    public interface IApianStateData
    {
        string ApianSerialized();
        // Requires a paired:
        // public static <DerivedClassType> FromApainSerial() factory
    }


}