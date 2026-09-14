// Порода катакомб. Меш Marching Cubes не имеет UV, поэтому текстура кладётся
// трипланарно — по трём мировым проекциям с весами от нормали.
//
// Освещение в пещере держится на одном фонаре у игрока, поэтому всё, что от него
// отвёрнуто, прямого света не получает вовсе. Раньше такая грань падала почти в чистый
// чёрный и на стыке с освещённой соседкой читалась как залитый краской многоугольник.
// Здесь этого не происходит по трём причинам, см. CaveSurface() и CaveLightContribution().
//
// Версия под URP. Surface-шейдеров в URP нет, поэтому весь тракт написан руками:
// вершинный и фрагментный этапы, свой цикл по источникам и отдельные проходы для теней
// и глубины. Имена свойств оставлены прежними — ассет материала и эталонные значения
// в CatacombWorldEditor.ApplyCaveMaterialPreset продолжают работать без правок.
Shader "Mine Generator/Cave Triplanar"
{
    Properties
    {
        _Color ("Цвет породы", Color) = (0.44, 0.41, 0.37, 1)
        _MainTex ("Текстура породы", 2D) = "white" {}
        _TexScale ("Масштаб текстуры", Float) = 0.22
        _DetailScale ("Частота детальной октавы", Float) = 5.7
        _DetailStrength ("Сила детальной октавы", Range(0,1)) = 0.5
        _DetailMid ("Средняя яркость текстуры", Range(0.1,1)) = 0.62

        [Header(Ottenok po naklonu)]
        _FloorTint ("Оттенок пола", Color) = (1.0, 0.98, 0.92, 1)
        _WallTint ("Оттенок стен", Color) = (0.72, 0.70, 0.68, 1)
        _CeilingTint ("Оттенок потолка", Color) = (0.34, 0.34, 0.40, 1)
        _TintStrength ("Сила оттенка", Range(0,1)) = 0.85

        [Header(Zalivka)]
        _SkyFill ("Заливка сверху", Color) = (0.30, 0.32, 0.38, 1)
        _GroundFill ("Заливка снизу", Color) = (0.17, 0.15, 0.14, 1)
        _MinLight ("Пол освещённости", Range(0,1)) = 0.06
        _FloorLevel ("Жёсткий пол яркости", Range(0,1)) = 0.03

        [Header(Relef)]
        [Normal] _BumpMap ("Карта нормалей породы", 2D) = "bump" {}
        _BumpScale ("Сила рельефа", Range(0,3)) = 1.2

        [Header(Terminator)]
        _Wrap ("Заворот света за терминатор", Range(0,1)) = 0.25

        [Header(Blik)]
        _Glossiness ("Гладкость", Range(0,1)) = 0.06
        _Metallic ("Металличность", Range(0,1)) = 0.0
        _SpecAA ("Гашение блика на изломах", Range(0,4)) = 2.0
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

        // Общая часть всех проходов: свойства и построение поверхности. Освещение сюда
        // не входит намеренно — Lighting.hlsl тянет за собой весь свет и тени, а проходам
        // глубины и теней он не нужен и только раздувает время компиляции.
        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        TEXTURE2D(_MainTex);
        SAMPLER(sampler_MainTex);

        TEXTURE2D(_BumpMap);
        SAMPLER(sampler_BumpMap);

        // Всё, что объявлено в Properties и не является текстурой, обязано лежать
        // в этом буфере — иначе SRP Batcher откажется собирать материал в пакет,
        // а чанков в кадре десятки.
        CBUFFER_START(UnityPerMaterial)
            half4 _Color;
            float _TexScale;
            float _DetailScale;
            half _DetailStrength;
            half _DetailMid;

            half4 _FloorTint;
            half4 _WallTint;
            half4 _CeilingTint;
            half _TintStrength;

            half4 _SkyFill;
            half4 _GroundFill;
            half _MinLight;
            half _FloorLevel;

            half _BumpScale;
            half _Wrap;

            half _Glossiness;
            half _Metallic;
            half _SpecAA;
        CBUFFER_END

        /// Результат разбора поверхности в точке. Мировая нормаль после рельефа держится
        /// отдельно от геометрической: рельеф нужен только освещению, а оттенок по наклону
        /// и полусферическая заливка должны идти от нормали самой геометрии. Иначе рельеф
        /// начнёт перекрашивать породу — пиксель с бампом «вниз» получал бы цвет потолка.
        struct CaveSurface
        {
            half3 albedo;
            half3 emission;
            half smoothness;
            float3 bumpedNormalWS;
        };

        /// Трипланарный бамп сразу в мировом пространстве, whiteout-смешение.
        ///
        /// У меша Marching Cubes нет ни UV, ни касательных: касательные выводятся из UV,
        /// а UV здесь не существует в принципе. Обычная карта нормалей живёт в касательном
        /// пространстве и сюда не встаёт. В Built-in это лечилось лишним полем в структуре
        /// вывода поверхности; в URP тракт свой, и бамп просто кладётся прямо в
        /// inputData.normalWS — никакой матрицы из касательных в цепочке нет.
        ///
        /// Берётся одна частота — детальная, та же, что у мелкой октавы альбедо. Крупную
        /// добавлять незачем: на масштабе четырёх метров форму и так лепит сама геометрия,
        /// а каждая лишняя частота стоит трёх выборок текстуры.
        float3 TriplanarBump(float3 positionWS, float3 n, float3 blend)
        {
            float3 p = positionWS * (_TexScale * _DetailScale);

            half3 bx = UnpackNormalScale(SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, p.zy), _BumpScale);
            half3 by = UnpackNormalScale(SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, p.xz), _BumpScale);
            half3 bz = UnpackNormalScale(SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, p.xy), _BumpScale);

            // Whiteout: касательные составляющие складываются с мировой нормалью, а её
            // собственная ось берётся по модулю и умножается на знак грани. Без знака
            // рельеф на противоположных стенках хода выворачивается наизнанку.
            bx = half3(bx.xy + n.zy, abs(bx.z) * n.x);
            by = half3(by.xy + n.xz, abs(by.z) * n.y);
            bz = half3(bz.xy + n.xy, abs(bz.z) * n.z);

            return normalize(bx.zyx * blend.x + by.xzy * blend.y + bz.xyz * blend.z);
        }

        CaveSurface BuildCaveSurface(float3 positionWS, float3 normalWS)
        {
            CaveSurface surface;

            float3 n = normalize(normalWS);

            // Веса проекций: чем сильнее нормаль смотрит вдоль оси, тем больше её вклад.
            float3 blend = abs(n);
            blend = blend / max(blend.x + blend.y + blend.z, 1e-4);

            float3 p = positionWS * _TexScale;

            half3 albedo = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, p.zy).rgb * blend.x
                         + SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, p.xz).rgb * blend.y
                         + SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, p.xy).rgb * blend.z;

            // Детальная октава той же текстуры на частоте в _DetailScale раз выше.
            // Базовый тайл — около 4.5 метра, вблизи он расплывается в мыло; детальная
            // возвращает зерно камня. Множитель нецелый, иначе две частоты резонируют
            // и повтор становится виден сеткой.
            float3 pd = positionWS * (_TexScale * _DetailScale);
            half3 det = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, pd.zy).rgb * blend.x
                      + SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, pd.xz).rgb * blend.y
                      + SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, pd.xy).rgb * blend.z;

            // Делим на среднюю яркость текстуры: так октава модулирует, а не темнит.
            albedo *= lerp(half3(1, 1, 1), det / max(_DetailMid, 0.1h), _DetailStrength);

            // 1 — пол, 0.5 — стена, 0 — потолок.
            float up = n.y * 0.5 + 0.5;

            // smoothstep, а не линейный lerp: нормали берутся из градиента поля плотности,
            // и на воксельной сетке они гуляют от грани к грани на десяток-другой градусов.
            // Линейная рампа переносила этот разброс в цвет один к одному; smoothstep
            // прижимает его у самых полюсов, где разброс заметнее всего.
            half3 tint = up > 0.5
                ? lerp(_WallTint.rgb, _FloorTint.rgb, smoothstep(0.0, 1.0, (up - 0.5) * 2.0))
                : lerp(_CeilingTint.rgb, _WallTint.rgb, smoothstep(0.0, 1.0, up * 2.0));

            // Оттенок ослабляется целиком: полусферическая заливка ниже и так темнит вниз,
            // и на полной силе два затемнения перемножались.
            tint = lerp(half3(1, 1, 1), tint, _TintStrength);

            half3 baseAlbedo = _Color.rgb * albedo;
            surface.albedo = baseAlbedo * tint;

            // 1. Заливка считается от альбедо БЕЗ оттенка по наклону. Когда оттенок входил
            //    и сюда, потолок получал двойное умножение и уходил в чёрный.
            // 2. Полусфера сверху вниз, а не константа, — объём остаётся читаемым.
            // 3. Второе слагаемое — жёсткий пол, он берётся от чистой текстуры, а не от
            //    baseAlbedo: цвет породы в линейном пространстве около 0.16, текстура около
            //    0.21, их произведение 0.03 — доля от такого альбедо остаётся чёрной при
            //    любом множителе. Держите его маленьким: это плоская добавка, не зависящая
            //    от источников, и на больших значениях она съедает всю светотень.
            half3 fill = lerp(_GroundFill.rgb, _SkyFill.rgb, up);
            surface.emission = baseAlbedo * max(fill, half3(_MinLight, _MinLight, _MinLight))
                             + albedo * _FloorLevel;

            // Зеркальное отражение поворачивается вдвое быстрее нормали, поэтому на изломе
            // между гранями соседние пиксели выбирают совсем разные точки окружения — блик
            // рассыпается на чёрное и яркое, и тем сильнее, чем выше гладкость. fwidth даёт
            // скачок нормали на пиксель; там, где он велик, гладкость гасится.
            half nvar = saturate(length(fwidth(n)) * _SpecAA);
            surface.smoothness = _Glossiness * (1.0 - nvar);

            surface.bumpedNormalWS = TriplanarBump(positionWS, n, blend);

            return surface;
        }
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            ZWrite On
            Cull Back

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex CaveForwardVertex
            #pragma fragment CaveForwardFragment

            // Освещение и тени.
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma multi_compile _ EVALUATE_SH_MIXED EVALUATE_SH_VERTEX

            // Карт освещения здесь нет и быть не может: меш порождается в рантайме
            // и не имеет UV, разворачивать нечего. Поэтому ни LIGHTMAP_ON, ни маски
            // теней в вариантах не заводится — на WebGL каждый лишний вариант это
            // лишнее время компиляции шейдера при первом показе.

            // Ради него всё и затевалось: при Forward+ источники раздаются кластерами
            // по тайлам экрана, а не поштучно каждому рендереру. Именно поштучная раздача
            // давала ровный вертикальный шов на стыке соседних чанков, когда ламп в кадре
            // было больше попиксельного бюджета.
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP

            #pragma multi_compile_instancing

            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Fog.hlsl"

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
                half3 vertexSH    : TEXCOORD2;
                half fogFactor    : TEXCOORD3;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings CaveForwardVertex(Attributes input)
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

                // Ambient. Пустой при полностью попиксельном разборе, L2 при смешанном —
                // режим задаётся в ассете URP, и шейдер должен уметь оба.
                OUTPUT_SH(output.normalWS.xyz, output.vertexSH);

                output.fogFactor = ComputeFogFactor(positionInputs.positionCS.z);

                return output;
            }

            /// Вклад одного источника: обычная физическая модель плюс заворот за терминатор.
            ///
            /// Фонарь висит на камере, поэтому грани, идущие вдоль взгляда, оказываются ровно
            /// на терминаторе: N*L около нуля, прямого света почти нет. Обычный ламберт режет
            /// их по нулю, и рядом с освещённой стенкой они читаются как чёрные полосы.
            ///
            /// Заливкой это не лечится: альбедо породы в линейном пространстве около 0.03,
            /// и любой множитель от него остаётся тёмным. Поэтому свет заворачивается за
            /// терминатор — добавляем ровно ту разницу, которую даёт обёрнутый ламберт сверх
            /// обычного. Освещённой стороны это не касается: там разница равна нулю.
            ///
            /// В отличие от Built-in, light.color здесь идёт БЕЗ затухания — его дают
            /// distanceAttenuation и shadowAttenuation, и их надо домножить руками.
            half3 CaveLightContribution(BRDFData brdfData, Light light, half3 albedo,
                                        float3 normalWS, half3 viewDirectionWS)
            {
                half3 color = LightingPhysicallyBased(brdfData, light, normalWS, viewDirectionWS);

                half ndl = dot(normalWS, light.direction);
                half extra = saturate((ndl + _Wrap) / (1.0h + _Wrap)) - saturate(ndl);

                half3 attenuated = light.color * (light.distanceAttenuation * light.shadowAttenuation);

                return color + albedo * attenuated * extra;
            }

            /// Повторяет UniversalFragmentPBR, но вклад каждого источника считает через
            /// CaveLightContribution. Отдельным вторым циклом заворот не сделать дёшево:
            /// при Forward+ сам обход кластера стоит заметно дороже, чем добавленные
            /// к источнику скалярное произведение и пара операций.
            half4 CaveFragmentPBR(InputData inputData, SurfaceData surfaceData)
            {
                BRDFData brdfData;
                InitializeBRDFData(surfaceData, brdfData);

                half4 shadowMask = CalculateShadowMask(inputData);
                AmbientOcclusionFactor aoFactor = CreateAmbientOcclusionFactor(inputData, surfaceData);
                Light mainLight = GetMainLight(inputData, shadowMask, aoFactor);

                MixRealtimeAndBakedGI(mainLight, inputData.normalWS, inputData.bakedGI);

                LightingData lightingData = CreateLightingData(inputData, surfaceData);

                lightingData.giColor = GlobalIllumination(brdfData, inputData.bakedGI,
                                                          aoFactor.indirectAmbientOcclusion,
                                                          inputData.positionWS, inputData.normalWS,
                                                          inputData.viewDirectionWS);

                lightingData.mainLightColor = CaveLightContribution(brdfData, mainLight, surfaceData.albedo,
                                                                    inputData.normalWS, inputData.viewDirectionWS);

                #if defined(_ADDITIONAL_LIGHTS)
                uint pixelLightCount = GetAdditionalLightsCount();

                // При Forward+ направленные источники сверх главного в кластеры не попадают
                // и обходятся отдельно. В катакомбах такой один — заполняющий.
                #if USE_CLUSTER_LIGHT_LOOP
                [loop] for (uint directionalIndex = 0;
                            directionalIndex < min(URP_FP_DIRECTIONAL_LIGHTS_COUNT, MAX_VISIBLE_LIGHTS);
                            directionalIndex++)
                {
                    CLUSTER_LIGHT_LOOP_SUBTRACTIVE_LIGHT_CHECK

                    Light light = GetAdditionalLight(directionalIndex, inputData, shadowMask, aoFactor);

                    lightingData.additionalLightsColor += CaveLightContribution(
                        brdfData, light, surfaceData.albedo, inputData.normalWS, inputData.viewDirectionWS);
                }
                #endif

                LIGHT_LOOP_BEGIN(pixelLightCount)
                    Light light = GetAdditionalLight(lightIndex, inputData, shadowMask, aoFactor);

                    lightingData.additionalLightsColor += CaveLightContribution(
                        brdfData, light, surfaceData.albedo, inputData.normalWS, inputData.viewDirectionWS);
                LIGHT_LOOP_END
                #endif

                #if defined(_ADDITIONAL_LIGHTS_VERTEX)
                lightingData.vertexLightingColor += inputData.vertexLighting * brdfData.diffuse;
                #endif

                return CalculateFinalColor(lightingData, surfaceData.alpha);
            }

            half4 CaveForwardFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                CaveSurface cave = BuildCaveSurface(input.positionWS, input.normalWS);

                SurfaceData surfaceData = (SurfaceData)0;
                surfaceData.albedo = cave.albedo;
                surfaceData.emission = cave.emission;
                surfaceData.metallic = _Metallic;
                surfaceData.smoothness = cave.smoothness;
                surfaceData.occlusion = 1.0h;
                surfaceData.alpha = 1.0h;
                surfaceData.normalTS = half3(0, 0, 1);

                InputData inputData = (InputData)0;
                inputData.positionWS = input.positionWS;
                inputData.positionCS = input.positionCS;

                // Рельеф кладётся прямо сюда: никакого касательного пространства
                // в цепочке нет, преобразовывать его нечем и незачем.
                inputData.normalWS = cave.bumpedNormalWS;
                inputData.viewDirectionWS = GetWorldSpaceNormalizeViewDir(input.positionWS);

                #if defined(MAIN_LIGHT_CALCULATE_SHADOWS)
                inputData.shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                #else
                inputData.shadowCoord = float4(0, 0, 0, 0);
                #endif

                inputData.fogCoord = InitializeInputDataFog(float4(input.positionWS, 1.0), input.fogFactor);

                // Ambient. Приходит из RenderSettings.ambient* — тех самых трёх цветов,
                // которые CaveLevelMood ведёт за игроком по высоте.
                inputData.bakedGI = SampleSHPixel(input.vertexSH, inputData.normalWS);

                // Нужен и кластерам Forward+, и экранному затенению: без него свет
                // берётся не из того тайла, и по кадру идут квадраты.
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
                inputData.shadowMask = half4(1, 1, 1, 1);

                half4 color = CaveFragmentPBR(inputData, surfaceData);
                color.rgb = MixFog(color.rgb, inputData.fogCoord);

                return color;
            }
            ENDHLSL
        }

        // Тень от породы. Своя, а не из ShadowCasterPass.hlsl: тот тянет LitInput.hlsl
        // с чужим набором свойств (_BaseMap, _BaseColor, _Cutoff), которых здесь нет.
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull Back

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex CaveShadowVertex
            #pragma fragment CaveShadowFragment

            #pragma multi_compile_instancing
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            // Задаются из ShadowUtils.SetupShadowCasterConstantBuffer. Для направленного
            // источника нормальное смещение берётся вдоль _LightDirection, для точечного
            // направление в каждой вершине своё и считается от _LightPosition.
            float3 _LightDirection;
            float3 _LightPosition;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings CaveShadowVertex(Attributes input)
            {
                Varyings output;

                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);

                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);

                #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                float3 lightDirectionWS = normalize(_LightPosition - positionWS);
                #else
                float3 lightDirectionWS = _LightDirection;
                #endif

                output.positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDirectionWS));
                output.positionCS = ApplyShadowClamping(output.positionCS);

                return output;
            }

            half4 CaveShadowFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                return 0;
            }
            ENDHLSL
        }

        // Глубина. Нужна экранному затенению, мягким частицам и всему, что читает
        // _CameraDepthTexture.
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ZWrite On
            ColorMask R
            Cull Back

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex CaveDepthVertex
            #pragma fragment CaveDepthFragment

            #pragma multi_compile_instancing

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings CaveDepthVertex(Attributes input)
            {
                Varyings output = (Varyings)0;

                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);

                return output;
            }

            half4 CaveDepthFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                return 0;
            }
            ENDHLSL
        }

        // Глубина вместе с нормалями. Отдаёт нормаль ПОСЛЕ рельефа — экранное затенение
        // должно видеть ту же поверхность, что и освещение, иначе оно ляжет мимо.
        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }

            ZWrite On
            Cull Back

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex CaveDepthNormalsVertex
            #pragma fragment CaveDepthNormalsFragment

            #pragma multi_compile_instancing

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings CaveDepthNormalsVertex(Attributes input)
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

                return output;
            }

            half4 CaveDepthNormalsFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                CaveSurface cave = BuildCaveSurface(input.positionWS, input.normalWS);

                return half4(NormalizeNormalPerPixel(cave.bumpedNormalWS), 0.0);
            }
            ENDHLSL
        }
    }

    FallBack "Universal Render Pipeline/Lit"
}
