using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;

namespace G3.Unity
{
    /// <summary>Per welded vertex facts of one terrain mesh (in its weld order): the surface's inputs that must agree between cells.</summary>
    public sealed class ReliefShared
    {
        public Vector3[] N;          // geometry normal (quantised to int16 and renormalised: every cell decodes the same bits)
        public byte[] Ny1, Ny05;     // flattest face normal.y of the full-amplitude / half-amplitude materials (None = no such face)
        public byte[] Flags;         // ReliefFacts.Amp0 | Walk | Phong0 | Pin
        public short[] Q;            // the stored int16 normal (3 per vertex), when built offline
        public byte[] Open;          // distance to gameplay (paths, buildings, objects, NPC spots): byte / 255 x ReliefFacts.OpenRange m (null = 0)
        public byte[] Clear;         // fix-174: room before other geometry (ReliefClearance.Encode; null / 255 = no limit)
        public int Count => N != null ? N.Length : 0;
    }

    /// <summary>
    /// The facts of every welded terrain position (Docs/remaster-world.md section 13): the averaged vertex normal and the
    /// faces touching it, over all cells at once. G3's landscape cells share their border vertices and overlap (the same
    /// triangles in two cells), so facts taken from one cell alone would differ between the copies and open cracks or
    /// make the copies fight; <c>G3Cli relief facts</c> accumulates the whole world offline and writes
    /// <c>Resources/G3TerrainReliefFacts.bytes</c> (per cell, in its weld order); the runtime reads its cell's block.
    /// <see cref="Local"/> computes the same from one mesh (tests, a cell missing from the table).
    /// </summary>
    public sealed class ReliefFacts
    {
        public const byte None = 255;
        /// <summary>Pin (fix-174): on an open edge of the terrain union (G3's tunnel / cave holes, non-conforming cell seams): never moved.</summary>
        public const byte Amp0 = 1, Walk = 2, Phong0 = 4, Pin = 8;
        /// <summary>Metres covered by the Open byte (255 = this far or farther from gameplay).</summary>
        public const float OpenRange = 100f;
        const uint Magic = 0x46523347;   // "G3RF"
        const int Record = 11;           // bytes per welded vertex: normal 3 x int16, Ny1, Ny05, Flags, Open, Clear
        public const int Version = 4;

        struct Acc
        {
            public Vector3 NSum;
            public byte Ny1, Ny05, Flags;
        }

        readonly Dictionary<Vector3, Acc> _at = new Dictionary<Vector3, Acc>();
        readonly float _walkNy;

        public ReliefFacts(float walkNy) { _walkNy = walkNy; }

        public int Positions => _at.Count;

        public static byte QuantNy(float ny) => (byte)Math.Max(0, Math.Min(254, (int)Math.Round((ny + 1) * 127)));
        public static float NyOf(byte q) => q / 127f - 1;

        /// <summary>The int16 code of a normal (what the table stores).</summary>
        public static (short x, short y, short z) Encode(Vector3 n)
        {
            float l = n.Length();
            if (l < 1e-6f) return (0, 32767, 0);
            n /= l;
            return ((short)Math.Round(n.X * 32767), (short)Math.Round(n.Y * 32767), (short)Math.Round(n.Z * 32767));
        }

        /// <summary>The normal every reader decodes from an int16 code.</summary>
        public static Vector3 DecodeNormal(short x, short y, short z)
        {
            var q = new Vector3(x / 32767f, y / 32767f, z / 32767f);
            float l = q.Length();
            return l > 1e-6f ? q / l : Vector3.UnitY;
        }

        /// <summary>Adds one mesh: every vertex normal and every face (its normal.y, material class, walkability) at its corners.</summary>
        public void Add(ReliefMesh m)
        {
            for (int i = 0; i < m.Pos.Length; i++)
            {
                if (!_at.TryGetValue(m.Pos[i], out var a)) a = new Acc { Ny1 = None, Ny05 = None };
                a.NSum += m.Nrm[i];
                _at[m.Pos[i]] = a;
            }
            for (int sm = 0; sm < m.Sub.Length; sm++)
            {
                var tris = m.Sub[sm];
                float amp = m.SubAmp != null && sm < m.SubAmp.Length ? m.SubAmp[sm] : 1;
                float pho = m.SubPhong != null && sm < m.SubPhong.Length ? m.SubPhong[sm] : 1;
                for (int k = 0; k + 2 < tris.Length; k += 3)
                {
                    int i0 = tris[k], i1 = tris[k + 1], i2 = tris[k + 2];
                    Vector3 p0 = m.Pos[i0], p1 = m.Pos[i1], p2 = m.Pos[i2];
                    var fn = Vector3.Cross(p1 - p0, p2 - p0);
                    float fl = fn.Length();
                    if (fl < 1e-9f) continue;
                    fn /= fl;   // Unity winding: cross(p1 - p0, p2 - p0) is the front (agrees with G3's normals on all but 675 of 2.08 M faces)
                    byte ny = QuantNy(fn.Y);
                    bool walk = fn.Y >= _walkNy;
                    for (int j = 0; j < 3; j++)
                    {
                        var p = j == 0 ? p0 : j == 1 ? p1 : p2;
                        var a = _at[p];
                        if (amp <= 0) a.Flags |= Amp0;
                        else if (amp < 1) { if (a.Ny05 == None || ny > a.Ny05) a.Ny05 = ny; }
                        else if (a.Ny1 == None || ny > a.Ny1) a.Ny1 = ny;
                        if (walk) a.Flags |= Walk;
                        if (pho <= 0) a.Flags |= Phong0;
                        _at[p] = a;
                    }
                }
            }
        }

        /// <summary>
        /// The facts of <paramref name="m"/>'s welded vertices (<see cref="TerrainRelief.Weld"/> must have run);
        /// <paramref name="open"/>: how far a position is from gameplay (paths, buildings, objects, NPC spots), 0..255;
        /// <paramref name="clear"/> (fix-174, position and normal): pinned (open edge of the terrain union) and the clearance byte.
        /// </summary>
        public ReliefShared For(ReliefMesh m, Func<Vector3, byte> open = null, Func<Vector3, Vector3, (bool pin, byte clear)> clear = null)
        {
            int nw = m.WeldCount;
            var r = new ReliefShared { N = new Vector3[nw], Ny1 = new byte[nw], Ny05 = new byte[nw], Flags = new byte[nw], Q = new short[nw * 3], Open = new byte[nw],
                                       Clear = new byte[nw] };
            for (int w = 0; w < nw; w++)
            {
                if (!_at.TryGetValue(m.WPos[w], out var a)) a = new Acc { NSum = Vector3.UnitY, Ny1 = None, Ny05 = None };
                var (x, y, z) = Encode(a.NSum);
                r.Q[w * 3] = x; r.Q[w * 3 + 1] = y; r.Q[w * 3 + 2] = z;
                r.N[w] = DecodeNormal(x, y, z); r.Ny1[w] = a.Ny1; r.Ny05[w] = a.Ny05; r.Flags[w] = a.Flags;
                r.Clear[w] = ReliefClearance.NoLimit;
                if (clear != null)
                {
                    var (pin, c) = clear(m.WPos[w], r.N[w]);
                    r.Clear[w] = c;
                    if (pin) r.Flags[w] |= Pin;
                }
                r.Open[w] = open != null && (r.Flags[w] & Amp0) == 0 ? open(m.WPos[w]) : (byte)0;
            }
            return r;
        }

        /// <summary>Facts from the mesh's own faces only (welds it if needed).</summary>
        public static ReliefShared Local(ReliefMesh m, ReliefSettings s)
        {
            if (m.Weld == null) TerrainRelief.Weld(m);
            var f = new ReliefFacts(s.walkNy);
            f.Add(m);
            return f.For(m);
        }

        // ------------------------------------------------------------------ the table file

        /// <summary>Writes the per-mesh blocks: magic, version, count; per mesh: name, weld count, then N (3 x int16), Ny1, Ny05, Flags, Open, Clear.</summary>
        public static void Write(Stream stream, IEnumerable<(string name, ReliefShared facts)> meshes)
        {
            var list = new List<(string, ReliefShared)>(meshes);
            using var bw = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            bw.Write(Magic); bw.Write(Version); bw.Write(list.Count);
            foreach (var (name, f) in list)
            {
                bw.Write(name); bw.Write(f.Count);
                for (int i = 0; i < f.Count; i++)
                {
                    var (x, y, z) = f.Q != null ? (f.Q[i * 3], f.Q[i * 3 + 1], f.Q[i * 3 + 2]) : Encode(f.N[i]);
                    bw.Write(x); bw.Write(y); bw.Write(z);
                }
                bw.Write(f.Ny1, 0, f.Count); bw.Write(f.Ny05, 0, f.Count); bw.Write(f.Flags, 0, f.Count);
                bw.Write(f.Open ?? new byte[f.Count], 0, f.Count);
                if (f.Clear != null) bw.Write(f.Clear, 0, f.Count);
                else for (int i = 0; i < f.Count; i++) bw.Write(ReliefClearance.NoLimit);
            }
        }

        /// <summary>
        /// Index of the table: mesh name → (byte offset of its block's data, weld count). The data is decoded per mesh on use
        /// (<see cref="Decode"/>), so the runtime keeps only the bytes (10 B per welded vertex).
        /// </summary>
        public static Dictionary<string, (int offset, int count)> Index(byte[] data)
        {
            var d = new Dictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase);
            using var br = new BinaryReader(new MemoryStream(data));
            if (data.Length < 12 || br.ReadUInt32() != Magic || br.ReadInt32() != Version) return d;
            int n = br.ReadInt32();
            for (int i = 0; i < n; i++)
            {
                string name = br.ReadString();
                int count = br.ReadInt32();
                int offset = (int)br.BaseStream.Position;
                d[name] = (offset, count);
                br.BaseStream.Position = offset + count * Record;
            }
            return d;
        }

        /// <summary>One mesh's facts from the table (normals by <see cref="DecodeNormal"/>, as <see cref="For"/> made them).</summary>
        public static ReliefShared Decode(byte[] data, int offset, int count)
        {
            var r = new ReliefShared { N = new Vector3[count], Ny1 = new byte[count], Ny05 = new byte[count], Flags = new byte[count], Open = new byte[count], Clear = new byte[count] };
            for (int i = 0; i < count; i++)
            {
                int o = offset + i * 6;
                r.N[i] = DecodeNormal((short)(data[o] | data[o + 1] << 8), (short)(data[o + 2] | data[o + 3] << 8), (short)(data[o + 4] | data[o + 5] << 8));
            }
            int b = offset + count * 6;
            Buffer.BlockCopy(data, b, r.Ny1, 0, count);
            Buffer.BlockCopy(data, b + count, r.Ny05, 0, count);
            Buffer.BlockCopy(data, b + 2 * count, r.Flags, 0, count);
            Buffer.BlockCopy(data, b + 3 * count, r.Open, 0, count);
            Buffer.BlockCopy(data, b + 4 * count, r.Clear, 0, count);
            return r;
        }
    }
}
