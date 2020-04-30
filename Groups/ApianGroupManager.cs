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

        public ApianGroupMember(string peerId)
        {
            CurStatus = Status.New;
            PeerId = peerId;
        }
        // ReSharper enable MemberCanBePrivate.Global,UnusedMember.Global,UnusedAutoPropertyAccessor.Global,NotAccessedField.Global
    }

    public interface IApianGroupManager
    {
        // ReSharper disable MemberCanBePrivate.Global,UnusedMember.Global,UnusedMemberInSuper.Global
        string GroupType {get;}
        string GroupId {get;}
        string GroupCreatorId {get;}
        string LocalPeerId {get;}
        Dictionary<string, ApianGroupMember> Members {get;}

        void CreateGroup(string groupId, string groupName); // does NOT imply join
        void CreateGroup(ApianGroupInfo info);
        void JoinGroup(string groupChannel, string localMemberJson);
        void Update();
        ApianMessage DeserializeGroupMessage(string subType, string json);
        void OnApianMessage(ApianMessage msg, string msgSrc, string msgChan);
        // ReSharper enable MemberCanBePrivate.Global,UnusedMember.Global,UnusedMemberInSuper.Global
    }



}