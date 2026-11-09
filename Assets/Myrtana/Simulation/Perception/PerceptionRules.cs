using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using Myrtana.Core;

namespace Myrtana.Simulation
{
    /// <summary>What an NPC notices in its active perception scan, nearest first per slot (perception.json slots).</summary>
    public enum PerceptionSlot { Player, Friendly, Neutral, Angry, Hostile, Prey, Panic, Body, Food }

    /// <summary>Things that happen and are announced to the NPCs around (G3 SendPerception*).</summary>
    public enum BroadcastKind { Warn, FightSound, Defeat, Murder, TakeItem, UseInteractObject, CallHelp, EnterZone }

    /// <summary>How far a broadcast carries and whether a receiver must see its origin.</summary>
    public sealed class BroadcastRule
    {
        [JsonConstructor]
        public BroadcastRule(BroadcastKind kind, float range, bool lineOfSight, float? revolutionRange = null)
        {
            Kind = kind;
            Range = range;
            LineOfSight = lineOfSight;
            RevolutionRange = revolutionRange;
        }

        public BroadcastKind Kind { get; }
        /// <summary>Centimetres (G3 units).</summary>
        public float Range { get; }
        public bool LineOfSight { get; }
        /// <summary>The fight sound carries further while the receiver's own enclave is under attack.</summary>
        public float? RevolutionRange { get; }
    }

    public sealed class SlotRange
    {
        [JsonConstructor]
        public SlotRange(PerceptionSlot slot, float range)
        {
            Slot = slot;
            Range = range;
        }

        public PerceptionSlot Slot { get; }
        public float Range { get; }
    }

    /// <summary>The warn – warn – attack sequence of an NPC that dislikes what the player does (perception.json warnings).</summary>
    public sealed class WarningTimings
    {
        [JsonConstructor]
        public WarningTimings(double secondWarning, double attack, float range)
        {
            SecondWarning = secondWarning;
            Attack = attack;
            Range = range;
        }

        /// <summary>Real seconds after the first warning (4).</summary>
        public double SecondWarning { get; }
        /// <summary>Real seconds after the first warning (8).</summary>
        public double Attack { get; }
        /// <summary>Centimetres: the NPC follows the player this close while warning.</summary>
        public float Range { get; }
    }

    /// <summary>
    /// The senses of NPCs and the reach of perception broadcasts (perception.json; Docs/perception-crime.md §2). All
    /// distances are centimetres (G3 units); poses are metres, see <see cref="CentimetresPerUnit"/>.
    /// </summary>
    public sealed class PerceptionRules : IDefinition
    {
        public const string DefinitionId = "perceptionRules";
        /// <summary>World poses are in metres, G3 distances in centimetres.</summary>
        public const float CentimetresPerUnit = 100f;

        [JsonConstructor]
        public PerceptionRules(string id, double activeInterval, double gateGuardInterval, float fieldOfView, float hearingRange,
                               float maxHeightDifference, float sneakVsAnimalsRange, IReadOnlyList<SlotRange> slots,
                               float arenaOrAttackerRange, IReadOnlyList<BroadcastRule> broadcasts, WarningTimings weaponWarnings,
                               WarningTimings zoneWarnings, float weaponRange, float zoneMaxHeight, float sneakRange,
                               float watchFightMin, float watchFightMax, float wakeRangeIndoor, float wakeRangeOutdoor,
                               float pursueGiveUpRange, float creatureSenseRange = DefaultCreatureSenseRange,
                               float occludedHearingFactor = DefaultOccludedHearingFactor)
        {
            Id = id ?? DefinitionId;
            ActiveInterval = activeInterval;
            GateGuardInterval = gateGuardInterval;
            FieldOfView = fieldOfView;
            HearingRange = hearingRange;
            MaxHeightDifference = maxHeightDifference;
            SneakVsAnimalsRange = sneakVsAnimalsRange;
            Slots = slots ?? Array.Empty<SlotRange>();
            ArenaOrAttackerRange = arenaOrAttackerRange;
            Broadcasts = broadcasts ?? Array.Empty<BroadcastRule>();
            WeaponWarnings = weaponWarnings ?? throw new ArgumentNullException(nameof(weaponWarnings));
            ZoneWarnings = zoneWarnings ?? throw new ArgumentNullException(nameof(zoneWarnings));
            WeaponRange = weaponRange;
            ZoneMaxHeight = zoneMaxHeight;
            SneakRange = sneakRange;
            WatchFightMin = watchFightMin;
            WatchFightMax = watchFightMax;
            WakeRangeIndoor = wakeRangeIndoor;
            WakeRangeOutdoor = wakeRangeOutdoor;
            PursueGiveUpRange = pursueGiveUpRange;
            CreatureSenseRange = creatureSenseRange;
            OccludedHearingFactor = Math.Max(0f, Math.Min(1f, occludedHearingFactor));
        }

        /// <summary>Port default of <see cref="CreatureSenseRange"/> (cm, fix-204).</summary>
        public const float DefaultCreatureSenseRange = 1000f;
        /// <summary>Port default of <see cref="OccludedHearingFactor"/> (fix-204).</summary>
        public const float DefaultOccludedHearingFactor = 0.5f;

        /// <summary>
        /// Port (fix-204): a creature (non-humanoid) senses a candidate this close (cm) without seeing it - smell and hearing;
        /// farther away it needs a free line of sight. G3 has no sight test for creatures at all (= a range above every slot's);
        /// the owner saw wolves aggro through rock walls and from below cliffs.
        /// </summary>
        public float CreatureSenseRange { get; }

        /// <summary>
        /// Port (fix-204): the share (0..1) of a hearing range that carries through a wall or the ground (no free line between
        /// the listener and the source): a humanoid's hearing, the fight sound, warnings and calls for help, a sleeping animal's
        /// wake range. G3: 1 (sound ignores obstacles).
        /// </summary>
        public float OccludedHearingFactor { get; }

        public string Id { get; }
        /// <summary>Real seconds between active perception scans of an Active NPC (1).</summary>
        public double ActiveInterval { get; }
        /// <summary>Gate guards check the player more often (0.1).</summary>
        public double GateGuardInterval { get; }
        /// <summary>Full horizontal opening angle of a humanoid's sight, degrees (180: the front half-space).</summary>
        public float FieldOfView { get; }
        /// <summary>A humanoid hears a moving, non-sneaking target this close (1000).</summary>
        public float HearingRange { get; }
        /// <summary>Nothing is perceived across a larger height difference (1000).</summary>
        public float MaxHeightDifference { get; }
        /// <summary>Animals do not notice a sneaking or blocking player beyond this (1000).</summary>
        public float SneakVsAnimalsRange { get; }
        public IReadOnlyList<SlotRange> Slots { get; }
        /// <summary>An NPC in the arena or attacking the player keeps track of it this far, without a sense check (4000).</summary>
        public float ArenaOrAttackerRange { get; }
        public IReadOnlyList<BroadcastRule> Broadcasts { get; }
        public WarningTimings WeaponWarnings { get; }
        public WarningTimings ZoneWarnings { get; }
        /// <summary>A drawn weapon is noticed this close (700).</summary>
        public float WeaponRange { get; }
        /// <summary>CP: a trespasser more than this above or below the NPC is ignored (330).</summary>
        public float ZoneMaxHeight { get; }
        /// <summary>A sneaking player this close makes an NPC suspicious (700).</summary>
        public float SneakRange { get; }
        public float WatchFightMin { get; }
        public float WatchFightMax { get; }
        /// <summary>A sleeper wakes when a non-friend moves in its house this close (1000) …</summary>
        public float WakeRangeIndoor { get; }
        /// <summary>… or this close outdoors (500).</summary>
        public float WakeRangeOutdoor { get; }
        /// <summary>A warning NPC gives up when the intruder is this far away (2000).</summary>
        public float PursueGiveUpRange { get; }

        string IDefinition.Key => Id;

        // loops, not FirstOrDefault: per scan and per broadcast receiver, no closure (perf-9)
        public float RangeOf(PerceptionSlot slot)
        {
            for (int i = 0; i < Slots.Count; i++)
                if (Slots[i] != null && Slots[i].Slot == slot) return Slots[i].Range;
            return 0;
        }

        public BroadcastRule BroadcastOf(BroadcastKind kind)
        {
            for (int i = 0; i < Broadcasts.Count; i++)
                if (Broadcasts[i] != null && Broadcasts[i].Kind == kind) return Broadcasts[i];
            return null;
        }

        /// <summary>
        /// Real seconds between active scans at a detail level; null = no active perception. Only Active NPCs look
        /// around (G3: only NPCs in processing range get OnTick). Near and Abstract NPCs only receive broadcasts
        /// (Near) or nothing at all (Abstract; owner decision: no off-screen events).
        /// </summary>
        public double? ScanIntervalOf(DetailLevel detail, bool gateGuard = false) =>
            detail == DetailLevel.Active ? (gateGuard ? GateGuardInterval : ActiveInterval) : (double?)null;

        /// <summary>Whether an NPC at this detail level receives broadcasts.</summary>
        public static bool ReceivesBroadcasts(DetailLevel detail) => detail != DetailLevel.Abstract;
    }
}
