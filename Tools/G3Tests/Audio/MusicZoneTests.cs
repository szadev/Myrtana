using System.Collections.Generic;
using G3.Audio;
using NUnit.Framework;

namespace G3Tests.Audio
{
    /// <summary>
    /// fix-245: the music zone at sample points of the real G3 zones (G3AudioWorld.asset values, metres). The most
    /// specific zone the hero is in wins; the world-wide Myrtana circle must not beat the Varant / Nordmar region
    /// circles or towns on their fade ramps.
    /// </summary>
    [TestFixture]
    public class MusicZoneTests
    {
        static MusicZoneData Circle(string name, float x, float z, float inner, float outer, string env, string music) =>
            new MusicZoneData { name = name, shape = ZoneShape.Circle2D, centre = new V3(x, 0, z), innerRadius = inner, outerRadius = outer,
                weatherEnvironment = env, musicLocation = music };

        static MusicZoneData Sphere(string name, float x, float y, float z, float inner, float outer, string env, string music) =>
            new MusicZoneData { name = name, shape = ZoneShape.Sphere3D, centre = new V3(x, y, z), innerRadius = inner, outerRadius = outer,
                weatherEnvironment = env, musicLocation = music };

        static MusicZoneData Box(string name, float x, float y, float z, V3 inner, V3 outer, string env, string music) =>
            new MusicZoneData { name = name, shape = ZoneShape.Box3D, centre = new V3(x, y, z), innerRadius = 4000, outerRadius = 5000,
                innerExtents = inner, outerExtents = outer, weatherEnvironment = env, musicLocation = music };

        static List<MusicZoneData> World() => new List<MusicZoneData>
        {
            Circle("Myrtana_WeatherZone_01", -266, 316, 8000, 10000, "Myrtana", "Myrtana"),
            Circle("PathToVarant", -958, -272, 49, 50, "Myrtana", "PathToVarant"),
            Circle("Varant_WeatherZone_01", -985, -1456, 800, 1000, "Varant", "varant"),
            Circle("Varant_WeatherZone_04", -292, -1049, 500, 700, "Varant", "varant"),
            Circle("Varant_WeatherZone_05", -1552, -989, 800, 1000, "Varant", "varant"),
            Circle("Music_Braga", -872, -531, 60, 80, "Varant", "braga"),
            Circle("Music_MoraSul", -962, -1456, 87, 90, "Varant", "varant"),
            Circle("MoraSulArena", -1015, -1494, 15, 16, "Varant", "arena"),
            Circle("Music_Lago", -159, -527, 50, 65, "Varant", "braga"),
            Box("Music_Bakaresch", 401, 39, -985, new V3(125, 45, 100), new V3(130, 46, 100), "Varant", "varant"),
            Circle("BakareschArena", 499, -942, 15, 16, "Varant", "arena"),
            Circle("Nordmar_WeatherZone_03", 619, 1097, 300, 400, "Nordmar", "northmarlow"),
            Sphere("Nordmar_Highlands", 344, 177, 817, 65, 95, "Nordmar", "highlands"),
            Sphere("MusicZone_Faring", 601, 96, 557, 74, 75, "Myrtana", "faring"),
        };

        static string MusicAt(float x, float y, float z) => new MusicZoneMap(World()).ZoneAt(new V3(x, y, z))?.musicLocation;

        [TestCase(-887, 49, -397, "varant", TestName = "Varant sand north of Braga (region circle f 0.55)")]
        [TestCase(-958, 59, -342, "varant", TestName = "Varant border just past the pass (f 0.6)")]
        [TestCase(-940, 24, -542, "braga", TestName = "Braga edge (f 0.57) beats the Varant circle at full")]
        [TestCase(-872, 30, -531, "braga", TestName = "Braga centre")]
        [TestCase(-958, 115, -272, "PathToVarant", TestName = "Pass to Varant keeps its own zone")]
        [TestCase(-962, 92, -1456, "varant", TestName = "Mora Sul")]
        [TestCase(-1015, 54, -1494, "arena", TestName = "Mora Sul arena inside Mora Sul")]
        [TestCase(-159, 12, -527, "braga", TestName = "Lago")]
        [TestCase(401, 39, -985, "varant", TestName = "Bakaresh")]
        [TestCase(499, 26, -942, "arena", TestName = "Bakaresh arena")]
        [TestCase(450, 170, 800, "northmarlow", TestName = "Nordmar pass (region circle f 0.58)")]
        [TestCase(400, 170, 780, "highlands", TestName = "Nordmar highlands at f 0.94")]
        [TestCase(601, 96, 557, "faring", TestName = "Faring")]
        [TestCase(-266, 1, 316, "Myrtana", TestName = "Myrtana heartland")]
        public void Zone_at_sample_point(float x, float y, float z, string music) => Assert.That(MusicAt(x, y, z), Is.EqualTo(music));

        [Test]
        public void Outer_fade_still_myrtana()
        {
            // Varant_WeatherZone_05 f 0.19 here (963 m from its centre): not in Varant yet.
            Assert.That(MusicAt(-1000, 80, -200), Is.EqualTo("Myrtana"));
        }

        [Test]
        public void Leaving_a_zone_has_hysteresis()
        {
            var map = new MusicZoneMap(World());
            // Walk east out of Braga along z = -531: enters at f >= 0.5 (r 70 m), stays until f <= 0.25 (r 75 m).
            Assert.That(map.ZoneAt(new V3(-872 + 69, 30, -531)).musicLocation, Is.EqualTo("braga"));
            Assert.That(map.ZoneAt(new V3(-872 + 73, 30, -531)).musicLocation, Is.EqualTo("braga"), "f 0.35: still Braga");
            Assert.That(map.ZoneAt(new V3(-872 + 76, 30, -531)).musicLocation, Is.EqualTo("varant"), "f 0.2: out");
            Assert.That(map.ZoneAt(new V3(-872 + 73, 30, -531)).musicLocation, Is.EqualTo("varant"), "f 0.35 from outside: not in yet");
            Assert.That(map.ZoneAt(new V3(-872 + 69, 30, -531)).musicLocation, Is.EqualTo("braga"));
        }

        [Test]
        public void Resolved_state_for_varant_border()
        {
            var catalog = new MusicCatalog();
            foreach (var loc in new[] { "myrtana", "varant", "braga" })
                catalog.fragments.Add(new MusicFragmentData { location = loc, situation = "explore", dayTime = "day", variation = "0",
                    clip = loc + "_explore_day_01", duration = 60f });
            var resolver = new MusicStateResolver(catalog);
            var map = new MusicZoneMap(World());
            Assert.That(resolver.Resolve(map.ZoneAt(new V3(-887, 49, -397)), null, 12).Location, Is.EqualTo("varant"));
            Assert.That(resolver.Resolve(map.ZoneAt(new V3(-958, 115, -272)), null, 12).Location, Is.EqualTo("myrtana"),
                "PathToVarant has no fragment → its region (Myrtana)");
        }
    }
}
