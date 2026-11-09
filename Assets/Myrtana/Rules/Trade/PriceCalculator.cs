using System;
using System.Linq;

namespace Myrtana.Rules
{
    /// <summary>One price question: who sells what to whom.</summary>
    public readonly struct PriceQuery
    {
        public PriceQuery(ItemDef item, bool playerSells, string merchantSpecialty, bool playerHasBarter, bool merchantFriendly)
        {
            Item = item;
            PlayerSells = playerSells;
            MerchantSpecialty = merchantSpecialty;
            PlayerHasBarter = playerHasBarter;
            MerchantFriendly = merchantFriendly;
        }

        public ItemDef Item { get; }
        public bool PlayerSells { get; }
        public string MerchantSpecialty { get; }
        public bool PlayerHasBarter { get; }
        public bool MerchantFriendly { get; }
    }

    /// <summary>
    /// G3's price per unit (gameplay-logic.md §7.3): max(min, trunc(gold value · (F·B − penalty))), where F depends on
    /// the direction and the merchant's specialty, B on the barter perk and a friendly merchant, and the Alternative
    /// Balancing penalty applies when the player sells.
    /// </summary>
    public static class PriceCalculator
    {
        public static int UnitPrice(TradeRules rules, RuleOptions options, in PriceQuery q)
        {
            double factor = Direction(rules, q) * Barter(rules, q);
            if (q.PlayerSells) factor -= rules.SellPenalty.For(options);
            // The epsilon keeps exact decimal products (100 · 0.25) from truncating one below due to binary fractions.
            return Math.Max(rules.MinimumPrice, (int)(q.Item.GoldValue * factor + 1e-9));
        }

        static double Direction(TradeRules rules, in PriceQuery q)
        {
            if (!q.PlayerSells) return rules.BuyFactor;
            return IsSpecialty(rules, q.MerchantSpecialty, q.Item.Kind) ? rules.SpecialistSellFactor : rules.SellFactor;
        }

        static double Barter(TradeRules rules, in PriceQuery q)
        {
            double b = q.PlayerHasBarter ? (q.PlayerSells ? rules.BarterSellFactor : rules.BarterBuyFactor) : 1.0;
            if (q.MerchantFriendly) b += q.PlayerSells ? rules.FriendlyAdjustment : -rules.FriendlyAdjustment;
            return b;
        }

        public static bool IsSpecialty(TradeRules rules, string specialty, string itemKind) =>
            !string.IsNullOrEmpty(specialty) &&
            rules.Specialties.Any(s => s.Specialty == specialty && s.ItemKinds.Contains(itemKind));
    }
}
