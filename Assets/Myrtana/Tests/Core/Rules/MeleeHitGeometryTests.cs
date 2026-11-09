using Myrtana.Rules;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// Melee hit geometry (fix-51): G3's weapon trigger (shape group 7) is far taller than the creature, so a swing at the
    /// hero's chest height touches meatbugs, goblins and boars on flat ground and downhill; a fast swing does not tunnel.
    /// </summary>
    public class MeleeHitGeometryTests
    {
        // Joint extents incl. flesh, m (width x, top y, length z).
        static readonly WeaponTrigger Goblin = MeleeHitGeometry.Trigger(0.7f, 1.3f, 0.5f, 0f);
        static readonly WeaponTrigger Meatbug = MeleeHitGeometry.Trigger(0.35f, 0.3f, 0.5f, 0f);
        static readonly WeaponTrigger Boar = MeleeHitGeometry.Trigger(0.6f, 1.0f, 1.6f, 0.1f);
        static readonly WeaponTrigger Wolf = MeleeHitGeometry.Trigger(0.6f, 0.9f, 1.6f, 0.1f);
        static readonly WeaponTrigger Human = MeleeHitGeometry.Trigger(0.8f, 1.95f, 0.5f, 0f);
        const float Blade = 0.08f;

        /// <summary>
        /// One frame of the hero's 1H Attack in the victim's frame: the blade (hand 0.4 m, tip 1.4 m ahead of the hero) sweeps
        /// sideways from <paramref name="fromX"/> to <paramref name="toX"/> at 1.15 m above the hero's feet. The victim stands
        /// <paramref name="ahead"/> m in front of the hero and <paramref name="below"/> m lower.
        /// </summary>
        static bool Swing(WeaponTrigger victim, float ahead, float below, float fromX = 0.9f, float toX = -0.9f)
        {
            float y = 1.15f + below, z = -ahead;
            return MeleeHitGeometry.SweepTouches(victim,
                new HitPoint(fromX, y, z + 0.4f), new HitPoint(fromX, y, z + 1.4f),
                new HitPoint(toX, y, z + 0.4f), new HitPoint(toX, y, z + 1.4f), Blade);
        }

        [Test]
        public void Trigger_IsFarTallerThanTheBody_AsG3Templates()
        {
            foreach (var t in new[] { Goblin, Meatbug, Boar, Wolf, Human })
            {
                Assert.That(t.Bottom, Is.EqualTo(-MeleeHitGeometry.Below).Within(1e-4), "below the feet (G3 -0.4..-1.3 m)");
                Assert.That(t.Top, Is.GreaterThanOrEqualTo(MeleeHitGeometry.MinTop), "G3 tops 1.9..2.5 m");
            }
            Assert.That(Human.Top, Is.EqualTo(2.45f).Within(1e-3), "G3 human capsule up to 2.42 m");
            Assert.That(Human.IsBox, Is.False);
            Assert.That(Human.Radius, Is.EqualTo(0.64f).Within(0.01f), "G3 human capsule r 0.66");
            Assert.That(Boar.IsBox && Wolf.IsBox && Meatbug.IsBox, "animals: boxes over the footprint");
            Assert.That(Meatbug.HalfX, Is.EqualTo(MeleeHitGeometry.MinHalf), "G3 meatbug +-0.25 m");
            Assert.That(Boar.CenterZ, Is.EqualTo(0.1f));
        }

        [Test]
        public void ChestHighSwing_TouchesSmallCreatures_OnFlatGround()
        {
            Assert.That(Swing(Meatbug, 1.2f, 0f), "meatbug");
            Assert.That(Swing(Goblin, 1.2f, 0f), "goblin");
            Assert.That(Swing(Wolf, 1.6f, 0f), "wolf");
            Assert.That(Swing(Boar, 1.6f, 0f), "boar");
        }

        [Test]
        public void ChestHighSwing_TouchesCreaturesDownhillAndUphill()
        {
            Assert.That(Swing(Goblin, 1.2f, 0.7f), "goblin 0.7 m below (the owner's snowy slope)");
            Assert.That(Swing(Boar, 1.5f, 0.7f), "boar 0.7 m below (fix-25 okara_01)");
            Assert.That(Swing(Meatbug, 1.2f, 0.8f), "meatbug 0.8 m below");
            Assert.That(Swing(Goblin, 1.2f, -1.0f), "goblin 1 m above");
        }

        [Test]
        public void OldSkeletonCapsule_WasMissedDownhill()
        {
            // the pre-fix body: an upright capsule over the joints only (goblin top 1.3 m) -> the swing passed above it
            var body = new WeaponTrigger(false, 0.25f, 0.25f, 0f, 1.3f, 0f);
            Assert.That(Swing(body, 1.2f, 0.7f), Is.False);
        }

        [Test]
        public void SwingOutOfReach_DoesNotTouch()
        {
            Assert.That(Swing(Goblin, 2.6f, 0f), Is.False, "tip 1.4 m ahead, goblin 2.6 m away");
            Assert.That(Swing(Goblin, 1.2f, 1.2f), Is.False, "1.2 m below: the swing passes over its 2 m trigger");
        }

        [Test]
        public void FastSwing_DoesNotTunnel()
        {
            // a thin target right between two frames' poses: neither pose touches, the sweep does
            var post = new WeaponTrigger(true, 0.05f, 0.05f, 0f, 2f, 0f);
            var a0 = new HitPoint(1.5f, 1f, -0.5f);
            var b0 = new HitPoint(1.5f, 1f, 0.5f);
            var a1 = new HitPoint(-1.5f, 1f, -0.5f);
            var b1 = new HitPoint(-1.5f, 1f, 0.5f);
            Assert.That(MeleeHitGeometry.Gap(post, a0, b0, Blade), Is.GreaterThan(0f));
            Assert.That(MeleeHitGeometry.Gap(post, a1, b1, Blade), Is.GreaterThan(0f));
            Assert.That(MeleeHitGeometry.SweepTouches(post, a0, b0, a1, b1, Blade));
        }

        [Test]
        public void Steps_KeepConsecutivePosesOverlapping()
        {
            Assert.That(MeleeHitGeometry.Steps(0f, Blade), Is.EqualTo(1));
            Assert.That(MeleeHitGeometry.Steps(1.6f, Blade), Is.InRange(20, 21));
            Assert.That(1.6f / MeleeHitGeometry.Steps(1.6f, Blade), Is.LessThanOrEqualTo(Blade + 1e-4f));
            Assert.That(MeleeHitGeometry.Steps(100f, Blade), Is.EqualTo(MeleeHitGeometry.MaxSteps));
        }

        [Test]
        public void Gap_MeasuresTheClosestApproach()
        {
            // a blade lying 0.5 m above the goblin's 2 m top
            float gap = MeleeHitGeometry.Gap(Goblin, new HitPoint(-1f, Goblin.Top + 0.5f, 0f), new HitPoint(1f, Goblin.Top + 0.5f, 0f), Blade);
            Assert.That(gap, Is.EqualTo(0.5f - Blade).Within(0.01f));
            Assert.That(Goblin.Distance(new HitPoint(0f, 1f, 0f)), Is.EqualTo(0f));
        }
    }
}
