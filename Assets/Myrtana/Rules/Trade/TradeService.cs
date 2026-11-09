using System;
using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;

namespace Myrtana.Rules
{
    /// <summary>NothingOffered: one of the two offers is worth nothing (G3 keeps the Trade button disabled).</summary>
    public enum TradeResult { Done, NotForSale, NotEnoughGold, MerchantLacksGold, NotCarried, NeedsFame, NothingOffered }

    /// <summary>
    /// Buying and selling with a merchant (trading.json, Docs/gameplay-logic.md §7.3): unit prices from
    /// <see cref="PriceCalculator"/>, the stock generated on the first conversation and topped up after
    /// <see cref="TradeRules.RefreshHours"/>, body armour gated by political fame. Goods and gold move through
    /// <see cref="Inventories.Transfer"/>.
    /// <para>
    /// Like G3's stack type <i>Merchandise</i>, a merchant sells only what his trade sets generated and what he got in
    /// trade; items he received otherwise (quest deliveries, dialog gifts) stay his own. The amount per item is kept here
    /// (<see cref="ForSale"/>) and never exceeds what he actually carries.
    /// </para>
    /// </summary>
    public sealed class TradeService : IActorStore
    {
        readonly TradeRules _rules;
        readonly ItemRules _items;
        readonly RuleOptions _options;
        readonly IContentDatabase _content;
        readonly Inventories _inventories;
        readonly TreasureRoller _roller;
        readonly Hero _hero;
        readonly Func<string, int> _politicalFameOf;
        readonly Dictionary<ActorId, MerchantState> _states = new Dictionary<ActorId, MerchantState>();

        /// <summary>The actor left the game (<see cref="ActorRemoval"/>): its entries go.</summary>
        public void Forget(ActorId actor)
        {
            _states.Remove(actor);
        }

        public bool Holds(ActorId actor) => _states.ContainsKey(actor);

        public TradeService(TradeRules rules, ItemRules items, RuleOptions options, IContentDatabase content, Inventories inventories,
                            TreasureRoller roller, Hero hero, Func<string, int> politicalFameOf)
        {
            _rules = rules;
            _items = items;
            _options = options;
            _content = content;
            _inventories = inventories;
            _roller = roller;
            _hero = hero;
            _politicalFameOf = politicalFameOf;
        }

        public bool IsMerchant(ActorId actor) => _content.TryGet(actor.Name, out MerchantDef _);

        /// <summary>
        /// A conversation with a merchant begins at game time <paramref name="gameHours"/> (G3 ZS_Talk): the trade sets
        /// are generated once, and the refresh sets top up the stock on the first conversation or when more than
        /// <see cref="TradeRules.RefreshHours"/> passed since the last one ended. Further calls before
        /// <see cref="NoteContactEnded"/> (the trade page of the same conversation) change nothing.
        /// </summary>
        public void Open(ActorId merchant, double gameHours)
        {
            if (!_content.TryGet(merchant.Name, out MerchantDef def)) return;
            var state = StateOf(merchant);
            if (state.InContact) return;
            state.InContact = true;
            var stock = _inventories.Of(merchant);
            bool firstContact = !state.Generated;
            bool refresh = firstContact || gameHours - state.LastContactHours > _rules.RefreshHours;
            foreach (var set in Sets(def))
            {
                if (firstContact && set.Use == TreasureUse.TradeGenerate) Stock(state, stock, into => _roller.Generate(set, into));
                if (firstContact && set.Use == TreasureUse.NotRandom) Stock(state, stock, into => _roller.GenerateAll(set, into));
                if (refresh && set.Use == TreasureUse.TradeRefresh) Stock(state, stock, into => _roller.Refresh(set, stock, into));
            }
            state.Generated = true;
        }

        /// <summary>The conversation with a merchant ended (the refresh interval counts from here).</summary>
        public void NoteContactEnded(ActorId merchant, double gameHours)
        {
            if (!IsMerchant(merchant)) return;
            var state = StateOf(merchant);
            state.LastContactHours = gameHours;
            state.InContact = false;
        }

        public int BuyPrice(ActorId merchant, string item) => UnitPrice(merchant, item, playerSells: false);

        public int SellPrice(ActorId merchant, string item) => UnitPrice(merchant, item, playerSells: true);

        /// <summary>How many of an item the merchant offers (his merchandise that he still carries).</summary>
        public int ForSale(ActorId merchant, string item)
        {
            if (item == _items.GoldItem || !_states.TryGetValue(merchant, out var state)) return 0;
            return Math.Min(state.Merchandise.TryGetValue(item, out int n) ? n : 0, _inventories.Of(merchant).Count(item));
        }

        /// <summary>
        /// How many units of an item the actor carries as merchandise (G3 stack type 2, gold included): hidden from the
        /// loot page and quick loot of a downed or dead merchant.
        /// </summary>
        public int Merchandise(ActorId merchant, string item)
        {
            if (!_states.TryGetValue(merchant, out var state) || !state.Merchandise.TryGetValue(item ?? "", out int n)) return 0;
            return Math.Min(n, _inventories.Of(merchant).Count(item));
        }

        /// <summary>
        /// Whether a stack may go into an offer (G3: the hero's list hides gold and mission items; the merchant's list
        /// shows only merchandise, and OnWantsToSell refuses body armour above the hero's political fame).
        /// </summary>
        public TradeResult CanOffer(ActorId merchant, string item, bool playerSells)
        {
            if (item == _items.GoldItem || !_content.TryGet(item, out ItemDef def)) return TradeResult.NotForSale;
            if (playerSells) return def.IsMission ? TradeResult.NotForSale : TradeResult.Done;
            if (ForSale(merchant, item) <= 0) return TradeResult.NotForSale;
            return FameAllowsSale(merchant, item) ? TradeResult.Done : TradeResult.NeedsFame;
        }

        /// <summary>
        /// G3's Trade button: both offers change hands as they are, when each is worth something and the hero's
        /// (<paramref name="heroGoods"/> at sell prices plus <paramref name="heroGold"/>) is worth at least the
        /// merchant's. A surplus on the hero's side is not paid out. Goods the merchant receives become merchandise.
        /// </summary>
        public TradeResult Exchange(ActorId merchant, IReadOnlyList<KeyValuePair<string, int>> heroGoods, int heroGold,
                                    IReadOnlyList<KeyValuePair<string, int>> vendorGoods, int vendorGold)
        {
            heroGoods = heroGoods ?? Array.Empty<KeyValuePair<string, int>>();
            vendorGoods = vendorGoods ?? Array.Empty<KeyValuePair<string, int>>();
            var hero = _inventories.Of(ActorId.Player);
            foreach (var kv in Totals(heroGoods))
            {
                var check = CanOffer(merchant, kv.Key, playerSells: true);
                if (check != TradeResult.Done) return check;
                if (!hero.Has(kv.Key, kv.Value)) return TradeResult.NotCarried;
            }
            foreach (var kv in Totals(vendorGoods))
            {
                var check = CanOffer(merchant, kv.Key, playerSells: false);
                if (check != TradeResult.Done) return check;
                if (ForSale(merchant, kv.Key) < kv.Value) return TradeResult.NotForSale;
            }
            if (heroGold < 0 || vendorGold < 0) return TradeResult.NotForSale;
            if (!hero.Has(_items.GoldItem, heroGold)) return TradeResult.NotEnoughGold;
            if (!_inventories.Of(merchant).Has(_items.GoldItem, vendorGold)) return TradeResult.MerchantLacksGold;
            int heroValue = heroGoods.Sum(kv => SellPrice(merchant, kv.Key) * kv.Value) + heroGold;
            int vendorValue = vendorGoods.Sum(kv => BuyPrice(merchant, kv.Key) * kv.Value) + vendorGold;
            if (heroValue <= 0 || vendorValue <= 0) return TradeResult.NothingOffered;
            if (heroValue < vendorValue) return TradeResult.NotEnoughGold;

            var state = StateOf(merchant);
            foreach (var kv in heroGoods) state.Add(kv.Key, _inventories.Transfer(ActorId.Player, merchant, kv.Key, kv.Value));
            foreach (var kv in vendorGoods) state.Add(kv.Key, -_inventories.Transfer(merchant, ActorId.Player, kv.Key, kv.Value));
            _inventories.Transfer(ActorId.Player, merchant, _items.GoldItem, heroGold);
            _inventories.Transfer(merchant, ActorId.Player, _items.GoldItem, vendorGold);
            return TradeResult.Done;
        }

        /// <summary>Buys at the unit price, paying the exact amount (a balanced <see cref="Exchange"/>).</summary>
        public TradeResult Buy(ActorId merchant, string item, int amount)
        {
            if (amount <= 0) return TradeResult.NotForSale;
            var check = CanOffer(merchant, item, playerSells: false);
            if (check != TradeResult.Done) return check;
            if (ForSale(merchant, item) < amount) return TradeResult.NotForSale;
            int cost = BuyPrice(merchant, item) * amount;
            if (!_inventories.Of(ActorId.Player).Has(_items.GoldItem, cost)) return TradeResult.NotEnoughGold;
            return Exchange(merchant, null, cost, new[] { new KeyValuePair<string, int>(item, amount) }, 0);
        }

        /// <summary>Sells to the merchant, who pays from his own gold (his stock sets give him a purse); quest items are refused.</summary>
        public TradeResult Sell(ActorId merchant, string item, int amount)
        {
            if (amount <= 0 || !_inventories.Of(ActorId.Player).Has(item, amount)) return TradeResult.NotCarried;
            var check = CanOffer(merchant, item, playerSells: true);
            if (check != TradeResult.Done) return check;
            int earned = SellPrice(merchant, item) * amount;
            if (!_inventories.Of(merchant).Has(_items.GoldItem, earned)) return TradeResult.MerchantLacksGold;
            return Exchange(merchant, new[] { new KeyValuePair<string, int>(item, amount) }, 0, null, earned);
        }

        /// <summary>Fame a merchant needs for selling this item (0 = none).</summary>
        public int RequiredFame(string item)
        {
            if (!_content.TryGet(item, out ItemDef def) || def.Slot != EquipSlot.Armor) return 0;
            var limits = _rules.ArmorSales.For(_options);
            if (limits == null) return 0;
            foreach (var limit in limits)
                if (limit.MaxGoldValue == 0 || def.GoldValue <= limit.MaxGoldValue) return limit.RequiredFame;
            return 0;
        }

        /// <summary>The political faction whose fame the merchant's armour sales need (null = none).</summary>
        public string FactionOf(ActorId merchant) => _content.TryGet(merchant.Name, out MerchantDef def) ? def.Faction : null;

        internal IEnumerable<KeyValuePair<ActorId, MerchantState>> States => _states;

        internal void Restore(ActorId merchant, bool generated, double lastContactHours, IEnumerable<KeyValuePair<string, int>> merchandise)
        {
            var state = new MerchantState { Generated = generated, LastContactHours = lastContactHours };
            foreach (var kv in merchandise ?? Array.Empty<KeyValuePair<string, int>>()) state.Add(kv.Key, kv.Value);
            _states[merchant] = state;
        }

        /// <summary>Saves from before the merchandise record: everything a merchant carries counts as merchandise.</summary>
        internal void AssumeAllMerchandise()
        {
            foreach (var kv in _states)
            {
                kv.Value.Merchandise.Clear();
                foreach (var s in _inventories.Of(kv.Key).Stacks) kv.Value.Add(s.Item, s.Amount);
            }
        }

        bool FameAllowsSale(ActorId merchant, string item)
        {
            int required = RequiredFame(item);
            return required == 0 || _politicalFameOf(FactionOf(merchant)) >= required;
        }

        // G3 OnPlayerGetGoldCost. The merchant-attitude term of trading.json is never applied: G3 asks GetAttitude with the
        // player as the first entity on both price paths, and GetAttitude answers None for the player (Docs/gameplay-logic.md §7.3).
        int UnitPrice(ActorId merchant, string item, bool playerSells)
        {
            if (!_content.TryGet(item, out ItemDef def)) return 0;
            string specialty = _content.TryGet(merchant.Name, out MerchantDef m) ? m.Specialty : null;
            var query = new PriceQuery(def, playerSells, specialty, _hero.Knows(_rules.BarterPerk), merchantFriendly: false);
            return PriceCalculator.UnitPrice(_rules, _options, query);
        }

        /// <summary>Rolls into a scratch inventory, then adds the result to the stock as merchandise.</summary>
        static void Stock(MerchantState state, Inventory stock, Action<Inventory> roll)
        {
            var rolled = new Inventory();
            roll(rolled);
            foreach (var s in rolled.Stacks)
            {
                stock.Add(s.Item, s.Amount, s.Quality);
                state.Add(s.Item, s.Amount);
            }
        }

        static IEnumerable<KeyValuePair<string, int>> Totals(IEnumerable<KeyValuePair<string, int>> goods) =>
            goods.GroupBy(kv => kv.Key).Select(g => new KeyValuePair<string, int>(g.Key, g.Sum(kv => kv.Value)));

        IEnumerable<TreasureSetDef> Sets(MerchantDef def) =>
            def.Stock.Select(id => _content.TryGet(id, out TreasureSetDef set) ? set : null).Where(s => s != null);

        MerchantState StateOf(ActorId merchant)
        {
            if (!_states.TryGetValue(merchant, out var state)) _states[merchant] = state = new MerchantState();
            return state;
        }

        internal sealed class MerchantState
        {
            public bool Generated;
            public double LastContactHours;
            /// <summary>Not saved: a conversation is in progress (stock already prepared for it).</summary>
            public bool InContact;
            public readonly SortedDictionary<string, int> Merchandise = new SortedDictionary<string, int>(StringComparer.Ordinal);

            public void Add(string item, int amount)
            {
                if (amount == 0 || string.IsNullOrEmpty(item)) return;
                int n = (Merchandise.TryGetValue(item, out int have) ? have : 0) + amount;
                if (n > 0) Merchandise[item] = n;
                else Merchandise.Remove(item);
            }
        }
    }
}
