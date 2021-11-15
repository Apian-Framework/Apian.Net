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

        public GroupCoreMessage(string t, long ts) : base(ApianCoreMessage.kGroupMgr, t, ts) {}
        public GroupCoreMessage() {}
    }

    public class CheckpointRequestMsg : GroupCoreMessage
    {
        public CheckpointRequestMsg(long ts) : base(GroupCoreMessage.CheckpointRequest, ts) {}
    }


    public class GroupCoreMessageDeserializer : ApianCoreMessageDeserializer
    {

        public GroupCoreMessageDeserializer() : base()
        {
            coreDeserializers =  coreDeserializers.Concat(
                new  Dictionary<string, Func<string, ApianCoreMessage>>()
                {
                    {GroupCoreMessage.CheckpointRequest, (s) => JsonConvert.DeserializeObject<CheckpointRequestMsg>(s) },
                } ).ToDictionary(x=>x.Key,x=>x.Value);
        }
    }

}