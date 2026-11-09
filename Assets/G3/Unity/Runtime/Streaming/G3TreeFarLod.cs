using System.Collections.Generic;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace G3.Unity
{
    /// <summary>
    /// perf-22d (Docs/performance.md, "Far trees: billboard only"): switches the geometry renderers (LOD0..LODn) of far
    /// trees off while their LODGroup can only show the billboard (<see cref="TreeFarLod"/>), and on again before the
    /// camera comes near enough to need them. The GPU Resident Drawer culls every enabled renderer once per culling view
    /// (camera, 4 sun cascades, 6 faces per shadowed point light); trees are ~5 renderers each and most of the ~60k trees
    /// in the loaded cells are far, so this removes most of the CullingJob work without changing a pixel (the drawn level
    /// is the billboard either way). Trees register as <see cref="G3DeferredActivation"/> wakes them (before they are
    /// active, so far ones never reach the drawer with their geometry). A sweep checks a slice of the trees per frame;
    /// switches are capped per frame (drawer registration ≈ 26 µs per renderer) except when the camera is already inside
    /// a tree's billboard distance. Player switch <c>-g3treefar 0</c> turns it off (A/B).
    /// </summary>
    public static class G3TreeFarLod
    {
        /// <summary>Master switch (<c>-g3treefar 0</c> = off).</summary>
        public static bool Enabled = true;
        /// <summary>Trees checked per frame (all trees are checked every Count / SweepPerFrame frames).</summary>
        public static int SweepPerFrame = 3000;
        /// <summary>Trees switched per frame at most when not urgent (~4 renderers each).</summary>
        public static int MaxSwitchesPerFrame = 12;

        struct Entry
        {
            public LODGroup group;
            public Renderer[] geometry;
            public Vector3 position;
            public float k;
            public Scene scene;
            public bool on;
            // perf-2: squared on / off / urgent distances for the camera factor of _version (the sweep is a compare)
            public int version;
            public float on2, off2, urgent2;
        }

        static readonly List<Entry> Entries = new List<Entry>();
        static readonly List<Renderer> Tmp = new List<Renderer>();
        static readonly ProfilerMarker SweepMarker = new ProfilerMarker("G3TreeFarLod.Sweep");
        static int _cursor, _version = 1;
        static float _factor = -1f;
        static Driver _driver;
        static bool _hooked;

        /// <summary>Registered trees / trees with the geometry off / renderers switched so far (runner, perf probe).</summary>
        public static int Count => Entries.Count;
        public static int Off { get; private set; }
        public static long Switched { get; private set; }

        public static string Describe() => Enabled ? $"tree far LOD: {Entries.Count} trees, {Off} billboard only, {Switched} switches" : "tree far LOD off";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Reset()
        {
            Entries.Clear();
            _cursor = 0;
            _version = 1;
            _factor = -1f;
            Off = 0;
            Switched = 0;
            _driver = null;
            _hooked = false;
            var args = System.Environment.GetCommandLineArgs();
            int i = System.Array.IndexOf(args, "-g3treefar");
            Enabled = !(i >= 0 && i + 1 < args.Length && args[i + 1] == "0");
        }

        /// <summary>
        /// A deferred target is about to wake (G3RenderTuning.BeforeWake): when it is a tree LODGroup, register it and, if it
        /// is far, switch its geometry off before the drawer sees it.
        /// </summary>
        public static void BeforeWake(GameObject target)
        {
            if (!Enabled || !Application.isPlaying || target == null || !target.TryGetComponent(out LODGroup group)) return;
            var lods = group.GetLODs();
            if (lods.Length < 2) return;
            var last = lods[lods.Length - 1].renderers;
            if (last == null || last.Length != 1 || last[0] == null) return;
            string n = last[0].name;
            if (n != "Billboard" && n != "Impostor") return;   // G3SpeedTreeImporter / G3TreeV2Builder trees only
            Tmp.Clear();
            for (int i = 0; i < lods.Length - 1; i++)
                foreach (var r in lods[i].renderers)
                    if (r != null && r != last[0] && !Tmp.Contains(r)) Tmp.Add(r);
            if (Tmp.Count == 0) return;
            foreach (var r in Tmp) if (!r.enabled) return;   // someone else (G3TreeV2) owns the visibility
            var t = group.transform;
            var s = t.lossyScale;
            float size = group.size * Mathf.Max(Mathf.Abs(s.x), Mathf.Max(Mathf.Abs(s.y), Mathf.Abs(s.z)));
            var e = new Entry
            {
                group = group,
                geometry = Tmp.ToArray(),
                position = t.TransformPoint(group.localReferencePoint),
                k = TreeFarLod.Coefficient(size, lods[lods.Length - 2].screenRelativeTransitionHeight),
                scene = target.scene,
                on = true,
            };
            if (float.IsInfinity(e.k)) return;
            Hook();
            var cam = Camera.main;
            if (cam != null && !cam.orthographic)
            {
                float f = TreeFarLod.CameraFactor(cam.fieldOfView, QualitySettings.lodBias);
                float d = Vector3.Distance(cam.transform.position, e.position);
                if (!TreeFarLod.WantGeometry(false, d, e.k, f)) { Set(ref e, false); }
            }
            Entries.Add(e);
        }

        static void Hook()
        {
            if (_hooked) return;
            _hooked = true;
            SceneManager.sceneUnloaded -= OnSceneUnloaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
            if (_driver == null)
            {
                var go = new GameObject("G3 Tree Far LOD") { hideFlags = HideFlags.HideAndDontSave };
                Object.DontDestroyOnLoad(go);
                _driver = go.AddComponent<Driver>();
            }
        }

        static void OnSceneUnloaded(Scene scene)
        {
            for (int i = Entries.Count - 1; i >= 0; i--)
            {
                if (Entries[i].scene != scene) continue;
                if (!Entries[i].on) Off--;
                Entries[i] = Entries[Entries.Count - 1];
                Entries.RemoveAt(Entries.Count - 1);
            }
            if (_cursor >= Entries.Count) _cursor = 0;
        }

        static void Set(ref Entry e, bool on)
        {
            foreach (var r in e.geometry) if (r != null) r.enabled = on;
            Switched += e.geometry.Length;
            if (e.on && !on) Off++;
            else if (!e.on && on) Off--;
            e.on = on;
        }

        /// <summary>Everything back on (switch turned off, tests).</summary>
        public static void RestoreAll()
        {
            for (int i = 0; i < Entries.Count; i++)
            {
                var e = Entries[i];
                if (e.on || e.group == null || !e.group.enabled) continue;
                Set(ref e, true);
                Entries[i] = e;
            }
        }

        static void Sweep()
        {
            if (Entries.Count == 0) return;
            if (!Enabled) { if (Off > 0) RestoreAll(); return; }
            var cam = Camera.main;
            if (cam == null || cam.orthographic) return;
            using var _ = SweepMarker.Auto();
            var p = cam.transform.position;
            float f = TreeFarLod.CameraFactor(cam.fieldOfView, QualitySettings.lodBias);
            if (Mathf.Abs(f - _factor) > 0.005f * _factor) { _factor = f; _version++; }
            int n = Mathf.Min(SweepPerFrame, Entries.Count), switched = 0;
            for (int j = 0; j < n; j++)
            {
                if (_cursor >= Entries.Count) _cursor = 0;
                int i = _cursor++;
                var e = Entries[i];
                if (e.version != _version)
                {
                    // perf-2: thresholds cached per camera factor (was the full LOD maths per tree per frame)
                    float on = TreeFarLod.OnDistance(e.k, _factor), off = TreeFarLod.OffDistance(e.k, _factor);
                    float urgent = TreeFarLod.BillboardDistance(e.k, _factor) * 1.05f + 5f;
                    e.on2 = on * on; e.off2 = off * off; e.urgent2 = urgent * urgent; e.version = _version;
                    Entries[i] = e;
                }
                float dx = p.x - e.position.x, dy = p.y - e.position.y, dz = p.z - e.position.z;
                float d2 = dx * dx + dy * dy + dz * dz;
                bool want = e.on ? !(d2 > e.off2) : d2 < e.on2;
                if (want == e.on) continue;
                if (switched >= MaxSwitchesPerFrame && !(want && d2 < e.urgent2)) continue;
                if (e.group == null) continue;   // destroyed without a scene unload (removed on that scene's unload)
                if (!e.group.enabled) continue;   // replaced by a v2 model (G3TreeV2 owns its renderers meanwhile)
                Set(ref e, want);
                Entries[i] = e;
                switched++;
            }
        }

        sealed class Driver : MonoBehaviour
        {
            void Update() => Sweep();
        }
    }
}
