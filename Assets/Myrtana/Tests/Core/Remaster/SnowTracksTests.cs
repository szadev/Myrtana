using System;
using System.Collections.Generic;
using G3.Unity;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>fix-178: persistent snow tracks (store, budget, cover by snowfall, age cap, save) and the sparse snow glints.</summary>
    public class SnowTracksTests
    {
        [SetUp]
        public void Defaults() => WorldRemasterLook.Current = WorldLookSettings.Default;

        static int Add(SnowTrackStore s, float x, float z, double now, int kind = SnowV2.Paw, float side = 1) =>
            s.Add(x, z, 1, 0, 0.06f, 0.05f, 100f, now, 0.8f, kind, side, 0.6f, out _);

        [Test]
        public void Budget_OldestDroppedFirst_SpatialHashFindsTheLiveOnes()
        {
            var s = new SnowTrackStore(100);
            for (int i = 0; i < 150; i++) Add(s, i * 0.5f, 0, i);
            Assert.That(s.Count, Is.EqualTo(100));
            var near = new List<int>();
            s.Near(0, 0, 20, near);   // prints 0..40 (x 0..20 m) were made first: 0..49 are gone
            Assert.That(near, Is.Empty, "the oldest 50 were dropped");
            s.Near(50, 0, 5.01f, near);   // x 45..55 = prints 90..110
            Assert.That(near.Count, Is.EqualTo(21));
            foreach (int i in near) Assert.That(s.Made[i], Is.GreaterThanOrEqualTo(50));
            s.Near(-1000, 3000, 30, near);
            Assert.That(near, Is.Empty);
        }

        [Test]
        public void Prints_StayWithoutSnowfall_AreCoveredBySnowfall_AndCappedByAge()
        {
            var s = new SnowTrackStore(64);
            int a = Add(s, 1, 1, 0);
            var removed = new List<(int, int)>();
            s.Step(0, 100000, 300);   // a dry day: nothing fills them (no wind refill while persistent)
            Assert.That(s.Expire(1000, 3 * 86400, 64, removed), Is.EqualTo(0));
            Assert.That(s.Alive[a], Is.True);
            s.Step(1, 300, 300);      // 5 min of full snowfall: one cover, the print is filled but not yet gone
            Assert.That(s.Age(a), Is.EqualTo(1f).Within(1e-4));
            Assert.That(SnowV2.Fade(s.Age(a)), Is.EqualTo(0f).Within(1e-4), "the shader shows it filled");
            Assert.That(s.Expire(1300, 3 * 86400, 64, removed), Is.EqualTo(0));
            s.Step(1, 90, 300);
            s.Window[a] = 7;
            Assert.That(s.Expire(1400, 3 * 86400, 64, removed), Is.EqualTo(1), "gone at AgeGone");
            Assert.That(removed, Is.EqualTo(new List<(int, int)> { (a, 7) }), "its GPU window slot is reported");
            Assert.That(s.Count, Is.EqualTo(0));

            int b = Add(s, 2, 2, 10000);   // made after the snow: fresh
            Assert.That(s.Age(b), Is.EqualTo(0f));
            Assert.That(s.Expire(10000 + 3 * 86400 - 1, 3 * 86400, 64, removed), Is.EqualTo(0));
            Assert.That(s.Expire(10000 + 3 * 86400 + 1, 3 * 86400, 64, removed), Is.EqualTo(1), "older than the cap (game days)");
        }

        [Test]
        public void Save_RoundTripsCompactly_AgesRelativeToTheGameClock()
        {
            var rnd = new Random(5);
            var s = new SnowTrackStore(60000);
            s.Step(1, 120, 300);   // clock 0.4
            float x = 400, z = 1040;
            for (int i = 0; i < 50000; i++)
            {
                // a few dozen walkers' trails, interleaved like the runtime adds them
                x += (float)(rnd.NextDouble() - 0.45) * 1.5f; z += (float)(rnd.NextDouble() - 0.5) * 1.5f;
                double ang = rnd.NextDouble() * Math.PI * 2;
                s.Add(x + (i % 37) * 3f, z, (float)Math.Cos(ang), (float)Math.Sin(ang), i % 2 == 0 ? 0.144f : 0.066f, i % 2 == 0 ? 0.056f : 0.054f,
                      128.37f + (i % 50) * 0.01f, 20000 + i * 0.1, i % 2 == 0 ? 1f : 0.8f, i % 2 == 0 ? SnowV2.Boot : SnowV2.Paw, i % 3 == 0 ? -1 : 1, 0.6f, out _);
                if (i % 10000 == 0) s.Step(1, 3, 300);
            }
            byte[] data = s.Write(30000);
            Assert.That(data.Length / 50000.0, Is.LessThan(14), $"bytes per print ({data.Length} for 50000)");

            var t = new SnowTrackStore(60000);
            t.Read(data, 100000);   // loaded at another game time
            Assert.That(t.Count, Is.EqualTo(s.Count));
            Assert.That(t.Clock, Is.EqualTo(s.Clock).Within(1e-9));
            int checkedN = 0;
            for (int i = 0; i < 50000; i += 997)
            {
                // the store keeps the ring order (oldest first) on load: the i-th print is the i-th again
                Assert.That(t.X[i], Is.EqualTo(s.X[i]).Within(0.006f));
                Assert.That(t.Z[i], Is.EqualTo(s.Z[i]).Within(0.006f));
                Assert.That(t.Y[i], Is.EqualTo(s.Y[i]).Within(0.006f));
                float dot = t.DirX[i] * s.DirX[i] + t.DirZ[i] * s.DirZ[i];
                Assert.That(dot, Is.GreaterThan(Math.Cos(1.5 * Math.PI / 180)));
                Assert.That(t.HalfLength[i], Is.EqualTo(s.HalfLength[i]).Within(0.006f));
                Assert.That(t.Kind[i], Is.EqualTo(s.Kind[i]));
                Assert.That(t.Side[i], Is.EqualTo(s.Side[i]));
                Assert.That(t.Depth[i], Is.EqualTo(s.Depth[i]).Within(0.003f));
                Assert.That(100000 - t.Made[i], Is.EqualTo(30000 - s.Made[i]).Within(31), "age in game time kept");
                Assert.That(t.Age(i), Is.EqualTo(s.Age(i)).Within(1e-4), "cover age kept");
                checkedN++;
            }
            Assert.That(checkedN, Is.GreaterThan(40));
            var near = new List<int>();
            t.Near(t.X[123], t.Z[123], 0.01f, near);
            Assert.That(near, Does.Contain(123), "the spatial hash is rebuilt on load");

            var small = new SnowTrackStore(1000);
            small.Read(data, 100000);
            Assert.That(small.Count, Is.EqualTo(1000), "a smaller budget keeps the newest");
            for (int i = 0; i < 1000; i++) Assert.That(small.Made[i], Is.GreaterThan(100000 - (30000 - (20000 + 49000 * 0.1)) - 31), "only the newest 1000");

            var empty = new SnowTrackStore(16);
            empty.Read(new SnowTrackStore(16).Write(0), 0);
            Assert.That(empty.Count, Is.EqualTo(0));

            // perf-2: capture (main thread) + encode (worker) gives the same bytes; a reused snapshot is overwritten
            var snap = small.Capture(5, s.Capture(30000));
            Assert.That(snap.Count, Is.EqualTo(1000));
            Assert.That(SnowTrackStore.Encode(snap), Is.EqualTo(small.Write(5)));
            Assert.That(SnowTrackStore.Encode(s.Capture(30000, snap)), Is.EqualTo(data));
        }

        [Test]
        public void SavedTracks_KeepEachPrintsKindAndSide_ThroughCaptureAndEncode()
        {
            // fix-193: boots stay boots, paws paws, round trails round, each with its foot (perf-2's capture/encode path)
            var s = new SnowTrackStore(64);
            int[] kinds = { SnowV2.Boot, SnowV2.Paw, SnowV2.Round };
            for (int i = 0; i < 30; i++)
                s.Add(10 + i * 0.4f, 20, 1, 0, 0.14f, 0.05f, 100, 50 + i, 1f, kinds[i % 3], i % 2 == 0 ? -1 : 1, 0.6f, out _);
            var data = SnowTrackStore.Encode(s.Capture(200));
            Assert.That(data, Is.EqualTo(s.Write(200)));
            var t = new SnowTrackStore(64);
            t.Read(data, 200);
            Assert.That(t.Count, Is.EqualTo(30));
            for (int i = 0; i < 30; i++)
            {
                Assert.That(t.Kind[i], Is.EqualTo(kinds[i % 3]), $"print {i} kind");
                Assert.That(t.Side[i], Is.EqualTo(i % 2 == 0 ? -1 : 1), $"print {i} side");
            }
        }

        [Test]
        public void CoarseTrail_OnePrintPerStride_AlternatingFeet_NoneAcrossAJump()
        {
            var steps = new List<(float x, float z, float dx, float dz, float side)>();
            float carry = 0, foot = 1;
            SnowTrackStore.StrideSteps(0, 0, 0, 10, 1.5f, 0.11f, 30, ref carry, ref foot, steps);
            Assert.That(steps.Count, Is.EqualTo(6));
            for (int i = 0; i < steps.Count; i++)
            {
                Assert.That(steps[i].z, Is.EqualTo(1.5f * (i + 1)).Within(1e-4));
                Assert.That(Math.Abs(steps[i].x), Is.EqualTo(0.11f).Within(1e-4));
                Assert.That(steps[i].dz, Is.EqualTo(1f).Within(1e-5));
                if (i > 0) Assert.That(steps[i].side, Is.EqualTo(-steps[i - 1].side));
                Assert.That(Math.Sign(steps[i].x), Is.EqualTo(Math.Sign(steps[i].side)), "right foot to the right of the walk (+x when walking +z)");
            }
            Assert.That(carry, Is.EqualTo(1f).Within(1e-4));
            SnowTrackStore.StrideSteps(0, 10, 0, 10.6f, 1.5f, 0.11f, 30, ref carry, ref foot, steps);
            Assert.That(steps.Count, Is.EqualTo(1), "the stride carries over between samples");
            Assert.That(steps[0].z, Is.EqualTo(10.5f).Within(1e-4));
            SnowTrackStore.StrideSteps(0, 10.6f, 0, 200, 1.5f, 0.11f, 30, ref carry, ref foot, steps);
            Assert.That(steps, Is.Empty, "a teleport / load jump leaves no trail");
            Assert.That(SnowTrackStore.IsBiped("Human") && SnowTrackStore.IsBiped("Orc") && !SnowTrackStore.IsBiped("Wolf") && !SnowTrackStore.IsBiped("Boar"));
        }

        [Test]
        public void Glints_AreSparse_AndSpreadOverTheSnow_NotAPatchAtTheMirrorDirection()
        {
            int N = 400000;
            double Share(float hx, float hy, float hz)
            {
                double sum = 0;
                var r = new Random(11);
                for (int i = 0; i < N; i++) sum += SnowV2.GlintLobe(0, 1, 0, hx, hy, hz, (float)r.NextDouble(), (float)r.NextDouble()) > 0.5f ? 1 : 0;
                return sum / N;
            }
            double mirror = Share(0, 1, 0);                                              // the surface's own mirror direction
            double off30 = Share((float)Math.Sin(0.52), (float)Math.Cos(0.52), 0);      // 30 deg away from it
            Assert.That(mirror, Is.LessThan(0.01), "sparse");
            Assert.That(mirror, Is.GreaterThan(0.0002), "but there");
            Assert.That(off30 / mirror, Is.GreaterThan(0.5), "as many 30 deg off the highlight: no wet-looking lobe of glints");
        }

        [Test]
        public void GlintGrains_FarFromTheOrigin_AreUncorrelated_NoRows()
        {
            // fix-194: 3 mm grains ~1 km from the origin (ids ~3e5) and a negative row: the facet numbers stay uniform and
            // neighbouring grains (along x = the rows the owner saw) independent
            foreach (int z in new[] { 350000, -2 })
            {
                int n = 8192, lit = 0;
                double mean = 0, lag = 0, prev = 0;
                for (int i = 0; i < n; i++)
                {
                    var (r0, _, r2) = SnowV2.GrainHash(140000 + i, z, 0x3B800000u);   // salt = asuint(2^-8)
                    Assert.That(r0, Is.InRange(0f, 1f));
                    if (r2 < 0.25f) lit++;
                    mean += r0;
                    if (i > 0) lag += (r0 - 0.5) * (prev - 0.5);
                    prev = r0;
                }
                mean /= n; lag /= (n - 1) / 12.0;   // lag-1 autocorrelation (variance of U(0,1) = 1/12)
                Assert.That(mean, Is.EqualTo(0.5).Within(0.02));
                Assert.That(lit / (double)n, Is.EqualTo(0.25).Within(0.02), "density share");
                Assert.That(Math.Abs(lag), Is.LessThan(0.05), "neighbours independent");
            }
            Assert.That(SnowV2.GrainHash(5, 7, 1).r0, Is.Not.EqualTo(SnowV2.GrainHash(5, 7, 2).r0), "levels salted");
        }
    }
}
