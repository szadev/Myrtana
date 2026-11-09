using System.Collections.Generic;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// fix-28: attacking from the sneak stance and the unaware-victim special hits (G3 CP 1.75 PS_Melee_Sneak, _AI_* ani-states,
    /// AssessHit; Docs/combat.md §1.6, §5.4).
    /// </summary>
    public class SneakCombatTests
    {
        static readonly ActorId Bandit = new ActorId("Bandit");

        // ------------------------------------------------------------------------------------------- stance

        [Test]
        public void SneakStance_MeleeOrFistsDrawn_IsParade_OtherwiseSneak()
        {
            Assert.That(SneakCombatRules.SneakStance(meleeDrawn: true), Is.EqualTo(BodyStance.Parade));
            Assert.That(SneakCombatRules.SneakStance(meleeDrawn: false), Is.EqualTo(BodyStance.Sneak));
        }

        [TestCase(CombatAction.Attack, BodyStance.Stand)]
        [TestCase(CombatAction.PowerAttack, BodyStance.Stand)]
        [TestCase(CombatAction.QuickAttack, BodyStance.Stand)]
        [TestCase(CombatAction.SimpleWhirl, BodyStance.Stand)]
        [TestCase(CombatAction.PierceAttack, BodyStance.Parade)]
        [TestCase(CombatAction.WhirlAttack, BodyStance.Parade)]
        [TestCase(CombatAction.HackAttack, BodyStance.Parade)]
        [TestCase(CombatAction.FinishingAttack, BodyStance.Parade)]
        [TestCase(CombatAction.Parade, BodyStance.Parade)]
        public void MoveStance_IsTheAniStateOfItsCommand(CombatAction action, BodyStance stance)
        {
            Assert.That(SneakCombatRules.MoveStance(action), Is.EqualTo(stance));
            Assert.That(SneakCombatRules.RisesFromSneak(action), Is.EqualTo(stance == BodyStance.Stand));
        }

        [Test]
        public void SneakToggle_SurvivesAttacks_TheBlockEndsIt()
        {
            Assert.That(SneakCombatRules.KeepsSneaking(CombatAction.Attack), Is.True);
            Assert.That(SneakCombatRules.KeepsSneaking(CombatAction.PowerAttack), Is.True);
            Assert.That(SneakCombatRules.KeepsSneaking(CombatAction.Parade), Is.False);
        }

        /// <summary>
        /// From the sneak stance the block is not held (only the secondary button sets G3's "block held"; raising it ends
        /// sneaking), so every weapon class attacks as standing: tap → Attack, hold → PowerAttack, and the power combos.
        /// </summary>
        [TestCase(WeaponClass.OneHanded, false)]
        [TestCase(WeaponClass.TwoHanded, false)]
        [TestCase(WeaponClass.Axe, false)]
        [TestCase(WeaponClass.Staff, false)]
        [TestCase(WeaponClass.Fist, false)]
        [TestCase(WeaponClass.OneHanded, true)] // dual swords
        public void FromSneak_EveryWeaponClass_TapAttacks_HoldPowerAttacks_AndStandsUp(WeaponClass weapon, bool dual)
        {
            var stance = new MeleeStance { RightHand = weapon, LeftOneHandedOrTorch = dual, CanParade = true };
            var input = new MeleeInputInterpreter();
            input.PrimaryPressed(0, stance);
            var tap = input.PrimaryReleased(0.05, stance);
            Assert.That(tap, Is.EqualTo(MeleeCommand.Start(CombatAction.Attack)));
            input.MoveEnded(1, stance);

            input.PrimaryPressed(2, stance);
            var hold = input.Update(2.15, stance);
            Assert.That(hold, Is.EqualTo(MeleeCommand.Start(CombatAction.PowerAttack)), "the 100 ms hold threshold is kept");
            Assert.That(SneakCombatRules.RisesFromSneak(hold.Move), Is.True);
            var combo = input.PrimaryReleased(2.45, stance);
            Assert.That(combo, Is.EqualTo(MeleeCommand.Start(dual ? CombatAction.SimpleWhirl : CombatAction.Attack)));
        }

        [Test]
        public void SneakStanceBlocks_OnlyWithoutAbAndAai()
        {
            Assert.That(SneakCombatRules.SneakStanceBlocks(new RuleOptions(alternativeBalancing: false, extendedAI: false)), Is.True);
            Assert.That(SneakCombatRules.SneakStanceBlocks(new RuleOptions(alternativeBalancing: true, extendedAI: false)), Is.False);
            Assert.That(SneakCombatRules.SneakStanceBlocks(new RuleOptions(alternativeBalancing: false, extendedAI: true)), Is.False);
            Assert.That(SneakCombatRules.SneakStanceBlocks(RuleOptions.Default), Is.False);
        }

        // ------------------------------------------------------------------------------------------- awareness

        [TestCase(0f, 1f, true)]      // straight ahead
        [TestCase(1f, 0f, true)]      // 90° to the side: the edge of the front half-space
        [TestCase(1f, -0.1f, false)]  // ~96°: was "seen" with the old ±100° cone
        [TestCase(0f, -1f, false)]    // behind
        [TestCase(0f, 0f, true)]      // same spot
        public void FieldOfView_IsTheFrontHalfSpace(float toX, float toZ, bool seen)
        {
            Assert.That(SneakCombatRules.InFieldOfView(0, 1, toX, toZ), Is.EqualTo(seen));
        }

        [Test]
        public void InRoutine_AndUnaware()
        {
            Assert.That(SneakCombatRules.InRoutine(false, false, false, false), Is.True);
            Assert.That(SneakCombatRules.InRoutine(fighting: true, false, false, false), Is.False);
            Assert.That(SneakCombatRules.InRoutine(false, reacting: true, false, false), Is.False);
            Assert.That(SneakCombatRules.InRoutine(false, false, spellbound: true, false), Is.False);
            Assert.That(SneakCombatRules.InRoutine(false, false, false, talking: true), Is.False);
            Assert.That(SneakCombatRules.InRoutine(false, false, false, false, alive: false), Is.False);
            Assert.That(SneakCombatRules.Unaware(true, false), Is.True);
            Assert.That(SneakCombatRules.Unaware(true, true), Is.False);
            Assert.That(SneakCombatRules.Unaware(false, false), Is.False);
        }

        // ------------------------------------------------------------------------------------------- the knock-out hit

        static CombatResolver Resolver() => new CombatResolver(TestRules.Combat(), RuleOptions.Default, new SeededRandom(7));

        static Combatant Hero(CombatAction action, params string[] perks) => new Combatant
        {
            Id = ActorId.Player, IsPlayer = true, Level = 10, Strength = 100, Dexterity = 100, Weapon = WeaponClass.OneHanded,
            Action = action, HitPoints = 200, Stamina = 150, Mana = 100, Perks = new HashSet<string>(perks),
        };

        static Combatant Victim(int hp = 300) => new Combatant
        {
            Id = Bandit, Species = "Human", Humanoid = true, Level = 5, Strength = 40,
            Protection = DamageTypeValues.All(CombatStats.NpcProtection(5)), Weapon = WeaponClass.Fist,
            Action = CombatAction.None, HitPoints = hp, Stamina = 100, InRoutine = true,
        };

        /// <summary>The hero's club swing: It_1H_Club_01 deals 15 impact damage.</summary>
        static HitContext Club(Combatant hero, Combatant victim, bool seen = false, DamageType type = DamageType.Impact) =>
            new HitContext
            {
                Attacker = hero, Victim = victim, Delivery = HitDelivery.Melee, DamageType = type, DamageAmount = 15,
                VictimSeesAttacker = seen, AttackerToVictim = Attitude.Neutral, VictimToAttacker = Attitude.Neutral, PlayerLevel = 10,
            };

        [Test]
        public void KnockOutPerk_PowerAttackWithAClub_OnAnUnawareRoutineVictim_KnocksOut_WithoutDamage()
        {
            var o = Resolver().Resolve(Club(Hero(CombatAction.PowerAttack, "Perk_KnockOut"), Victim()));
            Assert.That(o.Reaction, Is.EqualTo(HitReaction.LieKnockDown));
            Assert.That(o.KnockedOut, Is.True);
            Assert.That(o.Killed, Is.False);
            // AssessHit zeroes the DamageReceiver amount and the knock-out branch never sets it (vanilla and CP 1.75).
            Assert.That(o.Damage, Is.EqualTo(0));
            Assert.That(o.Lethal, Is.False);
            var plain = Resolver().Resolve(Club(Hero(CombatAction.PowerAttack), Victim()));
            Assert.That(plain.KnockedOut, Is.False, "without the perk: a normal hit");
            Assert.That(plain.Damage, Is.GreaterThan(0));
        }

        [Test]
        public void KnockOutPerk_AWoundedVictim_IsKnockedOutNotKilled()
        {
            var o = Resolver().Resolve(Club(Hero(CombatAction.PowerAttack, "Perk_KnockOut"), Victim(hp: 3)));
            Assert.That(o.Reaction, Is.EqualTo(HitReaction.LieKnockDown));
            Assert.That(o.Damage, Is.EqualTo(0));
            Assert.That(o.Killed, Is.False);
        }

        [Test]
        public void KnockOutPerk_NeedsPowerImpactUnawareRoutine()
        {
            Assert.That(Resolver().Resolve(Club(Hero(CombatAction.Attack, "Perk_KnockOut"), Victim())).KnockedOut, Is.False, "a plain attack");
            Assert.That(Resolver().Resolve(Club(Hero(CombatAction.PowerAttack, "Perk_KnockOut"), Victim(), type: DamageType.Blade)).KnockedOut,
                        Is.False, "a blade");
            Assert.That(Resolver().Resolve(Club(Hero(CombatAction.PowerAttack, "Perk_KnockOut"), Victim(), seen: true)).KnockedOut,
                        Is.False, "the victim sees the hero");
            var fighting = Victim();
            fighting.InRoutine = false;
            Assert.That(Resolver().Resolve(Club(Hero(CombatAction.PowerAttack, "Perk_KnockOut"), fighting)).KnockedOut, Is.False,
                        "the victim is not in its routine");
        }

        [Test]
        public void KnockOut_ByTheSneakHit_PutsTheVictimDown_ItGetsUpAfter30s()
        {
            var events = new EventBus();
            var db = new ContentDatabase();
            foreach (var a in TestRules.Attributes()) db.Put(a);
            var hero = Myrtana.Rules.Hero.CreateNew(db, TestRules.Progression(), RuleOptions.Default);
            var vitals = new ActorVitals(hero.Stats, id => id == Bandit ? new VitalsProfile(300, 150, 1000, 5, false, "Human") : null,
                                         TestRules.Combat(), events);
            var defeats = new List<ActorDefeated>();
            events.Subscribe<ActorDefeated>(defeats.Add);
            var hit = Club(Hero(CombatAction.PowerAttack, "Perk_KnockOut"), Victim());
            var o = Resolver().Resolve(hit);
            vitals.ApplyHit(hit, o, byPlayerParty: true);
            Assert.That(vitals.IsDown(Bandit), Is.True);
            Assert.That(vitals.HitPoints(Bandit), Is.EqualTo(300));
            Assert.That(defeats, Has.Count.EqualTo(1));
            Assert.That(defeats[0].Killed, Is.False);
            for (int i = 0; i < 29; i++) vitals.Tick(1f);
            Assert.That(vitals.IsDown(Bandit), Is.True);
            vitals.Tick(1f);
            Assert.That(vitals.IsDown(Bandit), Is.False);
        }
    }
}
