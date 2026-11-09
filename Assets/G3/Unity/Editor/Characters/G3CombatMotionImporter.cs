using System;
using System.Collections.Generic;
using System.Linq;
using G3.Formats.Animation;
using G3.Unity.Characters;
using UnityEditor;
using UnityEngine;

namespace G3.Unity.Editor.Characters
{
    /// <summary>
    /// Adds the combat motions (<see cref="G3CombatMotions.IsCombatMotion"/>: draw, combat locomotion, attacks, parade,
    /// reactions, down states, death) to existing motion libraries without rebuilding prefabs: the hero's
    /// (<see cref="G3HeroImporter.LibraryPath"/>) and the NPC rig libraries (<see cref="G3NpcMotionImporter.LibraryDir"/>).
    /// Full rebuilds of those libraries include them as well. Clips are shared generic clips (Clips/&lt;Rig&gt;_Generic).
    /// CLI: <c>G3.Unity.Editor.Characters.G3CombatMotionImporter.AddToLibraries("Hero,Orc,Wolf", true)</c>.
    /// </summary>
    public static class G3CombatMotionImporter
    {
        public static bool IsCombatMotion(MotionName n) =>
            n != null && G3CombatMotions.IsCombatMotion(n.Stance, n.LeftItem, n.RightItem, n.Action);

        /// <summary>NPC rig libraries of <paramref name="rigs"/> (comma separated), and the hero's library when <paramref name="hero"/>.</summary>
        public static string AddToLibraries(string rigs, bool hero) => AddToLibraries(rigs, hero, IsCombatMotion, "combat");

        /// <summary>Adds the motions <paramref name="wanted"/> selects (<paramref name="what"/> names them in the report).</summary>
        internal static string AddToLibraries(string rigs, bool hero, Func<MotionName, bool> wanted, string what)
        {
            var lines = new List<string>();
            using var imp = new G3CharacterImporter();
            var motions = imp.FindMotions("");
            if (hero)
            {
                var spawn = G3HeroImporter.FindHeroSpawn(out _);
                lines.Add("player hero (G3_Hero_Motions): " + Add(imp, G3HeroImporter.LibraryPath, "Hero", spawn, motions, wanted, what));
            }
            var rigSet = (rigs ?? "").Split(',').Select(r => r.Trim()).Where(r => r.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (rigSet.Count > 0)
            {
                var samples = new Dictionary<string, NpcSpawn>(StringComparer.OrdinalIgnoreCase);
                foreach (var s in G3NpcImporter.AllSpawns)
                {
                    var idle = s.PickIdle(motions);
                    string rig = idle != null ? MotionName.Parse(idle)?.Rig : null;
                    if (rig != null && rigSet.Contains(rig) && !samples.ContainsKey(rig)) samples[rig] = s;
                }
                foreach (var kv in samples.OrderBy(k => k.Key))
                    lines.Add(kv.Key + ": " + Add(imp, $"{G3NpcMotionImporter.LibraryDir}/{kv.Key}.asset", kv.Key, kv.Value, motions, wanted, what));
                foreach (var missing in rigSet.Where(r => !samples.ContainsKey(r))) lines.Add(missing + ": no spawn of this rig");
            }
            AssetDatabase.SaveAssets();
            return string.Join("\n", lines) + imp.Log.Where(l => !l.StartsWith("prefab ")).Distinct().Take(10).Aggregate("", (a, l) => a + "\n  " + l);
        }

        static string Add(G3CharacterImporter imp, string path, string rig, NpcSpawn sample, List<string> allMotions,
                          Func<MotionName, bool> wanted, string what)
        {
            var lib = AssetDatabase.LoadAssetAtPath<G3MotionLibrary>(path);
            if (lib == null) return "no library at " + path;
            var have = new HashSet<string>(lib.entries.Where(e => e != null).Select(e => e.motion), StringComparer.OrdinalIgnoreCase);
            var stems = allMotions.Where(m => m.StartsWith(rig + "_", StringComparison.OrdinalIgnoreCase) && !have.Contains(m) &&
                                              wanted(MotionName.Parse(m))).ToList();
            // fix-89: overlay entries from before Entry.drives: rebuild their clips (no spine held at identity) and fill the mask
            var stale = lib.entries.Where(e => e != null && (e.drives == null || e.drives.Length == 0) && MotionName.Parse(e.motion)?.Variant == "O").ToList();
            if (stems.Count == 0 && stale.Count == 0) return $"{lib.entries.Count} motions, every {what} motion of rig {rig} already present";
            var root = imp.BuildNpcRig(sample, rig + "_MotionRig", out var skel, out var xf, out _, out _);
            int added = 0;
            try
            {
                bool force = imp.ForceRebuild;
                imp.ForceRebuild = true;
                try
                {
                    foreach (var e in stale)
                    {
                        var clip = imp.GetClip(e.motion, skel, xf, root.transform, out _, legacy: false);
                        if (clip != null) e.clip = clip;
                        e.drives = imp.OverlayDrives(e.motion, skel);
                    }
                }
                finally { imp.ForceRebuild = force; }
                foreach (var m in stems)
                {
                    var clip = imp.GetClip(m, skel, xf, root.transform, out bool loop, legacy: false);
                    if (clip == null) continue;
                    lib.entries.Add(new G3MotionLibrary.Entry
                    {
                        motion = m, clip = clip, speed = (MotionName.Parse(m)?.Speed ?? 0) * 0.01f, loop = loop, events = imp.LoadMotionEvents(m),
                        drives = imp.OverlayDrives(m, skel),
                    });
                    added++;
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
            EditorUtility.SetDirty(lib);
            return $"+{added} {what} motions, {stale.Count} overlay masks refreshed ({lib.entries.Count} total)";
        }
    }
}
