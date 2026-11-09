using System;
using System.Collections.Generic;

namespace G3.Unity
{
    /// <summary>The ground under one point (the top landscape triangle at x, z): height, normal, the two layer classes and
    /// G3's vertex blend between them (alpha 0 = layer A, 1 = layer B).</summary>
    public struct GroundSample
    {
        public float Y;
        public float Nx, Ny, Nz;
        public GroundClass A, B;
        public float Alpha;
        /// <summary>Tree cover over the point (0..1): broadleaf and conifer crowns of G3's SpeedTrees (<see cref="ScatterCanopy"/>).</summary>
        public float Broad, Conifer;
        public float Canopy => Math.Max(Broad, Conifer);

        /// <summary>Share (0..1) of the ground that is one of <paramref name="classes"/>.</summary>
        public float Weight(GroundClass[] classes)
        {
            float w = 0;
            if (classes == null) return 0;
            bool a = false, b = false;
            foreach (var c in classes) { if (c == A) a = true; if (c == B) b = true; }
            if (a) w += 1 - Alpha;
            if (b) w += Alpha;
            return w;
        }

        /// <summary>Share of barren ground (towns, dungeons, water, unknown).</summary>
        public float Barren => (ScatterGround.Barren(A) ? 1 - Alpha : 0) + (ScatterGround.Barren(B) ? Alpha : 0);
    }

    /// <summary>The landscape for the sampler (runtime: <see cref="ScatterTerrainIndex"/> over G3's cells).</summary>
    public interface IScatterGround
    {
        bool Sample(float x, float z, out GroundSample s);
    }

    /// <summary>Water surfaces (runtime: G3's WaterMap).</summary>
    public interface IScatterWater
    {
        /// <summary>A water surface covering (x, z) from 1.5 m below <paramref name="y"/> up to 10 m above it.</summary>
        bool Surface(float x, float y, float z, out float surfaceY);
    }

    /// <summary>One placed thing (world space).</summary>
    public struct ScatterInstance
    {
        public float X, Y, Z;
        public float Scale;
        public float Qx, Qy, Qz, Qw;
        /// <summary>Tint 0xAARRGGBB (G3's vegetation colour convention, 0xFF808080-ish; white = untinted).</summary>
        public uint Color;
        public ushort Item;
        public byte Variant;
        public byte Layer;
    }

    /// <summary>Everything one tile needs besides the catalog (main thread fills it, the worker samples).</summary>
    public sealed class ScatterTileInput
    {
        public int Tx, Tz;
        public float TileSize = 16f;
        public ScatterBiome Biome;
        public float Density = 1f;
        /// <summary>Occupancy: the first hit height of a straight-down ray per cell (NaN = nothing hit), row-major over the tile,
        /// <see cref="OccN"/>² cells. A candidate is blocked when something stands higher than its ground there (houses, rocks,
        /// bridges, overhangs, trunks). Null = no occupancy test.</summary>
        public float[] OccTop;
        public int OccN;
        public IScatterGround Ground;
        public IScatterWater Water;
        /// <summary>G3's local vegetation colour at (x, z) (0 = unknown → <see cref="ScatterSampler.DefaultTint"/>).</summary>
        public Func<float, float, uint> Tint;
        /// <summary>G3's trees round the tile (null = no canopy rules).</summary>
        public ScatterCanopy Canopy;
        /// <summary>Places kept clear for NPCs (routine stations, benches, campfires; null = none).</summary>
        public ScatterKeepOut KeepOut;
    }

    /// <summary>
    /// Discs the scatter keeps clear (pass 2, Docs section 18.2): NPC routine stations and the interactive objects they use
    /// (benches, campfires, anvils ...), so no fern or bed sits where an NPC stands, sits or works. Bucketed on an 8 m grid;
    /// immutable once handed to the worker (the runtime swaps the whole set).
    /// </summary>
    public sealed class ScatterKeepOut
    {
        const float Cell = 8f;
        readonly Dictionary<long, List<(float x, float z, float r2)>> _cells = new Dictionary<long, List<(float, float, float)>>();
        public int Count { get; private set; }

        static long Key(int i, int j) => ((long)i << 32) ^ (uint)j;

        /// <summary>Adds a disc of <paramref name="radius"/> m round (x, z).</summary>
        public void Add(float x, float z, float radius)
        {
            if (radius <= 0 || float.IsNaN(x) || float.IsNaN(z)) return;
            int i0 = (int)Math.Floor((x - radius) / Cell), i1 = (int)Math.Floor((x + radius) / Cell);
            int j0 = (int)Math.Floor((z - radius) / Cell), j1 = (int)Math.Floor((z + radius) / Cell);
            for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++)
                {
                    if (!_cells.TryGetValue(Key(i, j), out var l)) _cells[Key(i, j)] = l = new List<(float, float, float)>();
                    l.Add((x, z, radius * radius));
                }
            Count++;
        }

        /// <summary>True inside any disc.</summary>
        public bool Blocked(float x, float z)
        {
            if (!_cells.TryGetValue(Key((int)Math.Floor(x / Cell), (int)Math.Floor(z / Cell)), out var l)) return false;
            foreach (var (cx, cz, r2) in l)
            {
                float dx = x - cx, dz = z - cz;
                if (dx * dx + dz * dz < r2) return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Deterministic placement (Docs/remaster-world.md section 18.2): per layer a world-aligned jittered grid of candidates
    /// (stable however the world is tiled), the ground under each candidate (class weights from G3's layer blend, slope),
    /// per rule a chance = rule chance x ground weight x slope window x clump noise x shore factor x density, one item at most
    /// per candidate (a random number against the cumulative chances: "nothing" keeps its share). Blocked by the occupancy
    /// heights (buildings, rocks, bridges) and by water deeper than <see cref="ShallowDepth"/>.
    /// </summary>
    public static class ScatterSampler
    {
        /// <summary>Water up to this depth (m) still takes shore plants (reeds); deeper ground is skipped.</summary>
        public const float ShallowDepth = 0.45f;
        /// <summary>Dry ground within this distance (m) of water counts as shore.</summary>
        public const float ShoreReach = 2.2f;
        /// <summary>A hit this much (m) above the candidate's ground blocks it.</summary>
        public const float OccupiedAbove = 0.3f;
        /// <summary>Untinted G3 vegetation colour (the shader multiplies by 2 x this ~ 1).</summary>
        public const uint DefaultTint = 0xFF808080u;

        // ------------------------------------------------------------------ hashing and noise (shared with tests)

        public static uint Hash(uint x)
        {
            x ^= x >> 16; x *= 0x7FEB352Du; x ^= x >> 15; x *= 0x846CA68Bu; x ^= x >> 16;
            return x;
        }

        public static uint Hash(int a, int b, uint salt) => Hash((uint)a * 0x9E3779B1u ^ Hash((uint)b * 0x85EBCA77u ^ salt));

        public static float Hash01(uint h) => (h & 0xFFFFFF) / 16777216f;

        public static uint StringSalt(string s)
        {
            uint h = 2166136261u;
            if (s != null) foreach (char ch in s) { h ^= ch; h *= 16777619u; }
            return Hash(h);
        }

        static float Smooth(float t) => t * t * (3 - 2 * t);

        /// <summary>Value noise 0..1 with wavelength <paramref name="scale"/> m (two octaves, smooth).</summary>
        public static float Noise(float x, float z, float scale, uint salt)
        {
            float N1(float px, float pz, uint s)
            {
                int ix = (int)Math.Floor(px), iz = (int)Math.Floor(pz);
                float fx = Smooth(px - ix), fz = Smooth(pz - iz);
                float a = Hash01(Hash(ix, iz, s)), b = Hash01(Hash(ix + 1, iz, s));
                float c = Hash01(Hash(ix, iz + 1, s)), d = Hash01(Hash(ix + 1, iz + 1, s));
                return (a + (b - a) * fx) + ((c + (d - c) * fx) - (a + (b - a) * fx)) * fz;
            }
            float k = 1f / Math.Max(scale, 0.01f);
            return N1(x * k, z * k, salt) * 0.7f + N1(x * k * 2.31f + 17.3f, z * k * 2.31f - 9.1f, salt ^ 0xA511E9B3u) * 0.3f;
        }

        public static float SmoothStep(float a, float b, float x)
        {
            if (Math.Abs(b - a) < 1e-6f) return x >= b ? 1 : 0;
            float t = Math.Max(0, Math.Min(1, (x - a) / (b - a)));
            return Smooth(t);
        }

        // ------------------------------------------------------------------ rules

        /// <summary>The rules of one layer and land with their item index and noise salt (precomputed per catalog).</summary>
        public sealed class Prepared
        {
            public ScatterCatalog Catalog;
            public List<(ScatterRule rule, int item, uint salt)>[,] ByLayerBiome;
            public bool[] LayerNeedsShore;
        }

        public static Prepared Prepare(ScatterCatalog c)
        {
            var p = new Prepared { Catalog = c, ByLayerBiome = new List<(ScatterRule, int, uint)>[3, 3], LayerNeedsShore = new bool[3] };
            for (int l = 0; l < 3; l++)
                for (int b = 0; b < 3; b++)
                    p.ByLayerBiome[l, b] = new List<(ScatterRule, int, uint)>();
            for (int r = 0; r < c.Rules.Count; r++)
            {
                var rule = c.Rules[r];
                int item = c.IndexOf(rule.Item);
                if (item < 0) continue;
                uint salt = StringSalt(rule.Item) ^ Hash((uint)r + 101u);
                for (int b = 0; b < 3; b++)
                    if (rule.InBiome((ScatterBiome)b)) p.ByLayerBiome[(int)rule.Layer, b].Add((rule, item, salt));
                if (rule.Shore || Math.Abs(rule.ShoreFactor - 1) > 1e-3f) p.LayerNeedsShore[(int)rule.Layer] = true;
            }
            return p;
        }

        /// <summary>Chance of one rule at a candidate (before the density option).</summary>
        public static float Chance(ScatterRule rule, in GroundSample g, float x, float z, uint salt, bool shore)
        {
            if (rule.Shore && !shore) return 0;
            float w = g.Weight(rule.Grounds);
            if (w <= 0) return 0;
            if (rule.Avoid != null)
            {
                w *= Math.Max(0, 1 - 3 * g.Weight(rule.Avoid));   // fix-169: off rock (a third of the blend = none)
                if (w <= 0) return 0;
            }
            if (rule.Under != CanopyRule.Any)
            {
                float c = rule.Under == CanopyRule.Broadleaf ? g.Broad : rule.Under == CanopyRule.Conifer ? g.Conifer
                        : rule.Under == CanopyRule.Open ? 1 - g.Canopy : g.Canopy;
                w *= c;
                if (w <= 0) return 0;
            }
            float slope = SmoothStep(rule.MinUp - 0.06f, rule.MinUp, g.Ny) * (g.Ny <= rule.MaxUp ? 1 : 0);
            if (slope <= 0) return 0;
            float clump = 1;
            if (rule.ClumpContrast > 0)
            {
                float n = Noise(x, z, rule.Clump, salt);
                // contrast 1: 0 .. 2 (squared towards the low end: real patches with bare ground between)
                float v = n * 2 - 1;
                clump = Math.Max(0, 1 + rule.ClumpContrast * (v >= 0 ? v : -(v * v) - (-v) * 0.6f));
            }
            float s = !rule.Shore && shore ? rule.ShoreFactor : 1;
            return rule.Chance * w * slope * clump * s;
        }

        // ------------------------------------------------------------------ tiles

        /// <summary>Candidate cells of one layer inside a tile (world-aligned grid of the layer's spacing).</summary>
        public static void CellRange(int tile, float tileSize, float spacing, out int c0, out int c1)
        {
            // a cell belongs to the tile that holds its centre
            c0 = (int)Math.Ceiling(tile * tileSize / spacing - 0.5f);
            c1 = (int)Math.Ceiling((tile + 1) * tileSize / spacing - 0.5f) - 1;
        }

        /// <summary>Places one layer of one tile into <paramref name="output"/> (appended). Returns the number placed.</summary>
        public static int Tile(Prepared p, ScatterTileInput t, ScatterLayer layer, List<ScatterInstance> output)
        {
            var rules = p.ByLayerBiome[(int)layer, (int)t.Biome];
            if (rules.Count == 0 || t.Density <= 0 || t.Ground == null) return 0;
            float sp = p.Catalog.Spacing[(int)layer];
            CellRange(t.Tx, t.TileSize, sp, out int i0, out int i1);
            CellRange(t.Tz, t.TileSize, sp, out int j0, out int j1);
            uint layerSalt = 0x51ED27u * (uint)(layer + 1);
            bool needShore = p.LayerNeedsShore[(int)layer] && t.Water != null;
            float tileX0 = t.Tx * t.TileSize, tileZ0 = t.Tz * t.TileSize;
            int placed = 0;
            var chances = new float[rules.Count];
            for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++)
                {
                    uint h = Hash(i, j, layerSalt);
                    float x = (i + 0.5f + (Hash01(h) - 0.5f) * 0.9f) * sp;
                    float z = (j + 0.5f + (Hash01(Hash(h ^ 0x68E31DA4u)) - 0.5f) * 0.9f) * sp;
                    if (!t.Ground.Sample(x, z, out var g)) continue;
                    if (t.Canopy != null) t.Canopy.At(x, z, out g.Broad, out g.Conifer);
                    if (g.Barren >= 0.999f) continue;
                    if (Blocked(t, x - tileX0, z - tileZ0, g.Y)) continue;
                    if (t.KeepOut != null && t.KeepOut.Blocked(x, z)) continue;
                    bool shore = false;
                    if (t.Water != null)
                    {
                        if (t.Water.Surface(x, g.Y, z, out float sy) && sy > g.Y + 0.02f)
                        {
                            if (sy - g.Y > ShallowDepth) continue;   // under water
                            shore = true;
                        }
                        else if (needShore) shore = NearWater(t.Water, x, g.Y, z);
                    }
                    float total = 0;
                    for (int r = 0; r < rules.Count; r++)
                    {
                        var (rule, _, salt) = rules[r];
                        float c = Chance(rule, g, x, z, salt, shore) * t.Density;
                        chances[r] = c;
                        total += c;
                    }
                    if (total <= 0) continue;
                    float u = Hash01(Hash(h ^ 0xB5297A4Du));
                    float norm = total > 1 ? 1 / total : 1;
                    if (u >= total * norm) continue;
                    float acc = 0;
                    int pick = rules.Count - 1;
                    for (int r = 0; r < rules.Count; r++)
                    {
                        acc += chances[r] * norm;
                        if (u < acc) { pick = r; break; }
                    }
                    var (prule, pitem, _) = rules[pick];
                    if (prule.Fit > 0 && !Fits(t.Ground, prule, p.Catalog.Items[pitem], x, z, g)) continue;
                    output.Add(Make(p.Catalog.Items[pitem], pitem, layer, h, x, z, g, t.Tint));
                    placed++;
                }
            return placed;
        }

        /// <summary>fix-169: a large flat card fits its footprint: 4 points at 0.45 x its largest size lie within
        /// <see cref="ScatterRule.Fit"/> of the ground plane at the centre and are mostly the rule's ground, not one it avoids.</summary>
        public static bool Fits(IScatterGround ground, ScatterRule rule, ScatterItem it, float x, float z, in GroundSample g)
        {
            float r = 0.45f * Math.Max(it.SizeMax, 0.1f);
            float ny = Math.Max(g.Ny, 0.2f);
            float[] ox = { r, -r, 0, 0 }, oz = { 0, 0, r, -r };
            for (int k = 0; k < 4; k++)
            {
                if (!ground.Sample(x + ox[k], z + oz[k], out var s)) return false;
                // the plane through the centre: y = Y - (nx dx + nz dz) / ny
                float plane = g.Y - (g.Nx * ox[k] + g.Nz * oz[k]) / ny;
                if (Math.Abs(s.Y - plane) > rule.Fit) return false;
                if (s.Weight(rule.Grounds) < 0.5f) return false;
                if (rule.Avoid != null && s.Weight(rule.Avoid) > 0.33f) return false;
            }
            return true;
        }

        static bool Blocked(ScatterTileInput t, float lx, float lz, float groundY)
        {
            if (t.OccTop == null || t.OccN <= 0) return false;
            float cell = t.TileSize / t.OccN;
            int cx = Math.Max(0, Math.Min(t.OccN - 1, (int)(lx / cell)));
            int cz = Math.Max(0, Math.Min(t.OccN - 1, (int)(lz / cell)));
            float top = t.OccTop[cz * t.OccN + cx];
            return !float.IsNaN(top) && top > groundY + OccupiedAbove;
        }

        static bool NearWater(IScatterWater w, float x, float y, float z)
        {
            const float d = ShoreReach, e = ShoreReach * 0.7071f;
            float[] ox = { d, -d, 0, 0, e, -e, e, -e };
            float[] oz = { 0, 0, d, -d, e, e, -e, -e };
            for (int k = 0; k < ox.Length; k++)
                if (w.Surface(x + ox[k], y, z + oz[k], out float s) && s > y - 1.2f) return true;
            return false;
        }

        /// <summary>The instance of an item at a candidate: size, yaw, lean towards the ground normal, sink, tint.</summary>
        public static ScatterInstance Make(ScatterItem it, int item, ScatterLayer layer, uint h, float x, float z, in GroundSample g, Func<float, float, uint> tint)
        {
            uint h2 = Hash(h ^ 0x3C6EF372u), h3 = Hash(h2), h4 = Hash(h3), h5 = Hash(h4);
            float size = it.SizeMin + (it.SizeMax - it.SizeMin) * Hash01(h2);
            float yaw = Hash01(h3) * 6.2831853f;
            // up vector: lerp(world up, ground normal, align) + a random lean
            float ux = g.Nx * it.Align, uy = 1 + (g.Ny - 1) * it.Align, uz = g.Nz * it.Align;
            float lean = it.Tilt * Hash01(h4), la = Hash01(h5) * 6.2831853f;
            ux += (float)Math.Cos(la) * lean; uz += (float)Math.Sin(la) * lean;
            float ul = (float)Math.Sqrt(ux * ux + uy * uy + uz * uz);
            ux /= ul; uy /= ul; uz /= ul;
            // q = FromTo(up, u) * Yaw
            float qyx = 0, qyy = (float)Math.Sin(yaw * 0.5f), qyz = 0, qyw = (float)Math.Cos(yaw * 0.5f);
            // FromTo((0,1,0), u): axis = (uz, 0, -ux), w = 1 + uy (normalized)
            float fx = uz, fy = 0, fz = -ux, fw = 1 + uy;
            float fl = (float)Math.Sqrt(fx * fx + fy * fy + fz * fz + fw * fw);
            if (fl < 1e-6f) { fx = 1; fy = 0; fz = 0; fw = 0; fl = 1; }   // upside down: 180 deg about x
            fx /= fl; fy /= fl; fz /= fl; fw /= fl;
            // Hamilton product f * y
            float qx = fw * qyx + fx * qyw + fy * qyz - fz * qyy;
            float qy = fw * qyy - fx * qyz + fy * qyw + fz * qyx;
            float qz = fw * qyz + fx * qyy - fy * qyx + fz * qyw;
            float qw = fw * qyw - fx * qyx - fy * qyy - fz * qyz;
            uint col = it.Tint != 0 ? it.Tint : it.UseG3Tint && tint != null ? tint(x, z) : 0;
            if (col == 0) col = it.UseG3Tint ? DefaultTint : 0xFF808080u;
            // +-8 % brightness per instance
            col = Brighten(col, 0.92f + 0.16f * Hash01(Hash(h5 ^ 0x1B873593u)));
            int variants = it.Sources != null ? Math.Max(1, it.Sources.Length) : 1;
            return new ScatterInstance
            {
                X = x, Y = g.Y - it.Sink * size, Z = z, Scale = size,
                Qx = qx, Qy = qy, Qz = qz, Qw = qw,
                Color = col, Item = (ushort)item, Variant = (byte)(Hash(h ^ 0x27D4EB2Fu) % (uint)variants), Layer = (byte)layer,
            };
        }

        /// <summary>0xAARRGGBB with rgb x k (clamped), alpha kept.</summary>
        public static uint Brighten(uint c, float k)
        {
            uint Ch(int sh) => (uint)Math.Min(255, Math.Max(0, (int)Math.Round(((c >> sh) & 255) * k))) << sh;
            return (c & 0xFF000000u) | Ch(16) | Ch(8) | Ch(0);
        }
    }
}
