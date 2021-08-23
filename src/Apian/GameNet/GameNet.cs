using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json;
using P2pNet;
using UniLog;

namespace GameNet
{
    public class PeerJoinedNetworkData
    {
        public string PeerId {get; private set;}
        public string NetId {get; private set;}
        public string HelloData {get; private set;}
        public PeerJoinedNetworkData(string peerId, string netId, string helloData)
        {
            PeerId = peerId;
            NetId = netId;
            HelloData = helloData;
        }
    }

    public interface IGameNet
    {
        void Connect( string p2pConectionString );
        void AddClient(IGameNetClient _client);
        void Disconnect();
        void JoinNetwork(P2pNetChannelInfo netP2pChannel, string netLocalData);
        Task<PeerJoinedNetworkData> JoinNetworkAsync (P2pNetChannelInfo netP2pChannel, string netLocalData);
        void AddChannel(P2pNetChannelInfo subChannel, string channelLocalData);
        void RemoveChannel(string subchannelId);
        void LeaveNetwork();
        string LocalP2pId();
        string CurrentNetworkId();
        void Update(); /// <summary> needs to be called periodically (drives message pump + group handling)</summary>
    }

    public interface IGameNetClient
    {
        void OnPeerJoinedNetwork(PeerJoinedNetworkData peerData);
        void OnPeerLeftNetwork(string p2pId, string netId);
        void OnPeerMissing(string p2pId, string networkId);
        void OnPeerReturned(string p2pId, string networkId);
        void OnPeerSync(string channelId, string p2pId, long clockOffsetMs, long netLagMs);
    }

    // used internally
    public class GameNetClientMessage
    {
        public string clientMsgType;
        public string payload; // string or json-encoded application object
    }

    public abstract class GameNetBase : IGameNet, IP2pNetClient
    {
        //
        // This is a single game GameNet base implementation
        //
        protected IGameNetClient client;
        protected IP2pNet p2p;
        public UniLogger logger;

        // Some client callbacks can happen as a direct result of a call, but we would like for
        // them to be dispatched during poll(), rather than during th ecall itself. Put them
        // in this queue and it'll happen that way.
        // OnGameCreated() is an example of one that might take a while, or might
        // happen immediately.
        protected Queue<Action> callbacksForNextPoll;
        protected Queue<Action> loopedBackMessageHandlers; // messages that come from this node (loopbacks) get handled at the end of the loop

        protected GameNetBase()
        {
            callbacksForNextPoll = new Queue<Action>();
            loopedBackMessageHandlers  = new Queue<Action>();
            logger = UniLogger.GetLogger("GameNet");
        }

        public virtual void AddClient(IGameNetClient _client)
        {
            client = _client;
        }

        // Override this to account for P2pNet implementations you support
        protected virtual IP2pNet P2pNetFactory(string p2pConnectionString)
        {
            // P2pConnectionString is <p2p implmentation name>::<imp-dependent connection string>
            IP2pNet ip2p;
            string[] parts = p2pConnectionString.Split(new string[]{"::"},StringSplitOptions.None); // Yikes! This is fugly.

            switch(parts[0])
            {
                case "p2ploopback":
                    ip2p = new P2pLoopback(this, null);
                    break;
                default:
                    throw( new Exception($"Invalid connection type: {parts[0]}"));
            }

            return ip2p;
        }

        //
        // IGameNet
        //
        public virtual void Connect( string p2pConnectionString )
        {
            p2p = P2pNetFactory(p2pConnectionString);
        }

        public virtual void Disconnect()
        {
            if (p2p?.GetId() != null)
                p2p.Leave();
            p2p = null;
        }

        // TODO: need "Destroy() or Reset() or (Init)" to null out P2pNet instance? Don;t want to destroy instance immediately on Leave()

        // public abstract void CreateNetwork<T>(T t); // really can only be defined in the game-specific implmentation

        // protected void _SyncTrivialNewNetwork()
        // {
        //     // The most basic thing you can in a CreateGame() implmentation.
        //     // It actually does nothing at all except to make up a random name and then call back saying a game was created.
        //     // This works because at its *absolute simplest* a "game" is just an agree-to p2p channel, and "joining" it
        //     // just means subscribing to it.
        //     string newId = "APIANNET" + System.Guid.NewGuid().ToString();
        //     callbacksForNextPoll.Enqueue( () => client.OnNetworkCreated(newId));
        // }


        public virtual void JoinNetwork(P2pNetChannelInfo netP2pChannel, string netLocalData)
        {
            if (netLocalData != null)
                p2p.Join(netP2pChannel, netLocalData);    // Results in "OnPeerJoined(localPeer)" call
            else
               logger.Error($"JoinNetwork() - no local network data.");

            //callbacksForNextPoll.Enqueue( () => this.OnPeerJoined( netP2pChannel.id, LocalP2pId(), netLocalData));
        }
        private TaskCompletionSource<PeerJoinedNetworkData> JoinNetworkCompletion; // can only be one
        public async Task<PeerJoinedNetworkData> JoinNetworkAsync(P2pNetChannelInfo netP2pChannel, string netLocalData)
        {
            if (JoinNetworkCompletion != null)
                throw new Exception("Already wainting for JoinNetwokAsync()");

            JoinNetworkCompletion = new TaskCompletionSource<PeerJoinedNetworkData>();
            JoinNetwork(netP2pChannel, netLocalData);
            return await JoinNetworkCompletion.Task.ContinueWith( t => {JoinNetworkCompletion=null; return t.Result;},  TaskScheduler.Default).ConfigureAwait(false);
        }

        public virtual void OnPeerJoined(string channel, string p2pId, string helloData)
        {
            // See P2pHelloData() comment regarding actual data struct
            if (channel == CurrentNetworkId())
            {
                PeerJoinedNetworkData peerData = new PeerJoinedNetworkData(p2pId, CurrentNetworkId(), helloData);
                if (p2pId == LocalP2pId() && JoinNetworkCompletion != null)
                    JoinNetworkCompletion.TrySetResult(peerData);
                client.OnPeerJoinedNetwork(peerData);
            }

            // Note: ApianGameNet overrides this (and calls it)
        }

        public virtual void LeaveNetwork()
        {
            callbacksForNextPoll.Enqueue( () => client.OnPeerLeftNetwork(LocalP2pId(), CurrentNetworkId())); // well get this next polling update
            p2p.Leave(); // sends goodbye
            // Note that this is not Disconnect(). It probably almost always is, but just as Connect() and JoinNetwork()
            // are separate, so are LeaveNetwork and Disconnect. I dunno if that good, bad, or irrelevant.
        }

        public virtual void AddChannel(P2pNetChannelInfo subChannel, string channelLocalData)
        {
            p2p.AddSubchannel(subChannel, channelLocalData);
        }
        public virtual void RemoveChannel(string subChannelId)
        {
            p2p.RemoveSubchannel(subChannelId);
        }

        public virtual void Update()
        {
            // Dispatch any locally-enqueued actions
            while(callbacksForNextPoll.Count != 0)
            {
                Action action = callbacksForNextPoll.Dequeue();
                action();
            }

            // and any looped-back local messages from the previous Loop()
            while(loopedBackMessageHandlers.Count != 0)
            {
                Action action = loopedBackMessageHandlers.Dequeue();
                action();
            }

            p2p?.Update();

        }

        public string LocalP2pId() => p2p?.GetId();
        public string CurrentNetworkId() => p2p?.GetMainChannel()?.Id;

        //
        // IP2pNetClient
        //


        public virtual void OnPeerSync(string channelId, string p2pId, long clockOffsetMs, long netLagMs)
        {
            // Note: ApianGameNet overrides this and DOESN'T call it - Apian deals with any sync stuff for apian apps
            client.OnPeerSync(channelId, p2pId, clockOffsetMs, netLagMs);
        }

        public virtual void OnPeerLeft(string channelId, string p2pId)
        {

            // Note: ApianGameNet overrides this (and calls it)
            if (channelId == CurrentNetworkId())
                client.OnPeerLeftNetwork(p2pId, CurrentNetworkId());
        }

        public virtual void OnPeerMissing(string channelId, string p2pId)
        {
            // Note: ApianGameNet overrides this (and calls it)
            if (channelId == CurrentNetworkId())
                client.OnPeerMissing(p2pId, CurrentNetworkId());

        }

        public virtual void OnPeerReturned(string channelId, string p2pId)
        {
            // Note: ApianGameNet overrides this (and calls it)
            if (channelId == CurrentNetworkId())
                client.OnPeerReturned(p2pId, CurrentNetworkId());
        }

        public void OnClientMsg(string from, string to, long msSinceSent, string payload)
        {
            GameNetClientMessage gameNetClientMessage = JsonConvert.DeserializeObject<GameNetClientMessage>(payload);

            if (from == LocalP2pId())
                loopedBackMessageHandlers.Enqueue( () => HandleClientMessage(from, to, msSinceSent, gameNetClientMessage));
            else
                HandleClientMessage(from, to, msSinceSent, gameNetClientMessage);

        }

        // Derived classes Must implment this, as well as client-specific messages
        // that call _SendClientMessage()

        protected abstract void HandleClientMessage(string from, string dest, long msSinceSent, GameNetClientMessage clientMessage);


        public void SendClientMessage(string _toChan, string _clientMsgType, string _payload)
        {
            string gameNetClientMsgJSON = JsonConvert.SerializeObject(new GameNetClientMessage(){clientMsgType=_clientMsgType, payload=_payload});
            p2p.Send(_toChan, gameNetClientMsgJSON);
        }

    }
}
