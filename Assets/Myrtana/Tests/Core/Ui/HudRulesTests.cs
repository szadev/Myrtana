using System.Linq;
using G3.Formats.Gui;
using Myrtana.Ui.Skin;
using NUnit.Framework;

namespace Myrtana.Tests
{
    public class HudRulesTests
    {
        [Test]
        public void Visibility_FollowsTheOriginalPageTable()
        {
            Assert.That(HudVisibility.Of(HudPage.None), Is.EqualTo(HudParts.FocusBar | HudParts.FocusName | HudParts.Vitals | HudParts.QuickSlots | HudParts.Compass));
            Assert.That(HudVisibility.Of(HudPage.Sheet), Is.EqualTo(HudParts.Vitals | HudParts.QuickSlots | HudParts.Sidebar | HudParts.Compass));
            Assert.That(HudVisibility.Of(HudPage.Dialog), Is.EqualTo(HudParts.FocusBar), "a dialog keeps only the focus bar");
            Assert.That(HudVisibility.Of(HudPage.Exclusive), Is.EqualTo(HudParts.None), "trade hides the whole HUD");
        }

        [TestCase(0f, 1f, 0f)]
        [TestCase(1f, 0f, -90f)]
        [TestCase(-1f, 0f, 90f)]
        [TestCase(1f, 1f, -45f)]
        public void Needle_TurnsAgainstTheCameraYaw(float x, float z, float degrees) =>
            Assert.That(CompassRules.NeedleDegrees(x, z), Is.EqualTo(degrees).Within(1e-4));

        [Test]
        public void Needle_FacingSouthIsUpsideDown() =>
            Assert.That(System.Math.Abs(CompassRules.NeedleDegrees(0f, -1f)), Is.EqualTo(180f).Within(1e-4));

        [Test]
        public void MessageColors_AreGoldTealOrangeRed()
        {
            Assert.That(GameMessageColors.Of(GameMessageTone.Info), Is.EqualTo(G3Palette.Gold));
            Assert.That(GameMessageColors.Of(GameMessageTone.Result), Is.EqualTo(G3Palette.Teal));
            Assert.That(GameMessageColors.Of(GameMessageTone.Quest), Is.EqualTo(G3Palette.Orange));
            Assert.That(GameMessageColors.Of(GameMessageTone.Error), Is.EqualTo(G3Palette.BloodRed));
        }

        [Test]
        public void Message_FadesInDriftsUpAndExpires()
        {
            var feed = new GameMessageFeed(areaHeight: 200, lineHeight: 40);
            feed.Post("Game saved", GameMessageTone.Info, now: 10);

            var start = feed.Layout(10).Single();
            Assert.That(start.Top, Is.EqualTo(160));
            Assert.That(start.Alpha, Is.Zero);

            var later = feed.Layout(12).Single();
            Assert.That(later.Top, Is.EqualTo(120).Within(1e-3), "20 px/s");
            Assert.That(later.Alpha, Is.EqualTo(1));

            Assert.That(feed.Layout(15.8).Single().Alpha, Is.EqualTo(0.5f).Within(1e-3), "fades out in the last 0.4 s");
            Assert.That(feed.Layout(16), Is.Empty);
            Assert.That(feed.Count, Is.Zero);
        }

        [Test]
        public void NewMessage_PushesOlderOnesUpOneRow_OnTopOfTheirDrift()
        {
            // G3 PrintGameMessage: every shown message goes one row up, whatever its age (no "only if overlapping")
            var feed = new GameMessageFeed(areaHeight: 200, lineHeight: 40);
            feed.Post("first", GameMessageTone.Info, 0);
            feed.Post("second", GameMessageTone.Quest, 0.5);

            var lines = feed.Layout(0.5);
            Assert.That(lines.Select(l => l.Text), Is.EqualTo(new[] { "first", "second" }), "oldest first, newest at the bottom");
            Assert.That(lines[1].Top, Is.EqualTo(160));
            Assert.That(lines[0].Top, Is.EqualTo(160 - 40 - 10).Within(1e-3), "one row up and its own 0.5 s of drift");
        }

        [Test]
        public void Burst_OfLoot_StacksOneLinePerStack_NoMerging()
        {
            // the HUD control: 216 px, 20 px rows; a quick loot posts one GO_ItemTaken_Formatted line per stack at once
            var feed = new GameMessageFeed(areaHeight: 216, lineHeight: 20);
            Assert.That(feed.NewestTop, Is.EqualTo(196), "G3 row 10 = 200 px, kept inside the 216 px control");
            for (int i = 0; i < 5; i++) feed.Post("1 x Water received", GameMessageTone.Item, 3);
            var lines = feed.Layout(3);
            Assert.That(lines, Has.Count.EqualTo(5), "identical texts are not merged");
            Assert.That(lines.Select(l => l.Top), Is.EqualTo(new[] { 116f, 136f, 156f, 176f, 196f }));
            Assert.That(feed.Layout(9.0), Is.Empty, "all gone after 6 s");
        }

        [Test]
        public void Messages_LiveSixSecondsWhateverTheirNumber_DroppedOnlyWhenWhollyAbove()
        {
            var feed = new GameMessageFeed(areaHeight: 216, lineHeight: 20);
            for (int i = 0; i < 12; i++) feed.Post("m" + i, GameMessageTone.Info, 0);
            var lines = feed.Layout(0);
            // tops 196 … −24: m0 (−24) is wholly above the control and dropped, m1 (−4) still shows its lower part
            Assert.That(lines.First().Text, Is.EqualTo("m1"));
            Assert.That(lines, Has.Count.EqualTo(11));
            Assert.That(feed.Layout(1.5).First().Text, Is.EqualTo("m2"), "drift takes the top row out too");
        }

        [Test]
        public void ItemTone_IsTan_GainsGold()
        {
            Assert.That(GameMessageColors.Of(GameMessageTone.Item), Is.EqualTo(G3Palette.Tan));
            Assert.That(GameMessageColors.Of(GameMessageTone.Info), Is.EqualTo(G3Palette.Gold));
        }
    }

    public class ThemeTintMaskTests
    {
        [Test]
        public void RowBars_AreTintMasks_FrontLayersAreNot()
        {
            Assert.That(ThemeUssWriter.IsTintMask("listctrl_item_details_back"), Is.True);
            Assert.That(ThemeUssWriter.IsTintMask("listctrl_item_details_front"), Is.False);
            Assert.That(ThemeUssWriter.IsTintMask("listctrl_item_tiles_back"), Is.False);
        }

        [Test]
        public void FlatColourBar_BecomesWhiteKeepingAlpha()
        {
            var px = new byte[] { 107, 69, 0, 51, 107, 69, 0, 119, 0, 0, 0, 0 };
            Assert.That(GuiImageAnalysis.TryWhitenAlphaMask(px), Is.True);
            Assert.That(px, Is.EqualTo(new byte[] { 255, 255, 255, 51, 255, 255, 255, 119, 255, 255, 255, 0 }));
        }

        [Test]
        public void ColouredImage_IsLeftAlone()
        {
            var px = new byte[] { 107, 69, 0, 51, 100, 69, 0, 119 };
            Assert.That(GuiImageAnalysis.TryWhitenAlphaMask(px), Is.False);
            Assert.That(px[0], Is.EqualTo(107));
        }
    }
}
