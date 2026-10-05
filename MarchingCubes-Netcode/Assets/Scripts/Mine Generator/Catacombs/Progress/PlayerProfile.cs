using System;
using System.Collections.Generic;

namespace MineGenerator.Catacombs
{
    /// <summary>Пара «номер — значение»: JsonUtility не умеет словари (план M4.2).</summary>
    [Serializable]
    public struct IntPair
    {
        public int Key;
        public int Value;

        public IntPair(int key, int value)
        {
            Key = key;
            Value = value;
        }
    }

    /// <summary>Пушка в снимке забега.</summary>
    [Serializable]
    public struct SnapshotWeapon
    {
        public int Id;
        public int Level;
        public bool Evolved;
    }

    /// <summary>
    /// Снимок забега на контрольной точке (вход на ярус, M4.1): перезагрузка страницы —
    /// «Продолжить вылазку с яруса N». Требование Яндекса 1.9: прогресс не теряется при обновлении.
    /// Только то, что нужно продолжить: рост, ярус, здоровье, сид. Толпа и капли — нет: ярус
    /// начинается заново, с тем же ростом.
    /// </summary>
    [Serializable]
    public sealed class RunSnapshot
    {
        public int ClassId;
        public int Depth = 1;
        public int Tier = 1;
        public int Seed;

        public int Level = 1;
        public float Xp;
        public float Chitin;
        public int Kills;
        public int EliteKills;
        public int SpecialKills;
        public int QueensKilled;
        public float Seconds;

        public float HealthShare = 1f;

        /// <summary>
        /// Остаток перебросов, пропусков и изгнаний и то, что изгнано. Контроллер выдаёт их только
        /// на первом уровне забега, и без них перезагрузка страницы молча обнуляла бы остаток.
        /// </summary>
        public int Rerolls;
        public int Skips;
        public int Banishes;
        public int PendingLevels;
        public List<int> BanishedWeapons = new List<int>();
        public List<int> BanishedTomes = new List<int>();

        public List<SnapshotWeapon> Weapons = new List<SnapshotWeapon>();
        public List<IntPair> Tomes = new List<IntPair>();
        public int[] KillsByWeapon = new int[(int)WeaponId.Count];

        public static RunSnapshot From(RunSession session, int classId, int seed, float healthShare)
        {
            var snapshot = new RunSnapshot
            {
                ClassId = classId,
                Depth = session.Depth,
                Tier = session.Tier,
                Seed = seed,
                Level = session.Level,
                Xp = session.Xp,
                Chitin = session.Chitin,
                Kills = session.Kills,
                EliteKills = session.EliteKills,
                SpecialKills = session.SpecialKills,
                QueensKilled = session.QueensKilled,
                Seconds = session.Seconds,
                HealthShare = healthShare,
                Rerolls = session.Rerolls,
                Skips = session.Skips,
                Banishes = session.Banishes,
                PendingLevels = session.PendingLevels,
                KillsByWeapon = (int[])session.KillsByWeapon.Clone()
            };

            foreach (var id in session.BanishedWeapons) snapshot.BanishedWeapons.Add((int)id);
            foreach (var id in session.BanishedTomes) snapshot.BanishedTomes.Add((int)id);

            foreach (var weapon in session.Weapons)
            {
                snapshot.Weapons.Add(new SnapshotWeapon { Id = (int)weapon.Id, Level = weapon.Level, Evolved = weapon.Evolved });
            }

            foreach (var tome in session.Tomes) snapshot.Tomes.Add(new IntPair((int)tome.Id, tome.Level));

            return snapshot;
        }

        /// <summary>Сессия забега из снимка — ярус начинается заново, с тем же ростом.</summary>
        public RunSession ToSession()
        {
            var session = new RunSession
            {
                Depth = Depth,
                Tier = Tier,
                Level = Level,
                Xp = Xp,
                Chitin = Chitin,
                Kills = Kills,
                EliteKills = EliteKills,
                SpecialKills = SpecialKills,
                QueensKilled = QueensKilled,
                Seconds = Seconds,
                Rerolls = Rerolls,
                Skips = Skips,
                Banishes = Banishes,
                PendingLevels = PendingLevels
            };

            if (BanishedWeapons != null) foreach (var id in BanishedWeapons) session.BanishedWeapons.Add((WeaponId)id);
            if (BanishedTomes != null) foreach (var id in BanishedTomes) session.BanishedTomes.Add((TomeId)id);

            foreach (var weapon in Weapons)
            {
                session.Weapons.Add(new OwnedWeapon { Id = (WeaponId)weapon.Id, Level = weapon.Level, Evolved = weapon.Evolved });
            }

            foreach (var tome in Tomes) session.Tomes.Add(new OwnedTome { Id = (TomeId)tome.Key, Level = tome.Value });

            if (KillsByWeapon != null)
            {
                for (var i = 0; i < Math.Min(KillsByWeapon.Length, session.KillsByWeapon.Length); i++)
                {
                    session.KillsByWeapon[i] = KillsByWeapon[i];
                }
            }

            return session;
        }
    }

    /// <summary>Статистика профиля за всё время.</summary>
    [Serializable]
    public sealed class ProfileStats
    {
        public int Runs;
        public int Victories;
        public long Kills;
        public long[] KillsByWeapon = new long[(int)WeaponId.Count];

        /// <summary>Убито Маток по ярусам (Ткачиха, Кислотница, Праматерь).</summary>
        public int[] QueensByTier = new int[3];

        public int EliteKills;
        public int SpecialKills;
        public int BestDepth;
        public int BestTier;

        /// <summary>Забегов на каждой глубине и сколько из них дошло до яруса 3 — для рекомендации глубины.</summary>
        public List<IntPair> RunsAtDepth = new List<IntPair>();
        public List<IntPair> ReachedTier3AtDepth = new List<IntPair>();

        public long KillsByFire;
        public float BestFinalSwarmSeconds;
    }

    /// <summary>Настройки игрока — живут в профиле, чтобы ехать с облачным сохранением.</summary>
    [Serializable]
    public sealed class ProfileSettings
    {
        public float Sensitivity = 1f;
        public bool InvertY;
        public float MasterVolume = 0.8f;
        public float MusicVolume = 0.6f;
        public float EffectsVolume = 0.9f;
        public int Quality = -1;

        /// <summary>Язык, выбранный игроком; пусто — по площадке и системе.</summary>
        public string Language = "";

        public bool DamageNumbers = true;
        public bool AutoFire = true;
    }

    /// <summary>Прогресс одного задания.</summary>
    [Serializable]
    public struct QuestProgress
    {
        public string Id;
        public long Value;
        public bool Done;
        public bool Claimed;
    }

    /// <summary>Ежедневные задания: день и три задания с прогрессом.</summary>
    [Serializable]
    public sealed class DailyState
    {
        public int Day = -1;
        public List<QuestProgress> Quests = new List<QuestProgress>();
    }

    /// <summary>Счётчики рекламы для частоты (M5).</summary>
    [Serializable]
    public sealed class AdCounters
    {
        public int RunsSinceInterstitial;
        public long LastInterstitialAt;
        public int RewardedToday;
        public int RewardedDay = -1;

        /// <summary>День последнего «ящика дня» за рекламу (M5.3).</summary>
        public int DailyBoxDay = -1;
    }

    /// <summary>
    /// Профиль игрока (M4.2): валюта, открытия, покупки, выбор, статистика, задания, настройки,
    /// обучение, реклама, снимок забега.
    ///
    /// Обычный сериализуемый класс под JsonUtility: без словарей (списки пар), без свойств
    /// (JsonUtility видит только поля). Версия и счётчик сохранений — для миграций и слияния
    /// локального с облачным (<see cref="SaveService.Merge"/>).
    /// </summary>
    [Serializable]
    public sealed class PlayerProfile
    {
        /// <summary>Нынешняя версия. Новое поле — новая версия и шаг в <see cref="ProfileMigrations"/>.</summary>
        public const int CurrentVersion = 1;

        public int Version = CurrentVersion;

        /// <summary>Растёт с каждым сохранением: при слиянии берётся профиль с большим.</summary>
        public long SaveCounter;

        public long CreatedAt;
        public int LastSeenDay = -1;

        public float Chitin;
        public float ChitinEarnedTotal;

        /// <summary>Хитин, потраченный на улучшения, — его возвращает «Сбросить».</summary>
        public float ChitinSpentOnUpgrades;

        public List<int> UnlockedWeapons = new List<int>();
        public List<int> UnlockedClasses = new List<int>();
        public List<int> UnlockedTomes = new List<int>();
        public int UnlockedDepth = 1;

        /// <summary>Уровни мета-улучшений: номер улучшения → уровень.</summary>
        public List<IntPair> Upgrades = new List<IntPair>();

        public int SelectedClass;
        public int SelectedDepth = 1;

        public ProfileStats Stats = new ProfileStats();

        public List<QuestProgress> Quests = new List<QuestProgress>();
        public DailyState Daily = new DailyState();
        public int StreakDays;
        public int StreakLastDay = -1;

        /// <summary>Косметика оружия: номер окраски пушки (0 — заводская).</summary>
        public List<IntPair> WeaponSkins = new List<IntPair>();
        public List<int> UnlockedSkins = new List<int>();

        public ProfileSettings Settings = new ProfileSettings();

        /// <summary>Показанные подсказки (M1.10) — ключи.</summary>
        public List<string> HintsShown = new List<string>();

        public AdCounters Ads = new AdCounters();

        public bool HasSnapshot;
        public RunSnapshot Snapshot = new RunSnapshot();

        /// <summary>Свежий профиль нового игрока: открыты Барабан и Монета, класс Рейнджер.</summary>
        public static PlayerProfile CreateNew(long now)
        {
            var profile = new PlayerProfile { CreatedAt = now };
            profile.EnsureDefaults();
            return profile;
        }

        /// <summary>
        /// То, что открыто у каждого: без этого профиль, пришедший из старой версии или битый,
        /// оставил бы игрока без стартовой пушки.
        /// </summary>
        public void EnsureDefaults()
        {
            UnlockedWeapons ??= new List<int>();
            UnlockedClasses ??= new List<int>();
            UnlockedTomes ??= new List<int>();
            Upgrades ??= new List<IntPair>();
            Quests ??= new List<QuestProgress>();
            Daily ??= new DailyState();
            Daily.Quests ??= new List<QuestProgress>();
            WeaponSkins ??= new List<IntPair>();
            UnlockedSkins ??= new List<int>();
            Settings ??= new ProfileSettings();
            HintsShown ??= new List<string>();
            Ads ??= new AdCounters();
            Snapshot ??= new RunSnapshot();
            Stats ??= new ProfileStats();
            Stats.RunsAtDepth ??= new List<IntPair>();
            Stats.ReachedTier3AtDepth ??= new List<IntPair>();

            if (Stats.KillsByWeapon == null || Stats.KillsByWeapon.Length < (int)WeaponId.Count)
            {
                var kills = new long[(int)WeaponId.Count];
                if (Stats.KillsByWeapon != null) Array.Copy(Stats.KillsByWeapon, kills, Stats.KillsByWeapon.Length);
                Stats.KillsByWeapon = kills;
            }

            if (Stats.QueensByTier == null || Stats.QueensByTier.Length < 3) Stats.QueensByTier = new int[3];

            for (var i = 0; i < (int)WeaponId.Count; i++)
            {
                if (WeaponCatalogStarts((WeaponId)i) && !UnlockedWeapons.Contains(i)) UnlockedWeapons.Add(i);
            }

            if (!UnlockedClasses.Contains((int)PlayerClass.Ranger)) UnlockedClasses.Add((int)PlayerClass.Ranger);

            for (var i = 0; i < (int)TomeId.Count; i++)
            {
                if (!UnlockedTomes.Contains(i)) UnlockedTomes.Add(i);
            }

            UnlockedDepth = Math.Max(1, UnlockedDepth);
            SelectedDepth = Math.Max(1, Math.Min(SelectedDepth, UnlockedDepth));

            if (!UnlockedClasses.Contains(SelectedClass)) SelectedClass = (int)PlayerClass.Ranger;
        }

        /// <summary>Открыты с начала (план M2.4): Барабан и Монета. Без обращения к ассетам — тесты вне Unity.</summary>
        private static bool WeaponCatalogStarts(WeaponId id) => id == WeaponId.Launcher || id == WeaponId.Revolver;

        public int UpgradeLevel(MetaUpgrade id)
        {
            foreach (var pair in Upgrades)
            {
                if (pair.Key == (int)id) return pair.Value;
            }

            return 0;
        }

        public void SetUpgradeLevel(MetaUpgrade id, int level)
        {
            for (var i = 0; i < Upgrades.Count; i++)
            {
                if (Upgrades[i].Key != (int)id) continue;

                Upgrades[i] = new IntPair((int)id, level);
                return;
            }

            Upgrades.Add(new IntPair((int)id, level));
        }

        public int TotalUpgradeLevels()
        {
            var total = 0;
            foreach (var pair in Upgrades) total += pair.Value;
            return total;
        }

        public bool IsWeaponUnlocked(WeaponId id) => UnlockedWeapons.Contains((int)id);
        public bool IsClassUnlocked(PlayerClass id) => UnlockedClasses.Contains((int)id);

        public void AddChitin(float amount)
        {
            if (amount <= 0f) return;

            Chitin += amount;
            ChitinEarnedTotal += amount;
        }

        public static void Increment(List<IntPair> pairs, int key, int by = 1)
        {
            for (var i = 0; i < pairs.Count; i++)
            {
                if (pairs[i].Key != key) continue;

                pairs[i] = new IntPair(key, pairs[i].Value + by);
                return;
            }

            pairs.Add(new IntPair(key, by));
        }

        public static int Get(List<IntPair> pairs, int key)
        {
            foreach (var pair in pairs)
            {
                if (pair.Key == key) return pair.Value;
            }

            return 0;
        }
    }

    /// <summary>
    /// Миграции профиля: цепочка шагов «версия N → N+1», по одному на версию, тест на каждый.
    /// Нулевая версия — профиль, сохранённый до появления версии (поле отсутствует, JsonUtility
    /// оставляет его по умолчанию нулём): ему достаются открытия по умолчанию.
    /// </summary>
    public static class ProfileMigrations
    {
        private static readonly Action<PlayerProfile>[] Steps =
        {
            // 0 → 1: профиль без версии. Всё, чего у него нет, — по умолчанию.
            profile => profile.EnsureDefaults()
        };

        /// <summary>Довести профиль до нынешней версии. Возвращает, сколько шагов сделано.</summary>
        public static int Migrate(PlayerProfile profile)
        {
            if (profile == null) return 0;

            var steps = 0;

            while (profile.Version < PlayerProfile.CurrentVersion)
            {
                var step = profile.Version;
                if (step < 0 || step >= Steps.Length) break;

                Steps[step](profile);
                profile.Version++;
                steps++;
            }

            profile.EnsureDefaults();
            return steps;
        }
    }
}
