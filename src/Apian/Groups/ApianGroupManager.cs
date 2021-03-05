using System.Collections.Generic;
using Newtonsoft.Json;
using P2pNet;
using UniLog;

namespace Apian
{
    public class ApianGroupInfo
    {
        public string GroupType;
        P2pNetChannelInfo GroupChannelInfo;
        public string GroupId { get => GroupChannelInfo?.id;} // channel
        public string GroupCreatorId;
        public string GroupName;

        public ApianGroupInfo(string groupType, P2pNetChannelInfo groupChannel, string creatorId, string groupName)
        {
            GroupType = groupType;
            GroupChannelInfo = groupChannel;
            GroupCreatorId = creatorId;
            GroupName = groupName;
        }

        public string Serialized() =>  JsonConvert.SerializeObject(this);
        public static ApianGroupInfo Deserialize(string jsonString) => JsonConvert.DeserializeObject<ApianGroupInfo>(jsonString);
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
        string GroupName {get;}
        string GroupCreatorId {get;}
        string LocalPeerId {get;}
        ApianGroupMember LocalMember {get;}
        ApianGroupMember GetMember(string peerId); // returns null if not there

        void SetupNewGroup(string groupName); // does NOT imply join
        void SetGroupInfo(ApianGroupInfo info);
        void JoinGroup(string groupChannel, string localMemberJson);
        void Update();
        void OnApianMessage(ApianMessage msg, string msgSrc, string msgChan); // TODO: replace with specific methods (OnApianRequest...)
        void OnApianRequest(ApianRequest msg, string msgSrc, string msgChan);
        void OnApianObservation(ApianObservation msg, string msgSrc, string msgChan);
        void OnLocalStateCheckpoint(long seqNum, long timeStamp, string stateHash, string serializedState);
        ApianCommandStatus EvaluateCommand(ApianCommand msg, string msgSrc, string msgChan);

        // ReSharper enable MemberCanBePrivate.Global,UnusedMember.Global,UnusedMemberInSuper.Global
    }

    public abstract class ApianGroupManagerBase
    {
        protected ApianBase ApianInst {get; }

        public ApianGroupInfo GroupInfo {get; protected set;}
        public string GroupId {get => GroupInfo.GroupId;}
        public string GroupName {get => GroupInfo.GroupName;}

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