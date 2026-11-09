using System;
using System.Collections.Generic;
using Myrtana.Rules;
using Myrtana.Session;
using UnityEngine;

namespace Myrtana.Unity
{
    /// <summary>
    /// Shows the world items (<see cref="WorldItemDef"/>) that are still lying around near the hero and finds the one he
    /// can pick up. Views are disposable: they are created within <see cref="showRadius"/>, destroyed beyond
    /// <see cref="hideRadius"/> or when the item is taken; the state lives in <see cref="WorldItems"/>.
    /// </summary>
    public sealed class WorldItemViews : MonoBehaviour, ISessionView
    {
        const float CellSize = 16f;

        [Tooltip("Items closer than this (m) are shown.")] public float showRadius = 40f;
        [Tooltip("Shown items farther than this (m) are removed.")] public float hideRadius = 50f;
        [Tooltip("Seconds between visibility updates.")] public float updateInterval = 0.5f;

        readonly Dictionary<(int, int), List<WorldItemDef>> _grid = new Dictionary<(int, int), List<WorldItemDef>>();
        readonly Dictionary<string, GameObject> _shown = new Dictionary<string, GameObject>(StringComparer.Ordinal);
        Transform _player;
        ItemVisualCatalog _catalog;
        GameSession _session;
        float _nextUpdate;

        public void Init(Transform player, ItemVisualCatalog catalog)
        {
            _player = player;
            _catalog = catalog;
        }

        public void Bind(GameSession session)
        {
            if (_session != null)
            {
                _session.Items.World.Removed -= Hide;
                _session.Items.World.Dropped -= OnDropped;
            }
            foreach (var view in _shown.Values) Destroy(view);
            _shown.Clear();
            _session = session;
            session.Items.World.Removed += Hide;
            session.Items.World.Dropped += OnDropped;
            if (_grid.Count == 0) BuildGrid(session);
            _nextUpdate = 0;
        }

        /// <summary>The world item id of a view's collider, or null.</summary>
        public static string IdOf(Collider collider) => collider != null && collider.TryGetComponent<WorldItemView>(out var v) ? v.Id : null;

        public ShownSet<GameObject> Shown => new ShownSet<GameObject>(_shown);

        void BuildGrid(GameSession session)
        {
            foreach (var def in session.Content.Database.All<WorldItemDef>())
            {
                var key = Cell(def.Position[0], def.Position[2]);
                if (!_grid.TryGetValue(key, out var list)) _grid[key] = list = new List<WorldItemDef>();
                list.Add(def);
            }
        }

        static (int, int) Cell(float x, float z) => ((int)Mathf.Floor(x / CellSize), (int)Mathf.Floor(z / CellSize));

        void Update()
        {
            if (_session == null || _player == null || Time.unscaledTime < _nextUpdate) return;
            _nextUpdate = Time.unscaledTime + updateInterval;
            var p = _player.position;
            HideFar(p);
            ShowNear(p);
        }

        void HideFar(Vector3 p)
        {
            List<string> far = null;
            foreach (var kv in _shown)
                if (kv.Value == null || (kv.Value.transform.position - p).sqrMagnitude > hideRadius * hideRadius)
                    (far ?? (far = new List<string>())).Add(kv.Key);
            if (far != null) foreach (var id in far) Hide(id);
        }

        void OnDestroy()
        {
            if (_session == null) return;
            _session.Items.World.Removed -= Hide;
            _session.Items.World.Dropped -= OnDropped;
        }

        /// <summary>An item dropped during the game (a knocked-out fighter's weapon) shows at once when it is near.</summary>
        void OnDropped(DroppedItem d)
        {
            if (_player == null || _shown.ContainsKey(d.Id)) return;
            var position = new Vector3(d.Position[0], d.Position[1], d.Position[2]);
            if ((position - _player.position).sqrMagnitude <= showRadius * showRadius) _shown[d.Id] = Create(d, position);
        }

        void ShowNear(Vector3 p)
        {
            foreach (var d in _session.Items.World.AllDropped)
            {
                if (_shown.ContainsKey(d.Id)) continue;
                var position = new Vector3(d.Position[0], d.Position[1], d.Position[2]);
                if ((position - p).sqrMagnitude <= showRadius * showRadius) _shown[d.Id] = Create(d, position);
            }
            int r = Mathf.CeilToInt(showRadius / CellSize);
            var (cx, cz) = Cell(p.x, p.z);
            for (int x = cx - r; x <= cx + r; x++)
            for (int z = cz - r; z <= cz + r; z++)
            {
                if (!_grid.TryGetValue((x, z), out var list)) continue;
                foreach (var def in list)
                {
                    if (_shown.ContainsKey(def.Id) || !_session.Items.World.IsPresent(def.Id)) continue;
                    var position = new Vector3(def.Position[0], def.Position[1], def.Position[2]);
                    if ((position - p).sqrMagnitude <= showRadius * showRadius) _shown[def.Id] = Create(def, position);
                }
            }
        }

        GameObject Create(WorldItemDef def, Vector3 position)
        {
            var go = Create(def.Id, def.Item, position, Quaternion.LookRotation(Vector(def.Forward), Vector(def.Up)), def.Scale);
            global::G3.Unity.G3ReliefGrounded.Place(go);   // G3 put it on its collision ground: shown on the drawn relief (remaster-world 14)
            return go;
        }

        /// <summary>
        /// An item dropped during the game (a downed fighter's weapon) rests flat on the ground across the dropper's facing
        /// (<see cref="WorldItemRest"/>, fix-75: G3's physics settles it on its widest side; the old fixed Euler(90, yaw+90, 0)
        /// stood G3 blades, spears and bows upright, their mesh's long axis being local X or Z).
        /// </summary>
        GameObject Create(DroppedItem d, Vector3 position)
        {
            if (d.Rotation != null)
            {
                // fix-195: a spent arrow or bolt stays in the pose it stuck in; its flame went out on the touch (fix-176)
                var r = d.Rotation;
                return Create(d.Id, d.Item, position, new Quaternion(r[0], r[1], r[2], r[3]).normalized, 1f, effects: !d.Spent);
            }
            var go = Create(d.Id, d.Item, position, Quaternion.Euler(0, d.Yaw + 90, 0), 1f, effects: !d.Spent);
            var bounds = go.TryGetComponent<MeshFilter>(out var mf) && mf.sharedMesh != null ? mf.sharedMesh.bounds : new Bounds(Vector3.zero, Vector3.one * 0.3f);
            var pose = WorldItemRest.Place(bounds, 1f, position, d.Yaw + 90);
            go.transform.SetPositionAndRotation(pose.position, pose.rotation);
            global::G3.Unity.G3ReliefGrounded.Place(go);   // shown on the drawn relief (remaster-world 14)
            return go;
        }

        /// <summary>The views of items dropped during the game (dev probe).</summary>
        public IEnumerable<KeyValuePair<string, GameObject>> ShownDropped()
        {
            foreach (var kv in _shown)
                if (kv.Value != null && _session.Items.World.TryGetDropped(kv.Key, out _)) yield return kv;
        }

        GameObject Create(string id, string item, Vector3 position, Quaternion rotation, float scale, bool effects = true)
        {
            var go = new GameObject(item);
            go.transform.SetParent(transform, false);
            go.transform.SetPositionAndRotation(position, rotation);
            go.transform.localScale = Vector3.one * scale;
            var mesh = MeshOf(item, out var materials);
            if (mesh != null)
            {
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                go.AddComponent<MeshRenderer>().sharedMaterials = materials;
            }
            var box = go.AddComponent<BoxCollider>();
            box.isTrigger = true;
            if (mesh != null)
            {
                box.center = mesh.bounds.center;
                box.size = Vector3.Max(mesh.bounds.size, Vector3.one * 0.15f);
            }
            else box.size = Vector3.one * 0.3f;
            go.AddComponent<WorldItemView>().Id = id;
            if (effects) G3.Unity.G3ItemEffect.Sync(go, item); // items with an effect glow or burn where they lie (effects-2)
            return go;
        }

        Mesh MeshOf(string item, out Material[] materials)
        {
            materials = null;
            if (_catalog == null || !_session.Content.Database.TryGet(item, out ItemDef def) || def.Visual?.Mesh == null) return null;
            if (!_catalog.TryGetMesh(def.Visual.Mesh, out var entry)) return null;
            materials = entry.materials;
            return entry.mesh;
        }

        void Hide(string id)
        {
            if (!_shown.TryGetValue(id, out var view)) return;
            _shown.Remove(id);
            if (view != null) Destroy(view);
        }

        static Vector3 Vector(float[] v) => new Vector3(v[0], v[1], v[2]);
    }

    /// <summary>
    /// The shown views by id, read-only and foreach-able without allocating (struct enumerator; the focus, the HUD and the
    /// spell views walk them every frame, perf-3).
    /// </summary>
    public readonly struct ShownSet<T> : IEnumerable<KeyValuePair<string, T>>
    {
        readonly Dictionary<string, T> _items;
        internal ShownSet(Dictionary<string, T> items) => _items = items;
        public int Count => _items.Count;
        public Dictionary<string, T>.Enumerator GetEnumerator() => _items.GetEnumerator();
        IEnumerator<KeyValuePair<string, T>> IEnumerable<KeyValuePair<string, T>>.GetEnumerator() => _items.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => _items.GetEnumerator();
    }
}
