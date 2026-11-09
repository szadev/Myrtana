using Myrtana.Ui.Skin;
using UnityEngine;
using UnityEngine.UIElements;

namespace Myrtana.Unity.Ui.Skin
{
    /// <summary>
    /// Panel settings of the G3 skin. The original screens are authored for 1024×768; windows move or stretch with
    /// their anchors inside a wider screen (formats-gui §7, <see cref="Layout.G3LayoutBuilder"/>). The panels use a
    /// constant pixel size whose scale <see cref="G3UiScale"/> sets per <see cref="GuiPanelKind"/>: the 1024×768
    /// coordinate system fitted to the screen height, times the player's GUI size. Scaling the panel (not the elements)
    /// renders text at the screen's resolution, so it stays crisp.
    /// </summary>
    public static class G3PanelSettings
    {
        public const int ReferenceWidth = 1024;
        public const int ReferenceHeight = 768;
        public const string ThemePath = "MyrtanaUi/G3Runtime";

        public static PanelSettings Create(string name, GuiPanelKind kind, int sortingOrder = 100)
        {
            var settings = ScriptableObject.CreateInstance<PanelSettings>();
            settings.name = name;
            settings.themeStyleSheet = Resources.Load<ThemeStyleSheet>(ThemePath);
            settings.scaleMode = PanelScaleMode.ConstantPixelSize;
            settings.referenceResolution = new Vector2Int(ReferenceWidth, ReferenceHeight);
            settings.sortingOrder = sortingOrder;
            G3UiScale.Register(settings, kind);
            var skin = G3Skin.Load();
            if (skin != null) settings.textSettings = skin.TextSettings;
            return settings;
        }

        /// <summary>A UIDocument on a new GameObject with G3 panel settings and the skin applied to its root.</summary>
        public static UIDocument CreateDocument(string name, GuiPanelKind kind, Transform parent = null, int sortingOrder = 100)
        {
            var go = new GameObject(name);
            if (parent != null) go.transform.SetParent(parent, false);
            var doc = go.AddComponent<UIDocument>();
            doc.panelSettings = Create(name, kind, sortingOrder);
            G3Skin.Load()?.Apply(doc.rootVisualElement);
            doc.rootVisualElement.pickingMode = PickingMode.Ignore;
            return doc;
        }
    }
}
