using System.Diagnostics;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;
using Newtonsoft.Json;
using Moq;
using P2pNet;
using Apian;
using UniLog;

namespace ApianTests
{
    // CoreMessages
    public class MockCoreMessage : ApianCoreMessage
    {
        public MockCoreMessage(string typ, long ts) : base(typ, ts) {}
        public MockCoreMessage() : base() {}

        // types
        public const string kGetThing = "getT";
        public const string kPutThing = "putT";
        public const string kSeeThing = "disT";
        public const string kStupidMsg = "stpm";
    }

    public class StupidTestMsg : MockCoreMessage
    {
        public StupidTestMsg(long ts) : base(kStupidMsg, ts) {}
        public StupidTestMsg() : base() {}
    }

    public class PutThingMsg : MockCoreMessage
    {
        public string thingName;
        public int place;
        public PutThingMsg(long ts, string tn, int tp) : base(kPutThing, ts)
        {
            thingName = tn;
            place = tp;
        }
        public PutThingMsg() : base() {}
    }

    public class GetThingMsg : MockCoreMessage
    {
        public string thingName;
        public int place;
        public GetThingMsg(long ts, string tn, int tp) : base(kGetThing, ts)
        {
            thingName = tn;
            place = tp;
        }
        public GetThingMsg() : base() {}

    }

    public class SeeThingMsg : MockCoreMessage
    {
        public string thingName;
        public int place;
        public SeeThingMsg(long ts, string tn, int tp) : base(kSeeThing, ts)
        {
            thingName = tn;
            place = tp;
        }
        public SeeThingMsg() : base() {}
    }

    // Wrapped Apian Messages
    public class PutThingObservation : ApianObservation
    {
        public override ApianCoreMessage ClientMsg {get => putThingMsg;}
        public PutThingMsg putThingMsg;
        public PutThingObservation(string gid, PutThingMsg _putThingMsg) : base(gid, _putThingMsg) {putThingMsg=_putThingMsg;}
        public PutThingObservation() : base() {}
        //public override ApianCommand ToCommand(long seqNum) => new ApianPlaceClaimCommand(seqNum, DestGroupId, placeClaimMsg);
    }

    public class GetThingObservation : ApianObservation
    {
        public override ApianCoreMessage ClientMsg {get => getThingMsg;}
        public GetThingMsg getThingMsg;
        public GetThingObservation(string gid, GetThingMsg _getThingMsg) : base(gid, _getThingMsg) {getThingMsg=_getThingMsg;}
        public GetThingObservation() : base() {}
        //public override ApianCommand ToCommand(long seqNum) => new ApianPlaceClaimCommand(seqNum, DestGroupId, placeClaimMsg);
    }
    public class SeeThingObservation : ApianObservation
    {
        public override ApianCoreMessage ClientMsg {get => seeThingMsg;}
        public SeeThingMsg seeThingMsg;
        public SeeThingObservation(string gid, SeeThingMsg _seeThingMsg) : base(gid, _seeThingMsg) {seeThingMsg=_seeThingMsg;}
        public SeeThingObservation() : base() {}
        //public override ApianCommand ToCommand(long seqNum) => new ApianPlaceClaimCommand(seqNum, DestGroupId, placeClaimMsg);
    }

    // Validator
    public static class MockAppCoreValidator
    {
        public static Dictionary<string,Func<ApianCoreMessage,ApianCoreMessage,(ApianConflictResult, string)>> conflictFuncs
            = new Dictionary<string,Func<ApianCoreMessage,ApianCoreMessage,(ApianConflictResult, string)>>()
        {
            {MockCoreMessage.kGetThing+MockCoreMessage.kGetThing, GetAfterGet},
            {MockCoreMessage.kPutThing+MockCoreMessage.kGetThing, GetAfterPut},
            {MockCoreMessage.kPutThing+MockCoreMessage.kPutThing, PutAfterPut},
            {MockCoreMessage.kGetThing+MockCoreMessage.kPutThing, PutAfterGet},
        };


        public static (ApianConflictResult result, string reason) ValidateCoreMessages(ApianCoreMessage prevMsg, ApianCoreMessage testMsg)
        {
            string key = prevMsg.MsgType + testMsg.MsgType;

            // Test the sorting
            if ( prevMsg.TimeStamp > testMsg.TimeStamp) // ok to be equal
                throw new ArgumentException($"Observations are out of order");

            return   !conflictFuncs.ContainsKey(key)
                ? (ApianConflictResult.Unaffected, null)
                : conflictFuncs[key](prevMsg, testMsg) ;
        }

        public static (ApianConflictResult, string) GetAfterPut(ApianCoreMessage amsg, ApianCoreMessage bmsg)
        {
            PutThingMsg msg = amsg as PutThingMsg;
            GetThingMsg msg2 = bmsg as GetThingMsg;
            if (msg.place == msg2.place) // right place
            {
                if (msg.thingName == msg2.thingName)
                    return (ApianConflictResult.Validated, null);
                else
                    return (ApianConflictResult.Invalidated, "Wrong thing in place" );
            }
            // Developer's question whether putting right thing in wrong place invalidates.
            return (ApianConflictResult.Unaffected, null);
        }

        public static (ApianConflictResult, string) GetAfterGet(ApianCoreMessage amsg, ApianCoreMessage bmsg)
        {
            GetThingMsg msg = amsg as GetThingMsg;
            GetThingMsg msg2 = bmsg as GetThingMsg;
            if (msg.thingName == msg2.thingName)
                return (ApianConflictResult.Invalidated, "Thing taken already" );
            if (msg.place == msg2.place)
                return (ApianConflictResult.Invalidated, "Place emptied" );
            return (ApianConflictResult.Unaffected, null);
        }

        public static (ApianConflictResult, string) PutAfterPut(ApianCoreMessage amsg, ApianCoreMessage bmsg)
        {
            PutThingMsg msg = amsg as PutThingMsg;
            PutThingMsg msg2 = bmsg as PutThingMsg;
            if (msg.place == msg2.place)
                return (ApianConflictResult.Invalidated, "Place full already" );
            if (msg.thingName == msg2.thingName)
                return (ApianConflictResult.Invalidated, "Thing already put elsewhere" );
            return (ApianConflictResult.Unaffected, null);
        }

        public static (ApianConflictResult, string) PutAfterGet(ApianCoreMessage amsg, ApianCoreMessage bmsg)
        {
            GetThingMsg msg = amsg as GetThingMsg;
            PutThingMsg msg2 = bmsg as PutThingMsg;
            if (msg.thingName == msg2.thingName)
                return (ApianConflictResult.Invalidated, "Thing taken already" );
            if (msg.place == msg2.place)
                return (ApianConflictResult.Invalidated, "Place emptied" );
            return (ApianConflictResult.Unaffected, null);
        }

        public static (ApianConflictResult, string) SeeAfterPut(ApianCoreMessage amsg, ApianCoreMessage bmsg)
        {
            PutThingMsg msg = amsg as PutThingMsg;
            SeeThingMsg msg2 = bmsg as SeeThingMsg;
            if (msg.place == msg2.place) // right place
            {
                if (msg.thingName == msg2.thingName)
                    return (ApianConflictResult.Validated, null);
                else
                    return (ApianConflictResult.Invalidated, "Wrong thing in place" );
            }
            // Developer's question whether putting right thing in wrong place invalidates.
            return (ApianConflictResult.Unaffected, null);
        }

        public static (ApianConflictResult, string) SeeAfterGet(ApianCoreMessage amsg, ApianCoreMessage bmsg)
        {
            GetThingMsg msg = amsg as GetThingMsg;
            SeeThingMsg msg2 = bmsg as SeeThingMsg;
            if (msg.place == msg2.place)
                return (ApianConflictResult.Invalidated, "Place empty" );
            if (msg.thingName == msg2.thingName)
                return (ApianConflictResult.Invalidated, "Thing no longer there" );
            return (ApianConflictResult.Unaffected, null);
        }
    }
}