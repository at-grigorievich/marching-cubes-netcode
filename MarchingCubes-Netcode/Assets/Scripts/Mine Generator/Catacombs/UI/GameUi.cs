using UnityEngine;

namespace MineGenerator.Catacombs
{
    /// <summary>
    /// Интерфейс игры: HUD забега и экраны (карточки, смерть, итоги, пауза, подсказки; лагерь — M4).
    /// Заменяет отладочный OnGUI (он остался в <see cref="CatacombDebugOverlay"/>, только в
    /// редакторе и development-сборке).
    ///
    /// Заводится сам при старте игры (как <see cref="GameFlow"/>) и в сцене не хранится: числа
    /// интерфейса живут в коде, а сцена расходилась бы с ним (грабли №8). Экран выбирается
    /// по состоянию игры: подписка на смену состояния, а не опрос в каждом кадре.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class GameUi : MonoBehaviour
    {
        public static GameUi Instance { get; private set; }

        public CatacombTestRig Rig { get; private set; }
        public PlayerArsenal Arsenal { get; private set; }
        public CaveRunController Controller { get; private set; }
        public CaveRunDirector Director { get; private set; }
        public CaveQueen Queen { get; private set; }
        public SpiderCrowd Crowd { get; private set; }
        public Camera Camera { get; private set; }
        public WeaponAudio Audio => Arsenal != null && Arsenal.Context != null ? Arsenal.Context.Audio : null;

        private Canvas _static;
        private Canvas _dynamic;
        private Canvas _screens;

        private RectTransform _staticRoot;
        private RectTransform _dynamicRoot;
        private RectTransform _screenRoot;

        public HudView Hud { get; private set; }
        public CardScreen Cards { get; private set; }
        public DeathScreen Death { get; private set; }
        public ResultsScreen Results { get; private set; }
        public PauseScreen Pause { get; private set; }
        public HintView Hints { get; private set; }

        /// <summary>Сколько миллисекунд занял последний кадр интерфейса — для UiCheck.</summary>
        public double LastTickMs { get; private set; }

        private GameFlow _flow;
        private PlayerVitals _vitals;
        private HitFeedback _feedback;

        /// <summary>Экран лагеря (M4) — его ставит мета, когда она есть.</summary>
        public IUiScreen HubScreen { get; set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (Instance != null) return;
            if (FindAnyObjectByType<CatacombWorld>() == null) return;

            new GameObject("Game UI").AddComponent<GameUi>();
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
            Build();
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;

            Unbind();
        }

        /// <summary>Собрать интерфейс. Публичный — UiCheck собирает его в режиме редактирования.</summary>
        public void Build()
        {
            if (_static != null) return;

            Loc.SetLanguage(Loc.SystemCode());

            UiKit.EnsureEventSystem();

            _static = UiKit.Canvas("HUD Static", 10, transform);
            _dynamic = UiKit.Canvas("HUD Dynamic", 11, transform);
            _screens = UiKit.Canvas("Screens", 20, transform);

            _staticRoot = SafeRoot(_static);
            _dynamicRoot = SafeRoot(_dynamic);
            _screenRoot = SafeRoot(_screens);

            Hud = new HudView(this);
            Hud.Build(_staticRoot, _dynamicRoot);

            Cards = new CardScreen(this);
            Cards.Build(_screenRoot);

            Death = new DeathScreen(this);
            Death.Build(_screenRoot);

            Results = new ResultsScreen(this);
            Results.Build(_screenRoot);

            Pause = new PauseScreen(this);
            Pause.Build(_screenRoot);

            Hints = new HintView(this);
            Hints.Build(_screenRoot);

            HideScreens();
        }

        private static RectTransform SafeRoot(Canvas canvas)
        {
            var root = UiKit.Node(canvas.transform, "Safe Area").Stretch();
            root.gameObject.AddComponent<SafeArea>();
            return root;
        }

        public RectTransform ScreenRoot => _screenRoot;

        // ------------------------------------------------------------------ связи

        /// <summary>Достать связи — публично для UiCheck (в редакторе Update не зовётся).</summary>
        public void EnsureLinks()
        {
            if (Rig == null)
            {
                Rig = FindAnyObjectByType<CatacombTestRig>();

                if (Rig != null)
                {
                    Arsenal = Rig.GetComponent<PlayerArsenal>();
                    Camera = Rig.GetComponent<Camera>();
                    _vitals = Rig.GetComponent<PlayerVitals>();

                    if (_vitals != null) _vitals.Damaged += OnDamaged;
                }
            }

            if (Controller == null)
            {
                Controller = FindAnyObjectByType<CaveRunController>();

                if (Controller != null)
                {
                    Controller.LevelUpOffered += OnLevelUpOffered;
                    Controller.LevelUpClosed += OnLevelUpClosed;
                    Controller.DeathShown += OnDeathShown;
                    Controller.Finished += OnFinished;
                }
            }

            if (Director == null) Director = FindAnyObjectByType<CaveRunDirector>();
            if (Queen == null) Queen = FindAnyObjectByType<CaveQueen>();
            if (Crowd == null) Crowd = FindAnyObjectByType<SpiderCrowd>();

            if (_feedback == null && Arsenal != null && Arsenal.Context != null)
            {
                _feedback = Arsenal.Context.Feedback;
                _feedback.Marked += OnMarked;
            }

            if (_flow == null && GameFlow.Instance != null)
            {
                _flow = GameFlow.Instance;
                _flow.StateChanged += OnStateChanged;
            }
        }

        private void Unbind()
        {
            if (_vitals != null) _vitals.Damaged -= OnDamaged;
            if (_feedback != null) _feedback.Marked -= OnMarked;
            if (_flow != null) _flow.StateChanged -= OnStateChanged;

            if (Controller != null)
            {
                Controller.LevelUpOffered -= OnLevelUpOffered;
                Controller.LevelUpClosed -= OnLevelUpClosed;
                Controller.DeathShown -= OnDeathShown;
                Controller.Finished -= OnFinished;
            }
        }

        // ------------------------------------------------------------------ кадр

        private void Update()
        {
            EnsureLinks();

            var clock = System.Diagnostics.Stopwatch.StartNew();

            Tick(Time.unscaledDeltaTime);

            LastTickMs = clock.Elapsed.TotalMilliseconds;
        }

        public void Tick(float deltaTime)
        {
            var state = _flow != null ? _flow.State : GameState.Playing;
            var inRun = state == GameState.Playing || state == GameState.LevelUp || state == GameState.Dead ||
                        state == GameState.Paused;

            Hud.SetVisible(inRun);

            if (inRun) Hud.Tick(deltaTime);

            Hints.Tick(deltaTime, state);

            if (Cards.IsOpen) Cards.Tick();
            if (Death.IsOpen) Death.Tick();

            // Пауза — Esc / кнопка; в карточках Esc ничего не делает (выбор обязателен).
            if (Rig != null && Rig.Commands.Pause && _flow != null)
            {
                if (state == GameState.Playing)
                {
                    _flow.SetPaused(true);
                    Rig.SetLooking(false);
                }
                else if (state == GameState.Paused)
                {
                    ResumeFromPause();
                }
            }
        }

        public void ResumeFromPause()
        {
            if (_flow == null) return;

            _flow.SetPaused(false);
            Rig?.SetLooking(true);
        }

        // ------------------------------------------------------------------ события

        private void OnStateChanged(GameState previous, GameState next)
        {
            Pause.SetOpen(next == GameState.Paused);

            if (next == GameState.Playing || next == GameState.Loading)
            {
                Death.SetOpen(false);
                Results.SetOpen(false);
            }

            HubScreen?.SetOpen(next == GameState.Hub);

            if (next == GameState.Playing) Platform.Current.GameplayStart();
            else Platform.Current.GameplayStop();
        }

        private void OnLevelUpOffered() => Cards.Show(Controller);

        private void OnLevelUpClosed() => Cards.SetOpen(false);

        private void OnDeathShown() => Death.Show(Controller);

        private void OnFinished(RunResults results)
        {
            Death.SetOpen(false);
            Cards.SetOpen(false);
            Results.Show(results);
        }

        private void OnMarked(HitMarker marker)
        {
            Hud.OnHitMarker(marker);

            if (Audio != null) Audio.Play(marker.Kill ? SoundId.KillTick : SoundId.HitTick, marker.Kill ? 0.45f : 0.3f);
        }

        private void OnDamaged(DamageHit hit, DamageResult result)
        {
            Hud.OnDamaged(hit, result, Rig != null ? Rig.transform : null);

            if (result.Dealt > 1f && Audio != null) Audio.Play(SoundId.Hurt, Mathf.Clamp01(0.3f + result.Dealt * 0.03f), 1f, null, 7);

            Hints.OnBitten();
        }

        private void HideScreens()
        {
            Cards.SetOpen(false);
            Death.SetOpen(false);
            Results.SetOpen(false);
            Pause.SetOpen(false);
        }
    }

    /// <summary>Экран, который открывается и закрывается целиком.</summary>
    public interface IUiScreen
    {
        bool IsOpen { get; }
        void SetOpen(bool open);
    }
}
