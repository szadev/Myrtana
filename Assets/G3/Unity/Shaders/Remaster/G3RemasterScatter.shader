// Ground cover scatter (Docs/remaster-world.md section 18, remaster-veg): plants, litter cards, debris and rocks placed by
// G3GroundScatter on tiles round the camera, culled per camera by G3ScatterCull.compute and drawn with
// Graphics.RenderMeshIndirect, one call per material group with one command per (type, LOD). Instance = _G3ScatterInstances[_G3ScatterVisible[
// startInstance + SV_InstanceID]]; per type: view range, fade, thinning, wind, bend (_G3ScatterTypes). Look as G3/Remaster/GrassV2 (G3's atlases
// and instance colours, mip-aware alpha, G3 wind + remaster waves, translucency, ground occlusion), plus character bending
// (G3ScatterBend.hlsl), the relief's drawn ground (G3ReliefGround.hlsl), and an opaque "solid" mode for rocks: G3's own rock
// image world-triplanar (G3's palette), no alpha.
Shader "G3/Remaster/Scatter"
{
    Properties
    {
        _BaseMap("Diffuse (cards) / rock image (solid)", 2D) = "white" {}
        _BaseArray("Generated cards (array; _UseArray 1)", 2DArray) = "" {}
        _UseArray("Sample the card array (slice per type)", Float) = 0
        _BaseColor("Tint", Color) = (1,1,1,1)
        _Cutoff("Alpha cutoff", Range(0,1)) = 0.5
        _Smoothness("Smoothness", Range(0,1)) = 0.05
        _InstColorStrength("Instance colour strength", Range(0,1)) = 1
        _InstColorScale("Instance colour scale", Float) = 1
        _TintMode("Tint: 0 G3 instance colour, 1 brightness only", Float) = 0
        _NormalUp("Bend normals up", Range(0,1)) = 0.5
        _MipAlphaScale("Mip alpha boost", Range(0,1)) = 0.25
        _FoliageAO("Ground occlusion", Range(0, 1)) = 0.45
        _FoliageAOHeight("Ground occlusion height (m)", Float) = 0.6
        _Translucency("Translucency (x the grass value)", Range(0, 2)) = 1
        _Solid("Solid (rock: opaque, triplanar)", Float) = 0
        _TriScale("Triplanar repeats per m", Float) = 0.35
        [Enum(UnityEngine.Rendering.CullMode)] _Cull("Cull", Float) = 0
    }

    HLSLINCLUDE
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
    #include "Assets/G3/Unity/Shaders/Wind/G3Wind.hlsl"
    #include "Assets/G3/Unity/Shaders/Remaster/G3ReliefGround.hlsl"
    #include "Assets/G3/Unity/Shaders/Remaster/G3ScatterBend.hlsl"

    TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
    TEXTURE2D_ARRAY(_BaseArray); SAMPLER(sampler_BaseArray);

    CBUFFER_START(UnityPerMaterial)
        float4 _BaseMap_ST;
        float4 _BaseMap_TexelSize;
        float4 _BaseArray_TexelSize;
        half _UseArray;
        half4 _BaseColor;
        half _Cutoff;
        half _Smoothness;
        half _InstColorStrength;
        half _InstColorScale;
        half _TintMode;
        half _NormalUp;
        half _MipAlphaScale;
        half _FoliageAO;
        half _FoliageAOHeight;
        half _Translucency;
        half _Solid;
        float _TriScale;
    CBUFFER_END

    struct G3ScatterInst
    {
        float3 pos;
        float scale;
        float4 rot;
        uint color;
        uint type;
        float2 pad;
    };
    struct G3ScatterType
    {
        float4 range;    // x view range (m), y fade start (m), z LOD1 distance (m), w thin start (m)
        float4 look;     // x wind, y bend, z kind (0 plant, 1 litter, 2 solid, 3 debris), w mesh top (m at scale 1)
        float4 cull;     // x bounding radius (m at scale 1), y shadow distance (m), z card array slice, w unused
        uint4 slots;     // x slot LOD0, y slot LOD1, z flags (1 casts shadows), w unused
    };
    StructuredBuffer<G3ScatterInst> _G3ScatterInstances;
    StructuredBuffer<uint> _G3ScatterVisible;
    StructuredBuffer<G3ScatterType> _G3ScatterTypes;
    // Multi-command indirect draws (one call per material group, one command per type and LOD): each command's
    // startInstance is its first visible index. Unity binds the args and the command id (as UnityIndirect.cginc).
    ByteAddressBuffer unity_IndirectDrawArgs;
    uint unity_BaseCommandID;
    uint G3ScatterVisibleIndex(uint svInstanceID)
    {
    #if defined(SHADER_API_VULKAN) || defined(SHADER_API_WEBGPU)
        return svInstanceID;   // SV_InstanceID includes startInstance there
    #else
        return svInstanceID + unity_IndirectDrawArgs.Load(unity_BaseCommandID * 20 + 16);
    #endif
    }
    float4 _G3V2Foliage;    // w grass translucency (G3WorldV2)
    // remaster snow globals (G3SnowV2; all zero = off): snow caps on the scatter in snow regions (pass 2)
    float4 _G3Snow;         // x on, y fresh snow 0..1, z lasting cover on top faces
    float4 _G3V2Weather;    // fix-255: x wetness 0..1 (G3WorldV2, the ground's rain wetness)
    float4 _G3Snow3;        // x fresh cover amount
    float4 _G3Snow4;        // x snow albedo lift, z albedo shoulder (white)
    float4 _G3SnowRegion;   // snow-region map uv = p.xz * xy + zw (x 0 = no map)
    float4 _G3SnowTex;      // x G3 snow repeat (1/m)
    TEXTURE2D(_G3SnowRegionTex);
    TEXTURE2D(_G3SnowAlbedo);
    SamplerState g3scatter_linear_clamp_sampler;
    SamplerState g3scatter_linear_repeat_sampler;
    float4 _G3ScatterParams;   // x thin floor (kept fraction at the range end), y thin growth, z debug magenta, w unused

    float3 G3ScatterRotate(float4 q, float3 v)
    {
        float3 t = 2 * cross(q.xyz, v);
        return v + q.w * t + cross(q.xyz, t);
    }

    // Kept fraction of the thinned far field at distance d (the cull drops the rest by hash; survivors grow by 1/sqrt).
    float G3ScatterKeep(G3ScatterType t, float d)
    {
        float s = saturate((d - t.range.w) / max(t.range.x - t.range.w, 1e-3));
        return lerp(1, _G3ScatterParams.x, s);
    }

    struct ScatterVertex
    {
        float3 positionWS;
        float3 normalWS;
        float3 origin;
        float fade;
        float4 color;
        float plantHeight;
        float kind;
        float slice;     // generated cards: slice of the card array (pass 2: all cards in one array, one material)
    };

    ScatterVertex G3ScatterVertex(float3 positionOS, float3 normalOS, uint instanceID)
    {
        ScatterVertex o;
        G3ScatterInst g = _G3ScatterInstances[_G3ScatterVisible[G3ScatterVisibleIndex(instanceID)]];
        G3ScatterType t = _G3ScatterTypes[g.type];
        float originDelta = G3ReliefGroundDelta(g.pos);
        g.pos.y += originDelta;   // on the drawn relief (remaster-relief2)
        float d = distance(g.pos, _WorldSpaceCameraPos.xyz);
        float keep = G3ScatterKeep(t, d);
        float scale = g.scale * lerp(1, rsqrt(max(keep, 0.05)), _G3ScatterParams.y);
        o.fade = saturate(1 - (d - t.range.y) / max(t.range.x - t.range.y, 1e-3));
        float3 local = G3ScatterRotate(g.rot, positionOS * scale);
        float3 p = g.pos + local;
        // litter beds (2-3 m): every vertex follows the drawn relief, not only the origin (pass 2)
        if (t.look.z > 0.5 && t.look.z < 1.5) p.y += G3ReliefGroundDelta(p) - originDelta;
        if (t.look.x > 0) p = G3WindGrass(p, g.pos, t.look.x);
        p = G3BendApply(p, g.pos, t.look.y);
        o.positionWS = lerp(g.pos, p, o.fade);
        o.normalWS = normalize(G3ScatterRotate(g.rot, normalOS));
        o.origin = g.pos;
        o.plantHeight = t.look.w * scale;
        o.kind = t.look.z;
        o.slice = t.cull.z;
        uint c = g.color;
        o.color = float4(((c >> 16) & 255) / 255.0, ((c >> 8) & 255) / 255.0, (c & 255) / 255.0, (c >> 24) / 255.0);
        return o;
    }

    // the card texture: G3's atlas / own image, or a slice of the generated-card array (uv.z)
    half4 ScatterTex(float3 uv)
    {
        half4 c;
        [branch] if (_UseArray > 0.5) c = SAMPLE_TEXTURE2D_ARRAY(_BaseArray, sampler_BaseArray, uv.xy, uv.z);
        else c = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, uv.xy);
        return c;
    }

    half ScatterAlpha(float3 uv)
    {
        half a = ScatterTex(uv).a * _BaseColor.a;
        float2 tx = uv.xy * (_UseArray > 0.5 ? _BaseArray_TexelSize.zw : _BaseMap_TexelSize.zw);
        float2 dx = ddx(tx), dy = ddy(tx);
        float mip = 0.5 * log2(max(dot(dx, dx), dot(dy, dy)));
        return a * (1 + max(0, mip) * _MipAlphaScale);
    }

    // Snow cap (pass 2): the remaster snow's top-face cover (G3SnowCover, Docs section 12) on the scatter in snow regions:
    // up-facing parts of rocks, logs, stumps and the tops of plants turn to G3's snow image, lifted like the snow ground.
    half ScatterSnowCover(float3 pw, half up, half heightFrac, half kind)
    {
        if (_G3Snow.x < 0.5 || _G3SnowRegion.x <= 0) return 0;
        float region = SAMPLE_TEXTURE2D_LOD(_G3SnowRegionTex, g3scatter_linear_clamp_sampler, saturate(pw.xz * _G3SnowRegion.xy + _G3SnowRegion.zw), 0).r;
        // only inside the snow lands (the region map's soft 250 m border reaches Faring's hills: no caps there)
        float amount = saturate(_G3Snow.z + _G3Snow.y * _G3Snow3.x) * smoothstep(0.6, 0.95, region);
        if (amount <= 0.005) return 0;
        // plants and cards: their tops (height along the plant), not their card normals
        half u = kind < 0.5 ? 0.35 + 0.65 * saturate(heightFrac) : up;
        float bias = (frac(sin(dot(floor(pw.xz * 3.1), float2(12.9898, 78.233))) * 43758.5453) - 0.5) * 0.25;
        float thr = 0.95 - 0.5 * amount;
        return smoothstep(thr, thr + 0.15, u + bias * 0.3) * saturate(amount * 2) * (kind < 0.5 ? 0.85 : 1);
    }

    half3 ScatterSnowAlbedo(float3 pw)
    {
        half3 g3s = SAMPLE_TEXTURE2D(_G3SnowAlbedo, g3scatter_linear_repeat_sampler, pw.xz * max(_G3SnowTex.x, 0.01)).rgb;
        half l = max(dot(g3s, half3(0.2126, 0.7152, 0.0722)), 0.002);
        half wht = _G3Snow4.z > 0 ? _G3Snow4.z : 0.62;
        half nl = wht * (1 - exp(-max(_G3Snow4.x, 1) * l / wht));
        return g3s * (nl / l);
    }

    // fix-169: litter cards (beds 1-3 m, small litter) end in a dithered band instead of a hard alpha-test edge: the cutoff
    // is jittered per pixel and frame (interleaved gradient noise) over +-0.2 around _Cutoff, the temporal AA (on with the
    // remaster lighting) resolves it to a soft frayed rim that blends into the ground (b1021a: hard dark ovals)
    float ScatterSoftCut(float4 posCS, float kind)
    {
        if (kind < 0.5 || kind > 1.5) return _Cutoff;
        float frame = fmod(floor(_Time.y * 60), 64);
        float n = frac(52.9829189 * frac(dot(posCS.xy + frame * 5.588238, float2(0.06711056, 0.00583715))));
        return _Cutoff + (n - 0.5) * 0.4;
    }

    void ScatterClip(float3 uv, float fade, float4 posCS, float kind)
    {
        if (_Solid < 0.5) clip(ScatterAlpha(uv) - ScatterSoftCut(posCS, kind));
        clip(fade - 0.001);
    }

    // world triplanar of the rock image (solid mode)
    half3 ScatterTriplanar(float3 pw, half3 n)
    {
        float3 w = pow(abs(n), 4);
        w /= max(w.x + w.y + w.z, 1e-4);
        float3 p = pw * _TriScale;
        half3 cx = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, p.zy).rgb;
        half3 cy = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, p.xz).rgb;
        half3 cz = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, p.xy).rgb;
        return cx * w.x + cy * w.y + cz * w.z;
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
                float3 uv         : TEXCOORD0;   // z card array slice
                float3 positionWS : TEXCOORD1;
                half3 normalWS    : TEXCOORD2;
                half4 color       : TEXCOORD3;   // rgb tint, a fade
                half fogFactor    : TEXCOORD4;
                half3 vertexSH    : TEXCOORD5;
                half2 heightKind  : TEXCOORD6;   // x height above the origin / occlusion height, y kind
            };

            Varyings Vert(Attributes v)
            {
                Varyings o = (Varyings)0;
                ScatterVertex g = G3ScatterVertex(v.positionOS.xyz, v.normalOS, v.instanceID);
                o.positionCS = TransformWorldToHClip(g.positionWS);
                o.positionWS = g.positionWS;
                o.normalWS = g.normalWS;
                o.uv = float3(TRANSFORM_TEX(v.uv, _BaseMap), g.slice);
                half3 tint = _TintMode > 0.5 ? g.color.rgb * 1.992 : lerp(1, g.color.rgb * _InstColorScale, _InstColorStrength);
                o.color.rgb = tint;
                o.color.a = g.fade;
                o.fogFactor = ComputeFogFactor(o.positionCS.z);
                o.vertexSH = SampleSH(normalize(lerp(g.normalWS, float3(0, 1, 0), 0.5)));
                float aoH = g.plantHeight > 0 ? min(_FoliageAOHeight, 0.5 * g.plantHeight) : _FoliageAOHeight;
                o.heightKind = half2((g.positionWS.y - g.origin.y) / max(aoH, 0.01), g.kind);
                return o;
            }

            half4 Frag(Varyings i, FRONT_FACE_TYPE face : FRONT_FACE_SEMANTIC) : SV_Target
            {
                ScatterClip(i.uv, i.color.a, i.positionCS, i.heightKind.y);
                half3 n = normalize(i.normalWS);
                bool solid = _Solid > 0.5;
                half3 albedo;
                if (solid)
                {
                    albedo = ScatterTriplanar(i.positionWS, n);
                }
                else
                {
                    albedo = ScatterTex(i.uv).rgb;
                    n = IS_FRONT_VFACE(face, n, -n);
                    n = normalize(lerp(n, half3(0, 1, 0), _NormalUp));
                }

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
                s.albedo = albedo * _BaseColor.rgb * i.color.rgb;
                [branch] if (_G3Snow.x > 0.5)
                {
                    half snow = ScatterSnowCover(i.positionWS, solid ? n.y : normalize(i.normalWS).y, i.heightKind.x, i.heightKind.y);
                    s.albedo = lerp(s.albedo, ScatterSnowAlbedo(i.positionWS), snow);
                }
                s.alpha = 1;
                s.smoothness = _Smoothness;
                // fix-255: litter / stones / debris wet with the ground under them (the dry leaf cards lit up as yellow
                // patches on the darkened wet floor); plants keep their own look
                [branch] if (_G3V2Weather.x > 0.01 && i.heightKind.y > 0.5)
                {
                    half wet = saturate(_G3V2Weather.x) * (0.5 + 0.5 * saturate(n.y));
                    s.albedo *= lerp(1, 0.6, wet);
                    s.smoothness = lerp(s.smoothness, 0.42, wet);   // fix-258: 0.55 -> 0.42 (pebbles on the rainy path glared white towards the sun)
                }
                // plants: darker towards their base; litter/solids: a flat contact darkening
                s.occlusion = i.heightKind.y < 0.5 ? lerp(1 - _FoliageAO, 1, saturate(i.heightKind.x)) : 1 - _FoliageAO * 0.35;
                s.normalTS = half3(0, 0, 1);
                half4 color = UniversalFragmentPBR(input, s);
                if (i.heightKind.y < 0.5)
                {
                    Light sun = GetMainLight(input.shadowCoord);
                    color.rgb += G3GrassLight(s.albedo, input.normalWS, input.viewDirectionWS, sun.direction, sun.color,
                                              sun.shadowAttenuation, _G3V2Foliage.w * _Translucency);
                }
                color.rgb = MixFog(color.rgb, input.fogCoord);
                color.a = 1;
                if (_G3ScatterParams.z > 0) color.rgb = half3(1, 0, 1) * (0.4 + 0.6 * saturate(dot(n, half3(0.3, 0.9, 0.2))));   // debug: where the scatter is
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
            struct Varyings { float4 positionCS : SV_POSITION; float3 uv : TEXCOORD0; float fade : TEXCOORD1; float kind : TEXCOORD2; };

            Varyings Vert(Attributes v)
            {
                Varyings o = (Varyings)0;
                ScatterVertex g = G3ScatterVertex(v.positionOS.xyz, v.normalOS, v.instanceID);
                o.positionCS = ApplyShadowClamping(TransformWorldToHClip(ApplyShadowBias(g.positionWS, g.normalWS, _LightDirection)));
                o.uv = float3(TRANSFORM_TEX(v.uv, _BaseMap), g.slice);
                o.fade = g.fade; o.kind = g.kind;
                return o;
            }

            half4 Frag(Varyings i) : SV_Target { ScatterClip(i.uv, i.fade, i.positionCS, i.kind); return 0; }
            ENDHLSL
        }

        // Remaster TAA (Docs/remaster-world.md 11.8): wind motion at this and the previous frame's wind clock.
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
            struct Varyings { float4 positionCS : SV_POSITION; float3 uv : TEXCOORD0; float fade : TEXCOORD1; float4 curCS : TEXCOORD2; float4 prevCS : TEXCOORD3; float kind : TEXCOORD4; };

            Varyings Vert(Attributes v)
            {
                Varyings o = (Varyings)0;
                G3ScatterInst gi = _G3ScatterInstances[_G3ScatterVisible[G3ScatterVisibleIndex(v.instanceID)]];
                if (distance(gi.pos, _WorldSpaceCameraPos.xyz) > _G3MotionVectors.x || _G3ScatterTypes[gi.type].look.x <= 0 && _G3ScatterTypes[gi.type].look.y <= 0)
                {
                    o.positionCS = float4(0, 0, -2, 1);   // rigid things: URP's camera motion covers them
                    return o;
                }
                ScatterVertex g = G3ScatterVertex(v.positionOS.xyz, v.normalOS, v.instanceID);
                o.positionCS = TransformWorldToHClip(g.positionWS);
                o.uv = float3(TRANSFORM_TEX(v.uv, _BaseMap), g.slice);
                o.fade = g.fade; o.kind = g.kind;
                o.curCS = mul(_NonJitteredViewProjMatrix, float4(g.positionWS, 1));
                g3WindTimeShift = _G3WindTimePrev - _G3WindTime;
                float3 pp = G3ScatterVertex(v.positionOS.xyz, v.normalOS, v.instanceID).positionWS;
                g3WindTimeShift = 0;
                o.prevCS = mul(_PrevViewProjMatrix, float4(pp, 1));
                return o;
            }

            float4 Frag(Varyings i) : SV_Target { ScatterClip(i.uv, i.fade, i.positionCS, i.kind); return float4(G3WindMotion(i.curCS, i.prevCS), 0, 0); }
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
            struct Varyings { float4 positionCS : SV_POSITION; float3 uv : TEXCOORD0; float fade : TEXCOORD1; float kind : TEXCOORD2; };

            Varyings Vert(Attributes v)
            {
                Varyings o = (Varyings)0;
                ScatterVertex g = G3ScatterVertex(v.positionOS.xyz, v.normalOS, v.instanceID);
                o.positionCS = TransformWorldToHClip(g.positionWS);
                o.uv = float3(TRANSFORM_TEX(v.uv, _BaseMap), g.slice);
                o.fade = g.fade; o.kind = g.kind;
                return o;
            }

            half4 Frag(Varyings i) : SV_Target { ScatterClip(i.uv, i.fade, i.positionCS, i.kind); return i.positionCS.z; }
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
            struct Varyings { float4 positionCS : SV_POSITION; float3 uv : TEXCOORD0; half3 normalWS : TEXCOORD1; float fade : TEXCOORD2; float kind : TEXCOORD3; };

            Varyings Vert(Attributes v)
            {
                Varyings o = (Varyings)0;
                ScatterVertex g = G3ScatterVertex(v.positionOS.xyz, v.normalOS, v.instanceID);
                o.positionCS = TransformWorldToHClip(g.positionWS);
                o.normalWS = g.normalWS;
                o.uv = float3(TRANSFORM_TEX(v.uv, _BaseMap), g.slice);
                o.fade = g.fade; o.kind = g.kind;
                return o;
            }

            half4 Frag(Varyings i, FRONT_FACE_TYPE face : FRONT_FACE_SEMANTIC) : SV_Target
            {
                ScatterClip(i.uv, i.fade, i.positionCS, i.kind);
                half3 n = normalize(i.normalWS);
                if (_Solid < 0.5)
                {
                    n = IS_FRONT_VFACE(face, n, -n);
                    n = normalize(lerp(n, half3(0, 1, 0), _NormalUp));
                }
                return half4(NormalizeNormalPerPixel(n), 0);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
