using Myrtana.Simulation;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>fix-142: the way of a combat chase and the unreachable verdict (<see cref="CombatChaseRules"/>).</summary>
    public class CombatChaseTests
    {
        const float Reach = 2f;

        [Test]
        public void Straight_WhenTheNavMeshLineIsClear_OrThereIsNoNavMesh()
        {
            Assert.That(CombatChaseRules.Decide(ChasePlan.Complete, true, 0, 0, Reach), Is.EqualTo(ChaseWay.Straight));
            Assert.That(CombatChaseRules.Decide(ChasePlan.NoNavMesh, false, 0, 0, Reach), Is.EqualTo(ChaseWay.Straight),
                        "no NavMesh yet: the old straight run, no verdict");
        }

        [Test]
        public void Path_RoundTheLedge_WhenThePathGetsThereOrIntoReach()
        {
            Assert.That(CombatChaseRules.Decide(ChasePlan.Complete, false, 0, 0, Reach), Is.EqualTo(ChaseWay.Path),
                        "the ogre at the rim walks round to the hero below");
            Assert.That(CombatChaseRules.Decide(ChasePlan.Partial, false, Reach + 0.5f, 1f, Reach), Is.EqualTo(ChaseWay.Path),
                        "a partial path ending in reach (the hero just off the NavMesh)");
        }

        [Test]
        public void Unreachable_WhenNoWayEndsInReach()
        {
            Assert.That(CombatChaseRules.Decide(ChasePlan.Partial, false, 12f, 0, Reach), Is.EqualTo(ChaseWay.Unreachable));
            Assert.That(CombatChaseRules.Decide(ChasePlan.Partial, false, 0.5f, -5f, Reach), Is.EqualTo(ChaseWay.Unreachable),
                        "the path ends right above the target, 5 m up a cliff");
            Assert.That(CombatChaseRules.Decide(ChasePlan.None, false, 0, 0, Reach), Is.EqualTo(ChaseWay.Unreachable));
        }

        [Test]
        public void Replans_WhenTheTargetMoved_OrThePlanIsOld()
        {
            Assert.IsTrue(CombatChaseRules.Replan(false, 0, 0));
            Assert.IsFalse(CombatChaseRules.Replan(true, 0.2f, 0.3f));
            Assert.IsTrue(CombatChaseRules.Replan(true, CombatChaseRules.ReplanSeconds, 0));
            Assert.IsTrue(CombatChaseRules.Replan(true, 0.1f, CombatChaseRules.ReplanMove));
        }

        [Test]
        public void Tracker_HoldsTheVerdict_BeforeTellingTheBrain_StuckAtOnce()
        {
            var t = new ChaseReachTracker();
            t.Update(ChaseWay.Unreachable, 1f);
            Assert.IsFalse(t.Unreachable, "a re-plan or a drop link may still find a way");
            t.Update(ChaseWay.Path, 0.1f);
            t.Update(ChaseWay.Unreachable, 1f);
            Assert.IsFalse(t.Unreachable, "a way in between starts the count afresh");
            t.Update(ChaseWay.Unreachable, CombatChaseRules.UnreachableSeconds);
            Assert.IsTrue(t.Unreachable);
            t.Update(ChaseWay.Path, 0.1f);
            Assert.IsFalse(t.Unreachable);

            t.Stuck();
            Assert.IsTrue(t.Unreachable, "the walk along the path made no progress");
            t.Update(ChaseWay.Path, 0.1f);
            Assert.IsTrue(t.Unreachable, "a new plan leads into the same wall: stays until reset");
            t.Reset();
            Assert.IsFalse(t.Unreachable);
        }
    }
}
