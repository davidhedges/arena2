#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;

namespace Arena.Tests.Editor
{
    public sealed class DungeonLabRouteTopologyLoaderTests
    {
        private const int TopologySeed = 2026072100;
        private static readonly Type GeneratorType = AppDomain.CurrentDomain
            .Load("Assembly-CSharp-Editor")
            .GetType("DungeonLab.Editor.DungeonLabGenerator", throwOnError: true)!;

        [Test]
        public void ProductionGraph_KeepsTheExistingNodeOrderExactly()
        {
            Dictionary<string, string> graph = TopologySnapshot();

            Assert.That(graph["graph.pattern"], Is.EqualTo("processional-spine"));
            Assert.That(graph["graph.nodeIds"], Is.EqualTo(
                "arrival|threshold|choice|reveal|vista-target|ascent|approach|rejoin|culmination|" +
                "vista-source|branch-passage|branch-reward|branch-return"));
        }

        [Test]
        public void ProductionGraph_KeepsTheExistingEdgeOrderExactly()
        {
            Dictionary<string, string> graph = TopologySnapshot();

            Assert.That(graph["graph.edgeIds"], Is.EqualTo(
                "main-0-1|main-1-2|main-2-3|main-3-4|main-4-5|main-5-6|main-6-7|main-7-8|" +
                "branch-2-9|branch-9-10|branch-10-11|branch-11-12|rejoin-12-7"));
            Assert.That(graph["graph.edgeDetails"], Is.EqualTo(
                "main-0-1:arrival>threshold:LevelCorridor:0|" +
                "main-1-2:threshold>choice:Stair:4|" +
                "main-2-3:choice>reveal:LevelCorridor:0|" +
                "main-3-4:reveal>vista-target:Stair:4|" +
                "main-4-5:vista-target>ascent:Stair:4|" +
                "main-5-6:ascent>approach:Stairwell:4|" +
                "main-6-7:approach>rejoin:Stair:4|" +
                "main-7-8:rejoin>culmination:Stair:4|" +
                "branch-2-9:choice>vista-source:Bridge:8|" +
                "branch-9-10:vista-source>branch-passage:LevelCorridor:0|" +
                "branch-10-11:branch-passage>branch-reward:Stair:4|" +
                "branch-11-12:branch-reward>branch-return:Stair:4|" +
                "rejoin-12-7:branch-return>rejoin:LevelCorridor:0"));
        }

        [Test]
        public void LoaderDerivesTheGraphMetricsRatherThanReadingThem()
        {
            Dictionary<string, string> graph = TopologySnapshot();

            Assert.That(graph["derived.riseFromLevels"], Is.EqualTo("True"));
            Assert.That(graph["derived.cycleRank"], Is.EqualTo("1"));
            Assert.That(graph["graph.loopEdges"], Is.EqualTo("1"));
            Assert.That(graph["derived.cycleCoreNodeCount"], Is.EqualTo("10"));
            Assert.That(graph["derived.junctions"], Is.EqualTo("choice:3|rejoin:3"));
            Assert.That(graph["derived.branchAttach"], Is.EqualTo("choice"));
            Assert.That(graph["derived.branchRejoin"], Is.EqualTo("rejoin"));
        }

        [Test]
        public void LoaderReadsTheGraphFromItsTopologyFile()
        {
            Dictionary<string, string> graph = TopologySnapshot();

            Assert.That(
                graph["graph.source"],
                Does.EndWith("Topologies/processional-spine.json"));
            Assert.That(graph["contract.probeLoaded"], Is.EqualTo("True"));
            Assert.That(graph["contract.probeNodeIds"], Is.EqualTo("probe-a|probe-b|probe-c"));
            // An edge id is derived from its endpoints unless a legacy id is pinned.
            Assert.That(graph["contract.probeEdgeIds"], Is.EqualTo("A-B|B-C"));
            Assert.That(graph["contract.nodeOrderIsDerived"], Is.EqualTo("True"));
        }

        [Test]
        public void LoaderRejectsMalformedTopologies()
        {
            Dictionary<string, string> graph = TopologySnapshot();

            foreach (string contract in new[]
                     {
                         "contract.duplicateNodeIdRejected",
                         "contract.duplicateEdgeIdRejected",
                         "contract.unknownEndpointRejected",
                         "contract.selfEdgeRejected",
                         "contract.parallelEdgeRejected",
                         "contract.unknownTransitionKindRejected",
                         "contract.mapCellWithoutNodeRejected",
                         "contract.nodeWithoutMapCellRejected",
                         "contract.slotOnUnknownEdgeRejected",
                         "contract.repeatedMainOrderRejected",
                         "contract.unknownAnchorRejected",
                         "contract.laneGapCountRejected",
                         "contract.idMustMatchFileNameRejected"
                     })
            {
                Assert.That(graph[contract], Is.EqualTo("True"), contract);
            }
        }

        [Test]
        public void FixedSeed_StillProducesOneDeterministicCanonicalPlan()
        {
            string firstText = InvokeSnapshot("BuildRouteCharacterizationSnapshot", TopologySeed);
            string secondText = InvokeSnapshot("BuildRouteCharacterizationSnapshot", TopologySeed);
            Dictionary<string, string> first = ParseSnapshot(firstText);

            Assert.That(first["accepted"], Is.EqualTo("true"), firstText);
            Assert.That(first["route.pattern"], Is.EqualTo("processional-spine"));
            Assert.That(first["hash.routeIntent"], Is.Not.Empty);
            Assert.That(firstText, Is.EqualTo(secondText));
        }

        private static Dictionary<string, string> TopologySnapshot()
        {
            return ParseSnapshot(InvokeSnapshot("BuildRouteGraphCompositionSnapshot", TopologySeed));
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
                if (separator < 0)
                    continue;

                result[line.Substring(0, separator)] = line.Substring(separator + 1);
            }

            return result;
        }
    }
}
