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
    public class ApianBaseTests
    {
        Mock<IApianGameNet> mockGameNet;
        Mock<IApianAppCore> mockAppCore;

        public class TestApianBase : ApianBase // We need a public ctor
        {
            public TestApianBase(IApianGameNet gn, IApianAppCore core, IApianClock clock=null) : base(gn, core)
            {
                ApianClock = clock;
            }

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

        [Test]
        public void ApianBase_OnGroupMemberJoined()
        {
            const string localP2pId = "localP2pId";
            const string remoteId = "remoteId";
            PeerClockSyncData peerSyncData = new PeerClockSyncData(remoteId, 132, 17, 42);

            mockGameNet = new Mock<IApianGameNet>(MockBehavior.Strict);
            mockGameNet.Setup(p => p.LocalP2pId()).Returns(localP2pId);

            // If peerId is "noSync" it returns null
            mockGameNet.Setup(gn => gn.GetP2pPeerClockSyncData(It.IsAny<string>())).Returns(peerSyncData);
            mockGameNet.Setup(gn => gn.GetP2pPeerClockSyncData("noSync")).Returns(null as PeerClockSyncData);

            mockAppCore = new Mock<IApianAppCore>(MockBehavior.Strict);
            mockAppCore.Setup(p => p.SetApianReference(It.IsAny<ApianBase>()));

            Mock<IApianClock> mClock = new Mock<IApianClock>(MockBehavior.Strict);
            mClock.Setup(cl => cl.OnP2pPeerSync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<long>()));
            mClock.Setup(cl => cl.IsIdle).Returns(false);
            mClock.Setup(cl => cl.SendApianClockOffset());

            ApianGroupMember member = new ApianGroupMember(remoteId, "[some json]");
            TestApianBase ap =  new TestApianBase(mockGameNet.Object, mockAppCore.Object, mClock.Object);

            ap.OnGroupMemberJoined(member);
            mockGameNet.Verify(gn => gn.GetP2pPeerClockSyncData(It.IsAny<string>()), Times.Once);
            mClock.Verify(cl => cl.OnP2pPeerSync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<long>()), Times.Once);
            mClock.Verify(cl => cl.SendApianClockOffset(), Times.Once);

            ApianGroupMember noSyncMember = new ApianGroupMember("noSync", "[other json]");
            ap.OnGroupMemberJoined(noSyncMember);
            mockGameNet.Verify(gn => gn.GetP2pPeerClockSyncData(It.IsAny<string>()), Times.Exactly(2));
            mClock.Verify(cl => cl.OnP2pPeerSync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<long>()), Times.Once); // Not called
            mClock.Verify(cl => cl.SendApianClockOffset(), Times.Once);  // didn;t get called

            ApianGroupMember localMember = new ApianGroupMember(localP2pId, "[other json]");
            ap.OnGroupMemberJoined(localMember);
            mockGameNet.Verify(gn => gn.GetP2pPeerClockSyncData(It.IsAny<string>()), Times.Exactly(2)); // Not called
        }

    }

}