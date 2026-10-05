using System;
using System.Runtime.InteropServices;

namespace MineGenerator.Catacombs
{
    /// <summary>
    /// Yandex Games, SDK v2 — тонким <c>.jslib</c> (<c>Plugins/WebGL/YandexGames.jslib</c>), без пакетов:
    /// нужно десяток вызовов, и пакет-обёртка тянул бы в сборку свой интерфейс и свои сцены.
    /// SDK подключает шаблон страницы (<c>/sdk.js</c>), ответы приходят через <see cref="PlatformBridge"/>.
    ///
    /// Сверено с документацией (октябрь 2026): <c>YaGames.init</c>, <c>LoadingAPI.ready</c>,
    /// <c>GameplayAPI.start/stop</c>, <c>adv.showFullscreenAdv</c>, <c>adv.showRewardedVideo</c>
    /// (награда — по <c>onRewarded</c>, не по закрытию), <c>getPlayer().getData/setData</c>,
    /// события <c>game_api_pause/resume</c>. В песочнице Яндекса НЕ проверено — см. CLAUDE.md.
    /// </summary>
    public sealed class YandexPlatform : IPlatformServices
    {
        private string _language = "";
        private DeviceKind _device = Platform.EngineDevice();
        private Action<string> _cloudLoaded;
        private Action _ready;

        public string Name => "yandex";

        public void Init(Action ready)
        {
            _ready = ready;
            PlatformBridge.SdkReady += OnSdkReady;
            PlatformBridge.CloudLoaded += OnCloudLoaded;

#if UNITY_WEBGL && !UNITY_EDITOR
            Native.YaInit();
#else
            OnSdkReady("");
#endif
        }

        private void OnSdkReady(string payload)
        {
            PlatformBridge.SdkReady -= OnSdkReady;

            var parts = payload.Split('|');
            _language = Platform.ParseLanguage(parts[0]);
            _device = Platform.ParseDevice(parts.Length > 1 ? parts[1] : "", _device);

            var ready = _ready;
            _ready = null;
            ready?.Invoke();
        }

        private void OnCloudLoaded(string json)
        {
            var done = _cloudLoaded;
            _cloudLoaded = null;
            done?.Invoke(json);
        }

        public void GameReady() => Native.YaReady();

        public void GameplayStart() => Native.YaGameplay(1);

        public void GameplayStop() => Native.YaGameplay(0);

        /// <summary>У Яндекса реклама за награду есть всегда; незаполненный показ вернётся ошибкой.</summary>
        public bool RewardedAvailable => true;

        public void ShowRewarded(string placement, Action<AdResult> done) => Native.YaShowRewarded(Platform.Await(placement, done));

        public void ShowInterstitial(string placement, Action<AdResult> done) => Native.YaShowFullscreen(Platform.Await(placement, done));

        public string Language => _language;

        public DeviceKind Device => _device;

        public void CloudLoad(Action<string> done)
        {
            _cloudLoaded = done;

#if UNITY_WEBGL && !UNITY_EDITOR
            Native.YaCloudLoad();
#else
            OnCloudLoaded("");
#endif
        }

        public void CloudSave(string json) => Native.YaCloudSave(json);

        public void SubmitScore(string board, int value) => Native.YaSubmitScore(board, value);

        public void Happytime() { }

        private static class Native
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            [DllImport("__Internal")] public static extern void YaInit();
            [DllImport("__Internal")] public static extern void YaReady();
            [DllImport("__Internal")] public static extern void YaGameplay(int playing);
            [DllImport("__Internal")] public static extern void YaShowFullscreen(string token);
            [DllImport("__Internal")] public static extern void YaShowRewarded(string token);
            [DllImport("__Internal")] public static extern void YaCloudLoad();
            [DllImport("__Internal")] public static extern void YaCloudSave(string json);
            [DllImport("__Internal")] public static extern void YaSubmitScore(string board, int value);
#else
            public static void YaReady() { }
            public static void YaGameplay(int playing) { }
            public static void YaShowFullscreen(string token) => Platform.DeliverAd(token + "|failed");
            public static void YaShowRewarded(string token) => Platform.DeliverAd(token + "|failed");
            public static void YaCloudSave(string json) { }
            public static void YaSubmitScore(string board, int value) { }
#endif
        }
    }
}
