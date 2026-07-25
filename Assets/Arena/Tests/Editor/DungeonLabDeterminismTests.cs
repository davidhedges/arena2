#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;

namespace Arena.Tests.Editor
{
    public sealed class DungeonLabDeterminismTests
    {
        private const int CharacterizationSeed = 2026072100;
        private static readonly Type GeneratorType = AppDomain.CurrentDomain
            .Load("Assembly-CSharp-Editor")
            .GetType("DungeonLab.Editor.DungeonLabGenerator", throwOnError: true)!;

        [Test]
        public void FixedSeed_ProducesIdenticalCanonicalSummaries()
        {
            string firstText = InvokeSnapshot("BuildCharacterizationSnapshot", CharacterizationSeed);
            string secondText = InvokeSnapshot("BuildCharacterizationSnapshot", CharacterizationSeed);
            Dictionary<string, string> first = ParseSnapshot(firstText);
            Dictionary<string, string> second = ParseSnapshot(secondText);

            Assert.That(first["accepted"], Is.EqualTo("true"), firstText);
            Assert.That(second["accepted"], Is.EqualTo("true"), secondText);
            Assert.That(first["hash.layout"], Is.EqualTo(second["hash.layout"]));
            Assert.That(first["hash.tieredLevelPlan"], Is.EqualTo(second["hash.tieredLevelPlan"]));
            Assert.That(first["hash.canonical"], Is.EqualTo(second["hash.canonical"]));
            Assert.That(firstText, Is.EqualTo(secondText));
        }

        [Test]
        public void AcceptedPlan_PreservesFloorAndRoomGraphConnectivity()
        {
            Dictionary<string, string> report = SeedSnapshot(CharacterizationSeed);

            AssertCheckPassed(report, "layoutConnectivity");
            AssertCheckPassed(report, "roomGraphConnectivity");
            Assert.That(int.Parse(report["metric.rootedRouteCount"]), Is.EqualTo(1));
            Assert.That(int.Parse(report["metric.longestRootRouteRooms"]), Is.GreaterThan(1));
        }

        [Test]
        public void AcceptedPlan_PreservesVerticalTransitionAndBottomToTopTraversal()
        {
            Dictionary<string, string> report = SeedSnapshot(CharacterizationSeed);

            AssertCheckPassed(report, "transitionContracts");
            AssertCheckPassed(report, "verticalTraversal");
            AssertCheckPassed(report, "bottomToTopTraversal");
            Assert.That(int.Parse(report["metric.transitionCount"]), Is.GreaterThan(0));
            Assert.That(int.Parse(report["metric.elevationSpan"]), Is.GreaterThan(0));
        }

        [Test]
        public void AcceptedPlan_PreservesHeadroomGate()
        {
            Dictionary<string, string> report = SeedSnapshot(CharacterizationSeed);

            AssertCheckPassed(report, "headroom");
        }

        [Test]
        public void AcceptedPlan_PreservesBoundaryAndRendererInputPreconditions()
        {
            Dictionary<string, string> report = SeedSnapshot(CharacterizationSeed);

            AssertCheckPassed(report, "boundary");
            AssertCheckPassed(report, "rendererInputs");
            Assert.That(report["validation.passed"], Is.EqualTo("true"));
        }

        [Test]
        public void ExistingRenderer_ProducesCollisionExportInputsWithoutRepair()
        {
            string snapshot = InvokeSnapshot("BuildRendererProbeSnapshot", CharacterizationSeed);
            Dictionary<string, string> report = ParseSnapshot(snapshot);

            Assert.That(report["accepted"], Is.EqualTo("true"), snapshot);
            Assert.That(report["boundary"], Is.EqualTo("true"), snapshot);
            Assert.That(report["renderer.passed"], Is.EqualTo("true"), snapshot);
            Assert.That(int.Parse(report["renderer.rejectedPlacements"]), Is.Zero);
            Assert.That(int.Parse(report["renderer.stairFootprintChecks"]), Is.GreaterThan(0));
            Assert.That(report["collision.passed"], Is.EqualTo("true"), snapshot);
            Assert.That(int.Parse(report["collision.enabledNonTriggerColliders"]), Is.GreaterThan(0));
            Assert.That(int.Parse(report["collision.missingMeshes"]), Is.Zero);
        }

        private static void AssertCheckPassed(Dictionary<string, string> report, string check)
        {
            Assert.That(report["accepted"], Is.EqualTo("true"));
            Assert.That(report[$"validation.{check}"], Is.EqualTo("true"));
        }

        private static Dictionary<string, string> SeedSnapshot(int seed)
        {
            return ParseSnapshot(InvokeSnapshot("BuildCharacterizationSnapshot", seed));
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
