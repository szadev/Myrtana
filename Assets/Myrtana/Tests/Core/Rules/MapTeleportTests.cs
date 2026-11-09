using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using G3.Formats.Genome;
using MapMarkerList = G3.Formats.Items.MapMarkerList;
using System.Linq;
using Myrtana.Core;
using Myrtana.Rules;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>World map and teleport stones (Docs/map-teleport.md).</summary>
    public sealed class MapTeleportTests
    {
        // Corners of It_Map_World and It_Map_Ancestorgraves in metres (G3 cm × 0.01).
        static MapDef World() => new MapDef("It_Map_World", "G3_Map_World", -2364f, 1711f, 1212f, -1865f);
        static MapDef Graves() => new MapDef("It_Map_Ancestorgraves", "G3_Map_Ancestorgraves", 158.8f, 1768f, 1112f, 814.3f);

        static SpellDef TeleportSpell(string town) => new SpellDef("Spl_Teleport_" + town, new[] { "It_Teleport_" + town }, null,
            SpellCastType.Regular, SpellTarget.Caster, SpellKind.Target, SpellDeity.None, 0, 0, SpellDelivery.Instant,
            Teleports.Effect, null, 0, null, null, null);

        static TeleportDestinationDef Faring() => new TeleportDestinationDef("Teleport_Faring", new[] { 560.67f, 57.33f, 546.95f }, 30f);
        static TeleportDestinationDef Geldern() => new TeleportDestinationDef("Teleport_Geldern", new[] { -594.48f, 29.73f, 397.09f }, -90f);

        // ---------------------------------------------------------------- projection

        [Test]
        public void Projection_CornersAndCentre()
        {
            var p = World().Projection;
            Assert.That(p.TryToMap(-2364f, 1711f, out float u, out float v), Is.True);
            Assert.That(u, Is.EqualTo(0f));
            Assert.That(v, Is.EqualTo(0f));
            p.TryToMap(1212f, -1865f, out u, out v);
            Assert.That(u, Is.EqualTo(1f).Within(1e-6));
            Assert.That(v, Is.EqualTo(1f).Within(1e-6));
            p.TryToMap((-2364f + 1212f) / 2, (1711f - 1865f) / 2, out u, out v);
            Assert.That(u, Is.EqualTo(0.5f).Within(1e-5));
            Assert.That(v, Is.EqualTo(0.5f).Within(1e-5));
        }

        [Test]
        public void Projection_RoundTripsAndNorthIsUp()
        {
            var p = World().Projection;
            p.TryToMap(560.67f, 546.95f, out float u, out float v); // Faring
            var (x, z) = p.ToWorld(u, v);
            Assert.That(x, Is.EqualTo(560.67f).Within(1e-3));
            Assert.That(z, Is.EqualTo(546.95f).Within(1e-3));
            p.TryToMap(560.67f, 600f, out _, out float north);
            Assert.That(north, Is.LessThan(v), "further north is higher on the map");
        }

        [Test]
        public void Projection_OutsideIsNotClampedAndDegenerateFails()
        {
            var p = World().Projection;
            p.TryToMap(-3000f, 2000f, out float u, out float v);
            Assert.That(u, Is.LessThan(0f));
            Assert.That(v, Is.LessThan(0f));
            Assert.That(p.Contains(-3000f, 0f), Is.False);
            Assert.That(p.Contains(1212f, -1865f), Is.True, "inclusive");
            Assert.That(new MapProjection(5, 5, 5, 0).TryToMap(1, 1, out _, out _), Is.False);
        }

        [TestCase(0f, 1f, 0f)]
        [TestCase(1f, 0f, 90f)]
        [TestCase(0f, -1f, 180f)]
        [TestCase(-1f, 0f, -90f)]
        public void Heading_ClockwiseFromNorth(float fx, float fz, float degrees) =>
            Assert.That(MapProjection.HeadingDegrees(fx, fz), Is.EqualTo(degrees).Within(1e-4));

        // ---------------------------------------------------------------- maps

        static (WorldMaps maps, Inventory hero) Maps()
        {
            var hero = new Inventory();
            return (new WorldMaps(new[] { World(), Graves() }, () => hero), hero);
        }

        [Test]
        public void Choose_NothingOwned_ShowsNothing()
        {
            var (maps, _) = Maps();
            Assert.That(maps.Choose(null, 560f, 546f), Is.Null);
            Assert.That(maps.Shown, Is.Null);
        }

        [Test]
        public void Choose_TheLargestOwnedMapAroundTheHero()
        {
            var (maps, hero) = Maps();
            hero.Add("It_Map_Ancestorgraves", 1);
            hero.Add("It_Map_World", 1);
            Assert.That(maps.Owned().Select(m => m.Id), Is.EqualTo(new[] { "It_Map_Ancestorgraves", "It_Map_World" }));
            Assert.That(maps.Choose(null, 560f, 1000f).Id, Is.EqualTo("It_Map_World"), "both contain him: the larger wins");
        }

        [Test]
        public void Choose_SmallMapNotAroundTheHero_ShowsNothing()
        {
            var (maps, hero) = Maps();
            hero.Add("It_Map_Ancestorgraves", 1);
            Assert.That(maps.Choose(null, -500f, 0f), Is.Null);
        }

        [Test]
        public void Choose_PickedThenShownLast_WhileStillCarried()
        {
            var (maps, hero) = Maps();
            hero.Add("It_Map_Ancestorgraves", 1);
            hero.Add("It_Map_World", 1);
            Assert.That(maps.Choose("It_Map_Ancestorgraves", -500f, 0f).Id, Is.EqualTo("It_Map_Ancestorgraves"), "a picked map shows anywhere");
            Assert.That(maps.Choose(null, -500f, 0f).Id, Is.EqualTo("It_Map_Ancestorgraves"), "the shown one stays");
            hero.Remove("It_Map_Ancestorgraves", 1);
            Assert.That(maps.Choose(null, -500f, 0f).Id, Is.EqualTo("It_Map_World"), "gone: back to the map around the hero");
        }

        // ---------------------------------------------------------------- teleports

        sealed class TeleportSetup
        {
            public readonly EventBus Events = new EventBus();
            public readonly List<TeleportRequested> Requests = new List<TeleportRequested>();
            public readonly Teleports Teleports;
            readonly Dictionary<string, SpellDef> _spells = new Dictionary<string, SpellDef>();

            public TeleportSetup()
            {
                foreach (var s in new[] { TeleportSpell("Faring"), TeleportSpell("Geldern"), TeleportSpell("Nowhere") })
                {
                    _spells[s.Id] = s;
                    _spells[s.Items[0]] = s;
                }
                Teleports = new Teleports(new[] { Faring(), Geldern() }, id => id != null && _spells.TryGetValue(id, out var s) ? s : null, Events);
                Events.Subscribe<TeleportRequested>(Requests.Add);
            }
        }

        [Test]
        public void Destination_IsTheSpellNameWithoutItsPrefix()
        {
            var t = new TeleportSetup().Teleports;
            Assert.That(t.TryDestinationOf("It_Teleport_Faring", out var d), Is.True);
            Assert.That(d.Id, Is.EqualTo("Teleport_Faring"));
            Assert.That(t.TryDestinationOf("Spl_Teleport_Geldern", out d), Is.True);
            Assert.That(d.Id, Is.EqualTo("Teleport_Geldern"));
            Assert.That(t.TryGet("teleport_faring", out _), Is.True, "names compare case-insensitively (HammerClan/Hammerclan)");
            Assert.That(t.TryDestinationOf("It_Teleport_Nowhere", out _), Is.False);
            Assert.That(t.IsTeleport("It_Apple"), Is.False);
        }

        [Test]
        public void Known_AreTheCarriedStones()
        {
            var t = new TeleportSetup().Teleports;
            var hero = new Inventory();
            Assert.That(t.Known(hero), Is.Empty);
            hero.Add("It_Teleport_Geldern", 1);
            hero.Add("It_Apple", 3);
            hero.Add("It_Teleport_Faring", 1);
            Assert.That(t.Known(hero).Select(d => d.Id), Is.EqualTo(new[] { "Teleport_Geldern", "Teleport_Faring" }));
        }

        [Test]
        public void PlayerCast_RequestsTheTeleport()
        {
            var s = new TeleportSetup();
            s.Events.Publish(new SpellCasted(ActorId.Player, "Spl_Teleport_Faring", "It_Teleport_Faring", false));
            Assert.That(s.Requests.Count, Is.EqualTo(1));
            Assert.That(s.Requests[0].Destination.Id, Is.EqualTo("Teleport_Faring"));
            Assert.That(s.Requests[0].Item, Is.EqualTo("It_Teleport_Faring"));
        }

        [Test]
        public void NpcCastOrMissingDestination_DoesNothing()
        {
            var s = new TeleportSetup();
            s.Events.Publish(new SpellCasted(new ActorId("Lee"), "Spl_Teleport_Faring", "It_Teleport_Faring", false));
            s.Events.Publish(new SpellCasted(ActorId.Player, "Spl_Teleport_Nowhere", "It_Teleport_Nowhere", false));
            Assert.That(s.Requests, Is.Empty);
        }

        [Test]
        public void Stone_IsNeverUsedUp() =>
            Assert.That(TeleportSpell("Faring").UsesUp("It_Teleport_Faring"), Is.False);

        // ---------------------------------------------------------------- markers (gCMap_PS post data)

        static byte[] MarkerData(uint elementSize, params (string label, float x, float y, float z, bool visible)[] markers)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(markers.Length);
                w.Write(elementSize);
                foreach (var m in markers)
                {
                    w.Write((ushort)m.label.Length);
                    w.Write(Encoding.ASCII.GetBytes(m.label));
                    w.Write(m.x);
                    w.Write(m.y);
                    w.Write(m.z);
                    w.Write((byte)(m.visible ? 1 : 0));
                }
                w.Write(0xDEADC0DEu);
                return ms.ToArray();
            }
        }

        [Test]
        public void MarkerList_ReadsLabelPositionAndFlag()
        {
            var data = MarkerData(20, ("MAP_World_Myrtana_Ardea_01", 91192f, 5119f, -11775f, true), ("MAP_World_X", 1f, 2f, 3f, false));
            var g = new GenomeReader(data);
            var list = MapMarkerList.Read(g, 0, 1);
            Assert.That(list.Count, Is.EqualTo(2));
            Assert.That(list[0].Label, Is.EqualTo("MAP_World_Myrtana_Ardea_01"));
            Assert.That(list[0].X, Is.EqualTo(91192f));
            Assert.That(list[0].Z, Is.EqualTo(-11775f));
            Assert.That(list[0].Visible, Is.True);
            Assert.That(list[1].Visible, Is.False);
            Assert.That(g.Position, Is.EqualTo(0), "the reader's position is restored");
        }

        [Test]
        public void MarkerList_OtherElementSizeOrVersion_IsEmpty()
        {
            var data = MarkerData(24, ("A", 0f, 0f, 0f, true));
            Assert.That(MapMarkerList.Read(new GenomeReader(data), 0, 1), Is.Empty);
            Assert.That(MapMarkerList.Read(new GenomeReader(MarkerData(20, ("A", 0f, 0f, 0f, true))), 0, 2), Is.Empty);
        }

        static MapDef WorldWithMarkers() => new MapDef("It_Map_World", "G3_Map_World", -2364f, 1711f, 1212f, -1865f, new[]
        {
            new MapMarkerDef(new TextId("MAP_World_Myrtana_Ardea_01"), 911.92f, -117.75f, true),
            new MapMarkerDef(new TextId("MAP_World_Hidden"), 0f, 0f, false),
        });

        [Test]
        public void Markers_OnlyVisibleOnesAreDrawn() =>
            Assert.That(WorldWithMarkers().VisibleMarkers.Select(m => m.Label.Value), Is.EqualTo(new[] { "MAP_World_Myrtana_Ardea_01" }));

        [Test]
        public void MapDef_MarkersSurviveTheContentJson()
        {
            var schema = new ContentSchema();
            RulesSchema.Register(schema);
            var options = ContentJson.CreateOptions(schema);
            var back = JsonSerializer.Deserialize<MapDef>(JsonSerializer.Serialize(WorldWithMarkers(), options), options);
            Assert.That(back.Markers.Count, Is.EqualTo(2));
            Assert.That(back.Markers[0].Label.Value, Is.EqualTo("MAP_World_Myrtana_Ardea_01"));
            Assert.That(back.Markers[0].X, Is.EqualTo(911.92f));
            Assert.That(back.Markers[0].Visible, Is.True);
            Assert.That(back.Markers[1].Visible, Is.False);
            var old = JsonSerializer.Deserialize<MapDef>("{\"id\":\"It_Map_World\",\"bitmap\":\"G3_Map_World\",\"left\":0,\"top\":1,\"right\":1,\"bottom\":0}", options);
            Assert.That(old.Markers, Is.Empty);
        }

        // ---------------------------------------------------------------- session + save

        [Test]
        public void Session_StoneCastTeleportsAndShownMapSurvivesSave()
        {
            using (var f = new SessionFixture())
            {
                f.Add(RulesSchema.MapFolder, World());
                f.Add(RulesSchema.TeleportFolder, Faring());
                f.Add(RulesSchema.SpellFolder, TeleportSpell("Faring"));
                var session = f.NewGame();
                var requests = new List<TeleportRequested>();
                session.Events.Subscribe<TeleportRequested>(requests.Add);
                session.Events.Publish(new SpellCasted(ActorId.Player, "Spl_Teleport_Faring", "It_Teleport_Faring", false));
                Assert.That(requests.Single().Destination.Position[0], Is.EqualTo(560.67f));

                session.Items.Inventories.Of(ActorId.Player).Add("It_Map_World", 1);
                Assert.That(session.Maps.Choose(null, 560f, 546f).Id, Is.EqualTo("It_Map_World"));
                var loaded = f.Reload(session);
                Assert.That(loaded.Maps.Shown, Is.EqualTo("It_Map_World"));
            }
        }
    }
}
