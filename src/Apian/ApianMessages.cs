using System;
using System.Collections.Generic;
using Newtonsoft.Json;
namespace Apian
{
    // ReSharper disable UnusedType.Global,NotAccessedFIeld.Global,FieldCanBeMadeReadOnly.Global,UnusedMember.Global
    // (can;t be readonly because of NewtonSoft.JSON)

     public enum ApianConflictResult { Unaffected, Validated, Invalidated }

    // Parent class for the "Game messages"
    public class ApianCoreMessage
    {
        // Client game or app messages derive from this
        public string MsgType;
        public long TimeStamp; // Apian time when core message happened or gets applied. Not related to network timing.
        public ApianCoreMessage(string t, long ts) {MsgType = t; TimeStamp = ts;}
        public ApianCoreMessage() {}

    }

    public class ApianMessage
    {
        public const string CliRequest = "APapRq";
        public const string CliObservation = "APapObs";
        public const string CliCommand = "APapCmd";
        public const string ApianClockOffset = "APclk";
        public const string ApianGroupAnnounce = "APga"; // NOT a GroupMessage, since apian instance never read it (it's just for clients)
        public const string GroupMessage = "APGrp";
        public const string CheckpointMsg = "APchk";
        public string DestGroupId; // Can be empty
        public string MsgType;
       // ReSharper enable MemberCanBeProtected.Global
        protected ApianMessage(string gid, string typ) { DestGroupId = gid; MsgType = typ; }
        protected ApianMessage() {}
    }

    public class ApianWrappedCoreMessage : ApianMessage
    {
        public string CoreMsgType;
        public long  CoreMsgTimeStamp;
        public string SerializedCoreMsg;

        public ApianWrappedCoreMessage(string gid, string apianMsgType, ApianCoreMessage coreMsg) : base(gid, apianMsgType)
        {
            CoreMsgType = coreMsg.MsgType;
            CoreMsgTimeStamp = coreMsg.TimeStamp;
            SerializedCoreMsg =   JsonConvert.SerializeObject(coreMsg); // FIXME? This is maybe NOT the right type to serialize?
        }

        public ApianWrappedCoreMessage(string apianMsgType, ApianWrappedCoreMessage inMsg) : base(inMsg.DestGroupId, apianMsgType)
        {
            // Use this convert a request or obs to a command
            CoreMsgType = inMsg.CoreMsgType;
            CoreMsgTimeStamp = inMsg.CoreMsgTimeStamp;
            SerializedCoreMsg = inMsg.SerializedCoreMsg;
        }

        public ApianWrappedCoreMessage() : base() {}

    }

    public class GroupAnnounceMsg : ApianMessage // Send on main channel - no DestGroupId
    {
        public string groupInfoJson;
        public ApianGroupInfo GroupInfo {get => ApianGroupInfo.Deserialize(groupInfoJson);}
        public GroupAnnounceMsg() : base() {}
        public GroupAnnounceMsg(ApianGroupInfo info) : base("", ApianGroupAnnounce)
        {
            groupInfoJson = info.Serialized();
        }
    }

    public class ApianRequest : ApianWrappedCoreMessage
    {
        public ApianRequest(string gid, ApianCoreMessage coreMsg) : base(gid, CliRequest, coreMsg) {}
        public ApianRequest() : base() {}
    }

    public class ApianObservation : ApianWrappedCoreMessage
    {
        public ApianObservation(string gid,ApianCoreMessage coreMsg) : base(gid, CliObservation, coreMsg) {}
        public ApianObservation() : base() {}
    }

    public class ApianCommand : ApianWrappedCoreMessage {
        public long Epoch;
        public long SequenceNum;
        public ApianCommand(long ep, long seqNum, string gid, ApianCoreMessage coreMsg) : base(gid, CliCommand, coreMsg)
        {
            Epoch=ep; SequenceNum=seqNum;
        }
        public ApianCommand(long ep, long seqNum, ApianWrappedCoreMessage wrappedMsg) : base(CliCommand, wrappedMsg)
        {
            Epoch=ep;
            SequenceNum=seqNum;
        }

        public ApianCommand() : base() {}

    }

    public class ApianClockOffsetMsg : ApianMessage // Send on main channel
    {
        public string PeerId;
        public long ClockOffset;
        public ApianClockOffsetMsg(string gid, string pid, long offset) : base(gid, ApianClockOffset) {PeerId=pid; ClockOffset=offset;}
        public ApianClockOffsetMsg() : base() {}
    }


    public class ApianCheckpointMsg : ApianCoreMessage
    {
        // This is a "mock core message" for an ApianCommand to wrap and insert int he command stream.
        public  ApianCheckpointMsg( long timeStamp) : base(ApianMessage.CheckpointMsg, timeStamp) {}
    }

    static public class ApianMessageDeserializer
    {
        // The Apian-internal msesages can all be deserialized by this code.
        // But the "WrappedCoreMsgs" need help from the client AppCore implmentation to recognize and deserialize the app-specific payload

        public static Dictionary<string, Func<string, ApianMessage>> deserializers = new  Dictionary<string, Func<string, ApianMessage>>()
        {
            {ApianMessage.ApianGroupAnnounce, (s) => JsonConvert.DeserializeObject<GroupAnnounceMsg>(s) },
            {ApianMessage.CliRequest, (s) => JsonConvert.DeserializeObject<ApianRequest>(s) },
            {ApianMessage.CliObservation, (s) => JsonConvert.DeserializeObject<ApianObservation>(s) },
            {ApianMessage.CliCommand, (s) => JsonConvert.DeserializeObject<ApianCommand>(s) },
            {ApianMessage.GroupMessage, (s) => JsonConvert.DeserializeObject<ApianGroupMessage>(s) },
            {ApianMessage.ApianClockOffset, (s) => JsonConvert.DeserializeObject<ApianClockOffsetMsg>(s) },
        };

        public static Dictionary<string, Func<ApianMessage, string>> subTypeExtractor = new  Dictionary<string, Func<ApianMessage, string>>()
        {
            {ApianMessage.ApianGroupAnnounce, (msg) => null },
            {ApianMessage.CliRequest, (msg) => (msg as ApianRequest).CoreMsgType }, // Need to use App-level message deserializer to fully decode
            {ApianMessage.CliObservation, (msg) => (msg as ApianObservation).CoreMsgType },
            {ApianMessage.CliCommand, (msg) => (msg as ApianCommand).CoreMsgType },
            {ApianMessage.GroupMessage, (msg) => (msg as ApianGroupMessage).GroupMsgType }, // Need to use ApianGroupMessageDeserializer to fully decode
            {ApianMessage.ApianClockOffset, (msg) => null },
        };

        public static string GetSubType(ApianMessage msg)
        {
            return subTypeExtractor[msg.MsgType](msg);
        }

        public static ApianMessage FromJSON(string msgType, string json)
        {
            // Deserialize once. May have to do it again
            ApianMessage aMsg = deserializers[msgType](json) as ApianMessage;

            string subType = ApianMessageDeserializer.GetSubType(aMsg);

            return  aMsg.MsgType == ApianMessage.GroupMessage ? ApianGroupMessageDeserializer.FromJson(subType, json) : aMsg;
        }

    }



}