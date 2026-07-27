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

            // Edge ids are derived as "{from}-{to}" from the map keys. The
            // step 1 files pinned legacy ids to hold the batch hash; step 2
            // deleted them, so this is now the only spelling there is.
            Assert.That(graph["graph.edgeIds"], Is.EqualTo(
                "A-B|B-C|C-D|D-E|E-F|F-G|G-H|H-I|C-J|J-K|K-L|L-M|M-H"));
            Assert.That(graph["graph.edgeDetails"], Is.EqualTo(
                "A-B:arrival>threshold:LevelCorridor:0|" +
                "B-C:threshold>choice:Stair:4|" +
                "C-D:choice>reveal:LevelCorridor:0|" +
                "D-E:reveal>vista-target:Stair:4|" +
                "E-F:vista-target>ascent:Stair:4|" +
                "F-G:ascent>approach:Stairwell:4|" +
                "G-H:approach>rejoin:Stair:4|" +
                "H-I:rejoin>culmination:Stair:4|" +
                "C-J:choice>vista-source:Bridge:8|" +
                "J-K:vista-source>branch-passage:LevelCorridor:0|" +
                "K-L:branch-passage>branch-reward:Stair:4|" +
                "L-M:branch-reward>branch-return:Stair:4|" +
                "M-H:branch-return>rejoin:LevelCorridor:0"));
        }

        [Test]
        public void LoaderDerivesTheGraphMetricsRatherThanReadingThem()
        {
            Dictionary<string, string> graph = TopologySnapshot();

            Assert.That(graph["derived.riseFromLevels"], Is.EqualTo("True"));
            Assert.That(graph["derived.cycleRank"], Is.EqualTo("1"));
            Assert.That(graph["graph.loopEdges"], Is.EqualTo("1"));
            Assert.That(graph["derived.cycleCoreNodeCount"], Is.EqualTo("10"));
            // Every node of degree >= 3, not a first/last "attach/rejoin" pair
            // that a general graph does not have.
            Assert.That(graph["derived.junctions"], Is.EqualTo("choice:3|rejoin:3"));
            Assert.That(graph["derived.weight"], Is.EqualTo("1"));
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
                         "contract.pinnedEdgeIdRejected",
                         "contract.legacyBlockRejected",
                         "contract.spatialSettingsTokenRejected",
                         "contract.invertedLaneGapRejected",
                         // The absolute-cell spatial vocabulary retired when
                         // density became a dial. A bare number is legal in both
                         // vocabularies and means something different in each,
                         // so the loader has to refuse the old names by name
                         // rather than reinterpret them.
                         "contract.absoluteLaneGapRejected",
                         "contract.absoluteRoomSizesRejected",
                         "contract.unknownRoomSizeClassRejected",
                         "contract.negativeWeightRejected",
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
            // Selection is a weighted draw now, so the snapshot reports which
            // seed actually lands on this topology instead of assuming one.
            int seed = int.Parse(TopologySnapshot()["selector.firstSeed"]);
            string firstText = InvokeSnapshot("BuildRouteCharacterizationSnapshot", seed);
            string secondText = InvokeSnapshot("BuildRouteCharacterizationSnapshot", seed);
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
