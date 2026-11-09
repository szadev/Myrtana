using System.Collections.Generic;
using Myrtana.Session;
using Myrtana.Session.Dev;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>fix-172: the FPS overlay's and the perf report's pure statistics, state and texts.</summary>
    public class FrameStatsTests
    {
        [Test]
        public void Window_KeepsOnlyTheLastSeconds()
        {
            var w = new FrameTimeWindow(5f);
            for (int i = 1; i <= 100; i++) w.Add(i * 0.1, 10f);   // 10 s at 10 fps
            Assert.That(w.Count, Is.EqualTo(50), "frames of the last 5 s (t > 5.0)");
            var s = w.Compute();
            Assert.That(s.AvgMs, Is.EqualTo(10f).Within(1e-4));
            Assert.That(s.MaxMs, Is.EqualTo(10f));
        }

        [Test]
        public void Window_AvgLow1AndMax()
        {
            var w = new FrameTimeWindow(5f);
            double t = 0;
            for (int i = 0; i < 198; i++) w.Add(t += 0.01, 10f);
            w.Add(t += 0.04, 40f);
            w.Add(t += 0.06, 60f);
            var s = w.Compute();
            Assert.That(s.Frames, Is.EqualTo(200));
            Assert.That(s.MaxMs, Is.EqualTo(60f));
            Assert.That(s.Low1Ms, Is.EqualTo(50f).Within(1e-4), "1 % of 200 = the 2 slowest: (40 + 60) / 2");
            Assert.That(s.AvgMs, Is.EqualTo((198 * 10f + 100f) / 200f).Within(1e-4));
        }

        [Test]
        public void Window_RingOverflow_DropsTheOldest()
        {
            var w = new FrameTimeWindow(1000f, capacity: 16);
            for (int i = 0; i < 20; i++) w.Add(i, i);
            Assert.That(w.Count, Is.EqualTo(16));
            var s = w.Compute();
            Assert.That(s.MaxMs, Is.EqualTo(19f));
            Assert.That(s.AvgMs, Is.EqualTo((4 + 19) / 2f).Within(1e-4), "frames 4..19 left");
            w.Clear();
            Assert.That(w.Compute().Frames, Is.EqualTo(0));
        }

        [Test]
        public void Smoother_ReportsFramesPerHalfSecond()
        {
            var f = new FpsSmoother(0.5f);
            int windows = 0;
            for (int i = 0; i < 60; i++) if (f.Add(1f / 60f)) windows++;
            Assert.That(windows, Is.EqualTo(2));
            Assert.That(f.Fps, Is.EqualTo(60f).Within(0.5f));
            Assert.That(f.Add(0f), Is.False, "a zero delta is no frame");
        }

        [Test]
        public void Percentile_NearestRank()
        {
            var a = new float[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
            Assert.That(FrameStats.Percentile(a, 10, 50), Is.EqualTo(5f));
            Assert.That(FrameStats.Percentile(a, 10, 95), Is.EqualTo(10f));
            Assert.That(FrameStats.Percentile(a, 10, 90), Is.EqualTo(9f));
            Assert.That(FrameStats.Percentile(a, 10, 0), Is.EqualTo(1f));
            Assert.That(FrameStats.Percentile(a, 0, 50), Is.EqualTo(0f));
        }

        [Test]
        public void Summary_PercentilesHitchesCpuGpu()
        {
            var frames = new List<FrameSample>();
            double t = 0;
            for (int i = 0; i < 100; i++)
            {
                float ms = i == 30 ? 80f : i == 70 ? 120f : 10f;
                t += ms / 1000.0;
                frames.Add(new FrameSample(t, ms, i % 2 == 0 ? 6f : -1f, 8f));
            }
            var s = FrameStats.Summarize(frames);
            Assert.That(s.Frames, Is.EqualTo(100));
            Assert.That(s.P50Ms, Is.EqualTo(10f));
            Assert.That(s.P95Ms, Is.EqualTo(10f));
            Assert.That(s.P99Ms, Is.EqualTo(80f));
            Assert.That(s.MaxMs, Is.EqualTo(120f));
            Assert.That(s.AvgMs, Is.EqualTo((98 * 10f + 200f) / 100f).Within(1e-4));
            Assert.That(s.Hitches.Count, Is.EqualTo(2));
            Assert.That(s.Hitches[0].ms, Is.EqualTo(80f));
            Assert.That(s.Hitches[1].time, Is.GreaterThan(s.Hitches[0].time));
            Assert.That(s.CpuAvgMs, Is.EqualTo(6f).Within(1e-4), "frames without a CPU time are left out");
            Assert.That(s.GpuAvgMs, Is.EqualTo(8f).Within(1e-4));
            Assert.That(s.Seconds, Is.EqualTo(t).Within(1e-6));
        }

        [Test]
        public void Summary_Empty_IsZero()
        {
            var s = FrameStats.Summarize(new List<FrameSample>());
            Assert.That(s.Frames, Is.EqualTo(0));
            Assert.That(s.CpuAvgMs, Is.LessThan(0));
            Assert.That(PerfText.Report(s, new System.DateTime(2026, 10, 21, 12, 0, 0), null), Does.Contain("frames 0"));
        }

        [Test]
        public void OverlayState_OptionCommandHotkey()
        {
            var st = new FpsOverlayState();
            Assert.That(st.Visible, Is.False);
            Assert.That(st.Hotkey(), Is.False, "Ctrl+F does nothing while neither the option nor the command is on");
            st.Option = true;
            Assert.That(st.Visible, Is.True);
            Assert.That(st.Hotkey(), Is.True);
            Assert.That(st.Visible, Is.False, "Ctrl+F hides");
            st.Hotkey();
            Assert.That(st.Visible, Is.True);
            Assert.That(st.SetCommand(false), Is.False, "fps off hides even with the option on");
            Assert.That(st.Armed, Is.True);
            st.Option = false;
            Assert.That(st.Armed, Is.False);
            Assert.That(st.SetCommand(true), Is.True);
            Assert.That(st.Armed, Is.True);
            Assert.That(st.SetCommand(null), Is.False, "fps toggles");
            Assert.That(st.SetCommand(null), Is.True);
        }

        [Test]
        public void Texts()
        {
            Assert.That(PerfText.RemasterLine(false, false, false, false, false, false, false, false, false, false), Is.EqualTo("RM off"));
            Assert.That(PerfText.RemasterLine(true, true, true, true, true, true, true, true, true, true), Is.EqualTo("RM Li Gd Gr Rk Bd Tr Ch +Sn Rl Mo"));
            Assert.That(PerfText.RemasterLine(false, false, false, false, false, false, false, true, false, false), Is.EqualTo("RM +Sn"));
            var w = new FrameWindowStats { Frames = 10, AvgMs = 6.94f, Low1Ms = 11.26f, MaxMs = 15f };
            Assert.That(PerfText.Overlay(143.6f, w, 5.1f, -1f, "RM off"), Is.EqualTo("144 FPS\nms 6.9 · 1% 11.3 · max 15.0\nCPU 5.1 · GPU –\nRM off"));
            Assert.That(PerfText.FileName(new System.DateTime(2026, 10, 21, 9, 5, 7)), Is.EqualTo("perf_20261021_090507.txt"));
            var s = new PerfSummary { Frames = 3, HitchThresholdMs = 50 };
            s.Hitches.Add((1.5, 70f));
            var text = PerfText.Report(s, new System.DateTime(2026, 10, 21, 9, 5, 7),
                new[] { new KeyValuePair<string, string>("cell", "Cell_10_12") });
            Assert.That(text, Does.Contain("t+1.500 s  70.0 ms"));
            Assert.That(text, Does.Contain("cell: Cell_10_12"));
            Assert.That(text, Does.Contain("hitches > 50 ms: 1"));
        }

        [Test]
        public void PortOption_DefaultOff_RowAboveForgiveHits()
        {
            var port = new PlayerOptions().Port;
            Assert.That(port.ShowFps, Is.False);
            Assert.That(PortOptions.RowText(PortOptions.Row.ShowFps, "Russian"), Is.EqualTo("Показывать FPS"));
            Assert.That(PortOptions.RowText(PortOptions.Row.ShowFps, "English"), Is.EqualTo("Show FPS"));
            Assert.That(PortOptions.ValueText(PortOptions.Row.ShowFps, false, "English"), Is.EqualTo("No (original)"));
            Assert.That(Myrtana.Ui.Skin.VideoPortRowsLayout.ShowFpsTop, Is.EqualTo(168f));
            Assert.That(Myrtana.Ui.Skin.VideoPortRowsLayout.RowBottom(Myrtana.Ui.Skin.VideoPortRowsLayout.ShowFpsTop),
                        Is.LessThanOrEqualTo(Myrtana.Ui.Skin.VideoPortRowsLayout.ForgiveHitsTop + Myrtana.Ui.Skin.VideoPortRowsLayout.SliderOffset));
        }

        [Test]
        public void Ops_FpsAndPerfReport_Bind()
        {
            Assert.That(ScenarioOps.TryGet("fps", out _), Is.True);
            var errors = new List<string>();
            var step = ScenarioOps.Bind("perfreport", new[] { "10" }, null, "perfreport 10", errors);
            Assert.That(errors, Is.Empty);
            Assert.That(step.Float("secs", 30f), Is.EqualTo(10f));
        }
    }
}
