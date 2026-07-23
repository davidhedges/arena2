#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace Arena.Tests.Editor
{
    public sealed class DungeonLabPhase3VerticalIntentTests
    {
        private const int VerticalIntentSeed = 2026072100;
        private static readonly Type GeneratorType = AppDomain.CurrentDomain
            .Load("Assembly-CSharp-Editor")
            .GetType("DungeonLab.Editor.DungeonLabGenerator", throwOnError: true)!;

        [Test]
        public void Intent_DeclaresElevationStoryAndTypedTransitionsBeforeCoordinates()
        {
            Dictionary<string, string> intent = ParseSnapshot(
                InvokeSnapshot("BuildRouteIntentOnlySnapshot", VerticalIntentSeed));

            Assert.That(intent["vertical.elevationPolicy"], Is.EqualTo("AscendingSpine"));
            Assert.That(intent["vertical.bottomRelativeLevel"], Is.EqualTo("0"));
            Assert.That(intent["vertical.topRelativeLevel"], Is.EqualTo("24"));
            Assert.That(intent["vertical.requiredStairs"], Is.EqualTo("7"));
            Assert.That(intent["vertical.requiredBridges"], Is.EqualTo("1"));
            Assert.That(intent["vertical.requiredStairwells"], Is.EqualTo("1"));
            Assert.That(intent["containsSpatialCoordinates"], Is.EqualTo("False"));
        }

        [Test]
        public void FixedSeed_DeterministicallyResolvesVerticalIntent()
        {
            string firstText = InvokeSnapshot("BuildRouteCharacterizationSnapshot", VerticalIntentSeed);
            string secondText = InvokeSnapshot("BuildRouteCharacterizationSnapshot", VerticalIntentSeed);
            Dictionary<string, string> first = ParseSnapshot(firstText);
            Dictionary<string, string> second = ParseSnapshot(secondText);

            Assert.That(first["accepted"], Is.EqualTo("true"), firstText);
            Assert.That(first["vertical.requirementsSatisfied"], Is.EqualTo("true"), firstText);
            Assert.That(first["hash.routeIntent"], Is.EqualTo(second["hash.routeIntent"]));
            Assert.That(first["hash.layout"], Is.EqualTo(second["hash.layout"]));
            Assert.That(first["hash.tieredLevelPlan"], Is.EqualTo(second["hash.tieredLevelPlan"]));
            Assert.That(first["hash.canonical"], Is.EqualTo(second["hash.canonical"]));
            Assert.That(firstText, Is.EqualTo(secondText));
        }

        [Test]
        public void StructuralTransitions_ReserveLandingsAndFootprintsBeforeFill()
        {
            Dictionary<string, string> report = VerticalSnapshot();

            Assert.That(report["vertical.requiredTransitionCount"], Is.EqualTo("13"));
            Assert.That(report["vertical.stairCount"], Is.EqualTo("7"));
            Assert.That(report["vertical.bridgeCount"], Is.EqualTo("1"));
            Assert.That(report["vertical.stairwellCount"], Is.EqualTo("1"));
            Assert.That(report["vertical.allStructuralReservedBeforeFill"], Is.EqualTo("true"));
            Assert.That(report["validation.routeRequirements"], Is.EqualTo("true"));
        }

        [Test]
        public void DeclaredProcessionalStair_ReservesEmbeddedFootprintInNarrowCorridor()
        {
            string report = InvokeReportText("BuildPhase0SeedReport", VerticalIntentSeed);
            Match transition = Regex.Match(
                report,
                @"""edgeId"": ""main-1-2""(?:(?!""edgeId"").)*?""reservedBeforeFill"": true",
                RegexOptions.CultureInvariant | RegexOptions.Singleline);
            Match footprint = Regex.Match(
                transition.Value,
                @"""footprintCells"": \[(?<cells>.*?)\]\s*,\s*""reservedBeforeFill""",
                RegexOptions.CultureInvariant | RegexOptions.Singleline);

            Assert.That(report, Does.Contain("\"accepted\": true"), report);
            Assert.That(transition.Success, Is.True, report);
            Assert.That(transition.Value, Does.Contain("\"transitionKind\": \"Stair\""));
            Assert.That(transition.Value, Does.Contain("\"requiredRiseLevels\": 4"));
            Assert.That(transition.Value, Does.Contain("\"resolvedRiseLevels\": 4"));
            Assert.That(transition.Value, Does.Contain("\"placementClass\": \"embedded\""));
            Assert.That(transition.Value, Does.Contain("\"reservedBeforeFill\": true"));
            Assert.That(footprint.Success, Is.True, transition.Value);
            Assert.That(
                Regex.Matches(footprint.Groups["cells"].Value, "\"x\":").Count,
                Is.EqualTo(2),
                "The declared one-lane stair must remain inside its two-cell corridor footprint.");
        }

        [Test]
        public void NamedVista_RemainsValidThroughFinalTierPlanning()
        {
            Dictionary<string, string> report = VerticalSnapshot();

            Assert.That(report["vista.facingOpposed"], Is.EqualTo("true"));
            Assert.That(report["vista.finalValid"], Is.EqualTo("true"));
            Assert.That(int.Parse(report["vista.finalReservedVoidCells"]), Is.GreaterThanOrEqualTo(3));
            Assert.That(int.Parse(report["vista.finalSourceLevel"]),
                Is.GreaterThanOrEqualTo(int.Parse(report["vista.finalTargetLevel"]) + 4));
        }

        [Test]
        public void Route_ClimbsBottomToTopAndPassesExistingHardPipeline()
        {
            Dictionary<string, string> report = VerticalSnapshot();

            Assert.That(report["vertical.routeClimb"], Is.EqualTo("24"));
            Assert.That(report["validation.layoutConnectivity"], Is.EqualTo("true"));
            Assert.That(report["validation.roomGraphConnectivity"], Is.EqualTo("true"));
            Assert.That(report["validation.verticalTraversal"], Is.EqualTo("true"));
            Assert.That(report["validation.routeRequirements"], Is.EqualTo("true"));
            Assert.That(report["validation.headroom"], Is.EqualTo("true"));
            Assert.That(report["validation.passed"], Is.EqualTo("true"));
        }

        [Test]
        public void ExistingRendererAndCollisionConsumeResolvedPlanWithoutRepair()
        {
            string snapshot = InvokeSnapshot("BuildRendererProbeSnapshot", VerticalIntentSeed);
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

        private static Dictionary<string, string> VerticalSnapshot()
        {
            return ParseSnapshot(InvokeSnapshot("BuildRouteCharacterizationSnapshot", VerticalIntentSeed));
        }

        private static string InvokeSnapshot(string methodName, int seed)
        {
            MethodInfo method = GeneratorType.GetMethod(
                methodName,
                BindingFlags.Static | BindingFlags.NonPublic,
                binder: null,
                types: new[] { typeof(int) },
                modifiers: null)!;
            Assert.That(method, Is.Not.Null, $"Missing diagnostic method {methodName}.");
            return (string)method.Invoke(null, new object[] { seed })!;
        }

        private static string InvokeReportText(string methodName, int seed)
        {
            MethodInfo method = GeneratorType.GetMethod(
                methodName,
                BindingFlags.Static | BindingFlags.NonPublic,
                binder: null,
                types: new[] { typeof(int) },
                modifiers: null)!;
            Assert.That(method, Is.Not.Null, $"Missing diagnostic method {methodName}.");
            return method.Invoke(null, new object[] { seed })!.ToString();
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
