namespace Myrtana.Rules
{
    /// <summary>Weapon quality bits that change damage (G3 item quality; values as stored in items).</summary>
    [System.Flags]
    public enum WeaponQuality
    {
        None = 0,
        /// <summary>Diseases the victim (CanBeDiseased: weapon quality bit 2).</summary>
        Diseased = 2,
        Poisoned = 4,
        Burning = 8,
        Frozen = 16,
        Sharp = 32,
        Blessed = 64,
        Forged = 128,
        Worn = 256,
    }

    /// <summary>
    /// One hit as the presentation reports it ("this swing / arrow / spell touched that victim"), plus the situation the
    /// gates and the lethality rules need. A pure snapshot: the resolver never looks anything up.
    /// </summary>
    public sealed class HitContext
    {
        public Combatant Attacker { get; set; }
        public Combatant Victim { get; set; }

        public HitDelivery Delivery { get; set; }
        public DamageType DamageType { get; set; }
        /// <summary>Weapon (+ ammo) or spell damage.</summary>
        public int DamageAmount { get; set; }
        /// <summary>0..1: quality of the swing or the bow draw (1 for a full melee swing).</summary>
        public float HitMultiplier { get; set; } = 1f;
        public WeaponQuality Quality { get; set; }
        /// <summary>The touched bone is a head bone.</summary>
        public bool Headshot { get; set; }
        /// <summary>A projectile spawned by a spell (fireball …), as opposed to an instant spell effect.</summary>
        public bool SpellProjectile { get; set; }
        /// <summary>A power-cast spell projectile (path style "Missile").</summary>
        public bool PowerCast { get; set; }
        /// <summary>
        /// The damage comes from an area spell's object (MagicAreaDamageTick: the source is the Smn_ entity, not the
        /// caster): no attacker bonus, damage = the base after species (G3 "attacker without NPC data").
        /// </summary>
        public bool AreaSpell { get; set; }
        /// <summary>Mana spent on the spell (0 = scroll or NPC cast).</summary>
        public int ManaUsed { get; set; }
        /// <summary>The projectile's name contains "Fire" or "Explosive" (beats skeleton and ice golem missile immunity).</summary>
        public bool FireOrExplosiveProjectile { get; set; }
        /// <summary>Deity of the spell ("Innos", "Adanos", "Beliar"; the matching mage perk doubles the damage).</summary>
        public string SpellDeity { get; set; }

        // --- situation (filled by the simulation)
        /// <summary>The victim is in the attacker's field of view (melee needs it).</summary>
        public bool AttackerSeesVictim { get; set; } = true;
        /// <summary>The defender sees the attacker (blocking; the player's sneak attacks need the opposite).</summary>
        public bool VictimSeesAttacker { get; set; } = true;
        /// <summary>The victim is the attacker's current combat target.</summary>
        public bool VictimIsTarget { get; set; } = true;
        public Attitude AttackerToVictim { get; set; } = Attitude.Hostile;
        public Attitude VictimToAttacker { get; set; } = Attitude.Hostile;
        /// <summary>The victim is fighting someone other than the attacker and the attacker's party.</summary>
        public bool VictimFightsSomeoneElse { get; set; }
        /// <summary>
        /// The victim is the attacker's current target in the strict sense (the player's focus; no target = false). A
        /// neutral NPC the player has targeted takes his melee hits even while it fights someone else.
        /// </summary>
        public bool AttackerTargetsVictim { get; set; }
        /// <summary>
        /// The victim is a member of the party the attacker leads (the hero's companions, followers, controlled and
        /// summoned creatures; an NPC summoner's creatures). G3 OnHit / OnTouchDamage drop such hits before
        /// DoLogicalDamage (<see cref="HitGate.PartyMember"/>).
        /// </summary>
        public bool VictimInAttackersParty { get; set; }

        // --- G3 OnTouchDamage's bystander filter (NPC attackers; HitGate.Bystander)
        /// <summary>The attacker has a current target (G3 PSNpc.GetCurrentTarget is not None).</summary>
        public bool AttackerHasTarget { get; set; }
        /// <summary>The attacker's attitude toward its current target (meaningful with <see cref="AttackerHasTarget"/>).</summary>
        public Attitude AttackerToTarget { get; set; } = Attitude.Hostile;
        /// <summary>The attitude of the attacker's current target toward the attacker.</summary>
        public Attitude TargetToAttacker { get; set; } = Attitude.Hostile;
        /// <summary>The victim's own current target is the attacker (it fights back: the touch always counts).</summary>
        public bool VictimTargetsAttacker { get; set; }
        /// <summary>The victim is in AI mode Combat (an NPC fighting or fleeing; the hero always counts as in combat [interp]).</summary>
        public bool VictimInCombat { get; set; } = true;

        /// <summary>The victim fights in the arena (AB halves damage to NPCs there).</summary>
        public bool Arena { get; set; }
        /// <summary>The level of the player (NPC damage under AB, lethality protection).</summary>
        public int PlayerLevel { get; set; } = 1;
    }

    /// <summary>What a hit did. <see cref="ActorVitals.ApplyHit"/> applies it to the actors' state.</summary>
    public sealed class HitOutcome
    {
        public HitResult Result { get; internal set; }
        public HitGate Gate { get; internal set; }
        /// <summary>Hit points the victim loses.</summary>
        public int Damage { get; internal set; }
        /// <summary>Stamina the victim loses by blocking.</summary>
        public int StaminaCost { get; internal set; }
        /// <summary>Mana the victim loses by blocking a spell.</summary>
        public int ManaCost { get; internal set; }
        /// <summary>Power difference D (attack level − defence level), drives the reaction.</summary>
        public int PowerDelta { get; internal set; }
        public HitReaction Reaction { get; internal set; }
        /// <summary>The victim's hit points drop below 1.</summary>
        public bool Lethal { get; internal set; }
        public bool Killed => Reaction == HitReaction.Dead || Reaction == HitReaction.PiercedDead;
        public bool KnockedOut =>
            Reaction == HitReaction.LieKnockDown || Reaction == HitReaction.Unconscious || Reaction == HitReaction.PiercedKnockOut;
        /// <summary>The victim catches fire.</summary>
        public bool Burns { get; internal set; }
        /// <summary>Real seconds the victim is frozen (0 = not frozen).</summary>
        public float FreezeSeconds { get; internal set; }
        /// <summary>The victim is poisoned (CanBePoisoned: a poisoned weapon or missile; −5 HP a second, StatusEffects).</summary>
        public bool Poisons { get; internal set; }
        /// <summary>The victim catches a disease (CanBeDiseased: a diseased weapon or missile).</summary>
        public bool Diseases { get; internal set; }

        public static HitOutcome Ignored(HitGate gate) => new HitOutcome { Result = HitResult.Ignored, Gate = gate };

        public override string ToString() =>
            Result == HitResult.Ignored ? $"Ignored({Gate})" : $"{Result} dmg={Damage} sp={StaminaCost} mp={ManaCost} D={PowerDelta} {Reaction}";
    }
}
