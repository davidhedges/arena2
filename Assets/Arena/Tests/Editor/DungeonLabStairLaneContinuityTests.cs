#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Arena.Tests.Editor
{
    public sealed class DungeonLabStairLaneContinuityTests
    {
        private const int ScreenshotRegressionSeed = -2078245253;
        private const int West = 8;
        private static readonly Type GeneratorType = AppDomain.CurrentDomain
            .Load("Assembly-CSharp-Editor")
            .GetType("DungeonLab.Editor.DungeonLabGenerator", throwOnError: true)!;
        private static readonly MethodInfo LandingContinuityMethod = GeneratorType
            .GetMethod(
                "LandingSpanHasFullWidthContinuation",
                BindingFlags.Static | BindingFlags.NonPublic)!;

        [Test]
        public void ScreenshotSeed_AcceptsWithTheLaneContinuityGate()
        {
            MethodInfo method = GeneratorType.GetMethod(
                "BuildSeedReport",
                BindingFlags.Static | BindingFlags.NonPublic,
                binder: null,
                new[] { typeof(int), typeof(string) },
                modifiers: null)!;
            object report = method.Invoke(null, new object[] { ScreenshotRegressionSeed, "dense" })!;

            Assert.That(
                report.ToString(),
                Does.Contain("\"accepted\": true"),
                $"Screenshot seed {ScreenshotRegressionSeed} did not find a valid single- or two-lane stair plan.");
        }

        [Test]
        public void TwoLaneLanding_RejectsImmediateSingleLaneContinuation()
        {
            var floorCells = new HashSet<Vector2Int>
            {
                new Vector2Int(19, 4),
                new Vector2Int(19, 5),
                new Vector2Int(18, 5)
            };
            var levels = new Dictionary<Vector2Int, int>
            {
                [new Vector2Int(18, 5)] = 16
            };

            Assert.That(
                HasFullWidthContinuation(
                    floorCells,
                    levels,
                    new[] { new Vector2Int(19, 4), new Vector2Int(19, 5) },
                    West,
                    expectedLevel: 16,
                    laneCount: 2),
                Is.False);
        }

        [Test]
        public void TwoLaneLanding_AcceptsTwoSameLevelContinuationCells()
        {
            var floorCells = new HashSet<Vector2Int>
            {
                new Vector2Int(19, 4),
                new Vector2Int(19, 5),
                new Vector2Int(18, 4),
                new Vector2Int(18, 5)
            };
            var levels = new Dictionary<Vector2Int, int>
            {
                [new Vector2Int(18, 4)] = 16,
                [new Vector2Int(18, 5)] = 16
            };

            Assert.That(
                HasFullWidthContinuation(
                    floorCells,
                    levels,
                    new[] { new Vector2Int(19, 4), new Vector2Int(19, 5) },
                    West,
                    expectedLevel: 16,
                    laneCount: 2),
                Is.True);
        }

        [Test]
        public void TwoLaneLanding_RejectsContinuationAtTheWrongLevel()
        {
            var floorCells = new HashSet<Vector2Int>
            {
                new Vector2Int(19, 4),
                new Vector2Int(19, 5),
                new Vector2Int(18, 4),
                new Vector2Int(18, 5)
            };
            var levels = new Dictionary<Vector2Int, int>
            {
                [new Vector2Int(18, 4)] = 12,
                [new Vector2Int(18, 5)] = 16
            };

            Assert.That(
                HasFullWidthContinuation(
                    floorCells,
                    levels,
                    new[] { new Vector2Int(19, 4), new Vector2Int(19, 5) },
                    West,
                    expectedLevel: 16,
                    laneCount: 2),
                Is.False);
        }

        [Test]
        public void OneLaneLanding_DoesNotRequireASecondLane()
        {
            Assert.That(
                HasFullWidthContinuation(
                    new HashSet<Vector2Int> { new Vector2Int(19, 5) },
                    new Dictionary<Vector2Int, int>(),
                    new[] { new Vector2Int(19, 5) },
                    West,
                    expectedLevel: 16,
                    laneCount: 1),
                Is.True);
        }

        private static bool HasFullWidthContinuation(
            HashSet<Vector2Int> floorCells,
            IReadOnlyDictionary<Vector2Int, int> levels,
            IReadOnlyList<Vector2Int> landingCells,
            int direction,
            int expectedLevel,
            int laneCount)
        {
            return (bool)LandingContinuityMethod.Invoke(
                null,
                new object[]
                {
                    floorCells,
                    levels,
                    landingCells,
                    direction,
                    expectedLevel,
                    laneCount
                })!;
        }
    }
}
