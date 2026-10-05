using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace MineGenerator.Catacombs
{
    /// <summary>Событие аналитики: имя, время от старта и поля «ключ=значение;…».</summary>
    public struct AnalyticsEvent
    {
        public string Name;
        public float Time;
        public string Fields;

        public override string ToString() => $"{Time:0.0} {Name} {Fields}";
    }

    /// <summary>Куда уходят события — GameAnalytics, статистика площадки или локальный журнал.</summary>
    public interface IAnalytics
    {
        void Send(in AnalyticsEvent e);
    }

    /// <summary>
    /// Аналитика (M6.2): событийный слой, без которого баланс — догадки. Куда отправлять, решает
    /// пользователь (README плана, «Вопросы»: GameAnalytics или только статистика площадок), поэтому
    /// по умолчанию — <see cref="LocalAnalytics"/>: журнал в памяти и счётчики, их видят прогоны
    /// и отладка. Отправщик встанет реализацией <see cref="IAnalytics"/> без правки мест вызова.
    ///
    /// Поля — строкой «ключ=значение;…»: любой бэкенд разберёт её в свои параметры, а места вызова
    /// не зависят от его типов. События редкие (забег, покупка, реклама) — аллокации здесь не в кадре.
    /// </summary>
    public static class Analytics
    {
        public static IAnalytics Sink = new LocalAnalytics();

        /// <summary>Реальное время готовности игры (<c>GameReady</c>) — от него меряется первый ввод.</summary>
        public static float ReadyAt { get; private set; } = -1f;

        private static bool _firstInput;
        private static readonly StringBuilder Fields = new StringBuilder(128);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnLoad()
        {
            Sink = new LocalAnalytics();
            ReadyAt = -1f;
            _firstInput = false;
        }

        public static void Event(string name, string fields = "")
        {
            Sink?.Send(new AnalyticsEvent { Name = name, Time = UnityEngine.Time.realtimeSinceStartup, Fields = fields ?? "" });
        }

        /// <summary>Сборщик полей: <c>Analytics.F("tier", 2).F("level", 9).Send("death")</c> — без интерполяции строк в местах вызова.</summary>
        public static FieldWriter F(string key, object value)
        {
            Fields.Clear();
            return new FieldWriter().F(key, value);
        }

        public readonly struct FieldWriter
        {
            public FieldWriter F(string key, object value)
            {
                if (Fields.Length > 0) Fields.Append(';');
                Fields.Append(key).Append('=').Append(Format(value));
                return this;
            }

            public void Send(string name) => Event(name, Fields.ToString());

            private static string Format(object value) => value switch
            {
                float f => f.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
                double d => d.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
                null => "",
                _ => value.ToString()
            };
        }

        /// <summary>Игру можно играть — событие загрузки (M6.2 <c>boot</c>).</summary>
        public static void Ready(string platform, string quality, string language)
        {
            if (ReadyAt >= 0f) return;

            ReadyAt = UnityEngine.Time.realtimeSinceStartup;
            F("load", ReadyAt).F("platform", platform).F("quality", quality).F("lang", language).Send("boot");
        }

        /// <summary>Первый ввод игрока — секунд от готовности: долгая пауза значит «не понял, что делать».</summary>
        public static void Input()
        {
            if (_firstInput || ReadyAt < 0f) return;

            _firstInput = true;
            F("after", UnityEngine.Time.realtimeSinceStartup - ReadyAt).Send("first_input");
        }
    }

    /// <summary>Журнал в памяти: последние 256 событий и счётчик по имени.</summary>
    public sealed class LocalAnalytics : IAnalytics
    {
        public const int Capacity = 256;

        public readonly List<AnalyticsEvent> Recent = new List<AnalyticsEvent>(Capacity);
        public readonly Dictionary<string, int> Counts = new Dictionary<string, int>();

        public void Send(in AnalyticsEvent e)
        {
            if (Recent.Count >= Capacity) Recent.RemoveAt(0);
            Recent.Add(e);

            Counts.TryGetValue(e.Name, out var count);
            Counts[e.Name] = count + 1;
        }

        public int Count(string name) => Counts.TryGetValue(name, out var count) ? count : 0;

        public AnalyticsEvent? Last(string name)
        {
            for (var i = Recent.Count - 1; i >= 0; i--)
            {
                if (Recent[i].Name == name) return Recent[i];
            }

            return null;
        }
    }
}
