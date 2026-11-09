namespace Myrtana.Rules
{
    /// <summary>What a hit does to the victim; selects the protection value and the species table (G3 gEDamageType).</summary>
    public enum DamageType { None, Impact, Blade, Missile, Fire, Ice, Lightning }

    /// <summary>How a hit arrives: a weapon swing, a projectile (arrow, bolt) or a spell.</summary>
    public enum HitDelivery { Melee, Missile, Spell }

    /// <summary>The combat move an actor is performing (the combat subset of G3 gEAction).</summary>
    public enum CombatAction
    {
        None,
        Attack,
        PowerAttack,
        SprintAttack,
        QuickAttack,
        QuickAttackLeft,
        QuickAttackRight,
        SimpleWhirl,
        WhirlAttack,
        PierceAttack,
        HackAttack,
        FinishingAttack,
        GetUpAttack,
        Parade,
        GetUpParade,
        Jump,
        JumpBack,
    }

    /// <summary>What an actor fights with in the right hand (G3 weapon use types).</summary>
    public enum WeaponClass { Fist, OneHanded, TwoHanded, Axe, Halberd, Staff, Bow, Crossbow, Tool }

    /// <summary>The worn body armour, for the armour perks (robes count for Perk_LightArmor, everything else for Perk_HeavyArmor).</summary>
    public enum ArmorKind { None, Robe, Armor }

    /// <summary>How one actor regards another (G3 gEAttitude; Predator = an animal toward its prey). The attitude service comes with the crime slice.</summary>
    public enum Attitude { Friendly, Neutral, Angry, Hostile, Panic, Predator }

    /// <summary>Why an NPC fights (G3 gEAttackReason, same order). Reasons only escalate during one fight.</summary>
    public enum AttackReason
    {
        None, LeaveZone, Livestock, StopThief, Theft, UseInteractObject, ReactToWeapon, Angry, StopFight, ReactToDamage,
        GateGuard, Intruder, Enemy, Murder, Prey, DriveAway, PlayerCommand, Duel, Arena, Revolution, Kill,
    }

    /// <summary>What the victim's body does after a hit (the G3 reaction states, ZS_*).</summary>
    public enum HitReaction
    {
        None,
        /// <summary>Power difference below 1: a minimal flinch (PipiStumble).</summary>
        Flinch,
        QuickStumble,
        Stumble,
        /// <summary>Knocked into a sitting position; gets up with GetUpAttack or GetUpParade.</summary>
        SitKnockDown,
        PierceStumble,
        ParadeStumble,
        HeavyParadeStumble,
        /// <summary>Knocked out, lying (non-deadly lethal hit with a small power difference).</summary>
        LieKnockDown,
        /// <summary>Knocked out by a heavy hit (power difference above 2).</summary>
        Unconscious,
        /// <summary>Knocked out by a pierce attack.</summary>
        PiercedKnockOut,
        /// <summary>Killed (ZS_RagDollDead).</summary>
        Dead,
        /// <summary>Killed by an assassin's pierce attack.</summary>
        PiercedDead,
    }

    /// <summary>Why a hit did no damage at all (DoLogicalDamage gates).</summary>
    public enum HitGate
    {
        None,
        /// <summary>
        /// The victim belongs to the attacker's own party: a melee swing or an area spell never hurts it, a projectile
        /// or spell missile only when the member is the attacker's current target (G3 OnHit, OnTouchDamage,
        /// MagicAreaDamageTick).
        /// </summary>
        PartyMember,
        /// <summary>
        /// An NPC's weapon or missile touched a bystander that it is not fighting and likes better than its target (a
        /// companion's blade or arrow on the hero or on another companion): G3 OnTouchDamage drops the touch before
        /// DoLogicalDamage (<see cref="CombatResolver.BystanderSpared"/>).
        /// </summary>
        Bystander,
        GodMode,
        Ghost,
        /// <summary>The victim is dead, down, talking or fighting in the arena's waiting mode.</summary>
        VictimUnavailable,
        VictimKnockedDown,
        VictimSwimming,
        VictimInMenu,
        /// <summary>Pierce and hack attacks only hurt the attacker's own target.</summary>
        NotTheTarget,
        /// <summary>An NPC's projectile or spell hit someone it regards as friendly or neutral.</summary>
        FriendlyFire,
        /// <summary>Melee outside the attacker's field of view.</summary>
        OutOfView,
        /// <summary>The player hit a friendly or neutral NPC that is busy fighting someone else.</summary>
        ProtectedBystander,
        /// <summary>The species is immune to the damage type.</summary>
        Immune,
    }

    /// <summary>The result class of a hit.</summary>
    public enum HitResult { Ignored, Blocked, Hit }

    /// <summary>The life state of an actor as combat sees it.</summary>
    public enum VitalState { Alive, Down, Dead }

    public static class CombatActions
    {
        public static bool IsQuick(this CombatAction a) =>
            a == CombatAction.QuickAttack || a == CombatAction.QuickAttackLeft || a == CombatAction.QuickAttackRight;

        /// <summary>Power attacks and the CP sprint attack, which the rules treat as a power attack.</summary>
        public static bool IsPower(this CombatAction a) => a == CombatAction.PowerAttack || a == CombatAction.SprintAttack;

        public static bool IsAttack(this CombatAction a) =>
            a != CombatAction.None && a != CombatAction.Parade && a != CombatAction.GetUpParade &&
            a != CombatAction.Jump && a != CombatAction.JumpBack;
    }

    public static class AttackReasons
    {
        /// <summary>
        /// Whether a fight for this reason may end with a finishing move (G3 HasAttackReasonToKill). Gate guards and
        /// intruder fights kill only when <paramref name="hostileZone"/> (the player is not welcome in the zone).
        /// </summary>
        public static bool IsToKill(this AttackReason reason, bool hostileZone = false) =>
            reason == AttackReason.Enemy || reason == AttackReason.Murder || reason == AttackReason.Revolution ||
            reason == AttackReason.Kill ||
            (hostileZone && (reason == AttackReason.GateGuard || reason == AttackReason.Intruder));

        /// <summary>Bystanders watch these fights instead of joining (HasAttackReasonToWatch).</summary>
        public static bool IsToWatch(this AttackReason reason) =>
            reason == AttackReason.Duel || reason == AttackReason.Arena || reason == AttackReason.Kill;
    }
}
