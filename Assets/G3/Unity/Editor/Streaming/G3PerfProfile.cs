using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using G3.Unity;
using UnityEditor.Profiling;
using UnityEditorInternal;
using UnityEngine.Profiling;

namespace G3.Unity.Editor
{
    /// <summary>
    /// CLI profiling helper (Docs/performance.md): <c>Begin()</c> starts recording with the editor profiler,
    /// <c>Dump(frames, depth, minMs)</c> averages the main-thread (or another thread's) marker hierarchy over the
    /// last recorded frames and returns it as text.
    /// </summary>
    public static class G3PerfProfile
    {
        public static string Begin(bool deep = false)
        {
            ProfilerDriver.ClearAllFrames();
            ProfilerDriver.deepProfiling = deep;
            Profiler.enabled = true;
            ProfilerDriver.enabled = true;
            return "recording";
        }

        /// <summary>Arms / disarms <see cref="G3StartupTrace"/> for the next Play-mode starts (Logs/startup_trace.txt).</summary>
        public static string ArmStartupTrace(bool on = true)
        {
            var path = System.IO.Path.GetFullPath(System.IO.Path.Combine(UnityEngine.Application.dataPath, "..", G3StartupTrace.EditorFlagFile));
            if (on) System.IO.File.WriteAllText(path, "armed");
            else if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
            return on ? "startup trace armed" : "startup trace off";
        }

        public static string End()
        {
            ProfilerDriver.enabled = false;
            Profiler.enabled = false;
            return "stopped";
        }

        public static string Dump(int frames = 30, int maxDepth = 4, float minMs = 0.5f, int thread = 0)
        {
            int last = ProfilerDriver.lastFrameIndex;
            int first = System.Math.Max(ProfilerDriver.firstFrameIndex, last - frames + 1);
            if (last < 0) return "no frames";
            var total = new Dictionary<string, double>();
            var self = new Dictionary<string, double>();
            var calls = new Dictionary<string, double>();
            var order = new List<string>();
            int n = 0;
            double frameMs = 0;
            for (int f = first; f <= last; f++)
            {
                using var v = ProfilerDriver.GetHierarchyFrameDataView(f, thread, HierarchyFrameDataView.ViewModes.MergeSamplesWithTheSameName,
                    HierarchyFrameDataView.columnTotalTime, false);
                if (v == null || !v.valid) continue;
                n++;
                frameMs += v.frameTimeMs;
                Walk(v, v.GetRootItemID(), "", 0, maxDepth, total, self, calls, order);
            }
            if (n == 0) return "no valid frames";
            var sb = new StringBuilder();
            sb.AppendLine($"frames {first}..{last} ({n}), avg frame {frameMs / n:F1} ms, thread {thread}");
            foreach (var k in order)
            {
                double t = total[k] / n;
                if (t < minMs) continue;
                int depth = k.Count(c => c == '/');
                string name = k.Substring(k.LastIndexOf('/') + 1);
                sb.AppendLine($"{new string(' ', depth * 2)}{name}  {t:F2} ms (self {self[k] / n:F2}, calls {calls[k] / n:F0})");
            }
            return sb.ToString();
        }

        /// <summary>Hierarchy of the <paramref name="rank"/>-th slowest recorded frame (0 = worst).</summary>
        public static string DumpWorst(int rank = 0, int maxDepth = 8, float minMs = 2f, int thread = 0)
        {
            int last = ProfilerDriver.lastFrameIndex, first = ProfilerDriver.firstFrameIndex;
            if (last < 0) return "no frames";
            var times = new List<(int f, float ms)>();
            for (int f = first; f <= last; f++)
            {
                using var v = ProfilerDriver.GetHierarchyFrameDataView(f, 0, HierarchyFrameDataView.ViewModes.MergeSamplesWithTheSameName, HierarchyFrameDataView.columnTotalTime, false);
                if (v != null && v.valid) times.Add((f, v.frameTimeMs));
            }
            times.Sort((a, b) => b.ms.CompareTo(a.ms));
            if (rank >= times.Count) return "rank out of range";
            var sb = new StringBuilder();
            sb.AppendLine("worst frames: " + string.Join(", ", times.Take(12).Select(x => $"{x.f}:{x.ms:F0}")));
            int frame = times[rank].f;
            var total = new Dictionary<string, double>(); var self = new Dictionary<string, double>(); var calls = new Dictionary<string, double>(); var order = new List<string>();
            using (var v = ProfilerDriver.GetHierarchyFrameDataView(frame, thread, HierarchyFrameDataView.ViewModes.MergeSamplesWithTheSameName, HierarchyFrameDataView.columnTotalTime, false))
                Walk(v, v.GetRootItemID(), "", 0, maxDepth, total, self, calls, order);
            foreach (var k in order)
            {
                if (total[k] < minMs) continue;
                int depth = k.Count(c => c == '/');
                sb.AppendLine($"{new string(' ', depth * 2)}{k.Substring(k.LastIndexOf('/') + 1)}  {total[k]:F1} ms (self {self[k]:F1}, calls {calls[k]:F0})");
            }
            return sb.ToString();
        }

        /// <summary>Frame times of all recorded frames ("index:ms ...").</summary>
        public static string FrameTimes()
        {
            var sb = new StringBuilder();
            for (int f = ProfilerDriver.firstFrameIndex; f <= ProfilerDriver.lastFrameIndex && f >= 0; f++)
            {
                using var v = ProfilerDriver.GetRawFrameDataView(f, 0);
                if (v != null && v.valid) sb.Append($"{f}:{v.frameTimeMs:F0} ");
            }
            return sb.ToString();
        }

        /// <summary>
        /// Self time per marker name in one frame of the thread whose name contains <paramref name="threadName"/>
        /// (e.g. "Main Thread", "PreloadManager", "Render Thread"), largest first. Shows native work the merged
        /// hierarchy hides (e.g. Prefabs.MergePrefabs inside scene integration).
        /// </summary>
        public static string SelfTimes(int frame, string threadName = "Main Thread", int top = 15)
        {
            var v = FindThread(frame, threadName);
            if (v == null) return $"thread '{threadName}' not found in frame {frame}";
            using (v)
            {
                var self = new Dictionary<string, (double ms, int calls)>();
                AddSelfTimes(v, self);
                var sb = new StringBuilder($"frame {frame} ({v.frameTimeMs:F0} ms) thread {v.threadName}\n");
                foreach (var kv in self.OrderByDescending(x => x.Value.ms).Take(top))
                    sb.AppendLine($"{kv.Value.ms,8:F1} ms x{kv.Value.calls} {kv.Key}");
                return sb.ToString();
            }
        }

        static void AddSelfTimes(RawFrameDataView v, Dictionary<string, (double ms, int calls)> self)
        {
            int n = v.sampleCount;
            var childSum = new double[n];
            var open = new Stack<(int index, int left)>();
            for (int i = 0; i < n; i++)
            {
                while (open.Count > 0 && open.Peek().left == 0) open.Pop();
                if (open.Count > 0)
                {
                    var (p, left) = open.Pop();
                    open.Push((p, left - 1));
                    childSum[p] += v.GetSampleTimeMs(i);
                }
                if (v.GetSampleChildrenCount(i) > 0) open.Push((i, v.GetSampleChildrenCount(i)));
            }
            for (int i = 0; i < n; i++)
            {
                string name = v.GetSampleName(i) ?? "?";
                self.TryGetValue(name, out var a);
                self[name] = (a.ms + v.GetSampleTimeMs(i) - childSum[i], a.calls + 1);
            }
        }

        /// <summary>
        /// Self time per marker, summed over all threads whose name contains <paramref name="threadName"/> (e.g. "Worker"
        /// for the job workers), averaged over frames <paramref name="first"/>..<paramref name="last"/>, largest first.
        /// </summary>
        public static string ThreadSelfAverages(int first, int last, string threadName, int top = 40)
        {
            var self = new Dictionary<string, (double ms, int calls)>();
            int frames = 0, threads = 0;
            for (int f = first; f <= last; f++)
            {
                bool any = false;
                for (int t = 0; t < 256; t++)
                {
                    RawFrameDataView v;
                    try { v = ProfilerDriver.GetRawFrameDataView(f, t); }
                    catch (System.ArgumentException) { break; }
                    if (v == null) break;
                    using (v)
                    {
                        if (!v.valid || v.threadName == null || !v.threadName.Contains(threadName)) continue;
                        AddSelfTimes(v, self);
                        any = true;
                        if (f == first) threads++;
                    }
                }
                if (any) frames++;
            }
            if (frames == 0) return $"no thread '{threadName}'\n";
            double sum = self.Values.Sum(x => x.ms) / frames;
            var sb = new StringBuilder($"{threads} threads '{threadName}', {frames} frames, busy {sum:F2} ms per frame (all threads)\n");
            foreach (var kv in self.Where(x => x.Key != "Idle").OrderByDescending(x => x.Value.ms).Take(top))
                sb.AppendLine($"{kv.Value.ms / frames,8:F2} ms x{kv.Value.calls / (double)frames:F1} {kv.Key}");
            return sb.ToString();
        }

        /// <summary>
        /// Loads a binary profiler log (perf_&lt;view&gt;.raw written by G3PerfProbe with -g3perfprofile, or any .raw) and writes a
        /// text summary next to it (.txt): average main-thread hierarchy, then self time per marker for the main thread, the
        /// job workers (summed) and the render thread. Returns the summary path. Usage:
        /// <c>G3.Unity.Editor.G3PerfProfile.SummarizeCapture("Builds/G3World_Dev/perf_Faring.raw")</c>.
        /// </summary>
        public static string SummarizeCapture(string rawPath, int maxDepth = 6, float minMs = 0.1f, int top = 40)
        {
            if (!System.IO.File.Exists(rawPath)) return "missing " + rawPath;
            if (!ProfilerDriver.LoadProfile(rawPath, false)) return "could not load " + rawPath;
            int first = ProfilerDriver.firstFrameIndex, last = ProfilerDriver.lastFrameIndex;
            if (last < 0) return "no frames in " + rawPath;
            // The first frames after enabling the profiler are incomplete; skip a few if there are enough.
            if (last - first > 20) first += 5;
            var sb = new StringBuilder($"{rawPath}: frames {first}..{last}\n");
            sb.AppendLine("== Main thread hierarchy (average per frame)");
            sb.Append(Dump(last - first + 1, maxDepth, minMs, 0));
            sb.AppendLine("== Main thread self times");
            sb.Append(ThreadSelfAverages(first, last, "Main Thread", top));
            sb.AppendLine("== Job workers self times (summed over workers)");
            sb.Append(ThreadSelfAverages(first, last, "Worker", top));
            sb.AppendLine("== Render thread self times");
            sb.Append(ThreadSelfAverages(first, last, "Render Thread", top));
            string outPath = System.IO.Path.ChangeExtension(rawPath, ".txt");
            System.IO.File.WriteAllText(outPath, sb.ToString());
            return outPath;
        }

        static RawFrameDataView FindThread(int frame, string threadName)
        {
            for (int t = 0; t < 256; t++)
            {
                RawFrameDataView v;
                try { v = ProfilerDriver.GetRawFrameDataView(frame, t); }
                catch (System.ArgumentException) { return null; }
                if (v == null || !v.valid) { v?.Dispose(); continue; }
                if (v.threadName.Contains(threadName)) return v;
                v.Dispose();
            }
            return null;
        }

        /// <summary>
        /// Development player with the master scene and the cells in the rectangle "ix0,iz0,ix1,iz1" (default: Faring,
        /// Montera woods, Bakaresh and the path between) — Builds\ (git-ignored). Run it with -g3perf to
        /// execute <see cref="G3.Unity.G3PerfProbe"/> and quit (results in perf_probe.txt next to the exe).
        /// </summary>
        public static string BuildDev(string rect = "13,11,21,21", bool development = true)
        {
            var p = rect.Split(',').Select(int.Parse).ToArray();
            var manifest = AssetDatabase.LoadAssetAtPath<G3WorldManifest>(G3WorldBatch.ManifestPath);
            var scenes = new List<string> { G3WorldBatch.MasterPath };
            foreach (var c in manifest.cells)
                if (c.HasScene && c.ix >= p[0] && c.ix <= p[2] && c.iz >= p[1] && c.iz <= p[3] && System.IO.File.Exists(c.scenePath))
                    scenes.Add(c.scenePath);
            var opts = new BuildPlayerOptions
            {
                scenes = scenes.ToArray(),
                locationPathName = "Builds/G3World_" + (development ? "Dev" : "Release") + "/G3World.exe",
                target = BuildTarget.StandaloneWindows64,
                options = development ? BuildOptions.Development : BuildOptions.None,
            };
            var report = BuildPipeline.BuildPlayer(opts);
            var s = report.summary;
            return $"{s.result} {scenes.Count} scenes, {s.totalSize / 1048576} MB, {s.totalTime}, errors {s.totalErrors}, warnings {s.totalWarnings} -> {opts.locationPathName}";
        }

        static void Walk(HierarchyFrameDataView v, int id, string path, int depth, int maxDepth,
            Dictionary<string, double> total, Dictionary<string, double> self, Dictionary<string, double> calls, List<string> order)
        {
            var children = new List<int>();
            v.GetItemChildren(id, children);
            foreach (int c in children)
            {
                string key = path + "/" + v.GetItemName(c);
                if (!total.ContainsKey(key)) { total[key] = 0; self[key] = 0; calls[key] = 0; order.Add(key); }
                total[key] += v.GetItemColumnDataAsDouble(c, HierarchyFrameDataView.columnTotalTime);
                self[key] += v.GetItemColumnDataAsDouble(c, HierarchyFrameDataView.columnSelfTime);
                calls[key] += v.GetItemColumnDataAsDouble(c, HierarchyFrameDataView.columnCalls);
                if (depth + 1 < maxDepth) Walk(v, c, key, depth + 1, maxDepth, total, self, calls, order);
            }
        }
    }
}
