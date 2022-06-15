using System;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Apian
{

  public class IAmLeaderMsg : GroupCoreMessage
    {
        public const string MsgTypeId = "APLsIAL";

        public string nextLeaderId;  // can be "" if there's no one to assign
        public long nextLeaderEpoch;  // 0 for don't expire

        public long ElectionTerm;
        public long LastCmdSeqNum;
        public IAmLeaderMsg(long msgTimeStamp, string nextLeader, long nextEpoch ):  base(MsgTypeId, msgTimeStamp)
        {
            nextLeaderId = nextLeader;
            nextLeaderEpoch = nextEpoch;
        }
    }

    public class LeaderSezCoreMgsDeserializer : GroupCoreMessageDeserializer
    {

        public LeaderSezCoreMgsDeserializer() : base()
        {

            coreDeserializers =  coreDeserializers.Concat(
                new  Dictionary<string, Func<string, ApianCoreMessage>>()
                {
                    {IAmLeaderMsg.MsgTypeId, (s) => JsonConvert.DeserializeObject<IAmLeaderMsg>(s) },

                } ).ToDictionary(x=>x.Key,x=>x.Value);
        }
    }

}
