using System;
using System.Collections.Generic;

namespace G3.Unity
{
    /// <summary>
    /// Engine-free meshes for the generated scatter items (Docs/remaster-world.md section 18.4): flat litter patches,
    /// crossed cards for shrubs/cacti, and the fallback rock (noise-displaced icosphere) used when the AI rocks are not
    /// built. Arrays: positions/normals xyz, uv xy, triangles; pivot at the ground point (y = 0).
    /// </summary>
    public static class ScatterMeshes
    {
        public sealed class Data
        {
            public float[] Pos, Nrm, Uv;
            public int[] Tri;
            public int VertexCount => Pos.Length / 3;
        }

        /// <summary>A 1 x 1 m ground patch (n x n quads), slightly domed (2 cm) so it never z-fights the ground.</summary>
        public static Data Patch(int n = 2)
        {
            int v = (n + 1) * (n + 1);
            var d = new Data { Pos = new float[v * 3], Nrm = new float[v * 3], Uv = new float[v * 2], Tri = new int[n * n * 6] };
            for (int j = 0, k = 0; j <= n; j++)
                for (int i = 0; i <= n; i++, k++)
                {
                    float u = i / (float)n, w = j / (float)n;
                    float x = u - 0.5f, z = w - 0.5f;
                    float r2 = (x * x + z * z) * 2;
                    d.Pos[k * 3] = x; d.Pos[k * 3 + 1] = 0.02f + 0.02f * Math.Max(0, 1 - r2); d.Pos[k * 3 + 2] = z;
                    d.Nrm[k * 3 + 1] = 1;
                    d.Uv[k * 2] = u; d.Uv[k * 2 + 1] = w;
                }
            for (int j = 0, t = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                {
                    int a = j * (n + 1) + i, b = a + 1, c = a + n + 1, e = c + 1;
                    d.Tri[t++] = a; d.Tri[t++] = c; d.Tri[t++] = b;
                    d.Tri[t++] = b; d.Tri[t++] = c; d.Tri[t++] = e;
                }
            return d;
        }

        /// <summary><paramref name="cards"/> vertical 1 m cards crossed round the y axis (pivot at the bottom centre),
        /// normals bent up for soft plant lighting.</summary>
        public static Data Cross(int cards = 3, float width = 1f, float height = 1f)
        {
            var d = new Data { Pos = new float[cards * 4 * 3], Nrm = new float[cards * 4 * 3], Uv = new float[cards * 4 * 2], Tri = new int[cards * 6] };
            for (int c = 0; c < cards; c++)
            {
                double a = Math.PI * c / cards;
                float dx = (float)Math.Cos(a) * width * 0.5f, dz = (float)Math.Sin(a) * width * 0.5f;
                float nx = (float)Math.Sin(a), nz = -(float)Math.Cos(a);   // = cross(b - a, c - a) of its triangles (front)
                float[] px = { -dx, dx, -dx, dx }, py = { 0, 0, height, height }, pz = { -dz, dz, -dz, dz };
                float[] u = { 0, 1, 0, 1 }, w = { 0, 0, 1, 1 };
                for (int k = 0; k < 4; k++)
                {
                    int i = c * 4 + k;
                    d.Pos[i * 3] = px[k]; d.Pos[i * 3 + 1] = py[k]; d.Pos[i * 3 + 2] = pz[k];
                    float l = (float)Math.Sqrt(nx * nx + 0.25f + nz * nz);
                    d.Nrm[i * 3] = nx / l; d.Nrm[i * 3 + 1] = 0.5f / l; d.Nrm[i * 3 + 2] = nz / l;
                    d.Uv[i * 2] = u[k]; d.Uv[i * 2 + 1] = w[k];
                }
                int b = c * 4;
                d.Tri[c * 6] = b; d.Tri[c * 6 + 1] = b + 2; d.Tri[c * 6 + 2] = b + 1;
                d.Tri[c * 6 + 3] = b + 1; d.Tri[c * 6 + 4] = b + 2; d.Tri[c * 6 + 5] = b + 3;
            }
            return d;
        }

        /// <summary>
        /// A rock: icosphere (subdivision <paramref name="level"/>), radius ~0.5 m displaced by value noise (seeded), flattened
        /// (y x 0.62) and lifted so ~25 % sits below y = 0 (the item's sink buries the rest). Smooth normals; uv = xz (unused:
        /// solids are triplanar).
        /// </summary>
        public static Data Rock(uint seed, int level = 2)
        {
            var verts = new List<float[]>();
            var faces = new List<int[]>();
            float t = (1 + (float)Math.Sqrt(5)) / 2;
            float[][] ico =
            {
                new[] { -1, t, 0f }, new[] { 1, t, 0f }, new[] { -1, -t, 0f }, new[] { 1, -t, 0f },
                new[] { 0f, -1, t }, new[] { 0f, 1, t }, new[] { 0f, -1, -t }, new[] { 0f, 1, -t },
                new[] { t, 0f, -1 }, new[] { t, 0f, 1 }, new[] { -t, 0f, -1 }, new[] { -t, 0f, 1 },
            };
            foreach (var p in ico) verts.Add(Norm(p));
            int[][] f0 =
            {
                new[] { 0, 11, 5 }, new[] { 0, 5, 1 }, new[] { 0, 1, 7 }, new[] { 0, 7, 10 }, new[] { 0, 10, 11 },
                new[] { 1, 5, 9 }, new[] { 5, 11, 4 }, new[] { 11, 10, 2 }, new[] { 10, 7, 6 }, new[] { 7, 1, 8 },
                new[] { 3, 9, 4 }, new[] { 3, 4, 2 }, new[] { 3, 2, 6 }, new[] { 3, 6, 8 }, new[] { 3, 8, 9 },
                new[] { 4, 9, 5 }, new[] { 2, 4, 11 }, new[] { 6, 2, 10 }, new[] { 8, 6, 7 }, new[] { 9, 8, 1 },
            };
            faces.AddRange(f0);
            for (int l = 0; l < level; l++)
            {
                var mid = new Dictionary<long, int>();
                int Mid(int a, int b)
                {
                    long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                    if (mid.TryGetValue(key, out int m)) return m;
                    var pa = verts[a]; var pb = verts[b];
                    verts.Add(Norm(new[] { pa[0] + pb[0], pa[1] + pb[1], pa[2] + pb[2] }));
                    return mid[key] = verts.Count - 1;
                }
                var next = new List<int[]>();
                foreach (var f in faces)
                {
                    int a = Mid(f[0], f[1]), b = Mid(f[1], f[2]), c = Mid(f[2], f[0]);
                    next.Add(new[] { f[0], a, c }); next.Add(new[] { f[1], b, a }); next.Add(new[] { f[2], c, b }); next.Add(new[] { a, b, c });
                }
                faces = next;
            }
            int n = verts.Count;
            var d = new Data { Pos = new float[n * 3], Nrm = new float[n * 3], Uv = new float[n * 2], Tri = new int[faces.Count * 3] };
            float sx = 0.85f + 0.3f * ScatterSampler.Hash01(ScatterSampler.Hash(seed ^ 11u));
            float sz = 0.85f + 0.3f * ScatterSampler.Hash01(ScatterSampler.Hash(seed ^ 23u));
            for (int i = 0; i < n; i++)
            {
                var p = verts[i];
                // 3D-ish value noise by projecting on two planes (cheap, seeded): big lumps + small facets
                float big = ScatterSampler.Noise(p[0] * 2 + p[1], p[2] * 2 - p[1], 1.3f, seed);
                float small = ScatterSampler.Noise(p[0] * 5 + 3, p[2] * 5 + p[1] * 3, 0.9f, seed ^ 0x9E37u);
                float r = 0.5f * (0.78f + 0.38f * big + 0.12f * small);
                float y = p[1] * r * 0.62f;
                d.Pos[i * 3] = p[0] * r * sx; d.Pos[i * 3 + 1] = y + 0.5f * 0.62f * 0.5f; d.Pos[i * 3 + 2] = p[2] * r * sz;
                d.Uv[i * 2] = d.Pos[i * 3]; d.Uv[i * 2 + 1] = d.Pos[i * 3 + 2];
            }
            for (int f = 0; f < faces.Count; f++)
            {
                // Unity's front face: cross(b - a, c - a) points at the viewer = outward for the icosahedron's order
                d.Tri[f * 3] = faces[f][0]; d.Tri[f * 3 + 1] = faces[f][1]; d.Tri[f * 3 + 2] = faces[f][2];
            }
            SmoothNormals(d);
            return d;
        }

        static float[] Norm(float[] p)
        {
            float l = (float)Math.Sqrt(p[0] * p[0] + p[1] * p[1] + p[2] * p[2]);
            return new[] { p[0] / l, p[1] / l, p[2] / l };
        }

        /// <summary>Area-weighted vertex normals oriented away from the centroid.</summary>
        public static void SmoothNormals(Data d)
        {
            int n = d.VertexCount;
            Array.Clear(d.Nrm, 0, d.Nrm.Length);
            for (int f = 0; f < d.Tri.Length; f += 3)
            {
                int a = d.Tri[f], b = d.Tri[f + 1], c = d.Tri[f + 2];
                float ux = d.Pos[b * 3] - d.Pos[a * 3], uy = d.Pos[b * 3 + 1] - d.Pos[a * 3 + 1], uz = d.Pos[b * 3 + 2] - d.Pos[a * 3 + 2];
                float vx = d.Pos[c * 3] - d.Pos[a * 3], vy = d.Pos[c * 3 + 1] - d.Pos[a * 3 + 1], vz = d.Pos[c * 3 + 2] - d.Pos[a * 3 + 2];
                float nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
                foreach (int i in new[] { a, b, c }) { d.Nrm[i * 3] += nx; d.Nrm[i * 3 + 1] += ny; d.Nrm[i * 3 + 2] += nz; }
            }
            float cx = 0, cy = 0, cz = 0;
            for (int i = 0; i < n; i++) { cx += d.Pos[i * 3]; cy += d.Pos[i * 3 + 1]; cz += d.Pos[i * 3 + 2]; }
            cx /= n; cy /= n; cz /= n;
            for (int i = 0; i < n; i++)
            {
                float nx = d.Nrm[i * 3], ny = d.Nrm[i * 3 + 1], nz = d.Nrm[i * 3 + 2];
                float l = (float)Math.Sqrt(nx * nx + ny * ny + nz * nz);
                if (l < 1e-12f) { nx = d.Pos[i * 3] - cx; ny = d.Pos[i * 3 + 1] - cy; nz = d.Pos[i * 3 + 2] - cz; l = (float)Math.Sqrt(nx * nx + ny * ny + nz * nz) + 1e-12f; }
                d.Nrm[i * 3] = nx / l; d.Nrm[i * 3 + 1] = ny / l; d.Nrm[i * 3 + 2] = nz / l;
            }
        }

        /// <summary>Signed volume x 6 of a closed mesh: positive when every cross(b - a, c - a) points outward (Unity front faces).</summary>
        public static float SignedVolume6(Data d)
        {
            double v = 0;
            for (int f = 0; f < d.Tri.Length; f += 3)
            {
                int a = d.Tri[f] * 3, b = d.Tri[f + 1] * 3, c = d.Tri[f + 2] * 3;
                v += d.Pos[a] * (d.Pos[b + 1] * d.Pos[c + 2] - d.Pos[b + 2] * d.Pos[c + 1])
                   - d.Pos[a + 1] * (d.Pos[b] * d.Pos[c + 2] - d.Pos[b + 2] * d.Pos[c])
                   + d.Pos[a + 2] * (d.Pos[b] * d.Pos[c + 1] - d.Pos[b + 1] * d.Pos[c]);
            }
            return (float)v;
        }
    }
}
