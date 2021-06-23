using System.Linq;
using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using UniLog;
using static UniLog.UniLogger; // for SID

namespace Apian
{
    public class RatfishApianCommand : ApianCommand
    {
        public long ElectionTerm;

        public RatfishApianCommand(long term, long ep, long seqNum, string gid, ApianCoreMessage coreMsg) : base(ep, seqNum, gid, coreMsg)
        {
            ElectionTerm = term;
        }
        public RatfishApianCommand(long term, long ep, long seqNum, ApianWrappedCoreMessage wrappedMsg) : base(ep, seqNum, wrappedMsg)
        {
            ElectionTerm = term;
        }
        public RatfishApianCommand() : base() {}   // need this for NewtonSoft.Json to work
    }

    public class RatfishElection
    {
        public enum RfRole : int {
            kFollower = 0,
            kCandidate = 1,
            kLeader = 2,
        };

        public string CurrentLeader { get; private set; }
        public long CurrentTerm { get; private set; }

        protected ApianBase ApianInst;
        protected string VotedFor; // this term

        protected Dictionary<string,string> ConfigDict;

        public RatfishElection(ApianBase _apian, Dictionary<string,string> _config)
        {
            ApianInst = _apian;
            ConfigDict = _config;
        }

        public void Update()
        {

        }

    }

    static public class RatfishMessageDeserializer
    {
        public static Dictionary<string, Func<string, ApianMessage>> rfDeserializers = new  Dictionary<string, Func<string, ApianMessage>>()
        {
            {ApianMessage.CliCommand, (s) => JsonConvert.DeserializeObject<RatfishApianCommand>(s) },
        };

        public static ApianMessage FromJSON(string msgType, string json)
        {
            // Deserialize once. May have to do it again
            ApianMessage aMsg =  rfDeserializers.ContainsKey(msgType) ? rfDeserializers[msgType](json) as ApianMessage : null;
            return aMsg;
        }
    }
}