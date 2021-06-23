using System.Security.Cryptography;
using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using GameNet;
using P2pNet;
using System.Threading.Tasks;

namespace Apian
{
    public interface IApianGameNet : IGameNet
    {
        void AddApianInstance( ApianBase instance, string groupId);
        void RequestGroups();
        Task<Dictionary<string, ApianGroupInfo>> RequestGroupsAsync(int timeoutMs);
        void OnApianGroupMemberStatus( string groupId, string peerId, ApianGroupMember.Status newStatus, ApianGroupMember.Status prevStatus);
        void SendApianMessage(string toChannel, ApianMessage appMsg);
        PeerClockSyncData GetP2pPeerClockSyncData(string P2pPeerId);

    }

    public class ApianNetworkPeer
    {
        // This is a GameNet/P2pNet peer. There is only one of these per node, no matter
        // how many ApianInstances/Groups there are.

        public string P2pId;
        public string P2NetpHelloData; // almost always JSON

        public ApianNetworkPeer(string p2pId, string helloData)
        {
            P2pId = p2pId;
            P2NetpHelloData = helloData;
        }
    }

    public abstract class ApianGameNetBase : GameNetBase, IApianGameNet
    {
        // This is the actual GameNet instance
        public IApianApplication Client {get => client as IApianApplication;}
        public Dictionary<string,ApianNetworkPeer> Peers; // keyed by p2pid
        public Dictionary<string, ApianBase> ApianInstances; // keyed by groupId

        protected Dictionary<string, Action<string, string, long, GameNetClientMessage>> _MsgDispatchers;

        public ApianGameNetBase() : base()
        {
            ApianInstances = new Dictionary<string, ApianBase>();
            Peers = new Dictionary<string,ApianNetworkPeer>();

            _MsgDispatchers = new  Dictionary<string, Action<string, string, long, GameNetClientMessage>>()
            {
                [ApianMessage.ApianGroupAnnounce] = (f,t,s,m) => this._DispatchGroupAnnounceMessage(f,t,s,m),
                [ApianMessage.CliRequest] = (f,t,s,m) => this._DispatchApianMessage(f,t,s,m),
                [ApianMessage.CliObservation] = (f,t,s,m) => this._DispatchApianMessage(f,t,s,m),
                [ApianMessage.CliCommand] = (f,t,s,m) => this._DispatchApianMessage(f,t,s,m),
                [ApianMessage.ApianClockOffset] = (f,t,s,m) => this._DispatchApianMessage(f,t,s,m),
                [ApianMessage.GroupMessage] = (f,t,s,m) => this._DispatchApianMessage(f,t,s,m),
            };
        }

        //
        // *** IGameNet Overrides
        //

        // void Connect( string p2pConectionString );
        // void Disconnect();

        public override void AddClient(IGameNetClient _client)
        {
            base.AddClient(_client);
        }

         // void JoinNetwork(P2pNetChannelInfo netP2pChannel, string netLocalData)

        public override void LeaveNetwork()
        {
            foreach (ApianBase ap in ApianInstances.Values)
            {
                ap.LeaveGroup(); // post leaveGroup requests. Even tho we aren't waiting around for them.
            }
            // needs to clean up ApianInstances
            ApianInstances.Clear();
            Peers.Clear();
            base.LeaveNetwork();
        }

        //void SendClientMessage(string _toChan, string _clientMsgType, string _payload)

        //
        // ApianGameNet Client API
        //


        //  Request announcement of existing Apian groups
        public void RequestGroups()
        {
            logger.Verbose($"RequestApianGroups()");
            SendApianMessage( CurrentNetworkId(),  new GroupsRequestMsg());
        }

        private Dictionary<string, ApianGroupInfo> GroupRequestResults;

        public async Task<Dictionary<string, ApianGroupInfo>> RequestGroupsAsync(int timeoutMs)
        {
            // TODO: if results dict non-null then throw a "simultaneous requests not supported" exception
            GroupRequestResults = new Dictionary<string, ApianGroupInfo>();
            logger.Verbose($"RequestGroupsAsync()");
            SendApianMessage( CurrentNetworkId(),  new GroupsRequestMsg());
            await Task.Delay(timeoutMs);
            Dictionary<string, ApianGroupInfo> results = GroupRequestResults;
            GroupRequestResults = null;
            return results;
        }
        protected void _OnGroupAnnounceMsg(GroupAnnounceMsg gaMsg)
        {

        }

        // Joining a group (or creating and joining one)
        //
        // Eventual result is hopefully a call to:
        //    OnGroupMemberStatusChange(_Member) where member is the local peer and CurStatus is "Active"
        //

        public void JoinExistingGroup(ApianGroupInfo groupInfo, ApianBase apian, string localGroupData)
        {
            // need to set the groupMgr's "groupInfo" and open/join the p2pNet group channel
            apian.SetupExistingGroup(groupInfo); // initialize the groupMgr
            ApianInstances[groupInfo.GroupId] = apian; // add the ApianCorePair
            AddChannel(groupInfo.GroupChannelInfo, "Default local channel data"); // TODO: Should put something useful here
            apian.JoinGroup(localGroupData); // results in
        }
        public void CreateAndJoinGroup(ApianGroupInfo groupInfo, ApianBase apian, string localGroupData)
        {
            apian.SetupNewGroup(groupInfo); // create the group
            ApianInstances[groupInfo.GroupId] = apian; // add the ApianCorePair
            AddChannel(groupInfo.GroupChannelInfo,  "Default local channel data"); // TODO: see above
            apian.JoinGroup(localGroupData); //
        }

        public void LeaveGroup(string groupId)
        {
            logger.Info($"LeaveGroup( {groupId} )");
            if (! ApianInstances.ContainsKey(groupId))
                logger.Warn($"LeaveGroup() - No group: {groupId}");
            else
                ApianInstances[groupId].LeaveGroup();
        }
        public void SendApianMessage(string toChannel, ApianMessage appMsg)
        {
            logger.Verbose($"SendApianMessage() - type: {appMsg.MsgType}, To: {toChannel}");
            SendClientMessage( toChannel, appMsg.MsgType,  JsonConvert.SerializeObject(appMsg));
        }

        //
        //  *** IP2pNetClient Overrides
        //

        public override void OnPeerJoined(string channelId, string p2pId, string helloData)
        {
            // This means a peer joined the main Game channel.
            Peers[p2pId] = new ApianNetworkPeer(p2pId, helloData);
            base.OnPeerJoined(channelId, p2pId, helloData); // inform GameManager
        }

        public override void OnPeerLeft(string channelId, string p2pId)
        {

            if (channelId == CurrentNetworkId()) // P2pNet Peer left main game channel.
            {
                // Leave any groups
                foreach (ApianBase ap in ApianInstances.Values)
                    ap.OnGroupMemberLeft(channelId, p2pId);
                Peers.Remove(p2pId); // remove the peer
            } else {
                if (ApianInstances.ContainsKey(channelId))
                    ApianInstances[channelId].OnGroupMemberLeft(channelId, p2pId);
            }
            base.OnPeerLeft(channelId, p2pId); // calls client
        }

        public override void OnPeerMissing(string channelId, string p2pId)
        {
            if (ApianInstances.ContainsKey(channelId))
                ApianInstances[channelId].OnPeerMissing(channelId, p2pId);
            base.OnPeerMissing(channelId, p2pId);
        }

        public override void OnPeerReturned(string channelId, string p2pId)
        {
           if (ApianInstances.ContainsKey(channelId))
                ApianInstances[channelId].OnPeerReturned(channelId, p2pId);
            base.OnPeerReturned(channelId, p2pId);
        }

        public override void OnPeerSync(string channelId, string p2pId, long clockOffsetMs, long netLagMs)
        {
            // TODO: Should this go to the gamenet client?

            // P2pNet sends this for each channel that wants it
            if (ApianInstances.ContainsKey(channelId))
               ApianInstances[channelId].OnPeerClockSync(p2pId, clockOffsetMs, netLagMs);
        }

        //
        // *** Additional ApianGameNet stuff
        //

        public PeerClockSyncData GetP2pPeerClockSyncData(string p2pPeerId)
        {
            return p2p.GetPeerClockSyncData(p2pPeerId);
        }

        public void AddApianInstance( ApianBase instance, string groupId)
        {
            ApianInstances[groupId] = instance;
        }

        public void OnApianGroupMemberStatus( string groupId, string peerId, ApianGroupMember.Status newStatus, ApianGroupMember.Status prevStatus)
        {
            Client.OnGroupMemberStatus( groupId, peerId, newStatus, prevStatus);
        }

        protected override void _HandleClientMessage(string from, string to, long msSinceSent, GameNetClientMessage msg)
        {
            // This is called by GameNetBase.OnClientMessage()
            // We want to pass messages through a dispatch table.
            // Turns out (for now, anyway) we're best-off letting it throw rather than handling exceptions
            _MsgDispatchers[msg.clientMsgType](from, to, msSinceSent, msg);
        }

        protected void _DispatchApianMessage(string from, string to, long msSinceSent, GameNetClientMessage clientMessage)
        {
            ApianMessage apMsg = ApianMessageDeserializer.FromJSON(clientMessage.clientMsgType,clientMessage.payload);
            logger.Verbose($"_DispatchApianMessage() Type: {clientMessage.clientMsgType}, src: {(from==LocalP2pId()?"Local":from)}");

            if (ApianInstances.ContainsKey(apMsg.DestGroupId))
            {
                // Maybe the group manager defines/overrides the message
                ApianMessage gApMsg = ApianInstances[apMsg.DestGroupId].GroupMgr.DeserializeApianMessage(clientMessage.clientMsgType,clientMessage.payload)
                    ?? apMsg;
                ApianInstances[apMsg.DestGroupId].OnApianMessage( from,  to,  gApMsg,  msSinceSent);
            }
            else if (apMsg.DestGroupId == "") // Send to  all groups
            {
                foreach (ApianBase ap in ApianInstances.Values)
                    ap.OnApianMessage( from,  to,  apMsg,  msSinceSent);
            }
        }

        protected void _DispatchGroupAnnounceMessage(string from, string to, long msSinceSent, GameNetClientMessage clientMessage)
        {
            // Special message only goes to client
            GroupAnnounceMsg gaMsg = ApianMessageDeserializer.FromJSON(clientMessage.clientMsgType,clientMessage.payload) as GroupAnnounceMsg;
            logger.Verbose($"_DispatchGroupAnnounceMessage() Group: {gaMsg.GroupInfo.GroupId}, src: {(from==LocalP2pId()?"Local":from)}");

            if (GroupRequestResults != null)
                GroupRequestResults[gaMsg.GroupInfo.GroupId] = gaMsg.GroupInfo; // RequestGroupsAsync was called
            else
                Client.OnGroupAnnounce(gaMsg.GroupInfo); // TODO: should this happen even on an async request?
        }



    }

}