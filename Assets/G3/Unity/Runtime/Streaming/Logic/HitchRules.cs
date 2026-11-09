using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace G3.Unity
{
    /// <summary>What a long frame was (fix-251, <see cref="HitchRules"/>).</summary>
    public enum HitchKind
    {
        /// <summary>An ordinary frame.</summary>
        None,
        /// <summary>The game itself took this long (main thread, render, a load …).</summary>
        Hitch,
        /// <summary>The window lost focus during the frame: the player paused (runInBackground off), nothing froze.</summary>
        Background,
    }

    /// <summary>
    /// fix-251 (owner: "the game froze for a while, then came back"; the FPS overlay later showed max 6872.8 ms): the
    /// always-on long-frame log. A frame over <see cref="ThresholdMs"/> gets one line with the wall clock (to match the
    /// owner's report / screenshot), whether the window was in the background (Player Settings runInBackground is off, so
    /// a focus change — a snipping tool, a notification — pauses the game and the next frame carries the whole pause),
    /// the GCs of each generation, managed memory and the marks systems left in that frame (scene loads, saves, asset
    /// clean-ups). Lines are rate-limited so a stall loop cannot flood the log.
    /// </summary>
    public sealed class HitchRules
    {
        /// <summary>Frames longer than this are logged, ms.</summary>
        public const double ThresholdMs = 250;
        /// <summary>At most this many lines per <see cref="WindowSeconds"/>, and <see cref="MaxLines"/> in a run.</summary>
        public const int LinesPerWindow = 4, MaxLines = 400;
        public const double WindowSeconds = 10;

        int _total, _inWindow;
        double _windowStart = double.NegativeInfinity;
        List<string> _marks = new List<string>(8), _previous = new List<string>(8);

        /// <summary>Lines suppressed by the rate limit since the last logged one.</summary>
        public int Suppressed { get; private set; }

        public static HitchKind Classify(double frameMs, bool focusLostDuringFrame)
        {
            if (double.IsNaN(frameMs) || frameMs <= ThresholdMs) return HitchKind.None;
            return focusLostDuringFrame ? HitchKind.Background : HitchKind.Hitch;
        }

        /// <summary>A system notes what it did in this frame (kept until <see cref="EndFrame"/>; a few per frame).</summary>
        public void Mark(string what)
        {
            if (string.IsNullOrEmpty(what) || _marks.Count >= 8) return;
            _marks.Add(what);
        }

        /// <summary>
        /// The marks of the previous and the current frame, joined ("-" when none): a frame's length is only known in the
        /// next frame (delta time), so both are shown.
        /// </summary>
        public string Marks => _previous.Count + _marks.Count == 0 ? "-" : string.Join("; ", _previous.Concat(_marks));

        /// <summary>The frame ends: its marks become the previous frame's.</summary>
        public void EndFrame()
        {
            var t = _previous;
            _previous = _marks;
            _marks = t;
            _marks.Clear();
        }

        /// <summary>True when a line may be written at <paramref name="now"/> (s); counts it.</summary>
        public bool Allow(double now)
        {
            if (_total >= MaxLines) { Suppressed++; return false; }
            if (now - _windowStart >= WindowSeconds) { _windowStart = now; _inWindow = 0; }
            if (_inWindow >= LinesPerWindow) { Suppressed++; return false; }
            _inWindow++;
            _total++;
            return true;
        }

        /// <summary>The log line of a long frame.</summary>
        public string Line(HitchKind kind, double frameMs, string clock, int frame, double backgroundSeconds,
                           int gc0, int gc1, int gc2, double heapMb, string context)
        {
            var sb = new StringBuilder(160);
            sb.Append("[G3Hitch] ").Append(clock).Append(" frame ").Append(frame).Append(": ")
              .Append(frameMs.ToString("0", System.Globalization.CultureInfo.InvariantCulture)).Append(" ms");
            if (kind == HitchKind.Background)
                sb.Append(" - window in the background ")
                  .Append(backgroundSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture))
                  .Append(" s (game paused, not a freeze)");
            sb.Append("; GC ").Append(gc0).Append('/').Append(gc1).Append('/').Append(gc2)
              .Append(", heap ").Append(heapMb.ToString("0", System.Globalization.CultureInfo.InvariantCulture)).Append(" MB");
            sb.Append("; marks ").Append(Marks);
            if (!string.IsNullOrEmpty(context)) sb.Append("; ").Append(context);
            if (Suppressed > 0) { sb.Append(" (").Append(Suppressed).Append(" earlier lines suppressed)"); Suppressed = 0; }
            return sb.ToString();
        }
    }
}
