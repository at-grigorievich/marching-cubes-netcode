using System;
using UnityEngine;

namespace MineGenerator.Catacombs
{
    /// <summary>Состояние игры целиком — не забега, а того, что на экране.</summary>
    public enum GameState
    {
        /// <summary>Старт: платформа, профиль, язык (наполняет M4–M5).</summary>
        Boot,

        /// <summary>Лагерь — мета-игра (M4).</summary>
        Hub,

        /// <summary>Строится уровень.</summary>
        Loading,

        Playing,

        Paused,

        /// <summary>Выбор улучшения (M1): время стоит.</summary>
        LevelUp,

        /// <summary>Ярус пройден, переход к следующему (M4).</summary>
        StageClear,

        /// <summary>Игрок пал (M1).</summary>
        Dead,

        /// <summary>Итоги забега (M1).</summary>
        Results
    }

    /// <summary>
    /// Машина состояний игры. Сейчас это скелет: старт сразу в забеге (как и было), загрузка
    /// уровня, игра и пауза. Лагерь, смерть, выбор улучшения и итоги добавят M1 и M4.
    ///
    /// Одна сцена: на WebGL загрузка сцены — лишние секунды и память, и лагерь будет
    /// интерфейсом поверх того же мира, а не отдельной сценой.
    ///
    /// Компонент заводится САМ при старте play-режима, а не хранится в сцене. Настраиваемых
    /// чисел у него нет, а то, что сцена хранит, расходится с кодом молча (грабли №8, №25,
    /// №67). Сам он ещё и ведёт часы (<see cref="GameClock.Tick"/>) — без него стоп-кадр
    /// не кончился бы никогда, поэтому зависеть от «Починить свет» ему нельзя.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class GameFlow : MonoBehaviour
    {
        public static GameFlow Instance { get; private set; }

        public GameState State { get; private set; } = GameState.Boot;

        /// <summary>Смена состояния: прежнее, новое.</summary>
        public event Action<GameState, GameState> StateChanged;

        /// <summary>Замедление смерти кончилось — пора показывать экран смерти.</summary>
        public event Action DeathSettled;

        private CatacombWorld _world;

        /// <summary>
        /// Заводит компонент в сцене с катакомбами, если его нет.
        ///
        /// Обычным объектом, БЕЗ <c>HideFlags.DontSave</c>: такой объект переживает выход
        /// из play-режима и остаётся висеть в сцене редактора. Объект, заведённый в игре,
        /// при выходе из неё уничтожается сам и в сцену не пишется.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (Instance != null) return;
            if (FindFirstObjectByType<CatacombWorld>() == null) return;

#if !UNITY_EDITOR
            // Числа и время — без культуры системы (M5.4): в турецкой локали «1,5» вместо «1.5»
            // и «İ» из «i» ломают разбор и подписи. Только в сборке: в редакторе это сменило бы
            // культуру потока самого редактора.
            System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
#endif

            new GameObject("Game Flow").AddComponent<GameFlow>();
        }

        /// <summary>Статика переживает выход из play-режима без перезагрузки домена.</summary>
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
            _world = FindFirstObjectByType<CatacombWorld>();

            // Площадка (M5): SDK поднимается с первым кадром — язык и облако нужны профилю,
            // а пауза от площадки (Яндекс game_api_pause) — часам.
            Platform.Initialize();
            Platform.PauseRequested += OnPlatformPause;
        }

        private void OnEnable()
        {
#if !UNITY_EDITOR
            // Только в сборке: в редакторе прогоны и съёмка кадров идут через CLI, пока окно
            // редактора НЕ в фокусе (CLAUDE.md, «Чем проверять»), и пауза по фокусу
            // останавливала бы их.
            Application.focusChanged += OnFocusChanged;
#endif
        }

        private void OnDisable()
        {
#if !UNITY_EDITOR
            Application.focusChanged -= OnFocusChanged;
#endif
        }

        private void OnDestroy()
        {
            Platform.PauseRequested -= OnPlatformPause;

            if (Instance != this) return;

            Instance = null;
            GameClock.ResetAll();
        }

        private void Start()
        {
            SetState(_world != null && _world.IsGenerating ? GameState.Loading : GameState.Playing);
        }

        private void Update()
        {
            GameClock.Tick(Time.unscaledDeltaTime);

            // Замедление смерти идёт по реальному времени: игровое под ним ползёт втрое медленнее.
            if (_dying >= 0f && State == GameState.Dead)
            {
                _dying += Time.unscaledDeltaTime;

                if (_dying >= DyingSeconds)
                {
                    _dying = -1f;
                    GameClock.Hold(ClockReason.Death, 0f);
                    DeathSettled?.Invoke();
                }
            }

            // Уровень перестраивается и сам (R на стенде, переход между ярусами в M4) —
            // опросом, а не событием: начало генерации события не имеет.
            var generating = _world != null && _world.IsGenerating;

            if (State == GameState.Playing && generating) SetState(GameState.Loading);
            else if (State == GameState.Loading && !generating) SetState(GameState.Playing);
        }

        /// <summary>Пауза и выход из неё — меню паузы (M1.9).</summary>
        public void SetPaused(bool paused)
        {
            if (paused && State == GameState.Playing)
            {
                GameClock.Hold(ClockReason.Pause);
                SetState(GameState.Paused);
            }
            else if (!paused && State == GameState.Paused)
            {
                GameClock.Release(ClockReason.Pause);
                SetState(GameState.Playing);
            }
        }

        public bool IsPlaying => State == GameState.Playing;

        /// <summary>Экран выбора карточки: время стоит, как в VS и Megabonk.</summary>
        public bool EnterLevelUp()
        {
            if (State != GameState.Playing) return false;

            GameClock.Hold(ClockReason.LevelUp);
            SetState(GameState.LevelUp);
            return true;
        }

        public void ExitLevelUp()
        {
            if (State != GameState.LevelUp) return;

            GameClock.Release(ClockReason.LevelUp);
            SetState(GameState.Playing);
        }

        private float _dying = -1f;

        /// <summary>Сколько секунд реального времени длится замедление смерти, и во сколько раз медленнее.</summary>
        public const float DyingSeconds = 0.6f;
        public const float DyingScale = 0.3f;

        /// <summary>Доля замедления смерти 0..1 — для оседания камеры и затемнения.</summary>
        public float DyingProgress => _dying < 0f ? (State == GameState.Dead || State == GameState.Results ? 1f : 0f)
            : Mathf.Clamp01(_dying / DyingSeconds);

        /// <summary>
        /// Смерть: замедление ×0.3 на 0.6 с реального времени (камера оседает, экран темнеет),
        /// затем время стоит и открывается экран смерти — возрождение или итоги.
        /// </summary>
        public void EnterDying()
        {
            if (State != GameState.Playing && State != GameState.LevelUp) return;

            if (State == GameState.LevelUp) GameClock.Release(ClockReason.LevelUp);

            _dying = 0f;
            GameClock.Hold(ClockReason.Death, DyingScale);
            SetState(GameState.Dead);
        }

        /// <summary>Возрождение: время снова идёт, игра продолжается.</summary>
        public void Revive()
        {
            if (State != GameState.Dead) return;

            _dying = -1f;
            GameClock.Release(ClockReason.Death);
            SetState(GameState.Playing);
        }

        /// <summary>Итоги забега: время стоит.</summary>
        public void EnterResults()
        {
            _dying = -1f;
            GameClock.Hold(ClockReason.Death, 0f);
            SetState(GameState.Results);
        }

        /// <summary>Ярус пройден — привал между ярусами (M4): время стоит.</summary>
        public void EnterStageClear()
        {
            if (State != GameState.Playing) return;

            GameClock.Hold(ClockReason.Pause);
            SetState(GameState.StageClear);
        }

        /// <summary>Лагерь (M4): время идёт (облёт камеры), забега нет.</summary>
        public void EnterHub()
        {
            _dying = -1f;
            GameClock.ResetGame();
            SetState(GameState.Hub);
        }

        /// <summary>
        /// Новый забег или следующий ярус: снять все причины остановки времени — генерация уровня
        /// переведёт в загрузку и потом в игру сама.
        /// </summary>
        public void BeginRun()
        {
            _dying = -1f;
            GameClock.ResetGame();
            SetState(_world != null && _world.IsGenerating ? GameState.Loading : GameState.Playing);
        }

        private void SetState(GameState next)
        {
            if (next == State) return;

            var previous = State;
            State = next;

            // Разметка площадки — отсюда и только отсюда (M5.2): геймплей — это Playing, всё
            // остальное (пауза, карточки, привал, итоги, лагерь, загрузка) — стоп. Готовность —
            // при первом показе того, во что можно играть.
            Platform.SetGameplay(next == GameState.Playing);
            if (next == GameState.Playing || next == GameState.Hub) Platform.MarkReady();

            StateChanged?.Invoke(previous, next);
        }

        private static void OnPlatformPause(bool paused)
        {
            if (paused) GameClock.Hold(ClockReason.Platform);
            else GameClock.Release(ClockReason.Platform);
        }

#if !UNITY_EDITOR
        private static void OnFocusChanged(bool focused)
        {
            if (focused) GameClock.Release(ClockReason.FocusLost);
            else GameClock.Hold(ClockReason.FocusLost);
        }
#endif
    }
}
