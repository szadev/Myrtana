using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session;
using Myrtana.Ui.Skin;
using Myrtana.Unity.Ui.Components;
using Myrtana.Unity.Ui.Skin;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace Myrtana.Unity
{
    /// <summary>
    /// The inventory in the original look (<c>IDD_PAGE_INVENTORY_2</c>, Docs/formats-gui.md §6.5): the character view on
    /// the left, the selected stack's item panel in the middle, the 5×10 tile grid with five category tabs and the gold
    /// line on the right. Worn stacks are tinted blue; there is no paper doll. Double click, Enter or E uses, equips or
    /// takes off; the arrows or the mouse (hover) move the selection, Q/R switch the tab, 1…0 put the selected stack into a
    /// quick slot (G3 OnPlayerAssignStackKey). [owner, fix-85/92] Dragging a stack out of the UI and letting go over the game
    /// world drops it in front of the hero (<see cref="HeroItemDrop"/>; no key, no text); a stack asks for the amount in
    /// G3's amount dialog (IDD_VIEW_AMOUNT_2); quest items are refused with G3's deny sound. A click on the selected
    /// category tab cycles the sort mode (<see cref="G3SortToggle"/>).
    /// </summary>
    public sealed class G3InventoryScreen : GameScreen
    {
        public const string Layout = "IDD_PAGE_INVENTORY_2";
        static readonly TextId GoldText = new TextId("HUD_GoldAmount");
        /// <summary>How far in front of the hero a dropped item lands (m) [interp].</summary>
        const float DropDistance = 0.9f;

        readonly HudMessages _messages;
        readonly G3CharacterView _character;
        readonly G3InventoryTabs _tabs;
        readonly G3ItemGrid _grid;
        readonly G3ItemPanel _panel;
        readonly G3AmountView _amount;
        IReadOnlyList<ItemRow> _rows = new List<ItemRow>();
        string _selected;

        public G3InventoryScreen(VisualElement pagesRoot, HudMessages messages) : base(pagesRoot, G3Pages.Build(Layout))
        {
            _messages = messages;
            _character = new G3CharacterView(G3Pages.BuildInto(Root.Q("IDC_VIEW_INVENTORY_CHARACTER_2"), "IDD_VIEW_CHARACTER_2"));
            _panel = G3Pages.ItemPanel(Root, "IDC_STATIC_VIEW_ITEM_2");
            _tabs = new G3InventoryTabs(Root.Q("IDC_TABCTRL_INVENTORY_2"));
            _tabs.Changed += () =>
            {
                _selected = null;
                Render();
            };
            _tabs.SortChanged += () => { if (Session != null) Render(); }; // [owner] sort modes
            _grid = G3Pages.Grid(Root, "IDC_LISTCTRL_INVENTORY_2");
            _grid.SelectionChanged += Select;
            _grid.Activated += index => Activate();
            // drag a stack onto a quick-bar cell to put it there (gCHUDListCtrlInventory → OnPlayerAssignStackKey)
            _grid.DragEnabled = true;
            _grid.DropRole = "inventory";
            _grid.Dropped += OnDropped;
            // G3's amount dialog (287×173, as on the trade page) under the item panel (336,16–623,366)
            var amountSlot = new VisualElement { name = "myr-drop-amount", pickingMode = PickingMode.Position };
            amountSlot.style.position = Position.Absolute;
            amountSlot.style.left = 336;
            amountSlot.style.top = 372;
            amountSlot.style.width = 287;
            amountSlot.style.height = 173;
            Root.Add(amountSlot);
            _amount = new G3AmountView(amountSlot);
        }

        /// <summary>
        /// A dragged stack was let go: on a quick-bar cell it goes into that slot; over the game world (outside every
        /// UI panel: the page's views, the sidebar, the quick bar, any other document) it is dropped [owner, fix-85/92].
        /// </summary>
        void OnDropped(ItemDrop drop)
        {
            if (Session == null || drop.SourceIndex < 0 || drop.SourceIndex >= _rows.Count) return;
            string item = _rows[drop.SourceIndex].Item;
            if (drop.Target == null)
            {
                if (IsWorldPoint(drop.Screen)) RequestDrop(item);
                return;
            }
            if (drop.Target.DropRole != QuickBarRole || drop.TargetIndex < 0) return;
            HeroItemUse.AssignQuickSlot(Session, drop.TargetIndex, item);
        }

        /// <summary>
        /// Letting a dragged stack go at this screen point drops it: no drop grid, none of the page's views and no other
        /// UI (sidebar, quick bar, bars …) under it, only the game world.
        /// </summary>
        public bool IsWorldPoint(Vector2 screen) =>
            G3ItemGrid.TargetAt(screen).grid == null && !G3ItemGrid.OverViews(Root, screen) && !G3ItemGrid.OverUi(screen);

        /// <summary>
        /// Lets a stack go at a screen point as the end of a mouse drag does (dev runner): same target search and
        /// <see cref="OnDropped"/>. False when the item is not listed on the current tab.
        /// </summary>
        public bool ReleaseDrag(string item, Vector2 screen)
        {
            int index = IndexOf(item);
            if (index < 0) return false;
            var (target, cell) = G3ItemGrid.TargetAt(screen);
            OnDropped(new ItemDrop(_grid, index, target, cell, screen));
            return true;
        }

        /// <summary>The items listed on the current tab, in grid order (dev runner).</summary>
        public IReadOnlyList<string> Listed => _rows.Select(r => r.Item).ToList();

        /// <summary>The page's category tabs (dev runner: sort by clicking the selected tab).</summary>
        public G3InventoryTabs Tabs => _tabs;

        protected override void OnOpen()
        {
            _tabs.Reset();
            _selected = null;
            _amount.Cancel();
        }

        protected override void OnClose() => _amount.Cancel();

        /// <summary>G3 runs the amount dialog as a modal loop: Escape closes only the dialog.</summary>
        public override bool HandleEscape()
        {
            if (!_amount.IsOpen) return false;
            _amount.Cancel();
            return true;
        }

        public override void Render()
        {
            var text = Session.Localizer;
            _character.Render(CharacterSheet.Of(Session));
            G3Pages.Localize(_grid, Session);
            G3Pages.SetText(Root, "IDC_LABEL_INVENTORY_2", _tabs.Title(Session));
            G3Pages.SetText(Root, "IDC_LABEL_GOLD_2", text.Text(GoldText));
            G3Pages.SetText(Root, "IDC_VALUE_GOLD_2", G3Pages.Amount(ItemLists.Gold(Session, ActorId.Player)));
            _rows = _tabs.Filter(ItemLists.Of(Session, ActorId.Player, sort: G3SortToggle.Mode));
            _tabs.ShowSort();
            var tiles = G3Pages.Tiles(_rows);
            for (int i = 0; i < tiles.Count; i++) tiles[i].QuickSlot = Session.Items.QuickSlots.SlotOf(_rows[i].Item);
            _grid.SetItems(tiles);
            int index = IndexOf(_selected);
            _grid.Select(index, notify: false);
            ShowPanel(index);
        }

        public override G3.Player.PadHintPage PadHints => G3.Player.PadHintPage.Inventory;

        public override void Tick()
        {
            var kb = global::G3.Unity.G3Input.GameKeyboard;
            var keys = PageKeys.Now; // gamepad-1: the arrows / Enter or the pad's d-pad / A
            if (_amount.IsOpen)
            {
                if (keys.Left) _amount.Step(-1);
                if (keys.Right) _amount.Step(1);
                if (keys.Enter) _amount.Confirm();
                return;
            }
            if (kb != null && kb[G3SortToggle.Hotkey].wasPressedThisFrame) _tabs.CycleSort();
            if (keys.TabPrev(Key.Q)) _tabs.Move(-1);
            if (keys.TabNext(Key.R)) _tabs.Move(1);
            int s = _grid.SelectedIndex;
            if (keys.GridMove(s, _grid.Count, _grid.Columns) is int moved) _grid.Select(moved);
            if (keys.Enter || keys.Key(Key.E)) Activate();
            int slot = QuickSlotKeys.Pressed(kb);
            if (slot >= 0) AssignQuickSlot(slot);
            if (keys.Key(Key.Backspace)) ClearQuickSlot();
            // gamepad-1: X puts the stack into the pad's highlighted quick slot, Y clears it, LT/RT move the highlight
            if (keys.PadSecondary) AssignQuickSlot(QuickSlotInput.PadCursor.Slot);
            if (keys.PadTertiary) ClearQuickSlot();
            if (keys.PagePrev) QuickSlotInput.PadCursor.Move(-1);
            if (keys.PageNext) QuickSlotInput.PadCursor.Move(1);
        }

        /// <summary>Backspace (G3's second inventory key) clears the selected quick slot here instead of closing the page.</summary>
        public override bool ConsumesKey(Key key) => key == Key.Backspace;

        /// <summary>
        /// [owner, fix-85] Drops the stack of <paramref name="item"/> in front of the hero: refused (deny sound) for quest
        /// items and items held in the hand; one item at once, a stack after the amount dialog (or <paramref name="amount"/>
        /// directly, dev runner). False when refused. Reached by dragging a stack out over the world (<see cref="OnDropped"/>).
        /// </summary>
        public bool RequestDrop(string item, int? amount = null)
        {
            if (Session == null || item == null) return false;
            if (HeroItemDrop.Check(Session, item) != DropRefusal.None)
            {
                HeroItemUse.Deny();
                return false;
            }
            int max = HeroItemDrop.MaxAmount(Session, item);
            if (amount.HasValue || max <= 1) return DropNow(item, amount ?? 1);
            string title = Session.Content.Database.TryGet(item, out ItemDef def) ? ItemLists.Name(Session, def) : item;
            _amount.Ask(Session, title, max, n => DropNow(item, n));
            return true;
        }

        /// <summary>The amount dialog is open (dev runner); <see cref="ConfirmAmount"/> presses its OK.</summary>
        public bool AmountOpen => _amount.IsOpen;

        public void ConfirmAmount() => _amount.Confirm();

        /// <summary>Sets the open amount dialog's amount (dev runner).</summary>
        public void SetAmount(int amount) => _amount.Set(amount);

        bool DropNow(string item, int amount)
        {
            var hero = UnityEngine.Object.FindAnyObjectByType<global::G3.Unity.G3Player>();
            if (hero == null) return false;
            var t = hero.transform;
            var forward = Vector3.ProjectOnPlane(t.forward, Vector3.up);
            if (forward.sqrMagnitude < 1e-4f) forward = Vector3.forward;
            var at = t.position + forward.normalized * DropDistance + Vector3.up * 0.3f;
            if (HeroItemDrop.Drop(Session, item, amount, new[] { at.x, at.y, at.z }, t.eulerAngles.y, out _) == null) return false;
            if (Session.Items.Inventories.Of(ActorId.Player).Count(item) <= 0) _selected = null;
            Render();
            return true;
        }

        /// <summary>
        /// A number key puts the selected stack into that quick slot (G3 OnPlayerAssignStackKey: 1…9, 0; a slot holding
        /// another stack gets this one, the stack leaves the slot it was in; armour, rings, keys … are refused with
        /// eff_inv_deny_01). False when nothing is selected or the stack is refused.
        /// </summary>
        public bool AssignQuickSlot(int slot)
        {
            int index = _grid.SelectedIndex;
            return index >= 0 && index < _rows.Count && HeroItemUse.AssignQuickSlot(Session, slot, _rows[index].Item);
        }

        /// <summary>Switches to the tab that lists the item (dev runner); false when no tab does.</summary>
        public bool ShowItem(string item)
        {
            for (int i = 0; i < 8 && IndexOf(item) < 0; i++) _tabs.Move(1);
            return IndexOf(item) >= 0;
        }

        /// <summary>Selects the stack of an item on the current tab (dev runner, tests); false when it is not listed.</summary>
        public bool SelectItem(string item)
        {
            int index = IndexOf(item);
            if (index < 0) return false;
            _grid.Select(index);
            return true;
        }

        /// <summary>The item whose info panel is shown: the selected stack (hovering selects, fix-75).</summary>
        public string PanelItem => SelectedItem;

        /// <summary>The mouse moved onto a stack (dev runner, tests): the grid selects it as a real hover does.</summary>
        public bool Hover(string item)
        {
            int index = IndexOf(item);
            if (index < 0 || !_grid.HoverSelects) return false;
            _grid.Select(index);
            return true;
        }

        /// <summary>The item of the selected stack, or null.</summary>
        public string SelectedItem => _grid.SelectedIndex >= 0 && _grid.SelectedIndex < _rows.Count ? _rows[_grid.SelectedIndex].Item : null;

        /// <summary>The quick bar's drop role (<see cref="G3Hud"/>).</summary>
        public const string QuickBarRole = "quickslots";

        /// <summary>Backspace takes the selected stack off the quick bar (G3 inventory list).</summary>
        public void ClearQuickSlot()
        {
            int index = _grid.SelectedIndex;
            if (index < 0 || index >= _rows.Count) return;
            int slot = Session.Items.QuickSlots.SlotOf(_rows[index].Item);
            if (slot >= 0) Session.Items.QuickSlots.Clear(slot);
        }

        void Select(int index)
        {
            _selected = index >= 0 && index < _rows.Count ? _rows[index].Item : null;
            ShowPanel(index);
        }

        void ShowPanel(int index)
        {
            bool shown = index >= 0 && index < _rows.Count;
            _panel.Bind(shown ? G3Pages.Panel(ItemLists.Detail(Session, _rows[index])) : null);
        }

        void Activate()
        {
            int index = _grid.SelectedIndex;
            if (index >= 0 && index < _rows.Count) HeroItemUse.Activate(Session, _messages, _rows[index]);
        }

        int IndexOf(string item)
        {
            if (item == null) return -1;
            for (int i = 0; i < _rows.Count; i++)
                if (_rows[i].Item == item) return i;
            return -1;
        }
    }
}
