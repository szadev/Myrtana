using System.Linq;
using Myrtana.Core;

namespace Myrtana.Rules
{
    /// <summary>
    /// Saves <see cref="ActorVitals"/>: the player's down state (HP/SP/MP are hero attributes, saved with the hero) and
    /// every NPC whose vitals differ from full strength or that the player has defeated. Unknown NPC ids are dropped.
    /// </summary>
    public sealed class VitalsSaveSection : ISaveParticipant
    {
        readonly ActorVitals _vitals;
        readonly System.Func<ActorId, bool> _known;

        /// <param name="known">Whether the content still has an NPC (saves of an older pack may name removed ones).</param>
        public VitalsSaveSection(ActorVitals vitals, System.Func<ActorId, bool> known)
        {
            _vitals = vitals;
            _known = known;
        }

        public string Section => "vitals";
        public int Version => 2;

        public void Write(ISaveWriter w)
        {
            WriteEntry(w, _vitals.Player);
            var npcs = _vitals.Npcs.OrderBy(p => p.Key.Name, System.StringComparer.Ordinal).ToList();
            w.WriteInt32(npcs.Count);
            foreach (var pair in npcs)
            {
                w.WriteString(pair.Key.Name);
                WriteEntry(w, pair.Value);
            }
        }

        public void Read(ISaveReader r, int version)
        {
            _vitals.Clear();
            _vitals.Restore(ActorId.Player, ReadEntry(r, version));
            int n = r.ReadInt32();
            for (int i = 0; i < n; i++)
            {
                var id = new ActorId(r.ReadString());
                var entry = ReadEntry(r, version);
                if (_known(id)) _vitals.Restore(id, entry);
            }
        }

        static void WriteEntry(ISaveWriter w, ActorVitals.Entry e)
        {
            w.WriteInt32((int)e.State);
            w.WriteInt32(e.HitPoints);
            w.WriteInt32(e.Stamina);
            w.WriteInt32(e.Mana);
            w.WriteDouble(e.DownSeconds);
            w.WriteBool(e.ArenaLoser);
            w.WriteBool(e.DefeatedByPlayer);
            w.WriteBool(e.HurtByPlayer);
        }

        static ActorVitals.Entry ReadEntry(ISaveReader r, int version)
        {
            var e = new ActorVitals.Entry
            {
                State = (VitalState)r.ReadInt32(),
                HitPoints = r.ReadInt32(),
                Stamina = r.ReadInt32(),
                Mana = r.ReadInt32(),
                DownSeconds = (float)r.ReadDouble(),
                ArenaLoser = r.ReadBool(),
                DefeatedByPlayer = r.ReadBool(),
            };
            if (version >= 2) e.HurtByPlayer = r.ReadBool(); // fix-93
            return e;
        }
    }
}
