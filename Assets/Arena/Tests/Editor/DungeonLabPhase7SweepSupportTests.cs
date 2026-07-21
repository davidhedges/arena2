#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;

namespace Arena.Tests.Editor
{
    public sealed class DungeonLabPhase7SweepSupportTests
    {
        private static readonly Type GeneratorType = AppDomain.CurrentDomain
            .Load("Assembly-CSharp-Editor")
            .GetType("DungeonLab.Editor.DungeonLabGenerator", throwOnError: true)!;
        private static readonly Lazy<Dictionary<string, string>> Snapshot =
            new Lazy<Dictionary<string, string>>(BuildSnapshot);

        [Test]
        public void SweepRangeAndReliabilityFloors_MatchTheApprovedBudget()
        {
            Dictionary<string, string> values = Snapshot.Value;

            Assert.That(values["range.first"], Is.EqualTo("2026072300"));
            Assert.That(values["range.last"], Is.EqualTo("2026074299"));
            Assert.That(values["range.count"], Is.EqualTo("2000"));
            Assert.That(values["reliability.overall"], Is.EqualTo("1990"));
            Assert.That(values["reliability.processional"], Is.EqualTo("990"));
            Assert.That(values["reliability.atrium"], Is.EqualTo("495"));
            Assert.That(values["reliability.twinWing"], Is.EqualTo("495"));
        }

        [Test]
        public void AttemptAndPerformanceLimits_MatchTheApprovedBudget()
        {
            Dictionary<string, string> values = Snapshot.Value;

            Assert.That(values["attempt.p95"], Is.EqualTo("1"));
            Assert.That(values["attempt.max"], Is.EqualTo("2"));
            Assert.That(values["performance.meanMs"], Is.EqualTo("125"));
            Assert.That(values["performance.p95Ms"], Is.EqualTo("200"));
            Assert.That(values["performance.maxMs"], Is.EqualTo("750"));
            Assert.That(values["performance.loopSeconds"], Is.EqualTo("250"));
            Assert.That(values["warmup.count"], Is.EqualTo("40"));
        }

        [Test]
        public void TimingDistribution_UsesNearestRankWithoutWeakeningLimits()
        {
            Dictionary<string, string> values = Snapshot.Value;

            Assert.That(values["distribution.p50"], Is.EqualTo("100"));
            Assert.That(values["distribution.p95"], Is.EqualTo("190"));
            Assert.That(values["distribution.max"], Is.EqualTo("200"));
        }

        [Test]
        public void DeterminismEvidence_IsStableSensitiveAndSeparatedByRun()
        {
            Dictionary<string, string> values = Snapshot.Value;

            Assert.That(values["determinism.identical"], Is.EqualTo("True"));
            Assert.That(values["determinism.changeDetected"], Is.EqualTo("True"));
            Assert.That(values["paths.distinct"], Is.EqualTo("True"));
            Assert.That(values["versions.summary"], Is.EqualTo("dungeon-plan-v8"));
            Assert.That(values["versions.generator"], Is.EqualTo("route-topologies-v8"));
        }

        [Test]
        public void OutlierDiagnostic_IsSeparateAndRetainsTheLockedMaximum()
        {
            Dictionary<string, string> values = Snapshot.Value;

            Assert.That(values["outlierDiagnostic.acceptanceSweep"], Is.EqualTo("False"));
            Assert.That(values["outlierDiagnostic.stageCount"], Is.EqualTo("27"));
            Assert.That(values["outlierDiagnostic.maximumMs"], Is.EqualTo("750"));
        }

        [Test]
        public void TierRetryOptimization_ReusesPreparedImmutableSynthesisCatalogs()
        {
            MethodInfo method = GeneratorType.GetMethod(
                "BuildPhase7TierRetryOptimizationSnapshot",
                BindingFlags.Static | BindingFlags.NonPublic)!;
            Assert.That(method, Is.Not.Null, "Missing Phase 7 tier-retry optimization diagnostic.");
            Dictionary<string, string> values = Parse(
                (string)method.Invoke(null, Array.Empty<object>())!);

            Assert.That(int.Parse(values["active.designs"]), Is.GreaterThan(0));
            Assert.That(values["active.options"], Is.EqualTo(values["active.designs"]));
            Assert.That(values["active.reused"], Is.EqualTo("True"));
            Assert.That(int.Parse(values["stairwell.designs"]), Is.GreaterThan(0));
            Assert.That(values["stairwell.options"], Is.EqualTo(values["stairwell.designs"]));
            Assert.That(values["stairwell.reused"], Is.EqualTo("True"));
        }

        [Test]
        public void TierRetryOptimization_PreservesTheExactOutlierSeedResult()
        {
            MethodInfo method = GeneratorType.GetMethod(
                "BuildPhase7TierRetryPreservationSnapshot",
                BindingFlags.Static | BindingFlags.NonPublic)!;
            Assert.That(method, Is.Not.Null, "Missing Phase 7 tier-retry preservation diagnostic.");
            Dictionary<string, string> values = Parse(
                (string)method.Invoke(null, Array.Empty<object>())!);

            Assert.That(values["seed"], Is.EqualTo("2026072486"));
            Assert.That(values["accepted"], Is.EqualTo("True"));
            Assert.That(values["hardValid"], Is.EqualTo("True"));
            Assert.That(values["layoutAttempts"], Is.EqualTo("2"));
            Assert.That(values["stairPlacementRejections"], Is.EqualTo("32"));
            Assert.That(
                values["canonicalHash"],
                Is.EqualTo("237cb023d29d8540ea6aa8cfb3bbd56055254612604d26cbd7731ec253288289"));
        }

        private static Dictionary<string, string> BuildSnapshot()
        {
            MethodInfo method = GeneratorType.GetMethod(
                "BuildPhase7SweepSupportSnapshot",
                BindingFlags.Static | BindingFlags.NonPublic)!;
            Assert.That(method, Is.Not.Null, "Missing Phase 7 sweep-support diagnostic.");
            return Parse((string)method.Invoke(null, Array.Empty<object>())!);
        }

        private static Dictionary<string, string> Parse(string snapshot)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string line in snapshot.Split('\n'))
            {
                int separator = line.IndexOf('=');
                if (separator >= 0)
                {
                    result[line.Substring(0, separator)] = line.Substring(separator + 1);
                }
            }

            return result;
        }
    }
}
