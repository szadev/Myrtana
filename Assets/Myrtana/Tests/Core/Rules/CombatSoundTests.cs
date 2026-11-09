using System.Collections.Generic;
using Myrtana.Core;
using Myrtana.Rules;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>Combat sound selection as Script_Game.dll (CP 1.75) does it (Docs/combat.md §13).</summary>
    [TestFixture]
    public class CombatSoundTests
    {
        static ItemDef Weapon(string id, string kind, string damageType, string material = null) =>
            new ItemDef(id, new TextId("FO_" + id), default, ItemCategory.Weapon, kind, EquipSlot.MeleeWeapon, 0, 0, null, null,
                        null, null, null, null, null, new ItemDamage(damageType, 10, default), default, material: material);

        /// <summary>Plays a fixed list of rolls.</summary>
        sealed class Rolls : IRandom
        {
            readonly Queue<int> _values;
            public Rolls(params int[] values) => _values = new Queue<int>(values);
            public int Next(int maxExclusive) => _values.Dequeue();
            public float NextFloat() => 0;
            public RandomState Save() => default;
            public void Load(RandomState state) { }
        }

        [Test]
        public void BodyHits_DependOnTheReactionOnly()
        {
            Assert.That(CombatSoundRules.HitEffect(HitReaction.Flinch, 0), Is.EqualTo("eff_col_wh_01_me_fl"));
            Assert.That(CombatSoundRules.HitEffect(HitReaction.QuickStumble, 1), Is.EqualTo("eff_col_wh_01_me_fl"));
            Assert.That(CombatSoundRules.HitEffect(HitReaction.Stumble, 2), Is.EqualTo("eff_col_wh_02_me_fl"));
            Assert.That(CombatSoundRules.HitEffect(HitReaction.PierceStumble, 2), Is.EqualTo("eff_col_wh_02_me_fl"));
            foreach (var r in new[] { HitReaction.SitKnockDown, HitReaction.LieKnockDown, HitReaction.Unconscious,
                                      HitReaction.PiercedKnockOut, HitReaction.Dead, HitReaction.PiercedDead })
                Assert.That(CombatSoundRules.HitEffect(r, 4), Is.EqualTo("eff_col_wh_03_me_fl"), r.ToString());
            Assert.That(CombatSoundRules.HitEffect(HitReaction.None, 2), Is.Null);
        }

        [Test]
        public void ParadeStumbles_ClashByPowerDifference()
        {
            Assert.That(CombatSoundRules.HitEffect(HitReaction.ParadeStumble, 1), Is.EqualTo("eff_col_wh_01_me_me"));
            Assert.That(CombatSoundRules.HitEffect(HitReaction.ParadeStumble, 2), Is.EqualTo("eff_col_wh_02_me_me"));
            Assert.That(CombatSoundRules.HitEffect(HitReaction.HeavyParadeStumble, 3), Is.EqualTo("eff_col_wh_03_me_me"));
        }

        [Test]
        public void ParadeClash_OnlyWithExtendedAi_MetalOnMetalOrMetalOnWood()
        {
            Assert.That(CombatSoundRules.ParadeClashEffect(false, false, true, true), Is.Null);
            Assert.That(CombatSoundRules.ParadeClashEffect(true, true, true, true), Is.Null);
            Assert.That(CombatSoundRules.ParadeClashEffect(true, false, true, true), Is.EqualTo("eff_col_wh_01_me_me"));
            Assert.That(CombatSoundRules.ParadeClashEffect(true, false, true, false), Is.EqualTo("eff_col_weaponhitslevel_metal_wood_01"));
            Assert.That(CombatSoundRules.ParadeClashEffect(true, false, false, true), Is.EqualTo("eff_col_weaponhitslevel_metal_wood_01"));
        }

        [Test]
        public void Draw_SwordForBlades_WoodForBluntWeapons_NothingForTorchesFistsAndBows()
        {
            Assert.That(CombatSoundRules.DrawEffect("Blade", "OneHanded", true), Is.EqualTo("eff_ani_fight_draw_1hsword_01"));
            Assert.That(CombatSoundRules.DrawEffect("Blade", "Axe", false), Is.EqualTo("eff_ani_fight_undraw_1hsword_01"));
            Assert.That(CombatSoundRules.DrawEffect("Impact", "Staff", true), Is.EqualTo("eff_ani_fight_draw_1hwood_01"));
            Assert.That(CombatSoundRules.DrawEffect("Impact", "Staff", false), Is.EqualTo("eff_ani_fight_undraw_1hwood_01"));
            Assert.That(CombatSoundRules.DrawEffect("Impact", "Torch", true), Is.Null);
            Assert.That(CombatSoundRules.DrawEffect("Impact", "Fist", true), Is.Null);
            Assert.That(CombatSoundRules.DrawEffect("Missile", "Bow", true), Is.Null);
            Assert.That(CombatSoundRules.DrawEffect(null, "Shield", true), Is.Null);
        }

        [Test]
        public void ArrowImpact_ByLevelMaterial_EarthWhenThereIsNoSuchEffect()
        {
            var known = new HashSet<string> { "eff_col_arrow_wood_01", "eff_col_arrow_stone_01", "eff_col_arrow_earth_01" };
            Assert.That(CombatSoundRules.ArrowImpactEffect("Wood", known.Contains), Is.EqualTo("eff_col_arrow_wood_01"));
            Assert.That(CombatSoundRules.ArrowImpactEffect("stone", known.Contains), Is.EqualTo("eff_col_arrow_stone_01"));
            Assert.That(CombatSoundRules.ArrowImpactEffect("grass", known.Contains), Is.EqualTo("eff_col_arrow_earth_01"));
            Assert.That(CombatSoundRules.ArrowImpactEffect(null, known.Contains), Is.EqualTo("eff_col_arrow_earth_01"));
        }

        [Test]
        public void WeaponHitsLevel_ByWeaponAndLevelMaterial_WithFallbacks()
        {
            var known = new HashSet<string>
            {
                "eff_col_weaponhitslevel_metal_stone_01", "eff_col_weaponhitslevel_metal_earth_01", "eff_col_weaponhitslevel_wood_earth_01",
                "eff_col_weaponhitslevel_metal_wood_01",
            };
            Assert.That(CombatSoundRules.WeaponHitsLevelEffect(true, "Stone", known.Contains), Is.EqualTo("eff_col_weaponhitslevel_metal_stone_01"));
            Assert.That(CombatSoundRules.WeaponHitsLevelEffect(false, "earth", known.Contains), Is.EqualTo("eff_col_weaponhitslevel_wood_earth_01"));
            Assert.That(CombatSoundRules.WeaponHitsLevelEffect(false, "metal", known.Contains), Is.EqualTo("eff_col_weaponhitslevel_metal_wood_01"),
                        "swapped materials");
            Assert.That(CombatSoundRules.WeaponHitsLevelEffect(true, "snow", known.Contains), Is.EqualTo("eff_col_weaponhitslevel_metal_earth_01"));
            Assert.That(CombatSoundRules.WeaponHitsLevelEffect(true, null, known.Contains), Is.EqualTo("eff_col_weaponhitslevel_metal_earth_01"));
        }

        [Test]
        public void VictimLines_ForHumanoidsOnly()
        {
            Assert.That(CombatSoundRules.VictimLine(HitReaction.Stumble, true), Is.EqualTo(CombatLine.Aargh));
            Assert.That(CombatSoundRules.VictimLine(HitReaction.Flinch, true), Is.EqualTo(CombatLine.Aargh));
            Assert.That(CombatSoundRules.VictimLine(HitReaction.SitKnockDown, true), Is.EqualTo(CombatLine.Aargh));
            Assert.That(CombatSoundRules.VictimLine(HitReaction.PierceStumble, true), Is.EqualTo(CombatLine.None));
            Assert.That(CombatSoundRules.VictimLine(HitReaction.ParadeStumble, true), Is.EqualTo(CombatLine.None));
            Assert.That(CombatSoundRules.VictimLine(HitReaction.Unconscious, true), Is.EqualTo(CombatLine.Down));
            Assert.That(CombatSoundRules.VictimLine(HitReaction.PiercedDead, true), Is.EqualTo(CombatLine.Dead));
            Assert.That(CombatSoundRules.VictimLine(HitReaction.Dead, false), Is.EqualTo(CombatLine.None));
        }

        [Test]
        public void Creatures_FlinchAndDieThemselves()
        {
            Assert.That(CombatSoundRules.CreatureEffect(HitReaction.Dead, false, "Wolf"), Is.EqualTo("eff_creature_wolf_die_01"));
            Assert.That(CombatSoundRules.CreatureEffect(HitReaction.Flinch, false, "Scavenger"), Is.EqualTo("eff_creature_scavenger_stumble_01"));
            Assert.That(CombatSoundRules.CreatureEffect(HitReaction.Stumble, false, "Wolf"), Is.Null, "the stumble motion carries it");
            Assert.That(CombatSoundRules.CreatureEffect(HitReaction.Dead, true, "Hero"), Is.Null);
        }

        [Test]
        public void PainCries_CycleLikeStartSayAargh()
        {
            var cycle = new PainCryCycle();
            // state 0 → _00; roll 50 → state 2 → _02; roll 10 → state 0 → _00; roll 30 → state 1 → _01; roll 99 → state 2
            var rolls = new Rolls(50, 10, 30, 99, 0);
            Assert.That(cycle.Next(rolls), Is.EqualTo("Aargh_00"));
            Assert.That(cycle.Next(rolls), Is.EqualTo("Aargh_02"));
            Assert.That(cycle.Next(rolls), Is.EqualTo("Aargh_00"));
            Assert.That(cycle.Next(rolls), Is.EqualTo("Aargh_01"));
            Assert.That(cycle.Next(rolls), Is.EqualTo("Aargh_02"));
        }

        [Test]
        public void Metal_FromTheTemplateMaterial_BladesWhenUnknown()
        {
            Assert.That(CombatSoundRules.IsMetal(Weapon("It_1H_Sword", "OneHanded", "Blade", "Metal")), Is.True);
            Assert.That(CombatSoundRules.IsMetal(Weapon("It_Staff_Spear", "Halberd", "Blade", "Wood")), Is.False);
            Assert.That(CombatSoundRules.IsMetal(Weapon("It_Axe_Mace", "Axe", "Impact", "Metal")), Is.True);
            Assert.That(CombatSoundRules.IsMetal(Weapon("It_Old", "OneHanded", "Blade")), Is.True);
            Assert.That(CombatSoundRules.IsMetal(Weapon("It_Club", "OneHanded", "Impact")), Is.False);
            Assert.That(CombatSoundRules.IsMetal(null), Is.False);
            Assert.That(CombatSoundRules.ClipKey("Hum_Warrior_Hard", "Aargh_01"), Is.EqualTo("SVM_Hum_Warrior_Hard_Aargh_01"));
        }
    }
}
