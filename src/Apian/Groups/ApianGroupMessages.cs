using System;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Apian
{
    public class ApianGroupMessage : ApianMessage
    {
        public const string GroupsRequest = "APrg";
        public const string GroupJoinRequest = "APgjr";
         public const string GroupMemberJoined = "APgmj"; // Sent on "active" - we need it 'cause it has AppData
        public const string GroupMemberLeft = "APgml";  // Sent to tell members to deelte the (probably already marejed "Gone") member
        public const string GroupJoinFailed = "APgno"; // No group for YOU!
        public const string GroupMemberStatus = "APgms"; // joining, active, removed, etc.
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

    public class GroupJoinRequestMsg : ApianGroupMessage
    {
        public string PeerAddr;
        public string ApianClientPeerJson;
        public bool JoinAsValidator;
        public GroupJoinRequestMsg(string gid, string pid, string peerData, bool asValidator=false) : base(gid, GroupJoinRequest)
        {
            PeerAddr=pid;
            ApianClientPeerJson = peerData;
            JoinAsValidator = asValidator;
        }
    }

    public class GroupMemberJoinedMsg : ApianGroupMessage
    {
        // Need to send both this and MemberStatus update on transition to "active"
        public string PeerAddr;
        public string ApianClientPeerJson;
        public bool JoinedAsValidator;
          public GroupMemberJoinedMsg(string gid, string pid, string peerData, bool asValidator) : base(gid, GroupMemberJoined)
        {
            PeerAddr=pid;
            ApianClientPeerJson = peerData;
            JoinedAsValidator = asValidator;
        }
    }

    public class GroupJoinFailedMsg : ApianGroupMessage
    {
        // Sent to joining peer.
        public string PeerAddr;
        public string FailureReason;
        public GroupJoinFailedMsg(string gid, string pid, string failureReason) : base(gid, GroupJoinFailed)
        {
            PeerAddr=pid;
            FailureReason = failureReason;
        }
    }

    public class GroupMemberLeftMsg : ApianGroupMessage
    {
        public string PeerAddr;
        public GroupMemberLeftMsg(string gid, string pid) : base(gid, GroupMemberLeft) {PeerAddr=pid;}
    }

    public class GroupMemberStatusMsg : ApianGroupMessage
    {
        public string PeerAddr;
        public ApianGroupMember.Status MemberStatus;
        public GroupMemberStatusMsg(string gid, string pid, ApianGroupMember.Status status) : base(gid, GroupMemberStatus)
        {
            PeerAddr=pid;
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
        public long StateEpoch; // current epoch #
        public long StateSeqNum; // sequence number of the last applied ApianCommand
        public long StateTimeStamp;
        public string StateHash;
        public string StateData; // serialized state data (app-dependent format)
        public GroupSyncDataMsg(string gid, long timestamp, long epoch, long seqNum, string hash, string data) : base(gid, GroupSyncData)
        {
            StateEpoch = epoch;
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
        public string PeerAddr;
        public long SeqNum;
        public long TimeStamp; // ApianClock ms
        public string StateHash; // this might want to be more complicated.
        public string HashSignature; // state hash signed by in-game acct
        public GroupCheckpointReportMsg(string addr, string gid, long seqNum, long checkpointTime, string stateHash, string sig) : base(gid, GroupCheckpointReport )
        {
            PeerAddr = addr;
            SeqNum = seqNum;
            TimeStamp = checkpointTime;
            StateHash = stateHash;
            HashSignature = sig;
        }
    }

    static public class ApianGroupMessageDeserializer
    {

       private static readonly Dictionary<string, Func<string, ApianGroupMessage>> deserializers = new  Dictionary<string, Func<string, ApianGroupMessage>>()
        {
            {ApianGroupMessage.GroupsRequest, (s) => JsonConvert.DeserializeObject<GroupsRequestMsg>(s) },
            {ApianGroupMessage.GroupJoinRequest, (s) => JsonConvert.DeserializeObject<GroupJoinRequestMsg>(s) },
            {ApianGroupMessage.GroupMemberStatus, (s) => JsonConvert.DeserializeObject<GroupMemberStatusMsg>(s) },
            {ApianGroupMessage.GroupMemberJoined, (s) => JsonConvert.DeserializeObject<GroupMemberJoinedMsg>(s) },
            {ApianGroupMessage.GroupMemberLeft, (s) => JsonConvert.DeserializeObject<GroupMemberLeftMsg>(s) },
            {ApianGroupMessage.GroupJoinFailed, (s) => JsonConvert.DeserializeObject<GroupJoinFailedMsg>(s) },
            {ApianGroupMessage.GroupSyncRequest, (s) => JsonConvert.DeserializeObject<GroupSyncRequestMsg>(s) },
            {ApianGroupMessage.GroupSyncData, (s) => JsonConvert.DeserializeObject<GroupSyncDataMsg>(s) },
            {ApianGroupMessage.GroupSyncCompletion, (s) => JsonConvert.DeserializeObject<GroupSyncCompletionMsg>(s) },
            {ApianGroupMessage.GroupCheckpointReport, (s) => JsonConvert.DeserializeObject<GroupCheckpointReportMsg>(s) },
        };

        public static ApianGroupMessage FromJson(string msgSubType, string json)
        {
            // If subType not defned here just decode ApianGroupMessage
            return deserializers.ContainsKey(msgSubType) ? deserializers[msgSubType](json) : JsonConvert.DeserializeObject<ApianGroupMessage>(json);
        }
    }

}