using System;
using System.Collections.Generic;
using UnityEngine;

namespace MineGenerator.Catacombs
{
    /// <summary>Чем кончился показ рекламы.</summary>
    public enum AdResult
    {
        /// <summary>Досмотрена: награда положена.</summary>
        Completed,

        /// <summary>Закрыта раньше времени: награды нет.</summary>
        Skipped,

        /// <summary>Ошибка или незаполненный показ: игра продолжается, награды нет.</summary>
        Failed
    }

    /// <summary>Тип устройства — по SDK площадки, без него — по движку.</summary>
    public enum DeviceKind
    {
        Desktop,
        Mobile,
        Tablet
    }

    /// <summary>
    /// Площадка: Yandex Games, CrazyGames или редактор. Игра зовёт только это — какая площадка
    /// за ним, знает одна реализация (M5).
    ///
    /// Требования обеих площадок, которые держит этот слой: реклама только через SDK, время и звук
    /// на время показа стоят (<see cref="ClockReason.Ad"/>), ошибка показа — игра продолжается,
    /// а кнопка «за награду» не врёт (награды без досмотра нет). Разметка геймплея строго
    /// по состоянию игры: <see cref="GameplayStart"/> в игре, <see cref="GameplayStop"/> — во всём остальном.
    ///
    /// Реализации отвечают асинхронно: SDK живёт в JS, и ответ приходит через <see cref="PlatformBridge"/>
    /// в один из следующих кадров. Звать <c>done</c> они обязаны ровно один раз.
    /// </summary>
    public interface IPlatformServices
    {
        string Name { get; }

        /// <summary>Поднять SDK. <paramref name="ready"/> — когда язык и устройство известны (или SDK нет).</summary>
        void Init(Action ready);

        /// <summary>Игра готова к игре — Яндекс LoadingAPI.ready, CrazyGames loadingStop.</summary>
        void GameReady();

        void GameplayStart();
        void GameplayStop();

        /// <summary>Есть ли сейчас реклама за награду (не показывать кнопку, если нет).</summary>
        bool RewardedAvailable { get; }

        void ShowRewarded(string placement, Action<AdResult> done);

        void ShowInterstitial(string placement, Action<AdResult> done);

        /// <summary>Язык из SDK площадки (ru, en, tr…) или пусто.</summary>
        string Language { get; }

        DeviceKind Device { get; }

        /// <summary>Облачное сохранение (M4): прочитать строку профиля или пусто.</summary>
        void CloudLoad(Action<string> done);

        void CloudSave(string json);

        void SubmitScore(string board, int value);

        /// <summary>CrazyGames happytime — после победы над Маткой.</summary>
        void Happytime();
    }

    /// <summary>
    /// Площадка для игры. В редакторе — заглушка: реклама «показывается» мгновенно и считается
    /// досмотренной (решение — <see cref="EditorPlatform.NextAd"/>). Реализации площадок
    /// подставляются по define сборки (<c>PLATFORM_YANDEX</c>, <c>PLATFORM_CRAZYGAMES</c>).
    /// </summary>
    public static class Platform
    {
        private static IPlatformServices _current;

        public static IPlatformServices Current
        {
            get
            {
                if (_current == null) _current = Create();
                return _current;
            }
        }

        public static void Set(IPlatformServices services)
        {
            _current = services;
            _gameplay = null;
            Ready = false;
            _readySent = false;
        }

        /// <summary>Сколько раз за жизнь игры показывалась реклама — для прогонов и частоты.</summary>
        public static int AdsShown { get; private set; }

        /// <summary>SDK поднят: язык и устройство известны.</summary>
        public static bool Ready { get; private set; }

        /// <summary>SDK поднялся — язык площадки можно применять.</summary>
        public static event Action Readied;

        /// <summary>Площадка просит паузу (Яндекс game_api_pause) или снимает её.</summary>
        public static event Action<bool> PauseRequested;

        public static bool IsMobile => Current.Device != DeviceKind.Desktop;

        private static bool _initStarted;
        private static bool _readySent;
        private static bool? _gameplay;

        /// <summary>Поднять SDK один раз за жизнь игры. Зовёт GameFlow при старте.</summary>
        public static void Initialize()
        {
            if (_initStarted) return;
            _initStarted = true;

            PlatformBridge.Ensure();

            Current.Init(() =>
            {
                Ready = true;
                Readied?.Invoke();
            });
        }

        /// <summary>
        /// Игру можно играть: первый показ лагеря или первого яруса. Ровно один раз — второй
        /// <c>LoadingAPI.ready</c> Яндекс считает ошибкой разметки.
        /// </summary>
        public static void MarkReady()
        {
            if (_readySent) return;
            _readySent = true;
            Current.GameReady();
        }

        /// <summary>
        /// Разметка геймплея — идемпотентно: состояние меняют и поток игры, и контроллер забега,
        /// а повторный <c>gameplayStart</c> подряд обе площадки считают ошибкой.
        /// </summary>
        public static void SetGameplay(bool playing)
        {
            if (_gameplay == playing) return;
            _gameplay = playing;

            if (playing) Current.GameplayStart();
            else Current.GameplayStop();
        }

        /// <summary>
        /// Показать рекламу за награду: время и звук стоят на время показа (обе площадки этого
        /// требуют), награда — только при досмотре.
        /// </summary>
        public static void Rewarded(string placement, Action<bool> rewarded)
        {
            GameClock.Hold(ClockReason.Ad);
            AdsShown++;

            Current.ShowRewarded(placement, result =>
            {
                GameClock.Release(ClockReason.Ad);
                rewarded?.Invoke(result == AdResult.Completed);
            });
        }

        public static void Interstitial(string placement, Action done)
        {
            GameClock.Hold(ClockReason.Ad);
            AdsShown++;

            Current.ShowInterstitial(placement, _ =>
            {
                GameClock.Release(ClockReason.Ad);
                done?.Invoke();
            });
        }

        // ------------------------------------------------------------------ ответы из JS

        private static readonly Dictionary<string, Action<AdResult>> Pending = new Dictionary<string, Action<AdResult>>();
        private static int _token;

        /// <summary>Запомнить, кому отдать ответ показа; вернуть метку, которая поедет в JS и обратно.</summary>
        internal static string Await(string placement, Action<AdResult> done)
        {
            var token = placement + "#" + ++_token;
            Pending[token] = done;
            return token;
        }

        /// <summary>Ответ показа пришёл: «метка|completed/skipped/failed».</summary>
        internal static void DeliverAd(string payload)
        {
            var split = (payload ?? "").Split('|');
            var token = split[0];
            var result = split.Length > 1 ? ParseResult(split[1]) : AdResult.Failed;

            if (!Pending.TryGetValue(token, out var done)) return;

            Pending.Remove(token);
            done?.Invoke(result);
        }

        internal static void RequestPause(bool paused) => PauseRequested?.Invoke(paused);

        private static AdResult ParseResult(string value) => value switch
        {
            "completed" => AdResult.Completed,
            "skipped" => AdResult.Skipped,
            _ => AdResult.Failed
        };

        /// <summary>Тип устройства по строке SDK: desktop / mobile / tablet / tv.</summary>
        public static DeviceKind ParseDevice(string value, DeviceKind fallback) => (value ?? "").ToLowerInvariant() switch
        {
            "mobile" => DeviceKind.Mobile,
            "tablet" => DeviceKind.Tablet,
            "desktop" => DeviceKind.Desktop,
            "tv" => DeviceKind.Desktop,
            _ => fallback
        };

        /// <summary>Код языка по строке SDK: «ru», «en-US», «tr_TR» → ru, en, tr.</summary>
        public static string ParseLanguage(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";

            var code = value.Length >= 2 ? value.Substring(0, 2) : value;
            return code.ToLowerInvariant();
        }

        internal static DeviceKind EngineDevice() => Application.isMobilePlatform ? DeviceKind.Mobile : DeviceKind.Desktop;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnLoad()
        {
            _current = null;
            AdsShown = 0;
            Ready = false;
            Readied = null;
            PauseRequested = null;
            _initStarted = false;
            _readySent = false;
            _gameplay = null;
            Pending.Clear();
        }

        private static IPlatformServices Create()
        {
#if PLATFORM_YANDEX && UNITY_WEBGL && !UNITY_EDITOR
            return new YandexPlatform();
#elif PLATFORM_CRAZYGAMES && UNITY_WEBGL && !UNITY_EDITOR
            return new CrazyGamesPlatform();
#else
            return new EditorPlatform();
#endif
        }
    }

    /// <summary>
    /// Заглушка площадки для редактора и прогонов: реклама отвечает сразу тем, что задано
    /// (<see cref="NextAd"/>) — так прогон проверяет и досмотр, и ошибку, и отмену.
    /// Облако — в PlayerPrefs под отдельным ключом: проверка слияния локального и облачного.
    /// </summary>
    public sealed class EditorPlatform : IPlatformServices
    {
        private const string CloudKey = "MineGenerator.Catacombs.EditorCloud";

        /// <summary>Чем кончится следующий показ.</summary>
        public static AdResult NextAd = AdResult.Completed;

        public static bool RewardedReady = true;

        /// <summary>Сколько раз размечался старт и стоп геймплея — прогон проверяет идемпотентность.</summary>
        public static int GameplayStarts;
        public static int GameplayStops;
        public static int ReadyCalls;

        public string Name => "editor";

        public void Init(Action ready) => ready?.Invoke();

        public void GameReady() => ReadyCalls++;

        public bool InGameplay { get; private set; }

        public void GameplayStart()
        {
            InGameplay = true;
            GameplayStarts++;
        }

        public void GameplayStop()
        {
            InGameplay = false;
            GameplayStops++;
        }

        public bool RewardedAvailable => RewardedReady;

        public void ShowRewarded(string placement, Action<AdResult> done) => done?.Invoke(NextAd);

        public void ShowInterstitial(string placement, Action<AdResult> done) => done?.Invoke(NextAd);

        public string Language => "";

        public DeviceKind Device => Platform.EngineDevice();

        public void CloudLoad(Action<string> done) => done?.Invoke(PlayerPrefs.GetString(CloudKey, ""));

        public void CloudSave(string json)
        {
            PlayerPrefs.SetString(CloudKey, json);
            PlayerPrefs.Save();
        }

        public void SubmitScore(string board, int value) { }

        public void Happytime() { }
    }
}
