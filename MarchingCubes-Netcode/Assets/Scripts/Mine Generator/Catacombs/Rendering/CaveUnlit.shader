// Неосвещаемая мелочь катакомб: ореолы вокруг источников, пылинки, паутина и светящиеся
// ядра ламп. Один шейдер на всё, режим смешения задаётся из кода.
//
// Раньше эти четыре вещи брали три разных встроенных шейдера — Mobile/Particles/Additive,
// Unlit/Color и Unlit/Transparent. В URP они формально ещё рисуются (проход без метки
// LightMode пайплайн считает за SRPDefaultUnlit), но полагаться на это не стоит: они не
// знают ни про туман URP, ни про SRP Batcher, а Mobile/Particles/Additive вдобавок молча
// игнорирует любой заданный цвет — из свойств у него есть только _MainTex. Именно из-за
// этого цвет ореолов приходилось запекать в саму текстуру.
//
// Здесь _Color работает, и запекание цвета в текстуру больше не обязательно.
Shader "Mine Generator/Cave Unlit"
{
    Properties
    {
        [MainTexture] _MainTex ("Текстура", 2D) = "white" {}
        [MainColor] _Color ("Цвет", Color) = (1, 1, 1, 1)

        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend ("Множитель источника", Float) = 1
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend ("Множитель приёмника", Float) = 0

        [Enum(Off, 0, On, 1)] _ZWrite ("Запись глубины", Float) = 1
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Отсечение граней", Float) = 2
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "Unlit"

            // Метки LightMode здесь намеренно нет — ровно как у штатного
            // Universal Render Pipeline/Unlit. Проход без неё пайплайн рисует
            // как SRPDefaultUnlit; выдуманная метка не совпала бы ни с одной
            // из тех, что URP ищет, и проход молча не рисовался бы вовсе.
            Blend [_SrcBlend] [_DstBlend]
            ZWrite [_ZWrite]
            Cull [_Cull]

            HLSLPROGRAM
            #pragma target 2.0
            #pragma vertex CaveUnlitVertex
            #pragma fragment CaveUnlitFragment

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
                half _ZWrite;
                half _Cull;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                half fogFactor    : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings CaveUnlitVertex(Attributes input)
            {
                Varyings output = (Varyings)0;

                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                VertexPositionInputs positionInputs = GetVertexPositionInputs(input.positionOS.xyz);

                output.positionCS = positionInputs.positionCS;
                output.uv = TRANSFORM_TEX(input.uv, _MainTex);
                output.fogFactor = ComputeFogFactor(positionInputs.positionCS.z);

                return output;
            }

            half4 CaveUnlitFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                half4 color = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv) * _Color;

                // Аддитивное свечение гасится туманом в чёрное, а не в цвет тумана:
                // добавка к кадру должна убывать с расстоянием, а не подменяться дымкой.
                // Для полупрозрачного (паутина) это даёт ровно то же, что MixFog, потому
                // что её собственный цвет и так тёмный.
                color.rgb = MixFogColor(color.rgb, half3(0, 0, 0), input.fogFactor);

                return color;
            }
            ENDHLSL
        }
    }

    FallBack "Universal Render Pipeline/Unlit"
}
