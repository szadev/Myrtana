using System;

namespace Myrtana.Rules
{
    /// <summary>
    /// The hero's ten quick slots (G3 HUD quick bar, keys 1…9 and 0). Each slot names an item the hero carries; an
    /// item sits in at most one slot, and a slot empties when the hero no longer has its item.
    /// </summary>
    public sealed class QuickSlots
    {
        public const int Count = 10;

        readonly string[] _items = new string[Count];
        readonly Inventory _hero;

        public QuickSlots(Inventory hero)
        {
            _hero = hero ?? throw new ArgumentNullException(nameof(hero));
            _hero.Changed += Prune;
        }

        public event Action Changed;

        /// <summary>
        /// Whether an item may go on the bar beyond being carried (G3 OnPlayerAssignStackKey: a spell-book item only once
        /// its spell is learned, <see cref="HeroSpells.CanReady"/>); null = every carried item.
        /// </summary>
        public Func<string, bool> CanAssign { get; set; }

        /// <summary>The item in a slot, or null.</summary>
        public string ItemAt(int slot) => IsSlot(slot) ? _items[slot] : null;

        /// <summary>The slot holding an item, or −1.</summary>
        public int SlotOf(string item)
        {
            if (string.IsNullOrEmpty(item)) return -1;
            for (int i = 0; i < Count; i++)
                if (_items[i] == item) return i;
            return -1;
        }

        /// <summary>
        /// Puts a carried item into a slot; it leaves the slot it was in, and the slot's previous item is dropped from
        /// the bar. Returns false for an item the hero does not carry, one <see cref="CanAssign"/> refuses, or an invalid slot.
        /// </summary>
        public bool Assign(int slot, string item)
        {
            if (!IsSlot(slot) || string.IsNullOrEmpty(item) || !_hero.Has(item)) return false;
            if (CanAssign != null && !CanAssign(item)) return false;
            if (_items[slot] == item) return true;
            int old = SlotOf(item);
            if (old >= 0) _items[old] = null;
            _items[slot] = item;
            Changed?.Invoke();
            return true;
        }

        public void Clear(int slot)
        {
            if (!IsSlot(slot) || _items[slot] == null) return;
            _items[slot] = null;
            Changed?.Invoke();
        }

        /// <summary>Slot of a number key: 1…9 → 0…8, 0 → 9; −1 for other digits.</summary>
        public static int SlotOfDigit(int digit) => digit == 0 ? Count - 1 : digit >= 1 && digit <= 9 ? digit - 1 : -1;

        /// <summary>The number key of a slot as shown on the bar: 0…8 → 1…9, 9 → 0.</summary>
        public static int DigitOfSlot(int slot) => (slot + 1) % Count;

        static bool IsSlot(int slot) => slot >= 0 && slot < Count;

        void Prune()
        {
            bool changed = false;
            for (int i = 0; i < Count; i++)
            {
                if (_items[i] == null || _hero.Has(_items[i])) continue;
                _items[i] = null;
                changed = true;
            }
            if (changed) Changed?.Invoke();
        }
    }
}
