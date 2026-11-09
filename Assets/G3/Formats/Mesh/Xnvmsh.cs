using System;
using System.Collections.Generic;
using System.IO;
using G3.Formats.Genome;

namespace G3.Formats.Mesh
{
    /// <summary>
    /// One cooked PhysX 2.x mesh inside an .xnvmsh. Positions are converted to centimetres (the NXS data is in
    /// metres) so they share the render meshes' raw engine convention: left-handed, D3D winding.
    /// </summary>
    public sealed class CollisionSubMesh
    {
        public bool Convex;               // NXS "CVXM" (hull) vs "MESH" (triangle mesh)
        public float[] Positions;         // xyz, cm
        public uint[] Indices;            // triangle list
        public ushort[] TriangleMaterials;// per triangle, MESH only (null if absent)
        // eCResourceCollisionMesh_PS per-mesh shape flags (class version >= 42)
        public byte ShapeMaterial;        // eEShapeMaterial
        public bool IgnoredByTraceRay, DisableCollision, DisableResponse;

        public int VertexCount => Positions.Length / 3;
    }

    public sealed class CollisionMeshResource
    {
        public string Name;
        public ushort ClassVersion;
        public bool ConvexResource;
        /// <summary>Class version &lt; 64: cooked by an older PhysX that the game rejects (it re-cooks from the .xcmsh).
        /// The container is parsed, but <see cref="Meshes"/> stays empty.</summary>
        public bool Obsolete;
        public float[] Bounds = new float[6]; // cm, min xyz / max xyz
        public readonly List<CollisionSubMesh> Meshes = new List<CollisionSubMesh>();
    }

    /// <summary>
    /// .xnvmsh — eCResourceCollisionMesh_PS (Gothic 3 collision meshes, PhysX 2.4 cooked data).
    /// <code>
    /// accessor object eCResourceCollisionMesh_PS (props: ResourcePriority), class version cv (64 in G3)
    /// [cv>=35] bool convexResource ; f32 priority ; u32 meshCount
    /// meshCount × { u64 size ; size bytes NXS stream ("NXS\1MESH" or "NXS\1CVXM") }
    /// eCResourceBase_PS tail ; bCBox boundary ; meshCount × bCBox (unused, often inverted)
    /// [cv>=42] bool ; u32 n ; n × { u8 shapeMaterial ; bool ignoredByTraceRay, disableCollision, disableResponse }
    /// [cv>=65] u64 timestamp
    ///
    /// NXS MESH (all little-endian, metres):
    ///   "NXS\1" "MESH" u32 version(0) u32 flags (1 materials, 2 face remap, 8 u8 indices, 16 u16 indices)
    ///   f32 convexEdgeThreshold ; u32 heightFieldAxis (255) ; f32 heightFieldExtent ; u32 nv ; u32 nt
    ///   nv × vec3 ; nt × 3 indices ; [1] nt × u16 material ; [2] u32 maxIdx + nt × idx(u8/u16/u32 by maxIdx)
    ///   u32 nConvexParts ; [n>0] u32 nFlatParts ; nt × u16 ; nt × (nFlat&lt;256 ? u8 : u16)
    ///   u32 opcodeSize ; opcodeSize bytes "OPC\1"…"HBM\1" collision tree
    ///   f32 geomEpsilon ; vec4 sphere ; bCBox ; f32 mass ; [mass>=0] 3×3 inertia + vec3 COM ; u32 n ; n bytes edge flags
    /// NXS CVXM v2: "ICE\1CLHL" u32 ; "ICE\1CVHL" u32 ; u32 nv, nt, nEdges, nPolys, a, b ; nv × vec3 ;
    ///   u32 maxIdx ; nt × 3 idx(u8/u16/u32) ; … (hull polygons, valencies, opcode model — skipped by size)
    /// NXS CVXM v0 (one file): u32 flags ; u32 nv ; u32 nt ; nv × vec3 ; nt × 3 u32 ; …
    /// </code>
    /// </summary>
    public static class Xnvmsh
    {
        public static CollisionMeshResource Read(byte[] data, string name = null)
        {
            if (data == null || data.Length == 0) return null;
            var g = new GenomeReader(data);
            var obj = g.ReadObject();
            if (obj == null || obj.ClassName != "eCResourceCollisionMesh_PS")
                throw new InvalidDataException($"{name}: unexpected class {obj?.ClassName}");
            var res = new CollisionMeshResource { Name = name, ClassVersion = obj.ClassVersion };
            var r = g.R;
            int cv = obj.ClassVersion;
            if (cv < 32) throw new InvalidDataException($"{name}: collision mesh class version {cv}");
            res.Obsolete = cv < 64;
            if (cv >= 35) res.ConvexResource = r.ReadByte() != 0;
            r.ReadSingle();
            int meshCount = r.ReadInt32();
            for (int i = 0; i < meshCount; i++)
            {
                long size = r.ReadInt64();
                long start = g.Position;
                if (size <= 0 || start + size > obj.End) throw new InvalidDataException($"{name}: NXS size {size}");
                if (!res.Obsolete) res.Meshes.Add(ReadNxs(r, start + size, name));
                g.Position = start + size;
            }
            g.ReadResourceTail();
            for (int i = 0; i < 6; i++) res.Bounds[i] = r.ReadSingle();
            g.Skip(24L * meshCount);
            if (cv >= 42)
            {
                r.ReadByte();
                int n = r.ReadInt32();
                for (int i = 0; i < n; i++)
                {
                    byte mat = r.ReadByte();
                    bool ign = r.ReadByte() != 0, dc = r.ReadByte() != 0, dr = r.ReadByte() != 0;
                    if (i < res.Meshes.Count)
                    {
                        var m = res.Meshes[i];
                        m.ShapeMaterial = mat; m.IgnoredByTraceRay = ign; m.DisableCollision = dc; m.DisableResponse = dr;
                    }
                }
            }
            // u64 FILETIME cook timestamp: always from cv 65, but 174 cv-64 files (castle/guardhouse/ruin "_COL" meshes,
            // "_SC_" stone plateaus) carry it too. Rejecting them dropped those objects' collision (walk-through walls).
            if (cv >= 65 || (!res.Obsolete && obj.End - g.Position == 8)) g.Skip(8);
            // Obsolete v35 files carry one extra trailing byte, a few v41 files one extra box; the game ignores them anyway.
            if (res.Obsolete && g.Position < obj.End) g.Position = obj.End;
            if (g.Position != obj.End) throw new InvalidDataException($"{name}: {obj.End - g.Position} bytes left in object");
            if (g.Position != g.PayloadEnd) throw new InvalidDataException($"{name}: {g.PayloadEnd - g.Position} bytes after object");
            return res;
        }

        static void Expect(BinaryReader r, string tag, string name)
        {
            var b = r.ReadBytes(4);
            if (b.Length != 4 || b[0] != tag[0] || b[1] != tag[1] || b[2] != tag[2] || b[3] != tag[3])
                throw new InvalidDataException($"{name}: expected chunk '{tag}'");
        }

        static CollisionSubMesh ReadNxs(BinaryReader r, long end, string name)
        {
            Expect(r, "NXS\u0001", name);
            var id = new string(r.ReadChars(4));
            if (id == "MESH") return ReadTriMesh(r, end, name);
            if (id == "CVXM") return ReadConvex(r, end, name);
            throw new InvalidDataException($"{name}: unknown NXS chunk '{id}'");
        }

        static CollisionSubMesh ReadTriMesh(BinaryReader r, long end, string name)
        {
            var m = new CollisionSubMesh();
            uint ver = r.ReadUInt32();
            if (ver != 0) throw new InvalidDataException($"{name}: NXS MESH version {ver}");
            uint flags = r.ReadUInt32();
            if ((flags & ~0x1Bu) != 0) throw new InvalidDataException($"{name}: NXS MESH flags {flags:X}");
            r.ReadSingle(); r.ReadUInt32(); r.ReadSingle();
            int nv = r.ReadInt32(), nt = r.ReadInt32();
            m.Positions = ReadPositions(r, nv);
            int isz = (flags & 8) != 0 ? 1 : (flags & 16) != 0 ? 2 : 4;
            m.Indices = ReadIndices(r, nt * 3, isz);
            if ((flags & 1) != 0)
            {
                m.TriangleMaterials = new ushort[nt];
                for (int i = 0; i < nt; i++) m.TriangleMaterials[i] = r.ReadUInt16();
            }
            if ((flags & 2) != 0)
            {
                uint maxIdx = r.ReadUInt32();
                Skip(r, (long)nt * IndexSize(maxIdx));
            }
            int convexParts = r.ReadInt32();
            if (convexParts > 0)
            {
                int flat = r.ReadInt32();
                Skip(r, 2L * nt + (long)nt * (flat < 256 ? 1 : 2));
            }
            int opc = r.ReadInt32();
            if (opc < 0 || r.BaseStream.Position + opc > end) throw new InvalidDataException($"{name}: opcode size {opc}");
            var tag = r.ReadBytes(4);
            if (opc > 0 && (tag[0] != 'O' || tag[1] != 'P' || tag[2] != 'C')) throw new InvalidDataException($"{name}: OPC chunk missing");
            Skip(r, opc - 4);
            Skip(r, 4 * (1 + 4 + 6));        // geomEpsilon, sphere, box
            if (r.ReadSingle() >= 0f) Skip(r, 4 * (9 + 3)); // mass; inertia + COM only when mass >= 0 (-1 = none)
            int extra = r.ReadInt32();
            Skip(r, extra);
            if (r.BaseStream.Position != end)
                throw new InvalidDataException($"{name}: NXS MESH parsed to {end - r.BaseStream.Position} bytes before end");
            Validate(m, name);
            return m;
        }

        static CollisionSubMesh ReadConvex(BinaryReader r, long end, string name)
        {
            var m = new CollisionSubMesh { Convex = true };
            uint ver = r.ReadUInt32();
            if (ver == 0)
            {
                r.ReadUInt32(); // flags
                int nv0 = r.ReadInt32(), nt0 = r.ReadInt32();
                m.Positions = ReadPositions(r, nv0);
                m.Indices = ReadIndices(r, nt0 * 3, 4);
            }
            else if (ver == 2)
            {
                r.ReadUInt32();
                Expect(r, "ICE\u0001", name); Expect(r, "CLHL", name); r.ReadUInt32();
                Expect(r, "ICE\u0001", name); Expect(r, "CVHL", name); r.ReadUInt32();
                int nv = r.ReadInt32(), nt = r.ReadInt32();
                Skip(r, 16);
                m.Positions = ReadPositions(r, nv);
                uint maxIdx = r.ReadUInt32();
                if (maxIdx >= nv) throw new InvalidDataException($"{name}: hull max index {maxIdx} >= {nv}");
                m.Indices = ReadIndices(r, nt * 3, IndexSize(maxIdx));
            }
            else throw new InvalidDataException($"{name}: NXS CVXM version {ver}");
            if (r.BaseStream.Position > end) throw new InvalidDataException($"{name}: CVXM overrun");
            Validate(m, name);
            return m;
        }

        static int IndexSize(uint maxIdx) => maxIdx <= 0xFF ? 1 : maxIdx <= 0xFFFF ? 2 : 4;

        static void Validate(CollisionSubMesh m, string name)
        {
            int nv = m.VertexCount;
            foreach (var i in m.Indices)
                if (i >= nv) throw new InvalidDataException($"{name}: index {i} >= {nv}");
            foreach (var p in m.Positions)
                if (float.IsNaN(p) || float.IsInfinity(p)) throw new InvalidDataException($"{name}: bad vertex");
        }

        static float[] ReadPositions(BinaryReader r, int nv)
        {
            if (nv < 0 || nv > 10_000_000) throw new InvalidDataException("vertex count");
            var a = new float[nv * 3];
            Buffer.BlockCopy(r.ReadBytes(nv * 12), 0, a, 0, nv * 12);
            for (int i = 0; i < a.Length; i++) a[i] *= 100f; // metres -> cm
            return a;
        }

        static uint[] ReadIndices(BinaryReader r, int n, int size)
        {
            if (n < 0 || n > 30_000_000) throw new InvalidDataException("index count");
            var a = new uint[n];
            for (int i = 0; i < n; i++)
                a[i] = size == 1 ? r.ReadByte() : size == 2 ? r.ReadUInt16() : r.ReadUInt32();
            return a;
        }

        static void Skip(BinaryReader r, long n)
        {
            if (n < 0) throw new InvalidDataException("negative skip");
            r.BaseStream.Position += n;
        }
    }
}
