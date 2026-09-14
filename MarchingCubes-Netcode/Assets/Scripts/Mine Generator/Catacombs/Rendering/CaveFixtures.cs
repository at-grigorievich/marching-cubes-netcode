using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace MineGenerator.Catacombs
{
    /// <summary>
    /// Светильники катакомб: тёплые лампы на стенах и холодные светящиеся жилы в породе.
    /// Расставляются генератором вместе с уровнем, из того же сида.
    ///
    /// Зачем они вообще существуют. Фонарь висит у глаза, поэтому N*L почти совпадает
    /// с N*V: что бы он ни освещал, это по определению обращено к зрителю, светотень
    /// получается почти постоянной, и форму породы такой свет не лепит в принципе.
    /// Замер это подтверждает — фонарь давал 62% яркости кадра при среднеквадратичном
    /// разбросе яркости 0.06–0.09, то есть картинка была плоской. Лампа висит на стене
    /// сбоку: свет идёт поперёк взгляда, тени ложатся вдоль хода, и стена наконец
    /// получает градиент вместо ровной заливки.
    ///
    /// Два вида, а не один, — ради двух цветовых температур. Тёплая лампа и холодная жила
    /// дают кадру второй тон; при одних тёплых источниках порода остаётся коричневой
    /// независимо от того, что происходит с яркостью.
    ///
    /// Почему один компонент, а не два. Размещение у них общее до последней строчки —
    /// луч из центра узла, проверка на стену, проверка на дистанцию до соседей. Две копии
    /// этого кода разъехались бы на первой же правке.
    /// </summary>
    [ExecuteAlways]
    public sealed class CaveFixtures : MonoBehaviour
    {
        [SerializeField] private CatacombWorld world;

        [Header("Лампы на стенах")]
        // Количество ламп упирается не во вкус, а в устройство Built-in RP: попиксельные
        // источники раздаются ПОШТУЧНО КАЖДОМУ РЕНДЕРЕРУ, и когда ламп больше бюджета
        // (на Ultra это 4, и одно место у фонаря), соседние чанки получают разные наборы.
        // На стыке двух чанков это видно как ровный вертикальный шов через весь экран.
        // Замер: 81 лампа — шов есть и заметен, 41 — шва нет. Яркость, потерянную при
        // прореживании, возвращаем ambient и заливкой в шейдере: они одинаковы для всех
        // объектов и швов не дают в принципе.
        [Tooltip("Сколько ламп разложить по уровню.")]
        [SerializeField] private int lampCount = 45;

        [Tooltip("Минимальное расстояние между лампами, юниты.")]
        [SerializeField] private float lampSpacing = 12f;

        [Tooltip("На какой высоте над полом искать стену под лампу.")]
        [SerializeField] private Vector2 lampHeight = new Vector2(1.6f, 2.4f);

        [SerializeField] private Color lampColor = new Color(1f, 0.52f, 0.22f);

        // Дальность и яркость подняты при переезде на URP: было 9 и 3.0.
        //
        // В Built-in точечный источник затухал по пологой табличной кривой, в URP —
        // строго обратноквадратично, да ещё с гладким окном, гасящим свет в ноль
        // у самой границы дальности. На середине хода это разница в три-семь раз,
        // и прежние значения давали втрое более тёмную сцену: доля пикселей ниже
        // порога черноты подскакивала с 23-26% до 65-83%.
        //
        // Множители подобраны замером, а не на глаз: перебор яркости от x5 до x12
        // и дальности от x1.5 до x2 по четырём эталонным ракурсам. x8 и x2 — единственная
        // пара, попадающая разом во все три метрики стиля (тёмных 25.3%, полоса 0.1-0.3
        // 73.3%, максимум 0.959). Направленный заполняющий при этом не трогали:
        // затухания по расстоянию у него нет, компенсировать нечего.
        [SerializeField] private float lampRange = 18f;
        [SerializeField] private float lampIntensity = 24f;

        /// <summary>
        /// Тени от ламп. Выключены по умолчанию, и это осознанный размен.
        ///
        /// Тень здесь получается с резким полигональным краем, и причина не в настройках:
        /// проверено перебором, разрешение карты теней от Low до VeryHigh край не меняет
        /// совсем, смещения (bias, normalBias) — тоже, цифры кадра совпадают до третьего
        /// знака. Край полигональный потому, что это силуэт самого меша: Marching Cubes
        /// даёт грани размером с воксель, и любая резкая тень от такой геометрии обводит
        /// её гранями. Карта нормалей сделала поверхность детальной и тем самым выставила
        /// этот грубый силуэт напоказ.
        ///
        /// Смягчить нечем и в URP: мягкая тень точечного источника — это всё то же
        /// небольшое PCF-ядро, ширину источника задать негде. Снижение силы тени до 0.45
        /// пятно приглушает, но полигон остаётся виден.
        ///
        /// Чем платим за выключение: свет лампы проходит сквозь породу в соседний ход,
        /// и после перехода на URP платим больше — дальность выросла с 9 до 18 юнитов.
        ///
        /// Экономия от выключения в URP уже не символическая, в отличие от Built-in:
        /// при Forward+ попиксельны все источники, а не четверо ближайших, и каждая
        /// включённая лампа — это ещё одно место в атласе теней дополнительных источников.
        /// </summary>
        [Tooltip("Тени от ламп. Дают резкий полигональный край по граням меша — см. комментарий.")]
        [SerializeField] private bool lampShadows;

        [Tooltip("Сила тени лампы, если тени включены.")]
        [SerializeField] private float lampShadowStrength = 0.45f;

        [Header("Светящиеся жилы")]
        [SerializeField] private int veinCount = 34;
        [SerializeField] private float veinSpacing = 8f;
        [SerializeField] private Color veinColor = new Color(0.24f, 0.82f, 1f);
        // Те же множители, что у ламп, и по той же причине: было 8 и 1.8.
        [SerializeField] private float veinRange = 16f;
        [SerializeField] private float veinIntensity = 14.4f;

        /// <summary>
        /// Цвет светильников по этажам.
        ///
        /// Замер показал, в чём беда с ориентированием: восемь кадров подряд с пробежки
        /// давали схожесть цветовых гистограмм 0.65, а холодных пикселей в каждом было
        /// 3–7 процентов. Проще говоря, весь уровень — один коричневый коридор, и через
        /// пять минут игрок перестаёт понимать, где был. Разные пары «тёплая лампа плюс
        /// холодная жила» на каждом этаже отвечают на вопрос «где я» мгновенно и не стоят
        /// ни одного лишнего треугольника.
        /// </summary>
        private static readonly Color[] LampByLevel =
        {
            new Color(1f, 0.52f, 0.22f),   // верх: рыжий факел
            new Color(1f, 0.72f, 0.30f),   // середина: янтарь
            new Color(1f, 0.46f, 0.32f)    // низ: угольный красный
        };

        private static readonly Color[] VeinByLevel =
        {
            new Color(0.24f, 0.82f, 1f),   // верх: холодная бирюза
            new Color(0.42f, 1f, 0.45f),   // середина: ядовитая зелень
            new Color(0.62f, 0.40f, 1f)    // низ: фиолет
        };

        public static Color LampColorFor(int level) => LampByLevel[((level % LampByLevel.Length) + LampByLevel.Length) % LampByLevel.Length];
        public static Color VeinColorFor(int level) => VeinByLevel[((level % VeinByLevel.Length) + VeinByLevel.Length) % VeinByLevel.Length];

        [Header("Общее")]
        [Tooltip("Как далеко от центра узла искать поверхность.")]
        [SerializeField] private float probeDistance = 16f;

        private static Mesh _sphere;
        private static readonly Dictionary<Color, Material> CoreMaterials = new Dictionary<Color, Material>();

        private Transform _container;

        // Мерцание всех ламп считает один Update на весь компонент, а не по MonoBehaviour
        // на каждую лампу: их полсотни, и полсотни вызовов Update в кадре — это на ровном
        // месте. Ровное дыхание синусом читается как баг освещения, поэтому перлин
        // на двух частотах со своей фазой у каждой лампы.
        private readonly List<Light> _lamps = new List<Light>();
        private readonly List<float> _lampIntensity = new List<float>();
        private readonly List<float> _lampPhase = new List<float>();
        // Ореол мерцает размером, а не цветом, и это не прихоть.
        //
        // Материал ореола общий на цвет, поэтому renderer.material для мерцания не годится:
        // это свойство создаёт КОПИЮ материала на каждый вызов, а в режиме редактирования
        // копии ещё и оседают в сцене — Unity об этом прямо ругается. К тому же у шейдера
        // Mobile/Particles/Additive из свойств есть только _MainTex, цвет в него запечён,
        // и менять было бы всё равно нечего. Масштаб же не трогает материал вовсе,
        // а пульсирующее свечение читается даже лучше, чем меняющий яркость круг.
        private readonly List<Transform> _lampHalo = new List<Transform>();
        private readonly List<float> _lampHaloScale = new List<float>();

        private void OnEnable()
        {
            if (world == null) world = GetComponent<CatacombWorld>();
            if (world == null) world = FindFirstObjectByType<CatacombWorld>();

            if (world != null) world.Generated += Rebuild;
        }

        private void OnDisable()
        {
            if (world != null) world.Generated -= Rebuild;
        }

        /// <summary>Расставляет светильники заново. Зовётся по событию генерации мира.</summary>
        public void Rebuild()
        {
            Clear();

            if (world == null) return;

            var origins = CollectOrigins();
            if (origins.Count == 0) return;

            var container = new GameObject("Cave Fixtures");
            container.transform.SetParent(transform, false);

            // Светильники восстанавливаются из сида при каждой генерации, поэтому в сцену
            // их сохранять незачем — это те же мегабайты в файле сцены, из-за которых
            // когда-то выкинули сериализацию поля плотности.
            if (!Application.isPlaying) container.hideFlags = HideFlags.DontSave;

            _container = container.transform;

            // Сид тот же, что у мира: одинаковый уровень должен светиться одинаково, иначе
            // сравнение двух скриншотов «до и после» перестаёт быть сравнением. Лампы
            // и жилы берут разные соли, чтобы не ложиться в одни и те же точки.
            PlaceLamps(origins, new System.Random(world.CurrentSeed ^ 0x1A3B));
            PlaceVeins(origins, new System.Random(world.CurrentSeed ^ 0x6C17));
        }

        /// <summary>Убирает ранее расставленные светильники.</summary>
        public void Clear()
        {
            _lamps.Clear();
            _lampIntensity.Clear();
            _lampPhase.Clear();
            _lampHalo.Clear();
            _lampHaloScale.Clear();

            if (_container != null)
            {
                DestroyNow(_container.gameObject);
                _container = null;
            }

            // Контейнер мог пережить перезагрузку домена или прийти из сохранённой сцены —
            // тогда поля уже нет, а объект есть, и светильники удваивались бы с каждой
            // генерацией. Старое имя тоже проверяем: сцены от прошлой версии носят его.
            for (var i = transform.childCount - 1; i >= 0; i--)
            {
                var child = transform.GetChild(i);
                if (child.name == "Cave Fixtures" || child.name == "Glow Veins") DestroyNow(child.gameObject);
            }
        }

        private void PlaceLamps(List<Origin> origins, System.Random random)
        {
            var placed = new List<Vector3>(lampCount);
            var spacingSqr = lampSpacing * lampSpacing;
            var attempts = lampCount * 16;

            for (var i = 0; i < attempts && placed.Count < lampCount; i++)
            {
                var height = Mathf.Lerp(lampHeight.x, lampHeight.y, (float)random.NextDouble());
                var node = origins[random.Next(origins.Count)];
                var origin = node.Position + Vector3.up * height;

                // Луч почти горизонтальный: лампа висит на стене, а не лежит на полу
                // и не приклеена к потолку.
                var yaw = (float)random.NextDouble() * Mathf.PI * 2f;
                var direction = new Vector3(Mathf.Cos(yaw), ((float)random.NextDouble() - 0.5f) * 0.2f, Mathf.Sin(yaw));

                RaycastHit hit;
                if (!Physics.Raycast(origin, direction.normalized, out hit, probeDistance,
                        ~0, QueryTriggerInteraction.Ignore)) continue;

                if (hit.distance < 2.5f) continue;

                // Стена, а не пол и не потолок: на почти горизонтальной поверхности лампа
                // торчала бы из пола столбиком.
                if (Mathf.Abs(hit.normal.y) > 0.55f) continue;

                if (TooClose(placed, hit.point, spacingSqr)) continue;

                SpawnFixture(hit.point, hit.normal, "Wall Lamp", LampColorFor(node.Level), lampRange, lampIntensity,
                    lampShadows ? LightShadows.Soft : LightShadows.None, 0.75f, 0.09f);

                placed.Add(hit.point);
            }
        }

        private void PlaceVeins(List<Origin> origins, System.Random random)
        {
            var placed = new List<Vector3>(veinCount);
            var spacingSqr = veinSpacing * veinSpacing;
            var attempts = veinCount * 12;

            for (var i = 0; i < attempts && placed.Count < veinCount; i++)
            {
                var node = origins[random.Next(origins.Count)];
                var origin = node.Position;

                var yaw = (float)random.NextDouble() * Mathf.PI * 2f;
                var pitch = ((float)random.NextDouble() - 0.4f) * 0.55f;
                var horizontal = Mathf.Cos(pitch);

                var direction = new Vector3(Mathf.Cos(yaw) * horizontal, Mathf.Sin(pitch), Mathf.Sin(yaw) * horizontal);

                RaycastHit hit;
                if (!Physics.Raycast(origin, direction, out hit, probeDistance,
                        ~0, QueryTriggerInteraction.Ignore)) continue;

                // Жила в паре шагов от узла перекрывает пол-экрана бледным пятном: ореол
                // аддитивный, вблизи он упирается в единицу и превращается в блин с резкой
                // кромкой. Жила — это ориентир в глубине хода, ей там и место.
                if (hit.distance < 4f) continue;

                if (TooClose(placed, hit.point, spacingSqr)) continue;

                // Жилы без теней: их десятки, а задача жилы — цветное пятно в темноте,
                // а не светотень.
                //
                // Раньше они были ещё и вершинными: в Built-in попиксельных мест было
                // всего четыре, и жилы приходилось уводить в вершинные вручную. В Forward+
                // вершинных источников нет вовсе — свет раздаётся кластерами по тайлам
                // экрана, и каждый источник попиксельный. Ограничение теперь другое:
                // на WebGL2 в кадре не больше 32 видимых источников на камеру.
                SpawnFixture(hit.point, hit.normal, "Vein", VeinColorFor(node.Level), veinRange, veinIntensity,
                    LightShadows.None, 0.85f, 0.055f);

                placed.Add(hit.point);
            }
        }

        private void Update()
        {
            if (_lamps.Count == 0) return;

            // Вне игры Time.time стоит, а мерцание хочется видеть и в редакторе. Часы
            // редактора при этом живут в UnityEditor, и прямая ссылка на них из рантайм-скрипта
            // ломает сборку плеера — отсюда директива.
            var time = Time.time;

#if UNITY_EDITOR
            if (!Application.isPlaying) time = (float)UnityEditor.EditorApplication.timeSinceStartup;
#endif

            for (var i = 0; i < _lamps.Count; i++)
            {
                var lamp = _lamps[i];
                if (lamp == null) continue;

                var phase = _lampPhase[i];

                var flicker = 0.86f
                              + 0.10f * Mathf.PerlinNoise(phase, time * 5.5f)
                              + 0.04f * Mathf.PerlinNoise(phase + 17f, time * 16f);

                lamp.intensity = _lampIntensity[i] * flicker;

                var halo = _lampHalo[i];
                if (halo != null) halo.localScale = Vector3.one * (_lampHaloScale[i] * Mathf.Lerp(0.88f, 1.06f, flicker));
            }
        }

        private static bool TooClose(List<Vector3> placed, Vector3 point, float spacingSqr)
        {
            foreach (var other in placed)
            {
                if ((other - point).sqrMagnitude < spacingSqr) return true;
            }

            return false;
        }

        private void SpawnFixture(Vector3 point, Vector3 normal, string name, Color color, float range,
            float intensity, LightShadows shadows, float haloSize, float coreSize)
        {
            var fixture = new GameObject(name);
            fixture.transform.SetParent(_container, false);
            fixture.transform.position = point;

            // Ориентируем по нормали поверхности: светильник смотрит из породы в пустоту.
            fixture.transform.rotation = Quaternion.LookRotation(normal);

            var lightObject = new GameObject("Light");
            lightObject.transform.SetParent(fixture.transform, false);

            // Отодвинут от стены, иначе половина сферы источника внутри породы.
            lightObject.transform.localPosition = new Vector3(0f, 0f, 0.35f);

            var light = lightObject.AddComponent<Light>();
            light.type = LightType.Point;
            light.range = range;
            light.intensity = intensity;
            light.color = color;
            light.shadows = shadows;

            if (shadows != LightShadows.None)
            {
                light.shadowStrength = lampShadowStrength;

                // Те же смещения, что у фонаря: нормали породы берутся из градиента поля
                // плотности и на гранях вокселя гуляют, поэтому обычного bias не хватает —
                // остаются полосы самозатенения. normalBias двигает выборку вдоль нормали.
                light.shadowBias = 0.05f;
                light.shadowNormalBias = 0.5f;
                light.shadowNearPlane = 0.1f;
            }

            // Ореол — аддитивный квад к камере. Без него источник виден только по следу
            // на породе: сам свет в кадре не рисуется, и лампа читается как пустое место,
            // вокруг которого почему-то светло. Насыщенность ниже, чем у света, — иначе
            // аддитивный слой поверх освещённой стены упирается в единицу и белеет.
            // Цвет берётся целиком: множитель здесь стоял, пока шейдер ореола тинт игнорировал
            // и любое значение давало белое пятно. Теперь цвет запечён в текстуру, и гасить
            // его нечем — яркость центра задаёт сам цвет источника.
            var halo = CaveGlowBillboard.Attach(lightObject.transform, haloSize, color);

            // В мерцание идут только лампы: жилы — это минерал, он не горит.
            if (name == "Wall Lamp")
            {
                _lamps.Add(light);
                _lampIntensity.Add(intensity);
                _lampPhase.Add((float)_lamps.Count * 7.13f);
                _lampHalo.Add(halo != null ? halo.transform : null);
                _lampHaloScale.Add(haloSize);
            }

            // Ядро тёплое, а не белое. Почти белое ядро пробовалось и читалось как
            // приклеенный к стене овал: аддитивный ореол вокруг него и так пересвечен,
            // и второе белое пятно внутри первого стирает у лампы центр.
            SpawnCore(lightObject.transform, coreSize, Color.Lerp(color, Color.white, 0.2f));

        }

        private static void SpawnCore(Transform parent, float size, Color color)
        {
            var core = new GameObject("Core");
            core.transform.SetParent(parent, false);
            core.transform.localScale = Vector3.one * size;

            // Меш встроенный, а не CreatePrimitive: у примитива есть коллайдер, снять его
            // в play-режиме сразу нельзя, и он цеплял бы лучи копания и выстрелов.
            core.AddComponent<MeshFilter>().sharedMesh = SphereMesh();

            var view = core.AddComponent<MeshRenderer>();
            view.sharedMaterial = CoreMaterial(color);

            // Ядро сидит внутри собственного источника: его тень падала бы сама на себя
            // чёрным ободком вокруг яркой точки.
            view.shadowCastingMode = ShadowCastingMode.Off;
            view.receiveShadows = false;
        }

        /// <summary>Узел вместе с номером этажа: от него зависит цвет светильника.</summary>
        private struct Origin
        {
            public Vector3 Position;
            public int Level;
        }

        private List<Origin> CollectOrigins()
        {
            var origins = new List<Origin>();

            for (var level = 0; ; level++)
            {
                var rooms = world.GetRoomCenters(level);
                if (rooms.Count == 0) break;

                foreach (var room in rooms) origins.Add(new Origin { Position = room, Level = level });
            }

            return origins;
        }

        private static Mesh SphereMesh()
        {
            return _sphere != null ? _sphere : _sphere = Resources.GetBuiltinResource<Mesh>("Sphere.fbx");
        }

        /// <summary>Материал на цвет: ядра ламп и жил разного цвета, общий был бы перекрашен.</summary>
        private static Material CoreMaterial(Color color)
        {
            Material cached;
            if (CoreMaterials.TryGetValue(color, out cached) && cached != null) return cached;

            var material = CaveMaterials.UnlitOpaque("Cave Fixture Core", color);

            CoreMaterials[color] = material;
            return material;
        }

        private static void DestroyNow(Object target)
        {
            if (target == null) return;

            if (Application.isPlaying) Destroy(target);
            else DestroyImmediate(target);
        }
    }
}
