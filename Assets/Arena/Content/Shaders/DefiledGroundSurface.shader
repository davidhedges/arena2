Shader "Arena/Presentation/DefiledGroundSurface"
{
    Properties
    {
        [MainTexture] _BaseMap("Skull Surface", 2D) = "white" {}
        [MainColor] _BaseColor("Tint", Color) = (1, 1, 1, 1)
        [Normal] _BumpMap("Normal Map", 2D) = "bump" {}
        _BumpScale("Normal Strength", Range(0, 2)) = 1
        _MetallicGlossMap("Metallic (R) Smoothness (A)", 2D) = "black" {}
        _Metallic("Metallic", Range(0, 1)) = 0
        _Smoothness("Smoothness", Range(0, 1)) = 1
        _OcclusionMap("Occlusion (G)", 2D) = "white" {}
        _OcclusionStrength("Occlusion Strength", Range(0, 1)) = 1
        _ParallaxMap("Height (G)", 2D) = "gray" {}
        _Parallax("Height Scale", Range(0, 0.08)) = 0.08
        _Opacity("Opacity", Range(0, 1)) = 1
        _Dissolve("Dissolve", Range(0, 1)) = 0
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "RenderType" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "UniversalMaterialType" = "Lit"
            "IgnoreProjector" = "True"
        }
        LOD 300

        Pass
        {
            Name "DefiledGround"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off
            Offset -1, -1

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex Vert
            #pragma fragment Frag

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_ATLAS
            #pragma multi_compile_fog
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/ParallaxMapping.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float4 tangentOS : TANGENT;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                half3 normalWS : TEXCOORD2;
                half4 tangentWS : TEXCOORD3;
                half fogFactor : TEXCOORD4;
                #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
                float4 shadowCoord : TEXCOORD5;
                #endif
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);
            TEXTURE2D(_BumpMap);
            SAMPLER(sampler_BumpMap);
            TEXTURE2D(_MetallicGlossMap);
            SAMPLER(sampler_MetallicGlossMap);
            TEXTURE2D(_OcclusionMap);
            SAMPLER(sampler_OcclusionMap);
            TEXTURE2D(_ParallaxMap);
            SAMPLER(sampler_ParallaxMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4 _BaseColor;
                half _BumpScale;
                half _Metallic;
                half _Smoothness;
                half _OcclusionStrength;
                half _Parallax;
                half _Opacity;
                half _Dissolve;
            CBUFFER_END

            float Hash21(float2 value)
            {
                value = frac(value * float2(123.34, 456.21));
                value += dot(value, value + 45.32);
                return frac(value.x * value.y);
            }

            float ValueNoise(float2 value)
            {
                float2 cell = floor(value);
                float2 fraction = frac(value);
                fraction = fraction * fraction * (3.0 - 2.0 * fraction);
                float lower = lerp(Hash21(cell), Hash21(cell + float2(1.0, 0.0)), fraction.x);
                float upper = lerp(Hash21(cell + float2(0.0, 1.0)), Hash21(cell + 1.0), fraction.x);
                return lerp(lower, upper, fraction.y);
            }

            Varyings Vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                VertexPositionInputs positionInputs = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs normalInputs = GetVertexNormalInputs(input.normalOS, input.tangentOS);
                output.positionHCS = positionInputs.positionCS;
                output.positionWS = positionInputs.positionWS;
                output.uv = input.uv;
                output.normalWS = normalInputs.normalWS;
                output.tangentWS = half4(
                    normalInputs.tangentWS,
                    input.tangentOS.w * GetOddNegativeScale());
                output.fogFactor = ComputeFogFactor(positionInputs.positionCS.z);
                #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
                output.shadowCoord = GetShadowCoord(positionInputs);
                #endif
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float2 centered = input.uv - 0.5;
                float radialMask = 1.0 - smoothstep(0.43, 0.5, length(centered));
                clip(radialMask - 0.001);

                half3 viewDirectionWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
                half3 viewDirectionTS = GetViewDirectionTangentSpace(
                    input.tangentWS,
                    input.normalWS,
                    viewDirectionWS);
                float2 surfaceUv = TRANSFORM_TEX(input.uv, _BaseMap);
                surfaceUv += ParallaxMapping(
                    TEXTURE2D_ARGS(_ParallaxMap, sampler_ParallaxMap),
                    viewDirectionTS,
                    _Parallax,
                    surfaceUv);

                half4 skull = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, surfaceUv);
                float noise = saturate(ValueNoise(input.uv * 17.0) * 0.72
                    + dot(skull.rgb, float3(0.2126, 0.7152, 0.0722)) * 0.28);
                float visible = 1.0 - smoothstep(noise - 0.09, noise + 0.09, _Dissolve);
                float alpha = radialMask * visible * _Opacity * skull.a;
                clip(alpha - 0.01);

                half3 normalTS = UnpackNormalScale(
                    SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, surfaceUv),
                    _BumpScale);
                half4 metallicGloss = SAMPLE_TEXTURE2D(
                    _MetallicGlossMap,
                    sampler_MetallicGlossMap,
                    surfaceUv);
                half occlusion = lerp(
                    1.0h,
                    SAMPLE_TEXTURE2D(_OcclusionMap, sampler_OcclusionMap, surfaceUv).g,
                    _OcclusionStrength);

                SurfaceData surfaceData = (SurfaceData)0;
                surfaceData.albedo = skull.rgb * _BaseColor.rgb;
                surfaceData.metallic = saturate(metallicGloss.r + _Metallic);
                surfaceData.smoothness = metallicGloss.a * _Smoothness;
                surfaceData.normalTS = normalTS;
                surfaceData.occlusion = occlusion;
                surfaceData.alpha = alpha * _BaseColor.a;

                InputData inputData = (InputData)0;
                inputData.positionWS = input.positionWS;
                inputData.positionCS = input.positionHCS;
                half3 bitangent = input.tangentWS.w * cross(input.normalWS, input.tangentWS.xyz);
                inputData.tangentToWorld = half3x3(input.tangentWS.xyz, bitangent, input.normalWS);
                inputData.normalWS = NormalizeNormalPerPixel(
                    TransformTangentToWorld(normalTS, inputData.tangentToWorld));
                inputData.viewDirectionWS = viewDirectionWS;
                #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
                inputData.shadowCoord = input.shadowCoord;
                #elif defined(MAIN_LIGHT_CALCULATE_SHADOWS)
                inputData.shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                #else
                inputData.shadowCoord = float4(0, 0, 0, 0);
                #endif
                inputData.fogCoord = input.fogFactor;
                inputData.bakedGI = SampleSH(inputData.normalWS);
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionHCS);
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
