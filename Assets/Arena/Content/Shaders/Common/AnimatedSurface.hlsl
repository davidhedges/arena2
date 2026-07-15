// Reusable building blocks for animated environment surfaces (lava, poison,
// blood, arcane floors, ...). Pure functions only: no material CBUFFER state,
// no texture declarations, and no knowledge of any specific surface type.
// Include after URP's Core.hlsl (needs SAMPLE_TEXTURE2D and TWO_PI).
#ifndef ARENA_ANIMATED_SURFACE_INCLUDED
#define ARENA_ANIMATED_SURFACE_INCLUDED

// ---------------------------------------------------------------- UV source
// Selects mesh UVs or world-space planar XZ (1 world unit = 1 UV) and applies
// tiling/offset. useWorldSpace is a material float (0 or 1); the lerp keeps
// the choice branchless and variant-free.
float2 AnimatedSurfaceUV(float2 meshUV, float3 positionWS, half useWorldSpace, float4 tilingOffset)
{
    float2 source = lerp(meshUV, positionWS.xz, useWorldSpace);
    return source * tilingOffset.xy + tilingOffset.zw;
}

// ------------------------------------------------------------------ UV flow
// Constant-velocity scroll. Direction magnitude scales speed, so (1, 0.25)
// drifts mostly along +U with a slight +V component.
float2 FlowUV(float2 uv, float2 direction, float speed, float time)
{
    return uv + direction * (speed * time);
}

// Rotates UVs around the origin. sinCos = (sin(angle), cos(angle)); callers
// with a constant angle should sincos() once and reuse. Rotating one layer of
// a multi-layer surface keeps the layers' tiling grids from ever aligning,
// which is the cheapest way to hide texture repetition.
float2 RotateUV(float2 uv, float2 sinCos)
{
    return float2(uv.x * sinCos.y - uv.y * sinCos.x,
                  uv.x * sinCos.x + uv.y * sinCos.y);
}

// --------------------------------------------------------- Noise distortion
// Builds an evolving 2D UV offset from two decorrelated scrolling samples of
// a single-channel noise texture. The samples drift in fixed opposing
// directions at slightly different rates and scales so the offset field never
// settles or visibly repeats; applied to layer UVs it turns straight scrolling
// into internal churn. Also outputs the first raw noise value so callers can
// reuse it (pulse phase, blend jitter) without paying for another sample.
// NOTE: the effective warp is amount * the noise texture's actual contrast.
// A soft low-contrast source (e.g. a height map spanning ~0.2..0.5) needs
// amount several times higher than a full-range 0..1 noise would.
float2 NoiseDistortionUVOffset(
    TEXTURE2D_PARAM(noiseMap, noiseSampler),
    float2 noiseUV, float speed, float amount, float time,
    out half rawNoise)
{
    float2 uvA = noiseUV + float2(0.31, -0.71) * (speed * time);
    float2 uvB = noiseUV * 1.37 + float2(-0.83, 0.29) * (speed * time * 1.23);
    half noiseA = SAMPLE_TEXTURE2D(noiseMap, noiseSampler, uvA).r;
    half noiseB = SAMPLE_TEXTURE2D(noiseMap, noiseSampler, uvB).r;
    rawNoise = noiseA;
    return (float2(noiseA, noiseB) - 0.5) * amount;
}

// --------------------------------------------------------- Flow map advection
// Valve-style ping-pong flow. A flow map stores a per-texel 2D direction in RG
// (0..1 encoded, 0.5 = still; sample it as LINEAR data, never sRGB). UVs
// advect along that direction so every point moves its own way, and because
// advection cannot run forever without smearing, content is sampled at two
// half-period-offset phases that each snap back to zero while their crossfade
// weight is zero. Sample content at both phase UVs and lerp by blendB
// (FlowedSampleRGB does this for a color texture).
half2 DecodeFlowVector(half2 encodedRG, half strength)
{
    return (encodedRG * 2.0 - 1.0) * strength;
}

void FlowMapPhases(float2 uv, half2 flowVector, float cycleSpeed, float time,
                   out float2 uvA, out float2 uvB, out half blendB)
{
    float phaseA = frac(time * cycleSpeed);
    float phaseB = frac(time * cycleSpeed + 0.5);
    uvA = uv - flowVector * phaseA;
    uvB = uv - flowVector * phaseB;
    blendB = abs(2.0 * phaseA - 1.0); // 1 when phase A snaps back, 0 mid-cycle
}

half3 FlowedSampleRGB(TEXTURE2D_PARAM(tex, texSampler), float2 uvA, float2 uvB, half blendB)
{
    return lerp(SAMPLE_TEXTURE2D(tex, texSampler, uvA).rgb,
                SAMPLE_TEXTURE2D(tex, texSampler, uvB).rgb, blendB);
}

// ---------------------------------------------------------- Dual layer blend
// Weight of layer B given a per-layer scalar "height" (any brightness, height,
// or mask signal the caller chooses). balance 0..1 shifts the mix toward A or
// B; sharpness hardens the transition (0 degenerates to a plain balance mix).
half DualLayerWeight(half heightA, half heightB, half balance, half sharpness)
{
    return saturate((heightB - heightA) * sharpness + balance);
}

// Normalized lerp of two tangent-space normals by the dual-layer weight.
half3 BlendLayerNormals(half3 normalA, half3 normalB, half weight)
{
    return normalize(lerp(normalA, normalB, weight));
}

// ------------------------------------------------------------ Emission pulse
// Sine brightness multiplier centered on 1, clamped at 0. phase (0..1) offsets
// the wave spatially -- feed it a noise sample so the pulse travels across the
// surface instead of throbbing globally. amount 0 = constant 1.
half EmissionPulse(float time, half speed, half amount, half phase)
{
    return max(1.0 + amount * sin(TWO_PI * (time * speed + phase)), 0.0);
}

#endif // ARENA_ANIMATED_SURFACE_INCLUDED
