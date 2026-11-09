using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEditor;
using Debug = UnityEngine.Debug;

namespace Myrtana.Unity.Editor
{
    /// <summary>
    /// Fresh-checkout setup: every import and builder step a new clone needs, in dependency order, so a player can go from
    /// "repository + Gothic 3 install + remaster pack" to a built game with one batch-mode call (Tools/Setup/setup.ps1):
    /// <c>Unity.exe -batchmode -projectPath . -executeMethod Myrtana.Unity.Editor.FreshSetup.RunAll [-setupSteps a,b] [-setupFrom step]</c>.
    /// Every step is logged with its time to Logs/fresh-setup.log; a failed step stops the run (exit code 1) unless it is optional.
    /// </summary>
    public static class FreshSetup
    {
        sealed class Step
        {
            public string Name;
            public Func<string> Run;
            public bool Optional;
        }

        static Func<string> Menu(string path) => () =>
        {
            if (!EditorApplication.ExecuteMenuItem(path)) throw new InvalidOperationException("menu item not found: " + path);
            return "menu " + path;
        };

        static List<Step> Steps() => new List<Step>
        {
            new Step { Name = "content",      Run = ContentImportMenu.ImportPack },
            new Step { Name = "voice",        Run = ContentImportMenu.ExtractAllVoice, Optional = true },
            new Step { Name = "gui",          Run = Menu("G3/GUI/Import") },
            new Step { Name = "audio",        Run = Menu("G3/Audio/Import All (music, ambience, emitters, footsteps)") },
            new Step { Name = "audio-combat", Run = Menu("G3/Audio/Import Combat Sounds (hits, creatures, bow, magic)") },
            new Step { Name = "audio-anim",   Run = Menu("G3/Audio/Import Animation Effects (tools, eating, drinking)") },
            new Step { Name = "weather",      Run = Menu("G3/Environment/Import Weather") },
            new Step { Name = "effects",      Run = Menu("G3/Effects/Import Effects") },
            new Step { Name = "speedtrees",   Run = () => G3.Unity.Editor.Trees.G3SpeedTreeImporter.RebuildAll(rebake: true) },
            new Step { Name = "tree-models",  Run = G3.Unity.Editor.Trees.TreeModelSetup.RunAll },
            new Step { Name = "hero",         Run = () => G3.Unity.Editor.Characters.G3HeroImporter.Build(true) },
            new Step { Name = "hero-torch",   Run = Menu("G3/Characters/Add Hero Torch Motions") },
            new Step { Name = "npc-motions",  Run = () => G3.Unity.Editor.Characters.G3NpcMotionImporter.Build() },
            new Step { Name = "face-kits",    Run = () => G3.Unity.Editor.Characters.G3FaceKitImporter.Build() },
            new Step { Name = "npc-prefabs",  Run = () => G3.Unity.Editor.G3NpcImporter.BuildPrefabs() },
            new Step { Name = "items",        Run = Menu("Myrtana/Content/Build item visuals") },
            new Step { Name = "interactives", Run = Menu("Myrtana/Content/Build interactive visuals") },
            new Step { Name = "world",        Run = () => G3.Unity.Editor.G3WorldBatch.ImportAllSync(force: false) },
            new Step { Name = "water",        Run = Menu("G3/World/Import Water Zones") },
            new Step { Name = "colliders",    Run = Menu("G3/World/Rebuild Cell Colliders") },
            new Step { Name = "audio-world",  Run = Menu("G3/Audio/Bake Audio World Data") },
            new Step { Name = "remaster-world",   Run = Menu("G3/Remaster/World/Build Overrides"), Optional = true },
            new Step { Name = "remaster-sand",    Run = Menu("G3/Remaster/World/V2/Import Sand Scans"), Optional = true },
            new Step { Name = "remaster-v2",      Run = Menu("G3/Remaster/World/V2/Build Materials"), Optional = true },
            new Step { Name = "remaster-trees",   Run = Menu("G3/Remaster/World/V2/Build Trees"), Optional = true },
            new Step { Name = "remaster-scatter", Run = Menu("G3/Remaster/World/Build Scatter Set"), Optional = true },
            new Step { Name = "remaster-chars",   Run = Menu("G3/Remaster/Build Runtime Character Set (Options → Remaster)"), Optional = true },
        };

        static string LogFile => Path.Combine(Directory.GetCurrentDirectory(), "Logs", "fresh-setup.log");

        static void Log(string line)
        {
            Debug.Log("[FreshSetup] " + line);
            Directory.CreateDirectory(Path.GetDirectoryName(LogFile));
            File.AppendAllText(LogFile, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + line + Environment.NewLine);
        }

        [MenuItem("Myrtana/Build/Fresh Setup (all import steps)", priority = 99)]
        static void MenuRun() => Debug.Log(Run(null, null));

        public static void RunAll()
        {
            var args = Environment.GetCommandLineArgs();
            string Arg(string n) { int i = Array.IndexOf(args, n); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
            string result = Run(Arg("-setupSteps"), Arg("-setupFrom"));
            Debug.Log(result);
            if (UnityEngine.Application.isBatchMode) EditorApplication.Exit(result.StartsWith("ok", StringComparison.Ordinal) ? 0 : 1);
        }

        /// <param name="only">comma-separated step names (null = all)</param>
        /// <param name="from">start at this step (resume after a failure)</param>
        public static string Run(string only, string from)
        {
            var steps = Steps();
            if (!string.IsNullOrEmpty(from))
            {
                int k = steps.FindIndex(s => s.Name == from);
                if (k < 0) return "error: unknown step " + from + " (steps: " + string.Join(",", steps.Select(s => s.Name)) + ")";
                steps = steps.Skip(k).ToList();
            }
            if (!string.IsNullOrEmpty(only))
            {
                var set = new HashSet<string>(only.Split(',').Select(s => s.Trim()));
                steps = steps.Where(s => set.Contains(s.Name)).ToList();
            }
            var total = Stopwatch.StartNew();
            int warned = 0;
            Log($"start: {steps.Count} steps, Gothic 3 = {G3.Formats.G3Install.DataDir ?? "NOT FOUND"}");
            foreach (var s in steps)
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    // batch mode starts on an untitled scene that earlier steps may dirty; the world import opens cells additively
                    var active = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene();
                    // (an untitled scene, even a fresh empty one, blocks NewScene(Additive): open a saved one)
                    if (string.IsNullOrEmpty(active.path))
                        UnityEditor.SceneManagement.EditorSceneManager.OpenScene("Assets/Scenes/SampleScene.unity",
                            UnityEditor.SceneManagement.OpenSceneMode.Single);
                    // menu items swallow exceptions (the importers log them): count what the step logs
                    int exceptions = 0;
                    string first = null;
                    void OnLog(string msg, string stack, UnityEngine.LogType type)
                    {
                        if (type != UnityEngine.LogType.Exception) return;
                        exceptions++;
                        first ??= msg;
                    }
                    UnityEngine.Application.logMessageReceived += OnLog;
                    string r;
                    try { r = s.Run() ?? ""; }
                    finally { UnityEngine.Application.logMessageReceived -= OnLog; }
                    AssetDatabase.SaveAssets();
                    if (exceptions > 0)
                    {
                        Log($"WARN {s.Name} ({sw.Elapsed:hh\\:mm\\:ss}): {exceptions} exception(s) logged, first: {first}");
                        warned++;
                    }
                    else Log($"ok   {s.Name} ({sw.Elapsed:hh\\:mm\\:ss}): {r.Split('\n')[0]}");
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                    Log($"FAIL {s.Name} ({sw.Elapsed:hh\\:mm\\:ss}): {ex.GetType().Name}: {ex.Message}");
                    if (!s.Optional) return $"error: step {s.Name} failed (resume with -setupFrom {s.Name}); log {LogFile}";
                    warned++;
                }
            }
            AssetDatabase.Refresh();
            return $"ok: {steps.Count} steps in {total.Elapsed:hh\\:mm\\:ss}, {warned} optional failed; log {LogFile}";
        }
    }
}
