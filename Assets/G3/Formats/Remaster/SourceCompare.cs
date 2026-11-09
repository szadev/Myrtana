using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using G3.Formats.Image;

namespace G3.Formats.Remaster
{
    /// <summary>
    /// Numerical comparison of two Source glbs of the same part (editor export vs offline export,
    /// Docs/remaster-pipeline.md §12): node names/order/parents and local TRS, per mesh the counts, positions, normals,
    /// UVs, triangles, joints, inverse bind matrices and weights (per joint name), material names, and embedded base
    /// colour images (decoded pixels).
    /// </summary>
    public sealed class SourceCompareReport
    {
        public readonly List<string> Errors = new List<string>();
        public readonly List<string> Lines = new List<string>();
        public double MaxNodeT, MaxNodeRDeg, MaxPos, MaxNormal, MaxUv, MaxIbm, MaxWeight;
        public int MaxPixel = -1;
        public double MeanPixel;
        public bool Passed => Errors.Count == 0;

        public string ToText()
        {
            var sb = new StringBuilder();
            sb.AppendLine(Passed ? "EQUIVALENT" : "DIFFERENT");
            sb.AppendLine($"  nodes: max dT {MaxNodeT * 1000:0.####} mm, max dR {MaxNodeRDeg:0.#####} deg");
            sb.AppendLine($"  meshes: max dPos {MaxPos * 1000:0.####} mm, dNormal {MaxNormal:0.######}, dUV {MaxUv:0.######}, dIBM {MaxIbm:0.######}, dWeight {MaxWeight:0.######}");
            if (MaxPixel >= 0) sb.AppendLine($"  images: max channel diff {MaxPixel}, mean {MeanPixel:0.####}");
            foreach (var l in Lines) sb.AppendLine("  " + l);
            foreach (var e in Errors) sb.AppendLine("  ERROR " + e);
            return sb.ToString().TrimEnd();
        }
    }

    public static class SourceCompare
    {
        /// <summary>Tolerances: 0.01 mm, 0.001°, 1e-4 for unit vectors/UVs/IBM/weights, 8 levels per channel (GPU vs CPU DXT decoding).</summary>
        public const double TolPos = 1e-5, TolDeg = 1e-3, TolUnit = 1e-4;
        public const int TolPixel = 8;

        public static SourceCompareReport Compare(RigModel a, RigModel b)
        {
            var r = new SourceCompareReport();
            // Nodes (rig nodes only: mesh carrier nodes are compared with their meshes).
            var na = a.Nodes.Where(n => !n.CarriesMesh).ToList();
            var nb = b.Nodes.Where(n => !n.CarriesMesh).ToList();
            if (na.Count != nb.Count) r.Errors.Add($"node count {na.Count} vs {nb.Count}");
            for (int i = 0; i < Math.Min(na.Count, nb.Count); i++)
            {
                var x = na[i]; var y = nb[i];
                if (!string.Equals(x.Name, y.Name, StringComparison.Ordinal)) { r.Errors.Add($"node {i}: {x.Name} vs {y.Name}"); continue; }
                string px = x.Parent >= 0 ? a.Nodes[x.Parent].Name : null, py = y.Parent >= 0 ? b.Nodes[y.Parent].Name : null;
                if (px != py) r.Errors.Add($"node {x.Name}: parent {px} vs {py}");
                r.MaxNodeT = Math.Max(r.MaxNodeT, Dist(x.T, y.T));
                r.MaxNodeRDeg = Math.Max(r.MaxNodeRDeg, QuatAngleDeg(x.R, y.R));
            }
            if (r.MaxNodeT > TolPos) r.Errors.Add($"node translations differ by {r.MaxNodeT * 1000:0.###} mm");
            if (r.MaxNodeRDeg > TolDeg) r.Errors.Add($"node rotations differ by {r.MaxNodeRDeg:0.####} deg");

            if (a.Meshes.Count != b.Meshes.Count) r.Errors.Add($"mesh count {a.Meshes.Count} vs {b.Meshes.Count}");
            foreach (var ma in a.Meshes)
            {
                var mb = b.Meshes.FirstOrDefault(m => m.Name == ma.Name);
                if (mb == null) { r.Errors.Add($"mesh {ma.Name} missing"); continue; }
                CompareMesh(a, b, ma, mb, r);
            }

            var mats = a.Materials.Select(m => m.Name).ToList();
            var matsB = b.Materials.Select(m => m.Name).ToList();
            if (!mats.SequenceEqual(matsB)) r.Errors.Add($"materials [{string.Join(", ", mats)}] vs [{string.Join(", ", matsB)}]");
            long sum = 0, count = 0;
            foreach (var m in a.Materials)
            {
                var o = b.Materials.FirstOrDefault(x => x.Name == m.Name);
                if (o == null) continue;
                if ((m.BaseColorImage == null) != (o.BaseColorImage == null)) { r.Errors.Add($"material {m.Name}: image present {m.BaseColorImage != null} vs {o.BaseColorImage != null}"); continue; }
                if (m.BaseColorImage == null) continue;
                var pa = Png.Decode(m.BaseColorImage, out int wa, out int ha);
                var pb = Png.Decode(o.BaseColorImage, out int wb, out int hb);
                if (wa != wb || ha != hb) { r.Errors.Add($"material {m.Name}: image {wa}x{ha} vs {wb}x{hb}"); continue; }
                int max = 0;
                for (int i = 0; i < pa.Length; i++) { int d = Math.Abs(pa[i] - pb[i]); if (d > max) max = d; sum += d; }
                count += pa.Length;
                r.MaxPixel = Math.Max(r.MaxPixel, max);
                if (max > TolPixel)
                {
                    var flipped = Png.FlipRows(pb, wb, hb);
                    int maxF = 0;
                    for (int i = 0; i < pa.Length; i++) maxF = Math.Max(maxF, Math.Abs(pa[i] - flipped[i]));
                    r.Errors.Add($"material {m.Name}: image max channel diff {max} (flipped rows: {maxF})");
                }
            }
            if (count > 0) r.MeanPixel = (double)sum / count;
            return r;
        }

        static void CompareMesh(RigModel a, RigModel b, RigMesh x, RigMesh y, SourceCompareReport r)
        {
            string n = x.Name;
            if (x.VertexCount != y.VertexCount) { r.Errors.Add($"{n}: vertices {x.VertexCount} vs {y.VertexCount}"); return; }
            if (x.TriangleCount != y.TriangleCount) r.Errors.Add($"{n}: triangles {x.TriangleCount} vs {y.TriangleCount}");
            r.Lines.Add($"{n}: {x.VertexCount} v, {x.TriangleCount} tris, {x.SubMeshes.Count} slots, {(x.IsSkinned ? x.Joints.Length + " joints" : "static on " + NodeName(a, x.Node))}");
            r.MaxPos = Math.Max(r.MaxPos, MaxDiff(x.Positions, y.Positions));
            if ((x.Normals == null) != (y.Normals == null)) r.Errors.Add($"{n}: normals present {x.Normals != null} vs {y.Normals != null}");
            else if (x.Normals != null) r.MaxNormal = Math.Max(r.MaxNormal, MaxDiff(x.Normals, y.Normals));
            foreach (var (ua, ub, label) in new[] { (x.Uv0, y.Uv0, "uv0"), (x.Uv1, y.Uv1, "uv1") })
            {
                if ((ua == null) != (ub == null)) r.Errors.Add($"{n}: {label} present {ua != null} vs {ub != null}");
                else if (ua != null) r.MaxUv = Math.Max(r.MaxUv, MaxDiff(ua, ub));
            }
            if (x.SubMeshes.Count != y.SubMeshes.Count) r.Errors.Add($"{n}: slots {x.SubMeshes.Count} vs {y.SubMeshes.Count}");
            for (int s = 0; s < Math.Min(x.SubMeshes.Count, y.SubMeshes.Count); s++)
            {
                if (x.SubMeshes[s].Material != y.SubMeshes[s].Material) r.Errors.Add($"{n}: slot {s} material {x.SubMeshes[s].Material} vs {y.SubMeshes[s].Material}");
                if (!x.SubMeshes[s].Indices.SequenceEqual(y.SubMeshes[s].Indices)) r.Errors.Add($"{n}: slot {s} triangles differ");
            }
            if (x.IsSkinned != y.IsSkinned) { r.Errors.Add($"{n}: skinned {x.IsSkinned} vs {y.IsSkinned}"); return; }
            if (!x.IsSkinned)
            {
                if (NodeName(a, x.Node) != NodeName(b, y.Node)) r.Errors.Add($"{n}: on {NodeName(a, x.Node)} vs {NodeName(b, y.Node)}");
                return;
            }
            var ja = x.Joints.Select(j => a.Nodes[j].Name).ToList();
            var jb = y.Joints.Select(j => b.Nodes[j].Name).ToList();
            if (!ja.SequenceEqual(jb)) r.Errors.Add($"{n}: joints differ ({ja.Count} vs {jb.Count})");
            for (int j = 0; j < Math.Min(x.InverseBind.Length, y.InverseBind.Length); j++)
                r.MaxIbm = Math.Max(r.MaxIbm, MaxDiff(x.InverseBind[j], y.InverseBind[j]));
            for (int v = 0; v < x.VertexCount; v++)
            {
                var wa = Influences(a, x, v); var wb = Influences(b, y, v);
                foreach (var k in wa.Keys.Union(wb.Keys))
                    r.MaxWeight = Math.Max(r.MaxWeight, Math.Abs((wa.TryGetValue(k, out var p) ? p : 0) - (wb.TryGetValue(k, out var q) ? q : 0)));
            }
            if (r.MaxPos > TolPos) r.Errors.Add($"{n}: positions differ by {r.MaxPos * 1000:0.###} mm");
            if (r.MaxNormal > TolUnit) r.Errors.Add($"{n}: normals differ by {r.MaxNormal:0.######}");
            if (r.MaxUv > TolUnit) r.Errors.Add($"{n}: uvs differ by {r.MaxUv:0.######}");
            if (r.MaxIbm > TolUnit) r.Errors.Add($"{n}: inverse bind matrices differ by {r.MaxIbm:0.######}");
            if (r.MaxWeight > TolUnit) r.Errors.Add($"{n}: weights differ by {r.MaxWeight:0.######}");
        }

        static Dictionary<string, double> Influences(RigModel model, RigMesh m, int v)
        {
            var d = new Dictionary<string, double>();
            for (int k = 0; k < m.Stride; k++)
            {
                float w = m.Weights[v * m.Stride + k];
                if (w <= 0) continue;
                string name = model.Nodes[m.Joints[m.JointIndices[v * m.Stride + k]]].Name;
                d[name] = (d.TryGetValue(name, out var o) ? o : 0) + w;
            }
            return d;
        }

        /// <summary>Name of a node; a glTF mesh carrier node (as read from a file) stands for its parent, the slot.</summary>
        static string NodeName(RigModel m, int i)
        {
            if (i >= 0 && i < m.Nodes.Count && m.Nodes[i].CarriesMesh) i = m.Nodes[i].Parent;
            return i >= 0 && i < m.Nodes.Count ? m.Nodes[i].Name : "(none)";
        }

        static double MaxDiff(float[] a, float[] b)
        {
            if (a.Length != b.Length) return double.PositiveInfinity;
            double m = 0;
            for (int i = 0; i < a.Length; i++) m = Math.Max(m, Math.Abs(a[i] - b[i]));
            return m;
        }

        static double MaxDiff(double[] a, double[] b)
        {
            double m = 0;
            for (int i = 0; i < Math.Min(a.Length, b.Length); i++) m = Math.Max(m, Math.Abs(a[i] - b[i]));
            return m;
        }

        static double Dist(double[] a, double[] b) => Math.Sqrt(Enumerable.Range(0, 3).Sum(i => (a[i] - b[i]) * (a[i] - b[i])));

        static double QuatAngleDeg(double[] a, double[] b)
        {
            double la = Math.Sqrt(a.Sum(v => v * v)), lb = Math.Sqrt(b.Sum(v => v * v));
            double dot = Math.Abs((a[0] * b[0] + a[1] * b[1] + a[2] * b[2] + a[3] * b[3]) / (la * lb));
            return 2 * Math.Acos(Math.Min(1, dot)) * 180 / Math.PI;
        }
    }
}
