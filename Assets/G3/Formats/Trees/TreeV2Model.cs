using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace G3.Formats.Trees
{
    /// <summary>
    /// World remaster v2 replacement tree (Docs/remaster-world.md 10.15), as written by Tools/Remaster/trees/treev2_gen.py
    /// (<c>.g3tv2</c>, generated, git-ignored under GameData/Remaster/World/Trees/Models). Unity axes, metres,
    /// clockwise-front triangles. LODs 0..2 are geometry (bark tubes + alpha-tested cards), the last one is the impostor
    /// quad (G3/SpeedTree billboard mode). Vertex layout = the G3/SpeedTree shader's: uv1 = (corner x, corner y, wind
    /// weight, wind matrix 0..3); colour rgb = baked crown occlusion.
    /// </summary>
    public sealed class TreeV2Model
    {
        public const string Magic = "G3TREEV2";
        public const int Version = 1;
        public const int MaterialBark = 0, MaterialCards = 1, MaterialImpostor = 2;

        public string Name;
        public float Height, HalfWidth, TrunkRadius;
        public readonly List<TreeV2Lod> Lods = new List<TreeV2Lod>();

        public static TreeV2Model Read(Stream s)
        {
            using var r = new BinaryReader(s, Encoding.UTF8, true);
            if (Encoding.ASCII.GetString(r.ReadBytes(8)) != Magic) throw new InvalidDataException("not a .g3tv2 file");
            int version = r.ReadUInt16();
            if (version != Version) throw new InvalidDataException($".g3tv2 version {version}, expected {Version}");
            var m = new TreeV2Model { Name = Encoding.UTF8.GetString(r.ReadBytes(r.ReadByte())) };
            m.Height = r.ReadSingle(); m.HalfWidth = r.ReadSingle(); m.TrunkRadius = r.ReadSingle();
            int lods = r.ReadByte();
            for (int l = 0; l < lods; l++)
            {
                int n = checked((int)r.ReadUInt32());
                var lod = new TreeV2Lod
                {
                    Positions = Floats(r, 3 * n), Normals = Floats(r, 3 * n), Colors = r.ReadBytes(4 * n),
                    Uv0 = Floats(r, 2 * n), Uv1 = Floats(r, 4 * n),
                };
                int subs = r.ReadByte();
                for (int k = 0; k < subs; k++)
                {
                    int mat = r.ReadByte();
                    int count = checked((int)r.ReadUInt32());
                    var idx = new int[count];
                    for (int i = 0; i < count; i++)
                    {
                        uint v = r.ReadUInt32();
                        if (v >= (uint)n) throw new InvalidDataException($"index {v} out of range ({n} vertices)");
                        idx[i] = (int)v;
                    }
                    lod.Submeshes.Add(new TreeV2Submesh { Material = mat, Indices = idx });
                }
                m.Lods.Add(lod);
            }
            return m;
        }

        public void Write(Stream s)
        {
            using var w = new BinaryWriter(s, Encoding.UTF8, true);
            w.Write(Encoding.ASCII.GetBytes(Magic));
            w.Write((ushort)Version);
            var name = Encoding.UTF8.GetBytes(Name ?? "");
            w.Write((byte)name.Length); w.Write(name);
            w.Write(Height); w.Write(HalfWidth); w.Write(TrunkRadius);
            w.Write((byte)Lods.Count);
            foreach (var l in Lods)
            {
                w.Write((uint)l.VertexCount);
                foreach (var a in new[] { l.Positions, l.Normals }) foreach (var f in a) w.Write(f);
                w.Write(l.Colors);
                foreach (var a in new[] { l.Uv0, l.Uv1 }) foreach (var f in a) w.Write(f);
                w.Write((byte)l.Submeshes.Count);
                foreach (var sm in l.Submeshes)
                {
                    w.Write((byte)sm.Material); w.Write((uint)sm.Indices.Length);
                    foreach (int i in sm.Indices) w.Write((uint)i);
                }
            }
        }

        static float[] Floats(BinaryReader r, int n)
        {
            var a = new float[n];
            var b = r.ReadBytes(4 * n);
            if (b.Length != 4 * n) throw new EndOfStreamException();
            Buffer.BlockCopy(b, 0, a, 0, b.Length);
            return a;
        }
    }

    public sealed class TreeV2Lod
    {
        public float[] Positions, Normals, Uv0, Uv1;   // 3, 3, 2, 4 per vertex
        public byte[] Colors;                          // RGBA per vertex
        public readonly List<TreeV2Submesh> Submeshes = new List<TreeV2Submesh>();

        public int VertexCount => Positions.Length / 3;

        public int Triangles
        {
            get { int t = 0; foreach (var s in Submeshes) t += s.Indices.Length / 3; return t; }
        }

        public bool IsImpostor => Submeshes.Count == 1 && Submeshes[0].Material == TreeV2Model.MaterialImpostor;
    }

    public sealed class TreeV2Submesh
    {
        public int Material;
        public int[] Indices;
    }

    /// <summary>One row of <c>manifest.tsv</c> (treev2_build.py): a model and what it needs.</summary>
    public sealed class TreeV2ManifestEntry
    {
        public string Model, Species, Region, Leaf, Bark;
        public int Variant;
        public float Height, HalfWidth, TrunkRadius, Snow, Flex;
        public int[] Triangles = new int[4];

        public static List<TreeV2ManifestEntry> Parse(string tsv)
        {
            var list = new List<TreeV2ManifestEntry>();
            var lines = tsv.Replace("\r", "").Split('\n');
            if (lines.Length == 0) return list;
            var head = new List<string>(lines[0].Split('\t'));
            int Col(string n)
            {
                int i = head.IndexOf(n);
                if (i < 0) throw new InvalidDataException("manifest.tsv: no column " + n);
                return i;
            }
            int cModel = Col("model"), cSpecies = Col("species"), cVariant = Col("variant"), cRegion = Col("region"),
                cHeight = Col("height"), cHalf = Col("half_width"), cTrunk = Col("trunk_radius"), cLeaf = Col("leaf"),
                cBark = Col("bark"), cSnow = Col("snow"), cFlex = Col("flex");
            var cTris = new[] { Col("tris0"), Col("tris1"), Col("tris2"), Col("tris3") };
            var ci = CultureInfo.InvariantCulture;
            for (int i = 1; i < lines.Length; i++)
            {
                if (lines[i].Trim().Length == 0) continue;
                var c = lines[i].Split('\t');
                if (c.Length < head.Count) throw new InvalidDataException($"manifest.tsv line {i + 1}: {c.Length} columns");
                var e = new TreeV2ManifestEntry
                {
                    Model = c[cModel], Species = c[cSpecies], Variant = int.Parse(c[cVariant], ci), Region = c[cRegion],
                    Height = float.Parse(c[cHeight], ci), HalfWidth = float.Parse(c[cHalf], ci),
                    TrunkRadius = float.Parse(c[cTrunk], ci), Leaf = c[cLeaf], Bark = c[cBark],
                    Snow = float.Parse(c[cSnow], ci), Flex = float.Parse(c[cFlex], ci),
                };
                for (int k = 0; k < 4; k++) e.Triangles[k] = int.Parse(c[cTris[k]], ci);
                list.Add(e);
            }
            return list;
        }
    }
}
