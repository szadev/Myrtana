using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;
using Myrtana.Import.G3;
using Myrtana.Rules;
using Myrtana.Simulation;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>Magic (Docs/combat.md §12, CP 1.75 with AB): cast actions, mana phases, scrolls, spell damage, burning and freezing, heal, summons.</summary>
    public class MagicTests
    {
        static readonly MagicRules Rules = G3MagicImporter.Rules();
        static readonly ActorId Bandit = new ActorId("Bandit"), Wolf = new ActorId("Wolf");

        static SpellDef Fireball() => new SpellDef("Spl_Fireball", new[] { "It_Spell_Fireball" }, new[] { "It_Scroll_Fireball" },
            SpellCastType.Power, SpellTarget.Focus, SpellKind.Projectile, SpellDeity.Innos, 15, 30, SpellDelivery.Projectile, null, null, 50,
            "Mis_Fireball", new SpellMissile("Mis_Fireball", DamageType.Fire, 50, 2, 100, "eff_magic_fireball_projectile_01", "eff_magic_fireball_impact_01"),
            null);

        static SpellDef Icelance() => new SpellDef("Spl_Icelance", new[] { "It_Spell_Icelance" }, null, SpellCastType.Power, SpellTarget.Focus,
            SpellKind.Projectile, SpellDeity.Adanos, 15, 30, SpellDelivery.Projectile, null, null, 50, "Mis_Icelance",
            new SpellMissile("Mis_Icelance", DamageType.Ice, 50, 2, 100, null, null), null);

        static SpellDef Heal() => new SpellDef("Spl_Cure", new[] { "It_Spell_Healing" }, new[] { "It_Scroll_Healing" }, SpellCastType.Regular,
            SpellTarget.Caster, SpellKind.Target, SpellDeity.Innos, 60, 60, SpellDelivery.Instant, "MagicHeal", null, 0, null, null, null);

        static SpellDef ArmyOfDarkness() => new SpellDef("Spl_SummonArmyOfDarkness", new[] { "It_Spell_ArmyOfDarkness" }, null,
            SpellCastType.Regular, SpellTarget.Caster, SpellKind.Summon, SpellDeity.Beliar, 100, 100, SpellDelivery.Instant,
            "MagicSummonArmyOfDarkness", "Summon", 0, null, null, null);

        sealed class Setup
        {
            public readonly EventBus Events = new EventBus();
            public readonly Hero Hero;
            public readonly ActorVitals Vitals;
            public readonly StatusEffects Status;
            public readonly MagicService Magic;
            public readonly Dictionary<string, int> Carried = new Dictionary<string, int> { ["It_Scroll_Fireball"] = 1 };
            public readonly List<SpellCasted> Casts = new List<SpellCasted>();

            public Setup()
            {
                var db = new ContentDatabase();
                foreach (var a in TestRules.Attributes()) db.Put(a);
                Hero = Hero.CreateNew(db, TestRules.Progression(), RuleOptions.Default);
                Vitals = new ActorVitals(Hero.Stats, Profile, TestRules.Combat(), Events);
                Status = new StatusEffects(Vitals, Rules.Burning, id => id != Wolf, Events);
                Magic = new MagicService(Rules, new[] { Fireball(), Icelance(), Heal(), ArmyOfDarkness() }, RuleOptions.Default, Vitals, Status,
                                         _ => null, Consume, Events);
                Events.Subscribe<SpellCasted>(Casts.Add);
            }

            int Consume(ActorId actor, string item, int amount)
            {
                Carried.TryGetValue(item, out int have);
                int taken = System.Math.Min(have, amount);
                Carried[item] = have - taken;
                return taken;
            }

            static VitalsProfile Profile(ActorId id) =>
                id == Bandit ? new VitalsProfile(300, 150, 1000, 5, false, "Human") :
                id == Wolf ? new VitalsProfile(120, 100, 1000, 5, false, "Wolf") : null;

            public int Mana => Vitals.Mana(ActorId.Player);
        }

        // ------------------------------------------------------------------------------------------- casting

        [Test]
        public void CastAction_FollowsTheCastType_PowerNeedsTheMaximumMana()
        {
            var options = RuleOptions.Default;
            Assert.That(SpellCasting.ActionFor(Fireball(), "Human", 30, false, Rules, options), Is.EqualTo(CastAction.PowerCast));
            Assert.That(SpellCasting.ActionFor(Fireball(), "Human", 29, false, Rules, options), Is.EqualTo(CastAction.Cast));
            Assert.That(SpellCasting.ActionFor(Fireball(), "Human", 0, true, Rules, options), Is.EqualTo(CastAction.PowerCast), "god mode");
            Assert.That(SpellCasting.ActionFor(Fireball(), "IceGolem", 0, false, Rules, options), Is.EqualTo(CastAction.PowerCast));
            Assert.That(SpellCasting.ActionFor(Fireball(), "Goblin", 1000, false, Rules, options), Is.EqualTo(CastAction.Cast), "AB: goblins never");
            Assert.That(SpellCasting.ActionFor(Fireball(), "Goblin", 1000, false, Rules, new RuleOptions(false, true)), Is.EqualTo(CastAction.PowerCast));
            Assert.That(SpellCasting.ActionFor(Heal(), "Human", 100, false, Rules, options), Is.EqualTo(CastAction.Summon), "Regular");
            Assert.That(SpellCasting.MotionSpeed(CastAction.Summon, "Hero", Rules, options), Is.EqualTo(1.5f));
            Assert.That(SpellCasting.MotionSpeed(CastAction.Cast, "Xardas", Rules, options), Is.EqualTo(1.6f));
            Assert.That(SpellCasting.MotionSpeed(CastAction.Cast, "Xardas", Rules, new RuleOptions(false, true)), Is.EqualTo(1f));
        }

        [Test]
        public void PreCheck_FizzlesWithoutMana_FocusSpellsNeedATargetUnlessPowerCast_NpcsSkipIt()
        {
            Assert.That(SpellCasting.PreCheck(Heal(), true, 59, false, false, 12), Is.EqualTo(CastRefusal.NotEnoughMana));
            Assert.That(SpellCasting.PreCheck(Heal(), true, 60, false, false, 12), Is.EqualTo(CastRefusal.None));
            Assert.That(SpellCasting.PreCheck(Fireball(), true, 100, false, false, 12), Is.EqualTo(CastRefusal.None), "Power spells fly without a focus");
            Assert.That(SpellCasting.PreCheck(Heal(), false, 0, false, false, 12), Is.EqualTo(CastRefusal.None));
        }

        [Test]
        public void ManaPhases_LoadPaysTheMinimum_HoldingPowersUp_AbortRefunds()
        {
            var s = new Setup();
            Assert.That(s.Mana, Is.EqualTo(100));
            var cast = s.Magic.Begin(ActorId.Player, "It_Spell_Fireball", "Human", now: 10);
            Assert.That(cast.Action, Is.EqualTo(CastAction.PowerCast));
            Assert.That((cast.Phase, cast.ManaUsed, s.Mana), Is.EqualTo((CastPhase.Load, 15, 85)));
            Assert.That(s.Magic.Update(cast, 10.6), Is.False, "held 0.6 s");
            Assert.That(s.Magic.Update(cast, 10.7), Is.True, "held 0.7 s: powered up");
            Assert.That((cast.Phase, cast.ManaUsed, cast.Powered, s.Mana), Is.EqualTo((CastPhase.Power, 30, true, 70)));
            s.Magic.Abort(cast);
            Assert.That((cast.Phase, s.Mana), Is.EqualTo((CastPhase.Aborted, 100)));
        }

        [Test]
        public void Begin_ForcedPlainCast_PowerSpellLetGoEarly_LoadsUnpoweredAndNeverPowersUp()
        {
            // fix-114: G3 PS_Magic_PowerCast let go before 0.7 s → FullStop → PS_Magic_Cast (AnimActionCast, unpowered)
            var s = new Setup();
            var power = s.Magic.Begin(ActorId.Player, "It_Spell_Fireball", "Human", now: 0);
            Assert.That(power.Action, Is.EqualTo(CastAction.PowerCast));
            Assert.That(CastTrigger.OnButtonUp(power.Action, power.Released, power.Powered, 0.3, s.Magic.Rules.PowerCastHoldSeconds),
                        Is.EqualTo(CastButtonUp.RecastPlain));
            s.Magic.Abort(power);
            Assert.That(s.Mana, Is.EqualTo(100), "the power cast's load came back");
            var plain = s.Magic.Begin(ActorId.Player, "It_Spell_Fireball", "Human", now: 0.3, action: CastAction.Cast);
            Assert.That((plain.Action, plain.ManaUsed, s.Mana), Is.EqualTo((CastAction.Cast, 15, 85)));
            Assert.That(s.Magic.Update(plain, 5), Is.False, "a plain cast never powers up");
            Assert.That(CastTrigger.GoesOff(plain.Action, plain.Powered), Is.True);
            Assert.That(s.Magic.Release(plain) && !plain.Powered, Is.True);
        }

        [Test]
        public void Release_UsesUpAScroll_KeepsTheLearnedSpell_AndTellsTheQuests()
        {
            var s = new Setup();
            var cast = s.Magic.Begin(ActorId.Player, "It_Scroll_Fireball", "Human", 0);
            Assert.That(cast.UsesUpItem, Is.True);
            Assert.That(s.Magic.Release(cast), Is.True);
            Assert.That(s.Carried["It_Scroll_Fireball"], Is.EqualTo(0));
            Assert.That(s.Mana, Is.EqualTo(85), "scrolls cost mana too (only Alchemy items are free)");
            Assert.That(s.Casts.Single().Spell, Is.EqualTo("Spl_Fireball"));
            Assert.That(s.Magic.Begin(ActorId.Player, "It_Scroll_Fireball", "Human", 1) is SpellCast again && !s.Magic.Release(again), Is.True,
                        "no scroll left: the cast fails and refunds");
            Assert.That(s.Mana, Is.EqualTo(85));
            var learned = s.Magic.Begin(ActorId.Player, "It_Spell_Fireball", "Human", 2);
            Assert.That(learned.UsesUpItem, Is.False);
            Assert.That(s.Magic.Release(learned), Is.True);
        }

        [Test]
        public void NpcCasts_CostNoMana()
        {
            var s = new Setup();
            var cast = s.Magic.Begin(Bandit, "It_Spell_Fireball", "Human", 0);
            s.Magic.PowerUp(cast);
            Assert.That((cast.ManaUsed, cast.ManaPaid, s.Vitals.Mana(Bandit)), Is.EqualTo((30, 0, 1000)));
        }

        // ------------------------------------------------------------------------------------------- damage and effects

        static HitContext SpellHit(Setup s, SpellCast cast, Combatant attacker, Combatant victim, bool atTarget = true) =>
            MagicService.ToSpellHit(new HitContext { Attacker = attacker, Victim = victim, PlayerLevel = 10 }, cast, atTarget);

        static Combatant PlayerCaster(int intelligence, params string[] perks) => new Combatant
        {
            Id = ActorId.Player, IsPlayer = true, Level = 10, Intelligence = intelligence, HitPoints = 200, Mana = 100,
            Perks = new HashSet<string>(perks),
        };

        static Combatant Target(ActorId id, string species = "Human", bool humanoid = true, int hp = 2000) => new Combatant
        {
            Id = id, Species = species, Humanoid = humanoid, HitPoints = hp, Stamina = 100, Mana = 1000, Weapon = WeaponClass.Fist,
        };

        [Test]
        public void SpellProjectile_DoublesItsBase_AddsTheIntBonus_PowerCastDoublesAgain()
        {
            var s = new Setup();
            var resolver = new CombatResolver(TestRules.Combat(), RuleOptions.Default, new SeededRandom(1));
            var cast = s.Magic.Begin(ActorId.Player, "It_Spell_Fireball", "Human", 0);
            s.Magic.Release(cast);
            var hit = SpellHit(s, cast, PlayerCaster(100), Target(Bandit));
            Assert.That((hit.Delivery, hit.DamageType, hit.DamageAmount, hit.ManaUsed, hit.SpellDeity, hit.PowerCast),
                        Is.EqualTo((HitDelivery.Spell, DamageType.Fire, 50, 15, "Innos", false)));
            Assert.That(hit.FireOrExplosiveProjectile, Is.True);
            int normal = resolver.Resolve(hit).Damage;
            Assert.That(normal, Is.EqualTo(200), "min(50,100)·2 = 100, + 100·INT/100");
            Assert.That(resolver.Resolve(SpellHit(s, cast, PlayerCaster(100, "Perk_Firemage"), Target(Bandit))).Damage, Is.EqualTo(300),
                        "the deity perk adds the (doubled) base once more");

            var powered = s.Magic.Begin(ActorId.Player, "It_Spell_Fireball", "Human", 0);
            s.Magic.PowerUp(powered);
            s.Magic.Release(powered);
            Assert.That(resolver.Resolve(SpellHit(s, powered, PlayerCaster(100), Target(Bandit))).Damage, Is.EqualTo(400), "path style Missile");
            Assert.That(resolver.Resolve(SpellHit(s, powered, PlayerCaster(100), Target(Bandit), atTarget: false)).Damage, Is.EqualTo(200),
                        "no target: no Missile path");
        }

        [Test]
        public void Fire_SetsOnFire_FiveHpASecondForTenSeconds_CreaturesDie_HumanoidsAreKnockedOut()
        {
            var s = new Setup();
            var o = new CombatResolver(TestRules.Combat(), RuleOptions.Default, new SeededRandom(1))
                .Resolve(SpellHit(s, ReleasedFireball(s), PlayerCaster(0), Target(Bandit)));
            Assert.That(o.Burns, Is.True);
            s.Status.OnHit(Bandit, o);
            Assert.That(s.Status.IsBurning(Bandit), Is.True);
            int before = s.Vitals.HitPoints(Bandit);
            for (int i = 0; i < 40; i++) s.Status.Tick(0.25f);
            Assert.That(before - s.Vitals.HitPoints(Bandit), Is.EqualTo(50));
            Assert.That(s.Status.IsBurning(Bandit), Is.False);

            s.Vitals.AddHitPoints(Bandit, -(s.Vitals.HitPoints(Bandit) - 4));
            s.Status.Burn(Bandit, 10);
            s.Status.Tick(1f);
            Assert.That(s.Vitals.StateOf(Bandit), Is.EqualTo(VitalState.Down), "a humanoid is knocked out by the burns");

            s.Vitals.AddHitPoints(Wolf, -(s.Vitals.HitPoints(Wolf) - 4));
            s.Status.Burn(Wolf, 10);
            s.Status.Tick(1f);
            Assert.That(s.Vitals.StateOf(Wolf), Is.EqualTo(VitalState.Dead));
        }

        [Test]
        public void Fix209_BurnsAndPoison_KillAKillableHumanoidHostileToThePlayer_OthersAreKnockedOut()
        {
            Assert.That(StatusEffects.DiesOfDamageOverTime(true, Attitude.Hostile), Is.True);
            Assert.That(StatusEffects.DiesOfDamageOverTime(true, Attitude.Predator), Is.True);
            Assert.That(StatusEffects.DiesOfDamageOverTime(true, Attitude.Angry), Is.False);
            Assert.That(StatusEffects.DiesOfDamageOverTime(true, Attitude.Neutral), Is.False);
            Assert.That(StatusEffects.DiesOfDamageOverTime(true, Attitude.Panic), Is.False);
            Assert.That(StatusEffects.DiesOfDamageOverTime(false, Attitude.Hostile), Is.False, "CanBeKilled protects");

            var s = new Setup();
            s.Status.HumanoidDiesOfDamageOverTime = id => id == Bandit;
            s.Vitals.AddHitPoints(Bandit, -(s.Vitals.HitPoints(Bandit) - 4));
            s.Status.Burn(Bandit, 10);
            s.Status.Tick(1f);
            Assert.That(s.Vitals.StateOf(Bandit), Is.EqualTo(VitalState.Dead), "a hostile humanoid burns to death");
        }

        [Test]
        public void Poison_Takes5HpASecond_ForItsDuration_StacksWithBurning_AndIsCured()
        {
            // combat-6, CP 1.75 OnTick: -5 HP a second; the player 30 s; NPCs max(10, 60 - (LevelMax + Level)/2).
            Assert.That(StatusEffects.NpcPoisonSeconds(5, 10), Is.EqualTo(53));
            Assert.That(StatusEffects.NpcPoisonSeconds(60, 80), Is.EqualTo(10), "never under 10 s");
            var s = new Setup();
            int before = s.Vitals.HitPoints(Bandit);
            Assert.That(s.Status.Poison(Bandit, 12f), Is.True);
            Assert.That(s.Status.IsPoisoned(Bandit), Is.True);
            for (int i = 0; i < 40; i++) s.Status.Tick(0.25f);
            Assert.That(before - s.Vitals.HitPoints(Bandit), Is.EqualTo(50), "10 s x 5");
            for (int i = 0; i < 20; i++) s.Status.Tick(0.25f);
            Assert.That(before - s.Vitals.HitPoints(Bandit), Is.EqualTo(60), "12 s in all");
            Assert.That(s.Status.IsPoisoned(Bandit), Is.False, "worn off");

            before = s.Vitals.HitPoints(Bandit);
            s.Status.Poison(Bandit, 30f);
            s.Status.Burn(Bandit, 10);
            s.Status.Tick(1f);
            Assert.That(before - s.Vitals.HitPoints(Bandit), Is.EqualTo(10), "poison and burning stack");
            s.Status.Clear(Bandit, ActorStatus.Poisoned);
            Assert.That(s.Status.IsPoisoned(Bandit), Is.False, "CurePoison");

            s.Vitals.AddHitPoints(Wolf, -(s.Vitals.HitPoints(Wolf) - 4));
            s.Status.Poison(Wolf, 30f);
            s.Status.Tick(1f);
            Assert.That(s.Vitals.StateOf(Wolf), Is.EqualTo(VitalState.Dead), "a creature dies of the poison");
            Assert.That(s.Status.IsPoisoned(Wolf), Is.False);
        }

        [Test]
        public void NpcPotion_HealsByTheItemsUseEffects_AndIsUsedUp()
        {
            // combat-6: ZS_Attack_DrinkPotion. It_Potion_Health: +50 % HP (the hero's potion effect, same item).
            var s = new Setup();
            var db = new ContentDatabase();
            db.Put(ItemTests.Item("It_Potion_Health", 250, use: new[] { new AttributeGain("HP", ModifierOp.AddPercentageToVal, 50) }));
            var inventories = new Inventories(s.Events);
            Assert.That(NpcPotions.Drink(inventories, db, s.Vitals, Bandit, "It_Potion_Health"), Is.False, "none carried");
            inventories.Of(Bandit).Add("It_Potion_Health", 1);
            s.Vitals.AddHitPoints(Bandit, -200);
            Assert.That(s.Vitals.HitPoints(Bandit), Is.EqualTo(100));
            Assert.That(NpcPotions.Has(inventories, Bandit, "It_Potion_Health"), Is.True);
            Assert.That(NpcPotions.Drink(inventories, db, s.Vitals, Bandit, "It_Potion_Health"), Is.True);
            Assert.That(s.Vitals.HitPoints(Bandit), Is.EqualTo(250), "+50 % of 300");
            Assert.That(NpcPotions.Has(inventories, Bandit, "It_Potion_Health"), Is.False, "used up");
            inventories.Of(Bandit).Add("It_Potion_Health", 1);
            NpcPotions.Drink(inventories, db, s.Vitals, Bandit, "It_Potion_Health");
            Assert.That(s.Vitals.HitPoints(Bandit), Is.EqualTo(300), "capped at the maximum");
        }

        [Test]
        public void Disease_HasNoTick_EndsOnlyWhenCured_AndEvilIsImmune()
        {
            var events = new EventBus();
            var db = new ContentDatabase();
            foreach (var a in TestRules.Attributes()) db.Put(a);
            var hero = Hero.CreateNew(db, TestRules.Progression(), RuleOptions.Default);
            var vitals = new ActorVitals(hero.Stats, id => id == Bandit ? new VitalsProfile(300, 150, 1000, 5, false, "Human") :
                                                          id == Wolf ? new VitalsProfile(120, 100, 1000, 5, false, "Wolf") : null,
                                         TestRules.Combat(), events);
            var status = new StatusEffects(vitals, Rules.Burning, id => id != Wolf, events, immune: id => id == Wolf);
            int hp = vitals.HitPoints(Bandit);
            Assert.That(status.Infect(Bandit), Is.True);
            Assert.That(vitals.Stamina(Bandit), Is.EqualTo(0), "MagicDisease takes the stamina");
            for (int i = 0; i < 100; i++) status.Tick(1f);
            Assert.That(status.Has(Bandit, ActorStatus.Diseased), Is.True, "no end");
            Assert.That(vitals.HitPoints(Bandit), Is.EqualTo(hp), "no damage");
            status.Clear(Bandit, ActorStatus.Diseased);
            Assert.That(status.Has(Bandit, ActorStatus.Diseased), Is.False);
            Assert.That(status.Infect(Wolf), Is.False, "evil: immune");
            Assert.That(status.Poison(Wolf), Is.False);
            Assert.That(StaminaRules.PlayerPerSecond(new PlayerStaminaState { Diseased = true }, RuleOptions.Default), Is.EqualTo(1),
                        "a diseased player regains 1 stamina a second");
        }

        [Test]
        public void Ice_Freezes_ForDamageOver20Seconds_AtLeast5_AndFrozenVictimsTakeNoMoreIce()
        {
            var s = new Setup();
            var resolver = new CombatResolver(TestRules.Combat(), RuleOptions.Default, new SeededRandom(1));
            var cast = s.Magic.Begin(ActorId.Player, "It_Spell_Icelance", "Human", 0);
            s.Magic.Release(cast);
            var o = resolver.Resolve(SpellHit(s, cast, PlayerCaster(100), Target(Bandit)));
            Assert.That(o.FreezeSeconds, Is.EqualTo(10f).Within(1e-4), "200 / 20");
            s.Status.OnHit(Bandit, o);
            Assert.That(s.Status.IsFrozen(Bandit), Is.True);
            var frozen = Target(Bandit);
            frozen.Frozen = true;
            Assert.That(resolver.Resolve(SpellHit(s, cast, PlayerCaster(100), frozen)).Damage, Is.EqualTo(0));
            s.Status.Tick(9.9f);
            Assert.That(s.Status.IsFrozen(Bandit), Is.True);
            s.Status.Tick(0.2f);
            Assert.That(s.Status.IsFrozen(Bandit), Is.False);
        }

        [Test]
        public void Heal_FillsTheCastersHitPoints()
        {
            var s = new Setup();
            s.Vitals.AddHitPoints(ActorId.Player, -150);
            var cast = s.Magic.Begin(ActorId.Player, "It_Spell_Healing", "Human", 0);
            Assert.That(cast.Action, Is.EqualTo(CastAction.Summon));
            Assert.That(s.Magic.ApplyInstant(cast, ActorId.Player), Is.False, "not released yet");
            s.Magic.Release(cast);
            Assert.That(s.Magic.ApplyInstant(cast, ActorId.Player), Is.True);
            Assert.That(s.Vitals.HitPoints(ActorId.Player), Is.EqualTo(s.Vitals.HitPointsMax(ActorId.Player)));
            Assert.That(s.Mana, Is.EqualTo(40));
        }

        static SpellCast ReleasedFireball(Setup s)
        {
            var cast = s.Magic.Begin(ActorId.Player, "It_Spell_Fireball", "Human", 0);
            s.Magic.Release(cast);
            return cast;
        }

        // ------------------------------------------------------------------------------------------- area spells, summons

        [Test]
        public void AreaSpells_WavesFallOffLinearly_RainsHitMoreOftenInACrowd()
        {
            var heat = Rules.AreaSpell("Heatwave");
            Assert.That(heat.WaveDamage(0), Is.EqualTo(400));
            Assert.That(heat.WaveDamage(500), Is.EqualTo(201), "round(400 + 0.5·(1 − 400)) = round(200.5)");
            Assert.That(heat.WaveDamage(1000), Is.EqualTo(1));
            var rain = Rules.AreaSpell("Firerain");
            Assert.That(rain.RainHitChance(2), Is.EqualTo(43));
            Assert.That(rain.RainHitChance(30), Is.EqualTo(103), "the cap is on the bonus");
            Assert.That(rain.RainHits(43, 2) && !rain.RainHits(44, 2), Is.True);
        }

        [Test]
        public void Summons_ArmyGrowsWithInt_ANewGroupDismissesTheOld_PlayersCreaturesAreLeashed()
        {
            var groups = new SummonGroups(Rules.Summons);
            Assert.That(groups.Plan(ArmyOfDarkness(), 100, "Xardas"), Has.Count.EqualTo(6));
            Assert.That(groups.Plan(ArmyOfDarkness(), 250, "Xardas").Count(t => t == "SlaveZombie"), Is.EqualTo(4));
            Assert.That(groups.Plan(ArmyOfDarkness(), 300, "Xardas"), Has.Count.EqualTo(10));

            var a = new ActorId("Skeleton~1");
            var b = new ActorId("Skeleton~2");
            groups.Join(ActorId.Player, a);
            groups.Join(ActorId.Player, b);
            Assert.That(groups.LeaderOf(a), Is.EqualTo(ActorId.Player));
            Assert.That(groups.BeyondLeash(id => id == b ? 4100 : 300), Is.EqualTo(new[] { b }));
            groups.Remove(b);
            Assert.That(groups.Dismiss(ActorId.Player), Is.EqualTo(new[] { a }));
            Assert.That(groups.IsSummoned(a) || groups.HasSummons(ActorId.Player), Is.False);
            Assert.That(groups.NpcSummonAttacks(1999) && !groups.NpcSummonAttacks(2001), Is.True);
        }
    }
}
