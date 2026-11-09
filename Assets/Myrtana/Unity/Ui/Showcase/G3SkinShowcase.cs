using System.Collections.Generic;
using System.Linq;
using Myrtana.Ui.Skin;
using Myrtana.Unity.Ui.Components;
using Myrtana.Unity.Ui.Layout;
using Myrtana.Unity.Ui.Skin;
using UnityEngine;
using UnityEngine.UIElements;

namespace Myrtana.Unity.Ui.Showcase
{
    /// <summary>
    /// Renders the G3 skin components with the imported original assets, for screenshots and visual checks
    /// (scene <c>Assets/Myrtana/Unity/Ui/Showcase/G3UiShowcase.unity</c>; F1/F2/F3 switch the page). Pages:
    /// <see cref="Page.Components"/> (every component and state), <see cref="Page.Dialog"/> (the dialog page and the
    /// subtitle box built from the original layouts) and <see cref="Page.Hud"/> (the HUD page layout with bars and
    /// quick slots). Sample data only — no game state.
    /// </summary>
    public sealed class G3SkinShowcase : MonoBehaviour
    {
        public enum Page { Components, Dialog, Hud }

        public Page page = Page.Components;

        UIDocument _doc;
        G3Skin _skin;
        Page _shown = (Page)(-1);

        public static G3SkinShowcase Instance { get; private set; }

        void OnEnable()
        {
            Instance = this;
            _skin = G3Skin.Load();
            _doc = G3PanelSettings.CreateDocument("G3 Skin Showcase", GuiPanelKind.Page, transform);
        }

        void OnDisable()
        {
            if (Instance == this) Instance = null;
        }

        void Update()
        {
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb != null)
            {
                if (kb.f1Key.wasPressedThisFrame) page = Page.Components;
                if (kb.f2Key.wasPressedThisFrame) page = Page.Dialog;
                if (kb.f3Key.wasPressedThisFrame) page = Page.Hud;
            }
            if (page != _shown) Rebuild();
        }

        public void Rebuild()
        {
            _shown = page;
            var root = _doc.rootVisualElement;
            root.Clear();
            var board = new VisualElement { name = "showcase" };
            board.AddToClassList("g3-fill-parent");
            board.style.backgroundColor = new Color(0.09f, 0.08f, 0.07f);
            root.Add(board);
            if (_skin == null)
            {
                board.Add(new Label("G3 skin not imported — run G3/GUI/Import") { style = { color = Color.white, fontSize = 24 } });
                return;
            }
            switch (page)
            {
                case Page.Components: BuildComponents(board); break;
                case Page.Dialog: BuildDialog(board); break;
                case Page.Hud: BuildHud(board); break;
            }
        }

        // ---------------------------------------------------------------- components

        void BuildComponents(VisualElement board)
        {
            var col1 = Column(board, 16, 12, 420);
            Caption(col1, "G3Button: normal / hover / selected / disabled (EN | RU)");
            foreach (var (en, ru, flags) in new[]
                     { ("New Game", "Новая игра", WidgetFlags.None), ("Load Game", "Загрузить игру", WidgetFlags.Hovered),
                       ("Options", "Настройки", WidgetFlags.Selected), ("Save Game", "Сохранить игру", WidgetFlags.Disabled) })
            {
                var row = new VisualElement { style = { flexDirection = FlexDirection.Row, marginBottom = 2 } };
                foreach (var text in new[] { en, ru })
                {
                    var b = new G3Button(text);
                    b.style.width = 200;
                    b.style.height = 32;
                    b.style.marginRight = 4;
                    ForceButton(b, flags);
                    row.Add(b);
                }
                col1.Add(row);
            }

            Caption(col1, "G3ListRow: normal / hover / selected / focus");
            var rows = new VisualElement();
            rows.style.width = 410;
            rows.style.backgroundColor = new Color(0.05f, 0.09f, 0.07f);
            col1.Add(rows);
            foreach (var (text, flags) in new[]
                     { ("Who are you? — Кто ты?", WidgetFlags.None), ("I want into the fortress! — Я хочу в крепость!", WidgetFlags.Hovered),
                       ("What's new? — Что нового? (ä ö ü ß)", WidgetFlags.Selected), ("I have to go. — Мне пора.", WidgetFlags.Focused) })
            {
                var r = new G3ListRow(text);
                r.style.minHeight = G3ListView.DefaultRowHeight;
                r.SetFlags(flags);
                rows.Add(r);
            }

            Caption(col1, "G3TabBar (inventory categories) + sidebar tabs");
            var tabs = new G3TabBar(false) { ItemWidth = 48, ItemHeight = 26, Spacing = 2, IconSize = 16 };
            foreach (var icon in new[] { "Misc", "Food", "Artefact", "Armor", "Weapon" })
                tabs.AddTab("G3_HUD_Icon_Category_" + icon + ".tga");
            tabs.Select(4);
            tabs.Tabs[1].SetFlags(WidgetFlags.Hovered);
            col1.Add(tabs);

            Caption(col1, "G3StatusBar: HP / SP / MP / poisoned / exhausted");
            AddBar(col1, StatusBarKind.Health, 75, false, false);
            AddBar(col1, StatusBarKind.Stamina, 50, false, false);
            AddBar(col1, StatusBarKind.Mana, 30, false, false);
            AddBar(col1, StatusBarKind.Health, 60, true, false);
            AddBar(col1, StatusBarKind.Stamina, 90, false, true);

            Caption(col1, "G3Tooltip / G3Frame");
            var tipRow = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.FlexStart } };
            tipRow.Add(new G3Tooltip("Weapons / Оружие"));
            var frame = new G3Frame { style = { width = 160, height = 60, marginLeft = 8 } };
            frame.Content.Add(new Label("Frame / Рамка") { style = { color = G3Palette.Text.ToColor(), fontSize = 16 } });
            tipRow.Add(frame);
            col1.Add(tipRow);

            // column 2: item tiles and grid
            var col2 = Column(board, 460, 12, 320);
            Caption(col2, "G3ItemTile: count / quality / quick slot / equipped / grey");
            var samples = SampleTiles();
            var tileRow = new VisualElement { style = { flexDirection = FlexDirection.Row, flexWrap = Wrap.Wrap, width = 300 } };
            for (int i = 0; i < samples.Count; i++)
            {
                var t = new G3ItemTile();
                t.style.marginRight = 2;
                t.style.marginBottom = 2;
                t.Bind(samples[i], "т", "м");
                if (i == 1) t.SetFlags(WidgetFlags.Hovered);
                if (i == 2) t.SetFlags(WidgetFlags.Selected);
                tileRow.Add(t);
            }
            col2.Add(tileRow);

            Caption(col2, "G3ItemGrid in the inventory view");
            var inv = new VisualElement();
            inv.style.width = 312;
            inv.style.height = 420;
            inv.style.overflow = Overflow.Hidden;
            var invBack = new G3Image("G3_HUD_View_Inventory_Back.tga", nativeSize: true);
            invBack.style.position = Position.Absolute;
            inv.Add(invBack);
            var grid = new G3ItemGrid(258, 482);
            grid.style.position = Position.Absolute;
            grid.style.left = 26;
            grid.style.top = 85;
            grid.SetItems(SampleGrid());
            grid.Select(3);
            inv.Add(grid);
            var title = new Label("Оружие") { style = { position = Position.Absolute, left = 26, top = 12, width = 258, height = 25 } };
            G3Text.Font(title, 24, true, true);
            title.style.unityTextAlign = TextAnchor.MiddleCenter;
            title.style.color = G3Palette.Label.ToColor();
            inv.Add(title);
            var invTabs = new G3TabBar(false, reversed: true) { ItemWidth = 48, ItemHeight = 26, Spacing = 2, IconSize = 16 }; // style 0x502 (fix-85)
            foreach (var icon in new[] { "Misc", "Food", "Artefact", "Armor", "Weapon" }) invTabs.AddTab("G3_HUD_Icon_Category_" + icon + ".tga");
            invTabs.Select(4);
            invTabs.style.position = Position.Absolute;
            invTabs.style.left = 30;
            invTabs.style.top = 46;
            inv.Add(invTabs);
            col2.Add(inv);

            // column 3: item panel + talk box
            var col3 = Column(board, 840, 12, 420);
            Caption(col3, "G3ItemPanel (IDD_VIEW_ITEM_2 layout)");
            var panel = new G3ItemPanel(_skin);
            panel.Bind(SamplePanel());
            col3.Add(panel);
            Caption(col3, "G3TalkBox (IDD_VIEW_TALK_2, shown at 0.57 scale)");
            var talk = new G3TalkBox(_skin) { Text = "It was long ago, but the deeds of Einar are still sung at the fires of my clan." };
            talk.style.scale = new Scale(new Vector3(0.57f, 0.57f, 1));
            talk.style.transformOrigin = new TransformOrigin(0, 0);
            col3.Add(talk);
        }

        static void ForceButton(G3Button b, WidgetFlags flags)
        {
            b.EnableInClassList(ThemeUssWriter.HoverClass, (flags & WidgetFlags.Hovered) != 0);
            b.EnableInClassList(ThemeUssWriter.SelectedClass, (flags & WidgetFlags.Selected) != 0);
            if ((flags & WidgetFlags.Disabled) != 0) b.SetEnabled(false);
        }

        static void AddBar(VisualElement parent, StatusBarKind kind, int value, bool poisoned, bool exhausted)
        {
            var bar = new G3StatusBar(kind) { Poisoned = poisoned, Exhausted = exhausted };
            bar.SetValue(value, 100, immediate: true);
            bar.style.marginBottom = 3;
            parent.Add(bar);
        }

        List<ItemTileModel> SampleTiles()
        {
            string Pick(string group, int skip = 0) =>
                _skin.Catalog.Data.Images.Where(i => i.Group == group && !i.Name.ToLowerInvariant().Contains("_grey")).Skip(skip).FirstOrDefault()?.Name;
            string spell = _skin.Catalog.Data.Images.Where(i => i.Group == "it_spells" && i.Name.ToLowerInvariant().Contains("_grey"))
                .Select(i => i.Name.Replace("_Grey", "").Replace("_grey", "")).FirstOrDefault();
            return new List<ItemTileModel>
            {
                new ItemTileModel { Texture = Pick("it_alchemy"), Category = ItemIconCategory.Alchemy, Amount = 12, QuickSlot = 0 },
                new ItemTileModel { Texture = Pick("it_weapons", 3), Category = ItemIconCategory.Weapon, Equipped = true, Quality = ItemQualityFlags.Sharpened },
                new ItemTileModel { Texture = Pick("it_armor", 2), Category = ItemIconCategory.Armor, Equipped = true },
                new ItemTileModel { Texture = Pick("it_misc", 5), Category = ItemIconCategory.Misc, Amount = 15400 },
                new ItemTileModel { Texture = Pick("it_weapons", 12), Category = ItemIconCategory.Weapon, Quality = ItemQualityFlags.Blessed | ItemQualityFlags.Poison, QuickSlot = 9 },
                new ItemTileModel { Texture = spell, Category = ItemIconCategory.Spellbook, Learned = false },
                new ItemTileModel { Texture = "", Category = ItemIconCategory.Artefact, Amount = 3 },
                new ItemTileModel { Texture = "IT_DoesNotExist.tga", Category = ItemIconCategory.Weapon },
            };
        }

        List<ItemTileModel> SampleGrid()
        {
            var list = new List<ItemTileModel>();
            var weapons = _skin.Catalog.Data.Images.Where(i => i.Group == "it_weapons" && !i.Name.ToLowerInvariant().Contains("_grey")).Take(17).ToList();
            for (int i = 0; i < weapons.Count; i++)
                list.Add(new ItemTileModel
                {
                    Texture = weapons[i].Name, Category = ItemIconCategory.Weapon, Equipped = i == 0,
                    Amount = weapons[i].Name.ToLowerInvariant().Contains("arrow") ? 120 : 1,
                    Quality = i == 2 ? ItemQualityFlags.Forged : ItemQualityFlags.None, QuickSlot = i == 1 ? 1 : -1,
                });
            return list;
        }

        ItemPanelModel SamplePanel()
        {
            var weapon = _skin.Catalog.Data.Images.Where(i => i.Group == "it_weapons").Skip(3).FirstOrDefault()?.Name;
            return new ItemPanelModel
            {
                Name = "Меч паладина",
                Texture = weapon,
                Category = ItemIconCategory.Weapon,
                Description = "A fine sword from Vengard. Остро заточен.",
                Rows =
                {
                    new ItemStatRow(ItemStatKind.RequirementMet, "Сила", "80"),
                    new ItemStatRow(ItemStatKind.RequirementUnmet, "Одноручное", "Мастер"),
                    new ItemStatRow(ItemStatKind.Damage, "Damage (blade)", "95"),
                    new ItemStatRow(ItemStatKind.Quality, "Заточено", "(+10)", "G3_HUD_Icon_Quality_Sharpened.tga"),
                    new ItemStatRow(ItemStatKind.Quality, "Освящено", "(+10)", "G3_HUD_Icon_Quality_Blessed.tga"),
                    new ItemStatRow(ItemStatKind.Modifier, "Сила", "+5"),
                    new ItemStatRow(ItemStatKind.Gold, "Value / Стоимость", "1.250"),
                },
            };
        }

        // ---------------------------------------------------------------- full screens from layout data

        void BuildDialog(VisualElement board)
        {
            var opt = new G3LayoutBuilder.Options { ExpandViews = true };
            var page = G3LayoutBuilder.Build(_skin, "IDD_PAGE_DIALOG_2", opt);
            page.AddToClassList("g3-fill-parent");
            page.style.width = StyleKeyword.Null;
            page.style.height = StyleKeyword.Null;
            board.Add(page);
            var slot = page.Q("IDC_LISTCTRL_DIALOG_2");
            var list = new G3ListView { HoverSelects = true };
            list.AddToClassList("g3-fill-parent");
            list.SetItems(new[]
            {
                "Who are you?", "Кто ты?", "Will you let me into the fortress?", "Ты пропустишь меня в крепость?",
                "What do you know about Faring?", "Что ты знаешь о Фаринге?", "Tell me about the orcs.", "Мне нужно идти.", "END", "КОНЕЦ",
            });
            list.Select(1);
            list.Rows[3].SetFlags(WidgetFlags.Hovered);
            slot?.Add(list);

            // the subtitle box is part of the HUD page (IDD_PAGE_MAIN_2 IDC_VIEW_TALK_2, top-centred)
            var hud = _skin.Catalog.Screen("IDD_PAGE_MAIN_2");
            var talkSlot = hud?.Find("IDC_VIEW_TALK_2");
            if (talkSlot != null)
            {
                var talk = new G3TalkBox(_skin) { Text = "Давно это было, но о подвигах Эйнара до сих пор поют у костров моего клана. Он был великим героем." };
                G3LayoutBuilder.Place(talk, talkSlot, hud.Width, hud.Height);
                board.Add(talk);
            }
        }

        void BuildHud(VisualElement board)
        {
            var opt = new G3LayoutBuilder.Options { ExpandViews = true, OutlineSlots = true };
            var page = G3LayoutBuilder.Build(_skin, "IDD_PAGE_MAIN_2", opt);
            page.AddToClassList("g3-fill-parent");
            page.style.width = StyleKeyword.Null;
            page.style.height = StyleKeyword.Null;
            board.Add(page);

            // bars: the layout's progress bar slots are filled by G3StatusBar's fill rule; here the whole view is replaced
            foreach (var (view, kind, value) in new[]
                     { ("IDC_VIEW_BAR_HEALTH_2", StatusBarKind.Health, 80), ("IDC_VIEW_BAR_ENDURANCE_2", StatusBarKind.Stamina, 55),
                       ("IDC_VIEW_BAR_MANA_2", StatusBarKind.Mana, 35), ("IDC_VIEW_BAR_FOCUS_2", StatusBarKind.Focus, 65) })
            {
                var host = page.Q(view);
                if (host == null) continue;
                host.Clear();
                var bar = new G3StatusBar(kind);
                bar.SetValue(value, 100, immediate: true);
                host.Add(bar);
            }
            var quick = page.Q("IDC_LISTCTRL_QUICKSLOTS_2");
            if (quick != null)
            {
                var grid = new G3ItemGrid(480, 48, scrollable: false);
                var items = SampleTiles().Take(6).ToList();
                for (int i = 0; i < items.Count; i++) items[i].QuickSlot = i;
                grid.SetItems(items);
                quick.Add(grid);
            }
            var sidebarTabs = page.Q("IDC_TABCTRL_PAGES_2");
            var tabData = _skin.Catalog.Screen("IDD_VIEW_BAR_PAGES_2")?.Find("IDC_TABCTRL_PAGES_2");
            if (sidebarTabs != null && tabData != null)
            {
                var bar = G3TabBar.FromLayout(tabData);
                foreach (var icon in new[] { "Inventory", "Character", "Book", "Map", "Log" }) bar.AddTab("G3_HUD_Icon_Page_" + icon + ".tga");
                bar.Select(0);
                sidebarTabs.Add(bar);
            }
            var talkLabel = page.Q<Label>("IDC_LABEL_TALK_2");
            if (talkLabel != null) talkLabel.text = "Ты вернулся! Ворота Монтеры для тебя открыты. — You're back! The gates of Montera are open to you.";
            var needle = new G3Image("G3_HUD_Icon_Misc_Compass_Needle.tga");
            var compass = page.Q("IDC_STATIC_COMPASS_2");
            if (compass != null)
            {
                needle.AddToClassList("g3-fill-parent");
                needle.style.rotate = new Rotate(30);
                compass.Add(needle);
            }
        }

        // ---------------------------------------------------------------- helpers

        static VisualElement Column(VisualElement board, int left, int top, int width)
        {
            var c = new VisualElement();
            c.style.position = Position.Absolute;
            c.style.left = left;
            c.style.top = top;
            c.style.width = width;
            board.Add(c);
            return c;
        }

        static void Caption(VisualElement parent, string text)
        {
            var l = new Label(text);
            l.style.color = new Color(0.6f, 0.75f, 0.9f);
            l.style.fontSize = 11;
            l.style.marginTop = 8;
            l.style.marginBottom = 3;
            parent.Add(l);
        }
    }
}
