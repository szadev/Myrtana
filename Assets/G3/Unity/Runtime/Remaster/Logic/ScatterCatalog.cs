using System;
using System.Collections.Generic;

namespace G3.Unity
{
    // Ground cover scatter (Docs/remaster-world.md section 18): what grows where. Engine-free, compiled into G3.Unity and
    // Tools/G3Tests. The runtime (G3GroundScatter) resolves the item sources to meshes/materials (G3ScatterSet).

    /// <summary>What a terrain layer is, from G3's own layer texture (or material) name.</summary>
    public enum GroundClass : byte
    {
        None = 0,
        Meadow,       // G3_Nature_Ground_Grass_01/02/03
        Steppe,       // G3_Nature_Ground_Grass_07 ("Spain" grass: Varant's north, dry)
        Forest,       // G3_Nature_Ground_Forest_01
        Earth,        // Earthground, Ground_Earth
        Gravel,
        Riverstone,
        Sand,         // dunes, plain/wave sand
        Snow,
        SnowForest,   // G3_Nature_Forest_Snowground
        Rock,         // Wall_Stone, rock layers
        Path,         // paths, packed snow paths
        Town,         // city ground, architecture, marble, plates
        Dungeon,
        Water,
        Count,
    }

    /// <summary>The three lands (G3's regions; the manifest's RegionAt at the tile centre).</summary>
    public enum ScatterBiome : byte { Myrtana = 0, Nordmar = 1, Varant = 2 }

    /// <summary>How an item is drawn and placed.</summary>
    public enum ScatterKind : byte
    {
        /// <summary>Alpha-tested plant cards/meshes (wind, bending, translucency): grass, herbs, flowers, ferns, bushes, reeds.</summary>
        Plant = 0,
        /// <summary>Flat alpha-tested cards lying on the ground (aligned to the slope, no wind): leaf litter, needles, moss.</summary>
        Litter = 1,
        /// <summary>Opaque solids (no wind/bending, G3 stone colour triplanar): pebbles, small rocks, debris.</summary>
        Solid = 2,
        /// <summary>Alpha-tested rigid meshes (no wind): G3's branches, twigs, mushrooms, stumps.</summary>
        Debris = 3,
    }

    /// <summary>Where a rule grows relative to G3's trees (<see cref="ScatterCanopy"/>): anywhere, under any crown, under
    /// broadleaf / conifer crowns only, or only in the open.</summary>
    public enum CanopyRule : byte { Any = 0, Under, Broadleaf, Conifer, Open }

    /// <summary>The three candidate layers: each has its own jittered grid (spacing) and view range band.</summary>
    public enum ScatterLayer : byte { Cover = 0, Plants = 1, Large = 2 }

    /// <summary>One placeable thing. <see cref="Sources"/>: G3 mesh names (prefix match) or <c>gen:&lt;name&gt;</c> (generated).</summary>
    public sealed class ScatterItem
    {
        public string Id;
        public ScatterKind Kind;
        public string[] Sources;
        public float SizeMin = 0.8f, SizeMax = 1.2f;   // uniform scale on the source mesh
        public float ViewRange = 40f;                  // m; shrink-fade over the last 40 %
        public float Align = 0.3f;                     // 0 upright .. 1 along the ground normal
        public float Tilt = 0.08f;                     // random lean (rad)
        public float Sink = 0f;                        // m into the ground (x size)
        public float Wind = 1f;                        // x G3's grass wind
        public float Bend = 1f;                        // x the trail bending (hero/NPCs)
        public bool Shadows;                           // casts shadows (near only)
        public bool UseG3Tint = true;                  // tint by G3's local vegetation colour field
        public float Radius = 0.5f;                    // m bounding radius at scale 1 (for culling), set by the runtime from the mesh
        /// <summary>Solids: G3's image drawn world-triplanar on them (the land's own rock: G3's palette).</summary>
        public string Texture;
        /// <summary>Fixed instance colour 0xAARRGGBB instead of G3's local vegetation colour (0 = none): frost on Nordmar's grass.</summary>
        public uint Tint;

        public ScatterItem Clone() => (ScatterItem)MemberwiseClone();
    }

    /// <summary>One placement rule: the item's chance per candidate of its layer, where it may grow.</summary>
    public sealed class ScatterRule
    {
        public string Item;
        public ScatterLayer Layer;
        /// <summary>Lands (bit per <see cref="ScatterBiome"/>).</summary>
        public int Biomes = 7;
        /// <summary>Ground classes (bit per <see cref="GroundClass"/>) and how much each counts (weight 0..1 x the layer weight).</summary>
        public GroundClass[] Grounds;
        /// <summary>Chance per candidate at full ground weight (before clumping and the density option).</summary>
        public float Chance;
        /// <summary>Ground normal y range (1 = flat).</summary>
        public float MinUp = 0.75f, MaxUp = 1.01f;
        /// <summary>Clumps: noise wavelength (m) and contrast (0 = uniform, 1 = patches from none to double).</summary>
        public float Clump = 6f, ClumpContrast = 0.8f;
        /// <summary>0 anywhere dry; 1 only on the shore (dry ground within <see cref="ScatterSampler.ShoreReach"/> of water, or
        /// shallow water up to <see cref="ScatterSampler.ShallowDepth"/>).</summary>
        public bool Shore;
        /// <summary>Near the water (shore) the rule's chance is multiplied by this (0 = never on the shore).</summary>
        public float ShoreFactor = 1f;

        /// <summary>Tree cover condition: the chance is multiplied by the cover (or by 1 - cover for <see cref="CanopyRule.Open"/>).</summary>
        public CanopyRule Under;

        /// <summary>fix-169: ground classes the item must keep off - the chance falls to 0 where they make up a third of the
        /// blend (forest-floor beds on a rock ledge drew as flat dark ovals over G3's rock).</summary>
        public GroundClass[] Avoid;
        /// <summary>fix-169: large flat cards (beds): the ground across the card's footprint (4 points at 0.45 x its largest
        /// size) must lie within this of the plane at its centre (m) and be the rule's ground there too (0 = no check):
        /// no bed bridging a ledge edge or hanging over a drop.</summary>
        public float Fit;

        public bool InBiome(ScatterBiome b) => (Biomes & (1 << (int)b)) != 0;
    }

    /// <summary>Items and rules. <see cref="Default"/> is the shipped palette (G3's own colours: G3 meshes and atlases, the
    /// generated cards matched to G3's ground images).</summary>
    public sealed class ScatterCatalog
    {
        public readonly List<ScatterItem> Items = new List<ScatterItem>();
        public readonly List<ScatterRule> Rules = new List<ScatterRule>();

        /// <summary>Candidate spacing per layer (m): a jittered grid, one item at most per candidate.</summary>
        public float[] Spacing = { 0.55f, 1.1f, 3.2f };

        public ScatterItem Find(string id)
        {
            foreach (var i in Items) if (i.Id == id) return i;
            return null;
        }

        public int IndexOf(string id)
        {
            for (int i = 0; i < Items.Count; i++) if (Items[i].Id == id) return i;
            return -1;
        }

        /// <summary>Largest view range of the items of one layer (m): how far that layer's tiles are kept.</summary>
        public float LayerRange(ScatterLayer layer)
        {
            float r = 0;
            foreach (var rule in Rules)
                if (rule.Layer == layer) { var it = Find(rule.Item); if (it != null) r = Math.Max(r, it.ViewRange); }
            return r;
        }

        const int MY = 1 << (int)ScatterBiome.Myrtana, NO = 1 << (int)ScatterBiome.Nordmar, VA = 1 << (int)ScatterBiome.Varant;

        static GroundClass[] G(params GroundClass[] g) => g;

        /// <summary>The shipped palette (tuned in engine; Docs section 18.3).</summary>
        public static ScatterCatalog Default()
        {
            var c = new ScatterCatalog();
            void I(ScatterItem i) => c.Items.Add(i);
            void R(string item, ScatterLayer layer, int biomes, float chance, GroundClass[] grounds, float minUp = 0.78f, float clump = 6f,
                   float contrast = 0.8f, bool shore = false, float shoreFactor = 1f, float maxUp = 1.01f, CanopyRule under = CanopyRule.Any,
                   GroundClass[] avoid = null, float fit = 0)
                => c.Rules.Add(new ScatterRule { Item = item, Layer = layer, Biomes = biomes, Chance = chance, Grounds = grounds, MinUp = minUp,
                                                 MaxUp = maxUp, Clump = clump, ClumpContrast = contrast, Shore = shore, ShoreFactor = shoreFactor, Under = under,
                                                 Avoid = avoid, Fit = fit });

            // (pass 2 perf: shadows only for rocks and logs - every shadow-casting group is drawn once per cascade; casting ferns,
            // shrubs and cacti cost ~+1 ms CPU at Ardea, rocks/logs alone +0.27 ms total, Docs 17.8)
            // ---------------- items: G3's own plants (vegetation atlases, G3 meshes) ----------------
            I(new ScatterItem { Id = "grass", Kind = ScatterKind.Plant, Sources = new[] { "G3_Brush_Plant_Grass_05", "G3_Brush_Plant_Grass_06" }, SizeMin = 0.55f, SizeMax = 0.95f, ViewRange = 45 });
            I(new ScatterItem { Id = "flowers", Kind = ScatterKind.Plant, Sources = new[] { "G3_Brush_Plant_Herb_01", "G3_Brush_Plant_Herb_03", "G3_Brush_Plant_Herb_05" }, SizeMin = 0.6f, SizeMax = 1.0f, ViewRange = 45 });
            I(new ScatterItem { Id = "herbs", Kind = ScatterKind.Plant, Sources = new[] { "G3_Brush_Plant_Herb_04", "G3_Brush_Plant_Herb_08" }, SizeMin = 0.6f, SizeMax = 1.0f, ViewRange = 45 });
            I(new ScatterItem { Id = "underbrush", Kind = ScatterKind.Plant, Sources = new[] { "G3_Brush_Plant_Forest_Underbrush_01", "G3_Brush_Plant_Forest_Underbrush_02" }, SizeMin = 0.7f, SizeMax = 1.2f, ViewRange = 55 });
            I(new ScatterItem { Id = "fern_small", Kind = ScatterKind.Plant, Sources = new[] { "G3_Brush_Plant_Fern_01", "G3_Brush_Plant_Fern_02" }, SizeMin = 0.6f, SizeMax = 1.0f, ViewRange = 60, Wind = 0.7f });
            I(new ScatterItem { Id = "fern", Kind = ScatterKind.Plant, Sources = new[] { "G3_Object_Plant_Fern_01", "G3_Object_Plant_Fern_02" }, SizeMin = 0.28f, SizeMax = 0.45f, ViewRange = 90, Wind = 0.5f, Bend = 0.6f, UseG3Tint = false });
            I(new ScatterItem { Id = "reed", Kind = ScatterKind.Plant, Sources = new[] { "G3_Object_Plant_Reed_01" }, SizeMin = 0.55f, SizeMax = 0.9f, ViewRange = 80, Wind = 0.9f, UseG3Tint = false });
            I(new ScatterItem { Id = "drygrass", Kind = ScatterKind.Plant, Sources = new[] { "G3_Brush_Plant_DryGrass_01", "G3_Brush_Plant_DryGrass_02" }, SizeMin = 0.5f, SizeMax = 0.9f, ViewRange = 50 });
            I(new ScatterItem { Id = "varant_plants", Kind = ScatterKind.Plant, Sources = new[] { "G3_Nature_Varant_SmallPlants_01", "G3_Nature_Varant_SmallPlants_06" }, SizeMin = 0.7f, SizeMax = 1.1f, ViewRange = 55, Wind = 0.5f });
            I(new ScatterItem { Id = "varant_bush", Kind = ScatterKind.Plant, Sources = new[] { "G3_Nature_Varant_Vegetation_Bushes_01", "G3_Nature_Varant_Vegetation_Bushes_02", "G3_Nature_Varant_Vegetation_Bushes_06" }, SizeMin = 0.6f, SizeMax = 1.0f, ViewRange = 90, Wind = 0.6f, Bend = 0.5f });
            I(new ScatterItem { Id = "roots", Kind = ScatterKind.Debris, Sources = new[] { "G3_Brush_Plant_Roots_01", "G3_Nature_Varant_Vegetation_Roots_01" }, SizeMin = 0.6f, SizeMax = 1.0f, ViewRange = 45, Align = 0.6f, Wind = 0, Bend = 0 });
            I(new ScatterItem { Id = "twigs", Kind = ScatterKind.Debris, Sources = new[] { "G3_Object_Branch_Small_01", "G3_Object_Branch_Small_04" }, SizeMin = 0.6f, SizeMax = 1.0f, ViewRange = 40, Align = 0.9f, Wind = 0, Bend = 0 });
            I(new ScatterItem { Id = "branch", Kind = ScatterKind.Debris, Sources = new[] { "G3_Object_LooseBranch_01", "G3_Object_BrokenBranch_01" }, SizeMin = 0.6f, SizeMax = 0.95f, ViewRange = 70, Align = 0.9f, Wind = 0, Bend = 0, UseG3Tint = false });
            I(new ScatterItem { Id = "mushrooms", Kind = ScatterKind.Debris, Sources = new[] { "G3_Object_Mushroom_01", "G3_Object_Mushroom_03" }, SizeMin = 0.6f, SizeMax = 1.0f, ViewRange = 35, Align = 0.4f, Wind = 0, Bend = 0, UseG3Tint = false });
            I(new ScatterItem { Id = "snow_mushrooms", Kind = ScatterKind.Debris, Sources = new[] { "G3_Object_Mushroom_Snow_01" }, SizeMin = 0.6f, SizeMax = 1.0f, ViewRange = 35, Align = 0.4f, Wind = 0, Bend = 0, UseG3Tint = false });
            I(new ScatterItem { Id = "pebbles", Kind = ScatterKind.Debris, Sources = new[] { "G3_Brush_Object_Stone_Small_01", "G3_Brush_Object_Stone_Small_03" }, SizeMin = 0.7f, SizeMax = 1.3f, ViewRange = 40, Align = 0.8f, Sink = 0.02f, Wind = 0, Bend = 0 });
            // pass 2: deadwood and G3's own snowy rocks/stumps, Nordmar's frosted grass, Varant's bones
            I(new ScatterItem { Id = "log", Kind = ScatterKind.Debris, Sources = new[] { "G3_Object_Plant_RottenTree_01" }, SizeMin = 0.28f, SizeMax = 0.4f, ViewRange = 90, Align = 0.9f, Tilt = 0.03f, Wind = 0, Bend = 0, UseG3Tint = false, Shadows = true });
            I(new ScatterItem { Id = "stump", Kind = ScatterKind.Debris, Sources = new[] { "G3_Object_TreeStump_01", "G3_Object_TreeStump_02" }, SizeMin = 0.35f, SizeMax = 0.55f, ViewRange = 80, Align = 0.5f, Tilt = 0.05f, Sink = 0.05f, Wind = 0, Bend = 0, UseG3Tint = false });
            I(new ScatterItem { Id = "snow_log", Kind = ScatterKind.Debris, Sources = new[] { "G3_Object_Plant_RottenTree_Snow_01", "G3_Object_Plant_RottenTree_Snow_02" }, SizeMin = 0.28f, SizeMax = 0.4f, ViewRange = 90, Align = 0.9f, Tilt = 0.03f, Wind = 0, Bend = 0, UseG3Tint = false, Shadows = true });
            I(new ScatterItem { Id = "snow_stump", Kind = ScatterKind.Debris, Sources = new[] { "G3_Object_TreeStump_Snow_01", "G3_Object_TreeStump_Snow_02" }, SizeMin = 0.35f, SizeMax = 0.55f, ViewRange = 80, Align = 0.5f, Tilt = 0.05f, Sink = 0.05f, Wind = 0, Bend = 0, UseG3Tint = false });
            I(new ScatterItem { Id = "snow_rocks", Kind = ScatterKind.Debris, Sources = new[] { "G3_Object_Stone_Snow_01", "G3_Object_Stone_Snow_02" }, SizeMin = 0.25f, SizeMax = 0.7f, ViewRange = 110, Align = 0.6f, Tilt = 0.3f, Sink = 0.1f, Wind = 0, Bend = 0, UseG3Tint = false, Shadows = true });
            I(new ScatterItem { Id = "g3_rocks", Kind = ScatterKind.Debris, Sources = new[] { "G3_Object_Stone_01", "G3_Object_Stone_02" }, SizeMin = 0.22f, SizeMax = 0.6f, ViewRange = 110, Align = 0.6f, Tilt = 0.3f, Sink = 0.12f, Wind = 0, Bend = 0, UseG3Tint = false, Shadows = true });
            I(new ScatterItem { Id = "frost_grass", Kind = ScatterKind.Plant, Sources = new[] { "G3_Brush_Plant_DryGrass_01", "G3_Brush_Plant_DryGrass_02" }, SizeMin = 0.4f, SizeMax = 0.75f, ViewRange = 45, Wind = 0.5f, UseG3Tint = false, Tint = 0xFFB4B8BCu });
            I(new ScatterItem { Id = "skull", Kind = ScatterKind.Debris, Sources = new[] { "G3_Object_Skull_Buffalo_01" }, SizeMin = 0.7f, SizeMax = 1.0f, ViewRange = 50, Align = 0.8f, Tilt = 0.3f, Sink = 0.05f, Wind = 0, Bend = 0, UseG3Tint = false });
            I(new ScatterItem { Id = "varant_pebbles", Kind = ScatterKind.Debris, Sources = new[] { "G3_Brush_Object_Stone_Varant_Small_01", "G3_Brush_Object_Stone_Varant_Small_04" }, SizeMin = 0.7f, SizeMax = 1.3f, ViewRange = 45, Align = 0.8f, Sink = 0.02f, Wind = 0, Bend = 0 });

            // ---------------- items: generated locally (Tools/Remaster/scatter, Docs 17.4), G3-palette matched ----------------
            I(new ScatterItem { Id = "litter_leaves", Kind = ScatterKind.Litter, Sources = new[] { "gen:litter_leaves" }, SizeMin = 0.7f, SizeMax = 1.3f, ViewRange = 32, Align = 1, Tilt = 0.02f, Wind = 0, Bend = 0, UseG3Tint = false });
            I(new ScatterItem { Id = "litter_needles", Kind = ScatterKind.Litter, Sources = new[] { "gen:litter_needles" }, SizeMin = 0.7f, SizeMax = 1.3f, ViewRange = 32, Align = 1, Tilt = 0.02f, Wind = 0, Bend = 0, UseG3Tint = false });
            I(new ScatterItem { Id = "moss", Kind = ScatterKind.Litter, Sources = new[] { "gen:moss" }, SizeMin = 0.6f, SizeMax = 1.2f, ViewRange = 32, Align = 1, Tilt = 0.02f, Wind = 0, Bend = 0, UseG3Tint = false });
            I(new ScatterItem { Id = "desert_debris", Kind = ScatterKind.Litter, Sources = new[] { "gen:desert_debris" }, SizeMin = 0.7f, SizeMax = 1.2f, ViewRange = 32, Align = 1, Tilt = 0.02f, Wind = 0, Bend = 0, UseG3Tint = false });
            I(new ScatterItem { Id = "frozen_shrub", Kind = ScatterKind.Plant, Sources = new[] { "gen:frozen_shrub" }, SizeMin = 0.6f, SizeMax = 1.1f, ViewRange = 80, Wind = 0.35f, Bend = 0.5f, UseG3Tint = false });
            I(new ScatterItem { Id = "desert_shrub", Kind = ScatterKind.Plant, Sources = new[] { "gen:desert_shrub" }, SizeMin = 0.6f, SizeMax = 1.1f, ViewRange = 80, Wind = 0.4f, Bend = 0.5f, UseG3Tint = false });
            // pass 2 beds: decal-like ground cards 2-3 m (G3's forest floor image = a leaf litter photo; needles; moss)
            I(new ScatterItem { Id = "litter_bed", Kind = ScatterKind.Litter, Sources = new[] { "gen:litter_bed_a", "gen:litter_bed_b" }, SizeMin = 1.9f, SizeMax = 3.0f, ViewRange = 55, Align = 1, Tilt = 0.01f, Wind = 0, Bend = 0, UseG3Tint = false });
            I(new ScatterItem { Id = "needle_bed", Kind = ScatterKind.Litter, Sources = new[] { "gen:needle_bed_a", "gen:needle_bed_b" }, SizeMin = 1.9f, SizeMax = 3.0f, ViewRange = 55, Align = 1, Tilt = 0.01f, Wind = 0, Bend = 0, UseG3Tint = false });
            I(new ScatterItem { Id = "moss_bed", Kind = ScatterKind.Litter, Sources = new[] { "gen:moss_bed_a", "gen:moss_bed_b" }, SizeMin = 1.2f, SizeMax = 2.2f, ViewRange = 50, Align = 1, Tilt = 0.01f, Wind = 0, Bend = 0, UseG3Tint = false });
            I(new ScatterItem { Id = "palm_litter", Kind = ScatterKind.Litter, Sources = new[] { "gen:palm_litter" }, SizeMin = 1.6f, SizeMax = 2.6f, ViewRange = 50, Align = 1, Tilt = 0.02f, Wind = 0, Bend = 0, UseG3Tint = false });
            I(new ScatterItem { Id = "thorn_bush", Kind = ScatterKind.Plant, Sources = new[] { "gen:thorn_bush" }, SizeMin = 0.7f, SizeMax = 1.4f, ViewRange = 85, Wind = 0.25f, Bend = 0.4f, UseG3Tint = false });
            I(new ScatterItem { Id = "cactus", Kind = ScatterKind.Plant, Sources = new[] { "gen:cactus" }, SizeMin = 0.6f, SizeMax = 1.2f, ViewRange = 100, Wind = 0, Bend = 0, UseG3Tint = false });
            // rocks and stones: generated shapes (Hunyuan3D-2mini, decimated) in each land's own G3 rock image
            foreach (var (land, tex) in new[] { ("my", "G3_Nature_Wall_Stone_02_Diffuse_01"), ("no", "G3_Nature_Wall_Stone_03_Diffuse_01"), ("va", "G3_Nature_Wall_Stone_07_Diffuse_04") })
            {
                I(new ScatterItem { Id = "rocks_" + land, Kind = ScatterKind.Solid, Sources = new[] { "gen:rock_a", "gen:rock_b" }, Texture = tex, SizeMin = 0.25f, SizeMax = 0.7f, ViewRange = 120, Align = 0.6f, Tilt = 0.4f, Sink = 0.25f, Wind = 0, Bend = 0, UseG3Tint = false, Shadows = true });
                I(new ScatterItem { Id = "stones_" + land, Kind = ScatterKind.Solid, Sources = new[] { "gen:rock_a", "gen:rock_b" }, Texture = tex, SizeMin = 0.06f, SizeMax = 0.16f, ViewRange = 40, Align = 0.7f, Tilt = 0.6f, Sink = 0.2f, Wind = 0, Bend = 0, UseG3Tint = false });
            }

            // ---------------- rules ----------------
            var meadow = G(GroundClass.Meadow);
            var green = G(GroundClass.Meadow, GroundClass.Forest, GroundClass.Earth);
            var forest = G(GroundClass.Forest);
            var steppe = G(GroundClass.Steppe);
            var desert = G(GroundClass.Sand, GroundClass.Earth, GroundClass.Gravel);
            var stony = G(GroundClass.Gravel, GroundClass.Riverstone, GroundClass.Rock);
            var snow = G(GroundClass.Snow, GroundClass.SnowForest);
            var any = G(GroundClass.Meadow, GroundClass.Forest, GroundClass.Earth, GroundClass.Gravel, GroundClass.Riverstone, GroundClass.Steppe);

            // Myrtana: meadows (grass gaps, flowers in colonies, herbs), forest floor (litter, moss, ferns, twigs, mushrooms)
            // (pass 2: grass and herbs thin out under G3's crowns - the forest floor read as a meadow)
            R("grass", ScatterLayer.Plants, MY, 0.28f, meadow, clump: 5, contrast: 0.9f, under: CanopyRule.Open);
            R("flowers", ScatterLayer.Plants, MY, 0.18f, meadow, clump: 9, contrast: 1.0f, under: CanopyRule.Open);
            R("herbs", ScatterLayer.Plants, MY, 0.10f, G(GroundClass.Meadow, GroundClass.Earth), clump: 7, contrast: 1.0f, under: CanopyRule.Open);
            R("underbrush", ScatterLayer.Plants, MY, 0.20f, forest, clump: 6);
            R("fern_small", ScatterLayer.Plants, MY, 0.10f, forest, clump: 8, contrast: 1.0f);
            R("fern", ScatterLayer.Large, MY, 0.16f, forest, minUp: 0.8f, clump: 14, contrast: 1.0f);
            R("litter_leaves", ScatterLayer.Cover, MY, 0.12f, forest, minUp: 0.7f, clump: 8, contrast: 0.9f);
            R("litter_needles", ScatterLayer.Cover, MY, 0.06f, G(GroundClass.Forest), minUp: 0.7f, clump: 11, contrast: 1.0f);
            // fix-169: beds (2-3 m flat cards) keep off rock and need ground that fits their footprint (b1021a: moss beds on a
            // rock ledge as hard dark ovals)
            var rocky = G(GroundClass.Rock, GroundClass.Riverstone, GroundClass.Gravel);
            const float bedFit = 0.12f;
            R("litter_bed", ScatterLayer.Large, MY, 0.16f, forest, minUp: 0.8f, clump: 10, contrast: 1.0f, avoid: rocky, fit: bedFit);
            R("moss", ScatterLayer.Cover, MY, 0.16f, G(GroundClass.Forest, GroundClass.Rock, GroundClass.Riverstone), minUp: 0.55f, clump: 7, contrast: 1.0f);
            R("twigs", ScatterLayer.Cover, MY | NO, 0.035f, G(GroundClass.Forest, GroundClass.SnowForest, GroundClass.Earth), clump: 9);
            R("roots", ScatterLayer.Plants, MY, 0.02f, forest, clump: 12);
            R("branch", ScatterLayer.Large, MY, 0.05f, G(GroundClass.Forest), clump: 8, contrast: 1.6f);
            R("log", ScatterLayer.Large, MY, 0.015f, G(GroundClass.Forest), minUp: 0.85f, clump: 9, contrast: 1.8f);
            R("mushrooms", ScatterLayer.Plants, MY, 0.025f, forest, clump: 10, contrast: 1.0f);
            R("pebbles", ScatterLayer.Cover, MY | NO, 0.05f, G(GroundClass.Earth, GroundClass.Gravel, GroundClass.Riverstone, GroundClass.Path), clump: 5, contrast: 1.0f);
            R("stones_my", ScatterLayer.Cover, MY, 0.05f, stony, minUp: 0.6f, clump: 6, contrast: 1.0f);
            // (pass 2: half of the rocks are G3's own Stone_01/02 meshes with their G3 material - the AI rocks could not be
            // generated, RAM - the rest the procedural rock in G3's rock image)
            R("rocks_my", ScatterLayer.Large, MY, 0.045f, any, minUp: 0.6f, clump: 20, contrast: 1.0f, shoreFactor: 1.5f);
            R("g3_rocks", ScatterLayer.Large, MY, 0.045f, any, minUp: 0.6f, clump: 12, contrast: 1.5f, shoreFactor: 1.5f);
            R("rocks_my", ScatterLayer.Large, MY, 0.09f, G(GroundClass.Rock), minUp: 0.45f, clump: 15, contrast: 1.0f);
            R("g3_rocks", ScatterLayer.Large, MY, 0.09f, G(GroundClass.Rock), minUp: 0.45f, clump: 12, contrast: 1.5f);
            R("rocks_no", ScatterLayer.Large, NO, 0.12f, G(GroundClass.Rock, GroundClass.Gravel), minUp: 0.45f, clump: 15, contrast: 1.0f);
            R("rocks_va", ScatterLayer.Large, VA, 0.14f, G(GroundClass.Rock), minUp: 0.45f, clump: 15, contrast: 1.0f);
            // under G3's trees whatever the ground layer (G3 painted grass under many of its forests): litter by crown type,
            // ferns, moss, twigs, branches, mushrooms - the forest floor follows the forest
            var soft = G(GroundClass.Meadow, GroundClass.Forest, GroundClass.Earth, GroundClass.Gravel, GroundClass.Riverstone, GroundClass.Steppe);
            // pass 2: the floor is beds of litter / needles / moss (2-3 m decal cards on the Large grid, nearly touching under
            // dense crowns), small cards only as accents; deadwood and stones in clusters (short clump wavelength, contrast
            // > 1: patches with bare ground between)
            var bedGround = G(GroundClass.Meadow, GroundClass.Forest, GroundClass.Earth, GroundClass.Steppe);
            R("litter_bed", ScatterLayer.Large, MY, 0.55f, bedGround, minUp: 0.8f, clump: 9, contrast: 0.7f, under: CanopyRule.Broadleaf, avoid: rocky, fit: bedFit);
            R("needle_bed", ScatterLayer.Large, MY, 0.55f, G(GroundClass.Meadow, GroundClass.Forest, GroundClass.Earth), minUp: 0.8f, clump: 9, contrast: 0.7f, under: CanopyRule.Conifer, avoid: rocky, fit: bedFit);
            R("moss_bed", ScatterLayer.Large, MY, 0.10f, G(GroundClass.Forest, GroundClass.Meadow), minUp: 0.85f, clump: 7, contrast: 1.4f, under: CanopyRule.Under, avoid: rocky, fit: bedFit);
            R("litter_leaves", ScatterLayer.Cover, MY, 0.10f, soft, minUp: 0.7f, clump: 7, contrast: 1.0f, under: CanopyRule.Broadleaf);
            R("litter_needles", ScatterLayer.Cover, MY, 0.08f, G(GroundClass.Meadow, GroundClass.Forest, GroundClass.Earth, GroundClass.Gravel), minUp: 0.7f, clump: 9, contrast: 1.0f, under: CanopyRule.Conifer);
            R("moss", ScatterLayer.Cover, MY, 0.06f, soft, minUp: 0.6f, clump: 6, contrast: 1.2f, under: CanopyRule.Under);
            R("twigs", ScatterLayer.Cover, MY | NO, 0.06f, soft, clump: 4, contrast: 1.6f, under: CanopyRule.Under);
            R("stones_my", ScatterLayer.Cover, MY, 0.035f, soft, minUp: 0.6f, clump: 4, contrast: 1.8f, under: CanopyRule.Under);
            R("fern_small", ScatterLayer.Plants, MY, 0.10f, soft, clump: 8, contrast: 1.2f, under: CanopyRule.Under);
            R("underbrush", ScatterLayer.Plants, MY, 0.05f, soft, clump: 6, contrast: 1.2f, under: CanopyRule.Under);
            R("mushrooms", ScatterLayer.Plants, MY, 0.03f, soft, clump: 6, contrast: 1.6f, under: CanopyRule.Under);
            R("fern", ScatterLayer.Large, MY, 0.14f, soft, minUp: 0.8f, clump: 12, contrast: 1.2f, under: CanopyRule.Under);
            R("branch", ScatterLayer.Large, MY, 0.08f, soft, clump: 7, contrast: 1.6f, under: CanopyRule.Under);
            R("log", ScatterLayer.Large, MY, 0.025f, soft, minUp: 0.85f, clump: 9, contrast: 1.8f, under: CanopyRule.Under);
            R("stump", ScatterLayer.Large, MY, 0.012f, soft, minUp: 0.85f, clump: 9, contrast: 1.8f, under: CanopyRule.Under);
            // shoreline: reeds in the shallows and on the bank, grass and flowers thin out there
            R("reed", ScatterLayer.Large, MY, 0.55f, any, minUp: 0.7f, clump: 7, contrast: 0.7f, shore: true);
            R("reed", ScatterLayer.Plants, MY, 0.10f, any, minUp: 0.7f, clump: 7, contrast: 0.8f, shore: true);

            // Nordmar: sparse frozen shrubs, frosted grass tufts, G3's snowy rocks, logs and stumps (snow caps from the shader)
            R("frozen_shrub", ScatterLayer.Large, NO, 0.07f, snow, minUp: 0.75f, clump: 18, contrast: 1.0f);
            R("rocks_no", ScatterLayer.Large, NO, 0.015f, snow, minUp: 0.7f, clump: 20, contrast: 1.0f);
            R("snow_rocks", ScatterLayer.Large, NO, 0.03f, snow, minUp: 0.6f, clump: 10, contrast: 1.6f);
            R("frost_grass", ScatterLayer.Plants, NO, 0.07f, snow, minUp: 0.8f, clump: 5, contrast: 1.6f);
            R("snow_log", ScatterLayer.Large, NO, 0.012f, snow, minUp: 0.85f, clump: 9, contrast: 1.8f, under: CanopyRule.Under);
            R("snow_stump", ScatterLayer.Large, NO, 0.01f, snow, minUp: 0.85f, clump: 9, contrast: 1.8f, under: CanopyRule.Under);
            R("frozen_shrub", ScatterLayer.Plants, NO, 0.02f, snow, minUp: 0.8f, clump: 14, contrast: 1.0f);
            // (session 4: G3_Object_Mushroom_Snow_01 drew as blue-teal stalks, and needle litter read as orange blotches on the
            // snow: Nordmar keeps shrubs, twigs, branches, stones and rocks only)
            R("stones_no", ScatterLayer.Cover, NO, 0.02f, G(GroundClass.Snow, GroundClass.SnowForest, GroundClass.Rock, GroundClass.Gravel), clump: 9, contrast: 1.0f);

            // Varant: dry grass on the steppe, desert shrubs and cacti, debris and pebbles on sand
            // (pass 2: varied density - dry grass in clumps with bare sand between, thorn scrub, pebbles and stones in
            // clusters, dry palm fronds under the palms, a rare bleached skull)
            R("drygrass", ScatterLayer.Plants, VA, 0.30f, steppe, clump: 7, contrast: 1.4f);
            R("drygrass", ScatterLayer.Plants, VA, 0.12f, desert, clump: 6, contrast: 1.7f);
            R("varant_plants", ScatterLayer.Plants, VA, 0.04f, G(GroundClass.Steppe, GroundClass.Earth, GroundClass.Sand), clump: 8, contrast: 1.4f);
            R("varant_bush", ScatterLayer.Large, VA, 0.05f, G(GroundClass.Steppe, GroundClass.Earth, GroundClass.Sand), clump: 12, contrast: 1.3f);
            R("thorn_bush", ScatterLayer.Large, VA, 0.06f, G(GroundClass.Steppe, GroundClass.Earth, GroundClass.Sand, GroundClass.Gravel), minUp: 0.8f, clump: 11, contrast: 1.4f);
            R("desert_shrub", ScatterLayer.Large, VA, 0.035f, desert, clump: 18, contrast: 1.0f);
            R("cactus", ScatterLayer.Large, VA, 0.015f, G(GroundClass.Sand, GroundClass.Earth), minUp: 0.85f, clump: 25, contrast: 1.0f);
            R("palm_litter", ScatterLayer.Large, VA, 0.30f, G(GroundClass.Sand, GroundClass.Earth, GroundClass.Steppe, GroundClass.Gravel), minUp: 0.8f, clump: 8, contrast: 1.0f, under: CanopyRule.Under);
            R("desert_debris", ScatterLayer.Cover, VA, 0.07f, desert, clump: 6, contrast: 1.5f);
            R("varant_pebbles", ScatterLayer.Cover, VA, 0.07f, G(GroundClass.Earth, GroundClass.Gravel, GroundClass.Steppe, GroundClass.Sand, GroundClass.Path), clump: 4, contrast: 1.7f);
            R("stones_va", ScatterLayer.Cover, VA, 0.03f, G(GroundClass.Earth, GroundClass.Gravel, GroundClass.Sand, GroundClass.Steppe), minUp: 0.6f, clump: 4, contrast: 1.8f);
            R("rocks_va", ScatterLayer.Large, VA, 0.03f, G(GroundClass.Earth, GroundClass.Gravel, GroundClass.Steppe, GroundClass.Sand), minUp: 0.6f, clump: 14, contrast: 1.5f);
            R("skull", ScatterLayer.Large, VA, 0.004f, G(GroundClass.Sand, GroundClass.Earth, GroundClass.Steppe), minUp: 0.85f, clump: 30, contrast: 1.0f, under: CanopyRule.Open);
            return c;
        }
    }

    /// <summary>G3 layer textures/materials → <see cref="GroundClass"/>.</summary>
    public static class ScatterGround
    {
        static bool Has(string n, string s) => n.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>The class of one layer from its G3 texture name (G3_Nature_Ground_Forest_01_Diffuse_01 ...) or material name.</summary>
        public static GroundClass Classify(string name)
        {
            if (string.IsNullOrEmpty(name)) return GroundClass.None;
            string n = name;
            if (Has(n, "Ocean") || Has(n, "Water")) return GroundClass.Water;
            if (Has(n, "Dungeon")) return GroundClass.Dungeon;
            if (Has(n, "Snow_Path") || Has(n, "City_Snow") || Has(n, "Snowpath")) return GroundClass.Path;
            if (Has(n, "Ground_City") || Has(n, "Cityground") || Has(n, "Architecture") || Has(n, "Marble") || Has(n, "GroundPlates") ||
                Has(n, "Groundstone") || Has(n, "SandGround") || Has(n, "SandWall") || Has(n, "Bricks") || Has(n, "Stair") || Has(n, "House")) return GroundClass.Town;
            if (Has(n, "Forest_Snowground")) return GroundClass.SnowForest;
            if (Has(n, "Snow") || Has(n, "Ice")) return GroundClass.Snow;
            if (Has(n, "Path")) return GroundClass.Path;
            if (Has(n, "Forest")) return GroundClass.Forest;
            if (Has(n, "Grass_07") || Has(n, "SpainGrass")) return GroundClass.Steppe;
            if (Has(n, "Grass")) return GroundClass.Meadow;
            if (Has(n, "Riverstone")) return GroundClass.Riverstone;
            if (Has(n, "Gravel")) return GroundClass.Gravel;
            if (Has(n, "Earth")) return GroundClass.Earth;
            if (Has(n, "Dunes") || Has(n, "Sand")) return GroundClass.Sand;
            if (Has(n, "Wall_Stone") || Has(n, "Rock") || Has(n, "Stone") || Has(n, "Mountain")) return GroundClass.Rock;
            return GroundClass.None;
        }

        /// <summary>Layer classes of a terrain material: (A, B) from the layer textures when known, else from the material name
        /// (single-layer materials: B = A).</summary>
        public static (GroundClass a, GroundClass b) Layers(string material, string textureA, string textureB, bool singleLayer)
        {
            var a = Classify(textureA);
            if (a == GroundClass.None) a = Classify(material);
            var b = singleLayer ? a : Classify(textureB);
            if (b == GroundClass.None) b = a;
            return (a, b);
        }

        /// <summary>The land from the manifest region name ("Myrtana", "Nordmar", "Varant"); unknown = Myrtana.</summary>
        public static ScatterBiome Biome(string region)
        {
            if (!string.IsNullOrEmpty(region))
            {
                if (Has(region, "Nordmar")) return ScatterBiome.Nordmar;
                if (Has(region, "Varant")) return ScatterBiome.Varant;
            }
            return ScatterBiome.Myrtana;
        }

        /// <summary>No scatter at all on these (towns, paths keep only pebbles by their own rule, dungeons, water).</summary>
        public static bool Barren(GroundClass c) => c == GroundClass.None || c == GroundClass.Town || c == GroundClass.Dungeon || c == GroundClass.Water;
    }

    /// <summary>The options page's vegetation quality (0 off, 1 low, 2 medium, 3 high) → scatter density and range.</summary>
    public static class ScatterQuality
    {
        /// <summary>Candidate density factor (also thins the far field): low 0.45, medium 0.7, high 1.</summary>
        public static float Density(int vegetationQuality) =>
            vegetationQuality <= 0 ? 0f : vegetationQuality == 1 ? 0.45f : vegetationQuality == 2 ? 0.7f : 1f;

        /// <summary>View range factor: low 0.7, medium 0.85, high 1.</summary>
        public static float Range(int vegetationQuality) =>
            vegetationQuality <= 0 ? 0f : vegetationQuality == 1 ? 0.7f : vegetationQuality == 2 ? 0.85f : 1f;

        /// <summary>Shadows of the scatter only on high.</summary>
        public static bool Shadows(int vegetationQuality) => vegetationQuality >= 3;
    }
}
