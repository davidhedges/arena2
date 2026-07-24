// Focused translucent resin for overlay dice. The forward pass deliberately
// writes depth: the die is a presentation object with no intersecting world
// geometry, and reliable numeral occlusion matters more than conventional
// transparent-object compositing.
Shader "Arena/Dice/Resin"
{
    Properties
    {
        [MainColor] _BaseColor("Resin Tint", Color) = (0.28, 0.008, 0.012, 0.94)
        _Smoothness("Smoothness", Range(0, 1)) = 0.88
        _Metallic("Metallic", Range(0, 1)) = 0.04

        [Space(12)]
        [Header(Edge Light)]
        [HDR] _FresnelColor("Fresnel Color", Color) = (1.2, 0.12, 0.035, 1)
        _FresnelPower("Fresnel Power", Range(0.5, 8)) = 3.4
        _FresnelStrength("Fresnel Strength", Range(0, 2)) = 0.62
        _EdgeOpacity("Edge Opacity", Range(0, 1)) = 0.05

        [Space(12)]
        [Header(Internal Character)]
        _VariationStrength("Tonal Variation", Range(0, 0.3)) = 0.075
        _VariationScale("Variation Scale", Range(0.1, 12)) = 3.2
        _ShimmerAmount("Held Shimmer", Range(0, 0.1)) = 0.012
        _ShimmerSpeed("Shimmer Speed", Range(0, 2)) = 0.18
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "UniversalMaterialType" = "Lit"
            "Queue" = "Transparent"
            "IgnoreProjector" = "True"
        }
        LOD 250

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        CBUFFER_START(UnityPerMaterial)
            half4 _BaseColor;
            half4 _FresnelColor;
            half _Smoothness;
            half _Metallic;
            half _FresnelPower;
            half _FresnelStrength;
            half _EdgeOpacity;
            half _VariationStrength;
            float _VariationScale;
            half _ShimmerAmount;
            half _ShimmerSpeed;
        CBUFFER_END
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite On
            ZTest LEqual
            Cull Back

            HLSLPROGRAM
            #pragma target 2.0
            #pragma vertex ResinVertex
            #pragma fragment ResinFragment

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_ATLAS
            #pragma multi_compile_fog
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                half3 normalWS : TEXCOORD1;
                float3 positionOS : TEXCOORD2;
                half fogFactor : TEXCOORD3;
                #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
                float4 shadowCoord : TEXCOORD4;
                #endif
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings ResinVertex(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                VertexPositionInputs positionInputs = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs normalInputs = GetVertexNormalInputs(input.normalOS);
                output.positionCS = positionInputs.positionCS;
                output.positionWS = positionInputs.positionWS;
                output.normalWS = normalInputs.normalWS;
                output.positionOS = input.positionOS.xyz;
                output.fogFactor = ComputeFogFactor(positionInputs.positionCS.z);
                #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
                output.shadowCoord = GetShadowCoord(positionInputs);
                #endif
                return output;
            }

            half Hash31(float3 position)
            {
                position = frac(position * 0.1031);
                position += dot(position, position.yzx + 33.33);
                return frac((position.x + position.y) * position.z);
            }

            half4 ResinFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                half3 normalWS = NormalizeNormalPerPixel(input.normalWS);
                half3 viewDirectionWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
                half fresnel = pow(
                    1.0h - saturate(dot(normalWS, viewDirectionWS)),
                    _FresnelPower);

                float3 variationCell = floor(input.positionOS * _VariationScale * 3.0) / 3.0;
                half variationNoise = Hash31(variationCell);
                half tonalVariation = 1.0h + (variationNoise * 2.0h - 1.0h) * _VariationStrength;
                half shimmer = sin(
                    _Time.y * _ShimmerSpeed * TWO_PI +
                    dot(input.positionOS, float3(1.37, 2.11, 0.83))) * _ShimmerAmount;

                SurfaceData surfaceData = (SurfaceData)0;
                surfaceData.albedo = _BaseColor.rgb * tonalVariation;
                surfaceData.metallic = _Metallic;
                surfaceData.smoothness = _Smoothness;
                surfaceData.normalTS = half3(0, 0, 1);
                surfaceData.emission =
                    _FresnelColor.rgb * (fresnel * _FresnelStrength) +
                    _BaseColor.rgb * max(shimmer, 0.0h);
                surfaceData.occlusion = 1.0h;
                surfaceData.alpha = saturate(_BaseColor.a + fresnel * _EdgeOpacity);

                InputData inputData = (InputData)0;
                inputData.positionWS = input.positionWS;
                inputData.positionCS = input.positionCS;
                inputData.normalWS = normalWS;
                inputData.viewDirectionWS = viewDirectionWS;
                #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
                inputData.shadowCoord = input.shadowCoord;
                #elif defined(MAIN_LIGHT_CALCULATE_SHADOWS)
                inputData.shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                #else
                inputData.shadowCoord = float4(0, 0, 0, 0);
                #endif
                inputData.fogCoord = input.fogFactor;
                inputData.bakedGI = SampleSH(normalWS);
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
                inputData.shadowMask = half4(1, 1, 1, 1);

                half4 color = UniversalFragmentPBR(inputData, surfaceData);
                color.rgb = MixFog(color.rgb, inputData.fogCoord);
                color.a = surfaceData.alpha;
                return color;
            }
            ENDHLSL
        }
    }

    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
