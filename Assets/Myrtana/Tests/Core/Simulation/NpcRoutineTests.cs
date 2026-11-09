using System.Collections.Generic;
using System.IO;
using System.Linq;
using Myrtana.Core;
using Myrtana.Import.G3;
using Myrtana.Simulation;
using NUnit.Framework;

namespace Myrtana.Tests.Simulation
{
    public sealed class RoutineScheduleTests
    {
        static readonly RoutineSchedule G3 = new RoutineSchedule(G3RoutineTables.Schedule());

        [TestCase(0f, DayPhase.Sleep)]
        [TestCase(5.99f, DayPhase.Sleep)]
        [TestCase(6f, DayPhase.Work)]
        [TestCase(19.99f, DayPhase.Work)]
        [TestCase(20f, DayPhase.Relax)]
        [TestCase(23.9f, DayPhase.Relax)]
        [TestCase(24f, DayPhase.Sleep)]
        public void HumansAndOrcs_SleepWorkRelax(float hour, DayPhase expected)
        {
            Assert.That(G3.PhaseOf("Human", hour), Is.EqualTo(expected));
            Assert.That(G3.PhaseOf("Orc", hour), Is.EqualTo(expected));
        }

        [TestCase("Scavenger", 4.5f, DayPhase.Sleep)]
        [TestCase("Scavenger", 5f, DayPhase.Work)]
        [TestCase("Scavenger", 23.5f, DayPhase.Sleep)]
        [TestCase("Wolf", 3f, DayPhase.Work)]
        [TestCase("Wolf", 5f, DayPhase.Sleep)]
        [TestCase("Shadowbeast", 12f, DayPhase.Sleep)]
        [TestCase("Shadowbeast", 22f, DayPhase.Work)]
        [TestCase("Goblin", 2f, DayPhase.Work)]
        [TestCase(null, 2f, DayPhase.Work)]
        public void OtherSpecies_FollowTheirOwnHours(string species, float hour, DayPhase expected) =>
            Assert.That(G3.PhaseOf(species, hour), Is.EqualTo(expected));

        [Test]
        public void MissingPhasePoint_FallsBackToWorkingPoint()
        {
            var routine = new NpcRoutineDef(working: P("anvil"));
            Assert.That(routine.PointFor(DayPhase.Sleep), Is.EqualTo(P("anvil")));
            Assert.That(routine.PointFor(DayPhase.Relax), Is.EqualTo(P("anvil")));
            Assert.That(new NpcRoutineDef(P("anvil"), P("bench")).PointFor(DayPhase.Relax), Is.EqualTo(P("bench")));
        }

        static Id<RoutinePointDef> P(string id) => new Id<RoutinePointDef>(id);
    }

    public sealed class NpcSimulationTests
    {
        // A small village: an anvil with two slots, a bench, two beds, a roam anchor with three spots, a private chair.
        static readonly List<RoutinePointDef> Points = new List<RoutinePointDef>
        {
            Point("anvil", "Anvil", Pose(10, 0), Pose(11, 0)),
            Point("bench", "Bench", Pose(0, 20)),
            Point("bed1", "Bed", Pose(-30, 0)),
            Point("bed2", "Bed", Pose(-31, 0)),
            Point("roam1", "Stand", "herd", Pose(100, 0)),
            Point("roam2", "Stand", "herd", Pose(110, 0)),
            Point("roam3", "Stand", "herd", Pose(120, 0)),
            new RoutinePointDef(Id("throne"), "throne", "Throne", new[] { Pose(5, 5) }, owner: new ActorId("Boss")),
        };

        static readonly List<AnchorDef> Anchors = new List<AnchorDef> { new AnchorDef(new Id<AnchorDef>("herd"), AnchorKind.Roam) };

        GameClock _clock;

        NpcSimulation Create(float hour, params NpcDef[] npcs)
        {
            _clock = new GameClock(hour, timeScale: 12);
            var sim = new NpcSimulation(npcs, new RoutinePointCatalog(Points, Anchors), new RoutineSchedule(G3RoutineTables.Schedule()),
                                        _clock, new SeededRandom(7));
            sim.PlaceAtRoutineStart();
            return sim;
        }

        static NpcDef Smith(string name, string work = "anvil", string relax = "bench", string sleep = "bed1") =>
            new NpcDef(new ActorId(name), default, null, "Human", "visual", Pose(0, 0), new NpcRoutineDef(Id(work), Id(relax), Id(sleep)));

        [Test]
        public void NewGame_PlacesNpcsAtTheirPhaseStation_OneNpcPerObject()
        {
            var sim = Create(8, Smith("A"), Smith("B"));
            var a = Agent(sim, "A");
            var b = Agent(sim, "B");
            Assert.That(a.Activity, Is.EqualTo(NpcActivity.AtStation));
            Assert.That(a.Station, Is.EqualTo(new Station(Id("anvil"), 0)));
            Assert.That(a.Pose, Is.EqualTo(Pose(10, 0)));
            // fix-66: the anvil's two use poses are its two sides, not two places (G3 gCInteraction_PS has one User)
            Assert.That(b.Activity, Is.EqualTo(NpcActivity.Idle), "one smith per anvil");
            Assert.That(b.Pose, Is.EqualTo(Pose(0, 0)), "keeps its spawn pose");
        }

        [Test]
        public void PhaseChange_SendsNpcToTheRelaxPoint_AndAbstractTravelArrivesAfterTheEta()
        {
            var sim = Create(19.99f, Smith("A"));
            var a = Agent(sim, "A");
            Advance(sim, 4); // 20:00:12 game time
            Assert.That(a.Phase, Is.EqualTo(DayPhase.Relax));
            Assert.That(a.Activity, Is.EqualTo(NpcActivity.Travelling));
            Assert.That(a.Station.Point, Is.EqualTo(Id("bench")));
            Assert.That(sim.Claims.TryGetUser(new Station(Id("anvil"), 0), out _), Is.False, "the anvil is released");

            // Anvil (10,0) → bench (0,20): 22.4 m × 1.3 / 1.6 m/s ≈ 18 real seconds.
            var halfway = sim.PoseOf(a);
            Assert.That(halfway.X, Is.LessThanOrEqualTo(10f).And.GreaterThan(0f));
            Advance(sim, 20);
            Assert.That(a.Activity, Is.EqualTo(NpcActivity.AtStation));
            Assert.That(sim.PoseOf(a), Is.EqualTo(Pose(0, 20)));
        }

        [Test]
        public void ArenaFight_SendsTheOpponentIntoTheRing_AndTheRoutineResumesAfterIt()
        {
            // G3 SendToArena + ZS_Arena (editor-runner-26: the Paladin stayed on the MoraSul stands 6 m above the ring).
            var sim = Create(8, Smith("A"));
            var a = Agent(sim, "A");
            var ring = new WorldPose(10, 0, 30, 0);
            Assert.That(sim.InArena(a.Id), Is.Null, "no arena fight");
            bool running = true;
            sim.ArenaPointOf = id => running && id == a.Id ? ring : (WorldPose?)null;
            Advance(sim, 1);
            Assert.That(a.Activity, Is.EqualTo(NpcActivity.Travelling));
            Assert.That(sim.InArena(a.Id), Is.False, "on its way");
            Assert.That(sim.Runs(a), Is.True, "fix-84: ZS_Arena's goto runs (walk mode 2) — a smith walks to his routine points");
            Assert.That(sim.Claims.TryGetUser(new Station(Id("anvil"), 0), out _), Is.False, "the anvil is released");
            Advance(sim, 40);
            Assert.That(sim.InArena(a.Id), Is.True, "waits in the ring");
            Assert.That(sim.PoseOf(a).DistanceTo(ring), Is.LessThan(NpcSimulation.ArenaReach));
            Advance(sim, 60);
            Assert.That(sim.InArena(a.Id), Is.True, "stays there while the arena runs (no routine, no wandering)");

            running = false;
            Advance(sim, 40);
            Assert.That(a.Station.Point, Is.EqualTo(Id("anvil")), "back to the routine");
            Assert.That(sim.Runs(a), Is.False, "the way back is the normal travel gait");
        }

        [Test]
        public void SleepingPhase_UsesTheSleepingPoint()
        {
            var sim = Create(2, Smith("A"));
            Assert.That(Agent(sim, "A").Station.Point, Is.EqualTo(Id("bed1")));
        }

        [Test]
        public void RoamAnchor_MovesToAnotherPointAfterTheStayTime()
        {
            var cow = new NpcDef(new ActorId("Cow"), default, null, "Cow", "cow", Pose(100, 0), new NpcRoutineDef(Id("roam1")));
            var sim = Create(10, cow);
            var a = Agent(sim, "Cow");
            var first = a.Station.Point;
            Advance(sim, 2160 / 12f + 1); // stay time is 2160 game seconds
            Assert.That(a.Activity, Is.EqualTo(NpcActivity.Travelling));
            Assert.That(a.Station.Point, Is.Not.EqualTo(first));
            Assert.That(a.Station.Point.Name, Does.StartWith("roam"));
        }

        [Test]
        public void OwnedPoint_IsOnlyUsedByItsOwner()
        {
            var thief = new NpcDef(new ActorId("Thief"), default, null, "Human", "v", Pose(0, 0), new NpcRoutineDef(Id("throne")));
            var boss = new NpcDef(new ActorId("Boss"), default, null, "Human", "v", Pose(0, 0), new NpcRoutineDef(Id("throne")));
            var sim = Create(10, thief, boss);
            Assert.That(Agent(sim, "Thief").Activity, Is.EqualTo(NpcActivity.Idle));
            Assert.That(Agent(sim, "Boss").Station.Point, Is.EqualTo(Id("throne")));
        }

        [Test]
        public void ActiveBody_DrivesTheNpc_AndLeavingActiveContinuesAbstractly()
        {
            var sim = Create(19.99f, Smith("A"));
            var a = Agent(sim, "A");
            sim.SetDetail(a.Id, DetailLevel.Active);
            Advance(sim, 4);
            Assert.That(a.Activity, Is.EqualTo(NpcActivity.Travelling));

            Advance(sim, 60); // an active NPC only arrives when its body says so
            Assert.That(a.Activity, Is.EqualTo(NpcActivity.Travelling));
            sim.ReportPose(a.Id, Pose(5, 10));
            Assert.That(sim.PoseOf(a), Is.EqualTo(Pose(5, 10)));

            sim.SetDetail(a.Id, DetailLevel.Abstract); // rest of the way (11.2 m) as abstract travel
            Assert.That(sim.PoseOf(a).X, Is.EqualTo(5f).Within(0.01f));
            Advance(sim, 10);
            Assert.That(a.Activity, Is.EqualTo(NpcActivity.AtStation));

            sim.SetDetail(a.Id, DetailLevel.Active);
            Advance(sim, 0.1f);
            sim.ReportArrived(a.Id);
            Assert.That(a.Activity, Is.EqualTo(NpcActivity.AtStation));
        }

        [Test]
        public void Talking_FreezesTheNpc_UntilTheConversationEnds()
        {
            var sim = Create(19.99f, Smith("A"));
            var a = Agent(sim, "A");
            Advance(sim, 4);
            Advance(sim, 5);
            sim.SetTalking(a.Id, true);
            var stopped = sim.PoseOf(a);
            Advance(sim, 60);
            Assert.That(a.Activity, Is.EqualTo(NpcActivity.Talking));
            Assert.That(sim.PoseOf(a), Is.EqualTo(stopped));
            sim.SetTalking(a.Id, false);
            Assert.That(a.Activity, Is.EqualTo(NpcActivity.Travelling));
            Advance(sim, 30);
            Assert.That(a.Activity, Is.EqualTo(NpcActivity.AtStation));
        }

        [Test]
        public void AbstractNpcs_AreProcessedRoundRobinWithinTheBudget()
        {
            var npcs = Enumerable.Range(0, 10).Select(i => Smith("N" + i, sleep: "bed2")).ToArray();
            _clock = new GameClock(5.99f, 12);
            var sim = new NpcSimulation(npcs, new RoutinePointCatalog(Points, Anchors), new RoutineSchedule(G3RoutineTables.Schedule()),
                                        _clock, new SeededRandom(1), new NpcSimulationSettings { AbstractBudget = 3 });
            sim.PlaceAtRoutineStart();
            _clock.Advance(10); // 6:01, everybody's phase changed
            sim.Tick();
            Assert.That(sim.Agents.Count(a => a.Phase == DayPhase.Work), Is.EqualTo(3));
            sim.Tick();
            sim.Tick();
            sim.Tick();
            Assert.That(sim.Agents.All(a => a.Phase == DayPhase.Work), Is.True);
        }

        [Test]
        public void Save_RoundTripsToIdenticalBytes_AndRestoresClaims()
        {
            var sim = Create(19.99f, Smith("A"), Smith("B"));
            Advance(sim, 7);
            var first = Save(sim);

            var copy = Create(8, Smith("A"), Smith("B"));
            using (var input = new MemoryStream(first)) SaveFile.Read(input, new ISaveParticipant[] { new NpcSimulationSave(copy) });
            Assert.That(Save(copy), Is.EqualTo(first));
            var relaxing = copy.Agents.Single(a => !a.Station.IsNone);
            Assert.That(copy.Claims.TryGetUser(relaxing.Station, out var user) && user == relaxing.Id, Is.True, "the bench is claimed again");
            Assert.That(copy.Claims.TryGetUser(new Station(Id("anvil"), 0), out _), Is.False);
        }

        [Test]
        public void Save_SkipsNpcsInTheirSpawnState_AndPutsThemBackThereOnLoad()
        {
            // fix-157: B found no free anvil and stands at its spawn: it is not written
            var sim = Create(8, Smith("A"), Smith("B"));
            var first = Save(sim);
            Assert.That(first.Length, Is.EqualTo(Save(Create(8, Smith("A"))).Length), "only A is in the section");

            // the fresh game the save is read into gave the anvil to B
            var copy = Create(8, Smith("B"), Smith("A"));
            Assert.That(Agent(copy, "B").Station.IsNone, Is.False);
            using (var input = new MemoryStream(first)) SaveFile.Read(input, new ISaveParticipant[] { new NpcSimulationSave(copy) });
            var b = Agent(copy, "B");
            Assert.That(b.Activity, Is.EqualTo(NpcActivity.Idle));
            Assert.That(b.Station.IsNone, Is.True);
            Assert.That(b.Pose, Is.EqualTo(Pose(0, 0)), "back at its spawn");
            Assert.That(b.ArrivedAt, Is.EqualTo(0));
            Assert.That(Agent(copy, "A").Station, Is.EqualTo(new Station(Id("anvil"), 0)));
            Assert.That(copy.Claims.TryGetUser(new Station(Id("anvil"), 0), out var user) && user == new ActorId("A"), Is.True);
            Assert.That(Save(copy), Is.EqualTo(first));
        }

        [Test]
        public void Save_RoundTripsEveryField_OfTravellingTalkingAndRenamedRoutines()
        {
            var sim = Create(19.99f, Smith("A"), Smith("B", work: "roam1", relax: "roam1"), Smith("C", work: "bed2", relax: "bed2"));
            Advance(sim, 4); // A travels to the bench
            sim.SetTalking(new ActorId("B"), true);
            sim.SetRoutine(new ActorId("C"), "Night"); // no such points: C idles where it is
            Assert.That(Agent(sim, "A").Activity, Is.EqualTo(NpcActivity.Travelling));
            Assert.That(Agent(sim, "B").Activity, Is.EqualTo(NpcActivity.Talking));
            var first = Save(sim);

            var copy = Create(8, Smith("A"), Smith("B", work: "roam1", relax: "roam1"), Smith("C", work: "bed2", relax: "bed2"));
            using (var input = new MemoryStream(first)) SaveFile.Read(input, new ISaveParticipant[] { new NpcSimulationSave(copy) });
            foreach (var a in sim.Agents) AssertSameState(a, Agent(copy, a.Id.Name));
            Assert.That(Save(copy), Is.EqualTo(first));
        }

        [TestCase(1)]
        [TestCase(2)]
        public void OldSave_WithEveryNpcAsDoubles_LoadsIntoTheSameState_AndIsSavedAgainAsVersion3(int version)
        {
            var sim = Create(19.99f, Smith("A"), Smith("B"), Smith("C", work: "bed2", relax: "bed2"));
            Advance(sim, 4);
            sim.SetTalking(new ActorId("C"), true);
            if (version >= 2) sim.SetRoutine(new ActorId("C"), "Night");
            byte[] old;
            using (var output = new MemoryStream())
            {
                SaveFile.Write(output, new SaveHeader(new PackStamp[0], 0), new ISaveParticipant[] { new LegacyNpcSection(sim, version) });
                old = output.ToArray();
            }

            var copy = Create(8, Smith("A"), Smith("B"), Smith("C", work: "bed2", relax: "bed2"));
            using (var input = new MemoryStream(old)) SaveFile.Read(input, new ISaveParticipant[] { new NpcSimulationSave(copy) });
            foreach (var a in sim.Agents)
            {
                var c = Agent(copy, a.Id.Name);
                AssertSameState(a, c);
                Assert.That(c.Phase, Is.EqualTo(a.Phase), a.Id.Name);
            }
            Assert.That(Save(copy), Is.EqualTo(Save(sim)), "written as version 3 like the original");
            Assert.That(Save(copy).Length, Is.LessThan(old.Length));
        }

        /// <summary>Every saved field; the phase only where it means something (not idle without a station).</summary>
        static void AssertSameState(NpcAgent expected, NpcAgent actual)
        {
            string who = expected.Id.Name;
            Assert.That(actual.Pose, Is.EqualTo(expected.Pose), who);
            Assert.That(actual.Activity, Is.EqualTo(expected.Activity), who);
            Assert.That(actual.Station, Is.EqualTo(expected.Station), who);
            Assert.That(actual.TravelFrom, Is.EqualTo(expected.TravelFrom), who);
            Assert.That(actual.TravelStart, Is.EqualTo(expected.TravelStart), who);
            Assert.That(actual.TravelEnd, Is.EqualTo(expected.TravelEnd), who);
            Assert.That(actual.ArrivedAt, Is.EqualTo(expected.ArrivedAt), who);
            Assert.That(actual.RetryAt, Is.EqualTo(expected.RetryAt), who);
            Assert.That(actual.ResumeActivity, Is.EqualTo(expected.ResumeActivity), who);
            Assert.That(actual.RoutineName, Is.EqualTo(expected.RoutineName), who);
            if (expected.Activity != NpcActivity.Idle || !expected.Station.IsNone) Assert.That(actual.Phase, Is.EqualTo(expected.Phase), who);
        }

        /// <summary>The "npcs" section as versions 1 and 2 wrote it (every NPC, every field, poses as doubles).</summary>
        sealed class LegacyNpcSection : ISaveParticipant
        {
            readonly NpcSimulation _sim;

            public LegacyNpcSection(NpcSimulation sim, int version)
            {
                _sim = sim;
                Version = version;
            }

            public string Section => "npcs";
            public int Version { get; }

            public void Write(ISaveWriter w)
            {
                w.WriteInt32(_sim.Agents.Count);
                foreach (var a in _sim.Agents)
                {
                    w.WriteString(a.Id.Name);
                    WritePose(w, a.Pose);
                    w.WriteInt32((int)a.Activity);
                    w.WriteString(a.Station.Point.Name);
                    w.WriteInt32(a.Station.Slot);
                    w.WriteInt32((int)a.Phase);
                    WritePose(w, a.TravelFrom);
                    w.WriteDouble(a.TravelStart);
                    w.WriteDouble(a.TravelEnd);
                    w.WriteDouble(a.ArrivedAt);
                    w.WriteDouble(a.RetryAt);
                    w.WriteInt32((int)a.ResumeActivity);
                    if (Version >= 2) w.WriteString(a.RoutineName ?? "");
                }
            }

            static void WritePose(ISaveWriter w, WorldPose p)
            {
                w.WriteDouble(p.X);
                w.WriteDouble(p.Y);
                w.WriteDouble(p.Z);
                w.WriteDouble(p.Yaw);
            }

            public void Read(ISaveReader r, int version) => throw new System.NotSupportedException();
        }

        static byte[] Save(NpcSimulation sim)
        {
            using var output = new MemoryStream();
            SaveFile.Write(output, new SaveHeader(new PackStamp[0], 0), new ISaveParticipant[] { new NpcSimulationSave(sim) });
            return output.ToArray();
        }

        void Advance(NpcSimulation sim, float realSeconds)
        {
            // Small steps, like frames; several ticks per step so the round-robin reaches every NPC.
            for (float t = 0; t < realSeconds; t += 0.5f)
            {
                _clock.Advance(System.Math.Min(0.5f, realSeconds - t));
                sim.Tick();
            }
        }

        static NpcAgent Agent(NpcSimulation sim, string name) => sim.TryGet(new ActorId(name), out var a) ? a : null;

        static Id<RoutinePointDef> Id(string id) => new Id<RoutinePointDef>(id);

        static WorldPose Pose(float x, float z) => new WorldPose(x, 0, z, 0);

        static RoutinePointDef Point(string id, string interaction, params WorldPose[] slots) =>
            new RoutinePointDef(Id(id), id, interaction, slots);

        static RoutinePointDef Point(string id, string interaction, string anchor, params WorldPose[] slots) =>
            new RoutinePointDef(Id(id), id, interaction, slots, new Id<AnchorDef>(anchor));
    }
}
