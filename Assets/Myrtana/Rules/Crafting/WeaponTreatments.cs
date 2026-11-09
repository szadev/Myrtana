using System;
using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;

namespace Myrtana.Rules
{
    /// <summary>Which inventory page a blessing or poisoning item opens.</summary>
    public enum TreatmentKind { None, Bless, Poison }

    /// <summary>Why a blessing or poisoning did not happen.</summary>
    public enum TreatmentVerdict
    {
        Done,
        /// <summary>Not a weapon of the page, or the consumable is gone.</summary>
        NotPossible,
        /// <summary>The poison page needs Perk_PoisonBlade (HUD_MissingSkill).</summary>
        MissingSkill,
        /// <summary>The bless spell needs 100 MP (HUD_MissingAttrib).</summary>
        MissingMana,
        /// <summary>Burning or frozen weapons are magic already (HUD_AlreadyMagic).</summary>
        AlreadyMagic,
        AlreadyBlessed,
        AlreadyPoisoned,
    }

    /// <summary>A weapon was blessed or poisoned from the inventory (the presentation plays eff_inv_bless_01 / eff_inv_poison_01).</summary>
    public readonly struct WeaponTreated : IGameEvent
    {
        public WeaponTreated(TreatmentKind kind, string item, int amount, int quality)
        {
            Kind = kind;
            Item = item;
            Amount = amount;
            Quality = quality;
        }

        public TreatmentKind Kind { get; }
        public string Item { get; }
        public int Amount { get; }
        public int Quality { get; }
    }

    /// <summary>
    /// Blessing and poisoning weapons from the inventory (G3 CP 1.75 <c>OnPlayerUseStack</c> UseType Modify, the transform
    /// pages 11 Bless / 12 Poison and their worker; Docs/combat.md §12.5). Pure.
    /// <list type="bullet">
    /// <item>Bless: It_Scroll_BlessWeapon (one scroll used up) or It_Spell_BlessWeapon (100 MP; the page opens only with the
    /// spell carried). Lists the melee weapons (one- and two-handed, axes, halberds); armour is never touched.</item>
    /// <item>Poison: It_Potion_Poison (one potion used up); opening the page needs Perk_PoisonBlade. Lists blade weapons and,
    /// in CP, arrows: a stack of arrows gets as many poisoned as the poison arrow recipe makes (10).</item>
    /// <item>Burning or frozen weapons are refused (HUD_AlreadyMagic), so is a second blessing or poisoning. One item of the
    /// stack gets the quality bit (G3 ApplyQuality(…, 1)); the bit stays for good.</item>
    /// </list>
    /// </summary>
    public sealed class WeaponTreatments
    {
        static readonly HashSet<string> BlessKinds = new HashSet<string>(StringComparer.Ordinal) { "OneHanded", "TwoHanded", "Axe", "Halberd" };
        const string BladeDamage = "Blade", ArrowKind = "Arrow";

        readonly IContentDatabase _content;
        readonly Inventory _hero;
        readonly Func<string, bool> _knows;
        readonly Func<int> _mana;
        readonly Action<int> _spendMana;
        readonly IEventBus _events;

        public WeaponTreatments(IContentDatabase content, Inventory hero, Func<string, bool> heroKnows, Func<int> mana, Action<int> spendMana,
                                IEventBus events)
        {
            _content = content ?? throw new ArgumentNullException(nameof(content));
            _hero = hero ?? throw new ArgumentNullException(nameof(hero));
            _knows = heroKnows ?? (_ => false);
            _mana = mana ?? (() => 0);
            _spendMana = spendMana ?? (_ => { });
            _events = events;
        }

        /// <summary>The page an inventory item opens (None: not a blessing or poisoning item).</summary>
        public static TreatmentKind KindOf(string item) =>
            item == SpellEffectRules.BlessScroll || item == SpellEffectRules.BlessSpellItem ? TreatmentKind.Bless
            : item == SpellEffectRules.PoisonPotion ? TreatmentKind.Poison : TreatmentKind.None;

        /// <summary>
        /// Can <paramref name="item"/> open its page now? Poison needs Perk_PoisonBlade; the bless spell needs to be carried
        /// (G3: learned); the consumable must be there.
        /// </summary>
        public TreatmentVerdict CanOpen(string item)
        {
            var kind = KindOf(item);
            if (kind == TreatmentKind.None || !_hero.Has(item)) return TreatmentVerdict.NotPossible;
            if (kind == TreatmentKind.Poison && !_knows(SpellEffectRules.PoisonPerk)) return TreatmentVerdict.MissingSkill;
            return TreatmentVerdict.Done;
        }

        /// <summary>The stacks the page lists: melee weapons for blessing, blade weapons and arrows for poisoning.</summary>
        public IReadOnlyList<ItemStack> Candidates(TreatmentKind kind) =>
            _hero.Stacks.Where(s => _content.TryGet(s.Item, out ItemDef def) && Fits(kind, def)).ToList();

        static bool Fits(TreatmentKind kind, ItemDef def)
        {
            if (def.Category == ItemCategory.Armor) return false;
            if (kind == TreatmentKind.Bless) return def.Category == ItemCategory.Weapon && BlessKinds.Contains(def.Kind ?? "");
            if (kind != TreatmentKind.Poison) return false;
            if (string.Equals(def.Kind, ArrowKind, StringComparison.Ordinal)) return true;
            return def.Category == ItemCategory.Weapon && def.Damage != null && string.Equals(def.Damage.Type, BladeDamage, StringComparison.Ordinal);
        }

        /// <summary>What applying <paramref name="with"/> to <paramref name="stack"/> would do (nothing changes).</summary>
        public TreatmentVerdict Check(string with, ItemStack stack)
        {
            var kind = KindOf(with);
            if (kind == TreatmentKind.None || stack == null || !_content.TryGet(stack.Item, out ItemDef def) || !Fits(kind, def))
                return TreatmentVerdict.NotPossible;
            if (!_hero.Has(with)) return TreatmentVerdict.NotPossible;
            int quality = stack.Quality | def.Quality;
            if ((quality & (int)(WeaponQuality.Burning | WeaponQuality.Frozen)) != 0) return TreatmentVerdict.AlreadyMagic;
            if (kind == TreatmentKind.Bless && (quality & (int)WeaponQuality.Blessed) != 0) return TreatmentVerdict.AlreadyBlessed;
            if (kind == TreatmentKind.Poison && (quality & (int)WeaponQuality.Poisoned) != 0) return TreatmentVerdict.AlreadyPoisoned;
            if (with == SpellEffectRules.BlessSpellItem && _mana() < SpellEffectRules.BlessMana) return TreatmentVerdict.MissingMana;
            return TreatmentVerdict.Done;
        }

        /// <summary>
        /// Blesses or poisons one item of the stack (arrows: a batch of the recipe's size), paying the scroll, potion or
        /// 100 MP. <paramref name="amount"/> is how many items got the bit.
        /// </summary>
        public TreatmentVerdict Apply(string with, ItemStack stack, out int amount)
        {
            amount = 0;
            var verdict = Check(with, stack);
            if (verdict != TreatmentVerdict.Done) return verdict;
            var kind = KindOf(with);
            _content.TryGet(stack.Item, out ItemDef def);
            int bit = kind == TreatmentKind.Bless ? (int)WeaponQuality.Blessed : (int)WeaponQuality.Poisoned;
            amount = string.Equals(def.Kind, ArrowKind, StringComparison.Ordinal)
                ? Math.Min(stack.Amount, ArrowBatch())
                : 1;
            if (with == SpellEffectRules.BlessSpellItem) _spendMana(SpellEffectRules.BlessMana);
            else _hero.Remove(with, 1);
            string item = stack.Item;
            int quality = stack.Quality | bit;
            amount = _hero.RemoveExact(item, stack.Quality, amount);
            _hero.Add(item, amount, quality);
            _events?.Publish(new WeaponTreated(kind, item, amount, quality));
            return TreatmentVerdict.Done;
        }

        /// <summary>CP: the poison arrow recipe's result amount (10), 1 without the recipe in the content.</summary>
        int ArrowBatch() => _content.TryGet(SpellEffectRules.PoisonArrowRecipe, out RecipeDef recipe) ? recipe.ResultAmount : 1;
    }
}
