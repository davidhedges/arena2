#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;

namespace Arena.Tests.Editor
{
    public sealed class DungeonLabNamedPromontoryTests
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
            // Cell counts are the vista's SURPLUS over its required void, and
            // the rubber sheet moves that surplus per seed. The contract is
            // that surplus becomes a promontory, capped at four cells - not any
            // particular number.
            foreach ((string prefix, string target) sample in new[]
                     {
                         ("processional", "vista-target"),
                         ("atrium", "atrium-landmark"),
                         ("twinWing", "keep-landmark")
                     })
            {
                Assert.That(snapshot[$"{sample.prefix}.resolutionCount"], Is.EqualTo("1"));
                Assert.That(snapshot[$"{sample.prefix}.targetNodeId"], Is.EqualTo(sample.target));
                int cells = int.Parse(snapshot[$"{sample.prefix}.cellCount"]);
                Assert.That(cells, Is.InRange(1, 4), sample.prefix);
            }
        }

        // Was MinimumVistaGap_RemainsVoidAndProducesNoPromontory, which asserted
        // that seed 2026072100's vista had exactly zero surplus. The rubber
        // sheet varies the lane gap per seed, so no production seed is a fixed
        // "no surplus" fixture any more. What survives is the invariant.
        [Test]
        public void Promontory_NeverConsumesTheRequiredVoid()
        {
            Dictionary<string, string> snapshot = PromontorySnapshot();

            Assert.That(snapshot["noSurplus.accepted"], Is.EqualTo("True"));
            Assert.That(int.Parse(snapshot["noSurplus.resolutionCount"]), Is.LessThanOrEqualTo(1));
            Assert.That(int.Parse(snapshot["noSurplus.remainingVoid"]), Is.GreaterThanOrEqualTo(3));
            Assert.That(snapshot["noSurplus.validation"], Is.EqualTo("True"));
            // A promontory may never eat into the required void, whatever the
            // lattice made available.
            Assert.That(int.Parse(snapshot["processional.remainingVoid"]), Is.GreaterThanOrEqualTo(3));
            Assert.That(int.Parse(snapshot["atrium.remainingVoid"]), Is.GreaterThanOrEqualTo(3));
            Assert.That(int.Parse(snapshot["twinWing.remainingVoid"]), Is.GreaterThanOrEqualTo(3));
        }

        [Test]
        public void CanonicalPlan_AdvancesIdentityWhileRendererUsesTheSharedPath()
        {
            Dictionary<string, string> snapshot = PromontorySnapshot();

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
                "BuildNamedPromontorySnapshot",
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
