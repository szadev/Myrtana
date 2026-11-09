using System;
using G3.Unity;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// mt-1 (Docs/multithreading-plan.md C, D, E.1): the -g3mt switch and the evasion grid. The snapshot jobs only copy
    /// transforms (no arithmetic: Mono keeps float expressions in double precision where Burst rounds each step, so a
    /// distance computed in a job differed from the managed one in the last bit in ~20 % of the views).
    /// </summary>
    public class MtJobMathTests
    {
        [TestCase("0", 0)]
        [TestCase("3", 3)]
        [TestCase("0x83", 0x83)]
        [TestCase("0X1", 1)]
        public void ParsesMasks(string text, int mask)
        {
            Assert.That(MtBits.TryParse(text, out int m), Is.True);
            Assert.That(m, Is.EqualTo(mask));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("-1")]
        [TestCase("on")]
        public void RejectsBadMasks(string text) => Assert.That(MtBits.TryParse(text, out _), Is.False);

        [Test]
        public void CommandLineThenEnvironmentThenDefault()
        {
            Assert.That(MtBits.Resolve(0, "3"), Is.EqualTo(0), "-g3mt 0 wins over G3_MT");
            Assert.That(MtBits.Resolve(-1, "1"), Is.EqualTo(1));
            Assert.That(MtBits.Resolve(-1, null), Is.EqualTo(MtBits.Default));
            Assert.That(MtBits.Resolve(-1, "junk"), Is.EqualTo(MtBits.Default));
            Assert.That(MtBits.Default & MtBits.Verify, Is.Zero, "verify mode is never on by default");
        }

        [Test]
        public void RenderTuningParsesTheSwitch()
        {
            var o = RenderTuningOptions.Parse(new[] { "-g3npclod", "1", "-g3mt", "0x81" });
            Assert.That(o.MtMask, Is.EqualTo(0x81));
            Assert.That(o.Describe(), Does.Contain("mt=129"));
            Assert.That(RenderTuningOptions.Parse(new[] { "-g3mt" }).MtMask, Is.EqualTo(-1));
            Assert.That(RenderTuningOptions.Merge(new RenderTuningOptions(), RenderTuningOptions.Parse(new[] { "-g3mt", "2" })).MtMask, Is.EqualTo(2));
            Assert.That(MtBits.Describe(0), Is.EqualTo("off"));
            Assert.That(MtBits.Describe(MtBits.BodySnapshot | MtBits.TierJob | MtBits.Verify), Is.EqualTo("snap+tier+verify"));
        }

        [Test]
        public void GridQueryKeepsEveryNearIndexInListOrder()
        {
            var r = new Random(7);
            var grid = new IndexGrid();
            var hits = new System.Collections.Generic.List<int>();
            for (int round = 0; round < 50; round++)
            {
                int n = r.Next(0, 400);
                var xs = new float[n];
                var zs = new float[n];
                grid.Clear(20f);
                for (int i = 0; i < n; i++)
                {
                    xs[i] = (float)(r.NextDouble() * 400 - 200);
                    zs[i] = (float)(r.NextDouble() * 400 - 200);
                    grid.Add(i, xs[i], zs[i]);
                }
                for (int q = 0; q < 40; q++)
                {
                    float x = (float)(r.NextDouble() * 440 - 220), z = (float)(r.NextDouble() * 440 - 220), radius = (float)(r.NextDouble() * 19 + 0.5);
                    grid.Query(x, z, radius, hits);
                    for (int k = 1; k < hits.Count; k++) Assert.That(hits[k], Is.GreaterThan(hits[k - 1]), "ascending, no duplicates");
                    for (int i = 0; i < n; i++)
                    {
                        float dx = xs[i] - x, dz = zs[i] - z;
                        if (dx * dx + dz * dz <= radius * radius) Assert.That(hits.Contains(i), $"index {i} within {radius} m is returned");
                    }
                }
            }
            grid.Clear(20f);
            grid.Add(0, 1, 1);
            grid.Add(1, 500, 500);
            grid.Query(float.NaN, 0, 5, hits);
            Assert.That(hits, Is.EqualTo(new[] { 0, 1 }), "NaN position: everything, as the full scan");
        }
    }
}
