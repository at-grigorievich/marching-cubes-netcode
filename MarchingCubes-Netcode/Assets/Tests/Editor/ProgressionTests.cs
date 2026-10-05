using System;
using System.Linq;
using NUnit.Framework;

namespace MineGenerator.Catacombs.Tests
{
    /// <summary>Кривая опыта, очередь уровней, карточки: виды, веса, эволюция, запасные.</summary>
    public sealed class ProgressionTests
    {
        [TestCase(1, 12)]
        [TestCase(5, 46)]
        [TestCase(10, 116)]
        [TestCase(20, 346)]
        [TestCase(30, 696)]
        public void XpCurveMatchesPlan(int level, int need)
        {
            Assert.AreEqual(need, XpCurve.Need(level), 1e-3f);
        }

        [Test]
        public void SeveralLevelsQueue()
        {
            var run = new RunSession();

            // 12 до второго, 18 до третьего: 31 опыт — два уровня и единица сверху.
            Assert.AreEqual(2, run.AddXp(31f));
            Assert.AreEqual(3, run.Level);
            Assert.AreEqual(2, run.PendingLevels);
            Assert.AreEqual(1f, run.Xp, 1e-3f);
        }

        private static CardPool Pool() => new CardPool
        {
            PairOf = id => (TomeId.Reach, 3)
        };

        [Test]
        public void FreshRunOffersNewThings()
        {
            var pool = Pool();
            pool.Weapons.Add(WeaponId.Revolver);
            pool.Tomes.Add(TomeId.Might);
            pool.Tomes.Add(TomeId.Haste);

            var run = new RunSession();
            run.Weapons.Add(new OwnedWeapon { Id = WeaponId.Launcher, Level = 1 });

            var cards = CardOffer.Make(run, pool, new Random(1));

            Assert.GreaterOrEqual(cards.Count, 3);
            Assert.AreEqual(cards.Count, cards.Distinct().Count(), "карточки не повторяются");
            Assert.IsTrue(cards.All(c => c.Type != CardType.Evolution));
        }

        [Test]
        public void EvolutionGuaranteedFirstTime()
        {
            var pool = Pool();

            var run = new RunSession();
            run.Weapons.Add(new OwnedWeapon { Id = WeaponId.Launcher, Level = 5 });
            run.Tomes.Add(new OwnedTome { Id = TomeId.Reach, Level = 3 });

            var cards = CardOffer.Make(run, pool, new Random(7));

            Assert.AreEqual(CardType.Evolution, cards[0].Type);
            Assert.AreEqual(WeaponId.Launcher, cards[0].Weapon);
        }

        [Test]
        public void NoEvolutionWithoutPairTome()
        {
            var pool = Pool();

            var run = new RunSession();
            run.Weapons.Add(new OwnedWeapon { Id = WeaponId.Launcher, Level = 5 });
            run.Tomes.Add(new OwnedTome { Id = TomeId.Reach, Level = 2 });

            for (var seed = 0; seed < 50; seed++)
            {
                var cards = CardOffer.Make(run, pool, new Random(seed));
                Assert.IsTrue(cards.All(c => c.Type != CardType.Evolution));
            }
        }

        [Test]
        public void EverythingMaxedGivesFallbacks()
        {
            var pool = Pool();
            var run = new RunSession();

            foreach (var id in new[] { WeaponId.Launcher, WeaponId.Revolver, WeaponId.Shotgun })
            {
                run.Weapons.Add(new OwnedWeapon { Id = id, Level = 5, Evolved = true });
            }

            foreach (var id in new[] { TomeId.Might, TomeId.Haste, TomeId.Reach, TomeId.Supply })
            {
                run.Tomes.Add(new OwnedTome { Id = id, Level = 5 });
            }

            var cards = CardOffer.Make(run, pool, new Random(3));

            Assert.IsTrue(cards.Any(c => c.Type == CardType.Heal));
            Assert.IsTrue(cards.All(c => c.Type == CardType.Heal || c.Type == CardType.Chitin));
        }

        [Test]
        public void UpgradesTwiceAsLikelyAsNew()
        {
            // Одна пушка в руках против многих новых: улучшение должно выпадать заметно чаще,
            // чем если бы веса были равны.
            var pool = CardPool.Everything();
            pool.PairOf = id => (TomeId.Reach, 3);

            var run = new RunSession();
            run.Weapons.Add(new OwnedWeapon { Id = WeaponId.Launcher, Level = 1 });

            var upgrades = 0;
            const int trials = 2000;

            for (var seed = 0; seed < trials; seed++)
            {
                var cards = CardOffer.Make(run, pool, new Random(seed));
                if (cards.Any(c => c.Type == CardType.WeaponLevel)) upgrades++;
            }

            // Кандидатов: 1 улучшение (вес 2) + 7 новых пушек + 12 томов (вес 1 каждый) = 21.
            // При трёх карточках без возврата шанс встретить улучшение ≈ 1 − Π(1 − 2/21…) ≈ 0.27;
            // при равных весах был бы ≈ 0.15.
            Assert.Greater(upgrades / (float)trials, 0.22f);
        }

        [Test]
        public void ApplyCards()
        {
            var run = new RunSession();

            run.Apply(new Card { Type = CardType.NewWeapon, Weapon = WeaponId.Tesla });
            run.Apply(new Card { Type = CardType.WeaponLevel, Weapon = WeaponId.Tesla });
            run.Apply(new Card { Type = CardType.NewTome, Tome = TomeId.Might });
            run.Apply(new Card { Type = CardType.TomeLevel, Tome = TomeId.Might });
            run.Apply(new Card { Type = CardType.Evolution, Weapon = WeaponId.Tesla });
            run.Apply(new Card { Type = CardType.Chitin, Amount = 25f });

            Assert.AreEqual(2, run.WeaponLevel(WeaponId.Tesla));
            Assert.IsTrue(run.IsEvolved(WeaponId.Tesla));
            Assert.AreEqual(2, run.TomeLevel(TomeId.Might));
            Assert.AreEqual(25f, run.Chitin, 1e-4f);

            var stats = new PlayerStats();
            run.ApplyTomes(stats);
            Assert.AreEqual(1.2f, stats.DamageScale, 1e-4f);
        }

        [Test]
        public void CocoonPrefersEvolution()
        {
            var pool = Pool();
            var run = new RunSession();
            run.Weapons.Add(new OwnedWeapon { Id = WeaponId.Launcher, Level = 5 });
            run.Weapons.Add(new OwnedWeapon { Id = WeaponId.Revolver, Level = 2 });
            run.Tomes.Add(new OwnedTome { Id = TomeId.Reach, Level = 3 });

            Assert.AreEqual(CardType.Evolution, CardOffer.Cocoon(run, pool, new Random(1)).Type);

            run.Tomes.Clear();
            var card = CardOffer.Cocoon(run, pool, new Random(1));
            Assert.AreEqual(CardType.WeaponLevel, card.Type);
            Assert.AreEqual(WeaponId.Revolver, card.Weapon);
        }
    }
}
