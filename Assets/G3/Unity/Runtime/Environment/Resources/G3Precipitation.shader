// Gothic 3 precipitation (eCPrecipitation_PS rain, snow and splashes) for URP, fix-224 (owner: thick, bright, blobby rain).
// The quad is rebuilt from the particle's custom vertex streams (G3WeatherDirector: Position, Color, UV, Center, Velocity,
// SizeXYZ): rain (_Streak 1) is a camera-facing ribbon along the velocity, _Width wide and |v|·_StreakSeconds long; snow and
// splashes are camera billboards of the particle size. Never thinner than _MinPixels on screen (a widened drop's alpha is
// its coverage, so far drops fade instead of aliasing), faded out near the camera (_NearFade: start, end, m; a fully faded
// particle collapses to nothing), lit like an unshaded sprite (ambient SH + main light + Forward+ lights by distance, as
// G3/Particle _G3_LIT), alpha-blended, fogged. Tile _Tile of the 2 × 2 RainAndSnow sheet. Math mirrored by
// G3.Weather.PrecipitationLook. Transparent: no motion vectors; the streak length (≈ one frame of fall) carries the motion
// under TAA.
Shader "G3/Precipitation"
{
    Properties
    {
        _MainTex("Texture (2 x 2 sheet)", 2D) = "white" {}
        _Tile("Sheet tile (0..3, G3 order)", Float) = 2
        _Streak("Streak along the velocity", Float) = 1
        _Width("Drop width, m (streaks)", Float) = 0.0025
        _StreakSeconds("Streak length per m/s", Float) = 0.0156
        _MinPixels("Minimum screen width, px", Float) = 1
        _NearFade("Near fade start, end (m)", Vector) = (0.75, 2, 0, 0)
        _Opacity("Opacity", Range(0, 1)) = 0.55
    }
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" "PreviewType" = "Plane" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        ZTest LEqual
        Cull Off

        Pass
        {
            Name "G3Precipitation"
            Tags { "LightMode" = "UniversalForward" }
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_fog
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _CLUSTER_LIGHT_LOOP
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                float _Tile;
                float _Streak;
                float _Width;
                float _StreakSeconds;
                float _MinPixels;
                float4 _NearFade;
                half _Opacity;
            CBUFFER_END

            // Unity packs the custom streams in order: UV.xy + Center.xy | Center.z + Velocity.xyz | SizeXYZ
            struct Attributes
            {
                float4 positionOS : POSITION;
                half4 color : COLOR;
                float4 uvCenter : TEXCOORD0;
                float4 centerVelocity : TEXCOORD1;
                float3 size : TEXCOORD2;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                half4 color : COLOR;
                float2 uv : TEXCOORD0;
                half fog : TEXCOORD1;
                float3 positionWS : TEXCOORD2;
            };

            Varyings Vert(Attributes v)
            {
                Varyings o = (Varyings)0;
                float2 corner = v.uvCenter.xy - 0.5;   // billboard corner, -0.5..0.5 (texture sheet off)
                float3 center = TransformObjectToWorld(float3(v.uvCenter.zw, v.centerVelocity.x));
                float3 velocity = TransformObjectToWorldDir(v.centerVelocity.yzw, false);
                float3 toCam = _WorldSpaceCameraPos - center;
                float dist = length(toCam);
                float depth = max(1e-3, -TransformWorldToView(center).z);
                // metres per pixel at the particle (perspective): depth · 2 / (P11 · screen height)
                float mpp = depth * 2.0 / (abs(UNITY_MATRIX_P[1][1]) * _ScreenParams.y);
                float3 camRight = UNITY_MATRIX_V[0].xyz, camUp = UNITY_MATRIX_V[1].xyz;
                float3 side, along;
                float width, len;
                float speed = length(velocity);
                if (_Streak > 0.5 && speed > 1e-3)
                {
                    float3 axis = velocity / speed;
                    side = cross(axis, toCam / max(dist, 1e-4));
                    float s = length(side);
                    side = s > 1e-3 ? side / s : camRight;
                    along = -axis;   // uv.y = 1 is the tail (up the fall)
                    width = _Width;
                    len = max(_Width, speed * _StreakSeconds);
                }
                else
                {
                    side = camRight;
                    along = camUp;
                    width = v.size.x;
                    len = v.size.y;
                }
                float drawn = max(width, _MinPixels * mpp);
                float coverage = width / drawn;
                float near = saturate((dist - _NearFade.x) / max(1e-3, _NearFade.y - _NearFade.x));
                float alpha = coverage * near * _Opacity;
                float3 positionWS = center + side * (corner.x * drawn) + along * (corner.y * max(len, drawn));
                o.positionCS = alpha > 0.002 ? TransformWorldToHClip(positionWS) : float4(0, 0, 0, 1);   // faded: collapsed
                o.positionWS = positionWS;
                o.color = half4(v.color.rgb, v.color.a * alpha);
                // tile _Tile of the 2 × 2 sheet like Unity's Texture Sheet Animation (frame 0 top-left), then the material's
                // DDS flip (_MainTex_ST, copied from G3_Precipitation.mat)
                float tile = clamp(round(_Tile), 0, 3);
                float col = fmod(tile, 2), row = floor(tile / 2);
                float2 sheet = float2((col + v.uvCenter.x) * 0.5, 1.0 - (row + 1.0 - v.uvCenter.y) * 0.5);
                o.uv = TRANSFORM_TEX(sheet, _MainTex);
                o.fog = ComputeFogFactor(o.positionCS.z);
                return o;
            }

            half3 SpriteLight(float3 positionWS, float4 positionCS)
            {
                half3 light = SampleSH(half3(0, 1, 0));
                Light mainLight = GetMainLight();
                light += mainLight.color * mainLight.distanceAttenuation;
            #if defined(_ADDITIONAL_LIGHTS) || defined(_ADDITIONAL_LIGHTS_VERTEX)
                InputData inputData = (InputData)0;
                inputData.positionWS = positionWS;
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(positionCS);
                uint lightCount = GetAdditionalLightsCount();
                LIGHT_LOOP_BEGIN(lightCount)
                    Light l = GetAdditionalLight(lightIndex, positionWS);
                    light += l.color * l.distanceAttenuation;
                LIGHT_LOOP_END
            #endif
                return min(light, half3(1, 1, 1));
            }

            half4 Frag(Varyings i) : SV_Target
            {
                half4 c = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv) * i.color;
                c.rgb *= SpriteLight(i.positionWS, i.positionCS);
                c.rgb = MixFog(c.rgb, i.fog);
                return c;
            }
            ENDHLSL
        }
    }
    Fallback Off
}
