using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session;
using Myrtana.Story;
using Myrtana.Ui.Skin;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// fix-126 [owner]: the map views' filter bar (chests / place names / companions / quest people, kept per install), the
    /// place names' declutter, the quest log map's fitted projection and the quest talk marks.
    /// </summary>
    public sealed class MapFiltersTests
    {
        static MapFilterState State(Dictionary<string, int> store) =>
            new MapFilterState((k, d) => store.TryGetValue(k, out int v) ? v : d, (k, v) => store[k] = v);

        // ------------------------------------------------------------------------------------------------ filters

        [Test]
        public void Filters_Defaults_ChestsHidden_NamesQuestsCompanionsShown()
        {
            var state = State(new Dictionary<string, int>());
            Assert.That(state.IsOn(MapFilter.Chests), Is.False, "fix-116 [owner]: chests only by the filter");
            Assert.That(state.IsOn(MapFilter.Names), Is.True, "G3 shows the place names");
            Assert.That(state.IsOn(MapFilter.Companions), Is.True);
            Assert.That(state.IsOn(MapFilter.Quests), Is.True);
            Assert.That(MapChestMarksLayout.ShownByDefault, Is.False);
        }

        [Test]
        public void Filters_PersistPerKey_AndKeepFix116sChestKey()
        {
            var store = new Dictionary<string, int>();
            var state = State(store);
            Assert.That(state.Toggle(MapFilter.Names), Is.False);
            Assert.That(state.Toggle(MapFilter.Chests), Is.True);
            Assert.That(store["Myrtana.Map.Names"], Is.EqualTo(0));
            Assert.That(store["Myrtana.Map.LockedChests"], Is.EqualTo(1), "fix-116's key: an earlier choice survives");
            var again = State(store);
            Assert.That(again.IsOn(MapFilter.Names), Is.False);
            Assert.That(again.IsOn(MapFilter.Chests), Is.True);
            Assert.That(again.IsOn(MapFilter.Quests), Is.True, "untouched filters keep their default");
            Assert.That(MapFilterBarLayout.Order.Select(MapFilterBarLayout.Key).Distinct().Count(), Is.EqualTo(4));
        }

        [Test]
        public void Filters_ChangedOnlyOnARealChange_AndABrokenStoreFallsBackToTheDefault()
        {
            var state = State(new Dictionary<string, int>());
            var changes = new List<MapFilter>();
            state.Changed += changes.Add;
            state.Set(MapFilter.Names, true);
            state.Set(MapFilter.Quests, false);
            state.Set(MapFilter.Quests, false);
            Assert.That(changes, Is.EqualTo(new[] { MapFilter.Quests }));
            var broken = new MapFilterState((k, d) => throw new System.InvalidOperationException(), (k, v) => throw new System.InvalidOperationException());
            Assert.That(broken.IsOn(MapFilter.Names), Is.True);
            Assert.That(broken.Toggle(MapFilter.Names), Is.False, "the state still flips without a store");
        }

        /// <summary>The bar inside the quest log map's top-right corner covers none of G3's place buttons.</summary>
        [Test]
        public void Bar_FitsTheQuestLogMapCorner_WithoutCoveringItsPlaceButtons()
        {
            // IDD_VIEW_WORLD_2 442x392; the place buttons nearest that corner (Fire clan, monastery, Hammer clan, Wolf clan, Vangard)
            var buttons = new[] { (284, 22, 302, 40), (276, 42, 294, 60), (324, 58, 342, 76), (312, 78, 330, 96), (356, 146, 374, 164) };
            float right = 442f - MapFilterBarLayout.Inset, left = right - MapFilterBarLayout.Width(3);
            float top = MapFilterBarLayout.Inset, bottom = top + MapFilterBarLayout.ButtonSize + 2f * MapFilterBarLayout.Padding;
            foreach (var (l, t, r, b) in buttons)
                Assert.That(l < right && left < r && t < bottom && top < b, Is.False, $"button {l},{t}");
            Assert.That(MapFilterBarLayout.Width(4), Is.LessThan(576f / 4f), "small next to the documents map (576 px)");
            Assert.That(MapFilterBarLayout.OffOpacity, Is.LessThan(0.5f));
        }

        [Test]
        public void Bar_Captions_InRussianAndEnglish()
        {
            Assert.That(PatchStrings.TryGet("Russian", MapFilterBarLayout.TextId(MapFilter.Chests), out var chests) ? chests : null, Is.EqualTo("Сундуки"));
            Assert.That(PatchStrings.TryGet("Russian", MapFilterBarLayout.TextId(MapFilter.Names), out var names) ? names : null, Is.EqualTo("Названия"));
            Assert.That(PatchStrings.TryGet("Russian", MapFilterBarLayout.TextId(MapFilter.Companions), out var party) ? party : null, Is.EqualTo("Спутники"));
            Assert.That(PatchStrings.TryGet("Russian", MapFilterBarLayout.TextId(MapFilter.Quests), out var quests) ? quests : null, Is.EqualTo("Задания"));
            Assert.That(PatchStrings.TryGet("Polish", MapFilterBarLayout.TextId(MapFilter.Names), out var fallback) ? fallback : null, Is.EqualTo("Place names"));
        }

        // ------------------------------------------------------------------------------------------------ declutter

        [Test]
        public void Declutter_NudgesTheLaterOfTwoOverlappingLabels_AndLeavesTheRest()
        {
            // Bakaresh (G3 marker order: first) and Ben Sala a few px left of it, as on the owner's screenshot
            var labels = new[]
            {
                new MapLabelBox(390f, 400f, 70f, 16f), // Bakaresh
                new MapLabelBox(100f, 100f, 60f, 16f), // far away
                new MapLabelBox(370f, 395f, 70f, 16f), // Ben Sala
            };
            var offsets = MapLabelLayout.Declutter(labels);
            Assert.That(offsets[0], Is.EqualTo((0f, 0f)));
            Assert.That(offsets[1], Is.EqualTo((0f, 0f)));
            Assert.That(offsets[2], Is.Not.EqualTo((0f, 0f)));
            Assert.That(System.Math.Abs(offsets[2].dy), Is.LessThanOrEqualTo(MapLabelLayout.MaxVerticalLines * 16f));
            Assert.That(offsets[2].dx, Is.EqualTo(0f), "a vertical nudge is preferred");
            Assert.That(Overlap(Moved(labels[0], offsets[0]), Moved(labels[2], offsets[2])), Is.False);
            Assert.That(MapLabelLayout.Declutter(labels), Is.EqualTo(offsets), "same input, same offsets");
        }

        [Test]
        public void Declutter_ManyLabels_LeavesNoOverlap_AndIgnoresEmptyOnes()
        {
            var labels = new List<MapLabelBox>
            {
                new MapLabelBox(200f, 200f, 80f, 16f),
                new MapLabelBox(205f, 204f, 80f, 16f),
                new MapLabelBox(210f, 196f, 80f, 16f),
                new MapLabelBox(205f, 200f, 0f, 0f), // no text yet
            };
            var offsets = MapLabelLayout.Declutter(labels);
            Assert.That(offsets[3], Is.EqualTo((0f, 0f)));
            for (int i = 0; i < 3; i++)
                for (int j = i + 1; j < 3; j++)
                    Assert.That(Overlap(Moved(labels[i], offsets[i]), Moved(labels[j], offsets[j])), Is.False, $"{i} / {j}");
            Assert.That(MapLabelLayout.Declutter(null), Is.Empty);
        }

        [Test]
        public void Declutter_ClusterNoSmallNudgeFrees_IsStackedWithoutOverlap()
        {
            // fix-136: six names on one point; the 4th onwards has no free spot within two lines / one width
            var labels = new List<MapLabelBox>();
            for (int i = 0; i < 6; i++) labels.Add(new MapLabelBox(300f, 300f, 80f, 16f));
            var offsets = MapLabelLayout.Declutter(labels);
            for (int i = 0; i < labels.Count; i++)
                for (int j = i + 1; j < labels.Count; j++)
                    Assert.That(Overlap(Moved(labels[i], offsets[i]), Moved(labels[j], offsets[j])), Is.False, $"{i} / {j}");
            Assert.That(MapLabelLayout.Declutter(labels), Is.EqualTo(offsets), "same input, same offsets");
        }

        [Test]
        public void Declutter_TouchingLabels_AreMovedApartByTheGap()
        {
            // fix-154: Klan Ognya 0.64 px above Monastyr (docs map, 1080p): pixel snapping laid them out touching
            var labels = new[]
            {
                new MapLabelBox(417.73f, 13.03f, 90.31f, 21.33f), // Klan Ognya
                new MapLabelBox(404.05f, 35.01f, 98.84f, 21.33f), // Monastyr
            };
            var offsets = MapLabelLayout.Declutter(labels);
            Assert.That(offsets[0], Is.EqualTo((0f, 0f)));
            Assert.That(offsets[1], Is.Not.EqualTo((0f, 0f)), "a touching label is nudged");
            var a = Moved(labels[0], offsets[0]);
            var b = Moved(labels[1], offsets[1]);
            float gap = System.Math.Max(b.Top - a.Bottom, a.Top - b.Bottom);
            Assert.That(gap, Is.GreaterThanOrEqualTo(MapLabelLayout.Gap - 0.01f));
        }

        static MapLabelBox Moved(MapLabelBox b, (float dx, float dy) o) => new MapLabelBox(b.X + o.dx, b.Y + o.dy, b.Width, b.Height);

        static bool Overlap(MapLabelBox a, MapLabelBox b) => a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom;

        // ------------------------------------------------------------------------------------------------ projection

        [Test]
        public void Fit_RecoversAKnownProjection_AndNeedsTwoPoints()
        {
            var truth = new MapProjection(-2364f, 1711f, 1212f, -1865f);
            var pairs = new List<(float px, float py, float x, float z)>();
            foreach (var (x, z) in new[] { (0f, 0f), (500f, -900f), (-1500f, 1200f), (900f, 300f) })
            {
                truth.TryToMap(x, z, out float u, out float v);
                pairs.Add((u * 442f, v * 392f, x, z));
            }
            Assert.That(MapProjection.TryFit(pairs, 442f, 392f, out var fit), Is.True);
            Assert.That(fit.Left, Is.EqualTo(truth.Left).Within(0.5f));
            Assert.That(fit.Top, Is.EqualTo(truth.Top).Within(0.5f));
            Assert.That(fit.Right, Is.EqualTo(truth.Right).Within(0.5f));
            Assert.That(fit.Bottom, Is.EqualTo(truth.Bottom).Within(0.5f));
            Assert.That(MapProjection.TryFit(pairs.Take(1).ToList(), 442f, 392f, out _), Is.False);
            Assert.That(MapProjection.TryFit(new[] { (10f, 10f, 5f, 5f), (10f, 10f, 5f, 5f) }, 442f, 392f, out _), Is.False);
        }

        /// <summary>The quest log map's place buttons against the world map's town markers (CP 1.75 data): a few px off.</summary>
        [Test]
        public void QuestLogMap_FittedFromThePlaceButtons_PlacesTownsNearTheirButtons()
        {
            var world = new MapDef(QuestLocations.WorldMapItem, "G3_Map_World", -2364.15869f, 1711.27869f, 1212.38867f, -1865.26868f, new[]
            {
                new MapMarkerDef(new TextId("MAP_World_Myrtana_Faring_01"), 610.17f, 561.02f, true),
                new MapMarkerDef(new TextId("MAP_World_Myrtana_Trelis_01"), -448.79f, 107.28f, true),
                new MapMarkerDef(new TextId("MAP_World_Varant_Ishtar_01"), -2070.71f, -1549.56f, true),
                new MapMarkerDef(new TextId("MAP_World_Myrtana_Ardea_01"), 911.92f, -117.75f, true),
                new MapMarkerDef(new TextId("MAP_World_Nordmar_FireClan_01"), 229.65f, 1629.28f, true),
                new MapMarkerDef(new TextId("MAP_World_Myrtana_$_Hunter_KapDun_01"), 575.75f, -242.13f, false),
            });
            var centres = new Dictionary<string, (float x, float y)>
            {
                ["IDC_BUTTON_LOCATION_FARING"] = (337f, 127f), ["IDC_BUTTON_LOCATION_TRELIS"] = (217f, 179f),
                ["IDC_BUTTON_LOCATION_ISHTAR"] = (45f, 327f), ["IDC_BUTTON_LOCATION_ARDEA"] = (369f, 195f),
                ["IDC_BUTTON_LOCATION_FIRE_CLAN"] = (293f, 31f),
            };
            Assert.That(QuestLocations.MarkerOf(QuestLocations.All.First(l => l.Key == "kapdun"), world), Is.Null, "minor '$' markers do not count");
            Assert.That(QuestLocations.MarkerOf(QuestLocations.All[QuestLocations.World], world), Is.Null);
            Assert.That(QuestLocations.TryProjection(world, c => centres.TryGetValue(c, out var p) ? p : ((float, float)?)null, 442f, 392f, out var fit), Is.True);
            fit.TryToMap(610.17f, 561.02f, out float u, out float v);
            Assert.That(u * 442f, Is.EqualTo(337f).Within(8f));
            Assert.That(v * 392f, Is.EqualTo(127f).Within(8f));
            Assert.That(QuestLocations.TryProjection(world, _ => null, 442f, 392f, out _), Is.False);
        }

        [Test]
        public void GroupIndices_TellWhichPointsEachMarkStandsFor()
        {
            var groups = MapChestMarksLayout.GroupIndices(new[] { (10f, 10f), (200f, 10f), (13f, 12f) });
            Assert.That(groups.Select(g => g.members), Is.EqualTo(new[] { new List<int> { 0, 2 }, new List<int> { 1 } }));
            Assert.That(groups[0].group.Count, Is.EqualTo(2));
        }

        // ------------------------------------------------------------------------------------------------ quest talks

        static readonly Id<QuestDef> Q1 = new Id<QuestDef>("Gorn_ShowReddock"), Q2 = new Id<QuestDef>("Other");
        static readonly ActorId Gorn = new ActorId("Gorn"), Diego = new ActorId("Diego");

        static (EventBus events, QuestTalkMarks marks, Dictionary<ActorId, (float, float, float)> where) Talks()
        {
            var events = new EventBus();
            var where = new Dictionary<ActorId, (float, float, float)> { [Gorn] = (10f, 1f, 20f), [Diego] = (-5f, 0f, 7f) };
            var marks = new QuestTalkMarks(events, a => where.TryGetValue(a, out var p) ? p : ((float, float, float)?)null);
            return (events, marks, where);
        }

        static void Line(EventBus events, Id<QuestDef> quest, ActorId speaker) =>
            events.Publish(new DialogLineSpoken(new Id<DialogEntryDef>("E"), quest, speaker, new TextId("T")));

        [Test]
        public void QuestTalks_RecordThePartner_WhereHeStands_EvenForTheHerosLines()
        {
            var (events, marks, _) = Talks();
            events.Publish(new DialogStarted(Gorn));
            Line(events, Q1, ActorId.Player);
            Line(events, default, Gorn); // a line of no quest's log
            events.Publish(new DialogEnded(Gorn));
            Assert.That(marks.All.Count, Is.EqualTo(1));
            var m = marks.All[0];
            Assert.That((m.Quest, m.Npc, m.X, m.Z), Is.EqualTo((Q1, Gorn, 10f, 20f)));
            Line(events, Q2, ActorId.Player); // no dialog: the hero's own note has no partner
            Assert.That(marks.All.Count, Is.EqualTo(1));
            Line(events, Q2, Diego); // a quest line spoken by an NPC outside a talk (ambient)
            Assert.That(marks.Of(q => q == Q2).Select(t => t.Npc), Is.EqualTo(new[] { Diego }));
        }

        [Test]
        public void QuestTalks_OneMarkPerQuestAndNpc_TheLatestTalkMovesIt()
        {
            var (events, marks, where) = Talks();
            events.Publish(new DialogStarted(Gorn));
            Line(events, Q1, Gorn);
            Line(events, Q1, Gorn);
            events.Publish(new DialogEnded(Gorn));
            where[Gorn] = (300f, 0f, 400f);
            events.Publish(new DialogStarted(Gorn));
            Line(events, Q1, Gorn);
            Line(events, Q2, Gorn);
            Assert.That(marks.All.Count, Is.EqualTo(2));
            Assert.That(marks.Of(q => q == Q1).Single().X, Is.EqualTo(300f));
            Assert.That(marks.All.Select(t => t.Quest), Is.EqualTo(new[] { Q1, Q2 }), "oldest first");
        }

        [Test]
        public void QuestTalks_SaveRoundTrip_KeepsMarksAndOrder()
        {
            var (events, marks, _) = Talks();
            events.Publish(new DialogStarted(Diego));
            Line(events, Q2, Diego);
            events.Publish(new DialogEnded(Diego));
            events.Publish(new DialogStarted(Gorn));
            Line(events, Q1, Gorn);
            var section = new QuestTalkMarksSaveSection(marks);
            var stream = new System.IO.MemoryStream();
            section.Write(new BinarySaveWriter(stream));
            var (_, loaded, _) = Talks();
            new QuestTalkMarksSaveSection(loaded).Read(new BinarySaveReader(new System.IO.MemoryStream(stream.ToArray())), section.Version);
            Assert.That(loaded.All.Select(t => (t.Quest, t.Npc, t.X, t.Z)), Is.EqualTo(marks.All.Select(t => (t.Quest, t.Npc, t.X, t.Z))));
            Assert.That(section.Section, Is.EqualTo("story.questtalks"));
        }

        [Test]
        public void QuestTalks_AreWiredIntoTheSession_AndItsSave()
        {
            using var fixture = new SessionFixture().Quest("Q1");
            var s = fixture.NewGame();
            Assert.That(s.QuestTalks, Is.Not.Null);
            Assert.That(s.QuestTalks.Record(new Id<QuestDef>("Q1"), ActorId.Player), Is.False, "the hero is never marked");
            var loaded = fixture.Reload(s);
            Assert.That(loaded.QuestTalks.All, Is.Empty);
        }
    }
}
