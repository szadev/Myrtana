using System.Collections.Generic;
using G3.Unity;
using Unity.Collections;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.AI;

namespace Myrtana.Unity
{
    /// <summary>
    /// The NavMesh of the cells around the player for walking NPCs, built at runtime from the cells' collision. It is
    /// one <see cref="NavMeshData"/> for all cells, so paths cross cell borders (followers and escorts no longer walk
    /// straight through walls at a border). Each cell's sources are collected once, when the cell is awake within
    /// <see cref="buildRadius"/>; then the shared NavMesh is updated in the background
    /// (<c>NavMeshBuilder.UpdateNavMeshDataAsync</c>, Unity's pattern for large / streamed worlds, as in the
    /// NavMeshComponents LocalNavMeshBuilder sample: only the tiles whose sources changed are rebuilt, tiles outside the
    /// new bounds are dropped). One update runs at a time; cells that became ready meanwhile go into the next one.
    /// A cell is used only after the start-up hold is released and its deferred content is awake: built during the
    /// start-up load, Faring's NavMesh had holes (the gate and fence gaps were closed, slaves' paths ended early and the
    /// rest went straight over the rock piles); the same build a few seconds later is complete.
    /// </summary>
    public sealed class CellNavMeshes : MonoBehaviour
    {
        [Tooltip("Cells whose square is closer than this to the player (m) are in the NavMesh.")] public float buildRadius = 180f;
        [Tooltip("A cell in the NavMesh leaves it beyond buildRadius + this (m), so walking along a border does not rebuild.")]
        public float dropMargin = 60f;
        /// <summary>
        /// Voxel size: two voxels per agent radius, so the radius erodes exactly (0.25 m voxels eroded 0.5 m per side and
        /// cut narrow ramps and stairs between terraces; Unity recommends radius/3, half the cost again).
        /// </summary>
        [Tooltip("NavMesh voxel size (m): coarser builds faster. Two voxels per agent radius.")] public float voxelSize = 0.175f;
        /// <summary>
        /// The G3 character movement (gCCharacterMovement_PS, Docs/hero.md): StepHeight 65 cm is the highest floor stepped
        /// onto, SteepGroundAngleMax 49° the steepest walkable floor. Higher steps are one-way: <see cref="NavDropLinks"/>.
        /// </summary>
        public float agentRadius = 0.35f, agentHeight = 1.8f, agentClimb = G3.Player.SlopeRules.StepHeight, agentSlope = G3.Player.SlopeRules.WalkableAngleMax;
        [Tooltip("Job worker threads a build may use. The rest stay free for rendering and physics: a build on all workers " +
                 "made Submit / PhysX wait 25–60 ms per frame for minutes after the start (Docs/performance.md).")]
        public int buildWorkers = 2;
        [Tooltip("Workers of an update that only adds cells farther than nearRadius from the player (background work: " +
                 "the area around the hero is walkable already, so rendering keeps more workers).")]
        public int backgroundWorkers = 1;
        [Tooltip("Cells closer than this to the player (m) are urgent: their update uses buildWorkers.")]
        public float nearRadius = 60f;

        sealed class Entry
        {
            public G3WorldManifest.Cell cell;
            public int index;
            /// <summary>The cell's own build sources (colliders + deep-water modifiers); null until collected.</summary>
            public List<NavMeshBuildSource> sources;
            public Bounds bounds;
            /// <summary>Its sources are in the running update / in a finished one (its tiles are walkable).</summary>
            public bool inFlight, live;
        }

        readonly Dictionary<int, Entry> _cells = new Dictionary<int, Entry>();
        readonly List<NavMeshBuildSource> _sources = new List<NavMeshBuildSource>();
        /// <summary>Scratch list of one cell's collection (kept apart from <see cref="_sources"/>: cells are collected while an update runs).</summary>
        readonly List<NavMeshBuildSource> _collect = new List<NavMeshBuildSource>();
        readonly List<int> _keys = new List<int>();
        readonly List<NavMeshBuildMarkup> _markups = new List<NavMeshBuildMarkup>();
        readonly HashSet<Component> _seenComponents = new HashSet<Component>();
        static readonly ProfilerMarker CollectMarker = new ProfilerMarker("CellNavMeshes.CollectSources");
        static readonly ProfilerMarker StartMarker = new ProfilerMarker("CellNavMeshes.StartUpdate");
        float _buildStart;
        G3WorldStreamer _streamer;
        Transform _player;
        NavMeshData _data;
        NavMeshDataInstance _instance;
        AsyncOperation _building;
        bool _dirty;
        float _nextCheck;

        /// <summary>Cells in the NavMesh (sources collected).</summary>
        public int BuiltCount { get; private set; }
        /// <summary>Finished NavMesh updates.</summary>
        public int UpdateCount { get; private set; }

        /// <summary>The cell NavMesh of the running session (null outside Play).</summary>
        public static CellNavMeshes Active { get; private set; }

        public void Init(G3WorldStreamer streamer, Transform player)
        {
            _streamer = streamer;
            _player = player;
            Active = this;
            G3PerfProbe.SampleBegin = NavStats.Begin;
            G3PerfProbe.SampleExtra = NavStats.Line;
            if (_streamer == null) return;
            _streamer.CellLoaded += (cell, _) => _cells[Key(cell)] = new Entry { cell = cell, index = _streamer.manifest.cells.IndexOf(cell) };
            _streamer.CellUnloaded += Remove;
            var manifest = _streamer.manifest;
            for (int i = 0; manifest != null && i < manifest.cells.Count; i++)
                if (_streamer.IsLoaded(i)) _cells[Key(manifest.cells[i])] = new Entry { cell = manifest.cells[i], index = i };
        }

        /// <summary>
        /// Cells are collected one per second, nearest first, also while an update runs (fix-22: collecting only between
        /// updates made the start-up / post-teleport build one cell per update, 45 s and more for 9 cells at 0.175 m
        /// voxels); an update starts as soon as the previous one finished and something changed, so it takes every cell
        /// collected meanwhile.
        /// </summary>
        void Update()
        {
            if (_streamer == null || _player == null) return;
            if (_building != null && _building.isDone)
            {
                _building = null;
                UpdateCount++;
                NavStats.Updates++;
                NavStats.LastUpdateSeconds = Time.realtimeSinceStartup - _buildStart;
                foreach (var e in _cells.Values)
                    if (e.inFlight) { e.inFlight = false; e.live = e.sources != null; }
            }
            NavStats.Building = _building != null;
            if (Time.unscaledTime >= _nextCheck && !_streamer.IsHolding)
            {
                _nextCheck = Time.unscaledTime + 1f;
                Refresh();
            }
            if (_dirty && _building == null && !_streamer.IsHolding) StartUpdate();
        }

        /// <summary>
        /// Whether the NavMesh around <paramref name="p"/> is still being made: a cell within <paramref name="radius"/> of it
        /// that will be in the NavMesh (within the build radius of the player) is not walkable yet (not collected, in the
        /// running update, or still streaming in). A walker there that finds no way is waiting for the build, not stuck
        /// (editor-runner-22: Geldern_Slave_01's partial path was the NavMesh still building after the start).
        /// </summary>
        public static bool IsBuildingNear(Vector3 p, float radius = 30f) => Active != null && Active.BuildingNear(p, radius);

        bool BuildingNear(Vector3 p, float radius)
        {
            var manifest = _streamer != null ? _streamer.manifest : null;
            if (manifest == null || _player == null) return false;
            if (_streamer.IsHolding) return true;
            var self = manifest.CellOf(p);
            bool selfKnown = false;
            foreach (var e in _cells.Values)
            {
                if (e.cell.ix == self.x && e.cell.iz == self.y) selfKnown = true;
                if (manifest.Distance(e.cell, p) > radius) continue;
                if (manifest.Distance(e.cell, _player.position) >= buildRadius) continue; // not built while the hero is away
                if (!e.live) return true;
            }
            // its own cell is not even loaded yet but will be: streaming
            return !selfKnown && Flat(p - _player.position) < buildRadius;
        }

        static float Flat(Vector3 v) => new Vector2(v.x, v.z).magnitude;

        /// <summary>Collects the nearest ready cell's sources (one per check) and drops cells that moved far away.</summary>
        void Refresh()
        {
            Entry best = null;
            float bestDist = buildRadius;
            foreach (var e in _cells.Values)
            {
                float d = _streamer.manifest.Distance(e.cell, _player.position);
                if (e.sources != null)
                {
                    if (d > buildRadius + dropMargin) { e.sources = null; e.live = false; BuiltCount--; _dirty = true; }
                    continue;
                }
                if (!_streamer.IsAwake(e.index)) continue;
                if (d < bestDist) { best = e; bestDist = d; }
            }
            if (best == null) return;
            if (!Collect(best)) return;   // its one-sided copies are building: the next check collects it
            BuiltCount++;
            _dirty = true;
        }

        NavMeshBuildSettings Settings(int workers)
        {
            var settings = NavMesh.GetSettingsByID(0);
            settings.agentRadius = agentRadius;
            settings.agentHeight = agentHeight;
            settings.agentClimb = agentClimb;
            settings.agentSlope = agentSlope;
            settings.overrideVoxelSize = true;
            settings.voxelSize = voxelSize;
            settings.overrideTileSize = true;
            settings.tileSize = 128;
            settings.maxJobWorkers = (uint)Mathf.Max(1, workers);
            return settings;
        }

        /// <summary>Collects the cell's sources; false while one-sided copies of its colliders are still being built on a worker
        /// (perf-5: the first build of a big town's two-sided meshes was a 50-520 ms frame), the cell is collected again later.</summary>
        bool Collect(Entry e)
        {
            var rect = _streamer.manifest.CellRect(e.cell.ix, e.cell.iz);
            var b = e.cell.contentBounds;
            float minY = b.size.sqrMagnitude > 0 ? b.min.y - 5 : -200, maxY = b.size.sqrMagnitude > 0 ? b.max.y + 5 : 800;
            e.bounds = new Bounds(new Vector3(rect.center.x, (minY + maxY) * 0.5f, rect.center.y),
                                  new Vector3(rect.width, maxY - minY, rect.height));
            _collect.Clear();
            using (CollectMarker.Auto())
            {
                NavMeshBuilder.CollectSources(e.bounds, ~0, NavMeshCollectGeometry.PhysicsColliders, 0, _markups, _collect);
                if (!OneSidedSources(_collect)) { _collect.Clear(); return false; }
                AddDeepWater(rect);
            }
            e.sources = new List<NavMeshBuildSource>(_collect);
            e.live = false;
            return true;
        }

        // ------------------------------------------------------------------ fix-99: no NavMesh on the back of surfaces

        /// <summary>One-sided NavMesh copy per (two-sided collision mesh, render mesh); null = keep the collider's mesh.</summary>
        readonly Dictionary<(Mesh, Mesh), Mesh> _oneSided = new Dictionary<(Mesh, Mesh), Mesh>();
        static readonly ProfilerMarker OneSidedMarker = new ProfilerMarker("CellNavMeshes.OneSided");
        /// <summary>Two-sided collision meshes made one-sided for the NavMesh / triangle pairs that lost their back copy (runner probes).</summary>
        public static int OneSidedMeshes, OneSidedCeilings;

        /// <summary>
        /// The two-sided triangle colliders (G3CollisionBuilder "_2s" meshes) go into the NavMesh one-sided where the
        /// object's render mesh shows the real side (<see cref="G3.Player.NavFacingRules"/>): no walkable NavMesh on top of
        /// cave ceilings inside the rock, under floors, on the back of walls (fix-99: a Reddock varan walked into the
        /// ceiling and was put back there by the fall rule). The physics colliders stay two-sided.
        /// </summary>
        bool OneSidedSources(List<NavMeshBuildSource> sources)
        {
            using var marker = OneSidedMarker.Auto();
            bool complete = true;
            for (int i = 0; i < sources.Count; i++)
            {
                var src = sources[i];
                if (src.shape != NavMeshBuildSourceShape.Mesh || !(src.component is MeshCollider mc) || mc.convex) continue;
                var mesh = src.sourceObject as Mesh;
                if (mesh == null) continue;
                if (mesh.name.EndsWith("_2s", System.StringComparison.Ordinal))
                {
                    var mf = mc.GetComponent<MeshFilter>(); // pre-scaled shapes sit on a child: no render mesh in their space, kept
                    var render = mf != null ? mf.sharedMesh : null;
                    if (render != null && render.isReadable && mesh.isReadable)
                    {
                        var key = (mesh, render);
                        if (!_oneSided.TryGetValue(key, out var one))
                        {
                            // perf-5: the facing rules run on a worker; the copy is made when they are done
                            if (!_oneSidedJobs.TryGetValue(key, out var job)) _oneSidedJobs[key] = job = StartOneSided(mesh, render);
                            if (!job.task.IsCompleted) { complete = false; continue; }
                            _oneSidedJobs.Remove(key);
                            _oneSided[key] = one = FinishOneSided(mesh, job);
                        }
                        if (one != null) mesh = one;
                    }
                }
                // fix-128: a cave sheet's walkable triangles on its roof / wall tops inside the hill go out of the NavMesh
                if (CaveGround.KindOf(mc) == G3.Player.CaveFloorRules.Kind.Cave && mesh.isReadable)
                {
                    if (!_caveNav.TryGetValue(mc, out var cave))
                        _caveNav[mc] = cave = (mesh, BuildCaveNav(mesh, src.transform));
                    if (cave.nav != null && cave.from == mesh) mesh = cave.nav;
                }
                if (mesh == (Mesh)src.sourceObject) continue;
                src.sourceObject = mesh;
                sources[i] = src;
            }
            return complete;
        }

        // ------------------------------------------------------------------ fix-128: no NavMesh on cave roofs / wall tops

        /// <summary>Per placed cave collider: the NavMesh mesh it was built from and the copy without its roof tops (null = none).</summary>
        readonly Dictionary<MeshCollider, (Mesh from, Mesh nav)> _caveNav = new Dictionary<MeshCollider, (Mesh, Mesh)>();
        static readonly ProfilerMarker CaveNavMarker = new ProfilerMarker("CellNavMeshes.CaveRoofs");
        /// <summary>Cave colliders judged / walkable triangles dropped as roof or wall tops (runner probes <c>caveroofmeshes</c>, <c>caverooftris</c>).</summary>
        public static int CaveMeshes, CaveRoofTris;

        /// <summary>
        /// The walkable (up-facing, within the agent slope) triangles of a placed cave sheet that <see cref="CaveGround.Judge"/>
        /// puts on a roof or wall top inside the hill (the hill's top over them before any cave ceiling) are left out of its
        /// NavMesh copy (G3Cli collision caveroofs: fix-99 left 1 558 such triangles, 1 548 m2, mostly Nordmar mine props);
        /// placed per instance (the column over each copy differs). Null: nothing to drop.
        /// </summary>
        Mesh BuildCaveNav(Mesh mesh, Matrix4x4 toWorld)
        {
            using var marker = CaveNavMarker.Auto();
            CaveMeshes++;
            var v = mesh.vertices;
            var t = mesh.triangles;
            float walkNy = Mathf.Cos(agentSlope * Mathf.Deg2Rad);
            var keep = new List<int>(t.Length);
            int dropped = 0;
            for (int i = 0; i + 2 < t.Length; i += 3)
            {
                var a = toWorld.MultiplyPoint3x4(v[t[i]]);
                var b = toWorld.MultiplyPoint3x4(v[t[i + 1]]);
                var c = toWorld.MultiplyPoint3x4(v[t[i + 2]]);
                var n = Vector3.Cross(b - a, c - a);
                bool walkable = n.sqrMagnitude > 1e-10f && n.normalized.y >= walkNy;
                if (walkable && G3.Player.CaveFloorRules.Stand.RoofTop == CaveGround.Judge((a + b + c) / 3f, out _))
                {
                    dropped++;
                    continue;
                }
                keep.Add(t[i]); keep.Add(t[i + 1]); keep.Add(t[i + 2]);
            }
            if (dropped == 0) return null;
            CaveRoofTris += dropped;
            var nav = new Mesh { name = mesh.name + "_cave", indexFormat = mesh.indexFormat };
            nav.vertices = v;
            nav.triangles = keep.ToArray();
            nav.RecalculateBounds();
            return nav;
        }

        /// <summary>A one-sided copy being judged on a worker (perf-5): the collider's vertices and the rules' task.</summary>
        sealed class OneSidedJob
        {
            public Vector3[] vertices;
            public System.Threading.Tasks.Task<G3.Player.NavFacingRules.Result> task;
        }

        readonly Dictionary<(Mesh, Mesh), OneSidedJob> _oneSidedJobs = new Dictionary<(Mesh, Mesh), OneSidedJob>();

        static OneSidedJob StartOneSided(Mesh collision, Mesh render)
        {
            var cv = collision.vertices;
            var ct = collision.triangles;
            var rv = render.vertices;
            var rt = render.triangles;
            var job = new OneSidedJob { vertices = cv };
            job.task = System.Threading.Tasks.Task.Run(() =>
            {
                var cp = new float[cv.Length * 3];
                for (int k = 0; k < cv.Length; k++) { cp[k * 3] = cv[k].x; cp[k * 3 + 1] = cv[k].y; cp[k * 3 + 2] = cv[k].z; }
                var rp = new float[rv.Length * 3];
                for (int k = 0; k < rv.Length; k++) { rp[k * 3] = rv[k].x; rp[k * 3 + 1] = rv[k].y; rp[k * 3 + 2] = rv[k].z; }
                return G3.Player.NavFacingRules.OneSided(cp, ct, rp, rt);
            });
            return job;
        }

        static Mesh FinishOneSided(Mesh collision, OneSidedJob job)
        {
            if (job.task.IsFaulted) { Debug.LogException(job.task.Exception); return null; }
            var r = job.task.Result;
            var cv = job.vertices;
            if (r.KeptUp + r.KeptDown + r.Steep == 0) return null; // nothing matched: the collider as it is
            var one = new Mesh { name = collision.name + "_nav", indexFormat = collision.indexFormat };
            one.vertices = cv;
            one.triangles = r.Triangles;
            one.RecalculateBounds();
            OneSidedMeshes++;
            OneSidedCeilings += r.KeptDown;
            return one;
        }

        /// <summary>
        /// Starts one background update of the shared NavMesh over every collected cell. A collider that reaches into two
        /// cells was collected by both: it goes in once. Sources whose object was destroyed meanwhile are skipped.
        /// </summary>
        void StartUpdate()
        {
            using var marker = StartMarker.Auto();
            _dirty = false;
            _sources.Clear();
            _seenComponents.Clear();
            bool any = false, urgent = false;
            var bounds = new Bounds();
            // a stable source order (cell key), so the tiles of unchanged cells are recognised and kept by the update
            _keys.Clear();
            _keys.AddRange(_cells.Keys);
            _keys.Sort();
            foreach (int key in _keys)
            {
                var e = _cells[key];
                if (e.sources == null) continue;
                if (!e.live)
                {
                    e.inFlight = true;
                    if (_streamer.manifest.Distance(e.cell, _player.position) < nearRadius) urgent = true;
                }
                foreach (var src in e.sources)
                {
                    if (src.shape == NavMeshBuildSourceShape.ModifierBox) { _sources.Add(src); continue; }
                    if (src.component == null) continue; // destroyed (Unity null) or never set: nothing to build from
                    if (!_seenComponents.Add(src.component)) continue;
                    if ((src.shape == NavMeshBuildSourceShape.Mesh || src.shape == NavMeshBuildSourceShape.Terrain) && src.sourceObject == null) continue;
                    _sources.Add(src);
                }
                if (!any) { bounds = e.bounds; any = true; }
                else bounds.Encapsulate(e.bounds);
            }
            // Near cells (the hero's surroundings after a teleport / the start) build on buildWorkers; an update that only
            // adds far cells, or drops cells, is background work on fewer workers (less contention with rendering jobs).
            int workers = urgent ? buildWorkers : Mathf.Min(buildWorkers, backgroundWorkers);
            NavStats.BuildWorkers = workers;
            var settings = Settings(workers);
            if (_data == null)
            {
                if (!any) return;
                _data = new NavMeshData(settings.agentTypeID);
                _instance = NavMesh.AddNavMeshData(_data);
            }
            if (!any) bounds = new Bounds(_player.position, Vector3.one); // nothing near: the update empties the NavMesh
            bounds.Expand(new Vector3(2, 0, 2));
            NavDropLinks.Prune(bounds); // links over cells that left the NavMesh go with them
            _buildStart = Time.realtimeSinceStartup;
            _building = NavMeshBuilder.UpdateNavMeshDataAsync(_data, settings, _sources, bounds);
        }

        [Tooltip("Sample spacing of the deep-water test, m.")] public float waterStep = 2.5f;
        const int NotWalkableArea = 1;

        /// <summary>
        /// Marks water too deep for walking NPCs (column ≥ <see cref="G3.Player.SwimRules.HumanSwimDepth"/> (2 m, the smallest
        /// NPC SwimDepth; golems/ogres 2.5, trolls 3 m go deeper only on straight walks) − 0.1 m:
        /// G3 NPCs that start swimming die) as not walkable, so paths go around lakes, rivers and the sea; wading depth
        /// stays walkable. Samples every <see cref="waterStep"/> m inside the water zones over the cell and adds one
        /// NavMesh modifier box per run of deep samples in a row (from 100 m below to 0.2 m above the surface).
        /// </summary>
        void AddDeepWater(Rect rect)
        {
            var map = G3Water.Map;
            if (map == null) return;
            float step = Mathf.Max(0.5f, waterStep);
            // Pass 1: the sample grid in row order (what the old per-sample loop visited) with the water surface of the
            // covered samples; their floor rays are cast together in pass 2 (perf-7).
            _waterSamples.Clear();
            foreach (var zone in map.Zones)
            {
                float x0 = Mathf.Max(rect.xMin, zone.MinX), x1 = Mathf.Min(rect.xMax, zone.MaxX);
                float z0 = Mathf.Max(rect.yMin, zone.MinZ), z1 = Mathf.Min(rect.yMax, zone.MaxZ);
                if (x0 >= x1 || z0 >= z1) continue;
                for (float z = z0 + step * 0.5f; z < z1; z += step)
                {
                    for (float x = x0 + step * 0.5f; ; x += step)
                    {
                        bool end = x >= x1;
                        bool covered = !end && zone.Covers(x, z);
                        _waterSamples.Add(new WaterSample
                        {
                            x = x, z = z, end = end, covered = covered,
                            surface = covered ? zone.SurfaceY(x, z) : 0f,
                        });
                        if (end) break;
                    }
                }
            }
            if (_waterSamples.Count == 0) return;
            CastWaterFloors(step);
        }

        struct WaterSample
        {
            public float x, z, surface;
            public bool end, covered, deep;
        }

        readonly List<WaterSample> _waterSamples = new List<WaterSample>();

        /// <summary>
        /// Pass 2: the floor under every covered sample (= <see cref="G3Water.FloorUnder"/>: highest non-trigger hit of a
        /// ray from just above the surface down) as one batch of <see cref="RaycastCommand"/>s on the job workers instead
        /// of thousands of main-thread raycasts in one frame, then the same runs of deep samples → modifier boxes.
        /// </summary>
        void CastWaterFloors(float step)
        {
            int n = 0;
            foreach (var w in _waterSamples) if (w.covered) n++;
            if (n > 0)
            {
                var commands = new NativeArray<RaycastCommand>(n, Allocator.TempJob);
                var results = new NativeArray<RaycastHit>(n, Allocator.TempJob);
                try
                {
                    var query = new QueryParameters(~0, false, QueryTriggerInteraction.Ignore, false);
                    int k = 0;
                    foreach (var w in _waterSamples)
                        if (w.covered)
                            commands[k++] = new RaycastCommand(new Vector3(w.x, w.surface + G3Water.FloorRayAbove, w.z), Vector3.down,
                                                               query, G3Water.FloorRayAbove + G3Water.FloorRayDepth);
                    RaycastCommand.ScheduleBatch(commands, results, 64, 1).Complete();
                    k = 0;
                    for (int i = 0; i < _waterSamples.Count; i++)
                    {
                        var w = _waterSamples[i];
                        if (!w.covered) continue;
                        var hit = results[k++];
                        float floor = hit.collider != null ? hit.point.y : float.NegativeInfinity;
                        w.deep = !G3.Player.SwimRules.NpcCanWalk(w.surface - floor);
                        _waterSamples[i] = w;
                    }
                }
                finally
                {
                    commands.Dispose();
                    results.Dispose();
                }
            }

            float runStart = float.NaN, runTop = 0;
            foreach (var w in _waterSamples)
            {
                bool deep = w.deep;
                if (deep && float.IsNaN(runStart)) { runStart = w.x; runTop = w.surface; }
                else if (deep) runTop = Mathf.Max(runTop, w.surface);
                if (!deep && !float.IsNaN(runStart))
                {
                    float a = runStart - step * 0.5f, b = w.x - step * 0.5f;
                    const float below = 100f, above = 0.2f;
                    _collect.Add(new NavMeshBuildSource
                    {
                        shape = NavMeshBuildSourceShape.ModifierBox,
                        transform = Matrix4x4.Translate(new Vector3((a + b) * 0.5f, runTop + (above - below) * 0.5f, w.z)),
                        size = new Vector3(b - a, below + above, step),
                        area = NotWalkableArea,
                    });
                    runStart = float.NaN;
                }
                if (w.end) runStart = float.NaN; // a row ends (the old loop reset the run per row)
            }
            _waterSamples.Clear();
        }

        void Remove(G3WorldManifest.Cell cell)
        {
            if (!_cells.TryGetValue(Key(cell), out var e)) return;
            _cells.Remove(Key(cell));
            DropGoneCaves();
            if (e.sources == null) return;
            BuiltCount--;
            _dirty = true; // its colliders are gone: the next update drops its tiles
        }

        void OnDestroy()
        {
            if (Active == this) Active = null;
            foreach (var m in _oneSided.Values) if (m != null) Destroy(m);
            _oneSided.Clear();
            foreach (var m in _caveNav.Values) if (m.nav != null) Destroy(m.nav);
            _caveNav.Clear();
            NavDropLinks.Clear();
            if (_instance.valid) _instance.Remove();
        }

        readonly List<MeshCollider> _gone = new List<MeshCollider>();

        /// <summary>Forgets the cave copies of colliders that were unloaded with their cell.</summary>
        void DropGoneCaves()
        {
            _gone.Clear();
            foreach (var kv in _caveNav) if (kv.Key == null) _gone.Add(kv.Key);
            foreach (var k in _gone)
            {
                if (_caveNav[k].nav != null) Destroy(_caveNav[k].nav);
                _caveNav.Remove(k);
            }
        }

        static int Key(G3WorldManifest.Cell c) => c.iz * 1000 + c.ix;
    }
}
