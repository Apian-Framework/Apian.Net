using System;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Apian
{

  public class SetLeaderMsg : ApianGroupMessage
    {
        public const string MsgTypeId = "APGslm";

        public string newLeaderId;
        public long newLeaderEpoch;  // 0 for "now"

        public long ElectionTerm;
        public long LastCmdSeqNum;
        public SetLeaderMsg(string groupId, string newLeader, long newEpoch ):  base(groupId, MsgTypeId)
        {
            newLeaderId = newLeader;
            newLeaderEpoch = newEpoch;
        }
    }

 static public class LeaderSezGroupMessageDeserializer
    {
       private static readonly Dictionary<string, Func<string, ApianGroupMessage>> deserializers = new  Dictionary<string, Func<string, ApianGroupMessage>>()
        {
            {SetLeaderMsg.MsgTypeId, (s) => JsonConvert.DeserializeObject<SetLeaderMsg>(s) },
        };

        public static ApianGroupMessage FromJson(string msgType, string json)
        {
            ApianMessage aMsg =  ApianMessageDeserializer.FromJSON(msgType, json); // this message is getting deserialized too many times
            string subType = ApianMessageDeserializer.GetSubType(aMsg);

            // If subType not defned here just decode ApianGroupMessage
            return deserializers.ContainsKey(subType) ? deserializers[subType](json) :null;
        }
    }

}
