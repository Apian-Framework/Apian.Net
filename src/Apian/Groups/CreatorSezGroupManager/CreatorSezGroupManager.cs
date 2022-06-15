using System.Linq;
using System;
using System.Collections.Generic;
using UniLog;
using static UniLog.UniLogger; // for SID

namespace Apian
{
    public class CreatorSezGroupManager : LeaderDecidesGmBase
    {
        // Factories and UI stuff needs these static const defs
        public const string kGroupType = "CreatorSez";
        public const string kGroupTypeName = "CreatorSez";

        // IApianGroupManager interface needs these non-static defs
        public override string GroupType {get => kGroupType; }
        public override string GroupTypeName {get => kGroupTypeName; }

        public CreatorSezGroupManager(ApianBase apianInst, Dictionary<string,string> config=null) : base(apianInst, config)
        {

        }

    }

}