using System;

namespace MineGenerator.Catacombs
{
    public enum QualityTier
    {
        High,
        Medium,
        Low
    }

    /// <summary>
    /// Уровень качества (M5.6): таблица плана. Чистая логика — тесты вне Unity.
    ///
    /// **Население на «Низком» меньше — значит, и убийств меньше**, а опыт и хитин идут с убийств.
    /// Поэтому ценность ихора и шанс хитина растут обратно населению (<see cref="ValueScale"/>),
    /// живучесть особей не трогается: иначе телефонный игрок рос бы медленнее компьютерного.
    /// </summary>
    public readonly struct QualityPreset
    {
        public readonly QualityTier Tier;
        public readonly float RenderScale;
        public readonly int CrowdCapacity;
        public readonly float Population;
        public readonly bool Dust;
        public readonly float Particles;
        public readonly int ScorchSlots;
        public readonly float DrawDistance;

        public QualityPreset(QualityTier tier, float renderScale, int capacity, float population, bool dust, float particles,
            int scorchSlots, float drawDistance)
        {
            Tier = tier;
            RenderScale = renderScale;
            CrowdCapacity = capacity;
            Population = population;
            Dust = dust;
            Particles = particles;
            ScorchSlots = scorchSlots;
            DrawDistance = drawDistance;
        }

        /// <summary>Множитель опыта и шанса хитина — обратно населению.</summary>
        public float ValueScale => 1f / Math.Max(0.1f, Population);

        public static QualityPreset For(QualityTier tier) => tier switch
        {
            QualityTier.Medium => new QualityPreset(tier, 0.85f, 900, 0.85f, true, 0.75f, 16, 50f),
            QualityTier.Low => new QualityPreset(tier, 0.7f, 600, 0.6f, false, 0.5f, 8, 40f),
            _ => new QualityPreset(QualityTier.High, 1f, 1200, 1f, true, 1f, 16, 60f)
        };
    }

    /// <summary>Выбор уровня: настройка игрока, иначе по устройству; замер первых 10 с — ступень вниз один раз.</summary>
    public static class QualityPolicy
    {
        /// <summary>Средний кадр дольше — ступень вниз (M5.6): 26 мс — это уже меньше 40 к/с.</summary>
        public const float SlowFrameMs = 26f;

        /// <summary>Сколько секунд игры мерить перед решением.</summary>
        public const float MeasureSeconds = 10f;

        /// <summary>Настройка профиля: −1 — авто, иначе номер уровня.</summary>
        public static QualityTier Resolve(int setting, DeviceKind device, int memoryMb)
        {
            if (setting >= 0 && setting <= (int)QualityTier.Low) return (QualityTier)setting;

            return Detect(device, memoryMb);
        }

        /// <summary>Телефон — «Низкое», планшет — «Среднее», компьютер — «Высокое», если памяти не мало.</summary>
        public static QualityTier Detect(DeviceKind device, int memoryMb)
        {
            if (device == DeviceKind.Mobile) return QualityTier.Low;
            if (device == DeviceKind.Tablet) return QualityTier.Medium;

            // Chromebook с 4 ГБ — нижняя планка CrazyGames. Ноль — браузер память не сообщил.
            return memoryMb > 0 && memoryMb <= 4096 ? QualityTier.Medium : QualityTier.High;
        }

        /// <summary>Ступень вниз по замеру — или та же, если ниже некуда или кадр в норме.</summary>
        public static QualityTier StepDown(QualityTier tier, float averageFrameMs)
        {
            if (averageFrameMs <= SlowFrameMs || tier == QualityTier.Low) return tier;
            return tier + 1;
        }
    }
}
