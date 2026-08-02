// Distance silhouettes for the generated dungeon's cavern envelope.
//
// Unlit and vertex-coloured ON PURPOSE. The envelope's depth read is baked into
// vertex colour at build time (bottom vertices carry the lava underglow, tips
// dissolve toward the void), which keeps it independent of RenderSettings.fog.
// Scene fog is tuned for interior gameplay legibility; if the envelope shared
// it, retuning one would silently break the other.
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

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                half4 color : COLOR;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                half4 color : COLOR;
            };

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
            CBUFFER_END

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionHCS = TransformObjectToHClip(input.positionOS.xyz);
                output.color = input.color * _BaseColor;
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                return input.color;
            }
            ENDHLSL
        }
    }
}
