using System.Collections.Generic;
using G3.Unity;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>dx12-3: deferred GPU releases (D3D12-safe lifetimes) and the GPU diagnostics switch / frame line.</summary>
    public class GpuSafetyTests
    {
        [Test]
        public void DeferredRelease_WaitsTheDelayThenReleasesOldestFirst()
        {
            var q = new DeferredRelease<string>(3);
            var gone = new List<string>();
            q.Add("a", 10);
            q.Add("b", 11);
            q.Add(null, 11);   // ignored
            Assert.That(q.Count, Is.EqualTo(2));
            Assert.That(q.Drain(10, gone.Add), Is.EqualTo(0));
            Assert.That(q.Drain(12, gone.Add), Is.EqualTo(0), "2 frames: a draw queued in frame 10 may still render in 11/12");
            Assert.That(q.Drain(13, gone.Add), Is.EqualTo(1));
            Assert.That(gone, Is.EqualTo(new[] { "a" }));
            Assert.That(q.Drain(14, gone.Add), Is.EqualTo(1));
            Assert.That(gone, Is.EqualTo(new[] { "a", "b" }));
            Assert.That(q.Count, Is.EqualTo(0));
        }

        [Test]
        public void DeferredRelease_FrameCounterResetAndDrainAll()
        {
            var q = new DeferredRelease<string>(3);
            var gone = new List<string>();
            q.Add("old", 5000);
            Assert.That(q.Drain(2, gone.Add), Is.EqualTo(1), "a new Play session restarts the frame count: everything older is due");
            q.Add("x", 1); q.Add("y", 2);
            Assert.That(q.DrainAll(gone.Add), Is.EqualTo(2));
            Assert.That(gone, Is.EqualTo(new[] { "old", "x", "y" }));
            Assert.That(new DeferredRelease<string>(0).Delay, Is.EqualTo(1));
        }

        [Test]
        public void GpuDiagSwitch_CommandLineOrEnv()
        {
            Assert.That(GpuDiagSwitch.Enabled(new[] { "G3WorldPlayer.exe" }, null), Is.False);
            Assert.That(GpuDiagSwitch.Enabled(new[] { "G3WorldPlayer.exe", "-G3GpuDiag" }, null), Is.True);
            Assert.That(GpuDiagSwitch.Enabled(new[] { "x", "-g3gpudiagx" }, ""), Is.False);
            Assert.That(GpuDiagSwitch.Enabled(null, " 1 "), Is.True);
            Assert.That(GpuDiagSwitch.Enabled(null, "yes"), Is.True);
            Assert.That(GpuDiagSwitch.Enabled(null, "0"), Is.False);
        }

        [Test]
        public void GpuDiagFrame_OneLinePerFrameInFirstUseOrder()
        {
            var f = new GpuDiagFrame();
            Assert.That(f.Empty, Is.True);
            f.Add("grass.cull"); f.Add("relief.write", 2); f.Add("grass.cull"); f.Add(null);
            Assert.That(f.Get("grass.cull"), Is.EqualTo(2));
            Assert.That(f.Take(42), Is.EqualTo("f42 grass.cull=2 relief.write=2"));
            Assert.That(f.Empty, Is.True);
            Assert.That(f.Take(43), Is.EqualTo("f43"));
        }
    }
}
