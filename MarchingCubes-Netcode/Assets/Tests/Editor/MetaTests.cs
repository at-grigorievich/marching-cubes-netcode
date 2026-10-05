using System.Linq;
using NUnit.Framework;

namespace MineGenerator.Catacombs.Tests
{
    /// <summary>
    /// Мета-игра (M4): статы класса и меты, цены и сброс улучшений, слияние сохранений, миграции,
    /// задания и ежедневные, кривая глубин, рецепты уровня. Чистая логика — без движка.
    /// </summary>
    public sealed class MetaTests
    {
        private static PlayerProfile Fresh() => PlayerProfile.CreateNew(0);

        // ------------------------------------------------------------------ статы

        [Test]
        public void StatsSumClassMetaAndTomes()
        {
            var profile = Fresh();
            profile.SetUpgradeLevel(MetaUpgrade.Might, 2);
            profile.SetUpgradeLevel(MetaUpgrade.Reach, 1);

            var stats = new PlayerStats();
            PlayerClasses.Apply(PlayerClass.Ranger, stats);
            MetaUpgrades.Apply(profile, stats);

            var session = new RunSession();
            session.Tomes.Add(new OwnedTome { Id = TomeId.Might, Level = 1 });
            session.ApplyTomes(stats);

            // Рейнджер +10% площади, мета «Охват» +5%: площадь складывается.
            Assert.AreEqual(1.15f, stats.AreaScale, 1e-4f);

            // Мета «Сила» ×2 = +10% урона, плюс том «Сила» первого уровня.
            Assert.Greater(stats.DamageScale, 1.1f);
        }

        // ------------------------------------------------------------------ улучшения

        [Test]
        public void PricesGrowWithEveryPurchase()
        {
            var profile = Fresh();
            profile.Chitin = 10000f;

            Assert.AreEqual(120f, MetaUpgrades.PriceOf(profile, MetaUpgrade.Might), 1e-3f);

            Assert.IsTrue(MetaUpgrades.Buy(profile, MetaUpgrade.Might));

            // Куплен один уровень — все дорожают на 10%, и чужие тоже.
            Assert.AreEqual(132f, MetaUpgrades.PriceOf(profile, MetaUpgrade.Might), 1e-3f);
            Assert.AreEqual(220f, MetaUpgrades.PriceOf(profile, MetaUpgrade.Armor), 1e-3f);
        }

        [Test]
        public void CannotBuyPastMaxOrWithoutChitin()
        {
            var profile = Fresh();
            profile.Chitin = 100f;

            Assert.IsFalse(MetaUpgrades.Buy(profile, MetaUpgrade.Might), "120 хитина нет");

            profile.Chitin = 100000f;
            for (var i = 0; i < 5; i++) Assert.IsTrue(MetaUpgrades.Buy(profile, MetaUpgrade.Might));

            Assert.IsFalse(MetaUpgrades.Buy(profile, MetaUpgrade.Might), "пять уровней — максимум");
            Assert.IsTrue(float.IsInfinity(MetaUpgrades.PriceOf(profile, MetaUpgrade.Might)));
        }

        [Test]
        public void RefundReturnsEverythingSpent()
        {
            var profile = Fresh();
            profile.Chitin = 1000f;

            MetaUpgrades.Buy(profile, MetaUpgrade.Might);
            MetaUpgrades.Buy(profile, MetaUpgrade.Magnet);

            var refund = MetaUpgrades.Refund(profile);

            Assert.AreEqual(120f + 110f, refund, 1e-3f);
            Assert.AreEqual(1000f, profile.Chitin, 1e-3f);
            Assert.AreEqual(0, profile.TotalUpgradeLevels());
        }

        // ------------------------------------------------------------------ сохранения

        [Test]
        public void MergeTakesBiggerCounterThenBiggerEarned()
        {
            var local = Fresh();
            var cloud = Fresh();

            local.SaveCounter = 5;
            cloud.SaveCounter = 7;
            Assert.AreSame(cloud, SaveService.Merge(local, cloud));

            // Равные счётчики (две вкладки) — больше заработано: купленное не теряется.
            cloud.SaveCounter = 5;
            local.ChitinEarnedTotal = 300f;
            cloud.ChitinEarnedTotal = 200f;
            Assert.AreSame(local, SaveService.Merge(local, cloud));

            Assert.AreSame(local, SaveService.Merge(local, null));
            Assert.AreSame(cloud, SaveService.Merge(null, cloud));
        }

        [Test]
        public void VersionlessProfileMigratesToDefaults()
        {
            // Профиль без версии: пустые открытия, нулевая глубина.
            var old = new PlayerProfile { Version = 0, UnlockedDepth = 0 };
            old.UnlockedWeapons.Clear();
            old.UnlockedClasses.Clear();

            var steps = ProfileMigrations.Migrate(old);

            Assert.AreEqual(1, steps);
            Assert.AreEqual(PlayerProfile.CurrentVersion, old.Version);
            Assert.IsTrue(old.IsWeaponUnlocked(WeaponId.Launcher));
            Assert.IsTrue(old.IsWeaponUnlocked(WeaponId.Revolver));
            Assert.IsTrue(old.IsClassUnlocked(PlayerClass.Ranger));
            Assert.AreEqual(1, old.UnlockedDepth);
        }

        [Test]
        public void SaveThrottledToFiveSeconds()
        {
            var local = new MemorySaveBackend();
            var saves = new SaveService(local) { ToJson = p => p.SaveCounter.ToString(), FromJson = j => null };
            saves.Load(0);

            saves.MarkDirty();
            saves.Tick(0.1f);
            Assert.AreEqual(1, local.Writes, "первое — сразу");

            saves.MarkDirty();
            saves.Tick(1f);
            Assert.AreEqual(1, local.Writes, "раньше 5 с — копится");

            saves.Tick(4.5f);
            Assert.AreEqual(2, local.Writes);
        }

        [Test]
        public void SnapshotRestoresGrowth()
        {
            var session = new RunSession { Level = 7, Xp = 12f, Tier = 2, Depth = 3, Kills = 900 };
            session.Weapons.Add(new OwnedWeapon { Id = WeaponId.Shotgun, Level = 4, Evolved = false });
            session.Tomes.Add(new OwnedTome { Id = TomeId.Haste, Level = 2 });
            session.Rerolls = 2;
            session.Banishes = 1;
            session.BanishedWeapons.Add(WeaponId.Rail);

            var restored = RunSnapshot.From(session, (int)PlayerClass.Stormer, 77, 0.6f).ToSession();

            Assert.AreEqual(7, restored.Level);
            Assert.AreEqual(2, restored.Tier);
            Assert.AreEqual(3, restored.Depth);
            Assert.AreEqual(900, restored.Kills);
            Assert.AreEqual(WeaponId.Shotgun, restored.Weapons[0].Id);
            Assert.AreEqual(4, restored.Weapons[0].Level);
            Assert.AreEqual(TomeId.Haste, restored.Tomes[0].Id);

            // Остаток перебросов выдаётся только на первом уровне — без снимка он обнулился бы.
            Assert.AreEqual(2, restored.Rerolls);
            Assert.AreEqual(1, restored.Banishes);
            CollectionAssert.AreEqual(new[] { WeaponId.Rail }, restored.BanishedWeapons);
        }

        // ------------------------------------------------------------------ задания

        [Test]
        public void DailyQuestsAreStablePerDayAndDiffer()
        {
            var a = Quests.DailyFor(400).Select(q => q.Kind + ":" + q.Target + ":" + q.Weapon).ToArray();
            var b = Quests.DailyFor(400).Select(q => q.Kind + ":" + q.Target + ":" + q.Weapon).ToArray();

            Assert.AreEqual(3, a.Length);
            CollectionAssert.AreEqual(a, b, "один день — одни задания");

            var differs = false;
            for (var day = 401; day < 410 && !differs; day++)
            {
                differs = !Quests.DailyFor(day).Select(q => q.Kind + ":" + q.Target + ":" + q.Weapon).SequenceEqual(a);
            }

            Assert.IsTrue(differs, "в другие дни — другие");
        }

        [Test]
        public void QuestProgressAccumulatesAndGrantsOnce()
        {
            var profile = Fresh();
            var run = new RunSummary();
            run.KillsByWeapon[(int)WeaponId.Launcher] = 150;

            Quests.Apply(profile, run, 100);
            var chitin = profile.Chitin;
            var done = Quests.Apply(profile, run, 100);

            // 150 + 150 ≥ 200 — первая ступень гранатомёта выполнена, награда 60 выдана один раз.
            Assert.IsTrue(done.Any(q => q.Id == "kills.launcher.1"));
            Assert.AreEqual(chitin + 60f, profile.Chitin, 1e-3f);

            Quests.Apply(profile, run, 100);
            Assert.AreEqual(1, profile.Quests.Count(q => q.Id == "kills.launcher.1" && q.Done));
        }

        [Test]
        public void ClassUnlockQuests()
        {
            var profile = Fresh();
            var run = new RunSummary { MaxTier = 2, FinalSwarmSeconds = 61f };

            Quests.Apply(profile, run, 1);

            Assert.IsTrue(profile.IsClassUnlocked(PlayerClass.Gunslinger), "дойти до яруса 2");
            Assert.IsTrue(profile.IsClassUnlocked(PlayerClass.Stormer), "60 с финального роя");
            Assert.IsFalse(profile.IsClassUnlocked(PlayerClass.Electrician));
        }

        [Test]
        public void StreakGrowsDailyAndResetsAfterGap()
        {
            var profile = Fresh();

            Assert.AreEqual(20f, Quests.DailyLogin(profile, 10), 1e-3f);
            Assert.AreEqual(0f, Quests.DailyLogin(profile, 10), 1e-3f, "второй вход в тот же день — ничего");
            Assert.AreEqual(40f, Quests.DailyLogin(profile, 11), 1e-3f);
            Assert.AreEqual(20f, Quests.DailyLogin(profile, 13), 1e-3f, "пропуск — серия заново");
        }

        // ------------------------------------------------------------------ кривая и рецепты

        [Test]
        public void DepthAndTierMultiply()
        {
            var scale = SortieDifficulty.Scale(3, 2, 99);

            Assert.AreEqual(3.5f * 1.6f, scale.Health, 1e-3f);
            Assert.AreEqual(1.9f * 1.3f, scale.Bite, 1e-3f);
            Assert.AreEqual(1.5f, scale.Chitin, 1e-3f);

            // Бездна: глубина 7 — 5.5 × 1.35².
            Assert.AreEqual(5.5f * 1.35f * 1.35f, SortieDifficulty.DepthScale(7).Health, 1e-2f);
        }

        [Test]
        public void SoftStartOnlyForFirstThreeRuns()
        {
            Assert.AreEqual(0.75f, SortieDifficulty.Scale(1, 1, 0).Bite, 1e-3f);
            Assert.AreEqual(1f, SortieDifficulty.Scale(1, 1, 3).Bite, 1e-3f);
            Assert.IsFalse(SortieDifficulty.SpecialsAllowed(2, 1, 0), "мягкий старт — без особых");
            Assert.IsTrue(SortieDifficulty.SpecialsAllowed(2, 1, 5));
            Assert.IsFalse(SortieDifficulty.SpecialsAllowed(1, 1, 5), "глубина 1: особые с яруса 2");
            Assert.IsTrue(SortieDifficulty.SpecialsAllowed(1, 2, 5));
        }

        [Test]
        public void RecipesFollowTheTable()
        {
            var first = LevelRecipe.For(1, 1);
            Assert.AreEqual(4, first.ChunksX);
            Assert.AreEqual(2, first.LevelCount);
            Assert.AreEqual(300f, first.StageSeconds, 1e-3f);

            var third = LevelRecipe.For(3, 4);
            Assert.AreEqual(6, third.ChunksX, "глубина 4 — карты 6×4×6");
            Assert.AreEqual(0.7f * 0.5f, third.LampScale, 1e-3f, "тьма глубины 4");

            Assert.AreEqual(420f - 60f, LevelRecipe.For(3, 5).StageSeconds, 1e-3f, "глубина 5 — финальный рой на минуту раньше");
        }
    }
}
