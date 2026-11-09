using System;
using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;

namespace Myrtana.Simulation
{
    /// <summary>What happens to a dead NPC that enters the processing range (G3 OnEnterProcessingRange → Respawn).</summary>
    public enum CorpseVerdict
    {
        /// <summary>It stays (too fresh, mission items, the player or a living human too close for a revival).</summary>
        Keep,
        /// <summary>First time seen without a time of death: the clock starts now.</summary>
        Stamp,
        /// <summary>Gone for good (G3: species 40 and moved away).</summary>
        Remove,
        /// <summary>Alive again in place, refreshed, without its generated loot and its fight memory.</summary>
        Revive,
    }

    /// <summary>The facts <see cref="CorpseRules.Judge"/> needs about one corpse entering the range.</summary>
    public struct CorpseFacts
    {
        /// <summary>The NPC's template name (an id's part before '#', else the id), matched against the revival lists.</summary>
        public string Template;
        public string Species;
        public bool AmbientCreature;
        /// <summary>Game hours of the last fight / death; null = never stamped.</summary>
        public double? DiedAt;
        public bool CarriesMissionItem;
        /// <summary>Burning or inside a magic barrier when it is judged: never revived (G3 status 0x1 / 0x100).</summary>
        public bool BurningOrBarrier;
        /// <summary>Centimetres to the player.</summary>
        public float PlayerDistance;
        /// <summary>Water deeper than 20 at the spot.</summary>
        public bool UnderWater;
        /// <summary>A living human or orc within 2500 (only checked for non-ambient creatures).</summary>
        public bool HumanoidNear;
    }

    /// <summary>
    /// CP 1.75 corpse removal and creature respawn (Engine_Engine_Callback Respawn.c, called by OnEnterProcessingRange for a
    /// dead NPC; Docs/npc-simulation.md §corpses). A dead NPC (never the player) disappears once 120 game hours (240
    /// for big creatures: troll, T-rex, dragon) have passed since its last fight, unless it carries a mission item; the
    /// first entry of an NPC without a time stamp stamps it. CP: before it goes, a creature may come back to life: ambient
    /// creatures 80 %, the common game animals 60 %, the bigger ones 40 %, humans and story NPCs never; a revival waits
    /// until the player is at least 3000 away and is called off over water or near a living human or orc (2500). Original
    /// G3 1.12 removed every corpse after 36 / 72 h and revived nothing.
    /// </summary>
    public static class CorpseRules
    {
        public const double DelayHours = 120, BigDelayHours = 240;
        /// <summary>Original G3 (1.12) delays, before the community patch.</summary>
        public const double ClassicDelayHours = 36, ClassicBigDelayHours = 72;
        public const float ReviveMinPlayerDistance = 3000f, ReviveNoHumanoidRange = 2500f;
        public const int AmbientChance = 80;

        static readonly HashSet<string> Common = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Alligator", "Bloodfly", "Boar", "Deer", "Lurker", "SandLurker", "Scavenger", "Snapper", "GreenSnapper",
            "Varant_Snapper_01", "SwampLurker", "Waran",
        };

        static readonly HashSet<string> Big = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Bison", "LonghairBison", "Goblin", "Ripper", "Lion", "Lioness", "Rhino", "LonghairRhino", "Sabertooth", "Shadowbeast",
            "IceWolf", "Schakal", "Warg", "Wolf",
        };

        public static double DelayOf(string species) => BattleLines.IsBig(species) ? BigDelayHours : DelayHours;

        /// <summary>The revival chance in percent (CP Respawn's name lists; template name first, then the species).</summary>
        public static int ReviveChance(string template, string species, bool ambientCreature)
        {
            if (ambientCreature) return AmbientChance;
            if (In(Common, template, species)) return 60;
            if (In(Big, template, species)) return 40;
            return 0;
        }

        static bool In(HashSet<string> set, string template, string species) =>
            (template != null && set.Contains(template)) || (species != null && set.Contains(species));

        /// <summary>The template part of an actor id ("Wolf#1a2b" → "Wolf").</summary>
        public static string TemplateOf(ActorId actor)
        {
            var name = actor.Name ?? "";
            int hash = name.IndexOf('#');
            return hash > 0 ? name.Substring(0, hash) : name;
        }

        /// <summary>
        /// G3 Respawn for a corpse entering the processing range at <paramref name="nowHours"/> (game hours);
        /// <paramref name="roll"/> is Random(100).
        /// </summary>
        public static CorpseVerdict Judge(in CorpseFacts c, double nowHours, int roll)
        {
            if (!(c.DiedAt is double died)) return CorpseVerdict.Stamp;
            if (nowHours - died < DelayOf(c.Species)) return CorpseVerdict.Keep;
            if (c.CarriesMissionItem) return CorpseVerdict.Keep;
            if (c.BurningOrBarrier || roll >= ReviveChance(c.Template, c.Species, c.AmbientCreature)) return CorpseVerdict.Remove;
            if (c.PlayerDistance < ReviveMinPlayerDistance) return CorpseVerdict.Keep;
            if (c.UnderWater || (!c.AmbientCreature && c.HumanoidNear)) return CorpseVerdict.Keep;
            return CorpseVerdict.Revive;
        }
    }

    /// <summary>
    /// The time of every NPC's last fight or death in game hours (G3 PSNpc.LastFightTimestamp, as far as corpses need it):
    /// stamped when an NPC dies, or when a corpse without a stamp first enters the processing range; and the content NPCs
    /// whose corpses are gone (G3 keeps the removed entity as species 40 far away: a loaded game must not bring them back).
    /// Saved ("corpses").
    /// </summary>
    public sealed class CorpseLedger : IActorStore, ISaveParticipant
    {
        readonly Dictionary<ActorId, double> _diedAt = new Dictionary<ActorId, double>();
        readonly HashSet<ActorId> _gone = new HashSet<ActorId>();

        /// <summary>The corpse was removed for good.</summary>
        public void MarkGone(ActorId npc)
        {
            if (!npc.IsEmpty && !npc.IsPlayer) _gone.Add(npc);
        }

        public bool IsGone(ActorId npc) => _gone.Contains(npc);

        public IReadOnlyCollection<ActorId> Gone => _gone;

        /// <summary>A loaded game whose gone corpses must leave the simulation again (once, on the next tick).</summary>
        public bool NeedsPurge { get; private set; }

        /// <summary>The gone corpses left the simulation again.</summary>
        public void Purged() => NeedsPurge = false;

        public double? DiedAt(ActorId npc) => _diedAt.TryGetValue(npc, out var h) ? h : (double?)null;

        public void Stamp(ActorId npc, double gameHours)
        {
            if (!npc.IsEmpty && !npc.IsPlayer) _diedAt[npc] = gameHours;
        }

        /// <summary>The actor left the game: its time stamp goes (a gone mark stays: it is why it left).</summary>
        public void Forget(ActorId actor) => _diedAt.Remove(actor);

        public bool Holds(ActorId actor) => _diedAt.ContainsKey(actor) || _gone.Contains(actor);

        public string Section => "corpses";
        public int Version => 1;

        public void Write(ISaveWriter w)
        {
            var ids = _diedAt.Keys.Select(k => k.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
            w.WriteInt32(ids.Count);
            foreach (var id in ids)
            {
                w.WriteString(id);
                w.WriteDouble(_diedAt[new ActorId(id)]);
            }
            var gone = _gone.Select(k => k.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
            w.WriteInt32(gone.Count);
            foreach (var id in gone) w.WriteString(id);
        }

        public void Read(ISaveReader r, int version)
        {
            _diedAt.Clear();
            _gone.Clear();
            int count = r.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                var id = new ActorId(r.ReadString());
                _diedAt[id] = r.ReadDouble();
            }
            int gone = r.ReadInt32();
            for (int i = 0; i < gone; i++) _gone.Add(new ActorId(r.ReadString()));
            NeedsPurge = _gone.Count > 0;
        }
    }
}
