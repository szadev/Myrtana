// Vertex wrappers for the G3/WindLit passes: displace the object-space position by the G3 tree wind, then run
// the stock URP vertex function. Include after G3WindLitInput.hlsl and the pass include.
#ifndef G3_WIND_LIT_PASSES_INCLUDED
#define G3_WIND_LIT_PASSES_INCLUDED

#include "Assets/G3/Unity/Shaders/Wind/G3Wind.hlsl"

float4 G3WindLitPositionOS(float4 positionOS)
{
    float3 positionWS = TransformObjectToWorld(positionOS.xyz);
    float3 displaced = G3WindTree(positionWS, G3WindObjectOrigin(), _G3WindHeight * G3WindObjectScale(),
                                  _G3WindAmount, _G3WindFlutter);
    return float4(TransformWorldToObject(displaced), positionOS.w);
}

// POS: object-space position field of the pass' Attributes (positionOS; "position" in DepthOnlyPass.hlsl).
#define G3_WIND_VERTEX(NAME, BASE, POS) \
    Varyings NAME(Attributes input) \
    { \
        UNITY_SETUP_INSTANCE_ID(input); \
        input.POS = G3WindLitPositionOS(input.POS); \
        return BASE(input); \
    }

#endif
