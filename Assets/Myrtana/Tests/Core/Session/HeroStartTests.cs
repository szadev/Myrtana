using Myrtana.Core;
using Myrtana.Rules;
using NUnit.Framework;

namespace Myrtana.Tests
{
    public class HeroStartTests
    {
        SessionFixture _f;

        [SetUp]
        public void SetUp()
        {
            var sword = new ItemDef("It_1H_Sword", new TextId("FO_Sword"), default, ItemCategory.Weapon, "OneHanded", EquipSlot.MeleeWeapon,
                                    100, 0, null, null, null, new[] { "Perk_1H_1" }, null, null, null, null, ItemFlags.None);
            _f = new SessionFixture()
                .Add(RulesSchema.ItemFolder, sword, ItemTests.Item("It_Gold", 1), ItemTests.Item("It_Torch", 5, kind: "Torch"))
                .Add(RulesSchema.HeroStartFolder, new HeroStartKit(HeroStartKit.DefinitionId, new[]
                {
                    new StartItem("It_1H_Sword", 1, 0, 0, true),
                    new StartItem("It_Torch", 1, 8, 1, false),
                    new StartItem("It_Gold", 123, 0, -1, false),
                }, new[] { "Perk_1H_1" }, startXp: 2500));
        }

        [TearDown]
        public void TearDown() => _f.Dispose();

        [Test]
        public void NewGame_GivesEquipsAndBindsTheStartKit()
        {
            var game = _f.NewGame();
            var inventory = game.Items.Inventories.Of(ActorId.Player);

            Assert.That(game.Hero.Knows("Perk_1H_1"), Is.True, "skills come first: the sword needs the perk");
            Assert.That(inventory.Count("It_Gold"), Is.EqualTo(123));
            Assert.That(inventory.QualityOf("It_Torch"), Is.EqualTo(8));
            Assert.That(game.Items.Equipment.In(WornSlot.MeleeWeapon), Is.EqualTo("It_1H_Sword"));
            Assert.That(game.Items.QuickSlots.ItemAt(0), Is.EqualTo("It_1H_Sword"));
            Assert.That(game.Items.QuickSlots.ItemAt(1), Is.EqualTo("It_Torch"));
            Assert.That(_f.Log.Messages, Has.None.Contains("start kit"));
            Assert.That(game.Hero.Xp, Is.EqualTo(2500), "CP Ardea_Spawn_Point's GiveXP 500");
        }

        [Test]
        public void LoadedGame_KeepsTheSavedInventory()
        {
            var game = _f.NewGame();
            game.Items.Equipment.Unequip(WornSlot.MeleeWeapon);
            game.Items.Inventories.Consume(ActorId.Player, "It_Gold", 123);

            var loaded = _f.Reload(game);

            Assert.That(loaded.Items.Inventories.Of(ActorId.Player).Count("It_Gold"), Is.Zero);
            Assert.That(loaded.Items.Equipment.In(WornSlot.MeleeWeapon), Is.Null);
        }
    }
}
