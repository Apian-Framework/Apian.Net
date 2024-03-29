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
    public class GroupMemberLimits
    {
        // ( Min, Max) - Zero means "unspecified"
        [JsonProperty]
        public (int, int) Players {get; private set;}
        [JsonProperty]
        public (int, int) Validators {get; private set;}

        // For members, "min" is just the sum, because zero reall means zero
        // for "max", on the other hand, since zero means "unlimited" it's zero if either is zero, else it's the sum
        public (int, int) Members {get => (Players.Item1+Validators.Item1,  (Players.Item2==0 || Validators.Item2==0) ? 0 : Players.Item2+Validators.Item2); }


        public GroupMemberLimits()
        {
            Players = (0,0);
            Validators = (0,0);
         }
        public GroupMemberLimits( (int, int) playerLimits, (int, int) validatorLimits, (int, int) memberLimits)
        {
            Players = playerLimits;
            Validators = validatorLimits;
           }

        public bool IsEquivalentTo(GroupMemberLimits other)
            =>  (Players == other.Players && Validators == other.Validators );


        public int MinPlayers { get => Players.Item1;}
        public int MaxPlayers { get => Players.Item2;}
        public int MinValidators { get => Validators.Item1;}
        public int MaxValidators { get => Validators.Item2;}
        public int MinMembers { get => Members.Item1;}
        public int MaxMembers { get => Members.Item2;}
    }

    [JsonObject(MemberSerialization.OptIn)]
    public class ApianGroupInfo
    {
        public const string GROUPINFO_VERSION = "1.0.1";

        public const string AnchorPostsNone = "None";
        public const string AnchorPostsCreator = "CreatorPosts";
        public const string AnchorPostsLeader = "LeaderPosts";

        [JsonProperty]
        public string Version = GROUPINFO_VERSION;
        [JsonProperty]
        public string GroupType;
        [JsonProperty]
        public P2pNetChannelInfo GroupChannelInfo;
        [JsonProperty]
        public string GroupCreatorAddr;
        [JsonProperty]
        public string GroupName ; // TODO: Note that this is not just GroupChannelInfo?.id - decide what it should be and replace this with the explanation
        [JsonProperty]
        public string AnchorAddr; // Anchor Contract address
        [JsonProperty]
        public string AnchorPostAlg; // How do checkpoint state anchors get posted to the chain?
        [JsonProperty]
        public GroupMemberLimits MemberLimits;
        [JsonProperty]
        protected Dictionary<string, string> OtherProperties; // subclasses will have accesors for them

        public static readonly IList<string> AnchorPostAlgorithms = new List<string>(){AnchorPostsNone, AnchorPostsCreator, AnchorPostsLeader};

        public string GroupId { get => GroupChannelInfo?.id;} // channel id IS the group session id.
        public string SessionId { get => GroupChannelInfo?.id;} // Since groupId is the sessionId then this alias should be there.
                                                                // TODO: maybe most/all of the references to GroupId should be changed to say SessionId?

        public string GroupFriendlyId { get => $"{GroupName}-{GroupId}"; } // todo: maybe truncate ID?

        public  ApianGroupInfo(string groupType, P2pNetChannelInfo groupChannel, string creatorAddr, string groupName, string anchorAddr, string anchorPostAlg, GroupMemberLimits memberLimits)
        {
            GroupType = groupType;
            GroupChannelInfo = groupChannel;
            GroupCreatorAddr = creatorAddr;
            GroupName = groupName;
            MemberLimits = memberLimits;
            AnchorAddr = anchorAddr;
            AnchorPostAlg = anchorPostAlg ?? AnchorPostsNone;
            OtherProperties = new Dictionary<string, string>();
        }

        protected ApianGroupInfo(ApianGroupInfo agi)
        {
            // TODO: custom exception for error handling
            if (agi.Version != ApianGroupInfo.GROUPINFO_VERSION)
                throw new Exception($"GroupInfo version mismatch. Group: {agi.GroupId} Version: {agi.Version} should be {ApianGroupInfo.GROUPINFO_VERSION}");
            GroupType = agi.GroupType;
            GroupChannelInfo = agi.GroupChannelInfo;
            GroupCreatorAddr = agi.GroupCreatorAddr;
            AnchorAddr = agi.AnchorAddr;
            AnchorPostAlg = agi.AnchorPostAlg;
            GroupName = agi.GroupName;
            MemberLimits = agi.MemberLimits;
            OtherProperties = agi.OtherProperties;
        }

        public ApianGroupInfo() {} // required by Newtonsoft JSON stuff

        public string Serialized() =>  JsonConvert.SerializeObject(this);
        public static ApianGroupInfo Deserialize(string jsonString) => JsonConvert.DeserializeObject<ApianGroupInfo>(jsonString);
        public virtual bool IsEquivalentTo(ApianGroupInfo agi2)
        {
            // subclasses need to compare Otherproperties
            // TODO: Does anything use this? Delete it?
            return GroupType.Equals(agi2.GroupType, System.StringComparison.Ordinal)
                && GroupChannelInfo.IsEquivalentTo(agi2.GroupChannelInfo)
                && GroupCreatorAddr.Equals(agi2.GroupCreatorAddr, System.StringComparison.Ordinal)
                && GroupName.Equals(agi2.GroupName, System.StringComparison.Ordinal)
                && AnchorAddr.Equals(agi2.AnchorAddr, System.StringComparison.Ordinal)
                && AnchorPostAlg.Equals(agi2.AnchorPostAlg, System.StringComparison.Ordinal)
                && MemberLimits.IsEquivalentTo(agi2.MemberLimits);
        }
    }
}