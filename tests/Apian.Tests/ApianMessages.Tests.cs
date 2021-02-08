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

            }
            public PutThingMsg() : base() {}
        }


        public class GetThingMsg : ApianCoreMessage
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

        public class SeeThingMsg : ApianCoreMessage
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

        public class MockAppCore : IApianAppCore
        {
            public Dictionary<string,Func<ApianCoreMessage,ApianCoreMessage,(ApianConflictResult, string)>> conflictFuncs;
            public MockAppCore()
            {
                conflictFuncs = new Dictionary<string,Func<ApianCoreMessage,ApianCoreMessage,(ApianConflictResult, string)>>()
                {
                    {kGetThing+kGetThing, GetAfterGet},
                    {kPutThing+kGetThing, GetAfterPut},
                    {kPutThing+kPutThing, PutAfterPut},
                    {kGetThing+kPutThing, PutAfterGet},
                };
            }

            public (ApianConflictResult, string) GetAfterPut(ApianCoreMessage amsg, ApianCoreMessage bmsg)
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

            public (ApianConflictResult, string) GetAfterGet(ApianCoreMessage amsg, ApianCoreMessage bmsg)
            {
                GetThingMsg msg = amsg as GetThingMsg;
                GetThingMsg msg2 = bmsg as GetThingMsg;
                if (msg.thingName == msg2.thingName)
                    return (ApianConflictResult.Invalidated, "Thing taken already" );
                if (msg.place == msg2.place)
                    return (ApianConflictResult.Invalidated, "Place emptied" );
                return (ApianConflictResult.Unaffected, null);
            }

            public (ApianConflictResult, string) PutAfterPut(ApianCoreMessage amsg, ApianCoreMessage bmsg)
            {
                PutThingMsg msg = amsg as PutThingMsg;
                PutThingMsg msg2 = bmsg as PutThingMsg;
                if (msg.place == msg2.place)
                    return (ApianConflictResult.Invalidated, "Place full already" );
                if (msg.thingName == msg2.thingName)
                    return (ApianConflictResult.Invalidated, "Thing already put elsewhere" );
                return (ApianConflictResult.Unaffected, null);
            }

            public (ApianConflictResult, string) PutAfterGet(ApianCoreMessage amsg, ApianCoreMessage bmsg)
            {
                GetThingMsg msg = amsg as GetThingMsg;
                PutThingMsg msg2 = bmsg as PutThingMsg;
                if (msg.thingName == msg2.thingName)
                    return (ApianConflictResult.Invalidated, "Thing taken already" );
                if (msg.place == msg2.place)
                    return (ApianConflictResult.Invalidated, "Place emptied" );
                return (ApianConflictResult.Unaffected, null);
            }

            public (ApianConflictResult, string) SeeAfterPut(ApianCoreMessage amsg, ApianCoreMessage bmsg)
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

            public (ApianConflictResult, string) SeeAfterGet(ApianCoreMessage amsg, ApianCoreMessage bmsg)
            {
                GetThingMsg msg = amsg as GetThingMsg;
                SeeThingMsg msg2 = bmsg as SeeThingMsg;
                if (msg.place == msg2.place)
                    return (ApianConflictResult.Invalidated, "Place empty" );
                if (msg.thingName == msg2.thingName)
                    return (ApianConflictResult.Invalidated, "Thing no longer there" );
                return (ApianConflictResult.Unaffected, null);
            }
            public (ApianConflictResult result, string reason) ValidateCoreMessages(ApianCoreMessage prevMsg, ApianCoreMessage testMsg)
            {
                string key = prevMsg.MsgType + testMsg.MsgType;
                return   !conflictFuncs.ContainsKey(key)
                    ? (ApianConflictResult.Unaffected, null)
                    : conflictFuncs[key](prevMsg, testMsg) ;

            }
            public void ApplyCheckpointStateData(long seqNum, long timeStamp, string stateHash, string serializedData)
            {
                throw new NotImplementedException();
            }

            public bool CommandIsValid(ApianCoreMessage cmdMsg)
            {
                throw new NotImplementedException();
            }

            public void OnApianCommand(ApianCommand cmd)
            {
                throw new NotImplementedException();
            }

            public void OnCheckpointCommand(long seqNum, long timeStamp)
            {
                throw new NotImplementedException();
            }

            public void SetApianReference(ApianBase apian)
            {
                throw new NotImplementedException();
            }

        }


        [Test]
        public void ApianCoreMessage_BaseCtor()
        {
            StupidTestMsg acm =  new StupidTestMsg();
            Assert.That(acm, Is.Not.Null);
            Assert.That(acm.MsgType, Is.Null);
            Assert.That(acm.TimeStamp, Is.Zero);
        }

        [Test]
        public void ApianCoreMessage_ArgsCtor()
        {
            long ts = 100;

            StupidTestMsg acm =  new StupidTestMsg(ts);
            Assert.That(acm, Is.Not.Null);
            Assert.That(acm.MsgType, Is.EqualTo(kStupidMsg));
            Assert.That(acm.TimeStamp, Is.EqualTo(ts));
        }

        [Test]
        public void Call_validator_not_found()
        {
            long ts1 = 100;
            long ts2 = 200;

            MockAppCore ac = new MockAppCore();
            StupidTestMsg stm =  new StupidTestMsg(ts1);
            PutThingMsg ptm =  new PutThingMsg(ts2, "thing", 999);

            ApianConflictResult v;
            string errStr;
            (v,errStr) = ac.ValidateCoreMessages(ptm, stm);
            Assert.That(v, Is.EqualTo(ApianConflictResult.Unaffected));
            Assert.That(errStr, Is.Null);
        }

        [Test]
        public void Call_validator_validated()
        {
            long ts1 = 100;
            long ts2 = 200;

            MockAppCore ac = new MockAppCore();
            PutThingMsg ptm =  new PutThingMsg(ts1, "thing", 999);
            GetThingMsg gtm =  new GetThingMsg(ts2, "thing", 999);

            ApianConflictResult v;
            string errStr;
            (v,errStr) = ac.ValidateCoreMessages(ptm, gtm); // get after put
            Assert.That(v, Is.EqualTo(ApianConflictResult.Validated));
            Assert.That(errStr, Is.Null);
        }
    }

}