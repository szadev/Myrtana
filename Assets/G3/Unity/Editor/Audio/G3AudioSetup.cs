using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace G3.Unity.Editor
{
    /// <summary>
    /// Adds the G3 audio objects to a scene (idempotent): "G3 Audio" with context, music director, ambient director,
    /// emitter streamer and the music-situation stub, plus <see cref="G3Footsteps"/> on the G3Player. Called by the
    /// master-scene build (G3WorldBatch.CallHook) and from the G3/Audio menu for the open scene.
    /// </summary>
    public static class G3AudioSetup
    {
        public const string RootName = "G3 Audio";

        [MenuItem("G3/Audio/Add Audio To Open Scene", priority = 220)]
        static void Menu() => Debug.Log(SetupScene());

        [MenuItem("G3/Audio/Add Audio To Open Master Scene + Save", priority = 221)]
        static void MenuMaster() => Debug.Log(SetupOpenMaster());

        /// <summary>Sets up the active scene. Returns a one-line summary (shown by the master build log).</summary>
        public static string SetupScene() => SetupScene(SceneManager.GetActiveScene());

        /// <summary>Adds audio to the loaded master scene without rebuilding it, and saves it.</summary>
        public static string SetupOpenMaster()
        {
            var scene = SceneManager.GetSceneByPath(G3WorldBatch.MasterPath);
            if (!scene.IsValid() || !scene.isLoaded) return $"audio setup: {G3WorldBatch.MasterPath} is not open";
            var r = SetupScene(scene);
            EditorSceneManager.SaveScene(scene);
            return r + " (saved)";
        }

        public static string SetupScene(Scene scene)
        {
            var library = AssetDatabase.LoadAssetAtPath<G3AudioLibrary>(G3AudioImporter.LibraryPath);
            var world = AssetDatabase.LoadAssetAtPath<G3AudioWorldAsset>(G3AudioWorldBaker.AssetPath);

            var root = scene.GetRootGameObjects().FirstOrDefault(g => g.name == RootName);
            if (root == null)
            {
                root = new GameObject(RootName);
                if (SceneManager.GetActiveScene() != scene) SceneManager.MoveGameObjectToScene(root, scene);
                Undo.RegisterCreatedObjectUndo(root, RootName);
            }

            var ctx = GetOrAdd<G3AudioContext>(root);
            ctx.library = library;
            ctx.world = world;
            ctx.environment = FindInScene<G3Environment>(scene);
            GetOrAdd<G3MusicDirector>(root);
            GetOrAdd<G3AmbientDirector>(root);
            GetOrAdd<G3AudioEmitterStreamer>(root).streamer = FindInScene<G3WorldStreamer>(scene);
            GetOrAdd<G3MusicSituationOverride>(root);

            string footsteps = "no G3Player";
            var player = FindInScene<G3Player>(scene);
            if (player != null)
            {
                var fs = GetOrAdd<G3Footsteps>(player.gameObject);
                fs.library = library;
                fs.walkSpeed = player.walkSpeed;
                fs.runSpeed = player.runSpeed;
                EditorUtility.SetDirty(fs);
                footsteps = "footsteps on " + player.name;
            }
            EditorUtility.SetDirty(root);
            EditorSceneManager.MarkSceneDirty(scene);

            string data = library == null || world == null ? " — WARNING: run G3/Audio/Import All first, then this again" : "";
            return $"audio: '{RootName}' in {scene.name}, {footsteps}, library {(library != null ? "ok" : "missing")}, world {(world != null ? "ok" : "missing")}{data}";
        }

        static T GetOrAdd<T>(GameObject go) where T : Component
        {
            var c = go.GetComponent<T>();
            return c != null ? c : Undo.AddComponent<T>(go);
        }

        static T FindInScene<T>(Scene scene) where T : Component
        {
            foreach (var g in scene.GetRootGameObjects())
            {
                var c = g.GetComponentInChildren<T>(true);
                if (c != null) return c;
            }
            return null;
        }
    }
}
