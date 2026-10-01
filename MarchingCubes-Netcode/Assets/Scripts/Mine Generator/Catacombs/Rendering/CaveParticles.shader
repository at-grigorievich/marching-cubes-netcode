// Частицы катакомб: огонь, искры, дым и брызги взрыва гранаты, вспышка у дула.
//
// Отдельно от Cave Unlit, а не его режимом: частицам нужен цвет вершин — через него
// система частиц ведёт цвет и прозрачность по времени жизни, — а меши ореолов и паутины
// его не несут, и чтение несуществующего атрибута на разных платформах даёт разное.
// Проверенные ореолы и паутину эта правка не трогает.
//
// Режим смешения задаётся из кода (см. CaveExplosions): свечение — SrcAlpha One,
// дым и брызги — SrcAlpha OneMinusSrcAlpha. Свечение туман гасит в чёрное (добавка
// к кадру убывает с расстоянием), дым — в цвет тумана.
Shader "Mine Generator/Cave Particles"
{
    Properties
    {
        [MainTexture] _MainTex ("Текстура", 2D) = "white" {}
        [MainColor] _Color ("Цвет", Color) = (1, 1, 1, 1)

        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend ("Множитель источника", Float) = 5
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend ("Множитель приёмника", Float) = 1

        // 1 — туман гасит в чёрное (свечение), 0 — в цвет тумана (дым, брызги).
        _FogToBlack ("Туман гасит в чёрное", Range(0, 1)) = 1
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
            "PreviewType" = "Plane"
        }

        Pass
        {
            Name "Particles"

            // Без метки LightMode, как у Cave Unlit и штатного URP Unlit: такой проход
            // пайплайн рисует как SRPDefaultUnlit.
            Blend [_SrcBlend] [_DstBlend]
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma target 2.0
            #pragma vertex CaveParticlesVertex
            #pragma fragment CaveParticlesFragment

            #pragma multi_compile_instancing

            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Fog.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                half4 _Color;
                half _SrcBlend;
                half _DstBlend;
                half _FogToBlack;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                half4 color       : COLOR;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                half4 color       : COLOR;
                float2 uv         : TEXCOORD0;
                half fogFactor    : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings CaveParticlesVertex(Attributes input)
            {
                Varyings output = (Varyings)0;

                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                VertexPositionInputs positionInputs = GetVertexPositionInputs(input.positionOS.xyz);

                output.positionCS = positionInputs.positionCS;
                output.color = input.color;
                output.uv = TRANSFORM_TEX(input.uv, _MainTex);
                output.fogFactor = ComputeFogFactor(positionInputs.positionCS.z);

                return output;
            }

            half4 CaveParticlesFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                half4 color = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv) * _Color * input.color;

                half3 toBlack = MixFogColor(color.rgb, half3(0, 0, 0), input.fogFactor);
                half3 toFog = MixFog(color.rgb, input.fogFactor);

                color.rgb = lerp(toFog, toBlack, _FogToBlack);

                return color;
            }
            ENDHLSL
        }
    }

    FallBack "Universal Render Pipeline/Particles/Unlit"
}
