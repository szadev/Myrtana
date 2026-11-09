using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using G3.Formats.Animation;
using G3.Formats.Image;
using G3.Formats.Material;
using G3.Formats.Pak;
using G3.Formats.Remaster;
using G3.Unity.Characters;
using UnityEditor;
using UnityEngine;

namespace G3.Unity.Editor.Characters
{
    /// <summary>
    /// Builds SkinnedMeshRenderer prefabs and legacy AnimationClips from Gothic 3 actors (.xact) and motions (.xmot).
    /// Conventions (Docs/formats-animation.md §4): engine space = Unity space (left-handed, Y up) scaled by 0.01;
    /// EMotionFX quaternions are copied 1:1; triangles keep their stored order (opposite to .xcmsh).
    /// Motions: a track's key is the node's local transform relative to its nearest tracked ancestor, so the
    /// *_ROOT/*_END helper nodes in between are keyed to identity in every clip.
    /// </summary>
    public sealed class G3CharacterImporter : IDisposable
    {
        public const string Root = "Assets/G3Imported/Characters";
        const float S = 0.01f;

        readonly G3FileSystem _fs;
        readonly Dictionary<string, PakEntry> _byStem = new Dictionary<string, PakEntry>(StringComparer.OrdinalIgnoreCase);
        G3AssetCache _cache;
        readonly IMaterialFactory _factory = new UrpMaterialFactory();
        readonly Dictionary<string, Texture2D> _textures = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Log = new List<string>();
        public bool ForceRebuild;
        /// <summary>
        /// Skin part vertices to part nodes the skeleton lacks (face bones of a facial head, added by
        /// <see cref="BuildNpcRig"/>) instead of to their nearest skeleton ancestor. Off for the NPC prefabs (their heads
        /// do not animate faces); on for the talking heads (<see cref="G3FaceKitImporter"/>).
        /// </summary>
        public bool BindAddedNodes;
        /// <summary>
        /// Remaster override (Docs/remaster-pipeline.md): a part whose <c>GameData/Remaster/Characters/&lt;stem&gt;.glb</c>
        /// exists takes its meshes (and embedded textures) from that file; skeleton, slots, clips and face kits stay.
        /// Materials whose texture has a <c>GameData/Remaster/Textures</c> set get the R1 shaders (<see cref="G3RemasterMaterials"/>).
        /// Default from <see cref="G3RemasterSettings.Enabled"/>; off = the original .xact/.xcmsh (A/B comparison).
        /// </summary>
        public bool UseRemaster = G3RemasterSettings.Enabled;
        /// <summary>
        /// Building a talking-head rig (<see cref="G3FaceKitImporter"/>): an actor part takes the override exported from that
        /// rig, <c>&lt;stem&gt;__talk.glb</c> (<see cref="G3.Formats.Remaster.RemasterPaths.TalkSuffix"/>), when it exists,
        /// else <c>&lt;stem&gt;.glb</c>.
        /// </summary>
        public bool TalkRig;

        /// <summary>
        /// With <see cref="UseRemaster"/>: R2 LOD files (<c>Characters/LOD/&lt;stem&gt;_LOD1|2.glb</c>) add LOD renderers and
        /// a LODGroup (<see cref="G3RemasterLods"/>). Off for the player hero (always near the camera; HeroArmorView swaps
        /// its body mesh) and item visuals (body parts are copied by mesh).
        /// </summary>
        public bool RemasterLods = true;

        public G3CharacterImporter(string dataDir = null)
        {
            dataDir ??= G3WorldImporter.DataDir;
            _fs = new G3FileSystem(dataDir, "_compiledAnimation", "_compiledMaterial", "_compiledImage");
            foreach (var e in _fs.Files)
                _byStem[Path.GetFileName(e.Path)] = e;
            foreach (var d in new[] { "Meshes", "Materials", "Textures", "Clips", "Prefabs" })
                Directory.CreateDirectory(Path.Combine(Root, d));
        }

        public void Dispose()
        {
            _fs.Dispose();
            _cache?.Dispose();
        }

        G3AssetCache Cache => _cache ??= new G3AssetCache(G3WorldImporter.DataDir, _factory);

        public ActorResource LoadActor(string name)
        {
            string file = Path.GetFileNameWithoutExtension(name) + ".xact";
            if (!_byStem.TryGetValue(file, out var e)) throw new FileNotFoundException(file);
            return Xact.Read(e.Read(), file);
        }

        public MotionResource LoadMotion(string name)
        {
            string file = name.EndsWith(".xmot", StringComparison.OrdinalIgnoreCase) ? name : name + ".xmot";
            if (!_byStem.TryGetValue(file, out var e)) throw new FileNotFoundException(file);
            return Xmot.Read(e.Read(), file);
        }

        /// <summary>The motion's effect track in clip seconds (frame × key step, clamped to the clip); empty if it has none.</summary>
        public G3MotionLibrary.MotionEvent[] LoadMotionEvents(string motion)
        {
            MotionResource m;
            try { m = LoadMotion(motion); }
            catch (Exception) { return Array.Empty<G3MotionLibrary.MotionEvent>(); }
            if (m == null || m.Effects.Count == 0) return Array.Empty<G3MotionLibrary.MotionEvent>();
            return m.Effects.Select(e => new G3MotionLibrary.MotionEvent { time = Math.Min(e.KeyFrame * m.KeyStep, m.Duration), effect = e.Name })
                            .OrderBy(e => e.time).ToArray();
        }

        /// <summary>
        /// fix-89: the skeleton nodes an overlay motion (variant <c>_O_</c>) drives — its tracked nodes and the helpers folded
        /// into them (<see cref="ActorPose.Drives"/>); the player masks the overlay to these, so everything else keeps the
        /// motion underneath (G3 overlay mixing). Null for other variants (full-body motions drive the whole body).
        /// </summary>
        public string[] OverlayDrives(string motion, EmfxActor skel)
        {
            if (MotionName.Parse(motion)?.Variant != "O" || skel == null) return null;
            MotionResource m;
            try { m = LoadMotion(motion); }
            catch (Exception) { return null; }
            if (m == null) return null;
            var map = ActorPose.MapTracks(skel, m);
            var drives = ActorPose.Drives(map, ActorPose.HeldHelpers(skel, map));
            return Enumerable.Range(0, drives.Length).Where(i => drives[i]).Select(i => skel.Nodes[i].Name).ToArray();
        }

        /// <summary>Motion stems (without .xmot) whose name starts with the prefix (case-insensitive).</summary>
        public List<string> FindMotions(string prefix) =>
            _byStem.Keys.Where(k => k.EndsWith(".xmot", StringComparison.OrdinalIgnoreCase) && k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(k => k.Substring(0, k.Length - 5)).OrderBy(k => k).ToList();

        // ---------------------------------------------------------------------------------------------------------
        // Prefab

        /// <param name="prefabName">output name</param>
        /// <param name="skeleton">actor whose nodes become the bone hierarchy (usually the body actor)</param>
        /// <param name="parts">actors whose meshes are attached (body, head, …); bones are matched by name</param>
        /// <param name="motions">.xmot stems to convert into clips</param>
        /// <param name="clipFolder">own clip folder (under Clips/) for clips bound to this skeleton only: an object's part of a
        /// shared hero motion (the chests' OpenChest <c>_I_</c> parts differ in their top node per chest actor)</param>
        public GameObject Import(string prefabName, string skeleton, IList<string> parts, IList<string> motions, string autoPlay = null,
                                 string clipFolder = null)
        {
            var skel = LoadActor(skeleton).Actor;
            var root = new GameObject(prefabName);
            try
            {
                var xf = BuildSkeleton(skel, root.transform);
                var info = root.AddComponent<G3Character>();
                info.Skeleton = Path.GetFileNameWithoutExtension(skeleton);
                foreach (var part in parts)
                {
                    var res = LoadActor(part);
                    AttachMeshes(res, skel, xf, root.transform, 0);
                    info.Parts.Add(Path.GetFileNameWithoutExtension(part));
                }

                var anim = root.AddComponent<Animation>();
                anim.playAutomatically = false;
                foreach (var mname in motions)
                {
                    var name = MotionName.Parse(mname);
                    var clip = GetClip(mname, skel, xf, root.transform, out bool loop, clipFolder: clipFolder);
                    if (clip == null) continue;
                    anim.AddClip(clip, clip.name);
                    if (info.Rig == null && name != null) info.Rig = name.Rig;
                    info.Clips.Add(new G3Character.ClipInfo
                    {
                        Motion = mname, Clip = clip, Action = name?.Action, Phase = name?.Phase, Direction = name?.Direction,
                        Speed = (name?.Speed ?? 0) * S, Loop = loop,
                    });
                }
                if (autoPlay != null)
                {
                    var c = info.Find(autoPlay);
                    if (c != null) { anim.clip = c.Clip; anim.playAutomatically = true; anim.wrapMode = c.Loop ? WrapMode.Loop : WrapMode.Once; info.AutoPlay = c.Motion; }
                }
                string path = $"{Root}/Prefabs/{prefabName}.prefab";
                var prefab = PrefabUtility.SaveAsPrefabAsset(root, path);
                Log.Add($"prefab {path}: {xf.Length} bones, {info.Parts.Count} parts, {info.Clips.Count} clips");
                return prefab;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        // ---------------------------------------------------------------------------------------------------------
        // NPCs (phase 2): skeleton + body/head actors with MaterialSwitch variants + hair/beard meshes on Slot_* nodes

        /// <summary>
        /// Builds one NPC/monster prefab (Docs/formats-animation.md §8). Humans/orcs: the body actor gives the bone
        /// hierarchy (it has the Slot_* weapon nodes), the head actor adds Slot_Hair/Beard/Helmet, static hair/beard
        /// meshes hang on those slots. Monsters: the single actor. The prefab is baked in the idle clip's first frame.
        /// </summary>
        public GameObject ImportNpc(NpcSpawn s, string prefabPath, string idleMotion)
        {
            var root = BuildNpcRig(s, Path.GetFileNameWithoutExtension(prefabPath), out var skel, out var xf, out var info, out int nodes);
            try
            {
                var anim = root.AddComponent<Animation>();
                anim.playAutomatically = false;
                anim.cullingType = AnimationCullingType.BasedOnRenderers;
                if (idleMotion != null)
                {
                    var clip = GetClip(idleMotion, skel, xf, root.transform, out _);
                    if (clip != null)
                    {
                        var name = MotionName.Parse(idleMotion);
                        anim.AddClip(clip, clip.name);
                        anim.clip = clip;
                        anim.wrapMode = WrapMode.Loop;
                        info.Rig = name?.Rig;
                        info.Clips.Add(new G3Character.ClipInfo
                        {
                            Motion = idleMotion, Clip = clip, Action = name?.Action, Phase = name?.Phase, Direction = name?.Direction,
                            Speed = (name?.Speed ?? 0) * S, Loop = true,
                        });
                        info.AutoPlay = idleMotion;
                        clip.SampleAnimation(root, 0); // the prefab rests in the idle pose instead of the bind pose
                    }
                }
                Directory.CreateDirectory(Path.GetDirectoryName(prefabPath));
                var prefab = PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                Log.Add($"prefab {prefabPath}: {nodes} nodes, {info.Parts.Count} parts, idle {idleMotion}");
                return prefab;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        /// <summary>
        /// Scene object of an NPC without animation components: bone hierarchy (from the body actor), skinned part
        /// meshes, static hair/beard meshes on the head slots and a <see cref="G3Character"/>. The caller adds the
        /// animation system (legacy <see cref="Animation"/> for world NPCs, an Animator for the player hero) and
        /// destroys the object after saving it as a prefab.
        /// </summary>
        public GameObject BuildNpcRig(NpcSpawn s, string name, out EmfxActor skel, out Transform[] xf, out G3Character info, out int nodeCount)
        {
            var actorParts = s.Parts.Where(p => !p.IsMesh).Select(p => (p.Resource, p.Switch)).ToList();
            if (actorParts.Count == 0) actorParts.Add((s.Actor, s.Switch));
            string skelName = s.Parts.FirstOrDefault(p => p.IsBody)?.Resource ?? actorParts[0].Resource;
            skel = LoadActor(skelName).Actor;
            var root = new GameObject(name);
            try
            {
                xf = BuildSkeleton(skel, root.transform);
                var byName = new Dictionary<string, Transform>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < xf.Length; i++) byName[skel.Nodes[i].Name] = xf[i];
                info = root.AddComponent<G3Character>();
                info.Skeleton = Path.GetFileNameWithoutExtension(skelName);
                foreach (var (res, sw) in actorParts)
                {
                    ActorResource a;
                    try { a = LoadActor(res); }
                    catch (Exception ex) { Log.Add($"{s.Name}: part {res}: {ex.Message}"); continue; }
                    AddMissingNodes(a.Actor, byName);
                    AttachMeshes(a, skel, xf, root.transform, sw, BindAddedNodes ? byName : null);
                    info.Parts.Add(Path.GetFileNameWithoutExtension(res) + (sw != 0 ? "_S" + sw : ""));
                }
                foreach (var p in s.Parts.Where(p => p.IsMesh))
                {
                    if (!Cache.TryGetMesh(p.Resource, out var mesh, out var mats)) { Log.Add($"{s.Name}: mesh {p.Resource} missing"); continue; }
                    if (!byName.TryGetValue(p.Slot, out var slot))
                    {
                        Log.Add($"{s.Name}: no {p.Slot} for {p.Resource}");
                        continue;
                    }
                    var go = new GameObject(p.Stem);
                    go.transform.SetParent(slot, false); // modelled in the slot frame (cm -> m, same axes)
                    if (UseRemaster && G3RemasterOverride.TryStatic(this, p.Stem, slot, root.transform, mats, p.Switch, out var rmesh, out var rmats))
                    {
                        mesh = rmesh;
                        mats = rmats;
                    }
                    go.AddComponent<MeshFilter>().sharedMesh = mesh;
                    var mr = go.AddComponent<MeshRenderer>();
                    // generated FACES hair materials are already the switch's own set (fix-52); originals get their variant
                    mr.sharedMaterials = mats.Select(m => IsGeneratedRemaster(m) ? m : Variant(m, p.Switch)).ToArray();
                    info.Parts.Add(p.Stem + (p.Switch != 0 ? "_S" + p.Switch : ""));
                }
                if (UseRemaster) AttachRemasterHair(s, actorParts.Select(x => Path.GetFileNameWithoutExtension(x.Resource)), byName, root.transform);
                if (UseRemaster) G3RemasterMaterials.ApplyTo(this, root); // R1 shaders + upscaled maps where a texture set exists
                if (UseRemaster && RemasterLods) G3RemasterLods.Setup(this, root); // R2 LOD renderers -> LODGroup
                nodeCount = byName.Count;
                return root;
            }
            catch
            {
                UnityEngine.Object.DestroyImmediate(root);
                throw;
            }
        }

        /// <summary>
        /// FACES hair cards (<see cref="RemasterHair"/>): a head part whose remaster override is in use and that has
        /// <c>Characters/Hair/&lt;static head&gt;__hair.glb</c> gets those cards on its Slot_Hair, unless the NPC already
        /// wears a hair mesh there (G3 hair meshes keep their own remaster path).
        /// </summary>
        void AttachRemasterHair(NpcSpawn s, IEnumerable<string> partStems, Dictionary<string, Transform> byName, Transform root)
        {
            if (s.Parts.Any(p => p.IsMesh && string.Equals(p.Slot, RemasterHair.SlotName, StringComparison.OrdinalIgnoreCase))) return;
            if (!byName.TryGetValue(RemasterHair.SlotName, out var slot)) return;
            string projectDir = G3RemasterOverride.ProjectDir;
            string part = RemasterHair.FirstWithHair(
                partStems.Where(p => G3RemasterOverride.Exists(p) || (TalkRig && G3RemasterOverride.Exists(RemasterPaths.TalkStem(p)))),
                head => File.Exists(RemasterHair.File(projectDir, head)));
            if (part == null) return;
            var go = G3RemasterOverride.TryHair(this, part, slot, root);
            if (go != null) Log.Add($"{s.Name}: remaster hair cards {go.name} on {slot.name}");
        }

        /// <summary>Adds part-actor nodes the skeleton lacks (head Slot_Hair/Beard/Helmet) under their same-named parent.</summary>
        static void AddMissingNodes(EmfxActor a, Dictionary<string, Transform> byName)
        {
            for (bool added = true; added;)
            {
                added = false;
                foreach (var n in a.Nodes)
                {
                    if (byName.ContainsKey(n.Name) || n.Parent < 0) continue;
                    if (!byName.TryGetValue(a.Nodes[n.Parent].Name, out var parent)) continue;
                    var t = new GameObject(n.Name).transform;
                    t.SetParent(parent, false);
                    t.localPosition = ToUnity(n.Position);
                    t.localRotation = ToUnity(n.Rotation);
                    byName[n.Name] = t;
                    added = true;
                }
            }
        }

        readonly Dictionary<string, Material> _variants = new Dictionary<string, Material>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// MaterialSwitch n → every texture named <c>…_S&lt;k&gt;</c> (or <c>…_NN</c>) is replaced by <c>…_S&lt;n&gt;</c> when that
        /// image exists (G3_Animation_Head_Main_01_S1 → _S14, G3_Orc_Warrior_Body_Diffuse_S1 → _S5). 0 = as authored.
        /// </summary>
        /// <summary>A FACES generated material (G3RemasterMaterials.Generated: eye, cornea, hair cards/cap).</summary>
        static bool IsGeneratedRemaster(Material m) =>
            m != null && m.shader != null && m.shader.name.StartsWith("G3/Remaster/", StringComparison.Ordinal) && RemasterGenerated.IsGenerated(m.name);

        public Material Variant(Material m, int sw)
        {
            if (m == null || sw == 0) return m;
            string path = $"{Root}/Materials/Variants/{Safe(m.name)}_S{sw}.mat";
            if (_variants.TryGetValue(path, out var v)) return v;
            v = ForceRebuild ? null : AssetDatabase.LoadAssetAtPath<Material>(path);
            if (v == null)
            {
                Material copy = null;
                foreach (var prop in m.GetTexturePropertyNames())
                {
                    var tex = m.GetTexture(prop) as Texture2D;
                    if (tex == null) continue;
                    string stem = Path.GetFileNameWithoutExtension(AssetDatabase.GetAssetPath(tex));
                    if (string.IsNullOrEmpty(stem)) stem = tex.name;
                    bool normal = prop.IndexOf("Bump", StringComparison.OrdinalIgnoreCase) >= 0 || prop.IndexOf("Normal", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (normal && stem.EndsWith("_n", StringComparison.Ordinal)) stem = stem.Substring(0, stem.Length - 2);
                    var target = ResolveImage(stem, sw);
                    if (target == null || string.Equals(target, stem, StringComparison.OrdinalIgnoreCase)) continue;
                    var nt = LoadTexture(new TextureSlot { Image = target }, normal);
                    if (nt == null) continue;
                    copy ??= new Material(m);
                    copy.SetTexture(prop, nt);
                }
                if (copy == null) v = m;
                else
                {
                    copy.name = $"{m.name}_S{sw}";
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    var old = AssetDatabase.LoadAssetAtPath<Material>(path);
                    if (old != null) { old.shader = copy.shader; old.CopyPropertiesFromMaterial(copy); EditorUtility.SetDirty(old); v = old; }
                    else { AssetDatabase.CreateAsset(copy, path); v = copy; }
                }
            }
            _variants[path] = v;
            return v;
        }

        /// <summary>
        /// Re-creates the character materials in place (same assets/GUIDs, prefabs untouched) with the current material
        /// factory: the FXA-built base materials (Materials/*.mat) and every MaterialSwitch copy (Materials/Variants), which
        /// are otherwise only rebuilt when a character is re-imported. Run after <see cref="G3AssetCache.RefreshMaterialAssets"/>
        /// (the variants copy the shared world materials). CLI: <c>G3.Unity.Editor.Characters.G3CharacterImporter.RefreshMaterials()</c>.
        /// </summary>
        public static string RefreshMaterials()
        {
            string folder = Root + "/Materials", varFolder = folder + "/Variants";
            List<string> Mats(string dir) => AssetDatabase.FindAssets("t:Material", new[] { dir }).Select(AssetDatabase.GUIDToAssetPath)
                .Where(p => Path.GetDirectoryName(p)?.Replace('\\', '/') == dir).ToList();
            int bases = 0, variants = 0, missing = 0;
            using (var imp = new G3CharacterImporter { ForceRebuild = true })
            {
                var own = Mats(folder).Select(Path.GetFileNameWithoutExtension).ToList();
                foreach (var file in imp._byStem.Keys.Where(k => k.EndsWith(".xact", StringComparison.OrdinalIgnoreCase)).ToList())
                {
                    string stem = Path.GetFileNameWithoutExtension(file);
                    if (!own.Any(s => s.StartsWith(stem + "_", StringComparison.OrdinalIgnoreCase))) continue;
                    var act = imp.LoadActor(stem).Actor;
                    foreach (var id in act.Meshes.SelectMany(m => m.SubMeshes).Select(sm => sm.MaterialId).Distinct())
                    {
                        imp.GetMaterial(act, id, stem);
                        bases++;
                    }
                }
                foreach (var path in Mats(varFolder))
                {
                    var m = Regex.Match(Path.GetFileNameWithoutExtension(path), @"^(.*)_S(\d+)$");
                    if (!m.Success) continue;
                    string baseName = m.Groups[1].Value;
                    var src = AssetDatabase.LoadAssetAtPath<Material>($"{folder}/{baseName}.mat") ??
                              AssetDatabase.LoadAssetAtPath<Material>($"{G3AssetCache.Root}/Materials/{baseName}.mat");
                    if (src == null) { missing++; continue; }
                    if (imp.Variant(src, int.Parse(m.Groups[2].Value)) == src)
                    {
                        // The switch now resolves to the base textures: keep the (referenced) variant asset as a copy.
                        var old = AssetDatabase.LoadAssetAtPath<Material>(path);
                        old.shader = src.shader;
                        old.CopyPropertiesFromMaterial(src);
                        EditorUtility.SetDirty(old);
                    }
                    variants++;
                }
            }
            AssetDatabase.SaveAssets();
            return $"character materials: {bases} base slots, {variants} variants refreshed, {missing} variants without base";
        }

        static readonly Regex VariantSuffix = new Regex(@"^(.*?)(_S\d+|_\d+)$", RegexOptions.IgnoreCase);

        /// <summary>Existing image stem for texture <paramref name="stem"/> under switch <paramref name="sw"/>, or null.</summary>
        public string ResolveImage(string stem, int sw)
        {
            var m = VariantSuffix.Match(stem);
            string b = m.Success ? m.Groups[1].Value : stem;
            var cands = sw > 0 ? new[] { $"{b}_S{sw}", stem, $"{b}_S1", $"{b}_01" } : new[] { stem, $"{b}_S1", $"{b}_01" };
            foreach (var c in cands)
                if (_byStem.TryGetValue(c + ".ximg", out var e)) return Path.GetFileNameWithoutExtension(e.Path);
            return null;
        }

        static Transform[] BuildSkeleton(EmfxActor a, Transform root)
        {
            var xf = new Transform[a.Nodes.Count];
            for (int i = 0; i < xf.Length; i++) xf[i] = new GameObject(a.Nodes[i].Name).transform;
            for (int i = 0; i < xf.Length; i++)
            {
                var n = a.Nodes[i];
                xf[i].SetParent(n.Parent >= 0 ? xf[n.Parent] : root, false);
                xf[i].localPosition = ToUnity(n.Position);
                xf[i].localRotation = ToUnity(n.Rotation);
            }
            return xf;
        }

        public static Vector3 ToUnity(V3 v) => new Vector3(v.X * S, v.Y * S, v.Z * S);
        public static Quaternion ToUnity(Q4 q) { var n = q.Normalized; return new Quaternion(n.X, n.Y, n.Z, n.W); }

        void AttachMeshes(ActorResource res, EmfxActor skel, Transform[] skelXf, Transform root, int materialSwitch,
                          IReadOnlyDictionary<string, Transform> addedNodes = null)
        {
            var act = res.Actor;
            // Bind poses come from the skeleton the part is worn on: the part's vertices are modelled in the skeleton's
            // bind space, but some part files carry their own, differently rotated Hero_ROOT (G3_Head_Hero_Myrtana_*:
            // 180° about Z instead of Y), whose bind pose would turn the head upside down below the feet.
            var skelBind = ActorPose.Bind(skel);
            string stem = Path.GetFileNameWithoutExtension(res.Name);
            if (UseRemaster && G3RemasterOverride.TryAttach(this, res, skel, skelXf, skelBind, root, materialSwitch, addedNodes)) return;
            int meshNo = 0;
            foreach (var m in act.Meshes)
            {
                if (m.IsCollisionMesh || m.Indices.Length == 0) continue;
                string meshName = meshNo++ == 0 ? stem : $"{stem}_{meshNo}";

                // Bones: every part node referenced by the skin (or the mesh node for rigid meshes).
                var used = new List<int>();
                var slot = new Dictionary<int, int>();
                int Slot(int node) { if (!slot.TryGetValue(node, out int s)) { s = used.Count; slot[node] = s; used.Add(node); } return s; }

                var bones = new List<Transform>();
                var bindposes = new List<Matrix4x4>();
                int nv = m.Positions.Length;
                var weights = new BoneWeight[nv];
                for (int v = 0; v < nv; v++)
                {
                    var inf = m.Skin?[m.OrgVertex[v]];
                    var list = inf == null || inf.Length == 0
                        ? new List<SkinInfluence> { new SkinInfluence((ushort)m.Node, 1) }
                        : inf.Where(x => x.Weight > 0).OrderByDescending(x => x.Weight).Take(4).ToList();
                    float sum = list.Sum(x => x.Weight);
                    var bw = new BoneWeight();
                    for (int k = 0; k < list.Count; k++)
                    {
                        int s = Slot(list[k].Node);
                        float w = list[k].Weight / sum;
                        switch (k)
                        {
                            case 0: bw.boneIndex0 = s; bw.weight0 = w; break;
                            case 1: bw.boneIndex1 = s; bw.weight1 = w; break;
                            case 2: bw.boneIndex2 = s; bw.weight2 = w; break;
                            default: bw.boneIndex3 = s; bw.weight3 = w; break;
                        }
                    }
                    weights[v] = bw;
                }
                foreach (int node in used)
                {
                    BindBone(act, node, skel, skelXf, skelBind, root, addedNodes, stem, out var bone, out var bindpose);
                    bones.Add(bone);
                    bindposes.Add(bindpose);
                }

                var mesh = BuildMesh(m, meshName, weights, bindposes.ToArray());
                var mats = m.SubMeshes.Select(sm => Variant(GetMaterial(act, sm.MaterialId, stem), materialSwitch)).ToArray();
                AddSkinnedRenderer(meshName, mesh, bones.ToArray(), mats, skelXf, root);
            }
        }

        /// <summary>
        /// The bone and bind pose a part node is skinned to: a part node added to the rig (face bone, when
        /// <paramref name="addedNodes"/> is given) in its bind pose under the root; else the same-named skeleton bone, or
        /// the nearest ancestor (in the part actor) the skeleton has; the skeleton root when nothing matches.
        /// </summary>
        internal void BindBone(EmfxActor act, int node, EmfxActor skel, Transform[] skelXf, ActorPose.Xf[] skelBind, Transform root,
                               IReadOnlyDictionary<string, Transform> addedNodes, string stem, out Transform bone, out Matrix4x4 bindpose)
        {
            if (addedNodes != null && skel.FindNode(act.Nodes[node].Name) < 0 &&
                addedNodes.TryGetValue(act.Nodes[node].Name, out var added))
            {
                bone = added;
                bindpose = (root.worldToLocalMatrix * added.localToWorldMatrix).inverse;
                return;
            }
            int target = -1, src = node;
            for (int p = node; p >= 0 && target < 0; p = act.Nodes[p].Parent)
            {
                target = skel.FindNode(act.Nodes[p].Name);
                src = p;
            }
            if (target < 0) { target = 0; Log.Add($"{stem}: bone {act.Nodes[node].Name} not in skeleton, bound to {skel.Nodes[0].Name}"); }
            else if (src != node) Log.Add($"{stem}: bone {act.Nodes[node].Name} not in skeleton, bound to {skel.Nodes[target].Name}");
            bone = skelXf[target];
            bindpose = SkeletonBindpose(skelBind, target);
        }

        internal static Matrix4x4 SkeletonBindpose(ActorPose.Xf[] skelBind, int bone)
        {
            var b = skelBind[bone];
            return Matrix4x4.TRS(ToUnity(b.Pos), ToUnity(b.Rot), Vector3.one).inverse;
        }

        internal static void AddSkinnedRenderer(string name, Mesh mesh, Transform[] bones, Material[] mats, Transform[] skelXf, Transform root)
        {
            var go = new GameObject(name);
            go.transform.SetParent(root, false);
            var smr = go.AddComponent<SkinnedMeshRenderer>();
            smr.sharedMesh = mesh;
            smr.bones = bones;
            smr.rootBone = skelXf[0];
            smr.sharedMaterials = mats;
            smr.updateWhenOffscreen = false;
            // Generous local bounds around the root bone so animated limbs never get culled.
            var bb = mesh.bounds;
            smr.localBounds = new Bounds(skelXf[0].InverseTransformPoint(root.TransformPoint(bb.center)), bb.size * 1.6f + Vector3.one * 0.5f);
        }

        /// <summary>The (pre-switch) material of an actor's material id, as the original mesh gets it.</summary>
        internal Material ActorMaterial(EmfxActor act, int materialId, string actorStem) => GetMaterial(act, materialId, actorStem);

        /// <summary>True if <paramref name="mesh"/> already has exactly these bone weights and bind poses (same bone layout).</summary>
        static bool SameSkin(Mesh mesh, BoneWeight[] weights, Matrix4x4[] bindposes)
        {
            var bp = mesh.bindposes;
            if (bp.Length != bindposes.Length || mesh.vertexCount != weights.Length) return false;
            for (int i = 0; i < bp.Length; i++)
                if (bp[i] != bindposes[i]) return false;
            var bw = mesh.boneWeights;
            for (int i = 0; i < bw.Length; i++)
                if (!bw[i].Equals(weights[i])) return false;
            return true;
        }

        Mesh BuildMesh(ActorMesh m, string name, BoneWeight[] weights, Matrix4x4[] bindposes)
        {
            string path = $"{Root}/Meshes/{name}.asset";
            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            int nv = m.Positions.Length;
            var mesh = existing ?? new Mesh { name = name };
            bool sameSkin = existing != null && SameSkin(existing, weights, bindposes);
            if (sameSkin && !ForceRebuild) return mesh;
            // The asset is shared by every prefab wearing this part, each with its own SkinnedMeshRenderer.bones in the
            // order of these weights: if the bone order changed, the prefabs built before are bound to the wrong bones.
            if (existing != null && !sameSkin)
                Log.Add($"mesh {name}: skin rebuilt; run G3NpcImporter.RebuildStalePrefabs if its bone order changed");
            mesh.Clear();
            mesh.indexFormat = nv > 65535 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
            var pos = new Vector3[nv];
            var nrm = new Vector3[nv];
            var uv = new Vector2[nv];
            for (int i = 0; i < nv; i++)
            {
                pos[i] = ToUnity(m.Positions[i]);
                var n = m.Normals[i];
                nrm[i] = new Vector3(n.X, n.Y, n.Z).normalized;
                if (m.Uv0 != null) uv[i] = new Vector2(m.Uv0[i * 2], m.Uv0[i * 2 + 1]);
            }
            mesh.vertices = pos;
            mesh.normals = nrm;
            mesh.uv = uv;
            if (m.Uv1 != null) mesh.uv2 = Enumerable.Range(0, nv).Select(i => new Vector2(m.Uv1[i * 2], m.Uv1[i * 2 + 1])).ToArray();
            mesh.subMeshCount = m.SubMeshes.Count;
            for (int s = 0; s < m.SubMeshes.Count; s++)
            {
                var sm = m.SubMeshes[s];
                var tris = new int[sm.IndexCount];
                for (int t = 0; t < tris.Length; t += 3)
                {
                    // EMotionFX actors are wound the other way round than .xcmsh (cross(p1-p0,p2-p0) points along the
                    // vertex normal for 99.9% of triangles, see `G3Cli anim winding`), so the order is kept as stored.
                    tris[t] = m.Indices[sm.IndexStart + t];
                    tris[t + 1] = m.Indices[sm.IndexStart + t + 1];
                    tris[t + 2] = m.Indices[sm.IndexStart + t + 2];
                }
                mesh.SetTriangles(tris, s, false);
            }
            mesh.boneWeights = weights;
            mesh.bindposes = bindposes;
            mesh.RecalculateBounds();
            mesh.RecalculateTangents();
            if (existing == null) AssetDatabase.CreateAsset(mesh, path);
            else EditorUtility.SetDirty(mesh);
            return mesh;
        }

        // ---------------------------------------------------------------------------------------------------------
        // Materials

        Material GetMaterial(EmfxActor act, int materialId, string actorStem)
        {
            string xsh = null;
            foreach (var r in act.MaterialRefs)
                if (r.MaterialIndex == materialId && !string.IsNullOrEmpty(r.Name)) { xsh = Path.GetFileNameWithoutExtension(r.Name); break; }
            var mc = materialId >= 0 && materialId < act.Materials.Count ? act.Materials[materialId] : null;
            string layerDiffuse = mc?.Layers.FirstOrDefault(l => l.MapType == 1)?.Texture;
            MaterialResource shared = null;
            if (xsh != null && _byStem.TryGetValue(xsh + ".xshmat", out var xe))
            {
                try { shared = Xshmat.Read(xe.Read(), xsh); } catch (Exception ex) { Log.Add($"material {xsh}: {ex.Message}"); }
                // Body/monster materials name the same texture as the FXA layer: use the shared world material.
                // Heads reference generic FACE/HAIR/Eye materials (grey constant, Diego's hair) — the per-head
                // textures only live in the FXA material layers, so those win (Docs/formats-animation.md §6) — unless the
                // layer names an image the game does not ship (GFG_Hero_Body_King: GFG_Human_King_Body_Diffuse_New_01,
                // G3_Hero_Body_NomadNovice: G3_Human_Nomad_NOVIZE_Diffuse_S1), where only the .xshmat has the real one.
                // fix-237: also when the layer is the numbered source X_01 of the .xshmat's switch image X_S1 (Stalker).
                if (shared != null && ActorMaterialChoice.UseShared(shared.Diffuse?.Image, layerDiffuse, l => ResolveImage(l, 0) != null))
                    return Cache.GetMaterial(xsh);
            }

            // Build from the FXA material chunk and its texture layers (blend/mask taken from the .xshmat if any).
            string matName = (mc?.Name ?? $"Mat{materialId}").Replace("|", "");
            string path = $"{Root}/Materials/{actorStem}_{Safe(matName)}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat != null && !ForceRebuild) return mat;
            var src = new MaterialResource
            {
                Name = matName, DiffuseColor = mc?.Diffuse,
                Blend = shared?.Blend ?? BlendMode.Normal, MaskReference = shared?.MaskReference ?? 0, EnableSpecular = shared?.EnableSpecular ?? false,
            };
            if (src.Blend == BlendMode.AlphaBlend) { src.Blend = BlendMode.Masked; src.MaskReference = 64; } // eyelashes: cutout sorts better on skinned meshes
            if (mc != null)
                foreach (var l in mc.Layers)
                {
                    var ts = new TextureSlot { Image = l.Texture };
                    switch (l.MapType)
                    {
                        case 1: src.Diffuse ??= ts; break;
                        case 3: src.Normal ??= ts; break;
                        case 9: src.Specular ??= ts; src.EnableSpecular = true; break;
                        case 6: src.Opacity ??= ts; if (src.Blend == BlendMode.Normal) { src.Blend = BlendMode.Masked; src.MaskReference = 128; } break;
                    }
                }
            var created = _factory.Create(src, LoadTexture);
            created.name = matName;
            if (mat != null) { mat.CopyPropertiesFromMaterial(created); mat.shader = created.shader; EditorUtility.SetDirty(mat); return mat; }
            AssetDatabase.CreateAsset(created, path);
            return created;
        }

        Texture2D LoadTexture(TextureSlot slot, bool normalMap)
        {
            string key = slot.Image + (normalMap ? "#n" : "");
            if (_textures.TryGetValue(key, out var tex)) return tex;
            string suffix = normalMap ? "_n" : "";
            // Reuse a texture the world importer already converted, else convert into the characters folder.
            tex = AssetDatabase.LoadAssetAtPath<Texture2D>($"{G3AssetCache.Root}/Textures/{Safe(slot.Image)}{suffix}.asset");
            string path = $"{Root}/Textures/{Safe(slot.Image)}{suffix}.asset";
            if (tex == null) tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (tex == null && _byStem.TryGetValue(slot.Image + ".ximg", out var e))
            {
                try
                {
                    tex = BuildTexture(Ximg.Read(e.Read()), normalMap);
                    tex.name = slot.Image;
                    tex.anisoLevel = 4;
                    AssetDatabase.CreateAsset(tex, path);
                }
                catch (Exception ex) { Log.Add($"texture {slot.Image}: {ex.Message}"); tex = null; }
            }
            else if (tex == null)
            {
                // e.g. G3_Orc_Face_Diffuse_01 (named by the FXA layer) only ships as _S1.._S21.
                var alt = ResolveImage(slot.Image, 0);
                if (alt != null && !string.Equals(alt, slot.Image, StringComparison.OrdinalIgnoreCase))
                    tex = LoadTexture(new TextureSlot { Image = alt }, normalMap);
                else Log.Add($"texture not found: {slot.Image}");
            }
            _textures[key] = tex;
            return tex;
        }

        static Texture2D BuildTexture(ImageResource img, bool normalMap)
        {
            int mips = img.Mips.Length;
            switch (img.Format)
            {
                case PixelFormat.DXT1:
                case PixelFormat.DXT5:
                {
                    var raw = img.RawData();
                    if (normalMap && img.Format == PixelFormat.DXT5) Dxt.FixNormalDxt5(raw, flipGreen: true);
                    var tex = new Texture2D(img.Width, img.Height, img.Format == PixelFormat.DXT1 ? TextureFormat.DXT1 : TextureFormat.DXT5, mips, normalMap);
                    tex.LoadRawTextureData(raw);
                    tex.Apply(false, false);
                    return tex;
                }
                case PixelFormat.DXT3:
                {
                    var tex = new Texture2D(img.Width, img.Height, TextureFormat.RGBA32, mips, normalMap);
                    for (int i = 0; i < mips; i++)
                        tex.SetPixelData(Dxt.DecodeDxt3(img.Mips[i], Math.Max(1, img.Width >> i), Math.Max(1, img.Height >> i)), i);
                    tex.Apply(false, false);
                    EditorUtility.CompressTexture(tex, TextureFormat.DXT5, TextureCompressionQuality.Normal);
                    return tex;
                }
                default:
                {
                    var tex = new Texture2D(img.Width, img.Height, TextureFormat.BGRA32, mips, normalMap);
                    tex.LoadRawTextureData(img.RawData());
                    tex.Apply(false, false);
                    return tex;
                }
            }
        }

        // ---------------------------------------------------------------------------------------------------------
        // Clips

        /// <param name="legacy">true: legacy clip for the <see cref="Animation"/> component (Clips/&lt;Rig&gt;);
        /// false: generic Mecanim clip for an Animator (Clips/&lt;Rig&gt;_Generic), same curves.</param>
        /// <param name="forceLoop">overrides the loop flag derived from the motion name (e.g. Slide_* clips loop).</param>
        public AnimationClip GetClip(string motion, EmfxActor skel, Transform[] xf, Transform root, out bool loop, bool legacy = true, bool? forceLoop = null,
                                     string clipFolder = null)
        {
            var name = MotionName.Parse(motion);
            loop = forceLoop ?? (name != null && (name.Phase == "Loop" || name.Action == "Move" || name.Phase == "Ambient"));
            string rig = name?.Rig ?? "Misc";
            string dir = clipFolder != null ? $"{Root}/Clips/{clipFolder}" : $"{Root}/Clips/{rig}{(legacy ? "" : "_Generic")}";
            Directory.CreateDirectory(dir);
            string path = $"{dir}/{Safe(motion)}.anim";
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            if (clip != null && !ForceRebuild) return clip;

            MotionResource m;
            try { m = LoadMotion(motion); }
            catch (Exception ex) { Log.Add($"motion {motion}: {ex.Message}"); return null; }
            if (m == null) { Log.Add($"motion {motion}: deleted (empty)"); return null; }

            var built = BuildClip(m, skel, xf, root, loop, legacy);
            built.name = motion;
            if (clip == null) { AssetDatabase.CreateAsset(built, path); return built; }
            EditorUtility.CopySerialized(built, clip);
            clip.name = motion;
            EditorUtility.SetDirty(clip);
            return clip;
        }

        AnimationClip BuildClip(MotionResource m, EmfxActor skel, Transform[] xf, Transform root, bool loop, bool legacy)
        {
            var clip = new AnimationClip { legacy = legacy, frameRate = Mathf.Round(1f / m.KeyStep), wrapMode = loop ? WrapMode.Loop : WrapMode.Once };
            float dur = Math.Max(m.Duration, 1f / 30);
            var map = ActorPose.MapTracks(skel, m);
            var held = ActorPose.HeldHelpers(skel, map);
            int matched = 0;
            for (int i = 0; i < skel.Nodes.Count; i++)
            {
                string path = AnimationUtility.CalculateTransformPath(xf[i], root);
                var t = map[i];
                if (t != null)
                {
                    matched++;
                    // rotation
                    var rk = Rising(t.Rotation.Select(k => (k.Time, k.Value)).ToList());
                    if (rk.Count == 0) rk = new List<(float, Q4)> { (0, t.PoseRotation), (dur, t.PoseRotation) };
                    var q = new Quaternion[rk.Count];
                    for (int k = 0; k < q.Length; k++)
                    {
                        q[k] = ToUnity(rk[k].Item2);
                        if (k > 0 && Quaternion.Dot(q[k], q[k - 1]) < 0) q[k] = new Quaternion(-q[k].x, -q[k].y, -q[k].z, -q[k].w);
                    }
                    var times = rk.Select(k => k.Item1).ToArray();
                    clip.SetCurve(path, typeof(Transform), "localRotation.x", Linear(times, q.Select(v => v.x).ToArray()));
                    clip.SetCurve(path, typeof(Transform), "localRotation.y", Linear(times, q.Select(v => v.y).ToArray()));
                    clip.SetCurve(path, typeof(Transform), "localRotation.z", Linear(times, q.Select(v => v.z).ToArray()));
                    clip.SetCurve(path, typeof(Transform), "localRotation.w", Linear(times, q.Select(v => v.w).ToArray()));
                    // position
                    var pk = Rising(t.Position.Select(k => (k.Time, k.Value)).ToList());
                    if (pk.Count == 0) pk = new List<(float, V3)> { (0, t.PosePosition), (dur, t.PosePosition) };
                    SetVec(clip, path, "localPosition", pk.Select(k => k.Item1).ToArray(), pk.Select(k => ToUnity(k.Item2)).ToArray());
                    if (t.Scale.Length > 0)
                    {
                        var sk = Rising(t.Scale.Select(k => (k.Time, k.Value)).ToList());
                        SetVec(clip, path, "localScale", sk.Select(k => k.Item1).ToArray(), sk.Select(k => new Vector3(k.Item2.X, k.Item2.Y, k.Item2.Z)).ToArray());
                    }
                }
                else if (held[i])
                {
                    var tt = new[] { 0f, dur };
                    SetVec(clip, path, "localPosition", tt, new[] { Vector3.zero, Vector3.zero });
                    clip.SetCurve(path, typeof(Transform), "localRotation.x", Linear(tt, new[] { 0f, 0f }));
                    clip.SetCurve(path, typeof(Transform), "localRotation.y", Linear(tt, new[] { 0f, 0f }));
                    clip.SetCurve(path, typeof(Transform), "localRotation.z", Linear(tt, new[] { 0f, 0f }));
                    clip.SetCurve(path, typeof(Transform), "localRotation.w", Linear(tt, new[] { 1f, 1f }));
                }
            }
            if (matched == 0) Log.Add($"motion {m.Name}: no track matches the skeleton");
            if (!legacy)
            {
                var settings = AnimationUtility.GetAnimationClipSettings(clip);
                settings.loopTime = loop;
                AnimationUtility.SetAnimationClipSettings(clip, settings);
            }
            return clip;
        }

        /// <summary>Keys with strictly rising times (a repeated time keeps its last value).</summary>
        static List<(float, T)> Rising<T>(List<(float, T)> keys)
        {
            var o = new List<(float, T)>(keys.Count);
            foreach (var k in keys)
            {
                if (o.Count > 0 && k.Item1 <= o[o.Count - 1].Item1)
                {
                    if (k.Item1 == o[o.Count - 1].Item1) o[o.Count - 1] = k;
                    continue;
                }
                o.Add(k);
            }
            if (o.Count == 1) o.Add((o[0].Item1 + 1f / 30, o[0].Item2));
            return o;
        }

        static void SetVec(AnimationClip clip, string path, string prop, float[] t, Vector3[] v)
        {
            clip.SetCurve(path, typeof(Transform), prop + ".x", Linear(t, v.Select(x => x.x).ToArray()));
            clip.SetCurve(path, typeof(Transform), prop + ".y", Linear(t, v.Select(x => x.y).ToArray()));
            clip.SetCurve(path, typeof(Transform), prop + ".z", Linear(t, v.Select(x => x.z).ToArray()));
        }

        static AnimationCurve Linear(float[] t, float[] v)
        {
            var keys = new Keyframe[t.Length];
            for (int i = 0; i < t.Length; i++)
            {
                float inT = i > 0 ? (v[i] - v[i - 1]) / (t[i] - t[i - 1]) : 0;
                float outT = i < t.Length - 1 ? (v[i + 1] - v[i]) / (t[i + 1] - t[i]) : 0;
                if (i == 0) inT = outT;
                if (i == t.Length - 1) outT = inT;
                keys[i] = new Keyframe(t[i], v[i], inT, outT);
            }
            return new AnimationCurve(keys);
        }

        static string Safe(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name.Replace('%', 'P');
        }
    }
}
