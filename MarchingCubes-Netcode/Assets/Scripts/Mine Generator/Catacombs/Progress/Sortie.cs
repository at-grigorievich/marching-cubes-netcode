using System;

namespace MineGenerator.Catacombs
{
    /// <summary>Что добавляет глубина сверх множителей (план M4.8).</summary>
    public struct DepthRules
    {
        /// <summary>Особые пауки уже на ярусе 1 (с глубины 2); на глубине 1 — с яруса 2.</summary>
        public bool SpecialsOnTier1;

        /// <summary>Передышка из милосердия выключена (с глубины 3).</summary>
        public bool MercyOff;

        /// <summary>Элита с двумя аффиксами на всех ярусах (с глубины 3).</summary>
        public bool TwoAffixes;

        /// <summary>Множитель ламп и жил: «тьма» с глубины 4.</summary>
        public float LampScale;

        /// <summary>Финальный рой раньше на столько секунд (глубина 5+).</summary>
        public float FinalSwarmEarlier;

        /// <summary>Карты 6×4×6 на ярусе 3 (глубина 4+).</summary>
        public bool BigMaps;
    }

    /// <summary>
    /// Кривая сложности вылазки (M4.8): ярус × глубина × мягкий старт. Ни один множитель не зависит
    /// от мета-покупок (решение плана №5). Время внутри яруса — директор и <see cref="RunCurve"/>.
    /// </summary>
    public static class SortieDifficulty
    {
        public const int MaxTier = 3;

        /// <summary>Сколько первых забегов профиля идут с мягким стартом.</summary>
        public const int SoftStartRuns = 3;

        private static readonly float[] TierHealth = { 1f, 2f, 3.5f };
        private static readonly float[] TierBite = { 1f, 1.4f, 1.9f };
        private static readonly float[] TierPopulation = { 1f, 1.15f, 1.3f };

        private static readonly float[] DepthHealth = { 1f, 1.6f, 2.5f, 3.8f, 5.5f };
        private static readonly float[] DepthBite = { 1f, 1.3f, 1.6f, 2f, 2.5f };
        private static readonly float[] DepthPopulation = { 1f, 1.1f, 1.2f, 1.3f, 1.4f };
        private static readonly float[] DepthChitin = { 1f, 1.5f, 2.2f, 3f, 4f };

        /// <summary>Множители яруса и глубины; мягкий старт — для первых трёх забегов профиля.</summary>
        public static DifficultyScale Scale(int tier, int depth, int runsPlayed)
        {
            tier = Math.Max(1, Math.Min(MaxTier, tier));
            depth = Math.Max(1, depth);

            var t = tier - 1;
            var scale = new DifficultyScale
            {
                Health = TierHealth[t],
                Bite = TierBite[t],
                Population = TierPopulation[t],
                Chitin = 1f
            };

            scale *= DepthScale(depth);

            // Мягкий старт: невидимо, только первые три забега. Не подстройка под игрока, а обучение —
            // новичок не знает рывка, а смерть на первой минуте первого забега — уход навсегда.
            if (runsPlayed < SoftStartRuns)
            {
                scale.Population *= 0.8f;
                scale.Bite *= 0.75f;
            }

            return scale;
        }

        /// <summary>Множители глубины; с шестой — «Бездна N», бесконечно.</summary>
        public static DifficultyScale DepthScale(int depth)
        {
            depth = Math.Max(1, depth);

            if (depth <= 5)
            {
                var d = depth - 1;
                return new DifficultyScale
                {
                    Health = DepthHealth[d],
                    Bite = DepthBite[d],
                    Population = DepthPopulation[d],
                    Chitin = DepthChitin[d]
                };
            }

            var abyss = depth - 5;
            return new DifficultyScale
            {
                Health = 5.5f * (float)Math.Pow(1.35, abyss),
                Bite = 2.5f * (float)Math.Pow(1.2, abyss),
                Population = 1.4f,
                Chitin = 4f + 0.5f * abyss
            };
        }

        public static DepthRules Rules(int depth, int runsPlayed)
        {
            return new DepthRules
            {
                SpecialsOnTier1 = depth >= 2,
                MercyOff = depth >= 3,
                TwoAffixes = depth >= 3,
                LampScale = depth >= 4 ? 0.5f : 1f,
                FinalSwarmEarlier = depth >= 5 ? 60f : 0f,
                BigMaps = depth >= 4
            };
        }

        /// <summary>Особые на этом ярусе? Мягкий старт — без особых.</summary>
        public static bool SpecialsAllowed(int tier, int depth, int runsPlayed) =>
            runsPlayed >= SoftStartRuns && (tier >= 2 || Rules(depth, runsPlayed).SpecialsOnTier1);

        /// <summary>
        /// Рекомендованная глубина: самая глубокая, на которой хотя бы половина прошлых забегов дошла
        /// до яруса 3; новичку — первая.
        /// </summary>
        public static int Recommended(PlayerProfile profile)
        {
            var best = 1;

            for (var depth = 1; depth <= profile.UnlockedDepth; depth++)
            {
                var runs = PlayerProfile.Get(profile.Stats.RunsAtDepth, depth);
                var deep = PlayerProfile.Get(profile.Stats.ReachedTier3AtDepth, depth);

                if (runs >= 2 && deep * 2 >= runs) best = Math.Min(profile.UnlockedDepth, depth + 1);
            }

            return best;
        }
    }

    /// <summary>
    /// Рецепт уровня (M4.7): «всегда случайно, но по кривой». Карта генерируется заново на каждом
    /// ярусе (сид случайный), рецепт — набор переопределений <see cref="CatacombSettings"/> по ярусу
    /// и глубине, применяемый к КОПИИ ассета: правка самого ассета в рантайме в редакторе осела бы
    /// в файле (грабли №8 в рантайм-виде).
    /// </summary>
    public struct LevelRecipe
    {
        public int Tier;
        public int Depth;

        public int ChunksX;
        public int ChunksY;
        public int ChunksZ;
        public int LevelCount;
        public int RoomsMin;
        public int RoomsMax;
        public float GalleryShare;
        public float PinchChance;
        public float PathChaos;

        /// <summary>Лампы и жилы (с «тьмой» глубины), паутина — множители к числу.</summary>
        public float LampScale;
        public float WebScale;

        /// <summary>Длина стадии директора, секунды.</summary>
        public float StageSeconds;

        public static LevelRecipe For(int tier, int depth)
        {
            tier = Math.Max(1, Math.Min(SortieDifficulty.MaxTier, tier));
            var rules = SortieDifficulty.Rules(depth, int.MaxValue);

            var recipe = tier switch
            {
                1 => new LevelRecipe
                {
                    ChunksX = 4, ChunksY = 3, ChunksZ = 4, LevelCount = 2, RoomsMin = 6, RoomsMax = 8,
                    GalleryShare = 0.45f, PinchChance = 0.2f, PathChaos = 0.4f, LampScale = 1.2f, WebScale = 0.7f,
                    StageSeconds = 300f
                },
                2 => new LevelRecipe
                {
                    ChunksX = 5, ChunksY = 3, ChunksZ = 5, LevelCount = 3, RoomsMin = 8, RoomsMax = 11,
                    GalleryShare = 0.35f, PinchChance = 0.3f, PathChaos = 0.6f, LampScale = 1f, WebScale = 1f,
                    StageSeconds = 360f
                },
                _ => new LevelRecipe
                {
                    ChunksX = rules.BigMaps ? 6 : 5, ChunksY = 4, ChunksZ = rules.BigMaps ? 6 : 5, LevelCount = 3,
                    RoomsMin = 10, RoomsMax = 14, GalleryShare = 0.3f, PinchChance = 0.4f, PathChaos = 0.75f,
                    LampScale = 0.7f, WebScale = 1.3f, StageSeconds = 420f
                }
            };

            recipe.Tier = tier;
            recipe.Depth = Math.Max(1, depth);
            recipe.LampScale *= rules.LampScale;
            recipe.StageSeconds -= rules.FinalSwarmEarlier;

            return recipe;
        }
    }
}
