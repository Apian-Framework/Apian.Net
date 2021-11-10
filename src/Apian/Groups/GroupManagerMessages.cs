using System;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Apian
{

    public class GroupManagerMessage : ApianCoreMessage
    {
        // Internal group protocol messages inherit deom this. The are passed around as ApianWrappedMessage payloads
        // and typically end up as ApianCommand payloads and are part of the serial Apian event stream along withwith ApianCoreMessages

        public const string CheckpointRequest = "GmmCkRq";

        public GroupManagerMessage(string t, long ts) : base(ApianCoreMessage.kGroupMgr, t, ts) {}
        public GroupManagerMessage() {}
    }

    public class CheckpointRequestMsg : GroupManagerMessage
    {
        public CheckpointRequestMsg(long ts) : base(GroupManagerMessage.CheckpointRequest, ts) {}
    }


    public class GroupManagerMessageDeserializer : ApianCoreMessageDeserializer
    {

        public GroupManagerMessageDeserializer() : base()
        {
            coreDeserializers =  coreDeserializers.Concat(
                new  Dictionary<string, Func<string, ApianCoreMessage>>()
                {
                    {GroupManagerMessage.CheckpointRequest, (s) => JsonConvert.DeserializeObject<CheckpointRequestMsg>(s) },
                } ).ToDictionary(x=>x.Key,x=>x.Value);
        }
    }

}