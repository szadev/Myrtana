using System;
using System.Collections.Generic;
using System.Linq;
using G3.Formats.Remaster;
using G3.Unity;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>v2 hero showcase: material names, the eye repair on v2 heads, pre-integrated skin LUT, looks, light rig tuning.</summary>
    public class RemasterV2Tests
    {
        // ---- names ---------------------------------------------------------------------------------------------------

        [TestCase("G3_Remaster_V2_Skin__HeroFace", RemasterGeneratedKind.SkinV2)]
        [TestCase("G3_Remaster_V2_Skin__HeroBodySkin.001", RemasterGeneratedKind.SkinV2)]
        [TestCase("G3_Remaster_V2_Eye__Hero", RemasterGeneratedKind.EyeV2)]
        [TestCase("G3_Remaster_V2_Hair__Hero", RemasterGeneratedKind.HairV2)]
        [TestCase("G3_Remaster_V2_Stubble__Hero", RemasterGeneratedKind.Stubble)]
        [TestCase("G3_Remaster_V2_EyeOcclusion__Hero", RemasterGeneratedKind.EyeOcclusion)]
        [TestCase("G3_Remaster_V2_TearFilm", RemasterGeneratedKind.TearFilm)]
        [TestCase("G3_Remaster_V2_Surface__HeroBody", RemasterGeneratedKind.Surface)]
        [TestCase("G3_Remaster_V2_Unknown__Hero", RemasterGeneratedKind.None)]
        [TestCase("G3_Remaster_V2_Skin__", RemasterGeneratedKind.None)]
        [TestCase("G3_Remaster_Hero_HairCards", RemasterGeneratedKind.HairCards)] // v6 names unchanged
        [TestCase("G3_Remaster_Eye__G3_Animation_Head_Hero_01_S1_eye", RemasterGeneratedKind.Eye)]
        public void ClassifiesV2Names(string name, RemasterGeneratedKind kind) =>
            Assert.That(RemasterGenerated.Classify(name), Is.EqualTo(kind));

        [Test]
        public void V2NamesRoundTripAndMapToShaders()
        {
            foreach (var k in new[] { RemasterGeneratedKind.SkinV2, RemasterGeneratedKind.EyeV2, RemasterGeneratedKind.HairV2,
                                      RemasterGeneratedKind.Stubble, RemasterGeneratedKind.EyeOcclusion, RemasterGeneratedKind.TearFilm,
                                      RemasterGeneratedKind.Surface })
            {
                Assert.That(RemasterGenerated.IsV2(k), Is.True);
                Assert.That(RemasterGenerated.Classify(RemasterGenerated.V2Name(k, "Hero")), Is.EqualTo(k));
                Assert.That(RemasterGenerated.Classify(RemasterGenerated.V2Name(k, "")), Is.EqualTo(k));
                Assert.That(RemasterGenerated.ShaderName(k), Is.Not.Null);
            }
            Assert.That(RemasterGenerated.IsV2(RemasterGeneratedKind.HairCards), Is.False);
            Assert.That(RemasterGenerated.ShaderName(RemasterGeneratedKind.SkinV2), Is.EqualTo("G3/Remaster/V2/Skin"));
            Assert.That(RemasterGenerated.ShaderName(RemasterGeneratedKind.Stubble), Is.EqualTo("G3/Remaster/V2/Hair"));
            Assert.That(RemasterGenerated.ShaderName(RemasterGeneratedKind.TearFilm), Is.EqualTo("G3/Remaster/Cornea"));
            Assert.That(RemasterGenerated.ShaderName(RemasterGeneratedKind.Surface), Is.EqualTo("G3/Remaster/Armour"));
            Assert.That(RemasterGenerated.TextureSet("G3_Remaster_V2_TearFilm"), Is.Null);
            Assert.That(RemasterGenerated.TextureSet("G3_Remaster_V2_Skin__HeroFace.002"), Is.EqualTo("G3_Remaster_V2_Skin__HeroFace"));
            Assert.That(RemasterGenerated.IsCutout(RemasterGeneratedKind.HairV2), Is.True);
            Assert.That(RemasterGenerated.IsCutout(RemasterGeneratedKind.Stubble), Is.True);
            Assert.That(RemasterGenerated.IsCutout(RemasterGeneratedKind.SkinV2), Is.False);
            Assert.Throws<ArgumentException>(() => RemasterGenerated.V2Name(RemasterGeneratedKind.Eye, "x"));
        }

        /// <summary>runner-31 HV1: a v2 head with the G3 face material next to its v2 skin drew the G3 atlas as blotches.</summary>
        [Test]
        public void AV2HeadCarriesNoG3FaceMaterial()
        {
            var skin = RemasterGenerated.V2Name(RemasterGeneratedKind.SkinV2, "HeroFace");
            var eye = RemasterGenerated.V2Name(RemasterGeneratedKind.EyeV2, "Hero");
            Assert.That(RemasterGenerated.V2HeadForeignMaterials(new[] { skin, eye, "G3_Remaster_V2_TearFilm" }), Is.Empty);
            Assert.That(RemasterGenerated.V2HeadForeignMaterials(new[] { "G3_Human_HERO_Face_Diffuse_01_A", skin, eye }),
                        Is.EqualTo(new[] { "G3_Human_HERO_Face_Diffuse_01_A" }));
            // v6 heads (G3 skin + generated eyes) are not v2 heads: no rule
            Assert.That(RemasterGenerated.V2HeadForeignMaterials(new[] { "G3_Human_HERO_Face_Diffuse_01_A", "G3_Remaster_Eye__X_eye" }), Is.Empty);
            // and the contract check reports it
            var original = TestRig.Build();
            var v2 = TestRig.Build();
            var mesh = v2.Meshes.First(x => x.IsSkinned && x.SubMeshes.Count >= 2);
            mesh.SubMeshes[1].Material = skin;          // slot 0 keeps its G3 name next to the v2 skin
            var r = RigCheck.Compare(original, v2);
            Assert.That(r.Lines.Any(l => l.Check.EndsWith("v2 materials") && l.Level == RigCheck.Level.Fail), Is.True);
        }

        [Test]
        public void OnlySkinAndSurfacesCastShadows()
        {
            Assert.That(RemasterHairLook.CastsShadows(RemasterGeneratedKind.SkinV2), Is.True);
            Assert.That(RemasterHairLook.CastsShadows(RemasterGeneratedKind.Surface), Is.True);
            Assert.That(RemasterHairLook.CastsShadows(RemasterGeneratedKind.HairCap), Is.True);
            foreach (var k in new[] { RemasterGeneratedKind.HairCards, RemasterGeneratedKind.HairV2, RemasterGeneratedKind.Stubble,
                                      RemasterGeneratedKind.EyeOcclusion, RemasterGeneratedKind.TearFilm, RemasterGeneratedKind.EyeV2 })
                Assert.That(RemasterHairLook.CastsShadows(k), Is.False, k.ToString());
        }

        // ---- eye repair ----------------------------------------------------------------------------------------------

        /// <summary>A UV sphere (eyeball) at c with radius r; normals inward when <paramref name="inward"/>; Unity front-face winding outward unless <paramref name="invertWinding"/>.</summary>
        static void Sphere(List<float> pos, List<float> nrm, List<int> idx, double cx, double cy, double cz, double r, bool inward, bool invertWinding)
        {
            const int rings = 10, segs = 16;
            int b = pos.Count / 3;
            for (int i = 0; i <= rings; i++)
            {
                double th = Math.PI * i / rings;
                for (int j = 0; j < segs; j++)
                {
                    double ph = 2 * Math.PI * j / segs;
                    double x = Math.Sin(th) * Math.Cos(ph), y = Math.Cos(th), z = Math.Sin(th) * Math.Sin(ph);
                    pos.Add((float)(cx + r * x)); pos.Add((float)(cy + r * y)); pos.Add((float)(cz + r * z));
                    double s = inward ? -1 : 1;
                    nrm.Add((float)(s * x)); nrm.Add((float)(s * y)); nrm.Add((float)(s * z));
                }
            }
            for (int i = 0; i < rings; i++)
                for (int j = 0; j < segs; j++)
                {
                    int a = b + i * segs + j, c = b + i * segs + (j + 1) % segs, d = b + (i + 1) * segs + j, e = b + (i + 1) * segs + (j + 1) % segs;
                    foreach (var t in new[] { (a, c, d), (c, e, d) })
                    {
                        // orient so cross(b - a, c - a) points away from the centre (Unity front face), unless inverted
                        var (p, q, w) = t;
                        bool outward = Outward(pos, p, q, w, cx, cy, cz);
                        if (outward == invertWinding) (q, w) = (w, q);
                        idx.Add(p); idx.Add(q); idx.Add(w);
                    }
                }
        }

        static bool Outward(List<float> P, int a, int b, int c, double cx, double cy, double cz)
        {
            double ux = P[b * 3] - P[a * 3], uy = P[b * 3 + 1] - P[a * 3 + 1], uz = P[b * 3 + 2] - P[a * 3 + 2];
            double vx = P[c * 3] - P[a * 3], vy = P[c * 3 + 1] - P[a * 3 + 1], vz = P[c * 3 + 2] - P[a * 3 + 2];
            double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
            double mx = (P[a * 3] + P[b * 3] + P[c * 3]) / 3 - cx, my = (P[a * 3 + 1] + P[b * 3 + 1] + P[c * 3 + 1]) / 3 - cy,
                   mz = (P[a * 3 + 2] + P[b * 3 + 2] + P[c * 3 + 2]) / 3 - cz;
            return nx * mx + ny * my + nz * mz > 0;
        }

        static int Tri(List<float> pos, List<float> nrm, List<int> idx, params double[] xyz)
        {
            int b = pos.Count / 3;
            for (int i = 0; i < 9; i++) pos.Add((float)xyz[i]);
            for (int i = 0; i < 3; i++) { nrm.Add(0); nrm.Add(0); nrm.Add(1); }
            idx.Add(b); idx.Add(b + 1); idx.Add(b + 2);
            return b;
        }

        static RigMesh V2Head(List<float> pos, List<float> nrm, List<int> eye, List<int> skin, List<int> lash) =>
            new RigMesh
            {
                Name = "G3_Head_Hero_Hero_01", Positions = pos.ToArray(), Normals = nrm.ToArray(),
                SubMeshes =
                {
                    new RigSubMesh { Material = RemasterGenerated.V2Name(RemasterGeneratedKind.SkinV2, "HeroFace"), Indices = skin.ToArray() },
                    new RigSubMesh { Material = RemasterGenerated.V2Name(RemasterGeneratedKind.EyeV2, "Hero"), Indices = eye.ToArray() },
                    new RigSubMesh { Material = RemasterGenerated.V2Name(RemasterGeneratedKind.HairV2, "HeroLashes"), Indices = lash.ToArray() },
                },
            };

        /// <summary>The black-eyes repair (fix-52 RemasterEyes) also covers the v2 head: its eyeball / skin slot names.</summary>
        [Test]
        public void EyeRepairCoversTheV2Head()
        {
            var pos = new List<float>(); var nrm = new List<float>();
            var eye = new List<int>(); var skin = new List<int>(); var lash = new List<int>();
            const double r = 0.016;
            Sphere(pos, nrm, eye, 0.032, 1.83, 0.10, r, inward: true, invertWinding: false);
            Tri(pos, nrm, skin, 0.030, 1.83, 0.108, 0.036, 1.83, 0.108, 0.033, 1.835, 0.108);   // cap inside
            Tri(pos, nrm, skin, 0.05, 1.78, 0.10, 0.06, 1.78, 0.10, 0.055, 1.79, 0.10);         // cheek
            Tri(pos, nrm, lash, 0.031, 1.83, 0.105, 0.035, 1.83, 0.105, 0.033, 1.832, 0.105);   // lash card dipping in
            var m = V2Head(pos, nrm, eye, skin, lash);
            Assert.That(RemasterEyes.HasGeneratedEyes(m), Is.True);
            var fix = RemasterEyes.Repair(m);
            Assert.That(fix.Eyeballs.Count, Is.EqualTo(1));
            Assert.That(fix.Eyeballs[0].R, Is.EqualTo(r).Within(1e-4));
            Assert.That(fix.DroppedTriangles, Is.EqualTo(1));
            Assert.That(m.SubMeshes[0].Indices.Length, Is.EqualTo(3), "the cheek stays");
            Assert.That(m.SubMeshes[2].Indices.Length, Is.EqualTo(3), "lash cards are not skin");
            Assert.That(fix.FlippedNormals, Is.GreaterThan(0));
            Assert.That(RemasterEyes.Check(m).Passed, Is.True, RemasterEyes.Check(m).ToString());
        }

        [Test]
        public void ASkullSizedSphereIsNotAnEyeball()
        {
            var pos = new List<float>(); var nrm = new List<float>(); var big = new List<int>(); var skin = new List<int>();
            Sphere(pos, nrm, big, 0, 1.7, 0, 0.11, inward: false, invertWinding: false);
            Tri(pos, nrm, skin, 0, 1.7, 0.01, 0.01, 1.7, 0.01, 0, 1.71, 0.01);
            var m = V2Head(pos, nrm, big, skin, new List<int>());
            Assert.That(RemasterEyes.Eyeballs(m), Is.Empty);
            Assert.That(RemasterEyes.Repair(m).DroppedTriangles, Is.EqualTo(0));
        }

        // ---- skin LUT ------------------------------------------------------------------------------------------------

        [Test]
        public void FlatSkinIsLambert()
        {
            var rgb = new double[3];
            foreach (var nl in new[] { -0.5, 0.0, 0.3, 0.7, 1.0 })
            {
                RemasterSkinLut.Integrate(nl, double.PositiveInfinity, rgb);
                Assert.That(rgb[0], Is.EqualTo(Math.Max(0, nl)).Within(1e-9));
                RemasterSkinLut.Integrate(nl, 500.0, rgb); // 50 cm radius: practically flat
                Assert.That(rgb[0], Is.EqualTo(Math.Max(0, nl)).Within(0.02));
                Assert.That(rgb[2], Is.EqualTo(Math.Max(0, nl)).Within(0.02));
            }
        }

        [Test]
        public void CurvedSkinCarriesRedPastTheTerminator()
        {
            var rgb = new double[3];
            RemasterSkinLut.Integrate(-0.05, 3.0, rgb); // just past the terminator on a 3 mm radius (ear rim)
            Assert.That(rgb[0], Is.GreaterThan(0.05));
            Assert.That(rgb[0], Is.GreaterThan(rgb[1] * 1.5));
            Assert.That(rgb[1], Is.GreaterThanOrEqualTo(rgb[2] * 0.9));
            // energy: the fully lit side is a little darker than Lambert (light leaves towards the dark side)
            RemasterSkinLut.Integrate(1.0, 3.0, rgb);
            Assert.That(rgb[0], Is.LessThan(1.0));
            Assert.That(rgb[0], Is.GreaterThan(0.6));
        }

        [Test]
        public void LutIsBoundedAndMonotonicInNdotL()
        {
            const int w = 32, h = 16;
            var t = RemasterSkinLut.Compute(w, h, 128);
            Assert.That(t.Length, Is.EqualTo(w * h * 3));
            for (int y = 0; y < h; y++)
                for (int c = 0; c < 3; c++)
                    for (int x = 0; x < w; x++)
                    {
                        float v = t[(y * w + x) * 3 + c];
                        Assert.That(v, Is.InRange(0f, 1f));
                        if (x > 0) Assert.That(v, Is.GreaterThanOrEqualTo(t[(y * w + x - 1) * 3 + c] - 1e-4f));
                    }
            // row 0 (flat) at N·L <= 0 is black; the most curved row is not
            Assert.That(t[(0 * w + w / 2 - 1) * 3], Is.EqualTo(0f).Within(1e-6));
            Assert.That(t[((h - 1) * w + w / 2 - 1) * 3], Is.GreaterThan(0.05f));
        }

        [Test]
        public void LutCoordinatesAndPngRows()
        {
            Assert.That(RemasterSkinLut.LutV(0), Is.EqualTo(0));
            Assert.That(RemasterSkinLut.LutV(RemasterSkinLut.MaxCurvature), Is.EqualTo(1));
            Assert.That(RemasterSkinLut.LutV(1e6), Is.EqualTo(1));
            Assert.That(RemasterSkinLut.LutV(RemasterSkinLut.MaxCurvature / 4), Is.EqualTo(0.5).Within(1e-12));
            Assert.That(RemasterSkinLut.RadiusMm(1), Is.EqualTo(2.0).Within(1e-9));
            Assert.That(RemasterSkinLut.RadiusMm(RemasterSkinLut.LutV(25)), Is.EqualTo(40.0).Within(1e-6)); // c 25/m = 4 cm
            // PNG: top row = v 1, values stored as sqrt
            var table = new float[] { 0.25f, 0.25f, 0.25f, /* row v=1 */ 1f, 0.04f, 0f };
            var px = RemasterSkinLut.ToRgba(table, 1, 2);
            Assert.That(px[0], Is.EqualTo(255));
            Assert.That(px[1], Is.EqualTo(51));
            Assert.That(px[4], Is.EqualTo(128));
            Assert.That(px[7], Is.EqualTo(255));
        }

        // ---- looks ---------------------------------------------------------------------------------------------------

        [Test]
        public void SkinLooksAreDualLobeAndPlausible()
        {
            var f = RemasterV2Look.Floats(RemasterGeneratedKind.SkinV2);
            Assert.That(f["_Lobe2Roughness"], Is.GreaterThan(f["_Lobe1Roughness"]));
            Assert.That(f["_LobeMix"], Is.InRange(0.05f, 0.5f));
            Assert.That(f["_SkinF0"], Is.InRange(0.02f, 0.04f));
            Assert.That(f["_BakedCurvature"], Is.EqualTo(1f));
            Assert.That(f["_NormalBlurR"], Is.GreaterThan(f["_NormalBlurG"]));
            Assert.That(f["_NormalBlurG"], Is.GreaterThan(f["_NormalBlurB"]));
            var body = RemasterV2Look.Floats(RemasterGeneratedKind.SkinV2, bodySkin: true);
            Assert.That(body["_BakedCurvature"], Is.EqualTo(0f));
            Assert.That(body["_TransmissionStrength"], Is.LessThan(f["_TransmissionStrength"]));
            Assert.That(RemasterV2Look.Colors(RemasterGeneratedKind.SkinV2)["_TransmissionColor"][0], Is.EqualTo(1f));
            Assert.That(RemasterV2Look.IsBodySkin("G3_Remaster_V2_Skin__HeroBodySkin"), Is.True);
            Assert.That(RemasterV2Look.IsBodySkin("G3_Remaster_V2_Skin__HeroFace"), Is.False);
        }

        [Test]
        public void HairAndEyeLooks()
        {
            var h = RemasterV2Look.Floats(RemasterGeneratedKind.HairV2);
            Assert.That(h["_Cutoff"], Is.GreaterThan(h["_FringeCutoff"]));
            var s = RemasterV2Look.Floats(RemasterGeneratedKind.Stubble);
            Assert.That(s["_Cutoff"], Is.GreaterThan(s["_FringeCutoff"]));
            var e = RemasterV2Look.Floats(RemasterGeneratedKind.EyeV2);
            Assert.That(e["_CorneaRadius"], Is.GreaterThan(e["_IrisRadius"]));
            Assert.That(e["_IOR"], Is.EqualTo(1.336f).Within(1e-4));
            Assert.That(RemasterV2Look.EyeRadius(0.0118f), Is.EqualTo(0.0118f));
            Assert.That(RemasterV2Look.EyeRadius(0.16f), Is.EqualTo(e["_EyeRadius"]));
            Assert.That(RemasterV2Look.EyeRadius(null), Is.EqualTo(e["_EyeRadius"]));
            Assert.That(RemasterV2Look.Floats(RemasterGeneratedKind.HairCards), Is.Empty);
            var srf = RemasterV2Look.Floats(RemasterGeneratedKind.Surface);
            Assert.That(srf["_MetalDiffuse"], Is.EqualTo(0f));
            Assert.That(srf["_DarkSpecular"], Is.EqualTo(1f));
        }

        [Test]
        public void EyesAreAliveButNotGlowing()
        {
            var e = RemasterV2Look.Floats(RemasterGeneratedKind.EyeV2);
            Assert.That(e["_CatchlightStrength"], Is.GreaterThan(0f).And.LessThanOrEqualTo(6f));
            Assert.That(e["_CatchlightSmoothness"], Is.InRange(0.8f, 0.97f));
            Assert.That(e["_CatchlightUp"], Is.GreaterThan(0f));   // light from above, as the sky
            Assert.That(e["_LimbusDark"], Is.InRange(0.15f, 0.8f));
            Assert.That(e["_IrisGain"], Is.InRange(0.5f, 3f));
            Assert.That(e["_ScleraGain"], Is.InRange(0.5f, 1.5f));
            Assert.That(e["_CaruncleGain"], Is.LessThan(1f));
            Assert.That(e["_IrisAO"], Is.InRange(0f, 0.6f));
            Assert.That(RemasterV2Look.Floats(RemasterGeneratedKind.TearFilm)["_EnvSpecular"], Is.GreaterThan(0.6f));
            // iris / cornea radius follow the set's eye_v2.json (hero: iris 0.46, cornea 0.51)
            Assert.That(RemasterV2Look.IrisRadius(0.46f), Is.EqualTo(0.46f));
            Assert.That(RemasterV2Look.IrisRadius(0.9f), Is.EqualTo(e["_IrisRadius"]));
            Assert.That(RemasterV2Look.IrisRadius(null), Is.EqualTo(e["_IrisRadius"]));
            Assert.That(RemasterV2Look.CorneaRadius(0.51f, 0.46f), Is.EqualTo(0.51f));
            Assert.That(RemasterV2Look.CorneaRadius(0.4f, 0.46f), Is.GreaterThan(0.46f));
            Assert.That(RemasterV2Look.CorneaRadius(null, 0.6f), Is.GreaterThan(0.6f));
        }

        [Test]
        public void HairShellIsMatteAndNormalMapped()
        {
            Assert.That(RemasterV2Look.IsHairShell("G3_Remaster_V2_Hair__HeroShell"), Is.True);
            Assert.That(RemasterV2Look.IsHairShell("G3_Remaster_V2_Hair__HeroBrows"), Is.False);
            Assert.That(RemasterV2Look.IsHairShell(null), Is.False);
            var cards = RemasterV2Look.Floats(RemasterGeneratedKind.HairV2);
            var shell = RemasterV2Look.Floats(RemasterGeneratedKind.HairV2, hairShell: true);
            Assert.That(cards["_HairShell"], Is.EqualTo(0f));
            Assert.That(shell["_HairShell"], Is.EqualTo(1f));
            Assert.That(shell["_BumpScale"], Is.GreaterThan(0f));
            Assert.That(shell["_Cutoff"], Is.GreaterThan(shell["_FringeCutoff"]));
            Assert.That(cards["_HairSpecCap"], Is.LessThanOrEqualTo(0.2f), "owner: hair read shiny");
        }

        // ---- light rig -----------------------------------------------------------------------------------------------

        static readonly float[] Sun = { 1f, 0.95f, 0.85f }, Sky = { 0.6f, 0.7f, 1f };

        [Test]
        public void FillLiftsTheShadowSideByDayAndStaysLowAtNight()
        {
            var noon = CharacterLightTuning.Compute(1.5f, 60f, Sun, 0.3f, Sky, 0f);
            float lit = 1.5f * (float)Math.Sin(Math.PI / 3) + 0.3f;
            Assert.That(noon.FillIntensity, Is.EqualTo(CharacterLightTuning.ShadowSideShare * lit - 0.3f).Within(1e-4));
            var night = CharacterLightTuning.Compute(0f, -10f, Sun, 0.05f, Sky, 0f);
            Assert.That(night.FillIntensity, Is.EqualTo(CharacterLightTuning.FillMin));
            Assert.That(night.RimIntensity, Is.EqualTo(CharacterLightTuning.RimMin));
            Assert.That(noon.RimIntensity, Is.GreaterThan(night.RimIntensity));
            Assert.That(noon.RimIntensity, Is.LessThanOrEqualTo(CharacterLightTuning.RimMax));
        }

        [Test]
        public void BacklightCutsTheRimAndTintsFollowTheLight()
        {
            var front = CharacterLightTuning.Compute(1.2f, 40f, Sun, 0.4f, Sky, 0f);
            var back = CharacterLightTuning.Compute(1.2f, 40f, Sun, 0.4f, Sky, 1f);
            Assert.That(back.RimIntensity, Is.LessThan(front.RimIntensity));
            // by day the rim leans to the sun's warm tint, at night to the sky's blue
            var night = CharacterLightTuning.Compute(0f, 0f, Sun, 0.1f, Sky, 0f);
            Assert.That(front.RimColor[2] / front.RimColor[0], Is.LessThan(night.RimColor[2] / night.RimColor[0]));
            Assert.That(night.RimColor[2], Is.GreaterThan(night.RimColor[0]));
            var noon = CharacterLightTuning.Compute(3f, 70f, Sun, 0.2f, Sky, 0f);
            Assert.That(noon.RimColor[0], Is.GreaterThan(noon.RimColor[2]));
            Assert.That(front.RimColor.Max(), Is.EqualTo(1f).Within(1e-5));
            Assert.That(CharacterLightTuning.Normalised(new[] { 0f, 0f, 0f }), Is.EqualTo(new[] { 1f, 1f, 1f }));
        }
    }
}
