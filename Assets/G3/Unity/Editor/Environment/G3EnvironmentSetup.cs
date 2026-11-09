using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace G3.Unity.Editor
{
    /// <summary>Adds/configures the G3 environment (weather-driven sun, ambient, fog, sky) in the active scene.</summary>
    public static class G3EnvironmentSetup
    {
        public const string SkyMaterialPath = G3WeatherImporter.AssetFolder + "/G3Sky.mat";
        public const string PostProfilePath = G3WeatherImporter.AssetFolder + "/G3PostProfile.asset";
        public const string PostVolumeName = "G3 Post Volume";
        /// <summary>
        /// _InstColorScale of the G3/Vegetation materials. The per-plant colour (eCVegetation_PS, 0xAARRGGBB, mostly
        /// 0.4–0.8) is multiplied straight into the linear albedo; under the weather lighting the grass read darker than
        /// the trees next to it, so it is lifted a little here (environment tuning, the shader is unchanged).
        /// </summary>
        public const float VegetationInstColorScale = 1.3f;

        [MenuItem("G3/Environment/Setup Scene Environment")]
        public static void SetupSceneMenu()
        {
            var env = SetupScene();
            Selection.activeGameObject = env.gameObject;
        }

        /// <summary>
        /// Idempotent: finds or creates the "G3 Environment" object, the main directional light, the weather
        /// asset (imports it when missing) and the procedural sky material, then applies the current time once.
        /// </summary>
        public static G3Environment SetupScene()
        {
            var weather = AssetDatabase.LoadAssetAtPath<G3WeatherAsset>(G3WeatherImporter.AssetPath)
                          ?? G3WeatherImporter.Import();

            var env = Object.FindFirstObjectByType<G3Environment>();
            if (env == null)
            {
                var go = new GameObject("G3 Environment");
                Undo.RegisterCreatedObjectUndo(go, "G3 Environment");
                env = go.AddComponent<G3Environment>();
            }
            env.weather = weather;
            if (string.IsNullOrEmpty(env.weatherState) || weather.GetState(env.weatherState) == null && weather.GetEnvironment(env.weatherState) == null)
                env.weatherState = "Myrtana";

            env.sun = FindOrCreateSun();
            env.skyMaterial = GetSkyMaterial();

            var cam = Camera.main;
            if (cam != null)
            {
                cam.clearFlags = CameraClearFlags.Skybox;
                if (cam.farClipPlane < 1000f) cam.farClipPlane = 5000f;
            }
            SetupPostVolume(env.gameObject.scene, cam);
            SetVegetationBrightness(VegetationInstColorScale);

            // fog/ambient/sky are driven by the component (it writes RenderSettings.ambientProbe directly)
            RenderSettings.defaultReflectionMode = DefaultReflectionMode.Skybox;
            env.Apply();
            EditorUtility.SetDirty(env);
            EditorSceneManager.MarkSceneDirty(env.gameObject.scene);
            return env;
        }

        static Light FindOrCreateSun()
        {
            Light best = RenderSettings.sun != null && RenderSettings.sun.type == LightType.Directional ? RenderSettings.sun : null;
            if (best == null)
                foreach (var l in Object.FindObjectsByType<Light>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                    if (l.type == LightType.Directional && (best == null || l.intensity > best.intensity)) best = l;
            if (best == null)
            {
                var go = new GameObject("Directional Light");
                Undo.RegisterCreatedObjectUndo(go, "Directional Light");
                best = go.AddComponent<Light>();
                best.type = LightType.Directional;
            }
            best.shadows = LightShadows.Soft;
            best.enabled = true;
            RenderSettings.sun = best;
            return best;
        }

        /// <summary>G3/Sky material (Assets/G3/Environment/G3Sky.mat); an older Skybox/Procedural one is switched over.</summary>
        static Material GetSkyMaterial()
        {
            var shader = Shader.Find("G3/Sky");
            var m = AssetDatabase.LoadAssetAtPath<Material>(SkyMaterialPath);
            if (m != null)
            {
                if (shader != null && m.shader != shader) { m.shader = shader; EditorUtility.SetDirty(m); AssetDatabase.SaveAssets(); }
                return m;
            }
            m = new Material(shader != null ? shader : Shader.Find("Skybox/Procedural")) { name = "G3Sky" };
            AssetDatabase.CreateAsset(m, SkyMaterialPath);
            AssetDatabase.SaveAssets();
            return m;
        }

        /// <summary>
        /// The base post volume ("G3 Post Volume", profile Assets/G3/Environment/G3PostProfile.asset): G3's look with no
        /// tonemapping and no exposure offset (the port's lit image is already matched to CP 1.75), and URP's bloom off: G3's
        /// bloom, DoF and noise are G3ImageProcessing (fix-55). At runtime the options volume (priority 100) pins these too;
        /// post-processing on the camera comes from G3CameraPost (fix-50). Idempotent.
        /// </summary>
        public static Volume SetupPostVolume(UnityEngine.SceneManagement.Scene scene, Camera cam)
        {
            var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(PostProfilePath);
            if (profile == null)
            {
                profile = ScriptableObject.CreateInstance<VolumeProfile>();
                profile.name = "G3PostProfile";
                AssetDatabase.CreateAsset(profile, PostProfilePath);
            }
            var tm = Get<Tonemapping>(profile);
            tm.mode.Override(TonemappingMode.None);
            var bloom = Get<Bloom>(profile);
            bloom.intensity.Override(0f);
            var ca = Get<ColorAdjustments>(profile);
            ca.postExposure.Override(0f);
            ca.saturation.Override(0f);
            EditorUtility.SetDirty(profile);
            AssetDatabase.SaveAssets();

            Volume vol = null;
            foreach (var v in Object.FindObjectsByType<Volume>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (v.name == PostVolumeName && v.gameObject.scene == scene) { vol = v; break; }
            if (vol == null)
            {
                var go = new GameObject(PostVolumeName);
                if (scene.IsValid()) UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(go, scene);
                Undo.RegisterCreatedObjectUndo(go, PostVolumeName);
                vol = go.AddComponent<Volume>();
            }
            vol.isGlobal = true;
            vol.priority = 0;
            vol.sharedProfile = profile;
            EditorUtility.SetDirty(vol);

            if (cam != null)
            {
                var data = cam.GetUniversalAdditionalCameraData();
                if (data != null && !data.renderPostProcessing) { data.renderPostProcessing = true; EditorUtility.SetDirty(data); }
            }
            return vol;
        }

        /// <summary>Sets _InstColorScale on every G3/Vegetation material asset; returns the number changed.</summary>
        public static int SetVegetationBrightness(float scale)
        {
            int n = 0;
            foreach (var guid in AssetDatabase.FindAssets("t:Material"))
            {
                var m = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(guid));
                if (m == null || m.shader == null || m.shader.name != "G3/Vegetation" || !m.HasProperty("_InstColorScale")) continue;
                if (Mathf.Approximately(m.GetFloat("_InstColorScale"), scale)) continue;
                m.SetFloat("_InstColorScale", scale);
                EditorUtility.SetDirty(m);
                n++;
            }
            if (n > 0) AssetDatabase.SaveAssets();
            return n;
        }

        static T Get<T>(VolumeProfile p) where T : VolumeComponent
        {
            if (!p.TryGet<T>(out var c))
            {
                c = p.Add<T>(true);
                c.name = typeof(T).Name;
                AssetDatabase.AddObjectToAsset(c, p);
            }
            c.active = true;
            return c;
        }
    }
}
