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

    public class TestApianBase : ApianBase //  We need a public ctor
    {
        public Mock<IApianGroupManager> GrpMgrMock;
        public TestApianBase(IApianGameNet gn, IApianAppCore core, IApianClock clock=null) : base(gn, core)
        {
            ApianGroupMember local = new ApianGroupMember("localPeerId", null);
            local.CurStatus = ApianGroupMember.Status.Active;

            GrpMgrMock = new Mock<IApianGroupManager>();
            GrpMgrMock.Setup( g => g.LocalMember).Returns( local );
            GrpMgrMock.Setup( g => g.SendApianObservation(It.IsAny<ApianCoreMessage>()));

            ApianClock = clock;
            GroupMgr = GrpMgrMock.Object;

        }

        public int MsgHandlerCount => ApMsgHandlers.Count;

        public override ApianGroupMember CreateGroupMember(string peerId, string appMemberDataJson)
        {
            return new ApianGroupMember(peerId, appMemberDataJson);
        }

        public override void ApplyCheckpointStateData(long epoch, long seqNum, long timeStamp, string stateHash, string stateData)
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

        public override void Update()
        {
            throw new NotImplementedException();
        }

        public override (bool, string) CheckQuorum()
        {
            throw new NotImplementedException();
        }
    }

    [TestFixture]
    public class ApianBaseTests
    {
        Mock<IApianGameNet> mockGameNet;
        Mock<IApianAppCore> mockAppCore;
        [Test]
        public void ApianBase_ConstructorWorks()
        {
            mockGameNet = new Mock<IApianGameNet>(MockBehavior.Strict);

            mockAppCore = new Mock<IApianAppCore>(MockBehavior.Strict);
            mockAppCore.Setup(p => p.SetApianReference(It.IsAny<ApianBase>()));

            TestApianBase ap =  new TestApianBase(mockGameNet.Object, mockAppCore.Object);
            Assert.That(ap.GameNet, Is.EqualTo(mockGameNet.Object));
            Assert.That(ap.AppCore, Is.EqualTo(mockAppCore.Object));
            mockAppCore.Verify(foo => foo.SetApianReference(ap), Times.Once);
            Assert.That(ap.Logger, Is.InstanceOf<UniLogger>());
            Assert.That(ap.MsgHandlerCount, Is.EqualTo(5)); // will also fail if dict isn't there
        }

        [Test]
        public void ApianBase_OnGroupMemberJoined()
        {
            const string localP2pId = "localP2pId";
            const string remoteId = "remoteId";
            const long  cnt = 5,
                        since = 10,
                        offset = -10,
                        lag = 46;
            const double offsetSigma = 3.14, lagSigma = 4.2;
            // PeerClockSyncInfo(string pid, long cnt, long since, long offset, double offsetSigma, long lag, double lagSigma)
            PeerClockSyncInfo peerSyncData = new PeerClockSyncInfo(remoteId, cnt, since, offset, offsetSigma, lag, lagSigma);

            mockGameNet = new Mock<IApianGameNet>(MockBehavior.Strict);
            mockGameNet.Setup(p => p.LocalP2pId()).Returns(localP2pId);

            mockGameNet.Setup(p => p.OnPeerJoinedGroup(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string>() ));

            // If peerId is "noSync" it returns null
            mockGameNet.Setup(gn => gn.GetP2pClockSyncInfo(It.IsAny<string>())).Returns(peerSyncData);
            mockGameNet.Setup(gn => gn.GetP2pClockSyncInfo("noSync")).Returns(null as PeerClockSyncInfo);

            mockAppCore = new Mock<IApianAppCore>(MockBehavior.Strict);
            mockAppCore.Setup(p => p.SetApianReference(It.IsAny<ApianBase>()));

            Mock<IApianClock> mClock = new Mock<IApianClock>(MockBehavior.Strict);
            mClock.Setup(cl => cl.IsIdle).Returns(false);
            mClock.Setup(cl => cl.OnNewPeer(It.IsAny<string>()));


            ApianGroupMember member = new ApianGroupMember(remoteId, "[some json]");
            TestApianBase ap =  new TestApianBase(mockGameNet.Object, mockAppCore.Object, mClock.Object);

            ap.OnGroupMemberJoined(member);
            mockGameNet.Verify(gn => gn.GetP2pClockSyncInfo(It.IsAny<string>()), Times.Exactly(0));
            mClock.Verify(cl => cl.OnNewPeer(It.IsAny<string>()), Times.Once);

            ApianGroupMember noSyncMember = new ApianGroupMember("noSync", "[other json]");
            ap.OnGroupMemberJoined(noSyncMember);
            mockGameNet.Verify(gn => gn.GetP2pClockSyncInfo(It.IsAny<string>()), Times.Exactly(0));
            mClock.Verify(cl => cl.OnNewPeer(It.IsAny<string>()), Times.Exactly(2));

            ApianGroupMember localMember = new ApianGroupMember(localP2pId, "[other json]");
            ap.OnGroupMemberJoined(localMember);
            mockGameNet.Verify(gn => gn.GetP2pClockSyncInfo(It.IsAny<string>()), Times.Exactly(0));
        }

    }

    [TestFixture]
    public class IApianAppCoreServicesTests
    {

        [Test]
        public void ApianBase_ObservationSet_order()
        {
            Mock<IApianGameNet> mockGameNet = new Mock<IApianGameNet>(MockBehavior.Strict);
            mockGameNet.Setup(gn => gn.SendApianMessage(It.IsAny<string>(),It.IsAny<ApianMessage>()));

            Mock<IApianAppCore> mockAppCore = new Mock<IApianAppCore>(MockBehavior.Strict);
            mockAppCore.Setup(p => p.SetApianReference(It.IsAny<ApianBase>()));
            mockAppCore.Setup(p => p.ValidateCoreMessages(It.IsAny<ApianCoreMessage>(), It.IsAny<ApianCoreMessage>()))
                .Returns( (ApianCoreMessage m1, ApianCoreMessage m2) => MockAppCoreValidator.ValidateCoreMessages(m1,m2));

            TestApianBase apian = new TestApianBase(mockGameNet.Object, mockAppCore.Object);

            apian.StartObservationSet();

            apian.SendObservation( new GetThingMsg(200, "thing3", 12) );
            apian.SendObservation( new GetThingMsg(100, "thing1",  6) );
            apian.SendObservation( new GetThingMsg(150, "thing2",  10) );

            apian.EndObservationSet();

            apian.GrpMgrMock.Verify(g => g.SendApianObservation(It.IsAny<ApianCoreMessage>()), Times.Exactly(3));
        }
    }

}