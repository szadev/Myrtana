using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace G3.Unity.Editor
{
    /// <summary>
    /// Scene setup for the G3 wind: adds one <see cref="G3Wind"/> (linked to the scene's G3Environment) and removes
    /// BatchingStatic from renderers that use the wind shader (static batching bakes them to world space, which
    /// would lose the tree pivot the wind bends around). Called by the world importer; marks the scene dirty.
    /// </summary>
    public static class G3WindSetup
    {
        const string WindShader = "G3/WindLit";

        [MenuItem("G3/Wind/Setup Wind In Scene")]
        public static void SetupSceneMenu() => Debug.Log(SetupScene());

        public static string SetupScene()
        {
            var scene = EditorSceneManager.GetActiveScene();
            var wind = Object.FindAnyObjectByType<G3Wind>(FindObjectsInactive.Include);
            bool created = false;
            if (wind == null)
            {
                var go = new GameObject("G3Wind");
                wind = go.AddComponent<G3Wind>();
                created = true;
            }
            if (wind.environment == null) wind.environment = Object.FindAnyObjectByType<G3Environment>();

            int unbatched = 0;
            foreach (var r in Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (!r.sharedMaterials.Any(m => m != null && m.shader != null && m.shader.name == WindShader)) continue;
                var go = r.gameObject;
                var flags = GameObjectUtility.GetStaticEditorFlags(go);
                if ((flags & StaticEditorFlags.BatchingStatic) == 0) continue;
                GameObjectUtility.SetStaticEditorFlags(go, flags & ~StaticEditorFlags.BatchingStatic);
                unbatched++;
            }
            EditorUtility.SetDirty(wind);
            EditorSceneManager.MarkSceneDirty(scene);
            return $"G3Wind {(created ? "added" : "present")} (environment={(wind.environment != null ? wind.environment.name : "none")}), " +
                   $"BatchingStatic removed from {unbatched} wind renderers";
        }
    }
}
