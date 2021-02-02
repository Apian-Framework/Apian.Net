
using System;
using System.Collections.Generic;
using P2pNet;
using UniLog;

namespace Apian
{
    // ApianBase must (it IS abstract) be subclassed.
    // The expectation is that it will usuall be subclassed twice.
    // The first SubClass ( : ApianBase ) should provide all of the application-specific
    // behavior and APIs
    // The Second should be GroupManager-implmentation-dependant, and should create one
    // and assign ApianBase.GroupMgr. I should also override virtual methods to provide
    // any GroupManager-specific behavior.

    // TODO: ApianBase should check to make sure GroupMgr is not null.

    public abstract class ApianBase
    {
		// public API
        protected Dictionary<string, Action<string, string, ApianMessage, long>> ApMsgHandlers;
        // Args are fromId, toId, ApianMsg, msDelay
        public UniLogger Logger;
        public IApianGroupManager GroupMgr  {get; protected set;}  // set in a sublcass ctor
        public IApianClock ApianClock {get; protected set;}
        public IApianGameNet GameNet {get; private set;}
        public IApianAppCore Client {get; private set;}
        protected long SysMs { get => DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond;}
        public string GroupName { get => GroupMgr.GroupName; }
        public string GameId { get => GameNet.CurrentGameId(); }
        public string GroupId { get => GroupMgr.GroupId; }

        // Command-related stuff
        // protected Dictionary<long, ApianCommand> PendingCommands; // Commands received but not yet applied to the state
        // protected long ExpectedCommandNum { get; set;} // starts at 0 - there should be no skipping unles a data checkpoint is loaded

        protected ApianBase(IApianGameNet gn, IApianAppCore cl) {
            GameNet = gn;
            Client = cl;
            Client.SetApianReference(this);
            Logger = UniLogger.GetLogger("Apian");
            ApMsgHandlers = new Dictionary<string, Action<string, string, ApianMessage, long>>();
            // Add any truly generic handlers here
        }

        public abstract bool Update(); // Returns TRUE is local peer is in active state

        // Apian Messages
        public abstract void OnApianMessage(string fromId, string toId, ApianMessage msg, long lagMs);
        public virtual void SendApianMessage(string toChannel, ApianMessage msg)
        {
            Logger.Verbose($"SendApianMsg() To: {toChannel} MsgType: {msg.MsgType} {((msg.MsgType==ApianMessage.GroupMessage)? "GrpMsgTYpe: "+(msg as ApianGroupMessage).GroupMsgType:"")}");
            GameNet.SendApianMessage(toChannel, msg);
        }

        // Group-related

        public void CreateNewGroup( string groupName) => GroupMgr.CreateNewGroup(groupName);
        public void InitExistingGroup(ApianGroupInfo info) => GroupMgr.InitExistingGroup(info);
        public void JoinGroup(string groupName, string localMemberJson) => GroupMgr.JoinGroup(groupName, localMemberJson);
        public abstract void ApplyCheckpointStateData(long seqNum, long timeStamp, string stateHash, string stateData);

        // FROM GroupManager
        public virtual void OnGroupMemberJoined(ApianGroupMember member)
        {
            // By default just helps getting ApianClock set up.
            // App-specific Apian instance needs to field this if it cares for any other reason.
            // Note that the local gameinstance usually doesn't care about a remote peer joining a group until a Player Joins the gameInst
            // But it usually DOES care about the LOCAL peer's group membership status.

            if (member.PeerId != GameNet.LocalP2pId() &&  ApianClock != null)
            {
                PeerClockSyncData syncData = GameNet.GetP2pPeerClockSyncData(member.PeerId);
                if (syncData == null)
                    Logger.Warn($"ApianBase.OnGroupMemberJoined(): peer {member.PeerId} has no P2pClockSync data");
                else
                {
                    ApianClock.OnP2pPeerSync(member.PeerId, syncData.clockOffsetMs, syncData.networkLagMs);
                    if (!ApianClock.IsIdle)
                        ApianClock.SendApianClockOffset();

                }
            }
        }

        public abstract void OnGroupMemberStatusChange(ApianGroupMember member, ApianGroupMember.Status oldStatus);
        public abstract void ApplyStashedApianCommand(ApianCommand cmd);
        public abstract void SendCheckpointState(long timeStamp, long seqNum, string serializedState); // called by client app

        // CoreApp -> Apian API
        // TODO: You know, these should be interfaces
        protected virtual void SendRequest(string destCh, ApianMessage msg)
        {
            // Make sure these only get sent out if we are ACTIVE.
            // It wouldn't cause any trouble, since the groupmgr would not make it into a command
            // after seeing we aren't active - but there's a lot of message traffic between the 2

            // Also - this func can be overridden in any derived Apian class which is able to
            // be even more selctive (in a server-based group, for instance, if you're not the
            // server then you should just return)
            if (GroupMgr.LocalMember?.CurStatus != ApianGroupMember.Status.Active)
            {
                Logger.Info($"SendRequest() - outgoing message not sent: We are not ACTIVE.");
                return;
            }
            GameNet.SendApianMessage(destCh, msg);
        }

        protected virtual void SendObservation(string destCh, ApianMessage msg)
        {
            // See comments in SendRequest
            if (GroupMgr.LocalMember?.CurStatus != ApianGroupMember.Status.Active)
            {
                Logger.Info($"SendObservation() - outgoing message not sent: We are not ACTIVE.");
                return;
            }
            GameNet.SendApianMessage(destCh, msg);
        }

        // Other stuff
        public void OnP2pPeerSync(string remotePeerId, long clockOffsetMs, long netLagMs) // sys + offset = apian
        {
            // TODO: This is awkward.
            ApianClock?.OnP2pPeerSync( remotePeerId,  clockOffsetMs,  netLagMs);
        }

		// public API
		// ReSharper enable MemberCanBePrivate.Global,UnusedMember.Global,FieldCanBeMadeReadOnly.Global
    }


}