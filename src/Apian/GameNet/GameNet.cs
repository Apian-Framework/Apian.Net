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
        void CreateGame<T>(T createGameData);
        void JoinGame(P2pNetChannelInfo gameP2pChannel);
        void AddChannel(P2pNetChannelInfo subChannel);
        void RemoveChannel(string subchannelId);
        void LeaveGame();
        string LocalP2pId();
        string CurrentGameId();
        void Loop(); /// <summary> needs to be called periodically (drives message pump + group handling)</summary>
    }

    public interface IGameNetClient
    {
        void OnGameCreated(string gameP2pChannel);
        void OnPeerJoinedGame(string peerId, string gameId, string helloData);
        void OnPeerLeftGame(string p2pId, string gameId);
        void OnPeerSync(string p2pId, long clockOffsetMs, long netLagMs);
        string LocalPeerData(); // client serializes this app-specific stuff
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

        public abstract void CreateGame<T>(T t); // really can only be defined in the game-specific implmentation

        protected void _SyncTrivialNewGame()
        {
            // The most basic thing you can in a CreateGame() implmentation.
            // It actually does nothing at all except to make up a random name and then call back saying a game was created.
            // This works because at its *absolute simplest* a "game" is just an agree-to p2p channel, and "joining" it
            // just means subscribing to it.
            string newGameId = "GAME" + System.Guid.NewGuid().ToString();
            callbacksForNextPoll.Enqueue( () => client.OnGameCreated(newGameId));
        }


        public virtual void JoinGame(P2pNetChannelInfo gameP2pChannel)
        {
            p2p.Join(gameP2pChannel);
            callbacksForNextPoll.Enqueue( () => this.OnPeerJoined( LocalP2pId(),  client.LocalPeerData()));

        }
        public virtual void LeaveGame()
        {
            callbacksForNextPoll.Enqueue( () => client.OnPeerLeftGame(LocalP2pId(), CurrentGameId()));
            p2p.Leave();
        }

        public virtual void AddChannel(P2pNetChannelInfo subChannel)
        {
            p2p.AddSubchannel(subChannel);
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
        public string CurrentGameId() => p2p?.GetMainChannel().id;

        //
        // IP2pNetClient
        //
        public virtual string P2pHelloData()
        {
            // TODO: might want to put localPlayerData into a larger GameNet-level object
            return client.LocalPeerData(); // Client (which knows about the fnal class) serializes this
        }
        public virtual void OnPeerJoined(string p2pId, string helloData)
        {
            // See P2pHelloData() comment regarding actual data struct
            client.OnPeerJoinedGame(p2pId, CurrentGameId(), helloData);
        }

        public virtual void OnPeerSync(string p2pId, long clockOffsetMs, long netLagMs)
        {
            client.OnPeerSync(p2pId, clockOffsetMs, netLagMs);
        }

        public virtual void OnPeerLeft(string p2pId)
        {
            client.OnPeerLeftGame(p2pId, CurrentGameId());
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
