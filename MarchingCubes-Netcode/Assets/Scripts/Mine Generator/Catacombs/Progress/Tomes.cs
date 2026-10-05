using System;

namespace MineGenerator.Catacombs
{
    /// <summary>
    /// Тома — пассивные статы забега (книги Vampire Survivors). Порядок фиксирован: номер
    /// пишется в снимок забега и в пары эволюций пушек.
    /// </summary>
    public enum TomeId
    {
        /// <summary>Сила: +10% урона.</summary>
        Might,

        /// <summary>Темп: +8% скорострельности.</summary>
        Haste,

        /// <summary>Охват: +10% площади.</summary>
        Reach,

        /// <summary>Запас: +15% магазина, −8% перезарядки.</summary>
        Supply,

        /// <summary>Меткость: +5% шанс крита.</summary>
        Precision,

        /// <summary>Панцирь: +1 броня.</summary>
        Carapace,

        /// <summary>Живучесть: +20 к максимуму здоровья.</summary>
        Vitality,

        /// <summary>Регенерация: +0.4 HP/с.</summary>
        Regeneration,

        /// <summary>Проворство: +6% скорости, на 3-м уровне +1 заряд рывка.</summary>
        Agility,

        /// <summary>Магнит: +30% радиуса.</summary>
        Magnet,

        /// <summary>Чутьё: +10% опыта.</summary>
        Insight,

        /// <summary>Жадность: +12% хитина.</summary>
        Greed,

        Count
    }

    /// <summary>Описание тома: ключи текста и что он даёт за уровень.</summary>
    public readonly struct TomeDef
    {
        public readonly TomeId Id;
        public readonly string Key;
        public readonly Stat Stat;
        public readonly float PerLevel;

        /// <summary>Второй стат (у «Запаса» — ускорение перезарядки), или <see cref="Stat.Count"/>.</summary>
        public readonly Stat Second;
        public readonly float SecondPerLevel;

        public TomeDef(TomeId id, string key, Stat stat, float perLevel, Stat second = Stat.Count, float secondPerLevel = 0f)
        {
            Id = id;
            Key = key;
            Stat = stat;
            PerLevel = perLevel;
            Second = second;
            SecondPerLevel = secondPerLevel;
        }

        public string NameKey => "tome." + Key + ".name";
        public string DescKey => "tome." + Key + ".desc";
    }

    /// <summary>
    /// Таблица томов (план M1.7). Чистые данные в коде, а не ассет: таблица короткая, её числа
    /// проверяет тест, а ассет в сцене расходился бы с кодом молча (грабли №8).
    /// </summary>
    public static class Tomes
    {
        public const int MaxLevel = 5;

        /// <summary>Слотов под тома в забеге.</summary>
        public const int Slots = 4;

        private static readonly TomeDef[] Table =
        {
            new TomeDef(TomeId.Might, "might", Stat.Damage, 0.10f),
            new TomeDef(TomeId.Haste, "haste", Stat.FireRate, 0.08f),
            new TomeDef(TomeId.Reach, "reach", Stat.Area, 0.10f),
            new TomeDef(TomeId.Supply, "supply", Stat.Capacity, 0.15f, Stat.ReloadSpeed, 0.08f),
            new TomeDef(TomeId.Precision, "precision", Stat.Crit, 0.05f),
            new TomeDef(TomeId.Carapace, "carapace", Stat.Armor, 1f),
            new TomeDef(TomeId.Vitality, "vitality", Stat.MaxHealthFlat, 20f),
            new TomeDef(TomeId.Regeneration, "regen", Stat.Regen, 0.4f),
            new TomeDef(TomeId.Agility, "agility", Stat.MoveSpeed, 0.06f),
            new TomeDef(TomeId.Magnet, "magnet", Stat.Magnet, 0.30f),
            new TomeDef(TomeId.Insight, "insight", Stat.Xp, 0.10f),
            new TomeDef(TomeId.Greed, "greed", Stat.Chitin, 0.12f)
        };

        public static TomeDef Get(TomeId id) => Table[(int)id];

        public static int Count => Table.Length;

        /// <summary>Вклад тома уровня <paramref name="level"/> в статы.</summary>
        public static void Apply(TomeId id, int level, PlayerStats stats)
        {
            if (level <= 0) return;

            level = Math.Min(level, MaxLevel);

            var def = Get(id);

            stats.Add(def.Stat, def.PerLevel * level);
            if (def.Second != Stat.Count) stats.Add(def.Second, def.SecondPerLevel * level);

            // Проворство на третьем уровне даёт заряд рывка: подвижность — главная защита,
            // и рост её в штуках читается сильнее, чем шесть процентов скорости.
            if (id == TomeId.Agility && level >= 3) stats.Add(Stat.DashCharges, 1f);
        }
    }
}
