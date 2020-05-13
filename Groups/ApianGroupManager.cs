using System.Collections.Generic;

namespace Apian
{
    public class ApianGroupInfo
    {
        public string GroupType;
        public string GroupId; // channel
        public string GroupCreatorId;
        public string GroupName;

        public ApianGroupInfo(string groupType, string groupId, string creatorId, string groupName)
        {
            GroupType = groupType;
            GroupId = groupId;
            GroupCreatorId = creatorId;
            GroupName = groupName;
        }
    }

    public class ApianGroupMember
    {
        // ReSharper disable MemberCanBePrivate.Global,UnusedMember.Global,UnusedAutoPropertyAccessor.Global,NotAccessedField.Global
        public enum Status
        {
            New,  // just created
            Joining, // In the process of joining a group
            Active, // part of the gang
            Missing, // not currently present, but only newly so
            Removed, // has left, or was missing long enough to be removed
        }

        public string PeerId {get;}
        public Status CurStatus;

        public string AppDataJson; // This is ApianClient-relevant data. It's Apian doesn;t read it

        public ApianGroupMember(string peerId, string appDataJson)
        {
            CurStatus = Status.New;
            PeerId = peerId;
            AppDataJson = appDataJson;
        }
        // ReSharper enable MemberCanBePrivate.Global,UnusedMember.Global,UnusedAutoPropertyAccessor.Global,NotAccessedField.Global
    }

    public interface IApianGroupManager
    {
        // ReSharper disable MemberCanBePrivate.Global,UnusedMember.Global,UnusedMemberInSuper.Global
        bool Intialized {get;}
        ApianGroupInfo GroupInfo {get;}
        string GroupType {get;}
        string GroupId {get;}
        string GroupCreatorId {get;}
        string LocalPeerId {get;}
        Dictionary<string, ApianGroupMember> Members {get;}

        void CreateNewGroup(string groupId, string groupName); // does NOT imply join
        void InitExistingGroup(ApianGroupInfo info);
        void JoinGroup(string groupChannel, string localMemberJson);
        void Update();
        ApianMessage DeserializeGroupMessage(string subType, string json);
        void OnApianMessage(ApianMessage msg, string msgSrc, string msgChan); // TODO: replace with specific methods (OnApianRequest...)
        void OnApianRequest(ApianRequest msg, string msgSrc, string msgChan);
        void OnApianObservation(ApianObservation msg, string msgSrc, string msgChan);
        bool ValidateCommand(ApianCommand msg, string msgSrc, string msgChan);
        long GetNewCommandSequenceNumber();
        // ReSharper enable MemberCanBePrivate.Global,UnusedMember.Global,UnusedMemberInSuper.Global
    }

    public abstract class ApianGroupManagerBase
    {
        public abstract long GetNewCommandSequenceNumber(); // Use when creating a new command
        // TODO: I hate that this is public, but currently it is application-specific when a group member becomes
        // "ready to play" so the code to send the ApianCommand for "NewPlayer" or whatever has to live
        // in application-specific ApianBase-derived code. And it needs to be able to ask for a Command sequence # in order to
        // build the command
        // Since in general ApianCommands ARE ClientMessageWrappers this is probably not the only situation when it'll happen
    }

}