//#define SINGLE_THREADED
using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using GameNet;
using P2pNet;
using ApianCrypto;

#if !SINGLE_THREADED
using System.Threading.Tasks;
#endif

namespace Apian
{
    public class PeerJoinedGroupData
    {
        public string PeerAddr {get; private set;}
        public bool IsValidator;
        public ApianGroupInfo GroupInfo {get; private set;}
        public bool Success {get; private set;}
        public string Message {get; private set;}
        public PeerJoinedGroupData (string peerAddr, ApianGroupInfo groupInfo, bool isValidator, bool success, string message = null)
        {
            PeerAddr = peerAddr;
            GroupInfo = groupInfo;
            IsValidator = isValidator;
            Success = success;
            Message = message;
        }
    }

    public interface IApianGameNetClient : IGameNetClient
    {
        // Callback results for single-threaded crypto chain calls
        void OnChainId(int chainId, Exception ex);
        void OnChainBlockNumber(int blockNumber, Exception ex);
        void OnChainAcctBalance(string addr, int balance, Exception ex);
        void OnSessionRegistered(string sessId, string txHash, Exception ex);
        void OnEpochReported(string sessId, long epochNum, string txHash, Exception ex);
    }


    public interface IApianGameNet : IGameNet
    {
        IApianApplication Client {get; }

        void AddApianInstance( ApianBase instance, string groupId);
        void RequestGroups();
        void OnApianGroupMemberStatus( string groupId,  ApianGroupMember member, ApianGroupMember.Status prevStatus);
        void SendApianMessage(string toChannel, ApianMessage appMsg);
        PeerNetworkStats GetPeerNetStats(string P2pPeerAddr);
        ApianGroupStatus GetGroupStatus(string groupId);

        // Crypto/blockchain
        // TODO: consider creaking up this interface
        (string,string) NewCryptoAccountKeystore(string password);
        string SetupNewCryptoAccount(string password = null);
        string RestoreCryptoAccount(PersistentAccount pAcct, string password);
        string CryptoAccountAddress();
        string HashString(string msg);
        string EncodeUTF8AndSign(string addr, string msg);
        string EncodeUTF8AndEcRecover(string msg, string sig);
        // Called by Apian
         void OnPeerJoinedGroup(string peerAddr, string groupId, bool isValidator,  bool joinSuccess, string failureReason = null);
         //void OnPeerLeftGroup(string peerAddr, string groupId);  // TODO: DO we need this? Currently the MemberStatus switch to "Gone" is sent...
         void OnNewGroupLeader(string groupId, string newLeaderAddr, ApianGroupMember newLeader);

        // Crypto stuff API
        //void CreateCryptoInstance(); // can be problematic in Unity (needs to happen on main thread)
        void ConnectToBlockchain(string chainInfoJson);
        void DisconnectFromBlockchain();

        // TODO: cleanup the below funcs &&&&&&&&&&&&
        //void GetChainId();
        //void GetChainBlockNumber();
        //void GetChainAccountBalance(string acctAddr);
        //void RegisterSession(string sessionId, AnchorSessionInfo sessInfo);
        //void ReportEpoch(string sessionId, ApianEpochReport rpt);

#if !SINGLE_THREADED
        Task<Dictionary<string, GroupAnnounceResult>> RequestGroupsAsync(int timeoutMs);
        //Task ConnectToBlockchainAsync(string chainInfoJson);
        Task<int> GetChainIdAsync();
        Task<int> GetChainBlockNumberAsync();
        Task<int> GetChainAccountBalanceAsync(string acctAddr);

        Task<string> RegisterSessionAsync(string sessionId, AnchorSessionInfo sessInfo);
        Task<string>  ReportEpochAsync(string sessionId, ApianEpochReport rpt);

#endif

    }

    public class ApianNetworkPeer
    {
        // This is a GameNet/P2pNet peer. There is only one of these per node, no matter
        // how many ApianInstances/Groups there are.

        public string Addr { get; private set; }
        public string P2NetpHelloData { get; private set; } // almost always JSON
        public ApianNetworkPeer(string addr, string helloData)
        {
            Addr = addr;
            P2NetpHelloData = helloData;
        }
    }

    public abstract class ApianGameNetBase : GameNetBase, IApianGameNet, IApianCryptoClient
    {
        // This is the actual GameNet instance
        public IApianApplication Client {get => client as IApianApplication;}
        protected Dictionary<string,ApianNetworkPeer> Peers; // keyed by addr  // TODO: Isn;t actually used for anything
        public Dictionary<string, ApianBase> ApianInstances; // keyed by groupId

        protected Dictionary<string, Action<string, string, long, GameNetClientMessage>> _MsgDispatchers;

#if !SINGLE_THREADED
        private Dictionary<string, TaskCompletionSource<PeerJoinedGroupData>> JoinGroupAsyncCompletionSources;
#endif

        public IApianCrypto apianCrypto;

        protected ApianGameNetBase() : base()
        {
            ApianInstances = new Dictionary<string, ApianBase>();
            Peers = new Dictionary<string,ApianNetworkPeer>();

            apianCrypto = EthForApian.Create();

            _MsgDispatchers = new  Dictionary<string, Action<string, string, long, GameNetClientMessage>>()
            {
                [ApianMessage.ApianGroupAnnounce] = (f,t,s,m) => this.DispatchGroupAnnounceMessage(f,t,s,m),
                [ApianMessage.CliRequest] = (f,t,s,m) => this.DispatchApianMessage(f,t,s,m),
                [ApianMessage.CliObservation] = (f,t,s,m) => this.DispatchApianMessage(f,t,s,m),
                [ApianMessage.CliCommand] = (f,t,s,m) => this.DispatchApianMessage(f,t,s,m),
                [ApianMessage.ApianClockOffset] = (f,t,s,m) => this.DispatchApianMessage(f,t,s,m),
                [ApianMessage.GroupMessage] = (f,t,s,m) => this.DispatchApianMessage(f,t,s,m),
            };

#if !SINGLE_THREADED
            JoinGroupAsyncCompletionSources = new Dictionary<string, TaskCompletionSource<PeerJoinedGroupData>>();
#endif
        }

        //
        // *** IGameNet Overrides
        //

        public override void Update()
        {
            base.Update();

            foreach (ApianBase ap in ApianInstances.Values)
            {
                ap.Update();
            }

        }

        // void SetupConnection( string p2pConectionString );
        // void TearDownConnection();

        public override void AddClient(IGameNetClient _client)
        {
            base.AddClient(_client);
        }

        protected void InitApianJoinData()
        {
            // State that should be reset before and cleaned up after, network join/leave
            base.InitNetJoinState();
            ApianInstances.Clear();
            Peers.Clear();
#if !SINGLE_THREADED
            JoinGroupAsyncCompletionSources = new Dictionary<string, TaskCompletionSource<PeerJoinedGroupData>>();
#endif
        }

        public override void JoinNetwork(P2pNetChannelInfo netP2pChannel, string netLocalData)
        {
            InitApianJoinData();
             base.JoinNetwork(netP2pChannel, netLocalData);
        }

        public override void LeaveNetwork()
        {

            InitApianJoinData(); // needs to clean up ApianInstances

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

        //
        // Joining a group (or creating and joining one)
        //
        // Eventual result is hopefully a call to caller's:
        //    OnPeerJoinedGroup(group, localPeer)  with result info including failure results
        // or
        //    OnApianGroupMemberStatus(group, localPeer, StatusActive)
        //
        // The JoinExistingGroupAsync() and CreateAndJoinGroupAsync() both wait for the latter,
        // but non-async application code can choose how to handle it.
        //
        // TODO: Current beam code is somewhat inconsistent in that the single-threaded version waits for BeamApplication.OnPeerJoinedGroup()
        // Maybe ought to change this?

        public void JoinExistingGroup(ApianGroupInfo groupInfo, ApianBase apian, string localGroupData, bool joinAsValidator)
        {
            // see comment above on how this func is resolved.

            // Sets the groupMgr's "groupInfo" and open/join the p2pNet group channel
            apian.SetupExistingGroup(groupInfo); // initialize the groupMgr
            ApianInstances[groupInfo.GroupId] = apian; // add the ApianCorePair
            AddChannel(groupInfo.GroupChannelInfo, "Default local channel data"); // TODO: Should put something useful here

            if (groupInfo.AnchorAddr != null)
                apianCrypto.AddSessionAnchorService( groupInfo.SessionId,  groupInfo.AnchorAddr);

            apian.JoinGroup(localGroupData, joinAsValidator); // results in
        }
        public void CreateAndJoinGroup(ApianGroupInfo groupInfo, ApianBase apian, string localGroupData, bool joinAsValidator)
        {
            // see comment above on how this func is resolved.
            DoCreateAndJoinGroup( groupInfo,  apian,  localGroupData, joinAsValidator);

            if (groupInfo.AnchorAddr != null)
                apian.RegisterNewSession();

        }

        public void DoCreateAndJoinGroup(ApianGroupInfo groupInfo, ApianBase apian, string localGroupData, bool joinAsValidator)
        {

            apian.SetupNewGroup(groupInfo); // create the group
            ApianInstances[groupInfo.GroupId] = apian; // add the ApianCorePair
            AddChannel(groupInfo.GroupChannelInfo,  "Default local channel data"); // TODO: see above

            if (groupInfo.AnchorAddr != null)
                apianCrypto.AddSessionAnchorService( groupInfo.SessionId,  groupInfo.AnchorAddr);

            apian.JoinGroup(localGroupData, joinAsValidator) ; //

            GroupAnnounceMsg amsg = new GroupAnnounceMsg(groupInfo, apian.CurrentGroupStatus());
            SendApianMessage( CurrentNetworkId() , amsg); // send announcement to  everyone
        }

        // Async versions of the above group joining methods which return success/failure results
#if !SINGLE_THREADED
        private Dictionary<string, GroupAnnounceResult> GroupRequestResults;
        public async Task<Dictionary<string, GroupAnnounceResult>> RequestGroupsAsync(int timeoutMs)
        {
            // TODO: if results dict non-null then throw a "simultaneous requests not supported" exception
            GroupRequestResults = new Dictionary<string, GroupAnnounceResult>();
            logger.Verbose($"RequestGroupsAsync()");
            SendApianMessage( CurrentNetworkId(),  new GroupsRequestMsg());
            await Task.Delay(timeoutMs).ConfigureAwait(false);
            Dictionary<string, GroupAnnounceResult> results = GroupRequestResults;
            GroupRequestResults = null;
            return results;
        }

        public async Task<PeerJoinedGroupData> JoinExistingGroupAsync(ApianGroupInfo groupInfo, ApianBase apian, string localGroupData, int timeoutMs, bool joinAsValidator)
        {
             if (JoinGroupAsyncCompletionSources.ContainsKey(groupInfo.GroupId))
                throw new Exception($"Already waiting for JoinGroupAsync() for group {groupInfo.GroupFriendlyId}");

            JoinGroupAsyncCompletionSources[groupInfo.GroupId] = new TaskCompletionSource<PeerJoinedGroupData>();

            JoinExistingGroup( groupInfo,  apian,  localGroupData, joinAsValidator);

            _ = Task.Delay(timeoutMs).ContinueWith(t => TimeoutJoinGroup(groupInfo) );

            return await  JoinGroupAsyncCompletionSources[groupInfo.GroupId].Task.ContinueWith(
                t => {  JoinGroupAsyncCompletionSources.Remove(groupInfo.GroupId); return t.Result;}, TaskScheduler.Default
                ).ConfigureAwait(false);
        }

        public async Task<PeerJoinedGroupData> CreateAndJoinGroupAsync(ApianGroupInfo groupInfo, ApianBase apian, string localGroupData, int timeoutMs, bool joinAsValidator)
        {
             if (JoinGroupAsyncCompletionSources.ContainsKey(groupInfo.GroupId))
                throw new Exception($"Already waiting for JoinGroupAsync() for group {groupInfo.GroupFriendlyId}");

            JoinGroupAsyncCompletionSources[groupInfo.GroupId] = new TaskCompletionSource<PeerJoinedGroupData>();
            DoCreateAndJoinGroup( groupInfo,  apian,  localGroupData, joinAsValidator);

            try {
                if (groupInfo.AnchorAddr != null)
                    await apian.RegisterNewSessionAsync();
            } catch (Exception e) {
                //logger.Error(e.Message + " " + e.StackTrace);
                PeerJoinedGroupData joinData = new PeerJoinedGroupData(LocalPeerAddr(), groupInfo, false, false, e.Message);
                JoinGroupAsyncCompletionSources[groupInfo.GroupId].TrySetResult(joinData);
                return joinData;
            }

            _ = Task.Delay(timeoutMs).ContinueWith(t => TimeoutJoinGroup(groupInfo) );
            return await  JoinGroupAsyncCompletionSources[groupInfo.GroupId].Task.ContinueWith(
                t => {  JoinGroupAsyncCompletionSources.Remove(groupInfo.GroupId); return t.Result;}, TaskScheduler.Default
                ).ConfigureAwait(false);
        }

        protected void TimeoutJoinGroup(ApianGroupInfo groupInfo)
        {
            string groupId = groupInfo.GroupId;
            if (JoinGroupAsyncCompletionSources.ContainsKey(groupId))
            {
                PeerJoinedGroupData joinData = new PeerJoinedGroupData(LocalPeerAddr(), groupInfo, false, false, "Timeout");
                JoinGroupAsyncCompletionSources[groupId].TrySetResult(joinData);
            }
        }


#endif

        public void LeaveGroup(string groupId)
        {
            logger.Info($"LeaveGroup( {groupId} )");
            if (! ApianInstances.ContainsKey(groupId))
                logger.Warn($"LeaveGroup() - No group: {groupId}");
            else {
                RemoveChannel(groupId); // You don;t need to ask or anything. Just leave the net channel and clean up
                ApianInstances.Remove(groupId);
            }
        }
        public void SendApianMessage(string toChannel, ApianMessage appMsg)
        {
            logger.Verbose($"SendApianMessage() - type: {appMsg.MsgType}, To: {toChannel}");
            SendClientMessage( toChannel, appMsg.MsgType,  JsonConvert.SerializeObject(appMsg));
        }

        //
        //  *** IP2pNetClient Overrides
        //

        public override void OnPeerJoined(string channelId, string peerAddr, string helloData)
        {
            if (channelId == CurrentNetworkId())
            {
                // This means a peer joined the main Game channel.
                Peers[peerAddr] = new ApianNetworkPeer(peerAddr, helloData);
            }
            base.OnPeerJoined(channelId, peerAddr, helloData); //
        }

        public override void OnPeerLeft(string channelId, string peerAddr)
        {
            if (channelId == CurrentNetworkId()) // P2pNet Peer left main game channel.
            {
                logger.Info($"OnPeerLeft() - Is main network channel. Informing all groups.");
                // Leave any groups
                foreach (ApianBase ap in ApianInstances.Values)
                    ap.OnPeerLeftGroupChannel(channelId, peerAddr);
                Peers.Remove(peerAddr); // remove the peer
            } else {
                if (ApianInstances.ContainsKey(channelId))
                    ApianInstances[channelId].OnPeerLeftGroupChannel(channelId, peerAddr);
            }
            base.OnPeerLeft(channelId, peerAddr); // calls client
        }

        public override void OnPeerMissing(string channelId, string peerAddr)
        {
            if (ApianInstances.ContainsKey(channelId))
                ApianInstances[channelId].OnPeerMissing(channelId, peerAddr);
            base.OnPeerMissing(channelId, peerAddr);
        }

        public override void OnPeerReturned(string channelId, string peerAddr)
        {
           if (ApianInstances.ContainsKey(channelId))
                ApianInstances[channelId].OnPeerReturned(channelId, peerAddr);
            base.OnPeerReturned(channelId, peerAddr);
        }

        public override void OnPeerSync(string channelId, string peerAddr, PeerClockSyncInfo syncInfo)
        {
            // P2pNet sends this for each channel that wants it
            if (ApianInstances.ContainsKey(channelId))
               ApianInstances[channelId].OnPeerClockSync(peerAddr, syncInfo.sysClockOffsetMs, syncInfo.syncCount);
        }

        //
        // *** Additional ApianGameNet stuff
        //

        public ApianGroupStatus GetGroupStatus(string groupId)
        {
            return ApianInstances.ContainsKey(groupId)? ApianInstances[groupId].CurrentGroupStatus() : null;
        }

        public PeerNetworkStats GetPeerNetStats(string p2pPeerAddr)
        {
            return p2p.GetPeerNetworkStats(p2pPeerAddr);
        }

        public void AddApianInstance( ApianBase instance, string groupId)
        {
            ApianInstances[groupId] = instance;
        }

        public virtual void OnPeerJoinedGroup( string peerAddr, string groupId, bool isValidator, bool joinSuccess, string message = null)
        {

            if (ApianInstances.ContainsKey(groupId))
            {
                ApianGroupInfo groupInfo = ApianInstances[groupId].GroupInfo;
                PeerJoinedGroupData joinData = new PeerJoinedGroupData(peerAddr, groupInfo, isValidator, joinSuccess, message);

                // local async join requests aren't considered complete until the peer has Active status

                Client.OnPeerJoinedGroup(joinData);
            }
        }

        public void OnNewGroupLeader(string groupId, string newLeaderAddr, ApianGroupMember newLeader)
        {
            // newLeaderDat might be null (when group is first getting created)
            Client.OnGroupLeaderChange(groupId, newLeaderAddr, newLeader);
        }

        public void OnApianGroupMemberStatus( string groupId,  ApianGroupMember member, ApianGroupMember.Status prevStatus)
        {

#if !SINGLE_THREADED
            //  For Async Join request, local application isn't told that it has "joined" a group until it is Active
            if (member.PeerAddr == LocalPeerAddr() && member.CurStatus == ApianGroupMember.Status.Active
            && JoinGroupAsyncCompletionSources.ContainsKey(groupId))
            {
                ApianGroupInfo groupInfo = ApianInstances[groupId].GroupInfo;
                PeerJoinedGroupData joinData = new PeerJoinedGroupData(member.PeerAddr, groupInfo, member.IsValidator, true);
                JoinGroupAsyncCompletionSources[groupId].TrySetResult(joinData);
            }
#endif

            Client.OnGroupMemberStatus( groupId, member.PeerAddr, member.CurStatus, prevStatus );

        }

        protected override void HandleClientMessage(string from, string to, long msSinceSent, GameNetClientMessage msg)
        {
            // This is called by GameNetBase.OnClientMessage()
            // We want to pass messages through a dispatch table.
            // Turns out (for now, anyway) we're best-off letting it throw rather than handling exceptions
            _MsgDispatchers[msg.clientMsgType](from, to, msSinceSent, msg);
        }

        // protected void OldDispatchApianMessage(string from, string to, long msSinceSent, GameNetClientMessage clientMessage)
        // {
        //     // Decode generic (non-group) ApianMessage. If it's an ApianGroupMessage we'll let the group instance decode it in more detail
        //     ApianMessage apMsg = ApianMessageDeserializer.FromJSON(clientMessage.clientMsgType,clientMessage.payload);
        //     logger.Verbose($"_DispatchApianMessage() Type: {clientMessage.clientMsgType}, src: {(from==LocalPeerAddr()?"Local":from)}");

        //     if (ApianInstances.ContainsKey(apMsg.DestGroupId))
        //     {
        //         // Maybe the group instance defines/overrides the message
        //         ApianMessage gApMsg = ApianInstances[apMsg.DestGroupId].DeserializeApianMessage(apMsg, clientMessage.payload)
        //             ?? apMsg;
        //         ApianInstances[apMsg.DestGroupId].OnApianMessage( from,  to,  gApMsg,  msSinceSent);
        //     }
        //     else if (string.IsNullOrEmpty(apMsg.DestGroupId)) // Send to  all groups
        //     {
        //         foreach (ApianBase ap in ApianInstances.Values)
        //             ap.OnApianMessage( from,  to,  apMsg,  msSinceSent);
        //     }
        // }

        protected void DispatchApianMessage(string from, string to, long msSinceSent, GameNetClientMessage clientMessage)
        {

            // Who's it for?
            string destGroupId = ApianMessageDeserializer.DecodeDestGroup(clientMessage.payload);

            logger.Debug($"DispatchApianMessage() Type: {clientMessage.clientMsgType}, src: {(from==LocalPeerAddr()?"Local":from)}, dest: {destGroupId}");

            if (string.IsNullOrEmpty(destGroupId))
            {
                // It has no destination group, so we can use the static message deserializer. (No dest group means it's
                // "for all groups" and so can't be a group-specific message type)
                ApianMessage apMsg = ApianMessageDeserializer.FromJSON(clientMessage.clientMsgType,clientMessage.payload);

                foreach (ApianBase ap in ApianInstances.Values)
                    ap.OnApianMessage( from,  to,  apMsg,  msSinceSent);
            }
            else if (ApianInstances.ContainsKey(destGroupId))
            {
                // For a specific group. It's possible that the group instance defines (or overrides) the message
                ApianMessage gApMsg = ApianInstances[destGroupId].DeserializeCustomApianMessage(clientMessage.clientMsgType, clientMessage.payload)
                    ?? ApianMessageDeserializer.FromJSON(clientMessage.clientMsgType,clientMessage.payload); // ...else pass it to the default DeSer
                ApianInstances[destGroupId].OnApianMessage( from,  to,  gApMsg,  msSinceSent);
            } else {
                logger.Warn($"DispatchApianMessage() Received ApianMsg type: {clientMessage.clientMsgType} to unknown Group: {destGroupId}");
            }

        }

        protected void DispatchGroupAnnounceMessage(string from, string to, long msSinceSent, GameNetClientMessage clientMessage)
        {
            // Special message only goes to client
            GroupAnnounceMsg gaMsg = ApianMessageDeserializer.FromJSON(clientMessage.clientMsgType,clientMessage.payload) as GroupAnnounceMsg;
            ApianGroupInfo groupInfo = gaMsg.DecodeGroupInfo();
            ApianGroupStatus groupStatus = gaMsg.DecodeGroupStatus();
            logger.Verbose($"_DispatchGroupAnnounceMessage() Group: {groupInfo.GroupFriendlyId}, src: {(from==LocalPeerAddr()?"Local":from)}");

            GroupAnnounceResult result =  new GroupAnnounceResult(groupInfo, groupStatus);

            Client.OnGroupAnnounce(result); // Q: should this happen even on an async request? YES

#if !SINGLE_THREADED
            if (GroupRequestResults != null)
                GroupRequestResults[groupInfo.GroupId] = result;// RequestGroupsAsync was called
#endif
        }

        //
        // Crypto/blockchain stuff
        //

        // public void CreateCryptoInstance()
        // {
        //     apianCrypto = EthForApian.Create();
        // }

        public string CryptoAccountAddress() => apianCrypto?.CurrentAccountAddress;

        public (string,string) NewCryptoAccountKeystore(string password)
        {
            string addr, json;
            ( addr,  json) = apianCrypto.KeystoreForNewAccount(password);
            logger.Info($"NewCryptoAccountKeystore() - Created new Eth keystore: {addr}");
            return (addr, json);
        }

        public string SetupNewCryptoAccount(string password = null)
        {
            // returns encrypted json keystore if password is not null.
            // If password is null it still sets up a new account, but doesn;t return the persistence data.
            // It creates a temporary account, in other words.

                string addr =  apianCrypto.SetNewAccount();
            logger.Info($"SetupNewCryptoAccount() - Created new {(string.IsNullOrEmpty(password)?" temp ":"")} Eth acct: {addr}");

            return string.IsNullOrEmpty(password) ? null : apianCrypto.KeystoreForCurrentAccount(password);
        }

        public string RestoreCryptoAccount(PersistentAccount pAcct, string password)
        {
            string addr = null;
            if (pAcct.Type == PersistentAccount.AvailTypes.V3Keystore)
            {
                addr = apianCrypto.SetAccountFromKeystore(password, pAcct.Data);
            } else if (pAcct.Type == PersistentAccount.AvailTypes.ClearPrivKey) {
                addr = apianCrypto.SetAccountFromKey(pAcct.Data);
            }
            logger.Info( $"_SetupCrypto() - Restored Eth acct: {addr} from settings");
            return addr;
        }

        public string HashString(string str) => apianCrypto.HashString(str);

        public string EncodeUTF8AndSign(string addr, string msg) => apianCrypto.EncodeUTF8AndSign(addr, msg);

        public string EncodeUTF8AndEcRecover(string msg, string sig) => apianCrypto.EncodeUTF8AndEcRecover(msg, sig);

#if !SINGLE_THREADED
        // public async Task ConnectToBlockchainAsync(string chainInfoJson)
        // {
        //     BlockchainInfo bcInfo = JsonConvert.DeserializeObject<BlockchainInfo>(chainInfoJson);
        //     apianCrypto.Connect(bcInfo.RpcUrl);

        //     int chainId = await apianCrypto.GetChainIdAsync();
        //     logger.Info( $"ConnectToBlockchainAsync() - Connected to chain ID: {chainId}");

        //     if (chainId!= bcInfo.ChainId)
        //         throw new Exception($"ConnectToBlockchainAsync() - Chain ID mismatch. Expected: {bcInfo.ChainId}, got: {chainId}");

        // }

        public async Task<int> GetChainIdAsync()
        {
            return await apianCrypto.GetChainIdAsync();
        }

        public async Task<int> GetChainBlockNumberAsync()
        {
            return await apianCrypto.GetBlockNumberAsync();
        }

        public async Task<int> GetChainAccountBalanceAsync(string acctAddr)
        {
            return await apianCrypto.GetBalanceAsync(acctAddr);
        }
#endif


        public void ConnectToBlockchain(string chainInfoJson)
        {
            BlockchainInfo bcInfo = JsonConvert.DeserializeObject<BlockchainInfo>(chainInfoJson);
            apianCrypto.Connect(bcInfo.RpcUrl, bcInfo.ChainId, this);
        }

        public void DisconnectFromBlockchain()
        {
            apianCrypto.Disconnect();
        }

        // public void GetChainId() => apianCrypto.GetChainId();
        // public void GetChainBlockNumber() => apianCrypto.GetBlockNumber();
        // public void GetChainAccountBalance(string acctAddr) => apianCrypto.GetBalance(acctAddr);

        // public void  RegisterSession(string sessionId, AnchorSessionInfo sessInfo) =>  apianCrypto.RegisterSession(sessionId, sessInfo);

        // public void ReportEpoch(string sessionId, ApianEpochReport rpt) => apianCrypto.ReportEpoch(sessionId, rpt);

#if !SINGLE_THREADED

        public async Task<string> RegisterSessionAsync(string sessionId, AnchorSessionInfo sessInfo)
            => await apianCrypto.RegisterSessionAsync(sessionId, sessInfo);


        public async Task<string> ReportEpochAsync(string sessionId, ApianEpochReport rpt)
            => await apianCrypto.ReportEpochAsync(sessionId, rpt);

#endif


        // IApianCryptoClient API
        public void OnChainId(int chainId, Exception ex)
        {
            logger.Info($"OnChainId() - Chain ID: {chainId}");
            (client as IApianGameNetClient).OnChainId(chainId, ex);
        }
        public void OnBlockNumber(int blockNumber, Exception ex)
        {
            logger.Info($"OnBlockNumber() - Block Number: {blockNumber}");
            (client as IApianGameNetClient).OnChainBlockNumber(blockNumber, ex);
        }
        public void OnBalance(string addr, int balance, Exception ex)
        {
            logger.Info($"OnBalance() - Account: {addr}, Balance: {balance}");
            (client as IApianGameNetClient).OnChainAcctBalance(addr, balance, ex);
        }

        public void OnSessionRegistered(string sessionId, string txHash, Exception ex)
        {
            logger.Info($"OnSessionRegistered() - session: {sessionId}, txHash: {txHash} Err: {(ex!=null?ex.Message:"None")}");
            (client as IApianGameNetClient).OnSessionRegistered(sessionId, txHash, ex);
        }

        public void OnEpochReported(string sessionId, long epochNum, string txHash, Exception ex)
        {
            logger.Info($"OnEpochReported() - session: {sessionId}, txHash: {txHash}");
            (client as IApianGameNetClient).OnEpochReported(sessionId, epochNum, txHash, ex);
        }
    }
}