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
        public const string GroupLeaveRequest = "APglr";
        public const string GroupMemberJoined = "APgmj"; // Sent on "active" - we need it 'cause it has AppData
        public const string GroupMemberStatus = "APgms"; // joining, active, removed, etc
        public const string GroupSyncRequest = "APgsyr"; // request sync data
        public const string GroupSyncData = "APgsd"; // response to GroupSyncRequest
        public const string GroupSyncCompletion = "APgsyc"; // "I'm done with sync"
        public const string GroupCheckpointReport = "APgcrp"; // "Here's my local checkpoint hash"

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
        public string groupInfoJson;
        public ApianGroupInfo GroupInfo {get => ApianGroupInfo.Deserialize(groupInfoJson);}
        public GroupAnnounceMsg() : base() {}
        public GroupAnnounceMsg(ApianGroupInfo info) : base("", GroupAnnounce)
        {
            groupInfoJson = info.Serialized();
        }


    }

    public class GroupJoinRequestMsg : ApianGroupMessage
    {
        public string PeerId;
        public string ApianClientPeerJson;
        public GroupJoinRequestMsg(string gid, string pid, string peerData) : base(gid, GroupJoinRequest) {PeerId=pid; ApianClientPeerJson = peerData;}
    }

    public class GroupLeaveRequestMsg : ApianGroupMessage
    {
        public string PeerId;
        public GroupLeaveRequestMsg(string gid, string pid) : base(gid, GroupLeaveRequest) {PeerId=pid;}
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

    public class GroupSyncDataMsg : ApianGroupMessage
    {
        public long StateSeqNum; // sequence number of the last applied ApianCommand
        public long StateTimeStamp;
        public string StateHash;
        public string StateData; // serialized state data (app-dependent format)
        public GroupSyncDataMsg(string gid, long timestamp, long seqNum, string hash, string data) : base(gid, GroupSyncData)
        {
            StateSeqNum=seqNum;
            StateTimeStamp = timestamp;
            StateHash = hash;
            StateData=data;
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

    public class GroupCheckpointReportMsg : ApianGroupMessage
    {
        public long SeqNum;
        public long TimeStamp; // ApianClock ms
        public string StateHash; // this might want to be more complicated.
        public GroupCheckpointReportMsg(string gid, long seqNum, long checkpointTime, string stateHash) : base(gid, GroupCheckpointReport )
        {
            SeqNum = seqNum;
            TimeStamp = checkpointTime;
            StateHash = stateHash;
        }
    }

    static public class ApianGroupMessageDeserializer
    {
       private static Dictionary<string, Func<string, ApianMessage>> deserializers = new  Dictionary<string, Func<string, ApianMessage>>()
        {
            {ApianGroupMessage.GroupAnnounce, (s) => JsonConvert.DeserializeObject<GroupAnnounceMsg>(s) },
            {ApianGroupMessage.GroupsRequest, (s) => JsonConvert.DeserializeObject<GroupsRequestMsg>(s) },
            {ApianGroupMessage.GroupJoinRequest, (s) => JsonConvert.DeserializeObject<GroupJoinRequestMsg>(s) },
            {ApianGroupMessage.GroupLeaveRequest, (s) => JsonConvert.DeserializeObject<GroupLeaveRequestMsg>(s) },
            {ApianGroupMessage.GroupMemberStatus, (s) => JsonConvert.DeserializeObject<GroupMemberStatusMsg>(s) },
            {ApianGroupMessage.GroupMemberJoined, (s) => JsonConvert.DeserializeObject<GroupMemberJoinedMsg>(s) },
            {ApianGroupMessage.GroupSyncRequest, (s) => JsonConvert.DeserializeObject<GroupSyncRequestMsg>(s) },
            {ApianGroupMessage.GroupSyncData, (s) => JsonConvert.DeserializeObject<GroupSyncDataMsg>(s) },
            {ApianGroupMessage.GroupSyncCompletion, (s) => JsonConvert.DeserializeObject<GroupSyncCompletionMsg>(s) },
            {ApianGroupMessage.GroupCheckpointReport, (s) => JsonConvert.DeserializeObject<GroupCheckpointReportMsg>(s) },
        };

        public static ApianGroupMessage FromJson(string msgSubType, string json)
        {
            return deserializers[msgSubType](json) as ApianGroupMessage;
        }
    }

}