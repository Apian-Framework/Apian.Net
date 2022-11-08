using System;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Apian
{

    public class GroupCoreMessage : ApianCoreMessage
    {
        // Internal group protocol messages inherit deom this. The are passed around as ApianWrappedMessage payloads
        // and typically end up as ApianCommand payloads and are part of the serial Apian event stream along withwith ApianCoreMessages

        public const string CheckpointRequest = "GmmCkRq";
        public const string PauseAppCore = "GcmPs";
        public const string ResumeAppCore = "GcmRs";

        public GroupCoreMessage(string t, long ts) : base(ApianCoreMessage.kGroupMgr, t, ts) {}
        public GroupCoreMessage() : base() {}

    }

    public class CheckpointRequestMsg : GroupCoreMessage
    {
        public CheckpointRequestMsg(long ts) : base(GroupCoreMessage.CheckpointRequest, ts) {}

        public CheckpointRequestMsg() : base() {}
    }

    public class PauseAppCoreMsg : GroupCoreMessage
    {
        public string reason; // descriptive "why": ("quorum Lost", "Bob requested"...)
        public string instanceId; // a unique id for resume to use.


        public PauseAppCoreMsg(long ts, string _reason, string _instance) : base(GroupCoreMessage.PauseAppCore, ts)
        {
            reason = _reason;
            instanceId = _instance;
        }
        public PauseAppCoreMsg() : base() {}

    }

    public class ResumeAppCoreMsg : GroupCoreMessage
    {
        public string instanceId; // using pause command identifier.
        public ResumeAppCoreMsg(long ts, string isntance) : base(GroupCoreMessage.PauseAppCore, ts)
        {
            instanceId = isntance;
        }
    }

    public class GroupCoreMessageDeserializer : ApianCoreMessageDeserializer
    {

        public GroupCoreMessageDeserializer() : base()
        {
            coreDeserializers =  coreDeserializers.Concat(
                new  Dictionary<string, Func<string, ApianCoreMessage>>()
                {
                    {GroupCoreMessage.CheckpointRequest, (s) => JsonConvert.DeserializeObject<CheckpointRequestMsg>(s) },
                    {GroupCoreMessage.PauseAppCore, (s) => JsonConvert.DeserializeObject<PauseAppCoreMsg>(s) },
                    {GroupCoreMessage.ResumeAppCore, (s) => JsonConvert.DeserializeObject<ResumeAppCoreMsg>(s) },
                } ).ToDictionary(x=>x.Key,x=>x.Value);
        }
    }

}