// Gothic 3 magic barrier (the dome over Vengard, gCMagicBarrier_PS; Docs/formats-world.md §7.6, fix-229).
// The shipped material G3_Special_Magic_Barrier_01 is eCShaderDefault with BlendMode 7 Brighten (SrcAlpha/One):
//   Diffuse          = G3_Special_Magic_Barrier_04 (dark blue energy swirls)
//   SelfIllumination = G3_Special_Magic_Barrier_05 (blue cloud, the glow you see from outside the walls)
//   Opacity          = Add(Barrier_04.r, Barrier_05)
// The URP Simple Lit stand-in only lit the dark swirl map, so the dome added almost nothing to the frame (invisible).
// This keeps the G3 terms and adds a restrained remaster on top:
//  - two slowly drifting copies of the cloud map + the swirl map (G3's graph carries unused oscillator / rotator nodes)
//  - Fresnel edge glow (the dome outline reads against the sky from far away)
//  - a slow pulse, a soft depth fade and a thin glow where the shell meets terrain and walls (_CameraDepthTexture)
//  - additive, no depth write, partial fog (a 360 m dome stays readable through the haze, as in G3)
// The mesh holds two shells with opposite normals (outer + inner), so back-face culling shows the same layers as G3.
// Tag G3FarCull=Never: G3FarCull never hides it (huge, thin, transparent: its small-mesh rule must not apply).
Shader "G3/MagicBarrier"
{
    Properties
    {
        [Header(G3 maps)]
        _CloudMap("Glow clouds (SelfIllumination: G3_Special_Magic_Barrier_05)", 2D) = "white" {}
        _StreakMap("Energy swirls (Diffuse: G3_Special_Magic_Barrier_04)", 2D) = "black" {}
        [HDR] _Tint("Tint", Color) = (1, 1, 1, 1)
        _Intensity("Intensity", Range(0, 8)) = 0.6

        [Header(Motion)]
        _CloudFlow("Cloud drift (layer A uv/s, layer B uv/s)", Vector) = (0.006, 0.004, -0.005, 0.007)
        _StreakFlow("Swirl drift (uv/s) + tiling (zw)", Vector) = (0.004, -0.01, 0.5, 0.5)
        _StreakStrength("Swirl strength", Range(0, 4)) = 1
        _Pulse("Pulse amount", Range(0, 1)) = 0.18
        _PulseSpeed("Pulse speed (rad/s)", Float) = 0.7

        [Header(Edges)]
        _FresnelPower("Fresnel power", Range(0.5, 8)) = 3
        _FresnelStrength("Fresnel strength", Range(0, 6)) = 1.5
        _DepthFade("Soft fade at geometry (m, 0 = off)", Float) = 4
        _ContactWidth("Contact glow width (m)", Float) = 1.5
        _ContactGlow("Contact glow", Range(0, 4)) = 0.8
        _FogAmount("Fog amount (0 = none, 1 = full URP fog)", Range(0, 1)) = 0.45

        [Header(Light level)]
        _LightFollow("Follow the ambient level (0 = constant glow)", Range(0, 1)) = 1
        _AmbientRef("Ambient luminance of the full glow, G3 day 0.5", Float) = 0.45
        _NightLevel("Glow left in the darkest night", Range(0, 1)) = 0.1
        _Desaturate("Glow desaturation (0 = G3's ultramarine)", Range(0, 1)) = 0.6
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True" "G3FarCull" = "Never"
        }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }
            Blend One One
            ZWrite Off
            Cull Back

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"   // fix-258: SampleSH

            TEXTURE2D(_CloudMap);
            TEXTURE2D(_StreakMap);
            SAMPLER(sampler_linear_repeat);

            CBUFFER_START(UnityPerMaterial)
                float4 _CloudMap_ST;
                float4 _StreakMap_ST;
                half4 _Tint;
                half _Intensity;
                float4 _CloudFlow;
                float4 _StreakFlow;
                half _StreakStrength;
                half _Pulse;
                float _PulseSpeed;
                half _FresnelPower;
                half _FresnelStrength;
                float _DepthFade;
                float _ContactWidth;
                half _ContactGlow;
                half _FogAmount;
                half _LightFollow;
                float _AmbientRef;
                half _NightLevel;
                half _Desaturate;
            CBUFFER_END

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
                float3 normalWS   : TEXCOORD2;
                float4 screenPos  : TEXCOORD3;
                float  fogFactor  : TEXCOORD4;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vert(Attributes v)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                VertexPositionInputs p = GetVertexPositionInputs(v.positionOS.xyz);
                o.positionCS = p.positionCS;
                o.positionWS = p.positionWS;
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                o.uv = v.uv;
                o.screenPos = ComputeScreenPos(p.positionCS);
                o.fogFactor = ComputeFogFactor(p.positionCS.z);
                return o;
            }

            half4 Frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float t = _Time.y;
                float2 uvCloud = i.uv * _CloudMap_ST.xy + _CloudMap_ST.zw;
                half4 cloudA = SAMPLE_TEXTURE2D(_CloudMap, sampler_linear_repeat, uvCloud + t * _CloudFlow.xy);
                half4 cloudB = SAMPLE_TEXTURE2D(_CloudMap, sampler_linear_repeat, uvCloud * 1.37 + float2(0.31, 0.17) + t * _CloudFlow.zw);
                half4 cloud = (cloudA + cloudB) * 0.5h;
                float2 uvStreak = i.uv * _StreakFlow.zw + t * _StreakFlow.xy;
                half4 streak = SAMPLE_TEXTURE2D(_StreakMap, sampler_linear_repeat, uvStreak);

                // G3: Opacity = Add(Barrier_04.r, Barrier_05); the colour = self-illumination (+ the swirl map).
                half alpha = saturate(streak.r + cloud.a);
                half3 color = cloud.rgb + streak.rgb * _StreakStrength;

                // Fresnel on both shells (the inner shell's normals face inwards): brighter where the view grazes it.
                float3 viewWS = normalize(GetWorldSpaceViewDir(i.positionWS));
                half ndv = saturate(abs(dot(normalize(i.normalWS), viewWS)));
                half rim = pow(1.0h - ndv, _FresnelPower) * _FresnelStrength;

                half pulse = 1.0h + _Pulse * sin(t * _PulseSpeed + i.positionWS.y * 0.04 + cloud.b * 3.0);

                // Soft intersection with terrain / walls plus a thin glowing seam.
                half fade = 1.0h, contact = 0.0h;
                if (_DepthFade > 0)
                {
                    float2 suv = i.screenPos.xy / i.screenPos.w;
                    float sceneEye = LinearEyeDepth(SampleSceneDepth(suv), _ZBufferParams);
                    float diff = sceneEye - i.screenPos.w;
                    fade = saturate(diff / _DepthFade);
                    contact = (1.0h - saturate(diff / max(_ContactWidth, 0.01))) * _ContactGlow;
                }

                half3 rgb = color * alpha * (1.0h + rim) * pulse * fade + cloud.rgb * contact;
                rgb *= _Tint.rgb * _Intensity;

                // fix-258: the additive glow follows the scene's light level (G3's ambient, the weather keys) and is a little
                // less saturated: at a constant level the ultramarine dome outshone G3's night sky behind it (Vengard 23 h:
                // a saturated blue sky, (47, 59, 120) vs Ardea's navy (42, 55, 78)) and tinted the warm dusk sky lavender
                // (18 h); by day (ambient >= _AmbientRef) it is unchanged in level
                const half3 lumW = half3(0.2126h, 0.7152h, 0.0722h);
                half la = dot(max(SampleSH(half3(0, 1, 0)), 0), lumW);
                half lk = saturate(la / max(_AmbientRef, 1e-3));
                half lvl = lerp(_NightLevel, 1.0h, lk * sqrt(lk));
                half lr = dot(rgb, lumW);
                rgb = lerp(rgb, lr.xxx * half3(0.85h, 1.0h, 1.3h), _Desaturate * _LightFollow) * lerp(1.0h, lvl, _LightFollow);

                // Additive: fog fades the glow towards black, partly (a far dome stays readable).
                half3 fogged = MixFogColor(rgb, half3(0, 0, 0), i.fogFactor);
                rgb = lerp(rgb, fogged, _FogAmount);
                return half4(max(rgb, 0), 1);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
