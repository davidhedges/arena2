#nullable enable
using System.Collections.Generic;
using UnityEngine;

namespace Arena.Presentation
{
    internal sealed class CombatAnimationSetBinder
    {
        private readonly List<KeyValuePair<AnimationClip, AnimationClip>> _overridePairs = new();

        public void Bind(CombatAnimationSet set, AnimatorOverrideController overrideController)
        {
            var clipMap = new Dictionary<string, AnimationClip>(128);

            MapBaseLocomotion(clipMap, set);

            MapCardinalSet(clipMap, "slot_jump_start",         set.jumpStart);
            MapCardinalSet(clipMap, "slot_jump_start_combat",  set.jumpStartCombat);
            if (set.freeFall       != null) clipMap["slot_freefall"]         = set.freeFall;
            if (set.freeFallCombat != null) clipMap["slot_freefall_combat"]  = set.freeFallCombat;
            MapCardinalSet(clipMap, "slot_jump_land",          set.jumpLand);
            MapCardinalSet(clipMap, "slot_jump_land_combat",   set.jumpLandCombat);

            if (set.enterCombatIdle != null) clipMap["slot_enter_combat_idle"] = set.enterCombatIdle;
            if (set.enterCombatWalk != null) clipMap["slot_enter_combat_walk"] = set.enterCombatWalk;
            if (set.enterCombatRun  != null) clipMap["slot_enter_combat_run"]  = set.enterCombatRun;
            if (set.exitCombatIdle  != null) clipMap["slot_exit_combat_idle"]  = set.exitCombatIdle;
            if (set.exitCombatWalk  != null) clipMap["slot_exit_combat_walk"]  = set.exitCombatWalk;
            if (set.exitCombatRun   != null) clipMap["slot_exit_combat_run"]   = set.exitCombatRun;
            if (set.DrawWeaponClip   != null) clipMap["slot_draw_weapon"]   = set.DrawWeaponClip;
            if (set.SheathWeaponClip != null) clipMap["slot_sheath_weapon"] = set.SheathWeaponClip;

            MapDirectionalSet(clipMap, "slot_block_walk_start", set.blockWalkStart, set.blockStart);
            MapDirectionalSet(clipMap, "slot_block_walk_loop", set.blockWalkLoop, set.blockLoop);
            MapDirectionalSet(clipMap, "slot_block_walk_end", set.blockWalkEnd, set.blockEnd);
            if (set.parryStart != null) clipMap["slot_parry_start"] = set.parryStart;
            if (set.parryHit   != null) clipMap["slot_parry_hit"] = set.parryHit;
            if (set.blockStart    != null) clipMap["slot_block_start"] = set.blockStart;
            if (set.blockLoop     != null) clipMap["slot_block_loop"] = set.blockLoop;
            if (set.blockEnd      != null) clipMap["slot_block_end"] = set.blockEnd;
            if (set.blockHit      != null) clipMap["slot_block_hit"] = set.blockHit;
            if (set.blockHitBreak != null) clipMap["slot_block_hit_break"] = set.blockHitBreak;

            for (int strikeIndex = 1; strikeIndex <= CombatAnimationSet.AnimatorStrikeBankCount; strikeIndex++)
            {
                AnimationClip? strikeClip = set.GetStrikeClip(strikeIndex);
                if (strikeClip != null)
                    clipMap[$"slot_strike_{strikeIndex}"] = strikeClip;
            }

            if (set.hitF  != null) clipMap["slot_hit_F"] = set.hitF;
            if (set.hitB  != null) clipMap["slot_hit_B"] = set.hitB;
            if (set.hitL  != null) clipMap["slot_hit_L"] = set.hitL;
            if (set.hitR  != null) clipMap["slot_hit_R"] = set.hitR;
            if (set.staggerF != null) clipMap["slot_stagger_F"] = set.staggerF;
            if (set.staggerB != null) clipMap["slot_stagger_B"] = set.staggerB;
            if (set.staggerL != null) clipMap["slot_stagger_L"] = set.staggerL;
            if (set.staggerR != null) clipMap["slot_stagger_R"] = set.staggerR;
            if (set.knockdownStart != null) clipMap["slot_knockdown_start"] = set.knockdownStart;
            if (set.knockdownLoop  != null) clipMap["slot_knockdown_loop"] = set.knockdownLoop;
            if (set.getUp          != null) clipMap["slot_get_up"] = set.getUp;
            if (set.stunLoop       != null) clipMap["slot_hard_crowd_control_loop"] = set.stunLoop;

            if (set.death != null) clipMap["slot_death"] = set.death;

            ApplyClipMap(overrideController, clipMap);
        }

        public void ApplyLocomotionMode(
            CombatAnimationSet set,
            string? modeId,
            AnimatorOverrideController overrideController)
        {
            var clipMap = new Dictionary<string, AnimationClip>(64);
            MapBaseLocomotion(clipMap, set);

            if (set.TryGetLocomotionModeOverride(modeId, out CombatAnimationLocomotionModeOverride? modeOverride)
                && modeOverride != null)
            {
                MapLocomotionModeOverride(clipMap, modeOverride);
            }

            ApplyClipMap(overrideController, clipMap);
        }

        public void ApplyDirectionalOverrideSet(
            AnimatorOverrideController overrideController,
            string prefix,
            DirectionalClipSet set)
        {
            var clipMap = new Dictionary<string, AnimationClip>(8);
            MapOptionalClip(clipMap, $"{prefix}_N", set.n);
            MapOptionalClip(clipMap, $"{prefix}_NE", set.ne);
            MapOptionalClip(clipMap, $"{prefix}_E", set.e);
            MapOptionalClip(clipMap, $"{prefix}_SE", set.se);
            MapOptionalClip(clipMap, $"{prefix}_S", set.s);
            MapOptionalClip(clipMap, $"{prefix}_SW", set.sw);
            MapOptionalClip(clipMap, $"{prefix}_W", set.w);
            MapOptionalClip(clipMap, $"{prefix}_NW", set.nw);
            ApplyClipMap(overrideController, clipMap);
        }

        public void ApplyPreferredClipOverride(
            AnimatorOverrideController overrideController,
            string slotName,
            AnimationClip? preferredClip,
            AnimationClip? fallbackClip)
        {
            ApplyOptionalOverride(overrideController, slotName, preferredClip ?? fallbackClip);
        }

        public void ApplyHitClipOverrides(
            AnimatorOverrideController overrideController,
            CombatAnimationSet set,
            bool grounded,
            bool inCombat)
        {
            HitReactionClipSet clips = set.ResolveHitReactionClips(grounded, inCombat);
            var clipMap = new Dictionary<string, AnimationClip>(4);
            MapOptionalClip(clipMap, "slot_hit_F", clips.Forward);
            MapOptionalClip(clipMap, "slot_hit_B", clips.Back);
            MapOptionalClip(clipMap, "slot_hit_L", clips.Left);
            MapOptionalClip(clipMap, "slot_hit_R", clips.Right);
            ApplyClipMap(overrideController, clipMap);
        }

        private void ApplyClipMap(
            AnimatorOverrideController overrideController,
            IReadOnlyDictionary<string, AnimationClip> clipMap)
        {
            if (clipMap.Count == 0)
                return;

            _overridePairs.Clear();
            overrideController.GetOverrides(_overridePairs);
            bool changed = false;
            for (int index = 0; index < _overridePairs.Count; index++)
            {
                KeyValuePair<AnimationClip, AnimationClip> pair = _overridePairs[index];
                if (pair.Key == null
                    || !clipMap.TryGetValue(pair.Key.name, out AnimationClip newClip)
                    || ReferenceEquals(pair.Value, newClip))
                {
                    continue;
                }

                _overridePairs[index] = new KeyValuePair<AnimationClip, AnimationClip>(pair.Key, newClip);
                changed = true;
            }

            if (changed)
                overrideController.ApplyOverrides(_overridePairs);
        }

        private static void MapBaseLocomotion(Dictionary<string, AnimationClip> clipMap, CombatAnimationSet set)
        {
            if (set.locomotionIdle       != null) clipMap["slot_loco_idle"]        = set.locomotionIdle;
            if (set.locomotionIdleCombat != null) clipMap["slot_loco_idle_combat"] = set.locomotionIdleCombat;

            MapDirectionalSet(clipMap, "slot_walk",              set.walk);
            MapDirectionalSet(clipMap, "slot_walk_combat",       set.walkCombat);
            MapDirectionalSet(clipMap, "slot_run",               set.run);
            MapDirectionalSet(clipMap, "slot_run_combat",        set.runCombat);

            MapDirectionalSet(clipMap, "slot_walk_stop",         set.walkStop);
            MapDirectionalSet(clipMap, "slot_walk_stop_combat",  set.walkStopCombat);
            MapDirectionalSet(clipMap, "slot_run_stop",          set.runStop);
            MapDirectionalSet(clipMap, "slot_run_stop_combat",   set.runStopCombat);

            if (set.turn90L        != null) clipMap["slot_turn_90_L"]          = set.turn90L;
            if (set.turn90R        != null) clipMap["slot_turn_90_R"]          = set.turn90R;
            if (set.turn180L       != null) clipMap["slot_turn_180_L"]         = set.turn180L;
            if (set.turn180R       != null) clipMap["slot_turn_180_R"]         = set.turn180R;
            if (set.turn90CombatL  != null) clipMap["slot_turn_combat_90_L"]   = set.turn90CombatL;
            if (set.turn90CombatR  != null) clipMap["slot_turn_combat_90_R"]   = set.turn90CombatR;
            if (set.turn180CombatL != null) clipMap["slot_turn_combat_180_L"]  = set.turn180CombatL;
            if (set.turn180CombatR != null) clipMap["slot_turn_combat_180_R"]  = set.turn180CombatR;
        }

        private static void MapLocomotionModeOverride(
            Dictionary<string, AnimationClip> clipMap,
            CombatAnimationLocomotionModeOverride modeOverride)
        {
            if (modeOverride.locomotionIdle       != null) clipMap["slot_loco_idle"]        = modeOverride.locomotionIdle;
            if (modeOverride.locomotionIdleCombat != null) clipMap["slot_loco_idle_combat"] = modeOverride.locomotionIdleCombat;

            MapDirectionalSet(clipMap, "slot_walk",              modeOverride.walk);
            MapDirectionalSet(clipMap, "slot_walk_combat",       modeOverride.walkCombat);
            MapDirectionalSet(clipMap, "slot_run",               modeOverride.run);
            MapDirectionalSet(clipMap, "slot_run_combat",        modeOverride.runCombat);

            MapDirectionalSet(clipMap, "slot_walk_stop",         modeOverride.walkStop);
            MapDirectionalSet(clipMap, "slot_walk_stop_combat",  modeOverride.walkStopCombat);
            MapDirectionalSet(clipMap, "slot_run_stop",          modeOverride.runStop);
            MapDirectionalSet(clipMap, "slot_run_stop_combat",   modeOverride.runStopCombat);

            if (modeOverride.turn90L        != null) clipMap["slot_turn_90_L"]          = modeOverride.turn90L;
            if (modeOverride.turn90R        != null) clipMap["slot_turn_90_R"]          = modeOverride.turn90R;
            if (modeOverride.turn180L       != null) clipMap["slot_turn_180_L"]         = modeOverride.turn180L;
            if (modeOverride.turn180R       != null) clipMap["slot_turn_180_R"]         = modeOverride.turn180R;
            if (modeOverride.turn90CombatL  != null) clipMap["slot_turn_combat_90_L"]   = modeOverride.turn90CombatL;
            if (modeOverride.turn90CombatR  != null) clipMap["slot_turn_combat_90_R"]   = modeOverride.turn90CombatR;
            if (modeOverride.turn180CombatL != null) clipMap["slot_turn_combat_180_L"]  = modeOverride.turn180CombatL;
            if (modeOverride.turn180CombatR != null) clipMap["slot_turn_combat_180_R"]  = modeOverride.turn180CombatR;
        }

        private static void MapDirectionalSet(
            Dictionary<string, AnimationClip> clipMap,
            string prefix,
            DirectionalClipSet set)
        {
            if (set.n  != null) clipMap[$"{prefix}_N"]  = set.n;
            if (set.ne != null) clipMap[$"{prefix}_NE"] = set.ne;
            if (set.e  != null) clipMap[$"{prefix}_E"]  = set.e;
            if (set.se != null) clipMap[$"{prefix}_SE"] = set.se;
            if (set.s  != null) clipMap[$"{prefix}_S"]  = set.s;
            if (set.sw != null) clipMap[$"{prefix}_SW"] = set.sw;
            if (set.w  != null) clipMap[$"{prefix}_W"]  = set.w;
            if (set.nw != null) clipMap[$"{prefix}_NW"] = set.nw;
        }

        private static void MapDirectionalSet(
            Dictionary<string, AnimationClip> clipMap,
            string prefix,
            DirectionalClipSet set,
            AnimationClip? fallbackClip)
        {
            MapOptionalClip(clipMap, $"{prefix}_N", set.n ?? fallbackClip);
            MapOptionalClip(clipMap, $"{prefix}_NE", set.ne ?? fallbackClip);
            MapOptionalClip(clipMap, $"{prefix}_E", set.e ?? fallbackClip);
            MapOptionalClip(clipMap, $"{prefix}_SE", set.se ?? fallbackClip);
            MapOptionalClip(clipMap, $"{prefix}_S", set.s ?? fallbackClip);
            MapOptionalClip(clipMap, $"{prefix}_SW", set.sw ?? fallbackClip);
            MapOptionalClip(clipMap, $"{prefix}_W", set.w ?? fallbackClip);
            MapOptionalClip(clipMap, $"{prefix}_NW", set.nw ?? fallbackClip);
        }

        private static void MapOptionalClip(
            Dictionary<string, AnimationClip> clipMap,
            string slotName,
            AnimationClip? clip)
        {
            if (clip != null)
                clipMap[slotName] = clip;
        }

        private static void MapCardinalSet(
            Dictionary<string, AnimationClip> clipMap,
            string prefix,
            CardinalClipSet set)
        {
            if (set.center != null) clipMap[prefix]           = set.center;
            if (set.n      != null) clipMap[$"{prefix}_N"]    = set.n;
            if (set.e      != null) clipMap[$"{prefix}_E"]    = set.e;
            if (set.s      != null) clipMap[$"{prefix}_S"]    = set.s;
            if (set.w      != null) clipMap[$"{prefix}_W"]    = set.w;
        }

        private static void ApplyOptionalOverride(
            AnimatorOverrideController overrideController,
            string slotName,
            AnimationClip? clip)
        {
            if (clip != null)
                overrideController[slotName] = clip;
        }
    }
}
