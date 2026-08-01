#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;

namespace Arena.Tests.Editor
{
    public sealed class DungeonLabPhaseEContractsTests
    {
        private static readonly Type GeneratorType = AppDomain.CurrentDomain
            .Load("Assembly-CSharp-Editor")
            .GetType("DungeonLab.Editor.DungeonLabGenerator", throwOnError: true)!;

        [Test]
        public void PhaseE_CeilingAbyssAndVoidContractsStayExplicit()
        {
            Dictionary<string, string> snapshot = InvokeSnapshot("BuildPhaseEContractSnapshot");

            Assert.That(snapshot["ceiling.default"], Is.EqualTo("24"));
            Assert.That(snapshot["ceiling.globalCap"], Is.EqualTo("40"));
            Assert.That(snapshot["abyss.depth"], Is.EqualTo("20"));
            Assert.That(snapshot["abyss.baseAtMin0"], Is.EqualTo("-20"));
            Assert.That(snapshot["abyss.baseAtMin12"], Is.EqualTo("-8"));
            Assert.That(snapshot["opening.apertureIsZero"], Is.EqualTo("True"));
            Assert.That(snapshot["opening.voidProducerCarriesKind"], Is.EqualTo("True"));
            Assert.That(snapshot["opening.voidSchemaAccepted"], Is.EqualTo("True"));
            Assert.That(snapshot["opening.clearFallColumnAccepted"], Is.EqualTo("True"));
            Assert.That(snapshot["opening.obstructedFallColumnRejected"], Is.EqualTo("True"));
            Assert.That(snapshot["opening.obstructedFailureCode"], Is.EqualTo("True"));
            Assert.That(snapshot["opening.maxSurvivableFall"], Is.EqualTo("8"));
            Assert.That(snapshot["opening.apertureNoCatchRejected"], Is.EqualTo("True"));
            Assert.That(snapshot["opening.apertureNoCatchFailureCode"], Is.EqualTo("True"));
            Assert.That(snapshot["opening.apertureLegalAccepted"], Is.EqualTo("True"));
            Assert.That(snapshot["opening.apertureShallowRejected"], Is.EqualTo("True"));
            Assert.That(snapshot["opening.apertureShallowFailureCode"], Is.EqualTo("True"));
            Assert.That(snapshot["opening.apertureDeepRejected"], Is.EqualTo("True"));
            Assert.That(snapshot["opening.apertureDeepFailureCode"], Is.EqualTo("True"));
        }

        [Test]
        public void PhaseE_NavigationEdgesCarryPhysicalWitnesses()
        {
            Dictionary<string, string> snapshot =
                InvokeSnapshot("BuildNavigationSurfaceContractSnapshot");

            Assert.That(snapshot["graph.valid"], Is.EqualTo("True"), snapshot["graph.failure"]);
            Assert.That(snapshot["graph.nodeCount"], Is.EqualTo("3"));
            Assert.That(snapshot["graph.edgeCount"], Is.EqualTo("2"));
            Assert.That(snapshot["graph.unwitnessedTransitionRejected"], Is.EqualTo("True"));
            Assert.That(snapshot["graph.wrongTransitionWitnessRejected"], Is.EqualTo("True"));
            Assert.That(snapshot["graph.witnessedTransitionEdges"], Is.EqualTo("1"));
            Assert.That(snapshot["graph.sealedPartitionRejected"], Is.EqualTo("True"));
            Assert.That(snapshot["graph.doorwayAccepted"], Is.EqualTo("True"));
            Assert.That(snapshot["collision.triangleEdgeAccepted"], Is.EqualTo("True"));
            Assert.That(snapshot["collision.outsideTriangleRejected"], Is.EqualTo("True"));
            Assert.That(snapshot["collision.exactHeightAccepted"], Is.EqualTo("True"));
            Assert.That(snapshot["collision.captureWindowDriftRejected"], Is.EqualTo("True"));
        }

        private static Dictionary<string, string> InvokeSnapshot(string methodName)
        {
            MethodInfo method = GeneratorType.GetMethod(
                methodName,
                BindingFlags.Static | BindingFlags.NonPublic)!;
            Assert.That(method, Is.Not.Null, $"Missing diagnostic method {methodName}.");
            string text = (string)method.Invoke(null, Array.Empty<object>())!;
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string line in text.Split('\n'))
            {
                int separator = line.IndexOf('=');
                if (separator >= 0)
                    result[line.Substring(0, separator)] = line.Substring(separator + 1);
            }

            return result;
        }
    }
}
