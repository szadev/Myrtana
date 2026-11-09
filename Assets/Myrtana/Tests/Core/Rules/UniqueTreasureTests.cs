using System.Linq;
using Myrtana.Core;
using Myrtana.Rules;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// fix-99: G3 GeneratePlunderInventory for chests — a unique set (TS_Unique_Weapons: 48 weapons, min = max = 1) gives
    /// ONE stack per chest, the cheapest not handed out yet in the world; the ledger and the rolled chest survive a load.
    /// </summary>
    public sealed class UniqueTreasureTests
    {
        static ItemDef Weapon(string id, int gold) =>
            new ItemDef(id, new TextId("FO_" + id), default, ItemCategory.Weapon, "Sword", EquipSlot.MeleeWeapon, gold, 0, null, null,
                        null, null, null, null, null, null, default);

        static InteractiveObjectDef Chest(string id, params string[] sets) =>
            new InteractiveObjectDef(id, "G3_Object_Interact_Animated_Chest_Unique_Weapon", InteractiveKind.Chest, "Chest", "chest.FXA",
                                     new float[] { 1, 2, 3 }, null, null, 1, default, null, new ContainerDef(sets, null), null, null);

        static SessionFixture Fixture() => new SessionFixture()
            .Add(RulesSchema.ItemFolder, Weapon("It_Sword_C", 300), Weapon("It_Sword_A", 100), Weapon("It_Sword_B", 200))
            .Add(RulesSchema.TreasureFolder,
                 new TreasureSetDef("TS_Unique_Weapons", TreasureUse.Unique, 1, 1,
                                    new[] { new TreasureStack("It_Sword_C", 1, 0), new TreasureStack("It_Sword_A", 1, 0), new TreasureStack("It_Sword_B", 1, 0) }),
                 new TreasureSetDef("TS_Zero", TreasureUse.Plunder, 0, 0, new[] { new TreasureStack("It_Sword_A", 1, 0) }),
                 new TreasureSetDef("TS_Trader", TreasureUse.NotRandom, 0, 0, new[] { new TreasureStack("It_Sword_A", 5, 0) }))
            .Add(RulesSchema.InteractiveObjectFolder,
                 Chest("u1", "TS_Unique_Weapons"), Chest("u2", "TS_Unique_Weapons"), Chest("u3", "TS_Unique_Weapons"),
                 Chest("u4", "TS_Unique_Weapons"), Chest("zero", "TS_Zero"), Chest("trader", "TS_Trader"));

        static Inventory Contents(Session.GameSession s, string chest) => s.Items.Inventories.Of(InteractiveObjects.ContainerOf(chest));

        [Test]
        public void UniqueSet_GivesOneStack_CheapestNotHandedOut_AndTheLedgerIsSaved()
        {
            using var f = Fixture();
            var s = f.NewGame();
            Assert.That(s.Interaction.Objects.OpenContainer("u1"), Is.True);
            Assert.That(Contents(s, "u1").Stacks.Select(x => (x.Item, x.Amount)), Is.EqualTo(new[] { ("It_Sword_A", 1) }), "one stack, the cheapest");
            s.Interaction.Objects.OpenContainer("u2");
            Assert.That(Contents(s, "u2").Stacks.Select(x => x.Item), Is.EqualTo(new[] { "It_Sword_B" }));

            var loaded = f.Reload(s);
            Assert.That(Contents(loaded, "u1").Stacks.Select(x => x.Item), Is.EqualTo(new[] { "It_Sword_A" }), "a rolled chest keeps its roll");
            loaded.Interaction.Objects.OpenContainer("u1");
            Assert.That(Contents(loaded, "u1").Stacks.Count, Is.EqualTo(1), "reopening after a load does not refill");
            loaded.Interaction.Objects.OpenContainer("u3");
            Assert.That(Contents(loaded, "u3").Stacks.Select(x => x.Item), Is.EqualTo(new[] { "It_Sword_C" }), "ledger survived the load");
            loaded.Interaction.Objects.OpenContainer("u4");
            Assert.That(Contents(loaded, "u4").Stacks.Count, Is.EqualTo(1), "all handed out: one random stack");
        }

        [Test]
        public void ChestPlunder_FollowsG3_MinMaxZeroGivesOne_NotRandomGivesNothing()
        {
            using var f = Fixture();
            var s = f.NewGame();
            s.Interaction.Objects.OpenContainer("zero");
            Assert.That(Contents(s, "zero").Count("It_Sword_A"), Is.EqualTo(1));
            s.Interaction.Objects.OpenContainer("trader");
            Assert.That(Contents(s, "trader").Stacks, Is.Empty, "G3 plunder generation ignores trade/NotRandom sets");
        }
    }
}
