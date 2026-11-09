using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Myrtana.Core;

namespace Myrtana.Rules
{
    /// <summary>What a treasure set is used for (G3 gETreasureDistribution).</summary>
    public enum TreasureUse
    {
        Plunder, Unique, Trophy, Weaponry, TradeGenerate, TradeRefresh, Mining, Pickpocket, NotRandom, Guard, Ammunition,
    }

    /// <summary>A list of candidate stacks that containers, NPCs and merchants are filled from.</summary>
    public sealed class TreasureSetDef : IDefinition
    {
        [JsonConstructor]
        public TreasureSetDef(string id, TreasureUse use, int minStacks, int maxStacks, IReadOnlyList<TreasureStack> stacks)
        {
            Id = id;
            Use = use;
            MinStacks = minStacks;
            MaxStacks = maxStacks;
            Stacks = stacks ?? Array.Empty<TreasureStack>();
        }

        public string Id { get; }
        public TreasureUse Use { get; }
        public int MinStacks { get; }
        public int MaxStacks { get; }
        public IReadOnlyList<TreasureStack> Stacks { get; }

        string IDefinition.Key => Id;
    }

    public sealed class TreasureStack
    {
        [JsonConstructor]
        public TreasureStack(string item, int amount, int quality)
        {
            Item = item;
            Amount = amount;
            Quality = quality;
        }

        public string Item { get; }
        public int Amount { get; }
        public int Quality { get; }
    }
}
