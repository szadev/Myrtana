// v2 hero skin: pre-integrated subsurface scattering (LUT), two specular lobes, thickness transmission. Code and
// parameters: G3RemasterV2.hlsl, G3.Formats.Remaster.RemasterV2Look (material builder: G3RemasterMaterials.Generated).
Shader "G3/Remaster/V2/Skin"
{
    Properties
    {
        _BaseMap("Base map (sRGB)", 2D) = "white" {}
        _BaseColor("Tint", Color) = (1,1,1,1)
        [Normal] _BumpMap("Normal map", 2D) = "bump" {}
        _BumpScale("Normal strength", Range(0, 2)) = 1
        [NoScaleOffset] _MaskMap("Mask map", 2D) = "white" {}
        _DetailNormalMap("Micro detail normal (tiled)", 2D) = "bump" {}
        _DetailNormalScale("Micro detail strength", Range(0, 1)) = 0.3
        [NoScaleOffset] _SkinLut("Pre-integrated skin LUT (sqrt-stored)", 2D) = "white" {}
        _Cutoff("Alpha cutoff (opaque pass)", Range(0, 1)) = 0.7
        _FringeCutoff("Fringe cutoff (blended pass)", Range(0, 1)) = 0.06
        _Smoothness("Smoothness scale", Range(0, 2)) = 1
        _OcclusionStrength("Occlusion (MaskMap G)", Range(0, 1)) = 1
        _Lobe1Roughness("Lobe 1 roughness x", Range(0.2, 2)) = 0.75
        _Lobe2Roughness("Lobe 2 roughness x", Range(0.2, 2)) = 1.3
        _LobeMix("Lobe 2 share", Range(0, 1)) = 0.15
        _SkinF0("Skin F0", Range(0.01, 0.08)) = 0.028
        _CavitySpecular("Specular occlusion by cavity", Range(0, 1)) = 1
        _CurvatureScale("Curvature scale (1/m to LUT)", Float) = 0.002
        _BakedCurvature("Baked curvature (MaskMap B) vs screen space", Range(0, 1)) = 1
        _NormalBlurR("Diffuse normal blur R", Range(0, 1)) = 0.65
        _NormalBlurG("Diffuse normal blur G", Range(0, 1)) = 0.35
        _NormalBlurB("Diffuse normal blur B", Range(0, 1)) = 0.15
        _TransmissionColor("Transmission colour", Color) = (1, 0.28, 0.12, 1)
        _TransmissionStrength("Transmission", Range(0, 2)) = 0.6
        _TransmissionDistortion("Transmission distortion", Range(0, 1)) = 0.25
        _TransmissionPower("Transmission power", Range(1, 16)) = 3
        _EnvSpecular("Environment specular", Range(0, 1)) = 0.6
        _EnvSaturation("Environment saturation", Range(0, 1)) = 0.3
        _RimStrength("Ambient rim", Range(0, 1)) = 0.12
        _SsaoStrength("SSAO kept", Range(0, 1)) = 0.8
        _AlbedoGain("Albedo gain", Range(0.5, 4)) = 1
        _ShadowSampleOffset("Shadow sample offset (m)", Range(0, 0.05)) = 0
        _IrisRadius("Iris radius (eyeball radii)", Range(0.2, 0.7)) = 0.48
        _CorneaRadius("Cornea radius (eyeball radii)", Range(0.2, 0.8)) = 0.52
        _IrisDepth("Iris depth behind the cornea apex (m)", Float) = 0.0026
        _EyeRadius("Eyeball radius (m)", Float) = 0.012
        _IOR("Cornea IOR", Range(1, 1.6)) = 1.336
        _CorneaSmoothness("Cornea smoothness", Range(0, 1)) = 0.96
        _CorneaF0("Cornea F0", Range(0, 0.1)) = 0.025
        _ScleraSmoothness("Sclera smoothness", Range(0, 1)) = 0.6
        _IrisConcavity("Iris concavity", Range(0, 1)) = 0.6
        _LimbusDark("Limbal ring darkening", Range(0, 1)) = 0.35
        _HairRoughness("Hair roughness", Range(0.1, 1)) = 0.35
        _HairShift("Cuticle tilt (rad)", Range(0, 0.2)) = 0.035
        _HairSpecular("Hair specular", Range(0, 2)) = 0.5
        _HairScatter("Hair multiple scatter", Range(0, 1)) = 0.35
        _HairBacklit("Hair back light", Range(0, 1)) = 0.6
        [Toggle] _StrandAxis("Strands along V (else U)", Float) = 1
        _DitherRange("Dither (unused)", Range(0, 0.5)) = 0
        _OcclusionPower("Occlusion power", Range(0.2, 4)) = 1
        _HairSpecCap("Hair: cap of the R+TT+TRT lobes (per unit light)", Range(0.05, 4)) = 0.35
        [Toggle] _HairShell("Hair: shell (helmet) - matte normal-mapped surface, no strand lobes", Float) = 0
        _CatchlightStrength("Eye: catch light (x ambient seen behind the camera)", Range(0, 8)) = 0
        _CatchlightSmoothness("Eye: catch light smoothness (size)", Range(0.5, 1)) = 0.9
        _CatchlightUp("Eye: catch light above the camera (tan)", Range(-1, 1)) = 0.3
        _CatchlightSide("Eye: catch light beside the camera (tan)", Range(-1, 1)) = 0.2
        _IrisAO("Eye: ambient occlusion of the iris bowl at the limbus", Range(0, 1)) = 0
        _IrisGain("Eye: iris albedo gain", Range(0, 4)) = 1
        _ScleraGain("Eye: sclera albedo gain", Range(0, 4)) = 1
        _CaruncleGain("Eye: caruncle (inner corner) albedo gain", Range(0, 2)) = 1
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" "UniversalMaterialType" = "Lit" }
        Cull Back

        HLSLINCLUDE
            #define G3_V2_SKIN 1
            #include "G3RemasterV2.hlsl"
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex V2Vert
            #pragma fragment V2Frag
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
            #define G3_V2_FORWARD 1
            #include "G3RemasterV2.hlsl"
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
