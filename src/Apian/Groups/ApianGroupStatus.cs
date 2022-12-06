using System.Collections.Generic;
using System.Linq;
using System;
using Newtonsoft.Json;
using P2pNet;
using UniLog;
using static UniLog.UniLogger; // for SID

namespace Apian
{
        [JsonObject(MemberSerialization.OptIn)]
        public class ApianGroupStatus
    {
        // Created via Apian.CurrentGroupStatus()
        [JsonProperty]
        public int PlayerCount {get; private set;}
        [JsonProperty]
        public int ValidatorCount {get; private set;}
        [JsonProperty]
        public int MemberCount {get; private set;}
        [JsonProperty]
        public bool AppCorePaused {get; private set; } // see groupmgr for active pauseInfos

        public ApianGroupStatus(int pCnt, int vCnt, int mCnt, bool corePaused )
        {
            PlayerCount = pCnt;
            ValidatorCount = vCnt;
            MemberCount = mCnt;
            AppCorePaused = corePaused;
        }

        public ApianGroupStatus(ApianGroupStatus ags)
        {
            PlayerCount = ags.PlayerCount;
            ValidatorCount = ags.ValidatorCount;
            MemberCount = ags.MemberCount;
            AppCorePaused = ags.AppCorePaused;
        }

        public ApianGroupStatus() {} // required by Newtonsoft JSON stuff

        public string Serialized() =>  JsonConvert.SerializeObject(this);
        public static ApianGroupStatus Deserialize(string jsonString) => JsonConvert.DeserializeObject<ApianGroupStatus>(jsonString);
    }

}