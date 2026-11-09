using System.Linq;
using System.Numerics;
using G3.Unity.Characters;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>fix-246: a beard / moustache takes the bone weights and speech shapes of the head skin under it.</summary>
    public class FacialHairSkinTests
    {
        const int Head = 0, Jaw = 1, UpperLip = 2;

        // A face "front" in the XY plane (z = 0): an upper strip (y 0..2) on the head bone, a chin strip (y -2..0) on the
        // jaw. Vertices: 0 (-1,2) 1 (1,2) 2 (-1,0) 3 (1,0) 4 (-1,-2) 5 (1,-2).
        static readonly Vector3[] Skin =
        {
            new Vector3(-1, 2, 0), new Vector3(1, 2, 0), new Vector3(-1, 0, 0), new Vector3(1, 0, 0), new Vector3(-1, -2, 0), new Vector3(1, -2, 0),
        };
        static readonly int[] SkinTris = { 0, 2, 1, 1, 2, 3, 2, 4, 3, 3, 4, 5 };
        static readonly BoneWeights4[] SkinWeights =
        {
            new BoneWeights4(Head, 1), new BoneWeights4(Head, 1), new BoneWeights4(Head, 0.5f, Jaw, 0.5f),
            new BoneWeights4(Head, 0.5f, Jaw, 0.5f), new BoneWeights4(Jaw, 1), new BoneWeights4(Jaw, 1),
        };

        [Test]
        public void ClosestPointOnTriangleCoversFaceEdgeAndCorner()
        {
            Vector3 a = new Vector3(0, 0, 0), b = new Vector3(2, 0, 0), c = new Vector3(0, 2, 0);
            var q = FacialHairSkin.ClosestPointOnTriangle(new Vector3(0.5f, 0.5f, 3), a, b, c, out float u, out float v, out float w);
            Assert.That(Vector3.Distance(q, new Vector3(0.5f, 0.5f, 0)), Is.LessThan(1e-5f));
            Assert.That(u, Is.EqualTo(0.5f).Within(1e-5f));
            Assert.That(v, Is.EqualTo(0.25f).Within(1e-5f));
            Assert.That(w, Is.EqualTo(0.25f).Within(1e-5f));
            q = FacialHairSkin.ClosestPointOnTriangle(new Vector3(1, -5, 0), a, b, c, out u, out v, out w); // below edge ab
            Assert.That(Vector3.Distance(q, new Vector3(1, 0, 0)), Is.LessThan(1e-5f));
            Assert.That(w, Is.EqualTo(0f));
            q = FacialHairSkin.ClosestPointOnTriangle(new Vector3(-3, -3, 1), a, b, c, out u, out v, out w); // corner a
            Assert.That(q, Is.EqualTo(a));
            Assert.That(u, Is.EqualTo(1f));
        }

        [Test]
        public void MapFindsTheNearestTriangleAgainstBruteForce()
        {
            // a bumpy 12 x 12 sheet and random query points around it: the grid search equals the brute force minimum
            int n = 12;
            var verts = new Vector3[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                    verts[y * n + x] = new Vector3(x * 0.01f, y * 0.01f, 0.003f * (float)System.Math.Sin(x * 1.3 + y * 0.7));
            var tris = Enumerable.Range(0, (n - 1) * (n - 1)).SelectMany(i =>
            {
                int x = i % (n - 1), y = i / (n - 1), v0 = y * n + x;
                return new[] { v0, v0 + n, v0 + 1, v0 + 1, v0 + n, v0 + n + 1 };
            }).ToArray();
            var rnd = new System.Random(246);
            var pts = Enumerable.Range(0, 200).Select(_ => new Vector3((float)rnd.NextDouble() * 0.16f - 0.03f,
                (float)rnd.NextDouble() * 0.16f - 0.03f, (float)rnd.NextDouble() * 0.06f - 0.03f)).ToArray();
            var map = FacialHairSkin.Map(verts, tris, pts);
            for (int i = 0; i < pts.Length; i++)
            {
                float best = float.MaxValue;
                for (int t = 0; t < tris.Length; t += 3)
                {
                    var q = FacialHairSkin.ClosestPointOnTriangle(pts[i], verts[tris[t]], verts[tris[t + 1]], verts[tris[t + 2]], out _, out _, out _);
                    best = System.Math.Min(best, Vector3.Distance(pts[i], q));
                }
                Assert.That(map[i].Distance, Is.EqualTo(best).Within(1e-6f), $"point {i}");
                var p = map[i];
                var onSurface = verts[p.A] * p.WA + verts[p.B] * p.WB + verts[p.C] * p.WC;
                Assert.That(Vector3.Distance(onSurface, pts[i]), Is.EqualTo(best).Within(1e-5f));
            }
        }

        [Test]
        public void ChinHairFollowsTheJawAndCheekHairTheHead()
        {
            // beard points in front of the face: one on the chin, one at the mouth line, one high on the cheek
            var hair = new[] { new Vector3(0, -1.8f, 0.4f), new Vector3(0, 0, 0.4f), new Vector3(0, 1.9f, 0.4f) };
            var w = FacialHairSkin.TransferWeights(Skin, SkinTris, SkinWeights, 3, hair, null, Head, smoothIterations: 0);
            Assert.That(w[0].WeightOf(Jaw), Is.EqualTo(0.95f).Within(1e-4f)); // 90 % of the way from the mouth line to the chin
            Assert.That(w[1].WeightOf(Jaw), Is.EqualTo(0.5f).Within(1e-4f));
            Assert.That(w[2].WeightOf(Head), Is.EqualTo(0.975f).Within(1e-4f)); // 5 % of the way down to the mouth line
            foreach (var x in w) Assert.That(x.W0 + x.W1 + x.W2 + x.W3, Is.EqualTo(1f).Within(1e-5f));
        }

        [Test]
        public void SmoothingPullsNeighbouringStrandsTogether()
        {
            // a strand chain 0-1-2-3 from the cheek down to the chin (edges from degenerate triangles); 4 is a seam copy of 3
            var hair = new[]
            {
                new Vector3(0, 1.9f, 0.1f), new Vector3(0, 0.6f, 0.1f), new Vector3(0, -0.6f, 0.1f), new Vector3(0, -1.9f, 0.1f),
                new Vector3(0, -1.9f, 0.1f),
            };
            var hairTris = new[] { 0, 1, 1, 1, 2, 2, 2, 3, 3, 3, 4, 4 };
            var raw = FacialHairSkin.TransferWeights(Skin, SkinTris, SkinWeights, 3, hair, hairTris, Head, smoothIterations: 0);
            var smooth = FacialHairSkin.TransferWeights(Skin, SkinTris, SkinWeights, 3, hair, hairTris, Head, smoothIterations: 2);
            float MaxGap(BoneWeights4[] w) => Enumerable.Range(0, 3).Max(i => w[i + 1].WeightOf(Jaw) - w[i].WeightOf(Jaw));
            for (int i = 0; i < 3; i++) Assert.That(smooth[i + 1].WeightOf(Jaw), Is.GreaterThan(smooth[i].WeightOf(Jaw)), "still ordered");
            Assert.That(MaxGap(smooth), Is.LessThan(MaxGap(raw)));
            Assert.That(smooth[4].WeightOf(Jaw), Is.EqualTo(smooth[3].WeightOf(Jaw)).Within(1e-5f), "welded copies stay together");
        }

        [Test]
        public void Top4KeepsTheStrongestAndRenormalises()
        {
            var dense = new float[] { 0.05f, 0.4f, 0.1f, 0.3f, 0.2f, 0f };
            var w = FacialHairSkin.Top4(dense, 0, 6, 0);
            Assert.That(new[] { w.B0, w.B1, w.B2, w.B3 }, Is.EqualTo(new[] { 1, 3, 4, 2 }));
            Assert.That(w.W0 + w.W1 + w.W2 + w.W3, Is.EqualTo(1f).Within(1e-6f));
            Assert.That(w.W0, Is.EqualTo(0.4f / 1.0f).Within(1e-6f));
            var empty = FacialHairSkin.Top4(new float[6], 0, 6, UpperLip);
            Assert.That(empty.B0, Is.EqualTo(UpperLip));
            Assert.That(empty.W0, Is.EqualTo(1f));
        }

        [Test]
        public void NoSkinTrianglesFallsBackToTheAnchorBone()
        {
            var w = FacialHairSkin.TransferWeights(Skin, new int[0], SkinWeights, 3, new[] { Vector3.Zero }, null, Head);
            Assert.That(w[0].WeightOf(Head), Is.EqualTo(1f));
        }

        [Test]
        public void SpeechShapeDeltasCarryOverToTheBeard()
        {
            // jawOpen moves the chin row down 1 cm, the mouth line 0.5 cm
            var deltas = new[]
            {
                Vector3.Zero, Vector3.Zero, new Vector3(0, -0.5f, 0), new Vector3(0, -0.5f, 0), new Vector3(0, -1, 0), new Vector3(0, -1, 0),
            };
            var hair = new[] { new Vector3(0, -2, 0.3f), new Vector3(0, 2, 0.3f) };
            var map = FacialHairSkin.Map(Skin, SkinTris, hair);
            var d = FacialHairSkin.TransferDeltas(map, deltas, FacialHairSkin.Neighbours(hair, null, 1e-4f));
            Assert.That(d[0].Y, Is.EqualTo(-1f).Within(1e-5f));
            Assert.That(d[1].Y, Is.EqualTo(0f).Within(1e-5f));
        }

        [Test]
        public void BonesOutsideTheHeadGoToTheAnchor()
        {
            // bones: 0 head (anchor), 1 jaw under it, 2 neck = the head's parent; the chin skin is half jaw, half neck
            var parents = new[] { 2, 0, -1 };
            var allowed = FacialHairSkin.Subtree(parents, Head);
            Assert.That(allowed, Is.EqualTo(new[] { true, true, false }));
            var weights = Enumerable.Repeat(new BoneWeights4(Jaw, 0.5f, 2, 0.5f), Skin.Length).ToArray();
            var w = FacialHairSkin.TransferWeights(Skin, SkinTris, weights, 3, new[] { new Vector3(0, -1, 0.2f) }, null, Head,
                                                   smoothIterations: 0, allowed: allowed);
            Assert.That(w[0].WeightOf(2), Is.EqualTo(0f));
            Assert.That(w[0].WeightOf(Jaw), Is.EqualTo(0.5f).Within(1e-5f));
            Assert.That(w[0].WeightOf(Head), Is.EqualTo(0.5f).Within(1e-5f));
        }

        [TestCase("G3_Animation_Head_Beard_Myrtana_03_01", "G3_Animation_Head_Beard_Myrtana_03_01", new[] { "Slot_Beard", "Hero_Head_Head_1" }, true)]
        [TestCase("remaster_part", "whatever", new[] { "Slot_Beard", "Hero_Head_Head_1" }, true)]
        [TestCase("NPC_Mustache_Cards", "cards", new[] { "Hero_Head_Head_1" }, true)]
        [TestCase("G3_Animation_Head_Hair_Myrtana_01", "G3_Animation_Head_Hair_Myrtana_01", new[] { "Slot_Hair", "Hero_Head_Head_1" }, false)]
        [TestCase("Beard_Of_Helmet", "x", new[] { "Slot_Helmet", "Hero_Head_Head_1" }, false)]
        [TestCase("It_Sword", "It_Sword", new[] { "Slot_RightHand_Weapon" }, false)]
        public void FindsFacialHairBySlotOrName(string obj, string mesh, string[] ancestors, bool expected) =>
            Assert.That(FacialHairSkin.IsFacialHair(obj, mesh, ancestors), Is.EqualTo(expected));

        [TestCase("G3_Animation_Head_Main_01", true)]
        [TestCase("G3_Animation_Head_Mouth_01", false)]
        [TestCase("G3_Animation_Head_Eyes_01", false)]
        [TestCase("MH_Teeth", false)]
        [TestCase(null, true)]
        public void SkinSubmeshesExcludeMouthAndEyes(string material, bool skin) =>
            Assert.That(FacialHairSkin.IsSkinMaterial(material), Is.EqualTo(skin));
    }
}
