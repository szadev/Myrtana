using System;
using System.Collections.Generic;

namespace G3.Formats.Remaster
{
    /// <summary>
    /// A skinned character in exchange form (Docs/remaster-characters.md, phase R0): the node hierarchy (bones, Slot_*
    /// nodes, face bones) in its bind pose, skinned and static meshes, and material slots. Space and units are glTF's
    /// (right-handed, Y up, +Z forward, metres); <see cref="MirrorX"/> converts from/to Unity's left-handed space.
    /// Written and read by <see cref="Glb"/>, compared by <see cref="RigCheck"/>.
    /// </summary>
    public sealed class RigModel
    {
        public string Generator = "";
        public readonly List<RigNode> Nodes = new List<RigNode>();
        public readonly List<RigMesh> Meshes = new List<RigMesh>();
        public readonly List<RigMaterial> Materials = new List<RigMaterial>();

        public int FindNode(string name)
        {
            for (int i = 0; i < Nodes.Count; i++)
                if (string.Equals(Nodes[i].Name, name, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        public int AddNode(string name, int parent, double[] t = null, double[] r = null)
        {
            Nodes.Add(new RigNode { Name = name, Parent = parent, T = t ?? new double[3], R = r ?? new double[] { 0, 0, 0, 1 } });
            return Nodes.Count - 1;
        }

        /// <summary>Local matrix of a node (row-major, see <see cref="M4"/>).</summary>
        public double[] Local(int node) { var n = Nodes[node]; return M4.Trs(n.T, n.R, n.S); }

        /// <summary>World (scene) matrix of a node in the stored pose.</summary>
        public double[] World(int node)
        {
            var m = Local(node);
            for (int p = Nodes[node].Parent; p >= 0; p = Nodes[p].Parent) m = M4.Mul(Local(p), m);
            return m;
        }

        public double[][] Worlds()
        {
            var w = new double[Nodes.Count][];
            for (int i = 0; i < w.Length; i++) w[i] = World(i);
            return w;
        }

        /// <summary>
        /// Rest positions and normals of a mesh in scene space: skinned vertices through Σ w·(world(joint)·inverseBind),
        /// static vertices through the world matrix of their node. Independent of how a tool laid out its armature objects.
        /// </summary>
        public void RestWorld(RigMesh m, out float[] positions, out float[] normals)
        {
            int nv = m.VertexCount;
            positions = new float[nv * 3];
            normals = m.Normals != null ? new float[nv * 3] : null;
            var worlds = Worlds();
            double[][] skin = null;
            if (m.IsSkinned)
            {
                skin = new double[m.Joints.Length][];
                for (int j = 0; j < skin.Length; j++) skin[j] = M4.Mul(worlds[m.Joints[j]], m.InverseBind[j]);
            }
            var nodeM = m.Node >= 0 ? worlds[m.Node] : M4.Identity();
            var acc = new double[16];
            for (int v = 0; v < nv; v++)
            {
                double[] mat;
                if (skin == null) mat = nodeM;
                else
                {
                    Array.Clear(acc, 0, 16);
                    double sum = 0;
                    for (int k = 0; k < m.Stride; k++)
                    {
                        float w = m.Weights[v * m.Stride + k];
                        if (w <= 0) continue;
                        var s = skin[m.JointIndices[v * m.Stride + k]];
                        for (int e = 0; e < 16; e++) acc[e] += w * s[e];
                        sum += w;
                    }
                    if (sum <= 0) { mat = nodeM; }
                    else { for (int e = 0; e < 16; e++) acc[e] /= sum; mat = acc; }
                }
                var p = M4.Point(mat, m.Positions[v * 3], m.Positions[v * 3 + 1], m.Positions[v * 3 + 2]);
                positions[v * 3] = (float)p[0]; positions[v * 3 + 1] = (float)p[1]; positions[v * 3 + 2] = (float)p[2];
                if (normals != null)
                {
                    var n = M4.Dir(mat, m.Normals[v * 3], m.Normals[v * 3 + 1], m.Normals[v * 3 + 2]);
                    double l = Math.Sqrt(n[0] * n[0] + n[1] * n[1] + n[2] * n[2]);
                    if (l > 0) { n[0] /= l; n[1] /= l; n[2] /= l; }
                    normals[v * 3] = (float)n[0]; normals[v * 3 + 1] = (float)n[1]; normals[v * 3 + 2] = (float)n[2];
                }
            }
        }

        /// <summary>
        /// A morph's deltas in scene space through the same rest matrix per vertex as <see cref="RestWorld"/> (linear part
        /// only: deltas are directions; normal deltas are not renormalised).
        /// </summary>
        public float[] RestWorldDeltas(RigMesh m, float[] deltas)
        {
            if (deltas == null) return null;
            int nv = m.VertexCount;
            var r = new float[nv * 3];
            var worlds = Worlds();
            double[][] skin = null;
            if (m.IsSkinned)
            {
                skin = new double[m.Joints.Length][];
                for (int j = 0; j < skin.Length; j++) skin[j] = M4.Mul(worlds[m.Joints[j]], m.InverseBind[j]);
            }
            var nodeM = m.Node >= 0 ? worlds[m.Node] : M4.Identity();
            var acc = new double[16];
            for (int v = 0; v < nv; v++)
            {
                double[] mat = nodeM;
                if (skin != null)
                {
                    Array.Clear(acc, 0, 16);
                    double sum = 0;
                    for (int k = 0; k < m.Stride; k++)
                    {
                        float w = m.Weights[v * m.Stride + k];
                        if (w <= 0) continue;
                        var s = skin[m.JointIndices[v * m.Stride + k]];
                        for (int e = 0; e < 16; e++) acc[e] += w * s[e];
                        sum += w;
                    }
                    if (sum > 0) { for (int e = 0; e < 16; e++) acc[e] /= sum; mat = acc; }
                }
                var d = M4.Dir(mat, deltas[v * 3], deltas[v * 3 + 1], deltas[v * 3 + 2]);
                r[v * 3] = (float)d[0]; r[v * 3 + 1] = (float)d[1]; r[v * 3 + 2] = (float)d[2];
            }
            return r;
        }

        /// <summary>
        /// Mirrors the model at the YZ plane (x → −x) and reverses the triangle winding: converts Unity space
        /// (left-handed, clockwise front faces) to glTF space (right-handed, counter-clockwise) and back.
        /// </summary>
        public void MirrorX()
        {
            foreach (var n in Nodes)
            {
                n.T[0] = -n.T[0];
                n.R = new[] { n.R[0], -n.R[1], -n.R[2], n.R[3] };
            }
            foreach (var m in Meshes)
            {
                for (int i = 0; i < m.Positions.Length; i += 3) m.Positions[i] = -m.Positions[i];
                if (m.Normals != null) for (int i = 0; i < m.Normals.Length; i += 3) m.Normals[i] = -m.Normals[i];
                if (m.InverseBind != null)
                    for (int j = 0; j < m.InverseBind.Length; j++) m.InverseBind[j] = M4.MirrorX(m.InverseBind[j]);
                foreach (var sm in m.SubMeshes)
                    for (int t = 0; t + 2 < sm.Indices.Length; t += 3) { int a = sm.Indices[t + 1]; sm.Indices[t + 1] = sm.Indices[t + 2]; sm.Indices[t + 2] = a; }
                foreach (var mo in m.Morphs)
                {
                    for (int i = 0; i < mo.Positions.Length; i += 3) mo.Positions[i] = -mo.Positions[i];
                    if (mo.Normals != null) for (int i = 0; i < mo.Normals.Length; i += 3) mo.Normals[i] = -mo.Normals[i];
                }
            }
        }
    }

    public sealed class RigNode
    {
        public string Name;
        public int Parent = -1;
        public double[] T = new double[3];
        public double[] R = { 0, 0, 0, 1 };   // x, y, z, w
        public double[] S = { 1, 1, 1 };
        /// <summary>The node only carries a mesh (glTF mesh node read from a file): not part of the rig contract.</summary>
        public bool CarriesMesh;
        public override string ToString() => Name;
    }

    public sealed class RigSubMesh
    {
        public string Material;
        public int[] Indices;
    }

    public sealed class RigMesh
    {
        public string Name;
        /// <summary>Node the mesh hangs on (static meshes: hair, beard); -1 for a skinned mesh at the scene root.</summary>
        public int Node = -1;
        /// <summary>Joint node indices (null = static mesh).</summary>
        public int[] Joints;
        /// <summary>Inverse bind matrix per joint (row-major).</summary>
        public double[][] InverseBind;
        public float[] Positions, Normals, Uv0, Uv1;
        /// <summary>Influences per vertex in <see cref="JointIndices"/>/<see cref="Weights"/> (4, or 8 when a file had JOINTS_1).</summary>
        public int Stride = 4;
        public int[] JointIndices;
        public float[] Weights;
        public readonly List<RigSubMesh> SubMeshes = new List<RigSubMesh>();
        /// <summary>Morph targets (glTF <c>targets</c>, names from <c>extras.targetNames</c>): per-vertex deltas in mesh space.</summary>
        public readonly List<RigMorph> Morphs = new List<RigMorph>();

        public bool IsSkinned => Joints != null && Joints.Length > 0 && Weights != null;
        public int VertexCount => Positions.Length / 3;
        public int TriangleCount { get { int n = 0; foreach (var s in SubMeshes) n += s.Indices.Length / 3; return n; } }
        public override string ToString() => Name;
    }

    /// <summary>One blend shape: position (and optional normal) deltas for every vertex of its mesh, x y z.</summary>
    public sealed class RigMorph
    {
        public string Name;
        public float[] Positions, Normals;
        public override string ToString() => Name;
    }

    public sealed class RigMaterial
    {
        public string Name;
        /// <summary>Embedded base colour / normal map images (PNG or JPEG bytes), optional.</summary>
        public byte[] BaseColorImage, NormalImage;
        public string BaseColorMime = "image/png", NormalMime = "image/png";
        public override string ToString() => Name;
    }

    /// <summary>4×4 matrices as row-major double[16] (element (r, c) at r*4+c), column vectors: p' = M·p.</summary>
    public static class M4
    {
        public static double[] Identity() => new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };

        public static double[] Mul(double[] a, double[] b)
        {
            var r = new double[16];
            for (int i = 0; i < 4; i++)
                for (int j = 0; j < 4; j++)
                    r[i * 4 + j] = a[i * 4] * b[j] + a[i * 4 + 1] * b[4 + j] + a[i * 4 + 2] * b[8 + j] + a[i * 4 + 3] * b[12 + j];
            return r;
        }

        public static double[] Trs(double[] t, double[] q, double[] s)
        {
            double x = q[0], y = q[1], z = q[2], w = q[3];
            double l = Math.Sqrt(x * x + y * y + z * z + w * w);
            if (l > 0) { x /= l; y /= l; z /= l; w /= l; }
            double sx = s?[0] ?? 1, sy = s?[1] ?? 1, sz = s?[2] ?? 1;
            return new[]
            {
                (1 - 2 * (y * y + z * z)) * sx, 2 * (x * y - z * w) * sy, 2 * (x * z + y * w) * sz, t[0],
                2 * (x * y + z * w) * sx, (1 - 2 * (x * x + z * z)) * sy, 2 * (y * z - x * w) * sz, t[1],
                2 * (x * z - y * w) * sx, 2 * (y * z + x * w) * sy, (1 - 2 * (x * x + y * y)) * sz, t[2],
                0, 0, 0, 1,
            };
        }

        /// <summary>Splits an affine matrix without shear into translation, rotation (x, y, z, w) and scale.</summary>
        public static void Decompose(double[] m, out double[] t, out double[] q, out double[] s)
        {
            t = new[] { m[3], m[7], m[11] };
            s = new[] { Len(m[0], m[4], m[8]), Len(m[1], m[5], m[9]), Len(m[2], m[6], m[10]) };
            if (Det3(m) < 0) s[0] = -s[0];
            double r00 = m[0] / s[0], r10 = m[4] / s[0], r20 = m[8] / s[0];
            double r01 = m[1] / s[1], r11 = m[5] / s[1], r21 = m[9] / s[1];
            double r02 = m[2] / s[2], r12 = m[6] / s[2], r22 = m[10] / s[2];
            q = FromRotation(r00, r01, r02, r10, r11, r12, r20, r21, r22);
        }

        public static double[] FromRotation(double r00, double r01, double r02, double r10, double r11, double r12, double r20, double r21, double r22)
        {
            double tr = r00 + r11 + r22, x, y, z, w;
            if (tr > 0) { double s = Math.Sqrt(tr + 1) * 2; w = s / 4; x = (r21 - r12) / s; y = (r02 - r20) / s; z = (r10 - r01) / s; }
            else if (r00 > r11 && r00 > r22) { double s = Math.Sqrt(1 + r00 - r11 - r22) * 2; w = (r21 - r12) / s; x = s / 4; y = (r01 + r10) / s; z = (r02 + r20) / s; }
            else if (r11 > r22) { double s = Math.Sqrt(1 + r11 - r00 - r22) * 2; w = (r02 - r20) / s; x = (r01 + r10) / s; y = s / 4; z = (r12 + r21) / s; }
            else { double s = Math.Sqrt(1 + r22 - r00 - r11) * 2; w = (r10 - r01) / s; x = (r02 + r20) / s; y = (r12 + r21) / s; z = s / 4; }
            return new[] { x, y, z, w };
        }

        static double Len(double a, double b, double c) => Math.Sqrt(a * a + b * b + c * c);
        static double Det3(double[] m) =>
            m[0] * (m[5] * m[10] - m[6] * m[9]) - m[1] * (m[4] * m[10] - m[6] * m[8]) + m[2] * (m[4] * m[9] - m[5] * m[8]);

        public static double[] Inverse(double[] m)
        {
            var inv = new double[16];
            inv[0] = m[5] * m[10] * m[15] - m[5] * m[11] * m[14] - m[9] * m[6] * m[15] + m[9] * m[7] * m[14] + m[13] * m[6] * m[11] - m[13] * m[7] * m[10];
            inv[4] = -m[4] * m[10] * m[15] + m[4] * m[11] * m[14] + m[8] * m[6] * m[15] - m[8] * m[7] * m[14] - m[12] * m[6] * m[11] + m[12] * m[7] * m[10];
            inv[8] = m[4] * m[9] * m[15] - m[4] * m[11] * m[13] - m[8] * m[5] * m[15] + m[8] * m[7] * m[13] + m[12] * m[5] * m[11] - m[12] * m[7] * m[9];
            inv[12] = -m[4] * m[9] * m[14] + m[4] * m[10] * m[13] + m[8] * m[5] * m[14] - m[8] * m[6] * m[13] - m[12] * m[5] * m[10] + m[12] * m[6] * m[9];
            inv[1] = -m[1] * m[10] * m[15] + m[1] * m[11] * m[14] + m[9] * m[2] * m[15] - m[9] * m[3] * m[14] - m[13] * m[2] * m[11] + m[13] * m[3] * m[10];
            inv[5] = m[0] * m[10] * m[15] - m[0] * m[11] * m[14] - m[8] * m[2] * m[15] + m[8] * m[3] * m[14] + m[12] * m[2] * m[11] - m[12] * m[3] * m[10];
            inv[9] = -m[0] * m[9] * m[15] + m[0] * m[11] * m[13] + m[8] * m[1] * m[15] - m[8] * m[3] * m[13] - m[12] * m[1] * m[11] + m[12] * m[3] * m[9];
            inv[13] = m[0] * m[9] * m[14] - m[0] * m[10] * m[13] - m[8] * m[1] * m[14] + m[8] * m[2] * m[13] + m[12] * m[1] * m[10] - m[12] * m[2] * m[9];
            inv[2] = m[1] * m[6] * m[15] - m[1] * m[7] * m[14] - m[5] * m[2] * m[15] + m[5] * m[3] * m[14] + m[13] * m[2] * m[7] - m[13] * m[3] * m[6];
            inv[6] = -m[0] * m[6] * m[15] + m[0] * m[7] * m[14] + m[4] * m[2] * m[15] - m[4] * m[3] * m[14] - m[12] * m[2] * m[7] + m[12] * m[3] * m[6];
            inv[10] = m[0] * m[5] * m[15] - m[0] * m[7] * m[13] - m[4] * m[1] * m[15] + m[4] * m[3] * m[13] + m[12] * m[1] * m[7] - m[12] * m[3] * m[5];
            inv[14] = -m[0] * m[5] * m[14] + m[0] * m[6] * m[13] + m[4] * m[1] * m[14] - m[4] * m[2] * m[13] - m[12] * m[1] * m[6] + m[12] * m[2] * m[5];
            inv[3] = -m[1] * m[6] * m[11] + m[1] * m[7] * m[10] + m[5] * m[2] * m[11] - m[5] * m[3] * m[10] - m[9] * m[2] * m[7] + m[9] * m[3] * m[6];
            inv[7] = m[0] * m[6] * m[11] - m[0] * m[7] * m[10] - m[4] * m[2] * m[11] + m[4] * m[3] * m[10] + m[8] * m[2] * m[7] - m[8] * m[3] * m[6];
            inv[11] = -m[0] * m[5] * m[11] + m[0] * m[7] * m[9] + m[4] * m[1] * m[11] - m[4] * m[3] * m[9] - m[8] * m[1] * m[7] + m[8] * m[3] * m[5];
            inv[15] = m[0] * m[5] * m[10] - m[0] * m[6] * m[9] - m[4] * m[1] * m[10] + m[4] * m[2] * m[9] + m[8] * m[1] * m[6] - m[8] * m[2] * m[5];
            double det = m[0] * inv[0] + m[1] * inv[4] + m[2] * inv[8] + m[3] * inv[12];
            if (Math.Abs(det) < 1e-30) throw new InvalidOperationException("singular matrix");
            for (int i = 0; i < 16; i++) inv[i] /= det;
            return inv;
        }

        public static double[] Point(double[] m, double x, double y, double z) => new[]
        {
            m[0] * x + m[1] * y + m[2] * z + m[3], m[4] * x + m[5] * y + m[6] * z + m[7], m[8] * x + m[9] * y + m[10] * z + m[11],
        };

        public static double[] Dir(double[] m, double x, double y, double z) => new[]
        {
            m[0] * x + m[1] * y + m[2] * z, m[4] * x + m[5] * y + m[6] * z, m[8] * x + m[9] * y + m[10] * z,
        };

        /// <summary>D·M·D with D = diag(−1, 1, 1, 1): the same transform seen in the mirrored space.</summary>
        public static double[] MirrorX(double[] m)
        {
            var r = (double[])m.Clone();
            for (int i = 0; i < 4; i++)
                for (int j = 0; j < 4; j++)
                    if ((i == 0) != (j == 0)) r[i * 4 + j] = -r[i * 4 + j];
            return r;
        }

        /// <summary>Distance between the translations of two matrices.</summary>
        public static double TranslationDelta(double[] a, double[] b) => Len(a[3] - b[3], a[7] - b[7], a[11] - b[11]);

        /// <summary>Largest angle (degrees) between corresponding normalised basis axes of two matrices.</summary>
        public static double AxisDeltaDegrees(double[] a, double[] b)
        {
            double worst = 0;
            for (int c = 0; c < 3; c++)
            {
                double ax = a[c], ay = a[4 + c], az = a[8 + c], bx = b[c], by = b[4 + c], bz = b[8 + c];
                double la = Len(ax, ay, az), lb = Len(bx, by, bz);
                if (la <= 0 || lb <= 0) continue;
                double d = Math.Max(-1, Math.Min(1, (ax * bx + ay * by + az * bz) / (la * lb)));
                worst = Math.Max(worst, Math.Acos(d) * 180 / Math.PI);
            }
            return worst;
        }
    }
}
