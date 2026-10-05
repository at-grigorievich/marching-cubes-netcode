using System;
using System.Collections.Generic;

namespace MineGenerator.Catacombs
{
    /// <summary>Что считает задание.</summary>
    public enum QuestKind
    {
        /// <summary>Убийств пушкой (накопительно).</summary>
        KillsWithWeapon,

        KillsTotal,

        /// <summary>Убийств огнём — огнемёт и горение (накопительно).</summary>
        KillsByFire,

        /// <summary>Матка яруса убита (накопительно).</summary>
        QueenOfTier,

        /// <summary>Матка яруса убита быстрее стольких секунд от пробуждения (лучшее).</summary>
        QueenOfTierUnder,

        /// <summary>Маток всего (накопительно).</summary>
        QueensTotal,

        /// <summary>Дойти до яруса (лучшее).</summary>
        ReachTier,

        /// <summary>Продержаться столько секунд финального роя (лучшее).</summary>
        SurviveFinalSwarm,

        EliteKills,
        SpecialKills,
        Runs,

        /// <summary>Дойти до глубины (лучшее).</summary>
        ReachDepth,

        /// <summary>Пройти ярус, ни разу не получив урона кислотой (накопительно — ярусов).</summary>
        TierWithoutAcid,

        /// <summary>Пройти ярус со стартовой пушкой (ежедневные).</summary>
        TierWithWeapon
    }

    /// <summary>Чем награждает задание.</summary>
    public enum QuestReward
    {
        Chitin,
        UnlockClass,

        /// <summary>Окраска пушки — дёшево и ценится (план M4.6).</summary>
        Skin
    }

    /// <summary>Описание задания.</summary>
    public sealed class QuestDef
    {
        public string Id;
        public QuestKind Kind;
        public WeaponId Weapon = WeaponId.Count;
        public int Tier;
        public long Target;
        public float Seconds;
        public QuestReward Reward;
        public int RewardValue;
        public bool Daily;

        /// <summary>Лучшее за забег, а не сумма: «дойти до», «быстрее», «продержаться».</summary>
        public bool Best => Kind == QuestKind.QueenOfTierUnder || Kind == QuestKind.ReachTier ||
                            Kind == QuestKind.SurviveFinalSwarm || Kind == QuestKind.ReachDepth;

        public string TextKey => "quest." + Kind.ToString().ToLowerInvariant();
    }

    /// <summary>
    /// Сводка забега — то, из чего считаются задания. Её собирает контроллер забега из журнала
    /// убийств и событий (M0.3, M1, M3) одним местом, а не разбросанными счётчиками (план M4.6).
    /// </summary>
    public sealed class RunSummary
    {
        public int Depth = 1;
        public int MaxTier = 1;
        public long Kills;
        public long[] KillsByWeapon = new long[(int)WeaponId.Count];
        public long KillsByFire;
        public int EliteKills;
        public int SpecialKills;

        /// <summary>Матки по ярусам и лучшее время от пробуждения до смерти, секунды (0 — не убита).</summary>
        public int[] QueensByTier = new int[3];
        public float[] QueenSeconds = new float[3];

        public float FinalSwarmSeconds;
        public int TiersWithoutAcid;

        public WeaponId StartWeapon = WeaponId.Launcher;

        /// <summary>Пройдено ярусов (Матка убита и спуск взят) — для «ярус с пушкой».</summary>
        public int TiersCleared;
    }

    /// <summary>
    /// Задания (M4.6): постоянные, ежедневные (три в день), серия входов. Чистая логика: прогресс
    /// живёт в профиле, сводку забега даёт контроллер, награды выдаются сразу при выполнении.
    /// </summary>
    public static class Quests
    {
        public static readonly IReadOnlyList<QuestDef> Permanent = BuildPermanent();

        private static List<QuestDef> BuildPermanent()
        {
            var list = new List<QuestDef>();

            // По пушкам: три ступени на каждую.
            long[] steps = { 200, 1000, 5000 };
            int[] rewards = { 60, 200, 600 };

            for (var w = 0; w < (int)WeaponId.Count; w++)
            {
                for (var s = 0; s < steps.Length; s++)
                {
                    list.Add(new QuestDef
                    {
                        Id = $"kills.{WeaponIds.Key((WeaponId)w)}.{s + 1}", Kind = QuestKind.KillsWithWeapon, Weapon = (WeaponId)w,
                        Target = steps[s], Reward = s == 2 ? QuestReward.Skin : QuestReward.Chitin,
                        RewardValue = s == 2 ? w : rewards[s]
                    });
                }
            }

            // Матки.
            for (var t = 1; t <= 3; t++)
            {
                list.Add(new QuestDef { Id = $"queen.{t}", Kind = QuestKind.QueenOfTier, Tier = t, Target = 1, Reward = QuestReward.Chitin, RewardValue = 150 * t });
            }

            list.Add(new QuestDef { Id = "queen.1.fast", Kind = QuestKind.QueenOfTierUnder, Tier = 1, Seconds = 150f, Target = 1, Reward = QuestReward.Chitin, RewardValue = 300 });
            list.Add(new QuestDef { Id = "queen.2.fast", Kind = QuestKind.QueenOfTierUnder, Tier = 2, Seconds = 180f, Target = 1, Reward = QuestReward.Chitin, RewardValue = 500 });

            // Открытия классов (M4.5).
            list.Add(new QuestDef { Id = "class.gunslinger", Kind = QuestKind.ReachTier, Target = 2, Reward = QuestReward.UnlockClass, RewardValue = (int)PlayerClass.Gunslinger });
            list.Add(new QuestDef { Id = "class.stormer", Kind = QuestKind.SurviveFinalSwarm, Target = 60, Reward = QuestReward.UnlockClass, RewardValue = (int)PlayerClass.Stormer });
            list.Add(new QuestDef { Id = "class.miner", Kind = QuestKind.QueensTotal, Target = 3, Reward = QuestReward.UnlockClass, RewardValue = (int)PlayerClass.Miner });
            list.Add(new QuestDef { Id = "class.firefighter", Kind = QuestKind.KillsByFire, Target = 5000, Reward = QuestReward.UnlockClass, RewardValue = (int)PlayerClass.Firefighter });
            list.Add(new QuestDef { Id = "class.electrician", Kind = QuestKind.QueenOfTier, Tier = 3, Target = 1, Reward = QuestReward.UnlockClass, RewardValue = (int)PlayerClass.Electrician });

            // Стиль, глубины, накопительное.
            list.Add(new QuestDef { Id = "style.noacid", Kind = QuestKind.TierWithoutAcid, Target = 1, Reward = QuestReward.Chitin, RewardValue = 250 });
            list.Add(new QuestDef { Id = "style.noacid.5", Kind = QuestKind.TierWithoutAcid, Target = 5, Reward = QuestReward.Chitin, RewardValue = 600 });

            for (var d = 2; d <= 5; d++)
            {
                list.Add(new QuestDef { Id = $"depth.{d}", Kind = QuestKind.ReachDepth, Target = d, Reward = QuestReward.Chitin, RewardValue = 300 * d });
            }

            list.Add(new QuestDef { Id = "elite.10", Kind = QuestKind.EliteKills, Target = 10, Reward = QuestReward.Chitin, RewardValue = 150 });
            list.Add(new QuestDef { Id = "elite.50", Kind = QuestKind.EliteKills, Target = 50, Reward = QuestReward.Chitin, RewardValue = 500 });
            list.Add(new QuestDef { Id = "special.25", Kind = QuestKind.SpecialKills, Target = 25, Reward = QuestReward.Chitin, RewardValue = 150 });
            list.Add(new QuestDef { Id = "special.100", Kind = QuestKind.SpecialKills, Target = 100, Reward = QuestReward.Chitin, RewardValue = 500 });
            list.Add(new QuestDef { Id = "runs.5", Kind = QuestKind.Runs, Target = 5, Reward = QuestReward.Chitin, RewardValue = 100 });
            list.Add(new QuestDef { Id = "runs.25", Kind = QuestKind.Runs, Target = 25, Reward = QuestReward.Chitin, RewardValue = 400 });
            list.Add(new QuestDef { Id = "kills.10000", Kind = QuestKind.KillsTotal, Target = 10000, Reward = QuestReward.Chitin, RewardValue = 300 });
            list.Add(new QuestDef { Id = "kills.50000", Kind = QuestKind.KillsTotal, Target = 50000, Reward = QuestReward.Chitin, RewardValue = 1000 });
            list.Add(new QuestDef { Id = "swarm.180", Kind = QuestKind.SurviveFinalSwarm, Target = 180, Reward = QuestReward.Chitin, RewardValue = 700 });

            return list;
        }

        public static QuestDef Find(string id)
        {
            foreach (var def in Permanent)
            {
                if (def.Id == id) return def;
            }

            return null;
        }

        /// <summary>Сколько этот забег продвинул задание: сумма или лучшее — по виду.</summary>
        public static long Measure(QuestDef def, RunSummary run)
        {
            var tier = Math.Max(1, Math.Min(3, def.Tier)) - 1;

            switch (def.Kind)
            {
                case QuestKind.KillsWithWeapon: return def.Weapon < WeaponId.Count ? run.KillsByWeapon[(int)def.Weapon] : 0;
                case QuestKind.KillsTotal: return run.Kills;
                case QuestKind.KillsByFire: return run.KillsByFire;
                case QuestKind.QueenOfTier: return run.QueensByTier[tier];
                case QuestKind.QueenOfTierUnder:
                    return run.QueensByTier[tier] > 0 && run.QueenSeconds[tier] > 0f && run.QueenSeconds[tier] <= def.Seconds ? 1 : 0;
                case QuestKind.QueensTotal: return run.QueensByTier[0] + run.QueensByTier[1] + run.QueensByTier[2];
                case QuestKind.ReachTier: return run.MaxTier;
                case QuestKind.SurviveFinalSwarm: return (long)Math.Floor(run.FinalSwarmSeconds);
                case QuestKind.EliteKills: return run.EliteKills;
                case QuestKind.SpecialKills: return run.SpecialKills;
                case QuestKind.Runs: return 1;
                case QuestKind.ReachDepth: return run.Depth;
                case QuestKind.TierWithoutAcid: return run.TiersWithoutAcid;
                case QuestKind.TierWithWeapon: return run.StartWeapon == def.Weapon && run.MaxTier > def.Tier ? 1 : 0;
                default: return 0;
            }
        }

        /// <summary>
        /// Учесть забег: постоянные и ежедневные. Возвращает только что выполненные — их награды
        /// уже выданы (хитин, класс, окраска).
        /// </summary>
        public static List<QuestDef> Apply(PlayerProfile profile, RunSummary run, int today)
        {
            var completed = new List<QuestDef>();

            foreach (var def in Permanent) Advance(profile, profile.Quests, def, run, completed);

            if (profile.Daily.Day == today)
            {
                foreach (var def in DailyFor(today))
                {
                    Advance(profile, profile.Daily.Quests, def, run, completed);
                }
            }

            return completed;
        }

        private static void Advance(PlayerProfile profile, List<QuestProgress> list, QuestDef def, RunSummary run,
            List<QuestDef> completed)
        {
            var index = list.FindIndex(q => q.Id == def.Id);
            var progress = index >= 0 ? list[index] : new QuestProgress { Id = def.Id };

            if (progress.Done) return;

            var measure = Measure(def, run);
            progress.Value = def.Best ? Math.Max(progress.Value, measure) : progress.Value + measure;

            if (progress.Value >= def.Target)
            {
                progress.Done = true;
                progress.Claimed = true;
                Grant(profile, def);
                completed.Add(def);
            }

            if (index >= 0) list[index] = progress;
            else list.Add(progress);
        }

        private static void Grant(PlayerProfile profile, QuestDef def)
        {
            switch (def.Reward)
            {
                case QuestReward.Chitin:
                    profile.AddChitin(def.RewardValue);
                    break;

                case QuestReward.UnlockClass:
                    if (!profile.UnlockedClasses.Contains(def.RewardValue)) profile.UnlockedClasses.Add(def.RewardValue);
                    break;

                case QuestReward.Skin:
                    if (!profile.UnlockedSkins.Contains(def.RewardValue)) profile.UnlockedSkins.Add(def.RewardValue);
                    break;
            }
        }

        public static long ProgressOf(PlayerProfile profile, QuestDef def)
        {
            var list = def.Daily ? profile.Daily.Quests : profile.Quests;
            foreach (var q in list)
            {
                if (q.Id == def.Id) return q.Done ? def.Target : Math.Min(def.Target, q.Value);
            }

            return 0;
        }

        // ------------------------------------------------------------------ ежедневные

        /// <summary>
        /// Три ежедневных задания на день — детерминированно по номеру дня: в двух вкладках
        /// и на двух устройствах в один день задания одни и те же. Короткие, на 1–2 забега.
        /// </summary>
        public static List<QuestDef> DailyFor(int day)
        {
            var random = new Random(day * 7919 + 17);
            var pool = new List<QuestDef>
            {
                new QuestDef { Kind = QuestKind.EliteKills, Target = 2, RewardValue = 80 },
                new QuestDef { Kind = QuestKind.SpecialKills, Target = 6, RewardValue = 80 },
                new QuestDef { Kind = QuestKind.QueenOfTier, Tier = 1, Target = 1, RewardValue = 120 },
                new QuestDef { Kind = QuestKind.ReachTier, Target = 2, RewardValue = 100 },
                new QuestDef { Kind = QuestKind.KillsTotal, Target = 3000, RewardValue = 90 },
                new QuestDef { Kind = QuestKind.SurviveFinalSwarm, Target = 20, RewardValue = 100 },
                new QuestDef { Kind = QuestKind.KillsWithWeapon, Weapon = (WeaponId)random.Next((int)WeaponId.Count), Target = 400, RewardValue = 90 },
                new QuestDef { Kind = QuestKind.TierWithWeapon, Weapon = (WeaponId)random.Next((int)WeaponId.Count), Tier = 1, Target = 1, RewardValue = 120 }
            };

            var picked = new List<QuestDef>(3);

            for (var i = 0; i < 3 && pool.Count > 0; i++)
            {
                var index = random.Next(pool.Count);
                var def = pool[index];
                pool.RemoveAt(index);

                def.Id = $"daily.{day}.{i}";
                def.Daily = true;
                def.Reward = QuestReward.Chitin;
                picked.Add(def);
            }

            return picked;
        }

        /// <summary>
        /// Вход в игру: обновить ежедневные, если наступил новый день, и серию входов. Возвращает
        /// хитин за серию (20 × день серии, 7-й день — ещё и окраска; дальше серия начинается заново).
        /// </summary>
        public static float DailyLogin(PlayerProfile profile, int today)
        {
            if (profile.Daily.Day != today)
            {
                profile.Daily.Day = today;
                profile.Daily.Quests.Clear();
            }

            if (profile.StreakLastDay == today) return 0f;

            profile.StreakDays = profile.StreakLastDay == today - 1 ? profile.StreakDays % 7 + 1 : 1;
            profile.StreakLastDay = today;
            profile.LastSeenDay = today;

            var reward = 20f * profile.StreakDays;
            profile.AddChitin(reward);

            // Седьмой день — окраска (номер окраски «серия» — 100).
            if (profile.StreakDays == 7 && !profile.UnlockedSkins.Contains(100)) profile.UnlockedSkins.Add(100);

            return reward;
        }

        /// <summary>Номер дня по UTC — общий для всех часовых поясов игрока.</summary>
        public static int DayOf(DateTime utc) => (int)(utc - new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalDays;
    }
}
