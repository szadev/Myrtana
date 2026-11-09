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
    /// Builds the player hero (the nameless hero) like a world NPC (Docs/formats-animation.md §8): the world's
    /// <c>PC_Hero</c> spawn gives the parts (G3_Hero_Skeleton + Body_Player = G3_Hero_Body_Player, Head_Player =
    /// G3_Head_Hero_Hero_01, the clothes he wears at game start). Instead of a legacy Animation the prefab gets an Animator
    /// driven by a PlayableGraph (<see cref="G3MotionPlayer"/>) with a <see cref="G3MotionLibrary"/> of generic clips of
    /// the hero's Stand/None/None/P0 Move, Jump, Fall and Slide motions, plus <see cref="G3HeroAnimator"/> (selector that
    /// composes the motion names) and <see cref="G3HeroView"/>.
    /// Output (gitignored): Assets/G3Imported/Characters/Hero/{G3_Hero.prefab, G3_Hero_Motions.asset}, clips in
    /// Characters/Clips/Hero_Generic. CLI: <c>return G3.Unity.Editor.Characters.G3HeroImporter.Build(true);</c>
    /// </summary>
    public static class G3HeroImporter
    {
        public const string Dir = G3CharacterImporter.Root + "/Hero";
        public const string PrefabPath = Dir + "/G3_Hero.prefab";
        public const string LibraryPath = Dir + "/G3_Hero_Motions.asset";
        public const string HeroSpawnName = "PC_Hero";
        /// <summary>Motion prefix of the unarmed standing hero and the actions the player uses.</summary>
        public const string MotionPrefix = "Hero_Stand_None_None_P0_";
        /// <summary>Unarmed sneaking (stand, walk in 4 directions, turns): library only, chosen by the animator's Sneak stance.</summary>
        public const string SneakPrefix = "Hero_Sneak_None_None_P0_Move_";
        static readonly string[] Actions = { "Move", "Jump", "Fall", "Slide", "Ambient" }; // Ambient: stand idle loops and fidgets
        /// <summary>Sneaking stand idle loop (library only); the armed idle sets come with the combat motions.</summary>
        public const string SneakIdlePrefix = "Hero_Sneak_None_None_P0_Ambient_";
        /// <summary>
        /// Wading and swimming (library only, chosen by the animator's Wade / Swim stance; Docs/hero.md "Water and swimming"):
        /// Wade = full locomotion set + Ambient_Loop; Swim = Stand, Walk/Run Fwd/Back, Ambient_Loop and Fall_Hit (dive-in).
        /// </summary>
        public static readonly string[] WaterPrefixes = { "Hero_Wade_None_None_P0_", "Hero_Swim_None_None_P0_" };

        /// <summary>Hero prefab; built when missing (or always with <paramref name="force"/>).</summary>
        public static GameObject GetOrBuild(bool force = false)
        {
            var p = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            if (p != null && !force && AssetDatabase.LoadAssetAtPath<G3MotionLibrary>(LibraryPath) != null) return p;
            Build(force);
            return AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        }

        /// <summary>Builds the prefab and motion library. <paramref name="force"/> also reconverts clips, meshes and materials.</summary>
        public static string Build(bool force = false)
        {
            Directory.CreateDirectory(Dir);
            var spawn = FindHeroSpawn(out string source);
            using var imp = new G3CharacterImporter { ForceRebuild = force, RemasterLods = false };
            var root = imp.BuildNpcRig(spawn, Path.GetFileNameWithoutExtension(PrefabPath), out var skel, out var xf, out var info, out int nodes);
            try
            {
                info.Rig = "Hero";
                var lib = AssetDatabase.LoadAssetAtPath<G3MotionLibrary>(LibraryPath);
                if (lib == null)
                {
                    lib = ScriptableObject.CreateInstance<G3MotionLibrary>();
                    AssetDatabase.CreateAsset(lib, LibraryPath);
                }
                lib.rig = "Hero";
                lib.entries.Clear();
                var motions = imp.FindMotions(MotionPrefix).Where(m => Actions.Contains(MotionName.Parse(m)?.Action)).ToList();
                motions.AddRange(imp.FindMotions("Hero_").Where(m => G3CombatMotionImporter.IsCombatMotion(MotionName.Parse(m))));
                motions.AddRange(imp.FindMotions("Hero_").Where(m => G3TalkMotionImporter.IsTalkMotion(MotionName.Parse(m)) && !motions.Contains(m)));
                motions.AddRange(imp.FindMotions("Hero_").Where(m => G3TorchMotionImporter.IsTorchMotion(MotionName.Parse(m)) && !motions.Contains(m)));
                motions.AddRange(imp.FindMotions(SneakPrefix));
                motions.AddRange(imp.FindMotions(SneakIdlePrefix));
                motions.AddRange(WaterPrefixes.SelectMany(imp.FindMotions).Where(m => !motions.Contains(m)));
                motions.AddRange(G3HeroUseMotions.Prefixes.SelectMany(imp.FindMotions).Where(m => !motions.Contains(m))); // chests, locks, doors, beds, stations
                foreach (var m in motions)
                {
                    var name = MotionName.Parse(m);
                    bool? loop = name.Action == "Slide" ? true : (bool?)null; // Slide_Begin/_End are closed 1.6 s cycles
                    if (name.Action == "Fall" && name.Phase == "Hit") loop = false; // Swim Fall_Hit: the dive-in one-shot
                    var clip = imp.GetClip(m, skel, xf, root.transform, out bool looped, legacy: false, forceLoop: loop);
                    if (clip == null) continue;
                    lib.entries.Add(new G3MotionLibrary.Entry { motion = m, clip = clip, speed = name.Speed * 0.01f, loop = looped, events = imp.LoadMotionEvents(m), drives = imp.OverlayDrives(m, skel) });
                    if (!m.StartsWith(MotionPrefix, StringComparison.Ordinal)) continue; // combat motions: library only
                    info.Clips.Add(new G3Character.ClipInfo
                    {
                        Motion = m, Clip = clip, Action = name.Action, Phase = name.Phase, Direction = name.Direction,
                        Speed = name.Speed * 0.01f, Loop = looped,
                    });
                }
                EditorUtility.SetDirty(lib);

                var animator = root.AddComponent<Animator>();
                animator.applyRootMotion = false;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                var player = root.AddComponent<G3MotionPlayer>();
                player.library = lib;
                root.AddComponent<G3HeroAnimator>();
                var view = root.AddComponent<G3HeroView>();
                view.headRenderers = root.GetComponentsInChildren<Renderer>(true).Where(IsHeadRenderer).ToArray();

                // The prefab rests in the first frame of the standing motion instead of the bind pose.
                var stand = lib.Find(MotionPrefix + "Move_Stand_N_Fwd_00");
                if (stand != null) stand.clip.SampleAnimation(root, 0);

                var prefab = PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
                AssetDatabase.SaveAssets();
                string warnings = imp.Log.Where(l => !l.StartsWith("prefab ")).Distinct().Take(10).Aggregate("", (a, l) => a + "\n  " + l);
                return $"hero {PrefabPath} from {source}: {nodes} nodes, parts {string.Join(", ", info.Parts)}, " +
                       $"{lib.entries.Count} motions ({string.Join(" ", lib.entries.Select(e => ShortName(e.motion)))}), " +
                       $"{view.headRenderers.Length} head renderers{warnings}";
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        static string ShortName(string motion) => motion.StartsWith(MotionPrefix) ? motion.Substring(MotionPrefix.Length) : motion;

        /// <summary>Head actor meshes and the static meshes hanging on head slots (hair, beard, helmet).</summary>
        static bool IsHeadRenderer(Renderer r)
        {
            if (r is SkinnedMeshRenderer smr && smr.sharedMesh != null &&
                smr.sharedMesh.name.StartsWith("G3_Head_", StringComparison.OrdinalIgnoreCase)) return true;
            for (var t = r.transform.parent; t != null; t = t.parent)
                if (t.name.StartsWith("Slot_Hair") || t.name.StartsWith("Slot_Beard") || t.name.StartsWith("Slot_Helmet")) return true;
            return false;
        }

        /// <summary>The world's PC_Hero spawn (parts as at game start); a fixed fallback with the same parts.</summary>
        public static NpcSpawn FindHeroSpawn(out string source)
        {
            try
            {
                var s = G3NpcImporter.Spawns.FirstOrDefault(x => string.Equals(x.Name, HeroSpawnName, StringComparison.OrdinalIgnoreCase));
                if (s != null && s.Parts.Count > 0) { source = $"{HeroSpawnName} ({s.Sector})"; return s; }
            }
            catch (Exception ex) { Debug.LogWarning("G3HeroImporter: world spawns not readable: " + ex.Message); }
            source = "built-in parts";
            var f = new NpcSpawn { Name = HeroSpawnName, Actor = "G3_Hero_Skeleton.FXA", Enabled = true, World = Identity() };
            f.Parts.Add(new NpcPart { Resource = "G3_Head_Hero_Hero_01.FXA", EntityName = "Head_Player" });
            f.Parts.Add(new NpcPart { Resource = "G3_Hero_Body_Player.FXA", EntityName = "Body_Player" });
            return f;
        }

        static float[] Identity() => new float[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };

        [MenuItem("G3/Player/Rebuild Hero Prefab")]
        static void MenuBuild() => Debug.Log(Build(true));
    }
}
