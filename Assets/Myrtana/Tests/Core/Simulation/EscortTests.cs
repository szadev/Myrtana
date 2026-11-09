using System.Collections.Generic;
using Myrtana.Core;
using Myrtana.Import.G3;
using Myrtana.Rules;
using Myrtana.Simulation;
using NUnit.Framework;

namespace Myrtana.Tests.Simulation
{
    /// <summary>Party members and guides in the NPC simulation and their movement decisions (Docs/party.md §3–§4).</summary>
    public sealed class EscortTests
    {
        sealed class Escorts : IEscorts
        {
            public readonly Dictionary<ActorId, EscortMode> Modes = new Dictionary<ActorId, EscortMode>();
            public WorldPose? Leader;
            public EscortMode ModeOf(ActorId npc) => Modes.TryGetValue(npc, out var m) ? m : EscortMode.None;
            public float FollowDistance(ActorId npc) => 4.5f;
            public bool TryGetDestination(ActorId guide, out WorldPose destination)
            {
                destination = new WorldPose(50, 0, 0, 0);
                return true;
            }
            public bool TryGetLeader(out WorldPose leader)
            {
                leader = Leader ?? default;
                return Leader.HasValue;
            }
        }

        static readonly ActorId Gorn = new ActorId("Gorn");
        GameClock _clock;

        NpcSimulation Create(Escorts escorts)
        {
            _clock = new GameClock(10, timeScale: 12);
            var points = new List<RoutinePointDef>
            {
                new RoutinePointDef(new Id<RoutinePointDef>("anvil"), "anvil", "Anvil", new[] { new WorldPose(10, 0, 0, 0) }),
            };
            var gorn = new NpcDef(Gorn, default, null, "Human", "visual", new WorldPose(0, 0, 0, 0),
                                  new NpcRoutineDef(new Id<RoutinePointDef>("anvil")));
            var sim = new NpcSimulation(new[] { gorn }, new RoutinePointCatalog(points, new List<AnchorDef>()),
                                        new RoutineSchedule(G3RoutineTables.Schedule()), _clock, new SeededRandom(1)) { Escorts = escorts };
            sim.PlaceAtRoutineStart();
            return sim;
        }

        void Advance(NpcSimulation sim, float realSeconds)
        {
            for (float t = 0; t < realSeconds; t += 0.5f)
            {
                _clock.Advance(0.5f);
                sim.Tick();
            }
        }

        [Test]
        public void Follower_LeavesItsStation_KeepsUpOffScreen_AndReturnsToItsRoutineWhenDismissed()
        {
            var escorts = new Escorts { Leader = new WorldPose(100, 0, 0, 0) };
            var sim = Create(escorts);
            sim.TryGet(Gorn, out var gorn);
            Assert.That(gorn.Activity, Is.EqualTo(NpcActivity.AtStation));

            escorts.Modes[Gorn] = EscortMode.Follow;
            Advance(sim, 2);
            Assert.That(gorn.Activity, Is.EqualTo(NpcActivity.Escorting));
            Assert.That(sim.Claims.TryGetUser(new Station(new Id<RoutinePointDef>("anvil"), 0), out _), Is.False, "the station is free");
            Assert.That(gorn.Pose.HorizontalDistanceTo(escorts.Leader.Value), Is.EqualTo(4.5f).Within(0.01f),
                        "an abstract follower is at its follow distance behind the hero");

            Assert.That(sim.SetRoutine(Gorn, "Start"), Is.True);
            Assert.That(gorn.Activity, Is.EqualTo(NpcActivity.Escorting), "SetRoutine waits until the escort ends");

            escorts.Modes[Gorn] = EscortMode.Wait;
            escorts.Leader = new WorldPose(300, 0, 0, 0);
            Advance(sim, 2);
            Assert.That(gorn.Pose.X, Is.EqualTo(95.5f).Within(0.01f), "a waiting member stays");

            escorts.Modes.Remove(Gorn);
            Advance(sim, 2);
            Assert.That(gorn.Activity, Is.EqualTo(NpcActivity.Travelling), "back to the routine");
            Assert.That(gorn.Station.Point.Name, Is.EqualTo("anvil"));
        }

        [Test]
        public void NearFollower_RunsTowardsTheHero()
        {
            var self = new WorldPose(0, 0, 0, 0);
            var hero = new WorldPose(100, 0, 0, 0);
            var next = AbstractEscort.Follow(self, hero, 4.5f, DetailLevel.Near, 1f);
            Assert.That(next.X, Is.EqualTo(AbstractEscort.CatchUpSpeed).Within(0.01f));
            Assert.That(AbstractEscort.Follow(new WorldPose(97, 0, 0, 0), hero, 4.5f, DetailLevel.Near, 1f).X, Is.EqualTo(97f), "close enough");
        }

        [Test]
        public void FollowPlanner_GoesOnceASecond_StopsWithinTheDistance_SprintsWhenFarBehind()
        {
            var p = new FollowPlanner();
            Assert.That(p.Update(0.1f, 3f, 4.5f, EscortGait.Run), Is.EqualTo(EscortGait.Stand));
            Assert.That(p.Update(0.1f, 6f, 4.5f, EscortGait.Run), Is.EqualTo(EscortGait.Run), "first check right away");
            Assert.That(p.Update(0.1f, 4f, 4.5f, EscortGait.Run), Is.EqualTo(EscortGait.Stand), "within the distance: stop");
            Assert.That(p.Update(0.1f, 6f, 4.5f, EscortGait.Run), Is.EqualTo(EscortGait.Stand), "the next check is a second later");
            Assert.That(p.Update(1f, 6f, 4.5f, EscortGait.Walk), Is.EqualTo(EscortGait.Walk), "matches the hero's gait");
            Assert.That(p.Update(0.1f, 20f, 4.5f, EscortGait.Walk), Is.EqualTo(EscortGait.Sprint), "far behind");
            Assert.That(FollowPlanner.NeedsCatchUp(60f), Is.True);
            Assert.That(FollowPlanner.NeedsCatchUp(40f), Is.False);
        }

        [Test]
        public void FollowPlanner_WaitsWhileTheHeroSwims_FollowsAtTheNextSecondCheckAfterwards()
        {
            var p = new FollowPlanner();
            Assert.That(p.Update(0.1f, 20f, 4.5f, EscortGait.Run, leaderSwimming: true), Is.EqualTo(EscortGait.Stand));
            Assert.That(p.WaitingForSwimmer, Is.True);
            Assert.That(p.Update(2f, 20f, 4.5f, EscortGait.Run, leaderSwimming: true), Is.EqualTo(EscortGait.Stand), "still swimming");
            Assert.That(p.Update(0.5f, 20f, 4.5f, EscortGait.Run), Is.EqualTo(EscortGait.Stand), "out of the water, before the next check");
            Assert.That(p.Update(0.6f, 20f, 4.5f, EscortGait.Run), Is.Not.EqualTo(EscortGait.Stand), "the one-second check ends the wait");
            Assert.That(p.WaitingForSwimmer, Is.False);
        }

        [Test]
        public void GuidePlanner_WaitsForTheHero_AndGoesOnWhenHeIsClose()
        {
            var g = new GuidePlanner();
            Assert.That(g.Update(3f, false, 10f, 8f, 50f, 48f, EscortGait.Walk), Is.EqualTo(EscortGait.Walk));
            Assert.That(g.Update(12f, false, 20f, 8f, 52f, 40f, EscortGait.Walk), Is.EqualTo(EscortGait.Stand), "the hero fell behind");
            Assert.That(g.Waiting, Is.True);
            Assert.That(g.Update(9f, true, 12f, 8f, 49f, 40f, EscortGait.Run), Is.EqualTo(EscortGait.Stand), "waits until within 8 m");
            Assert.That(g.Update(7f, true, 10f, 8f, 47f, 40f, EscortGait.Run), Is.EqualTo(EscortGait.Run));
            Assert.That(g.Update(12f, true, 5f, 8f, 30f, 40f, EscortGait.Run), Is.EqualTo(EscortGait.Run),
                        "CP: no wait while the hero is in view and as close to the current path point");
            Assert.That(g.Update(2f, true, 2f, 0.5f, 2f, 0.5f, EscortGait.Run), Is.EqualTo(EscortGait.Stand), "arrived");
        }

        [Test]
        public void GuidePlanner_WaitingDoesNotFlipWithItsFacing()
        {
            // editor-runner-21 (Arakos, Cole): the hero stands 13.5 m behind on a winding path, nearer the far destination
            // as the crow flies. Waiting, the guide turns to him (in view); it must not go on until he is within 8 m.
            var g = new GuidePlanner();
            Assert.That(g.Update(13.5f, false, 20f, 6f, 110f, 113f, EscortGait.Stand), Is.EqualTo(EscortGait.Stand), "behind, out of view");
            for (int i = 0; i < 20; i++)
            {
                bool inView = i % 2 == 0;
                Assert.That(g.Update(13.5f, inView, 110f, 113f, 110f, 113f, EscortGait.Stand), Is.EqualTo(EscortGait.Stand), $"tick {i}");
            }
            Assert.That(g.Update(7.9f, false, 15f, 6f, 110f, 113f, EscortGait.Walk), Is.EqualTo(EscortGait.Walk), "within 8 m: goes on");
            Assert.That(g.Update(9.5f, false, 15f, 6f, 110f, 113f, EscortGait.Walk), Is.EqualTo(EscortGait.Walk), "8..10 m: keeps walking");
        }

        [Test]
        public void GuidePlanner_Waiting_GoesOnWhenTheHeroIsAtTheDestination()
        {
            var g = new GuidePlanner();
            Assert.That(g.Update(12f, false, 30f, 20f, 30f, 20f, EscortGait.Stand), Is.EqualTo(EscortGait.Stand), "hero behind and out of view: waits");
            Assert.That(g.Waiting, Is.True);
            Assert.That(g.Update(12f, true, 9f, 6.5f, 9f, 16f, EscortGait.Stand), Is.EqualTo(EscortGait.Stand), "ahead but neither close nor there");
            Assert.That(g.Update(9f, true, 3f, 6.5f, 3f, 6.5f, EscortGait.Stand), Is.EqualTo(EscortGait.Walk), "hero at the destination: goes on");
            Assert.That(g.Waiting, Is.False);
            Assert.That(g.Update(9f, true, 3f, 0.8f, 3f, 0.8f, EscortGait.Stand), Is.EqualTo(EscortGait.Stand), "arrived");
        }

        [Test]
        public void PartyLeash_KeepsFightsNearTheHero()
        {
            Assert.That(PartyLeash.BreaksOff(EscortMode.Follow, true, 1723f), Is.True, "a follower chasing far away comes back");
            Assert.That(PartyLeash.BreaksOff(EscortMode.Guide, true, 31f), Is.True);
            Assert.That(PartyLeash.BreaksOff(EscortMode.Follow, true, 25f), Is.False, "fights near the hero go on");
            Assert.That(PartyLeash.GivesUpChase(EscortMode.Guide, true), Is.True, "a guide whose chase ran out guides on");
            Assert.That(PartyLeash.GivesUpChase(EscortMode.Follow, true), Is.True);
            Assert.That(PartyLeash.GivesUpChase(EscortMode.Wait, true), Is.False, "a waiting member defends its spot");
            Assert.That(PartyLeash.GivesUpChase(EscortMode.Guide, false), Is.False, "fighting in reach goes on");
            Assert.That(PartyLeash.BreaksOff(EscortMode.Follow, false, 100f), Is.False, "not fighting");
            Assert.That(PartyLeash.BreaksOff(EscortMode.Wait, true, 100f), Is.False, "a waiting member defends its spot");
            Assert.That(PartyLeash.BreaksOff(EscortMode.Follow, true, float.MaxValue), Is.False, "unknown distance");
        }

        /// <summary>fix-37 (r27 Mort, Rufus, Karlen, Hauke): G3's escort fights back when attacked; only chases are leashed,
        /// and a fight where nobody lands a hit ends.</summary>
        [Test]
        public void PartyLeash_AnAttackedEscortFightsBackOnlyChasesAreLeashed()
        {
            Assert.That(PartyLeash.BreaksOff(EscortMode.Guide, true, 35f, attacked: true), Is.False, "the wolves bite Rufus: he fights (G3 AssessAgressor)");
            Assert.That(PartyLeash.BreaksOff(EscortMode.Guide, true, 35f, attacked: false), Is.True, "a chase 35 m from the hero ends");
            Assert.That(PartyLeash.BreaksOff(EscortMode.Follow, true, 25f, attacked: false), Is.False);
            Assert.That(PartyLeash.GivesUpStalemate(EscortMode.Guide, 20f), Is.True, "Hauke vs a sabertooth below the ledge");
            Assert.That(PartyLeash.GivesUpStalemate(EscortMode.Guide, 5f), Is.False);
            Assert.That(PartyLeash.GivesUpStalemate(EscortMode.Wait, 100f), Is.False, "a waiting member defends its spot");
        }

        [Test]
        public void PartyLeash_TheGuideHasNoLookout_ItFightsOnlyWhatAttacksIt()
        {
            // fix-41 (CP AssessActivePerception: party leader == player skips the hostile step): the guide's look-out picked
            // the enemy it had just broken off from again, 51-63 times in a row (r28 Cole, Karlen, Xardas).
            Assert.That(PartyLeash.LooksOut(PartyMemberType.None, guiding: true), Is.False);
            // the chase that ran out still ends the guide's fight; an attacker hitting it is fought (AssessAgressor)
            Assert.That(PartyLeash.GivesUpChase(EscortMode.Guide, chaseRanOut: true), Is.True);
            Assert.That(PartyLeash.BreaksOff(EscortMode.Guide, true, 40f, attacked: true), Is.False);
        }

        /// <summary>
        /// fix-106 (owner: Cyrus, an Orc-aligned Kap Dun mercenary in the party, attacked the rebel Iomar on the way to
        /// Ortega): CP AssessActivePerception skips the hostile step for an NPC led by the player unless it is Summoned, and
        /// an NPC in AIMode PlayerParty senses only the player. Companions, mobs and slaves never attack on sight.
        /// </summary>
        [Test]
        public void PartyLeash_OnlySummonsLookOut_CompanionsNeverAttackOnSight()
        {
            Assert.That(PartyLeash.LooksOut(PartyMemberType.Party, guiding: false), Is.False, "Cyrus walks past Iomar");
            Assert.That(PartyLeash.LooksOut(PartyMemberType.Mob, guiding: false), Is.False);
            Assert.That(PartyLeash.LooksOut(PartyMemberType.Slave, guiding: false), Is.False);
            Assert.That(PartyLeash.LooksOut(PartyMemberType.Controlled, guiding: false), Is.False);
            Assert.That(PartyLeash.LooksOut(PartyMemberType.Summoned, guiding: false), Is.True, "a summon attacks the hero's attacker (slot 1)");
        }

        [Test]
        public void PartyLeash_LookoutWaitsUntilTheMemberIsBack()
        {
            Assert.That(PartyLeash.LookoutResumes(1f, 4f), Is.False, "just broke off");
            Assert.That(PartyLeash.LookoutResumes(20f, 37f), Is.False, "still far from the hero: no turning back to the enemy");
            Assert.That(PartyLeash.LookoutResumes(6f, 5f), Is.True);
        }
    }
}
