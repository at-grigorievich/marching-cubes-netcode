// Капли ихора, хитина и коконы — точка с ореолом, развёрнутая к камере в вершинном шейдере.
//
// Не гладкий шар (грабли №9: гладкая выпуклость у стены читается пластиком), а «точка плюс
// ореол» — то, что в этих катакомбах работает. Все капли — один динамический меш: у каждой
// четыре вершины в одной точке (центр капли), угол квадрата — в UV0, размер и своё число —
// в UV1. Квадрат раскрывается к камере здесь, по её осям из матрицы вида: так шесть сотен
// капель — один вызов отрисовки и ни одного расчёта поворота на процессоре.
//
// Мерцание по своему числу капли: одинаково мигающая россыпь читалась бы гирляндой.
Shader "Mine Generator/Cave Ichor"
{
    Properties
    {
        _Glow ("Яркость", Range(0, 8)) = 2.2
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "Ichor"

            Blend One One
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma target 2.0
            #pragma vertex IchorVertex
            #pragma fragment IchorFragment

            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Fog.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half _Glow;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                half4 color       : COLOR;
                float2 corner     : TEXCOORD0;
                float2 sizeSeed   : TEXCOORD1;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                half4 color       : COLOR;
                float2 corner     : TEXCOORD0;
                float seed        : TEXCOORD1;
                half fogFactor    : TEXCOORD2;
            };

            Varyings IchorVertex(Attributes input)
            {
                Varyings output = (Varyings)0;

                // Меш лежит в мировых координатах (матрица единичная): центр капли — сразу мир.
                float3 centre = TransformObjectToWorld(input.positionOS.xyz);

                // Оси камеры — строки матрицы вида.
                float3 right = UNITY_MATRIX_V[0].xyz;
                float3 up = UNITY_MATRIX_V[1].xyz;

                float3 world = centre + (right * input.corner.x + up * input.corner.y) * input.sizeSeed.x;

                output.positionCS = TransformWorldToHClip(world);
                output.color = input.color;
                output.corner = input.corner;
                output.seed = input.sizeSeed.y;
                output.fogFactor = ComputeFogFactor(output.positionCS.z);

                return output;
            }

            half4 IchorFragment(Varyings input) : SV_Target
            {
                float r = length(input.corner);

                // Ядро — маленькая яркая точка, ореол — мягкий спад до края квадрата.
                half core = saturate(1.0 - r / 0.28);
                half halo = pow(saturate(1.0 - r), 3.0) * 0.55;

                half flicker = 0.75 + 0.25 * sin(_Time.y * (5.0 + input.seed * 6.0) + input.seed * 40.0);

                half3 color = input.color.rgb * (core * core * 1.6 + halo) * flicker * _Glow;

                // Свечение гаснет туманом в чёрное, а не в цвет тумана — как у ореолов ламп.
                color = MixFogColor(color, half3(0, 0, 0), input.fogFactor);

                return half4(color, 1.0);
            }
            ENDHLSL
        }
    }
}
