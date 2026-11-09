using System;
using System.Collections.Generic;

namespace Myrtana.Rules
{
    /// <summary>Some units of one item with one quality.</summary>
    public sealed class ItemStack
    {
        public ItemStack(string item, int amount, int quality = 0, int acquired = 0)
        {
            Item = item;
            Amount = amount;
            Quality = quality;
            Acquired = acquired;
        }

        public string Item { get; }
        public int Amount { get; internal set; }
        public int Quality { get; }
        /// <summary>
        /// When units last came into this stack, as the owner's running acquisition number (higher = newer; owner's
        /// "newest first" list order). G3 keeps no such order.
        /// </summary>
        public int Acquired { get; internal set; }
    }

    /// <summary>
    /// The items one actor carries. Stacks of the same item and quality merge (items.json "stacks"); stacks keep the
    /// order in which they were first added. Gold is an ordinary item. Every addition stamps the stack with the next
    /// acquisition number (<see cref="ItemStack.Acquired"/>).
    /// </summary>
    public sealed class Inventory
    {
        readonly List<ItemStack> _stacks = new List<ItemStack>();
        int _lastAcquired;

        public event Action Changed;

        public IReadOnlyList<ItemStack> Stacks => _stacks;

        public int Count(string item)
        {
            int n = 0;
            foreach (var s in _stacks)
                if (s.Item == item) n += s.Amount;
            return n;
        }

        public bool Has(string item, int amount = 1) => Count(item) >= amount;

        public void Add(string item, int amount, int quality = 0)
        {
            if (amount <= 0 || string.IsNullOrEmpty(item)) return;
            var stack = _stacks.Find(s => s.Item == item && s.Quality == quality);
            if (stack != null)
            {
                stack.Amount += amount;
                stack.Acquired = ++_lastAcquired;
            }
            else _stacks.Add(new ItemStack(item, amount, quality, ++_lastAcquired));
            Changed?.Invoke();
        }

        /// <summary>The highest acquisition number handed out so far (saved with the inventory).</summary>
        public int LastAcquired => _lastAcquired;

        /// <summary>
        /// Loading: puts a saved stack back with its acquisition number (0 = unknown, then it counts as acquired now) and
        /// keeps the counter above every number in use.
        /// </summary>
        internal void Restore(string item, int amount, int quality, int acquired)
        {
            Add(item, amount, quality);
            if (acquired <= 0) return;
            var stack = _stacks.Find(s => s.Item == item && s.Quality == quality);
            if (stack != null) stack.Acquired = acquired;
            if (acquired > _lastAcquired) _lastAcquired = acquired;
        }

        /// <summary>Removes up to <paramref name="amount"/> units (any quality, plain stacks first); returns how many were removed.</summary>
        public int Remove(string item, int amount)
        {
            int left = amount;
            for (int pass = 0; pass < 2 && left > 0; pass++)
                for (int i = _stacks.Count - 1; i >= 0 && left > 0; i--)
                {
                    var s = _stacks[i];
                    if (s.Item != item || (pass == 0) != (s.Quality == 0)) continue;
                    int take = Math.Min(left, s.Amount);
                    s.Amount -= take;
                    left -= take;
                    if (s.Amount == 0) _stacks.RemoveAt(i);
                }
            if (left != amount) Changed?.Invoke();
            return amount - left;
        }

        /// <summary>Removes up to <paramref name="amount"/> units of the stack with exactly this quality; returns how many were removed.</summary>
        public int RemoveExact(string item, int quality, int amount)
        {
            int i = _stacks.FindIndex(s => s.Item == item && s.Quality == quality);
            if (i < 0 || amount <= 0) return 0;
            var stack = _stacks[i];
            int take = Math.Min(amount, stack.Amount);
            stack.Amount -= take;
            if (stack.Amount == 0) _stacks.RemoveAt(i);
            Changed?.Invoke();
            return take;
        }

        /// <summary>The quality bits of the best unit of an item (for requirement checks).</summary>
        public int QualityOf(string item)
        {
            int q = 0;
            foreach (var s in _stacks)
                if (s.Item == item) q |= s.Quality;
            return q;
        }

        internal void Clear()
        {
            _stacks.Clear();
            _lastAcquired = 0;
            Changed?.Invoke();
        }
    }
}
