using System;
using System.Collections.Generic;
using System.IO;
using G3.Formats.Image;
using G3.Formats.Material;
using G3.Formats.Mesh;
using G3.Formats.Pak;
using UnityEditor;
using UnityEngine;
using MaterialResource = G3.Formats.Material.MaterialResource;

namespace G3.Unity.Editor
{
    /// <summary>
    /// Converts Gothic 3 resources into Unity assets under Assets/G3Imported, reusing assets that already exist.
    /// Engine data is centimetres / D3D winding; Unity assets are metres with flipped triangle order.
    /// </summary>
    public sealed class G3AssetCache : IDisposable
    {
        public const string Root = "Assets/G3Imported";
        public const float Scale = 0.01f;

        readonly G3FileSystem _fs;
        readonly IMaterialFactory _materials;
        readonly Dictionary<string, (Mesh mesh, Material[] mats)> _meshes = new Dictionary<string, (Mesh, Material[])>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, Material> _materialCache = new Dictionary<string, Material>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, Texture2D> _textures = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, PakEntry> _images = new Dictionary<string, PakEntry>(StringComparer.OrdinalIgnoreCase);
        Material _fallback;

        public readonly List<string> Warnings = new List<string>();
        public int CreatedAssets;

        public G3AssetCache(string dataDir, IMaterialFactory materials)
        {
            _materials = materials;
            _fs = new G3FileSystem(dataDir, "_compiledMesh", "_compiledMaterial", "_compiledImage");
            foreach (var e in _fs.Files)
                if (e.Path.EndsWith(".ximg", StringComparison.OrdinalIgnoreCase))
                    _images[Path.GetFileNameWithoutExtension(e.Path)] = e;
            foreach (var dir in new[] { "Meshes", "Materials", "Textures" })
                Directory.CreateDirectory(Path.Combine(Root, dir));
        }

        public void Dispose() => _fs.Dispose();

        /// <summary>
        /// Forgets the loaded Mesh/Material/Texture references (the assets stay on disk) so that
        /// EditorUtility.UnloadUnusedAssetsImmediate can free them between world cells.
        /// </summary>
        public void ReleaseLoaded()
        {
            _meshes.Clear();
            _materialCache.Clear();
            _textures.Clear();
            _water.Clear();
            _invisible.Clear();
            _fallback = null;
        }

        /// <summary>Parsed source mesh (.xcmsh, or .xlmsh resolved to LOD0) without creating a Unity asset; null if missing.</summary>
        public MeshResource ReadMeshResource(string file)
        {
            if (string.IsNullOrEmpty(file)) return null;
            string resolved = file;
            if (file.EndsWith(".xlmsh", StringComparison.OrdinalIgnoreCase))
            {
                var lod = _fs.GetByName(file);
                var list = lod != null ? Xlmsh.Read(lod.Read()) : null;
                resolved = list != null && list.Count > 0 ? list[0] : null;
            }
            var entry = resolved != null ? _fs.GetByName(resolved) : null;
            if (entry == null) { Warnings.Add($"mesh not found: {file}"); return null; }
            try { return Xcmsh.Read(entry.Read(), resolved); }
            catch (Exception ex) { Warnings.Add($"mesh {resolved}: {ex.Message}"); return null; }
        }

        public Material Fallback
        {
            get
            {
                if (_fallback != null) return _fallback;
                string path = $"{Root}/Materials/_Fallback.mat";
                _fallback = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (_fallback == null)
                {
                    _fallback = _materials.Create(new MaterialResource { Name = "_Fallback", DiffuseColor = new[] { 0.6f, 0.6f, 0.6f } }, GetTexture);
                    AssetDatabase.CreateAsset(_fallback, path);
                }
                return _fallback;
            }
        }

        /// <summary>Mesh by resource file name (.xcmsh, or .xlmsh resolved to its LOD0).</summary>
        public bool TryGetMesh(string file, out Mesh mesh, out Material[] materials)
        {
            mesh = null; materials = null;
            if (string.IsNullOrEmpty(file)) return false;
            if (_meshes.TryGetValue(file, out var cached))
            {
                mesh = cached.mesh; materials = cached.mats;
                return mesh != null;
            }

            string resolved = file;
            if (file.EndsWith(".xlmsh", StringComparison.OrdinalIgnoreCase))
            {
                var lod = _fs.GetByName(file);
                var list = lod != null ? Xlmsh.Read(lod.Read()) : null;
                resolved = list != null && list.Count > 0 ? list[0] : null;
            }

            MeshResource res = null;
            var entry = resolved != null ? _fs.GetByName(resolved) : null;
            if (entry != null)
            {
                try { res = Xcmsh.Read(entry.Read(), resolved); }
                catch (Exception ex) { Warnings.Add($"mesh {resolved}: {ex.Message}"); }
            }
            else Warnings.Add($"mesh not found: {file}");

            if (res != null && res.Elements.Count > 0)
            {
                string path = $"{Root}/Meshes/{Safe(Path.GetFileNameWithoutExtension(resolved))}.asset";
                mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
                if (mesh == null)
                {
                    mesh = BuildMesh(res);
                    AssetDatabase.CreateAsset(mesh, path);
                    CreatedAssets++;
                }
                materials = new Material[res.Elements.Count];
                for (int i = 0; i < materials.Length; i++)
                    materials[i] = GetMaterial(res.Elements[i].Material);
            }
            _meshes[file] = (mesh, materials);
            return mesh != null;
        }

        static Mesh BuildMesh(MeshResource res)
        {
            int total = 0;
            foreach (var e in res.Elements) total += e.VertexCount;
            var pos = new Vector3[total];
            var nrm = new Vector3[total];
            var uv0 = new Vector2[total];
            var col = new Color32[total];
            Vector2[] uv1 = null;
            foreach (var e in res.Elements) if (e.Uv1 != null) { uv1 = new Vector2[total]; break; }

            var mesh = new Mesh { name = res.Name };
            mesh.indexFormat = total > 65535 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
            var submeshes = new List<int[]>();
            int baseVertex = 0;
            foreach (var e in res.Elements)
            {
                int n = e.VertexCount;
                for (int i = 0; i < n; i++)
                {
                    int v = baseVertex + i;
                    pos[v] = new Vector3(e.Positions[i * 3], e.Positions[i * 3 + 1], e.Positions[i * 3 + 2]) * Scale;
                    if (e.Normals != null) nrm[v] = new Vector3(e.Normals[i * 3], e.Normals[i * 3 + 1], e.Normals[i * 3 + 2]);
                    if (e.Uv0 != null) uv0[v] = new Vector2(e.Uv0[i * 2], e.Uv0[i * 2 + 1]);
                    if (uv1 != null && e.Uv1 != null) uv1[v] = new Vector2(e.Uv1[i * 2], e.Uv1[i * 2 + 1]);
                    uint c = e.Diffuse != null ? e.Diffuse[i] : 0xFFFFFFFF;
                    col[v] = new Color32((byte)(c >> 16), (byte)(c >> 8), (byte)c, (byte)(c >> 24));
                }
                // D3D winding → Unity: (i0, i2, i1)
                var tris = new int[e.Indices.Length];
                for (int t = 0; t < tris.Length; t += 3)
                {
                    tris[t] = (int)e.Indices[t] + baseVertex;
                    tris[t + 1] = (int)e.Indices[t + 2] + baseVertex;
                    tris[t + 2] = (int)e.Indices[t + 1] + baseVertex;
                }
                submeshes.Add(tris);
                baseVertex += n;
            }
            mesh.vertices = pos;
            mesh.normals = nrm;
            mesh.uv = uv0;
            if (uv1 != null) mesh.uv2 = uv1;
            mesh.colors32 = col;
            mesh.subMeshCount = submeshes.Count;
            for (int i = 0; i < submeshes.Count; i++) mesh.SetTriangles(submeshes[i], i, false);
            mesh.RecalculateBounds();
            mesh.RecalculateTangents();
            return mesh;
        }

        readonly HashSet<Material> _water = new HashSet<Material>();
        readonly HashSet<Material> _invisible = new HashSet<Material>();

        public bool IsWater(Material m) => m != null && _water.Contains(m);
        public bool IsInvisible(Material m) => m != null && _invisible.Contains(m);

        public Material GetMaterial(string file)
        {
            if (string.IsNullOrEmpty(file)) return Fallback;
            string stem = Path.GetFileNameWithoutExtension(file);
            if (_materialCache.TryGetValue(stem, out var m)) return m;

            // The source is always parsed (cheap) so water/invisible classification also works for cached assets.
            var entry = _fs.GetByName(stem + ".xshmat");
            MaterialResource res = null;
            if (entry != null)
            {
                try { res = Xshmat.Read(entry.Read(), stem); }
                catch (Exception ex) { Warnings.Add($"material {stem}: {ex.Message}"); }
            }
            else Warnings.Add($"material not found: {file}");

            string path = $"{Root}/Materials/{Safe(stem)}.mat";
            m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null)
            {
                if (res == null) m = Fallback;
                else
                {
                    m = _materials.Create(res, GetTexture);
                    m.name = stem;
                    AssetDatabase.CreateAsset(m, path);
                    CreatedAssets++;
                }
            }
            if (res != null && m != Fallback)
            {
                if (UrpMaterialFactory.IsInvisible(res)) _invisible.Add(m);
                else if (UrpMaterialFactory.IsWater(res)) _water.Add(m);
            }
            _materialCache[stem] = m;
            return m;
        }

        /// <summary>
        /// Rebuilds existing material assets (<c>Assets/G3Imported/Materials/&lt;stem&gt;.mat</c>) from their .xshmat with the
        /// current factory, in place: the asset keeps its GUID, so cell scenes and prefabs keep their references and no
        /// world reimport is needed. Assets without a source .xshmat (character variants, _Fallback) are left alone.
        /// </summary>
        /// <param name="filter">Case-insensitive substring of the stem, or null for all.</param>
        public int RefreshMaterials(string filter = null)
        {
            string folder = $"{Root}/Materials";
            int n = 0;
            foreach (var guid in AssetDatabase.FindAssets("t:Material", new[] { folder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetDirectoryName(path)?.Replace('\\', '/') != folder) continue;
                string stem = Path.GetFileNameWithoutExtension(path);
                if (filter != null && stem.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                var entry = _fs.GetByName(stem + ".xshmat");
                if (entry == null) continue;
                MaterialResource res;
                try { res = Xshmat.Read(entry.Read(), stem); }
                catch (Exception ex) { Warnings.Add($"material {stem}: {ex.Message}"); continue; }
                var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (existing == null) continue;
                var fresh = _materials.Create(res, GetTexture);
                fresh.name = existing.name;
                EditorUtility.CopySerialized(fresh, existing);
                UnityEngine.Object.DestroyImmediate(fresh);
                EditorUtility.SetDirty(existing);
                n++;
            }
            AssetDatabase.SaveAssets();
            return n;
        }

        /// <summary>CLI entry: <c>G3.Unity.Editor.G3AssetCache.RefreshMaterialAssets(null)</c>.</summary>
        public static string RefreshMaterialAssets(string filter = null)
        {
            using (var cache = new G3AssetCache(G3WorldImporter.DataDir, new UrpMaterialFactory()))
            {
                int n = cache.RefreshMaterials(filter);
                return $"{n} material assets rebuilt; {cache.Warnings.Count} warnings";
            }
        }

        [MenuItem("G3/World/Rebuild Material Assets", priority = 130)]
        static void MenuRefreshMaterials() => Debug.Log(RefreshMaterialAssets());

        /// <summary>
        /// Sampling of world textures as Gothic 3 at its top texture settings (ge3.ini [Options.VeryHigh]: TextureQuality=3,
        /// TextureFilter=4 = anisotropic 16x; D3D9 linear mip filter = trilinear). Before: bilinear (no blend between mips:
        /// a hard switch to the next, half-resolution mip) and anisotropy 4, which made ground and rock faces seen at an angle
        /// (slopes in front of the hero) blurry and smeared (tools-2).
        /// </summary>
        public const int WorldAnisoLevel = 16;

        public static void ApplyWorldFiltering(Texture2D tex)
        {
            tex.filterMode = FilterMode.Trilinear;
            tex.anisoLevel = WorldAnisoLevel;
            tex.mipMapBias = 0f;
        }

        /// <summary>
        /// Applies <see cref="ApplyWorldFiltering"/> to the existing texture assets in Assets/G3Imported/Textures in place (GUIDs
        /// kept, no world reimport). CLI: <c>G3.Unity.Editor.G3AssetCache.RefreshTextureFiltering()</c>.
        /// </summary>
        public static string RefreshTextureFiltering()
        {
            int n = 0, total = 0;
            foreach (var guid in AssetDatabase.FindAssets("t:Texture2D", new[] { Root + "/Textures" }))
            {
                var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(AssetDatabase.GUIDToAssetPath(guid));
                if (tex == null) continue;
                total++;
                if (tex.filterMode == FilterMode.Trilinear && tex.anisoLevel == WorldAnisoLevel && tex.mipMapBias == 0f) continue;
                ApplyWorldFiltering(tex);
                EditorUtility.SetDirty(tex);
                n++;
            }
            AssetDatabase.SaveAssets();
            return $"{n} of {total} world textures set to trilinear, aniso {WorldAnisoLevel}";
        }

        /// <summary>
        /// Rebuilds texture assets whose pixels no longer match the game data, in place (GUIDs, wrap and filter settings
        /// kept). Texture assets are only created when missing, so a cache built from an earlier install survives every
        /// reimport: in Oct 2026 156 world/character textures still held the old install's images (C:\Gothic3-backup,
        /// e.g. G3_Human_Paladin/RebMercenary/NrdWarrior_Body_Diffuse_S1) instead of CP 1.75's, and the R1 A|B colour check
        /// compared B against different art. Scans Assets/G3Imported/Textures and Characters/Textures; <paramref name="dryRun"/>
        /// only counts. CLI: <c>G3.Unity.Editor.G3AssetCache.RefreshStaleTextures(false)</c>.
        /// </summary>
        public static string RefreshStaleTextures(bool dryRun = false, string dataDir = null)
        {
            dataDir ??= G3WorldImporter.DataDir;
            var images = new Dictionary<string, PakEntry>(StringComparer.OrdinalIgnoreCase);
            using var fs = new G3FileSystem(dataDir, "_compiledMesh", "_compiledMaterial", "_compiledImage", "_compiledAnimation");
            foreach (var e in fs.Files)
                if (e.Path.EndsWith(".ximg", StringComparison.OrdinalIgnoreCase))
                    images[Path.GetFileNameWithoutExtension(e.Path)] = e;
            int total = 0, stale = 0, missing = 0, n = 0;
            var names = new List<string>();
            foreach (var guid in AssetDatabase.FindAssets("t:Texture2D", new[] { Root + "/Textures", Root + "/Characters/Textures" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.EndsWith(".asset", StringComparison.OrdinalIgnoreCase)) continue; // remaster PNGs are not from the paks
                string stem = Path.GetFileNameWithoutExtension(path);
                bool normal = stem.EndsWith("_n", StringComparison.Ordinal);
                if (normal) stem = stem.Substring(0, stem.Length - 2);
                if (!images.TryGetValue(stem, out var entry)) { missing++; continue; }
                var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                if (tex == null) continue;
                total++;
                Texture2D fresh;
                try { fresh = BuildTexture(Ximg.Read(entry.Read()), normal); }
                catch (Exception) { continue; }
                bool same = tex.isReadable && fresh.width == tex.width && fresh.height == tex.height && fresh.format == tex.format &&
                            fresh.GetRawTextureData<byte>().AsReadOnlySpan().SequenceEqual(tex.GetRawTextureData<byte>().AsReadOnlySpan());
                if (!same)
                {
                    stale++;
                    if (names.Count < 40) names.Add(stem + (normal ? "_n" : ""));
                    if (!dryRun)
                    {
                        fresh.name = tex.name;
                        fresh.wrapModeU = tex.wrapModeU;
                        fresh.wrapModeV = tex.wrapModeV;
                        fresh.filterMode = tex.filterMode;
                        fresh.anisoLevel = tex.anisoLevel;
                        fresh.mipMapBias = tex.mipMapBias;
                        EditorUtility.CopySerialized(fresh, tex);
                        EditorUtility.SetDirty(tex);
                    }
                }
                UnityEngine.Object.DestroyImmediate(fresh);
                if (++n % 100 == 0)
                {
                    if (!dryRun) AssetDatabase.SaveAssets();
                    EditorUtility.UnloadUnusedAssetsImmediate();
                }
            }
            if (!dryRun) AssetDatabase.SaveAssets();
            return $"{stale} of {total} cached textures {(dryRun ? "differ from" : "rebuilt from")} {dataDir} ({missing} without a game image)" +
                   (names.Count > 0 ? ": " + string.Join(", ", names) + (stale > names.Count ? ", ..." : "") : "");
        }

        [MenuItem("G3/World/Refresh Texture Filtering", priority = 131)]
        static void MenuRefreshTextureFiltering() => Debug.Log(RefreshTextureFiltering());

        /// <summary>Texture asset of an image stem that no material references (SpeedTree bark / composite atlases),
        /// created on first use; null if the image is not in _compiledImage.</summary>
        public Texture2D GetImageTexture(string image, bool normalMap = false, bool warn = true)
        {
            if (!warn && !_images.ContainsKey(image)) return null;
            return GetTexture(new TextureSlot { Image = image }, normalMap);
        }

        Texture2D GetTexture(TextureSlot slot, bool normalMap)
        {
            string key = slot.Image + (normalMap ? "#n" : "");
            if (_textures.TryGetValue(key, out var tex)) return tex;

            string path = $"{Root}/Textures/{Safe(slot.Image)}{(normalMap ? "_n" : "")}.asset";
            tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (tex == null && slot.Bake != null)
            {
                // fix-260: composite SelfIllumination (fire golem lava) baked from its game images.
                try
                {
                    tex = SelfIlluminationTextures.Bake(slot.Bake,
                        stem => _images.TryGetValue(stem, out var ie) ? Ximg.Read(ie.Read()) : null, out var error);
                    if (tex == null) Warnings.Add($"self-illumination {slot.Image}: {error}");
                    else
                    {
                        ApplyWorldFiltering(tex);
                        AssetDatabase.CreateAsset(tex, path);
                        CreatedAssets++;
                    }
                }
                catch (Exception ex) { Warnings.Add($"self-illumination {slot.Image}: {ex.Message}"); tex = null; }
            }
            else if (tex == null)
            {
                if (_images.TryGetValue(slot.Image, out var entry))
                {
                    try
                    {
                        tex = BuildTexture(Ximg.Read(entry.Read()), normalMap);
                        tex.name = slot.Image;
                        tex.wrapModeU = slot.ClampU ? TextureWrapMode.Clamp : TextureWrapMode.Repeat;
                        tex.wrapModeV = slot.ClampV ? TextureWrapMode.Clamp : TextureWrapMode.Repeat;
                        ApplyWorldFiltering(tex);
                        AssetDatabase.CreateAsset(tex, path);
                        CreatedAssets++;
                    }
                    catch (Exception ex) { Warnings.Add($"texture {slot.Image}: {ex.Message}"); tex = null; }
                }
                else Warnings.Add($"texture not found: {slot.Image}");
            }
            _textures[key] = tex;
            return tex;
        }

        static Texture2D BuildTexture(ImageResource img, bool normalMap)
        {
            int mips = img.Mips.Length;
            switch (img.Format)
            {
                case PixelFormat.DXT1:
                case PixelFormat.DXT5:
                {
                    var fmt = img.Format == PixelFormat.DXT1 ? TextureFormat.DXT1 : TextureFormat.DXT5;
                    var raw = img.RawData();
                    if (normalMap && img.Format == PixelFormat.DXT5) Dxt.FixNormalDxt5(raw, flipGreen: true);
                    var tex = new Texture2D(img.Width, img.Height, fmt, mips, normalMap);
                    tex.LoadRawTextureData(raw);
                    tex.Apply(false, false);
                    return tex;
                }
                case PixelFormat.DXT3:
                {
                    // Unity has no BC2 format: decode to RGBA32, then recompress as DXT5 (BC3).
                    var tex = new Texture2D(img.Width, img.Height, TextureFormat.RGBA32, mips, normalMap);
                    for (int i = 0; i < mips; i++)
                    {
                        int w = Math.Max(1, img.Width >> i), h = Math.Max(1, img.Height >> i);
                        tex.SetPixelData(Dxt.DecodeDxt3(img.Mips[i], w, h), i);
                    }
                    tex.Apply(false, false);
                    EditorUtility.CompressTexture(tex, TextureFormat.DXT5, TextureCompressionQuality.Normal);
                    return tex;
                }
                default:
                {
                    var tex = new Texture2D(img.Width, img.Height, TextureFormat.BGRA32, mips, normalMap);
                    tex.LoadRawTextureData(img.RawData());
                    tex.Apply(false, false);
                    return tex;
                }
            }
        }

        static string Safe(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name;
        }
    }
}
