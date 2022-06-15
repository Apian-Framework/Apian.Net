using System.Linq;
using System;
using System.Collections.Generic;
using UniLog;
using static UniLog.UniLogger; // for SID

namespace Apian
{
    public class LeaderSezGroupManager : LeaderDecidesGmBase
    {
        // Factories and UI stuff needs these static const defs
        public const string kGroupType = "LeaderSez";
        public const string kGroupTypeName = "LeaderSez";

        // IApianGroupManager interface needs these non-static defs
        public override string GroupType {get => kGroupType; }
        public override string GroupTypeName {get => kGroupTypeName; }

        protected string nextLeaderId; // null == no failover leader defined
        protected long nextLeaderFirstEpoch; // epoch # when nextLeader takes over. 0 means no planned handover.

        public LeaderSezGroupManager(ApianBase apianInst, Dictionary<string,string> config=null) : base(apianInst, config)
        {
            groupMgrMsgDeser = new LeaderSezCoreMgsDeserializer(); // includes default + LeaaderSez GoupCore messages
        }



        public override ApianCoreMessage DeserializeGroupMessage(ApianWrappedMessage aMsg)
        {
            return groupMgrMsgDeser.FromJSON(aMsg.PayloadMsgType, aMsg.SerializedPayload);
        }

    }

}