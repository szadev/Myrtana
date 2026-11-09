using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace G3.Formats.Remaster
{
    /// <summary>
    /// World remaster files (Docs/remaster-world.md), next to the character ones under the git-ignored GameData folder:
    /// <list type="bullet">
    /// <item><c>GameData/Remaster/World/Textures/&lt;image stem&gt;/{BaseMap,BumpMap,MaskMap}.png</c>: replacement maps of one
    /// original world image (same UVs, PNG rows top first like R1; the override flips V as for characters)</item>
    /// <item><c>GameData/Remaster/World/Materials/&lt;material stem&gt;.txt</c>: optional shader recipe of one original material
    /// (<see cref="WorldMaterialRecipe"/>: shader kind, CC0 detail layers, parameters)</item>
    /// <item><c>GameData/Remaster/World/Inventory/</c>: <c>G3Cli remaster world inventory</c> output</item>
    /// <item><c>GameData/Remaster/World/Reports/</c>: before/after renders, colour checks</item>
    /// </list>
    /// </summary>
    public static class WorldRemasterPaths
    {
        public static string Root(string projectDirectory) => Path.Combine(RemasterPaths.Root(projectDirectory), "World");
        public static string TextureDir(string projectDirectory) => Path.Combine(Root(projectDirectory), "Textures");
        public static string MaterialDir(string projectDirectory) => Path.Combine(Root(projectDirectory), "Materials");
        public static string ReportDir(string projectDirectory) => Path.Combine(Root(projectDirectory), "Reports");
        public static string RecipeFile(string projectDirectory, string materialStem) =>
            Path.Combine(MaterialDir(projectDirectory), RemasterPaths.Stem(materialStem) + ".txt");

        /// <summary>Replacement maps of one original image stem (empty when none); keys as <see cref="RemasterTextures"/>.</summary>
        /// <summary>Shared CC0 detail maps of r1 (Tools/Remaster/worlddetail.py): <c>World/Detail/&lt;kind&gt;/Normal.png</c>,
        /// kinds <see cref="WorldDetailPlan.Ground"/>, <see cref="WorldDetailPlan.Cliff"/>, <see cref="WorldDetailPlan.Bark"/>.</summary>
        public static string DetailNormal(string projectDirectory, string kind) =>
            Path.Combine(Root(projectDirectory), "Detail", kind, "Normal.png");

        public static Dictionary<string, string> FindTextures(string projectDirectory, string imageStem)
        {
            var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(imageStem)) return found;
            string dir = Path.Combine(TextureDir(projectDirectory), RemasterTextures.StemOf(imageStem));
            if (!Directory.Exists(dir)) return found;
            foreach (var f in new[] { RemasterTextures.BaseMap, RemasterTextures.BumpMap, RemasterTextures.MaskMap })
            {
                string p = Path.Combine(dir, f);
                if (File.Exists(p)) found[f] = p;
            }
            return found;
        }
    }

    /// <summary>
    /// Remaster category of a world mesh (Docs/remaster-world.md §1), by its resource name and material names. Pure, shared
    /// by the inventory (G3Cli) and the Unity override (which looks it applies, budget per category).
    /// </summary>
    public static class WorldRemasterCategories
    {
        public const string Terrain = "terrain", Water = "water", Rock = "rock", Architecture = "architecture",
            Plant = "plant", Prop = "prop", Grass = "grass", TreeBark = "tree_bark", TreeLeaf = "tree_leaf", Other = "other",
            Hidden = "hidden";

        /// <summary>Order of precedence when an image is used by several categories (the first one wins).</summary>
        public static readonly string[] Precedence = { Terrain, Water, Rock, Architecture, TreeBark, TreeLeaf, Grass, Plant, Prop, Other, Hidden };

        static readonly Regex TerrainRe = new Regex(@"_Landscape_(Cell_|Lowpoly)", RegexOptions.IgnoreCase);
        static readonly Regex WaterRe = new Regex(@"(Oceanmesh|Rivermesh|_Water_|Waterfall)", RegexOptions.IgnoreCase);
        static readonly Regex RockRe = new Regex(@"(Rock|Stone(?!wall)|Cliff|Cave|Mountain|Boulder|Canyon|_Crag|Glacier|Iceberg|_Ice_)", RegexOptions.IgnoreCase);
        static readonly Regex PlantRe = new Regex(@"(Plant|Bush|Tree|Shrub|Fern|Grass|Flower|Cactus|Palm|Mushroom|Root|Trunk|Branch|Stump|Ivy|Leaf|Leaves|Reed|Vine|Herb)", RegexOptions.IgnoreCase);
        static readonly Regex ArchRe = new Regex(@"(Building|House|Hut|Wall|Tower|Gate|Palisade|Fence|Bridge|Roof|Castle|Temple|Ruin|Stair|Floor|Door|Window|Tent|Mine|Arena|Palace|Harbour|Harbor|Pier|Jetty|Dock|Well|Shrine|Fort|Bastion|Column|Pillar|Arch|Statue|Monastery|Tavern|Smithy|Barracks|Stable|Mill|Watchtower|Scaffold|Plank|Beam|Ladder|Platform|Balcony|Chimney|Pavement|Street|Road|Battlement|Brick|Hall|Lighthouse|Outpost|Camp|_City_|_Village_|Graveyard|Tomb|Crypt|Portal|Altar)", RegexOptions.IgnoreCase);

        public static bool IsTerrainMesh(string mesh) => mesh != null && TerrainRe.IsMatch(mesh);

        /// <summary>Category of a placed world mesh; <paramref name="isWater"/> = one of its materials is a water material.</summary>
        public static string Classify(string mesh, IEnumerable<string> materials = null, bool isWater = false)
        {
            string m = Path.GetFileNameWithoutExtension(mesh ?? "");
            if (IsTerrainMesh(m)) return Terrain;
            if (m.IndexOf("_Depth_", StringComparison.OrdinalIgnoreCase) >= 0) return Hidden;   // river depth volumes (never drawn)
            if (isWater || WaterRe.IsMatch(m)) return Water;
            // Object kind = the name without the G3_ prefix and the region/kind words: "G3_Nature_Rock_Big_01",
            // "G3_Object_Barrel_01", "G3_Myrtana_Faring_House_01", "G3_Varant_Building_...".
            bool nature = m.IndexOf("_Nature_", StringComparison.OrdinalIgnoreCase) >= 0 || m.IndexOf("Vegetation", StringComparison.OrdinalIgnoreCase) >= 0;
            bool obj = m.IndexOf("_Object_", StringComparison.OrdinalIgnoreCase) >= 0 || m.IndexOf("_Objects_", StringComparison.OrdinalIgnoreCase) >= 0;
            if (RockRe.IsMatch(m) && !ArchRe.IsMatch(m)) return Rock;
            if (PlantRe.IsMatch(m) && (nature || !ArchRe.IsMatch(m))) return Plant;
            if (obj && !ArchRe.IsMatch(m)) return Prop;
            if (ArchRe.IsMatch(m)) return Architecture;
            if (nature) return Rock;   // remaining nature pieces are mostly rock/ground formations
            if (materials != null)
                foreach (var mat in materials)
                {
                    if (mat == null) continue;
                    if (RockRe.IsMatch(mat)) return Rock;
                    if (ArchRe.IsMatch(mat) || mat.IndexOf("Wood", StringComparison.OrdinalIgnoreCase) >= 0) return Architecture;
                }
            return obj ? Prop : Other;
        }

        static readonly Regex MatPlantRe = new Regex(@"^G3_Nature_(Decal|Plant|Tree|OldTree|Leaf|Bush|Misc)|Mushroom|_Leaves?_|_Bark_", RegexOptions.IgnoreCase);
        static readonly Regex MatRockRe = new Regex(@"_Wall_Stone|Rock|Mountain|Cliff|_Cave|Glacier|_Ice_|_Stone_\d|Lava", RegexOptions.IgnoreCase);
        static readonly Regex MatArchRe = new Regex(@"^G3_Architecture_|_Dungeon_|_Building|_Bricks|_WallHouse|_Roof|_Temple|_Ruin", RegexOptions.IgnoreCase);
        static readonly Regex MatPropRe = new Regex(@"^G3_(Objects?|Items|Weapons)_", RegexOptions.IgnoreCase);
        static readonly Regex MatGroundRe = new Regex(@"_Ground_|_PlainSand|_Desert_(Ground|Plain|Wave|Dune)|_Snow_|_Grass_|_Dirt|_Gravel|_Mud", RegexOptions.IgnoreCase);

        /// <summary>
        /// Category of a material (what a texture set is remastered as): water and depth by name; on terrain cells every
        /// material is terrain; else the material's own family (<c>G3_Architecture_*</c>, <c>G3_Nature_Wall_Stone_*</c>,
        /// <c>G3_Nature_Decal_*</c>, <c>G3_Objects_*</c>), else the mesh's category. Ground materials on level meshes count
        /// as terrain (same textures as the landscape cells).
        /// </summary>
        public static string ClassifyMaterial(string material, string meshCategory)
        {
            string n = Path.GetFileNameWithoutExtension(material ?? "");
            if (n.IndexOf("_Depth_", StringComparison.OrdinalIgnoreCase) >= 0) return Hidden;
            if (n.IndexOf("_Water_", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Waterfall", StringComparison.OrdinalIgnoreCase) >= 0) return Water;
            if (meshCategory == Terrain) return Terrain;
            if (MatPlantRe.IsMatch(n)) return Plant;
            if (MatRockRe.IsMatch(n) && !MatArchRe.IsMatch(n)) return Rock;
            if (MatArchRe.IsMatch(n)) return Architecture;
            if (MatPropRe.IsMatch(n)) return Prop;
            if (MatGroundRe.IsMatch(n)) return Terrain;
            return string.IsNullOrEmpty(meshCategory) ? Other : meshCategory;
        }

        /// <summary>Summed triangle areas in world units (positions) and UV units (uv0): sqrt(world / uv) = world units per UV
        /// unit, i.e. how large one texture repeat is on the surface.</summary>
        public static (double world, double uv) Areas(float[] positions, float[] uv, uint[] indices)
        {
            double wa = 0, ua = 0;
            if (positions == null || uv == null || indices == null) return (0, 0);
            for (int t = 0; t + 2 < indices.Length; t += 3)
            {
                long a = indices[t], b = indices[t + 1], c = indices[t + 2];
                double ax = positions[b * 3] - positions[a * 3], ay = positions[b * 3 + 1] - positions[a * 3 + 1], az = positions[b * 3 + 2] - positions[a * 3 + 2];
                double bx = positions[c * 3] - positions[a * 3], by = positions[c * 3 + 1] - positions[a * 3 + 1], bz = positions[c * 3 + 2] - positions[a * 3 + 2];
                double cx = ay * bz - az * by, cy = az * bx - ax * bz, cz = ax * by - ay * bx;
                wa += Math.Sqrt(cx * cx + cy * cy + cz * cz) / 2;
                double u1 = uv[b * 2] - uv[a * 2], v1 = uv[b * 2 + 1] - uv[a * 2 + 1], u2 = uv[c * 2] - uv[a * 2], v2 = uv[c * 2 + 1] - uv[a * 2 + 1];
                ua += Math.Abs(u1 * v2 - u2 * v1) / 2;
            }
            return (wa, ua);
        }

        /// <summary>The category an image belongs to when several use it (<see cref="Precedence"/>).</summary>
        public static string PrimaryOf(IEnumerable<string> categories)
        {
            var set = new HashSet<string>(categories ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            foreach (var c in Precedence) if (set.Contains(c)) return c;
            return set.FirstOrDefault() ?? Other;
        }
    }

    /// <summary>How a world material is remastered (<see cref="WorldRemasterPlan"/>).</summary>
    public enum WorldRemasterMode
    {
        /// <summary>No replacement maps for any of its images: the original material stays.</summary>
        Keep,
        /// <summary>Opaque G3/LitBlend or Simple Lit ground/wall material: <c>G3/Remaster/WorldBlend</c> (height blend, mask maps).</summary>
        Blend,
        /// <summary>Any other shader (alpha-tested Simple Lit, G3/Vegetation, G3/SpeedTree): a copy with the maps swapped
        /// and the shader's own remaster keyword (<see cref="WorldRemasterPlan.FoliageKeyword"/>) where it has one.</summary>
        Textures,
    }

    /// <summary>What the override needs to know about an original world material (read from the Unity material).</summary>
    public sealed class WorldMaterialInfo
    {
        public string Name;
        public string Shader;            // "G3/LitBlend", "Universal Render Pipeline/Simple Lit", "G3/Vegetation", "G3/SpeedTree", ...
        public string BaseMap, BaseMap2; // image stems of the layers (texture asset names), null if unset
        public string BumpMap, BumpMap2;
        public bool SingleLayer;         // LitBlend _SINGLE_LAYER
        public bool Opaque = true;       // no alpha clip / transparency (G3/Vegetation and G3/SpeedTree are never opaque)
        public bool AlphaClip;           // _AlphaClip / _ALPHATEST_ON
        public int TreeMode;             // G3/SpeedTree _G3Tree: 0 mesh (bark, fronds), 1 leaf, 2 billboard
    }

    /// <summary>The decision for one material: shader mode and the replacement file per material property.</summary>
    public sealed class WorldMaterialPlan
    {
        public WorldRemasterMode Mode;
        /// <summary>Material property to replacement PNG (absolute path). Keys: _BaseMap, _BumpMap, _MaskMap and the layer-B
        /// counterparts _BaseMap2, _BumpMap2, _MaskMap2.</summary>
        public readonly Dictionary<string, string> Maps = new Dictionary<string, string>(StringComparer.Ordinal);
        public WorldMaterialRecipe Recipe = new WorldMaterialRecipe();
        public bool HasLayerB;
        public override string ToString() => $"{Mode} [{string.Join(", ", Maps.Keys.OrderBy(k => k, StringComparer.Ordinal))}]";
    }

    /// <summary>
    /// Optional per-material shader parameters (<c>World/Materials/&lt;material&gt;.txt</c>, "key = value" lines, # comments),
    /// written by <c>Tools/Remaster/worldtex.py</c> or by hand. Unknown keys are kept.
    /// </summary>
    public sealed class WorldMaterialRecipe
    {
        public readonly Dictionary<string, string> Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Height blend amount: 0 = G3's linear vertex-alpha lerp, 1 = full height blend.</summary>
        public float HeightBlend => Float("height_blend", 1f);
        /// <summary>Blend band in height units (smaller = crisper transitions).</summary>
        public float HeightDepth => Float("height_depth", 0.2f);
        public float NormalStrength => Float("normal_strength", 1f);
        public float OcclusionStrength => Float("occlusion_strength", 1f);
        /// <summary>Foliage back-light translucency (vegetation/tree keyword).</summary>
        public float Translucency => Float("translucency", 0.35f);
        // r1 (gameplay distance): see WorldRemasterPlan.Detail
        public float MacroStrength(float fallback) => Float("macro_strength", fallback);
        public float AntiTile(float fallback) => Float("anti_tile", fallback);
        public float MesoStrength(float fallback) => Float("meso_strength", fallback);
        public float Triplanar(float fallback) => Float("triplanar", fallback);

        public float Float(string key, float fallback) =>
            Values.TryGetValue(key, out var v) && float.TryParse(v, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : fallback;

        public static WorldMaterialRecipe Parse(string text)
        {
            var r = new WorldMaterialRecipe();
            if (string.IsNullOrEmpty(text)) return r;
            foreach (var raw in text.Split('\n'))
            {
                string line = raw.Trim();
                int hash = line.IndexOf('#');
                if (hash >= 0) line = line.Substring(0, hash).Trim();
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                r.Values[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }
            return r;
        }

        public override string ToString() => string.Join("\n", Values.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => $"{k.Key} = {k.Value}"));
    }

    /// <summary>
    /// Trees v3 (Docs/remaster-world.md 10.17): the material values the remaster copy of an original G3 SpeedTree material
    /// gets (shader G3/SpeedTree, <c>_TreeV3</c> = 1). G3's meshes stay; foliage (leaf cards, fronds, billboards) is lit as a
    /// crown volume, varies per tree and reads the atlas normal/mask from Tools/Remaster/trees/treev3_tex.py; bark keeps
    /// its own normal and only varies. Recipe keys (<c>crown_normal</c>, <c>crown_ao</c>, <c>tint_variation</c>,
    /// <c>normal_strength</c>) override.
    /// </summary>
    public sealed class TreeV3Look
    {
        public const string Shader = "G3/SpeedTree";
        public const float FoliageCrownNormal = 0.45f, FoliageCrownAO = 0.4f, FoliageTint = 0.1f, FoliageNormal = 0.8f;
        public const float BarkTint = 0.06f;
        /// <summary>Alpha reference of the importer's coverage-preserving mips: SpeedTree's cutoff 84/255.</summary>
        public const float AlphaReference = 0.33f;

        public bool Applies;
        public bool Foliage;            // leaf cards, fronds, billboard (not bark)
        public float CrownNormal, CrownAO, TintVariation, NormalStrength = 1f;
        /// <summary>Leaf and billboard copies take the atlas normal (G3's have none: _NORMALMAP is enabled); fronds do not.</summary>
        public bool ForceNormalMap;
        /// <summary>Foliage copies read the atlas mask packed into the normal map (RG normal xy, B occlusion, A translucency;
        /// imported as a plain linear texture, <c>_UseMaskMap</c> = 1); bark masks have another layout and are not used.</summary>
        public bool PackedMask;

        public static TreeV3Look For(WorldMaterialInfo m, WorldMaterialRecipe recipe = null)
        {
            var l = new TreeV3Look();
            if (m == null || m.Shader != Shader) return l;
            var r = recipe ?? new WorldMaterialRecipe();
            l.Applies = true;
            l.Foliage = !WorldRemasterPlan.IsBark(m);
            l.CrownNormal = l.Foliage ? r.Float("crown_normal", FoliageCrownNormal) : 0f;
            l.CrownAO = l.Foliage ? r.Float("crown_ao", FoliageCrownAO) : 0f;
            l.TintVariation = r.Float("tint_variation", l.Foliage ? FoliageTint : BarkTint);
            l.NormalStrength = l.Foliage ? r.Float("normal_strength", FoliageNormal) : r.NormalStrength;
            // fronds (TreeMode 0, alpha-clipped: G3's dead twigs and branch sprays) carry no usable mesh tangents: with a
            // normal map they rendered near-black in the runner A|B (pass 1 and 2); they keep the crown terms only
            l.ForceNormalMap = l.Foliage && m.TreeMode != 0;
            l.PackedMask = l.ForceNormalMap;
            return l;
        }

        public override string ToString() => !Applies ? "no v3" :
            $"v3 {(Foliage ? "foliage" : "bark")} crown {CrownNormal:0.##}/{CrownAO:0.##} tint {TintVariation:0.##}";
    }

    /// <summary>r1 gameplay-distance terms of one material (Docs/remaster-world.md section 7).</summary>
    public sealed class WorldDetailPlan
    {
        public const string Ground = "ground", Cliff = "cliff", Bark = "bark";
        public float MacroStrength;   // WorldBlend _MacroStrength
        public float AntiTile;        // WorldBlend _AntiTile
        public float MesoStrength;    // WorldBlend _MacroNormalStrength (ground meso normal)
        public float Triplanar;       // WorldBlend _Triplanar (cliff normal + X/Z plane layers on steep faces)
        public bool BarkDetail;       // G3/SpeedTree _G3REMASTER_BARK
        public override string ToString() =>
            BarkDetail ? "bark detail" : $"macro {MacroStrength:0.##} anti {AntiTile:0.##} meso {MesoStrength:0.##} triplanar {Triplanar:0.##}";
    }

    /// <summary>
    /// Decides how one world material is remastered from the replacement maps that exist for its images
    /// (Docs/remaster-world.md section 4). Pure; <c>find</c> = <see cref="WorldRemasterPaths.FindTextures"/> in production.
    /// </summary>
    public static class WorldRemasterPlan
    {
        public const string BlendShader = "G3/Remaster/WorldBlend";
        /// <summary>Keyword of the foliage terms in G3/Vegetation and G3/SpeedTree (translucency, ground AO).</summary>
        public const string FoliageKeyword = "_G3REMASTER_FOLIAGE";

        public const string BarkKeyword = "_G3REMASTER_BARK";

        static readonly Regex GroundImageRe = new Regex(
            @"^G3_(Nature_(Ground_|Earthground|Forest_Snowground|Snow_Path|Dunes_)|Architecture_(Ground_City|SandGround)|Desert_Sand)",
            RegexOptions.IgnoreCase);
        static readonly Regex RockImageRe = new Regex(@"^G3_(Nature_(Wall_Stone|Rock|Myrtana_Rock)|Desert_Rock)", RegexOptions.IgnoreCase);

        /// <summary>
        /// A SpeedTree bark material: mesh mode (not leaf cards or billboards) and either named/textured as bark or not
        /// alpha-clipped (fronds are). r1 required <see cref="WorldMaterialInfo.Opaque"/>, which is never true for
        /// G3/SpeedTree, so no bark ever got its detail (runner-29 finding W1).
        /// </summary>
        public static bool IsBark(WorldMaterialInfo m) =>
            m != null && m.Shader == "G3/SpeedTree" && m.TreeMode == 0 &&
            (Has(m.Name, "bark") || Has(m.BaseMap, "bark") || !m.AlphaClip);

        static bool Has(string s, string part) => s != null && s.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;

        public static bool IsGroundImage(string stem) => !string.IsNullOrEmpty(stem) && GroundImageRe.IsMatch(stem);
        public static bool IsRockImage(string stem) => !string.IsNullOrEmpty(stem) && RockImageRe.IsMatch(stem);

        /// <summary>
        /// The r1 gameplay-distance terms: blend materials with a ground layer get macro variation, anti-tiling and the
        /// ground meso normal; with a rock layer and a ground layer (the terrain's vertex blends) or a rock layer alone
        /// they get triplanar on steep faces; other blend materials (walls, roofs) only a light macro variation; opaque
        /// SpeedTree materials (bark) the tiling CC0 bark normal. Recipe keys override.
        /// </summary>
        public static WorldDetailPlan Detail(WorldMaterialInfo m, WorldMaterialPlan plan)
        {
            var d = new WorldDetailPlan();
            if (m == null || plan == null || plan.Mode == WorldRemasterMode.Keep) return d;
            var r = plan.Recipe ?? new WorldMaterialRecipe();
            if (plan.Mode == WorldRemasterMode.Textures)
            {
                d.BarkDetail = IsBark(m);
                return d;
            }
            bool ground = IsGroundImage(m.BaseMap) || (plan.HasLayerB && IsGroundImage(m.BaseMap2));
            bool rock = IsRockImage(m.BaseMap) || (plan.HasLayerB && IsRockImage(m.BaseMap2));
            d.MacroStrength = r.MacroStrength(ground || rock ? 0.08f : 0.04f);
            d.AntiTile = r.AntiTile(ground || rock ? 0.5f : 0f);
            d.MesoStrength = r.MesoStrength(ground ? 0.6f : 0f);
            d.Triplanar = r.Triplanar(rock && m.Shader == "G3/LitBlend" ? 1f : 0f);   // terrain (planar UVs); not props with own UVs
            return d;
        }

        static bool IsBlendSource(string shader) =>
            shader == "G3/LitBlend" || shader == "Universal Render Pipeline/Simple Lit" || shader == "Universal Render Pipeline/Lit";

        public static WorldMaterialPlan For(WorldMaterialInfo m, Func<string, Dictionary<string, string>> find, string recipeText = null)
        {
            var plan = new WorldMaterialPlan { Recipe = WorldMaterialRecipe.Parse(recipeText) };
            if (m == null || find == null) return plan;
            var a = (string.IsNullOrEmpty(m.BaseMap) ? null : find(m.BaseMap)) ?? new Dictionary<string, string>();
            bool layerB = !m.SingleLayer && m.Shader == "G3/LitBlend" && !string.IsNullOrEmpty(m.BaseMap2);
            var b = (layerB ? find(m.BaseMap2) : null) ?? new Dictionary<string, string>();
            if (a.Count == 0 && b.Count == 0)
            {
                // r2: trees and plants without CC0 maps still get a terms-only copy (foliage translucency/sheen, bark
                // detail) - in r1 only the few with maps did, so the in-engine gain on vegetation was hard to see
                if (m.Shader == "G3/Vegetation" || m.Shader == "G3/SpeedTree") plan.Mode = WorldRemasterMode.Textures;
                return plan;   // else Keep
            }

            void Put(string prop, Dictionary<string, string> set, string key)
            {
                if (set.TryGetValue(key, out var path)) plan.Maps[prop] = path;
            }
            Put("_BaseMap", a, RemasterTextures.BaseMap);
            Put("_BumpMap", a, RemasterTextures.BumpMap);
            Put("_MaskMap", a, RemasterTextures.MaskMap);
            if (layerB)
            {
                Put("_BaseMap2", b, RemasterTextures.BaseMap);
                Put("_BumpMap2", b, RemasterTextures.BumpMap);
                Put("_MaskMap2", b, RemasterTextures.MaskMap);
            }
            plan.HasLayerB = layerB;
            plan.Mode = IsBlendSource(m.Shader) && m.Opaque ? WorldRemasterMode.Blend : WorldRemasterMode.Textures;
            return plan;
        }
    }
}
