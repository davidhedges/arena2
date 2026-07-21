#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;

namespace Arena.Tests.Editor
{
    public sealed class DungeonLabPhase6RouteRhythmTests
    {
        private const int Seed = 2026072100;
        private static readonly Type GeneratorType = AppDomain.CurrentDomain
            .Load("Assembly-CSharp-Editor")
            .GetType("DungeonLab.Editor.DungeonLabGenerator", throwOnError: true)!;

        [Test]
        public void ProductionPatterns_ConsumeOneSharedVersionedRhythmPolicy()
        {
            Dictionary<string, string> snapshot = RhythmSnapshot();

            Assert.That(snapshot["policy.version"], Is.EqualTo("route-rhythm-v1"));
            Assert.That(snapshot["policy.maxMainRouteRoleOccurrences"], Is.EqualTo("2"));
            Assert.That(snapshot["policy.maxConsecutiveSameRole"], Is.EqualTo("1"));
            Assert.That(snapshot["policy.maxConsecutiveSameBeat"], Is.EqualTo("1"));
            Assert.That(snapshot["policy.minimumMainRouteNodesBetweenRecipeSlots"], Is.EqualTo("2"));
            Assert.That(snapshot["production.processionalValid"], Is.EqualTo("True"), snapshot["production.processionalError"]);
            Assert.That(snapshot["production.atriumValid"], Is.EqualTo("True"), snapshot["production.atriumError"]);
            Assert.That(snapshot["production.twinWingValid"], Is.EqualTo("True"), snapshot["production.twinWingError"]);
        }

        [Test]
        public void RhythmPolicy_RejectsNonContiguousOrDuplicateMainRouteOrder()
        {
            Dictionary<string, string> snapshot = RhythmSnapshot();

            Assert.That(snapshot["probe.orderRejected"], Is.EqualTo("True"));
            Assert.That(snapshot["probe.orderError"], Does.Contain("contiguous unique main-route orders"));
        }

        [Test]
        public void RhythmPolicy_RejectsAdjacentRepeatedRole()
        {
            Dictionary<string, string> snapshot = RhythmSnapshot();

            Assert.That(snapshot["probe.adjacentRoleRejected"], Is.EqualTo("True"));
            Assert.That(snapshot["probe.adjacentRoleError"], Does.Contain("adjacent main-route role 'hall'"));
        }

        [Test]
        public void RhythmPolicy_RejectsAdjacentRepeatedBeat()
        {
            Dictionary<string, string> snapshot = RhythmSnapshot();

            Assert.That(snapshot["probe.adjacentBeatRejected"], Is.EqualTo("True"));
            Assert.That(snapshot["probe.adjacentBeatError"], Does.Contain("adjacent main-route beat 'reveal'"));
        }

        [Test]
        public void RhythmPolicy_RejectsThirdSeparatedOccurrenceOfOneRole()
        {
            Dictionary<string, string> snapshot = RhythmSnapshot();

            Assert.That(snapshot["probe.roleLimitRejected"], Is.EqualTo("True"));
            Assert.That(snapshot["probe.roleLimitError"], Does.Contain("at most 2 main-route nodes with role 'hall'"));
        }

        [Test]
        public void RhythmPolicy_RejectsRecipeCrowdingThroughTheProductionFailureBoundary()
        {
            Dictionary<string, string> snapshot = RhythmSnapshot();

            Assert.That(snapshot["probe.recipeSpacingRejected"], Is.EqualTo("True"));
            Assert.That(snapshot["probe.recipeSpacingError"], Does.Contain(
                "at least 2 intervening main-route nodes between recipe-bearing nodes"));
            Assert.That(snapshot["probe.fullValidatorRejected"], Is.EqualTo("True"));
            Assert.That(snapshot["probe.fullValidationError"], Does.StartWith("route-rhythm-v1"));
            Assert.That(snapshot["probe.productionFailureCode"], Is.EqualTo("ROUTE_INTENT_INVALID"));
        }

        private static Dictionary<string, string> RhythmSnapshot()
        {
            MethodInfo method = GeneratorType.GetMethod(
                "BuildPhase6dRouteRhythmSnapshot",
                BindingFlags.Static | BindingFlags.NonPublic)!;
            Assert.That(method, Is.Not.Null, "Missing Phase 6d route-rhythm diagnostic.");
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
