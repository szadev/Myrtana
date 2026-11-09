using System;
using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;

namespace Myrtana.Rules
{
    /// <summary>Why a station does not open its page (each has G3's HUD message; see the presentation).</summary>
    public enum StationVerdict
    {
        Open,
        /// <summary>The player cannot use this station (forge, bookshelf: NPC routine stations; any use type without a rule).</summary>
        NotUsable,
        /// <summary>No recipe of the station's craft is carried (<c>HL_Recipes_*</c> + "missing").</summary>
        NoRecipes,
        /// <summary>The tool is not carried (<see cref="StationCheck.Item"/>).</summary>
        MissingTool,
        /// <summary>The skill is not known (<see cref="StationCheck.Item"/>).</summary>
        MissingSkill,
        /// <summary>No blade weapon is carried (<c>HUD_BladeWeapon</c> + "missing").</summary>
        NoBladeWeapon,
    }

    public readonly struct StationCheck
    {
        public StationCheck(StationVerdict verdict, StationRule rule, string item = null)
        {
            Verdict = verdict;
            Rule = rule;
            Item = item;
        }

        public StationVerdict Verdict { get; }
        /// <summary>Null when the use type has no rule.</summary>
        public StationRule Rule { get; }
        /// <summary>The missing tool or skill.</summary>
        public string Item { get; }
        public bool Opens => Verdict == StationVerdict.Open;
    }

    public enum CraftVerdict { Done, MissingIngredients, MissingSkill, UnknownRecipe }

    /// <summary>Why a stack cannot be sharpened or fried.</summary>
    public enum TransformVerdict { Done, NotPossible, MissingSkill, AlreadySharp, AlreadyMagic }

    /// <summary>Published when the hero crafted something at a station.</summary>
    public readonly struct ItemCrafted : IGameEvent
    {
        public ItemCrafted(string recipe, string item, int amount)
        {
            Recipe = recipe;
            Item = item;
            Amount = amount;
        }

        public string Recipe { get; }
        public string Item { get; }
        public int Amount { get; }
    }

    /// <summary>Published when a stack was sharpened or fried.</summary>
    public readonly struct ItemTransformed : IGameEvent
    {
        public ItemTransformed(string from, string to, int amount, int quality)
        {
            From = from;
            To = to;
            Amount = amount;
            Quality = quality;
        }

        public string From { get; }
        public string To { get; }
        public int Amount { get; }
        public int Quality { get; }
    }

    /// <summary>
    /// The hero's crafting (G3 <c>PS_Interact_*</c> station checks, <c>OnPlayerSynthRecipe</c>, <c>PSInventory::Synthesize</c>,
    /// <c>OnPlayerTransformStack</c>, <c>OnPlayerFryStack</c>). Recipes are known by carrying them; a recipe whose skill the
    /// hero lacks can be read but not made. Crafting takes no game time and gives no experience.
    /// </summary>
    public sealed class CraftingService
    {
        readonly CraftingRules _rules;
        readonly IContentDatabase _content;
        readonly Inventory _hero;
        readonly Func<string, bool> _knows;
        readonly IEventBus _events;

        public CraftingService(CraftingRules rules, IContentDatabase content, Inventory hero, Func<string, bool> heroKnows, IEventBus events)
        {
            _rules = rules ?? CraftingRules.None;
            _content = content;
            _hero = hero;
            _knows = heroKnows;
            _events = events;
        }

        public CraftingRules Rules => _rules;

        /// <summary>Can the hero open the station of this use type now?</summary>
        public StationCheck Check(string useType)
        {
            var rule = _rules.StationFor(useType);
            if (rule == null) return new StationCheck(StationVerdict.NotUsable, null);
            if (rule.Page == StationPage.Synth && Recipes(rule.Craft).Count == 0) return new StationCheck(StationVerdict.NoRecipes, rule);
            if (!string.IsNullOrEmpty(rule.Tool) && !_hero.Has(rule.Tool)) return new StationCheck(StationVerdict.MissingTool, rule, rule.Tool);
            if (!string.IsNullOrEmpty(rule.RequiredSkill) && !_knows(rule.RequiredSkill))
                return new StationCheck(StationVerdict.MissingSkill, rule, rule.RequiredSkill);
            if (rule.NeedsBladeWeapon && SharpenCandidates().Count == 0) return new StationCheck(StationVerdict.NoBladeWeapon, rule);
            return new StationCheck(StationVerdict.Open, rule);
        }

        /// <summary>The recipes of a craft the hero carries, in inventory order (G3's synth list).</summary>
        public IReadOnlyList<RecipeDef> Recipes(CraftKind craft)
        {
            var list = new List<RecipeDef>();
            foreach (var stack in _hero.Stacks)
                if (_content.TryGet(stack.Item, out RecipeDef recipe) && recipe.Craft == craft && !list.Contains(recipe)) list.Add(recipe);
            return list;
        }

        /// <summary>
        /// How many times the recipe can be made from what the hero carries: −1 when he lacks the skill, 0 when an
        /// ingredient is missing (G3 <c>gCHUDTableRecipeStats</c>).
        /// </summary>
        public int MaxCount(RecipeDef recipe)
        {
            if (recipe == null) return 0;
            if (!string.IsNullOrEmpty(recipe.RequiredSkill) && !_knows(recipe.RequiredSkill)) return -1;
            int max = int.MaxValue;
            foreach (var ingredient in recipe.Ingredients) max = Math.Min(max, _hero.Count(ingredient.Item) / ingredient.Amount);
            return max == int.MaxValue ? 0 : max;
        }

        /// <summary>Makes the recipe <paramref name="count"/> times (clamped to <see cref="MaxCount"/>).</summary>
        public CraftVerdict Craft(string recipeId, int count, out int made)
        {
            made = 0;
            if (!_content.TryGet(recipeId, out RecipeDef recipe) || !_hero.Has(recipeId)) return CraftVerdict.UnknownRecipe;
            int max = MaxCount(recipe);
            if (max < 0) return CraftVerdict.MissingSkill;
            count = Math.Min(count, max);
            if (count <= 0) return CraftVerdict.MissingIngredients;
            foreach (var ingredient in recipe.Ingredients) _hero.Remove(ingredient.Item, ingredient.Amount * count);
            made = recipe.ResultAmount * count;
            _hero.Add(recipe.Result, made, ResultQuality(recipe));
            _events?.Publish(new ItemCrafted(recipe.Id, recipe.Result, made));
            return CraftVerdict.Done;
        }

        /// <summary>The quality bits of a crafted item: the recipe's, plus forged for smithed weapons.</summary>
        public int ResultQuality(RecipeDef recipe)
        {
            int quality = recipe.ResultQuality;
            if (recipe.Craft == CraftKind.Smithing && _content.TryGet(recipe.Result, out ItemDef item) && item.Category == ItemCategory.Weapon)
                quality |= _rules.ForgedQuality;
            return quality;
        }

        /// <summary>The hero's blade weapon stacks (the sharpen list).</summary>
        public IReadOnlyList<ItemStack> SharpenCandidates() =>
            _hero.Stacks.Where(s => _content.TryGet(s.Item, out ItemDef def) && IsBlade(def)).ToList();

        bool IsBlade(ItemDef def) =>
            def.Category == ItemCategory.Weapon && def.Damage != null && _rules.BladeDamageTypes.Contains(def.Damage.Type);

        public TransformVerdict CheckSharpen(ItemStack stack)
        {
            if (stack == null || !_content.TryGet(stack.Item, out ItemDef def) || !IsBlade(def)) return TransformVerdict.NotPossible;
            var station = _rules.Stations.FirstOrDefault(s => s.Page == StationPage.Sharpen);
            if (!string.IsNullOrEmpty(station?.RequiredSkill) && !_knows(station.RequiredSkill)) return TransformVerdict.MissingSkill;
            int quality = stack.Quality | def.Quality;
            if ((quality & _rules.SharpQuality) != 0) return TransformVerdict.AlreadySharp;
            if ((quality & _rules.MagicQualities) != 0) return TransformVerdict.AlreadyMagic;
            return TransformVerdict.Done;
        }

        /// <summary>
        /// Sharpens one weapon of the stack: it splits off with the sharp quality bit (CP 1.75 FUN_10068040 → ApplyQuality
        /// with count 1 for anything but ammunition, use type 7; blades are never ammunition).
        /// </summary>
        public TransformVerdict Sharpen(ItemStack stack)
        {
            var verdict = CheckSharpen(stack);
            if (verdict != TransformVerdict.Done) return verdict;
            int amount = 1, quality = stack.Quality | _rules.SharpQuality;
            _hero.RemoveExact(stack.Item, stack.Quality, amount);
            _hero.Add(stack.Item, amount, quality);
            _events?.Publish(new ItemTransformed(stack.Item, stack.Item, amount, quality));
            return TransformVerdict.Done;
        }

        /// <summary>The raw stacks the fry page lists.</summary>
        public IReadOnlyList<ItemStack> FryCandidates() => _hero.Stacks.Where(s => FriedOf(s.Item) != null).ToList();

        public string FriedOf(string raw) => _rules.Fry.FirstOrDefault(f => f.Raw == raw)?.Fried;

        /// <summary>Fries <paramref name="count"/> pieces of a raw item, one for one.</summary>
        public TransformVerdict Fry(string raw, int count, out int fried)
        {
            fried = 0;
            string result = FriedOf(raw);
            if (result == null || count <= 0) return TransformVerdict.NotPossible;
            fried = _hero.Remove(raw, count);
            if (fried == 0) return TransformVerdict.NotPossible;
            _hero.Add(result, fried);
            _events?.Publish(new ItemTransformed(raw, result, fried, 0));
            return TransformVerdict.Done;
        }
    }
}
