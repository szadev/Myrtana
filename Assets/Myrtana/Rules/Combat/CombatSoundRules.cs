using System;
using Myrtana.Core;

namespace Myrtana.Rules
{
    /// <summary>A shared combat line (SVM) a humanoid speaks on a hit (G3 FUN_1000baf0 → Entity::StartSaySVM).</summary>
    public enum CombatLine { None, Aargh, Down, Dead, FinishingMove }

    /// <summary>
    /// Which G3 effect (g3.efm) or shared voice line a combat event plays, as Script_Game.dll of CP 1.75 decides it
    /// (Docs/combat.md §13). Pure names; the views look them up and play them.
    /// </summary>
    public static class CombatSoundRules
    {
        /// <summary>
        /// The impact sound of a resolved hit (DoLogicalDamage: the effect is chosen from AssessHit's result only; the
        /// weapon and body materials are read there but not used, so every body hit is "metal on flesh").
        /// Parade stumbles: D &lt; 2 → level 1, D = 2 → level 2, D &gt; 2 → level 3 (metal on metal).
        /// </summary>
        public static string HitEffect(HitReaction reaction, int powerDelta)
        {
            switch (reaction)
            {
                case HitReaction.Flinch:
                case HitReaction.QuickStumble:
                    return "eff_col_wh_01_me_fl";
                case HitReaction.Stumble:
                case HitReaction.PierceStumble:
                    return "eff_col_wh_02_me_fl";
                case HitReaction.SitKnockDown:
                case HitReaction.LieKnockDown:
                case HitReaction.Unconscious:
                case HitReaction.PiercedKnockOut:
                case HitReaction.Dead:
                case HitReaction.PiercedDead:
                    return "eff_col_wh_03_me_fl";
                case HitReaction.ParadeStumble:
                case HitReaction.HeavyParadeStumble:
                    return powerDelta < 2 ? "eff_col_wh_01_me_me" : powerDelta == 2 ? "eff_col_wh_02_me_me" : "eff_col_wh_03_me_me";
                default:
                    return null;
            }
        }

        /// <summary>
        /// The extra clash of a blocked melee hit under ExtendedAI (AssessHit, AAI branch): metal on metal when the
        /// attacker's weapon and the blocking item are both metal, else the "metal on wood" level hit; none when the
        /// defender fights with fists.
        /// </summary>
        public static string ParadeClashEffect(bool extendedAi, bool defenderFists, bool attackerWeaponMetal, bool blockerMetal)
        {
            if (!extendedAi || defenderFists) return null;
            return attackerWeaponMetal && blockerMetal ? "eff_col_wh_01_me_me" : "eff_col_weaponhitslevel_metal_wood_01";
        }

        /// <summary>
        /// Draw or sheathe sound of a hand item (_AI_HoldInventoryItems): blade weapons the sword sound, blunt ones
        /// (not torches or fists) the wood sound; bows, shields and the rest are silent.
        /// </summary>
        public static string DrawEffect(string damageType, string useKind, bool draw)
        {
            if (string.Equals(damageType, "Blade", StringComparison.OrdinalIgnoreCase))
                return draw ? "eff_ani_fight_draw_1hsword_01" : "eff_ani_fight_undraw_1hsword_01";
            if (string.Equals(damageType, "Impact", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(useKind, "Torch", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(useKind, "Fist", StringComparison.OrdinalIgnoreCase))
                return draw ? "eff_ani_fight_draw_1hwood_01" : "eff_ani_fight_undraw_1hwood_01";
            return null;
        }

        /// <summary>The bow string being drawn (PS_Ranged_PowerAim / ZS_Attack_Shoot); stopped by the shot.</summary>
        public const string BowRaiseEffect = "eff_ani_fight_bow_raise_01";
        /// <summary>The release of a bow or crossbow (Shoot).</summary>
        public const string ShootEffect = "eff_ani_fight_bow_shoot_01";
        /// <summary>A finishing blow on a downed body (OnAI_FinishingAttack).</summary>
        public const string FinishingBlowEffect = "eff_col_wh_03_me_fl";

        /// <summary>
        /// A held weapon touching the level (CP 1.75 OnItemTouchesLevel, collision group 7 without a spell):
        /// <c>eff_col_weaponhitslevel_&lt;weapon&gt;_&lt;level&gt;_01</c> with the weapon's material (metal or wood) and the
        /// level's; when that effect does not exist, the materials swapped, then the weapon's earth one [interp: G3's third
        /// candidate is built from the same two names; g3.efm has metal/wood x earth/stone/water/wood].
        /// </summary>
        public static string WeaponHitsLevelEffect(bool weaponMetal, string levelMaterial, Func<string, bool> exists)
        {
            string weapon = weaponMetal ? "metal" : "wood";
            string level = string.IsNullOrEmpty(levelMaterial) ? "earth" : levelMaterial.ToLowerInvariant();
            string first = "eff_col_weaponhitslevel_" + weapon + "_" + level + "_01";
            if (exists == null || exists(first)) return first;
            string swapped = "eff_col_weaponhitslevel_" + level + "_" + weapon + "_01";
            if (exists(swapped)) return swapped;
            return "eff_col_weaponhitslevel_" + weapon + "_earth_01";
        }

        /// <summary>A weapon in the level is heard only from the player or an owner within 5 m of him (250000 cm2).</summary>
        public const float LevelTouchSoundRange = 5f;

        /// <summary>
        /// An arrow or bolt hitting the level (OnItemTouchesLevel): <c>eff_col_arrow_&lt;material&gt;_01</c>, else the earth one.
        /// </summary>
        public static string ArrowImpactEffect(string material, Func<string, bool> exists)
        {
            const string fallback = "eff_col_arrow_earth_01";
            if (string.IsNullOrEmpty(material)) return fallback;
            string name = "eff_col_arrow_" + material.ToLowerInvariant() + "_01";
            return exists == null || exists(name) ? name : fallback;
        }

        /// <summary>
        /// The line a humanoid victim says: Aargh on the stumbles and the sit-down (PipiStumble, _AI_QuickStumble,
        /// _AI_Stumble, ZS_SitKnockDown), Down when knocked out (ZS_LieKnockDown, ZS_Unconscious, ZS_PiercedKO), Dead when
        /// killed (ZS_RagDollDead, ZS_PiercedDead). Parade and pierce stumbles are silent; creatures speak no lines.
        /// </summary>
        public static CombatLine VictimLine(HitReaction reaction, bool humanoid)
        {
            if (!humanoid) return CombatLine.None;
            switch (reaction)
            {
                case HitReaction.Flinch:
                case HitReaction.QuickStumble:
                case HitReaction.Stumble:
                case HitReaction.SitKnockDown:
                    return CombatLine.Aargh;
                case HitReaction.LieKnockDown:
                case HitReaction.Unconscious:
                case HitReaction.PiercedKnockOut:
                    return CombatLine.Down;
                case HitReaction.Dead:
                case HitReaction.PiercedDead:
                    return CombatLine.Dead;
                default:
                    return CombatLine.None;
            }
        }

        /// <summary>
        /// The sound a creature makes itself (not through its motions): the light flinch plays its stumble effect
        /// (PipiStumble), a death its die effect (ZS_RagDollDead): <c>eff_creature_&lt;skeleton&gt;_stumble_01 / _die_01</c>.
        /// Other reactions sound through the effect tracks of the creature's stumble motions.
        /// </summary>
        public static string CreatureEffect(HitReaction reaction, bool humanoid, string skeleton)
        {
            if (humanoid || string.IsNullOrEmpty(skeleton)) return null;
            string s = skeleton.ToLowerInvariant();
            switch (reaction)
            {
                case HitReaction.Flinch: return "eff_creature_" + s + "_stumble_01";
                case HitReaction.Dead:
                case HitReaction.PiercedDead: return "eff_creature_" + s + "_die_01";
                default: return null;
            }
        }

        /// <summary>
        /// The item's collision material is metal (eEShapeMaterial of its template). Packs without the material: blade
        /// weapons count as metal [interp].
        /// </summary>
        public static bool IsMetal(ItemDef item)
        {
            if (item == null) return false;
            if (!string.IsNullOrEmpty(item.Material)) return string.Equals(item.Material, "Metal", StringComparison.OrdinalIgnoreCase);
            return item.Damage != null && string.Equals(item.Damage.Type, "Blade", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The voice clip key of a shared line: <c>SVM_&lt;VoiceSet&gt;_&lt;Line&gt;</c> (the Speech pak name).</summary>
        public static string ClipKey(string voiceSet, string line) =>
            string.IsNullOrEmpty(voiceSet) || string.IsNullOrEmpty(line) ? null : "SVM_" + voiceSet + "_" + line;

        /// <summary>The SVM names of the combat lines every voice set needs (for the voice extraction).</summary>
        public static readonly string[] SvmNames = { "Aargh_00", "Aargh_01", "Aargh_02", "Aargh_03", "Down", "Dead", "FinishingMove" };
    }

    /// <summary>
    /// The three pain cries of StartSayAargh, one global state for every speaker: after <c>Aargh_00</c> the next is
    /// <c>_01</c> or <c>_02</c> (50 % each); after <c>_01</c>/<c>_02</c> it is <c>_00</c> or <c>_02</c> (50 % each).
    /// </summary>
    public sealed class PainCryCycle
    {
        int _state;

        /// <summary>The line to say now; advances the state.</summary>
        public string Next(IRandom random)
        {
            int roll = random.Next(100);
            string line;
            if (_state == 0)
            {
                line = "Aargh_00";
                _state = roll > 49 ? 2 : 1;
            }
            else
            {
                line = _state == 1 ? "Aargh_01" : "Aargh_02";
                _state = roll < 50 ? 0 : 2;
            }
            return line;
        }

        public void Reset() => _state = 0;
    }
}
