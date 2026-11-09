using System;
using Myrtana.Core;
using Myrtana.Rules;

namespace Myrtana.Simulation
{
    /// <summary>What an NPC does about its active scan (G3 AssessActivePerception's steps, Docs/perception-crime.md §2.2).</summary>
    public enum PerceptionStep
    {
        None,
        /// <summary>Panic slot: ZS_Flee from <see cref="PerceptionDecision.Target"/>.</summary>
        Flee,
        /// <summary>The player steps of the crime code (grudge, hostile player, zone, weapon, gate guard, talk, sneaking).</summary>
        AssessPlayer,
        /// <summary>Food slot, non-humanoids: ZS_EatBody.</summary>
        EatBody,
        /// <summary>AssessTarget with <see cref="PerceptionDecision.Reason"/> (Prey, Enemy, a grudge's reason).</summary>
        Attack,
        /// <summary>Angry slot, non-humanoids: ZS_Threaten.</summary>
        Threaten,
    }

    public readonly struct PerceptionDecision
    {
        public PerceptionDecision(PerceptionStep step, ActorId target = default, AttackReason reason = AttackReason.None)
        {
            Step = step;
            Target = target;
            Reason = reason;
        }

        public PerceptionStep Step { get; }
        public ActorId Target { get; }
        public AttackReason Reason { get; }

        public override string ToString() => Step == PerceptionStep.Attack ? $"Attack {Target} ({Reason})" : $"{Step} {Target}";
    }

    /// <summary>The scanning NPC as the perception steps see it.</summary>
    public struct ActiveObserver
    {
        public bool Humanoid;
        /// <summary>
        /// The player is its party leader: a member of the hero's party or the NPC guiding him (CP: party leader == player
        /// skips the hostile step, only summons act; the members' look-out is the party driver's).
        /// </summary>
        public bool InPlayerParty;
        /// <summary>G3 IsUpset: a grudge against the player (step 2) and its reason.</summary>
        public bool UpsetWithPlayer;
        public AttackReason UpsetReason;
    }

    /// <summary>
    /// The non-crime steps of G3 AssessActivePerception (CP 0x100295f0, Script_Game AssessActivePerception.c) in their order:
    /// panic → ZS_Flee; grudge against the player; food body (non-humanoid) → ZS_EatBody; prey → AssessTarget(Prey); hostile →
    /// AssessTarget(Enemy) (not for the player's party); the player zone; angry (non-humanoid) → ZS_Threaten; then the player's
    /// weapon, gate guard, talk and sneaking steps. The slots hold any actor, so wolves threaten hunters, hunters attack
    /// wolves, orcs attack rebels and a wolf hunts a scavenger without the hero. Pure.
    /// </summary>
    public static class ActivePerception
    {
        /// <summary>
        /// The first step that applies. Humanoids hand every player matter to the crime steps (<see cref="PerceptionStep.AssessPlayer"/>,
        /// also when nothing else applies); non-humanoids attack a hostile or upset-with player themselves.
        /// </summary>
        public static PerceptionDecision Decide(PerceptionSnapshot s, in ActiveObserver o)
        {
            if (s == null) return default;
            if (s.TryGet(PerceptionSlot.Panic, out var panic)) return new PerceptionDecision(PerceptionStep.Flee, panic.Actor);
            if (o.UpsetWithPlayer && s.Has(PerceptionSlot.Player))
                return o.Humanoid ? new PerceptionDecision(PerceptionStep.AssessPlayer)
                                  : new PerceptionDecision(PerceptionStep.Attack, ActorId.Player, o.UpsetReason);
            if (!o.Humanoid && s.TryGet(PerceptionSlot.Food, out var food)) return new PerceptionDecision(PerceptionStep.EatBody, food.Actor);
            if (s.TryGet(PerceptionSlot.Prey, out var prey)) return new PerceptionDecision(PerceptionStep.Attack, prey.Actor, AttackReason.Prey);
            if (!o.InPlayerParty && s.TryGet(PerceptionSlot.Hostile, out var hostile))
            {
                if (hostile.Actor.IsPlayer && o.Humanoid) return new PerceptionDecision(PerceptionStep.AssessPlayer);
                return new PerceptionDecision(PerceptionStep.Attack, hostile.Actor, AttackReason.Enemy);
            }
            if (o.Humanoid) return new PerceptionDecision(PerceptionStep.AssessPlayer);
            if (s.TryGet(PerceptionSlot.Angry, out var angry)) return new PerceptionDecision(PerceptionStep.Threaten, angry.Actor);
            return default;
        }

        /// <summary>The slots <see cref="Decide"/> reads (the scan skips the others: no sense checks, no raycasts).</summary>
        public static bool Reads(PerceptionSlot slot, bool humanoid)
        {
            switch (slot)
            {
                case PerceptionSlot.Player:
                case PerceptionSlot.Panic:
                case PerceptionSlot.Prey:
                case PerceptionSlot.Hostile:
                    return true;
                case PerceptionSlot.Food:
                case PerceptionSlot.Angry:
                    return !humanoid;
                default:
                    return false;
            }
        }

        public static readonly Func<PerceptionSlot, bool> HumanoidSlots = slot => Reads(slot, true);
        public static readonly Func<PerceptionSlot, bool> CreatureSlots = slot => Reads(slot, false);

        /// <summary>The slots OnEatBody_Perception reads while eating.</summary>
        public static readonly Func<PerceptionSlot, bool> EaterSlots = slot =>
            slot == PerceptionSlot.Panic || slot == PerceptionSlot.Prey || slot == PerceptionSlot.Hostile ||
            slot == PerceptionSlot.Angry || slot == PerceptionSlot.Neutral;

        static readonly PerceptionSlot[] DriveOffOrder =
            { PerceptionSlot.Panic, PerceptionSlot.Prey, PerceptionSlot.Hostile, PerceptionSlot.Angry, PerceptionSlot.Neutral };

        /// <summary>
        /// OnEatBody_Perception (ZS_EatBody.cpp): while eating, the first of the panic, prey, hostile, angry and neutral slots
        /// whose actor is within <see cref="AnimalRules.DriveOffRange"/> of the body (not of the eater) is driven off:
        /// AssessTarget(DriveAway), which flees from a panic-slot actor. <paramref name="distanceToBody"/> in cm.
        /// </summary>
        public static PerceptionDecision DriveOff(PerceptionSnapshot s, ActorId body, Func<ActorId, float> distanceToBody)
        {
            if (s == null || distanceToBody == null) return default;
            foreach (var slot in DriveOffOrder)
            {
                if (!s.TryGet(slot, out var p) || p.Actor == body || distanceToBody(p.Actor) >= AnimalRules.DriveOffRange) continue;
                return slot == PerceptionSlot.Panic
                    ? new PerceptionDecision(PerceptionStep.Flee, p.Actor)
                    : new PerceptionDecision(PerceptionStep.Attack, p.Actor, AttackReason.DriveAway);
            }
            return default;
        }
    }
}
