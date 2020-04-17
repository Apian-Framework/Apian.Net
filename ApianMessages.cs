

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

}