using System;
using UnityEngine;

namespace MineGenerator.Catacombs
{
    /// <summary>
    /// Восемь пушек. Порядок фиксирован: номер пишется в профиль (арсенал, статистика)
    /// и в снимок забега; источник урона — по нему же (<see cref="WeaponIds.SourceOf"/>).
    /// </summary>
    public enum WeaponId
    {
        /// <summary>«Барабан» — многозарядный гранатомёт.</summary>
        Launcher,

        /// <summary>«Монета» — револьвер с монетками (Marksman из Ultrakill).</summary>
        Revolver,

        /// <summary>«Ядро» — дробовик с выбросом ядра.</summary>
        Shotgun,

        /// <summary>«Гвоздомёт» с магнитом.</summary>
        Nailgun,

        /// <summary>«Прострел» — рельсотрон.</summary>
        Rail,

        /// <summary>«Напалм» — огнемёт.</summary>
        Flamer,

        /// <summary>«Дуга» — тесла-ружьё.</summary>
        Tesla,

        /// <summary>«Забой» — кирка-молот.</summary>
        PickHammer,

        Count
    }

    public static class WeaponIds
    {
        /// <summary>Источник урона пушки: гранатомёт остался под прежним номером, прочие — подряд за миром.</summary>
        public static DamageSource SourceOf(WeaponId id) =>
            id == WeaponId.Launcher ? DamageSource.GrenadeLauncher : (DamageSource)((int)DamageSource.Environment + (int)id);

        /// <summary>Пушка по источнику урона или <see cref="WeaponId.Count"/>, если это не пушка.</summary>
        public static WeaponId FromSource(DamageSource source)
        {
            if (source == DamageSource.GrenadeLauncher) return WeaponId.Launcher;

            var id = (int)source - (int)DamageSource.Environment;
            return id >= 1 && id < (int)WeaponId.Count ? (WeaponId)id : WeaponId.Count;
        }

        public static string Key(WeaponId id) => id switch
        {
            WeaponId.Launcher => "launcher",
            WeaponId.Revolver => "revolver",
            WeaponId.Shotgun => "shotgun",
            WeaponId.Nailgun => "nailgun",
            WeaponId.Rail => "rail",
            WeaponId.Flamer => "flamer",
            WeaponId.Tesla => "tesla",
            WeaponId.PickHammer => "hammer",
            _ => "none"
        };
    }

    /// <summary>
    /// Числа пушки. Смысл у каждой свой (у гранатомёта «площадь» — радиус взрыва, у дробовика —
    /// полуугол конуса), а сводятся они одинаково: уровни пушки складываются, статы игрока
    /// умножают по типу. Новые — в конец: номер пишется в ассет пушки.
    /// </summary>
    public enum WeaponStat
    {
        /// <summary>Урон основного огня, HP.</summary>
        Damage,

        /// <summary>Выстрелов (взмахов, ударов) в секунду.</summary>
        Rate,

        /// <summary>Магазин: гранаты, патроны; у ресурсных — объём топлива или батареи.</summary>
        Magazine,

        /// <summary>Перезарядка, секунды (у дробовика — на патрон).</summary>
        Reload,

        /// <summary>Разброс, градусы.</summary>
        Spread,

        /// <summary>Дальность, юниты.</summary>
        Range,

        /// <summary>Площадь: радиус взрыва, полуугол конуса, ширина луча.</summary>
        Area,

        /// <summary>Сколько целей пробивает (луч, рассечение).</summary>
        Pierce,

        /// <summary>Снарядов (дробин) за выстрел, прыжков молнии.</summary>
        Projectiles,

        /// <summary>Отброс, юнитов в секунду.</summary>
        Knockback,

        /// <summary>Сила статуса: урон горения в секунду, доля замедления, длительность оглушения.</summary>
        Status,

        /// <summary>Скорость снаряда, юнитов в секунду.</summary>
        Speed,

        /// <summary>Откат альтернативного огня, секунды.</summary>
        AltCooldown,

        /// <summary>Заряды альта: липучки, монеты, магниты, катушки.</summary>
        AltCharges,

        /// <summary>Урон альта, HP.</summary>
        AltDamage,

        /// <summary>Площадь альта.</summary>
        AltArea,

        /// <summary>Длительность альта, секунды.</summary>
        AltDuration,

        /// <summary>Множитель временного здоровья за убийства вплотную.</summary>
        TempHealth,

        /// <summary>Расход ресурса: нагрев за выстрел, топливо в секунду, батарея в секунду.</summary>
        Drain,

        /// <summary>Восполнение ресурса в секунду: остывание, топливо, заряд.</summary>
        Refill,

        /// <summary>Доля крита сверх статов игрока.</summary>
        Crit,

        /// <summary>Своё число пушки: смысл — в её классе (радиус удара оземь, время заряда…).</summary>
        Extra1,

        /// <summary>Второе своё число пушки.</summary>
        Extra2,

        Count
    }

    /// <summary>Правка стата уровнем: значение = (база + Σ прибавок) × Π(1 + доля).</summary>
    [Serializable]
    public struct WeaponStatMod
    {
        public WeaponStat Stat;
        public float Add;
        public float Scale;

        public WeaponStatMod(WeaponStat stat, float add, float scale = 0f)
        {
            Stat = stat;
            Add = add;
            Scale = scale;
        }

        public static WeaponStatMod Plus(WeaponStat stat, float add) => new WeaponStatMod(stat, add);

        public static WeaponStatMod Percent(WeaponStat stat, float share) => new WeaponStatMod(stat, 0f, share);
    }

    /// <summary>Уровень пушки: текст карточки и правки.</summary>
    [Serializable]
    public sealed class WeaponLevel
    {
        public string TextKey;
        public WeaponStatMod[] Mods = Array.Empty<WeaponStatMod>();
    }

    /// <summary>
    /// Пушка как данные: имя, базовые числа, пять уровней, эволюция, цена открытия.
    ///
    /// Ассет в Resources/Data/Weapons, а значения проставляет «Починить свет» явно из
    /// <see cref="WeaponCatalog"/> (грабли №8: умолчание срабатывает раз, при создании). Если
    /// ассета нет (прогон до починки, сборка без него), пушка строит себя из каталога в коде.
    /// </summary>
    public sealed class WeaponDef : ScriptableObject
    {
        public const int MaxLevel = 5;

        /// <summary>С какого уровня открыт альтернативный огонь.</summary>
        public const int AltLevel = 3;

        public WeaponId Id;

        [Tooltip("Ключ локализации: weapon.<key>.name / .desc")]
        public string Key;

        [Tooltip("Базовые числа по WeaponStat.")]
        public float[] Base = new float[(int)WeaponStat.Count];

        [Tooltip("Уровни 2..5: правки к базе.")]
        public WeaponLevel[] Levels = Array.Empty<WeaponLevel>();

        [Header("Эволюция")]
        [Tooltip("Парный том: пушка 5-го уровня + этот том не ниже PairLevel даёт эволюцию.")]
        public TomeId PairTome;

        [Range(1, 5)] public int PairLevel = 3;

        public WeaponLevel Evolution = new WeaponLevel();

        [Header("Открытие (M4)")]
        [Tooltip("Открыта с начала.")]
        public bool StartsUnlocked;

        [Tooltip("Цена открытия в хитине.")]
        public int Price;

        [Tooltip("Цвет пушки в интерфейсе.")]
        public Color Tint = Color.white;

        public string NameKey => "weapon." + Key + ".name";
        public string DescKey => "weapon." + Key + ".desc";
        public string AltKey => "weapon." + Key + ".alt";
        public string EvolutionKey => "weapon." + Key + ".evo";

        /// <summary>Текст карточки следующего уровня (2..5).</summary>
        public string LevelTextKey(int level) =>
            level >= 2 && level - 2 < Levels.Length && Levels[level - 2] != null ? Levels[level - 2].TextKey : NameKey;

        public float BaseOf(WeaponStat stat) => Base != null && (int)stat < Base.Length ? Base[(int)stat] : 0f;

        /// <summary>Числа пушки на уровне с эволюцией и статами игрока.</summary>
        public WeaponNumbers Compute(int level, bool evolved, PlayerStats stats) =>
            WeaponNumbers.Compute(this, level, evolved, stats);
    }

    /// <summary>
    /// Итоговые числа пушки: уровни, эволюция и статы игрока. Чистая арифметика —
    /// проверяется тестом (уровни складываются, статы умножают по типу стата).
    /// </summary>
    public readonly struct WeaponNumbers
    {
        private readonly float[] _values;

        private WeaponNumbers(float[] values) => _values = values;

        public float this[WeaponStat stat] => _values != null ? _values[(int)stat] : 0f;

        public float Damage => this[WeaponStat.Damage];
        public float Rate => Math.Max(0.01f, this[WeaponStat.Rate]);
        public int Magazine => Math.Max(1, (int)Math.Round(this[WeaponStat.Magazine]));
        public float Reload => Math.Max(0.02f, this[WeaponStat.Reload]);
        public float Area => this[WeaponStat.Area];
        public float Range => this[WeaponStat.Range];

        public static WeaponNumbers Compute(WeaponDef def, int level, bool evolved, PlayerStats stats)
        {
            var count = (int)WeaponStat.Count;
            var add = new float[count];
            var scale = new float[count];

            for (var i = 0; i < count; i++) scale[i] = 1f;

            level = Math.Max(1, Math.Min(WeaponDef.MaxLevel, level));

            for (var l = 2; l <= level; l++)
            {
                var index = l - 2;
                if (def.Levels == null || index >= def.Levels.Length || def.Levels[index] == null) continue;

                Accumulate(def.Levels[index].Mods, add, scale);
            }

            if (evolved && def.Evolution != null) Accumulate(def.Evolution.Mods, add, scale);

            var values = new float[count];

            for (var i = 0; i < count; i++) values[i] = (def.BaseOf((WeaponStat)i) + add[i]) * scale[i];

            if (stats != null)
            {
                values[(int)WeaponStat.Damage] *= stats.DamageScale;
                values[(int)WeaponStat.AltDamage] *= stats.DamageScale;
                values[(int)WeaponStat.Rate] *= stats.FireRateScale;
                values[(int)WeaponStat.Area] *= stats.AreaScale;
                values[(int)WeaponStat.AltArea] *= stats.AreaScale;
                values[(int)WeaponStat.Magazine] *= stats.CapacityScale;
                values[(int)WeaponStat.Reload] *= stats.ReloadTimeScale;
                values[(int)WeaponStat.Knockback] *= stats.KnockbackScale;
                values[(int)WeaponStat.Status] *= stats.StatusScale;
                values[(int)WeaponStat.Crit] += stats.CritChance;
            }

            return new WeaponNumbers(values);
        }

        private static void Accumulate(WeaponStatMod[] mods, float[] add, float[] scale)
        {
            if (mods == null) return;

            foreach (var mod in mods)
            {
                var i = (int)mod.Stat;
                if (i < 0 || i >= add.Length) continue;

                add[i] += mod.Add;
                scale[i] *= 1f + mod.Scale;
            }
        }
    }
}
