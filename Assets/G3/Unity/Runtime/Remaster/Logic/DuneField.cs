using System;
using System.IO;

namespace G3.Unity
{
    /// <summary>One sample of the <see cref="DuneField"/>.</summary>
    public struct DuneSample
    {
        public float C;      // m: coarse change of the ground (vertical, wavelengths >= ~4 m), already x W
        public float B;      // m: the rest (crest kink, megaripples), already x W; faded by the live camera distance
        public float W;      // dune weight 0..1 (0 = untouched: objects, paths, NPCs, water, rock...)
        public float Gx, Gz; // slope (dy/dx, dy/dz) of the change (C + B), lightly smoothed: added to the shading normal
    }

    /// <summary>
    /// dune-1 (Docs/remaster-world.md section 23): the baked dune change of Varant's loose sand, a function of the world XZ
    /// position only (every cell, near and far mesh evaluates a point identically: crack-free), on a 1 m grid in sparse 32 m
    /// tiles (empty where W is 0). Built offline by <c>G3Cli dune bake</c> (<see cref="DuneShape"/>) into
    /// <c>Resources/G3DuneField.bytes</c>; read-only at runtime (any thread). The relief (TerrainRelief.Evaluate) moves the
    /// ground up by C + B, fades its own noise relief and Phong by W, and tilts the shading normal by the change's slope.
    /// </summary>
    public sealed class DuneField
    {
        public const int Tile = 32;              // grid points per tile side
        const uint Magic = 0x46443347;           // "G3DF"
        public const int Version = 2;

        public float X0, Z0, Res = 1f;           // grid point (i, j) is at (X0 + i Res, Z0 + j Res)
        public int Nx, Nz;                       // grid points
        public int TilesX, TilesZ;
        public float WindAzimuth;                // degrees (G3Wind: 0 = towards +Z, 90 = towards +X) the dunes were shaped for
        public TileData[] Tiles;                 // TilesX * TilesZ, null = empty

        public const int PointBytes = 2 + 1 + 1 + 2;

        public sealed class TileData
        {
            public short[] C = new short[Tile * Tile];
            public sbyte[] B = new sbyte[Tile * Tile];
            public byte[] W = new byte[Tile * Tile];
            public sbyte[] Gx = new sbyte[Tile * Tile], Gz = new sbyte[Tile * Tile];
            public float ScaleC, ScaleB, ScaleG;     // m (slope) per unit
        }

        public int TileCount { get { int n = 0; if (Tiles != null) foreach (var t in Tiles) if (t != null) n++; return n; } }
        /// <summary>Bytes held by the tiles (runtime memory of the field).</summary>
        public long Bytes => (long)TileCount * Tile * Tile * PointBytes + (Tiles?.Length ?? 0) * 8L;

        public static DuneField Empty(float x0, float z0, int nx, int nz, float res = 1f, float wind = 60f)
        {
            var f = new DuneField { X0 = x0, Z0 = z0, Nx = nx, Nz = nz, Res = res, WindAzimuth = wind };
            f.TilesX = (nx + Tile - 1) / Tile; f.TilesZ = (nz + Tile - 1) / Tile;
            f.Tiles = new TileData[f.TilesX * f.TilesZ];
            return f;
        }

        /// <summary>Fills the tiles from full grids (c, b m; w 0..1; gx, gz slopes); a tile whose W is 0 everywhere stays empty.</summary>
        public void Fill(float[] c, float[] b, float[] w, float[] gx, float[] gz)
        {
            for (int tz = 0; tz < TilesZ; tz++)
                for (int tx = 0; tx < TilesX; tx++)
                {
                    float mc = 0, mb = 0, mg = 0, mw = 0;
                    for (int j = tz * Tile; j < Math.Min(Nz, tz * Tile + Tile); j++)
                        for (int i = tx * Tile; i < Math.Min(Nx, tx * Tile + Tile); i++)
                        {
                            int k = j * Nx + i;
                            mw = Math.Max(mw, w[k]);
                            if (w[k] <= 0) continue;
                            mc = Math.Max(mc, Math.Abs(c[k])); mb = Math.Max(mb, Math.Abs(b[k]));
                            mg = Math.Max(mg, Math.Max(Math.Abs(gx[k]), Math.Abs(gz[k])));
                        }
                    if (mw < 0.5f / 255) { Tiles[tz * TilesX + tx] = null; continue; }
                    var t = new TileData { ScaleC = Math.Max(mc, 1e-3f) / 32767f, ScaleB = Math.Max(mb, 1e-4f) / 127f, ScaleG = Math.Max(mg, 1e-3f) / 127f };
                    for (int jj = 0; jj < Tile; jj++)
                        for (int ii = 0; ii < Tile; ii++)
                        {
                            int i = tx * Tile + ii, j = tz * Tile + jj;
                            if (i >= Nx || j >= Nz) continue;
                            int k = j * Nx + i, q = jj * Tile + ii;
                            byte wq = (byte)Math.Max(0, Math.Min(255, Math.Round(w[k] * 255)));
                            t.W[q] = wq;
                            if (wq == 0) continue;
                            t.C[q] = (short)Math.Max(-32767, Math.Min(32767, Math.Round(c[k] / t.ScaleC)));
                            t.B[q] = (sbyte)Math.Max(-127, Math.Min(127, Math.Round(b[k] / t.ScaleB)));
                            t.Gx[q] = (sbyte)Math.Max(-127, Math.Min(127, Math.Round(gx[k] / t.ScaleG)));
                            t.Gz[q] = (sbyte)Math.Max(-127, Math.Min(127, Math.Round(gz[k] / t.ScaleG)));
                        }
                    Tiles[tz * TilesX + tx] = t;
                }
        }

        TileData TileOf(int i, int j) => i < 0 || j < 0 || i >= Nx || j >= Nz ? null : Tiles[(j / Tile) * TilesX + i / Tile];

        /// <summary>
        /// Bilinear sample at world (x, z); false (all 0) where no grid point round it has a weight. C, B, W and the slope are
        /// plain bilinear (all 0 where W is 0).
        /// </summary>
        public bool Sample(float x, float z, out DuneSample s)
        {
            s = default;
            float fx = (x - X0) / Res, fz = (z - Z0) / Res;
            if (!(fx > -1 && fz > -1 && fx < Nx && fz < Nz)) return false;
            int i = (int)Math.Floor(fx), j = (int)Math.Floor(fz);
            float u = fx - i, v = fz - j;
            float wsum = 0, c = 0, b = 0, gx = 0, gz = 0;
            for (int q = 0; q < 4; q++)
            {
                int pi = i + (q & 1), pj = j + (q >> 1);
                var t = TileOf(pi, pj);
                if (t == null) continue;
                int o = (pj % Tile) * Tile + pi % Tile;
                if (t.W[o] == 0) continue;
                float k = ((q & 1) != 0 ? u : 1 - u) * ((q >> 1) != 0 ? v : 1 - v);
                float kw = k * t.W[o] * (1f / 255);
                wsum += kw;
                c += k * t.C[o] * t.ScaleC;
                b += k * t.B[o] * t.ScaleB;
                gx += k * t.Gx[o] * t.ScaleG; gz += k * t.Gz[o] * t.ScaleG;
            }
            if (wsum <= 0) return false;
            s.W = wsum; s.C = c; s.B = b; s.Gx = gx; s.Gz = gz;
            return true;
        }

        /// <summary>The weight W at (x, z) (0 outside).</summary>
        public float WeightAt(float x, float z) => Sample(x, z, out var s) ? s.W : 0;

        // ------------------------------------------------------------------ file

        public void Write(Stream stream)
        {
            using var bw = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            bw.Write(Magic); bw.Write(Version);
            bw.Write(X0); bw.Write(Z0); bw.Write(Res); bw.Write(Nx); bw.Write(Nz); bw.Write(WindAzimuth);
            bw.Write(TileCount);
            for (int k = 0; k < Tiles.Length; k++)
            {
                var t = Tiles[k];
                if (t == null) continue;
                bw.Write(k); bw.Write(t.ScaleC); bw.Write(t.ScaleB); bw.Write(t.ScaleG);
                foreach (var v in t.C) bw.Write(v);
                foreach (var v in t.B) bw.Write(v);
                bw.Write(t.W);
                foreach (var v in t.Gx) bw.Write(v);
                foreach (var v in t.Gz) bw.Write(v);
            }
        }

        public static DuneField Read(byte[] data)
        {
            if (data == null || data.Length < 36) return null;
            using var br = new BinaryReader(new MemoryStream(data));
            if (br.ReadUInt32() != Magic || br.ReadInt32() != Version) return null;
            float x0 = br.ReadSingle(), z0 = br.ReadSingle(), res = br.ReadSingle();
            int nx = br.ReadInt32(), nz = br.ReadInt32();
            float wind = br.ReadSingle();
            var f = Empty(x0, z0, nx, nz, res, wind);
            int n = br.ReadInt32();
            for (int k = 0; k < n; k++)
            {
                int idx = br.ReadInt32();
                var t = new TileData { ScaleC = br.ReadSingle(), ScaleB = br.ReadSingle(), ScaleG = br.ReadSingle() };
                for (int q = 0; q < t.C.Length; q++) t.C[q] = br.ReadInt16();
                for (int q = 0; q < t.B.Length; q++) t.B[q] = br.ReadSByte();
                br.Read(t.W, 0, t.W.Length);
                for (int q = 0; q < t.Gx.Length; q++) t.Gx[q] = br.ReadSByte();
                for (int q = 0; q < t.Gz.Length; q++) t.Gz[q] = br.ReadSByte();
                if (idx >= 0 && idx < f.Tiles.Length) f.Tiles[idx] = t;
            }
            return f;
        }
    }
}
