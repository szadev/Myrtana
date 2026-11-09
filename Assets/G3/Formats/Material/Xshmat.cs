using System;
using System.Collections.Generic;
using System.IO;
using G3.Formats.Genome;

namespace G3.Formats.Material
{
    public enum BlendMode
    {
        Normal = 0, Masked = 1, AlphaBlend = 2, Modulate = 3, AlphaModulate = 4,
        Translucent = 5, Darken = 6, Brighten = 7, Invisible = 8,
    }

    public sealed class TextureSlot
    {
        public string Image;       // image stem, e.g. "G3_Myrtana_Faring_Wall_01_Diffuse_S1" (look up "<stem>.ximg")
        public bool ClampU, ClampV;
        public float ScaleU = 1, ScaleV = 1;   // from eCTexCoordSrcScale (UV multiplier)
        /// <summary>Set on a synthetic slot whose pixels are baked from several images (Image = the baked stem, fix-260).</summary>
        public SelfIlluminationMap Bake;
    }

    /// <summary>
    /// eEColorSrcComponent: the u32 in front of every ColorSrcProxy. Order taken from Engine.dll's swizzle string table
    /// ("", ".rgb", ".r", ".g", ".b", ".a"); SpecularPower proxies always use A on a constant (0.055..0.8) [V-all].
    /// </summary>
    public enum ColorComponent { Default = 0, Rgb = 1, R = 2, G = 3, B = 4, A = 5 }

    /// <summary>
    /// The Specular slot reduced to "map · colour" (most shipped graphs are Mul(sampler, constant)).
    /// Genome multiplies its Phong term by this mask (ge_default.fx: colSpecular *= XFX_PS_SPECULAR).
    /// </summary>
    public sealed class SpecularMask
    {
        public TextureSlot Map;                          // null = constant mask
        public ColorComponent MapComponent;
        public float[] Color = { 1, 1, 1 };              // product of the constants on the path
        /// <summary>Further maps multiplied in (e.g. a dirt map); a single-map mask can only carry their mean.</summary>
        public List<TextureSlot> ExtraMaps = new List<TextureSlot>();
    }

    public sealed class MaterialResource
    {
        public string Name;
        public string ShaderClass;
        public BlendMode Blend;
        public int MaskReference;          // alpha-test reference 0..255
        public TextureSlot Diffuse, Normal, Specular, Opacity;
        /// <summary>
        /// Map of the SelfIllumination slot (emissive, unaffected by light); null when the slot is unset or not a sampler.
        /// Used as-is by the magic barrier (G3_Special_Magic_Barrier_05, fix-229); glow graphs: see <see cref="Emission"/>.
        /// </summary>
        public TextureSlot SelfIllumination;
        /// <summary>The SelfIllumination slot as an unlit glow recipe (fix-260: fire golem lava); null when the graph is
        /// unset or not one of the supported glow forms (reflections, animated tex-coords).</summary>
        public SelfIlluminationMap Emission;
        /// <summary>
        /// Second layer when a slot is an eCColorSrcBlend(A, B, vertex colour): result = A·α + B·(1−α),
        /// α = vertex diffuse alpha (D3DTOP_BLENDDIFFUSEALPHA). Used by landscape and transition materials.
        /// </summary>
        public TextureSlot Diffuse2, Normal2;
        /// <summary>
        /// Second map multiplied into a diffuse layer: the layer is eCColorSrcCombiner Mul(base sampler, detail sampler),
        /// which Genome compiles to a plain "( base * detail )" (no 2x, no distance fade). On the landscape this is mostly
        /// G3_Misc_Detailmap_01 at Scale 0.025 (40 m macro variation, mean 0.78); on meshes a dirt map or a second
        /// stone map. DiffuseDetail pairs with Diffuse, DiffuseDetail2 with Diffuse2 (Docs/formats-mesh-material-texture.md §4.7).
        /// </summary>
        public TextureSlot DiffuseDetail, DiffuseDetail2;
        /// <summary>rgb constant multiplied into a single-layer diffuse (Mul(sampler, constant)), gamma-space value.</summary>
        public float[] DiffuseMulColor;
        public bool VertexAlphaBlend;
        public float[] DiffuseColor;       // rgb when the diffuse slot is a constant
        public bool EnableSpecular;
        /// <summary>Specular mask; null = slot unset (Genome then applies the Phong term unmasked).</summary>
        public SpecularMask SpecularMask;
        /// <summary>Second layer of a vertex-colour blended specular slot (pairs with Diffuse2).</summary>
        public SpecularMask SpecularMask2;
        /// <summary>XFX_PS_SPECULARPOWER (0..1); Phong exponent = 128·p. Genome default 0.5.</summary>
        public float SpecularPower = 0.5f;
        /// <summary>eCResourceShaderMaterial_PS.PhysicMaterial (eEShapeMaterial: 1 Wood, 4 Stone, 5 Earth, 15 Grass, 20 Sand …);
        /// the floor material for footsteps on render-mesh collision. -1 when the property is missing.</summary>
        public int PhysicMaterial = -1;
        /// <summary>Slot → readable shader-graph expression; only filled when Xshmat.DescribeGraphs is set.</summary>
        public Dictionary<string, string> Graph;
    }

    /// <summary>.xshmat — eCResourceShaderMaterial_PS with a nested shader (Docs/formats-mesh-material-texture.md §4).</summary>
    public static class Xshmat
    {
        // Slot order of the ColorSrcProxy list per shader class (§4.2).
        static readonly Dictionary<string, string[]> ProxyLayouts = new Dictionary<string, string[]>
        {
            ["eCShaderDefault"] = new[] { "Diffuse", "Opacity", "SelfIllumination", "Specular", "SpecularPower", "Normal", "+Distortion" },
            ["eCShaderSkin"] = new[] { "Diffuse", "Opacity", "SelfIllumination", "Specular", "SpecularPower", "Normal", "SubSurface" },
            ["eCShaderLeaf"] = new[] { "Diffuse", "Specular", "SpecularPower", "Normal" },
            ["eCShaderWater"] = new[] { "Diffuse", "StaticBump", "FlowingBump", "Specular", "SpecularPower", "Reflection", "+Distortion" },
            ["eCShaderParticle"] = new[] { "Diffuse", "+Distortion" },
        };

        sealed class Element
        {
            public string Class;
            public PropertyObject Obj;
            public List<Guid20> Inputs = new List<Guid20>();
            public List<ColorComponent> Components = new List<ColorComponent>();
            public Guid20 TexCoord;      // samplers: TexCoordSrcProxy
        }

        public static MaterialResource Read(byte[] data, string name = null)
        {
            var g = new GenomeReader(data);
            var mat = g.ReadObject();
            if (mat == null || mat.ClassName != "eCResourceShaderMaterial_PS")
                throw new InvalidDataException($"{name}: unexpected class {mat?.ClassName}");
            g.ReadResourceTail();

            var result = new MaterialResource { Name = name };
            result.PhysicMaterial = mat.Get("PhysicMaterial")?.AsEnum() ?? -1;
            var shader = g.ReadObject();
            if (shader == null) return result;
            result.ShaderClass = shader.ClassName;
            result.Blend = (BlendMode)(shader.Get("BlendMode")?.AsEnum() ?? 0);
            result.MaskReference = shader.Get("MaskReference")?.AsInt() ?? 0;
            result.EnableSpecular = shader.Get("EnableSpecular")?.AsBool() ?? false;

            if (!ProxyLayouts.TryGetValue(shader.ClassName, out var layout))
                return result; // River/Ocean/LightStreaks: unknown layout, caller uses a fallback

            var slots = new Dictionary<string, Guid20>();
            var slotComponents = new Dictionary<string, ColorComponent>();
            foreach (var slot in layout)
            {
                bool optional = slot[0] == '+';
                if (optional && shader.ClassVersion <= 1) continue;
                string key = optional ? slot.Substring(1) : slot;
                slotComponents[key] = (ColorComponent)g.R.ReadUInt32();
                slots[key] = g.Guid();
            }
            g.R.ReadUInt16(); g.R.ReadUInt16();
            g.Guid();       // shader token
            g.Skip(16);     // editor rect

            var elements = new Dictionary<Guid20, Element>();
            int n = g.R.ReadInt32();
            for (int i = 0; i < n; i++)
            {
                var obj = g.ReadObject();
                if (obj == null) continue;
                var el = new Element { Class = obj.ClassName, Obj = obj };
                int proxies;
                switch (obj.ClassName)
                {
                    case "eCColorSrcSampler": proxies = -1; break;
                    case "eCColorSrcCombiner": proxies = 2; break;
                    case "eCColorSrcBlend": proxies = 3; break;
                    case "eCColorSrcConstant":
                    case "eCColorSrcVertexColor": proxies = 0; break;
                    default:
                        // Unknown layout (eCTexCoordSrc*, cube samplers...): the common tail
                        // (u16, u16, bCGuid token, bCRect) always ends the object, so the token is at End-36.
                        if (obj.End - g.Position >= 36)
                        {
                            g.Position = obj.End - 36;
                            elements[g.Guid()] = el;
                        }
                        g.Position = obj.End;
                        continue;
                }
                if (proxies < 0)
                {
                    g.R.ReadUInt32();
                    el.TexCoord = g.Guid();
                    g.R.ReadUInt32();   // sampler type
                }
                else
                {
                    for (int k = 0; k < proxies; k++)
                    {
                        el.Components.Add((ColorComponent)g.R.ReadUInt32());
                        el.Inputs.Add(g.Guid());
                    }
                }
                g.R.ReadUInt16(); g.R.ReadUInt16();
                var token = g.Guid();
                elements[token] = el;
                g.Position = obj.End;
            }

            if (DescribeGraphs)
            {
                result.Graph = new Dictionary<string, string>();
                foreach (var kv in slots)
                    if (kv.Value.Valid) result.Graph[kv.Key] = Describe(g, elements, kv.Value, slotComponents[kv.Key], 0);
            }
            ResolveLayered(g, elements, slots, "Diffuse", out result.Diffuse, out result.Diffuse2, ref result.VertexAlphaBlend);
            if (slots.TryGetValue("Diffuse", out var dg) && dg.Valid)
            {
                var vb = result.VertexAlphaBlend ? FindVertexBlend(elements, dg, 0) : null;
                if (vb != null)
                {
                    Modulation(g, elements, vb.Inputs[0], out result.DiffuseDetail, out _);
                    Modulation(g, elements, vb.Inputs[1], out result.DiffuseDetail2, out _);
                }
                else Modulation(g, elements, dg, out result.DiffuseDetail, out result.DiffuseMulColor);
            }
            ResolveLayered(g, elements, slots, "Normal", out result.Normal, out result.Normal2, ref result.VertexAlphaBlend);
            result.Specular = Resolve(g, elements, slots, "Specular", 0);
            if (slots.TryGetValue("Specular", out var sg) && sg.Valid && elements.TryGetValue(sg, out var se))
            {
                if (se.Class == "eCColorSrcBlend" && se.Inputs.Count == 3 &&
                    elements.TryGetValue(se.Inputs[2], out var w) && w.Class == "eCColorSrcVertexColor")
                {
                    result.SpecularMask = Mask(g, elements, se.Inputs[0], se.Components[0], 1);
                    result.SpecularMask2 = Mask(g, elements, se.Inputs[1], se.Components[1], 1);
                }
                else result.SpecularMask = Mask(g, elements, sg, slotComponents["Specular"], 0);
            }
            if (slots.TryGetValue("SpecularPower", out var pg) && pg.Valid &&
                elements.TryGetValue(pg, out var pe) && pe.Class == "eCColorSrcConstant")
            {
                var k = Constant(pe, slotComponents["SpecularPower"]);
                if (k != null) result.SpecularPower = k[0];
            }
            result.Opacity = Resolve(g, elements, slots, "Opacity", 0);
            result.SelfIllumination = Resolve(g, elements, slots, "SelfIllumination", 0);
            if (slots.TryGetValue("SelfIllumination", out var eg) && eg.Valid)
                result.Emission = Glow(g, elements, eg, slotComponents["SelfIllumination"]);
            if (result.Diffuse == null && slots.TryGetValue("Diffuse", out var d) && d.Valid
                && elements.TryGetValue(d, out var c) && c.Class == "eCColorSrcConstant")
            {
                var col = c.Obj.Get("Color");
                if (col != null && col.Value.Length >= 16)
                    result.DiffuseColor = new[]
                    {
                        BitConverter.ToSingle(col.Value, 4), BitConverter.ToSingle(col.Value, 8), BitConverter.ToSingle(col.Value, 12),
                    };
            }
            return result;
        }

        static TextureSlot Resolve(GenomeReader g, Dictionary<Guid20, Element> elements, Dictionary<string, Guid20> slots, string slot, int depth)
        {
            if (!slots.TryGetValue(slot, out var guid)) return null;
            return Follow(g, elements, guid, depth);
        }

        /// <summary>
        /// Like Resolve, but if the slot leads (through multiply-combiners) to a blend driven by vertex colour,
        /// return both blended layers.
        /// </summary>
        static void ResolveLayered(GenomeReader g, Dictionary<Guid20, Element> elements, Dictionary<string, Guid20> slots,
                                   string slot, out TextureSlot a, out TextureSlot b, ref bool vertexBlend)
        {
            a = b = null;
            if (!slots.TryGetValue(slot, out var guid)) return;
            var blend = FindVertexBlend(elements, guid, 0);
            if (blend != null)
            {
                a = Follow(g, elements, blend.Inputs[0], 1);
                b = Follow(g, elements, blend.Inputs[1], 1);
                if (a != null && b != null) { vertexBlend = true; return; }
            }
            a = Follow(g, elements, guid, 0);
            b = null;
        }

        /// <summary>
        /// Layer = Mul(sampler, X): X a sampler read as rgb → detail map; X a constant → colour multiplier.
        /// Other graphs (Add/Min/…, alpha-channel factors, deeper nesting) give nothing.
        /// </summary>
        static void Modulation(GenomeReader g, Dictionary<Guid20, Element> elements, Guid20 guid, out TextureSlot detail, out float[] color)
        {
            detail = null; color = null;
            if (!guid.Valid || !elements.TryGetValue(guid, out var el) || el.Class != "eCColorSrcCombiner") return;
            if ((el.Obj.Get("CombinerType")?.AsEnum() ?? 2) != 2 || el.Inputs.Count != 2) return;
            if (!elements.TryGetValue(el.Inputs[0], out var a) || a.Class != "eCColorSrcSampler") return;
            if (!elements.TryGetValue(el.Inputs[1], out var b)) return;
            var comp = el.Components[1];
            if (b.Class == "eCColorSrcSampler" && (comp == ColorComponent.Default || comp == ColorComponent.Rgb))
                detail = Follow(g, elements, el.Inputs[1], 1);
            else if (b.Class == "eCColorSrcConstant")
                color = Constant(b, comp);
        }

        static Element FindVertexBlend(Dictionary<Guid20, Element> elements, Guid20 guid, int depth)
        {
            if (!guid.Valid || depth > 8 || !elements.TryGetValue(guid, out var el)) return null;
            if (el.Class == "eCColorSrcBlend" && el.Inputs.Count == 3 &&
                elements.TryGetValue(el.Inputs[2], out var w) && w.Class == "eCColorSrcVertexColor")
                return el;
            if (el.Class == "eCColorSrcCombiner")
                foreach (var input in el.Inputs)
                {
                    var found = FindVertexBlend(elements, input, depth + 1);
                    if (found != null) return found;
                }
            return null;
        }

        static TextureSlot Follow(GenomeReader g, Dictionary<Guid20, Element> elements, Guid20 guid, int depth)
        {
            if (!guid.Valid || depth > 8 || !elements.TryGetValue(guid, out var el)) return null;
            if (el.Class == "eCColorSrcSampler")
            {
                var path = el.Obj.Get("ImageFilePath");
                if (path == null) return null;
                var stem = ImageStem(g.StrFrom(path.Value));
                if (string.IsNullOrEmpty(stem)) return null;
                var slot = new TextureSlot
                {
                    Image = stem,
                    ClampU = (el.Obj.Get("TexRepeatU")?.AsEnum() ?? 0) == 1,
                    ClampV = (el.Obj.Get("TexRepeatV")?.AsEnum() ?? 0) == 1,
                };
                if (el.TexCoord.Valid && elements.TryGetValue(el.TexCoord, out var tc) && tc.Class == "eCTexCoordSrcScale")
                {
                    var s = tc.Obj.Get("Scale");
                    if (s != null && s.Value.Length >= 8)
                    {
                        slot.ScaleU = BitConverter.ToSingle(s.Value, 0);
                        slot.ScaleV = BitConverter.ToSingle(s.Value, 4);
                    }
                }
                return slot;
            }
            foreach (var input in el.Inputs)
            {
                var t = Follow(g, elements, input, depth + 1);
                if (t != null) return t;
            }
            return null;
        }

        /// <summary>A static sampler (no scroller/rotator tex-coords) read as <paramref name="comp"/> = rgb; else null.</summary>
        static TextureSlot StaticSampler(GenomeReader g, Dictionary<Guid20, Element> elements, Guid20 guid, ColorComponent comp)
        {
            if (!guid.Valid || !elements.TryGetValue(guid, out var el) || el.Class != "eCColorSrcSampler") return null;
            if (comp != ColorComponent.Default && comp != ColorComponent.Rgb) return null;
            if (el.TexCoord.Valid && elements.TryGetValue(el.TexCoord, out var tc) && tc.Class != "eCTexCoordSrcScale") return null;
            return Follow(g, elements, guid, 0);
        }

        /// <summary>SelfIllumination graph → <see cref="SelfIlluminationMap"/>: T, Mul(T, K)/Mul(K, T), Blend(T, T, T.channel).</summary>
        static SelfIlluminationMap Glow(GenomeReader g, Dictionary<Guid20, Element> elements, Guid20 guid, ColorComponent comp)
        {
            if (!elements.TryGetValue(guid, out var el)) return null;
            if (el.Class == "eCColorSrcSampler")
            {
                var t = StaticSampler(g, elements, guid, comp);
                return t != null ? new SelfIlluminationMap { Glow = t } : null;
            }
            if (comp != ColorComponent.Default && comp != ColorComponent.Rgb) return null;
            if (el.Class == "eCColorSrcBlend" && el.Inputs.Count == 3)
            {
                var a = StaticSampler(g, elements, el.Inputs[0], el.Components[0]);
                var b = StaticSampler(g, elements, el.Inputs[1], el.Components[1]);
                var wc = el.Components[2];
                if (a == null || b == null || wc == ColorComponent.Default || wc == ColorComponent.Rgb) return null;
                if (!elements.TryGetValue(el.Inputs[2], out var we) || we.Class != "eCColorSrcSampler") return null;   // VC.a: per-vertex
                if (we.TexCoord.Valid && elements.TryGetValue(we.TexCoord, out var wtc) && wtc.Class != "eCTexCoordSrcScale") return null;
                var w = Follow(g, elements, el.Inputs[2], 0);
                return w != null ? new SelfIlluminationMap { Glow = a, Base = b, Weight = w, WeightChannel = wc } : null;
            }
            if (el.Class == "eCColorSrcCombiner" && el.Inputs.Count == 2 && (el.Obj.Get("CombinerType")?.AsEnum() ?? 2) == 2)
            {
                for (int i = 0; i < 2; i++)
                {
                    var t = StaticSampler(g, elements, el.Inputs[i], el.Components[i]);
                    if (t == null || !elements.TryGetValue(el.Inputs[1 - i], out var k) || k.Class != "eCColorSrcConstant") continue;
                    var col = Constant(k, el.Components[1 - i]);
                    return col != null ? new SelfIlluminationMap { Glow = t, Color = col } : null;
                }
            }
            return null;
        }

        /// <summary>
        /// Reduces a specular sub-graph to map · colour: a sampler gives the map, constants multiply the colour,
        /// combiners keep the first map found (Add/Max/Min/Sub add their constant side instead), a blend takes its
        /// first layer. Cube/skydome samplers count as 1.
        /// </summary>
        static SpecularMask Mask(GenomeReader g, Dictionary<Guid20, Element> elements, Guid20 guid, ColorComponent comp, int depth)
        {
            if (!guid.Valid || depth > 8 || !elements.TryGetValue(guid, out var el)) return null;
            switch (el.Class)
            {
                case "eCColorSrcSampler":
                {
                    var slot = Follow(g, elements, guid, depth);
                    return slot != null ? new SpecularMask { Map = slot, MapComponent = comp } : null;
                }
                case "eCColorSrcConstant":
                {
                    var k = Constant(el, comp);
                    return k != null ? new SpecularMask { Color = k } : null;
                }
                case "eCColorSrcCombiner":
                case "eCColorSrcBlend":
                {
                    bool blend = el.Class == "eCColorSrcBlend";
                    bool multiply = blend || (el.Obj.Get("CombinerType")?.AsEnum() ?? 2) == 2;
                    int n = blend ? 1 : Math.Min(el.Inputs.Count, el.Components.Count);
                    SpecularMask result = null;
                    for (int i = 0; i < n; i++)
                    {
                        var m = Mask(g, elements, el.Inputs[i], el.Components[i], depth + 1);
                        if (m == null) continue;
                        if (result == null) { result = m; continue; }
                        if (result.Map == null) { result.Map = m.Map; result.MapComponent = m.MapComponent; }
                        else if (multiply && m.Map != null) result.ExtraMaps.Add(m.Map);
                        if (multiply) result.ExtraMaps.AddRange(m.ExtraMaps);
                        for (int c = 0; c < 3; c++)
                            result.Color[c] = multiply ? result.Color[c] * m.Color[c]
                                                       : Math.Min(1f, result.Color[c] + (m.Map == null ? m.Color[c] : 0));
                    }
                    return result;
                }
                case "eCColorSrcVertexColor":
                    return new SpecularMask();
                default:
                    return new SpecularMask();   // cube/skydome sampler (reflection colour): 1
            }
        }

        /// <summary>rgb of an eCColorSrcConstant, swizzled by the proxy component (R/G/B/A → that channel in all three).</summary>
        static float[] Constant(Element el, ColorComponent comp)
        {
            var col = el.Obj.Get("Color");
            if (col == null || col.Value.Length < 16) return null;
            float r = BitConverter.ToSingle(col.Value, 4), gr = BitConverter.ToSingle(col.Value, 8), b = BitConverter.ToSingle(col.Value, 12);
            float a = el.Obj.Get("Alpha")?.AsFloat() ?? 1f;
            switch (comp)
            {
                case ColorComponent.R: return new[] { r, r, r };
                case ColorComponent.G: return new[] { gr, gr, gr };
                case ColorComponent.B: return new[] { b, b, b };
                case ColorComponent.A: return new[] { a, a, a };
                default: return new[] { r, gr, b };
            }
        }

        /// <summary>Debug/survey switch: fill MaterialResource.Graph with a readable expression per slot.</summary>
        public static bool DescribeGraphs;

        static string Describe(GenomeReader g, Dictionary<Guid20, Element> elements, Guid20 guid, ColorComponent comp, int depth)
        {
            if (!guid.Valid) return "-";
            if (depth > 10 || !elements.TryGetValue(guid, out var el)) return "?";
            string sw = comp == ColorComponent.Default ? "" : "." + comp.ToString().ToLowerInvariant();
            switch (el.Class)
            {
                case "eCColorSrcSampler":
                {
                    var path = el.Obj.Get("ImageFilePath");
                    string tc = "";
                    if (el.TexCoord.Valid && elements.TryGetValue(el.TexCoord, out var t))
                    {
                        var s = t.Obj.Get("Scale");
                        tc = t.Class == "eCTexCoordSrcScale" && s != null && s.Value.Length >= 8
                            ? $"@{BitConverter.ToSingle(s.Value, 0):0.####}x{BitConverter.ToSingle(s.Value, 4):0.####}"
                            : "@" + t.Class;
                    }
                    return $"T({(path != null ? ImageStem(g.StrFrom(path.Value)) : "?")}{tc}){sw}";
                }
                case "eCColorSrcConstant":
                {
                    var k = Constant(el, comp);
                    return k == null ? "K?" : $"K({k[0]:0.##},{k[1]:0.##},{k[2]:0.##})";
                }
                case "eCColorSrcVertexColor": return "VC" + sw;
                case "eCColorSrcCombiner":
                case "eCColorSrcBlend":
                {
                    string op = el.Class == "eCColorSrcBlend" ? "Blend"
                        : new[] { "Add", "Sub", "Mul", "Max", "Min" }[Math.Max(0, Math.Min(4, el.Obj.Get("CombinerType")?.AsEnum() ?? 2))];
                    var parts = new List<string>();
                    for (int i = 0; i < el.Inputs.Count; i++)
                        parts.Add(Describe(g, elements, el.Inputs[i], el.Components[i], depth + 1));
                    return $"{op}({string.Join(", ", parts)}){sw}";
                }
                default: return el.Class + sw;
            }
        }

        /// <summary>"Data/Materials/Images/Foo_Diffuse.tga" → "Foo_Diffuse".</summary>
        public static string ImageStem(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            int slash = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
            var file = path.Substring(slash + 1);
            int dot = file.LastIndexOf('.');
            return dot > 0 ? file.Substring(0, dot) : file;
        }
    }
}
