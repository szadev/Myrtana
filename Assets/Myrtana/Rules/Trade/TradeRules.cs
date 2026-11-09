using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Myrtana.Core;

namespace Myrtana.Rules
{
    /// <summary>Prices, stock refresh and armour sale limits (id "trade", G3: gameplay-data/trading.json).</summary>
    public sealed class TradeRules : IDefinition
    {
        public const string DefinitionId = "trade";

        [JsonConstructor]
        public TradeRules(string id, double buyFactor, double sellFactor, double specialistSellFactor,
                          IReadOnlyList<TradeSpecialty> specialties, string barterPerk, double barterSellFactor,
                          double barterBuyFactor, double friendlyAdjustment, Balanced<double> sellPenalty, int minimumPrice, double refreshHours,
                          Balanced<IReadOnlyList<ArmorSaleLimit>> armorSales)
        {
            Id = id;
            BuyFactor = buyFactor;
            SellFactor = sellFactor;
            SpecialistSellFactor = specialistSellFactor;
            Specialties = specialties ?? Array.Empty<TradeSpecialty>();
            BarterPerk = barterPerk;
            BarterSellFactor = barterSellFactor;
            BarterBuyFactor = barterBuyFactor;
            FriendlyAdjustment = friendlyAdjustment;
            SellPenalty = sellPenalty;
            MinimumPrice = minimumPrice;
            RefreshHours = refreshHours;
            ArmorSales = armorSales;
        }

        public string Id { get; }
        /// <summary>Factor F when the player buys …</summary>
        public double BuyFactor { get; }
        /// <summary>… sells …</summary>
        public double SellFactor { get; }
        /// <summary>… sells goods of a merchant's specialty.</summary>
        public double SpecialistSellFactor { get; }
        public IReadOnlyList<TradeSpecialty> Specialties { get; }
        /// <summary>Perk that changes the barter factor B to <see cref="BarterSellFactor"/> / <see cref="BarterBuyFactor"/>.</summary>
        public string BarterPerk { get; }
        public double BarterSellFactor { get; }
        public double BarterBuyFactor { get; }
        /// <summary>A friendly merchant raises B by this when the player sells and lowers it when he buys.</summary>
        public double FriendlyAdjustment { get; }
        /// <summary>Subtracted from F·B when the player sells.</summary>
        public Balanced<double> SellPenalty { get; }
        public int MinimumPrice { get; }
        /// <summary>Game hours after a conversation before a merchant's refresh sets top up the stock.</summary>
        public double RefreshHours { get; }
        /// <summary>Body armour a merchant sells only with enough political fame, by gold value (ascending limits).</summary>
        public Balanced<IReadOnlyList<ArmorSaleLimit>> ArmorSales { get; }

        string IDefinition.Key => Id;
    }

    /// <summary>A merchant specialty and the item kinds it buys at the specialist factor.</summary>
    public sealed class TradeSpecialty
    {
        [JsonConstructor]
        public TradeSpecialty(string specialty, IReadOnlyList<string> itemKinds)
        {
            Specialty = specialty;
            ItemKinds = itemKinds ?? Array.Empty<string>();
        }

        public string Specialty { get; }
        public IReadOnlyList<string> ItemKinds { get; }
    }

    /// <summary>Armour up to <see cref="MaxGoldValue"/> (0 = no limit) needs <see cref="RequiredFame"/>.</summary>
    public sealed class ArmorSaleLimit
    {
        [JsonConstructor]
        public ArmorSaleLimit(int maxGoldValue, int requiredFame)
        {
            MaxGoldValue = maxGoldValue;
            RequiredFame = requiredFame;
        }

        public int MaxGoldValue { get; }
        public int RequiredFame { get; }
    }
}
