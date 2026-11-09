using System.Collections.Generic;
using G3.Unity;
using Myrtana.Core;
using Myrtana.Session;
using Myrtana.Simulation;
using UnityEngine;

namespace Myrtana.Unity
{
    /// <summary>
    /// Decides how closely each NPC is simulated and keeps views for the ones near the player (gameplay-architecture.md
    /// §6.2): within <see cref="activeRadius"/> an NPC is <see cref="DetailLevel.Active"/> (its view walks and animates),
    /// within <see cref="viewRadius"/> it is <see cref="DetailLevel.Near"/> (its view follows the simulation), beyond it is
    /// <see cref="DetailLevel.Abstract"/> (no view). A view is only created where the streamer has loaded the ground.
    /// Views live in the persistent scene, so they survive cell unloads and NPCs can walk across cell borders.
    /// </summary>
    public sealed class NpcViewManager : MonoBehaviour, ISessionView
    {
        [Tooltip("NPCs closer than this (m) walk and animate.")] public float activeRadius = 70f;
        [Tooltip("NPCs closer than this (m) are shown.")] public float viewRadius = 160f;
        [Tooltip("Extra distance before a level is given up again (m).")] public float hysteresis = 10f;
        [Tooltip("Seconds between two passes over all NPCs.")] public float evaluateInterval = 0.25f;
        [Tooltip("Views created per frame at most (spreads prefab instantiation).")] public int spawnsPerFrame = 4;
        [Tooltip("Views returned per frame at most (a liberation drops dozens at once; spreads the GPU Resident Drawer changes, Docs/performance.md).")]
        public int despawnsPerFrame = 6;

        readonly Dictionary<ActorId, NpcView> _views = new Dictionary<ActorId, NpcView>();
        readonly List<(float dist, NpcAgent agent)> _toSpawn = new List<(float, NpcAgent)>();
        readonly List<ActorId> _scratch = new List<ActorId>();
        readonly List<ActorId> _toDespawn = new List<ActorId>();
        readonly HashSet<ActorId> _despawnQueued = new HashSet<ActorId>();
        NpcViewPool _pool;
        NpcSimulation _simulation;
        Transform _player;
        G3WorldStreamer _streamer;
        float _nextEvaluate;
        int _spawnCursor;

        public int ViewCount => _views.Count;
        /// <summary>Counts the views shown and returned: a new value means the set of shown views changed (perf-3, focus).</summary>
        public int Version { get; private set; }
        /// <summary>A view was bound to its NPC and shown / is about to be returned to the pool.</summary>
        public event System.Action<NpcView> ViewSpawned, ViewDespawned;
        public NpcSimulation Simulation => _simulation;

        public void Init(Transform player, G3WorldStreamer streamer)
        {
            _player = player;
            _streamer = streamer;
            _pool = new NpcViewPool(transform);
        }

        public void Bind(GameSession session)
        {
            ClearViews();
            _simulation = session.Npcs;
            var items = session.Content.Database;
            _pool.ItemMeshOf = item => items.TryGet(item, out Myrtana.Rules.ItemDef def) ? def.Visual?.Mesh : null;
            G3NavGraphRuntime.Bind(session);
            _nextEvaluate = 0;
        }

        /// <summary>The view of an actor, if one is shown.</summary>
        public bool TryGetView(ActorId actor, out Transform view)
        {
            view = _views.TryGetValue(actor, out var v) && v != null ? v.transform : null;
            return view != null;
        }

        /// <summary>The shown views by actor, without allocating: <c>foreach (var kv in ViewComponents)</c> (per-frame users, perf-9).</summary>
        public ViewSet ViewComponents => new ViewSet(_views);

        /// <summary>The view of <paramref name="actor"/> (false when it is not shown).</summary>
        public bool TryGetViewComponent(ActorId actor, out NpcView view) => _views.TryGetValue(actor, out view) && view != null;

        /// <summary>A read-only, foreach-able view of the shown views (struct enumerator).</summary>
        public readonly struct ViewSet
        {
            readonly Dictionary<ActorId, NpcView> _views;
            internal ViewSet(Dictionary<ActorId, NpcView> views) => _views = views;
            public int Count => _views.Count;
            public Dictionary<ActorId, NpcView>.Enumerator GetEnumerator() => _views.GetEnumerator();
        }

        public IEnumerable<KeyValuePair<ActorId, Transform>> Views
        {
            get
            {
                foreach (var kv in _views) yield return new KeyValuePair<ActorId, Transform>(kv.Key, kv.Value.transform);
            }
        }

        void Update()
        {
            if (_simulation == null || _player == null) return;
            if (Time.unscaledTime >= _nextEvaluate)
            {
                _nextEvaluate = Time.unscaledTime + evaluateInterval;
                Evaluate();
            }
            using (DespawnMarker.Auto()) DespawnSome();
            using (SpawnMarker.Auto()) SpawnSome();
        }

        /// <summary>One pass: detail level of every NPC, views dropped beyond the view radius, spawn requests nearest first.</summary>
        void Evaluate()
        {
            var origin = _player.position;
            _toSpawn.Clear();
            var agents = _simulation.Agents;
            for (int a = 0; a < agents.Count; a++) // indexed: no boxed enumerator over a few thousand agents (perf-8)
            {
                var agent = agents[a];
                if (!_simulation.IsInWorld(agent))
                {
                    // A switched-off world sector (G3 SetSectorStatus): the NPC is not in the world.
                    if (_views.ContainsKey(agent.Id)) QueueDespawn(agent.Id);
                    if (agent.Detail != DetailLevel.Abstract) _simulation.SetDetail(agent.Id, DetailLevel.Abstract);
                    continue;
                }
                var pose = _simulation.PoseOf(agent);
                float dx = pose.X - origin.x, dz = pose.Z - origin.z;
                float dist = Mathf.Sqrt(dx * dx + dz * dz);
                bool shown = _views.ContainsKey(agent.Id);
                float slack = shown ? hysteresis : 0;
                DetailLevel level;
                if (dist > viewRadius + slack || (!shown && !GroundReady(pose))) level = DetailLevel.Abstract;
                else if (dist <= activeRadius + (agent.Detail == DetailLevel.Active ? hysteresis : 0)) level = DetailLevel.Active;
                else level = DetailLevel.Near;

                if (level == DetailLevel.Abstract)
                {
                    if (shown) QueueDespawn(agent.Id);
                    _simulation.SetDetail(agent.Id, DetailLevel.Abstract);
                }
                else if (shown) _simulation.SetDetail(agent.Id, level);
                else _toSpawn.Add((dist, agent));
            }
            _toSpawn.Sort(ByDistance);
            // Agents that left the simulation (a summoned creature's body removed) take their view with them.
            _scratch.Clear();
            foreach (var id in _views.Keys)
                if (!_simulation.TryGet(id, out _)) _scratch.Add(id);
            foreach (var id in _scratch) QueueDespawn(id);
            _spawnCursor = 0;
        }

        static readonly System.Comparison<(float dist, NpcAgent agent)> ByDistance = (a, b) => a.dist.CompareTo(b.dist);

        static readonly global::Unity.Profiling.ProfilerMarker BindMarker = new global::Unity.Profiling.ProfilerMarker("NpcView.Bind");
        static readonly global::Unity.Profiling.ProfilerMarker SpawnMarker = new global::Unity.Profiling.ProfilerMarker("NpcViewManager.Spawn");
        static readonly global::Unity.Profiling.ProfilerMarker DespawnMarker = new global::Unity.Profiling.ProfilerMarker("NpcViewManager.Despawn");

        void SpawnSome()
        {
            int budget = spawnsPerFrame;
            while (budget > 0 && _spawnCursor < _toSpawn.Count)
            {
                var (dist, agent) = _toSpawn[_spawnCursor++];
                if (_views.ContainsKey(agent.Id)) continue;
                var view = _pool.Rent(agent.Definition.Visual);
                if (view == null) continue; // prefab still loading (next pass) or missing
                _simulation.SetDetail(agent.Id, dist <= activeRadius ? DetailLevel.Active : DetailLevel.Near);
                view.Leader = _player; // party members follow the hero, guides lead him
                using (BindMarker.Auto()) view.Bind(agent, _simulation);
                _views[agent.Id] = view;
                Version++;
                ViewSpawned?.Invoke(view);
                budget--;
            }
        }

        bool GroundReady(WorldPose pose) => _streamer == null || _streamer.IsReady(new Vector3(pose.X, pose.Y, pose.Z));

        /// <summary>Views to drop go out a few per frame (<see cref="despawnsPerFrame"/>), nearest-first order does not matter.</summary>
        void QueueDespawn(ActorId id)
        {
            if (_despawnQueued.Add(id)) _toDespawn.Add(id);
        }

        void DespawnSome()
        {
            if (_toDespawn.Count == 0) return;
            int n = despawnsPerFrame > 0 ? System.Math.Min(despawnsPerFrame, _toDespawn.Count) : _toDespawn.Count;
            for (int i = 0; i < n; i++)
            {
                var id = _toDespawn[i];
                _despawnQueued.Remove(id);
                Despawn(id);
            }
            _toDespawn.RemoveRange(0, n);
        }

        void Despawn(ActorId id)
        {
            if (!_views.TryGetValue(id, out var view)) return;
            _views.Remove(id);
            Version++;
            if (view != null) ViewDespawned?.Invoke(view);
            if (view != null) _pool.Return(view);
        }

        void ClearViews()
        {
            _toDespawn.Clear();
            _despawnQueued.Clear();
            _scratch.Clear();
            _scratch.AddRange(_views.Keys);
            foreach (var id in _scratch) Despawn(id);
        }
    }
}
