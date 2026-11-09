using System;
using System.Reflection;
using G3.Unity;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>fix-171 [owner]: the remaster lighting quality levels (Docs/remaster-world.md section 19).</summary>
    public class LightingQualityTests
    {
        static WorldLookSettings Json() => WorldLookSettings.Default.Clamped();

        [Test]
        public void Ultra_IsTheJsonAsAuthored()
        {
            var json = Json();
            var ultra = LightingQualityLevels.Apply(json, LightingQuality.Ultra);
            foreach (var f in typeof(WorldLookSettings).GetFields(BindingFlags.Public | BindingFlags.Instance))
                Assert.That(f.GetValue(ultra), Is.EqualTo(f.GetValue(json)), f.Name);
        }

        [Test]
        public void LowerLevels_OnlyLowerTheCost_AndKeepTheLookDefiningEffects()
        {
            var json = Json();
            foreach (var q in new[] { LightingQuality.High, LightingQuality.Medium, LightingQuality.Low })
            {
                var s = LightingQualityLevels.Apply(json, q);
                Assert.That(s.v2ShadowDistance, Is.LessThanOrEqualTo(json.v2ShadowDistance), q + " shadows");
                Assert.That(s.v2ShadowCascades, Is.LessThanOrEqualTo(json.v2ShadowCascades), q + " cascades");
                Assert.That(s.v2ShaftSteps, Is.LessThanOrEqualTo(json.v2ShaftSteps), q + " shafts");
                Assert.That(s.v2CloudSteps, Is.LessThanOrEqualTo(json.v2CloudSteps), q + " clouds");
                Assert.That(s.v2SkyOccResolution, Is.LessThanOrEqualTo(json.v2SkyOccResolution), q + " sky occ");
                Assert.That(s.v2SkyOccInterval, Is.GreaterThanOrEqualTo(json.v2SkyOccInterval), q + " sky occ interval");
                Assert.That(s.v2SkyReflectionInterval, Is.GreaterThanOrEqualTo(json.v2SkyReflectionInterval), q + " reflection interval");
                Assert.That(s.v2MotionDistance, Is.LessThanOrEqualTo(json.v2MotionDistance), q + " motion vectors");
                // the look: sun balance, fog, shafts, clouds, sky occlusion, SSAO, aerial, grade stay on at every level
                Assert.That(s.v2Sun && s.v2Fog && s.v2Shafts && s.v2Clouds && s.v2SkyOcclusion && s.v2Ssao && s.aerial && s.grade && s.v2Shoulder, Is.True, q + " look");
                Assert.That(s.v2SunIntensity, Is.EqualTo(json.v2SunIntensity), q + " sun");
                Assert.That(s.v2FogDensity, Is.EqualTo(json.v2FogDensity), q + " fog density");
            }
            var medium = LightingQualityLevels.Apply(json, LightingQuality.Medium);
            Assert.That(medium.v2Shadows && medium.v2Contact && medium.v2CloudShadows && medium.v2Bounce > 0, Is.True, "Medium keeps shadows, contact, cloud shadows, bounce");
            Assert.That(medium.v2ShadowDistance, Is.GreaterThanOrEqualTo(60f), "Medium shadows still reach the gameplay distance");
        }

        [Test]
        public void Gi_OnlyFromHigh()
        {
            var json = Json();
            json.v2GiStrength = 1;
            Assert.That(LightingQualityLevels.Apply(json, LightingQuality.Ultra).v2GiStrength, Is.GreaterThan(0));
            Assert.That(LightingQualityLevels.Apply(json, LightingQuality.High).v2GiStrength, Is.GreaterThan(0));
            Assert.That(LightingQualityLevels.Apply(json, LightingQuality.Medium).v2GiStrength, Is.EqualTo(0));
            Assert.That(LightingQualityLevels.Apply(json, LightingQuality.Low).v2GiStrength, Is.EqualTo(0));
        }

        [Test]
        public void Default_IsOneStepBelowThePreset()
        {
            Assert.That(LightingQualityLevels.FromPreset(0, 1), Is.EqualTo(LightingQuality.Low));
            Assert.That(LightingQualityLevels.FromPreset(1, 2), Is.EqualTo(LightingQuality.Low));
            Assert.That(LightingQualityLevels.FromPreset(2, 3), Is.EqualTo(LightingQuality.Medium));
            Assert.That(LightingQualityLevels.FromPreset(3, 4), Is.EqualTo(LightingQuality.High));
            Assert.That(LightingQualityLevels.FromPreset(4, 4), Is.EqualTo(LightingQuality.High), "Custom by its shadows");
            Assert.That(LightingQualityLevels.FromPreset(4, 3), Is.EqualTo(LightingQuality.Medium));
            Assert.That(LightingQualityLevels.FromPreset(4, 0), Is.EqualTo(LightingQuality.Low));
        }

        [Test]
        public void Parse_NamesAndNumbers()
        {
            Assert.That(LightingQualityLevels.Parse("medium"), Is.EqualTo(LightingQuality.Medium));
            Assert.That(LightingQualityLevels.Parse("3"), Is.EqualTo(LightingQuality.Ultra));
            Assert.That(LightingQualityLevels.Parse("x"), Is.Null);
            Assert.That(LightingQualityLevels.Parse(""), Is.Null);
        }

        [Test]
        public void Texts_RussianAndEnglish()
        {
            Assert.That(LightingQualityLevels.RowName("Russian"), Is.EqualTo("Качество освещения"));
            Assert.That(LightingQualityLevels.Value(LightingQuality.Medium, "Russian"), Is.EqualTo("Среднее"));
            Assert.That(RemasterOptions.ReliefName("Russian"), Is.EqualTo("Рельеф земли"));
            Assert.That(RemasterOptions.ReliefValue(true, false, "English"), Is.EqualTo("On (with Ground)"));
        }
    }
}
