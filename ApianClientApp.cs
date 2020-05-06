namespace Apian
{
    // ReSharper disable UnusedType.Global,NotAccessedFIeld.Global,UnusedMember.Global

    public abstract class ApianClientMemberData
    {
        // This is the base for your client app's (GameInstance's) idea of what
        // a peer is. You don't want to conflate this with an ApianPeer or
        // ApianGroupMember. It's really just for the client app itself.
        public string PeerId {get; }  // but it WILL have a read-only PeerId...

        public ApianClientMemberData(string peerId) { PeerId = peerId; }

        public abstract string ApianSerialized();
        // Will generally want: public static <type> FromApianSerialized(string jsonData)

    }

    public interface IApianClientApp
    {
        // This is generally part of a GameInstance definition
        // Most apps will subclass this to include app-relevant data
        // And subclass ApianGroupMember to do the same
        void SetApianReference(ApianBase apian);
        void OnMemberJoined( ApianClientMemberData member);
        void OnMemberStatus(string peerId, ApianGroupMember.Status newStatus);
        void OnMemberLeft( string peerId);
    }


    public interface IApianStateData
    {
        //string ApianHash();
        string ApianSerialized();

        // Requires a paired:
        // public static <DerivedClassType> FromApainSerial()
    }


}