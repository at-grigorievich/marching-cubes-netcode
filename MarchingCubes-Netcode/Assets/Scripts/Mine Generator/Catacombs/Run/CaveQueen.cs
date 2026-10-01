using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace MineGenerator.Catacombs
{
    /// <summary>
    /// Матка — босс стадии, устроенный по логике финального босса Megabonk.
    ///
    /// Что взято оттуда и зачем:
    ///
    /// * **Логово — это портал босса.** Оно лежит в дальнем зале, и его надо найти: ведут
    ///   тление жил и плотность орды. Будит Матку сам игрок, когда готов, — и чем позже,
    ///   тем она живучее (у Megabonk босс за стадию крепнет примерно вчетверо).
    /// * **Щит на порогах здоровья.** На двух третях и на трети Матка становится
    ///   неуязвимой и лечится, а вокруг зажигаются задачи — три генератора, которые
    ///   надо запустить, стоя рядом. Не успел за минуту — она снова почти здорова.
    ///   Порог нельзя перепрыгнуть одним мощным попаданием: здоровье упирается в него.
    /// * **Волны, сталкивающие с точки.** Пока игрок бегает по генераторам, Матка
    ///   бьёт ударной волной чаще и дальше — у Megabonk ровно это сгоняет с пилонов.
    /// * **Подкрепления по таймеру** — выводок вокруг неё, чаще под щитом.
    /// * **Одно попадание обездвиживает** — паутинный плевок вместо ледяного залпа.
    ///
    /// Генераторы здесь не выброшены из прежней игры, а стали её частью: механика
    /// «стой рядом, пока заряжается, и держись» теперь живёт в фазе щита, а зажжённый
    /// генератор освещает логово — к концу боя гнездо Матки залито светом.
    ///
    /// Урона игроку Матка не наносит: здоровья у игрока в проекте пока нет. Все её
    /// атаки поэтому выбраны такими, что работают и без него, — отброс, обездвиживание,
    /// выводок. Когда здоровье появится, подключать урон надо в тех же местах.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CaveQueen : MonoBehaviour
    {
        [SerializeField] private CatacombWorld world;
        [SerializeField] private SpiderCrowd crowd;
        [SerializeField] private CaveFixtures fixtures;

        [Tooltip("За кем охотится. Пусто — отладочный стенд со сцены.")]
        [SerializeField] private CatacombTestRig target;

        [Header("Логово")]
        /// <summary>
        /// Из какой доли самых дальних залов выбирается логово.
        ///
        /// Не строго самый дальний: тогда логово всегда в одном и том же углу карты,
        /// и второй забег на том же сиде превращается в пробежку по памяти. У Megabonk
        /// портал босса в случайном месте — здесь случайный, но заведомо не рядом.
        /// </summary>
        [Tooltip("Из какой доли самых дальних от входа залов выбирается логово.")]
        [SerializeField, Range(0.1f, 1f)] private float lairFarShare = 0.3f;

        [Tooltip("С какого расстояния можно разбудить Матку, юниты.")]
        [SerializeField, Range(1f, 15f)] private float useRange = 5f;

        [Tooltip("На каком удалении от логова тление жил сходит в ноль.")]
        [SerializeField, Range(10f, 300f)] private float guideReach = 90f;

        [SerializeField] private Color lairColor = new Color(0.85f, 0.16f, 0.1f);

        [Tooltip("Как далеко вниз искать пол под логово.")]
        [SerializeField] private float probeDistance = 20f;

        [Header("Матка")]
        [Tooltip("Во сколько раз Матка крупнее самого крупного вида толпы.")]
        [SerializeField, Range(1f, 3f)] private float sizeScale = 1.6f;

        /// <summary>
        /// Живучесть при пробуждении в самом начале стадии, в попаданиях. Попадание пока
        /// одно — пробный взрыв стенда, поэтому число условное: подбирать заново, когда
        /// появится граната.
        /// </summary>
        [Tooltip("Живучесть, если разбудить в самом начале стадии, в попаданиях.")]
        [SerializeField, Range(1, 5000)] private int baseHealth = 36;

        [Tooltip("Скорость доворота на игрока, градусов в секунду. Медленно: от неё бегают кругами.")]
        [SerializeField, Range(10f, 360f)] private float turnSpeed = 70f;

        [Tooltip("Окраска — множитель альбедо.")]
        [SerializeField] private Color hue = new Color(0.8f, 0.55f, 0.5f);

        [Tooltip("Дальше этого Матка игрока не замечает и не атакует.")]
        [SerializeField, Range(5f, 150f)] private float engageRange = 45f;

        [Header("Выводок")]
        [SerializeField, Range(1f, 60f)] private float broodInterval = 7f;
        [SerializeField, Range(0.5f, 60f)] private float broodShieldInterval = 3.5f;
        [SerializeField, Range(1, 60)] private int broodCount = 6;

        [Tooltip("Насколько далеко по ходам от Матки вылезает выводок, в шагах сетки.")]
        [SerializeField, Range(1, 20)] private int broodSteps = 6;

        [Tooltip("До скольких особей рядом с Маткой выводок добирает свиту. Сверх — не заводится.")]
        [SerializeField, Range(0, 500)] private int broodCap = 40;

        [Tooltip("В каком радиусе от логова считается свита, юниты.")]
        [SerializeField, Range(2f, 60f)] private float broodCapRadius = 20f;

        [Header("Ударная волна")]
        [SerializeField, Range(1f, 60f)] private float shockInterval = 9f;

        [Tooltip("Под щитом волна чаще: она сгоняет игрока с генераторов.")]
        [SerializeField, Range(0.5f, 60f)] private float shockShieldInterval = 4f;

        [SerializeField, Range(2f, 40f)] private float shockRadius = 11f;

        [Tooltip("Во сколько раз волна дальше под щитом.")]
        [SerializeField, Range(1f, 4f)] private float shockShieldReach = 1.8f;

        [Tooltip("Замах перед волной, секунды: пятно на полу растёт — отойди.")]
        [SerializeField, Range(0.1f, 4f)] private float shockWindup = 1.1f;

        [SerializeField, Range(0f, 60f)] private float shockPush = 16f;
        [SerializeField, Range(0f, 20f)] private float shockLift = 4f;

        [Header("Паутинный плевок")]
        [SerializeField, Range(1f, 60f)] private float spitInterval = 6f;
        [SerializeField, Range(1, 12)] private int spitVolley = 3;
        [SerializeField, Range(0.05f, 2f)] private float spitGap = 0.25f;
        [SerializeField, Range(2f, 60f)] private float spitSpeed = 14f;
        [SerializeField, Range(5f, 100f)] private float spitRange = 32f;

        [Tooltip("Сколько секунд игрок спутан после попадания.")]
        [SerializeField, Range(0.1f, 10f)] private float entangleSeconds = 2.2f;

        [Tooltip("Во сколько раз медленнее ходит спутанный игрок.")]
        [SerializeField, Range(0f, 1f)] private float entangleScale = 0.2f;

        [SerializeField] private Color spitColor = new Color(0.75f, 0.9f, 0.65f);

        [Header("Щит и генераторы")]
        /// <summary>
        /// Пороги щита, доли здоровья. Две трети и треть — как у финального босса Megabonk:
        /// три отрезка урона, между ними две фазы пилонов.
        /// </summary>
        [Tooltip("На каких долях здоровья Матка закрывается щитом.")]
        [SerializeField] private float[] shieldAt = { 2f / 3f, 1f / 3f };

        [SerializeField, Range(1, 6)] private int pylonCount = 3;

        [Tooltip("Сколько секунд стоять у генератора, чтобы он запустился.")]
        [SerializeField, Range(0.5f, 30f)] private float pylonCharge = 4f;

        [Tooltip("Насколько близко стоять, юниты по горизонтали.")]
        [SerializeField, Range(0.5f, 10f)] private float pylonRadius = 2.8f;

        [Tooltip("Как быстро заряд уходит, если отойти, долей в секунду.")]
        [SerializeField, Range(0f, 2f)] private float pylonDecay = 0.25f;

        /// <summary>
        /// Удаление генераторов от Матки по ходам, в шагах сетки (шаг — юнит).
        /// Не ближе восьми — иначе зарядка шла бы в упор к ней, — и не дальше двадцати
        /// шести: у Megabonk пилоны стоят в той же арене, а не по всей карте.
        /// </summary>
        [SerializeField] private Vector2Int pylonSteps = new Vector2Int(8, 26);

        /// <summary>
        /// За сколько секунд под щитом Матка возвращается от порога к полному здоровью.
        ///
        /// Минута — это «не успел за минуту — начинай заново» из разборов Megabonk.
        /// Задано временем, а не долей в секунду, и это не мелочь: постоянная доля
        /// в полтора процента возвращала Матку с двух третей к полному за двадцать две
        /// секунды — быстрее, чем игрок успевает обежать три генератора, — а с трети
        /// за сорок пять. Одна и та же фаза выходила то непроходимой, то прогулкой.
        /// </summary>
        [Tooltip("За сколько секунд под щитом Матка вылечивается от порога до полного здоровья.")]
        [SerializeField, Range(5f, 600f)] private float healFullSeconds = 60f;

        [SerializeField] private float pylonRange = 18f;
        [SerializeField] private float pylonIntensity = 30f;

        [Tooltip("Радиус района, который зажигает запущенный генератор.")]
        [SerializeField] private float pylonLitRadius = 12f;

        [SerializeField] private Color pylonDarkColor = new Color(0.30f, 0.46f, 0.60f);
        [SerializeField] private Color pylonLitColor = new Color(1f, 0.86f, 0.55f);
        [SerializeField] private Color shieldColor = new Color(0.45f, 0.7f, 1f);

        public enum QueenState
        {
            /// <summary>Спит в логове, ждёт игрока.</summary>
            Dormant,

            /// <summary>Проснулась, уязвима.</summary>
            Awake,

            /// <summary>Под щитом: неуязвима, лечится, ждёт, пока игрок запустит генераторы.</summary>
            Shielded,

            Dead
        }

        public QueenState State { get; private set; }

        public bool HasLair { get; private set; }
        public Vector3 LairPosition { get; private set; }

        public float Health { get; private set; }
        public float MaxHealth { get; private set; }
        public float HealthFraction => MaxHealth > 0f ? Health / MaxHealth : 0f;

        /// <summary>Сколько раз закрывалась щитом — для проверки фаз.</summary>
        public int ShieldsRaised { get; private set; }

        /// <summary>Сколько генераторов запущено за весь бой.</summary>
        public int PylonsLitTotal { get; private set; }

        public int PylonsInPhase => _pylons.Count;

        public int PylonsLitInPhase
        {
            get
            {
                var lit = 0;
                foreach (var pylon in _pylons)
                {
                    if (pylon.Lit) lit++;
                }

                return lit;
            }
        }

        /// <summary>Позиции генераторов текущей фазы щита.</summary>
        public IEnumerable<Vector3> PylonPositions
        {
            get
            {
                foreach (var pylon in _pylons) yield return pylon.Position;
            }
        }

        /// <summary>Центр тела — сюда целятся и отсюда идут волна и плевок.</summary>
        public Vector3 Centre => LairPosition + Vector3.up * _bodyLift;

        /// <summary>Радиус тела для попаданий — взрыватель гранаты срабатывает у него.</summary>
        public float HitRadius => _hitRadius;

        /// <summary>По Матке сейчас можно попасть: она проснулась (под щитом попадание есть, урона нет).</summary>
        public bool IsVulnerable => State == QueenState.Awake || State == QueenState.Shielded;

        /// <summary>Сколько раз волна достала игрока и сколько раз плевок попал — для прогона.</summary>
        public int ShocksLanded { get; private set; }

        public int SpitsLanded { get; private set; }

        /// <summary>Объявления боя: пробудилась, щит, генератор запущен.</summary>
        public event Action<string> Announced;

        /// <summary>Матка убита. Аргумент — центр тела.</summary>
        public event Action<Vector3> Died;

        private sealed class Pylon
        {
            public Vector3 Position;
            public float Charge;
            public bool Lit;

            public Light Light;
            public Transform Halo;
            public Renderer Core;
        }

        private sealed class Spit
        {
            public Vector3 Position;
            public Vector3 Velocity;
            public float Life;
            public GameObject View;
        }

        private readonly List<Pylon> _pylons = new List<Pylon>();
        private readonly List<Vector3> _pylonHistory = new List<Vector3>();
        private readonly List<Vector4> _litZones = new List<Vector4>();
        private readonly List<Spit> _spits = new List<Spit>();
        private readonly List<Vector3> _pickBuffer = new List<Vector3>();

        private int _nextShield;

        /// <summary>Лечение текущей фазы щита, здоровья в секунду.</summary>
        private float _healRate;

        private float _broodTimer;
        private float _shockTimer;
        private float _shockCharge = -1f;
        private float _spitTimer;
        private int _spitLeft;
        private float _spitGapTimer;

        private Vector3 _lastTargetPosition;
        private Vector3 _targetVelocity;

        private Transform _container;
        private Transform _lairHalo;
        private Light _lairLight;
        private Transform _body;
        private Transform _shieldHalo;
        private Transform _shockDisc;

        // Отрисовка — тем же материалом и той же запечённой анимацией, что у толпы.
        private SpiderKind _kind;
        private float _scale = 1f;
        private float _bodyLift = 1f;
        private float _hitRadius = 2f;
        private quaternion _rotation = quaternion.identity;
        private SpiderClip _clip = SpiderClip.Idle;
        private float _phase;

        private readonly Matrix4x4[] _matrix = new Matrix4x4[1];
        private readonly Vector4[] _anim = new Vector4[1];
        private readonly Vector4[] _tint = new Vector4[1];
        private MaterialPropertyBlock _block;

        private static readonly int AnimStateId = Shader.PropertyToID("_AnimState");
        private static readonly int InstanceTintId = Shader.PropertyToID("_InstanceTint");

        private bool _guideDirty;

        private void OnEnable()
        {
            EnsureLinks();

            if (world != null) world.Generated += Rebuild;
        }

        private void OnDisable()
        {
            if (world != null) world.Generated -= Rebuild;
        }

        /// <summary>Достаёт ссылки. Отдельно от OnEnable — см. грабли №17.</summary>
        public void EnsureLinks()
        {
            if (world == null) world = GetComponent<CatacombWorld>();
            if (world == null) world = FindFirstObjectByType<CatacombWorld>();
            if (crowd == null) crowd = FindFirstObjectByType<SpiderCrowd>();
            if (fixtures == null) fixtures = FindFirstObjectByType<CaveFixtures>();
            if (target == null) target = FindFirstObjectByType<CatacombTestRig>();
        }

        /// <summary>Кладёт логово заново. Зовётся по событию генерации мира.</summary>
        public void Rebuild()
        {
            EnsureLinks();
            Clear();

            if (world == null || world.Layout == null) return;

            var rooms = new List<Vector3>();

            for (var level = 0; ; level++)
            {
                var centres = world.GetRoomCenters(level);
                if (centres.Count == 0) break;

                rooms.AddRange(centres);
            }

            if (rooms.Count == 0) return;

            var entry = world.TryGetSpawnPoint(out var spawn) ? spawn : rooms[0];

            rooms.Sort((a, b) => (b - entry).sqrMagnitude.CompareTo((a - entry).sqrMagnitude));

            // Своя соль к сиду мира: логово не должно совпадать с тем, что выбрали
            // лампы и паутина из того же сида.
            var random = new System.Random(world.CurrentSeed ^ 0xB055);
            var pool = Mathf.Max(1, Mathf.RoundToInt(rooms.Count * lairFarShare));
            var room = rooms[random.Next(pool)];

            // На пол, а не в центр зала. Луч здесь допустим: он начат в пустоте зала,
            // а грабли №1 запрещают лучи, начатые в толще породы.
            LairPosition = Physics.Raycast(room, Vector3.down, out var hit, probeDistance, ~0,
                QueryTriggerInteraction.Ignore)
                ? hit.point
                : room;

            HasLair = true;

            var container = new GameObject("Cave Queen");
            container.transform.SetParent(transform, false);

            // Восстанавливается из сида и в сцену не сохраняется — как светильники.
            if (!Application.isPlaying) container.hideFlags = HideFlags.DontSave;

            _container = container.transform;

            BuildLair();

            _guideDirty = true;
            ApplyGuide();
        }

        /// <summary>Убирает логово, Матку, генераторы и плевки.</summary>
        public void Clear()
        {
            State = QueenState.Dormant;
            HasLair = false;
            Health = 0f;
            MaxHealth = 0f;
            ShieldsRaised = 0;
            PylonsLitTotal = 0;
            ShocksLanded = 0;
            SpitsLanded = 0;

            _nextShield = 0;
            _shockCharge = -1f;
            _spitLeft = 0;

            _pylons.Clear();
            _pylonHistory.Clear();
            _litZones.Clear();
            _spits.Clear();

            if (crowd != null) crowd.SetLitZones(null);

            if (_container != null)
            {
                DestroyNow(_container.gameObject);
                _container = null;
            }

            // Контейнер мог пережить перезагрузку домена: поля уже нет, объект есть.
            for (var i = transform.childCount - 1; i >= 0; i--)
            {
                var child = transform.GetChild(i);
                if (child.name == "Cave Queen") DestroyNow(child.gameObject);
            }
        }

        private void BuildLair()
        {
            var lair = new GameObject("Lair");
            lair.transform.SetParent(_container, false);
            lair.transform.position = LairPosition + Vector3.up * 0.6f;

            // Логово видно издалека ореолом, а не светом: источник в темноте занимал бы
            // место из тех 32, что даёт WebGL2, ничего толком не освещая. Свет включается,
            // когда Матка убита, — это и есть награда.
            // Только ореол, без ядра. Красный гладкий шар под Маткой читался в кадре
            // как пластиковый мяч или яйцо — ровно то, за что выбросили коконы
            // (грабли №9). «Точка плюс ореол» — то, что в этих катакомбах работает.
            var halo = CaveGlowBillboard.Attach(lair.transform, 3.2f, lairColor);
            _lairHalo = halo != null ? halo.transform : null;

            _lairLight = lair.AddComponent<Light>();
            _lairLight.type = LightType.Point;
            _lairLight.range = 30f;
            _lairLight.intensity = 42f;
            _lairLight.color = pylonLitColor;
            _lairLight.shadows = LightShadows.None;
            _lairLight.enabled = false;

            var body = new GameObject("Queen");
            body.transform.SetParent(_container, false);
            body.transform.position = LairPosition;
            _body = body.transform;

            var shield = CaveGlowBillboard.Attach(_body, 1f, shieldColor);
            _shieldHalo = shield != null ? shield.transform : null;
            if (_shieldHalo != null) _shieldHalo.gameObject.SetActive(false);

            // Пятно замаха: плоский ореол на полу, растущий до радиуса волны.
            // У Megabonk удар тоже предупреждён красным на земле — без этого волна
            // читается как случайный толчок, а не как атака, от которой можно уйти.
            var disc = new GameObject("Shock");
            disc.transform.SetParent(_container, false);
            disc.transform.position = LairPosition + Vector3.up * 0.15f;
            disc.transform.rotation = Quaternion.Euler(90f, 0f, 0f);

            CaveGlowBillboard.Attach(disc.transform, 1f, lairColor, false);

            _shockDisc = disc.transform;
            _shockDisc.gameObject.SetActive(false);
        }

        private void Update()
        {
            if (!Application.isPlaying) return;

            Tick(Time.deltaTime);
            Draw();
        }

        /// <summary>
        /// Будит Матку. Зовётся директором: живучесть зависит от того, КОГДА — это
        /// знает он, а не она.
        /// </summary>
        public bool TrySummon(Vector3 playerPosition, float healthScale)
        {
            if (State != QueenState.Dormant || !HasLair) return false;
            if ((playerPosition - LairPosition).magnitude > useRange + 2f) return false;

            EnsureLinks();

            if (!PrepareBody())
            {
                Debug.LogWarning($"{nameof(CaveQueen)}: у толпы нет ни одного вида — Матку рисовать нечем.", this);
            }

            MaxHealth = math.max(1f, baseHealth * healthScale);
            Health = MaxHealth;
            State = QueenState.Awake;

            // Первые секунды без атак: пробуждение должно читаться, а не сразу бить в лицо.
            _broodTimer = 2f;
            _shockTimer = 5f;
            _spitTimer = 3.5f;

            if (target != null) _lastTargetPosition = target.BodyCentre;

            Announce($"МАТКА ПРОБУДИЛАСЬ — {MaxHealth:0} попаданий");
            return true;
        }

        /// <summary>
        /// Удар по Матке. Сфера, как у толпы: оружие площадное.
        /// </summary>
        /// <returns>Попало ли. Под щитом попадает, но урона не наносит.</returns>
        public bool DamageAt(Vector3 point, float radius, float damage)
        {
            if (State != QueenState.Awake && State != QueenState.Shielded) return false;
            if ((point - Centre).magnitude > radius + _hitRadius) return false;

            if (State == QueenState.Shielded) return true;

            Health -= damage;

            // Порог перепрыгнуть нельзя: здоровье упирается в него, и щит встаёт.
            // Иначе мощное попадание с семидесяти процентов в минус проскакивало бы
            // обе фазы генераторов разом, а они и есть главная часть боя.
            if (_nextShield < shieldAt.Length && Health <= MaxHealth * shieldAt[_nextShield])
            {
                Health = MaxHealth * shieldAt[_nextShield];
                _nextShield++;

                EnterShield();
                return true;
            }

            if (Health <= 0f) Die();

            return true;
        }

        /// <summary>
        /// Шаг боя. Отдельно от Update, чтобы прогон мог гонять бой без play-режима.
        /// </summary>
        public void Tick(float deltaTime)
        {
            if (deltaTime <= 0f) return;

            if (_guideDirty) ApplyGuide();

            AnimateLair(deltaTime);
            UpdateSpits(deltaTime);

            if (State == QueenState.Dormant) return;

            AdvanceClip(deltaTime);

            if (State == QueenState.Dead) return;

            var player = target;
            var engaged = false;
            var toPlayer = Vector3.zero;

            if (player != null)
            {
                var body = player.BodyCentre;

                _targetVelocity = (body - _lastTargetPosition) / deltaTime;
                _lastTargetPosition = body;

                toPlayer = body - Centre;
                engaged = toPlayer.magnitude <= engageRange;
            }

            if (engaged) Face(toPlayer, deltaTime);

            if (State == QueenState.Shielded)
            {
                Health = math.min(MaxHealth, Health + _healRate * deltaTime);
                UpdatePylons(deltaTime);
            }

            if (State == QueenState.Dead || !engaged) return;

            var shielded = State == QueenState.Shielded;

            _broodTimer -= deltaTime;

            if (_broodTimer <= 0f)
            {
                _broodTimer = shielded ? broodShieldInterval : broodInterval;

                // Выводок добирает свиту до потолка, а не сыплется сверх него. Без потолка
                // он ложится поверх цели директора, и за полминуты у логова вырастала
                // сплошная стена хитина — кадр так и показал.
                if (crowd != null)
                {
                    var room = broodCap - crowd.CountNear(LairPosition, broodCapRadius);
                    var count = math.min(broodCount, room);

                    if (count > 0) crowd.SpawnAround(LairPosition + Vector3.up, count, broodSteps, 2);
                }
            }

            UpdateShock(deltaTime, shielded);
            UpdateSpitVolley(deltaTime);
        }

        /// <summary>Доворот на игрока по горизонтали. Медленный: от Матки бегают кругами.</summary>
        private void Face(Vector3 toPlayer, float deltaTime)
        {
            var flat = new Vector3(toPlayer.x, 0f, toPlayer.z);
            if (flat.sqrMagnitude < 1e-4f) return;

            var facing = _kind != null && _kind.FacesMinusZ ? -1f : 1f;
            var wanted = quaternion.LookRotationSafe(flat.normalized * facing, math.up());

            var dot = math.abs(math.dot(_rotation.value, wanted.value));
            var angle = 2f * math.acos(math.clamp(dot, -1f, 1f));
            var step = math.radians(turnSpeed) * deltaTime;

            _rotation = angle <= step ? wanted : math.slerp(_rotation, wanted, step / angle);

            // Поворачивается — перебирает лапами. Стоящая на месте туша, скользящая
            // по кругу, читалась бы как статуя на поворотном столе.
            if (_clip != SpiderClip.Attack) Play(angle > math.radians(8f) ? SpiderClip.Walk : SpiderClip.Idle);
        }

        private void UpdateShock(float deltaTime, bool shielded)
        {
            var reach = shockRadius * (shielded ? shockShieldReach : 1f);

            if (_shockCharge >= 0f)
            {
                _shockCharge += deltaTime;

                var progress = math.saturate(_shockCharge / math.max(0.05f, shockWindup));

                if (_shockDisc != null) _shockDisc.localScale = Vector3.one * (reach * 2f * progress);

                if (progress < 1f) return;

                _shockCharge = -1f;
                if (_shockDisc != null) _shockDisc.gameObject.SetActive(false);

                FireShock(reach);
                return;
            }

            _shockTimer -= deltaTime;
            if (_shockTimer > 0f) return;

            _shockTimer = shielded ? shockShieldInterval : shockInterval;
            _shockCharge = 0f;

            Play(SpiderClip.Attack);

            if (_shockDisc != null)
            {
                _shockDisc.localScale = Vector3.zero;
                _shockDisc.gameObject.SetActive(true);
            }
        }

        /// <summary>
        /// Волна: отбрасывает игрока прочь по горизонтали и чуть вверх. Сквозь породу
        /// не бьёт — за углом коридора от неё можно укрыться.
        /// </summary>
        private void FireShock(float reach)
        {
            if (target == null) return;

            var body = target.BodyCentre;
            var delta = body - Centre;
            var distance = delta.magnitude;

            if (distance > reach || Blocked(Centre, body)) return;

            var flat = new Vector3(delta.x, 0f, delta.z);
            var direction = flat.sqrMagnitude > 1e-4f ? flat.normalized : Vector3.forward;

            // У края слабее: вплотную волна швыряет, на излёте толкает.
            var strength = 1f - 0.5f * distance / reach;

            target.Push(direction * (shockPush * strength) + Vector3.up * shockLift);
            ShocksLanded++;
        }

        private void UpdateSpitVolley(float deltaTime)
        {
            if (target == null) return;

            if (_spitLeft > 0)
            {
                _spitGapTimer -= deltaTime;
                if (_spitGapTimer > 0f) return;

                _spitGapTimer = spitGap;
                _spitLeft--;

                FireSpit();
                return;
            }

            _spitTimer -= deltaTime;
            if (_spitTimer > 0f) return;

            _spitTimer = spitInterval;

            var body = target.BodyCentre;
            if ((body - Centre).magnitude > spitRange || Blocked(Head, body)) return;

            _spitLeft = spitVolley;
            _spitGapTimer = 0f;

            if (_clip != SpiderClip.Attack) Play(SpiderClip.Attack);
        }

        /// <summary>
        /// Плевок с упреждением: целится туда, где игрок будет, а не где он есть.
        /// Без упреждения залп бьёт в спину бегущему мимо — то есть не попадает никогда,
        /// и атака читается как фейерверк.
        /// </summary>
        private void FireSpit()
        {
            var from = Head;
            var body = target.BodyCentre;

            var flight = (body - from).magnitude / math.max(0.1f, spitSpeed);
            var aim = body + _targetVelocity * flight;

            var spit = new Spit
            {
                Position = from,
                Velocity = (aim - from).normalized * spitSpeed,
                Life = spitRange / math.max(0.1f, spitSpeed) + 0.5f
            };

            if (_container != null)
            {
                var view = new GameObject("Spit");
                view.transform.SetParent(_container, false);
                view.transform.position = from;

                CaveGlowBillboard.Attach(view.transform, 0.9f, spitColor);
                spit.View = view;
            }

            _spits.Add(spit);
        }

        private void UpdateSpits(float deltaTime)
        {
            for (var i = _spits.Count - 1; i >= 0; i--)
            {
                var spit = _spits[i];

                spit.Position += spit.Velocity * deltaTime;
                spit.Life -= deltaTime;

                if (spit.View != null) spit.View.transform.position = spit.Position;

                var hit = false;

                if (target != null)
                {
                    target.GetBodySegment(out var low, out var high, out var radius);

                    if (DistanceToSegment(spit.Position, low, high) <= radius + 0.35f)
                    {
                        target.Entangle(entangleSeconds, entangleScale);
                        SpitsLanded++;
                        hit = true;
                    }
                }

                // Порода по полю плотности, а не физикой: плевок в толще камня
                // физика не заметила бы вовсе (грабли №1).
                var dead = hit || spit.Life <= 0f || (world != null && world.IsSolid(spit.Position));

                if (!dead) continue;

                if (spit.View != null) DestroyNow(spit.View);
                _spits.RemoveAt(i);
            }
        }

        /// <summary>
        /// Щит: Матка неуязвима и лечится, вокруг неё встают генераторы.
        ///
        /// Генераторы ставятся по ходам, а не по кругу: заливка от Матки берёт клетки
        /// пола в восьми — двадцати шести шагах, и среди них выбираются разнесённые
        /// дальше всего друг от друга и от прошлых генераторов. Пилон за стеной, близкий
        /// по прямой и далёкий по пути, стоил бы полминуты обхода, а минута на всё.
        /// </summary>
        private void EnterShield()
        {
            State = QueenState.Shielded;
            ShieldsRaised++;

            _healRate = (MaxHealth - Health) / math.max(1f, healFullSeconds);

            _pylons.Clear();

            if (crowd != null)
            {
                crowd.PickFloorPoints(LairPosition + Vector3.up, pylonCount, pylonSteps.x, pylonSteps.y,
                    _pylonHistory, _pickBuffer);
            }
            else
            {
                _pickBuffer.Clear();
            }

            if (_pickBuffer.Count == 0)
            {
                // Без сетки навигации ставить генераторы некуда — а щит, который нечем
                // снять, это проигрыш на ровном месте. Пропускаем фазу.
                Debug.LogWarning($"{nameof(CaveQueen)}: генераторам не нашлось места — фаза щита пропущена.", this);
                State = QueenState.Awake;
                return;
            }

            foreach (var point in _pickBuffer)
            {
                _pylons.Add(SpawnPylon(point));
                _pylonHistory.Add(point);
            }

            if (_shieldHalo != null) _shieldHalo.gameObject.SetActive(true);

            // Под щитом волна и выводок сразу, а не по старому таймеру: фаза длится
            // меньше минуты, и первые пятнадцать секунд тишины превращали бы её в прогулку.
            _shockTimer = math.min(_shockTimer, 1.5f);
            _broodTimer = math.min(_broodTimer, 1f);

            Announce($"ЩИТ: Матка лечится — запустите генераторы (0/{_pylons.Count})");
        }

        private void UpdatePylons(float deltaTime)
        {
            var player = target;
            var lit = 0;

            foreach (var pylon in _pylons)
            {
                if (pylon.Lit)
                {
                    lit++;
                    continue;
                }

                var near = false;

                if (player != null)
                {
                    var feet = player.BodyCentre;
                    var flat = new Vector2(feet.x - pylon.Position.x, feet.z - pylon.Position.z);

                    near = flat.magnitude <= pylonRadius && math.abs(feet.y - pylon.Position.y) <= 3f;
                }

                pylon.Charge = near
                    ? math.min(1f, pylon.Charge + deltaTime / math.max(0.1f, pylonCharge))
                    : math.max(0f, pylon.Charge - pylonDecay * deltaTime);

                ShowPylon(pylon);

                if (pylon.Charge < 1f) continue;

                IgnitePylon(pylon);
                lit++;

                Announce($"Генератор запущен ({lit}/{_pylons.Count})");
            }

            if (lit < _pylons.Count) return;

            State = QueenState.Awake;

            if (_shieldHalo != null) _shieldHalo.gameObject.SetActive(false);

            // Передышка после снятого щита — окно для урона. У Megabonk после пилонов
            // босс ненадолго открыт; без окна игрок тратил бы его на уворот от волны.
            _shockTimer = math.max(_shockTimer, 3f);
            _spitTimer = math.max(_spitTimer, 3f);

            Announce("ЩИТ СПАЛ — бейте Матку");
        }

        /// <summary>
        /// Запущенный генератор — свет навсегда. Освещает кусок логова, отпугивает
        /// толпу рядом и запрещает там пополнение: тот же свет, что был у генератора
        /// прежней игры, только теперь его зажигают посреди боя.
        /// </summary>
        private void IgnitePylon(Pylon pylon)
        {
            pylon.Lit = true;
            pylon.Charge = 1f;
            PylonsLitTotal++;

            if (pylon.Light != null)
            {
                pylon.Light.enabled = true;
                pylon.Light.intensity = pylonIntensity;
            }

            if (pylon.Core != null) pylon.Core.sharedMaterial = CoreMaterial(pylonLitColor);
            if (pylon.Halo != null) pylon.Halo.localScale = Vector3.one * (2.4f * 1.25f);

            if (fixtures != null) fixtures.SetLit(pylon.Position, pylonLitRadius, true);

            _litZones.Add(new Vector4(pylon.Position.x, pylon.Position.y, pylon.Position.z, pylonLitRadius));

            if (crowd != null)
            {
                crowd.SetLitZones(_litZones);
                crowd.Panic(pylon.Position, pylonLitRadius, 4f);
            }
        }

        private void ShowPylon(Pylon pylon)
        {
            // Свет растёт вместе с зарядом: сколько осталось стоять, игрок видит
            // по самому генератору, не глядя в интерфейс. Включён только пока
            // заряжается — погашенные источники на WebGL2 тоже занимали бы лимит.
            if (pylon.Light != null)
            {
                pylon.Light.enabled = pylon.Charge > 0.01f;
                pylon.Light.intensity = pylonIntensity * math.lerp(0.12f, 0.8f, pylon.Charge);
            }

            if (pylon.Halo != null)
            {
                var beat = 0.85f + 0.15f * math.sin(Time.time * (2f + 10f * pylon.Charge));
                pylon.Halo.localScale = Vector3.one * (2.4f * math.lerp(0.42f, 1.7f, pylon.Charge) * beat);
            }
        }

        private Pylon SpawnPylon(Vector3 position)
        {
            var root = new GameObject("Generator");
            root.transform.SetParent(_container, false);
            root.transform.position = position + Vector3.up * 0.6f;

            var light = root.AddComponent<Light>();
            light.type = LightType.Point;
            light.range = pylonRange;
            light.intensity = pylonIntensity * 0.12f;
            light.color = pylonLitColor;
            light.shadows = LightShadows.None;
            light.enabled = false;

            var halo = CaveGlowBillboard.Attach(root.transform, 2.4f, pylonLitColor);

            // Ядро — точка, а не шар: неосвещаемый шар вблизи читается плоским диском.
            // В кадре с четырёх юнитов ядро в полъюнита заняло пол-экрана синим кругом,
            // а зарядка идёт как раз вплотную. Точка плюс ореол — грабли №9.
            var core = SpawnCore(root.transform, 0.16f, pylonDarkColor);

            var pylon = new Pylon
            {
                Position = position,
                Light = light,
                Halo = halo != null ? halo.transform : null,
                Core = core
            };

            ShowPylon(pylon);
            return pylon;
        }

        private void Die()
        {
            State = QueenState.Dead;
            Health = 0f;

            Play(SpiderClip.Dead);

            _shockCharge = -1f;
            _spitLeft = 0;

            if (_shockDisc != null) _shockDisc.gameObject.SetActive(false);
            if (_shieldHalo != null) _shieldHalo.gameObject.SetActive(false);

            foreach (var spit in _spits)
            {
                if (spit.View != null) DestroyNow(spit.View);
            }

            _spits.Clear();

            if (_lairLight != null) _lairLight.enabled = true;

            // Жилам вести больше некуда.
            _guideDirty = true;

            Died?.Invoke(Centre);
        }

        private void AnimateLair(float deltaTime)
        {
            if (_lairHalo == null) return;

            // Спящее логово дышит, как дышал погашенный генератор: в темноте его
            // находят по этому ореолу. Проснувшаяся Матка ореол гасит — она видна сама.
            var size = State switch
            {
                QueenState.Dormant => 3.2f * (0.8f + 0.2f * math.sin(Time.time * 1.1f)),
                QueenState.Dead => 3.2f * 1.3f,
                _ => 0f
            };

            _lairHalo.localScale = Vector3.one * size;

            if (_body == null) return;

            _body.position = Centre;

            if (_shieldHalo != null && State == QueenState.Shielded)
            {
                _shieldHalo.localScale = Vector3.one * (_hitRadius * 2.4f * (0.92f + 0.08f * math.sin(Time.time * 5f)));
            }
        }

        // ------------------------------------------------------------------ отрисовка

        /// <summary>
        /// Берёт вид для отрисовки — самый живучий вид толпы, увеличенный.
        /// Отдельного меша у Матки нет: пак не даёт королевы, а запечённая анимация
        /// самого крупного паука, растянутая в полтора раза, читается как она.
        /// </summary>
        private bool PrepareBody()
        {
            _kind = crowd != null ? crowd.KindAt(crowd.HeaviestKind) : null;
            if (_kind == null) return false;

            _scale = _kind.Scale * sizeScale;
            _bodyLift = math.max(0.5f, _kind.RestBounds.center.y * _scale + _kind.Hover * _scale);
            _hitRadius = math.max(1f, _kind.RestBounds.extents.magnitude * _scale * 0.7f);

            _block ??= new MaterialPropertyBlock();
            // Четвёртый канал — возраст разлёта на куски (CaveCrowd, Shatter): ноль — цела.
            _tint[0] = new Vector4(hue.r, hue.g, hue.b, 0f);

            return true;
        }

        private void Play(SpiderClip clip)
        {
            if (_clip == clip) return;

            _clip = clip;
            _phase = 0f;
        }

        private void AdvanceClip(float deltaTime)
        {
            if (_kind == null) return;

            var range = _kind.GetClip(_clip);
            _phase += deltaTime / math.max(0.05f, range.Length);

            if (range.Loop)
            {
                _phase = math.frac(_phase);
                return;
            }

            if (_phase < 1f) return;

            // Удар доигран — обратно в стояние; смерть застывает на последнем кадре.
            if (_clip == SpiderClip.Attack) Play(SpiderClip.Idle);
            else _phase = 1f;
        }

        private void Draw()
        {
            if (_kind == null || State == QueenState.Dormant || _kind.Material == null) return;

            var range = _kind.GetClip(_clip);

            _matrix[0] = Matrix4x4.TRS(LairPosition + Vector3.up * (_kind.Hover * _scale),
                _rotation, Vector3.one * _scale);

            _anim[0] = new Vector4(range.StartRow, range.FrameCount, _phase, range.Loop ? 1f : 0f);

            _block.SetVectorArray(AnimStateId, _anim);
            _block.SetVectorArray(InstanceTintId, _tint);

            var parameters = new RenderParams(_kind.Material)
            {
                worldBounds = new Bounds(Centre, Vector3.one * (_hitRadius * 4f)),
                shadowCastingMode = ShadowCastingMode.On,
                receiveShadows = true,
                layer = gameObject.layer,
                renderingLayerMask = 1,
                lightProbeUsage = LightProbeUsage.Off,
                reflectionProbeUsage = ReflectionProbeUsage.Off,
                matProps = _block
            };

            Graphics.RenderMeshInstanced(parameters, _kind.Mesh, 0, _matrix, 1);
        }

        // ------------------------------------------------------------------ помощники

        /// <summary>Откуда летит плевок: перед головой, на высоте тела.</summary>
        private Vector3 Head
        {
            get
            {
                var facing = _kind != null && _kind.FacesMinusZ ? -1f : 1f;
                var forward = (Vector3)math.forward(_rotation) * facing;

                return Centre + forward * (_hitRadius * 0.6f) + Vector3.up * 0.4f;
            }
        }

        /// <summary>
        /// Перекрыт ли путь породой. Луч начат в теле Матки, то есть в пустоте логова,
        /// и обрывается, не доходя до игрока: иначе он упирался бы в капсулу самого игрока.
        /// </summary>
        private static bool Blocked(Vector3 from, Vector3 to)
        {
            var delta = to - from;
            var distance = delta.magnitude;

            if (distance < 1f) return false;

            return Physics.Raycast(from, delta / distance, distance - 0.8f, ~0, QueryTriggerInteraction.Ignore);
        }

        private static float DistanceToSegment(Vector3 point, Vector3 a, Vector3 b)
        {
            var ab = b - a;
            var t = ab.sqrMagnitude > 1e-6f ? Mathf.Clamp01(Vector3.Dot(point - a, ab) / ab.sqrMagnitude) : 0f;

            return (point - (a + ab * t)).magnitude;
        }

        private void ApplyGuide()
        {
            if (fixtures == null || !HasLair) return;

            // Жилы тлеют ярче к логову. Пока Матка жива — ведут к ней; убита —
            // вести больше некуда.
            fixtures.ApplyGuide(State == QueenState.Dead ? Array.Empty<Vector3>() : new[] { LairPosition },
                guideReach);

            _guideDirty = false;
        }

        private void Announce(string text) => Announced?.Invoke(text);

        /// <summary>Строка для наложения в стенде.</summary>
        public string Describe(Vector3 playerPosition)
        {
            if (!HasLair) return "Логова нет — уровень без залов?";

            switch (State)
            {
                case QueenState.Dormant:
                    var distance = (playerPosition - LairPosition).magnitude;

                    return distance <= useRange + 2f
                        ? "<color=orange>Логово Матки рядом — E, чтобы разбудить</color>"
                        : $"Логово Матки: <b>{distance:0}</b> юнитов";

                case QueenState.Dead:
                    return "<color=lime>Матка убита</color>";
            }

            var bar = Bar(HealthFraction);
            var line = $"МАТКА {bar} {Health:0}/{MaxHealth:0}";

            if (State != QueenState.Shielded) return line;

            Pylon nearest = null;
            var nearestDistance = float.MaxValue;

            foreach (var pylon in _pylons)
            {
                if (pylon.Lit) continue;

                var d = (pylon.Position - playerPosition).magnitude;
                if (d >= nearestDistance) continue;

                nearest = pylon;
                nearestDistance = d;
            }

            var hint = nearest != null
                ? $", ближайший в {nearestDistance:0} ю, заряд {nearest.Charge:P0}"
                : "";

            return $"<color=#7fb2ff>{line} ЩИТ</color>   генераторы {PylonsLitInPhase}/{_pylons.Count}{hint}";
        }

        private static string Bar(float fraction)
        {
            const int cells = 20;
            var filled = Mathf.Clamp(Mathf.RoundToInt(fraction * cells), 0, cells);

            return "[" + new string('#', filled) + new string('.', cells - filled) + "]";
        }

        private static readonly Dictionary<Color, Material> CoreMaterials = new Dictionary<Color, Material>();

        private static Mesh _sphere;

        private static Renderer SpawnCore(Transform parent, float size, Color color)
        {
            var core = new GameObject("Core");
            core.transform.SetParent(parent, false);
            core.transform.localScale = Vector3.one * size;

            // Встроенный меш, а не CreatePrimitive: у примитива коллайдер, и он перехватывал
            // бы лучи взрыва и проверку, не перекрыта ли волна (грабли №5).
            core.AddComponent<MeshFilter>().sharedMesh =
                _sphere != null ? _sphere : _sphere = Resources.GetBuiltinResource<Mesh>("Sphere.fbx");

            var view = core.AddComponent<MeshRenderer>();
            view.sharedMaterial = CoreMaterial(color);
            view.shadowCastingMode = ShadowCastingMode.Off;
            view.receiveShadows = false;

            return view;
        }

        /// <summary>Материал ядра, по одному на цвет — ключ обязателен (грабли №2).</summary>
        private static Material CoreMaterial(Color color)
        {
            if (CoreMaterials.TryGetValue(color, out var cached) && cached != null) return cached;

            var material = CaveMaterials.UnlitOpaque("Cave Queen Core", color);

            CoreMaterials[color] = material;
            return material;
        }

        private static void DestroyNow(UnityEngine.Object target)
        {
            if (target == null) return;

            if (Application.isPlaying) Destroy(target);
            else DestroyImmediate(target);
        }
    }
}
