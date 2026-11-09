using System;

namespace G3.Weather
{
    /// <summary>
    /// fix-249 (owner: the Bakaresh wall fountain basin had no water): the look of small still water bodies — the water
    /// surfaces of props (G3_Object_Interact_Fountain_01: 1.5 x 1.1 m, G3_Object_Interact_Barrel_01: 0.8 m; both use
    /// G3_Nature_Water_River_01_A, which the five big Myrtana lake meshes share). G3 draws that material opaque
    /// (eCShaderDefault, BlendMode 0: the water diffuse x the skydome). The port's G3/Water shader fades the surface out
    /// over its depth (<c>alpha = depth / _ShoreFade</c>, 0.5 m for this material) and absorbs per metre: over a basin a few
    /// cm deep both are ~0 and the surface showed the stone under it (it read as a dry, sand-coloured basin).
    /// A small body therefore gets a material variant with a near-zero shore fade, a strong absorption (a few cm read as
    /// water), world-space normals tiled to its size and a slow still-water drift. Only float/vector/colour values change:
    /// the shader keywords (and so the compiled variants) stay those of the source material.
    /// </summary>
    public static class SmallWaterLook
    {
        /// <summary>A water renderer whose horizontal bounds are at most this large (m) is a small body (basins, barrels,
        /// troughs). The smallest landscape water mesh in G3 (Ishtar river piece) is 9.8 m.</summary>
        public const float MaxExtent = 4f;

        /// <summary>Depth over which the surface fades in at its edge (m): only the contact line with the basin wall.</summary>
        public const float ShoreFade = 0.015f;

        /// <summary>Absorption per metre (RGB): a 5 cm column keeps ~50 / 64 / 67 % of the floor's colour.</summary>
        public static readonly float[] Absorption = { 14f, 9f, 8f };

        /// <summary>Scatter (body) colour, a bit lighter than the river's so a shallow basin reads as clear water.</summary>
        public static readonly float[] ScatterColor = { 0.05f, 0.11f, 0.1f };

        /// <summary>World-space ripple tiles (m per tile) and their slow drift (xy direction, z speed).</summary>
        public const float TilingA = 0.7f, TilingB = 0.4f, NormalStrength = 0.22f;
        public static readonly float[] FlowA = { 0.7f, 0.7f, 0.004f, 0f };
        public static readonly float[] FlowB = { -0.6f, 0.8f, 0.006f, 0f };

        /// <summary>True for a water renderer of <paramref name="sizeX"/> x <paramref name="sizeZ"/> m (world bounds).</summary>
        public static bool IsSmallBody(float sizeX, float sizeZ) =>
            !float.IsNaN(sizeX) && !float.IsNaN(sizeZ) && sizeX > 0f && sizeZ > 0f && Math.Max(sizeX, sizeZ) <= MaxExtent;

        /// <summary>Surface opacity of the variant over <paramref name="depth"/> m of water (the shader's shore fade).</summary>
        public static float Alpha(float depth) => Math.Min(1f, Math.Max(0f, depth / ShoreFade));

        /// <summary>Fraction of the floor colour (channel 0..2) seen through <paramref name="depth"/> m of the variant.</summary>
        public static float Transmittance(int channel, float depth) => (float)Math.Exp(-Absorption[channel] * Math.Max(0f, depth));
    }
}
