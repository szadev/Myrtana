using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using G3.Formats.World;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;

namespace G3.Unity.Editor
{
    /// <summary>
    /// Whole-world import into streamable cells (Docs: see G3WorldManifest).
    /// <para>Grid: 250 m squares from (-4250, -4250), 28 × 27 cells. An entity goes to the cell holding its AABB centre
    /// (nothing is duplicated; terrain meshes up to ~590 m wide stick out of their cell — the manifest keeps the content
    /// bounds so the streamer loads such cells early enough).</para>
    /// <para>Resumable editor job: one cell per editor tick, scenes that already exist are skipped (unless forced),
    /// survives domain reloads (SessionState) — progress in the editor's background-task bar and Logs/g3world_batch.txt.</para>
    /// CLI: G3.Unity.Editor.G3WorldBatch.Start(false, null) / Status() / Stop().
    /// Headless (editor closed): Unity.exe -batchmode -projectPath . -executeMethod G3.Unity.Editor.G3WorldBatch.RunHeadless [-g3force]
    /// </summary>
    [InitializeOnLoad]
    public static class G3WorldBatch
    {
        public const string SceneFolder = "Assets/Scenes/World";
        public const string CellFolder = SceneFolder + "/Cells";
        public const string ManifestPath = SceneFolder + "/G3WorldManifest.asset";
        public const string MasterPath = SceneFolder + "/G3World_Master.unity";

        public static readonly Vector2 Origin = new Vector2(-4250, -4250);
        public const float CellSize = 250;
        public const int CountX = 28, CountZ = 27;

        const string KeyRunning = "G3World.Batch.Running";
        const string KeyForce = "G3World.Batch.Force";
        const string KeyFilter = "G3World.Batch.Filter";
        const string KeyDone = "G3World.Batch.Done";
        const string KeyMaster = "G3World.Batch.Master";
        const string KeyStats = "G3World.Batch.Stats";
        const string KeyStart = "G3World.Batch.StartTicks";

        public static string StatusFile => Path.GetFullPath(Path.Combine(Application.dataPath, "../Logs/g3world_batch.txt"));

        static G3WorldBatch()
        {
            if (SessionState.GetBool(KeyRunning, false)) Hook();
        }

        // ------------------------------------------------------------------ public API

        [MenuItem("G3/World/Import Whole World (resume)", priority = 100)]
        static void MenuStart() => Debug.Log(Start(false, null));

        [MenuItem("G3/World/Import Whole World (force re-import)", priority = 101)]
        static void MenuStartForce()
        {
            if (EditorUtility.DisplayDialog("G3 world import", "Re-import every cell scene (and vegetation)?", "Re-import", "Cancel"))
                Debug.Log(Start(true, null));
        }

        [MenuItem("G3/World/Stop World Import", priority = 102)]
        static void MenuStop() => Debug.Log(Stop());

        [MenuItem("G3/World/Rebuild Master Scene + Backdrop", priority = 103)]
        static void MenuMaster() => Debug.Log(BuildMasterNow());

        /// <summary>
        /// Starts (or resumes) the batch. <paramref name="cellFilter"/>: null = whole world, or "ix0,iz0,ix1,iz1"
        /// (inclusive cell rectangle). <paramref name="rebuildMaster"/>: also rebuild the master scene and backdrop
        /// (they are always built when missing).
        /// </summary>
        public static string Start(bool force, string cellFilter, bool rebuildMaster = false)
        {
            if (SessionState.GetBool(KeyRunning, false)) return "already running: " + Status();
            SessionState.SetBool(KeyRunning, true);
            SessionState.SetBool(KeyForce, force);
            SessionState.SetBool(KeyMaster, rebuildMaster || force);
            SessionState.SetString(KeyFilter, cellFilter ?? "");
            SessionState.SetString(KeyDone, "");
            SessionState.SetString(KeyStats, "0,0,0,0,0,0");
            SessionState.SetString(KeyStart, DateTime.Now.Ticks.ToString());
            File.WriteAllText(StatusFile, $"started {DateTime.Now:HH:mm:ss} force={force} filter={cellFilter}\n");
            Hook();
            return "started";
        }

        public static string Stop()
        {
            bool was = SessionState.GetBool(KeyRunning, false);
            SessionState.SetBool(KeyRunning, false);
            Unhook();
            Log("stopped by request");
            return was ? "stopped" : "not running";
        }

        public static string Status()
        {
            if (!File.Exists(StatusFile)) return "no status";
            var lines = File.ReadAllLines(StatusFile);
            return (SessionState.GetBool(KeyRunning, false) ? "RUNNING\n" : "idle\n") + string.Join("\n", lines.Skip(Math.Max(0, lines.Length - 6)));
        }

        /// <summary>Batch mode entry point: imports everything synchronously, then exits.</summary>
        public static void RunHeadless()
        {
            bool force = Environment.GetCommandLineArgs().Contains("-g3force");
            int code = 0;
            try { Debug.Log(ImportAllSync(force)); }
            catch (Exception ex) { Debug.LogException(ex); code = 1; }
            EditorApplication.Exit(code);
        }

        /// <summary>Imports every cell, the master scene and the build settings synchronously (batch mode, fresh setup).</summary>
        public static string ImportAllSync(bool force)
        {
            var sw = Stopwatch.StartNew();
            var index = WorldIndex.Build();
            var manifest = BuildManifest(index);
            int n = 0;
            using (var ctx = new CellContext())
            {
                foreach (int i in Queue(manifest, force, null, new HashSet<int>()).ToList())
                {
                    Debug.Log(ImportCell(index, manifest, i, ctx, force));
                    if (++n % 10 == 0)
                    {
                        // Cleanup unloads unused assets, the manifest included: save it first and load it back
                        EditorUtility.SetDirty(manifest);
                        AssetDatabase.SaveAssets();
                        ctx.Cleanup();
                        manifest = AssetDatabase.LoadAssetAtPath<G3WorldManifest>(ManifestPath);
                    }
                }
            }
            EditorUtility.SetDirty(manifest);
            AssetDatabase.SaveAssets();
            Debug.Log(BuildMaster(index, manifest));
            UpdateBuildSettings(manifest);
            return $"G3 world import finished in {sw.Elapsed} ({n} cells)";
        }

        // ------------------------------------------------------------------ job runner

        static WorldIndex _index;
        static G3WorldManifest _manifest;
        static List<int> _queue;
        static CellContext _ctx;
        static int _progressId = -1;
        static double _nextTick;
        static int _sinceCleanup;
        static int _total;
        static double _waitClean;

        static void Hook()
        {
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
        }

        static void Unhook()
        {
            EditorApplication.update -= Tick;
            _ctx?.Dispose();
            _ctx = null;
            _queue = null;
            _index = null;
            if (_progressId >= 0 && Progress.Exists(_progressId)) Progress.Remove(_progressId);
            _progressId = -1;
        }

        static void Tick()
        {
            if (!SessionState.GetBool(KeyRunning, false)) { Unhook(); return; }
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode) return;
            if (EditorApplication.timeSinceStartup < _nextTick) return;
            _nextTick = EditorApplication.timeSinceStartup + 0.25;   // leave the editor some breathing room between cells

            try
            {
                if (_index == null)
                {
                    var sw = Stopwatch.StartNew();
                    _index = WorldIndex.Build();
                    _manifest = BuildManifest(_index);
                    var done = DoneSet();
                    _queue = Queue(_manifest, SessionState.GetBool(KeyForce, false), SessionState.GetString(KeyFilter, ""), done).ToList();
                    _total = _queue.Count + done.Count;
                    Log($"index: {_index.Count} entities in {_index.Cells.Count(c => c != null && c.Count > 0)} cells ({sw.ElapsedMilliseconds} ms); {_queue.Count} cells to import");
                    if (_progressId < 0 || !Progress.Exists(_progressId))
                        _progressId = Progress.Start("G3 world import", "cells", Progress.Options.None);
                    return;
                }

                if (_queue.Count > 0)
                {
                    int i = _queue[0];
                    _queue.RemoveAt(0);
                    if (_ctx == null) _ctx = new CellContext();
                    string r;
                    try { r = ImportCell(_index, _manifest, i, _ctx, SessionState.GetBool(KeyForce, false)); }
                    catch (Exception ex)
                    {
                        r = $"cell {i} FAILED: {ex}";
                        Debug.LogException(ex);
                    }
                    MarkDone(i);
                    Log(r);
                    int doneCount = DoneSet().Count;
                    if (_progressId >= 0 && Progress.Exists(_progressId))
                        Progress.Report(_progressId, doneCount / (float)Math.Max(1, _total), $"{doneCount}/{_total} cells");
                    if (++_sinceCleanup >= 10)
                    {
                        _sinceCleanup = 0;
                        _ctx.Cleanup();
                    }
                    return;
                }

                // Finish: backdrop + master (when missing or requested), build settings.
                if (SessionState.GetBool(KeyMaster, false) || !File.Exists(MasterPath))
                {
                    if (!OpenScenesClean() && (_waitClean += 0.25) < 120) return;
                    Log(BuildMaster(_index, _manifest));
                }
                UpdateBuildSettings(_manifest);
                long startTicks = long.TryParse(SessionState.GetString(KeyStart, "0"), out var t) ? t : DateTime.Now.Ticks;
                Log($"finished: {_total} cells, wall time {TimeSpan.FromTicks(DateTime.Now.Ticks - startTicks)}; {DiskSummary()}");
                SessionState.SetBool(KeyRunning, false);
                Unhook();
            }
            catch (Exception ex)
            {
                Log("ERROR " + ex);
                Debug.LogException(ex);
                SessionState.SetBool(KeyRunning, false);
                Unhook();
            }
        }

        static HashSet<int> DoneSet()
        {
            var s = SessionState.GetString(KeyDone, "");
            return new HashSet<int>(s.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(int.Parse));
        }

        static void MarkDone(int i)
        {
            var s = SessionState.GetString(KeyDone, "");
            SessionState.SetString(KeyDone, s.Length == 0 ? i.ToString() : s + "," + i);
        }

        static void Log(string line)
        {
            try { File.AppendAllText(StatusFile, $"[{DateTime.Now:HH:mm:ss}] {line}\n"); } catch { /* ignore */ }
        }

        static IEnumerable<int> Queue(G3WorldManifest manifest, bool force, string filter, HashSet<int> done)
        {
            RectInt? rect = null;
            if (!string.IsNullOrEmpty(filter))
            {
                var p = filter.Split(',').Select(int.Parse).ToArray();
                rect = new RectInt(p[0], p[1], p[2] - p[0] + 1, p[3] - p[1] + 1);
            }
            // Nearest to Faring first, so the interesting area is usable early.
            var faring = manifest.CellOf(new Vector3(655, 0, 586));
            return Enumerable.Range(0, manifest.cells.Count)
                .Where(i =>
                {
                    var c = manifest.cells[i];
                    if (!c.HasScene || done.Contains(i)) return false;
                    if (rect.HasValue && !rect.Value.Contains(new Vector2Int(c.ix, c.iz))) return false;
                    return force || !File.Exists(c.scenePath);
                })
                .OrderBy(i => (new Vector2(manifest.cells[i].ix, manifest.cells[i].iz) - (Vector2)faring).sqrMagnitude);
        }

        // ------------------------------------------------------------------ index + manifest

        /// <summary>All static visual entities of the world, bucketed by cell (AABB centre).</summary>
        public sealed class WorldIndex
        {
            public List<(WorldEntity e, string sector)>[] Cells;
            public int Count;

            public static WorldIndex Build()
            {
                var all = G3WorldImporter.LoadEntities(null, null);
                var idx = new WorldIndex { Cells = new List<(WorldEntity, string)>[CountX * CountZ], Count = all.Count };
                foreach (var t in all)
                {
                    var c = G3WorldImporter.CentreXZ(t.e);
                    int ix = Mathf.Clamp(Mathf.FloorToInt((c.x - Origin.x) / CellSize), 0, CountX - 1);
                    int iz = Mathf.Clamp(Mathf.FloorToInt((c.y - Origin.y) / CellSize), 0, CountZ - 1);
                    int i = iz * CountX + ix;
                    (idx.Cells[i] ??= new List<(WorldEntity, string)>()).Add(t);
                }
                return idx;
            }
        }

        public static bool IsSea(WorldEntity e) =>
            e.Visual == VisualKind.Mesh && e.Light == null && e.Resource != null &&
            (e.Resource.IndexOf("Oceanmesh", StringComparison.OrdinalIgnoreCase) >= 0 ||
             e.Resource.IndexOf("Seamesh", StringComparison.OrdinalIgnoreCase) >= 0);

        public static string LandscapeRegion(WorldEntity e)
        {
            if (e.Visual != VisualKind.Mesh || e.Resource == null) return null;
            int k = e.Resource.IndexOf("_Landscape_Cell_", StringComparison.OrdinalIgnoreCase);
            if (k < 0 || !e.Resource.StartsWith("G3_", StringComparison.OrdinalIgnoreCase)) return null;
            return e.Resource.Substring(3, k - 3);   // Myrtana / Varant / Nordmar
        }

        static G3WorldManifest BuildManifest(WorldIndex index)
        {
            Directory.CreateDirectory(CellFolder);
            var manifest = AssetDatabase.LoadAssetAtPath<G3WorldManifest>(ManifestPath);
            bool create = manifest == null;
            if (create) manifest = ScriptableObject.CreateInstance<G3WorldManifest>();
            var old = manifest.cells.ToDictionary(c => (c.ix, c.iz));

            manifest.origin = Origin;
            manifest.cellSize = CellSize;
            manifest.countX = CountX;
            manifest.countZ = CountZ;
            manifest.masterScenePath = MasterPath;
            manifest.cells = new List<G3WorldManifest.Cell>(CountX * CountZ);
            var coverers = new HashSet<int>[CountX * CountZ];

            for (int iz = 0; iz < CountZ; iz++)
            for (int ix = 0; ix < CountX; ix++)
            {
                int i = iz * CountX + ix;
                var list = index.Cells[i];
                var cell = new G3WorldManifest.Cell { ix = ix, iz = iz };
                if (list != null && list.Count > 0)
                {
                    bool allSea = true;
                    var regions = new Dictionary<string, int>();
                    Bounds b = default;
                    bool hasB = false;
                    foreach (var (e, _) in list)
                    {
                        if (!IsSea(e)) allSea = false;
                        var eb = G3WorldImporter.BoundsM(e);
                        if (!hasB) { b = eb; hasB = true; } else b.Encapsulate(eb);
                        var reg = LandscapeRegion(e);
                        if (reg != null)
                        {
                            regions[reg] = regions.TryGetValue(reg, out int n) ? n + 1 : 1;
                            // Terrain overlap → coverers of the squares it touches (shrunk 1 m to ignore touching edges).
                            int x0 = Mathf.Clamp(Mathf.FloorToInt((eb.min.x + 1 - Origin.x) / CellSize), 0, CountX - 1);
                            int x1 = Mathf.Clamp(Mathf.FloorToInt((eb.max.x - 1 - Origin.x) / CellSize), 0, CountX - 1);
                            int z0 = Mathf.Clamp(Mathf.FloorToInt((eb.min.z + 1 - Origin.y) / CellSize), 0, CountZ - 1);
                            int z1 = Mathf.Clamp(Mathf.FloorToInt((eb.max.z - 1 - Origin.y) / CellSize), 0, CountZ - 1);
                            for (int z = z0; z <= z1; z++)
                            for (int x = x0; x <= x1; x++)
                                (coverers[z * CountX + x] ??= new HashSet<int>()).Add(i);
                        }
                    }
                    cell.kind = allSea ? G3WorldManifest.CellKind.Ocean : G3WorldManifest.CellKind.Land;
                    cell.region = regions.Count > 0 ? regions.OrderByDescending(r => r.Value).First().Key : null;
                    cell.contentBounds = b;
                    cell.entities = list.Count;
                    cell.scenePath = $"{CellFolder}/{G3WorldManifest.SceneName(ix, iz)}.unity";
                    if (old.TryGetValue((ix, iz), out var o) && File.Exists(cell.scenePath))
                    {
                        cell.imported = o.imported;
                        cell.meshes = o.meshes; cell.trees = o.trees; cell.lights = o.lights; cell.plants = o.plants;
                    }
                }
                manifest.cells.Add(cell);
            }
            for (int i = 0; i < coverers.Length; i++)
                manifest.cells[i].coverers = coverers[i] != null ? coverers[i].Where(k => k != i).OrderBy(k => k).ToArray() : Array.Empty<int>();

            // Cells without terrain inherit the region of the nearest cell that has one.
            var withRegion = manifest.cells.Where(c => c.region != null).ToList();
            foreach (var c in manifest.cells)
                if (c.region == null && withRegion.Count > 0)
                    c.region = withRegion.OrderBy(w => (w.ix - c.ix) * (w.ix - c.ix) + (w.iz - c.iz) * (w.iz - c.iz)).First().region;

            if (create) AssetDatabase.CreateAsset(manifest, ManifestPath);
            else EditorUtility.SetDirty(manifest);
            AssetDatabase.SaveAssetIfDirty(manifest);
            return manifest;
        }

        // ------------------------------------------------------------------ cells

        /// <summary>Asset cache + tree catalog shared across cells; Cleanup() frees memory between cells.</summary>
        sealed class CellContext : IDisposable
        {
            public G3AssetCache Cache = new G3AssetCache(G3WorldImporter.DataDir, new UrpMaterialFactory());
            public TreeCatalog Catalog = G3TreeSetup.GetOrCreateCatalog();

            public void Cleanup()
            {
                Cache.ReleaseLoaded();
                Catalog = null;
                EditorUtility.UnloadUnusedAssetsImmediate(true);
                GC.Collect();
                Catalog = G3TreeSetup.GetOrCreateCatalog();
            }

            public void Dispose() => Cache?.Dispose();
        }

        static string ImportCell(WorldIndex index, G3WorldManifest manifest, int i, CellContext ctx, bool force)
        {
            var sw = Stopwatch.StartNew();
            var cell = manifest.cells[i];
            var list = index.Cells[i];
            float cx = Origin.x + (cell.ix + 0.5f) * CellSize, cz = Origin.y + (cell.iz + 0.5f) * CellSize;

            // A cell left open in the editor (e.g. by "Open Cells Around Scene Camera") makes SaveScene to the same
            // path fail silently — close it first (discarding edits; cells are generated content).
            var open = SceneManager.GetSceneByPath(cell.scenePath);
            if (open.IsValid() && open.isLoaded && SceneManager.sceneCount > 1)
                EditorSceneManager.CloseScene(open, true);

            var prevActive = SceneManager.GetActiveScene();
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            try
            {
                SceneManager.SetActiveScene(scene);
                var root = new GameObject(G3WorldManifest.SceneName(cell.ix, cell.iz));
                root.transform.position = Vector3.zero;
                var st = new G3WorldImporter.PlaceStats();
                int created0 = ctx.Cache.CreatedAssets;

                AssetDatabase.StartAssetEditing();
                try
                {
                    G3WorldImporter.PlaceEntities(list, root.transform, ctx.Cache, ctx.Catalog, lights: true, groupBySector: false, st);
                }
                finally
                {
                    AssetDatabase.StopAssetEditing();
                }

                string veg = "";
                int plants = 0;
                if (cell.kind == G3WorldManifest.CellKind.Land)
                {
                    try
                    {
                        veg = G3VegetationImporter.ImportRegion(cx, cz, CellSize * 0.5f, root.transform, rebuild: force);
                        var vr = root.GetComponentInChildren<G3VegetationRenderer>();
                        plants = vr != null && vr.data != null ? vr.data.instanceCount : 0;
                        if (plants == 0 && vr != null) UnityEngine.Object.DestroyImmediate(vr.gameObject);
                    }
                    catch (Exception ex) { veg = "vegetation failed: " + ex.Message; }

                    // NPCs and creatures spawned in this cell (.lrentdat, cached prefabs per look).
                    try { veg += " | npc: " + G3NpcImporter.ImportCellNpcs(cx, cz, CellSize * 0.5f, root.transform); }
                    catch (Exception ex) { veg += " | npc failed: " + ex.Message; }
                }

                string hook = CallHook("G3.Unity.Editor.G3CollisionBuilder", "Build", root, root.transform);
                if (UnpackTreePrefabs) UnpackTrees(root);
                int deferred = DeferBulkContent(root);

                if (!EditorSceneManager.SaveScene(scene, cell.scenePath))
                    throw new Exception($"SaveScene failed for {cell.scenePath}");
                cell.meshes = st.Placed; cell.trees = st.Trees; cell.lights = st.Lights; cell.plants = plants;
                cell.imported = true;
                EditorUtility.SetDirty(manifest);
                AssetDatabase.SaveAssetIfDirty(manifest);
                return $"{G3WorldManifest.SceneName(cell.ix, cell.iz)} {cell.kind} {cell.region}: {list.Count} ent, {st.Placed} meshes, {st.Missing} missing, " +
                       $"{st.Trees} trees, {st.Lights} lights, {plants} plants, {ctx.Cache.CreatedAssets - created0} new assets{hook}; {deferred} deferred; {sw.ElapsedMilliseconds} ms" +
                       (veg.Contains("failed") ? " | " + veg : "");
            }
            finally
            {
                if (prevActive.IsValid() && prevActive.isLoaded) SceneManager.SetActiveScene(prevActive);
                if (scene.IsValid()) EditorSceneManager.CloseScene(scene, true);
            }
        }

        /// <summary>Unpack the tree prefab instances of each cell before it is saved (see <see cref="UnpackTrees"/>).</summary>
        public static bool UnpackTreePrefabs = true;

        /// <summary>
        /// Unpacks the tree prefab instances under the cell's "Trees" group into plain objects. A saved prefab instance is
        /// merged with its prefab asset when the scene loads — in the editor that "Prefabs.MergePrefabs" step cost
        /// 0.5–2.7 s per cell for ~1000 trees (Docs/performance.md, "Startup"); unpacked objects load like any other
        /// (Cell_19_19, 971 SpeedTree prefabs: worst Play-mode load frame 1.49 s → 0.24 s; scene file 15 → 26 MB).
        /// The player is not affected either way (the build merges prefab instances).
        /// Trees are not edited through their prefabs in the cells, and every rebuild places them again.
        /// </summary>
        public static int UnpackTrees(GameObject root)
        {
            var trees = root.transform.Find("Trees");
            if (trees == null) return 0;
            int n = 0;
            foreach (Transform t in trees)
                if (PrefabUtility.IsOutermostPrefabInstanceRoot(t.gameObject))
                {
                    PrefabUtility.UnpackPrefabInstance(t.gameObject, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
                    n++;
                }
            return n;
        }

        /// <summary>
        /// Saves the cell's NPCs and replacement trees inactive and lists them in a <see cref="G3DeferredActivation"/>
        /// on the cell root, which wakes them time-sliced after the scene loaded (Docs/performance.md: the one-frame
        /// integration of ~10–20k objects per cell was the main streaming hitch).
        /// </summary>
        static int DeferBulkContent(GameObject root)
        {
            var list = new List<GameObject>();
            // NPCs / creatures (top-most G3Character of each actor).
            foreach (var c in root.GetComponentsInChildren<G3.Unity.Characters.G3Character>(true))
            {
                var go = c.gameObject;
                if (go.activeSelf && (go.transform.parent == null || go.transform.parent.GetComponentInParent<G3.Unity.Characters.G3Character>(true) == null))
                    list.Add(go);
            }
            // Replacement trees (children of the "Trees" group, see G3WorldImporter.PlaceEntities).
            var trees = root.transform.Find("Trees");
            if (trees != null)
                foreach (Transform t in trees)
                    if (t.gameObject.activeSelf) list.Add(t.gameObject);
            if (list.Count == 0) return 0;
            foreach (var go in list) go.SetActive(false);
            var da = root.GetComponent<G3DeferredActivation>() ?? root.AddComponent<G3DeferredActivation>();
            da.targets = list.ToArray();
            return list.Count;
        }

        /// <summary>
        /// Calls a public static hook of another agent if that type exists (compiles without it).
        /// Trailing optional parameters are filled with their defaults.
        /// </summary>
        public static string CallHook(string typeName, string method, params object[] args)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType(typeName, false);
                if (t == null) continue;
                var m = t.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .Where(x => x.Name == method)
                    .FirstOrDefault(x =>
                    {
                        var ps = x.GetParameters();
                        return ps.Length >= args.Length && ps.Skip(args.Length).All(p => p.IsOptional);
                    });
                if (m == null) return $"; hook {typeName}.{method}: no matching overload";
                var ps2 = m.GetParameters();
                var call = new object[ps2.Length];
                for (int i = 0; i < call.Length; i++) call[i] = i < args.Length ? args[i] : ps2[i].DefaultValue;
                try
                {
                    var r = m.Invoke(null, call);
                    string s = r?.ToString();
                    return $"; {t.Name}.{method}" + (!string.IsNullOrEmpty(s) && s.Length < 160 && !(r is Component) ? ": " + s : "");
                }
                catch (Exception ex)
                {
                    var inner = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                    Debug.LogException(inner);
                    return $"; hook {t.Name}.{method} failed: {inner.Message}";
                }
            }
            return "";
        }

        // ------------------------------------------------------------------ master scene + build settings

        public static string BuildMasterNow()
        {
            var index = WorldIndex.Build();
            var manifest = BuildManifest(index);
            var r = BuildMaster(index, manifest);
            UpdateBuildSettings(manifest);
            return r;
        }

        /// <summary>True if the open scenes can be swapped out and restored without losing anything.</summary>
        public static bool OpenScenesClean()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                if (s.isDirty || (s.isLoaded && string.IsNullOrEmpty(s.path) && s.rootCount > 0)) return false;
            }
            return true;
        }

        /// <summary>Faring spawn point (metres): lower-town street (zone bbc17932, security None), a bit above the ground.
        /// Not the upper castle (~646,84,576): that Political zone needs 75 Orc fame, orcs attack the hero there.</summary>
        public static readonly Vector3 FaringSpawn = new Vector3(568, 69, 576);

        static string BuildMaster(WorldIndex index, G3WorldManifest manifest)
        {
            var sw = Stopwatch.StartNew();
            // Scene-global lookups of the setup helpers (FindAnyObjectByType, Camera.main, GameObject.Find) must only see
            // the master: build it alone (Single) when the open scenes are saved, restoring them afterwards.
            SceneSetup[] restore = null;
            bool single = OpenScenesClean();
            if (single) restore = EditorSceneManager.GetSceneManagerSetup();
            else
            {
                var existing = SceneManager.GetSceneByPath(MasterPath);
                if (existing.IsValid() && existing.isLoaded)
                {
                    if (SceneManager.sceneCount == 1) EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
                    EditorSceneManager.CloseScene(existing, true);
                }
            }
            var prevActive = SceneManager.GetActiveScene();
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, single ? NewSceneMode.Single : NewSceneMode.Additive);
            string backdropSummary, hook, wind;
            try
            {
                SceneManager.SetActiveScene(scene);

                // Sun + environment (weather-driven sun, ambient, fog, sky).
                var sunGo = new GameObject("Directional Light");
                var sun = sunGo.AddComponent<Light>();
                sun.type = LightType.Directional;
                sun.shadows = LightShadows.Soft;
                RenderSettings.sun = sun;
                var envGo = new GameObject("G3 Environment");
                var env = envGo.AddComponent<G3Environment>();
                env.weatherState = "Myrtana";
                var setupEnv = G3EnvironmentSetup.SetupScene();
                if (setupEnv != env)
                {
                    // SetupScene found another open scene's environment first: configure ours the same way.
                    env.weather = setupEnv.weather;
                    env.skyMaterial = setupEnv.skyMaterial;
                }
                env.sun = sun;
                RenderSettings.skybox = env.skyMaterial;
                RenderSettings.defaultReflectionMode = UnityEngine.Rendering.DefaultReflectionMode.Skybox;
                env.Apply();
                // Wind (wind agent).
                wind = CallHook("G3.Unity.Editor.G3WindSetup", "SetupScene");
                EnsureComponentInScene(scene, "G3.Unity.G3Wind", "G3Wind", c => SetField(c, "environment", env));

                // Low-poly backdrop.
                var backdropGo = new GameObject("G3 Lowpoly Backdrop");
                var backdrop = backdropGo.AddComponent<G3LowpolyBackdrop>();
                using (var cache = new G3AssetCache(G3WorldImporter.DataDir, new UrpMaterialFactory()))
                    backdropSummary = G3LowpolyImporter.Build(backdrop, manifest, cache);

                // Camera (fly camera at Faring); the player agent's setup turns it into the player camera.
                var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
                var cam = camGo.AddComponent<Camera>();
                camGo.AddComponent<AudioListener>();
                camGo.AddComponent<FlyCamera>();
                camGo.transform.position = new Vector3(610, 135, 520);
                camGo.transform.LookAt(new Vector3(655, 90, 586));
                cam.farClipPlane = 6000;
                cam.clearFlags = CameraClearFlags.Skybox;

                // Streamer.
                var streamerGo = new GameObject("G3 World Streamer");
                var streamer = streamerGo.AddComponent<G3WorldStreamer>();
                streamer.manifest = manifest;
                streamer.backdrop = backdrop;
                streamer.environment = env;

                // Player (player agent): spawns at Faring; cells have their own colliders, so none are built here.
                if (single)
                {
                    hook = CallHook("G3.Unity.Editor.G3PlayerSetup", "SetupScene", (Vector3?)FaringSpawn, false);
                    if (hook.Contains("no matching overload")) hook = CallHook("G3.Unity.Editor.G3PlayerSetup", "SetupScene", (Vector3?)FaringSpawn);
                }
                else hook = "; player setup skipped (other scenes have unsaved changes)";
                var player = scene.GetRootGameObjects().FirstOrDefault(g => g.name == "G3Player");
                if (player != null)
                {
                    player.transform.position = FaringSpawn;
                    streamer.target = player.transform;
                    streamer.holdUntilReady = player;
                }
                if (cam != null) cam.farClipPlane = Mathf.Max(cam.farClipPlane, 6000);
                // Music, ambience, world sound emitters, player footsteps (audio agent).
                hook += CallHook("G3.Unity.Editor.G3AudioSetup", "SetupScene");

                EditorSceneManager.SaveScene(scene, MasterPath);
            }
            finally
            {
                if (single)
                {
                    if (restore != null && restore.Length > 0) EditorSceneManager.RestoreSceneManagerSetup(restore);
                }
                else
                {
                    if (prevActive.IsValid() && prevActive.isLoaded && prevActive != scene) SceneManager.SetActiveScene(prevActive);
                    if (scene.IsValid() && SceneManager.sceneCount > 1) EditorSceneManager.CloseScene(scene, true);
                }
            }
            return $"master {MasterPath}: {backdropSummary}{wind}{hook}; {sw.ElapsedMilliseconds} ms";
        }

        /// <summary>Adds a component (by type name, compile-safe) to the scene if no root object of the scene has one.</summary>
        static void EnsureComponentInScene(Scene scene, string typeName, string goName, Action<Component> init)
        {
            Type t = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                if ((t = asm.GetType(typeName, false)) != null) break;
            if (t == null) return;
            foreach (var root in scene.GetRootGameObjects())
                if (root.GetComponentInChildren(t, true) != null) return;
            var c = new GameObject(goName).AddComponent(t);
            init?.Invoke(c);
        }

        static void SetField(object o, string name, object value)
        {
            var f = o.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance);
            if (f != null && (value == null || f.FieldType.IsInstanceOfType(value))) f.SetValue(o, value);
        }

        public static void UpdateBuildSettings(G3WorldManifest manifest)
        {
            var scenes = EditorBuildSettings.scenes
                .Where(s => !s.path.StartsWith(SceneFolder + "/", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var world = new List<EditorBuildSettingsScene>();
            if (File.Exists(MasterPath)) world.Add(new EditorBuildSettingsScene(MasterPath, true));
            foreach (var c in manifest.cells)
                if (c.HasScene && File.Exists(c.scenePath)) world.Add(new EditorBuildSettingsScene(c.scenePath, true));
            // Master first so a player build starts in the world.
            EditorBuildSettings.scenes = world.Take(1).Concat(scenes).Concat(world.Skip(1)).ToArray();
        }

        public static string DiskSummary()
        {
            long Size(string dir) => Directory.Exists(dir) ? new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0;
            return $"disk: G3Imported {Size("Assets/G3Imported") / 1048576} MB, Scenes/World {Size(SceneFolder) / 1048576} MB";
        }
    }
}
