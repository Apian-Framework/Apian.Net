
using System;
using System.Linq;
using System.Collections.Generic;
using GameNet;
using UniLog;

namespace Apian
{
    public class ApianMessage
    {   
        public const string kRequestGroups = "APrg";        
        public const string kGroupAnnounce = "APga";
        public const string kGroupJoinReq = "APgjr";        
        public const string kGroupJoinVote = "APgjv";       
        public const string kGroupMemberLeft = "APgml";
        public const string kApianClockOffset = "APclk";  

        public string msgType;
        public ApianMessage(string t) => msgType = t;
    }

    public class RequestGroupsMsg : ApianMessage // Send on main channel
    {
        public RequestGroupsMsg() : base(kRequestGroups) {}  
    } 
    public class GroupAnnounceMsg : ApianMessage // Send on main channel
    {
        // Sent by group members. New members should use it to populate member list. TODO: BFT issue?
        public string groupId;
        public string creatorId;        
        public List<string> memberIds;
        public GroupAnnounceMsg(string gid, string cid, List<string> _members) : base(kGroupAnnounce) {groupId = gid; creatorId=cid; memberIds=_members;}  
    }  

    public class GroupJoinRequestMsg : ApianMessage // Send on main channel
    {
        public string groupId;
        public string peerId;
        public GroupJoinRequestMsg(string id, string pid) : base(kGroupJoinReq) {groupId = id; peerId=pid;}  
    }    

    public class GroupJoinVoteMsg : ApianMessage // Send on main channel
    {
        public string groupId;
        public string peerId;
        public bool approve;
        public GroupJoinVoteMsg(string gid, string pid, bool doIt) : base(kGroupJoinVote) {groupId = gid; peerId=pid; approve=doIt;}  
    }

    public class GroupMemberLefttMsg : ApianMessage // Send on main channel
    {
        public string groupId;
        public string peerId;
        public GroupMemberLefttMsg(string gid, string pid) : base(kGroupMemberLeft) {groupId = gid; peerId=pid;}  
    }  

    public class ApianClockOffsetMsg : ApianMessage // Send on main channel
    {
        public string peerId;
        public long clockOffset;
        public ApianClockOffsetMsg(string pid, long offset) : base(kApianClockOffset) {peerId=pid; clockOffset=offset;}  
    }  

    public abstract class ApianAssertion 
    {
        // TODO: WHile it looked good written down, it may be that "ApianAssertion" is a really bad name,
        // given what "assertion" usually means in the world of programming.
        public long SequenceNumber {get; private set;}

        public ApianAssertion( long seq)
        {
            SequenceNumber = seq;
        }
    }
}