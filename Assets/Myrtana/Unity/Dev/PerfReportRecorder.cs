#if UNITY_EDITOR || DEVELOPMENT_BUILD || G3_DEMO
using System;
using System.Collections.Generic;
using System.IO;
using Myrtana.Session;
using UnityEngine;

namespace Myrtana.Unity.Dev
{
    /// <summary>
    /// fix-172 <c>perfreport [secs=30]</c>: records every frame's time (unscaled delta) and the FrameTimingManager CPU main
    /// thread / GPU ms for <c>secs</c> seconds after a short delay (to close the console), then writes the summary
    /// (<see cref="FrameStats.Summarize"/>, <see cref="PerfText.Report"/>) to Player.log and to
    /// <c>Application.persistentDataPath/perf_yyyyMMdd_HHmmss.txt</c> (LocalLow/G3World/G3World on Windows). The context
    /// lines (scene, cell, position, quality, remaster) are taken when the recording ends.
    /// </summary>
    public sealed class PerfReportRecorder : MonoBehaviour
    {
        public static PerfReportRecorder Current { get; private set; }
        public static string LastPath { get; private set; }
        public static string LastSummary { get; private set; }

        readonly List<FrameSample> _frames = new List<FrameSample>(8192);
        float _seconds, _delay;
        double _start = -1, _armedAt;
        DateTime _startedAt;
        Func<List<KeyValuePair<string, string>>> _context;
        Action<string> _done;

        public bool Recording => _start >= 0;
        public float Remaining => _start < 0 ? _seconds + Mathf.Max(0f, _delay - (float)(Time.realtimeSinceStartupAsDouble - _armedAt))
                                             : Mathf.Max(0f, _seconds - (float)(Time.realtimeSinceStartupAsDouble - _start));

        /// <summary>Starts a recording (one at a time); <paramref name="done"/> gets the short result line.</summary>
        public static string Begin(float seconds, float delay, Func<List<KeyValuePair<string, string>>> context, Action<string> done)
        {
            if (Current != null) return $"perfreport: already recording ({Current.Remaining:0} s left)";
            var go = new GameObject("[PerfReport]") { hideFlags = HideFlags.DontSave };
            DontDestroyOnLoad(go);
            var r = go.AddComponent<PerfReportRecorder>();
            r._seconds = Mathf.Clamp(seconds, 1f, 600f);
            r._delay = Mathf.Clamp(delay, 0f, 30f);
            r._armedAt = Time.realtimeSinceStartupAsDouble;
            r._context = context;
            r._done = done;
            Current = r;
            return $"perfreport: recording {r._seconds:0} s in {r._delay:0.#} s (close the console)";
        }

        void Update()
        {
            double now = Time.realtimeSinceStartupAsDouble;
            if (_start < 0)
            {
                if (now - _armedAt < _delay) return;
                _start = now;
                _startedAt = DateTime.Now;
                return; // the first frame's delta still belongs to the delay
            }
            FpsOverlay.LatestTiming(out float cpu, out float gpu);
            _frames.Add(new FrameSample(now - _start, Time.unscaledDeltaTime * 1000f, cpu, gpu));
            if (now - _start >= _seconds) Finish();
        }

        void Finish()
        {
            string line;
            try
            {
                var summary = FrameStats.Summarize(_frames);
                List<KeyValuePair<string, string>> context = null;
                try { context = _context?.Invoke(); }
                catch (Exception e) { context = new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>("context error", e.Message) }; }
                string text = PerfText.Report(summary, _startedAt, context);
                string path = Path.Combine(Application.persistentDataPath, PerfText.FileName(_startedAt));
                string written;
                try
                {
                    Directory.CreateDirectory(Application.persistentDataPath);
                    File.WriteAllText(path, text);
                    LastPath = path;
                    written = path;
                }
                catch (Exception e) { written = "not written: " + e.Message; }
                Debug.Log("[PerfReport] " + written + "\n" + text);
                line = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "perfreport: {0} frames, avg {1:0.00} ms ({2:0} fps), p95 {3:0.00}, p99 {4:0.00}, max {5:0.0}, hitches {6} -> {7}",
                    summary.Frames, summary.AvgMs, summary.AvgFps, summary.P95Ms, summary.P99Ms, summary.MaxMs, summary.Hitches.Count, written);
            }
            catch (Exception e)
            {
                line = "perfreport failed: " + e.Message;
                Debug.LogException(e);
            }
            LastSummary = line;
            try { _done?.Invoke(line); } catch (Exception) { }
            Current = null;
            Destroy(gameObject);
        }

        void OnDestroy()
        {
            if (Current == this) Current = null;
        }
    }
}
#endif
