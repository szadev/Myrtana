using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace G3.Unity
{
    /// <summary>
    /// Time-sliced activation of a world cell's bulk content (Docs/performance.md). The cell importer
    /// (G3WorldBatch) saves the NPCs and the replacement trees of a cell inactive and lists them here; after the cell
    /// scene has loaded they are activated nearest-first within a small per-frame budget shared by all cells. Without
    /// this, the single "integrate" step of an additive scene load wakes ~10–20k objects (skinned characters with
    /// ~75 bones each, ~1000 LOD trees) in one frame — the main source of streaming hitches. Cells load ≥ 250 m
    /// away, so the ~0.2–0.5 s it takes to wake everything is not visible.
    /// Besides the time budget, the mesh renderers woken per frame are capped (<see cref="MaxRenderersPerFrame"/>): the GPU
    /// Resident Drawer registers each newly enabled renderer later in the same frame (instance data, batches, light-probe
    /// jobs), a cost the Stopwatch here does not see (Docs/performance.md, "After the loading screen").
    /// In the editor (cells opened for editing) G3DeferredActivationEditor shows the content immediately and keeps
    /// it inactive in the saved scene.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("G3/G3 Deferred Activation")]
    public sealed class G3DeferredActivation : MonoBehaviour
    {
        [Tooltip("Saved inactive by the importer; activated after load.")]
        public GameObject[] targets = System.Array.Empty<GameObject>();
        /// <summary>Milliseconds per frame (shared by all loading cells) spent activating.</summary>
        public static float BudgetMs = 1.5f;
        /// <summary>Objects activated per frame at least, whatever the budget.</summary>
        public static int MinPerFrame = 2;
        /// <summary>Targets closer than this to the camera are woken immediately, whatever the time budget (start, teleport, fast travel);
        /// the renderer cap still applies (G3StartupLoader lifts it behind the loading screen).</summary>
        public static float ImmediateDistance = 120f;
        /// <summary>Mesh renderers (all cells together) woken per frame at most; the first target of a frame always wakes.</summary>
        public static int MaxRenderersPerFrame = 48;

        /// <summary>Mesh renderers woken so far (all cells; for G3StartupTrace).</summary>
        public static long RenderersWoken { get; private set; }

        static int _frame = -1;
        static double _spent;
        static int _frameRenderers;
        static readonly List<MeshRenderer> RendererBuffer = new List<MeshRenderer>();
        int _next;
        bool _sorted;

        /// <summary>Cells whose content is still being woken (all loaded cells).</summary>
        public static int Pending { get; private set; }

        public bool Done => targets == null || _next >= targets.Length;
        public int Remaining => targets == null ? 0 : targets.Length - _next;

        static int _noDefer = -1;

        // Player command line "-g3renderercap N": MaxRenderersPerFrame for A/B measurements (0 = no cap).
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void ReadCommandLine()
        {
            var args = System.Environment.GetCommandLineArgs();
            int i = System.Array.IndexOf(args, "-g3renderercap");
            if (i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out int cap))
                MaxRenderersPerFrame = cap > 0 ? cap : int.MaxValue;
        }

        void Awake()
        {
            // Player command line "-g3nodefer": wake everything in the load frame (A/B performance comparison).
            if (_noDefer < 0) _noDefer = System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-g3nodefer") >= 0 ? 1 : 0;
            if (_noDefer == 1) ActivateAll();
        }

        void OnEnable() => Pending++;
        void OnDisable() => Pending--;

        void Update()
        {
            if (Done) { enabled = false; return; }
            if (!_sorted) SortByDistance();
            if (_frame != Time.frameCount) { _frame = Time.frameCount; _spent = 0; _frameRenderers = 0; }
            var sw = Stopwatch.StartNew();
            int n = 0;
            var cam = Camera.main;
            Vector3 p = cam != null ? cam.transform.position : Vector3.zero;
            float near2 = cam != null ? ImmediateDistance * ImmediateDistance : -1f;
            while (_next < targets.Length && (n < MinPerFrame || _spent + sw.Elapsed.TotalMilliseconds < BudgetMs ||
                                              (targets[_next] != null && (targets[_next].transform.position - p).sqrMagnitude < near2)))
            {
                var t = targets[_next];
                int renderers = 0;
                if (t != null && !t.activeSelf)
                {
                    t.GetComponentsInChildren(true, RendererBuffer);
                    renderers = RendererBuffer.Count;
                    if (_frameRenderers > 0 && _frameRenderers + renderers > MaxRenderersPerFrame) break;
                    G3RenderTuning.BeforeWake(t, RendererBuffer);
                    t.SetActive(true);
                }
                _next++;
                _frameRenderers += renderers;
                RenderersWoken += renderers;
                n++;
            }
            _spent += sw.Elapsed.TotalMilliseconds;
            if (Done) enabled = false;
        }

        /// <summary>Activates everything left at once (e.g. teleport target cell).</summary>
        public void ActivateAll()
        {
            if (targets == null) return;
            for (; _next < targets.Length; _next++)
            {
                var t = targets[_next];
                if (t == null || t.activeSelf) continue;
                t.GetComponentsInChildren(true, RendererBuffer);
                G3RenderTuning.BeforeWake(t, RendererBuffer);
                t.SetActive(true);
            }
            enabled = false;
        }

        void SortByDistance()
        {
            _sorted = true;
            var cam = Camera.main;
            if (cam == null || targets == null) return;
            var p = cam.transform.position;
            var keys = new float[targets.Length];
            for (int i = 0; i < targets.Length; i++)
                keys[i] = targets[i] != null ? (targets[i].transform.position - p).sqrMagnitude : float.MaxValue;
            System.Array.Sort(keys, targets);
        }
    }
}
