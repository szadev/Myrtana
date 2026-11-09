using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using G3.Formats.Pak;
using G3.Formats.World;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace G3.Unity.Editor
{
    /// <summary>
    /// Builds the far-distance backdrop from G3's disabled low-poly world (G3_World_Lowpoly_01_Levelmesh_01):
    /// the low-poly landscape / water / oasis meshes are cut along the streaming grid (a triangle goes to every cell
    /// square its XZ box touches, so there are no cracks), large low-poly objects (houses, walls, ruins) are placed by
    /// AABB centre; everything goes under a <see cref="G3LowpolyBackdrop"/> with one piece per cell.
    /// </summary>
    public static class G3LowpolyImporter
    {
        public const string Folder = "Assets/G3Imported/Lowpoly";
        const string SectorFolder = "G3_World_01/World/_Level/G3_World_Lowpoly_01_Levelmesh_01/";
        static readonly string[] Nodes =
        {
            SectorFolder + "G3_World_Lowpoly_Landscape_01_SpG.node",
            SectorFolder + "G3_World_Lowpoly_01_Levelmesh_01_Spat.node",
        };
        /// <summary>Low-poly objects smaller than this (metres, largest horizontal extent) are skipped (6.5k stones).</summary>
        public static float MinObjectSize = 12f;

        sealed class Builder
        {
            public readonly List<Vector3> Pos = new List<Vector3>();
            public readonly List<Vector3> Nrm = new List<Vector3>();
            public readonly List<Vector2> Uv0 = new List<Vector2>();
            public readonly List<Vector2> Uv1 = new List<Vector2>();
            public readonly List<Color32> Col = new List<Color32>();
            public readonly Dictionary<long, int> Remap = new Dictionary<long, int>();
            public readonly Dictionary<Material, List<int>> Tris = new Dictionary<Material, List<int>>();
            public bool HasUv1;
        }

        public static string Build(G3LowpolyBackdrop backdrop, G3WorldManifest manifest, G3AssetCache cache)
        {
            Directory.CreateDirectory(Folder);
            var entities = new List<WorldEntity>();
            using (var projects = new G3FileSystem(G3WorldImporter.DataDir, "Projects_compiled"))
                foreach (var n in Nodes)
                {
                    var entry = projects.Get("Projects_compiled/" + n);
                    var list = entry != null ? EntityArchive.Read(entry.Read(), decodeVegetation: false) : null;
                    if (list != null) entities.AddRange(list.Where(e => e.Enabled && e.Visual == VisualKind.Mesh));
                }

            var builders = new Dictionary<int, Builder>();
            var objects = new Dictionary<int, List<WorldEntity>>();
            int landscapeMeshes = 0, objectCount = 0, skipped = 0;
            long eid = 0;
            foreach (var e in entities)
            {
                eid++;
                bool landscape = e.Resource.IndexOf("Landscape", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!landscape)
                {
                    var b = G3WorldImporter.BoundsM(e);
                    if (Mathf.Max(b.size.x, b.size.z) < MinObjectSize) { skipped++; continue; }
                    var c = manifest.CellOf(b.center);
                    int ci = manifest.Index(Mathf.Clamp(c.x, 0, manifest.countX - 1), Mathf.Clamp(c.y, 0, manifest.countZ - 1));
                    if (!objects.TryGetValue(ci, out var ol)) objects[ci] = ol = new List<WorldEntity>();
                    ol.Add(e);
                    objectCount++;
                    continue;
                }

                var res = cache.ReadMeshResource(e.Resource);
                if (res == null) continue;
                landscapeMeshes++;
                // G3 row vectors: p' = p × M (row 3 = translation). Unity column-vector matrix = transpose.
                var m = e.World;
                var M = new Matrix4x4();
                for (int r = 0; r < 4; r++)
                for (int c = 0; c < 4; c++)
                    M[c, r] = m[r * 4 + c];
                for (int el = 0; el < res.Elements.Count; el++)
                {
                    var me = res.Elements[el];
                    var mat = cache.GetMaterial(me.Material);
                    if (cache.IsInvisible(mat)) continue;
                    int vc = me.VertexCount;
                    var wp = new Vector3[vc];
                    for (int v = 0; v < vc; v++)
                        wp[v] = M.MultiplyPoint3x4(new Vector3(me.Positions[v * 3], me.Positions[v * 3 + 1], me.Positions[v * 3 + 2])) * G3AssetCache.Scale;
                    for (int t = 0; t + 2 < me.Indices.Length; t += 3)
                    {
                        // D3D winding → Unity: (i0, i2, i1)
                        int a = (int)me.Indices[t], b = (int)me.Indices[t + 2], cIdx = (int)me.Indices[t + 1];
                        float minX = Mathf.Min(wp[a].x, Mathf.Min(wp[b].x, wp[cIdx].x)), maxX = Mathf.Max(wp[a].x, Mathf.Max(wp[b].x, wp[cIdx].x));
                        float minZ = Mathf.Min(wp[a].z, Mathf.Min(wp[b].z, wp[cIdx].z)), maxZ = Mathf.Max(wp[a].z, Mathf.Max(wp[b].z, wp[cIdx].z));
                        int x0 = Mathf.Clamp(Mathf.FloorToInt((minX - manifest.origin.x) / manifest.cellSize), 0, manifest.countX - 1);
                        int x1 = Mathf.Clamp(Mathf.FloorToInt((maxX - manifest.origin.x) / manifest.cellSize), 0, manifest.countX - 1);
                        int z0 = Mathf.Clamp(Mathf.FloorToInt((minZ - manifest.origin.y) / manifest.cellSize), 0, manifest.countZ - 1);
                        int z1 = Mathf.Clamp(Mathf.FloorToInt((maxZ - manifest.origin.y) / manifest.cellSize), 0, manifest.countZ - 1);
                        for (int z = z0; z <= z1; z++)
                        for (int x = x0; x <= x1; x++)
                        {
                            int ci = manifest.Index(x, z);
                            if (!builders.TryGetValue(ci, out var bld)) builders[ci] = bld = new Builder();
                            if (!bld.Tris.TryGetValue(mat, out var tl)) bld.Tris[mat] = tl = new List<int>();
                            tl.Add(Vertex(bld, me, M, wp, eid, el, a));
                            tl.Add(Vertex(bld, me, M, wp, eid, el, b));
                            tl.Add(Vertex(bld, me, M, wp, eid, el, cIdx));
                        }
                    }
                }
            }

            // Scene objects: one piece per cell.
            var pieces = new List<G3LowpolyBackdrop.Piece>();
            int meshAssets = 0;
            var cellIds = builders.Keys.Union(objects.Keys).OrderBy(k => k).ToList();
            foreach (int ci in cellIds)
            {
                var cell = manifest.cells[ci];
                string name = $"LP_{cell.ix}_{cell.iz}";
                var go = new GameObject(name);
                go.transform.SetParent(backdrop.transform, false);
                var renderers = new List<Renderer>();
                if (builders.TryGetValue(ci, out var bld))
                {
                    var mesh = ToMesh(bld, name);
                    string path = $"{Folder}/{name}.asset";
                    if (AssetDatabase.LoadAssetAtPath<Mesh>(path) != null) AssetDatabase.DeleteAsset(path);
                    AssetDatabase.CreateAsset(mesh, path);
                    meshAssets++;
                    go.AddComponent<MeshFilter>().sharedMesh = mesh;
                    var mr = go.AddComponent<MeshRenderer>();
                    mr.sharedMaterials = bld.Tris.Keys.ToArray();
                    Setup(mr);
                    renderers.Add(mr);
                }
                if (objects.TryGetValue(ci, out var ol))
                    foreach (var e in ol)
                    {
                        if (!cache.TryGetMesh(e.Resource, out var mesh, out var mats)) continue;
                        var o = new GameObject(mesh.name);
                        o.transform.SetParent(go.transform, false);
                        G3WorldImporter.ApplyMatrix(o.transform, e.World);
                        o.AddComponent<MeshFilter>().sharedMesh = mesh;
                        var mr = o.AddComponent<MeshRenderer>();
                        var shown = (Material[])mats.Clone();
                        for (int k = 0; k < shown.Length; k++) if (cache.IsInvisible(shown[k])) shown[k] = null;
                        mr.sharedMaterials = shown;
                        Setup(mr);
                        renderers.Add(mr);
                    }
                go.isStatic = true;
                pieces.Add(new G3LowpolyBackdrop.Piece { cell = ci, renderers = renderers.ToArray() });
            }
            backdrop.pieces = pieces;
            AssetDatabase.SaveAssets();
            return $"backdrop: {landscapeMeshes} low-poly landscape meshes cut into {meshAssets} cell meshes, {objectCount} objects ({skipped} small skipped), {pieces.Count} pieces";
        }

        static void Setup(MeshRenderer mr)
        {
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.lightProbeUsage = LightProbeUsage.Off;
            mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
            mr.gameObject.isStatic = true;
        }

        static int Vertex(Builder b, G3.Formats.Mesh.MeshElement me, Matrix4x4 M, Vector3[] wp, long eid, int el, int v)
        {
            long key = (eid << 40) | ((long)el << 28) | (uint)v;
            if (b.Remap.TryGetValue(key, out int idx)) return idx;
            idx = b.Pos.Count;
            b.Remap[key] = idx;
            b.Pos.Add(wp[v]);
            var n = me.Normals != null ? new Vector3(me.Normals[v * 3], me.Normals[v * 3 + 1], me.Normals[v * 3 + 2]) : Vector3.up;
            b.Nrm.Add(M.MultiplyVector(n).normalized);
            b.Uv0.Add(me.Uv0 != null ? new Vector2(me.Uv0[v * 2], me.Uv0[v * 2 + 1]) : Vector2.zero);
            if (me.Uv1 != null) b.HasUv1 = true;
            b.Uv1.Add(me.Uv1 != null ? new Vector2(me.Uv1[v * 2], me.Uv1[v * 2 + 1]) : Vector2.zero);
            uint c = me.Diffuse != null ? me.Diffuse[v] : 0xFFFFFFFF;
            b.Col.Add(new Color32((byte)(c >> 16), (byte)(c >> 8), (byte)c, (byte)(c >> 24)));
            return idx;
        }

        static Mesh ToMesh(Builder b, string name)
        {
            var mesh = new Mesh { name = name };
            mesh.indexFormat = b.Pos.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(b.Pos);
            mesh.SetNormals(b.Nrm);
            mesh.SetUVs(0, b.Uv0);
            if (b.HasUv1) mesh.SetUVs(1, b.Uv1);
            mesh.SetColors(b.Col);
            mesh.subMeshCount = b.Tris.Count;
            int s = 0;
            foreach (var kv in b.Tris) mesh.SetTriangles(kv.Value, s++, false);
            mesh.RecalculateBounds();
            mesh.RecalculateTangents();
            return mesh;
        }
    }
}
