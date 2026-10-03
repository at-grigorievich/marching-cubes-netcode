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

            // Уровень перестраивается и сам (R на стенде, переход между ярусами в M4) —
            // опросом, а не событием: начало генерации события не имеет.
            var generating = _world != null && _world.IsGenerating;

            if (State == GameState.Playing && generating) SetState(GameState.Loading);
            else if (State == GameState.Loading && !generating) SetState(GameState.Playing);
        }

        /// <summary>Пауза и выход из неё. Меню паузы появится в M1.</summary>
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

        private void SetState(GameState next)
        {
            if (next == State) return;

            var previous = State;
            State = next;

            StateChanged?.Invoke(previous, next);
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
