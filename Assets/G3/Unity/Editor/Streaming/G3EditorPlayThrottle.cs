using System;
using System.Diagnostics;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace G3.Unity.Editor
{
    /// <summary>
    /// perf-6 (Docs/performance.md, "Fire Clan: the editor shares the GPU"): while a built game (G3WorldPlayer.exe) runs on
    /// this PC and the editor is not the active app, an editor Play session renders at <see cref="EditorPlayThrottle.ThrottledFps"/>
    /// instead of full rate, so agents' editor sessions stop halving the owner's GPU. A measurement (<see cref="G3PerfRide"/>,
    /// <see cref="G3PerfHere"/>) is never throttled. <c>EditorPrefs G3.EditorPlayThrottle.Off = true</c> switches it off.
    /// </summary>
    [InitializeOnLoad]
    static class G3EditorPlayThrottleDriver
    {
        const string OffPref = "G3.EditorPlayThrottle.Off";
        const double CheckSeconds = 2.0;

        static double _nextCheck;
        static bool _playerRunning;
        static bool _applied;
        static int _savedTarget;

        /// <summary>For scripts: true while the cap is in force.</summary>
        public static bool Throttled => _applied;

        static G3EditorPlayThrottleDriver()
        {
            EditorApplication.update += Update;
            EditorApplication.playModeStateChanged += s =>
            {
                if (s == PlayModeStateChange.ExitingPlayMode) Restore();
            };
        }

        static void Update()
        {
            if (!EditorApplication.isPlaying) { _applied = false; return; }
            double now = EditorApplication.timeSinceStartup;
            if (now >= _nextCheck)
            {
                _nextCheck = now + CheckSeconds;
                _playerRunning = PlayerRunning();
            }
            bool measuring = G3PerfRide.Running || G3PerfHere.Running;
            var cap = EditorPlayThrottle.Cap(_playerRunning, InternalEditorUtility.isApplicationActive, measuring,
                EditorPrefs.GetBool(OffPref, false));
            if (cap.HasValue)
            {
                if (!_applied)
                {
                    _savedTarget = Application.targetFrameRate;
                    _applied = true;
                    UnityEngine.Debug.Log($"[G3EditorPlayThrottle] a game player runs and the editor is in the background: Play capped to {cap.Value} fps");
                }
                if (Application.targetFrameRate != cap.Value) Application.targetFrameRate = cap.Value;
            }
            else Restore();
        }

        static void Restore()
        {
            if (!_applied) return;
            _applied = false;
            Application.targetFrameRate = _savedTarget;
        }

        static bool PlayerRunning()
        {
            try
            {
                var ps = Process.GetProcessesByName("G3WorldPlayer");
                bool any = ps.Length > 0;
                foreach (var p in ps) p.Dispose();
                return any;
            }
            catch (Exception) { return false; }
        }
    }
}
