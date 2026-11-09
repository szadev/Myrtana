using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using G3.Formats.Animation;
using G3.Unity.Characters;
using UnityEditor;
using UnityEngine;

namespace G3.Unity.Editor.Characters
{
    /// <summary>
    /// Builds one <see cref="G3MotionLibrary"/> per NPC rig (Hero, Orc, Wolf …) for the runtime NPC views
    /// (Docs/npc-simulation.md): the unarmed locomotion set (<c>&lt;Rig&gt;_Stand_None_None_P0_Move_*</c>, and the rig's
    /// <c>_Wade_</c> set where it has one), the idle loops and
    /// every motion <see cref="G3InteractionMotions"/> may ask for, as generic clips (shared with the hero's clips in
    /// Characters/Clips/&lt;Rig&gt;_Generic). The bone paths come from one representative spawn of the rig; all NPCs of a rig
    /// share the skeleton's node names and hierarchy. Output: <see cref="LibraryDir"/>/&lt;Rig&gt;.asset (a Resources folder,
    /// loaded by rig name at runtime). CLI: <c>G3.Unity.Editor.Characters.G3NpcMotionImporter.Build("Hero,Orc")</c>.
    /// </summary>
    public static class G3NpcMotionImporter
    {
        public const string ResourcesDir = G3CharacterImporter.Root + "/Resources";
        public const string LibraryDir = ResourcesDir + "/" + G3MotionLibraries.ResourceFolder;

        /// <summary>Builds the libraries of the given rigs (comma separated; null = every rig used by a world spawn).</summary>
        public static string Build(string rigs = null, bool force = false)
        {
            Directory.CreateDirectory(LibraryDir);
            var wanted = rigs?.Split(',').Select(r => r.Trim()).Where(r => r.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var byRig = new Dictionary<string, NpcSpawn>(StringComparer.OrdinalIgnoreCase);
            using var imp = new G3CharacterImporter { ForceRebuild = force };
            var motions = imp.FindMotions("");
            foreach (var s in G3NpcImporter.AllSpawns)
            {
                var idle = s.PickIdle(motions);
                string rig = idle != null ? MotionName.Parse(idle)?.Rig : null;
                if (rig == null || byRig.ContainsKey(rig) || (wanted != null && !wanted.Contains(rig))) continue;
                byRig[rig] = s;
            }
            var lines = new List<string>();
            foreach (var kv in byRig.OrderBy(k => k.Key)) lines.Add(BuildRig(imp, kv.Key, kv.Value, motions));
            AssetDatabase.SaveAssets();
            return string.Join("\n", lines) + imp.Log.Where(l => !l.StartsWith("prefab ")).Distinct().Take(10).Aggregate("", (a, l) => a + "\n  " + l);
        }

        static string BuildRig(G3CharacterImporter imp, string rig, NpcSpawn sample, List<string> allMotions)
        {
            var keys = new HashSet<string>(G3InteractionMotions.CandidateKeys(rig), StringComparer.OrdinalIgnoreCase);
            var stems = allMotions.Where(m => m.StartsWith(rig + "_", StringComparison.OrdinalIgnoreCase) && Wanted(rig, m, keys)).ToList();
            var root = imp.BuildNpcRig(sample, rig + "_MotionRig", out var skel, out var xf, out _, out _);
            try
            {
                string path = $"{LibraryDir}/{rig}.asset";
                var lib = AssetDatabase.LoadAssetAtPath<G3MotionLibrary>(path);
                if (lib == null)
                {
                    lib = ScriptableObject.CreateInstance<G3MotionLibrary>();
                    AssetDatabase.CreateAsset(lib, path);
                }
                lib.rig = rig;
                lib.entries.Clear();
                foreach (var m in stems)
                {
                    var clip = imp.GetClip(m, skel, xf, root.transform, out bool loop, legacy: false);
                    if (clip == null) continue;
                    lib.entries.Add(new G3MotionLibrary.Entry { motion = m, clip = clip, speed = (MotionName.Parse(m)?.Speed ?? 0) * 0.01f, loop = loop, events = imp.LoadMotionEvents(m), drives = imp.OverlayDrives(m, skel) });
                }
                EditorUtility.SetDirty(lib);
                return $"{rig}: {lib.entries.Count} motions (rig of {sample.Name})";
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        /// <summary>Unarmed locomotion, idle loops, the interaction, combat and talk motions.</summary>
        static bool Wanted(string rig, string motion, HashSet<string> interactionKeys)
        {
            string key = G3MotionKey.KeyOf(motion);
            if (key == null) return false;
            if (interactionKeys.Contains(key)) return true;
            var n = MotionName.Parse(motion);
            if (G3CombatMotionImporter.IsCombatMotion(n) || G3TalkMotionImporter.IsTalkMotion(n)) return true;
            // Stand: the unarmed set; Wade: the same set in water (NPCs wade from SwimDepth − 50 cm, G3NpcAnimator.Wading).
            if (n == null || (n.Stance != "Stand" && n.Stance != "Wade") || n.LeftItem != "None" || n.RightItem != "None" || n.Pose != "P0") return false;
            return n.Action == "Move" || (n.Action == "Ambient" && n.Phase == "Loop");
        }

        /// <summary>
        /// Share of a library's animated bone paths that exist under <paramref name="prefab"/> (1 = every curve binds).
        /// For checking that one rig's clips fit all NPCs of that rig.
        /// </summary>
        public static string CheckBindings(string rig, int maxPrefabs = 40)
        {
            var lib = AssetDatabase.LoadAssetAtPath<G3MotionLibrary>($"{LibraryDir}/{rig}.asset");
            if (lib == null || lib.entries.Count == 0) return "no library for " + rig;
            var paths = AnimationUtility.GetCurveBindings(lib.entries[0].clip).Select(b => b.path).Distinct().ToList();
            var lines = new List<string>();
            foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[] { G3NpcImporter.PrefabDir }).Take(5000))
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guid));
                var info = prefab != null ? prefab.GetComponent<G3Character>() : null;
                if (info == null || !string.Equals(info.Rig, rig, StringComparison.OrdinalIgnoreCase)) continue;
                int found = paths.Count(p => prefab.transform.Find(p) != null);
                lines.Add($"{prefab.name}: {found}/{paths.Count}");
                if (lines.Count >= maxPrefabs) break;
            }
            return string.Join("\n", lines);
        }
    }
}
