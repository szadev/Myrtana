using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace G3.Formats.Remaster
{
    /// <summary>
    /// Eye contract of a FACES head (fix-52, after editor-runner-30's in-game A/B: "eyes pitch black"):
    /// <list type="bullet">
    /// <item>E2: the generated eyeball and cornea dome face outwards (winding and vertex normals). eyegen.py up to v6 built
    /// both inside out (fan order clockwise seen from the front, Blender's normals followed): with Cull Back the camera saw
    /// the far inner wall of the eyeball (the planar eye texture repeats there) and the cornea dome not at all.</item>
    /// <item>E1: no skin triangle inside an eyeball. The lid fit leaves MakeHuman's eye pocket and G3's eye-cap triangles
    /// (sampling the dark painted G3 eye) inside the sphere; in front of the far wall they were the black eyes. Behind a
    /// correctly oriented eyeball they are hidden, except through the concave iris, so they are dropped.</item>
    /// </list>
    /// <see cref="Repair"/> fixes a loaded head in place (the Unity importer: no Blender re-batch), <see cref="Check"/>
    /// reports the contract (<see cref="RigCheck"/>, Tools/Blender/remaster/validate.py has the same rules).
    /// Works in any space where cross(p1 − p0, p2 − p0) points to the front of a triangle (RigModel, before or after
    /// <see cref="RigModel.MirrorX"/>).
    /// </summary>
    public static class RemasterEyes
    {
        /// <summary>An eyeball component needs this many triangles (eyegen: 720; the caruncle has 20).</summary>
        public const int MinEyeballTriangles = 100;
        /// <summary>A skin triangle is inside when its centroid is closer than this share of the radius to the centre
        /// (the lids rest on the eyeball: their margin may dip a fraction of a millimetre in).</summary>
        public const double InsideShare = 0.97;
        /// <summary>A part is inside out when fewer than this share of its triangles face away from its centre.</summary>
        public const double OutwardMin = 0.25;
        /// <summary>The check fails when more than this share of eye vertex normals point against their faces.</summary>
        public const double InwardNormalsMax = 0.1;

        public readonly struct Sphere
        {
            public readonly double X, Y, Z, R;
            public Sphere(double x, double y, double z, double r) { X = x; Y = y; Z = z; R = r; }
            public double Distance(double x, double y, double z) => Math.Sqrt((x - X) * (x - X) + (y - Y) * (y - Y) + (z - Z) * (z - Z));
            public override string ToString() => string.Format(CultureInfo.InvariantCulture, "({0:0.0000}, {1:0.0000}, {2:0.0000}) r {3:0.0000}", X, Y, Z, R);
        }

        public sealed class Result
        {
            public readonly List<Sphere> Eyeballs = new List<Sphere>();
            /// <summary>Eye/cornea parts whose winding was reversed.</summary>
            public int InsideOutParts;
            /// <summary>Eye/cornea vertex normals turned to their faces.</summary>
            public int FlippedNormals;
            /// <summary>Skin triangles removed from inside the eyeballs.</summary>
            public int DroppedTriangles;
            public bool Changed => InsideOutParts > 0 || FlippedNormals > 0 || DroppedTriangles > 0;
            public override string ToString() =>
                $"eyes: {Eyeballs.Count} eyeballs, {InsideOutParts} inside-out parts turned, {FlippedNormals} normals flipped, " +
                $"{DroppedTriangles} skin triangles inside the eyeballs dropped";
        }

        public sealed class Report
        {
            public readonly List<Sphere> Eyeballs = new List<Sphere>();
            public int Parts, InsideOutParts, EyeVertices, InwardNormals, SkinInside;
            public bool HasEyes => Eyeballs.Count > 0;
            public bool Passed => InsideOutParts == 0 && InwardNormals <= InwardNormalsMax * Math.Max(1, EyeVertices) && SkinInside == 0;
            public override string ToString() =>
                $"{Eyeballs.Count} eyeballs (r {string.Join("/", Eyeballs.Select(s => (s.R * 1000).ToString("0.0", CultureInfo.InvariantCulture)))} mm); " +
                $"{InsideOutParts}/{Parts} eye/cornea parts inside out, {InwardNormals}/{EyeVertices} eye normals inward, " +
                $"{SkinInside} skin triangles inside an eyeball";
        }

        /// <summary>Plausible eyeball radius (m): G3 heads 11-17 mm; a bigger "sphere" is not an eyeball (hero-v2).</summary>
        public const double MinRadius = 0.004, MaxRadius = 0.03;

        /// <summary>The eyeball material: v1-v6 (G3_Remaster_Eye*) or the v2 hero's (G3_Remaster_V2_Eye*, cornea bulge built in).</summary>
        static bool IsEyeball(string material)
        {
            var k = RemasterGenerated.Classify(material);
            return k == RemasterGeneratedKind.Eye || k == RemasterGeneratedKind.EyeV2;
        }

        static bool IsEyePart(string material) =>
            IsEyeball(material) || RemasterGenerated.Classify(material) == RemasterGeneratedKind.Cornea;

        /// <summary>Skin slots whose triangles must not hide inside an eyeball: original G3 slots and the v2 skin.</summary>
        static bool IsSkin(string material)
        {
            // MetaHuman hero inner lids (set name ends in "Lid"): inside the eyeball at rest, over it in a blink
            if (material != null && material.StartsWith("G3_Remaster_V2_Skin__", StringComparison.Ordinal) &&
                RemasterGenerated.Classify(material) == RemasterGeneratedKind.SkinV2 && material.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '.').EndsWith("Lid", StringComparison.Ordinal))
                return false;
            var k = RemasterGenerated.Classify(material);
            return k == RemasterGeneratedKind.None || k == RemasterGeneratedKind.SkinV2;
        }

        /// <summary>Whether the mesh carries a generated eye (only FACES heads are checked/repaired).</summary>
        public static bool HasGeneratedEyes(RigMesh m) =>
            m?.SubMeshes.Any(s => IsEyeball(s.Material) && s.Indices.Length >= 3) ?? false;

        // ---------------------------------------------------------------------------------------------------------

        /// <summary>Eyeball spheres: least-squares fit of every large connected part of the eye material.</summary>
        public static List<Sphere> Eyeballs(RigMesh m)
        {
            var list = new List<Sphere>();
            if (m?.Positions == null) return list;
            foreach (var sm in m.SubMeshes.Where(s => IsEyeball(s.Material)))
                foreach (var part in Parts(m, sm.Indices))
                {
                    if (part.Count < MinEyeballTriangles) continue;
                    var verts = part.SelectMany(t => new[] { sm.Indices[t * 3], sm.Indices[t * 3 + 1], sm.Indices[t * 3 + 2] }).Distinct().ToList();
                    if (FitSphere(m.Positions, verts, out var s) && s.R >= MinRadius && s.R <= MaxRadius) list.Add(s);
                }
            return list;
        }

        /// <summary>Turns inside-out eye/cornea parts, points eye normals along their faces and drops skin triangles
        /// inside the eyeballs. In place; no-op for meshes without a generated eye.</summary>
        public static Result Repair(RigMesh m)
        {
            var r = new Result();
            if (!HasGeneratedEyes(m)) return r;
            r.Eyeballs.AddRange(Eyeballs(m));
            foreach (var sm in m.SubMeshes.Where(s => IsEyePart(s.Material)))
                foreach (var part in Parts(m, sm.Indices))
                    if (IsInsideOut(m.Positions, sm.Indices, part, r.Eyeballs))
                    {
                        foreach (int t in part)
                        {
                            int a = sm.Indices[t * 3 + 1];
                            sm.Indices[t * 3 + 1] = sm.Indices[t * 3 + 2];
                            sm.Indices[t * 3 + 2] = a;
                        }
                        r.InsideOutParts++;
                    }
            if (m.Normals != null)
            {
                var acc = FaceNormalSums(m, IsEyePart);
                foreach (var kv in acc)
                {
                    int v = kv.Key;
                    var g = kv.Value;
                    double d = m.Normals[v * 3] * g[0] + m.Normals[v * 3 + 1] * g[1] + m.Normals[v * 3 + 2] * g[2];
                    if (d >= 0) continue;
                    m.Normals[v * 3] = -m.Normals[v * 3];
                    m.Normals[v * 3 + 1] = -m.Normals[v * 3 + 1];
                    m.Normals[v * 3 + 2] = -m.Normals[v * 3 + 2];
                    r.FlippedNormals++;
                }
            }
            if (r.Eyeballs.Count > 0)
                foreach (var sm in m.SubMeshes.Where(s => IsSkin(s.Material)))
                {
                    var keep = new List<int>(sm.Indices.Length);
                    for (int t = 0; t + 2 < sm.Indices.Length; t += 3)
                    {
                        if (InsideEyeball(m.Positions, sm.Indices[t], sm.Indices[t + 1], sm.Indices[t + 2], r.Eyeballs)) { r.DroppedTriangles++; continue; }
                        keep.Add(sm.Indices[t]); keep.Add(sm.Indices[t + 1]); keep.Add(sm.Indices[t + 2]);
                    }
                    if (keep.Count != sm.Indices.Length) sm.Indices = keep.ToArray();
                }
            return r;
        }

        /// <summary>The eye contract of a mesh (see the class summary); <see cref="Report.HasEyes"/> false without a generated eye.</summary>
        public static Report Check(RigMesh m)
        {
            var r = new Report();
            if (!HasGeneratedEyes(m)) return r;
            r.Eyeballs.AddRange(Eyeballs(m));
            foreach (var sm in m.SubMeshes.Where(s => IsEyePart(s.Material)))
                foreach (var part in Parts(m, sm.Indices))
                {
                    r.Parts++;
                    if (IsInsideOut(m.Positions, sm.Indices, part, r.Eyeballs)) r.InsideOutParts++;
                }
            if (m.Normals != null)
                foreach (var kv in FaceNormalSums(m, IsEyePart))
                {
                    int v = kv.Key;
                    var g = kv.Value;
                    r.EyeVertices++;
                    if (m.Normals[v * 3] * g[0] + m.Normals[v * 3 + 1] * g[1] + m.Normals[v * 3 + 2] * g[2] < 0) r.InwardNormals++;
                }
            if (r.Eyeballs.Count > 0)
                foreach (var sm in m.SubMeshes.Where(s => IsSkin(s.Material)))
                    for (int t = 0; t + 2 < sm.Indices.Length; t += 3)
                        if (InsideEyeball(m.Positions, sm.Indices[t], sm.Indices[t + 1], sm.Indices[t + 2], r.Eyeballs)) r.SkinInside++;
            return r;
        }

        // ---------------------------------------------------------------------------------------------------------

        static bool InsideEyeball(float[] p, int a, int b, int c, List<Sphere> eyes)
        {
            double cx = (p[a * 3] + p[b * 3] + p[c * 3]) / 3, cy = (p[a * 3 + 1] + p[b * 3 + 1] + p[c * 3 + 1]) / 3, cz = (p[a * 3 + 2] + p[b * 3 + 2] + p[c * 3 + 2]) / 3;
            foreach (var s in eyes)
            {
                if (s.Distance(cx, cy, cz) < InsideShare * s.R) return true;
                if (s.Distance(p[a * 3], p[a * 3 + 1], p[a * 3 + 2]) < s.R && s.Distance(p[b * 3], p[b * 3 + 1], p[b * 3 + 2]) < s.R &&
                    s.Distance(p[c * 3], p[c * 3 + 1], p[c * 3 + 2]) < s.R) return true;
            }
            return false;
        }

        /// <summary>
        /// Inside out = (almost) no triangle faces away from the part's own centre and none away from the nearest eyeball
        /// centre. The eyeball and the cornea dome answer both the same way; the caruncle (a blob beside the eyeball) and
        /// the wet line (a strip on the lower lid) only one, so they are never turned.
        /// </summary>
        static bool IsInsideOut(float[] p, int[] idx, List<int> part, List<Sphere> eyes)
        {
            var verts = new HashSet<int>(part.SelectMany(t => new[] { idx[t * 3], idx[t * 3 + 1], idx[t * 3 + 2] }));
            double mx = 0, my = 0, mz = 0;
            foreach (int v in verts) { mx += p[v * 3]; my += p[v * 3 + 1]; mz += p[v * 3 + 2]; }
            mx /= verts.Count; my /= verts.Count; mz /= verts.Count;
            Sphere? eye = null;
            foreach (var s in eyes)
                if (eye == null || s.Distance(mx, my, mz) < eye.Value.Distance(mx, my, mz)) eye = s;
            int n = 0, outOwn = 0, outEye = 0;
            foreach (int t in part)
            {
                int a = idx[t * 3], b = idx[t * 3 + 1], c = idx[t * 3 + 2];
                var g = Geometric(p, a, b, c);
                if (g[0] * g[0] + g[1] * g[1] + g[2] * g[2] < 1e-24) continue;
                double cx = (p[a * 3] + p[b * 3] + p[c * 3]) / 3, cy = (p[a * 3 + 1] + p[b * 3 + 1] + p[c * 3 + 1]) / 3, cz = (p[a * 3 + 2] + p[b * 3 + 2] + p[c * 3 + 2]) / 3;
                n++;
                if (g[0] * (cx - mx) + g[1] * (cy - my) + g[2] * (cz - mz) > 0) outOwn++;
                if (eye == null || g[0] * (cx - eye.Value.X) + g[1] * (cy - eye.Value.Y) + g[2] * (cz - eye.Value.Z) > 0) outEye++;
            }
            return n > 0 && outOwn < OutwardMin * n && outEye < OutwardMin * n;
        }

        static double[] Geometric(float[] p, int a, int b, int c)
        {
            double ux = p[b * 3] - p[a * 3], uy = p[b * 3 + 1] - p[a * 3 + 1], uz = p[b * 3 + 2] - p[a * 3 + 2];
            double vx = p[c * 3] - p[a * 3], vy = p[c * 3 + 1] - p[a * 3 + 1], vz = p[c * 3 + 2] - p[a * 3 + 2];
            return new[] { uy * vz - uz * vy, uz * vx - ux * vz, ux * vy - uy * vx };
        }

        /// <summary>Area-weighted face normal sum per vertex of the selected submeshes.</summary>
        static Dictionary<int, double[]> FaceNormalSums(RigMesh m, Func<string, bool> select)
        {
            var acc = new Dictionary<int, double[]>();
            foreach (var sm in m.SubMeshes.Where(s => select(s.Material)))
                for (int t = 0; t + 2 < sm.Indices.Length; t += 3)
                {
                    var g = Geometric(m.Positions, sm.Indices[t], sm.Indices[t + 1], sm.Indices[t + 2]);
                    for (int k = 0; k < 3; k++)
                    {
                        int v = sm.Indices[t + k];
                        if (!acc.TryGetValue(v, out var s)) acc[v] = s = new double[3];
                        s[0] += g[0]; s[1] += g[1]; s[2] += g[2];
                    }
                }
            return acc;
        }

        /// <summary>
        /// Connected parts of a triangle list, joined by shared vertices and by equal positions (glTF splits vertices at
        /// UV/normal seams). Each part lists its triangle numbers (triangle t = idx[3t..3t+2]).
        /// </summary>
        static List<List<int>> Parts(RigMesh m, int[] idx)
        {
            var parent = new Dictionary<int, int>();
            int Find(int x)
            {
                if (!parent.TryGetValue(x, out int p)) { parent[x] = x; return x; }
                while (p != x) { int gp = parent[p]; parent[x] = gp; x = p; p = gp; }
                return x;
            }
            void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) parent[a] = b; }
            var byPos = new Dictionary<(long, long, long), int>();
            var pos = m.Positions;
            for (int k = 0; k < idx.Length; k++)
            {
                int v = idx[k];
                var key = ((long)Math.Round(pos[v * 3] * 1e6), (long)Math.Round(pos[v * 3 + 1] * 1e6), (long)Math.Round(pos[v * 3 + 2] * 1e6));
                if (byPos.TryGetValue(key, out int w)) Union(v, w); else byPos[key] = v;
            }
            for (int t = 0; t + 2 < idx.Length; t += 3) { Union(idx[t], idx[t + 1]); Union(idx[t + 1], idx[t + 2]); }
            var parts = new Dictionary<int, List<int>>();
            for (int t = 0; t + 2 < idx.Length; t += 3)
            {
                int root = Find(idx[t]);
                if (!parts.TryGetValue(root, out var list)) parts[root] = list = new List<int>();
                list.Add(t / 3);
            }
            return parts.Values.ToList();
        }

        /// <summary>Least-squares sphere through the vertices (|p|² = 2 c·p + k, r² = k + |c|²); false when degenerate.</summary>
        public static bool FitSphere(float[] pos, IList<int> verts, out Sphere sphere)
        {
            sphere = default;
            if (verts.Count < 4) return false;
            // centred for conditioning
            double ox = 0, oy = 0, oz = 0;
            foreach (int v in verts) { ox += pos[v * 3]; oy += pos[v * 3 + 1]; oz += pos[v * 3 + 2]; }
            ox /= verts.Count; oy /= verts.Count; oz /= verts.Count;
            var ata = new double[4, 4];
            var atb = new double[4];
            foreach (int v in verts)
            {
                double x = pos[v * 3] - ox, y = pos[v * 3 + 1] - oy, z = pos[v * 3 + 2] - oz;
                double[] row = { 2 * x, 2 * y, 2 * z, 1 };
                double f = x * x + y * y + z * z;
                for (int i = 0; i < 4; i++)
                {
                    atb[i] += row[i] * f;
                    for (int j = 0; j < 4; j++) ata[i, j] += row[i] * row[j];
                }
            }
            if (!Solve(ata, atb, out var s)) return false;
            double r2 = s[3] + s[0] * s[0] + s[1] * s[1] + s[2] * s[2];
            if (!(r2 > 0)) return false;
            sphere = new Sphere(s[0] + ox, s[1] + oy, s[2] + oz, Math.Sqrt(r2));
            return true;
        }

        static bool Solve(double[,] a, double[] b, out double[] x)
        {
            int n = b.Length;
            var m = new double[n, n + 1];
            for (int i = 0; i < n; i++) { for (int j = 0; j < n; j++) m[i, j] = a[i, j]; m[i, n] = b[i]; }
            for (int c = 0; c < n; c++)
            {
                int piv = c;
                for (int i = c + 1; i < n; i++) if (Math.Abs(m[i, c]) > Math.Abs(m[piv, c])) piv = i;
                if (Math.Abs(m[piv, c]) < 1e-18) { x = null; return false; }
                if (piv != c) for (int j = 0; j <= n; j++) { double t = m[c, j]; m[c, j] = m[piv, j]; m[piv, j] = t; }
                for (int i = 0; i < n; i++)
                {
                    if (i == c) continue;
                    double f = m[i, c] / m[c, c];
                    if (f == 0) continue;
                    for (int j = c; j <= n; j++) m[i, j] -= f * m[c, j];
                }
            }
            x = new double[n];
            for (int i = 0; i < n; i++) x[i] = m[i, n] / m[i, i];
            return true;
        }
    }
}
