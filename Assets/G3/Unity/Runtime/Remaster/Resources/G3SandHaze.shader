// remaster-sand (sand-2, Docs/remaster-world.md 22.7): daytime heat haze over Varant (G3SandHaze.cs). A full-screen copy before
// the remaster grade: pixels of distant ground near the horizon are re-sampled at a small offset from two octaves of rising
// value noise (hot air columns rising and wobbling), ~1-2 px at full strength. Faded in from 25 m, out by ~900 m (and on the
// sky), only for view rays within ~10 degrees of the horizontal, and never across a near silhouette (an offset that lands on
// something nearer than 25 m is dropped: no halo of the hero or of near grass). Mirrors nothing; strength from SandV2.HeatHaze.
Shader "Hidden/G3/Remaster/SandHaze"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off ZTest Always Cull Off Blend Off

        Pass
        {
            Name "G3SandHaze"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            float4 _G3SandHaze;   // x strength 0..1, y time (s)

            float HazeHash(float2 p) { return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453); }

            float HazeNoise(float2 p)
            {
                float2 i = floor(p), f = p - i;
                float2 u = f * f * (3 - 2 * f);
                return lerp(lerp(HazeHash(i), HazeHash(i + float2(1, 0)), u.x), lerp(HazeHash(i + float2(0, 1)), HazeHash(i + float2(1, 1)), u.x), u.y);
            }

            float HazeDist(float2 uv, out float3 dir)
            {
                float raw = SampleSceneDepth(uv);
            #if UNITY_REVERSED_Z
                float depth = raw;
                bool sky = raw <= 1e-6;
            #else
                float depth = lerp(UNITY_NEAR_CLIP_VALUE, 1, raw);
                bool sky = raw >= 1 - 1e-6;
            #endif
                float3 pw = ComputeWorldSpacePosition(uv, depth, UNITY_MATRIX_I_VP);
                float3 d = pw - GetCameraPositionWS();
                float len = length(d);
                dir = d / max(len, 1e-4);
                return sky ? 1e5 : len;
            }

            half4 Frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float2 uv = i.texcoord;
                float3 dir;
                float dist = HazeDist(uv, dir);
                float k = _G3SandHaze.x * smoothstep(25, 80, dist) * (1 - smoothstep(450, 900, dist)) * (1 - smoothstep(0.06, 0.18, abs(dir.y)));
                [branch] if (k < 0.01) return SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv);
                float t = _G3SandHaze.y;
                float2 px = uv * _ScreenParams.xy;
                // rising columns: stretched vertically, moving up; a finer octave wobbles sideways
                float n1 = HazeNoise(float2(px.x / 9.0, px.y / 26.0 - t * 1.7));
                float n2 = HazeNoise(float2(px.x / 4.0 + 17.0, px.y / 11.0 - t * 3.1));
                float2 o = float2((n2 - 0.5) * 1.2, (n1 - 0.5) * 2.0 + (n2 - 0.5) * 0.6) * 1.8 * k / _ScreenParams.xy;
                float3 d2;
                if (HazeDist(uv + o, d2) < 25) o = 0;   // never pull a near silhouette into the distance
                return SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + o);
            }
            ENDHLSL
        }
    }
}
