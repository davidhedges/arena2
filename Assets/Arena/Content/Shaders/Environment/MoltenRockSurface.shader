// Mixed static/molten surface: a spatially-fixed, fully-featured lit rock base
// (albedo, normal, metallic+smoothness, occlusion, height parallax, emission —
// URP Lit feature parity) with an animated molten layer blended in only where
// a static mask says the surface is lava/energy. The mask defaults to the
// emission map's luminance, so migrated URP Lit materials animate their
// existing glow regions without new authoring. Composes
// Common/AnimatedSurface.hlsl blocks; lighting/shadow/depth behavior matches
// URP Lit (opaque).
//
// Contract with the source materials (dark mask = static rock, bright mask =
// molten): rock color/shape, rock normal detail, height/parallax, occlusion,
// and the rock/molten boundary never move. Only the masked molten regions get
// animated color, UV distortion, emission variation, and (optional) animated
// normal detail, sampled from a separate molten texture layer.
Shader "Arena/Environment/MoltenRockSurface"
{
    Properties
    {
        [Header(Surface)]
        [MainTexture] _BaseMap("Albedo", 2D) = "white" {}
        [MainColor] _BaseColor("Tint", Color) = (1, 1, 1, 1)
        [NoScaleOffset][Normal] _NormalMap("Normal Map", 2D) = "bump" {}
        _NormalStrength("Normal Strength", Range(0, 2)) = 1
        [NoScaleOffset] _MetallicGlossMap("Metallic (R) Smoothness (A)", 2D) = "white" {}
        _Metallic("Metallic Scale", Range(0, 1)) = 0
        _Smoothness("Smoothness Scale", Range(0, 1)) = 0.5
        [NoScaleOffset] _OcclusionMap("Occlusion (G)", 2D) = "white" {}
        _OcclusionStrength("Occlusion Strength", Range(0, 1)) = 1
        [NoScaleOffset] _ParallaxMap("Height (G)", 2D) = "gray" {}
        _Parallax("Height Scale", Range(0, 0.08)) = 0.02

        [Space(12)]
        [Header(Molten Mask)]
        [ToggleUI] _UseMaskMap("Use Dedicated Mask Map", Float) = 0
        [NoScaleOffset] _MaskMap("Mask Map", 2D) = "white" {}
        [Enum(Luminance, 0, R, 1, G, 2, B, 3, A, 4)] _MaskChannel("Mask Channel", Float) = 0
        _MaskThreshold("Mask Threshold", Range(0, 1)) = 0.05
        _MaskSoftness("Mask Softness", Range(0.001, 1)) = 0.35

        [Space(12)]
        [Header(Molten Layer)]
        _MoltenMap("Molten Color", 2D) = "black" {}
        _MoltenColor("Molten Tint", Color) = (1, 1, 1, 1)
        _MoltenAlbedoBlend("Molten Albedo Blend", Range(0, 1)) = 1
        _MoltenEmissionBlend("Molten Emission Blend", Range(0, 1)) = 1
        [NoScaleOffset][Normal] _MoltenNormalMap("Molten Normal Map", 2D) = "bump" {}
        _MoltenNormalStrength("Molten Normal Strength", Range(0, 2)) = 1
        _MoltenNormalBlend("Molten Normal Blend", Range(0, 1)) = 0

        [Space(12)]
        [Header(Flow)]
        [NoScaleOffset] _FlowMap("Flow Map (RG)", 2D) = "gray" {}
        _FlowMapTiling("Flow Map Tiling", Float) = 1
        _FlowStrength("Flow Strength", Range(0, 1)) = 0.1
        _FlowCycleSpeed("Flow Cycle Speed", Float) = 0.15
        _FlowDirection("Layer 1 Drift Direction (XY)", Vector) = (1, 0.25, 0, 0)
        _FlowSpeed("Layer 1 Drift Speed", Float) = 0.03
        _FlowDirection2("Layer 2 Drift Direction (XY)", Vector) = (-0.5, -0.85, 0, 0)
        _FlowSpeed2("Layer 2 Drift Speed", Float) = 0.02
        _Layer2UVScale("Layer 2 UV Scale", Float) = 0.79
        _Layer2Rotation("Layer 2 Rotation (Degrees)", Range(0, 90)) = 54
        _LayerBalance("Layer Balance", Range(0, 1)) = 0.5
        _LayerBlendSharpness("Layer Blend Sharpness", Range(0, 8)) = 6

        [Space(12)]
        [Header(Distortion)]
        [NoScaleOffset] _NoiseMap("Noise (R)", 2D) = "gray" {}
        _NoiseTiling("Noise Tiling", Float) = 0.4
        _DistortionAmount("Distortion Amount", Range(0, 1)) = 0.15
        _DistortionSpeed("Distortion Speed", Float) = 0.12

        [Space(12)]
        [Header(Macro Variation)]
        _MacroVariation("Macro Variation", Range(0, 1)) = 0.3
        _MacroTiling("Macro Tiling", Float) = 0.07

        [Space(12)]
        [Header(Emission)]
        [NoScaleOffset] _EmissionMap("Emission Map", 2D) = "black" {}
        [HDR] _EmissionColor("Emission Color", Color) = (1, 1, 1, 1)
        _EmissionIntensity("Emission Intensity", Float) = 1
        _EmissionPulseAmount("Pulse Amount", Range(0, 1)) = 0.35
        _EmissionPulseSpeed("Pulse Speed", Float) = 0.3

        [Space(12)]
        [Header(Advanced)]
        [ToggleUI] _WorldSpaceUV("World Space UV (XZ, 1 unit = 1 UV)", Float) = 0
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
            float4 _MoltenMap_ST;
            half4 _BaseColor;
            half4 _MoltenColor;
            half4 _EmissionColor;
            float4 _FlowDirection;
            float4 _FlowDirection2;
            float _FlowMapTiling;
            float _FlowCycleSpeed;
            half _FlowStrength;
            float _FlowSpeed;
            float _FlowSpeed2;
            float _Layer2UVScale;
            float _Layer2Rotation;
            half _LayerBalance;
            half _LayerBlendSharpness;
            float _NoiseTiling;
            float _DistortionAmount;
            float _DistortionSpeed;
            float _MacroTiling;
            half _MacroVariation;
            half _NormalStrength;
            half _MoltenNormalStrength;
            half _MoltenNormalBlend;
            half _MoltenAlbedoBlend;
            half _MoltenEmissionBlend;
            half _EmissionIntensity;
            half _EmissionPulseAmount;
            half _EmissionPulseSpeed;
            half _Smoothness;
            half _Metallic;
            half _OcclusionStrength;
            half _Parallax;
            half _UseMaskMap;
            half _MaskChannel;
            half _MaskThreshold;
            half _MaskSoftness;
            half _WorldSpaceUV;
        CBUFFER_END
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            ZWrite On
            Cull Back

            HLSLPROGRAM
            #pragma target 2.0
            #pragma vertex MoltenRockVertex
            #pragma fragment MoltenRockFragment

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
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/ParallaxMapping.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "../Common/AnimatedSurface.hlsl"

            // The static rock set shares one sampler (identical repeat/filter
            // import settings); the molten set shares another. Keeps the
            // sampler count at 5 for 11 textures.
            TEXTURE2D(_BaseMap);         SAMPLER(sampler_BaseMap);
            TEXTURE2D(_NormalMap);       SAMPLER(sampler_NormalMap);
            TEXTURE2D(_MetallicGlossMap);
            TEXTURE2D(_OcclusionMap);
            TEXTURE2D(_ParallaxMap);
            TEXTURE2D(_EmissionMap);
            TEXTURE2D(_MaskMap);
            TEXTURE2D(_MoltenMap);       SAMPLER(sampler_MoltenMap);
            TEXTURE2D(_MoltenNormalMap);
            TEXTURE2D(_NoiseMap);        SAMPLER(sampler_NoiseMap);
            TEXTURE2D(_FlowMap);         SAMPLER(sampler_FlowMap);

            // Mask source channel as dot weights: 0 = Rec.709 luminance,
            // 1..4 = R/G/B/A. Uniform ternaries — no variants, no divergence.
            half4 MaskChannelWeights(half mode)
            {
                return mode < 0.5 ? half4(0.2126729, 0.7151522, 0.0721750, 0.0) :
                       mode < 1.5 ? half4(1, 0, 0, 0) :
                       mode < 2.5 ? half4(0, 1, 0, 0) :
                       mode < 3.5 ? half4(0, 0, 1, 0) :
                                    half4(0, 0, 0, 1);
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

            Varyings MoltenRockVertex(Attributes input)
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

            half4 MoltenRockFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float time = _Time.y;
                float2 uv = AnimatedSurfaceUV(input.uv, input.positionWS, _WorldSpaceUV, _BaseMap_ST);

                // One-step parallax from the height map, applied to the shared
                // UV so every sample — static rock, mask, and the molten
                // layer's anchor — displaces as one surface (URP Lit's
                // _PARALLAXMAP behavior).
                half3 viewDirWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
                half3 viewDirTS = GetViewDirectionTangentSpace(input.tangentWS, input.normalWS, viewDirWS);
                uv += ParallaxMapping(TEXTURE2D_ARGS(_ParallaxMap, sampler_BaseMap), viewDirTS, _Parallax, uv);

                // Static rock set: sampled once at the fixed UV. Never
                // scrolled, never distorted.
                half3 staticAlbedo = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, uv).rgb * _BaseColor.rgb;
                half3 staticNormalTS = UnpackNormalScale(
                    SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, uv), _NormalStrength);
                half4 metallicGloss = SAMPLE_TEXTURE2D(_MetallicGlossMap, sampler_BaseMap, uv);
                half occlusion = lerp(1.0, SAMPLE_TEXTURE2D(_OcclusionMap, sampler_BaseMap, uv).g, _OcclusionStrength);
                half4 emissionSample = SAMPLE_TEXTURE2D(_EmissionMap, sampler_BaseMap, uv);

                // The molten mask is sampled at the same fixed UV, so the
                // rock/molten boundary never moves. Default source is the
                // emission sample already in hand; a dedicated mask map can
                // replace it (its 1x1 white default makes the extra sample
                // free in the default path).
                half4 maskSource = lerp(
                    emissionSample, SAMPLE_TEXTURE2D(_MaskMap, sampler_BaseMap, uv), _UseMaskMap);
                half maskRaw = dot(maskSource, MaskChannelWeights(_MaskChannel));
                half moltenMask = smoothstep(_MaskThreshold, _MaskThreshold + _MaskSoftness, maskRaw);

                // Molten layer: an additional texture set on flowing UVs.
                // Noise-warped so the motion reads as churn, advected along
                // the flow map so each channel creeps in its own direction
                // (ping-pong phases hide the advection reset).
                half pulsePhase;
                float2 distortion = NoiseDistortionUVOffset(
                    TEXTURE2D_ARGS(_NoiseMap, sampler_NoiseMap),
                    uv * _NoiseTiling, _DistortionSpeed, _DistortionAmount, time, pulsePhase);

                // Two decorrelated copies of the molten texture, layer 2 on a
                // rotated/rescaled grid, blended by which copy is locally
                // hotter (same DualLayerWeight competition as LavaSurface).
                // The moving fronts where dominance flips are what make soft
                // low-contrast lava textures read as flowing — a single
                // scrolled copy of this pack's magma maps barely shows motion.
                float2 moltenUV = uv * _MoltenMap_ST.xy + _MoltenMap_ST.zw;
                float sinR, cosR;
                sincos(radians(_Layer2Rotation), sinR, cosR);
                float2 molten2UV = RotateUV(moltenUV, float2(sinR, cosR)) * _Layer2UVScale;

                float2 uv1 = FlowUV(moltenUV, _FlowDirection.xy, _FlowSpeed, time) + distortion;
                float2 uv2 = FlowUV(molten2UV, _FlowDirection2.xy, _FlowSpeed2, time) - distortion;

                // Flow vector scaled into each layer's UV space so molten
                // tiling/rotation don't change the world-space flow motion.
                half2 flowVector = DecodeFlowVector(
                    SAMPLE_TEXTURE2D(_FlowMap, sampler_FlowMap, uv * _FlowMapTiling).rg,
                    _FlowStrength) * half2(_MoltenMap_ST.xy);
                half2 flowVector2 = RotateUV(flowVector, float2(sinR, cosR)) * _Layer2UVScale;
                float2 uv1A, uv1B, uv2A, uv2B;
                half flowBlend;
                FlowMapPhases(uv1, flowVector, _FlowCycleSpeed, time, uv1A, uv1B, flowBlend);
                FlowMapPhases(uv2, flowVector2, _FlowCycleSpeed, time, uv2A, uv2B, flowBlend);

                half3 molten1 = FlowedSampleRGB(TEXTURE2D_ARGS(_MoltenMap, sampler_MoltenMap), uv1A, uv1B, flowBlend);
                half3 molten2 = FlowedSampleRGB(TEXTURE2D_ARGS(_MoltenMap, sampler_MoltenMap), uv2A, uv2B, flowBlend);
                half moltenWeight2 = DualLayerWeight(
                    Luminance(molten1), Luminance(molten2),
                    _LayerBalance + (pulsePhase - 0.5) * 0.35, _LayerBlendSharpness);
                half3 moltenColor = lerp(molten1, molten2, moltenWeight2) * _MoltenColor.rgb;

                // Molten normal rides layer 1's phases only: the ripple just
                // needs to move with the flow, not track layer dominance.
                half3 moltenNormalTS = BlendLayerNormals(
                    UnpackNormalScale(SAMPLE_TEXTURE2D(_MoltenNormalMap, sampler_MoltenMap, uv1A), _MoltenNormalStrength),
                    UnpackNormalScale(SAMPLE_TEXTURE2D(_MoltenNormalMap, sampler_MoltenMap, uv1B), _MoltenNormalStrength),
                    flowBlend);

                // Mask-gated composition: rock everywhere the mask is dark,
                // animated molten color/normal where it is bright.
                half3 albedo = lerp(staticAlbedo, moltenColor, moltenMask * _MoltenAlbedoBlend);
                half3 normalTS = BlendLayerNormals(staticNormalTS, moltenNormalTS, moltenMask * _MoltenNormalBlend);

                // Emission variation is molten-only: outside the mask the
                // original static emission passes through untouched. Macro is
                // an unscrolled meter-scale noise tap that breaks glow
                // repetition at distance (the trailing *2.0 compensates for
                // the low-contrast noise source; see AnimatedSurface.hlsl).
                half macroNoise = SAMPLE_TEXTURE2D(_NoiseMap, sampler_NoiseMap, uv * _MacroTiling).r;
                half macro = max(1.0 + _MacroVariation * (macroNoise * 2.0 - 1.0) * 2.0, 0.0);
                half pulse = EmissionPulse(time, _EmissionPulseSpeed, _EmissionPulseAmount, pulsePhase);
                half3 emission = lerp(emissionSample.rgb, moltenColor, moltenMask * _MoltenEmissionBlend)
                    * _EmissionColor.rgb * (_EmissionIntensity * lerp(1.0, pulse * macro, moltenMask));

                SurfaceData surfaceData = (SurfaceData)0;
                surfaceData.albedo = albedo;
                surfaceData.metallic = metallicGloss.r * _Metallic;
                surfaceData.smoothness = metallicGloss.a * _Smoothness;
                surfaceData.normalTS = normalTS;
                surfaceData.emission = emission;
                surfaceData.occlusion = occlusion;
                surfaceData.alpha = 1.0;

                InputData inputData = (InputData)0;
                inputData.positionWS = input.positionWS;
                inputData.positionCS = input.positionCS;
                half3 bitangent = input.tangentWS.w * cross(input.normalWS, input.tangentWS.xyz);
                inputData.tangentToWorld = half3x3(input.tangentWS.xyz, bitangent, input.normalWS);
                inputData.normalWS = NormalizeNormalPerPixel(TransformTangentToWorld(normalTS, inputData.tangentToWorld));
                inputData.viewDirectionWS = viewDirWS;
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

        // Geometric normal only: skipping the parallax/normal-map chain in the
        // prepass matches LavaSurface's tradeoff; SSAO differences on a
        // rock-scale surface are visually negligible.
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
