using GameNet;

namespace Apian
{
    public interface IApianApplication : IGameNetClient
    {
        // This is the "backend" part of an Apian app
        // which sets up GameNet (and probably the GameInstance/Apian pairs)
        // and that handles any stuff (chat messages, etc)  not Apian-related
        void AddAppCore(IApianAppCore coreInstance);
        void OnGroupAnnounce(ApianGroupInfo groupInfo);
        void OnGroupMemberStatus(string groupId, string peerId, ApianGroupMember.Status newStatus, ApianGroupMember.Status prevStatus);
    }
}