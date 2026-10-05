using System;
using System.Collections.Generic;
using UnityEngine;

namespace MineGenerator.Catacombs
{
    /// <summary>Итоги забега — для экрана итогов, профиля (M4) и аналитики (M6).</summary>
    public struct RunResults
    {
        public bool Victory;
        public float Seconds;
        public int Tier;
        public int Depth;
        public int Level;
        public int Kills;
        public int EliteKills;
        public int QueensKilled;
        public float Chitin;
        public int[] KillsByWeapon;

        /// <summary>Чем убит игрок: вид паука или иное — для аналитики (M6).</summary>
        public DamageKind DeathKind;
        public int DeathSpider;
    }

    /// <summary>
    /// Контроллер забега — петля выживания M1: здоровье и смерть, опыт с ихора, уровни и
    /// карточки, рост пушек и томов, возрождение, итоги, передышка из милосердия.
    ///
    /// Сам ничего не рисует: экраны (HUD, карточки, смерть, итоги) слушают его события, а время
    /// держит владелец времени через состояния <see cref="GameFlow"/>.
    ///
    /// Компонент сцены на объекте мира, пересоздаётся «Починить свет» (грабли №8, №25).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CaveRunController : MonoBehaviour
    {
        [SerializeField] private CatacombWorld world;
        [SerializeField] private SpiderCrowd crowd;
        [SerializeField] private CaveRunDirector director;
        [SerializeField] private CaveQueen queen;
        [SerializeField] private IchorField ichor;

        [Header("Старт")]
        [Tooltip("Стартовая пушка без класса (M4 выдаёт пушку класса).")]
        [SerializeField] private WeaponId startWeapon = WeaponId.Launcher;

        [Header("Передышка из милосердия (AI Director Left 4 Dead)")]
        [Tooltip("Ниже этой доли здоровья в момент решения — передышка возможна.")]
        [SerializeField, Range(0.05f, 0.8f)] private float mercyHealth = 0.5f;

        [Tooltip("Сколько секунд при смерти накопить (PlayerVitals.LowHealthSeconds), прежде чем директор отпустит.")]
        [SerializeField, Range(1f, 30f)] private float mercyAfter = 4f;

        [SerializeField, Range(0.3f, 1f)] private float mercyPopulation = 0.85f;
        [SerializeField, Range(0.1f, 1f)] private float mercySpawn = 0.6f;
        [SerializeField, Range(1f, 60f)] private float mercySeconds = 12f;

        [Tooltip("Не чаще раза в столько секунд: милосердие — исключение, а не режим.")]
        [SerializeField, Range(10f, 300f)] private float mercyCooldown = 60f;

        [Header("Хитин с Маток по ярусам (M4)")]
        [SerializeField] private float[] queenChitin = { 60f, 120f, 250f };

        private CatacombTestRig _rig;
        private PlayerArsenal _arsenal;
        private PlayerVitals _vitals;
        private PlayerMobility _mobility;
        private GameFlow _flow;

        private readonly PlayerStats _stats = new PlayerStats();
        private readonly List<Card> _cards = new List<Card>(4);
        private System.Random _random = new System.Random(1);

        private bool _bound;
        private bool _runActive;
        private bool _offering;
        private bool _cocoonOffer;
        private int _pendingCocoons;

        private float _mercyLeft;
        private float _mercyCooldownLeft;

        private DamageKind _lastHitKind;
        private int _lastHitSpider = -1;

        public RunSession Session { get; private set; } = new RunSession();

        public PlayerStats Stats => _stats;

        /// <summary>Карточки на экране выбора (пусто — экрана нет).</summary>
        public IReadOnlyList<Card> Cards => _cards;

        public bool Offering => _offering;

        /// <summary>Карточка из кокона — одна, без выбора.</summary>
        public bool CocoonOffer => _cocoonOffer;

        /// <summary>Сколько раз срабатывала передышка из милосердия — для прогона: исключение, а не режим.</summary>
        public int MercyTriggers { get; private set; }

        public bool MercyActive => _mercyLeft > 0f;

        /// <summary>Возрождение за рекламу уже использовано в этом забеге.</summary>
        public bool AdReviveUsed { get; private set; }

        public RunResults LastResults { get; private set; }

        /// <summary>Множители сложности вылазки (ярус, глубина, мягкий старт) — ставит M4.</summary>
        public DifficultyScale Difficulty { get; set; } = DifficultyScale.One;

        /// <summary>Источник открытых пушек и томов для карточек (M4: арсенал профиля). Пусто — всё.</summary>
        public Func<CardPool> PoolSource { get; set; }

        /// <summary>Класс и мета (M4) кладут свои статы сюда перед томами.</summary>
        public Action<PlayerStats> BaseStats { get; set; }

        /// <summary>Стартовая пушка забега (класс, M4).</summary>
        public WeaponId StartWeapon
        {
            get => startWeapon;
            set => startWeapon = value;
        }

        public event Action LevelUpOffered;
        public event Action LevelUpClosed;
        public event Action DeathShown;
        public event Action<RunResults> Finished;
        public event Action<string> Announced;

        /// <summary>Новый забег начат (после генерации уровня).</summary>
        public event Action RunStarted;

        // ------------------------------------------------------------------ связи

        private void OnEnable() => EnsureLinks();

        private void OnDisable() => Unbind();

        public void EnsureLinks()
        {
            if (world == null) world = GetComponent<CatacombWorld>();
            if (world == null) world = FindAnyObjectByType<CatacombWorld>();
            if (crowd == null) crowd = FindAnyObjectByType<SpiderCrowd>();
            if (director == null) director = FindAnyObjectByType<CaveRunDirector>();
            if (queen == null) queen = FindAnyObjectByType<CaveQueen>();
            if (ichor == null) ichor = FindAnyObjectByType<IchorField>();

            if (_rig == null) _rig = FindAnyObjectByType<CatacombTestRig>();

            if (_rig != null)
            {
                if (_arsenal == null) _arsenal = _rig.GetComponent<PlayerArsenal>();
                if (_vitals == null) _vitals = _rig.GetComponent<PlayerVitals>();
                if (_mobility == null) _mobility = _rig.GetComponent<PlayerMobility>();
            }

            Bind();
        }

        private void Bind()
        {
            if (_bound) return;
            _bound = true;

            if (world != null) world.Generated += OnGenerated;
            if (crowd != null) crowd.Killed += OnKilled;
            if (ichor != null) ichor.Collected += OnCollected;
            if (queen != null) queen.Died += OnQueenDied;

            if (_vitals != null)
            {
                _vitals.Died += OnDied;
                _vitals.Damaged += OnDamaged;
            }
        }

        private void Unbind()
        {
            if (!_bound) return;
            _bound = false;

            if (world != null) world.Generated -= OnGenerated;
            if (crowd != null) crowd.Killed -= OnKilled;
            if (ichor != null) ichor.Collected -= OnCollected;
            if (queen != null) queen.Died -= OnQueenDied;

            if (_vitals != null)
            {
                _vitals.Died -= OnDied;
                _vitals.Damaged -= OnDamaged;
            }

            if (_flow != null) _flow.DeathSettled -= OnDeathSettled;
        }

        private GameFlow Flow
        {
            get
            {
                if (_flow == null && GameFlow.Instance != null)
                {
                    _flow = GameFlow.Instance;
                    _flow.DeathSettled += OnDeathSettled;
                }

                return _flow;
            }
        }

        private void Start()
        {
            if (!Application.isPlaying) return;

            EnsureLinks();

            // Уровень мог собраться раньше, чем контроллер подписался.
            if (world != null && world.Layout != null && !world.IsGenerating && !_runActive) StartRun();
        }

        // ------------------------------------------------------------------ забег

        private void OnGenerated()
        {
            // Новый уровень без вылазки (R на стенде, «Ещё раз») — новый забег. Переход между
            // ярусами (M4) держит забег сам и зовёт ContinueRun.
            if (_continueOnGenerate)
            {
                _continueOnGenerate = false;
                ContinueRun();
                return;
            }

            StartRun();
        }

        private bool _continueOnGenerate;

        /// <summary>Следующая генерация — продолжение вылазки (ярус M4), а не новый забег.</summary>
        public void ContinueOnNextLevel() => _continueOnGenerate = true;

        /// <summary>Новый забег: всё с нуля, стартовая пушка, полное здоровье.</summary>
        public void StartRun(RunSession restore = null)
        {
            EnsureLinks();

            Session = restore ?? new RunSession();
            _random = new System.Random(world != null ? world.CurrentSeed ^ 0x51C0 : Environment.TickCount);

            _offering = false;
            _cocoonOffer = false;
            _pendingCocoons = 0;
            _cards.Clear();
            _mercyLeft = 0f;
            _mercyCooldownLeft = 0f;
            MercyTriggers = 0;
            AdReviveUsed = false;

            if (_arsenal != null)
            {
                _arsenal.ClearLoadout();

                if (Session.Weapons.Count == 0) Session.Weapons.Add(new OwnedWeapon { Id = startWeapon, Level = 1 });

                foreach (var weapon in Session.Weapons)
                {
                    _arsenal.Give(weapon.Id, weapon.Level);
                    if (weapon.Evolved) _arsenal.Evolve(weapon.Id);
                }

                _arsenal.Select(0);
            }

            if (_vitals != null) _vitals.ResetForRun();
            if (_mobility != null) _mobility.ResetForRun();
            if (ichor != null) ichor.Clear();

            RecomposeStats();

            if (_vitals != null) _vitals.Heal(float.MaxValue);

            ApplyTier();
            ApplyDifficulty();

            _runActive = true;
            Flow?.BeginRun();

            RunStarted?.Invoke();
        }

        /// <summary>Следующий ярус: рост переносится, привал лечит (M4 зовёт после генерации).</summary>
        public void ContinueRun()
        {
            EnsureLinks();

            if (ichor != null) ichor.Clear();
            if (_rig != null) _rig.SetDeathPose(0f);

            RecomposeStats();
            ApplyTier();
            ApplyDifficulty();

            _runActive = true;
            Flow?.BeginRun();

            RunStarted?.Invoke();
        }

        private void ApplyDifficulty()
        {
            if (director != null) director.Difficulty = Difficulty;
            if (ichor != null) ichor.ChitinScale = Difficulty.Chitin;
        }

        /// <summary>Свести статы: класс и мета (M4) + тома → игроку, пушкам, рывку, магниту.</summary>
        public void RecomposeStats()
        {
            _stats.Clear();

            BaseStats?.Invoke(_stats);
            Session.ApplyTomes(_stats);

            if (_rig != null) _rig.Stats = _stats;
            if (_vitals != null) _vitals.ApplyStats(_stats);
            if (_mobility != null) _mobility.ApplyStats(_stats);
            if (_arsenal != null) _arsenal.SetStats(_stats);
            if (ichor != null) ichor.MagnetRadius = _stats.MagnetRadius;

            if (Session.Rerolls == 0 && Session.Skips == 0 && Session.Banishes == 0 && Session.Level == 1)
            {
                Session.Rerolls = _stats.Rerolls;
                Session.Skips = _stats.Skips;
                Session.Banishes = _stats.Banishes;
            }
        }

        private void Update()
        {
            if (!Application.isPlaying) return;

            Tick(Time.deltaTime, Time.unscaledDeltaTime);
        }

        /// <summary>Шаг: время забега, милосердие, очередь уровней. Отдельно — прогоны шагают сами.</summary>
        public void Tick(float deltaTime, float unscaledDeltaTime)
        {
            if (!_runActive) return;

            var flow = Flow;
            var playing = flow == null || flow.IsPlaying;

            if (playing && deltaTime > 0f)
            {
                Session.Seconds += deltaTime;
                UpdateMercy(deltaTime);
            }

            // Уровни, набранные под стоп-кадром или в смерти, ждут игры.
            if (playing && !_offering && (Session.PendingLevels > 0 || _pendingCocoons > 0)) OfferNext();

            if (_rig != null && flow != null && flow.State == GameState.Dead) _rig.SetDeathPose(flow.DyingProgress);
        }

        // ------------------------------------------------------------------ опыт, хитин, коконы

        private void OnCollected(DropKind kind, float value, int count)
        {
            if (!_runActive) return;

            switch (kind)
            {
                case DropKind.Ichor:
                    Session.AddXp(value * _stats.XpScale);
                    break;

                case DropKind.Chitin:
                    Session.Chitin += value * _stats.ChitinScale;
                    break;

                case DropKind.Cocoon:
                    _pendingCocoons += count;
                    break;
            }
        }

        private void OnKilled(IReadOnlyList<KillRecord> kills)
        {
            if (!_runActive) return;

            for (var i = 0; i < kills.Count; i++)
            {
                var kill = kills[i];

                Session.Kills++;
                if (kill.Elite) Session.EliteKills++;
                if (kill.Special) Session.SpecialKills++;

                var weapon = WeaponIds.FromSource(kill.Source);
                if (weapon != WeaponId.Count) Session.KillsByWeapon[(int)weapon]++;
            }
        }

        private void OnQueenDied(Vector3 position)
        {
            if (!_runActive) return;

            Session.QueensKilled++;

            var tier = Mathf.Clamp(Session.Tier - 1, 0, queenChitin.Length - 1);
            if (ichor != null) ichor.Drop(position + Vector3.up, DropKind.Chitin, queenChitin[tier] * Difficulty.Chitin);

            Platform.Current.Happytime();
        }

        // ------------------------------------------------------------------ карточки

        private CardPool BuildPool()
        {
            var pool = PoolSource?.Invoke() ?? CardPool.Everything();

            pool.PairOf = id =>
            {
                var def = WeaponCatalog.Get(id);
                return (def.PairTome, def.PairLevel);
            };

            pool.Luck = _stats.Luck;

            return pool;
        }

        private void OfferNext()
        {
            var flow = Flow;
            if (flow != null && !flow.EnterLevelUp()) return;

            _cards.Clear();

            if (_pendingCocoons > 0)
            {
                _pendingCocoons--;
                _cocoonOffer = true;
                _cards.Add(CardOffer.Cocoon(Session, BuildPool(), _random));
            }
            else
            {
                _cocoonOffer = false;
                CardOffer.Make(Session, BuildPool(), _random, _cards);
            }

            _offering = true;

            if (_rig != null)
            {
                _rig.InputBlocked = true;
                _rig.SetLooking(false);
            }

            Platform.Current.GameplayStop();
            LevelUpOffered?.Invoke();
        }

        /// <summary>
        /// Ярус вылазки — тем, кто от него зависит: какая Матка (M3.5), сколько аффиксов у элиты
        /// и сколько особых живых одновременно (M3). Ярус ведёт сессия (M4).
        /// </summary>
        private void ApplyTier()
        {
            var tier = Mathf.Clamp(Session.Tier, 1, 3);

            if (queen != null) queen.SetTier(tier);
            if (director != null) director.Tier = tier;

            var specials = FindAnyObjectByType<CaveSpecials>();
            if (specials != null) specials.Tier = tier;
        }

        /// <summary>Выбрать карточку (клик, клавиша 1–4, касание).</summary>
        public void Choose(int index)
        {
            if (!_offering || index < 0 || index >= _cards.Count) return;

            Apply(_cards[index]);

            if (!_cocoonOffer) Session.PendingLevels = Math.Max(0, Session.PendingLevels - 1);

            CloseOffer();
        }

        /// <summary>Переброс карточек (счётчик из меты, M4; за рекламу — M5).</summary>
        public bool Reroll(bool free = false)
        {
            if (!_offering || _cocoonOffer) return false;
            if (!free && Session.Rerolls <= 0) return false;

            if (!free) Session.Rerolls--;

            // Эволюция, уже показанная, остаётся в пуле кандидатов — переброс её не теряет.
            CardOffer.Make(Session, BuildPool(), _random, _cards);
            LevelUpOffered?.Invoke();

            return true;
        }

        /// <summary>Пропуск уровня без выбора.</summary>
        public bool Skip()
        {
            if (!_offering || _cocoonOffer || Session.Skips <= 0) return false;

            Session.Skips--;
            Session.PendingLevels = Math.Max(0, Session.PendingLevels - 1);

            CloseOffer();
            return true;
        }

        /// <summary>Изгнание карточки до конца забега — и новый выбор.</summary>
        public bool Banish(int index)
        {
            if (!_offering || _cocoonOffer || Session.Banishes <= 0 || index < 0 || index >= _cards.Count) return false;

            var card = _cards[index];

            if (card.Type == CardType.NewWeapon) Session.BanishedWeapons.Add(card.Weapon);
            else if (card.Type == CardType.NewTome) Session.BanishedTomes.Add(card.Tome);
            else return false;

            Session.Banishes--;

            CardOffer.Make(Session, BuildPool(), _random, _cards);
            LevelUpOffered?.Invoke();

            return true;
        }

        private void CloseOffer()
        {
            _offering = false;
            _cocoonOffer = false;
            _cards.Clear();

            Flow?.ExitLevelUp();

            if (_rig != null)
            {
                _rig.InputBlocked = false;
                _rig.SetLooking(true);
            }

            Platform.Current.GameplayStart();
            LevelUpClosed?.Invoke();
        }

        /// <summary>Применить карточку: данные забега, пушки в руках, статы.</summary>
        public void Apply(in Card card)
        {
            Session.Apply(card);

            switch (card.Type)
            {
                case CardType.NewWeapon:
                    _arsenal?.Give(card.Weapon);
                    break;

                case CardType.WeaponLevel:
                    _arsenal?.Upgrade(card.Weapon);
                    break;

                case CardType.Evolution:
                    _arsenal?.Evolve(card.Weapon);
                    Announced?.Invoke("evolution");
                    break;

                case CardType.Heal:
                    _vitals?.Heal(card.Amount);
                    break;
            }

            if (card.IsTome) RecomposeStats();
        }

        // ------------------------------------------------------------------ смерть

        private void OnDamaged(DamageHit hit, DamageResult result)
        {
            _lastHitKind = hit.Kind;
            _lastHitSpider = hit.SpiderKind;
        }

        private void OnDied()
        {
            if (!_runActive) return;

            if (_offering) CloseOffer();

            if (_rig != null)
            {
                _rig.InputBlocked = true;
                _rig.SetLooking(false);
            }

            Platform.Current.GameplayStop();
            Flow?.EnterDying();

            // Без GameFlow (прогон) — экран смерти сразу.
            if (Flow == null) OnDeathSettled();
        }

        private void OnDeathSettled() => DeathShown?.Invoke();

        /// <summary>Можно ли возродиться зарядом.</summary>
        public bool CanReviveFree => _vitals != null && _vitals.Revives > 0;

        /// <summary>Можно ли возродиться за рекламу: раз за забег, если реклама есть.</summary>
        public bool CanReviveAd => !AdReviveUsed && Platform.Current.RewardedAvailable;

        public void ReviveFree()
        {
            if (!CanReviveFree) return;

            DoRevive(true);
        }

        public void ReviveForAd()
        {
            if (!CanReviveAd) return;

            Platform.Rewarded("revive", ok =>
            {
                if (!ok) return;

                AdReviveUsed = true;
                DoRevive(false);
            });
        }

        private void DoRevive(bool spendCharge)
        {
            _vitals?.Revive(spendCharge);

            if (_rig != null)
            {
                _rig.SetDeathPose(0f);
                _rig.InputBlocked = false;
                _rig.SetLooking(true);
            }

            Flow?.Revive();
            Platform.Current.GameplayStart();
        }

        /// <summary>Кончить забег: итоги (смерть или победа над Праматерью).</summary>
        public void Finish(bool victory)
        {
            if (!_runActive) return;

            _runActive = false;

            LastResults = new RunResults
            {
                Victory = victory,
                Seconds = Session.Seconds,
                Tier = Session.Tier,
                Depth = Session.Depth,
                Level = Session.Level,
                Kills = Session.Kills,
                EliteKills = Session.EliteKills,
                QueensKilled = Session.QueensKilled,
                Chitin = Session.Chitin,
                KillsByWeapon = (int[])Session.KillsByWeapon.Clone(),
                DeathKind = _lastHitKind,
                DeathSpider = _lastHitSpider
            };

            if (_rig != null)
            {
                _rig.InputBlocked = true;
                _rig.SetLooking(false);
            }

            Platform.Current.GameplayStop();
            Flow?.EnterResults();

            Finished?.Invoke(LastResults);
        }

        /// <summary>«Ещё раз»: новый уровень и новый забег сразу.</summary>
        public void Restart()
        {
            if (_rig != null)
            {
                _rig.SetDeathPose(0f);
                _rig.InputBlocked = false;
            }

            Flow?.BeginRun();
            _rig?.RegenerateLevel();
        }

        // ------------------------------------------------------------------ милосердие

        /// <summary>
        /// Передышка из милосердия (AI Director Left 4 Dead, Vermintide на нём же): после долгого
        /// пика у игрока при смерти цель населения ×0.85 и досыл ×0.6 на 12 с. Не чаще раза
        /// в минуту, не в финальном рое и не на глубине 3+ (там милосердия нет). Скрыто от игрока.
        /// </summary>
        private void UpdateMercy(float deltaTime)
        {
            _mercyCooldownLeft = Mathf.Max(0f, _mercyCooldownLeft - deltaTime);

            if (_mercyLeft > 0f)
            {
                _mercyLeft -= deltaTime;

                if (_mercyLeft <= 0f) SetMercy(false);
                return;
            }

            if (_vitals == null || director == null || _mercyCooldownLeft > 0f) return;
            if (director.Phase != CaveRunDirector.RunPhase.Stage || Session.Depth >= 3) return;

            if (_vitals.Fraction < mercyHealth && _vitals.LowHealthSeconds >= mercyAfter)
            {
                MercyTriggers++;
                _mercyLeft = mercySeconds;
                _mercyCooldownLeft = mercyCooldown;
                SetMercy(true);
            }
        }

        private void SetMercy(bool on)
        {
            if (director == null) return;

            director.MercyPopulation = on ? mercyPopulation : 1f;
            director.MercySpawn = on ? mercySpawn : 1f;
        }

        /// <summary>Для прогонов: подать здоровье напрямую в счётчик милосердия.</summary>
        public void TickMercyOnly(float deltaTime) => UpdateMercy(deltaTime);
    }
}
