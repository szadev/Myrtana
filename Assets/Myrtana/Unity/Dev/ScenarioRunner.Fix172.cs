#if UNITY_EDITOR || DEVELOPMENT_BUILD || G3_DEMO
using System.Collections;
using System.Collections.Generic;
using G3.Unity;
using Myrtana.Session.Dev;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace Myrtana.Unity.Dev
{
    /// <summary>
    /// fix-172 ops (Docs/dev-scenarios.md): <c>fps [on|off]</c> (the FPS overlay, <see cref="FpsOverlay"/>) and
    /// <c>perfreport [secs=30]</c> (<see cref="PerfReportRecorder"/>: frame-time summary to Player.log and
    /// LocalLow/G3World/G3World/perf_&lt;date&gt;.txt with scene, cell, position, quality and remaster context).
    /// </summary>
    public sealed partial class ScenarioRunner
    {
        void FpsStep(ScenarioStep st)
        {
            if (!G3.Player.NoclipRules.TryParse(st.Get("on"), out bool? want)) { Fail($"fps: '{st.Get("on")}' is not on/off"); return; }
            Log(FpsOverlay.Command(want));
        }

        /// <summary>'perfreport [secs=30] [delay=1] [wait=0]': wait=1 holds the scenario until the report is written.</summary>
        IEnumerator PerfReportStep(ScenarioStep st)
        {
            float secs = st.Float("secs", 30f), delay = st.Float("delay", 1f);
            bool wait = st.Bool("wait", false);
            string started = PerfReportRecorder.Begin(secs, delay, PerfContext, Log);
            Log(started);
            if (!wait || PerfReportRecorder.Current == null) yield break;
            while (PerfReportRecorder.Current != null) yield return null;
        }

        /// <summary>The report's context lines, taken when the recording ends.</summary>
        List<KeyValuePair<string, string>> PerfContext()
        {
            var l = new List<KeyValuePair<string, string>>();
            void Add(string k, string v) => l.Add(new KeyValuePair<string, string>(k, v));

            var hero = Hero != null ? Hero : FindAnyObjectByType<G3Player>();
            Vector3? pos = hero != null ? hero.transform.position : (Vector3?)null;
            var cam = Camera.main;
            Add("build", $"{Application.productName} {Application.version}, Unity {Application.unityVersion}, " +
                         $"{(Application.isEditor ? "editor" : Debug.isDebugBuild ? "development player" : "player")}");
            Add("system", $"{SystemInfo.graphicsDeviceName} ({SystemInfo.graphicsDeviceType}, {SystemInfo.graphicsMemorySize} MB), " +
                          $"{SystemInfo.processorType} x{SystemInfo.processorCount}, RAM {SystemInfo.systemMemorySize} MB, {SystemInfo.operatingSystem}");
            Add("frame timing", FrameTimingManager.IsFeatureEnabled() ? "FrameTimingManager on" : "FrameTimingManager off (no CPU/GPU ms)");

            var scenes = new List<string>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var sc = SceneManager.GetSceneAt(i);
                if (sc.isLoaded && !sc.name.StartsWith("Cell_")) scenes.Add(sc.name);
            }
            Add("scene", $"{SceneManager.GetActiveScene().name} (loaded: {string.Join(", ", scenes)})");
            var streamer = FindAnyObjectByType<G3WorldStreamer>();
            if (streamer != null)
            {
                string cell = "?";
                if (streamer.manifest != null && pos.HasValue)
                {
                    var c = streamer.manifest.CellOf(pos.Value);
                    cell = G3WorldManifest.SceneName(c.x, c.y);
                }
                Add("cell", $"{cell}, region {streamer.CurrentRegion ?? "-"}, cells loaded {streamer.LoadedCount} loading {streamer.LoadingCount}, " +
                            $"load/unload distance {streamer.loadDistance:0}/{streamer.unloadDistance:0} m");
            }
            else Add("cell", "no world streamer");
            Add("position", pos.HasValue ? $"hero {V3(pos.Value)}" + (cam != null ? $", camera {V3(cam.transform.position)} yaw {cam.transform.eulerAngles.y:0}" : "") : "no hero");
            var env = FindAnyObjectByType<G3Environment>();
            if (env != null) Add("time", $"{(int)env.timeOfDay:00}:{(int)(env.timeOfDay % 1 * 60):00}, timeScale {Time.timeScale:0.##}");

            Add("screen", $"{Screen.width}x{Screen.height} {Screen.fullScreenMode} @{Screen.currentResolution.refreshRateRatio.value:0.#} Hz, " +
                          $"vSyncCount {QualitySettings.vSyncCount}, targetFrameRate {Application.targetFrameRate}");
            var urp = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            string q = $"level {QualitySettings.names[QualitySettings.GetQualityLevel()]}, lodBias {QualitySettings.lodBias:0.##}, " +
                       $"maxLOD {QualitySettings.maximumLODLevel}, mipmap limit {QualitySettings.globalTextureMipmapLimit}, aniso {QualitySettings.anisotropicFiltering}, " +
                       $"pixel lights {QualitySettings.pixelLightCount}";
            if (urp != null)
                q += $", URP {urp.name}: renderScale {urp.renderScale:0.###} {urp.upscalingFilter}, shadow distance {urp.shadowDistance:0} m " +
                     $"cascades {urp.shadowCascadeCount}, MSAA {urp.msaaSampleCount}, HDR {urp.supportsHDR}";
            Add("quality", q);
            Add("view distance", cam != null ? $"camera far {cam.farClipPlane:0} m, near {cam.nearClipPlane:0.###}, fov {cam.fieldOfView:0.#}" : "no main camera");
            Add("menu options", OptionsPreferences.DescribeDisplay());
            Add("render tuning", G3RenderTuning.Options.Describe());
            Add("remaster", $"{FpsOverlay.RemasterLine()} (world parts: {WorldRemasterParts.Describe(G3WorldRemaster.Parts)}, characters {(G3CharacterRemaster.Enabled ? "on" : "off")}, " +
                            $"snow {(G3SnowV2.Active ? "on" : "off")}, relief {(G3TerrainRelief.Active ? "on" : "off")}, modern lighting {(G3WorldGi.ModernActive ? "on" : "off")})");
            if (_session != null)
                Add("session", $"NPC bodies {CountOf<NpcView>()}");
            return l;
        }

        static int CountOf<T>() where T : Object => FindObjectsByType<T>(FindObjectsSortMode.None).Length;

        static string V3(Vector3 p) => string.Format(Inv, "{0:0.0},{1:0.0},{2:0.0}", p.x, p.y, p.z);
    }
}
#endif
