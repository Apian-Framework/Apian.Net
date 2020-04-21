using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using GameNet;
using P2pNet;

namespace Apian
{
    public interface IApianGameNet : IGameNet
    {
        void SendApianMessage(string toChannel, ApianMessage appMsg);
    }

    public interface IApianGameNetClient : IGameNetClient
    {
        ApianMessage DeserializeApianMessage(string msgType, string msgJSON);
        void OnApianMessage(string srcId, string destId, ApianMessage msg, long msgDelay);
    }

    public class ApianGameNet : GameNetBase, IApianGameNet
    {
        public ApianBase ApianInst {get; protected set;}
        protected Dictionary<string, Action<string, string, long, GameNetClientMessage>> _MsgHandlers;
        public class GameCreationData {}

        public ApianGameNet() : base()
        {
            _MsgHandlers = new  Dictionary<string, Action<string, string, long, GameNetClientMessage>>()
            {
                [ApianMessage.CliRequest] = (f,t,s,m) => this._HandleApianMessage(f,t,s,m),
                [ApianMessage.CliObservation] = (f,t,s,m) => this._HandleApianMessage(f,t,s,m),
                [ApianMessage.CliCommand] = (f,t,s,m) => this._HandleApianMessage(f,t,s,m),
                [ApianMessage.GroupMessage] = (f,t,s,m) => this._HandleApianMessage(f,t,s,m),
                [ApianMessage.ApianClockOffset] = (f,t,s,m) => this._HandleApianMessage(f,t,s,m),
            };
        }

        public override void Loop()
        {
            base.Loop();
        }


        public override void  CreateGame<GameCreationData>(GameCreationData data)
        {
            logger.Verbose($"CreateGame()");
            _SyncTrivialNewGame(); // Creates/sets an ID and enqueues OnGameCreated()
        }

        // Sending

        public void SendApianMessage(string toChannel, ApianMessage appMsg)
        {
            logger.Verbose($"SendApianMessage() - type: {appMsg.MsgType}, To: {toChannel}");
            _SendClientMessage( toChannel, appMsg.MsgType,  JsonConvert.SerializeObject(appMsg));

        }

        //
        // Beam message handlers
        //
        protected override void _HandleClientMessage(string from, string to, long msSinceSent, GameNetClientMessage msg)
        {
                // Turns out we're best-off letting it throw rather than handling exceptions
                _MsgHandlers[msg.clientMsgType](from, to, msSinceSent, msg);
        }

         protected void _HandleApianMessage(string from, string to, long msSinceSent, GameNetClientMessage clientMessage)
        {
            ApianMessage apMsg = (client as IApianGameNetClient).DeserializeApianMessage(clientMessage.clientMsgType,clientMessage.payload);
            logger.Verbose($"_HandleApianMessage() Type: {apMsg.MsgType}, src: {(from==LocalP2pId()?"Local":from)}");
            (client as IApianGameNetClient).OnApianMessage(from, to, apMsg, msSinceSent);
        }
    }

}