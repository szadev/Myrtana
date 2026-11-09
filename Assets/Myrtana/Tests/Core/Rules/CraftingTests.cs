using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;
using Myrtana.Import.G3;
using Myrtana.Rules;
using Myrtana.Session;
using Myrtana.Story;
using NUnit.Framework;

namespace Myrtana.Tests
{
    public sealed class CraftingTests
    {
        const string Sword = "It_Longsword", Steel = "It_Steel", Coal = "It_Coal", Hammer = "It_Hammer_Smith",
                     SwordRecipe = "It_Recipe_Longsword", PotionRecipe = "It_Recipe_Health_1", Herb = "It_Herb", Potion = "It_Potion_Health_01";

        static ItemDef Item(string id, ItemCategory category, string damage = null) =>
            new ItemDef(id, new TextId("FO_" + id), default, category, "None", EquipSlot.None, 0, 0, null, null,
                        null, null, null, null, null, damage == null ? null : new ItemDamage(damage, 50, default), ItemFlags.None);

        static SessionFixture Fixture() => new SessionFixture()
            .Add(RulesSchema.ItemFolder, Item(Sword, ItemCategory.Weapon, "Blade"), Item("It_Club", ItemCategory.Weapon, "Impact"),
                 Item(Steel, ItemCategory.Misc), Item(Coal, ItemCategory.Misc), Item(Hammer, ItemCategory.Misc), Item(Herb, ItemCategory.Alchemy),
                 Item(Potion, ItemCategory.Alchemy), Item(SwordRecipe, ItemCategory.Written), Item(PotionRecipe, ItemCategory.Written),
                 Item("It_Meat_Raw", ItemCategory.Alchemy), Item("It_Meat_Fried", ItemCategory.Alchemy), Item("It_Letter_Test", ItemCategory.Written))
            .Add(RulesSchema.RecipeFolder,
                 new RecipeDef(SwordRecipe, CraftKind.Smithing, new[] { new RecipeIngredient(Steel, 1), new RecipeIngredient(Coal, 5) }, Sword, 1, 0,
                               "Perk_Smith_1"),
                 new RecipeDef(PotionRecipe, CraftKind.Alchemy, new[] { new RecipeIngredient(Herb, 2) }, Potion, 2, 0, null))
            .Add(RulesSchema.DocumentFolder, new DocumentDef("It_Letter_Test", DocumentKind.Letter, new TextId("LETTERHEADER_X"), new TextId("LETTERTEXT_X"), default))
            .Add(RulesSchema.CraftingRulesFolder, G3CraftingImporter.Rules())
            .Add(RulesSchema.InteractiveObjectFolder,
                 Stand("wisdom1", "BS_AncientWisdom"), Stand("wisdom2", "BS_AncientWisdom"), Stand("alchemy", "BS_Alchemy"),
                 Stand("event", "Ritual_Done #comment"), Stand("chronicles", "BS_Chronicles"), Stand("empty", null))
            .Quest("PC_Hero_FindChumanas").Quest("PC_Hero_FifthTemple");

        static InteractiveObjectDef Stand(string id, string useFunc) =>
            new InteractiveObjectDef(id, "G3_Object_Interact_Bookstand_01", InteractiveKind.Station, "Bookstand", "stand.xcmsh", null, null, null, 1,
                                     default, null, null, null, null, useFunc);

        static (GameSession session, CraftingServices crafting) Start(SessionFixture f)
        {
            var s = f.NewGame();
            return (s, s.Crafting);
        }

        [Test]
        public void Anvil_NeedsARecipeThenTheHammer()
        {
            using var f = Fixture();
            var (s, c) = Start(f);
            var hero = s.Items.Inventories;
            Assert.That(c.Crafting.Check("Anvil").Verdict, Is.EqualTo(StationVerdict.NoRecipes));
            hero.Give(ActorId.Player, SwordRecipe, 1);
            var check = c.Crafting.Check("Anvil");
            Assert.That(check.Verdict, Is.EqualTo(StationVerdict.MissingTool));
            Assert.That(check.Item, Is.EqualTo(Hammer));
            hero.Give(ActorId.Player, Hammer, 1);
            Assert.That(c.Crafting.Check("Anvil").Opens, Is.True);
            Assert.That(c.Crafting.Check("Alchemy").Verdict, Is.EqualTo(StationVerdict.NoRecipes));
            Assert.That(c.Crafting.Check("Forge").Verdict, Is.EqualTo(StationVerdict.NotUsable));
            Assert.That(c.Crafting.Check("Barbecue").Opens, Is.True, "motion only");
        }

        [Test]
        public void Smithing_NeedsThePerk_UsesIngredients_AndForges()
        {
            using var f = Fixture();
            var (s, c) = Start(f);
            var inv = s.Items.Inventories.Of(ActorId.Player);
            s.Items.Inventories.Give(ActorId.Player, SwordRecipe, 1);
            s.Items.Inventories.Give(ActorId.Player, Steel, 3);
            s.Items.Inventories.Give(ActorId.Player, Coal, 12);
            var recipe = c.Crafting.Recipes(CraftKind.Smithing).Single();
            Assert.That(c.Crafting.MaxCount(recipe), Is.EqualTo(-1), "no perk");
            Assert.That(c.Crafting.Craft(SwordRecipe, 1, out _), Is.EqualTo(CraftVerdict.MissingSkill));
            s.Progression.LearnSkill("Perk_Smith_1");
            Assert.That(c.Crafting.MaxCount(recipe), Is.EqualTo(2), "12 coal / 5");
            var crafted = new List<ItemCrafted>();
            s.Events.Subscribe<ItemCrafted>(crafted.Add);
            Assert.That(c.Crafting.Craft(SwordRecipe, 5, out int made), Is.EqualTo(CraftVerdict.Done));
            Assert.That(made, Is.EqualTo(2));
            Assert.That(inv.Count(Steel), Is.EqualTo(1));
            Assert.That(inv.Count(Coal), Is.EqualTo(2));
            Assert.That(inv.Count(SwordRecipe), Is.EqualTo(1), "recipes are kept");
            Assert.That(inv.Stacks.Single(st => st.Item == Sword).Quality & (int)WeaponQuality.Forged, Is.Not.Zero);
            Assert.That(crafted.Single().Amount, Is.EqualTo(2));
            Assert.That(c.Crafting.Craft(SwordRecipe, 1, out _), Is.EqualTo(CraftVerdict.MissingIngredients));
        }

        [Test]
        public void Alchemy_MakesResultAmountPerCount()
        {
            using var f = Fixture();
            var (s, c) = Start(f);
            s.Items.Inventories.Give(ActorId.Player, PotionRecipe, 1);
            s.Items.Inventories.Give(ActorId.Player, Herb, 5);
            Assert.That(c.Crafting.Craft(PotionRecipe, 2, out int made), Is.EqualTo(CraftVerdict.Done));
            var inv = s.Items.Inventories.Of(ActorId.Player);
            Assert.That(made, Is.EqualTo(4));
            Assert.That(inv.Count(Potion), Is.EqualTo(4));
            Assert.That(inv.Stacks.Single(st => st.Item == Potion).Quality, Is.Zero, "only smithed weapons are forged");
            Assert.That(inv.Count(Herb), Is.EqualTo(1));
            Assert.That(c.Documents(DocumentTab.AlchemyRecipes), Is.EqualTo(new[] { PotionRecipe }));
        }

        [Test]
        public void GrindStone_SharpensBladesOnce()
        {
            using var f = Fixture();
            var (s, c) = Start(f);
            var inv = s.Items.Inventories.Of(ActorId.Player);
            s.Items.Inventories.Give(ActorId.Player, "It_Club", 1);
            Assert.That(c.Crafting.Check("GrindStone").Verdict, Is.EqualTo(StationVerdict.MissingSkill));
            s.Progression.LearnSkill("Perk_SharpenBlade");
            Assert.That(c.Crafting.Check("GrindStone").Verdict, Is.EqualTo(StationVerdict.NoBladeWeapon));
            s.Items.Inventories.Give(ActorId.Player, Sword, 2);
            Assert.That(c.Crafting.Check("GrindStone").Opens, Is.True);
            var stack = c.Crafting.SharpenCandidates().Single();
            Assert.That(c.Crafting.Sharpen(stack), Is.EqualTo(TransformVerdict.Done));
            var sharp = inv.Stacks.Single(st => st.Item == Sword && (st.Quality & (int)WeaponQuality.Sharp) != 0);
            Assert.That(sharp.Amount, Is.EqualTo(1), "G3 sharpens one item, not the stack");
            Assert.That(inv.Stacks.Single(st => st.Item == Sword && st.Quality == stack.Quality).Amount, Is.EqualTo(1));
            Assert.That(c.Crafting.Sharpen(sharp), Is.EqualTo(TransformVerdict.AlreadySharp));
            inv.Add(Sword, 1, (int)WeaponQuality.Burning);
            var burning = inv.Stacks.Single(st => st.Item == Sword && st.Quality == (int)WeaponQuality.Burning);
            Assert.That(c.Crafting.Sharpen(burning), Is.EqualTo(TransformVerdict.AlreadyMagic));
        }

        [Test]
        public void Campfire_FriesMeatOneForOne()
        {
            using var f = Fixture();
            var (s, c) = Start(f);
            s.Items.Inventories.Give(ActorId.Player, "It_Meat_Raw", 3);
            Assert.That(c.Crafting.FryCandidates().Single().Item, Is.EqualTo("It_Meat_Raw"));
            Assert.That(c.Crafting.Fry("It_Meat_Raw", 2, out int fried), Is.EqualTo(TransformVerdict.Done));
            var inv = s.Items.Inventories.Of(ActorId.Player);
            Assert.That(fried, Is.EqualTo(2));
            Assert.That(inv.Count("It_Meat_Raw"), Is.EqualTo(1));
            Assert.That(inv.Count("It_Meat_Fried"), Is.EqualTo(2));
            Assert.That(c.Crafting.Fry(Sword, 1, out _), Is.EqualTo(TransformVerdict.NotPossible));
        }

        [Test]
        public void Bookstands_ReadBooksInOrder_TeachOnce_AndSave()
        {
            using var f = Fixture();
            var (s, c) = Start(f);
            int intel = s.Hero.Stats.Value("INT"), alc = s.Hero.Stats.Value("ALC");

            var first = c.Read("wisdom1");
            Assert.That(first.Lines, Is.EqualTo(new[] { "BookOfFlood_00", "BookOfFlood_01" }));
            Assert.That(s.Hero.Stats.Value("INT"), Is.EqualTo(intel + 3), "AB is on by default: 3 points");
            Assert.That(c.Read("wisdom1").NothingNew, Is.True, "used up");
            Assert.That(c.Read("wisdom2").Lines, Is.EqualTo(new[] { "BookOfFlood_02", "BookOfFlood_03" }));

            Assert.That(c.Read("alchemy").Lines, Is.EqualTo(new[] { "AlchemyBook" }));
            Assert.That(s.Hero.Stats.Value("ALC"), Is.EqualTo(alc + 3));

            Assert.That(c.Read("empty").NothingNew, Is.True);
            Assert.That(c.Read("event").NothingNew, Is.True);
            Assert.That(s.Flags.IsSet("Ritual_Done"), Is.True);

            Assert.That(c.Read("chronicles").Lines, Is.EqualTo(new[] { "NotReadyYet" }));
            s.Flags.Set("Dargoth_Chronicles");
            var chronicles = c.Read("chronicles");
            Assert.That(chronicles.Lines.Count, Is.EqualTo(4));
            Assert.That(chronicles.EnableSectors, Is.EqualTo(new[] { "G3_Nordmar_01_Monastery_Chronicles_Story_01" }));
            Assert.That(s.Quests.StatusOf(new Id<QuestDef>("PC_Hero_FindChumanas")), Is.EqualTo(QuestStatus.Running));

            var memory = f.Reload(s).Bookstands;
            Assert.That(memory.IsUsed("wisdom1") && memory.IsUsed("alchemy") && memory.IsUsed("chronicles"), Is.True);
            Assert.That(memory.PagesRead("BS_BookOfFlood"), Is.EqualTo(4));
        }

        [Test]
        public void Bookstand_RaiseComesAfterTheReading_AndIsPrintedOnce()
        {
            // fix-77: G3 BS_* raise the attribute (AddIntelligence → PrintAddValue) after the pages are read aloud
            using var f = Fixture();
            var (s, c) = Start(f);
            using var notices = new GameNotices(s);
            var posted = new List<(string text, NoticeKind kind)>();
            notices.Posted += (text, kind) => posted.Add((text, kind));
            int intel = s.Hero.Stats.Value("INT");
            string raised = $"{s.Localizer.Text(new TextId("ATTRIB_INT"))} + 3 = {intel + 3}";

            var reading = c.Read("wisdom1", deferGain: true);
            Assert.That(reading.Gain, Is.Not.Null);
            Assert.That(s.Hero.Stats.Value("INT"), Is.EqualTo(intel), "nothing yet while he reads aloud");
            Assert.That(posted, Is.Empty);

            c.Learn(reading);
            Assert.That(s.Hero.Stats.Value("INT"), Is.EqualTo(intel + 3));
            Assert.That(posted, Is.EqualTo(new[] { (raised, NoticeKind.Info) }), "'<long name> + n = value', type 15 gold");
            c.Learn(reading);
            Assert.That(s.Hero.Stats.Value("INT"), Is.EqualTo(intel + 3), "once");
            Assert.That(posted, Has.Count.EqualTo(1));

            Assert.That(c.Read("wisdom1").NothingNew, Is.True, "the stand is used up: no second bonus");
            Assert.That(s.Hero.Stats.Value("INT"), Is.EqualTo(intel + 3));
            Assert.That(f.Reload(s).Bookstands.IsUsed("wisdom1"), Is.True, "first read saved");

            int alc = s.Hero.Stats.Value("ALC");
            c.Read("alchemy"); // the dry-run path applies at once
            Assert.That(posted.Last().text, Is.EqualTo($"{s.Localizer.Text(new TextId("ATTRIB_ALC"))} + 3 = {alc + 3}"));
        }

        [Test]
        public void RecipeSheet_MarksMissingIngredientsAndSkill()
        {
            using var f = Fixture();
            var (s, c) = Start(f);
            s.Items.Inventories.Give(ActorId.Player, Steel, 1);
            s.Items.Inventories.Give(ActorId.Player, Coal, 2);
            c.TryGetRecipe(SwordRecipe, out var recipe);
            var sheet = RecipeSheet.Of(s, c.Crafting, recipe);
            Assert.That(sheet.Ingredients.Select(i => i.Carried), Is.EqualTo(new[] { true, false }));
            Assert.That(sheet.KnowsSkill, Is.False);
            Assert.That(sheet.MaxCount, Is.EqualTo(-1));
            Assert.That(DocumentSheet.Clean(@"a\nb"), Is.EqualTo("a\nb"));
        }

        [Test]
        public void RecipeRows_MarkWhatTheHeroCanMakeNow()
        {
            using var f = Fixture();
            var (s, c) = Start(f);
            var hero = s.Items.Inventories;
            hero.Give(ActorId.Player, PotionRecipe, 1);
            hero.Give(ActorId.Player, SwordRecipe, 1);
            hero.Give(ActorId.Player, Steel, 3);
            hero.Give(ActorId.Player, Coal, 10);
            var alchemy = RecipeRow.Of(s, c.Crafting, CraftKind.Alchemy);
            Assert.That(alchemy.Select(r => r.Recipe.Id), Is.EqualTo(new[] { PotionRecipe }));
            Assert.That(alchemy[0].Craftable, Is.False, "no herbs");
            Assert.That(alchemy[0].MaxCount, Is.EqualTo(0));
            Assert.That(alchemy[0].CountText, Is.Empty);
            hero.Give(ActorId.Player, Herb, 7);
            alchemy = RecipeRow.Of(s, c.Crafting, CraftKind.Alchemy);
            Assert.That(alchemy[0].Craftable, Is.True);
            Assert.That(alchemy[0].CountText, Is.EqualTo("×3"));
            var smithing = RecipeRow.Of(s, c.Crafting, CraftKind.Smithing);
            Assert.That(smithing[0].Craftable, Is.False, "ingredients carried but Perk_Smith_1 missing");
            Assert.That(smithing[0].MaxCount, Is.EqualTo(-1));
        }

        [Test]
        public void Documents_ListCarriedLettersAndRecipes()
        {
            using var f = Fixture();
            var (s, c) = Start(f);
            Assert.That(c.Documents(DocumentTab.Books), Is.Empty);
            s.Items.Inventories.Give(ActorId.Player, "It_Letter_Test", 1);
            s.Items.Inventories.Give(ActorId.Player, SwordRecipe, 1);
            Assert.That(c.Documents(DocumentTab.Books), Is.EqualTo(new[] { "It_Letter_Test" }));
            Assert.That(c.Documents(DocumentTab.SmithingRecipes), Is.EqualTo(new[] { SwordRecipe }));
            Assert.That(c.TryGetDocument("It_Letter_Test", out var doc) && doc.Kind == DocumentKind.Letter, Is.True);
        }
    }
}
