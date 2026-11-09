using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using G3.Formats.Animation;
using G3.Formats.Pak;
using G3.Formats.World;
using G3.Unity.Characters;
using G3.Unity.Editor.Characters;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace G3.Unity.Editor
{
    /// <summary>
    /// NPC and monster prefabs, and (old behaviour, optional) placing them into scenes — Docs/formats-animation.md §8.
    /// Spawns come from the enabled sectors' .lrentdat files (gCNPC_PS + eCVisualAnimation_PS entities with their
    /// Body/Head/hair child entities); one prefab per unique visual is cached under <see cref="PrefabDir"/>, a Resources
    /// folder: at runtime the NPC simulation spawns the views around the player (Docs/npc-simulation.md), so the world
    /// cells no longer contain NPCs. <see cref="BakeIntoCells"/> brings back the old static NPCs in cell scenes.
    /// </summary>
    public static class G3NpcImporter
    {
        public const string PrefabDir = G3CharacterImporter.Root + "/Resources/" + G3NpcPrefabs.ResourceFolder;
        /// <summary>Where the prefabs were cached before they moved to a Resources folder (moved on first use).</summary>
        const string OldPrefabDir = G3CharacterImporter.Root + "/Prefabs/NPC";
        const string BakePref = "G3World.Npc.BakeIntoCells";
        const string BakeMenu = "G3/World/Bake Static NPCs into Cells (old)";

        /// <summary>
        /// Old behaviour: the world importers place static NPCs (idle loop) into the cell scenes. Off by default — the NPC
        /// simulation owns the NPCs and spawns their views at runtime. Stored in EditorPrefs; menu <see cref="BakeMenu"/>.
        /// </summary>
        public static bool BakeIntoCells
        {
            get => EditorPrefs.GetBool(BakePref, false);
            set => EditorPrefs.SetBool(BakePref, value);
        }

        [MenuItem(BakeMenu, priority = 120)]
        static void ToggleBake() => BakeIntoCells = !BakeIntoCells;

        [MenuItem(BakeMenu, true)]
        static bool ToggleBakeValidate()
        {
            Menu.SetChecked(BakeMenu, BakeIntoCells);
            return true;
        }

        /// <summary>Hook of the world importers: the cell's NPCs when <see cref="BakeIntoCells"/> is on, else nothing.</summary>
        public static string ImportCellNpcs(float xm, float zm, float halfm, Transform parent) =>
            BakeIntoCells ? ImportRegion(xm, zm, halfm, parent) : "spawned at runtime by the NPC simulation";
        public const string TestScene = "Assets/Scenes/Test_NPCs.unity";
        const float S = 0.01f;

        /// <summary>Also place birds (2,600 seagulls circling in sky flocks, shown frozen in their fly loop).</summary>
        public static bool IncludeBirds = true;

        static List<NpcSpawn> _spawns, _allSpawns;
        static List<string> _motions;
        static readonly Dictionary<string, GameObject> _prefabs = new Dictionary<string, GameObject>(StringComparer.OrdinalIgnoreCase);
        public static readonly List<string> Errors = new List<string>();

        /// <summary>All NPC/monster spawns of the world (enabled entities, test sectors excluded). Loaded once (~2 s).</summary>
        public static List<NpcSpawn> Spawns
        {
            get
            {
                if (_spawns != null) return _spawns;
                using (var projects = new G3FileSystem(G3WorldImporter.DataDir, "Projects_compiled"))
                {
                    const string vol = "Projects_compiled/";
                    var layout = WorldLayout.Build(projects.Files.Select(f => f.Path), p => projects.Get(vol + p)?.Read());
                    var errors = new List<string>();
                    _spawns = NpcSpawns.LoadWorld(layout, p => projects.Get(vol + p)?.Read(), errors)
                        .Where(s => s.Enabled && !NpcSpawns.IsTestSector(s.Sector)).ToList();
                    // Start where the routine puts them, not in the level editor's parking rows (formats-animation §8.6).
                    RoutinePoints.LoadWorld(layout, p => projects.Get(vol + p)?.Read(), errors).PlaceAtRoutineStart(_spawns);
                    Errors.AddRange(errors);
                }
                return _spawns;
            }
        }

        /// <summary>
        /// <see cref="Spawns"/> plus the spawns of the sectors that are switched off at the start (G3 SetSectorStatus,
        /// liberated towns' *_NPC_02, story camps): their entities are all marked disabled, yet the story switches them on,
        /// so their looks need prefabs and motions too (story-4). Not placed into cells. Loaded once.
        /// </summary>
        public static List<NpcSpawn> AllSpawns
        {
            get
            {
                if (_allSpawns != null) return _allSpawns;
                var list = new List<NpcSpawn>(Spawns);
                using (var projects = new G3FileSystem(G3WorldImporter.DataDir, "Projects_compiled"))
                {
                    const string vol = "Projects_compiled/";
                    var layout = WorldLayout.Build(projects.Files.Select(f => f.Path), p => projects.Get(vol + p)?.Read(), includeDisabled: true);
                    layout.Sectors.RemoveAll(sec => sec.Enabled || NpcSpawns.IsTestSector(sec.Name));
                    var errors = new List<string>();
                    list.AddRange(NpcSpawns.LoadWorld(layout, p => projects.Get(vol + p)?.Read(), errors).Where(sp => !string.IsNullOrEmpty(sp.Name)));
                    Errors.AddRange(errors);
                }
                return _allSpawns = list;
            }
        }

        /// <summary>Forget loaded spawns and prefab lookups (after data or importer changes).</summary>
        public static void ClearCache()
        {
            _spawns = null;
            _allSpawns = null;
            _motions = null;
            _prefabs.Clear();
        }

        public static bool IsBird(NpcSpawn s) => s.ActorStem.StartsWith("G3_Seagull", StringComparison.OrdinalIgnoreCase) ||
                                                 s.ActorStem.StartsWith("G3_Vulture", StringComparison.OrdinalIgnoreCase);

        /// <summary>The player's own entity (PC_Hero): built as the player hero by G3HeroImporter, not placed as an NPC.</summary>
        public static bool IsPlayer(NpcSpawn s) => string.Equals(s.Name, "PC_Hero", StringComparison.OrdinalIgnoreCase);

        /// <summary>Spawns whose origin lies in the square region (metres, Unity X/Z).</summary>
        public static List<NpcSpawn> InRegion(float xm, float zm, float halfm) =>
            // Half-open [min, max) so adjacent world cells never both take an NPC lying exactly on their border.
            Spawns.Where(s => s.X * S >= xm - halfm && s.X * S < xm + halfm && s.Z * S >= zm - halfm && s.Z * S < zm + halfm &&
                              (IncludeBirds || !IsBird(s)) && !IsPlayer(s)).ToList();

        /// <summary>
        /// Instantiates the NPCs and monsters of a square region (centre xm/zm, half size halfm, metres) under
        /// <paramref name="parent"/> (a child "NPCs" group is created). Placement = routine start pose (else the entity world matrix), cm → m.
        /// Builds missing prefabs on the fly. Returns a one-line summary.
        /// </summary>
        public static string ImportRegion(float xm, float zm, float halfm, Transform parent)
        {
            var sw = Stopwatch.StartNew();
            var list = InRegion(xm, zm, halfm);
            if (list.Count == 0) return $"NPCs ({xm},{zm}) ±{halfm} m: none";
            var group = new GameObject("NPCs").transform;
            if (parent != null) group.SetParent(parent, false);
            int placed = 0, failed = 0, created = 0, humans = 0;
            G3CharacterImporter importer = null;
            try
            {
                foreach (var s in list)
                {
                    var prefab = GetPrefab(s, ref importer, ref created);
                    if (prefab == null) { failed++; continue; }
                    var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, group);
                    go.name = string.IsNullOrEmpty(s.Name) ? prefab.name : s.Name;
                    G3WorldImporter.ApplyMatrix(go.transform, s.World);
                    var ch = go.GetComponent<G3Character>();
                    if (ch != null) ch.StartPhase = (uint)s.Guid.GetHashCode() % 1000 / 1000f;
                    placed++;
                    if (s.Parts.Count > 0) humans++;
                }
            }
            finally
            {
                if (importer != null)
                {
                    foreach (var l in importer.Log.Where(l => !l.StartsWith("prefab ")).Distinct().Take(20)) Errors.Add(l);
                    importer.Dispose();
                    AssetDatabase.SaveAssets();
                }
            }
            return $"NPCs ({xm},{zm}) ±{halfm} m: {placed} placed ({humans} humans/orcs, {placed - humans} monsters/animals), " +
                   $"{failed} failed, {created} new prefabs, {sw.ElapsedMilliseconds} ms";
        }

        /// <summary>Cached prefab for the spawn's visual (actor, parts, switches); built when missing.</summary>
        public static GameObject GetPrefab(NpcSpawn s, ref G3CharacterImporter importer, ref int created)
        {
            string key = s.VisualKey;
            if (_prefabs.TryGetValue(key, out var p)) return p;
            MoveOldPrefabs();
            string path = $"{PrefabDir}/{G3NpcPrefabs.FileName(key)}.prefab";
            p = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (p == null)
            {
                try
                {
                    importer ??= new G3CharacterImporter();
                    _motions ??= importer.FindMotions("");
                    p = importer.ImportNpc(s, path, s.PickIdle(_motions));
                    created++;
                }
                catch (Exception ex)
                {
                    Errors.Add($"{s.Name} [{key}]: {ex.Message}");
                    p = null;
                }
            }
            _prefabs[key] = p;
            return p;
        }

        /// <summary>Moves prefabs cached in the old folder into the Resources folder (GUIDs and scene references are kept).</summary>
        public static void MoveOldPrefabs()
        {
            if (!AssetDatabase.IsValidFolder(OldPrefabDir) || AssetDatabase.IsValidFolder(PrefabDir)) return;
            string parent = Path.GetDirectoryName(PrefabDir).Replace('\\', '/');
            if (!AssetDatabase.IsValidFolder(parent))
                AssetDatabase.CreateFolder(Path.GetDirectoryName(parent).Replace('\\', '/'), Path.GetFileName(parent));
            string error = AssetDatabase.MoveAsset(OldPrefabDir, PrefabDir);
            if (!string.IsNullOrEmpty(error)) Errors.Add($"moving {OldPrefabDir} → {PrefabDir}: {error}");
        }

        /// <summary>
        /// Prefabs of every spawn, those of switched-off sectors included (or of those within <paramref name="halfm"/> of xm/zm when halfm &gt; 0), built
        /// where missing — the runtime views need them, since cells no longer bake NPCs. Returns a summary.
        /// </summary>
        public static string BuildPrefabs(float xm = 0, float zm = 0, float halfm = 0)
        {
            var sw = Stopwatch.StartNew();
            var list = halfm > 0 ? InRegion(xm, zm, halfm) : AllSpawns.Where(sp => !IsPlayer(sp)).ToList();
            int created = 0, failed = 0;
            G3CharacterImporter importer = null;
            try
            {
                foreach (var key in list.GroupBy(sp => sp.VisualKey))
                    if (GetPrefab(key.First(), ref importer, ref created) == null) failed++;
            }
            finally
            {
                importer?.Dispose();
                AssetDatabase.SaveAssets();
            }
            return $"NPC prefabs: {list.Select(sp => sp.VisualKey).Distinct().Count()} visuals, {created} built, {failed} failed, {sw.ElapsedMilliseconds} ms";
        }

        /// <summary>
        /// Re-imports cached NPC prefabs whose skinned parts no longer fit their shared mesh assets: the part meshes
        /// (Characters/Meshes) are shared by all prefabs, and a mesh rebuilt with a new bone layout (another importer, newer
        /// code) leaves older prefabs with <see cref="SkinnedMeshRenderer.bones"/> in the old order — the limbs stretch into
        /// spikes. A prefab is stale when a freshly built rig of its spawn has other meshes, bone names or materials (remaster switch). Returns a summary.
        /// </summary>
        public static string RebuildStalePrefabs()
        {
            var sw = Stopwatch.StartNew();
            int checkedCount = 0, rebuilt = 0;
            var names = new List<string>();
            using var importer = new G3CharacterImporter();
            _motions ??= importer.FindMotions("");
            foreach (var group in AllSpawns.Where(sp => !IsPlayer(sp)).GroupBy(sp => sp.VisualKey))
            {
                var spawn = group.First();
                string path = $"{PrefabDir}/{G3NpcPrefabs.FileName(group.Key)}.prefab";
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null) continue;
                checkedCount++;
                var fresh = importer.BuildNpcRig(spawn, "StaleCheck", out _, out _, out _, out _);
                bool stale;
                try { stale = !SameSkins(prefab, fresh); }
                finally { UnityEngine.Object.DestroyImmediate(fresh); }
                if (!stale) continue;
                _prefabs[group.Key] = importer.ImportNpc(spawn, path, spawn.PickIdle(_motions));
                rebuilt++;
                if (names.Count < 10) names.Add(prefab.name);
            }
            AssetDatabase.SaveAssets();
            return $"NPC prefabs: {checkedCount} checked, {rebuilt} stale rebuilt, {sw.ElapsedMilliseconds} ms" +
                   (names.Count > 0 ? "\n  " + string.Join("\n  ", names) : "");
        }

        [MenuItem("G3/Characters/Check NPC Heads", priority = 121)]
        static void CheckHeadsMenu() => UnityEngine.Debug.Log("[G3] " + CheckHeads());

        /// <summary>
        /// Lists the built NPC prefabs (every human/orc look, switched-off sectors included) without a visible head: no
        /// prefab, no head renderer (the head actor failed to load when the prefab was built — fix-15: Diego), a head
        /// renderer without mesh, disabled or on an inactive object. Fix with <see cref="RebuildStalePrefabs"/> (a missing
        /// head renderer makes the prefab stale). CLI twin without Unity: <c>G3Cli anim npc heads</c>. Returns a summary.
        /// </summary>
        public static string CheckHeads()
        {
            int looks = 0;
            var bad = new List<string>();
            foreach (var group in AllSpawns.Where(sp => !IsPlayer(sp)).GroupBy(sp => sp.VisualKey))
            {
                var parts = group.First().Parts;
                if (!parts.Any(p => !p.IsMesh)) continue; // monsters/animals: one actor, no head part
                var heads = parts.Where(p => !p.IsMesh && !p.IsBody).Select(p => p.Stem).ToList();
                looks++;
                string path = $"{PrefabDir}/{G3NpcPrefabs.FileName(group.Key)}.prefab";
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                string reason = null;
                if (prefab == null) reason = "no prefab";
                else if (heads.Count == 0) reason = "look has no head part";
                else
                    foreach (var head in heads)
                    {
                        var r = prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                            .FirstOrDefault(x => x.name.StartsWith(head, StringComparison.OrdinalIgnoreCase));
                        reason = r == null ? $"no {head} renderer (head actor failed at build)" :
                                 r.sharedMesh == null ? $"{head}: no mesh" :
                                 !r.enabled ? $"{head}: renderer disabled" :
                                 !r.gameObject.activeSelf ? $"{head}: object inactive" : null;
                        if (reason != null) break;
                    }
                if (reason != null) bad.Add($"{group.Key}: {reason} ({string.Join(", ", group.Select(x => x.Name).Distinct().Take(3))})");
            }
            return $"NPC heads: {looks} human/orc looks, {bad.Count} without a visible head" +
                   (bad.Count > 0 ? "\n  " + string.Join("\n  ", bad.Take(50)) : "");
        }

        /// <summary>Same skinned meshes (by renderer name) with the same bone names in the same order.</summary>
        static bool SameSkins(GameObject prefab, GameObject fresh)
        {
            var have = prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true).ToDictionary(r => r.name);
            foreach (var want in fresh.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (!have.TryGetValue(want.name, out var r) || r.sharedMesh != want.sharedMesh) return false;
                var a = r.bones;
                var b = want.bones;
                if (a.Length != b.Length) return false;
                for (int i = 0; i < a.Length; i++)
                    if (a[i] == null || b[i] == null || a[i].name != b[i].name) return false;
            }
            return SameMaterials(prefab, fresh);
        }

        /// <summary>
        /// The materials of every renderer (skinned parts, static heads, hand props) match: the remaster switch and R1
        /// texture sets swap materials only, with the same meshes and bones (editor-runner-20: Gorn kept Simple Lit after
        /// RebuildCharacters with remaster ON). Materials are compared by asset path (they are assets at fixed paths, so
        /// one updated in place needs no rebuild), in-memory ones by shader and base texture.
        /// </summary>
        static bool SameMaterials(GameObject prefab, GameObject fresh)
        {
            var have = new Dictionary<string, string>();
            foreach (var r in prefab.GetComponentsInChildren<Renderer>(true)) have[RendererKey(prefab.transform, r)] = MaterialsKey(r);
            foreach (var r in fresh.GetComponentsInChildren<Renderer>(true))
                if (have.TryGetValue(RendererKey(fresh.transform, r), out var key) && key != MaterialsKey(r)) return false; // missing parts: SameSkins
            return true;
        }

        static string RendererKey(Transform root, Renderer r)
        {
            var parts = new List<string>();
            for (var t = r.transform; t != null && t != root; t = t.parent) parts.Add(t.name);
            parts.Reverse();
            return string.Join("/", parts) + ":" + r.GetType().Name;
        }

        static string MaterialsKey(Renderer r) => string.Join("|", r.sharedMaterials.Select(MaterialKey));

        static string MaterialKey(Material m)
        {
            if (m == null) return "-";
            string path = AssetDatabase.GetAssetPath(m);
            if (!string.IsNullOrEmpty(path)) return path + "#" + m.name;
            var tex = m.HasProperty("_BaseMap") ? m.GetTexture("_BaseMap") : m.mainTexture;
            return $"mem:{(m.shader != null ? m.shader.name : "-")}:{(tex != null ? AssetDatabase.GetAssetPath(tex) + "#" + tex.name : "-")}";
        }

        /// <summary>
        /// Removes the baked "NPCs" groups from the world's cell scenes (cells imported before the simulation spawned
        /// NPC views), so the old static NPCs do not stand next to the live ones. Returns a summary.
        /// </summary>
        public static string StripNpcsFromCells()
        {
            var manifest = AssetDatabase.LoadAssetAtPath<G3WorldManifest>(G3WorldBatch.ManifestPath);
            if (manifest == null) return "no world manifest";
            int cells = 0, removed = 0;
            var sw = Stopwatch.StartNew();
            foreach (var cell in manifest.cells.Where(c => c.HasScene))
            {
                var scene = EditorSceneManager.OpenScene(cell.scenePath, OpenSceneMode.Additive);
                try
                {
                    int n = 0;
                    foreach (var root in scene.GetRootGameObjects())
                    {
                        var group = root.transform.Find("NPCs");
                        if (group == null) continue;
                        n += group.childCount;
                        UnityEngine.Object.DestroyImmediate(group.gameObject);
                        var da = root.GetComponent<G3DeferredActivation>();
                        if (da != null && da.targets != null) da.targets = da.targets.Where(t => t != null).ToArray();
                    }
                    if (n > 0)
                    {
                        EditorSceneManager.SaveScene(scene);
                        cells++;
                        removed += n;
                    }
                }
                finally
                {
                    EditorSceneManager.CloseScene(scene, true);
                }
            }
            return $"removed {removed} baked NPCs from {cells} cell scenes in {sw.Elapsed.TotalSeconds:F0} s";
        }

        // ------------------------------------------------------------------------------------------ test scene

        /// <summary>
        /// Assets/Scenes/Test_NPCs.unity = copy of the Faring region scene (G3_Region_655_586, imported by the world
        /// importer) + the region's NPCs. Rebuilds the NPC group if the scene exists.
        /// </summary>
        public static string BuildTestScene(float xm = 655, float zm = 586, float halfm = 150)
        {
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(TestScene) == null)
            {
                string src = $"Assets/Scenes/G3_Region_{xm:F0}_{zm:F0}.unity";
                if (AssetDatabase.LoadAssetAtPath<SceneAsset>(src) == null) G3WorldImporter.ImportRegion(xm, zm, halfm);
                if (!AssetDatabase.CopyAsset(src, TestScene)) throw new IOException($"copy {src} → {TestScene} failed");
            }
            var scene = EditorSceneManager.OpenScene(TestScene, OpenSceneMode.Single);
            foreach (var go in scene.GetRootGameObjects())
                if (go.name == "NPCs") UnityEngine.Object.DestroyImmediate(go);
            string summary = ImportRegion(xm, zm, halfm, null);
            EditorSceneManager.SaveScene(scene);
            return summary + (Errors.Count > 0 ? "\n" + string.Join("\n", Errors.Distinct().Take(25)) : "");
        }

        /// <summary>Opens the test scene and points the main camera from <paramref name="eye"/> at <paramref name="target"/> (metres).</summary>
        public static string ShowTestScene(Vector3 eye, Vector3 target, float fov = 50)
        {
            var scene = EditorSceneManager.GetActiveScene();
            if (scene.path != TestScene) scene = EditorSceneManager.OpenScene(TestScene, OpenSceneMode.Single);
            var cam = Camera.main;
            if (cam == null) return "no main camera";
            cam.transform.position = eye;
            cam.transform.LookAt(target);
            cam.fieldOfView = fov;
            cam.nearClipPlane = 0.1f;
            return $"{scene.path}: camera {eye} → {target}";
        }

        /// <summary>Opens the test scene and frames the NPC named <paramref name="name"/> (first match) from the front.</summary>
        public static string FrameNpc(string name, float dist = 2.5f, float height = 1.5f, float side = 0)
        {
            var scene = EditorSceneManager.GetActiveScene();
            if (scene.path != TestScene) scene = EditorSceneManager.OpenScene(TestScene, OpenSceneMode.Single);
            var t = GameObject.Find("NPCs/" + name);
            if (t == null) return "not found: " + name;
            var p = t.transform.position;
            var eye = p + t.transform.forward * dist + t.transform.right * side + Vector3.up * height;
            return ShowTestScene(eye, p + Vector3.up * (height * 0.8f), 45) + " npc at " + p;
        }
    }
}
