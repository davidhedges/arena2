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
            Assert.That(controller.layers.Length, Is.GreaterThanOrEqualTo(6));
            Assert.That(controller.layers[0].name, Is.EqualTo("Base Layer"));
            Assert.That(controller.layers[1].name, Is.EqualTo("UpperBody"));
            Assert.That(controller.layers[2].name, Is.EqualTo("HitReaction"));
            Assert.That(controller.layers[3].name, Is.EqualTo("MeleeAttack"));
            Assert.That(controller.layers[4].name, Is.EqualTo("SpellAction"));
            Assert.That(controller.layers[5].name, Is.EqualTo("LeftGesture"));
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
                RequireField(spellEntryType, "ground").SetValue(spellEntry, clip);
                SetClipEvents(
                    clip,
                    ("OnLowerBodyUnlock", 0.4f),
                    ("OnVisualInterruptible", 0.6f));

                object presentation = RequireMethod(
                        playbackControllerType,
                        "CreateSpellPresentation",
                        typeof(string),
                        typeof(int),
                        spellEntryType,
                        typeof(bool))
                    .Invoke(null, new[] { "FIREBALL", 3, spellEntry, true })!;

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
        public void DecideCombatAnimationRequest_PreservesPriorityAndComboPolicy()
        {
            Assert.That(
                InvokeCombatAnimationDecision(
                    incomingCategory: "AutoAttack",
                    isHigherPriorityActive: true,
                    isSpellActive: false,
                    isMeleeActive: true,
                    isComboFollowUp: false,
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
                    activeMeleeIsPhased: false,
                    visualGateEvaluated: false,
                    visualDecision: "PreserveExistingBehavior").ToString(),
                Is.EqualTo("HandoffComboFollowUpAndPlay"));

            Assert.That(
                InvokeCombatAnimationDecision(
                    incomingCategory: "MeleeSkill",
                    isHigherPriorityActive: false,
                    isSpellActive: false,
                    isMeleeActive: true,
                    isComboFollowUp: false,
                    activeMeleeIsPhased: false,
                    visualGateEvaluated: true,
                    visualDecision: "InterruptCurrentWithoutGhost").ToString(),
                Is.EqualTo("InterruptCurrentWithoutGhostAndPlay"));
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
        public void DecideVisualInterrupt_AllowsAutoAttackAfterThreshold()
        {
            object result = InvokeVisualInterruptDecision(
                activeCategory: "MeleeSkill",
                incomingCategory: "AutoAttack",
                activeIsPhased: false,
                activeElapsedSeconds: 0.50f,
                activeVisualInterruptibleAtSeconds: 0.50f);

            Assert.That(result.ToString(), Is.EqualTo("InterruptCurrentWithoutGhost"));
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

                RequireField(attackType, "visualInterruptibleAtSeconds").SetValue(attack, 0.42f);
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

                RequireField(attackType, "lowerBodyUnlockAtSeconds").SetValue(attack, 0.42f);
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
            Type attackType = RequireRuntimeType("Arena.Presentation.WeaponMeleeAttackAuthoring");
            ScriptableObject set = ScriptableObject.CreateInstance(setType);
            try
            {
                RequireMethod(setType, "EnsureMeleeAttackListSize", typeof(int)).Invoke(set, new object[] { 1 });

                IList attacks = (IList)RequireField(setType, "meleeAttacks").GetValue(set)!;
                object attack = attacks[0]!;
                FieldInfo lowerBodyBlendOutSeconds = RequireField(attackType, "lowerBodyBlendOutSeconds");
                MethodInfo resolver = RequireMethod(setType, "GetLowerBodyBlendOutSeconds", typeof(int), typeof(float));

                lowerBodyBlendOutSeconds.SetValue(attack, -1f);
                attacks[0] = attack;
                Assert.That((float)resolver.Invoke(set, new object[] { 1, 0.12f })!, Is.EqualTo(0.12f).Within(0.001f));

                lowerBodyBlendOutSeconds.SetValue(attack, 0f);
                attacks[0] = attack;
                Assert.That((float)resolver.Invoke(set, new object[] { 1, 0.12f })!, Is.EqualTo(0.12f).Within(0.001f));

                lowerBodyBlendOutSeconds.SetValue(attack, 0.25f);
                attacks[0] = attack;
                Assert.That((float)resolver.Invoke(set, new object[] { 1, 0.12f })!, Is.EqualTo(0.12f).Within(0.001f));
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
                RequireField(spellEntryType, "ground").SetValue(entry, clip);
                RequireField(spellEntryType, "lowerBodyUnlockAtSeconds").SetValue(entry, 0.35f);
                RequireField(spellEntryType, "visualInterruptibleAtSeconds").SetValue(entry, 0.65f);
                SetClipEvents(
                    clip,
                    ("OnLowerBodyUnlock", 0.25f),
                    ("OnVisualInterruptible", 0.75f));
                MethodInfo resolver = RequireMethod(spellEntryType, "ResolveLowerBodyUnlockAtSeconds", typeof(bool));
                MethodInfo visualResolver = RequireMethod(spellEntryType, "ResolveVisualInterruptibleAtSeconds", typeof(bool));

                Assert.That((float)resolver.Invoke(entry, new object[] { true })!, Is.EqualTo(0.25f).Within(0.001f));
                Assert.That((float)visualResolver.Invoke(entry, new object[] { true })!, Is.EqualTo(0.75f).Within(0.001f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(clip);
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
                typeof(string));

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
                })!;
        }

        private static (bool Resolved, float NormalizedStart, float AppliedCatchupSeconds)
            ResolveRemoteStartNormalizedTime(
                object request,
                bool isLocalPlayer,
                float timingReferenceLengthSeconds,
                float playedClipLengthSeconds,
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
                typeof(float).MakeByRefType(),
                typeof(float).MakeByRefType());
            object?[] args =
            {
                request,
                isLocalPlayer,
                timingReferenceLengthSeconds,
                playedClipLengthSeconds,
                firstHitWindowSeconds,
                0f,
                0f,
            };

            bool resolved = (bool)method.Invoke(null, args)!;
            return (resolved, (float)args[5]!, (float)args[6]!);
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
            animator.Update(0f);
        }

        private static void AssertActionLayersEmpty(Animator animator)
        {
            int emptyStateHash = Animator.StringToHash("Empty");
            Assert.That(animator.GetCurrentAnimatorStateInfo(1).shortNameHash, Is.EqualTo(emptyStateHash), "UpperBody must be empty.");
            Assert.That(animator.GetCurrentAnimatorStateInfo(3).shortNameHash, Is.EqualTo(emptyStateHash), "MeleeAttack must be empty.");
            Assert.That(animator.GetCurrentAnimatorStateInfo(4).shortNameHash, Is.EqualTo(emptyStateHash), "SpellAction must be empty.");
            Assert.That(animator.GetCurrentAnimatorStateInfo(5).shortNameHash, Is.EqualTo(emptyStateHash), "LeftGesture must be empty.");
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
