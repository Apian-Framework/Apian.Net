
using System;
using System.Collections.Generic;
using GameNet;
using UniLog;

namespace Apian
{
    public interface IApianClient
    {
        void SetApianReference(ApianBase apian);

    }

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
        protected IGameNet GameNet {get; private set;}
        protected IApianClient Client {get; private set;}
        protected long SysMs { get => DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond;}

        protected ApianBase(IGameNet gn, IApianClient cl) {
            GameNet = gn;
            Client = cl;
            Client.SetApianReference(this);
            Logger = UniLogger.GetLogger("Apian");
            ApMsgHandlers = new Dictionary<string, Action<string, string, ApianMessage, long>>();
            // Add any truly generic handlers here
        }

        public abstract void Update();

        // Apian Messages
        public abstract void OnApianMessage(string fromId, string toId, ApianMessage msg, long lagMs);
        public abstract void SendApianMessage(string toChannel, ApianMessage msg);
        public abstract ApianMessage DeserializeApianMessage(string apianMsgType, string json);

        // Group-related
        public void AddGroupChannel(string channel) => GameNet.AddChannel(channel); // IApianGroupManager uses this. Maybe it should use GameNet directly?
        public void RemoveGroupChannel(string channel) => GameNet.RemoveChannel(channel);
        public abstract void OnMemberJoinedGroup(string peerId); // Any peer, including local. On getting this check with ApianGroup for details.

		// public API
		// ReSharper enable MemberCanBePrivate.Global,UnusedMember.Global,FieldCanBeMadeReadOnly.Global
    }


}