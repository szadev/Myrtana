using System.Collections.Generic;
using Myrtana.Core;
using Myrtana.Import.G3;
using Myrtana.Rules;
using Myrtana.Simulation;
using NUnit.Framework;

namespace Myrtana.Tests.Simulation
{
    /// <summary>npc-world-2: ZS_Ambient wandering, travel gait, smalltalk pairs, monsters vs companions, ransacking once.</summary>
    public sealed class NpcWorldTests
    {
        static readonly List<RoutinePointDef> Points = new List<RoutinePointDef>
        {
            Point("talkA", "Smalltalk", Pose(0, 0)),
            Point("talkB", "Smalltalk", Pose(2, 0)),
            Point("talkFar", "Smalltalk", Pose(50, 0)),
            Point("den", "SleepGround", Pose(200, 0)),
            Point("fire", "SitCampfire", Pose(300, 0)),
            Point("bed", "Bed", Pose(400, 0)),
        };

        GameClock _clock;

        NpcSimulation Create(float hour, params NpcDef[] npcs)
        {
            _clock = new GameClock(hour, timeScale: 1);
            var sim = new NpcSimulation(npcs, new RoutinePointCatalog(Points, new List<AnchorDef>()),
                                        new RoutineSchedule(G3RoutineTables.Schedule()), _clock, new SeededRandom(3));
            sim.PlaceAtRoutineStart();
            return sim;
        }

        static NpcDef Creature(string name, string species, NpcRoutineDef routine = null) =>
            new NpcDef(new ActorId(name), default, null, species, "visual", Pose(0, 0), routine);

        static NpcDef Talker(string name, string point) =>
            new NpcDef(new ActorId(name), default, null, "Human", "visual", Pose(0, 0),
                       new NpcRoutineDef(Id(point), Id(point), Id(point)));

        // ------------------------------------------------------------------------------------------- ZS_Ambient

        [Test]
        public void Rules_RunnersGrazersAndHumanoids()
        {
            Assert.That(AmbientRules.RunsWhenTravelling("Wolf"), Is.True);
            Assert.That(AmbientRules.RunsWhenTravelling("Snapper"), Is.True);
            Assert.That(AmbientRules.RunsWhenTravelling("Human"), Is.False);
            Assert.That(AmbientRules.RunsWhenTravelling("Boar"), Is.False);
            Assert.That(AmbientRules.Wanders(Creature("w", "Wolf"), null), Is.True);
            Assert.That(AmbientRules.Wanders(Creature("h", "Human"), null), Is.False, "humans without points stand");
            Assert.That(AmbientRules.Wanders(Creature("o", "Orc"), null), Is.False);
            Assert.That(AmbientRules.StaysPut("Pig") && AmbientRules.StaysPut("Cow") && !AmbientRules.StaysPut("Wolf"), Is.True);

            var random = new SeededRandom(1);
            int eat = 0;
            for (int i = 0; i < 1000; i++) if (AmbientRules.ActionOf("Scavenger", random) == AmbientRules.EatGround) eat++;
            Assert.That(eat, Is.InRange(740, 860), "Scavengers graze 80 %");
            for (int i = 0; i < 100; i++) Assert.That(AmbientRules.ActionOf("Wolf", random), Is.EqualTo(AmbientRules.Ambient));
            for (int i = 0; i < 100; i++)
            {
                Assert.That(AmbientRules.WanderRadius(true, random), Is.InRange(2f, 4f));
                Assert.That(AmbientRules.WanderRadius(false, random), Is.InRange(3f, 5f));
            }
        }

        [Test]
        public void Wanderer_WalksAroundItsStart_ThenPlaysAnAction_OnlyWithAView()
        {
            var sim = Create(12, Creature("Wolf1", "Wolf"));
            var wolf = Agent(sim, "Wolf1");
            Advance(sim, 30);
            Assert.That(wolf.Activity, Is.EqualTo(NpcActivity.Idle), "off screen a wanderer stays put");
            Assert.That(wolf.WanderTarget, Is.Null);

            sim.SetDetail(wolf.Id, DetailLevel.Near);
            sim.Tick();
            Assert.That(wolf.Activity, Is.EqualTo(NpcActivity.Travelling));
            Assert.That(sim.TryGetTarget(wolf, out var target), Is.True);
            float r = target.HorizontalDistanceTo(Pose(0, 0));
            Assert.That(r, Is.InRange(2.99f, 5.01f));
            Assert.That(sim.Runs(wolf), Is.False, "wander steps are walked");

            Advance(sim, 10);
            Assert.That(wolf.Activity, Is.EqualTo(NpcActivity.Idle));
            Assert.That(sim.InteractionOf(wolf), Is.EqualTo(AmbientRules.Ambient));
            Assert.That(wolf.Pose.HorizontalDistanceTo(target), Is.LessThan(0.01f));

            for (int i = 0; i < 40 && wolf.Activity != NpcActivity.Travelling; i++) Advance(sim, 0.5f);
            Assert.That(wolf.Activity, Is.EqualTo(NpcActivity.Travelling), "and walks on");
            Assert.That(sim.TryGetTarget(wolf, out var next) && next.HorizontalDistanceTo(Pose(0, 0)) <= 5.01f, Is.True,
                        "always around the start position");
        }

        [Test]
        public void OnlyABedOrTheGroundIsSleep_NotTheSleepPhaseCampfire()
        {
            // fix-7 (Kent at his campfire): G3 SelectRoutineTask picks ZS_Sleep by the point's use type, not the day phase.
            var sim = Create(2, Talker("Kent", "fire"), Talker("Sleeper", "bed"));
            var kent = Agent(sim, "Kent");
            var sleeper = Agent(sim, "Sleeper");
            Assert.That((kent.Activity, sleeper.Activity), Is.EqualTo((NpcActivity.AtStation, NpcActivity.AtStation)));
            Assert.That(kent.Phase, Is.EqualTo(DayPhase.Sleep));
            Assert.That(sim.Sleeps(kent), Is.False, "sitting at the campfire at night is awake");
            Assert.That(sim.Sleeps(sleeper), Is.True);
            Assert.That(NpcSimulation.IsSleepInteraction(AmbientRules.SleepGround), Is.True);
        }

        [Test]
        public void Wanderer_SleepsInTheSleepPhase_PigsDoNotWalk()
        {
            var sim = Create(4.75f, Creature("Scav", "Scavenger"));
            var scav = Agent(sim, "Scav");
            sim.SetDetail(scav.Id, DetailLevel.Near);
            sim.Tick();
            Assert.That(sim.InteractionOf(scav), Is.EqualTo(AmbientRules.SleepGround), "Scavengers sleep until 5:00");
            Advance(sim, 15 * 60 + 61); // the sleep check (every 60 s) notices 5:00
            Assert.That(scav.Activity == NpcActivity.Travelling || sim.InteractionOf(scav) != AmbientRules.SleepGround, Is.True);

            sim = Create(12, Creature("Pig1", "Pig"));
            var pig = Agent(sim, "Pig1");
            sim.SetDetail(pig.Id, DetailLevel.Near);
            for (int i = 0; i < 20; i++)
            {
                Advance(sim, 3);
                Assert.That(pig.Activity, Is.EqualTo(NpcActivity.Idle));
            }
            Assert.That(new[] { AmbientRules.EatGround, AmbientRules.Ambient }, Does.Contain(sim.InteractionOf(pig)));
        }

        [Test]
        public void Runners_RunToTheirRoutinePoint_Faster()
        {
            var routine = new NpcRoutineDef(Id("den"), Id("den"), Id("den"));
            var sim = Create(12, Creature("Wolf1", "Wolf", routine), Creature("Boar1", "Boar", routine));
            var wolf = Agent(sim, "Wolf1");
            Assert.That(sim.Runs(wolf), Is.True);
            Assert.That(sim.Runs(Agent(sim, "Boar1")), Is.False);
            Assert.That(sim.Settings.RunTravelSpeed, Is.GreaterThan(sim.Settings.TravelSpeed));
        }

        // ------------------------------------------------------------------------------------------- ZS_Smalltalk

        [Test]
        public void Smalltalk_TurnsAlternate()
        {
            Assert.That(Smalltalk.RoleOf(true, 0), Is.EqualTo(Smalltalk.Interaction));
            Assert.That(Smalltalk.RoleOf(false, 0), Is.EqualTo(Smalltalk.Listener));
            Assert.That(Smalltalk.RoleOf(true, Smalltalk.TurnSeconds + 0.1), Is.EqualTo(Smalltalk.Listener));
            Assert.That(Smalltalk.RoleOf(false, Smalltalk.TurnSeconds + 0.1), Is.EqualTo(Smalltalk.Interaction));
            Assert.That(Smalltalk.IsLeader(1, "B", 2, "A"), Is.True, "first there leads");
            Assert.That(Smalltalk.IsLeader(1, "A", 1, "B"), Is.True);
            Assert.That(Smalltalk.IsLeader(1, "B", 1, "A"), Is.False);
        }

        [Test]
        public void Smalltalk_NeighboursPair_OneSpeaksOneListens_AloneStands()
        {
            var sim = Create(12, Talker("A", "talkA"), Talker("B", "talkB"), Talker("C", "talkFar"));
            var a = Agent(sim, "A");
            var b = Agent(sim, "B");
            var c = Agent(sim, "C");
            foreach (var x in new[] { a, b, c }) sim.SetDetail(x.Id, DetailLevel.Near);
            Assert.That(sim.SmalltalkPartnerOf(a), Is.SameAs(b));
            Assert.That(sim.SmalltalkPartnerOf(b), Is.SameAs(a));
            Assert.That(sim.SmalltalkPartnerOf(c), Is.Null);
            var roles = new[] { sim.InteractionOf(a), sim.InteractionOf(b) };
            Assert.That(roles, Is.EquivalentTo(new[] { Smalltalk.Interaction, Smalltalk.Listener }));
            Assert.That(sim.InteractionOf(c), Is.EqualTo(Smalltalk.Alone));
            _clock.Advance(Smalltalk.TurnSeconds);
            Assert.That(new[] { sim.InteractionOf(a), sim.InteractionOf(b) }, Is.EqualTo(new[] { roles[1], roles[0] }), "turns swap");
        }

        // ------------------------------------------------------------------------------------------- combat edges

        [Test]
        public void DownedVictim_IsRobbedOnce_UntilItGetsUp()
        {
            var claims = new RansackClaims();
            var hero = ActorId.Player;
            var a = new ActorId("Curtis");
            var b = new ActorId("Goran");
            Assert.That(claims.TryClaim(hero, a), Is.True);
            Assert.That(claims.TryClaim(hero, a), Is.True, "the looter keeps its claim while it works");
            Assert.That(claims.TryClaim(hero, b), Is.False, "a second winner leaves the body alone");
            Assert.That(claims.IsRansacked(hero), Is.True);
            claims.Clear(hero);
            Assert.That(claims.TryClaim(hero, b), Is.True, "after getting up he can be robbed again");
        }

        // ------------------------------------------------------------------------------------------- fix-133

        [Test]
        public void CorrectPose_SticksForANearBody_WhichReportPoseIgnores()
        {
            // fix-133: a Near goblin rescued out of the rock every 2 s went back there: ReportPose ignores Near NPCs
            var sim = Create(12, Creature("Gob", "Goblin"));
            var gob = Agent(sim, "Gob");
            sim.SetDetail(gob.Id, DetailLevel.Near);
            Advance(sim, 30); // wandered to a point and idles there, or walks on
            for (int i = 0; i < 40 && gob.Activity != NpcActivity.Idle; i++) Advance(sim, 0.5f);
            Assert.That(gob.Activity, Is.EqualTo(NpcActivity.Idle));
            var floor = new WorldPose(9, 1, 9, 0);
            sim.ReportPose(gob.Id, floor);
            Assert.That(sim.PoseOf(gob), Is.Not.EqualTo(floor), "ReportPose is for Active bodies only");
            Assert.That(sim.CorrectPose(gob.Id, floor), Is.True);
            Assert.That(sim.PoseOf(gob), Is.EqualTo(floor));
            Assert.That(sim.CorrectPose(new ActorId("nobody"), floor), Is.False);
        }

        [Test]
        public void CorrectPose_DuringANearWander_EndsTheWanderThere()
        {
            var sim = Create(12, Creature("Gob", "Goblin"));
            var gob = Agent(sim, "Gob");
            sim.SetDetail(gob.Id, DetailLevel.Near);
            sim.Tick();
            Assert.That(gob.Activity, Is.EqualTo(NpcActivity.Travelling));
            _clock.Advance(0.5f);
            var floor = new WorldPose(1, 2, 1, 0);
            sim.CorrectPose(gob.Id, floor);
            Assert.That(sim.PoseOf(gob), Is.EqualTo(floor), "the interpolated pose no longer runs on into the rock");
            for (int i = 0; i < 40 && gob.Activity == NpcActivity.Travelling; i++) Advance(sim, 0.5f);
            Assert.That(gob.Activity, Is.EqualTo(NpcActivity.Idle));
            Assert.That(gob.Pose, Is.EqualTo(floor), "the wander ended where the body was put");
        }

        [Test]
        public void CorrectWanderTarget_RedirectsTheNearTravel_FromWhereTheNpcIs()
        {
            var sim = Create(12, Creature("Gob", "Goblin"));
            var gob = Agent(sim, "Gob");
            sim.SetDetail(gob.Id, DetailLevel.Near);
            sim.Tick();
            Assert.That(sim.TryGetTarget(gob, out var rock), Is.True);
            _clock.Advance(0.5f);
            var here = sim.PoseOf(gob);
            var floor = new WorldPose(rock.X + 1, rock.Y + 0.8f, rock.Z + 1, rock.Yaw);
            Assert.That(sim.CorrectWanderTarget(gob.Id, floor), Is.True);
            Assert.That(sim.TryGetTarget(gob, out var now) && now.Equals(floor), Is.True);
            Assert.That(sim.PoseOf(gob).HorizontalDistanceTo(here), Is.LessThan(0.01f), "no jump: the travel starts where it was");
            for (int i = 0; i < 40 && gob.Activity == NpcActivity.Travelling; i++) Advance(sim, 0.5f);
            Assert.That(gob.Activity, Is.EqualTo(NpcActivity.Idle));
            Assert.That(gob.Pose, Is.EqualTo(floor), "arrives on the corrected point");

            var idle = Create(12, Creature("Pig1", "Pig"));
            var pig = Agent(idle, "Pig1");
            Assert.That(idle.CorrectWanderTarget(pig.Id, floor), Is.False, "no wander travel, nothing to redirect");
        }

        // ------------------------------------------------------------------------------------------- helpers

        void Advance(NpcSimulation sim, float realSeconds)
        {
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
    }
}
