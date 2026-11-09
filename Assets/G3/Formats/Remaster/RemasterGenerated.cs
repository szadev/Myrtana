using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace G3.Formats.Remaster
{
    /// <summary>What a material made by the Blender face pipeline is (it has no G3 original to follow).</summary>
    public enum RemasterGeneratedKind
    {
        None, Eye, Cornea, HairCards, HairCap,
        // v2 hero showcase (Docs/remaster-characters.md "v2 hero showcase"): G3_Remaster_V2_<Kind>__<tag>
        SkinV2, EyeV2, HairV2, Stubble, EyeOcclusion, TearFilm, Surface,
    }

    /// <summary>
    /// Materials the FACES pipeline (Tools/Blender/remaster/mhface.py, eyegen.py, hairgen.py) adds to remastered heads:
    /// <list type="bullet">
    /// <item><c>G3_Remaster_Eye__&lt;skin set&gt;_eye</c>: the generated eyeball (sclera, iris, pupil), texture set of the same name</item>
    /// <item><c>G3_Remaster_Cornea</c>: the clear cornea shell over each eye, no texture (glossy, transparent)</item>
    /// <item><c>G3_Remaster_&lt;who&gt;_HairCards</c>: alpha-tested hair cards, texture set of the same name</item>
    /// <item><c>G3_Remaster_&lt;who&gt;_HairCap</c>: the opaque dark scalp layer under the cards, texture set of the same name</item>
    /// </list>
    /// Texture sets live in <c>GameData/Remaster/Textures/&lt;set&gt;/</c> like the R1 sets (<see cref="RemasterTextures"/>).
    /// Names are matched without Blender's <c>.001</c> duplicate suffix.
    /// </summary>
    public static class RemasterGenerated
    {
        public const string Prefix = "G3_Remaster_";
        public const string EyePrefix = "G3_Remaster_Eye";
        public const string Cornea = "G3_Remaster_Cornea";
        public const string HairCardsSuffix = "_HairCards";
        public const string HairCapSuffix = "_HairCap";

        static readonly Regex BlenderDuplicate = new Regex(@"(\.\d{3})+$");

        /// <summary>The material name without Blender's <c>.001</c> suffix.</summary>
        public static string BaseName(string materialName) => BlenderDuplicate.Replace(materialName ?? "", "");

        /// <summary>v2 materials: <c>G3_Remaster_V2_&lt;Kind&gt;</c> or <c>G3_Remaster_V2_&lt;Kind&gt;__&lt;tag&gt;</c>.</summary>
        public const string V2Prefix = "G3_Remaster_V2_";

        static readonly Regex V2Pattern = new Regex(@"^G3_Remaster_V2_(Skin|Eye|Hair|Stubble|EyeOcclusion|TearFilm|Surface)(__[A-Za-z0-9_]+)?$");

        /// <summary>The v2 material name for a kind and tag (tag may be empty), e.g. <c>G3_Remaster_V2_Skin__HeroFace</c>.</summary>
        public static string V2Name(RemasterGeneratedKind kind, string tag)
        {
            string k = kind switch
            {
                RemasterGeneratedKind.SkinV2 => "Skin",
                RemasterGeneratedKind.EyeV2 => "Eye",
                RemasterGeneratedKind.HairV2 => "Hair",
                RemasterGeneratedKind.Stubble => "Stubble",
                RemasterGeneratedKind.EyeOcclusion => "EyeOcclusion",
                RemasterGeneratedKind.TearFilm => "TearFilm",
                RemasterGeneratedKind.Surface => "Surface",
                _ => throw new ArgumentException("not a v2 kind: " + kind),
            };
            return V2Prefix + k + (string.IsNullOrEmpty(tag) ? "" : "__" + tag);
        }

        /// <summary>
        /// The v2 head contract (runner-31 HV1: the G3 face atlas on a v2 head drew blotches): a mesh carrying v2 skin must
        /// not carry any other material but generated ones (no G3 / R1 face material: the v2 layout is not the G3 atlas).
        /// Returns the offending material names (empty = fine; also empty for meshes without v2 skin).
        /// </summary>
        public static List<string> V2HeadForeignMaterials(IEnumerable<string> materials)
        {
            var names = materials?.Where(n => n != null).ToList() ?? new List<string>();
            if (!names.Any(n => Classify(n) == RemasterGeneratedKind.SkinV2)) return new List<string>();
            return names.Where(n => Classify(n) == RemasterGeneratedKind.None).Distinct().ToList();
        }

        /// <summary>True for the v2 showcase kinds.</summary>
        public static bool IsV2(RemasterGeneratedKind k) => k >= RemasterGeneratedKind.SkinV2;

        public static RemasterGeneratedKind Classify(string materialName)
        {
            string n = BaseName(materialName);
            if (!n.StartsWith(Prefix, StringComparison.Ordinal)) return RemasterGeneratedKind.None;
            if (n.StartsWith(V2Prefix, StringComparison.Ordinal))
            {
                var m = V2Pattern.Match(n);
                if (!m.Success) return RemasterGeneratedKind.None;
                switch (m.Groups[1].Value)
                {
                    case "Skin": return RemasterGeneratedKind.SkinV2;
                    case "Eye": return RemasterGeneratedKind.EyeV2;
                    case "Hair": return RemasterGeneratedKind.HairV2;
                    case "Stubble": return RemasterGeneratedKind.Stubble;
                    case "EyeOcclusion": return RemasterGeneratedKind.EyeOcclusion;
                    case "TearFilm": return RemasterGeneratedKind.TearFilm;
                    default: return RemasterGeneratedKind.Surface;
                }
            }
            if (n == Cornea || n.StartsWith(Cornea + "_", StringComparison.Ordinal)) return RemasterGeneratedKind.Cornea;
            if (n.StartsWith(EyePrefix + "_", StringComparison.Ordinal) || n == EyePrefix) return RemasterGeneratedKind.Eye;
            if (n.EndsWith(HairCardsSuffix, StringComparison.Ordinal)) return RemasterGeneratedKind.HairCards;
            if (n.EndsWith(HairCapSuffix, StringComparison.Ordinal)) return RemasterGeneratedKind.HairCap;
            return RemasterGeneratedKind.None;
        }

        public static bool IsGenerated(string materialName) => Classify(materialName) != RemasterGeneratedKind.None;

        /// <summary>The texture set (folder under Textures/) of a generated material; null for the cornea and non-generated names.</summary>
        public static string TextureSet(string materialName)
        {
            var k = Classify(materialName);
            return k == RemasterGeneratedKind.None || k == RemasterGeneratedKind.Cornea || k == RemasterGeneratedKind.TearFilm
                ? null : BaseName(materialName);
        }

        static readonly Regex HairSwitch = new Regex(@"_S(\d+)(?=_Hair(Cards|Cap)$)");

        /// <summary>
        /// FACES v6: the texture set of a generated hair material for an NPC's material switch. A G3 hair/beard mesh is
        /// shared by NPCs with different switches (hair colours); hairmesh.py writes one tinted card/cap set per switch
        /// of the manifest (<c>G3_Remaster_&lt;tag&gt;_S&lt;n&gt;_HairCards</c>). Switch 0 is the mesh's own (S1) texture,
        /// like G3's base material. Falls back to the material's own set when that switch has none.
        /// </summary>
        public static string SwitchTextureSet(string materialName, int materialSwitch, Func<string, bool> exists)
        {
            string set = TextureSet(materialName);
            var k = Classify(materialName);
            if (set == null || (k != RemasterGeneratedKind.HairCards && k != RemasterGeneratedKind.HairCap)) return set;
            var m = HairSwitch.Match(set);
            if (!m.Success) return set;
            string alt = set.Substring(0, m.Index) + "_S" + Math.Max(1, materialSwitch) + set.Substring(m.Index + m.Length);
            return alt != set && exists != null && exists(alt) ? alt : set;
        }

        /// <summary>The R1 shader family a generated material renders with (the cornea has its own shader).</summary>
        public static RemasterMaterialKind? ShaderKind(RemasterGeneratedKind k) => k switch
        {
            RemasterGeneratedKind.Eye => RemasterMaterialKind.Eye,
            RemasterGeneratedKind.HairCards => RemasterMaterialKind.Hair,
            RemasterGeneratedKind.HairCap => RemasterMaterialKind.Hair,
            RemasterGeneratedKind.SkinV2 => RemasterMaterialKind.Skin,
            RemasterGeneratedKind.EyeV2 => RemasterMaterialKind.Eye,
            RemasterGeneratedKind.HairV2 => RemasterMaterialKind.Hair,
            RemasterGeneratedKind.Stubble => RemasterMaterialKind.Hair,
            RemasterGeneratedKind.Surface => RemasterMaterialKind.Armour,
            _ => (RemasterMaterialKind?)null,
        };

        /// <summary>Alpha-tested (cards, stubble shells) or opaque/transparent.</summary>
        public static bool IsCutout(RemasterGeneratedKind k) =>
            k == RemasterGeneratedKind.HairCards || k == RemasterGeneratedKind.HairV2 || k == RemasterGeneratedKind.Stubble;

        /// <summary>Unity shader name per generated kind.</summary>
        public static string ShaderName(RemasterGeneratedKind k) => k switch
        {
            RemasterGeneratedKind.Cornea => "G3/Remaster/Cornea",
            RemasterGeneratedKind.Eye => "G3/Remaster/Eye",
            RemasterGeneratedKind.HairCards => "G3/Remaster/Hair",
            RemasterGeneratedKind.HairCap => "G3/Remaster/Hair",
            RemasterGeneratedKind.SkinV2 => "G3/Remaster/V2/Skin",
            RemasterGeneratedKind.EyeV2 => "G3/Remaster/V2/Eye",
            RemasterGeneratedKind.HairV2 => "G3/Remaster/V2/Hair",
            RemasterGeneratedKind.Stubble => "G3/Remaster/V2/Hair",
            RemasterGeneratedKind.EyeOcclusion => "G3/Remaster/V2/EyeOcclusion",
            RemasterGeneratedKind.TearFilm => "G3/Remaster/Cornea",
            RemasterGeneratedKind.Surface => "G3/Remaster/Armour",
            _ => null,
        };
    }

    /// <summary>
    /// Hair cards for heads whose hair G3 only painted on the scalp (the hero): a static mesh on the head's
    /// <c>Slot_Hair</c>, written by mhface.py <c>--hair-out</c> as
    /// <c>GameData/Remaster/Characters/Hair/&lt;static head&gt;__hair.glb</c>. Facial actors (talk rig,
    /// <c>…_Animated_NN</c>) use the hair of their static head (<c>…_NN</c>).
    /// </summary>
    public static class RemasterHair
    {
        public const string Suffix = "__hair";
        public const string SlotName = "Slot_Hair";

        static readonly Regex Animated = new Regex(@"_Animated(?=_\d+$)", RegexOptions.IgnoreCase);

        public static string Dir(string projectDirectory) => System.IO.Path.Combine(RemasterPaths.OverrideDir(projectDirectory), "Hair");

        /// <summary>The static head stem a part's hair is made for: the part itself, or the static head of a facial actor.</summary>
        public static string HeadStem(string partStem)
        {
            string s = RemasterPaths.Stem(partStem);
            if (s.EndsWith(RemasterPaths.TalkSuffix, StringComparison.Ordinal)) s = s.Substring(0, s.Length - RemasterPaths.TalkSuffix.Length);
            return Animated.Replace(s, "");
        }

        /// <summary><c>Characters/Hair/&lt;static head&gt;__hair.glb</c> for a head part (static or facial).</summary>
        public static string File(string projectDirectory, string partStem) =>
            System.IO.Path.Combine(Dir(projectDirectory), HeadStem(partStem) + Suffix + RemasterPaths.Extension);

        /// <summary>The stem under which the hair mesh asset is saved (one per static head).</summary>
        public static string MeshStem(string partStem) => HeadStem(partStem) + Suffix;

        /// <summary>Parts that get hair: those with a hair file. The first matching part wins (static and facial head share one).</summary>
        public static string FirstWithHair(IEnumerable<string> partStems, Func<string, bool> exists)
        {
            foreach (var p in partStems)
                if (!string.IsNullOrEmpty(p) && exists(HeadStem(p))) return p;
            return null;
        }
    }
}
