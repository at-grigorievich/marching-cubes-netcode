using NUnit.Framework;

namespace MineGenerator.Catacombs.Tests
{
    /// <summary>
    /// Форма кривой давления (CLAUDE.md, «Кривая давления»): затишье на старте, два гребня
    /// на долях 0.4 и 0.7, передышка между ними, каждое затишье тяжелее прежнего.
    /// Числа в таблице CLAUDE.md сняты с этих же умолчаний.
    /// </summary>
    public sealed class RunCurveTests
    {
        private static RunCurve Curve() => new RunCurve();

        [Test]
        public void StartsInLull()
        {
            var curve = Curve();

            Assert.AreEqual(-1f, curve.Wave(0f), 1e-4f, "в начале — дно волны");
            Assert.AreEqual(0.65f, curve.Intensity(0f), 0.01f, "давление в затишье по таблице");
        }

        [Test]
        public void PeaksSitOnTheirShares()
        {
            var curve = Curve();

            Assert.AreEqual(1f, curve.Wave(curve.firstPeak), 1e-4f, "первый гребень");
            Assert.AreEqual(1f, curve.Wave(curve.secondPeak), 1e-4f, "второй гребень");
            Assert.AreEqual(-1f, curve.Wave(curve.lull), 1e-4f, "передышка — дно");
        }

        [Test]
        public void SecondPeakHarderThanFirst_LullHarderThanStart()
        {
            var curve = Curve();

            Assert.Greater(curve.Intensity(curve.secondPeak), curve.Intensity(curve.firstPeak));
            Assert.Greater(curve.Intensity(curve.lull), curve.Intensity(0f),
                "рост умножает волну: каждое затишье тяжелее прежнего");
        }

        [Test]
        public void PhaseIsMonotonic()
        {
            var curve = Curve();
            var previous = curve.Phase(0f);

            for (var i = 1; i <= 400; i++)
            {
                var phase = curve.Phase(i / 400f);
                Assert.GreaterOrEqual(phase, previous, $"фаза не идёт вспять на t={i / 400f}");
                previous = phase;
            }

            Assert.AreEqual(4f, curve.Phase(1f), 1e-4f);
        }

        [Test]
        public void WaveHasNoKinks()
        {
            // Вторая разность на шаге 1/400 — тот же замер, что в CatacombRunCheck (0.0016).
            var curve = Curve();
            const float step = 1f / 400f;
            var worst = 0f;

            for (var i = 1; i < 400; i++)
            {
                var t = i * step;
                var second = curve.Wave(t - step) - 2f * curve.Wave(t) + curve.Wave(t + step);
                worst = System.Math.Max(worst, System.Math.Abs(second));
            }

            Assert.Less(worst, 0.005f, "на стыках кусков фазы излома нет");
        }
    }
}
