using System;
using G3.Formats.Remaster;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>FACES v6 in-engine look of the generated materials (RemasterLook.cs): eye gain, hair cap, skin profile.</summary>
    public class RemasterLookTests
    {
        /// <summary>A square eye texture like eyegen.py's: iris disc, sclera ring of one sRGB grey, dark lid band on top.</summary>
        static byte[] EyeTexture(int n, byte sclera, byte iris, byte lid, out int size)
        {
            size = n;
            var px = new byte[n * n * 4];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float u = (x + 0.5f) / n * 2 - 1, v = (y + 0.5f) / n * 2 - 1;
                    float r = (float)Math.Sqrt(u * u + v * v);
                    byte g = r < 0.53f ? iris : v > 0.7f ? lid : sclera; // the lid band covers < 25 % of the ring
                    int i = (y * n + x) * 4;
                    px[i] = px[i + 1] = px[i + 2] = g;
                    px[i + 3] = 255;
                }
            return px;
        }

        [Test]
        public void SrgbDecodeMatchesTheStandardCurve()
        {
            Assert.That(RemasterEyeLook.SrgbToLinear(0f), Is.EqualTo(0f));
            Assert.That(RemasterEyeLook.SrgbToLinear(1f), Is.EqualTo(1f).Within(1e-5));
            Assert.That(RemasterEyeLook.SrgbToLinear(0.5f), Is.EqualTo(0.214f).Within(0.001));
            Assert.That(RemasterEyeLook.SrgbToLinear(0.40f), Is.EqualTo(0.133f).Within(0.001)); // eyegen's SCLERA
        }

        [Test]
        public void ScleraLuminanceReadsTheRingNotTheIrisOrTheLidShadow()
        {
            var px = EyeTexture(128, 102, 20, 10, out int n); // 102 = eyegen's 0.40 sRGB sclera
            float lum = RemasterEyeLook.ScleraLuminance(px, n, n);
            Assert.That(lum, Is.EqualTo(RemasterEyeLook.SrgbToLinear(102 / 255f)).Within(0.002));
        }

        [Test]
        public void TheGeneratedEyeIsBroughtToTheG3ScleraLevel()
        {
            // measured on the v6 eye sets: sclera p75 0.068 linear vs G3_Animation_Head_Eyes_01 p75 0.200
            Assert.That(RemasterEyeLook.AlbedoGain(0.068f), Is.EqualTo(2.94f).Within(0.01));
            Assert.That(RemasterEyeLook.AlbedoGain(0.30f), Is.EqualTo(1f), "never darkened");
            Assert.That(RemasterEyeLook.AlbedoGain(0.01f), Is.EqualTo(RemasterEyeLook.MaxGain), "capped");
            Assert.That(RemasterEyeLook.AlbedoGain(-1f), Is.EqualTo(1f), "unreadable texture: unchanged");
            Assert.That(RemasterEyeLook.ScleraLuminance(null, 4, 4), Is.EqualTo(-1f));
            Assert.That(RemasterEyeLook.ScleraLuminance(new byte[8], 4, 4), Is.EqualTo(-1f), "short buffer");
        }

        [Test]
        public void EyeShadowIsSampledInFrontOfTheLidsNotInTheSocket()
        {
            // the shadow map texel (~1.2 cm) is larger than the lid gap; the sample point must clear the ~1-2 cm socket
            Assert.That(RemasterEyeLook.ShadowSampleOffset, Is.InRange(0.015f, 0.04f));
            Assert.That(RemasterEyeLook.SsaoStrength, Is.InRange(0f, 0.5f));
        }

        [TestCase("G3_Remaster_Hero_HairCap", "G3_Remaster_Hero_HairCards")]
        [TestCase("G3_Remaster_Hair_Myrtana_01_S3_HairCap", "G3_Remaster_Hair_Myrtana_01_S3_HairCards")]
        [TestCase("G3_Remaster_Hero_HairCards", null)]
        [TestCase(null, null)]
        public void CapFindsItsCards(string cap, string cards) =>
            Assert.That(RemasterHairLook.CardsSetOfCap(cap), Is.EqualTo(cards));

        [Test]
        public void CapIsLiftedTowardsItsCards()
        {
            // hero: cap 0.05 sRGB (0.0039 linear) under cards 0.15 sRGB (0.019): x3.9 (fix-52: 0.8 of the cards)
            float cap = RemasterEyeLook.SrgbToLinear(0.05f), cards = RemasterEyeLook.SrgbToLinear(0.148f);
            Assert.That(RemasterHairLook.CapGain(cap, cards), Is.EqualTo(RemasterHairLook.CapToCards * cards / cap).Within(1e-4).And.InRange(3.5f, 4f));
            // Milten (editor-runner-30: dark spot under the quiff): cap 0.0101 / cards 0.0532 linear -> the maximum, cap at
            // 3/4 of its cards instead of 1/2
            Assert.That(RemasterHairLook.CapGain(0.0101f, 0.0532f), Is.EqualTo(RemasterHairLook.MaxCapGain));
            Assert.That(RemasterHairLook.CapGain(0.0101f, 0.0532f) * 0.0101f / 0.0532f, Is.GreaterThan(0.7f));
            Assert.That(RemasterHairLook.CapGain(0.05f, 0.06f), Is.EqualTo(1f), "a cap already near its cards stays");
            Assert.That(RemasterHairLook.CapGain(0.0001f, 0.2f), Is.EqualTo(RemasterHairLook.MaxCapGain));
            Assert.That(RemasterHairLook.CapGain(-1f, 0.2f), Is.EqualTo(1f));
            Assert.That(RemasterHairLook.CapGain(0.01f, -1f), Is.EqualTo(1f), "no cards set: unchanged");
        }

        [Test]
        public void MedianLuminanceSkipsCutAwayTexels()
        {
            // 4x4: 12 transparent white texels, 4 opaque dark ones
            var px = new byte[16 * 4];
            for (int i = 0; i < 16; i++)
            {
                byte g = i < 4 ? (byte)50 : (byte)255;
                px[i * 4] = px[i * 4 + 1] = px[i * 4 + 2] = g;
                px[i * 4 + 3] = i < 4 ? (byte)255 : (byte)0;
            }
            Assert.That(RemasterHairLook.MedianLuminance(px, 4, 4), Is.EqualTo(RemasterEyeLook.SrgbToLinear(50 / 255f)).Within(1e-4));
            Assert.That(RemasterHairLook.MedianLuminance(px, 4, 4, 0f), Is.EqualTo(1f).Within(1e-4), "alpha ignored: white majority");
        }

        [Test]
        public void CapCutoffFollowsTheSet()
        {
            Assert.That(RemasterHairLook.CapCutoff(0.5f), Is.EqualTo(0.5f)); // hairmesh caps, like the game and Blender
            Assert.That(RemasterHairLook.CapCutoff(null), Is.EqualTo(RemasterHairLook.DefaultCapCutoff));
            Assert.That(RemasterHairLook.CapCutoff(-1f), Is.EqualTo(RemasterHairLook.DefaultCapCutoff));
        }

        [TestCase(RemasterGeneratedKind.HairCards, false)]
        [TestCase(RemasterGeneratedKind.HairCap, true)]
        [TestCase(RemasterGeneratedKind.Eye, true)]
        public void OnlyAlphaTestedCardsSkipTheShadowMap(RemasterGeneratedKind kind, bool casts) =>
            Assert.That(RemasterHairLook.CastsShadows(kind), Is.EqualTo(casts));

        [Test]
        public void FaceSkinMatchesTheBlenderLook()
        {
            var f = RemasterSkinProfile.Face;
            // primary lobe smoothness at the skin mean (facetex roughness 0.51 -> MaskMap_R2.A 0.49) within 0.02 of Blender's
            Assert.That(f.UnitySmoothness(0.49f), Is.EqualTo(RemasterSkinProfile.BlenderSmoothness(0.49f)).Within(0.02));
            Assert.That(f.BumpScale, Is.EqualTo(0.6f));            // facerender FACE_NORMAL_STRENGTH
            Assert.That(f.DetailNormalScale, Is.EqualTo(0f));       // pores already in BumpMap_R2
            Assert.That(f.SkinF0, Is.EqualTo(0.04f).Within(1e-6));   // Specular IOR level 0.5
            Assert.That(f.PrimaryLobe, Is.GreaterThan(RemasterSkinProfile.Body.PrimaryLobe));
            Assert.That(f.ScatterSaturation, Is.GreaterThan(RemasterSkinProfile.Body.ScatterSaturation));
            // fix-52: warm ambient + a little sheen in shade (NPC heads matte/grey vs the sunlit hero)
            Assert.That(f.AmbientWarmth, Is.InRange(0.3f, 0.7f));
            Assert.That(f.EnvSheen, Is.InRange(0.2f, 0.5f));
        }

        [Test]
        public void BodyProfileIsTheShaderDefault()
        {
            // the G3/Remaster/Skin property defaults (R1 r1c/r1d): bodies and G3 heads must render as before
            var b = RemasterSkinProfile.Body;
            Assert.That(new[] { b.BumpScale, b.DetailNormalScale, b.Smoothness, b.PrimaryLobe, b.SecondLobeSmoothness, b.SkinF0, b.Wrap, b.ScatterSaturation },
                        Is.EqualTo(new[] { 1f, 0.45f, 0.85f, 0.3f, 0.25f, 0.028f, 0.25f, 0.5f }));
            Assert.That(new[] { b.AmbientWarmth, b.EnvSheen }, Is.EqualTo(new[] { 0f, 0f }), "the shader defaults: R1 unchanged");
            Assert.That(RemasterSkinProfile.For(false), Is.EqualTo(b));
            Assert.That(RemasterSkinProfile.For(true), Is.EqualTo(RemasterSkinProfile.Face));
        }
    }
}
