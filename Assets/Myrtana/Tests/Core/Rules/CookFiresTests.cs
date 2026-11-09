using System.Linq;
using Myrtana.Core;
using Myrtana.Import.G3;
using Myrtana.Rules;
using Myrtana.Session;
using Myrtana.Simulation;
using Myrtana.Ui.Skin;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// fix-88: the port's cooking fires ([owner] option "Cook on any fire"): which decorative G3 fires become stations (CP
    /// 1.75 mesh bounds and effect offsets), where the hero cooks, the campfire's rules, the option and the focus names.
    /// </summary>
    public sealed class CookFiresTests
    {
        // CP 1.75 meshes (cm): bounds and gCEffect_PS offsets of the world entities (G3Cli objects probe Fire*)
        static readonly float[] HearthMin = { -139, 0, -140 }, HearthMax = { 186, 108, 0 }, HearthFire = { -55, 30, -65 };   // Fireplace_04
        static readonly float[] WideMin = { -180, 0, -188 }, WideMax = { 182, 203, 0 }, WideFire = { 0, 60, -50 };           // Fireplace_02/03
        static readonly float[] LargeMin = { -254, 0, -309 }, LargeMax = { 254, 403, 1 }, LargeFire = { 0, 0, -100 };        // Fireplace_Large_01
        static readonly float[] CageMin = { -33, 0, -33 }, CageMax = { 33, 75, 33 }, Centre = { 0, 35, 0 };                  // Fire_Cage_01
        static readonly float[] BasinMin = { -73, 0, -69 }, BasinMax = { 73, 63, 69 };                                       // Varant_Firebasin_01
        static readonly float[] PelvisMin = { -76, -5, -225 }, PelvisMax = { 77, 348, 85 };                                  // Fire_Pelvis_01

        static InteractiveObjectDef Fire(CookFireDef fire, float[] position = null, float[] forward = null) =>
            new InteractiveObjectDef("fire", "G3_Object_Fireplace_04", InteractiveKind.Station, CookFires.UseType, "G3_Object_Fireplace_04.xcmsh",
                                     position ?? new float[] { 10, 0, 20 }, forward ?? new float[] { 0, 0, 1 }, new float[] { 0, 1, 0 }, 1,
                                     default, null, null, null, null, null, null, 0f, fire);

        static CookFireDef Hearth() =>
            CookFires.Classify("G3_Object_Fireplace_04", "eff_env_fire_fireplace_01", HearthFire, HearthMin, HearthMax);

        [Test]
        public void LitFireplaces_OpenToTheirFront_TheHeroSitsBeforeTheOpening()
        {
            var hearth = Hearth();
            Assert.That(hearth, Is.Not.Null);
            Assert.That(hearth.Fire, Is.EqualTo(new[] { -0.55f, 0.3f, -0.65f }).Within(1e-4f));
            Assert.That(hearth.Spot, Is.EqualTo(new[] { -0.55f, 0f, -1.4f - CookFires.FrontGap }).Within(1e-4f), "in front of the mesh, at the flames' x");
            var wide = CookFires.Classify("G3_Object_Fireplace_02", "eff_env_fire_fireplace_01", WideFire, WideMin, WideMax);
            Assert.That(wide.Spot[2], Is.EqualTo(-1.88f - CookFires.FrontGap).Within(1e-4f), "clear of the raised hearth slab");
            var large = CookFires.Classify("G3_Object_Fireplace_Large_01", "eff_env_fire_fireplace_02", LargeFire, LargeMin, LargeMax);
            Assert.That(large, Is.Not.Null);
            Assert.That(large.Spot, Is.Not.Null);
        }

        [Test]
        public void RoundFires_BrazierAndBasin_TheHeroSitsOnHisSide()
        {
            var cage = CookFires.Classify("G3_Object_Fire_Cage_01", "eff_env_fire_campfire_02", Centre, CageMin, CageMax);
            var basin = CookFires.Classify("G3_Object_Varant_Firebasin_01", "eff_env_fire_bowl_01", Centre, BasinMin, BasinMax);
            Assert.That(cage.Spot, Is.Null);
            Assert.That(cage.Reach, Is.EqualTo(0.33f + CookFires.RimGap).Within(1e-4f));
            Assert.That(basin.Reach, Is.EqualTo(0.73f + CookFires.RimGap).Within(1e-4f));
        }

        [Test]
        public void ColdOrUnreachableFires_StayDecoration()
        {
            // no effect: the smithy fireplace and the chimney hood (CP 1.75: 9 + 15, no gCEffect_PS)
            Assert.That(CookFires.Classify("G3_Object_Fireplace_Blacksmith_01", null, null, WideMin, WideMax), Is.Null);
            Assert.That(CookFires.Classify("G3_Object_Fireplace_Hood_01", "", null, WideMin, WideMax), Is.Null);
            // the 3.5 m orc fire pelvis, a torch, a fireplace mesh with another effect
            Assert.That(CookFires.Classify("G3_Object_Fire_Pelvis_01", "eff_env_fire_bowl_01", Centre, PelvisMin, PelvisMax), Is.Null);
            Assert.That(CookFires.Classify("G3_Object_Torch_01", "eff_env_fire_torch_01", Centre, CageMin, CageMax), Is.Null);
            Assert.That(CookFires.Classify("G3_Object_Fireplace_04", "eff_env_misc_flies_01", HearthFire, HearthMin, HearthMax), Is.Null);
        }

        [Test]
        public void HeroSpot_FollowsTheObjectsFrame_AndFacesTheFlames()
        {
            var (at, yaw) = CookFires.HeroSpot(Fire(Hearth()), 0, 0);
            Assert.That(at, Is.EqualTo(new[] { 9.45f, 0f, 18.2f }).Within(1e-3f));
            Assert.That(yaw, Is.EqualTo(0f).Within(1e-3f), "faces +Z, into the hearth");
            // the same hearth turned to face +X: its opening looks to -X
            (at, yaw) = CookFires.HeroSpot(Fire(Hearth(), forward: new float[] { 1, 0, 0 }), 0, 0);
            Assert.That(at, Is.EqualTo(new[] { 8.2f, 0f, 20.55f }).Within(1e-3f));
            Assert.That(yaw, Is.EqualTo(90f).Within(1e-3f));
            // a brazier at the origin, the hero east of it: he sits at its east rim, facing west
            var cage = CookFires.Classify("G3_Object_Fire_Cage_01", "eff_env_fire_campfire_02", Centre, CageMin, CageMax);
            (at, yaw) = CookFires.HeroSpot(Fire(cage, new float[] { 0, 0, 0 }), 5, 0);
            Assert.That(at, Is.EqualTo(new[] { 0.78f, 0f, 0f }).Within(1e-3f));
            Assert.That(yaw, Is.EqualTo(-90f).Within(1e-3f));
        }

        [Test]
        public void CookingFire_IsACampfire_UsableOnlyWithTheOption_SingleUser()
        {
            var fire = Fire(Hearth());
            Assert.That(CookFires.StationUseType(fire), Is.EqualTo("Campfire"));
            Assert.That(CookFires.Usable(fire, cookOnAnyFire: true), Is.True);
            Assert.That(CookFires.Usable(fire, cookOnAnyFire: false), Is.False, "option No = original: decoration");
            Assert.That(ObjectFocusRules.IsDecorative(fire), Is.False);
            Assert.That(ObjectOccupancy.IsShared(fire.UseType), Is.False, "one cook at a fireplace");
            var campfire = new InteractiveObjectDef("c", "G3_Object_Interact_Campfire_01", InteractiveKind.Station, "Campfire", "c.xcmsh",
                                                    null, null, null, 1, default, null, null, null, null);
            Assert.That(CookFires.StationUseType(campfire), Is.EqualTo("Campfire"));
            Assert.That(CookFires.Usable(campfire, cookOnAnyFire: false), Is.True, "G3's own stations do not depend on the option");
        }

        [Test]
        public void CookingFire_OpensTheCampfiresFryPage_AndFriesMeat()
        {
            static ItemDef Item(string id) =>
                new ItemDef(id, new TextId("FO_" + id), default, ItemCategory.Alchemy, "None", EquipSlot.None, 0, 0, null, null,
                            null, null, null, null, null, null, ItemFlags.None);
            using var f = new SessionFixture()
                .Add(RulesSchema.ItemFolder, Item("It_Meat_Raw"), Item("It_Meat_Fried"))
                .Add(RulesSchema.CraftingRulesFolder, G3CraftingImporter.Rules())
                .Add(RulesSchema.InteractiveObjectFolder, Fire(Hearth()));
            var s = f.NewGame();
            s.Items.Inventories.Give(ActorId.Player, "It_Meat_Raw", 1);
            var check = s.Crafting.Crafting.Check(CookFires.StationUseType(s.Content.Database.All<InteractiveObjectDef>().Single()));
            Assert.That(check.Opens, Is.True);
            Assert.That(check.Rule.Page, Is.EqualTo(StationPage.Fry));
            Assert.That(s.Crafting.Crafting.Fry("It_Meat_Raw", 1, out int fried), Is.EqualTo(TransformVerdict.Done));
            Assert.That(fried, Is.EqualTo(1));
            Assert.That(s.Items.Inventories.Of(ActorId.Player).Count("It_Meat_Fried"), Is.EqualTo(1));
        }

        [Test]
        public void FocusNames_PortTextsForTheFires_NothingForAMissingString()
        {
            Assert.That(PatchStrings.TryGet("Russian", "FO_G3_Object_Fireplace_04", out var hearth) ? hearth : null, Is.EqualTo("Очаг"));
            Assert.That(PatchStrings.TryGet("Russian", "FO_G3_Object_Fireplace_Large_01", out var large) ? large : null, Is.EqualTo("Камин"));
            Assert.That(PatchStrings.TryGet("English", "FO_G3_Object_Fire_Cage_01", out var cage) ? cage : null, Is.EqualTo("Brazier"));
            Assert.That(PatchStrings.TryGet("Polish", "FO_G3_Object_Varant_Firebasin_01", out var basin) ? basin : null, Is.EqualTo("Fire basin"));
            using var f = new SessionFixture().Add(RulesSchema.InteractiveObjectFolder, Fire(Hearth()),
                new InteractiveObjectDef("spot", "G3_Object_Interact_Treasure_Spot_01", InteractiveKind.Treasure, "PirateTreasure", "t.xcmsh",
                                         null, null, null, 1, default, null, null, null, null));
            var s = f.NewGame();
            var db = s.Content.Database;
            Assert.That(HudFocus.ObjectName(s, db.All<InteractiveObjectDef>().Single(o => o.Id == "spot")), Is.Empty,
                        "G3 draws only the localized FO_ string: no raw entity name, no [FO_…] marker");
            Assert.That(HudFocus.ObjectName(s, db.All<InteractiveObjectDef>().Single(o => o.Id == "fire")), Is.Not.Empty.And.Not.Contains("FO_"));
        }

        [Test]
        public void LootTitle_SameAsTheFocusName_NothingForAMissingString()
        {
            using var f = new SessionFixture().Add(RulesSchema.InteractiveObjectFolder, Fire(Hearth()),
                new InteractiveObjectDef("spot", "G3_Object_Interact_Treasure_Spot_01", InteractiveKind.Treasure, "PirateTreasure", "t.xcmsh",
                                         null, null, null, 1, default, null, null, null, null));
            var s = f.NewGame();
            Assert.That(HudFocus.LootTitle(s, default, "spot"), Is.Empty, "G3 gCHUDViewLootPool2: localized FO_ text only (fix-94)");
            Assert.That(HudFocus.LootTitle(s, default, "missing"), Is.Empty);
            Assert.That(HudFocus.LootTitle(s, default, "fire"), Is.EqualTo(HudFocus.ObjectName(s, s.Content.Database.All<InteractiveObjectDef>().Single(o => o.Id == "fire"))));
        }

        [Test]
        public void Option_DefaultYes_Owner_RowAboveTheMouseHeading()
        {
            var port = new PlayerOptions().Port;
            Assert.That(port.CookOnAnyFire, Is.True, "[owner] default Yes (fix-88)");
            Assert.That(PortOptions.RowText(PortOptions.Row.CookFire, "Russian"), Is.EqualTo("Готовить на любом огне"));
            Assert.That(PortOptions.ValueText(PortOptions.Row.CookFire, false, "Russian"), Is.EqualTo("Нет (оригинал)"));
            Assert.That(PortOptions.ValueText(PortOptions.Row.CookFire, true, "Russian"), Is.EqualTo("Да"));
            Assert.That(PortOptions.ValueText(PortOptions.Row.CookFire, true, "English"), Is.EqualTo("Yes"));
            float top = ControlsPortRowsLayout.CookFireTop;
            Assert.That(top, Is.GreaterThanOrEqualTo(ControlsPortRowsLayout.FrameTop), "inside the page frame");
            Assert.That(ControlsPortRowsLayout.RowBottom(top), Is.LessThan(ControlsPortRowsLayout.MouseHeadingTop), "above G3's Mouse heading");
            Assert.That(ControlsPortRowsLayout.RowBottom(top), Is.LessThan(ControlsPortRowsLayout.TurnKeysTop), "clear of the other port rows");
        }
    }
}
