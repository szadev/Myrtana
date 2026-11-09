using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using G3.Formats.Animation;
using G3.Formats.Pak;
using G3.Formats.Templates;
using G3.Unity.Editor;
using G3.Unity.Characters;
using G3.Unity.Editor.Characters;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session;
using UnityEditor;
using UnityEngine;

namespace Myrtana.Unity.Editor
{
    /// <summary>
    /// Builds <see cref="ItemVisualCatalog"/> from the imported content pack and the game files: the static mesh of every
    /// item that has one (through the world importer's mesh cache), the hero's skinned body for every armour (through
    /// the character importer: the hero skeleton with that body actor), and the items NPCs hold (<see cref="G3HandItems.AllTemplates"/>:
    /// tools and "Fake" show items, keyed by template name, read from the G3 templates since most are not content items).
    /// Inventory icons are not built here: they come from the G3 GUI skin.
    /// Output (git-ignored): Assets/G3Imported/Myrtana/Resources/Myrtana/ItemVisuals.asset.
    /// CLI: <c>return Myrtana.Unity.Editor.ItemVisualBuilder.Build();</c>
    /// </summary>
    public static class ItemVisualBuilder
    {
        const string Root = "Assets/G3Imported/Myrtana";
        const string CatalogPath = Root + "/Resources/" + ItemVisualCatalog.ResourcePath + ".asset";
        const string HeroSkeleton = "G3_Hero_Skeleton.FXA";

        [MenuItem("Myrtana/Content/Build item visuals")]
        static void Menu() => Debug.Log("[Myrtana] " + Build());

        public static string Build()
        {
            var content = GameContent.Load(ContentLocations.Packs());
            var items = content.Database.All<ItemDef>().ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(CatalogPath));
            var catalog = AssetDatabase.LoadAssetAtPath<ItemVisualCatalog>(CatalogPath);
            if (catalog == null)
            {
                catalog = ScriptableObject.CreateInstance<ItemVisualCatalog>();
                AssetDatabase.CreateAsset(catalog, CatalogPath);
            }
            catalog.meshes.Clear();
            catalog.bodies.Clear();
            var problems = new List<string>();
            AddMeshes(catalog, items, problems);
            int actors = AddActorMeshes(catalog, items, problems);
            AddBodies(catalog, items, problems);
            int props = AddHandProps(catalog, problems);
            EditorUtility.SetDirty(catalog);
            AssetDatabase.SaveAssets();
            return $"item visuals: {catalog.meshes.Count} meshes ({props} hand props, {actors} actors), {catalog.bodies.Count} armour bodies ({catalog.bodies.Sum(b => b.parts.Count)} meshes); " +
                   $"{problems.Count} problems{string.Concat(problems.Take(8).Select(p => "\n  " + p))}";
        }

        static void AddMeshes(ItemVisualCatalog catalog, List<ItemDef> items, List<string> problems)
        {
            using (var cache = new G3AssetCache(G3WorldImporter.DataDir, new UrpMaterialFactory()))
                foreach (var mesh in items.Select(i => i.Visual?.Mesh).Where(m => m != null).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (cache.TryGetMesh(mesh, out var m, out var mats))
                        catalog.meshes.Add(new ItemVisualCatalog.MeshEntry { key = mesh, mesh = m, materials = mats });
                    else problems.Add("mesh " + mesh);
                }
        }

        /// <summary>
        /// Weapons whose visual is an actor (bows and crossbows, whose string is skinned; <see cref="ItemVisual.Body"/>):
        /// the actor's mesh in its bind pose, keyed by the actor name, through the character importer (prefab and mesh
        /// assets under G3Imported/Characters) [interp: the string does not move].
        /// </summary>
        static int AddActorMeshes(ItemVisualCatalog catalog, List<ItemDef> items, List<string> problems)
        {
            int added = 0;
            using (var imp = new G3CharacterImporter { RemasterLods = false })
                foreach (var body in items.Where(i => i.Slot != EquipSlot.Armor && i.Visual?.Mesh == null && i.Visual?.Body != null)
                                          .Select(i => i.Visual.Body).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    string mesh = body + ".FXA";
                    try
                    {
                        var prefab = imp.Import("Item_" + body, mesh, new[] { mesh }, Array.Empty<string>());
                        var smr = prefab != null ? prefab.GetComponentInChildren<SkinnedMeshRenderer>() : null;
                        if (smr == null || smr.sharedMesh == null) { problems.Add("actor mesh " + mesh); continue; }
                        catalog.meshes.Add(new ItemVisualCatalog.MeshEntry { key = body, mesh = smr.sharedMesh, materials = smr.sharedMaterials });
                        added++;
                    }
                    catch (Exception e)
                    {
                        problems.Add($"actor mesh {mesh}: {e.Message}");
                    }
                }
            return added;
        }

        /// <summary>Meshes of the items NPCs hold during interactions, keyed by template name (Docs/formats-animation.md §8.7).</summary>
        static int AddHandProps(ItemVisualCatalog catalog, List<string> problems)
        {
            G3.Formats.Items.ItemCatalog templates;
            using (var fs = new G3FileSystem(G3WorldImporter.DataDir, "Templates"))
                templates = G3.Formats.Items.ItemCatalog.Build(TemplateArchive.Load(fs));
            int added = 0;
            using (var cache = new G3AssetCache(G3WorldImporter.DataDir, new UrpMaterialFactory()))
                foreach (var template in G3HandItems.AllTemplates())
                {
                    string mesh = templates.Get(template)?.Mesh;
                    if (string.IsNullOrEmpty(mesh)) { problems.Add("hand prop template " + template); continue; }
                    if (!cache.TryGetMesh(mesh, out var m, out var mats)) { problems.Add($"hand prop mesh {mesh} ({template})"); continue; }
                    catalog.meshes.Add(new ItemVisualCatalog.MeshEntry { key = template, mesh = m, materials = mats });
                    added++;
                }
            return added;
        }

        static void AddBodies(ItemVisualCatalog catalog, List<ItemDef> items, List<string> problems)
        {
            var bodies = items.Where(i => i.Slot == EquipSlot.Armor && i.Visual?.Body != null)
                              .Select(i => (i.Visual.Body, i.Visual.MaterialSwitch)).Distinct().ToList();
            using (var importer = new G3CharacterImporter { RemasterLods = false })
                foreach (var (body, materialSwitch) in bodies)
                {
                    var entry = BuildBody(importer, body, materialSwitch);
                    if (entry != null) catalog.bodies.Add(entry);
                    else problems.Add("body " + body);
                }
        }

        /// <summary>The hero skeleton dressed in one body actor; every skinned mesh of the body with materials and bone names.</summary>
        static ItemVisualCatalog.BodyEntry BuildBody(G3CharacterImporter importer, string body, int materialSwitch)
        {
            var spawn = new NpcSpawn { Name = body, Actor = HeroSkeleton, Enabled = true, World = new float[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 } };
            spawn.Parts.Add(new NpcPart { Resource = body + ".FXA", Switch = materialSwitch, EntityName = body });
            GameObject root = null;
            try
            {
                root = importer.BuildNpcRig(spawn, body, out _, out _, out _, out _);
                // The body actor's meshes are named after it (GFG_Hero_Body_King, GFG_Hero_Body_King_2); else whatever the rig has.
                var skins = root.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r => r.sharedMesh != null).ToList();
                var own = skins.Where(r => r.sharedMesh.name.IndexOf(body, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                if (own.Count > 0) skins = own;
                if (skins.Count == 0) return null;
                var entry = new ItemVisualCatalog.BodyEntry { key = ItemVisualCatalog.BodyKey(body, materialSwitch) };
                foreach (var skin in skins)
                    entry.parts.Add(new ItemVisualCatalog.BodyPart
                    {
                        mesh = skin.sharedMesh,
                        materials = skin.sharedMaterials,
                        bones = skin.bones.Select(b => b != null ? b.name : null).ToArray(),
                        rootBone = skin.rootBone != null ? skin.rootBone.name : null,
                    });
                return entry;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Myrtana] armour body {body}: {ex.Message}");
                return null;
            }
            finally
            {
                if (root != null) UnityEngine.Object.DestroyImmediate(root);
            }
        }
    }
}
