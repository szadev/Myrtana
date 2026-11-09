using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;
using Myrtana.Import.G3;
using Myrtana.Rules;
using Myrtana.Simulation;
using NUnit.Framework;

namespace Myrtana.Tests.Simulation
{
    /// <summary>par-npc2: fight watching, KO lines, smalltalk, sermons, work cycles, NPC-led parties (parity-audit #25–#36).</summary>
    public sealed class NpcTaskTests
    {
        // ------------------------------------------------------------------------------------------ watch fight

        [Test]
        public void FightWatcher_KeepsFiveAndAHalfToSixAndAHalfMetres_FollowsThenWaits_AndGivesUpBeyondTwenty()
        {
            var w = new FightWatcher(new ActorId("A"), new ActorId("B"));
            Assert.That(w.Update(0.2f, 6f, 9f, false, false), Is.EqualTo(WatchStep.Stand));
            Assert.That(w.Focus, Is.EqualTo(new ActorId("A")), "the nearer fighter");
            Assert.That(w.Update(0.2f, 4f, 9f, false, false), Is.EqualTo(WatchStep.StepBack));
            Assert.That(w.Update(0.2f, 5.9f, 9f, false, false), Is.EqualTo(WatchStep.StepBack), "back until 6 m");
            Assert.That(w.Update(0.2f, 6.1f, 9f, false, false), Is.EqualTo(WatchStep.Stand));
            Assert.That(w.Update(0.2f, 9f, 8f, false, false), Is.EqualTo(WatchStep.Approach));
            Assert.That(w.Focus, Is.EqualTo(new ActorId("B")));
            for (int i = 0; i < 30; i++) w.Update(0.2f, 15f, 12f, false, false); // the fight runs off: 5 s follow time
            Assert.That(w.Step, Is.EqualTo(WatchStep.WaitForFlee));
            w.Update(1.1f, 14f, 11f, false, false);
            Assert.That(w.Step, Is.EqualTo(WatchStep.Stand), "a fighter came closer again: back to the loop");
            for (int i = 0; i < 30; i++) w.Update(0.2f, 15f, 12f, false, false);
            w.Update(1.1f, 25f, 21f, false, false);
            Assert.That(w.Step, Is.EqualTo(WatchStep.Done), "beyond 20 m the watch ends");
        }

        // fix-85: an arena fight is watched from a spectator point or from where the watcher stands (ZS_WatchFight_Spectator).
        [Test]
        public void FightWatcher_ArenaSpectator_NeverClosesInNorBacksOff_FacesTheNearer_Applauds()
        {
            var w = new FightWatcher(new ActorId("A"), new ActorId("B"), spectator: true);
            Assert.That(w.Update(0.2f, 25f, 30f, false, false), Is.EqualTo(WatchStep.Stand), "far: no Goto (ZS_WatchFight_Loop would approach)");
            Assert.That(w.Focus, Is.EqualTo(new ActorId("A")));
            Assert.That(w.Update(0.2f, 2f, 1f, false, false), Is.EqualTo(WatchStep.Stand), "close: no GoBack");
            Assert.That(w.Focus, Is.EqualTo(new ActorId("B")), "turns to the nearer fighter");
            for (int i = 0; i < 40; i++) w.Update(0.2f, 40f, 45f, false, false);
            Assert.That(w.Step, Is.EqualTo(WatchStep.Stand), "no WaitForFlee for a spectator");
            Assert.That(w.Update(0.2f, 3f, 4f, false, true), Is.EqualTo(WatchStep.Applaud));
            Assert.That(w.Focus, Is.EqualTo(new ActorId("A")), "the winner");
        }

        [Test]
        public void FightWatcher_ArenaSpectator_WithAPoint_GoesThereFirst()
        {
            var w = new FightWatcher(new ActorId("A"), new ActorId("B"), spectator: true, toSpot: true);
            Assert.That(w.Update(0.2f, 6f, 7f, false, false), Is.EqualTo(WatchStep.GoToSpot));
            w.AtSpot();
            Assert.That(w.Update(0.2f, 6f, 7f, false, false), Is.EqualTo(WatchStep.Stand));
        }

        [TestCase(false, AttackReason.Arena, false, null, true)]
        [TestCase(false, null, false, AttackReason.Arena, true)]
        [TestCase(true, null, false, null, true)] // the opponent waits in the ring (AIMode Arena)
        [TestCase(false, AttackReason.Duel, false, AttackReason.Duel, false)]
        [TestCase(false, AttackReason.Enemy, false, null, false)]
        public void SpectatorRules_ArenaFight(bool aIn, AttackReason? a, bool bIn, AttackReason? b, bool arena) =>
            Assert.That(SpectatorRules.IsArenaFight(aIn, a, bIn, b), Is.EqualTo(arena));

        [Test]
        public void SpectatorRules_FindPoint_TakesAFreePointOfTheOwnAnchor_OrOneNearby()
        {
            var anchor = new Id<AnchorDef>("Ring");
            RoutinePointDef P(string id, string use, float x, Id<AnchorDef> a = default) =>
                new RoutinePointDef(new Id<RoutinePointDef>(id), id, use, new[] { new WorldPose(x, 0, 0, 0) }, a);
            var catalog = new RoutinePointCatalog(new[]
            {
                P("bench", "Bench", 0, anchor), P("spec_ring", "Spectator", 12, anchor), P("spec_far", "Spectator", 3),
                P("spec_near", "Spectator", 8),
            }, new[] { new AnchorDef(anchor, AnchorKind.Local) });
            var claims = new StationClaims(catalog);
            var npc = new ActorId("Watcher");
            var here = new WorldPose(1, 0, 0, 0);
            var onBench = new Station(new Id<RoutinePointDef>("bench"), 0);
            Assert.That(SpectatorRules.FindPoint(catalog, claims, npc, onBench, here).Point.Name, Is.EqualTo("spec_ring"),
                        "the anchor's own point, though another is nearer");
            Assert.That(SpectatorRules.FindPoint(catalog, claims, npc, Station.None, here).Point.Name, Is.EqualTo("spec_far"),
                        "no anchor: the nearest within 20 m");
            Assert.That(SpectatorRules.FindPoint(catalog, claims, npc, Station.None, new WorldPose(100, 0, 0, 0)).IsNone, Is.True,
                        "none nearby: watch from here");
        }

        [Test]
        public void FightWatcher_ApplaudsTheWinner_WhenTheOtherGoesDown()
        {
            var w = new FightWatcher(new ActorId("A"), new ActorId("B"));
            w.Update(0.2f, 6f, 6f, false, false);
            Assert.That(w.Update(0.2f, 6f, 6f, aDown: true, bDown: false), Is.EqualTo(WatchStep.Applaud));
            Assert.That(w.Focus, Is.EqualTo(new ActorId("B")));
            w.Update(FightWatcher.ApplaudSeconds + 0.1f, 6f, 6f, true, false);
            Assert.That(w.Step, Is.EqualTo(WatchStep.Done));
        }

        [Test]
        public void FightWatcher_EndsAQuietFight_UnlessAHitRenewsIt()
        {
            var w = new FightWatcher(new ActorId("A"), new ActorId("B"));
            for (int i = 0; i < 20; i++) w.Update(1f, 6f, 7f, false, false);
            w.Renew();
            for (int i = 0; i < 20; i++) w.Update(1f, 6f, 7f, false, false);
            Assert.That(w.Step, Is.Not.EqualTo(WatchStep.Done));
            for (int i = 0; i < 12; i++) w.Update(1f, 6f, 7f, false, false);
            Assert.That(w.Step, Is.EqualTo(WatchStep.Done));
        }

        [Test]
        public void CheerFight_HeavyHitsAtCounterZeroAndOne_OohForAFriendOfTheVictim_ElseCheer()
        {
            var c = new FightCheers();
            Assert.That(c.LineFor(HitReaction.SitKnockDown, Attitude.Neutral, Attitude.Neutral), Is.EqualTo("HeavyHit")); // counter 1
            Assert.That(c.LineFor(HitReaction.Stumble, Attitude.Friendly, Attitude.Neutral), Is.EqualTo("Ooh_02"));    // counter 2
            Assert.That(c.LineFor(HitReaction.PierceStumble, Attitude.Neutral, Attitude.Neutral), Is.EqualTo("HeavyHit")); // 0
            Assert.That(c.LineFor(HitReaction.ParadeStumble, Attitude.Friendly, Attitude.Friendly), Is.EqualTo("Cheer_01"));
            Assert.That(c.LineFor(HitReaction.SitKnockDown, Attitude.Neutral, Attitude.Neutral), Is.Null, "a heavy hit at counter 2 is silent");
            Assert.That(c.LineFor(HitReaction.Unconscious, Attitude.Neutral, Attitude.Neutral), Is.Null);
            Assert.That(c.LineFor(HitReaction.Flinch, Attitude.Hostile, Attitude.Friendly), Is.EqualTo("Cheer_01"));
        }

        [TestCase(49, Attitude.Hostile, "OhMyHead")]
        [TestCase(50, Attitude.Hostile, "NextTimeYoureInForIt")]
        [TestCase(99, Attitude.Neutral, "NextTimeYoureInForIt")]
        [TestCase(99, Attitude.Friendly, "OhMyHead")]
        public void KnockOutGetUpLine(int roll, Attitude toAttacker, string line) =>
            Assert.That(KnockOutLines.GetUpLine(roll, toAttacker), Is.EqualTo(line));

        [Test]
        public void BattleTalk_ListsTheWatchersOfAFighter()
        {
            double now = 0;
            var talk = new BattleTalk(() => now, () => 0);
            talk.Watch(new ActorId("W1"), new ActorId("A"), new ActorId("B"));
            talk.Watch(new ActorId("W2"), new ActorId("C"), new ActorId("B"));
            var list = new List<ActorId>();
            talk.WatchersOf(new ActorId("B"), list);
            Assert.That(list.Select(i => i.Name), Is.EqualTo(new[] { "W1", "W2" }));
            talk.WatchersOf(new ActorId("A"), list);
            Assert.That(list.Select(i => i.Name), Is.EqualTo(new[] { "W1" }));
            now = BattleTalk.WatchMemorySeconds + 1;
            talk.WatchersOf(new ActorId("B"), list);
            Assert.That(list, Is.Empty);
        }

        // ------------------------------------------------------------------------------------------ simulation

        GameClock _clock;
        readonly List<(string speaker, string line)> _said = new List<(string, string)>();

        NpcSimulation Create(List<RoutinePointDef> points, List<AnchorDef> anchors, float hour, params NpcDef[] npcs)
        {
            _clock = new GameClock(hour, timeScale: 12);
            _said.Clear();
            var sim = new NpcSimulation(npcs, new RoutinePointCatalog(points, anchors ?? new List<AnchorDef>()),
                                        new RoutineSchedule(G3RoutineTables.Schedule()), _clock, new SeededRandom(5));
            sim.Said += (s, l) => _said.Add((s.Name, l));
            sim.PlaceAtRoutineStart();
            foreach (var n in npcs) sim.SetDetail(n.Id, DetailLevel.Near);
            return sim;
        }

        void Advance(NpcSimulation sim, float realSeconds)
        {
            for (float t = 0; t < realSeconds; t += 0.5f)
            {
                _clock.Advance(System.Math.Min(0.5f, realSeconds - t));
                sim.Tick();
            }
        }

        [Test]
        public void Smalltalk_TheLeaderAndThePartnerSayTheirLinesInTurn_AndTheTalkEndsAfter120Seconds()
        {
            var points = new List<RoutinePointDef> { Point("st1", "Smalltalk", Pose(0, 0)), Point("st2", "Smalltalk", Pose(2, 0)) };
            var sim = Create(points, null, 8, Npc("A", "st1"), Npc("B", "st2"));
            Advance(sim, 0.5f);
            Assert.That(_said.First(), Is.EqualTo(("A", "Smalltalk_00")), "A leads (same arrival: the smaller id)");
            Advance(sim, Smalltalk.TurnSeconds);
            Assert.That(_said.Last(), Is.EqualTo(("B", "Smalltalk_01")));
            Advance(sim, Smalltalk.EndSeconds - Smalltalk.TurnSeconds - 1);
            int before = _said.Count;
            Assert.That(_said.Count(s => s.line == "Smalltalk_00"), Is.EqualTo(2), "turn 10 wraps to line 00");
            Advance(sim, 2);
            Assert.That(_said.Skip(before).Last(), Is.EqualTo(("A", "Smalltalk_00")), "after 120 s a new conversation starts");
        }

        [Test]
        public void Preacher_SaysTenSpeechLines_SendsNearbyNpcsToListenerPoints_AndReleasesThemAtTheEnd()
        {
            var points = new List<RoutinePointDef>
            {
                Point("pulpit", "Preach", Pose(0, 0)),
                Point("l1", "Listener", Pose(3, 0)), Point("l2", "Listener", Pose(4, 0)), Point("lfar", "Listener", Pose(40, 0)),
                Point("bench1", "Bench", Pose(10, 0)), Point("bench2", "Bench", Pose(12, 0)), Point("bench3", "Bench", Pose(14, 0)),
            };
            var sim = Create(points, null, 8, Npc("Preacher", "pulpit"), Npc("U", "bench1"), Npc("V", "bench2"), Npc("W", "bench3"));
            sim.MayListen = id => id.Name != "W"; // e.g. a slave
            Advance(sim, 0.5f);
            Assert.That(_said.Single(), Is.EqualTo(("Preacher", "Speech_00")));
            var u = Agent(sim, "U");
            var v = Agent(sim, "V");
            Assert.That(u.ListeningTo.Name, Is.EqualTo("Preacher"));
            Assert.That(u.Station.Point.Name, Is.EqualTo("l1"), "the nearest listener goes to the nearest point");
            Assert.That(v.Station.Point.Name, Is.EqualTo("l2"));
            Assert.That(Agent(sim, "W").Station.Point.Name, Is.EqualTo("bench3"));
            Advance(sim, 30);
            Assert.That(u.Activity, Is.EqualTo(NpcActivity.AtStation));
            Assert.That(sim.InteractionOf(u), Is.EqualTo("Listener"));
            Advance(sim, Preaching.LineSeconds * 10);
            Assert.That(_said.Where(s => s.speaker == "Preacher").Select(s => s.line).Take(10),
                        Is.EqualTo(Enumerable.Range(0, 10).Select(Preaching.LineOf)));
            Assert.That(u.ListeningTo.IsEmpty, Is.True, "released after Speech_09");
            Assert.That(u.Station.Point.Name, Is.EqualTo("bench1"), "back to its routine");
            Assert.That(sim.SermonLine(new ActorId("Preacher")), Is.EqualTo(-1), "a break (no anchor)");
        }

        [Test]
        public void Preacher_WithoutSpeechLines_OnlyGestures()
        {
            var points = new List<RoutinePointDef> { Point("pulpit", "Preach", Pose(0, 0)), Point("l1", "Listener", Pose(3, 0)), Point("bench1", "Bench", Pose(10, 0)) };
            var sim = Create(points, null, 8, Npc("Preacher", "pulpit"), Npc("U", "bench1"));
            sim.HasLine = (id, line) => false;
            Advance(sim, 5);
            Assert.That(_said, Is.Empty);
            Assert.That(Agent(sim, "U").Station.Point.Name, Is.EqualTo("bench1"));
        }

        [Test]
        public void Smith_GoesForgeAnvilForgeAnvilBarrel_ThenStartsAgain()
        {
            var points = new List<RoutinePointDef>
            {
                Point("anvil", "Anvil", "smithy", Pose(0, 0)), Point("forge", "Forge", "smithy", Pose(3, 0)),
                Point("barrel", "WaterBarrel", "smithy", Pose(0, 3)),
            };
            var anchors = new List<AnchorDef> { new AnchorDef(new Id<AnchorDef>("smithy"), AnchorKind.Local) };
            var sim = Create(points, anchors, 8, Npc("Smith", "anvil"));
            var smith = Agent(sim, "Smith");
            var visited = new List<string>();
            for (int i = 0; i < 2400 && visited.Count < 6; i++)
            {
                Advance(sim, 1);
                if (smith.Activity == NpcActivity.AtStation && (visited.Count == 0 || visited.Last() != smith.Station.Point.Name))
                    visited.Add(smith.Station.Point.Name);
            }
            // After the barrel the task ends; the routine sends it back to its anvil, where ZS_Smith starts again at the forge.
            Assert.That(visited, Is.EqualTo(new[] { "forge", "anvil", "forge", "anvil", "barrel", "forge" }));
        }

        [Test]
        public void Sweeper_SweepsThreeSpotsWithinTenMetres_ThenGoesOnWithItsRoutine()
        {
            var points = new List<RoutinePointDef>
            {
                Point("f1", "CleanFloor", Pose(0, 0)), Point("f2", "CleanFloor", Pose(4, 0)), Point("f3", "CleanFloor", Pose(0, 5)),
                Point("ffar", "CleanFloor", Pose(30, 0)),
            };
            var sim = Create(points, null, 8, Npc("Babe", "f1"));
            var babe = Agent(sim, "Babe");
            var visited = new List<string> { "f1" };
            for (int i = 0; i < 400; i++)
            {
                Advance(sim, 1);
                if (babe.Activity == NpcActivity.AtStation && visited.Last() != babe.Station.Point.Name) visited.Add(babe.Station.Point.Name);
            }
            Assert.That(visited.Take(4), Is.EqualTo(new[] { "f1", "f2", "f3", "f1" }), "own spot, the nearest other, the next, then the routine's spot");
            Assert.That(visited, Has.No.Member("ffar"));
        }

        [Test]
        public void StoneCarrier_AlternatesBetweenTakeAndDropSpots()
        {
            var points = new List<RoutinePointDef>
            {
                Point("take", "TakeStone", "quarry", Pose(0, 0)), Point("drop", "DropStone", "quarry", Pose(8, 0)),
            };
            var anchors = new List<AnchorDef> { new AnchorDef(new Id<AnchorDef>("quarry"), AnchorKind.Local) };
            var sim = Create(points, anchors, 8, Npc("Slave", "take"));
            var slave = Agent(sim, "Slave");
            var visited = new List<string> { "take" };
            for (int i = 0; i < 120; i++)
            {
                Advance(sim, 1);
                if (slave.Activity == NpcActivity.AtStation && visited.Last() != slave.Station.Point.Name) visited.Add(slave.Station.Point.Name);
            }
            Assert.That(visited.Take(5), Is.EqualTo(new[] { "take", "drop", "take", "drop", "take" }));
        }

        [Test]
        public void NpcParty_MembersFollowTheLeader_WhichEndsThePartyFiveSecondsAfterReachingItsDestination()
        {
            var points = new List<RoutinePointDef> { Point("home", "Stand", Pose(0, 0)), Point("camp", "Stand", Pose(60, 0)) };
            var sim = Create(points, null, 8, Npc("Leader", "home"), Npc("Follower", "home"));
            var parties = new NpcParties();
            sim.Parties = parties;
            Assert.That(parties.Join(new ActorId("Follower"), new ActorId("Leader")), Is.True);
            sim.SetRoutine(new ActorId("Leader"), null);
            var leader = Agent(sim, "Leader");
            var follower = Agent(sim, "Follower");
            // send the leader to the camp: its routine destination
            sim.SetRoutine(leader.Id, "toCamp");
            Advance(sim, 2);
            Assert.That(follower.Activity, Is.EqualTo(NpcActivity.Escorting), "ZS_FollowNPC");
            Assert.That(sim.TryGetNpcLeader(follower.Id, out var l) && l == leader, Is.True);
            for (int i = 0; i < 120 && leader.Activity != NpcActivity.AtStation; i++) Advance(sim, 0.5f);
            Assert.That(leader.Activity, Is.EqualTo(NpcActivity.AtStation));
            Assert.That(parties.Count, Is.EqualTo(1));
            Assert.That(sim.PoseOf(follower).DistanceTo(sim.PoseOf(leader)), Is.LessThanOrEqualTo(NpcParties.FollowDistance + 0.5f));
            Advance(sim, NpcParties.LeaderWaitSeconds + 1);
            Assert.That(parties.Count, Is.Zero, "the party ended");
            Advance(sim, 1);
            Assert.That(follower.Activity, Is.Not.EqualTo(NpcActivity.Escorting), "back to its routine");
        }

        [Test]
        public void NpcParties_SaveRoundTrip_AndARemovedLeaderReleasesItsMembers()
        {
            var parties = new NpcParties();
            parties.Join(new ActorId("M1"), new ActorId("L"));
            parties.Join(new ActorId("M2"), new ActorId("L"));
            Assert.That(parties.Join(new ActorId("M3"), ActorId.Player), Is.False);
            var copy = new NpcParties();
            SaveRoundTrip(new NpcPartiesSaveSection(parties), new NpcPartiesSaveSection(copy));
            Assert.That(copy.MembersOf(new ActorId("L")).Select(i => i.Name), Is.EqualTo(new[] { "M1", "M2" }));
            copy.Forget(new ActorId("L"));
            Assert.That(copy.Count, Is.Zero);
        }

        [Test]
        public void MobMembers_LeaveBeyond620MetresFromTheFireclan()
        {
            var fireclan = new WorldPose(0, 0, 0, 0);
            Assert.That(MobMeetingPoint.TooFar(new WorldPose(600, 0, 0, 0), fireclan), Is.False);
            Assert.That(MobMeetingPoint.TooFar(new WorldPose(621, 0, 0, 0), fireclan), Is.True);
        }

        static void SaveRoundTrip(ISaveParticipant from, ISaveParticipant to)
        {
            var w = new System.IO.MemoryStream();
            from.Write(new BinarySaveWriter(w));
            to.Read(new BinarySaveReader(new System.IO.MemoryStream(w.ToArray())), from.Version);
        }

        static NpcDef Npc(string name, string point) =>
            new NpcDef(new ActorId(name), default, null, "Human", "visual", Pose(0, 0), new NpcRoutineDef(Id(point)),
                       routines: new Dictionary<string, NpcRoutineDef> { ["toCamp"] = new NpcRoutineDef(Id("camp")) });

        static NpcAgent Agent(NpcSimulation sim, string name) => sim.TryGet(new ActorId(name), out var a) ? a : null;
        static Id<RoutinePointDef> Id(string id) => new Id<RoutinePointDef>(id);
        static WorldPose Pose(float x, float z) => new WorldPose(x, 0, z, 0);
        static RoutinePointDef Point(string id, string interaction, params WorldPose[] slots) => new RoutinePointDef(Id(id), id, interaction, slots);
        static RoutinePointDef Point(string id, string interaction, string anchor, params WorldPose[] slots) =>
            new RoutinePointDef(Id(id), id, interaction, slots, new Id<AnchorDef>(anchor));
    }
}
