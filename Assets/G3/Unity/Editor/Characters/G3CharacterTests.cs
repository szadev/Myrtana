using System.Linq;
using System.Text;
using G3.Unity.Characters;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace G3.Unity.Editor.Characters
{
    /// <summary>Test set + test scene for the character importer (Assets/Scenes/Test_Characters.unity).</summary>
    public static class G3CharacterTests
    {
        public const string ScenePath = "Assets/Scenes/Test_Characters.unity";

        public static readonly string[] HumanMotions =
        {
            "Hero_Stand_None_None_P0_Ambient_Loop_N_Fwd_00_%_02_P0_0",
            "Hero_Stand_None_None_P0_Move_Walk_N_Fwd_00_%_00_P0_160",
            "Hero_Stand_None_None_P0_Move_Run_N_Fwd_00_%_00_P0_400",
            "Hero_Stand_None_None_P0_Move_Stand_N_Fwd_00_%_00_P0_0",
        };

        public static readonly string[] WolfMotions =
        {
            "Wolf_Stand_None_None_P0_Ambient_Loop_N_Fwd_00_%_05_P0_0",
            "Wolf_Stand_None_None_P0_Move_Walk_N_Fwd_00_%_00_P0_150",
            "Wolf_Stand_None_None_P0_Move_Run_N_Fwd_00_%_00_P0_400",
        };

        [MenuItem("G3/Characters/Import Test Characters")]
        public static string ImportTestSet() => ImportTestSet(false);

        public static string ImportTestSet(bool force)
        {
            using var imp = new G3CharacterImporter { ForceRebuild = force };
            imp.Import("G3_Human_Rebel", "G3_Hero_Body_RebBandit", new[] { "G3_Hero_Body_RebBandit", "G3_Head_Hero_Myrtana_01" }, HumanMotions, HumanMotions[0]);
            imp.Import("G3_Wolf", "G3_Wolf_Body_01", new[] { "G3_Wolf_Body_01" }, WolfMotions, WolfMotions[0]);
            AssetDatabase.SaveAssets();
            return string.Join("\n", imp.Log);
        }

        /// <summary>Open (or create) the test scene; refuses when another agent's scene has unsaved changes.</summary>
        public static string OpenScene()
        {
            var cur = SceneManager.GetActiveScene();
            if (cur.path != ScenePath)
            {
                if (cur.isDirty) return $"busy: active scene {cur.path} has unsaved changes";
                if (System.IO.File.Exists(ScenePath)) EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
                else
                {
                    var s = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
                    var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
                    ground.name = "Ground";
                    ground.transform.localScale = new Vector3(3, 1, 3);
                    EditorSceneManager.SaveScene(s, ScenePath);
                }
            }
            return null;
        }

        /// <summary>Ensure instances, pose each at the given clip/time and frame the camera. Returns a status line.</summary>
        public static string Pose(string humanClip, string wolfClip, float normalizedTime, string view = "front")
        {
            var busy = OpenScene();
            if (busy != null) return busy;
            var sb = new StringBuilder();
            var human = Ensure("G3_Human_Rebel", new Vector3(0, 0, 0));
            var wolf = Ensure("G3_Wolf", new Vector3(1.6f, 0, 0));
            sb.Append(Sample(human, humanClip, normalizedTime)).Append(" | ").Append(Sample(wolf, wolfClip, normalizedTime));

            var cam = Camera.main;
            if (cam != null)
            {
                switch (view)
                {
                    case "side": cam.transform.position = new Vector3(5.0f, 1.0f, 0.2f); cam.transform.LookAt(new Vector3(0.8f, 0.8f, 0)); break;
                    case "back": cam.transform.position = new Vector3(0.8f, 1.2f, -4.6f); cam.transform.LookAt(new Vector3(0.8f, 0.85f, 0)); break;
                    case "face": cam.transform.position = new Vector3(0.0f, 1.75f, 0.9f); cam.transform.LookAt(new Vector3(0, 1.72f, 0)); break;
                    default: cam.transform.position = new Vector3(0.8f, 1.2f, 4.6f); cam.transform.LookAt(new Vector3(0.8f, 0.85f, 0)); break;
                }
                cam.fieldOfView = 40;
                cam.nearClipPlane = 0.05f;
            }
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            EditorSceneManager.SaveScene(SceneManager.GetActiveScene());
            return sb.ToString();
        }

        /// <summary>
        /// Render the test characters in an isolated preview scene (independent of the scene other agents have open)
        /// into Docs/screens/&lt;name&gt;.png. Clip filters are substrings of the motion name; null = bind pose.
        /// </summary>
        public static string Shot(string name, string humanClip, string wolfClip, float normalizedTime, string view = "front", int w = 1200, int h = 800) =>
            Strip(name, humanClip, wolfClip, normalizedTime.ToString(System.Globalization.CultureInfo.InvariantCulture), view, w, h);

        /// <summary>Render one cell per normalized time (comma separated) side by side into Docs/screens/&lt;name&gt;.png.</summary>
        public static string Strip(string name, string humanClip, string wolfClip, string times, string view = "side", int w = 480, int h = 640)
        {
            var ts = times.Split(',').Select(s => float.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            var pru = new PreviewRenderUtility();
            var sb = new StringBuilder();
            var strip = new Texture2D(w * ts.Length, h, TextureFormat.RGBA32, false);
            Material gm = null;
            try
            {
                var human = Spawn(pru, "G3_Human_Rebel", new Vector3(0, 0, 0));
                var wolf = Spawn(pru, "G3_Wolf", new Vector3(1.6f, 0, 0));
                var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
                ground.transform.localScale = new Vector3(2, 1, 2);
                gm = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                gm.SetColor("_BaseColor", new Color(0.35f, 0.33f, 0.3f));
                ground.GetComponent<Renderer>().sharedMaterial = gm;
                pru.AddSingleGO(ground);

                var cam = pru.camera;
                cam.fieldOfView = 35;
                cam.nearClipPlane = 0.05f; cam.farClipPlane = 100;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.45f, 0.55f, 0.65f);
                Vector3 target = new Vector3(0.8f, 0.85f, 0);
                switch (view)
                {
                    case "side": cam.transform.position = new Vector3(6.0f, 1.0f, 0.0f); break;
                    case "sidehuman": cam.transform.position = new Vector3(-4.0f, 1.0f, 0.0f); target = new Vector3(0, 0.9f, 0); break;
                    case "back": cam.transform.position = new Vector3(0.8f, 1.2f, -5.2f); break;
                    case "face": cam.transform.position = new Vector3(0.0f, 1.75f, 1.0f); target = new Vector3(0, 1.68f, 0); break;
                    case "face34": cam.transform.position = new Vector3(-0.7f, 1.8f, 0.7f); target = new Vector3(0, 1.72f, 0); break;
                    case "faceback": cam.transform.position = new Vector3(0.0f, 1.75f, -1.0f); target = new Vector3(0, 1.68f, 0); break;
                    case "top": cam.transform.position = new Vector3(0.8f, 6f, 0.01f); break;
                    case "wolf": cam.transform.position = new Vector3(4.6f, 0.9f, 1.4f); target = new Vector3(1.6f, 0.45f, 0); break;
                    case "human34": cam.transform.position = new Vector3(-2.2f, 1.4f, 3.0f); target = new Vector3(0, 0.9f, 0); break;
                    default: cam.transform.position = new Vector3(0.8f, 1.2f, 5.2f); break;
                }
                cam.transform.LookAt(target);
                pru.lights[0].intensity = 1.2f;
                pru.lights[0].transform.rotation = Quaternion.Euler(40, -30, 0);
                pru.lights[1].intensity = 0.6f;
                pru.ambientColor = new Color(0.35f, 0.35f, 0.38f);

                for (int i = 0; i < ts.Length; i++)
                {
                    sb.Append(SampleIn(human, humanClip, ts[i])).Append(" | ").Append(SampleIn(wolf, wolfClip, ts[i])).Append('\n');
                    pru.BeginStaticPreview(new Rect(0, 0, w, h));
                    pru.Render(true);
                    var tex = pru.EndStaticPreview();
                    var px = tex.GetPixels32();
                    if (tex.width == w && tex.height == h) strip.SetPixels32(i * w, 0, w, h, px);
                    Object.DestroyImmediate(tex);
                }
                strip.Apply();
                System.IO.Directory.CreateDirectory("Docs/screens");
                System.IO.File.WriteAllBytes($"Docs/screens/{name}.png", strip.EncodeToPNG());
            }
            finally
            {
                pru.Cleanup();
                Object.DestroyImmediate(strip);
                if (gm != null) Object.DestroyImmediate(gm);
            }
            return sb.ToString();
        }

        static GameObject Spawn(PreviewRenderUtility pru, string prefabName, Vector3 pos)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>($"{G3CharacterImporter.Root}/Prefabs/{prefabName}.prefab");
            if (prefab == null) return null;
            var go = pru.InstantiatePrefabInScene(prefab);
            go.transform.position = pos;
            return go;
        }

        static string SampleIn(GameObject go, string clipFilter, float normalizedTime) =>
            string.IsNullOrEmpty(clipFilter) ? $"{go?.name}: bind" : Sample(go, clipFilter, normalizedTime);

        static GameObject Ensure(string prefabName, Vector3 pos)
        {
            var go = GameObject.Find(prefabName);
            if (go == null)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>($"{G3CharacterImporter.Root}/Prefabs/{prefabName}.prefab");
                if (prefab == null) return null;
                go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                go.name = prefabName;
            }
            go.transform.position = pos;
            go.transform.rotation = Quaternion.identity;
            return go;
        }

        static string Sample(GameObject go, string clipFilter, float normalizedTime)
        {
            if (go == null) return "missing";
            var info = go.GetComponent<G3Character>();
            if (string.IsNullOrEmpty(clipFilter))
            {
                // bind pose: re-apply the prefab's transforms
                PrefabUtility.RevertPrefabInstance(go, InteractionMode.AutomatedAction);
                return $"{go.name}: bind";
            }
            var c = info.Clips.FirstOrDefault(x => x.Clip != null && x.Motion.IndexOf(clipFilter, System.StringComparison.OrdinalIgnoreCase) >= 0);
            if (c == null) return $"{go.name}: no clip '{clipFilter}'";
            float t = normalizedTime * c.Clip.length;
            c.Clip.SampleAnimation(go, t);
            // lowest skinned vertex (feet on ground?)
            float minY = float.MaxValue, maxY = float.MinValue;
            foreach (var smr in go.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                var m = new Mesh();
                smr.BakeMesh(m, true);
                foreach (var v in m.vertices)
                {
                    var w = smr.transform.TransformPoint(v);
                    minY = Mathf.Min(minY, w.y); maxY = Mathf.Max(maxY, w.y);
                }
                Object.DestroyImmediate(m);
            }
            return $"{go.name} {c.Clip.name} t={t:F2}/{c.Clip.length:F2}s skinned Y {minY:F3}..{maxY:F3}";
        }
    }
}
