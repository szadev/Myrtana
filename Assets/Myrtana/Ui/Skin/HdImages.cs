using System;
using System.Collections.Generic;

namespace Myrtana.Ui.Skin
{
    /// <summary>
    /// map-hd: 4× copies of the G3 map pictures for the zoomable map screens (Docs/map-teleport.md "Map zoom"). The upscale
    /// (Tools/Remaster/maphd.py, 4x-UltraSharp, colour-locked to the original) writes <c>GameData/Remaster/Maps/&lt;stem&gt;.png</c>;
    /// the GUI importer copies them as sprites to <c>Resources/G3Gui/ImagesHD/&lt;key&gt;.png</c> (game-derived, git-ignored) and
    /// <c>G3Skin.Sprite</c> returns the HD sprite for the same image name when one exists and the switch is on. The picture
    /// is the same (same aspect, same UVs, element sizes come from the image index), so every marker stays where it was.
    /// The switch is on by default (a pure resolution upgrade of the same painting, no new content): PlayerPrefs
    /// <see cref="PrefKey"/>, overridden by <see cref="EnvVar"/> then <see cref="SharedEnvVar"/> (<c>1/true/on</c>, <c>0/false/off</c>).
    /// </summary>
    public static class HdImages
    {
        public const string ResourceFolder = "G3Gui/ImagesHD/";
        public const string PrefKey = "G3.Remaster.MapsHd";
        public const string EnvVar = "G3_REMASTER_MAPS";
        public const string SharedEnvVar = "G3_REMASTER_RUNTIME";
        public const bool Default = true;
        public const int Scale = 4;

        /// <summary>Pictures that get an HD copy: every hud_maps image and the quest log's world map.</summary>
        public static readonly string[] Groups = { "hud_maps" };
        public static readonly string[] Extra = { "G3_HUD_View_World_Back" };

        /// <summary>Whether an imported image (group, file name) is one of the HD pictures.</summary>
        public static bool Wanted(string group, string fileName)
        {
            foreach (var g in Groups)
                if (string.Equals(g, group, StringComparison.OrdinalIgnoreCase)) return true;
            string key = GuiSkinCatalog.ImageKey(fileName);
            foreach (var e in Extra)
                if (GuiSkinCatalog.ImageKey(e) == key) return true;
            return false;
        }

        /// <summary>Resources path of the HD copy of an image (by name or key): <c>G3Gui/ImagesHD/g3_map_world</c>.</summary>
        public static string ResourcePath(string imageName) => ResourceFolder + GuiSkinCatalog.ImageKey(imageName);

        /// <summary><c>1/true/on/yes</c> → true, <c>0/false/off/no</c> → false, anything else → null.</summary>
        public static bool? ParseFlag(string v)
        {
            if (string.IsNullOrWhiteSpace(v)) return null;
            switch (v.Trim().ToLowerInvariant())
            {
                case "1": case "true": case "on": case "yes": return true;
                case "0": case "false": case "off": case "no": return false;
                default: return null;
            }
        }

        /// <summary>Own variable, then the shared runtime one, then the stored choice (null: <see cref="Default"/>).</summary>
        public static bool Resolve(string ownEnv, string sharedEnv, int? stored) =>
            ParseFlag(ownEnv) ?? ParseFlag(sharedEnv) ?? (stored.HasValue ? stored.Value != 0 : Default);

        /// <summary>The HD size of a picture: exactly <see cref="Scale"/>× the original, so UVs and aspect are unchanged.</summary>
        public static bool SizeMatches(int w, int h, int hdW, int hdH) => hdW == w * Scale && hdH == h * Scale;
    }
}
