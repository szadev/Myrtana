using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Unity.Profiling;
using UnityEngine;

namespace G3.Unity
{
    /// <summary>
    /// fix-171: the in-game A/B of the remaster's lighting features where the player stands (Docs/performance.md,
    /// "Lighting cost in the player"). The owner's player measures itself: started with <c>-g3perfhere [rounds]</c> (or env
    /// <c>G3_PERF_HERE=rounds</c>), it waits until a game is being played (<see cref="GameReady"/>, set by the game layer:
    /// a save loaded, no loading screen, no menu) and the view has been still for a few seconds, turns VSync and the frame
    /// cap off, then alternates short windows: the current state, one feature off, the current state, the next feature off ...
    /// Each feature's cost = its window against the mean of the two neighbouring base windows (frame, main thread, render
    /// thread, GPU, draw calls, shadow casters). The table goes to <c>perf_here.txt</c> in the player's data folder
    /// (LocalLow/G3World/G3World, next to Player.log; editor: Logs/) and to the log. Prefs are never written: every switch
    /// is flipped with persist=false and put back. Editor / runner: <see cref="Run"/> in Play mode.
    /// </summary>
    public sealed class G3PerfHere : MonoBehaviour
    {
        /// <summary>True while the game is played (set by the game layer); null = always (editor scripts).</summary>
        public static Func<bool> GameReady;
        public static bool Running { get; private set; }
        /// <summary>A line for the player (the game layer shows it as a HUD message): the run starts / is done.</summary>
        public static Action<string> Notice;
        public static string LastReport { get; private set; } = "";

        public int rounds = 1;
        public float settle = 1.5f, window = 3f;
        /// <summary>Seconds the game must have been ready (streaming, NPC spawns after a load) before the first window.</summary>
        public float warmup = 20f;
        public bool includeAllOff = true;

        static string ReportPath => Application.isEditor
            ? Path.GetFullPath(Path.Combine(Application.dataPath, "../Logs/perf_here.txt"))
            : Path.Combine(Application.persistentDataPath, "perf_here.txt");

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AutoStart()
        {
            if (Application.isEditor) return;
            var args = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(args, "-g3perfhere");
            string env = Environment.GetEnvironmentVariable("G3_PERF_HERE");
            if (i < 0 && string.IsNullOrEmpty(env)) return;
            int r = 1;
            if (i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out int a)) r = a;
            else if (!string.IsNullOrEmpty(env) && int.TryParse(env, out int b)) r = b;
            Application.runInBackground = true;
            var p = new GameObject("G3PerfHere").AddComponent<G3PerfHere>();
            DontDestroyOnLoad(p.gameObject);
            p.rounds = Mathf.Clamp(r, 1, 5);
            Debug.Log($"[G3PerfHere] armed: {p.rounds} round(s) once a game is played; report {ReportPath}");
        }

        /// <summary>Starts the A/B at the current view (Play mode). Returns at once; poll <see cref="Running"/> / <see cref="LastReport"/>.</summary>
        public static string Run(int rounds = 1, float window = 3f, float settle = 1.5f, float warmup = 0f, bool allOff = true)
        {
            if (!Application.isPlaying) return "not playing";
            if (Running) return "already running";
            var p = new GameObject("G3PerfHere").AddComponent<G3PerfHere>();
            p.rounds = Mathf.Clamp(rounds, 1, 10); p.window = window; p.settle = settle; p.warmup = warmup; p.includeAllOff = allOff;
            return "started";
        }

        sealed class Variant
        {
            public string name;
            public Action off, on;
            public float settle;   // extra settle (s) after off and after on
        }

        sealed class Win
        {
            public double dt, main, render, gpu, draws, setpass, casters, tris;
            public int frames;
        }

        void Start() => StartCoroutine(Main());

        static bool Ru => Application.systemLanguage == SystemLanguage.Russian;

        static void Say(string text)
        {
            Debug.Log("[G3PerfHere] " + text);
            try { Notice?.Invoke(text); } catch (Exception) { }
        }

        static bool Ready()
        {
            var cam = Camera.main;
            if (cam == null || cam.cullingMask == 0) return false;
            try { return GameReady == null || GameReady(); } catch (Exception) { return false; }
        }

        IEnumerator Main()
        {
            Running = true;
            // a game being played, continuously for `warmup` seconds
            float readySince = -1;
            while (true)
            {
                if (Ready()) { if (readySince < 0) readySince = Time.realtimeSinceStartup; }
                else readySince = -1;
                if (readySince >= 0 && Time.realtimeSinceStartup - readySince >= warmup) break;
                yield return new WaitForSecondsRealtime(0.25f);
            }
            var vs = QualitySettings.vSyncCount; var fps = Application.targetFrameRate;
            Say(Ru ? "Замер производительности ремастера: ~4 минуты не трогайте мышь и клавиатуру (картинка будет меняться)"
                   : "Remaster performance probe: ~4 minutes, do not touch mouse or keyboard (the picture will change)");
            QualitySettings.vSyncCount = 0; Application.targetFrameRate = -1;
            var cam = Camera.main;
            var pos = cam != null ? cam.transform.position : Vector3.zero;
            var variants = Variants();
            var sb = new StringBuilder();
            var ic = CultureInfo.InvariantCulture;
            sb.AppendLine($"=== G3PerfHere {DateTime.Now:yyyy-MM-dd HH:mm:ss} {(Application.isEditor ? "editor" : "player")} {Screen.width}x{Screen.height} " +
                          $"q={QualitySettings.names[QualitySettings.GetQualityLevel()]} vsync was {vs} at ({pos.x:F0}, {pos.y:F0}, {pos.z:F0}); rounds {rounds}, windows {settle:0.#}+{window:0.#} s");
            sb.AppendLine("parts: " + WorldRemasterParts.Describe(G3WorldRemaster.Parts) + (G3WorldGi.ModernOption ? " + modern" : "") +
                          " | lighting quality " + LightingQualityLevels.Describe(WorldRemasterLook.Current, G3WorldRemasterLook.Quality) + " (" + G3WorldRemasterLook.QualitySource + ")");
            sb.AppendLine(G3WorldV2.Describe());
            var bases = new List<Win>();
            var results = new Dictionary<string, List<(Win before, Win v, Win after)>>();
            Win last = null;
            for (int r = 0; r < rounds; r++)
            {
                if (last == null) { last = new Win(); yield return Sample(last, settle); bases.Add(last); }
                else if (includeAllOff) { last = new Win(); yield return Sample(last, 30f); bases.Add(last); }   // after the whole remaster came back
                foreach (var v in variants)
                {
                    bool ok = true;
                    try { v.off(); } catch (Exception e) { Debug.LogWarning($"[G3PerfHere] {v.name} off: {e.Message}"); ok = false; }
                    var w = new Win();
                    yield return Sample(w, settle + v.settle);
                    try { v.on(); } catch (Exception e) { Debug.LogWarning($"[G3PerfHere] {v.name} on: {e.Message}"); ok = false; }
                    var after = new Win();
                    yield return Sample(after, settle + v.settle);
                    bases.Add(after);
                    if (ok)
                    {
                        if (!results.TryGetValue(v.name, out var l)) results[v.name] = l = new List<(Win, Win, Win)>();
                        // a variant with a long settle (the whole remaster: material waves, the relief rebuilding every cell) is
                        // compared with the window before it only: the one after still pays for the rebuild
                        l.Add((last, w, v.settle >= 10 ? last : after));
                    }
                    last = after;
                }
            }
            QualitySettings.vSyncCount = vs; Application.targetFrameRate = fps;

            var b0 = Median(bases);
            sb.AppendLine(string.Format(ic, "base (as set): frame {0:F2} ms ({1:F0} fps) | main {2:F2} render {3:F2} gpu {4:F2} | draws {5:F0} setpass {6:F0} shadow casters {7:F0} tris {8:F2}M | {9} windows",
                b0.dt, 1000 / Math.Max(0.01, b0.dt), b0.main, b0.render, b0.gpu, b0.draws, b0.setpass, b0.casters, b0.tris / 1e6, bases.Count));
            sb.AppendLine("cost of each feature (base minus the window with it off; + = it costs that much):");
            sb.AppendLine(string.Format(ic, "  {0,-58} {1,8} {2,8} {3,8} {4,8} {5,8} {6,8}", "feature", "frame", "main", "render", "gpu", "draws", "casters"));
            var rows = new List<(double cost, string line)>();
            foreach (var kv in results)
            {
                var d = new List<Win>();
                foreach (var (before, v, after) in kv.Value)
                    d.Add(new Win
                    {
                        dt = (before.dt + after.dt) / 2 - v.dt, main = (before.main + after.main) / 2 - v.main,
                        render = (before.render + after.render) / 2 - v.render, gpu = (before.gpu + after.gpu) / 2 - v.gpu,
                        draws = (before.draws + after.draws) / 2 - v.draws, casters = (before.casters + after.casters) / 2 - v.casters,
                    });
                var m = Median(d);
                rows.Add((m.dt, string.Format(ic, "  {0,-58} {1,8:+0.00;-0.00} {2,8:+0.00;-0.00} {3,8:+0.00;-0.00} {4,8:+0.00;-0.00} {5,8:+0;-0} {6,8:+0;-0}",
                    kv.Key, m.dt, m.main, m.render, m.gpu, m.draws, m.casters)));
            }
            rows.Sort((x, y) => y.cost.CompareTo(x.cost));
            foreach (var (_, line) in rows) sb.AppendLine(line);
            LastReport = sb.ToString();
            try { File.AppendAllText(ReportPath, LastReport + Environment.NewLine); } catch (Exception e) { Debug.LogWarning("[G3PerfHere] " + e.Message); }
            Debug.Log("[G3PerfHere] report (" + ReportPath + ")\n" + LastReport);
            Say(Ru ? "Замер готов: " + ReportPath : "Probe done: " + ReportPath);
            Running = false;
            Destroy(gameObject);
        }

        static Win Median(List<Win> l)
        {
            double M(Func<Win, double> f) { var a = new List<double>(); foreach (var w in l) a.Add(f(w)); a.Sort(); return a.Count == 0 ? 0 : a[a.Count / 2]; }
            return new Win
            {
                dt = M(w => w.dt), main = M(w => w.main), render = M(w => w.render), gpu = M(w => w.gpu),
                draws = M(w => w.draws), setpass = M(w => w.setpass), casters = M(w => w.casters), tris = M(w => w.tris),
            };
        }

        static ProfilerRecorder Rec(ProfilerCategory c, string name) => ProfilerRecorder.StartNew(c, name, 1);

        /// <summary>Waits <paramref name="settleSeconds"/>, then medians over <see cref="window"/> seconds.</summary>
        IEnumerator Sample(Win w, float settleSeconds)
        {
            float t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < settleSeconds) yield return null;
            using var draws = Rec(ProfilerCategory.Render, "Draw Calls Count");
            using var setpass = Rec(ProfilerCategory.Render, "SetPass Calls Count");
            using var casters = Rec(ProfilerCategory.Render, "Shadow Casters Count");
            using var tris = Rec(ProfilerCategory.Render, "Triangles Count");
            using var mainRec = Rec(ProfilerCategory.Internal, "Main Thread");
            var dt = new List<double>(); var main = new List<double>(); var render = new List<double>(); var gpu = new List<double>();
            var dc = new List<double>(); var sp = new List<double>(); var sc = new List<double>(); var tr = new List<double>();
            var ft = new FrameTiming[1];
            yield return null;
            t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < window)
            {
                yield return null;
                dt.Add(Time.unscaledDeltaTime * 1000.0);
                FrameTimingManager.CaptureFrameTimings();
                if (FrameTimingManager.GetLatestTimings(1, ft) > 0)
                {
                    if (ft[0].cpuMainThreadFrameTime > 0) main.Add(ft[0].cpuMainThreadFrameTime);
                    if (ft[0].cpuRenderThreadFrameTime > 0) render.Add(ft[0].cpuRenderThreadFrameTime);
                    if (ft[0].gpuFrameTime > 0) gpu.Add(ft[0].gpuFrameTime);
                }
                else if (mainRec.Valid) main.Add(mainRec.LastValue * 1e-6);
                dc.Add(draws.LastValue); sp.Add(setpass.LastValue); sc.Add(casters.LastValue); tr.Add(tris.LastValue);
            }
            double Med(List<double> l) { if (l.Count == 0) return -1; l.Sort(); return l[l.Count / 2]; }
            w.dt = Med(dt); w.main = Med(main); w.render = Med(render); w.gpu = Med(gpu);
            w.draws = Med(dc); w.setpass = Med(sp); w.casters = Med(sc); w.tris = Med(tr); w.frames = dt.Count;
        }

        /// <summary>
        /// The switches measured, in this order: every lighting feature that is on now (off, then back on), the whole
        /// Lighting part, Modern lighting, and last the whole remaster (every world part + characters; long settle: the
        /// material waves and the relief rebuild).
        /// </summary>
        List<Variant> Variants()
        {
            var l = new List<Variant>();
            // mt-1: the -g3mt job paths (Docs/multithreading-plan.md): each finished bit off = the old managed path
            int mt = G3Mt.Mask;
            foreach (var (bit, label) in new[] { (MtBits.BodySnapshot, "body snapshot job (-g3mt 1)"), (MtBits.TierJob, "update-LOD tier job (-g3mt 2)"), (MtBits.EvadeGrid, "walker evasion grid (-g3mt 4)") })
            {
                if ((mt & bit) == 0) continue;
                int b = bit;
                l.Add(new Variant { name = "mt: " + label, off = () => G3Mt.Mask = mt & ~b, on = () => G3Mt.Mask = mt });
            }
            if ((mt & MtBits.Default) != 0)
                l.Add(new Variant { name = "mt: all job paths (-g3mt 0)", off = () => G3Mt.Mask = mt & ~MtBits.Default, on = () => G3Mt.Mask = mt });
            var c = WorldRemasterLook.Current;
            bool lit = (G3WorldRemaster.Parts & WorldParts.Lighting) != 0;
            // fix-171: every other lighting quality level against the current one (+ = the current level costs more)
            var q0 = G3WorldRemasterLook.Quality;
            if (lit)
                foreach (LightingQuality q in Enum.GetValues(typeof(LightingQuality)))
                {
                    if (q == q0) continue;
                    var qq = q;
                    l.Add(new Variant
                    {
                        name = $"lighting quality {q0} instead of {qq}",
                        off = () => G3WorldRemasterLook.SetQuality(qq, false),
                        on = () => G3WorldRemasterLook.SetQuality(q0, false),
                        settle = 2f,
                    });
                }
            c = WorldRemasterLook.Current;
            void Flag(string name, Func<bool> get, Action<bool> set)
            {
                if (!lit || !get()) return;
                l.Add(new Variant { name = name, off = () => { set(false); G3WorldRemasterLook.ReapplyLighting(); }, on = () => { set(true); G3WorldRemasterLook.ReapplyLighting(); } });
            }
            if (lit && G3WorldGi.ModernOption && WorldGi.Wanted)
                l.Add(new Variant { name = "modern lighting (GI volume + probes)", off = () => G3WorldGi.SetModern(false, false), on = () => G3WorldGi.SetModern(true, false), settle = 0.5f });
            Flag($"v2 shadows ({c.v2ShadowDistance:0} m, {c.v2ShadowCascades} cascades, {c.v2ShadowResolution}) -> asset's", () => c.v2Shadows, v => c.v2Shadows = v);
            Flag("sky occlusion map + its bounce", () => c.v2SkyOcclusion, v => c.v2SkyOcclusion = v);
            Flag("contact shadows", () => c.v2Contact, v => c.v2Contact = v);
            float bounce = c.v2Bounce;
            Flag("one-bounce colour", () => c.v2Bounce > 0, v => c.v2Bounce = v ? bounce : 0);
            bool fog = c.v2Fog, shafts = c.v2Shafts;
            Flag("height fog + sun shafts (volumetric)", () => c.v2Fog || c.v2Shafts, v => { c.v2Fog = v && fog; c.v2Shafts = v && shafts; });
            Flag("volumetric clouds (sky)", () => c.v2Clouds, v => c.v2Clouds = v);
            Flag("sky reflection cube", () => c.v2SkyReflection, v => c.v2SkyReflection = v);
            Flag("cloud shadow map", () => c.v2CloudShadows, v => c.v2CloudShadows = v);
            int taa = c.v2Taa;
            Flag("remaster TAA/STP + motion vectors", () => c.v2Taa > 0, v => c.v2Taa = v ? taa : 0);
            bool ssao = c.ssao, ssao2 = c.v2Ssao;
            Flag("SSAO boost", () => c.ssao || c.v2Ssao, v => { c.ssao = v && ssao; c.v2Ssao = v && ssao2; });
            bool aerial = c.aerial, clarity = c.clarity, grade = c.grade, shoulder = c.v2Shoulder;
            Flag("aerial + clarity + grade + shoulder passes", () => c.aerial || c.clarity || c.grade || c.v2Shoulder,
                 v => { c.aerial = v && aerial; c.clarity = v && clarity; c.grade = v && grade; c.v2Shoulder = v && shoulder; });
            l.Add(new Variant
            {
                name = lit ? "Lighting part (all of it)" : "Lighting part (turned ON: negative = its cost)",
                off = () => G3WorldRemaster.SetPart(RemasterFeature.Lighting, !lit, false),
                on = () => G3WorldRemaster.SetPart(RemasterFeature.Lighting, lit, false),
                settle = 0.5f,
            });
            if (includeAllOff && G3WorldRemaster.Parts != WorldParts.None)
            {
                var parts = new List<RemasterFeature>();
                foreach (var f in RemasterOptions.WorldFeatures) if (G3WorldRemaster.IsOn(f)) parts.Add(f);
                bool chars = G3CharacterRemaster.Enabled;
                l.Add(new Variant
                {
                    name = "whole remaster (world parts on now + characters)",
                    off = () => { foreach (var f in parts) G3WorldRemaster.SetPart(f, false, false); if (chars) G3CharacterRemaster.SetEnabled(false, false); },
                    on = () => { foreach (var f in parts) G3WorldRemaster.SetPart(f, true, false); if (chars) G3CharacterRemaster.SetEnabled(true, false); },
                    settle = 25f,
                });
            }
            return l;
        }
    }
}
