using System.Linq;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// par-magic: G3's spell stacks (gCInventory_PS::LearnSpell / HasLearnedSpell). Teaching a spell makes it castable and
    /// gives the hero its spell-book item; a found spell-book item of an unlearned spell cannot be readied or put on the
    /// quick bar; scrolls need no learning; all of it survives save and load; the spell book lists the spells by deity.
    /// </summary>
    public class HeroSpellsTests
    {
        const string Fireball = "Spl_Fireball", FireballBook = "It_Spell_Fireball", FireballScroll = "It_Scroll_Fireball";
        const string Light = "Spl_Light", LightBook = "It_Spell_Light";
        const string Icelance = "Spl_Icelance", IcelanceBook = "It_Spell_Icelance";
        const string Lightning = "Spl_Lightningbolt", LightningBook = "It_Spell_Lightningbolt";

        SessionFixture _f;

        static ItemDef Item(string id, ItemCategory category, string kind) =>
            new ItemDef(id, new TextId("FO_" + id), new TextId("ITEMDESC_" + id), category, kind, EquipSlot.None, 10, 0, "IT_" + id, null,
                        null, null, null, null, null, null, ItemFlags.None);

        static SpellDef Spell(string id, string book, string scroll, SpellDeity deity, int min, int max) =>
            new SpellDef(id, new[] { book }, scroll == null ? null : new[] { scroll }, SpellCastType.Power, SpellTarget.Focus,
                         SpellKind.Projectile, deity, min, max, SpellDelivery.Projectile, null, null, 0, null, null, null);

        static SkillDef Skill(string id, string item, int lp, int intelligence, string requiredSkill = null) =>
            new SkillDef(id, item, lp, new[] { new AttributeRequirement("INT", intelligence) }, requiredSkill, "Magic");

        [SetUp]
        public void SetUp() => _f = new SessionFixture()
            .Add(RulesSchema.ItemFolder,
                 Item(FireballBook, ItemCategory.Spellbook, "Cast"), Item(FireballScroll, ItemCategory.Artefact, "Cast"),
                 Item(LightBook, ItemCategory.Spellbook, "Cast"), Item(IcelanceBook, ItemCategory.Spellbook, "Cast"),
                 Item(LightningBook, ItemCategory.Spellbook, "Cast"))
            .Add(RulesSchema.SpellFolder,
                 Spell(Fireball, FireballBook, FireballScroll, SpellDeity.Innos, 15, 30), Spell(Light, LightBook, null, SpellDeity.Innos, 10, 10),
                 Spell(Icelance, IcelanceBook, null, SpellDeity.Adanos, 15, 30), Spell(Lightning, LightningBook, null, SpellDeity.Beliar, 15, 30))
            .Add(RulesSchema.SkillFolder,
                 Skill(Fireball, FireballBook, 10, 50), Skill(Light, LightBook, 5, 25, requiredSkill: null),
                 Skill(Icelance, IcelanceBook, 10, 50, requiredSkill: Fireball), Skill(Lightning, LightningBook, 10, 50));

        [TearDown]
        public void TearDown() => _f.Dispose();

        static Inventory HeroBag(GameSession s) => s.Items.Inventories.Of(ActorId.Player);

        static TeachResult Teach(GameSession s, string spell)
        {
            s.Hero.Stats.Apply("INT", ModifierOp.SetVal, 100);
            s.Progression.AddLearningPoints(20);
            return s.Items.Teaching.Teach(new Lesson(null, 0, spell));
        }

        [Test]
        public void Teaching_LearnsTheSpellAndGivesItsSpellBookItem()
        {
            var s = _f.NewGame();
            Assert.That(s.HeroSpells.CanReady(FireballBook), Is.False);
            Assert.That(Teach(s, Fireball), Is.EqualTo(TeachResult.Done));
            Assert.That(s.HeroSpells.IsLearned(Fireball), Is.True);
            Assert.That(HeroBag(s).Count(FireballBook), Is.EqualTo(1), "G3: the learned stack is in the inventory");
            Assert.That(s.HeroSpells.CanReady(FireballBook), Is.True);
            Assert.That(s.Items.QuickSlots.Assign(0, FireballBook), Is.True);
            Assert.That(Teach(s, Fireball), Is.EqualTo(TeachResult.AlreadyKnown));
            Assert.That(HeroBag(s).Count(FireballBook), Is.EqualTo(1));
        }

        [Test]
        public void Teaching_ASpellThatNeedsAnother_IsRefusedFirst()
        {
            var s = _f.NewGame();
            Assert.That(Teach(s, Icelance), Is.EqualTo(TeachResult.MissingRequirements));
            Assert.That(HeroBag(s).Has(IcelanceBook), Is.False);
            Teach(s, Fireball);
            Assert.That(Teach(s, Icelance), Is.EqualTo(TeachResult.Done));
        }

        [Test]
        public void FoundSpellBookItem_OfAnUnlearnedSpell_CannotBeReadiedOrSlotted()
        {
            var s = _f.NewGame();
            HeroBag(s).Add(LightningBook, 1); // a found It_Spell_* (G3: merges into the unlearned stack)
            Assert.That(s.HeroSpells.CanReady(LightningBook), Is.False);
            Assert.That(s.Items.QuickSlots.Assign(1, LightningBook), Is.False, "OnPlayerAssignStackKey: spells must be learned");
            s.Progression.LearnSkill(Lightning); // learned later: the same stack becomes castable, no second one
            Assert.That(s.HeroSpells.CanReady(LightningBook), Is.True);
            Assert.That(HeroBag(s).Count(LightningBook), Is.EqualTo(1));
            Assert.That(s.Items.QuickSlots.Assign(1, LightningBook), Is.True);
        }

        [Test]
        public void Scrolls_NeedNoLearning()
        {
            var s = _f.NewGame();
            HeroBag(s).Add(FireballScroll, 3);
            Assert.That(s.HeroSpells.SpellOf(FireballScroll), Is.Null);
            Assert.That(s.HeroSpells.CanReady(FireballScroll), Is.True);
            Assert.That(s.Items.QuickSlots.Assign(2, FireballScroll), Is.True);
        }

        [Test]
        public void SaveAndLoad_KeepTheLearnedSpellsTheirItemsAndQuickSlots()
        {
            var s = _f.NewGame();
            Teach(s, Fireball);
            s.Items.QuickSlots.Assign(3, FireballBook);
            HeroBag(s).Add(LightningBook, 1);
            var loaded = _f.Reload(s);
            Assert.That(loaded.HeroSpells.IsLearned(Fireball), Is.True);
            Assert.That(loaded.HeroSpells.IsLearned(Lightning), Is.False);
            Assert.That(HeroBag(loaded).Count(FireballBook), Is.EqualTo(1));
            Assert.That(HeroBag(loaded).Count(LightningBook), Is.EqualTo(1));
            Assert.That(loaded.Items.QuickSlots.ItemAt(3), Is.EqualTo(FireballBook));
            Assert.That(loaded.HeroSpells.CanReady(LightningBook), Is.False);
        }

        [Test]
        public void Load_OfASaveWithoutTheItem_GivesTheLearnedSpellsItsStack()
        {
            var s = _f.NewGame();
            Teach(s, Fireball);
            HeroBag(s).Remove(FireballBook, 1); // as a save from before the spell stacks were carried
            Assert.That(HeroBag(s).Has(FireballBook), Is.False);
            var loaded = _f.Reload(s);
            Assert.That(HeroBag(loaded).Count(FireballBook), Is.EqualTo(1));
        }

        [Test]
        public void SpellBook_ListsTheSpellsByDeity_GreyUntilLearnedOrCarried()
        {
            var s = _f.NewGame();
            Teach(s, Fireball);
            HeroBag(s).Add(LightningBook, 1);
            var book = SpellBook.Of(s);
            Assert.That(book.Of(SpellDeity.Innos).Select(x => x.Item), Is.EqualTo(new[] { FireballBook, LightBook }), "template-name order");
            Assert.That(book.Of(SpellDeity.Adanos).Select(x => x.Item), Is.EqualTo(new[] { IcelanceBook }));
            var fireball = book.Of(SpellDeity.Innos)[0];
            Assert.That(fireball.Learned && fireball.Coloured, Is.True);
            Assert.That(fireball.Requirements.Last().Value, Is.EqualTo("15 - 30"), "mana min - max");
            Assert.That(fireball.Requirements.Select(r => r.Value), Has.Member("50"));
            var light = book.Of(SpellDeity.Innos)[1];
            Assert.That(light.Learned || light.Coloured, Is.False, "unlearned and not carried: grey");
            Assert.That(light.Requirements.Last().Value, Is.EqualTo("10"));
            var lightning = book.Of(SpellDeity.Beliar)[0];
            Assert.That(lightning.Learned, Is.False);
            Assert.That(lightning.Coloured, Is.True, "G3 GetIcon: amount > 0 shows the colour icon");
        }

        [Test]
        public void InventoryPages_NoLongerListSpellBooks_ButTheQuickBarFindsThem()
        {
            var s = _f.NewGame();
            Teach(s, Fireball);
            HeroBag(s).Add(FireballScroll, 1);
            var rows = ItemLists.Of(s, ActorId.Player).SelectMany(g => g.Rows).Select(r => r.Item).ToList();
            Assert.That(rows, Has.No.Member(FireballBook), "G3 inventory tabs filter 1, 2, 3, 4, 6; spells are on the spell page");
            Assert.That(rows, Has.Member(FireballScroll));
            var row = ItemLists.HeroRow(s, FireballBook);
            Assert.That(row, Is.Not.Null);
            Assert.That(row.Category, Is.EqualTo(ItemCategory.Spellbook));
            Assert.That(ItemLists.HeroRow(s, LightBook), Is.Null, "not carried");
        }
    }
}
