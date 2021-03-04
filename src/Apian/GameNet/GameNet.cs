using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using P2pNet;
using UniLog;

namespace GameNet
{
    public interface IGameNet
    {
        void Connect( string p2pConectionString );
        void AddClient(IGameNetClient _client);
        void Disconnect();
        void CreateNetwork<T>(T createNetData);
        void JoinNetwork(P2pNetChannelInfo netP2pChannel, string netLocalData);
        void AddChannel(P2pNetChannelInfo subChannel, string channelLocalData);
        void RemoveChannel(string subchannelId);
        void LeaveNetwork();
        string LocalP2pId();
        string CurrentNetworkId();
        void Loop(); /// <summary> needs to be called periodically (drives message pump + group handling)</summary>
    }

    public interface IGameNetClient
    {
        void OnNetworkCreated(string netP2pChannel);
        void OnPeerJoinedNetwork(string peerId, string netId, string helloData);
        void OnPeerLeftNetwork(string p2pId, string netId);
        void OnPeerSync(string channelId, string p2pId, long clockOffsetMs, long netLagMs);

        // TODO: do we want PeerJoinedChannel-type notifications?
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
        protected IGameNetClient client = null;
        protected IP2pNet p2p = null;
        public UniLogger logger;

        // Some client callbacks can happen as a direct result of a call, but we would like for
        // them to be dispatched during poll(), rather than during th ecall itself. Put them
        // in this queue and it'll happen that way.
        // OnGameCreated() is an example of one that might take a while, or might
        // happen immediately.
        protected Queue<Action> callbacksForNextPoll;
        protected Queue<Action> loopedBackMessageHandlers; // messages that come from this node (loopbacks) get handled at the end of the loop

        public GameNetBase()
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
            IP2pNet ip2p = null;
            string[] parts = p2pConnectionString.Split(new string[]{"::"},StringSplitOptions.None); // Yikes! This is fugly.

            switch(parts[0].ToLower())
            {
                case "p2ploopback":
                    ip2p = new P2pLoopback(this, null);
                    break;
                default:
                    throw( new Exception($"Invalid connection type: {parts[0]}"));
            }

            if (ip2p == null)
                throw( new Exception("p2p Connect failed"));

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

        public abstract void CreateNetwork<T>(T t); // really can only be defined in the game-specific implmentation

        protected void _SyncTrivialNewNetwork()
        {
            // The most basic thing you can in a CreateGame() implmentation.
            // It actually does nothing at all except to make up a random name and then call back saying a game was created.
            // This works because at its *absolute simplest* a "game" is just an agree-to p2p channel, and "joining" it
            // just means subscribing to it.
            string newId = "APIANNET" + System.Guid.NewGuid().ToString();
            callbacksForNextPoll.Enqueue( () => client.OnNetworkCreated(newId));
        }


        public virtual void JoinNetwork(P2pNetChannelInfo netP2pChannel, string netLocalData)
        {
            p2p.Join(netP2pChannel, netLocalData);
            callbacksForNextPoll.Enqueue( () => this.OnPeerJoined( netP2pChannel.id, LocalP2pId(), netLocalData));

        }
        public virtual void LeaveNetwork()
        {
            callbacksForNextPoll.Enqueue( () => client.OnPeerLeftNetwork(LocalP2pId(), CurrentNetworkId()));
            p2p.Leave();
        }

        public virtual void AddChannel(P2pNetChannelInfo subChannel, string channelLocalData)
        {
            p2p.AddSubchannel(subChannel, channelLocalData);
        }
        public virtual void RemoveChannel(string subChannelId)
        {
            p2p.RemoveSubchannel(subChannelId);
        }

        public virtual void Loop()
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

            p2p?.Loop();

        }

        public string LocalP2pId() => p2p?.GetId();
        public string CurrentNetworkId() => p2p?.GetMainChannel().Id;

        //
        // IP2pNetClient
        //
        public virtual void OnPeerJoined(string channel, string p2pId, string helloData)
        {
            // See P2pHelloData() comment regarding actual data struct
            if (channel == CurrentNetworkId())
                client.OnPeerJoinedNetwork(p2pId, CurrentNetworkId(), helloData);

            // FIXME: what about other channels?
        }

        public virtual void OnPeerSync(string channelId, string p2pId, long clockOffsetMs, long netLagMs)
        {
            client.OnPeerSync(channelId, p2pId, clockOffsetMs, netLagMs);
        }

        public virtual void OnPeerLeft(string channelId, string p2pId)
        {
            // FIXME: what about other channels?
            if (channelId == CurrentNetworkId())
                client.OnPeerLeftNetwork(p2pId, CurrentNetworkId());
        }

        public void OnClientMsg(string from, string to, long msSinceSent, string payload)
        {
            GameNetClientMessage gameNetClientMessage = JsonConvert.DeserializeObject<GameNetClientMessage>(payload);

            if (from == LocalP2pId())
                loopedBackMessageHandlers.Enqueue( () => _HandleClientMessage(from, to, msSinceSent, gameNetClientMessage));
            else
                _HandleClientMessage(from, to, msSinceSent, gameNetClientMessage);

        }

        // Derived classes Must implment this, as well as client-specific messages
        // that call _SendClientMessage()

        protected abstract void _HandleClientMessage(string from, string to, long msSinceSent, GameNetClientMessage clientMessage);


        protected void _SendClientMessage(string _toChan, string _clientMsgType, string _payload)
        {
            string gameNetClientMsgJSON = JsonConvert.SerializeObject(new GameNetClientMessage(){clientMsgType=_clientMsgType, payload=_payload});
            p2p.Send(_toChan, gameNetClientMsgJSON);
        }

    }
}
