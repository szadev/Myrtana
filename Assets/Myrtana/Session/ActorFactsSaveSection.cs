using System;
using System.Collections.Generic;
using Myrtana.Core;
using Myrtana.Simulation;

namespace Myrtana.Session
{
    /// <summary>
    /// Save section "actors.talked": which NPCs of the content have talked to the player, sorted by id. fix-157: one pass
    /// over the actor registry (no LINQ over every content NPC: the crash-recovery snapshot runs every 2 minutes); same
    /// bytes as before, so the version stays 1.
    /// </summary>
    public sealed class ActorFactsSaveSection : ISaveParticipant
    {
        readonly ActorRegistry _actors;
        readonly IContentDatabase _content;
        readonly Func<ActorState, bool> _talkedContentActor;
        readonly List<ActorState> _talked = new List<ActorState>();

        public ActorFactsSaveSection(ActorRegistry actors, IContentDatabase content)
        {
            _actors = actors;
            _content = content;
            // a content NPC only (an actor that appeared during play under a content id is the same as before: listed)
            _talkedContentActor = a => a.TalkedToPlayer && _content.TryGet(a.Id.Name, out NpcDef _);
        }

        public string Section => "actors.talked";
        public int Version => 1;

        public void Write(ISaveWriter w)
        {
            _actors.CollectSorted(_talkedContentActor, _talked);
            w.WriteInt32(_talked.Count);
            foreach (var a in _talked) w.WriteString(a.Id.Name);
            _talked.Clear();
        }

        public void Read(ISaveReader r, int version)
        {
            foreach (var name in r.ReadStrings()) _actors.MarkTalkedToPlayer(new ActorId(name));
        }
    }
}
