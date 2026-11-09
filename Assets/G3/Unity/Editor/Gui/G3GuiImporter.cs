using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using G3.Formats.Gui;
using G3.Formats.Pak;
using Myrtana.Ui.Skin;
using Myrtana.Unity.Ui.Skin;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.TextCore.LowLevel;
using UnityEngine.TextCore.Text;
using Debug = UnityEngine.Debug;

namespace G3.Unity.Editor.Gui
{
    /// <summary>
    /// Menu <c>G3/GUI/Import</c>: turns the original GUI data into Unity assets under <c>Assets/G3Imported/Gui</c>
    /// (game-derived, git-ignored; Docs/ui-skin.md):
    /// <list type="bullet">
    /// <item><c>Images/&lt;group&gt;/*.png</c> — every packed image of g3.igl (840 item icons incl. _Grey variants, HUD
    /// icons, view backgrounds, maps, slides) cropped from the DXT3 atlases of gui.pak, imported as sprites.</item>
    /// <item><c>Theme/*.png</c> — the 163 theme element crops and 7 cursors of theme_g3.thm.</item>
    /// <item><c>Uss/G3Theme.uss</c> — generated widget-part classes with per-state tints (<see cref="ThemeUssWriter"/>).</item>
    /// <item><c>Fonts/Gothic3.ttf</c> + <c>Gothic3 SDF.asset</c> — the game's font as a dynamic UI Toolkit SDF asset (template placeholders removed from the cmap); the SDF asset is built from the tracked <c>Assets/G3/Fonts/Gothic3_Cyr.ttf</c> (with Cyrillic) when it exists.</item>
    /// <item><c>Resources/G3Gui/</c> — <c>G3GuiData.json</c> (theme, image index, 62 screens), <c>G3Skin.asset</c>
    /// and one <see cref="G3ImageLibrary"/> per group.</item>
    /// </list>
    /// Re-running only rewrites files whose content changed.
    /// </summary>
    public static class G3GuiImporter
    {
        public const string Root = "Assets/G3Imported/Gui";
        const string ResourcesDir = Root + "/Resources/G3Gui";
        const string ThemeDir = Root + "/Theme";
        const string ImagesDir = Root + "/Images";
        /// <summary>map-hd: the 4x map pictures as one sprite per file (<see cref="HdImages.ResourceFolder"/>).</summary>
        public const string HdDir = ResourcesDir + "/ImagesHD";
        const string FontDir = Root + "/Fonts";
        const string UssPath = Root + "/Uss/G3Theme.uss";
        const string DataPath = ResourcesDir + "/G3GuiData.json";
        const string SkinPath = ResourcesDir + "/G3Skin.asset";
        const string FontAssetPath = FontDir + "/Gothic3 SDF.asset";

        public static string DataDir
        {
            get => G3.Formats.G3Install.Pref(EditorPrefs.GetString("G3.DataDir", "")) ?? G3.Formats.G3Install.DataDirOrHint;
            set => EditorPrefs.SetString("G3.DataDir", value);
        }

        public static string ResultFile => Path.GetFullPath(Path.Combine(Application.dataPath, "../Logs/g3gui_import.txt"));

        [MenuItem("G3/GUI/Import")]
        static void ImportMenu() => Debug.Log(Import());

        /// <summary>For the CLI (eval has a 5 s limit): runs <see cref="Import"/> on the next editor tick, result in Logs/g3gui_import.txt.</summary>
        public static string Schedule()
        {
            File.WriteAllText(ResultFile, "running");
            EditorApplication.delayCall += () =>
            {
                string result;
                try { result = "done\n" + Import(); }
                catch (Exception e) { result = "error\n" + e; }
                File.WriteAllText(ResultFile, result);
            };
            return "scheduled; see " + ResultFile;
        }

        public static string Import(string dataDir = null)
        {
            dataDir ??= DataDir;
            var sw = Stopwatch.StartNew();
            var log = new StringBuilder();
            using var fs = new G3FileSystem(dataDir, "gui", "Library");
            byte[] Need(string f) => (fs.GetByName(f) ?? throw new FileNotFoundException($"{f} not found in gui.pak/Library.pak under {dataDir}")).Read();
            var res = GuiResource.Read(Need("g3.gui.res"));
            var theme = GuiTheme.Read(Need("theme_g3.thm"));
            var igl = ImageGroupList.Read(Need("g3.igl"));

            var atlases = new Dictionary<string, (byte[] rgba, int w, int h)>(StringComparer.OrdinalIgnoreCase);
            (byte[] rgba, int w, int h) Atlas(string texture)
            {
                string f = Path.GetFileName(texture.Replace('\\', '/'));
                if (!atlases.TryGetValue(f, out var a))
                {
                    var dds = DdsImage.Read(Need(f));
                    a = (dds.ToRgba(), dds.Width, dds.Height);
                    atlases[f] = a;
                }
                return a;
            }

            int written = 0, unchanged = 0;
            void Png(string assetPath, byte[] rgba, int w, int h)
            {
                if (WritePng(assetPath, rgba, w, h)) written++; else unchanged++;
            }

            // 1. packed images, one PNG each
            var imageFiles = new List<(string key, string group, string path)>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var img in igl.AllImages)
            {
                string key = GuiSkinCatalog.ImageKey(img.FileName);
                if (!seen.Add(key)) continue;
                var a = Atlas(img.Texture);
                string path = $"{ImagesDir}/{img.Group.Name}/{Path.GetFileNameWithoutExtension(img.FileName)}.png";
                Png(path, DdsImage.Crop(a.rgba, a.w, img.Rect), img.Rect.Width, img.Rect.Height);
                imageFiles.Add((key, img.Group.Name, path));
            }

            // 1b. map-hd: the 4x copies of the map pictures, when made (Tools/Remaster/maphd.py)
            log.AppendLine(CopyHd(imageFiles.Select(f => (f.group, f.path))));

            // 2. theme crops + frame detection for 9-slicing
            var slices = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
            var themeFiles = new List<(string key, string path)>();
            foreach (var e in theme.Elements)
            {
                var a = Atlas(e.Texture);
                var crop = DdsImage.Crop(a.rgba, a.w, e.Rect);
                if (GuiImageAnalysis.TryDetectFrame(crop, e.Rect.Width, e.Rect.Height, out int l, out int t, out int r, out int b))
                    slices[e.Name] = new[] { l, t, r, b };
                if (ThemeUssWriter.IsTintMask(e.Name)) GuiImageAnalysis.TryWhitenAlphaMask(crop);
                string path = $"{ThemeDir}/{e.Name}.png";
                Png(path, crop, e.Rect.Width, e.Rect.Height);
                themeFiles.Add((e.Name.ToLowerInvariant(), path));
            }
            foreach (var c in theme.Cursors)
            {
                var a = Atlas(c.Texture);
                string path = $"{ThemeDir}/cursor_{c.Name}.png";
                Png(path, DdsImage.Crop(a.rgba, a.w, c.Rect), c.Rect.Width, c.Rect.Height);
                themeFiles.Add(("cursor_" + c.Name.ToLowerInvariant(), path));
            }

            // 3. data + generated USS
            var data = GuiSkinDataBuilder.Build(res, theme, igl, slices);
            WriteText(DataPath, GuiSkinJson.Write(data));
            WriteText(UssPath, ThemeUssWriter.Write(data.Theme, ThemeUrl));

            // 4. font
            string ttf = Path.Combine(Path.GetDirectoryName(dataDir.TrimEnd('\\', '/')) ?? dataDir, "Gothic3.ttf");
            string ttfAsset = FontDir + "/Gothic3.ttf";
            bool hasFont = File.Exists(ttf);
            // The tracked Gothic3 + Cyrillic font (Tools/Fonts/g3cyrillic.py) wins; without it the install's Gothic3.ttf
            // is copied Latin-only and Russian comes from the fallback fonts.
            bool cyrillic = File.Exists(Full(CyrillicFontPath));
            bool fontChanged = !cyrillic && hasFont &&
                               WriteIfChanged(ttfAsset, TrueTypeCmapFilter.RemoveCodePoints(File.ReadAllBytes(ttf), IsBrokenGlyph));
            if (cyrillic) log.AppendLine($"font: {CyrillicFontPath} (Gothic3 + Cyrillic)");
            else if (!hasFont) log.AppendLine($"Gothic3.ttf not found at {ttf}: the skin falls back to the default UI font");

            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

            // 5. libraries and the skin entry asset
            foreach (var group in imageFiles.GroupBy(f => f.group))
                SaveLibrary(group.Key, group.Select(f => (f.key, f.path)).ToList());
            SaveLibrary(G3Skin.ThemeGroup, themeFiles);

            var font = cyrillic ? AssetDatabase.LoadAssetAtPath<Font>(CyrillicFontPath)
                     : hasFont ? AssetDatabase.LoadAssetAtPath<Font>(ttfAsset) : null;
            var fontAsset = font != null ? EnsureFontAsset(font, fontChanged) : null;
            var skin = AssetDatabase.LoadAssetAtPath<G3SkinAssets>(SkinPath);
            if (skin == null)
            {
                skin = ScriptableObject.CreateInstance<G3SkinAssets>();
                AssetDatabase.CreateAsset(skin, SkinPath);
            }
            skin.data = AssetDatabase.LoadAssetAtPath<UnityEngine.TextAsset>(DataPath);
            skin.theme = AssetDatabase.LoadAssetAtPath<UnityEngine.UIElements.StyleSheet>(UssPath);
            skin.font = font;
            skin.fontAsset = fontAsset;
            EditorUtility.SetDirty(skin);
            AssetDatabase.SaveAssets();
            G3Skin.Reset();

            log.Insert(0, $"G3 GUI import: {imageFiles.Count} images in {imageFiles.Select(f => f.group).Distinct().Count()} groups, " +
                          $"{theme.Elements.Count} theme elements ({slices.Count} 9-sliced frames), {theme.Cursors.Count} cursors, " +
                          $"{data.Screens.Count} screens; PNG written {written}, unchanged {unchanged}; " +
                          $"font {(fontAsset != null ? "Gothic3 SDF" : font != null ? "Gothic3.ttf only" : "missing")}; {sw.Elapsed.TotalSeconds:F1} s\n");
            return log.ToString();
        }

        [MenuItem("G3/GUI/Import HD maps")]
        static void ImportHdMenu() => Debug.Log(ImportHd());

        /// <summary>map-hd: only the HD map step (no game data needed): copies GameData/Remaster/Maps into the import.</summary>
        public static string ImportHd()
        {
            var images = Directory.Exists(Full(ImagesDir))
                ? Directory.GetFiles(Full(ImagesDir), "*.png", SearchOption.AllDirectories)
                    .Select(f => (Path.GetFileName(Path.GetDirectoryName(f)), f)).ToList()
                : new List<(string, string)>();
            string result = CopyHd(images);
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            G3Skin.Reset();
            return result;
        }

        /// <summary>
        /// Copies <c>GameData/Remaster/Maps/&lt;stem&gt;.png</c> of every wanted picture (<see cref="HdImages.Wanted"/>) that
        /// is exactly 4x its imported original to <see cref="HdDir"/>/&lt;key&gt;.png; removes stale copies.
        /// </summary>
        static string CopyHd(IEnumerable<(string group, string path)> images)
        {
            string project = Full("");
            string src = G3.Formats.Remaster.RemasterPaths.MapsDir(project);
            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int copied = 0, wanted = 0;
            var skipped = new List<string>();
            foreach (var (group, path) in images)
            {
                string file = Path.GetFileName(path);
                if (!HdImages.Wanted(group, file)) continue;
                wanted++;
                string hd = Path.Combine(src, Path.GetFileNameWithoutExtension(file) + ".png");
                if (!File.Exists(hd)) { skipped.Add(file + " (none)"); continue; }
                var (w, h) = PngSize(File.ReadAllBytes(Full(path)));
                byte[] bytes = File.ReadAllBytes(hd);
                var (hw, hh) = PngSize(bytes);
                if (!HdImages.SizeMatches(w, h, hw, hh)) { skipped.Add($"{file} ({hw}x{hh} is not 4x {w}x{h})"); continue; }
                string target = $"{HdDir}/{GuiSkinCatalog.ImageKey(file)}.png";
                keep.Add(Path.GetFullPath(Full(target)));
                WriteIfChanged(target, bytes);
                copied++;
            }
            if (Directory.Exists(Full(HdDir)))
                foreach (var f in Directory.GetFiles(Full(HdDir), "*.png"))
                    if (!keep.Contains(Path.GetFullPath(f))) { File.Delete(f); if (File.Exists(f + ".meta")) File.Delete(f + ".meta"); }
            return $"HD maps: {copied}/{wanted} from {src}" + (skipped.Count > 0 ? "; skipped " + string.Join(", ", skipped) : "");
        }

        /// <summary>Width and height from a PNG's IHDR.</summary>
        static (int w, int h) PngSize(byte[] png)
        {
            if (png == null || png.Length < 24) return (0, 0);
            int Be(int o) => (png[o] << 24) | (png[o + 1] << 16) | (png[o + 2] << 8) | png[o + 3];
            return (Be(16), Be(20));
        }

        static string ThemeUrl(string imageKey)
        {
            string name = imageKey.StartsWith(GuiSkinDataBuilder.ThemeImagePrefix, StringComparison.Ordinal)
                ? imageKey.Substring(GuiSkinDataBuilder.ThemeImagePrefix.Length) : imageKey;
            return "/" + ThemeDir + "/" + name + ".png";
        }

        static void SaveLibrary(string group, List<(string key, string path)> files)
        {
            string path = $"{ResourcesDir}/Images/{group}.asset";
            var lib = AssetDatabase.LoadAssetAtPath<G3ImageLibrary>(path);
            if (lib == null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                lib = ScriptableObject.CreateInstance<G3ImageLibrary>();
                AssetDatabase.CreateAsset(lib, path);
            }
            lib.group = group;
            lib.keys = files.Select(f => f.key).ToArray();
            lib.sprites = files.Select(f => AssetDatabase.LoadAssetAtPath<Sprite>(f.path)).ToArray();
            int missing = lib.sprites.Count(s => s == null);
            if (missing > 0) Debug.LogWarning($"G3 GUI import: {missing} sprites of group {group} did not import");
            EditorUtility.SetDirty(lib);
        }

        /// <summary>
        /// Gothic3.ttf has real Gothic3 outlines only for ASCII and part of Latin-1; the rest are placeholders of the font
        /// editor's template: Cyrillic as Greek-like shapes (likely why CP 1.75 defaults to another face), dashes, quotes,
        /// №, € and many Latin-1/Latin Extended letters as unrelated glyphs. The imported copy keeps only
        /// <see cref="G3FontMetrics.IsTrustedGlyph"/> code points in its cmap, so everything else comes from the fallback
        /// fonts (<see cref="G3Skin.FallbackFamilies"/>) like Russian in the original engine.
        /// </summary>
        static bool IsBrokenGlyph(int codePoint) => !G3FontMetrics.IsTrustedGlyph(codePoint);

        /// <summary>
        /// Gothic3 with a Cyrillic alphabet, №, « » and dashes composed from its own Latin outlines by
        /// <c>Tools/Fonts/g3cyrillic.py</c>; its cmap already holds only
        /// <see cref="G3FontMetrics.IsTrustedGlyph(int, bool)"/> code points. Game-derived, but kept in git by the owner's
        /// decision (Docs/ui-skin.md "Fonts"), so the import does not need Python.
        /// </summary>
        public const string CyrillicFontPath = G3FontMap.PortFontPath;

        /// <summary>
        /// Points the skin at a font asset built from <see cref="CyrillicFontPath"/> when it is not (a GUI import that ran
        /// before the font existed left the Latin-only Gothic3.ttf as the SDF source, so all Russian text fell back to
        /// Arial/Tahoma). Only the font asset is rebuilt; the rest of the import is not needed. True when it changed
        /// anything. Runs on every editor load (<see cref="G3GuiFontCheck"/>) and before player builds.
        /// </summary>
        public static bool RepairFontAsset()
        {
            var skin = AssetDatabase.LoadAssetAtPath<G3SkinAssets>(SkinPath);
            var font = AssetDatabase.LoadAssetAtPath<Font>(CyrillicFontPath);
            if (skin == null || font == null) return false;
            var asset = skin.fontAsset;
            if (asset != null && skin.font == font && asset.sourceFontFile == font &&
                asset.atlasPopulationMode == AtlasPopulationMode.Dynamic && asset.atlasPadding == FontAtlasPadding) return false;
            var fontAsset = EnsureFontAsset(font, false);
            if (fontAsset == null) return false;
            skin.font = font;
            skin.fontAsset = fontAsset;
            EditorUtility.SetDirty(skin);
            AssetDatabase.SaveAssets();
            G3Skin.Reset();
            Debug.Log($"G3 GUI: font asset rebuilt from {CyrillicFontPath} (was {(asset != null && asset.sourceFontFile != null ? AssetDatabase.GetAssetPath(asset.sourceFontFile) : "missing")}).");
            return true;
        }

        /// <summary>
        /// SDF spread of the font atlas in pixels at the 48 pt sampling size. The 6 px default showed the glyph quads as
        /// faint boxes once the synthetic bold and the 1 px focus-name outline reached beyond it at small sizes.
        /// </summary>
        const int FontAtlasPadding = 12;

        /// <summary>
        /// Dynamic SDF font asset of the imported (Latin-only) Gothic3.ttf. Dynamic because UI Toolkit's advanced text
        /// generator cannot render static font assets; recreated when the TTF changed or an older static asset exists.
        /// </summary>
        static FontAsset EnsureFontAsset(Font font, bool fontChanged)
        {
            var existing = AssetDatabase.LoadAssetAtPath<FontAsset>(FontAssetPath);
            if (existing != null && !fontChanged && existing.sourceFontFile == font && existing.atlasPopulationMode == AtlasPopulationMode.Dynamic &&
                existing.atlasPadding == FontAtlasPadding) return existing;
            if (existing != null) AssetDatabase.DeleteAsset(FontAssetPath);

            var fa = FontAsset.CreateFontAsset(font, 48, FontAtlasPadding, GlyphRenderMode.SDFAA, 1024, 1024, AtlasPopulationMode.Dynamic, true);
            if (fa == null) return null;
            fa.name = "Gothic3 SDF";
            // a fresh checkout with the tracked Cyrillic font never copies Gothic3.ttf, so Fonts/ may not exist yet
            if (!AssetDatabase.IsValidFolder(FontDir)) AssetDatabase.CreateFolder(Root, "Fonts");
            AssetDatabase.CreateAsset(fa, FontAssetPath);
            for (int i = 0; i < fa.atlasTextures.Length; i++)
            {
                if (fa.atlasTextures[i] == null) continue;
                fa.atlasTextures[i].name = $"Gothic3 SDF Atlas {i}";
                AssetDatabase.AddObjectToAsset(fa.atlasTextures[i], fa);
            }
            if (fa.material != null)
            {
                fa.material.name = "Gothic3 SDF Material";
                AssetDatabase.AddObjectToAsset(fa.material, fa);
            }
            EditorUtility.SetDirty(fa);
            return fa;
        }

        // ---- file helpers (no AssetDatabase calls: everything is imported in one Refresh) ----

        static string Full(string assetPath) => Path.GetFullPath(Path.Combine(Application.dataPath, "..", assetPath));

        /// <summary>Writes a PNG (RGBA32, top row first) if its bytes differ from the file on disk.</summary>
        static bool WritePng(string assetPath, byte[] rgbaTopDown, int w, int h)
        {
            var flipped = new byte[rgbaTopDown.Length];
            int row = w * 4;
            for (int y = 0; y < h; y++) Buffer.BlockCopy(rgbaTopDown, y * row, flipped, (h - 1 - y) * row, row);
            byte[] png = ImageConversion.EncodeArrayToPNG(flipped, GraphicsFormat.R8G8B8A8_UNorm, (uint)w, (uint)h);
            return WriteIfChanged(assetPath, png);
        }

        static void WriteText(string assetPath, string text) => WriteIfChanged(assetPath, new UTF8Encoding(false).GetBytes(text));


        static bool WriteIfChanged(string assetPath, byte[] bytes)
        {
            string full = Full(assetPath);
            if (File.Exists(full) && File.ReadAllBytes(full).AsSpan().SequenceEqual(bytes)) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, bytes);
            return true;
        }
    }

    /// <summary>Repairs a stale GUI font asset once per editor load (<see cref="G3GuiImporter.RepairFontAsset"/>).</summary>
    [InitializeOnLoad]
    static class G3GuiFontCheck
    {
        static G3GuiFontCheck() => EditorApplication.delayCall += Run;

        static void Run()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                EditorApplication.delayCall += Run;
                return;
            }
            try { G3GuiImporter.RepairFontAsset(); }
            catch (Exception e) { Debug.LogWarning("G3 GUI font check: " + e.Message); }
        }
    }

    /// <summary>Player builds never ship the Latin-only GUI font (<see cref="G3GuiImporter.RepairFontAsset"/>).</summary>
    sealed class G3GuiFontBuildCheck : UnityEditor.Build.IPreprocessBuildWithReport
    {
        public int callbackOrder => 0;
        public void OnPreprocessBuild(UnityEditor.Build.Reporting.BuildReport report) => G3GuiImporter.RepairFontAsset();
    }

    /// <summary>Import settings of everything under Assets/G3Imported/Gui: UI sprites without mipmaps; small icons stay
    /// uncompressed so UI Toolkit's dynamic atlas can batch them, large backgrounds are BC7-compressed.</summary>
    sealed class G3GuiTexturePostprocessor : AssetPostprocessor
    {
        const int UncompressedMax = 128;

        void OnPreprocessTexture()
        {
            if (!assetPath.StartsWith(G3GuiImporter.Root + "/", StringComparison.Ordinal)) return;
            if (assetPath.Contains("/Fonts/")) return;
            var ti = (TextureImporter)assetImporter;
            if (assetPath.StartsWith(G3GuiImporter.HdDir + "/", StringComparison.Ordinal))
            {
                // map-hd: zoomable pictures, minified up to 4x at zoom 1: mipmaps (trilinear), BC7, up to 4096
                ti.textureType = TextureImporterType.Sprite;
                ti.spriteImportMode = SpriteImportMode.Single;
                var settings = new TextureImporterSettings();
                ti.ReadTextureSettings(settings);
                settings.spriteMeshType = SpriteMeshType.FullRect;
                ti.SetTextureSettings(settings);
                ti.mipmapEnabled = true;
                ti.mipmapFilter = TextureImporterMipFilter.KaiserFilter;
                ti.streamingMipmaps = false;
                ti.alphaIsTransparency = true;
                ti.sRGBTexture = true;
                ti.wrapMode = TextureWrapMode.Clamp;
                ti.filterMode = FilterMode.Trilinear;
                ti.anisoLevel = 1;
                ti.npotScale = TextureImporterNPOTScale.None;
                ti.maxTextureSize = 4096;
                ti.spritePixelsPerUnit = 100;
                ti.textureCompression = TextureImporterCompression.CompressedHQ;   // BC7 on desktop
                ti.compressionQuality = 100;
                return;
            }
            ti.textureType = TextureImporterType.Sprite;
            ti.spriteImportMode = SpriteImportMode.Single;
            ti.mipmapEnabled = false;
            ti.alphaIsTransparency = true;
            ti.sRGBTexture = true;
            ti.wrapMode = TextureWrapMode.Clamp;
            ti.filterMode = FilterMode.Bilinear;
            ti.npotScale = TextureImporterNPOTScale.None;
            ti.maxTextureSize = 2048;
            ti.spritePixelsPerUnit = 100;
            ti.GetSourceTextureWidthAndHeight(out int w, out int h);
            ti.textureCompression = w <= UncompressedMax && h <= UncompressedMax
                ? TextureImporterCompression.Uncompressed
                : TextureImporterCompression.CompressedHQ;
        }
    }
}
