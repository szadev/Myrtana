using System;
using System.Collections.Generic;
using G3.Unity;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>Ground cover scatter (Docs/remaster-world.md section 18): ground classes, palette, deterministic placement,
    /// exclusions (barren ground, occupancy, water), shore plants, terrain index, tiles, tint, bending, meshes, quality.</summary>
    public class GroundScatterTests
    {
        /// <summary>Flat ground of one class pair everywhere (alpha blend constant).</summary>
        sealed class Flat : IScatterGround
        {
            public GroundClass A, B; public float Alpha, Y, Ny = 1;
            public bool Sample(float x, float z, out GroundSample s)
            {
                float nx = (float)Math.Sqrt(Math.Max(0, 1 - Ny * Ny));
                s = new GroundSample { Y = Y, Nx = nx, Ny = Ny, Nz = 0, A = A, B = B, Alpha = Alpha };
                return true;
            }
        }

        /// <summary>A lake: water surface at y = 0 for x &gt; 0.</summary>
        sealed class Lake : IScatterWater
        {
            public bool Surface(float x, float y, float z, out float surfaceY)
            {
                surfaceY = 0;
                return x > 0 && surfaceY >= y - 1.5f && surfaceY <= y + 10;
            }
        }

        static List<ScatterInstance> Place(IScatterGround g, ScatterBiome biome, ScatterLayer layer, int tx = 0, int tz = 0,
                                           float density = 1, IScatterWater water = null, float[] occ = null, int occN = 0, ScatterCatalog c = null)
        {
            var p = ScatterSampler.Prepare(c ?? ScatterCatalog.Default());
            var t = new ScatterTileInput { Tx = tx, Tz = tz, TileSize = 16, Biome = biome, Density = density, Ground = g, Water = water, OccTop = occ, OccN = occN };
            var list = new List<ScatterInstance>();
            ScatterSampler.Tile(p, t, layer, list);
            return list;
        }

        static HashSet<string> Ids(List<ScatterInstance> list, ScatterCatalog c)
        {
            var h = new HashSet<string>();
            foreach (var i in list) h.Add(c.Items[i.Item].Id);
            return h;
        }

        [Test]
        public void Classify_G3LayerImages()
        {
            Assert.That(ScatterGround.Classify("G3_Nature_Ground_Grass_01_Diffuse_01"), Is.EqualTo(GroundClass.Meadow));
            Assert.That(ScatterGround.Classify("G3_Nature_Ground_Grass_07_Diffuse_01"), Is.EqualTo(GroundClass.Steppe));
            Assert.That(ScatterGround.Classify("G3_Nature_Ground_Forest_01_Diffuse_01"), Is.EqualTo(GroundClass.Forest));
            Assert.That(ScatterGround.Classify("G3_Nature_Forest_Snowground_01_Diffuse_01"), Is.EqualTo(GroundClass.SnowForest));
            Assert.That(ScatterGround.Classify("G3_Nature_Ground_Snow_01_Diffuse_01"), Is.EqualTo(GroundClass.Snow));
            Assert.That(ScatterGround.Classify("G3_Nature_Snow_Path_01_Diffuse_01"), Is.EqualTo(GroundClass.Path));
            Assert.That(ScatterGround.Classify("G3_Nature_Earthground_01_Diffuse_02"), Is.EqualTo(GroundClass.Earth));
            Assert.That(ScatterGround.Classify("G3_Nature_Ground_Gravel_01_Diffuse_01"), Is.EqualTo(GroundClass.Gravel));
            Assert.That(ScatterGround.Classify("G3_Nature_Ground_Riverstone_02_Diffuse_01"), Is.EqualTo(GroundClass.Riverstone));
            Assert.That(ScatterGround.Classify("G3_Nature_Dunes_PlainSand_01_Diffuse_01"), Is.EqualTo(GroundClass.Sand));
            Assert.That(ScatterGround.Classify("G3_Nature_Wall_Stone_02_Diffuse_01"), Is.EqualTo(GroundClass.Rock));
            Assert.That(ScatterGround.Classify("G3_Architecture_Ground_City_03_Diffuse_01"), Is.EqualTo(GroundClass.Town));
            Assert.That(ScatterGround.Classify("G3_Varant_Dungeon_Wall_01"), Is.EqualTo(GroundClass.Dungeon));
            Assert.That(ScatterGround.Classify("G3_Nature_Ground_Path_02_Diffuse_01"), Is.EqualTo(GroundClass.Path));
            Assert.That(ScatterGround.Classify(null), Is.EqualTo(GroundClass.None));
            // material name fallback, single layer
            var (a, b) = ScatterGround.Layers("G3_Nature_Ground_Forest_01_01_B", null, null, true);
            Assert.That(a, Is.EqualTo(GroundClass.Forest)); Assert.That(b, Is.EqualTo(GroundClass.Forest));
            (a, b) = ScatterGround.Layers("G3_Nature_Wall_Stone_02_F", "G3_Nature_Wall_Stone_02_Diffuse_01", "G3_Nature_Ground_Grass_01_Diffuse_01", false);
            Assert.That(a, Is.EqualTo(GroundClass.Rock)); Assert.That(b, Is.EqualTo(GroundClass.Meadow));
            Assert.That(ScatterGround.Biome("Nordmar"), Is.EqualTo(ScatterBiome.Nordmar));
            Assert.That(ScatterGround.Biome("Varant"), Is.EqualTo(ScatterBiome.Varant));
            Assert.That(ScatterGround.Biome(null), Is.EqualTo(ScatterBiome.Myrtana));
        }

        [Test]
        public void Catalog_EveryRuleNamesAnItem_RangesPerLayer()
        {
            var c = ScatterCatalog.Default();
            foreach (var r in c.Rules) Assert.That(c.Find(r.Item), Is.Not.Null, r.Item);
            foreach (var i in c.Items)
            {
                Assert.That(i.Sources, Is.Not.Empty, i.Id);
                Assert.That(i.SizeMax, Is.GreaterThanOrEqualTo(i.SizeMin), i.Id);
                if (i.Kind == ScatterKind.Solid) Assert.That(i.Texture, Is.Not.Null.And.Not.Empty, i.Id + ": solids take a G3 rock image");
            }
            Assert.That(c.LayerRange(ScatterLayer.Cover), Is.LessThan(c.LayerRange(ScatterLayer.Plants)));
            Assert.That(c.LayerRange(ScatterLayer.Plants), Is.LessThan(c.LayerRange(ScatterLayer.Large)));
            // every land has something for the cover and the large layer
            var p = ScatterSampler.Prepare(c);
            for (int b = 0; b < 3; b++)
                for (int l = 0; l < 3; l++)
                    Assert.That(p.ByLayerBiome[l, b], Is.Not.Empty, $"land {(ScatterBiome)b} layer {(ScatterLayer)l}");
        }

        [Test]
        public void Placement_IsDeterministicAndIndependentOfTiling()
        {
            var g = new Flat { A = GroundClass.Forest, B = GroundClass.Forest };
            var a = Place(g, ScatterBiome.Myrtana, ScatterLayer.Plants, 3, -2);
            var b = Place(g, ScatterBiome.Myrtana, ScatterLayer.Plants, 3, -2);
            Assert.That(a.Count, Is.GreaterThan(20));
            Assert.That(b.Count, Is.EqualTo(a.Count));
            for (int i = 0; i < a.Count; i++) { Assert.That(b[i].X, Is.EqualTo(a[i].X)); Assert.That(b[i].Item, Is.EqualTo(a[i].Item)); }
            // the candidate grid is world aligned: a 16 m tile equals the union of its four 8 m tiles
            var p = ScatterSampler.Prepare(ScatterCatalog.Default());
            var small = new List<ScatterInstance>();
            for (int dz = 0; dz < 2; dz++)
                for (int dx = 0; dx < 2; dx++)
                    ScatterSampler.Tile(p, new ScatterTileInput { Tx = 6 + dx, Tz = -4 + dz, TileSize = 8, Ground = g, Biome = ScatterBiome.Myrtana }, ScatterLayer.Plants, small);
            Assert.That(small.Count, Is.EqualTo(a.Count));
            var set = new HashSet<(float, float)>();
            foreach (var i in a) set.Add((i.X, i.Z));
            foreach (var i in small) Assert.That(set.Contains((i.X, i.Z)), Is.True);
            // all inside the tile
            foreach (var i in a) { Assert.That(i.X, Is.InRange(48f, 64f)); Assert.That(i.Z, Is.InRange(-32f, -16f)); }
        }

        [Test]
        public void Palette_FollowsGroundAndLand()
        {
            var c = ScatterCatalog.Default();
            var forest = Ids(Place(new Flat { A = GroundClass.Forest, B = GroundClass.Forest }, ScatterBiome.Myrtana, ScatterLayer.Cover, c: c), c);
            Assert.That(forest, Does.Contain("litter_leaves"));
            Assert.That(forest, Does.Not.Contain("desert_debris"));
            var meadow = Ids(Place(new Flat { A = GroundClass.Meadow, B = GroundClass.Meadow }, ScatterBiome.Myrtana, ScatterLayer.Plants, c: c), c);
            Assert.That(meadow, Does.Contain("flowers"));
            Assert.That(meadow, Does.Not.Contain("underbrush"));
            var dunes = Ids(Place(new Flat { A = GroundClass.Sand, B = GroundClass.Sand }, ScatterBiome.Varant, ScatterLayer.Large, c: c), c);
            Assert.That(dunes, Does.Not.Contain("fern"));
            var nordmar = Place(new Flat { A = GroundClass.Snow, B = GroundClass.Snow }, ScatterBiome.Nordmar, ScatterLayer.Large, c: c);
            var forestLarge = Place(new Flat { A = GroundClass.Forest, B = GroundClass.Forest }, ScatterBiome.Myrtana, ScatterLayer.Large, c: c);
            Assert.That(nordmar.Count, Is.LessThan(forestLarge.Count), "Nordmar stays sparse");
            foreach (var id in Ids(nordmar, c)) Assert.That(id == "frozen_shrub" || id == "rocks_no" || id == "snow_rocks", Is.True, id);
        }

        [Test]
        public void Barren_Towns_And_LayerBlend()
        {
            Assert.That(Place(new Flat { A = GroundClass.Town, B = GroundClass.Town }, ScatterBiome.Myrtana, ScatterLayer.Plants), Is.Empty);
            Assert.That(Place(new Flat { A = GroundClass.Dungeon, B = GroundClass.Dungeon }, ScatterBiome.Myrtana, ScatterLayer.Cover), Is.Empty);
            // half forest, half town by G3's vertex blend: fewer than full forest, more than none
            int full = Place(new Flat { A = GroundClass.Forest, B = GroundClass.Forest }, ScatterBiome.Myrtana, ScatterLayer.Cover).Count;
            int half = Place(new Flat { A = GroundClass.Forest, B = GroundClass.Town, Alpha = 0.5f }, ScatterBiome.Myrtana, ScatterLayer.Cover).Count;
            Assert.That(half, Is.LessThan(full)); Assert.That(half, Is.GreaterThan(0));
        }

        [Test]
        public void Slope_SteepGroundTakesLess()
        {
            var flat = Place(new Flat { A = GroundClass.Meadow, B = GroundClass.Meadow, Ny = 1 }, ScatterBiome.Myrtana, ScatterLayer.Plants).Count;
            var steep = Place(new Flat { A = GroundClass.Meadow, B = GroundClass.Meadow, Ny = 0.5f }, ScatterBiome.Myrtana, ScatterLayer.Plants).Count;
            Assert.That(flat, Is.GreaterThan(0));
            Assert.That(steep, Is.EqualTo(0));
        }

        [Test]
        public void Density_ScalesTheCount()
        {
            var g = new Flat { A = GroundClass.Forest, B = GroundClass.Forest };
            int full = Place(g, ScatterBiome.Myrtana, ScatterLayer.Cover, density: 1).Count;
            int low = Place(g, ScatterBiome.Myrtana, ScatterLayer.Cover, density: ScatterQuality.Density(1)).Count;
            Assert.That(low, Is.LessThan(full * 0.65f)); Assert.That(low, Is.GreaterThan(full * 0.25f));
            Assert.That(Place(g, ScatterBiome.Myrtana, ScatterLayer.Cover, density: 0), Is.Empty);
            Assert.That(ScatterQuality.Density(0), Is.EqualTo(0f));
            Assert.That(ScatterQuality.Density(3), Is.EqualTo(1f));
            Assert.That(ScatterQuality.Density(2), Is.InRange(ScatterQuality.Density(1), 1f));
            Assert.That(ScatterQuality.Shadows(2), Is.False); Assert.That(ScatterQuality.Shadows(3), Is.True);
        }

        [Test]
        public void Occupancy_BlocksUnderHousesAndRocks()
        {
            var g = new Flat { A = GroundClass.Forest, B = GroundClass.Forest, Y = 10 };
            int n = 4;
            var occ = new float[n * n];
            for (int i = 0; i < occ.Length; i++) occ[i] = float.NaN;
            occ[0] = 14;      // a roof over the first 4 x 4 m cell
            occ[1] = 10.1f;   // ground-level hit (the terrain itself / a flat stone): not blocking
            var list = Place(g, ScatterBiome.Myrtana, ScatterLayer.Cover, occ: occ, occN: n);
            foreach (var i in list) Assert.That(i.X >= 4 || i.Z >= 4, Is.True, "nothing under the roof");
            Assert.That(list.Exists(i => i.X >= 4 && i.X < 8 && i.Z < 4), Is.True, "ground-level hit does not block");
        }

        [Test]
        public void Water_DeepSkipped_ShoreGetsReeds()
        {
            var c = ScatterCatalog.Default();
            // ground under the lake: 1 m deep for x > 0 (tile 0), dry and 0.3 m above the water at tile -1
            var deep = Place(new Flat { A = GroundClass.Meadow, B = GroundClass.Meadow, Y = -1 }, ScatterBiome.Myrtana, ScatterLayer.Large, 0, 0, water: new Lake(), c: c);
            Assert.That(deep, Is.Empty);
            var shallow = Place(new Flat { A = GroundClass.Meadow, B = GroundClass.Meadow, Y = -0.2f }, ScatterBiome.Myrtana, ScatterLayer.Large, 0, 0, water: new Lake(), c: c);
            Assert.That(Ids(shallow, c), Does.Contain("reed"));
            var dryFar = Place(new Flat { A = GroundClass.Meadow, B = GroundClass.Meadow, Y = 0.3f }, ScatterBiome.Myrtana, ScatterLayer.Large, -3, 0, water: new Lake(), c: c);
            Assert.That(Ids(dryFar, c), Does.Not.Contain("reed"), "no reeds 30+ m from the water");
            var bank = Place(new Flat { A = GroundClass.Meadow, B = GroundClass.Meadow, Y = 0.3f }, ScatterBiome.Myrtana, ScatterLayer.Large, -1, 0, water: new Lake(), c: c);
            Assert.That(bank.Exists(i => c.Items[i.Item].Id == "reed" && i.X > -ScatterSampler.ShoreReach - 0.01f), Is.True, "reeds on the bank");
            Assert.That(bank.Exists(i => c.Items[i.Item].Id == "reed" && i.X < -ScatterSampler.ShoreReach - 0.6f), Is.False);
        }

        [Test]
        public void Instance_LeansToGroundAndSinks()
        {
            var it = new ScatterItem { Id = "x", Sources = new[] { "a" }, SizeMin = 1, SizeMax = 1, Align = 1, Tilt = 0, Sink = 0.25f };
            var g = new GroundSample { Y = 5, Nx = 0.6f, Ny = 0.8f, Nz = 0, A = GroundClass.Forest, B = GroundClass.Forest };
            var s = ScatterSampler.Make(it, 0, ScatterLayer.Large, 12345u, 1, 2, g, null);
            // rotate (0,1,0) by q: must be the ground normal
            float qx = s.Qx, qy = s.Qy, qz = s.Qz, qw = s.Qw;
            Assert.That(qx * qx + qy * qy + qz * qz + qw * qw, Is.EqualTo(1f).Within(1e-4));
            float ux = 2 * (qx * qy - qw * qz), uy = 1 - 2 * (qx * qx + qz * qz), uz = 2 * (qy * qz + qw * qx);
            Assert.That(ux, Is.EqualTo(0.6f).Within(1e-3)); Assert.That(uy, Is.EqualTo(0.8f).Within(1e-3)); Assert.That(uz, Is.EqualTo(0f).Within(1e-3));
            Assert.That(s.Y, Is.EqualTo(4.75f).Within(1e-5));
            // tint: G3's local colour when asked for, brightness jitter <= 8 %
            var t = ScatterSampler.Make(new ScatterItem { Id = "y", Sources = new[] { "a" }, UseG3Tint = true }, 0, ScatterLayer.Plants, 7u, 0, 0, g, (x, z) => 0xFF406020u);
            Assert.That((t.Color >> 8) & 255, Is.InRange(0x60 * 0.9, 0x60 * 1.1));
        }

        [Test]
        public void TerrainIndex_TopTriangle_Classes_AlphaBlend()
        {
            var idx = new ScatterTerrainIndex();
            // a 10 x 10 m quad at y 0..1 (sloped in x), forest|meadow blend by alpha 0 at x=0 .. 1 at x=10
            var pos = new float[] { 0, 0, 0, 10, 1, 0, 0, 0, 10, 10, 1, 10 };
            var alpha = new float[] { 0, 1, 0, 1 };
            idx.Add(1, pos, alpha, new[] { new[] { 0, 2, 1, 1, 2, 3 } }, new[] { (GroundClass.Forest, GroundClass.Meadow) });
            Assert.That(idx.Sample(5, 5, out var s), Is.True);
            Assert.That(s.Y, Is.EqualTo(0.5f).Within(1e-4));
            Assert.That(s.Alpha, Is.EqualTo(0.5f).Within(1e-4));
            Assert.That(s.Ny, Is.GreaterThan(0.99f));
            Assert.That(s.A, Is.EqualTo(GroundClass.Forest)); Assert.That(s.B, Is.EqualTo(GroundClass.Meadow));
            Assert.That(idx.Sample(15, 5, out _), Is.False);
            // an overlapping copy of the same triangles (G3's overlapping cells): same answer, no doubling
            idx.Add(2, (float[])pos.Clone(), alpha, new[] { new[] { 0, 2, 1, 1, 2, 3 } }, new[] { (GroundClass.Forest, GroundClass.Meadow) });
            Assert.That(idx.Sample(5, 5, out var s2), Is.True); Assert.That(s2.Y, Is.EqualTo(s.Y));
            // a town floor above: towns are not indexed (nothing placed there)... a higher ground layer wins
            idx.Add(3, new float[] { 0, 3, 0, 10, 3, 0, 0, 3, 10, 10, 3, 10 }, null, new[] { new[] { 0, 2, 1, 1, 2, 3 } }, new[] { (GroundClass.Rock, GroundClass.Rock) });
            Assert.That(idx.Sample(5, 5, out var s3), Is.True); Assert.That(s3.Y, Is.EqualTo(3f).Within(1e-4)); Assert.That(s3.A, Is.EqualTo(GroundClass.Rock));
            idx.Remove(3);
            Assert.That(idx.Sample(5, 5, out var s4), Is.True); Assert.That(s4.Y, Is.EqualTo(0.5f).Within(1e-4));
            Assert.That(idx.Bounds(1, out float x0, out _, out float x1, out _), Is.True);
            Assert.That(x0, Is.EqualTo(0f)); Assert.That(x1, Is.EqualTo(10f));
        }

        [Test]
        public void Tiles_WantedNearestFirst_LayersByRange_Hysteresis()
        {
            var w = ScatterTiles.Wanted(8, 8, 16, new[] { 20f, 40f, 100f });
            Assert.That(w[0].tx, Is.EqualTo(0)); Assert.That(w[0].tz, Is.EqualTo(0)); Assert.That(w[0].layers, Is.EqualTo(7));
            for (int i = 1; i < w.Count; i++) Assert.That(w[i].dist, Is.GreaterThanOrEqualTo(w[i - 1].dist));
            foreach (var t in w)
            {
                if ((t.layers & 1) != 0) Assert.That(t.dist, Is.LessThanOrEqualTo(20f));
                Assert.That(t.dist, Is.LessThanOrEqualTo(100f));
            }
            Assert.That(ScatterTiles.Drop(3, 0, 16, 8, 8, 20, 8), Is.True);   // 40 m away > 28
            Assert.That(ScatterTiles.Drop(2, 0, 16, 8, 8, 20, 8), Is.False);  // 24 m away <= 28 (hysteresis)
            long k = ScatterTiles.Key(-5, 7);
            Assert.That(ScatterTiles.KeyX(k), Is.EqualTo(-5)); Assert.That(ScatterTiles.KeyZ(k), Is.EqualTo(7));
        }

        [Test]
        public void TintGrid_LocalAverage_NearestFallback()
        {
            var g = new ScatterTintGrid();
            g.Add(1, 1, 0xFF102030u); g.Add(2, 2, 0xFF304050u);
            Assert.That(g.At(3, 3), Is.EqualTo(0xFF203040u));
            Assert.That(g.At(12, 3), Is.EqualTo(0xFF203040u), "neighbouring cell");
            Assert.That(g.At(100, 100), Is.EqualTo(0u));
        }

        [Test]
        public void Bend_PushOutward_Recovers_Toroidal()
        {
            var (px, pz) = ScatterBend.Push(0.3f, 0, 1, 0, 0);
            Assert.That(px, Is.GreaterThan(0)); Assert.That(pz, Is.EqualTo(0f).Within(1e-6));
            Assert.That(ScatterBend.Push(1.2f, 0, 1, 0, 0), Is.EqualTo((0f, 0f)));
            var (mx, mz) = ScatterBend.Push(0.3f, 0, 1, 0, 3);   // walking +z: leans along the walk
            Assert.That(mz, Is.GreaterThan(0));
            Assert.That(Math.Sqrt(mx * mx + mz * mz), Is.LessThanOrEqualTo(1.0001));
            Assert.That(ScatterBend.Decay(1, ScatterBend.Recover), Is.EqualTo((float)Math.Exp(-1)).Within(1e-5));
            Assert.That(ScatterBend.Combine((0.2f, 0), (0, 0.5f)), Is.EqualTo((0f, 0.5f)));
            Assert.That(ScatterBend.Texel(1, 64, 256), Is.EqualTo(ScatterBend.Texel(65, 64, 256)));
            Assert.That(ScatterBend.Texel(-0.1f, 64, 256), Is.EqualTo(255));
        }

        [Test]
        public void Meshes_ClosedRockOutward_CardsAndPatch()
        {
            var r = ScatterMeshes.Rock(42, 2);
            Assert.That(r.VertexCount, Is.EqualTo(162));
            Assert.That(r.Tri.Length / 3, Is.EqualTo(320));
            Assert.That(ScatterMeshes.SignedVolume6(r), Is.GreaterThan(0), "front faces outward");
            Assert.That(ScatterMeshes.Rock(1, 1).Tri.Length / 3, Is.EqualTo(80), "LOD1");
            float minY = float.MaxValue, maxY = float.MinValue;
            for (int i = 0; i < r.VertexCount; i++) { minY = Math.Min(minY, r.Pos[i * 3 + 1]); maxY = Math.Max(maxY, r.Pos[i * 3 + 1]); }
            Assert.That(minY, Is.LessThan(0)); Assert.That(maxY, Is.GreaterThan(0.2f));
            var p = ScatterMeshes.Patch(2);
            Assert.That(p.VertexCount, Is.EqualTo(9)); Assert.That(p.Tri.Length, Is.EqualTo(24));
            for (int f = 0; f < p.Tri.Length; f += 3)
            {
                // front face up: cross(b - a, c - a).y > 0
                int a = p.Tri[f] * 3, b = p.Tri[f + 1] * 3, c = p.Tri[f + 2] * 3;
                float ux = p.Pos[b] - p.Pos[a], uz = p.Pos[b + 2] - p.Pos[a + 2], vx = p.Pos[c] - p.Pos[a], vz = p.Pos[c + 2] - p.Pos[a + 2];
                Assert.That(uz * vx - ux * vz, Is.GreaterThan(0));
            }
            var x = ScatterMeshes.Cross(3);
            Assert.That(x.VertexCount, Is.EqualTo(12)); Assert.That(x.Tri.Length, Is.EqualTo(18));
        }

        [Test]
        public void Settings_JsonDefaults_Clamp()
        {
            var d = WorldLookSettings.Default;
            Assert.That(d.scatter, Is.True);
            Assert.That(d.scatterTile, Is.EqualTo(16f));
            var s = new WorldLookSettings { scatterDensity = 99, scatterTile = 1, scatterThinStart = 2 }.Clamped();
            Assert.That(s.scatterDensity, Is.EqualTo(4f)); Assert.That(s.scatterTile, Is.EqualTo(4f)); Assert.That(s.scatterThinStart, Is.LessThan(1f));
        }

        [Test]
        public void Canopy_ForestFloorFollowsTheTrees()
        {
            var cn = new ScatterCanopy();
            cn.Add(1, 8, 8, 4, false);
            cn.Add(2, 40, 8, 4, ScatterCanopy.IsConifer("G3_Speedtree_DouglasFir_02"));
            cn.At(8, 8, out float b, out float c); Assert.That(b, Is.EqualTo(1f).Within(1e-4)); Assert.That(c, Is.EqualTo(0f));
            cn.At(40, 9, out b, out c); Assert.That(c, Is.EqualTo(1f).Within(1e-4)); Assert.That(b, Is.EqualTo(0f));
            cn.At(24, 8, out b, out c); Assert.That(b + c, Is.EqualTo(0f));
            Assert.That(ScatterCanopy.IsConifer("G3_Speedtree_CarolinaBuckthorn_02"), Is.False);
            // meadow ground under a broadleaf crown takes leaf litter, the open meadow does not; flowers only in the open
            var c0 = ScatterCatalog.Default();
            var p = ScatterSampler.Prepare(c0);
            var g = new Flat { A = GroundClass.Meadow, B = GroundClass.Meadow };
            var under = new List<ScatterInstance>(); var open = new List<ScatterInstance>();
            var big = new ScatterCanopy(); big.Add(1, 8, 8, 30, false);
            ScatterSampler.Tile(p, new ScatterTileInput { Tx = 0, Tz = 0, Ground = g, Canopy = big }, ScatterLayer.Cover, under);
            ScatterSampler.Tile(p, new ScatterTileInput { Tx = 0, Tz = 0, Ground = g, Canopy = new ScatterCanopy() }, ScatterLayer.Cover, open);
            Assert.That(Ids(under, c0), Does.Contain("litter_leaves"));
            Assert.That(Ids(open, c0), Does.Not.Contain("litter_leaves"));
            var fl = new List<ScatterInstance>();
            ScatterSampler.Tile(p, new ScatterTileInput { Tx = 0, Tz = 0, Ground = g, Canopy = big }, ScatterLayer.Plants, fl);
            Assert.That(Ids(fl, c0), Does.Not.Contain("flowers"));
        }

        [Test]
        public void Pass2_ForestFloorBeds_NoGrassUnderCrowns_KeepOut_Tint()
        {
            var c0 = ScatterCatalog.Default();
            var p = ScatterSampler.Prepare(c0);
            var g = new Flat { A = GroundClass.Meadow, B = GroundClass.Meadow };
            var fir = new ScatterCanopy(); fir.Add(1, 8, 8, 30, true);
            var oak = new ScatterCanopy(); oak.Add(1, 8, 8, 30, false);
            List<ScatterInstance> T(ScatterCanopy cn, ScatterLayer l, ScatterKeepOut k = null)
            {
                var r = new List<ScatterInstance>();
                ScatterSampler.Tile(p, new ScatterTileInput { Tx = 0, Tz = 0, Ground = g, Canopy = cn, KeepOut = k }, l, r);
                return r;
            }
            // under conifers: needle beds, no grass / herbs from the meadow ground; under broadleaf: leaf litter beds
            Assert.That(Ids(T(fir, ScatterLayer.Large), c0), Does.Contain("needle_bed"));
            Assert.That(Ids(T(oak, ScatterLayer.Large), c0), Does.Contain("litter_bed"));
            Assert.That(Ids(T(fir, ScatterLayer.Plants), c0), Does.Not.Contain("grass").And.Not.Contain("herbs"));
            Assert.That(Ids(T(new ScatterCanopy(), ScatterLayer.Plants), c0), Does.Contain("grass"));
            // keep-out discs: nothing inside, the rest untouched
            var k = new ScatterKeepOut(); k.Add(8, 8, 3); k.Add(-100, -100, 1);
            Assert.That(k.Count, Is.EqualTo(2));
            Assert.That(k.Blocked(9, 9), Is.True); Assert.That(k.Blocked(8, 11.5f), Is.False); Assert.That(k.Blocked(-100.5f, -100.5f), Is.True);
            var all = T(fir, ScatterLayer.Cover); var kept = T(fir, ScatterLayer.Cover, k);
            foreach (var i in kept) Assert.That((i.X - 8) * (i.X - 8) + (i.Z - 8) * (i.Z - 8), Is.GreaterThanOrEqualTo(9f));
            Assert.That(kept.Count, Is.LessThan(all.Count)); Assert.That(kept.Count, Is.GreaterThan(all.Count / 2));
            // Nordmar's frosted grass keeps its fixed colour (not G3's green tint field)
            int fg = c0.IndexOf("frost_grass");
            Assert.That(fg, Is.GreaterThanOrEqualTo(0));
            var inst = ScatterSampler.Make(c0.Items[fg], fg, ScatterLayer.Plants, 7, 1, 1, new GroundSample { Ny = 1 }, (x, z) => 0xFF20A020u);
            Assert.That((inst.Color >> 8) & 255, Is.GreaterThan(150u));
            // Varant gets clustered dry grass, thorn scrub, pebbles and stones on its sand
            var sand = new Flat { A = GroundClass.Sand, B = GroundClass.Sand };
            var va = new HashSet<string>();
            for (int t = 0; t < 6; t++)
                foreach (var l in new[] { ScatterLayer.Cover, ScatterLayer.Plants, ScatterLayer.Large })
                    va.UnionWith(Ids(Place(sand, ScatterBiome.Varant, l, tx: t, tz: -t, c: c0), c0));
            Assert.That(va, Is.SupersetOf(new[] { "drygrass", "thorn_bush", "varant_pebbles", "stones_va" }));
        }

        /// <summary>A ledge: flat ground at y 0 for x &lt; 8, a 1 m step up beyond (layer classes per side).</summary>
        sealed class Ledge : IScatterGround
        {
            public GroundClass Low = GroundClass.Forest, High = GroundClass.Forest;
            public bool Sample(float x, float z, out GroundSample s)
            {
                bool hi = x >= 8;
                var c = hi ? High : Low;
                s = new GroundSample { Y = hi ? 1 : 0, Ny = 1, A = c, B = c };
                return true;
            }
        }

        [Test]
        public void Fix169_BedsKeepOffRockAndLedges()
        {
            var c0 = ScatterCatalog.Default();
            var p = ScatterSampler.Prepare(c0);
            var oak = new ScatterCanopy(); oak.Add(1, 8, 8, 40, false);
            string[] beds = { "litter_bed", "needle_bed", "moss_bed" };
            List<ScatterInstance> T(IScatterGround g)
            {
                var r = new List<ScatterInstance>();
                for (int tx = 0; tx < 1; tx++)
                    ScatterSampler.Tile(p, new ScatterTileInput { Tx = tx, Tz = 0, Ground = g, Canopy = oak }, ScatterLayer.Large, r);
                return r;
            }
            bool IsBed(ScatterInstance i) => Array.IndexOf(beds, c0.Items[i.Item].Id) >= 0;
            // flat forest floor under a crown: beds
            Assert.That(T(new Flat { A = GroundClass.Forest, B = GroundClass.Forest }).Exists(IsBed), Is.True);
            // rock (G3 rock layer, also half-blended with forest): no beds
            Assert.That(T(new Flat { A = GroundClass.Rock, B = GroundClass.Rock }).Exists(IsBed), Is.False);
            Assert.That(T(new Flat { A = GroundClass.Rock, B = GroundClass.Forest, Alpha = 0.5f }).Exists(IsBed), Is.False);
            // a 1 m ledge at x = 8: no bed whose footprint crosses it, beds away from it
            var ledge = T(new Ledge());
            foreach (var i in ledge)
                if (IsBed(i)) Assert.That(Math.Abs(i.X - 8), Is.GreaterThan(0.45f * c0.Items[i.Item].SizeMax), $"{c0.Items[i.Item].Id} at {i.X}");
            Assert.That(ledge.Exists(IsBed), Is.True);
            // forest below, rock on the ledge: no bed reaches onto the rock
            foreach (var i in T(new Ledge { High = GroundClass.Rock }))
                if (IsBed(i)) Assert.That(i.X, Is.LessThan(8 - 0.45f * c0.Items[i.Item].SizeMax), $"{c0.Items[i.Item].Id} at {i.X}");
        }

        [Test]
        public void Pass2_G3GrassThinsUnderCrowns()
        {
            var cn = new ScatterCanopy();
            cn.Add(1, 8, 8, 6, true); cn.Add(2, 40, 8, 6, false); cn.Add(3, 300, 8, 6, false);
            Assert.That(cn.CountIn(0, 0, 64, 64), Is.EqualTo(2));
            Assert.That(cn.Cover(8, 8), Is.EqualTo(1f).Within(1e-4)); Assert.That(cn.Cover(120, 8), Is.EqualTo(0f));
            int kept = 0, open = 0, n = 0;
            for (float x = 0; x < 4; x += 0.1f) for (float z = 0; z < 4; z += 0.1f)
            {
                n++;
                if (ScatterCanopy.KeepGrass(1, 0.3f, x, z)) kept++;
                if (ScatterCanopy.KeepGrass(0, 0.3f, x, z)) open++;
            }
            Assert.That(open, Is.EqualTo(n));
            Assert.That(kept / (float)n, Is.EqualTo(0.3f).Within(0.08f));
            Assert.That(ScatterCanopy.KeepGrass(1, 0.3f, 1.23f, 2.5f), Is.EqualTo(ScatterCanopy.KeepGrass(1, 0.3f, 1.23f, 2.5f)));
            Assert.That(WorldLookSettings.Default.scatterGrassUnderCanopy, Is.EqualTo(0.3f));
        }
    }
}
