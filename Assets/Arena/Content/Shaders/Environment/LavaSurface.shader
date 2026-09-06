// Animated molten lava with flow-map advection and spatially pulsing emission.
// The default blends two scrolling layers; the arena opts into randomized,
// smoothly overlapping patches to hide repetition across its large lava lake.
// Composes Common/AnimatedSurface.hlsl; lighting/shadows/depth match URP Lit.
Shader "Arena/Environment/LavaSurface"
{
    Properties
    {
        [Header(Surface)]
        [MainTexture] _BaseMap("Albedo", 2D) = "white" {}
        [MainColor] _BaseColor("Tint", Color) = (1, 1, 1, 1)
        _Smoothness("Smoothness", Range(0, 1)) = 0.35
        _Metallic("Metallic", Range(0, 1)) = 0

        [Space(12)]
        [Header(Flow)]
        [NoScaleOffset] _FlowMap("Flow Map (RG)", 2D) = "gray" {}
        _FlowMapTiling("Flow Map Tiling", Float) = 0.2
        _FlowStrength("Flow Strength", Range(0, 1)) = 0.15
        _FlowCycleSpeed("Flow Cycle Speed", Float) = 0.1
        _FlowDirection1("Layer 1 Drift Direction (XY)", Vector) = (1, 0.25, 0, 0)
        _FlowSpeed1("Layer 1 Drift Speed", Float) = 0.015
        _FlowDirection2("Layer 2 Drift Direction (XY)", Vector) = (-0.5, -0.85, 0, 0)
        _FlowSpeed2("Layer 2 Drift Speed", Float) = 0.01
        _Layer2UVScale("Layer 2 UV Scale", Float) = 0.79
        _Layer2Rotation("Layer 2 Rotation (Degrees)", Range(0, 90)) = 54
        _LayerBalance("Layer Balance", Range(0, 1)) = 0.5
        _LayerBlendSharpness("Layer Blend Sharpness", Range(0, 8)) = 4

        [Space(12)]
        [Header(Distortion)]
        [NoScaleOffset] _NoiseMap("Noise (R)", 2D) = "gray" {}
        _NoiseTiling("Noise Tiling", Float) = 0.4
        _DistortionAmount("Distortion Amount", Range(0, 1)) = 0.1
        _DistortionSpeed("Distortion Speed", Float) = 0.06

        [Space(12)]
        [Header(Macro Variation)]
        _MacroVariation("Macro Variation", Range(0, 1)) = 0.4
        _MacroTiling("Macro Tiling", Float) = 0.07

        [Space(12)]
        [Header(Normals)]
        [NoScaleOffset][Normal] _NormalMap("Normal Map", 2D) = "bump" {}
        _NormalStrength("Normal Strength", Range(0, 2)) = 1

        [Space(12)]
        [Header(Emission)]
        [NoScaleOffset] _EmissionMap("Emission Map", 2D) = "white" {}
        [HDR] _EmissionColor("Emission Color", Color) = (1, 1, 1, 1)
        _EmissionIntensity("Emission Intensity", Float) = 2.5
        _EmissionPulseAmount("Pulse Amount", Range(0, 1)) = 0.25
        _EmissionPulseSpeed("Pulse Speed", Float) = 0.25

        [Space(12)]
        [Header(Advanced)]
        [ToggleUI] _WorldSpaceUV("World Space UV (XZ, 1 unit = 1 UV)", Float) = 0
        [ToggleUI] _TilingBreakup("Randomize Lava Patches", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "UniversalMaterialType" = "Lit"
            "Queue" = "Geometry"
            "IgnoreProjector" = "True"
        }
        LOD 300

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        // Declared once for every pass so the shader stays SRP-batcher
        // compatible (UnityPerMaterial layout must match across passes).
        CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            half4 _BaseColor;
            half4 _EmissionColor;
            float4 _FlowDirection1;
            float4 _FlowDirection2;
            float _FlowMapTiling;
            float _FlowCycleSpeed;
            half _FlowStrength;
            float _FlowSpeed1;
            float _FlowSpeed2;
            float _Layer2UVScale;
            float _Layer2Rotation;
            float _NoiseTiling;
            float _DistortionAmount;
            float _DistortionSpeed;
            float _MacroTiling;
            half _MacroVariation;
            half _LayerBalance;
            half _LayerBlendSharpness;
            half _NormalStrength;
            half _EmissionIntensity;
            half _EmissionPulseAmount;
            half _EmissionPulseSpeed;
            half _Smoothness;
            half _Metallic;
            half _WorldSpaceUV;
            half _TilingBreakup;
        CBUFFER_END
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            ZWrite On
            Cull Back

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex LavaVertex
            #pragma fragment LavaFragment

            // Universal Pipeline keywords: the realtime lighting/shadow/decal
            // set this project's Forward+ renderer actually uses (reflection
            // probe blending/box/atlas and DBuffer decals are enabled in
            // PC_RPAsset/PC_Renderer). Deliberately omitted to keep the variant
            // space small (add back if the project adopts them): lightmaps,
            // light cookies, light layers, shadowmask.
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_ATLAS
            #pragma multi_compile_fragment _ _DBUFFER_MRT1 _DBUFFER_MRT2 _DBUFFER_MRT3
            #pragma multi_compile_fog
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "../Common/AnimatedSurface.hlsl"

            TEXTURE2D(_BaseMap);     SAMPLER(sampler_BaseMap);
            TEXTURE2D(_NormalMap);   SAMPLER(sampler_NormalMap);
            TEXTURE2D(_EmissionMap); SAMPLER(sampler_EmissionMap);
            TEXTURE2D(_NoiseMap);    SAMPLER(sampler_NoiseMap);
            TEXTURE2D(_FlowMap);     SAMPLER(sampler_FlowMap);

            // A triangular lattice gives three overlapping texture patches.
            // Each lattice vertex owns one stable random rotation/offset, so
            // neighbouring triangles agree along their shared edge. Sharpened
            // barycentric weights retain detail through the blends.
            void LavaPatchTriangle(float2 uv, out float2 a, out float2 b, out float2 c, out float3 weights)
            {
                float2 lattice = float2(uv.x - uv.y * 0.577350269, uv.y * 1.154700538) * 0.65;
                float2 cell = floor(lattice);
                float2 f = frac(lattice);
                if (f.x + f.y < 1.0)
                {
                    a = cell;
                    b = cell + float2(1, 0);
                    c = cell + float2(0, 1);
                    weights = float3(1.0 - f.x - f.y, f.x, f.y);
                }
                else
                {
                    a = cell + 1.0;
                    b = cell + float2(0, 1);
                    c = cell + float2(1, 0);
                    weights = float3(f.x + f.y - 1.0, 1.0 - f.x, 1.0 - f.y);
                }
                weights *= weights;
                weights *= weights;
                weights /= dot(weights, float3(1, 1, 1));
            }

            void LavaPatchTransform(float2 id, out float2 rotation, out float2 offset)
            {
                // Float-only hash, evaluated in full precision on both Metal
                // and mobile. No dependence on the camera or animation phase.
                float3 h = frac(float3(id.x, id.y, id.x) * float3(0.1031, 0.1030, 0.0973));
                h += dot(h, h.yxz + 33.33);
                h = frac((h.xxy + h.yzz) * h.zyx);
                sincos(h.x * TWO_PI, rotation.x, rotation.y);
                offset = h.yz * 17.0;
            }

            struct LavaPatchSample
            {
                half3 albedo;
                half3 emission;
                half3 normal;
            };

            LavaPatchSample SampleLavaPatch(
                float2 id, float2 uvA, float2 uvB, float4 gradientX, float4 gradientY, half flowBlend)
            {
                float2 rotation, offset;
                LavaPatchTransform(id, rotation, offset);
                uvA = RotateUV(uvA, rotation) + offset;
                uvB = RotateUV(uvB, rotation) + offset;
                float2 dxA = RotateUV(gradientX.xy, rotation);
                float2 dxB = RotateUV(gradientX.zw, rotation);
                float2 dyA = RotateUV(gradientY.xy, rotation);
                float2 dyB = RotateUV(gradientY.zw, rotation);

                // Explicit gradients exclude the discontinuous random offset
                // from mip selection; otherwise cell edges blur or shimmer.
                LavaPatchSample result;
                result.albedo = lerp(
                    SAMPLE_TEXTURE2D_GRAD(_BaseMap, sampler_BaseMap, uvA, dxA, dyA).rgb,
                    SAMPLE_TEXTURE2D_GRAD(_BaseMap, sampler_BaseMap, uvB, dxB, dyB).rgb, flowBlend);
                result.emission = lerp(
                    SAMPLE_TEXTURE2D_GRAD(_EmissionMap, sampler_EmissionMap, uvA, dxA, dyA).rgb,
                    SAMPLE_TEXTURE2D_GRAD(_EmissionMap, sampler_EmissionMap, uvB, dxB, dyB).rgb, flowBlend);
                result.normal = BlendLayerNormals(
                    UnpackNormalScale(SAMPLE_TEXTURE2D_GRAD(_NormalMap, sampler_NormalMap, uvA, dxA, dyA), _NormalStrength),
                    UnpackNormalScale(SAMPLE_TEXTURE2D_GRAD(_NormalMap, sampler_NormalMap, uvB, dxB, dyB), _NormalStrength), flowBlend);
                result.normal.xy = RotateUV(result.normal.xy, float2(-rotation.x, rotation.y));
                return result;
            }

            half SampleLavaMacroPatch(float2 id, float2 uv, float2 dx, float2 dy)
            {
                float2 rotation, offset;
                LavaPatchTransform(id, rotation, offset);
                return SAMPLE_TEXTURE2D_GRAD(_NoiseMap, sampler_NoiseMap,
                    RotateUV(uv, rotation) + offset, RotateUV(dx, rotation), RotateUV(dy, rotation)).r;
            }

            half LavaMacroNoise(float2 uv)
            {
                half noise = 0.0h;
                UNITY_BRANCH
                if (_TilingBreakup < 0.5h)
                {
                    noise = SAMPLE_TEXTURE2D(_NoiseMap, sampler_NoiseMap, uv).r;
                }
                else
                {
                    float2 a = 0.0, b = 0.0, c = 0.0;
                    float3 weights = 0.0;
                    LavaPatchTriangle(uv, a, b, c, weights);
                    float2 dx = ddx(uv), dy = ddy(uv);
                    // Randomize the broad brightness field too: leaving it tiled
                    // would preserve the large repeating patches in distant views.
                    // Sampling the same noise retains its existing brightness range.
                    noise = SampleLavaMacroPatch(a, uv, dx, dy) * weights.x
                        + SampleLavaMacroPatch(b, uv, dx, dy) * weights.y
                        + SampleLavaMacroPatch(c, uv, dx, dy) * weights.z;
                }
                return noise;
            }

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float4 tangentOS  : TANGENT;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS  : SV_POSITION;
                float2 uv          : TEXCOORD0;
                float3 positionWS  : TEXCOORD1;
                half3 normalWS     : TEXCOORD2;
                half4 tangentWS    : TEXCOORD3; // xyz = tangent, w = bitangent sign
                half fogFactor     : TEXCOORD4;
                #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
                float4 shadowCoord : TEXCOORD5;
                #endif
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings LavaVertex(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                VertexPositionInputs vertexInput = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs normalInput = GetVertexNormalInputs(input.normalOS, input.tangentOS);

                output.positionCS = vertexInput.positionCS;
                output.positionWS = vertexInput.positionWS;
                output.uv = input.uv;
                output.normalWS = normalInput.normalWS;
                output.tangentWS = half4(normalInput.tangentWS, input.tangentOS.w * GetOddNegativeScale());
                output.fogFactor = ComputeFogFactor(vertexInput.positionCS.z);
                #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
                output.shadowCoord = GetShadowCoord(vertexInput);
                #endif
                return output;
            }

            half4 LavaFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float time = _Time.y;
                float2 baseUV = AnimatedSurfaceUV(input.uv, input.positionWS, _WorldSpaceUV, _BaseMap_ST);

                // Warp every layer UV by an evolving noise offset so the scroll
                // reads as internal circulation, not a sliding decal. Layer 2
                // subtracts the offset for extra decorrelation at zero cost.
                half pulsePhase;
                float2 distortion = NoiseDistortionUVOffset(
                    TEXTURE2D_ARGS(_NoiseMap, sampler_NoiseMap),
                    baseUV * _NoiseTiling, _DistortionSpeed, _DistortionAmount, time, pulsePhase);

                float2 uv1 = FlowUV(baseUV, _FlowDirection1.xy, _FlowSpeed1, time) + distortion;
                half2 flowVector = DecodeFlowVector(
                    SAMPLE_TEXTURE2D(_FlowMap, sampler_FlowMap, baseUV * _FlowMapTiling).rg,
                    _FlowStrength);
                float2 uv1A, uv1B;
                half flowBlend;
                FlowMapPhases(uv1, flowVector, _FlowCycleSpeed, time, uv1A, uv1B, flowBlend);

                half3 albedo, emissionPattern, normalTS;
                UNITY_BRANCH
                if (_TilingBreakup > 0.5h)
                {
                    float2 a, b, c;
                    float3 weights;
                    LavaPatchTriangle(baseUV, a, b, c, weights);
                    float4 gradientX = float4(ddx(uv1A), ddx(uv1B));
                    float4 gradientY = float4(ddy(uv1A), ddy(uv1B));
                    LavaPatchSample patchA = SampleLavaPatch(a, uv1A, uv1B, gradientX, gradientY, flowBlend);
                    LavaPatchSample patchB = SampleLavaPatch(b, uv1A, uv1B, gradientX, gradientY, flowBlend);
                    LavaPatchSample patchC = SampleLavaPatch(c, uv1A, uv1B, gradientX, gradientY, flowBlend);
                    albedo = (patchA.albedo * weights.x + patchB.albedo * weights.y + patchC.albedo * weights.z) * _BaseColor.rgb;
                    emissionPattern = patchA.emission * weights.x + patchB.emission * weights.y + patchC.emission * weights.z;
                    normalTS = normalize(patchA.normal * weights.x + patchB.normal * weights.y + patchC.normal * weights.z);
                }
                else
                {
                    // Preserve the existing dual-layer look for other scenes.
                    float sinR, cosR;
                    sincos(radians(_Layer2Rotation), sinR, cosR);
                    float2 base2 = RotateUV(baseUV, float2(sinR, cosR)) * _Layer2UVScale;
                    float2 uv2 = FlowUV(base2, _FlowDirection2.xy, _FlowSpeed2, time) - distortion;
                    half2 flowVector2 = RotateUV(flowVector, float2(sinR, cosR)) * _Layer2UVScale;
                    float2 uv2A, uv2B;
                    FlowMapPhases(uv2, flowVector2, _FlowCycleSpeed, time, uv2A, uv2B, flowBlend);
                    half3 albedo1 = FlowedSampleRGB(TEXTURE2D_ARGS(_BaseMap, sampler_BaseMap), uv1A, uv1B, flowBlend);
                    half3 albedo2 = FlowedSampleRGB(TEXTURE2D_ARGS(_BaseMap, sampler_BaseMap), uv2A, uv2B, flowBlend);
                    half3 emission1 = FlowedSampleRGB(TEXTURE2D_ARGS(_EmissionMap, sampler_EmissionMap), uv1A, uv1B, flowBlend);
                    half3 emission2 = FlowedSampleRGB(TEXTURE2D_ARGS(_EmissionMap, sampler_EmissionMap), uv2A, uv2B, flowBlend);
                    half weight2 = DualLayerWeight(
                        Luminance(emission1), Luminance(emission2),
                        _LayerBalance + (pulsePhase - 0.5) * 0.35, _LayerBlendSharpness);
                    albedo = lerp(albedo1, albedo2, weight2) * _BaseColor.rgb;
                    emissionPattern = lerp(emission1, emission2, weight2);
                    half3 normal1 = BlendLayerNormals(
                        UnpackNormalScale(SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, uv1A), _NormalStrength),
                        UnpackNormalScale(SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, uv1B), _NormalStrength), flowBlend);
                    half3 normal2 = BlendLayerNormals(
                        UnpackNormalScale(SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, uv2A), _NormalStrength),
                        UnpackNormalScale(SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, uv2B), _NormalStrength), flowBlend);
                    normalTS = BlendLayerNormals(normal1, normal2, weight2);
                }

                // Meter-scale brightness variation from one extra unscrolled
                // noise tap: repetition reads mostly through emission, so
                // modulating it at a frequency far below the texture tile is
                // what breaks the residual grid.
                // (the trailing *2.0 compensates for the low-contrast noise
                // source; see the contrast note in AnimatedSurface.hlsl)
                half macroNoise = LavaMacroNoise(baseUV * _MacroTiling);
                half macro = max(1.0 + _MacroVariation * (macroNoise * 2.0 - 1.0) * 2.0, 0.0);
                albedo *= lerp(1.0, macro, 0.35);

                half pulse = EmissionPulse(time, _EmissionPulseSpeed, _EmissionPulseAmount, pulsePhase);
                half3 emission = emissionPattern
                    * _EmissionColor.rgb * (_EmissionIntensity * pulse * macro);

                SurfaceData surfaceData = (SurfaceData)0;
                surfaceData.albedo = albedo;
                surfaceData.metallic = _Metallic;
                surfaceData.smoothness = _Smoothness;
                surfaceData.normalTS = normalTS;
                surfaceData.emission = emission;
                surfaceData.occlusion = 1.0;
                surfaceData.alpha = 1.0;

                InputData inputData = (InputData)0;
                inputData.positionWS = input.positionWS;
                inputData.positionCS = input.positionCS;
                half3 bitangent = input.tangentWS.w * cross(input.normalWS, input.tangentWS.xyz);
                inputData.tangentToWorld = half3x3(input.tangentWS.xyz, bitangent, input.normalWS);
                inputData.normalWS = NormalizeNormalPerPixel(TransformTangentToWorld(normalTS, inputData.tangentToWorld));
                inputData.viewDirectionWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
                #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
                inputData.shadowCoord = input.shadowCoord;
                #elif defined(MAIN_LIGHT_CALCULATE_SHADOWS)
                inputData.shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                #else
                inputData.shadowCoord = float4(0, 0, 0, 0);
                #endif
                inputData.fogCoord = input.fogFactor;
                // Probes only, per pixel: animated surfaces aren't lightmapped.
                inputData.bakedGI = SampleSH(inputData.normalWS);
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
                inputData.shadowMask = half4(1, 1, 1, 1);

                #if defined(_DBUFFER)
                ApplyDecalToSurfaceData(input.positionCS, surfaceData, inputData);
                #endif

                half4 color = UniversalFragmentPBR(inputData, surfaceData);
                color.rgb = MixFog(color.rgb, inputData.fogCoord);
                color.a = 1.0;
                return color;
            }
            ENDHLSL
        }

        // URP's stock opaque passes are reused directly: with _ALPHATEST_ON
        // undefined they touch no material properties, so nothing is forked.
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull Back

            HLSLPROGRAM
            #pragma target 2.0
            #pragma vertex ShadowPassVertex
            #pragma fragment ShadowPassFragment
            #pragma multi_compile_instancing
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #include "Packages/com.unity.render-pipelines.universal/Shaders/ShadowCasterPass.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ZWrite On
            ColorMask R
            Cull Back

            HLSLPROGRAM
            #pragma target 2.0
            #pragma vertex DepthOnlyVertex
            #pragma fragment DepthOnlyFragment
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/Shaders/DepthOnlyPass.hlsl"
            ENDHLSL
        }

        // Geometric normal only: skipping the animated normal here saves the
        // whole UV/noise chain in the prepass; SSAO on emissive lava is
        // visually negligible.
        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }

            ZWrite On
            Cull Back

            HLSLPROGRAM
            #pragma target 2.0
            #pragma vertex DepthNormalsVertex
            #pragma fragment DepthNormalsFragment
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/Shaders/DepthNormalsPass.hlsl"
            ENDHLSL
        }
    }

    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
