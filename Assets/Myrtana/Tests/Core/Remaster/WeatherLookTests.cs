using System.Collections.Generic;
using G3.Unity;
using NUnit.Framework;

namespace Myrtana.Tests.Remaster
{
    /// <summary>
    /// fix-239 (owner, build 25d, day, Braga at the Myrtana/Varant edge: rocks with white glossy patches, the sand bright
    /// white-grey): wetness from the rain that falls (not the rainy share of a fade), the desert dries and never shows wet,
    /// the grade's region from the weather mix ("Sunny(Varant)") instead of the state name's prefix.
    /// </summary>
    public class WeatherLookTests
    {
        [SetUp, TearDown]
        public void Defaults() => WorldRemasterLook.Current = WorldLookSettings.Default;

        static List<KeyValuePair<string, float>> L(params (string k, float w)[] e)
        {
            var l = new List<KeyValuePair<string, float>>();
            foreach (var x in e) l.Add(new KeyValuePair<string, float>(x.k, x.w));
            return l;
        }

        // the owner's save713 (Day 17 17:28, hero (-889, 72, -319)): Myrtana Rainy -> Sunny at 48 % of a 12.6 h fade,
        // Varant_WeatherZone_05 0.28, Myrtana_WeatherZone_01 the rest
        static readonly List<KeyValuePair<string, float>> Braga = L(("Rainy", 0.72f * 0.52f), ("Sunny", 0.72f * 0.48f), ("Sunny(Varant)", 0.28f));

        [Test]
        public void Braga_FadingRainShare_IsDry()
        {
            Assert.That(WorldV2.RainOf(Braga), Is.EqualTo(0f), "no state above 50 %: G3 shows no rain");
            Assert.That(WorldV2.WetTarget(Braga), Is.EqualTo(0f), "was 0.37: the world stayed wet for hours of the fade");
            Assert.That(WorldV2.DesertShare(Braga), Is.EqualTo(0.28f).Within(1e-4f));
        }

        [Test]
        public void Rain_WetsOnlyWhenItFalls()
        {
            Assert.That(WorldV2.RainOf(L(("Rainy", 0.6f), ("Sunny", 0.4f))), Is.EqualTo(0.2f).Within(1e-5f));
            Assert.That(WorldV2.WetTarget(L(("Rainy", 0.6f), ("Sunny", 0.4f))), Is.EqualTo(0.8f).Within(1e-5f));
            Assert.That(WorldV2.WetTarget(L(("Rainy", 0.9f), ("Sunny", 0.1f))), Is.EqualTo(1f));
            Assert.That(WorldV2.WetTarget(L(("Snowstorm(Nordmar)", 1f))), Is.EqualTo(0f), "snow does not wet");
            Assert.That(WorldV2.WetTarget(L(("Sandstormy(Varant)", 1f))), Is.EqualTo(0f), "a sandstorm does not wet");
            Assert.That(WorldV2.RainOf(new List<KeyValuePair<string, float>>()), Is.Null);
            Assert.That(WorldV2.RainOf("Rainy", "Sunny", 0.25f), Is.EqualTo(0.5f).Within(1e-5f));
            Assert.That(WorldV2.RainOf("Rainy", "Sunny", 0.5f), Is.EqualTo(0f));
        }

        [Test]
        public void Wetness_Dries_FasterInTheDesert_AndShowsOnlyOutsideIt()
        {
            float w = 0;
            for (int i = 0; i < 60; i++) w = WorldV2.WetStep(w, 1, 1);
            Assert.That(w, Is.GreaterThan(0.9f), "wet within a minute of rain");
            float myr = w, var = w;
            for (int i = 0; i < 120; i++) { myr = WorldV2.WetStep(myr, 0, 1, 0); var = WorldV2.WetStep(var, 0, 1, 1); }
            Assert.That(myr, Is.InRange(0.15f, 0.4f), "Myrtana: damp two minutes after the rain");
            Assert.That(var, Is.LessThan(0.02f), "Varant: dry in two minutes");
            for (int i = 0; i < 240; i++) myr = WorldV2.WetStep(myr, 0, 1, 0);
            Assert.That(myr, Is.LessThan(0.02f), "Myrtana: dry in ~6 minutes");
            Assert.That(WorldV2.ShownWetness(1, 1), Is.EqualTo(0f), "Varant sand and rock never show Myrtana's rain");
            Assert.That(WorldV2.ShownWetness(0.8f, 0.25f), Is.EqualTo(0.6f).Within(1e-5f));
            Assert.That(WorldV2.ShownWetness(0.8f, 0f), Is.EqualTo(0.8f).Within(1e-5f));
        }

        [Test]
        public void Grade_RegionFromTheStateNames()
        {
            Assert.That(WorldLightNew.RegionOfState("Sunny(Varant)"), Is.EqualTo("Varant"));
            Assert.That(WorldLightNew.RegionOfState("Sandstormy(Varant)"), Is.EqualTo("Varant"));
            Assert.That(WorldLightNew.RegionOfState("Snowy(Nordmar)"), Is.EqualTo("Nordmar"));
            Assert.That(WorldLightNew.RegionOfState("Rainy"), Is.EqualTo("Myrtana"));
            Assert.That(WorldLightNew.RegionOfState(null), Is.EqualTo("Myrtana"));

            var m = WorldLightNew.Grade("Rainy", 12, true, 1, 0);
            var v = WorldLightNew.Grade("Sunny(Varant)", 12, true, 1, 0);
            var n = WorldLightNew.Grade("Snowy(Nordmar)", 12, true, 1, 0);
            Assert.That(v.b / v.r, Is.LessThan(m.b / m.r), "Varant's real state name gets the warm grade (was Myrtana's)");
            Assert.That(n.b / n.r, Is.GreaterThan(m.b / m.r), "Nordmar's real state name gets the cold grade");

            var (sv, sn) = WorldLightNew.RegionShares(Braga);
            Assert.That(sv, Is.EqualTo(0.28f).Within(1e-4f));
            Assert.That(sn, Is.EqualTo(0f));
            var edge = WorldLightNew.Grade(sv, sn, 12, true, 1, 0);
            Assert.That(edge.b / edge.r, Is.InRange(v.b / v.r, m.b / m.r), "the edge blends Myrtana and Varant");
            var full = WorldLightNew.Grade(1f, 0f, 12, true, 1, 0);
            Assert.That(full.r, Is.EqualTo(v.r).Within(1e-5f));
            Assert.That(full.b, Is.EqualTo(v.b).Within(1e-5f));
            Assert.That(full.sat, Is.EqualTo(v.sat).Within(1e-5f));
        }

        [Test]
        public void Twilight_LowSunAndAfterSunset_NotNightOrNoon()
        {
            WorldRemasterLook.Current = WorldLookSettings.Default;
            Assert.That(WorldLightNew.Twilight(13, true, 1), Is.EqualTo(0f), "noon");
            Assert.That(WorldLightNew.Twilight(21.4f, true, 0.225f), Is.EqualTo(0.775f).Within(1e-5f), "owner's dusk (log: 21.4 h)");
            Assert.That(WorldLightNew.Twilight(22.5f, false, 0), Is.EqualTo(0.5f).Within(1e-5f), "half an hour after sunset");
            Assert.That(WorldLightNew.Twilight(1f, false, 0), Is.EqualTo(0f), "night stays night");
            Assert.That(WorldLightNew.Twilight(5.5f, false, 0), Is.EqualTo(0.5f).Within(1e-5f), "before sunrise");

            var c = WorldRemasterLook.Current;
            Assert.That(WorldLightNew.Floor(0), Is.EqualTo(c.v2NewFloor), "night: the plain floor (dark)");
            Assert.That(WorldLightNew.Floor(1), Is.EqualTo(c.v2NewFloor + c.v2NewTwilightFloor).Within(1e-6f));
            Assert.That(WorldLightNew.Floor(0.775f), Is.GreaterThan(4 * c.v2NewFloor), "dusk: bodies get G3's fill");
            Assert.That(WorldLightNew.PurkinjeStrength(0), Is.EqualTo(c.v2NewPurkinje).Within(1e-6f), "night: rod shift");
            Assert.That(WorldLightNew.PurkinjeStrength(1), Is.EqualTo(0f), "dusk: no blue rod shift (it made Varant violet)");
        }

        [Test]
        public void Grade_VarantDusk_IsAmber_NotMagenta()
        {
            WorldRemasterLook.Current = WorldLookSettings.Default;
            var day = WorldLightNew.Grade("Sunny(Varant)", 21.4f, true, 0.225f, 0f, 0f);
            var dusk = WorldLightNew.Grade("Sunny(Varant)", 21.4f, true, 0.225f, 0f, 0.775f);
            var myr = WorldLightNew.Grade("Sunny", 21.4f, true, 0.225f, 0f, 0.775f);
            var myr0 = WorldLightNew.Grade("Sunny", 21.4f, true, 0.225f, 0f, 0f);
            Assert.That(WorldLightNew.Lum(dusk.r, dusk.g, dusk.b), Is.EqualTo(1f).Within(1e-4f), "luminance kept");
            Assert.That(dusk.b / dusk.r, Is.LessThan(day.b / day.r * 0.9f), "blue down against red: magenta -> amber");
            Assert.That(dusk.g / dusk.b, Is.GreaterThan(day.g / day.b), "green up against blue: orange, not pink");
            Assert.That(myr.b, Is.EqualTo(myr0.b).Within(1e-5f), "Myrtana's dusk unchanged");
            WorldRemasterLook.Current = new WorldLookSettings { v2NewDuskWarm = 0 }.Clamped();
            var off = WorldLightNew.Grade("Sunny(Varant)", 21.4f, true, 0.225f, 0f, 0.775f);
            Assert.That(off.b, Is.EqualTo(day.b).Within(1e-5f), "v2NewDuskWarm 0: off");
            WorldRemasterLook.Current = WorldLookSettings.Default;
        }
    }
}
