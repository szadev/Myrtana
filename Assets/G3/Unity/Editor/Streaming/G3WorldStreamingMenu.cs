using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace G3.Unity.Editor
{
    /// <summary>Edit-mode streaming: open the master scene and the cells around the Scene view camera.</summary>
    public static class G3WorldStreamingMenu
    {
        [MenuItem("G3/World/Open Master Scene", priority = 120)]
        static void MenuOpenMaster()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            EditorSceneManager.OpenScene(G3WorldBatch.MasterPath, OpenSceneMode.Single);
        }

        [MenuItem("G3/World/Open Cells Around Scene Camera", priority = 121)]
        static void MenuOpenAround()
        {
            var sv = SceneView.lastActiveSceneView;
            Vector3 p = sv != null ? sv.camera.transform.position : (Camera.main != null ? Camera.main.transform.position : Vector3.zero);
            if (!SceneManager.GetSceneByPath(G3WorldBatch.MasterPath).isLoaded && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            Debug.Log(OpenCellsAround(p.x, p.z));
        }

        [MenuItem("G3/World/Close All Cells", priority = 122)]
        static void MenuCloseAll() => Debug.Log(CloseCells(_ => true));

        static G3WorldManifest Manifest => AssetDatabase.LoadAssetAtPath<G3WorldManifest>(G3WorldBatch.ManifestPath);

        /// <summary>
        /// Opens the master scene (single, discarding nothing that is saved) if needed, then additively opens every cell
        /// within <paramref name="distance"/> m (default: the streamer's load distance) of (x, z) and closes cells beyond
        /// the unload distance. Also sets the region weather. CLI-friendly; returns a summary.
        /// </summary>
        public static string OpenCellsAround(float x, float z, float distance = -1, bool openMaster = true)
        {
            var manifest = Manifest;
            if (manifest == null) return "no manifest — run the world import first";
            if (openMaster && !SceneManager.GetSceneByPath(manifest.masterScenePath).isLoaded && File.Exists(manifest.masterScenePath))
                EditorSceneManager.OpenScene(manifest.masterScenePath, OpenSceneMode.Single);
            var master = SceneManager.GetSceneByPath(manifest.masterScenePath);
            var streamer = Object.FindAnyObjectByType<G3WorldStreamer>();
            float near = distance > 0 ? distance : streamer != null ? streamer.loadDistance : 400;
            float far = Mathf.Max(near + 50, streamer != null ? streamer.unloadDistance : 600);
            var p = new Vector3(x, 0, z);

            int opened = 0, kept = 0;
            string closed = CloseCells(c => manifest.Distance(c, p) > far);
            foreach (var c in manifest.cells.Where(c => c.HasScene && File.Exists(c.scenePath)).OrderBy(c => manifest.Distance(c, p)))
            {
                float d = manifest.Distance(c, p);
                float lim = c.kind == G3WorldManifest.CellKind.Ocean && streamer != null ? Mathf.Min(near, streamer.oceanLoadDistance) : near;
                if (d > lim) continue;
                if (SceneManager.GetSceneByPath(c.scenePath).isLoaded) { kept++; continue; }
                EditorSceneManager.OpenScene(c.scenePath, OpenSceneMode.Additive);
                opened++;
            }
            if (master.IsValid() && master.isLoaded) SceneManager.SetActiveScene(master);

            string region = manifest.RegionAt(p);
            if (streamer != null)
            {
                var env = streamer.environment != null ? streamer.environment : Object.FindAnyObjectByType<G3Environment>();
                var w = streamer.WeatherFor(region);
                if (env != null && w != null && env.weatherState != w)
                {
                    env.weatherState = w;
                    env.blend = 0;
                    env.Apply();
                }
            }
            return $"cells around ({x:F0},{z:F0}) ≤{near:F0} m: {opened} opened, {kept} already open; {closed}; region {region}";
        }

        /// <summary>
        /// For screenshots/CLI: opens the master (only if the open scenes are saved) and the cells around (x, z), then
        /// puts Camera.main at <paramref name="eye"/> looking at <paramref name="look"/> (metres).
        /// </summary>
        public static string ShowSpot(Vector3 eye, Vector3 look, float distance = -1, float hour = -1)
        {
            var manifest = Manifest;
            if (manifest == null) return "no manifest";
            if (!SceneManager.GetSceneByPath(manifest.masterScenePath).isLoaded)
            {
                if (!G3WorldBatch.OpenScenesClean()) return "open scenes have unsaved changes — not switching";
                EditorSceneManager.OpenScene(manifest.masterScenePath, OpenSceneMode.Single);
            }
            string r = OpenCellsAround(eye.x, eye.z, distance);
            var cam = Camera.main;
            if (cam != null)
            {
                cam.transform.position = eye;
                cam.transform.rotation = Quaternion.LookRotation(look - eye);
            }
            var env = Object.FindAnyObjectByType<G3Environment>();
            if (env != null && hour >= 0) { env.timeOfDay = hour; env.Apply(); }
            var st = Object.FindAnyObjectByType<G3WorldStreamer>();
            if (st != null) st.SendMessage("Update");
            return r + $"; camera {(cam != null ? cam.name : "none")}; weather {(env != null ? env.weatherState : "-")}";
        }

        public static string CloseCells(System.Func<G3WorldManifest.Cell, bool> which)
        {
            var manifest = Manifest;
            if (manifest == null) return "no manifest";
            int n = 0;
            foreach (var c in manifest.cells)
            {
                if (!c.HasScene || !which(c)) continue;
                var s = SceneManager.GetSceneByPath(c.scenePath);
                if (!s.IsValid() || !s.isLoaded || s.isDirty || SceneManager.sceneCount <= 1) continue;
                EditorSceneManager.CloseScene(s, true);
                n++;
            }
            return $"{n} closed";
        }
    }
}
