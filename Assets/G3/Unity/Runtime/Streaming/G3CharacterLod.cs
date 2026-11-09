using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace G3.Unity
{
    /// <summary>
    /// Distance LOD for the NPCs/creatures of the streamed cells (Docs/performance.md). Each cell holds 50–190
    /// skinned characters (≈75 bones each); with ~20 cells loaded that is ~2500 characters, of which 500–1000 are in
    /// the view frustum at any distance — legacy Animation sampling and skinning them cost 5–8 ms CPU and several ms GPU.
    /// <list type="bullet">
    /// <item>closer than <see cref="animateDistance"/>: fully animated (unchanged);</item>
    /// <item>up to <see cref="visibleDistance"/>: pose frozen (Animation disabled after one last sample) — idle loops of
    /// a 2–5 px tall figure are not noticeable; characters that move by their clip (G3Character.ApplyClipSpeed) keep animating;</item>
    /// <item>beyond: renderers off (a 1.8 m figure is ≤ 7 px tall at 1440p there).</item>
    /// </list>
    /// Time-sliced: a few hundred characters are re-evaluated per frame. Added automatically by <see cref="G3WorldStreamer"/>.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("G3/G3 Character LOD")]
    public sealed class G3CharacterLod : MonoBehaviour
    {
        public float animateDistance = 60f;
        public float visibleDistance = 250f;
        [Tooltip("Characters re-evaluated per frame.")]
        public int perFrame = 300;
        [Tooltip("At most this many characters change LOD state per frame (spreads the work when a cell arrives).")]
        public int maxChangesPerFrame = 40;

        sealed class Entry
        {
            public Transform t;
            public Animation anim;
            public Renderer[] renderers;
            public bool mover;
            public int band = -1;
        }

        readonly List<Entry> _chars = new List<Entry>();
        readonly List<Scene> _pending = new List<Scene>();
        readonly List<Characters.G3Character> _tmp = new List<Characters.G3Character>();
        int _cursor;

        public int Registered => _chars.Count;

        void OnEnable()
        {
            SceneManager.sceneLoaded += OnSceneLoaded;
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                if (s.isLoaded) _pending.Add(s);
            }
        }

        void OnDisable()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            // Leave everything animated/visible.
            foreach (var e in _chars) Apply(e, 0);
            _chars.Clear();
        }

        // sceneLoaded runs before the characters' Start (which starts their clip): register in LateUpdate.
        void OnSceneLoaded(Scene s, LoadSceneMode mode) => _pending.Add(s);

        void Register(Scene s)
        {
            if (!s.isLoaded) return;
            foreach (var root in s.GetRootGameObjects())
            {
                root.GetComponentsInChildren(true, _tmp);
                foreach (var c in _tmp)
                {
                    var anim = c.GetComponent<Animation>();
                    _chars.Add(new Entry
                    {
                        t = c.transform,
                        anim = anim,
                        renderers = System.Array.FindAll(c.GetComponentsInChildren<Renderer>(true), r => r.enabled),   // leave intentionally hidden parts alone
                        mover = c.ApplyClipSpeed,
                    });
                }
            }
        }

        static readonly global::Unity.Profiling.ProfilerMarker RegisterMarker = new global::Unity.Profiling.ProfilerMarker("G3CharacterLod.Register");

        void LateUpdate()
        {
            if (_pending.Count > 0)
            {
                using var _ = RegisterMarker.Auto();
                foreach (var s in _pending) Register(s);
                _pending.Clear();
                _cursor = 0;
            }
            var cam = Camera.main;
            if (cam == null || _chars.Count == 0) return;
            var p = cam.transform.position;
            float a2 = animateDistance * animateDistance, v2 = visibleDistance * visibleDistance;
            int n = Mathf.Min(perFrame, _chars.Count), changes = 0;
            for (int k = 0; k < n && changes < maxChangesPerFrame; k++)
            {
                if (_cursor >= _chars.Count) _cursor = 0;
                var e = _chars[_cursor];
                if (e.t == null)
                {
                    // Unloaded with its cell: swap-remove.
                    _chars[_cursor] = _chars[_chars.Count - 1];
                    _chars.RemoveAt(_chars.Count - 1);
                    if (_chars.Count == 0) break;
                    continue;
                }
                _cursor++;
                if (!e.t.gameObject.activeInHierarchy) continue;   // not woken yet (G3DeferredActivation) or hidden by gameplay
                float d2 = (e.t.position - p).sqrMagnitude;
                int band = d2 < a2 ? 0 : d2 < v2 ? 1 : 2;
                if (band != e.band) { Apply(e, band); changes++; }
            }
        }

        static void Apply(Entry e, int band)
        {
            if (e.t == null) return;
            int prev = e.band;
            e.band = band;
            if (e.anim != null)
            {
                bool animate = band == 0 || (band == 1 && e.mover);
                if (animate) e.anim.enabled = true;
                else if (e.anim.enabled)
                {
                    // Freeze in a valid pose (off-screen characters may never have been sampled: bind pose otherwise).
                    if (band == 1 && e.anim.isPlaying) e.anim.Sample();
                    e.anim.enabled = false;
                }
                else if (band == 1 && prev == 2 && e.anim.isPlaying) e.anim.Sample();
            }
            bool visible = band < 2;
            if (prev < 0 || (prev < 2) != visible)
                foreach (var r in e.renderers) if (r != null) r.enabled = visible;
        }
    }
}
