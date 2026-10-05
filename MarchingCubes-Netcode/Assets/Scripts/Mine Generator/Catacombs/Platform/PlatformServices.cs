using System;
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

    /// <summary>
    /// Площадка: Yandex Games, CrazyGames или редактор. Игра зовёт только это — какая площадка
    /// за ним, знает одна реализация (M5).
    ///
    /// Требования обеих площадок, которые держит этот слой: реклама только через SDK, время и звук
    /// на время показа стоят (<see cref="ClockReason.Ad"/>), ошибка показа — игра продолжается,
    /// а кнопка «за награду» не врёт (награды без досмотра нет). Разметка геймплея строго
    /// по состоянию игры: <see cref="GameplayStart"/> в игре, <see cref="GameplayStop"/> — во всём остальном.
    /// </summary>
    public interface IPlatformServices
    {
        string Name { get; }

        /// <summary>Игра готова к игре — Яндекс LoadingAPI.ready, CrazyGames loadingStop.</summary>
        void GameReady();

        void GameplayStart();
        void GameplayStop();

        /// <summary>Есть ли сейчас реклама за награду (не показывать кнопку, если нет).</summary>
        bool RewardedAvailable { get; }

        void ShowRewarded(string placement, Action<AdResult> done);

        void ShowInterstitial(string placement, Action<AdResult> done);

        /// <summary>Язык из SDK площадки (ru, en, tr) или пусто.</summary>
        string Language { get; }

        /// <summary>Мобильное ли устройство — по SDK площадки.</summary>
        bool IsMobile { get; }

        /// <summary>Облачное сохранение (M4): прочитать строку профиля или пусто.</summary>
        void CloudLoad(Action<string> done);

        void CloudSave(string json);

        void SubmitScore(string board, int value);

        /// <summary>CrazyGames happytime — после победы над Маткой.</summary>
        void Happytime();
    }

    /// <summary>
    /// Площадка для игры. В редакторе — заглушка: реклама «показывается» мгновенно и считается
    /// досмотренной (решение — <see cref="EditorPlatform.NextAd"/>), сохранение — в файл.
    /// Реализации площадок (M5) подставляются при старте по define сборки.
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

        public static void Set(IPlatformServices services) => _current = services;

        /// <summary>Сколько раз за жизнь игры показывалась реклама — для прогонов и частоты.</summary>
        public static int AdsShown { get; private set; }

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

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnLoad()
        {
            _current = null;
            AdsShown = 0;
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

        public string Name => "editor";

        public void GameReady() { }

        public bool InGameplay { get; private set; }

        public void GameplayStart() => InGameplay = true;

        public void GameplayStop() => InGameplay = false;

        public bool RewardedAvailable => RewardedReady;

        public void ShowRewarded(string placement, Action<AdResult> done) => done?.Invoke(NextAd);

        public void ShowInterstitial(string placement, Action<AdResult> done) => done?.Invoke(NextAd);

        public string Language => "";

        public bool IsMobile => Application.isMobilePlatform;

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
