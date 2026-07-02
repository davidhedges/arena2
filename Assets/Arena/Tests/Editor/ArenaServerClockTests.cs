#nullable enable
using System;
using System.Reflection;
using NUnit.Framework;

namespace Arena.EditModeTests
{
    /// <summary>
    /// Feel-audit F2b: the ping_clock sampler feeds
    /// ArenaServerClock.RecordReducerSampleMs. Precise round-trip samples must
    /// be able to pull the estimate *down* from the one-way monotonic-max
    /// estimate (which by design never decreases on its own), and samples with
    /// implausible round-trip times must be rejected outright.
    /// </summary>
    public class ArenaServerClockTests
    {
        private const string ClockTypeName = "Arena.Network.ArenaServerClock";
        private static readonly Assembly RuntimeAssembly = AppDomain.CurrentDomain.Load("Assembly-CSharp");
        private static readonly Type ClockType = RuntimeAssembly.GetType(ClockTypeName)
            ?? throw new InvalidOperationException($"Missing runtime type {ClockTypeName}");

        [SetUp]
        public void SetUp() => Invoke("Reset");

        [TearDown]
        public void TearDown() => Invoke("Reset");

        [Test]
        public void PreciseSamples_OverrideMonotonicMaxEstimate_Downward()
        {
            // One-way observed row: server appears 250 ms ahead of the client.
            Invoke("RecordObservedServerTimestampMs", 1_000_250L, 1_000_000L);
            Assert.That(OffsetMs(), Is.EqualTo(250.0).Within(0.5));

            // First precise ping (true offset 100 ms, RTT 100 ms). The
            // correction exceeds the snap threshold, so a single sample must
            // not move the estimate on its own.
            Invoke("RecordReducerSampleMs", 2_000_000L, 2_000_150L, 2_000_100L);
            Assert.That(OffsetMs(), Is.EqualTo(250.0).Within(0.5),
                "an uncorroborated large correction must not snap immediately");

            // Replicated row timestamps keep flooding in between pings; they
            // must not evict the pending precise sample or move the estimate.
            for (long i = 1; i <= 40; i++)
                Invoke("RecordObservedServerTimestampMs", 2_000_250L + i * 33L, 2_000_000L + i * 33L);

            // A second corroborating ping pulls the estimate down — something
            // the monotonic-max path can never do.
            Invoke("RecordReducerSampleMs", 4_000_000L, 4_000_150L, 4_000_100L);
            Assert.That(OffsetMs(), Is.EqualTo(100.0).Within(0.5),
                "corroborated precise samples must override the monotonic-max estimate downward");
            Assert.That(GetProp<long>("LastRoundTripMs"), Is.EqualTo(100L));
        }

        [Test]
        public void ReducerSample_WithRttOver1000Ms_IsRejected()
        {
            // RTT 1500 ms: rejected before it can seed an estimate.
            Invoke("RecordReducerSampleMs", 1_000_000L, 1_000_900L, 1_001_500L);
            Assert.That(GetProp<bool>("HasEstimate"), Is.False);
            Assert.That(GetProp<bool>("HasPreciseSample"), Is.False);
            Assert.That(GetProp<long>("LastRoundTripMs"), Is.EqualTo(0L));

            // With a healthy estimate in place, an implausible sample must
            // leave both the estimate and the RTT stats untouched.
            Invoke("RecordReducerSampleMs", 2_000_000L, 2_000_150L, 2_000_100L);
            double before = OffsetMs();
            Invoke("RecordReducerSampleMs", 3_000_000L, 3_009_999L, 3_001_501L);
            Assert.That(OffsetMs(), Is.EqualTo(before).Within(0.001));
            Assert.That(GetProp<long>("LastRoundTripMs"), Is.EqualTo(100L));
        }

        [Test]
        public void RttStats_ReportNearestRankPercentiles()
        {
            // Five accepted pings with RTTs 100/200/300/400/1000 ms, all with
            // a true offset of 100 ms.
            Invoke("RecordReducerSampleMs", 1_000_000L, 1_000_150L, 1_000_100L);
            Invoke("RecordReducerSampleMs", 2_000_000L, 2_000_200L, 2_000_200L);
            Invoke("RecordReducerSampleMs", 3_000_000L, 3_000_250L, 3_000_300L);
            Invoke("RecordReducerSampleMs", 4_000_000L, 4_000_300L, 4_000_400L);
            Invoke("RecordReducerSampleMs", 5_000_000L, 5_000_600L, 5_001_000L);

            (bool ok, long last, long p50, long p95) = GetRttStats();
            Assert.That(ok, Is.True);
            Assert.That(last, Is.EqualTo(1000L));
            Assert.That(p50, Is.EqualTo(300L));
            Assert.That(p95, Is.EqualTo(400L));
        }

        [Test]
        public void RttStats_AreUnavailableWithoutPreciseSamples()
        {
            Invoke("RecordObservedServerTimestampMs", 1_000_250L, 1_000_000L);
            Assert.That(GetRttStats().ok, Is.False);
        }

        private static double OffsetMs() => GetProp<double>("EstimatedServerMinusClientMs");

        private static (bool ok, long last, long p50, long p95) GetRttStats()
        {
            var args = new object[] { 0L, 0L, 0L };
            MethodInfo method = ClockType.GetMethod("TryGetRttStats", BindingFlags.Public | BindingFlags.Static)
                ?? throw new InvalidOperationException("Missing ArenaServerClock.TryGetRttStats");
            bool ok = (bool)method.Invoke(null, args)!;
            return (ok, (long)args[0], (long)args[1], (long)args[2]);
        }

        private static void Invoke(string name, params object[] args)
        {
            MethodInfo method = ClockType.GetMethod(name, BindingFlags.Public | BindingFlags.Static)
                ?? throw new InvalidOperationException($"Missing ArenaServerClock.{name}");
            method.Invoke(null, args);
        }

        private static T GetProp<T>(string name)
        {
            PropertyInfo property = ClockType.GetProperty(name, BindingFlags.Public | BindingFlags.Static)
                ?? throw new InvalidOperationException($"Missing ArenaServerClock.{name}");
            return (T)property.GetValue(null)!;
        }
    }
}
