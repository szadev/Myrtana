using System.IO;
using Myrtana.Unity.Ui.Showcase;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace G3.Unity.Editor.Gui
{
    /// <summary>Creates the G3 skin showcase scene (camera + <see cref="G3SkinShowcase"/>); open it and press Play.</summary>
    public static class G3GuiShowcaseMenu
    {
        public const string ScenePath = "Assets/Myrtana/Unity/Ui/Showcase/G3UiShowcase.unity";

        [MenuItem("G3/GUI/Create Showcase Scene")]
        public static string CreateScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            var cam = new GameObject("Camera").AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.09f, 0.08f, 0.07f);
            cam.tag = "MainCamera";
            var go = new GameObject("G3 Skin Showcase");
            go.AddComponent<G3SkinShowcase>();
            SceneManager.MoveGameObjectToScene(cam.gameObject, scene);
            SceneManager.MoveGameObjectToScene(go, scene);
            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath)!);
            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorSceneManager.CloseScene(scene, true);
            return ScenePath;
        }
    }
}
