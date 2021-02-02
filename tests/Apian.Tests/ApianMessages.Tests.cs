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
    [TestFixture]
    public class ApianCoreMessageTests
    {
        public const string kGetThing = "getT";
        public const string kPutThing = "putT";
        public const string kSeeThing = "disT";
        public const string kStupidMsg = "stpm";

        public class StupidTestMsg : ApianCoreMessage
        {
            public Dictionary<string, Func<ApianCoreMessage, (ValidState, string)>> IsValidAfterFuncs { get => isValidAfterFuncs; }
            public StupidTestMsg(long ts) : base(kStupidMsg, ts) {}
            public StupidTestMsg() : base() {}
        }

        public class PutThingMsg : ApianCoreMessage
        {
            public string thingName;
            public int place;
            public PutThingMsg(long ts, string tn, int tp) : base(kPutThing, ts)
            {
                thingName = tn;
                place = tp;

                isValidAfterFuncs = new Dictionary<string, Func<ApianCoreMessage,(ApianCoreMessage.ValidState, string)>>()
                {
                    { kPutThing, ValidateAfterPutThing},
                    { kGetThing, ValidateAfterGetThing}
                };

            }
            public PutThingMsg() : base() {}

            public (ApianCoreMessage.ValidState, string) ValidateAfterPutThing(ApianCoreMessage amsg)
            {
                PutThingMsg msg = amsg as PutThingMsg;
                if (msg.place == place)
                    return (ValidState.Invalidated, "Place full already" );
                if (msg.thingName == thingName)
                    return (ValidState.Invalidated, "Thing elsewhere" );
                return (ValidState.Unaffected, null);
            }

            public (ApianCoreMessage.ValidState, string) ValidateAfterGetThing(ApianCoreMessage amsg)
            {
                GetThingMsg msg = amsg as GetThingMsg;
                if (msg.thingName == thingName)
                    return (ValidState.Invalidated, "Thing taken already" );
                if (msg.place == place)
                    return (ValidState.Invalidated, "Place emptied" );
                return (ValidState.Unaffected, null);
            }

        }

        public class GetThingMsg : ApianCoreMessage
        {
            public string thingName;
            public int place;
            public GetThingMsg(long ts, string tn, int tp) : base(kGetThing, ts)
            {
                thingName = tn;
                place = tp;
                isValidAfterFuncs = new Dictionary<string, Func<ApianCoreMessage,(ApianCoreMessage.ValidState, string)>>()
                {
                    { kPutThing, ValidateAfterPutThing},
                    { kGetThing, ValidateAfterGetThing}
                };
            }
            public GetThingMsg() : base() {}
            public (ApianCoreMessage.ValidState, string) ValidateAfterPutThing(ApianCoreMessage amsg)
            {
                PutThingMsg msg = amsg as PutThingMsg;
                if (msg.place == place) // right place
                {
                    if (msg.thingName == thingName)
                        return (ValidState.Validated, null);
                    else
                        return (ValidState.Invalidated, "Wrong thing in place" );
                }
                // Developer's question whether putting right thing in wrong place invalidates.
                return (ValidState.Unaffected, null);
            }

            public (ApianCoreMessage.ValidState, string) ValidateAfterGetThing(ApianCoreMessage amsg)
            {
                GetThingMsg msg = amsg as GetThingMsg;
                if (msg.thingName == thingName)
                    return (ValidState.Invalidated, "Thing taken already" );
                if (msg.place == place)
                    return (ValidState.Invalidated, "Place emptied" );
                return (ValidState.Unaffected, null);
            }

        }

        public class SeeThingMsg : ApianCoreMessage
        {
            public string thingName;
            public int place;
            public SeeThingMsg(long ts, string tn, int tp) : base(kSeeThing, ts)
            {
                thingName = tn;
                place = tp;
                isValidAfterFuncs = new Dictionary<string, Func<ApianCoreMessage,(ApianCoreMessage.ValidState, string)>>()
                {
                    { kPutThing, ValidateAfterPutThing}, // same criteria as GetThingMsg
                    { kGetThing, ValidateAfterGetThing}
                };
            }
            public SeeThingMsg() : base() {}
            public (ApianCoreMessage.ValidState, string) ValidateAfterPutThing(ApianCoreMessage amsg)
            {
                PutThingMsg msg = amsg as PutThingMsg;
                if (msg.place == place) // right place
                {
                    if (msg.thingName == thingName)
                        return (ValidState.Validated, null);
                    else
                        return (ValidState.Invalidated, "Wrong thing in place" );
                }
                // Developer's question whether putting right thing in wrong place invalidates.
                return (ValidState.Unaffected, null);
            }

            public (ApianCoreMessage.ValidState, string) ValidateAfterGetThing(ApianCoreMessage amsg)
            {
                GetThingMsg msg = amsg as GetThingMsg;
                if (msg.place == place)
                    return (ValidState.Invalidated, "Place empty" );
                if (msg.thingName == thingName)
                    return (ValidState.Invalidated, "Thing no longer there" );
                return (ValidState.Unaffected, null);
            }
        }


        [Test]
        public void ApianCoreMessage_BaseCtor()
        {
            StupidTestMsg acm =  new StupidTestMsg();
            Assert.That(acm, Is.Not.Null);
            Assert.That(acm.MsgType, Is.Null);
            Assert.That(acm.TimeStamp, Is.Zero);
            Assert.That(acm.IsValidAfterFuncs, Is.Null);
        }

        [Test]
        public void ApianCoreMessage_ArgsCtor()
        {
            long ts = 100;

            StupidTestMsg acm =  new StupidTestMsg(ts);
            Assert.That(acm, Is.Not.Null);
            Assert.That(acm.MsgType, Is.EqualTo(kStupidMsg));
            Assert.That(acm.TimeStamp, Is.EqualTo(ts));
            Assert.That(acm.IsValidAfterFuncs, Is.Null);
        }

        [Test]
        public void Call_unititialzed_validator()
        {
            long ts1 = 100;
            long ts2 = 200;

            PutThingMsg ptm =  new PutThingMsg(ts1, "thing", 999);
            StupidTestMsg acm =  new StupidTestMsg(ts2);

            ApianCoreMessage.ValidState v;
            string errStr;
            (v,errStr) = acm.IsValidAfter(ptm);
            Assert.That(v, Is.EqualTo(ApianCoreMessage.ValidState.Unaffected));
            Assert.That(errStr, Is.Null);
        }

        public void Call_validator_not_found()
        {
            long ts1 = 100;
            long ts2 = 200;

            StupidTestMsg stm =  new StupidTestMsg(ts1);
            PutThingMsg ptm =  new PutThingMsg(ts2, "thing", 999);

            ApianCoreMessage.ValidState v;
            string errStr;
            (v,errStr) = ptm.IsValidAfter(stm);
            Assert.That(v, Is.EqualTo(ApianCoreMessage.ValidState.Unaffected));
            Assert.That(errStr, Is.Null);
        }

        public void Call_validator_validated()
        {
            long ts1 = 100;
            long ts2 = 200;

            PutThingMsg ptm =  new PutThingMsg(ts1, "thing", 999);
            GetThingMsg gtm =  new GetThingMsg(ts2, "thing", 999);

            ApianCoreMessage.ValidState v;
            string errStr;
            (v,errStr) = gtm.IsValidAfter(ptm);
            Assert.That(v, Is.EqualTo(ApianCoreMessage.ValidState.Validated));
            Assert.That(errStr, Is.Null);
        }


    }



}