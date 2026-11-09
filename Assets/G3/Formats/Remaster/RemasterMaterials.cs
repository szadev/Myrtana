using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace G3.Formats.Remaster
{
    /// <summary>Which R1 shader a character material gets (Docs/remaster-pipeline.md §9).</summary>
    public enum RemasterMaterialKind { Armour, Skin, Hair, Eye, Lash }

    /// <summary>
    /// Material classification by name (pure, shared by the texture pipeline listing in G3Cli and the Unity override).
    /// G3 character material/texture names are regular: <c>G3_Animation_Head_Eyes_01</c>, <c>…_Hair_…</c>,
    /// <c>…_Beard_…</c>, <c>…_Eyelashes…</c>, heads <c>G3_Animation_Head_Main_01</c> / <c>G3_Hero_Head_…</c> / <c>…_Face_…</c>;
    /// everything else (bodies = armour with hands and neck) is <see cref="RemasterMaterialKind.Armour"/>, whose skin parts
    /// the texture pipeline marks per texel (MaskMap B).
    /// </summary>
    public static class RemasterMaterialKinds
    {
        static readonly (Regex re, RemasterMaterialKind kind)[] Rules =
        {
            (new Regex(@"lash", RegexOptions.IgnoreCase), RemasterMaterialKind.Lash),
            (new Regex(@"eye(?!brow)", RegexOptions.IgnoreCase), RemasterMaterialKind.Eye),
            (new Regex(@"hair|beard|brow|mustache|moustache|stubble", RegexOptions.IgnoreCase), RemasterMaterialKind.Hair),
            (new Regex(@"head|face|skin|teeth|tongue|mouth", RegexOptions.IgnoreCase), RemasterMaterialKind.Skin),
        };

        /// <summary>
        /// First rule (lash, eye, hair, skin) any of the names matches: material, diffuse image, specular image (the hero's
        /// eye material <c>G3_Human_HERO_Face_Diffuse_01_B</c> is only recognisable by its <c>…_Eyes_01_Specular_01</c>); default armour.
        /// </summary>
        public static RemasterMaterialKind Classify(string materialName, string diffuseImage = null, string specularImage = null)
        {
            foreach (var (re, kind) in Rules)
                foreach (var name in new[] { materialName, diffuseImage, specularImage })
                    if (!string.IsNullOrEmpty(name) && re.IsMatch(name)) return kind;
            return RemasterMaterialKind.Armour;
        }

        public static string Name(RemasterMaterialKind k) => k.ToString().ToLowerInvariant();

        public static bool TryParse(string s, out RemasterMaterialKind kind) => Enum.TryParse(s, true, out kind);
    }

    /// <summary>
    /// Output of the R1 texture pipeline (<c>Tools/Remaster/texpipe.py</c>) for one source image stem:
    /// <c>GameData/Remaster/Textures/&lt;stem&gt;/{BaseMap,BumpMap,MaskMap}.png</c> (+ <c>info.json</c>). The stem is the
    /// original diffuse image (switch variant included, e.g. <c>G3_Human_Paladin_Body_Diffuse_S2</c>), so a material is
    /// matched by the texture it already shows.
    /// </summary>
    public static class RemasterTextures
    {
        public const string BaseMap = "BaseMap.png";   // sRGB albedo (alpha = original alpha / cutout)
        public const string BumpMap = "BumpMap.png";   // linear tangent-space normal, Unity (Y up) convention
        public const string MaskMap = "MaskMap.png";   // linear: R metallic, G occlusion, B skin weight, A smoothness
        /// <summary>R2 (Tools/Blender/remaster/pipeline.py --mode r2): BumpMap with the baked relief on top, same UVs and
        /// convention; used instead of <see cref="BumpMap"/> when present (returned under the BumpMap key).</summary>
        public const string BumpMapR2 = "BumpMap_R2.png";
        /// <summary>R2b (pipeline.py --mode r2b, Tools/Blender/remaster/r2bmat.py): MaskMap with the AO bake in G, metal
        /// fixed in plate regions, metal/leather smoothness; used instead of <see cref="MaskMap"/> when present.</summary>
        public const string MaskMapR2 = "MaskMap_R2.png";
        /// <summary>R2c (Tools/Blender/remaster/r2cmat.py): the armour/clothes re-surfaced with CC0 material detail
        /// (albedo, normal, mask; same UVs and conventions, usually 4096 px); each replaces the R2/R1 map under its key.</summary>
        public const string BaseMapR2c = "BaseMap_R2c.png";
        public const string BumpMapR2c = "BumpMap_R2c.png";
        public const string MaskMapR2c = "MaskMap_R2c.png";
        /// <summary>Local-AI detail pass (Tools/Remaster/ai/char_ai.py, Docs/remaster-characters.md §6): R1's albedo with
        /// SDXL ControlNet-tile micro detail locked to G3's colour, normal/mask re-derived; same UVs and row order. Each
        /// replaces every other map under its key (R1, R2, R2c) unless <see cref="AiVariable"/> is "0".</summary>
        public const string BaseMapAI = "BaseMap_AI.png";
        public const string BumpMapAI = "BumpMap_AI.png";
        public const string MaskMapAI = "MaskMap_AI.png";
        /// <summary>Environment switch for the AI maps (A|B of the AI pass alone): "0" = ignore them.</summary>
        public const string AiVariable = "G3_REMASTER_AI";
        public const string Info = "info.json";

        /// <summary>True unless the environment turns the AI maps off.</summary>
        public static bool UseAi => System.Environment.GetEnvironmentVariable(AiVariable) != "0";

        public static string Dir(string projectDirectory) => Path.Combine(RemasterPaths.Root(projectDirectory), "Textures");
        public static string SetDir(string projectDirectory, string imageStem) => Path.Combine(Dir(projectDirectory), imageStem);

        /// <summary>The image stem behind a Unity texture asset name (drops the importer's <c>_n</c> normal suffix).</summary>
        public static string StemOf(string textureName)
        {
            if (string.IsNullOrEmpty(textureName)) return null;
            string s = Path.GetFileNameWithoutExtension(textureName);
            return s.EndsWith("_n", StringComparison.Ordinal) ? s.Substring(0, s.Length - 2) : s;
        }

        /// <summary>Maps present for an image stem (empty when the pipeline has not produced it); the R2 normal map
        /// replaces R1's under the <see cref="BumpMap"/> key, the R2b mask map R1's under <see cref="MaskMap"/>, and the
        /// R2c maps (<see cref="BaseMapR2c"/> etc.) replace both under their keys; the AI maps (<see cref="BaseMapAI"/>
        /// etc.) replace all of them unless <see cref="UseAi"/> is false.</summary>
        public static Dictionary<string, string> Find(string projectDirectory, string imageStem)
        {
            var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(imageStem)) return found;
            string dir = SetDir(projectDirectory, imageStem);
            if (!Directory.Exists(dir)) return found;
            foreach (var f in new[] { BaseMap, BumpMap, MaskMap })
            {
                string p = Path.Combine(dir, f);
                if (File.Exists(p)) found[f] = p;
            }
            string r2 = Path.Combine(dir, BumpMapR2);
            if (File.Exists(r2)) found[BumpMap] = r2;
            string m2 = Path.Combine(dir, MaskMapR2);
            if (File.Exists(m2)) found[MaskMap] = m2;
            foreach (var (key, file) in new[] { (BaseMap, BaseMapR2c), (BumpMap, BumpMapR2c), (MaskMap, MaskMapR2c) })
            {
                string p = Path.Combine(dir, file);
                if (File.Exists(p)) found[key] = p;
            }
            if (UseAi)
                foreach (var (key, file) in new[] { (BaseMap, BaseMapAI), (BumpMap, BumpMapAI), (MaskMap, MaskMapAI) })
                {
                    string p = Path.Combine(dir, file);
                    if (File.Exists(p)) found[key] = p;
                }
            return found;
        }
    }
}
