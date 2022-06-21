using System.Xml.Linq;
using System.Runtime.InteropServices;
using System.Linq;
using System.Net.Http;
using System;
using System.Collections.Generic;
using P2pNet;
using UniLog;
using static UniLog.UniLogger; // for SID

namespace Apian
{
    public class ApianGroupSynchronizer
    {

        public static  Dictionary<string, string> DefaultConfig = new Dictionary<string, string>()
        {
            {"StashedCmdsToApplyPerUpdate", "10"}, // applying locally received commands that we weren't ready for yet
            {"MaxSyncCmdsToSendPerUpdate", "10"} // sending commands to another peer to "catch it up"
        };

        protected class SyncingPeerData
        {
            public string peerId;
            public long nextCommandToSend;
            public long firstCommandPeerHas;

            public SyncingPeerData(string pid, long firstNeeded, long firstCmdPeerHas)
            {
                // The peer has been storing - but not applying - commands while waiting for the
                // leader to send data, and keeps doing so while syncing. It can be assumed the peer
                // ALREADY has "firstCmdPeerHas" through whatever is current.
                peerId = pid;
                nextCommandToSend = firstNeeded;
                firstCommandPeerHas = firstCmdPeerHas + 1; // add 1 to what we think we need to send
            }
        }

        public UniLogger Logger;

        protected ApianBase ApianInst;

        // Stashing commands we aren't ready for yet so we
        // can apply them once we've caught up,
        private int StashedCmdsToApplyPerUpdate {get; set;}
        private readonly Dictionary<long, ApianCommand> CommandStash; // commands we have save up LOCALLY until we have been sync'ed up to that point
        private long MaxStashedCmdSeqNum; // largest we have received
        private long MaxAppliedCmdSeqNum {get => ApianInst?.MaxAppliedCmdSeqNum ?? -1;}


        // Sending commands to sync other peers
        public int MaxSyncCmdsToSendPerUpdate {get; private set;}
        protected Dictionary<string, SyncingPeerData> syncingPeers;

        public ApianGroupSynchronizer(ApianBase apianInst, Dictionary<string,string> config)
        {
            Logger = UniLogger.GetLogger("ApianGroupSynchronizer");
            ApianInst = apianInst;
            _ParseConfig(config ?? DefaultConfig);
            syncingPeers = new Dictionary<string, SyncingPeerData>();
            CommandStash = new Dictionary<long, ApianCommand>();
        }

        private void _ParseConfig(Dictionary<string,string> configDict)
        {
            MaxSyncCmdsToSendPerUpdate = int.Parse(configDict["MaxSyncCmdsToSendPerUpdate"]);
            StashedCmdsToApplyPerUpdate = int.Parse(configDict["StashedCmdsToApplyPerUpdate"]);
        }

        //
        // API
        //

        public void StashCommand(ApianCommand cmd)
        {
            CommandStash[cmd.SequenceNum] = cmd;
            MaxStashedCmdSeqNum = Math.Max(MaxStashedCmdSeqNum, cmd.SequenceNum);
        }


        public bool ApplyStashedCommands()
        {
            // Returns TRUE if we applied a command that we think makes the current peer up-to-date
            // returns FALSE otherwise - whether we did anything or not
            for(int i=0; i<StashedCmdsToApplyPerUpdate;i++)
            {
                if (CommandStash.Count == 0)
                    break;

                long expectedSeqNum = MaxAppliedCmdSeqNum+1; // note that MaxAppliedCmdSeqNum gets updated by Apian.ApplyStashedCommand. Kinda ugly?
                if (CommandStash.ContainsKey(expectedSeqNum))
                {
                    ApianInst.ApplyStashedApianCommand(CommandStash[expectedSeqNum]); // this updates MaxAppliedCmdSeqNum
                    CommandStash.Remove(expectedSeqNum); // TODO: remove all comamnds <= expectedSeqNum
                    if (MaxAppliedCmdSeqNum >= MaxStashedCmdSeqNum )
                        return true;
                }
                else
                {
                    // This is NOT a bad thing unless we are synchronizing.
                    // NOTE: Implmenting the above TODO (deleting commands < what has been applied) makes the
                    // uncertainty go away and this condition is always a bad thing.
                    if (MaxAppliedCmdSeqNum < MaxStashedCmdSeqNum)
                        Logger.Debug($"{this.GetType().Name}.ApplyStashedCommands(). Next expected command #{expectedSeqNum} not found in stash.");
                    break;
                }
            }
            return false;
        }

        public virtual void ApplyCheckpointStateData(long epoch, long seqNum, long timeStamp, string stateHash, string stateData)
        {
            ApianInst.ApplyCheckpointStateData( epoch, seqNum, timeStamp, stateHash, stateData);
            // MaxStashedCmdSeqNum = seqNum; // short circuits most of the command-by-command sync process
            Logger.Verbose($"{this.GetType().Name}.ApplyCheckpointStateData(). Serialized data applied. New max applied seq: {MaxAppliedCmdSeqNum}");
        }

        public void AddSyncingPeer(string peerId, long firstNeededCmd, long firstCmdPeerHas )
        {
            Logger.Info($"{this.GetType().Name}.AddSyncingPeer() Sending peer {SID(peerId)} stashed commands from {firstNeededCmd} through {firstCmdPeerHas}");
            SyncingPeerData peer = !syncingPeers.ContainsKey(peerId) ? new SyncingPeerData(peerId, firstNeededCmd, firstCmdPeerHas)
                : _UpdateSyncingPeer(syncingPeers[peerId], firstNeededCmd, firstCmdPeerHas);
            syncingPeers[peerId] = peer;
        }

        public void SendSyncData()
        {
            List<string> donePeers = new List<string>();
            int msgsLeft = MaxSyncCmdsToSendPerUpdate;
            foreach (SyncingPeerData sPeer in syncingPeers.Values )
            {
                msgsLeft -= _SendCmdsToOnePeer(sPeer, msgsLeft);
                if (sPeer.nextCommandToSend >= sPeer.firstCommandPeerHas) // might be greater than if checkpoint data ends after firstCommandPeerHas
                    donePeers.Add(sPeer.peerId);
                if (msgsLeft <= 0)
                    break;
            }

            foreach (string id in donePeers)
            {
                syncingPeers.Remove(id);
            }
        }

        //

        private SyncingPeerData _UpdateSyncingPeer(SyncingPeerData peerData,  long firstCmdNeededByPeer, long firstCmdPeerHas)
        {
            // Update the adta for a peer already syncing. (really?)
            // TODO: This should not be able to happen
            peerData.nextCommandToSend = Math.Min(peerData.nextCommandToSend, firstCmdNeededByPeer);
            peerData.firstCommandPeerHas = Math.Max(peerData.firstCommandPeerHas, firstCmdPeerHas) + 1; // add 1 to what we think we need to send
            return peerData;
        }

        private int _SendCmdsToOnePeer(SyncingPeerData sPeer, int maxToSend)  // true means "keep going"
        {
            Dictionary<long, ApianCommand> CommandLog = ApianInst.AppliedCommands;
            int cmdsSent = 0;
            Logger.Verbose($"{this.GetType().Name}._SendCmdsToOnePeer() Preparing to send {SID(sPeer.peerId)} stashed commands starting with: {sPeer.nextCommandToSend}");
            for (int i=0; i<maxToSend;i++)
            {
                if (CommandLog.ContainsKey(sPeer.nextCommandToSend))
                {
                    ApianCommand cmd =  CommandLog[sPeer.nextCommandToSend];
                    ApianInst.GameNet.SendApianMessage(sPeer.peerId, cmd);
                    cmdsSent++;
                    sPeer.nextCommandToSend++;
                    if (sPeer.nextCommandToSend == sPeer.firstCommandPeerHas)
                        break;
                }
                else
                {
                    Logger.Warn($"{this.GetType().Name}._SendCmdsToOnePeer() command {sPeer.nextCommandToSend} not in local stash!");
                    break;
                }
            }
            Logger.Info($"{this.GetType().Name}._SendCmdsToOnePeer() Sent {SID(sPeer.peerId)} {cmdsSent} stashed commands starting with: {sPeer.nextCommandToSend-cmdsSent}");
            return cmdsSent;
        }

    }

}