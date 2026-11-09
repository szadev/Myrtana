using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session;
using Myrtana.Simulation;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace Myrtana.Unity
{
    /// <summary>
    /// Trading with a merchant (opened by a dialog's trade topic): the merchant's stock with buy prices on the left, the
    /// hero's items with sell prices on the right, both purses on top. ←/→ or a click chooses the side, ↑↓ the item,
    /// Enter (or a double click) buys or sells one, Shift+Enter the whole stack. Esc closes.
    /// </summary>
    public sealed class TradeScreen : TradePage
    {
        static readonly TextId Title = new TextId("HUD_Trade"), GoldText = new TextId("HUD_GoldAmount"), EmptyText = new TextId("HUD_NoEntries"),
                               NotEnoughGold = new TextId("GO_NotEnoughGold"), NeedsFame = new TextId("GO_RequiredPoliticalReputation"),
                               FameText = new TextId("HUD_Reputation");

        readonly Label _title, _hint, _merchantGold, _heroGold, _merchantName;
        readonly ItemListView _stock, _carried;
        readonly HudMessages _messages;
        ActorId _merchant;
        bool _heroSide;

        public TradeScreen(VisualElement hudRoot, HudMessages messages) : base(hudRoot, "TradeScreen")
        {
            _messages = messages;
            _title = Root.Q<Label>("screen-title");
            _hint = Root.Q<Label>("screen-hint");
            _merchantGold = Root.Q<Label>("merchant-gold");
            _heroGold = Root.Q<Label>("hero-gold");
            _merchantName = Root.Q<Label>("merchant-name");
            _stock = new ItemListView(Root.Q<ScrollView>("merchant-list")) { ShowPrices = true };
            _carried = new ItemListView(Root.Q<ScrollView>("hero-list")) { ShowPrices = true };
            _stock.Selected += _ => _heroSide = false;
            _carried.Selected += _ => _heroSide = true;
            _stock.Activated += row => Deal(row, false, 1);
            _carried.Activated += row => Deal(row, true, 1);
        }

        public override void With(GameSession session, ActorId merchant)
        {
            _merchant = merchant;
            session.Items.Trade.Open(merchant, session.Clock.GameSeconds / 3600.0);
        }

        public override void Render()
        {
            var text = Session.Localizer;
            var trade = Session.Items.Trade;
            _title.text = text.Text(Title);
            _merchantName.text = Session.Content.Database.TryGet(_merchant.Name, out NpcDef npc) ? text.Text(npc.DisplayName) : _merchant.Name;
            _merchantGold.text = $"{text.Text(GoldText)}: {ItemLists.Gold(Session, _merchant)}";
            _heroGold.text = $"{text.Text(GoldText)}: {ItemLists.Gold(Session, ActorId.Player)}";
            _hint.text = "←→    ↑↓    Enter / Shift+Enter    Esc";
            _stock.Show(ItemLists.Of(Session, _merchant, d => trade.BuyPrice(_merchant, d.Id)), text.Text(EmptyText));
            _carried.Show(ItemLists.Of(Session, ActorId.Player, d => trade.SellPrice(_merchant, d.Id)), text.Text(EmptyText));
        }

        public override void Tick()
        {
            var kb = global::G3.Unity.G3Input.GameKeyboard;
            if (kb == null) return;
            if (kb.leftArrowKey.wasPressedThisFrame) _heroSide = false;
            if (kb.rightArrowKey.wasPressedThisFrame) _heroSide = true;
            var list = _heroSide ? _carried : _stock;
            if (kb.downArrowKey.wasPressedThisFrame) list.Move(1);
            if (kb.upArrowKey.wasPressedThisFrame) list.Move(-1);
            if ((kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame) && list.Current != null)
                Deal(list.Current, _heroSide, kb.shiftKey.isPressed ? list.Current.Amount : 1);
        }

        void Deal(ItemRow row, bool heroSells, int amount)
        {
            var trade = Session.Items.Trade;
            var result = heroSells ? trade.Sell(_merchant, row.Item, amount) : trade.Buy(_merchant, row.Item, amount);
            if (result == TradeResult.NotEnoughGold || result == TradeResult.MerchantLacksGold) _messages.Show(Session.Localizer.Text(NotEnoughGold), NoticeKind.Error);
            if (result == TradeResult.NeedsFame)
                _messages.Show(Session.Localizer.Text(NeedsFame).Replace("$(name)", Session.Localizer.Text(FameText))
                                      .Replace("$(value)", trade.RequiredFame(row.Item).ToString()));
            Render();
        }
    }
}
