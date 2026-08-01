#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;

namespace Arena.Tests.Editor
{
    public sealed class DungeonLabLayeredProductionTests
    {
        private const int AnySeed = 2026072100;
        private static readonly string[] ProductionTopologies =
        {
            "hanging-ring",
            "layered-cascade",
            "vertical-braid"
        };

        private const string LayeredRecipes =
            "episode_hanging_bridge_court_01|episode_spiral_return_01|episode_switchback_mezzanine_01";

        private static readonly Type GeneratorType = AppDomain.CurrentDomain
            .Load("Assembly-CSharp-Editor")
            .GetType("DungeonLab.Editor.DungeonLabGenerator", throwOnError: true)!;

        [Test]
        public void WeightedCorpus_ContainsOnlyTheThreeLayeredReplacements()
        {
            Dictionary<string, string> snapshot = ProductionSnapshot();

            Assert.That(snapshot["deprecated.count"], Is.EqualTo("10"));
            Assert.That(snapshot["production.ids"], Is.EqualTo(
                "hanging-ring|layered-cascade|vertical-braid"));
            Assert.That(snapshot["selector.weights"], Is.EqualTo(
                "aperture-gallery:0|atrium-hub:0|atrium-ring:0|deep-processional:0|" +
                "descent-shaft:0|hanging-ring:1|layered-cascade:1|processional-spine:0|" +
                "ridge-ravine:0|sunken-basin:0|terraced-cascade:0|twin-wing-keep:0|" +
                "vertical-braid:1"));

            foreach (string topology in ProductionTopologies)
            {
                Assert.That(snapshot["selector.distribution"],
                    Does.Not.Contain($"{topology}:0"), snapshot["selector.distribution"]);
            }
        }

        [Test]
        public void EveryProductionTopology_ResolvesThreeDistinctLayeredEpisodes()
        {
            Dictionary<string, string> snapshot = ProductionSnapshot();

            foreach (string topology in ProductionTopologies)
            {
                Assert.That(snapshot[$"{topology}.accepted"], Is.EqualTo("True"), topology);
                Assert.That(snapshot[$"{topology}.hardValid"], Is.EqualTo("True"), topology);
                Assert.That(snapshot[$"{topology}.richLayering"], Is.EqualTo("True"), topology);
                Assert.That(int.Parse(snapshot[$"{topology}.stackedSurfaces"]),
                    Is.GreaterThanOrEqualTo(48), topology);
                Assert.That(int.Parse(snapshot[$"{topology}.layerOffsetConnectionEnds"]),
                    Is.GreaterThanOrEqualTo(3), topology);
                Assert.That(snapshot[$"{topology}.recipes"], Is.EqualTo(LayeredRecipes), topology);
                Assert.That(snapshot[$"{topology}.richLayeringMessage"],
                    Does.Contain("3 distinct layered episodes"), topology);
                Assert.That(snapshot[$"{topology}.richLayeringMessage"],
                    Does.Contain("3 with internal vertical transitions"), topology);
            }
        }

        [Test]
        public void EveryProductionTopology_RendersAndProducesCollisionWithoutRepair()
        {
            Dictionary<string, string> snapshot = ProductionSnapshot();
            foreach (string topology in ProductionTopologies)
            {
                int seed = int.Parse(snapshot[$"{topology}.seed"]);
                string rendered = InvokeSnapshot("BuildRendererProbeSnapshot", seed);
                Dictionary<string, string> report = ParseSnapshot(rendered);

                Assert.That(report["accepted"], Is.EqualTo("true"), rendered);
                Assert.That(report["boundary"], Is.EqualTo("true"), rendered);
                Assert.That(report["renderer.passed"], Is.EqualTo("true"), rendered);
                Assert.That(report["renderer.rejectedPlacements"], Is.EqualTo("0"), rendered);
                Assert.That(report["collision.passed"], Is.EqualTo("true"), rendered);
            }
        }

        [Test]
        public void DeprecatedTopology_CannotBeForcedThroughAGenerationEntryPoint()
        {
            MethodInfo method = GeneratorType.GetMethod(
                "ForceRouteTopology",
                BindingFlags.Static | BindingFlags.NonPublic)!;

            TargetInvocationException exception = Assert.Throws<TargetInvocationException>(
                () => method.Invoke(null, new object[] { "processional-spine" }))!;
            Assert.That(exception.InnerException, Is.TypeOf<InvalidOperationException>());
            Assert.That(exception.InnerException!.Message,
                Does.Contain("ROUTE_TOPOLOGY_DEPRECATED"));
        }

        private static Dictionary<string, string> ProductionSnapshot()
        {
            return ParseSnapshot(InvokeSnapshot("BuildLayeredProductionSnapshot", AnySeed));
        }

        private static string InvokeSnapshot(string methodName, int seed)
        {
            MethodInfo method = GeneratorType.GetMethod(
                methodName,
                BindingFlags.Static | BindingFlags.NonPublic)!;
            Assert.That(method, Is.Not.Null, $"Missing diagnostic method {methodName}.");
            return (string)method.Invoke(null, new object[] { seed })!;
        }

        private static Dictionary<string, string> ParseSnapshot(string snapshot)
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
