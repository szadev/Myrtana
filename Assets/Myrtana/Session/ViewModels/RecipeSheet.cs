using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;
using Myrtana.Rules;

namespace Myrtana.Session
{
    /// <summary>One ingredient line of a recipe view.</summary>
    public readonly struct RecipeIngredientLine
    {
        public RecipeIngredientLine(string text, bool carried)
        {
            Text = text;
            Carried = carried;
        }

        public string Text { get; }
        /// <summary>The hero carries enough for one craft.</summary>
        public bool Carried { get; }
    }

    /// <summary>
    /// The recipe view (<c>IDD_VIEW_RECIPE_2</c>, documents page and stations): the result's name, icon, description and
    /// numbers, the ingredients and the required knowledge, with how many the hero can make now.
    /// </summary>
    public sealed class RecipeSheet
    {
        static readonly TextId IngredientsText = new TextId("HUD_Ingredients"), KnowledgeText = new TextId("HUD_Req_Knowledge");

        public string Title { get; private set; }
        public string Description { get; private set; }
        public string Icon { get; private set; }
        public ItemCategory Category { get; private set; }
        public IReadOnlyList<ItemLine> Stats { get; private set; }
        public string IngredientsLabel { get; private set; }
        public IReadOnlyList<RecipeIngredientLine> Ingredients { get; private set; }
        public string KnowledgeLabel { get; private set; }
        /// <summary>The required skill's name; empty when none.</summary>
        public string Knowledge { get; private set; }
        public bool KnowsSkill { get; private set; }
        /// <summary>−1 = skill missing, 0 = ingredients missing (G3's max craftable count).</summary>
        public int MaxCount { get; private set; }

        public static RecipeSheet Of(GameSession session, CraftingService crafting, RecipeDef recipe)
        {
            var text = session.Localizer;
            var detail = ItemLists.Detail(session, recipe.Result);
            var hero = session.Items.Inventories.Of(ActorId.Player);
            string skill = string.IsNullOrEmpty(recipe.RequiredSkill) ? string.Empty : SkillName(session, recipe.RequiredSkill);
            return new RecipeSheet
            {
                Title = detail.Name,
                Description = detail.Description,
                Icon = detail.Icon,
                Category = detail.Category,
                Stats = detail.Lines.Where(l => l.Kind != ItemLineKind.Gold).ToList(),
                IngredientsLabel = text.Text(IngredientsText),
                Ingredients = recipe.Ingredients
                    .Select(i => new RecipeIngredientLine($"{i.Amount} x {ItemName(session, i.Item)}", hero.Count(i.Item) >= i.Amount)).ToList(),
                KnowledgeLabel = text.Text(KnowledgeText),
                Knowledge = skill,
                KnowsSkill = string.IsNullOrEmpty(recipe.RequiredSkill) || session.Hero.Knows(recipe.RequiredSkill),
                MaxCount = crafting.MaxCount(recipe),
            };
        }

        /// <summary>The skill's name as G3 shows it: the perk item's focus name (<c>FO_It_&lt;perk&gt;</c>).</summary>
        public static string SkillName(GameSession session, string skill)
        {
            var db = session.Content.Database;
            if (db.TryGet(skill, out SkillDef def) && def.Item != null && db.TryGet(def.Item, out ItemDef item)) return ItemLists.Name(session, item);
            return session.Localizer.TryText(new TextId("FO_It_" + skill), out var name) && !string.IsNullOrEmpty(name) ? name : skill;
        }

        public static string ItemName(GameSession session, string item) =>
            session.Content.Database.TryGet(item, out ItemDef def) ? ItemLists.Name(session, def) : item;
    }

    /// <summary>
    /// One row of a station's recipe list (<c>IDD_PAGE_SYNTH_2</c>): the recipe item's icon and name, as G3, plus
    /// whether the hero can make it now. [owner] fix-125: G3 draws every carried recipe alike (gCHUDListCtrlInventory
    /// paints the stack name in the item colour, no availability check); the port dims the rows he cannot make (theme
    /// disabled text colour) and shows the craftable count right-aligned on the others. G3 order is kept.
    /// </summary>
    public readonly struct RecipeRow
    {
        public RecipeRow(RecipeDef recipe, string name, string icon, int maxCount)
        {
            Recipe = recipe;
            Name = name;
            Icon = icon;
            MaxCount = maxCount;
        }

        public RecipeDef Recipe { get; }
        public string Name { get; }
        public string Icon { get; }
        /// <summary>G3's max craftable count: −1 = skill missing, 0 = ingredients missing.</summary>
        public int MaxCount { get; }
        /// <summary>The hero has the skill and every ingredient for at least one craft.</summary>
        public bool Craftable => MaxCount >= 1;
        /// <summary>"×n" for a craftable row, empty otherwise.</summary>
        public string CountText => Craftable ? "×" + MaxCount : string.Empty;

        /// <summary>The carried recipes of a craft in G3's (inventory) order.</summary>
        public static IReadOnlyList<RecipeRow> Of(GameSession session, CraftingService crafting, CraftKind craft)
        {
            var db = session.Content.Database;
            return crafting.Recipes(craft)
                .Select(r => new RecipeRow(r, RecipeSheet.ItemName(session, r.Id),
                                           db.TryGet(r.Id, out ItemDef item) && !string.IsNullOrEmpty(item.Icon) ? item.Icon : null,
                                           crafting.MaxCount(r)))
                .ToList();
        }
    }

    /// <summary>A letter or book as the documents page shows it.</summary>
    public sealed class DocumentSheet
    {
        public DocumentKind Kind { get; private set; }
        public string Header { get; private set; }
        public string Text { get; private set; }
        public string RightText { get; private set; }

        public static DocumentSheet Of(GameSession session, DocumentDef document) => new DocumentSheet
        {
            Kind = document.Kind,
            Header = Clean(Localized(session, document.Header)),
            Text = Clean(Localized(session, document.Text)),
            RightText = Clean(Localized(session, document.RightText)),
        };

        static string Localized(GameSession session, TextId id) =>
            !id.IsEmpty && session.Localizer.TryText(id, out var s) ? s : string.Empty;

        /// <summary>G3 document strings write line breaks as the two characters "\n".</summary>
        public static string Clean(string text) => string.IsNullOrEmpty(text) ? string.Empty : text.Replace("\\n", "\n");
    }
}
