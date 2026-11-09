using System;
using System.Collections.Generic;

namespace G3.Formats.Remaster
{
    /// <summary>
    /// Material parameters of the v2 hero showcase (Docs/remaster-characters.md "v2 hero showcase"), the pure part the
    /// Unity material builder (G3RemasterMaterials.Generated) applies to the <c>G3_Remaster_V2_*</c> slots. Values are
    /// shader property names of G3/Remaster/V2/{Skin,Eye,Hair,EyeOcclusion} (shared include G3RemasterV2.hlsl),
    /// G3/Remaster/Cornea (tear film) and G3/Remaster/Armour (outfit surfaces with full CC0 PBR maps).
    /// </summary>
    public static class RemasterV2Look
    {
        /// <summary>Skin texture set marker (herov2tex.py): MaskMap = R thickness (1 = thin), G cavity, B curvature as
        /// LUT v (<see cref="RemasterSkinLut.LutV"/>), A smoothness.</summary>
        public const string SkinInfo = "skin_v2.json";

        /// <summary>Pre-integrated LUT size (u = N·L, v = curvature).</summary>
        public const int LutWidth = 128, LutHeight = 64;

        // ---- skin ------------------------------------------------------------------------------------------------
        // Dual-lobe specular as in production skin models (Unreal's "dual specular": 85 % of a lobe at 0.75 x the
        // roughness, 15 % at 1.3 x), F0 0.028 (skin IOR ~1.4), specular occlusion by the cavity map; diffuse from the
        // pre-integrated LUT with per-channel softened normals; transmission through thin parts (ears, nostrils) by
        // the thickness map.
        static readonly Dictionary<string, float> SkinFace = new Dictionary<string, float>
        {
            ["_BumpScale"] = 1.0f,
            ["_DetailNormalScale"] = 0.3f,      // tiled micro pores on top of the 4096 face normal
            ["_Smoothness"] = 0.8f,             // x MaskMap A; owner: the head 'shines like a beast' -> matte-satin
            ["_Lobe1Roughness"] = 1.0f,         // primary lobe at the map's roughness (UE's 0.75 is for wet-look close-ups)
            ["_Lobe2Roughness"] = 1.45f,
            ["_LobeMix"] = 0.15f,               // share of the broad lobe
            ["_SkinF0"] = 0.028f,
            ["_CavitySpecular"] = 1.0f,
            ["_OcclusionStrength"] = 1.0f,
            ["_CurvatureScale"] = (float)(1.0 / RemasterSkinLut.MaxCurvature),
            ["_BakedCurvature"] = 1.0f,         // MaskMap B carries the baked curvature (LUT v)
            ["_NormalBlurR"] = 0.65f,           // per-channel diffuse normal: share of the geometric normal
            ["_NormalBlurG"] = 0.35f,
            ["_NormalBlurB"] = 0.15f,
            ["_TransmissionStrength"] = 0.3f,      // hero-v3: 0.6 lit the lower lids / nostril rims orange-red in the sun
            ["_TransmissionDistortion"] = 0.25f,
            ["_TransmissionPower"] = 3.0f,
            ["_EnvSpecular"] = 0.3f,            // probe reflection: satin, not wet
            ["_EnvSaturation"] = 0.3f,
            ["_RimStrength"] = 0.12f,
            ["_SsaoStrength"] = 0.8f,
            ["_AlbedoGain"] = 1.0f,
            ["_ShadowSampleOffset"] = 0.0f,
        };

        /// <summary>Hands/arms/neck of the v2 outfit: same model, curvature from screen-space derivatives, less transmission.</summary>
        static readonly Dictionary<string, float> SkinBody = With(SkinFace, new Dictionary<string, float>
        {
            ["_BakedCurvature"] = 0.0f,
            ["_DetailNormalScale"] = 0.2f,
            ["_TransmissionStrength"] = 0.2f,     // below the face (hero-v3 face 0.3)
            ["_RimStrength"] = 0.08f,
        });

        // ---- eye ---------------------------------------------------------------------------------------------------
        // One eyeball mesh with a real cornea bulge (eyegen2.py, planar front UV: centre = pupil, radius 0.5 = eyeball
        // radius). The iris is a virtual plane _IrisDepth behind the cornea apex: the view ray is refracted at the cornea
        // (IOR 1.336) and the iris texture is sampled where it hits that plane (parallax refraction). The cornea is a
        // sharp GGX lobe + probe reflection on the geometric normal; the iris is lit with a concave normal (light enters
        // on the far side: the caustic look); the sclera gets a soft wrap.
        static readonly Dictionary<string, float> Eye = new Dictionary<string, float>
        {
            ["_IrisRadius"] = 0.48f,            // in eyeball radii
            ["_CorneaRadius"] = 0.52f,
            ["_IrisDepth"] = 0.0f,              // m; hero-v4.2: 0.0026 parallax pulled the sclera into the cornea disc through the
                                                // narrow slit (segmented "petal" iris); depth now from _IrisAO + concave lighting
            ["_EyeRadius"] = 0.012f,            // m; per eye from the set's eye_v2.json
            ["_IOR"] = 1.336f,
            ["_CorneaSmoothness"] = 0.96f,
            ["_CorneaF0"] = 0.025f,
            ["_ScleraSmoothness"] = 0.6f,
            ["_IrisConcavity"] = 0.3f,          // hero-v4: 0.6 lit one half of the iris, the other dark (segmented look)
            ["_LimbusDark"] = 0.3f,             // hero-v4: soft limbal band from the iris edge out past the cornea rim
            ["_EnvSpecular"] = 1.0f,            // hero-v4: the wet cornea mirrors the sky (was 0.5: dull)
            ["_CatchlightStrength"] = 3.0f,     // hero-v4: view-anchored eye light x the ambient seen behind the camera
            ["_CatchlightSmoothness"] = 0.88f,  // spot size: small, a few pixels at dialogue distance
            ["_CatchlightUp"] = 0.25f,          // above the camera: lands on the upper-middle cornea, below the lid shadow
            ["_CatchlightSide"] = 0.15f,
            ["_IrisAO"] = 0.12f,                // iris bowl: darker towards the limbus (depth under the cornea)
            ["_IrisGain"] = 1.0f,               // texture carries the level now (eyegen2 hero-v4: A's green-grey, lum 0.12)
            ["_ScleraGain"] = 1.0f,
            ["_CaruncleGain"] = 0.45f,          // inner-corner patch read as bright pink plastic
            ["_EnvSaturation"] = 0.3f,
            ["_BumpScale"] = 0.4f,              // sclera veins / surface
            ["_ShadowSampleOffset"] = 0.012f,   // m: the lids' shadow is the occlusion shell's, not the cascade's
            ["_SsaoStrength"] = 0.3f,
            ["_AlbedoGain"] = 1.0f,
            ["_RimStrength"] = 0.0f,
        };

        static readonly Dictionary<string, float> EyeOcclusion = new Dictionary<string, float>
        {
            ["_OcclusionStrength"] = 1.0f,   // hero-v3: eyes in the brow's shadow (A), no staring
            ["_OcclusionPower"] = 1.0f,
        };

        /// <summary>Tear film (meniscus + wet line) on G3/Remaster/Cornea: brighter, sharper than the old cornea shell.</summary>
        static readonly Dictionary<string, float> TearFilm = new Dictionary<string, float>
        {
            ["_Smoothness"] = 0.97f,
            ["_F0"] = 0.25f,                    // hero-v4: thin meniscus ribbon; physical 0.03 was invisible -> a faint wet line
            ["_SpecularStrength"] = 4.0f,
            ["_EnvSpecular"] = 1.0f,
            ["_EnvSaturation"] = 0.3f,
        };

        // ---- hair ----------------------------------------------------------------------------------------------------
        // Marschner R / TT / TRT lobes in Karis' real-time form (SIGGRAPH 2016) + a wrapped Kajiya-Kay multiple-scatter
        // diffuse. Two passes: opaque cards above _Cutoff (depth write), then the soft fringe (alpha between
        // _FringeCutoff and _Cutoff) blended on top without depth write: smooth strand ends without MSAA or sorting.
        static readonly Dictionary<string, float> Hair = new Dictionary<string, float>
        {
            ["_Cutoff"] = 0.5f,                 // runner-31: 0.7 left the crown gappy
            ["_FringeCutoff"] = 0.15f,          // 0.06 drew grey haze from near-empty texels
            ["_HairRoughness"] = 0.7f,          // owner: 0.35 read glossy-white in the sun; hero-v3: v2 hair still "shiny" -> matte
            ["_HairShift"] = 0.035f,            // rad, cuticle tilt
            ["_HairSpecular"] = 0.12f,
            ["_HairScatter"] = 1.0f,            // diffuse = albedo x wrapped Kajiya (was 0.35 x sqrt(albedo))
            ["_HairBacklit"] = 0.12f,           // TT through dark hair from behind: a warm edge, not white fur
            ["_HairSpecCap"] = 0.15f,
            ["_StrandAxis"] = 1.0f,             // strands along V
            ["_EnvSpecular"] = 0.1f,
            ["_EnvSaturation"] = 0.3f,
            ["_RimStrength"] = 0.0f,            // the sky rim on thin cards read whitish
            ["_SsaoStrength"] = 0.6f,
            ["_AlbedoGain"] = 1.0f,
            ["_BumpScale"] = 0.0f,
            ["_DitherRange"] = 0.0f,
            ["_HairShell"] = 0.0f,
        };

        /// <summary>hero-v3 hair shell (helmet, herov2hair.make_shell): a closed textured layer in the skin layout with a
        /// strand normal map; matte wrapped Lambert instead of the strand lobes (its UV tangents do not follow the hair),
        /// hard alpha cut at the painted hairline (the blended fringe pass keeps the edge soft).</summary>
        static readonly Dictionary<string, float> HairShell = With(Hair, new Dictionary<string, float>
        {
            ["_HairShell"] = 1.0f,
            ["_BumpScale"] = 1.0f,
            ["_Cutoff"] = 0.5f,
            ["_FringeCutoff"] = 0.2f,
            ["_HairSpecular"] = 0.5f,          // x 0.04 broad sheen in the shell model
            ["_OcclusionStrength"] = 1.0f,
            ["_EnvSpecular"] = 0.05f,
        });

        /// <summary>Stubble shells: the same hair model; short dots, harder cut, no fringe pass needed beyond a sliver.</summary>
        static readonly Dictionary<string, float> Stubble = With(Hair, new Dictionary<string, float>
        {
            ["_Cutoff"] = 0.5f,
            ["_FringeCutoff"] = 0.3f,
            ["_HairRoughness"] = 0.45f,
            ["_HairSpecular"] = 0.12f,
            ["_RimStrength"] = 0.0f,
        });

        // ---- outfit surfaces: G3/Remaster/Armour with plain PBR (CC0 scans are calibrated, not G3's dark baked albedos) --
        static readonly Dictionary<string, float> Surface = new Dictionary<string, float>
        {
            ["_BumpScale"] = 1.0f,
            ["_Metallic"] = 1.0f,
            ["_Smoothness"] = 1.0f,
            ["_OcclusionStrength"] = 1.0f,
            ["_EnvSpecular"] = 0.8f,
            ["_EnvSaturation"] = 0.5f,
            ["_SkinMaskMin"] = 0.0f,
            ["_DetailNormalScale"] = 0.0f,
            ["_Transmission"] = 0.0f,
            ["_DarkSpecular"] = 1.0f,
            ["_MetalDiffuse"] = 0.0f,
            ["_MetalSpecBoost"] = 1.0f,
            ["_MetalTint"] = 1.0f,
            ["_MetalF0Max"] = 1.0f,
            ["_MetalSmoothness"] = 1.0f,
            ["_SkinF0"] = 0.028f,
            ["_SkinPrimaryLobe"] = 0.3f,
        };

        /// <summary>Float properties for a v2 kind (<paramref name="bodySkin"/>: skin slot of an outfit). Empty for other kinds.</summary>
        public static IReadOnlyDictionary<string, float> Floats(RemasterGeneratedKind kind, bool bodySkin = false, bool hairShell = false) => kind switch
        {
            RemasterGeneratedKind.SkinV2 => bodySkin ? SkinBody : SkinFace,
            RemasterGeneratedKind.EyeV2 => Eye,
            RemasterGeneratedKind.EyeOcclusion => EyeOcclusion,
            RemasterGeneratedKind.TearFilm => TearFilm,
            RemasterGeneratedKind.HairV2 => hairShell ? HairShell : Hair,
            RemasterGeneratedKind.Stubble => Stubble,
            RemasterGeneratedKind.Surface => Surface,
            _ => new Dictionary<string, float>(),
        };

        /// <summary>Colour properties (linear RGBA) for a v2 kind.</summary>
        public static IReadOnlyDictionary<string, float[]> Colors(RemasterGeneratedKind kind) => kind switch
        {
            // deep red: what survives a few mm of tissue (ears against the sun)
            RemasterGeneratedKind.SkinV2 => new Dictionary<string, float[]> { ["_TransmissionColor"] = new[] { 1.0f, 0.28f, 0.12f, 1f } },
            _ => new Dictionary<string, float[]>(),
        };

        /// <summary>Hair shell (helmet) set rather than strand cards: the set name says so (G3_Remaster_V2_Hair__HeroShell).</summary>
        public static bool IsHairShell(string set) =>
            !string.IsNullOrEmpty(set) && set.EndsWith("Shell", StringComparison.OrdinalIgnoreCase);

        /// <summary>Skin slot of an outfit body (hands, arms, neck) rather than a face: the set name says so.</summary>
        public static bool IsBodySkin(string set) =>
            !string.IsNullOrEmpty(set) && set.IndexOf("Body", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>
        /// Eye radius (m) for the eye shader from the set's measured value (eyegen2: eye_v2.json); the default when
        /// missing or implausible (eyeballs are 10-17 mm in radius on the G3 heads).
        /// </summary>
        public static float EyeRadius(float? measured) =>
            measured.HasValue && measured.Value >= 0.008f && measured.Value <= 0.02f ? measured.Value : Eye["_EyeRadius"];

        /// <summary>
        /// Iris / cornea radius (eyeball radii) for the eye shader from the set's eye_v2.json (eyegen2 scales the iris per
        /// head), so the shader's refraction disc and limbal ring sit on the painted iris; the defaults when missing or
        /// implausible. The cornea is kept just outside the iris.
        /// </summary>
        public static float IrisRadius(float? measured) =>
            measured.HasValue && measured.Value >= 0.25f && measured.Value <= 0.65f ? measured.Value : Eye["_IrisRadius"];

        public static float CorneaRadius(float? measured, float iris) =>
            measured.HasValue && measured.Value > iris && measured.Value <= 0.75f ? measured.Value : Math.Max(Eye["_CorneaRadius"], iris + 0.04f);

        static Dictionary<string, float> With(Dictionary<string, float> a, Dictionary<string, float> b)
        {
            var r = new Dictionary<string, float>(a);
            foreach (var kv in b) r[kv.Key] = kv.Value;
            return r;
        }
    }
}
