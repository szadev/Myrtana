using System.Linq;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session;
using Myrtana.Ui.Skin;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// fix-102 ([owner]): the map marks of locked chests the hero found but could not open — recorded on a refusal, gone
    /// once the chest is opened, kept in the save, projected onto each region map like the hero arrow; fix-116: the map filter, the grouping, the messages' layer.
    /// </summary>
    public sealed class LockedChestMarksTests
    {
        const string Lockpick = "It_Key_Lockpick", Key = "It_Key_Ali";

        static InteractiveObjectDef Chest(string id, LockDef lockDef, float[] position, string sector = null) =>
            new InteractiveObjectDef(id, "G3_Object_Interact_Animated_Chest_02", InteractiveKind.Chest, "Chest", "chest.FXA", position,
                                     null, null, 1, default, lockDef, new ContainerDef(null, new[] { new TreasureStack("It_Gold", 5, 0) }),
                                     null, null, null, sector);

        static SessionFixture Fixture() => new SessionFixture()
            .Add(RulesSchema.InteractiveObjectFolder,
                 Chest("picklock", new LockDef(true, 1, null, 1), new float[] { 100, 5, 200 }),
                 Chest("keylock", new LockDef(true, -1, Key, 1), new float[] { -300, 0, 50 }),
                 Chest("hardlock", new LockDef(true, 3, null, 1), new float[] { 10, 0, 10 }),
                 Chest("unlocked", new LockDef(false, 1, null, 1), new float[] { 0, 0, 0 }),
                 Chest("nolock", null, new float[] { 1, 0, 1 }),
                 new InteractiveObjectDef("door", "Door", InteractiveKind.Door, "Door", "door.FXA", null, null, null, 1, default,
                                          new LockDef(true, -1, Key, 1), null, new DoorDef(true, "open", "close"), null));

        [Test]
        public void Refusal_RecordsTheChest_WithItsPosition()
        {
            using var f = Fixture();
            var s = f.NewGame();
            var marks = s.Interaction.ChestMarks;
            Assert.That(s.Interaction.Objects.Unlock("picklock").Verdict, Is.EqualTo(LockVerdict.MissingSkill));
            Assert.That(marks.Discover("picklock"), Is.True, "no perk: refused");
            Assert.That(marks.Discover("picklock"), Is.False, "once");
            Assert.That(marks.Discover("keylock"), Is.True, "no key");
            var mark = marks.All.Single(m => m.Id == "picklock");
            Assert.That(new[] { mark.X, mark.Y, mark.Z }, Is.EqualTo(new[] { 100f, 5f, 200f }));
            Assert.That(marks.Visible.Select(m => m.Id), Is.EquivalentTo(new[] { "picklock", "keylock" }));
        }

        [Test]
        public void OpenableOrNotAChest_IsNotRecorded()
        {
            using var f = Fixture();
            var s = f.NewGame();
            var marks = s.Interaction.ChestMarks;
            Assert.That(marks.Discover("unlocked"), Is.False, "not locked");
            Assert.That(marks.Discover("nolock"), Is.False, "no lock");
            Assert.That(marks.Discover("door"), Is.False, "a locked door is no chest");
            Assert.That(marks.Discover("nothing"), Is.False);
            Assert.That(marks.Discover(null), Is.False);
            s.Items.Inventories.Give(ActorId.Player, Lockpick, 3);
            s.Progression.LearnSkill("Perk_LockPick_1");
            Assert.That(s.Interaction.Objects.CheckLock("picklock"), Is.EqualTo(LockVerdict.Pick));
            Assert.That(marks.Discover("picklock"), Is.False, "he can pick it: not a chest he could not open");
            s.Items.Inventories.Give(ActorId.Player, Key, 1);
            Assert.That(marks.Discover("keylock"), Is.False, "he carries the key");
            Assert.That(marks.Discover("hardlock"), Is.True, "difficulty 3 without Perk_LockPick_3");
            Assert.That(LockedChestMarks.Refuses(LockVerdict.NeedLockpick) && LockedChestMarks.Refuses(LockVerdict.Impossible), Is.True);
        }

        [Test]
        public void OpeningTheLock_RemovesTheMark_ByPickKeyOrSpell()
        {
            using var f = Fixture();
            var s = f.NewGame();
            var objects = s.Interaction.Objects;
            var marks = s.Interaction.ChestMarks;
            marks.Discover("picklock");
            marks.Discover("keylock");
            marks.Discover("hardlock");
            int changed = 0;
            marks.Changed += () => changed++;

            s.Items.Inventories.Give(ActorId.Player, Lockpick, 5);
            s.Progression.LearnSkill("Perk_LockPick_1");
            s.Progression.LearnSkill("Perk_LockPick_2"); // no break chance at difficulty 1
            Assert.That(objects.Unlock("picklock").Opened, Is.True);
            Assert.That(marks.Contains("picklock"), Is.False, "picked");

            s.Items.Inventories.Give(ActorId.Player, Key, 1);
            Assert.That(objects.Unlock("keylock").Verdict, Is.EqualTo(LockVerdict.Key));
            Assert.That(marks.Contains("keylock"), Is.False, "key");

            Assert.That(objects.OpenByMagic("hardlock"), Is.EqualTo(LockSpellOutcome.Opens));
            Assert.That(marks.Contains("hardlock"), Is.False, "Open Locks spell (and any script unlock: LockOpened)");
            Assert.That(marks.All, Is.Empty);
            Assert.That(changed, Is.EqualTo(3));
        }

        [Test]
        public void SaveLoad_KeepsTheMarks_AndAnOpenedChestStaysUnmarked()
        {
            using var f = Fixture();
            var s = f.NewGame();
            s.Interaction.ChestMarks.Discover("picklock");
            s.Interaction.ChestMarks.Discover("keylock");

            var loaded = f.Reload(s);
            var marks = loaded.Interaction.ChestMarks;
            Assert.That(marks.Visible.Select(m => m.Id), Is.EquivalentTo(new[] { "picklock", "keylock" }));
            var mark = marks.All.Single(m => m.Id == "keylock");
            Assert.That(new[] { mark.X, mark.Y, mark.Z }, Is.EqualTo(new[] { -300f, 0f, 50f }));

            loaded.Items.Inventories.Give(ActorId.Player, Key, 1);
            loaded.Interaction.Objects.Unlock("keylock");
            var again = f.Reload(loaded).Interaction.ChestMarks;
            Assert.That(again.All.Select(m => m.Id), Is.EquivalentTo(new[] { "picklock" }));
            Assert.That(again.Contains("keylock"), Is.False);
        }

        [Test]
        public void OldSave_WithoutTheSection_LoadsWithNoMarks()
        {
            using var f = Fixture();
            var s = f.NewGame();
            var loaded = f.Reload(s);
            Assert.That(loaded.Interaction.ChestMarks.All, Is.Empty);
        }

        [Test]
        public void Projection_IsTheMapsUv_AndOtherRegionsChestsStayOff()
        {
            // a region map 0..400 m east, 400..0 m north to south (G3 gCMap_PS: v from the top/north edge)
            var region = new MapProjection(0f, 400f, 400f, 0f);
            Assert.That(LockedChestMarks.OnMap(region, new LockedChestMark("a", 100, 5, 300), out float u, out float v), Is.True);
            Assert.That(u, Is.EqualTo(0.25f).Within(1e-5f));
            Assert.That(v, Is.EqualTo(0.25f).Within(1e-5f), "north of the centre: upper half");
            Assert.That(LockedChestMarks.OnMap(region, new LockedChestMark("b", 400, 0, 0), out u, out v) && u == 1f && v == 1f, Is.True, "edge");
            Assert.That(LockedChestMarks.OnMap(region, new LockedChestMark("c", -300, 0, 50), out _, out _), Is.False, "another region's chest");
            Assert.That(LockedChestMarks.OnMap(new MapProjection(5, 5, 5, 5), new LockedChestMark("d", 5, 0, 5), out _, out _), Is.False, "degenerate map");
            // the same chest on the hero arrow's transform
            region.TryToMap(100, 300, out float hu, out float hv);
            Assert.That((hu, hv), Is.EqualTo((0.25f, 0.25f)));
        }

        [Test]
        public void ChestInASwitchedOffSector_IsNotShown()
        {
            using var f = new SessionFixture().Add(RulesSchema.InteractiveObjectFolder,
                                                   Chest("story", new LockDef(true, 1, null, 1), new float[] { 1, 0, 1 }, "Sector_A"));
            var s = f.NewGame();
            s.Interaction.Objects.SectorEnabled = sector => sector != "Sector_A";
            Assert.That(s.Interaction.ChestMarks.Discover("story"), Is.False, "not in the world");
            s.Interaction.Objects.SectorEnabled = null;
            Assert.That(s.Interaction.ChestMarks.Discover("story"), Is.True);
            s.Interaction.Objects.SectorEnabled = sector => sector != "Sector_A";
            Assert.That(s.Interaction.ChestMarks.Visible, Is.Empty, "kept, but hidden while the sector is off");
        }

        /// <summary>fix-116 [owner]: "shown only by a filter" — the map's chest filter, hidden by default; the Video row is gone.</summary>
        [Test]
        public void Marks_ShowOnlyByTheMapFilter_HiddenByDefault_NoVideoRow()
        {
            Assert.That(MapChestMarksLayout.ShownByDefault, Is.False);
            Assert.That(System.Enum.GetNames(typeof(PortOptions.Row)), Has.No.Member("LockedChests"));
            Assert.That(typeof(PortOptions).GetProperty("LockedChestMarkers"), Is.Null);
            // fix-126: the chest button sits on the map views' filter bar (inside the map's top-right corner)
            Assert.That(MapFilterBarLayout.Order, Has.Member(MapFilter.Chests));
            // small, slightly transparent marks (G3's own markers and arrow are 32 px)
            Assert.That(MapChestMarksLayout.IconSize, Is.InRange(12f, 14f));
            Assert.That(MapChestMarksLayout.Opacity, Is.LessThan(1f).And.GreaterThan(0.5f));
        }

        [Test]
        public void Group_JoinsChestsAFewPixelsApart_AndCountsThem()
        {
            var groups = MapChestMarksLayout.Group(new[] { (100f, 100f), (104f, 103f), (300f, 50f), (98f, 106f), (309f, 50f), (330f, 50f) });
            Assert.That(groups.Select(g => g.Count), Is.EqualTo(new[] { 3, 2, 1 }));
            Assert.That(groups[0].X, Is.EqualTo((100f + 104f + 98f) / 3f).Within(1e-3));
            Assert.That(groups[0].Y, Is.EqualTo((100f + 103f + 106f) / 3f).Within(1e-3));
            Assert.That(groups[1].X, Is.EqualTo(304.5f).Within(1e-3), "the centre is the mean of its chests");
            Assert.That(groups[2].X, Is.EqualTo(330f), "25.5 px from the group's centre: a mark of its own");
        }

        [Test]
        public void Group_Empty_AndSingles()
        {
            Assert.That(MapChestMarksLayout.Group(new (float, float)[0]), Is.Empty);
            Assert.That(MapChestMarksLayout.Group(null), Is.Empty);
            var far = MapChestMarksLayout.Group(new[] { (0f, 0f), (MapChestMarksLayout.GroupRadius + 1f, 0f) });
            Assert.That(far.Select(g => g.Count), Is.EqualTo(new[] { 1, 1 }));
            Assert.That(MapChestMarksLayout.CountText(1), Is.Empty, "a single chest has no count");
            Assert.That(MapChestMarksLayout.CountText(3), Is.EqualTo("3"));
            Assert.That(MapChestMarksLayout.CountText(12), Is.EqualTo("9+"));
        }

        /// <summary>fix-116: G3 draws its HUD page (and so the game messages) under the overlay pages.</summary>
        [Test]
        public void GameMessages_LieUnderThePages_TheSidebarAbove()
        {
            Assert.That(GuiLayers.HudMessages, Is.LessThan(GuiLayers.Pages));
            Assert.That(GuiLayers.HudMessages, Is.GreaterThan(GuiLayers.Vitals));
            Assert.That(GuiLayers.Hud, Is.GreaterThan(GuiLayers.Pages), "the page sidebar takes clicks over the pages");
        }
    }
}
