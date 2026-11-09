using G3.Unity;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>NPC update LOD (perf-8): every frame near or when important, slower far away and out of view.</summary>
    public class NpcUpdateLodTests
    {
        [Test]
        public void Near_Important_OrDisabled_EveryFrame()
        {
            Assert.AreEqual(1, NpcUpdateLod.Interval(true, false, 10f, false));
            Assert.AreEqual(1, NpcUpdateLod.Interval(true, false, NpcUpdateLod.FullDistance - 0.01f, true));
            Assert.AreEqual(1, NpcUpdateLod.Interval(true, true, 150f, false));
            Assert.AreEqual(1, NpcUpdateLod.Interval(false, false, 150f, false));
            Assert.AreEqual(1, NpcUpdateLod.Interval(true, false, float.NaN, false));
        }

        [Test]
        public void Far_ByDistanceAndVisibility()
        {
            Assert.AreEqual(2, NpcUpdateLod.Interval(true, false, NpcUpdateLod.FullDistance, true));
            Assert.AreEqual(3, NpcUpdateLod.Interval(true, false, NpcUpdateLod.HalfDistance, true));
            Assert.AreEqual(NpcUpdateLod.MaxInterval, NpcUpdateLod.Interval(true, false, 40f, false));
        }

        [Test]
        public void Due_OncePerInterval_ShiftedByPhase()
        {
            for (int interval = 1; interval <= NpcUpdateLod.MaxInterval; interval++)
                for (int phase = 0; phase < interval; phase++)
                {
                    int due = 0;
                    for (int f = 100; f < 100 + interval * 5; f++) if (NpcUpdateLod.Due(f, interval, phase)) due++;
                    Assert.AreEqual(5, due, $"interval {interval} phase {phase}");
                }
            Assert.IsTrue(NpcUpdateLod.Due(7, 1, 3));
            Assert.AreNotEqual(NpcUpdateLod.Due(8, 2, 0), NpcUpdateLod.Due(8, 2, 1));
        }

        [Test]
        public void Frames_FollowTheFrameRate_NeverBelowTheTier()
        {
            Assert.AreEqual(1, NpcUpdateLod.Frames(1, 1f / 120f));
            // 120 FPS: 30 / 15 / 5 Hz
            Assert.AreEqual(4, NpcUpdateLod.Frames(2, 1f / 120f));
            Assert.AreEqual(8, NpcUpdateLod.Frames(3, 1f / 120f));
            Assert.AreEqual(24, NpcUpdateLod.Frames(4, 1f / 120f));
            // 60 FPS
            Assert.AreEqual(2, NpcUpdateLod.Frames(2, 1f / 60f));
            Assert.AreEqual(4, NpcUpdateLod.Frames(3, 1f / 60f));
            Assert.AreEqual(12, NpcUpdateLod.Frames(4, 1f / 60f));
            // low frame rates: perf-8's frame intervals (never more often)
            Assert.AreEqual(2, NpcUpdateLod.Frames(2, 1f / 20f));
            Assert.AreEqual(3, NpcUpdateLod.Frames(3, 1f / 20f));
            Assert.AreEqual(4, NpcUpdateLod.Frames(4, 1f / 20f));
            // capped; unknown frame time = the tier
            Assert.AreEqual(NpcUpdateLod.MaxFrames, NpcUpdateLod.Frames(4, 1f / 400f));
            Assert.AreEqual(4, NpcUpdateLod.Frames(4, 0f));
            Assert.AreEqual(3, NpcUpdateLod.Frames(3, float.NaN));
        }

        [Test]
        public void PresentationFrames_NearOutOfView_TierTwoRate()
        {
            float f = 1f / 120f;
            Assert.AreEqual(1, NpcUpdateLod.PresentationFrames(1, false, 5f, false, f), "very near: every frame");
            Assert.AreEqual(1, NpcUpdateLod.PresentationFrames(1, false, 20f, true, f), "near, visible");
            Assert.AreEqual(4, NpcUpdateLod.PresentationFrames(1, false, 20f, false, f), "near, out of view: 30 Hz");
            Assert.AreEqual(1, NpcUpdateLod.PresentationFrames(1, true, 20f, false, f), "important");
            Assert.AreEqual(24, NpcUpdateLod.PresentationFrames(4, false, 40f, false, f), "far tiers unchanged");
            Assert.AreEqual(2, NpcUpdateLod.PresentationFrames(1, false, 20f, false, 1f / 20f), "low frame rate: every 2nd frame");
        }

        [Test]
        public void Due_LongIntervals_OncePerInterval()
        {
            foreach (int interval in new[] { 8, 12, 24, NpcUpdateLod.MaxFrames })
                foreach (int phase in new[] { 0, 7, 1234 })
                {
                    int due = 0;
                    for (int f = 1000; f < 1000 + interval * 3; f++) if (NpcUpdateLod.Due(f, interval, phase)) due++;
                    Assert.AreEqual(3, due, $"interval {interval} phase {phase}");
                }
        }

        [Test]
        public void StepClock_GathersWhatPerFrameAddingGave()
        {
            var c = new StepClock();
            Assert.AreEqual(0.016f, c.Take(1.0, 0.016f, 10), 1e-6, "never reset: this frame's delta (pending += dt)");
            Assert.AreEqual(0.02f, c.Take(1.02, 0.02f, 11), 1e-6, "every frame: exactly the delta time");
            Assert.AreEqual(0f, c.Take(1.02, 0.02f, 11), "twice in a frame: nothing gathered");
            // skipped frames 12, 13: the time since the last step
            Assert.AreEqual(0.06f, c.Take(1.08, 0.02f, 14), 1e-5);
            // reset before the batch ran this frame (pending = 0, then += dt): this frame's delta counts
            c.Reset(2.0, 0.025f, 20, steppedThisFrame: false);
            Assert.AreEqual(0.025f, c.Take(2.0, 0.025f, 20), 1e-6);
            // reset after the batch ran (its += dt was before the reset): only the next frames count
            c.Reset(3.0, 0.025f, 30, steppedThisFrame: true);
            Assert.AreEqual(0.01f, c.Take(3.01, 0.01f, 31), 1e-6);
            c.Reset(4.0, 0.025f, 40, steppedThisFrame: true);
            Assert.AreEqual(0.05f, c.Take(4.05, 0.01f, 43), 1e-5);
        }

        [Test]
        public void FrameBatchList_OrderAddRemoveWhileRunning()
        {
            var list = new FrameBatchList<string>();
            Assert.IsTrue(list.Add("a"));
            Assert.IsTrue(list.Add("b"));
            Assert.IsTrue(list.Add("c"));
            Assert.IsFalse(list.Add("b"), "once");
            Assert.IsFalse(list.Add(null));
            var seen = new System.Collections.Generic.List<string>();
            list.Run(x =>
            {
                seen.Add(x);
                if (x == "a") { list.Remove("b"); list.Add("d"); }   // b is not stepped any more, d waits for the next run
                if (x == "c") throw new System.InvalidOperationException("boom");
            }, (x, e) => seen.Add("failed " + x));
            CollectionAssert.AreEqual(new[] { "a", "c", "failed c" }, seen);
            Assert.AreEqual(3, list.Count);
            seen.Clear();
            list.Run(seen.Add, null);
            CollectionAssert.AreEqual(new[] { "a", "c", "d" }, seen);
            Assert.IsTrue(list.Remove("a"));
            Assert.IsFalse(list.Remove("a"));
            seen.Clear();
            list.Run(seen.Add, null);
            CollectionAssert.AreEqual(new[] { "c", "d" }, seen);
            list.Clear();
            Assert.AreEqual(0, list.Count);
        }

        [Test]
        public void Option_DefaultsOn_SwitchOff()
        {
            Assert.IsTrue(RenderTuningOptions.Parse(new[] { "-g3perf" }).NpcLod);
            var o = RenderTuningOptions.Parse(new[] { "-g3npclod", "0" });
            Assert.IsFalse(o.NpcLod);
            Assert.AreEqual("probes=off fade=dither pshadows=def npclod=0", o.Describe());
            Assert.IsFalse(o.NpcLodHidden, "-g3npclod 0: everything every frame");
            var hidden = RenderTuningOptions.Parse(new[] { "-g3npclod", "2" });
            Assert.IsFalse(hidden.NpcLod);
            Assert.IsTrue(hidden.NpcLodHidden);
            Assert.AreEqual("probes=off fade=dither pshadows=def npclod=hidden", hidden.Describe());
            Assert.IsTrue(RenderTuningOptions.Parse(new[] { "-g3perf" }).NpcLodHidden, "default on");
            var menu = new RenderTuningOptions { NpcLod = false };   // Very high object detail
            var merged = RenderTuningOptions.Merge(menu, new RenderTuningOptions());
            Assert.IsFalse(merged.NpcLod);
            Assert.IsTrue(merged.NpcLodHidden);
            Assert.IsFalse(RenderTuningOptions.Merge(menu, o).NpcLodHidden, "the command line wins");
        }
    }
}
