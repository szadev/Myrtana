using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Myrtana.Core;

namespace Myrtana.Rules
{
    /// <summary>The craft a recipe belongs to: the station that uses it and the documents tab that lists it.</summary>
    public enum CraftKind { Smithing, Alchemy, Cooking }

    /// <summary>
    /// A recipe (G3 <c>gCRecipe_PS</c> on a Written item). Carrying the recipe item is knowing the recipe: the station
    /// lists every carried recipe of its craft, and crafting turns <see cref="Ingredients"/> × n into
    /// <see cref="Result"/> × (<see cref="ResultAmount"/> × n). <see cref="RequiredSkill"/> gates it. The id is the
    /// recipe item's id.
    /// </summary>
    public sealed class RecipeDef : IDefinition
    {
        [JsonConstructor]
        public RecipeDef(string id, CraftKind craft, IReadOnlyList<RecipeIngredient> ingredients, string result, int resultAmount,
                         int resultQuality, string requiredSkill)
        {
            Id = id;
            Craft = craft;
            Ingredients = ingredients ?? Array.Empty<RecipeIngredient>();
            Result = result;
            ResultAmount = resultAmount < 1 ? 1 : resultAmount;
            ResultQuality = resultQuality;
            RequiredSkill = requiredSkill;
        }

        public string Id { get; }
        public CraftKind Craft { get; }
        public IReadOnlyList<RecipeIngredient> Ingredients { get; }
        public string Result { get; }
        public int ResultAmount { get; }
        /// <summary>Quality bits added to the result (on top of the result item's own and the forged bit).</summary>
        public int ResultQuality { get; }
        /// <summary>Null = no skill needed.</summary>
        public string RequiredSkill { get; }

        string IDefinition.Key => Id;
    }

    public sealed class RecipeIngredient
    {
        [JsonConstructor]
        public RecipeIngredient(string item, int amount)
        {
            Item = item;
            Amount = amount < 1 ? 1 : amount;
        }

        public string Item { get; }
        public int Amount { get; }
    }

    /// <summary>How a written item is shown on the documents page.</summary>
    public enum DocumentKind { Letter, Book }

    /// <summary>
    /// The text of a letter or book item (G3 <c>gCLetter_PS</c> Header/Text, <c>gCBook_PS</c> Header/LeftText/RightText).
    /// The id is the item's id. Reading neither uses up the item nor remembers it: the documents page lists what the
    /// hero carries.
    /// </summary>
    public sealed class DocumentDef : IDefinition
    {
        [JsonConstructor]
        public DocumentDef(string id, DocumentKind kind, TextId header, TextId text, TextId rightText)
        {
            Id = id;
            Kind = kind;
            Header = header;
            Text = text;
            RightText = rightText;
        }

        public string Id { get; }
        public DocumentKind Kind { get; }
        public TextId Header { get; }
        /// <summary>The letter's text, or the left page of a book.</summary>
        public TextId Text { get; }
        /// <summary>The right page of a book; empty for letters.</summary>
        public TextId RightText { get; }

        string IDefinition.Key => Id;
    }
}
