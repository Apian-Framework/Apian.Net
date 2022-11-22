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
        // As do internal GroupManager protocol Messages
        public const int kAppCore = 1;
        public const int kGroupMgr = 2;

        public int SubSys;
        public string MsgType;
        public long TimeStamp; // Apian time when core message happened or gets applied. Not related to network timing.
        public ApianCoreMessage(int sys, string t, long ts) {SubSys=sys; MsgType = t; TimeStamp = ts;}
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

        public string DestGroupId; // Can be empty
        public string MsgType;
        protected ApianMessage(string gid, string typ) { DestGroupId = gid; MsgType = typ; }
        protected ApianMessage() {}
    }

    public class ApianWrappedMessage : ApianMessage
    {
        public int PayloadSubSys;
        public string PayloadMsgType;
        public long  PayloadTimeStamp; // TODO: get rid of this property (I think?)
        public string SerializedPayload;

        public ApianWrappedMessage(string gid, string apianMsgType, ApianCoreMessage payload) : base(gid, apianMsgType)
        {
            PayloadSubSys = payload.SubSys;
            PayloadMsgType = payload.MsgType;
            PayloadTimeStamp = payload.TimeStamp;
            SerializedPayload =   JsonConvert.SerializeObject(payload);
        }


        public ApianWrappedMessage(string apianMsgType, ApianWrappedMessage inMsg) : base(inMsg.DestGroupId, apianMsgType)
        {
            // Use this convert a request or obs to a command
            PayloadSubSys = inMsg.PayloadSubSys;
            PayloadMsgType = inMsg.PayloadMsgType;
            PayloadTimeStamp = inMsg.PayloadTimeStamp;
            SerializedPayload = inMsg.SerializedPayload;
        }

        public ApianWrappedMessage() : base() {}

    }

    public class GroupAnnounceMsg : ApianMessage // Send on main channel - no DestGroupId
    {
        public string groupInfoJson;
        public string groupStatusJson;
        public ApianGroupInfo DecodeGroupInfo() => ApianGroupInfo.Deserialize(groupInfoJson);
        public ApianGroupStatus DecodeGroupStatus() => ApianGroupStatus.Deserialize(groupStatusJson);

        public GroupAnnounceMsg() : base() {}
        public GroupAnnounceMsg(ApianGroupInfo info, ApianGroupStatus curStatus) : base("", ApianGroupAnnounce)
        {
            groupInfoJson = info.Serialized();
            groupStatusJson = curStatus.Serialized();
        }
    }

    public class ApianRequest : ApianWrappedMessage
    {
        public ApianRequest(string gid, ApianCoreMessage coreMsg) : base(gid, CliRequest, coreMsg) {}
        public ApianRequest() : base() {}
    }

    public class ApianObservation : ApianWrappedMessage
    {
        public ApianObservation(string gid,ApianCoreMessage coreMsg) : base(gid, CliObservation, coreMsg) {}
        public ApianObservation() : base() {}
    }

    public class ApianCommand : ApianWrappedMessage {
        public long Epoch;
        public long SequenceNum;
        public ApianCommand(long ep, long seqNum, string gid, ApianCoreMessage coreMsg) : base(gid, CliCommand, coreMsg)
        {
            Epoch=ep; SequenceNum=seqNum;
        }

        public ApianCommand(long ep, long seqNum, string gid, GroupCoreMessage gmMsg) : base(gid, CliCommand, gmMsg)
        {
            Epoch=ep; SequenceNum=seqNum;
        }

        public ApianCommand(long ep, long seqNum, ApianWrappedMessage wrappedMsg) : base(CliCommand, wrappedMsg)
        {
            Epoch=ep;
            SequenceNum=seqNum;
        }

        public ApianCommand() : base() {}

    }

    public class ApianClockOffsetMsg : ApianMessage // Send on main channel
    {
        // TODO: There should be a top-level ApianClockMessage type and Apian should route them to the ApianClock
        // instance for decoding/action. This "offset message" should be a subclass of ApianClockMessage.
        // OTOH, since this is currently the ONLY clock message it's not a priority ATM
        public string PeerAddr;
        public long ClockOffset;
        public ApianClockOffsetMsg(string gid, string pid, long offset) : base(gid, ApianClockOffset) {PeerAddr=pid; ClockOffset=offset;}
        public ApianClockOffsetMsg() : base() {}
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
            {ApianMessage.CliRequest, (msg) => (msg as ApianRequest).PayloadMsgType }, // Need to use App-level message deserializer to fully decode
            {ApianMessage.CliObservation, (msg) => (msg as ApianObservation).PayloadMsgType },
            {ApianMessage.CliCommand, (msg) => (msg as ApianCommand).PayloadMsgType },
            {ApianMessage.GroupMessage, (msg) => (msg as ApianGroupMessage).GroupMsgType }, // Need to use ApianGroupMessageDeserializer to fully decode
            {ApianMessage.ApianClockOffset, (msg) => null },
        };

        public static string GetSubType(ApianMessage msg)
        {
            return subTypeExtractor[msg.MsgType](msg);
        }

        public static string DecodeDestGroup(string apiamMsgJson)
        {
            // Use this to fetch the destgroup from a serialized message
            return JsonConvert.DeserializeObject<ApianMessage>(apiamMsgJson)?.DestGroupId;
        }

        public static ApianMessage FromJSON(string msgType, string json)
        {
            // Deserialize once. May have to do it again - if type not deifined here just stop at ApianMessage
            ApianMessage aMsg = deserializers.ContainsKey(msgType) ? deserializers[msgType](json) : JsonConvert.DeserializeObject<ApianMessage>(json);

            string subType = ApianMessageDeserializer.GetSubType(aMsg);

            // group deser will do the same thing: it may just go as far as ApianGroupMessage
            return  aMsg.MsgType == ApianMessage.GroupMessage ? ApianGroupMessageDeserializer.FromJson(subType, json) : aMsg;
        }

    }

    public abstract class ApianCoreMessageDeserializer
    {
        // Subclass this and include your own core message definitions and then use it
        // in you apian instance to decode core messages that will then include the
        // standard Apian "pseudo CoreMesages"
        protected Dictionary<string, Func<string, ApianCoreMessage>> coreDeserializers;

        protected ApianCoreMessageDeserializer()
        {

            coreDeserializers = new  Dictionary<string, Func<string, ApianCoreMessage>>()
            {  // currently no default "generic" CoreMessage typs
            };
        }
        public ApianCoreMessage FromJSON(string coreMsgType, string json)
        {
            return  coreDeserializers[coreMsgType](json) as ApianCoreMessage;
        }
    }


}