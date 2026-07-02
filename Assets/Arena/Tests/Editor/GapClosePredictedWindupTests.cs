#nullable enable
using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Arena.EditModeTests
{
    /// <summary>
    /// Feel-audit F5 (slice 1): pure coverage of the predicted gap-close
    /// windup's playback substrate seams in CombatActionPlaybackController.
    ///
    /// Handoff math: the predicted windup's phase clock starts at press, so
    /// when the authoritative SpecialMovementRuntime row arrives (~RTT later)
    /// track sampling owns movement while the already-playing phased timeline
    /// carries its elapsed offset forward — Start holds until its exit
    /// threshold, the Loop holds until the row delete requests the end
    /// segment, and the End segment cancels at its completion threshold.
    ///
    /// Suppression: the server's COMBAT_CAST replay for the same action must
    /// be ignored as a duplicate while the predicted special-movement-driven
    /// windup is active, and must play again once the end segment has been
    /// requested (a same-action start after the dash is a new action).
    /// </summary>
    public class GapClosePredictedWindupTests
    {
        private const string ControllerTypeName = "Arena.Presentation.CombatActionPlaybackController";
        private const string PhaseTypeName = "Arena.Presentation.PhasedMeleePlaybackPhase";
        private const string AuthorityTypeName = "Arena.Presentation.CombatAnimationAuthority";

        // Mirror PlayerAnimator's authored thresholds so the cases pin the
        // shipped policy, not arbitrary numbers.
        private const float StartOnlyEndTriggerNormalizedTime = 0.82f;
        private const float SegmentTransitionNormalizedTime = 0.84f;
        private const float EndCompleteNormalizedTime = 0.88f;

        private static readonly Assembly RuntimeAssembly = AppDomain.CurrentDomain.Load("Assembly-CSharp");
        private static readonly Type ControllerType = RequireRuntimeType(ControllerTypeName);
        private static readonly Type PhaseType = RequireRuntimeType(PhaseTypeName);
        private static readonly Type AuthorityType = RequireRuntimeType(AuthorityTypeName);

        // -------------------------------------------------------------------
        // Handoff math: predicted windup elapsed → track-sampling segment/offset
        // -------------------------------------------------------------------

        [Test]
        public void Transition_StartBelowExit_KeepsPredictedWindupPlaying()
        {
            var result = ResolveTransition("Start", 0.5f, releaseAfterStart: false, endRequested: false);

            Assert.That(result.Transitioned, Is.False);
        }

        [Test]
        public void Transition_StartAtExit_EntersLoopHoldForTrackSampling()
        {
            var result = ResolveTransition(
                "Start",
                SegmentTransitionNormalizedTime,
                releaseAfterStart: false,
                endRequested: false);

            Assert.That(result.Transitioned, Is.True);
            Assert.That(result.NextPhase, Is.EqualTo("Loop"));
            Assert.That(result.ShouldCancel, Is.False);
        }

        [Test]
        public void Transition_StartAtExit_WithEndRequested_SkipsLoop()
        {
            // Short dash: the row delete landed while the windup was still in
            // its start segment.
            var result = ResolveTransition(
                "Start",
                SegmentTransitionNormalizedTime,
                releaseAfterStart: false,
                endRequested: true);

            Assert.That(result.Transitioned, Is.True);
            Assert.That(result.NextPhase, Is.EqualTo("End"));
        }

        [Test]
        public void Transition_ReleaseAfterStart_UsesStartOnlyThreshold()
        {
            var belowBoth = ResolveTransition("Start", 0.81f, releaseAfterStart: true, endRequested: false);
            Assert.That(belowBoth.Transitioned, Is.False);

            // 0.83 clears the release-after-start trigger (0.82) even though it
            // is below the segment-transition threshold (0.84).
            var betweenThresholds = ResolveTransition("Start", 0.83f, releaseAfterStart: true, endRequested: false);
            Assert.That(betweenThresholds.Transitioned, Is.True);
            Assert.That(betweenThresholds.NextPhase, Is.EqualTo("End"));
        }

        [Test]
        public void Transition_LoopHoldsUntilRowDeleteRequestsEnd()
        {
            // The loop repeats while track sampling drives the dash, no matter
            // how many times its clip has wrapped.
            var holding = ResolveTransition("Loop", 5.0f, releaseAfterStart: false, endRequested: false);
            Assert.That(holding.Transitioned, Is.False);

            var ended = ResolveTransition("Loop", 0.1f, releaseAfterStart: false, endRequested: true);
            Assert.That(ended.Transitioned, Is.True);
            Assert.That(ended.NextPhase, Is.EqualTo("End"));
        }

        [Test]
        public void Transition_EndCancelsOnlyAtCompletionThreshold()
        {
            var stillPlaying = ResolveTransition("End", 0.87f, releaseAfterStart: false, endRequested: true);
            Assert.That(stillPlaying.Transitioned, Is.False);

            var complete = ResolveTransition("End", EndCompleteNormalizedTime, releaseAfterStart: false, endRequested: true);
            Assert.That(complete.Transitioned, Is.True);
            Assert.That(complete.ShouldCancel, Is.True);
        }

        [Test]
        public void PredictedWindupElapsed_CarriesIntoTrackSamplingOffset()
        {
            object controller = Activator.CreateInstance(ControllerType)!;
            AnimationClip start = CreateOneSecondClip();
            AnimationClip loop = CreateOneSecondClip();
            AnimationClip end = CreateOneSecondClip();

            try
            {
                BeginSpecialMovementDrivenPhasedMelee(controller, start, loop, end);
                Assert.That(
                    GetBool(controller, "IsPhasedMeleeSpecialMovementDriven"),
                    Is.True);
                Assert.That(
                    GetBool(controller, "IsPhasedMeleeSpecialMovementEndRequested"),
                    Is.False);

                // Predicted windup: the start segment (0.5 s) completes while
                // the authoritative row is in flight...
                SetSegment(controller, "Start", stateHash: 11, phaseLengthSeconds: 0.5f);
                RequireMethod(ControllerType, "AddCompletedPhasedMeleePhaseSeconds", typeof(float))
                    .Invoke(controller, new object[] { 1f });

                // ...and the loop (the track-sampling era) starts carrying the
                // full windup elapsed as its offset instead of restarting at 0.
                SetSegment(controller, "Loop", stateHash: 22, phaseLengthSeconds: 1f);
                MethodInfo timing = RequireMethod(
                    ControllerType,
                    "TryGetPhasedMeleePresentationTiming",
                    typeof(float),
                    typeof(float).MakeByRefType(),
                    typeof(float).MakeByRefType());
                object?[] timingArgs = { 0.25f, 0f, 0f };
                Assert.That((bool)timing.Invoke(controller, timingArgs)!, Is.True);
                Assert.That((float)timingArgs[1]!, Is.EqualTo(0.75f).Within(0.001f));

                // Row delete: the end request latches for the transition policy.
                Assert.That(
                    (bool)RequireMethod(ControllerType, "RequestPhasedMeleeSpecialMovementEnd")
                        .Invoke(controller, Array.Empty<object>())!,
                    Is.True);
                Assert.That(
                    GetBool(controller, "IsPhasedMeleeSpecialMovementEndRequested"),
                    Is.True);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(start);
                UnityEngine.Object.DestroyImmediate(loop);
                UnityEngine.Object.DestroyImmediate(end);
            }
        }

        // -------------------------------------------------------------------
        // Suppression: authoritative start after predicted start must not
        // double-play
        // -------------------------------------------------------------------

        [Test]
        public void AuthoritativeSameActionStart_WhileWindupActive_IsDuplicate()
        {
            Assert.That(
                IsDuplicate(
                    authority: "Authoritative",
                    drivesPhases: true,
                    matchesActiveAction: true,
                    activeIsSpecialMovementDriven: true,
                    activeEndRequested: false),
                Is.True);
        }

        [Test]
        public void PredictedStart_IsNeverTreatedAsDuplicate()
        {
            // A fresh local press must always be able to (re)start the windup.
            Assert.That(
                IsDuplicate(
                    authority: "Predicted",
                    drivesPhases: true,
                    matchesActiveAction: true,
                    activeIsSpecialMovementDriven: true,
                    activeEndRequested: false),
                Is.False);
        }

        [Test]
        public void AuthoritativeStart_AfterDashEnded_IsNotDuplicate()
        {
            Assert.That(
                IsDuplicate(
                    authority: "Authoritative",
                    drivesPhases: true,
                    matchesActiveAction: true,
                    activeIsSpecialMovementDriven: true,
                    activeEndRequested: true),
                Is.False);
        }

        [Test]
        public void UnrelatedAuthoritativeStarts_AreNotDuplicates()
        {
            // Different action id.
            Assert.That(
                IsDuplicate("Authoritative", true, matchesActiveAction: false, true, false),
                Is.False);
            // Active phased melee is time-driven, not a gap-close windup.
            Assert.That(
                IsDuplicate("Authoritative", true, true, activeIsSpecialMovementDriven: false, false),
                Is.False);
            // Incoming request does not drive phases from special movement.
            Assert.That(
                IsDuplicate("Authoritative", drivesPhases: false, true, true, false),
                Is.False);
        }

        [Test]
        public void AuthoritativeReplayAfterPredictedWindup_DoesNotDoublePlay()
        {
            object controller = Activator.CreateInstance(ControllerType)!;
            AnimationClip start = CreateOneSecondClip();
            AnimationClip loop = CreateOneSecondClip();
            AnimationClip end = CreateOneSecondClip();

            try
            {
                BeginSpecialMovementDrivenPhasedMelee(controller, start, loop, end);

                // The authoritative COMBAT_CAST replay for the same action is a
                // duplicate of the running predicted windup...
                Assert.That(
                    IsDuplicate(
                        "Authoritative",
                        drivesPhases: true,
                        matchesActiveAction: true,
                        activeIsSpecialMovementDriven: GetBool(controller, "IsPhasedMeleeSpecialMovementDriven"),
                        activeEndRequested: GetBool(controller, "IsPhasedMeleeSpecialMovementEndRequested")),
                    Is.True);

                // ...until the row delete requests the end segment; after that a
                // same-action authoritative start is a new action.
                RequireMethod(ControllerType, "RequestPhasedMeleeSpecialMovementEnd")
                    .Invoke(controller, Array.Empty<object>());
                Assert.That(
                    IsDuplicate(
                        "Authoritative",
                        drivesPhases: true,
                        matchesActiveAction: true,
                        activeIsSpecialMovementDriven: GetBool(controller, "IsPhasedMeleeSpecialMovementDriven"),
                        activeEndRequested: GetBool(controller, "IsPhasedMeleeSpecialMovementEndRequested")),
                    Is.False);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(start);
                UnityEngine.Object.DestroyImmediate(loop);
                UnityEngine.Object.DestroyImmediate(end);
            }
        }

        // -------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------

        private static (bool Transitioned, string NextPhase, bool ShouldCancel) ResolveTransition(
            string currentPhase,
            float normalizedTime,
            bool releaseAfterStart,
            bool endRequested)
        {
            MethodInfo method = RequireMethod(
                ControllerType,
                "TryResolveSpecialMovementDrivenPhasedTransition",
                PhaseType,
                typeof(float),
                typeof(bool),
                typeof(bool),
                typeof(float),
                typeof(float),
                typeof(float),
                PhaseType.MakeByRefType(),
                typeof(bool).MakeByRefType());
            object?[] args =
            {
                Enum.Parse(PhaseType, currentPhase),
                normalizedTime,
                releaseAfterStart,
                endRequested,
                StartOnlyEndTriggerNormalizedTime,
                SegmentTransitionNormalizedTime,
                EndCompleteNormalizedTime,
                Enum.Parse(PhaseType, "None"),
                false,
            };

            bool transitioned = (bool)method.Invoke(null, args)!;
            return (transitioned, args[7]!.ToString()!, (bool)args[8]!);
        }

        private static bool IsDuplicate(
            string authority,
            bool drivesPhases,
            bool matchesActiveAction,
            bool activeIsSpecialMovementDriven,
            bool activeEndRequested)
        {
            MethodInfo method = RequireMethod(
                ControllerType,
                "IsDuplicateAuthoritativeSpecialMovementMeleeStart",
                AuthorityType,
                typeof(bool),
                typeof(bool),
                typeof(bool),
                typeof(bool));
            return (bool)method.Invoke(
                null,
                new object?[]
                {
                    Enum.Parse(AuthorityType, authority),
                    drivesPhases,
                    matchesActiveAction,
                    activeIsSpecialMovementDriven,
                    activeEndRequested,
                })!;
        }

        private static void BeginSpecialMovementDrivenPhasedMelee(
            object controller,
            AnimationClip start,
            AnimationClip loop,
            AnimationClip end)
        {
            RequireMethod(
                    ControllerType,
                    "BeginPhasedMelee",
                    typeof(int),
                    typeof(AnimationClip),
                    typeof(AnimationClip),
                    typeof(AnimationClip),
                    typeof(bool),
                    typeof(bool))
                .Invoke(controller, new object[] { 1, start, loop, end, false, true });
        }

        private static void SetSegment(object controller, string phase, int stateHash, float phaseLengthSeconds)
        {
            RequireMethod(ControllerType, "SetPhasedMeleeSegment", PhaseType, typeof(int), typeof(float))
                .Invoke(controller, new[] { Enum.Parse(PhaseType, phase), stateHash, (object)phaseLengthSeconds });
        }

        private static bool GetBool(object instance, string propertyName)
        {
            PropertyInfo property = instance.GetType().GetProperty(
                                        propertyName,
                                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                                    ?? throw new AssertionException($"Missing property {propertyName}");
            return (bool)property.GetValue(instance)!;
        }

        private static Type RequireRuntimeType(string fullName)
        {
            return RuntimeAssembly.GetType(fullName)
                   ?? throw new InvalidOperationException($"Missing runtime type {fullName}");
        }

        private static MethodInfo RequireMethod(Type type, string name, params Type[] parameterTypes)
        {
            return type.GetMethod(
                       name,
                       BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance,
                       binder: null,
                       types: parameterTypes,
                       modifiers: null)
                   ?? throw new AssertionException($"Missing method {type.FullName}.{name}");
        }

        private static AnimationClip CreateOneSecondClip()
        {
            AnimationClip clip = new AnimationClip();
            clip.SetCurve(
                relativePath: string.Empty,
                type: typeof(Transform),
                propertyName: "localPosition.x",
                curve: AnimationCurve.Linear(0f, 0f, 1f, 1f));
            return clip;
        }
    }
}
