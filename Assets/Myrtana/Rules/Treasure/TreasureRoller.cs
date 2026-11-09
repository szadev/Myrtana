using System;
using Myrtana.Core;

namespace Myrtana.Rules
{
    /// <summary>
    /// Fills an inventory from a treasure set (trading.json "stock"; the general roll algorithm of other uses is still
    /// open in formats-items.md §5):
    /// <list type="bullet">
    /// <item>generate: rand[min, max] picks of a random stack, each rand[amount/2, amount] units (at least 1);</item>
    /// <item>all: every stack in full (NotRandom);</item>
    /// <item>refresh: a stack (same item and quality) missing or below amount/2 gets max(1, rand[amount/2, amount]) more.</item>
    /// </list>
    /// Quality = stack quality | item quality (the item's part is added by the caller's item lookup).
    /// </summary>
    public sealed class TreasureRoller
    {
        readonly IRandom _random;

        public TreasureRoller(IRandom random) => _random = random;

        public void Generate(TreasureSetDef set, Inventory into)
        {
            if (set.Stacks.Count == 0) return;
            int picks = Between(set.MinStacks, set.MaxStacks);
            for (int i = 0; i < picks; i++)
            {
                var stack = set.Stacks[_random.Next(set.Stacks.Count)];
                into.Add(stack.Item, Math.Max(1, Between(stack.Amount / 2, stack.Amount)), stack.Quality);
            }
        }

        public void GenerateAll(TreasureSetDef set, Inventory into)
        {
            foreach (var stack in set.Stacks) into.Add(stack.Item, stack.Amount, stack.Quality);
        }

        public void Refresh(TreasureSetDef set, Inventory into) => Refresh(set, into, into);

        /// <summary>
        /// G3 RefreshTradeInventory: a set stack is topped up when <paramref name="current"/> has no stack of that item and
        /// quality, or one with fewer than amount/2 (integer halving) units; the new units go to <paramref name="into"/>.
        /// </summary>
        public void Refresh(TreasureSetDef set, Inventory current, Inventory into)
        {
            foreach (var stack in set.Stacks)
            {
                int have = StackAmount(current, stack.Item, stack.Quality);
                if (have == 0 || have < stack.Amount / 2)
                    into.Add(stack.Item, Math.Max(1, Between(stack.Amount / 2, stack.Amount)), stack.Quality);
            }
        }

        static int StackAmount(Inventory inventory, string item, int quality)
        {
            foreach (var s in inventory.Stacks)
                if (s.Item == item && s.Quality == quality) return s.Amount;
            return 0;
        }

        /// <summary>Uniform in [min, max] (inclusive); min when max ≤ min.</summary>
        int Between(int min, int max) => max <= min ? min : min + _random.Next(max - min + 1);
    }
}
