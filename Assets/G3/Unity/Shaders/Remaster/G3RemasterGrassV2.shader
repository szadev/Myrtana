// World remaster v2 grass (Docs/remaster-world.md section 10.3): G3's vegetation meshes and atlases drawn with
// Graphics.RenderMeshIndirect from GPU buffers (G3GrassV2): G3's own plants plus the GrassDensify copies, culled per
// camera by G3GrassCull.compute. Same look as G3/Vegetation (instance colour, shrink-fade towards the view range,
// mip-aware alpha, G3 wind incl. the remaster waves), plus back-light translucency (_G3V2Foliage.w), ground occlusion
// by height and a soft sheen. Instance data: _G3GrassInstances[_G3GrassVisible[SV_InstanceID]].
Shader "G3/Remaster/GrassV2"
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
        _G3WindScale("Wind amount", Range(0,3)) = 1
        _FoliageAO("Ground occlusion", Range(0, 1)) = 0.45
        _FoliageAOHeight("Ground occlusion height (m)", Float) = 0.6
        [Enum(UnityEngine.Rendering.CullMode)] _Cull("Cull", Float) = 0
    }

    HLSLINCLUDE
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
    #include "Assets/G3/Unity/Shaders/Wind/G3Wind.hlsl"
    #include "Assets/G3/Unity/Shaders/Remaster/G3ReliefGround.hlsl"
    #include "Assets/G3/Unity/Shaders/Remaster/G3ScatterBend.hlsl"

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
        half _G3WindScale;
        half _FoliageAO;
        half _FoliageAOHeight;
    CBUFFER_END

    struct G3GrassInst
    {
        float3 pos;
        float width;
        float4 rot;
        float height;
        uint color;
        float extra;
        float pad;
    };
    StructuredBuffer<G3GrassInst> _G3GrassInstances;
    StructuredBuffer<uint> _G3GrassVisible;
    float4 _G3GrassFade;    // x fade start (m), y 1 / fade length, z end (m), w mesh top (m at scale 1; 0 = unknown)
    float4 _G3V2Foliage;    // x on, y crown AO (trees), z coverage boost, w grass translucency

    float3 G3QuatRotate(float4 q, float3 v)
    {
        float3 t = 2 * cross(q.xyz, v);
        return v + q.w * t + cross(q.xyz, t);
    }

    struct GrassVertex
    {
        float3 positionWS;
        float3 normalWS;
        float3 origin;
        float fade;
        float4 color;
        float plantHeight;   // m (mesh top x instance height), 0 = unknown
    };

    GrassVertex G3GrassVertex(float3 positionOS, float3 normalOS, uint instanceID)
    {
        GrassVertex o;
        G3GrassInst g = _G3GrassInstances[_G3GrassVisible[instanceID]];
        g.pos.y += G3ReliefGroundDelta(g.pos);   // on the drawn relief (remaster-world 14)
        float3 local = G3QuatRotate(g.rot, positionOS * float3(g.width, g.height, g.width));
        float3 p = g.pos + local;
        float d = distance(g.pos, _WorldSpaceCameraPos.xyz);
        o.fade = _G3GrassFade.y > 0 ? saturate(1 - (d - _G3GrassFade.x) * _G3GrassFade.y) : 1;
        p = G3WindGrass(p, g.pos, _G3WindScale);
        p = G3BendApply(p, g.pos, 1);   // characters bend the grass (remaster-veg, 17.5)
        o.positionWS = lerp(g.pos, p, o.fade);
        o.normalWS = normalize(G3QuatRotate(g.rot, normalOS / float3(g.width, g.height, g.width)));
        o.origin = g.pos;
        o.plantHeight = _G3GrassFade.w * g.height;
        uint c = g.color;
        o.color = float4(((c >> 16) & 255) / 255.0, ((c >> 8) & 255) / 255.0, (c & 255) / 255.0, (c >> 24) / 255.0);
        return o;
    }

    half GrassAlpha(float2 uv)
    {
        half a = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, uv).a * _BaseColor.a;
        float2 t = uv * _BaseMap_TexelSize.zw;
        float2 dx = ddx(t), dy = ddy(t);
        float mip = 0.5 * log2(max(dot(dx, dx), dot(dy, dy)));
        return a * (1 + max(0, mip) * _MipAlphaScale);
    }

    void GrassClip(float2 uv, float fade)
    {
        clip(GrassAlpha(uv) - _Cutoff);
        clip(fade - 0.001);
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
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Fog.hlsl"

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
                uint instanceID   : SV_InstanceID;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                half3 normalWS    : TEXCOORD2;
                half4 color       : TEXCOORD3;   // rgb instance tint, a fade
                half fogFactor    : TEXCOORD4;
                half3 vertexSH    : TEXCOORD5;
                half heightAbove  : TEXCOORD6;   // height above the origin / ground occlusion height
            };

            Varyings Vert(Attributes v)
            {
                Varyings o = (Varyings)0;
                GrassVertex g = G3GrassVertex(v.positionOS.xyz, v.normalOS, v.instanceID);
                o.positionCS = TransformWorldToHClip(g.positionWS);
                o.positionWS = g.positionWS;
                o.normalWS = g.normalWS;
                o.uv = TRANSFORM_TEX(v.uv, _BaseMap);
                o.color.rgb = lerp(1, g.color.rgb * _InstColorScale, _InstColorStrength);
                o.color.a = g.fade;
                o.fogFactor = ComputeFogFactor(o.positionCS.z);
                o.vertexSH = SampleSH(normalize(lerp(g.normalWS, float3(0, 1, 0), 0.5)));
                // ground occlusion over the lower part of the plant, at most _FoliageAOHeight (grass-v3: by absolute height
                // the short herbs, flowers and underbrush were darkened whole, which dulled the mix to the tall tufts' tone)
                float aoH = g.plantHeight > 0 ? min(_FoliageAOHeight, 0.5 * g.plantHeight) : _FoliageAOHeight;
                o.heightAbove = (g.positionWS.y - g.origin.y) / max(aoH, 0.01);
                return o;
            }

            half4 Frag(Varyings i, FRONT_FACE_TYPE face : FRONT_FACE_SEMANTIC) : SV_Target
            {
                half4 tex = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv);
                GrassClip(i.uv, i.color.a);
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
                input.bakedGI = i.vertexSH;
                input.shadowMask = half4(1, 1, 1, 1);

                SurfaceData s = (SurfaceData)0;
                s.albedo = tex.rgb * _BaseColor.rgb * i.color.rgb;
                s.alpha = 1;
                s.smoothness = _Smoothness;
                s.occlusion = lerp(1 - _FoliageAO, 1, saturate(i.heightAbove));
                s.normalTS = half3(0, 0, 1);
                half4 color = UniversalFragmentPBR(input, s);
                Light sun = GetMainLight(input.shadowCoord);
                color.rgb += G3GrassLight(s.albedo, input.normalWS, input.viewDirectionWS, sun.direction, sun.color,
                                            sun.shadowAttenuation, _G3V2Foliage.w);
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
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
            float3 _LightDirection;

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; float2 uv : TEXCOORD0; uint instanceID : SV_InstanceID; };
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; float fade : TEXCOORD1; };

            Varyings Vert(Attributes v)
            {
                Varyings o = (Varyings)0;
                GrassVertex g = G3GrassVertex(v.positionOS.xyz, v.normalOS, v.instanceID);
                o.positionCS = ApplyShadowClamping(TransformWorldToHClip(ApplyShadowBias(g.positionWS, g.normalWS, _LightDirection)));
                o.uv = TRANSFORM_TEX(v.uv, _BaseMap);
                o.fade = g.fade;
                return o;
            }

            half4 Frag(Varyings i) : SV_Target { GrassClip(i.uv, i.fade); return 0; }
            ENDHLSL
        }

        // Remaster TAA (Docs/remaster-world.md 11.7): wind motion at this and the previous frame's wind clock.
        Pass
        {
            Name "MotionVectors"
            Tags { "LightMode" = "MotionVectors" }
            ColorMask RG

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag

            float4 _G3MotionVectors;   // x max distance of per-object wind motion (m), 0 = none

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; float2 uv : TEXCOORD0; uint instanceID : SV_InstanceID; };
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; float fade : TEXCOORD1; float4 curCS : TEXCOORD2; float4 prevCS : TEXCOORD3; };

            Varyings Vert(Attributes v)
            {
                Varyings o = (Varyings)0;
                float3 origin = _G3GrassInstances[_G3GrassVisible[v.instanceID]].pos;
                if (distance(origin, _WorldSpaceCameraPos.xyz) > _G3MotionVectors.x) { o.positionCS = float4(0, 0, -2, 1); return o; }
                GrassVertex g = G3GrassVertex(v.positionOS.xyz, v.normalOS, v.instanceID);
                o.positionCS = TransformWorldToHClip(g.positionWS);
                o.uv = TRANSFORM_TEX(v.uv, _BaseMap);
                o.fade = g.fade;
                o.curCS = mul(_NonJitteredViewProjMatrix, float4(g.positionWS, 1));
                g3WindTimeShift = _G3WindTimePrev - _G3WindTime;
                float3 pp = G3GrassVertex(v.positionOS.xyz, v.normalOS, v.instanceID).positionWS;
                g3WindTimeShift = 0;
                o.prevCS = mul(_PrevViewProjMatrix, float4(pp, 1));
                return o;
            }

            float4 Frag(Varyings i) : SV_Target { GrassClip(i.uv, i.fade); return float4(G3WindMotion(i.curCS, i.prevCS), 0, 0); }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On ColorMask R

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; float2 uv : TEXCOORD0; uint instanceID : SV_InstanceID; };
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; float fade : TEXCOORD1; };

            Varyings Vert(Attributes v)
            {
                Varyings o = (Varyings)0;
                GrassVertex g = G3GrassVertex(v.positionOS.xyz, v.normalOS, v.instanceID);
                o.positionCS = TransformWorldToHClip(g.positionWS);
                o.uv = TRANSFORM_TEX(v.uv, _BaseMap);
                o.fade = g.fade;
                return o;
            }

            half4 Frag(Varyings i) : SV_Target { GrassClip(i.uv, i.fade); return i.positionCS.z; }
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }
            ZWrite On

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; float2 uv : TEXCOORD0; uint instanceID : SV_InstanceID; };
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; half3 normalWS : TEXCOORD1; float fade : TEXCOORD2; };

            Varyings Vert(Attributes v)
            {
                Varyings o = (Varyings)0;
                GrassVertex g = G3GrassVertex(v.positionOS.xyz, v.normalOS, v.instanceID);
                o.positionCS = TransformWorldToHClip(g.positionWS);
                o.normalWS = g.normalWS;
                o.uv = TRANSFORM_TEX(v.uv, _BaseMap);
                o.fade = g.fade;
                return o;
            }

            half4 Frag(Varyings i, FRONT_FACE_TYPE face : FRONT_FACE_SEMANTIC) : SV_Target
            {
                GrassClip(i.uv, i.fade);
                half3 n = normalize(i.normalWS);
                n = IS_FRONT_VFACE(face, n, -n);
                n = normalize(lerp(n, half3(0, 1, 0), _NormalUp));
                return half4(NormalizeNormalPerPixel(n), 0);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
