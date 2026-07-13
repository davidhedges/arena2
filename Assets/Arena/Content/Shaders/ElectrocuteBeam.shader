// Electrocute channel beam: renders the Piloto lightning-bolt flipbook sheet
// (Lightning_H_Trails_FB_1: 4 stacked horizontal bolt frames; G = bolt core,
// R = glow halo) along a camera-aligned LineRenderer ribbon. The C# driver
// tiles _MainTex.x by beam length so bolt density stays constant; this shader
// animates the flipbook rows and tints the packed channels.
Shader "Arena/Presentation/ElectrocuteBeam"
{
    Properties
    {
        _MainTex("Bolt Sheet", 2D) = "black" {}
        [HDR] _CoreColor("Core Color", Color) = (2.2, 3.2, 5.0, 1)
        [HDR] _GlowColor("Glow Color", Color) = (0.2, 0.5, 2.0, 1)
        _Rows("Flipbook Rows", Float) = 4
        _Fps("Flipbook FPS", Float) = 10
        _ScrollSpeed("Scroll Speed", Float) = 0.35
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "RenderType" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
        }

        Pass
        {
            Name "Forward"
            Tags { "LightMode" = "UniversalForward" }

            Blend One One
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                half4 color : COLOR;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                half4 color : COLOR;
            };

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                half4 _CoreColor;
                half4 _GlowColor;
                float _Rows;
                float _Fps;
                float _ScrollSpeed;
            CBUFFER_END

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionHCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = input.uv;
                output.color = input.color;
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float rows = max(_Rows, 1.0);
                float frame = floor(fmod(_Time.y * _Fps, rows));

                // Tile along the beam (ST.x set from C# = length-based repeats),
                // scroll slowly, and de-correlate frames with a per-frame offset
                // so the loop doesn't read as a repeating pattern.
                float u = input.uv.x * _MainTex_ST.x + _MainTex_ST.z
                    + _Time.y * _ScrollSpeed
                    + frac(frame * 0.618);
                // Map the ribbon's 0..1 V into the current flipbook row.
                float v = (input.uv.y + frame) / rows;

                half4 tex = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, float2(u, v));
                half3 bolt = _CoreColor.rgb * tex.g + _GlowColor.rgb * tex.r;
                return half4(bolt * input.color.rgb * input.color.a, 1);
            }
            ENDHLSL
        }
    }
}
