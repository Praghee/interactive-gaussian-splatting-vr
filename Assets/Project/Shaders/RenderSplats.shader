// SPDX-License-Identifier: MIT
// InteractiveGaussianSplattingVR
//
// Procedural Gaussian-splat draw with single-pass stereo support.
// The vertex shader re-projects each splat's world centre with the per-eye VP
// (via URP's stereo-aware TransformWorldToHClip + the instancing/stereo macros),
// so one draw renders correctly to both eyes under multiview (Quest) or
// instanced (PC) stereo, and unchanged in mono. 2D axes + colour + sort order
// come from the centre eye (computed once in CSCalcView). Quest-safe.

Shader "GaussianSplatVR/RenderSplats"
{
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }
        Pass
        {
            ZWrite On                    // stamp the cloud's front-surface depth (nearest visible splat)
            ZTest Always                 // soft occlusion runs in the fragment; splats aren't depth-tested vs each other
            Cull Off
            Blend One OneMinusSrcAlpha   // premultiplied over

HLSLPROGRAM
#pragma vertex vert
#pragma fragment frag
#pragma target 4.5
#pragma require compute
#pragma multi_compile_instancing        // brings in the stereo-instancing / multiview variants

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

struct SplatViewData { float4 pos; float2 axis1; float2 axis2; uint2 color; };  // pos.xyz = world centre, pos.w = valid
StructuredBuffer<SplatViewData> _SplatViewData;
StructuredBuffer<uint> _OrderBuffer;
float4 _SplatScreenParams;   // (eye width, eye height, _, _)
float _SplatSoftFade;        // soft-depth fade distance in metres (0 => hard occlusion)
float _SplatColorToLinear;   // 1 => convert splat colour sRGB(gamma)->linear before blending

struct v2f
{
    half4 col : COLOR0;
    float2 pos : TEXCOORD0;
    float splatEye : TEXCOORD1;           // splat centre eye-space depth (for scene-depth test)
    float4 vertex : SV_POSITION;
    UNITY_VERTEX_OUTPUT_STEREO           // routes the fragment to the correct eye slice
};

// two triangles of a quad, corners in [-1,1]
static const float2 kCorners[6] = { float2(-1,-1), float2(1,-1), float2(-1,1), float2(1,-1), float2(1,1), float2(-1,1) };

v2f vert(uint vtxID : SV_VertexID, uint instID : SV_InstanceID)
{
    v2f o = (v2f)0;

    // The splat index comes straight from the procedural instance id. Only
    // *instanced* stereo packs the eye into it (multiview keeps it separate and
    // sets unity_StereoEyeIndex itself; mono is a no-op).
    uint splatIndex = instID;
#if defined(UNITY_STEREO_INSTANCING_ENABLED)
    unity_StereoEyeIndex = instID % 2u;
    splatIndex = instID / 2u;
#endif
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);   // routes fragment to the unity_StereoEyeIndex slice

    SplatViewData view = _SplatViewData[_OrderBuffer[splatIndex]];
    if (view.pos.w < 0.5) { o.vertex = asfloat(0x7fc00000); return o; }   // culled by compute

    float4 clip = TransformWorldToHClip(view.pos.xyz);                    // per-eye projection
    if (clip.w <= 0) { o.vertex = asfloat(0x7fc00000); return o; }        // behind this eye -> discard primitive
    o.splatEye = -TransformWorldToView(view.pos.xyz).z;                   // eye-space depth of the splat centre

    o.col = half4(f16tof32(view.color.x >> 16), f16tof32(view.color.x & 0xFFFF),
                  f16tof32(view.color.y >> 16), f16tof32(view.color.y & 0xFFFF));
    float2 quadPos = kCorners[vtxID] * 2.0;   // +/-2 sigma extent
    o.pos = quadPos;
    float2 deltaScreen = (quadPos.x * view.axis1 + quadPos.y * view.axis2) * 2.0 / _SplatScreenParams.xy;
    o.vertex = clip;
    o.vertex.xy += deltaScreen * clip.w;
    return o;
}

half4 frag(v2f i) : SV_Target
{
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);

    // Occlude against opaque scene geometry (requires URP Depth Texture enabled).
    float2 screenUV = i.vertex.xy / _ScaledScreenParams.xy;
    float sceneEye = LinearEyeDepth(SampleSceneDepth(screenUV), _ZBufferParams);

    // Soft depth: fade smoothly as the splat approaches the surface; 0 when behind.
    half depthFade = (half)saturate((sceneEye - i.splatEye) / max(_SplatSoftFade, 1e-4));

    half alpha = exp(-dot(i.pos, i.pos)) * i.col.a * depthFade;
    if (alpha < 1.0/255.0) discard;

    // Splat colours are gamma (sRGB) trained. In a Linear project the pipeline
    // sRGB-encodes on output, so convert to linear here to avoid double-gamma
    // (the washed-out / foggy look). Toggle via the feature for A/B.
    half3 rgb = i.col.rgb;
    if (_SplatColorToLinear > 0.5h) rgb = pow(max(rgb, 0.0h), 2.2h);

    return half4(rgb * alpha, alpha);   // premultiplied
}
ENDHLSL
        }
    }
}
