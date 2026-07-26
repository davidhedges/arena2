#nullable enable

using Arena.Interaction;
using Arena.Network;
using Arena.Simulation;
using NUnit.Framework;
using SpacetimeDB;
using SpacetimeDB.Types;
using UnityEditor;
using UnityEngine;

namespace Arena.EditModeTests
{
    public sealed class WorldInteractionPresentationTests
    {
        [TestCase(
            "Assets/Arena/Runtime/Interaction/DoorMotor.cs",
            typeof(DoorMotor))]
        [TestCase(
            "Assets/Arena/Runtime/Interaction/DoorInteractable.cs",
            typeof(DoorInteractable))]
        [TestCase(
            "Assets/Arena/Runtime/Interaction/WorldInteractionHitbox.cs",
            typeof(WorldInteractionHitbox))]
        public void RuntimeComponents_HaveStableUnityScriptAssets(
            string assetPath,
            System.Type expectedType)
        {
            MonoScript script = AssetDatabase.LoadAssetAtPath<MonoScript>(assetPath);

            Assert.That(script, Is.Not.Null, $"Missing MonoScript asset: {assetPath}");
            Assert.That(script.GetClass(), Is.EqualTo(expectedType));
        }

        [Test]
        public void LocalInteractionState_UsesAuthoritativeTimingAndNeverShowsInstantRows()
        {
            var state = new LocalInteractionState();
            Identity actor = default;
            state.Bind(actor);
            ActiveWorldInteraction row = CreateInteraction(
                actor,
                "action-1",
                startedAtMs: 1_000L,
                completesAtMs: 2_500L,
                label: "UNLOCKING_DOOR");

            state.OnActiveWorldInteractionInsert(null!, row);

            TimedActionPresentationSnapshot? active =
                state.CurrentTimedAction(1_750L);
            Assert.That(active.HasValue, Is.True);
            Assert.That(active!.Value.StartMs, Is.EqualTo(1_000L));
            Assert.That(active.Value.EndMs, Is.EqualTo(2_500L));
            Assert.That(active.Value.Label, Is.EqualTo("UNLOCKING DOOR"));
            Assert.That(
                active.Value.Style,
                Is.EqualTo(TimedActionPresentationStyle.WorldInteraction));

            Assert.That(state.CurrentTimedAction(2_500L), Is.Null);

            row.CompletesAt = new Timestamp(1_000_000L);
            state.OnActiveWorldInteractionUpdate(null!, row, row);
            Assert.That(state.CurrentTimedAction(1_000L), Is.Null);

            state.OnActiveWorldInteractionDelete(null!, row);
            Assert.That(state.Active, Is.Null);
        }

        [Test]
        public void TimedActionPresenter_ChoosesNewestDuringCallbackOverlap()
        {
            var combat = new TimedActionPresentationSnapshot(
                "SPELL",
                2_000L,
                3_000L,
                "SPELL",
                TimedActionPresentationStyle.CombatCast);
            var interaction = new TimedActionPresentationSnapshot(
                "USE",
                1_000L,
                4_000L,
                "USING",
                TimedActionPresentationStyle.WorldInteraction);

            TimedActionPresentationSnapshot? selected =
                TimedActionPresentation.Select(combat, interaction);
            Assert.That(selected!.Value.ActionId, Is.EqualTo("SPELL"));
            Assert.That(
                TimedActionPresentation.Select(null, interaction)!.Value.ActionId,
                Is.EqualTo("USE"));
        }

        [Test]
        public void AnimationTiming_LateSubscriberStartsInCurrentPhase()
        {
            WorldInteractionAnimationSample start =
                WorldInteractionAnimationTiming.Resolve(
                    1_250L,
                    1_000L,
                    4_000L,
                    startLengthMs: 500L,
                    loopLengthMs: 1_000L);
            Assert.That(start.Phase, Is.EqualTo(WorldInteractionAnimationPhase.Start));
            Assert.That(start.NormalizedTime, Is.EqualTo(0.5f).Within(0.001f));

            WorldInteractionAnimationSample lateLoop =
                WorldInteractionAnimationTiming.Resolve(
                    2_750L,
                    1_000L,
                    4_000L,
                    startLengthMs: 500L,
                    loopLengthMs: 1_000L);
            Assert.That(lateLoop.Phase, Is.EqualTo(WorldInteractionAnimationPhase.Loop));
            Assert.That(lateLoop.NormalizedTime, Is.EqualTo(0.25f).Within(0.001f));

            WorldInteractionAnimationSample completed =
                WorldInteractionAnimationTiming.Resolve(
                    4_000L,
                    1_000L,
                    4_000L,
                    startLengthMs: 500L,
                    loopLengthMs: 1_000L);
            Assert.That(completed.Phase, Is.EqualTo(WorldInteractionAnimationPhase.None));
        }

        [Test]
        public void DoorMotor_OrdersRevisionsAndAllowsMidSwingReversal()
        {
            var root = new GameObject("Door");
            var leaf = new GameObject("Leaf").transform;
            leaf.SetParent(root.transform, false);
            var secondLeaf = new GameObject("SecondLeaf").transform;
            secondLeaf.SetParent(root.transform, false);
            try
            {
                DoorAuthoring authoring = root.AddComponent<DoorAuthoring>();
                authoring.Configure(
                    "DOOR:TEST",
                    "RANDOM_DUNGEON",
                    templateOnly: false,
                    productionEnabled: true,
                    defaultOpen: false,
                    definitionVersion: 1,
                    openInteractionProfileId: "WORLD_DOOR_INSTANT",
                    closeInteractionProfileId: "WORLD_DOOR_INSTANT",
                    interactionAnchorLocal: Vector3.zero,
                    maxInteractionDistance: 3f,
                    closedBlockerCenterLocal: Vector3.zero,
                    closedBlockerSize: Vector3.one,
                    closedBlockerLocalYaw: 0f,
                    new[]
                    {
                        new DoorAuthoring.LeafPose(
                            leaf,
                            Quaternion.identity,
                            Quaternion.Euler(0f, 90f, 0f)),
                        new DoorAuthoring.LeafPose(
                            secondLeaf,
                            Quaternion.identity,
                            Quaternion.Euler(0f, -90f, 0f)),
                    });
                DoorMotor motor = root.AddComponent<DoorMotor>();
                motor.Configure(authoring);
                motor.SnapToState(open: false, revision: 2UL);
                motor.SnapToState(open: true, revision: 2UL);
                Assert.That(leaf.localEulerAngles.y, Is.EqualTo(90f).Within(0.01f));
                Assert.That(secondLeaf.localEulerAngles.y, Is.EqualTo(270f).Within(0.01f));
                motor.SnapToState(open: false, revision: 2UL);

                motor.ApplyAuthoritativeState(open: true, revision: 1UL, animate: true);
                Assert.That(motor.TargetOpen, Is.False);
                Assert.That(motor.AppliedRevision, Is.EqualTo(2UL));

                motor.ApplyAuthoritativeState(open: true, revision: 3UL, animate: true);
                Assert.That(motor.TargetOpen, Is.True);
                Assert.That(motor.IsMoving, Is.True);

                motor.ApplyAuthoritativeState(open: false, revision: 4UL, animate: true);
                Assert.That(motor.TargetOpen, Is.False);
                Assert.That(motor.AppliedRevision, Is.EqualTo(4UL));
                Assert.That(motor.IsMoving, Is.True);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void DoorReplication_SnapsSubscriptionBaselineAndAnimatesFreshChanges()
        {
            var row = new WorldDoorState(
                "OPEN:RandomDungeon:DOOR:TEST",
                "DOOR:TEST",
                "OPEN",
                null,
                "RandomDungeon",
                false,
                3UL,
                new Timestamp(10_000_000L));

            Assert.That(
                WorldDoorStateReplicator.ShouldAnimateReplicatedInsert(
                    subscriptionSnapshot: true,
                    row,
                    serverNowMs: 10_100L),
                Is.False);
            Assert.That(
                WorldDoorStateReplicator.ShouldAnimateReplicatedInsert(
                    subscriptionSnapshot: false,
                    row,
                    serverNowMs: 10_100L),
                Is.True);
            Assert.That(
                WorldDoorStateReplicator.ShouldAnimateReplicatedInsert(
                    subscriptionSnapshot: false,
                    row,
                    serverNowMs: 12_000L),
                Is.False);
        }

        private static ActiveWorldInteraction CreateInteraction(
            Identity actor,
            string actionId,
            long startedAtMs,
            long completesAtMs,
            string label)
        {
            return new ActiveWorldInteraction(
                actor,
                actionId,
                "DOOR",
                "DOOR:TEST",
                "OPEN:RandomDungeon:DOOR:TEST",
                "OPEN",
                true,
                0UL,
                "TIMED_HUMANOID_USE",
                "HUMANOID_USE",
                label,
                "OPEN",
                null,
                "RandomDungeon",
                0f,
                0f,
                0f,
                3f,
                0f,
                0f,
                0f,
                100,
                new Timestamp(startedAtMs * 1000L),
                new Timestamp(completesAtMs * 1000L),
                completesAtMs * 1000L,
                255U);
        }
    }
}
