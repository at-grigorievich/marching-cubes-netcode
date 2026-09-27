using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace MineGenerator.Catacombs
{
    /// <summary>
    /// Директор забега — ядро игры по схеме Megabonk.
    ///
    /// Стадия идёт по таймеру. Давление орды растёт по кривой с двумя гребнями
    /// (<see cref="RunCurve"/>), на гребнях приходят рои одного вида, рядом с ними —
    /// элита. Где-то в катакомбах лежит логово Матки: его можно найти и разбудить
    /// в любой момент, и чем раньше, тем она слабее. Не успел до конца таймера —
    /// начинается финальный рой, который только крепнет. Убил Матку — катакомбы
    /// загораются, орда разбегается, стадия пройдена.
    ///
    /// Разложение по времени взято из разборов Megabonk (стадия десять минут):
    /// мини-босс 3:00, рой 4:00, рой 7:00, мини-босс 8:00, на 10:00 финальный рой.
    /// Здесь стадия короче — семь минут под веб, — а события стоят на тех же ДОЛЯХ.
    ///
    /// Директор ничего не рисует и никого не двигает: он раз в кадр пишет толпе
    /// директиву (<see cref="CrowdDirective"/>) — сколько держать, как быстро досылать,
    /// кого заводить. Как особи бегут, по-прежнему решает сама толпа.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CaveRunDirector : MonoBehaviour
    {
        [SerializeField] private CatacombWorld world;
        [SerializeField] private SpiderCrowd crowd;
        [SerializeField] private CaveFixtures fixtures;
        [SerializeField] private CaveQueen queen;

        [Header("Стадия")]
        /// <summary>
        /// Длина стадии до финального роя.
        ///
        /// Семь минут, а не десять, как у Megabonk: забег в браузере должен укладываться
        /// в пять-семь минут, иначе его не доигрывают. События стоят в долях стадии,
        /// поэтому при правке длины расписание сдвигается вместе с ней.
        /// </summary>
        [Tooltip("Секунд до финального роя.")]
        [SerializeField, Range(60f, 1200f)] private float stageSeconds = 420f;

        [SerializeField] private RunCurve curve = new RunCurve();

        [Header("Население")]
        /// <summary>
        /// Население при давлении, равном единице. Кривая даёт от 0.65 в затишье до 2.3
        /// на втором гребне, то есть от полутора сотен до шести сотен, плюс рябь толпы
        /// поверх и рой на гребне — это около тысячи в самый тяжёлый момент стадии,
        /// где-то у потолка читаемости, замеренного по кадрам.
        /// </summary>
        [Tooltip("Сколько особей держать при давлении, равном единице.")]
        [SerializeField, Range(20, 1500)] private int basePopulation = 260;

        [Tooltip("Досыл в секунду при давлении, равном единице. Досыл реактивный: убиваешь " +
                 "быстрее — приходят быстрее, как в Megabonk.")]
        [SerializeField, Min(0f)] private float baseSpawnRate = 30f;

        [Tooltip("Размах коротких волн толпы поверх кривой. Единица — без ряби.")]
        [SerializeField, Range(1f, 3f)] private float microWave = 1.4f;

        /// <summary>
        /// Рост живучести к концу стадии. Скромный намеренно: в Megabonk враги крепнут
        /// в разы, но и игрок растёт вместе с ними. Здесь роста игрока пока нет вовсе,
        /// и крепкая орда при неизменном оружии означала бы просто проигрыш по таймеру.
        /// </summary>
        [Tooltip("На сколько вырастает живучесть к концу стадии. 0.5 — в полтора раза.")]
        [SerializeField, Range(0f, 3f)] private float healthGrowth = 0.5f;

        [Tooltip("На сколько вырастает скорость к концу стадии.")]
        [SerializeField, Range(0f, 1f)] private float speedGrowth = 0.15f;

        [Tooltip("К какой доле стадии открываются все виды. До этого тяжёлые приходят по одному.")]
        [SerializeField, Range(0.1f, 1f)] private float heavyUnlock = 0.6f;

        [Header("Рой")]
        [Tooltip("Сколько длится рой, секунды. Стоит серединой на гребне кривой.")]
        [SerializeField, Range(5f, 120f)] private float swarmSeconds = 35f;

        [Tooltip("Сколько особей рой добавляет сверх кривой.")]
        [SerializeField, Range(0, 1500)] private int swarmExtra = 220;

        [Tooltip("Досыл роя в секунду сверх кривой.")]
        [SerializeField, Min(0f)] private float swarmRate = 60f;

        [Tooltip("Какая доля новых особей во время роя — его вида.")]
        [SerializeField, Range(0f, 1f)] private float swarmShare = 0.85f;

        [Header("Элита")]
        [Tooltip("Когда приходит первая элита, доля стадии. У Megabonk — 3:00 из 10.")]
        [SerializeField, Range(0f, 1f)] private float firstElite = 0.3f;

        [Tooltip("Когда приходит вторая элита, доля стадии. У Megabonk — 8:00 из 10.")]
        [SerializeField, Range(0f, 1f)] private float secondElite = 0.8f;

        [SerializeField, Range(1f, 2.5f)] private float eliteSize = 1.45f;

        [Tooltip("Во сколько раз элита живучее самого живучего вида.")]
        [SerializeField, Range(1f, 60f)] private float eliteHealth = 12f;

        [SerializeField, Range(0.5f, 2f)] private float eliteSpeed = 1.1f;

        [Tooltip("Окраска элиты — множитель альбедо, ржавый.")]
        [SerializeField] private Color eliteHue = new Color(1.35f, 0.72f, 0.6f);

        [Header("Финальный рой")]
        [Tooltip("За сколько секунд финальный рой выходит на потолок особей.")]
        [SerializeField, Range(1f, 120f)] private float finalRamp = 30f;

        [SerializeField, Min(0f)] private float finalSpawnRate = 120f;

        /// <summary>
        /// Ближний круг в финальном рое — если у толпы он вообще ограничен.
        ///
        /// Задумывался главным отличием финала: весь забег толпа держала у игрока не больше
        /// семи десятков, а в финальном рое переставала ждать. Потолок пользователь отменил
        /// («остальные айдлят вдалеке»), толпа теперь не ждёт никогда, и по умолчанию
        /// эта ручка ничего не делает. Оставлена на случай, если потолок вернут.
        /// </summary>
        [Tooltip("До скольких особей растёт ближний круг в финальном рое.")]
        [SerializeField, Range(0, 1500)] private int finalNearCap = 400;

        [SerializeField, Range(1f, 300f)] private float finalNearRamp = 60f;

        [Tooltip("Прибавка скорости за каждую минуту финального роя.")]
        [SerializeField, Range(0f, 1f)] private float finalSpeedPerMinute = 0.12f;

        [SerializeField, Range(1f, 3f)] private float finalSpeedMax = 1.8f;

        /// <summary>
        /// Ступени финального роя, секунды от его начала. У Megabonk призраки
        /// темнеют через минуту и краснеют через шесть; здесь вторая ступень ближе,
        /// потому что вся стадия короче.
        /// </summary>
        [Tooltip("Через сколько секунд финального роя вторая и третья ступень.")]
        [SerializeField] private Vector2 finalTiers = new Vector2(60f, 180f);

        [Tooltip("Прибавка живучести за каждую ступень финального роя.")]
        [SerializeField, Range(0f, 5f)] private float finalHealthPerTier = 1f;

        [SerializeField] private Color finalHueSecond = new Color(0.5f, 0.48f, 0.58f);
        [SerializeField] private Color finalHueThird = new Color(1.35f, 0.45f, 0.4f);

        [Header("Матка")]
        /// <summary>
        /// Насколько Матка крепнет, если будить её позже. В Megabonk босс первой стадии
        /// растёт примерно вчетверо за десять минут — это и есть выбор, который стоит
        /// перед игроком: идти к логову сразу или сперва набрать силу.
        /// </summary>
        [Tooltip("Живучесть Матки: база × (1 + рост × доля стадии). 3 — вчетверо к концу стадии.")]
        [SerializeField, Range(0f, 10f)] private float queenGrowth = 3f;

        [Tooltip("Радиус паники при смерти Матки.")]
        [SerializeField, Range(10f, 200f)] private float victoryPanicRadius = 80f;

        [SerializeField, Range(1f, 30f)] private float victoryPanicTime = 12f;

        [Tooltip("Сколько секунд держится объявление на экране.")]
        [SerializeField, Range(1f, 10f)] private float bannerSeconds = 4f;

        public enum RunPhase
        {
            /// <summary>Таймер идёт, давление растёт по кривой.</summary>
            Stage,

            /// <summary>Таймер кончился, Матка жива: финальный рой.</summary>
            FinalSwarm,

            /// <summary>Матка убита.</summary>
            Victory
        }

        public RunPhase Phase { get; private set; }

        /// <summary>Секунд с начала стадии. В финальном рое продолжает идти.</summary>
        public float Elapsed { get; private set; }

        public float StageSeconds => stageSeconds;
        public RunCurve Curve => curve;

        /// <summary>Доля стадии. Больше единицы — финальный рой.</summary>
        public float Progress => Elapsed / math.max(1f, stageSeconds);

        /// <summary>Давление прямо сейчас — по кривой, без роя и финала.</summary>
        public float Intensity => curve.Intensity(math.min(1f, Progress));

        /// <summary>Идёт ли рой, и какого вида.</summary>
        public bool SwarmActive => _swarmLeft > 0f;

        public int SwarmKind { get; private set; } = -1;

        /// <summary>Сколько роёв и элит пришло за стадию. Для проверки расписания.</summary>
        public int SwarmsFired { get; private set; }

        public int ElitesSpawned { get; private set; }

        /// <summary>Живые элиты, ссылками. Директор сам вычищает мёртвых.</summary>
        public IReadOnlyList<SpiderHandle> Elites => _elites;

        /// <summary>Последнее объявление и сколько ему ещё висеть.</summary>
        public string Banner { get; private set; } = "";

        public float BannerLeft { get; private set; }

        /// <summary>Объявления стадии — для интерфейса и звука.</summary>
        public event Action<string> Announced;

        public CaveQueen Queen => queen;

        private float _swarmLeft;
        private readonly bool[] _swarmFired = new bool[2];
        private readonly bool[] _eliteDue = new bool[2];
        private int _elitePending;
        private readonly List<SpiderHandle> _elites = new List<SpiderHandle>();

        private int _minKindHealth = 1;
        private int _maxKindHealth = 1;

        private System.Random _random = new System.Random(1);

        /// <summary>На какую Матку подписаны. Подписка идёт из EnsureLinks, а не из OnEnable.</summary>
        private CaveQueen _boundQueen;

        private void OnEnable()
        {
            EnsureLinks();

            if (world != null) world.Generated += Restart;

            // Уровень мог быть собран до того, как компонент включили.
            if (world != null && world.Layout != null && !world.IsGenerating) Restart();
        }

        private void OnDisable()
        {
            if (world != null) world.Generated -= Restart;

            Unbind();

            if (crowd != null) crowd.ClearDirective();
        }

        /// <summary>
        /// Достаёт ссылки и подписывается на Матку. Отдельно от OnEnable: в режиме
        /// редактирования он у обычного MonoBehaviour не вызывается (грабли №17),
        /// а прогоны работают именно там — и без подписки прогон не увидел бы смерти
        /// Матки, то есть победы.
        /// </summary>
        public void EnsureLinks()
        {
            if (world == null) world = GetComponent<CatacombWorld>();
            if (world == null) world = FindFirstObjectByType<CatacombWorld>();
            if (crowd == null) crowd = FindFirstObjectByType<SpiderCrowd>();
            if (fixtures == null) fixtures = FindFirstObjectByType<CaveFixtures>();
            if (queen == null) queen = FindFirstObjectByType<CaveQueen>();

            if (queen == _boundQueen) return;

            Unbind();

            if (queen == null) return;

            queen.Announced += Announce;
            queen.Died += OnQueenDied;
            _boundQueen = queen;
        }

        private void Unbind()
        {
            if (_boundQueen == null) return;

            _boundQueen.Announced -= Announce;
            _boundQueen.Died -= OnQueenDied;
            _boundQueen = null;
        }

        private void Update()
        {
            if (!Application.isPlaying) return;

            Tick(Time.deltaTime);
        }

        /// <summary>Начинает стадию заново. Зовётся по событию генерации мира.</summary>
        public void Restart()
        {
            EnsureLinks();

            Phase = RunPhase.Stage;
            Elapsed = 0f;

            _swarmLeft = 0f;
            _swarmFired[0] = _swarmFired[1] = false;
            _eliteDue[0] = _eliteDue[1] = false;
            _elitePending = 0;
            _elites.Clear();

            SwarmKind = -1;
            SwarmsFired = 0;
            ElitesSpawned = 0;

            Banner = "";
            BannerLeft = 0f;

            // Сид мира, а не часы: один и тот же уровень должен давать один и тот же
            // забег, иначе два прогона проверки не сравнить.
            _random = new System.Random(world != null ? world.CurrentSeed ^ 0x7A11 : 1);

            if (crowd != null)
            {
                crowd.ClearEvents();
                crowd.SetDirective(Compose());
            }
        }

        /// <summary>
        /// Шаг директора. Отдельно от Update, чтобы прогон мог гонять стадию сам —
        /// семь минут забега за десяток секунд.
        /// </summary>
        public void Tick(float deltaTime)
        {
            if (deltaTime <= 0f) return;

            EnsureLinks();
            ReadKinds();

            BannerLeft = math.max(0f, BannerLeft - deltaTime);

            if (Phase != RunPhase.Victory)
            {
                Elapsed += deltaTime;

                if (Phase == RunPhase.Stage && Elapsed >= stageSeconds)
                {
                    Phase = RunPhase.FinalSwarm;
                    Announce("ФИНАЛЬНЫЙ РОЙ — Матка не убита вовремя");
                }

                UpdateSwarms(deltaTime);
                UpdateElites();
            }

            if (crowd == null) return;

            crowd.SetDirective(Compose());

            // Толпа гуще к логову: стало плотнее — значит теплее. Тот же указатель,
            // что вёл раньше к незажжённому генератору.
            crowd.SetHotPoint(Phase != RunPhase.Victory && queen != null && queen.HasLair
                ? queen.LairPosition
                : (Vector3?)null);
        }

        /// <summary>Будит Матку, если игрок стоит у логова. Её живучесть зависит от того, когда.</summary>
        public bool TrySummonQueen(Vector3 playerPosition)
        {
            if (queen == null || Phase == RunPhase.Victory) return false;

            // Сверх конца стадии рост продолжается, но упирается: в финальном рое
            // Матка и так дерётся на два фронта с игроком.
            var t = math.min(Progress, 1.5f);

            return queen.TrySummon(playerPosition, 1f + queenGrowth * t);
        }

        /// <summary>Перематывает стадию — отладка, чтобы не ждать гребня вживую.</summary>
        public void SkipAhead(float seconds)
        {
            if (Phase == RunPhase.Victory || seconds <= 0f) return;

            Elapsed += seconds;
        }

        /// <summary>
        /// Директива толпе на текущий момент.
        /// </summary>
        public CrowdDirective Compose()
        {
            var t = math.min(1f, Progress);
            var intensity = curve.Intensity(t);

            var directive = new CrowdDirective
            {
                Population = (int)math.round(basePopulation * intensity),
                SpawnRate = baseSpawnRate * intensity,
                MicroWave = microWave,
                HealthScale = 1f + healthGrowth * t,
                SpeedScale = 1f + speedGrowth * t,
                NearCap = crowd != null ? crowd.DefaultNearCap : 0,
                SwarmKind = -1,
                SwarmShare = 0f,
                Hue = Vector3.one,
                Frenzy = false,

                // Тяжёлые виды открываются ступенями: сначала только мелочь, к шестой
                // десятой стадии — все. «+0.001» — чтобы ровно на границе ступень
                // уже открылась, а не ждала следующего кадра.
                MaxKindHealth = (int)math.floor(math.lerp(_minKindHealth, _maxKindHealth,
                    math.saturate(t / math.max(0.01f, heavyUnlock))) + 0.001f)
            };

            if (_swarmLeft > 0f && SwarmKind >= 0)
            {
                directive.Population += swarmExtra;
                directive.SpawnRate += swarmRate;
                directive.SwarmKind = SwarmKind;
                directive.SwarmShare = swarmShare;
            }

            switch (Phase)
            {
                case RunPhase.FinalSwarm:
                    ApplyFinal(ref directive);
                    break;

                case RunPhase.Victory:
                    // Матка убита: пополнения нет вовсе. Живые разбегаются паникой
                    // и остаются в темноте за пределами света — но новых не будет.
                    directive.Population = 0;
                    directive.SpawnRate = 0f;
                    directive.SwarmKind = -1;
                    break;
            }

            return directive;
        }

        /// <summary>
        /// Финальный рой: потолок особей, снятый ближний круг, растущая скорость
        /// и ступени, на которых орда крепнет и меняет цвет.
        ///
        /// Его задача та же, что у призраков Megabonk: не дать забегу длиться вечно.
        /// Убить Матку можно и в нём — но уже на два фронта.
        /// </summary>
        private void ApplyFinal(ref CrowdDirective directive)
        {
            var overtime = math.max(0f, Elapsed - stageSeconds);
            var capacity = crowd != null ? crowd.Capacity : directive.Population;

            directive.Population = (int)math.round(math.lerp(directive.Population, capacity,
                math.saturate(overtime / finalRamp)));

            directive.SpawnRate = math.max(directive.SpawnRate, finalSpawnRate);

            if (directive.NearCap > 0)
            {
                directive.NearCap = (int)math.round(math.lerp(directive.NearCap, finalNearCap,
                    math.saturate(overtime / finalNearRamp)));
            }

            directive.SpeedScale *= math.min(finalSpeedMax, 1f + finalSpeedPerMinute * overtime / 60f);

            var tier = FinalTier;

            directive.HealthScale *= 1f + finalHealthPerTier * tier;
            directive.Hue = tier switch
            {
                2 => (Vector3)(Vector4)finalHueThird,
                1 => (Vector3)(Vector4)finalHueSecond,
                _ => Vector3.one
            };

            // Без ряби: у финального роя затиший нет.
            directive.MicroWave = 1f;
            directive.MaxKindHealth = _maxKindHealth;
            directive.Frenzy = true;
        }

        /// <summary>Ступень финального роя: 0, 1 или 2. Вне финального роя — 0.</summary>
        public int FinalTier
        {
            get
            {
                if (Phase != RunPhase.FinalSwarm) return 0;

                var overtime = Elapsed - stageSeconds;

                return overtime >= finalTiers.y ? 2 : overtime >= finalTiers.x ? 1 : 0;
            }
        }

        /// <summary>
        /// Рои стоят серединой на гребнях кривой. Вид роя — мелочь (живучесть 1),
        /// потому что рой — это волна, которую выкашивают, а не стена, которую долбят.
        /// </summary>
        private void UpdateSwarms(float deltaTime)
        {
            _swarmLeft = math.max(0f, _swarmLeft - deltaTime);

            if (_swarmLeft <= 0f) SwarmKind = -1;

            for (var i = 0; i < 2; i++)
            {
                if (_swarmFired[i]) continue;

                var peak = i == 0 ? curve.firstPeak : curve.secondPeak;
                var start = peak * stageSeconds - swarmSeconds * 0.5f;

                if (Elapsed < start) continue;

                _swarmFired[i] = true;

                // Пропущенный целиком рой (перемотка, долгий кадр) не догоняется:
                // рой, начавшийся после своего конца, был бы вспышкой из ничего.
                if (Elapsed > start + swarmSeconds) continue;

                SwarmKind = PickSwarmKind();
                if (SwarmKind < 0) continue;

                _swarmLeft = start + swarmSeconds - Elapsed;
                SwarmsFired++;

                var kind = crowd.KindAt(SwarmKind);
                Announce($"РОЙ: {(kind != null ? kind.name : "?")}");
            }
        }

        private int PickSwarmKind()
        {
            if (crowd == null || crowd.KindCount == 0) return -1;

            var light = new List<int>();

            for (var i = 0; i < crowd.KindCount; i++)
            {
                if (crowd.KindAt(i).Health <= _minKindHealth) light.Add(i);
            }

            if (light.Count == 0) return 0;

            return light[_random.Next(light.Count)];
        }

        /// <summary>
        /// Элита приходит по расписанию и заводится, как только нашлось место:
        /// в самый плотный момент слотов может не быть, и элита просто дождётся.
        /// </summary>
        private void UpdateElites()
        {
            for (var i = 0; i < 2; i++)
            {
                if (_eliteDue[i]) continue;

                var when = (i == 0 ? firstElite : secondElite) * stageSeconds;
                if (Elapsed < when) continue;

                _eliteDue[i] = true;
                _elitePending++;
            }

            if (crowd != null && _elitePending > 0)
            {
                var handle = crowd.SpawnElite(eliteSize, eliteHealth * (1f + healthGrowth * math.min(1f, Progress)),
                    eliteSpeed, (Vector3)(Vector4)eliteHue);

                if (handle.IsValid)
                {
                    _elitePending--;
                    _elites.Add(handle);
                    ElitesSpawned++;

                    Announce("ЭЛИТА: " + (crowd.KindAt(crowd.HeaviestKind) != null
                        ? crowd.KindAt(crowd.HeaviestKind).name
                        : "?"));
                }
            }

            for (var i = _elites.Count - 1; i >= 0; i--)
            {
                if (crowd == null || !crowd.TryGetSpider(_elites[i], out _, out _))
                {
                    _elites.RemoveAt(i);
                    if (crowd != null) Announce("элита убита");
                }
            }
        }

        private void ReadKinds()
        {
            if (crowd == null || crowd.KindCount == 0) return;

            _minKindHealth = int.MaxValue;
            _maxKindHealth = int.MinValue;

            for (var i = 0; i < crowd.KindCount; i++)
            {
                var health = crowd.KindAt(i).Health;

                _minKindHealth = math.min(_minKindHealth, health);
                _maxKindHealth = math.max(_maxKindHealth, health);
            }
        }

        private void OnQueenDied(Vector3 position)
        {
            Phase = RunPhase.Victory;

            // Награда — свет. Катакомбы загораются целиком: это та же вспышка, что
            // была у генератора, только одна и на весь уровень, в конце.
            if (fixtures != null) fixtures.SetAllLit(true);

            if (crowd != null)
            {
                crowd.Panic(position, victoryPanicRadius, victoryPanicTime);
                crowd.SetDirective(Compose());
                crowd.SetHotPoint(null);
            }

            Announce("МАТКА УБИТА — катакомбы озарены, стадия пройдена");
        }

        private void Announce(string text)
        {
            Banner = text;
            BannerLeft = bannerSeconds;

            Announced?.Invoke(text);
        }

        /// <summary>Строка для наложения в стенде.</summary>
        public string Describe()
        {
            var time = Phase == RunPhase.FinalSwarm
                ? $"<color=red>ФИНАЛЬНЫЙ РОЙ +{Clock(Elapsed - stageSeconds)}, ступень {FinalTier + 1}</color>"
                : Phase == RunPhase.Victory
                    ? "<color=lime>СТАДИЯ ПРОЙДЕНА</color>"
                    : $"<b>{Clock(Elapsed)}</b> / {Clock(stageSeconds)} — {curve.Section(math.min(1f, Progress))}";

            var swarm = SwarmActive ? $"   <color=orange>РОЙ {_swarmLeft:0} с</color>" : "";
            var elites = _elites.Count > 0 ? $"   элита: {_elites.Count}" : "";

            return $"{time}   давление {Intensity:0.00}{swarm}{elites}";
        }

        private static string Clock(float seconds)
        {
            seconds = math.max(0f, seconds);
            return $"{(int)(seconds / 60f):00}:{(int)(seconds % 60f):00}";
        }
    }
}
