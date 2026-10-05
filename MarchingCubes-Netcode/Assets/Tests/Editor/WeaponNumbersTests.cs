using NUnit.Framework;

namespace MineGenerator.Catacombs.Tests
{
    /// <summary>
    /// Числа пушек: уровни складываются, эволюция поверх, статы игрока умножают по типу стата.
    /// И тома: вклад за уровень, «Проворство» даёт заряд рывка на третьем.
    /// </summary>
    public sealed class WeaponNumbersTests
    {
        [Test]
        public void LauncherBaseMatchesOldNumbers()
        {
            // Гранатомёт переехал без изменения поведения: 6 гранат, радиус 5, 30 HP, 2.1 с.
            var n = WeaponCatalog.Build(WeaponId.Launcher).Compute(1, false, null);

            Assert.AreEqual(6, n.Magazine);
            Assert.AreEqual(5f, n.Area, 1e-4f);
            Assert.AreEqual(30f, n.Damage, 1e-4f);
            Assert.AreEqual(2.1f, n.Reload, 1e-4f);
            Assert.AreEqual(1f / 0.32f, n.Rate, 1e-3f);
        }

        [Test]
        public void LevelsAccumulate()
        {
            var def = WeaponCatalog.Build(WeaponId.Launcher);

            // Уровень 2: +1 граната; 3: +15% радиуса; 4: +20% урона; 5: −20% перезарядки.
            var n5 = def.Compute(5, false, null);

            Assert.AreEqual(7, n5.Magazine);
            Assert.AreEqual(5f * 1.15f, n5.Area, 1e-4f);
            Assert.AreEqual(36f, n5.Damage, 1e-4f);
            Assert.AreEqual(2.1f * 0.8f, n5.Reload, 1e-4f);
        }

        [Test]
        public void PlayerStatsScaleByStatType()
        {
            var stats = new PlayerStats();
            stats.Add(Stat.Damage, 0.2f);
            stats.Add(Stat.Area, 0.1f);
            stats.Add(Stat.ReloadSpeed, 0.08f);
            stats.Add(Stat.Capacity, 0.5f);

            var n = WeaponCatalog.Build(WeaponId.Launcher).Compute(1, false, stats);

            Assert.AreEqual(36f, n.Damage, 1e-4f);
            Assert.AreEqual(5.5f, n.Area, 1e-4f);
            Assert.AreEqual(2.1f * 0.92f, n.Reload, 1e-4f);
            Assert.AreEqual(9, n.Magazine);
        }

        [Test]
        public void EveryWeaponHasFiveLevelsAndEvolution()
        {
            for (var i = 0; i < (int)WeaponId.Count; i++)
            {
                var def = WeaponCatalog.Build((WeaponId)i);

                Assert.AreEqual(WeaponDef.MaxLevel - 1, def.Levels.Length, def.Key);
                Assert.IsNotNull(def.Evolution, def.Key);
                Assert.Greater(def.Evolution.Mods.Length, 0, def.Key);
                Assert.Greater(def.BaseOf(WeaponStat.Damage), 0f, def.Key);
            }
        }

        [Test]
        public void SourceRoundTrip()
        {
            for (var i = 0; i < (int)WeaponId.Count; i++)
            {
                var id = (WeaponId)i;
                Assert.AreEqual(id, WeaponIds.FromSource(WeaponIds.SourceOf(id)));
            }

            Assert.AreEqual(WeaponId.Count, WeaponIds.FromSource(DamageSource.Queen));
        }

        [Test]
        public void TomesAddPerLevel()
        {
            var stats = new PlayerStats();

            Tomes.Apply(TomeId.Supply, 2, stats);
            Assert.AreEqual(0.30f, stats.Get(Stat.Capacity), 1e-4f);
            Assert.AreEqual(0.16f, stats.Get(Stat.ReloadSpeed), 1e-4f);

            Tomes.Apply(TomeId.Agility, 2, stats);
            Assert.AreEqual(PlayerStats.BaseDashCharges, stats.DashCharges);

            Tomes.Apply(TomeId.Agility, 1, stats);
            Assert.AreEqual(PlayerStats.BaseDashCharges, stats.DashCharges, "третий уровень — это один вызов уровня 3");

            var fresh = new PlayerStats();
            Tomes.Apply(TomeId.Agility, 3, fresh);
            Assert.AreEqual(PlayerStats.BaseDashCharges + 1, fresh.DashCharges);
        }
    }
}
