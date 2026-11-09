using System.Collections.Generic;
using Myrtana.Core;
using Myrtana.Import.G3;
using Myrtana.Simulation;
using NUnit.Framework;

namespace Myrtana.Tests.Simulation
{
    /// <summary>The story's SetRoutine: an NPC switches to another named routine and back.</summary>
    public sealed class NamedRoutineTests
    {
        static readonly List<RoutinePointDef> Points = new List<RoutinePointDef>
        {
            Point("anvil", 10, 0), Point("bench", 0, 20), Point("bed", -30, 0), Point("ambush", 500, 500),
        };

        static RoutinePointDef Point(string id, float x, float z) =>
            new RoutinePointDef(new Id<RoutinePointDef>(id), id, "Stand", new[] { new WorldPose(x, 0, z, 0) });

        static Id<RoutinePointDef> P(string id) => new Id<RoutinePointDef>(id);

        static readonly NpcDef Smith = new NpcDef(new ActorId("Smith"), default, null, "Human", "v", new WorldPose(0, 0, 0, 0),
            new NpcRoutineDef(P("anvil"), P("bench"), P("bed")), routines: new Dictionary<string, NpcRoutineDef>
            {
                ["RebAmbush"] = new NpcRoutineDef(P("ambush")),
            });

        static NpcSimulation Create(GameClock clock)
        {
            var sim = new NpcSimulation(new[] { Smith }, new RoutinePointCatalog(Points, new List<AnchorDef>()),
                                        new RoutineSchedule(G3RoutineTables.Schedule()), clock, new SeededRandom(7));
            sim.PlaceAtRoutineStart();
            return sim;
        }

        [Test]
        public void SetRoutine_HeadsForTheNamedRoutinesPoint_AndAnUnknownNameReturnsToTheOwnRoutine()
        {
            var sim = Create(new GameClock(10, timeScale: 12));
            sim.TryGet(Smith.Id, out var smith);
            Assert.That(smith.Station.Point, Is.EqualTo(P("anvil")));

            Assert.That(sim.SetRoutine(Smith.Id, "RebAmbush"), Is.True);
            Assert.That(smith.RoutineName, Is.EqualTo("RebAmbush"));
            Assert.That(smith.Activity, Is.EqualTo(NpcActivity.Travelling));
            Assert.That(smith.Station.Point, Is.EqualTo(P("ambush")));

            sim.SetRoutine(Smith.Id, "Start");
            Assert.That(smith.RoutineName, Is.Null);
            Assert.That(smith.Station.Point, Is.EqualTo(P("anvil")));
            Assert.That(sim.SetRoutine(new ActorId("Nobody"), "Start"), Is.False);
        }

        [Test]
        public void SetRoutine_ARoutineWithoutPoints_LeavesTheNpcIdlingWhereItStands()
        {
            var sim = Create(new GameClock(10, timeScale: 12));
            sim.TryGet(Smith.Id, out var smith);
            sim.SetRoutine(Smith.Id, "RebAmbush"); // walking away from the anvil
            var before = sim.PoseOf(smith);
            Assert.That(sim.SetRoutine(Smith.Id, "Mine"), Is.True, "G3 Hamil 'Mine': a name without points");
            Assert.That(smith.RoutineName, Is.EqualTo("Mine"));
            Assert.That(smith.Routine, Is.SameAs(NpcAgent.NoPoints));
            Assert.That(smith.Station.IsNone, Is.True);
            Assert.That(smith.Activity, Is.EqualTo(NpcActivity.Idle));
            sim.SetDetail(Smith.Id, DetailLevel.Near);
            for (int i = 0; i < 5; i++) sim.Tick();
            Assert.That(smith.Station.IsNone, Is.True, "no point to go to: not back to the own routine");
            Assert.That(sim.PoseOf(smith).DistanceTo(before), Is.LessThan(0.01f));
        }

        [Test]
        public void FullStop_CutsTheWalk_ThenTheRoutineGoesOn()
        {
            var sim = Create(new GameClock(10, timeScale: 12));
            sim.TryGet(Smith.Id, out var smith);
            sim.SetRoutine(Smith.Id, "RebAmbush");
            Assert.That(smith.Activity, Is.EqualTo(NpcActivity.Travelling));
            Assert.That(sim.FullStop(Smith.Id), Is.True);
            Assert.That(smith.Activity, Is.EqualTo(NpcActivity.Idle));
            Assert.That(smith.Station.IsNone, Is.True);
            sim.SetDetail(Smith.Id, DetailLevel.Near);
            sim.Tick();
            Assert.That(smith.Station.Point, Is.EqualTo(P("ambush")), "replans its routine from where it stopped");
            Assert.That(sim.FullStop(new ActorId("Nobody")), Is.False);
        }

        [Test]
        public void Held_StaysWhereTheFullStopLeftIt_UntilReleased()
        {
            var sim = Create(new GameClock(10, timeScale: 12));
            sim.TryGet(Smith.Id, out var smith);
            sim.SetRoutine(Smith.Id, "RebAmbush");
            bool held = true;
            sim.Held = id => held && id == Smith.Id;
            sim.FullStop(Smith.Id);
            sim.SetDetail(Smith.Id, DetailLevel.Near);
            for (int i = 0; i < 5; i++) sim.Tick();
            Assert.That(smith.Activity, Is.EqualTo(NpcActivity.Idle), "frozen: no new walk (editor-runner-16 freeze)");
            Assert.That(smith.Station.IsNone, Is.True);
            held = false;
            sim.Tick();
            Assert.That(smith.Station.Point, Is.EqualTo(P("ambush")), "released: the routine goes on");
        }

        [Test]
        public void Lifeless_ACorpseKeepsItsPlace_OutOfViewToo()
        {
            // fix-80: a dead NPC (session: Vitals.IsDead) plans no walk; its body stays where the FullStop of its death left it
            var sim = Create(new GameClock(10, timeScale: 12));
            sim.TryGet(Smith.Id, out var smith);
            sim.SetRoutine(Smith.Id, "RebAmbush");
            bool dead = true;
            sim.Lifeless = id => dead && id == Smith.Id;
            sim.FullStop(Smith.Id);
            var at = sim.PoseOf(smith);
            sim.SetDetail(Smith.Id, DetailLevel.Abstract);
            for (int i = 0; i < 20; i++) sim.Tick();
            Assert.That(smith.Activity, Is.EqualTo(NpcActivity.Idle));
            Assert.That(smith.Station.IsNone, Is.True);
            Assert.That(sim.PoseOf(smith).DistanceTo(at), Is.LessThan(0.01f));
            dead = false; // revived (CP Respawn)
            sim.SetDetail(Smith.Id, DetailLevel.Near);
            sim.Tick();
            Assert.That(smith.Station.Point, Is.EqualTo(P("ambush")), "alive again: the routine goes on");
        }

        [Test]
        public void SetRoutine_WhileTalking_ReplansWhenTheTalkEnds()
        {
            var sim = Create(new GameClock(10, timeScale: 12));
            sim.TryGet(Smith.Id, out var smith);
            sim.SetTalking(Smith.Id, true);
            sim.SetRoutine(Smith.Id, "RebAmbush");
            Assert.That(smith.Activity, Is.EqualTo(NpcActivity.Talking));
            sim.SetTalking(Smith.Id, false);
            sim.SetDetail(Smith.Id, DetailLevel.Near);
            sim.Tick();
            Assert.That(smith.Station.Point, Is.EqualTo(P("ambush")));
        }
    }
}
