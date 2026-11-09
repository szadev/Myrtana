using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Myrtana.Core;

namespace Myrtana.Simulation
{
    /// <summary>
    /// A place an NPC goes to in its daily routine: a free point (guard post, smalltalk spot) or an interaction object
    /// (anvil, bench, bed). <see cref="Slots"/> are where its users stand, one NPC per slot. <see cref="Interaction"/> names
    /// what is done there ("Anvil", "Bed", "Guard" …); the presentation picks the animation for it.
    /// </summary>
    public sealed class RoutinePointDef : IDefinition
    {
        [JsonConstructor]
        public RoutinePointDef(Id<RoutinePointDef> id, string name, string interaction, IReadOnlyList<WorldPose> slots,
                               Id<AnchorDef> anchor = default, ActorId owner = default, IReadOnlyList<string> slotDirections = null)
        {
            Id = id;
            Name = name;
            Interaction = interaction ?? string.Empty;
            Slots = slots != null && slots.Count > 0 ? slots : throw new ArgumentException($"Routine point {id} has no slot.");
            Anchor = anchor;
            Owner = owner;
            SlotDirections = slotDirections;
        }

        public Id<RoutinePointDef> Id { get; }
        /// <summary>Name in the world editor (informational).</summary>
        public string Name { get; }
        public string Interaction { get; }
        public IReadOnlyList<WorldPose> Slots { get; }
        /// <summary>The group this point belongs to (empty = none).</summary>
        public Id<AnchorDef> Anchor { get; }
        /// <summary>Only this actor may use the point (empty = anyone).</summary>
        public ActorId Owner { get; }
        /// <summary>Per slot, the side the interaction is done from ("Fwd", "Back", "Left", "Right"); null = all "Fwd".
        /// A bed's two slots lie down to the left and to the right.</summary>
        public IReadOnlyList<string> SlotDirections { get; }

        public string DirectionOf(int slot) =>
            SlotDirections != null && slot >= 0 && slot < SlotDirections.Count && !string.IsNullOrEmpty(SlotDirections[slot]) ? SlotDirections[slot] : "Fwd";

        string IDefinition.Key => Id.Name;
    }

    /// <summary>How the points of an anchor are used when an NPC is sent to one of them (gameplay-logic.md §5.5).</summary>
    public enum AnchorKind
    {
        /// <summary>A cluster of stations (a smithy): any free station of the anchor will do.</summary>
        Local,
        /// <summary>A named set of points: a random free one, a different one each time.</summary>
        Roam,
        /// <summary>Ordered points walked one after the other.</summary>
        Patrol,
        /// <summary>Used by story events only.</summary>
        Event,
    }

    /// <summary>A group of routine points with a common capacity.</summary>
    public sealed class AnchorDef : IDefinition
    {
        [JsonConstructor]
        public AnchorDef(Id<AnchorDef> id, AnchorKind kind, int maxUsers = 0)
        {
            Id = id;
            Kind = kind;
            MaxUsers = maxUsers;
        }

        public Id<AnchorDef> Id { get; }
        public AnchorKind Kind { get; }
        /// <summary>NPCs that may use the anchor's points at once; 0 = one per slot.</summary>
        public int MaxUsers { get; }

        string IDefinition.Key => Id.Name;
    }

    /// <summary>The three routine points of an NPC. An empty point falls back to the working point.</summary>
    public sealed class NpcRoutineDef
    {
        [JsonConstructor]
        public NpcRoutineDef(Id<RoutinePointDef> working = default, Id<RoutinePointDef> relaxing = default,
                             Id<RoutinePointDef> sleeping = default)
        {
            Working = working;
            Relaxing = relaxing;
            Sleeping = sleeping;
        }

        public Id<RoutinePointDef> Working { get; }
        public Id<RoutinePointDef> Relaxing { get; }
        public Id<RoutinePointDef> Sleeping { get; }

        /// <summary>The point for <paramref name="phase"/>, or the working point when that one is empty.</summary>
        public Id<RoutinePointDef> PointFor(DayPhase phase)
        {
            var point = phase switch
            {
                DayPhase.Sleep => Sleeping,
                DayPhase.Relax => Relaxing,
                _ => Working,
            };
            return point.IsEmpty ? Working : point;
        }

        [JsonIgnore]
        public bool IsEmpty => Working.IsEmpty && Relaxing.IsEmpty && Sleeping.IsEmpty;
    }
}
