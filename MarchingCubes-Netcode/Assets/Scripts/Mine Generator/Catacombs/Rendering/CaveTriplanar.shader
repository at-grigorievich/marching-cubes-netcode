// Порода катакомб. Меш Marching Cubes не имеет UV, поэтому текстура кладётся
// трипланарно — по трём мировым проекциям с весами от нормали.
//
// Освещение в пещере держится на одном фонаре у игрока, поэтому всё, что от него
// отвёрнуто, прямого света не получает вовсе. Раньше такая грань падала почти в чистый
// чёрный и на стыке с освещённой соседкой читалась как залитый краской многоугольник.
// Здесь этого не происходит по трём причинам, см. surf() и LightingCaveStandard().
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
        Tags { "RenderType" = "Opaque" }
        LOD 200

        CGPROGRAM
        #pragma surface surf CaveStandard fullforwardshadows
        #pragma target 3.0

        // При своей модели освещения Unity не подставляет этот инклуд сама,
        // а без него нет ни SurfaceOutputStandard, ни UnityGI.
        #include "UnityPBSLighting.cginc"

        half _Wrap;

        // Свой вывод поверхности вместо SurfaceOutputStandard — ради поля Bump.
        //
        // У меша Marching Cubes нет ни UV, ни касательных: касательные брать неоткуда,
        // они выводятся из UV, а UV здесь не существует в принципе. Поэтому обычная
        // карта нормалей, которая живёт в касательном пространстве, сюда не встаёт —
        // Unity преобразовала бы её матрицей из нулей.
        //
        // Обходим так: трипланарный бамп считается сразу в мировом пространстве и едет
        // в лишнем поле Bump, а модель освещения подставляет его вместо s.Normal.
        // Поле o.Normal при этом не трогается намеренно: стоит записать в него хоть что-то,
        // и генератор шейдеров решит, что шейдер использует касательное пространство,
        // и вставит то самое преобразование, которого мы избегаем.
        //
        // Цена альтернативы: касательные пришлось бы складывать в меш — это ещё 16 байт
        // на вершину, около мегабайта на уровень, и пересчёт на каждый удар киркой.
        struct CaveSurfaceOutput
        {
            fixed3 Albedo;
            float3 Normal;
            half3 Emission;
            half Metallic;
            half Smoothness;
            half Occlusion;
            fixed Alpha;

            // Мировая нормаль после рельефа.
            float3 Bump;
        };

        inline SurfaceOutputStandard CaveToStandard(CaveSurfaceOutput s)
        {
            SurfaceOutputStandard o;
            o.Albedo = s.Albedo;
            o.Normal = s.Bump;
            o.Emission = s.Emission;
            o.Metallic = s.Metallic;
            o.Smoothness = s.Smoothness;
            o.Occlusion = s.Occlusion;
            o.Alpha = s.Alpha;

            return o;
        }

        // Фонарь висит на камере, поэтому грани, идущие вдоль взгляда, оказываются ровно
        // на терминаторе: N*L около нуля, прямого света почти нет. Обычный ламберт режет
        // их по нулю, и рядом с освещённой стенкой они читаются как чёрные полосы.
        //
        // Заливкой это не лечится: альбедо породы в линейном пространстве около 0.03,
        // и любой множитель от него остаётся тёмным. Поэтому свет заворачивается за
        // терминатор — добавляем ровно ту разницу, которую даёт обёрнутый ламберт сверх
        // обычного. Освещённой стороны это не касается: там разница равна нулю.
        inline void LightingCaveStandard_GI(CaveSurfaceOutput s, UnityGIInput data, inout UnityGI gi)
        {
            SurfaceOutputStandard std = CaveToStandard(s);
            LightingStandard_GI(std, data, gi);
        }

        inline half4 LightingCaveStandard(CaveSurfaceOutput s, half3 viewDir, UnityGI gi)
        {
            SurfaceOutputStandard std = CaveToStandard(s);
            half4 c = LightingStandard(std, viewDir, gi);

            half ndl = dot(std.Normal, gi.light.dir);
            half extra = saturate((ndl + _Wrap) / (1.0h + _Wrap)) - saturate(ndl);

            // gi.light.color уже включает затухание источника.
            c.rgb += s.Albedo * gi.light.color * extra;
            return c;
        }

        sampler2D _MainTex;
        sampler2D _BumpMap;
        half _BumpScale;

        fixed4 _Color;
        float _TexScale;
        float _DetailScale;
        half _DetailStrength;
        half _DetailMid;

        fixed4 _FloorTint;
        fixed4 _WallTint;
        fixed4 _CeilingTint;
        half _TintStrength;

        fixed4 _SkyFill;
        fixed4 _GroundFill;
        half _MinLight;
        half _FloorLevel;

        half _Glossiness;
        half _Metallic;
        half _SpecAA;

        struct Input
        {
            float3 worldPos;
            float3 worldNormal;
        };

        /// Трипланарный бамп сразу в мировом пространстве, whiteout-смешение.
        ///
        /// Берётся одна частота — детальная, та же, что у мелкой октавы альбедо. Крупную
        /// добавлять незачем: на масштабе четырёх метров форму и так лепит сама геометрия,
        /// а каждая лишняя частота стоит трёх выборок текстуры. В прямом рендере шейдер
        /// исполняется заново на каждый попиксельный источник, и эти выборки умножаются
        /// на число ламп в кадре.
        float3 TriplanarBump(float3 worldPos, float3 n, float3 blend)
        {
            float3 p = worldPos * (_TexScale * _DetailScale);

            half3 bx = UnpackScaleNormal(tex2D(_BumpMap, p.zy), _BumpScale);
            half3 by = UnpackScaleNormal(tex2D(_BumpMap, p.xz), _BumpScale);
            half3 bz = UnpackScaleNormal(tex2D(_BumpMap, p.xy), _BumpScale);

            // Whiteout: касательные составляющие складываются с мировой нормалью, а её
            // собственная ось берётся по модулю и умножается на знак грани. Без знака
            // рельеф на противоположных стенках хода выворачивается наизнанку.
            bx = half3(bx.xy + n.zy, abs(bx.z) * n.x);
            by = half3(by.xy + n.xz, abs(by.z) * n.y);
            bz = half3(bz.xy + n.xy, abs(bz.z) * n.z);

            return normalize(bx.zyx * blend.x + by.xzy * blend.y + bz.xyz * blend.z);
        }

        void surf (Input IN, inout CaveSurfaceOutput o)
        {
            float3 n = normalize(IN.worldNormal);

            // Веса проекций: чем сильнее нормаль смотрит вдоль оси, тем больше её вклад.
            float3 blend = abs(n);
            blend = blend / max(blend.x + blend.y + blend.z, 1e-4);

            float3 p = IN.worldPos * _TexScale;

            fixed3 albedo = tex2D(_MainTex, p.zy).rgb * blend.x
                          + tex2D(_MainTex, p.xz).rgb * blend.y
                          + tex2D(_MainTex, p.xy).rgb * blend.z;

            // Детальная октава той же текстуры на частоте в _DetailScale раз выше.
            // Базовый тайл — около 4.5 метра, вблизи он расплывается в мыло; детальная
            // возвращает зерно камня. Множитель нецелый, иначе две частоты резонируют
            // и повтор становится виден сеткой.
            float3 pd = IN.worldPos * (_TexScale * _DetailScale);
            fixed3 det = tex2D(_MainTex, pd.zy).rgb * blend.x
                       + tex2D(_MainTex, pd.xz).rgb * blend.y
                       + tex2D(_MainTex, pd.xy).rgb * blend.z;

            // Делим на среднюю яркость текстуры: так октава модулирует, а не темнит.
            albedo *= lerp(fixed3(1, 1, 1), det / max(_DetailMid, 0.1h), _DetailStrength);

            // 1 — пол, 0.5 — стена, 0 — потолок.
            float up = n.y * 0.5 + 0.5;

            // smoothstep, а не линейный lerp: нормали берутся из градиента поля плотности,
            // и на воксельной сетке они гуляют от грани к грани на десяток-другой градусов.
            // Линейная рампа переносила этот разброс в цвет один к одному; smoothstep
            // прижимает его у самых полюсов, где разброс заметнее всего.
            fixed3 tint = up > 0.5
                ? lerp(_WallTint.rgb, _FloorTint.rgb, smoothstep(0.0, 1.0, (up - 0.5) * 2.0))
                : lerp(_CeilingTint.rgb, _WallTint.rgb, smoothstep(0.0, 1.0, up * 2.0));

            // Оттенок ослабляется целиком: полусферическая заливка ниже и так темнит вниз,
            // и на полной силе два затемнения перемножались.
            tint = lerp(fixed3(1, 1, 1), tint, _TintStrength);

            fixed3 baseAlbedo = _Color.rgb * albedo;
            o.Albedo = baseAlbedo * tint;

            // 1. Заливка считается от альбедо БЕЗ оттенка по наклону. Когда оттенок входил
            //    и сюда, потолок получал двойное умножение и уходил в чёрный.
            // 2. Полусфера сверху вниз, а не константа, — объём остаётся читаемым.
            // 3. Второе слагаемое — жёсткий пол, он берётся от чистой текстуры, а не от
            //    baseAlbedo: цвет породы в линейном пространстве около 0.16, текстура около
            //    0.21, их произведение 0.03 — доля от такого альбедо остаётся чёрной при
            //    любом множителе. Держите его маленьким: это плоская добавка, не зависящая
            //    от источников, и на больших значениях она съедает всю светотень.
            fixed3 fill = lerp(_GroundFill.rgb, _SkyFill.rgb, up);
            o.Emission = baseAlbedo * max(fill, fixed3(_MinLight, _MinLight, _MinLight))
                       + albedo * _FloorLevel;

            // Зеркальное отражение поворачивается вдвое быстрее нормали, поэтому на изломе
            // между гранями соседние пиксели выбирают совсем разные точки окружения — блик
            // рассыпается на чёрное и яркое, и тем сильнее, чем выше гладкость. fwidth даёт
            // скачок нормали на пиксель; там, где он велик, гладкость гасится.
            half nvar = saturate(length(fwidth(n)) * _SpecAA);

            o.Metallic = _Metallic;
            o.Smoothness = _Glossiness * (1.0 - nvar);
            o.Alpha = 1.0;
            o.Occlusion = 1.0;

            // o.Normal здесь не трогается, и это не забывчивость. Генератор шейдеров решает,
            // что шейдер работает в касательном пространстве, по самому факту записи в это
            // поле — и тогда требует INTERNAL_DATA и матрицу из касательных, которых у меша
            // Marching Cubes нет. Рельеф едет в o.Bump, а модель освещения подставляет его
            // вместо s.Normal, см. CaveToStandard.
            //
            // Рельеф считается последним: он нужен только освещению, а заливка и оттенок
            // по наклону должны идти от нормали самой геометрии. Иначе рельеф начнёт
            // перекрашивать породу — пиксель с бампом «вниз» получал бы цвет потолка.
            o.Bump = TriplanarBump(IN.worldPos, n, blend);
        }
        ENDCG
    }

    FallBack "Diffuse"
}
