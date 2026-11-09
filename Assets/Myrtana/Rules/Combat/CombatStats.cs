using System;

namespace Myrtana.Rules
{
    /// <summary>
    /// Derived combat numbers: NPC stats from levels (gameplay-logic.md §2.2), player protections, weapon skill levels and
    /// the attack/defence levels that decide how hard a hit staggers (§3.3). G3 CP 1.75 formulas, the same for every pack.
    /// </summary>
    public static class CombatStats
    {
        public const int ProtectionCap = 80;
        public const int ArmorPerkCap = 90;
        public const int ResistPerkBonus = 10;
        public const string ResistHeatPerk = "Perk_ResistHeat", ResistColdPerk = "Perk_ResistCold";
        public const string LightArmorPerk = "Perk_LightArmor", HeavyArmorPerk = "Perk_HeavyArmor";
        public const string ShieldPerk = "Perk_Shield_2";
        /// <summary>The player cannot be poisoned / diseased with these perks (CanBePoisoned, CanBeDiseased).</summary>
        public const string ImmuneToPoisonPerk = "Perk_ImmuneToPoison", ImmuneToDiseasePerk = "Perk_ImmuneToDisease";

        // ----------------------------------------------------------------------------------------------- NPC levels

        public static int DifficultyOffset(Difficulty d) => d == Difficulty.Easy ? -10 : d == Difficulty.Hard ? 10 : 0;

        /// <summary>GetLevel / GetLevelMax of a non-player: the property plus the difficulty offset, at least 1.</summary>
        public static int NpcLevel(int property, Difficulty difficulty) => Math.Max(1, property + DifficultyOffset(difficulty));

        /// <summary>NPCs scale with the player: their Level is an offset above the player's level, capped by LevelMax.</summary>
        public static int NpcCurrentLevel(int level, int levelMax, int playerLevel, Difficulty difficulty) =>
            Math.Min(NpcLevel(level, difficulty) + playerLevel, NpcLevel(levelMax, difficulty));

        /// <summary>STR, DEX and INT of an NPC: 10 + 1.5·CurrentLevel.</summary>
        public static int NpcAttribute(int currentLevel) => 10 + (int)(1.5 * currentLevel);

        public static int NpcHitPointsMax(int levelMax, bool ambientCreature) => ambientCreature ? 1 : Math.Max(100, 20 * levelMax);

        public static int NpcStaminaMax(int levelMax, bool humanoid) => Math.Max(100, (humanoid ? 10 : 20) * levelMax);

        /// <summary>NPC mana is effectively unlimited.</summary>
        public const int NpcMana = 1000;

        /// <summary>Every NPC protection: min(80, 1.5·LevelMax).</summary>
        public static int NpcProtection(int levelMax) => Math.Min(ProtectionCap, (int)(1.5 * levelMax));

        // ----------------------------------------------------------------------------------------- player protection

        /// <summary>
        /// The player's combat protection from the stored attribute: 40 % of it (AB: 20 % for blade and impact), capped
        /// at 80, then +10 fire/ice with the resist perks.
        /// </summary>
        public static int PlayerProtection(int stored, DamageType type, RuleOptions options, Func<string, bool> hasPerk)
        {
            bool reduced = options.AlternativeBalancing && (type == DamageType.Blade || type == DamageType.Impact);
            int value = Math.Min(ProtectionCap, stored * (reduced ? 20 : 40) / 100);
            if (type == DamageType.Fire && hasPerk(ResistHeatPerk)) value += ResistPerkBonus;
            if (type == DamageType.Ice && hasPerk(ResistColdPerk)) value += ResistPerkBonus;
            return value;
        }

        /// <summary>The armour perks on top of the protection: Perk_LightArmor with a robe, Perk_HeavyArmor with other armour.</summary>
        public static int WithArmorPerks(int protection, Combatant victim, DamageType type, RuleOptions options)
        {
            bool light = victim.Armor == ArmorKind.Robe && victim.Has(LightArmorPerk);
            bool heavy = victim.Armor == ArmorKind.Armor && victim.Has(HeavyArmorPerk);
            if (!light && !heavy) return protection;
            if (options.AlternativeBalancing)
                return protection + victim.ArmorItemProtection[type] / (light ? 5 : 20);
            int boosted = light ? protection * 2 : protection + protection / 4;
            return Math.Min(ArmorPerkCap, boosted);
        }

        // ---------------------------------------------------------------------------------------- power levels (§3.3)

        /// <summary>
        /// Weapon skill level 0..3 (CP 1.75 FUN_1002afc0): NPCs by their level, the player by the level 2/3 perks of the
        /// right-hand melee class only (1H, dual 1H, 2H/axe/halberd, staff). A bow or crossbow gives the player level 0:
        /// G3 checks no ranged perk here; Perk_Bow_2/3 and Perk_Crossbow_2/3 add damage (and the crossbow perks speed up
        /// the reload), nothing else (fix-227).
        /// </summary>
        public static int WeaponSkillLevel(Combatant c)
        {
            if (!c.IsPlayer || c.IsTransformed) return c.Level > 44 ? 3 : c.Level > 29 ? 2 : c.Level > 14 ? 1 : 0;
            if (c.Weapon == WeaponClass.Bow || c.Weapon == WeaponClass.Crossbow) return 0;
            var (level2, level3) = SkillPerks(c);
            return c.Has(level3) ? 2 : c.Has(level2) ? 1 : 0;
        }

        /// <summary>The level 2/3 perks of the player's weapon class, for the damage bonus (null when the class has none).</summary>
        public static (string level2, string level3) SkillPerks(Combatant c)
        {
            switch (c.Weapon)
            {
                case WeaponClass.OneHanded:
                    return c.DualWield ? ("Perk_1H1H_2", "Perk_1H1H_SecretBonus") : ("Perk_1H_2", "Perk_1H_3");
                case WeaponClass.TwoHanded:
                case WeaponClass.Axe:
                case WeaponClass.Halberd:
                    return ("Perk_Axe_2", "Perk_Axe_3");
                case WeaponClass.Staff:
                    return ("Perk_Staff_2", "Perk_Staff_3");
                case WeaponClass.Bow:
                    return ("Perk_Bow_2", "Perk_Bow_3");
                case WeaponClass.Crossbow:
                    return ("Perk_Crossbow_2", "Perk_Crossbow_3");
                default:
                    return (null, null);
            }
        }

        public static int ActionLevel(Combatant attacker)
        {
            switch (attacker.Action)
            {
                case CombatAction.Attack:
                case CombatAction.SimpleWhirl:
                case CombatAction.WhirlAttack:
                case CombatAction.PierceAttack:
                case CombatAction.GetUpAttack:
                    return 2;
                case CombatAction.PowerAttack:
                case CombatAction.SprintAttack:
                    return attacker.DualWield ? (attacker.SecondPose ? 3 : 2) : 4;
                case CombatAction.HackAttack:
                    return 4;
                default:
                    return 0;
            }
        }

        public static int AttackLevel(Combatant attacker) => ActionLevel(attacker) + WeaponSkillLevel(attacker);

        public static int DefenseLevel(Combatant defender)
        {
            if (defender.Action == CombatAction.GetUpParade) return 2;
            int level = WeaponSkillLevel(defender);
            bool shieldCounts = defender.Shield && (!defender.IsPlayer || defender.Has(ShieldPerk));
            return shieldCounts ? level + 1 : level;
        }

        /// <summary>The power difference D of a hit: melee max(1, attack − defence); spells 2; missiles 2 or 0 by draw.</summary>
        public static int PowerDelta(HitContext hit)
        {
            switch (hit.Delivery)
            {
                case HitDelivery.Spell: return 2;
                case HitDelivery.Missile: return hit.HitMultiplier >= 0.6f ? 2 : 0;
                default: return Math.Max(1, AttackLevel(hit.Attacker) - DefenseLevel(hit.Victim));
            }
        }
    }
}
