using System.Collections.Generic;
using UniLog;

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
            Syncing, // In the process of syncing app state
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

    public enum ApianCommandStatus {
        kShouldApply, // It's good. Apply it to the state
        kStashedInQueued, // means that seq# was higher than we can apply. GroupMgr will ask for missing ones
        kBadSource, // Ignore it and complain about the source
        kAlreadyReceived, // seqence nuber < that what we were expecting
        kLocalPeerNotReady, // local peer hasn;t been accepted into group yet
    }

    public interface IApianGroupManager
    {
        // ReSharper disable MemberCanBePrivate.Global,UnusedMember.Global,UnusedMemberInSuper.Global        ApianGroupInfo GroupInfo {get;}
        string GroupType {get;}
        string GroupId {get;}
        string GroupCreatorId {get;}
        string LocalPeerId {get;}
        ApianGroupMember LocalMember {get;}
        ApianGroupMember GetMember(string peerId); // returns null if not there

        void CreateNewGroup(string groupId, string groupName); // does NOT imply join
        void InitExistingGroup(ApianGroupInfo info);
        void JoinGroup(string groupChannel, string localMemberJson);
        void Update();
        //ApianMessage DeserializeGroupMessage(string subType, string json);
        void OnApianMessage(ApianMessage msg, string msgSrc, string msgChan); // TODO: replace with specific methods (OnApianRequest...)
        void OnApianRequest(ApianRequest msg, string msgSrc, string msgChan);
        void OnApianObservation(ApianObservation msg, string msgSrc, string msgChan);
        ApianCommandStatus EvaluateCommand(ApianCommand msg, string msgSrc, string msgChan);

        // ReSharper enable MemberCanBePrivate.Global,UnusedMember.Global,UnusedMemberInSuper.Global
    }

    public abstract class ApianGroupManagerBase
    {
        protected ApianBase ApianInst {get; }
        public UniLogger Logger;
        protected Dictionary<string, ApianGroupMember> Members {get;}

        public ApianGroupManagerBase(ApianBase apianInst)
        {
            Logger = UniLogger.GetLogger("ApianGroup");
            ApianInst = apianInst;
            Members = new Dictionary<string, ApianGroupMember>();
        }

        public ApianGroupMember GetMember(string peerId)
        {
            if (!Members.ContainsKey(peerId))
                return null;
            return Members[peerId];
        }
    }

}