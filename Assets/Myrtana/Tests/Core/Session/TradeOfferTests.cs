using System.Linq;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session;
using NUnit.Framework;

namespace Myrtana.Tests
{
    public class TradeOfferTests
    {
        static readonly ActorId Wilson = new ActorId("Wilson");

        [Test]
        public void Pools_ChangeNothingUntilAcceptedAndNeedTheHeroSideToBeWorthEnough()
        {
            using var f = new SessionFixture()
                .Add(RulesSchema.ItemFolder, ItemTests.Item("It_Gold", 1), ItemTests.Item("It_Sword", 2500, EquipSlot.MeleeWeapon, "OneHanded"),
                     ItemTests.Item("It_Wolf_Fur", 50, kind: "TrophyFur"))
                .Add(RulesSchema.TreasureFolder, new TreasureSetDef("TS_All", TreasureUse.NotRandom, 0, 0,
                                                                    new[] { new TreasureStack("It_Gold", 1000, 0), new TreasureStack("It_Sword", 1, 0) }))
                .Add(RulesSchema.MerchantFolder, new MerchantDef(Wilson, "Orc", "Trophy", new[] { "TS_All" }, false));
            var session = f.NewGame();
            var hero = session.Items.Inventories.Of(ActorId.Player);
            hero.Add("It_Gold", 3000);
            hero.Add("It_Wolf_Fur", 2);
            session.Items.Trade.Open(Wilson, 0);
            var offer = new TradeOffer(session, Wilson);

            Assert.That(offer.Add(TradeOffer.Side.Vendor, "It_Sword", 1), Is.EqualTo(1));
            Assert.That(offer.Add(TradeOffer.Side.Hero, "It_Wolf_Fur", 5), Is.EqualTo(2), "only what the hero carries");
            Assert.That(offer.Add(TradeOffer.Side.Hero, "It_Gold", 5), Is.Zero, "gold only through balancing");
            int buy = session.Items.Trade.BuyPrice(Wilson, "It_Sword"), sell = session.Items.Trade.SellPrice(Wilson, "It_Wolf_Fur");
            Assert.That(offer.Value(TradeOffer.Side.Vendor), Is.EqualTo(buy));
            Assert.That(offer.Value(TradeOffer.Side.Hero), Is.EqualTo(2 * sell));
            Assert.That(offer.WithoutOffered(TradeOffer.Side.Hero, ItemLists.Of(session, ActorId.Player)[0].Rows), Is.Empty);

            Assert.That(offer.Accept(), Is.EqualTo(TradeResult.NotEnoughGold));
            Assert.That(hero.Count("It_Sword"), Is.Zero);
            Assert.That(hero.Count("It_Wolf_Fur"), Is.EqualTo(2));

            Assert.That(offer.BalanceGold(), Is.True);
            Assert.That(offer.Gold(TradeOffer.Side.Hero), Is.EqualTo(buy - 2 * sell));
            Assert.That(offer.Value(TradeOffer.Side.Hero), Is.EqualTo(offer.Value(TradeOffer.Side.Vendor)));
            Assert.That(offer.Accept(), Is.EqualTo(TradeResult.Done));
            Assert.That(hero.Count("It_Sword"), Is.EqualTo(1));
            Assert.That(hero.Count("It_Wolf_Fur"), Is.Zero);
            Assert.That(hero.Count("It_Gold"), Is.EqualTo(3000 - buy + 2 * sell));
            Assert.That(offer.IsEmpty, Is.True);
        }

        static SessionFixture Merchant(params TreasureSetDef[] sets) => new SessionFixture()
            .Add(RulesSchema.ItemFolder, ItemTests.Item("It_Gold", 1), ItemTests.Item("It_Sword", 2500, EquipSlot.MeleeWeapon, "OneHanded"),
                 ItemTests.Item("It_Wolf_Fur", 50, kind: "TrophyFur"), ItemTests.Item("It_Letter", 10, flags: ItemFlags.Mission),
                 ItemTests.Item("Body_Heavy", 90000, EquipSlot.Armor, "Body"), ItemTests.Item("It_Apple", 5))
            .Add(RulesSchema.TreasureFolder, sets.Length > 0 ? sets : new[]
            {
                new TreasureSetDef("TS_All", TreasureUse.NotRandom, 0, 0, new[]
                {
                    new TreasureStack("It_Gold", 1000, 0), new TreasureStack("It_Sword", 1, 0), new TreasureStack("Body_Heavy", 1, 0),
                }),
            })
            .Add(RulesSchema.MerchantFolder, new MerchantDef(Wilson, "Orc", "Trophy", sets.Length > 0 ? sets.Select(t => t.Id).ToArray() : new[] { "TS_All" }, false));

        [Test]
        public void Accept_SwapsBothPoolsAsTheyAreAndNeedsBothSidesWorthSomething()
        {
            using var f = Merchant();
            var session = f.NewGame();
            var hero = session.Items.Inventories.Of(ActorId.Player);
            hero.Add("It_Wolf_Fur", 10);
            session.Items.Trade.Open(Wilson, 0);
            var offer = new TradeOffer(session, Wilson);

            offer.Add(TradeOffer.Side.Hero, "It_Wolf_Fur", 1);
            Assert.That(offer.CanAccept, Is.False, "G3: the Trade button needs both pools");
            Assert.That(offer.Accept(), Is.EqualTo(TradeResult.NothingOffered));

            offer.Clear();
            offer.Add(TradeOffer.Side.Hero, "It_Wolf_Fur", 10);
            offer.Add(TradeOffer.Side.Vendor, "It_Sword", 1);
            Assert.That(offer.Value(TradeOffer.Side.Hero), Is.LessThan(offer.Value(TradeOffer.Side.Vendor)));
            Assert.That(offer.CanAccept, Is.False);
            Assert.That(offer.BalanceGold(), Is.False, "the hero has no gold");
            hero.Add("It_Gold", 5000);
            Assert.That(offer.BalanceGold(), Is.True);
            int paid = offer.Gold(TradeOffer.Side.Hero);
            Assert.That(offer.Accept(), Is.EqualTo(TradeResult.Done));
            Assert.That(hero.Count("It_Gold"), Is.EqualTo(5000 - paid));
            Assert.That(session.Items.Trade.ForSale(Wilson, "It_Wolf_Fur"), Is.EqualTo(10), "sold goods become merchandise");

            // A surplus is the merchant's gain: furs worth more than one apple, no change paid.
            hero.Add("It_Wolf_Fur", 10);
            session.Items.Inventories.Of(Wilson).Add("It_Apple", 1);
            Assert.That(offer.Add(TradeOffer.Side.Vendor, "It_Apple", 1), Is.Zero, "not merchandise: given, not traded");
            offer.Add(TradeOffer.Side.Vendor, "It_Wolf_Fur", 1);
            offer.Add(TradeOffer.Side.Hero, "It_Wolf_Fur", 10);
            int gold = hero.Count("It_Gold");
            Assert.That(offer.Accept(), Is.EqualTo(TradeResult.Done));
            Assert.That(hero.Count("It_Gold"), Is.EqualTo(gold));
            Assert.That(hero.Count("It_Wolf_Fur"), Is.EqualTo(1));
        }

        [Test]
        public void Offers_HideMissionAndWornItemsAndRefuseArmourWithoutFame()
        {
            using var f = Merchant();
            var session = f.NewGame();
            var hero = session.Items.Inventories.Of(ActorId.Player);
            hero.Add("It_Letter", 1);
            hero.Add("It_Sword", 2);
            session.Items.Equipment.Equip("It_Sword");
            session.Items.Trade.Open(Wilson, 0);
            var offer = new TradeOffer(session, Wilson);

            Assert.That(offer.Add(TradeOffer.Side.Hero, "It_Letter", 1), Is.Zero);
            Assert.That(offer.LastRefusal, Is.EqualTo(TradeResult.NotForSale));
            Assert.That(offer.Add(TradeOffer.Side.Hero, "It_Sword", 2), Is.EqualTo(1), "the worn sword stays");
            Assert.That(offer.Add(TradeOffer.Side.Vendor, "Body_Heavy", 1), Is.Zero);
            Assert.That(offer.LastRefusal, Is.EqualTo(TradeResult.NeedsFame));
            Assert.That(offer.FameRefusal("$(name) $(value)", "Body_Heavy"), Does.EndWith(" 50"), "AB: up to 120000 needs 50");
            session.Fame.Add(FameKind.Political, "Orc", 50);
            Assert.That(offer.Add(TradeOffer.Side.Vendor, "Body_Heavy", 1), Is.EqualTo(1));
        }

        [Test]
        public void Stock_IsPreparedWhenTheConversationStartsAndRefreshedADayAfterTheLastOne()
        {
            using var f = Merchant(new TreasureSetDef("TS_Refresh", TreasureUse.TradeRefresh, 0, 0, new[] { new TreasureStack("It_Apple", 5, 0) }));
            var session = f.NewGame();
            var trade = session.Items.Trade;
            var stock = session.Items.Inventories.Of(Wilson);
            trade.Open(Wilson, 0);
            Assert.That(stock.Count("It_Apple"), Is.InRange(2, 5), "first conversation refreshes");
            stock.Remove("It_Apple", stock.Count("It_Apple") - 2);
            trade.Open(Wilson, 100);
            Assert.That(stock.Count("It_Apple"), Is.EqualTo(2), "same conversation (the trade page) changes nothing");
            trade.NoteContactEnded(Wilson, 1);
            trade.Open(Wilson, 30);
            Assert.That(stock.Count("It_Apple"), Is.EqualTo(2), "2 is not below 5/2 = 2");
            trade.NoteContactEnded(Wilson, 30);
            stock.Remove("It_Apple", 1);
            trade.Open(Wilson, 50);
            Assert.That(stock.Count("It_Apple"), Is.EqualTo(1), "only 20 hours since the last conversation ended");
            trade.NoteContactEnded(Wilson, 50);
            trade.Open(Wilson, 74.5);
            Assert.That(stock.Count("It_Apple"), Is.InRange(3, 6));
        }

        [Test]
        public void Merchandise_SurvivesSaveAndLoad()
        {
            using var f = Merchant();
            var session = f.NewGame();
            session.Items.Trade.Open(Wilson, 0);
            session.Items.Inventories.Of(Wilson).Add("It_Apple", 3);
            var loaded = f.Reload(session);
            Assert.That(loaded.Items.Trade.ForSale(Wilson, "It_Sword"), Is.EqualTo(1));
            Assert.That(loaded.Items.Trade.ForSale(Wilson, "It_Apple"), Is.Zero);
            Assert.That(loaded.Items.Inventories.Of(Wilson).Count("It_Apple"), Is.EqualTo(3));
        }

        [Test]
        public void Lists_FollowG3InventoryOrder()
        {
            ItemDef W(string id, string kind, int damage) =>
                new ItemDef(id, new TextId(id), default, ItemCategory.Weapon, kind, EquipSlot.MeleeWeapon, 10, 0, null, null, null, null, null, null,
                            null, new ItemDamage("Blade", damage, default), ItemFlags.None);
            var defs = new[] { W("Axe_A", "Axe", 99), W("Sword_B", "OneHanded", 50), W("Sword_A", "OneHanded", 50), W("Big", "TwoHanded", 10),
                               W("Sword_C", "OneHanded", 80), W("Bow", "Bow", 200) }.ToList();
            defs.Sort((a, b) => ItemOrder.Compare(a, a.Id, b, b.Id));
            Assert.That(defs.Select(d => d.Id), Is.EqualTo(new[] { "Big", "Sword_C", "Sword_A", "Sword_B", "Bow", "Axe_A" }));
        }
    }
}
