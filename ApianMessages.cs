using System;
using System.Collections.Generic;
using Newtonsoft.Json;
namespace Apian
{
    // ReSharper disable UnusedType.Global,NotAccessedFIeld.Global,FieldCanBeMadeReadOnly.Global,UnusedMember.Global
    // (can;t be readonly because of NewtonSoft.JSON)

    public class ApianClientMsg
    {
        // ReSharper disable MemberCanBePrivate.Global
        // Client game or app messages derive from this
        public string MsgType;
        public long TimeStamp;
        public ApianClientMsg(string t, long ts) {MsgType = t; TimeStamp = ts;}
        public ApianClientMsg() {}
        // ReSharper enable MemberCanBePrivate.Global
    }

    public class ApianMessage
    {
        // ReSharper disable MemberCanBeProtected.Global
        public const string CliRequest = "APapRq";
        public const string CliObservation = "APapObs";
        public const string CliCommand = "APapCmd";
        public const string ApianClockOffset = "APclk";
        public const string GroupMessage = "APGrp";

        public string MsgType;
       // ReSharper enable MemberCanBeProtected.Global

        protected ApianMessage(string t) => MsgType = t;
    }

    public class ApianRequest : ApianMessage
    {
        // Requests (typically from frontend)
        public string CliMsgType;
        public ApianRequest(string clientMsgType) : base(CliRequest) {CliMsgType=clientMsgType;}
        public ApianRequest() : base(CliRequest) {}
    }

    public class ApianObservation : ApianMessage
    {
        public string CliMsgType;
        public ApianObservation(string clientMsgType) : base(CliObservation) {CliMsgType=clientMsgType;}
        public ApianObservation() : base(CliObservation) {}
    }

    public class ApianCommand : ApianMessage
    {
        public string CliMsgType;
        public ApianCommand(string clientMsgType) : base(CliCommand) {CliMsgType=clientMsgType;}
        public ApianCommand() : base(CliCommand) {}
    }

    public class ApianClockOffsetMsg : ApianMessage // Send on main channel
    {
        public string PeerId;
        public long ClockOffset;
        public ApianClockOffsetMsg(string pid, long offset) : base(ApianClockOffset) {PeerId=pid; ClockOffset=offset;}
    }

    public class ApianGroupMessage : ApianMessage
    {
        public string GroupMsgType;
        public ApianGroupMessage(string groupMsgType) : base(GroupMessage) {GroupMsgType=groupMsgType;}
        public ApianGroupMessage() : base(GroupMessage) {}
    }

    static public class ApianMessageDeserializer
    {
        // IMPORTANT: this only deserialized to the Apian[Foo]Msg level. In many cases all that gets you is
        // an app-specific "subType" and you then have to do it again at the App message level.
        // TODO: This is super fugly. Make it not.
        public static Dictionary<string, Func<string, ApianMessage>> deserializers = new  Dictionary<string, Func<string, ApianMessage>>()
        {
            {ApianMessage.CliRequest, (s) => JsonConvert.DeserializeObject<ApianRequest>(s) },
            {ApianMessage.CliObservation, (s) => JsonConvert.DeserializeObject<ApianObservation>(s) },
            {ApianMessage.CliCommand, (s) => JsonConvert.DeserializeObject<ApianCommand>(s) },
            {ApianMessage.GroupMessage, (s) => JsonConvert.DeserializeObject<ApianGroupMessage>(s) },
            {ApianMessage.ApianClockOffset, (s) => JsonConvert.DeserializeObject<ApianClockOffsetMsg>(s) },
        };

        public static Dictionary<string, Func<ApianMessage, string>> subTypeExtractor = new  Dictionary<string, Func<ApianMessage, string>>()
        {
            {ApianMessage.CliRequest, (msg) => (msg as ApianRequest).CliMsgType },
            {ApianMessage.CliObservation, (msg) => (msg as ApianObservation).CliMsgType },
            {ApianMessage.CliCommand, (msg) => (msg as ApianCommand).CliMsgType },
            {ApianMessage.GroupMessage, (msg) => (msg as ApianGroupMessage).GroupMsgType },
            {ApianMessage.ApianClockOffset, (msg) => null },
        };

        public static ApianMessage FromJSON(string msgType, string json)
        {
            return deserializers[msgType](json) as ApianMessage;
        }
        public static string GetSubType(ApianMessage msg)
        {
            return subTypeExtractor[msg.MsgType](msg);
        }


    }



}