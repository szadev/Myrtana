using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using G3.Formats.Remaster;
using UnityEditor;
using UnityEngine;

namespace G3.Unity.Editor.Remaster
{
    /// <summary>
    /// Ground cover scatter assets (Docs/remaster-world.md section 18.4): resolves every <see cref="ScatterCatalog"/> source to
    /// a mesh + material and writes <see cref="G3ScatterSet"/> to <c>Assets/G3Imported/Remaster/Scatter/Resources</c>
    /// (git-ignored; G3-derived and locally generated content):
    /// <list type="bullet">
    /// <item>G3 vegetation meshes (<c>Assets/G3Imported/Vegetation/Meshes/&lt;mesh&gt;__&lt;atlas&gt;.asset</c>) with their atlas
    ///   material (<c>Vegetation/Materials/&lt;atlas&gt;.mat</c>);</item>
    /// <item>G3 static meshes (<c>Assets/G3Imported/Meshes/&lt;mesh&gt;.asset</c>) with their G3 material from the world inventory
    ///   (<c>GameData/Remaster/World/Inventory/meshes.tsv</c>, column materials);</item>
    /// <item>generated cards <c>GameData/Remaster/Scatter/Cards/&lt;name&gt;.png</c> (alpha; Tools/Remaster/scatter/scatter_ai.py:
    ///   SDXL, matted, colour matched to G3's ground images) for <c>gen:&lt;name&gt;</c>;</item>
    /// <item>generated rocks <c>GameData/Remaster/Scatter/Rocks/&lt;name&gt;.obj</c> (+ <c>&lt;name&gt;_lod1.obj</c>; Hunyuan3D-2mini,
    ///   decimated) for <c>gen:rock_*</c>; missing rocks fall back to the runtime's procedural rock.</item>
    /// </list>
    /// CLI: <c>G3.Unity.Editor.Remaster.G3ScatterSetBuilder.Build()</c>.
    /// </summary>
    public static class G3ScatterSetBuilder
    {
        public const string Root = G3AssetCache.Root + "/Remaster/Scatter";
        public const string SetPath = Root + "/Resources/" + G3ScatterSet.ResourceName + ".asset";
        const string TextureDir = Root + "/Textures", MeshDir = Root + "/Meshes";

        static string Project => Directory.GetParent(Application.dataPath)!.FullName;
        static string Source => Path.Combine(RemasterPaths.Root(Project), "Scatter");

        [MenuItem("G3/Remaster/World/Build Scatter Set", priority = 236)]
        static void MenuBuild() => Debug.Log(Build());

        public static string Build()
        {
            foreach (var d in new[] { Root, Root + "/Resources", TextureDir, MeshDir }) Directory.CreateDirectory(d);
            var set = AssetDatabase.LoadAssetAtPath<G3ScatterSet>(SetPath);
            if (set == null)
            {
                set = ScriptableObject.CreateInstance<G3ScatterSet>();
                AssetDatabase.CreateAsset(set, SetPath);
            }
            var materialOf = StaticMaterials();
            var catalog = ScatterCatalog.Default();
            var done = new HashSet<string>();
            var entries = new List<G3ScatterSet.Entry>();
            var missing = new List<string>();
            foreach (var item in catalog.Items)
                foreach (var src in item.Sources)
                {
                    if (!done.Add(src)) continue;
                    var e = new G3ScatterSet.Entry { source = src };
                    if (src.StartsWith("gen:", StringComparison.Ordinal))
                    {
                        string name = src.Substring(4);
                        string png = Path.Combine(Source, "Cards", name + ".png");
                        string obj = Path.Combine(Source, "Rocks", name + ".obj");
                        if (File.Exists(png)) e.texture = ImportCard(png, name);
                        if (File.Exists(obj))
                        {
                            e.mesh = ImportObj(obj, name);
                            string lod1 = Path.Combine(Source, "Rocks", name + "_lod1.obj");
                            if (File.Exists(lod1)) e.lod1 = ImportObj(lod1, name + "_lod1");
                        }
                        if (e.texture == null && e.mesh == null) { missing.Add(src); continue; }
                    }
                    else
                    {
                        var veg = AssetDatabase.FindAssets(src + "__ t:Mesh", new[] { G3AssetCache.Root + "/Vegetation/Meshes" });
                        foreach (var guid in veg)
                        {
                            string path = AssetDatabase.GUIDToAssetPath(guid);
                            string file = Path.GetFileNameWithoutExtension(path);
                            if (!file.StartsWith(src + "__", StringComparison.Ordinal)) continue;
                            e.mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
                            string atlas = file.Substring(src.Length + 2);
                            e.material = AssetDatabase.LoadAssetAtPath<Material>($"{G3AssetCache.Root}/Vegetation/Materials/{atlas}.mat");
                            break;
                        }
                        if (e.mesh == null)
                        {
                            e.mesh = AssetDatabase.LoadAssetAtPath<Mesh>($"{G3AssetCache.Root}/Meshes/{src}.asset");
                            if (e.mesh != null && materialOf.TryGetValue(src, out var mat))
                                e.material = AssetDatabase.LoadAssetAtPath<Material>($"{G3AssetCache.Root}/Materials/{mat}.mat");
                        }
                        if (e.mesh == null || e.material == null) { missing.Add(src); continue; }
                        if (!e.mesh.isReadable) e.mesh = ReadableCopy(e.mesh, src);   // the runtime merges meshes per material (one draw call)
                    }
                    entries.Add(e);
                }
            set.cardArray = BuildCardArray(entries);
            set.entries = entries;
            EditorUtility.SetDirty(set);
            AssetDatabase.SaveAssets();
            return $"scatter set: {entries.Count} sources ({SetPath}); missing {missing.Count}: {string.Join(", ", missing)}";
        }

        /// <summary>
        /// Pass 2: the generated cards (same size) stacked into one vertical strip PNG and imported as a Texture2DArray
        /// (flipbook, one row per card, slice 0 at the top); each card entry gets its slice. Null when fewer than two cards.
        /// </summary>
        static Texture2DArray BuildCardArray(List<G3ScatterSet.Entry> entries)
        {
            var cards = new List<int>();
            for (int i = 0; i < entries.Count; i++)
                if (entries[i].source.StartsWith("gen:", StringComparison.Ordinal) && entries[i].mesh == null && entries[i].texture != null) cards.Add(i);
            if (cards.Count < 2) return null;
            const int size = 1024;
            var strip = new Texture2D(size, size * cards.Count, TextureFormat.RGBA32, false);
            for (int k = 0; k < cards.Count; k++)
            {
                var e = entries[cards[k]];
                string png = Path.Combine(Source, "Cards", e.source.Substring(4) + ".png");
                var t = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                t.LoadImage(File.ReadAllBytes(png));
                if (t.width != size || t.height != size)
                {
                    var rt = RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                    Graphics.Blit(t, rt);
                    var prev = RenderTexture.active; RenderTexture.active = rt;
                    var r = new Texture2D(size, size, TextureFormat.RGBA32, false);
                    r.ReadPixels(new Rect(0, 0, size, size), 0, 0); r.Apply();
                    RenderTexture.active = prev; RenderTexture.ReleaseTemporary(rt);
                    UnityEngine.Object.DestroyImmediate(t); t = r;
                }
                strip.SetPixels32(0, (cards.Count - 1 - k) * size, size, size, t.GetPixels32());
                UnityEngine.Object.DestroyImmediate(t);
                e.arraySlice1 = k + 1;
                entries[cards[k]] = e;
            }
            string path = $"{TextureDir}/scatter_cards_array.png";
            File.WriteAllBytes(path, strip.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(strip);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            if (AssetImporter.GetAtPath(path) is TextureImporter ti)
            {
                ti.textureType = TextureImporterType.Default;
                ti.textureShape = TextureImporterShape.Texture2DArray;
                var st = new TextureImporterSettings();
                ti.ReadTextureSettings(st);
                st.flipbookRows = cards.Count; st.flipbookColumns = 1;
                ti.SetTextureSettings(st);
                ti.sRGBTexture = true;
                ti.alphaSource = TextureImporterAlphaSource.FromInput;
                ti.alphaIsTransparency = true;
                ti.mipMapsPreserveCoverage = true;
                ti.alphaTestReferenceValue = 0.42f;
                ti.maxTextureSize = 16384;
                ti.anisoLevel = G3AssetCache.WorldAnisoLevel;
                ti.filterMode = FilterMode.Trilinear;
                ti.mipmapEnabled = true;
                ti.wrapMode = TextureWrapMode.Clamp;
                ti.textureCompression = TextureImporterCompression.CompressedHQ;
                ti.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2DArray>(path);
        }

        /// <summary>A CPU-readable copy of a G3 mesh (Assets/G3Imported/Remaster/Scatter/Meshes): G3GroundScatter combines the
        /// meshes of one material into one mesh and draws them with one multi-command indirect call.</summary>
        static Mesh ReadableCopy(Mesh src, string name)
        {
            string path = $"{MeshDir}/{name}.asset";
            var copy = UnityEngine.Object.Instantiate(src);
            copy.name = name;
            var so = new SerializedObject(copy);
            var pr = so.FindProperty("m_IsReadable");
            if (pr != null) { pr.boolValue = true; so.ApplyModifiedPropertiesWithoutUndo(); }
            var old = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (old != null) { EditorUtility.CopySerialized(copy, old); UnityEngine.Object.DestroyImmediate(copy); return old; }
            AssetDatabase.CreateAsset(copy, path);
            return copy;
        }

        /// <summary>G3 mesh name → its first G3 material (world inventory, game-derived, GameData).</summary>
        static Dictionary<string, string> StaticMaterials()
        {
            var map = new Dictionary<string, string>();
            string tsv = Path.Combine(WorldRemasterPaths.Root(Project), "Inventory", "meshes.tsv");
            if (!File.Exists(tsv)) return map;
            string[] head = null;
            foreach (var line in File.ReadLines(tsv))
            {
                var f = line.Split('\t');
                if (head == null) { head = f; continue; }
                int mi = Array.IndexOf(head, "materials");
                if (mi < 0 || f.Length <= mi) continue;
                string mesh = Path.GetFileNameWithoutExtension(f[0]);
                string mat = f[mi].Split(',', ';', ' ')[0];
                if (!string.IsNullOrEmpty(mat) && !map.ContainsKey(mesh)) map[mesh] = mat;
            }
            return map;
        }

        static Texture2D ImportCard(string src, string name)
        {
            string path = $"{TextureDir}/{name}.png";
            File.Copy(src, path, true);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            if (AssetImporter.GetAtPath(path) is TextureImporter ti)
            {
                ti.textureType = TextureImporterType.Default;
                ti.sRGBTexture = true;
                ti.alphaSource = TextureImporterAlphaSource.FromInput;
                ti.alphaIsTransparency = true;
                ti.mipMapsPreserveCoverage = true;
                ti.alphaTestReferenceValue = 0.42f;
                ti.maxTextureSize = 1024;
                ti.anisoLevel = G3AssetCache.WorldAnisoLevel;
                ti.filterMode = FilterMode.Trilinear;
                ti.mipmapEnabled = true;
                ti.wrapMode = TextureWrapMode.Clamp;
                ti.textureCompression = TextureImporterCompression.CompressedHQ;
                ti.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        /// <summary>Minimal OBJ (v / vn / vt / triangular or polygon f) → a Mesh asset; pivot as written (ground at y = 0).</summary>
        static Mesh ImportObj(string src, string name)
        {
            var v = new List<Vector3>(); var vn = new List<Vector3>(); var vt = new List<Vector2>();
            var outV = new List<Vector3>(); var outN = new List<Vector3>(); var outT = new List<Vector2>(); var tris = new List<int>();
            var map = new Dictionary<string, int>();
            var ci = CultureInfo.InvariantCulture;
            foreach (var raw in File.ReadLines(src))
            {
                var l = raw.Trim();
                if (l.StartsWith("v ")) { var p = l.Split((char[])null, StringSplitOptions.RemoveEmptyEntries); v.Add(new Vector3(-float.Parse(p[1], ci), float.Parse(p[2], ci), float.Parse(p[3], ci))); }
                else if (l.StartsWith("vn ")) { var p = l.Split((char[])null, StringSplitOptions.RemoveEmptyEntries); vn.Add(new Vector3(-float.Parse(p[1], ci), float.Parse(p[2], ci), float.Parse(p[3], ci))); }
                else if (l.StartsWith("vt ")) { var p = l.Split((char[])null, StringSplitOptions.RemoveEmptyEntries); vt.Add(new Vector2(float.Parse(p[1], ci), float.Parse(p[2], ci))); }
                else if (l.StartsWith("f "))
                {
                    var p = l.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                    var idx = new List<int>();
                    for (int k = 1; k < p.Length; k++)
                    {
                        if (!map.TryGetValue(p[k], out int id))
                        {
                            var s = p[k].Split('/');
                            int iv = int.Parse(s[0]); iv = iv < 0 ? v.Count + iv : iv - 1;
                            outV.Add(v[iv]);
                            int it = s.Length > 1 && s[1].Length > 0 ? int.Parse(s[1]) : 0;
                            outT.Add(it != 0 ? vt[it < 0 ? vt.Count + it : it - 1] : Vector2.zero);
                            int ivn = s.Length > 2 && s[2].Length > 0 ? int.Parse(s[2]) : 0;
                            outN.Add(ivn != 0 ? vn[ivn < 0 ? vn.Count + ivn : ivn - 1] : Vector3.zero);
                            id = outV.Count - 1;
                            map[p[k]] = id;
                        }
                        idx.Add(id);
                    }
                    // OBJ is right-handed counter-clockwise; x mirrored above -> reverse the winding for Unity
                    for (int k = 1; k + 1 < idx.Count; k++) { tris.Add(idx[0]); tris.Add(idx[k + 1]); tris.Add(idx[k]); }
                }
            }
            var mesh = new Mesh { name = name, indexFormat = outV.Count > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16 };
            mesh.SetVertices(outV); mesh.SetUVs(0, outT); mesh.SetTriangles(tris, 0);
            if (vn.Count > 0) mesh.SetNormals(outN); else mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            string path = $"{MeshDir}/{name}.asset";
            var old = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (old != null) { EditorUtility.CopySerialized(mesh, old); UnityEngine.Object.DestroyImmediate(mesh); return old; }
            AssetDatabase.CreateAsset(mesh, path);
            return mesh;
        }
    }
}
