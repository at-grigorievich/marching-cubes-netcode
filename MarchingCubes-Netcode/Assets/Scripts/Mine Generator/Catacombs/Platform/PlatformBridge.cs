using System;
using UnityEngine;

namespace MineGenerator.Catacombs
{
    /// <summary>
    /// Приёмник ответов SDK из JS. Шаблон страницы зовёт <c>unityInstance.SendMessage("PlatformBridge", …)</c>,
    /// а SendMessage находит объект по имени — поэтому имя постоянное и объект один на всю игру.
    ///
    /// Сам ничего не решает: раздаёт ответы фасаду <see cref="Platform"/> и текущей площадке.
    /// Колбэки SDK приходят из цикла событий браузера, то есть между кадрами, на главном потоке, —
    /// обрабатывать их можно сразу.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PlatformBridge : MonoBehaviour
    {
        public const string ObjectName = "PlatformBridge";

        private static PlatformBridge _instance;

        /// <summary>Ответ поднятого SDK: «язык|устройство».</summary>
        internal static event Action<string> SdkReady;

        /// <summary>Облачный профиль пришёл (может быть пустым).</summary>
        internal static event Action<string> CloudLoaded;

        public static void Ensure()
        {
            if (_instance != null) return;

            var go = new GameObject(ObjectName);
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<PlatformBridge>();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            _instance = null;
            SdkReady = null;
            CloudLoaded = null;
        }

        // Имена методов — контракт с .jslib и шаблонами страниц.

        public void OnSdkReady(string payload) => SdkReady?.Invoke(payload ?? "");

        public void OnAdDone(string payload) => Platform.DeliverAd(payload);

        public void OnCloudLoaded(string json) => CloudLoaded?.Invoke(json ?? "");

        /// <summary>Площадка просит паузу: «1» — пауза, «0» — снять.</summary>
        public void OnPlatformPause(string value) => Platform.RequestPause(value == "1");

        /// <summary>
        /// Вкладка скрыта или показана (<c>visibilitychange</c>). Фокус окна на вебе ловит не всё:
        /// свёрнутая вкладка на телефоне фокус не отдаёт, а звук играть не должен (требование Яндекса).
        /// </summary>
        public void OnVisibility(string value)
        {
            if (value == "0") GameClock.Hold(ClockReason.Hidden);
            else GameClock.Release(ClockReason.Hidden);
        }
    }
}
