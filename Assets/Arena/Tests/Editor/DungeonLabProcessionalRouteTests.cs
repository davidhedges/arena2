#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;

namespace Arena.Tests.Editor
{
    public sealed class DungeonLabProcessionalRouteTests
    {
        // BuildRouteIntentOnlySnapshot forces this topology, so any seed builds
        // its graph; the production snapshots below need a seed the weighted
        // selector actually lands on, which that snapshot reports.
        private const int AnySeed = 2026072100;
        private static readonly Type GeneratorType = AppDomain.CurrentDomain
            .Load("Assembly-CSharp-Editor")
            .GetType("DungeonLab.Editor.DungeonLabGenerator", throwOnError: true)!;

        [Test]
        public void RouteIntent_ExistsBeforeSpatialCoordinates()
        {
            Dictionary<string, string> intent = ParseSnapshot(
                InvokeSnapshot("BuildRouteIntentOnlySnapshot", AnySeed));

            Assert.That(intent["route.pattern"], Is.EqualTo("processional-spine"));
            Assert.That(intent["route.nodeCount"], Is.EqualTo("13"));
            Assert.That(intent["route.mainRouteCount"], Is.EqualTo("9"));
            Assert.That(intent["route.branchNodeCount"], Is.EqualTo("4"));
            Assert.That(intent["route.loopEdges"], Is.EqualTo("1"));
            Assert.That(intent["route.bottomNode"], Is.EqualTo("arrival"));
            Assert.That(intent["route.topNode"], Is.EqualTo("culmination"));
            Assert.That(intent["containsSpatialCoordinates"], Is.EqualTo("False"));
        }

        [Test]
        public void FixedSeed_ProducesIdenticalIntentLayoutAndTierHashes()
        {
            int seed = PilotSeed();
            string firstText = InvokeSnapshot("BuildRouteCharacterizationSnapshot", seed);
            string secondText = InvokeSnapshot("BuildRouteCharacterizationSnapshot", seed);
            Dictionary<string, string> first = ParseSnapshot(firstText);
            Dictionary<string, string> second = ParseSnapshot(secondText);

            Assert.That(first["accepted"], Is.EqualTo("true"), firstText);
            Assert.That(first["hash.routeIntent"], Is.Not.Empty);
            Assert.That(first["hash.routeIntent"], Is.EqualTo(second["hash.routeIntent"]));
            Assert.That(first["hash.layout"], Is.EqualTo(second["hash.layout"]));
            Assert.That(first["hash.tieredLevelPlan"], Is.EqualTo(second["hash.tieredLevelPlan"]));
            Assert.That(first["hash.canonical"], Is.EqualTo(second["hash.canonical"]));
            Assert.That(firstText, Is.EqualTo(secondText));
        }

        [Test]
        public void Pilot_CompilesProcessionalGraphDirectlyIntoDungeonLayout()
        {
            Dictionary<string, string> report = PilotSnapshot();

            Assert.That(report["accepted"], Is.EqualTo("true"));
            Assert.That(report["route.pattern"], Is.EqualTo("processional-spine"));
            Assert.That(report["route.mainRouteCount"], Is.EqualTo("9"));
            Assert.That(report["route.branchNodeCount"], Is.EqualTo("4"));
            Assert.That(report["route.loopEdges"], Is.EqualTo("1"));
            Assert.That(int.Parse(report["metric.rooms"]), Is.EqualTo(13));
            Assert.That(int.Parse(report["metric.connections"]), Is.GreaterThanOrEqualTo(13));
            Assert.That(int.Parse(report["metric.loopEdges"]), Is.GreaterThanOrEqualTo(1));
        }

        [Test]
        public void Pilot_ReservesMutuallyFacingUnobstructedVistaVolume()
        {
            Dictionary<string, string> report = PilotSnapshot();

            Assert.That(report["vista.sourceFacing"], Is.Not.EqualTo("0,0"));
            Assert.That(report["vista.targetFacing"], Is.Not.EqualTo("0,0"));
            Assert.That(report["vista.facingOpposed"], Is.EqualTo("true"));
            Assert.That(int.Parse(report["vista.reservedVoidCells"]), Is.GreaterThanOrEqualTo(3));
            Assert.That(report["vista.unobstructed"], Is.EqualTo("true"));
        }

        [Test]
        public void Pilot_UsesExistingHardValidityPipeline()
        {
            Dictionary<string, string> report = PilotSnapshot();

            Assert.That(int.Parse(report["layoutAttempts"]), Is.InRange(1, 2));
            Assert.That(report["validation.layoutConnectivity"], Is.EqualTo("true"));
            Assert.That(report["validation.roomGraphConnectivity"], Is.EqualTo("true"));
            Assert.That(report["validation.verticalTraversal"], Is.EqualTo("true"));
            Assert.That(report["validation.headroom"], Is.EqualTo("true"));
            Assert.That(report["validation.passed"], Is.EqualTo("true"));
        }

        [Test]
        public void Pilot_ExistingRendererProducesCollisionInputsWithoutRepair()
        {
            string snapshot = InvokeSnapshot("BuildRendererProbeSnapshot", PilotSeed());
            Dictionary<string, string> report = ParseSnapshot(snapshot);

            Assert.That(report["accepted"], Is.EqualTo("true"), snapshot);
            Assert.That(report["boundary"], Is.EqualTo("true"), snapshot);
            Assert.That(report["renderer.passed"], Is.EqualTo("true"), snapshot);
            Assert.That(int.Parse(report["renderer.rejectedPlacements"]), Is.Zero);
            Assert.That(report["collision.passed"], Is.EqualTo("true"), snapshot);
            Assert.That(int.Parse(report["collision.enabledNonTriggerColliders"]), Is.GreaterThan(0));
        }

        private static int PilotSeed()
        {
            return int.Parse(ParseSnapshot(
                InvokeSnapshot("BuildRouteIntentOnlySnapshot", AnySeed))["selector.firstSeed"]);
        }

        private static Dictionary<string, string> PilotSnapshot()
        {
            return ParseSnapshot(InvokeSnapshot("BuildRouteCharacterizationSnapshot", PilotSeed()));
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
