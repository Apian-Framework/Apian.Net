using System;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Apian
{
    public class ApianGroupMessage : ApianMessage
    {
        public const string GroupsRequest = "APrg";
        public const string GroupAnnounce = "APga";
        public const string GroupJoinRequest = "APgjr";
        public const string GroupMemberJoined = "APgmj"; // Sent on "active" - we need it 'cause it has AppData
        public const string GroupMemberStatus = "APgms"; // joining, active, removed, etc
        public const string GroupStateRequest = "APgsr"; // requesting serialized ApianStateData
        public const string GroupStateData = "APgsd"; // response to GroupStateRequest

        public string GroupMsgType;
        public ApianGroupMessage(string gid, string groupMsgType) : base(gid, GroupMessage) {GroupMsgType=groupMsgType;}
        public ApianGroupMessage() : base("", GroupMessage) {}
    }


    public class GroupsRequestMsg : ApianGroupMessage // Send on main channel - no DestGroupId
    {
        public GroupsRequestMsg() : base("", GroupsRequest) {}
    }

    public class GroupAnnounceMsg : ApianGroupMessage // Send on main channel - no DestGroupId
    {
        public string GroupType;
        public string GroupId; // The annouced group. DestGroupId is ""
        public string GroupCreatorId;
        public string GroupName;
        public GroupAnnounceMsg() : base() {}
        public GroupAnnounceMsg(ApianGroupInfo info) : base("", GroupAnnounce)
        {
            GroupType = info.GroupType;
            GroupId = info.GroupId;
            GroupCreatorId = info.GroupCreatorId;
            GroupName = info.GroupName;
        }
    }

    public class GroupJoinRequestMsg : ApianGroupMessage
    {
        public string PeerId;
        public string ApianClientPeerJson;
        public GroupJoinRequestMsg(string gid, string pid, string peerData) : base(gid, GroupJoinRequest) {PeerId=pid; ApianClientPeerJson = peerData;}
    }

    public class GroupMemberJoinedMsg : ApianGroupMessage
    {
        // Need to send both this and MemberStatus update on transition to "active"
        public string PeerId;
        public string ApianClientPeerJson;
        public GroupMemberJoinedMsg(string gid, string pid, string peerData) : base(gid, GroupMemberJoined) {PeerId=pid; ApianClientPeerJson = peerData;}
    }


    public class GroupMemberStatusMsg : ApianGroupMessage
    {
        public string PeerId;
        public ApianGroupMember.Status MemberStatus;
        public GroupMemberStatusMsg(string gid, string pid, ApianGroupMember.Status status) : base(gid, GroupMemberStatus)
        {
            PeerId=pid;
            MemberStatus=status;
        }
    }

    public class GroupStateRequest : ApianGroupMessage
    {
        public int CurCmdSeqNum; // sequence number of the first ApianCommand that is locally queued and can be applied
        public GroupStateRequest(string gid, int seqNum) : base(gid, GroupStateRequest) {CurCmdSeqNum=seqNum;}
    }

    public class GroupStateDataMsg : ApianGroupMessage
    {
        public int StateEpoch; // sequence number of the last applied ApianCommand
        public string StateData; // serialized state data (app-dependent format)
        public GroupStateDataMsg(string gid, int epoch, string data) : base(gid, GroupStateData) {StateEpoch=epoch; StateData=data;}
    }

    static public class ApianGroupMessageDeserializer
    {
       private static Dictionary<string, Func<string, ApianMessage>> deserializers = new  Dictionary<string, Func<string, ApianMessage>>()
        {
            {ApianGroupMessage.GroupAnnounce, (s) => JsonConvert.DeserializeObject<GroupAnnounceMsg>(s) },
            {ApianGroupMessage.GroupsRequest, (s) => JsonConvert.DeserializeObject<GroupsRequestMsg>(s) },
            {ApianGroupMessage.GroupJoinRequest, (s) => JsonConvert.DeserializeObject<GroupJoinRequestMsg>(s) },
            {ApianGroupMessage.GroupMemberStatus, (s) => JsonConvert.DeserializeObject<GroupMemberStatusMsg>(s) },
            {ApianGroupMessage.GroupMemberJoined, (s) => JsonConvert.DeserializeObject<GroupMemberJoinedMsg>(s) },
        };

        static ApianGroupMessageDeserializer()
        {

        }

        public static ApianGroupMessage FromJson(string msgSubType, string json)
        {
            return deserializers[msgSubType](json) as ApianGroupMessage;
        }
    }

}