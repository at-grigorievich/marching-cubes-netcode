using System;
using System.Runtime.InteropServices;

namespace MineGenerator.Catacombs
{
    /// <summary>
    /// CrazyGames, HTML5 SDK v3 — тонким <c>.jslib</c> (<c>Plugins/WebGL/CrazyGames.jslib</c>).
    /// План называл официальный Unity SDK v3; он тянет пакет со своими префабами и редакторным
    /// окном, а игре нужно восемь вызовов — те же, что у Яндекса. SDK подключает шаблон страницы.
    ///
    /// Сверено с документацией (октябрь 2026): <c>SDK.init</c>, <c>game.loadingStart/Stop</c>,
    /// <c>game.gameplayStart/Stop</c>, <c>ad.requestAd("midgame" | "rewarded", { adFinished, adError })</c>,
    /// <c>game.happytime</c>, <c>data.getItem/setItem</c>, <c>user.systemInfo</c> (язык и устройство).
    /// QA-инструментом SDK НЕ проверено — см. CLAUDE.md.
    ///
    /// У CrazyGames нет «закрыл раньше времени»: <c>adFinished</c> — досмотрено, <c>adError</c> — нет показа.
    /// </summary>
    public sealed class CrazyGamesPlatform : IPlatformServices
    {
        private string _language = "";
        private DeviceKind _device = Platform.EngineDevice();
        private Action<string> _cloudLoaded;
        private Action _ready;

        public string Name => "crazygames";

        public void Init(Action ready)
        {
            _ready = ready;
            PlatformBridge.SdkReady += OnSdkReady;
            PlatformBridge.CloudLoaded += OnCloudLoaded;

#if UNITY_WEBGL && !UNITY_EDITOR
            Native.CgInit();
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

        /// <summary>Загрузка кончилась — <c>loadingStop</c> (старт загрузки отмечает сам SDK при init).</summary>
        public void GameReady() => Native.CgLoading(0);

        public void GameplayStart() => Native.CgGameplay(1);

        public void GameplayStop() => Native.CgGameplay(0);

        public bool RewardedAvailable => true;

        public void ShowRewarded(string placement, Action<AdResult> done) => Native.CgRequestAd("rewarded", Platform.Await(placement, done));

        public void ShowInterstitial(string placement, Action<AdResult> done) => Native.CgRequestAd("midgame", Platform.Await(placement, done));

        public string Language => _language;

        public DeviceKind Device => _device;

        public void CloudLoad(Action<string> done)
        {
            _cloudLoaded = done;

#if UNITY_WEBGL && !UNITY_EDITOR
            Native.CgCloudLoad();
#else
            OnCloudLoaded("");
#endif
        }

        public void CloudSave(string json) => Native.CgCloudSave(json);

        /// <summary>Таблиц рекордов у CrazyGames в SDK нет.</summary>
        public void SubmitScore(string board, int value) { }

        public void Happytime() => Native.CgHappytime();

        private static class Native
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            [DllImport("__Internal")] public static extern void CgInit();
            [DllImport("__Internal")] public static extern void CgLoading(int loading);
            [DllImport("__Internal")] public static extern void CgGameplay(int playing);
            [DllImport("__Internal")] public static extern void CgRequestAd(string type, string token);
            [DllImport("__Internal")] public static extern void CgCloudLoad();
            [DllImport("__Internal")] public static extern void CgCloudSave(string json);
            [DllImport("__Internal")] public static extern void CgHappytime();
#else
            public static void CgLoading(int loading) { }
            public static void CgGameplay(int playing) { }
            public static void CgRequestAd(string type, string token) => Platform.DeliverAd(token + "|failed");
            public static void CgCloudSave(string json) { }
            public static void CgHappytime() { }
#endif
        }
    }
}
