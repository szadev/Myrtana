// Gothic 3 vegetation grid (eCVegetation_PS: grass, flowers, small bushes), drawn by G3VegetationRenderer with
// Graphics.RenderMeshInstanced. Alpha-clipped URP lighting plus:
//  * per-instance colour (_InstColor, 0xAARRGGBB of the plant record) multiplied into the albedo,
//  * distance fade like Genome (FadeOutStart..ViewRange): plants shrink towards their origin between
//    _G3VegFade.x and the view range (per instance origin), so there is no popping and no dither noise,
//  * mip-aware alpha so thin blades do not vanish at distance,
//  * back faces get flipped normals (double-sided cards), normals optionally bent towards world up,
//  * G3 wind (G3Wind.hlsl / G3Wind component): bend by height above the instance origin, per-instance phase from
//    the instance position, travelling gusts, light flutter - applied in every pass (VegPositionWS).
Shader "G3/Vegetation"
{
    Properties
    {
        _BaseMap("Diffuse", 2D) = "white" {}
        _BaseColor("Tint", Color) = (1,1,1,1)
        _Cutoff("Alpha cutoff", Range(0,1)) = 0.5
        _Smoothness("Smoothness", Range(0,1)) = 0.05
        _InstColorStrength("Instance colour strength", Range(0,1)) = 1
        _InstColorScale("Instance colour scale", Float) = 1
        _NormalUp("Bend normals up", Range(0,1)) = 0.5
        _MipAlphaScale("Mip alpha boost", Range(0,1)) = 0.25
        _G3WindScale("Wind amount (eCVegetation_Mesh WindStrength)", Range(0,3)) = 1
        [Enum(UnityEngine.Rendering.CullMode)] _Cull("Cull", Float) = 2
        [HideInInspector] _G3VegFade("Fade (start, 1/len, end)", Vector) = (0,0,0,0)
        // Unused, keeps Lit-style tooling happy.
        [HideInInspector] _EnvironmentReflections("Environment reflections", Float) = 0
        // World remaster only (Docs/remaster-world.md; keyword set on the override copies, never on the originals)
        [Toggle(_G3REMASTER_FOLIAGE)] _G3RemasterFoliage("Remaster foliage terms", Float) = 0
        _Translucency("Back-light translucency", Range(0, 1)) = 0.35
        _FoliageAO("Ground occlusion", Range(0, 1)) = 0.35
        _FoliageAOHeight("Ground occlusion height (m)", Float) = 0.6
    }

    HLSLINCLUDE
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
    #include "Assets/G3/Unity/Shaders/Wind/G3Wind.hlsl"
    #include "Assets/G3/Unity/Shaders/Remaster/G3ReliefGround.hlsl"

    TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);

    CBUFFER_START(UnityPerMaterial)
        float4 _BaseMap_ST;
        float4 _BaseMap_TexelSize;
        half4 _BaseColor;
        half _Cutoff;
        half _Smoothness;
        half _InstColorStrength;
        half _InstColorScale;
        half _NormalUp;
        half _MipAlphaScale;
        float4 _G3VegFade;
        half _G3WindScale;
        half _Translucency;
        half _FoliageAO;
        half _FoliageAOHeight;
    CBUFFER_END

    UNITY_INSTANCING_BUFFER_START(G3VegProps)
        UNITY_DEFINE_INSTANCED_PROP(float4, _InstColor)
    UNITY_INSTANCING_BUFFER_END(G3VegProps)

    // 1 = fully visible, 0 = gone. Uses the instance origin so a plant fades as a whole.
    float VegFade()
    {
        float3 origin = UNITY_MATRIX_M._m03_m13_m23;
        float d = distance(origin, _WorldSpaceCameraPos.xyz);
        return _G3VegFade.y > 0 ? saturate(1 - (d - _G3VegFade.x) * _G3VegFade.y) : 1;
    }

    // World position after wind and the distance fade (plant shrinks into the ground at its origin).
    float3 VegPositionWS(float3 positionOS, out float fade)
    {
        float3 positionWS = TransformObjectToWorld(positionOS);
        fade = VegFade();
        float3 origin = UNITY_MATRIX_M._m03_m13_m23;
        float relief = G3ReliefGroundDelta(origin);   // on the drawn relief (remaster-world 14)
        positionWS.y += relief; origin.y += relief;
        positionWS = G3WindGrass(positionWS, origin, _G3WindScale);
        return lerp(origin, positionWS, fade);
    }

    half VegAlpha(float2 uv)
    {
        half a = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, uv).a * _BaseColor.a;
        float2 t = uv * _BaseMap_TexelSize.zw;
        float2 dx = ddx(t), dy = ddy(t);
        float mip = 0.5 * log2(max(dot(dx, dx), dot(dy, dy)));
        return a * (1 + max(0, mip) * _MipAlphaScale);
    }

    void VegClip(float2 uv, float fade, float4 positionCS)
    {
        clip(VegAlpha(uv) - _Cutoff);
        clip(fade - 0.001);   // fully faded plants are degenerate anyway (see VegPositionWS)
    }
    ENDHLSL

    SubShader
    {
        Tags { "RenderType" = "TransparentCutout" "RenderPipeline" = "UniversalPipeline" "Queue" = "AlphaTest" }
        Cull [_Cull]

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile _ _LIGHT_LAYERS
            #pragma multi_compile_instancing
            #pragma shader_feature_local_fragment _G3REMASTER_FOLIAGE
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Fog.hlsl"
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/ProbeVolumeVariants.hlsl"

            #define _ENVIRONMENTREFLECTIONS_OFF
            #define _SPECULARHIGHLIGHTS_OFF
            #define _ALPHATEST_ON
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Assets/G3/Unity/Shaders/Remaster/G3RemasterTune.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                half3 normalWS    : TEXCOORD2;
                half4 color       : TEXCOORD3;   // rgb = instance tint, a = fade
                half fogFactor    : TEXCOORD4;
                half3 vertexSH    : TEXCOORD5;
                half heightAbove  : TEXCOORD6;   // metres above the instance origin (remaster ground occlusion)
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings Vert(Attributes v)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_TRANSFER_INSTANCE_ID(v, o);
                float fade;
                float3 positionWS = VegPositionWS(v.positionOS.xyz, fade);
                o.positionCS = TransformWorldToHClip(positionWS);
                o.positionWS = positionWS;
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                o.uv = TRANSFORM_TEX(v.uv, _BaseMap);
                float4 ic = UNITY_ACCESS_INSTANCED_PROP(G3VegProps, _InstColor);
            #if !defined(UNITY_INSTANCING_ENABLED)
                ic = float4(1, 1, 1, 1);
            #endif
                o.color.rgb = lerp(1, ic.rgb * _InstColorScale, _InstColorStrength);
                o.color.a = fade;
                o.fogFactor = ComputeFogFactor(o.positionCS.z);
                o.vertexSH = SampleSHVertex(normalize(lerp(o.normalWS, float3(0, 1, 0), 0.5)));
                o.heightAbove = positionWS.y - UNITY_MATRIX_M._m13 - G3ReliefGroundDelta(UNITY_MATRIX_M._m03_m13_m23);
                return o;
            }

            half4 Frag(Varyings i, FRONT_FACE_TYPE face : FRONT_FACE_SEMANTIC) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                half4 tex = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv);
                VegClip(i.uv, i.color.a, i.positionCS);

                half3 n = normalize(i.normalWS);
                n = IS_FRONT_VFACE(face, n, -n);
                n = normalize(lerp(n, half3(0, 1, 0), _NormalUp));

                InputData input = (InputData)0;
                input.positionWS = i.positionWS;
                input.positionCS = i.positionCS;
                input.normalWS = n;
                input.viewDirectionWS = GetWorldSpaceNormalizeViewDir(i.positionWS);
                input.shadowCoord = TransformWorldToShadowCoord(i.positionWS);
                input.fogCoord = i.fogFactor;
                input.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(i.positionCS);
                input.bakedGI = SampleSHPixel(i.vertexSH, n);
                input.shadowMask = half4(1, 1, 1, 1);

                SurfaceData s = (SurfaceData)0;
                s.albedo = tex.rgb * _BaseColor.rgb * i.color.rgb;
                s.alpha = 1;
                s.smoothness = _Smoothness;
                s.occlusion = 1;
                s.normalTS = half3(0, 0, 1);
            #if defined(_G3REMASTER_FOLIAGE)
                s.occlusion = lerp(1 - _FoliageAO, 1, saturate(i.heightAbove / max(_FoliageAOHeight, 0.01)));
            #endif

                half4 color = UniversalFragmentPBR(input, s);
            #if defined(_G3REMASTER_FOLIAGE)
                {
                    // light through the blades when looking towards the sun (thin cards, no subsurface model) + sheen
                    Light sun = GetMainLight(input.shadowCoord);
                    color.rgb += G3GrassLight(s.albedo, input.normalWS, input.viewDirectionWS, sun.direction, sun.color,
                                                sun.shadowAttenuation, _Translucency);
                }
            #endif
                color.rgb = MixFog(color.rgb, input.fogCoord);
                color.a = 1;
                return color;
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On ZTest LEqual ColorMask 0

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;
            float3 _LightPosition;

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; float2 uv : TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; float fade : TEXCOORD1; UNITY_VERTEX_INPUT_INSTANCE_ID };

            Varyings Vert(Attributes v)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_TRANSFER_INSTANCE_ID(v, o);
                float fade;
                float3 positionWS = VegPositionWS(v.positionOS.xyz, fade);
                float3 normalWS = TransformObjectToWorldNormal(v.normalOS);
            #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                float3 l = normalize(_LightPosition - positionWS);
            #else
                float3 l = _LightDirection;
            #endif
                o.positionCS = ApplyShadowClamping(TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, l)));
                o.uv = TRANSFORM_TEX(v.uv, _BaseMap);
                o.fade = fade;
                return o;
            }

            half4 Frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                VegClip(i.uv, i.fade, i.positionCS);
                return 0;
            }
            ENDHLSL
        }

        // Remaster TAA (Docs/remaster-world.md 11.7): wind motion at this and the previous frame's wind clock.
        Pass
        {
            Name "MotionVectors"
            Tags { "LightMode" = "MotionVectors" }
            ColorMask RG

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing

            float4 _G3MotionVectors;   // x max distance of per-object wind motion (m), 0 = none

            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; float fade : TEXCOORD1; float4 curCS : TEXCOORD2; float4 prevCS : TEXCOORD3; UNITY_VERTEX_INPUT_INSTANCE_ID };

            Varyings Vert(Attributes v)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_TRANSFER_INSTANCE_ID(v, o);
                if (distance(UNITY_MATRIX_M._m03_m13_m23, _WorldSpaceCameraPos.xyz) > _G3MotionVectors.x) { o.positionCS = float4(0, 0, -2, 1); return o; }
                float fade;
                float3 p = VegPositionWS(v.positionOS.xyz, fade);
                o.positionCS = TransformWorldToHClip(p);
                o.uv = TRANSFORM_TEX(v.uv, _BaseMap);
                o.fade = fade;
                o.curCS = mul(_NonJitteredViewProjMatrix, float4(p, 1));
                g3WindTimeShift = _G3WindTimePrev - _G3WindTime;
                float fp;
                float3 pp = VegPositionWS(v.positionOS.xyz, fp);
                g3WindTimeShift = 0;
                o.prevCS = mul(_PrevViewProjMatrix, float4(pp, 1));
                return o;
            }

            float4 Frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                VegClip(i.uv, i.fade, i.positionCS);
                return float4(G3WindMotion(i.curCS, i.prevCS), 0, 0);
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On ColorMask R

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing

            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; float fade : TEXCOORD1; UNITY_VERTEX_INPUT_INSTANCE_ID };

            Varyings Vert(Attributes v)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_TRANSFER_INSTANCE_ID(v, o);
                float fade;
                o.positionCS = TransformWorldToHClip(VegPositionWS(v.positionOS.xyz, fade));
                o.uv = TRANSFORM_TEX(v.uv, _BaseMap);
                o.fade = fade;
                return o;
            }

            half4 Frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                VegClip(i.uv, i.fade, i.positionCS);
                return i.positionCS.z;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }
            ZWrite On

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; float2 uv : TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; half3 normalWS : TEXCOORD1; float fade : TEXCOORD2; UNITY_VERTEX_INPUT_INSTANCE_ID };

            Varyings Vert(Attributes v)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_TRANSFER_INSTANCE_ID(v, o);
                float fade;
                o.positionCS = TransformWorldToHClip(VegPositionWS(v.positionOS.xyz, fade));
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                o.uv = TRANSFORM_TEX(v.uv, _BaseMap);
                o.fade = fade;
                return o;
            }

            half4 Frag(Varyings i, FRONT_FACE_TYPE face : FRONT_FACE_SEMANTIC) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                VegClip(i.uv, i.fade, i.positionCS);
                half3 n = normalize(i.normalWS);
                n = IS_FRONT_VFACE(face, n, -n);
                n = normalize(lerp(n, half3(0, 1, 0), _NormalUp));
                return half4(NormalizeNormalPerPixel(n), 0);
            }
            ENDHLSL
        }
    }
    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
