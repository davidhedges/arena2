#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Arena.Input;
using Arena.Presentation;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Arena.EditModeTests
{
    public sealed class JumpAnimationPlaybackTests
    {
        private const string ControllerPath = "Assets/Arena/Content/Animation/Arena_Character.controller";
        private static readonly MovementStepContext Context = new(false, 1f, 0.28f, 1.8f);

        [TestCase(0f)]
        [TestCase(0.5f)]
        [TestCase(0.999f)]
        public void GroundedPredictionDoesNotPlantBeforeRenderedContact(float alpha)
        {
            var older = State(new Vector3(0f, 0.2f, 0f), new Vector3(0f, -7f, 0f));
            var newer = State(Vector3.zero, Vector3.zero, true);
            var sample = JumpAnimationPrediction.Interpolate(older, newer, alpha);
            Assert.That(sample.Grounded, Is.False);
            Assert.That(sample.Position.y, Is.GreaterThan(0f));
            Assert.That(sample.Velocity.y, Is.LessThan(0f));
            Assert.That(JumpAnimationPrediction.Interpolate(older, newer, 1f).Grounded, Is.True);
        }

        [Test]
        public void TakeoffInterpolationPreservesJumpImpulseAndGroundedSlopesStayGrounded()
        {
            var ground = State(Vector3.zero, Vector3.zero, true);
            var air = State(new Vector3(0f, 0.25f, 0f), new Vector3(0f, 8f, 0f));
            var sample = JumpAnimationPrediction.Interpolate(ground, air, 0.01f);
            Assert.That(sample.Velocity.y, Is.EqualTo(8f));
            Assert.That(sample.Grounded, Is.False);
            Assert.That(JumpAnimationPrediction.Interpolate(ground,
                State(Vector3.up, Vector3.forward, true), 0.5f).Grounded, Is.True);
        }

        [TestCase(0)] // flat
        [TestCase(1)] // rising slope
        [TestCase(2)] // ledge dropping away in the direction of travel
        [TestCase(3)] // wall stops horizontal travel before a drop
        public void LandingForecastMatchesActualMovementOnTheFutureTrajectory(int terrain)
        {
            var environment = new Terrain(terrain);
            var initial = State(new Vector3(0f, 1f, 0f), new Vector3(0f, -4f, 7f));
            bool found = JumpAnimationPrediction.TryFindLanding(initial, Context, environment, 0.4f, out float seconds);
            var actual = initial;
            float? contact = null;
            for (int tick = 1; tick <= 12; tick++)
            {
                actual = MovementPrediction.Step(actual, new MovementCommand((uint)tick, 0, 0, 0, false),
                    Context, environment, MovementNetcodeConfig.FixedTickSeconds);
                if (!actual.Grounded) continue;
                contact = tick * MovementNetcodeConfig.FixedTickSeconds;
                break;
            }
            Assert.That(found, Is.EqualTo(contact.HasValue));
            if (contact.HasValue) Assert.That(seconds, Is.EqualTo(contact.Value).Within(0.0001f));
            if (terrain == 2) Assert.That(found, Is.False, "Do not land on the height of the ledge already left behind.");
            if (terrain == 3) Assert.That(found, Is.True, "The wall should prevent travelling over the drop.");
            Assert.That(initial.Position.y, Is.EqualTo(1f), "Forecasting must not advance the source state.");
        }

        [Test]
        public void RisingOrBottomlessMotionDoesNotPredictLanding()
        {
            Assert.That(JumpAnimationPrediction.TryFindLanding(State(Vector3.up, Vector3.up),
                Context, new Terrain(0), 0.4f, out _), Is.False);
            Assert.That(JumpAnimationPrediction.TryFindLanding(State(Vector3.up, Vector3.down),
                Context, new Terrain(4), 0.4f, out _), Is.False);
        }

        [TestCase(30)]
        [TestCase(60)]
        [TestCase(120)]
        public void ClipApproachesContactThenReleasesRecoveryAtAnyFrameRate(int fps)
        {
            var playback = new JumpAnimationPlayback();
            float dt = 1f / fps;
            playback.Tick(false, 8f, null, 0.6f, 0.2f, dt);
            Assert.That(playback.JumpStarted, Is.True);
            playback.Tick(false, 4f, null, 0.6f, 0.2f, dt);
            Assert.That(playback.JumpPhase, Is.EqualTo(0.5f).Within(0.001f));
            playback.Tick(false, 0f, null, 0.6f, 0.2f, dt);
            Assert.That(playback.JumpPhase, Is.GreaterThan(0.99f));
            float last = 0f;
            for (float remaining = 0.27f; remaining > 0f; remaining -= dt)
            {
                playback.Tick(false, -6f, remaining, 0.6f, 0.2f, dt);
                Assert.That(playback.IsPreparingLanding, Is.True);
                Assert.That(playback.LandingPhase, Is.GreaterThanOrEqualTo(last));
                Assert.That(playback.LandingPhase, Is.LessThan(0.2f / 0.6f));
                Assert.That(playback.CanRecoverWhileMoving, Is.False);
                last = playback.LandingPhase;
            }
            playback.Tick(true, 0f, 0f, 0.6f, 0.2f, dt);
            Assert.That(playback.LandingPhase, Is.EqualTo(0.2f / 0.6f).Within(0.0001f));
            Assert.That(playback.CanRecoverWhileMoving, Is.False);
            for (int i = 0; i < Mathf.CeilToInt(0.13f / dt); i++)
                playback.Tick(true, 0f, 0f, 0.6f, 0.2f, dt);
            Assert.That(playback.CanRecoverWhileMoving, Is.True);
        }

        [Test]
        public void ChangedLandingEstimateNeverScrubsBackwardsAndMissingFloorCancelsApproach()
        {
            var playback = new JumpAnimationPlayback();
            playback.Tick(false, -4f, 0.1f, 0.6f, 0.2f, 0.016f);
            float phase = playback.LandingPhase;
            playback.Tick(false, -4f, 0.15f, 0.6f, 0.2f, 0.016f);
            Assert.That(playback.LandingPhase, Is.EqualTo(phase));
            playback.Tick(false, -4f, null, 0.6f, 0.2f, 0.016f);
            Assert.That(playback.LandingCancelled, Is.True);
            Assert.That(playback.IsPreparingLanding, Is.False);
            Assert.That(playback.CanRecoverWhileMoving, Is.False);
            playback.Tick(false, -8f, 0.25f, 0.6f, 0.2f, 0.016f);
            Assert.That(playback.LandingStarted, Is.True);
            Assert.That(playback.LandingPhase, Is.Zero);
        }

        [Test]
        public void UnexpectedContactAndRejumpDoNotReplayAnticipationOrHoldRecovery()
        {
            var playback = new JumpAnimationPlayback();
            playback.Tick(false, 8f, null, 0.6f, 0.2f, 0.016f);
            playback.Tick(true, 0f, 0f, 0.6f, 0.2f, 0.016f);
            Assert.That(playback.LandingPhase, Is.EqualTo(1f / 3f).Within(0.0001f));
            float outgoingLandingPhase = playback.LandingPhase;
            playback.Tick(false, 8f, null, 0.6f, 0.2f, 0.016f);
            Assert.That(playback.JumpStarted, Is.True);
            Assert.That(playback.JumpPhase, Is.Zero);
            Assert.That(playback.IsPreparingLanding, Is.False);
            Assert.That(playback.CanRecoverWhileMoving, Is.False);
            Assert.That(playback.LandingPhase, Is.EqualTo(outgoingLandingPhase),
                "Re-jumping must preserve the outgoing landing pose during its blend.");
        }

        [Test]
        public void WalkOffLedgeStartsFallingWithoutPlayingTakeoff()
        {
            var playback = new JumpAnimationPlayback();
            playback.Tick(false, -1f, null, 0.6f, 0.2f, 0.016f);
            Assert.That(playback.JumpStarted, Is.False);
            Assert.That(playback.JumpPhase, Is.GreaterThan(0.99f));
            Assert.That(playback.IsFalling, Is.True);
        }

        [TestCase(30)]
        [TestCase(60)]
        [TestCase(120)]
        public void ShortJumpAnimatesThroughoutDescentWithoutSelectingFalling(int fps)
        {
            var playback = new JumpAnimationPlayback();
            float dt = 1f / fps;
            playback.Tick(false, 8f, null, 0.6f, 0.12f, dt);
            playback.Tick(false, 0f, 0.36f, 0.6f, 0.12f, dt);
            Assert.That(playback.IsPreparingLanding, Is.True, "Begin the direct blend at the apex.");
            float previous = playback.LandingPhase;
            for (float remaining = 0.36f - dt; remaining > 0.05f; remaining -= dt)
            {
                playback.Tick(false, -4f, remaining, 0.6f, 0.12f, dt);
                Assert.That(playback.IsFalling, Is.False);
                Assert.That(playback.LandingPhase, Is.GreaterThan(previous), "Do not replace falling with a held apex pose.");
                Assert.That(playback.LandingPhase, Is.LessThan(0.12f / 0.6f));
                previous = playback.LandingPhase;
            }
            playback.Tick(true, 0f, 0f, 0.6f, 0.12f, dt);
            Assert.That(playback.LandingPhase, Is.EqualTo(0.12f / 0.6f).Within(0.0001f));
            Assert.That(playback.IsFalling, Is.False);
        }

        [Test]
        public void ShortJumpBecomesSustainedFallWhenItsLandingSurfaceDisappears()
        {
            var playback = new JumpAnimationPlayback();
            playback.Tick(false, 8f, null, 0.6f, 0.2f, 0.016f);
            playback.Tick(false, 0f, 0.36f, 0.6f, 0.2f, 0.016f);
            playback.Tick(false, -2f, 0.28f, 0.6f, 0.2f, 0.016f);
            float outgoing = playback.LandingPhase;
            playback.Tick(false, -3f, null, 0.6f, 0.2f, 0.016f);
            Assert.That(playback.IsFalling, Is.True);
            Assert.That(playback.LandingCancelled, Is.True);
            Assert.That(playback.LandingPhase, Is.EqualTo(outgoing));
            playback.Tick(false, -6f, 0.35f, 0.6f, 0.2f, 0.016f);
            Assert.That(playback.IsPreparingLanding, Is.False, "A sustained fall must retain its normal landing window.");
            playback.Tick(false, -8f, 0.1f, 0.6f, 0.2f, 0.016f);
            Assert.That(playback.IsPreparingLanding, Is.True);
            Assert.That(playback.LandingPhase, Is.EqualTo(0.1f / 0.6f).Within(0.0001f));
        }

        private static IEnumerable<TestCaseData> JumpRoutes()
        {
            foreach (string set in new[] { "TwoHandedSword", "SwordAndShield", "ArcherBow", "Daggers", "Staff" })
            foreach (bool combat in new[] { false, true })
            foreach (Vector2 direction in new[] { Vector2.zero, Vector2.up, Vector2.right, Vector2.down, Vector2.left })
                yield return new TestCaseData(set, combat, direction, false);
            // Also exercise a real moving jump beyond a ledge, with both stances.
            foreach (bool combat in new[] { false, true })
                yield return new TestCaseData("TwoHandedSword", combat, Vector2.up, true);
        }

        [TestCaseSource(nameof(JumpRoutes))]
        public void ActualJumpTrajectoryChoosesDirectLandingOrSustainedFall(
            string setName, bool combat, Vector2 direction, bool cliff)
        {
            var rig = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/Arena/Resources/CharacterAvatarBases/HumanMale.prefab"));
            rig.hideFlags = HideFlags.HideAndDontSave;
            var overrides = new AnimatorOverrideController(AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath));
            try
            {
                var set = AssetDatabase.LoadAssetAtPath<CombatAnimationSet>("Assets/Arena/Resources/CombatAnimationSets/" + setName + ".asset");
                var binderType = typeof(PlayerAnimator).Assembly.GetType("Arena.Presentation.CombatAnimationSetBinder")!;
                binderType.GetMethod("Bind")!.Invoke(Activator.CreateInstance(binderType), new object[] { set, overrides });
                var animator = rig.GetComponentInChildren<Animator>();
                animator.runtimeAnimatorController = overrides;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                animator.applyRootMotion = false;
                CombatAnimationEventReceiver.EnsureOn(animator);
                var clips = combat ? set.jumpLandCombat : set.jumpLand;
                AnimationClip clip = (direction.x > 0 ? clips.e : direction.x < 0 ? clips.w
                    : direction.y > 0 ? clips.n : direction.y < 0 ? clips.s : clips.center)!;
                float contact = CombatAnimationEvents.GetEventTimeOrFallback(clip, CombatAnimationEvents.OnGroundedFrame, 0f);
                string start = combat ? "JumpStartCombat" : "JumpStart";
                string air = combat ? "InAirCombat" : "InAir";
                string land = combat ? "JumpLandCombat" : "JumpLand";
                animator.SetBool("InCombat", combat);
                animator.SetBool("Grounded", true);
                animator.SetFloat("JumpX", direction.x);
                animator.SetFloat("JumpZ", direction.y);
                animator.Play(combat ? "IdleCombat" : "Idle Walk Run Blend", 0, 0f);
                animator.Update(0f);
                var playback = new JumpAnimationPlayback();
                var state = State(Vector3.zero, Vector3.zero, true);
                var terrain = new Terrain(cliff ? 2 : 0);
                bool sawJump = false, sawFall = false, sawLanding = false;
                float dt = MovementNetcodeConfig.FixedTickSeconds;
                for (uint tick = 1; tick <= 60; tick++)
                {
                    animator.Update(dt);
                    state = MovementPrediction.Step(state,
                        new MovementCommand(tick, direction.y, direction.x, 0f, tick == 1), Context, terrain, dt);
                    float? remaining = JumpAnimationPrediction.TryFindLanding(state, Context, terrain,
                        JumpAnimationPrediction.LookAheadSeconds, out float seconds) ? seconds : null;
                    playback.Tick(state.Grounded, state.Velocity.y, remaining, clip.length, contact, dt);
                    animator.SetBool("Grounded", state.Grounded);
                    animator.SetBool("Jump", playback.JumpStarted);
                    animator.SetBool("FreeFall", !state.Grounded);
                    animator.SetBool("Falling", playback.IsFalling);
                    animator.SetBool("Landing", playback.IsPreparingLanding);
                    animator.SetFloat("JumpPhase", playback.JumpPhase);
                    animator.SetFloat("LandingPhase", playback.LandingPhase);
                    animator.Update(0f);
                    var current = animator.GetCurrentAnimatorStateInfo(0);
                    var next = animator.GetNextAnimatorStateInfo(0);
                    bool In(string name) => current.IsName(name) || (animator.IsInTransition(0) && next.IsName(name));
                    sawJump |= In(start);
                    sawFall |= In(air);
                    sawLanding |= In(land);
                    if (!cliff) Assert.That(In(air), Is.False, "An ordinary jump must never even blend toward the falling pose.");
                    if (!state.Grounded) continue;
                    Assert.That(In(land), Is.True, "Reach the landing state by actual contact.");
                    Assert.That(playback.LandingPhase, Is.EqualTo(contact / clip.length).Within(0.0001f));
                    break;
                }
                Assert.That(state.Grounded, Is.True);
                Assert.That(sawJump, Is.True);
                Assert.That(sawLanding, Is.True);
                Assert.That(sawFall, Is.EqualTo(cliff));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(rig);
                UnityEngine.Object.DestroyImmediate(overrides);
            }
        }

        [Test]
        public void EveryShippedLandingClipHasOneUsableContactMarker()
        {
            var clips = AssetDatabase.FindAssets("t:CombatAnimationSet", new[] { "Assets/Arena/Resources/CombatAnimationSets" })
                .Select(g => AssetDatabase.LoadAssetAtPath<CombatAnimationSet>(AssetDatabase.GUIDToAssetPath(g)))
                .SelectMany(s => new[] { s.jumpLand.center, s.jumpLand.n, s.jumpLand.e, s.jumpLand.s, s.jumpLand.w,
                    s.jumpLandCombat.center, s.jumpLandCombat.n, s.jumpLandCombat.e, s.jumpLandCombat.s, s.jumpLandCombat.w })
                .Where(c => c != null).Cast<AnimationClip>().Distinct().ToArray();
            Assert.That(clips, Is.Not.Empty);
            foreach (AnimationClip clip in clips)
            {
                var markers = clip.events.Where(e => e.functionName == CombatAnimationEvents.OnGroundedFrame).ToArray();
                Assert.That(markers.Length, Is.EqualTo(1), clip.name);
                Assert.That(markers[0].time, Is.InRange(0.05f, Mathf.Min(0.35f, clip.length - 0.12f)), clip.name);
            }
        }

        [Test]
        public void SamplingLocalJumpAnimationDoesNotMutatePredictionOrMovementContext()
        {
            var root = new GameObject("JumpPredictionReadOnlyTest");
            try
            {
                var driver = root.AddComponent<LocalMovementPredictionDriver>();
                var state = State(new Vector3(0f, 0.7f, 0f), new Vector3(0f, -4f, 0f));
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                void Set(string name, object value) => typeof(LocalMovementPredictionDriver).GetField(name, flags)!.SetValue(driver, value);
                Set("_environment", new Terrain(0));
                Set("_hasCurrentPredictedState", true);
                Set("_currentPredictedState", state);
                Set("_effectiveMoveSpeedMultiplier", 1.234f);
                typeof(LocalMovementPredictionDriver).GetMethod("PushRenderSample", flags)!.Invoke(driver, new object[] { state });
                Assert.That(driver.TryGetJumpAnimationSample(out var sample, out var landing), Is.True);
                Assert.That(landing.HasValue, Is.True);
                Assert.That(sample.Grounded, Is.False);
                Assert.That(driver.CurrentPredictedPosition, Is.EqualTo(state.Position));
                Assert.That(driver.CurrentPredictedTick, Is.EqualTo(state.LastProcessedTick));
                Assert.That(typeof(LocalMovementPredictionDriver).GetField("_effectiveMoveSpeedMultiplier", flags)!.GetValue(driver), Is.EqualTo(1.234f));
                Assert.That(typeof(LocalMovementPredictionDriver).GetField("_renderHistoryCount", flags)!.GetValue(driver), Is.EqualTo(1));
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        [TestCase("TwoHandedSword")]
        [TestCase("SwordAndShield")]
        [TestCase("ArcherBow")]
        [TestCase("Daggers")]
        [TestCase("Staff")]
        public void ContactPhaseResamplesTheActualFeetWithoutAdvancingTheAnimationClock(string setName)
        {
            var rig = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/Arena/Resources/CharacterAvatarBases/HumanMale.prefab"));
            rig.hideFlags = HideFlags.HideAndDontSave;
            var overrides = new AnimatorOverrideController(AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath));
            try
            {
                var set = AssetDatabase.LoadAssetAtPath<CombatAnimationSet>("Assets/Arena/Resources/CombatAnimationSets/" + setName + ".asset");
                var binderType = typeof(PlayerAnimator).Assembly.GetType("Arena.Presentation.CombatAnimationSetBinder")!;
                binderType.GetMethod("Bind")!.Invoke(Activator.CreateInstance(binderType), new object[] { set, overrides });
                var animator = rig.GetComponentInChildren<Animator>();
                animator.runtimeAnimatorController = overrides;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                animator.applyRootMotion = false;
                CombatAnimationEventReceiver.EnsureOn(animator);
                animator.SetBool("Landing", true);
                animator.SetBool("Grounded", true);
                animator.Play("JumpLand", 0, 0f);
                animator.Update(0f);
                float Feet() => Mathf.Min(animator.GetBoneTransform(HumanBodyBones.LeftFoot).position.y,
                    animator.GetBoneTransform(HumanBodyBones.RightFoot).position.y) - rig.transform.position.y;
                float approachFeet = Feet();
                float clock = animator.GetCurrentAnimatorStateInfo(0).normalizedTime;
                Assert.That(CombatAnimationEvents.TryGetEventNormalizedTime(set.jumpLand.center,
                    CombatAnimationEvents.OnGroundedFrame, out float contact), Is.True);
                animator.SetFloat("LandingPhase", contact);
                animator.Update(0f);
                Assert.That(Feet(), Is.LessThan(approachFeet - 0.03f),
                    "The zero-delta late evaluation must move the visible feet into the contact pose.");
                Assert.That(animator.GetCurrentAnimatorStateInfo(0).normalizedTime, Is.EqualTo(clock).Within(0.0001f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(rig);
                UnityEngine.Object.DestroyImmediate(overrides);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AnimatorUsesMotionPhaseAndDoesNotExitLandingBeforeContact(bool combat)
        {
            var rig = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/Arena/Resources/CharacterAvatarBases/HumanMale.prefab"));
            rig.hideFlags = HideFlags.HideAndDontSave;
            var overrides = new AnimatorOverrideController(AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath));
            try
            {
                var set = AssetDatabase.LoadAssetAtPath<CombatAnimationSet>("Assets/Arena/Resources/CombatAnimationSets/TwoHandedSword.asset");
                var binderType = typeof(PlayerAnimator).Assembly.GetType("Arena.Presentation.CombatAnimationSetBinder")!;
                binderType.GetMethod("Bind")!.Invoke(Activator.CreateInstance(binderType), new object[] { set, overrides });
                var animator = rig.GetComponentInChildren<Animator>();
                animator.runtimeAnimatorController = overrides;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                animator.applyRootMotion = false;
                CombatAnimationEventReceiver.EnsureOn(animator);
                string start = combat ? "JumpStartCombat" : "JumpStart";
                string air = combat ? "InAirCombat" : "InAir";
                string land = combat ? "JumpLandCombat" : "JumpLand";
                animator.SetBool("InCombat", combat);
                animator.SetBool("Grounded", false);
                animator.SetBool("FreeFall", true);
                animator.SetFloat("JumpPhase", 0.5f);
                animator.Play(start, 0, 0f);
                animator.Update(2f);
                Assert.That(animator.GetCurrentAnimatorStateInfo(0).IsName(start), Is.True,
                    "Clip elapsed time must not determine the apex.");
                animator.SetFloat("JumpPhase", 0.999f);
                animator.SetBool("Falling", true);
                animator.Update(0.15f);
                animator.Update(0.15f);
                Assert.That(animator.GetCurrentAnimatorStateInfo(0).IsName(air), Is.True);
                animator.SetBool("Landing", true);
                animator.SetFloat("LandingPhase", 0.1f);
                animator.Update(0.15f);
                animator.Update(2f);
                Assert.That(animator.GetCurrentAnimatorStateInfo(0).IsName(land), Is.True,
                    "Anticipation must not run through the impact/recovery on an elapsed-time exit.");
                animator.SetBool("Landing", false);
                animator.Update(0.15f);
                animator.Update(0.15f);
                Assert.That(animator.GetCurrentAnimatorStateInfo(0).IsName(air), Is.True,
                    "A missing landing surface must release the landing pose.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(rig);
                UnityEngine.Object.DestroyImmediate(overrides);
            }
        }

        private static PredictedMovementState State(Vector3 position, Vector3 velocity, bool grounded = false)
            => new(position, velocity, 0f, grounded, 10);

        private sealed class Terrain : IMovementEnvironment
        {
            private readonly int _kind;
            public Terrain(int kind) => _kind = kind;
            public float SampleGroundHeight(float x, float z, float probeY)
                => _kind == 1 ? z * 0.4f : (_kind == 2 || _kind == 3) && z > 0.5f ? -5f : 0f;
            public bool TrySampleGroundHeight(float x, float z, float probeY, out float groundY)
            {
                groundY = SampleGroundHeight(x, z, probeY);
                return _kind != 4;
            }
            public Vector2 ResolveHorizontalCollision(float startX, float startZ, float desiredX, float desiredZ,
                float playerRadius, float playerHeight, float currentY)
                => new(desiredX, _kind == 3 ? Mathf.Min(0.4f, desiredZ) : desiredZ);
        }
    }
}
