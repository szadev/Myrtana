using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using G3.Formats.Animation;
using G3.Unity.Characters;
using UnityEditor;
using UnityEngine;

namespace G3.Unity.Editor.Characters
{
    /// <summary>
    /// Builds the talking heads (<see cref="G3FaceKit"/>, Docs/dialog-presentation.md §4): for every static head the
    /// world NPCs (and the hero) wear that has a facial actor (the head item's <c>FacialAnimFilePath</c>, else by name), its mesh is
    /// skinned to the skeleton's head bones plus its own face bones, and the face pose of each viseme is read from
    /// <c>&lt;facial actor&gt;_LipSync.xmot</c> (frame i = viseme i, 25 fps). Output: <see cref="KitDir"/>/&lt;static head&gt;.asset.
    /// CLI: <c>G3.Unity.Editor.Characters.G3FaceKitImporter.Build("G3_Orc_Head_Head01,G3_Head_Hero_Hero_01")</c>.
    /// </summary>
    public static class G3FaceKitImporter
    {
        public const string KitDir = G3NpcMotionImporter.ResourcesDir + "/" + G3FaceKit.ResourceFolder;

        /// <summary>Builds the kits of the given static heads (comma separated stems; null = every head with a facial actor).</summary>
        public static string Build(string heads = null)
        {
            var wanted = heads?.Split(',').Select(h => h.Trim()).Where(h => h.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Directory.CreateDirectory(KitDir);
            var lines = new List<string>();
            using var imp = new G3CharacterImporter { BindAddedNodes = true };
            var lipSync = new HashSet<string>(imp.FindMotions("").Where(m => m.EndsWith(LipSyncSuffix, StringComparison.OrdinalIgnoreCase)),
                                              StringComparer.OrdinalIgnoreCase);
            var jobs = FindHeads(facial => lipSync.Contains(facial + LipSyncSuffix));
            foreach (var job in jobs.Values.OrderBy(j => j.Head))
            {
                if (wanted != null && !wanted.Contains(job.Head)) continue;
                try { lines.Add(BuildKit(imp, job)); }
                catch (Exception ex) { lines.Add($"{job.Head}: {ex.Message}"); }
            }
            AssetDatabase.SaveAssets();
            if (wanted != null)
                foreach (var h in wanted.Where(h => !jobs.ContainsKey(h))) lines.Add($"{h}: no facial actor known");
            return string.Join("\n", lines) + imp.Log.Where(l => !l.StartsWith("prefab ")).Distinct().Take(10).Aggregate("", (a, l) => a + "\n  " + l);
        }

        sealed class HeadJob
        {
            public string Head, Facial;
            public NpcSpawn Sample;
        }

        const string LipSyncSuffix = "_LipSync";

        /// <summary>Static head stem → facial actor and one spawn wearing it (its body gives the skeleton).</summary>
        static Dictionary<string, HeadJob> FindHeads(Func<string, bool> hasLipSync)
        {
            var jobs = new Dictionary<string, HeadJob>(StringComparer.OrdinalIgnoreCase);
            var spawns = G3NpcImporter.AllSpawns.ToList();
            spawns.Add(G3HeroImporter.FindHeroSpawn(out _));
            foreach (var s in spawns)
                foreach (var p in s.Parts.Where(p => p.IsHead))
                {
                    if (jobs.ContainsKey(p.Stem)) continue;
                    string facial = p.FacialResource != null ? Path.GetFileNameWithoutExtension(p.FacialResource) : NamedFacial(p.Stem, hasLipSync);
                    if (facial == null || !hasLipSync(facial)) continue;
                    jobs[p.Stem] = new HeadJob { Head = p.Stem, Facial = facial, Sample = s };
                }
            return jobs;
        }

        /// <summary>
        /// Heads without a FacialAnimFilePath (the player's head, the orc heads): the facial actor named after the head,
        /// <c>G3_Head_Hero_Hero_01</c> → <c>G3_Head_Hero_Hero_Animated_01</c>, <c>G3_Orc_Head_Head01</c> → <c>…_Head01_Animated</c>.
        /// </summary>
        static string NamedFacial(string head, Func<string, bool> hasLipSync)
        {
            var numbered = Regex.Match(head, @"^(.*)_(\d+)$");
            if (numbered.Success && hasLipSync($"{numbered.Groups[1].Value}_Animated_{numbered.Groups[2].Value}"))
                return $"{numbered.Groups[1].Value}_Animated_{numbered.Groups[2].Value}";
            return hasLipSync(head + "_Animated") ? head + "_Animated" : null;
        }

        /// <summary>
        /// The talking head of one static head built from the remaster overrides (<c>&lt;facial&gt;__talk.glb</c> /
        /// <c>&lt;facial&gt;.glb</c>), saved at <paramref name="assetPath"/> (outside Resources/G3Faces, so
        /// <see cref="G3FaceKit.Load"/> keeps finding the original): the runtime character remaster's kit
        /// (G3CharacterRemasterSet.faceKits). Null with the reason in <paramref name="line"/> when the head has no facial actor.
        /// </summary>
        public static G3FaceKit BuildRemasterKit(string head, NpcSpawn sample, string assetPath, out string line)
        {
            // a remaster head with blend shapes (MetaHuman hero, Docs/remaster-metahuman.md §5.2) talks by its own shapes:
            // <head>.visemes.txt beside the .glb holds the viseme -> ARKit weights table
            string table = Path.ChangeExtension(G3RemasterOverride.FileFor(head), ".visemes.txt");
            if (File.Exists(table)) return BuildMorphKit(head, table, assetPath, out line);
            using var imp = new G3CharacterImporter { BindAddedNodes = true, UseRemaster = true };
            var lipSync = new HashSet<string>(imp.FindMotions("").Where(m => m.EndsWith(LipSyncSuffix, StringComparison.OrdinalIgnoreCase)),
                                              StringComparer.OrdinalIgnoreCase);
            var part = sample.Parts.FirstOrDefault(p => p.IsHead && string.Equals(p.Stem, head, StringComparison.OrdinalIgnoreCase));
            string facial = part?.FacialResource != null ? Path.GetFileNameWithoutExtension(part.FacialResource) : NamedFacial(head, f => lipSync.Contains(f + LipSyncSuffix));
            if (facial == null || !lipSync.Contains(facial + LipSyncSuffix)) { line = $"{head}: no facial actor with lip-sync"; return null; }
            Directory.CreateDirectory(Path.GetDirectoryName(assetPath));
            line = BuildKit(imp, new HeadJob { Head = head, Facial = facial, Sample = sample }, assetPath);
            AssetDatabase.SaveAssets();
            return AssetDatabase.LoadAssetAtPath<G3FaceKit>(assetPath);
        }

        /// <summary>A blend shape kit (<see cref="G3FaceKit.IsMorph"/>): no facial mesh, the viseme table from the text file.</summary>
        static G3FaceKit BuildMorphKit(string head, string tableFile, string assetPath, out string line)
        {
            VisemeMorphTable table;
            try { table = VisemeMorphTable.Parse(File.ReadAllText(tableFile)); }
            catch (Exception ex) { line = $"{head}: {Path.GetFileName(tableFile)} unreadable: {ex.Message}"; return null; }
            Directory.CreateDirectory(Path.GetDirectoryName(assetPath));
            var kit = AssetDatabase.LoadAssetAtPath<G3FaceKit>(assetPath);
            if (kit == null)
            {
                kit = ScriptableObject.CreateInstance<G3FaceKit>();
                AssetDatabase.CreateAsset(kit, assetPath);
            }
            kit.head = head;
            kit.facialActor = Path.GetFileName(tableFile);
            kit.mesh = null;
            kit.materials = Array.Empty<Material>();
            kit.bones = Array.Empty<string>();
            kit.rootBone = null;
            kit.nodes = Array.Empty<G3FaceKit.Node>();
            kit.tracks = Array.Empty<G3FaceKit.Track>();
            kit.morphs = table;
            kit.visemeCount = table.VisemeCount;
            int jaw = Array.FindIndex(table.Shapes, s => string.Equals(s, "jawOpen", StringComparison.OrdinalIgnoreCase));
            kit.openViseme = 1;
            if (jaw >= 0)
                for (int v = 0; v < table.VisemeCount; v++)
                    if (table.Get(v, jaw) > table.Get(kit.openViseme, jaw)) kit.openViseme = v;
            EditorUtility.SetDirty(kit);
            AssetDatabase.SaveAssets();
            line = $"{head}: blend shape kit, {table.Shapes.Length} shapes, {table.VisemeCount} visemes, open {kit.openViseme}";
            return kit;
        }

        static string BuildKit(G3CharacterImporter imp, HeadJob job, string assetPath = null)
        {
            var motion = imp.LoadMotion(job.Facial + "_LipSync");
            var facialActor = imp.LoadActor(job.Facial).Actor;

            // The sample NPC with the facial actor as its only head part (body for the skeleton, no hair).
            var spawn = new NpcSpawn { Name = job.Sample.Name, Actor = job.Sample.Actor, Switch = job.Sample.Switch, World = job.Sample.World };
            spawn.Parts.AddRange(job.Sample.Parts.Where(p => p.IsBody));
            spawn.Parts.Add(new NpcPart { Resource = job.Facial + ".FXA", EntityName = "Head" });
            imp.TalkRig = true;
            GameObject root;
            EmfxActor skel;
            try { root = imp.BuildNpcRig(spawn, job.Head + "_FaceRig", out skel, out _, out _, out _); }
            finally { imp.TalkRig = false; }
            try
            {
                var smr = root.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                              .FirstOrDefault(r => r.sharedMesh != null && r.sharedMesh.name.Equals(job.Facial, StringComparison.OrdinalIgnoreCase));
                if (smr == null) return $"{job.Head}: {job.Facial} has no mesh";

                var faceNodes = facialActor.Nodes.Where(n => n.Parent >= 0 && skel.FindNode(n.Name) < 0)
                                               .Select(n => FindDeep(root.transform, n.Name)).Where(t => t != null).ToList();
                var tracks = BuildTracks(motion, faceNodes, out int visemes);

                string path = assetPath ?? $"{KitDir}/{job.Head}.asset";
                var kit = AssetDatabase.LoadAssetAtPath<G3FaceKit>(path);
                if (kit == null)
                {
                    kit = ScriptableObject.CreateInstance<G3FaceKit>();
                    AssetDatabase.CreateAsset(kit, path);
                }
                kit.head = job.Head;
                kit.facialActor = job.Facial;
                kit.mesh = smr.sharedMesh;
                kit.materials = smr.sharedMaterials;
                kit.bones = smr.bones.Select(b => b.name).ToArray();
                kit.rootBone = smr.rootBone != null ? smr.rootBone.name : null;
                kit.nodes = faceNodes.Select(t => new G3FaceKit.Node
                {
                    name = t.name, parent = t.parent.name, position = t.localPosition, rotation = t.localRotation,
                }).ToArray();
                kit.tracks = tracks;
                kit.visemeCount = visemes;
                kit.openViseme = WidestViseme(tracks, visemes);
                EditorUtility.SetDirty(kit);
                return $"{job.Head}: {job.Facial}, {faceNodes.Count} face bones, {tracks.Length} tracks, {visemes} visemes, open {kit.openViseme} " +
                       $"(jaw° per viseme: {string.Join(" ", JawAngles(tracks, visemes).Select(a => a.ToString("0")))})";
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        /// <summary>The local pose of every keyed face bone at frame v (= viseme v).</summary>
        static G3FaceKit.Track[] BuildTracks(MotionResource motion, List<Transform> faceNodes, out int visemes)
        {
            float step = motion.KeyStep > 0 ? motion.KeyStep : 0.04f;
            visemes = Mathf.RoundToInt(motion.Duration / step) + 1;
            var result = new List<G3FaceKit.Track>();
            foreach (var node in faceNodes)
            {
                var t = motion.Find(node.name);
                if (t == null || (t.Rotation.Length == 0 && t.Position.Length == 0)) continue;
                var track = new G3FaceKit.Track { bone = node.name, positions = new Vector3[visemes], rotations = new Quaternion[visemes] };
                for (int v = 0; v < visemes; v++)
                {
                    track.positions[v] = G3CharacterImporter.ToUnity(t.SamplePosition(v * step));
                    track.rotations[v] = G3CharacterImporter.ToUnity(t.SampleRotation(v * step));
                }
                result.Add(track);
            }
            return result.ToArray();
        }

        static IEnumerable<float> JawAngles(G3FaceKit.Track[] tracks, int visemes)
        {
            var jaw = tracks.FirstOrDefault(t => t.bone.EndsWith("_Jaw_B", StringComparison.OrdinalIgnoreCase)) ??
                      tracks.FirstOrDefault(t => t.bone.IndexOf("Jaw", StringComparison.OrdinalIgnoreCase) >= 0);
            if (jaw == null) return Enumerable.Repeat(0f, visemes);
            var rest = jaw.rotations[Mathf.Min(1, visemes - 1)];
            return jaw.rotations.Select(r => Quaternion.Angle(rest, r));
        }

        /// <summary>The viseme that opens the jaw most (the loudness-driven fallback shows it).</summary>
        static int WidestViseme(G3FaceKit.Track[] tracks, int visemes)
        {
            var angles = JawAngles(tracks, visemes).ToArray();
            int best = Mathf.Min(1, visemes - 1);
            for (int v = 0; v < angles.Length; v++) if (angles[v] > angles[best]) best = v;
            return best;
        }

        static Transform FindDeep(Transform t, string name)
        {
            if (t.name.Equals(name, StringComparison.OrdinalIgnoreCase)) return t;
            foreach (Transform c in t)
            {
                var f = FindDeep(c, name);
                if (f != null) return f;
            }
            return null;
        }
    }
}
