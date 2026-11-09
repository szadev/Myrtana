using System;
using System.Collections.Generic;
using Myrtana.Core;

namespace Myrtana.Rules
{
    /// <summary>
    /// The hero's spell book (G3 gCInventory_PS spell stacks, Docs/combat.md §12.6). In G3 the hero carries a stack of every
    /// spell item (<c>It_Spell_*</c>, category Spellbook) with a "learned" flag: teaching runs <c>LearnSpell</c>
    /// (<c>FindSpellStackIndex</c> + <c>LearnStack</c>, Game.dll 0x100b6920), and readying a spell-book item from a quick
    /// slot or the spell page, using it, or putting it on the quick bar needs <c>HasLearnedSpell</c> of its spell
    /// (Script_Game OnPlayerUseQuickSlot / OnPlayerUseStack / OnPlayerAssignStackKey). A found spell-book item merges into
    /// the unlearned stack: it shows in colour on the spell page but casts nothing. Scrolls, runes and potions that cast
    /// a spell need no learning.
    /// <para>Here the learned flag is <see cref="Hero.Knows"/> of the spell (<c>Spl_*</c>, saved with the hero), and the
    /// hero carries one unit of every learned spell's item so that it can be readied and put on the quick bar.</para>
    /// </summary>
    public sealed class HeroSpells
    {
        readonly Hero _hero;
        readonly Inventory _inventory;
        readonly Dictionary<string, string> _spellOfItem = new Dictionary<string, string>(StringComparer.Ordinal);
        readonly Dictionary<string, string> _itemOfSpell = new Dictionary<string, string>(StringComparer.Ordinal);
        bool _syncing;

        public HeroSpells(Hero hero, Inventory heroInventory, IContentDatabase content)
        {
            _hero = hero ?? throw new ArgumentNullException(nameof(hero));
            _inventory = heroInventory ?? throw new ArgumentNullException(nameof(heroInventory));
            foreach (var spell in content.All<SpellDef>())
                foreach (var item in spell.Items)
                {
                    if (!content.TryGet(item, out ItemDef def) || def.Category != ItemCategory.Spellbook) continue;
                    _spellOfItem[item] = spell.Id;
                    if (!_itemOfSpell.ContainsKey(spell.Id)) _itemOfSpell[spell.Id] = item;
                }
            _hero.Changed += Sync;
        }

        /// <summary>The spell-book items of the content (one per spell), in no particular order.</summary>
        public IEnumerable<string> Items => _itemOfSpell.Values;

        /// <summary>The spell of a spell-book item, or null (scrolls, runes, other items).</summary>
        public string SpellOf(string item) => item != null && _spellOfItem.TryGetValue(item, out var spell) ? spell : null;

        /// <summary>The spell-book item of a spell ("Spl_Fireball" → "It_Spell_Fireball"), or null.</summary>
        public string ItemOf(string spell) => spell != null && _itemOfSpell.TryGetValue(spell, out var item) ? item : null;

        /// <summary>G3 HasLearnedSpell.</summary>
        public bool IsLearned(string spell) => _hero.Knows(spell);

        /// <summary>
        /// The item may be readied, used or put on the quick bar as far as learning goes: anything but a spell-book item
        /// whose spell the hero has not learned.
        /// </summary>
        public bool CanReady(string item)
        {
            string spell = SpellOf(item);
            return spell == null || _hero.Knows(spell);
        }

        /// <summary>The hero carries the item of every learned spell (G3: the learned stack is always in the inventory).</summary>
        public void Sync()
        {
            if (_syncing) return;
            _syncing = true;
            try
            {
                foreach (var skill in _hero.Skills)
                {
                    string item = ItemOf(skill);
                    if (item != null && !_inventory.Has(item)) _inventory.Add(item, 1);
                }
            }
            finally
            {
                _syncing = false;
            }
        }
    }
}
