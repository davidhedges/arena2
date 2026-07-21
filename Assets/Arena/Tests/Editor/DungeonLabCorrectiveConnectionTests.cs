#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;

namespace Arena.Tests.Editor
{
    public sealed class DungeonLabCorrectiveConnectionTests
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

        [Test]
        public void FixedAndRegressionProductionSeeds_AreHardValidAndPreservePlans()
        {
            Dictionary<string, string> values = Snapshot.Value;

            var preCorrectivePlanHashes = new Dictionary<int, string>
            {
                [2026072100] = "3d0bf9c14d8979934c0171d8c0e102f55c668f3e615f17a7b4adc4ab8295217f",
                [2026072101] = "1682e02f49ca41d52ae225a1fd62a7743890dfa3964ec0182f75bce9495bc384",
                [2026072103] = "7db2d9e0f10011a273b077d84e021521a8e76cfa8c8b0f8a09ac2f429f3c0c33",
                [2026072170] = "368134d11c9cb60365a35919b6d4746cc741ca6c1c7a06be12dfc0ce5612c651",
                [2026072220] = "b3593707adfd20f066b0f556b4ea20c5a2e6b1801fe77265a58cfa59b28c65b4"
            };
            foreach (KeyValuePair<int, string> expected in preCorrectivePlanHashes)
            {
                int seed = expected.Key;
                Assert.That(values[$"production.{seed}.accepted"], Is.EqualTo("True"));
                Assert.That(values[$"production.{seed}.hardValid"], Is.EqualTo("True"));
                Assert.That(
                    values[$"production.{seed}.count"],
                    Is.EqualTo(values[$"production.{seed}.desired"]));
                Assert.That(values[$"production.{seed}.externalValid"], Is.EqualTo("True"));
                Assert.That(values[$"production.{seed}.transitionHash"], Has.Length.EqualTo(64));
                Assert.That(values[$"production.{seed}.prechangePlanHash"], Is.EqualTo(expected.Value));
            }
        }

        [Test]
        public void CanonicalAndRendererPaths_AdvanceWithoutRejectedPlacements()
        {
            Dictionary<string, string> values = Snapshot.Value;

            Assert.That(values["versions.summary"], Is.EqualTo("dungeon-plan-v9"));
            Assert.That(values["versions.generator"], Is.EqualTo("route-topologies-v9"));
            Assert.That(values["renderer.accepted"], Is.EqualTo("True"));
            Assert.That(values["renderer.passed"], Is.EqualTo("True"));
            Assert.That(values["renderer.rejected"], Is.EqualTo("0"));
        }

        private static Dictionary<string, string> BuildSnapshot()
        {
            MethodInfo method = GeneratorType.GetMethod(
                "BuildCorrectiveConnectionSnapshot",
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
