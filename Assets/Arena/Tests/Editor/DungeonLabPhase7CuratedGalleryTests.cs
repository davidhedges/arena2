#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;

namespace Arena.Tests.Editor
{
    public sealed class DungeonLabPhase7CuratedGalleryTests
    {
        private static readonly Type GeneratorType = AppDomain.CurrentDomain
            .Load("Assembly-CSharp-Editor")
            .GetType("DungeonLab.Editor.DungeonLabGenerator", throwOnError: true)!;
        private static readonly Lazy<Dictionary<string, string>> Snapshot =
            new Lazy<Dictionary<string, string>>(BuildSnapshot);

        [Test]
        public void LockedCorpus_BecomesThirtyBlindedFloorsInFixedOrder()
        {
            Dictionary<string, string> values = Snapshot.Value;

            Assert.That(values["gallery.version"], Is.EqualTo("phase7-curated-gallery-v1"));
            Assert.That(values["gallery.floorCount"], Is.EqualTo("30"));
            Assert.That(values["gallery.uniqueIds"], Is.EqualTo("30"));
            Assert.That(values["gallery.historicalControls"], Is.EqualTo("6"));
            Assert.That(values["gallery.orderFixed"], Is.EqualTo("True"));
            Assert.That(values["gallery.reviewNamesBlinded"], Is.EqualTo("True"));
        }

        [Test]
        public void CaptureContract_HasThreeConsistentScoredViews()
        {
            Dictionary<string, string> values = Snapshot.Value;

            Assert.That(values["gallery.viewsPerFloor"], Is.EqualTo("3"));
            Assert.That(values["gallery.imageCount"], Is.EqualTo("90"));
            Assert.That(values["gallery.width"], Is.EqualTo("1600"));
            Assert.That(values["gallery.height"], Is.EqualTo("900"));
            Assert.That(values["gallery.playerEyeHeight"], Is.EqualTo("1.65"));
            Assert.That(values["gallery.playerFov"], Is.EqualTo("70"));
        }

        [Test]
        public void ScoreSheets_AreEmptyAndCoverLockedRubric()
        {
            Dictionary<string, string> values = Snapshot.Value;

            Assert.That(values["gallery.criteria"], Is.EqualTo("7"));
            Assert.That(values["gallery.emptyFloorSheet"], Is.EqualTo("True"));
            Assert.That(values["gallery.repetitionScopes"], Is.EqualTo("4"));
        }

        [Test]
        public void RepetitionGroups_AreAnonymousEightFloorTopologySets()
        {
            Dictionary<string, string> values = Snapshot.Value;

            Assert.That(values["gallery.topologyGroups"], Is.EqualTo("3"));
            Assert.That(values["gallery.groupSizes"], Is.EqualTo("8,8,8"));
        }

        [Test]
        public void GallerySupport_UsesCanonicalPlanGeometryAndDoesNotScore()
        {
            string source = File.ReadAllText(
                "Assets/Arena/Editor/Dungeons/RandomDungeon/DungeonLabGenerator.Phase7Gallery.cs");

            Assert.That(source, Does.Contain("RecipePortPlacement port"));
            Assert.That(source, Does.Contain("port.outwardDirection"));
            Assert.That(source, Does.Contain("vista.vistaSourceCell"));
            Assert.That(source, Does.Contain("vista.vistaTargetCell"));
            Assert.That(source, Does.Contain("plan.namedPromontories.Length > 0"));
            Assert.That(source, Does.Contain("BuildPhase0RenderedSeed("));
            Assert.That(source, Does.Not.Contain("acceptedScore"));
            Assert.That(source, Does.Not.Contain("reviewPassed"));
        }

        private static Dictionary<string, string> BuildSnapshot()
        {
            MethodInfo method = GeneratorType.GetMethod(
                "BuildPhase7CuratedGallerySupportSnapshot",
                BindingFlags.Static | BindingFlags.NonPublic)!;
            Assert.That(method, Is.Not.Null, "Missing Phase 7 curated-gallery support diagnostic.");
            return Parse((string)method.Invoke(null, Array.Empty<object>())!);
        }

        private static Dictionary<string, string> Parse(string snapshot)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string line in snapshot.Split('\n'))
            {
                int separator = line.IndexOf('=');
                if (separator >= 0)
                    result[line.Substring(0, separator)] = line.Substring(separator + 1);
            }
            return result;
        }
    }
}
