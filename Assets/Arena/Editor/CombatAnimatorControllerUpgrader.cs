#nullable enable

using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Arena.Editor
{
    [System.Obsolete("Deprecated legacy controller mutator. Use CombatAnimatorControllerInventory for read-only Phase 10 inspection.")]
    public static class CombatAnimatorControllerUpgrader
    {
        private const string ControllerPath = "Assets/Arena/Content/Animation/Arena_Character.controller";
        private const string UpperBodyLayerName = "UpperBody";
        private const string BlockStartSlotPath = "Assets/Arena/Content/Animation/Slots/slot_block_start.anim";
        private const string BlockLoopSlotPath = "Assets/Arena/Content/Animation/Slots/slot_block_loop.anim";
        private const string BlockEndSlotPath = "Assets/Arena/Content/Animation/Slots/slot_block_end.anim";
        private const string BlockHitSlotPath = "Assets/Arena/Content/Animation/Slots/slot_block_hit.anim";
        private const string BlockHitBreakSlotPath = "Assets/Arena/Content/Animation/Slots/slot_block_hit_break.anim";
        private const string ParryStartSlotPath = "Assets/Arena/Content/Animation/Slots/slot_parry_start.anim";
        private const string ParryHitSlotPath = "Assets/Arena/Content/Animation/Slots/slot_parry_hit.anim";
        private const string BlockWalkStartSlotPrefix = "slot_block_walk_start";
        private const string BlockWalkLoopSlotPrefix = "slot_block_walk_loop";
        private const string BlockWalkEndSlotPrefix = "slot_block_walk_end";
        private const string EnterCombatIdleSlotPath = "Assets/Arena/Content/Animation/Slots/slot_enter_combat_idle.anim";
        private const string EnterCombatWalkSlotPath = "Assets/Arena/Content/Animation/Slots/slot_enter_combat_walk.anim";
        private const string EnterCombatRunSlotPath = "Assets/Arena/Content/Animation/Slots/slot_enter_combat_run.anim";
        private const string ExitCombatIdleSlotPath = "Assets/Arena/Content/Animation/Slots/slot_exit_combat_idle.anim";
        private const string ExitCombatWalkSlotPath = "Assets/Arena/Content/Animation/Slots/slot_exit_combat_walk.anim";
        private const string ExitCombatRunSlotPath = "Assets/Arena/Content/Animation/Slots/slot_exit_combat_run.anim";
        private const string DrawWeaponSlotPath = "Assets/Arena/Content/Animation/Slots/slot_draw_weapon.anim";
        private const string SheathWeaponSlotPath = "Assets/Arena/Content/Animation/Slots/slot_sheath_weapon.anim";
        private const string SpellSlot1Path = "Assets/Arena/Content/Animation/Slots/slot_spell_1.anim";
        private const string SpellSlot2Path = "Assets/Arena/Content/Animation/Slots/slot_spell_2.anim";
        private const string SpellSlot3Path = "Assets/Arena/Content/Animation/Slots/slot_spell_3.anim";
        private const string SpellSlot4Path = "Assets/Arena/Content/Animation/Slots/slot_spell_4.anim";
        private const string KnockdownStartSlotPath = "Assets/Arena/Content/Animation/Slots/slot_knockdown_start.anim";
        private const string KnockdownLoopSlotPath = "Assets/Arena/Content/Animation/Slots/slot_knockdown_loop.anim";
        private const string GetUpSlotPath = "Assets/Arena/Content/Animation/Slots/slot_get_up.anim";
        private const string HardCrowdControlLoopSlotPath = "Assets/Arena/Content/Animation/Slots/slot_hard_crowd_control_loop.anim";
        private const string PhasedMeleeStartSlotPath = "Assets/Arena/Content/Animation/Slots/slot_phased_melee_start.anim";
        private const string PhasedMeleeLoopSlotPath = "Assets/Arena/Content/Animation/Slots/slot_phased_melee_loop.anim";
        private const string PhasedMeleeEndSlotPath = "Assets/Arena/Content/Animation/Slots/slot_phased_melee_end.anim";
        private const string DodgeSlotPrefix = "slot_dodge";
        private const float BlockDirectionalMotionThreshold = 0.12f;
        private const string DeprecationMessage =
            "CombatAnimatorControllerUpgrader is deprecated and disabled. It used to mutate Arena_Character.controller automatically on editor load, but the Phase 10 cleanup now uses read-only inventory/validation before any explicit controller edits.";

        [MenuItem("Arena/Animation/Upgrade Combat Controller (Deprecated, Disabled)")]
        public static void ShowDeprecatedUpgradeMessage()
        {
            Debug.LogWarning($"[CombatAnimatorControllerUpgrader] {DeprecationMessage}");
            EditorUtility.DisplayDialog(
                "Combat Controller Upgrader Disabled",
                DeprecationMessage,
                "OK");
        }

        private static void EnsureUpToDateLegacyUnsafe()
        {
            AnimatorController? controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
            if (controller == null || controller.layers.Length == 0)
                return;

            AnimatorStateMachine root = controller.layers[0].stateMachine;
            AnimatorStateMachine upperBody = FindLayerStateMachine(controller, UpperBodyLayerName) ?? controller.layers[0].stateMachine;
            AnimatorState idleWalkRunBlend = FindState(root, "Idle Walk Run Blend") ?? root.defaultState;
            AnimatorState idleCombat = FindState(root, "IdleCombat") ?? root.defaultState;
            AnimatorState inAir = FindState(root, "InAir") ?? root.defaultState;
            AnimatorState inAirCombat = FindState(root, "InAirCombat") ?? root.defaultState;
            AnimatorState upperBodyEmpty = FindState(upperBody, "Empty") ?? upperBody.defaultState;
            bool changed = false;

            changed |= EnsureParameter(controller, "TriggerBlockStart", AnimatorControllerParameterType.Trigger);
            changed |= EnsureParameter(controller, "IsBlocking", AnimatorControllerParameterType.Bool);
            changed |= EnsureParameter(controller, "TriggerBlockHit", AnimatorControllerParameterType.Trigger);
            changed |= EnsureParameter(controller, "TriggerParryHit", AnimatorControllerParameterType.Trigger);
            changed |= EnsureParameter(controller, "TriggerPhasedMeleeStart", AnimatorControllerParameterType.Trigger);
            changed |= EnsureParameter(controller, "IsPhasedMeleeActive", AnimatorControllerParameterType.Bool);
            changed |= EnsureParameter(controller, "TriggerPhasedMeleeEnd", AnimatorControllerParameterType.Trigger);
            changed |= EnsureParameter(controller, "TriggerDodge", AnimatorControllerParameterType.Trigger);
            changed |= EnsureParameter(controller, "DodgeX", AnimatorControllerParameterType.Float);
            changed |= EnsureParameter(controller, "DodgeZ", AnimatorControllerParameterType.Float);
            changed |= EnsureParameter(controller, "TriggerKnockdown", AnimatorControllerParameterType.Trigger);
            changed |= EnsureParameter(controller, "IsKnockedDown", AnimatorControllerParameterType.Bool);
            changed |= EnsureParameter(controller, "TriggerGetUp", AnimatorControllerParameterType.Trigger);
            changed |= EnsureParameter(controller, "TriggerHardCrowdControl", AnimatorControllerParameterType.Trigger);
            changed |= EnsureParameter(controller, "IsHardCrowdControlled", AnimatorControllerParameterType.Bool);
            changed |= EnsureParameter(controller, "TriggerStaggerF", AnimatorControllerParameterType.Trigger);
            changed |= EnsureParameter(controller, "TriggerStaggerB", AnimatorControllerParameterType.Trigger);
            changed |= EnsureParameter(controller, "TriggerStaggerL", AnimatorControllerParameterType.Trigger);
            changed |= EnsureParameter(controller, "TriggerStaggerR", AnimatorControllerParameterType.Trigger);

            AnimationClip blockStartClip = LoadAnimationClip(BlockStartSlotPath);
            AnimationClip blockLoopClip = LoadAnimationClip(BlockLoopSlotPath);
            AnimationClip blockEndClip = LoadAnimationClip(BlockEndSlotPath);
            BlendTree blockStartDirectional = EnsureDirectionalBlendTree(controller, "BlockStartDirectional", BlockWalkStartSlotPrefix, ref changed);
            BlendTree blockLoopDirectional = EnsureDirectionalBlendTree(controller, "BlockLoopDirectional", BlockWalkLoopSlotPrefix, ref changed);
            BlendTree blockEndDirectional = EnsureDirectionalBlendTree(controller, "BlockEndDirectional", BlockWalkEndSlotPrefix, ref changed);
            BlendTree dodgeDirectional = EnsureDirectionalBlendTree(controller, "DodgeDirectional", DodgeSlotPrefix, "DodgeX", "DodgeZ", ref changed);
            BlendTree blockStartMotion = EnsureMotionSpeedBlendTree(controller, "BlockStartMotion", blockStartClip, blockStartDirectional, ref changed);
            BlendTree blockLoopMotion = EnsureMotionSpeedBlendTree(controller, "BlockLoopMotion", blockLoopClip, blockLoopDirectional, ref changed);
            BlendTree blockEndMotion = EnsureMotionSpeedBlendTree(controller, "BlockEndMotion", blockEndClip, blockEndDirectional, ref changed);

            AnimatorState blockStart = EnsureState(root, "BlockStart", blockStartMotion, ref changed);
            AnimatorState blockLoop = EnsureState(root, "BlockLoop", blockLoopMotion, ref changed);
            AnimatorState blockEnd = EnsureState(root, "BlockEnd", blockEndMotion, ref changed);
            AnimatorState blockHit = EnsureState(root, "BlockHit", LoadAnimationClip(BlockHitSlotPath), ref changed);
            _ = EnsureState(root, "BlockHitBreak", LoadAnimationClip(BlockHitBreakSlotPath), ref changed);
            AnimatorState parryHit = EnsureState(root, "ParryHit", LoadAnimationClip(ParryHitSlotPath), ref changed);
            AnimatorState enterCombatIdle = EnsureState(root, "EnterCombatIdle", LoadAnimationClip(EnterCombatIdleSlotPath), ref changed);
            AnimatorState enterCombatWalk = EnsureState(root, "EnterCombatWalk", LoadAnimationClip(EnterCombatWalkSlotPath), ref changed);
            AnimatorState enterCombatRun = EnsureState(root, "EnterCombatRun", LoadAnimationClip(EnterCombatRunSlotPath), ref changed);
            AnimatorState exitCombatIdle = EnsureState(root, "ExitCombatIdle", LoadAnimationClip(ExitCombatIdleSlotPath), ref changed);
            AnimatorState exitCombatWalk = EnsureState(root, "ExitCombatWalk", LoadAnimationClip(ExitCombatWalkSlotPath), ref changed);
            AnimatorState exitCombatRun = EnsureState(root, "ExitCombatRun", LoadAnimationClip(ExitCombatRunSlotPath), ref changed);

            AnimatorState knockdownStart = EnsureState(root, "KnockdownStart", LoadAnimationClip(KnockdownStartSlotPath), ref changed);
            AnimatorState knockdownLoop = EnsureState(root, "KnockdownLoop", LoadAnimationClip(KnockdownLoopSlotPath), ref changed);
            AnimatorState getUp = EnsureState(root, "GetUp", LoadAnimationClip(GetUpSlotPath), ref changed);
            AnimatorState hardCrowdControlLoop = EnsureState(root, "HardCrowdControlLoop", LoadAnimationClip(HardCrowdControlLoopSlotPath), ref changed);
            AnimatorState staggerF = EnsureState(root, "StaggerF", EnsureEmbeddedSlotClip(controller, "slot_stagger_F", ref changed), ref changed);
            AnimatorState staggerB = EnsureState(root, "StaggerB", EnsureEmbeddedSlotClip(controller, "slot_stagger_B", ref changed), ref changed);
            AnimatorState staggerL = EnsureState(root, "StaggerL", EnsureEmbeddedSlotClip(controller, "slot_stagger_L", ref changed), ref changed);
            AnimatorState staggerR = EnsureState(root, "StaggerR", EnsureEmbeddedSlotClip(controller, "slot_stagger_R", ref changed), ref changed);

            AnimatorState phasedMeleeStart = EnsureState(root, "PhasedMeleeStart", LoadAnimationClip(PhasedMeleeStartSlotPath), ref changed);
            AnimatorState phasedMeleeLoop = EnsureState(root, "PhasedMeleeLoop", LoadAnimationClip(PhasedMeleeLoopSlotPath), ref changed);
            AnimatorState phasedMeleeEnd = EnsureState(root, "PhasedMeleeEnd", LoadAnimationClip(PhasedMeleeEndSlotPath), ref changed);
            AnimatorState dodge = EnsureState(root, "Dodge", dodgeDirectional, ref changed);
            AnimatorState upperBodyDraw = EnsureState(upperBody, "UpperBodyDrawWeapon", LoadAnimationClip(DrawWeaponSlotPath), ref changed);
            AnimatorState upperBodySheath = EnsureState(upperBody, "UpperBodySheathWeapon", LoadAnimationClip(SheathWeaponSlotPath), ref changed);
            AnimatorState upperBodyBlockStart = EnsureState(upperBody, "UpperBodyBlockStart", LoadAnimationClip(BlockStartSlotPath), ref changed);
            AnimatorState upperBodyBlockLoop = EnsureState(upperBody, "UpperBodyBlockLoop", LoadAnimationClip(BlockLoopSlotPath), ref changed);
            AnimatorState upperBodyBlockEnd = EnsureState(upperBody, "UpperBodyBlockEnd", LoadAnimationClip(BlockEndSlotPath), ref changed);
            AnimatorState upperBodyBlockHit = EnsureState(upperBody, "UpperBodyBlockHit", LoadAnimationClip(BlockHitSlotPath), ref changed);
            AnimatorState upperBodySpellAction1 = EnsureState(upperBody, "UpperBodySpellAction1", LoadAnimationClip(SpellSlot1Path), ref changed);
            AnimatorState upperBodySpellAction2 = EnsureState(upperBody, "UpperBodySpellAction2", LoadAnimationClip(SpellSlot2Path), ref changed);
            AnimatorState upperBodySpellAction3 = EnsureState(upperBody, "UpperBodySpellAction3", LoadAnimationClip(SpellSlot3Path), ref changed);
            AnimatorState upperBodySpellAction4 = EnsureState(upperBody, "UpperBodySpellAction4", LoadAnimationClip(SpellSlot4Path), ref changed);

            changed |= EnsureAnyStateTriggerTransition(root, blockStart, "TriggerBlockStart", 0.04f);
            changed |= EnsureExitTransition(blockStart, blockLoop, 0.05f, 0.92f);
            changed |= EnsureBoolTransition(blockLoop, blockEnd, "IsBlocking", false, 0.05f);
            changed |= EnsureExitTransition(blockEnd, idleCombat, 0.05f, 0.92f);
            changed |= EnsureAnyStateTriggerTransition(root, blockHit, "TriggerBlockHit", 0.02f);
            changed |= EnsureExitTransition(blockHit, blockLoop, 0.03f, 0.92f);
            changed |= EnsureAnyStateTriggerTransition(root, parryHit, "TriggerParryHit", 0.02f);
            changed |= EnsureExitTransition(parryHit, idleCombat, 0.03f, 0.92f);
            changed |= EnsureExitTransition(enterCombatIdle, idleCombat, 0.05f, 0.95f);
            changed |= EnsureExitTransition(enterCombatWalk, idleCombat, 0.05f, 0.95f);
            changed |= EnsureExitTransition(enterCombatRun, idleCombat, 0.05f, 0.95f);
            changed |= EnsureExitTransition(exitCombatIdle, idleWalkRunBlend, 0.05f, 0.95f);
            changed |= EnsureExitTransition(exitCombatWalk, idleWalkRunBlend, 0.05f, 0.95f);
            changed |= EnsureExitTransition(exitCombatRun, idleWalkRunBlend, 0.05f, 0.95f);

            changed |= EnsureAnyStateTriggerTransition(root, phasedMeleeStart, "TriggerPhasedMeleeStart", 0.04f);
            changed |= EnsureExitTransition(phasedMeleeStart, phasedMeleeLoop, 0.05f, 0.88f);
            changed |= EnsureBoolTransition(phasedMeleeLoop, idleCombat, "IsPhasedMeleeActive", false, 0.05f);
            changed |= EnsureAnyStateTriggerTransition(root, phasedMeleeEnd, "TriggerPhasedMeleeEnd", 0.03f);
            changed |= EnsureExitTransition(phasedMeleeEnd, idleCombat, 0.05f, 0.92f);
            changed |= EnsureAnyStateTriggerTransition(root, dodge, "TriggerDodge", 0.02f);
            changed |= RemoveUnconditionalTransition(dodge, idleCombat);
            changed |= RemoveUnconditionalTransition(dodge, idleWalkRunBlend);
            changed |= RemoveSingleBoolTransition(dodge, idleCombat, "InCombat", true);
            changed |= RemoveSingleBoolTransition(dodge, idleWalkRunBlend, "InCombat", false);
            changed |= EnsureTwoBoolExitTransition(dodge, idleCombat, "InCombat", true, "Grounded", true, 0.04f, 0.92f);
            changed |= EnsureTwoBoolExitTransition(dodge, idleWalkRunBlend, "InCombat", false, "Grounded", true, 0.04f, 0.92f);
            changed |= EnsureTwoBoolExitTransition(dodge, inAirCombat, "InCombat", true, "FreeFall", true, 0.04f, 0.92f);
            changed |= EnsureTwoBoolExitTransition(dodge, inAir, "InCombat", false, "FreeFall", true, 0.04f, 0.92f);

            changed |= EnsureAnyStateTriggerTransition(root, knockdownStart, "TriggerKnockdown", 0.03f);
            changed |= EnsureExitTransition(knockdownStart, knockdownLoop, 0.04f, 0.92f);
            changed |= EnsureAnyStateTriggerTransition(root, getUp, "TriggerGetUp", 0.03f);
            changed |= EnsureExitTransition(getUp, idleCombat, 0.05f, 0.92f);
            changed |= EnsureAnyStateTriggerTransition(root, hardCrowdControlLoop, "TriggerHardCrowdControl", 0.03f);
            changed |= EnsureBoolTransition(hardCrowdControlLoop, idleCombat, "IsHardCrowdControlled", false, 0.05f);
            changed |= EnsureAnyStateTriggerTransition(root, staggerF, "TriggerStaggerF", 0.02f);
            changed |= EnsureAnyStateTriggerTransition(root, staggerB, "TriggerStaggerB", 0.02f);
            changed |= EnsureAnyStateTriggerTransition(root, staggerL, "TriggerStaggerL", 0.02f);
            changed |= EnsureAnyStateTriggerTransition(root, staggerR, "TriggerStaggerR", 0.02f);
            changed |= EnsureExitTransition(staggerF, idleCombat, 0.04f, 0.92f);
            changed |= EnsureExitTransition(staggerB, idleCombat, 0.04f, 0.92f);
            changed |= EnsureExitTransition(staggerL, idleCombat, 0.04f, 0.92f);
            changed |= EnsureExitTransition(staggerR, idleCombat, 0.04f, 0.92f);

            changed |= EnsureExitTransition(upperBodyDraw, upperBodyEmpty, 0.05f, 0.95f);
            changed |= EnsureExitTransition(upperBodySheath, upperBodyEmpty, 0.05f, 0.95f);
            changed |= EnsureExitTransition(upperBodyBlockStart, upperBodyBlockLoop, 0.05f, 0.92f);
            changed |= EnsureBoolTransition(upperBodyBlockLoop, upperBodyBlockEnd, "IsBlocking", false, 0.05f);
            changed |= EnsureExitTransition(upperBodyBlockEnd, upperBodyEmpty, 0.05f, 0.92f);
            changed |= EnsureExitTransition(upperBodyBlockHit, upperBodyBlockLoop, 0.03f, 0.92f);
            changed |= EnsureExitTransition(upperBodySpellAction1, upperBodyEmpty, 0.05f, 0.9f);
            changed |= EnsureExitTransition(upperBodySpellAction2, upperBodyEmpty, 0.05f, 0.9f);
            changed |= EnsureExitTransition(upperBodySpellAction3, upperBodyEmpty, 0.05f, 0.9f);
            changed |= EnsureExitTransition(upperBodySpellAction4, upperBodyEmpty, 0.05f, 0.9f);

            if (!changed)
                return;

            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();
            Debug.Log("[CombatAnimatorControllerUpgrader] Arena_Character.controller upgraded for parry, block, dodge, knockdown, hard crowd control, stagger, and phased melee states.");
        }

        private static bool EnsureParameter(
            AnimatorController controller,
            string name,
            AnimatorControllerParameterType type)
        {
            foreach (var parameter in controller.parameters)
            {
                if (parameter.name == name && parameter.type == type)
                    return false;
            }

            controller.AddParameter(name, type);
            return true;
        }

        private static AnimatorState EnsureState(
            AnimatorStateMachine root,
            string stateName,
            Motion motion,
            ref bool changed)
        {
            AnimatorState? existing = FindState(root, stateName);
            AnimatorState state = existing ?? root.AddState(stateName);
            if (existing == null)
                changed = true;

            if (state.motion != motion)
            {
                state.motion = motion;
                changed = true;
            }

            if (!state.writeDefaultValues)
            {
                state.writeDefaultValues = true;
                changed = true;
            }

            return state;
        }

        private static AnimationClip LoadAnimationClip(string clipPath)
        {
            AnimationClip? clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(clipPath);
            if (clip == null)
                throw new System.InvalidOperationException($"Missing slot clip: {clipPath}");

            return clip;
        }

        private static BlendTree EnsureDirectionalBlendTree(
            AnimatorController controller,
            string treeName,
            string slotPrefix,
            ref bool changed)
        {
            return EnsureDirectionalBlendTree(controller, treeName, slotPrefix, "VelocityX", "VelocityZ", ref changed);
        }

        private static BlendTree EnsureDirectionalBlendTree(
            AnimatorController controller,
            string treeName,
            string slotPrefix,
            string blendParameterX,
            string blendParameterY,
            ref bool changed)
        {
            BlendTree tree = EnsureBlendTreeSubAsset(controller, treeName, ref changed);
            changed |= ConfigureDirectionalBlendTree(controller, tree, slotPrefix, blendParameterX, blendParameterY, ref changed);
            return tree;
        }

        private static BlendTree EnsureMotionSpeedBlendTree(
            AnimatorController controller,
            string treeName,
            Motion stationaryMotion,
            Motion movingMotion,
            ref bool changed)
        {
            BlendTree tree = EnsureBlendTreeSubAsset(controller, treeName, ref changed);
            changed |= ConfigureMotionSpeedBlendTree(tree, stationaryMotion, movingMotion);
            return tree;
        }

        private static BlendTree EnsureBlendTreeSubAsset(
            AnimatorController controller,
            string treeName,
            ref bool changed)
        {
            foreach (Object asset in AssetDatabase.LoadAllAssetsAtPath(ControllerPath))
            {
                if (asset is BlendTree existing && existing.name == treeName)
                    return existing;
            }

            var created = new BlendTree { name = treeName };
            AssetDatabase.AddObjectToAsset(created, controller);
            changed = true;
            return created;
        }

        private static AnimationClip EnsureEmbeddedSlotClip(
            AnimatorController controller,
            string clipName,
            ref bool changed)
        {
            foreach (Object asset in AssetDatabase.LoadAllAssetsAtPath(ControllerPath))
            {
                if (asset is AnimationClip existing && existing.name == clipName)
                    return existing;
            }

            var created = new AnimationClip
            {
                name = clipName,
                frameRate = 60f,
            };
            AssetDatabase.AddObjectToAsset(created, controller);
            changed = true;
            return created;
        }

        private static bool ConfigureDirectionalBlendTree(
            AnimatorController controller,
            BlendTree tree,
            string slotPrefix,
            string blendParameterX,
            string blendParameterY,
            ref bool changed)
        {
            bool treeChanged = false;
            treeChanged |= SetBlendTreeCore(tree, BlendTreeType.SimpleDirectional2D, blendParameterX, blendParameterY);

            ChildMotion[] children =
            {
                CreateChildMotion(EnsureEmbeddedSlotClip(controller, $"{slotPrefix}_N", ref changed), 0f, 1f),
                CreateChildMotion(EnsureEmbeddedSlotClip(controller, $"{slotPrefix}_NE", ref changed), 1f, 1f),
                CreateChildMotion(EnsureEmbeddedSlotClip(controller, $"{slotPrefix}_E", ref changed), 1f, 0f),
                CreateChildMotion(EnsureEmbeddedSlotClip(controller, $"{slotPrefix}_SE", ref changed), 1f, -1f),
                CreateChildMotion(EnsureEmbeddedSlotClip(controller, $"{slotPrefix}_S", ref changed), 0f, -1f),
                CreateChildMotion(EnsureEmbeddedSlotClip(controller, $"{slotPrefix}_SW", ref changed), -1f, -1f),
                CreateChildMotion(EnsureEmbeddedSlotClip(controller, $"{slotPrefix}_W", ref changed), -1f, 0f),
                CreateChildMotion(EnsureEmbeddedSlotClip(controller, $"{slotPrefix}_NW", ref changed), -1f, 1f),
            };

            if (!ChildMotionArraysEqual(tree.children, children))
            {
                tree.children = children;
                treeChanged = true;
            }

            return treeChanged;
        }

        private static bool ConfigureMotionSpeedBlendTree(
            BlendTree tree,
            Motion stationaryMotion,
            Motion movingMotion)
        {
            bool changed = false;
            changed |= SetBlendTreeCore(tree, BlendTreeType.Simple1D, "MotionSpeed", string.Empty);

            ChildMotion[] children =
            {
                CreateThresholdChildMotion(stationaryMotion, 0f),
                CreateThresholdChildMotion(movingMotion, BlockDirectionalMotionThreshold),
            };

            if (!ChildMotionArraysEqual(tree.children, children))
            {
                tree.children = children;
                changed = true;
            }

            return changed;
        }

        private static bool SetBlendTreeCore(
            BlendTree tree,
            BlendTreeType blendType,
            string blendParameter,
            string blendParameterY)
        {
            bool changed = false;
            if (tree.blendType != blendType)
            {
                tree.blendType = blendType;
                changed = true;
            }

            if (tree.blendParameter != blendParameter)
            {
                tree.blendParameter = blendParameter;
                changed = true;
            }

            if (tree.blendParameterY != blendParameterY)
            {
                tree.blendParameterY = blendParameterY;
                changed = true;
            }

            if (tree.useAutomaticThresholds)
            {
                tree.useAutomaticThresholds = false;
                changed = true;
            }

            return changed;
        }

        private static ChildMotion CreateChildMotion(Motion motion, float x, float y)
        {
            return new ChildMotion
            {
                motion = motion,
                position = new Vector2(x, y),
                timeScale = 1f,
                threshold = 0f,
                cycleOffset = 0f,
                mirror = false,
                directBlendParameter = string.Empty,
            };
        }

        private static ChildMotion CreateThresholdChildMotion(Motion motion, float threshold)
        {
            return new ChildMotion
            {
                motion = motion,
                threshold = threshold,
                timeScale = 1f,
                cycleOffset = 0f,
                mirror = false,
                directBlendParameter = string.Empty,
                position = Vector2.zero,
            };
        }

        private static bool ChildMotionArraysEqual(ChildMotion[] left, ChildMotion[] right)
        {
            if (left.Length != right.Length)
                return false;

            for (int i = 0; i < left.Length; i++)
            {
                if (left[i].motion != right[i].motion)
                    return false;
                if ((left[i].position - right[i].position).sqrMagnitude > 0.0001f)
                    return false;
                if (Mathf.Abs(left[i].threshold - right[i].threshold) > 0.0001f)
                    return false;
                if (Mathf.Abs(left[i].timeScale - right[i].timeScale) > 0.0001f)
                    return false;
                if (Mathf.Abs(left[i].cycleOffset - right[i].cycleOffset) > 0.0001f)
                    return false;
                if (left[i].mirror != right[i].mirror)
                    return false;
                if (left[i].directBlendParameter != right[i].directBlendParameter)
                    return false;
            }

            return true;
        }

        private static AnimatorState? FindState(AnimatorStateMachine root, string stateName)
        {
            foreach (ChildAnimatorState child in root.states)
            {
                if (child.state != null && child.state.name == stateName)
                    return child.state;
            }

            return null;
        }

        private static AnimatorStateMachine? FindLayerStateMachine(AnimatorController controller, string layerName)
        {
            foreach (AnimatorControllerLayer layer in controller.layers)
            {
                if (layer.name == layerName)
                    return layer.stateMachine;
            }

            return null;
        }

        private static bool EnsureAnyStateTriggerTransition(
            AnimatorStateMachine root,
            AnimatorState destination,
            string triggerName,
            float duration)
        {
            foreach (AnimatorStateTransition transition in root.anyStateTransitions)
            {
                if (transition.destinationState == destination && HasSingleCondition(transition, AnimatorConditionMode.If, triggerName))
                    return false;
            }

            AnimatorStateTransition added = root.AddAnyStateTransition(destination);
            added.hasExitTime = false;
            added.hasFixedDuration = true;
            added.duration = duration;
            added.canTransitionToSelf = false;
            added.AddCondition(AnimatorConditionMode.If, 0f, triggerName);
            return true;
        }

        private static bool EnsureExitTransition(
            AnimatorState source,
            AnimatorState destination,
            float duration,
            float exitTime)
        {
            foreach (AnimatorStateTransition transition in source.transitions)
            {
                if (transition.destinationState == destination && transition.conditions.Length == 0)
                    return false;
            }

            AnimatorStateTransition added = source.AddTransition(destination);
            added.hasExitTime = true;
            added.exitTime = exitTime;
            added.hasFixedDuration = true;
            added.duration = duration;
            added.canTransitionToSelf = false;
            return true;
        }

        private static bool EnsureBoolTransition(
            AnimatorState source,
            AnimatorState destination,
            string parameterName,
            bool requiredValue,
            float duration)
        {
            AnimatorConditionMode mode = requiredValue ? AnimatorConditionMode.If : AnimatorConditionMode.IfNot;
            foreach (AnimatorStateTransition transition in source.transitions)
            {
                if (transition.destinationState == destination && HasSingleCondition(transition, mode, parameterName))
                    return false;
            }

            AnimatorStateTransition added = source.AddTransition(destination);
            added.hasExitTime = false;
            added.hasFixedDuration = true;
            added.duration = duration;
            added.canTransitionToSelf = false;
            added.AddCondition(mode, 0f, parameterName);
            return true;
        }

        private static bool EnsureBoolExitTransition(
            AnimatorState source,
            AnimatorState destination,
            string parameterName,
            bool requiredValue,
            float duration,
            float exitTime)
        {
            AnimatorConditionMode mode = requiredValue ? AnimatorConditionMode.If : AnimatorConditionMode.IfNot;
            foreach (AnimatorStateTransition transition in source.transitions)
            {
                if (transition.destinationState == destination && HasSingleCondition(transition, mode, parameterName))
                    return false;
            }

            AnimatorStateTransition added = source.AddTransition(destination);
            added.hasExitTime = true;
            added.exitTime = exitTime;
            added.hasFixedDuration = true;
            added.duration = duration;
            added.canTransitionToSelf = false;
            added.AddCondition(mode, 0f, parameterName);
            return true;
        }

        private static bool EnsureTwoBoolExitTransition(
            AnimatorState source,
            AnimatorState destination,
            string firstParameterName,
            bool firstRequiredValue,
            string secondParameterName,
            bool secondRequiredValue,
            float duration,
            float exitTime)
        {
            AnimatorConditionMode firstMode = firstRequiredValue ? AnimatorConditionMode.If : AnimatorConditionMode.IfNot;
            AnimatorConditionMode secondMode = secondRequiredValue ? AnimatorConditionMode.If : AnimatorConditionMode.IfNot;
            foreach (AnimatorStateTransition transition in source.transitions)
            {
                if (transition.destinationState == destination
                    && HasTwoConditions(
                        transition,
                        firstMode,
                        firstParameterName,
                        secondMode,
                        secondParameterName))
                {
                    return false;
                }
            }

            AnimatorStateTransition added = source.AddTransition(destination);
            added.hasExitTime = true;
            added.exitTime = exitTime;
            added.hasFixedDuration = true;
            added.duration = duration;
            added.canTransitionToSelf = false;
            added.AddCondition(firstMode, 0f, firstParameterName);
            added.AddCondition(secondMode, 0f, secondParameterName);
            return true;
        }

        private static bool RemoveUnconditionalTransition(
            AnimatorState source,
            AnimatorState destination)
        {
            for (int i = source.transitions.Length - 1; i >= 0; i--)
            {
                AnimatorStateTransition transition = source.transitions[i];
                if (transition.destinationState != destination || transition.conditions.Length != 0)
                    continue;

                source.RemoveTransition(transition);
                Object.DestroyImmediate(transition, true);
                return true;
            }

            return false;
        }

        private static bool RemoveSingleBoolTransition(
            AnimatorState source,
            AnimatorState destination,
            string parameterName,
            bool requiredValue)
        {
            AnimatorConditionMode mode = requiredValue ? AnimatorConditionMode.If : AnimatorConditionMode.IfNot;
            bool changed = false;
            for (int i = source.transitions.Length - 1; i >= 0; i--)
            {
                AnimatorStateTransition transition = source.transitions[i];
                if (transition.destinationState != destination || !HasSingleCondition(transition, mode, parameterName))
                    continue;

                source.RemoveTransition(transition);
                Object.DestroyImmediate(transition, true);
                changed = true;
            }

            return changed;
        }

        private static bool HasSingleCondition(
            AnimatorStateTransition transition,
            AnimatorConditionMode mode,
            string parameterName)
        {
            return transition.conditions.Length == 1
                   && transition.conditions[0].mode == mode
                   && transition.conditions[0].parameter == parameterName;
        }

        private static bool HasTwoConditions(
            AnimatorStateTransition transition,
            AnimatorConditionMode firstMode,
            string firstParameterName,
            AnimatorConditionMode secondMode,
            string secondParameterName)
        {
            if (transition.conditions.Length != 2)
                return false;

            return HasCondition(transition.conditions[0], firstMode, firstParameterName)
                       && HasCondition(transition.conditions[1], secondMode, secondParameterName)
                   || HasCondition(transition.conditions[0], secondMode, secondParameterName)
                       && HasCondition(transition.conditions[1], firstMode, firstParameterName);
        }

        private static bool HasCondition(
            AnimatorCondition condition,
            AnimatorConditionMode mode,
            string parameterName)
        {
            return condition.mode == mode && condition.parameter == parameterName;
        }
    }
}
