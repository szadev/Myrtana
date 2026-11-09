using Myrtana.Unity.Ui.Skin;
using UnityEngine;
using UnityEngine.UIElements;

namespace Myrtana.Unity
{
    /// <summary>
    /// The UI Toolkit document of the gameplay HUD (Resources/Myrtana/Hud.uxml + Hud.uss): interaction prompt, dialog
    /// panel. Created in code so no scene needs editing; scales with the screen from a 1920×1080 reference. With the G3
    /// GUI imported its texts (the game-over line, the fallback prompt) use the G3 font like every other panel.
    /// </summary>
    public static class HudDocument
    {
        const string ResourceFolder = "Myrtana/";

        public static UIDocument Create(Transform parent)
        {
            var settings = ScriptableObject.CreateInstance<PanelSettings>();
            settings.name = "Myrtana HUD";
            settings.themeStyleSheet = Resources.Load<ThemeStyleSheet>(ResourceFolder + "MyrtanaTheme");
            settings.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            settings.referenceResolution = new Vector2Int(1920, 1080);
            settings.match = 0.5f;
            settings.sortingOrder = 100;

            var go = new GameObject("Myrtana HUD");
            go.transform.SetParent(parent, false);
            var document = go.AddComponent<UIDocument>();
            document.panelSettings = settings;
            document.visualTreeAsset = Resources.Load<VisualTreeAsset>(ResourceFolder + "Hud");
            var skin = G3Skin.Load();
            if (skin != null)
            {
                settings.textSettings = skin.TextSettings;
                skin.ApplyFont(document.rootVisualElement);
            }
            return document;
        }
    }
}
