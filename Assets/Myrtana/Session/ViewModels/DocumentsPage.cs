using System;
using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;
using Myrtana.Rules;

namespace Myrtana.Session
{
    /// <summary>What the reading area of the documents page shows (G3 <c>gCHUDViewDocument2::SetStack</c>).</summary>
    public enum DocumentView { None, Map, Letter, Book, Recipe }

    /// <summary>One row of the documents list: item icon + name (theme <c>listctrl_item_icons_*</c>, 238×46).</summary>
    public readonly struct DocumentRow
    {
        public DocumentRow(string item, string name, string icon)
        {
            Item = item;
            Name = name;
            Icon = icon;
        }

        public string Item { get; }
        public string Name { get; }
        /// <summary>The item's GUI icon (null = none).</summary>
        public string Icon { get; }
    }

    /// <summary>
    /// The documents page (<c>IDD_PAGE_DOCUMENTS_2</c>, Docs/interactive-objects.md §6.1, Docs/map-teleport.md §1.2) of one
    /// tab: the header, the list of the hero's carried documents of the tab and what the reading area shows. G3 CP 1.75
    /// (Game.dll <c>gCHUDViewDocuments2</c>, <c>gCHUDPageDocuments2</c>): 5 icon tabs, header = the tab's <c>HL_*</c> string
    /// [V 10200120, 10200180, 100a85d0]; the list is a filter over the inventory (nothing is remembered: no read marks, no
    /// learned recipes, no bookstand texts); the reading area is the map, letter, book or recipe view of the selected item,
    /// or nothing (the empty <c>G3_HUD_View_Document_Back</c> panel; no empty-list text exists). Books have one left and
    /// one right page (no page turning); the texts are plain statics (no scrolling).
    /// </summary>
    public sealed class DocumentsPage
    {
        /// <summary>The tab headers in G3's order (Game.dll table 0x103f274c).</summary>
        public static readonly IReadOnlyList<TextId> TabTitles = new[]
        {
            new TextId("HL_Maps"), new TextId("HL_Books"), new TextId("HL_Recipes_ALC"), new TextId("HL_Recipes_Cooking"),
            new TextId("HL_Recipes_SMT"),
        };

        /// <summary>The tab icons in G3's order (Game.dll table 0x103f26f4).</summary>
        public static readonly IReadOnlyList<string> TabIcons = new[]
        {
            "G3_HUD_Icon_Document_Map.tga", "G3_HUD_Icon_Document_Book.tga", "G3_HUD_Icon_Document_Recipe_Alchemy.tga",
            "G3_HUD_Icon_Document_Recipe_Cooking.tga", "G3_HUD_Icon_Document_Recipe_Smithing.tga",
        };

        DocumentsPage(DocumentTab tab) => Tab = tab;

        public DocumentTab Tab { get; }
        /// <summary>Localized header of the tab.</summary>
        public string Title { get; private set; }
        public IReadOnlyList<DocumentRow> Rows { get; private set; } = Array.Empty<DocumentRow>();
        /// <summary>Index of the selected row (−1 = none).</summary>
        public int Selected { get; private set; } = -1;
        public string SelectedItem => Selected >= 0 && Selected < Rows.Count ? Rows[Selected].Item : null;
        public DocumentView View { get; private set; }
        /// <summary>The letter or book shown (null otherwise).</summary>
        public DocumentSheet Document { get; private set; }
        /// <summary>The recipe shown (null otherwise).</summary>
        public RecipeSheet Recipe { get; private set; }
        /// <summary>The map shown on the Maps tab (null = the map view stays hidden).</summary>
        public MapDef Map { get; private set; }

        /// <summary>
        /// The page on <paramref name="tab"/>. <paramref name="picked"/> is the item selected in the list (kept while the hero
        /// still carries it). On the Maps tab the shown map is G3's choice (<see cref="WorldMaps.Choose"/>, at the hero's
        /// x/z) and only a picked map is selected; on the other tabs the picked item, else the first row, is selected and read.
        /// </summary>
        public static DocumentsPage Of(GameSession session, DocumentTab tab, string picked = null, float heroX = 0f, float heroZ = 0f)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            var page = new DocumentsPage(tab) { Title = session.Localizer.Text(TabTitles[(int)tab]) };
            var items = session.Crafting.Documents(tab);
            page.Rows = items.Select(i => Row(session, i)).ToList();
            int index = picked == null ? -1 : IndexOf(items, picked);
            if (tab == DocumentTab.Maps)
            {
                page.Selected = index;
                page.Map = session.Maps.Choose(index >= 0 ? picked : null, heroX, heroZ);
                page.View = page.Map != null ? DocumentView.Map : DocumentView.None;
                return page;
            }
            page.Selected = index >= 0 ? index : items.Count > 0 ? 0 : -1;
            page.Read(session);
            return page;
        }

        void Read(GameSession session)
        {
            string item = SelectedItem;
            if (item == null) return;
            var crafting = session.Crafting;
            if (crafting.TryGetRecipe(item, out var recipe))
            {
                View = DocumentView.Recipe;
                Recipe = RecipeSheet.Of(session, crafting.Crafting, recipe);
            }
            else if (crafting.TryGetDocument(item, out var document))
            {
                View = document.Kind == DocumentKind.Book ? DocumentView.Book : DocumentView.Letter;
                Document = DocumentSheet.Of(session, document);
            }
        }

        static int IndexOf(IReadOnlyList<string> items, string item)
        {
            for (int i = 0; i < items.Count; i++)
                if (items[i] == item) return i;
            return -1;
        }

        static DocumentRow Row(GameSession session, string item) =>
            session.Content.Database.TryGet(item, out ItemDef def)
                ? new DocumentRow(item, ItemLists.Name(session, def), string.IsNullOrEmpty(def.Icon) ? null : def.Icon)
                : new DocumentRow(item, item, null);

        /// <summary>Words of the dev op <c>docs</c> (Docs/dev-scenarios.md).</summary>
        public static readonly IReadOnlyList<string> GiveKinds = new[] { "all", "maps", "books", "letters", "recipes", "alchemy", "cooking", "smithing" };

        /// <summary>
        /// Every readable item of the content of one kind (<see cref="GiveKinds"/>; for the dev op <c>docs</c>), in id
        /// order; null for an unknown kind.
        /// </summary>
        public static IReadOnlyList<string> AllOfKind(GameSession session, string kind)
        {
            var db = session.Content.Database;
            IEnumerable<string> maps = db.All<MapDef>().Select(m => m.Id);
            IEnumerable<string> Documents(DocumentKind? k) =>
                db.All<DocumentDef>().Where(d => (k == null || d.Kind == k) && !session.Maps.IsMap(d.Id)).Select(d => d.Id);
            IEnumerable<string> Recipes(CraftKind? k) => db.All<RecipeDef>().Where(r => k == null || r.Craft == k).Select(r => r.Id);
            IEnumerable<string> ids;
            switch ((kind ?? "all").ToLowerInvariant())
            {
                case "all": ids = maps.Concat(Documents(null)).Concat(Recipes(null)); break;
                case "maps": ids = maps; break;
                case "books": ids = Documents(DocumentKind.Book); break;
                case "letters": ids = Documents(DocumentKind.Letter); break;
                case "recipes": ids = Recipes(null); break;
                case "alchemy": ids = Recipes(CraftKind.Alchemy); break;
                case "cooking": ids = Recipes(CraftKind.Cooking); break;
                case "smithing": ids = Recipes(CraftKind.Smithing); break;
                default: return null;
            }
            return ids.Where(id => db.TryGet(id, out ItemDef _)).Distinct().OrderBy(id => id, StringComparer.Ordinal).ToList();
        }

        /// <summary>The documents tab named by a word (maps, books, alchemy, cooking, smithing; also letters, recipes); null = unknown.</summary>
        public static DocumentTab? ParseTab(string word)
        {
            switch ((word ?? "").ToLowerInvariant())
            {
                case "maps": case "map": return DocumentTab.Maps;
                case "books": case "book": case "letters": case "letter": return DocumentTab.Books;
                case "alchemy": case "recipes": return DocumentTab.AlchemyRecipes;
                case "cooking": return DocumentTab.CookingRecipes;
                case "smithing": return DocumentTab.SmithingRecipes;
                default: return null;
            }
        }
    }
}
