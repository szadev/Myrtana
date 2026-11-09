using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Myrtana.Unity
{
    /// <summary>
    /// Dev log of the hero's melee input (console <c>combatlog on|off</c>): every click and release with its time, whether
    /// the move started, was queued (and what it replaced) or was refused and why, when each move and its swing started and
    /// ended. Lines go to the Unity log ("[combatlog]"), to <c>combatlog.txt</c> in the persistent data folder (the file the
    /// owner sends) and to a small overlay at the bottom left. Off by default; free when off.
    /// </summary>
    public static class CombatInputLog
    {
        const int OverlayLines = 14;
        static readonly List<string> s_lines = new List<string>();
        static StreamWriter s_file;
        static CombatInputLogOverlay s_overlay;
        static double s_lastClick = -1, s_lastStart = -1;

        public static bool Enabled { get; private set; }
        public static string FilePath => Path.Combine(Application.persistentDataPath, "combatlog.txt");
        internal static IReadOnlyList<string> Lines => s_lines;

        /// <summary>Turns the log on or off; returns a status line for the console.</summary>
        public static string Set(bool on)
        {
            if (on == Enabled) return on ? $"combatlog already on: {FilePath}" : "combatlog already off";
            Enabled = on;
            if (on)
            {
                try
                {
                    s_file = new StreamWriter(FilePath, append: true) { AutoFlush = true };
                    s_file.WriteLine(FormattableString.Invariant($"=== combatlog {DateTime.Now:yyyy-MM-dd HH:mm:ss}, {Application.version}, target {Application.targetFrameRate} fps"));
                }
                catch (Exception e)
                {
                    s_file = null;
                    Debug.LogWarning($"[combatlog] no file: {e.Message}");
                }
                if (s_overlay == null)
                {
                    var go = new GameObject("[CombatInputLog]") { hideFlags = HideFlags.DontSave };
                    UnityEngine.Object.DontDestroyOnLoad(go);
                    s_overlay = go.AddComponent<CombatInputLogOverlay>();
                }
                s_lastClick = s_lastStart = -1;
                return $"combatlog on: {FilePath}";
            }
            s_file?.Dispose();
            s_file = null;
            if (s_overlay != null) UnityEngine.Object.Destroy(s_overlay.gameObject);
            s_overlay = null;
            s_lines.Clear();
            return "combatlog off";
        }

        /// <summary>A primary/secondary press or release.</summary>
        public static void Click(string what)
        {
            if (!Enabled) return;
            double now = Time.timeAsDouble;
            if (what.EndsWith("down", StringComparison.Ordinal)) s_lastClick = now;
            Write($"{what}");
        }

        /// <summary>A move started on the body (the next combo step).</summary>
        public static void MoveStarted(string action, int pose, string shape, bool chained)
        {
            if (!Enabled) return;
            double now = Time.timeAsDouble;
            string sinceClick = s_lastClick >= 0 ? $", {Ms(now - s_lastClick)} after the last press" : "";
            string sinceStart = s_lastStart >= 0 && chained ? $", {Ms(now - s_lastStart)} after the previous start" : "";
            s_lastStart = now;
            Write($"START {action} P{pose} [{shape}]{(chained ? " chained" : "")}{sinceClick}{sinceStart}");
        }

        public static void Line(string text)
        {
            if (Enabled) Write(text);
        }

        static string Ms(double seconds) => FormattableString.Invariant($"{seconds * 1000:0} ms");

        static void Write(string text)
        {
            // Invariant culture: the editor thread runs in the OS culture (ru: "204,262"), mixed with invariant callers (editor-runner-20).
            string line = FormattableString.Invariant($"{Time.timeAsDouble,9:0.000} f{Time.frameCount} dt{Time.deltaTime * 1000:0}  ") + text;
            s_lines.Add(line);
            while (s_lines.Count > OverlayLines) s_lines.RemoveAt(0);
            Debug.Log("[combatlog] " + line);
            try { s_file?.WriteLine(line); } catch (Exception) { }
        }
    }

    /// <summary>The overlay of <see cref="CombatInputLog"/> (IMGUI, bottom left).</summary>
    sealed class CombatInputLogOverlay : MonoBehaviour
    {
        GUIStyle _style;

        void OnGUI()
        {
            var lines = CombatInputLog.Lines;
            if (lines.Count == 0) return;
            _style ??= new GUIStyle(GUI.skin.label) { fontSize = 12, wordWrap = false };
            float h = 16f, w = Mathf.Min(Screen.width - 16, 900);
            float y = Screen.height - 8 - h * lines.Count;
            GUI.Box(new Rect(4, y - 4, w + 8, h * lines.Count + 8), GUIContent.none);
            for (int i = 0; i < lines.Count; i++) GUI.Label(new Rect(8, y + i * h, w, h), lines[i], _style);
        }
    }
}
