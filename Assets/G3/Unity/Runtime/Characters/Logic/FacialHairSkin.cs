using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text.RegularExpressions;

namespace G3.Unity.Characters
{
    /// <summary>Up to four bone influences of one vertex (Unity BoneWeight layout, weights sum to 1).</summary>
    public struct BoneWeights4
    {
        public int B0, B1, B2, B3;
        public float W0, W1, W2, W3;

        public BoneWeights4(int b0, float w0, int b1 = 0, float w1 = 0, int b2 = 0, float w2 = 0, int b3 = 0, float w3 = 0)
        {
            B0 = b0; W0 = w0; B1 = b1; W1 = w1; B2 = b2; W2 = w2; B3 = b3; W3 = w3;
        }

        public float WeightOf(int bone) =>
            (B0 == bone ? W0 : 0) + (B1 == bone ? W1 : 0) + (B2 == bone ? W2 : 0) + (B3 == bone ? W3 : 0);
    }

    /// <summary>The point of a skin surface closest to a hair vertex: triangle corners and barycentric weights.</summary>
    public struct SurfacePoint
    {
        public int A, B, C;
        public float WA, WB, WC;
        public float Distance;
    }

    /// <summary>
    /// Facial hair that follows the talking face (fix-246, Docs/dialog-presentation.md §4.1). G3 hangs a beard or
    /// moustache (<c>G3_Animation_Head_Beard_*.xcmsh</c>, a static mesh without skin) on the head actor's
    /// <c>Slot_Beard</c>, a child of <c>*_Head_Head_1</c> in the static and the facial actor alike, so the original beard
    /// never moves with the jaw and the chin pokes through it while an NPC speaks. The beard is instead skinned to the
    /// talking head's bones like the skin under it: every hair vertex takes the bone weights (and, for a blend shape
    /// head, the speech shape deltas) of the closest point of the head's skin surface, blended by its barycentric
    /// coordinates, then smoothed over the hair mesh so neighbouring strands do not tear apart (a moustache tip over the
    /// mouth corner must not split between upper and lower lip). Pure maths: the Unity side is G3FacialHair.
    /// </summary>
    public static class FacialHairSkin
    {
        static readonly Regex HairName = new Regex(@"beard|mustache|moustache|_bart", RegexOptions.IgnoreCase);
        static readonly Regex NotSkin = new Regex(@"eye|mouth|teeth|tooth|tongue|lash|brow|tear|cornea|iris|pupil", RegexOptions.IgnoreCase);

        /// <summary>The slot G3 hangs beards and moustaches on.</summary>
        public const string BeardSlot = "Slot_Beard";

        /// <summary>
        /// A static part that is facial hair: it hangs under <see cref="BeardSlot"/> (whatever its mesh), or its object /
        /// mesh is named as a beard or moustache. Hair (<c>Slot_Hair</c>) and helmets stay rigid.
        /// </summary>
        public static bool IsFacialHair(string objectName, string meshName, IEnumerable<string> ancestorNames)
        {
            if (ancestorNames != null)
                foreach (var a in ancestorNames)
                {
                    if (a == null) continue;
                    if (a.StartsWith(BeardSlot, StringComparison.OrdinalIgnoreCase)) return true;
                    if (a.StartsWith("Slot_", StringComparison.OrdinalIgnoreCase)) return false; // Slot_Hair / Slot_Helmet / hands
                }
            return (objectName != null && HairName.IsMatch(objectName)) || (meshName != null && HairName.IsMatch(meshName));
        }

        /// <summary>
        /// A head submesh whose triangles are skin the hair can follow; eyes, the inner mouth, teeth, tongue, lashes are
        /// not (a moustache vertex must not take the inner mouth's weights).
        /// </summary>
        public static bool IsSkinMaterial(string materialName) => string.IsNullOrEmpty(materialName) || !NotSkin.IsMatch(materialName);

        /// <summary>Closest point of triangle abc to p (Ericson, Real-Time Collision Detection 5.1.5), with barycentrics.</summary>
        public static Vector3 ClosestPointOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c, out float u, out float v, out float w)
        {
            Vector3 ab = b - a, ac = c - a, ap = p - a;
            float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
            if (d1 <= 0 && d2 <= 0) { u = 1; v = 0; w = 0; return a; }
            Vector3 bp = p - b;
            float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
            if (d3 >= 0 && d4 <= d3) { u = 0; v = 1; w = 0; return b; }
            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0 && d1 >= 0 && d3 <= 0)
            {
                float t = d1 / (d1 - d3);
                u = 1 - t; v = t; w = 0;
                return a + t * ab;
            }
            Vector3 cp = p - c;
            float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
            if (d6 >= 0 && d5 <= d6) { u = 0; v = 0; w = 1; return c; }
            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0 && d2 >= 0 && d6 <= 0)
            {
                float t = d2 / (d2 - d6);
                u = 1 - t; v = 0; w = t;
                return a + t * ac;
            }
            float va = d3 * d6 - d5 * d4;
            if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0)
            {
                float t = (d4 - d3) / ((d4 - d3) + (d5 - d6));
                u = 0; v = 1 - t; w = t;
                return b + t * (c - b);
            }
            float denom = va + vb + vc;
            if (denom <= 1e-30f) { u = 1; v = 0; w = 0; return a; } // degenerate triangle
            float inv = 1f / denom;
            v = vb * inv;
            w = vc * inv;
            u = 1 - v - w;
            return a + ab * v + ac * w;
        }

        /// <summary>
        /// For every hair vertex the closest point of the skin triangles (<paramref name="triangles"/> index triples into
        /// <paramref name="skin"/>; both in one space). Uniform grid over the triangles' bounds, searched ring by ring.
        /// Without triangles every point is (-1,-1,-1) at infinite distance.
        /// </summary>
        public static SurfacePoint[] Map(Vector3[] skin, int[] triangles, Vector3[] hair)
        {
            var result = new SurfacePoint[hair.Length];
            int triCount = triangles != null ? triangles.Length / 3 : 0;
            if (triCount == 0)
            {
                for (int i = 0; i < result.Length; i++) result[i] = new SurfacePoint { A = -1, B = -1, C = -1, Distance = float.PositiveInfinity };
                return result;
            }
            var grid = new TriangleGrid(skin, triangles);
            for (int i = 0; i < hair.Length; i++) result[i] = grid.Closest(hair[i]);
            return result;
        }

        /// <summary>
        /// The bone weights of the skin at every mapped point (corner weights blended barycentrically), as dense rows of
        /// <paramref name="boneCount"/> per hair vertex. Unmapped points get <paramref name="fallbackBone"/> fully, and
        /// so does the share of every bone <paramref name="allowed"/> leaves out (null = all bones).
        /// </summary>
        public static float[] BlendWeights(SurfacePoint[] map, BoneWeights4[] skinWeights, int boneCount, int fallbackBone, bool[] allowed = null)
        {
            var dense = new float[map.Length * boneCount];
            for (int i = 0; i < map.Length; i++)
            {
                var p = map[i];
                int row = i * boneCount;
                if (p.A < 0) { if (fallbackBone >= 0 && fallbackBone < boneCount) dense[row + fallbackBone] = 1; continue; }
                Accumulate(dense, row, boneCount, skinWeights[p.A], p.WA, allowed, fallbackBone);
                Accumulate(dense, row, boneCount, skinWeights[p.B], p.WB, allowed, fallbackBone);
                Accumulate(dense, row, boneCount, skinWeights[p.C], p.WC, allowed, fallbackBone);
            }
            return dense;
        }

        static void Accumulate(float[] dense, int row, int boneCount, BoneWeights4 w, float k, bool[] allowed, int fallback)
        {
            if (k <= 0) return;
            Add(dense, row, boneCount, w.B0, w.W0 * k, allowed, fallback);
            Add(dense, row, boneCount, w.B1, w.W1 * k, allowed, fallback);
            Add(dense, row, boneCount, w.B2, w.W2 * k, allowed, fallback);
            Add(dense, row, boneCount, w.B3, w.W3 * k, allowed, fallback);
        }

        static void Add(float[] dense, int row, int boneCount, int bone, float w, bool[] allowed, int fallback)
        {
            if (w <= 0 || bone < 0 || bone >= boneCount) return;
            if (allowed != null && (bone >= allowed.Length || !allowed[bone])) bone = fallback;
            if (bone >= 0 && bone < boneCount) dense[row + bone] += w;
        }

        /// <summary>
        /// The bones at or below <paramref name="root"/> (<paramref name="parents"/>: parent index per bone, -1 = none;
        /// a parent outside the list must be given as -1). The hair hangs rigidly on the head bone in G3: it may follow the
        /// face bones under it, never the neck or spine (a braid on Slot_Beard would bend with the body).
        /// </summary>
        public static bool[] Subtree(int[] parents, int root)
        {
            var result = new bool[parents.Length];
            for (int b = 0; b < parents.Length; b++)
            {
                int p = b;
                for (int guard = 0; p >= 0 && guard <= parents.Length; guard++)
                {
                    if (p == root) { result[b] = true; break; }
                    p = p < parents.Length ? parents[p] : -1;
                }
            }
            return result;
        }

        /// <summary>The skin's vector field (a blend shape's vertex deltas) at every mapped point; unmapped points get zero.</summary>
        public static Vector3[] BlendDeltas(SurfacePoint[] map, Vector3[] skinDeltas)
        {
            var result = new Vector3[map.Length];
            for (int i = 0; i < map.Length; i++)
            {
                var p = map[i];
                if (p.A < 0) continue;
                result[i] = skinDeltas[p.A] * p.WA + skinDeltas[p.B] * p.WB + skinDeltas[p.C] * p.WC;
            }
            return result;
        }

        /// <summary>
        /// Vertex neighbours over the triangles' edges no longer than <paramref name="maxEdge"/>; vertices closer than
        /// <paramref name="weld"/> (split UV / normal seams of one point) are welded into one, so they also share their
        /// neighbours. The edge limit keeps the smoothing local: G3's beards are shells of ~50 vertices whose long edges
        /// would average the chin with the cheeks (measured with <c>G3Cli anim beard</c>: 0.1 cm off the skin unsmoothed,
        /// 0.45 cm with two passes over every edge), while dense hair cards still lose their tears.
        /// </summary>
        public static int[][] Neighbours(Vector3[] vertices, int[] triangles, float weld, float maxEdge = float.PositiveInfinity)
        {
            int n = vertices.Length;
            var group = Weld(vertices, weld);
            var sets = new HashSet<int>[n];
            if (triangles != null)
                for (int t = 0; t + 2 < triangles.Length; t += 3)
                    for (int e = 0; e < 3; e++)
                    {
                        int a = group[triangles[t + e]], b = group[triangles[t + (e + 1) % 3]];
                        if (a == b || Vector3.Distance(vertices[a], vertices[b]) > maxEdge) continue;
                        (sets[a] ??= new HashSet<int>()).Add(b);
                        (sets[b] ??= new HashSet<int>()).Add(a);
                    }
            // a welded duplicate shares its representative's neighbours (representatives only): both start from the
            // same mapped values and stay equal through the smoothing
            var result = new int[n][];
            for (int i = 0; i < n; i++)
            {
                var s = sets[group[i]];
                result[i] = s != null ? new List<int>(s).ToArray() : Array.Empty<int>();
            }
            return result;
        }

        /// <summary>Index of the first vertex at (almost) the same position, per vertex.</summary>
        static int[] Weld(Vector3[] vertices, float weld)
        {
            var group = new int[vertices.Length];
            float cell = weld > 0 ? weld : 1e-6f;
            var first = new Dictionary<(long, long, long), int>();
            for (int i = 0; i < vertices.Length; i++)
            {
                var v = vertices[i];
                var key = ((long)Math.Round(v.X / cell), (long)Math.Round(v.Y / cell), (long)Math.Round(v.Z / cell));
                if (first.TryGetValue(key, out int g)) group[i] = g;
                else { first[key] = i; group[i] = i; }
            }
            return group;
        }

        /// <summary>
        /// Laplacian smoothing of per-vertex rows (<paramref name="channels"/> floats each): each pass moves a row
        /// <paramref name="amount"/> of the way to its neighbours' mean. Rows without neighbours stay.
        /// </summary>
        public static void Smooth(float[] rows, int channels, int[][] neighbours, int iterations, float amount)
        {
            if (iterations <= 0 || amount <= 0 || channels <= 0) return;
            int n = neighbours.Length;
            var next = new float[rows.Length];
            for (int it = 0; it < iterations; it++)
            {
                for (int i = 0; i < n; i++)
                {
                    int row = i * channels;
                    var nb = neighbours[i];
                    if (nb == null || nb.Length == 0) { Array.Copy(rows, row, next, row, channels); continue; }
                    float k = amount / nb.Length;
                    for (int c = 0; c < channels; c++)
                    {
                        float mean = 0;
                        foreach (int j in nb) mean += rows[j * channels + c];
                        next[row + c] = rows[row + c] * (1 - amount) + mean * k;
                    }
                }
                Array.Copy(next, rows, rows.Length);
            }
        }

        /// <summary>The four strongest influences of one dense row, renormalised; an empty row gets <paramref name="fallbackBone"/>.</summary>
        public static BoneWeights4 Top4(float[] dense, int vertex, int boneCount, int fallbackBone)
        {
            int row = vertex * boneCount;
            Span<int> bones = stackalloc int[4];
            Span<float> weights = stackalloc float[4];
            for (int k = 0; k < 4; k++) { bones[k] = 0; weights[k] = 0; }
            for (int b = 0; b < boneCount; b++)
            {
                float w = dense[row + b];
                if (w <= weights[3]) continue;
                int k = 3;
                while (k > 0 && w > weights[k - 1]) { weights[k] = weights[k - 1]; bones[k] = bones[k - 1]; k--; }
                weights[k] = w;
                bones[k] = b;
            }
            float sum = weights[0] + weights[1] + weights[2] + weights[3];
            if (sum <= 1e-6f) return new BoneWeights4(Math.Max(0, fallbackBone), 1);
            float inv = 1f / sum;
            return new BoneWeights4(bones[0], weights[0] * inv, bones[1], weights[1] * inv, bones[2], weights[2] * inv, bones[3], weights[3] * inv);
        }

        /// <summary>
        /// The whole transfer: hair vertices (skin space) take the skin's weights at their closest skin point, smoothed
        /// <paramref name="smoothIterations"/> times over the hair mesh (<paramref name="hairTriangles"/>), four strongest kept.
        /// </summary>
        public static BoneWeights4[] TransferWeights(Vector3[] skin, int[] skinTriangles, BoneWeights4[] skinWeights, int boneCount,
                                                     Vector3[] hair, int[] hairTriangles, int fallbackBone,
                                                     int smoothIterations = 2, float weld = 1e-4f, bool[] allowed = null)
        {
            var map = Map(skin, skinTriangles, hair);
            return TransferWeights(map, skinWeights, boneCount, Neighbours(hair, hairTriangles, weld), fallbackBone, smoothIterations, allowed);
        }

        /// <summary><see cref="TransferWeights(Vector3[],int[],BoneWeights4[],int,Vector3[],int[],int,int,float,bool[])"/> on a ready map.</summary>
        public static BoneWeights4[] TransferWeights(SurfacePoint[] map, BoneWeights4[] skinWeights, int boneCount, int[][] hairNeighbours,
                                                     int fallbackBone, int smoothIterations = 2, bool[] allowed = null)
        {
            var dense = BlendWeights(map, skinWeights, boneCount, fallbackBone, allowed);
            Smooth(dense, boneCount, hairNeighbours, smoothIterations, 0.5f);
            var result = new BoneWeights4[map.Length];
            for (int i = 0; i < result.Length; i++) result[i] = Top4(dense, i, boneCount, fallbackBone);
            return result;
        }

        /// <summary>A blend shape of the skin carried over to the hair: mapped deltas, smoothed like the weights.</summary>
        public static Vector3[] TransferDeltas(SurfacePoint[] map, Vector3[] skinDeltas, int[][] hairNeighbours, int smoothIterations = 2)
        {
            var d = BlendDeltas(map, skinDeltas);
            if (smoothIterations <= 0) return d;
            var rows = new float[d.Length * 3];
            for (int i = 0; i < d.Length; i++) { rows[i * 3] = d[i].X; rows[i * 3 + 1] = d[i].Y; rows[i * 3 + 2] = d[i].Z; }
            Smooth(rows, 3, hairNeighbours, smoothIterations, 0.5f);
            for (int i = 0; i < d.Length; i++) d[i] = new Vector3(rows[i * 3], rows[i * 3 + 1], rows[i * 3 + 2]);
            return d;
        }

        /// <summary>Uniform grid of triangles (by their bounds) for closest-point queries.</summary>
        sealed class TriangleGrid
        {
            readonly Vector3[] _v;
            readonly int[] _t;
            readonly Vector3 _min;
            readonly float _cell;
            readonly int _nx, _ny, _nz;
            readonly List<int>[] _cells;
            readonly int[] _stamp;
            int _query;

            public TriangleGrid(Vector3[] vertices, int[] triangles)
            {
                _v = vertices;
                _t = triangles;
                int triCount = triangles.Length / 3;
                Vector3 min = new Vector3(float.MaxValue), max = new Vector3(float.MinValue);
                for (int i = 0; i < triangles.Length; i++)
                {
                    var p = vertices[triangles[i]];
                    min = Vector3.Min(min, p);
                    max = Vector3.Max(max, p);
                }
                var size = max - min;
                float extent = Math.Max(size.X, Math.Max(size.Y, size.Z));
                // ~ two triangles across a cell for a dense head; never zero
                _cell = Math.Max(extent / Math.Max(1f, (float)Math.Ceiling(Math.Pow(triCount, 1.0 / 3.0) * 1.5)), 1e-5f);
                _min = min;
                _nx = Math.Max(1, (int)(size.X / _cell) + 1);
                _ny = Math.Max(1, (int)(size.Y / _cell) + 1);
                _nz = Math.Max(1, (int)(size.Z / _cell) + 1);
                _cells = new List<int>[_nx * _ny * _nz];
                _stamp = new int[triCount];
                for (int t = 0; t < triCount; t++)
                {
                    Vector3 a = vertices[triangles[t * 3]], b = vertices[triangles[t * 3 + 1]], c = vertices[triangles[t * 3 + 2]];
                    var lo = Vector3.Min(a, Vector3.Min(b, c));
                    var hi = Vector3.Max(a, Vector3.Max(b, c));
                    Cell(lo, out int x0, out int y0, out int z0);
                    Cell(hi, out int x1, out int y1, out int z1);
                    for (int z = z0; z <= z1; z++)
                        for (int y = y0; y <= y1; y++)
                            for (int x = x0; x <= x1; x++)
                                (_cells[Index(x, y, z)] ??= new List<int>()).Add(t);
                }
            }

            int Index(int x, int y, int z) => (z * _ny + y) * _nx + x;

            void Cell(Vector3 p, out int x, out int y, out int z)
            {
                var q = (p - _min) / _cell;
                x = Clamp((int)Math.Floor(q.X), _nx);
                y = Clamp((int)Math.Floor(q.Y), _ny);
                z = Clamp((int)Math.Floor(q.Z), _nz);
            }

            static int Clamp(int i, int n) => i < 0 ? 0 : i >= n ? n - 1 : i;

            public SurfacePoint Closest(Vector3 p)
            {
                _query++;
                Cell(p, out int cx, out int cy, out int cz);
                var best = new SurfacePoint { A = -1, B = -1, C = -1, Distance = float.PositiveInfinity };
                float bestSq = float.PositiveInfinity;
                int maxRing = Math.Max(_nx, Math.Max(_ny, _nz));
                for (int r = 0; r <= maxRing; r++)
                {
                    for (int z = cz - r; z <= cz + r; z++)
                    {
                        if (z < 0 || z >= _nz) continue;
                        for (int y = cy - r; y <= cy + r; y++)
                        {
                            if (y < 0 || y >= _ny) continue;
                            for (int x = cx - r; x <= cx + r; x++)
                            {
                                if (x < 0 || x >= _nx) continue;
                                // only the shell of the ring (inner cells were searched before)
                                if (Math.Abs(x - cx) != r && Math.Abs(y - cy) != r && Math.Abs(z - cz) != r) continue;
                                var list = _cells[Index(x, y, z)];
                                if (list == null) continue;
                                foreach (int t in list)
                                {
                                    if (_stamp[t] == _query) continue;
                                    _stamp[t] = _query;
                                    int ia = _t[t * 3], ib = _t[t * 3 + 1], ic = _t[t * 3 + 2];
                                    var q = ClosestPointOnTriangle(p, _v[ia], _v[ib], _v[ic], out float u, out float v, out float w);
                                    float d = Vector3.DistanceSquared(p, q);
                                    if (d < bestSq)
                                    {
                                        bestSq = d;
                                        best = new SurfacePoint { A = ia, B = ib, C = ic, WA = u, WB = v, WC = w };
                                    }
                                }
                            }
                        }
                    }
                    // the projection of p into the grid lies in the centre cell: every cell of ring r+1 is at least r cells
                    // from it, and from p (projection onto the grid box never brings points closer)
                    float bound = r * _cell;
                    if (bestSq <= bound * bound) break;
                }
                best.Distance = (float)Math.Sqrt(bestSq);
                return best;
            }
        }
    }
}
