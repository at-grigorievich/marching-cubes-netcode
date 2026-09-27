using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using Random = Unity.Mathematics.Random;

namespace MineGenerator.Catacombs
{
    /// <summary>
    /// Толпа пауков: поведение на джобах, отрисовка инстансингом.
    ///
    /// Ни одного GameObject на особь. Состояние лежит в нативном массиве, шаг поведения
    /// это три джоба под Burst, а в кадр уходят матрицы партиями по сотне с небольшим.
    /// Так и получается то, ради чего всё делалось: коридор, залитый пауками от стены
    /// до стены, на платформе без воркеров.
    ///
    /// Почему не ECS, раз уж речь про толпу. Entities сюда не тянется по одной конкретной
    /// причине: рисует толпу в ECS модуль Entities Graphics, а он стоит на
    /// BatchRendererGroup с вычислительными буферами, которых в WebGL2 нет вовсе.
    /// То есть пришлось бы всё равно писать свою отрисовку — ровно ту, что здесь, —
    /// а взамен получить систему, где вся выгода от многопоточности отключена
    /// (webGLThreadsSupport = 0, воркеров нет). Burst и джобы дают тот же выигрыш
    /// в арифметике без этой цены: на WebGL джоб просто доезжает на главном потоке,
    /// но уже скомпилированный в машинный код, а не в IL.
    ///
    /// Вся математика идёт в ЛОКАЛЬНЫХ координатах генерации — тех же, в которых лежат
    /// чанки и поле плотности. В мир переводится только на выходе, матрицей.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SpiderCrowd : MonoBehaviour
    {
        [SerializeField] private CatacombWorld world;

        [Tooltip("За кем бежит толпа. Пусто — возьмётся отладочный стенд со сцены.")]
        [SerializeField] private Transform target;

        [Tooltip("Камера для отсева невидимого. Пусто — возьмётся камера цели. " +
                 "Camera.main тут не годится: камера стенда создаётся кодом и без тега.")]
        [SerializeField] private Camera cullCamera;

        [Header("Виды")]
        [Tooltip("Запечённые виды пауков. Заполняет пункт меню «Пауки: запечь анимацию в текстуры».")]
        [SerializeField] private SpiderKind[] kinds;

        [Header("Население")]
        [Tooltip("Потолок особей. Память под них выделяется один раз и не растёт.")]
        [SerializeField, Range(0, 4000)] private int capacity = 1200;

        /// <summary>
        /// Сколько особей держать живыми в затишье.
        ///
        /// Снижено с восьмисот после того, как кадр показал предел читаемости: в коридоре
        /// в кадре было 690 особей из 800, и породы за ними не видно вовсе. Орда перестаёт
        /// пугать, когда становится обоями. Страх теперь добирается не числом,
        /// а разрежением, волнами и засадами.
        /// </summary>
        [Tooltip("Сколько особей держать живыми в затишье.")]
        [SerializeField, Range(0, 4000)] private int population = 450;

        [Tooltip("Сколько особей в секунду досылать взамен убитых.")]
        [SerializeField, Min(0f)] private float spawnRate = 45f;

        [Header("Волны")]
        /// <summary>
        /// Во сколько раз население на гребне волны больше, чем в затишье.
        ///
        /// Постоянная плотность — главная причина, по которой орда перестаёт читаться
        /// как угроза через минуту: к ней привыкают. Волна возвращает ритм: затишье,
        /// нарастание, накат.
        /// </summary>
        [Tooltip("Во сколько раз население на гребне волны больше, чем в затишье.")]
        [SerializeField, Range(1f, 4f)] private float waveScale = 2.2f;

        [Tooltip("Длина полного цикла волны, секунды.")]
        [SerializeField, Min(1f)] private float wavePeriod = 26f;

        [Tooltip("Какую долю цикла занимает гребень. Остальное — затишье и переходы.")]
        [SerializeField, Range(0.05f, 0.8f)] private float waveCrest = 0.25f;

        [Header("Ближний круг")]
        [Tooltip("Радиус ближнего круга, юниты. В нём же толпа лезет друг на друга — дальше только держится.")]
        [SerializeField, Range(2f, 30f)] private float nearRadius = 12f;

        /// <summary>
        /// Сколько особей пускать в ближний круг, остальные ждут дальше. Ноль — без потолка.
        ///
        /// Был 70: без потолка вся орда сжималась в кольцо у игрока, и в кадре терялся
        /// силуэт. Пользователь это решение отменил: «остальные пауки айдлят вдалеке
        /// и ничего не делают, из-за этого чувство страха и противности теряется —
        /// пусть ползают друг по другу, лезут на игрока, заполняют пространство перед
        /// ним». Замер при потолке: у игрока 839 особей, из них бьют 70, стоят 308 —
        /// 40% от всех, кто не в засаде. Теперь давка разрешается не ожиданием,
        /// а ярусами: упёршийся лезет соседу на спину.
        /// </summary>
        [Tooltip("Сколько особей пускать в ближний круг. 0 — без потолка: все лезут к игроку.")]
        [SerializeField, Range(0, 500)] private int nearCap;

        [Tooltip("Сколько убитых разом останавливают досыл на паузу — пролом в толпе должен постоять.")]
        [SerializeField, Range(1, 500)] private int holdKillCount = 24;

        [Tooltip("И какую долю из тех, кто был в ближнем круге вокруг взрыва, они должны составлять.")]
        [SerializeField, Range(0f, 1f)] private float holdShare = 0.4f;

        [Header("Куча")]
        [Tooltip("Выше этого особь по спинам соседей не лезет, юниты. Страховка: высоту кучи " +
                 "задаёт число ярусов, а свод срезает её по месту.")]
        [SerializeField, Range(0f, 4f)] private float maxClimb = 2.4f;

        /// <summary>
        /// Сколько ярусов в куче, считая стоящих на камне. Пользователь: «чтобы могли залезать
        /// друг на друга не только в два ряда, а например и в три — полностью заполняли пространство».
        /// </summary>
        [Tooltip("Сколько ярусов в куче, считая стоящих на камне. 2 — наездники только на стоящих.")]
        [SerializeField, Range(2, 6)] private int pileTiers = 3;

        [Tooltip("С какой скоростью упёршаяся ставит передние лапы на спину соседа, юниты в секунду. " +
                 "Дальше она всходит по горбу его спины на ходу, со скоростью, которую требует крутизна.")]
        [SerializeField, Range(0.5f, 20f)] private float climbRate = 3f;

        [Tooltip("Ускорение падения, когда опоры не стало, юниты в секунду за секунду.")]
        [SerializeField, Range(1f, 100f)] private float gravity = 30f;

        [Tooltip("Насколько упёршаяся особь берёт вбок — в обход затора, на стену и на свод.")]
        [SerializeField, Range(0f, 4f)] private float detourWeight = 1.2f;

        [Tooltip("Ближе этого к игроку особь идёт на него прямо по своей поверхности, " +
                 "а не по полю потока — так стенные остаются на стенах.")]
        [SerializeField, Range(0f, 30f)] private float wallRange = 8f;

        [Header("Личное пространство игрока")]
        /// <summary>
        /// Ближе этого к камере особь не бывает: её сдвигает вбок по поверхности.
        ///
        /// Без этого, стоило зайти в толпу, особи оказывались вплотную к камере, и экран
        /// целиком занимали огромные лапы — пользователь назвал это «куча-мала».
        /// </summary>
        [Tooltip("Ближе этого к камере особь не бывает, юниты.")]
        [SerializeField, Range(0f, 4f)] private float eyeRadius = 1.4f;

        [Tooltip("С какой скоростью влезших в личное пространство сдвигает прочь, юниты в секунду.")]
        [SerializeField, Range(0.5f, 30f)] private float shoveSpeed = 8f;

        [Header("Стенолазы")]
        [Tooltip("Какая доля толпы тянется на стены и вверх по ним, к своду.")]
        [SerializeField, Range(0f, 1f)] private float climberShare = 0.35f;

        [Tooltip("Насколько сильно стенолаз тянется вверх по стене.")]
        [SerializeField, Range(0f, 3f)] private float climberPull = 0.9f;

        /// <summary>
        /// Сколько секунд засада ждёт игрока в среднем (разброс от 0.6 до 1.4 этого).
        ///
        /// Без терпения засада копилась: засадник срывается, только когда игрок подойдёт
        /// на семь юнитов, а в боковые ходы игрок не заходит. К концу стадии 95% живых
        /// висели по стенам, занимали место в населении, и досыла не было — ещё одна
        /// причина жалобы «остальные айдлят вдалеке».
        ///
        /// Десять, а не двадцать: при двадцати засадников в установившемся режиме висело
        /// за сотню (четверть досыла на гребне — шесть в секунду — на двадцать секунд),
        /// и в разборе 141 из 206 дальних стоял неподвижно.
        /// </summary>
        [Tooltip("Сколько секунд засада ждёт игрока в среднем. Не дождалась — срывается сама.")]
        [SerializeField, Range(1f, 120f)] private float lurkPatience = 10f;

        [Header("Отлив")]
        /// <summary>
        /// Сколько особей в секунду уходит, когда волна спала.
        ///
        /// Без отлива спад волны меняет только скорость досыла: пауки сами не умирают,
        /// и те, кто ждёт за ближним кругом, стоят там до конца стадии. Замер это
        /// и показал — в передышке живых было 722 при цели 249, перед финалом 1009
        /// при 372. Передышка, которой не видно, — не передышка.
        /// </summary>
        [Tooltip("Сколько особей в секунду уходит в темноту, когда волна спала.")]
        [SerializeField, Range(0f, 200f)] private float ebbRate = 25f;

        /// <summary>
        /// Сколько секунд уходящая особь бежит прочь. Потом, если она уже далеко, уходит
        /// в щель; если рядом — возвращается в строй.
        ///
        /// Недолго, и это не про скорость. Катакомбы без тупиков, и бег «прочь по потоку»
        /// кончается в дальней точке петли вокруг игрока — а она часто ближе полосы спавна.
        /// Первая версия ждала там двенадцать секунд и возвращала особь в кольцо: замер
        /// давал 2341 вернувшихся на 484 ушедших и 909 живых перед финалом при цели 372.
        /// </summary>
        [Tooltip("Сколько секунд уходящая особь бежит прочь, прежде чем уйти в щель или вернуться.")]
        [SerializeField, Range(1f, 30f)] private float ebbSeconds = 6f;

        [Tooltip("Насколько живых должно быть больше цели, чтобы начался отлив. " +
                 "Запас нужен, чтобы толпа не дёргалась туда-сюда на каждой ряби.")]
        [SerializeField, Range(1f, 2f)] private float ebbSlack = 1.1f;

        [Header("Смерть")]
        [Tooltip("Сила отброса трупа в эпицентре взрыва, юнитов в секунду.")]
        [SerializeField, Range(0f, 40f)] private float deathImpulse = 14f;

        [Tooltip("Как быстро гаснет полёт трупа, доля в секунду.")]
        [SerializeField, Range(0.5f, 12f)] private float corpseDrag = 2.5f;

        /// <summary>
        /// Сколько секунд не досылать пополнение после крупного убийства.
        ///
        /// Без паузы досыл затягивает пролом мгновенно, и игрок не видит того, что сделал:
        /// взрыв убивал сто с лишним особей, а плотность в кадре не менялась. Пауза
        /// и есть обратная связь.
        /// </summary>
        [Tooltip("Сколько секунд не досылать пополнение после крупного убийства.")]
        [SerializeField, Range(0f, 8f)] private float spawnHoldAfterKill = 2.5f;

        /// <summary>
        /// Полоса удаления, в шагах сетки, где появляются новые особи.
        ///
        /// Ближе прежнего намеренно. Раньше полоса была 12-40 шагов, и толпа
        /// размазывалась по половине уровня: живых четыре сотни, а в кадре
        /// семь десятков. Плотность в кадре задаёт не население, а то, на какой
        /// площади оно распределено.
        /// </summary>
        [Tooltip("На каком удалении от игрока появляться, в шагах сетки. Узкая полоса " +
                 "ближе к игроку даёт плотную толпу в кадре при том же населении.")]
        [SerializeField] private Vector2Int spawnSteps = new Vector2Int(6, 26);

        [Header("Сетка навигации")]
        /// <summary>
        /// Сторона клетки сетки. Подобрана перебором (CatacombCrowdSweep), а не на глаз.
        ///
        /// Единица — единственное значение, которое держит связность на всех проверенных
        /// сидах: 95.0 / 93.8 / 94.4 процента проходимых клеток достижимо на сидах
        /// 1337, 2 и 777. Соседние значения выглядят не хуже на одном сиде и разваливаются
        /// на другом — 1.25 даёт 96.0 / 28.8 / 95.2, полтора 64.5 / 29.2 / 93.5. Причина
        /// в щелях: уровень намеренно сужается местами до пары юнитов, и клетка крупнее
        /// единицы через такую щель не проходит. Мельче единицы связность не растёт,
        /// а сетка дорожает кубически — 0.75 это уже 32 мегабайта против 13.
        /// </summary>
        [Tooltip("Сторона клетки поля потока, юниты. Подобрана перебором, см. комментарий.")]
        [SerializeField, Range(0.5f, 4f)] private float cellSize = 1f;

        /// <summary>
        /// Дальше этого от камня клетка непроходима: держаться не за что.
        ///
        /// Задаёт толщину «обитаемой корки» вдоль стен. В коридоре в три с половиной
        /// юнита полтора юнита покрывают почти всё сечение, то есть толпа заполняет ход
        /// целиком; в зале остаётся корка по стенам, а середина пустует — и это верно,
        /// пауку посреди зала держаться не за что.
        /// </summary>
        [Tooltip("Дальше этого от камня особь держаться не может, юниты.")]
        [SerializeField, Range(0.5f, 4f)] private float attachRange = 1.6f;

        [Tooltip("Докуда считать путь от игрока, в шагах сетки. Ограничение делает " +
                 "стоимость пересчёта независимой от размера мира.")]
        [SerializeField, Range(32, 512)] private int floodSteps = 250;

        [Tooltip("Не чаще этого пересчитывать поток, секунды. Пересчёт идёт только когда " +
                 "игрок сменил клетку, но при беге по прямой это каждые пол-секунды.")]
        [SerializeField, Min(0f)] private float flowInterval = 0.25f;

        [Header("Стая")]
        [SerializeField, Range(0f, 8f)] private float separation = 3.2f;
        [SerializeField, Range(0f, 2f)] private float alignment = 0.35f;
        [SerializeField, Range(0f, 2f)] private float cohesion = 0.2f;

        /// <summary>
        /// Радиус, в котором особь видит соседей. Он же задаёт клетку пространственной
        /// сетки, а искать по ней можно только в пределах одной клетки вокруг — значит
        /// он ОБЯЗАН быть не меньше суммы радиусов двух самых крупных особей, иначе
        /// они перестанут расталкиваться и начнут слипаться в одну точку.
        /// </summary>
        [Tooltip("Радиус, в котором особь видит соседей, юниты. Не меньше поперечника " +
                 "самой крупной особи — см. комментарий.")]
        [SerializeField, Range(0.5f, 10f)] private float neighbourRadius = 4f;

        [Tooltip("Сколько соседей разбирать максимум. Потолок стоимости в давке.")]
        [SerializeField, Range(4, 64)] private int maxNeighbours = 32;

        [SerializeField, Range(1f, 30f)] private float acceleration = 9f;

        [Tooltip("Ближе этого особь идёт прямо на игрока, минуя поле потока.")]
        [SerializeField, Range(1f, 12f)] private float closeRange = 4f;

        [Header("Отрисовка")]
        [Tooltip("Дальше этого особь не рисуется. В пещере дальше и не видно — туман.")]
        [SerializeField, Range(10f, 200f)] private float drawDistance = 60f;

        /// <summary>
        /// Сколько особей уходит в один вызов отрисовки.
        ///
        /// Не 1023, как позволяет Unity, и это не перестраховка. Свойства инстанса едут
        /// в uniform-буфере, а WebGL2 гарантирует всего 256 векторов на вершинный этап.
        /// Одна особь занимает шесть: четыре на матрицу, один на состояние анимации,
        /// один на оттенок. Сто двадцать восемь особей это 768 векторов — с запасом
        /// влезает в типичные для WebGL2 1024 и режется по партиям предсказуемо,
        /// а не силами драйвера.
        /// </summary>
        [Tooltip("Особей в одном вызове отрисовки. См. комментарий в коде про лимит WebGL2.")]
        [SerializeField, Range(16, 511)] private int instancesPerBatch = 128;

        [Tooltip("Отбрасывает ли толпа тени. Выключено: при Forward+ попиксельны все " +
                 "источники, и сотни теней в атласе стоят дороже, чем дают.")]
        [SerializeField] private bool castShadows;

        [Header("Отладка")]
        [SerializeField] private bool drawFlowGizmos;
        [SerializeField, Range(4f, 60f)] private float gizmoRadius = 20f;

        private readonly SpiderFlowField _flow = new SpiderFlowField();

        private NativeArray<SpiderState> _states;
        private NativeArray<float4> _neighbours;
        private NativeArray<float3> _velocities;
        private NativeArray<float> _heights;
        private NativeArray<float> _climbs;
        private NativeArray<int> _tiers;
        private NativeArray<SpiderTuning> _tuning;
        private NativeArray<float4> _frustum;
        private NativeArray<int> _killed;
        private NativeParallelMultiHashMap<int, int> _hash;

        private KindRuntime[] _runtime;

        /// <summary>Капсула цели, если она есть: по ней толпа целится в тело, а не в глаза.</summary>
        private CharacterController _targetBody;

        private Random _random;
        private float _flowTimer;
        private int _flowCell = -1;
        private float _spawnCredit;

        /// <summary>Время для волны населения и пауза досыла после крупного убийства.</summary>
        private float _waveTime;
        private float _spawnHold;

        /// <summary>
        /// Директива забега. Пока её нет, толпа живёт по своим полям — так её меряет
        /// проверка толпы, отдельно от сложности. Раньше на этом месте был всплеск
        /// от генератора; генераторы стали пилонами Матки, и всплеск ушёл вместе с ними:
        /// население теперь целиком ведёт директор.
        /// </summary>
        private bool _directed;
        private CrowdDirective _directive;

        /// <summary>Сквозной номер появления — см. <see cref="SpiderState.Serial"/>.</summary>
        private int _serial;

        /// <summary>Самый лёгкий и самый тяжёлый вид по живучести: запасной для отбора и вид элиты.</summary>
        private int _lightest;
        private int _heaviest;

        /// <summary>Буфер заливки под выводок и пилоны, чтобы не выделять его на каждый вызов.</summary>
        private readonly Dictionary<int, int> _reach = new Dictionary<int, int>();
        private readonly List<int> _cellBuffer = new List<int>();

        /// <summary>
        /// Паника: сфера, из которой толпа разбегается. Ставится вспышкой света в районе.
        /// Хранится в локальных координатах мира — в них же живут и особи.
        /// </summary>
        private float3 _panicCentre;
        private float _panicRadius;
        private float _panicTimer;

        /// <summary>
        /// Освещённые районы, в которых не заводится пополнение (мировые координаты,
        /// w — радиус). Забежать за игроком орда туда всё ещё может: свет — не крепость.
        /// </summary>
        private readonly List<Vector4> _litZones = new List<Vector4>();

        /// <summary>
        /// Куда орда стягивается плотнее всего — ближайший незажжённый генератор.
        /// Пусто, если такого нет: тогда спавн равномерный, как раньше.
        /// </summary>
        private Vector3? _hotPoint;

        /// <summary>
        /// Сколько особей всё-таки завелось внутри освещённого района.
        ///
        /// Ответ на «работает ли запрет» прямой, а не через «пустеет ли район». Пустеет
        /// район по-разному: в проходном он опустеет за десяток секунд, в тупиковом
        /// особи будут выбираться минуту, и порог по остатку пришлось бы подгонять под
        /// геометрию каждого. А вот завестись внутри не должен НИ ОДИН, и это число
        /// от геометрии не зависит вовсе.
        /// </summary>
        public int SpawnedInLitZones { get; private set; }

        /// <summary>Обнуляет счётчик спавнов в освещённом — перед замером.</summary>
        public void ResetLitZoneCounter() => SpawnedInLitZones = 0;

        private readonly Plane[] _planes = new Plane[6];

        public int Alive { get; private set; }
        public int Drawn { get; private set; }
        public int Batches { get; private set; }
        public bool HasField => _flow.IsCreated;
        public SpiderFlowField Field => _flow;

        /// <summary>Сколько особей появилось за всё время. Прямой ответ на «идёт ли досыл».</summary>
        public int SpawnedTotal { get; private set; }

        /// <summary>Сколько раз крупное убийство ставило досыл на паузу, и сколько секунд он стоял.</summary>
        public int HoldsTriggered { get; private set; }

        public float HeldSeconds { get; private set; }

        /// <summary>Потолок особей: столько слотов выделено.</summary>
        public int Capacity => _states.IsCreated ? _states.Length : capacity;

        /// <summary>Собственные числа толпы — от них директор считает свои.</summary>
        public int DefaultPopulation => population;
        public int DefaultNearCap => nearCap;
        public float DefaultSpawnRate => spawnRate;

        public bool IsDirected => _directed;
        public CrowdDirective Directive => _directive;

        /// <summary>Готовые виды в порядке индексов, которыми их знают джобы.</summary>
        public int KindCount => _runtime != null ? _runtime.Length : 0;

        public SpiderKind KindAt(int index) =>
            _runtime != null && index >= 0 && index < _runtime.Length ? _runtime[index].Kind : null;

        /// <summary>Индекс вида элиты — самого живучего.</summary>
        public int HeaviestKind => _heaviest;

        private int EffectiveNearCap => _directed ? _directive.NearCap : nearCap;

        /// <summary>Готовые к отрисовке буферы одного вида.</summary>
        private sealed class KindRuntime
        {
            public SpiderKind Kind;
            public int Index;

            public NativeArray<float4x4> Matrices;
            public NativeArray<float4> Anim;
            public NativeArray<float4> Tint;
            public NativeArray<int> Count;

            public Matrix4x4[] MatrixBatch;
            public Vector4[] AnimBatch;
            public Vector4[] TintBatch;

            public Matrix4x4[] AllMatrices;
            public Vector4[] AllAnim;
            public Vector4[] AllTint;

            /// <summary>По блоку свойств на партию: один общий блок все партии читали бы одинаково.</summary>
            public readonly List<MaterialPropertyBlock> Blocks = new List<MaterialPropertyBlock>();

            public float CullRadius;

            public void Dispose()
            {
                if (Matrices.IsCreated) Matrices.Dispose();
                if (Anim.IsCreated) Anim.Dispose();
                if (Tint.IsCreated) Tint.Dispose();
                if (Count.IsCreated) Count.Dispose();
            }
        }

        private static readonly int AnimStateId = Shader.PropertyToID("_AnimState");
        private static readonly int InstanceTintId = Shader.PropertyToID("_InstanceTint");

        /// <summary>
        /// Достаёт ссылки на мир и цель, если их не проставили в инспекторе.
        ///
        /// Зовётся и из OnEnable, и из Rebuild намеренно: в режиме редактирования
        /// OnEnable у обычного MonoBehaviour не вызывается вовсе, а инструменты проверки
        /// работают именно там и зовут Rebuild напрямую.
        /// </summary>
        private void EnsureLinks()
        {
            if (world == null) world = GetComponentInParent<CatacombWorld>();
            if (world == null) world = FindFirstObjectByType<CatacombWorld>();

            if (target != null) return;

            var rig = FindFirstObjectByType<CatacombTestRig>();
            if (rig != null) target = rig.transform;
        }

        private void OnEnable()
        {
            EnsureLinks();

            if (world != null) world.Generated += Rebuild;

#if UNITY_EDITOR
            // Нативная память переживает перезагрузку домена, а ссылки на неё — нет.
            // Тот же приём, что в CatacombWorld: без него Unity рапортует об утечке.
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload -= Release;
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += Release;
#endif

            // Уровень мог быть сгенерирован до того, как компонент включили.
            if (world != null && world.Layout != null && !world.IsGenerating) Rebuild();
        }

        private void OnDisable()
        {
            if (world != null) world.Generated -= Rebuild;

#if UNITY_EDITOR
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload -= Release;
#endif

            Release();
        }

        private void Update()
        {
            if (!Application.isPlaying) return;

            Simulate(Time.deltaTime);
            Submit(ResolveCamera());
        }

        /// <summary>
        /// Перестраивает сетку навигации под текущий уровень и заселяет его заново.
        /// Зовётся событием генерации мира и вручную из инструментов проверки.
        /// </summary>
        public void Rebuild()
        {
            Release();
            EnsureLinks();

            if (world == null || world.Layout == null || world.Chunks.Count == 0) return;

            var ready = CollectKinds();
            if (ready == 0) return;

            _flow.Build(world, cellSize, attachRange);

            if (_flow.WalkableCount == 0)
            {
                Debug.LogWarning($"{nameof(SpiderCrowd)}: в уровне не нашлось ни одной проходимой клетки. " +
                                 "Толпе негде жить — проверьте размер клетки и запас над полом.", this);
                return;
            }

            AllocateState();

            _random = new Random((uint)math.max(1, math.abs(world.CurrentSeed ^ 0x5D1DE5)));

            // Первая заливка — от точки входа, а не от игрока: игрока к этому моменту
            // к ней ещё не телепортировали, и толпа иначе разошлась бы от старого места.
            var source = target != null ? LocalOf(target.position) : SpawnPointLocal();

            _flow.Rebuild(source, spawnSteps.x, spawnSteps.y, floodSteps);
            _flowCell = _flow.SourceCell;
            _flowTimer = 0f;

            // Под директором заселять мгновенно нельзя. Директива в этот момент может быть
            // ещё от прошлого уровня — финальный рой на тысячу с лишним особей, — а свежую
            // директор выставит в том же событии генерации, но в неизвестном порядке
            // относительно этого вызова. Досыл за первые секунды доведёт толпу до числа
            // уже новой директивы, и это заодно правильный старт: забег Megabonk
            // начинается с редких врагов.
            if (!_directed) FillPopulation(population);
        }

        /// <summary>
        /// Отрезок, в который целится толпа: ось капсулы цели по высоте.
        ///
        /// Точка трансформа для этого не годится. У отладочного стенда она стоит
        /// на уровне ГЛАЗ — центр капсулы смещён вниз, — и паук, подошедший вплотную
        /// по полу, оставался в полутора юнитах ПО ПРЯМОЙ просто потому, что цель
        /// была выше него. Дистанция удара в таких условиях означает гипотенузу,
        /// а не расстояние между телами.
        ///
        /// Берётся из капсулы, если она на цели есть: <c>height * 0.5 - radius</c> —
        /// это отрезок между центрами полусфер, то есть настоящая ось капсулы.
        /// Радиус в отрезок не входит намеренно: он учтён в дистанции удара,
        /// которую считает запекатель.
        ///
        /// Смещение центра БЕРЁТСЯ С ПОВОРОТОМ, и это не перестраховка, а замер:
        /// Unity держит саму капсулу вертикальной при любом повороте, но поле
        /// <c>center</c> поворачивает вместе с трансформом. У стенда трансформ несёт
        /// питч до 89 градусов, и при 60 измеренное смещение было (-0.09, -0.10, -0.15)
        /// вместо записанных (0, -0.2, 0). Читать вместо этого <c>bounds</c> нельзя:
        /// в режиме полёта стенд выключает контроллер, и габариты у него пропадают.
        /// </summary>
        private void ResolveTargetBody(float3 targetLocal, out float low, out float high)
        {
            low = targetLocal.y;
            high = targetLocal.y;

            if (target == null) return;

            if (_targetBody == null || _targetBody.transform != target)
            {
                _targetBody = target.GetComponent<CharacterController>();
            }

            if (_targetBody == null) return;

            var centre = LocalOf(target.position + target.rotation * _targetBody.center).y;
            var half = math.max(0f, _targetBody.height * 0.5f - _targetBody.radius);

            low = centre - half;
            high = centre + half;
        }

        /// <summary>Шаг поведения. Отдельно от Update, чтобы инструменты проверки могли шагать сами.</summary>
        public void Simulate(float deltaTime)
        {
            if (!_states.IsCreated || _runtime == null || deltaTime <= 0f) return;

            var targetLocal = float3.zero;
            var targetValid = 0;

            if (target != null)
            {
                targetLocal = LocalOf(target.position);
                targetValid = 1;
            }

            ResolveTargetBody(targetLocal, out var targetLow, out var targetHigh);

            UpdateFlow(targetLocal, targetValid != 0, deltaTime);

            _waveTime += deltaTime;
            if (_spawnHold > 0f) HeldSeconds += math.min(_spawnHold, deltaTime);
            _spawnHold = math.max(0f, _spawnHold - deltaTime);
            _panicTimer = math.max(0f, _panicTimer - deltaTime);

            TopUp(deltaTime);
            UpdateEbb(targetLocal, targetValid != 0, deltaTime);

            var near = targetValid != 0 ? CountNear(targetLocal) : 0;

            _hash.Clear();

            var handle = new SpiderHashJob
            {
                Neighbours = _neighbours,
                Hash = _hash.AsParallelWriter(),
                CellSize = math.max(0.1f, neighbourRadius)
            }.Schedule(_states.Length, 64);

            handle = new SpiderMoveJob
            {
                States = _states,
                Neighbours = _neighbours,
                Velocities = _velocities,
                Heights = _heights,
                Climbs = _climbs,
                Tiers = _tiers,
                Hash = _hash,
                Tuning = _tuning,
                Field = _flow.Sampler,

                Target = targetLocal,
                TargetLow = targetLow,
                TargetHigh = targetHigh,
                TargetValid = targetValid,
                DeltaTime = deltaTime,

                HashCellSize = math.max(0.1f, neighbourRadius),
                NeighbourRadius = neighbourRadius,

                SeparationWeight = separation,
                AlignmentWeight = alignment,
                CohesionWeight = cohesion,

                Acceleration = acceleration,
                CloseRange = closeRange,
                MaxNeighbours = maxNeighbours,
                IdleStride = 0.35f,

                NearCount = near,
                NearCap = EffectiveNearCap,
                NearRadius = nearRadius,
                CorpseDrag = corpseDrag,

                PanicCentre = _panicCentre,
                PanicRadiusSq = _panicRadius * _panicRadius,
                PanicActive = _panicTimer > 0f ? 1 : 0,
                PanicLeft = _panicTimer,

                MaxClimb = maxClimb,
                PileTiers = math.max(2, pileTiers),
                ClimbRate = climbRate,
                Gravity = gravity,
                DetourWeight = detourWeight,
                WallRange = wallRange,
                EyeRadius = eyeRadius,
                ShoveSpeed = shoveSpeed,
                ClimberShare = climberShare,
                ClimberPull = climberPull,
                LurkPatience = lurkPatience,
                PileRadius = nearRadius
            }.Schedule(_states.Length, 32, handle);

            handle = new SpiderPublishJob
            {
                States = _states,
                Tuning = _tuning,
                Neighbours = _neighbours,
                Velocities = _velocities,
                Heights = _heights,
                Climbs = _climbs,
                Tiers = _tiers
            }.Schedule(_states.Length, 64, handle);

            handle.Complete();

            CountAlive();
        }

        /// <summary>Собирает матрицы и отправляет толпу в кадр.</summary>
        public void Submit(Camera camera)
        {
            Drawn = 0;
            Batches = 0;

            if (!_states.IsCreated || _runtime == null || camera == null) return;

            GeometryUtility.CalculateFrustumPlanes(camera, _planes);

            for (var i = 0; i < 6; i++)
            {
                _frustum[i] = new float4(_planes[i].normal.x, _planes[i].normal.y, _planes[i].normal.z,
                    _planes[i].distance);
            }

            var localToWorld = (float4x4)world.transform.localToWorldMatrix;
            var cameraWorld = (float3)camera.transform.position;

            foreach (var runtime in _runtime)
            {
                new SpiderRenderJob
                {
                    States = _states,
                    Tuning = _tuning,
                    FrustumPlanes = _frustum,

                    Matrices = runtime.Matrices,
                    AnimState = runtime.Anim,
                    Tints = runtime.Tint,
                    Count = runtime.Count,

                    Kind = runtime.Index,
                    LocalToWorld = localToWorld,
                    CullRadius = runtime.CullRadius,
                    CameraWorld = cameraWorld,
                    MaxDistanceSq = drawDistance * drawDistance
                }.Schedule().Complete();

                Draw(runtime, cameraWorld);
            }
        }

        /// <summary>
        /// Бьёт всех в сфере. Зовётся взрывом гранаты — тем же вызовом, что и
        /// <see cref="CaveWebs.TearAt"/>, и с тем же смыслом: оружие здесь площадное.
        /// </summary>
        /// <returns>Сколько особей убито.</returns>
        public int DamageAt(Vector3 worldPoint, float radius, int damage = 1)
        {
            if (!_states.IsCreated || radius <= 0f) return 0;

            for (var i = 0; i < _killed.Length; i++) _killed[i] = 0;

            var centre = LocalOf(worldPoint);
            var around = CountNear(centre, nearRadius);

            new SpiderDamageJob
            {
                States = _states,
                Center = centre,
                RadiusSq = radius * radius,
                Damage = math.max(1, damage),
                Impulse = deathImpulse,
                Seed = (uint)math.max(1, Environment.TickCount),
                Killed = _killed
            }.Schedule(_states.Length, 64).Complete();

            var killed = 0;
            for (var i = 0; i < _killed.Length; i++) killed += _killed[i];

            // Пролом в толпе должен постоять — но только если это ПРОЛОМ, то есть выбита
            // заметная доля тех, кто был вокруг, а не два десятка из кучи в несколько сотен.
            //
            // Порог числом держался, пока толпа ждала в очереди и у игрока стояло семь
            // десятков. Когда очередь сняли, почти любой взрыв по куче убивал больше двух
            // десятков, и досыл стоял на паузе половину стадии — 240 секунд из 480,
            // 148 раз подряд. Сильный игрок выключал себе волны, то есть ровно обратное
            // реактивному спавну Megabonk: убиваешь быстрее — досылают быстрее.
            if (killed >= holdKillCount && killed >= around * holdShare)
            {
                _spawnHold = spawnHoldAfterKill;
                HoldsTriggered++;
            }

            return killed;
        }

        /// <summary>
        /// Разгоняет толпу прочь от точки на несколько секунд.
        ///
        /// Зовётся вспышкой света в районе. Это разрядка сразу после самой тяжёлой волны
        /// забега: игрок полторы минуты держался, свет загорелся — и сотни тварей бегут
        /// от него. Разбежавшиеся не исчезают, а уходят в соседнюю темноту, то есть
        /// уплотняют её: чем больше карты засвечено, тем гуще в остатке.
        ///
        /// Сфера, а не список районов: паника живёт секунды, и пересечение со сферой
        /// в джобе стоит одно скалярное произведение на особь.
        /// </summary>
        public void Panic(Vector3 worldPoint, float radius, float duration)
        {
            if (radius <= 0f || duration <= 0f) return;

            _panicCentre = LocalOf(worldPoint);
            _panicRadius = radius;
            _panicTimer = math.max(_panicTimer, duration);
        }

        /// <summary>Снимает панику и паузу досыла — при перегенерации уровня и рестарте забега.</summary>
        public void ClearEvents()
        {
            _panicTimer = 0f;
            _spawnHold = 0f;
        }

        /// <summary>
        /// Директор забега говорит, сколько держать и кого заводить. Зовётся каждый кадр:
        /// директива дешёвая, а сложность меняется непрерывно.
        /// </summary>
        public void SetDirective(in CrowdDirective directive)
        {
            _directive = directive;
            _directed = true;
        }

        /// <summary>Возвращает толпу к её собственным числам.</summary>
        public void ClearDirective() => _directed = false;

        /// <summary>
        /// Заводит элиту — самого живучего паука, увеличенного и перекрашенного.
        ///
        /// Элита рождается в той же полосе, что и рядовые: мини-босс Megabonk приходит
        /// из толпы, а не падает с неба рядом с игроком. Засады у неё нет — неподвижный
        /// мини-босс на своде просто не встретился бы игроку.
        /// </summary>
        /// <returns>Ссылка на особь или <see cref="SpiderHandle.None"/>, если места не нашлось.</returns>
        public SpiderHandle SpawnElite(float sizeScale, float healthScale, float speedScale, Vector3 hue)
        {
            if (_runtime == null || !_flow.SpawnCells.IsCreated || _flow.SpawnCells.Length == 0)
            {
                return SpiderHandle.None;
            }

            var slot = FindFreeSlot();
            if (slot < 0) return SpiderHandle.None;

            var cell = PickSpawnCell();
            if (cell < 0) return SpiderHandle.None;

            var kind = _runtime[_heaviest].Kind;
            var health = math.max(1, (int)math.round(kind.Health * healthScale));

            Place(slot, cell, _heaviest, sizeScale, health, speedScale, hue, false, 0f, true);
            Alive++;

            return new SpiderHandle(slot, _states[slot].Serial);
        }

        /// <summary>
        /// Выводок: особи появляются в проходимых клетках вокруг точки, связанных с ней
        /// ходами. Зовётся Маткой — её подкрепления в Megabonk приходят по таймеру.
        ///
        /// Клетки берутся заливкой, а не кубом вокруг: куб захватывает соседний ход
        /// за стенкой, и выводок Матки вылезал бы из-за стены в чужом коридоре.
        /// </summary>
        /// <returns>Сколько особей появилось.</returns>
        public int SpawnAround(Vector3 worldPoint, int count, int radiusSteps, int maxKindHealth)
        {
            if (_runtime == null || !_flow.IsCreated || count <= 0) return 0;

            if (_flow.Reach(LocalOf(worldPoint), math.max(1, radiusSteps), _reach) < 0) return 0;

            _cellBuffer.Clear();
            foreach (var pair in _reach) _cellBuffer.Add(pair.Key);

            if (_cellBuffer.Count == 0) return 0;

            var healthScale = _directed ? _directive.HealthScale : 1f;
            var speedScale = _directed ? _directive.SpeedScale : 1f;
            var hue = _directed ? (float3)_directive.Hue : new float3(1f);

            var spawned = 0;

            for (var i = 0; i < count; i++)
            {
                var slot = FindFreeSlot();
                if (slot < 0) break;

                var cell = _cellBuffer[_random.NextInt(_cellBuffer.Count)];
                var kindIndex = PickKind(maxKindHealth, false);

                Place(slot, cell, kindIndex, 1f, RollHealth(_runtime[kindIndex].Kind.Health * healthScale),
                    speedScale, hue, false, _random.NextFloat(), false);

                spawned++;
                Alive++;
            }

            return spawned;
        }

        /// <summary>Жива ли особь по ссылке, и где она. Здоровье — сколько попаданий ей осталось.</summary>
        public bool TryGetSpider(SpiderHandle handle, out Vector3 worldPosition, out int health)
        {
            worldPosition = Vector3.zero;
            health = 0;

            if (!_states.IsCreated || handle.Slot < 0 || handle.Slot >= _states.Length) return false;

            var spider = _states[handle.Slot];

            if (spider.Active == 0 || spider.Serial != handle.Serial || spider.Clip == (int)SpiderClip.Dead)
            {
                return false;
            }

            worldPosition = WorldOf(spider.Position);
            health = spider.Health;
            return true;
        }

        /// <summary>Что делает толпа вокруг игрока — разбивка для прогона.</summary>
        public struct Engagement
        {
            public int Near;
            public int Attacking;
            public int Climbing;
            public int Moving;

            /// <summary>Стоят: не двигаются вовсе и не бьют. Это и есть «айдлят вдалеке».</summary>
            public int Stalled;

            /// <summary>
            /// Толкутся: движутся, но вперёд не продвигаются — очередь за кучей в заторе.
            /// Отдельно от стоящих: в очереди лапы скребут и тела ходят, в кадре это давка,
            /// а не безделье.
            /// </summary>
            public int Jostling;

            public int Lurking;
            public float MaxClimb;

            /// <summary>На чём держатся: пол, стены, свод.</summary>
            public int OnFloor;
            public int OnWall;
            public int OnCeiling;

            /// <summary>Поднимаются прямо сейчас — и из них «лифтом», почти без хода вперёд.</summary>
            public int Rising;
            public int Elevator;

            /// <summary>
            /// Ставят передние лапы на спину соседа, в которого упёрлись: поднимаются стоя, задрав нос.
            /// Это только шаг на край горба; дальше подъём идёт на ходу.
            /// </summary>
            public int Rearing;

            /// <summary>На верхних поверхностях: на своде или на стене выше глаз игрока.</summary>
            public int Upper;

            /// <summary>
            /// Влезли в личное пространство игрока: ближе своей дистанции удара к его оси
            /// или к самой камере. Отсюда «куча-мала» вплотную и лапы сквозь экран.
            /// </summary>
            public int Intruding;

            /// <summary>Самая высокая куча в трёх юнитах от игрока — то, что закрывает обзор.</summary>
            public float CloseClimb;
        }

        /// <summary>
        /// Разбивка живых особей в радиусе от игрока: бьют, лезут поверх, бегут, стоят, в засаде.
        ///
        /// Заведена по жалобе «только малая часть толпы атакует, остальные стоят вдалеке
        /// и ничего не делают». Прогон этого не видел: связность, скорость и расстояние
        /// до игрока у стоящей кольцом толпы ровно те же, что у давящей.
        /// </summary>
        public Engagement MeasureEngagement(float radius)
        {
            var result = new Engagement();

            if (!_states.IsCreated || target == null) return result;

            var targetLocal = LocalOf(target.position);
            var radiusSq = radius * radius;

            ResolveTargetBody(targetLocal, out var bodyLow, out var bodyHigh);

            for (var i = 0; i < _states.Length; i++)
            {
                var spider = _states[i];

                if (spider.Active == 0 || spider.Clip == (int)SpiderClip.Dead || spider.Ebb != 0) continue;
                if (math.lengthsq(spider.Position - targetLocal) > radiusSq) continue;

                result.Near++;
                result.MaxClimb = math.max(result.MaxClimb, spider.Climb);

                if (spider.Up.y > 0.5f) result.OnFloor++;
                else if (spider.Up.y < -0.5f) result.OnCeiling++;
                else result.OnWall++;

                if (spider.ClimbVel > 0.3f)
                {
                    result.Rising++;

                    // Лифт — это подъём стоя И с ровным телом. Подъём стоя с задранным носом —
                    // шаг передними лапами на спину соседа, так и задумано. «Ровное» — меньше
                    // 0.2 рад: нос задирается за пару кадров, и только что начавший подниматься
                    // при пороге 0.35 попадал в лифт — проверка через раз падала на своих же.
                    if (math.length(spider.Velocity) < 0.5f)
                    {
                        if (spider.Pitch < 0.2f) result.Elevator++;
                        else result.Rearing++;
                    }
                }

                if (spider.Up.y < -0.5f || (math.abs(spider.Up.y) <= 0.5f && spider.Position.y > targetLocal.y))
                {
                    result.Upper++;
                }

                // Личное пространство: та же мера, что держит его в джобе.
                var axis = new float3(targetLocal.x, math.clamp(spider.Position.y, bodyLow, bodyHigh), targetLocal.z);
                var keep = _tuning[spider.Kind].AttackRange * spider.Scale * 0.85f;

                if (math.distance(spider.Position, axis) < keep || math.distance(spider.Position, targetLocal) < eyeRadius)
                {
                    result.Intruding++;
                }

                if (math.distance(spider.Position.xz, targetLocal.xz) < 3f)
                {
                    result.CloseClimb = math.max(result.CloseClimb, spider.Climb);
                }

                if (spider.Climb > SpiderTuning.RideLevel) result.Climbing++;

                // «Бежит» — это ПРОДВИГАЕТСЯ к игроку, а не просто движется: сдержанная
                // особь ползала вбок на четверти скорости и честно считалась бы бегущей.
                //
                // Продвижение — по полю потока, а не по прямой. Путь к игроку по катакомбам
                // петляет, и особь, бегущая по нему во весь дух, по прямой может от игрока
                // даже удаляться: первый замер записал в стоящие 220 таких бегущих
                // со скоростью три-четыре юнита в секунду.
                var sampler = _flow.Sampler;
                sampler.SampleAt(spider.Position - spider.Up * spider.Climb, out var flow, out _, out _,
                    out var onField);

                var toward = onField && math.lengthsq(flow) > 0.01f
                    ? flow
                    : math.normalizesafe(targetLocal - spider.Position);

                var closing = math.dot(spider.Velocity, toward);

                if (spider.Clip == (int)SpiderClip.Attack) result.Attacking++;
                else if (spider.Clip == (int)SpiderClip.Idle) result.Lurking++;
                else if (closing >= 0.4f || spider.Climb >= SpiderTuning.RideLevel) result.Moving++;
                else if (math.length(spider.Velocity) < 0.4f) result.Stalled++;
                else result.Jostling++;
            }

            return result;
        }

        /// <summary>
        /// Копия состояний живых особей — для разбора поимённо в инструментах проверки.
        /// Позиции в локальных координатах генерации. Копией, а не выдачей массива:
        /// см. <see cref="CopyPositions"/>.
        /// </summary>
        public int CopyStates(List<SpiderState> into)
        {
            into.Clear();

            if (!_states.IsCreated) return 0;

            for (var i = 0; i < _states.Length; i++)
            {
                if (_states[i].Active != 0) into.Add(_states[i]);
            }

            return into.Count;
        }

        /// <summary>Игрок в тех же локальных координатах, что и <see cref="CopyStates"/>.</summary>
        public Vector3 TargetLocal => target != null ? (Vector3)LocalOf(target.position) : Vector3.zero;

        /// <summary>
        /// Сколько особей рядом с игроком сидит ТЕЛОМ в другой — на одном ярусе и ближе
        /// трёх четвертей суммы радиусов тел. Лапы в расчёт не идут: им переплетаться можно.
        ///
        /// Заведено по жалобе «пауки залезают друг в друга, а не друг на друга». Все прежние
        /// замеры этого не видели: связность, посадка, раскладка по поверхностям и даже
        /// «лезут поверх» у проникающей друг в друга толпы ровно те же.
        /// </summary>
        public int CountBodyOverlaps(float radius, out int near, out int deep)
        {
            const float LyingShare = 0.95f;

            near = 0;
            deep = 0;

            if (!_states.IsCreated || !_tuning.IsCreated || target == null) return 0;

            var targetLocal = LocalOf(target.position);
            var picked = new List<int>();

            for (var i = 0; i < _states.Length; i++)
            {
                var spider = _states[i];

                if (spider.Active == 0 || spider.Clip == (int)SpiderClip.Dead || spider.Ebb != 0) continue;
                if (math.distance(spider.Position, targetLocal) > radius) continue;

                picked.Add(i);
            }

            near = picked.Count;
            var overlapping = 0;

            foreach (var i in picked)
            {
                var a = _states[i];
                var ta = _tuning[a.Kind];

                var coreA = ta.BodyRadius * a.Scale * SpiderTuning.CoreShare;
                var heightA = ta.Height * a.Scale * SpiderTuning.BackShare;

                foreach (var k in picked)
                {
                    if (k == i) continue;

                    var b = _states[k];
                    var tb = _tuning[b.Kind];

                    var delta = a.Position - b.Position;
                    var vertical = math.dot(delta, a.Up);

                    // Не один ярус: один лежит на другом. С допуском в двадцатую долю спины:
                    // наездник на горбе лежит ровно на высоте спины нижнего, и нормали двух особей,
                    // расходящиеся на градус-другой, опускали его на волос ниже границы — замер
                    // записывал лежащего на спине в сидящих внутри. Таких было больше половины
                    // «глубоких» пар.
                    if (vertical >= tb.Height * b.Scale * SpiderTuning.BackShare * LyingShare ||
                        vertical <= -heightA * LyingShare) continue;

                    var planar = math.length(delta - a.Up * vertical);
                    var coreSum = coreA + tb.BodyRadius * b.Scale * SpiderTuning.CoreShare;

                    if (planar >= coreSum * 0.75f) continue;

                    overlapping++;

                    // Глубоко — центры ближе половины суммы ядер: тело почти целиком
                    // в другом. Это и видно в кадре как «друг в друге»; лёгкое касание
                    // ядер в давке неизбежно и глазом не читается.
                    if (planar < coreSum * 0.5f) deep++;

                    break;
                }
            }

            return overlapping;
        }

        /// <summary>
        /// Наездники рядом с игроком: сколько их, сколько висит в воздухе — выше горба спины
        /// под собой больше чем на 0.15 юнита (чуть больше мёртвой зоны спуска), — и сколько лежит
        /// на спинах бьющих. Порог был треть юнита, пока спины считались по раздутым габаритам;
        /// при настоящих, 0.37-0.68, треть юнита — это почти целая спина.
        ///
        /// Заведено по жалобе «должны залазить лапками друг на друга, а не висеть в воздухе».
        /// «Лезут поверх» в разбивке натиска этого не видел: наездник, поднятый на полную спину
        /// соседа и стоящий над кончиками его лап, считался там ровно так же, как лежащий на теле.
        /// Горб — тот же, что в джобе (<see cref="SpiderTuning.Hump"/>), и опора — те же стоящие
        /// на камне вровень.
        /// </summary>
        /// <param name="stacked">Лежат на наезднике — третий ярус и выше.</param>
        public int MeasureRiders(float radius, out int floating, out int onAttackers, out int stacked)
        {
            floating = 0;
            onAttackers = 0;
            stacked = 0;

            if (!_states.IsCreated || !_tuning.IsCreated || target == null) return 0;

            var targetLocal = LocalOf(target.position);
            var picked = new List<int>();

            for (var i = 0; i < _states.Length; i++)
            {
                var spider = _states[i];

                if (spider.Active == 0 || spider.Clip == (int)SpiderClip.Dead || spider.Ebb != 0) continue;

                // Опоры берутся и чуть дальше радиуса: наездник на краю круга стоит на том, кто за краем.
                if (math.distance(spider.Position, targetLocal) > radius + 4f) continue;

                picked.Add(i);
            }

            var riders = 0;

            foreach (var i in picked)
            {
                var rider = _states[i];

                if (rider.Climb <= SpiderTuning.RideLevel || math.distance(rider.Position, targetLocal) > radius) continue;

                riders++;

                var ground = rider.Position - rider.Up * rider.Climb;
                var riderRadius = _tuning[rider.Kind].BodyRadius * rider.Scale;

                var best = 0f;
                var bestAttacking = false;
                var bestRaised = false;

                foreach (var k in picked)
                {
                    var under = _states[k];

                    if (k == i) continue;

                    var delta = under.Position - ground;
                    var above = math.dot(delta, rider.Up);

                    // Опора — на нашей поверхности (её камень вровень с нашим) и не выше нас лапами:
                    // то же, что держит в джобе, вместе с теми, на кого наездник ещё только всходит.
                    if (math.abs(above - under.Climb) >= SpiderTuning.LevelTolerance || above > rider.Climb) continue;

                    var planar = math.length(delta - rider.Up * above);
                    var tuning = _tuning[under.Kind];

                    // Мельче наездника — не опора: см. SpiderTuning.SturdyShare.
                    if (tuning.BodyRadius * under.Scale < riderRadius * SpiderTuning.SturdyShare) continue;

                    var hump = above + SpiderTuning.Hump(planar, tuning.BodyRadius * under.Scale,
                        tuning.Height * under.Scale, riderRadius, out _);

                    if (hump <= best) continue;

                    best = hump;
                    bestAttacking = under.Clip == (int)SpiderClip.Attack;
                    bestRaised = under.Climb > SpiderTuning.RideLevel;
                }

                if (rider.Climb > best + 0.15f)
                {
                    floating++;
                    continue;
                }

                if (bestAttacking) onAttackers++;
                if (bestRaised) stacked++;
            }

            return riders;
        }

        /// <summary>Сколько живых особей данного вида. Этим прогон проверяет, что рой — именно рой.</summary>
        public int CountAlive(int kindIndex)
        {
            if (!_states.IsCreated) return 0;

            var count = 0;

            for (var i = 0; i < _states.Length; i++)
            {
                var spider = _states[i];

                if (spider.Active == 0 || spider.Clip == (int)SpiderClip.Dead) continue;
                if (spider.Kind == kindIndex) count++;
            }

            return count;
        }

        /// <summary>
        /// Точки на ПОЛУ, связанные ходами с заданной и удалённые от неё на
        /// [minSteps, maxSteps] шагов, разнесённые друг от друга как можно дальше.
        /// Под пилоны Матки.
        ///
        /// Разнос жадный, «самая дальняя от уже выбранных»: случайный выбор ставил бы
        /// два пилона бок о бок, и третий приходился бы на другой конец зала —
        /// бег между ними превращался бы в стояние на месте плюс одну пробежку.
        /// </summary>
        /// <param name="avoid">Точки, от которых тоже держаться подальше — прежние пилоны, сама Матка.</param>
        public int PickFloorPoints(Vector3 aroundWorld, int count, int minSteps, int maxSteps,
            IReadOnlyList<Vector3> avoid, List<Vector3> into)
        {
            into.Clear();

            if (!_flow.IsCreated || count <= 0) return 0;
            if (_flow.Reach(LocalOf(aroundWorld), maxSteps, _reach) < 0) return 0;

            var candidates = new List<Vector3>();

            foreach (var pair in _reach)
            {
                if (pair.Value < minSteps) continue;

                // Только пол: на стене пилон висел бы боком, а на своде до него
                // не дотянуться — зарядка идёт стоянием рядом.
                if (_flow.Normal[pair.Key].y < 0.75f) continue;

                candidates.Add(WorldOf(_flow.CellSurfacePoint(pair.Key, 0f)));
            }

            if (candidates.Count == 0) return 0;

            var occupied = new List<Vector3> { aroundWorld };
            if (avoid != null) occupied.AddRange(avoid);

            for (var k = 0; k < count; k++)
            {
                var best = -1;
                var bestScore = float.MinValue;

                for (var i = 0; i < candidates.Count; i++)
                {
                    var nearest = float.MaxValue;

                    foreach (var point in occupied)
                    {
                        nearest = math.min(nearest, (candidates[i] - point).sqrMagnitude);
                    }

                    if (nearest <= bestScore) continue;

                    best = i;
                    bestScore = nearest;
                }

                if (best < 0) break;

                into.Add(candidates[best]);
                occupied.Add(candidates[best]);
                candidates.RemoveAt(best);

                if (candidates.Count == 0) break;
            }

            return into.Count;
        }

        /// <summary>
        /// Освещённые районы, в которых не заводится пополнение. Мировые координаты,
        /// w — радиус. Список копируется: у звонящего он живой и меняется.
        /// </summary>
        public void SetLitZones(IReadOnlyList<Vector4> zones)
        {
            _litZones.Clear();

            if (zones == null) return;

            for (var i = 0; i < zones.Count; i++) _litZones.Add(zones[i]);
        }

        /// <summary>
        /// Точка, вокруг которой толпа гуще всего — ближайший незажжённый генератор.
        /// Это второй указатель направления, помимо тления жил: стало плотнее — теплее.
        /// </summary>
        public void SetHotPoint(Vector3? worldPoint) => _hotPoint = worldPoint;

        /// <summary>Мгновенно доводит население до заданного — для замеров и стенда.</summary>
        public void FillPopulation(int count)
        {
            if (!_states.IsCreated || !_flow.SpawnCells.IsCreated) return;

            for (var i = 0; i < count; i++)
            {
                if (!TrySpawn()) break;
            }

            CountAlive();

            // Соседи публикуются в конце кадра, а спавн идёт до первого шага поведения:
            // без явного снимка первый кадр толпа не видела бы друг друга и слиплась.
            new SpiderPublishJob
            {
                States = _states,
                Tuning = _tuning,
                Neighbours = _neighbours,
                Velocities = _velocities,
                Heights = _heights,
                Climbs = _climbs,
                Tiers = _tiers
            }.Schedule(_states.Length, 64).Complete();
        }

        private void UpdateFlow(float3 targetLocal, bool valid, float deltaTime)
        {
            if (!valid) return;

            _flowTimer += deltaTime;
            if (_flowTimer < flowInterval) return;

            var cell = _flow.FindNearestWalkable(targetLocal, 3);

            // Пересчёт только при смене клетки: заливка по всей сетке стоит миллисекунды,
            // и гонять её на каждом шаге игрока внутри одной клетки не за чем.
            if (cell < 0 || cell == _flowCell) return;

            _flowTimer = 0f;
            _flowCell = cell;

            _flow.Rebuild(targetLocal, spawnSteps.x, spawnSteps.y, floodSteps);
        }

        /// <summary>
        /// Сколько особей держать сейчас: затишье, нарастание, гребень.
        ///
        /// Трапеция, а не синус: у синуса нет ровного затишья и ровного гребня, он всё
        /// время где-то посередине, и ритм из него не читается. Здесь же есть отчётливое
        /// «сейчас тихо» и отчётливое «сейчас накат».
        /// </summary>
        private int WavePopulation()
        {
            // Под директором базу задаёт он — по кривой сложности забега, — а трапеция
            // остаётся коротким ритмом поверх неё: волна директора длится минуты,
            // и без ряби внутри неё кадр опять становится стеной.
            var basePopulation = _directed ? _directive.Population : population;
            var scale = _directed ? _directive.MicroWave : waveScale;

            if (scale <= 1.001f || wavePeriod <= 0f) return basePopulation;

            var t = math.frac(_waveTime / wavePeriod);
            var half = waveCrest * 0.5f;

            // Гребень посередине цикла, по четверти периода с каждой стороны на разгон
            // и спад. Края цикла — затишье.
            var rise = math.saturate((t - (0.5f - half - 0.25f)) / 0.25f);
            var fall = 1f - math.saturate((t - (0.5f + half)) / 0.25f);

            var shape = math.min(rise, fall);

            return (int)math.round(basePopulation * math.lerp(1f, scale, shape));
        }

        /// <summary>Сколько живых особей рядом с мировой точкой. Этим же ведётся звук толпы.</summary>
        public int CountNear(Vector3 worldPoint, float radius) => CountNear(LocalOf(worldPoint), radius);

        /// <summary>Сколько живых особей сейчас в ближнем круге игрока.</summary>
        private int CountNear(float3 targetLocal) => CountNear(targetLocal, nearRadius);

        private int CountNear(float3 targetLocal, float radius)
        {
            if (!_states.IsCreated) return 0;

            var radiusSq = radius * radius;
            var count = 0;

            for (var i = 0; i < _states.Length; i++)
            {
                var spider = _states[i];

                if (spider.Active == 0 || spider.Clip == (int)SpiderClip.Dead) continue;
                if (math.lengthsq(spider.Position - targetLocal) > radiusSq) continue;

                count++;
            }

            return count;
        }

        private void TopUp(float deltaTime)
        {
            // Пауза после крупного убийства: пролом в толпе должен постоять, иначе
            // игрок не увидит того, что сделал.
            if (_spawnHold > 0f) return;

            var wanted = WavePopulation();
            var rate = _directed ? _directive.SpawnRate : spawnRate;

            if (Alive >= wanted || rate <= 0f) return;

            // Кредит копится и когда спавнить некуда: игрок стоит в освещённом районе,
            // и все клетки полосы отвергнуты. Без потолка за минуту такого простоя
            // накопилось бы на залп в тысячу особей разом, стоило игроку выйти в темноту.
            _spawnCredit = math.min(_spawnCredit + rate * deltaTime, math.max(1f, rate));

            while (_spawnCredit >= 1f && Alive < wanted)
            {
                _spawnCredit -= 1f;

                if (!TrySpawn()) break;

                Alive++;
            }
        }

        private float _ebbTimer;

        /// <summary>Сколько особей сейчас уходит отливом. Ноль — проход по толпе не нужен.</summary>
        private int _ebbing;

        /// <summary>Сколько ушло отливом совсем и сколько вернулось в строй, не скрывшись. Для прогона.</summary>
        public int EbbVanished { get; private set; }

        public int EbbReturned { get; private set; }
        private readonly List<float2> _ebbCandidates = new List<float2>();

        /// <summary>
        /// Отлив: когда живых заметно больше, чем велит директор, лишние уходят в темноту.
        ///
        /// Уходят дальние, а не ближние: у игрока в ближнем круге идёт бой, и особь,
        /// развернувшаяся посреди атаки, читалась бы как сбой. Кто уже за дальностью
        /// отрисовки, того просто убираем — его всё равно не видно. Остальные бегут
        /// прочь по полю потока и исчезают, как только скроются; выдохшиеся рядом
        /// возвращаются в строй. Элита не уходит никогда.
        ///
        /// Порог — по цели БЕЗ коротких волн (с их гребнем): иначе отлив срабатывал бы
        /// на каждой ряби, и толпа ходила бы туда-сюда каждые полминуты.
        /// </summary>
        private void UpdateEbb(float3 targetLocal, bool targetValid, float deltaTime)
        {
            if (!targetValid) return;

            var gone = drawDistance * 0.9f;
            var goneSq = gone * gone;
            var ebbing = 0;

            // Скрылась ли особь, решает путь по ходам, а не прямая. Первая версия ждала,
            // пока уходящий отойдёт на дальность отрисовки, — и за восемь секунд бега
            // туда почти никто не добирался: особь возвращалась в строй и вставала
            // обратно в кольцо. Замер: в передышке стало 816 живых при цели 249, хуже,
            // чем без отлива вовсе. А по извилистым катакомбам дальше полосы спавна
            // уже не видно — медиана линии обзора здесь 14-16 юнитов.
            var vanishSteps = spawnSteps.y + 4;

            var sink = nearRadius * 1.5f;
            var sinkSq = sink * sink;

            // Проход по всей толпе — только когда кто-то уходит. Вне отлива (а без
            // директора всегда) он стоил бы копии каждой особи в каждом кадре.
            for (var i = 0; _ebbing > 0 && i < _states.Length; i++)
            {
                var spider = _states[i];

                if (spider.Active == 0 || spider.Ebb == 0 || spider.Clip == (int)SpiderClip.Dead) continue;

                var distanceSq = math.lengthsq(spider.Position - targetLocal);

                var vanish = spider.Ebb == 2
                    ? spider.Timer >= SpiderHash.SinkSeconds
                    : distanceSq > goneSq || StepsFromTarget(spider.Position) > vanishSteps;

                if (vanish)
                {
                    spider.Active = 0;
                    _states[i] = spider;
                    _neighbours[i] = new float4(spider.Position, 0f);
                    Alive--;
                    EbbVanished++;
                    continue;
                }

                if (spider.Ebb == 1 && spider.Flee <= 0f)
                {
                    // Отбежала, но не скрылась — застряла в дальней точке петли. Далеко
                    // от игрока — уходит в щель, в темноте на таком удалении это
                    // читается как паук, забившийся в трещину. Рядом — возвращается:
                    // гаснущая на глазах в пяти шагах тварь читалась бы как сбой.
                    if (distanceSq > sinkSq)
                    {
                        spider.Ebb = 2;
                        spider.Timer = 0f;
                    }
                    else
                    {
                        spider.Ebb = 0;
                        EbbReturned++;
                    }

                    _states[i] = spider;
                }

                if (spider.Ebb != 0) ebbing++;
            }

            _ebbing = ebbing;

            if (!_directed || ebbRate <= 0f) return;

            _ebbTimer -= deltaTime;
            if (_ebbTimer > 0f) return;

            const float interval = 0.25f;
            _ebbTimer = interval;

            var ceiling = _directive.Population * math.max(1f, _directive.MicroWave) * ebbSlack;
            var excess = Alive - ebbing - (int)math.ceil(ceiling);

            if (excess <= 0) return;

            var budget = math.min(excess, (int)math.ceil(ebbRate * interval));

            // Победа: вести больше некого — уходят все, включая ближний круг.
            var keepNear = _directive.Population > 0;
            var nearSq = nearRadius * nearRadius;

            _ebbCandidates.Clear();

            for (var i = 0; i < _states.Length; i++)
            {
                var spider = _states[i];

                if (spider.Active == 0 || spider.Ebb != 0 || spider.Elite != 0) continue;
                if (spider.Clip == (int)SpiderClip.Dead) continue;

                var distanceSq = math.lengthsq(spider.Position - targetLocal);
                if (keepNear && distanceSq <= nearSq) continue;

                _ebbCandidates.Add(new float2(distanceSq, i));
            }

            _ebbCandidates.Sort((a, b) => b.x.CompareTo(a.x));

            var drawSq = drawDistance * drawDistance;

            for (var k = 0; k < budget && k < _ebbCandidates.Count; k++)
            {
                var index = (int)_ebbCandidates[k].y;
                var spider = _states[index];

                if (_ebbCandidates[k].x > drawSq || StepsFromTarget(spider.Position) > vanishSteps)
                {
                    spider.Active = 0;
                    _neighbours[index] = new float4(spider.Position, 0f);
                    Alive--;
                    EbbVanished++;
                }
                else
                {
                    spider.Ebb = 1;
                    spider.Flee = ebbSeconds;
                    _ebbing++;

                    // Засадник на своде уходит вместе со всеми, а не висит один.
                    if (spider.Clip == (int)SpiderClip.Idle)
                    {
                        spider.Clip = (int)SpiderClip.Walk;
                        spider.Phase = 0f;
                    }
                }

                _states[index] = spider;
            }
        }

        /// <summary>
        /// Шагов по ходам от особи до игрока — меньшее по проходимым клеткам вокруг неё.
        ///
        /// По восьми клеткам, а не по одной под координатой: особь прижата к камню,
        /// и её собственная клетка сплошь и рядом непроходима (грабли №11). За пределом
        /// заливки — <see cref="SpiderFlowField.Unreachable"/>, то есть «очень далеко».
        /// Без единой проходимой клетки рядом — -1: судить не по чему.
        /// </summary>
        private int StepsFromTarget(float3 position)
        {
            var baseCell = (int3)math.floor(position / _flow.CellSize - 0.5f);
            var best = -1;

            for (var dz = 0; dz <= 1; dz++)
            for (var dy = 0; dy <= 1; dy++)
            for (var dx = 0; dx <= 1; dx++)
            {
                var c = baseCell + new int3(dx, dy, dz);

                if (!_flow.InRange(c)) continue;

                var index = _flow.CellIndex(c);
                if (_flow.Walkable[index] == 0) continue;

                var steps = (int)_flow.Distance[index];
                if (best < 0 || steps < best) best = steps;
            }

            return best;
        }

        private bool TrySpawn()
        {
            if (!_flow.SpawnCells.IsCreated || _flow.SpawnCells.Length == 0) return false;

            var slot = FindFreeSlot();
            if (slot < 0) return false;

            var cell = PickSpawnCell();
            if (cell < 0) return false;

            // Проверка запрета, а не его повторение: PickSpawnCell уже отвергает клетки
            // в освещённом, и если сюда что-то доехало, значит отбор промахнулся.
            if (_litZones.Count > 0 && InLitZone(WorldOf(_flow.CellCentre(cell)))) SpawnedInLitZones++;

            var swarm = _directed ? _directive.SwarmKind : -1;

            var kindIndex = swarm >= 0 && swarm < _runtime.Length && _random.NextFloat() < _directive.SwarmShare
                ? swarm
                : PickKind(_directed ? _directive.MaxKindHealth : int.MaxValue, true);

            var healthScale = _directed ? _directive.HealthScale : 1f;
            var speedScale = _directed ? _directive.SpeedScale : 1f;
            var hue = _directed ? (float3)_directive.Hue : new float3(1f);

            // Остервенение финального роя: засадников нет, все бегут сразу. Висящий
            // на своде паук в разгар роя читался бы как забытый, а не как затаившийся.
            var mayLurk = !_directed || !_directive.Frenzy;

            Place(slot, cell, kindIndex, 1f, RollHealth(_runtime[kindIndex].Kind.Health * healthScale),
                speedScale, hue, mayLurk, _random.NextFloat(), false);

            return true;
        }

        /// <summary>
        /// Вид новой особи.
        ///
        /// Под директором тяжёлые виды приходят по ходу забега: первые минуты это мелочь,
        /// которую выбивает любое попадание, а тарантулы появляются, когда игрок уже
        /// освоился. Выбор отбраковкой, а не взвешенной таблицей — видов десяток, и таблица
        /// пересчитывалась бы при каждом сдвиге порога.
        /// </summary>
        private int PickKind(int maxHealth, bool anyWhenUndirected)
        {
            if (anyWhenUndirected && !_directed) return _random.NextInt(_runtime.Length);

            for (var attempt = 0; attempt < 8; attempt++)
            {
                var index = _random.NextInt(_runtime.Length);

                if (_runtime[index].Kind.Health <= maxHealth) return index;
            }

            return _lightest;
        }

        /// <summary>
        /// Дробная живучесть в целые попадания. Дробь бросается жребием, а не округляется:
        /// округление держало бы мелочь на одном попадании до середины забега и переводило
        /// на два разом, то есть сложность шла бы ступенькой, а не кривой.
        /// </summary>
        private int RollHealth(float value)
        {
            var whole = (int)math.floor(value);

            if (_random.NextFloat() < value - whole) whole++;

            return math.max(1, whole);
        }

        /// <summary>Кладёт особь в слот: на камень под клеткой, с разбросом вдоль поверхности.</summary>
        private void Place(int slot, int cell, int kindIndex, float sizeScale, int health, float speedScale,
            float3 hue, bool mayLurk, float rank, bool elite)
        {
            var kind = _runtime[kindIndex].Kind;

            var scale = kind.Scale * sizeScale * (1f + _random.NextFloat(-kind.ScaleJitter, kind.ScaleJitter));

            // Сажаем сразу на камень, а не в центр клетки: клетка крупнее точности,
            // с которой особь должна прилегать к поверхности, и рождённая в её центре
            // особь первый кадр висит в воздухе или торчит из стены — а первый кадр
            // это ровно тот, в котором её замечает игрок.
            var normal = _flow.Normal[cell];
            var point = _flow.CellSurfacePoint(cell, kind.Hover * scale);

            // Разброс вдоль поверхности, а не по всем осям: поперёк неё разброс означал бы
            // отрыв от камня. Иначе вся волна выходит из одной точки колонной.
            var spread = _random.NextFloat2Direction() * (_random.NextFloat() * 0.35f * _flow.CellSize);
            var tangent = math.normalizesafe(math.cross(normal, math.abs(normal.y) > 0.9f
                ? new float3(1f, 0f, 0f)
                : new float3(0f, 1f, 0f)));

            point += tangent * spread.x + math.cross(normal, tangent) * spread.y;

            // Засада. Только на стенах и своде: паук, замерший посреди пола в пустом
            // коридоре, читается не как засада, а как сломавшийся паук. Сверху и сбоку
            // неподвижность объясняется сама собой.
            var lurks = mayLurk && normal.y < 0.4f && _random.NextFloat() < kind.LurkShare;

            // Белая окраска, если её не задали: нулевая означала бы чёрную особь.
            if (math.csum(hue) < 0.01f) hue = new float3(1f);

            var state = new SpiderState
            {
                Position = point,
                Velocity = float3.zero,
                Up = normal,
                Rotation = quaternion.LookRotationSafe(tangent * (kind.FacesMinusZ ? -1f : 1f), normal),
                Phase = _random.NextFloat(),
                Rank = rank,
                Scale = scale,
                SpeedScale = (1f + _random.NextFloat(-kind.SpeedJitter, kind.SpeedJitter)) * speedScale,
                Tint = _random.NextFloat(0.65f, 1.15f),
                Hue = hue,
                Serial = ++_serial,
                Elite = elite ? 1 : 0,
                Ebb = 0,
                Flee = 0f,
                Timer = 0f,
                Kind = kindIndex,
                Clip = (int)(lurks ? SpiderClip.Idle : SpiderClip.Walk),
                Health = health,
                Active = 1
            };

            _states[slot] = state;
            _neighbours[slot] = new float4(state.Position, _tuning[kindIndex].BodyRadius * state.Scale);
            _velocities[slot] = float3.zero;
            _heights[slot] = _tuning[kindIndex].Height * state.Scale;
            _climbs[slot] = 0f;
            _tiers[slot] = 0;

            SpawnedTotal++;
        }

        /// <summary>
        /// Сколько клеток перебрать, выбирая место под новую особь.
        ///
        /// Отбор здесь не может быть исчерпывающим: клеток под спавн тысячи, и проверять
        /// их все на каждую особь — это на порядок дороже самого шага поведения. Четыре
        /// пробы дают и отсев освещённого (шанс промахнуться мимо запрета падает до долей
        /// процента при сколько-нибудь заметной доле тёмных клеток), и уклон к генератору.
        /// </summary>
        private const int SpawnCellAttempts = 4;

        /// <summary>
        /// Клетка под новую особь: не в освещённом районе и, по возможности, ближе
        /// к незажжённому генератору.
        ///
        /// Уклон сделан выбором лучшей из нескольких проб, а не взвешенной выборкой по всем
        /// клеткам: таблица весов пересчитывалась бы на каждой перестройке поля потока
        /// и на каждом зажжённом районе, а лучшее из четырёх даёт тот же уклон бесплатно.
        /// </summary>
        /// <returns>Индекс клетки или -1, если подходящей не нашлось.</returns>
        private int PickSpawnCell()
        {
            var cells = _flow.SpawnCells;

            // Ни запретов, ни цели — прежнее поведение, без единой лишней проверки.
            if (_litZones.Count == 0 && !_hotPoint.HasValue) return cells[_random.NextInt(cells.Length)];

            var best = -1;
            var bestDistance = float.MaxValue;

            for (var attempt = 0; attempt < SpawnCellAttempts; attempt++)
            {
                var cell = cells[_random.NextInt(cells.Length)];
                var point = WorldOf(_flow.CellCentre(cell));

                if (InLitZone(point)) continue;

                if (!_hotPoint.HasValue) return cell;

                var distance = (point - _hotPoint.Value).sqrMagnitude;
                if (distance >= bestDistance) continue;

                best = cell;
                bestDistance = distance;
            }

            return best;
        }

        /// <summary>Внутри ли точка хоть одного освещённого района.</summary>
        private bool InLitZone(Vector3 worldPoint)
        {
            for (var i = 0; i < _litZones.Count; i++)
            {
                var zone = _litZones[i];
                var centre = new Vector3(zone.x, zone.y, zone.z);

                if ((centre - worldPoint).sqrMagnitude <= zone.w * zone.w) return true;
            }

            return false;
        }

        private int _scanCursor;

        /// <summary>
        /// Свободный слот, поиском с того места, где остановились в прошлый раз.
        ///
        /// Кольцевой курсор, а не список свободных: список пришлось бы держать в согласии
        /// с массивом, который правят джобы, а курсор не хранит состояния и не может
        /// разойтись с ним в принципе. Проход по всей ёмкости случается только когда
        /// свободных слотов нет вовсе.
        /// </summary>
        private int FindFreeSlot()
        {
            var length = _states.Length;

            for (var i = 0; i < length; i++)
            {
                var index = _scanCursor;
                _scanCursor = _scanCursor + 1 >= length ? 0 : _scanCursor + 1;

                if (_states[index].Active == 0) return index;
            }

            return -1;
        }

        private void CountAlive()
        {
            var alive = 0;

            for (var i = 0; i < _states.Length; i++)
            {
                if (_states[i].Active != 0) alive++;
            }

            Alive = alive;
        }

        private void Draw(KindRuntime runtime, float3 cameraWorld)
        {
            var count = runtime.Count[0];
            if (count <= 0) return;

            runtime.Matrices.Reinterpret<Matrix4x4>().CopyTo(runtime.AllMatrices);
            runtime.Anim.Reinterpret<Vector4>().CopyTo(runtime.AllAnim);
            runtime.Tint.Reinterpret<Vector4>().CopyTo(runtime.AllTint);

            var batchSize = math.max(1, instancesPerBatch);

            var parameters = new RenderParams(runtime.Kind.Material)
            {
                // Границы партии — куб вокруг камеры по дальности отрисовки. Точные
                // границы толпы считать незачем: отсев на особь уже сделан в джобе,
                // а эти нужны только чтобы Unity не выкинул партию целиком.
                worldBounds = new Bounds((Vector3)cameraWorld, Vector3.one * (drawDistance * 2f)),
                shadowCastingMode = castShadows ? ShadowCastingMode.On : ShadowCastingMode.Off,
                receiveShadows = true,
                layer = gameObject.layer,
                renderingLayerMask = 1,
                lightProbeUsage = LightProbeUsage.Off,
                reflectionProbeUsage = ReflectionProbeUsage.Off
            };

            var batch = 0;

            for (var offset = 0; offset < count; offset += batchSize, batch++)
            {
                var size = math.min(batchSize, count - offset);

                Array.Copy(runtime.AllMatrices, offset, runtime.MatrixBatch, 0, size);
                Array.Copy(runtime.AllAnim, offset, runtime.AnimBatch, 0, size);
                Array.Copy(runtime.AllTint, offset, runtime.TintBatch, 0, size);

                while (runtime.Blocks.Count <= batch) runtime.Blocks.Add(new MaterialPropertyBlock());

                var block = runtime.Blocks[batch];

                block.SetVectorArray(AnimStateId, runtime.AnimBatch);
                block.SetVectorArray(InstanceTintId, runtime.TintBatch);

                parameters.matProps = block;

                Graphics.RenderMeshInstanced(parameters, runtime.Kind.Mesh, 0, runtime.MatrixBatch, size);

                Batches++;
            }

            Drawn += count;
        }

        private int CollectKinds()
        {
            var ready = new List<SpiderKind>();

            if (kinds != null)
            {
                foreach (var kind in kinds)
                {
                    if (kind == null) continue;

                    if (!kind.IsReady)
                    {
                        Debug.LogWarning($"{nameof(SpiderCrowd)}: вид «{kind.name}» не запечён — пропущен. " +
                                         "Запустите Tools/Mine Generator/Пауки: запечь анимацию в текстуры.", this);
                        continue;
                    }

                    ready.Add(kind);
                }
            }

            if (ready.Count == 0)
            {
                Debug.LogWarning($"{nameof(SpiderCrowd)}: не назначено ни одного готового вида пауков.", this);
                return 0;
            }

            _runtime = new KindRuntime[ready.Count];
            _tuning = new NativeArray<SpiderTuning>(ready.Count, Allocator.Persistent);

            for (var i = 0; i < ready.Count; i++)
            {
                var kind = ready[i];

                var walk = kind.GetClip(SpiderClip.Walk);
                var attack = kind.GetClip(SpiderClip.Attack);
                var dead = kind.GetClip(SpiderClip.Dead);
                var idle = kind.GetClip(SpiderClip.Idle);

                _tuning[i] = new SpiderTuning
                {
                    MoveSpeed = kind.MoveSpeed,
                    TurnSpeed = kind.TurnSpeed,
                    BodyRadius = kind.BodyRadius,
                    AttackRange = kind.AttackRange,
                    StrideLength = kind.StrideLength,
                    CorpseLinger = kind.CorpseLinger,
                    Hover = kind.Hover,
                    // По позе бега; у ассета, не перезапечённого после появления поля, —
                    // по габаритам, как было (вдвое выше настоящей, см. SpiderKind.PoseHeight).
                    Height = math.max(0.05f, kind.PoseHeight > 0f ? kind.PoseHeight : kind.RestBounds.size.y),
                    FacingSign = kind.FacesMinusZ ? -1f : 1f,

                    WalkLength = math.max(0.05f, walk.Length),
                    AttackLength = math.max(0.05f, attack.Length),
                    DeadLength = math.max(0.05f, dead.Length),
                    IdleLength = math.max(0.05f, idle.Length),
                    LurkTrigger = kind.LurkTrigger,

                    WalkRow = new float3(walk.StartRow, walk.FrameCount, walk.Loop ? 1f : 0f),
                    AttackRow = new float3(attack.StartRow, attack.FrameCount, attack.Loop ? 1f : 0f),
                    DeadRow = new float3(dead.StartRow, dead.FrameCount, dead.Loop ? 1f : 0f),
                    IdleRow = new float3(idle.StartRow, idle.FrameCount, idle.Loop ? 1f : 0f)
                };

                var extents = kind.RestBounds.extents.magnitude * kind.Scale * (1f + kind.ScaleJitter);

                _runtime[i] = new KindRuntime
                {
                    Kind = kind,
                    Index = i,
                    CullRadius = math.max(0.25f, extents)
                };
            }

            // Самый лёгкий — запасной вид для отбора по живучести; самый тяжёлый — вид
            // элиты. При равной живучести крупнее тот, кто страшнее выглядит.
            _lightest = 0;
            _heaviest = 0;

            for (var i = 1; i < ready.Count; i++)
            {
                if (Weight(ready[i]) < Weight(ready[_lightest])) _lightest = i;
                if (Weight(ready[i]) > Weight(ready[_heaviest])) _heaviest = i;
            }

            return ready.Count;
        }

        private static float Weight(SpiderKind kind) => kind.Health * 100f + kind.Scale;

        private void AllocateState()
        {
            var size = math.max(1, capacity);

            _states = new NativeArray<SpiderState>(size, Allocator.Persistent);
            _neighbours = new NativeArray<float4>(size, Allocator.Persistent);
            _velocities = new NativeArray<float3>(size, Allocator.Persistent);
            _heights = new NativeArray<float>(size, Allocator.Persistent);
            _climbs = new NativeArray<float>(size, Allocator.Persistent);
            _tiers = new NativeArray<int>(size, Allocator.Persistent);
            _killed = new NativeArray<int>(size, Allocator.Persistent);
            _frustum = new NativeArray<float4>(6, Allocator.Persistent);

            _hash = new NativeParallelMultiHashMap<int, int>(size, Allocator.Persistent);

            foreach (var runtime in _runtime)
            {
                runtime.Matrices = new NativeArray<float4x4>(size, Allocator.Persistent);
                runtime.Anim = new NativeArray<float4>(size, Allocator.Persistent);
                runtime.Tint = new NativeArray<float4>(size, Allocator.Persistent);
                runtime.Count = new NativeArray<int>(1, Allocator.Persistent);

                runtime.AllMatrices = new Matrix4x4[size];
                runtime.AllAnim = new Vector4[size];
                runtime.AllTint = new Vector4[size];

                var batchSize = math.max(1, instancesPerBatch);

                runtime.MatrixBatch = new Matrix4x4[batchSize];
                runtime.AnimBatch = new Vector4[batchSize];
                runtime.TintBatch = new Vector4[batchSize];
            }

            _scanCursor = 0;
            _spawnCredit = 0f;
            Alive = 0;
            SpawnedTotal = 0;
        }

        private void Release()
        {
            if (_states.IsCreated) _states.Dispose();
            if (_neighbours.IsCreated) _neighbours.Dispose();
            if (_velocities.IsCreated) _velocities.Dispose();
            if (_heights.IsCreated) _heights.Dispose();
            if (_climbs.IsCreated) _climbs.Dispose();
            if (_tiers.IsCreated) _tiers.Dispose();
            if (_tuning.IsCreated) _tuning.Dispose();
            if (_killed.IsCreated) _killed.Dispose();
            if (_frustum.IsCreated) _frustum.Dispose();
            if (_hash.IsCreated) _hash.Dispose();

            if (_runtime != null)
            {
                foreach (var runtime in _runtime) runtime.Dispose();
                _runtime = null;
            }

            _flow.Dispose();

            _flowCell = -1;
            Alive = 0;
            Drawn = 0;
            Batches = 0;
        }

        private float3 LocalOf(Vector3 worldPoint) =>
            world != null ? (float3)world.transform.InverseTransformPoint(worldPoint) : (float3)worldPoint;

        private Vector3 WorldOf(float3 localPoint) =>
            world != null ? world.transform.TransformPoint(localPoint) : (Vector3)localPoint;

        private float3 SpawnPointLocal()
        {
            if (world != null && world.TryGetSpawnPoint(out var spawn)) return LocalOf(spawn);

            return world != null ? (float3)world.Settings.WorldSize * 0.5f : float3.zero;
        }

        private Camera ResolveCamera()
        {
            if (cullCamera != null) return cullCamera;

            // Camera.main тут не годится: камера отладочного стенда создаётся кодом
            // и тега MainCamera не имеет. На этом в проекте уже молча ломался туман.
            if (target != null)
            {
                cullCamera = target.GetComponentInChildren<Camera>();
                if (cullCamera != null) return cullCamera;
            }

            cullCamera = Camera.main;
            return cullCamera;
        }

        /// <summary>
        /// Мировые позиции живых особей — для инструментов проверки.
        ///
        /// Копией, а не выдачей нативного массива наружу: массив живёт ровно столько,
        /// сколько компонент, и ссылка на него, пережившая перегенерацию уровня,
        /// указывала бы в освобождённую память.
        /// </summary>
        public int CopyPositions(List<Vector3> into, bool includeDead = false)
        {
            into.Clear();

            if (!_states.IsCreated) return 0;

            var matrix = world != null ? world.transform.localToWorldMatrix : Matrix4x4.identity;

            for (var i = 0; i < _states.Length; i++)
            {
                var spider = _states[i];

                if (spider.Active == 0) continue;
                if (!includeDead && spider.Clip == (int)SpiderClip.Dead) continue;

                into.Add(matrix.MultiplyPoint3x4((Vector3)spider.Position));
            }

            return into.Count;
        }

        /// <summary>
        /// Средняя скорость живых особей. Различает два очень разных отказа, которые
        /// по одному только расстоянию до игрока неотличимы: толпа застряла (скорость
        /// около нуля) или толпа бодро мечется, но не туда (скорость нормальная,
        /// расстояние стоит).
        /// </summary>
        public float MeanSpeed()
        {
            if (!_states.IsCreated) return 0f;

            var sum = 0f;
            var alive = 0;

            for (var i = 0; i < _states.Length; i++)
            {
                var spider = _states[i];

                if (spider.Active == 0 || spider.Clip == (int)SpiderClip.Dead) continue;

                sum += math.length(spider.Velocity);
                alive++;
            }

            return alive == 0 ? 0f : sum / alive;
        }

        /// <summary>
        /// Зазор между телом бьющей особи и капсулой игрока, в юнитах. Возвращает,
        /// сколько особей бьёт прямо сейчас.
        ///
        /// Метрика заведена по жалобе «атакуют слишком далеко, как будто стреляют».
        /// Прогон этого не ловил вовсе: у толпы, заносящей лапу в четырёх юнитах
        /// от игрока, связность, посадка, скорость и ориентация ровно те же, что
        /// у толпы, бьющей вплотную. Тот же урок, что грабли №19.
        ///
        /// Мерить надо именно ЗАЗОР, а не расстояние до цели: само расстояние ничего
        /// не значит, пока из него не вычтены половина длины особи и радиус игрока —
        /// тарантул вдвое длиннее каракурта, и одно и то же расстояние для них
        /// означает разное. Ноль — тела соприкоснулись.
        /// </summary>
        public int MeasureAttackGap(out float mean, out float min, out float max)
        {
            mean = 0f;
            min = 0f;
            max = 0f;

            if (!_states.IsCreated || !_tuning.IsCreated || target == null) return 0;

            var targetLocal = LocalOf(target.position);
            ResolveTargetBody(targetLocal, out var low, out var high);

            var playerRadius = _targetBody != null ? _targetBody.radius : 0f;

            var sum = 0f;
            var count = 0;

            min = float.MaxValue;
            max = float.MinValue;

            for (var i = 0; i < _states.Length; i++)
            {
                var spider = _states[i];

                if (spider.Active == 0 || spider.Clip != (int)SpiderClip.Attack) continue;

                var aim = new float3(targetLocal.x, math.clamp(spider.Position.y, low, high), targetLocal.z);

                var gap = math.distance(aim, spider.Position)
                          - _tuning[spider.Kind].BodyRadius * spider.Scale
                          - playerRadius;

                sum += gap;
                min = math.min(min, gap);
                max = math.max(max, gap);
                count++;
            }

            if (count == 0)
            {
                min = 0f;
                max = 0f;
                return 0;
            }

            mean = sum / count;
            return count;
        }

        /// <summary>
        /// Меряет то, что числами прогона не видно, а глазами видно сразу: бежит ли толпа
        /// лицом вперёд и не крутится ли она на месте.
        ///
        /// Оба отказа прошли бы любую другую проверку с отличием — связность, посадка
        /// и скорость у вертящейся задом наперёд толпы ровно те же. Поэтому мерится
        /// отдельно и явно.
        ///
        /// Усредняется по нескольким десяткам шагов, а не снимается за один кадр.
        /// Толпа — система хаотичная, и замер по одному кадру гуляет: на неизменном коде
        /// он давал от 129 до 207 градусов в секунду, то есть пересекал любой разумный
        /// порог туда-сюда просто от того, в какой момент его сняли. Усреднение эту
        /// болтанку убирает, и порог начинает что-то значить.
        /// </summary>
        /// <param name="headingError">Угол между взглядом и направлением бега, градусы. Должен быть мал.</param>
        /// <param name="turnRate">Средняя угловая скорость, градусов в секунду.</param>
        public void MeasureOrientation(float deltaTime, out float headingError, out float turnRate)
        {
            headingError = 0f;
            turnRate = 0f;

            if (!_states.IsCreated || deltaTime <= 0f) return;

            const int samples = 30;

            var before = new quaternion[_states.Length];
            var moving = new bool[_states.Length];

            var headingSum = 0f;
            var headingCount = 0;

            var turnSum = 0f;
            var turnCount = 0;

            for (var pass = 0; pass < samples; pass++)
            {
                for (var i = 0; i < _states.Length; i++)
                {
                    before[i] = _states[i].Rotation;
                    moving[i] = _states[i].Active != 0 && _states[i].Clip != (int)SpiderClip.Dead;
                }

                Simulate(deltaTime);

                for (var i = 0; i < _states.Length; i++)
                {
                    var spider = _states[i];

                    if (!moving[i] || spider.Active == 0 || spider.Clip == (int)SpiderClip.Dead) continue;

                    var dot = math.abs(math.dot(before[i].value, spider.Rotation.value));

                    turnSum += math.degrees(2f * math.acos(math.clamp(dot, -1f, 1f)));
                    turnCount++;

                    var speed = math.length(spider.Velocity);
                    if (speed < 0.2f) continue;

                    // Куда особь СМОТРИТ. У пака Spiders голова в минус Z, поэтому взгляд
                    // это минус forward; знак живёт в SpiderKind.FacesMinusZ.
                    var sign = _tuning[spider.Kind].FacingSign;
                    var gaze = math.forward(spider.Rotation) * sign;

                    headingSum += math.degrees(math.acos(math.clamp(
                        math.dot(gaze, spider.Velocity / speed), -1f, 1f)));

                    headingCount++;
                }
            }

            if (headingCount > 0) headingError = headingSum / headingCount;
            if (turnCount > 0) turnRate = turnSum / turnCount / deltaTime;
        }

        /// <summary>Нормали поверхностей, за которые держатся живые особи — для проверки.</summary>
        public int CopyNormals(List<Vector3> into)
        {
            into.Clear();

            if (!_states.IsCreated) return 0;

            var matrix = world != null ? world.transform.localToWorldMatrix : Matrix4x4.identity;

            for (var i = 0; i < _states.Length; i++)
            {
                var spider = _states[i];

                if (spider.Active == 0 || spider.Clip == (int)SpiderClip.Dead) continue;

                into.Add(matrix.MultiplyVector((Vector3)spider.Up).normalized);
            }

            return into.Count;
        }

        /// <summary>Строка для наложения в стенде.</summary>
        public string Describe()
        {
            if (!_flow.IsCreated) return "толпа: сетка не построена";

            return $"пауков {Alive}, в кадре {Drawn} за {Batches} вызовов | {_flow.Describe()}";
        }

        private void OnDrawGizmosSelected()
        {
            if (!drawFlowGizmos || !_flow.IsCreated || world == null) return;

            var around = target != null ? LocalOf(target.position) : SpawnPointLocal();

            _flow.DrawGizmos(world.transform.localToWorldMatrix, around, gizmoRadius);
        }
    }
}
