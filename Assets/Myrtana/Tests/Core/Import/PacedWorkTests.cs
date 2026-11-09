using System.Collections.Generic;
using G3.Unity;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// The GPU-Resident-Drawer-safe pacing of bulk renderer changes (G3RendererChanges, Docs/performance.md "Native crash
    /// with the GPU Resident Drawer") and the <c>-g3nogrd</c> fallback switch.
    /// </summary>
    public class PacedWorkTests
    {
        static PacedWork<int> Queue(int count, List<int> ran, int weight = 1)
        {
            var q = new PacedWork<int>();
            for (int i = 0; i < count; i++)
            {
                int k = i;
                q.Enqueue(k, () => { ran.Add(k); return weight; });
            }
            return q;
        }

        [Test]
        public void Runs_InOrder_WithinTheWeightBudget()
        {
            var ran = new List<int>();
            var q = Queue(10, ran);
            Assert.AreEqual(4, q.Run(new PaceBudget(4, 0), null, out int w));
            Assert.AreEqual(4, w);
            CollectionAssert.AreEqual(new[] { 0, 1, 2, 3 }, ran);
            Assert.AreEqual(6, q.Count);
            q.Run(new PaceBudget(4, 0), null, out _);
            q.Run(new PaceBudget(4, 0), null, out _);
            Assert.AreEqual(0, q.Count);
            Assert.AreEqual(10, ran.Count);
        }

        [Test]
        public void FirstItem_AlwaysRuns_EvenOverBudget()
        {
            var ran = new List<int>();
            var q = Queue(3, ran, weight: 5000);
            Assert.AreEqual(1, q.Run(new PaceBudget(10, 0.001), () => 99, out int w));
            Assert.AreEqual(5000, w);
            Assert.AreEqual(2, q.Count);
        }

        [Test]
        public void ZeroWeightItems_DoNotUseTheBudget()
        {
            var ran = new List<int>();
            var q = Queue(100, ran, weight: 0);
            Assert.AreEqual(100, q.Run(new PaceBudget(1, 0), null, out int w));
            Assert.AreEqual(0, w);
        }

        [Test]
        public void TimeBudget_StopsTheFrame()
        {
            var ran = new List<int>();
            var q = Queue(10, ran, weight: 0);
            double t = 0;
            Assert.AreEqual(3, q.Run(new PaceBudget(0, 2.5), () => t++, out _));
        }

        [Test]
        public void Unlimited_RunsEverything()
        {
            var ran = new List<int>();
            var q = Queue(5000, ran);
            Assert.IsTrue(new PaceBudget(0, 0).Unlimited);
            Assert.AreEqual(5000, q.Run(new PaceBudget(0, 0), () => 1e9, out _));
            Assert.AreEqual(0, q.Count);
        }

        [Test]
        public void Requeue_ReplacesWork_KeepsPlace_NeverGrows()
        {
            var ran = new List<string>();
            var q = new PacedWork<int>();
            // a switch flipped 30 times while the wave runs: one entry per key, the last work wins, the first place stays
            for (int flip = 0; flip < 30; flip++)
                for (int k = 0; k < 3; k++)
                {
                    int key = k, f = flip;
                    q.Enqueue(key, () => { ran.Add($"{key}:{f}"); return 1; });
                }
            Assert.AreEqual(3, q.Count);
            q.Run(new PaceBudget(0, 0), null, out _);
            CollectionAssert.AreEqual(new[] { "0:29", "1:29", "2:29" }, ran);
        }

        [Test]
        public void KeyRunAgain_AfterItRan_GoesToTheBack()
        {
            var ran = new List<int>();
            var q = Queue(3, ran);
            q.Run(new PaceBudget(1, 0), null, out _);          // 0 ran
            q.Enqueue(0, () => { ran.Add(100); return 1; });  // 0 again: behind 1 and 2
            q.Run(new PaceBudget(0, 0), null, out _);
            CollectionAssert.AreEqual(new[] { 0, 1, 2, 100 }, ran);
        }

        [Test]
        public void Remove_SkipsTheKey()
        {
            var ran = new List<int>();
            var q = Queue(3, ran);
            Assert.IsTrue(q.Remove(1));
            q.Run(new PaceBudget(0, 0), null, out _);
            CollectionAssert.AreEqual(new[] { 0, 2 }, ran);
        }

        [Test]
        public void ThrowingWork_IsDropped_QueueStaysConsistent()
        {
            var q = new PacedWork<int>();
            q.Enqueue(1, () => throw new System.InvalidOperationException("boom"));
            q.Enqueue(2, () => 1);
            Assert.Throws<System.InvalidOperationException>(() => q.Run(new PaceBudget(0, 0), null, out _));
            Assert.AreEqual(1, q.Count);
            Assert.IsFalse(q.Contains(1));
            Assert.AreEqual(1, q.Run(new PaceBudget(0, 0), null, out _));
            Assert.AreEqual(0, q.Count);
        }

        [Test]
        public void LongWave_CompactsAndKeepsOrder()
        {
            var ran = new List<int>();
            var q = Queue(20000, ran);
            int frames = 0;
            while (q.Count > 0 && frames < 1000) { q.Run(new PaceBudget(512, 0), null, out _); frames++; }
            Assert.AreEqual(40, frames);   // 20000 / 512 rounded up
            for (int i = 0; i < ran.Count; i++) Assert.AreEqual(i, ran[i]);
        }

        [Test]
        public void SwapRate_IsParsed()
        {
            Assert.AreEqual(-1, RenderTuningOptions.Parse(new[] { "x.exe" }).SwapRate);
            var o = RenderTuningOptions.Parse(new[] { "-g3swaprate", "128" });
            Assert.AreEqual(128, o.SwapRate);
            StringAssert.EndsWith(" swaprate=128", o.Describe());
            Assert.AreEqual(0, RenderTuningOptions.Parse(new[] { "-G3SWAPRATE", "-5" }).SwapRate);
        }

        [Test]
        public void GrdFallback_CommandLineThenEnv()
        {
            Assert.IsFalse(GrdFallback.Disable(new[] { "G3World.exe" }, null));
            Assert.IsTrue(GrdFallback.Disable(new[] { "G3World.exe", "-g3nogrd" }, null));
            Assert.IsTrue(GrdFallback.Disable(new[] { "G3World.exe", "-G3NoGrd" }, "0"));
            Assert.IsTrue(GrdFallback.Disable(null, "1"));
            Assert.IsTrue(GrdFallback.Disable(new string[0], " true "));
            Assert.IsFalse(GrdFallback.Disable(new string[0], "0"));
            Assert.IsFalse(GrdFallback.Disable(new[] { "-g3grd" }, "1"), "-g3grd forces it on again");
            Assert.IsFalse(GrdFallback.Disable(new[] { "-g3nogrd", "-g3grd" }, null), "the last switch wins");
            Assert.IsTrue(GrdFallback.Disable(new[] { "-g3grd", "-g3nogrd" }, null));
        }
    }
}
