
using System.Xml.Linq;
using System.Reflection.Emit;
using System;
using System.Linq;
using System.Collections.Generic;
using GameNet;
using UniLog;

namespace Apian
{
    public interface IApianClient 
    {
        void OnApianAssertion(ApianAssertion aa);
    }

    public abstract class ApianBase
    {
        protected Dictionary<string, Action<string, string, string, long>> ApMsgHandlers;
        public UniLogger logger; 

        public IApianGroupManager ApianGroup  {get; protected set;}    
        public IApianClock ApianClock {get; protected set;}  
        protected IGameNet GameNet {get; private set;}      
        protected long SysMs { get => DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond;}

        public ApianBase(IGameNet gn) {
            GameNet = gn;          
            logger = UniLogger.GetLogger("Apian");             
            ApMsgHandlers = new Dictionary<string, Action<string, string, string, long>>(); 
            // Add any truly generic handlers here          
        }

        public abstract void Update();
        
        // Apian Messages
        public abstract void OnApianMessage(string msgType, string msgJson, string fromId, string toId, long lagMs);         
        public abstract void SendApianMessage(string toChannel, ApianMessage msg);

        // Group-related
        public void AddGroupChannel(string channel) => GameNet.AddChannel(channel); // IApianGroupManager uses this. Maybe it should use GameNet directly?
        public void RemoveGroupChannel(string channel) => GameNet.RemoveChannel(channel);
        public abstract void OnMemberJoinedGroup(string peerId); // Any peer, including local. On getting this check with ApianGroup for details.
  
    }
  
 
}