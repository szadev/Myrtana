using System;
using System.Collections.Generic;
using Myrtana.Core;
using Myrtana.Rules;

namespace Myrtana.Simulation
{
    /// <summary>Someone the scanning NPC might notice, with the NPC's attitude toward it.</summary>
    public struct PerceptionCandidate
    {
        public SenseTarget Target;
        public bool IsPlayer;
        /// <summary>The observer's attitude toward the candidate (from the attitude service).</summary>
        public Attitude Attitude;
        /// <summary>Knocked out or dead: a body (and food for a predator).</summary>
        public bool Body;
    }

    /// <summary>One noticed actor of a slot.</summary>
    public readonly struct Percept
    {
        public Percept(ActorId actor, float distance, Sense sense)
        {
            Actor = actor;
            Distance = distance;
            Sense = sense;
        }

        public ActorId Actor { get; }
        /// <summary>Centimetres.</summary>
        public float Distance { get; }
        public Sense Sense { get; }
    }

    /// <summary>The nearest noticed actor per slot after one active scan (G3 AssessActivePerception's table).</summary>
    public sealed class PerceptionSnapshot
    {
        // indexed by slot (a handful of slots; no dictionary per scan, perf-9)
        const int SlotCount = (int)PerceptionSlot.Food + 1;
        readonly Percept[] _nearest = new Percept[SlotCount];
        readonly bool[] _has = new bool[SlotCount];

        public bool TryGet(PerceptionSlot slot, out Percept percept)
        {
            percept = _nearest[(int)slot];
            return _has[(int)slot];
        }

        public bool Has(PerceptionSlot slot) => _has[(int)slot];

        /// <summary>The slots the scan filled (null = all).</summary>
        internal Func<PerceptionSlot, bool> Wanted { get; set; }

        /// <summary>Empties every slot (a snapshot reused for the next scan).</summary>
        public void Clear()
        {
            Array.Clear(_has, 0, SlotCount);
            Array.Clear(_nearest, 0, SlotCount);
            Wanted = null;
        }

        internal void Offer(PerceptionSlot slot, Percept percept)
        {
            int i = (int)slot;
            if (!_has[i] || percept.Distance < _nearest[i].Distance)
            {
                _nearest[i] = percept;
                _has[i] = true;
            }
        }
    }

    /// <summary>
    /// The active perception scan of one NPC: every candidate is sorted into its slot (player, by attitude, bodies)
    /// and kept when it is the nearest one the NPC notices within the slot's range. Docs/perception-crime.md §2.2.
    /// </summary>
    public static class PerceptionScan
    {
        /// <param name="trackingPlayer">An NPC in the arena or holding the attack token against the player: it keeps
        /// the player within <see cref="PerceptionRules.ArenaOrAttackerRange"/> without a sense check.</param>
        /// <param name="wanted">The slots the caller reads (null = all): the others are skipped before any sense check or
        /// raycast (<see cref="ActivePerception.Reads"/>).</param>
        public static PerceptionSnapshot Scan(in SenseObserver observer, IEnumerable<PerceptionCandidate> candidates, PerceptionRules rules,
                                              ILineOfSight lineOfSight, bool trackingPlayer = false, Func<PerceptionSlot, bool> wanted = null)
        {
            return Scan(new PerceptionSnapshot(), observer, candidates, rules, lineOfSight, trackingPlayer, wanted);
        }

        /// <summary>The scan into <paramref name="snapshot"/> (cleared first; reused by a caller that scans every frame).</summary>
        public static PerceptionSnapshot Scan(PerceptionSnapshot snapshot, in SenseObserver observer, IEnumerable<PerceptionCandidate> candidates,
                                              PerceptionRules rules, ILineOfSight lineOfSight, bool trackingPlayer = false,
                                              Func<PerceptionSlot, bool> wanted = null)
        {
            snapshot.Clear();
            snapshot.Wanted = wanted;
            if (candidates is IReadOnlyList<PerceptionCandidate> list)
            {
                for (int i = 0; i < list.Count; i++) Consider(snapshot, observer, list[i], rules, lineOfSight, trackingPlayer);
                return snapshot;
            }
            foreach (var c in candidates) Consider(snapshot, observer, c, rules, lineOfSight, trackingPlayer);
            return snapshot;
        }

        static void Consider(PerceptionSnapshot snapshot, in SenseObserver observer, in PerceptionCandidate c, PerceptionRules rules,
                             ILineOfSight lineOfSight, bool trackingPlayer)
        {
            if (c.Target.Id == observer.Id) return;
            if (c.IsPlayer)
            {
                if (trackingPlayer && !c.Target.Undetectable)
                {
                    float d = Senses.Distance(observer.Pose, c.Target.Pose);
                    if (d <= rules.ArenaOrAttackerRange) snapshot.Offer(PerceptionSlot.Player, new Percept(c.Target.Id, d, Sense.Sensed));
                }
                else Offer(snapshot, PerceptionSlot.Player, observer, c, rules, lineOfSight);
            }
            if (c.Body)
            {
                Offer(snapshot, PerceptionSlot.Body, observer, c, rules, lineOfSight);
                if (c.Attitude == Attitude.Predator) Offer(snapshot, PerceptionSlot.Food, observer, c, rules, lineOfSight);
                return;
            }
            Offer(snapshot, SlotOf(c.Attitude), observer, c, rules, lineOfSight);
        }

        public static PerceptionSlot SlotOf(Attitude attitude)
        {
            switch (attitude)
            {
                case Attitude.Friendly: return PerceptionSlot.Friendly;
                case Attitude.Angry: return PerceptionSlot.Angry;
                case Attitude.Hostile: return PerceptionSlot.Hostile;
                case Attitude.Predator: return PerceptionSlot.Prey;
                case Attitude.Panic: return PerceptionSlot.Panic;
                default: return PerceptionSlot.Neutral;
            }
        }

        static void Offer(PerceptionSnapshot snapshot, PerceptionSlot slot, in SenseObserver observer, in PerceptionCandidate c,
                          PerceptionRules rules, ILineOfSight lineOfSight)
        {
            if (snapshot.Wanted != null && !snapshot.Wanted(slot)) return;
            float range = rules.RangeOf(slot);
            var sense = Senses.Perceive(observer, c.Target, range, rules, lineOfSight);
            if (sense == Sense.None) return;
            snapshot.Offer(slot, new Percept(c.Target.Id, Senses.Distance(observer.Pose, c.Target.Pose), sense));
        }
    }
}
