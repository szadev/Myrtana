using System;
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
    /// <summary>
    /// The engine-free core of the active perception (perf-9): scan scheduling (first scan spread, interval, Active only,
    /// update LOD, the per-frame budget nearest first), a scan's consequence (an orc attacks an evil creature next to it),
    /// and no allocations per frame with a crowd of NPCs.
    /// </summary>
    public class PerceptionScannerTests
    {
        static SessionFixture Fixture(params NpcDef[] npcs) => new SessionFixture()
            .Add(SimulationSchema.PerceptionRulesFolder, G3CrimeImporter.Perception())
            .Add(RulesSchema.AttitudeRulesFolder, G3CrimeImporter.Attitudes())
            .Add(RulesSchema.CrimeRulesFolder, G3CrimeImporter.Crime())
            .Add(RulesSchema.EnclaveFolder, new EnclaveDef("Faring", "Orc"))
            .Add(SimulationSchema.NpcFolder, npcs);

        static NpcDef Orc(string name, float x, float z = 0) =>
            new NpcDef(new ActorId(name), new TextId("FO_" + name), "Orc_Warrior_Hard", "Orc", "visual", new WorldPose(x, 0, z, 0),
                       combat: new NpcCombatDef(5, 10, "Orc", true, faction: "Orc", enclave: "Faring", hasEnclave: true));

        static NpcDef Skeleton(string name, float x, float z) =>
            new NpcDef(new ActorId(name), new TextId("FO_" + name), "Skeleton", "Skeleton", "visual", new WorldPose(x, 0, z, 0),
                       combat: new NpcCombatDef(5, 10, "Skeleton", false, evil: true));

        static PerceptionScanner.PlayerInput FarPlayer() => new PerceptionScanner.PlayerInput
        {
            Target = new SenseTarget { Id = ActorId.Player, Pose = new WorldPose(500, 0, 500, 0) },
            Perceivable = false,
        };

        static List<PerceptionScanner.ViewInput> Views(GameSession s, int interval = 1) =>
            s.Npcs.Agents.Where(a => a.Detail != DetailLevel.Abstract)
             .Select(a => new PerceptionScanner.ViewInput(a.Id, a.Pose, interval, false)).ToList();

        [Test]
        public void FirstScanIsSpread_ThenEveryInterval_OnlyForActiveNpcs()
        {
            using (var f = Fixture(Orc("A", 0), Orc("B", 50)))
            {
                var s = f.NewGame();
                s.Npcs.SetDetail(new ActorId("A"), DetailLevel.Active);
                s.Npcs.SetDetail(new ActorId("B"), DetailLevel.Near); // Near NPCs only receive broadcasts
                var scanner = new PerceptionScanner(s, () => 0.5);
                var views = Views(s);
                double interval = s.Crime.Perception.ActiveInterval;
                scanner.Frame(FarPlayer(), views, 0); // schedules A at 0.5 × interval
                Assert.That(scanner.Stats.Scans, Is.EqualTo(0));
                scanner.Frame(FarPlayer(), views, 0.49 * interval);
                Assert.That(scanner.Stats.Scans, Is.EqualTo(0));
                scanner.Frame(FarPlayer(), views, 0.5 * interval);
                Assert.That(scanner.Stats.Scans, Is.EqualTo(1));
                scanner.Frame(FarPlayer(), views, 1.49 * interval);
                Assert.That(scanner.Stats.Scans, Is.EqualTo(1));
                scanner.Frame(FarPlayer(), views, 1.5 * interval);
                Assert.That(scanner.Stats.Scans, Is.EqualTo(2), "B (Near) never scans");
            }
        }

        [Test]
        public void UpdateLod_SlowsACalmViewsScans_ButNotABusyBody()
        {
            using (var f = Fixture(Orc("A", 0), Orc("B", 50)))
            {
                var s = f.NewGame();
                s.Npcs.SetDetail(new ActorId("A"), DetailLevel.Active);
                s.Npcs.SetDetail(new ActorId("B"), DetailLevel.Active);
                var scanner = new PerceptionScanner(s, () => 0);
                var a = s.Npcs.Agents.First(x => x.Id.Name == "A");
                var b = s.Npcs.Agents.First(x => x.Id.Name == "B");
                var views = new List<PerceptionScanner.ViewInput>
                {
                    new PerceptionScanner.ViewInput(a.Id, a.Pose, 3, false), // far and calm: every 3rd frame
                    new PerceptionScanner.ViewInput(b.Id, b.Pose, 3, true),  // its body is taken: full rate
                };
                double interval = s.Crime.Perception.ActiveInterval;
                scanner.Frame(FarPlayer(), views, 0);
                scanner.Frame(FarPlayer(), views, 0.01); // both scan (due at 0)
                Assert.That(scanner.Stats.Scans, Is.EqualTo(2));
                scanner.Frame(FarPlayer(), views, 0.01 + interval);
                Assert.That(scanner.Stats.Scans, Is.EqualTo(3), "only B after one interval");
                scanner.Frame(FarPlayer(), views, 0.01 + 3 * interval);
                Assert.That(scanner.Stats.Scans, Is.EqualTo(5), "A after three");
            }
        }

        [Test]
        public void AtMostScansPerFrame_TheRestNextFrame()
        {
            var orcs = Enumerable.Range(0, PerceptionScanner.ScansPerFrame + 3).Select(i => Orc("Orc" + i, i * 30f)).ToArray();
            using (var f = Fixture(orcs))
            {
                var s = f.NewGame();
                foreach (var o in orcs) s.Npcs.SetDetail(o.Id, DetailLevel.Active);
                var scanner = new PerceptionScanner(s, () => 0);
                var views = Views(s);
                scanner.Frame(FarPlayer(), views, 0);
                scanner.Frame(FarPlayer(), views, 0.01);
                Assert.That(scanner.Stats.Scans, Is.EqualTo(PerceptionScanner.ScansPerFrame));
                scanner.Frame(FarPlayer(), views, 0.02);
                Assert.That(scanner.Stats.Scans, Is.EqualTo(PerceptionScanner.ScansPerFrame + 3));
            }
        }

        [Test]
        public void AnOrcSeesAnEvilCreatureInFront_AndAttacksIt()
        {
            using (var f = Fixture(Orc("Orc", 0), Skeleton("Skel", 0, 5)))
            {
                var s = f.NewGame();
                var orc = new ActorId("Orc");
                s.Npcs.SetDetail(orc, DetailLevel.Active);
                s.Npcs.SetDetail(new ActorId("Skel"), DetailLevel.Near);
                Assert.That(s.Crime.Attitudes.Of(orc, new ActorId("Skel")), Is.EqualTo(Attitude.Hostile));
                var scanner = new PerceptionScanner(s, () => 0);
                var views = Views(s);
                scanner.Frame(FarPlayer(), views, 0);
                scanner.Frame(FarPlayer(), views, 0.01);
                Assert.That(s.NpcCombat.Get(orc)?.IsFighting, Is.True);
                Assert.That(s.NpcCombat.Get(orc).State.Target, Is.EqualTo(new ActorId("Skel")));
            }
        }

        [Test]
        public void RebelOfAnOrcEnclave_AttacksTheLuredOrcOnSight() // fix-129/135: Daryl and Montera_Orc_03
        {
            var daryl = new NpcDef(new ActorId("Daryl"), new TextId("FO_Daryl"), "Hum_Warrior_Calm", "Human", "visual", new WorldPose(0, 0, 0, 0),
                                   combat: new NpcCombatDef(15, 35, "Human", true, faction: "Reb", enclave: "Montera", hasEnclave: true));
            var orcDef = new NpcDef(new ActorId("MOrc"), new TextId("FO_MOrc"), "Orc_Warrior_Hard", "Orc", "visual", new WorldPose(0, 0, 1.5f, 180),
                                    combat: new NpcCombatDef(10, 30, "Orc", true, faction: "Orc", enclave: "Montera", hasEnclave: true));
            using (var f = new SessionFixture()
                .Add(SimulationSchema.PerceptionRulesFolder, G3CrimeImporter.Perception())
                .Add(RulesSchema.AttitudeRulesFolder, G3CrimeImporter.Attitudes())
                .Add(RulesSchema.CrimeRulesFolder, G3CrimeImporter.Crime())
                .Add(RulesSchema.EnclaveFolder, new EnclaveDef("Montera", "Orc"))
                .Add(SimulationSchema.NpcFolder, daryl, orcDef))
            {
                var s = f.NewGame();
                var d = new ActorId("Daryl"); var o = new ActorId("MOrc");
                s.Npcs.SetDetail(d, DetailLevel.Active);
                s.Npcs.SetDetail(o, DetailLevel.Active);
                var scanner = new PerceptionScanner(s, () => 0);
                var views = Views(s);
                scanner.Frame(FarPlayer(), views, 0);
                scanner.Frame(FarPlayer(), views, 0.01);
                Assert.That(s.NpcCombat.Get(d)?.State.Target, Is.EqualTo(o), "the rebel attacks the orc of its own enclave");
            }
        }

        [Test]
        public void DuelKnockOut_TheCampWatchesAndComments() // fix-135: the duel itself calls nobody in
        {
            NpcDef Reb(string n, float x) => new NpcDef(new ActorId(n), new TextId("FO_" + n), "Hum_Warrior_Calm", "Human", "visual", new WorldPose(x, 0, 0, 0),
                                   combat: new NpcCombatDef(15, 35, "Human", true, faction: "Reb", enclave: "Montera", hasEnclave: true));
            using (var f = new SessionFixture()
                .Add(SimulationSchema.PerceptionRulesFolder, G3CrimeImporter.Perception())
                .Add(RulesSchema.AttitudeRulesFolder, G3CrimeImporter.Attitudes())
                .Add(RulesSchema.CrimeRulesFolder, G3CrimeImporter.Crime())
                .Add(RulesSchema.EnclaveFolder, new EnclaveDef("Montera", "Orc"))
                .Add(SimulationSchema.NpcFolder, Reb("Daryl", 0), Reb("Reb5", 2)))
            {
                var s = f.NewGame();
                var d = new ActorId("Daryl"); var r = new ActorId("Reb5");
                s.Npcs.SetDetail(d, DetailLevel.Active);
                s.Npcs.SetDetail(r, DetailLevel.Active);
                s.Crime.ReportPlayer(new WorldPose(3, 0, 0, 0));
                var reactions = new List<NpcReacted>();
                s.Events.Subscribe<NpcReacted>(e => { if (e.Npc == d) reactions.Add(e); });
                s.NpcCombat.Engage(r, ActorId.Player, AttackReason.Duel);
                s.Tick(0.1f);
                s.Crime.CombatBroadcasts.Report(r, ActorId.Player, CombatBroadcasts.HitEffect.Hurt);
                s.Crime.CombatBroadcasts.Report(ActorId.Player, r, CombatBroadcasts.HitEffect.Hurt);
                s.Tick(0.1f);
                s.Crime.CombatBroadcasts.Report(ActorId.Player, r, CombatBroadcasts.HitEffect.KnockedOut);
                Assert.That(s.NpcCombat.Get(d)?.IsFighting == true, Is.False, "Daryl does not join a duel of his man");
                Assert.That(reactions.Select(x => x.Reaction.Kind), Is.EqualTo(new[] { ReactionKind.Watch, ReactionKind.Watch, ReactionKind.Comment }));
            }
        }

        [Test]
        public void TheHerosGuide_DoesNotReEngageAfterALeashBreakOff()
        {
            // fix-41 (CP AssessActivePerception: party leader == player skips the hostile step, guide included): the guide's
            // own scan picked the enemy it had just broken off from again (r28: Cole 63x, Karlen 51x, Xardas vs IceWolf).
            using (var f = Fixture(Orc("Orc", 0), Skeleton("Skel", 0, 5)))
            {
                var s = f.NewGame();
                var orc = new ActorId("Orc");
                s.Npcs.SetDetail(orc, DetailLevel.Active);
                s.Npcs.SetDetail(new ActorId("Skel"), DetailLevel.Near);
                var scanner = new PerceptionScanner(s, () => 0);
                var views = Views(s);
                scanner.Frame(FarPlayer(), views, 0);
                scanner.Frame(FarPlayer(), views, 0.01);
                Assert.That(s.NpcCombat.Get(orc)?.IsFighting, Is.True, "not guiding yet: it attacks on sight");

                s.Party.Members.StartGuide(orc, "FP_Somewhere", null);
                s.NpcCombat.Get(orc).Disengage(); // the party leash breaks off
                long scans = scanner.Stats.Scans;
                double interval = s.Crime.Perception.ActiveInterval;
                for (int i = 1; i <= 5; i++) scanner.Frame(FarPlayer(), views, 0.01 + i * interval);
                Assert.That(scanner.Stats.Scans, Is.GreaterThan(scans), "the guide keeps scanning");
                Assert.That(s.NpcCombat.Get(orc).IsFighting, Is.False, "the guide is on the hero's side: no hostile step");
            }
        }

        [Test]
        public void ACrowdScansWithoutAllocating()
        {
            // 40 orcs in a 30 × 30 m square, all Active, the player among them (perceivable): every frame scans.
            var orcs = Enumerable.Range(0, 40).Select(i => Orc("Orc" + i, (i % 7) * 4.5f, (i / 7) * 4.5f)).ToArray();
            using (var f = Fixture(orcs))
            {
                var s = f.NewGame();
                foreach (var o in orcs) s.Npcs.SetDetail(o.Id, DetailLevel.Active);
                var random = new Random(7);
                var scanner = new PerceptionScanner(s, () => random.NextDouble());
                var views = Views(s);
                var player = new PerceptionScanner.PlayerInput
                {
                    Target = new SenseTarget { Id = ActorId.Player, Pose = new WorldPose(12, 0, 12, 0) },
                    Perceivable = true,
                };
                double now = 0;
                for (int i = 0; i < 200; i++) scanner.Frame(player, views, now += 1 / 60.0); // warm-up
                long before = GC.GetAllocatedBytesForCurrentThread();
                long scans = scanner.Stats.Scans;
                for (int i = 0; i < 300; i++) scanner.Frame(player, views, now += 1 / 60.0);
                long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                Assert.That(scanner.Stats.Scans - scans, Is.GreaterThan(150), "the crowd scans about once a second each");
                Assert.That(allocated / 300, Is.LessThan(64), "bytes per frame");
            }
        }

        static PerceptionScanner.PlayerInput PlayerAt(float x, float z) => new PerceptionScanner.PlayerInput
        {
            Target = new SenseTarget { Id = ActorId.Player, Pose = new WorldPose(x, 0, z, 180), Moving = true },
            Perceivable = true,
        };

        [Test]
        public void AWokenRaiderOrc_AttacksTheHeroInFrontOfIt() // fix-217: Nrd_OrcCamp_3 tent, the orc stood and watched
        {
            // CP ZS_ObserveSuspect sets the callback OnGoto = AssessActivePerception: the woken sleeper perceives as in its
            // routine, so an enemy 2 m in front is attacked instead of watched for as long as he stays by its bed.
            var raider = new NpcDef(new ActorId("Raider"), new TextId("FO_Raider"), "Orc_Warrior_Hard", "Orc", "visual", new WorldPose(0, 0, 0, 0),
                                    combat: new NpcCombatDef(10, 30, "Orc", true, faction: "Out", enclave: "Nrd_OrcCamp_3", hasEnclave: true));
            using (var f = new SessionFixture()
                .Add(SimulationSchema.PerceptionRulesFolder, G3CrimeImporter.Perception())
                .Add(RulesSchema.AttitudeRulesFolder, G3CrimeImporter.Attitudes())
                .Add(RulesSchema.CrimeRulesFolder, G3CrimeImporter.Crime())
                .Add(RulesSchema.EnclaveFolder, new EnclaveDef("Nrd_OrcCamp_3", "Out"))
                .Add(SimulationSchema.NpcFolder, raider))
            {
                var s = f.NewGame();
                var orc = raider.Id;
                s.Npcs.SetDetail(orc, DetailLevel.Active);
                Assert.That(s.Crime.Attitudes.Of(orc, ActorId.Player), Is.EqualTo(Attitude.Hostile), "precondition");
                s.Crime.ReportPlayer(new WorldPose(0, 0, 2, 180));
                s.Life.Tasks.StartObserveWaker(orc, ActorId.Player, 0); // ZS_ReactToFootsteps: woke up, stood, watches
                var scanner = new PerceptionScanner(s, () => 0);
                var views = Views(s);
                scanner.Frame(PlayerAt(0, 2), views, 0);
                scanner.Frame(PlayerAt(0, 2), views, 0.01);
                Assert.That(s.NpcCombat.Get(orc)?.IsFighting, Is.True, "the woken enemy attacks");
                Assert.That(s.NpcCombat.Get(orc).State.Target, Is.EqualTo(ActorId.Player));
                Assert.That(s.Life.Tasks.Has(orc), Is.False, "the watching is over");
            }
        }

        [Test]
        public void AWokenNeutralOrc_GoesOnWatching()
        {
            using (var f = Fixture(Orc("Orc", 0)))
            {
                var s = f.NewGame();
                var orc = new ActorId("Orc");
                s.Npcs.SetDetail(orc, DetailLevel.Active);
                Assume.That(s.Crime.Attitudes.Of(orc, ActorId.Player), Is.Not.EqualTo(Attitude.Hostile));
                s.Crime.ReportPlayer(new WorldPose(0, 0, 2, 180));
                s.Life.Tasks.StartObserveWaker(orc, ActorId.Player, 0);
                var scanner = new PerceptionScanner(s, () => 0);
                var views = Views(s);
                scanner.Frame(PlayerAt(0, 2), views, 0);
                scanner.Frame(PlayerAt(0, 2), views, 0.01);
                Assert.That(s.NpcCombat.Get(orc)?.IsFighting == true, Is.False);
                Assert.That(s.Life.Tasks.KindOf(orc), Is.EqualTo(ReactionTaskKind.ObserveWaker));
            }
        }

        [Test]
        public void AWokenOrcThatWarnsTheHero_StaysUp() // fix-231: it lay down and stood up again in front of the hero
        {
            using (var f = Fixture(Orc("Orc", 0)))
            {
                var s = f.NewGame();
                var orc = new ActorId("Orc");
                s.Npcs.SetDetail(orc, DetailLevel.Active);
                Assume.That(s.Crime.Attitudes.Of(orc, ActorId.Player), Is.Not.EqualTo(Attitude.Hostile));
                s.Crime.ReportPlayer(new WorldPose(0, 0, 2, 180));
                s.Life.Tasks.StartObserveWaker(orc, ActorId.Player, 0);
                var reactions = new List<ReactionKind>();
                using (s.Events.Subscribe<NpcReacted>(e => { if (e.Npc == orc) reactions.Add(e.Reaction.Kind); }))
                {
                    var scanner = new PerceptionScanner(s, () => 0);
                    var views = Views(s);
                    var armed = PlayerAt(0, 2);
                    armed.Conduct.WeaponDrawn = true; // the hero stands at its bed with his sword out: "put the weapon away!"
                    scanner.Frame(armed, views, 0);
                    scanner.Frame(armed, views, 0.01);
                    Assume.That(reactions, Does.Contain(ReactionKind.Warn), "the woken orc warns");
                    Assert.That(s.Life.Tasks.KindOf(orc), Is.EqualTo(ReactionTaskKind.ObserveWaker),
                                "it stays on its feet watching the hero instead of going back to sleep");
                }
            }
        }
    }
}
