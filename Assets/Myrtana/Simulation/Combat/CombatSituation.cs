using Myrtana.Core;
using Myrtana.Rules;

namespace Myrtana.Simulation
{
    /// <summary>
    /// What an NPC in a fight perceives this tick, gathered by the world (positions, navigation, the target's animation)
    /// and handed to <see cref="NpcCombatBrain.Tick"/>. Distances are in G3 units (cm). Plain data so the brain stays pure.
    /// </summary>
    public struct CombatSituation
    {
        /// <summary>Real seconds (the brain's timers) and game seconds (the G3 hit cooldowns).</summary>
        public double Now, GameNow;
        /// <summary>Own position on the ground plane (for the attack token).</summary>
        public float X, Z;
        public float Distance;
        public bool LineOfSight;
        /// <summary>Free space behind, behind-left and behind-right (G3 IsFreeLOSDirection).</summary>
        public bool FreeBack, FreeBackLeft, FreeBackRight;

        public bool TargetIsPlayer;
        public VitalState TargetState;
        public bool TargetKillable;
        public bool TargetHumanoid;
        public string TargetSpecies;
        public bool TargetFists;
        /// <summary>The target fights with two one-handed blades (CanParadeMoveOf: its power attack needs a shield).</summary>
        public bool TargetDualWield;
        /// <summary>The target holds a melee weapon (the CP Move needs the player or such a target).</summary>
        public bool TargetHoldsMelee;
        /// <summary>The target holds a bow, crossbow or spell (an NPC does not drink a potion then).</summary>
        public bool TargetHoldsRangedOrMagic;
        /// <summary>The weapon in the own hand (<see cref="WeaponSelection.HeldOf"/>); Unknown = not reported.</summary>
        public HeldWeapon Held;
        public FighterAction TargetAction;
        public MovePhase TargetPhase;
        public bool TargetSittingKnockedDown;
        /// <summary>The target fights self (not part of a group fight against it).</summary>
        public bool TargetEngagedWithSelf;
        public bool TargetInWater;
        /// <summary>No way leads into melee reach of the target (fix-142: the view's NavMesh chase, <see cref="CombatChaseRules"/>).</summary>
        public bool TargetUnreachable;
        public Attitude AttitudeToTarget;
        /// <summary>G3 IsUpset: the NPC holds a grudge against its target, the player (the attack reason line "IGetYouStill").</summary>
        public bool UpsetWithTarget;

        public int PlayerLevel;
        public bool PlayerSeesSelf;
        public bool PlayerSprinting;
        /// <summary>The player's attack pressure (CP swing counter, <see cref="CombatService.PlayerPressure"/>).</summary>
        public int PlayerAttackPressure;

        public int HitPointsPercent;
        public bool HasPotion;
        public bool Arena;
        public bool InPlayerParty;
        /// <summary>The fight is in a zone hostile to the player (gate guards and intruder fights may kill).</summary>
        public bool HostileZone;
        /// <summary>Fleeing: the flee point is reached.</summary>
        public bool ReachedFleePoint;
        /// <summary>Self has living summoned creatures (summoner bosses summon again only without).</summary>
        public bool HasSummons;
        /// <summary>Another enemy nearby to turn to when the target is down (default = none).</summary>
        public ActorId NextEnemy;
    }

    /// <summary>What the brain wants the body to do.</summary>
    public enum CombatIntent
    {
        /// <summary>Nothing (not fighting).</summary>
        None,
        /// <summary>Perform <see cref="CombatOrder.Move"/> against the target (attacks, parade, footwork, run at it).</summary>
        Fight,
        /// <summary>Stand and watch the fleeing target (WaitForFlee, or waiting on the shore).</summary>
        WaitForFlee,
        /// <summary>Run away from the target to the nearest flee point.</summary>
        Flee,
        /// <summary>Drink a healing potion (<see cref="FightTimingRules.PotionItem"/>) or cast a heal.</summary>
        Heal,
        /// <summary>Loot the downed target (half its gold), then return to the routine.</summary>
        Ransack,
        /// <summary>The fight is over: holster and continue the routine.</summary>
        Holster,
    }

    /// <summary>How the NPC fights at the moment (G3 weapon category).</summary>
    public enum WeaponMode { Melee, Ranged, Magic }

    /// <summary>What the body holds right now (reported each tick); the brain's <see cref="WeaponMode"/> is the wanted one.</summary>
    public enum HeldWeapon
    {
        /// <summary>Not reported (tests, bodies without a hand state): the brain trusts its own choice.</summary>
        Unknown,
        /// <summary>Empty hands, or a draw or sheathe in progress.</summary>
        None,
        Melee,
        Ranged,
        Magic,
    }

    /// <summary>The brain's order for this tick.</summary>
    public readonly struct CombatOrder
    {
        public CombatOrder(CombatIntent intent, CombatMove move, ActorId target, WeaponMode weapon, string shout = null)
            : this(intent, move, target, weapon, shout, null, false)
        {
        }

        public CombatOrder(CombatIntent intent, CombatMove move, ActorId target, WeaponMode weapon, string shout, string spell, bool powerCast)
        {
            Intent = intent;
            Move = move;
            Target = target;
            Weapon = weapon;
            Shout = shout;
            Spell = spell;
            PowerCast = powerCast;
        }

        public CombatIntent Intent { get; }
        public CombatMove Move { get; }
        public ActorId Target { get; }
        public WeaponMode Weapon { get; }
        /// <summary>A shared line to say now ("RunAway", "AttackEnd", "StandStill" …; null = none).</summary>
        public string Shout { get; }
        /// <summary>The spell item in the hand (magic; null otherwise).</summary>
        public string Spell { get; }
        /// <summary>A Cast move is a power cast.</summary>
        public bool PowerCast { get; }

        public override string ToString() =>
            $"{Intent} {Move} → {Target} ({Weapon}{(Spell != null ? " " + Spell + (PowerCast ? " power" : "") : "")}){(Shout != null ? " \"" + Shout + "\"" : "")}";
    }
}
