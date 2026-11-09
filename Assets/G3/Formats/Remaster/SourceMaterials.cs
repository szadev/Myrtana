using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using G3.Formats.Animation;
using G3.Formats.Material;

namespace G3.Formats.Remaster
{
    /// <summary>A texture property of a character material: the image stem it shows.</summary>
    public sealed class SourceTexture
    {
        public string Property;   // Unity property (_BaseMap, _BumpMap, _SpecGlossMap, …)
        public string Image;      // image stem as stored
        public bool Normal;
    }

    /// <summary>A material slot as the Unity importer names it, with the images of its texture properties.</summary>
    public sealed class SourceMaterial
    {
        public string Name;
        public readonly List<SourceTexture> Textures = new List<SourceTexture>();
        /// <summary>The image of <c>_BaseMap</c> (what the Source glb embeds as base colour), null without one.</summary>
        public string BaseImage => Textures.FirstOrDefault(t => t.Property == "_BaseMap")?.Image;
        public override string ToString() => Name;
    }

    /// <summary>
    /// Pure port of the Unity character importer's material naming (G3CharacterImporter.GetMaterial / Variant /
    /// ResolveImage / LoadTexture, G3AssetCache.GetMaterial, UrpMaterialFactory's texture properties): which material a
    /// mesh slot gets, its name (the glb material name: <c>&lt;FXA material&gt;</c>, the .xshmat stem, or
    /// <c>&lt;name&gt;_S&lt;n&gt;</c> when a MaterialSwitch changes one of its images) and its base colour image.
    /// </summary>
    public sealed class SourceMaterials
    {
        public const string FallbackName = "_Fallback";
        readonly ISourceData _data;
        readonly Dictionary<string, SourceMaterial> _world = new Dictionary<string, SourceMaterial>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, SourceMaterial> _fxa = new Dictionary<string, SourceMaterial>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, SourceMaterial> _variants = new Dictionary<string, SourceMaterial>(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Log = new List<string>();

        public SourceMaterials(ISourceData data) { _data = data; }

        static readonly Regex VariantSuffix = new Regex(@"^(.*?)(_S\d+|_\d+)$", RegexOptions.IgnoreCase);

        /// <summary>Existing image stem for texture <paramref name="stem"/> under switch <paramref name="sw"/>, or null.</summary>
        public string ResolveImage(string stem, int sw)
        {
            if (stem == null) return null;
            var m = VariantSuffix.Match(stem);
            string b = m.Success ? m.Groups[1].Value : stem;
            var cands = sw > 0 ? new[] { $"{b}_S{sw}", stem, $"{b}_S1", $"{b}_01" } : new[] { stem, $"{b}_S1", $"{b}_01" };
            foreach (var c in cands)
            {
                var hit = _data.ImageName(c);
                if (hit != null) return hit;
            }
            return null;
        }

        /// <summary>Character texture: the image, else its switch/number variant (G3_Orc_Face_Diffuse_01 → _S1).</summary>
        string CharacterImage(string image)
        {
            if (image == null) return null;
            var hit = _data.ImageName(image);
            if (hit != null) return hit;
            var alt = ResolveImage(image, 0);
            return alt != null && !string.Equals(alt, image, StringComparison.OrdinalIgnoreCase) ? CharacterImage(alt) : null;
        }

        /// <summary>World texture: only the image itself.</summary>
        string WorldImage(string image) => image != null ? _data.ImageName(image) : null;

        /// <summary>The (pre-switch) material of an actor's material id.</summary>
        public SourceMaterial ForActor(EmfxActor act, int materialId, string actorStem)
        {
            string xsh = null;
            foreach (var r in act.MaterialRefs)
                if (r.MaterialIndex == materialId && !string.IsNullOrEmpty(r.Name)) { xsh = Path.GetFileNameWithoutExtension(r.Name); break; }
            var mc = materialId >= 0 && materialId < act.Materials.Count ? act.Materials[materialId] : null;
            string layerDiffuse = mc?.Layers.FirstOrDefault(l => l.MapType == 1)?.Texture;
            MaterialResource shared = xsh != null ? _data.Shader(xsh) : null;
            // Bodies name the same texture in the FXA layer and the .xshmat: the shared world material. Heads reference
            // generic materials; their per-head textures only live in the FXA layers (unless the layer's image is missing).
            // fix-237: also when the layer is the numbered source X_01 of the .xshmat's switch image X_S1 (Stalker).
            if (shared != null && ActorMaterialChoice.UseShared(shared.Diffuse?.Image, layerDiffuse, l => ResolveImage(l, 0) != null))
                return World(xsh);

            string matName = (mc?.Name ?? $"Mat{materialId}").Replace("|", "");
            string key = actorStem + "_" + matName;
            if (_fxa.TryGetValue(key, out var hit)) return hit;
            var mat = new SourceMaterial { Name = matName };
            if (mc != null)
            {
                string diffuse = null, normal = null;
                foreach (var l in mc.Layers)
                {
                    if (l.MapType == 1) diffuse ??= l.Texture;
                    else if (l.MapType == 3) normal ??= l.Texture;
                }
                Add(mat, "_BaseMap", CharacterImage(diffuse), false);
                Add(mat, "_BumpMap", CharacterImage(normal), true);
            }
            return _fxa[key] = mat;
        }

        /// <summary>Shared world material by .xshmat file or stem (static meshes; bodies); <see cref="FallbackName"/> when missing.</summary>
        public SourceMaterial World(string file)
        {
            if (string.IsNullOrEmpty(file)) return Fallback();
            string stem = Path.GetFileNameWithoutExtension(file);
            if (_world.TryGetValue(stem, out var hit)) return hit;
            var res = _data.Shader(stem);
            if (res == null) { Log.Add($"material not found: {file}"); return _world[stem] = Fallback(); }
            var mat = new SourceMaterial { Name = stem };
            string diffuse = WorldImage(res.Diffuse?.Image), normal = WorldImage(res.Normal?.Image);
            string detailA = WorldImage(res.DiffuseDetail?.Image);
            string detailB = res.VertexAlphaBlend ? WorldImage(res.DiffuseDetail2?.Image) : null;
            bool single = !res.VertexAlphaBlend && detailA != null && diffuse != null;
            if ((res.VertexAlphaBlend || single) && res.Blend == BlendMode.Normal)
            {
                // G3/LitBlend: two layers, detail maps.
                Add(mat, "_BaseMap", diffuse, false);
                if (!single) Add(mat, "_BaseMap2", WorldImage(res.Diffuse2?.Image), false);
                bool normalShared = res.Normal != null && res.Normal2 == null;
                Add(mat, "_BumpMap", normal, true);
                Add(mat, "_BumpMap2", res.Normal2 != null ? WorldImage(res.Normal2.Image) : normalShared ? normal : null, true);
                Add(mat, "_DetailMap", detailA ?? detailB, false);
                if (detailA != null && detailB != null && !string.Equals(detailA, detailB, StringComparison.OrdinalIgnoreCase))
                    Add(mat, "_DetailMap2", detailB, false);
            }
            else
            {
                Add(mat, "_BaseMap", diffuse, false);
                Add(mat, "_BumpMap", normal, true);
                var mask = res.SpecularMask;
                if (res.EnableSpecular && mask?.Map != null && mask.MapComponent != ColorComponent.A)
                    Add(mat, "_SpecGlossMap", WorldImage(mask.Map.Image), false);
            }
            return _world[stem] = mat;
        }

        SourceMaterial Fallback() => new SourceMaterial { Name = FallbackName };

        static void Add(SourceMaterial m, string prop, string image, bool normal)
        {
            if (image != null) m.Textures.Add(new SourceTexture { Property = prop, Image = image, Normal = normal });
        }

        /// <summary>
        /// MaterialSwitch n: every texture <c>…_S&lt;k&gt;</c> (or <c>…_NN</c>) is replaced by <c>…_S&lt;n&gt;</c> when that image
        /// exists; a material with a replaced texture is a copy named <c>&lt;name&gt;_S&lt;n&gt;</c>. 0 = as authored.
        /// </summary>
        public SourceMaterial Variant(SourceMaterial m, int sw)
        {
            if (m == null || sw == 0) return m;
            string key = m.Name + "_S" + sw;
            if (_variants.TryGetValue(key, out var v)) return v;
            SourceMaterial copy = null;
            foreach (var t in m.Textures)
            {
                var target = ResolveImage(t.Image, sw);
                if (target == null || string.Equals(target, t.Image, StringComparison.OrdinalIgnoreCase)) continue;
                copy ??= Copy(m, key);
                copy.Textures.First(x => x.Property == t.Property).Image = target;
            }
            return _variants[key] = copy ?? m;
        }

        static SourceMaterial Copy(SourceMaterial m, string name)
        {
            var c = new SourceMaterial { Name = name };
            foreach (var t in m.Textures) c.Textures.Add(new SourceTexture { Property = t.Property, Image = t.Image, Normal = t.Normal });
            return c;
        }
    }
}
