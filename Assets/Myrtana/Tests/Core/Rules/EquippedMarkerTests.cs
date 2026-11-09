using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>G3's blue quick-bar / inventory tile (Game.dll 100a2d40: the stack sits in any gESlot).</summary>
    public class EquippedMarkerTests
    {
        const int Sword = 0, Bow = 1, Arrows = 2, Torch = 3, Scroll = 4, Apple = 5;

        static SessionFixture Fixture() => new SessionFixture()
            .Add(RulesSchema.ItemFolder,
                 ItemTests.Item("It_Sword", 100, EquipSlot.MeleeWeapon, "OneHanded"),
                 ItemTests.Item("It_Bow", 100, EquipSlot.RangedWeapon, "Bow"),
                 ItemTests.Item("It_Bow2", 100, EquipSlot.RangedWeapon, "Bow"),
                 ItemTests.Item("It_Arrow", 1, EquipSlot.Ammo, "Arrow"),
                 ItemTests.Item("It_Torch", 5, kind: "Torch"),
                 ItemTests.Item("It_Scroll", 5, kind: "Cast"),
                 ItemTests.Item("It_Apple", 1, kind: "Fruit"));

        static GameSession Hero(SessionFixture f)
        {
            var session = f.NewGame();
            var hero = session.Items.Inventories.Of(ActorId.Player);
            foreach (var id in new[] { "It_Sword", "It_Bow", "It_Bow2", "It_Torch", "It_Scroll", "It_Apple" }) hero.Add(id, 1);
            hero.Add("It_Arrow", 20);
            var slots = session.Items.QuickSlots;
            slots.Assign(Sword, "It_Sword");
            slots.Assign(Bow, "It_Bow");
            slots.Assign(Arrows, "It_Arrow");
            slots.Assign(Torch, "It_Torch");
            slots.Assign(Scroll, "It_Scroll");
            slots.Assign(Apple, "It_Apple");
            return session;
        }

        static int Mask(GameSession s) =>
            EquippedMarker.QuickBarMask(s.Items.QuickSlots, i => EquippedMarker.IsMarked(i, s.Items.Equipment, s.Items.Hands));

        static int Bits(params int[] slots) => slots.Aggregate(0, (m, i) => m | 1 << i);

        static bool Tinted(GameSession s, string item) =>
            ItemLists.Of(s, ActorId.Player).SelectMany(g => g.Rows).Single(r => r.Item == item).Marked;

        [Test]
        public void SwordStaysBlueWhenTheBowIsChosen_BowAndItsArrowsTurnBlueToo()
        {
            using var f = Fixture();
            var s = Hero(f);
            s.Items.Equipment.Equip("It_Sword");
            Assert.That(Mask(s), Is.EqualTo(Bits(Sword)));

            s.Items.Equipment.Equip("It_Bow");   // quick slot: OnPlayerUseQuickSlot equips the bow (gESlot_Bow / left hand)
            s.Items.Equipment.Equip("It_Arrow"); // the draw equips the arrows (gESlot_Ammo)
            Assert.That(Mask(s), Is.EqualTo(Bits(Sword, Bow, Arrows)), "the sheathed sword keeps gESlot_BackRight");
        }

        static int QuickMask(GameSession s) =>
            EquippedMarker.QuickBarMask(s.Items.QuickSlots, i => EquippedMarker.IsQuickMarked(i, s.Items.Equipment, s.Items.Hands));

        [Test]
        public void QuickBar_DrawnBowAndItsArrowsAreActive_TheSheathedSwordIsNot()
        {
            using var f = Fixture();
            var s = Hero(f);
            s.Items.Equipment.Equip("It_Sword");
            s.Items.Equipment.Equip("It_Bow");
            s.Items.Equipment.Equip("It_Arrow");
            Assert.That(QuickMask(s), Is.EqualTo(Bits(Sword, Bow, Arrows)), "nothing drawn: G3's tint, every worn stack");
            s.Items.Hands.SetWeapon(HandWeapon.Melee);
            Assert.That(QuickMask(s), Is.EqualTo(Bits(Sword)), "the sword drawn");
            s.Items.Hands.SetWeapon(HandWeapon.Ranged);
            Assert.That(QuickMask(s), Is.EqualTo(Bits(Bow, Arrows)), "the bow drawn: bow and its arrows, not the sword it replaced");
            Assert.That(Tinted(s, "It_Sword"), Is.True, "the inventory keeps G3's tint of every worn stack");
            s.Items.Hands.SetWeapon(HandWeapon.None);
            Assert.That(QuickMask(s), Is.EqualTo(Bits(Sword, Bow, Arrows)));
        }

        [Test]
        public void QuickBar_HeldTorchOrSpellIsActive_SheathedWeaponsAreNot()
        {
            using var f = Fixture();
            var s = Hero(f);
            s.Items.Equipment.Equip("It_Sword");
            s.Items.Equipment.Equip("It_Bow");
            s.Items.Hands.SetLeft("It_Torch");
            Assert.That(QuickMask(s), Is.EqualTo(Bits(Torch)));
            s.Items.Hands.SetWeapon(HandWeapon.Melee); // sword drawn beside the torch
            Assert.That(QuickMask(s), Is.EqualTo(Bits(Sword, Torch)));
            s.Items.Hands.SetWeapon(HandWeapon.None);
            s.Items.Hands.SetLeft(null);
            s.Items.Hands.SetRight("It_Scroll");
            Assert.That(QuickMask(s), Is.EqualTo(Bits(Scroll)));
            s.Items.Hands.Clear();
            Assert.That(QuickMask(s), Is.EqualTo(Bits(Sword, Bow)));
        }

        [Test]
        public void TorchIsBlueOnlyWhileInTheHand()
        {
            using var f = Fixture();
            var s = Hero(f);
            s.Items.Equipment.Equip("It_Sword");
            s.Items.Hands.SetLeft("It_Torch");
            Assert.That(Mask(s), Is.EqualTo(Bits(Sword, Torch)));
            Assert.That(Tinted(s, "It_Torch"), Is.True);
            Assert.That(Tinted(s, "It_Sword"), Is.True);

            s.Items.Hands.SetLeft(null); // the torch key again: put away (GetLeftEquipSlot(torch) = None)
            Assert.That(Mask(s), Is.EqualTo(Bits(Sword)));
            Assert.That(Tinted(s, "It_Torch"), Is.False);
        }

        [Test]
        public void SpellOrScrollIsBlueOnlyWhileDrawn()
        {
            using var f = Fixture();
            var s = Hero(f);
            s.Items.Hands.SetRight("It_Scroll");
            Assert.That(Mask(s), Is.EqualTo(Bits(Scroll)));
            s.Items.Hands.SetRight(null);
            Assert.That(Mask(s), Is.Zero);
        }

        [Test]
        public void ConsumablesAndEmptySlotsAreNeverBlue()
        {
            using var f = Fixture();
            var s = Hero(f);
            Assert.That(Mask(s), Is.Zero);
            Assert.That(EquippedMarker.IsMarked(null, s.Items.Equipment, s.Items.Hands), Is.False);
            Assert.That(Tinted(s, "It_Apple"), Is.False);
        }

        [Test]
        public void AnotherBowTakesTheBowSlotAndItsBlue()
        {
            using var f = Fixture();
            var s = Hero(f);
            s.Items.Equipment.Equip("It_Bow");
            s.Items.QuickSlots.Assign(Apple, "It_Bow2");
            s.Items.Equipment.Equip("It_Bow2");
            Assert.That(Mask(s), Is.EqualTo(Bits(Apple)));
        }

        [Test]
        public void DroppedOrUsedUpItemsLoseTheMarkAndTheSlot()
        {
            using var f = Fixture();
            var s = Hero(f);
            s.Items.Equipment.Equip("It_Sword");
            s.Items.Hands.SetRight("It_Scroll");
            var hero = s.Items.Inventories.Of(ActorId.Player);
            hero.Remove("It_Sword", 1);
            hero.Remove("It_Scroll", 1);
            Assert.That(s.Items.Equipment.IsWorn("It_Sword"), Is.False);
            Assert.That(s.Items.QuickSlots.ItemAt(Sword), Is.Null);
            Assert.That(Mask(s), Is.Zero);
        }

        [Test]
        public void HandsReportChangesOnlyWhenTheyChange()
        {
            var hands = new HeroHands();
            var changes = new List<string>();
            hands.Changed += () => changes.Add(hands.Left + "|" + hands.Right);
            hands.SetLeft("It_Torch");
            hands.SetLeft("It_Torch");
            hands.SetRight("");
            hands.SetRight("It_Scroll");
            Assert.That(hands.Holds("It_Torch") && hands.Holds("It_Scroll"), Is.True);
            hands.Clear();
            hands.Clear();
            hands.SetWeapon(HandWeapon.Ranged);
            hands.SetWeapon(HandWeapon.Ranged);
            hands.Clear();
            Assert.That(changes, Is.EqualTo(new[] { "It_Torch|", "It_Torch|It_Scroll", "|", "|", "|" }));
            Assert.That(hands.Holds(null), Is.False);
        }
    }
}
