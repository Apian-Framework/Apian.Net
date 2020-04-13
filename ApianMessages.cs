
using System;
using System.Linq;
using System.Collections.Generic;
using GameNet;
using UniLog;

namespace Apian
{
    public class ApianClientMsg
    {
        // Client game or app messages derive from this
        public string MsgType;
        public long TimeStamp;
        public ApianClientMsg(string t, long ts) {MsgType = t; TimeStamp = ts;}
        public ApianClientMsg() {}

    }

    public class ApianMessage
    {   
        public const string kCliRequest = "APapRq";         
        public const string kCliObservation = "APapObs";
        public const string kCliCommand = "APapCmd";    
        public const string kApianClockOffset = "APclk"; 
        public const string kGroupMessage = "APGrp";
 
        public string msgType;
        public ApianMessage(string t) => msgType = t;
    }

    public class ApianRequest : ApianMessage
    {
        // Requests (typically from frontend)
        public string cliMsgType;
        public ApianRequest(string clientMsgType) : base(kCliRequest) {cliMsgType=clientMsgType;}
        public ApianRequest() : base(kCliRequest) {}         
    }

    public class ApianObservation : ApianMessage
    {
        public string cliMsgType;
        public ApianObservation(string clientMsgType) : base(kCliObservation) {cliMsgType=clientMsgType;}
        public ApianObservation() : base(kCliObservation) {}         
    }

    public class ApianClockOffsetMsg : ApianMessage // Send on main channel
    {
        public string peerId;
        public long clockOffset;
        public ApianClockOffsetMsg(string pid, long offset) : base(kApianClockOffset) {peerId=pid; clockOffset=offset;}  
    } 

    public class ApianGroupMessage : ApianMessage
    {
        public string groupMsgType;
        public ApianGroupMessage(string _groupMsgType) : base(kGroupMessage) {groupMsgType=_groupMsgType;}
        public ApianGroupMessage() : base(kGroupMessage) {}         
    }
 

    public abstract class ApianAssertion 
    {
        // TODO: WHile it looked good written down, it may be that "ApianAssertion" is a really bad name,
        // given what "assertion" usually means in the world of programming.
        public long SequenceNumber {get; private set;}

        public ApianAssertion( long seq)
        {
            SequenceNumber = seq;
        }
    }
}