using System.Linq;
using G3.Unity;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>MetaHuman hero head (Docs/remaster-metahuman.md §5): ARKit names, viseme table, fit, blinks.</summary>
    public class FaceMorphsTests
    {
        [Test]
        public void ArkitHas52UniqueNamesAndSpeechIsASubset()
        {
            Assert.That(ArkitShapes.Names.Length, Is.EqualTo(52));
            Assert.That(ArkitShapes.Names.Distinct().Count(), Is.EqualTo(52));
            Assert.That(ArkitShapes.Speech.All(s => ArkitShapes.Names.Contains(s)), Is.True);
        }

        [TestCase("jawOpen", 1)]
        [TestCase("JAWOPEN", 1)]
        [TestCase("eyeBlinkLeft", 2)]
        [TestCase("mouthClose", -1)]
        public void FindsShapesByNameOrPrefixedSuffix(string shape, int index)
        {
            var mesh = new[] { "head_lod0_mesh.mouthOpenish", "head_lod0_mesh.jawOpen", "ARKit_eyeBlinkLeft", "xmouthClose" };
            Assert.That(ArkitShapes.Find(mesh, shape), Is.EqualTo(index));
        }

        [Test]
        public void EvaluateBlendsAndScales()
        {
            var t = new VisemeMorphTable(new[] { "jawOpen", "mouthPucker" }, 3);
            t.Set(1, 0, 0f); t.Set(2, 0, 0.8f); t.Set(2, 1, 0.4f);
            var r = new float[2];
            t.Evaluate(1, 2, 0.5f, 1f, r);
            Assert.That(r[0], Is.EqualTo(0.4f).Within(1e-6));
            Assert.That(r[1], Is.EqualTo(0.2f).Within(1e-6));
            t.Evaluate(2, 2, 0f, 0.5f, r);
            Assert.That(r[0], Is.EqualTo(0.4f).Within(1e-6));
            t.Evaluate(-5, 99, 1f, 1f, r); // clamped to 0 and 2
            Assert.That(r[0], Is.EqualTo(0.8f).Within(1e-6));
        }

        [Test]
        public void TextFormRoundTripsAndRejectsBadRows()
        {
            var t = new VisemeMorphTable(new[] { "jawOpen", "mouthPucker" }, 14);
            t.Set(7, 0, 0.55f); t.Set(13, 1, 1f);
            var u = VisemeMorphTable.Parse("# hero\n" + t.ToText());
            Assert.That(u.Shapes, Is.EqualTo(t.Shapes));
            Assert.That(u.VisemeCount, Is.EqualTo(14));
            Assert.That(u.Get(7, 0), Is.EqualTo(0.55f).Within(1e-4));
            Assert.That(u.Get(13, 1), Is.EqualTo(1f));
            Assert.That(VisemeMorphTable.Parse("visemes 1\nshapes jawOpen\n3 2.5").Get(3, 0), Is.EqualTo(1f)); // clamped
            Assert.Throws<System.FormatException>(() => VisemeMorphTable.Parse("visemes 1\nshapes a b\n1 0.5"));
            Assert.Throws<System.FormatException>(() => VisemeMorphTable.Parse("1 0.5"));
        }

        [Test]
        public void FitRecoversKnownWeightsWithinBox()
        {
            var shapes = new[] { "jawOpen", "mouthPucker", "mouthStretchLeft" };
            var resp = new[]
            {
                new float[] { 25, 2.0f, 0, 0, 0, 0.5f, 0, 0 },
                new float[] { 0, 0.2f, -1.5f, 1.0f, 0, 0, 0, 0 },
                new float[] { 0, 0, 0.8f, 0, 0, 0, 0.2f, 0 },
            };
            float[] Mix(float a, float b, float c) =>
                Enumerable.Range(0, MouthFeatures.Count).Select(k => a * resp[0][k] + b * resp[1][k] + c * resp[2][k]).ToArray();
            var targets = new[] { Mix(0, 0, 0), Mix(0.6f, 0.3f, 0f), Mix(1.4f, 0f, 0.5f) }; // last one exceeds the box
            var t = VisemeFit.Fit(shapes, targets, resp, ridge: 1e-6f);
            Assert.That(t.Get(0, 0), Is.EqualTo(0f).Within(1e-3));
            Assert.That(t.Get(1, 0), Is.EqualTo(0.6f).Within(1e-2));
            Assert.That(t.Get(1, 1), Is.EqualTo(0.3f).Within(1e-2));
            Assert.That(t.Get(2, 0), Is.EqualTo(1f).Within(1e-6));
            Assert.That(t.Get(2, 2), Is.GreaterThan(0.3f));
        }

        [Test]
        public void BlinksAreDeterministicSpacedAndShaped()
        {
            var a = new BlinkSchedule(7); var b = new BlinkSchedule(7);
            int blinks = 0; bool closed = false; float lastStart = -10, minGap = 99;
            for (int i = 0; i < 60 * 120; i++) // 2 minutes at 60 fps
            {
                float t = i / 60f, x = a.Sample(t);
                Assert.That(b.Sample(t), Is.EqualTo(x));
                Assert.That(x, Is.InRange(0f, 1f));
                if (x > 0.5f && !closed) { blinks++; if (t - lastStart < minGap) minGap = t - lastStart; lastStart = t; }
                closed = x > 0.5f;
            }
            Assert.That(blinks, Is.InRange(15, 70));
            Assert.That(minGap, Is.GreaterThan(BlinkSchedule.DoubleGap - 0.05f));
            Assert.That(BlinkSchedule.Shape(BlinkSchedule.Close + 0.01f), Is.EqualTo(1f));
            Assert.That(BlinkSchedule.Shape(BlinkSchedule.Duration), Is.EqualTo(0f));
        }
    }
}
