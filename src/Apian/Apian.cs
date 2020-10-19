
using System;
using System.Collections.Generic;
using P2pNet;
using UniLog;

namespace Apian
{
    public abstract class ApianBase
    {
		// public API
		// ReSharper disable MemberCanBePrivate.Global,UnusedMember.Global,FieldCanBeMadeReadOnly.Global
        // ReSharper disable UnusedAutoPropertyAccessor.Global,AutoPropertyCanBeMadeGetOnly.Local,NotAccessedField.Global
        protected Dictionary<string, Action<string, string, ApianMessage, long>> ApMsgHandlers;
        // Args are fromId, toId, ApianMsg, msDelay
        public UniLogger Logger;
        public IApianGroupManager ApianGroup  {get; protected set;}
        public IApianClock ApianClock {get; protected set;}
        public IApianGameNet GameNet {get; private set;}
        public IApianAppCore Client {get; private set;}
        protected long SysMs { get => DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond;}
        public string GroupId { get => ApianGroup.GroupId; }
        public string GameId { get => GameNet.CurrentGameId(); }

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
        public abstract void SendApianMessage(string toChannel, ApianMessage msg);


        // Group-related

        public void CreateNewGroup( string groupName) => ApianGroup.CreateNewGroup(groupName);
        public void InitExistingGroup(ApianGroupInfo info) => ApianGroup.InitExistingGroup(info);
        public void JoinGroup(string groupName, string localMemberJson) => ApianGroup.JoinGroup(groupName, localMemberJson);
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