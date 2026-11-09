using System.Linq;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// fix-93: skills a worn item activates (G3 ActivateSkill1/2, gCInventorySlot::OnApplyStats / OnUnApplyStats:
    /// ActivationCount ±1; IsSkillActive = learned || count &gt; 0) count in combat and in the skill list while the
    /// item is worn and go when it comes off; teachers still teach them (HasLearnedSkill). A worn-out stack (quality
    /// 0x100) applies half of its modifiers and no skill.
    /// </summary>
    public class ItemSkillTests
    {
        static readonly AttributeGain[] None = new AttributeGain[0];

        static ItemDef Def(string id, ItemCategory category, string kind, EquipSlot slot = EquipSlot.None, string[] bonuses = null,
                           AttributeGain[] modifiers = null, ItemDamage damage = null) =>
            new ItemDef(id, new TextId("FO_" + id), default, category, kind, slot, 100, 0, null, null,
                        null, null, slot != EquipSlot.None ? modifiers ?? None : null, null, null, damage, ItemFlags.None,
                        modifiers: modifiers ?? None, skillBonuses: bonuses);

        const string OrcSlayer = "It_1H_OrcSlayer_01", Body = "Body_Nomad";

        static SessionFixture Fixture() => new SessionFixture()
            .Add(RulesSchema.ItemFolder, new[]
            {
                Def("It_Gold", ItemCategory.Misc, "Valuable"),
                Def(OrcSlayer, ItemCategory.Weapon, "OneHanded", EquipSlot.MeleeWeapon, new[] { "Perk_OrcSlayer" },
                    damage: new ItemDamage("Blade", 50, new TextId("HUD_Damage_Blade"))),
                Def("It_1H_Plain", ItemCategory.Weapon, "OneHanded", EquipSlot.MeleeWeapon,
                    damage: new ItemDamage("Blade", 30, new TextId("HUD_Damage_Blade"))),
                Def(Body, ItemCategory.Armor, "Body", EquipSlot.Armor, new[] { "Perk_ResistHeat" },
                    new[] { new AttributeGain("PROT_BLADE", ModifierOp.AddToVal, 40) }),
                Def("It_Perk_OrcSlayer", ItemCategory.Skill, "None"), Def("It_Perk_ResistHeat", ItemCategory.Skill, "None"),
            })
            .Add(RulesSchema.SkillFolder, new SkillDef("Perk_OrcSlayer", "It_Perk_OrcSlayer", 5, null, null, "SkillCombat"),
                 new SkillDef("Perk_ResistHeat", "It_Perk_ResistHeat", 5, null, null, "SkillMisc"));

        [Test]
        public void TheWornOrcSlayer_ActivatesPerkOrcSlayer_InCombatAndTheSkillList_UntilItComesOff()
        {
            using var f = Fixture();
            var s = f.NewGame();
            s.Items.Inventories.Of(ActorId.Player).Add(OrcSlayer, 1);
            Assert.That(s.Hero.Knows("Perk_OrcSlayer"), Is.False);
            Assert.That(s.Items.Equipment.Equip(OrcSlayer), Is.EqualTo(EquipResult.Done));
            Assert.That(s.Hero.Knows("Perk_OrcSlayer"), Is.True, "G3 IsSkillActive: activated by the worn item");
            Assert.That(s.Hero.HasLearned("Perk_OrcSlayer"), Is.False, "not learned");
            Assert.That(s.Hero.IsActivatedByItem("Perk_OrcSlayer"), Is.True);
            Assert.That(s.Combat.PlayerCombatant(s.Hero).Has("Perk_OrcSlayer"), Is.True, "the resolver's +25 vs orcs");
            var sheet = SkillSheet.Of(s).Groups.Values.SelectMany(g => g).Single(k => k.Id == "Perk_OrcSlayer");
            Assert.That(sheet.Learned, Is.True, "the skill list shows it lit while worn");
            Assert.That(s.Hero.Skills, Does.Not.Contain("Perk_OrcSlayer"), "the save keeps learned skills only");
            s.Items.Equipment.UnequipItem(OrcSlayer);
            Assert.That(s.Hero.Knows("Perk_OrcSlayer"), Is.False, "taken off: gone (OnUnApplyStats)");
            Assert.That(s.Combat.PlayerCombatant(s.Hero).Has("Perk_OrcSlayer"), Is.False);
        }

        [Test]
        public void TheOrcSlayersPerk_AddsItsDamageAgainstOrcs()
        {
            using var f = Fixture();
            var s = f.NewGame();
            s.Items.Inventories.Of(ActorId.Player).Add(OrcSlayer, 1);
            var resolver = new CombatResolver(s.Combat.Rules, RuleOptions.Default, new SeededRandom(7));
            int Damage()
            {
                var hero = s.Combat.PlayerCombatant(s.Hero);
                hero.Weapon = WeaponClass.OneHanded;
                hero.Action = CombatAction.Attack;
                hero.Strength = 100;
                var orc = new Combatant { Id = new ActorId("Orc_01"), Species = "Orc", Humanoid = true, Level = 10, HitPoints = 1000,
                                          Stamina = 100, Weapon = WeaponClass.OneHanded, Protection = DamageTypeValues.All(0) };
                var hit = new HitContext { Attacker = hero, Victim = orc, Delivery = HitDelivery.Melee, DamageType = DamageType.Blade,
                                           DamageAmount = 50, PlayerLevel = 10, VictimIsTarget = true };
                return resolver.Resolve(hit).Damage;
            }
            int bare = Damage();
            s.Items.Equipment.Equip(OrcSlayer);
            Assert.That(Damage(), Is.GreaterThan(bare), "Perk_OrcSlayer from the worn sword: +25 base vs orcs (GetPlayerSkillDamageBonus)");
        }

        [Test]
        public void ALearnedSkill_StaysWhenTheItemComesOff_TwoItemsCountTwice()
        {
            using var f = Fixture();
            var s = f.NewGame();
            s.Items.Inventories.Of(ActorId.Player).Add(Body, 1);
            s.Items.Equipment.Equip(Body);
            Assert.That(s.Hero.Knows("Perk_ResistHeat"), Is.True, "the nomad robe's resist-heat perk");
            s.Progression.LearnSkill("Perk_ResistHeat");
            s.Items.Equipment.UnequipItem(Body);
            Assert.That(s.Hero.Knows("Perk_ResistHeat"), Is.True, "learned stays");
            Assert.That(s.Hero.IsActivatedByItem("Perk_ResistHeat"), Is.False);
        }

        [Test]
        public void ASwapToAnotherWeapon_OrGivingTheWornItemAway_EndsTheActivation()
        {
            // fix-97: the activation follows what is worn now (Recalculate after every change), not a counter that could drift
            using var f = Fixture();
            var s = f.NewGame();
            var inv = s.Items.Inventories.Of(ActorId.Player);
            inv.Add(OrcSlayer, 1);
            inv.Add("It_1H_Plain", 1);
            s.Items.Equipment.Equip(OrcSlayer);
            Assert.That(s.Hero.IsActivatedByItem("Perk_OrcSlayer"), Is.True);
            Assert.That(s.Items.Equipment.Equip("It_1H_Plain"), Is.EqualTo(EquipResult.Done));
            Assert.That(s.Items.Equipment.In(WornSlot.MeleeWeapon), Is.EqualTo("It_1H_Plain"));
            Assert.That(s.Hero.Knows("Perk_OrcSlayer"), Is.False, "the other sword took the slot: the Orc Slayer's skill went");
            s.Items.Equipment.Equip(OrcSlayer);
            s.Items.Equipment.Equip(OrcSlayer); // equipping it again does not count twice
            s.Items.Equipment.UnequipItem(OrcSlayer);
            Assert.That(s.Hero.Knows("Perk_OrcSlayer"), Is.False, "one take-off ends it");
            s.Items.Equipment.Equip(OrcSlayer);
            inv.Remove(OrcSlayer, 1);
            Assert.That(s.Items.Equipment.In(WornSlot.MeleeWeapon), Is.Null, "no longer carried: no longer worn");
            Assert.That(s.Hero.Knows("Perk_OrcSlayer"), Is.False);
        }

        [Test]
        public void AfterLoading_TheWornItemsActivationIsBack_AndAnUnwornItemsIsNot()
        {
            using var f = Fixture();
            var s = f.NewGame();
            s.Items.Inventories.Of(ActorId.Player).Add(OrcSlayer, 1);
            s.Items.Inventories.Of(ActorId.Player).Add(Body, 1);
            s.Items.Equipment.Equip(OrcSlayer);
            var loaded = f.Reload(s);
            Assert.That(loaded.Hero.IsActivatedByItem("Perk_OrcSlayer"), Is.True, "worn in the save: active again");
            Assert.That(loaded.Hero.HasLearned("Perk_OrcSlayer"), Is.False, "and still not learned");
            Assert.That(loaded.Hero.Knows("Perk_ResistHeat"), Is.False, "the robe was carried, not worn");
            loaded.Items.Equipment.UnequipItem(OrcSlayer);
            Assert.That(f.Reload(loaded).Hero.Knows("Perk_OrcSlayer"), Is.False, "taken off before saving: not active after loading");
        }

        [Test]
        public void AWornOutStack_HalvesItsModifiers_AndActivatesNoSkill()
        {
            using var f = Fixture();
            var s = f.NewGame();
            int before = s.Hero.Stats.Value("PROT_BLADE");
            s.Items.Inventories.Of(ActorId.Player).Add(Body, 1, quality: (int)WeaponQuality.Worn);
            s.Items.Equipment.Equip(Body);
            Assert.That(s.Hero.Stats.Value("PROT_BLADE") - before, Is.EqualTo(20), "OnApplyStats: worn-out → half");
            Assert.That(s.Hero.Knows("Perk_ResistHeat"), Is.False, "OnApplyStats returns before ActivateSkill");
        }
    }
}
