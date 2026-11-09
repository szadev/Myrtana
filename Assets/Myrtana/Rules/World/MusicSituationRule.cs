using System;
using System.Collections.Generic;
using Myrtana.Core;

namespace Myrtana.Rules
{
    /// <summary>The enclave state the music rule cares about (G3 gEEnclaveStatus 1 Attack, 2 Flee = liberated).</summary>
    public enum EnclaveMusicStatus { None, Attack, Liberated }

    /// <summary>What the music rule needs to know about one NPC (any creature or human) near the player.</summary>
    public readonly struct MusicNpc
    {
        public MusicNpc(ActorId id, float distanceMetres, bool inCombat, ActorId target,
            string enclave = null, EnclaveMusicStatus enclaveStatus = EnclaveMusicStatus.None, int level = UnknownLevel,
            bool perceives = true)
        {
            Perceives = perceives;
            Id = id;
            DistanceMetres = distanceMetres;
            InCombat = inCombat;
            Target = target;
            Enclave = enclave;
            EnclaveStatus = enclaveStatus;
            Level = level;
        }

        /// <summary>No level known: the NPC counts as a full threat.</summary>
        public const int UnknownLevel = -1;

        public ActorId Id { get; }
        public float DistanceMetres { get; }
        /// <summary>In combat (G3 AI mode Combat; a fleeing NPC is not).</summary>
        public bool InCombat { get; }
        public ActorId Target { get; }
        public string Enclave { get; }
        public EnclaveMusicStatus EnclaveStatus { get; }
        /// <summary>
        /// The NPC's current level (G3 CurrentLevel: Level above the player's, capped by LevelMax; 0 for ambient
        /// creatures), for the port's threat option; <see cref="UnknownLevel"/> when not known.
        /// </summary>
        public int Level { get; }
        /// <summary>
        /// The NPC actually perceives its target (close, or a free line of sight from its eyes; fix-204). An attacker stuck below a
        /// cliff or behind a rock wall does not start fight music; once it plays, every attacker keeps it (no flapping).
        /// </summary>
        public bool Perceives { get; }
    }

    /// <summary>
    /// The music situation of the player, evaluated once per real second (Script_Game OnTick → HandleMusicSituation,
    /// Docs/gameplay-logic.md §11.3). First match wins: a menu open keeps the last answer; dead → "dead"; soul
    /// travel → "soultravel"; then the NPCs within 30 m in order: one of an attacking enclave → "revolution"; one of a
    /// freshly liberated enclave → "revolutionwon" (once per enclave); one in combat against the player or a party
    /// member → "showdown" for Rhobar, Zuben, Kan, Xardas or the Dragon (that NPC alone decides), otherwise it counts
    /// for "fight" once all NPCs are checked; else "explore". No hold or release delay: the music sequencer's exit
    /// links and transitions give the timing.
    /// <para>The port's option <see cref="ThreatOnly"/> (player feedback item 12; off = G3): the attackers only start
    /// fight music when their levels add up to at least <see cref="ThreatShare"/> of the hero's level (a lone scavenger
    /// late in the game does not; a pack of wolves does). Once fight music plays it stays while any attacker is left,
    /// so it does not flap as the pack thins out. Showdown, revolution and the rest are unchanged.</para>
    /// <para>Port (fix-204): an attacker that does not perceive its target (<see cref="MusicNpc.Perceives"/>: far and out of
    /// sight, below a cliff, behind rock) does not start fight or showdown music; once it plays such attackers keep it.</para>
    /// </summary>
    public sealed class MusicSituationRule
    {
        public const float RadiusMetres = 30f;
        public const string Explore = "explore", Fight = "fight", Showdown = "showdown", Dead = "dead",
            SoulTravel = "soultravel", Revolution = "revolution", Victory = "revolutionwon";

        /// <summary>Entity names (case-insensitive, without the "#id" suffix of placed copies) that make fight music a showdown.</summary>
        static readonly HashSet<string> ShowdownNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "Rhobar", "Zuben", "Kan", "Xardas", "Dragon" };

        readonly HashSet<string> _victoryPlayed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Share of the hero's level the attackers' levels must reach for fight music under <see cref="ThreatOnly"/>.</summary>
        public const float ThreatShare = 0.5f;

        /// <summary>The port's threat option (default off = G3: every attacker starts fight music).</summary>
        public bool ThreatOnly { get; set; }

        /// <summary>The hero's level for <see cref="ThreatOnly"/> (set by the caller before evaluating).</summary>
        public int HeroLevel { get; set; } = 1;

        /// <summary>The last answer (explore before the first evaluation).</summary>
        public string Current { get; private set; } = Explore;

        /// <summary>Enclaves whose liberation music has played (G3 PSEnclave MusicPlayed).</summary>
        public IEnumerable<string> VictoryPlayed => _victoryPlayed;

        public void MarkVictoryPlayed(string enclave)
        {
            if (!string.IsNullOrEmpty(enclave)) _victoryPlayed.Add(enclave);
        }

        public static bool IsShowdownBoss(ActorId npc)
        {
            var name = npc.Name ?? "";
            int hash = name.IndexOf('#');
            return ShowdownNames.Contains(hash < 0 ? name : name.Substring(0, hash));
        }

        /// <param name="isPartyMember">True for the player's party members (companions, summons).</param>
        public string Evaluate(bool menuOpen, bool playerDead, bool soulTravel, IEnumerable<MusicNpc> npcs,
            Func<ActorId, bool> isPartyMember = null)
        {
            if (menuOpen) return Current;
            return Current = Decide(playerDead, soulTravel, npcs, isPartyMember);
        }

        string Decide(bool playerDead, bool soulTravel, IEnumerable<MusicNpc> npcs, Func<ActorId, bool> isPartyMember)
        {
            if (playerDead) return Dead;
            if (soulTravel) return SoulTravel;
            bool fight = false;
            int threat = 0;
            bool unknownThreat = false;
            if (npcs == null) return Explore;
            foreach (var n in npcs)
            {
                if (n.Id.IsPlayer || n.DistanceMetres > RadiusMetres) continue;
                if (!string.IsNullOrEmpty(n.Enclave))
                {
                    if (n.EnclaveStatus == EnclaveMusicStatus.Attack) return Revolution;
                    if (n.EnclaveStatus == EnclaveMusicStatus.Liberated && _victoryPlayed.Add(n.Enclave)) return Victory;
                }
                if (!n.InCombat || n.Target.IsEmpty) continue;
                if (!n.Target.IsPlayer && (isPartyMember == null || !isPartyMember(n.Target))) continue;
                if (!n.Perceives && Current != Fight && Current != Showdown) continue; // fix-204
                if (IsShowdownBoss(n.Id)) return Showdown;
                fight = true;
                if (n.Level < 0) unknownThreat = true;
                else threat += n.Level;
            }
            if (!fight) return Explore;
            if (!ThreatOnly || unknownThreat || Current == Fight) return Fight;
            return IsThreat(threat, HeroLevel) ? Fight : Explore;
        }

        /// <summary>The attackers' summed level against the hero's (<see cref="ThreatShare"/>).</summary>
        public static bool IsThreat(int attackerLevels, int heroLevel) => attackerLevels >= ThreatShare * Math.Max(1, heroLevel);
    }
}
