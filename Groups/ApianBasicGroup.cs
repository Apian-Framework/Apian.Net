using System;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json;
using UniLog;

namespace Apian
{

   public class BasicGroupMessages
    {
        public const string GroupRequest = "APrg";
        public const string GroupAnnounce = "APga";
        public const string GroupJoinReq = "APgjr";
        public const string GroupJoinVote = "APgjv";
        public const string GroupMemberLeft = "APgml";


        public class GroupsRequestMsg : ApianGroupMessage // Send on main channel
        {
            public GroupsRequestMsg() : base(GroupRequest) {}
        }

        public class GroupAnnounceMsg : ApianGroupMessage // Send on main channel
        {
            // Sent by group members. New members should use it to populate member list. TODO: BFT issue?
            public string GroupId;
            public string CreatorId;
            public List<string> MemberIds;
            public GroupAnnounceMsg(string gid, string cid, List<string> members) : base(GroupAnnounce) {GroupId = gid; CreatorId=cid; MemberIds=members;}
        }

        public class GroupJoinRequestMsg : ApianGroupMessage // Send on main channel
        {
            public string GroupId;
            public string PeerId;
            public GroupJoinRequestMsg(string id, string pid) : base(GroupJoinReq) {GroupId = id; PeerId=pid;}
        }

        public class GroupJoinVoteMsg : ApianGroupMessage // Send on main channel
        {
            public string GroupId;
            public string PeerId;
            public bool Approve;
            public GroupJoinVoteMsg(string gid, string pid, bool doIt) : base(GroupJoinVote) {GroupId = gid; PeerId=pid; Approve=doIt;}
        }

        public class GroupMemberLefttMsg : ApianGroupMessage // Send on main channel
        {
            public string GroupId;
            public string PeerId;
            public GroupMemberLefttMsg(string gid, string pid) : base(GroupMemberLeft) {GroupId = gid; PeerId=pid;}
        }

       private static Dictionary<string, Func<string, ApianMessage>> deserializers = new  Dictionary<string, Func<string, ApianMessage>>()
        {
            {GroupAnnounce, (s) => JsonConvert.DeserializeObject<GroupAnnounceMsg>(s) },
            {GroupRequest, (s) => JsonConvert.DeserializeObject<GroupsRequestMsg>(s) },
            {GroupJoinReq, (s) => JsonConvert.DeserializeObject<GroupJoinRequestMsg>(s) },
            {GroupJoinVote, (s) => JsonConvert.DeserializeObject<GroupJoinVoteMsg>(s) },
            {GroupMemberLeft, (s) => JsonConvert.DeserializeObject<GroupMemberLefttMsg>(s) },
        };

        public static ApianMessage FromJson(string msgId, string json)
        {
            return deserializers[msgId](json);
        }

    }

    public class ApianBasicGroupManager : BasicGroupMessages, IApianGroupManager
    {
        // Apian Messages

        protected abstract class LocalState
        {
            protected struct JoinVoteKey // for ApianVoteMachine
            {
                public string PeerId;
                public JoinVoteKey(string pid) => PeerId=pid;
            }

            public class GroupData
            {
                public string GroupId;
                public string CreatorId;
                public List<string> MemberIds;

                public GroupData(string gid, string cid, List<string> members) {GroupId=gid; CreatorId=cid; MemberIds=members;}
                public GroupData(GroupAnnounceMsg m) {GroupId=m.GroupId; CreatorId=m.GroupId; MemberIds=m.MemberIds;}
            }


            public const int GroupAnnouceTimeoutMs = 1000;
            public const int GroupAnnouceSendTimeoutMs = 250;
            public const int GroupVoteTimeoutMs = 1000;
            protected ApianBasicGroupManager Group {get; private set;}

            protected readonly Dictionary<string, Action<ApianGroupMessage, string, string>> GroupMsgHandlers;
            protected int NeededJoinVotes(int peerCnt) => peerCnt/2 + 1;
            public abstract void Start();
            public abstract LocalState Update(); // returns isntance to make current and start. Null if done.

            public void OnApianMessage(ApianMessage msg, string msgSrc, string msgChannel)
            {
                if (msg != null && msg.MsgType == ApianMessage.GroupMessage)
                {
                    ApianGroupMessage gMsg = msg as ApianGroupMessage;
                    try {
                        GroupMsgHandlers[gMsg.GroupMsgType](gMsg, msgSrc, msgChannel);
                    } catch (KeyNotFoundException){ }
                }
                else
                    Group.Logger.Warn($"OnGroupMsg(): unexpected APianMsg Type: {msg?.MsgType}");
            }

            public LocalState(ApianBasicGroupManager group)
            {
                Group = group;
                GroupMsgHandlers = new  Dictionary<string, Action<ApianGroupMessage, string, string>>();
            }
         }

        protected class StateListeningForGroup : LocalState
        {
            private long _listenTimeoutMs;
            private GroupData _announcedGroup;

            public StateListeningForGroup(ApianBasicGroupManager group) : base(group)
            {
                GroupMsgHandlers[BasicGroupMessages.GroupAnnounce] = OnGroupAnnounce;
            }

            public override void Start()
            {
                Group.RequestGroups();
                // wait at least 1.5 timeouts, plus a random .5
                // being pretty loosy-goosey with Random bcause it really doesn't matter much
                _listenTimeoutMs = Group.SysMs + 3*GroupAnnouceTimeoutMs/2 + new Random().Next(GroupAnnouceTimeoutMs/2);
                Group.Logger.Verbose($"{this.GetType().Name} - Requested Groups. Listening");
            }
            public override LocalState Update()
            {
                LocalState retVal = this;
                if (_announcedGroup != null)
                    retVal = new StateJoiningGroup(Group, _announcedGroup);

                else if (Group.SysMs > _listenTimeoutMs) // Bail
                {
                    Group.Logger.Verbose($"{this.GetType().Name} - Listen timed out.");
                    retVal = new StateCreatingGroup(Group);
                }
                return retVal;
            }

            protected void OnGroupAnnounce(ApianGroupMessage msg, string msgSrc, string msgChannel)
            {
                _announcedGroup = new GroupData(msg as GroupAnnounceMsg);
                Group.Logger.Verbose($"{this.GetType().Name} - Heard group announced: {_announcedGroup.GroupId}. Joining.");
            }
        }

        protected class StateCreatingGroup : LocalState
        {
            private long _listenTimeoutMs;
            private string _newGroupId;
            private GroupData _otherGroupData; // group we heard announced. We should cancel.

            public StateCreatingGroup(ApianBasicGroupManager group) : base(group)
            {
                GroupMsgHandlers[BasicGroupMessages.GroupAnnounce] = OnGroupAnnounce;
            }
            public override void  Start()
            {
                _newGroupId = "ApianGrp" + Guid.NewGuid().ToString(); // TODO: do this better (details are all hidden in here)
                Group.AnnounceGroup(_newGroupId, Group.LocalP2pId, new List<string>(){Group.LocalP2pId});
                Group.RequestGroups();
                _listenTimeoutMs = Group.SysMs + 3*GroupAnnouceTimeoutMs/2 + new Random().Next(GroupAnnouceTimeoutMs/2);
                Group.Logger.Verbose($"{this.GetType().Name} - Announced new group: {_newGroupId}. Waiting.");
            }
            public override LocalState Update()
            {
                LocalState retVal = this;
                if (_otherGroupData != null) // Cancel back to the  start
                    retVal = new StateListeningForGroup(Group);

                else if (Group.SysMs > _listenTimeoutMs) // We're in!
                {
                    Group.Logger.Verbose($"{this.GetType().Name} - Wait timed out. Joining created group.");
                    retVal = new StateInGroup(Group, new GroupData(_newGroupId, Group.LocalP2pId, new List<string>{Group.LocalP2pId}));
                }
                return retVal;
            }

            private void OnGroupAnnounce(ApianGroupMessage msg, string msgSrc, string msgChannel)
            {
                GroupData heardGroup= new GroupData(msg as GroupAnnounceMsg);
                if (heardGroup.GroupId != _newGroupId)
                {
                    Group.Logger.Verbose($"{this.GetType().Name} - Received an announcement for group: {heardGroup.GroupId}. Bailing.");
                    _otherGroupData = heardGroup; // An announcement from someone else
                }

            }
        }

        protected class StateJoiningGroup : LocalState
        {
            private ApianVoteMachine<JoinVoteKey> _joinVoteMachine;
            private GroupData _newGroup;
            private JoinVoteKey _voteKey; // only care about one
            private long _responseTimeout;

            public StateJoiningGroup(ApianBasicGroupManager group, GroupData groupToJoin) : base(group)
            {
                GroupMsgHandlers[GroupJoinVote] = OnGroupJoinVote;
                _newGroup = groupToJoin;
                _joinVoteMachine = new ApianVoteMachine<JoinVoteKey>(GroupVoteTimeoutMs, GroupVoteTimeoutMs*2, Group.Logger);
                _voteKey =  new JoinVoteKey(Group.LocalP2pId);
                _responseTimeout = Group.SysMs + GroupVoteTimeoutMs; // if no votes by this time give up
            }
            public override void Start()
            {
                Group.RequestToJoinGroup(_newGroup.GroupId);
                Group.Logger.Verbose($"{this.GetType().Name} - Requested join group: {_newGroup.GroupId}. Waiting for votes.");
            }
            public override LocalState Update()
            {
                LocalState retVal = this;
                VoteResult vr = _joinVoteMachine.GetResult(_voteKey);
                if (!vr.WasComplete)
                {
                    switch (vr.Status) // cleans up if done
                    {
                    case VoteStatus.Won:
                        Group.Logger.Verbose($"{this.GetType().Name} - Got enough yes votes.");
                        retVal = new StateInGroup(Group, _newGroup);
                        break;
                    case VoteStatus.Lost:
                        Group.Logger.Warn("{this.GetType().Name} - Failed joining group: lost vote");
                        retVal = new StateListeningForGroup(Group);
                        break;
                    case VoteStatus.NotFound:
                        if (Group.SysMs > _responseTimeout)
                        {
                            Group.Logger.Verbose("{this.GetType().Name} - No one voted. Bailing");
                            retVal = new StateListeningForGroup(Group);
                        }
                        break;
                    }
                }

                return retVal;
            }

            private void OnGroupJoinVote(ApianGroupMessage msg, string msgSrc, string msgChannel)
            {
                GroupJoinVoteMsg gv = (msg as GroupJoinVoteMsg);
                if (gv != null && gv.GroupId == _newGroup.GroupId && gv.PeerId == Group.LocalP2pId)
                {
                    Group.Logger.Verbose($"{this.GetType().Name} - Got a {(gv.Approve ? "yes" : "no")} join vote.");
                    _joinVoteMachine.AddVote(_voteKey, msgSrc, 0, _newGroup.MemberIds.Count);  // msg time is irrelevant here
                }

            }
        }

        protected class StateInGroup : LocalState
        {
            private readonly ApianVoteMachine<JoinVoteKey> _joinVoteMachine;
            private readonly GroupData _groupData;
            private long _groupAnnounceTimeoutMs;
            public StateInGroup(ApianBasicGroupManager group, GroupData groupData) : base(group)
            {
                GroupMsgHandlers[BasicGroupMessages.GroupRequest] = OnGroupRequest;
                GroupMsgHandlers[GroupAnnounce] = OnGroupAnnounce;
                GroupMsgHandlers[GroupJoinReq] = OnGroupJoinRequest;
                GroupMsgHandlers[GroupJoinVote] = OnGroupJoinVote;
                GroupMsgHandlers[GroupMemberLeft] = OnGroupMemberLeft;

                this._groupData = groupData;
                _joinVoteMachine = new ApianVoteMachine<JoinVoteKey>(GroupVoteTimeoutMs, GroupVoteTimeoutMs*2, Group.Logger);
            }
            public override void Start()
            {
                Group.GroupId = _groupData.GroupId;
                Group.GroupCreatorId = _groupData.CreatorId;
                Group.Members.Clear();

                Group.Members[Group.LocalP2pId] = new ApianMember(Group.LocalP2pId); // local host might or might not be in groupdata.
                Group.ApianInst.OnMemberJoinedGroup(Group.LocalP2pId); // notify Apian
                foreach (string mid in _groupData.MemberIds)
                {
                    Group.Members[mid] = new ApianMember(mid);
                    if (mid != Group.LocalP2pId) // local host already announced
                        Group.ApianInst.OnMemberJoinedGroup(mid);
                }
                Group.ListenToGroupChannel(Group.GroupId);
                Group.Logger.Verbose($"{this.GetType().Name} - Joined group: {Group.GroupId}");
                _groupAnnounceTimeoutMs = 0; //

            }

            private long SetGroupAnnounceTimeout()
            {
                return Group.SysMs + GroupAnnouceSendTimeoutMs - new Random().Next(GroupAnnouceSendTimeoutMs/2);
            }

            public override LocalState Update()
            {
                if (_groupAnnounceTimeoutMs > 0 && Group.SysMs > _groupAnnounceTimeoutMs)
                {
                    // Need to send a group announcement?
                    Group.Logger.Verbose($"{this.GetType().Name} - Timeout: announcing group.");
                    Group.AnnounceGroup(Group.GroupId, Group.GroupId, Group.Members.Keys.ToList());
                    _groupAnnounceTimeoutMs = 0;
                }
                return this;
            }

            private void OnGroupRequest(ApianGroupMessage msg, string msgSrc, string msgChannel)
            {
                Group.Logger.Verbose($"{this.GetType().Name} - Received a group request.");
                _groupAnnounceTimeoutMs = SetGroupAnnounceTimeout(); // reset the timer

            }
            private void OnGroupAnnounce(ApianGroupMessage msg, string msgSrc, string msgChannel)
            {
                    GroupAnnounceMsg ga = (msg as GroupAnnounceMsg);
                    if (ga?.GroupId == Group.GroupId)
                    {
                        Group.Logger.Verbose($"{this.GetType().Name} - Received an anouncement for this group.");
                        _groupAnnounceTimeoutMs = 0; // cancel any send (someone else sent it)
                    }
            }
            private void OnGroupJoinRequest(ApianGroupMessage msg, string msgSrc, string msgChannel)
            {
                GroupJoinRequestMsg gr = (msg as GroupJoinRequestMsg);
                Group.Logger.Verbose($"{this.GetType().Name} - Gote a join req for gid: {gr?.GroupId}");
                 if (gr != null && gr.GroupId == Group.GroupId)
                {
                    Group.Logger.Verbose($"{this.GetType().Name} - Received a request to join this group. Voting yes.");
                    Group.VoteOnJoinReq(gr.GroupId, gr.PeerId, true);
                }
            }
            private void OnGroupJoinVote(ApianGroupMessage msg, string msgSrc, string msgChannel)
            {
                GroupJoinVoteMsg gv = (msg as GroupJoinVoteMsg);
                if (gv != null && gv.GroupId == Group.GroupId)
                {
                    Group.Logger.Verbose($"{this.GetType().Name} - Got a {(gv.Approve ? "yes" : "no")} join vote for {gv.PeerId}");
                    JoinVoteKey jvk = new JoinVoteKey(gv.PeerId);
                    _joinVoteMachine.AddVote(jvk, msgSrc, 0, Group.Members.Count);
                    VoteResult result = _joinVoteMachine.GetResult(jvk);
                    if (result.Status == VoteStatus.Won)
                    {
                        if (!Group.Members.Keys.Contains(gv.PeerId))
                        {
                            Group.Members[gv.PeerId] = new ApianMember(gv.PeerId);
                            Group.Logger.Verbose($"{this.GetType().Name} - Added {gv.PeerId} to group");
                            Group.ApianInst.OnMemberJoinedGroup(gv.PeerId); // notify Apian
                        }
                    }
                }
            }
            private void OnGroupMemberLeft(ApianGroupMessage msg, string msgSrc, string msgChannel)
            {
                GroupMemberLefttMsg gml = msg as GroupMemberLefttMsg;
                Group.Logger.Info($"{this.GetType().Name} - Member {gml?.PeerId} left group {gml?.GroupId}");
                if (gml != null && gml.GroupId == Group.GroupId)
                {
                    Group.Logger.Info($"{this.GetType().Name} - Member {gml.PeerId} left group {gml.GroupId}");
                    Group.Members.Remove(gml.PeerId);
                }
            }

        }

        // - - - - - - - - - - - - -
        //

        private ApianBase ApianInst {get; }

        // ReSharper disable MemberCanBePrivate.Global,UnusedMember.Global,FieldCanBeMadeReadOnly.Global
        public string GroupId {get; private set;}
        public string GroupCreatorId {get; private set;}
        public string MainP2pChannel {get; private set;}
        public string LocalP2pId {get; private set;} // need this? Or should we have a localMember reference?
        public Dictionary<string, ApianMember> Members {get; private set;}
        public UniLogger Logger;
        // ReSharper enable MemberCanBePrivate.Global,UnusedMember.Global,FieldCanBeMadeReadOnly.Global
        protected LocalState CurrentState;

        protected long SysMs { get => DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond;}

        public ApianBasicGroupManager(ApianBase apianInst, string mainChannel, string localP2pId)
        {
            Logger = UniLogger.GetLogger("ApianGroup");
            ApianInst = apianInst;
            MainP2pChannel = mainChannel;
            LocalP2pId = localP2pId;
            GroupId = null;
            GroupCreatorId = null;
            Members = new Dictionary<string, ApianMember>();
         }

        private void InitState()
        {
            CurrentState = new StateListeningForGroup(this);
            CurrentState.Start(); // dont want to xcall state methods in group ctor
        }

        public void StartLocalOnlyGroup()
        {
            // Skip the whole startup process. We're the only peer, period.
            LocalState.GroupData newGroup = new LocalState.GroupData("LocalGroup", LocalP2pId,  new List<string>{LocalP2pId});
            CurrentState = new StateInGroup(this, newGroup);
            CurrentState.Start();
        }

        public void Update()
        {
            if (CurrentState == null)
                InitState();

            LocalState newState = CurrentState?.Update();
            if (newState != null && newState != CurrentState)
            {
                CurrentState = newState;
                CurrentState.Start();
            }
        }

        public ApianMessage DeserializeMessage(string groupMsgType, string json)
        {
            return BasicGroupMessages.FromJson(groupMsgType, json);
        }

        public void OnApianMessage(ApianMessage msg, string msgSrc, string msgChan) {
            // Dispatch to current state
            CurrentState?.OnApianMessage(msg, msgSrc, msgChan);
        }

        protected void RequestGroups()
        {
            ApianInst.SendApianMessage(MainP2pChannel, new GroupsRequestMsg());
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