#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;

namespace Arena.Tests.Editor
{
    public sealed class DungeonLabPhase6GraphCompositionTests
    {
        private const int CompositionSeed = 2026072100;
        private static readonly Type GeneratorType = AppDomain.CurrentDomain
            .Load("Assembly-CSharp-Editor")
            .GetType("DungeonLab.Editor.DungeonLabGenerator", throwOnError: true)!;

        [Test]
        public void ProductionGraph_ComposesTheExistingNodeOrderExactly()
        {
            Dictionary<string, string> graph = CompositionSnapshot();

            Assert.That(graph["graph.pattern"], Is.EqualTo("processional-spine"));
            Assert.That(graph["graph.nodeIds"], Is.EqualTo(
                "arrival|threshold|choice|reveal|vista-target|ascent|approach|rejoin|culmination|" +
                "vista-source|branch-passage|branch-reward|branch-return"));
        }

        [Test]
        public void ProductionGraph_ComposesTheExistingEdgeOrderExactly()
        {
            Dictionary<string, string> graph = CompositionSnapshot();

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
        public void Composer_ExposesOnlyTheEarnedOperationsAndOneResultingCycle()
        {
            Dictionary<string, string> graph = CompositionSnapshot();

            Assert.That(graph["graph.operations"], Is.EqualTo("spine,branch,rejoin"));
            Assert.That(graph["graph.loopEdges"], Is.EqualTo("1"));
            Assert.That(graph["contract.spineAdded"], Is.EqualTo("True"));
            Assert.That(graph["contract.branchAdded"], Is.EqualTo("True"));
            Assert.That(graph["contract.rejoinAdded"], Is.EqualTo("True"));
            Assert.That(graph["contract.publishedGraphHasOneCycle"], Is.EqualTo("True"));
        }

        [Test]
        public void Composer_RejectsInvalidIdsEndpointsSelfEdgesAndSecondRejoin()
        {
            Dictionary<string, string> graph = CompositionSnapshot();

            Assert.That(graph["contract.duplicateNodeRejected"], Is.EqualTo("True"));
            Assert.That(graph["contract.duplicateEdgeRejected"], Is.EqualTo("True"));
            Assert.That(graph["contract.missingEndpointRejected"], Is.EqualTo("True"));
            Assert.That(graph["contract.selfEdgeRejected"], Is.EqualTo("True"));
            Assert.That(graph["contract.missingRejoinTargetRejected"], Is.EqualTo("True"));
            Assert.That(graph["contract.secondRejoinRejected"], Is.EqualTo("True"));
        }

        [Test]
        public void Composer_FailedOperationsAreAtomicAndPublishedGraphIsImmutable()
        {
            Dictionary<string, string> graph = CompositionSnapshot();

            Assert.That(graph["contract.failedBranchesWereAtomic"], Is.EqualTo("True"));
            Assert.That(graph["contract.failedRejoinsWereAtomic"], Is.EqualTo("True"));
            Assert.That(graph["contract.publishedGraphIsImmutable"], Is.EqualTo("True"));
        }

        [Test]
        public void FixedSeed_StillProducesOneDeterministicCanonicalPlan()
        {
            string firstText = InvokeSnapshot("BuildRouteCharacterizationSnapshot", CompositionSeed);
            string secondText = InvokeSnapshot("BuildRouteCharacterizationSnapshot", CompositionSeed);
            Dictionary<string, string> first = ParseSnapshot(firstText);

            Assert.That(first["accepted"], Is.EqualTo("true"), firstText);
            Assert.That(first["route.pattern"], Is.EqualTo("processional-spine"));
            Assert.That(first["hash.routeIntent"], Is.Not.Empty);
            Assert.That(firstText, Is.EqualTo(secondText));
        }

        private static Dictionary<string, string> CompositionSnapshot()
        {
            return ParseSnapshot(InvokeSnapshot("BuildRouteGraphCompositionSnapshot", CompositionSeed));
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
