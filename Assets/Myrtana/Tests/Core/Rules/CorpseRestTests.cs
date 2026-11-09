using System;
using System.Collections.Generic;
using System.Linq;
using Myrtana.Rules;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>fix-80: a corpse rests on the ground (CorpseRestRules: ground plane, tilt limit, rest offset).</summary>
    public sealed class CorpseRestTests
    {
        static List<(float x, float y, float z)> Grid(Func<float, float, float> height, float size = 2f, int n = 4)
        {
            var list = new List<(float, float, float)>();
            for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                float x = -size / 2 + size * i / (n - 1), z = -size / 2 + size * j / (n - 1);
                list.Add((x, height(x, z), z));
            }
            return list;
        }

        [Test]
        public void FitPlane_FlatAndSlope()
        {
            Assert.That(CorpseRestRules.FitPlane(Grid((x, z) => 5f), out var n, out float a, out float b, out float c), Is.True);
            Assert.That(n.y, Is.EqualTo(1f).Within(1e-4f));
            Assert.That(c, Is.EqualTo(5f).Within(1e-4f));
            // 20° up along +x
            float k = (float)Math.Tan(20 * Math.PI / 180);
            Assert.That(CorpseRestRules.FitPlane(Grid((x, z) => 1f + k * x), out n, out a, out b, out c), Is.True);
            Assert.That(a, Is.EqualTo(k).Within(1e-4f));
            Assert.That(b, Is.EqualTo(0f).Within(1e-4f));
            Assert.That(Math.Acos(n.y) * 180 / Math.PI, Is.EqualTo(20).Within(0.01));
            Assert.That(n.x, Is.LessThan(0f), "the normal leans away from the uphill side");
        }

        [Test]
        public void FitPlane_NeedsThreeSpreadSamples()
        {
            Assert.That(CorpseRestRules.FitPlane(new[] { (0f, 0f, 0f), (1f, 0f, 0f) }, out _, out _, out _, out _), Is.False);
            Assert.That(CorpseRestRules.FitPlane(new[] { (0f, 0f, 0f), (1f, 0f, 0f), (2f, 0f, 0f) }, out _, out _, out _, out _), Is.False,
                        "samples on one line give no plane");
        }

        [Test]
        public void LimitTilt_CapsSteepGround()
        {
            var steep = ((float)-Math.Sin(60 * Math.PI / 180), (float)Math.Cos(60 * Math.PI / 180), 0f);
            var n = CorpseRestRules.LimitTilt(steep);
            Assert.That(Math.Acos(n.y) * 180 / Math.PI, Is.EqualTo(CorpseRestRules.MaxTiltDegrees).Within(0.01));
            Assert.That(n.x, Is.LessThan(0f), "same direction");
            Assert.That(Math.Sqrt(n.x * n.x + n.y * n.y + n.z * n.z), Is.EqualTo(1).Within(1e-5));
            var gentle = CorpseRestRules.LimitTilt((0f, 0.98f, 0.2f));
            Assert.That(gentle.z, Is.EqualTo(0.2f / Math.Sqrt(0.98 * 0.98 + 0.04)).Within(1e-4));
            Assert.That(CorpseRestRules.LimitTilt((0f, -1f, 0f)).y, Is.EqualTo(1f), "a downward normal is turned up");
        }

        [Test]
        public void RestOffset_PoseTouchesTheGround()
        {
            // the authored LieDead pose: skin from -11 cm (under the ground) up to 36 cm
            var gaps = Enumerable.Range(0, 100).Select(i => -0.11f + 0.47f * i / 99f).ToList();
            float up = CorpseRestRules.RestOffset(gaps, ragdoll: false);
            Assert.That(up, Is.GreaterThan(0.1f), "lifted out of the ground");
            Assert.That(gaps.Select(g => g + up).OrderBy(g => g).ElementAt(2), Is.GreaterThanOrEqualTo(CorpseRestRules.Contact - 1e-4f));
            // a pose floating 30 cm above a dip comes down
            var floating = gaps.Select(g => g + 0.41f).ToList();
            Assert.That(CorpseRestRules.RestOffset(floating, ragdoll: false), Is.LessThan(-0.25f));
        }

        [Test]
        public void RestOffset_PercentileIgnoresAFinger()
        {
            var gaps = Enumerable.Repeat(0.05f, 200).ToList();
            gaps[7] = -0.5f; // one finger in a hole
            Assert.That(CorpseRestRules.RestOffset(gaps, ragdoll: false), Is.EqualTo(CorpseRestRules.Contact - 0.05f).Within(1e-5f));
        }

        [Test]
        public void RestOffset_RagdollOnlyLiftsWhenSunk()
        {
            var lying = Enumerable.Range(0, 50).Select(i => -0.02f + 0.3f * i / 49f).ToList();
            Assert.That(CorpseRestRules.RestOffset(lying, ragdoll: true), Is.EqualTo(0f), "within the tolerance: left as physics laid it");
            var onRock = lying.Select(g => g + 0.5f).ToList();
            Assert.That(CorpseRestRules.RestOffset(onRock, ragdoll: true), Is.EqualTo(0f), "a ragdoll is never lowered");
            var sunk = lying.Select(g => g - 0.6f).ToList();
            Assert.That(CorpseRestRules.RestOffset(sunk, ragdoll: true), Is.EqualTo(0.62f).Within(0.01f));
            var deep = lying.Select(g => g - 9f).ToList();
            Assert.That(CorpseRestRules.RestOffset(deep, ragdoll: true), Is.EqualTo(CorpseRestRules.MaxLift), "capped");
        }

        [Test]
        public void GridHeight_Bilinear_PlaneFillsGaps()
        {
            // 3 x 2 grid, step 1, from (0,0): heights rise 1 per metre in x
            var h = new[] { 0f, 1f, 2f, 0f, float.NaN, 2f };
            Assert.That(CorpseRestRules.GridHeight(h, 3, 2, 0, 0, 1, 0.5f, 0f, 1, 0, 0), Is.EqualTo(0.5f).Within(1e-5f));
            Assert.That(CorpseRestRules.GridHeight(h, 3, 2, 0, 0, 1, 1f, 1f, 1, 0, 0), Is.EqualTo(1f).Within(1e-5f), "missing sample: plane");
            Assert.That(CorpseRestRules.GridHeight(h, 3, 2, 0, 0, 1, 5f, 0.5f, 1, 0, 0), Is.EqualTo(2f).Within(1e-5f), "outside: border");
            Assert.That(CorpseRestRules.Percentile(new[] { 3f, 1f, 2f }, 0f), Is.EqualTo(1f));
        }

        [Test]
        public void DropRaisedOutliers_BarrelTopBesideTheBodyIsNoGround()
        {
            // fix-166 (b1019d, Vengard yard): dirt 6.08-6.33 under the orc, a barrel and a ruin wall beside it (5x5 grid, 0.3 m)
            var h = new[] { 6.09f, 6.12f, 6.15f, 6.18f, 7.85f, 6.12f, 6.15f, 6.18f, 6.21f, 6.23f, 6.16f, 6.19f, 6.21f, 6.24f, 6.26f,
                            6.19f, 6.22f, 6.25f, 7.39f, 7.19f, 6.23f, 6.26f, 6.28f, 7.22f, 6.33f };
            Assert.That(CorpseRestRules.DropRaisedOutliers(h, 5, 5, 0f, 0f, 0.3f), Is.EqualTo(4));
            Assert.That(h.Where(v => !float.IsNaN(v)).Max(), Is.LessThan(6.4f));
        }

        [Test]
        public void DropRaisedOutliers_SlopeAndStepStay()
        {
            // fix80 slope (Nordmar, 22-33°): 4 x 5 samples 0.35 m apart rising 0.4 m per step along x
            var slope = new float[20];
            for (int iz = 0; iz < 5; iz++)
            for (int ix = 0; ix < 4; ix++) slope[iz * 4 + ix] = 178f + 0.4f * ix + 0.05f * iz;
            Assert.That(CorpseRestRules.DropRaisedOutliers(slope, 4, 5, 0f, 0f, 0.35f), Is.EqualTo(0));
            // a stair step of 0.3 m across the footprint
            var step = new float[16];
            for (int iz = 0; iz < 4; iz++)
            for (int ix = 0; ix < 4; ix++) step[iz * 4 + ix] = ix < 2 ? 5f : 5.3f;
            Assert.That(CorpseRestRules.DropRaisedOutliers(step, 4, 4, 0f, 0f, 0.3f), Is.EqualTo(0));
            // too few samples: nothing dropped
            Assert.That(CorpseRestRules.DropRaisedOutliers(new[] { 1f, 9f, float.NaN, 1f }, 2, 2, 0f, 0f, 1f), Is.EqualTo(0));
        }

        [Test]
        public void DropWallSamples_RockFaceBesideTheBodyIsNoGround()
        {
            // fix-232 (fix180 Nordmar, x 446.3): 20° slope ground, a rock face from x >= 0.8 m rising 4-12 m; floor at (0.2, 178.3, 0.6)
            var h = new float[20];
            for (int iz = 0; iz < 4; iz++)
            for (int ix = 0; ix < 5; ix++)
            {
                float x = ix * 0.4f;
                h[iz * 5 + ix] = x >= 0.8f ? 182f + ix : 178.3f + 0.36f * (x - 0.2f);
            }
            Assert.That(CorpseRestRules.DropWallSamples(h, 5, 4, 0f, 0f, 0.4f, 0.2f, 178.3f, 0.6f), Is.EqualTo(12));
            Assert.That(h.Where(v => !float.IsNaN(v)).Max(), Is.LessThan(178.5f));
            Assert.That(CorpseRestRules.FitPlane(Enumerable.Range(0, 20).Where(i => !float.IsNaN(h[i]))
                                                     .Select(i => ((i % 5) * 0.4f, h[i], (i / 5) * 0.4f)).ToList(),
                                                 out var n, out _, out _, out _), Is.True);
            Assert.That(Math.Acos(n.y) * 180 / Math.PI, Is.LessThan(25.0), "the slope, not the face");
        }

        [Test]
        public void DropWallSamples_SlopesUpTo45AndStepsStay()
        {
            // a 40° slope rising from the floor and a 0.3 m stair step 0.3 m away stay ground
            var slope = new float[20];
            float tan40 = (float)Math.Tan(40 * Math.PI / 180);
            for (int iz = 0; iz < 4; iz++)
            for (int ix = 0; ix < 5; ix++) slope[iz * 5 + ix] = 10f + tan40 * Math.Abs(ix * 0.4f - 0.8f);
            Assert.That(CorpseRestRules.DropWallSamples(slope, 5, 4, 0f, 0f, 0.4f, 0.8f, 10f, 0.6f), Is.EqualTo(0));
            var step = new float[16];
            for (int iz = 0; iz < 4; iz++)
            for (int ix = 0; ix < 4; ix++) step[iz * 4 + ix] = ix < 2 ? 5f : 5.3f;
            Assert.That(CorpseRestRules.DropWallSamples(step, 4, 4, 0f, 0f, 0.3f, 0.3f, 5f, 0.45f), Is.EqualTo(0));
            // a body down in a narrow gully (fewer than 3 heights left): nothing dropped
            Assert.That(CorpseRestRules.DropWallSamples(new[] { 5f, 9f, 9f, 9f }, 2, 2, 0f, 0f, 1f, 0f, 5f, 0f), Is.EqualTo(0));
        }

        // fix-221: a ragdoll on an 18-33 deg slope is lifted only by its collision parts' real penetration, never by its skin
        static readonly float Cos30 = (float)Math.Cos(30 * Math.PI / 180);

        [Test]
        public void RagdollLift_RestingPartsOnASlopeAreNotLifted()
        {
            // every part's sphere touches the 30 deg ground (clearance ~0); the feet's skin 0.4 m in the hill is not a part
            var parts = new List<float>
            {
                CorpseRestRules.PartClearance(0.15f / Cos30, 0.15f, Cos30), CorpseRestRules.PartClearance(0.08f / Cos30 - 0.02f, 0.08f, Cos30),
                CorpseRestRules.PartClearance(0.3f, 0.11f, Cos30),
            };
            Assert.That(CorpseRestRules.RagdollLift(parts, Cos30), Is.EqualTo(0f));
            Assert.That(CorpseRestRules.LowestClearance(parts), Is.GreaterThan(-CorpseRestRules.RagdollTolerance));
        }

        [Test]
        public void RagdollLift_DeepestPartLiftsVerticallyOnTheSlope()
        {
            // the chest 0.2 m (along the normal) in the drawn relief: lifted 0.2 / cos 30 = 0.231 m, the shallower part ignored
            var parts = new List<float> { -0.05f, -0.2f, 0.1f };
            Assert.That(CorpseRestRules.RagdollLift(parts, Cos30), Is.EqualTo(0.2f / Cos30).Within(1e-4f));
            Assert.That(CorpseRestRules.RagdollLift(new List<float> { -0.2f }, 1f), Is.EqualTo(0.2f).Within(1e-4f));
        }

        [Test]
        public void RagdollLift_NeverLowersAndIsCapped()
        {
            Assert.That(CorpseRestRules.RagdollLift(new List<float> { 0.5f, 0.3f }, 1f), Is.EqualTo(0f), "floating parts: never lowered");
            Assert.That(CorpseRestRules.RagdollLift(new List<float> { -0.02f }, 1f), Is.EqualTo(0f), "within the tolerance");
            Assert.That(CorpseRestRules.RagdollLift(new List<float> { -9f }, 1f), Is.EqualTo(CorpseRestRules.MaxLift));
            Assert.That(CorpseRestRules.RagdollLift(new List<float>(), 1f), Is.EqualTo(0f));
            Assert.That(CorpseRestRules.RagdollLift(null, 1f), Is.EqualTo(0f));
            Assert.That(CorpseRestRules.RagdollLift(new List<float> { float.NaN }, 1f), Is.EqualTo(0f));
            Assert.That(float.IsNaN(CorpseRestRules.LowestClearance(new List<float>())), Is.True);
        }

        [Test]
        public void PartClearance_AlongTheNormal()
        {
            Assert.That(CorpseRestRules.PartClearance(0.3f, 0.1f, 1f), Is.EqualTo(0.2f).Within(1e-5f));
            // a sphere centre r / cos over a slope touches it
            Assert.That(CorpseRestRules.PartClearance(0.1f / Cos30, 0.1f, Cos30), Is.EqualTo(0f).Within(1e-5f));
            Assert.That(CorpseRestRules.PartClearance(0f, 0.1f, Cos30), Is.EqualTo(-0.1f).Within(1e-5f));
        }
    }
}
