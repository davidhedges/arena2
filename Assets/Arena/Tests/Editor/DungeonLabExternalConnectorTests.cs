#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;

namespace Arena.Tests.Editor
{
    public sealed class DungeonLabExternalConnectorTests
    {
        private static readonly Type GeneratorType = AppDomain.CurrentDomain
            .Load("Assembly-CSharp-Editor")
            .GetType("DungeonLab.Editor.DungeonLabGenerator", throwOnError: true)!;
        private static readonly Lazy<Dictionary<string, string>> Snapshot =
            new Lazy<Dictionary<string, string>>(BuildSnapshot);

        [Test]
        public void Policy_RealizesEveryExactCountWithUniqueDirections()
        {
            Dictionary<string, string> values = Snapshot.Value;

            Assert.That(values["policy.version"], Is.EqualTo("external-connector-promontory-v1"));
            for (int count = 1; count <= 4; count++)
            {
                Assert.That(values[$"resolver.{count}.resolved"], Is.EqualTo("True"));
                Assert.That(values[$"resolver.{count}.count"], Is.EqualTo(count.ToString()));
                Assert.That(values[$"resolver.{count}.uniqueDirections"], Is.EqualTo(count.ToString()));
                Assert.That(values[$"resolver.{count}.addedCells"], Is.EqualTo((count * 2).ToString()));
                Assert.That(values[$"resolver.{count}.error"], Is.Empty);
            }
        }

        [Test]
        public void Resolver_RejectsAtomicallyWithStableCode()
        {
            Dictionary<string, string> values = Snapshot.Value;

            Assert.That(values["atomic.rejected"], Is.EqualTo("True"));
            Assert.That(values["atomic.unchanged"], Is.EqualTo("True"));
            Assert.That(values["atomic.code"], Is.EqualTo("True"));
        }

        // Behavioural assertions only. The former hardcoded per-seed plan hashes
        // were a closed-phase identity lock: they rotted on every unrelated
        // change and trained the suite to run red. Determinism is proven by
        // DungeonLabDeterminismTests running one seed twice, not by a stored
        // digest that no longer corresponds to any intended behaviour.
        [Test]
        public void FixedAndRegressionProductionSeeds_AreHardValidWithExactConnectors()
        {
            Dictionary<string, string> values = Snapshot.Value;

            foreach (int seed in new[] { 2026072100, 2026072101, 2026072103, 2026072170, 2026072220 })
            {
                Assert.That(values[$"production.{seed}.accepted"], Is.EqualTo("True"));
                Assert.That(values[$"production.{seed}.hardValid"], Is.EqualTo("True"));
                Assert.That(
                    values[$"production.{seed}.count"],
                    Is.EqualTo(values[$"production.{seed}.desired"]));
                Assert.That(values[$"production.{seed}.externalValid"], Is.EqualTo("True"));
                Assert.That(values[$"production.{seed}.transitionHash"], Has.Length.EqualTo(64));
            }
        }

        [Test]
        public void CanonicalAndRendererPaths_AdvanceWithoutRejectedPlacements()
        {
            Dictionary<string, string> values = Snapshot.Value;

            Assert.That(values["renderer.accepted"], Is.EqualTo("True"));
            Assert.That(values["renderer.passed"], Is.EqualTo("True"));
            Assert.That(values["renderer.rejected"], Is.EqualTo("0"));
        }

        private static Dictionary<string, string> BuildSnapshot()
        {
            MethodInfo method = GeneratorType.GetMethod(
                "BuildExternalConnectorSnapshot",
                BindingFlags.Static | BindingFlags.NonPublic)!;
            Assert.That(method, Is.Not.Null, "Missing corrective-connection diagnostic.");
            return Parse((string)method.Invoke(null, Array.Empty<object>())!);
        }

        private static Dictionary<string, string> Parse(string snapshot)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string line in snapshot.Split('\n'))
            {
                int separator = line.IndexOf('=');
                if (separator >= 0)
                    result[line.Substring(0, separator)] = line.Substring(separator + 1);
            }

            return result;
        }
    }
}
