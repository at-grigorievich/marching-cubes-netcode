using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace MineGenerator.Catacombs
{
    /// <summary>
    /// Генераторы света — ядро игры, а не оформление.
    ///
    /// Катакомбы стартуют погашенными целиком (см. <see cref="CaveFixtures"/>), и весь забег
    /// состоит из одного: найти в темноте генератор, запустить его и продержаться, пока он
    /// разгорается. Разгорелся — район вспыхивает навсегда, орда из него разбегается,
    /// и генератор становится точкой старта следующего забега.
    ///
    /// Почему генератор это ЕЩЁ И ВЫХОД. Отдельная дверь плюс отдельный рубильник — это две
    /// сущности, которые игрок должен связать в голове, а на вебе, где решают первые тридцать
    /// секунд, такую связь никто устанавливать не будет. Когда это один объект, игра
    /// объясняется одной фразой: иди туда, где светится. Заодно снимается главная дыра
    /// чистого «найди выход» — при нём драться незачем, оптимально пробежать мимо всех,
    /// и лучшая часть жанра становится необязательной.
    ///
    /// Три числа, которыми это настраивается, и все три подобраны под забег в пять-семь
    /// минут: <see cref="chargeTime"/> задаёт длину кульминации, <see cref="districtRadius"/> —
    /// сколько мира отдаётся за один забег, <see cref="surgeScale"/> — насколько тяжело
    /// эту кульминацию пережить.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public sealed class CaveGenerators : MonoBehaviour
    {
        [SerializeField] private CatacombWorld world;
        [SerializeField] private CaveFixtures fixtures;
        [SerializeField] private SpiderCrowd crowd;

        [Header("Расстановка")]
        [Tooltip("Сколько генераторов на уровень. Это же число забегов на прохождение.")]
        [SerializeField, Range(1, 12)] private int count = 4;

        /// <summary>
        /// Минимальное расстояние между генераторами.
        ///
        /// Меньше двух радиусов района ставить нельзя: районы перекроются, и второй
        /// генератор окажется в уже освещённом куске — то есть забег за ним пройдёт
        /// без темноты и почти без пауков, ради которых всё и затевалось.
        /// </summary>
        [Tooltip("Минимальное расстояние между генераторами, юниты.")]
        [SerializeField] private float spacing = 70f;

        [Tooltip("Радиус района, который зажигает один генератор, юниты.")]
        [SerializeField] private float districtRadius = 32f;

        [Header("Запуск")]
        [Tooltip("С какого расстояния можно запустить генератор, юниты.")]
        [SerializeField] private float useRange = 4f;

        /// <summary>
        /// Сколько секунд разгорается генератор — то есть сколько длится кульминация забега.
        ///
        /// Полторы минуты, а не двадцать секунд: за двадцать орда не успевает собраться,
        /// и запуск читается как нажатие кнопки, а не как оборона. И не пять минут: забег
        /// целиком должен укладываться в пять-семь, иначе на вебе его не доигрывают.
        /// </summary>
        [Tooltip("Сколько секунд разгорается генератор.")]
        [SerializeField, Range(5f, 240f)] private float chargeTime = 90f;

        [Header("Ответ орды")]
        [Tooltip("Во сколько раз больше пауков, пока генератор разгорается.")]
        [SerializeField, Range(1f, 5f)] private float surgeScale = 2.6f;

        /// <summary>
        /// Сколько секунд орда разбегается после вспышки.
        ///
        /// Десять, а не пять. Пяти хватало, чтобы среднее расстояние до генератора
        /// выросло наполовину, — и это выглядело как успех в отчёте прогона. А глазами
        /// не менялось НИЧЕГО: число особей в районе падало с 985 до 916, то есть
        /// на семь процентов. Чтобы толпа ушла за радиус района, ей нужно этот радиус
        /// пройти, а в давке она делает три-четыре юнита в секунду.
        ///
        /// Это же и урок про метрику: среднее по разбегающейся толпе растёт от того,
        /// что дальние убежали далеко, и про ближних не говорит ничего.
        /// </summary>
        [Tooltip("Сколько секунд орда разбегается после вспышки.")]
        [SerializeField, Range(0f, 30f)] private float panicTime = 10f;

        /// <summary>
        /// Во сколько раз сфера паники шире района.
        ///
        /// Шире единицы намеренно: на самой границе района особи иначе остались бы стоять
        /// вплотную к свету, и стал бы виден сам круг, которого в мире нет.
        /// </summary>
        [Tooltip("Во сколько раз сфера паники шире района.")]
        [SerializeField, Range(1f, 2f)] private float panicReach = 1.3f;

        [Header("Вид")]
        [Tooltip("Цвет ядра погашенного генератора: заметен в темноте, но не светит.")]
        [SerializeField] private Color darkColor = new Color(0.30f, 0.46f, 0.60f);

        [SerializeField] private Color litColor = new Color(1f, 0.86f, 0.55f);

        [Tooltip("Дальность и яркость зажжённого генератора. Больше, чем у лампы: это центр " +
                 "отвоёванного района, его должно быть видно издалека.")]
        [SerializeField] private float litRange = 30f;

        [SerializeField] private float litIntensity = 42f;

        [Tooltip("Как далеко вниз искать пол под генератор.")]
        [SerializeField] private float probeDistance = 20f;

        /// <summary>Что сейчас с генератором.</summary>
        public enum Stage
        {
            Dark,
            Charging,
            Lit
        }

        private sealed class Generator
        {
            public Vector3 Position;
            public Stage Stage;

            /// <summary>Разгорание, 0..1.</summary>
            public float Charge;

            public Light Light;
            public Transform Halo;
            public float HaloScale;
            public Renderer Core;
        }

        private readonly List<Generator> _generators = new List<Generator>();
        private readonly List<Vector4> _litZones = new List<Vector4>();
        private readonly List<Vector3> _darkPoints = new List<Vector3>();

        private Transform _container;

        /// <summary>Последний зажжённый: отсюда начинается следующий забег.</summary>
        public Vector3? Respawn { get; private set; }

        public int Count => _generators.Count;
        public int LitCount { get; private set; }

        /// <summary>Уровень пройден: все районы засвечены.</summary>
        public bool Cleared => _generators.Count > 0 && LitCount >= _generators.Count;

        /// <summary>Разгорание идущего сейчас генератора, 0..1. Ноль, если ни один не запущен.</summary>
        public float Charging { get; private set; }

        /// <summary>Радиус района, который зажигает один генератор. Нужен инструментам проверки.</summary>
        public float DistrictRadius => districtRadius;

        /// <summary>Сколько секунд разгорается генератор.</summary>
        public float ChargeTime => chargeTime;

        /// <summary>Зовётся, когда район вспыхнул. Аргумент — позиция генератора.</summary>
        public event Action<Vector3> Ignited;

        private void OnEnable()
        {
            EnsureLinks();

            if (world != null) world.Generated += Rebuild;
        }

        private void OnDisable()
        {
            if (world != null) world.Generated -= Rebuild;
        }

        /// <summary>
        /// Достаёт ссылки. Отдельно от OnEnable, потому что у обычного MonoBehaviour
        /// в режиме редактирования OnEnable не вызывается вовсе, и у инструментов проверки
        /// ссылки были бы пустыми.
        /// </summary>
        public void EnsureLinks()
        {
            if (world == null) world = GetComponent<CatacombWorld>();
            if (world == null) world = FindFirstObjectByType<CatacombWorld>();

            if (fixtures == null) fixtures = FindFirstObjectByType<CaveFixtures>();
            if (crowd == null) crowd = FindFirstObjectByType<SpiderCrowd>();
        }

        /// <summary>Расставляет генераторы заново. Зовётся по событию генерации мира.</summary>
        public void Rebuild()
        {
            EnsureLinks();
            Clear();

            if (world == null) return;

            var rooms = CollectRooms();
            if (rooms.Count == 0) return;

            var container = new GameObject("Cave Generators");
            container.transform.SetParent(transform, false);

            // Как и светильники, восстанавливаются из сида и в сцену не сохраняются:
            // это те же мегабайты в файле сцены, из-за которых когда-то выкинули
            // сериализацию поля плотности.
            if (!Application.isPlaying) container.hideFlags = HideFlags.DontSave;

            _container = container.transform;

            // Своя соль к сиду мира: иначе генераторы легли бы ровно туда же, куда лампы.
            Place(rooms, new System.Random(world.CurrentSeed ^ 0x51D3));

            PublishState();
        }

        /// <summary>Убирает расставленные генераторы.</summary>
        public void Clear()
        {
            _generators.Clear();
            _litZones.Clear();
            _darkPoints.Clear();

            LitCount = 0;
            Charging = 0f;
            Respawn = null;

            if (crowd != null)
            {
                crowd.SetLitZones(null);
                crowd.SetHotPoint(null);
                crowd.ClearEvents();
            }

            if (_container != null)
            {
                DestroyNow(_container.gameObject);
                _container = null;
            }

            // Контейнер мог пережить перезагрузку домена: тогда поля уже нет, а объект есть,
            // и генераторы удваивались бы с каждой генерацией.
            for (var i = transform.childCount - 1; i >= 0; i--)
            {
                var child = transform.GetChild(i);
                if (child.name == "Cave Generators") DestroyNow(child.gameObject);
            }
        }

        private void Place(List<Vector3> rooms, System.Random random)
        {
            var spacingSqr = spacing * spacing;
            var attempts = rooms.Count * 8;

            for (var i = 0; i < attempts && _generators.Count < count; i++)
            {
                var room = rooms[random.Next(rooms.Count)];

                // Ставим на пол, а не в центр зала: центр может висеть в воздухе, и генератор
                // читался бы как парящая железка. Луч здесь допустим — он начинается
                // в пустоте зала, а не внутри породы (см. грабли про проверку связности
                // физикой: запрет касается лучей, НАЧАТЫХ в толще камня).
                var position = room;

                if (Physics.Raycast(room, Vector3.down, out var hit, probeDistance, ~0,
                        QueryTriggerInteraction.Ignore))
                {
                    position = hit.point + Vector3.up * 0.6f;
                }

                if (TooClose(position, spacingSqr)) continue;

                _generators.Add(Spawn(position));
            }

            // Залов на уровне может оказаться меньше, чем нужно генераторов, и разрядка
            // по spacing не наберёт их числа. Один быть обязан: без него у забега
            // нет цели вовсе.
            if (_generators.Count == 0) _generators.Add(Spawn(rooms[0]));
        }

        private bool TooClose(Vector3 point, float spacingSqr)
        {
            foreach (var generator in _generators)
            {
                if ((generator.Position - point).sqrMagnitude < spacingSqr) return true;
            }

            return false;
        }

        private Generator Spawn(Vector3 position)
        {
            var root = new GameObject("Generator");
            root.transform.SetParent(_container, false);
            root.transform.position = position;

            var light = root.AddComponent<Light>();
            light.type = LightType.Point;
            light.range = litRange;
            light.intensity = litIntensity;
            light.color = litColor;
            light.shadows = LightShadows.None;

            // Погашенный источник выключен целиком, а не приглушён: на WebGL2 в кадре
            // не больше 32 видимых источников на камеру, и держать место под то,
            // что не светит, нельзя.
            light.enabled = false;

            const float haloSize = 2.4f;

            // Ореол сразу тёплого цвета, а холод погашенного даёт ядро и размер.
            // Иначе на каждый цвет заводился бы свой материал ореола, и генераторы
            // перестали бы собираться в одну партию отрисовки с лампами.
            var halo = CaveGlowBillboard.Attach(root.transform, haloSize, litColor);

            var core = SpawnCore(root.transform, 0.5f, darkColor);

            return new Generator
            {
                Position = position,
                Stage = Stage.Dark,
                Light = light,
                Halo = halo != null ? halo.transform : null,
                HaloScale = haloSize,
                Core = core
            };
        }

        /// <summary>Размер ореола в каждом из состояний. Погашенный — тусклая искра вдалеке.</summary>
        private const float DarkHalo = 0.42f;

        private const float LitHalo = 1.25f;

        private void Update()
        {
            if (_generators.Count == 0) return;

            var time = Time.time;

#if UNITY_EDITOR
            if (!Application.isPlaying) time = (float)UnityEditor.EditorApplication.timeSinceStartup;
#endif

            // Разгорание идёт только в игре. В режиме редактирования оно дотикало бы
            // до конца само, пока сцена открыта, и уровень оказался бы зажжён ещё
            // до первого запуска.
            var running = Application.isPlaying;

            Charging = 0f;

            foreach (var generator in _generators)
            {
                switch (generator.Stage)
                {
                    case Stage.Dark:
                        // Дышит вместо мерцания: погашенный генератор должен читаться как
                        // спящая машина, которую можно разбудить, а не как сломанная лампа.
                        Pulse(generator, DarkHalo * (0.85f + 0.15f * Mathf.Sin(time * 1.4f + generator.Position.x)));
                        break;

                    case Stage.Charging:
                        if (running)
                        {
                            generator.Charge = Mathf.Min(1f,
                                generator.Charge + Time.deltaTime / Mathf.Max(0.5f, chargeTime));
                        }

                        Charging = Mathf.Max(Charging, generator.Charge);

                        // Чем ближе к концу, тем чаще и ярче: сколько осталось держаться,
                        // игрок должен понимать по самому генератору, не глядя в интерфейс.
                        var beat = 2f + 10f * generator.Charge;
                        var swell = Mathf.Lerp(DarkHalo, LitHalo * 1.7f, generator.Charge);

                        Pulse(generator, swell * (0.85f + 0.15f * Mathf.Sin(time * beat)));

                        generator.Light.intensity = litIntensity * Mathf.Lerp(0.12f, 1f, generator.Charge);

                        if (generator.Charge >= 1f && running) Ignite(generator);
                        break;

                    case Stage.Lit:
                        Pulse(generator, LitHalo);
                        break;
                }
            }
        }

        private void Pulse(Generator generator, float scale)
        {
            if (generator.Halo == null) return;

            generator.Halo.localScale = Vector3.one * (generator.HaloScale * scale);
        }

        /// <summary>
        /// Запускает ближайший погашенный генератор, если игрок достаточно близко.
        /// </summary>
        /// <returns>true, если запуск состоялся.</returns>
        public bool TryActivate(Vector3 worldPoint)
        {
            var generator = NearestDark(worldPoint, out var distance);

            if (generator == null || distance > useRange) return false;
            if (generator.Stage != Stage.Dark) return false;

            generator.Stage = Stage.Charging;
            generator.Charge = 0f;

            SetCore(generator, litColor);

            // Свет включается сразу, но слабый, и растёт вместе с разгоранием: игрок должен
            // видеть, что машина ожила, иначе первые секунды обороны выглядят как ошибка.
            generator.Light.enabled = true;
            generator.Light.intensity = litIntensity * 0.12f;

            // Орда сбегается на запуск. Это единственное место забега, где игрок
            // не отходит, а держится.
            if (crowd != null) crowd.SurgeFor(chargeTime, surgeScale);

            return true;
        }

        /// <summary>
        /// Дожигает запущенный генератор мгновенно.
        ///
        /// Не читерство для игрока, а инструмент: разгорание длится полторы минуты,
        /// и проверять вспышку, панику и запрет спавна, выжидая их вживую каждый раз,
        /// невозможно. Этим же пользуется прогон в batchmode, где play-режима нет вовсе.
        /// </summary>
        /// <returns>true, если было что дожигать.</returns>
        public bool ForceFinish()
        {
            foreach (var generator in _generators)
            {
                if (generator.Stage != Stage.Charging) continue;

                Ignite(generator);
                return true;
            }

            return false;
        }

        /// <summary>Ближайший незапущенный генератор. Этим стенд показывает направление.</summary>
        public bool TryGetNearestDark(Vector3 worldPoint, out Vector3 position, out float distance)
        {
            var generator = NearestDark(worldPoint, out distance);

            position = generator != null ? generator.Position : Vector3.zero;
            return generator != null;
        }

        private Generator NearestDark(Vector3 worldPoint, out float distance)
        {
            Generator best = null;
            var bestDistance = float.MaxValue;

            foreach (var generator in _generators)
            {
                if (generator.Stage == Stage.Lit) continue;

                var d = (generator.Position - worldPoint).sqrMagnitude;
                if (d >= bestDistance) continue;

                best = generator;
                bestDistance = d;
            }

            distance = best != null ? Mathf.Sqrt(bestDistance) : float.MaxValue;
            return best;
        }

        /// <summary>
        /// Вспышка: район загорается навсегда, орда из него разбегается.
        /// </summary>
        private void Ignite(Generator generator)
        {
            generator.Stage = Stage.Lit;
            generator.Charge = 1f;

            generator.Light.enabled = true;
            generator.Light.intensity = litIntensity;

            LitCount++;
            Respawn = generator.Position;

            if (fixtures != null) fixtures.SetLit(generator.Position, districtRadius, true);

            if (crowd != null)
            {
                // Всплеск снимается ВМЕСТЕ со вспышкой, и это не мелочь. Осада кончилась
                // ровно в тот момент, когда загорелся свет; если оставить множитель
                // населения тикать дальше, пополнение затягивает район быстрее, чем
                // толпа из него разбегается. Замер до этой правки: в районе было 850
                // особей, а через две с половиной секунды ПОСЛЕ вспышки — 907, то есть
                // награда за оборону выглядела как её продолжение.
                crowd.ClearEvents();

                // Паника после снятия всплеска и до запрета спавна: иначе те, кто уже
                // внутри района, остались бы стоять, а новых просто не заводилось бы —
                // и вспышка читалась бы как «толпа медленно рассосалась», а не как
                // «они бросились врассыпную».
                crowd.Panic(generator.Position, districtRadius * panicReach, panicTime);
            }

            PublishState();

            Ignited?.Invoke(generator.Position);
        }

        /// <summary>
        /// Раздаёт наружу то, что зависит от состояния районов: где не заводить пополнение,
        /// куда стягивать толпу и куда вести жилами.
        /// </summary>
        private void PublishState()
        {
            _litZones.Clear();
            _darkPoints.Clear();

            foreach (var generator in _generators)
            {
                if (generator.Stage == Stage.Lit)
                {
                    _litZones.Add(new Vector4(generator.Position.x, generator.Position.y,
                        generator.Position.z, districtRadius));
                }
                else
                {
                    _darkPoints.Add(generator.Position);
                }
            }

            if (crowd != null)
            {
                crowd.SetLitZones(_litZones);

                // Толпа гуще у ближайшего незажжённого генератора. Точка одна, а не список:
                // ведёт она игрока, а вести в две стороны сразу нельзя.
                crowd.SetHotPoint(_darkPoints.Count > 0 ? _darkPoints[0] : (Vector3?)null);
            }

            // Тление жил ведёт к оставшимся тёмным генераторам. Зажжённые из списка убраны:
            // подсказка должна указывать туда, где игрок ещё не был.
            if (fixtures != null) fixtures.ApplyGuide(_darkPoints, districtRadius * 2f);
        }

        private static readonly Dictionary<Color, Material> CoreMaterials = new Dictionary<Color, Material>();

        private static Mesh _sphere;

        private static Renderer SpawnCore(Transform parent, float size, Color color)
        {
            var core = new GameObject("Core");
            core.transform.SetParent(parent, false);
            core.transform.localScale = Vector3.one * size;

            // Меш встроенный, а не CreatePrimitive: у примитива есть коллайдер, и он
            // перехватывал бы лучи выстрелов и копания.
            core.AddComponent<MeshFilter>().sharedMesh =
                _sphere != null ? _sphere : _sphere = Resources.GetBuiltinResource<Mesh>("Sphere.fbx");

            var view = core.AddComponent<MeshRenderer>();
            view.sharedMaterial = CoreMaterial(color);
            view.shadowCastingMode = ShadowCastingMode.Off;
            view.receiveShadows = false;

            return view;
        }

        private static void SetCore(Generator generator, Color color)
        {
            if (generator.Light != null) generator.Light.color = color;
            if (generator.Core != null) generator.Core.sharedMaterial = CoreMaterial(color);
        }

        /// <summary>
        /// Материал ядра, по одному на цвет.
        ///
        /// Ключ по цвету обязателен: статический кэш без ключа по аргументу в этом проекте
        /// уже дважды давал «правишь цвет — на экране старый», с ореолами и с паутиной.
        /// </summary>
        private static Material CoreMaterial(Color color)
        {
            Material cached;
            if (CoreMaterials.TryGetValue(color, out cached) && cached != null) return cached;

            var material = CaveMaterials.UnlitOpaque("Cave Generator Core", color);

            CoreMaterials[color] = material;
            return material;
        }

        private List<Vector3> CollectRooms()
        {
            var rooms = new List<Vector3>();

            for (var level = 0; ; level++)
            {
                var centres = world.GetRoomCenters(level);
                if (centres.Count == 0) break;

                rooms.AddRange(centres);
            }

            return rooms;
        }

        private static void DestroyNow(UnityEngine.Object target)
        {
            if (target == null) return;

            if (Application.isPlaying) Destroy(target);
            else DestroyImmediate(target);
        }
    }
}
