using GameNet;

namespace Apian
{
    public interface IApianApplication : IGameNetClient
    {
        // This is the "backend" part of an Apian app
        // which sets up GameNet (and probably the GameInstance/Apian pairs)
        // and that handles any stuff (chat messages, etc)  not Apian-related
        void AddAppCore(IApianAppCore coreInstance);
        void OnGroupAnnounce(GroupAnnounceResult groupAnnouncement);
        void OnGroupMemberStatus(string groupId, string peerId, ApianGroupMember.Status newStatus, ApianGroupMember.Status prevStatus);
        void OnPeerJoinedGroup(PeerJoinedGroupData data);
    }
}