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
        public const string GroupSyncRequest = "APgsyr"; // request sync data
        public const string GroupSyncCompletion = "APgsyc"; // "I'm done with sync"

        // These next 2 may be premature - nothing depends on em yet to feel free to toss/change them
        //public const string GroupStateRequest = "APgsr"; // requesting serialized ApianStateData
        //public const string GroupStateData = "APgsd"; // response to GroupStateRequest

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

    // public class GroupStateRequestMsg : ApianGroupMessage
    // {
    //     public long CurCmdSeqNum; // sequence number of the first ApianCommand that is locally queued and can be applied
    //     public GroupStateRequestMsg(string gid, long seqNum) : base(gid, GroupStateRequest) {CurCmdSeqNum=seqNum;}
    // }

    // public class GroupStateDataMsg : ApianGroupMessage
    // {
    //     public int StateEpoch; // sequence number of the last applied ApianCommand
    //     public string StateData; // serialized state data (app-dependent format)
    //     public GroupStateDataMsg(string gid, int epoch, string data) : base(gid, GroupStateData) {StateEpoch=epoch; StateData=data;}
    // }

    public class GroupSyncRequestMsg : ApianGroupMessage
    {
        public long ExpectedCmdSeqNum; // sequence number of the first MISSING command (probably 0)
        public long FirstStashedCmdSeqNum; // sequence number of the first ApianCommand that is locally queued and can be applied
                                          // -1 means: I have none - use the last sent command
        public GroupSyncRequestMsg(string gid, long expected, long stashed) : base(gid, GroupSyncRequest)
        {
            ExpectedCmdSeqNum = expected;
            FirstStashedCmdSeqNum = stashed;
        }
    }

    public class GroupSyncCompletionMsg : ApianGroupMessage
    {
        // Sent by a syncing client to say "Hey! I'm up to date! Make me active"
        public long CompletionSeqNum; // sequence number when it caught up
        public string CompleteionStateHash; // the data hash as of CompletionSeqNum (as proof - or unsused)
        public GroupSyncCompletionMsg(string gid, long seqNum, string hash) : base(gid, GroupSyncCompletion)
        {
            CompletionSeqNum = seqNum;
            CompleteionStateHash = hash;
        }
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
            {ApianGroupMessage.GroupSyncRequest, (s) => JsonConvert.DeserializeObject<GroupSyncRequestMsg>(s) },
            {ApianGroupMessage.GroupSyncCompletion, (s) => JsonConvert.DeserializeObject<GroupSyncCompletionMsg>(s) },
        };

        public static ApianGroupMessage FromJson(string msgSubType, string json)
        {
            return deserializers[msgSubType](json) as ApianGroupMessage;
        }
    }

}