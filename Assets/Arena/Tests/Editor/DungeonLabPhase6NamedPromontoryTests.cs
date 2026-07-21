#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;

namespace Arena.Tests.Editor
{
    public sealed class DungeonLabPhase6NamedPromontoryTests
    {
        private const int Seed = 2026072100;
        private static readonly Type GeneratorType = AppDomain.CurrentDomain
            .Load("Assembly-CSharp-Editor")
            .GetType("DungeonLab.Editor.DungeonLabGenerator", throwOnError: true)!;

        [Test]
        public void ProductionPatterns_ResolveNamedPromontoriesOnlyFromVistaSurplus()
        {
            Dictionary<string, string> snapshot = PromontorySnapshot();

            Assert.That(snapshot["policy.version"], Is.EqualTo("named-vista-promontory-v1"));
            Assert.That(snapshot["policy.maximumCells"], Is.EqualTo("4"));
            Assert.That(snapshot["processional.resolutionCount"], Is.EqualTo("1"));
            Assert.That(snapshot["processional.cellCount"], Is.EqualTo("1"));
            Assert.That(snapshot["processional.targetNodeId"], Is.EqualTo("vista-target"));
            Assert.That(snapshot["atrium.resolutionCount"], Is.EqualTo("1"));
            Assert.That(snapshot["atrium.cellCount"], Is.EqualTo("4"));
            Assert.That(snapshot["atrium.targetNodeId"], Is.EqualTo("atrium-landmark"));
            Assert.That(snapshot["twinWing.resolutionCount"], Is.EqualTo("1"));
            Assert.That(snapshot["twinWing.cellCount"], Is.EqualTo("1"));
            Assert.That(snapshot["twinWing.targetNodeId"], Is.EqualTo("keep-landmark"));
        }

        [Test]
        public void MinimumVistaGap_RemainsVoidAndProducesNoPromontory()
        {
            Dictionary<string, string> snapshot = PromontorySnapshot();

            Assert.That(snapshot["noSurplus.accepted"], Is.EqualTo("True"));
            Assert.That(snapshot["noSurplus.resolutionCount"], Is.EqualTo("0"));
            Assert.That(snapshot["noSurplus.remainingVoid"], Is.EqualTo("3"));
            Assert.That(snapshot["noSurplus.validation"], Is.EqualTo("True"));
            Assert.That(snapshot["processional.remainingVoid"], Is.EqualTo("3"));
            Assert.That(snapshot["atrium.remainingVoid"], Is.EqualTo("3"));
            Assert.That(snapshot["twinWing.remainingVoid"], Is.EqualTo("3"));
        }

        [Test]
        public void CanonicalPlan_AdvancesIdentityWhileRendererUsesTheSharedPath()
        {
            Dictionary<string, string> snapshot = PromontorySnapshot();

            Assert.That(snapshot["versions.summary"], Is.EqualTo("dungeon-plan-v9"));
            Assert.That(snapshot["versions.generator"], Is.EqualTo("route-topologies-v9"));
            Assert.That(snapshot["processional.validation"], Is.EqualTo("True"));
            Assert.That(snapshot["atrium.validation"], Is.EqualTo("True"));
            Assert.That(snapshot["twinWing.validation"], Is.EqualTo("True"));
            Assert.That(snapshot["renderer.accepted"], Is.EqualTo("True"));
            Assert.That(snapshot["renderer.passed"], Is.EqualTo("True"));
            Assert.That(snapshot["renderer.rejected"], Is.EqualTo("0"));
        }

        [Test]
        public void NamedPromontory_RejectsMissingIdentityOrInvalidFacing()
        {
            Dictionary<string, string> snapshot = PromontorySnapshot();

            Assert.That(snapshot["probe.missingIdentityRejected"], Is.EqualTo("True"));
            Assert.That(snapshot["probe.missingIdentityError"], Does.Contain("no named target identity"));
            Assert.That(snapshot["probe.facingRejected"], Is.EqualTo("True"));
            Assert.That(snapshot["probe.facingError"], Does.Contain("cardinal opposed facing"));
        }

        [Test]
        public void NamedPromontory_RejectsOffAxisOrOccupiedCellsAtomically()
        {
            Dictionary<string, string> snapshot = PromontorySnapshot();

            Assert.That(snapshot["probe.validResolved"], Is.EqualTo("True"), snapshot["probe.validError"]);
            Assert.That(snapshot["probe.validResolutionCount"], Is.EqualTo("1"));
            Assert.That(snapshot["probe.offAxisRejected"], Is.EqualTo("True"));
            Assert.That(snapshot["probe.offAxisError"], Does.Contain("off-axis"));
            Assert.That(snapshot["probe.occupiedRejected"], Is.EqualTo("True"));
            Assert.That(snapshot["probe.occupiedError"], Does.Contain("occupied"));
        }

        [Test]
        public void NamedPromontory_RejectsLostVoidBudgetOrNonLowerTarget()
        {
            Dictionary<string, string> snapshot = PromontorySnapshot();

            Assert.That(snapshot["probe.voidBudgetRejected"], Is.EqualTo("True"));
            Assert.That(snapshot["probe.voidBudgetError"], Does.Contain("fewer than 3 void cells"));
            Assert.That(snapshot["probe.lowerTargetRejected"], Is.EqualTo("True"));
            Assert.That(snapshot["probe.lowerTargetError"], Does.Contain("not at least 4u below"));
        }

        private static Dictionary<string, string> PromontorySnapshot()
        {
            MethodInfo method = GeneratorType.GetMethod(
                "BuildPhase6eNamedPromontorySnapshot",
                BindingFlags.Static | BindingFlags.NonPublic)!;
            Assert.That(method, Is.Not.Null, "Missing Phase 6e named-promontory diagnostic.");
            return ParseSnapshot((string)method.Invoke(null, new object[] { Seed })!);
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
