using Myrtana.Core;
using Myrtana.Rules;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>par-magic: the staff in the magic stance (G3 PS_Magic.cpp, FUN_10002550, OnMagicHandleInput, HasStaffMagicParade).</summary>
    public class StaffParadeTests
    {
        [Test]
        public void ReadyingASpell_MovesTheDrawnStaffIntoTheLeftHand()
        {
            Assert.That(StaffParade.StaffInLeftHand(WeaponClass.Staff, false), Is.True);
            Assert.That(StaffParade.StaffInLeftHand(null, true), Is.True, "switching spells keeps the staff");
            Assert.That(StaffParade.StaffInLeftHand(WeaponClass.OneHanded, false), Is.False);
            Assert.That(StaffParade.StaffInLeftHand(null, false), Is.False, "from the sheathed stance: no staff");
        }

        [Test]
        public void StaffKey_JoinsADrawnSpell()
        {
            Assert.That(StaffParade.StaffKeyJoinsSpell(WeaponClass.Staff, true), Is.True);
            Assert.That(StaffParade.StaffKeyJoinsSpell(WeaponClass.Staff, false), Is.False);
            Assert.That(StaffParade.StaffKeyJoinsSpell(WeaponClass.TwoHanded, true), Is.False);
        }

        [Test]
        public void SecondaryKey_BlocksOnlyWithAStaffAndNotWhileSummoning()
        {
            Assert.That(StaffParade.RaisesBlock(staffInLeftHand: true, summoning: false), Is.True);
            Assert.That(StaffParade.RaisesBlock(staffInLeftHand: false, summoning: false), Is.False, "no staff: RMB does nothing (never aborts)");
            Assert.That(StaffParade.RaisesBlock(staffInLeftHand: true, summoning: true), Is.False);
        }

        [Test]
        public void ExtendedAI_EndsTheBlockAfter1501Ms()
        {
            Assert.That(StaffParade.EndsByItself(true, 1.500), Is.False);
            Assert.That(StaffParade.EndsByItself(true, 1.501), Is.True);
            Assert.That(StaffParade.EndsByItself(false, 10), Is.False);
        }

        [Test]
        public void MagicBlock_NeedsThePerkForTheHeroAndTheMageClassForNpcs()
        {
            Assert.That(StaffParade.BlocksMagic(true, true, p => p == StaffParade.Perk, false), Is.True);
            Assert.That(StaffParade.BlocksMagic(true, true, p => false, true), Is.False);
            Assert.That(StaffParade.BlocksMagic(true, false, null, true), Is.True);
            Assert.That(StaffParade.BlocksMagic(false, false, null, true), Is.False);
        }

        [Test]
        public void QuickSlots_CanAssignGate()
        {
            var bag = new Inventory();
            bag.Add("It_Spell_Fireball", 1);
            bag.Add("It_Apple", 1);
            var slots = new QuickSlots(bag) { CanAssign = item => item != "It_Spell_Fireball" };
            Assert.That(slots.Assign(0, "It_Spell_Fireball"), Is.False);
            Assert.That(slots.Assign(0, "It_Apple"), Is.True);
        }
    }
}
