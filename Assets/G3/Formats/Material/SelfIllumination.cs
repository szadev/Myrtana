using System;

namespace G3.Formats.Material
{
    /// <summary>
    /// The SelfIllumination slot reduced to a map Genome adds unlit on top of the lit colour (fix-260). Supported graphs:
    /// <c>T</c>, <c>Mul(T, K)</c> and <c>Blend(Glow, Base, Weight.channel)</c> = lerp(Glow, Base, w): Base where w = 1
    /// (eCColorSrcBlend, Docs/formats-mesh-material-texture.md §4.6). The fire golem's lava veins are the last kind:
    /// Blend(G3_Monster_FireGolem_Lava_A1, G3_Monster_Golem_Diffuse_S1, G3_Monster_Golem_Diffuse_S1.a). Graphs with a
    /// cube/skydome sampler are environment reflections, not glow, and give no recipe; so do animated tex-coords.
    /// </summary>
    public sealed class SelfIlluminationMap
    {
        public TextureSlot Glow;
        /// <summary>Second layer of a Blend (null = Glow only).</summary>
        public TextureSlot Base;
        /// <summary>Sampler whose <see cref="WeightChannel"/> drives the blend; null when <see cref="Base"/> is null.</summary>
        public TextureSlot Weight;
        public ColorComponent WeightChannel = ColorComponent.A;
        /// <summary>Constant multiplied in (gamma value).</summary>
        public float[] Color = { 1, 1, 1 };

        public bool IsComposite => Base != null || Color[0] != 1 || Color[1] != 1 || Color[2] != 1;

        /// <summary>Stem of the baked texture: the glow image itself when nothing has to be combined.</summary>
        public string BakedStem
        {
            get
            {
                if (!IsComposite) return Glow.Image;
                string s = Glow.Image;
                if (Base != null) s += "_x_" + Base.Image + "_by_" + Weight.Image + "_" + WeightChannel.ToString().ToLowerInvariant();
                if (Color[0] != 1 || Color[1] != 1 || Color[2] != 1) s += $"_k{(int)(Color[0] * 255)}_{(int)(Color[1] * 255)}_{(int)(Color[2] * 255)}";
                return s + "_SelfIllum";
            }
        }

        /// <summary>
        /// The glow a material gets as unlit emission, or null. Applied to actor materials (G3_Animation_*: creatures,
        /// bodies) in the opaque/masked Simple Lit path; of those only the fire golem has a glow graph, the rest are
        /// reflections (cube sampler). World materials (snow ground "T(snow specular)", the magic barrier with its own
        /// shader) are left to a world-look pass. The emission map shares the base map's UV transform in URP, so a glow
        /// map at another tiling than the diffuse is only accepted when baked (the bake applies each input's scale).
        /// </summary>
        public static SelfIlluminationMap ForUnlitEmission(MaterialResource m)
        {
            var e = m?.Emission;
            if (e == null || m.Name == null || !m.Name.StartsWith("G3_Animation_", StringComparison.OrdinalIgnoreCase)) return null;
            if (m.Blend != BlendMode.Normal && m.Blend != BlendMode.Masked) return null;
            if (m.VertexAlphaBlend) return null;
            float su = m.Diffuse?.ScaleU ?? 1, sv = m.Diffuse?.ScaleV ?? 1;
            if (e.IsComposite ? su != 1 || sv != 1 : e.Glow.ScaleU != su || e.Glow.ScaleV != sv) return null;
            return e;
        }

        /// <summary>The slot a texture loader gets: the glow map itself, or a synthetic slot (Bake = this) to be baked.</summary>
        public TextureSlot Slot => IsComposite ? new TextureSlot { Image = BakedStem, Bake = this } : Glow;

        /// <summary>Baked size: the larger input, at most <paramref name="max"/> per side.</summary>
        public static int BakeSize(int glow, int baseSize, int max = 1024) => Math.Min(max, Math.Max(glow, baseSize));

        /// <summary>
        /// Combines decoded RGBA32 images (gamma values, top row first, as <see cref="Image.Dxt.DecodeRgba"/>) into the
        /// emission map at <paramref name="width"/>×<paramref name="height"/>: every input is sampled bilinearly (repeat
        /// wrap, its own UV scale) at the same texel centre, as the shader samples them at one UV. Alpha = 255.
        /// </summary>
        public byte[] Bake(Rgba glow, Rgba baseImg, Rgba weight, int width, int height)
        {
            if (glow == null) throw new ArgumentNullException(nameof(glow));
            if (Base != null && (baseImg == null || weight == null)) throw new ArgumentException("blend needs base and weight images");
            int wc = WeightChannel switch { ColorComponent.R => 0, ColorComponent.G => 1, ColorComponent.B => 2, _ => 3 };
            var dst = new byte[width * height * 4];
            Span<float> g = stackalloc float[4], b = stackalloc float[4], w = stackalloc float[4];
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                float u = (x + 0.5f) / width, v = (y + 0.5f) / height;
                glow.Sample(u * Glow.ScaleU, v * Glow.ScaleV, g);
                float t = 0;
                if (Base != null)
                {
                    baseImg.Sample(u * Base.ScaleU, v * Base.ScaleV, b);
                    weight.Sample(u * Weight.ScaleU, v * Weight.ScaleV, w);
                    t = w[wc] / 255f;
                }
                int o = (y * width + x) * 4;
                for (int k = 0; k < 3; k++)
                {
                    float c = Base != null ? g[k] + (b[k] - g[k]) * t : g[k];
                    dst[o + k] = (byte)Math.Clamp((int)(c * Color[k] + 0.5f), 0, 255);
                }
                dst[o + 3] = 255;
            }
            return dst;
        }

        /// <summary>A decoded RGBA32 image (rows top first).</summary>
        public sealed class Rgba
        {
            public readonly byte[] Pixels;
            public readonly int Width, Height;
            public Rgba(byte[] pixels, int width, int height)
            {
                if (pixels == null || pixels.Length < width * height * 4) throw new ArgumentException("pixel buffer too small");
                Pixels = pixels; Width = width; Height = height;
            }

            /// <summary>Bilinear, repeat wrap; uv in texture units (0..1 = one tile).</summary>
            public void Sample(float u, float v, Span<float> rgba)
            {
                float fx = u * Width - 0.5f, fy = v * Height - 0.5f;
                int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
                float ax = fx - x0, ay = fy - y0;
                int Wrap(int i, int n) => ((i % n) + n) % n;
                int xa = Wrap(x0, Width), xb = Wrap(x0 + 1, Width), ya = Wrap(y0, Height), yb = Wrap(y0 + 1, Height);
                for (int k = 0; k < 4; k++)
                {
                    float p00 = Pixels[(ya * Width + xa) * 4 + k], p10 = Pixels[(ya * Width + xb) * 4 + k];
                    float p01 = Pixels[(yb * Width + xa) * 4 + k], p11 = Pixels[(yb * Width + xb) * 4 + k];
                    rgba[k] = (p00 + (p10 - p00) * ax) * (1 - ay) + (p01 + (p11 - p01) * ax) * ay;
                }
            }
        }
    }
}
