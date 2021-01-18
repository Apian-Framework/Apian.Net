using System.Diagnostics;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;
using Newtonsoft.Json;
using Moq;
using Apian;
using UniLog;

namespace ApianTests
{
    [TestFixture]
    public class ApianBaseTests
    {
        Mock<IApianGameNet> mockGameNet;
        Mock<IApianAppCore> mockAppCore;

        public class TestApianBase : ApianBase // We need a public ctor
        {
            public TestApianBase(IApianGameNet gn, IApianAppCore cl) : base(gn, cl) {  }

            public int MsgHandlerCount => ApMsgHandlers.Count;

            public override void ApplyCheckpointStateData(long seqNum, long timeStamp, string stateHash, string stateData)
            {
                throw new NotImplementedException();
            }

            public override void ApplyStashedApianCommand(ApianCommand cmd)
            {
                throw new NotImplementedException();
            }

            public override void OnApianMessage(string fromId, string toId, ApianMessage msg, long lagMs)
            {
                throw new NotImplementedException();
            }

            public override void OnGroupMemberStatusChange(ApianGroupMember member, ApianGroupMember.Status oldStatus)
            {
                throw new NotImplementedException();
            }

            public override void SendApianMessage(string toChannel, ApianMessage msg)
            {
                throw new NotImplementedException();
            }

            public override void SendCheckpointState(long timeStamp, long seqNum, string serializedState)
            {
                throw new NotImplementedException();
            }

            public override bool Update()
            {
                throw new NotImplementedException();
            }
        }

        [Test]
        public void ApianBase_ConstructorWorks()
        {
            mockGameNet = new Mock<IApianGameNet>(MockBehavior.Strict);
            mockAppCore = new Mock<IApianAppCore>(MockBehavior.Strict);
            mockAppCore.Setup(p => p.SetApianReference(It.IsAny<ApianBase>()));

            // protected ApianBase(IApianGameNet gn, IApianAppCore cl) {
            TestApianBase ap =  new TestApianBase(mockGameNet.Object, mockAppCore.Object);
            Assert.That(ap.GameNet, Is.EqualTo(mockGameNet.Object));
            Assert.That(ap.Client, Is.EqualTo(mockAppCore.Object));
            mockAppCore.Verify(foo => foo.SetApianReference(ap), Times.Once);
            Assert.That(ap.Logger, Is.InstanceOf<UniLogger>());
            Assert.That(ap.MsgHandlerCount, Is.EqualTo(0)); // will also fail if dict isn't there
        }
    }

}