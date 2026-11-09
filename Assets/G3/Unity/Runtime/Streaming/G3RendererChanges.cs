using System.Collections.Generic;
using System.Diagnostics;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.PlayerLoop;
using Debug = UnityEngine.Debug;

namespace G3.Unity
{
    /// <summary>
    /// Bulk renderer changes spread over frames (Docs/performance.md, "Native crash with the GPU Resident Drawer").
    /// The GPU Resident Drawer turns every renderer whose materials, enable state or hierarchy changed into instance and
    /// batch updates in PostLateUpdate; the native crashes (DrawBuffersBatchMode / HybridRendererFillVisibilityBuffer on the
    /// render thread) came right after frames that changed tens of thousands of them at once (world remaster switch,
    /// liberation sector switch). Code that changes many renderers live enqueues one keyed item per renderer / object here
    /// instead; the queue runs at one fixed point of the frame (the start of Update, before every script and long before the
    /// drawer's PostLateUpdate pass) within <see cref="Budget"/> (renderers and milliseconds per frame). Each item applies the
    /// state wanted when it runs, so a switch flipped again while a wave is running converges without double work.
    /// Scene loads stay immediate (their renderers are new to the drawer anyway). <c>-g3swaprate N</c> sets the renderers per
    /// frame (0 = everything in one frame, the old behaviour, for A/B).
    /// </summary>
    public static class G3RendererChanges
    {
        /// <summary>Renderers per frame by default (≈ 26 µs drawer cost each in the player: ~13 ms per frame during a wave).</summary>
        public const int DefaultRenderersPerFrame = 512;
        /// <summary>Main-thread milliseconds per frame for the queue's own work.</summary>
        public const double DefaultBudgetMs = 3.0;

        static readonly PacedWork<Object> Queue = new PacedWork<Object>();
        static readonly ProfilerMarker RunMarker = new ProfilerMarker("G3RendererChanges.Run");
        static readonly List<string> WaveLabels = new List<string>();
        /// <summary>fix-186: small waves (relief cell swaps) are logged as a digest, not one line each.</summary>
        static readonly WaveLogDigest LogDigest = new WaveLogDigest();
        static PaceBudget _budget = new PaceBudget(DefaultRenderersPerFrame, DefaultBudgetMs);
        static bool _inserted;

        // the running wave (queue non-empty → empty), for the log line and the runner's measurement
        static int _waveStartFrame = -1, _waveFrames, _waveItems, _waveWeight;
        static float _waveStartTime;
        static double _waveMs, _waveMaxFrameMs;

        /// <summary>Per-frame limits (<c>-g3swaprate</c>, else the defaults).</summary>
        public static PaceBudget Budget
        {
            get => _budget;
            set => _budget = value;
        }

        /// <summary>Items waiting.</summary>
        public static int Pending => Queue.Count;

        /// <summary>The last finished wave: frames, renderers touched, wall-clock seconds (for the runner).</summary>
        public static string LastWave { get; private set; } = "";

        /// <summary>
        /// Queues <paramref name="work"/> for <paramref name="key"/> (a renderer or GameObject; a waiting entry is replaced).
        /// <paramref name="work"/> returns the renderers it touched. With pacing off, or outside play mode, it runs at once.
        /// </summary>
        public static void Enqueue(Object key, System.Func<int> work, string label)
        {
            if (key == null || work == null) return;
            if (!Application.isPlaying || _budget.Unlimited)
            {
                work();
                return;
            }
            EnsureInserted();
            if (Queue.Count == 0 && _waveStartFrame < 0) BeginWave();
            if (label != null && !WaveLabels.Contains(label)) WaveLabels.Add(label);
            Queue.Enqueue(key, work);
        }

        /// <summary>Runs everything still waiting now (quitting, tests, a teleport that must not show a half-converted world).</summary>
        public static void Flush()
        {
            if (Queue.Count == 0) return;
            RunFrame(new PaceBudget(0, 0));
        }

        static void BeginWave()
        {
            _waveStartFrame = Time.frameCount;
            _waveStartTime = Time.realtimeSinceStartup;
            _waveFrames = _waveItems = _waveWeight = 0;
            _waveMs = _waveMaxFrameMs = 0;
            WaveLabels.Clear();
        }

        static void Tick()
        {
            if (!Application.isPlaying || Queue.Count == 0) return;
            RunFrame(_budget);
        }

        static void RunFrame(PaceBudget budget)
        {
            using var _ = RunMarker.Auto();
            var sw = Stopwatch.StartNew();
            int items;
            int weight;
            try
            {
                items = Queue.Run(budget, () => sw.Elapsed.TotalMilliseconds, out weight);
            }
            catch (System.Exception e)
            {
                Debug.LogException(e);
                return;
            }
            double ms = sw.Elapsed.TotalMilliseconds;
            G3GpuDiag.Count("renderer.changes", weight);
            _waveFrames++;
            _waveItems += items;
            _waveWeight += weight;
            _waveMs += ms;
            if (ms > _waveMaxFrameMs) _waveMaxFrameMs = ms;
            if (Queue.Count == 0 && _waveStartFrame >= 0)
            {
                float wall = Time.realtimeSinceStartup - _waveStartTime;
                LastWave = $"{string.Join("+", WaveLabels)}: {_waveItems} items, {_waveWeight} renderers over {_waveFrames} frames " +
                           $"({wall:0.00} s wall, own work {_waveMs:0.0} ms, max {_waveMaxFrameMs:0.0} ms/frame, budget " +
                           $"{(_budget.MaxWeight > 0 ? _budget.MaxWeight.ToString() : "-")} renderers/{_budget.MaxMs:0.#} ms)";
                string line = LogDigest.Finished(string.Join("+", WaveLabels), _waveItems, _waveWeight, LastWave, Time.realtimeSinceStartup);
                if (line != null) Debug.Log("[G3RendererChanges] " + line);
                _waveStartFrame = -1;
            }
        }

        // The queue runs as its own player-loop system at the start of Update (before ScriptRunBehaviourUpdate).
        static void EnsureInserted()
        {
            if (_inserted) return;
            var root = PlayerLoop.GetCurrentPlayerLoop();
            if (Contains(root)) { _inserted = true; return; }
            var systems = root.subSystemList;
            for (int i = 0; i < systems.Length; i++)
            {
                if (systems[i].type != typeof(Update)) continue;
                var sub = new List<PlayerLoopSystem>(systems[i].subSystemList ?? new PlayerLoopSystem[0]);
                sub.Insert(0, new PlayerLoopSystem { type = typeof(G3RendererChangesTick), updateDelegate = Tick });
                systems[i].subSystemList = sub.ToArray();
                root.subSystemList = systems;
                PlayerLoop.SetPlayerLoop(root);
                _inserted = true;
                return;
            }
        }

        static bool Contains(PlayerLoopSystem s)
        {
            if (s.type == typeof(G3RendererChangesTick)) return true;
            if (s.subSystemList != null)
                foreach (var c in s.subSystemList)
                    if (Contains(c)) return true;
            return false;
        }

        /// <summary>Player-loop marker type.</summary>
        struct G3RendererChangesTick { }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            Queue.Clear();
            WaveLabels.Clear();
            LogDigest.Clear();
            _waveStartFrame = -1;
            _inserted = false;   // the editor may have reset the player loop; EnsureInserted checks before adding
            LastWave = "";
            _budget = new PaceBudget(DefaultRenderersPerFrame, DefaultBudgetMs);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void ReadCommandLine()
        {
            int rate = RenderTuningOptions.Parse(System.Environment.GetCommandLineArgs()).SwapRate;
            if (rate == 0) _budget = new PaceBudget(0, 0);
            else if (rate > 0) _budget = new PaceBudget(rate, DefaultBudgetMs);
            if (rate >= 0) Debug.Log($"[G3RendererChanges] -g3swaprate {rate}: {(rate == 0 ? "no pacing" : rate + " renderers/frame")}");
            Application.quitting -= OnQuit;
            Application.quitting += OnQuit;
        }

        static void OnQuit() => Queue.Clear();
    }
}
