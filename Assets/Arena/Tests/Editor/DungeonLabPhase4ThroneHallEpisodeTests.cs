#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;

namespace Arena.Tests.Editor
{
    public sealed class DungeonLabPhase4ThroneHallEpisodeTests
    {
        private const int EpisodeSeed = 2026072100;
        private static readonly Type GeneratorType = AppDomain.CurrentDomain
            .Load("Assembly-CSharp-Editor")
            .GetType("DungeonLab.Editor.DungeonLabGenerator", throwOnError: true)!;

        [Test]
        public void RouteIntent_DeclaresOneNarrowLandmarkEpisodeBeforeCoordinates()
        {
            Dictionary<string, string> intent = ParseSnapshot(
                InvokeSnapshot("BuildRouteIntentOnlySnapshot", EpisodeSeed));

            Assert.That(intent["episode.id"], Is.EqualTo("episode_throne_twin_stairs_01"));
            Assert.That(intent["episode.slotNode"], Is.EqualTo("4"));
            Assert.That(intent["episode.focalAxisBinding"], Is.EqualTo("vista-source-to-target"));
            Assert.That(intent["episode.coupledStairCount"], Is.EqualTo("2"));
            Assert.That(intent["episode.allowedFocalVariations"], Is.EqualTo("2"));
            Assert.That(intent["episode.thresholdCount"], Is.EqualTo("2"));
            Assert.That(intent["containsSpatialCoordinates"], Is.EqualTo("False"));
        }

        [Test]
        public void IsolatedProbe_CoversEveryAllowedOrientationAndFocalVariation()
        {
            Dictionary<string, string> isolated = ParseSnapshot(
                InvokeSnapshot("BuildThroneHallIsolatedSnapshot", EpisodeSeed));

            Assert.That(isolated["isolated.episodeId"], Is.EqualTo("episode_throne_twin_stairs_01"));
            Assert.That(isolated["isolated.orientationCount"], Is.EqualTo("4"));
            Assert.That(isolated["isolated.focalVariationCount"], Is.EqualTo("2"));
            Assert.That(isolated["isolated.combinationCount"], Is.EqualTo("8"));
            Assert.That(isolated["isolated.geometryValid"], Is.EqualTo("True"));
            Assert.That(isolated["isolated.focalAssetsValid"], Is.EqualTo("True"));
            Assert.That(isolated["schema.allFieldsConsumed"], Is.EqualTo("True"));
            Assert.That(int.Parse(isolated["schema.fieldCount"]), Is.GreaterThan(0));
        }

        [Test]
        public void FixedSeed_ResolvesTheWholeEpisodeDeterministically()
        {
            string firstText = InvokeSnapshot("BuildRouteCharacterizationSnapshot", EpisodeSeed);
            string secondText = InvokeSnapshot("BuildRouteCharacterizationSnapshot", EpisodeSeed);
            Dictionary<string, string> first = ParseSnapshot(firstText);
            Dictionary<string, string> second = ParseSnapshot(secondText);

            Assert.That(first["accepted"], Is.EqualTo("true"), firstText);
            Assert.That(first["episode.atomicAndValid"], Is.EqualTo("true"), firstText);
            Assert.That(first["hash.routeIntent"], Is.EqualTo(second["hash.routeIntent"]));
            Assert.That(first["hash.layout"], Is.EqualTo(second["hash.layout"]));
            Assert.That(first["hash.tieredLevelPlan"], Is.EqualTo(second["hash.tieredLevelPlan"]));
            Assert.That(first["hash.canonical"], Is.EqualTo(second["hash.canonical"]));
            Assert.That(firstText, Is.EqualTo(secondText));
        }

        [Test]
        public void GenericRouteConnections_EndAtTheTwoDeclaredTypedThresholds()
        {
            Dictionary<string, string> report = EpisodeSnapshot();

            Assert.That(report["episode.thresholdCount"], Is.EqualTo("2"));
            Assert.That(report["episode.genericConnectionsThroughDeclaredPorts"], Is.EqualTo("true"));
            Assert.That(report["validation.throneHallEpisode"], Is.EqualTo("true"));
        }

        [Test]
        public void TwinStairs_Landings_GalleriesAndFocalZoneRemainCoupledAndProtected()
        {
            Dictionary<string, string> report = EpisodeSnapshot();

            Assert.That(report["episode.focalZoneCells"], Is.EqualTo("15"));
            Assert.That(report["episode.sideGalleryCount"], Is.EqualTo("2"));
            Assert.That(report["episode.twinStairCount"], Is.EqualTo("2"));
            Assert.That(report["episode.baseLevel"], Is.EqualTo("8"));
            Assert.That(report["episode.galleryLevel"], Is.EqualTo("9"));
            Assert.That(report["episode.coupledReservationsComplete"], Is.EqualTo("true"));
            Assert.That(report["episode.sideGalleriesSymmetric"], Is.EqualTo("true"));
            Assert.That(report["episode.protectedZonesValid"], Is.EqualTo("true"));
            Assert.That(report["schema.allFieldsConsumed"], Is.EqualTo("true"));
        }

        [Test]
        public void FullDungeon_PreservesEveryPhase3HardGate()
        {
            Dictionary<string, string> report = EpisodeSnapshot();

            Assert.That(report["vertical.routeClimb"], Is.EqualTo("24"));
            Assert.That(report["vertical.requirementsSatisfied"], Is.EqualTo("true"));
            Assert.That(report["vista.finalValid"], Is.EqualTo("true"));
            Assert.That(report["validation.layoutConnectivity"], Is.EqualTo("true"));
            Assert.That(report["validation.roomGraphConnectivity"], Is.EqualTo("true"));
            Assert.That(report["validation.verticalTraversal"], Is.EqualTo("true"));
            Assert.That(report["validation.routeRequirements"], Is.EqualTo("true"));
            Assert.That(report["validation.headroom"], Is.EqualTo("true"));
            Assert.That(report["validation.passed"], Is.EqualTo("true"));
        }

        [Test]
        public void ExistingRendererAndCollision_ConsumeTheEpisodeWithoutRepair()
        {
            string snapshot = InvokeSnapshot("BuildRendererProbeSnapshot", EpisodeSeed);
            Dictionary<string, string> report = ParseSnapshot(snapshot);

            Assert.That(report["accepted"], Is.EqualTo("true"), snapshot);
            Assert.That(report["boundary"], Is.EqualTo("true"), snapshot);
            Assert.That(report["renderer.passed"], Is.EqualTo("true"), snapshot);
            Assert.That(report["renderer.episodeShowpieces"], Is.EqualTo("1"));
            Assert.That(int.Parse(report["renderer.rejectedPlacements"]), Is.Zero);
            Assert.That(report["collision.passed"], Is.EqualTo("true"), snapshot);
            Assert.That(int.Parse(report["collision.enabledNonTriggerColliders"]), Is.GreaterThan(0));
            Assert.That(int.Parse(report["collision.missingMeshes"]), Is.Zero);
        }

        private static Dictionary<string, string> EpisodeSnapshot()
        {
            return ParseSnapshot(InvokeSnapshot("BuildRouteCharacterizationSnapshot", EpisodeSeed));
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
