using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Myrtana.Session
{
    /// <summary>Frame-time figures of a window of frames (ms; 0 when the window is empty).</summary>
    public struct FrameWindowStats
    {
        public int Frames;
        public float AvgMs;
        /// <summary>"1 % low": the mean of the slowest 1 % of the frames (at least the slowest one).</summary>
        public float Low1Ms;
        public float MaxMs;
    }

    /// <summary>
    /// The frame times of the last <see cref="Seconds"/> seconds (fix-172, the FPS overlay): a fixed ring buffer, no
    /// allocation per frame; <see cref="Compute"/> sorts a reused scratch copy (called a few times a second, not per frame).
    /// </summary>
    public sealed class FrameTimeWindow
    {
        readonly double[] _times;
        readonly float[] _ms;
        readonly float[] _scratch;
        int _start, _count;

        public FrameTimeWindow(float seconds = 5f, int capacity = 4096)
        {
            Seconds = Math.Max(0.01f, seconds);
            capacity = Math.Max(16, capacity);
            _times = new double[capacity];
            _ms = new float[capacity];
            _scratch = new float[capacity];
        }

        public float Seconds { get; }
        public int Count => _count;

        public void Clear() { _start = 0; _count = 0; }

        /// <summary>A frame that ended at <paramref name="time"/> (s) and took <paramref name="ms"/>; frames older than the window go.</summary>
        public void Add(double time, float ms)
        {
            if (_count == _ms.Length) { _start = (_start + 1) % _ms.Length; _count--; }
            int i = (_start + _count) % _ms.Length;
            _times[i] = time;
            _ms[i] = ms;
            _count++;
            double oldest = time - Seconds;
            while (_count > 0 && _times[_start] <= oldest) { _start = (_start + 1) % _ms.Length; _count--; }
        }

        public FrameWindowStats Compute()
        {
            var s = new FrameWindowStats { Frames = _count };
            if (_count == 0) return s;
            double sum = 0;
            float max = 0;
            for (int k = 0; k < _count; k++)
            {
                float v = _ms[(_start + k) % _ms.Length];
                _scratch[k] = v;
                sum += v;
                if (v > max) max = v;
            }
            s.AvgMs = (float)(sum / _count);
            s.MaxMs = max;
            s.Low1Ms = FrameStats.SlowestMean(_scratch, _count, 0.01);
            return s;
        }
    }

    /// <summary>Frames per second over windows of <see cref="Window"/> seconds (the overlay's 0.5 s smoothing).</summary>
    public sealed class FpsSmoother
    {
        float _elapsed;
        int _frames;

        public FpsSmoother(float window = 0.5f) { Window = Math.Max(0.05f, window); }

        public float Window { get; }
        /// <summary>The last window's frames per second (0 before the first window closed).</summary>
        public float Fps { get; private set; }

        /// <summary>One frame of <paramref name="dt"/> seconds; true when a window closed and <see cref="Fps"/> is new.</summary>
        public bool Add(float dt)
        {
            if (dt <= 0f) return false;
            _elapsed += dt;
            _frames++;
            if (_elapsed < Window) return false;
            Fps = _frames / _elapsed;
            _elapsed = 0f;
            _frames = 0;
            return true;
        }
    }

    /// <summary>One recorded frame of a perf report (fix-172 <c>perfreport</c>).</summary>
    public struct FrameSample
    {
        public double Time;  // s since the recording started
        public float Ms;
        public float CpuMs;  // main thread (FrameTimingManager), ≤ 0 = unknown
        public float GpuMs;  // ≤ 0 = unknown

        public FrameSample(double time, float ms, float cpuMs = -1f, float gpuMs = -1f)
        {
            Time = time; Ms = ms; CpuMs = cpuMs; GpuMs = gpuMs;
        }
    }

    /// <summary>The summary of a recording: frame-time percentiles, hitches and the mean CPU / GPU times.</summary>
    public sealed class PerfSummary
    {
        public int Frames;
        public double Seconds;
        public float AvgMs, P50Ms, P95Ms, P99Ms, MaxMs, Low1Ms;
        /// <summary>Mean main-thread CPU / GPU ms of the frames that have one (−1: none).</summary>
        public float CpuAvgMs = -1f, CpuP95Ms = -1f, GpuAvgMs = -1f, GpuP95Ms = -1f;
        public float HitchThresholdMs;
        /// <summary>Frames above <see cref="HitchThresholdMs"/>: (time s, ms), in time order.</summary>
        public readonly List<(double time, float ms)> Hitches = new List<(double, float)>();
        public float AvgFps => AvgMs > 0f ? 1000f / AvgMs : 0f;
    }

    /// <summary>Pure frame statistics (fix-172): percentiles, slowest-share means, the perf summary.</summary>
    public static class FrameStats
    {
        public const float DefaultHitchMs = 50f;

        /// <summary>
        /// Nearest-rank percentile (<paramref name="p"/> 0…100) of the first <paramref name="count"/> values of an ascending
        /// <paramref name="sorted"/> array: the smallest value with at least p % of the values at or below it.
        /// </summary>
        public static float Percentile(float[] sorted, int count, double p)
        {
            if (sorted == null || count <= 0) return 0f;
            if (p <= 0) return sorted[0];
            int rank = (int)Math.Ceiling(p / 100.0 * count);
            return sorted[Math.Max(1, Math.Min(count, rank)) - 1];
        }

        /// <summary>
        /// Sorts the first <paramref name="count"/> values ascending (in place) and returns the mean of the slowest
        /// <paramref name="share"/> of them (at least one value).
        /// </summary>
        public static float SlowestMean(float[] values, int count, double share)
        {
            if (values == null || count <= 0) return 0f;
            Array.Sort(values, 0, count);
            int n = Math.Max(1, (int)Math.Ceiling(count * share));
            double sum = 0;
            for (int i = count - n; i < count; i++) sum += values[i];
            return (float)(sum / n);
        }

        public static PerfSummary Summarize(IReadOnlyList<FrameSample> frames, float hitchMs = DefaultHitchMs)
        {
            var s = new PerfSummary { HitchThresholdMs = hitchMs };
            if (frames == null || frames.Count == 0) return s;
            int n = frames.Count;
            var ms = new float[n];
            var cpu = new List<float>(n);
            var gpu = new List<float>(n);
            double sum = 0;
            for (int i = 0; i < n; i++)
            {
                var f = frames[i];
                ms[i] = f.Ms;
                sum += f.Ms;
                if (f.CpuMs > 0f) cpu.Add(f.CpuMs);
                if (f.GpuMs > 0f) gpu.Add(f.GpuMs);
                if (f.Ms > hitchMs) s.Hitches.Add((f.Time, f.Ms));
            }
            s.Frames = n;
            s.Seconds = frames[n - 1].Time - frames[0].Time + frames[0].Ms / 1000.0;
            s.AvgMs = (float)(sum / n);
            s.Low1Ms = SlowestMean(ms, n, 0.01); // sorts ms
            s.P50Ms = Percentile(ms, n, 50);
            s.P95Ms = Percentile(ms, n, 95);
            s.P99Ms = Percentile(ms, n, 99);
            s.MaxMs = ms[n - 1];
            if (cpu.Count > 0) { s.CpuAvgMs = Mean(cpu); s.CpuP95Ms = SortedPercentile(cpu, 95); }
            if (gpu.Count > 0) { s.GpuAvgMs = Mean(gpu); s.GpuP95Ms = SortedPercentile(gpu, 95); }
            return s;
        }

        static float Mean(List<float> l)
        {
            double sum = 0;
            foreach (var v in l) sum += v;
            return (float)(sum / l.Count);
        }

        static float SortedPercentile(List<float> l, double p)
        {
            var a = l.ToArray();
            Array.Sort(a);
            return Percentile(a, a.Length, p);
        }
    }

    /// <summary>
    /// When the FPS overlay shows (fix-172): the port option "Show FPS" or the console's <c>fps on</c> arm it; Ctrl+F then
    /// hides / shows it; <c>fps off</c> hides it (Ctrl+F brings it back while the option is on).
    /// </summary>
    public sealed class FpsOverlayState
    {
        bool _option, _command, _hidden;

        public bool Option
        {
            get => _option;
            set
            {
                if (value && !_option) _hidden = false; // switching the option on shows it
                _option = value;
            }
        }

        public bool Command => _command;
        /// <summary>The Ctrl+F hotkey works (the option or the command is on).</summary>
        public bool Armed => _option || _command;
        public bool Visible => Armed && !_hidden;

        /// <summary>Console <c>fps on|off</c> (null toggles what is shown).</summary>
        public bool SetCommand(bool? on)
        {
            bool want = on ?? !Visible;
            if (want) { _command = true; _hidden = false; }
            else { _command = false; _hidden = true; }
            return Visible;
        }

        /// <summary>Ctrl+F: flips the overlay while armed; false when the key does nothing.</summary>
        public bool Hotkey()
        {
            if (!Armed) return false;
            _hidden = !_hidden;
            return true;
        }
    }

    /// <summary>The texts of the FPS overlay and the perf report (fix-172), invariant culture.</summary>
    public static class PerfText
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>The remaster parts that are on, compact (overlay): "RM Li Gd Gr Rk Bd Tr Ch +Sn Rl Mo" or "RM off".</summary>
        public static string RemasterLine(bool lighting, bool ground, bool grass, bool rocks, bool buildings, bool trees,
                                          bool characters, bool snow, bool relief, bool modern)
        {
            var sb = new StringBuilder("RM");
            int n = sb.Length;
            if (lighting) sb.Append(" Li");
            if (ground) sb.Append(" Gd");
            if (grass) sb.Append(" Gr");
            if (rocks) sb.Append(" Rk");
            if (buildings) sb.Append(" Bd");
            if (trees) sb.Append(" Tr");
            if (characters) sb.Append(" Ch");
            bool any = sb.Length > n;
            bool extra = false;
            void X(bool on, string code)
            {
                if (!on) return;
                sb.Append(extra ? " " : " +").Append(code);
                extra = true;
            }
            X(snow, "Sn");
            X(relief, "Rl");
            X(modern, "Mo");
            return any || extra ? sb.ToString() : "RM off";
        }

        /// <summary>The overlay's lines (fps, frame ms window, CPU / GPU, remaster); cpu / gpu ≤ 0 = unknown.</summary>
        public static string Overlay(float fps, FrameWindowStats w, float cpuMs, float gpuMs, string remaster)
        {
            var sb = new StringBuilder(128);
            sb.Append(fps.ToString("0", Inv)).Append(" FPS\n");
            sb.Append("ms ").Append(w.AvgMs.ToString("0.0", Inv))
              .Append(" · 1% ").Append(w.Low1Ms.ToString("0.0", Inv))
              .Append(" · max ").Append(w.MaxMs.ToString("0.0", Inv)).Append('\n');
            sb.Append("CPU ").Append(Ms(cpuMs)).Append(" · GPU ").Append(Ms(gpuMs));
            if (!string.IsNullOrEmpty(remaster)) sb.Append('\n').Append(remaster);
            return sb.ToString();
        }

        static string Ms(float v) => v > 0f ? v.ToString("0.0", Inv) : "–";

        static string Opt(float v) => v > 0f ? v.ToString("0.00", Inv) : "n/a";

        /// <summary>The perf report file / log text: summary, hitches (at most <paramref name="maxHitches"/>), then the context lines.</summary>
        public static string Report(PerfSummary s, DateTime started, IEnumerable<KeyValuePair<string, string>> context, int maxHitches = 200)
        {
            var sb = new StringBuilder(2048);
            sb.Append("G3World perf report ").Append(started.ToString("yyyy-MM-dd HH:mm:ss", Inv)).Append('\n');
            sb.Append("frames ").Append(s.Frames).Append(" in ").Append(s.Seconds.ToString("0.0", Inv)).Append(" s, avg ")
              .Append(s.AvgFps.ToString("0.0", Inv)).Append(" fps\n");
            sb.Append("frame ms: avg ").Append(s.AvgMs.ToString("0.00", Inv))
              .Append("  p50 ").Append(s.P50Ms.ToString("0.00", Inv))
              .Append("  p95 ").Append(s.P95Ms.ToString("0.00", Inv))
              .Append("  p99 ").Append(s.P99Ms.ToString("0.00", Inv))
              .Append("  1%low ").Append(s.Low1Ms.ToString("0.00", Inv))
              .Append("  max ").Append(s.MaxMs.ToString("0.00", Inv)).Append('\n');
            sb.Append("cpu main ms: avg ").Append(Opt(s.CpuAvgMs)).Append("  p95 ").Append(Opt(s.CpuP95Ms))
              .Append("   gpu ms: avg ").Append(Opt(s.GpuAvgMs)).Append("  p95 ").Append(Opt(s.GpuP95Ms)).Append('\n');
            sb.Append("hitches > ").Append(s.HitchThresholdMs.ToString("0", Inv)).Append(" ms: ").Append(s.Hitches.Count).Append('\n');
            for (int i = 0; i < s.Hitches.Count && i < maxHitches; i++)
                sb.Append("  t+").Append(s.Hitches[i].time.ToString("0.000", Inv)).Append(" s  ")
                  .Append(s.Hitches[i].ms.ToString("0.0", Inv)).Append(" ms\n");
            if (s.Hitches.Count > maxHitches) sb.Append("  … ").Append(s.Hitches.Count - maxHitches).Append(" more\n");
            if (context != null)
                foreach (var kv in context)
                    sb.Append(kv.Key).Append(": ").Append(kv.Value).Append('\n');
            return sb.ToString();
        }

        /// <summary>The report's file name: perf_yyyyMMdd_HHmmss.txt.</summary>
        public static string FileName(DateTime started) => "perf_" + started.ToString("yyyyMMdd_HHmmss", Inv) + ".txt";
    }
}
