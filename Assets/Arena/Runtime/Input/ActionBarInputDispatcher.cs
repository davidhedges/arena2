#nullable enable

using System.Collections.Generic;
using Arena.Combat;
using Arena.Debugging;
using Arena.Entity;
using Arena.Network;
using Arena.Simulation;
using SpacetimeDB.Types;

namespace Arena.Input
{
    public static class ActionBarInputDispatcher
    {
        public static void ProcessSelectableBindings(
            DbConnection conn,
            LocalPlayerInputSource input,
            bool shiftHeld,
            SpellInputHandler spellInput)
        {
            var consumedPressKeys = new HashSet<UnityEngine.KeyCode>();

            foreach (ActionBarSlotBinding binding in DisciplineBarKeymap.SelectableBindings)
            {
                if (binding.RequiresShift != shiftHeld) continue;
                if (consumedPressKeys.Contains(binding.KeyCode)) continue;
                if (!input.WasKeyPressedThisFrame(binding.KeyCode)) continue;

                ActiveActionBarAction resolved = ActiveActionBarResolver.ResolveGlobalActionBarAction(
                    conn,
                    conn.Identity,
                    binding.SlotId);
                if (!resolved.HasAssignedAction)
                {
                    ActionBarTrace.Trace(
                        $"{binding.KeyLabel} -> {binding.SlotId} unresolved (assigned={resolved.HasAssignedAction})");
                    continue;
                }

                consumedPressKeys.Add(binding.KeyCode);
                TryTrigger(resolved, conn, spellInput, binding.KeyLabel, binding.SlotId);
            }

            foreach (SpellbookSlotBinding binding in SpellbookKeymap.SelectableBindings)
            {
                if (binding.RequiresShift != shiftHeld) continue;
                if (consumedPressKeys.Contains(binding.KeyCode)) continue;
                if (!input.WasKeyPressedThisFrame(binding.KeyCode)) continue;

                string slotId = SpellbookKeymap.SlotIdForIndex(binding.SlotIndex);
                ActiveActionBarAction resolved = ActiveActionBarResolver.ResolveEquippedSpellbookAction(
                    conn,
                    conn.Identity,
                    binding.SlotIndex);
                if (!resolved.HasAssignedAction)
                {
                    ActionBarTrace.Trace(
                        $"{binding.KeyLabel} -> {slotId} unresolved (assigned={resolved.HasAssignedAction})");
                    continue;
                }
                consumedPressKeys.Add(binding.KeyCode);

                TryTrigger(resolved, conn, spellInput, binding.KeyLabel, slotId);
            }

            foreach (ActionBarSlotBinding binding in ActionBarKeymap.SelectableBindings)
            {
                if (binding.RequiresShift != shiftHeld) continue;
                if (consumedPressKeys.Contains(binding.KeyCode)) continue;
                if (!input.WasKeyPressedThisFrame(binding.KeyCode)) continue;

                ActiveActionBarAction resolved = ActiveActionBarResolver.ResolveActiveSelectableAction(
                    conn,
                    conn.Identity,
                    binding.SlotId);
                if (!resolved.HasAssignedAction)
                {
                    ActionBarTrace.Trace(
                        $"{binding.KeyLabel} -> {binding.SlotId} unresolved (assigned={resolved.HasAssignedAction})");
                    continue;
                }
                consumedPressKeys.Add(binding.KeyCode);

                TryTrigger(resolved, conn, spellInput, binding.KeyLabel, binding.SlotId);
            }

            var consumedReleaseKeys = new HashSet<UnityEngine.KeyCode>();
            foreach (SpellbookSlotBinding binding in SpellbookKeymap.SelectableBindings)
            {
                if (binding.RequiresShift != shiftHeld) continue;
                if (consumedReleaseKeys.Contains(binding.KeyCode)) continue;
                if (!input.WasKeyReleasedThisFrame(binding.KeyCode)) continue;

                ActiveActionBarAction resolved = ActiveActionBarResolver.ResolveEquippedSpellbookAction(
                    conn,
                    conn.Identity,
                    binding.SlotIndex);
                string? actionId = resolved.ActionId;
                if (string.IsNullOrWhiteSpace(actionId)) continue;
                consumedReleaseKeys.Add(binding.KeyCode);
                if (!resolved.IsSpellAbility)
                    continue;
                if (!SpellDefinitionContracts.CastsOnRelease(GetSpellDefinition(conn, actionId))) continue;
                ActionBarTrace.Trace($"{binding.KeyLabel} release -> cast release {actionId}");
                CastActionToken token = LocalCombatState.Instance.CurrentCastTokenForRelease(actionId);
                conn.Reducers.ReleaseCastRequest(
                    actionId,
                    token.PredictedCastId,
                    token.ClientActionSeq);
            }

            foreach (ActionBarSlotBinding binding in ActionBarKeymap.SelectableBindings)
            {
                if (binding.RequiresShift != shiftHeld) continue;
                if (consumedReleaseKeys.Contains(binding.KeyCode)) continue;

                ActiveActionBarAction resolved = ActiveActionBarResolver.ResolveActiveSelectableAction(
                    conn,
                    conn.Identity,
                    binding.SlotId);
                if (resolved.IsFixed)
                {
                    consumedReleaseKeys.Add(binding.KeyCode);
                    bool isHeld = input.IsKeyHeldThisFrame(binding.KeyCode);
                    if (input.WasKeyReleasedThisFrame(binding.KeyCode))
                    {
                        FixedActionDispatcher.TryRelease(resolved, conn);
                    }
                    else
                    {
                        FixedActionDispatcher.ReconcileHeldState(resolved, conn, isHeld);
                    }
                    continue;
                }
                string? actionId = resolved.ActionId;
                if (string.IsNullOrWhiteSpace(actionId)) continue;
                consumedReleaseKeys.Add(binding.KeyCode);
                if (!resolved.IsSpellAbility)
                    continue;
                if (!SpellDefinitionContracts.CastsOnRelease(GetSpellDefinition(conn, actionId))) continue;
                if (!input.WasKeyReleasedThisFrame(binding.KeyCode)) continue;
                ActionBarTrace.Trace($"{binding.KeyLabel} release -> cast release {actionId}");
                CastActionToken token = LocalCombatState.Instance.CurrentCastTokenForRelease(actionId);
                conn.Reducers.ReleaseCastRequest(
                    actionId,
                    token.PredictedCastId,
                    token.ClientActionSeq);
            }
        }

        public static bool TryTrigger(ActiveActionBarAction action, DbConnection? conn)
        {
            return TryTrigger(action, conn, SpellInputHandler.Instance, string.Empty, action.SlotId);
        }

        private static bool TryTrigger(
            ActiveActionBarAction action,
            DbConnection? conn,
            SpellInputHandler? spellInput,
            string keyLabel,
            string slotId)
        {
            if (conn == null)
                return false;

            if (LingeringShadeInput.TryConsumeRecast(conn, action))
                return true;

            if (!action.CanTrigger)
                return false;

            if (action.IsCombatDisciplineSwitch)
            {
                if (!string.IsNullOrWhiteSpace(keyLabel))
                    ActionBarTrace.Trace($"{keyLabel} -> {slotId} discipline={action.ActionId}");
                conn.Reducers.SetCombatDiscipline(action.ActionId);
                return true;
            }

            if (action.IsFixed)
            {
                return FixedActionDispatcher.TryTrigger(action, conn);
            }

            if (!string.IsNullOrWhiteSpace(keyLabel))
            {
                ActionBarTrace.Trace(
                    $"{keyLabel} -> {slotId} ability={action.AbilityId} kind={action.AbilityKind} authored={action.AuthoredActionId} runtime={action.ActionId}");
            }

            if (action.IsMeleeAbility)
            {
                return TryTriggerSelectableMeleeAction(conn, action.ActionId);
            }

            if (action.IsAutoAttackReplacementAbility)
            {
                ActionBarTrace.Trace($"auto-attack replacement dispatch arming {action.AbilityId}");
                conn.Reducers.ArmAutoAttackReplacement(action.AbilityId);
                // The armed replacement swaps the next swing's strike
                // server-side; local swing scheduling can't know the outcome,
                // so it degrades to CAST-driven playback until that swing
                // arrives (netcode design review S6).
                Arena.Presentation.AutoAttackSwingScheduler.NotifyAutoAttackReplacementArmed();
                return true;
            }

            if (action.IsCombatModeToggleAbility)
            {
                return CombatModeActionDispatcher.TryTrigger(conn, action);
            }

            if (action.IsMovementAbility)
            {
                return spellInput?.TryTriggerMovementFromActionBar(conn, action) == true;
            }

            if (!action.IsSpellAbility)
            {
                ActionBarTrace.Trace(
                    $"action-bar dispatch rejected unsupported ability kind '{action.AbilityKind}' for {action.AbilityId}");
                return false;
            }

            return spellInput?.TryTriggerSpellFromActionBar(conn, action.ActionId) == true;
        }

        private static SpellDefinition? GetSpellDefinition(DbConnection conn, string spellId)
        {
            return conn.Db.SpellDefinition.Kind.Find(spellId);
        }

        private static bool TryTriggerSelectableMeleeAction(DbConnection conn, string actionId)
        {
            return TryTriggerMeleeAction(conn, actionId, "selectable");
        }

        private static bool TryTriggerMeleeAction(
            DbConnection conn,
            string actionId,
            string source)
        {
            PlayerEntity? entity = EntityRegistry.Instance?.LocalPlayerEntity;
            if (entity == null || entity.IsDestroyed || !entity.IsAlive)
            {
                ActionBarTrace.Trace($"melee dispatch unavailable for {source}:{actionId} (no live local entity)");
                return false;
            }

            string combatProfile = CombatProfileResolver.ResolveForEntity(conn, entity);
            if (CombatActionIds.FindMeleeDefinition(conn, combatProfile, actionId) == null)
            {
                ActionBarTrace.Trace($"melee dispatch unavailable for {source}:{actionId} profile={combatProfile} (missing definition)");
                return false;
            }

            bool accepted = MeleeInputHandler.Instance?.TryTriggerAction(conn, entity, actionId) == true;
            if (!accepted)
                ActionBarTrace.Trace($"melee dispatch declined locally for {source}:{actionId}");
            return accepted;
        }
    }
}
