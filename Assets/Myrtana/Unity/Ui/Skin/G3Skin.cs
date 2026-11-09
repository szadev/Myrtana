using System;
using System.Collections.Generic;
using Myrtana.Ui.Skin;
using UnityEngine;
using UnityEngine.TextCore;
using UnityEngine.TextCore.Text;
using UnityEngine.UIElements;

namespace Myrtana.Unity.Ui.Skin
{
    /// <summary>
    /// The loaded G3 skin: theme and layout data (<see cref="Catalog"/>), sprites by image name, item icons, the
    /// Gothic3 font and the style sheets. One instance per process (<see cref="Load"/>); null when the GUI has not been
    /// imported yet (menu <c>G3/GUI/Import</c>) — screens then fall back to their plain look.
    /// </summary>
    public sealed class G3Skin
    {
        /// <summary>Hand-written component styles (Resources/MyrtanaUi/G3Skin.uss).</summary>
        public const string ComponentSheetPath = "MyrtanaUi/G3Skin";
        public const string ThemeGroup = "theme";
        const string ThemePrefix = "theme:";

        /// <summary>
        /// OS faces the engine falls back to for glyphs Gothic3 lacks, in its order (Docs/formats-gui.md §5,
        /// eCGUIDatabase::Create). Gothic3.ttf maps Cyrillic to placeholder outlines; the importer replaces them with
        /// a Cyrillic alphabet built from Gothic3's Latin (Tools/Fonts/g3cyrillic.py), so these only serve the remaining
        /// code points, and all of Russian when the generator could not run.
        /// </summary>
        public static readonly string[] FallbackFamilies = { "Arial Unicode MS", "Tahoma" };

        static G3Skin _instance;
        static bool _loadFailed;

        readonly G3SkinAssets _assets;
        readonly Dictionary<string, G3ImageLibrary> _libraries = new Dictionary<string, G3ImageLibrary>(StringComparer.Ordinal);

        public GuiSkinCatalog Catalog { get; }
        public ItemIconResolver Icons { get; }
        public StyleSheet ThemeSheet => _assets.theme;
        public StyleSheet ComponentSheet { get; }

        PanelTextSettings _textSettings;

        G3Skin(G3SkinAssets assets, GuiSkinCatalog catalog)
        {
            _assets = assets;
            Catalog = catalog;
            Icons = new ItemIconResolver(catalog);
            ComponentSheet = Resources.Load<StyleSheet>(ComponentSheetPath);
            ScaleFace(assets.fontAsset);
        }

        /// <summary>
        /// Draws Gothic3 at <see cref="G3FontMetrics.ResFaceScale"/> of its em, so a res font size gives the original's
        /// Times-sized text (Docs/ui-skin.md "Fonts"). Set on the loaded asset in memory, the same value every time.
        /// </summary>
        static void ScaleFace(FontAsset font)
        {
            if (font == null) return;
            var face = font.faceInfo;
            face.scale = G3FontMetrics.ResFaceScale;
            font.faceInfo = face;
        }

        /// <summary>The imported skin, or null (logged once) when it is missing or unreadable.</summary>
        public static G3Skin Load()
        {
            if (_instance != null || _loadFailed) return _instance;
            var assets = Resources.Load<G3SkinAssets>(G3SkinAssets.ResourcePath);
            if (assets == null || assets.data == null)
            {
                _loadFailed = true;
                Debug.LogWarning("G3 skin not imported: run the menu G3/GUI/Import (needs the Gothic 3 data).");
                return null;
            }
            try
            {
                _instance = new G3Skin(assets, new GuiSkinCatalog(GuiSkinJson.Read(assets.data.text)));
            }
            catch (Exception e)
            {
                _loadFailed = true;
                Debug.LogError("G3 skin data unreadable, re-run G3/GUI/Import: " + e.Message);
            }
            return _instance;
        }

        /// <summary>Forget the cached instance (after a re-import in the editor).</summary>
        public static void Reset() { _instance = null; _loadFailed = false; _hdEnabled = null; }

        /// <summary>
        /// Entering Play without a domain reload keeps the static instance, but the runtime fallback font assets of the
        /// previous Play session lost their atlas textures (Russian text vanished, MissingReferenceException): start fresh.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetOnPlay() => Reset();

        /// <summary>Sprite of a packed image ("G3_HUD_View_Item_Back.tga", "IT_Apple") or a theme crop ("theme:listctrl_item_tiles_back").</summary>
        public Sprite Sprite(string imageName)
        {
            if (string.IsNullOrEmpty(imageName)) return null;
            if (imageName.StartsWith(ThemePrefix, StringComparison.OrdinalIgnoreCase))
                return Library(ThemeGroup)?.Get(GuiSkinCatalog.ImageKey(imageName.Substring(ThemePrefix.Length)));
            var info = Catalog.Image(imageName);
            if (info == null) return null;
            string key = GuiSkinCatalog.ImageKey(imageName);
            return (HdEnabled && HdImages.Wanted(info.Group, imageName) ? Hd(key) : null) ?? Library(info.Group)?.Get(key);
        }

        readonly Dictionary<string, Sprite> _hd = new Dictionary<string, Sprite>(StringComparer.Ordinal);
        static bool? _hdEnabled;

        /// <summary>
        /// map-hd: the 4x map pictures (<see cref="HdImages"/>) replace the originals when imported; PlayerPrefs
        /// <see cref="HdImages.PrefKey"/> / env <see cref="HdImages.EnvVar"/>, default on. Read once per process.
        /// </summary>
        public static bool HdEnabled
        {
            get
            {
                if (_hdEnabled == null)
                {
                    int? stored = null;
                    try { if (PlayerPrefs.HasKey(HdImages.PrefKey)) stored = PlayerPrefs.GetInt(HdImages.PrefKey); } catch (Exception) { }
                    _hdEnabled = HdImages.Resolve(Environment.GetEnvironmentVariable(HdImages.EnvVar),
                                                  Environment.GetEnvironmentVariable(HdImages.SharedEnvVar), stored);
                }
                return _hdEnabled.Value;
            }
        }

        /// <summary>The HD copy of an image (one sprite per Resources file, loaded only when asked for; misses cached).</summary>
        Sprite Hd(string key)
        {
            if (!_hd.TryGetValue(key, out var s))
            {
                s = Resources.Load<Sprite>(HdImages.ResourceFolder + key);
                _hd[key] = s;
            }
            return s;
        }

        /// <summary>Icon of an item: <c>gCItem_PS.Texture</c>, else the category default (grey variant when not learned).</summary>
        public Sprite ItemIcon(string texture, ItemIconCategory category, bool learned = true) =>
            Sprite(Icons.Resolve(texture, category, learned));

        G3ImageLibrary Library(string group)
        {
            if (!_libraries.TryGetValue(group, out var lib))
            {
                lib = Resources.Load<G3ImageLibrary>(G3SkinAssets.ImageLibraryFolder + group);
                _libraries[group] = lib;
            }
            return lib;
        }

        /// <summary>Font definition of the Gothic3 face (SDF asset when imported, else the TTF, else none).</summary>
        public StyleFontDefinition Font
        {
            get
            {
                if (_assets.fontAsset != null) return new StyleFontDefinition(FontDefinition.FromSDFFont(_assets.fontAsset));
                if (_assets.font != null) return new StyleFontDefinition(FontDefinition.FromFont(_assets.font));
                return new StyleFontDefinition(StyleKeyword.Null);
            }
        }

        /// <summary>
        /// Text settings for G3 panels: the <see cref="FallbackFamilies"/> as global fallbacks, each scaled so its cap
        /// height matches the scaled Gothic3's (Gothic3 caps are 1.07 em, Arial's 0.72 em), so mixed EN/RU lines keep one size.
        /// </summary>
        public PanelTextSettings TextSettings
        {
            get
            {
                if (_textSettings != null) return _textSettings;
                _textSettings = ScriptableObject.CreateInstance<PanelTextSettings>();
                _textSettings.name = "G3TextSettings";
                _textSettings.hideFlags = HideFlags.DontSave;
                var fallbacks = new List<FontAsset>();
                foreach (var family in FallbackFamilies)
                {
                    var fallback = FontAsset.CreateFontAsset(family, "Regular");
                    if (fallback == null) continue;
                    fallback.hideFlags = HideFlags.DontSave;
                    MatchCapHeight(fallback, _assets.fontAsset);
                    AddBoldFace(fallback, family);
                    fallbacks.Add(fallback);
                }
                if (fallbacks.Count == 0) Debug.LogWarning("G3 skin: no Cyrillic fallback font found (" + string.Join(", ", FallbackFamilies) + ").");
                _textSettings.fallbackFontAssets = fallbacks;
                return _textSettings;
            }
        }

        /// <summary>
        /// GDI emboldens linked faces for the bold default font; the text generator does not, so bold fallback text
        /// uses the family's bold face (Arial Unicode MS has none: Tahoma Bold).
        /// </summary>
        void AddBoldFace(FontAsset fallback, string family)
        {
            var bold = FontAsset.CreateFontAsset(family, "Bold") ?? FontAsset.CreateFontAsset("Tahoma", "Bold");
            if (bold == null || fallback.fontWeightTable == null || fallback.fontWeightTable.Length <= 7) return;
            bold.hideFlags = HideFlags.DontSave;
            MatchCapHeight(bold, _assets.fontAsset);
            fallback.fontWeightTable[7].regularTypeface = bold;
        }

        static void MatchCapHeight(FontAsset fallback, FontAsset primary)
        {
            if (primary == null) return;
            FaceInfo p = primary.faceInfo, f = fallback.faceInfo;
            if (p.pointSize <= 0 || f.pointSize <= 0 || p.capLine <= 0 || f.capLine <= 0) return;
            f.scale = (p.capLine / p.pointSize) * G3FontMetrics.ResFaceScale / (f.capLine / f.pointSize);
            fallback.faceInfo = f;
        }

        /// <summary>Makes <paramref name="root"/> a G3-skinned subtree: style sheets, the <c>g3-root</c> class and the font.</summary>
        public void Apply(VisualElement root)
        {
            if (ThemeSheet != null && !root.styleSheets.Contains(ThemeSheet)) root.styleSheets.Add(ThemeSheet);
            if (ComponentSheet != null && !root.styleSheets.Contains(ComponentSheet)) root.styleSheets.Add(ComponentSheet);
            root.AddToClassList(ThemeUssWriter.RootClass);
            ApplyFont(root);
        }

        /// <summary>
        /// The G3 font for a subtree (<see cref="G3FontMap"/>): the port's face, and the theme text font (16 px bold) as the
        /// inherited default, which is what the original draws every control without a font of its own with. Panels whose
        /// settings do not come from <see cref="G3PanelSettings"/> also need <see cref="TextSettings"/> for the fallbacks.
        /// </summary>
        public void ApplyFont(VisualElement root)
        {
            CheckCoverage();
            root.style.unityFontDefinition = Font;
            G3Text.Font(root, G3FontMap.ThemeText);
        }

        bool _coverageChecked;

        /// <summary>
        /// Logs once when the imported font asset is not built from <see cref="G3FontMap.PortFontPath"/> (it lacks the
        /// Cyrillic, so Russian falls back to Arial/Tahoma). The editor rebuilds it on load (G3FontAssetCheck).
        /// </summary>
        void CheckCoverage()
        {
            if (_coverageChecked) return;
            _coverageChecked = true;
            var font = _assets.fontAsset;
            if (font == null) return;
            const string probe = "Дж’…"; // Д ж ’ …
            int missing = 0;
            foreach (char c in probe)
                if (!font.HasCharacter(c, false, true)) missing++;
            if (missing > 0)
                Debug.LogError($"G3 skin: the GUI font '{font.name}' lacks {missing} of '{probe}' (Russian would fall back to " +
                               $"{string.Join("/", FallbackFamilies)}); rebuild it from {G3FontMap.PortFontPath} with the menu G3/GUI/Import.");
        }

        /// <summary>Theme text colour of an element in a state (e.g. "static_text" Normal = gold label colour).</summary>
        public Color ThemeText(string element, WidgetState state, Color fallback)
        {
            var e = Catalog.Element(element);
            return e == null ? fallback : e.Text(state).ToColor();
        }
    }

    public static class SkinColorExtensions
    {
        public static Color ToColor(this SkinColor c) => new Color32(c.R, c.G, c.B, c.A);
    }
}
