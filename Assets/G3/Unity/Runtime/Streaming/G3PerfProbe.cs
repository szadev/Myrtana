using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Unity.Profiling;
using Unity.Profiling.LowLevel.Unsafe;
using UnityEngine;

namespace G3.Unity
{
    /// <summary>
    /// Repeatable performance measurement of the streamed world (Docs/performance.md).
    /// Teleports the player/camera to fixed views (Faring, a forest, Bakaresh), waits for the cells to stream in, then
    /// samples frame time, CPU main/render thread, GPU time and render counters; finally walks a straight path to
    /// measure streaming hitches. Results are appended to Logs/perf_probe.txt (editor) or perf_probe.txt next to the
    /// player executable. Editor: <c>G3PerfProbe.Run()</c> in Play mode. Player: start with <c>-g3perf</c>
    /// (quits when done).
    /// </summary>
    public sealed class G3PerfProbe : MonoBehaviour
    {
        public struct View
        {
            public string name;
            public float x, z, yaw, pitch;
            public View(string n, float x, float z, float yaw, float pitch) { name = n; this.x = x; this.z = z; this.yaw = yaw; this.pitch = pitch; }
        }

        public static readonly View[] DefaultViews =
        {
            new View("Faring", 655, 586, 40, 0),
            new View("Forest (Montera woods)", -125, 125, 200, 0),
            new View("Bakaresh", 389, -978, 30, 0),
        };

        public static string LastResult = "";
        public static bool Running;
        /// <summary>Called when a sample window starts / appended to its line (game layers add their own counters, e.g. the NPC navigation cost).</summary>
        public static Action SampleBegin;
        public static Func<string> SampleExtra;
        public float sampleSeconds = 6f;
        public float settleSeconds = 4f;
        /// <summary>
        /// Before sampling a view, wait (at most this many seconds) until <see cref="G3DeferredActivation"/> has woken every
        /// loaded cell's NPCs and trees: until then each frame registers up to 48 renderers with the GPU Resident Drawer
        /// (~1.5 ms, ProcessMeshRendererChanges) plus the SetActive work, which the r14 capture measured as steady-state
        /// cost (perf-8). The rest is woken without the per-frame caps meanwhile. 0 = do not wait (player switch <c>-g3perfnowake</c>, the pre-perf-8 behaviour).
        /// </summary>
        public float wakeTimeoutSeconds = 60f;
        public bool walk = true;
        public bool quitWhenDone;
        public string label = "";
        public View[] views = DefaultViews;

        static string LogPath => Application.isEditor
            ? Path.GetFullPath(Path.Combine(Application.dataPath, "../Logs/perf_probe.txt"))
            : Path.GetFullPath(Path.Combine(Application.dataPath, "../perf_probe.txt"));

        /// <summary>Starts the probe (Play mode / player). Returns immediately; poll <see cref="Running"/> / <see cref="LastResult"/>.</summary>
        public static string Run(string label = "", bool walk = true, float sampleSeconds = 6f)
        {
            if (!Application.isPlaying) return "not playing";
            if (Running) return "already running";
            var go = new GameObject("G3PerfProbe");
            var p = go.AddComponent<G3PerfProbe>();
            p.label = label;
            p.walk = walk;
            p.sampleSeconds = sampleSeconds;
            return "started";
        }

        /// <summary>Samples at the current camera position only (no teleport/walk).</summary>
        public static string RunHere(string label = "", float sampleSeconds = 3f)
        {
            if (!Application.isPlaying) return "not playing";
            if (Running) return "already running";
            var go = new GameObject("G3PerfProbe");
            var p = go.AddComponent<G3PerfProbe>();
            p.label = label;
            p.walk = false;
            p.here = true;
            p.sampleSeconds = sampleSeconds;
            return "started";
        }

        bool here;
        int gotoOnly = -1;

        /// <summary>Only moves the (frozen) player/camera to <see cref="DefaultViews"/>[index] and waits for streaming (Running → false).</summary>
        public static string Goto(int index)
        {
            if (!Application.isPlaying) return "not playing";
            if (Running) return "already running";
            var p = new GameObject("G3PerfProbe").AddComponent<G3PerfProbe>();
            p.gotoOnly = index;
            return "started";
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AutoStart()
        {
            if (Application.isEditor) return;
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "-g3perf") < 0) return;
            Application.runInBackground = true;   // keep measuring if the window loses focus
            var go = new GameObject("G3PerfProbe");
            var p = go.AddComponent<G3PerfProbe>();
            p.label = "player" + (Array.IndexOf(Environment.GetCommandLineArgs(), "-g3noopt") >= 0 ? " -g3noopt" : "")
                      + " " + G3RenderTuning.Options.Describe();
            p.quitWhenDone = true;
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "-g3perfnowake") >= 0) { p.wakeTimeoutSeconds = 0; p.label += " nowake"; }
        }

        void Start() => StartCoroutine(Main());

        sealed class Stats
        {
            public readonly List<float> frames = new List<float>();
            public double main, render, gpu; public int gpuN;
            public double batches, draws, setpass, tris, verts, shadowCasters, skinned;
            public int n;
            public long usedMem, gfxMem;
            public int lodGroups, lodAnimated;   // LODGroups with animateCrossFading are excluded from the GPU Resident Drawer
            public int meshRenderers, blendProbes; // active mesh renderers / of them with LightProbeUsage.BlendProbes
            public int lights, lightsOn;           // active point/spot lights / of them enabled (G3LightShadowBudget distance LOD)
            public string top = "";                // largest profiler markers (avg ms per frame, all threads)
            public readonly List<string> hitches = new List<string>();
        }

        // Markers watched during the walk to explain hitch frames (dev builds / editor).
        static readonly string[] HitchMarkers =
        {
            "EarlyUpdate.UpdatePreloading", "Application.LoadLevelAsync Integrate", "Application.Integrate Assets in Background",
            "GC.Collect", "GPUResidentDrawer", "GRD.RegisterMaterialsAndMeshes", "RegisterMaterials", "Inl_Setup Light Constants",
            "GameObject.AddComponent", "Update.ScriptRunBehaviourUpdate", "PreLateUpdate.ScriptRunBehaviourLateUpdate",
            "PreLateUpdate.LegacyAnimationUpdate", "FixedUpdate.PhysicsFixedUpdate", "PostLateUpdate.UpdateAllRenderers",
            "PostLateUpdate.FinishFrameRendering", "PostLateUpdate.PresentAfterDraw", "Gfx.WaitForPresentOnGfxThread",
            "Gfx.WaitForRenderThread", "Shader.CreateGPUProgram", "CreateGPUProgram", "Physics.BakeMesh", "MeshCollider.Cook",
            "Loading.ReadObject", "Loading.AwakeFromLoad", "UnloadScene", "Application.UnloadLevel", "AssetGarbageCollectHelper",
            "GarbageCollectAssetsProfile", "UnloadUnusedAssets", "Resources.UnloadUnusedAssets", "Mesh.AwakeFromLoad",
            "Texture.AwakeFromLoad", "SceneCulling", "WaitForJobGroupID", "Shadows.ExecuteDrawShadows", "ExecuteRenderGraph",
        };

        static List<(string name, ProfilerRecorder rec)> StartHitchRecorders()
        {
            var list = new List<(string, ProfilerRecorder)>();
            var handles = new List<ProfilerRecorderHandle>();
            ProfilerRecorderHandle.GetAvailable(handles);
            var want = new HashSet<string>(HitchMarkers);
            foreach (var h in handles)
            {
                var d = ProfilerRecorderHandle.GetDescription(h);
                if (!want.Remove(d.Name)) continue;
                list.Add((d.Name, new ProfilerRecorder(h, 1)));
            }
            foreach (var (_, r) in list) r.Start();
            return list;
        }

        // Markers averaged over a view's sample (all threads; nested markers are counted at every level) to show where the
        // frame goes in steady state: GPU Resident Drawer work (probe updates, culling jobs), job waits, scripts, rendering.
        static readonly string[] SteadyMarkerParts =
        {
            "GPUResident", "GRD", "Probe", "Cull", "LOD", "InstanceData", "WorldProcessor", "MeshRendererProcessor",
            "UploadGPUInstanceData", "ComputeBuffer.SetData", "WaitForJobGroupID", "ScriptRunBehaviour", "UpdateAllRenderers",
            "Submit", "DoRenderLoop", "Shadows", "PhysicsFixedUpdate", "Animators", "Director", "NpcView", "G3",
            "Gfx.", "RenderGraph", "BatchRendererGroup", "Inl_", "DrawCommand", "AllocateBins", "PrefixSum",
        };
        const int MaxSteadyRecorders = 400;

        static List<(string name, ProfilerRecorder rec)> StartSteadyRecorders()
        {
            var list = new List<(string, ProfilerRecorder)>();
            var handles = new List<ProfilerRecorderHandle>();
            ProfilerRecorderHandle.GetAvailable(handles);
            var seen = new HashSet<string>();
            foreach (var h in handles)
            {
                if (list.Count >= MaxSteadyRecorders) break;
                var d = ProfilerRecorderHandle.GetDescription(h);
                // Markers only (counters such as "Draw Calls Count" have other units).
                if (d.UnitType != ProfilerMarkerDataUnit.TimeNanoseconds || string.IsNullOrEmpty(d.Name) || !seen.Add(d.Name)) continue;
                bool match = false;
                foreach (var part in SteadyMarkerParts)
                    if (d.Name.IndexOf(part, StringComparison.Ordinal) >= 0) { match = true; break; }
                if (match) list.Add((d.Name, new ProfilerRecorder(h, 1)));
            }
            foreach (var (_, r) in list) r.Start();
            return list;
        }

        static ProfilerRecorder Rec(ProfilerCategory c, string name) => ProfilerRecorder.StartNew(c, name, 4);

        IEnumerator Main()
        {
            Running = true;
            var sb = new StringBuilder();
            if (gotoOnly >= 0)
            {
                var st = FindAnyObjectByType<G3WorldStreamer>();
                var pl = st.holdUntilReady;
                st.Hold(this, true);   // stays frozen (Goto is for interactive inspection)
                yield return GoTo(st, pl, Camera.main, views[gotoOnly]);
                for (int k = 0; k < 60; k++) yield return null;
                Running = false;
                Destroy(gameObject);
                yield break;
            }
            if (here)
            {
                var hs = new Stats();
                yield return Sample(hs, sampleSeconds, null);
                Finish(Format("here " + label, hs, FindAnyObjectByType<G3WorldStreamer>()) + Environment.NewLine);
                yield break;
            }
            var streamer = FindAnyObjectByType<G3WorldStreamer>();
            var player = streamer != null && streamer.holdUntilReady != null ? streamer.holdUntilReady : null;
            var cam = Camera.main;
            sb.AppendLine($"=== {DateTime.Now:yyyy-MM-dd HH:mm:ss} {label} {(Application.isEditor ? "editor" : "player")} {Screen.width}x{Screen.height} q={QualitySettings.names[QualitySettings.GetQualityLevel()]}");
            if (streamer == null || cam == null) { Finish(sb.AppendLine("no streamer/camera").ToString()); yield break; }
            // Let the start-up loading screen / streamer release the player first, then freeze it for the whole probe.
            for (int k = 0; k < 1200 && streamer.IsHolding; k++) yield return new WaitForSecondsRealtime(0.1f);
            for (int k = 0; k < 10; k++) yield return null;
            streamer.Hold(this, true);

            foreach (var v in views)
            {
                yield return GoTo(streamer, player, cam, v);
                float t0 = Time.realtimeSinceStartup;
                while (Time.realtimeSinceStartup - t0 < settleSeconds) yield return null;
                float w0 = Time.realtimeSinceStartup;
                if (wakeTimeoutSeconds > 0 && G3DeferredActivation.Pending > 0)
                {
                    // Wake the rest quickly, as G3StartupLoader does behind the loading screen, then let a few frames pass.
                    float budget = G3DeferredActivation.BudgetMs;
                    int cap = G3DeferredActivation.MaxRenderersPerFrame;
                    G3DeferredActivation.BudgetMs = 250f;
                    G3DeferredActivation.MaxRenderersPerFrame = int.MaxValue;
                    while (G3DeferredActivation.Pending > 0 && Time.realtimeSinceStartup - w0 < wakeTimeoutSeconds) yield return null;
                    G3DeferredActivation.BudgetMs = budget;
                    G3DeferredActivation.MaxRenderersPerFrame = cap;
                    for (int k = 0; k < 30; k++) yield return null;
                }
                string wake = string.Format(CultureInfo.InvariantCulture, " | wake {0:F0}s pending {1}",
                                            Time.realtimeSinceStartup - w0, G3DeferredActivation.Pending);
                var s = new Stats();
                yield return Sample(s, sampleSeconds, null);
                sb.AppendLine(Format(v.name, s, streamer) + wake);
                if (G3RenderTuning.Options.ProfileFrames > 0)
                    yield return Capture(v.name, G3RenderTuning.Options.ProfileFrames, sb);
            }

            if (walk)
            {
                // Faring → north-west across the forest towards Montera at a fast run: crosses several cell borders.
                var a = new Vector3(655, 0, 586);
                var b = new Vector3(-125, 0, 125);
                yield return GoTo(streamer, player, cam, new View("walk", a.x, a.z, 0, 0));
                float t0 = Time.realtimeSinceStartup;
                while (Time.realtimeSinceStartup - t0 < settleSeconds) yield return null;
                var s = new Stats();
                float speed = 12f;   // m/s (horse-ish; player run is 8)
                float dur = Vector3.Distance(a, b) / speed;
                int loads0 = 0;
                Action<G3WorldManifest.Cell, UnityEngine.SceneManagement.Scene> onLoad = (c, sc) => loads0++;
                streamer.CellLoaded += onLoad;
                float start = Time.realtimeSinceStartup;
                float lastY = cam.transform.position.y;
                var dir = (b - a).normalized;
                var rot = Quaternion.LookRotation(dir);
                yield return Sample(s, dur, () =>
                {
                    float f = Mathf.Clamp01((Time.realtimeSinceStartup - start) / dur);
                    var p = Vector3.Lerp(a, b, f);
                    float y = Ground(p.x, p.z, lastY + 50);
                    if (!float.IsNaN(y)) lastY = y;
                    Place(player, cam, new Vector3(p.x, lastY, p.z), rot);
                });
                streamer.CellLoaded -= onLoad;
                sb.AppendLine(Format($"walk {dur:F0}s @{speed}m/s, {loads0} cell loads", s, streamer));
                foreach (var h in s.hitches) sb.AppendLine("  hitch " + h);
            }

            streamer.Hold(this, false);
            Finish(sb.ToString());
        }

        void Finish(string text)
        {
            LastResult = text;
            try { File.AppendAllText(LogPath, text); } catch { /* ignore */ }
            Debug.Log("G3PerfProbe\n" + text);
            Running = false;
            if (quitWhenDone) Application.Quit();
            Destroy(gameObject);
        }

        /// <summary>
        /// Binary profiler log of <paramref name="frames"/> frames at the current view, after the sample (so the profiler's own
        /// overhead does not enter the numbers): perf_&lt;view&gt;.raw next to the player exe (editor: Logs/). Read it in the
        /// editor with <c>G3.Unity.Editor.G3PerfProfile.SummarizeCapture(path)</c>.
        /// </summary>
        static IEnumerator Capture(string view, int frames, StringBuilder sb)
        {
            var name = new StringBuilder();
            foreach (char c in view) { if (char.IsLetterOrDigit(c)) name.Append(c); else if (c == ' ') break; }
            string dir = Application.isEditor ? Path.Combine(Application.dataPath, "../Logs") : Path.Combine(Application.dataPath, "..");
            string path = Path.GetFullPath(Path.Combine(dir, $"perf_{name}.raw"));
            UnityEngine.Profiling.Profiler.maxUsedMemory = 256 * 1024 * 1024;
            UnityEngine.Profiling.Profiler.logFile = path;
            UnityEngine.Profiling.Profiler.enableBinaryLog = true;
            UnityEngine.Profiling.Profiler.enabled = true;
            for (int k = 0; k < frames; k++) yield return null;
            UnityEngine.Profiling.Profiler.enabled = false;
            UnityEngine.Profiling.Profiler.enableBinaryLog = false;
            UnityEngine.Profiling.Profiler.logFile = "";
            sb.AppendLine($"  profile {path} ({frames} frames)");
        }

        static float Ground(float x, float z, float from)
        {
            if (Physics.Raycast(new Vector3(x, Mathf.Max(from, 50f) + 2000f, z), Vector3.down, out var hit, 5000f, ~0, QueryTriggerInteraction.Ignore))
                return hit.point.y;
            return float.NaN;
        }

        static void Place(GameObject player, Camera cam, Vector3 feet, Quaternion rot)
        {
            if (player != null) player.transform.position = feet + Vector3.up * 0.05f;
            cam.transform.SetPositionAndRotation(feet + Vector3.up * 1.83f, rot);
        }

        IEnumerator GoTo(G3WorldStreamer streamer, GameObject player, Camera cam, View v)
        {
            var rot = Quaternion.Euler(v.pitch, v.yaw, 0);
            Place(player, cam, new Vector3(v.x, 200, v.z), rot);
            float t0 = Time.realtimeSinceStartup;
            // Wait for streaming to settle (all cells in range loaded, nothing loading).
            int stable = 0;
            while (Time.realtimeSinceStartup - t0 < 120f)
            {
                yield return null;
                if (streamer.LoadingCount == 0 && streamer.IsReady(new Vector3(v.x, 0, v.z))) { if (++stable > 30) break; }
                else stable = 0;
            }
            float y = Ground(v.x, v.z, 200);
            Place(player, cam, new Vector3(v.x, float.IsNaN(y) ? 100 : y, v.z), rot);
        }

        IEnumerator Sample(Stats s, float seconds, Action perFrame)
        {
            SampleBegin?.Invoke();
            var hitchRecs = perFrame != null ? StartHitchRecorders() : null;
            var steadyRecs = perFrame == null ? StartSteadyRecorders() : null;
            var steadySums = steadyRecs != null ? new double[steadyRecs.Count] : null;
            using var main = Rec(ProfilerCategory.Internal, "Main Thread");
            using var render = Rec(ProfilerCategory.Internal, "Render Thread");
            using var batches = Rec(ProfilerCategory.Render, "Batches Count");
            using var draws = Rec(ProfilerCategory.Render, "Draw Calls Count");
            using var setpass = Rec(ProfilerCategory.Render, "SetPass Calls Count");
            using var tris = Rec(ProfilerCategory.Render, "Triangles Count");
            using var verts = Rec(ProfilerCategory.Render, "Vertices Count");
            using var shadow = Rec(ProfilerCategory.Render, "Shadow Casters Count");
            using var skinned = Rec(ProfilerCategory.Render, "Visible Skinned Meshes Count");
            var timings = new FrameTiming[1];
            yield return null;
            yield return null;
            float t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < seconds)
            {
                perFrame?.Invoke();
                yield return null;
                s.frames.Add(Time.unscaledDeltaTime * 1000f);
                if (hitchRecs != null && Time.unscaledDeltaTime > 0.0334f && s.hitches.Count < 40)
                {
                    var parts = new List<(string n, double ms)>();
                    foreach (var (n, r) in hitchRecs) { double ms = r.LastValue * 1e-6; if (ms >= 2) parts.Add((n, ms)); }
                    parts.Sort((x, y) => y.ms.CompareTo(x.ms));
                    var line = new StringBuilder($"t={Time.realtimeSinceStartup - t0:F1}s {Time.unscaledDeltaTime * 1000f:F0} ms:");
                    for (int k = 0; k < parts.Count && k < 8; k++) line.Append($" {parts[k].n}={parts[k].ms:F0}");
                    s.hitches.Add(line.ToString());
                }
                if (steadyRecs != null)
                    for (int k = 0; k < steadyRecs.Count; k++) steadySums[k] += steadyRecs[k].rec.LastValue * 1e-6;
                s.main += main.LastValue * 1e-6; s.render += render.LastValue * 1e-6;
                s.batches += batches.LastValue; s.draws += draws.LastValue; s.setpass += setpass.LastValue;
                s.tris += tris.LastValue; s.verts += verts.LastValue; s.shadowCasters += shadow.LastValue; s.skinned += skinned.LastValue;
                s.n++;
                FrameTimingManager.CaptureFrameTimings();
                if (FrameTimingManager.GetLatestTimings(1, timings) > 0 && timings[0].gpuFrameTime > 0) { s.gpu += timings[0].gpuFrameTime; s.gpuN++; }
            }
            if (hitchRecs != null) foreach (var (_, r) in hitchRecs) r.Dispose();
            if (steadyRecs != null)
            {
                var avg = new List<KeyValuePair<string, double>>();
                for (int k = 0; k < steadyRecs.Count; k++)
                {
                    avg.Add(new KeyValuePair<string, double>(steadyRecs[k].name, steadySums[k] / Math.Max(1, s.n)));
                    steadyRecs[k].rec.Dispose();
                }
                s.top = MarkerSummary.Top(avg, 16, 0.2);
            }
            foreach (var r in FindObjectsByType<MeshRenderer>())
            {
                s.meshRenderers++;
                if (r.lightProbeUsage == UnityEngine.Rendering.LightProbeUsage.BlendProbes) s.blendProbes++;
            }
            foreach (var l in FindObjectsByType<Light>())
            {
                if (l.type == LightType.Directional) continue;
                s.lights++;
                if (l.enabled) s.lightsOn++;
            }
            s.usedMem = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong(); s.gfxMem = UnityEngine.Profiling.Profiler.GetAllocatedMemoryForGraphicsDriver();
            foreach (var g in FindObjectsByType<LODGroup>())
            {
                s.lodGroups++;
                if (g.animateCrossFading) s.lodAnimated++;
            }
        }

        static string Format(string name, Stats s, G3WorldStreamer streamer)
        {
            var ic = CultureInfo.InvariantCulture;
            int n = Math.Max(1, s.n);
            var f = new List<float>(s.frames); f.Sort();
            float P(float q) => f.Count == 0 ? 0 : f[Mathf.Clamp((int)(q * (f.Count - 1)), 0, f.Count - 1)];
            float avg = 0; foreach (var x in f) avg += x; avg /= Math.Max(1, f.Count);
            int over33 = 0, over50 = 0; foreach (var x in f) { if (x > 33.4f) over33++; if (x > 50f) over50++; }
            return string.Format(ic,
                "{0,-40} frame avg {1,6:F1} ms ({2,5:F0} fps) p50 {3,5:F1} p99 {4,6:F1} max {5,6:F1} | >33ms {6} >50ms {7} of {8} | main {9,5:F1} render {10,5:F1} gpu {11,5:F1} | batches {12,6:F0} draws {13,6:F0} setpass {14,5:F0} tris {15,5:F1}M verts {16,5:F1}M shadowCasters {17,5:F0} skinned {18,4:F0} | cells {19} | mem {20:F0} MB gfx {21:F0} MB | lodAnim {22}/{23} | mr {24} blendProbes {25} | lights {27}/{28} | top {26}",
                name, avg, 1000f / Math.Max(0.01f, avg), P(0.5f), P(0.99f), f.Count > 0 ? f[f.Count - 1] : 0, over33, over50, f.Count,
                s.main / n, s.render / n, s.gpuN > 0 ? s.gpu / s.gpuN : -1,
                s.batches / n, s.draws / n, s.setpass / n, s.tris / n / 1e6, s.verts / n / 1e6, s.shadowCasters / n, s.skinned / n,
                streamer.LoadedCount, s.usedMem / 1048576.0, s.gfxMem / 1048576.0, s.lodAnimated, s.lodGroups,
                s.meshRenderers, s.blendProbes, s.top, s.lightsOn, s.lights) + (SampleExtra != null ? " | " + SampleExtra() : "");
        }
    }
}
