using System.Collections.Generic;
using Myrtana.Rules;
using Myrtana.Session;
using Myrtana.Ui.Skin;
using Myrtana.Unity.Ui.Components;
using Myrtana.Unity.Ui.Layout;
using Myrtana.Unity.Ui.Skin;
using UnityEngine;
using UnityEngine.UIElements;

namespace Myrtana.Unity
{
    /// <summary>
    /// The in-game HUD page of the original (<c>IDD_PAGE_MAIN_2</c>, Docs/formats-gui.md §6.3): compass, quick slots, page
    /// sidebar, game messages and the focus name, on its own document above the pages (so the sidebar takes clicks) and
    /// below the dialog; the game messages on a third document under the pages (fix-116: G3 draws its HUD page under the
    /// overlay pages, so a pickup message never covers the map or the inventory; <see cref="GuiLayers"/>). The HP/SP/MP and focus bars stay in <see cref="VitalsHud"/>; this HUD tells it what to show.
    /// Which parts show follows G3's page table (<see cref="HudVisibility"/>, <see cref="GameScreen.HudPage"/>): a dialog
    /// keeps only the focus bar, trade, loot and the crafting stations hide everything (fix-40: loot and stations showed the
    /// sidebar over the loot inventory), the frying pan keeps the play HUD,
    /// inventory/character/quests/map add the sidebar. The subtitle box belongs to the dialog; G3's log
    /// (gCHUDLog) is a debug view switched off in Ini/log.ini, so it is not built — G3 keeps no message history. Parts fade
    /// over 200 ms; LEFT CTRL+F1 and Scroll Lock hide parts (<see cref="HudToggle"/>). The sidebar only shows with a page
    /// open and sits next to it, so it lives on a second document scaled like the pages (<see cref="GuiPanelKind.Page"/>):
    /// at a GUI size above 100 % the HUD grows but the pages stay at the fit, and a HUD-scaled sidebar drifted off the page. Without the G3 GUI import <see cref="TryCreate"/> returns null and the plain HUD stays.
    /// </summary>
    public sealed class G3Hud : MonoBehaviour, ISessionView
    {
        public const string Layout = "IDD_PAGE_MAIN_2";
        /// <summary>Above the pages (105), below the dialog (110) and the game menu (120).</summary>
        public const int SortingOrder = GuiLayers.Hud;
        /// <summary>Cell height of the focus-name font (gCGUIEntityRenderer: LOGFONT height +24, weight 800).</summary>
        const int FocusNameCellHeight = 24;
        const float FocusDescribeSeconds = 0.25f;
        const string NeedleImage = "G3_HUD_Icon_Misc_Compass_Needle.tga";

        /// <summary>Sidebar tabs in G3's order with their icons; Spells has no page yet.</summary>
        static readonly string[] SidebarIcons = { "Inventory", "Character", "Book", "Map", "Log" };
        const int SpellsTab = 2;

        VisualElement _root, _sideRoot, _compass, _quickBar, _sidebar;
        GameObject _sideDoc, _messagesDoc;
        G3Image _needle;
        G3ItemGrid _quickGrid;
        G3TabBar _tabs;
        Label _focusName;
        G3MessageArea _messages;
        G3Image _crosshair;
        float _crosshairAlpha;
        int _crosshairSize;
        readonly GameScreen[] _sheets = new GameScreen[5];
        readonly List<ItemTileModel> _quickTiles = new List<ItemTileModel>();

        GameSession _session;
        GameScreens _screens;
        VitalsHud _vitals;
        HudFocusTracker _focus;
        /// <summary>The focus the name follows (runner probes, fix-79).</summary>
        public HudFocusTracker FocusTracker => _focus;
        HudParts _shown = (HudParts)(-1);
        bool _quickDirty = true;
        float _nextDescribe;
        readonly HudToggle _toggle = new HudToggle();
        G3Fade _compassFade, _quickFade, _sidebarFade;

        /// <summary>The hero's combat: the aiming crosshair follows its aim (null = no crosshair).</summary>
        public PlayerCombat Aim { get; set; }

        /// <summary>The message view of this HUD (pass it to <see cref="HudMessages"/>).</summary>
        public IHudMessageView Messages => _messages;

        /// <summary>The G3 HUD on a new document, or null when the G3 GUI is not imported.</summary>
        public static G3Hud TryCreate(Transform parent)
        {
            var skin = G3Skin.Load();
            var layout = skin?.Catalog.Screen(Layout);
            if (layout == null) return null;
            var doc = G3PanelSettings.CreateDocument("G3 HUD", GuiPanelKind.Hud, parent, SortingOrder);
            // a sibling, not a child: a UIDocument under another UIDocument shares the parent's panel (Unity asserts on its own settings)
            var side = G3PanelSettings.CreateDocument("G3 HUD sidebar", GuiPanelKind.Page, parent, SortingOrder);
            // fix-116: G3 draws the HUD page under the overlay pages, so the game messages go under an open page
            var messages = G3PanelSettings.CreateDocument("G3 HUD messages", GuiPanelKind.Hud, parent, GuiLayers.HudMessages);
            var hud = doc.gameObject.AddComponent<G3Hud>();
            hud._sideDoc = side.gameObject;
            hud._messagesDoc = messages.gameObject;
            hud.Build(doc.rootVisualElement, side.rootVisualElement, messages.rootVisualElement, layout);
            return hud;
        }

        void Build(VisualElement root, VisualElement sideRoot, VisualElement messagesRoot, WindowData layout)
        {
            _root = root;
            _sideRoot = sideRoot;
            _compass = Host(layout, "IDC_STATIC_COMPASS_2");
            _compass.Add(Fill(new G3Image(layout.Find("IDC_STATIC_COMPASS_2").Image)));
            _needle = Fill(new G3Image(NeedleImage));
            _compass.Add(_needle);

            _quickBar = Host(layout, "IDC_VIEW_BAR_QUICKSLOTS_2");
            var quickView = G3Pages.BuildInto(_quickBar, "IDD_VIEW_BAR_QUICKSLOTS_2");
            var quickList = quickView?.Q("IDC_LISTCTRL_QUICKSLOTS_2");
            if (quickList != null)
            {
                _quickGrid = new G3ItemGrid(QuickSlots.Count * G3ItemGrid.Pitch, G3ItemGrid.Pitch, scrollable: false);
                _quickGrid.HoverSelects = false; // the bar has no item panel
                quickList.Add(_quickGrid);
                // with a page open: drag a cell onto another to move it, anywhere else to clear it (gCHUDListCtrlQuickSlots)
                _quickGrid.DropRole = G3InventoryScreen.QuickBarRole;
                _quickGrid.DragEnabled = true;
                _quickGrid.Dropped += OnQuickDrop;
            }

            _sidebar = Host(layout, "IDC_VIEW_BAR_PAGES_2", _sideRoot);
            BuildSidebar(G3Pages.BuildInto(_sidebar, "IDD_VIEW_BAR_PAGES_2"));

            var messages = Host(layout, "IDC_STATIC_GAME_MESSAGES_2", messagesRoot);
            _messages = new G3MessageArea(messages, layout.Find("IDC_STATIC_GAME_MESSAGES_2").Height);

            _focusName = new Label { pickingMode = PickingMode.Ignore, name = "focus-name" };
            _focusName.style.position = Position.Absolute;
            _focusName.style.translate = new Translate(Length.Percent(-50), Length.Percent(-100));
            _focusName.style.whiteSpace = WhiteSpace.NoWrap;
            _focusName.style.unityTextOutlineWidth = 1;
            _focusName.style.unityTextOutlineColor = Color.black;
            G3Text.CellFont(_focusName, FocusNameCellHeight, bold: true);
            _root.Add(_focusName);

            // gCHUDCrossHair: centred on the screen, 8…32 px, alpha-blended in/out (§6.3)
            _crosshair = new G3Image(CrosshairRules.Image) { name = "crosshair" };
            _crosshair.style.position = Position.Absolute;
            _crosshair.style.left = Length.Percent(50);
            _crosshair.style.top = Length.Percent(50);
            _crosshair.style.translate = new Translate(Length.Percent(-50), Length.Percent(-50));
            _crosshair.style.opacity = 0;
            _crosshair.style.display = DisplayStyle.None;
            _root.Add(_crosshair);
            // gCHUDPageMain2::OnPageChanged animates its parts over 200 ms: the compass blends, the rest grows from its centre
            _compassFade = new G3Fade(_compass, G3FadeKind.Blend);
            _quickFade = new G3Fade(_quickBar, G3FadeKind.Center);
            _sidebarFade = new G3Fade(_sidebar, G3FadeKind.Center);
            Apply(HudParts.None);
        }

        void BuildSidebar(VisualElement view)
        {
            if (view == null) return;
            var tabSlot = view.Q("IDC_TABCTRL_PAGES_2");
            if (tabSlot?.userData is WindowData tabData)
            {
                _tabs = G3TabBar.FromLayout(tabData);
                foreach (var icon in SidebarIcons) _tabs.AddTab($"G3_HUD_Icon_Page_{icon}.tga");
                _tabs.Tabs[SpellsTab].SetEnabled(false);
                _tabs.SelectionChanged += OpenSheet;
                tabSlot.pickingMode = PickingMode.Position;
                tabSlot.Add(_tabs);
            }
            var closeSlot = view.Q("IDC_BUTTON_CLOSE_2");
            if (closeSlot?.userData is WindowData closeData)
            {
                var close = new G3Button(null, () => _screens?.CloseCurrent()).WithIcon(closeData.Image);
                close.AddToClassList("g3-fill-parent");
                closeSlot.pickingMode = PickingMode.Position;
                closeSlot.Add(close);
            }
        }

        /// <param name="sheets">Pages of the sidebar tabs in G3's order: inventory, character, spells, map, quests (null = none).</param>
        public void Init(GameScreens screens, VitalsHud vitals, HudFocusTracker focus, params GameScreen[] sheets)
        {
            _screens = screens;
            _vitals = vitals;
            _focus = focus;
            for (int i = 0; i < _sheets.Length && i < sheets.Length; i++) _sheets[i] = sheets[i];
            if (_vitals != null) _vitals.FocusSource = () => _focus.Actor;
        }

        public void Bind(GameSession session)
        {
            Watch(false);
            _session = session;
            Watch(true);
            _quickDirty = true;
            _messages.Clear();
        }

        void OnDestroy()
        {
            Watch(false);
            if (_sideDoc != null) Destroy(_sideDoc);
            if (_messagesDoc != null) Destroy(_messagesDoc);
        }

        /// <summary>The quick bar redraws when a slot or the hero's inventory changes (counts, emptied slots).</summary>
        void Watch(bool on)
        {
            if (_session == null) return;
            var slots = _session.Items.QuickSlots;
            var inventory = _session.Items.Inventories.Of(Core.ActorId.Player);
            if (on)
            {
                slots.Changed += MarkQuickDirty;
                inventory.Changed += MarkQuickDirty;
            }
            else
            {
                slots.Changed -= MarkQuickDirty;
                inventory.Changed -= MarkQuickDirty;
            }
        }

        void MarkQuickDirty() => _quickDirty = true;

        void LateUpdate()
        {
            _messages.Update();
            if (_session == null) return;
            ReadToggleKeys();
            UpdateCrosshair();
            var page = CurrentPage();
            var parts = HudVisibility.Of(page) & _toggle.Mask;
            Apply(parts);
            if ((parts & HudParts.Sidebar) != 0) _tabs?.Select(System.Array.IndexOf(_sheets, _screens.Current), notify: false);
            if ((parts & HudParts.Compass) != 0) TurnNeedle();
            if ((parts & HudParts.QuickSlots) != 0 && (_quickDirty || QuickMarks() != _quickMarks)) RenderQuickSlots();
            bool picking = page == HudPage.Sheet;
            if (picking != _quickPicking) SetQuickPicking(picking);
            UpdatePadQuickCursor((page == HudPage.None || page == HudPage.Sheet) && (parts & HudParts.QuickSlots) != 0);
            UpdateFocusName((parts & HudParts.FocusName) != 0);
        }

        /// <summary>gamepad-1: the pad's quick-bar highlight (<see cref="QuickSlotInput.PadCursor"/>) while the pad is in use.</summary>
        void UpdatePadQuickCursor(bool playing)
        {
            if (_quickGrid == null) return;
            int want = playing && global::G3.Unity.G3Pad.PadIsLast ? QuickSlotInput.PadCursor.Slot : -1;
            if (_quickGrid.SelectedIndex != want) _quickGrid.Select(want, notify: false);
        }

        HudPage CurrentPage()
        {
            var open = _screens != null ? _screens.Current : null;
            if (_session.Dialog.IsActive && !(open != null && _screens.IsDialogPage)) return HudPage.Dialog; // fix-140: trade over a held talk
            if (open == null) return HudPage.None;
            return open.HudPage;
        }

        void Apply(HudParts parts)
        {
            if (parts == _shown) return;
            _shown = parts;
            _compassFade.Show((parts & HudParts.Compass) != 0);
            _quickFade.Show((parts & HudParts.QuickSlots) != 0);
            _sidebarFade.Show((parts & HudParts.Sidebar) != 0);
            if ((parts & HudParts.FocusName) == 0) Show(_focusName, false);
            if (_vitals == null) return;
            _vitals.VitalsVisible = (parts & HudParts.Vitals) != 0;
            _vitals.FocusVisible = (parts & HudParts.FocusBar) != 0;
        }

        /// <summary>LEFT CTRL+F1 hides the HUD layer; Scroll Lock (ToggleHUD) steps through the quick-slot/compass/bars cycle.</summary>
        void ReadToggleKeys()
        {
            var kb = global::G3.Unity.G3Input.GameKeyboard;
            if (kb == null) return;
            if (kb.f1Key.wasPressedThisFrame && kb.leftCtrlKey.isPressed) _toggle.ToggleLayer();
            if (G3.Unity.G3Input.WasPressed(G3.Player.SessionKey.ToggleHUD)) _toggle.NextStep(); // G3 default Scroll Lock
        }

        static void Show(VisualElement e, bool on)
        {
            if (e != null) e.style.display = on ? DisplayStyle.Flex : DisplayStyle.None;
        }

        void OpenSheet(int tab)
        {
            var sheet = tab >= 0 && tab < _sheets.Length ? _sheets[tab] : null;
            if (sheet != null && sheet != _screens.Current) _screens.Open(sheet);
        }

        /// <summary>Shown while the hero aims a bow or crossbow; the size follows the distance to the aimed point.</summary>
        void UpdateCrosshair()
        {
            bool shown = Aim != null && Aim.CrosshairShown && CurrentPage() == HudPage.None;
            _crosshairAlpha = CrosshairRules.Fade(_crosshairAlpha, shown, Time.unscaledDeltaTime);
            bool visible = _crosshairAlpha > 0f;
            _crosshair.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
            if (!visible) return;
            _crosshair.style.opacity = _crosshairAlpha;
            if (!shown) return; // fading out keeps the last size
            int size = CrosshairRules.SizePixels(Aim.AimDistance());
            if (size == _crosshairSize) return;
            _crosshairSize = size;
            _crosshair.style.width = size;
            _crosshair.style.height = size;
        }

        void TurnNeedle()
        {
            var cam = Camera.main;
            if (cam == null) return;
            var forward = cam.transform.forward;
            _needle.style.rotate = new Rotate(CompassRules.NeedleDegrees(forward.x, forward.z));
        }

        /// <summary>
        /// The blue cells of the bar (<see cref="EquippedMarker.IsQuickMarked"/>: the active hand item, else every worn
        /// stack): checked every frame, so equipping, drawing a bow (its arrows), taking or putting away the torch, drawing
        /// or holstering a spell and loading a save repaint at once.
        /// </summary>
        int QuickMarks()
        {
            _isMarked ??= item => EquippedMarker.IsQuickMarked(item, _session.Items.Equipment, _session.Items.Hands);
            return EquippedMarker.QuickBarMask(_session.Items.QuickSlots, _isMarked);
        }

        System.Func<string, bool> _isMarked;
        int _quickMarks;

        void RenderQuickSlots()
        {
            _quickDirty = false;
            _quickMarks = QuickMarks();
            if (_quickGrid == null) return;
            var slots = _session.Items.QuickSlots;
            var rows = new Dictionary<string, ItemRow>();
            foreach (var group in ItemLists.Of(_session, Core.ActorId.Player))
                foreach (var row in group.Rows)
                    if (!rows.ContainsKey(row.Item)) rows.Add(row.Item, row);
            _quickTiles.Clear();
            for (int i = 0; i < QuickSlots.Count; i++)
            {
                string item = slots.ItemAt(i);
                var row = item == null ? null : rows.TryGetValue(item, out var listed) ? listed : ItemLists.HeroRow(_session, item); // spells: no inventory page
                var tile = row != null ? G3Pages.Tile(row) : null;
                if (tile != null) tile.QuickSlot = i;
                _quickTiles.Add(tile);
            }
            G3Pages.Localize(_quickGrid, _session);
            _quickGrid.SetItems(_quickTiles);
            SetQuickPicking(_quickPicking);
        }

        bool _quickPicking;

        /// <summary>The bar takes the mouse only while a page shows the cursor (drag and drop); in play it is display only.</summary>
        void SetQuickPicking(bool on)
        {
            _quickPicking = on;
            if (_quickGrid == null) return;
            foreach (var tile in _quickGrid.Tiles) tile.pickingMode = on ? PickingMode.Position : PickingMode.Ignore;
        }

        void OnQuickDrop(ItemDrop drop)
        {
            if (_session == null) return;
            var slots = _session.Items.QuickSlots;
            string item = slots.ItemAt(drop.SourceIndex);
            if (item == null) return;
            if (drop.Target == _quickGrid && drop.TargetIndex >= 0) slots.Assign(drop.TargetIndex, item);
            else slots.Clear(drop.SourceIndex);
        }

        void UpdateFocusName(bool allowed)
        {
            bool changed = _focus != null && _focus.Refresh(_session);
            var cam = Camera.main;
            if (!allowed || _focus == null || !_focus.HasFocus || !_focus.TryGetAnchor(cam, out var anchor)) // fix-79: G3's view lift
            {
                Show(_focusName, false);
                return;
            }
            if (cam == null || _focusName.panel == null || cam.WorldToViewportPoint(anchor).z <= 0f)
            {
                Show(_focusName, false);
                return;
            }
            if (changed || Time.unscaledTime >= _nextDescribe)
            {
                _nextDescribe = Time.unscaledTime + FocusDescribeSeconds;
                var target = _focus.Describe(_session);
                _focusName.text = target.Name;
                G3Text.Color(_focusName, FocusNameColors.Of(Attitude(target.Relation)));
            }
            var p = RuntimePanelUtils.CameraTransformWorldToPanel(_focusName.panel, anchor, cam);
            _focusName.style.left = p.x;
            _focusName.style.top = p.y;
            Show(_focusName, true);
        }

        static FocusAttitude Attitude(FocusRelation r)
        {
            switch (r)
            {
                case FocusRelation.Friendly: return FocusAttitude.Friendly;
                case FocusRelation.Party: return FocusAttitude.Party;
                case FocusRelation.Dead: return FocusAttitude.Dead;
                case FocusRelation.Angry: return FocusAttitude.Angry;
                case FocusRelation.Hostile:
                case FocusRelation.Locked: return FocusAttitude.Hostile;   // G3 renderer attitude 4: shut lock
                case FocusRelation.Panic:
                case FocusRelation.Unlooted: return FocusAttitude.Panic;   // G3 renderer attitude 8: chest not looted (fix-230)
                case FocusRelation.Item: return FocusAttitude.Item;
                default: return FocusAttitude.Neutral;
            }
        }

        VisualElement Host(WindowData layout, string name, VisualElement into = null)
        {
            var host = new VisualElement { name = name, pickingMode = PickingMode.Ignore };
            var w = layout.Find(name);
            if (w != null) G3LayoutBuilder.Place(host, w, layout.Width, layout.Height);
            (into ?? _root).Add(host);
            return host;
        }

        static T Fill<T>(T e) where T : VisualElement
        {
            e.AddToClassList("g3-fill-parent");
            return e;
        }
    }
}
