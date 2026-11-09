using Myrtana.Rules;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// fix-93 (owner: "NPC archers always hit; in the original you could dodge"): G3 aims an NPC arrow once at release
    /// (PathStyle Line, ShootIteration re-aims only Missile); a target that steps aside after release is missed.
    /// </summary>
    public class ProjectileFlightTests
    {
        const float Dt = 1f / 60f;

        /// <summary>Flies a shot from <paramref name="from"/> for up to 2 s; the target walks sideways at <paramref name="sideSpeed"/> m/s
        /// for <paramref name="sideMetres"/> m from release. True when the arrow enters the target's capsule.</summary>
        static bool Flies(ProjectilePathStyle style, P3 from, P3 target, float sideSpeed, float sideMetres)
        {
            var v = ProjectileFlight.ReleaseVelocity(from, target + new P3(0, 1.2f, 0), RangedRules.ShotVelocity(player: false, 1f));
            var p = from;
            float moved = 0;
            for (int i = 0; i < 120; i++)
            {
                float step = System.Math.Min(sideSpeed * Dt, sideMetres - moved);
                if (step > 0) { target = target + new P3(0, 0, step); moved += step; }
                if (ProjectileFlight.ReAims(style)) // what the port did before fix-93: re-aim each frame
                    v = ProjectileFlight.ReleaseVelocity(p, target + new P3(0, 1.2f, 0), v.Length);
                v = ProjectileFlight.Advance(style, v, Dt);
                var next = p + v * Dt;
                if (ProjectilePath.Enter(p, next, target + new P3(0, 0.3f, 0), target + new P3(0, 1.5f, 0), 0.3f) != null) return true;
                p = next;
            }
            return false;
        }

        [Test]
        public void NpcArrows_FlyLine_AndAreNeverReAimed_PowerCastSpellMissilesWithATargetHome()
        {
            Assert.That(ProjectileFlight.StyleOf(player: false, spell: false, hasTarget: true), Is.EqualTo(ProjectilePathStyle.Line));
            Assert.That(ProjectileFlight.StyleOf(player: true, spell: false, hasTarget: false), Is.EqualTo(ProjectilePathStyle.Physics));
            Assert.That(ProjectileFlight.StyleOf(player: false, spell: true, hasTarget: true, powerCast: true), Is.EqualTo(ProjectilePathStyle.Missile));
            Assert.That(ProjectileFlight.ReAims(ProjectilePathStyle.Line), Is.False);
            Assert.That(ProjectileFlight.ReAims(ProjectilePathStyle.Physics), Is.False);
            Assert.That(ProjectileFlight.ReAims(ProjectilePathStyle.Missile), Is.True);
            Assert.That(ProjectileFlight.Falls(ProjectilePathStyle.Line), Is.False, "NPC shots do not drop");
            Assert.That(ProjectileFlight.Falls(ProjectilePathStyle.Physics), Is.True);
        }

        [Test]
        public void ATargetThatStandsStill_IsHit()
        {
            Assert.That(Flies(ProjectilePathStyle.Line, new P3(0, 1.5f, 0), new P3(20, 0, 0), 0, 0), Is.True);
        }

        [Test]
        public void ATargetThatStepsSideways1_5mAfterRelease_IsMissed()
        {
            // 20 m at 27.8 m/s = 0.72 s of flight; a sidestep (1.5 m at 3 m/s, done in 0.5 s) takes him out of the line.
            Assert.That(Flies(ProjectilePathStyle.Line, new P3(0, 1.5f, 0), new P3(20, 0, 0), 3f, 1.5f), Is.False, "G3 Line: dodged");
            Assert.That(Flies(ProjectilePathStyle.Missile, new P3(0, 1.5f, 0), new P3(20, 0, 0), 3f, 1.5f), Is.True,
                        "a re-aimed shot (the old port, or a spell missile) follows him");
        }

        /// <summary>
        /// fix-159 (owner: "the fireball and other magic turn after a moving target"): G3 MagicProjectile flies Missile
        /// only when the caster's action is PowerCast and it has a focus; a plain cast is Line, aimed once at release [0x10052db0].
        /// </summary>
        [Test]
        public void SpellMissiles_HomeOnlyWhenPowerCastAtATarget()
        {
            foreach (bool player in new[] { true, false })
            {
                Assert.That(ProjectileFlight.StyleOf(player, spell: true, hasTarget: true, powerCast: false), Is.EqualTo(ProjectilePathStyle.Line), "plain cast at a target");
                Assert.That(ProjectileFlight.StyleOf(player, spell: true, hasTarget: false, powerCast: true), Is.EqualTo(ProjectilePathStyle.Line), "power cast without a focus");
                Assert.That(ProjectileFlight.StyleOf(player, spell: true, hasTarget: false, powerCast: false), Is.EqualTo(ProjectilePathStyle.Line));
                Assert.That(ProjectileFlight.StyleOf(player, spell: true, hasTarget: true, powerCast: true), Is.EqualTo(ProjectilePathStyle.Missile));
            }
            Assert.That(ProjectileFlight.Falls(ProjectileFlight.StyleOf(true, spell: true, hasTarget: false)), Is.False, "spells never drop");
        }

        [Test]
        public void APlainFireball_IsDodgedByASidestep()
        {
            var style = ProjectileFlight.StyleOf(player: false, spell: true, hasTarget: true, powerCast: false);
            Assert.That(Flies(style, new P3(0, 1.5f, 0), new P3(20, 0, 0), 3f, 1.5f), Is.False, "Line: dodged");
            Assert.That(Flies(style, new P3(0, 1.5f, 0), new P3(20, 0, 0), 0, 0), Is.True, "a target standing still is hit");
        }

        [Test]
        public void OnlyPowerTypeSpellsCanHome()
        {
            Assert.That(ProjectileFlight.CanHome(Spell(SpellCastType.Power)), Is.True);
            Assert.That(ProjectileFlight.CanHome(Spell(SpellCastType.Immediate)), Is.False);
            Assert.That(ProjectileFlight.CanHome(Spell(SpellCastType.Regular)), Is.False);
        }

        static SpellDef Spell(SpellCastType type) =>
            new SpellDef("Spl_Test", new[] { "It_Test" }, new string[0], type, SpellTarget.Focus, SpellKind.Target,
                         SpellDeity.Adanos, 10, 20, SpellDelivery.Projectile, null, null, 0, "Mis_Test", null, null);

        [Test]
        public void TheReleaseAimHasNoLead()
        {
            var v = ProjectileFlight.ReleaseVelocity(new P3(0, 0, 0), new P3(10, 0, 0), 27.8f);
            Assert.That(v.X, Is.EqualTo(27.8f).Within(1e-3f));
            Assert.That(v.Z, Is.EqualTo(0f).Within(1e-6f));
        }
    }
}
