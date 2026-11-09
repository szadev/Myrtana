// G3 CP 1.75's image filters on the game camera (fix-55; G3.Unity G3ImageProcessing injects the passes, the math is
// mirrored and tested in Myrtana.Session PostLook). Ports of Materials.pak Effects/ImageProcessing:
//  0 DoF      IP_DOF:            8 Poisson taps on a disc of coc * maxBlur pixels, coc = saturate(depth * x + y)
//  1 Mask     IP_MaskHighLights: c * max(luma - threshold, 0) * multiplier on display values (quarter resolution)
//  2/3 Blur   IP_Blur:           G3's 13-tap separable blur (7 bilinear taps), horizontal / vertical
//  4 Combine  IP_HDRI BloomFx (screen blend c + b * (1 - c)) and IP_Noise NoiseFx (additive, weighted (1 - luma)^7)
// The camera colour is linear; bloom and noise work on display (sRGB) values like G3, DoF averages the linear colour.
Shader "Hidden/G3/ImageProcessing"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off ZTest Always Cull Off Blend Off

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

        float4 _G3Dof;     // x 1 / (end - start), y -start / (end - start) (view depth in m), z max blur (pixels)
        float4 _G3Bloom;   // x threshold (display luma), y multiplier
        float4 _G3Noise;   // x threshold (exponent), y intensity (0 = off), z frame offset (0..31)
        TEXTURE2D_X(_G3BloomTex);

        static const float3 kG3Luma = float3(0.3, 0.59, 0.11);

        half3 ToDisplay(half3 c) { return LinearToSRGB(max(c, 0)); }
        half3 ToLinear(half3 c) { return SRGBToLinear(max(c, 0)); }

        // IP_Blur: "optimized 13x1 using bilinear filter", offsets in target texels
        half3 G3Blur(float2 uv, float2 dir)
        {
            float2 t = _BlitTexture_TexelSize.xy * dir;
            half3 c = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv).rgb * 0.1964825501;
            c += (SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + t * 1.4117647058).rgb +
                  SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv - t * 1.4117647058).rgb) * 0.2969069646;
            c += (SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + t * 3.2941176468).rgb +
                  SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv - t * 3.2941176468).rgb) * 0.0944703978;
            c += (SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + t * 5.1764705871).rgb +
                  SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv - t * 5.1764705871).rgb) * 0.0103813624;
            return c;
        }
        ENDHLSL

        Pass
        {
            Name "G3DepthOfField"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            // eCIPImageFilterDOF's Poisson disc (unit circle), scaled by 1 / target size: pixels.
            static const float2 kPoisson[8] =
            {
                float2(-0.286573, 0.76383), float2(0.527837, -0.085868), float2(-0.040088, 0.536087),
                float2(-0.670445, -0.179949), float2(-0.419418, -0.616039), float2(0.440453, -0.639399),
                float2(-0.757088, 0.349334), float2(0.574619, 0.685879)
            };

            float CocAt(float2 uv)
            {
                float depth = LinearEyeDepth(SampleSceneDepth(uv), _ZBufferParams);
                return saturate(depth * _G3Dof.x + _G3Dof.y);
            }

            half4 Frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float2 uv = i.texcoord;
                half4 center = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv);
                float cc = CocAt(uv);
                [branch] if (cc <= 0) return center;
                float2 radius = _BlitTexture_TexelSize.xy * (cc * _G3Dof.z);
                float3 acc = 0;
                float weight = 0;
                [unroll] for (int k = 0; k < 8; k++)
                {
                    float2 u = uv + kPoisson[k] * radius;
                    float3 tapColor = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, u).rgb;
                    float tapCoc = CocAt(u);
                    // mix the tap by its own blurriness; a sharper tap nearer than the centre keeps a low weight (leaks)
                    float3 tap = lerp(center.rgb, tapColor, tapCoc);
                    float a = lerp(cc, tapCoc, tapCoc);
                    float w = a >= cc ? 1.0 : a;
                    acc += tap * w;
                    weight += w;
                }
                return half4(acc / max(weight, 1e-5), center.a);
            }
            ENDHLSL
        }

        Pass
        {
            Name "G3BloomMask"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            half4 Frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float2 uv = i.texcoord;
                float2 t = _BlitTexture_TexelSize.xy;
                // 4 bilinear taps = the 4x4 source pixels of this quarter-resolution texel
                half3 c = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + float2(-t.x, -t.y)).rgb;
                c += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + float2(t.x, -t.y)).rgb;
                c += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + float2(-t.x, t.y)).rgb;
                c += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + float2(t.x, t.y)).rgb;
                half3 d = saturate(ToDisplay(c * 0.25));
                half k = max(dot(d, kG3Luma) - _G3Bloom.x, 0) * _G3Bloom.y;
                return half4(saturate(d * k), 1);
            }
            ENDHLSL
        }

        Pass
        {
            Name "G3BloomBlurH"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            half4 Frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                return half4(G3Blur(i.texcoord, float2(1, 0)), 1);
            }
            ENDHLSL
        }

        Pass
        {
            Name "G3BloomBlurV"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            half4 Frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                return half4(G3Blur(i.texcoord, float2(0, 1)), 1);
            }
            ENDHLSL
        }

        Pass
        {
            Name "G3BloomNoise"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            // per-pixel white noise in 0..1 per channel (stands in for G3's noise texture, one texel per pixel)
            float3 Hash3(uint3 v)
            {
                v = v * 1664525u + 1013904223u;
                v.x += v.y * v.z; v.y += v.z * v.x; v.z += v.x * v.y;
                v ^= v >> 16u;
                v.x += v.y * v.z; v.y += v.z * v.x; v.z += v.x * v.y;
                return float3(v) * (1.0 / 4294967295.0);
            }

            half4 Frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float2 uv = i.texcoord;
                half4 col = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv);
                half3 d = ToDisplay(col.rgb);
                [branch] if (_G3Bloom.y > 0)
                {
                    half3 b = SAMPLE_TEXTURE2D_X(_G3BloomTex, sampler_LinearClamp, uv).rgb;
                    d += b * saturate(1 - d);
                }
                [branch] if (_G3Noise.y > 0)
                {
                    float lum = 1 - dot(saturate(d), kG3Luma);
                    float3 n = Hash3(uint3(i.positionCS.xy, (uint)_G3Noise.z)) - 0.5;
                    d = max(d + n * (pow(max(lum, 0.00001), _G3Noise.x) * _G3Noise.y), 0);
                }
                return half4(ToLinear(d), col.a);
            }
            ENDHLSL
        }
    }
}
