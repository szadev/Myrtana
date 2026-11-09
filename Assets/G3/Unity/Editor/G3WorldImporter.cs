using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using G3.Formats.Pak;
using G3.Formats.World;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace G3.Unity.Editor
{
    /// <summary>
    /// Builds a Unity scene for a square region of the Gothic 3 world.
    /// Usage from the CLI: unity command eval "return G3.Unity.Editor.G3WorldImporter.ImportRegion(650, 585, 150);"
    /// </summary>
    public static class G3WorldImporter
    {
        public static string DataDir
        {
            get => G3.Formats.G3Install.Pref(EditorPrefs.GetString("G3.DataDir", "")) ?? G3.Formats.G3Install.DataDirOrHint;
            set => EditorPrefs.SetString("G3.DataDir", value);
        }

        [MenuItem("G3/Import Faring (test, 150 m)")]
        static void ImportFaring() => UnityEngine.Debug.Log(ImportRegion(655, 586, 150));

        public static string ResultFile => System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath, "../Logs/g3import.txt"));

        /// <summary>
        /// For the CLI, whose eval must finish within a few seconds: queue the import on the next editor tick
        /// and write the summary (or the exception) to Logs/g3import.txt.
        /// </summary>
        public static string Schedule(float centerX, float centerZ, float halfSize)
        {
            System.IO.File.WriteAllText(ResultFile, "running");
            EditorApplication.delayCall += () =>
            {
                string result;
                try { result = "done\n" + ImportRegion(centerX, centerZ, halfSize); }
                catch (Exception ex) { result = "error\n" + ex; }
                System.IO.File.WriteAllText(ResultFile, result);
            };
            return "scheduled";
        }

        /// <param name="centerX">Region centre, metres (Unity X).</param>
        /// <param name="centerZ">Region centre, metres (Unity Z).</param>
        /// <param name="halfSize">Half side of the square region, metres.</param>
        public static string ImportRegion(float centerX, float centerZ, float halfSize, bool lights = true)
        {
            var sw = Stopwatch.StartNew();
            float cx = centerX * 100, cz = centerZ * 100, hs = halfSize * 100;
            var entities = LoadEntities(node => TileMayIntersect(node, cx, cz, hs), e => Intersects(e, cx, cz, hs));
            long tParse = sw.ElapsedMilliseconds;

            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            var root = new GameObject("G3World");
            var catalog = G3TreeSetup.GetOrCreateCatalog();
            var st = new PlaceStats();

            AssetDatabase.StartAssetEditing();
            using (var cache = new G3AssetCache(DataDir, new UrpMaterialFactory()))
            {
                try
                {
                    PlaceEntities(entities, root.transform, cache, catalog, lights, groupBySector: true, st,
                        (i, n, res) => EditorUtility.DisplayCancelableProgressBar("G3 import", $"{i}/{n} {res}", i / (float)n));
                }
                finally
                {
                    EditorUtility.ClearProgressBar();
                    AssetDatabase.StopAssetEditing();
                    AssetDatabase.SaveAssets();
                }

                // Frame the camera on the region.
                var cam = Camera.main;
                if (cam != null)
                {
                    cam.transform.position = new Vector3(centerX - halfSize * 0.5f, 120, centerZ - halfSize * 0.5f);
                    cam.transform.LookAt(new Vector3(centerX, 60, centerZ));
                    cam.farClipPlane = 5000;
                    if (cam.GetComponent<FlyCamera>() == null) cam.gameObject.AddComponent<FlyCamera>();
                }

                // Grass / small plants from the eCVegetation_PS grids (GPU-instanced; region asset is cached).
                string vegSummary;
                try { vegSummary = G3VegetationImporter.ImportRegion(centerX, centerZ, halfSize, root.transform, rebuild: false); }
                catch (Exception ex) { vegSummary = "vegetation failed: " + ex.Message; }
                try { vegSummary += " | npc: " + G3NpcImporter.ImportCellNpcs(centerX, centerZ, halfSize, root.transform); }
                catch (Exception ex) { vegSummary += " | npc failed: " + ex.Message; }

                // Gothic 3 sun, ambient, fog and sky from WeatherStates.g3ws.
                try { G3EnvironmentSetup.SetupScene(); }
                catch (Exception ex) { cache.Warnings.Add("environment: " + ex.Message); }
                // Wind (wind agent) — looked up by name so this compiles without it.
                G3WorldBatch.CallHook("G3.Unity.Editor.G3WindSetup", "SetupScene");

                string scenePath = $"Assets/Scenes/G3_Region_{centerX:F0}_{centerZ:F0}.unity";
                EditorSceneManager.SaveScene(scene, scenePath);

                var warn = cache.Warnings.Distinct().Take(15);
                return $"region ({centerX},{centerZ}) ±{halfSize} m: {entities.Count} entities, {st.Placed} meshes placed, " +
                       $"{st.Missing} missing, {st.Trees} trees, {st.Lights} lights, {cache.CreatedAssets} new assets; " +
                       $"parse {tParse} ms, total {sw.ElapsedMilliseconds} ms; scene {scenePath}\n" +
                       $"vegetation: {vegSummary}\n" +
                       string.Join("\n", warn);
            }
        }

        // ------------------------------------------------------------------ shared by the region and world importers

        /// <summary>Static, visible, non-NPC/item entity (the static scene of Docs/formats-world.md §7.5).</summary>
        public static bool IsStaticVisual(WorldEntity e) =>
            e.Enabled && e.RenderingEnabled && !e.IsNpc && !e.IsItem && (e.Visual != VisualKind.None || e.Light != null);

        /// <summary>
        /// Reads all enabled sector nodes of G3_World_01 (about 3.5 s for the whole world) plus the usable objects of the
        /// sector .lrentdat files (DynamicObjects) and returns the static visual entities that pass <paramref name="filter"/>;
        /// <paramref name="nodeFilter"/> can skip whole node files early (it is not applied to .lrentdat files).
        /// </summary>
        public static List<(WorldEntity e, string sector)> LoadEntities(Func<string, bool> nodeFilter, Func<WorldEntity, bool> filter)
        {
            var entities = new List<(WorldEntity e, string sector)>();
            using (var projects = new G3FileSystem(DataDir, "Projects_compiled"))
            {
                const string vol = "Projects_compiled/";
                // Story-switchable sectors that are off at the start are read too (their entities are all marked disabled):
                // PlaceEntities puts every switchable sector's objects under a G3SectorContent that follows the story.
                var layout = WorldLayout.Build(projects.Files.Select(f => f.Path), p => projects.Get(vol + p)?.Read(), includeDisabled: true);
                StorySectors = G3StorySectorList.Load(layout);
                layout.Sectors.RemoveAll(s => !s.Enabled && !StorySectors.ContainsKey(s.Name));
                foreach (var sector in layout.Sectors)
                foreach (var node in sector.Nodes)
                {
                    if (nodeFilter != null && !nodeFilter(node)) continue;
                    var entry = projects.Get(vol + node);
                    var list = entry != null ? EntityArchive.Read(entry.Read(), decodeVegetation: false) : null;
                    if (list == null) continue;
                    if (!sector.Enabled) foreach (var e in list) e.Enabled = true;
                    foreach (var e in list)
                        if (IsStaticVisual(e) && (filter == null || filter(e)))
                            entities.Add((e, sector.Name));
                }
                // Usable objects (beds, benches, thrones …) exist only as dynamic entities (Docs/formats-world.md §7.4).
                foreach (var sector in layout.Sectors)
                foreach (var lrentdat in sector.Lrentdats)
                {
                    var entry = projects.Get(vol + lrentdat);
                    var list = entry != null ? EntityArchive.Read(entry.Read(), decodeVegetation: false) : null;
                    if (list == null) continue;
                    if (!sector.Enabled) foreach (var e in list) e.Enabled = true;
                    foreach (var e in DynamicObjects.StaticMeshes(list).Concat(DynamicObjects.StaticLights(list)))
                        if (filter == null || filter(e))
                            entities.Add((e, sector.Name));
                }
            }
            return entities;
        }

        /// <summary>
        /// The story-switchable sectors (name → on at the start) of the last <see cref="LoadEntities"/>: their objects are
        /// placed under a <see cref="G3SectorContent"/> holder ("Sector &lt;name&gt;").
        /// </summary>
        public static Dictionary<string, bool> StorySectors = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Entity XZ centre in metres: centre of the visual AABB, or the origin if the box is invalid.</summary>
        public static Vector2 CentreXZ(WorldEntity e)
        {
            var b = e.Bounds;
            return b[0] <= b[3] && b[2] <= b[5]
                ? new Vector2((b[0] + b[3]) * 0.5f, (b[2] + b[5]) * 0.5f) * G3AssetCache.Scale
                : new Vector2(e.X, e.Z) * G3AssetCache.Scale;
        }

        /// <summary>Entity AABB in metres (a point box at the origin if invalid).</summary>
        public static Bounds BoundsM(WorldEntity e)
        {
            var b = e.Bounds;
            if (b[0] <= b[3] && b[1] <= b[4] && b[2] <= b[5])
            {
                var min = new Vector3(b[0], b[1], b[2]) * G3AssetCache.Scale;
                var max = new Vector3(b[3], b[4], b[5]) * G3AssetCache.Scale;
                return new Bounds((min + max) * 0.5f, max - min);
            }
            return new Bounds(new Vector3(e.X, e.Y, e.Z) * G3AssetCache.Scale, Vector3.zero);
        }

        const string WindShader = "G3/WindLit";
        const StaticEditorFlags TreeStaticFlags = StaticEditorFlags.ContributeGI | StaticEditorFlags.OccluderStatic |
                                                  StaticEditorFlags.OccludeeStatic | StaticEditorFlags.ReflectionProbeStatic;

        public sealed class PlaceStats
        {
            public int Placed, Missing, Trees, Lights;
        }

        /// <summary>
        /// Instantiates meshes, replacement trees and point lights under <paramref name="root"/>.
        /// groupBySector: one child per sector (region scenes); otherwise "Meshes" / "Trees" / "Lights" children.
        /// progress(i, count, resource) is called every 200 entities and returns true to cancel.
        /// </summary>
        public static void PlaceEntities(IList<(WorldEntity e, string sector)> entities, Transform root, G3AssetCache cache,
            TreeCatalog catalog, bool lights, bool groupBySector, PlaceStats st, Func<int, int, string, bool> progress = null)
        {
            var groups = new Dictionary<(Transform, string), Transform>();
            Transform Group(string name, Transform parent)
            {
                var key = (parent, name);
                if (groups.TryGetValue(key, out var t)) return t;
                t = new GameObject(name).transform;
                t.SetParent(parent != null ? parent : root, false);
                groups[key] = t;
                return t;
            }

            var storyContent = new Dictionary<string, Transform>(StringComparer.OrdinalIgnoreCase);
            // A story-switchable sector's objects: "Sector <name>" (G3SectorContent, stays active) / "Content" (toggled).
            Transform StoryContent(string sector)
            {
                if (sector == null || !StorySectors.TryGetValue(sector, out bool atStart)) return null;
                if (storyContent.TryGetValue(sector, out var t)) return t;
                var holder = new GameObject("Sector " + sector);
                holder.transform.SetParent(root, false);
                var content = new GameObject("Content");
                content.transform.SetParent(holder.transform, false);
                content.SetActive(atStart);
                var switcher = holder.AddComponent<G3SectorContent>();
                switcher.sector = sector;
                switcher.enabledAtStart = atStart;
                switcher.content = content;
                return storyContent[sector] = content.transform;
            }

            for (int i = 0; i < entities.Count; i++)
            {
                var (e, sector) = entities[i];
                if (progress != null && (i + 1) % 200 == 0 && progress(i + 1, entities.Count, e.Resource)) break;
                Transform story = StoryContent(sector);
                Transform sectorGroup = story != null ? story : groupBySector ? Group(sector, null) : null;

                if (e.Visual == VisualKind.Mesh)
                {
                    if (!cache.TryGetMesh(e.Resource, out var mesh, out var mats)) { st.Missing++; }
                    else
                    {
                        bool allInvisible = true, water = false;
                        foreach (var m in mats)
                        {
                            if (!cache.IsInvisible(m)) allInvisible = false;
                            if (cache.IsWater(m)) water = true;
                        }
                        if (!allInvisible)
                        {
                            var go = new GameObject(string.IsNullOrEmpty(e.Name) ? mesh.name : e.Name);
                            go.transform.SetParent(story != null || groupBySector ? sectorGroup : Group("Meshes", null), false);
                            ApplyMatrix(go.transform, e.World);
                            go.AddComponent<MeshFilter>().sharedMesh = mesh;
                            var mr = go.AddComponent<MeshRenderer>();
                            // Invisible sub-meshes get no material, so Unity skips drawing them.
                            var shown = (Material[])mats.Clone();
                            for (int k = 0; k < shown.Length; k++) if (cache.IsInvisible(shown[k])) shown[k] = null;
                            mr.sharedMaterials = shown;
                            // No light probes in the world (see G3RenderTuning): use the ambient probe directly.
                            mr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
                            if (water) mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                            // Wind-animated materials need their own pivot: everything static except batching.
                            bool windy = false;
                            foreach (var m in shown) if (m != null && m.shader != null && m.shader.name == WindShader) windy = true;
                            if (windy) GameObjectUtility.SetStaticEditorFlags(go, TreeStaticFlags);
                            else go.isStatic = true;
                            st.Placed++;
                        }
                    }
                }
                else if (e.Visual == VisualKind.SpeedTree && catalog != null)
                {
                    // Original trees generated from the installed game (G3SpeedTreeImporter), unless switched off.
                    var original = Trees.G3SpeedTreeImporter.Enabled
                        ? Trees.G3SpeedTreeImporter.Instantiate(e, Group("Trees", sectorGroup)) : null;
                    if (original != null)
                    {
                        GameObjectUtility.SetStaticEditorFlags(original, TreeStaticFlags);
                        st.Trees++;
                    }
                    // Replacement trees: height is matched to the original bounds.
                    var prefab = original != null ? null : catalog.Resolve(e.Resource, e.Guid.GetHashCode());
                    if (prefab != null)
                    {
                        var treeParent = Group("Trees", sectorGroup);
                        var tree = (GameObject)PrefabUtility.InstantiatePrefab(prefab, treeParent);
                        tree.name = System.IO.Path.GetFileNameWithoutExtension(e.Resource);
                        ApplyMatrix(tree.transform, e.World);
                        float height = (e.Bounds[4] - e.Bounds[1]) * G3AssetCache.Scale;
                        if (e.Bounds[4] <= e.Bounds[1]) height = 8 * tree.transform.localScale.y;
                        float yaw = tree.transform.eulerAngles.y;
                        tree.transform.rotation = Quaternion.Euler(0, yaw, 0);
                        tree.transform.localScale = Vector3.one * (height / G3TreeSetup.PrefabHeight(prefab));
                        // Not batching-static: static batching bakes trees into world space and freezes the wind shader.
                        GameObjectUtility.SetStaticEditorFlags(tree, TreeStaticFlags);
                        st.Trees++;
                    }
                }

                if (lights && e.Light != null)
                {
                    var lgo = new GameObject("Light");
                    lgo.transform.SetParent(story != null || groupBySector ? sectorGroup : Group("Lights", null), false);
                    ApplyMatrix(lgo.transform, e.World);
                    lgo.transform.localPosition += lgo.transform.rotation * new Vector3(e.Light.OffX, e.Light.OffY, e.Light.OffZ) * G3AssetCache.Scale;
                    var l = lgo.AddComponent<Light>();
                    l.type = LightType.Point;
                    l.color = new Color(e.Light.R, e.Light.G, e.Light.B);
                    l.range = e.Light.Range * G3AssetCache.Scale;
                    l.intensity = 2f;
                    l.shadows = e.Light.CastShadows ? LightShadows.Soft : LightShadows.None;
                    // URP adds this lazily the first time the light is rendered (≈0.06 ms per light, hundreds in one
                    // frame when a town comes into view): add it at import. Shadows of these lights are budgeted at
                    // runtime (G3LightShadowBudget: only the nearest few cast them).
                    lgo.AddComponent<UnityEngine.Rendering.Universal.UniversalAdditionalLightData>();
                    st.Lights++;
                }
            }
        }

        /// <summary>G3 row-major matrix (rows = axes, row 3 = translation, cm) → Unity transform (metres).</summary>
        public static void ApplyMatrix(Transform t, float[] m)
        {
            var x = new Vector3(m[0], m[1], m[2]);
            var y = new Vector3(m[4], m[5], m[6]);
            var z = new Vector3(m[8], m[9], m[10]);
            t.localPosition = new Vector3(m[12], m[13], m[14]) * G3AssetCache.Scale;
            t.localRotation = Quaternion.LookRotation(z.normalized, y.normalized);
            t.localScale = new Vector3(x.magnitude, y.magnitude, z.magnitude);
        }

        static bool Intersects(WorldEntity e, float cx, float cz, float hs)
        {
            var b = e.Bounds;
            bool validBox = b[0] <= b[3];
            float minX = validBox ? b[0] : e.X, maxX = validBox ? b[3] : e.X;
            float minZ = validBox ? b[2] : e.Z, maxZ = validBox ? b[5] : e.Z;
            return maxX >= cx - hs && minX <= cx + hs && maxZ >= cz - hs && minZ <= cz + hs;
        }

        public static bool TileMayIntersect(string nodePath, float cx, float cz, float hs)
        {
            // "..._x55000y0z55000_CStat.node"
            int ix = nodePath.LastIndexOf("_x", StringComparison.Ordinal);
            if (ix < 0 || !nodePath.EndsWith("_CStat.node", StringComparison.OrdinalIgnoreCase)) return true;
            var s = nodePath.Substring(ix + 2, nodePath.Length - ix - 2 - "_CStat.node".Length);
            int iy = s.IndexOf('y'), iz = s.IndexOf('z');
            if (iy < 0 || iz < 0 || !float.TryParse(s.Substring(0, iy), out float tx) || !float.TryParse(s.Substring(iz + 1), out float tz))
                return true;
            // Objects are assigned to tiles by AABB centre; large objects can stick out, so allow a generous margin.
            const float margin = 20000;
            return Math.Abs(tx - cx) <= hs + 5000 + margin && Math.Abs(tz - cz) <= hs + 5000 + margin;
        }
    }
}
