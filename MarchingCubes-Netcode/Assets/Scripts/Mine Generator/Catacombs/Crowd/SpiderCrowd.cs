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
        [Tooltip("Радиус ближнего круга, юниты.")]
        [SerializeField, Range(2f, 30f)] private float nearRadius = 12f;

        /// <summary>
        /// Сколько особей пускать в ближний круг. Остальные ждут дальше.
        ///
        /// Без потолка вся орда сжимается в кольцо вокруг игрока и упирается в него
        /// сплошной стеной: замер давал 690 видимых особей при полном отсутствии
        /// негативного пространства. Силуэт читается только тогда, когда вокруг него
        /// есть пустота.
        /// </summary>
        [Tooltip("Сколько особей пускать в ближний круг. Остальные ждут дальше.")]
        [SerializeField, Range(0, 500)] private int nearCap = 70;

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
        private NativeArray<SpiderTuning> _tuning;
        private NativeArray<float4> _frustum;
        private NativeArray<int> _killed;
        private NativeParallelMultiHashMap<int, int> _hash;

        private KindRuntime[] _runtime;

        private Random _random;
        private float _flowTimer;
        private int _flowCell = -1;
        private float _spawnCredit;

        /// <summary>Время для волны населения и пауза досыла после крупного убийства.</summary>
        private float _waveTime;
        private float _spawnHold;

        private readonly Plane[] _planes = new Plane[6];

        public int Alive { get; private set; }
        public int Drawn { get; private set; }
        public int Batches { get; private set; }
        public bool HasField => _flow.IsCreated;
        public SpiderFlowField Field => _flow;

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

            FillPopulation(population);
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

            UpdateFlow(targetLocal, targetValid != 0, deltaTime);

            _waveTime += deltaTime;
            _spawnHold = math.max(0f, _spawnHold - deltaTime);

            TopUp(deltaTime);

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
                Hash = _hash,
                Tuning = _tuning,
                Field = _flow.Sampler,

                Target = targetLocal,
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
                NearCap = nearCap,
                NearRadius = nearRadius,
                CorpseDrag = corpseDrag
            }.Schedule(_states.Length, 32, handle);

            handle = new SpiderPublishJob
            {
                States = _states,
                Tuning = _tuning,
                Neighbours = _neighbours,
                Velocities = _velocities
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

            new SpiderDamageJob
            {
                States = _states,
                Center = LocalOf(worldPoint),
                RadiusSq = radius * radius,
                Damage = math.max(1, damage),
                Impulse = deathImpulse,
                Seed = (uint)math.max(1, Environment.TickCount),
                Killed = _killed
            }.Schedule(_states.Length, 64).Complete();

            var killed = 0;
            for (var i = 0; i < _killed.Length; i++) killed += _killed[i];

            // Пролом в толпе должен постоять. Порог по доле ближнего круга, а не по числу:
            // одиночное попадание не должно останавливать волну, а выкос половины
            // ближнего круга — должен, иначе игрок не увидит результата.
            if (killed >= math.max(4, nearCap / 3)) _spawnHold = spawnHoldAfterKill;

            return killed;
        }

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
                Velocities = _velocities
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
            if (waveScale <= 1.001f || wavePeriod <= 0f) return population;

            var t = math.frac(_waveTime / wavePeriod);
            var half = waveCrest * 0.5f;

            // Гребень посередине цикла, по четверти периода с каждой стороны на разгон
            // и спад. Края цикла — затишье.
            var rise = math.saturate((t - (0.5f - half - 0.25f)) / 0.25f);
            var fall = 1f - math.saturate((t - (0.5f + half)) / 0.25f);

            var shape = math.min(rise, fall);

            return (int)math.round(population * math.lerp(1f, waveScale, shape));
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

            if (Alive >= wanted || spawnRate <= 0f) return;

            _spawnCredit += spawnRate * deltaTime;

            while (_spawnCredit >= 1f && Alive < wanted)
            {
                _spawnCredit -= 1f;

                if (!TrySpawn()) break;

                Alive++;
            }
        }

        private bool TrySpawn()
        {
            if (!_flow.SpawnCells.IsCreated || _flow.SpawnCells.Length == 0) return false;

            var slot = FindFreeSlot();
            if (slot < 0) return false;

            var cell = _flow.SpawnCells[_random.NextInt(_flow.SpawnCells.Length)];

            var kindIndex = _random.NextInt(_runtime.Length);
            var kind = _runtime[kindIndex].Kind;

            var scale = kind.Scale * (1f + _random.NextFloat(-kind.ScaleJitter, kind.ScaleJitter));

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
            var lurks = normal.y < 0.4f && _random.NextFloat() < kind.LurkShare;

            var state = new SpiderState
            {
                Position = point,
                Velocity = float3.zero,
                Up = normal,
                Rotation = quaternion.LookRotationSafe(tangent * (kind.FacesMinusZ ? -1f : 1f), normal),
                Phase = _random.NextFloat(),
                Rank = _random.NextFloat(),
                Scale = scale,
                SpeedScale = 1f + _random.NextFloat(-kind.SpeedJitter, kind.SpeedJitter),
                Tint = _random.NextFloat(0.65f, 1.15f),
                Timer = 0f,
                Kind = kindIndex,
                Clip = (int)(lurks ? SpiderClip.Idle : SpiderClip.Walk),
                Health = kind.Health,
                Active = 1
            };

            _states[slot] = state;
            _neighbours[slot] = new float4(state.Position, _tuning[kindIndex].BodyRadius * state.Scale);
            _velocities[slot] = float3.zero;

            return true;
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

            return ready.Count;
        }

        private void AllocateState()
        {
            var size = math.max(1, capacity);

            _states = new NativeArray<SpiderState>(size, Allocator.Persistent);
            _neighbours = new NativeArray<float4>(size, Allocator.Persistent);
            _velocities = new NativeArray<float3>(size, Allocator.Persistent);
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
        }

        private void Release()
        {
            if (_states.IsCreated) _states.Dispose();
            if (_neighbours.IsCreated) _neighbours.Dispose();
            if (_velocities.IsCreated) _velocities.Dispose();
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
