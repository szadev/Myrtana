using System.Collections.Generic;
using System.IO;
using Myrtana.Core;
using Myrtana.Session;
using Myrtana.Ui.Skin;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>Title version line, HUD toggles, tutorial tips, quest location filter and patched strings (front-end-2).</summary>
    public class FrontEndTests
    {
        [Test]
        public void VersionLine_KeepsFirstSecondAndLastFieldOfTheVersionResource()
        {
            Assert.That(AppVersionText.Format(AppVersionText.CommunityPatch), Is.EqualTo("pb/cpt/sb v1.75.14"));
            Assert.That(AppVersionText.Format("1,60,25931,29"), Is.EqualTo("pb/cpt/sb v1.60.29"));
        }

        [Test]
        public void RemasterBrand_VersionLineAndCredits()
        {
            Assert.That(RemasterBrand.VersionLine("English", "2026-10-21 ff8b7e6"), Is.EqualTo("Gothic 3 Remaster — demo 2026-10-21, build ff8b7e6"));
            Assert.That(RemasterBrand.VersionLine("Russian", "2026-10-21 ff8b7e6"), Is.EqualTo("Gothic 3 Remaster — демо 2026-10-21, сборка ff8b7e6"));
            Assert.That(RemasterBrand.VersionLine("English", null), Is.EqualTo("Gothic 3 Remaster — demo"));
            Assert.That(RemasterBrand.VersionLine("German", "2026-10-21"), Is.EqualTo("Gothic 3 Remaster — demo 2026-10-21"));
            foreach (var lang in new[] { "English", "Russian" })
            {
                string c = RemasterBrand.CreditsCaption(lang);
                Assert.That(c, Does.StartWith("Gothic 3 Remaster"));
                Assert.That(c, Does.Contain("Community Patch 1.75"));
                Assert.That(c, Does.Not.Contain("Claude"));
            }
        }

        [Test]
        public void CtrlF1_HidesTheHudLayerButNotTheSidebarOrFocusName()
        {
            var toggle = new HudToggle();
            toggle.ToggleLayer();
            var parts = HudVisibility.Of(HudPage.None) & toggle.Mask;
            Assert.That(parts, Is.EqualTo(HudParts.FocusName));
            Assert.That(HudVisibility.Of(HudPage.Sheet) & toggle.Mask, Is.EqualTo(HudParts.Sidebar));
            toggle.ToggleLayer();
            Assert.That(HudVisibility.Of(HudPage.None) & toggle.Mask, Is.EqualTo(HudVisibility.Of(HudPage.None)));
        }

        [Test]
        public void ToggleHud_CyclesQuickSlotsThenCompassThenBars()
        {
            var toggle = new HudToggle();
            var all = HudVisibility.Of(HudPage.None);
            toggle.NextStep();
            Assert.That(all & toggle.Mask, Is.EqualTo(all & ~HudParts.QuickSlots));
            toggle.NextStep();
            Assert.That(all & toggle.Mask, Is.EqualTo(all & ~(HudParts.QuickSlots | HudParts.Compass)));
            toggle.NextStep();
            Assert.That(all & toggle.Mask, Is.EqualTo(HudParts.FocusBar | HudParts.FocusName));
            toggle.NextStep();
            Assert.That(all & toggle.Mask, Is.EqualTo(all), "the fourth press shows everything again");
        }

        [Test]
        public void Tips_AreShownOnceAndInOrder()
        {
            var tips = new TutorialTips();
            Assert.That(tips.Request(TutorialTip.Start), Is.True);
            Assert.That(tips.Request(TutorialTip.Inventory), Is.True);
            Assert.That(tips.Request(TutorialTip.Start), Is.False, "each occasion only once");
            Assert.That(tips.Next, Is.EqualTo(TutorialTip.Start));
            tips.Dismiss();
            Assert.That(tips.Next, Is.EqualTo(TutorialTip.Inventory));
            tips.Dismiss();
            Assert.That(tips.Next, Is.Null);
            Assert.That(TutorialTips.TextOf(TutorialTip.MissionLog).Value, Is.EqualTo("TUT_MissionLog"));
        }

        [Test]
        public void NoMoreTips_DropsTheQueueAndUsesUpLaterOccasions()
        {
            var tips = new TutorialTips();
            tips.Request(TutorialTip.Start);
            tips.Request(TutorialTip.Maps);
            tips.HideTips = true;
            tips.Dismiss();
            Assert.That(tips.Next, Is.Null);
            Assert.That(tips.Request(TutorialTip.Fight), Is.False);
            Assert.That(tips.HasSeen(TutorialTip.Fight), Is.True);
        }

        [Test]
        public void Tips_AreSavedWithTheGame()
        {
            var tips = new TutorialTips();
            tips.Request(TutorialTip.Start);
            tips.Request(TutorialTip.Bow);
            tips.HideTips = true;
            var stream = new MemoryStream();
            ((ISaveParticipant)tips).Write(new BinarySaveWriter(stream));
            stream.Position = 0;
            var loaded = new TutorialTips();
            loaded.Request(TutorialTip.Trade);
            ((ISaveParticipant)loaded).Read(new BinarySaveReader(stream), 1);
            Assert.That(loaded.HideTips, Is.True);
            Assert.That(loaded.HasSeen(TutorialTip.Bow) && loaded.HasSeen(TutorialTip.Start), Is.True);
            Assert.That(loaded.HasSeen(TutorialTip.Trade), Is.False, "loading replaces the state");
            Assert.That(loaded.Next, Is.Null, "nothing pending after a load");
            loaded.Reset();
            Assert.That(loaded.HideTips || loaded.HasSeen(TutorialTip.Bow), Is.False, "a new game starts over");
        }

        [TestCase("Myrtana/Silden_City/", "IDC_BUTTON_LOCATION_SILDEN")]
        [TestCase("Varant/MoraSul/", "IDC_BUTTON_LOCATION_MORA_SUL")]
        [TestCase("Nordmar/Innos_Monastery/", "IDC_BUTTON_LOCATION_INNOS_MONASTERY")]
        [TestCase("Hauptstory/", "IDC_BUTTON_LOCATION_MAIN")]
        [TestCase("Varant/BenErai/", "IDC_BUTTON_LOCATION_BEN_ERAI")]
        [TestCase("Nordmar/Hammerclan/", "IDC_BUTTON_LOCATION_HAMMER_CLAN")]
        [TestCase("Myrtana/Vangard_City/", "IDC_BUTTON_LOCATION_VANGARD")]
        public void QuestFolder_MatchesItsPlace(string folder, string control)
        {
            int index = QuestLocations.FirstOf(folder);
            Assert.That(index, Is.GreaterThanOrEqualTo(0));
            Assert.That(QuestLocations.All[index].Control, Is.EqualTo(control));
            Assert.That(QuestLocations.Matches(index, folder), Is.True);
            Assert.That(QuestLocations.Matches(QuestLocations.World, folder), Is.True, "World lists every quest");
        }

        [Test]
        public void QuestFilter_HasG3sTwentyFiveButtons_AndEmptyFoldersOnlyUnderWorld()
        {
            Assert.That(QuestLocations.All.Count, Is.EqualTo(25));
            Assert.That(QuestLocations.All[QuestLocations.World].IsAll, Is.True);
            Assert.That(QuestLocations.FirstOf("World/Vier_Freunde/"), Is.EqualTo(-1));
            Assert.That(QuestLocations.Matches(QuestLocations.FirstOf("Myrtana/Faring_City/"), ""), Is.False);
            var faring = QuestLocations.All[QuestLocations.FirstOf("Myrtana/Faring_City/")];
            Assert.That(faring.Name.Value, Is.EqualTo("GO_Enclave_Faring"));
            Assert.That(QuestLocations.FameGroup(faring, new[] { "Geldern", "faring" }), Is.EqualTo("faring"));
            var lago = QuestLocations.All[QuestLocations.FirstOf("Varant/Lago/")];
            Assert.That(QuestLocations.FameGroup(lago, new[] { "Enclave_Lago" }), Is.EqualTo("Enclave_Lago"));
        }

        [Test]
        public void QuestMap_CartoucheCaptionsMainAndWorld_PlacesNamedWhenHoveredOrPicked()
        {
            int main = QuestLocations.Main, world = QuestLocations.World;
            Assert.That(QuestLocations.All[main].Control, Is.EqualTo("IDC_BUTTON_LOCATION_MAIN"));
            Assert.That(QuestLocations.All[main].Name.Value, Is.EqualTo("HUD_Main"));
            Assert.That(QuestLocations.All[world].Name.Value, Is.EqualTo("HUD_All"));
            Assert.That(QuestLocations.CaptionControl(main), Is.EqualTo("IDC_LABEL_LOCATION_MAIN_2"));
            Assert.That(QuestLocations.CaptionControl(world), Is.EqualTo("IDC_LABEL_LOCATION_WORLD_2"));
            for (int i = 0; i < main; i++)
            {
                Assert.That(QuestLocations.CaptionControl(i), Is.Null, QuestLocations.All[i].Control);
                Assert.That(QuestLocations.All[i].Name.Value, Does.StartWith("GO_Enclave_"));
            }

            int hammer = QuestLocations.FirstOf("Nordmar/Hammerclan/"), faring = QuestLocations.FirstOf("Myrtana/Faring_City/");
            Assert.That(QuestLocations.NamedOnMap(world, -1), Is.EqualTo(-1), "World picked, nothing hovered: no map name");
            Assert.That(QuestLocations.NamedOnMap(main, -1), Is.EqualTo(-1));
            Assert.That(QuestLocations.NamedOnMap(hammer, -1), Is.EqualTo(hammer), "the picked place is named");
            Assert.That(QuestLocations.NamedOnMap(hammer, faring), Is.EqualTo(faring), "the hovered place wins");
            Assert.That(QuestLocations.NamedOnMap(hammer, main), Is.EqualTo(hammer), "hovering a cartouche entry keeps the pick");
            Assert.That(QuestLocations.NamedOnMap(world, world), Is.EqualTo(-1));
        }

        sealed class NoTables : IStringTableSource
        {
            public StringTable Load(string language) => null;
        }

        [Test]
        public void MissingCpString_ChestLocked_ComesFromThePatchTable()
        {
            var russian = new Localizer(new LanguageSettings("Russian", "Russian"), new NoTables());
            Assert.That(russian.Text(new TextId("HUD_ChestLocked")), Is.EqualTo("Сундук заперт!"));
            var polish = new Localizer(new LanguageSettings("Polish", "Polish"), new NoTables());
            Assert.That(polish.Text(new TextId("HUD_ChestLocked")), Is.EqualTo("The chest is locked!"), "English otherwise");
            Assert.That(polish.Text(new TextId("HUD_Nothing")), Is.EqualTo("[HUD_Nothing]"));
        }
    }
}
