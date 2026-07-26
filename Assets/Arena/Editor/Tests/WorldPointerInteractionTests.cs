#nullable enable

using System.Collections.Generic;
using Arena.Interaction;
using NUnit.Framework;
using UnityEngine;

namespace Arena.EditModeTests
{
    public sealed class WorldPointerInteractionTests
    {
        [Test]
        public void Gesture_ShortStationaryRelease_IsClick()
        {
            var gesture = new WorldPointerGestureClassifier(0.28f, 8f);
            gesture.Begin(new Vector2(10f, 20f), 1f, consumed: false);

            WorldPointerGestureResult result = gesture.Release(
                new Vector2(14f, 23f),
                1.2f,
                pointerBlocked: false);

            Assert.That(result, Is.EqualTo(WorldPointerGestureResult.Click));
        }

        [TestCase(1.4f, 10f, 20f)]
        [TestCase(1.1f, 30f, 20f)]
        public void Gesture_LongOrMovedRelease_IsDrag(float releaseTime, float x, float y)
        {
            var gesture = new WorldPointerGestureClassifier(0.28f, 8f);
            gesture.Begin(new Vector2(10f, 20f), 1f, consumed: false);

            WorldPointerGestureResult result = gesture.Release(
                new Vector2(x, y),
                releaseTime,
                pointerBlocked: false);

            Assert.That(result, Is.EqualTo(WorldPointerGestureResult.Drag));
        }

        [Test]
        public void Gesture_ConsumedPress_NeverClicks()
        {
            var gesture = new WorldPointerGestureClassifier(0.28f, 8f);
            gesture.Begin(Vector2.zero, 1f, consumed: true);

            Assert.That(
                gesture.Release(Vector2.zero, 1.1f, pointerBlocked: false),
                Is.EqualTo(WorldPointerGestureResult.Consumed));
        }

        [Test]
        public void Arbitration_NearestCandidateWinsOutsideDepthTie()
        {
            int nearDispatches = 0;
            int farDispatches = 0;
            var candidates = new List<WorldInteractionCandidate>
            {
                Candidate("far-loot", 10f, 300, () => { farDispatches++; return true; }),
                Candidate("near-prop", 8f, 100, () => { nearDispatches++; return true; }),
            };

            bool dispatched = WorldInteractionArbitration.TryDispatchBest(candidates, Vector3.zero);

            Assert.That(dispatched, Is.True);
            Assert.That(nearDispatches, Is.EqualTo(1));
            Assert.That(farDispatches, Is.Zero);
        }

        [Test]
        public void Arbitration_PriorityBreaksDepthTie_AndDispatchesExactlyOnce()
        {
            int combatDispatches = 0;
            int propDispatches = 0;
            var candidates = new List<WorldInteractionCandidate>
            {
                Candidate("prop", 8f, 100, () => { propDispatches++; return true; }),
                Candidate("combat", 8.1f, 200, () => { combatDispatches++; return true; }),
            };

            bool dispatched = WorldInteractionArbitration.TryDispatchBest(candidates, Vector3.zero);

            Assert.That(dispatched, Is.True);
            Assert.That(combatDispatches, Is.EqualTo(1));
            Assert.That(propDispatches, Is.Zero);
        }

        [Test]
        public void Arbitration_RejectsOutOfRangeCandidate()
        {
            int dispatches = 0;
            var candidate = new WorldInteractionCandidate(
                WorldInteractionCandidateKind.Prop,
                "door",
                "OPEN",
                new Vector3(4f, 0f, 0f),
                4f,
                WorldInteractionArbitration.PropPriority,
                3f,
                () => { dispatches++; return true; });

            bool dispatched = WorldInteractionArbitration.TryDispatchBest(
                new[] { candidate },
                Vector3.zero);

            Assert.That(dispatched, Is.False);
            Assert.That(dispatches, Is.Zero);
        }

        [Test]
        public void ScreenTargeting_AcceptsForgivingNearMissWithinPadding()
        {
            bool selected = WorldInteractionScreenTargeting.TryScore(
                new Rect(100f, 100f, 80f, 160f),
                new Vector2(207f, 180f),
                32f,
                8f,
                out float score);

            Assert.That(selected, Is.True);
            Assert.That(score, Is.GreaterThanOrEqualTo(0f));
        }

        [Test]
        public void ScreenTargeting_RejectsPointerOutsideForgivingPadding()
        {
            bool selected = WorldInteractionScreenTargeting.TryScore(
                new Rect(100f, 100f, 80f, 160f),
                new Vector2(213f, 180f),
                32f,
                8f,
                out _);

            Assert.That(selected, Is.False);
        }

        private static WorldInteractionCandidate Candidate(
            string stableId,
            float depth,
            int priority,
            System.Func<bool> dispatch)
        {
            return new WorldInteractionCandidate(
                WorldInteractionCandidateKind.Prop,
                stableId,
                "USE",
                Vector3.zero,
                depth,
                priority,
                0f,
                dispatch);
        }
    }
}
