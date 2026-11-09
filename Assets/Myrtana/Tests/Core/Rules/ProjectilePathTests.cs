using Myrtana.Rules;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>fix-68: the first body on an arrow's path takes it (G3 projectiles collide with every movement capsule).</summary>
    public class ProjectilePathTests
    {
        static readonly P3 Up = new P3(0, 1, 0);

        /// <summary>An upright human capsule (r 0.35, 1.8 m) standing at x/z.</summary>
        static float? Body(P3 from, P3 to, float x, float z, float r = 0.35f) =>
            ProjectilePath.Enter(from, to, new P3(x, 0.35f, z), new P3(x, 1.45f, z), r);

        [Test]
        public void ASegmentThroughABody_EntersAtItsSurface()
        {
            var from = new P3(0, 1.2f, 0);
            var to = new P3(10, 1.2f, 0);
            var t = Body(from, to, 5, 0);
            Assert.That(t, Is.Not.Null);
            Assert.That(t.Value * 10, Is.EqualTo(4.65f).Within(0.01f), "enters 0.35 m before the axis");
        }

        [Test]
        public void ASegmentBesideOrAboveABody_Misses()
        {
            Assert.That(Body(new P3(0, 1.2f, 0), new P3(10, 1.2f, 0), 5, 0.5f), Is.Null, "0.5 m to the side");
            Assert.That(Body(new P3(0, 2.0f, 0), new P3(10, 2.0f, 0), 5, 0), Is.Null, "over the head");
            Assert.That(Body(new P3(0, 1.2f, 0), new P3(4, 1.2f, 0), 5, 0), Is.Null, "the step ends before it");
        }

        [Test]
        public void StartingInsideABody_TouchesAtOnce()
        {
            Assert.That(Body(new P3(5.1f, 1.2f, 0), new P3(9, 1.2f, 0), 5, 0), Is.EqualTo(0f));
        }

        [Test]
        public void TheOrcInBetween_IsMetBeforeTheHero()
        {
            // An orc archer at x=0 shoots the hero at x=20; another orc stands at x=8, 0.2 m off the line.
            var from = new P3(0, 1.3f, 0);
            var to = new P3(20, 1.3f, 0);
            var orc = Body(from, to, 8, 0.2f);
            var hero = Body(from, to, 20, 0);
            Assert.That(orc, Is.Not.Null);
            Assert.That(hero, Is.Not.Null);
            Assert.That(orc.Value, Is.LessThan(hero.Value));
        }

        [Test]
        public void ALongAnimal_LyingCapsuleAlongItsBody()
        {
            // A wolf: horizontal capsule from z=-0.6 to z=0.6 at 0.5 m height, r 0.3.
            var t = ProjectilePath.Enter(new P3(-5, 0.6f, 0.4f), new P3(5, 0.6f, 0.4f), new P3(0, 0.5f, -0.6f), new P3(0, 0.5f, 0.6f), 0.3f);
            Assert.That(t, Is.Not.Null);
            Assert.That(t.Value * 10, Is.EqualTo(5f - 0.3f).Within(0.02f));
            Assert.That(Up.Length, Is.EqualTo(1f));
        }
    }
}
