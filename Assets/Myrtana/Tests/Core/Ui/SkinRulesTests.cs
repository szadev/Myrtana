using System.Collections.Generic;
using System.Linq;
using G3.Formats.Gui;
using Myrtana.Ui.Skin;
using NUnit.Framework;

namespace Myrtana.Tests
{
    public class SkinRulesTests
    {
        sealed class SetCatalog : IImageCatalog
        {
            readonly HashSet<string> _names;
            public SetCatalog(params string[] names) { _names = new HashSet<string>(names.Select(GuiSkinCatalog.ImageKey)); }
            public bool Contains(string imageName) => _names.Contains(GuiSkinCatalog.ImageKey(imageName));
        }

        [Test]
        public void StateResolution_FollowsPriority()
        {
            Assert.That(WidgetStates.Resolve(WidgetFlags.None), Is.EqualTo(WidgetState.Normal));
            Assert.That(WidgetStates.Resolve(WidgetFlags.Focused), Is.EqualTo(WidgetState.Focus));
            Assert.That(WidgetStates.Resolve(WidgetFlags.Focused | WidgetFlags.Hovered), Is.EqualTo(WidgetState.MouseOver));
            Assert.That(WidgetStates.Resolve(WidgetFlags.Hovered | WidgetFlags.Selected), Is.EqualTo(WidgetState.Pressed));
            Assert.That(WidgetStates.Resolve(WidgetFlags.Pressed), Is.EqualTo(WidgetState.Pressed));
            Assert.That(WidgetStates.Resolve(WidgetFlags.Selected | WidgetFlags.Disabled), Is.EqualTo(WidgetState.Disabled));
        }

        [Test]
        public void ThemeElement_GivesTintAndTextPerState()
        {
            var e = ListRowElement();
            Assert.That(e.Blend(WidgetState.Normal).IsInvisible, Is.True, "no bar in the normal state");
            Assert.That(e.Blend(WidgetStates.Resolve(WidgetFlags.Hovered)), Is.EqualTo(SkinColor.Parse("#FFC0C0")));
            Assert.That(e.Blend(WidgetStates.Resolve(WidgetFlags.Selected)), Is.EqualTo(SkinColor.Parse("#FF0000")));
            Assert.That(e.Text(WidgetState.MouseOver), Is.EqualTo(G3Palette.TextHighlight));
            Assert.That(e.Text(WidgetState.Normal), Is.EqualTo(G3Palette.Text));
        }

        [Test]
        public void Colour_ParsesAndFormats()
        {
            Assert.That(SkinColor.Parse("#E9D9C7D2"), Is.EqualTo(new SkinColor(0xE9, 0xD9, 0xC7, 210)));
            Assert.That(SkinColor.Parse("9F1813").A, Is.EqualTo(255));
            Assert.That(SkinColor.TryParse("#12345", out _), Is.False);
            Assert.That(G3Palette.SteelBlue.Hex, Is.EqualTo("#0C7097FF"));
        }

        [Test]
        public void ItemIcon_UsesTextureThenCategoryFallback()
        {
            var r = new ItemIconResolver(new SetCatalog("IT_Apple.tga", "IT_Spell_Fireball.tga", "IT_Spell_Fireball_Grey.tga"));
            Assert.That(r.Resolve("IT_Apple.tga", ItemIconCategory.Alchemy), Is.EqualTo("IT_Apple.tga"));
            Assert.That(r.Resolve("it_apple", ItemIconCategory.Alchemy), Is.EqualTo("it_apple"), "names are case-insensitive, extension optional");
            Assert.That(r.Resolve("", ItemIconCategory.Alchemy), Is.EqualTo("G3_HUD_Icon_Category_Food.tga"));
            Assert.That(r.Resolve(null, ItemIconCategory.Written), Is.EqualTo("G3_HUD_Icon_Category_Misc.tga"));
            Assert.That(r.Resolve("IT_Missing.tga", ItemIconCategory.Weapon), Is.EqualTo("G3_HUD_Icon_Category_Weapon.tga"));
            Assert.That(r.Resolve("IT_Spell_Fireball.tga", ItemIconCategory.Spellbook, learned: false), Is.EqualTo("IT_Spell_Fireball_Grey.tga"));
            Assert.That(r.Resolve("IT_Spell_Fireball.tga", ItemIconCategory.Spellbook, learned: true), Is.EqualTo("IT_Spell_Fireball.tga"));
            Assert.That(r.Resolve("IT_Apple.tga", ItemIconCategory.Alchemy, learned: false), Is.EqualTo("IT_Apple.tga"), "only spells/skills grey out");
        }

        [Test]
        public void QualityIcon_ShowsOneByPriority_NeverWorn()
        {
            Assert.That(ItemIconResolver.QualityIcon(ItemQualityFlags.Poison | ItemQualityFlags.Blessed), Is.EqualTo("G3_HUD_Icon_Quality_Blessed.tga"));
            Assert.That(ItemIconResolver.QualityIcon(ItemQualityFlags.Forged | ItemQualityFlags.Sharpened), Is.EqualTo("G3_HUD_Icon_Quality_Sharpened.tga"));
            Assert.That(ItemIconResolver.QualityIcon(ItemQualityFlags.Worn), Is.Null);
        }

        [Test]
        public void StackCount_Formats()
        {
            Assert.That(StackCountText.Format(1, ItemIconCategory.Misc, "k", "m"), Is.Null);
            Assert.That(StackCountText.Format(999, ItemIconCategory.Misc, "k", "m"), Is.EqualTo("999"));
            Assert.That(StackCountText.Format(15400, ItemIconCategory.Misc, "k", "m"), Is.EqualTo("15k"));
            Assert.That(StackCountText.Format(2500000, ItemIconCategory.Misc, "k", "m"), Is.EqualTo("2m"));
            Assert.That(StackCountText.Format(5, ItemIconCategory.Spellbook, "k", "m"), Is.Null);
            Assert.That(StackCountText.QuickSlotDigit(0), Is.EqualTo("1"));
            Assert.That(StackCountText.QuickSlotDigit(9), Is.EqualTo("0"));
            Assert.That(StackCountText.QuickSlotDigit(-1), Is.Null);
        }

        [Test]
        public void TileGrid_MatchesOriginalLists()
        {
            Assert.That((TileGrid.Columns(258, 17), TileGrid.Rows(482)), Is.EqualTo((5, 10)), "inventory");
            Assert.That((TileGrid.Columns(260, 17), TileGrid.Rows(384)), Is.EqualTo((5, 8)), "vendor");
            Assert.That((TileGrid.Columns(306, 17), TileGrid.Rows(144)), Is.EqualTo((6, 3)), "offer pool");
            Assert.That((TileGrid.Columns(480, 0), TileGrid.Rows(48)), Is.EqualTo((10, 1)), "quick slots");
        }

        [Test]
        public void Anchors_FollowGuiDllRule_At1920x1080()
        {
            // compass 16,660–116,760 anchor left+bottom → 16,972
            var h = GuiLayoutRules.Horizontal(16, 116, 1024, AnchorFlags.Left | AnchorFlags.Bottom).Resolve(1920);
            var v = GuiLayoutRules.Vertical(660, 760, 768, AnchorFlags.Left | AnchorFlags.Bottom).Resolve(1080);
            Assert.That((h.start, v.start), Is.EqualTo((16f, 972f)));
            // HP bar 807,688–1008,707 right+bottom → 1703,1000
            Assert.That(GuiLayoutRules.Horizontal(807, 1008, 1024, AnchorFlags.Right | AnchorFlags.Bottom).Resolve(1920).start, Is.EqualTo(1703f));
            // focus bar 411–612 anchored top only → horizontally centred → 859
            Assert.That(GuiLayoutRules.Horizontal(411, 612, 1024, AnchorFlags.Top).Resolve(1920).start, Is.EqualTo(859f));
            // dialog box 22–1002 bottom → 470
            Assert.That(GuiLayoutRules.Horizontal(22, 1002, 1024, AnchorFlags.Bottom).Resolve(1920).start, Is.EqualTo(470f));
            // message area stretches
            var m = GuiLayoutRules.Horizontal(87, 937, 1024, (AnchorFlags)15).Resolve(1920);
            Assert.That(m, Is.EqualTo((87f, 1920f - 87f - 87f)));
            // no flags (page sidebar 946,76–1008,576) = centred on both axes → 1394,232
            Assert.That(GuiLayoutRules.Horizontal(946, 1008, 1024, AnchorFlags.None).Resolve(1920).start, Is.EqualTo(1394f));
            Assert.That(GuiLayoutRules.Vertical(76, 576, 768, AnchorFlags.None).Resolve(1080).start, Is.EqualTo(232f));
        }

        [Test]
        public void StaticStyle_DecodesAlignment()
        {
            Assert.That(new StaticTextFormat(0x10000025).Align, Is.EqualTo(TextAlign.Center));
            Assert.That(new StaticTextFormat(0x10000026).Align, Is.EqualTo(TextAlign.Right));
            Assert.That(new StaticTextFormat(0x10000024).SingleLine, Is.True);
            Assert.That(new StaticTextFormat(0x10000000).SingleLine, Is.False);
        }

        [Test]
        public void StatusBars_ColourAndFraction()
        {
            Assert.That(StatusBarColors.Fill(StatusBarKind.Health), Is.EqualTo(G3Palette.BloodRed));
            Assert.That(StatusBarColors.Fill(StatusBarKind.Health, poisoned: true), Is.EqualTo(G3Palette.Teal));
            Assert.That(StatusBarColors.Fill(StatusBarKind.Stamina, exhausted: true), Is.EqualTo(G3Palette.ExhaustedGrey));
            Assert.That(StatusBarColors.Fill(StatusBarKind.Mana), Is.EqualTo(G3Palette.SteelBlue));
            Assert.That(StatusBarColors.Fraction(5, 0), Is.EqualTo(0f));
            Assert.That(StatusBarColors.Fraction(150, 100), Is.EqualTo(1f));
            Assert.That(StatusBarColors.Fraction(25, 100), Is.EqualTo(0.25f));
        }

        [Test]
        public void UssWriter_EmitsPartClassesWithStateCascade()
        {
            var theme = new ThemeData();
            theme.Elements.Add(ListRowElement());
            var front = ListRowElement();
            front.Name = "listctrl_item_details_front";
            front.Image = "theme:listctrl_item_details_front";
            theme.Elements.Add(front);
            string uss = ThemeUssWriter.Write(theme, k => "/T/" + k.Substring(6) + ".png");
            StringAssert.Contains(".g3-listctrl-item-details {", uss);
            StringAssert.Contains("url(\"/T/listctrl_item_details_back.png\")", uss);
            StringAssert.Contains(".g3-listctrl-item-details > .g3-front {", uss);
            StringAssert.Contains(".g3-listctrl-item-details:hover, .g3-listctrl-item-details.g3--hover {", uss);
            int hover = uss.IndexOf(":hover");
            int selected = uss.IndexOf(".g3--selected");
            int disabled = uss.IndexOf(":disabled");
            Assert.That(hover < selected && selected < disabled, "cascade order Focus < MouseOver < Pressed < Disabled");
            StringAssert.Contains("-unity-background-image-tint-color: rgba(255, 192, 192, 1)", uss);
            StringAssert.DoesNotContain(".g3-listctrl-item-details-front", uss);
        }

        [Test]
        public void SkinJson_RoundTrips()
        {
            var d = new GuiSkinData();
            d.Theme.Metrics["Border.Control.X"] = 3;
            d.Theme.Elements.Add(ListRowElement());
            d.Images.Add(new ImageData { Name = "IT_Apple.tga", Group = "it_alchemy", Width = 64, Height = 64 });
            d.Screens.Add(new WindowData
            {
                Class = "View", ResName = "IDD_VIEW_TALK_2", Rect = new[] { 227, 100, 797, 181 }, Image = "G3_HUD_View_Talk_Back.tga",
                Children = new List<WindowData> { new WindowData { Class = "Static", ResName = "IDC_LABEL_TALK_2", Rect = new[] { 19, 9, 545, 63 }, Font = new FontData { Size = 14, Bold = true } } },
            });
            var back = new GuiSkinCatalog(GuiSkinJson.Read(GuiSkinJson.Write(d)));
            Assert.That(back.Theme.Metric("Border.Control.X"), Is.EqualTo(3));
            Assert.That(back.Element("LISTCTRL_ITEM_DETAILS_BACK").Blend(WidgetState.Pressed), Is.EqualTo(SkinColor.Parse("#FF0000")));
            Assert.That(back.Image(@"\data\gui\icons\it_apple.TGA").Group, Is.EqualTo("it_alchemy"));
            var talk = back.Screen("IDD_VIEW_TALK_2");
            Assert.That((talk.Width, talk.Height), Is.EqualTo((570, 81)));
            Assert.That(talk.Find("IDC_LABEL_TALK_2").Font.Size, Is.EqualTo(14));
        }

        [Test]
        public void FrameDetection_FindsBorderOfFrameImages()
        {
            int w = 40, h = 30;
            var rgba = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (x < 3 || y < 2 || x >= w - 4 || y >= h - 5) rgba[(y * w + x) * 4 + 3] = 255;
            Assert.That(GuiImageAnalysis.TryDetectFrame(rgba, w, h, out int l, out int t, out int r, out int b), Is.True);
            Assert.That((l, t, r, b), Is.EqualTo((3, 2, 4, 5)));
            for (int i = 3; i < rgba.Length; i += 4) rgba[i] = 255;
            Assert.That(GuiImageAnalysis.TryDetectFrame(rgba, w, h, out _, out _, out _, out _), Is.False, "filled image");
        }

        static ThemeElementData ListRowElement()
        {
            var e = new ThemeElementData { Name = "listctrl_item_details_back", Image = "theme:listctrl_item_details_back", Width = 200, Height = 40 };
            string[] blend = { "#00000000", "#C0C0C0FF", "#00000000", "#FF8080FF", "#FFC0C0FF", "#FF0000FF" };
            string[] text = { "#E9D9C7D2", "#E9D9C780", "#B49B8280", "#FFEBDCFF", "#FFEBDCFF", "#E9D9C7FF" };
            for (int i = 0; i < 6; i++) e.States.Add(new StateColors { Text = text[i], Blend = blend[i] });
            return e;
        }
    }
}
