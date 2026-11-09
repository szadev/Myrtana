using System;
using Myrtana.Core;

namespace Myrtana.Rules
{
    /// <summary>
    /// Turns one reported hit into its outcome (gameplay-logic.md §3.1–3.5, CP 1.75 with the AB/AAI options). Pure: it
    /// reads the snapshot and changes nothing; <see cref="ActorVitals.ApplyHit"/> applies the outcome. The stages run in
    /// the CP order: gates → species → base damage → special player hits → block → protection → action modifier →
    /// post modifiers → lethality and reaction.
    /// </summary>
    public sealed class CombatResolver
    {
        const string AssassinPerk = "Perk_Assassin", KnockOutPerk = "Perk_KnockOut";
        const string StaffMagicPerk = "Perk_StaffMagic", OrcSlayerPerk = "Perk_OrcSlayer", PaladinPerk = "Perk_Paladin";
        const string OrcHunterPerk = "Perk_OrcHunter", Hunter1Perk = "Perk_Hunter_1", Hunter2Perk = "Perk_Hunter_2";
        const int SkillPerkBonus = 25;
        const int QualityBonus = 10;

        readonly CombatRules _rules;
        readonly RuleOptions _options;
        readonly IRandom _random;

        public CombatResolver(CombatRules rules, RuleOptions options, IRandom random, CombatPortOptions port = null)
        {
            _rules = rules ?? throw new ArgumentNullException(nameof(rules));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _random = random ?? throw new ArgumentNullException(nameof(random));
            Port = port ?? new CombatPortOptions();
        }

        /// <summary>The port's live combat options (menu rows; defaults = CP 1.75).</summary>
        public CombatPortOptions Port { get; }

        public HitOutcome Resolve(HitContext hit)
        {
            var gate = Gate(hit);
            if (gate != HitGate.None) return HitOutcome.Ignored(gate);
            int species = SpeciesMultiplier(hit);
            if (species == 0) return HitOutcome.Ignored(HitGate.Immune);

            var attacker = hit.Attacker;
            var victim = hit.Victim;
            int baseDamage = (int)(hit.DamageAmount * hit.HitMultiplier) * species;
            if (hit.Headshot) baseDamage *= 2;
            int damage = hit.AreaSpell ? baseDamage : IsPlayerBody(attacker) ? PlayerDamage(hit, baseDamage) : NpcDamage(hit, baseDamage);
            if (hit.SpellProjectile && hit.PowerCast) damage *= 2;

            var outcome = new HitOutcome { Result = HitResult.Hit, PowerDelta = CombatStats.PowerDelta(hit) };
            if (TrySpecialPlayerHit(hit, damage, outcome)) return outcome;
            if (TryBlock(hit, damage, outcome)) return outcome;

            int final = Protect(hit, damage);
            final = ActionModifier(hit, final);
            final = PostModifiers(hit, final);
            outcome.Damage = final;
            outcome.Lethal = victim.HitPoints - final < 1;
            outcome.Reaction = outcome.Lethal ? LethalReaction(hit, outcome.PowerDelta) : SurvivorReaction(hit, outcome.PowerDelta);
            if (!outcome.Lethal) ApplyStatusEffects(hit, final, outcome);
            return outcome;
        }

        // ------------------------------------------------------------------------------------------------ §3.1 gates

        /// <summary>The DoLogicalDamage gate a hit would stop at (None = it goes on to the damage); public for dev diagnostics.</summary>
        public static HitGate Gate(HitContext hit) => Gate(hit, downedVictim: false);

        /// <param name="downedVictim">fix-241: the gates of a ranged finish on a downed victim (<see cref="FinishesDowned"/>):
        /// its Down state and lying pose do not stop the hit, every other gate does.</param>
        static HitGate Gate(HitContext hit, bool downedVictim)
        {
            var a = hit.Attacker;
            var v = hit.Victim;
            if (PartyMemberSpared(hit)) return HitGate.PartyMember;
            if (BystanderSpared(hit)) return HitGate.Bystander;
            if (v.IsPlayer && v.GodMode) return HitGate.GodMode;
            if (a.Ghost || v.Ghost) return HitGate.Ghost;
            if (downedVictim ? v.State != VitalState.Down || v.Talking : v.State != VitalState.Alive || v.Talking) return HitGate.VictimUnavailable;
            if (v.KnockedDown && !downedVictim) return HitGate.VictimKnockedDown;
            if (v.Swimming) return HitGate.VictimSwimming;
            if (v.IsPlayer && v.InMenu) return HitGate.VictimInMenu;
            bool melee = hit.Delivery == HitDelivery.Melee;
            if (melee && (a.Action == CombatAction.PierceAttack || a.Action == CombatAction.HackAttack) && !hit.VictimIsTarget)
                return HitGate.NotTheTarget;
            if (!a.IsPlayer && !hit.VictimIsTarget)
            {
                bool friendly = hit.AttackerToVictim == Attitude.Friendly;
                bool neutral = hit.AttackerToVictim == Attitude.Neutral;
                // DoLogicalDamage: a projectile (an arrow, a bolt, a spell missile) spares friendly and neutral non-targets,
                // a blade or an instant spell only friendly ones.
                bool projectile = hit.Delivery == HitDelivery.Missile || (hit.SpellProjectile && !hit.AreaSpell);
                if (friendly || (neutral && projectile)) return HitGate.FriendlyFire;
            }
            if (melee && !hit.AttackerSeesVictim) return HitGate.OutOfView;
            if (a.IsPlayer && hit.VictimFightsSomeoneElse && hit.Delivery != HitDelivery.Missile)
            {
                bool friendly = hit.VictimToAttacker == Attitude.Friendly;
                bool neutral = hit.VictimToAttacker == Attitude.Neutral;
                // G3 DoLogicalDamage: a neutral NPC the player has targeted takes his melee hits (fix-25: a neutral
                // Okara boar fighting the guide Fraser took 28 touching sword hits without damage).
                if (friendly || (neutral && melee && !hit.AttackerTargetsVictim)) return HitGate.ProtectedBystander;
            }
            return HitGate.None;
        }

        /// <summary>
        /// G3 runs these checks before DoLogicalDamage [CP 1.75]: OnHit (0x100ad620) passes a melee touch on only when
        /// the victim is not in the attacker's party member list (combos, whirls and every other attack action alike);
        /// OnTouchDamage (0x100b47b0) drops a projectile's or spell's touch on a member of its owner's party unless that
        /// member is the owner's current target; MagicAreaDamageTick skips the caster's party (<c>AreaSpellRun</c>).
        /// A dropped touch does nothing at all: no damage, no stumble, no fight sound, no reaction.
        /// </summary>
        static bool PartyMemberSpared(HitContext hit)
        {
            if (!hit.VictimInAttackersParty) return false;
            return hit.Delivery == HitDelivery.Melee || hit.AreaSpell || !hit.AttackerTargetsVictim;
        }

        /// <summary>
        /// G3 OnTouchDamage (CP 0x100b47b0), after the party check: an NPC's weapon or projectile touch on a bystander
        /// (not the attacker's current target, not fighting the attacker) is dropped when the attacker prefers the
        /// bystander to its target:
        /// <list type="bullet">
        /// <item>bystander in AI mode Combat: attitude(attacker → bystander) is set, better than Hostile and better than
        /// attitude(attacker → target), or both are Neutral;</item>
        /// <item>bystander not in combat: the attacker has a target and attitude(bystander → attacker) is better than Hostile
        /// and better than attitude(target → attacker).</item>
        /// </list>
        /// A companion fighting an orc therefore never hurts the hero or another companion with a stray blow or arrow; a
        /// target-less swing (the foe just died) falls through to DoLogicalDamage, whose friendly-fire gate spares friends.
        /// Area spells (MagicAreaDamageTick) do not go through OnTouchDamage. The player's own touches are not filtered
        /// here (his rules: <see cref="HitGate.PartyMember"/>, <see cref="HitGate.ProtectedBystander"/>).
        /// </summary>
        public static bool BystanderSpared(HitContext hit)
        {
            if (hit.Attacker.IsPlayer || hit.AreaSpell || hit.VictimIsTarget || hit.VictimTargetsAttacker) return false;
            if (hit.VictimInCombat)
            {
                if (!hit.AttackerHasTarget) return false; // G3 attitude toward None is 0: nothing is better
                int toTarget = Rank(hit.AttackerToTarget), toVictim = Rank(hit.AttackerToVictim);
                return (toVictim < toTarget && toVictim < Rank(Attitude.Hostile)) ||
                       (toVictim == toTarget && hit.AttackerToVictim == Attitude.Neutral);
            }
            if (!hit.AttackerHasTarget) return false;
            int victimTo = Rank(hit.VictimToAttacker);
            return victimTo < Rank(hit.TargetToAttacker) && victimTo < Rank(Attitude.Hostile);
        }

        /// <summary>The G3 gEAttitude value (Friendly 1 … Hostile 4, Predator 5, Panic 6): lower is friendlier.</summary>
        static int Rank(Attitude a)
        {
            switch (a)
            {
                case Attitude.Friendly: return 1;
                case Attitude.Neutral: return 2;
                case Attitude.Angry: return 3;
                case Attitude.Hostile: return 4;
                case Attitude.Predator: return 5;
                default: return 6; // Panic
            }
        }

        int SpeciesMultiplier(HitContext hit)
        {
            if (hit.DamageType == DamageType.None) return 0;
            if (hit.DamageType == DamageType.Ice && hit.Victim.Frozen) return 0;
            return _rules.SpeciesMultiplier(hit.Victim.Species, hit.DamageType, hit.FireOrExplosiveProjectile);
        }

        // ---------------------------------------------------------------------------------------- §3.2 base damage

        static bool IsPlayerBody(Combatant c) => c.IsPlayer && !c.IsTransformed;

        /// <summary>The player's hits are weapon-based: base + quality + skill bonus + attribute bonus.</summary>
        static int PlayerDamage(HitContext hit, int baseDamage)
        {
            var a = hit.Attacker;
            int attributeBonus;
            switch (hit.Delivery)
            {
                case HitDelivery.Spell:
                    if (hit.SpellProjectile) baseDamage = Math.Min(baseDamage, 100) * 2;
                    attributeBonus = hit.ManaUsed == 0 ? baseDamage * 2 : baseDamage * a.Intelligence / 100;
                    break;
                case HitDelivery.Missile:
                    attributeBonus = a.Dexterity / 2;
                    break;
                default:
                    attributeBonus = a.Strength / 2;
                    break;
            }
            int quality = QualityBonusOf(hit.Quality, baseDamage);
            return baseDamage + quality + SkillBonus(hit, baseDamage + quality) + Math.Max(0, attributeBonus);
        }

        static int QualityBonusOf(WeaponQuality q, int baseDamage)
        {
            int bonus = 0;
            if ((q & WeaponQuality.Worn) != 0) bonus -= baseDamage / 2;
            if ((q & WeaponQuality.Sharp) != 0) bonus += QualityBonus;
            if ((q & WeaponQuality.Blessed) != 0) bonus += QualityBonus;
            if ((q & WeaponQuality.Forged) != 0) bonus += QualityBonus;
            return bonus;
        }

        /// <summary>GetPlayerSkillDamageBonus: perk bonuses of the player, added up.</summary>
        static int SkillBonus(HitContext hit, int passed)
        {
            var a = hit.Attacker;
            var v = hit.Victim;
            int bonus = 0;
            if (hit.Delivery == HitDelivery.Spell)
            {
                string perk = hit.SpellDeity == "Innos" ? "Perk_Firemage" : hit.SpellDeity == "Adanos" ? "Perk_Watermage" :
                              hit.SpellDeity == "Beliar" ? "Perk_Darkmage" : null;
                return a.Has(perk) ? passed : 0;
            }
            var (level2, level3) = CombatStats.SkillPerks(a);
            if (a.Has(level2)) bonus += SkillPerkBonus;
            if (a.Has(level3)) bonus += SkillPerkBonus;
            if (hit.Delivery == HitDelivery.Missile)
            {
                if (a.Weapon != WeaponClass.Bow) return bonus;
                if (v.GameAnimal && a.Has(Hunter1Perk)) bonus += passed;
                if (v.Animal && !v.GameAnimal && a.Has(Hunter2Perk)) bonus += passed;
                if (v.IsOrc && a.Has(OrcHunterPerk)) bonus += SkillPerkBonus;
                return bonus;
            }
            if (v.IsOrc && a.Has(OrcSlayerPerk)) bonus += SkillPerkBonus;
            if (v.Evil && a.Has(PaladinPerk)) bonus += SkillPerkBonus;
            return bonus;
        }

        /// <summary>NPC (and transformed player) hits use the attacker's strength, not the weapon.</summary>
        int NpcDamage(HitContext hit, int baseDamage)
        {
            var a = hit.Attacker;
            var v = hit.Victim;
            int damage = a.Strength;
            if (_options.AlternativeBalancing && hit.PlayerLevel >= 2 && a.Humanoid && hit.Delivery == HitDelivery.Melee)
            {
                bool fist = a.Weapon == WeaponClass.Fist;
                if (!IsPlayerBody(v)) damage = fist ? a.Strength / 2 : a.Strength;
                else if (fist) damage = (int)(0.75 * a.Strength);
                else if (a.IsOrc) damage = (int)(baseDamage / 7.0 + 0.75 * a.Strength);
                else damage = (int)(baseDamage / 9.0 + 0.9 * a.Strength);
            }
            if (v.Humanoid && !a.Humanoid && !a.IsPlayer && !v.IsPlayer) damage /= a.Summoned ? 2 : 10;
            if (hit.SpellProjectile) damage *= 2;
            return damage;
        }

        // --------------------------------------------------------------------------------------- §3.5 special hits

        /// <summary>The player's blunt arrows, assassin pierce and knock-out power attack.</summary>
        bool TrySpecialPlayerHit(HitContext hit, int damage, HitOutcome outcome)
        {
            var a = hit.Attacker;
            var v = hit.Victim;
            if (!IsPlayerBody(a)) return false;
            if (hit.Delivery == HitDelivery.Missile && hit.DamageType == DamageType.Impact && v.Humanoid)
            {
                int blunt = (int)(hit.DamageAmount * hit.HitMultiplier) + SkillBonus(hit, (int)(hit.DamageAmount * hit.HitMultiplier));
                bool lethal = v.HitPoints - blunt < 1;
                if (!lethal && hit.HitMultiplier < 0.7f) return false;
                outcome.Damage = blunt;
                outcome.Lethal = lethal;
                outcome.Reaction = lethal ? HitReaction.Unconscious : HitReaction.SitKnockDown;
                return true;
            }
            if (!v.InRoutine || hit.VictimSeesAttacker || hit.Delivery != HitDelivery.Melee) return false;
            if (a.Action == CombatAction.PierceAttack && a.Has(AssassinPerk))
            {
                bool killable = CanBeKilled(v, hit.PlayerLevel);
                outcome.Damage = Math.Max(0, v.HitPoints);
                outcome.Lethal = true;
                outcome.Reaction = !killable ? HitReaction.LieKnockDown : v.Humanoid ? HitReaction.PiercedDead : HitReaction.Dead;
                return true;
            }
            if (a.Action.IsPower() && hit.DamageType == DamageType.Impact && a.Has(KnockOutPerk))
            {
                // AssessHit (vanilla and CP 1.75) zeroes the DamageReceiver amount on entry and this branch sets the task
                // ZS_LieKnockDown without setting it again: the knock-out costs no HP (fix-28; was: the normal damage [interp]).
                outcome.Damage = 0;
                outcome.Lethal = false;
                outcome.Reaction = HitReaction.LieKnockDown;
                return true;
            }
            return false;
        }

        // -------------------------------------------------------------------------------------------- §3.4 blocking

        bool TryBlock(HitContext hit, int damage, HitOutcome outcome)
        {
            var v = hit.Victim;
            int cost, pool;
            switch (hit.Delivery)
            {
                case HitDelivery.Melee:
                    if (!CanBlockMelee(hit)) return false;
                    cost = damage / 2;
                    if (v.ShieldAndOneHanded) cost = v.IsPlayer && v.Has(CombatStats.ShieldPerk) ? cost / 2 : cost - cost / 4;
                    pool = v.Stamina;
                    break;
                case HitDelivery.Missile:
                    if (!v.InParade || !v.ShieldAndOneHanded || !hit.VictimSeesAttacker) return false;
                    cost = v.IsPlayer && v.Has(CombatStats.ShieldPerk) ? damage / 2 : damage;
                    pool = v.Stamina;
                    break;
                default:
                    if (!CanBlockMagic(hit)) return false;
                    cost = damage;
                    if (_options.AlternativeBalancing)
                        cost = (int)(damage * Math.Max(0.0, Math.Min(1.0, 1.0 - (v.Intelligence - 200) / 500.0)));
                    pool = v.Mana;
                    break;
            }

            int paid = Math.Min(cost, Math.Max(0, pool));
            int shortfall = cost - paid;
            bool shortfallHurts = !(hit.Delivery == HitDelivery.Melee && _options.ExtendedAI);
            if (hit.Delivery == HitDelivery.Spell) outcome.ManaCost = paid;
            else outcome.StaminaCost = paid;
            outcome.Result = HitResult.Blocked;
            outcome.Damage = shortfallHurts ? shortfall : 0;
            outcome.Lethal = v.HitPoints - outcome.Damage < 1;
            if (outcome.Lethal) outcome.Reaction = LethalReaction(hit, outcome.PowerDelta);
            else if (hit.Delivery == HitDelivery.Missile) outcome.Reaction = outcome.PowerDelta > 0 ? HitReaction.ParadeStumble : HitReaction.None;
            else outcome.Reaction = outcome.PowerDelta > 2 && hit.Delivery == HitDelivery.Melee ? HitReaction.HeavyParadeStumble : HitReaction.ParadeStumble;
            return true;
        }

        bool CanBlockMelee(HitContext hit)
        {
            var a = hit.Attacker;
            var v = hit.Victim;
            if (!v.InParade || !hit.VictimSeesAttacker) return false;
            // CP reads the defender's own action here (so a defender in a hack cannot parry), not the attacker's.
            if (v.Action == CombatAction.HackAttack) return false;
            if (_options.ExtendedAI)
            {
                if (v.Action == CombatAction.PierceAttack || v.Action == CombatAction.WhirlAttack || v.Action == CombatAction.FinishingAttack)
                    return false;
                if (a.Action == CombatAction.SprintAttack) return false;
                if (a.DualWield && a.Action == CombatAction.PowerAttack && !v.ShieldAndOneHanded) return false;
            }
            // CanParadeMoveOf: an attacker in fist mode (fists, claws, bites: every animal and monster holds a PhysicalFist)
            // is parried only by a humanoid in fist mode, and only if it is a humanoid itself; so creature attacks cannot
            // be blocked at all, except the Minecrawler's (IsInFistMode is false for it) [fix-81].
            bool attackerFist = IsInFistMode(a.Weapon, a.Species), defenderFist = IsInFistMode(v.Weapon, v.Species);
            if (attackerFist || defenderFist)
            {
                if (attackerFist && defenderFist && a.Humanoid && v.Humanoid) return true;
                // fix-109 [owner] option (UP 1.04): a raised shield blocks animals and fist fighters (ShieldCreatureParade)
                return Port.ShieldBlocksCreatures && attackerFist && !defenderFist &&
                       ShieldCreatureParade.BlocksFistAttack(a.Species, v.ShieldAndOneHanded, v.IsPlayer, v.Has);
            }
            if (Port.ShieldBlocksCreatures && ShieldCreatureParade.NeedsShield(a.Species) && !v.ShieldAndOneHanded) return false;
            if ((a.Action == CombatAction.PierceAttack || a.Action == CombatAction.HackAttack) && !v.ShieldAndOneHanded) return false;
            return true;
        }

        /// <summary>
        /// G3 IsInFistMode (Script_Game 0x1000db00): the right hand's weapon is used as Fist or PhysicalFist (bare hands, an
        /// animal's claws and teeth); never for a Minecrawler (species 8).
        /// </summary>
        public static bool IsInFistMode(WeaponClass weapon, string species) =>
            weapon == WeaponClass.Fist && !string.Equals(species, "Minecrawler", StringComparison.Ordinal);

        /// <summary>
        /// G3 CanParadeMagic (CanParade.cpp): HasStaffMagicParade (staff and spell in the hands; the hero needs Perk_StaffMagic,
        /// an NPC the mage class), in the parade, the caster in view (<see cref="StaffParade"/>).
        /// </summary>
        static bool CanBlockMagic(HitContext hit)
        {
            var v = hit.Victim;
            return v.InParade && hit.VictimSeesAttacker &&
                   StaffParade.BlocksMagic(v.CastStance && v.Weapon == WeaponClass.Staff, v.IsPlayer, v.Has, v.Mage);
        }

        // ------------------------------------------------------------------------------------ protection and modifiers

        int Protect(HitContext hit, int damage)
        {
            var v = hit.Victim;
            var type = hit.DamageType == DamageType.None ? DamageType.Impact : hit.DamageType;
            int protection = CombatStats.WithArmorPerks(v.Protection[type], v, type, _options);
            if (_options.AlternativeBalancing && IsPlayerBody(v) && hit.Delivery == HitDelivery.Melee)
            {
                int flat = damage - protection;
                if (flat >= 1) return flat;
                bool heavy = damage > 39 || hit.Attacker.Action == CombatAction.HackAttack || hit.Attacker.Action.IsPower();
                return heavy ? damage * (1 + _random.Next(2)) / 100 : 0;
            }
            return Math.Max(0, damage - damage * protection / 100);
        }

        static int ActionModifier(HitContext hit, int damage)
        {
            if (hit.Delivery != HitDelivery.Melee) return damage;
            var a = hit.Attacker;
            if (a.Action.IsPower()) return !a.DualWield || a.SecondPose ? damage * 2 : damage;
            if (a.Action == CombatAction.HackAttack) return damage * 2;
            if (a.Action.IsQuick()) return damage / 2;
            return damage;
        }

        int PostModifiers(HitContext hit, int damage)
        {
            var v = hit.Victim;
            if (v.IsPlayer) return damage;
            if (!CanBeKilled(v, hit.PlayerLevel))
                damage = v.HitPoints - Math.Min(damage, _rules.UnkillableDamageCap) < 1 ? 0 : Math.Min(damage, _rules.UnkillableDamageCap);
            if (_options.AlternativeBalancing)
            {
                if (hit.Arena) damage /= 2;
                if (hit.Delivery == HitDelivery.Missile && _options.Difficulty == Difficulty.Hard) damage = (int)(damage * 1.2);
            }
            return damage;
        }

        // ------------------------------------------------------------------------------------------ §3.5 reactions

        /// <summary>
        /// CanBeKilled: not Immortal, and on Easy/Medium the player and friend-type NPCs are protected while the player
        /// is below level 3 (not while transformed).
        /// </summary>
        public bool CanBeKilled(Combatant c, int playerLevel)
        {
            if (c.Immortal) return false;
            bool protectedLevel = _options.Difficulty != Difficulty.Hard && playerLevel < 3;
            return !(protectedLevel && (IsPlayerBody(c) || c.FriendType));
        }

        /// <summary>
        /// IsDeadlyDamage: whether a lethal hit kills or only knocks out. Not deadly: a victim that cannot be killed
        /// (<see cref="CanBeKilled"/>), a blunt missile on a humanoid, and melee between two humanoids that are not hostile to
        /// each other (unless a FinishingAttack). A boss (G3 IsBoss) and Xardas are always deadly when killable: they skip the
        /// two knock-out rules (CP @1002db80 and 1.12 @1003c6b0 return 1 right after IsBoss / the name check; fix-41 — was
        /// read inverted, so an orc boss of a liberation fight was only knocked out).
        /// </summary>
        public bool IsDeadly(HitContext hit)
        {
            var a = hit.Attacker;
            var v = hit.Victim;
            if (!CanBeKilled(v, hit.PlayerLevel)) return false;
            if (v.Boss || IsXardas(v)) return true;
            if (hit.Delivery == HitDelivery.Missile && hit.DamageType == DamageType.Impact && v.Humanoid) return false;
            if (hit.Delivery == HitDelivery.Melee && a.Humanoid && v.Humanoid && a.Action != CombatAction.FinishingAttack &&
                hit.VictimToAttacker != Attitude.Hostile && hit.VictimToAttacker != Attitude.Panic && hit.AttackerToVictim != Attitude.Hostile)
                return false;
            return true;
        }

        /// <summary>
        /// fix-241 [owner] port option "Arrows finish downed foes" (<see cref="CombatPortOptions.RangedFinishesDowned"/>; off = G3:
        /// DoLogicalDamage gives a victim in AI mode Down no damage, so arrows and spells do nothing to a knocked-out body).
        /// With the option on, a ranged hit of the hero or his party — an arrow, a bolt or a damaging spell projectile, not an
        /// area spell — on a downed NPC finishes it like the melee finishing move (<see cref="CombatService.FinishByRanged"/>),
        /// when: the hit passes every other gate (party, bystander, ghost, swimming, friendly fire …), the victim can be killed
        /// (<see cref="CanBeKilled"/>: not Immortal / story-protected, not a friend-type NPC on Easy/Medium below level 3), and
        /// the missile is not blunt on a humanoid: G3's blunt arrow (damage type Impact) is non-lethal to humanoids by design
        /// (IsDeadlyDamage, the player's blunt-arrow branch), so it never finishes one either.
        /// </summary>
        public bool FinishesDowned(HitContext hit, bool byPlayerParty)
        {
            if (!Port.RangedFinishesDowned || !byPlayerParty || hit?.Victim == null || hit.Attacker == null) return false;
            var v = hit.Victim;
            if (v.IsPlayer || v.State != VitalState.Down) return false;
            bool ranged = hit.Delivery == HitDelivery.Missile || hit.Delivery == HitDelivery.Spell && hit.SpellProjectile && !hit.AreaSpell;
            if (!ranged || hit.DamageAmount <= 0) return false;
            if (IsBluntOnHumanoid(hit)) return false;
            if (Gate(hit, downedVictim: true) != HitGate.None) return false;
            if (SpeciesMultiplier(hit) == 0) return false; // immune (a skeleton to plain arrows)
            return CanBeKilled(v, hit.PlayerLevel);
        }

        /// <summary>A missile of damage type Impact (the blunt arrow) on a humanoid: knocks out, never kills (G3 IsDeadlyDamage).</summary>
        public static bool IsBluntOnHumanoid(HitContext hit) =>
            hit.Delivery == HitDelivery.Missile && hit.DamageType == DamageType.Impact && hit.Victim.Humanoid;

        /// <summary>G3 IsDeadlyDamage compares the entity name with "Xardas".</summary>
        public const string XardasName = "Xardas";

        static bool IsXardas(Combatant c) => string.Equals(c.Id.Name, XardasName, StringComparison.Ordinal);

        HitReaction LethalReaction(HitContext hit, int delta)
        {
            if (IsDeadly(hit)) return HitReaction.Dead;
            if (hit.Attacker.Action == CombatAction.PierceAttack && hit.Victim.Humanoid && hit.Delivery == HitDelivery.Melee)
                return HitReaction.PiercedKnockOut;
            return delta > 2 ? HitReaction.Unconscious : HitReaction.LieKnockDown;
        }

        static HitReaction SurvivorReaction(HitContext hit, int delta)
        {
            var v = hit.Victim;
            if (v.Action == CombatAction.SprintAttack) return HitReaction.None;
            if (delta < 1) return HitReaction.Flinch;
            if (hit.Delivery == HitDelivery.Melee && hit.Attacker.Action == CombatAction.PierceAttack && v.Humanoid)
                return HitReaction.PierceStumble;
            if (!v.Humanoid) return HitReaction.Stumble;
            // G3 AssessHit: D > 3 sits only a victim with the melee weapon config; otherwise D < 2 QuickStumble, else Stumble.
            if (delta > 3 && v.MeleeConfig) return HitReaction.SitKnockDown;
            return delta < 2 ? HitReaction.QuickStumble : HitReaction.Stumble;
        }

        /// <summary>
        /// AssessHit's status part for a survived hit [CP 1.75, fix-127], in G3's order: CanBurn; then a victim in a
        /// sprint attack or a power difference below 1 (PipiStumble) gets nothing more; CanBePoisoned; a pierce attack on a
        /// humanoid (ZS_PierceStumble) stops there; CanBeDiseased; CanFreeze.
        /// </summary>
        void ApplyStatusEffects(HitContext hit, int final, HitOutcome outcome)
        {
            var v = hit.Victim;
            int remaining = v.HitPoints - final;
            bool melee = hit.Delivery == HitDelivery.Melee;
            if (CanBurn(hit, remaining) && !Contains(_rules.BurnImmuneSpecies, v.Species) && !(v.IsPlayer && v.Has(CombatStats.ResistHeatPerk)))
                outcome.Burns = true;
            if (outcome.Reaction == HitReaction.None || outcome.Reaction == HitReaction.Flinch) return;
            outcome.Poisons = CanCatch(hit, WeaponQuality.Poisoned, CombatStats.ImmuneToPoisonPerk);
            if (outcome.Reaction == HitReaction.PierceStumble) return;
            outcome.Diseases = CanCatch(hit, WeaponQuality.Diseased, CombatStats.ImmuneToDiseasePerk);
            bool freezes = hit.DamageType == DamageType.Ice || (melee && (hit.Quality & WeaponQuality.Frozen) != 0 && remaining < 50);
            if (freezes && !Contains(_rules.FreezeImmuneSpecies, v.Species) && !(v.IsPlayer && v.Has(CombatStats.ResistColdPerk)))
                outcome.FreezeSeconds = Math.Max(5f, final / 20f);
        }

        /// <summary>
        /// G3 CanBurn's damage source [0x1002d530]: fire damage (spells); a melee weapon with the burning quality (the
        /// flame swords, the fist of fire varans and fire golems, <see cref="SpeciesWeaponry"/>) leaving under 100 HP; a
        /// missile with the burning quality (fire and explosive arrows) shot at a draw of at least 0.5 (fix-127: the
        /// burning arrows set no one on fire before).
        /// </summary>
        static bool CanBurn(HitContext hit, int remaining)
        {
            if (hit.DamageType == DamageType.Fire) return true;
            if ((hit.Quality & WeaponQuality.Burning) == 0) return false;
            switch (hit.Delivery)
            {
                case HitDelivery.Melee: return remaining < 100;
                case HitDelivery.Missile: return hit.HitMultiplier >= 0.5f;
                default: return false;
            }
        }

        /// <summary>
        /// CanBePoisoned / CanBeDiseased for a weapon or missile [CP 1.75]: the weapon carries the quality bit (a
        /// creature's natural weapon too: bloodfly, swamp lurker and scorpion king poison; zombie, ripper, varan and troll
        /// infect, <see cref="SpeciesWeaponry"/>); poison only by a missile, not a blade, or a pierce/hack attack, disease by
        /// any blow; the player is immune with the perk; an NPC only catches it from the player's weapon, and evil creatures
        /// never. Every such blow does it, with no chance roll. (Spells poison through StatusEffects, MagicService.)
        /// </summary>
        static bool CanCatch(HitContext hit, WeaponQuality bit, string immunityPerk)
        {
            if ((hit.Quality & bit) == 0 || hit.Delivery == HitDelivery.Spell) return false;
            var v = hit.Victim;
            var a = hit.Attacker;
            bool missile = hit.Delivery != HitDelivery.Melee;
            bool way = bit != WeaponQuality.Poisoned || missile || hit.DamageType != DamageType.Blade ||
                       a != null && (a.Action == CombatAction.PierceAttack || a.Action == CombatAction.HackAttack);
            if (!way) return false;
            if (v.IsPlayer) return !v.Has(immunityPerk);
            return a != null && a.IsPlayer && !v.Evil;
        }

        static bool Contains(System.Collections.Generic.IReadOnlyList<string> list, string value)
        {
            foreach (var s in list)
                if (string.Equals(s, value, StringComparison.Ordinal)) return true;
            return false;
        }
    }
}
