using System.Collections.Generic;

namespace MineGenerator.Catacombs
{
    /// <summary>Событие стадии на шкале.</summary>
    public enum StageEventType
    {
        Elite,
        Swarm,
        Final
    }

    public struct StageEvent
    {
        public StageEventType Type;

        /// <summary>Когда начинается, секунды от начала стадии.</summary>
        public float Time;

        /// <summary>Где на шкале, доля стадии.</summary>
        public float Share;
    }

    /// <summary>
    /// Расписание стадии: элита на 0.3 и 0.8, рои серединой на гребнях кривой, финальный рой
    /// на конце. Чистая функция — ей пользуются и директор (когда запускать), и интерфейс
    /// (значки на шкале и предупреждение за 8 секунд). Раньше время событий считалось внутри
    /// директора, и шкале его было не узнать, не повторяя расчёт.
    /// </summary>
    public static class RunSchedule
    {
        public static void Build(float stageSeconds, RunCurve curve, float swarmSeconds, float firstElite,
            float secondElite, List<StageEvent> into)
        {
            into.Clear();

            Add(into, StageEventType.Elite, firstElite * stageSeconds, stageSeconds);
            Add(into, StageEventType.Swarm, SwarmStart(stageSeconds, curve.firstPeak, swarmSeconds), stageSeconds);
            Add(into, StageEventType.Swarm, SwarmStart(stageSeconds, curve.secondPeak, swarmSeconds), stageSeconds);
            Add(into, StageEventType.Elite, secondElite * stageSeconds, stageSeconds);
            Add(into, StageEventType.Final, stageSeconds, stageSeconds);

            into.Sort((a, b) => a.Time.CompareTo(b.Time));
        }

        /// <summary>Рой стоит серединой на гребне кривой.</summary>
        public static float SwarmStart(float stageSeconds, float peakShare, float swarmSeconds) =>
            peakShare * stageSeconds - swarmSeconds * 0.5f;

        private static void Add(List<StageEvent> into, StageEventType type, float time, float stage)
        {
            into.Add(new StageEvent { Type = type, Time = time, Share = stage > 0f ? time / stage : 0f });
        }

        /// <summary>Ближайшее событие после <paramref name="now"/>; false — событий больше нет.</summary>
        public static bool Next(List<StageEvent> schedule, float now, out StageEvent next)
        {
            foreach (var e in schedule)
            {
                if (e.Time <= now) continue;

                next = e;
                return true;
            }

            next = default;
            return false;
        }
    }
}
