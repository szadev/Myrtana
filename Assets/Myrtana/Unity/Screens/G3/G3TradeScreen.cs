using System.Collections.Generic;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session;
using Myrtana.Simulation;
using Myrtana.Unity.Ui.Components;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace Myrtana.Unity
{
    /// <summary>
    /// Trading in the original look (<c>IDD_PAGE_TRADE_2</c>, Docs/formats-gui.md §6.6): the merchant's 5×8 stock, the
    /// item panel, the hero's 5×7 inventory with category tabs, and the transaction strip with both 6×3 offer pools
    /// (<see cref="TradeOffer"/>), their values, trash buttons and <i>Gold balance</i> / <i>Trade</i> / <i>Close</i>.
    /// Nothing changes hands until Trade. Double click or Enter moves a whole stack into or out of a pool, the right
    /// mouse button or Space one piece (repeating while held, <see cref="StackTransferRepeat"/>); the arrows
    /// move in the active grid, PageUp/PageDown switch the grid.
    /// </summary>
    public sealed class G3TradeScreen : TradePage
    {
        public const string Layout = "IDD_PAGE_TRADE_2";
        static readonly TextId GoldText = new TextId("HUD_GoldAmount"), ValueText = new TextId("HUD_GoldValue"),
                               BalanceText = new TextId("HUD_GoldBalance"), TradeText = new TextId("HUD_Trade"), CloseText = new TextId("HUD_Close"),
                               NotEnoughGold = new TextId("GO_NotEnoughGold"), NeedsFame = new TextId("GO_RequiredPoliticalReputation");

        /// <summary>One tile grid of the page and the stacks it shows.</summary>
        sealed class Grid
        {
            public G3ItemGrid View;
            public TradeOffer.Side Side;
            public bool Pool;
            public IReadOnlyList<ItemRow> Rows = new List<ItemRow>();
        }

        readonly HudMessages _messages;
        readonly GameScreens _screens;
        readonly G3ItemPanel _panel;
        readonly G3InventoryTabs _tabs;
        readonly Grid[] _grids;
        readonly G3Button _balance, _accept, _close;
        ActorId _merchant;
        TradeOffer _offer;
        Grid _active;
        readonly HeldStackTransfer<Grid> _held = new HeldStackTransfer<Grid>();

        public G3TradeScreen(VisualElement pagesRoot, HudMessages messages, GameScreens screens) : base(pagesRoot, G3Pages.Build(Layout))
        {
            _messages = messages;
            _screens = screens;
            _panel = G3Pages.ItemPanel(Root, "IDC_VIEW_TRADE_ITEM_2");
            _tabs = new G3InventoryTabs(Root.Q("IDC_TABCTRL_TRADE_PLAYER_CATEGORY_2"));
            _tabs.Changed += Render;
            _tabs.SortChanged += Render; // fix-92: a click on the selected tab cycles the sort
            _grids = new[]
            {
                NewGrid("IDC_LISTCTRL_TRADE_VENDOR_2", TradeOffer.Side.Vendor, false),
                NewGrid("IDC_LISTCTRL_TRADE_POOL_VENDOR_2", TradeOffer.Side.Vendor, true),
                NewGrid("IDC_LISTCTRL_TRADE_POOL_PLAYER_2", TradeOffer.Side.Hero, true),
                NewGrid("IDC_LISTCTRL_TRADE_PLAYER_INVENTORY_2", TradeOffer.Side.Hero, false),
            };
            _active = _grids[0];
            _balance = Button("IDC_BUTTON_TRADE_GOLD_EXCHANGE_2", Balance);
            _accept = Button("IDC_BUTTON_TRADE_ACCEPT_2", Accept);
            _close = Button("IDC_BUTTON_TRADE_CANCEL_2", () => _screens.CloseCurrent());
            Button("IDC_BUTTON_TRADE_POOL_VENDOR_CLEAR_2", () => ClearPool(TradeOffer.Side.Vendor));
            Button("IDC_BUTTON_TRADE_POOL_PLAYER_CLEAR_2", () => ClearPool(TradeOffer.Side.Hero));
        }

        public override void With(GameSession session, ActorId merchant)
        {
            _merchant = merchant;
            session.Items.Trade.Open(merchant, session.Clock.GameSeconds / 3600.0);
            _offer = new TradeOffer(session, merchant);
        }

        protected override void OnOpen()
        {
            _tabs.Reset();
            _active = _grids[0];
            foreach (var g in _grids) g.View.Select(-1, notify: false);
        }

        protected override void OnClose() => _offer?.Clear();

        public override void Render()
        {
            if (_offer == null) return;
            var text = Session.Localizer;
            G3Pages.SetText(Root, "IDC_LABEL_VENDOR_2", Session.Content.Database.TryGet(_merchant.Name, out NpcDef npc) ? text.Text(npc.DisplayName) : _merchant.Name);
            G3Pages.SetText(Root, "IDC_LABEL_TRADE_PLAYER_CATEGORY_2", _tabs.Title(Session));
            foreach (var name in new[] { "IDC_LABEL_TRADE_VENDOR_GOLD_2", "IDC_LABEL_TRADE_PLAYER_GOLD_2" }) G3Pages.SetText(Root, name, text.Text(GoldText));
            foreach (var name in new[] { "IDC_LABEL_TRADE_POOL_VENDOR_GOLD_2", "IDC_LABEL_TRADE_POOL_PLAYER_GOLD_2" }) G3Pages.SetText(Root, name, text.Text(ValueText));
            G3Pages.SetText(Root, "IDC_VALUE_TRADE_VENDOR_GOLD_2", G3Pages.Amount(ItemLists.Gold(Session, _merchant) - _offer.Gold(TradeOffer.Side.Vendor)));
            G3Pages.SetText(Root, "IDC_VALUE_TRADE_PLAYER_2", G3Pages.Amount(ItemLists.Gold(Session, ActorId.Player) - _offer.Gold(TradeOffer.Side.Hero)));
            G3Pages.SetText(Root, "IDC_VALUE_TRADE_POOL_VENDOR_GOLD_2", G3Pages.Amount(_offer.Value(TradeOffer.Side.Vendor)));
            G3Pages.SetText(Root, "IDC_VALUE_TRADE_POOL_PLAYER_GOLD_2", G3Pages.Amount(_offer.Value(TradeOffer.Side.Hero)));
            _balance.text = text.Text(BalanceText);
            _accept.text = text.Text(TradeText);
            _close.text = text.Text(CloseText);
            _accept.SetEnabled(_offer.CanAccept);

            var trade = Session.Items.Trade;
            _grids[0].Rows = _offer.Offerable(TradeOffer.Side.Vendor, Flatten(ItemLists.Of(Session, _merchant, d => trade.BuyPrice(_merchant, d.Id), sort: G3SortToggle.Mode)));
            _grids[1].Rows = _offer.Rows(TradeOffer.Side.Vendor);
            _grids[2].Rows = _offer.Rows(TradeOffer.Side.Hero);
            _tabs.ShowSort();
            _grids[3].Rows = _offer.Offerable(TradeOffer.Side.Hero, _tabs.Filter(ItemLists.Of(Session, ActorId.Player, d => trade.SellPrice(_merchant, d.Id), sort: G3SortToggle.Mode)));
            foreach (var g in _grids)
            {
                G3Pages.Localize(g.View, Session);
                int keep = g.View.SelectedIndex;
                g.View.SetItems(G3Pages.Tiles(g.Rows, countOnly: true));
                g.View.Select(keep, notify: false);
            }
            ShowPanel();
        }

        public override G3.Player.PadHintPage PadHints => G3.Player.PadHintPage.Trade;

        public override void Tick()
        {
            var keys = PageKeys.Now; // gamepad-1: keyboard or pad (LT/RT or LB/RB = the grids, A = Enter, X = Space)
            if (keys.PageNext || G3.Unity.G3Pad.Ui(G3.Player.UiCommand.TabNext))
                Activate(_grids[(System.Array.IndexOf(_grids, _active) + 1) % _grids.Length]);
            if (keys.PagePrev || G3.Unity.G3Pad.Ui(G3.Player.UiCommand.TabPrev))
                Activate(_grids[(System.Array.IndexOf(_grids, _active) + _grids.Length - 1) % _grids.Length]);
            var view = _active.View;
            int s = view.SelectedIndex;
            if (keys.GridMove(s, view.Count, view.Columns) is int moved) view.Select(moved);
            if (keys.Enter) Move(_active, view.SelectedIndex, whole: true);
            if (keys.Space) PressPiece(_active, view.SelectedIndex, space: true);
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
            if (_offer == null || index < 0 || index >= grid.Rows.Count) return;
            MoveAmount(grid, index, _held.Press(grid, grid.Rows[index].Item, space));
        }

        /// <summary>Gold balance button.</summary>
        public void Balance()
        {
            if (!_offer.BalanceGold()) _messages.Show(Session.Localizer.Text(NotEnoughGold), NoticeKind.Error);
            Render();
        }

        /// <summary>Trade button: hands over both pools through the trade rules.</summary>
        public void Accept()
        {
            var result = _offer.Accept();
            if (result == TradeResult.NotEnoughGold || result == TradeResult.MerchantLacksGold) _messages.Show(Session.Localizer.Text(NotEnoughGold), NoticeKind.Error);
            Render();
        }

        /// <summary>Moves a stack (or one piece) of a grid into its pool, or out of a pool back to its owner.</summary>
        public void Move(int grid, int index, bool whole) => Move(_grids[grid], index, whole);

        void Move(Grid grid, int index, bool whole)
        {
            if (_offer == null || index < 0 || index >= grid.Rows.Count) return;
            MoveAmount(grid, index, whole ? grid.Rows[index].Amount : 1);
        }

        void MoveAmount(Grid grid, int index, int amount)
        {
            if (_offer == null || index < 0 || index >= grid.Rows.Count) return;
            var row = grid.Rows[index];
            amount = System.Math.Min(amount, row.Amount);
            if (grid.Pool) _offer.Remove(grid.Side, row.Item, amount);
            else if (_offer.Add(grid.Side, row.Item, amount) == 0)
            {
                _held.Release(); // a refused piece stops the repeat (one message, not one per step)
                if (_offer.LastRefusal == TradeResult.NeedsFame)
                    _messages.Show(_offer.FameRefusal(Session.Localizer.Text(NeedsFame), row.Item), NoticeKind.Error);
            }
            Render();
        }

        void ClearPool(TradeOffer.Side side)
        {
            _offer?.Clear(side);
            Render();
        }

        Grid NewGrid(string slot, TradeOffer.Side side, bool pool)
        {
            var grid = new Grid { View = G3Pages.Grid(Root, slot), Side = side, Pool = pool };
            // drag a stack between a list and its pool (G3: drag, double click or Enter move it)
            grid.View.DragEnabled = true;
            grid.View.DropRole = "trade." + side + (pool ? ".pool" : ".list");
            grid.View.Dropped += drop =>
            {
                var target = System.Array.Find(_grids, g => g.View == drop.Target);
                if (target != null && target.Side == grid.Side && target.Pool != grid.Pool) Move(grid, drop.SourceIndex, whole: true);
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
            foreach (var g in _grids)
                if (g != grid) g.View.Select(-1, notify: false);
            if (grid.View.SelectedIndex < 0 && grid.Rows.Count > 0) grid.View.Select(0, notify: false);
            ShowPanel();
        }

        void ShowPanel()
        {
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
