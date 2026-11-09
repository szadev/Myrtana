using G3.Player;
using Myrtana.Rules;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// fix-202 (owner, build 23e): a killed NPC fell, was snapped back up and fell again. The motion player's graph kept
    /// posing a ragdoll's bones (a disabled Animator does not stop it), the death lunge dragged the falling bodies, and the
    /// ragdoll's lift out of the ground jumped. PoseHolds, RagdollRules.LungeMovesBody, CorpseRestRules.SettlesAtOnce.
    /// </summary>
    public sealed class DeathFallTests
    {
        [Test]
        public void PoseHolds_StopOnFirstOwner_PlayAfterLast()
        {
            var holds = new PoseHolds();
            object ragdoll = new object(), corpse = new object();
            Assert.That(holds.Held, Is.False);
            Assert.That(holds.Hold(ragdoll), Is.True, "first owner stops the graph");
            Assert.That(holds.Hold(corpse), Is.False, "already stopped");
            Assert.That(holds.Hold(ragdoll), Is.False, "same owner twice");
            Assert.That(holds.Release(ragdoll), Is.False, "the corpse still holds the pose");
            Assert.That(holds.Held, Is.True);
            Assert.That(holds.Release(ragdoll), Is.False, "released twice: no change");
            Assert.That(holds.Release(corpse), Is.True, "last owner gone: the graph plays again");
            Assert.That(holds.Held, Is.False);
        }

        [Test]
        public void PoseHolds_NullAndUnknownOwners()
        {
            var holds = new PoseHolds();
            Assert.That(holds.Hold(null), Is.False);
            Assert.That(holds.Held, Is.False);
            Assert.That(holds.Release(new object()), Is.False, "never held");
            holds.Hold("a");
            holds.Clear();
            Assert.That(holds.Held, Is.False);
        }

        [Test]
        public void Lunge_NotForADeadRagdoll()
        {
            Assert.That(RagdollRules.LungeMovesBody(dead: true, ragdoll: true), Is.False, "physics carries the body");
            Assert.That(RagdollRules.LungeMovesBody(dead: true, ragdoll: false), Is.True, "the dying motion's distance (pose corpse)");
            Assert.That(RagdollRules.LungeMovesBody(dead: false, ragdoll: false), Is.True, "stumbles, falls, attacks");
        }

        [Test]
        public void Settle_BlendsUnlessRestored()
        {
            Assert.That(CorpseRestRules.SettlesAtOnce(restored: true), Is.True, "a corpse met again lies at once");
            Assert.That(CorpseRestRules.SettlesAtOnce(restored: false), Is.False, "a fresh corpse (ragdoll too) blends onto the ground");
        }
    }
}
