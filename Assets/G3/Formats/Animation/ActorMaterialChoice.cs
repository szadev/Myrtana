using System;
using System.Text.RegularExpressions;

namespace G3.Formats.Animation
{
    /// <summary>
    /// Which material an actor (.xact) material slot gets: the shared .xshmat it references, or one built from the FXA
    /// material chunk's own texture layers. Used by the Unity character importer and its pure port (SourceMaterials).
    /// </summary>
    public static class ActorMaterialChoice
    {
        static readonly Regex Suffix = new Regex(@"^(.*?)(_S\d+|_\d+)$", RegexOptions.IgnoreCase);

        /// <summary>
        /// True when the shared .xshmat wins over the FXA diffuse layer:
        /// <list type="bullet">
        /// <item>no layer, or the layer names the same image (bodies, monsters);</item>
        /// <item>the layer's image is not shipped (GFG_Hero_Body_King, G3_Hero_Body_NomadNovice);</item>
        /// <item>fix-237: the layer names the plain numbered source image <c>X_01</c> and the .xshmat its shipped switch
        /// variant <c>X_S1</c> of the same base (G3_Stalker_Body_01: layer G3_Monster_Stalker_Body_Diffuse_01, a flat navy
        /// leftover; the game draws the .xshmat's rotting-skin G3_Monster_Stalker_Body_Diffuse_S1). Bows/crossbows match too
        /// (their _01 and _S1 are the same image).</item>
        /// </list>
        /// Heads keep their FXA layers: they reference generic FACE/HAIR/Eye materials of a different base.
        /// </summary>
        public static bool UseShared(string sharedDiffuse, string layerDiffuse, Func<string, bool> layerShipped)
        {
            if (layerDiffuse == null || string.Equals(sharedDiffuse, layerDiffuse, StringComparison.OrdinalIgnoreCase)) return true;
            if (layerShipped != null && !layerShipped(layerDiffuse)) return true;
            return IsSwitchOfNumbered(sharedDiffuse, layerDiffuse);
        }

        /// <summary><paramref name="shared"/> = <c>base_S&lt;n&gt;</c> and <paramref name="layer"/> = <c>base_&lt;nn&gt;</c>.</summary>
        public static bool IsSwitchOfNumbered(string shared, string layer)
        {
            if (shared == null || layer == null) return false;
            var s = Suffix.Match(shared);
            var l = Suffix.Match(layer);
            if (!s.Success || !l.Success) return false;
            if (!s.Groups[2].Value.StartsWith("_S", StringComparison.OrdinalIgnoreCase)) return false;
            if (l.Groups[2].Value.StartsWith("_S", StringComparison.OrdinalIgnoreCase)) return false;
            return string.Equals(s.Groups[1].Value, l.Groups[1].Value, StringComparison.OrdinalIgnoreCase);
        }
    }
}
