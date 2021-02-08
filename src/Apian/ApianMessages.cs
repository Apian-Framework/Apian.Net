using System;
using System.Collections.Generic;
using Newtonsoft.Json;
namespace Apian
{
    // ReSharper disable UnusedType.Global,NotAccessedFIeld.Global,FieldCanBeMadeReadOnly.Global,UnusedMember.Global
    // (can;t be readonly because of NewtonSoft.JSON)

     public enum ApianConflictResult { Unaffected, Validated, Invalidated }

    public class ApianCoreMessage
    {
        // ReSharper disable MemberCanBePrivate.Global
        // Client game or app messages derive from this
        public string MsgType;
        public long TimeStamp; // Apian time when core message happened or gets applied. Not related to network timing.
        public ApianCoreMessage(string t, long ts) {MsgType = t; TimeStamp = ts;}
        public ApianCoreMessage() {}
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
        public const string CheckpointMsg = "APchk";
        public string DestGroupId; // Can be empty
        public string MsgType;
       // ReSharper enable MemberCanBeProtected.Global
        protected ApianMessage(string gid, string typ) { DestGroupId = gid; MsgType = typ; }
        protected ApianMessage() {}
    }

    public class ApianWrappedClientMessage : ApianMessage
    {
        public string CliMsgType; // TODO: This is a hack and is a copy of the ApianClientMessage MsgType
                                  // It's related to deserializing from JSON into an ApianWrappedClientMessage
                                  // and me not wanting to include the full derived class names in the data stream.

        [JsonIgnore]
        public virtual ApianCoreMessage ClientMsg {get;}
        public ApianWrappedClientMessage(string gid, string apianMsgType, string clientMsgType) : base(gid, apianMsgType)
        {
            CliMsgType=clientMsgType;
        }
        public ApianWrappedClientMessage() : base() {}

    }


    public class ApianRequest : ApianWrappedClientMessage
    {
        public ApianRequest(string gid, ApianCoreMessage clientMsg) : base(gid, CliRequest, clientMsg.MsgType) {}
        public ApianRequest() : base() {}
        public virtual ApianCommand ToCommand(long seqNum) {return null;}
    }

    public class ApianObservation : ApianWrappedClientMessage
    {
        public ApianObservation(string gid,ApianCoreMessage clientMsg) : base(gid, CliObservation, clientMsg.MsgType) {}
        public ApianObservation() : base() {}
        public virtual ApianCommand ToCommand(long seqNum) {return null;}
    }

    public class ApianCommand : ApianWrappedClientMessage {
        public long SequenceNum;
        public ApianCommand(long seqNum, string gid, ApianCoreMessage clientMsg) : base(gid, CliCommand, clientMsg.MsgType) {SequenceNum=seqNum;}
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
        // This is a "mock client command" for an ApianCheckpointCommand to "wrap"
        public  ApianCheckpointMsg( long timeStamp) : base(ApianMessage.CheckpointMsg, timeStamp) {}
    }

    public class ApianCheckpointCommand : ApianCommand
    {
        // A checkpoint request is implemented as an ApianCommand so it can:
        // - Explicitly specify an "epoch" for the checkpoint (its sequence number)
        // - Be part of the serial command stream. Client commands are strictly evaluated
        // and applied in order. By being a command the request can guarantee that it is processed
        // by all peers on an app state that has the identical commands applied - and will take advantage
        // of the ordering mechanism
        public override ApianCoreMessage ClientMsg {get => checkpointMsg;}
        public ApianCheckpointMsg checkpointMsg;
        public ApianCheckpointCommand(long seqNum, string gid, ApianCheckpointMsg _checkpointMsg) : base(seqNum, gid, _checkpointMsg) {checkpointMsg=_checkpointMsg;}
        public ApianCheckpointCommand() : base() {}
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
            {ApianMessage.CliRequest, (msg) => (msg as ApianRequest).CliMsgType }, // Need to use App-level message deserializer to fully decode
            {ApianMessage.CliObservation, (msg) => (msg as ApianObservation).CliMsgType },
            {ApianMessage.CliCommand, (msg) => (msg as ApianCommand).CliMsgType },
            {ApianMessage.GroupMessage, (msg) => (msg as ApianGroupMessage).GroupMsgType }, // Need to use ApianGroupMessageDeserializer to fully decode
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