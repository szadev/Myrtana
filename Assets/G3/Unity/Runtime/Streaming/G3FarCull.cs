using System.Collections.Generic;
using System.Reflection;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace G3.Unity
{
    /// <summary>
    /// perf-2 (Docs/performance.md, "Far objects: no shadow views, no culling"): keeps far world objects out of the GPU
    /// Resident Drawer's per-view work (<see cref="FarCull"/>). Every mesh renderer of the streamed cells is registered when
    /// its content wakes (<see cref="G3RenderTuning"/>); a sweep checks a slice of them per frame:
    /// <list type="bullet">
    /// <item>beyond the shadow distance plus its shadow length the renderer stops casting (ShadowCastingMode Off: the drawer
    /// skips it for the sun's cascades and every point-light face);</item>
    /// <item>a renderer without a LODGroup beyond the drawer's own small-mesh distance is not rendered at all
    /// (Renderer.forceRenderingOff: no view tests it).</item>
    /// </list>
    /// Only the shadow mode and forceRenderingOff are touched (not <c>enabled</c>, which G3TreeV2 / sector content own), and
    /// the original shadow mode is put back. Switches are capped per frame (each is a drawer update) except when the camera
    /// is already inside the distance where the object must show. <c>-g3farcull 0</c> = off (A/B).
    /// </summary>
    public static class G3FarCull
    {
        /// <summary>Master switch (<c>-g3farcull 0</c> = off).</summary>
        public static bool Enabled = true;
        /// <summary>Hiding of small far objects (<c>-g3farcull 2</c> = shadows only).</summary>
        public static bool HideSmall = true;
        /// <summary>Renderers checked per frame (the check is a squared distance against cached thresholds).</summary>
        public static int SweepPerFrame = 5000;
        /// <summary>Renderer changes per frame at most when not urgent.</summary>
        public static int MaxChangesPerFrame = 256;

        struct Entry
        {
            public Renderer renderer;
            public Scene scene;
            public Vector3 center;          // world bounds centre at registration (cell content is static)
            public float size;              // largest bounds extent x2 (the drawer's worldSpaceSize)
            public ShadowCastingMode shadowMode;   // original
            public bool canShadow;          // original mode casts
            public bool canHide;            // no LODGroup (the drawer's small-mesh culling applies)
            public bool transparent;        // a blended material: not drawn by the drawer, no small-mesh culling of its own
            public bool shadowOn;
            public bool rendered;
            public bool dead;               // destroyed without its scene unloading
            // squared distance thresholds for the parameters of _version (camera factor, shadow distance, small mesh %)
            public int version;
            public float shOn, shOff, shUrgent, rnOn, rnOff, rnUrgent;
        }

        static Entry[] _entries = new Entry[1024];
        static int _count, _cursor, _version = 1;
        static float _f = -1f, _shadowDistance = -1f, _small = -1f;
        static readonly HashSet<Renderer> LodRenderers = new HashSet<Renderer>();
        static readonly List<LODGroup> Groups = new List<LODGroup>();
        static readonly ProfilerMarker SweepMarker = new ProfilerMarker("G3FarCull.Sweep");
        static readonly ProfilerMarker RegisterMarker = new ProfilerMarker("G3FarCull.Register");
        static Driver _driver;
        static PropertyInfo _shadowDistanceProp, _smallMeshProp;
        static Object _pipeline;

        /// <summary>Registered renderers / with shadows off / not rendered / changes so far (runner, perf probe).</summary>
        public static int Count => _count;
        public static int ShadowsOff { get; private set; }
        public static int Hidden { get; private set; }
        public static long Changes { get; private set; }

        public static string Describe() => Enabled
            ? $"far cull: {_count} renderers, {ShadowsOff} shadows off, {Hidden} hidden, {Changes} changes"
            : "far cull off";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Reset()
        {
            _entries = new Entry[1024];
            _count = 0;
            _cursor = 0;
            _version = 1;
            _f = _shadowDistance = _small = -1f;
            ShadowsOff = 0;
            Hidden = 0;
            Changes = 0;
            _driver = null;
            _pipeline = null;
            var args = System.Environment.GetCommandLineArgs();
            int i = System.Array.IndexOf(args, "-g3farcull");
            string v = i >= 0 && i + 1 < args.Length ? args[i + 1] : "1";
            Enabled = v != "0";
            HideSmall = v != "2";
        }

        /// <summary>
        /// Registers the mesh renderers of a woken cell root / deferred target (before the drawer sees them this frame,
        /// so far ones arrive already culled). Renderers of LODGroups only get the shadow rule.
        /// </summary>
        public static void Register(GameObject root, List<MeshRenderer> renderers)
        {
            if (!Enabled || !Application.isPlaying || root == null || renderers == null || renderers.Count == 0) return;
            using var _ = RegisterMarker.Auto();
            Hook();
            LodRenderers.Clear();
            root.GetComponentsInChildren(true, Groups);
            foreach (var g in Groups)
                foreach (var lod in g.GetLODs())
                    foreach (var r in lod.renderers)
                        if (r != null) LodRenderers.Add(r);
            Groups.Clear();
            var cam = Camera.main;
            bool haveCam = cam != null && !cam.orthographic;
            if (haveCam) UpdateParameters(cam);
            Vector3 p = haveCam ? cam.transform.position : Vector3.zero;
            var scene = root.scene;
            for (int i = 0; i < renderers.Count; i++)
            {
                var r = renderers[i];
                if (r == null || r.forceRenderingOff) continue;
                var b = r.bounds;
                var ext = b.extents;
                var e = new Entry
                {
                    renderer = r,
                    scene = scene,
                    center = b.center,
                    size = 2f * Mathf.Max(ext.x, Mathf.Max(ext.y, ext.z)),
                    shadowMode = r.shadowCastingMode,
                    shadowOn = true,
                    rendered = true,
                };
                e.canShadow = e.shadowMode != ShadowCastingMode.Off;
                Classify(r, out e.transparent, out bool never);
                e.canHide = !never && !LodRenderers.Contains(r);
                if (!e.canShadow && !e.canHide) continue;
                if (haveCam) Apply(ref e, p, int.MaxValue);
                if (_count == _entries.Length) System.Array.Resize(ref _entries, _count * 2);
                _entries[_count++] = e;
            }
            LodRenderers.Clear();
        }

        static readonly List<Material> Materials = new List<Material>();

        /// <summary>Shader tag of materials whose renderers are never hidden (G3/MagicBarrier: the 360 m Vengard dome, fix-229).</summary>
        public const string NeverTag = "G3FarCull";

        /// <summary>
        /// transparent: a blended material (render queue above 2500); never: a material whose shader is tagged
        /// <c>G3FarCull = Never</c> (the renderer keeps only the shadow rule).
        /// </summary>
        static void Classify(Renderer r, out bool transparent, out bool never)
        {
            r.GetSharedMaterials(Materials);
            transparent = never = false;
            foreach (var m in Materials)
            {
                if (m == null) continue;
                if (m.renderQueue > 2500) transparent = true;
                if (m.GetTag(NeverTag, false) == "Never") never = true;
            }
            Materials.Clear();
        }

        static void Hook()
        {
            if (_driver != null) return;
            SceneManager.sceneUnloaded -= OnSceneUnloaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
            var go = new GameObject("G3 Far Cull") { hideFlags = HideFlags.HideAndDontSave };
            Object.DontDestroyOnLoad(go);
            _driver = go.AddComponent<Driver>();
        }

        static void OnSceneUnloaded(Scene scene)
        {
            for (int i = _count - 1; i >= 0; i--)
            {
                if (_entries[i].scene != scene) continue;
                if (!_entries[i].shadowOn) ShadowsOff--;
                if (!_entries[i].rendered) Hidden--;
                _entries[i] = _entries[--_count];
                _entries[_count] = default;
            }
            if (_cursor >= _count) _cursor = 0;
        }

        /// <summary>
        /// Reads the camera factor, the URP asset's shadow distance and small-mesh percentage (by reflection: G3.Unity does
        /// not reference URP); a change of more than 1 % invalidates every entry's cached thresholds.
        /// </summary>
        static void UpdateParameters(Camera cam)
        {
            var rp = GraphicsSettings.currentRenderPipeline;
            if (rp != _pipeline)
            {
                _pipeline = rp;
                var t = rp != null ? rp.GetType() : null;
                _shadowDistanceProp = t?.GetProperty("shadowDistance");
                _smallMeshProp = t?.GetProperty("smallMeshScreenPercentage");
            }
            float f = TreeFarLod.CameraFactor(cam.fieldOfView, QualitySettings.lodBias);
            float sd = _shadowDistanceProp != null && rp != null && _shadowDistanceProp.GetValue(rp) is float d ? d : 50f;
            float sm = HideSmall && _smallMeshProp != null && rp != null && _smallMeshProp.GetValue(rp) is float v ? v : 0f;
            if (Differs(f, _f) || Differs(sd, _shadowDistance) || Differs(sm, _small))
            {
                _f = f; _shadowDistance = sd; _small = sm;
                _version++;
            }
        }

        static bool Differs(float a, float b) => Mathf.Abs(a - b) > 0.01f * Mathf.Max(Mathf.Abs(b), 1e-3f) || (a == 0f) != (b == 0f);

        static void Thresholds(ref Entry e)
        {
            e.version = _version;
            float on = FarCull.ShadowOnDistance(e.size, _shadowDistance);
            float off = FarCull.ShadowOffDistance(e.size, _shadowDistance);
            float urgent = on - FarCull.ShadowMarginMetres * 0.5f;
            e.shOn = on * on; e.shOff = off * off; e.shUrgent = urgent * urgent;
            float k = FarCull.SmallMeshCoefficient(e.size, e.transparent ? FarCull.TransparentPercentage(_small) : _small);
            if (float.IsInfinity(k) || !e.canHide) { e.rnOn = e.rnOff = e.rnUrgent = float.PositiveInfinity; return; }
            float ron = TreeFarLod.OnDistance(k, _f), roff = TreeFarLod.OffDistance(k, _f);
            float rurg = TreeFarLod.BillboardDistance(k, _f) * 1.05f + 5f;
            e.rnOn = ron * ron; e.rnOff = roff * roff; e.rnUrgent = rurg * rurg;
        }

        /// <summary>Applies the wanted state to <paramref name="e"/>; returns the number of renderer changes (0-2).</summary>
        static int Apply(ref Entry e, Vector3 cam, int budget)
        {
            if (e.version != _version) Thresholds(ref e);
            float dx = e.center.x - cam.x, dy = e.center.y - cam.y, dz = e.center.z - cam.z;
            float d2 = dx * dx + dy * dy + dz * dz;
            int changes = 0;
            if (e.canShadow)
            {
                bool want = e.shadowOn ? !(d2 > e.shOff) : d2 < e.shOn;
                if (want != e.shadowOn && (budget > 0 || (want && d2 < e.shUrgent)))
                {
                    if (e.renderer == null) { e.dead = true; return 0; }
                    e.renderer.shadowCastingMode = want ? e.shadowMode : ShadowCastingMode.Off;
                    e.shadowOn = want;
                    ShadowsOff += want ? -1 : 1;
                    changes++;
                    budget--;
                }
            }
            if (e.canHide)
            {
                bool want = e.rendered ? !(d2 > e.rnOff) : d2 < e.rnOn;
                if (want != e.rendered && (budget > 0 || (want && d2 < e.rnUrgent)))
                {
                    if (e.renderer == null) { e.dead = true; return changes; }
                    e.renderer.forceRenderingOff = !want;
                    e.rendered = want;
                    Hidden += want ? -1 : 1;
                    changes++;
                }
            }
            return changes;
        }

        /// <summary>Everything back as built (switch turned off).</summary>
        public static void RestoreAll()
        {
            for (int i = 0; i < _count; i++)
            {
                ref var e = ref _entries[i];
                if (e.dead || e.renderer == null) continue;
                if (!e.shadowOn) { e.renderer.shadowCastingMode = e.shadowMode; e.shadowOn = true; ShadowsOff--; Changes++; }
                if (!e.rendered) { e.renderer.forceRenderingOff = false; e.rendered = true; Hidden--; Changes++; }
            }
        }

        static void Sweep()
        {
            if (_count == 0) return;
            if (!Enabled) { if (ShadowsOff > 0 || Hidden > 0) RestoreAll(); return; }
            var cam = Camera.main;
            if (cam == null || cam.orthographic) return;
            using var _ = SweepMarker.Auto();
            UpdateParameters(cam);
            var p = cam.transform.position;
            int n = Mathf.Min(SweepPerFrame, _count), changes = 0;
            var entries = _entries;
            for (int j = 0; j < n; j++)
            {
                if (_cursor >= _count) _cursor = 0;
                ref var e = ref entries[_cursor++];
                if (e.dead) continue;
                changes += Apply(ref e, p, MaxChangesPerFrame - changes);
            }
            Changes += changes;
        }

        sealed class Driver : MonoBehaviour
        {
            void Update() => Sweep();
        }
    }
}
