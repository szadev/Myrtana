using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// fix-192 ([owner] build 22d): a shield is not worn beside a two-handed weapon (2H sword, axe, staff, halberd), and it is
    /// hidden (not in the hand, not on the back) while a two-handed weapon, bow or crossbow is drawn; for the hero and NPCs,
    /// across save and load, and no longer tinted blue as equipped.
    /// </summary>
    public class ShieldTwoHandedTests
    {
        const string Sword = "It_1H_Sword_01", TwoHander = "It_2H_Sword_01", Axe = "It_2H_Axe_01", Staff = "It_Staff_01",
                     Bow = "It_Bow_01", Shield = "It_Shield_01";

        static SessionFixture Fixture() => new SessionFixture()
            .Add(RulesSchema.ItemFolder,
                 ItemTests.Item(Sword, 100, EquipSlot.MeleeWeapon, "OneHanded"),
                 ItemTests.Item(TwoHander, 100, EquipSlot.MeleeWeapon, "TwoHanded"),
                 ItemTests.Item(Axe, 100, EquipSlot.MeleeWeapon, "Axe"),
                 ItemTests.Item(Staff, 100, EquipSlot.MeleeWeapon, "Staff"),
                 ItemTests.Item(Bow, 100, EquipSlot.RangedWeapon, "Bow"),
                 ItemTests.Item(Shield, 100, EquipSlot.Shield, "Shield"));

        static GameSession Hero(SessionFixture f)
        {
            var s = f.NewGame();
            var hero = s.Items.Inventories.Of(ActorId.Player);
            foreach (var id in new[] { Sword, TwoHander, Axe, Staff, Bow, Shield }) hero.Add(id, 1);
            return s;
        }

        [TestCase(TwoHander)]
        [TestCase(Axe)]
        [TestCase(Staff)]
        public void EquippingATwoHandedWeapon_TakesTheShieldOff(string weapon)
        {
            using var f = Fixture();
            var s = Hero(f);
            var eq = s.Items.Equipment;
            eq.Equip(Sword);
            eq.Equip(Shield);
            Assert.That(eq.In(WornSlot.Shield), Is.EqualTo(Shield));
            Assert.That(eq.Equip(weapon), Is.EqualTo(EquipResult.Done));
            Assert.That(eq.In(WornSlot.MeleeWeapon), Is.EqualTo(weapon));
            Assert.That(eq.In(WornSlot.Shield), Is.Null);
            Assert.That(EquippedMarker.IsMarked(Shield, eq, s.Items.Hands), Is.False, "no blue equipped tint on the shield");
            Assert.That(s.Items.Inventories.Of(ActorId.Player).Has(Shield, 1), Is.True, "back in the inventory");
        }

        [Test]
        public void EquippingAShield_TakesATwoHandedWeaponOff_ButKeepsAOneHandedOne()
        {
            using var f = Fixture();
            var s = Hero(f);
            var eq = s.Items.Equipment;
            eq.Equip(Axe);
            Assert.That(eq.Equip(Shield), Is.EqualTo(EquipResult.Done));
            Assert.That(eq.In(WornSlot.Shield), Is.EqualTo(Shield));
            Assert.That(eq.In(WornSlot.MeleeWeapon), Is.Null);
            eq.Equip(Sword);
            Assert.That(eq.In(WornSlot.Shield), Is.EqualTo(Shield), "a one-handed weapon goes with the shield");
            Assert.That(eq.In(WornSlot.MeleeWeapon), Is.EqualTo(Sword));
        }

        [Test]
        public void ABow_KeepsTheShieldWorn()
        {
            using var f = Fixture();
            var s = Hero(f);
            var eq = s.Items.Equipment;
            eq.Equip(Sword);
            eq.Equip(Shield);
            eq.Equip(Bow);
            Assert.That(eq.In(WornSlot.Shield), Is.EqualTo(Shield), "own slot; hidden only while the bow is drawn");
            Assert.That(EquippedMarker.QuickMarkedWhileActive(WornSlot.Shield, HandWeapon.Ranged), Is.False, "not blue on the quick bar while the bow is drawn");
        }

        [Test]
        public void TheShieldStaysOffAfterSaveAndLoad()
        {
            using var f = Fixture();
            var s = Hero(f);
            s.Items.Equipment.Equip(Shield);
            s.Items.Equipment.Equip(TwoHander);
            var loaded = f.Reload(s);
            Assert.That(loaded.Items.Equipment.In(WornSlot.MeleeWeapon), Is.EqualTo(TwoHander));
            Assert.That(loaded.Items.Equipment.In(WornSlot.Shield), Is.Null);
        }

        [TestCase("TwoHanded", true)]
        [TestCase("Axe", true)]
        [TestCase("Staff", true)]
        [TestCase("Halberd", true)]
        [TestCase("Broom", true)]
        [TestCase("OneHanded", false)]
        [TestCase("Fist", false)]
        [TestCase("Bow", false)]
        [TestCase("CrossBow", false)]
        [TestCase("Shield", false)]
        public void MeleeExcludesShield(string kind, bool excludes) => Assert.AreEqual(excludes, WeaponHold.MeleeExcludesShield(kind));

        [TestCase(WeaponClass.OneHanded, null, "Slot_ShieldBack")]
        [TestCase(WeaponClass.Fist, null, "Slot_ShieldBack")]
        [TestCase(WeaponClass.OneHanded, WeaponClass.OneHanded, "Slot_LeftHand_Shield")]
        [TestCase(WeaponClass.OneHanded, WeaponClass.Fist, "Slot_ShieldBack")]
        [TestCase(WeaponClass.OneHanded, WeaponClass.Bow, null)]
        [TestCase(WeaponClass.OneHanded, WeaponClass.Crossbow, null)]
        [TestCase(WeaponClass.Fist, WeaponClass.Bow, null)]
        [TestCase(WeaponClass.TwoHanded, null, null)]
        [TestCase(WeaponClass.Axe, WeaponClass.Axe, null)]
        [TestCase(WeaponClass.Staff, WeaponClass.Staff, null)]
        [TestCase(WeaponClass.Halberd, null, null)]
        public void ShieldSlot(WeaponClass main, WeaponClass? drawn, string slot) => Assert.AreEqual(slot, WeaponHold.ShieldSlot(main, drawn));

        static ItemDef Item(string id, string kind) =>
            new ItemDef(id, new TextId("FO_" + id), default, ItemCategory.Weapon, kind, EquipSlot.MeleeWeapon, 0, 0, null, null,
                        null, null, null, null, null, null, default);

        static string Worn(CombatLoadout loadout, ItemDef drawn)
        {
            var list = new System.Collections.Generic.List<WeaponHold.Worn>();
            WeaponHold.WornItems(loadout, drawn, (System.Func<ItemDef, bool>)null, list);
            return string.Join(" ", list);
        }

        [Test]
        public void Npc_ShieldHiddenWhileTheBowIsDrawn_AndWithATwoHandedWeapon()
        {
            var sword = Item("It_1H_Sword_01", "OneHanded");
            var shield = Item("It_Shield_Wood_01", "Shield");
            var bow = Item("It_Bow_01", "Bow");
            var archer = CombatLoadout.From(new[] { sword, shield, bow });
            Assert.AreEqual("It_1H_Sword_01@Slot_MeleeBackRight It_Bow_01@Slot_Bow It_Shield_Wood_01@Slot_ShieldBack", Worn(archer, null));
            Assert.AreEqual("It_Bow_01@Slot_Bow It_Shield_Wood_01@Slot_LeftHand_Shield", Worn(archer, sword));
            Assert.AreEqual("It_1H_Sword_01@Slot_MeleeBackRight", Worn(archer, bow), "bow drawn: no shield in the hand or on the back");
            var heavy = CombatLoadout.From(new[] { Item("It_2H_Sword_01", "TwoHanded"), shield });
            Assert.AreEqual("It_2H_Sword_01@Slot_AxeBack", Worn(heavy, null));
            Assert.AreEqual("", Worn(heavy, heavy.MeleeItem));
        }
    }
}
