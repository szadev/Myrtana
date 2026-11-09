using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>fix-85 [owner]: the hero drops stacks from the inventory page (G3 CP has no such drop).</summary>
    public class HeroItemDropTests
    {
        static SessionFixture Fixture() => new SessionFixture()
            .Add(RulesSchema.ItemFolder, ItemTests.Item("It_Gold", 1), ItemTests.Item("It_Sword", 100, EquipSlot.MeleeWeapon, "OneHanded"),
                 ItemTests.Item("Arrow", 1, EquipSlot.Ammo, "Arrow"), ItemTests.Item("It_Letter", 0, flags: ItemFlags.Mission),
                 ItemTests.Item("It_Torch", 5, kind: "Torch"));

        static readonly float[] Here = { 1f, 2f, 3f };

        [Test]
        public void Drop_PartOfAStack_LiesInTheWorld_SavedAndPickedUpAgain()
        {
            using var f = Fixture();
            var s = f.NewGame();
            s.Items.Inventories.Give(ActorId.Player, "Arrow", 20);
            Assert.That(HeroItemDrop.MaxAmount(s, "Arrow"), Is.EqualTo(20));
            var d = HeroItemDrop.Drop(s, "Arrow", 5, Here, 45f, out var why);
            Assert.That(why, Is.EqualTo(DropRefusal.None));
            Assert.That(d.Amount, Is.EqualTo(5));
            Assert.That(d.Owner, Is.EqualTo(ActorId.Player));
            Assert.That(s.Items.Inventories.Of(ActorId.Player).Count("Arrow"), Is.EqualTo(15));

            var loaded = f.Reload(s);
            Assert.That(loaded.Items.World.TryGetDropped(d.Id, out var back) && back.Amount == 5 && back.Yaw == 45f, Is.True, "saved like other world items");
            Assert.That(loaded.Items.World.PickUp(d.Id), Is.True);
            Assert.That(loaded.Items.Inventories.Of(ActorId.Player).Count("Arrow"), Is.EqualTo(20));
        }

        [Test]
        public void Drop_TooMany_DropsWhatIsCarried()
        {
            using var f = Fixture();
            var s = f.NewGame();
            s.Items.Inventories.Give(ActorId.Player, "It_Gold", 7);
            var d = HeroItemDrop.Drop(s, "It_Gold", 50, Here, 0, out _);
            Assert.That(d.Amount, Is.EqualTo(7));
            Assert.That(s.Items.Inventories.Of(ActorId.Player).Count("It_Gold"), Is.EqualTo(0));
        }

        [Test]
        public void Drop_QuestItem_IsRefused()
        {
            using var f = Fixture();
            var s = f.NewGame();
            s.Items.Inventories.Give(ActorId.Player, "It_Letter", 1);
            Assert.That(HeroItemDrop.Drop(s, "It_Letter", 1, Here, 0, out var why), Is.Null);
            Assert.That(why, Is.EqualTo(DropRefusal.QuestItem));
            Assert.That(HeroItemDrop.MaxAmount(s, "It_Letter"), Is.EqualTo(0));
            Assert.That(s.Items.Inventories.Of(ActorId.Player).Count("It_Letter"), Is.EqualTo(1));
        }

        [Test]
        public void Drop_NotCarried_OrHeldInTheHand_IsRefused()
        {
            using var f = Fixture();
            var s = f.NewGame();
            HeroItemDrop.Drop(s, "It_Sword", 1, Here, 0, out var why);
            Assert.That(why, Is.EqualTo(DropRefusal.NotCarried));
            s.Items.Inventories.Give(ActorId.Player, "It_Torch", 1);
            s.Items.Hands.SetLeft("It_Torch");
            HeroItemDrop.Drop(s, "It_Torch", 1, Here, 0, out why);
            Assert.That(why, Is.EqualTo(DropRefusal.InHand));
        }

        [Test]
        public void Drop_WornItem_IsTakenOffFirst_ButNotWhileCopiesStay()
        {
            using var f = Fixture();
            var s = f.NewGame();
            s.Items.Inventories.Give(ActorId.Player, "It_Sword", 1);
            s.Items.Inventories.Give(ActorId.Player, "Arrow", 10);
            Assert.That(s.Items.Equipment.Equip("It_Sword"), Is.EqualTo(EquipResult.Done));
            Assert.That(s.Items.Equipment.Equip("Arrow"), Is.EqualTo(EquipResult.Done));
            HeroItemDrop.Drop(s, "Arrow", 4, Here, 0, out _);
            Assert.That(s.Items.Equipment.IsWorn("Arrow"), Is.True, "6 arrows stay in the quiver");
            Assert.That(HeroItemDrop.Drop(s, "It_Sword", 1, Here, 0, out _), Is.Not.Null);
            Assert.That(s.Items.Equipment.IsWorn("It_Sword"), Is.False);
            Assert.That(s.Items.Equipment.In(WornSlot.MeleeWeapon), Is.Null);
        }
    }
}
