using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using G3.Audio;
using G3.Formats.Pak;
using G3.Formats.Templates;
using G3.Formats.World;
using UnityEditor;
using UnityEngine;
using F = G3.Formats.Audio;

namespace G3.Unity.Editor
{
    /// <summary>
    /// Bakes the audio-relevant world data into Assets/G3Imported/Audio/G3AudioWorld.asset: music/weather zones,
    /// placed eCAudioEmitter_PS sounds plus the emitters of templates spawned by gCEffect_PS (campfires, fireplaces),
    /// each assigned to its world-manifest cell, and the SpeedTree ambient-environment grid.
    /// </summary>
    public static class G3AudioWorldBaker
    {
        public const string AssetPath = G3AudioImporter.Root + "/G3AudioWorld.asset";
        const float M = 0.01f;
        const float GridCell = 25f;

        [MenuItem("G3/Audio/Bake Audio World Data", priority = 231)]
        static void Menu() { Bake(out var s); UnityEngine.Debug.Log(s); }

        public static G3AudioWorldAsset Bake(out string summary)
        {
            var sw = Stopwatch.StartNew();
            var manifest = AssetDatabase.LoadAssetAtPath<G3WorldManifest>(G3WorldBatch.ManifestPath);
            var world = new F.AudioWorld();
            var trees = new List<(float x, float z, AmbientEnvironment env)>();
            ReadWorld(world, trees);
            var templateEmitters = EffectTemplateEmitters();

            var asset = AssetDatabase.LoadAssetAtPath<G3AudioWorldAsset>(AssetPath);
            bool create = asset == null;
            if (create) asset = ScriptableObject.CreateInstance<G3AudioWorldAsset>();

            asset.zones = world.Zones.Select(AudioCatalogConverter.Zone).ToList();
            asset.emitters = new List<SoundEmitterData>();
            int placed = 0, fromEffects = 0, outside = 0;
            foreach (var e in world.Emitters)
            {
                if (!e.Enabled || string.IsNullOrEmpty(e.Sound)) continue;
                asset.emitters.Add(AudioCatalogConverter.Emitter(e, e.Name, e.X, e.Y, e.Z));
                placed++;
            }
            foreach (var fx in world.Effects)
            {
                if (fx.IsItem || fx.IsNpc || !templateEmitters.TryGetValue(fx.Effect, out var em)) continue;
                asset.emitters.Add(AudioCatalogConverter.Emitter(em, fx.Name, fx.X + fx.OffX, fx.Y + fx.OffY, fx.Z + fx.OffZ));
                fromEffects++;
            }
            foreach (var e in asset.emitters)
            {
                e.cell = CellIndex(manifest, e.position);
                if (e.cell < 0) outside++;
            }

            var origin = manifest != null ? manifest.origin : new Vector2(-4250, -4250);
            float w = manifest != null ? manifest.countX * manifest.cellSize : 7000, d = manifest != null ? manifest.countZ * manifest.cellSize : 6750;
            asset.ambientGrid = AmbientEnvironmentGrid.Build(origin.x, origin.y, w, d, GridCell, trees);

            Directory.CreateDirectory(G3AudioImporter.Root);
            if (create) AssetDatabase.CreateAsset(asset, AssetPath);
            else EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();
            asset.ResetCaches();

            int treeCells = asset.ambientGrid.cells.Count(c => c != 0);
            summary = $"audio world: {asset.zones.Count} zones, {placed} placed emitters + {fromEffects} effect emitters " +
                      $"({outside} outside the cell grid{(manifest == null ? ", NO MANIFEST" : "")}), {trees.Count} ambient trees in {treeCells} grid cells, " +
                      $"{sw.Elapsed.TotalSeconds:F0} s";
            return asset;
        }

        static int CellIndex(G3WorldManifest manifest, V3 p)
        {
            if (manifest == null) return -1;
            var c = manifest.CellOf(new Vector3(p.x, p.y, p.z));
            int i = manifest.Index(c.x, c.y);
            return i >= 0 && i < manifest.cells.Count && manifest.cells[i].HasScene ? i : -1;
        }

        /// <summary>All sector nodes and lrentdats of G3_World_01, with raw property sets (as `G3Cli audio world`).</summary>
        static void ReadWorld(F.AudioWorld world, List<(float, float, AmbientEnvironment)> trees)
        {
            const string vol = "Projects_compiled/";
            using (var fs = new G3FileSystem(G3WorldImporter.DataDir, "Projects_compiled"))
            {
                var layout = WorldLayout.Build(fs.Files.Select(f => f.Path), p => fs.Get(vol + p)?.Read());
                foreach (var s in layout.Sectors)
                foreach (var path in s.Nodes.Concat(s.Lrentdats))
                {
                    var entry = fs.Get(vol + path);
                    if (entry == null) continue;
                    List<WorldEntity> list;
                    try { list = EntityArchive.Read(entry.Read(), false, true); }
                    catch (Exception ex) { UnityEngine.Debug.LogWarning($"G3 audio bake: {path}: {ex.Message}"); continue; }
                    if (list == null) continue;
                    foreach (var e in list)
                    {
                        world.Add(e, path);
                        var tree = e.RawPropertySets?.FirstOrDefault(ps => ps.ClassName == "eCSpeedTree_PS");
                        int env = tree?.Get("AmbientEnvironment")?.AsEnum() ?? -1;
                        if (env >= 0 && env < 6) trees.Add((e.X * M, e.Z * M, (AmbientEnvironment)env));
                    }
                }
            }
        }

        /// <summary>Effect name → the eCAudioEmitter_PS of a template it spawns (fires).</summary>
        internal static Dictionary<string, F.SoundEmitter> EffectTemplateEmitters()
        {
            var map = F.EffectMap.Read(G3AudioImporter.ReadLibraryFile("g3.efm"));
            var res = new Dictionary<string, F.SoundEmitter>(StringComparer.OrdinalIgnoreCase);
            TemplateArchive templates;
            using (var fs = new G3FileSystem(G3WorldImporter.DataDir, "Templates"))
                templates = TemplateArchive.Load(fs);
            foreach (var e in map.Effects.Values)
                foreach (var guid in e.SpawnTemplates)
                {
                    var f = templates.Get(guid);
                    var h = f?.Headers.FirstOrDefault(x => x.Get("eCAudioEmitter_PS") != null);
                    if (h == null || res.ContainsKey(e.Name)) continue;
                    var em = F.AudioWorld.ReadEmitter(h.Get("eCAudioEmitter_PS"), h.Source);
                    if (em.Enabled && !string.IsNullOrEmpty(em.Sound)) res[e.Name] = em;
                }
            return res;
        }
    }
}
