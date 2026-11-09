using Myrtana.Core;
using Myrtana.Rules;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// fix-176: how arrows and bolts end their flight (G3 gCProjectile_PS::OnTouch 0x10152650, CP 1.75 templates) and what
    /// the special arrows do to the one they hit (explosive: blast effect, burning, no area damage).
    /// </summary>
    [TestFixture]
    public class ProjectileTouchTests
    {
        static ItemDef Ammo(string id, string type, int damage, int quality = 0, ItemProjectile projectile = null) =>
            new ItemDef(id, new TextId("FO_" + id), default, ItemCategory.Weapon, id.StartsWith("Bolt") ? "Bolt" : "Arrow", EquipSlot.Ammo, 0,
                        quality, null, null, null, null, null, null, null, damage != 0 ? new ItemDamage(type, damage, default) : null, default,
                        projectile: projectile);

        const int Wood = 1, Metal = 2, Stone = 4, Earth = 5, Flesh = 10, Snow = 11, Grass = 15;

        [Test]
        public void ExplosiveArrow_VanishesOnEverything()
        {
            var explosive = ProjectileTouchRules.Of(Ammo("ExplosiveArrow", "Missile", 30, 8));
            var touch = ProjectileTouchRules.TouchOf(explosive);
            Assert.That(touch, Is.EqualTo(ProjectileTouch.KillSelf));
            Assert.That(ProjectileTouchRules.OnCharacter(touch, explosive.LinkToBones), Is.EqualTo(ProjectileRest.Vanish));
            foreach (int? m in new int?[] { null, Wood, Stone, Earth, Grass })
                Assert.That(ProjectileTouchRules.OnLevel(touch, m), Is.EqualTo(ProjectileRest.Vanish), $"material {m}");
        }

        [Test]
        public void Arrows_StickInSoftMaterials_BounceOffHardOnes_LinkToBones()
        {
            foreach (var id in new[] { "Arrow", "SharpArrow", "FireArrow", "PoisonArrow" })
            {
                var p = ProjectileTouchRules.Of(Ammo(id, "Missile", 10));
                var t = ProjectileTouchRules.TouchOf(p);
                Assert.That(t, Is.EqualTo(ProjectileTouch.MaterialDependant), id);
                Assert.That(ProjectileTouchRules.OnCharacter(t, p.LinkToBones), Is.EqualTo(ProjectileRest.StickToBone), id);
                foreach (int m in new[] { Wood, Earth, 6, 7, Flesh, Snow })
                    Assert.That(ProjectileTouchRules.OnLevel(t, m), Is.EqualTo(ProjectileRest.Stick), $"{id} in {m}");
                foreach (int m in new[] { 0, Metal, 3, Stone, 8, 9, 12, 13, 14, Grass, 20 })
                    Assert.That(ProjectileTouchRules.OnLevel(t, m), Is.EqualTo(ProjectileRest.Drop), $"{id} on {m}");
                Assert.That(ProjectileTouchRules.OnLevel(t, null), Is.EqualTo(ProjectileRest.Stick), "unknown material: sticks [interp]");
            }
        }

        [Test]
        public void BluntArrow_FallsOffABody_Bolts_BounceOffTheLevel()
        {
            var blunt = ProjectileTouchRules.Of(Ammo("BluntArrow", "Impact", 20));
            Assert.That(ProjectileTouchRules.OnCharacter(ProjectileTouchRules.TouchOf(blunt), blunt.LinkToBones), Is.EqualTo(ProjectileRest.Drop));
            foreach (var id in new[] { "Bolt", "Bolt_Sharp" })
            {
                var bolt = ProjectileTouchRules.Of(Ammo(id, "Blade", 20));
                var t = ProjectileTouchRules.TouchOf(bolt);
                Assert.That(t, Is.EqualTo(ProjectileTouch.Reflect), id);
                Assert.That(ProjectileTouchRules.OnLevel(t, Wood), Is.EqualTo(ProjectileRest.Drop), id);
                Assert.That(ProjectileTouchRules.OnCharacter(t, bolt.LinkToBones), Is.EqualTo(ProjectileRest.StickToBone), id);
            }
            Assert.That(ProjectileTouchRules.OnLevel(ProjectileTouch.Stop, Stone), Is.EqualTo(ProjectileRest.Stick));
            Assert.That(ProjectileTouchRules.OnCharacter(ProjectileTouch.Stop, false), Is.EqualTo(ProjectileRest.Stick));
        }

        [Test]
        public void ImportedProjectileData_WinsOverTheKnownTable()
        {
            var odd = Ammo("Arrow", "Missile", 0, projectile: new ItemProjectile("KillSelf", false));
            Assert.That(ProjectileTouchRules.TouchOf(ProjectileTouchRules.Of(odd)), Is.EqualTo(ProjectileTouch.KillSelf));
            var unknown = ProjectileTouchRules.Of(Ammo("Mike_Arrow", "Missile", 0));
            Assert.That(ProjectileTouchRules.TouchOf(unknown), Is.EqualTo(ProjectileTouch.MaterialDependant));
            Assert.That(unknown.LinkToBones, Is.True);
            Assert.That(ProjectileTouchRules.TouchOf(new ItemProjectile("nonsense", true)), Is.EqualTo(ProjectileTouch.MaterialDependant));
        }

        [Test]
        public void ImpactEffect_TargetHitOnACharacter_PointImpactElsewhere()
        {
            Assert.That(ProjectileTouchRules.ImpactEffect("eff_point", "eff_target", onCharacter: true), Is.EqualTo("eff_target"));
            Assert.That(ProjectileTouchRules.ImpactEffect("eff_point", "eff_target", onCharacter: false), Is.EqualTo("eff_point"));
            Assert.That(ProjectileTouchRules.ImpactEffect("eff_point", null, onCharacter: true), Is.EqualTo("eff_point"));
            Assert.That(ProjectileTouchRules.ImpactEffect("", "", onCharacter: true), Is.Null);
        }

        [Test]
        public void SpecialArrows_DamageTypeAndStatusQualities_AsTheTemplates()
        {
            var bow = new ItemDef("It_Bow_01", default, default, ItemCategory.Weapon, "Bow", EquipSlot.RangedWeapon, 0, 0, null, null,
                                  null, null, null, null, null, new ItemDamage("Missile", 40, default), default);
            var explosive = Ammo("ExplosiveArrow", "Missile", 30, 8);
            Assert.That(RangedRules.ProjectileDamage(explosive, bow), Is.EqualTo(70), "ammo + bow, one victim only (no area damage in G3)");
            Assert.That(RangedRules.ProjectileDamageType(explosive), Is.EqualTo(DamageType.Missile));
            Assert.That((WeaponQuality)explosive.Quality & WeaponQuality.Burning, Is.EqualTo(WeaponQuality.Burning));
            Assert.That(RangedRules.IsFireOrExplosive("ExplosiveArrow"), Is.True);
            Assert.That(RangedRules.ProjectileDamageType(Ammo("BluntArrow", "Impact", 20)), Is.EqualTo(DamageType.Impact));
            Assert.That(RangedRules.ProjectileDamageType(Ammo("Bolt_Sharp", "Blade", 20)), Is.EqualTo(DamageType.Blade));
            Assert.That(RangedRules.ProjectileDamageType(Ammo("PoisonArrow", "Missile", 0, 4)), Is.EqualTo(DamageType.Missile));
        }

        [Test]
        public void ItemProjectile_SurvivesTheContentJson()
        {
            var def = Ammo("ExplosiveArrow", "Missile", 30, 8, new ItemProjectile("KillSelf", false));
            var json = System.Text.Json.JsonSerializer.Serialize(def);
            var back = System.Text.Json.JsonSerializer.Deserialize<ItemDef>(json);
            Assert.That(back.Projectile, Is.Not.Null);
            Assert.That(back.Projectile.Touch, Is.EqualTo("KillSelf"));
            Assert.That(back.Projectile.LinkToBones, Is.False);
        }
    }
}
