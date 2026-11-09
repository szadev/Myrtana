using System;
using System.Collections.Generic;
using System.IO;
using G3.Formats.Genome;

namespace G3.Formats.Mesh
{
    /// <summary>One sub-mesh with a single material (eCMeshElement). Raw engine data: centimetres, D3D winding.</summary>
    public sealed class MeshElement
    {
        public string Material;          // "<Name>.xshmat" (sometimes legacy ".xmat")
        public float[] Positions;        // xyz
        public float[] Normals;          // xyz
        public float[] Uv0;              // uv
        public float[] Uv1;              // second UV set (stream 15) if present
        public uint[] Diffuse;           // D3DCOLOR 0xAARRGGBB, null if absent
        public uint[] Specular;
        public uint[] Indices;           // triangle list, local to this element

        public int VertexCount => Positions.Length / 3;
    }

    public sealed class MeshResource
    {
        public string Name;
        public float[] BoundsMin = new float[3];
        public float[] BoundsMax = new float[3];
        public readonly List<MeshElement> Elements = new List<MeshElement>();
    }

    /// <summary>.xcmsh — eCResourceMeshComplex_PS (Docs/formats-mesh-material-texture.md §3).</summary>
    public static class Xcmsh
    {
        public static MeshResource Read(byte[] data, string name = null)
        {
            if (data == null || data.Length == 0) return null; // a few shipped files are empty
            var g = new GenomeReader(data);
            var obj = g.ReadObject();
            if (obj == null || obj.ClassName != "eCResourceMeshComplex_PS")
                throw new InvalidDataException($"{name}: unexpected class {obj?.ClassName}");

            var mesh = new MeshResource { Name = name };
            var bbox = obj.Get("BoundingBox");
            if (bbox != null)
                for (int i = 0; i < 3; i++)
                {
                    mesh.BoundsMin[i] = BitConverter.ToSingle(bbox.Value, i * 4);
                    mesh.BoundsMax[i] = BitConverter.ToSingle(bbox.Value, 12 + i * 4);
                }

            g.ReadResourceTail();
            g.R.ReadSingle(); // resource priority
            int elementCount = g.R.ReadInt32();
            for (int i = 0; i < elementCount; i++)
                mesh.Elements.Add(ReadElement(g));
            g.Position = obj.End;
            return mesh;
        }

        static MeshElement ReadElement(GenomeReader g)
        {
            var r = g.R;
            var e = new MeshElement();
            ushort ver = r.ReadUInt16();
            r.ReadUInt32();     // FVF
            g.Skip(24);         // bbox
            r.ReadUInt32();     // size hint
            e.Material = g.Str();

            int streams = r.ReadInt32();
            for (int s = 0; s < streams; s++)
            {
                int type = r.ReadInt32();
                r.ReadUInt16();
                int count = g.ArrayCount();
                switch (type)
                {
                    case 0: e.Indices = ReadU32(r, count); break;
                    case 1: e.Positions = ReadF32(r, count * 3); break;
                    case 3: e.Normals = ReadF32(r, count * 3); break;
                    case 4: e.Diffuse = ReadU32(r, count); break;
                    case 5: e.Specular = ReadU32(r, count); break;
                    case 12: e.Uv0 = ReadF32(r, count * 2); break;
                    case 15: e.Uv1 = ReadF32(r, count * 2); break;
                    default: g.Skip((long)count * StreamElementSize(type)); break;
                }
            }

            if (ver >= 3)
                for (int k = 0; k < 2; k++) g.Skip(4L * g.ArrayCount());
            if (ver >= 2)
            {
                int groups = g.ArrayCount();
                for (int k = 0; k < groups; k++)
                {
                    g.Skip(4L * g.ArrayCount());
                    g.Skip(4L * g.ArrayCount());
                    g.Skip(12 + 64 + 8);
                }
            }
            if (ver >= 4)
            {
                g.Skip(24L * r.ReadInt32()); // sphere tree
                g.Skip(4L * g.ArrayCount()); // triangle order
            }
            return e;
        }

        /// <summary>eEVertexStreamArrayType element sizes (§3.2).</summary>
        public static int StreamElementSize(int type)
        {
            if (type == 0) return 4;
            if (type == 1 || type == 3) return 12;
            if (type == 2) return 16;
            if (type >= 4 && type <= 11) return 4;
            if (type == 12) return 8;
            if (type >= 13 && type <= 59)
                switch ((type - 13) % 3) { case 0: return 12; case 1: return 16; default: return 8; }
            if (type >= 60 && type <= 63) return 16;
            if (type >= 64 && type <= 67) return 12;
            if (type >= 68 && type <= 71) return 8;
            if (type == 72) return 12;
            if (type == 73) return 8;
            throw new InvalidDataException($"unknown vertex stream type {type}");
        }

        static float[] ReadF32(BinaryReader r, int n)
        {
            var a = new float[n];
            Buffer.BlockCopy(r.ReadBytes(n * 4), 0, a, 0, n * 4);
            return a;
        }

        static uint[] ReadU32(BinaryReader r, int n)
        {
            var a = new uint[n];
            Buffer.BlockCopy(r.ReadBytes(n * 4), 0, a, 0, n * 4);
            return a;
        }
    }

    /// <summary>.xlmsh — eCResourceMeshLoD_PS: list of mesh files, [0] is the full-detail mesh (§6).</summary>
    public static class Xlmsh
    {
        public static List<string> Read(byte[] data)
        {
            var g = new GenomeReader(data);
            var obj = g.ReadObject();
            g.ReadResourceTail();
            int n = g.R.ReadInt32();
            var list = new List<string>(n);
            for (int i = 0; i < n; i++) list.Add(g.Str());
            g.Position = obj.End;
            return list;
        }
    }
}
