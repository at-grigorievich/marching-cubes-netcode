using NUnit.Framework;

namespace MineGenerator.Catacombs.Tests
{
    /// <summary>
    /// Перевод прежних «попаданий» в HP: числа, на которых держится баланс в CLAUDE.md,
    /// обязаны переехать без изменений — граната (3 попадания) убивает мелочь (1) одним
    /// взрывом, тарантула (4) — двумя.
    /// </summary>
    public sealed class CombatUnitsTests
    {
        [Test]
        public void OneHitIsTenHp()
        {
            Assert.AreEqual(10f, CombatUnits.HitPoints);
            Assert.AreEqual(30f, CombatUnits.Hits(3));
        }

        [TestCase(1, 1)]
        [TestCase(2, 1)]
        [TestCase(4, 2)]
        public void GrenadeKillsLikeBefore(int kindHits, int expectedGrenades)
        {
            var health = CombatUnits.Hits(kindHits);
            var grenade = CombatUnits.Hits(3);

            var grenades = 0;
            while (health > 0.01f)
            {
                health -= grenade;
                grenades++;
            }

            Assert.AreEqual(expectedGrenades, grenades);
        }

        [Test]
        public void SourceCountCoversAllSources()
        {
            // Счётчики убийств — массив по DamageSource.Count: новый источник в конце перечисления.
            Assert.AreEqual((int)DamageSource.Environment + 1, (int)DamageSource.Count);
        }
    }
}
