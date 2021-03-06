using System.Text.RegularExpressions;
using System.Collections.Generic;
using Newtonsoft.Json;
using P2pNet;
using UniLog;

namespace Apian
{
    public class ApianGroupInfo
    {
        public string GroupType;
        public P2pNetChannelInfo GroupChannelInfo;
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

        public ApianGroupInfo() {} // required by Newtonsoft JSON stuff

        public string Serialized() =>  JsonConvert.SerializeObject(this);
        public static ApianGroupInfo Deserialize(string jsonString) => JsonConvert.DeserializeObject<ApianGroupInfo>(jsonString);
        public bool IsEquivalentTo(ApianGroupInfo agi2)
        {
            return ( GroupType.Equals(agi2.GroupType)
                && GroupChannelInfo.IsEquivalentTo(agi2.GroupChannelInfo)
                && GroupCreatorId.Equals(agi2.GroupCreatorId)
                && GroupName.Equals(agi2.GroupName) );
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
            Removed, // has left, or was missing long enough to be removed
        }

        public string PeerId {get;}
        public Status CurStatus {get; set;}

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
        kUnknown, // not evaluated
        kLocalPeerNotReady, // local peer hasn;t been accepted into group yet
        kAlreadyReceived, // seqence nuber < that what we were expecting
        kBadSource, // Ignore it and complain about the source
        kStashedInQueue, // Higher seq# than we can apply. Cache it and, if not already, ask to be sync'ed up
        kShouldApply, // It's good. Apply it to the state


        kStashedInQueued, // means that seq# was higher than we can apply. GroupMgr will ask for missing ones
    }

    public interface IApianGroupManager
    {
        // I hate that C# has no way to force derived classes to implement
        // uniformly-named static members or methods.
        // Make sure to add these 2 properties to any derived classes:
        //   public static string GroupType = "typeIdStr";
        //   public static string GroupTypeName = "Friendly Name";


        // ReSharper disable MemberCanBePrivate.Global,UnusedMember.Global,UnusedMemberInSuper.Global        ApianGroupInfo GroupInfo {get;}
        string GroupId {get; }
        string GroupName {get; }
        string GroupType {get;}
        string GroupTypeName {get;}
        string GroupCreatorId {get;}
        string LocalPeerId {get;}
        ApianGroupMember LocalMember {get;}
        ApianGroupMember GetMember(string peerId); // returns null if not there

        void SetupNewGroup(ApianGroupInfo info); // does NOT imply join
        void SetupExistingGroup(ApianGroupInfo info);
        void JoinGroup(string localMemberJson);
        void LeaveGroup();
        void Update();
        void SendApianRequest( ApianCoreMessage coreMsg );
        void SendApianObservation( ApianCoreMessage coreMsg );
        void OnApianGroupMessage(ApianGroupMessage msg, string msgSrc, string msgChan);
        void OnApianRequest(ApianRequest msg, string msgSrc, string msgChan);
        void OnApianObservation(ApianObservation msg, string msgSrc, string msgChan);
        void OnLocalStateCheckpoint(long seqNum, long timeStamp, string stateHash, string serializedState);
        ApianCommandStatus EvaluateCommand(ApianCommand msg, string msgSrc, long maxAppliedCmdNum);
        ApianMessage DeserializeApianMessage(ApianMessage apianMsg,  string msgJSON); // pass the generically-deserialized msg

        // ReSharper enable MemberCanBePrivate.Global,UnusedMember.Global,UnusedMemberInSuper.Global
    }

    public abstract class ApianGroupManagerBase : IApianGroupManager
    {
        public abstract string GroupType {get;}
        public abstract string GroupTypeName {get;}

        public ApianGroupInfo GroupInfo {get; protected set;}
        public string GroupId {get => GroupInfo.GroupId;}
        public string GroupName {get => GroupInfo.GroupName;}
        public string GroupCreatorId {get => GroupInfo.GroupCreatorId;}
        public string LocalPeerId {get => ApianInst.GameNet.LocalP2pId();}
        public ApianGroupMember LocalMember {protected set; get;}
        public UniLogger Logger;

        protected ApianBase ApianInst {get; }
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

        // TODO: There may be good default implmentations for some of these methods
        // that ought to just live here

        public abstract void SetupNewGroup(ApianGroupInfo info); // does NOT imply join
        public abstract void SetupExistingGroup(ApianGroupInfo info);
        public abstract void JoinGroup(string localMemberJson);
        public abstract void LeaveGroup();
        public abstract void Update();
        public abstract void SendApianRequest( ApianCoreMessage coreMsg );
        public abstract void SendApianObservation( ApianCoreMessage coreMsg );
        public abstract void OnApianGroupMessage(ApianGroupMessage msg, string msgSrc, string msgChan);
        public abstract void OnApianRequest(ApianRequest msg, string msgSrc, string msgChan);
        public abstract void OnApianObservation(ApianObservation msg, string msgSrc, string msgChan);
        public abstract void OnLocalStateCheckpoint(long cmdSeqNum, long timeStamp, string stateHash, string serializedState);
        public abstract ApianCommandStatus EvaluateCommand(ApianCommand msg, string msgSrc, long maxAppliedCmdNum);
        public virtual ApianMessage DeserializeApianMessage(ApianMessage genMsg, string msgJSON) => null; // by default don't re-deserialize any messages
    }

}