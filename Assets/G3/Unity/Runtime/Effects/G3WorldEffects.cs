using System.Collections.Generic;
using UnityEngine;

namespace G3.Unity
{
    /// <summary>
    /// Streams the world effects around the camera through <see cref="G3EffectSystem"/>: a placement starts when the camera
    /// comes within its radius and stops (its particles fade out) a few metres beyond; at most <see cref="maxActive"/> at once,
    /// nearest first. Created on first use by the effect system when the baked asset exists. Placements of a story sector
    /// that is off (<see cref="G3StorySectors"/>) are not shown; switched ones change on the next update.
    /// </summary>
    public sealed class G3WorldEffects : MonoBehaviour
    {
        public G3WorldEffectsAsset data;
        public float updateInterval = 0.4f;
        public int maxActive = 120;
        public float hysteresis = 5f;
        const float Cell = 32f;

        readonly Dictionary<long, List<int>> _grid = new Dictionary<long, List<int>>();
        readonly Dictionary<int, int> _active = new Dictionary<int, int>();   // placement → handle
        readonly List<(float d, int i)> _want = new List<(float, int)>();
        readonly HashSet<int> _wantSet = new HashSet<int>();
        readonly List<int> _drop = new List<int>();
        float _maxRadius, _next;

        public int Active => _active.Count;

        /// <summary>Adds the streamer when the baked asset exists (called by GameBootstrap or tools).</summary>
        public static G3WorldEffects Ensure()
        {
            var existing = FindAnyObjectByType<G3WorldEffects>();
            if (existing != null) return existing;
            var data = Resources.Load<G3WorldEffectsAsset>(G3WorldEffectsAsset.ResourcePath);
            if (data == null) return null;
            var w = new GameObject("G3 World Effects").AddComponent<G3WorldEffects>();
            w.data = data;
            return w;
        }

        void Start()
        {
            if (data == null) data = Resources.Load<G3WorldEffectsAsset>(G3WorldEffectsAsset.ResourcePath);
            if (data == null) { enabled = false; return; }
            for (int i = 0; i < data.placements.Count; i++)
            {
                var p = data.placements[i];
                _maxRadius = Mathf.Max(_maxRadius, p.radius);
                long k = Key(Mathf.FloorToInt(p.position.x / Cell), Mathf.FloorToInt(p.position.z / Cell));
                if (!_grid.TryGetValue(k, out var list)) _grid[k] = list = new List<int>();
                list.Add(i);
            }
        }

        void OnDisable()
        {
            foreach (var h in _active.Values) G3EffectPlayer.Stop(h);
            _active.Clear();
        }

        void Update()
        {
            if (Time.unscaledTime < _next || data == null) return;
            _next = Time.unscaledTime + updateInterval;
            var cam = Camera.main;
            if (cam == null || G3EffectSystem.Instance == null) return;
            var c = cam.transform.position;

            _want.Clear();
            _wantSet.Clear();
            int r = Mathf.CeilToInt((_maxRadius + hysteresis) / Cell);
            int cx = Mathf.FloorToInt(c.x / Cell), cz = Mathf.FloorToInt(c.z / Cell);
            for (int x = cx - r; x <= cx + r; x++)
            for (int z = cz - r; z <= cz + r; z++)
            {
                if (!_grid.TryGetValue(Key(x, z), out var list)) continue;
                foreach (int i in list)
                {
                    var p = data.placements[i];
                    if (!G3StorySectors.IsOn(p.sector, p.enabledAtStart)) continue; // its story sector is off
                    float d = (p.position - c).magnitude;
                    float limit = p.radius + (_active.ContainsKey(i) ? hysteresis : 0f);
                    if (d < limit) _want.Add((d, i));
                }
            }
            _want.Sort((a, b) => a.d.CompareTo(b.d));
            for (int k = 0; k < _want.Count && k < maxActive; k++) _wantSet.Add(_want[k].i);

            _drop.Clear();
            foreach (var kv in _active) if (!_wantSet.Contains(kv.Key)) _drop.Add(kv.Key);
            foreach (int i in _drop)
            {
                G3EffectPlayer.Stop(_active[i]);
                _active.Remove(i);
            }
            for (int k = 0; k < _want.Count && k < maxActive; k++) // nearest first
            {
                int i = _want[k].i;
                if (_active.ContainsKey(i)) continue;
                if (!G3EffectSystem.Instance.HasAmbientRoom) break; // fix-58: the gameplay reserve stays free (retried next update)
                var p = data.placements[i];
                int h = p.template
                    ? G3EffectSystem.Instance.SpawnTemplate(p.name, p.position, p.rotation)
                    : G3EffectPlayer.Start(p.name, p.position, p.rotation, null, withSound: false);
                if (h != 0) _active[i] = h;
            }
        }

        static long Key(int x, int z) => ((long)x << 32) ^ (uint)z;
    }
}
