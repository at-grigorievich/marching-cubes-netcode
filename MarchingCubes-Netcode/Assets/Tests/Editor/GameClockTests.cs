using NUnit.Framework;

namespace MineGenerator.Catacombs.Tests
{
    /// <summary>
    /// Владелец времени: причины складываются минимумом и не снимают друг друга, звук стоит
    /// только от паузы, рекламы и фокуса, стоп-кадр кончается сам и не чаще отката.
    /// Движок не трогаем (<see cref="GameClock.ApplyToEngine"/>): мерим чистую логику.
    /// </summary>
    public sealed class GameClockTests
    {
        private bool _apply;

        [SetUp]
        public void SetUp()
        {
            _apply = GameClock.ApplyToEngine;
            GameClock.ApplyToEngine = false;
            GameClock.ResetAll();
        }

        [TearDown]
        public void TearDown()
        {
            GameClock.ResetAll();
            GameClock.ApplyToEngine = _apply;
        }

        [Test]
        public void NoReasons_FullSpeed()
        {
            Assert.AreEqual(1f, GameClock.Scale);
            Assert.IsFalse(GameClock.AudioPaused);
        }

        [Test]
        public void ReasonsDoNotReleaseEachOther()
        {
            // Реклама кончилась посреди выбора карточки — время обязано остаться стоять.
            GameClock.Hold(ClockReason.LevelUp);
            GameClock.Hold(ClockReason.Ad);
            GameClock.Release(ClockReason.Ad);

            Assert.AreEqual(0f, GameClock.Scale);
            Assert.IsTrue(GameClock.IsHeld(ClockReason.LevelUp));

            GameClock.Release(ClockReason.LevelUp);
            Assert.AreEqual(1f, GameClock.Scale);
        }

        [Test]
        public void ScaleIsMinimumOfReasons()
        {
            GameClock.Hold(ClockReason.Death, 0.3f);
            Assert.AreEqual(0.3f, GameClock.Scale, 1e-5f);

            GameClock.Hold(ClockReason.Pause);
            Assert.AreEqual(0f, GameClock.Scale);

            GameClock.Release(ClockReason.Pause);
            Assert.AreEqual(0.3f, GameClock.Scale, 1e-5f);
        }

        [Test]
        public void AudioStopsOnlyForPauseAdFocus()
        {
            GameClock.Hold(ClockReason.LevelUp);
            GameClock.Hold(ClockReason.Death, 0.3f);
            Assert.IsFalse(GameClock.AudioPaused, "выбор карточки и смерть звук не глушат");

            GameClock.Hold(ClockReason.FocusLost);
            Assert.IsTrue(GameClock.AudioPaused);
        }

        [Test]
        public void HitStopEndsByItselfAndRespectsCooldown()
        {
            Assert.IsTrue(GameClock.HitStop(0.05f));
            Assert.Less(GameClock.Scale, 1f);

            Assert.IsFalse(GameClock.HitStop(0.05f), "второй подряд — в откате");

            GameClock.Tick(0.06f);
            Assert.AreEqual(1f, GameClock.Scale, "стоп-кадр кончился сам");

            GameClock.Tick(GameClock.HitStopCooldown);
            Assert.IsTrue(GameClock.HitStop(0.05f), "после отката — снова можно");
        }

        [Test]
        public void HitStopIsCapped()
        {
            GameClock.HitStop(10f);
            GameClock.Tick(GameClock.HitStopMax + 0.001f);

            Assert.AreEqual(1f, GameClock.Scale, "длинный стоп-кадр обрезан");
        }

        [Test]
        public void NoHitStopUnderPause()
        {
            GameClock.Hold(ClockReason.Pause);
            Assert.IsFalse(GameClock.HitStop(0.05f));
        }
    }
}
