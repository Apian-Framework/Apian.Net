using System;
using System.Linq;
using System.Collections.Generic;
using UniLog;

namespace Apian
{
    public class ApianMember
    {
        public enum Status
        {
            kNew,  // just created         
            kSyncing, // In the process of P2pNet syncing and getting up-to-date
            kJoining, // In the process of joining a group
            kActive, // part of the gang
            kMissing, // not currently present, but only newly so
        }

        public string P2pId {get; private set;}
        public Status status;

        public ApianMember(string _p2pId)
        {
            status = Status.kNew;
            P2pId = _p2pId;
        }
    }

    public interface IApianGroupManager
    {
        string GroupId {get;}
        string GroupCreatorId {get;}        
        string LocalP2pId {get;}
        Dictionary<string, ApianMember> Members {get;}
        void Update();
        void OnApianMsg(ApianMessage msg, string msgSrc, string msgChan);    
        void StartLocalOnlyGroup();
    }

    public class ApianBasicGroupManager : IApianGroupManager
    {
        protected abstract class LocalState
        {
            protected struct JoinVoteKey // for ApianVoteMachine
            {
                public string peerId;
                public JoinVoteKey(string _pid) => peerId=_pid;
            }

            public class GroupData 
            {
                public string groupId;
                public string creatorId;
                public List<string> memberIds;

                public GroupData(string gid, string cid, List<string> _members) {groupId=gid; creatorId=cid; memberIds=_members;}
                public GroupData(GroupAnnounceMsg m) {groupId=m.groupId; creatorId=m.groupId; memberIds=m.memberIds;}
            }

            public const int kGroupAnnouceTimeoutMs = 1000;
            public const int kGroupAnnouceSendTimeoutMs = 250;            
            public const int kGroupVoteTimeoutMs = 1000;
            protected ApianBasicGroupManager Group {get; private set;}

            protected int NeededJoinVotes(int peerCnt) => peerCnt/2 + 1;
            public abstract void Start();
            public abstract LocalState Update(); // returns isntance to make current and start. Null if done.
            public virtual void OnApianMsg(ApianMessage msg, string msgSrc, string msgChannel) {}           

            public LocalState(ApianBasicGroupManager group) { Group = group;}
         }

        protected class StateListeningForGroup : LocalState
        {
            long listenTimeoutMs;
            GroupData announcedGroup;

            public StateListeningForGroup(ApianBasicGroupManager group) : base(group) {}           

            public override void Start() 
            {
                Group.RequestGroups();
                // wait at least 1.5 timeouts, plus a random .5
                // being pretty loosy-goosey with Random bcause it really doesn't matter much
                listenTimeoutMs = Group.SysMs + 3*kGroupAnnouceTimeoutMs/2 + new Random().Next(kGroupAnnouceTimeoutMs/2);
                Group.logger.Verbose($"{this.GetType().Name} - Requested Groups. Listening");
            }
            public override LocalState Update()
            {
                LocalState retVal = this;
                if (announcedGroup != null)
                    retVal = new StateJoiningGroup(Group, announcedGroup);

                else if (Group.SysMs > listenTimeoutMs) // Bail
                {
                    Group.logger.Verbose($"{this.GetType().Name} - Listen timed out.");
                    retVal = new StateCreatingGroup(Group);
                }
                return retVal;
            }

            public override void OnApianMsg(ApianMessage msg, string msgSrc, string msgChannel)
            {
                switch (msg.msgType)
                {
                case ApianMessage.kGroupAnnounce:
                    announcedGroup = new GroupData(msg as GroupAnnounceMsg);                
                    Group.logger.Verbose($"{this.GetType().Name} - Heard group announced: {announcedGroup.groupId}. Joining.");
                    break;
                }
            }
        }

        protected class StateCreatingGroup : LocalState
        {
            long listenTimeoutMs;            
            string newGroupId;
            GroupData otherGroupData; // group we heard announced. We should cancel.       
            
            public StateCreatingGroup(ApianBasicGroupManager group) : base(group) {}               
            public override void  Start() 
            {
                newGroupId = "ApianGrp" + System.Guid.NewGuid().ToString(); // TODO: do this better (details are all hidden in here)
                Group.AnnounceGroup(newGroupId, Group.LocalP2pId, new List<string>(){Group.LocalP2pId});
                Group.RequestGroups();
                listenTimeoutMs = Group.SysMs + 3*kGroupAnnouceTimeoutMs/2 + new Random().Next(kGroupAnnouceTimeoutMs/2);
                Group.logger.Verbose($"{this.GetType().Name} - Announced new group: {newGroupId}. Waiting.");                
            }
            public override LocalState Update()
            {
                LocalState retVal = this;
                if (otherGroupData != null) // Cancel back to the  start
                    retVal = new StateListeningForGroup(Group);

                else if (Group.SysMs > listenTimeoutMs) // We're in! 
                {
                    Group.logger.Verbose($"{this.GetType().Name} - Wait timed out. Joining created group.");
                    retVal = new StateInGroup(Group, new GroupData(newGroupId, Group.LocalP2pId, new List<string>{Group.LocalP2pId}));
                }
                return retVal;                
            }

            public override void OnApianMsg(ApianMessage msg, string msgSrc, string msgChannel)
            {
                switch (msg.msgType)
                {
                case ApianMessage.kGroupAnnounce:
                    GroupData heardGroup= new GroupData(msg as GroupAnnounceMsg);
                    if (heardGroup.groupId != newGroupId) 
                    {
                        Group.logger.Verbose($"{this.GetType().Name} - Received an announcement for group: {heardGroup.groupId}. Bailing.");
                        otherGroupData = heardGroup; // An announcement from someone else
                    }
                    break;
                }
            }            
        }

        protected class StateJoiningGroup : LocalState
        {
            protected ApianVoteMachine<JoinVoteKey> joinVoteMachine;            
            protected GroupData newGroup;         
            protected JoinVoteKey voteKey; // only care about one
            protected long responseTimeout;

            public StateJoiningGroup(ApianBasicGroupManager group, GroupData _groupToJoin) : base(group) 
            {
                newGroup = _groupToJoin;
                joinVoteMachine = new ApianVoteMachine<JoinVoteKey>(kGroupVoteTimeoutMs, kGroupVoteTimeoutMs*2, Group.logger);
                voteKey =  new JoinVoteKey(Group.LocalP2pId);
                responseTimeout = Group.SysMs + kGroupVoteTimeoutMs; // if no votes by this time give up
            }               
            public override void Start() 
            {
                Group.RequestToJoinGroup(newGroup.groupId);
                Group.logger.Verbose($"{this.GetType().Name} - Requested join group: {newGroup.groupId}. Waiting for votes.");              
            }
            public override LocalState Update()
            {
                LocalState retVal = this;
                VoteResult vr = joinVoteMachine.GetResult(voteKey);
                if (!vr.wasComplete)
                {
                switch (vr.status) // cleans up if done
                    {
                    case VoteStatus.kWon:
                        Group.logger.Verbose($"{this.GetType().Name} - Got enough yes votes.");                    
                        retVal = new StateInGroup(Group, newGroup);                  
                        break;
                    case VoteStatus.kLost:
                        Group.logger.Warn("{this.GetType().Name} - Failed joining group: lost vote");
                        retVal = new StateListeningForGroup(Group);
                        break;
                    case VoteStatus.kNotFound:
                        if (Group.SysMs > responseTimeout)
                        {
                            Group.logger.Verbose("{this.GetType().Name} - No one voted. Bailing");
                            retVal = new StateListeningForGroup(Group);                        
                        }
                        break;
                    }
                }

                return retVal;
            }

            public override void OnApianMsg(ApianMessage msg, string msgSrc, string msgChannel)
            {
                switch (msg.msgType)
                {
                case ApianMessage.kGroupJoinVote: // our own is in here as well
                    GroupJoinVoteMsg gv = (msg as GroupJoinVoteMsg);
                    if (gv.groupId == newGroup.groupId && gv.peerId == Group.LocalP2pId)
                    {
                        Group.logger.Verbose($"{this.GetType().Name} - Got a {(gv.approve ? "yes" : "no")} join vote.");                        
                        joinVoteMachine.AddVote(voteKey, msgSrc, 0, newGroup.memberIds.Count);  // msg time is irrelevant here
                    }
                    break;
                }
            }
        }    

        protected class StateInGroup : LocalState
        {
            protected ApianVoteMachine<JoinVoteKey> joinVoteMachine;
            protected GroupData groupData;            
            protected long groupAnnounceTimeoutMs;
            public StateInGroup(ApianBasicGroupManager group, GroupData _groupData) : base(group) 
            {
                groupData = _groupData;
                joinVoteMachine = new ApianVoteMachine<JoinVoteKey>(kGroupVoteTimeoutMs, kGroupVoteTimeoutMs*2, Group.logger);
            }               
            public override void Start() 
            {
                Group.GroupId = groupData.groupId;
                Group.GroupCreatorId = groupData.creatorId;
                Group.Members.Clear();

                Group.Members[Group.LocalP2pId] = new ApianMember(Group.LocalP2pId); // local host might or might not be in groupdata. 
                Group.ApianInst.OnMemberJoinedGroup(Group.LocalP2pId); // notify Apian                     
                foreach (string mid in groupData.memberIds)
                {
                    Group.Members[mid] = new ApianMember(mid);
                    if (mid != Group.LocalP2pId) // local host already announced
                        Group.ApianInst.OnMemberJoinedGroup(mid);                    
                }
                Group.ListenToGroupChannel(Group.GroupId);
                Group.logger.Verbose($"{this.GetType().Name} - Joined group: {Group.GroupId}"); 
                groupAnnounceTimeoutMs = 0; //   
           
            }

            private long SetGroupAnnounceTimeout() 
            {
                return Group.SysMs + kGroupAnnouceSendTimeoutMs - new Random().Next(kGroupAnnouceSendTimeoutMs/2);
            }

            public override LocalState Update()
            {
                if (groupAnnounceTimeoutMs > 0 && Group.SysMs > groupAnnounceTimeoutMs) 
                {
                    // Need to send a group announcement?
                    Group.logger.Verbose($"{this.GetType().Name} - Timeout: announcing group.");                    
                    Group.AnnounceGroup(Group.GroupId, Group.GroupId, Group.Members.Keys.ToList());
                    groupAnnounceTimeoutMs = 0;                    
                }
                return this;
            }

            public override void OnApianMsg(ApianMessage msg, string msgSrc, string msgChannel)
            {
                // TODO: get rid of switch
                switch (msg.msgType)
                {
                case ApianMessage.kRequestGroups:
                    Group.logger.Verbose($"{this.GetType().Name} - Received a group request.");                
                    groupAnnounceTimeoutMs = SetGroupAnnounceTimeout(); // reset the timer
                    break;
                case ApianMessage.kGroupAnnounce:
                    GroupAnnounceMsg ga = (msg as GroupAnnounceMsg);
                    if (ga.groupId == Group.GroupId)  
                    {
                        Group.logger.Verbose($"{this.GetType().Name} - Received an anouncement for this group.");
                        groupAnnounceTimeoutMs = 0; // cancel any send (someone else sent it)
                    }
                    break;
                case ApianMessage.kGroupJoinReq:
                    GroupJoinRequestMsg gr = (msg as GroupJoinRequestMsg);
                    Group.logger.Verbose($"{this.GetType().Name} - Gote a join req for gid: {gr.groupId}");
                    if (gr.groupId == Group.GroupId)  
                    {
                        Group.logger.Verbose($"{this.GetType().Name} - Received a request to join this group. Voting yes.");
                        Group.VoteOnJoinReq(gr.groupId, gr.peerId, true);
                    }
                    break;  
                case ApianMessage.kGroupJoinVote: // our own is in here as well
                    GroupJoinVoteMsg gv = (msg as GroupJoinVoteMsg);
                    if (gv.groupId == Group.GroupId)
                    {
                        Group.logger.Verbose($"{this.GetType().Name} - Got a {(gv.approve ? "yes" : "no")} join vote for {gv.peerId}"); 
                        JoinVoteKey jvk = new JoinVoteKey(gv.peerId);                 
                        joinVoteMachine.AddVote(jvk, msgSrc, 0, Group.Members.Count);
                        VoteResult result = joinVoteMachine.GetResult(jvk);
                        if (result.status == VoteStatus.kWon)
                        {
                            if (!Group.Members.Keys.Contains(gv.peerId))
                            {
                                Group.Members[gv.peerId] = new ApianMember(gv.peerId);
                                Group.logger.Verbose($"{this.GetType().Name} - Added {gv.peerId} to group");  
                                Group.ApianInst.OnMemberJoinedGroup(gv.peerId); // notify Apian  
                            }                                                       
                        }
                    }
                    break;     
                case ApianMessage.kGroupMemberLeft:
                    GroupMemberLefttMsg gml = msg as GroupMemberLefttMsg;  
                    Group.logger.Info($"{this.GetType().Name} - Member {gml.peerId} left group {gml.groupId}");                     
                    if (gml.groupId == Group.GroupId)
                    {
                        Group.logger.Info($"{this.GetType().Name} - Member {gml.peerId} left group {gml.groupId}");                          
                        Group.Members.Remove(gml.peerId);
                    }
                    break;           
                }
            }  

        }

        // - - - - - - - - - - - - -
        //

        public ApianBase ApianInst {get; private set;}
        public string GroupId {get; private set;}
        public string GroupCreatorId {get; private set;}
        public string MainP2pChannel {get; private set;}
        public string LocalP2pId {get; private set;} // need this? Or should we have a localMember reference?
        public Dictionary<string, ApianMember> Members {get; private set;}
        public UniLogger logger;

        protected LocalState currentState;

        protected long SysMs { get => DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond;}

        public ApianBasicGroupManager(ApianBase _apianInst, string _mainChannel, string _localP2pId)
        {
            logger = UniLogger.GetLogger("ApianGroup");            
            ApianInst = _apianInst;
            MainP2pChannel = _mainChannel;
            LocalP2pId = _localP2pId;
            GroupId = null;
            GroupCreatorId = null;
            Members = new Dictionary<string, ApianMember>();

         }

        protected void InitState()
        {
            currentState = new StateListeningForGroup(this);
            currentState.Start(); // dont want to xcall state methods in group ctor   
        }

        public void StartLocalOnlyGroup()
        {
            // Skip the whole startup process. We're the only peer, period.
            LocalState.GroupData newGroup = new LocalState.GroupData("LocalGroup", LocalP2pId,  new List<string>{LocalP2pId});
            currentState = new StateInGroup(this, newGroup);
            currentState.Start();
            
        }

        public void Update()
        {
            if (currentState == null)
                InitState();

            LocalState newState = currentState?.Update();
            if (newState != null && newState != currentState)
            {
                currentState = newState;
                currentState.Start();
            }
        }

        public void OnApianMsg(ApianMessage msg, string msgSrc, string msgChan) {
            // Dispatch to current state
            currentState?.OnApianMsg(msg, msgSrc, msgChan);
        }

        protected void RequestGroups() 
        {
            ApianInst.SendApianMessage(MainP2pChannel, new RequestGroupsMsg());
        }

        protected void AnnounceGroup(string groupId, string groupCreator, List<string> members) 
        {
            ApianInst.SendApianMessage(MainP2pChannel, new GroupAnnounceMsg(groupId, groupCreator, members));
        }
        protected void RequestToJoinGroup(string groupId) 
        {
            ApianInst.SendApianMessage(MainP2pChannel, new GroupJoinRequestMsg(groupId, LocalP2pId));
        }        

        protected void VoteOnJoinReq(string groupId, string peerid, bool vote) 
        {
            ApianInst.SendApianMessage(MainP2pChannel, new GroupJoinVoteMsg(groupId, peerid, vote));
        }  
        protected void ListenToGroupChannel(string groupChannel)
        {
            ApianInst.AddGroupChannel(groupChannel);
        }


    }
}