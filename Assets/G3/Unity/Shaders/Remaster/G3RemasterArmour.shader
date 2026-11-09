// R1 remaster character shader: metal / leather / cloth body atlases (hands and neck: MaskMap B -> skin terms).
// Shared code: G3RemasterCharacter.hlsl (Docs/remaster-pipeline.md §9). Applied by the importer's remaster override
// (G3RemasterMaterials) to materials whose texture has a GameData/Remaster/Textures set; the originals stay for A/B.
Shader "G3/Remaster/Armour"
{
    Properties
    {
        _BaseMap("Base map (sRGB, alpha = cutout)", 2D) = "white" {}
        _BaseColor("Tint", Color) = (1,1,1,1)
        [Normal] _BumpMap("Normal map", 2D) = "bump" {}
        _BumpScale("Normal strength", Range(0, 2)) = 1
        [NoScaleOffset] _MaskMap("Mask map (R metal, G AO, B skin, A smooth)", 2D) = "white" {}
        _Metallic("Metallic scale", Range(0, 1)) = 1
        _Smoothness("Smoothness scale", Range(0, 2)) = 1
        _OcclusionStrength("Occlusion strength", Range(0, 1)) = 1
        _EnvSpecular("Environment specular", Range(0, 1)) = 0.6
        _EnvSaturation("Environment reflection saturation", Range(0, 1)) = 0.3
        _DetailNormalMap("Pore detail normal (tiled)", 2D) = "bump" {}
        _DetailNormalScale("Pore detail strength", Range(0, 1)) = 0.25
        _ScatterColor("Scatter colour (terminator band)", Color) = (1, 1, 1, 1)
        _Wrap("Wrap (subsurface) amount", Range(0, 1)) = 0.25
        _SkinMaskMin("Skin mask minimum", Range(0, 1)) = 0
        _Transmission("Back-light transmission", Range(0, 1)) = 0.05
        _SecondLobeSmoothness("Second specular lobe smoothness", Range(0, 1)) = 0.25
        [HideInInspector] _HairSpecColor1("Primary highlight", Color) = (0.35, 0.33, 0.3, 1)
        [HideInInspector] _HairSpecColor2("Secondary highlight (x albedo)", Color) = (0.6, 0.55, 0.5, 1)
        [HideInInspector] _HairShift1("Primary shift", Range(-1, 1)) = 0.1
        [HideInInspector] _HairShift2("Secondary shift", Range(-1, 1)) = -0.25
        [HideInInspector] _HairExponent1("Primary exponent", Range(1, 512)) = 160
        [HideInInspector] _HairExponent2("Secondary exponent", Range(1, 512)) = 24
        [HideInInspector] [Toggle] _StrandAxis("Strands along V (else U)", Float) = 1
        [HideInInspector] _DitherRange("Cutout dither range", Range(0, 0.5)) = 0.25
        [HideInInspector] _Cutoff("Alpha cutoff", Range(0, 1)) = 0.5
        [HideInInspector] _CorneaSmoothness("Cornea smoothness", Range(0, 1)) = 0.95
        [HideInInspector] _CorneaStrength("Cornea strength", Range(0, 4)) = 1
        _ScatterSaturation("Scatter band: share of the albedo's own hue", Range(0, 1)) = 0.5
        _DarkSpecular("Dielectric specular on dark albedos", Range(0, 1)) = 0.2
        _MetalDiffuse("Metal: albedo kept as diffuse", Range(0, 1)) = 0.85
        _MetalSpecBoost("Metal: F0 = tinted albedo x", Range(1, 4)) = 1.6
        _MetalTint("Metal: F0 hue (0 = albedo luminance, 1 = albedo colour)", Range(0, 1)) = 0.35
        _MetalF0Max("Metal: brightest F0", Range(0.05, 1)) = 0.4
        _MetalSmoothness("Metal: smoothness x", Range(0, 1)) = 0.8
        [HideInInspector] _AlbedoGain("Albedo gain (FACES eye sclera / hair cap)", Range(1, 4)) = 1
        [HideInInspector] _ShadowSampleOffset("Main-light shadow sampled this far out along the normal (m)", Range(0, 0.05)) = 0
        [HideInInspector] _SsaoStrength("SSAO kept", Range(0, 1)) = 1
        [HideInInspector] _SkinPrimaryLobe("Skin: texture-driven specular lobe weight", Range(0, 1)) = 0.3
        [HideInInspector] _SkinF0("Skin: reflectance at normal incidence", Range(0.02, 0.06)) = 0.028
        [HideInInspector] _SkinAmbientWarmth("Skin: ambient light tinted to the skin hue (FACES)", Range(0, 1)) = 0
        [HideInInspector] _SkinEnvSheen("Skin: environment sheen floor (FACES)", Range(0, 1)) = 0
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" "UniversalMaterialType" = "Lit" }
        Cull Back

        HLSLINCLUDE
            #define G3_REMASTER_ARMOUR 1
            #include "G3RemasterCharacter.hlsl"
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex RemasterVert
            #pragma fragment RemasterFrag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_ATLAS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fragment _ _LIGHT_COOKIES
            #pragma multi_compile _ _LIGHT_LAYERS
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_instancing
            #pragma instancing_options renderinglayer
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Fog.hlsl"
            #define G3_REMASTER_FORWARD 1
            #include "G3RemasterCharacter.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On
            ZTest LEqual
            ColorMask 0

            HLSLPROGRAM
            #pragma target 2.0
            #pragma vertex ShadowPassVertex
            #pragma fragment ShadowPassFragment
            #pragma multi_compile_instancing
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"
            #pragma multi_compile _ LOD_FADE_CROSSFADE
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #include "Packages/com.unity.render-pipelines.universal/Shaders/ShadowCasterPass.hlsl"
            ENDHLSL
        }

        // fix-191: per-object motion vectors (skinning via the SkinnedMeshRenderer's previous positions, TEXCOORD4, and
        // the previous object matrix) for the remaster TAA/STP; without it a moving character only got camera motion and
        // smeared. URP's ObjectMotionVectors pass (as in URP Lit) on this shader's UnityPerMaterial.
        Pass
        {
            Name "MotionVectors"
            Tags { "LightMode" = "MotionVectors" }
            ColorMask RG

            HLSLPROGRAM
            #pragma multi_compile _ LOD_FADE_CROSSFADE
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/ObjectMotionVectors.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On
            ColorMask R

            HLSLPROGRAM
            #pragma target 2.0
            #pragma vertex DepthOnlyVertex
            #pragma fragment DepthOnlyFragment
            #pragma multi_compile _ LOD_FADE_CROSSFADE
            #pragma multi_compile_instancing
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/Shaders/DepthOnlyPass.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }
            ZWrite On

            HLSLPROGRAM
            #pragma target 2.0
            #pragma vertex DepthNormalsVertex
            #pragma fragment DepthNormalsFragment
            #pragma multi_compile _ LOD_FADE_CROSSFADE
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RenderingLayers.hlsl"
            #pragma multi_compile_instancing
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/Shaders/DepthNormalsPass.hlsl"
            ENDHLSL
        }
    }
    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
