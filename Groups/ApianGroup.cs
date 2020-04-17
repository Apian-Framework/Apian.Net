using System.Collections.Generic;

namespace Apian
{
    public class ApianMember
    {
        // ReSharper disable MemberCanBePrivate.Global,UnusedMember.Global,UnusedAutoPropertyAccessor.Global,NotAccessedField.Global
        public enum Status
        {
            New,  // just created
            Syncing, // In the process of P2pNet syncing and getting up-to-date
            Joining, // In the process of joining a group
            Active, // part of the gang
            Missing, // not currently present, but only newly so
        }

        public string P2pId {get;}
        public Status CurStatus;

        public ApianMember(string p2pId)
        {
            CurStatus = Status.New;
            P2pId = p2pId;
        }
        // ReSharper enable MemberCanBePrivate.Global,UnusedMember.Global,UnusedAutoPropertyAccessor.Global,NotAccessedField.Global
    }

    public interface IApianGroupManager
    {
        // ReSharper disable MemberCanBePrivate.Global,UnusedMember.Global,UnusedMemberInSuper.Global
        string GroupId {get;}
        string GroupCreatorId {get;}
        string LocalP2pId {get;}
        Dictionary<string, ApianMember> Members {get;}
        void Update();
        ApianMessage DeserializeMessage(string subType, string json);
        void OnApianMessage(ApianMessage msg, string msgSrc, string msgChan);
        void StartLocalOnlyGroup();
        // ReSharper enable MemberCanBePrivate.Global,UnusedMember.Global,UnusedMemberInSuper.Global

    }
}