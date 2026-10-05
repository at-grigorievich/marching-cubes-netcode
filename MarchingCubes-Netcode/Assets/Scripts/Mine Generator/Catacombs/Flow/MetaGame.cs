using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace MineGenerator.Catacombs
{
    /// <summary>
    /// Мета-игра (M4): профиль и сохранения, лагерь, вылазка из трёх ярусов с привалами
    /// и контрольной точкой, итоги — хитин, статистика, задания.
    ///
    /// Заводится сам при старте игры в сцене с катакомбами и в сцене не хранится (как
    /// <see cref="GameFlow"/>): настраиваемых чисел у неё нет, а то, что сцена хранит, расходится
    /// с кодом (грабли №8). Без неё (прогоны в режиме редактирования, старые сцены) контроллер
    /// забега начинает забег сам на каждом уровне.
    ///
    /// Новый игрок лагеря не видит: первый запуск — сразу ярус 1 (CrazyGames: в игру сразу
    /// или за один клик). Лагерь — после первой смерти.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MetaGame : MonoBehaviour
    {
        public static MetaGame Instance { get; private set; }

        /// <summary>Сид лагеря: маленькие катакомбы, всегда одни и те же — «дом».</summary>
        private const int HubSeed = 4242;

        /// <summary>Привал лечит долю максимума (M4.1).</summary>
        public const float RestHeal = 0.3f;

        public SaveService Saves { get; private set; }
        public PlayerProfile Profile => Saves?.Profile;

        /// <summary>Номер сегодняшнего дня по UTC — ежедневные задания и серия.</summary>
        public int Today { get; private set; }

        /// <summary>Хитин за серию входов, выданный при загрузке, — показать в лагере.</summary>
        public float StreakReward { get; private set; }

        public bool InSortie { get; private set; }
        public PlayerClass Class { get; private set; }
        public int Depth { get; private set; } = 1;

        /// <summary>Задания, выполненные последним забегом, — для итогов.</summary>
        public List<QuestDef> LastCompleted { get; private set; } = new List<QuestDef>();

        /// <summary>Карточки привала (одна бесплатная).</summary>
        public List<Card> RestCards { get; } = new List<Card>();

        public event Action RestOffered;
        public event Action HubEntered;

        public CaveRunController Controller => _controller;

        private CatacombWorld _world;
        private CaveRunController _controller;
        private CaveRunDirector _director;
        private CaveQueen _queen;
        private SpiderCrowd _crowd;
        private CaveSpecials _specials;
        private CaveFixtures _fixtures;
        private CaveWebs _webs;
        private IchorField _ichor;
        private CatacombTestRig _rig;

        private CatacombSettings _baseSettings;
        private CatacombSettings _copy;

        private bool _startOnGenerate;
        private RunSnapshot _restore;
        private bool _inHub;
        private float _orbit;
        private readonly System.Random _random = new System.Random(Environment.TickCount);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (Instance != null) return;
            if (FindFirstObjectByType<CatacombWorld>() == null) return;

            new GameObject("Meta Game").AddComponent<MetaGame>();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => Instance = null;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;

            _world = FindAnyObjectByType<CatacombWorld>();
            _controller = FindAnyObjectByType<CaveRunController>();
            _director = FindAnyObjectByType<CaveRunDirector>();
            _queen = FindAnyObjectByType<CaveQueen>();
            _crowd = FindAnyObjectByType<SpiderCrowd>();
            _specials = FindAnyObjectByType<CaveSpecials>();
            _fixtures = FindAnyObjectByType<CaveFixtures>();
            _webs = FindAnyObjectByType<CaveWebs>();
            _ichor = FindAnyObjectByType<IchorField>();
            _rig = FindAnyObjectByType<CatacombTestRig>();

            _baseSettings = _world != null ? _world.Settings : null;

            Saves = new SaveService(new LocalSaveBackend(), new PlatformCloudBackend());
            Saves.Load(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

            // Облако площадки отвечает позже: новее локального — подменяет профиль (покупки с другого
            // устройства не теряются).
            Platform.Current.CloudLoad(json =>
            {
                if (Saves.MergeCloud(json)) HubEntered?.Invoke();
            });

            Today = Quests.DayOf(DateTime.UtcNow);
            StreakReward = Quests.DailyLogin(Profile, Today);
            Saves.MarkDirty();

            ApplyLanguage();
            Platform.Readied += ApplyLanguage;

            if (_rig != null && _rig.Vitals != null) _rig.Vitals.Damaged += OnDamaged;
            Platform.Readied += ApplyQuality;
            ApplySettings();
            ApplyQuality();

            if (_controller != null)
            {
                _controller.AutoStart = false;
                _controller.BaseStats = ComposeBase;
                _controller.PoolSource = BuildPool;
                _controller.Finished += OnFinished;
                _controller.DescentTaken += OnDescent;
                _controller.CardChosen += OnCardChosen;
            }

            if (_world != null) _world.Generated += OnGenerated;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;

            Platform.Readied -= ApplyLanguage;
            Platform.Readied -= ApplyQuality;
            if (_rig != null && _rig.Vitals != null) _rig.Vitals.Damaged -= OnDamaged;

            // Статические множители качества пережили бы выход из play-режима и попали бы в прогоны
            // редактора — те меряют «Высокое».
            CaveExplosions.Density = 1f;
            CaveBlastMarks.Slots = CaveBlastMarks.ShaderSlots;

            if (_controller != null)
            {
                _controller.Finished -= OnFinished;
                _controller.DescentTaken -= OnDescent;
                _controller.CardChosen -= OnCardChosen;
            }

            if (_world != null) _world.Generated -= OnGenerated;

            Saves?.Flush();
        }

        private void Start()
        {
            HookUi();

            // Первый запуск — сразу в забег: ни лагеря, ни меню.
            if (Profile.Stats.Runs == 0 && !Profile.HasSnapshot) StartSortie(PlayerClass.Ranger, 1);
            else EnterHub();
        }

        private void Update()
        {
            Saves?.Tick(Time.unscaledDeltaTime);
            MeasureQuality(Time.unscaledDeltaTime);

            if (_inHub) OrbitCamera(Time.unscaledDeltaTime);
        }

        private void OnApplicationPause(bool paused)
        {
            if (!paused) return;

            Saves?.Flush();
            SessionEnd();
        }

        private void OnApplicationQuit()
        {
            Saves?.Flush();
            SessionEnd();
        }

        // ------------------------------------------------------------------ аналитика (M6.2)

        private int _sessionRuns;
        private DamageHit _lastHit;
        private bool _sessionEnded;

        private void SessionEnd()
        {
            if (_sessionEnded) return;
            _sessionEnded = true;

            Analytics.F("seconds", Time.realtimeSinceStartup).F("runs", _sessionRuns).Send("session_end");
        }

        private void OnDamaged(DamageHit hit, DamageResult result)
        {
            if (result.Dealt > 0f) _lastHit = hit;
        }

        /// <summary>Причина смерти: чем ударили последним и шёл ли финальный рой.</summary>
        private string DeathCause()
        {
            var cause = _lastHit.Kind.ToString().ToLowerInvariant();
            if (_lastHit.Kind == DamageKind.Bite) cause += ":" + _lastHit.SpiderKind;
            if (_director != null && _director.Phase == CaveRunDirector.RunPhase.FinalSwarm) cause += ";final_swarm";
            return cause;
        }

        private void OnCardChosen(Card chosen, IReadOnlyList<Card> offered)
        {
            var list = new System.Text.StringBuilder();

            for (var i = 0; i < offered.Count; i++)
            {
                if (i > 0) list.Append(',');
                list.Append(Describe(offered[i]));
            }

            Analytics.F("level", _controller != null ? _controller.Session.Level : 0).F("offered", list.ToString()).F("chosen", Describe(chosen)).Send("level_up");
        }

        private static string Describe(in Card card) => card.Type switch
        {
            CardType.NewWeapon => "weapon:" + card.Weapon,
            CardType.WeaponLevel => "weapon:" + card.Weapon + ">" + card.ToLevel,
            CardType.Evolution => "evolve:" + card.Weapon,
            CardType.NewTome => "tome:" + card.Tome,
            CardType.TomeLevel => "tome:" + card.Tome + ">" + card.ToLevel,
            _ => card.Type.ToString().ToLowerInvariant()
        };

        private static string WeaponsOf(RunSession session)
        {
            var text = new System.Text.StringBuilder();
            foreach (var weapon in session.Weapons) text.Append(text.Length > 0 ? "," : "").Append(weapon.Id).Append(weapon.Level).Append(weapon.Evolved ? "e" : "");
            return text.ToString();
        }

        private static string TomesOf(RunSession session)
        {
            var text = new System.Text.StringBuilder();
            foreach (var tome in session.Tomes) text.Append(text.Length > 0 ? "," : "").Append(tome.Id).Append(tome.Level);
            return text.ToString();
        }

        // ------------------------------------------------------------------ связь с интерфейсом

        private void HookUi()
        {
            var ui = GameUi.Instance;
            if (ui == null) return;

            ui.Hints.WasShown = key => Profile.HintsShown.Contains(key);
            ui.Hints.MarkShown = key =>
            {
                if (Profile.HintsShown.Contains(key)) return;

                Profile.HintsShown.Add(key);
                Saves.MarkDirty();
            };

            // «Ещё раз» — переход, который игрок и так ждёт: здесь межстраничная (M5.3). «В лагерь» — навигация, без неё.
            ui.Results.OnAgain = () => AfterInterstitial("run_again", true, () => StartSortie(Class, Depth));
            ui.Results.OnDoubleChitin = DoubleChitinForAd;
            ui.Results.OnCamp = EnterHub;
            ui.Results.ExtraText = ResultsText;
            ui.Pause.OnCamp = () =>
            {
                ui.ResumeFromPause();
                _controller?.Finish(false);
            };

            ui.EnsureHub(this);
        }

        /// <summary>
        /// Язык (M5.4): выбранный в настройках → язык площадки → английский. SDK отвечает
        /// асинхронно, поэтому язык применяется и при загрузке профиля, и когда площадка поднялась.
        /// В редакторе без выбора — язык системы, как и было: разработка идёт на родном языке.
        /// </summary>
        private void ApplyLanguage()
        {
            var chosen = Profile.Settings.Language;
            var platform = Platform.Ready ? Platform.Current.Language : "";

            if (Application.isEditor && string.IsNullOrEmpty(chosen) && string.IsNullOrEmpty(platform)) return;

            Loc.SetLanguage(Loc.Resolve(chosen, platform));
        }

        // ------------------------------------------------------------------ качество (M5.6)

        public QualityTier Quality { get; private set; } = QualityTier.High;

        private float _measureSeconds;
        private int _measureFrames;
        private bool _measured;

        /// <summary>Уровень: выбранный в настройках, иначе по устройству площадки и памяти.</summary>
        public void ApplyQuality()
        {
            Quality = QualityPolicy.Resolve(Profile.Settings.Quality, Platform.Current.Device, SystemInfo.systemMemorySize);
            var preset = QualityPreset.For(Quality);

            if (_crowd != null) _crowd.SetQuality(preset.CrowdCapacity, preset.DrawDistance);
            if (_director != null) _director.QualityPopulation = preset.Population;
            if (_ichor != null) _ichor.ValueScale = preset.ValueScale;

            CaveExplosions.Density = preset.Particles;
            CaveBlastMarks.Slots = preset.ScorchSlots;

            var dust = FindAnyObjectByType<CaveDustMotes>();
            if (dust != null) dust.enabled = preset.Dust;

            // Масштаб рендера — свойство ассета пайплайна: в редакторе правка осела бы в файле
            // (грабли №8 в рантайм-виде), поэтому — только в сборке.
            if (!Application.isEditor && UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline is UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset urp)
            {
                urp.renderScale = preset.RenderScale;
            }
        }

        /// <summary>
        /// Замер (M5.6): средний кадр первых 10 с игры; дольше 26 мс — ступень вниз один раз, с записью
        /// в профиль. Только на «авто» (выбор игрока не трогаем) и только в сборке: кадр редактора
        /// тяжелее сборки, и замер в нём занизил бы качество всем.
        /// </summary>
        private void MeasureQuality(float deltaTime)
        {
            if (_measured || Application.isEditor || Profile == null || Profile.Settings.Quality >= 0) return;
            if (GameFlow.Instance == null || GameFlow.Instance.State != GameState.Playing) return;

            _measureSeconds += deltaTime;
            _measureFrames++;

            if (_measureSeconds < QualityPolicy.MeasureSeconds) return;

            _measured = true;

            var average = _measureSeconds * 1000f / Mathf.Max(1, _measureFrames);
            var lowered = QualityPolicy.StepDown(Quality, average);
            if (lowered == Quality) return;

            Profile.Settings.Quality = (int)lowered;
            Saves.MarkDirty();
            ApplyQuality();
        }

        // ------------------------------------------------------------------ статы и пул

        /// <summary>Класс + мета-улучшения — база статов забега, к ней прибавятся тома.</summary>
        private void ComposeBase(PlayerStats stats)
        {
            PlayerClasses.Apply(Class, stats);
            MetaUpgrades.Apply(Profile, stats);
        }

        /// <summary>Пул карточек — только открытое в арсенале (M4).</summary>
        private CardPool BuildPool()
        {
            var pool = new CardPool();

            foreach (var id in Profile.UnlockedWeapons)
            {
                if (id >= 0 && id < (int)WeaponId.Count) pool.Weapons.Add((WeaponId)id);
            }

            foreach (var id in Profile.UnlockedTomes)
            {
                if (id >= 0 && id < (int)TomeId.Count) pool.Tomes.Add((TomeId)id);
            }

            return pool;
        }

        // ------------------------------------------------------------------ вылазка

        /// <summary>Начать вылазку: класс, глубина; со снимком — продолжить с его яруса.</summary>
        public void StartSortie(PlayerClass cls, int depth, RunSnapshot snapshot = null)
        {
            InSortie = true;
            Class = cls;
            Depth = Mathf.Clamp(depth, 1, Mathf.Max(1, Profile.UnlockedDepth));

            Profile.SelectedClass = (int)cls;
            Profile.SelectedDepth = Depth;

            _restore = snapshot;
            _startOnGenerate = true;
            _sessionRuns++;

            Analytics.F("class", cls).F("depth", Depth).F("weapon", PlayerClasses.StartWeapon(cls)).F("run", Profile.Stats.Runs + 1)
                .F("continue", snapshot != null).Send("run_start");

            var tier = snapshot != null ? snapshot.Tier : 1;
            var seed = snapshot != null && snapshot.Seed != 0 ? snapshot.Seed : _random.Next(1, int.MaxValue);

            GenerateTier(tier, seed);
        }

        /// <summary>Продолжить вылазку с контрольной точки (перезагрузка страницы, M4.1).</summary>
        public void ContinueSortie()
        {
            if (!Profile.HasSnapshot) return;

            var snapshot = Profile.Snapshot;
            StartSortie((PlayerClass)snapshot.ClassId, snapshot.Depth, snapshot);
        }

        private void GenerateTier(int tier, int seed)
        {
            _inHub = false;
            SetRunSystems(true);

            var recipe = LevelRecipe.For(tier, Depth);
            ApplyRecipe(recipe);

            if (_controller != null)
            {
                _controller.Difficulty = SortieDifficulty.Scale(tier, Depth, Profile.Stats.Runs);
                _controller.FinalTier = SortieDifficulty.MaxTier;
            }

            if (_director != null) _director.SetStageSeconds(recipe.StageSeconds);
            if (_specials != null) _specials.ScheduleEnabled = SortieDifficulty.SpecialsAllowed(tier, Depth, Profile.Stats.Runs);

            if (_rig != null)
            {
                _rig.enabled = true;
                _rig.InputBlocked = false;
                if (_rig.TryGetComponent<PlayerArsenal>(out var arsenal)) arsenal.ShowViews(true);
            }

            GameFlow.Instance?.BeginRun();
            _world.Generate(seed);
        }

        private void ApplyRecipe(in LevelRecipe recipe)
        {
            if (_baseSettings == null || _world == null) return;

            // Копия ассета — правка самого ассета в рантайме в редакторе осела бы в файле.
            if (_copy != null) Destroy(_copy);
            _copy = Instantiate(_baseSettings);
            _copy.hideFlags = HideFlags.DontSave;
            _copy.ApplyRecipe(recipe);

            _world.UseSettings(_copy);

            if (_fixtures != null) _fixtures.CountScale = recipe.LampScale;
            if (_webs != null) _webs.Density = recipe.WebScale;
        }

        private void OnGenerated()
        {
            if (_inHub)
            {
                // Лагерь: всё горит — это дом.
                if (_fixtures != null) _fixtures.SetAllLit(true);
                PlaceHubCamera();
                return;
            }

            if (!_startOnGenerate || _controller == null) return;
            _startOnGenerate = false;

            var depth = Depth;
            RunSession session;

            if (_restore != null)
            {
                session = _restore.ToSession();
            }
            else
            {
                session = new RunSession { Depth = depth, Tier = 1 };
                _controller.StartWeapon = PlayerClasses.StartWeapon(Class);
            }

            _controller.StartRun(session);

            if (_restore != null)
            {
                _rig?.Vitals?.SetHealthShare(_restore.HealthShare);
                _restore = null;
            }

            SaveCheckpoint(_world.CurrentSeed);
        }

        /// <summary>Снимок на входе в ярус: перезагрузка страницы продолжит с него (тот же сид — та же карта).</summary>
        private void SaveCheckpoint(int seed)
        {
            if (_controller == null || _world == null) return;

            var health = _rig != null && _rig.Vitals != null ? _rig.Vitals.HealthShare : 1f;

            Profile.Snapshot = RunSnapshot.From(_controller.Session, (int)Class, seed, health);
            Profile.HasSnapshot = true;
            Saves.Flush();
        }

        /// <summary>Провал взят — привал: лечение и бесплатная карточка; время стоит.</summary>
        private void OnDescent()
        {
            if (_controller == null) return;

            var cleared = _controller.Session;
            Analytics.F("tier", cleared.Tier).F("time", cleared.Seconds).F("level", cleared.Level).F("weapons", WeaponsOf(cleared))
                .F("tomes", TomesOf(cleared)).Send("stage_clear");

            _rig?.Vitals?.Heal(_rig.Vitals.MaxHealth * RestHeal);

            RestCards.Clear();
            CardOffer.Make(_controller.Session, BuildPoolWithPairs(), new System.Random(_random.Next()), RestCards);

            if (_rig != null)
            {
                _rig.InputBlocked = true;
                _rig.SetLooking(false);
            }

            GameFlow.Instance?.EnterStageClear();
            RestOffered?.Invoke();
        }

        private CardPool BuildPoolWithPairs()
        {
            var pool = BuildPool();

            pool.PairOf = id =>
            {
                var def = WeaponCatalog.Get(id);
                return (def.PairTome, def.PairLevel);
            };

            return pool;
        }

        /// <summary>Карточка привала выбрана (или пропущена, index &lt; 0) — дальше вниз.</summary>
        public void ChooseRestCard(int index)
        {
            if (_controller != null && index >= 0 && index < RestCards.Count) _controller.Apply(RestCards[index]);

            RestCards.Clear();
            DescendFurther();
        }

        /// <summary>Полное лечение за рекламу на привале (M5): только если реклама есть.</summary>
        public void RestFullHealForAd(Action done)
        {
            Platform.Rewarded("rest_heal", rewarded =>
            {
                if (rewarded) _rig?.Vitals?.Heal(float.MaxValue);
                done?.Invoke();
            });
        }

        /// <summary>Спуск на следующий ярус: межстраничная, если пора (M5.3), — до генерации, пока стоит привал.</summary>
        private void DescendFurther() => AfterInterstitial("tier", false, DescendNow);

        private void DescendNow()
        {
            if (_controller == null) return;

            var session = _controller.Session;
            session.Tier = Mathf.Min(SortieDifficulty.MaxTier, session.Tier + 1);

            var seed = _random.Next(1, int.MaxValue);

            // Контрольная точка — вход на следующий ярус: перезагрузка соберёт тот же уровень.
            SaveCheckpoint(seed);

            _controller.ContinueOnNextLevel();
            GenerateTier(session.Tier, seed);
        }

        // ------------------------------------------------------------------ реклама (M5.3)

        private float _lastRunChitin;

        /// <summary>
        /// Межстраничная перед переходом, если политика разрешает (<see cref="AdPolicy"/>); иначе сразу.
        /// Время и звук на показ стоят (<see cref="Platform.Interstitial"/>), ошибка показа — переход идёт.
        /// </summary>
        private void AfterInterstitial(string placement, bool betweenRuns, Action next)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            if (!AdPolicy.CanInterstitial(Profile, now, betweenRuns))
            {
                next();
                return;
            }

            AdPolicy.MarkInterstitial(Profile, now);
            Saves.MarkDirty();
            Platform.Interstitial(placement, next);
        }

        /// <summary>«Хитин ×2» в итогах: ещё столько же, сколько принёс забег, — только при досмотре.</summary>
        private void DoubleChitinForAd()
        {
            if (_lastRunChitin < 1f) return;

            Platform.Rewarded("double_chitin", rewarded =>
            {
                if (!rewarded) return;

                Profile.AddChitin(_lastRunChitin);
                _lastRunChitin = 0f;
                Saves.Flush();
                GameUi.Instance?.Results.HideDouble();
            });
        }

        /// <summary>Ящик дня в лагере: раз в день, только за досмотренную рекламу.</summary>
        public bool DailyBoxAvailable => AdPolicy.CanClaimDailyBox(Profile, Today) && Platform.Current.RewardedAvailable;

        public void ClaimDailyBox(Action done)
        {
            Platform.Rewarded("daily_box", rewarded =>
            {
                if (rewarded)
                {
                    AdPolicy.ClaimDailyBox(Profile, Today);
                    Saves.Flush();
                }

                done?.Invoke();
            });
        }

        // ------------------------------------------------------------------ покупки и настройки

        /// <summary>Открыть пушку в арсенале за хитин (цена — описание пушки).</summary>
        public bool BuyWeapon(WeaponId id)
        {
            var price = WeaponCatalog.Get(id).Price;
            if (Profile.IsWeaponUnlocked(id) || Profile.Chitin < price) return false;

            Profile.Chitin -= price;
            Profile.UnlockedWeapons.Add((int)id);
            Saves.Flush();

            Analytics.F("item", "weapon:" + id).F("price", price).F("chitin", Profile.Chitin).Send("purchase");
            Analytics.F("what", "weapon:" + id).Send("unlock");
            return true;
        }

        public bool BuyUpgrade(MetaUpgrade id)
        {
            var price = MetaUpgrades.PriceOf(Profile, id);
            if (!MetaUpgrades.Buy(Profile, id)) return false;

            Saves.Flush();
            Analytics.F("item", "upgrade:" + id).F("level", Profile.UpgradeLevel(id)).F("price", price).F("chitin", Profile.Chitin).Send("purchase");
            return true;
        }

        public void RefundUpgrades()
        {
            MetaUpgrades.Refund(Profile);
            Saves.Flush();
        }

        /// <summary>Настройки профиля — в движок: громкость, взгляд.</summary>
        public void ApplySettings()
        {
            var settings = Profile.Settings;

            AudioListener.volume = Mathf.Clamp01(settings.MasterVolume);

            if (_rig != null)
            {
                _rig.LookScale = settings.Sensitivity;
                _rig.InvertY = settings.InvertY;
            }

            if (_crowd != null) ApplyQuality();
        }

        // ------------------------------------------------------------------ итоги

        private void OnFinished(RunResults results)
        {
            InSortie = false;

            var summary = _controller != null ? _controller.Summary : new RunSummary();
            summary.Depth = Depth;

            var stats = Profile.Stats;
            stats.Runs++;
            if (results.Victory) stats.Victories++;
            stats.Kills += results.Kills;
            stats.EliteKills += results.EliteKills;
            stats.SpecialKills += summary.SpecialKills;
            stats.KillsByFire += summary.KillsByFire;
            stats.BestDepth = Mathf.Max(stats.BestDepth, Depth);
            stats.BestTier = Mathf.Max(stats.BestTier, summary.MaxTier);
            stats.BestFinalSwarmSeconds = Mathf.Max(stats.BestFinalSwarmSeconds, summary.FinalSwarmSeconds);

            for (var i = 0; i < summary.KillsByWeapon.Length && i < stats.KillsByWeapon.Length; i++)
            {
                stats.KillsByWeapon[i] += summary.KillsByWeapon[i];
            }

            for (var t = 0; t < 3; t++) stats.QueensByTier[t] += summary.QueensByTier[t];

            PlayerProfile.Increment(stats.RunsAtDepth, Depth);
            if (summary.MaxTier >= 3) PlayerProfile.Increment(stats.ReachedTier3AtDepth, Depth);

            // Победа над Праматерью открывает следующую глубину.
            if (results.Victory) Profile.UnlockedDepth = Mathf.Max(Profile.UnlockedDepth, Depth + 1);

            Profile.AddChitin(results.Chitin);
            _lastRunChitin = results.Chitin;

            var classesBefore = Profile.UnlockedClasses.Count;
            LastCompleted = Quests.Apply(Profile, summary, Today);

            var session = _controller != null ? _controller.Session : null;
            Analytics.F("tier", results.Tier).F("depth", Depth).F("time", results.Seconds).F("level", results.Level)
                .F("cause", results.Victory ? "" : DeathCause()).F("kills", results.Kills).F("chitin", results.Chitin)
                .F("weapons", session != null ? WeaponsOf(session) : "").F("tomes", session != null ? TomesOf(session) : "")
                .Send(results.Victory ? "victory" : "death");

            foreach (var quest in LastCompleted) Analytics.F("id", quest.Id).Send("quest_done");
            for (var i = classesBefore; i < Profile.UnlockedClasses.Count; i++) Analytics.F("what", "class:" + (PlayerClass)Profile.UnlockedClasses[i]).Send("unlock");

            // Рекорд глубины — в таблицу площадки («Бездна», M6.3): растёт только на победе.
            if (results.Victory) Platform.Current.SubmitScore("depth", Profile.Stats.BestDepth);

            Profile.HasSnapshot = false;
            Saves.Flush();
        }

        /// <summary>Строка итогов: хитин в копилке, выполненные задания.</summary>
        private string ResultsText(RunResults results)
        {
            var text = new StringBuilder();

            text.AppendLine(Loc.F("results.chitin_total", Mathf.FloorToInt(Profile.Chitin)));

            // Цель на следующий забег (план M4.6): сколько не хватает до самой дешёвой закрытой
            // пушки. Хватает уже — молчим: об этом скажет лагерь.
            WeaponDef next = null;

            for (var i = 0; i < (int)WeaponId.Count; i++)
            {
                var def = WeaponCatalog.Get((WeaponId)i);
                if (def == null || Profile.IsWeaponUnlocked((WeaponId)i)) continue;
                if (next == null || def.Price < next.Price) next = def;
            }

            if (next != null && next.Price > Profile.Chitin)
            {
                text.AppendLine(Loc.F("results.next_unlock", Loc.T(next.NameKey), Mathf.CeilToInt(next.Price - Profile.Chitin)));
            }

            if (LastCompleted.Count > 0) text.AppendLine(Loc.T("results.quests"));

            foreach (var quest in LastCompleted)
            {
                text.AppendLine(Loc.F("results.quest_done", QuestText(quest)));
            }

            return text.ToString();
        }

        /// <summary>Текст задания по ключу вида: «Убей {0} пауков: {1}» и т. п.</summary>
        public static string QuestText(QuestDef def)
        {
            var weapon = def.Weapon < WeaponId.Count ? Loc.T(WeaponCatalog.Get(def.Weapon).NameKey) : "";
            var queen = def.Tier >= 1 ? Loc.T(QueenProfile.ForTier(def.Tier).NameKey) : "";

            return def.Kind switch
            {
                QuestKind.KillsWithWeapon => Loc.F(def.TextKey, def.Target, weapon),
                QuestKind.QueenOfTier => Loc.F(def.TextKey, queen),
                QuestKind.QueenOfTierUnder => Loc.F(def.TextKey, queen, UiKit.Clock(def.Seconds)),
                QuestKind.TierWithWeapon => Loc.F(def.TextKey, def.Tier, weapon),
                _ => Loc.F(def.TextKey, def.Target)
            };
        }

        // ------------------------------------------------------------------ лагерь

        /// <summary>Лагерь: забега нет, маленькие катакомбы с костром и лампами, камера облетает зал.</summary>
        public void EnterHub()
        {
            InSortie = false;
            _inHub = true;
            _startOnGenerate = false;

            SetRunSystems(false);

            if (_rig != null)
            {
                _rig.InputBlocked = true;
                _rig.SetLooking(false);
                _rig.enabled = false;
                if (_rig.TryGetComponent<PlayerArsenal>(out var arsenal)) arsenal.ShowViews(false);
            }

            GameFlow.Instance?.EnterHub();

            if (_baseSettings != null && _world != null)
            {
                ApplyRecipe(new LevelRecipe
                {
                    ChunksX = 2, ChunksY = 2, ChunksZ = 2, LevelCount = 1, RoomsMin = 3, RoomsMax = 4,
                    GalleryShare = 0.2f, PinchChance = 0f, PathChaos = 0.3f, LampScale = 1.6f, WebScale = 0f,
                    StageSeconds = 300f
                });

                _world.Generate(HubSeed);
            }

            Saves.Flush();
            HubEntered?.Invoke();
        }

        /// <summary>
        /// Системы забега — толпа, директор, Матка, особые, ихор. В лагере их нет: толпа без
        /// директора жила бы по своим полям и заселила бы лагерь. Включаются ДО генерации яруса:
        /// они подписаны на генерацию и перестраиваются по ней.
        /// </summary>
        private void SetRunSystems(bool on)
        {
            if (!on)
            {
                _specials?.Clear();
                _ichor?.Clear();
            }

            if (_crowd != null) _crowd.enabled = on;
            if (_director != null) _director.enabled = on;
            if (_queen != null) _queen.enabled = on;
            if (_specials != null) _specials.enabled = on;
            if (_ichor != null) _ichor.enabled = on;
        }

        private Vector3 _hubCentre;

        private void PlaceHubCamera()
        {
            if (_world == null) return;

            _hubCentre = _world.TryGetSpawnPoint(out var spawn) ? spawn + Vector3.up * 1.6f : _world.transform.position;
            _orbit = 0f;
        }

        private void OrbitCamera(float deltaTime)
        {
            if (_rig == null) return;

            _orbit += deltaTime * 0.12f;

            var offset = new Vector3(Mathf.Cos(_orbit), 0.25f, Mathf.Sin(_orbit)) * 2.5f;
            var eye = _hubCentre + offset;

            _rig.transform.position = eye;
            _rig.transform.rotation = Quaternion.LookRotation((_hubCentre - eye).normalized + Vector3.down * 0.15f, Vector3.up);
        }
    }
}
