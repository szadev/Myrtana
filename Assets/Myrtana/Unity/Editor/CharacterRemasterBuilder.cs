using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using G3.Formats.Animation;
using G3.Unity;
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
    /// Builds <see cref="G3CharacterRemasterSet"/>, the runtime character remaster (Docs/remaster-characters.md §5.6): the
    /// hero rig (world PC_Hero spawn: start body, hero head) and the hero skeleton in every armour body the content has are
    /// built twice by the character importer, once original and once with the remaster overrides (the same code the
    /// import-time switch uses, with LODs off as for the hero prefab), and every renderer that differs becomes a swap
    /// entry; renderers only the remaster has (v2 hair cards, a part's extra meshes) become extras of the part they belong to.
    /// The remastered talking head of each swapped head is built too. Prefabs and the import switch stay as they are
    /// (G3RemasterSettings may stay OFF: the original prefabs ship, the set adds the remaster on top).
    /// Output (git-ignored): <see cref="SetPath"/>; lazy swap data in <see cref="SwapDir"/> (§6.6: a
    /// <see cref="G3CharacterMeshRef"/> per remaster mesh, a copy of every swap material, the face kits): the set itself
    /// holds names and Resources paths only, so loading it loads nothing and a swap loads its mesh/maps on first use.
    /// The meshes/textures stay the importer's Characters/*/Remaster assets. Menu G3/Remaster/Build Runtime Character Set,
    /// or <c>return Myrtana.Unity.Editor.CharacterRemasterBuilder.Build();</c>.
    /// </summary>
    public static class CharacterRemasterBuilder
    {
        public const string Root = "Assets/G3Imported/Remaster/Characters";
        public const string SetPath = Root + "/Resources/" + G3CharacterRemasterSet.ResourceName + ".asset";
        /// <summary>Resources folder of the lazy swap data (mesh refs, material copies, face kits).</summary>
        public const string SwapDir = Root + "/Resources/" + G3CharacterRemasterSet.SwapFolder;
        public const string KitDir = SwapDir + "/Kits";

        // per Build(): source object -> Resources path written in this run
        static Dictionary<UnityEngine.Object, string> _written;
        const string HeroSkeleton = "G3_Hero_Skeleton.FXA";

        [MenuItem("G3/Remaster/Build Runtime Character Set (Options → Remaster)", priority = 222)]
        static void Menu() => Debug.Log(Build());

        /// <summary>Builds the set; <paramref name="armour"/> false = the hero rig only (quicker).</summary>
        public static string Build(bool armour = true)
        {
            var log = new StringBuilder();
            var set = LoadOrCreate();
            set.swaps.Clear();
            set.faceKits.Clear();
            int rigs = 0;
            _written = new Dictionary<UnityEngine.Object, string>();
            Directory.CreateDirectory(SwapDir + "/Meshes");
            Directory.CreateDirectory(SwapDir + "/Materials");
            Directory.CreateDirectory(KitDir);

            var hero = G3HeroImporter.FindHeroSpawn(out string source);
            using (var imp = new G3CharacterImporter { RemasterLods = false })
            {
                int n = Diff(imp, hero, "hero", set, log);
                log.AppendLine($"hero ({source}): {n} swaps");
                rigs++;
                if (armour)
                {
                    int bodies = 0, swapped = 0;
                    foreach (var (body, sw) in ArmourBodies(log))
                    {
                        var spawn = new NpcSpawn { Name = body, Actor = HeroSkeleton, Enabled = true, World = Identity() };
                        spawn.Parts.Add(new NpcPart { Resource = body + ".FXA", Switch = sw, EntityName = body });
                        int k = Diff(imp, spawn, ItemVisualCatalog.BodyKey(body, sw), set, log);
                        bodies++;
                        rigs++;
                        if (k > 0) swapped++;
                    }
                    log.AppendLine($"armour bodies: {bodies} built, {swapped} with remaster parts");
                }
                foreach (var l in imp.Log.Where(l => !l.StartsWith("prefab ")).Distinct().Take(12)) log.AppendLine("  " + l);
            }

            // remastered talking heads for the swapped heads
            var kitPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var head in set.swaps.Where(s => s.skinned && !s.hide && !string.IsNullOrEmpty(s.mesh) &&
                                                      s.original.StartsWith("G3_Head_", StringComparison.OrdinalIgnoreCase))
                                          .Select(s => s.original).Distinct(StringComparer.OrdinalIgnoreCase).ToList())
            {
                try
                {
                    string path = $"{KitDir}/{head}.asset";
                    var kit = G3FaceKitImporter.BuildRemasterKit(head, hero, path, out string line);
                    log.AppendLine("face kit " + line);
                    if (kit == null) continue;
                    set.faceKits.Add(new G3CharacterRemasterSet.FaceKitRef { head = head, kit = ResourcePath(path) });
                    kitPaths.Add(path);
                }
                catch (Exception ex) { log.AppendLine($"face kit {head}: {ex.Message}"); }
            }

            // lazy data no swap of this run uses any more
            var keep = new HashSet<string>(_written.Values.Select(AssetPathOf).Concat(kitPaths), StringComparer.OrdinalIgnoreCase);
            int stale = 0;
            foreach (var guid in AssetDatabase.FindAssets("", new[] { SwapDir }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (AssetDatabase.IsValidFolder(path) || keep.Contains(path)) continue;
                if (AssetDatabase.DeleteAsset(path)) stale++;
            }
            int meshes = _written.Keys.Count(o => o is Mesh), mats = _written.Keys.Count(o => o is Material);
            _written = null;

            set.built = $"{DateTime.Now:yyyy-MM-dd HH:mm}, {rigs} rigs, {set.swaps.Count} swaps, " +
                        $"{set.swaps.Sum(s => s.extras.Count)} extras, {set.faceKits.Count} face kits, lazy: {meshes} meshes, {mats} materials";
            EditorUtility.SetDirty(set);
            AssetDatabase.SaveAssets();
            return $"character remaster set {SetPath}: {set.built}, {stale} stale swap assets removed\n{log.ToString().TrimEnd()}";
        }

        /// <summary>
        /// The material table of the set (Docs/remaster-characters.md §6.3): every material on the NPC prefabs
        /// (Resources/G3Npc: humans, orcs, creatures) and the item visuals (weapons/shields in hand) that has a texture set
        /// in GameData/Remaster/Textures (R1, AI) gets its remaster material (<see cref="G3RemasterMaterials.Remaster"/>,
        /// the import-time override's own code). Importing new maps is slow (BC7, 2048): the pass stops after
        /// <paramref name="seconds"/> and reports what is left; rerun until "0 left" (finished materials are cheap).
        /// <c>return Myrtana.Unity.Editor.CharacterRemasterBuilder.BuildMaterials();</c>
        /// </summary>
        public static string BuildMaterials(float seconds = 150f)
        {
            var t0 = DateTime.Now;
            var set = LoadOrCreate();
            var all = new List<Material>();
            var seen = new HashSet<Material>();
            int prefabs = 0;
            foreach (var guid in AssetDatabase.FindAssets("t:GameObject", new[] { G3CharacterImporter.Root + "/Resources/" + G3NpcPrefabs.ResourceFolder }))
            {
                var go = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guid));
                if (go == null) continue;
                prefabs++;
                foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                    foreach (var m in r.sharedMaterials)
                        if (m != null && seen.Add(m)) all.Add(m);
            }
            var items = ItemVisualCatalog.Load();
            if (items != null)
            {
                foreach (var e in items.meshes) if (e.materials != null) foreach (var m in e.materials) if (m != null && seen.Add(m)) all.Add(m);
                foreach (var b in items.bodies) foreach (var p in b.parts) if (p.materials != null) foreach (var m in p.materials) if (m != null && seen.Add(m)) all.Add(m);
            }

            // pairs by name (the runtime matches a renderer's material by name and loads the remaster from Resources on
            // first use); a name two different materials carry is ambiguous and left out
            var ambiguous = new HashSet<string>(all.GroupBy(m => m.name).Where(g => g.Count() > 1).Select(g => g.Key), StringComparer.Ordinal);
            var pairs = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var p in set.materials)
                if (!string.IsNullOrEmpty(p.original) && !string.IsNullOrEmpty(p.remaster)) pairs[p.original] = p.remaster;
            Directory.CreateDirectory(MaterialDir);
            int done = 0, skipped = 0, left = 0;
            foreach (var m in all)
            {
                if (ambiguous.Contains(m.name)) { pairs.Remove(m.name); skipped++; continue; }
                if ((DateTime.Now - t0).TotalSeconds > seconds) { left++; continue; }
                var r = G3RemasterMaterials.Remaster(null, m);
                if (r == null || r == m) { pairs.Remove(m.name); skipped++; continue; }
                pairs[m.name] = G3CharacterRemasterSet.MaterialFolder + "/" + ResourceCopy(r).name;
                done++;
            }
            // the way back must be unique: one original per remaster (MaterialSwapMap rejects the rest)
            set.materials = pairs.GroupBy(kv => kv.Value).Select(g => g.First())
                                 .Select(kv => new G3CharacterRemasterSet.MaterialPair { original = kv.Key, remaster = kv.Value })
                                 .OrderBy(p => p.original, StringComparer.Ordinal).ToList();
            int removed = 0;
            if (left == 0)   // copies no pair uses any more
            {
                var used = new HashSet<string>(set.materials.Select(p => p.remaster.Substring(p.remaster.LastIndexOf('/') + 1)), StringComparer.Ordinal);
                foreach (var guid in AssetDatabase.FindAssets("t:Material", new[] { MaterialDir }))
                {
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    if (!used.Contains(Path.GetFileNameWithoutExtension(path)) && AssetDatabase.DeleteAsset(path)) removed++;
                }
            }
            set.built = (set.built ?? "").Split(new[] { " | materials" }, StringSplitOptions.None)[0] +
                        $" | materials {set.materials.Count} pairs ({DateTime.Now:HH:mm})";
            EditorUtility.SetDirty(set);
            AssetDatabase.SaveAssets();
            return $"character material table: {prefabs} NPC prefabs + item visuals, {all.Count} materials: " +
                   $"{done} remastered, {skipped} without texture set or ambiguous name ({ambiguous.Count} names), {left} left (rerun), " +
                   $"{set.materials.Count} pairs in the set, {removed} stale copies removed, {(DateTime.Now - t0).TotalSeconds:F0} s";
        }

        /// <summary>Resources folder of the table's remaster materials (loaded on first use at runtime, with their textures).</summary>
        public const string MaterialDir = Root + "/Resources/" + G3CharacterRemasterSet.MaterialFolder;

        /// <summary>The copy of a remaster material in <see cref="MaterialDir"/> (same shader, properties and textures).</summary>
        static Material ResourceCopy(Material r)
        {
            string path = $"{MaterialDir}/{r.name}.mat";
            var copy = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (copy == null)
            {
                copy = new Material(r) { name = r.name };
                AssetDatabase.CreateAsset(copy, path);
                return copy;
            }
            if (copy.shader != r.shader) copy.shader = r.shader;
            copy.CopyPropertiesFromMaterial(r);
            copy.enableInstancing = r.enableInstancing;
            EditorUtility.SetDirty(copy);
            return copy;
        }

        /// <summary>Resources path (no extension) of an asset under a Resources folder.</summary>
        static string ResourcePath(string assetPath)
        {
            int i = assetPath.IndexOf("/Resources/", StringComparison.Ordinal);
            string r = i >= 0 ? assetPath.Substring(i + "/Resources/".Length) : assetPath;
            return r.Substring(0, r.Length - Path.GetExtension(r).Length);
        }

        static string AssetPathOf(string resourcePath) =>
            Root + "/Resources/" + resourcePath + (resourcePath.Contains("/Meshes/") ? ".asset" : ".mat");

        static string Safe(string name) => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == '/' ? '_' : c));

        /// <summary>A free file name in <paramref name="dir"/> for <paramref name="stem"/> this run has not used yet.</summary>
        static string FreePath(string dir, string stem, string ext)
        {
            var used = new HashSet<string>(_written.Values, StringComparer.OrdinalIgnoreCase);
            string path = $"{dir}/{Safe(stem)}{ext}";
            for (int n = 2; used.Contains(ResourcePath(path)); n++) path = $"{dir}/{Safe(stem)}_{n}{ext}";
            return path;
        }

        /// <summary>The Resources path of a <see cref="G3CharacterMeshRef"/> for <paramref name="mesh"/> (written once per run).</summary>
        static string MeshPath(Mesh mesh)
        {
            if (mesh == null) return null;
            if (_written.TryGetValue(mesh, out var done)) return done;
            string path = FreePath(SwapDir + "/Meshes", $"{mesh.name}_{mesh.vertexCount}", ".asset");
            var r = AssetDatabase.LoadAssetAtPath<G3CharacterMeshRef>(path);
            if (r == null)
            {
                r = ScriptableObject.CreateInstance<G3CharacterMeshRef>();
                r.mesh = mesh;
                AssetDatabase.CreateAsset(r, path);
            }
            else if (r.mesh != mesh) { r.mesh = mesh; EditorUtility.SetDirty(r); }
            return _written[mesh] = ResourcePath(path);
        }

        /// <summary>Resources paths of copies of <paramref name="materials"/> (same shader, properties, textures).</summary>
        static string[] MaterialPaths(Material[] materials)
        {
            var result = new string[materials?.Length ?? 0];
            for (int i = 0; i < result.Length; i++)
            {
                var m = materials[i];
                if (m == null) continue;
                if (_written.TryGetValue(m, out var done)) { result[i] = done; continue; }
                string path = FreePath(SwapDir + "/Materials", m.name, ".mat");
                var copy = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (copy == null)
                {
                    copy = new Material(m) { name = Path.GetFileNameWithoutExtension(path) };
                    AssetDatabase.CreateAsset(copy, path);
                }
                else
                {
                    if (copy.shader != m.shader) copy.shader = m.shader;
                    copy.CopyPropertiesFromMaterial(m);
                    copy.enableInstancing = m.enableInstancing;
                    EditorUtility.SetDirty(copy);
                }
                result[i] = _written[m] = ResourcePath(path);
            }
            return result;
        }

        static string[] Names(Material[] materials) => (materials ?? Array.Empty<Material>()).Select(m => m != null ? m.name : null).ToArray();

        static G3CharacterRemasterSet LoadOrCreate()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SetPath));
            var set = AssetDatabase.LoadAssetAtPath<G3CharacterRemasterSet>(SetPath);
            if (set != null) return set;
            set = ScriptableObject.CreateInstance<G3CharacterRemasterSet>();
            AssetDatabase.CreateAsset(set, SetPath);
            return set;
        }

        /// <summary>Distinct (body, MaterialSwitch) of the content's armour items, as <see cref="ItemVisualBuilder"/> builds them.</summary>
        static List<(string body, int sw)> ArmourBodies(StringBuilder log)
        {
            try
            {
                var content = GameContent.Load(ContentLocations.Packs());
                return content.Database.All<ItemDef>().Where(i => i.Slot == EquipSlot.Armor && i.Visual?.Body != null)
                              .Select(i => (i.Visual.Body, i.Visual.MaterialSwitch)).Distinct().ToList();
            }
            catch (Exception ex)
            {
                log.AppendLine("armour bodies skipped (content not readable): " + ex.Message);
                return new List<(string, int)>();
            }
        }

        /// <summary>One rig original and remastered; adds a swap per differing renderer. Returns the swaps added.</summary>
        static int Diff(G3CharacterImporter imp, NpcSpawn spawn, string label, G3CharacterRemasterSet set, StringBuilder log)
        {
            GameObject a = null, b = null;
            bool was = imp.UseRemaster;
            try
            {
                imp.UseRemaster = false;
                a = imp.BuildNpcRig(spawn, label + " (original)", out _, out _, out _, out _);
                imp.UseRemaster = true;
                b = imp.BuildNpcRig(spawn, label + " (remaster)", out _, out _, out _, out _);
                return Compare(a.transform, b.transform, label, set);
            }
            catch (Exception ex)
            {
                log.AppendLine($"{label}: {ex.Message}");
                return 0;
            }
            finally
            {
                imp.UseRemaster = was;
                if (a != null) UnityEngine.Object.DestroyImmediate(a);
                if (b != null) UnityEngine.Object.DestroyImmediate(b);
            }
        }

        static int Compare(Transform original, Transform remaster, string label, G3CharacterRemasterSet set)
        {
            int added = 0;
            var oSkins = Skins(original);
            var rSkins = Skins(remaster);
            var oStatics = Statics(original);
            var rStatics = Statics(remaster);
            var mine = new List<G3CharacterRemasterSet.Swap>();

            foreach (var kv in oSkins)
            {
                var o = kv.Value;
                var swap = new G3CharacterRemasterSet.Swap
                {
                    label = $"{label}/{kv.Key}", skinned = true, original = o.sharedMesh.name, originalVertices = o.sharedMesh.vertexCount,
                    originalMaterials = Names(o.sharedMaterials),
                };
                if (rSkins.TryGetValue(kv.Key, out var r))
                {
                    if (r.sharedMesh == o.sharedMesh && r.sharedMaterials.SequenceEqual(o.sharedMaterials)) { mine.Add(swap); continue; }
                    if (Present(set, swap)) { mine.Add(set.swaps.First(x => Same(x, swap))); continue; }   // hero and armour rigs share bodies
                    swap.mesh = r.sharedMesh != o.sharedMesh ? MeshPath(r.sharedMesh) : null;
                    swap.materials = MaterialPaths(r.sharedMaterials);
                    swap.bones = r.bones.Select(t => t != null ? t.name : null).ToArray();
                    swap.rootBone = r.rootBone != null ? r.rootBone.name : null;
                }
                else swap.hide = true;
                mine.Add(swap);
                if (Add(set, swap)) added++;
            }
            foreach (var kv in oStatics)
            {
                var (of, orr) = kv.Value;
                var swap = new G3CharacterRemasterSet.Swap
                {
                    label = $"{label}/{kv.Key}", skinned = false, original = of.sharedMesh.name, originalVertices = of.sharedMesh.vertexCount,
                    originalMaterials = Names(orr.sharedMaterials),
                };
                if (rStatics.TryGetValue(kv.Key, out var r))
                {
                    if (r.filter.sharedMesh == of.sharedMesh && r.renderer.sharedMaterials.SequenceEqual(orr.sharedMaterials)) continue;
                    if (Present(set, swap)) { mine.Add(set.swaps.First(x => Same(x, swap))); continue; }
                    swap.mesh = r.filter.sharedMesh != of.sharedMesh ? MeshPath(r.filter.sharedMesh) : null;
                    swap.materials = MaterialPaths(r.renderer.sharedMaterials);
                }
                else swap.hide = true;
                mine.Add(swap);
                if (Add(set, swap)) added++;
            }

            // renderers only the remaster has: extras of the part they belong to (name prefix, else the head, else the first)
            var extras = new List<(string key, G3CharacterRemasterSet.Extra extra)>();
            foreach (var kv in rSkins.Where(kv => !oSkins.ContainsKey(kv.Key)))
            {
                var r = kv.Value;
                extras.Add((kv.Key, new G3CharacterRemasterSet.Extra
                {
                    name = r.name, parent = r.transform.parent != null && r.transform.parent != remaster ? r.transform.parent.name : null, skinned = true,
                    mesh = MeshPath(r.sharedMesh), materials = MaterialPaths(r.sharedMaterials), bones = r.bones.Select(t => t != null ? t.name : null).ToArray(),
                    rootBone = r.rootBone != null ? r.rootBone.name : null, position = r.transform.localPosition,
                    rotation = r.transform.localRotation, scale = r.transform.localScale, head = IsHead(r),
                }));
            }
            foreach (var kv in rStatics.Where(kv => !oStatics.ContainsKey(kv.Key)))
            {
                var (f, r) = kv.Value;
                extras.Add((kv.Key, new G3CharacterRemasterSet.Extra
                {
                    name = r.name, parent = r.transform.parent != null ? r.transform.parent.name : null, skinned = false,
                    mesh = MeshPath(f.sharedMesh), materials = MaterialPaths(r.sharedMaterials), position = r.transform.localPosition,
                    rotation = r.transform.localRotation, scale = r.transform.localScale, head = IsHead(r),
                }));
            }
            foreach (var (key, extra) in extras)
            {
                var owner = mine.Where(s => s.skinned && key.StartsWith(Part(s.label), StringComparison.OrdinalIgnoreCase))
                                .OrderByDescending(s => Part(s.label).Length).FirstOrDefault()
                            ?? (extra.head ? mine.FirstOrDefault(s => s.skinned && s.original != null && s.original.StartsWith("G3_Head_", StringComparison.OrdinalIgnoreCase)) : null)
                            ?? mine.FirstOrDefault(s => s.skinned);
                if (owner == null) continue;
                if (!set.swaps.Contains(owner))
                {
                    // the part itself is unchanged: an entry that keeps its mesh (and maps its materials by the table) and only adds the extra
                    owner.mesh = null;
                    owner.materials = Array.Empty<string>();
                    if (!Add(set, owner)) owner = set.swaps.First(s => Same(s, owner));
                    else added++;
                }
                if (!owner.extras.Any(e => e.name == extra.name)) owner.extras.Add(extra);
            }
            return added;
        }

        static string Part(string label) => label.Substring(label.LastIndexOf('/') + 1);

        /// <summary>Adds unless an entry for the same original mesh and materials exists (hero and armour rigs share bodies).</summary>
        static bool Add(G3CharacterRemasterSet set, G3CharacterRemasterSet.Swap swap)
        {
            if (set.swaps.Any(s => Same(s, swap))) return false;
            set.swaps.Add(swap);
            return true;
        }

        static bool Same(G3CharacterRemasterSet.Swap a, G3CharacterRemasterSet.Swap b) =>
            a.skinned == b.skinned && a.original == b.original && a.originalVertices == b.originalVertices &&
            a.originalMaterials.SequenceEqual(b.originalMaterials);

        static bool Present(G3CharacterRemasterSet set, G3CharacterRemasterSet.Swap swap) => set.swaps.Any(s => Same(s, swap));

        static Dictionary<string, SkinnedMeshRenderer> Skins(Transform root)
        {
            var d = new Dictionary<string, SkinnedMeshRenderer>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                if (r.sharedMesh != null && !d.ContainsKey(r.name)) d[r.name] = r;
            return d;
        }

        static Dictionary<string, (MeshFilter filter, MeshRenderer renderer)> Statics(Transform root)
        {
            var d = new Dictionary<string, (MeshFilter, MeshRenderer)>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in root.GetComponentsInChildren<MeshRenderer>(true))
            {
                var f = r.GetComponent<MeshFilter>();
                if (f == null || f.sharedMesh == null) continue;
                string key = (r.transform.parent != null ? r.transform.parent.name : "") + "/" + r.name;
                if (!d.ContainsKey(key)) d[key] = (f, r);
            }
            return d;
        }

        /// <summary>Head parts as G3HeroImporter counts them (first person: shadow only).</summary>
        static bool IsHead(Renderer r)
        {
            if (r is SkinnedMeshRenderer smr && smr.sharedMesh != null && smr.sharedMesh.name.StartsWith("G3_Head_", StringComparison.OrdinalIgnoreCase)) return true;
            for (var t = r.transform.parent; t != null; t = t.parent)
                if (t.name.StartsWith("Slot_Hair") || t.name.StartsWith("Slot_Beard") || t.name.StartsWith("Slot_Helmet")) return true;
            return false;
        }

        static float[] Identity() => new float[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
    }
}
