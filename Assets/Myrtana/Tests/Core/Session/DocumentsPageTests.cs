using System.Linq;
using Myrtana.Core;
using Myrtana.Import.G3;
using Myrtana.Rules;
using Myrtana.Session;
using Myrtana.Session.Dev;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>The documents page view model (fix-33, Docs/interactive-objects.md §6.1).</summary>
    public sealed class DocumentsPageTests
    {
        const string Letter = "It_Letter_Test", Book = "It_Book_Test", Map = "It_Map_World", SwordRecipe = "It_Recipe_Longsword",
                     PotionRecipe = "It_Recipe_Health_1", SoupRecipe = "It_Recipe_Soup";

        static ItemDef Item(string id, ItemCategory category, string icon = null) =>
            new ItemDef(id, new TextId("FO_" + id), default, category, "None", EquipSlot.None, 0, 0, icon, null,
                        null, null, null, null, null, null, ItemFlags.None);

        static SessionFixture Fixture() => new SessionFixture()
            .Add(RulesSchema.ItemFolder, Item(Letter, ItemCategory.Written, "IT_Letter"), Item(Book, ItemCategory.Written), Item(Map, ItemCategory.Written),
                 Item(SwordRecipe, ItemCategory.Written), Item(PotionRecipe, ItemCategory.Written), Item(SoupRecipe, ItemCategory.Written),
                 Item("It_Longsword", ItemCategory.Weapon), Item("It_Steel", ItemCategory.Misc), Item("It_Herb", ItemCategory.Alchemy),
                 Item("It_Potion", ItemCategory.Alchemy), Item("It_Soup", ItemCategory.Alchemy))
            .Add(RulesSchema.RecipeFolder,
                 new RecipeDef(SwordRecipe, CraftKind.Smithing, new[] { new RecipeIngredient("It_Steel", 1) }, "It_Longsword", 1, 0, "Perk_Smith_1"),
                 new RecipeDef(PotionRecipe, CraftKind.Alchemy, new[] { new RecipeIngredient("It_Herb", 2) }, "It_Potion", 1, 0, null),
                 new RecipeDef(SoupRecipe, CraftKind.Cooking, new[] { new RecipeIngredient("It_Herb", 1) }, "It_Soup", 1, 0, null))
            .Add(RulesSchema.DocumentFolder,
                 new DocumentDef(Letter, DocumentKind.Letter, new TextId("LETTERHEADER_X"), new TextId("LETTERTEXT_X"), default),
                 new DocumentDef(Book, DocumentKind.Book, new TextId("BOOKHEADER_X"), new TextId("BOOKLEFT_X"), new TextId("BOOKRIGHT_X")))
            .Add(RulesSchema.MapFolder, new MapDef(Map, "G3_Map_World", -2364f, 1711f, 1212f, -1865f))
            .Add(RulesSchema.CraftingRulesFolder, G3CraftingImporter.Rules());

        static void Give(GameSession s, params string[] items)
        {
            foreach (var item in items) s.Items.Inventories.Give(ActorId.Player, item, 1);
        }

        [Test]
        public void Tabs_FollowG3OrderWithTheirHeaders()
        {
            Assert.That(DocumentsPage.TabTitles.Select(t => t.Value),
                        Is.EqualTo(new[] { "HL_Maps", "HL_Books", "HL_Recipes_ALC", "HL_Recipes_Cooking", "HL_Recipes_SMT" }));
            Assert.That(DocumentsPage.TabIcons.Count, Is.EqualTo(5));
            Assert.That(DocumentsPage.TabIcons[0], Is.EqualTo("G3_HUD_Icon_Document_Map.tga"));
            using var f = Fixture();
            var s = f.NewGame();
            Assert.That(DocumentsPage.Of(s, DocumentTab.Books).Title, Is.EqualTo(s.Localizer.Text(new TextId("HL_Books"))));
        }

        [Test]
        public void EmptyInventory_ShowsEmptyListsAndNoView()
        {
            using var f = Fixture();
            var s = f.NewGame();
            foreach (DocumentTab tab in System.Enum.GetValues(typeof(DocumentTab)))
            {
                var page = DocumentsPage.Of(s, tab);
                Assert.That(page.Rows, Is.Empty, tab.ToString());
                Assert.That(page.Selected, Is.EqualTo(-1));
                Assert.That(page.View, Is.EqualTo(DocumentView.None));
                Assert.That(page.Document, Is.Null);
                Assert.That(page.Recipe, Is.Null);
            }
        }

        [Test]
        public void BooksTab_ListsLettersAndBooksAndReadsTheFirst()
        {
            using var f = Fixture();
            var s = f.NewGame();
            Give(s, Letter, Book, Map, SwordRecipe);
            var page = DocumentsPage.Of(s, DocumentTab.Books);
            Assert.That(page.Rows.Select(r => r.Item), Is.EqualTo(new[] { Letter, Book }), "maps and recipes are on their own tabs");
            Assert.That(page.Rows[0].Icon, Is.EqualTo("IT_Letter"));
            Assert.That(page.Rows[1].Icon, Is.Null);
            Assert.That(page.Selected, Is.EqualTo(0));
            Assert.That(page.View, Is.EqualTo(DocumentView.Letter));
            Assert.That(page.Document.Kind, Is.EqualTo(DocumentKind.Letter));

            var book = DocumentsPage.Of(s, DocumentTab.Books, Book);
            Assert.That(book.Selected, Is.EqualTo(1));
            Assert.That(book.View, Is.EqualTo(DocumentView.Book));
        }

        [Test]
        public void PickedItemGone_FallsBackToTheFirst()
        {
            using var f = Fixture();
            var s = f.NewGame();
            Give(s, Letter, Book);
            s.Items.Inventories.Of(ActorId.Player).Remove(Book, 1);
            var page = DocumentsPage.Of(s, DocumentTab.Books, Book);
            Assert.That(page.Rows.Select(r => r.Item), Is.EqualTo(new[] { Letter }));
            Assert.That(page.SelectedItem, Is.EqualTo(Letter));
        }

        [Test]
        public void RecipeTabs_ListTheCarriedRecipesOfTheirCraft()
        {
            using var f = Fixture();
            var s = f.NewGame();
            Give(s, SwordRecipe, PotionRecipe, SoupRecipe, Letter);
            Assert.That(DocumentsPage.Of(s, DocumentTab.AlchemyRecipes).Rows.Select(r => r.Item), Is.EqualTo(new[] { PotionRecipe }));
            Assert.That(DocumentsPage.Of(s, DocumentTab.CookingRecipes).Rows.Select(r => r.Item), Is.EqualTo(new[] { SoupRecipe }));
            var smith = DocumentsPage.Of(s, DocumentTab.SmithingRecipes);
            Assert.That(smith.Rows.Select(r => r.Item), Is.EqualTo(new[] { SwordRecipe }));
            Assert.That(smith.View, Is.EqualTo(DocumentView.Recipe));
            Assert.That(smith.Recipe, Is.Not.Null);
            Assert.That(smith.Recipe.KnowsSkill, Is.False);
        }

        [Test]
        public void PickingUpADocument_PutsItOnThePage()
        {
            using var f = Fixture();
            var s = f.NewGame();
            Assert.That(DocumentsPage.Of(s, DocumentTab.Books).Rows, Is.Empty);
            Give(s, Book);
            Assert.That(DocumentsPage.Of(s, DocumentTab.Books).Rows.Select(r => r.Item), Is.EqualTo(new[] { Book }));
        }

        [Test]
        public void MapsTab_ShowsG3sChoiceAndSelectsOnlyAPick()
        {
            using var f = Fixture();
            var s = f.NewGame();
            Give(s, Map);
            var inside = DocumentsPage.Of(s, DocumentTab.Maps, null, 0f, 0f);
            Assert.That(inside.Rows.Select(r => r.Item), Is.EqualTo(new[] { Map }));
            Assert.That(inside.Selected, Is.EqualTo(-1));
            Assert.That(inside.View, Is.EqualTo(DocumentView.Map));
            Assert.That(inside.Map.Id, Is.EqualTo(Map));
            var picked = DocumentsPage.Of(s, DocumentTab.Maps, Map, 0f, 0f);
            Assert.That(picked.Selected, Is.EqualTo(0));
        }

        [Test]
        public void AllOfKind_GivesTheDevOpItsItems()
        {
            using var f = Fixture();
            var s = f.NewGame();
            Assert.That(DocumentsPage.AllOfKind(s, "letters"), Is.EqualTo(new[] { Letter }));
            Assert.That(DocumentsPage.AllOfKind(s, "books"), Is.EqualTo(new[] { Book }));
            Assert.That(DocumentsPage.AllOfKind(s, "maps"), Is.EqualTo(new[] { Map }));
            Assert.That(DocumentsPage.AllOfKind(s, "recipes"), Is.EqualTo(new[] { PotionRecipe, SwordRecipe, SoupRecipe }.OrderBy(x => x, System.StringComparer.Ordinal)));
            Assert.That(DocumentsPage.AllOfKind(s, "all").Count, Is.EqualTo(6));
            Assert.That(DocumentsPage.AllOfKind(s, "nonsense"), Is.Null);
            Assert.That(DocumentsPage.ParseTab("smithing"), Is.EqualTo(DocumentTab.SmithingRecipes));
            Assert.That(DocumentsPage.ParseTab("letters"), Is.EqualTo(DocumentTab.Books));
        }

        [Test]
        public void DocsOp_GivesEveryDocumentOnce()
        {
            using var f = Fixture();
            var s = f.NewGame();
            Give(s, Letter);
            var run = new ChapterDryRun(s);
            Assert.That(run.Run(ScenarioParser.ParseInline("docs all\ndocs letters", "t")), Is.True, string.Join("; ", run.Failures));
            var hero = s.Items.Inventories.Of(ActorId.Player);
            Assert.That(hero.Count(Letter), Is.EqualTo(1));
            Assert.That(hero.Count(Book), Is.EqualTo(1));
            Assert.That(hero.Count(SoupRecipe), Is.EqualTo(1));
            Assert.That(DocumentsPage.Of(s, DocumentTab.Maps).Rows.Count, Is.EqualTo(1));
        }
    }
}
