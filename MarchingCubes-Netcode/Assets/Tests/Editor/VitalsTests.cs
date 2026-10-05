using NUnit.Framework;
using UnityEngine;

namespace MineGenerator.Catacombs.Tests
{
    /// <summary>
    /// Здоровье игрока: броня из каждого укуса, потолок урона в секунду, временное здоровье
    /// поверх основного, неуязвимость счётчиком причин, возрождение.
    /// </summary>
    public sealed class VitalsTests
    {
        private static VitalsModel Fresh(float max = 100f)
        {
            var model = new VitalsModel { CapShare = 0.15f, OverflowScale = 0.07f };
            model.Reset(max);
            return model;
        }

        private static DamageHit Bites(float amount, int count) => new DamageHit
        {
            Amount = amount, Count = count, Kind = DamageKind.Bite, PerBiteArmor = true, SpiderKind = -1
        };

        private static DamageHit Plain(float amount) => DamageHit.Simple(amount, Vector3.zero, DamageKind.Other);

        [Test]
        public void ArmorIsPerBiteButNeverBelowOne()
        {
            var model = Fresh();
            model.Armor = 3f;

            // Пять укусов по 2: каждый не слабее единицы — итого 5, а не 10 − 3.
            Assert.AreEqual(5f, model.TakeDamage(Bites(10f, 5)).Dealt, 1e-4f);

            // Один укус 7 при броне 3 — 4.
            model.Reset(100f);
            model.Armor = 3f;
            Assert.AreEqual(4f, model.TakeDamage(Bites(7f, 1)).Dealt, 1e-4f);
        }

        [Test]
        public void CapPassesFullUpToShareThenOverflow()
        {
            var model = Fresh();

            // 100 HP разом: 15 в полную силу, остальные 85 — с долей 0.07.
            var result = model.TakeDamage(Plain(100f));
            Assert.AreEqual(15f + 85f * 0.07f, result.Dealt, 1e-3f);
            Assert.Greater(result.Capped, 0f);
        }

        [Test]
        public void CapWindowSlides()
        {
            var model = Fresh();

            model.TakeDamage(Plain(15f));

            // В той же секунде потолок исчерпан — следующий удар идёт долей.
            Assert.AreEqual(10f * 0.07f, model.TakeDamage(Plain(10f)).Dealt, 1e-3f);

            // Через секунду окно отъехало — снова в полную силу.
            model.Tick(1.05f);
            Assert.AreEqual(10f, model.TakeDamage(Plain(10f)).Dealt, 1e-3f);
        }

        [Test]
        public void SteadyPressureKillsAtCapPlusShareOfOverflow()
        {
            // Натиск R HP/с кадрами по 1/30: итог ≈ потолок + доля сверх него.
            var model = Fresh();
            const float pressure = 95f;
            const float dt = 1f / 30f;

            var time = 0f;
            while (!model.Dead && time < 60f)
            {
                model.TakeDamage(Plain(pressure * dt));
                model.Tick(dt);
                time += dt;
            }

            var expected = 100f / (15f + 0.07f * (pressure - 15f));
            Assert.AreEqual(expected, time, 0.4f);
        }

        [Test]
        public void TempHealthGoesFirstAndDecays()
        {
            var model = Fresh();
            model.CapShare = 1f;
            model.TakeDamage(Plain(10f));

            model.AddTemp(5f);
            Assert.AreEqual(5f, model.Temp, 1e-4f);

            // Вместе с основным не больше максимума.
            model.AddTemp(50f);
            Assert.AreEqual(10f, model.Temp, 1e-4f);

            model.Tick(1.1f);
            model.TakeDamage(Plain(4f));
            Assert.AreEqual(6f, model.Temp, 1e-3f);
            Assert.AreEqual(90f, model.Health, 1e-3f);

            // Три секунды без пополнения — тает по 2 в секунду.
            model.Tick(2.0f);
            model.Tick(1.0f);
            Assert.Less(model.Temp, 6f);
        }

        [Test]
        public void InvulnerabilityIsACounterOfReasons()
        {
            var model = Fresh();

            model.HoldInvulnerable(InvulnReason.Revive, 2f);
            model.HoldInvulnerable(InvulnReason.Dash, 0.2f);

            model.Tick(0.5f);

            // Рывок кончился, возрождение держит.
            Assert.IsTrue(model.Invulnerable);
            Assert.IsTrue(model.TakeDamage(Plain(50f)).Blocked);

            model.Tick(2f);
            Assert.IsFalse(model.Invulnerable);
        }

        [Test]
        public void DeathAndRevive()
        {
            var model = Fresh();
            model.CapShare = 2f;

            var result = model.TakeDamage(Plain(150f));
            Assert.IsTrue(result.Killed);
            Assert.IsTrue(model.Dead);

            model.Revive(0.5f, 2f);
            Assert.IsFalse(model.Dead);
            Assert.AreEqual(50f, model.Health, 1e-4f);
            Assert.IsTrue(model.Invulnerable);
        }

        [Test]
        public void MaxHealthGainAddsCurrent()
        {
            var model = Fresh();
            model.CapShare = 1f;
            model.TakeDamage(Plain(40f));

            model.SetMaxHealth(120f);
            Assert.AreEqual(80f, model.Health, 1e-4f);
        }
    }

    public sealed class DashTests
    {
        [Test]
        public void TwoChargesRechargeOneByOne()
        {
            var dash = new DashModel();
            dash.SetMaxCharges(2);
            dash.Reset();

            Assert.IsTrue(dash.TryStart());
            Assert.IsFalse(dash.TryStart(), "пока идёт рывок, второй не начинается");

            var travelled = 0f;
            for (var i = 0; i < 20; i++) travelled += dash.Step(0.01f);

            Assert.AreEqual(dash.Distance, travelled, 1e-3f);
            Assert.IsTrue(dash.TryStart());

            for (var i = 0; i < 20; i++) dash.Step(0.01f);
            Assert.IsFalse(dash.TryStart(), "зарядов нет");

            for (var i = 0; i < 170; i++) dash.Step(0.01f);
            Assert.IsTrue(dash.TryStart(), "за 1.6 с заряд вернулся");
        }

        [Test]
        public void ProtectedForDurationPlusGrace()
        {
            var dash = new DashModel();
            dash.Reset();
            dash.TryStart();

            dash.Step(dash.Duration);
            Assert.IsTrue(dash.Protected);

            dash.Step(dash.Grace + 0.01f);
            Assert.IsFalse(dash.Protected);
        }
    }

    public sealed class PlayerStatsTests
    {
        [Test]
        public void SumsAndDerivedValues()
        {
            var stats = new PlayerStats();

            stats.Add(Stat.Damage, 0.1f);
            stats.Add(Stat.Damage, 0.05f);
            stats.Add(Stat.MaxHealthFlat, 20f);
            stats.Add(Stat.MaxHealthPct, 0.1f);
            stats.Add(Stat.ReloadSpeed, 0.08f);
            stats.Add(Stat.DashCharges, 1f);

            Assert.AreEqual(1.15f, stats.DamageScale, 1e-4f);
            Assert.AreEqual(132f, stats.MaxHealth(100f), 1e-3f);
            Assert.AreEqual(0.92f, stats.ReloadTimeScale, 1e-4f);
            Assert.AreEqual(3, stats.DashCharges);
        }

        [Test]
        public void ReloadNeverFasterThanAThird()
        {
            var stats = new PlayerStats();
            stats.Add(Stat.ReloadSpeed, 5f);

            Assert.AreEqual(0.3f, stats.ReloadTimeScale, 1e-4f);
        }
    }
}
