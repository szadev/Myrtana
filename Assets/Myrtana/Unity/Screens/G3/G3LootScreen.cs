using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session;
using Myrtana.Unity.Ui.Components;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace Myrtana.Unity
{
    /// <summary>
    /// A chest's contents or a downed or dead NPC's loot (<see cref="NpcLoot"/>, G3 PlayerOpenLoot page 7) in the original look (<c>IDD_PAGE_LOOT_2</c>, Docs/formats-gui.md §6.7): the container's 5×10
    /// pool with its gold, <i>Take all</i> and <i>Close</i>, the item panel, and the hero's inventory with category tabs.
    /// Double click or Enter moves a whole stack to the other side, the right mouse button or Space one piece (repeating
    /// while held, <see cref="StackTransferRepeat"/>), U takes everything and closes (CP 1.75 "Take all"); the arrows
    /// move in the active grid, PageUp/PageDown switch grids. Items move at once (through <see cref="InteractiveObjects"/>,
    /// which reports each stack taken for the theft rules, or <see cref="NpcLoot"/>: looting a body is no crime).
    /// </summary>
    public sealed class G3LootScreen : GameScreen
    {
        public const string Layout = "IDD_PAGE_LOOT_2";
        static readonly TextId TakeAllText = new TextId("HUD_TakeAll"), CloseText = new TextId("HUD_Close"),
                               GoldText = new TextId("HUD_GoldAmount");

        sealed class Grid
        {
            public G3ItemGrid View;
            public bool Pool;
            public IReadOnlyList<ItemRow> Rows = new List<ItemRow>();
        }

        readonly GameScreens _screens;
        readonly G3ItemPanel _panel;
        readonly G3InventoryTabs _tabs;
        readonly Grid _pool, _hero;
        readonly G3Button _takeAll, _close;
        string _container;
        ActorId _body;
        Grid _active;
        readonly HeldStackTransfer<Grid> _held = new HeldStackTransfer<Grid>();

        public G3LootScreen(VisualElement pagesRoot, GameScreens screens) : base(pagesRoot, G3Pages.Build(Layout))
        {
            _screens = screens;
            _panel = G3Pages.ItemPanel(Root, "IDC_VIEW_LOOT_ITEM_2");
            _tabs = new G3InventoryTabs(Root.Q("IDC_TABCTRL_LOOT_INVENTORY_2"));
            _tabs.Changed += Render;
            _tabs.SortChanged += Render; // fix-92: a click on the selected tab cycles the sort
            _pool = NewGrid("IDC_LISTCTRL_LOOT_2", pool: true);
            _hero = NewGrid("IDC_LISTCTRL_LOOT_INVENTORY_2", pool: false);
            _active = _pool;
            _takeAll = Button("IDC_BUTTON_LOOT_TAKE_ALL_2", TakeAllAndClose);
            _close = Button("IDC_BUTTON_LOOT_CLOSE_2", () => _screens.CloseCurrent());
        }

        /// <summary>G3 page 7: no HUD and no page sidebar (the sidebar would cover the inventory's right edge, x 946–1000).</summary>
        public override Myrtana.Ui.Skin.HudPage HudPage => Myrtana.Ui.Skin.HudPage.Exclusive;

        /// <summary>The chest whose contents the page shows (an opened <see cref="InteractiveObjectDef"/>).</summary>
        public string Container => _container;

        public void With(string container)
        {
            _container = container;
            _body = default;
        }

        /// <summary>The downed or dead NPC whose loot the page shows (empty when a chest is shown).</summary>
        public ActorId Body => _body;

        /// <summary>Shows a body's loot (G3 PS_GonnaLoot → PlayerOpenLoot).</summary>
        public void WithBody(ActorId npc)
        {
            _body = npc;
            _container = null;
        }

        /// <summary>
        /// The page is open on a chest or a body. A closed page has no session (GameScreen.Close): a late Render, tab or
        /// selection event then does nothing (editor-runner-25: NullReferenceException in Render after a close).
        /// </summary>
        bool Shows => IsOpen && Session != null && (_container != null || (!_body.IsEmpty && Session.Items.NpcLoot != null));

        protected override void OnOpen()
        {
            _tabs.Reset();
            _active = _pool;
            _pool.View.Select(0, notify: false);
            _hero.View.Select(-1, notify: false);
            // G3 ZS_Unconscious_Loop: a downed NPC stays down while the hero has its loot page open.
            if (!_body.IsEmpty && Session != null) Session.Combat.Vitals.HoldDown = id => IsOpen && id == _body;
        }

        protected override void OnClose()
        {
            if (!_body.IsEmpty && Session != null) Session.Combat.Vitals.HoldDown = _ => false;
        }

        public override void Render()
        {
            if (!Shows) return;
            var text = Session.Localizer;
            bool body = _container == null;
            var owner = body ? _body : InteractiveObjects.ContainerOf(_container);
            var loot = body ? Session.Items.NpcLoot.Pool(_body) : null;
            G3Pages.SetText(Root, "IDC_LABEL_LOOT_2", HudFocus.LootTitle(Session, _body, _container));
            G3Pages.SetText(Root, "IDC_LABEL_LOOT_INVENTORY_2", _tabs.Title(Session));
            G3Pages.SetText(Root, "IDC_LABEL_LOOT_GOLD_2", text.Text(GoldText));
            G3Pages.SetText(Root, "IDC_LABEL_LOOT_PLAYER_GOLD_2", text.Text(GoldText));
            G3Pages.SetText(Root, "IDC_VALUE_LOOT_GOLD_2", G3Pages.Amount(body ? LootGold(loot) : ItemLists.Gold(Session, owner)));
            G3Pages.SetText(Root, "IDC_VALUE_LOOT_PLAYER_GOLD_2", G3Pages.Amount(ItemLists.Gold(Session, ActorId.Player)));
            _takeAll.text = text.Text(TakeAllText);
            _close.text = text.Text(CloseText);

            _pool.Rows = Flatten(body ? ItemLists.Of(Session, loot.Select(l => new KeyValuePair<string, int>(l.Item, l.Amount)), hero: false, includeGold: true,
                                                     sort: G3SortToggle.Mode)
                                      : ItemLists.Of(Session, owner, includeGold: true, sort: G3SortToggle.Mode));
            _tabs.ShowSort();
            _hero.Rows = _tabs.Filter(ItemLists.Of(Session, ActorId.Player, sort: G3SortToggle.Mode));
            _takeAll.SetEnabled(Myrtana.Ui.Skin.LootPageRules.TakeAllEnabled(_pool.Rows.Count));
            foreach (var g in new[] { _pool, _hero })
            {
                G3Pages.Localize(g.View, Session);
                int keep = g.View.SelectedIndex;
                g.View.SetItems(G3Pages.Tiles(g.Rows, countOnly: true));
                g.View.Select(keep < g.Rows.Count ? keep : g.Rows.Count - 1, notify: false);
            }
            ShowPanel();
        }

        public override G3.Player.PadHintPage PadHints => G3.Player.PadHintPage.Loot;

        public override void Tick()
        {
            var keys = PageKeys.Now; // gamepad-1: keyboard or pad (LT/RT or LB/RB = the other grid, A = Enter, X = Space, Y = take all)
            if (keys.PageNext || keys.PagePrev || G3.Unity.G3Pad.Ui(G3.Player.UiCommand.TabPrev) || G3.Unity.G3Pad.Ui(G3.Player.UiCommand.TabNext))
                Activate(_active == _pool ? _hero : _pool);
            var view = _active.View;
            int s = view.SelectedIndex;
            if (keys.GridMove(s, view.Count, view.Columns) is int moved) view.Select(moved);
            if (keys.Enter) Move(_active, view.SelectedIndex, whole: true);
            if (keys.Space) PressPiece(_active, view.SelectedIndex, space: true);
            // CP 1.75 session key "Take all" (U) on the loot page: take everything and close (OnHUDHandleInput, page 7)
            if (G3.Unity.G3Input.WasPressed(G3.Player.SessionKey.TakeAll) || keys.PadTertiary)
            {
                TakeAllAndClose();
                return;
            }
            int due = _held.Tick(out var grid, out var item);
            if (due > 0)
            {
                int index = HeldStackTransfer<Grid>.IndexOf(grid.Rows, item);
                if (index < 0) _held.Release();
                else MoveAmount(grid, index, due);
            }
        }

        /// <summary>Right mouse button or Space: one piece now, more while held (G3 hold-to-repeat).</summary>
        void PressPiece(Grid grid, int index, bool space)
        {
            if (!Shows || index < 0 || index >= grid.Rows.Count) return;
            MoveAmount(grid, index, _held.Press(grid, grid.Rows[index].Item, space));
        }

        /// <summary>
        /// The Take all button and the CP key U (owner, fix-40: the button behaves like U): every stack goes to the hero,
        /// then the page closes. G3's Take all (100a0810) moves every visible pool stack without a script check and G3 has
        /// no carry limit, so nothing is refused; a stack the session still refuses stays in the container and the page
        /// closes all the same, as with U (LootPageRules.TakeAllCloses).
        /// </summary>
        public void TakeAllAndClose()
        {
            if (!Shows) return;
            TakeAll();
            if (Myrtana.Ui.Skin.LootPageRules.TakeAllCloses) _screens.CloseCurrent();
        }

        /// <summary>Every stack of the container goes to the hero (the page stays open).</summary>
        public void TakeAll()
        {
            if (!Shows) return;
            if (_container != null) Session.Interaction.Objects.TakeAll(_container);
            else Session.Items.NpcLoot.TakeAll(_body);
            Render();
        }

        int LootGold(IReadOnlyList<LootStack> loot) => loot.Where(l => l.Item == Session.Items.GoldItem).Sum(l => l.Amount);

        /// <summary>Moves a stack (or one piece) from the pool to the hero (grid 0) or from the hero into the container (grid 1).</summary>
        public void Move(int grid, int index, bool whole) => Move(grid == 0 ? _pool : _hero, index, whole);

        void Move(Grid grid, int index, bool whole)
        {
            if (!Shows || index < 0 || index >= grid.Rows.Count) return;
            MoveAmount(grid, index, whole ? grid.Rows[index].Amount : 1);
        }

        void MoveAmount(Grid grid, int index, int amount)
        {
            if (!Shows || index < 0 || index >= grid.Rows.Count) return;
            var row = grid.Rows[index];
            amount = System.Math.Min(amount, row.Amount);
            if (_container == null)
            {
                var loot = Session.Items.NpcLoot;
                if (grid.Pool) loot.Take(_body, row.Item, amount);
                else if (!row.Worn) loot.Put(_body, row.Item, amount);
                Render();
                return;
            }
            var objects = Session.Interaction.Objects;
            if (grid.Pool) objects.Take(_container, row.Item, amount);
            else if (!row.Worn) objects.Put(_container, row.Item, amount);
            Render();
        }

        Grid NewGrid(string slot, bool pool)
        {
            var grid = new Grid { View = G3Pages.Grid(Root, slot), Pool = pool };
            // drag a stack between the container and the hero (G3: drag, double click or Enter move it)
            grid.View.DragEnabled = true;
            grid.View.DropRole = pool ? "loot.pool" : "loot.hero";
            grid.View.Dropped += drop =>
            {
                if (drop.Target != null && drop.Target != grid.View && (drop.Target == _pool?.View || drop.Target == _hero?.View))
                    Move(grid, drop.SourceIndex, whole: true);
            };
            grid.View.SelectionChanged += index =>
            {
                if (index >= 0) Activate(grid);
                ShowPanel();
            };
            grid.View.Activated += index => Move(grid, index, whole: true);
            grid.View.RegisterCallback<PointerDownEvent>(e =>
            {
                if (e.button != 1) return;
                for (var t = e.target as VisualElement; t != null; t = t.parent)
                    if (t is G3ItemTile tile)
                    {
                        PressPiece(grid, tile.Index, space: false);
                        break;
                    }
            });
            return grid;
        }

        void Activate(Grid grid)
        {
            if (_active == grid) return;
            _active = grid;
            (grid == _pool ? _hero : _pool).View.Select(-1, notify: false);
            if (grid.View.SelectedIndex < 0 && grid.Rows.Count > 0) grid.View.Select(0, notify: false);
            ShowPanel();
        }

        void ShowPanel()
        {
            if (Session == null) { _panel.Bind(null); return; }
            int index = _active.View.SelectedIndex;
            _panel.Bind(index >= 0 && index < _active.Rows.Count ? G3Pages.Panel(ItemLists.Detail(Session, _active.Rows[index])) : null);
        }

        G3Button Button(string name, System.Action clicked)
        {
            var button = Root.Q<G3Button>(name);
            if (button != null) button.clicked += clicked;
            return button ?? new G3Button();
        }

        static IReadOnlyList<ItemRow> Flatten(IReadOnlyList<ItemGroup> groups)
        {
            var rows = new List<ItemRow>();
            foreach (var g in groups) rows.AddRange(g.Rows);
            return rows;
        }
    }
}
