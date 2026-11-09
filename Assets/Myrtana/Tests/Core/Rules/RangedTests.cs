using System;
using System.Collections.Generic;
using Myrtana.Core;
using Myrtana.Rules;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>Bows and crossbows (Docs/combat.md §2) and the combat motion speed factors.</summary>
    [TestFixture]
    public class RangedTests
    {
        static ItemDef Item(string id, string kind, string damageType, int damage) =>
            new ItemDef(id, new TextId("FO_" + id), default, ItemCategory.Weapon, kind, EquipSlot.Ammo, 0, 0, null, null,
                        null, null, null, null, null, new ItemDamage(damageType, damage, default), default);

        [Test]
        public void BowDraw_GrowsFromAFifthToFullIn195Seconds_CrossbowIsAlwaysFull()
        {
            Assert.That(RangedRules.HitMultiplier(WeaponClass.Bow, 0), Is.EqualTo(0.2f).Within(1e-5));
            Assert.That(RangedRules.HitMultiplier(WeaponClass.Bow, 0.975f), Is.EqualTo(0.6f).Within(1e-5));
            Assert.That(RangedRules.HitMultiplier(WeaponClass.Bow, 1.95f), Is.EqualTo(1f).Within(1e-5));
            Assert.That(RangedRules.HitMultiplier(WeaponClass.Bow, 5f), Is.EqualTo(1f).Within(1e-5));
            Assert.That(RangedRules.HitMultiplier(WeaponClass.Crossbow, 0), Is.EqualTo(1f));
            // GetDirection: ShootVelocity (km/h) · 0.277778 → m/s (fix-56)
            Assert.That(RangedRules.ShotVelocity(player: true, 1f), Is.EqualTo(55.556f).Within(1e-2));
            Assert.That(RangedRules.ShotVelocity(player: true, 0.5f), Is.EqualTo(27.778f).Within(1e-2));
            Assert.That(RangedRules.ShotVelocity(player: false, 0.2f), Is.EqualTo(27.778f).Within(1e-2));
        }

        [Test]
        public void LaunchElevation_ReachesTheTarget_OutOfReachIsRefused()
        {
            float v = RangedRules.ShotVelocity(true, 1f);
            Assert.That(RangedRules.LaunchElevation(40f, 0f, v, out float a), Is.True);
            // flat: sin 2θ = g·x / v²
            Assert.That(Math.Sin(2 * a), Is.EqualTo(RangedRules.Gravity * 40 / (v * v)).Within(1e-4));
            Assert.That(RangedRules.LaunchElevation(40f, -14f, v, out float down), Is.True);
            Assert.That(down, Is.LessThan(a));
            Assert.That(RangedRules.LaunchElevation(400f, 0f, v, out float far), Is.False, "range v²/g ≈ 315 m");
            Assert.That(far, Is.EqualTo((float)(Math.PI / 4)).Within(1e-6));
        }

        [Test]
        public void ProjectileDamage_IsAmmoPlusWeapon_BluntArrowsHitWithImpact()
        {
            var bow = Item("It_Bow_Hunting", "Bow", "Missile", 45);
            var arrow = Item("Arrow", "Arrow", "Missile", 0);
            var blunt = Item("BluntArrow", "Arrow", "Impact", 20);
            Assert.That(RangedRules.ProjectileDamage(arrow, bow), Is.EqualTo(45));
            Assert.That(RangedRules.ProjectileDamage(blunt, bow), Is.EqualTo(65));
            Assert.That(RangedRules.ProjectileDamageType(arrow), Is.EqualTo(DamageType.Missile));
            Assert.That(RangedRules.ProjectileDamageType(blunt), Is.EqualTo(DamageType.Impact));
            Assert.IsTrue(RangedRules.IsFireOrExplosive("FireArrow"));
            Assert.IsFalse(RangedRules.IsFireOrExplosive("SharpArrow"));
        }

        [Test]
        public void Ammo_EquippedStackFirst_ThenTheAutoSelectOrder()
        {
            var defs = new Dictionary<string, ItemDef>
            {
                ["Arrow"] = Item("Arrow", "Arrow", "Missile", 0),
                ["FireArrow"] = Item("FireArrow", "Arrow", "Missile", 10),
                ["Bolt"] = Item("Bolt", "Bolt", "Missile", 0),
            };
            var carried = new Dictionary<string, int> { ["FireArrow"] = 3, ["Arrow"] = 0, ["Bolt"] = 5 };
            ItemDef Def(string id) => defs.TryGetValue(id, out var d) ? d : null;
            int Count(string id) => carried.TryGetValue(id, out var n) ? n : 0;
            Assert.That(RangedRules.SelectAmmo(WeaponClass.Bow, "Arrow", Def, Count), Is.EqualTo("FireArrow"), "the equipped stack is empty");
            carried["Arrow"] = 2;
            Assert.That(RangedRules.SelectAmmo(WeaponClass.Bow, "FireArrow", Def, Count), Is.EqualTo("FireArrow"));
            Assert.That(RangedRules.SelectAmmo(WeaponClass.Bow, "Bolt", Def, Count), Is.EqualTo("Arrow"), "bolts do not fit a bow");
            Assert.That(RangedRules.SelectAmmo(WeaponClass.Crossbow, null, Def, Count), Is.EqualTo("Bolt"));
            carried["Bolt"] = 0;
            Assert.That(RangedRules.SelectAmmo(WeaponClass.Crossbow, null, Def, Count), Is.Null);
        }

        [Test]
        public void Renock_FollowsTheChoiceMadeAfterTheNock()
        {
            // fix-184: Arrow nocked at the draw, then ExplosiveArrow equipped: the explosive one goes on the string
            Assert.That(RangedRules.Renock("Arrow", "ExplosiveArrow"), Is.EqualTo("ExplosiveArrow"));
            Assert.That(RangedRules.Renock("Arrow", "Arrow"), Is.Null, "same stack: kept");
            Assert.That(RangedRules.Renock("Arrow", null), Is.Null, "nothing to shoot: kept (the release lowers the bow)");
        }

        [Test]
        public void NpcRefill_Is10To19()
        {
            var random = new SeededRandom(7);
            for (int i = 0; i < 50; i++) Assert.That(RangedRules.NpcRefill(random), Is.InRange(10, 19));
        }

        [Test]
        public void MotionSpeed_Table_AbPlayerPenalty_AndModifiers()
        {
            var ab = RuleOptions.Default;
            var vanilla = new RuleOptions(alternativeBalancing: false, extendedAI: false);
            var hard = new RuleOptions(difficulty: Difficulty.Hard);
            var npc1H = new MotionSpeedQuery { Motion = CombatMotion.Attack, RightHand = WeaponClass.OneHanded, Species = "Human" };
            Assert.That(MotionSpeedRules.Factor(npc1H, ab), Is.EqualTo(0.6f));
            var zombie = new MotionSpeedQuery { Motion = CombatMotion.Attack, RightHand = WeaponClass.Fist, Species = "Zombie" };
            Assert.That(MotionSpeedRules.Factor(zombie, ab), Is.EqualTo(1.4f));
            var orcWindUp = new MotionSpeedQuery { Motion = CombatMotion.PowerAttack, WindUp = true, Species = "Orc" };
            Assert.That(MotionSpeedRules.Factor(orcWindUp, ab), Is.EqualTo(1f));
            orcWindUp.Species = "Human";
            Assert.That(MotionSpeedRules.Factor(orcWindUp, ab), Is.EqualTo(1.5f));
            var troll = new MotionSpeedQuery { Motion = CombatMotion.Attack, Species = "Troll" };
            Assert.That(MotionSpeedRules.Factor(troll, vanilla), Is.EqualTo(1.5f));
            Assert.That(MotionSpeedRules.Factor(troll, ab), Is.EqualTo(1f));

            var player = new MotionSpeedQuery { Motion = CombatMotion.QuickAttack, IsPlayer = true, Stamina = 100 };
            Assert.That(MotionSpeedRules.Factor(player, ab), Is.EqualTo(1f));
            player.Stamina = 30;
            Assert.That(MotionSpeedRules.Factor(player, ab), Is.EqualTo(0.85f).Within(1e-5));
            player.Stamina = 10;
            Assert.That(MotionSpeedRules.Factor(player, ab), Is.EqualTo(0.7f).Within(1e-5));
            var parade = new MotionSpeedQuery { Motion = CombatMotion.Parade, IsPlayer = true, Stamina = 100 };
            Assert.That(MotionSpeedRules.Factor(parade, ab), Is.EqualTo(2.3f));

            var diseased = new MotionSpeedQuery { Motion = CombatMotion.Stumble, Diseased = true, Arena = true };
            Assert.That(MotionSpeedRules.Factor(diseased, hard), Is.EqualTo(1.3f * 0.75f * 1.25f).Within(1e-5));
            var cock = new MotionSpeedQuery { Motion = CombatMotion.Cock, IsPlayer = true, HasPerkCrossbow2 = true };
            Assert.That(MotionSpeedRules.Factor(cock, ab), Is.EqualTo(1.5f));
            cock.IsPlayer = false;
            Assert.That(MotionSpeedRules.Factor(cock, ab), Is.EqualTo(1.5f));
            Assert.That(MotionSpeedRules.MotionOf(HitReaction.LieKnockDown), Is.EqualTo(CombatMotion.Fixed));
        }
    }
}
