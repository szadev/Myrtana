using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using G3.Formats.Animation;
using G3.Formats.Remaster;
using G3.Unity.Characters;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace G3.Unity.Editor.Characters
{
    /// <summary>
    /// The remaster test scene (Docs/remaster-pipeline.md §4): one character built twice on a turntable — original (A,
    /// override off) and remastered (B, override on) — plus talk variants wearing the facial head; legacy clips walk,
    /// run, 1H and 2H attack, sit, talk; fixed lighting (key + fill directional, trilight ambient, grey ground, no post);
    /// cameras at 1 m (face), 5 m (full body) and 30 m (in-game distance). <see cref="Capture"/> renders every shot for
    /// A and B into GameData/Remaster/Reports/Screens/&lt;label&gt;/ (A, B and a side-by-side A|B image).
    /// The scene is generated (Assets/G3Imported, git-ignored).
    /// CLI: <c>return G3.Unity.Editor.Characters.G3RemasterTestScene.Build();</c> then <c>…Capture("pilot1");</c>
    /// </summary>
    public static class G3RemasterTestScene
    {
        public const string ScenePath = "Assets/G3Imported/Remaster/RemasterTest.unity";

        /// <summary>Test clips: label, hero motion, still time (normalized), talk variant.</summary>
        public static readonly (string label, string motion, float still, bool talk)[] Clips =
        {
            ("idle", "Hero_Stand_None_None_P0_Move_Stand_N_Fwd_00_%_00_P0_0", 0f, false),
            ("walk", "Hero_Stand_None_None_P0_Move_Walk_N_Fwd_00_%_00_P0_160", 0.25f, false),
            ("run", "Hero_Stand_None_None_P0_Move_Run_N_Fwd_00_%_00_P0_400", 0.3f, false),
            ("attack1h", "Hero_Stand_None_1H_P0_Attack_Hit_N_Fwd_00_%_00_P1_118_R", 0.45f, false),
            ("attack2h", "Hero_Stand_None_2H_P0_Attack_Hit_N_Fwd_00_%_00_P1_90_R", 0.45f, false),
            ("sit", "Hero_SitGround_None_None_P0_Ambient_Loop_N_Fwd_00_%_00_P0_0", 0f, false),
            ("talk", "Hero_Stand_None_None_P0_Say_Ambient_N_Fwd_00_%_00_P0_0", 0.5f, true),
        };

        /// <summary>
        /// Builds and saves the scene for a spawn (default: the hero). Replaces the open scene: refuses when it has
        /// unsaved changes unless <paramref name="discardOpenScene"/>.
        /// </summary>
        public static string Build(string spawnName = "PC_Hero", bool discardOpenScene = false)
        {
            if (!discardOpenScene && Enumerable.Range(0, EditorSceneManager.sceneCount).Any(i => EditorSceneManager.GetSceneAt(i).isDirty))
                return "open scene has unsaved changes; save it or call Build(spawn, discardOpenScene: true)";
            var spawn = string.Equals(spawnName, G3HeroImporter.HeroSpawnName, StringComparison.OrdinalIgnoreCase)
                ? G3HeroImporter.FindHeroSpawn(out _)
                : G3NpcImporter.AllSpawns.FirstOrDefault(s => string.Equals(s.Name, spawnName, StringComparison.OrdinalIgnoreCase));
            if (spawn == null) return $"no spawn {spawnName}";

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var log = new StringBuilder();
            var pivot = new GameObject("Turntable");
            var preview = pivot.AddComponent<G3RemasterPreview>();
            preview.spin = true;

            var talkSpawn = TalkSpawn(spawn, out string facial);
            preview.original = BuildCharacter(spawn, "A_Original", false, false, pivot.transform, log);
            preview.remaster = BuildCharacter(spawn, "B_Remaster", true, false, pivot.transform, log);
            if (talkSpawn != null)
            {
                preview.originalTalk = BuildCharacter(talkSpawn, "A_Original_Talk", false, true, pivot.transform, log);
                preview.remasterTalk = BuildCharacter(talkSpawn, "B_Remaster_Talk", true, true, pivot.transform, log);
                preview.visemes = VisemeTracks(facial, preview.originalTalk, preview.original, out preview.visemeCount);
            }
            preview.clips = Clips.Select(c => new G3RemasterPreview.TestClip { label = c.label, clip = c.motion, stillTime = c.still, talk = c.talk }).ToArray();

            // Face the cameras (+Z): the jaw sits in front of the head bone.
            var front = FrontOf(preview.originalTalk != null ? preview.originalTalk : preview.original);
            pivot.transform.rotation = Quaternion.FromToRotation(front, Vector3.forward);
            pivot.transform.rotation = Quaternion.Euler(0, pivot.transform.eulerAngles.y, 0);

            float headY = HeadHeight(preview.original);
            preview.cameras = new[]
            {
                MakeCamera("Cam_1m", new Vector3(0, headY, 1f), new Vector3(0, headY - 0.03f, 0), 35),
                MakeCamera("Cam_5m", new Vector3(0, 1.0f, 5f), new Vector3(0, 0.92f, 0), 28),
                MakeCamera("Cam_30m", new Vector3(0, 1.7f, 30f), new Vector3(0, 0.9f, 0), 30),
            };
            preview.cameraIndex = 1;
            SetupLighting();

            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
            EditorSceneManager.SaveScene(scene, ScenePath);
            return $"{ScenePath}: {spawn.Name} ({string.Join(", ", spawn.Parts.Select(p => p.Stem))}), talk head {facial ?? "-"}, " +
                   $"{preview.visemeCount} visemes, overrides present: {string.Join(", ", G3RemasterOverride.Overrides().DefaultIfEmpty("none"))}{log}";
        }

        /// <summary>
        /// Name of the first enabled spawn wearing the given parts (stems, switch suffix allowed: <c>G3_Hero_Body_Mercenary</c>,
        /// <c>G3_Head_Hero_Myrtana_05_S29</c>), for <see cref="Build"/> on an NPC look (R1 pilot: Mercenary, Paladin).
        /// </summary>
        public static string SpawnForLook(params string[] parts)
        {
            static string Key(NpcPart p) => p.Stem + (p.Switch != 0 ? "_S" + p.Switch : "");
            var s = G3NpcImporter.AllSpawns.FirstOrDefault(sp => sp.Enabled && parts.All(want =>
                sp.Parts.Any(p => string.Equals(Key(p), want, StringComparison.OrdinalIgnoreCase) ||
                                  string.Equals(p.Stem, want, StringComparison.OrdinalIgnoreCase))));
            return s?.Name;
        }

        /// <summary>The spawn with its static head swapped for the facial actor (the head that animates visemes).</summary>
        static NpcSpawn TalkSpawn(NpcSpawn s, out string facial)
        {
            facial = null;
            var head = s.Parts.FirstOrDefault(p => p.IsHead);
            if (head == null) return null;
            facial = head.FacialResource != null ? Path.GetFileNameWithoutExtension(head.FacialResource) : NamedFacial(head.Stem);
            if (facial == null) return null;
            var t = new NpcSpawn { Name = s.Name + "_Talk", Actor = s.Actor, Switch = s.Switch, World = s.World, Enabled = true };
            foreach (var p in s.Parts)
                t.Parts.Add(p == head ? new NpcPart { Resource = facial + ".xact", Switch = p.Switch, EntityName = "Head" } : p);
            return t;
        }

        static string NamedFacial(string head)
        {
            var m = System.Text.RegularExpressions.Regex.Match(head, @"^(.*)_(\d+)$");
            return m.Success ? $"{m.Groups[1].Value}_Animated_{m.Groups[2].Value}" : head + "_Animated";
        }

        static GameObject BuildCharacter(NpcSpawn s, string name, bool remastered, bool faceBones, Transform parent, StringBuilder log)
        {
            using var imp = new G3CharacterImporter { UseRemaster = remastered, BindAddedNodes = faceBones };
            GameObject root;
            try { root = imp.BuildNpcRig(s, name, out var skel, out var xf, out _, out _); AddClips(imp, root, skel, xf, log); }
            catch (Exception ex) { log.Append($"\n  {name}: {ex.Message}"); return null; }
            root.transform.SetParent(parent, false);
            foreach (var l in imp.Log.Where(l => !l.StartsWith("prefab ")).Distinct().Take(6)) log.Append($"\n  {name}: {l}");
            return root;
        }

        static void AddClips(G3CharacterImporter imp, GameObject root, EmfxActor skel, Transform[] xf, StringBuilder log)
        {
            var anim = root.AddComponent<Animation>();
            anim.playAutomatically = false;
            anim.cullingType = AnimationCullingType.AlwaysAnimate;
            foreach (var c in Clips)
            {
                var clip = imp.GetClip(c.motion, skel, xf, root.transform, out _);
                if (clip == null) { log.Append($"\n  clip {c.motion} missing"); continue; }
                anim.AddClip(clip, c.motion);
            }
            var idle = anim.GetClip(Clips[0].motion);
            if (idle != null) idle.SampleAnimation(root, 0);
        }

        /// <summary>Face-bone poses per viseme from <c>&lt;facial&gt;_LipSync.xmot</c> (as <see cref="G3FaceKitImporter"/> reads them).</summary>
        static G3FaceKit.Track[] VisemeTracks(string facial, GameObject talkRig, GameObject staticRig, out int count)
        {
            count = 0;
            if (talkRig == null) return Array.Empty<G3FaceKit.Track>();
            using var imp = new G3CharacterImporter();
            MotionResource motion;
            try { motion = imp.LoadMotion(facial + "_LipSync"); }
            catch (Exception) { return Array.Empty<G3FaceKit.Track>(); }
            float step = motion.KeyStep > 0 ? motion.KeyStep : 0.04f;
            count = Mathf.RoundToInt(motion.Duration / step) + 1;
            var tracks = new List<G3FaceKit.Track>();
            // Face bones = nodes the talk rig has beyond the static rig (the facial actor's own nodes).
            var body = new HashSet<string>(staticRig != null ? staticRig.GetComponentsInChildren<Transform>(true).Select(t => t.name) : Enumerable.Empty<string>(),
                                           StringComparer.OrdinalIgnoreCase);
            var names = new HashSet<string>(talkRig.GetComponentsInChildren<Transform>(true).Select(t => t.name).Where(n => !body.Contains(n)), StringComparer.OrdinalIgnoreCase);
            foreach (var t in motion.Tracks)
            {
                if (!names.Contains(t.Node) || (t.Rotation.Length == 0 && t.Position.Length == 0)) continue;
                var track = new G3FaceKit.Track { bone = t.Node, positions = new Vector3[count], rotations = new Quaternion[count] };
                for (int v = 0; v < count; v++)
                {
                    track.positions[v] = G3CharacterImporter.ToUnity(t.SamplePosition(v * step));
                    track.rotations[v] = G3CharacterImporter.ToUnity(t.SampleRotation(v * step));
                }
                tracks.Add(track);
            }
            return tracks.ToArray();
        }

        static Vector3 FrontOf(GameObject character)
        {
            if (character == null) return Vector3.forward;
            var all = character.GetComponentsInChildren<Transform>(true);
            var head = all.FirstOrDefault(t => t.name.EndsWith("_Head", StringComparison.OrdinalIgnoreCase)) ??
                       all.FirstOrDefault(t => t.name.IndexOf("Head", StringComparison.OrdinalIgnoreCase) >= 0 && !t.name.StartsWith("Slot_"));
            var jaw = all.FirstOrDefault(t => t.name.IndexOf("Jaw", StringComparison.OrdinalIgnoreCase) >= 0);
            if (head == null || jaw == null) return Vector3.forward;
            var d = jaw.position - head.position;
            d.y = 0;
            return d.sqrMagnitude > 1e-6f ? d.normalized : Vector3.forward;
        }

        static float HeadHeight(GameObject character)
        {
            var slot = character != null ? character.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "Slot_Hair" || t.name == "Slot_Helmet") : null;
            return slot != null ? slot.position.y - 0.08f : 1.65f;
        }

        static Camera MakeCamera(string name, Vector3 pos, Vector3 target, float fov)
        {
            var go = new GameObject(name);
            go.transform.position = pos;
            go.transform.LookAt(target);
            var cam = go.AddComponent<Camera>();
            cam.fieldOfView = fov;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 200;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.32f, 0.34f, 0.36f);
            cam.allowHDR = true;
            return cam;
        }

        static void SetupLighting()
        {
            var key = new GameObject("Key Light").AddComponent<Light>();
            key.type = LightType.Directional;
            key.transform.rotation = Quaternion.Euler(35, 150, 0); // from front-left, above
            key.color = new Color(1f, 0.96f, 0.9f);
            key.intensity = 1.6f;
            key.shadows = LightShadows.Soft;
            var fill = new GameObject("Fill Light").AddComponent<Light>();
            fill.type = LightType.Directional;
            fill.transform.rotation = Quaternion.Euler(20, -40, 0);
            fill.color = new Color(0.75f, 0.82f, 1f);
            fill.intensity = 0.45f;
            fill.shadows = LightShadows.None;
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.5f, 0.55f, 0.6f);
            RenderSettings.ambientEquatorColor = new Color(0.38f, 0.38f, 0.36f);
            RenderSettings.ambientGroundColor = new Color(0.2f, 0.18f, 0.16f);
            RenderSettings.fog = false;
            RenderSettings.skybox = null;
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            ground.transform.localScale = new Vector3(10, 1, 10);
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader != null)
            {
                var mat = new Material(shader) { name = "RemasterTestGround" };
                mat.SetColor("_BaseColor", new Color(0.42f, 0.42f, 0.4f));
                mat.SetFloat("_Smoothness", 0.1f);
                ground.GetComponent<MeshRenderer>().sharedMaterial = mat;
            }
        }

        // ---------------------------------------------------------------------------------------------------------
        // Capture

        /// <summary>
        /// Renders every still (clip × camera; the 5 m idle also at 0/90/180/270°) for A and B with identical framing
        /// and writes A, B and side-by-side images to GameData/Remaster/Reports/Screens/&lt;label&gt;/. Opens the test
        /// scene when it is not open. Returns the folder and the shot count.
        /// </summary>
        public static string Capture(string label = null, int width = 1600, int height = 900) => Capture(label, width, height, false);

        /// <summary>
        /// <see cref="Capture(string,int,int)"/> with the dialogue rim/fill rig (<see cref="G3CharacterLightRig"/>) on for
        /// every shot when <paramref name="lightRig"/> (R1: compare <c>Capture("r1")</c> with <c>Capture("r1_rig", lightRig: true)</c>).
        /// </summary>
        public static string Capture(string label, int width, int height, bool lightRig)
        {
            var preview = UnityEngine.Object.FindAnyObjectByType<G3RemasterPreview>();
            if (preview == null)
            {
                if (!File.Exists(ScenePath)) return $"no test scene; run Build() first";
                if (Enumerable.Range(0, EditorSceneManager.sceneCount).Any(i => EditorSceneManager.GetSceneAt(i).isDirty))
                    return "open scene has unsaved changes; save it first";
                EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
                preview = UnityEngine.Object.FindAnyObjectByType<G3RemasterPreview>();
                if (preview == null) return "test scene has no G3RemasterPreview";
            }
            label ??= DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string dir = Path.Combine(RemasterPaths.ReportDir(G3RemasterOverride.ProjectDir), "Screens", label);
            Directory.CreateDirectory(dir);

            var baseRot = preview.transform.rotation;
            var all = new[] { preview.original, preview.remaster, preview.originalTalk, preview.remasterTalk };
            bool[] wasActive = all.Select(g => g != null && g.activeSelf).ToArray();
            int shots = 0;
            var colour = new List<RemasterColourCheck.Result>();
            var colourLog = new StringBuilder();
            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { antiAliasing = 4 };
            try
            {
                foreach (var c in preview.clips)
                    for (int ci = 0; ci < preview.cameras.Length; ci++)
                    {
                        var cam = preview.cameras[ci];
                        if (cam == null) continue;
                        if (cam.name == "Cam_1m" && !(c.talk || c.label == "idle")) continue; // portraits: idle and talk only
                        var yaws = c.label == "idle" && cam.name == "Cam_5m" ? new[] { 0, 90, 180, 270 } : new[] { 0 };
                        foreach (int yaw in yaws)
                        {
                            preview.transform.rotation = Quaternion.Euler(0, yaw, 0) * baseRot;
                            var images = new Texture2D[2];
                            (string name, RectInt rect)[] regions = null;
                            for (int k = 0; k < 2; k++)
                            {
                                var go = preview.Pick(k == 1, c.talk);
                                foreach (var g in all) if (g != null) g.SetActive(g == go);
                                if (go == null) continue;
                                Pose(preview, go, c);
                                if (k == 0) regions = ColourRegions(cam, go, width, height);
                                G3CharacterLightRig rig = null;
                                if (lightRig)
                                {
                                    rig = new G3CharacterLightRig(cam);
                                    rig.Begin(go.transform);
                                    rig.Place();
                                }
                                try { images[k] = Render(cam, rt); }
                                finally { rig?.Dispose(); }
                            }
                            string name = $"{c.label}_{cam.name.Replace("Cam_", "")}{(yaws.Length > 1 ? "_" + yaw : "")}";
                            Save(images[0], Path.Combine(dir, name + "_A.png"));
                            Save(images[1], Path.Combine(dir, name + "_B.png"));
                            if (images[0] != null && images[1] != null) Save(SideBySide(images[0], images[1]), Path.Combine(dir, name + "_AB.png"));
                            if (images[0] != null && images[1] != null && regions != null && regions.Length > 0)
                            {
                                // Background plate (no character) for the foreground mask of the colour check.
                                foreach (var g in all) if (g != null) g.SetActive(false);
                                var plate = Render(cam, rt);
                                byte[] pa = images[0].GetRawTextureData(), pb = images[1].GetRawTextureData(), pg = plate.GetRawTextureData();
                                foreach (var (rn, rc) in regions)
                                {
                                    var res = RemasterColourCheck.Compare($"{name} {rn}", pa, pb, pg, width, height, rc.xMin, rc.yMin, rc.xMax, rc.yMax);
                                    colourLog.AppendLine(res.ToString());
                                    if (res.Judged) colour.Add(res);
                                }
                                UnityEngine.Object.DestroyImmediate(plate);
                            }
                            foreach (var i in images) if (i != null) UnityEngine.Object.DestroyImmediate(i);
                            shots++;
                        }
                    }
            }
            finally
            {
                preview.transform.rotation = baseRot;
                for (int i = 0; i < all.Length; i++) if (all[i] != null) all[i].SetActive(wasActive[i]);
                rt.Release();
                UnityEngine.Object.DestroyImmediate(rt);
            }
            string colourSummary = ColourSummary(colour);
            string colourText = colourSummary + Environment.NewLine + colourLog;
            File.WriteAllText(Path.Combine(dir, "colour_check.txt"), colourText);
            return $"{dir}: {shots} shots (A, B, A|B){(lightRig ? " with the rim/fill rig" : "")}, overrides {(preview.remaster != null ? "B built with remaster on" : "-")}" +
                   Environment.NewLine + colourText;
        }

        static string ColourSummary(List<RemasterColourCheck.Result> colour)
        {
            if (colour.Count == 0) return "colour check: no judged regions";
            int fails = colour.Count(r => !r.Pass);
            var worst = colour.OrderByDescending(r => r.DeltaE).First();
            return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "colour check (mean B vs A, CIE76 dE <= {0:0.0}): {1} ({2}/{3} regions fail; worst {4} dE {5:0.0})",
                RemasterColourCheck.MaxDeltaE, fails == 0 ? "PASS" : "FAIL", fails, colour.Count, worst.Name, worst.DeltaE);
        }

        /// <summary>
        /// Face and torso regions of the colour check in image pixels (row 0 = bottom, like ReadPixels): squares around the
        /// face (between the head bone and the hair/helmet slot, radius 0.12 m) and the chest (upper spine, 0.2 m).
        /// </summary>
        static (string name, RectInt rect)[] ColourRegions(Camera cam, GameObject character, int width, int height)
        {
            var all = character.GetComponentsInChildren<Transform>(true);
            var head = all.FirstOrDefault(t => t.name.EndsWith("_Head", StringComparison.OrdinalIgnoreCase)) ??
                       all.FirstOrDefault(t => t.name.IndexOf("Head", StringComparison.OrdinalIgnoreCase) >= 0 && !t.name.StartsWith("Slot_"));
            var slot = all.FirstOrDefault(t => t.name == "Slot_Hair" || t.name == "Slot_Helmet");
            var chest = all.FirstOrDefault(t => t.name.EndsWith("Spine_Spine_3", StringComparison.OrdinalIgnoreCase)) ??
                        all.Where(t => t.name.IndexOf("Spine", StringComparison.OrdinalIgnoreCase) >= 0).OrderByDescending(t => t.position.y).FirstOrDefault();
            var list = new List<(string, RectInt)>();
            if (head != null) list.Add(("face", Square(cam, slot != null ? (head.position + slot.position) * 0.5f : head.position + Vector3.up * 0.08f, 0.12f, width, height)));
            if (chest != null) list.Add(("torso", Square(cam, chest.position, 0.2f, width, height)));
            return list.Where(r => r.Item2.width > 0 && r.Item2.height > 0).ToArray();
        }

        static RectInt Square(Camera cam, Vector3 centre, float radius, int width, int height)
        {
            var c = cam.WorldToViewportPoint(centre);
            if (c.z <= 0) return new RectInt(0, 0, 0, 0);
            var e = cam.WorldToViewportPoint(centre + cam.transform.right * radius);
            int r = Mathf.CeilToInt(Mathf.Abs(e.x - c.x) * width); // square in pixels
            int cx = Mathf.RoundToInt(c.x * width), cy = Mathf.RoundToInt(c.y * height);
            int x0 = Mathf.Clamp(cx - r, 0, width), x1 = Mathf.Clamp(cx + r, 0, width);
            int y0 = Mathf.Clamp(cy - r, 0, height), y1 = Mathf.Clamp(cy + r, 0, height);
            return new RectInt(x0, y0, x1 - x0, y1 - y0);
        }

        static void Pose(G3RemasterPreview preview, GameObject go, G3RemasterPreview.TestClip c)
        {
            var anim = go.GetComponent<Animation>();
            var clip = anim != null ? anim.GetClip(c.clip) : null;
            if (clip != null) clip.SampleAnimation(go, c.stillTime * clip.length);
            if (c.talk && preview.visemeCount > 0)
            {
                int open = OpenViseme(preview);
                preview.PoseViseme(go, open, open, 0);
            }
        }

        /// <summary>The viseme with the widest jaw (largest rotation away from viseme 0 of a jaw track).</summary>
        static int OpenViseme(G3RemasterPreview p)
        {
            var jaw = p.visemes.FirstOrDefault(t => t.bone.IndexOf("Jaw", StringComparison.OrdinalIgnoreCase) >= 0);
            if (jaw == null) return Math.Min(1, p.visemeCount - 1);
            int best = 0;
            for (int v = 0; v < jaw.rotations.Length; v++)
                if (Quaternion.Angle(jaw.rotations[0], jaw.rotations[v]) > Quaternion.Angle(jaw.rotations[0], jaw.rotations[best])) best = v;
            return best;
        }

        static Texture2D Render(Camera cam, RenderTexture rt)
        {
            var prevTarget = cam.targetTexture;
            var prevActive = RenderTexture.active;
            try
            {
                cam.targetTexture = rt;
                cam.Render();
                RenderTexture.active = rt;
                var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                tex.Apply();
                return tex;
            }
            finally
            {
                cam.targetTexture = prevTarget;
                RenderTexture.active = prevActive;
            }
        }

        static Texture2D SideBySide(Texture2D a, Texture2D b)
        {
            var r = new Texture2D(a.width + b.width + 8, Math.Max(a.height, b.height), TextureFormat.RGB24, false);
            var fill = Enumerable.Repeat(new Color32(20, 20, 20, 255), r.width * r.height).ToArray();
            r.SetPixels32(fill);
            r.SetPixels32(0, 0, a.width, a.height, a.GetPixels32());
            r.SetPixels32(a.width + 8, 0, b.width, b.height, b.GetPixels32());
            r.Apply();
            return r;
        }

        static void Save(Texture2D tex, string path)
        {
            if (tex == null) return;
            File.WriteAllBytes(path, tex.EncodeToPNG());
        }

        [MenuItem("G3/Remaster/Build Test Scene (Hero)", priority = 230)]
        static void MenuBuild() => Debug.Log(Build());

        [MenuItem("G3/Remaster/Capture Before-After", priority = 231)]
        static void MenuCapture() => Debug.Log(Capture());

        [MenuItem("G3/Remaster/Capture Before-After (Rim+Fill Rig)", priority = 232)]
        static void MenuCaptureRig() => Debug.Log(Capture(null, 1600, 900, true));
    }
}
