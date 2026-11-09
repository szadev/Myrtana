using System.Collections.Generic;
using G3.Unity;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>Player command-line switches of the streamed world's render tuning (G3RenderTuning, G3PerfProbe).</summary>
    public class RenderTuningOptionsTests
    {
        [Test]
        public void Defaults_ProbesOff_DitherFade_DefaultShadows_NoProfile()
        {
            var o = RenderTuningOptions.Parse(new[] { "G3World.exe", "-g3perf" });
            Assert.AreEqual(WorldProbeMode.Off, o.Probes);
            Assert.IsTrue(o.TreeCrossFade);
            Assert.AreEqual(-1, o.PointShadows);
            Assert.AreEqual(0, o.ProfileFrames);
            Assert.AreEqual("probes=off fade=dither pshadows=def", o.Describe());
        }

        [Test]
        public void Null_GivesDefaults()
        {
            Assert.AreEqual(WorldProbeMode.Off, RenderTuningOptions.Parse(null).Probes);
        }

        [Test]
        public void ParsesAllSwitches_CaseInsensitive()
        {
            var o = RenderTuningOptions.Parse(new[] { "-g3probes", "BLEND", "-g3TreeFade", "0", "-g3pointshadows", "0", "-g3perfprofile", "60" });
            Assert.AreEqual(WorldProbeMode.Blend, o.Probes);
            Assert.IsFalse(o.TreeCrossFade);
            Assert.AreEqual(0, o.PointShadows);
            Assert.AreEqual(60, o.ProfileFrames);
            Assert.AreEqual("probes=blend fade=instant pshadows=0", o.Describe());
        }

        [Test]
        public void ShadowSwitches_AreParsedClamped_AndDescribed()
        {
            var o = RenderTuningOptions.Parse(new[] { "-g3cascades", "2", "-g3shadowsmall", "1.5", "-g3smallmesh", "99" });
            Assert.AreEqual(2, o.Cascades);
            Assert.AreEqual(1.5f, o.ShadowSmallMesh);
            Assert.AreEqual(20f, o.SmallMesh);
            Assert.AreEqual("probes=off fade=dither pshadows=def cascades=2 shadowsmall=1.5 smallmesh=20", o.Describe());
            Assert.AreEqual(4, RenderTuningOptions.Parse(new[] { "-g3cascades", "9" }).Cascades);
        }

        [Test]
        public void ShadowSwitches_DefaultToAsset_AndIgnoreBadNumbers()
        {
            var o = RenderTuningOptions.Parse(new[] { "-g3shadowsmall", "x", "-g3smallmesh", "NaN" });
            Assert.AreEqual(0, o.Cascades);
            Assert.Less(o.ShadowSmallMesh, 0f);
            Assert.Less(o.SmallMesh, 0f);
        }

        [Test]
        public void LightDistance_IsParsedClamped_AndDescribed()
        {
            Assert.Less(RenderTuningOptions.Parse(new[] { "-g3perf" }).LightDistance, 0f);
            var o = RenderTuningOptions.Parse(new[] { "-g3lightdist", "45" });
            Assert.AreEqual(45f, o.LightDistance);
            Assert.AreEqual("probes=off fade=dither pshadows=def lightdist=45", o.Describe());
            Assert.AreEqual(0f, RenderTuningOptions.Parse(new[] { "-g3lightdist", "-3" }).LightDistance);
        }

        [Test]
        public void LightLod_FullNearFadesInTheBandOffBeyond()
        {
            Assert.AreEqual(1f, LightLod.Factor(-2f, 60f));
            Assert.AreEqual(1f, LightLod.Factor(50f, 60f));
            Assert.AreEqual(0.5f, LightLod.Factor(55f, 60f), 1e-5f);
            Assert.AreEqual(0f, LightLod.Factor(60f, 60f));
            Assert.AreEqual(0f, LightLod.Factor(500f, 60f));
            Assert.AreEqual(1f, LightLod.Factor(500f, 0f), "0 = no distance culling");
            Assert.AreEqual(0.5f, LightLod.Factor(2f, 4f), 1e-5f, "a distance below the band fades over the whole distance");
        }

        [Test]
        public void ProbesKeep()
        {
            Assert.AreEqual(WorldProbeMode.Keep, RenderTuningOptions.Parse(new[] { "-g3probes", "keep" }).Probes);
        }

        [Test]
        public void PerfProfile_WithoutNumber_UsesDefault_AndDoesNotEatNextSwitch()
        {
            var o = RenderTuningOptions.Parse(new[] { "-g3perfprofile", "-g3probes", "blend" });
            Assert.AreEqual(RenderTuningOptions.DefaultProfileFrames, o.ProfileFrames);
            Assert.AreEqual(WorldProbeMode.Blend, o.Probes);
        }

        [Test]
        public void BadValues_AreIgnored()
        {
            var o = RenderTuningOptions.Parse(new[] { "-g3probes", "maybe", "-g3treefade", "x", "-g3pointshadows", "-7", "-g3pointshadows" });
            Assert.AreEqual(WorldProbeMode.Off, o.Probes);
            Assert.IsTrue(o.TreeCrossFade);
            Assert.AreEqual(-1, o.PointShadows);   // clamped, and a trailing switch without value is ignored
        }

        [Test]
        public void MarkerSummary_TopSortsFiltersAndFormats()
        {
            var avg = new Dictionary<string, double>
            {
                ["A"] = 1.0, ["B"] = 3.456, ["C"] = 0.1, ["D"] = 1.0, ["E"] = 2.0,
            };
            Assert.AreEqual("B=3.46 E=2.00 A=1.00", MarkerSummary.Top(avg, 3, 0.2));
            Assert.AreEqual("B=3.46 E=2.00 A=1.00 D=1.00", MarkerSummary.Top(avg, 10, 0.2));
            Assert.AreEqual("", MarkerSummary.Top(null, 3, 0));
        }

        [Test]
        public void PointShadows_BrightLightsOnly_FadeAndSlots()
        {
            Assert.IsTrue(PointShadowRules.Bright(4f, 253f / 255f, 196f / 255f, 135f / 255f), "a torch (fire gain 2)");
            Assert.IsTrue(PointShadowRules.Bright(2f, 139f / 255f, 223f / 255f, 202f / 255f), "a teal cave light");
            Assert.IsFalse(PointShadowRules.Bright(2f, 60f / 255f, 60f / 255f, 79f / 255f), "a blue-grey night fill");
            Assert.IsFalse(PointShadowRules.Bright(0f, 1f, 1f, 1f));
            Assert.AreEqual(0.5f, PointShadowRules.Fade(0f, true, PointShadowRules.FadeSeconds / 2f), 1e-5f);
            Assert.AreEqual(1f, PointShadowRules.Fade(0.9f, true, 1f));
            Assert.AreEqual(0f, PointShadowRules.Fade(0.1f, false, 1f));
            Assert.AreEqual(0.75f, PointShadowRules.Fade(1f, false, PointShadowRules.FadeSeconds / 4f), 1e-5f);
            Assert.IsTrue(PointShadowRules.MayStart(1, 2));
            Assert.IsFalse(PointShadowRules.MayStart(2, 2), "a new shadow waits for the one fading out");
            Assert.IsFalse(PointShadowRules.MayStart(0, 0));
            Assert.Less(PointShadowRules.DefaultDistance, 20f);
        }
    }
}
