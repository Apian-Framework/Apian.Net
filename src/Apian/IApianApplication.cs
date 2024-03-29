using GameNet;

namespace Apian
{
    public interface IApianApplication : IApianGameNetClient
    {
        // This is the "backend" part of an Apian app
        // which sets up GameNet (and probably the GameInstance/Apian pairs)
        // and that handles any stuff (chat messages, etc)  not Apian-related
        void AddAppCore(IApianAppCore coreInstance);
        void OnGroupAnnounce(GroupAnnounceResult groupAnnouncement);
        void OnGroupMemberStatus(string groupId, string peerAddr, ApianGroupMember.Status newStatus, ApianGroupMember.Status prevStatus);
        void OnPeerJoinedGroup(PeerJoinedGroupData data);

        // Not all groups even HAVE a leader, but for those that do it'd be nice to be able to keep any users informed
        // regarding changes.
        // THe idea with the data is that it is app-dependent and the app can extract and use it
        void OnGroupLeaderChange(string groupId, string newLeaderAddr, ApianGroupMember leaderAppData);

        void DisplayError(string message, int status = -1, int param = -1);
    }
}