using System.Collections.Generic;
using G3.Effects;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace G3.Unity
{
    /// <summary>
    /// Point/spot light budget for the streamed world (Docs/performance.md): distance LOD and shadows.
    /// <para><b>Distance:</b> the world's own lights (a GameObject with only the Light, as the world importer builds them)
    /// fade out between <see cref="lightDistance"/> − 10 m and <see cref="lightDistance"/> (camera to their range sphere)
    /// and are disabled beyond (<see cref="LightLod"/>: G3 lit each object with its nearest four static lights and culled
    /// far objects, so far lights showed nothing). Since the mesh-less .lrentdat lights joined the world (5026 lights,
    /// ≈ 360 in Faring) every enabled light in view went through URP's Forward+ light loop: +4.6 ms in Faring
    /// (editor-runner-11). Effect, torch and item lights are not touched (their own components drive them).</para>
    /// <para><b>Shadows:</b> the game data flags almost every torch/fire light as shadow casting; every shadowed point
    /// light costs six extra culling + shadow-map passes over the whole scene. The imported flag is kept, but only the
    /// <see cref="maxShadowedLights"/> nearest shadow-flagged lights within <see cref="shadowDistance"/> actually cast
    /// shadows (the ones whose shadows you can see), re-evaluated a few times per second. perf-4
    /// (<see cref="PointShadowRules"/>): only lights bright enough for a visible shadow (not the dim blue-grey night fills)
    /// whose range touches the view, within 12 m (was 20); a shadow fades in / out over 0.4 s and a new one waits until
    /// the one it replaces has faded, so no more than <see cref="maxShadowedLights"/> ever render.</para>
    /// <para><b>Fire lights</b> (fire-fx): world lights in the fire palette (<see cref="FireLightRules"/>: torches, camp
    /// fires, candles, braziers) get <see cref="FireLightRules.IntensityGain"/> × their imported intensity and, within
    /// <see cref="flickerDistance"/> m of their range sphere, a per-light flicker.</para>
    /// Added automatically by <see cref="G3WorldStreamer"/>.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("G3/G3 Light Shadow Budget")]
    public sealed class G3LightShadowBudget : MonoBehaviour
    {
        [Tooltip("Shadow-casting point/spot lights at a time (each point light = 6 shadow views). 0 = none.")]
        [Range(0, 8)] public int maxShadowedLights = 2;
        [Tooltip("Only lights whose range reaches within this distance (m) of the camera can get shadows.")]
        public float shadowDistance = PointShadowRules.DefaultDistance;
        [Tooltip("World lights whose range sphere is farther than this (m) from the camera are off (fading over the last 10 m). 0 = never.")]
        public float lightDistance = LightLod.DefaultDistance;
        [Tooltip("A candidate replaces a currently shadowed light only if it is closer by this factor (avoids flicker).")]
        [Range(0.5f, 1f)] public float hysteresis = 0.8f;
        public float updateInterval = 0.25f;
        [Tooltip("Fire lights whose range sphere is within this distance (m) of the camera flicker. 0 = no flicker.")]
        public float flickerDistance = 25f;

        sealed class Entry
        {
            public Light light;
            public Scene scene;       // perf-5: the cell it came with (dropped on sceneUnloaded without touching the light)
            public LightShadows wanted;
            public bool on;           // shadows on
            public bool lod;          // a world light under distance LOD
            public float intensity;   // its imported intensity
            public float factor = 1f; // current LOD factor
            public float target = 1f; // LOD factor it fades to
            public bool fire;         // fire palette: brighter, flickers
            public float phase;       // flicker phase (s)
            public bool bright;       // bright enough for a visible shadow (PointShadowRules.Bright)
            public bool want;         // selected for a shadow (on = its shadow is enabled, possibly still fading)
            public bool fadingShadow; // in _shadowFading
            public float shadowFade;  // 0..1 shadow strength factor
            public float strength;    // imported shadow strength
            public bool fixedPos;     // a world light: never moves, position cached
            public Vector3 pos;
        }

        readonly List<Entry> _lights = new List<Entry>();
        readonly List<Entry> _fading = new List<Entry>();
        readonly List<Entry> _flickering = new List<Entry>();
        readonly List<Entry> _shadowFading = new List<Entry>();
        readonly Plane[] _planes = new Plane[6];
        int _withShadows;   // lights whose shadow is enabled (fading in, on, fading out)
        readonly HashSet<Light> _known = new HashSet<Light>();   // registered once (a scene can be seen twice: OnEnable + sceneLoaded)
        readonly List<(Entry e, float d)> _cand = new List<(Entry, float)>();
        float _next;
        bool _dirty;

        public int Registered => _lights.Count;
        /// <summary>World lights currently on (LOD target above 0).</summary>
        public int LitWorldLights { get; private set; }
        /// <summary>Lights whose shadow is enabled now (fading ones included).</summary>
        public int ShadowedLights => _withShadows;

        void OnEnable()
        {
            // Player command line "-g3pointshadows N" (A/B: each shadowed point light is 6 more culling views) and the
            // Performance options (shadow quality, high detail distance) through G3RenderTuning.
            if (_defaultShadowed < 0) { _defaultShadowed = maxShadowedLights; _defaultDistance = lightDistance; }
            ApplyTuning();
            G3RenderTuning.Changed += ApplyTuning;
            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
            // Lights already present (master scene, cells opened in the editor before Play).
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                if (s.isLoaded) Register(s);
            }
        }

        void OnDisable()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneUnloaded -= OnSceneUnloaded;
            G3RenderTuning.Changed -= ApplyTuning;
        }

        int _defaultShadowed = -1;
        float _defaultDistance;

        void ApplyTuning()
        {
            var o = G3RenderTuning.Options;
            maxShadowedLights = o.PointShadows >= 0 ? o.PointShadows : _defaultShadowed;
            lightDistance = o.LightDistance >= 0 ? o.LightDistance : _defaultDistance;
            _dirty = true; // re-evaluate on the next update
        }

        void OnSceneLoaded(Scene s, LoadSceneMode mode) => Register(s);

        /// <summary>
        /// perf-5: the lights of an unloaded cell go here, by scene: a null check of a destroyed scene object asks the
        /// persistent manager, which the loading thread holds while the next cell streams in (the next update's checks of
        /// ~100 gone lights stalled the main thread for up to 400 ms in the editor).
        /// </summary>
        void OnSceneUnloaded(Scene s)
        {
            for (int i = _lights.Count - 1; i >= 0; i--)
            {
                var e = _lights[i];
                if (e.scene != s) continue;
                if (e.on) { e.on = false; _withShadows--; }
                _known.Remove(e.light);
                _fading.Remove(e);
                _flickering.Remove(e);
                if (e.fadingShadow) { e.fadingShadow = false; _shadowFading.Remove(e); }
                _lights.RemoveAt(i);
            }
        }

        readonly List<Light> _tmp = new List<Light>();
        readonly List<MonoBehaviour> _behaviours = new List<MonoBehaviour>();

        public void Register(Scene s)
        {
            foreach (var root in s.GetRootGameObjects())
            {
                root.GetComponentsInChildren(true, _tmp);
                foreach (var l in _tmp)
                {
                    if (l.type == LightType.Directional || _known.Contains(l)) continue;
                    bool lod = IsWorldLight(l);
                    if (l.shadows == LightShadows.None && !lod) continue;   // nothing to manage
                    _known.Add(l);
                    var c = l.color;
                    bool fire = lod && FireLightRules.IsFireColor(c.r, c.g, c.b);
                    if (fire) l.intensity *= FireLightRules.IntensityGain;
                    _lights.Add(new Entry
                    {
                        light = l, scene = s, wanted = l.shadows, lod = lod, intensity = l.intensity, fire = fire, phase = Random.value * 10f,
                        bright = PointShadowRules.Bright(l.intensity, c.r, c.g, c.b), strength = l.shadowStrength,
                        fixedPos = lod, pos = l.transform.position,
                    });
                    l.shadows = LightShadows.None;
                }
            }
            _dirty = true;
        }

        /// <summary>A light as the world importer builds it: nothing on its GameObject drives it (URP's light data aside).</summary>
        bool IsWorldLight(Light l)
        {
            l.GetComponents(_behaviours);
            foreach (var b in _behaviours)
                if (b != null && b.GetType().Name != "UniversalAdditionalLightData") return false;
            return true;
        }

        void Update()
        {
            FadeStep();
            FlickerStep();
            ShadowFadeStep();
            if (!_dirty && Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + updateInterval;
            _dirty = false;
            var cam = Camera.main;
            if (cam == null) return;
            var p = cam.transform.position;
            GeometryUtility.CalculateFrustumPlanes(cam, _planes);

            _cand.Clear();
            _flickering.Clear();
            int lit = 0;
            for (int i = _lights.Count - 1; i >= 0; i--)
            {
                var e = _lights[i];
                if (e.light == null) { DropShadow(e); _known.Remove(e.light); _lights.RemoveAt(i); continue; }
                if (!e.light.gameObject.activeInHierarchy) { DropShadow(e); continue; }
                var lp = e.fixedPos ? e.pos : e.light.transform.position;
                float range = e.light.range;
                float d = Vector3.Distance(p, lp) - range;
                if (e.lod)
                {
                    float t = LightLod.Factor(d, lightDistance);
                    if (t != e.target)
                    {
                        if (e.target == e.factor) _fading.Add(e);   // not fading yet
                        e.target = t;
                    }
                    if (t > 0f) lit++;
                    if (e.fire && t > 0f && flickerDistance > 0f && d < flickerDistance) _flickering.Add(e);
                }
                if (e.wanted == LightShadows.None) continue;
                if (d > shadowDistance || maxShadowedLights <= 0 || !e.bright || (e.lod && e.target <= 0f) ||
                    !GeometryUtility.TestPlanesAABB(_planes, new Bounds(lp, Vector3.one * (2f * (range + PointShadowRules.ViewMargin)))))
                {
                    Want(e, false);
                    continue;
                }
                // Currently shadowed lights get a bonus so near-equal candidates don't swap back and forth.
                _cand.Add((e, e.want ? d - Mathf.Abs(d) * (1f - hysteresis) - 0.5f : d));
            }
            LitWorldLights = lit;
            _cand.Sort((a, b) => a.d.CompareTo(b.d));
            for (int i = 0; i < _cand.Count; i++) Want(_cand[i].e, i < maxShadowedLights);
        }

        /// <summary>Selects or deselects a light for a shadow; <see cref="ShadowFadeStep"/> fades it in or out.</summary>
        void Want(Entry e, bool want)
        {
            if (e.want == want) return;
            e.want = want;
            if (!e.fadingShadow) { e.fadingShadow = true; _shadowFading.Add(e); }
        }

        /// <summary>A light gone or switched off: its shadow goes at once (nothing of it is visible).</summary>
        void DropShadow(Entry e)
        {
            e.want = false;
            e.shadowFade = 0f;
            if (e.on) SetShadows(e, false);
        }

        /// <summary>
        /// Shadows of selected lights fade in, deselected ones fade out (shadow strength over
        /// <see cref="PointShadowRules.FadeSeconds"/>); a selected light waits while the budget is full of shadows that are
        /// still fading out.
        /// </summary>
        void ShadowFadeStep()
        {
            if (_shadowFading.Count == 0) return;
            float dt = Time.unscaledDeltaTime;
            // fade-outs first (pass 0): a slot they give back can be taken in the same frame (pass 1)
            for (int pass = 0; pass < 2; pass++)
                for (int i = _shadowFading.Count - 1; i >= 0; i--)
                {
                    var e = _shadowFading[i];
                    if (e.light == null || (!e.want && !e.on)) { DropShadow(e); Finish(i); continue; }
                    if (e.want != (pass == 1)) continue;
                    if (e.want && !e.on)
                    {
                        if (!PointShadowRules.MayStart(_withShadows, maxShadowedLights)) continue;
                        e.shadowFade = 0f;
                        SetShadows(e, true);
                    }
                    e.shadowFade = PointShadowRules.Fade(e.shadowFade, e.want, dt);
                    e.light.shadowStrength = e.strength * e.shadowFade;
                    if (!e.want && e.shadowFade <= 0f) { SetShadows(e, false); Finish(i); }
                    else if (e.want && e.shadowFade >= 1f) Finish(i);
                }
        }

        void Finish(int i)
        {
            var e = _shadowFading[i];
            e.fadingShadow = false;
            int last = _shadowFading.Count - 1;
            _shadowFading[i] = _shadowFading[last];
            _shadowFading.RemoveAt(last);
        }

        /// <summary>World lights between two LOD factors move to the target every frame (a full fade takes 0.5 s).</summary>
        void FadeStep()
        {
            if (_fading.Count == 0) return;
            float step = Time.unscaledDeltaTime * 2f;
            for (int i = _fading.Count - 1; i >= 0; i--)
            {
                var e = _fading[i];
                if (e.light == null) { _fading.RemoveAt(i); continue; }
                e.factor = Mathf.MoveTowards(e.factor, e.target, step);
                e.light.intensity = e.intensity * e.factor * (e.fire ? FireLightRules.Flicker(Time.time, e.phase) : 1f);
                bool enabled = e.factor > 0f;
                if (e.light.enabled != enabled) e.light.enabled = enabled;
                if (e.factor == e.target) _fading.RemoveAt(i);
            }
        }

        /// <summary>Near fire lights: intensity × flicker every frame (a few dozen lights at most).</summary>
        void FlickerStep()
        {
            float t = Time.time;
            for (int i = 0; i < _flickering.Count; i++)
            {
                var e = _flickering[i];
                if (e.light == null || e.factor <= 0f) continue;
                e.light.intensity = e.intensity * e.factor * FireLightRules.Flicker(t, e.phase);
            }
        }

        void SetShadows(Entry e, bool on)
        {
            if (e.on == on) return;
            e.on = on;
            _withShadows += on ? 1 : -1;
            if (e.light == null) return;
            e.light.shadows = on ? e.wanted : LightShadows.None;
            if (!on) e.light.shadowStrength = e.strength;
        }
    }
}
