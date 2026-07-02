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
        public void CombatHitReactions_UseSingleCombatAnimationSetResolver()
        {
            string playerAnimatorSource = ReadAssetText("Scripts/Presentation/PlayerAnimator.cs");
            string binderSource = ReadAssetText("Scripts/Presentation/Animation/CombatAnimationSetBinder.cs");
            string animationSetSource = ReadAssetText("Scripts/Presentation/Animation/CombatAnimationSet.cs");
            string roleInfererSource = ReadAssetText("Scripts/Editor/CombatClipRoleInferer.cs");

            Assert.That(playerAnimatorSource, Does.Contain("_animationSetBinder.ApplyHitClipOverrides(_overrideController, animationSet, grounded, _inCombat);"));
            Assert.That(playerAnimatorSource, Does.Not.Contain("useAirVariant ? set.airHitF"));
            Assert.That(binderSource, Does.Contain("set.ResolveHitReactionClips(grounded, inCombat)"));
            Assert.That(binderSource, Does.Not.Contain("useAirVariant ? set.airHitF"));
            Assert.That(animationSetSource, Does.Contain("public HitReactionClipSet ResolveHitReactionClips(bool grounded, bool inCombat)"));
            Assert.That(animationSetSource, Does.Contain("hitCombatF"));
            Assert.That(animationSetSource, Does.Contain("airHitCombatF"));
            Assert.That(roleInfererSource, Does.Contain(".hitCombatF"));
            Assert.That(roleInfererSource, Does.Contain(".airHitCombatF"));
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
        public void AnimatorController_Phase10Inventory_HasNoBasicHygieneIssues()
        {
            string inventorySource = ReadAssetText("Scripts/Editor/CombatAnimatorControllerInventory.cs");
            string upgraderSource = ReadAssetText("Scripts/Editor/CombatAnimatorControllerUpgrader.cs");
            AnimatorController controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(
                "Assets/Arena/Content/Animation/Arena_Character.controller");

            Assert.That(inventorySource, Does.Contain("BuildDefaultReport"));
            Assert.That(inventorySource, Does.Contain("Print Combat Controller Inventory"));
            Assert.That(inventorySource, Does.Contain("duplicate state"));
            Assert.That(inventorySource, Does.Contain("legacy-retained"));
            Assert.That(inventorySource, Does.Contain("candidate-delete"));
            Assert.That(inventorySource, Does.Contain("\"UpperBody/UpperBodySpellAction4\""));
            Assert.That(inventorySource, Does.Contain("\"UpperBody/CastDefault\""));
            Assert.That(upgraderSource, Does.Contain("Deprecated, Disabled"));
            Assert.That(upgraderSource, Does.Not.Contain("[InitializeOnLoadMethod]"));
            Assert.That(upgraderSource, Does.Not.Contain("delayCall += EnsureUpToDate"));
            Assert.That(controller, Is.Not.Null);
            Assert.That(controller.layers.Select(layer => layer.name), Does.Contain("Base Layer"));
            Assert.That(controller.layers.Select(layer => layer.name), Does.Contain("UpperBody"));
            Assert.That(controller.layers.Select(layer => layer.name), Does.Contain("MeleeAttack"));
            Assert.That(controller.parameters.Select(parameter => parameter.name), Does.Not.Contain("TriggerPhasedMeleeStart"));
            Assert.That(controller.parameters.Select(parameter => parameter.name), Does.Not.Contain("IsPhasedMeleeActive"));
            Assert.That(controller.parameters.Select(parameter => parameter.name), Does.Not.Contain("TriggerPhasedMeleeEnd"));
            Assert.That(controller.parameters.Select(parameter => parameter.name), Does.Not.Contain("TriggerChargeStart"));
            Assert.That(controller.parameters.Select(parameter => parameter.name), Does.Not.Contain("IsCharging"));
            Assert.That(controller.parameters.Select(parameter => parameter.name), Does.Not.Contain("TriggerChargeEnd"));
            Assert.That(controller.parameters.Select(parameter => parameter.name), Does.Not.Contain("TriggerParry"));
            Assert.That(controller.parameters.Select(parameter => parameter.name), Does.Contain("IsHardCrowdControlled"));
            Assert.That(controller.parameters.Select(parameter => parameter.name), Does.Contain("TriggerHardCrowdControl"));
            Assert.That(controller.parameters.Select(parameter => parameter.name), Does.Not.Contain("IsStunned"));
            Assert.That(controller.parameters.Select(parameter => parameter.name), Does.Not.Contain("TriggerStun"));
            Assert.That(controller.layers[0].stateMachine.states.Select(child => child.state.name), Does.Not.Contain("PhasedMeleeStart"));
            Assert.That(controller.layers[0].stateMachine.states.Select(child => child.state.name), Does.Not.Contain("PhasedMeleeLoop"));
            Assert.That(controller.layers[0].stateMachine.states.Select(child => child.state.name), Does.Not.Contain("PhasedMeleeEnd"));
            Assert.That(controller.layers[0].stateMachine.states.Select(child => child.state.name), Does.Not.Contain("ChargeStart"));
            Assert.That(controller.layers[0].stateMachine.states.Select(child => child.state.name), Does.Not.Contain("ChargeLoop"));
            Assert.That(controller.layers[0].stateMachine.states.Select(child => child.state.name), Does.Not.Contain("ChargeEnd"));
            Assert.That(controller.layers[0].stateMachine.states.Select(child => child.state.name), Does.Not.Contain("ParryStart"));
            Assert.That(controller.layers[0].stateMachine.states.Select(child => child.state.name), Does.Contain("ParryHit"));
            Assert.That(controller.layers[0].stateMachine.states.Select(child => child.state.name), Does.Contain("HardCrowdControlLoop"));
            Assert.That(controller.layers[0].stateMachine.states.Select(child => child.state.name), Does.Not.Contain("StunLoop"));
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
        public void CombatActionPlaybackController_OwnsRuntimeBankState()
        {
            string playerAnimatorSource = ReadAssetText("Scripts/Presentation/PlayerAnimator.cs");
            string playbackControllerSource = ReadAssetText("Scripts/Presentation/CombatActionPlaybackController.cs");

            Assert.That(playerAnimatorSource, Does.Contain("private readonly CombatActionPlaybackController _actionPlayback = new();"));
            Assert.That(playerAnimatorSource, Does.Contain("_actionPlayback.ResetBanks(set);"));
            Assert.That(playerAnimatorSource, Does.Contain("_actionPlayback.TryBindStrikeClip"));
            Assert.That(playerAnimatorSource, Does.Contain("_actionPlayback.TryBindSpellClip"));
            Assert.That(playerAnimatorSource, Does.Not.Contain("private readonly AnimationClip?[] _strikeBankClips"));
            Assert.That(playerAnimatorSource, Does.Not.Contain("private readonly AnimationClip?[] _spellBankClips"));
            Assert.That(playerAnimatorSource, Does.Not.Contain("private int _nextSpellBankSlot"));
            Assert.That(playerAnimatorSource, Does.Not.Contain("private bool _activeMeleeLowerBodyUnlocked"));
            Assert.That(playerAnimatorSource, Does.Not.Contain("private bool _activeMeleeUpperBodyRecoveryActive"));
            Assert.That(playerAnimatorSource, Does.Not.Contain("private float _activeMeleeLowerBodyUnlockStartedAt"));
            Assert.That(playerAnimatorSource, Does.Not.Contain("private bool _activeSpellLowerBodyUnlocked"));
            Assert.That(playerAnimatorSource, Does.Not.Contain("private float _activeSpellLowerBodyUnlockStartedAt"));
            Assert.That(playerAnimatorSource, Does.Not.Contain("private CombatAnimationCategory? _activeBaseCombatAnimationCategory"));
            Assert.That(playerAnimatorSource, Does.Not.Contain("private enum CombatAnimationDecision"));
            Assert.That(playerAnimatorSource, Does.Not.Contain("private bool _phasedMeleeActive"));
            Assert.That(playerAnimatorSource, Does.Not.Contain("private AnimationClip? _phasedMeleeStartClip"));
            Assert.That(playerAnimatorSource, Does.Not.Contain("private PhasedMeleePlaybackPhase _phasedMeleePhase"));

            Assert.That(playbackControllerSource, Does.Contain("private readonly AnimationClip?[] _strikeBankClips"));
            Assert.That(playbackControllerSource, Does.Contain("private readonly AnimationClip?[] _spellBankClips"));
            Assert.That(playbackControllerSource, Does.Contain("private LowerBodyUnlockPlaybackState _meleeLowerBodyUnlock"));
            Assert.That(playbackControllerSource, Does.Contain("private LowerBodyUnlockPlaybackState _spellLowerBodyUnlock"));
            Assert.That(playbackControllerSource, Does.Contain("private bool _meleeUpperBodyRecoveryActive"));
            Assert.That(playbackControllerSource, Does.Contain("public bool TryBindStrikeClip"));
            Assert.That(playbackControllerSource, Does.Contain("public bool TryBindSpellClip"));
            Assert.That(playbackControllerSource, Does.Contain("public void OverrideStrikeBankSlot"));
            Assert.That(playbackControllerSource, Does.Contain("public static CombatVisualInterruptDecision DecideVisualInterrupt"));
            Assert.That(playbackControllerSource, Does.Contain("public static float ResolvePlaybackThresholdSeconds"));
            Assert.That(playbackControllerSource, Does.Contain("public static float ResolvePlayedMeleeLengthSeconds"));
            Assert.That(playbackControllerSource, Does.Contain("public static float ScaleAuthoredMeleeSeconds"));
            Assert.That(playbackControllerSource, Does.Contain("public static ActiveSpellPresentation CreateSpellPresentation"));
            Assert.That(playbackControllerSource, Does.Contain("public ActiveMeleePresentation CreateMeleePresentation"));
            Assert.That(playbackControllerSource, Does.Contain("public static string DescribeVisualInterruptDecision"));
            Assert.That(playbackControllerSource, Does.Contain("internal readonly struct ActiveMeleePresentation"));
            Assert.That(playbackControllerSource, Does.Contain("internal readonly struct ActiveSpellPresentation"));
            Assert.That(playbackControllerSource, Does.Contain("public ActiveMeleePresentation? ActiveMeleePresentation"));
            Assert.That(playbackControllerSource, Does.Contain("public ActiveSpellPresentation? ActiveSpellPresentation"));
            Assert.That(playbackControllerSource, Does.Contain("public void SetActiveMeleePresentation"));
            Assert.That(playbackControllerSource, Does.Contain("public bool ClearActiveMeleePresentation"));
            Assert.That(playbackControllerSource, Does.Contain("public void SetActiveSpellPresentation"));
            Assert.That(playbackControllerSource, Does.Contain("public void ClearActiveSpellPresentation"));
            Assert.That(playbackControllerSource, Does.Contain("public void MarkMeleeLowerBodyUnlocked"));
            Assert.That(playbackControllerSource, Does.Contain("public void MarkSpellLowerBodyUnlocked"));
            Assert.That(playbackControllerSource, Does.Contain("public float ResolveMeleeLowerBodyLayerWeight"));
            Assert.That(playbackControllerSource, Does.Contain("public float ResolveSpellLowerBodyLayerWeight"));
            Assert.That(playbackControllerSource, Does.Contain("public CombatAnimationCategory? ActiveBaseCombatAnimationCategory"));
            Assert.That(playbackControllerSource, Does.Contain("public bool ClearActiveBaseCombatAnimationCategoryIf"));
            Assert.That(playbackControllerSource, Does.Contain("public bool ClearActiveMeleeBaseCategory"));
            Assert.That(playbackControllerSource, Does.Contain("internal enum CombatAnimationDecision"));
            Assert.That(playbackControllerSource, Does.Contain("public static CombatAnimationDecision DecideCombatAnimationRequest"));
            Assert.That(playbackControllerSource, Does.Contain("internal enum PhasedMeleePlaybackPhase"));
            Assert.That(playbackControllerSource, Does.Contain("public void BeginPhasedMelee"));
            Assert.That(playbackControllerSource, Does.Contain("public bool CancelPhasedMelee"));
            Assert.That(playbackControllerSource, Does.Contain("public AnimationClip? GetPhasedMeleeClip"));
            Assert.That(playbackControllerSource, Does.Contain("public void SetPhasedMeleeSegment"));
            Assert.That(playbackControllerSource, Does.Contain("public bool TryResolvePhasedMeleeTransition"));
            Assert.That(playbackControllerSource, Does.Contain("internal enum CombatPreemptionMode"));
            Assert.That(playbackControllerSource, Does.Contain("public static CombatPreemptionMode ResolvePreemptionMode"));
            Assert.That(playbackControllerSource, Does.Contain("public static bool CanCaptureSuppressedAutoAttackGhost"));
            Assert.That(playbackControllerSource, Does.Contain("public static int ResolveBankedAnimatorHash"));
            Assert.That(playbackControllerSource, Does.Contain("public static int ResolvePhasedMeleeBankSlot"));
            Assert.That(playbackControllerSource, Does.Contain("public static bool TryResolvePhasedMeleeLayerRoute"));
            Assert.That(playbackControllerSource, Does.Contain("public static void PlayFullBodySpellAction"));
            Assert.That(playbackControllerSource, Does.Contain("public static void TriggerMeleeStrike"));
            Assert.That(playbackControllerSource, Does.Contain("public static void PlayMeleeStrikeState"));

            Assert.That(playerAnimatorSource, Does.Not.Contain("private float ResolvePlayedMeleeLengthSeconds"));
            Assert.That(playerAnimatorSource, Does.Not.Contain("private static float ScaleAuthoredMeleeSeconds"));
            Assert.That(playerAnimatorSource, Does.Not.Contain("string traceDecision = decision switch"));
            Assert.That(playerAnimatorSource, Does.Not.Contain("new ActiveMeleePresentation("));
            Assert.That(playerAnimatorSource, Does.Not.Contain("new ActiveSpellPresentation("));
            Assert.That(playerAnimatorSource, Does.Not.Contain("private readonly struct ActiveMeleePresentation"));
            Assert.That(playerAnimatorSource, Does.Not.Contain("private readonly struct ActiveSpellPresentation"));
            Assert.That(playerAnimatorSource, Does.Not.Contain("private ActiveMeleePresentation?"));
            Assert.That(playerAnimatorSource, Does.Not.Contain("private ActiveSpellPresentation?"));
            Assert.That(playerAnimatorSource, Does.Not.Contain("private bool _activeMeleePresentationEntered"));
            Assert.That(playerAnimatorSource, Does.Not.Contain("private bool _activeSpellPresentationEntered"));
            Assert.That(playerAnimatorSource, Does.Contain("_actionPlayback.SetActiveSpellPresentation"));
            Assert.That(playerAnimatorSource, Does.Contain("_actionPlayback.SetActiveMeleePresentation"));
            Assert.That(playerAnimatorSource, Does.Contain("_actionPlayback.ClearActiveSpellPresentation"));
            Assert.That(playerAnimatorSource, Does.Contain("_actionPlayback.ClearActiveMeleePresentation"));
        }

        [Test]
        public void CombatActionPlaybackController_DoesNotOwnStanceOrWholeActionOrchestration()
        {
            string playerAnimatorSource = ReadAssetText("Scripts/Presentation/PlayerAnimator.cs");
            string playbackControllerSource = ReadAssetText("Scripts/Presentation/CombatActionPlaybackController.cs");

            Assert.That(playerAnimatorSource, Does.Contain("private void PlaySpellAnimation"));
            Assert.That(playerAnimatorSource, Does.Contain("private void PlayMeleeAnimation"));
            Assert.That(playerAnimatorSource, Does.Contain("EnterCombatImmediate();"));
            Assert.That(playerAnimatorSource, Does.Contain("SetInCombat(true);"));
            Assert.That(playerAnimatorSource, Does.Contain("_weaponAttachments"));

            Assert.That(playbackControllerSource, Does.Not.Contain("PlaySpellAnimation"));
            Assert.That(playbackControllerSource, Does.Not.Contain("PlayMeleeAnimation"));
            Assert.That(playbackControllerSource, Does.Not.Contain("EnterCombatImmediate"));
            Assert.That(playbackControllerSource, Does.Not.Contain("SetInCombat("));
            Assert.That(playbackControllerSource, Does.Not.Contain("_weaponAttachments"));
            Assert.That(playbackControllerSource, Does.Not.Contain("IsCurrentlyGrounded"));
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
        public void CombatActionPlaybackController_CreateMeleePresentationCachesCategoryAndFallbackTiming()
        {
            Type playbackControllerType = RequireRuntimeType("Arena.Presentation.CombatActionPlaybackController");
            Type requestType = RequireRuntimeType("Arena.Presentation.CombatAnimationRequest");
            Type categoryType = RequireRuntimeType("Arena.Presentation.CombatAnimationCategory");
            Type authorityType = RequireRuntimeType("Arena.Presentation.CombatAnimationAuthority");
            Type playbackType = RequireRuntimeType("Arena.Presentation.CombatAnimationPlayback");
            Type animationSetType = RequireRuntimeType("Arena.Presentation.CombatAnimationSet");
            object controller = Activator.CreateInstance(playbackControllerType)!;
            object autoAttackCategory = Enum.Parse(categoryType, "AutoAttack");
            object request = Activator.CreateInstance(
                requestType,
                "basic_attack",
                Enum.Parse(categoryType, "MeleeSkill"),
                Enum.Parse(authorityType, "Authoritative"),
                Enum.Parse(playbackType, "Automatic"),
                "auto_attack",
                0L,
                null,
                null,
                null)!;

            object presentation = RequireMethod(
                    playbackControllerType,
                    "CreateMeleePresentation",
                    requestType,
                    typeof(int),
                    typeof(bool),
                    animationSetType,
                    typeof(bool),
                    typeof(float))
                .Invoke(controller, new[] { request, 0, false, null, true, 0.25f })!;

            Assert.That(RequireProperty(playbackControllerType, "ActiveBaseCombatAnimationCategory").GetValue(controller), Is.EqualTo(autoAttackCategory));
            Assert.That(RequireField(presentation.GetType(), "ActionId").GetValue(presentation), Is.EqualTo("basic_attack"));
            Assert.That(RequireField(presentation.GetType(), "Category").GetValue(presentation), Is.EqualTo(autoAttackCategory));
            Assert.That(RequireField(presentation.GetType(), "StrikeIndex").GetValue(presentation), Is.EqualTo(0));
            Assert.That((float)RequireField(presentation.GetType(), "PlayedLengthSeconds").GetValue(presentation)!, Is.EqualTo(0f).Within(0.001f));
            Assert.That((float)RequireField(presentation.GetType(), "AppliedCatchupSeconds").GetValue(presentation)!, Is.EqualTo(0.25f).Within(0.001f));
            Assert.That(RequireField(presentation.GetType(), "IsPhased").GetValue(presentation), Is.EqualTo(false));
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
        public void RemoteCombatTiming_RequiresServerClockEstimate()
        {
            ResetServerClock();
            object request = BuildAuthoritativeCombatAnimationRequest(
                "TEST_STRIKE",
                "MeleeSkill",
                1000L);

            var result = ResolveRemoteStartNormalizedTime(
                request,
                isLocalPlayer: false,
                timingReferenceLengthSeconds: 1.0f,
                playedClipLengthSeconds: 1.0f,
                firstHitWindowSeconds: 0.5f);

            Assert.That(result.Resolved, Is.False);
            Assert.That(result.NormalizedStart, Is.EqualTo(0f));
            Assert.That(result.AppliedCatchupSeconds, Is.EqualTo(0f));
        }

        [Test]
        public void RemoteCombatTiming_ClampsCatchupToFirstHitWindowSafetyMargin()
        {
            ResetServerClock();
            RecordServerClockReducerSample(
                clientSendMs: 900L,
                serverTimestampMs: 950L,
                clientReceiveMs: 1000L);
            object request = BuildAuthoritativeCombatAnimationRequest(
                "TEST_STRIKE",
                "MeleeSkill",
                GetServerNowMs() - 500L);

            var result = ResolveRemoteStartNormalizedTime(
                request,
                isLocalPlayer: false,
                timingReferenceLengthSeconds: 1.0f,
                playedClipLengthSeconds: 1.0f,
                firstHitWindowSeconds: 0.12f);

            Assert.That(result.Resolved, Is.True);
            Assert.That(result.AppliedCatchupSeconds, Is.EqualTo(0.07f).Within(0.001f));
            Assert.That(result.NormalizedStart, Is.EqualTo(0.07f).Within(0.001f));
        }

        [Test]
        public void RemoteCombatTiming_NormalizesCatchupAgainstPlayedClipLength()
        {
            ResetServerClock();
            RecordServerClockReducerSample(
                clientSendMs: 900L,
                serverTimestampMs: 950L,
                clientReceiveMs: 1000L);
            object request = BuildAuthoritativeCombatAnimationRequest(
                "TEST_STRIKE",
                "MeleeSkill",
                GetServerNowMs() - 100L);

            var result = ResolveRemoteStartNormalizedTime(
                request,
                isLocalPlayer: false,
                timingReferenceLengthSeconds: 1.0f,
                playedClipLengthSeconds: 2.0f,
                firstHitWindowSeconds: 0.5f);

            Assert.That(result.Resolved, Is.True);
            Assert.That(result.AppliedCatchupSeconds, Is.EqualTo(0.10f).Within(0.001f));
            Assert.That(result.NormalizedStart, Is.EqualTo(0.05f).Within(0.001f));
        }

        [Test]
        public void RemoteCombatTiming_AllowsSpellReleaseCatchupBeforeAuthoredReleasePoint()
        {
            ResetServerClock();
            RecordServerClockReducerSample(
                clientSendMs: 900L,
                serverTimestampMs: 950L,
                clientReceiveMs: 1000L);
            object request = BuildAuthoritativeCombatAnimationRequest(
                "FIREBALL",
                "Spell",
                GetServerNowMs() - 100L);

            var result = ResolveRemoteStartNormalizedTime(
                request,
                isLocalPlayer: false,
                timingReferenceLengthSeconds: 1.0f,
                playedClipLengthSeconds: 1.0f,
                firstHitWindowSeconds: 0.5f);

            Assert.That(result.Resolved, Is.True);
            Assert.That(result.AppliedCatchupSeconds, Is.EqualTo(0.10f).Within(0.001f));
            Assert.That(result.NormalizedStart, Is.EqualTo(0.10f).Within(0.001f));
        }

        [Test]
        public void RemoteCombatTiming_DoesNotOffsetLocalAuthoritativeReplay()
        {
            ResetServerClock();
            RecordServerClockReducerSample(
                clientSendMs: 900L,
                serverTimestampMs: 950L,
                clientReceiveMs: 1000L);
            object request = BuildAuthoritativeCombatAnimationRequest(
                "TEST_STRIKE",
                "MeleeSkill",
                GetServerNowMs() - 100L);

            var result = ResolveRemoteStartNormalizedTime(
                request,
                isLocalPlayer: true,
                timingReferenceLengthSeconds: 1.0f,
                playedClipLengthSeconds: 1.0f,
                firstHitWindowSeconds: 0.5f);

            Assert.That(result.Resolved, Is.False);
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

        [Test]
        public void PlayerAnimator_FullBodySpellLowerBodyUnlock_ContinuesOnUpperBodySpellBank()
        {
            string source = ReadAssetText("Scripts/Presentation/PlayerAnimator.cs");
            string playSpell = ExtractMethodBody(source, "private void PlaySpellAnimation");
            string updateSpellUnlock = ExtractMethodBody(source, "private void UpdateSpellLowerBodyUnlock");

            Assert.That(playSpell, Does.Contain("SetActiveSpellPresentation"));
            Assert.That(playSpell, Does.Contain("spellEntry.ResolveUsesOverlayPlayback"));
            Assert.That(updateSpellUnlock, Does.Contain("SpellActionLayerIndex"));
            Assert.That(updateSpellUnlock, Does.Contain("ResolveUpperBodySpellStateHash(active.BankSlot)"));
            Assert.That(updateSpellUnlock, Does.Contain("_animator.SetLayerWeight(SpellActionLayerIndex, nextWeight);"));
        }

        [Test]
        public void PlayerAnimator_LowerBodyUnlock_WaitsForLocomotionDemand()
        {
            string source = ReadAssetText("Scripts/Presentation/PlayerAnimator.cs");
            string meleeUnlock = ExtractMethodBody(source, "private void UpdateMeleeLowerBodyUnlock");
            string spellUnlock = ExtractMethodBody(source, "private void UpdateSpellLowerBodyUnlock");
            string shouldRelease = ExtractMethodBody(source, "private bool ShouldReleaseLowerBodyToLocomotion");

            Assert.That(meleeUnlock, Does.Contain("ShouldReleaseLowerBodyToLocomotion()"));
            Assert.That(spellUnlock, Does.Contain("ShouldReleaseLowerBodyToLocomotion()"));
            Assert.That(shouldRelease, Does.Contain("_latestLocomotionRawMagnitude >= StopTriggerThreshold"));
        }

        [Test]
        public void PlayerAnimator_DodgeRecovery_UnlocksLocomotionAtGameplayActiveEnd()
        {
            string source = ReadAssetText("Scripts/Presentation/PlayerAnimator.cs");
            string triggerDodge = ExtractMethodBody(source, "public void TriggerDodge(MovementActionState movementAction)");
            string recover = ExtractMethodBody(source, "private void RecoverLocomotionFromTransientStates");
            string movementGate = ExtractMethodBody(source, "private float ResolveMovementActionLocomotionUnlockGate");
            string resolver = ExtractMethodBody(source, "private static bool TryResolveLocomotionRecovery");
            string registry = ReadAssetText("Scripts/Entity/EntityRegistry.cs");

            Assert.That(triggerDodge, Does.Contain("movementAction.RecoveryUntil.MicrosecondsSinceUnixEpoch"));
            Assert.That(triggerDodge, Does.Contain("movementAction.ActiveUntil.MicrosecondsSinceUnixEpoch"));
            Assert.That(registry, Does.Contain("entity.TriggerDodge(row);"));
            Assert.That(recover, Does.Contain("ResolveMovementActionLocomotionUnlockGate(state.shortNameHash)"));
            Assert.That(movementGate, Does.Contain("stateHash != DodgeStateHash"));
            Assert.That(movementGate, Does.Contain("nowMs < active.ActiveUntilMs"));
            Assert.That(resolver, Does.Contain("stateHash == DodgeStateHash"));
            Assert.That(resolver, Does.Contain("Mathf.Clamp01(movementActionLocomotionUnlockGate)"));
        }

        [Test]
        public void PlayerAnimator_SpellVisualInterrupt_ParticipatesInRequestGate()
        {
            string source = ReadAssetText("Scripts/Presentation/PlayerAnimator.cs");
            string decideRequest = ExtractMethodBody(source, "private CombatAnimationDecision DecideCombatAnimationRequest");
            string spellGate = ExtractMethodBody(source, "private bool TryDecideVisualInterruptForActiveSpell");
            string preempt = ExtractMethodBody(source, "private void PreemptLowerPriorityPresentationFor");

            Assert.That(decideRequest, Does.Contain("bool isSpellActive = IsActiveSpellPresentationStateActive();"));
            Assert.That(decideRequest, Does.Contain("CombatActionPlaybackController.DecideCombatAnimationRequest"));
            Assert.That(decideRequest, Does.Contain("|| isSpellActive"));
            Assert.That(spellGate, Does.Contain("active.VisualInterruptibleAtSeconds"));
            Assert.That(spellGate, Does.Contain("CombatAnimationCategory.Spell"));
            Assert.That(preempt, Does.Contain("ClearActiveSpellPresentation"));
            Assert.That(preempt, Does.Contain("SpellActionEmptyStateHash"));
        }

        [Test]
        public void PlayerAnimator_LeftGestureSpellPlayback_UsesSingleMaskedGestureLayer()
        {
            string source = ReadAssetText("Scripts/Presentation/PlayerAnimator.cs");
            string animationSetSource = ReadAssetText("Scripts/Presentation/Animation/CombatAnimationSet.cs");
            string playSpell = ExtractMethodBody(source, "private void PlaySpellAnimation");
            string higherPriority = ExtractMethodBody(source, "private bool IsHigherPriorityCombatPresentationActive");
            string clearLeftGesture = ExtractMethodBody(source, "private void ClearLeftGestureSpellPresentation");

            Assert.That(animationSetSource, Does.Contain("playbackLayer == SpellPlaybackLayer.LeftGesture"));
            Assert.That(playSpell, Does.Contain("spellEntry.UsesLeftGesture"));
            Assert.That(playSpell, Does.Contain("PlayLeftGestureState(ResolveLeftGestureSpellStateHash(bankSlot), normalizedStart)"));
            Assert.That(playSpell, Does.Contain("PlayUpperBodyState(UpperBodyEmptyStateHash, 0f)"));
            Assert.That(higherPriority, Does.Contain("IsSkillPresentationStateActive(LeftGestureLayerIndex"));
            Assert.That(clearLeftGesture, Does.Contain("LeftGestureLayerIndex"));
            Assert.That(source, Does.Not.Contain("LeftArmLayerIndex"));
            Assert.That(source, Does.Not.Contain("LeftArmSpellAction"));
        }

        [Test]
        public void PlayerAnimator_ReactionPriority_StunPreemptsActiveHitReaction()
        {
            string source = ReadAssetText("Scripts/Presentation/CombatStatusReactionController.cs");
            string setHardCrowdControl = ExtractMethodBody(source, "public void SetHardCrowdControl");

            Assert.That(setHardCrowdControl, Does.Contain("ClearHitReactionPresentation();"));
            Assert.That(setHardCrowdControl, Does.Contain("_animator.SetTrigger(TriggerHardCrowdControlHash);"));
            Assert.That(source, Does.Contain("IsHardCrowdControlled"));
            Assert.That(source, Does.Contain("TriggerHardCrowdControl"));
            Assert.That(source, Does.Contain("slot_hard_crowd_control_loop"));
            Assert.That(source, Does.Not.Contain("IsStunned"));
            Assert.That(source, Does.Not.Contain("TriggerStun"));
            Assert.That(source, Does.Not.Contain("slot_stun_loop"));
        }

        [Test]
        public void PlayerAnimator_ReactionPriority_KnockdownPreemptsStunAndHitReaction()
        {
            string source = ReadAssetText("Scripts/Presentation/CombatStatusReactionController.cs");
            string setKnockedDown = ExtractMethodBody(source, "public void SetKnockedDown");
            string setHardCrowdControl = ExtractMethodBody(source, "public void SetHardCrowdControl");

            Assert.That(setKnockedDown, Does.Contain("ClearHitReactionPresentation();"));
            Assert.That(setKnockedDown, Does.Contain("ClearHardCrowdControlPresentation();"));
            Assert.That(setHardCrowdControl, Does.Contain("isActive && _animator.GetBool(IsKnockedDownHash)"));
            Assert.That(setHardCrowdControl, Does.Contain("ClearHardCrowdControlPresentation();"));
        }

        [Test]
        public void PlayerAnimator_ReactionPriority_DeathClearsNonDeathPresentation()
        {
            string source = ReadAssetText("Scripts/Presentation/PlayerAnimator.cs");
            string statusSource = ReadAssetText("Scripts/Presentation/CombatStatusReactionController.cs");
            string setDead = ExtractMethodBody(source, "public void SetDead");
            string clearNonDeath = ExtractMethodBody(source, "private void ClearNonDeathPresentation");
            string restoreAlive = ExtractMethodBody(source, "private void RestoreAlivePresentationAfterDeath");
            string clearController = ExtractMethodBody(statusSource, "public void ClearForNonDeath");

            Assert.That(setDead, Does.Contain("ClearNonDeathPresentation();"));
            Assert.That(setDead, Does.Contain("RestoreAlivePresentationAfterDeath();"));
            Assert.That(clearNonDeath, Does.Contain("StatusReactionController.ClearForNonDeath();"));
            Assert.That(clearController, Does.Contain("ClearHitReactionPresentation();"));
            Assert.That(clearController, Does.Contain("ClearHardCrowdControlPresentation();"));
            Assert.That(clearController, Does.Contain("ClearKnockdownPresentation();"));
            Assert.That(clearNonDeath, Does.Contain("PreemptMeleeAnimationIfActive(captureGhost: false);"));
            Assert.That(restoreAlive, Does.Contain("ClearNonDeathPresentation();"));
            Assert.That(restoreAlive, Does.Contain("_inCombat ? IdleCombatStateHash : IdleWalkRunBlendStateHash"));
            Assert.That(restoreAlive, Does.Contain("_animator.Update(0f);"));
        }

        [Test]
        public void EntityRegistry_ReactionPriority_DefersOrdinaryHitReactionForStatusCoalescing()
        {
            string source = ReadAssetText("Scripts/Entity/EntityRegistry.cs");
            string onCombatEventInsert = ExtractMethodBody(source, "public void OnCombatEventInsert");
            string queueHitReaction = ExtractMethodBody(source, "private void QueueHitReaction");
            string flushPending = ExtractMethodBody(source, "private void FlushPendingHitReactions");

            Assert.That(onCombatEventInsert, Does.Contain("QueueHitReaction(row.Hit"));
            Assert.That(queueHitReaction, Does.Contain("_pendingHitReactions.Add"));
            Assert.That(onCombatEventInsert, Does.Not.Contain("hitEntity.TriggerHit"));
            Assert.That(flushPending, Does.Contain("pending.FrameQueued >= Time.frameCount"));
            Assert.That(flushPending, Does.Contain("HasSuppressingReactionStatus(pending.Target)"));
            Assert.That(flushPending, Does.Contain("entity.TriggerHit(pending.Direction);"));
        }

        [Test]
        public void EntityRegistry_ProjectileContacts_QueueOrdinaryHitReaction()
        {
            string source = ReadAssetText("Scripts/Entity/EntityRegistry.cs");
            string onProjectileEventInsert = ExtractMethodBody(source, "public void OnProjectilePresentationEventInsert");
            string shouldTrigger = ExtractMethodBody(source, "private static bool ShouldTriggerProjectileContactHitReaction");
            string binder = ReadAssetText("Scripts/Network/NetworkCallbackBinder.cs");

            Assert.That(binder, Does.Contain("conn.Db.ProjectilePresentationEvent.OnInsert += registry.OnProjectilePresentationEventInsert"));
            Assert.That(onProjectileEventInsert, Does.Contain("ShouldTriggerProjectileContactHitReaction(row)"));
            Assert.That(onProjectileEventInsert, Does.Contain("QueueHitReaction(row.Hit, row.DirX, row.DirZ);"));
            Assert.That(shouldTrigger, Does.Contain("row.EventType != CombatEventTypes.Contact"));
            Assert.That(shouldTrigger, Does.Contain("TryGetSpellDamage(row.ActionKind, out int damage) && damage > 0"));
        }

        [Test]
        public void PlayerAnimator_PhasedMelee_UsesMeleeAndUpperBodyLayersInsteadOfBaseLayer()
        {
            string source = ReadAssetText("Scripts/Presentation/PlayerAnimator.cs");
            string triggerPhased = ExtractMethodBody(source, "private bool TryTriggerPhasedMeleeAction");
            string playSegment = ExtractMethodBody(source, "private bool PlayPhasedMeleeSegment");

            Assert.That(triggerPhased, Does.Not.Contain("BaseLayerIndex"));
            Assert.That(triggerPhased, Does.Not.Contain("PhasedMeleeStartStateHash"));
            Assert.That(triggerPhased, Does.Not.Contain("TriggerPhasedMeleeStartHash"));
            Assert.That(triggerPhased, Does.Not.Contain("IsPhasedMeleeActiveHash"));
            Assert.That(triggerPhased, Does.Contain("ResolveStrikeBankSlot(strikeIndex)"));
            Assert.That(playSegment, Does.Contain("MeleeAttackLayerIndex"));
            Assert.That(playSegment, Does.Contain("CombatActionPlaybackController.TryResolvePhasedMeleeLayerRoute"));
            Assert.That(playSegment, Does.Contain("UpperBodyRecoverySlotName"));
            Assert.That(playSegment, Does.Contain("UpperBodyRecoveryAction1StateHash"));
        }

        [Test]
        public void CombatAnimationSetEditor_PhasedMelee_UsesStampedTimingEvents()
        {
            string source = ReadAssetText("Scripts/Editor/CombatAnimationSetEditor.cs");
            string drawMeleeAttacks = ExtractMethodBody(source, "private void DrawMeleeAttackAuthoringSection");

            Assert.That(drawMeleeAttacks, Does.Contain("usesPhasedPresentation"));
            Assert.That(drawMeleeAttacks, Does.Contain("OnLowerBodyUnlock"));
            Assert.That(drawMeleeAttacks, Does.Contain("OnVisualInterruptible"));
            Assert.That(drawMeleeAttacks, Does.Contain("Lower-body blend-out uses the runtime default"));
            Assert.That(drawMeleeAttacks, Does.Not.Contain("lowerBodyUnlockAtSecondsProperty"));
            Assert.That(drawMeleeAttacks, Does.Not.Contain("visualInterruptibleAtSecondsProperty"));
        }

        [Test]
        public void CombatAnimationSetEditor_AnimationPreview_IncludesSpellActions()
        {
            string source = ReadAssetText("Scripts/Editor/CombatAnimationSetEditor.cs");
            string previewPane = ExtractMethodBody(source, "private void DrawMeleeAnimationPreviewPane");
            string spellSelection = ExtractMethodBody(source, "private bool TryDrawSpellPreviewSelection");
            string spellOptions = ExtractMethodBody(source, "private static List<AttackPreviewClipOption> BuildSpellPreviewClipOptions");

            Assert.That(previewPane, Does.Contain("Spell Actions"));
            Assert.That(previewPane, Does.Contain("TryDrawSpellPreviewSelection"));
            Assert.That(spellSelection, Does.Contain("WeaponSpellAnimationEntry"));
            Assert.That(spellSelection, Does.Contain("DescribeSpellPreviewClips"));
            Assert.That(spellOptions, Does.Contain("\"Ground\""));
            Assert.That(spellOptions, Does.Contain("\"Air\""));
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
