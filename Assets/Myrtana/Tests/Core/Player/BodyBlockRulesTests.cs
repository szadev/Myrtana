using System.Collections.Generic;
using G3.Player;
using NUnit.Framework;

namespace Myrtana.Tests
{
    public class BodyBlockRulesTests
    {
        const float Hero = 0.35f;
        static readonly List<BodyCircle> Npc = new List<BodyCircle> { new BodyCircle(0f, 0f, 0.35f) };

        /// <summary>Walks from (x, z) by (dx, dz) per step; returns the end point.</summary>
        static (float x, float z) Walk(float x, float z, float dx, float dz, int steps, IReadOnlyList<BodyCircle> bodies)
        {
            for (int i = 0; i < steps; i++)
            {
                float mx = dx, mz = dz;
                BodyBlockRules.Clip(x, z, Hero, ref mx, ref mz, bodies);
                x += mx;
                z += mz;
            }
            return (x, z);
        }

        static float Dist(float x, float z, BodyCircle b) => (float)System.Math.Sqrt((x - b.X) * (x - b.X) + (z - b.Z) * (z - b.Z));

        [Test]
        public void HeadOnStopsAtContact()
        {
            // Hamlar case: running straight at a standing NPC (4 m/s, 60 Hz) stops at hero + body radius, never inside.
            var (x, z) = Walk(0f, -3f, 0f, 4f / 60f, 240, Npc);
            Assert.That(Dist(x, z, Npc[0]), Is.EqualTo(0.7f).Within(0.002f));
            Assert.That(x, Is.EqualTo(0f).Within(1e-4f), "head-on: no sideways drift");
        }

        [Test]
        public void OffCentreSlidesRound()
        {
            // 0.3 m off the line: the hero slides round the body and goes on, never closer than contact.
            float x = 0.3f, z = -3f, min = float.MaxValue;
            for (int i = 0; i < 240; i++)
            {
                float mx = 0f, mz = 4f / 60f;
                BodyBlockRules.Clip(x, z, Hero, ref mx, ref mz, Npc);
                x += mx;
                z += mz;
                min = System.Math.Min(min, Dist(x, z, Npc[0]));
            }
            Assert.That(min, Is.GreaterThanOrEqualTo(0.7f - 0.002f));
            Assert.That(z, Is.GreaterThan(2f), "passed the body");
        }

        [Test]
        public void EveryOffsetPassesWithoutStalling([Values(0.02f, 0.1f, 0.25f, 0.45f, 0.6f, 0.69f)] float offset)
        {
            float x = offset, z = -3f, min = float.MaxValue;
            for (int i = 0; i < 240; i++)
            {
                float mx = 0f, mz = 6f / 60f; // sprint
                BodyBlockRules.Clip(x, z, Hero, ref mx, ref mz, Npc);
                x += mx;
                z += mz;
                min = System.Math.Min(min, Dist(x, z, Npc[0]));
            }
            Assert.That(min, Is.GreaterThanOrEqualTo(0.7f - 0.002f));
            Assert.That(z, Is.GreaterThan(2f), "slid round and went on");
        }

        [Test]
        public void MovesAwayAndPastFreely()
        {
            float dx = 0f, dz = -0.1f;
            Assert.That(BodyBlockRules.Clip(0f, -0.7f, Hero, ref dx, ref dz, Npc), Is.False, "stepping back from contact");
            dx = 0.1f; dz = 0f;
            Assert.That(BodyBlockRules.Clip(-2f, -2f, Hero, ref dx, ref dz, Npc), Is.False, "far away");
            Assert.That(dx, Is.EqualTo(0.1f));
        }

        [Test]
        public void OverlapNeverGoesDeeperButCanLeave()
        {
            // The NPC walked into the hero (0.3 m apart): no push, no deeper, out and aside allowed.
            float dx = 0f, dz = 0.1f;
            Assert.That(BodyBlockRules.Clip(0f, -0.3f, Hero, ref dx, ref dz, Npc), Is.True);
            Assert.That(dz, Is.EqualTo(0f).Within(1e-5f));
            dx = 0f; dz = -0.1f;
            Assert.That(BodyBlockRules.Clip(0f, -0.3f, Hero, ref dx, ref dz, Npc), Is.False, "leaving");
            dx = 0.1f; dz = 0.05f;
            BodyBlockRules.Clip(0f, -0.3f, Hero, ref dx, ref dz, Npc);
            Assert.That(dx, Is.EqualTo(0.1f).Within(1e-5f), "aside");
            Assert.That(dz, Is.LessThanOrEqualTo(1e-5f));
        }

        [Test]
        public void WedgedBetweenTwoStands()
        {
            // A gap narrower than the hero between two NPCs: he does not squeeze through.
            var two = new List<BodyCircle> { new BodyCircle(-0.6f, 0f, 0.35f), new BodyCircle(0.6f, 0f, 0.35f) };
            var (x, z) = Walk(0f, -2f, 0f, 4f / 60f, 240, two);
            Assert.That(z, Is.LessThan(0f));
            Assert.That(Dist(x, z, two[0]), Is.GreaterThanOrEqualTo(0.7f - 0.002f));
            Assert.That(Dist(x, z, two[1]), Is.GreaterThanOrEqualTo(0.7f - 0.002f));
        }

        [Test]
        public void SprintFrameDoesNotTunnel()
        {
            // One long step (a 0.1 s hitch at sprint: 0.6 m) that would end past a thin body still stops at contact.
            float dx = 0f, dz = 1.6f;
            BodyBlockRules.Clip(0f, -0.8f, Hero, ref dx, ref dz, new List<BodyCircle> { new BodyCircle(0f, 0f, 0.2f) });
            Assert.That(-0.8f + dz, Is.EqualTo(-0.55f).Within(0.002f));
        }

        [Test]
        public void NoBodiesNoChange()
        {
            float dx = 0.2f, dz = 0.3f;
            Assert.That(BodyBlockRules.Clip(0f, 0f, Hero, ref dx, ref dz, new List<BodyCircle>()), Is.False);
            Assert.That(BodyBlockRules.Clip(0f, 0f, Hero, ref dx, ref dz, null), Is.False);
            Assert.That((dx, dz), Is.EqualTo((0.2f, 0.3f)));
        }
    }
}
