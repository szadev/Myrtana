using System.Globalization;
using System.IO;
using G3.Formats.Trees;

namespace G3.Tools.SptBake
{
    /// <summary>Sanity preview: LOD 0 branches, fronds and leaf cards (facing −Z) as Wavefront OBJ in metres.</summary>
    static class ObjExport
    {
        public static void Write(BakedTree t, string path)
        {
            var ci = CultureInfo.InvariantCulture;
            using var w = new StreamWriter(path);
            w.WriteLine("# " + t.Name + " (SptBake preview, metres, Y up)");
            int vbase = 1;
            void Mesh(string group, BakedMeshLod m)
            {
                if (m == null) return;
                w.WriteLine("g " + group);
                for (int i = 0; i < m.VertexCount; i++)
                    w.WriteLine(string.Format(ci, "v {0} {1} {2}", m.Positions[3 * i] / 100, m.Positions[3 * i + 1] / 100, m.Positions[3 * i + 2] / 100));
                for (int i = 0; i < m.VertexCount; i++) w.WriteLine(string.Format(ci, "vt {0} {1}", m.Uv[2 * i], m.Uv[2 * i + 1]));
                // OBJ is counter-clockwise front; the baked lists are clockwise (Unity).
                for (int i = 0; i < m.Triangles.Length; i += 3)
                {
                    int a = m.Triangles[i] + vbase, b = m.Triangles[i + 1] + vbase, c = m.Triangles[i + 2] + vbase;
                    w.WriteLine($"f {a}/{a} {c}/{c} {b}/{b}");
                }
                vbase += m.VertexCount;
            }
            Mesh("branches", t.Branches.Count > 0 ? t.Branches[0] : null);
            Mesh("fronds", t.Fronds.Count > 0 ? t.Fronds[0] : null);
            if (t.Leaves.Count > 0)
            {
                var l = t.Leaves[0];
                w.WriteLine("g leaves");
                for (int i = 0; i < l.Count; i++)
                {
                    for (int k = 0; k < 4; k++)
                        w.WriteLine(string.Format(ci, "v {0} {1} {2}", (l.Centers[3 * i] + l.Corners[8 * i + 2 * k]) / 100,
                            (l.Centers[3 * i + 1] + l.Corners[8 * i + 2 * k + 1]) / 100, l.Centers[3 * i + 2] / 100));
                    for (int k = 0; k < 4; k++) w.WriteLine(string.Format(ci, "vt {0} {1}", l.Uv[8 * i + 2 * k], l.Uv[8 * i + 2 * k + 1]));
                    int v = vbase + 4 * i;
                    w.WriteLine($"f {v}/{v} {v + 3}/{v + 3} {v + 2}/{v + 2} {v + 1}/{v + 1}");
                }
            }
        }
    }
}
