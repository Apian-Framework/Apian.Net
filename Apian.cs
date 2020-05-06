
using System;
using System.Collections.Generic;
using GameNet;
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
        public IApianClientApp Client {get; private set;}
        protected long SysMs { get => DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond;}
        public string GroupId { get => ApianGroup.GroupId; }
        public string GameId { get => GameNet.CurrentGameId(); }

        protected ApianBase(IApianGameNet gn, IApianClientApp cl) {
            GameNet = gn;
            Client = cl;
            Client.SetApianReference(this);
            Logger = UniLogger.GetLogger("Apian");
            ApMsgHandlers = new Dictionary<string, Action<string, string, ApianMessage, long>>();
            // Add any truly generic handlers here
        }

        public abstract void Update();

        // Client

        // Apian Messages
        public abstract void OnApianMessage(string fromId, string toId, ApianMessage msg, long lagMs);
        public abstract void SendApianMessage(string toChannel, ApianMessage msg);

        // Group-related

        public void CreateNewGroup(string groupId, string groupName) => ApianGroup.CreateNewGroup(groupId, groupName);

        public void InitExistingGroup(ApianGroupInfo info) => ApianGroup.InitExistingGroup(info);
        public void JoinGroup(string groupId, string localMemberJson) => ApianGroup.JoinGroup(groupId, localMemberJson);

        // FROM GroupManager
        public abstract void OnGroupMemberJoined(string groupMemberJson); // App-specific Apian instance needs to field this
        public abstract void OnGroupMemberStatus(string peerId, ApianGroupMember.Status newStatus);

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