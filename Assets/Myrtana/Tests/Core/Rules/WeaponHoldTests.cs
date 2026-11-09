using Myrtana.Core;
using Myrtana.Rules;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>How weapons sit in the hands and on the body (WeaponHold; G3 use-type grouping of gCInventorySlot::GetBone).</summary>
    public class WeaponHoldTests
    {
        [TestCase(WeaponClass.OneHanded, "1H")]
        [TestCase(WeaponClass.TwoHanded, "2H")]
        [TestCase(WeaponClass.Axe, "2H")]          // orc axes: orcs only have None_2H melee motions
        [TestCase(WeaponClass.Staff, "Staff")]
        [TestCase(WeaponClass.Halberd, "Staff")]
        [TestCase(WeaponClass.Tool, "Staff")]
        [TestCase(WeaponClass.Bow, "Bow")]
        [TestCase(WeaponClass.Crossbow, "CrossBow")]
        [TestCase(WeaponClass.Fist, "Fist")]
        public void MotionField(WeaponClass weapon, string field) => Assert.AreEqual(field, WeaponHold.MotionField(weapon));

        [Test]
        public void OrcAxeTemplatesHoldTwoHanded()
        {
            // It_Axe_Orc_01 / It_Axe_OrcWaraxe_01 / It_Axe_OrcSword_01 carry use type "Axe"
            Assert.AreEqual(WeaponClass.Axe, CombatLoadout.WeaponClassOf("Axe"));
            Assert.AreEqual("2H", WeaponHold.MotionField(CombatLoadout.WeaponClassOf("Axe").Value));
        }

        [TestCase(WeaponClass.OneHanded, "Slot_RightHand_Weapon")]
        [TestCase(WeaponClass.Axe, "Slot_RightHand_Weapon")]
        [TestCase(WeaponClass.Staff, "Slot_RightHand_Weapon")]
        [TestCase(WeaponClass.Bow, "Slot_LeftHand_Weapon")]
        [TestCase(WeaponClass.Crossbow, "Slot_LeftHand_Weapon")]
        public void HandSlot(WeaponClass weapon, string slot) => Assert.AreEqual(slot, WeaponHold.HandSlot(weapon));

        [TestCase(WeaponClass.OneHanded, "Slot_MeleeBackRight")]
        [TestCase(WeaponClass.TwoHanded, "Slot_AxeBack")]
        [TestCase(WeaponClass.Axe, "Slot_AxeBack")]
        [TestCase(WeaponClass.Staff, "Slot_StaffBack")]
        [TestCase(WeaponClass.Halberd, "Slot_StaffBack")]
        [TestCase(WeaponClass.Bow, "Slot_Bow")]
        [TestCase(WeaponClass.Crossbow, "Slot_CrossBow")]
        [TestCase(WeaponClass.Fist, null)]
        public void CarrySlot(WeaponClass weapon, string slot) => Assert.AreEqual(slot, WeaponHold.CarrySlot(weapon));

        static ItemDef Item(string id, string kind) =>
            new ItemDef(id, new TextId("FO_" + id), default, ItemCategory.Weapon, kind, EquipSlot.MeleeWeapon, 0, 0, null, null,
                        null, null, null, null, null, null, default);

        static string Worn(CombatLoadout loadout, ItemDef drawn, bool lost = false)
        {
            var list = new System.Collections.Generic.List<WeaponHold.Worn>();
            WeaponHold.WornItems(loadout, drawn, lost, list);
            return string.Join(" ", list);
        }

        [Test]
        public void OrcGuardCarriesAxeAndCrossbow()
        {
            var axe = Item("It_Axe_OrcWaraxe_01", "Axe");
            var xbow = Item("It_Crossbow_Orc", "CrossBow");
            var loadout = CombatLoadout.From(new[] { axe, xbow });
            Assert.AreEqual("It_Axe_OrcWaraxe_01@Slot_AxeBack It_Crossbow_Orc@Slot_CrossBow", Worn(loadout, null));
            // axe drawn: only the crossbow stays on the back; crossbow drawn: the axe stays
            Assert.AreEqual("It_Crossbow_Orc@Slot_CrossBow", Worn(loadout, axe));
            Assert.AreEqual("It_Axe_OrcWaraxe_01@Slot_AxeBack", Worn(loadout, xbow));
        }

        [Test]
        public void ShieldOnBackUnlessOneHandedDrawn()
        {
            var sword = Item("It_1H_Sword_01", "OneHanded");
            var shield = Item("It_Shield_Skull_01", "Shield");
            var loadout = CombatLoadout.From(new[] { shield, sword });
            Assert.AreEqual(shield, loadout.ShieldItem);
            Assert.AreEqual("It_1H_Sword_01@Slot_MeleeBackRight It_Shield_Skull_01@Slot_ShieldBack", Worn(loadout, null));
            Assert.AreEqual("It_Shield_Skull_01@Slot_LeftHand_Shield", Worn(loadout, sword));
            // fix-192: a two-handed weapon goes without the shield, drawn or not
            var axe = Item("It_Axe", "Axe");
            var heavy = CombatLoadout.From(new[] { axe, shield });
            Assert.AreEqual("", Worn(heavy, axe));
            Assert.AreEqual("It_Axe@Slot_AxeBack", Worn(heavy, null));
        }

        [Test]
        public void DroppedOrTakenWeaponIsNotCarried()
        {
            var sword = Item("It_1H_Sword_01", "OneHanded");
            var shield = Item("It_Shield_Wood_01", "Shield");
            var loadout = CombatLoadout.From(new[] { sword, shield });
            Assert.AreEqual("It_Shield_Wood_01@Slot_ShieldBack", Worn(loadout, null, lost: true));
            var taken = loadout.Without(new[] { "It_1H_Sword_01" });
            Assert.AreEqual("It_Shield_Wood_01@Slot_ShieldBack", Worn(taken, null));
            Assert.AreEqual(shield, taken.ShieldItem);
            // fix-164: the looted shield leaves the body too
            var looted = loadout.Without(new[] { "It_1H_Sword_01", "It_Shield_Wood_01" });
            Assert.AreEqual("", Worn(looted, null));
            Assert.IsNull(looted.ShieldItem);
            Assert.IsFalse(looted.Shield);
        }

        static string WornLying(CombatLoadout loadout, ItemDef drawn, params string[] lying)
        {
            var list = new System.Collections.Generic.List<WeaponHold.Worn>();
            WeaponHold.WornItems(loadout, drawn, i => System.Array.IndexOf(lying, i.Id) >= 0, list);
            return string.Join(" ", list);
        }

        [Test]
        public void LyingItemsAreNotShown_PerItem()
        {
            // fix-149: DropHandItems drops sword and shield; each lying item leaves the body, the rest stays
            var sword = Item("It_1H_Sword_Rust_01", "OneHanded");
            var shield = Item("It_Shield_Wood_03", "Shield");
            var bow = Item("It_Bow_01", "Bow");
            var loadout = CombatLoadout.From(new[] { sword, shield, bow });
            Assert.AreEqual("It_1H_Sword_Rust_01@Slot_MeleeBackRight It_Bow_01@Slot_Bow It_Shield_Wood_03@Slot_ShieldBack", WornLying(loadout, null));
            Assert.AreEqual("It_Bow_01@Slot_Bow", WornLying(loadout, null, "It_1H_Sword_Rust_01", "It_Shield_Wood_03"));
            Assert.AreEqual("It_1H_Sword_Rust_01@Slot_MeleeBackRight It_Shield_Wood_03@Slot_ShieldBack", WornLying(loadout, null, "It_Bow_01"));
            Assert.AreEqual("It_Bow_01@Slot_Bow It_Shield_Wood_03@Slot_ShieldBack", WornLying(loadout, null, "It_1H_Sword_Rust_01"),
                            "the melee weapon lying: the shield goes to the back (as the bool overload)");
            Assert.AreEqual(Worn(loadout, null, lost: true), WornLying(loadout, null, "It_1H_Sword_Rust_01"));
            Assert.AreEqual(Worn(loadout, sword), WornLying(loadout, sword));
        }

        [Test]
        public void DropHandItems_LeftHand()
        {
            var sword = Item("It_1H_Sword_01", "OneHanded");
            var shield = Item("It_Shield_Wood_01", "Shield");
            var axe = Item("It_Axe", "Axe");
            var bow = Item("It_Bow_01", "Bow");
            var withShield = CombatLoadout.From(new[] { sword, shield, bow });
            Assert.AreEqual(shield, WeaponHold.LeftHandDrop(withShield, sword), "the shield is in the left hand beside the sword");
            Assert.IsNull(WeaponHold.LeftHandDrop(withShield, null), "sheathed: nothing in the hands");
            Assert.IsNull(WeaponHold.LeftHandDrop(withShield, bow), "the bow is the hand weapon itself");
            Assert.IsNull(WeaponHold.LeftHandDrop(CombatLoadout.From(new[] { axe, shield }), axe), "no shield with a two-handed weapon");
            var dual = CombatLoadout.From(new[] { sword, Item("It_1H_Sword_01", "OneHanded") }); // EquipWeaponry: a pair of one blade
            Assume.That(dual.DualWield, Is.True);
            Assert.AreEqual(dual.OffHandItem, WeaponHold.LeftHandDrop(dual, sword), "a dual wielder's second blade");
            Assert.IsNull(WeaponHold.LeftHandDrop(null, sword));
        }

        [Test]
        public void FistsShowNothing()
        {
            Assert.AreEqual("", Worn(CombatLoadout.Fists, null));
            Assert.AreEqual("", Worn(null, null));
        }
    }
}
