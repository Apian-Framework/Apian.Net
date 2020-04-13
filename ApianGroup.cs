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
        ApianMessage DeserializeMessage(string subType, string json);           
        void OnApianMessage(ApianMessage msg, string msgSrc, string msgChan);    
        void StartLocalOnlyGroup();
     
    }
}