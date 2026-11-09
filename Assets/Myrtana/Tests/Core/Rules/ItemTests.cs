using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Story;
using NUnit.Framework;

namespace Myrtana.Tests
{
    public class ItemTests
    {
        static readonly ActorId Wilson = new ActorId("Wilson");

        public static ItemDef Item(string id, int value = 10, EquipSlot slot = EquipSlot.None, string kind = "None",
                                   AttributeRequirement[] requirements = null, AttributeGain[] equip = null, AttributeGain[] use = null,
                                   ItemFlags flags = ItemFlags.None, int quality = 0, string[] grants = null) =>
            new ItemDef(id, new TextId("FO_" + id), default, ItemCategory.Misc, kind, slot, value, quality, null, null,
                        requirements, null, equip, use, grants, null, flags);

        static StoryFixture Fixture() => new StoryFixture()
            .Define(Item("It_Gold", 1))
            .Define(Item("It_Sword", 2500, EquipSlot.MeleeWeapon, "OneHanded", new[] { new AttributeRequirement("STR", 150) }))
            .Define(Item("It_ForgedSword", 2500, EquipSlot.MeleeWeapon, "OneHanded", new[] { new AttributeRequirement("STR", 105) }, quality: 128))
            .Define(Item("Body_Paladin", 150000, EquipSlot.Armor, "Body", equip: new[] { new AttributeGain("PROT_BLADE", ModifierOp.AddToVal, 100) }))
            .Define(Item("Body_King", 50000, EquipSlot.Armor, "Body", flags: ItemFlags.FullBody))
            .Define(Item("It_Helmet", 800, EquipSlot.Helmet, "Helmet"))
            .Define(Item("It_Ring_MP", 600, EquipSlot.Ring, "Ring", equip: new[] { new AttributeGain("MP", ModifierOp.AddToMax, 20) }))
            .Define(Item("It_Potion_Health", 250, use: new[] { new AttributeGain("HP", ModifierOp.AddPercentageToVal, 50) }))
            .Define(Item("It_Wolf_Fur", 50, kind: "TrophyFur"))
            .Define(Item("It_Quest_Letter", 10, flags: ItemFlags.Mission));

        [Test]
        public void Stacks_MergeByItemAndQuality()
        {
            var inv = new Inventory();
            inv.Add("It_Gold", 10);
            inv.Add("It_Gold", 5);
            inv.Add("It_Sword", 1, 128);
            inv.Add("It_Sword", 1);
            Assert.That(inv.Stacks.Count, Is.EqualTo(3));
            Assert.That(inv.Count("It_Gold"), Is.EqualTo(15));
            Assert.That(inv.Remove("It_Gold", 20), Is.EqualTo(15));
            Assert.That(inv.Remove("It_Sword", 1), Is.EqualTo(1));
            Assert.That(inv.QualityOf("It_Sword"), Is.EqualTo(128), "the plain stack goes first");
        }

        [Test]
        public void PickUp_WorldOnlyLookGivesItsInventoryItem()
        {
            var f = Fixture()
                .Define(new ItemDef("It_Gold_Stack_01", new TextId("FO_It_Gold"), default, ItemCategory.Misc, "None", EquipSlot.None, 0, 0,
                                    null, null, null, null, null, null, null, null, ItemFlags.None, takenAs: "It_Gold"))
                .Define(new WorldItemDef("stack", "It_Gold_Stack_01", 50, null, null, null, 1, default));
            var _ = f.Dialog;
            Assert.That(f.Items.World.PickUp("stack"), Is.True);
            Assert.That(f.Items.Inventories.Of(ActorId.Player).Count("It_Gold"), Is.EqualTo(50));
            Assert.That(f.Items.Inventories.Of(ActorId.Player).Count("It_Gold_Stack_01"), Is.Zero);
            Assert.That(f.Items.World.IsPresent("stack"), Is.False);
        }

        [Test]
        public void Transfer_CreatesMissingItemsOnTheGiverAndIsPublished()
        {
            var f = Fixture();
            var _ = f.Dialog;
            var moves = new List<ItemsTransferred>();
            f.Events.Subscribe<ItemsTransferred>(moves.Add);
            f.Items.Inventories.Transfer(Wilson, ActorId.Player, "It_Gold", 400, createMissing: true);
            Assert.That(f.Items.Inventories.Of(ActorId.Player).Count("It_Gold"), Is.EqualTo(400));
            Assert.That(moves.Single().Amount, Is.EqualTo(400));
        }

        [Test]
        public void EquipConjured_IgnoresRequirementsButNeedsTheItem()
        {
            var f = Fixture();
            var _ = f.Dialog;
            var eq = f.Items.Equipment;
            Assert.That(eq.EquipConjured("It_Sword"), Is.EqualTo(EquipResult.NotCarried));
            f.Items.Inventories.Of(ActorId.Player).Add("It_Sword", 1);
            Assert.That(eq.Equip("It_Sword"), Is.EqualTo(EquipResult.MissingAttribute), "STR 100 < 150");
            Assert.That(eq.EquipConjured("It_Sword"), Is.EqualTo(EquipResult.Done), "the flame sword spell puts it into the hand");
            Assert.That(eq.In(WornSlot.MeleeWeapon), Is.EqualTo("It_Sword"));
        }

        [Test]
        public void Equip_ChecksRequirementsForgingAndArmorVsHelmet()
        {
            var f = Fixture();
            var _ = f.Dialog;
            var hero = f.Items.Inventories.Of(ActorId.Player);
            var eq = f.Items.Equipment;
            foreach (var id in new[] { "It_Sword", "It_ForgedSword", "Body_Paladin", "Body_King", "It_Helmet", "It_Ring_MP", "It_Potion_Health" }) hero.Add(id, 1);
            hero.Add("It_ForgedSword", 0);

            Assert.That(eq.Equip("It_Sword"), Is.EqualTo(EquipResult.MissingAttribute), "STR 100 < 150");
            Assert.That(eq.Equip("It_ForgedSword"), Is.EqualTo(EquipResult.Done), "forged needs 10 less: 95 ≤ 100");
            Assert.That(eq.Equip("It_Potion_Health"), Is.EqualTo(EquipResult.NotEquippable));

            Assert.That(eq.Equip("Body_Paladin"), Is.EqualTo(EquipResult.Done));
            Assert.That(f.Hero.Stats.Value("PROT_BLADE"), Is.EqualTo(100));
            Assert.That(eq.Equip("It_Helmet"), Is.EqualTo(EquipResult.Done));
            Assert.That(eq.Equip("Body_King"), Is.EqualTo(EquipResult.HelmetWithFullArmor));
            eq.Unequip(WornSlot.Helmet);
            Assert.That(eq.Equip("Body_King"), Is.EqualTo(EquipResult.Done));
            Assert.That(f.Hero.Stats.Value("PROT_BLADE"), Is.EqualTo(0), "the paladin armour was replaced");
            Assert.That(eq.Equip("It_Helmet"), Is.EqualTo(EquipResult.HelmetWithFullArmor));

            Assert.That(eq.Equip("It_Ring_MP"), Is.EqualTo(EquipResult.Done));
            Assert.That(f.Hero.Stats.Maximum("MP"), Is.EqualTo(120));
            Assert.That(eq.Equip("It_Ring_MP"), Is.EqualTo(EquipResult.NotCarried), "only one ring carried");
            hero.Remove("It_Ring_MP", 1);
            Assert.That(eq.In(WornSlot.LeftRing), Is.Null, "an item that leaves the inventory is taken off");
            Assert.That(f.Hero.Stats.Maximum("MP"), Is.EqualTo(100));
        }

        [Test]
        public void Equip_KeepsBonusesOfOtherSources_AndTheOtherWeaponSlot()
        {
            // combat-6 (editor-runner-2): a bonus set by someone else (a script, a test) was wiped by the next Equip, and the
            // melee slot looked cleared after equipping a bow.
            var f = Fixture()
                .Define(Item("It_Staff", 900, EquipSlot.MeleeWeapon, "Staff"))
                .Define(Item("It_Bow", 400, EquipSlot.RangedWeapon, "Bow"))
                .Define(Item("It_Ring_STR", 600, EquipSlot.Ring, "Ring", equip: new[] { new AttributeGain("STR", ModifierOp.AddToVal, 10) }));
            var _ = f.Dialog;
            var hero = f.Items.Inventories.Of(ActorId.Player);
            foreach (var id in new[] { "It_Staff", "It_Bow", "It_Ring_STR" }) hero.Add(id, 1);
            var eq = f.Items.Equipment;
            int str = f.Hero.Stats.Value("STR");
            f.Hero.Stats.SetBonus("STR", 50);
            Assert.That(eq.Equip("It_Ring_STR"), Is.EqualTo(EquipResult.Done));
            Assert.That(f.Hero.Stats.Value("STR"), Is.EqualTo(str + 60), "the ring adds to the other bonus");
            Assert.That(eq.Equip("It_Staff"), Is.EqualTo(EquipResult.Done));
            Assert.That(eq.Equip("It_Bow"), Is.EqualTo(EquipResult.Done));
            Assert.That(eq.In(WornSlot.MeleeWeapon), Is.EqualTo("It_Staff"), "a bow does not take the staff off");
            Assert.That(eq.In(WornSlot.RangedWeapon), Is.EqualTo("It_Bow"));
            Assert.That(f.Hero.Stats.Value("STR"), Is.EqualTo(str + 60));
            eq.Unequip(WornSlot.LeftRing);
            Assert.That(f.Hero.Stats.Value("STR"), Is.EqualTo(str + 50), "only the ring's share goes");
        }

        [Test]
        public void Use_AppliesEffectsAndConsumesOne()
        {
            var f = Fixture();
            var _ = f.Dialog;
            f.Hero.Stats.Apply("HP", ModifierOp.SetVal, 20);
            f.Items.Inventories.Of(ActorId.Player).Add("It_Potion_Health", 2);
            Assert.That(f.Items.Use.Use("It_Potion_Health"), Is.True);
            Assert.That(f.Hero.Stats.Value("HP"), Is.EqualTo(120));
            Assert.That(f.Items.Inventories.Of(ActorId.Player).Count("It_Potion_Health"), Is.EqualTo(1));
            Assert.That(f.Items.Use.Use("It_Sword"), Is.False);
        }

        // trading.json: price = max(1, trunc(value · (F·B − AB penalty when selling)))
        [TestCase(false, false, false, null, 2500, 2500)]
        [TestCase(true, false, false, null, 2500, 750)]
        [TestCase(true, true, false, null, 2500, 625)]
        [TestCase(true, false, false, "Trophy", 50, 30)]
        [TestCase(true, true, false, "Trophy", 50, 27)]
        [TestCase(false, false, true, null, 100, 90)]
        [TestCase(true, false, true, null, 100, 33)]
        [TestCase(true, true, false, null, 1, 1)]
        public void Prices(bool sells, bool ab, bool barter, string specialty, int value, int expected)
        {
            var kind = specialty == "Trophy" ? "TrophyFur" : "None";
            var q = new PriceQuery(Item("X", value, kind: kind), sells, specialty, barter, merchantFriendly: false);
            Assert.That(PriceCalculator.UnitPrice(TestRules.Trade(), new RuleOptions(alternativeBalancing: ab), q), Is.EqualTo(expected));
        }

        /// <summary>
        /// Player feedback item 11 (trade exploits): with CP's formula no item can be bought and sold back at a profit — for
        /// any gold value, barter perk, specialist merchant and balancing, the unit sell price never exceeds the buy price
        /// (the minimum price 1 included). A stack is priced per unit (G3 pool value = Σ unit price × amount).
        /// </summary>
        [Test]
        public void Prices_NoBuySellLoopProfit()
        {
            var rules = TestRules.Trade();
            foreach (bool ab in new[] { false, true })
            foreach (bool barter in new[] { false, true })
            foreach (var (specialty, kind) in new[] { ((string)null, "None"), ("Trophy", "TrophyFur"), ("Valuable", "Valuable") })
            {
                var options = new RuleOptions(alternativeBalancing: ab);
                for (int value = 0; value <= 5000; value++)
                {
                    var item = Item("X", value, kind: kind);
                    int buy = PriceCalculator.UnitPrice(rules, options, new PriceQuery(item, false, specialty, barter, merchantFriendly: false));
                    int sell = PriceCalculator.UnitPrice(rules, options, new PriceQuery(item, true, specialty, barter, merchantFriendly: false));
                    Assert.That(sell, Is.LessThanOrEqualTo(buy), $"value {value} ab {ab} barter {barter} {specialty}");
                    Assert.That(buy, Is.GreaterThanOrEqualTo(1));
                }
            }
        }

        [Test]
        public void Trade_GeneratesStockOnceAndMovesGoodsAndGold()
        {
            var f = Fixture()
                .Define(new TreasureSetDef("TS_Gen", TreasureUse.TradeGenerate, 1, 1, new[] { new TreasureStack("It_Wolf_Fur", 4, 0) }))
                .Define(new TreasureSetDef("TS_All", TreasureUse.NotRandom, 0, 0, new[] { new TreasureStack("It_Gold", 1000, 0), new TreasureStack("It_Sword", 1, 0) }))
                .Define(new MerchantDef(Wilson, "Orc", "Trophy", new[] { "TS_Gen", "TS_All" }, false));
            var _ = f.Dialog;
            var trade = f.Items.Trade;
            var stock = f.Items.Inventories.Of(Wilson);
            trade.Open(Wilson, 0);
            Assert.That(stock.Count("It_Wolf_Fur"), Is.InRange(2, 4));
            Assert.That(stock.Count("It_Gold"), Is.EqualTo(1000));
            trade.Open(Wilson, 1);
            Assert.That(stock.Count("It_Gold"), Is.EqualTo(1000), "generated once");

            var hero = f.Items.Inventories.Of(ActorId.Player);
            Assert.That(trade.Buy(Wilson, "It_Sword", 1), Is.EqualTo(TradeResult.NotEnoughGold));
            hero.Add("It_Gold", 3000);
            Assert.That(trade.Buy(Wilson, "It_Sword", 1), Is.EqualTo(TradeResult.Done));
            Assert.That(hero.Count("It_Gold"), Is.EqualTo(500));
            Assert.That(stock.Count("It_Gold"), Is.EqualTo(3500));

            hero.Add("It_Wolf_Fur", 2);
            Assert.That(trade.SellPrice(Wilson, "It_Wolf_Fur"), Is.EqualTo(27), "specialist price with AB");
            Assert.That(trade.Sell(Wilson, "It_Wolf_Fur", 2), Is.EqualTo(TradeResult.Done));
            Assert.That(hero.Count("It_Gold"), Is.EqualTo(554));
            hero.Add("It_Quest_Letter", 1);
            Assert.That(trade.Sell(Wilson, "It_Quest_Letter", 1), Is.EqualTo(TradeResult.NotForSale));
        }

        [Test]
        public void ArmorSales_NeedPoliticalFameByValue()
        {
            var f = Fixture();
            var _ = f.Dialog;
            Assert.That(f.Items.Trade.RequiredFame("Body_Paladin"), Is.EqualTo(60), "AB: above 120000 needs 60");
            Assert.That(f.Items.Trade.RequiredFame("Body_King"), Is.EqualTo(40));
            Assert.That(f.Items.Trade.RequiredFame("It_Sword"), Is.EqualTo(0));
        }

        [Test]
        public void TreasureRefresh_TopsUpStacksBelowHalf()
        {
            var roller = new TreasureRoller(new SeededRandom(5));
            var set = new TreasureSetDef("R", TreasureUse.TradeRefresh, 0, 0, new[] { new TreasureStack("A", 10, 0), new TreasureStack("B", 10, 0) });
            var inv = new Inventory();
            inv.Add("B", 6);
            roller.Refresh(set, inv);
            Assert.That(inv.Count("A"), Is.InRange(5, 10));
            Assert.That(inv.Count("B"), Is.EqualTo(6));
        }

        [Test]
        public void Teaching_CostsLearningPointsAndFollowsTheSurcharge()
        {
            var f = Fixture()
                .Define(new SkillDef("Perk_1H_2", "It_Perk_1H_2", 5, new[] { new AttributeRequirement("STR", 150) }, "Perk_1H_1", "STR"))
                .Define(new TeacherDef(Wilson, new[] { "STR", "HP" }, new[] { "Perk_1H_2" }, false));
            var _ = f.Dialog;
            var teaching = f.Items.Teaching;
            var str = new Lesson("STR", 5, null);
            Assert.That(teaching.Offers(Wilson, str), Is.False, "teaching is not enabled yet");
            f.Items.Roles.Set(Wilson, ActorRole.Teach, true);
            Assert.That(teaching.Offers(Wilson, str), Is.True);
            Assert.That(teaching.Offers(Wilson, new Lesson("DEX", 5, null)), Is.False);

            Assert.That(teaching.Check(str, 0), Is.EqualTo(TeachResult.NotEnoughLearningPoints));
            f.Progression.AddLearningPoints(30);
            Assert.That(teaching.Teach(str), Is.EqualTo(TeachResult.Done));
            Assert.That(f.Hero.Stats.Value("STR"), Is.EqualTo(105));
            Assert.That(teaching.Teach(new Lesson("HP", 2, null)), Is.EqualTo(TeachResult.Done));
            Assert.That(f.Hero.Stats.Maximum("HP"), Is.EqualTo(208), "4 points per LP for vitals");

            var perk = new Lesson(null, 0, "Perk_1H_2");
            Assert.That(teaching.Check(perk, 0), Is.EqualTo(TeachResult.MissingRequirements));

            f.Hero.Stats.Set("STR", new StatValue(250));
            Assert.That(teaching.LearningPointCost(str), Is.EqualTo(10), "AB doubles LP from 250 on");
            Assert.That(teaching.GoldCost(str, 100), Is.EqualTo(200));
        }
    
        [Test]
        public void Teaching_AnnouncesTheRaiseAndTheLearningPointsLeft()
        {
            // fix-77: gCInfo_PS::OnEndInfo prints "<attribute> + n = value" / "GO_Learned …", then "GO_LearningPointsLeft"
            var f = Fixture()
                .Define(new SkillDef("Perk_Test", "It_Perk_Test", 5, null, null, "STR"))
                .Define(new TeacherDef(Wilson, new[] { "STR" }, new[] { "Perk_Test" }, false));
            var _ = f.Dialog;
            var raised = new List<AttributeRaised>();
            var taught = new List<LessonTaught>();
            f.Events.Subscribe<AttributeRaised>(raised.Add);
            f.Events.Subscribe<LessonTaught>(taught.Add);
            f.Progression.AddLearningPoints(30);

            Assert.That(f.Items.Teaching.Teach(new Lesson("STR", 5, null)), Is.EqualTo(TeachResult.Done));
            Assert.That(raised.Single().Attribute, Is.EqualTo("STR"));
            Assert.That(raised.Single().Amount, Is.EqualTo(5));
            Assert.That(raised.Single().Value, Is.EqualTo(105));
            Assert.That(taught.Single().Skill, Is.Null);
            Assert.That(taught.Single().LearningPointsLeft, Is.EqualTo(25));

            Assert.That(f.Items.Teaching.Teach(new Lesson(null, 0, "Perk_Test")), Is.EqualTo(TeachResult.Done));
            Assert.That(raised, Has.Count.EqualTo(1), "a skill raises nothing");
            Assert.That(taught.Last().Skill, Is.EqualTo("Perk_Test"));
            Assert.That(taught.Last().LearningPointsLeft, Is.EqualTo(20));
        }

        [Test]
        public void SkillCatalog_KnowsG3PerksOnly_FixFortyEight()
        {
            var db = new ContentDatabase();
            db.Put(new SkillDef("Perk_Axe_1", "It_Perk_Axe_1", 5, null, null, "STR"));
            db.Put(new SkillDef("Perk_Crossbow_1", "It_Perk_Crossbow_1", 5, null, null, "STR"));
            Assert.That(SkillCatalog.Exists(db, "Perk_Axe_1", out var hint), Is.True);
            Assert.That(hint, Is.Null);
            Assert.That(SkillCatalog.Exists(db, "Perk_2H_1", out hint), Is.False, "G3 has no Perk_2H_1: 2H swords need Perk_Axe_1");
            Assert.That(hint, Is.Null);
            Assert.That(SkillCatalog.Exists(db, "Perk_CrossBow_1", out hint), Is.False);
            Assert.That(hint, Is.EqualTo("Perk_Crossbow_1"));
        }
}
}
