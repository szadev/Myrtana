using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using G3.Formats.Animation;
using G3.Unity.Characters;
using G3.Unity.Editor.Characters;
using Myrtana.Rules;
using Myrtana.Session;
using UnityEditor;
using UnityEngine;

namespace Myrtana.Unity.Editor
{
    /// <summary>
    /// Builds the prefabs of the animated interactive objects of the content pack (every <c>.FXA</c> visual of an
    /// <see cref="InteractiveObjectDef"/>: chests, baskets, doors with their open/close motions, grindstones …) and the
    /// <see cref="InteractiveVisualCatalog"/> that finds them, and rebuilds the hero prefab so his motion library holds
    /// the use motions (<c>G3HeroUseMotions</c>).
    /// Output (git-ignored): Assets/G3Imported/Characters/Prefabs/Interactive_*.prefab,
    /// Assets/G3Imported/Myrtana/Resources/Myrtana/InteractiveVisuals.asset.
    /// CLI: <c>return Myrtana.Unity.Editor.InteractiveVisualBuilder.Build(true);</c>
    /// </summary>
    public static class InteractiveVisualBuilder
    {
        const string CatalogPath = "Assets/G3Imported/Myrtana/Resources/" + InteractiveVisualCatalog.ResourcePath + ".asset";

        [MenuItem("Myrtana/Content/Build interactive visuals")]
        static void Menu() => Debug.Log("[Myrtana] " + Build(true));

        /// <param name="heroMotions">Also rebuild the hero prefab and motion library (adds the use motions).</param>
        public static string Build(bool heroMotions)
        {
            var content = GameContent.Load(ContentLocations.Packs());
            var objects = content.Database.All<InteractiveObjectDef>().ToList();
            var actors = objects.Select(o => o.Visual).Where(v => v != null && v.EndsWith(".FXA", StringComparison.OrdinalIgnoreCase))
                                .Select(InteractiveVisualCatalog.KeyOf).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(a => a).ToList();
            var doorMotions = objects.Where(o => o.Door != null).SelectMany(o => new[] { o.Door.OpenMotion, o.Door.CloseMotion })
                                     .Where(m => m != null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            // fix-61: every actor whose use type has the object's own parts of the hero's use motions (ObjectUseParts: the
            // grindstone's wheel, the chests' lids, the barbecue spit, the water pipe, the orc boulder) gets them
            var partUseTypes = objects.Where(o => o.Visual != null && ObjectUseParts.Animated(o.UseType))
                                      .GroupBy(o => InteractiveVisualCatalog.KeyOf(o.Visual), StringComparer.OrdinalIgnoreCase)
                                      .Where(g => g.Key != null)
                                      .ToDictionary(g => g.Key, g => g.Select(o => o.UseType).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                                                    StringComparer.OrdinalIgnoreCase);
            int partClips = 0;

            Directory.CreateDirectory(Path.GetDirectoryName(CatalogPath));
            var catalog = AssetDatabase.LoadAssetAtPath<InteractiveVisualCatalog>(CatalogPath);
            if (catalog == null)
            {
                catalog = ScriptableObject.CreateInstance<InteractiveVisualCatalog>();
                AssetDatabase.CreateAsset(catalog, CatalogPath);
            }
            catalog.entries.Clear();
            var problems = new List<string>();
            using (var imp = new G3CharacterImporter())
            {
                foreach (var actor in actors)
                {
                    try
                    {
                        var motions = doorMotions.Where(m => m.StartsWith(actor + "_", StringComparison.OrdinalIgnoreCase)).ToList();
                        var parts = partUseTypes.TryGetValue(actor, out var useTypes) ? ObjectPartMotions(imp, useTypes) : new List<string>();
                        motions.AddRange(parts);
                        // the parts are bound to this actor's nodes: their own clip folder (the five chest actors share the
                        // OpenChest parts but not their top node)
                        var prefab = imp.Import("Interactive_" + actor, actor, new[] { actor }, motions, clipFolder: parts.Count > 0 ? "Objects/" + actor : null);
                        if (parts.Count > 0) KeepObjectInPlace(prefab);
                        partClips += parts.Count;
                        catalog.entries.Add(new InteractiveVisualCatalog.Entry { key = actor, prefab = prefab });
                    }
                    catch (Exception ex) when (ex is FileNotFoundException || ex is InvalidDataException)
                    {
                        problems.Add($"{actor}: {ex.Message}");
                    }
                }
            }
            EditorUtility.SetDirty(catalog);
            AssetDatabase.SaveAssets();
            string hero = heroMotions ? "\n" + G3HeroImporter.Build(false) : string.Empty;
            return $"interactive visuals: {catalog.entries.Count} of {actors.Count} actors ({objects.Count} objects), {partClips} object-part clips " +
                   $"({string.Join(", ", partUseTypes.Keys)}); {problems.Count} problems" +
                   string.Concat(problems.Take(8).Select(p => "\n  " + p)) + hero;
        }

        /// <summary>
        /// The object's own parts of the hero's use motions of these use types (the <c>_I_</c> variants,
        /// <see cref="ObjectUseParts.HeroMotionPrefixes"/>: they move the wheel, lid, spit, hose or stone bones).
        /// </summary>
        static List<string> ObjectPartMotions(G3CharacterImporter imp, IEnumerable<string> useTypes) =>
            useTypes.SelectMany(ObjectUseParts.HeroMotionPrefixes).SelectMany(imp.FindMotions)
                    .Where(m => MotionName.Parse(m)?.Variant == ObjectUseParts.ObjectVariant).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        /// <summary>
        /// The object parts also key the actor's placement dummy (<c>G3_Dummy_Offset_Exact_01</c>, the user's exact stand
        /// point, the parent of the actor's ROOT; the boulder's 35 cm off its bind pose, Chest_02/03's 1.4 cm) and the
        /// actor's top node; the engine aligns the user to that dummy rather than moving the object, so these curves are
        /// dropped: the object stays where it stands, only its moving bones (wheel, lid, spit, hose, stone) animate.
        /// </summary>
        static void KeepObjectInPlace(GameObject prefab)
        {
            var info = prefab != null ? prefab.GetComponent<G3.Unity.Characters.G3Character>() : null;
            if (info == null) return;
            foreach (var c in info.Clips)
            {
                if (c?.Clip == null) continue;
                bool changed = false;
                foreach (var b in AnimationUtility.GetCurveBindings(c.Clip))
                {
                    string leaf = b.path.Substring(b.path.LastIndexOf('/') + 1);
                    if (!leaf.StartsWith("G3_Dummy_Offset", StringComparison.OrdinalIgnoreCase) &&
                        !leaf.StartsWith("G3_Object_", StringComparison.OrdinalIgnoreCase)) continue;
                    AnimationUtility.SetEditorCurve(c.Clip, b, null);
                    changed = true;
                }
                if (changed) EditorUtility.SetDirty(c.Clip);
            }
        }
    }
}
