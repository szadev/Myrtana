using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using G3.Formats.Image;
using G3.Formats.Pak;
using G3.Formats.World;
using UnityEditor;
using UnityEngine;

namespace G3.Unity.Editor
{
    /// <summary>
    /// Imports the eCVegetation_PS grids (grass, flowers, small bushes; Docs/formats-world.md §5.4) of a square
    /// region into a <see cref="G3VegetationData"/> asset and adds a <see cref="G3VegetationRenderer"/> under a parent.
    /// Meshes, textures and materials are cached under Assets/G3Imported/Vegetation and reused.
    /// CLI: G3.Unity.Editor.G3VegetationImporter.ImportRegion(655, 586, 150, GameObject.Find("G3World").transform)
    /// </summary>
    public static class G3VegetationImporter
    {
        public const string Root = "Assets/G3Imported/Vegetation";
        public const string RendererName = "G3Vegetation";
        const float Scale = 0.01f;

        static string DataDir => G3.Formats.G3Install.Pref(EditorPrefs.GetString("G3.DataDir", "")) ?? G3.Formats.G3Install.DataDirOrHint;

        public static string ResultFile => Path.GetFullPath(Path.Combine(Application.dataPath, "../Logs/g3vegetation.txt"));

        /// <summary>Runs <see cref="ImportRegion"/> on the next editor tick (for the 5 s CLI eval limit); result → Logs/g3vegetation.txt.</summary>
        public static string Schedule(float centerXm, float centerZm, float halfSizeM, string parentName = "G3World", bool rebuild = true)
        {
            File.WriteAllText(ResultFile, "running");
            EditorApplication.delayCall += () =>
            {
                string result;
                try
                {
                    var p = GameObject.Find(parentName);
                    result = "done\n" + ImportRegion(centerXm, centerZm, halfSizeM, p != null ? p.transform : null, rebuild);
                }
                catch (Exception ex) { result = "error\n" + ex; }
                File.WriteAllText(ResultFile, result);
            };
            return "scheduled";
        }

        /// <summary>
        /// Reads the vegetation of the region from Projects_compiled, writes/reuses the assets and adds (or replaces)
        /// a "G3Vegetation" renderer object under <paramref name="parent"/> (scene root if null).
        /// </summary>
        /// <param name="rebuild">false: reuse an existing data asset for the same region instead of re-reading the world.</param>
        public static string ImportRegion(float centerXm, float centerZm, float halfSizeM, Transform parent, bool rebuild = true)
        {
            var sw = Stopwatch.StartNew();
            string dataPath = $"{Root}/Regions/Veg_{centerXm:F0}_{centerZm:F0}_{halfSizeM:F0}.asset";
            var data = rebuild ? null : AssetDatabase.LoadAssetAtPath<G3VegetationData>(dataPath);
            string summary;
            if (data == null)
            {
                summary = Build(centerXm, centerZm, halfSizeM, dataPath, out data);
            }
            else summary = $"reused {dataPath}: {data.instanceCount} instances, {data.meshes.Count} meshes, {data.chunks.Count} chunks";

            // Idempotent: replace a previous renderer object under the same parent.
            var existing = parent != null ? parent.Find(RendererName) : GameObject.Find("/" + RendererName)?.transform;
            if (existing != null) UnityEngine.Object.DestroyImmediate(existing.gameObject);
            var go = new GameObject(RendererName);
            if (parent != null) go.transform.SetParent(parent, false);
            var r = go.AddComponent<G3VegetationRenderer>();
            r.data = data;
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(go.scene);
            return summary + $"; total {sw.ElapsedMilliseconds} ms";
        }

        struct Inst
        {
            public int mesh;      // index into data.meshes
            public int cell;
            public VegetationPlant p;
        }

        static string Build(float centerXm, float centerZm, float halfSizeM, string dataPath, out G3VegetationData data)
        {
            var sw = Stopwatch.StartNew();
            float cx = centerXm * 100, cz = centerZm * 100, hs = halfSizeM * 100;
            const float cellM = 50;
            int cells = Mathf.Max(1, Mathf.CeilToInt(halfSizeM * 2 / cellM));

            // Distinct meshes by file + texture; embedded geometry of the first occurrence is used.
            var meshKeys = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var srcMeshes = new List<VegetationMesh>();
            var viewRanges = new List<float>();
            var instances = new List<Inst>();
            int propertySets = 0, totalPlants = 0;

            using (var projects = new G3FileSystem(DataDir, "Projects_compiled"))
            {
                const string vol = "Projects_compiled/";
                var layout = WorldLayout.Build(projects.Files.Select(f => f.Path), p => projects.Get(vol + p)?.Read());
                foreach (var sector in layout.Sectors)
                foreach (var node in sector.Nodes)
                {
                    if (!TileMayIntersect(node, cx, cz, hs)) continue;
                    var entry = projects.Get(vol + node);
                    var list = entry != null ? EntityArchive.Read(entry.Read(), decodeVegetation: true) : null;
                    if (list == null) continue;
                    foreach (var e in list)
                    {
                        var v = e.Vegetation;
                        if (v == null || !e.Enabled) continue;
                        propertySets++;
                        totalPlants += v.Plants.Count;
                        var local = new Dictionary<int, int>();
                        foreach (var p in v.Plants)
                        {
                            if (p.X < cx - hs || p.X >= cx + hs || p.Z < cz - hs || p.Z >= cz + hs) continue;
                            if (!local.TryGetValue(p.MeshIndex, out int mi))
                            {
                                mi = -1;
                                if (v.Meshes.TryGetValue(p.MeshIndex, out var vm) && vm.Positions != null && vm.Indices != null && vm.Indices.Length > 0)
                                {
                                    string key = (vm.MeshFile ?? "mesh" + vm.Index) + "|" + vm.DiffuseTexture + (vm.DoubleSided ? "|DS" : "");
                                    if (!meshKeys.TryGetValue(key, out mi))
                                    {
                                        mi = srcMeshes.Count;
                                        meshKeys[key] = mi;
                                        srcMeshes.Add(vm);
                                        viewRanges.Add(0);
                                    }
                                    viewRanges[mi] = Mathf.Max(viewRanges[mi], v.ViewRange * Scale);
                                }
                                local[p.MeshIndex] = mi;
                            }
                            if (mi < 0) continue;
                            int gx = Mathf.Clamp((int)((p.X - (cx - hs)) / (cellM * 100)), 0, cells - 1);
                            int gz = Mathf.Clamp((int)((p.Z - (cz - hs)) / (cellM * 100)), 0, cells - 1);
                            instances.Add(new Inst { mesh = mi, cell = gz * cells + gx, p = p });
                        }
                    }
                }
            }
            long tParse = sw.ElapsedMilliseconds;

            foreach (var dir in new[] { "Meshes", "Materials", "Textures", "Regions" })
                Directory.CreateDirectory(Path.Combine(Root, dir));

            data = ScriptableObject.CreateInstance<G3VegetationData>();
            data.center = new Vector3(centerXm, 0, centerZm);
            data.halfSize = halfSizeM;
            data.cellSize = cellM;
            var warnings = new List<string>();
            var meshBounds = new Bounds[srcMeshes.Count];

            _created.Clear();
            try
            {
                using (var images = new G3FileSystem(DataDir, "_compiledImage"))
                {
                    var shader = Shader.Find("G3/Vegetation");
                    for (int i = 0; i < srcMeshes.Count; i++)
                    {
                        var vm = srcMeshes[i];
                        string stem = Safe(Path.GetFileNameWithoutExtension(vm.MeshFile ?? "mesh" + vm.Index));
                        string texStem = string.IsNullOrEmpty(vm.DiffuseTexture) ? null : Path.GetFileNameWithoutExtension(vm.DiffuseTexture);
                        // Embedded UVs point into the texture atlas, so the same mesh file can differ per atlas.
                        var mesh = GetMesh(vm, stem + (texStem != null ? "__" + Safe(texStem) : ""));
                        var tex = texStem != null ? GetTexture(images, texStem, warnings) : null;
                        var mat = GetMaterial(shader, tex, vm.DoubleSided, (texStem != null ? Safe(texStem) : "notex") + (vm.DoubleSided ? "_DS" : ""));
                        meshBounds[i] = mesh.bounds;
                        data.meshes.Add(new G3VegetationData.MeshEntry
                        {
                            name = stem, mesh = mesh, material = mat,
                            viewRange = viewRanges[i] > 0 ? viewRanges[i] : 50,
                        });
                    }
                }
            }
            finally
            {
                _created.Clear();
            }

            // Sort by cell, then mesh; emit batches of ≤ MaxBatch and chunk bounds.
            instances.Sort((a, b) => a.cell != b.cell ? a.cell.CompareTo(b.cell) : a.mesh.CompareTo(b.mesh));
            var bytes = new byte[instances.Count * G3VegetationData.InstanceStride];
            var fl = new float[9];
            int ci = 0;
            while (ci < instances.Count)
            {
                int cell = instances[ci].cell;
                var chunk = new G3VegetationData.Chunk { firstBatch = data.batches.Count };
                bool hasBounds = false;
                var cb = new Bounds();
                while (ci < instances.Count && instances[ci].cell == cell)
                {
                    int mesh = instances[ci].mesh;
                    var batch = new G3VegetationData.Batch { mesh = mesh, start = ci };
                    while (ci < instances.Count && instances[ci].cell == cell && instances[ci].mesh == mesh && batch.count < G3VegetationData.MaxBatch)
                    {
                        var p = instances[ci].p;
                        var pos = new Vector3(p.X, p.Y, p.Z) * Scale;
                        fl[0] = pos.x; fl[1] = pos.y; fl[2] = pos.z;
                        fl[3] = p.Qx; fl[4] = p.Qy; fl[5] = p.Qz; fl[6] = p.Qw;
                        fl[7] = p.ScaleWidth; fl[8] = p.ScaleHeight;
                        int o = ci * G3VegetationData.InstanceStride;
                        Buffer.BlockCopy(fl, 0, bytes, o, 36);
                        Buffer.BlockCopy(BitConverter.GetBytes(p.Color), 0, bytes, o + 36, 4);

                        // Conservative instance bounds: mesh bounds radius scaled by the largest axis scale.
                        var mb = meshBounds[mesh];
                        float s = Mathf.Max(Mathf.Abs(p.ScaleWidth), Mathf.Abs(p.ScaleHeight));
                        float rad = (mb.center.magnitude + mb.extents.magnitude) * s;
                        var ib = new Bounds(pos, Vector3.one * (2 * rad));
                        if (!hasBounds) { cb = ib; hasBounds = true; } else cb.Encapsulate(ib);
                        var me = data.meshes[mesh];
                        me.maxHeight = Mathf.Max(me.maxHeight, mb.size.y * Mathf.Abs(p.ScaleHeight));
                        batch.count++;
                        ci++;
                    }
                    data.batches.Add(batch);
                    chunk.viewRange = Mathf.Max(chunk.viewRange, data.meshes[mesh].viewRange);
                }
                chunk.bounds = cb;
                chunk.batchCount = data.batches.Count - chunk.firstBatch;
                for (int b = chunk.firstBatch; b < data.batches.Count; b++) chunk.instanceCount += data.batches[b].count;
                data.chunks.Add(chunk);
            }
            data.instanceCount = instances.Count;

            // Binary instance blob as a TextAsset (.bytes) — the project serializes assets as text.
            string bytesPath = Path.ChangeExtension(dataPath, null) + "_instances.bytes";
            File.WriteAllBytes(bytesPath, bytes);
            AssetDatabase.ImportAsset(bytesPath, ImportAssetOptions.ForceSynchronousImport);
            data.instances = AssetDatabase.LoadAssetAtPath<TextAsset>(bytesPath);

            var old = AssetDatabase.LoadAssetAtPath<G3VegetationData>(dataPath);
            if (old != null)
            {
                EditorUtility.CopySerialized(data, old);
                UnityEngine.Object.DestroyImmediate(data);
                data = old;
                EditorUtility.SetDirty(data);
            }
            else AssetDatabase.CreateAsset(data, dataPath);
            AssetDatabase.SaveAssets();

            var sb = new StringBuilder();
            sb.Append($"vegetation ({centerXm},{centerZm}) ±{halfSizeM} m: {instances.Count} instances of {data.meshes.Count} meshes in {data.chunks.Count} chunks / {data.batches.Count} batches ");
            sb.Append($"(scanned {propertySets} PS with {totalPlants} plants); parse {tParse} ms, build {sw.ElapsedMilliseconds - tParse} ms; {dataPath}");
            foreach (var w in warnings.Distinct().Take(10)) sb.Append("\n").Append(w);
            return sb.ToString();
        }

        // ---------------------------------------------------------------- assets

        // Assets created during the current build (LoadAssetAtPath can miss assets created moments ago).
        static readonly Dictionary<string, UnityEngine.Object> _created = new Dictionary<string, UnityEngine.Object>(StringComparer.OrdinalIgnoreCase);

        static T Load<T>(string path) where T : UnityEngine.Object
        {
            if (_created.TryGetValue(path, out var o) && o != null) return (T)o;
            return AssetDatabase.LoadAssetAtPath<T>(path);
        }

        static void Create(UnityEngine.Object o, string path)
        {
            AssetDatabase.CreateAsset(o, path);
            _created[path] = o;
        }

        static Mesh GetMesh(VegetationMesh vm, string name)
        {
            string path = $"{Root}/Meshes/{name}.asset";
            var mesh = Load<Mesh>(path);
            if (mesh != null) return mesh;

            int n = vm.Positions.Length / 3;
            var pos = new Vector3[n];
            var nrm = new Vector3[n];
            var uv = new Vector2[n];
            bool hasN = vm.Normals != null && vm.Normals.Length >= n * 3;
            bool hasUv = vm.Uvs != null && vm.Uvs.Length >= n * 2;
            for (int i = 0; i < n; i++)
            {
                pos[i] = new Vector3(vm.Positions[i * 3], vm.Positions[i * 3 + 1], vm.Positions[i * 3 + 2]) * Scale;
                nrm[i] = hasN ? new Vector3(vm.Normals[i * 3], vm.Normals[i * 3 + 1], vm.Normals[i * 3 + 2]) : Vector3.up;
                if (hasUv) uv[i] = new Vector2(vm.Uvs[i * 2], vm.Uvs[i * 2 + 1]);
            }
            // D3D winding → Unity: (i0, i2, i1)
            int tc = vm.Indices.Length / 3 * 3;
            var tris = new int[tc];
            for (int t = 0; t < tc; t += 3)
            {
                tris[t] = (int)vm.Indices[t];
                tris[t + 1] = (int)vm.Indices[t + 2];
                tris[t + 2] = (int)vm.Indices[t + 1];
            }
            mesh = new Mesh { name = name };
            mesh.indexFormat = n > 65535 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
            mesh.vertices = pos;
            mesh.normals = nrm;
            mesh.uv = uv;
            mesh.triangles = tris;
            mesh.RecalculateBounds();
            mesh.UploadMeshData(false);
            Create(mesh, path);
            return mesh;
        }

        static Material GetMaterial(Shader shader, Texture2D tex, bool doubleSided, string name)
        {
            string path = $"{Root}/Materials/{name}.mat";
            var m = Load<Material>(path);
            if (m != null) return m;
            if (shader != null)
            {
                m = new Material(shader) { name = name };
                m.SetTexture("_BaseMap", tex);
                m.SetFloat("_Cutoff", 0.5f);
                m.SetFloat("_Cull", doubleSided ? 0 : 2);
            }
            else
            {
                // Fallback: stock URP Lit (no per-instance colour / distance fade).
                m = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = name };
                m.SetTexture("_BaseMap", tex);
                m.SetFloat("_AlphaClip", 1);
                m.SetFloat("_Cutoff", 0.5f);
                m.SetFloat("_Cull", doubleSided ? 0 : 2);
                m.SetFloat("_Smoothness", 0.05f);
                m.SetFloat("_EnvironmentReflections", 0);
                m.SetFloat("_SpecularHighlights", 0);
                BaseShaderGUI.SetMaterialKeywords(m);
            }
            m.enableInstancing = true;
            Create(m, path);
            return m;
        }

        static Texture2D GetTexture(G3FileSystem images, string stem, List<string> warnings)
        {
            string path = $"{Root}/Textures/{Safe(stem)}.asset";
            var tex = Load<Texture2D>(path);
            if (tex != null) return tex;
            var entry = images.GetByName(stem + ".ximg");
            if (entry == null) { warnings.Add("texture not found: " + stem); return null; }
            try
            {
                var img = Ximg.Read(entry.Read());
                int mips = img.Mips.Length;
                switch (img.Format)
                {
                    case PixelFormat.DXT1:
                    case PixelFormat.DXT5:
                        tex = new Texture2D(img.Width, img.Height, img.Format == PixelFormat.DXT1 ? TextureFormat.DXT1 : TextureFormat.DXT5, mips, false);
                        tex.LoadRawTextureData(img.RawData());
                        tex.Apply(false, false);
                        break;
                    case PixelFormat.DXT3:
                        tex = new Texture2D(img.Width, img.Height, TextureFormat.RGBA32, mips, false);
                        for (int i = 0; i < mips; i++)
                            tex.SetPixelData(Dxt.DecodeDxt3(img.Mips[i], Math.Max(1, img.Width >> i), Math.Max(1, img.Height >> i)), i);
                        tex.Apply(false, false);
                        EditorUtility.CompressTexture(tex, TextureFormat.DXT5, TextureCompressionQuality.Normal);
                        break;
                    default:
                        tex = new Texture2D(img.Width, img.Height, TextureFormat.BGRA32, mips, false);
                        tex.LoadRawTextureData(img.RawData());
                        tex.Apply(false, false);
                        break;
                }
                tex.name = stem;
                tex.wrapMode = TextureWrapMode.Repeat;   // vegetation UVs are offset by whole units (negative V)
                tex.anisoLevel = 4;
                Create(tex, path);
                return tex;
            }
            catch (Exception ex) { warnings.Add($"texture {stem}: {ex.Message}"); return null; }
        }

        static string Safe(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name;
        }

        /// <summary>CStat tiles are named by their 100 m cell centre ("..._x55000y0z55000_CStat.node").</summary>
        public static float TileMarginCm = 10000;

        static bool TileMayIntersect(string nodePath, float cx, float cz, float hs)
        {
            int ix = nodePath.LastIndexOf("_x", StringComparison.Ordinal);
            if (ix < 0 || !nodePath.EndsWith("_CStat.node", StringComparison.OrdinalIgnoreCase)) return true;
            var s = nodePath.Substring(ix + 2, nodePath.Length - ix - 2 - "_CStat.node".Length);
            int iy = s.IndexOf('y'), iz = s.IndexOf('z');
            if (iy < 0 || iz < 0 || !float.TryParse(s.Substring(0, iy), out float tx) || !float.TryParse(s.Substring(iz + 1), out float tz))
                return true;
            // Vegetation PS sit in the tile of their grid; plants are world space and stay within ~a tile.
            float margin = TileMarginCm;
            return Math.Abs(tx - cx) <= hs + 5000 + margin && Math.Abs(tz - cz) <= hs + 5000 + margin;
        }

        // ---------------------------------------------------------------- diagnostics

        /// <summary>Plant statistics for the region (counts per mesh, colour / scale ranges), without writing assets.</summary>
        public static string Analyze(float centerXm, float centerZm, float halfSizeM)
        {
            float cx = centerXm * 100, cz = centerZm * 100, hs = halfSizeM * 100;
            var perMesh = new Dictionary<string, int>();
            var colors = new Dictionary<uint, int>();
            float minW = float.MaxValue, maxW = 0, minH = float.MaxValue, maxH = 0;
            int n = 0;
            var sb = new StringBuilder();
            using (var projects = new G3FileSystem(DataDir, "Projects_compiled"))
            {
                const string vol = "Projects_compiled/";
                var layout = WorldLayout.Build(projects.Files.Select(f => f.Path), p => projects.Get(vol + p)?.Read());
                foreach (var sector in layout.Sectors)
                foreach (var node in sector.Nodes)
                {
                    if (!TileMayIntersect(node, cx, cz, hs)) continue;
                    var entry = projects.Get(vol + node);
                    var list = entry != null ? EntityArchive.Read(entry.Read(), decodeVegetation: true) : null;
                    if (list == null) continue;
                    foreach (var e in list)
                    {
                        var v = e.Vegetation;
                        if (v == null) continue;
                        foreach (var p in v.Plants)
                        {
                            if (p.X < cx - hs || p.X >= cx + hs || p.Z < cz - hs || p.Z >= cz + hs) continue;
                            n++;
                            v.Meshes.TryGetValue(p.MeshIndex, out var vm);
                            string k = vm != null ? vm.MeshFile + " | " + vm.DiffuseTexture + (vm.DoubleSided ? " DS" : "") + $" v{vm.Positions?.Length / 3}" : "?" + p.MeshIndex;
                            perMesh[k] = perMesh.TryGetValue(k, out int c) ? c + 1 : 1;
                            colors[p.Color] = colors.TryGetValue(p.Color, out int cc) ? cc + 1 : 1;
                            minW = Math.Min(minW, p.ScaleWidth); maxW = Math.Max(maxW, p.ScaleWidth);
                            minH = Math.Min(minH, p.ScaleHeight); maxH = Math.Max(maxH, p.ScaleHeight);
                        }
                    }
                }
            }
            sb.AppendLine($"{n} plants; scaleW {minW:F2}..{maxW:F2}, scaleH {minH:F2}..{maxH:F2}; {colors.Count} distinct colours");
            foreach (var kv in perMesh.OrderByDescending(k => k.Value)) sb.AppendLine($"  {kv.Value,7} {kv.Key}");
            foreach (var kv in colors.OrderByDescending(k => k.Value).Take(12)) sb.AppendLine($"  colour {kv.Key:X8} × {kv.Value}");
            return sb.ToString();
        }
    }
}
