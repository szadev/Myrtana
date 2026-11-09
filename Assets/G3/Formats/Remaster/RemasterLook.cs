using System;
using System.Collections.Generic;

namespace G3.Formats.Remaster
{
    /// <summary>
    /// In-engine look of the FACES generated materials (FACES v6, after editor-runner-29's first in-game A/B shots) - the
    /// pure parts the Unity material builder (G3RemasterMaterials) applies. The Blender renders that looked right used
    /// three soft area lights (key 55 W at ~1.2 m, raytraced soft shadows, AgX); in the game a noon sun, a 1.2 cm/texel
    /// shadow-map cascade (PC_RPAsset: 2048 atlas, 4 cascades, 50 m) and SSAO with a 0.3 m radius light the same textures.
    /// </summary>
    public static class RemasterEyeLook
    {
        /// <summary>
        /// Linear luminance the bright (unshadowed) sclera should reach: the 75th percentile of G3's own eye texture
        /// (G3_Animation_Head_Eyes_01: 0.200). eyegen.py writes the sclera at 0.40 sRGB and bakes the lid, lash and
        /// lower-lid shadows into it (p75 0.068 linear = 3x darker than G3's eyes): fine under the Blender key light, a dark
        /// socket under the game's ambient light.
        /// </summary>
        public const float ScleraTarget = 0.20f;
        public const float MaxGain = 3.5f;

        /// <summary>Main-light shadow sampled this far (m) in front of the eyeball along its normal: at the lid surface.
        /// The shadow map (~1.2 cm/texel) cannot resolve the ~1 cm lid gap, so the eyeball took the full shadow of brow,
        /// lids and lash cards; the lid shadow is already painted into the eye texture.</summary>
        public const float ShadowSampleOffset = 0.025f;

        /// <summary>Share of SSAO kept on the eyeball (the 0.3 m SSAO radius turns a whole socket into one occluded pit).</summary>
        public const float SsaoStrength = 0.3f;

        /// <summary>Sclera ring of a generated eye texture, in eyeball radii (eyegen: iris 0.53, limbal ring up to ~0.6).</summary>
        public const float RingInner = 0.6f, RingOuter = 0.85f;

        public static float SrgbToLinear(float c) => c <= 0.04045f ? c / 12.92f : (float)Math.Pow((c + 0.055f) / 1.055f, 2.4f);

        public static float Luminance(byte r, byte g, byte b) =>
            0.2126f * SrgbToLinear(r / 255f) + 0.7152f * SrgbToLinear(g / 255f) + 0.0722f * SrgbToLinear(b / 255f);

        /// <summary>
        /// 75th percentile of the linear luminance in the sclera ring of an eye texture (RGBA, 4 bytes per texel; the
        /// texture is a front projection of the eyeball, centre = pupil, so row order does not matter). -1 when empty.
        /// </summary>
        public static float ScleraLuminance(byte[] rgba, int width, int height)
        {
            if (rgba == null || width <= 0 || height <= 0 || rgba.Length < width * height * 4) return -1;
            var v = new List<float>();
            int step = Math.Max(1, Math.Min(width, height) / 256); // a 256 px grid is plenty for a percentile
            for (int y = 0; y < height; y += step)
                for (int x = 0; x < width; x += step)
                {
                    float u = (x + 0.5f) / width * 2 - 1, w = (y + 0.5f) / height * 2 - 1;
                    float r = (float)Math.Sqrt(u * u + w * w);
                    if (r < RingInner || r >= RingOuter) continue;
                    int i = (y * width + x) * 4;
                    v.Add(Luminance(rgba[i], rgba[i + 1], rgba[i + 2]));
                }
            return Percentile(v, 0.75f);
        }

        /// <summary>Albedo multiplier that brings the sclera to <see cref="ScleraTarget"/>; never darkens, at most <see cref="MaxGain"/>.</summary>
        public static float AlbedoGain(float scleraLuminance)
        {
            if (scleraLuminance <= 0) return scleraLuminance < 0 ? 1f : MaxGain; // unknown: unchanged; black: the most
            return Math.Max(1f, Math.Min(MaxGain, ScleraTarget / scleraLuminance));
        }

        internal static float Percentile(List<float> v, float p)
        {
            if (v.Count == 0) return -1;
            v.Sort();
            return v[Math.Min(v.Count - 1, (int)(p * (v.Count - 1) + 0.5f))];
        }
    }

    /// <summary>Hair cards and the cap under them (hairmesh.py / mhface.py sets).</summary>
    public static class RemasterHairLook
    {
        /// <summary>The cap should be at least this share of the cards' median linear luminance. hairmesh.py darkens the
        /// cap (original x 0.7 x 0.85; the hero's MakeHuman cap is 0.05 sRGB vs cards 0.15): through the card gaps at the
        /// hairline and under Milten's quiff it showed as dark streaks / a dark spot once the cards were lit by a high sun.
        /// fix-52 (editor-runner-30: the spot under Milten's quiff stayed at 0.5 / max x2.5): Milten's cap is 1/5 of its
        /// cards (0.010 vs 0.053 linear), so x2.5 left it at half their brightness, seen through the gaps under the quiff;
        /// now 0.8 of the cards, up to x4 (the Hair/Skin _AlbedoGain range).</summary>
        public const float CapToCards = 0.8f;
        public const float MaxCapGain = 4f;

        /// <summary>Cap cutoff when the set does not record one (hero MakeHuman caps: fully opaque).</summary>
        public const float DefaultCapCutoff = 0.01f;

        /// <summary>The cards set that belongs to a cap set (<c>…_HairCap</c> → <c>…_HairCards</c>), else null.</summary>
        public static string CardsSetOfCap(string capSet)
        {
            if (string.IsNullOrEmpty(capSet) || !capSet.EndsWith(RemasterGenerated.HairCapSuffix, StringComparison.Ordinal)) return null;
            return capSet.Substring(0, capSet.Length - RemasterGenerated.HairCapSuffix.Length) + RemasterGenerated.HairCardsSuffix;
        }

        /// <summary>Median linear luminance of the texels with alpha ≥ <paramref name="alphaMin"/> (RGBA bytes); -1 when none.</summary>
        public static float MedianLuminance(byte[] rgba, int width, int height, float alphaMin = 0.5f)
        {
            if (rgba == null || width <= 0 || height <= 0 || rgba.Length < width * height * 4) return -1;
            var v = new List<float>();
            int step = Math.Max(1, Math.Min(width, height) / 256);
            byte aMin = (byte)Math.Round(Math.Max(0, Math.Min(1, alphaMin)) * 255);
            for (int y = 0; y < height; y += step)
                for (int x = 0; x < width; x += step)
                {
                    int i = (y * width + x) * 4;
                    if (rgba[i + 3] < aMin) continue;
                    v.Add(RemasterEyeLook.Luminance(rgba[i], rgba[i + 1], rgba[i + 2]));
                }
            return RemasterEyeLook.Percentile(v, 0.5f);
        }

        /// <summary>Albedo gain of the cap: up to <see cref="CapToCards"/> of the cards, never darker, at most <see cref="MaxCapGain"/>.</summary>
        public static float CapGain(float capLuminance, float cardsLuminance)
        {
            if (capLuminance < 0 || cardsLuminance <= 0) return 1f;
            if (capLuminance == 0) return MaxCapGain;
            return Math.Max(1f, Math.Min(MaxCapGain, CapToCards * cardsLuminance / capLuminance));
        }

        /// <summary>The cap's alpha cutoff: the set's own <c>alpha_cut</c> (hairmesh caps: 0.5, like the game and the Blender
        /// renders) or <see cref="DefaultCapCutoff"/>.</summary>
        public static float CapCutoff(float? alphaCut) =>
            alphaCut.HasValue && alphaCut.Value > 0 && alphaCut.Value < 1 ? alphaCut.Value : DefaultCapCutoff;

        /// <summary>
        /// Whether a generated material casts shadow-map shadows. Alpha-tested cards (hair and lashes) do not: at
        /// ~1.2 cm/texel their strands become solid blocks (the hero's hairline band, the rectangle under Milten's quiff,
        /// lash cards shading the whole eye). The opaque cap still casts the hair's silhouette; the eyeball and cornea
        /// follow their shaders. v2: hair and stubble cards, the eye occlusion shell and the tear film neither; the v2
        /// eyeball does not either (it sits inside the lids, whose own shadow is enough). Skin and outfit surfaces do.
        /// </summary>
        public static bool CastsShadows(RemasterGeneratedKind kind) => kind switch
        {
            RemasterGeneratedKind.HairCards => false,
            RemasterGeneratedKind.HairV2 => false,
            RemasterGeneratedKind.Stubble => false,
            RemasterGeneratedKind.EyeOcclusion => false,
            RemasterGeneratedKind.TearFilm => false,
            RemasterGeneratedKind.EyeV2 => false,
            _ => true,
        };
    }

    /// <summary>
    /// G3/Remaster/Skin parameters. <see cref="Body"/> = the shader defaults (R1 bodies and G3 heads, tuned in r1c/r1d);
    /// <see cref="Face"/> = FACES heads (texture set with <c>face.json</c>, facetex.py), matched to the Blender look
    /// (facerender.texture_material 'face'): normal strength 0.6, no extra tiled pores (facetex already put them into
    /// BumpMap_R2), one GGX lobe at smoothness ≈ 0.8 x MaskMap_R2.A (Blender: roughness 0.3 + 0.6 (1 - A): 0.39 at the
    /// skin mean A 0.49), F0 0.04 (Specular IOR level 0.5), a warmer wrap band (Blender: subsurface 0.15, red radius).
    /// The body profile on a FACES head was the C3 look: twice the relief (1.0 + pores 0.45 over BumpMap_R2's pores),
    /// 70 % of the specular from a 0.25-smoothness lobe at F0 0.028 - matte, grey, "lifeless".
    /// </summary>
    public readonly struct RemasterSkinProfile
    {
        public readonly float BumpScale, DetailNormalScale, Smoothness, PrimaryLobe, SecondLobeSmoothness, SkinF0, Wrap, ScatterSaturation;
        /// <summary>
        /// fix-52 (editor-runner-30: hero warm, NPC heads matte and slightly grey): the profiles were the same, the light
        /// was not - the hero was shot in sun (Lambert + the warm wrap band), the NPCs facing the camera in shade, where a
        /// face gets only the SH ambient (blue-grey sky, no band) and an environment sheen weighted by smoothness² (~0.1).
        /// FACES heads re-tint part of the ambient to the skin's own hue (luminance kept) and keep a floor of environment
        /// sheen on skin texels. Both 0 on the body profile (R1 unchanged).
        /// </summary>
        public readonly float AmbientWarmth, EnvSheen;

        public RemasterSkinProfile(float bumpScale, float detailNormalScale, float smoothness, float primaryLobe,
                                   float secondLobeSmoothness, float skinF0, float wrap, float scatterSaturation,
                                   float ambientWarmth = 0f, float envSheen = 0f)
        {
            BumpScale = bumpScale; DetailNormalScale = detailNormalScale; Smoothness = smoothness; PrimaryLobe = primaryLobe;
            SecondLobeSmoothness = secondLobeSmoothness; SkinF0 = skinF0; Wrap = wrap; ScatterSaturation = scatterSaturation;
            AmbientWarmth = ambientWarmth; EnvSheen = envSheen;
        }

        public static readonly RemasterSkinProfile Body = new RemasterSkinProfile(1f, 0.45f, 0.85f, 0.3f, 0.25f, 0.028f, 0.25f, 0.5f);
        public static readonly RemasterSkinProfile Face = new RemasterSkinProfile(0.6f, 0f, 0.8f, 0.75f, 0.35f, 0.04f, 0.3f, 0.75f, 0.5f, 0.35f);

        /// <summary>facetex.py's marker file in a FACES head texture set.</summary>
        public const string FaceInfo = "face.json";

        public static RemasterSkinProfile For(bool faceSet) => faceSet ? Face : Body;

        /// <summary>Blender's skin smoothness for a MaskMap_R2 alpha (facerender: roughness = 0.3 + 0.6 (1 - A)).</summary>
        public static float BlenderSmoothness(float maskAlpha) => 1f - (0.3f + 0.6f * (1f - maskAlpha));

        /// <summary>The profile's primary-lobe smoothness for a mask alpha (the shader: saturate(A x _Smoothness)).</summary>
        public float UnitySmoothness(float maskAlpha) => Math.Max(0f, Math.Min(1f, maskAlpha * Smoothness));
    }
}
