using System;
using System.Collections.Generic;
using Myrtana.Rules;
using Myrtana.Session;
using UnityEngine;

namespace Myrtana.Unity
{
    /// <summary>
    /// The hero's interactive objects near him (<see cref="InteractiveObjectDef"/>). Animated objects (chests, doors —
    /// G3 actors the static world import leaves out) get their prefab from <see cref="InteractiveVisualCatalog"/>; beds
    /// and stations are already part of the cell scenes, so their view is only a focus point. Views are disposable like
    /// <see cref="WorldItemViews"/>: created within <see cref="showRadius"/>, removed beyond <see cref="hideRadius"/>;
    /// they show the state of <see cref="InteractiveObjects"/> (door open, lid open while looting). Objects of a story
    /// sector that is off (G3 SetSectorStatus) are not shown.
    /// </summary>
    public sealed class InteractiveObjectViews : MonoBehaviour, ISessionView
    {
        const float CellSize = 16f;

        [Tooltip("Objects closer than this (m) are shown.")] public float showRadius = 40f;
        [Tooltip("Shown objects farther than this (m) are removed.")] public float hideRadius = 50f;
        [Tooltip("Seconds between visibility updates.")] public float updateInterval = 0.5f;

        readonly Dictionary<(int, int), List<InteractiveObjectDef>> _grid = new Dictionary<(int, int), List<InteractiveObjectDef>>();
        readonly Dictionary<string, InteractiveObjectView> _shown = new Dictionary<string, InteractiveObjectView>(StringComparer.Ordinal);
        Transform _player;
        InteractiveVisualCatalog _catalog;
        GameSession _session;
        float _nextUpdate;

        public void Init(Transform player, InteractiveVisualCatalog catalog)
        {
            _player = player;
            _catalog = catalog;
        }

        public void Bind(GameSession session)
        {
            if (_session != null) _session.Interaction.Objects.Changed -= Refresh;
            foreach (var view in _shown.Values) if (view != null) Destroy(view.gameObject);
            _shown.Clear();
            _session = session;
            session.Interaction.Objects.Changed += Refresh;
            if (_grid.Count == 0) BuildGrid(session);
            _nextUpdate = 0;
        }

        public ShownSet<InteractiveObjectView> Shown => new ShownSet<InteractiveObjectView>(_shown);

        public bool TryGet(string id, out InteractiveObjectView view) => _shown.TryGetValue(id, out view) && view != null;

        void BuildGrid(GameSession session)
        {
            foreach (var def in session.Content.Database.All<InteractiveObjectDef>())
            {
                var key = Cell(def.Position[0], def.Position[2]);
                if (!_grid.TryGetValue(key, out var list)) _grid[key] = list = new List<InteractiveObjectDef>();
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
                if (kv.Value == null || (kv.Value.transform.position - p).sqrMagnitude > hideRadius * hideRadius ||
                    !_session.Interaction.Objects.IsInWorld(kv.Key)) // its story sector was switched off
                    (far ?? (far = new List<string>())).Add(kv.Key);
            if (far == null) return;
            foreach (var id in far)
            {
                if (_shown[id] != null) Destroy(_shown[id].gameObject);
                _shown.Remove(id);
            }
        }

        void ShowNear(Vector3 p)
        {
            int r = Mathf.CeilToInt(showRadius / CellSize);
            var (cx, cz) = Cell(p.x, p.z);
            for (int x = cx - r; x <= cx + r; x++)
            for (int z = cz - r; z <= cz + r; z++)
            {
                if (!_grid.TryGetValue((x, z), out var list)) continue;
                foreach (var def in list)
                {
                    if (_shown.ContainsKey(def.Id) || !_session.Interaction.Objects.IsInWorld(def)) continue;
                    var position = Vector(def.Position);
                    if ((position - p).sqrMagnitude <= showRadius * showRadius) _shown[def.Id] = Create(def, position);
                }
            }
        }

        InteractiveObjectView Create(InteractiveObjectDef def, Vector3 position)
        {
            GameObject prefab = null;
            var go = _catalog != null && _catalog.TryGet(def.Visual, out prefab) ? Instantiate(prefab) : new GameObject();
            go.name = def.Name;
            go.transform.SetParent(transform, false);
            go.transform.SetPositionAndRotation(position, Quaternion.LookRotation(Vector(def.Forward), Vector(def.Up)));
            go.transform.localScale = Vector3.one * def.Scale;
            var view = go.AddComponent<InteractiveObjectView>();
            view.Init(def, prefab != null);
            view.Show(_session.Interaction.Objects, animate: false);
            return view;
        }

        void Refresh(string id)
        {
            if (TryGet(id, out var view)) view.Show(_session.Interaction.Objects, animate: true);
        }

        static Vector3 Vector(float[] v) => new Vector3(v[0], v[1], v[2]);
    }
}
