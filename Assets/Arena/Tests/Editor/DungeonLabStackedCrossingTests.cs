#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;

namespace Arena.Tests.Editor
{
    public sealed class DungeonLabStackedCrossingTests
    {
        private static readonly Type GeneratorType = AppDomain.CurrentDomain
            .Load("Assembly-CSharp-Editor")
            .GetType("DungeonLab.Editor.DungeonLabGenerator", throwOnError: true)!;
        private static readonly Lazy<Dictionary<string, string>> Snapshot =
            new Lazy<Dictionary<string, string>>(BuildSnapshot);

        [Test]
        public void ExistingAerialBridgePath_CreatesOneExactStackedCoordinate()
        {
            Dictionary<string, string> values = Snapshot.Value;

            Assert.That(values["fixture.transitionCount"], Is.EqualTo("1"));
            Assert.That(values["fixture.placementClass"], Is.EqualTo("externalSpan"));
            Assert.That(values["fixture.stackedCoordinateCount"], Is.EqualTo("1"));
            Assert.That(values["fixture.lowerTraversable"], Is.EqualTo("True"));
            Assert.That(values["fixture.upperTraversable"], Is.EqualTo("True"));
        }

        [Test]
        public void HeadroomGate_PassesAtFourAndRejectsTwoLevels()
        {
            Dictionary<string, string> values = Snapshot.Value;

            Assert.That(values["fixture.positiveHeadroom"], Is.EqualTo("True"));
            Assert.That(values["fixture.negativeHeadroomRejected"], Is.EqualTo("True"));
        }

        [Test]
        public void SharedRenderer_PreservesBothSurfacesAndLowerClearance()
        {
            Dictionary<string, string> values = Snapshot.Value;

            Assert.That(values["fixture.rendererRejected"], Is.EqualTo("0"));
            Assert.That(values["fixture.lowerClearanceOpen"], Is.EqualTo("True"));
            Assert.That(int.Parse(values["fixture.lowerSurfaceColliders"]), Is.GreaterThan(0));
            Assert.That(int.Parse(values["fixture.upperSurfaceColliders"]), Is.GreaterThan(0));
        }

        private static Dictionary<string, string> BuildSnapshot()
        {
            MethodInfo method = GeneratorType.GetMethod(
                "BuildStackedCrossingSnapshot",
                BindingFlags.Static | BindingFlags.NonPublic)!;
            Assert.That(method, Is.Not.Null, "Missing stacked-crossing diagnostic.");
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
