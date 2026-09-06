#nullable enable

using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Arena.Tests.Editor
{
    public sealed class CombatAnimationVisualInterruptTests
    {
        private static readonly Assembly RuntimeAssembly = AppDomain.CurrentDomain.Load("Assembly-CSharp");

        [Test]
        public void AnimatorController_LayerOrderMatchesCombatAnimationContract()
        {
            AnimatorController controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(
                "Assets/Arena/Content/Animation/Arena_Character.controller");

            Assert.That(controller, Is.Not.Null);
            Assert.That(controller.layers.Length, Is.GreaterThanOrEqualTo(7));
            Assert.That(controller.layers[0].name, Is.EqualTo("Base Layer"));
            Assert.That(controller.layers[1].name, Is.EqualTo("UpperBody"));
            Assert.That(controller.layers[2].name, Is.EqualTo("HitReaction"));
            Assert.That(controller.layers[3].name, Is.EqualTo("MeleeAttack"));
            Assert.That(controller.layers[4].name, Is.EqualTo("SpellAction"));
            Assert.That(controller.layers[5].name, Is.EqualTo("LeftGesture"));
            Assert.That(controller.layers[6].name, Is.EqualTo("RightGesture"));
        }

        [Test]
        public void AnimatorController_HitReactionLayerIsFullBodyOverride()
        {
            AnimatorController controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(
                "Assets/Arena/Content/Animation/Arena_Character.controller");

            Assert.That(controller, Is.Not.Null);
            AnimatorControllerLayer hitReaction = controller.layers[2];

            Assert.That(hitReaction.name, Is.EqualTo("HitReaction"));
            Assert.That(hitReaction.avatarMask, Is.Null);
            Assert.That(hitReaction.blendingMode, Is.EqualTo(AnimatorLayerBlendingMode.Override));
            Assert.That(hitReaction.defaultWeight, Is.EqualTo(1f));
        }

        [Test]
        public void AnimatorController_LeftGestureLayerUsesGestureMaskAndSpellBankStates()
        {
            AnimatorController controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(
                "Assets/Arena/Content/Animation/Arena_Character.controller");
            AvatarMask mask = AssetDatabase.LoadAssetAtPath<AvatarMask>(
                "Assets/Arena/Content/Animation/LeftGestureMask.mask");

            Assert.That(controller, Is.Not.Null);
            Assert.That(mask, Is.Not.Null);

            AnimatorControllerLayer leftGesture = controller.layers[5];
            Assert.That(leftGesture.avatarMask, Is.EqualTo(mask));

            string[] stateNames = leftGesture.stateMachine.states
                .Select(childState => childState.state.name)
                .OrderBy(name => name)
                .ToArray();
            Assert.That(stateNames, Does.Contain("Empty"));
            Assert.That(stateNames, Does.Contain("LeftGestureSpellAction1"));
            Assert.That(stateNames, Does.Contain("LeftGestureSpellAction2"));
            Assert.That(stateNames, Does.Contain("LeftGestureSpellAction3"));
            Assert.That(stateNames, Does.Contain("LeftGestureSpellAction4"));
            Assert.That(stateNames, Does.Contain("LeftGestureSpellCastHoldAction1"));
            Assert.That(stateNames, Does.Contain("LeftGestureSpellCastHoldAction4"));
        }

        [Test]
        public void AnimatorController_RightGestureLayerUsesGestureMaskAndSpellBankStates()
        {
            AnimatorController controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(
                "Assets/Arena/Content/Animation/Arena_Character.controller");
            AvatarMask mask = AssetDatabase.LoadAssetAtPath<AvatarMask>(
                "Assets/Arena/Content/Animation/RightGestureMask.mask");

            Assert.That(controller, Is.Not.Null);
            Assert.That(mask, Is.Not.Null);

            AnimatorControllerLayer rightGesture = controller.layers[6];
            Assert.That(rightGesture.avatarMask, Is.EqualTo(mask));

            string[] stateNames = rightGesture.stateMachine.states
                .Select(childState => childState.state.name)
                .OrderBy(name => name)
                .ToArray();
            Assert.That(stateNames, Does.Contain("Empty"));
            Assert.That(stateNames, Does.Contain("RightGestureSpellAction1"));
            Assert.That(stateNames, Does.Contain("RightGestureSpellAction2"));
            Assert.That(stateNames, Does.Contain("RightGestureSpellAction3"));
            Assert.That(stateNames, Does.Contain("RightGestureSpellAction4"));
            Assert.That(stateNames, Does.Contain("RightGestureSpellCastHoldAction1"));
            Assert.That(stateNames, Does.Contain("RightGestureSpellCastHoldAction4"));
        }

        [Test]
        public void AnimatorController_SpellBankStatesShareTheMirrorParameter()
        {
            AnimatorController controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(
                "Assets/Arena/Content/Animation/Arena_Character.controller");

            Assert.That(controller, Is.Not.Null);
            AnimatorControllerParameter mirrorParameter = controller.parameters.Single(
                parameter => parameter.name == "MirrorSpellAction");
            Assert.That(mirrorParameter.type, Is.EqualTo(AnimatorControllerParameterType.Bool));

            foreach (int layerIndex in new[] { 1, 4, 5, 6 })
            {
                AnimatorControllerLayer layer = controller.layers[layerIndex];
                AnimatorState[] spellStates = layer.stateMachine.states
                    .Select(childState => childState.state)
                    .Where(state => state.name.Contains("SpellAction", StringComparison.Ordinal)
                        || state.name.Contains("SpellCastHoldAction", StringComparison.Ordinal))
                    .ToArray();
                Assert.That(spellStates.Length, Is.EqualTo(8), layer.name);
                foreach (AnimatorState state in spellStates)
                {
                    Assert.That(state.mirrorParameterActive, Is.True, $"{layer.name}/{state.name}");
                    Assert.That(state.mirrorParameter, Is.EqualTo("MirrorSpellAction"), $"{layer.name}/{state.name}");
                }
            }
        }

        [Test]
        public void AnimatorController_UpperBodyHasDedicatedRecoverySlot()
        {
            AnimatorController controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(
                "Assets/Arena/Content/Animation/Arena_Character.controller");

            Assert.That(controller, Is.Not.Null);
            AnimatorControllerLayer upperBody = controller.layers[1];
            ChildAnimatorState? recoveryState = null;
            foreach (ChildAnimatorState childState in upperBody.stateMachine.states)
            {
                if (childState.state.name == "UpperBodyRecoveryAction1")
                {
                    recoveryState = childState;
                    break;
                }
            }

            Assert.That(recoveryState.HasValue, Is.True);
            Assert.That(recoveryState!.Value.state.motion, Is.Not.Null);
            Assert.That(recoveryState.Value.state.motion.name, Is.EqualTo("slot_upper_body_recovery_1"));
        }

        [Test]
        public void AnimatorController_DodgeExitsRespectGroundedAndFreeFall()
        {
            AnimatorController controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(
                "Assets/Arena/Content/Animation/Arena_Character.controller");

            Assert.That(controller, Is.Not.Null);
            AnimatorState dodge = RequireBaseState(controller, "Dodge");

            AssertHasTransition(
                dodge,
                "IdleCombat",
                Condition("InCombat", AnimatorConditionMode.If),
                Condition("Grounded", AnimatorConditionMode.If));
            AssertHasTransition(
                dodge,
                "Idle Walk Run Blend",
                Condition("InCombat", AnimatorConditionMode.IfNot),
                Condition("Grounded", AnimatorConditionMode.If));
            AssertHasTransition(
                dodge,
                "InAirCombat",
                Condition("InCombat", AnimatorConditionMode.If),
                Condition("FreeFall", AnimatorConditionMode.If));
            AssertHasTransition(
                dodge,
                "InAir",
                Condition("InCombat", AnimatorConditionMode.IfNot),
                Condition("FreeFall", AnimatorConditionMode.If));
        }

        [Test]
        public void AnimatorController_DodgeUsesAuthoritativePhaseParameter()
        {
            AnimatorController controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(
                "Assets/Arena/Content/Animation/Arena_Character.controller");

            Assert.That(controller, Is.Not.Null);
            AnimatorControllerParameter phaseParameter = controller.parameters.Single(
                parameter => parameter.name == "DodgePhase");
            AnimatorState dodge = RequireBaseState(controller, "Dodge");

            Assert.That(phaseParameter.type, Is.EqualTo(AnimatorControllerParameterType.Float));
            Assert.That(dodge.timeParameterActive, Is.True);
            Assert.That(dodge.timeParameter, Is.EqualTo("DodgePhase"));
        }

        [TestCase(900L, 1000L, 1250L, 1470L, 0f, -1f, 1f, 0f)]
        [TestCase(1000L, 1000L, 1250L, 1470L, 0f, -1f, 1f, 0f)]
        [TestCase(1125L, 1000L, 1250L, 1470L, 0f, -1f, 1f, 0.26595745f)]
        [TestCase(1250L, 1000L, 1250L, 1470L, 0f, -1f, 1f, 0.5319149f)]
        [TestCase(1360L, 1000L, 1250L, 1470L, 0f, -1f, 1f, 0.6419149f)]
        [TestCase(1470L, 1000L, 1250L, 1470L, 0f, -1f, 1f, 0.7519149f)]
        [TestCase(1719L, 1000L, 1250L, 1470L, 0f, -1f, 1f, 1f)]
        [TestCase(1000L, 1000L, 1000L, 1000L, 0f, -1f, 1f, 1f)]
        [TestCase(1000L, 1000L, 1250L, 1470L, 0.1f, 0.65f, 1f, 0.1f)]
        [TestCase(1125L, 1000L, 1250L, 1470L, 0.1f, 0.65f, 1f, 0.375f)]
        [TestCase(1250L, 1000L, 1250L, 1470L, 0.1f, 0.65f, 1f, 0.65f)]
        [TestCase(1360L, 1000L, 1250L, 1470L, 0.1f, 0.65f, 1f, 0.76f)]
        [TestCase(1470L, 1000L, 1250L, 1470L, 0.1f, 0.65f, 1f, 0.87f)]
        [TestCase(1600L, 1000L, 1250L, 1470L, 0.1f, 0.65f, 1f, 1f)]
        [TestCase(1470L, 1000L, 1250L, 1470L, 0.1f, 0.65f, 2f, 0.76f)]
        public void PlayerAnimator_DodgePhaseSynchronizesTravelThenPlaysRecoveryAtAuthoredSpeed(
            long nowMs,
            long startedAtMs,
            long activeUntilMs,
            long recoveryUntilMs,
            float startNormalized,
            float travelEndNormalized,
            float clipLengthSeconds,
            float expectedPhase)
        {
            MethodInfo method = RequireMethod(
                RequireRuntimeType("Arena.Presentation.PlayerAnimator"),
                "ResolveMovementActionPhase",
                typeof(long),
                typeof(long),
                typeof(long),
                typeof(long),
                typeof(float),
                typeof(float),
                typeof(float));

            float phase = (float)method.Invoke(
                null,
                new object[]
                {
                    nowMs,
                    startedAtMs,
                    activeUntilMs,
                    recoveryUntilMs,
                    startNormalized,
                    travelEndNormalized,
                    clipLengthSeconds,
                })!;

            Assert.That(phase, Is.EqualTo(expectedPhase).Within(0.0001f));
        }

        [Test]
        public void PlayerAnimator_ForcedActionClearCancelsPendingActionTriggersBeforeAnimatorEvaluation()
        {
            (GameObject root, Animator animator, Component playerAnimator) = CreatePlayerAnimatorHarness();
            try
            {
                animator.SetTrigger("TriggerStrike1");
                animator.SetTrigger("TriggerSpellAction1");

                RequireMethod(
                        playerAnimator.GetType(),
                        "ClearCombatActionPresentation",
                        typeof(bool),
                        typeof(bool))
                    .Invoke(playerAnimator, new object[] { false, false });
                animator.Update(0f);

                AssertActionLayersEmpty(animator);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void PlayerAnimator_HardCrowdControlAndKnockdownClearHigherActionLayers()
        {
            (GameObject root, Animator animator, Component playerAnimator) = CreatePlayerAnimatorHarness();
            try
            {
                PlayAllActionLayers(animator);
                RequireMethod(playerAnimator.GetType(), "SetHardCrowdControl", typeof(string))
                    .Invoke(playerAnimator, new object?[] { "STUN" });
                animator.Update(0f);
                AssertActionLayersEmpty(animator);

                RequireMethod(playerAnimator.GetType(), "SetHardCrowdControl", typeof(string))
                    .Invoke(playerAnimator, new object?[] { null });
                animator.Update(0f);

                PlayAllActionLayers(animator);
                RequireMethod(playerAnimator.GetType(), "SetKnockedDown", typeof(bool))
                    .Invoke(playerAnimator, new object[] { true });
                animator.Update(0f);
                AssertActionLayersEmpty(animator);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void CombatActionPlaybackController_ActiveBaseCategoryClearsByOwner()
        {
            Type playbackControllerType = RequireRuntimeType("Arena.Presentation.CombatActionPlaybackController");
            Type categoryType = RequireRuntimeType("Arena.Presentation.CombatAnimationCategory");
            object controller = Activator.CreateInstance(playbackControllerType)!;
            PropertyInfo activeCategory = playbackControllerType.GetProperty(
                                              "ActiveBaseCombatAnimationCategory",
                                              BindingFlags.Public | BindingFlags.Instance)
                                          ?? throw new AssertionException("Missing ActiveBaseCombatAnimationCategory");
            MethodInfo clearMatching = RequireMethod(
                playbackControllerType,
                "ClearActiveBaseCombatAnimationCategoryIf",
                categoryType);
            MethodInfo clearMelee = RequireMethod(playbackControllerType, "ClearActiveMeleeBaseCategory");

            object meleeSkill = Enum.Parse(categoryType, "MeleeSkill");
            object autoAttack = Enum.Parse(categoryType, "AutoAttack");
            object spell = Enum.Parse(categoryType, "Spell");

            activeCategory.SetValue(controller, spell);
            Assert.That((bool)clearMelee.Invoke(controller, Array.Empty<object>())!, Is.False);
            Assert.That(activeCategory.GetValue(controller), Is.EqualTo(spell));
            Assert.That((bool)clearMatching.Invoke(controller, new[] { autoAttack })!, Is.False);
            Assert.That((bool)clearMatching.Invoke(controller, new[] { spell })!, Is.True);
            Assert.That(activeCategory.GetValue(controller), Is.Null);

            activeCategory.SetValue(controller, meleeSkill);
            Assert.That((bool)clearMelee.Invoke(controller, Array.Empty<object>())!, Is.True);
            Assert.That(activeCategory.GetValue(controller), Is.Null);

            activeCategory.SetValue(controller, autoAttack);
            Assert.That((bool)clearMelee.Invoke(controller, Array.Empty<object>())!, Is.True);
            Assert.That(activeCategory.GetValue(controller), Is.Null);

            activeCategory.SetValue(controller, spell);
            Assert.That((bool)clearMelee.Invoke(controller, Array.Empty<object>())!, Is.False);
            Assert.That(activeCategory.GetValue(controller), Is.EqualTo(spell));
        }

        [Test]
        public void CombatActionPlaybackController_CreateSpellPresentationUsesStampedSpellEvents()
        {
            Type playbackControllerType = RequireRuntimeType("Arena.Presentation.CombatActionPlaybackController");
            Type spellEntryType = RequireRuntimeType("Arena.Presentation.WeaponSpellAnimationEntry");
            object spellEntry = Activator.CreateInstance(spellEntryType)!;
            AnimationClip clip = CreateOneSecondClip();

            try
            {
                RequireField(spellEntryType, "clip").SetValue(spellEntry, clip);
                SetClipEvents(
                    clip,
                    ("OnLowerBodyUnlock", 0.4f),
                    ("OnVisualInterruptible", 0.6f));

                object presentation = RequireMethod(
                        playbackControllerType,
                        "CreateSpellPresentation",
                        typeof(string),
                        typeof(int),
                        spellEntryType)
                    .Invoke(null, new[] { "FIREBALL", 3, spellEntry })!;

                Assert.That(RequireField(presentation.GetType(), "ActionId").GetValue(presentation), Is.EqualTo("FIREBALL"));
                Assert.That(RequireField(presentation.GetType(), "BankSlot").GetValue(presentation), Is.EqualTo(3));
                Assert.That((float)RequireField(presentation.GetType(), "LowerBodyUnlockAtSeconds").GetValue(presentation)!, Is.EqualTo(0.4f).Within(0.001f));
                Assert.That((float)RequireField(presentation.GetType(), "LowerBodyBlendOutSeconds").GetValue(presentation)!, Is.EqualTo(0.12f).Within(0.001f));
                Assert.That((float)RequireField(presentation.GetType(), "VisualInterruptibleAtSeconds").GetValue(presentation)!, Is.EqualTo(0.6f).Within(0.001f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(clip);
            }
        }

        [Test]
        public void CombatActionPlaybackController_LowerBodyUnlockStateBlendsAndResets()
        {
            Type playbackControllerType = RequireRuntimeType("Arena.Presentation.CombatActionPlaybackController");
            object controller = Activator.CreateInstance(playbackControllerType)!;

            MethodInfo markMelee = RequireMethod(
                playbackControllerType,
                "MarkMeleeLowerBodyUnlocked",
                typeof(float),
                typeof(float),
                typeof(float));
            MethodInfo resolveMeleeWeight = RequireMethod(
                playbackControllerType,
                "ResolveMeleeLowerBodyLayerWeight",
                typeof(float));
            MethodInfo resetMelee = RequireMethod(
                playbackControllerType,
                "ResetMeleeLowerBodyUnlock",
                typeof(bool));

            Assert.That((float)resolveMeleeWeight.Invoke(controller, new object[] { 0f })!, Is.EqualTo(1f));

            markMelee.Invoke(controller, new object[] { 10f, 2f, 0.75f });
            Assert.That((float)resolveMeleeWeight.Invoke(controller, new object[] { 10f })!, Is.EqualTo(0.75f).Within(0.001f));
            Assert.That((float)resolveMeleeWeight.Invoke(controller, new object[] { 11f })!, Is.EqualTo(0.375f).Within(0.001f));
            Assert.That((float)resolveMeleeWeight.Invoke(controller, new object[] { 12.5f })!, Is.EqualTo(0f).Within(0.001f));
            Assert.That((bool)resetMelee.Invoke(controller, new object[] { true })!, Is.True);
            Assert.That((float)resolveMeleeWeight.Invoke(controller, new object[] { 13f })!, Is.EqualTo(1f));

            MethodInfo markSpell = RequireMethod(
                playbackControllerType,
                "MarkSpellLowerBodyUnlocked",
                typeof(float),
                typeof(float),
                typeof(float));
            MethodInfo resetSpell = RequireMethod(
                playbackControllerType,
                "ResetSpellLowerBodyUnlock",
                typeof(bool));

            markSpell.Invoke(controller, new object[] { 20f, 0f, 0.5f });
            Assert.That((bool)resetSpell.Invoke(controller, new object[] { true })!, Is.True);
            Assert.That((bool)resetSpell.Invoke(controller, new object[] { true })!, Is.False);
        }

        [Test]
        public void CombatActionPlaybackController_PhasedMeleeStateTracksClipsTimingAndCancel()
        {
            Type playbackControllerType = RequireRuntimeType("Arena.Presentation.CombatActionPlaybackController");
            Type phaseType = RequireRuntimeType("Arena.Presentation.PhasedMeleePlaybackPhase");
            object controller = Activator.CreateInstance(playbackControllerType)!;
            AnimationClip start = CreateOneSecondClip();
            AnimationClip loop = CreateOneSecondClip();
            AnimationClip end = CreateOneSecondClip();

            try
            {
                RequireMethod(
                        playbackControllerType,
                        "BeginPhasedMelee",
                        typeof(int),
                        typeof(AnimationClip),
                        typeof(AnimationClip),
                        typeof(AnimationClip),
                        typeof(bool),
                        typeof(bool))
                    .Invoke(controller, new object[] { 3, start, loop, end, false, false });

                Assert.That(RequireProperty(playbackControllerType, "IsPhasedMeleeActive").GetValue(controller), Is.EqualTo(true));
                Assert.That(RequireProperty(playbackControllerType, "PhasedMeleeBankSlot").GetValue(controller), Is.EqualTo(3));
                Assert.That((float)RequireProperty(playbackControllerType, "PhasedMeleeTotalLengthSeconds").GetValue(controller)!, Is.EqualTo(3f).Within(0.001f));

                object startPhase = Enum.Parse(phaseType, "Start");
                object loopPhase = Enum.Parse(phaseType, "Loop");
                MethodInfo getClip = RequireMethod(playbackControllerType, "GetPhasedMeleeClip", phaseType);
                Assert.That(getClip.Invoke(controller, new[] { startPhase }), Is.SameAs(start));
                Assert.That(getClip.Invoke(controller, new[] { loopPhase }), Is.SameAs(loop));

                RequireMethod(playbackControllerType, "SetPhasedMeleeSegment", phaseType, typeof(int), typeof(float))
                    .Invoke(controller, new object[] { startPhase, 123, 1f });
                RequireMethod(playbackControllerType, "AddCompletedPhasedMeleePhaseSeconds", typeof(float))
                    .Invoke(controller, new object[] { 0.5f });

                MethodInfo timing = RequireMethod(
                    playbackControllerType,
                    "TryGetPhasedMeleePresentationTiming",
                    typeof(float),
                    typeof(float).MakeByRefType(),
                    typeof(float).MakeByRefType());
                object?[] timingArgs = { 0.25f, 0f, 0f };
                Assert.That((bool)timing.Invoke(controller, timingArgs)!, Is.True);
                Assert.That((float)timingArgs[1]!, Is.EqualTo(0.75f).Within(0.001f));
                Assert.That((float)timingArgs[2]!, Is.EqualTo(3f).Within(0.001f));

                MethodInfo transition = RequireMethod(
                    playbackControllerType,
                    "TryResolvePhasedMeleeTransition",
                    typeof(float),
                    typeof(float),
                    typeof(float),
                    typeof(float),
                    phaseType.MakeByRefType(),
                    typeof(bool).MakeByRefType());
                object nonePhase = Enum.Parse(phaseType, "None");
                object?[] transitionArgs = { 0.7f, 0.55f, 0.65f, 0.95f, nonePhase, false };
                Assert.That((bool)transition.Invoke(controller, transitionArgs)!, Is.True);
                Assert.That(transitionArgs[4]!.ToString(), Is.EqualTo("Loop"));
                Assert.That((bool)transitionArgs[5]!, Is.False);

                object endPhase = Enum.Parse(phaseType, "End");
                RequireMethod(playbackControllerType, "SetPhasedMeleeSegment", phaseType, typeof(int), typeof(float))
                    .Invoke(controller, new object[] { endPhase, 456, 1f });
                transitionArgs = new object?[] { 1f, 0.55f, 0.65f, 0.95f, nonePhase, false };
                Assert.That((bool)transition.Invoke(controller, transitionArgs)!, Is.True);
                Assert.That((bool)transitionArgs[5]!, Is.True);

                Assert.That((bool)RequireMethod(playbackControllerType, "CancelPhasedMelee").Invoke(controller, Array.Empty<object>())!, Is.True);
                Assert.That(RequireProperty(playbackControllerType, "IsPhasedMeleeActive").GetValue(controller), Is.EqualTo(false));
                Assert.That((bool)RequireMethod(playbackControllerType, "CancelPhasedMelee").Invoke(controller, Array.Empty<object>())!, Is.False);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(start);
                UnityEngine.Object.DestroyImmediate(loop);
                UnityEngine.Object.DestroyImmediate(end);
            }
        }

        [Test]
        public void CombatActionPlaybackController_PhasedMeleeStartUsesPhaseLoopReadyMarker()
        {
            Type playbackControllerType = RequireRuntimeType("Arena.Presentation.CombatActionPlaybackController");
            Type phaseType = RequireRuntimeType("Arena.Presentation.PhasedMeleePlaybackPhase");
            object controller = Activator.CreateInstance(playbackControllerType)!;
            AnimationClip start = CreateOneSecondClip();
            AnimationClip loop = CreateOneSecondClip();
            AnimationClip end = CreateOneSecondClip();

            try
            {
                SetClipEvents(start, ("OnPhaseLoopReady", 0.25f));
                RequireMethod(
                        playbackControllerType,
                        "BeginPhasedMelee",
                        typeof(int),
                        typeof(AnimationClip),
                        typeof(AnimationClip),
                        typeof(AnimationClip),
                        typeof(bool),
                        typeof(bool))
                    .Invoke(controller, new object[] { 1, start, loop, end, false, false });

                Assert.That(
                    (float)RequireProperty(
                            playbackControllerType,
                            "PhasedMeleeTotalLengthSeconds")
                        .GetValue(controller)!,
                    Is.EqualTo(2.25f).Within(0.001f));
                Assert.That(
                    (float)RequireMethod(
                            playbackControllerType,
                            "ResolvePhasedMeleeStartExitNormalizedTime",
                            typeof(float),
                            typeof(float))
                        .Invoke(controller, new object[] { 0.55f, 0.65f })!,
                    Is.EqualTo(0.25f).Within(0.001f));

                object startPhase = Enum.Parse(phaseType, "Start");
                RequireMethod(
                        playbackControllerType,
                        "SetPhasedMeleeSegment",
                        phaseType,
                        typeof(int),
                        typeof(float))
                    .Invoke(controller, new object[] { startPhase, 123, 1f });

                MethodInfo transition = RequireMethod(
                    playbackControllerType,
                    "TryResolvePhasedMeleeTransition",
                    typeof(float),
                    typeof(float),
                    typeof(float),
                    typeof(float),
                    phaseType.MakeByRefType(),
                    typeof(bool).MakeByRefType());
                object nonePhase = Enum.Parse(phaseType, "None");
                object?[] beforeMarker = { 0.24f, 0.55f, 0.65f, 0.95f, nonePhase, false };
                Assert.That((bool)transition.Invoke(controller, beforeMarker)!, Is.False);

                object?[] atMarker = { 0.25f, 0.55f, 0.65f, 0.95f, nonePhase, false };
                Assert.That((bool)transition.Invoke(controller, atMarker)!, Is.True);
                Assert.That(atMarker[4]!.ToString(), Is.EqualTo("Loop"));
                Assert.That((bool)atMarker[5]!, Is.False);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(start);
                UnityEngine.Object.DestroyImmediate(loop);
                UnityEngine.Object.DestroyImmediate(end);
            }
        }

        [Test]
        public void CombatActionPlaybackController_PhaseLoopReadyCannotOutrunStrikeStateSafetyExit()
        {
            Type playbackControllerType = RequireRuntimeType("Arena.Presentation.CombatActionPlaybackController");
            object controller = Activator.CreateInstance(playbackControllerType)!;
            AnimationClip start = CreateOneSecondClip();
            AnimationClip loop = CreateOneSecondClip();
            AnimationClip end = CreateOneSecondClip();

            try
            {
                SetClipEvents(start, ("OnPhaseLoopReady", 0.95f));
                RequireMethod(
                        playbackControllerType,
                        "BeginPhasedMelee",
                        typeof(int),
                        typeof(AnimationClip),
                        typeof(AnimationClip),
                        typeof(AnimationClip),
                        typeof(bool),
                        typeof(bool))
                    .Invoke(controller, new object[] { 1, start, loop, end, false, false });

                Assert.That(
                    (float)RequireProperty(
                            playbackControllerType,
                            "PhasedMeleeTotalLengthSeconds")
                        .GetValue(controller)!,
                    Is.EqualTo(2.84f).Within(0.001f));
                Assert.That(
                    (float)RequireMethod(
                            playbackControllerType,
                            "ResolvePhasedMeleeStartExitNormalizedTime",
                            typeof(float),
                            typeof(float))
                        .Invoke(controller, new object[] { 0.82f, 0.84f })!,
                    Is.EqualTo(0.84f).Within(0.001f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(start);
                UnityEngine.Object.DestroyImmediate(loop);
                UnityEngine.Object.DestroyImmediate(end);
            }
        }

        [Test]
        public void DecideCombatAnimationRequest_NeverLetsAutoAttackPreemptAbilities()
        {
            Assert.That(
                InvokeCombatAnimationDecision(
                    incomingCategory: "AutoAttack",
                    isHigherPriorityActive: true,
                    isSpellActive: false,
                    isMeleeActive: true,
                    isComboFollowUp: false,
                    isAutoAttackSequenceRestart: false,
                    activeMeleeIsPhased: false,
                    visualGateEvaluated: true,
                    visualDecision: "SuppressIncomingWithGhost").ToString(),
                Is.EqualTo("DropAsLowerPriority"));

            Assert.That(
                InvokeCombatAnimationDecision(
                    incomingCategory: "MeleeSkill",
                    isHigherPriorityActive: true,
                    isSpellActive: false,
                    isMeleeActive: true,
                    isComboFollowUp: true,
                    isAutoAttackSequenceRestart: false,
                    activeMeleeIsPhased: false,
                    visualGateEvaluated: false,
                    visualDecision: "PreserveExistingBehavior").ToString(),
                Is.EqualTo("HandoffComboFollowUpAndPlay"));

            Assert.That(
                InvokeCombatAnimationDecision(
                    incomingCategory: "AutoAttack",
                    isHigherPriorityActive: true,
                    isSpellActive: false,
                    isMeleeActive: true,
                    isComboFollowUp: true,
                    isAutoAttackSequenceRestart: false,
                    activeMeleeIsPhased: false,
                    visualGateEvaluated: false,
                    visualDecision: "PreserveExistingBehavior").ToString(),
                Is.EqualTo("HandoffComboFollowUpAndPlay"));

            Assert.That(
                InvokeCombatAnimationDecision(
                    incomingCategory: "AutoAttack",
                    isHigherPriorityActive: true,
                    isSpellActive: false,
                    isMeleeActive: true,
                    isComboFollowUp: false,
                    isAutoAttackSequenceRestart: true,
                    activeMeleeIsPhased: false,
                    visualGateEvaluated: false,
                    visualDecision: "PreserveExistingBehavior").ToString(),
                Is.EqualTo("HandoffComboFollowUpAndPlay"));

            Assert.That(
                InvokeCombatAnimationDecision(
                    incomingCategory: "AutoAttack",
                    isHigherPriorityActive: true,
                    isSpellActive: false,
                    isMeleeActive: true,
                    isComboFollowUp: false,
                    isAutoAttackSequenceRestart: false,
                    activeMeleeIsPhased: false,
                    visualGateEvaluated: true,
                    visualDecision: "InterruptCurrentWithoutGhost").ToString(),
                Is.EqualTo("DropAsLowerPriority"));

            Assert.That(
                InvokeCombatAnimationDecision(
                    incomingCategory: "MeleeSkill",
                    isHigherPriorityActive: false,
                    isSpellActive: false,
                    isMeleeActive: true,
                    isComboFollowUp: false,
                    isAutoAttackSequenceRestart: false,
                    activeMeleeIsPhased: false,
                    visualGateEvaluated: true,
                    visualDecision: "InterruptCurrentWithoutGhost").ToString(),
                Is.EqualTo("InterruptCurrentWithoutGhostAndPlay"));

            Assert.That(
                InvokeCombatAnimationDecision(
                    incomingCategory: "AutoAttack",
                    isHigherPriorityActive: true,
                    isSpellActive: true,
                    isMeleeActive: false,
                    isComboFollowUp: false,
                    isAutoAttackSequenceRestart: false,
                    activeMeleeIsPhased: false,
                    visualGateEvaluated: false,
                    visualDecision: "PreserveExistingBehavior").ToString(),
                Is.EqualTo("DropAsLowerPriority"));

            Assert.That(
                InvokeCombatAnimationDecision(
                    incomingCategory: "AutoAttack",
                    isHigherPriorityActive: true,
                    isSpellActive: false,
                    isMeleeActive: true,
                    isComboFollowUp: false,
                    isAutoAttackSequenceRestart: false,
                    activeMeleeIsPhased: true,
                    visualGateEvaluated: true,
                    visualDecision: "InterruptCurrentWithoutGhost").ToString(),
                Is.EqualTo("DropAsLowerPriority"));

            Assert.That(
                InvokeCombatAnimationDecision(
                    incomingCategory: "Spell",
                    isHigherPriorityActive: true,
                    isSpellActive: false,
                    isMeleeActive: true,
                    isComboFollowUp: false,
                    isAutoAttackSequenceRestart: false,
                    activeMeleeIsPhased: false,
                    visualGateEvaluated: true,
                    visualDecision: "InterruptCurrentWithoutGhost").ToString(),
                Is.EqualTo("InterruptCurrentWithoutGhostAndPlay"));
        }

        [Test]
        public void HasTrackedHigherPriorityPresentation_ClosesPreAnimatorAutoAttackWindow()
        {
            Assert.That(
                InvokeHasTrackedHigherPriorityPresentation(
                    hasActiveMeleePresentation: true,
                    activeMeleeCategory: "MeleeSkill",
                    hasActiveSpellPresentation: false,
                    hasActiveSpellCastHoldPresentation: false),
                Is.True);
            Assert.That(
                InvokeHasTrackedHigherPriorityPresentation(
                    hasActiveMeleePresentation: true,
                    activeMeleeCategory: "AutoAttack",
                    hasActiveSpellPresentation: false,
                    hasActiveSpellCastHoldPresentation: false),
                Is.False);
            Assert.That(
                InvokeHasTrackedHigherPriorityPresentation(
                    hasActiveMeleePresentation: false,
                    activeMeleeCategory: "AutoAttack",
                    hasActiveSpellPresentation: true,
                    hasActiveSpellCastHoldPresentation: false),
                Is.True);
            Assert.That(
                InvokeHasTrackedHigherPriorityPresentation(
                    hasActiveMeleePresentation: false,
                    activeMeleeCategory: "AutoAttack",
                    hasActiveSpellPresentation: false,
                    hasActiveSpellCastHoldPresentation: true),
                Is.True);
            Assert.That(
                InvokeHasTrackedHigherPriorityPresentation(
                    hasActiveMeleePresentation: false,
                    activeMeleeCategory: "AutoAttack",
                    hasActiveSpellPresentation: false,
                    hasActiveSpellCastHoldPresentation: false),
                Is.False);
        }

        [Test]
        public void HasEnteredExpectedAnimatorState_RejectsOutgoingAndDispatchFrameStates()
        {
            const int emptyState = 10;
            const int outgoingStrikeState = 20;
            const int expectedStrikeState = 40;
            const int dispatchedFrame = 100;

            Assert.That(
                InvokeHasEnteredExpectedAnimatorState(
                    dispatchedFrame,
                    currentFrame: dispatchedFrame,
                    expectedStateHash: expectedStrikeState,
                    currentStateHash: expectedStrikeState,
                    isInTransition: false,
                    nextStateHash: 0),
                Is.False,
                "A same-bank outgoing state must not count during the incoming presentation's dispatch frame.");
            Assert.That(
                InvokeHasEnteredExpectedAnimatorState(
                    dispatchedFrame,
                    currentFrame: dispatchedFrame + 1,
                    expectedStateHash: expectedStrikeState,
                    currentStateHash: outgoingStrikeState,
                    isInTransition: false,
                    nextStateHash: 0),
                Is.False,
                "A different outgoing strike must not count as entry for the incoming presentation.");
            Assert.That(
                InvokeHasEnteredExpectedAnimatorState(
                    dispatchedFrame,
                    currentFrame: dispatchedFrame + 1,
                    expectedStateHash: expectedStrikeState,
                    currentStateHash: emptyState,
                    isInTransition: false,
                    nextStateHash: 0),
                Is.False,
                "The intermediate Empty frame must preserve the pending presentation.");
            Assert.That(
                InvokeHasEnteredExpectedAnimatorState(
                    dispatchedFrame,
                    currentFrame: dispatchedFrame + 2,
                    expectedStateHash: expectedStrikeState,
                    currentStateHash: emptyState,
                    isInTransition: true,
                    nextStateHash: expectedStrikeState),
                Is.True,
                "The incoming transition is the first valid entry observation.");
            Assert.That(
                InvokeHasEnteredExpectedAnimatorState(
                    dispatchedFrame,
                    currentFrame: dispatchedFrame + 2,
                    expectedStateHash: expectedStrikeState,
                    currentStateHash: expectedStrikeState,
                    isInTransition: false,
                    nextStateHash: 0),
                Is.True);
            Assert.That(
                InvokeHasEnteredExpectedAnimatorState(
                    dispatchedFrame,
                    currentFrame: dispatchedFrame + 2,
                    expectedStateHash: 0,
                    currentStateHash: 0,
                    isInTransition: false,
                    nextStateHash: 0),
                Is.False);
        }

        [Test]
        public void ResolvePreemptionMode_MapsDecisionsToExecutionModes()
        {
            Assert.That(
                InvokePreemptionMode("DropAsLowerPriority", "AutoAttack").ToString(),
                Is.EqualTo("SuppressIncomingWithGhost"));
            Assert.That(
                InvokePreemptionMode("InterruptCurrentAndPlay", "MeleeSkill").ToString(),
                Is.EqualTo("InterruptWithGhost"));
            Assert.That(
                InvokePreemptionMode("InterruptCurrentAndPlay", "AutoAttack").ToString(),
                Is.EqualTo("None"));
            Assert.That(
                InvokePreemptionMode("InterruptCurrentWithoutGhostAndPlay", "Spell").ToString(),
                Is.EqualTo("InterruptWithoutGhost"));
            Assert.That(
                InvokePreemptionMode("HandoffComboFollowUpAndPlay", "MeleeSkill").ToString(),
                Is.EqualTo("HandoffComboFollowUp"));
        }

        [Test]
        public void CanCaptureSuppressedAutoAttackGhost_RequiresAutoAttackFacingTarget()
        {
            Assert.That(
                InvokeCanCaptureSuppressedAutoAttackGhost("AutoAttack", true, out string skipReason),
                Is.True);
            Assert.That(skipReason, Is.EqualTo(string.Empty));

            Assert.That(
                InvokeCanCaptureSuppressedAutoAttackGhost("MeleeSkill", true, out skipReason),
                Is.False);
            Assert.That(skipReason, Is.EqualTo("wrong-category"));

            Assert.That(
                InvokeCanCaptureSuppressedAutoAttackGhost("AutoAttack", false, out skipReason),
                Is.False);
            Assert.That(skipReason, Is.EqualTo("no-facing-target"));
        }

        [Test]
        public void ResolveBankedAnimatorRouting_MapsBankSlotsAndPhasedSegments()
        {
            Type playbackControllerType = RequireRuntimeType("Arena.Presentation.CombatActionPlaybackController");
            Type phaseType = RequireRuntimeType("Arena.Presentation.PhasedMeleePlaybackPhase");
            MethodInfo bankedHash = RequireMethod(
                playbackControllerType,
                "ResolveBankedAnimatorHash",
                typeof(int),
                typeof(int),
                typeof(int),
                typeof(int),
                typeof(int));
            Assert.That((int)bankedHash.Invoke(null, new object[] { 1, 10, 20, 30, 40 })!, Is.EqualTo(10));
            Assert.That((int)bankedHash.Invoke(null, new object[] { 4, 10, 20, 30, 40 })!, Is.EqualTo(40));
            Assert.That((int)bankedHash.Invoke(null, new object[] { 99, 10, 20, 30, 40 })!, Is.EqualTo(10));

            MethodInfo phasedRoute = RequireMethod(
                playbackControllerType,
                "TryResolvePhasedMeleeLayerRoute",
                typeof(int),
                phaseType,
                typeof(int),
                typeof(int),
                typeof(int),
                typeof(int),
                typeof(int).MakeByRefType(),
                typeof(int).MakeByRefType());

            object endPhase = Enum.Parse(phaseType, "End");
            object?[] args = { 3, endPhase, 101, 102, 103, 104, 0, 0 };
            Assert.That((bool)phasedRoute.Invoke(null, args)!, Is.True);
            Assert.That((int)args[6]!, Is.EqualTo(1));
            Assert.That((int)args[7]!, Is.EqualTo(101));
        }

        [Test]
        public void DecideVisualInterrupt_SuppressesAutoAttackBeforeThreshold()
        {
            object result = InvokeVisualInterruptDecision(
                activeCategory: "MeleeSkill",
                incomingCategory: "AutoAttack",
                activeIsPhased: false,
                activeElapsedSeconds: 0.25f,
                activeVisualInterruptibleAtSeconds: 0.50f);

            Assert.That(result.ToString(), Is.EqualTo("SuppressIncomingWithGhost"));
        }

        [Test]
        public void DecideVisualInterrupt_SuppressesAutoAttackAfterThreshold()
        {
            object result = InvokeVisualInterruptDecision(
                activeCategory: "MeleeSkill",
                incomingCategory: "AutoAttack",
                activeIsPhased: false,
                activeElapsedSeconds: 0.50f,
                activeVisualInterruptibleAtSeconds: 0.50f);

            Assert.That(result.ToString(), Is.EqualTo("SuppressIncomingWithGhost"));
        }

        [Test]
        public void DecideVisualInterrupt_CapturesAutoAttackGhostBeforeThreshold()
        {
            object result = InvokeVisualInterruptDecision(
                activeCategory: "AutoAttack",
                incomingCategory: "MeleeSkill",
                activeIsPhased: false,
                activeElapsedSeconds: 0.25f,
                activeVisualInterruptibleAtSeconds: 0.50f);

            Assert.That(result.ToString(), Is.EqualTo("InterruptCurrentWithGhost"));
        }

        [Test]
        public void DecideVisualInterrupt_PhasedMeleeUsesSameThresholdPolicy()
        {
            object beforeThreshold = InvokeVisualInterruptDecision(
                activeCategory: "MeleeSkill",
                incomingCategory: "AutoAttack",
                activeIsPhased: true,
                activeElapsedSeconds: 0.25f,
                activeVisualInterruptibleAtSeconds: 0.50f);
            object afterThreshold = InvokeVisualInterruptDecision(
                activeCategory: "MeleeSkill",
                incomingCategory: "MeleeSkill",
                activeIsPhased: true,
                activeElapsedSeconds: 0.50f,
                activeVisualInterruptibleAtSeconds: 0.50f);

            Assert.That(beforeThreshold.ToString(), Is.EqualTo("SuppressIncomingWithGhost"));
            Assert.That(afterThreshold.ToString(), Is.EqualTo("InterruptCurrentWithoutGhost"));
        }

        [Test]
        public void IsComboFollowUp_ReturnsTrueWhenComboFromMatchesActiveAuthoredId()
        {
            object active = MakeStrikeCombat(id: "COMBO_ATTACK_1_1_HIGH_TO_LOW", comboFrom: string.Empty);
            object incoming = MakeStrikeCombat(id: "COMBO_ATTACK_1_2_LOW_TO_HIGH", comboFrom: "COMBO_ATTACK_1_1_HIGH_TO_LOW");

            Assert.That(InvokeIsComboFollowUp(active, incoming), Is.True);
        }

        [Test]
        public void IsComboFollowUp_IsCaseInsensitive()
        {
            object active = MakeStrikeCombat(id: "COMBO_ATTACK_1_1_HIGH_TO_LOW", comboFrom: string.Empty);
            object incoming = MakeStrikeCombat(id: "COMBO_ATTACK_1_2_LOW_TO_HIGH", comboFrom: "combo_attack_1_1_high_to_low");

            Assert.That(InvokeIsComboFollowUp(active, incoming), Is.True);
        }

        [Test]
        public void IsComboFollowUp_ReturnsFalseWhenIncomingHasNoComboFrom()
        {
            object active = MakeStrikeCombat(id: "COMBO_ATTACK_1_1_HIGH_TO_LOW", comboFrom: string.Empty);
            object incoming = MakeStrikeCombat(id: "WARRIOR_WHIRLWIND", comboFrom: string.Empty);

            Assert.That(InvokeIsComboFollowUp(active, incoming), Is.False);
        }

        [Test]
        public void IsComboFollowUp_ReturnsFalseWhenComboFromPointsAtDifferentStrike()
        {
            object active = MakeStrikeCombat(id: "COMBO_ATTACK_1_1_HIGH_TO_LOW", comboFrom: string.Empty);
            object incoming = MakeStrikeCombat(id: "COMBO_ATTACK_1_3_GROUND_TO_AIR", comboFrom: "COMBO_ATTACK_1_2_LOW_TO_HIGH");

            Assert.That(InvokeIsComboFollowUp(active, incoming), Is.False);
        }

        [Test]
        public void CombatAnimationSet_VisualInterruptibleReadsStampedEvent()
        {
            Type setType = RequireRuntimeType("Arena.Presentation.CombatAnimationSet");
            Type attackType = RequireRuntimeType("Arena.Presentation.WeaponMeleeAttackAuthoring");
            ScriptableObject set = ScriptableObject.CreateInstance(setType);
            AnimationClip clip = new AnimationClip();
            try
            {
                RequireMethod(setType, "EnsureMeleeAttackListSize", typeof(int)).Invoke(set, new object[] { 1 });

                clip.SetCurve(
                    relativePath: string.Empty,
                    type: typeof(Transform),
                    propertyName: "localPosition.x",
                    curve: AnimationCurve.Linear(0f, 0f, 1f, 1f));

                IList attacks = (IList)RequireField(setType, "meleeAttacks").GetValue(set)!;
                object attack = attacks[0]!;
                RequireField(attackType, "clip").SetValue(attack, clip);

                SetClipEvents(clip, ("OnVisualInterruptible", 0.38f));
                MethodInfo resolver = RequireMethod(setType, "GetVisualInterruptibleAtSeconds", typeof(int), typeof(bool));

                attacks[0] = attack;
                Assert.That((float)resolver.Invoke(set, new object[] { 1, true })!, Is.EqualTo(0.38f).Within(0.001f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(clip);
                UnityEngine.Object.DestroyImmediate(set);
            }
        }

        [Test]
        public void CombatAnimationSet_LowerBodyUnlockReadsStampedEvent()
        {
            Type setType = RequireRuntimeType("Arena.Presentation.CombatAnimationSet");
            Type attackType = RequireRuntimeType("Arena.Presentation.WeaponMeleeAttackAuthoring");
            ScriptableObject set = ScriptableObject.CreateInstance(setType);
            AnimationClip clip = new AnimationClip();
            try
            {
                RequireMethod(setType, "EnsureMeleeAttackListSize", typeof(int)).Invoke(set, new object[] { 1 });

                clip.SetCurve(
                    relativePath: string.Empty,
                    type: typeof(Transform),
                    propertyName: "localPosition.x",
                    curve: AnimationCurve.Linear(0f, 0f, 1f, 1f));

                IList attacks = (IList)RequireField(setType, "meleeAttacks").GetValue(set)!;
                object attack = attacks[0]!;
                RequireField(attackType, "clip").SetValue(attack, clip);

                SetClipEvents(clip, ("OnLowerBodyUnlock", 0.37f));
                MethodInfo resolver = RequireMethod(setType, "GetLowerBodyUnlockAtSeconds", typeof(int), typeof(bool));

                attacks[0] = attack;
                Assert.That((float)resolver.Invoke(set, new object[] { 1, true })!, Is.EqualTo(0.37f).Within(0.001f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(clip);
                UnityEngine.Object.DestroyImmediate(set);
            }
        }

        [Test]
        public void CombatAnimationSet_LowerBodyBlendOutUsesExplicitDefault()
        {
            Type setType = RequireRuntimeType("Arena.Presentation.CombatAnimationSet");
            ScriptableObject set = ScriptableObject.CreateInstance(setType);
            try
            {
                RequireMethod(setType, "EnsureMeleeAttackListSize", typeof(int)).Invoke(set, new object[] { 1 });

                MethodInfo resolver = RequireMethod(setType, "GetLowerBodyBlendOutSeconds", typeof(int), typeof(float));
                Assert.That((float)resolver.Invoke(set, new object[] { 1, 0.12f })!, Is.EqualTo(0.12f).Within(0.001f));
                Assert.That((float)resolver.Invoke(set, new object[] { 1, 0.25f })!, Is.EqualTo(0.25f).Within(0.001f));
                Assert.That((float)resolver.Invoke(set, new object[] { 1, -1f })!, Is.Zero);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(set);
            }
        }

        [Test]
        public void WeaponSpellAnimationEntry_LowerBodyAndVisualTimingUseStampedEvents()
        {
            Type spellEntryType = RequireRuntimeType("Arena.Presentation.WeaponSpellAnimationEntry");
            AnimationClip clip = new AnimationClip();
            try
            {
                clip.SetCurve(
                    relativePath: string.Empty,
                    type: typeof(Transform),
                    propertyName: "localPosition.x",
                    curve: AnimationCurve.Linear(0f, 0f, 1f, 1f));

                object entry = Activator.CreateInstance(spellEntryType)!;
                RequireField(spellEntryType, "clip").SetValue(entry, clip);
                SetClipEvents(
                    clip,
                    ("OnLowerBodyUnlock", 0.25f),
                    ("OnVisualInterruptible", 0.75f));
                MethodInfo resolver = RequireMethod(spellEntryType, "ResolveLowerBodyUnlockAtSeconds");
                MethodInfo visualResolver = RequireMethod(spellEntryType, "ResolveVisualInterruptibleAtSeconds");

                Assert.That((float)resolver.Invoke(entry, Array.Empty<object>())!, Is.EqualTo(0.25f).Within(0.001f));
                Assert.That((float)visualResolver.Invoke(entry, Array.Empty<object>())!, Is.EqualTo(0.75f).Within(0.001f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(clip);
            }
        }

        [Test]
        public void WeaponSpellAnimationEntry_InstantStartupTrimRequiresConfirmationAndStopsAtRelease()
        {
            Type spellEntryType = RequireRuntimeType("Arena.Presentation.WeaponSpellAnimationEntry");
            AnimationClip clip = CreateOneSecondClip();
            try
            {
                object entry = Activator.CreateInstance(spellEntryType)!;
                RequireField(spellEntryType, "clip").SetValue(entry, clip);
                MethodInfo resolver = RequireMethod(
                    spellEntryType,
                    "ResolveInstantCastStartupTrimSeconds",
                    typeof(bool));

                SetClipEvents(
                    clip,
                    ("OnCastReleaseEntry", 0.1f),
                    ("OnInstantCastStart", 0.2f),
                    ("OnReleaseFrame", 0.4f));

                Assert.That(
                    (float)resolver.Invoke(entry, new object[] { true })!,
                    Is.EqualTo(0.2f).Within(0.001f),
                    "confirmed Instant playback should use its own clip-authored marker, not the charged-cast receiving point");
                Assert.That(
                    (float)resolver.Invoke(entry, new object[] { false })!,
                    Is.Zero,
                    "a Charged/Channel spell sharing the same clip must retain its full opening");

                SetClipEvents(
                    clip,
                    ("OnReleaseFrame", 0.4f),
                    ("OnInstantCastStart", 0.65f));
                Assert.That(
                    (float)resolver.Invoke(entry, new object[] { true })!,
                    Is.EqualTo(0.4f).Within(0.001f),
                    "runtime must never trim past the visible release pose");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(clip);
            }
        }

        [Test]
        public void WeaponSpellAnimationEntry_CastReleaseEntryIsAClampedReceiverPoint()
        {
            Type spellEntryType = RequireRuntimeType("Arena.Presentation.WeaponSpellAnimationEntry");
            AnimationClip clip = CreateOneSecondClip();
            try
            {
                object entry = Activator.CreateInstance(spellEntryType)!;
                RequireField(spellEntryType, "clip").SetValue(entry, clip);
                MethodInfo entryResolver = RequireMethod(
                    spellEntryType,
                    "ResolveCastReleaseEntrySeconds");
                MethodInfo leadInResolver = RequireMethod(
                    spellEntryType,
                    "ResolveCastReleaseLeadInSeconds");

                SetClipEvents(
                    clip,
                    ("OnCastReleaseEntry", 0.2f),
                    ("OnReleaseFrame", 0.6f));

                Assert.That(
                    (float)entryResolver.Invoke(entry, Array.Empty<object>())!,
                    Is.EqualTo(0.2f).Within(0.001f));
                Assert.That(
                    (float)leadInResolver.Invoke(entry, Array.Empty<object>())!,
                    Is.EqualTo(0.4f).Within(0.001f),
                    "charged handoff timing should use only the receiving-point-to-release interval");

                SetClipEvents(clip, ("OnReleaseFrame", 0.6f));
                Assert.That(
                    (float)entryResolver.Invoke(entry, Array.Empty<object>())!,
                    Is.Zero,
                    "missing receiving markers must preserve legacy playback from clip start");
                Assert.That(
                    (float)leadInResolver.Invoke(entry, Array.Empty<object>())!,
                    Is.EqualTo(0.6f).Within(0.001f));

                SetClipEvents(
                    clip,
                    ("OnReleaseFrame", 0.6f),
                    ("OnCastReleaseEntry", 0.8f));
                Assert.That(
                    (float)entryResolver.Invoke(entry, Array.Empty<object>())!,
                    Is.EqualTo(0.6f).Within(0.001f),
                    "runtime must never receive the release clip after its visible release pose");
                Assert.That(
                    (float)leadInResolver.Invoke(entry, Array.Empty<object>())!,
                    Is.Zero);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(clip);
            }
        }

        [Test]
        public void SpellReleaseEventTemplates_ExposeCastReleaseEntryAsAStandardOptionalButton()
        {
            Assembly editorAssembly = AppDomain.CurrentDomain.Load("Assembly-CSharp-Editor");
            Type roleType = editorAssembly.GetType("Arena.Editor.CombatClipRole", throwOnError: true)!;
            Type templatesType = editorAssembly.GetType(
                "Arena.Editor.CombatClipEventTemplates",
                throwOnError: true)!;
            object spellReleaseRole = Enum.Parse(roleType, "SpellRelease");
            MethodInfo getTemplates = RequireMethod(templatesType, "GetTemplates", roleType);
            Array templates = (Array)getTemplates.Invoke(null, new[] { spellReleaseRole })!;
            object receiverTemplate = templates
                .Cast<object>()
                .Single(template => string.Equals(
                    RequireField(template.GetType(), "FunctionName").GetValue(template) as string,
                    "OnCastReleaseEntry",
                    StringComparison.Ordinal));

            Assert.That(
                (bool)RequireField(receiverTemplate.GetType(), "Required").GetValue(receiverTemplate)!,
                Is.False,
                "the receiving point must remain optional so legacy release clips use clip start");
        }

        [Test]
        public void WeaponSpellAnimationEntry_PropHandoffUsesRemainingReleaseDelay()
        {
            Type spellEntryType = RequireRuntimeType("Arena.Presentation.WeaponSpellAnimationEntry");
            AnimationClip clip = CreateOneSecondClip();
            try
            {
                object entry = Activator.CreateInstance(spellEntryType)!;
                RequireField(spellEntryType, "clip").SetValue(entry, clip);
                SetClipEvents(clip, ("OnReleaseFrame", 0.4f));
                MethodInfo resolver = RequireMethod(
                    spellEntryType,
                    "ResolveReleaseDelayAfterPlaybackStartSeconds",
                    typeof(float));

                Assert.That(
                    (float)resolver.Invoke(entry, new object[] { 0.15f })!,
                    Is.EqualTo(0.25f).Within(0.001f));
                Assert.That(
                    (float)resolver.Invoke(entry, new object[] { 0.5f })!,
                    Is.Zero,
                    "catch-up at or beyond release must hand the prop off immediately");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(clip);
            }
        }

        [Test]
        public void CombatAnimationRemoteTiming_InstantTrimAndCatchupRemainBeforeRelease()
        {
            ResetServerClock();
            try
            {
                long clientNowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                RecordServerClockReducerSample(
                    clientNowMs - 10L,
                    clientNowMs - 5L,
                    clientNowMs);
                object request = BuildAuthoritativeCombatAnimationRequest(
                    "FIREBALL",
                    "Spell",
                    GetServerNowMs() - 500L);

                (bool resolved, float normalizedStart, float appliedCatchupSeconds) =
                    ResolveRemoteStartNormalizedTime(
                        request,
                        isLocalPlayer: false,
                        timingReferenceLengthSeconds: 1f,
                        playedClipLengthSeconds: 1f,
                        startupTrimSeconds: 0.1f,
                        firstHitWindowSeconds: 0.15f);

                Assert.That(resolved, Is.True);
                Assert.That(appliedCatchupSeconds, Is.EqualTo(0.1f).Within(0.001f));
                Assert.That(normalizedStart, Is.EqualTo(0.2f).Within(0.001f));
            }
            finally
            {
                ResetServerClock();
            }
        }

        private static object MakeStrikeCombat(string id, string comboFrom)
        {
            Type combatType = RequireRuntimeType("Arena.Presentation.WeaponStrikeCombatAuthoring");
            object combat = Activator.CreateInstance(combatType)!;
            RequireField(combatType, "id").SetValue(combat, id);
            RequireField(combatType, "comboFrom").SetValue(combat, comboFrom);
            return combat;
        }

        private static bool InvokeIsComboFollowUp(object activeCombat, object incomingCombat)
        {
            Type playerAnimatorType = RequireRuntimeType("Arena.Presentation.PlayerAnimator");
            Type combatType = RequireRuntimeType("Arena.Presentation.WeaponStrikeCombatAuthoring");
            MethodInfo method = RequireMethod(
                playerAnimatorType,
                "IsComboFollowUp",
                combatType,
                combatType);

            return (bool)method.Invoke(null, new[] { activeCombat, incomingCombat })!;
        }

        private static object InvokeVisualInterruptDecision(
            string activeCategory,
            string incomingCategory,
            bool activeIsPhased,
            float activeElapsedSeconds,
            float activeVisualInterruptibleAtSeconds)
        {
            Type playbackControllerType = RequireRuntimeType("Arena.Presentation.CombatActionPlaybackController");
            Type categoryType = RequireRuntimeType("Arena.Presentation.CombatAnimationCategory");
            MethodInfo method = RequireMethod(
                playbackControllerType,
                "DecideVisualInterrupt",
                categoryType,
                categoryType,
                typeof(bool),
                typeof(float),
                typeof(float));

            return method.Invoke(
                null,
                new[]
                {
                    Enum.Parse(categoryType, activeCategory),
                    Enum.Parse(categoryType, incomingCategory),
                    activeIsPhased,
                    activeElapsedSeconds,
                    activeVisualInterruptibleAtSeconds,
                })!;
        }

        private static object InvokeCombatAnimationDecision(
            string incomingCategory,
            bool isHigherPriorityActive,
            bool isSpellActive,
            bool isMeleeActive,
            bool isComboFollowUp,
            bool isAutoAttackSequenceRestart,
            bool activeMeleeIsPhased,
            bool visualGateEvaluated,
            string visualDecision)
        {
            Type playbackControllerType = RequireRuntimeType("Arena.Presentation.CombatActionPlaybackController");
            Type categoryType = RequireRuntimeType("Arena.Presentation.CombatAnimationCategory");
            Type visualDecisionType = RequireRuntimeType("Arena.Presentation.CombatVisualInterruptDecision");
            MethodInfo method = RequireMethod(
                playbackControllerType,
                "DecideCombatAnimationRequest",
                categoryType,
                typeof(bool),
                typeof(bool),
                typeof(bool),
                typeof(bool),
                typeof(bool),
                typeof(bool),
                typeof(bool),
                visualDecisionType);

            return method.Invoke(
                null,
                new[]
                {
                    Enum.Parse(categoryType, incomingCategory),
                    isHigherPriorityActive,
                    isSpellActive,
                    isMeleeActive,
                    isComboFollowUp,
                    isAutoAttackSequenceRestart,
                    activeMeleeIsPhased,
                    visualGateEvaluated,
                    Enum.Parse(visualDecisionType, visualDecision),
                })!;
        }

        private static object InvokePreemptionMode(string decision, string incomingCategory)
        {
            Type playbackControllerType = RequireRuntimeType("Arena.Presentation.CombatActionPlaybackController");
            Type decisionType = RequireRuntimeType("Arena.Presentation.CombatAnimationDecision");
            Type categoryType = RequireRuntimeType("Arena.Presentation.CombatAnimationCategory");
            MethodInfo method = RequireMethod(
                playbackControllerType,
                "ResolvePreemptionMode",
                decisionType,
                categoryType);

            return method.Invoke(
                null,
                new[]
                {
                    Enum.Parse(decisionType, decision),
                    Enum.Parse(categoryType, incomingCategory),
                })!;
        }

        private static bool InvokeHasTrackedHigherPriorityPresentation(
            bool hasActiveMeleePresentation,
            string activeMeleeCategory,
            bool hasActiveSpellPresentation,
            bool hasActiveSpellCastHoldPresentation)
        {
            Type playbackControllerType = RequireRuntimeType("Arena.Presentation.CombatActionPlaybackController");
            Type categoryType = RequireRuntimeType("Arena.Presentation.CombatAnimationCategory");
            MethodInfo method = RequireMethod(
                playbackControllerType,
                "HasTrackedHigherPriorityPresentation",
                typeof(bool),
                categoryType,
                typeof(bool),
                typeof(bool));

            return (bool)method.Invoke(
                null,
                new[]
                {
                    (object)hasActiveMeleePresentation,
                    Enum.Parse(categoryType, activeMeleeCategory),
                    hasActiveSpellPresentation,
                    hasActiveSpellCastHoldPresentation,
                })!;
        }

        private static bool InvokeHasEnteredExpectedAnimatorState(
            int dispatchedFrame,
            int currentFrame,
            int expectedStateHash,
            int currentStateHash,
            bool isInTransition,
            int nextStateHash)
        {
            Type playbackControllerType = RequireRuntimeType("Arena.Presentation.CombatActionPlaybackController");
            MethodInfo method = RequireMethod(
                playbackControllerType,
                "HasEnteredExpectedAnimatorState",
                typeof(int),
                typeof(int),
                typeof(int),
                typeof(int),
                typeof(bool),
                typeof(int));

            return (bool)method.Invoke(
                null,
                new object[]
                {
                    dispatchedFrame,
                    currentFrame,
                    expectedStateHash,
                    currentStateHash,
                    isInTransition,
                    nextStateHash,
                })!;
        }

        private static bool InvokeCanCaptureSuppressedAutoAttackGhost(
            string incomingCategory,
            bool hasFacingTargetPoint,
            out string skipReason)
        {
            Type playbackControllerType = RequireRuntimeType("Arena.Presentation.CombatActionPlaybackController");
            Type categoryType = RequireRuntimeType("Arena.Presentation.CombatAnimationCategory");
            MethodInfo method = RequireMethod(
                playbackControllerType,
                "CanCaptureSuppressedAutoAttackGhost",
                categoryType,
                typeof(bool),
                typeof(string).MakeByRefType());
            object?[] args =
            {
                Enum.Parse(categoryType, incomingCategory),
                hasFacingTargetPoint,
                string.Empty,
            };

            bool result = (bool)method.Invoke(null, args)!;
            skipReason = (string)args[2]!;
            return result;
        }

        private static void ResetServerClock()
        {
            RequireMethod(RequireRuntimeType("Arena.Network.ArenaServerClock"), "Reset").Invoke(null, Array.Empty<object>());
        }

        private static void RecordServerClockReducerSample(long clientSendMs, long serverTimestampMs, long clientReceiveMs)
        {
            RequireMethod(
                    RequireRuntimeType("Arena.Network.ArenaServerClock"),
                    "RecordReducerSampleMs",
                    typeof(long),
                    typeof(long),
                    typeof(long))
                .Invoke(null, new object[] { clientSendMs, serverTimestampMs, clientReceiveMs });
        }

        private static long GetServerNowMs()
        {
            Type clockType = RequireRuntimeType("Arena.Network.ArenaServerClock");
            PropertyInfo property = clockType.GetProperty("ServerNowMs", BindingFlags.Public | BindingFlags.Static)
                                    ?? throw new AssertionException("Missing ArenaServerClock.ServerNowMs");
            return (long)property.GetValue(null)!;
        }

        private static object BuildAuthoritativeCombatAnimationRequest(
            string actionId,
            string category,
            long startedAtMs)
        {
            Type requestType = RequireRuntimeType("Arena.Presentation.CombatAnimationRequest");
            Type categoryType = RequireRuntimeType("Arena.Presentation.CombatAnimationCategory");
            MethodInfo method = RequireMethod(
                requestType,
                "Authoritative",
                typeof(string),
                categoryType,
                typeof(long),
                typeof(string),
                typeof(Vector3?),
                typeof(string),
                typeof(string),
                typeof(bool));

            return method.Invoke(
                null,
                new object?[]
                {
                    actionId,
                    Enum.Parse(categoryType, category),
                    startedAtMs,
                    null,
                    null,
                    null,
                    null,
                    false,
                })!;
        }

        private static (bool Resolved, float NormalizedStart, float AppliedCatchupSeconds)
            ResolveRemoteStartNormalizedTime(
                object request,
                bool isLocalPlayer,
                float timingReferenceLengthSeconds,
                float playedClipLengthSeconds,
                float startupTrimSeconds,
                float firstHitWindowSeconds)
        {
            Type requestType = RequireRuntimeType("Arena.Presentation.CombatAnimationRequest");
            Type timingType = RequireRuntimeType("Arena.Presentation.CombatAnimationRemoteTiming");
            MethodInfo method = RequireMethod(
                timingType,
                "TryResolveStartNormalizedTime",
                requestType.MakeByRefType(),
                typeof(bool),
                typeof(float),
                typeof(float),
                typeof(float),
                typeof(float),
                typeof(float).MakeByRefType(),
                typeof(float).MakeByRefType());
            object?[] args =
            {
                request,
                isLocalPlayer,
                timingReferenceLengthSeconds,
                playedClipLengthSeconds,
                startupTrimSeconds,
                firstHitWindowSeconds,
                0f,
                0f,
            };

            bool resolved = (bool)method.Invoke(null, args)!;
            return (resolved, (float)args[6]!, (float)args[7]!);
        }

        private static Type RequireRuntimeType(string fullName)
        {
            return RuntimeAssembly.GetType(fullName)
                   ?? throw new AssertionException($"Missing runtime type {fullName}");
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

        private static FieldInfo RequireField(Type type, string name)
        {
            return type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                   ?? throw new AssertionException($"Missing field {type.FullName}.{name}");
        }

        private static PropertyInfo RequireProperty(Type type, string name)
        {
            return type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                   ?? throw new AssertionException($"Missing property {type.FullName}.{name}");
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

        private static void SetClipEvents(AnimationClip clip, params (string FunctionName, float Time)[] events)
        {
            AnimationUtility.SetAnimationEvents(
                clip,
                events
                    .Select(item => new AnimationEvent
                    {
                        functionName = item.FunctionName,
                        time = item.Time,
                    })
                    .ToArray());
        }

        private static (GameObject Root, Animator Animator, Component PlayerAnimator) CreatePlayerAnimatorHarness()
        {
            AnimatorController controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(
                "Assets/Arena/Content/Animation/Arena_Character.controller");
            Assert.That(controller, Is.Not.Null);

            GameObject root = new("PlayerAnimator regression harness");
            Animator animator = root.AddComponent<Animator>();
            animator.runtimeAnimatorController = controller;

            Type playerAnimatorType = RequireRuntimeType("Arena.Presentation.PlayerAnimator");
            Component playerAnimator = root.AddComponent(playerAnimatorType);
            RequireMethod(playerAnimatorType, "BindAnimator", typeof(Animator))
                .Invoke(playerAnimator, new object[] { animator });
            animator.Update(0f);
            return (root, animator, playerAnimator);
        }

        private static void PlayAllActionLayers(Animator animator)
        {
            animator.Play("UpperBody.UpperBodySpellAction1", 1, 0f);
            animator.Play("MeleeAttack.Strike1", 3, 0f);
            animator.Play("SpellAction.SpellAction1", 4, 0f);
            animator.Play("LeftGesture.LeftGestureSpellAction1", 5, 0f);
            animator.Play("RightGesture.RightGestureSpellAction1", 6, 0f);
            animator.Update(0f);
        }

        private static void AssertActionLayersEmpty(Animator animator)
        {
            int emptyStateHash = Animator.StringToHash("Empty");
            Assert.That(animator.GetCurrentAnimatorStateInfo(1).shortNameHash, Is.EqualTo(emptyStateHash), "UpperBody must be empty.");
            Assert.That(animator.GetCurrentAnimatorStateInfo(3).shortNameHash, Is.EqualTo(emptyStateHash), "MeleeAttack must be empty.");
            Assert.That(animator.GetCurrentAnimatorStateInfo(4).shortNameHash, Is.EqualTo(emptyStateHash), "SpellAction must be empty.");
            Assert.That(animator.GetCurrentAnimatorStateInfo(5).shortNameHash, Is.EqualTo(emptyStateHash), "LeftGesture must be empty.");
            Assert.That(animator.GetCurrentAnimatorStateInfo(6).shortNameHash, Is.EqualTo(emptyStateHash), "RightGesture must be empty.");
        }

        private static string ReadAssetText(string relativeAssetPath)
        {
            string path = System.IO.Path.Combine(Application.dataPath, relativeAssetPath);
            return System.IO.File.ReadAllText(path);
        }

        private static AnimatorState RequireBaseState(AnimatorController controller, string stateName)
        {
            foreach (ChildAnimatorState childState in controller.layers[0].stateMachine.states)
            {
                if (childState.state != null && childState.state.name == stateName)
                    return childState.state;
            }

            throw new AssertionException($"Missing Base Layer state '{stateName}'.");
        }

        private static RequiredCondition Condition(string parameter, AnimatorConditionMode mode) =>
            new(parameter, mode);

        private static void AssertHasTransition(
            AnimatorState source,
            string destinationStateName,
            params RequiredCondition[] requiredConditions)
        {
            bool found = source.transitions.Any(transition =>
                transition.destinationState != null
                && transition.destinationState.name == destinationStateName
                && requiredConditions.All(required => transition.conditions.Any(required.Matches)));

            Assert.That(
                found,
                Is.True,
                $"{source.name} must transition to {destinationStateName} with conditions {string.Join(", ", requiredConditions.Select(condition => condition.ToString()))}.");
        }

        private readonly struct RequiredCondition
        {
            public RequiredCondition(string parameter, AnimatorConditionMode mode)
            {
                Parameter = parameter;
                Mode = mode;
            }

            private string Parameter { get; }
            private AnimatorConditionMode Mode { get; }

            public bool Matches(AnimatorCondition condition) =>
                condition.parameter == Parameter && condition.mode == Mode;

            public override string ToString() => $"{Parameter}:{Mode}";
        }

        private static string ExtractMethodBody(string source, string methodSignature)
        {
            int signatureIndex = source.IndexOf(methodSignature, StringComparison.Ordinal);
            if (signatureIndex < 0)
                throw new AssertionException($"Missing method signature '{methodSignature}'.");

            int openBraceIndex = source.IndexOf('{', signatureIndex);
            if (openBraceIndex < 0)
                throw new AssertionException($"Missing method body for '{methodSignature}'.");

            int depth = 0;
            for (int i = openBraceIndex; i < source.Length; i++)
            {
                if (source[i] == '{')
                    depth++;
                else if (source[i] == '}')
                {
                    depth--;
                    if (depth == 0)
                        return source.Substring(openBraceIndex, i - openBraceIndex + 1);
                }
            }

            throw new AssertionException($"Unterminated method body for '{methodSignature}'.");
        }
    }
}
