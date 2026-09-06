// Distance silhouettes for the generated dungeon's cavern envelope.
//
// Unlit and vertex-coloured ON PURPOSE. The envelope's depth read is baked into
// vertex colour at build time (bottom vertices carry the lava underglow, tips
// dissolve toward the void). Arena rock adds a softer distance haze below.
// Scene fog is tuned for interior gameplay legibility; the arena background
// shares its colour but uses a separate fade range to preserve combat contrast.
//
// OPAQUE, not alpha-blended. The distance fade is a colour lerp toward the
// camera clear colour rather than an alpha ramp, which is visually identical
// over a uniform background and avoids what blending actually cost: spires
// rendered see-through, so every cone showed its own hollow interior and read
// as a glass funnel instead of rock. Opaque also gets correct occlusion between
// overlapping silhouettes for free — and overlap is what encodes depth here.
Shader "Arena/Environment/CavernSilhouette"
{
    Properties
    {
        [MainColor] _BaseColor("Tint", Color) = (1, 1, 1, 1)
        [ToggleUI] _RockDetail("Volcanic rock detail", Float) = 0
        _RockMap("Basalt surface", 2D) = "white" {}
        _EmberMap("Ember fissures", 2D) = "black" {}
        _RockTiling("Rock repeats per metre", Float) = 0.025
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Geometry"
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
        }

        Pass
        {
            Name "Forward"
            Tags { "LightMode" = "UniversalForward" }

            ZWrite On
            // Two-sided, so silhouette winding never has to be reasoned about.
            Cull Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 3.0

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                half4 color : COLOR;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                half4 color : COLOR;
                float3 positionOS : TEXCOORD0;
                half3 normalOS : TEXCOORD1;
                half3 normalWS : TEXCOORD2;
                float3 positionWS : TEXCOORD3;
            };

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
                half _RockDetail;
                float _RockTiling;
            CBUFFER_END

            TEXTURE2D(_RockMap); SAMPLER(sampler_RockMap);
            TEXTURE2D(_EmberMap); SAMPLER(sampler_EmberMap);

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionHCS = TransformObjectToHClip(input.positionOS.xyz);
                output.color = input.color * _BaseColor;
                output.positionOS = input.positionOS.xyz;
                output.normalOS = input.normalOS;
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                // The dungeon keeps its original vertex-colour-only treatment.
                // Arena rock uses object-space triplanar mapping: no UV seams,
                // and the texture stays attached as the far vault follows the camera.
                UNITY_BRANCH
                if (_RockDetail < 0.5h)
                    return input.color;

                float3 p = input.positionOS * _RockTiling;
                half3 weights = pow(abs(input.normalOS), 4.0h);
                weights /= max(dot(weights, half3(1, 1, 1)), 0.001h);
                half3 stone = SAMPLE_TEXTURE2D(_RockMap, sampler_RockMap, p.zy).rgb * weights.x
                    + SAMPLE_TEXTURE2D(_RockMap, sampler_RockMap, p.xz).rgb * weights.y
                    + SAMPLE_TEXTURE2D(_RockMap, sampler_RockMap, p.xy).rgb * weights.z;
                half3 ember = SAMPLE_TEXTURE2D(_EmberMap, sampler_EmberMap, p.zy).rgb * weights.x
                    + SAMPLE_TEXTURE2D(_EmberMap, sampler_EmberMap, p.xz).rgb * weights.y
                    + SAMPLE_TEXTURE2D(_EmberMap, sampler_EmberMap, p.xy).rgb * weights.z;

                half facet = saturate(dot(normalize(input.normalWS), normalize(half3(-0.35h, 0.7h, -0.45h))));
                half grain = dot(stone, half3(0.2126h, 0.7152h, 0.0722h));
                // Vertex colour carries each band's distance haze. Keep detail
                // relative to it so farther formations cannot become bright cutouts.
                // Keep large forms readable, but compress texture and facet
                // contrast so the cavern sits behind the playable architecture.
                half3 rock = input.color.rgb * (0.5h + grain * 2.4h) * (0.65h + facet * 0.45h);
                half heat = saturate((input.color.r - input.color.b) * 5.0h);
                rock += ember * half3(0.035h, 0.012h, 0.004h) * (0.15h + heat);
                // Only the arena's textured background takes this atmospheric
                // fade. Its range is separate from fog across the combat floor.
                float distanceToCamera = distance(input.positionWS, _WorldSpaceCameraPos);
                half haze = lerp(0.12h, 0.8h, smoothstep(70.0, 350.0, distanceToCamera));
                rock = lerp(rock, unity_FogColor.rgb, haze);
                return half4(rock, 1);
            }
            ENDHLSL
        }
    }
}
