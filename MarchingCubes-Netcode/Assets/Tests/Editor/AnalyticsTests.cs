using NUnit.Framework;

namespace MineGenerator.Catacombs.Tests
{
    /// <summary>Аналитика (M6.2): поля без культуры системы, журнал и счётчики, готовность и первый ввод — по разу.</summary>
    public sealed class AnalyticsTests
    {
        private IAnalytics _saved;
        private LocalAnalytics _log;

        [SetUp]
        public void SetUp()
        {
            _saved = Analytics.Sink;
            _log = new LocalAnalytics();
            Analytics.Sink = _log;
        }

        [TearDown]
        public void TearDown() => Analytics.Sink = _saved;

        [Test]
        public void FieldsAreInvariantAndOrdered()
        {
            Analytics.F("tier", 2).F("time", 187.456f).F("weapon", WeaponId.Shotgun).Send("death");

            var e = _log.Last("death");
            Assert.IsTrue(e.HasValue);
            Assert.AreEqual("tier=2;time=187.46;weapon=Shotgun", e.Value.Fields, "точка, а не запятая — разбор на любом бэкенде");
        }

        [Test]
        public void CountsAndRingBuffer()
        {
            for (var i = 0; i < LocalAnalytics.Capacity + 10; i++) Analytics.Event("ad");

            Assert.AreEqual(LocalAnalytics.Capacity + 10, _log.Count("ad"));
            Assert.AreEqual(LocalAnalytics.Capacity, _log.Recent.Count, "журнал не растёт без предела");
        }

        [Test]
        public void SecondWriterDoesNotLeakFields()
        {
            Analytics.F("a", 1).Send("one");
            Analytics.F("b", 2).Send("two");

            Assert.AreEqual("b=2", _log.Last("two").Value.Fields);
        }
    }
}
