using NUnit.Framework;

namespace MineGenerator.Catacombs.Tests
{
    /// <summary>
    /// Площадки (M5): частота рекламы, ящик дня, выбор языка, разбор ответов SDK, разметка геймплея
    /// без повторов и время, которое реклама отпускает при любом исходе.
    /// </summary>
    public sealed class PlatformTests
    {
        private bool _apply;

        [SetUp]
        public void SetUp()
        {
            _apply = GameClock.ApplyToEngine;
            GameClock.ApplyToEngine = false;
            GameClock.ResetAll();

            Platform.Set(new EditorPlatform());
            EditorPlatform.GameplayStarts = 0;
            EditorPlatform.GameplayStops = 0;
            EditorPlatform.ReadyCalls = 0;
            EditorPlatform.NextAd = AdResult.Completed;
        }

        [TearDown]
        public void TearDown()
        {
            EditorPlatform.NextAd = AdResult.Completed;
            GameClock.ResetAll();
            GameClock.ApplyToEngine = _apply;
            Platform.Set(null);
        }

        // ------------------------------------------------------------------ реклама

        [Test]
        public void NoInterstitialBeforeThirdRunAndNotTooOften()
        {
            var profile = PlayerProfile.CreateNew(0);
            const long now = 1_000_000;

            profile.Stats.Runs = 1;
            Assert.IsFalse(AdPolicy.CanInterstitial(profile, now, true), "перед вторым забегом — нет");

            profile.Stats.Runs = 2;
            Assert.IsTrue(AdPolicy.CanInterstitial(profile, now, true), "перед третьим — можно");

            AdPolicy.MarkInterstitial(profile, now);
            Assert.IsFalse(AdPolicy.CanInterstitial(profile, now + 179, true), "реже раза в 3 минуты");
            Assert.IsTrue(AdPolicy.CanInterstitial(profile, now + 180, true));
        }

        [Test]
        public void BetweenTiersIgnoresRunCount()
        {
            var profile = PlayerProfile.CreateNew(0);
            profile.Stats.Runs = 0;

            Assert.IsTrue(AdPolicy.CanInterstitial(profile, 1_000_000, false), "между ярусами — только частота");
        }

        [Test]
        public void DailyBoxOncePerDay()
        {
            var profile = PlayerProfile.CreateNew(0);

            Assert.AreEqual(AdPolicy.DailyBoxChitin, AdPolicy.ClaimDailyBox(profile, 500), 1e-3f);
            Assert.AreEqual(0f, AdPolicy.ClaimDailyBox(profile, 500), 1e-3f, "второй раз в тот же день — ничего");
            Assert.IsTrue(AdPolicy.CanClaimDailyBox(profile, 501));
            Assert.AreEqual(AdPolicy.DailyBoxChitin, profile.Chitin, 1e-3f);
        }

        [Test]
        public void AdReleasesTimeWhateverTheOutcome()
        {
            foreach (var outcome in new[] { AdResult.Completed, AdResult.Skipped, AdResult.Failed })
            {
                EditorPlatform.NextAd = outcome;
                bool? rewarded = null;

                Platform.Rewarded("test", ok => rewarded = ok);

                Assert.IsFalse(GameClock.IsHeld(ClockReason.Ad), outcome + ": время отпущено");
                Assert.AreEqual(outcome == AdResult.Completed, rewarded, outcome + ": награда только за досмотр");
            }
        }

        // ------------------------------------------------------------------ разметка

        [Test]
        public void GameplayMarkingIsIdempotentAndReadyOnce()
        {
            Platform.SetGameplay(true);
            Platform.SetGameplay(true);
            Platform.SetGameplay(false);
            Platform.SetGameplay(false);
            Platform.SetGameplay(true);

            Assert.AreEqual(2, EditorPlatform.GameplayStarts, "старт подряд дважды не размечается");
            Assert.AreEqual(1, EditorPlatform.GameplayStops);

            Platform.MarkReady();
            Platform.MarkReady();
            Assert.AreEqual(1, EditorPlatform.ReadyCalls, "готовность — ровно один раз");
        }

        // ------------------------------------------------------------------ качество

        [Test]
        public void QualityByDeviceUnlessChosen()
        {
            Assert.AreEqual(QualityTier.Low, QualityPolicy.Resolve(-1, DeviceKind.Mobile, 0));
            Assert.AreEqual(QualityTier.Medium, QualityPolicy.Resolve(-1, DeviceKind.Tablet, 0));
            Assert.AreEqual(QualityTier.High, QualityPolicy.Resolve(-1, DeviceKind.Desktop, 0), "память не сообщена — высокое");
            Assert.AreEqual(QualityTier.Medium, QualityPolicy.Resolve(-1, DeviceKind.Desktop, 4096), "Chromebook с 4 ГБ");
            Assert.AreEqual(QualityTier.High, QualityPolicy.Resolve(0, DeviceKind.Mobile, 0), "выбор игрока важнее устройства");
        }

        [Test]
        public void SlowFramesStepDownOnce()
        {
            Assert.AreEqual(QualityTier.High, QualityPolicy.StepDown(QualityTier.High, 20f));
            Assert.AreEqual(QualityTier.Medium, QualityPolicy.StepDown(QualityTier.High, 30f));
            Assert.AreEqual(QualityTier.Low, QualityPolicy.StepDown(QualityTier.Low, 60f), "ниже некуда");
        }

        [Test]
        public void LowQualityValueCompensatesPopulation()
        {
            var low = QualityPreset.For(QualityTier.Low);

            // Меньше пауков — дороже каждый: опыта за минуту столько же, сколько на «Высоком».
            Assert.AreEqual(1f, low.Population * low.ValueScale, 1e-4f);
            Assert.AreEqual(600, low.CrowdCapacity);
            Assert.AreEqual(8, low.ScorchSlots);
            Assert.IsFalse(low.Dust);
        }

        // ------------------------------------------------------------------ язык и устройство

        [Test]
        public void LanguageChosenThenPlatformThenEnglish()
        {
            Assert.AreEqual("tr", Loc.Resolve("tr", "ru"), "выбранный в настройках важнее площадки");
            Assert.AreEqual("ru", Loc.Resolve("", "ru"));
            Assert.AreEqual("ru", Loc.Resolve("", "uk"), "украинский на Яндексе — русский");
            Assert.AreEqual("ru", Loc.Resolve("", "kk"));
            Assert.AreEqual("tr", Loc.Resolve("", "az"));
            Assert.AreEqual("en", Loc.Resolve("", "en-US"));
            Assert.AreEqual("en", Loc.Resolve("", "de"), "нет в таблице — английский");
            Assert.AreEqual("en", Loc.Resolve("", ""));
        }

        [Test]
        public void SdkStringsParse()
        {
            Assert.AreEqual("ru", Platform.ParseLanguage("ru"));
            Assert.AreEqual("en", Platform.ParseLanguage("en-US"));
            Assert.AreEqual("tr", Platform.ParseLanguage("tr_TR"));
            Assert.AreEqual("", Platform.ParseLanguage(""));

            Assert.AreEqual(DeviceKind.Mobile, Platform.ParseDevice("mobile", DeviceKind.Desktop));
            Assert.AreEqual(DeviceKind.Tablet, Platform.ParseDevice("tablet", DeviceKind.Desktop));
            Assert.AreEqual(DeviceKind.Desktop, Platform.ParseDevice("tv", DeviceKind.Mobile));
            Assert.AreEqual(DeviceKind.Mobile, Platform.ParseDevice("", DeviceKind.Mobile), "пусто — что знает движок");
        }
    }
}
