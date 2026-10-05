using System;

namespace MineGenerator.Catacombs
{
    /// <summary>Мета-улучшения (PowerUps Vampire Survivors, M4.4). Порядок — номер в профиле.</summary>
    public enum MetaUpgrade
    {
        Might,
        Armor,
        Vitality,
        Regen,
        Haste,
        Reach,
        Agility,
        Magnet,
        Luck,
        Greed,
        Insight,
        Revival,
        Rerolls,
        Skips,
        Banishes,

        /// <summary>Свои для этой игры: темнота катакомб — настоящее препятствие, и покупка света — понятная награда.</summary>
        Lamp,
        LairSense,

        Count
    }

    /// <summary>Описание улучшения: стат, прибавка за уровень, уровней, базовая цена.</summary>
    public readonly struct MetaUpgradeDef
    {
        public readonly MetaUpgrade Id;
        public readonly string Key;
        public readonly Stat Stat;
        public readonly float PerLevel;
        public readonly int MaxLevel;
        public readonly float BasePrice;

        public MetaUpgradeDef(MetaUpgrade id, string key, Stat stat, float perLevel, int maxLevel, float basePrice)
        {
            Id = id;
            Key = key;
            Stat = stat;
            PerLevel = perLevel;
            MaxLevel = maxLevel;
            BasePrice = basePrice;
        }

        public string NameKey => "meta." + Key + ".name";
        public string DescKey => "meta." + Key + ".desc";
    }

    /// <summary>
    /// Мета-улучшения: каталог, цены, покупка, сброс, вклад в статы забега.
    ///
    /// Цена уровня = база × (1 + 0.1 × куплено уровней всего) — как в VS: каждая покупка дорожает
    /// и остальные. «Сбросить» возвращает весь потраченный хитин: бесплатная перестройка снимает
    /// страх ошибиться. Ни одно улучшение не трогает сложность (решение плана №5).
    /// </summary>
    public static class MetaUpgrades
    {
        private static readonly MetaUpgradeDef[] Table =
        {
            new MetaUpgradeDef(MetaUpgrade.Might, "might", Stat.Damage, 0.05f, 5, 120f),
            new MetaUpgradeDef(MetaUpgrade.Armor, "armor", Stat.Armor, 1f, 3, 200f),
            new MetaUpgradeDef(MetaUpgrade.Vitality, "vitality", Stat.MaxHealthPct, 0.1f, 3, 150f),
            new MetaUpgradeDef(MetaUpgrade.Regen, "regen", Stat.Regen, 0.15f, 5, 160f),
            new MetaUpgradeDef(MetaUpgrade.Haste, "haste", Stat.FireRate, 0.03f, 3, 180f),
            new MetaUpgradeDef(MetaUpgrade.Reach, "reach", Stat.Area, 0.05f, 2, 200f),
            new MetaUpgradeDef(MetaUpgrade.Agility, "agility", Stat.MoveSpeed, 0.04f, 2, 150f),
            new MetaUpgradeDef(MetaUpgrade.Magnet, "magnet", Stat.Magnet, 0.2f, 2, 100f),
            new MetaUpgradeDef(MetaUpgrade.Luck, "luck", Stat.Luck, 0.08f, 3, 220f),
            new MetaUpgradeDef(MetaUpgrade.Greed, "greed", Stat.Chitin, 0.1f, 5, 100f),
            new MetaUpgradeDef(MetaUpgrade.Insight, "insight", Stat.Xp, 0.04f, 5, 140f),
            new MetaUpgradeDef(MetaUpgrade.Revival, "revival", Stat.Revives, 1f, 1, 1500f),
            new MetaUpgradeDef(MetaUpgrade.Rerolls, "rerolls", Stat.Rerolls, 2f, 3, 250f),
            new MetaUpgradeDef(MetaUpgrade.Skips, "skips", Stat.Skips, 1f, 3, 150f),
            new MetaUpgradeDef(MetaUpgrade.Banishes, "banishes", Stat.Banishes, 1f, 3, 300f),
            new MetaUpgradeDef(MetaUpgrade.Lamp, "lamp", Stat.Lamp, 0.15f, 3, 180f),
            new MetaUpgradeDef(MetaUpgrade.LairSense, "lairsense", Stat.LairSense, 1f, 1, 400f)
        };

        public static MetaUpgradeDef Get(MetaUpgrade id) => Table[(int)id];

        /// <summary>Цена следующего уровня с учётом всех купленных. Максимум взят — бесконечность.</summary>
        public static float PriceOf(PlayerProfile profile, MetaUpgrade id)
        {
            var def = Get(id);
            if (profile.UpgradeLevel(id) >= def.MaxLevel) return float.PositiveInfinity;

            return (float)Math.Round(def.BasePrice * (1f + 0.1f * profile.TotalUpgradeLevels()));
        }

        public static bool CanBuy(PlayerProfile profile, MetaUpgrade id) => profile.Chitin >= PriceOf(profile, id);

        /// <summary>Купить следующий уровень. False — не хватает хитина или всё куплено.</summary>
        public static bool Buy(PlayerProfile profile, MetaUpgrade id)
        {
            var price = PriceOf(profile, id);
            if (float.IsInfinity(price) || profile.Chitin < price) return false;

            profile.Chitin -= price;
            profile.ChitinSpentOnUpgrades += price;
            profile.SetUpgradeLevel(id, profile.UpgradeLevel(id) + 1);

            return true;
        }

        /// <summary>Сбросить все улучшения и вернуть потраченный хитин.</summary>
        public static float Refund(PlayerProfile profile)
        {
            var refund = profile.ChitinSpentOnUpgrades;

            profile.Chitin += refund;
            profile.ChitinSpentOnUpgrades = 0f;
            profile.Upgrades.Clear();

            return refund;
        }

        /// <summary>Вклад купленного в статы забега.</summary>
        public static void Apply(PlayerProfile profile, PlayerStats stats)
        {
            if (profile == null || stats == null) return;

            foreach (var def in Table)
            {
                var level = profile.UpgradeLevel(def.Id);
                if (level > 0) stats.Add(def.Stat, def.PerLevel * level);
            }
        }
    }

    /// <summary>Классы (M4.5). Порядок — номер в профиле.</summary>
    public enum PlayerClass
    {
        Ranger,
        Gunslinger,
        Stormer,
        Miner,
        Firefighter,
        Electrician,
        Count
    }

    /// <summary>
    /// Класс = стартовая пушка + пассивка + небольшой сдвиг базовых статов. Открываются заданиями
    /// (Megabonk) — каждый класс это цель на несколько забегов. Умений на отдельной кнопке нет:
    /// на таче это шестая кнопка (план).
    /// </summary>
    public static class PlayerClasses
    {
        public static WeaponId StartWeapon(PlayerClass id) => id switch
        {
            PlayerClass.Gunslinger => WeaponId.Revolver,
            PlayerClass.Stormer => WeaponId.Shotgun,
            PlayerClass.Miner => WeaponId.PickHammer,
            PlayerClass.Firefighter => WeaponId.Flamer,
            PlayerClass.Electrician => WeaponId.Tesla,
            _ => WeaponId.Launcher
        };

        /// <summary>Задание, открывающее класс (<see cref="Quests"/>); Рейнджер открыт с начала.</summary>
        public static string UnlockQuest(PlayerClass id) => id switch
        {
            PlayerClass.Gunslinger => "class.gunslinger",
            PlayerClass.Stormer => "class.stormer",
            PlayerClass.Miner => "class.miner",
            PlayerClass.Firefighter => "class.firefighter",
            PlayerClass.Electrician => "class.electrician",
            _ => null
        };

        public static string Key(PlayerClass id) => id.ToString().ToLowerInvariant();
        public static string NameKey(PlayerClass id) => "class." + Key(id) + ".name";
        public static string DescKey(PlayerClass id) => "class." + Key(id) + ".desc";

        /// <summary>Пассивка класса — в статы забега.</summary>
        public static void Apply(PlayerClass id, PlayerStats stats)
        {
            if (stats == null) return;

            switch (id)
            {
                case PlayerClass.Ranger:
                    stats.Add(Stat.Area, 0.1f);
                    break;

                case PlayerClass.Gunslinger:
                    stats.Add(Stat.CoinRecharge, 1f);
                    stats.Add(Stat.Crit, 0.1f);
                    break;

                case PlayerClass.Stormer:
                    stats.Add(Stat.Armor, 2f);
                    stats.Add(Stat.Knockback, 0.25f);
                    break;

                case PlayerClass.Miner:
                    stats.Add(Stat.MaxHealthPct, 0.2f);
                    stats.Add(Stat.FreeBreach, 1f);
                    break;

                case PlayerClass.Firefighter:
                    stats.Add(Stat.FireImmune, 1f);
                    stats.Add(Stat.StatusPower, 0.3f);
                    break;

                case PlayerClass.Electrician:
                    stats.Add(Stat.ExtraRods, 1f);
                    break;
            }
        }
    }
}
