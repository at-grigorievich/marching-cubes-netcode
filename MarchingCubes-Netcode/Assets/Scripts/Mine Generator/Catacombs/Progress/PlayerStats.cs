using System;

namespace MineGenerator.Catacombs
{
    /// <summary>
    /// Стат игрока. Значение — сумма вкладов всех источников (класс, мета, тома, эволюции).
    /// Как сумма читается (процент или единицы), решает <see cref="PlayerStats"/>.
    /// Новые статы — в конец: номер пишется в сохранения профиля.
    /// </summary>
    public enum Stat
    {
        /// <summary>Урон, доля: 0.1 — +10%.</summary>
        Damage,

        /// <summary>Скорострельность, доля.</summary>
        FireRate,

        /// <summary>Площадь: радиус взрыва, конус, дуга, доля.</summary>
        Area,

        /// <summary>Магазин, топливо, батарея, доля.</summary>
        Capacity,

        /// <summary>Ускорение перезарядки, доля: 0.08 — на 8% быстрее.</summary>
        ReloadSpeed,

        /// <summary>Шанс крита, доля.</summary>
        Crit,

        /// <summary>Броня, единицы: вычитается из каждого укуса.</summary>
        Armor,

        /// <summary>Максимум здоровья, единицы HP сверх базы.</summary>
        MaxHealthFlat,

        /// <summary>Максимум здоровья, доля — поверх единиц.</summary>
        MaxHealthPct,

        /// <summary>Регенерация, HP в секунду.</summary>
        Regen,

        /// <summary>Скорость ходьбы, доля.</summary>
        MoveSpeed,

        /// <summary>Заряды рывка сверх базовых, единицы.</summary>
        DashCharges,

        /// <summary>Радиус магнита ихора, доля.</summary>
        Magnet,

        /// <summary>Опыт, доля.</summary>
        Xp,

        /// <summary>Хитин, доля.</summary>
        Chitin,

        /// <summary>Удача: шанс четвёртой карточки, доля.</summary>
        Luck,

        /// <summary>Отброс, доля.</summary>
        Knockback,

        /// <summary>Сила статусов: горение, замедление, ток, доля.</summary>
        StatusPower,

        /// <summary>Дальность фонаря, доля.</summary>
        Lamp,

        /// <summary>Возрождения, единицы.</summary>
        Revives,

        /// <summary>Перебросы карточек, единицы за забег.</summary>
        Rerolls,

        /// <summary>Пропуски карточек, единицы за забег.</summary>
        Skips,

        /// <summary>Изгнания карточек, единицы за забег.</summary>
        Banishes,

        /// <summary>Указатель логова с начала стадии (мета «Чутьё логова»), флаг: больше нуля — есть.</summary>
        LairSense,

        /// <summary>Восполнение монет револьвера, доля (класс «Стрелок»).</summary>
        CoinRecharge,

        /// <summary>Невосприимчивость к огню, флаг (класс «Огнеборец»).</summary>
        FireImmune,

        /// <summary>Лишние громоотводы тесла-ружья, единицы (класс «Электрик»).</summary>
        ExtraRods,

        /// <summary>Пробой кирки без отката раз в 30 с, флаг (класс «Шахтёр»).</summary>
        FreeBreach,

        Count
    }

    /// <summary>
    /// Итоговые статы игрока: класс + мета + тома + эволюции → множители.
    ///
    /// Пушки читают только это, а не тома напрямую: тогда новый источник стата (предмет,
    /// мутатор недели) не требует правки ни одной пушки. Чистый класс — сложение статов
    /// проверяется тестами вне Unity.
    /// </summary>
    public sealed class PlayerStats
    {
        /// <summary>Базовые заряды рывка.</summary>
        public const int BaseDashCharges = 2;

        /// <summary>Базовый радиус магнита ихора, юниты.</summary>
        public const float BaseMagnet = 3f;

        private readonly float[] _sum = new float[(int)Stat.Count];

        /// <summary>Сколько раз статы пересчитывались — подписчики видят, что пора перечитать.</summary>
        public int Version { get; private set; }

        public void Clear()
        {
            Array.Clear(_sum, 0, _sum.Length);
            Version++;
        }

        public void Add(Stat stat, float value)
        {
            if (stat == Stat.Count) return;

            _sum[(int)stat] += value;
            Version++;
        }

        public float Get(Stat stat) => stat == Stat.Count ? 0f : _sum[(int)stat];

        public void CopyFrom(PlayerStats other)
        {
            Array.Copy(other._sum, _sum, _sum.Length);
            Version++;
        }

        public float DamageScale => Math.Max(0.05f, 1f + Get(Stat.Damage));

        public float FireRateScale => Math.Max(0.1f, 1f + Get(Stat.FireRate));

        public float AreaScale => Math.Max(0.1f, 1f + Get(Stat.Area));

        public float CapacityScale => Math.Max(0.1f, 1f + Get(Stat.Capacity));

        /// <summary>Множитель ВРЕМЕНИ перезарядки: меньше — быстрее. Не быстрее трети.</summary>
        public float ReloadTimeScale => Math.Max(0.3f, 1f - Get(Stat.ReloadSpeed));

        public float CritChance => Math.Min(1f, Math.Max(0f, Get(Stat.Crit)));

        public float Armor => Math.Max(0f, Get(Stat.Armor));

        public float Regen => Math.Max(0f, Get(Stat.Regen));

        public float MoveScale => Math.Max(0.2f, 1f + Get(Stat.MoveSpeed));

        public int DashCharges => Math.Max(1, BaseDashCharges + (int)Math.Round(Get(Stat.DashCharges)));

        public float MagnetRadius => BaseMagnet * Math.Max(0.1f, 1f + Get(Stat.Magnet));

        public float XpScale => Math.Max(0f, 1f + Get(Stat.Xp));

        public float ChitinScale => Math.Max(0f, 1f + Get(Stat.Chitin));

        public float Luck => Math.Max(0f, Get(Stat.Luck));

        public float KnockbackScale => Math.Max(0f, 1f + Get(Stat.Knockback));

        public float StatusScale => Math.Max(0f, 1f + Get(Stat.StatusPower));

        public float LampScale => Math.Max(0.2f, 1f + Get(Stat.Lamp));

        public int Revives => Math.Max(0, (int)Math.Round(Get(Stat.Revives)));

        public int Rerolls => Math.Max(0, (int)Math.Round(Get(Stat.Rerolls)));

        public int Skips => Math.Max(0, (int)Math.Round(Get(Stat.Skips)));

        public int Banishes => Math.Max(0, (int)Math.Round(Get(Stat.Banishes)));

        public bool Has(Stat flag) => Get(flag) > 0f;

        /// <summary>Максимум здоровья: (база + единицы) × (1 + доля).</summary>
        public float MaxHealth(float baseHealth) =>
            Math.Max(1f, (baseHealth + Get(Stat.MaxHealthFlat)) * Math.Max(0.1f, 1f + Get(Stat.MaxHealthPct)));
    }
}
