using System;
using System.Collections.Generic;

namespace G3.Player
{
    /// <summary>
    /// One Gothic 3 water zone (a water mesh with gCWaterZone_PS: sea cell, lake, river stretch), metres, Unity axes
    /// (G3 axes × 0.01). The water surface is a plane through (CX, CY, CZ) with unit normal (NX, NY, NZ); the zone covers
    /// the mesh's footprint inside its box and radius (gCWaterZone_PS Test3DPointInWaterAABBox / InWaterRadius /
    /// UnderWaterMesh; Docs/hero.md "Water and swimming"). Pure: no engine types (tested in Tools/G3Tests).
    /// </summary>
    public sealed class WaterZone
    {
        public string Name;
        public float NX, NY = 1, NZ;
        public float CX, CY, CZ;
        /// <summary>Squared 2D radius around the centre, m² (gCWaterZone_PS SquareRadius).</summary>
        public float RadiusSq = float.MaxValue;
        public float MinX, MaxX, MinZ, MaxZ;
        /// <summary>Footprint triangles projected on XZ: x0, z0, x1, z1, x2, z2 per triangle (import only; see <see cref="Rasterize"/>).</summary>
        public float[] Triangles;
        /// <summary>Footprint raster over the box (bit per cell, row-major from MinX/MinZ), used when set; null with no triangles = the whole box.</summary>
        public byte[] Mask;
        public float MaskCell;
        public int MaskW, MaskH;

        // Footprint grid: triangles bucketed into G×G cells over the box.
        int _grid;
        int[] _cellStart, _cellTris;

        /// <summary>Height of the water surface above (x, z).</summary>
        public float SurfaceY(float x, float z) =>
            NY > 0.05f ? CY - (NX * (x - CX) + NZ * (z - CZ)) / NY : CY;

        /// <summary>Whether (x, z) lies on this zone's water (box, radius, then the mesh footprint).</summary>
        public bool Covers(float x, float z)
        {
            if (x < MinX || x > MaxX || z < MinZ || z > MaxZ) return false;
            float dx = x - CX, dz = z - CZ;
            if (dx * dx + dz * dz > RadiusSq) return false;
            if (Mask != null)
            {
                int mx = Math.Clamp((int)((x - MinX) / MaskCell), 0, MaskW - 1), mz = Math.Clamp((int)((z - MinZ) / MaskCell), 0, MaskH - 1);
                int bit = mz * MaskW + mx;
                return (Mask[bit >> 3] & (1 << (bit & 7))) != 0;
            }
            if (Triangles == null) return true;
            if (_cellStart == null) BuildGrid();
            int cell = CellOf(x, z);
            for (int i = _cellStart[cell]; i < _cellStart[cell + 1]; i++)
                if (InTriangle(_cellTris[i], x, z)) return true;
            return false;
        }

        public int TriangleCount => Triangles == null ? 0 : Triangles.Length / 6;

        bool InTriangle(int t, float x, float z)
        {
            var p = Triangles;
            int o = t * 6;
            float x0 = p[o], z0 = p[o + 1], x1 = p[o + 2], z1 = p[o + 3], x2 = p[o + 4], z2 = p[o + 5];
            float d0 = (x1 - x0) * (z - z0) - (z1 - z0) * (x - x0);
            float d1 = (x2 - x1) * (z - z1) - (z2 - z1) * (x - x1);
            float d2 = (x0 - x2) * (z - z2) - (z0 - z2) * (x - x2);
            const float eps = 1e-6f;
            bool neg = d0 < -eps || d1 < -eps || d2 < -eps;
            bool pos = d0 > eps || d1 > eps || d2 > eps;
            return !(neg && pos);
        }

        int CellOf(float x, float z)
        {
            float w = Math.Max(MaxX - MinX, 1e-3f), h = Math.Max(MaxZ - MinZ, 1e-3f);
            int cx = Math.Clamp((int)((x - MinX) / w * _grid), 0, _grid - 1);
            int cz = Math.Clamp((int)((z - MinZ) / h * _grid), 0, _grid - 1);
            return cz * _grid + cx;
        }

        void BuildGrid()
        {
            int n = TriangleCount;
            _grid = Math.Clamp((int)MathF.Sqrt(n / 2f), 1, 64);
            var lists = new List<int>[_grid * _grid];
            float w = Math.Max(MaxX - MinX, 1e-3f), h = Math.Max(MaxZ - MinZ, 1e-3f);
            for (int t = 0; t < n; t++)
            {
                int o = t * 6;
                float x0 = Math.Min(Triangles[o], Math.Min(Triangles[o + 2], Triangles[o + 4]));
                float x1 = Math.Max(Triangles[o], Math.Max(Triangles[o + 2], Triangles[o + 4]));
                float z0 = Math.Min(Triangles[o + 1], Math.Min(Triangles[o + 3], Triangles[o + 5]));
                float z1 = Math.Max(Triangles[o + 1], Math.Max(Triangles[o + 3], Triangles[o + 5]));
                int ax = Math.Clamp((int)((x0 - MinX) / w * _grid), 0, _grid - 1), bx = Math.Clamp((int)((x1 - MinX) / w * _grid), 0, _grid - 1);
                int az = Math.Clamp((int)((z0 - MinZ) / h * _grid), 0, _grid - 1), bz = Math.Clamp((int)((z1 - MinZ) / h * _grid), 0, _grid - 1);
                for (int cz = az; cz <= bz; cz++)
                for (int cx = ax; cx <= bx; cx++)
                    (lists[cz * _grid + cx] ??= new List<int>()).Add(t);
            }
            _cellStart = new int[lists.Length + 1];
            var all = new List<int>();
            for (int i = 0; i < lists.Length; i++)
            {
                _cellStart[i] = all.Count;
                if (lists[i] != null) all.AddRange(lists[i]);
            }
            _cellStart[lists.Length] = all.Count;
            _cellTris = all.ToArray();
        }

        /// <summary>
        /// Replaces the footprint triangles by a raster of the box (a cell counts as water when its centre or any corner
        /// lies in a triangle). 210,000 triangles (5 MB) become about 1 MB of bits at 0.5–1 m. Cell size:
        /// box area / 40,000 cells, kept within [<paramref name="minCell"/>, <paramref name="maxCell"/>] m.
        /// </summary>
        public void Rasterize(float minCell = 0.5f, float maxCell = 1f)
        {
            if (Triangles == null) return;
            float w = Math.Max(MaxX - MinX, 0.01f), h = Math.Max(MaxZ - MinZ, 0.01f);
            float cell = Math.Clamp(MathF.Sqrt(w * h / 40000f), minCell, maxCell);
            int mw = Math.Max(1, (int)MathF.Ceiling(w / cell)), mh = Math.Max(1, (int)MathF.Ceiling(h / cell));
            var mask = new byte[(mw * mh + 7) / 8];
            if (_cellStart == null) BuildGrid();
            for (int iz = 0; iz < mh; iz++)
            for (int ix = 0; ix < mw; ix++)
            {
                float x0 = MinX + ix * cell, z0 = MinZ + iz * cell;
                if (CoversTris(x0 + cell * 0.5f, z0 + cell * 0.5f) || CoversTris(x0, z0) || CoversTris(x0 + cell, z0) ||
                    CoversTris(x0, z0 + cell) || CoversTris(x0 + cell, z0 + cell))
                {
                    int bit = iz * mw + ix;
                    mask[bit >> 3] |= (byte)(1 << (bit & 7));
                }
            }
            Mask = mask;
            MaskCell = cell;
            MaskW = mw;
            MaskH = mh;
            Triangles = null;
            _cellStart = null;
            _cellTris = null;
        }

        bool CoversTris(float x, float z)
        {
            int cell = CellOf(x, z);
            for (int i = _cellStart[cell]; i < _cellStart[cell + 1]; i++)
                if (InTriangle(_cellTris[i], x, z)) return true;
            return false;
        }

        /// <summary>
        /// Builds a zone from the G3 data: gCWaterZone_PS values (centimetres, G3 axes) and the water mesh's triangles
        /// (entity-local positions, cm, transformed by the row-major entity matrix <paramref name="world"/>; null = box only).
        /// </summary>
        public static WaterZone FromG3(string name, float[] normal, float[] centre, float squareRadius,
            float minX, float maxX, float minZ, float maxZ, float[] world, IEnumerable<(float[] positions, uint[] indices)> meshes)
        {
            const float s = 0.01f;
            float nl = MathF.Sqrt(normal[0] * normal[0] + normal[1] * normal[1] + normal[2] * normal[2]);
            // Six sea cells carry FaceNormal (1, 0, 0) (a degenerate first face); all water is horizontal or nearly so.
            if (nl < 1e-6f || normal[1] / nl < 0.5f) { normal = new[] { 0f, 1f, 0f }; nl = 1; }
            var z = new WaterZone
            {
                Name = name,
                NX = nl > 1e-6f ? normal[0] / nl : 0, NY = nl > 1e-6f ? normal[1] / nl : 1, NZ = nl > 1e-6f ? normal[2] / nl : 0,
                CX = centre[0] * s, CY = centre[1] * s, CZ = centre[2] * s,
                RadiusSq = squareRadius > 0 ? squareRadius * s * s : float.MaxValue,
                MinX = minX * s, MaxX = maxX * s, MinZ = minZ * s, MaxZ = maxZ * s,
            };
            if (meshes == null || world == null) return z;
            var tris = new List<float>();
            foreach (var (pos, idx) in meshes)
            {
                if (pos == null || idx == null) continue;
                for (int i = 0; i + 2 < idx.Length; i += 3)
                {
                    float ax = 0, az = 0, bx = 0, bz = 0, cx = 0, cz = 0;
                    for (int k = 0; k < 3; k++)
                    {
                        long v = idx[i + k] * 3L;
                        if (v + 2 >= pos.Length) goto skip;
                        float px = pos[v], py = pos[v + 1], pz = pos[v + 2];
                        float wx = (px * world[0] + py * world[4] + pz * world[8] + world[12]) * s;
                        float wz = (px * world[2] + py * world[6] + pz * world[10] + world[14]) * s;
                        if (k == 0) { ax = wx; az = wz; } else if (k == 1) { bx = wx; bz = wz; } else { cx = wx; cz = wz; }
                    }
                    if (MathF.Abs((bx - ax) * (cz - az) - (bz - az) * (cx - ax)) < 1e-6f) continue; // degenerate in plan view
                    tris.Add(ax); tris.Add(az); tris.Add(bx); tris.Add(bz); tris.Add(cx); tris.Add(cz);
                    skip:;
                }
            }
            if (tris.Count > 0) z.Triangles = tris.ToArray();
            return z;
        }
    }

    /// <summary>
    /// All water zones of the world with a coarse 2D grid for lookups. <see cref="Sample"/> answers "which water surface
    /// is at this spot" for the hero controller, the NPC walker and the NavMesh builder.
    /// </summary>
    public sealed class WaterMap
    {
        /// <summary>
        /// A surface higher than this above the feet is not "this" water (a lake above a cave, a cellar under the sea)
        /// [interp: G3 tests any point under the water mesh; 10 m keeps a hero who reached the sea floor in the water].
        /// </summary>
        public const float MaxSurfaceAboveFeet = 10f;
        const float CellSize = 64f;

        readonly List<WaterZone> _zones = new List<WaterZone>();
        readonly Dictionary<long, List<int>> _grid = new Dictionary<long, List<int>>();

        public IReadOnlyList<WaterZone> Zones => _zones;

        public WaterMap() { }
        public WaterMap(IEnumerable<WaterZone> zones) { foreach (var z in zones) Add(z); }

        public void Add(WaterZone zone)
        {
            int index = _zones.Count;
            _zones.Add(zone);
            int x0 = (int)MathF.Floor(zone.MinX / CellSize), x1 = (int)MathF.Floor(zone.MaxX / CellSize);
            int z0 = (int)MathF.Floor(zone.MinZ / CellSize), z1 = (int)MathF.Floor(zone.MaxZ / CellSize);
            if ((long)(x1 - x0 + 1) * (z1 - z0 + 1) > 1_000_000) return; // broken box
            for (int z = z0; z <= z1; z++)
            for (int x = x0; x <= x1; x++)
            {
                long key = Key(x, z);
                if (!_grid.TryGetValue(key, out var list)) _grid[key] = list = new List<int>(2);
                list.Add(index);
            }
        }

        static long Key(int x, int z) => ((long)x << 32) ^ (uint)z;

        /// <summary>
        /// The water surface at (x, z) for something whose feet are at <paramref name="feetY"/>: of the zones covering the
        /// spot, the highest surface at most <paramref name="maxAbove"/> (default <see cref="MaxSurfaceAboveFeet"/>) above
        /// the feet. False when there is none.
        /// </summary>
        public bool Sample(float x, float feetY, float z, out float surfaceY, out WaterZone zone, float maxAbove = MaxSurfaceAboveFeet)
        {
            surfaceY = float.NegativeInfinity;
            zone = null;
            if (!_grid.TryGetValue(Key((int)MathF.Floor(x / CellSize), (int)MathF.Floor(z / CellSize)), out var list)) return false;
            foreach (int i in list)
            {
                var w = _zones[i];
                if (!w.Covers(x, z)) continue;
                float y = w.SurfaceY(x, z);
                if (y > feetY + maxAbove || y <= surfaceY) continue;
                surfaceY = y;
                zone = w;
            }
            return zone != null;
        }
    }
}

namespace G3.Player
{
    /// <summary>
    /// Binary file of the imported water zones (written by the editor import, read at runtime): "G3WZ", version, count,
    /// then per zone the name, plane, radius, box and footprint (raster, or triangles when not rasterized).
    /// </summary>
    public static class WaterMapFile
    {
        const uint Magic = 0x5A573347; // "G3WZ"
        const int Version = 1;

        public static void Write(System.IO.Stream stream, System.Collections.Generic.IEnumerable<WaterZone> zones)
        {
            var list = new System.Collections.Generic.List<WaterZone>(zones);
            using var w = new System.IO.BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            w.Write(Magic);
            w.Write(Version);
            w.Write(list.Count);
            foreach (var z in list)
            {
                w.Write(z.Name ?? "");
                foreach (var f in new[] { z.NX, z.NY, z.NZ, z.CX, z.CY, z.CZ, z.RadiusSq, z.MinX, z.MaxX, z.MinZ, z.MaxZ }) w.Write(f);
                if (z.Mask != null)
                {
                    w.Write((byte)1);
                    w.Write(z.MaskCell);
                    w.Write(z.MaskW);
                    w.Write(z.MaskH);
                    w.Write(z.Mask.Length);
                    w.Write(z.Mask);
                }
                else if (z.Triangles != null)
                {
                    w.Write((byte)2);
                    w.Write(z.Triangles.Length);
                    foreach (var f in z.Triangles) w.Write(f);
                }
                else w.Write((byte)0);
            }
        }

        public static WaterMap Read(System.IO.Stream stream)
        {
            using var r = new System.IO.BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            if (r.ReadUInt32() != Magic) throw new System.IO.InvalidDataException("not a G3 water zone file");
            int version = r.ReadInt32();
            if (version != Version) throw new System.IO.InvalidDataException($"water zone file version {version}");
            int count = r.ReadInt32();
            var map = new WaterMap();
            for (int i = 0; i < count; i++)
            {
                var z = new WaterZone { Name = r.ReadString() };
                z.NX = r.ReadSingle(); z.NY = r.ReadSingle(); z.NZ = r.ReadSingle();
                z.CX = r.ReadSingle(); z.CY = r.ReadSingle(); z.CZ = r.ReadSingle();
                z.RadiusSq = r.ReadSingle();
                z.MinX = r.ReadSingle(); z.MaxX = r.ReadSingle(); z.MinZ = r.ReadSingle(); z.MaxZ = r.ReadSingle();
                switch (r.ReadByte())
                {
                    case 1:
                        z.MaskCell = r.ReadSingle();
                        z.MaskW = r.ReadInt32();
                        z.MaskH = r.ReadInt32();
                        z.Mask = r.ReadBytes(r.ReadInt32());
                        break;
                    case 2:
                        z.Triangles = new float[r.ReadInt32()];
                        for (int k = 0; k < z.Triangles.Length; k++) z.Triangles[k] = r.ReadSingle();
                        break;
                }
                map.Add(z);
            }
            return map;
        }
    }
}
