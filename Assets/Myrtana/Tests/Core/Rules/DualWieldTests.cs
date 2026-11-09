using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// G3 dual wielding (fix-32, Docs/combat.md §1.7): the hand choice of the quick slot and the inventory (CP 1.75
    /// FUN_10002550), the off-hand slot (gESlot_BackLeft), the blades per move, the NPC weaponry split (EquipWeaponry), the
    /// power attack's level and multiplier, the visuals' slots, saves and the equipped marker.
    /// </summary>
    public class DualWieldTests
    {
        const string Main = "It_1H_Sword_01", Second = "It_1H_AssSword_01", Axe = "It_2H_Axe_01", Shield = "It_Shield_01";

        static SessionFixture Fixture() => new SessionFixture()
            .Add(RulesSchema.ItemFolder,
                 ItemTests.Item(Main, 100, EquipSlot.MeleeWeapon, "OneHanded"),
                 ItemTests.Item(Second, 100, EquipSlot.MeleeWeapon, "OneHanded", new[] { new AttributeRequirement("DEX", 50) }),
                 ItemTests.Item(Axe, 100, EquipSlot.MeleeWeapon, "Axe"),
                 ItemTests.Item(Shield, 100, EquipSlot.Shield, "Shield"));

        static GameSession Hero(SessionFixture f, bool perk = true)
        {
            var s = f.NewGame();
            var hero = s.Items.Inventories.Of(ActorId.Player);
            foreach (var id in new[] { Main, Second, Axe, Shield }) hero.Add(id, 1);
            s.Hero.Stats.Set("DEX", new StatValue(100, 0));
            if (perk) s.Progression.LearnSkill(DualWieldRules.Perk);
            return s;
        }

        // ------------------------------------------------------------------------------------------- equipping

        [Test]
        public void WithThePerk_ASecondOneHandedWeaponGoesToTheOffHandSlot()
        {
            using var f = Fixture();
            var s = Hero(f);
            var eq = s.Items.Equipment;
            Assert.That(eq.Equip(Main), Is.EqualTo(EquipResult.Done));
            Assert.That(eq.Equip(Second), Is.EqualTo(EquipResult.Done));
            Assert.That(eq.In(WornSlot.MeleeWeapon), Is.EqualTo(Main), "the main weapon stays (gESlot_BackRight)");
            Assert.That(eq.In(WornSlot.OffHand), Is.EqualTo(Second), "gESlot_BackLeft");
            Assert.That(eq.DualWieldReady, Is.True);
        }

        [Test]
        public void WithoutThePerk_ASecondOneHandedWeaponReplacesTheMainOne()
        {
            using var f = Fixture();
            var s = Hero(f, perk: false);
            var eq = s.Items.Equipment;
            eq.Equip(Main);
            eq.Equip(Second);
            Assert.That(eq.In(WornSlot.MeleeWeapon), Is.EqualTo(Second));
            Assert.That(eq.In(WornSlot.OffHand), Is.Null);
            Assert.That(eq.EquipOffHand(Main), Is.EqualTo(EquipResult.NotEquippable), "the quick-slot path needs the perk too");
        }

        [Test]
        public void ShieldAndOffHandWeaponShareBackLeft()
        {
            using var f = Fixture();
            var s = Hero(f);
            var eq = s.Items.Equipment;
            eq.Equip(Main);
            eq.Equip(Shield);
            eq.Equip(Second);
            Assert.That(eq.In(WornSlot.Shield), Is.Null, "the second blade takes the shield's slot");
            Assert.That(eq.In(WornSlot.OffHand), Is.EqualTo(Second));
            eq.Equip(Shield);
            Assert.That(eq.In(WornSlot.OffHand), Is.Null);
            Assert.That(eq.In(WornSlot.Shield), Is.EqualTo(Shield));
        }

        [Test]
        public void TwoUnitsOfTheSameSword_AreMainAndOffHand()
        {
            using var f = Fixture();
            var s = Hero(f);
            s.Items.Inventories.Of(ActorId.Player).Add(Main, 1); // two now
            var eq = s.Items.Equipment;
            eq.Equip(Main);
            Assert.That(eq.Equip(Main), Is.EqualTo(EquipResult.Done), "G3 splits the stack");
            Assert.That(eq.In(WornSlot.OffHand), Is.EqualTo(Main));
            Assert.That(eq.Equip(Main), Is.EqualTo(EquipResult.NotCarried), "no third unit");
        }

        [Test]
        public void OffHandNeedsItsRequirementsAndAOneHandedMain()
        {
            using var f = Fixture();
            var s = Hero(f);
            var eq = s.Items.Equipment;
            eq.Equip(Axe);
            Assert.That(eq.Equip(Second), Is.EqualTo(EquipResult.Done));
            Assert.That(eq.In(WornSlot.MeleeWeapon), Is.EqualTo(Second), "beside an axe it is the main weapon");
            eq.Equip(Main);
            Assert.That(eq.In(WornSlot.OffHand), Is.EqualTo(Main));
            eq.Unequip(WornSlot.OffHand);
            s.Hero.Stats.Set("DEX", new StatValue(10, 0));
            Assert.That(eq.EquipOffHand(Second), Is.Not.EqualTo(EquipResult.Done));
        }

        [Test]
        public void OffHandWeapon_IsMarkedOnTheQuickBarAndInTheInventory()
        {
            using var f = Fixture();
            var s = Hero(f);
            s.Items.QuickSlots.Assign(0, Main);
            s.Items.QuickSlots.Assign(1, Second);
            s.Items.Equipment.Equip(Main);
            s.Items.Equipment.Equip(Second);
            int mask = EquippedMarker.QuickBarMask(s.Items.QuickSlots, i => EquippedMarker.IsMarked(i, s.Items.Equipment, s.Items.Hands));
            Assert.That(mask, Is.EqualTo(0b11));
            Assert.That(ItemLists.Of(s, ActorId.Player).SelectMany(g => g.Rows).Single(r => r.Item == Second).Marked, Is.True);
        }

        [Test]
        public void OffHandWeapon_SurvivesSaveAndLoad()
        {
            using var f = Fixture();
            var s = Hero(f);
            s.Items.Equipment.Equip(Main);
            s.Items.Equipment.Equip(Second);
            var loaded = f.Reload(s);
            Assert.That(loaded.Items.Equipment.In(WornSlot.MeleeWeapon), Is.EqualTo(Main));
            Assert.That(loaded.Items.Equipment.In(WornSlot.OffHand), Is.EqualTo(Second));
            Assert.That(loaded.Items.Equipment.DualWieldReady, Is.True);
        }

        // ------------------------------------------------------------------------------------------- the hand choice

        // FUN_10002550 case 2 (OnPlayerUseQuickSlot): isOffHand, isMain, leftHeld, mainDrawn, perk, main kind, off-hand worn.
        [TestCase(false, false, false, true, true, "OneHanded", false, OneHandedUse.DrawLeft, TestName = "Quick: another 1H while the main is drawn → left hand")]
        [TestCase(false, false, false, false, true, "OneHanded", false, OneHandedUse.DrawBoth, TestName = "Quick: another 1H while sheathed → both")]
        [TestCase(false, false, false, true, false, "OneHanded", false, OneHandedUse.Main, TestName = "Quick: no perk → ordinary switch")]
        [TestCase(false, false, false, true, true, "Axe", false, OneHandedUse.Main, TestName = "Quick: main not 1H → ordinary switch")]
        [TestCase(true, false, true, true, true, "OneHanded", true, OneHandedUse.PutAwayLeft, TestName = "Quick: off-hand key while held → left away")]
        [TestCase(true, false, false, true, true, "OneHanded", true, OneHandedUse.DrawLeft, TestName = "Quick: off-hand key, left away → left again")]
        [TestCase(true, false, false, false, true, "OneHanded", true, OneHandedUse.DrawBoth, TestName = "Quick: off-hand key while sheathed → both")]
        [TestCase(false, true, true, true, true, "OneHanded", true, OneHandedUse.Holster, TestName = "Quick: main key while drawn → holster all")]
        [TestCase(false, true, false, false, true, "OneHanded", true, OneHandedUse.DrawBoth, TestName = "Quick: main key while sheathed → both")]
        [TestCase(false, true, false, false, true, "OneHanded", false, OneHandedUse.Main, TestName = "Quick: main key, no off-hand → ordinary draw")]
        public void QuickSlot_AsG3HandChoice(bool isOffHand, bool isMain, bool leftHeld, bool mainDrawn, bool perk, string mainKind, bool offWorn,
                                            OneHandedUse expected) =>
            Assert.That(DualWieldRules.QuickSlot(isOffHand, isMain, leftHeld, mainDrawn, perk, mainKind, offWorn), Is.EqualTo(expected));

        [Test]
        public void InventoryEquip_SlotChoice()
        {
            Assert.That(DualWieldRules.EquipSlot(true, "OneHanded", false), Is.EqualTo(OneHandedUse.OffHand));
            Assert.That(DualWieldRules.EquipSlot(true, "OneHanded", true), Is.EqualTo(OneHandedUse.Main), "the same stack");
            Assert.That(DualWieldRules.EquipSlot(false, "OneHanded", false), Is.EqualTo(OneHandedUse.Main));
            Assert.That(DualWieldRules.EquipSlot(true, null, false), Is.EqualTo(OneHandedUse.Main));
            Assert.That(DualWieldRules.DrawsOffHand(WeaponClass.OneHanded, true, false), Is.True);
            Assert.That(DualWieldRules.DrawsOffHand(WeaponClass.OneHanded, true, true), Is.False, "the torch keeps the left hand");
            Assert.That(DualWieldRules.DrawsOffHand(WeaponClass.Axe, true, false), Is.False);
        }

        // ------------------------------------------------------------------------------------------- blades and damage

        [TestCase(CombatAction.Attack, 0, MeleeBlades.Both)]
        [TestCase(CombatAction.Attack, 1, MeleeBlades.Both)]
        [TestCase(CombatAction.PowerAttack, 0, MeleeBlades.Both)]
        [TestCase(CombatAction.SimpleWhirl, 1, MeleeBlades.Both)]
        [TestCase(CombatAction.QuickAttackRight, 0, MeleeBlades.Right)]
        [TestCase(CombatAction.QuickAttackLeft, 0, MeleeBlades.Right)]
        [TestCase(CombatAction.QuickAttackLeft, 1, MeleeBlades.Left)]
        [TestCase(CombatAction.PierceAttack, 1, MeleeBlades.Left)]
        [TestCase(CombatAction.PierceAttack, 0, MeleeBlades.Right)]
        [TestCase(CombatAction.FinishingAttack, 0, MeleeBlades.Right)]
        [TestCase(CombatAction.GetUpAttack, 0, MeleeBlades.Right)]
        public void Blades_AsTheG3AttackCommands(CombatAction action, int pose, MeleeBlades expected)
        {
            Assert.That(DualWieldRules.Blades(action, pose, dual: true), Is.EqualTo(expected));
            Assert.That(DualWieldRules.Blades(action, pose, dual: false), Is.EqualTo(MeleeBlades.Right));
        }

        [Test]
        public void DualPowerAttack_LevelTwoSingleDamagePerBlade_AndTheDualPerks()
        {
            var c = new Combatant { IsPlayer = true, Weapon = WeaponClass.OneHanded, DualWield = true, Action = CombatAction.PowerAttack,
                                    SecondPose = DualWieldRules.SecondPose(DualWieldRules.PowerAttackStatePosition) };
            Assert.That(c.SecondPose, Is.False, "the state is at position 1 while _AI_PowerAttack runs");
            Assert.That(CombatStats.ActionLevel(c), Is.EqualTo(2));
            Assert.That(CombatStats.SkillPerks(c), Is.EqualTo(("Perk_1H1H_2", "Perk_1H1H_SecretBonus")));
            c.DualWield = false;
            Assert.That(CombatStats.ActionLevel(c), Is.EqualTo(4));
            Assert.That(CombatStats.SkillPerks(c), Is.EqualTo(("Perk_1H_2", "Perk_1H_3")));
        }

        // ------------------------------------------------------------------------------------------- NPCs and visuals

        [Test]
        public void EquipWeaponry_AOneHandedStackOfTwoIsADualWielder()
        {
            Assert.That(DualWieldRules.DualWeaponOf(new[] { ("It_1H_AssSword_01", "OneHanded", 1), ("It_1H_AssSword_01", "OneHanded", 1) }),
                        Is.EqualTo("It_1H_AssSword_01"), "TS_Weaponry_Ass_1H_1H: merged to two");
            Assert.That(DualWieldRules.DualWeaponOf(new[] { ("It_1H_AssDagger_01", "OneHanded", 2) }), Is.EqualTo("It_1H_AssDagger_01"));
            Assert.That(DualWieldRules.DualWeaponOf(new[] { ("It_1H_Sword_01", "OneHanded", 1), ("It_1H_Club_01", "OneHanded", 1) }), Is.Null,
                        "two different swords: the second replaces the first in BackRight");
            Assert.That(DualWieldRules.DualWeaponOf(new[] { ("It_Arrow", "Arrow", 20) }), Is.Null);
        }

        static ItemDef Weapon(string id, string kind, EquipSlot slot = EquipSlot.MeleeWeapon) => ItemTests.Item(id, 1, slot, kind);

        [Test]
        public void Loadout_ReadsTheSecondListingAsTheLeftBlade_AndShowsItOnTheRightSlot()
        {
            var sword = Weapon(Second, "OneHanded");
            var dual = CombatLoadout.From(new[] { sword, sword });
            Assert.That(dual.DualWield, Is.True);
            Assert.That(dual.OffHandItem, Is.SameAs(sword));
            Assert.That(CombatLoadout.From(new[] { sword, Weapon(Main, "OneHanded") }).DualWield, Is.False);

            var worn = new List<WeaponHold.Worn>();
            WeaponHold.WornItems(dual, null, false, worn);
            Assert.That(worn.Select(w => w.Slot), Is.EquivalentTo(new[] { "Slot_MeleeBackRight", "Slot_MeleeBackLeft" }));
            WeaponHold.WornItems(dual, sword, false, worn);
            Assert.That(worn.Select(w => w.Slot), Is.EquivalentTo(new[] { "Slot_LeftHand_Weapon" }), "drawn: the left blade in the left hand");
            WeaponHold.WornItems(dual, sword, true, worn);
            Assert.That(worn, Is.Empty, "the dropped main weapon: no dual stance");
            Assert.That(dual.Without(new[] { Second }).DualWield, Is.False);
        }

        [Test]
        public void MotionSpeed_DualPowerSwingAndHeavyParadeStumble()
        {
            var options = new RuleOptions(alternativeBalancing: false, extendedAI: false);
            var q = new MotionSpeedQuery { Motion = CombatMotion.PowerAttack, Species = "Human", RightHand = WeaponClass.OneHanded, DualOneHanded = true };
            Assert.That(MotionSpeedRules.Factor(q, options), Is.EqualTo(0.9f));
            q.DualOneHanded = false;
            Assert.That(MotionSpeedRules.Factor(q, options), Is.EqualTo(1f));
        }
    }
}
