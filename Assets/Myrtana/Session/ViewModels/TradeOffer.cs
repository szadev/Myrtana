using System;
using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;
using Myrtana.Rules;

namespace Myrtana.Session
{
    /// <summary>
    /// The two offer pools of the G3 trade page: stacks the merchant and the hero put on the table, plus gold added by
    /// <i>Gold balance</i>. A stack enters a pool only if the rules let it be offered (<see cref="TradeService.CanOffer"/>:
    /// merchandise, no gold, no mission items, armour fame; the hero's worn items stay out). Nothing changes hands until
    /// <see cref="Accept"/>, which hands both pools over as they are (<see cref="TradeService.Exchange"/>).
    /// </summary>
    public sealed class TradeOffer
    {
        public enum Side { Vendor, Hero }

        readonly GameSession _session;
        readonly ActorId _merchant;
        readonly Dictionary<Side, List<KeyValuePair<string, int>>> _pools = new Dictionary<Side, List<KeyValuePair<string, int>>>
        {
            [Side.Vendor] = new List<KeyValuePair<string, int>>(), [Side.Hero] = new List<KeyValuePair<string, int>>(),
        };
        readonly Dictionary<Side, int> _gold = new Dictionary<Side, int> { [Side.Vendor] = 0, [Side.Hero] = 0 };

        public TradeOffer(GameSession session, ActorId merchant)
        {
            _session = session;
            _merchant = merchant;
        }

        public ActorId Merchant => _merchant;

        public ActorId Owner(Side side) => side == Side.Vendor ? _merchant : ActorId.Player;

        public bool IsEmpty => _pools.Values.All(p => p.Count == 0) && _gold.Values.All(g => g == 0);

        /// <summary>G3 enables the Trade button when both pools are worth something and the hero's at least the merchant's.</summary>
        public bool CanAccept => Value(Side.Vendor) > 0 && Value(Side.Hero) >= Value(Side.Vendor);

        /// <summary>Why the last <see cref="Add"/> put nothing into the pool (Done when it did).</summary>
        public TradeResult LastRefusal { get; private set; }

        /// <summary>
        /// G3's OnWantsToSell refusal text (<c>GO_RequiredPoliticalReputation</c>): $(value) = the fame the armour needs,
        /// $(name) = the merchant's faction (<c>GO_PoliticalFame_PAL_*</c>, the fame group's name).
        /// </summary>
        public string FameRefusal(string template, string item)
        {
            string faction = _session.Items.Trade.FactionOf(_merchant);
            string name = faction != null && _session.Content.Database.TryGet(faction, out FameGroupDef group)
                ? _session.Localizer.Text(group.Name) : faction ?? string.Empty;
            return template.Replace("$(value)", _session.Items.Trade.RequiredFame(item).ToString()).Replace("$(name)", name);
        }

        /// <summary>How many of an item the owner can still offer: the merchant's merchandise, the hero's unworn units.</summary>
        public int Available(Side side, string item)
        {
            if (_session.Items.Trade.CanOffer(_merchant, item, side == Side.Hero) == TradeResult.NotForSale) return 0;
            int have = side == Side.Vendor
                ? _session.Items.Trade.ForSale(_merchant, item)
                : _session.Items.Inventories.Of(ActorId.Player).Count(item) - WornUnits(item);
            return Math.Max(0, have - Offered(side, item));
        }

        /// <summary>Gold per unit for a stack on this side (what the hero pays, or gets).</summary>
        public int UnitPrice(Side side, string item) =>
            side == Side.Vendor ? _session.Items.Trade.BuyPrice(_merchant, item) : _session.Items.Trade.SellPrice(_merchant, item);

        public int Offered(Side side, string item) => _pools[side].Where(kv => kv.Key == item).Sum(kv => kv.Value);

        public int Gold(Side side) => _gold[side];

        /// <summary>Σ unit price × amount of the pool plus the gold put in by balancing.</summary>
        public int Value(Side side) => _pools[side].Sum(kv => UnitPrice(side, kv.Key) * kv.Value) + _gold[side];

        /// <summary>Puts up to <paramref name="amount"/> of a stack its owner still has (not yet offered) into the pool.</summary>
        public int Add(Side side, string item, int amount)
        {
            LastRefusal = _session.Items.Trade.CanOffer(_merchant, item, side == Side.Hero);
            if (LastRefusal != TradeResult.Done) return 0;
            amount = Math.Min(amount, Available(side, item));
            if (amount <= 0)
            {
                LastRefusal = TradeResult.NotCarried;
                return 0;
            }
            var pool = _pools[side];
            int index = pool.FindIndex(kv => kv.Key == item);
            if (index >= 0) pool[index] = new KeyValuePair<string, int>(item, pool[index].Value + amount);
            else pool.Add(new KeyValuePair<string, int>(item, amount));
            _gold[Side.Vendor] = _gold[Side.Hero] = 0;
            return amount;
        }

        /// <summary>Takes up to <paramref name="amount"/> of a pooled stack back.</summary>
        public int Remove(Side side, string item, int amount)
        {
            var pool = _pools[side];
            int index = pool.FindIndex(kv => kv.Key == item);
            if (index < 0 || amount <= 0) return 0;
            amount = Math.Min(amount, pool[index].Value);
            if (amount == pool[index].Value) pool.RemoveAt(index);
            else pool[index] = new KeyValuePair<string, int>(item, pool[index].Value - amount);
            _gold[Side.Vendor] = _gold[Side.Hero] = 0;
            return amount;
        }

        public void Clear(Side side)
        {
            _pools[side].Clear();
            _gold[Side.Vendor] = _gold[Side.Hero] = 0;
        }

        public void Clear()
        {
            Clear(Side.Vendor);
            Clear(Side.Hero);
        }

        /// <summary>Gold balance: the cheaper side adds the difference in gold, if its owner has that much.</summary>
        public bool BalanceGold()
        {
            _gold[Side.Vendor] = _gold[Side.Hero] = 0;
            int difference = Value(Side.Vendor) - Value(Side.Hero);
            if (difference == 0) return true;
            var payer = difference > 0 ? Side.Hero : Side.Vendor;
            int needed = Math.Abs(difference);
            if (ItemLists.Gold(_session, Owner(payer)) < needed) return false;
            _gold[payer] = needed;
            return true;
        }

        /// <summary>The pool as list rows (amount offered, unit price of that side).</summary>
        public IReadOnlyList<ItemRow> Rows(Side side)
        {
            var db = _session.Content.Database;
            var rows = new List<ItemRow>();
            foreach (var kv in _pools[side])
            {
                if (!db.TryGet(kv.Key, out ItemDef def)) continue;
                rows.Add(new ItemRow(def.Id, ItemLists.Name(_session, def), def.Icon, kv.Value, false, UnitPrice(side, def.Id), def.Category, def.Quality));
            }
            return rows;
        }

        /// <summary>An owner's list without what is in the pool (the grids show what is left).</summary>
        public IReadOnlyList<ItemRow> WithoutOffered(Side side, IEnumerable<ItemRow> rows) =>
            rows.Select(r => (row: r, left: r.Amount - Offered(side, r.Item)))
                .Where(x => x.left > 0)
                .Select(x => x.left == x.row.Amount
                            ? x.row
                            : new ItemRow(x.row.Item, x.row.Name, x.row.Icon, x.left, x.row.Worn, x.row.Price, x.row.Category, x.row.Quality, x.row.Held, x.row.Acquired))
                .ToList();

        /// <summary>
        /// An owner's grid as G3 shows it (<see cref="Available"/>): only the merchant's merchandise, the hero's list without
        /// gold, mission items and worn units, each without what is already pooled.
        /// </summary>
        public IReadOnlyList<ItemRow> Offerable(Side side, IEnumerable<ItemRow> rows) =>
            rows.Select(r => (row: r, left: Available(side, r.Item)))
                .Where(x => x.left > 0)
                .Select(x => x.left == x.row.Amount
                            ? x.row
                            : new ItemRow(x.row.Item, x.row.Name, x.row.Icon, x.left, x.row.Worn, x.row.Price, x.row.Category, x.row.Quality, x.row.Held, x.row.Acquired))
                .ToList();

        /// <summary>
        /// G3's Trade button: when <see cref="CanAccept"/>, both pools (stacks and balancing gold) change hands as they
        /// are and the pools empty; a surplus on the hero's side is the merchant's gain. Otherwise nothing happens.
        /// </summary>
        public TradeResult Accept()
        {
            var result = _session.Items.Trade.Exchange(_merchant, _pools[Side.Hero], _gold[Side.Hero], _pools[Side.Vendor], _gold[Side.Vendor]);
            if (result == TradeResult.Done) Clear();
            return result;
        }

        /// <summary>Units of a worn item that cannot be offered: the whole ammunition stack, one of anything else [interp].</summary>
        int WornUnits(string item)
        {
            var equipment = _session.Items.Equipment;
            if (!equipment.IsWorn(item)) return 0;
            return equipment.In(WornSlot.Ammo) == item ? _session.Items.Inventories.Of(ActorId.Player).Count(item) : 1;
        }
    }
}
