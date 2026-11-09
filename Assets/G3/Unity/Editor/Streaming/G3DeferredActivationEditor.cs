using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace G3.Unity.Editor
{
    /// <summary>
    /// Edit-mode side of <see cref="G3DeferredActivation"/>: cell scenes store their NPCs and trees inactive (they are
    /// woken time-sliced at runtime). When a cell is opened in the editor the content is shown right away without
    /// dirtying the scene, and it is saved inactive again.
    /// </summary>
    [InitializeOnLoad]
    static class G3DeferredActivationEditor
    {
        static readonly MethodInfo ClearDirtiness =
            typeof(EditorSceneManager).GetMethod("ClearSceneDirtiness", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

        static G3DeferredActivationEditor()
        {
            EditorSceneManager.sceneOpened += (s, mode) => Show(s, true);
            EditorSceneManager.sceneSaving += (s, path) => Show(s, false);
            EditorSceneManager.sceneSaved += s => Show(s, true);
        }

        static void Show(Scene s, bool active)
        {
            if (BuildPipeline.isBuildingPlayer || EditorApplication.isPlayingOrWillChangePlaymode || !s.isLoaded) return;
            bool any = false, wasDirty = s.isDirty;
            foreach (var root in s.GetRootGameObjects())
                foreach (var d in root.GetComponentsInChildren<G3DeferredActivation>(true))
                    if (d.targets != null)
                        foreach (var t in d.targets)
                            if (t != null && t.activeSelf != active) { t.SetActive(active); any = true; }
            // Showing is a view-only change: keep the scene clean (saving re-hides first, see sceneSaving).
            if (any && active && !wasDirty && ClearDirtiness != null) ClearDirtiness.Invoke(null, new object[] { s });
        }
    }
}
