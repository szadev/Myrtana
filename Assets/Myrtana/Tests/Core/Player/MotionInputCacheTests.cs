using G3.Player;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// crash-2 (build 22c, mecanim crash in Animator::OnGraphTopologyChanged): the motion player's slot mixers keep their
    /// clip playables (MotionInputCache) so cross-fades between known clips change no topology; new clips append, then
    /// replace the least recently used input the current request does not use; a new request takes the free slot that
    /// already holds most of its clips; topology changes wait for an active, shown Animator.
    /// </summary>
    public sealed class MotionInputCacheTests
    {
        sealed class Clip { public readonly string Name; public Clip(string n) => Name = n; public override string ToString() => Name; }

        static readonly Clip Walk = new Clip("walk"), Run = new Clip("run"), Idle = new Clip("idle"), Hit = new Clip("hit"), Jump = new Clip("jump");

        [Test]
        public void Bind_UnknownClips_StayUnconnected_UntilPlaced()
        {
            var cache = new MotionInputCache<Clip>(4);
            var keys = new[] { Walk, null, Run };
            var inputs = new int[3];
            cache.Bind(keys, inputs);
            Assert.That(inputs, Is.EqualTo(new[] { -1, -1, -1 }));
            var p = cache.Place(Walk, inputs);
            Assert.That(p.Appended, Is.True);
            Assert.That(p.Input, Is.EqualTo(0));
            Assert.That(cache.Count, Is.EqualTo(1));
        }

        [Test]
        public void CrossfadeBetweenKnownClips_ReusesInputs_NoPlacement()
        {
            var cache = new MotionInputCache<Clip>(4);
            var inputs = new int[2];
            cache.Bind(new[] { Walk, Run }, inputs);
            inputs[0] = cache.Place(Walk, inputs).Input;
            inputs[1] = cache.Place(Run, inputs).Input;
            for (int round = 0; round < 10; round++)
            {
                var again = new int[2];
                cache.Bind(new[] { Run, Walk }, again);
                Assert.That(again, Is.EqualTo(new[] { 1, 0 }), "each clip found at its fixed input");
            }
            Assert.That(cache.Count, Is.EqualTo(2), "no new inputs");
        }

        [Test]
        public void SameClipTwice_GetsTwoInputs()
        {
            var cache = new MotionInputCache<Clip>(4);
            var inputs = new int[2];
            cache.Bind(new[] { Idle, Idle }, inputs);
            inputs[0] = cache.Place(Idle, inputs).Input;
            inputs[1] = cache.Place(Idle, inputs).Input;
            Assert.That(inputs[0], Is.Not.EqualTo(inputs[1]));
            var again = new int[2];
            cache.Bind(new[] { Idle, Idle }, again);
            Assert.That(again[0], Is.Not.EqualTo(again[1]));
            Assert.That(again[0] >= 0 && again[1] >= 0, Is.True);
            Assert.That(cache.Hits(new[] { Idle, Idle, Idle }), Is.EqualTo(2));
        }

        [Test]
        public void Full_ReplacesLeastRecentlyUsed_NotTheCurrentRequest()
        {
            var cache = new MotionInputCache<Clip>(3);
            var none = new int[0];
            cache.Place(Walk, none);   // 0
            cache.Place(Run, none);    // 1
            cache.Place(Idle, none);   // 2
            var inputs = new int[2];
            cache.Bind(new[] { Walk, Hit }, inputs);   // walk used again: run is now the oldest
            Assert.That(inputs[0], Is.EqualTo(0));
            var p = cache.Place(Hit, inputs);
            Assert.That(p.Appended, Is.False);
            Assert.That(p.Input, Is.EqualTo(1));
            Assert.That(p.Evicted, Is.SameAs(Run));
            Assert.That(cache.KeyAt(1), Is.SameAs(Hit));
            Assert.That(cache.Count, Is.EqualTo(3));
        }

        [Test]
        public void Full_EveryInputInUse_Grows()
        {
            var cache = new MotionInputCache<Clip>(2);
            var inputs = new[] { -1, -1, -1 };
            inputs[0] = cache.Place(Walk, inputs).Input;
            inputs[1] = cache.Place(Run, inputs).Input;
            var p = cache.Place(Idle, inputs);
            Assert.That(p.Appended, Is.True);
            Assert.That(p.Input, Is.EqualTo(2));
        }

        [Test]
        public void PickSlot_FreeSlotWithMostClips_LowestOnTie_NoneWhenBusy()
        {
            var a = new MotionInputCache<Clip>(8);
            var b = new MotionInputCache<Clip>(8);
            var c = new MotionInputCache<Clip>(8);
            var none = new int[0];
            a.Place(Hit, none);
            b.Place(Walk, none); b.Place(Run, none);
            c.Place(Walk, none);
            var caches = new[] { a, b, c };
            Assert.That(MotionInputCache<Clip>.PickSlot(caches, new[] { false, false, false }, new[] { Walk, Run }), Is.EqualTo(1));
            Assert.That(MotionInputCache<Clip>.PickSlot(caches, new[] { false, true, false }, new[] { Walk, Run }), Is.EqualTo(2));
            Assert.That(MotionInputCache<Clip>.PickSlot(caches, new[] { false, false, false }, new[] { Jump }), Is.EqualTo(0));
            Assert.That(MotionInputCache<Clip>.PickSlot(caches, new[] { true, true, true }, new[] { Walk }), Is.EqualTo(-1));
        }

        [Test]
        public void Clear_ForgetsInputs()
        {
            var cache = new MotionInputCache<Clip>(4);
            cache.Place(Walk, new int[0]);
            cache.Clear();
            Assert.That(cache.Count, Is.EqualTo(0));
            Assert.That(cache.Hits(new[] { Walk }), Is.EqualTo(0));
        }

        [Test]
        public void Topology_OnlyOnActiveShownAnimator()
        {
            Assert.That(MotionTopology.CanChange(true, false, false), Is.True, "always animated (the hero)");
            Assert.That(MotionTopology.CanChange(true, true, true), Is.True, "culled completely but visible");
            Assert.That(MotionTopology.CanChange(true, true, false), Is.False, "culled: wait until shown");
            Assert.That(MotionTopology.CanChange(false, false, true), Is.False, "disabled (pooled)");
        }
    }
}
