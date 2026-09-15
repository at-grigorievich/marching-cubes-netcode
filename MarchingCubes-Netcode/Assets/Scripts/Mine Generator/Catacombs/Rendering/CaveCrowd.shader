// Толпа пауков. Один меш, один вызов отрисовки на партию, анимация из текстуры.
//
// Почему не SkinnedMeshRenderer с Animator: в паке у каждого паука 49 костей и
// 73 трансформа, и на трёх сотнях особей это двадцать с лишним тысяч трансформов
// плюс три сотни Animator в кадре. На WebGL, где воркеров нет и всё доезжает
// на главном потоке, это не крутится вовсе — а нужна именно толпа, как в Vampire
// Survivors: узкий коридор, залитый пауками от стены до стены.
//
// Поэтому анимация запечена в текстуру (VAT, vertex animation texture): строка — кадр,
// столбец — вершина, тексель — позиция вершины в этом кадре. Скиннинг посчитан заранее
// в редакторе (см. SpiderVatBaker), в рантайме от него остаётся одна выборка текстуры
// в вершинном шейдере. Все особи одного вида рисуются инстансингом, а фаза анимации
// у каждой своя и приезжает как свойство инстанса.
//
// Цена: анимации только те, что запечены, и смешивать их нельзя. Для толпы это ровно
// то, что нужно — там нет ни блендов, ни IK, есть «бежит», «бьёт» и «сдох».
Shader "Mine Generator/Cave Crowd"
{
    Properties
    {
        _BaseMap ("Текстура паука", 2D) = "white" {}
        _BaseColor ("Цвет", Color) = (1, 1, 1, 1)

        [NoScaleOffset] _VatPositions ("Позиции вершин по кадрам", 2D) = "black" {}
        [NoScaleOffset] _VatNormals ("Нормали вершин по кадрам", 2D) = "grey" {}

        _Smoothness ("Гладкость", Range(0,1)) = 0.18
        _Metallic ("Металличность", Range(0,1)) = 0.0

        // Тот же приём, что у породы: паук в неосвещённом коридоре иначе проваливается
        // в чистый чёрный и читается дырой в кадре, а не силуэтом.
        _MinLight ("Пол освещённости", Range(0,1)) = 0.02

        // Блика глаз здесь больше нет, и это не забыли удалить — отказались намеренно.
        //
        // Он был: маска головы в UV1.y и самосвет по ней при развороте на камеру.
        // В кадре это читалось по-детски, и причина не в силе свечения, а в том,
        // что светилась не та область. Глаза у паука это несколько миллиметров,
        // а геометрическая маска «передняя верхняя часть» захватывает всю головогрудь —
        // то есть светился кусок размером с ладонь. Сделать её по-настоящему узкой
        // геометрией нельзя: отдельных глаз в меше нет, они нарисованы в текстуре,
        // а порогом по альбедо их не отличить от таких же тёмных пятен на брюшке.
        //
        // Если возвращать, то не маской, а отдельной мелкой геометрией или каналом
        // в текстуре пака — то есть это правка ассетов, а не шейдера.

        [Header(Silhouette)]
        // Контровой свет по кромке. Без него паук на фоне породы того же тона
        // теряет силуэт: замер по кадру показывал, что дальние особи сливаются со стеной.
        _RimColor ("Цвет кромки", Color) = (0.55, 0.65, 0.85, 1)
        _RimPower ("Узость кромки", Range(0.5,8)) = 2.6
        _RimStrength ("Сила кромки", Range(0,3)) = 0.55
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Geometry"
        }

        LOD 200

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        TEXTURE2D(_BaseMap);
        SAMPLER(sampler_BaseMap);

        // Обе карты анимации читаются точечной выборкой: тексель здесь не цвет, а число,
        // и билинейная фильтрация смешала бы соседние ВЕРШИНЫ по горизонтали — то есть
        // тянула бы вершину к случайному соседу по индексу в буфере.
        TEXTURE2D(_VatPositions);
        SAMPLER(sampler_VatPositions);
        float4 _VatPositions_TexelSize;

        TEXTURE2D(_VatNormals);
        SAMPLER(sampler_VatNormals);

        CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            half4 _BaseColor;
            half _Smoothness;
            half _Metallic;
            half _MinLight;


            half4 _RimColor;
            half _RimPower;
            half _RimStrength;
        CBUFFER_END

        // Состояние анимации особи.
        //
        // x — первая строка клипа в текстуре, y — сколько в клипе кадров,
        // z — фаза от 0 до 1, w — зациклен ли клип.
        //
        // Зацикленность приезжает с инстансом, а не лежит в материале, потому что
        // клипы у одного вида разные: бег кольцевой, удар и смерть играются один раз
        // и замирают на последнем кадре. Без флага конец удара прыгал бы на начало.
        //
        // Объявление инстанс-буфера отключает шейдер от SRP Batcher — это ожидаемо
        // и является здесь целью: пакетирует толпу инстансинг, а не батчер.
        UNITY_INSTANCING_BUFFER_START(CrowdProps)
            UNITY_DEFINE_INSTANCED_PROP(float4, _AnimState)
            UNITY_DEFINE_INSTANCED_PROP(float4, _InstanceTint)
        UNITY_INSTANCING_BUFFER_END(CrowdProps)

        /// Позиция и нормаль вершины в объектном пространстве на текущем кадре анимации.
        ///
        /// Позиция смешивается между двумя соседними кадрами, нормаль — нет, и это
        /// не экономия наугад: клипы запечены с исходных 25 кадров в секунду, и без
        /// смешения бег заметно дёргается. Нормаль же за 1/25 секунды поворачивается
        /// на единицы градусов, в пещерном освещении разница не видна, а выборка
        /// текстуры в вершинном шейдере на WebGL не бесплатна.
        void SampleVat(float2 vatUV, float4 state, out float3 positionOS, out float3 normalOS)
        {
            float frames = max(state.y, 1.0);
            float looped = state.w;

            // У кольцевого клипа последний кадр смыкается с нулевым, поэтому шкала идёт
            // по числу кадров; у одноразового последний кадр — это конец, и шкала короче
            // на кадр, иначе хвост клипа проскакивается.
            float f = saturate(state.z) * (looped > 0.5 ? frames : frames - 1.0);

            float f0 = floor(f);
            float blend = f - f0;

            float f1 = looped > 0.5 ? fmod(f0 + 1.0, frames) : min(f0 + 1.0, frames - 1.0);
            f0 = looped > 0.5 ? fmod(f0, frames) : min(f0, frames - 1.0);

            float invHeight = _VatPositions_TexelSize.y;

            float2 uv0 = float2(vatUV.x, (state.x + f0 + 0.5) * invHeight);
            float2 uv1 = float2(vatUV.x, (state.x + f1 + 0.5) * invHeight);

            float3 p0 = SAMPLE_TEXTURE2D_LOD(_VatPositions, sampler_VatPositions, uv0, 0).xyz;
            float3 p1 = SAMPLE_TEXTURE2D_LOD(_VatPositions, sampler_VatPositions, uv1, 0).xyz;

            positionOS = lerp(p0, p1, blend);

            float3 packed = SAMPLE_TEXTURE2D_LOD(_VatNormals, sampler_VatNormals, uv0, 0).xyz;
            normalOS = normalize(packed * 2.0 - 1.0);
        }
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            ZWrite On
            Cull Back

            HLSLPROGRAM
            // 3.5, а не 3.0 как у породы: выборка текстуры в вершинном шейдере.
            // На WebGL2 это есть, на WebGL1 нет — но URP 17 первый и не поддерживает.
            #pragma target 3.5
            #pragma vertex CrowdForwardVertex
            #pragma fragment CrowdForwardFragment

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile _ EVALUATE_SH_MIXED EVALUATE_SH_VERTEX

            // Ради этого толпа и живёт в том же Forward+, что и порода: источники
            // раздаются кластерами по тайлам экрана, и паук в коридоре получает
            // ровно те лампы, что освещают стены вокруг него.
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP

            #pragma multi_compile_instancing

            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Fog.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
                float2 vatUV      : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
                float2 uv         : TEXCOORD2;
                half3 vertexSH    : TEXCOORD3;
                half fogFactor    : TEXCOORD4;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings CrowdForwardVertex(Attributes input)
            {
                Varyings output = (Varyings)0;

                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                float4 state = UNITY_ACCESS_INSTANCED_PROP(CrowdProps, _AnimState);

                float3 positionOS;
                float3 normalOS;
                SampleVat(input.vatUV, state, positionOS, normalOS);

                VertexPositionInputs positionInputs = GetVertexPositionInputs(positionOS);
                VertexNormalInputs normalInputs = GetVertexNormalInputs(normalOS);

                output.positionCS = positionInputs.positionCS;
                output.positionWS = positionInputs.positionWS;
                output.normalWS = normalInputs.normalWS;
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);


                OUTPUT_SH(output.normalWS.xyz, output.vertexSH);

                output.fogFactor = ComputeFogFactor(positionInputs.positionCS.z);

                return output;
            }

            half4 CrowdForwardFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);

                half4 tint = UNITY_ACCESS_INSTANCED_PROP(CrowdProps, _InstanceTint);

                half4 baseColor = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv) * _BaseColor;

                // Разброс яркости по особям. Толпа из одинаковых мешей читается как
                // размноженная копия, и на дистанции боя это заметно раньше силуэта.
                baseColor.rgb *= tint.rgb;

                SurfaceData surface = (SurfaceData)0;
                surface.albedo = baseColor.rgb;
                surface.metallic = _Metallic;
                surface.smoothness = _Smoothness;
                surface.occlusion = 1.0;
                surface.alpha = 1.0;

                InputData inputData = (InputData)0;
                inputData.positionWS = input.positionWS;
                inputData.normalWS = normalize(input.normalWS);
                inputData.viewDirectionWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
                inputData.shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                inputData.fogCoord = input.fogFactor;
                inputData.bakedGI = SAMPLE_GI(0, input.vertexSH, inputData.normalWS);
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
                inputData.shadowMask = half4(1, 1, 1, 1);

                half4 color = UniversalFragmentPBR(inputData, surface);

                // Пол освещённости поверх результата, а не подмешанный в bakedGI:
                // заливка в bakedGI проходит через модель отражения и на скользящих
                // углах гасится почти в ноль — ровно там, где паук и теряется.
                color.rgb = max(color.rgb, surface.albedo * _MinLight);

                half3 normalWS = inputData.normalWS;
                half3 viewWS = inputData.viewDirectionWS;

                half facing = saturate(dot(normalWS, viewWS));

                // Контровой свет по кромке: без него паук на фоне породы того же тона
                // теряет силуэт, а силуэт здесь — единственное, чем он читается
                // на дистанции боя.
                color.rgb += _RimColor.rgb * (_RimStrength * pow(1.0 - facing, _RimPower));

                color.rgb = MixFog(color.rgb, inputData.fogCoord);

                return half4(color.rgb, 1.0);
            }
            ENDHLSL
        }

        // Тень от толпы. Со стороны кода по умолчанию выключена (SpiderCrowd.castShadows):
        // при Forward+ попиксельны ВСЕ источники, и три сотни теней в атласе дополнительных
        // источников стоят дороже, чем дают. Проход есть, чтобы включение было галочкой,
        // а не переписыванием шейдера.
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull Back

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex CrowdShadowVertex
            #pragma fragment CrowdShadowFragment

            #pragma multi_compile_instancing
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;
            float3 _LightPosition;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 vatUV      : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings CrowdShadowVertex(Attributes input)
            {
                Varyings output;

                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);

                float4 state = UNITY_ACCESS_INSTANCED_PROP(CrowdProps, _AnimState);

                float3 positionOS;
                float3 normalOS;
                SampleVat(input.vatUV, state, positionOS, normalOS);

                float3 positionWS = TransformObjectToWorld(positionOS);
                float3 normalWS = TransformObjectToWorldNormal(normalOS);

                #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                float3 lightDirectionWS = normalize(_LightPosition - positionWS);
                #else
                float3 lightDirectionWS = _LightDirection;
                #endif

                output.positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDirectionWS));
                output.positionCS = ApplyShadowClamping(output.positionCS);

                return output;
            }

            half4 CrowdShadowFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                return 0;
            }
            ENDHLSL
        }

        // Глубина. Без неё толпа выпадает из экранного затенения и мягких частиц —
        // взрыв гранаты рисовался бы поверх паука, а не вокруг него.
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ZWrite On
            ColorMask R
            Cull Back

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex CrowdDepthVertex
            #pragma fragment CrowdDepthFragment

            #pragma multi_compile_instancing

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 vatUV      : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings CrowdDepthVertex(Attributes input)
            {
                Varyings output = (Varyings)0;

                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                float4 state = UNITY_ACCESS_INSTANCED_PROP(CrowdProps, _AnimState);

                float3 positionOS;
                float3 normalOS;
                SampleVat(input.vatUV, state, positionOS, normalOS);

                output.positionCS = TransformObjectToHClip(positionOS);

                return output;
            }

            half4 CrowdDepthFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                return 0;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }

            ZWrite On
            Cull Back

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex CrowdDepthNormalsVertex
            #pragma fragment CrowdDepthNormalsFragment

            #pragma multi_compile_instancing

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 vatUV      : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS   : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings CrowdDepthNormalsVertex(Attributes input)
            {
                Varyings output = (Varyings)0;

                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                float4 state = UNITY_ACCESS_INSTANCED_PROP(CrowdProps, _AnimState);

                float3 positionOS;
                float3 normalOS;
                SampleVat(input.vatUV, state, positionOS, normalOS);

                output.positionCS = TransformObjectToHClip(positionOS);
                output.normalWS = TransformObjectToWorldNormal(normalOS);

                return output;
            }

            half4 CrowdDepthNormalsFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                return half4(NormalizeNormalPerPixel(input.normalWS), 0.0);
            }
            ENDHLSL
        }
    }

    FallBack Off
}
