using Myrtana.Core;
using Myrtana.Rules;
using NUnit.Framework;

namespace Myrtana.Tests
{
    public class QuickSlotsTests
    {
        Inventory _hero;
        QuickSlots _slots;
        int _changes;

        [SetUp]
        public void SetUp()
        {
            _hero = new Inventory();
            _hero.Add("Apple", 3);
            _hero.Add("Sword", 1);
            _slots = new QuickSlots(_hero);
            _slots.Changed += () => _changes++;
            _changes = 0;
        }

        [TestCase("OneHanded", true)]
        [TestCase("TwoHanded", true)]
        [TestCase("Axe", true)]
        [TestCase("Halberd", true)]
        [TestCase("Staff", true)]
        [TestCase("Bow", true)]
        [TestCase("CrossBow", true)]
        [TestCase("Arrow", true)]
        [TestCase("Bolt", true)]
        [TestCase("Shield", true)]
        [TestCase("Cast", true)]
        [TestCase("Potion", true)]
        [TestCase("Plant", true)]
        [TestCase("Meat", true)]
        [TestCase("Bread", true)]
        [TestCase("Bottle", true)]
        [TestCase("Torch", true)]
        [TestCase("Smoke", true)]
        [TestCase("Letter", true)]
        [TestCase("Armor", false)]
        [TestCase("Body", false)]
        [TestCase("Helmet", false)]
        [TestCase("Ring", false)]
        [TestCase("Amulet", false)]
        [TestCase("Key", false)]
        [TestCase("Lockpick", false)]
        [TestCase("Map", false)]
        [TestCase("Book", false)]
        [TestCase("Valuable", false)]
        [TestCase("TrophyTeeth", false)]
        [TestCase("Pickaxe", false)]
        [TestCase("Lute", false)]
        [TestCase("None", false)]
        public void G3_OnPlayerAssignStackKey_UseTypes_FixSeventyFive(string kind, bool allowed)
        {
            Assert.That(QuickSlotRules.AllowsKind(kind), Is.EqualTo(allowed));
            var slots = new QuickSlots(_hero) { CanAssign = item => QuickSlotRules.AllowsKind(item == "Sword" ? kind : "Fruit") };
            Assert.That(slots.Assign(4, "Sword"), Is.EqualTo(allowed));
            Assert.That(slots.ItemAt(4), Is.EqualTo(allowed ? "Sword" : null), "a refused stack leaves the slot as it was");
        }

        [Test]
        public void Assign_Refused_KeepsTheSlotsItem()
        {
            var slots = new QuickSlots(_hero) { CanAssign = item => item == "Apple" };
            Assert.That(slots.Assign(1, "Apple"), Is.True);
            Assert.That(slots.Assign(1, "Sword"), Is.False);
            Assert.That(slots.ItemAt(1), Is.EqualTo("Apple"));
        }

        [Test]
        public void Assign_PutsCarriedItemIntoSlot()
        {
            Assert.That(_slots.Assign(2, "Apple"), Is.True);
            Assert.That(_slots.ItemAt(2), Is.EqualTo("Apple"));
            Assert.That(_slots.SlotOf("Apple"), Is.EqualTo(2));
            Assert.That(_changes, Is.EqualTo(1));
        }

        [Test]
        public void Assign_RefusesItemsTheHeroLacksAndBadSlots()
        {
            Assert.That(_slots.Assign(0, "Bread"), Is.False);
            Assert.That(_slots.Assign(10, "Apple"), Is.False);
            Assert.That(_slots.Assign(-1, "Apple"), Is.False);
            Assert.That(_changes, Is.Zero);
        }

        [Test]
        public void Assign_MovesAnItemThatSitsInAnotherSlot()
        {
            _slots.Assign(0, "Apple");
            _slots.Assign(5, "Apple");
            Assert.That(_slots.ItemAt(0), Is.Null);
            Assert.That(_slots.ItemAt(5), Is.EqualTo("Apple"));
        }

        [Test]
        public void Assign_ReplacesTheSlotsPreviousItem()
        {
            _slots.Assign(0, "Apple");
            _slots.Assign(0, "Sword");
            Assert.That(_slots.ItemAt(0), Is.EqualTo("Sword"));
            Assert.That(_slots.SlotOf("Apple"), Is.EqualTo(-1));
        }

        [Test]
        public void Slot_EmptiesWhenTheLastUnitIsGone()
        {
            _slots.Assign(1, "Apple");
            _hero.Remove("Apple", 2);
            Assert.That(_slots.ItemAt(1), Is.EqualTo("Apple"), "one apple left");
            _changes = 0;
            _hero.Remove("Apple", 1);
            Assert.That(_slots.ItemAt(1), Is.Null);
            Assert.That(_changes, Is.EqualTo(1));
        }

        [Test]
        public void Clear_EmptiesSlot()
        {
            _slots.Assign(3, "Sword");
            _slots.Clear(3);
            Assert.That(_slots.ItemAt(3), Is.Null);
        }

        [TestCase(1, 0)]
        [TestCase(9, 8)]
        [TestCase(0, 9)]
        public void Digits_MapToSlotsLikeTheBar(int digit, int slot)
        {
            Assert.That(QuickSlots.SlotOfDigit(digit), Is.EqualTo(slot));
            Assert.That(QuickSlots.DigitOfSlot(slot), Is.EqualTo(digit));
        }

        [Test]
        public void SlotOfDigit_RejectsNonDigits() => Assert.That(QuickSlots.SlotOfDigit(10), Is.EqualTo(-1));
    }
}
