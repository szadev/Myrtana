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
    /// Frame-spike recorder (perf-5, Docs/performance.md "Frame spikes"): rides the player along a fixed route at a fixed
    /// speed (streaming like a fast ride through the world) or records in place (<see cref="Here"/>, e.g. a fight), and
    /// reports avg / p50 / p99 / "1 % low" (the mean of the slowest 1 %, as the FPS overlay) / max frame and the frames over
    /// 25 / 33 / 50 ms. Every frame over the threshold gets a hitch line with the large frame-level markers of that frame,
    /// the cells that finished loading / unloading and the GC count. Results go to Logs/perf_ride.txt (player: next to the exe)
    /// and <see cref="LastResult"/>. Editor: <c>G3PerfRide.Run("faring_vengard")</c> in Play mode.
    /// </summary>
    public sealed class G3PerfRide : MonoBehaviour
    {
        /// <summary>Fixed routes (world metres x,z): the start of each is a teleport, then straight legs.</summary>
        public static readonly Dictionary<string, Vector2[]> Routes = new Dictionary<string, Vector2[]>
        {
            // Faring lower town -> south-east over the pass to Vengard's gate (~1.4 km).
            ["faring_vengard"] = new[] { new Vector2(568, 576), new Vector2(700, 380), new Vector2(860, 120), new Vector2(980, -60) },
            // Nordmar: from the Clan of the Hammer camp area around the valley (~1 km).
            ["nordmar"] = new[] { new Vector2(430, 1500), new Vector2(620, 1540), new Vector2(760, 1720), new Vector2(560, 1840) },
            // The probe's walk: Faring -> Montera woods.
            ["faring_montera"] = new[] { new Vector2(655, 586), new Vector2(-125, 125) },
        };

        public static string LastResult = "";
        public static bool Running;

        string _label = "";
        Vector2[] _route;
        float _speed = 12f;
        float _seconds;
        float _threshold = 40f;
        bool _here;

        static string LogPath => Application.isEditor
            ? Path.GetFullPath(Path.Combine(Application.dataPath, "../Logs/perf_ride.txt"))
            : Path.GetFullPath(Path.Combine(Application.dataPath, "../perf_ride.txt"));

        /// <summary>Rides <paramref name="route"/> (a name of <see cref="Routes"/> or "x,z x,z ...") at <paramref name="speed"/> m/s.</summary>
        public static string Run(string route, float speed = 12f, string label = "", float thresholdMs = 40f)
        {
            if (!Application.isPlaying) return "not playing";
            if (Running) return "already running";
            if (!Routes.TryGetValue(route, out var pts)) pts = Parse(route);
            if (pts == null || pts.Length < 2) return "bad route " + route;
            var p = new GameObject("G3PerfRide").AddComponent<G3PerfRide>();
            p._route = pts; p._speed = Mathf.Max(1f, speed); p._label = label + " " + route; p._threshold = thresholdMs;
            p.StartCoroutine(p.Main());
            return "started";
        }

        /// <summary>Records <paramref name="seconds"/> at the current place without moving or holding the player.</summary>
        public static string Here(float seconds, string label = "", float thresholdMs = 40f)
        {
            if (!Application.isPlaying) return "not playing";
            if (Running) return "already running";
            var p = new GameObject("G3PerfRide").AddComponent<G3PerfRide>();
            p._here = true; p._seconds = Mathf.Max(1f, seconds); p._label = label + " here"; p._threshold = thresholdMs;
            p.StartCoroutine(p.Main());
            return "started";
        }

        static Vector2[] Parse(string s)
        {
            var list = new List<Vector2>();
            foreach (var part in s.Split(new[] { ' ', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var xz = part.Split(',');
                if (xz.Length != 2 || !float.TryParse(xz[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) ||
                    !float.TryParse(xz[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float z)) return null;
                list.Add(new Vector2(x, z));
            }
            return list.ToArray();
        }

        // Frame-level markers (and the G3 ones that can be large in a frame) shown in the hitch lines.
        static readonly string[] Markers =
        {
            "EarlyUpdate.UpdatePreloading", "Application.LoadLevelAsync Integrate", "Application.Integrate Assets in Background",
            "Loading.AwakeFromLoad", "Loading.ReadObject", "UnloadScene", "Application.UnloadLevel", "GC.Collect",
            "GarbageCollectAssetsProfile", "Resources.UnloadUnusedAssets", "UnloadUnusedAssets",
            "Update.ScriptRunBehaviourUpdate", "PreLateUpdate.ScriptRunBehaviourLateUpdate", "FixedUpdate.ScriptRunBehaviourFixedUpdate",
            "FixedUpdate.PhysicsFixedUpdate", "PreLateUpdate.DirectorUpdateAnimationBegin", "PreLateUpdate.LegacyAnimationUpdate",
            "PostLateUpdate.UpdateAllRenderers", "PostLateUpdate.FinishFrameRendering", "Gfx.WaitForPresentOnGfxThread",
            "Gfx.WaitForRenderThread", "Shader.CreateGPUProgram", "CreateGPUProgram", "CreateGraphicsPipelineImpl",
            "Physics.BakeMesh", "MeshCollider.Cook", "Mesh.AwakeFromLoad", "Texture.AwakeFromLoad", "AudioClip.AwakeFromLoad",
            "WaitForJobGroupID", "GPUResidentDrawer", "ProcessMeshRendererChanges", "Update.ScriptRunDelayedDynamicFrameRate",
            "EarlyUpdate.ScriptRunDelayedStartupFrame", "PreUpdate.SendMouseEvents", "PostLateUpdate.PlayerSendFrameComplete",
            "Initialization.PlayerUpdateTime", "PostLateUpdate.UpdateAudio", "PreUpdate.AIUpdatePostScript",
        };

        static List<(string name, ProfilerRecorder rec)> StartRecorders()
        {
            var list = new List<(string, ProfilerRecorder)>();
            var handles = new List<ProfilerRecorderHandle>();
            ProfilerRecorderHandle.GetAvailable(handles);
            var want = new HashSet<string>(Markers);
            var seen = new HashSet<string>();
            foreach (var h in handles)
            {
                var d = ProfilerRecorderHandle.GetDescription(h);
                if (string.IsNullOrEmpty(d.Name) || d.UnitType != ProfilerMarkerDataUnit.TimeNanoseconds || !seen.Add(d.Name)) continue;
                bool g3 = d.Name.StartsWith("G3", StringComparison.Ordinal) || d.Name.StartsWith("Myrtana", StringComparison.Ordinal) ||
                          d.Name.StartsWith("CellNavMeshes", StringComparison.Ordinal) || d.Name.StartsWith("Npc", StringComparison.Ordinal);
                if (!want.Remove(d.Name) && !g3) continue;
                list.Add((d.Name, new ProfilerRecorder(h, 1)));
            }
            foreach (var (_, r) in list) r.Start();
            return list;
        }

        IEnumerator Main()
        {
            Running = true;
            var ic = CultureInfo.InvariantCulture;
            var streamer = FindAnyObjectByType<G3WorldStreamer>();
            var player = streamer != null ? streamer.holdUntilReady : null;
            var cam = Camera.main;
            if (streamer == null || cam == null) { Finish("no streamer/camera"); yield break; }
            for (int k = 0; k < 1200 && streamer.IsHolding; k++) yield return new WaitForSecondsRealtime(0.1f);

            Vector3[] legs = null;
            float length = 0;
            if (!_here)
            {
                streamer.Hold(this, true);
                legs = new Vector3[_route.Length];
                for (int i = 0; i < _route.Length; i++) legs[i] = new Vector3(_route[i].x, 0, _route[i].y);
                for (int i = 1; i < legs.Length; i++) length += Vector3.Distance(legs[i - 1], legs[i]);
                // Start: teleport, everything around streamed in and awake, then a calm moment.
                var rot0 = Quaternion.LookRotation(legs[1] - legs[0]);
                Place(player, cam, new Vector3(legs[0].x, 200, legs[0].z), rot0);
                float t0 = Time.realtimeSinceStartup;
                int stable = 0;
                while (Time.realtimeSinceStartup - t0 < 120f)
                {
                    yield return null;
                    if (streamer.LoadingCount == 0 && streamer.IsReady(legs[0]) && G3DeferredActivation.Pending == 0) { if (++stable > 60) break; }
                    else stable = 0;
                }
                float y0 = Ground(legs[0].x, legs[0].z, 200);
                Place(player, cam, new Vector3(legs[0].x, float.IsNaN(y0) ? 100 : y0, legs[0].z), rot0);
                float t1 = Time.realtimeSinceStartup;
                while (Time.realtimeSinceStartup - t1 < 4f) yield return null;
                _seconds = length / _speed;
            }

            int loads = 0, unloads = 0, frameLoads = 0, frameUnloads = 0;
            Action<G3WorldManifest.Cell, UnityEngine.SceneManagement.Scene> onLoad = (c, s) => { loads++; frameLoads++; };
            Action<G3WorldManifest.Cell> onUnload = c => { unloads++; frameUnloads++; };
            streamer.CellLoaded += onLoad;
            streamer.CellUnloaded += onUnload;
            var recs = StartRecorders();
            using var alloc = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame", 1);
            double allocSum = 0;
            var frames = new List<float>(8192);
            var hitches = new List<(float ms, string line)>();
            var blame = new Dictionary<string, (int top, double ms)>();
            float lastY = cam.transform.position.y;
            int gc0 = GC.CollectionCount(0), gcPrev = gc0;
            yield return null;
            yield return null;
            float start = Time.realtimeSinceStartup;
            int legIndex = 1;
            float legStart = 0, travelled = 0;
            while (Time.realtimeSinceStartup - start < _seconds)
            {
                if (!_here)
                {
                    travelled = Mathf.Min(length, (Time.realtimeSinceStartup - start) * _speed);
                    while (legIndex < legs.Length - 1 && travelled > legStart + Vector3.Distance(legs[legIndex - 1], legs[legIndex]))
                    {
                        legStart += Vector3.Distance(legs[legIndex - 1], legs[legIndex]);
                        legIndex++;
                    }
                    var a = legs[legIndex - 1]; var b = legs[legIndex];
                    float legLen = Mathf.Max(0.01f, Vector3.Distance(a, b));
                    var p = Vector3.Lerp(a, b, Mathf.Clamp01((travelled - legStart) / legLen));
                    float y = Ground(p.x, p.z, lastY + 50);
                    if (!float.IsNaN(y)) lastY = y;
                    Place(player, cam, new Vector3(p.x, lastY, p.z), Quaternion.LookRotation(b - a));
                }
                frameLoads = 0; frameUnloads = 0;
                yield return null;
                float ms = Time.unscaledDeltaTime * 1000f;
                frames.Add(ms);
                allocSum += alloc.LastValue;
                int gc = GC.CollectionCount(0);
                if (ms > _threshold && hitches.Count < 2000)
                {
                    var parts = new List<(string n, double ms)>();
                    foreach (var (n, r) in recs) { double v = r.LastValue * 1e-6; if (v >= 1.0) parts.Add((n, v)); }
                    parts.Sort((x, z) => z.ms.CompareTo(x.ms));
                    // Blame: the largest specific marker (not the player-loop phase that contains it) of the hitch frame.
                    string top = null;
                    foreach (var (n, v) in parts)
                        if (n.IndexOf('.') < 0 || n.StartsWith("G3", StringComparison.Ordinal) || n.StartsWith("Npc", StringComparison.Ordinal) ||
                            n.StartsWith("Myrtana", StringComparison.Ordinal) || n.StartsWith("GC.", StringComparison.Ordinal) ||
                            n.StartsWith("Loading.", StringComparison.Ordinal) || n.StartsWith("Application.", StringComparison.Ordinal) ||
                            n.StartsWith("CellNavMeshes", StringComparison.Ordinal) || n.StartsWith("Shader.", StringComparison.Ordinal))
                        { top = n; break; }
                    top ??= parts.Count > 0 ? parts[0].n : "-";
                    blame.TryGetValue(top, out var b);
                    blame[top] = (b.top + 1, b.ms + ms);
                    var line = new StringBuilder(string.Format(ic, "t={0,5:F1}s {1,5:F1} ms", Time.realtimeSinceStartup - start, ms));
                    if (frameLoads > 0 || frameUnloads > 0) line.Append($" [cells +{frameLoads} -{frameUnloads}]");
                    if (gc != gcPrev) line.Append($" [gc {gc - gcPrev}]");
                    line.Append($" pend {G3DeferredActivation.Pending}:");
                    for (int k = 0; k < parts.Count && k < 10; k++) line.Append(string.Format(ic, " {0}={1:F1}", parts[k].n, parts[k].ms));
                    hitches.Add((ms, line.ToString()));
                }
                gcPrev = gc;
            }
            streamer.CellLoaded -= onLoad;
            streamer.CellUnloaded -= onUnload;
            foreach (var (_, r) in recs) r.Dispose();
            if (!_here) streamer.Hold(this, false);

            var sorted = new List<float>(frames); sorted.Sort();
            float P(float q) => sorted.Count == 0 ? 0 : sorted[Mathf.Clamp((int)(q * (sorted.Count - 1)), 0, sorted.Count - 1)];
            double sum = 0; foreach (var x in sorted) sum += x;
            int n1 = Math.Max(1, sorted.Count / 100);
            double low = 0; for (int k = sorted.Count - n1; k < sorted.Count; k++) if (k >= 0) low += sorted[k];
            low /= n1;
            int o25 = 0, o33 = 0, o50 = 0; foreach (var x in sorted) { if (x > 25f) o25++; if (x > 33.4f) o33++; if (x > 50f) o50++; }
            var sb = new StringBuilder();
            sb.AppendLine(string.Format(ic,
                "=== {0:yyyy-MM-dd HH:mm:ss} ride {1} {2} {3}x{4}: {5:F0} s{6}, {7} frames | avg {8:F1} ms ({9:F0} fps) p50 {10:F1} p99 {11:F1} 1%low {12:F1} ({13:F0} fps) max {14:F1} | >25 {15} >33 {16} >50 {17} | cells +{18} -{19} | gc {20} ({22:F1} MB/s main-thread alloc, incremental {23}) | mem {21:F0} MB",
                DateTime.Now, _label.Trim(), Application.isEditor ? "editor" : "player", Screen.width, Screen.height, _seconds,
                _here ? "" : string.Format(ic, " {0:F0} m @{1:F0} m/s", length, _speed), sorted.Count,
                sum / Math.Max(1, sorted.Count), 1000.0 / Math.Max(0.01, sum / Math.Max(1, sorted.Count)), P(0.5f), P(0.99f), low,
                1000.0 / Math.Max(0.01, low), sorted.Count > 0 ? sorted[sorted.Count - 1] : 0, o25, o33, o50, loads, unloads,
                GC.CollectionCount(0) - gc0, UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong() / 1048576.0,
                allocSum / 1048576.0 / Math.Max(0.01, _seconds), UnityEngine.Scripting.GarbageCollector.isIncremental));
            var bl = new List<KeyValuePair<string, (int top, double ms)>>(blame);
            bl.Sort((x, z) => z.Value.ms.CompareTo(x.Value.ms));
            sb.Append($"  blame (frames > {_threshold:F0} ms: top marker, frames, sum ms):");
            foreach (var kv in bl) sb.Append(string.Format(ic, " {0} {1}/{2:F0}", kv.Key, kv.Value.top, kv.Value.ms));
            sb.AppendLine();
            hitches.Sort((x, z) => z.ms.CompareTo(x.ms));
            for (int k = 0; k < hitches.Count && k < 30; k++) sb.AppendLine("  " + hitches[k].line);
            Finish(sb.ToString());
        }

        void Finish(string text)
        {
            LastResult = text;
            try { File.AppendAllText(LogPath, text + Environment.NewLine); } catch { /* ignore */ }
            Debug.Log("[G3PerfRide] " + text);
            Running = false;
            Destroy(gameObject);
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
    }
}
