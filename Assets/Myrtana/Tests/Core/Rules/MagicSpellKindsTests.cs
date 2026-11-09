using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;
using Myrtana.Import.G3;
using Myrtana.Rules;
using Myrtana.Session;
using Myrtana.Simulation;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>Area spells, summons, transformations and control spells (Docs/combat.md §12.1–§12.3, CP 1.75).</summary>
    public class MagicSpellKindsTests
    {
        static readonly MagicRules Rules = G3MagicImporter.Rules();
        static readonly ActorId Mage = new ActorId("Mage"), A = new ActorId("A"), B = new ActorId("B"), C = new ActorId("C");

        static SpellDef AreaSpell(string id, string spawn, SpellTarget target = SpellTarget.Caster) =>
            new SpellDef(id, new[] { "It_" + id }, null, SpellCastType.Regular, target, SpellKind.Summon, SpellDeity.Innos, 90, 90,
                         SpellDelivery.Summon, null, null, 0, spawn, null, null);

        sealed class Rolls : IRandom
        {
            readonly Queue<int> _values;
            public Rolls(params int[] values) => _values = new Queue<int>(values);
            public int Next(int maxExclusive) => _values.Count > 0 ? System.Math.Min(_values.Dequeue(), maxExclusive - 1) : 0;
            public float NextFloat() => 0;
            public RandomState Save() => default;
            public void Load(RandomState state) { }
        }

        // ------------------------------------------------------------------------------------------- area spells

        [Test]
        public void AreaSpellFor_NamesTheParameterBlockByTheSpawnTemplate()
        {
            Assert.That(Rules.AreaSpellFor(AreaSpell("Spl_HeatWave", "Smn_HeatWave")).Name, Is.EqualTo("Heatwave"));
            Assert.That(Rules.AreaSpellFor(AreaSpell("Spl_SummonLightning", "Smn_Lightning")).Name, Is.EqualTo("Lightning"));
            Assert.That(Rules.AreaSpellFor(AreaSpell("Spl_Firerain", "Smn_Firerain")).Mode, Is.EqualTo(AreaSpellMode.Rain));
            Assert.That(Rules.AreaSpellFor(AreaSpell("Spl_IceBomb", "Smn_IceBomb")).DamageType, Is.EqualTo(DamageType.Missile));
            Assert.That(Rules.AreaSpellFor(AreaSpell("Spl_Light", "Smn_Light")), Is.Null);
            Assert.That(Rules.AreaSpellFor(AreaSpell("Spl_SummonMist", "Smn_Mist")), Is.Null);
        }

        [Test]
        public void Wave_HitsEachCandidateOnceNearestFirst_ThenDecays()
        {
            var run = new AreaSpellRun(Rules.AreaSpell("Heatwave"), Mage);
            var around = new[] { new AreaCandidate(B, 600), new AreaCandidate(A, 100), new AreaCandidate(C, 1200), new AreaCandidate(Mage, 0) };
            var hits = new List<AreaHit>();
            for (int i = 0; i < 5 && !run.Over; i++)
                if (run.Tick(0.016f, around, new Rolls(), out var hit)) hits.Add(hit);
            Assert.That(hits.Select(h => h.Target), Is.EqualTo(new[] { A, B }), "C beyond the 1000 radius, the caster skipped");
            Assert.That(hits[0].Damage, Is.EqualTo(Rules.AreaSpell("Heatwave").WaveDamage(100)));
            Assert.That(hits[0].Type, Is.EqualTo(DamageType.Fire));
            Assert.That(run.Over, Is.True);
        }

        [Test]
        public void Wave_DoesNotHitAVictimAgainWhenItIsThrownBackPastTheFront()
        {
            var run = new AreaSpellRun(Rules.AreaSpell("Heatwave"), Mage);
            var hits = new List<AreaHit>();
            // A is hit at 1 m, stumbles back to 7 m (beyond the front at 6.05 m after B) and stays in the radius.
            var before = new[] { new AreaCandidate(A, 100), new AreaCandidate(B, 600) };
            var after = new[] { new AreaCandidate(A, 700), new AreaCandidate(B, 650) };
            for (int i = 0; i < 10 && !run.Over; i++)
                if (run.Tick(0.016f, i == 0 ? before : after, new Rolls(), out var hit)) hits.Add(hit);
            Assert.That(hits.Select(h => h.Target), Is.EqualTo(new[] { A, B }));
            Assert.That(run.Over, Is.True);
        }

        [Test]
        public void MeteorAndLightning_StartAfterHalfASecond()
        {
            var run = new AreaSpellRun(Rules.AreaSpell("Meteor"), Mage);
            var around = new[] { new AreaCandidate(A, 0) };
            Assert.That(run.Tick(0.3f, around, new Rolls(), out _), Is.False);
            Assert.That(run.Tick(0.3f, around, new Rolls(), out var hit), Is.True);
            Assert.That(hit.Damage, Is.EqualTo(1000));
        }

        [Test]
        public void Areas_SkipTheCastersPartyAndItsLeader_CP()
        {
            var run = new AreaSpellRun(Rules.AreaSpell("Frostwave"), ActorId.Player);
            var around = new[]
            {
                new AreaCandidate(A, 100, partyLeader: ActorId.Player),     // the hero's summon / companion
                new AreaCandidate(B, 200, dead: true),
                new AreaCandidate(C, 300),
            };
            Assert.That(run.Tick(0.016f, around, new Rolls(), out var hit), Is.True);
            Assert.That(hit.Target, Is.EqualTo(C));
            Assert.That(AreaSpellRun.Skipped(new AreaCandidate(Mage, 50), A, Mage), Is.True, "the caster's own leader");
        }

        [Test]
        public void Rain_EveryFourTenths_HitChanceGrowsWithTheCrowd_EndsAfterItsTime()
        {
            var def = Rules.AreaSpell("Firerain");
            var run = new AreaSpellRun(def, Mage, durationSeconds: 8);
            var around = new[] { new AreaCandidate(A, 300), new AreaCandidate(B, 900) };
            // first tick at once: roll 43 ≤ 33 + 2·5 hits; candidate index 1 (B); damage 50 + 20
            Assert.That(run.Tick(0.01f, around, new Rolls(43, 1, 20), out var hit), Is.True);
            Assert.That((hit.Target, hit.Damage), Is.EqualTo((B, 70)));
            Assert.That(run.Tick(0.2f, around, new Rolls(0, 0, 0), out _), Is.False, "next tick only after 0.4 s");
            Assert.That(run.Tick(0.25f, around, new Rolls(44), out _), Is.False, "roll 44 > 43 misses");
            Assert.That(run.Tick(8f, around, new Rolls(0, 0, 0), out _), Is.False);
            Assert.That(run.Over, Is.True);
            Assert.That(AreaSpellRun.RainSeconds(new Rolls(3), SpellDeity.Innos, true, p => p == "Perk_Firemage"), Is.EqualTo(16.5f));
            Assert.That(AreaSpellRun.RainSeconds(new Rolls(0), SpellDeity.Innos, false, p => true), Is.EqualTo(8f));
        }

        [Test]
        public void AreaHit_IsTheObjectsBaseDamage_NoIntBonus()
        {
            var player = new Combatant { Id = ActorId.Player, IsPlayer = true, Intelligence = 300, HitPoints = 500, Humanoid = true };
            var wolf = new Combatant { Id = A, Species = "Wolf", Humanoid = false, Animal = true, HitPoints = 1000 };
            var hit = MagicService.ToAreaHit(new HitContext { Attacker = player, Victim = wolf }, AreaSpell("Spl_HeatWave", "Smn_HeatWave"),
                                             new AreaHit(A, 400, DamageType.Fire, 0));
            var outcome = new CombatResolver(TestRules.Combat(), RuleOptions.Default, new SeededRandom(1)).Resolve(hit);
            Assert.That(outcome.Result, Is.EqualTo(HitResult.Hit));
            Assert.That(outcome.Damage, Is.EqualTo(400));
            Assert.That(outcome.Burns, Is.True);
            var golem = new Combatant { Id = B, Species = "FireGolem", Humanoid = false, HitPoints = 1000 };
            hit = MagicService.ToAreaHit(new HitContext { Attacker = player, Victim = golem }, null, new AreaHit(B, 400, DamageType.Fire, 0));
            Assert.That(new CombatResolver(TestRules.Combat(), RuleOptions.Default, new SeededRandom(1)).Resolve(hit).Gate, Is.EqualTo(HitGate.Immune));
        }

        // ------------------------------------------------------------------------------------------- summons

        sealed class SummonWorld
        {
            public readonly EventBus Events = new EventBus();
            public readonly ActorRegistry Actors;
            public readonly NpcSimulation Npcs;
            public readonly PlayerParty Party;
            public readonly ActorVitals Vitals;
            public readonly NpcCombatSystem Combat;
            public readonly SummonService Summons;
            public WorldPose Player = new WorldPose(0, 0, 0, 0);
            public readonly List<CreatureSummoned> Appeared = new List<CreatureSummoned>();

            public SummonWorld()
            {
                var defs = new List<NpcDef>
                {
                    Npc("Skeleton_Sword#1", "Skeleton", "Hero_Body_Skeleton"),
                    Npc("Skeleton#1", "Skeleton", "Hero_Body_Skeleton"),
                    Npc("SlaveZombie#1", "Zombie", "Hero_Body_SlaveZombie"),
                    Npc("Gotha_Demon_01", "Demon", "Demon_Body_01"),
                    Npc("Xardas", "Human", "Xardas_Body", faction: "Xardas"),
                };
                var db = new ContentDatabase();
                foreach (var a in TestRules.Attributes()) db.Put(a);
                var hero = Hero.CreateNew(db, TestRules.Progression(), RuleOptions.Default);
                Actors = new ActorRegistry(defs);
                Npcs = new NpcSimulation(defs, new RoutinePointCatalog(new List<RoutinePointDef>(), new List<AnchorDef>()),
                                         new RoutineSchedule(G3RoutineTables.Schedule()), new GameClock(10, timeScale: 12), new SeededRandom(1));
                Party = new PlayerParty(new ActorRoles(new ContentDatabase()), Events);
                Vitals = new ActorVitals(hero.Stats, id => Actors.TryGet(id, out var s) && s.Definition.Combat != null
                    ? s.Definition.Combat.Profile(Difficulty.Medium) : null, TestRules.Combat(), Events);
                Combat = new NpcCombatSystem(TestRules.CombatAi(), TestRules.Combat(), RuleOptions.Default, new SeededRandom(1),
                                             id => Actors.TryGet(id, out var s) ? s.Definition.Combat : null, null, () => 0, () => 0, Events);
                Summons = new SummonService(new SummonGroups(Rules.Summons), Actors, Npcs, Party, Vitals, Combat, () => defs, Events, null)
                {
                    Positions = id => id.IsPlayer ? Player : Npcs.TryGet(id, out var a) ? Npcs.PoseOf(a) : (WorldPose?)null,
                };
                Events.Subscribe<CreatureSummoned>(Appeared.Add);
            }

            static NpcDef Npc(string id, string species, string visual, string faction = null) =>
                new NpcDef(new ActorId(id), new TextId("FO_" + id), null, species, visual, new WorldPose(500, 0, 500, 0),
                           combat: new NpcCombatDef(0, 30, species, humanoid: species == "Human", faction: faction, hasEnclave: true, enclave: "Somewhere"));
        }

        static SpellDef Summon(string id, string effect, string spawn = null) =>
            new SpellDef(id, new[] { "It_" + id }, null, SpellCastType.Regular, SpellTarget.Caster, SpellKind.Summon, SpellDeity.Beliar,
                         60, 60, SpellDelivery.Instant, effect, "Summon", 0, spawn, null, null);

        [Test]
        public void PlayerSummon_CreatesAFollowerFromTheTemplate_ANewSummonKillsTheOld()
        {
            var w = new SummonWorld();
            var spell = Summon("Spl_SummonSkeleton", "MagicSummonSkeleton", "Skeleton_Sword");
            var first = w.Summons.Summon(ActorId.Player, spell, 100, new WorldPose(0, 0, 0, 0));
            Assert.That(first.Count, Is.EqualTo(1));
            var skeleton = first[0];
            Assert.That(skeleton.Name, Does.StartWith("Skeleton_Sword#smn"));
            Assert.That(w.Actors.TryGet(skeleton, out var state) && state.Definition.Visual == "Hero_Body_Skeleton", Is.True);
            Assert.That(state.Definition.Combat.Enclave, Is.Null, "a summon belongs to no enclave");
            Assert.That(w.Npcs.TryGet(skeleton, out var agent) && agent.Pose.DistanceTo(new WorldPose(0, 0, 0, 0)) > 1.5f, Is.True);
            Assert.That(w.Party.TypeOf(skeleton), Is.EqualTo(PartyMemberType.Summoned));
            Assert.That(w.Party.IsCompanion(skeleton), Is.False, "not a companion: no join limit, no dialog");
            Assert.That(w.Summons.SummonerOf(skeleton), Is.EqualTo(ActorId.Player));
            Assert.That(w.Appeared.Single().CastEffect, Is.True);

            var second = w.Summons.Summon(ActorId.Player, Summon("Spl_SummonDemon", "MagicSummonDemon", "Demon"), 100, new WorldPose(0, 0, 0, 0));
            Assert.That(w.Vitals.IsDead(skeleton), Is.True, "the previous group dies (ZS_RagDollDead)");
            Assert.That(w.Party.TryGet(skeleton, out _), Is.False);
            Assert.That(w.Actors.TryGet(second[0], out var demon) && demon.Definition.Species == "Demon", Is.True, "species fallback for Demon");

            w.Summons.Tick(SummonService.CorpseSeconds + 1);
            Assert.That(w.Actors.Exists(skeleton) || w.Npcs.TryGet(skeleton, out _), Is.False, "the body is removed later");
        }

        [Test]
        public void ArmyOfDarkness_ByInt_AndTheLeashKillsStrays()
        {
            var w = new SummonWorld();
            var army = w.Summons.Summon(ActorId.Player, Summon("Spl_SummonArmyOfDarkness", "MagicSummonArmyOfDarkness"), 260,
                                        new WorldPose(0, 0, 0, 0));
            Assert.That(army.Count, Is.EqualTo(8), "4 skeletons + 4 zombies at INT 250..299");
            Assert.That(army.Count(a => a.Name.StartsWith("SlaveZombie")), Is.EqualTo(4));
            w.Player = new WorldPose(45, 0, 0, 0);
            w.Summons.Tick(0.1f);
            Assert.That(army.All(w.Vitals.IsDead), Is.True, "more than 40 m from the player");
            Assert.That(w.Summons.Groups.HasSummons(ActorId.Player), Is.False);
        }

        [Test]
        public void NpcSummon_TakesTheCastersAlignment_AttacksAPlayerWithin20m_ElseDies()
        {
            var w = new SummonWorld();
            var xardas = new ActorId("Xardas");
            var near = w.Summons.Summon(xardas, Summon("Spl_SummonSkeleton", "MagicSummonSkeleton"), 300, new WorldPose(5, 0, 0, 0));
            Assert.That(w.Actors.TryGet(near[0], out var s) && s.AlignmentOr(null) == "Xardas", Is.True);
            Assert.That(w.Combat.Get(near[0]).State.Target, Is.EqualTo(ActorId.Player));
            Assert.That(w.Party.TryGet(near[0], out _), Is.False, "not in the hero's party");
            Assert.That(w.Summons.SummonerOf(near[0]), Is.EqualTo(xardas));

            var far = w.Summons.Summon(xardas, Summon("Spl_SummonSkeleton", "MagicSummonSkeleton"), 300, new WorldPose(30, 0, 0, 0));
            Assert.That(w.Vitals.IsDead(far[0]), Is.True);
            Assert.That(w.Summons.Summon(ActorId.Player, Summon("Spl_SummonMonster", "MagicSummonMonster"), 300, default), Is.Empty,
                        "Summon Monster is Runak's");
        }

        [Test]
        public void NpcSummon_DiesWhenItsFightEnds()
        {
            var w = new SummonWorld();
            var xardas = new ActorId("Xardas");
            var s = w.Summons.Summon(xardas, Summon("Spl_SummonSkeleton", "MagicSummonSkeleton"), 300, new WorldPose(5, 0, 0, 0))[0];
            w.Summons.Tick(0.1f);
            Assert.That(w.Vitals.IsDead(s), Is.False, "still fighting");
            w.Combat.Get(s).Disengage();
            w.Summons.Tick(0.1f);
            Assert.That(w.Vitals.IsDead(s), Is.True, "CP ZS_Attack_End: an NPC's summon kills itself after the fight");
        }

        [Test]
        public void Teleport_DismissesTheSummons_TheyDie()
        {
            var w = new SummonWorld();
            var s = w.Summons.Summon(ActorId.Player, Summon("Spl_SummonSkeleton", "MagicSummonSkeleton"), 100, default)[0];
            w.Party.PlayerTeleported();
            Assert.That(w.Vitals.IsDead(s), Is.True);
        }

        // ------------------------------------------------------------------------------------------- transformation, control

        [Test]
        public void Transformation_DurationAndForms()
        {
            Assert.That(TransformRules.DurationSeconds(100, "Wolf", true, _ => false), Is.EqualTo(100f));
            Assert.That(TransformRules.DurationSeconds(100, "Sabertooth", true, _ => false), Is.EqualTo(50f), "large form ×0.5");
            Assert.That(TransformRules.DurationSeconds(40, "Wolf", true, p => p == "Perk_Druid" || p == "Perk_Watermage"), Is.EqualTo(150f));
            Assert.That(TransformRules.DurationSeconds(100, "Wolf", false, _ => true), Is.EqualTo(100f), "perks only for the player");
            Assert.That(TransformRules.DurationSeconds(100, "Wolf", true, null, artefact: true), Is.EqualTo(-1f));
            Assert.That(TransformRules.AnimalFormTargetAllowed("Meatbug") && !TransformRules.CanTransformInto("Meatbug"), Is.True);
            Assert.That(TransformRules.CanTransformInto("Pig") || TransformRules.AnimalFormTargetAllowed("Deer"), Is.False);
            Assert.That(TransformRules.LevelUpOnReturnBonus(RuleOptions.Default), Is.EqualTo((5, 5)));
        }

        [Test]
        public void ControlSpells_AsCp()
        {
            Assert.That(ControlSpells.Fear(new ControlTarget { Species = "Wolf" }), Is.EqualTo(ControlOutcome.Works));
            Assert.That(ControlSpells.Fear(new ControlTarget { Species = "Troll" }), Is.EqualTo(ControlOutcome.AttacksCaster));
            Assert.That(ControlSpells.Fear(new ControlTarget { Species = "Human", Mage = true }), Is.EqualTo(ControlOutcome.AttacksCaster));
            var orc = new ControlTarget { Species = "Orc", CalmOrParalysed = true };
            Assert.That(ControlSpells.WordOfControl(orc, true, 199), Is.EqualTo(ControlOutcome.TooHard));
            Assert.That(ControlSpells.WordOfControl(orc, true, 200), Is.EqualTo(ControlOutcome.Works));
            Assert.That(ControlSpells.WordOfControl(orc, false, 300), Is.EqualTo(ControlOutcome.DoesntWork));
            Assert.That(ControlSpells.TameAnimal(new ControlTarget { Species = "Ripper", Animal = true }, 99), Is.EqualTo(ControlOutcome.TooHard));
            Assert.That(ControlSpells.Charm(new ControlTarget { Species = "Human" }), Is.EqualTo(ControlOutcome.Works), "CP: guards too");
        }
    }
}
