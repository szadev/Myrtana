using System;
using System.Collections.Generic;
using G3.Formats.Trees;
using UnityEngine;
using UnityEngine.Rendering;

namespace G3.Unity.Editor.Trees
{
    /// <summary>
    /// Unity meshes from a <see cref="BakedTree"/> (cm, G3 axes → m, same axes): one mesh per combined LOD level with up to
    /// three sub-meshes (bark, fronds, leaf cards) and the billboard quad. Vertex layout as G3SpeedTree.hlsl expects:
    /// uv0 = texture coordinates (v flipped: SpeedTree's v points up, G3 textures are stored top row first),
    /// uv1 = (corner x, corner y, wind weight, wind matrix + 4 × rock group), colour = leaf dimming.
    /// </summary>
    public static class G3SpeedTreeMeshBuilder
    {
        public enum Part { Bark, Frond, Leaf }

        const float S = 0.01f;

        sealed class Buffers
        {
            public readonly List<Vector3> Pos = new List<Vector3>(), Nrm = new List<Vector3>();
            public readonly List<Vector4> Tan = new List<Vector4>(), Data = new List<Vector4>();
            public readonly List<Vector2> Uv = new List<Vector2>();
            public readonly List<Color32> Col = new List<Color32>();
            public readonly List<int[]> SubMeshes = new List<int[]>();
        }

        /// <summary>Mesh of one combined level; <paramref name="parts"/> lists the part of each sub-mesh.</summary>
        public static Mesh BuildLevel(string name, BakedMeshLod branch, BakedMeshLod frond, BakedLeafLod leaf, List<Part> parts)
        {
            var b = new Buffers();
            parts.Clear();
            if (branch != null && branch.Triangles.Length > 0) { AddIndexed(b, branch); parts.Add(Part.Bark); }
            if (frond != null && frond.Triangles.Length > 0) { AddIndexed(b, frond); parts.Add(Part.Frond); }
            float leafReach = 0;
            if (leaf != null && leaf.Count > 0) { leafReach = AddLeaves(b, leaf); parts.Add(Part.Leaf); }
            if (parts.Count == 0) return null;

            var mesh = Create(name, b);
            // Leaf cards are expanded in the vertex shader: grow the bounds by the largest card radius.
            var bounds = mesh.bounds;
            bounds.Expand(2 * leafReach);
            mesh.bounds = bounds;
            return mesh;
        }

        /// <summary>The far billboard: one upright quad, corners as baked (top-right, top-left, bottom-left, bottom-right).</summary>
        public static Mesh BuildBillboard(string name, BakedBillboard bb)
        {
            if (bb == null || bb.ImageCount == 0) return null;
            var b = new Buffers();
            float hw = bb.HalfWidth * S, top = bb.Top * S, bottom = bb.Bottom * S;
            var x = new[] { hw, -hw, -hw, hw };
            var y = new[] { top, top, bottom, bottom };
            for (int k = 0; k < 4; k++)
            {
                b.Pos.Add(new Vector3(0, y[k], 0));
                b.Nrm.Add(Vector3.back);
                b.Tan.Add(new Vector4(1, 0, 0, 1));
                b.Col.Add(new Color32(255, 255, 255, 255));
                b.Uv.Add(new Vector2(bb.ImageUvs[2 * k], 1 - bb.ImageUvs[2 * k + 1]));
                b.Data.Add(new Vector4(x[k], 0, 0, 0));
            }
            // Clockwise as seen from the viewer: x runs along the view's right axis in the shader.
            b.SubMeshes.Add(new[] { 1, 0, 3, 1, 3, 2 });
            var mesh = Create(name, b);
            mesh.bounds = new Bounds(new Vector3(0, (top + bottom) * 0.5f, 0), new Vector3(2 * hw, top - bottom, 2 * hw));
            return mesh;
        }

        static void AddIndexed(Buffers b, BakedMeshLod m)
        {
            int baseVertex = b.Pos.Count, n = m.VertexCount;
            for (int i = 0; i < n; i++)
            {
                b.Pos.Add(V3(m.Positions, i) * S);
                var nrm = V3(m.Normals, i);
                var tan = V3(m.Tangents, i);
                var bin = V3(m.Binormals, i);
                b.Nrm.Add(nrm);
                // Unity: binormal = cross(normal, tangent) × w
                float w = Vector3.Dot(Vector3.Cross(nrm, tan), bin) < 0 ? -1 : 1;
                b.Tan.Add(new Vector4(tan.x, tan.y, tan.z, w));
                b.Col.Add(new Color32(255, 255, 255, 255));
                b.Uv.Add(new Vector2(m.Uv[2 * i], 1 - m.Uv[2 * i + 1]));
                b.Data.Add(new Vector4(0, 0, m.WindWeight[i], m.WindMatrix[i] & 3));
            }
            var tris = new int[m.Triangles.Length];
            for (int i = 0; i < tris.Length; i++) tris[i] = m.Triangles[i] + baseVertex;
            b.SubMeshes.Add(tris);
        }

        /// <summary>Adds the leaf cards (4 vertices each); returns the largest card radius (m).</summary>
        static float AddLeaves(Buffers b, BakedLeafLod l)
        {
            int baseVertex = b.Pos.Count;
            float reach = 0;
            var tris = new int[l.Count * 6];
            for (int i = 0; i < l.Count; i++)
            {
                var center = V3(l.Centers, i) * S;
                var nrm = V3(l.Normals, i);
                uint argb = l.Colors[i];
                var col = new Color32((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb, 255);
                float windIndex = (l.WindMatrix[i] & 3) + 4 * (l.Cluster[i] & 3);
                for (int k = 0; k < 4; k++)
                {
                    float cx = l.Corners[8 * i + 2 * k] * S, cy = l.Corners[8 * i + 2 * k + 1] * S;
                    reach = Mathf.Max(reach, Mathf.Sqrt(cx * cx + cy * cy));
                    b.Pos.Add(center);
                    b.Nrm.Add(nrm);
                    b.Tan.Add(new Vector4(1, 0, 0, 1));
                    b.Col.Add(col);
                    b.Uv.Add(new Vector2(l.Uv[8 * i + 2 * k], 1 - l.Uv[8 * i + 2 * k + 1]));
                    b.Data.Add(new Vector4(cx, cy, l.WindWeight[i], windIndex));
                }
                // corners TL, TR, BR, BL (x right, y up): clockwise on screen
                int v = baseVertex + 4 * i;
                tris[6 * i] = v; tris[6 * i + 1] = v + 1; tris[6 * i + 2] = v + 2;
                tris[6 * i + 3] = v; tris[6 * i + 4] = v + 2; tris[6 * i + 5] = v + 3;
            }
            b.SubMeshes.Add(tris);
            return reach;
        }

        static Mesh Create(string name, Buffers b)
        {
            var mesh = new Mesh { name = name };
            if (b.Pos.Count > 65535) mesh.indexFormat = IndexFormat.UInt32;
            mesh.SetVertices(b.Pos);
            mesh.SetNormals(b.Nrm);
            mesh.SetTangents(b.Tan);
            mesh.SetColors(b.Col);
            mesh.SetUVs(0, b.Uv);
            mesh.SetUVs(1, b.Data);
            mesh.subMeshCount = b.SubMeshes.Count;
            for (int s = 0; s < b.SubMeshes.Count; s++) mesh.SetTriangles(b.SubMeshes[s], s, false);
            mesh.RecalculateBounds();
            mesh.UploadMeshData(false);
            return mesh;
        }

        static Vector3 V3(float[] a, int i) => new Vector3(a[3 * i], a[3 * i + 1], a[3 * i + 2]);
    }
}
