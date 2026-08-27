#nullable enable

using System.Collections.Generic;
using Arena.Combat;
using Arena.Debugging;
using Arena.Entity;
using Arena.Network;
using Arena.Presentation;
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

                ActiveActionBarAction resolved = ActiveActionBarResolver.ResolveCombatDisciplineSwitchAction(
                    conn,
                    conn.Identity,
                    binding.SlotId);
                if (!resolved.HasAssignedAction)
                {
                    ActionBarTrace.Diagnostic(
                        $"{binding.KeyLabel} -> {binding.SlotId} unresolved (assigned={resolved.HasAssignedAction})");
                    continue;
                }

                consumedPressKeys.Add(binding.KeyCode);
                TryTrigger(resolved, conn, spellInput, binding.KeyLabel, binding.SlotId);
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
                    ActionBarTrace.Diagnostic(
                        $"{binding.KeyLabel} -> {binding.SlotId} unresolved (assigned={resolved.HasAssignedAction})");
                    continue;
                }
                consumedPressKeys.Add(binding.KeyCode);

                TryTrigger(resolved, conn, spellInput, binding.KeyLabel, binding.SlotId);
            }

            var consumedReleaseKeys = new HashSet<UnityEngine.KeyCode>();
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
                if (input.WasKeyReleasedThisFrame(binding.KeyCode)
                    && TryReleaseHeldMeleeChannel(conn, resolved, binding.KeyLabel))
                {
                    continue;
                }
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

        /// Key-up for a holdable melee channel (Rapid Fire and anything else
        /// authoring `melee_channel.holdable`). The published MeleeDefinition
        /// carries the flag, so the client only sends the reducer for strikes
        /// that can actually be cut short.
        private static bool TryReleaseHeldMeleeChannel(
            DbConnection conn,
            ActiveActionBarAction action,
            string keyLabel)
        {
            if (!action.IsMeleeAbility || string.IsNullOrWhiteSpace(action.ActionId))
                return false;

            string combatProfile = CombatProfileResolver.ResolveForOwner(conn, conn.Identity);
            MeleeDefinition? definition =
                CombatActionIds.FindMeleeDefinition(conn, combatProfile, action.ActionId);
            if (definition == null || !definition.Holdable)
                return false;

            ActionBarTrace.Trace($"{keyLabel} release -> release melee channel {action.ActionId}");
            conn.Reducers.ReleaseMeleeChannel(action.AbilityId ?? string.Empty);
            return true;
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

            LogDispatchSnapshot(action, conn, keyLabel, slotId);

            if (LingeringShadeInput.TryConsumeRecast(conn, action))
                return true;

            if (!action.CanTrigger)
            {
                string rejectedActionId = string.IsNullOrWhiteSpace(action.ActionId)
                    ? action.AuthoredActionId
                    : action.ActionId;
                ActionBarTrace.Diagnostic(
                    $"dispatch stopped before handler slot={slotId} action={rejectedActionId} " +
                    $"hasAssigned={action.HasAssignedAction} isAvailable={action.IsAvailable}");
                return false;
            }

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
                ActionBarTrace.Diagnostic(
                    $"melee dispatch stopped source={source} action={actionId} reason=no_live_local_entity");
                return false;
            }

            string combatProfile = CombatProfileResolver.ResolveForEntity(conn, entity);
            if (CombatActionIds.FindMeleeDefinition(conn, combatProfile, actionId) == null)
            {
                ActionBarTrace.Diagnostic(
                    $"melee dispatch stopped source={source} action={actionId} " +
                    $"profile={combatProfile} reason=missing_melee_definition");
                return false;
            }

            MeleeInputHandler? handler = MeleeInputHandler.Instance;
            if (handler == null)
            {
                ActionBarTrace.Diagnostic(
                    $"melee dispatch stopped source={source} action={actionId} reason=missing_input_handler");
                return false;
            }

            bool accepted = handler.TryTriggerAction(conn, entity, actionId);
            ActionBarTrace.Diagnostic(
                $"melee handler returned source={source} action={actionId} accepted={accepted}");
            return accepted;
        }

        private static void LogDispatchSnapshot(
            ActiveActionBarAction action,
            DbConnection conn,
            string keyLabel,
            string slotId)
        {
            SpacetimeDB.Identity? owner = conn.Identity;
            string ownerProfile = CombatProfileResolver.ResolveForOwner(conn, owner);
            ActiveCombatDiscipline? discipline = owner.HasValue
                ? conn.Db.ActiveCombatDiscipline.Owner.Find(owner.Value)
                : null;
            EquipmentLoadout? equipment = owner.HasValue
                ? conn.Db.EquipmentLoadout.Owner.Find(owner.Value)
                : null;
            AbilityCatalog? ability = string.IsNullOrWhiteSpace(action.AbilityId)
                ? null
                : conn.Db.AbilityCatalog.AbilityId.Find(action.AbilityId);
            string abilityProfile = CombatProfileResolver.ResolveForAbility(conn, ability);
            MeleeDefinition? definition = action.IsMeleeAbility
                ? CombatActionIds.FindMeleeDefinition(conn, ownerProfile, action.ActionId)
                : null;
            CombatAnimationSet? animationSet = action.IsMeleeAbility
                ? CombatAnimationSetCatalog.Resolve(ownerProfile)
                : null;
            string directAnimationMapping = animationSet == null
                ? "<missing-set>"
                : animationSet.ResolveRuntimeSlotIdForStrikeReference(action.AuthoredActionId);
            PlayerEntity? entity = EntityRegistry.Instance?.LocalPlayerEntity;
            ICombatTargetEntity? target = TargetSelector.Instance?.SelectedTarget;

            ActionBarTrace.Diagnostic(
                $"press input={(string.IsNullOrWhiteSpace(keyLabel) ? "pointer" : keyLabel)} slot={slotId} " +
                $"kind={action.ActionKind}/{action.AbilityKind} ability={action.AbilityId} " +
                $"authored={action.AuthoredActionId} runtime={action.ActionId} " +
                $"assigned={action.HasAssignedAction} available={action.IsAvailable} canTrigger={action.CanTrigger} " +
                $"exactAssigned={action.HasAssignedAction} ownerProfile={ownerProfile} " +
                $"activeDiscipline={discipline?.DisciplineId ?? "<missing>"} " +
                $"activeProfile={discipline?.CombatProfileId ?? "<missing>"} " +
                $"abilityProfile={(string.IsNullOrWhiteSpace(abilityProfile) ? "<missing>" : abilityProfile)} " +
                $"catalogAction={ability?.ActionId ?? "<missing>"} " +
                $"mainHand={equipment?.MainHandItemId ?? "<missing>"} offHand={equipment?.OffHandItemId ?? "<missing>"} " +
                $"definition={(definition == null ? "<missing>" : $"{definition.Key}|comboFrom={definition.ComboFrom}")} " +
                $"animationSet={DescribeAnimationSet(animationSet)} directAnimationMapping={directAnimationMapping} " +
                $"entity={(entity == null ? "<missing>" : $"alive={entity.IsAlive},destroyed={entity.IsDestroyed}")} " +
                $"target={(target == null ? "<none>" : $"{target.DisplayName},alive={target.IsAlive},destroyed={target.IsDestroyed}")}");
        }

        private static string DescribeAnimationSet(CombatAnimationSet? set)
        {
            if (set == null)
                return "<missing>";

            var strikes = new List<string>(set.MeleeAttackCount);
            for (int strikeIndex = 1; strikeIndex <= set.MeleeAttackCount; strikeIndex++)
            {
                WeaponStrikeCombatAuthoring strike = set.GetStrikeCombat(strikeIndex);
                strikes.Add($"{strike.AuthoredStrikeIdOrDefault}>{strike.RuntimeSlotIdOrDefault}");
            }

            return $"{set.name}[{string.Join(",", strikes)}]";
        }
    }
}
