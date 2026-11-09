using System.Collections.Generic;
using Myrtana.Core;
using Myrtana.Import.G3;
using Myrtana.Simulation;
using NUnit.Framework;

namespace Myrtana.Tests.Simulation
{
    /// <summary>
    /// fix-66 (Docs/interactive-objects.md §9): G3 keeps one user per interactive object (gCInteraction_PS.User), the hero
    /// as well as NPCs; the campfire is shared, one user per use pose. A down or dead user lets go (IsFreeDestination).
    /// </summary>
    public sealed class ObjectOccupancyTests
    {
        static readonly ActorId Hero = ActorId.Player;

        static readonly List<RoutinePointDef> Points = new List<RoutinePointDef>
        {
            Point("anvil", "Anvil", null, Pose(10, 0), Pose(12, 0)),
            Point("smithy_a", "Anvil", "smithy", Pose(40, 0), Pose(42, 0)),
            Point("smithy_b", "Anvil", "smithy", Pose(50, 0), Pose(52, 0)),
            Point("fire", "Campfire", null, Pose(0, 30), Pose(4, 30)),
            Point("bed", "Bed", null, Pose(-30, 0), Pose(-32, 0)),
            Point("bench", "Bench", null, Pose(0, 20)),
        };

        static readonly List<AnchorDef> Anchors = new List<AnchorDef> { new AnchorDef(new Id<AnchorDef>("smithy"), AnchorKind.Local) };

        GameClock _clock;

        NpcSimulation Create(float hour, params NpcDef[] npcs)
        {
            _clock = new GameClock(hour, timeScale: 12);
            var sim = new NpcSimulation(npcs, new RoutinePointCatalog(Points, Anchors), new RoutineSchedule(G3RoutineTables.Schedule()),
                                        _clock, new SeededRandom(7));
            sim.PlaceAtRoutineStart();
            return sim;
        }

        [Test]
        public void HeroIsRefused_AtAnAnvilAnNpcUses_EvenFromItsOtherSide()
        {
            var sim = Create(8, Npc("Smith", "anvil"));
            Assert.That(Agent(sim, "Smith").Station, Is.EqualTo(new Station(Id("anvil"), 0)));

            Assert.That(sim.Claims.IsOccupiedFor(Id("anvil"), Hero), Is.True);
            Assert.That(sim.Claims.TryOccupy(Id("anvil"), Hero, 12, 0, out var taken), Is.False, "the free side is no second place");
            Assert.That(taken.IsNone, Is.True);
            Assert.That(sim.Claims.TryGetUser(new Station(Id("anvil"), 1), out _), Is.False);
        }

        [Test]
        public void SecondNpc_DoesNotTakeTheOtherSideOfAnOccupiedAnvilOrBed()
        {
            var sim = Create(8, Npc("A", "anvil"), Npc("B", "anvil"));
            Assert.That(Agent(sim, "B").Activity, Is.EqualTo(NpcActivity.Idle));
            var sleepers = Create(2, Npc("C", "anvil", sleep: "bed"), Npc("D", "anvil", sleep: "bed"));
            Assert.That(Agent(sleepers, "C").Station, Is.EqualTo(new Station(Id("bed"), 0)));
            Assert.That(Agent(sleepers, "D").Station.IsNone, Is.True, "one sleeper per bed");
        }

        [Test]
        public void Campfire_IsSharedByTheHeroAndAnNpc()
        {
            var sim = Create(8, Npc("Cook", "fire"));
            Assert.That(Agent(sim, "Cook").Station, Is.EqualTo(new Station(Id("fire"), 0)));

            Assert.That(sim.Claims.IsOccupiedFor(Id("fire"), Hero), Is.False);
            Assert.That(sim.Claims.TryOccupy(Id("fire"), Hero, 0, 30, out var taken), Is.True);
            Assert.That(taken, Is.EqualTo(new Station(Id("fire"), 1)), "the free pose, though the cook's is nearer");
            Assert.That(sim.Claims.TryGetUser(taken, out var user) && user == Hero, Is.True);
        }

        [Test]
        public void Campfire_IsSharedByTwoNpcs_AndTheHeroStillJoinsThem()
        {
            var sim = Create(8, Npc("Cook1", "fire"), Npc("Cook2", "fire"));
            Assert.That(Agent(sim, "Cook1").Station, Is.EqualTo(new Station(Id("fire"), 0)));
            Assert.That(Agent(sim, "Cook2").Station, Is.EqualTo(new Station(Id("fire"), 1)));

            Assert.That(sim.Claims.TryOccupy(Id("fire"), Hero, 2, 28, out var taken), Is.True);
            Assert.That(taken.IsNone, Is.True, "every pose is taken: he fries beside them without one");
        }

        [Test]
        public void NpcWaits_WhileTheHeroUsesItsAnvil_AndTakesItWhenHeLeaves()
        {
            var sim = Create(5.99f, Npc("Smith", "anvil", sleep: "bed"));
            var smith = Agent(sim, "Smith");
            Assert.That(sim.Claims.TryOccupy(Id("anvil"), Hero, 13, 0, out var held), Is.True);
            Assert.That(held, Is.EqualTo(new Station(Id("anvil"), 1)), "the slot nearest the hero");

            Advance(sim, 10); // 6:00 → work: the anvil is the hero's
            Assert.That(smith.Phase, Is.EqualTo(DayPhase.Work));
            Assert.That(smith.Activity, Is.EqualTo(NpcActivity.Idle), "waits");
            Assert.That(smith.Station.IsNone, Is.True);
            Advance(sim, 10);
            Assert.That(smith.Station.IsNone, Is.True, "still waiting while the hero works");

            sim.Claims.Release(held, Hero); // the hero's use ends
            Advance(sim, 3);
            Assert.That(smith.Station.Point, Is.EqualTo(Id("anvil")), "goes to his anvil");
            Assert.That(sim.Claims.IsOccupiedFor(Id("anvil"), Hero), Is.True, "and the hero cannot use it now");
        }

        [Test]
        public void NpcPicksAnotherAnvilOfItsSmithy_WhileTheHeroUsesItsOwn()
        {
            var sim = Create(5.99f, Npc("Smith", "smithy_a", sleep: "bed"));
            Assert.That(sim.Claims.TryOccupy(Id("smithy_a"), Hero, 40, 0, out _), Is.True);
            Advance(sim, 10);
            var smith = Agent(sim, "Smith");
            Assert.That(smith.Station.Point, Is.EqualTo(Id("smithy_b")));
            Assert.That(sim.Claims.IsOccupiedFor(Id("smithy_b"), Hero), Is.True);
        }

        [Test]
        public void InterruptedUse_ReleasesTheSlot()
        {
            var sim = Create(8);
            var smith = new ActorId("Smith");
            Assert.That(sim.Claims.TryOccupy(Id("anvil"), Hero, 10, 0, out var held), Is.True);
            Assert.That(sim.Claims.IsOccupiedFor(Id("anvil"), smith), Is.True);
            sim.Claims.Release(held, Hero); // HeroObjectUse.Finish: done, hit, fight, forced end
            Assert.That(sim.Claims.IsOccupiedFor(Id("anvil"), smith), Is.False);
            Assert.That(sim.Claims.IsFreeFor(new Station(Id("anvil"), 0), smith, Station.None), Is.True);
        }

        [Test]
        public void DownOrDeadUser_NoLongerHoldsItsObject()
        {
            var sim = Create(8, Npc("Smith", "anvil"));
            bool down = false;
            sim.Claims.UserGone = a => down && a == new ActorId("Smith");
            Assert.That(sim.Claims.IsOccupiedFor(Id("anvil"), Hero), Is.True);

            down = true; // knocked out or killed at the anvil (G3 IsFreeDestination: AIMode Down / Dead)
            Assert.That(sim.Claims.IsOccupiedFor(Id("anvil"), Hero), Is.False);
            Assert.That(sim.Claims.TryOccupy(Id("anvil"), Hero, 10, 0, out var held), Is.True);
            Assert.That(held, Is.EqualTo(new Station(Id("anvil"), 0)));
            Assert.That(sim.Claims.TryGetUser(held, out var user) && user == Hero, Is.True, "the slot is the hero's now");
        }

        /// <summary>
        /// fix-67 R33-3: the Faring rebel smith (sector Faring_NPC_02, off until the liberation) shares Zakosh's anvil point.
        /// An NPC of a switched-off sector is not in the world: it takes no station at the routine start, so the real smith
        /// gets the anvil whatever the order of the NPCs.
        /// </summary>
        [Test]
        public void NpcOfASwitchedOffSector_TakesNoStation_TheRealSmithGetsTheAnvil()
        {
            _clock = new GameClock(12, timeScale: 12);
            var sim = new NpcSimulation(new[] { Npc("RebSmith", "anvil", sector: "Faring_NPC_02"), Npc("Zakosh", "anvil", sector: "Faring_NPC_01") },
                                        new RoutinePointCatalog(Points, Anchors), new RoutineSchedule(G3RoutineTables.Schedule()), _clock,
                                        new SeededRandom(7)) { SectorEnabled = s => s != "Faring_NPC_02" };
            sim.PlaceAtRoutineStart();

            Assert.That(Agent(sim, "Zakosh").Station, Is.EqualTo(new Station(Id("anvil"), 0)));
            Assert.That(Agent(sim, "Zakosh").Activity, Is.EqualTo(NpcActivity.AtStation));
            Assert.That(Agent(sim, "RebSmith").Station.IsNone, Is.True);
            Assert.That(sim.Claims.TryGetUser(new Station(Id("anvil"), 0), out var user) && user == new ActorId("Zakosh"), Is.True);
        }

        /// <summary>
        /// fix-67 R33-3: a claim made while the NPC was in the world (or restored from a save) holds nothing once its sector
        /// is switched off (no body uses the object): the hero and other NPCs may take it.
        /// </summary>
        [Test]
        public void ClaimOfAnNpcWhoseSectorIsOff_HoldsNothing()
        {
            bool on = true;
            _clock = new GameClock(8, timeScale: 12);
            var sim = new NpcSimulation(new[] { Npc("Smith", "anvil", sector: "town") }, new RoutinePointCatalog(Points, Anchors),
                                        new RoutineSchedule(G3RoutineTables.Schedule()), _clock, new SeededRandom(7))
                      { SectorEnabled = s => on };
            sim.PlaceAtRoutineStart();
            Assert.That(sim.Claims.IsOccupiedFor(Id("anvil"), Hero), Is.True);

            on = false;
            Assert.That(sim.Claims.IsOccupiedFor(Id("anvil"), Hero), Is.False);
            Assert.That(sim.Claims.IsFreeFor(new Station(Id("anvil"), 1), new ActorId("Other"), Station.None), Is.True);
            Assert.That(sim.Claims.TryOccupy(Id("anvil"), Hero, 10, 0, out var held), Is.True);
            Assert.That(held, Is.EqualTo(new Station(Id("anvil"), 0)));
        }

        /// <summary>fix-67: a gone user (here: not in the world) does not count against its anchor's MaxUsers.</summary>
        [Test]
        public void AbsentUser_DoesNotFillItsAnchor()
        {
            var points = new List<RoutinePointDef> { Point("seat_a", "Bench", "yard", Pose(0, 0)), Point("seat_b", "Bench", "yard", Pose(5, 0)) };
            var anchors = new List<AnchorDef> { new AnchorDef(new Id<AnchorDef>("yard"), AnchorKind.Local, maxUsers: 1) };
            var claims = new StationClaims(new RoutinePointCatalog(points, anchors));
            var ghost = new ActorId("Ghost");
            claims.Claim(new Station(Id("seat_a"), 0), ghost);
            Assert.That(claims.IsFreeFor(new Station(Id("seat_b"), 0), new ActorId("Real"), Station.None), Is.False, "the anchor is full");

            claims.UserAbsent = a => a == ghost;
            Assert.That(claims.IsFreeFor(new Station(Id("seat_b"), 0), new ActorId("Real"), Station.None), Is.True);
        }

        [Test]
        public void ObjectWithoutRoutineData_IsNeverOccupied()
        {
            var sim = Create(8);
            Assert.That(sim.Claims.IsOccupiedFor(Id("chest"), Hero), Is.False);
            Assert.That(sim.Claims.TryOccupy(Id("chest"), Hero, 0, 0, out var taken), Is.True);
            Assert.That(taken.IsNone, Is.True);
        }

        void Advance(NpcSimulation sim, float realSeconds)
        {
            for (float t = 0; t < realSeconds; t += 0.5f)
            {
                _clock.Advance(System.Math.Min(0.5f, realSeconds - t));
                sim.Tick();
            }
        }

        static NpcDef Npc(string name, string work, string sleep = null, string sector = null) =>
            new NpcDef(new ActorId(name), default, null, "Human", "visual", Pose(0, 0),
                       new NpcRoutineDef(Id(work), default, sleep != null ? Id(sleep) : default), sector: sector);

        static NpcAgent Agent(NpcSimulation sim, string name) => sim.TryGet(new ActorId(name), out var a) ? a : null;

        static Id<RoutinePointDef> Id(string id) => new Id<RoutinePointDef>(id);

        static WorldPose Pose(float x, float z) => new WorldPose(x, 0, z, 0);

        static RoutinePointDef Point(string id, string interaction, string anchor, params WorldPose[] slots) =>
            new RoutinePointDef(Id(id), id, interaction, slots, anchor != null ? new Id<AnchorDef>(anchor) : default);
    }
}
