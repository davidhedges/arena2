#nullable enable

using Arena.Combat;
using Arena.Debugging;
using Arena.Entity;
using Arena.Network;
using Arena.Simulation;
using SpacetimeDB.Types;
using UnityEngine;

namespace Arena.Input
{
    public static class FixedActionDispatcher
    {
        public static bool IsActionBarVisible(string actionId, DbConnection? conn)
        {
            _ = conn;
            _ = actionId;
            return false;
        }

        public static bool IsVisible(string actionId, DbConnection? conn)
        {
            string normalized = WireIdentifier.Normalize(actionId);
            if (string.Equals(normalized, FixedActionIds.Dodge, System.StringComparison.Ordinal))
                return true;
            if (string.Equals(normalized, FixedActionIds.Parry, System.StringComparison.Ordinal))
                return true;

            return false;
        }

        public static void ProcessMovementBindings(DbConnection conn, LocalPlayerInputSource input)
        {
            if (input.WasKeyPressedThisFrame(MovementActionKeymap.DodgeKeyCode))
                TryTrigger(FixedActionIds.Dodge, conn);

            if (input.WasKeyPressedThisFrame(DefenseActionKeymap.ParryKeyCode))
                TryTrigger(FixedActionIds.Parry, conn);

            if (input.WasKeyReleasedThisFrame(DefenseActionKeymap.ParryKeyCode))
                TryRelease(FixedActionIds.Parry, conn);
            else
                ReconcileHeldState(FixedActionIds.Parry, conn, input.IsKeyHeldThisFrame(DefenseActionKeymap.ParryKeyCode));
        }

        public static bool IsEnabled(string actionId, DbConnection? conn)
        {
            if (!IsVisible(actionId, conn))
                return false;

            PlayerEntity? entity = EntityRegistry.Instance?.LocalPlayerEntity;
            if (entity == null || !entity.IsAlive)
                return false;
            if (SpellInputHandler.Instance?.IsAimActive == true)
                return false;

            long nowMs = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (LocalCombatState.Instance.ActiveCast is { } activeCast && nowMs < activeCast.endMs)
                return false;
            if (LocalCombatState.Instance.MovementAction is { } movementAction && nowMs < movementAction.recoveryUntilMs)
                return false;

            string normalized = WireIdentifier.Normalize(actionId);
            if (string.Equals(normalized, FixedActionIds.Dodge, System.StringComparison.Ordinal))
            {
                return LocalCombatState.Instance.FixedActionCharges.TryGetValue(normalized, out var charges)
                    && charges.CurrentCharges > 0;
            }

            if (string.Equals(normalized, FixedActionIds.Parry, System.StringComparison.Ordinal))
            {
                return conn != null
                    && !LocalDefensePrediction.IsBlocking(conn, entity)
                    && !LocalDefensePrediction.IsParrying(conn, entity);
            }

            if (LocalCombatState.Instance.SpellCooldowns.TryGetValue(normalized, out var cooldown)
                && cooldown.durationMs > 0
                && nowMs < cooldown.lastCastMs + cooldown.durationMs)
                return false;

            return true;
        }

        public static bool TryTrigger(string actionId, DbConnection? conn)
        {
            if (conn == null)
            {
                ActionBarTrace.Trace($"fixed action {WireIdentifier.Normalize(actionId)} rejected: no connection");
                return false;
            }

            if (!IsEnabled(actionId, conn))
            {
                TraceDisabledReason(actionId, conn);
                return false;
            }

            string normalized = WireIdentifier.Normalize(actionId);
            if (string.Equals(normalized, FixedActionIds.Dodge, System.StringComparison.Ordinal))
                return TryStartDodge(conn);
            if (string.Equals(normalized, FixedActionIds.Parry, System.StringComparison.Ordinal))
                return TryStartParry(conn);

            return false;
        }

        public static bool TryTrigger(ActiveActionBarAction action, DbConnection? conn)
        {
            if (!action.IsFixed)
                return false;

            string fixedActionId = string.IsNullOrWhiteSpace(action.ActionRefId)
                ? action.ActionId
                : action.ActionRefId;
            return TryTrigger(fixedActionId, conn);
        }

        public static bool TryRelease(string actionId, DbConnection? conn)
        {
            if (conn == null)
                return false;

            string normalized = WireIdentifier.Normalize(actionId);
            if (string.Equals(normalized, FixedActionIds.Parry, System.StringComparison.Ordinal))
                return TryStopParry(conn, "released");

            return false;
        }

        public static bool TryRelease(ActiveActionBarAction action, DbConnection? conn)
        {
            if (!action.IsFixed)
                return false;

            string fixedActionId = string.IsNullOrWhiteSpace(action.ActionRefId)
                ? action.ActionId
                : action.ActionRefId;
            return TryRelease(fixedActionId, conn);
        }

        public static void ReconcileHeldState(string actionId, DbConnection? conn, bool isHeld)
        {
            if (conn == null || isHeld)
                return;

            string normalized = WireIdentifier.Normalize(actionId);
            if (string.Equals(normalized, FixedActionIds.Parry, System.StringComparison.Ordinal))
                TryStopParry(conn, "not held");
        }

        public static void ReconcileHeldState(ActiveActionBarAction action, DbConnection? conn, bool isHeld)
        {
            if (!action.IsFixed)
                return;

            string fixedActionId = string.IsNullOrWhiteSpace(action.ActionRefId)
                ? action.ActionId
                : action.ActionRefId;
            ReconcileHeldState(fixedActionId, conn, isHeld);
        }

        private static bool TryStartDodge(DbConnection conn)
        {
            PlayerEntity? entity = EntityRegistry.Instance?.LocalPlayerEntity;
            LocalPlayerInputSource? input = entity?.GetLocalInputSource();
            if (entity == null || input == null)
                return false;

            uint effectiveInputTick = entity.GameObject.GetComponent<LocalMovementPredictionDriver>()?.NextMovementContextProposalTick
                ?? 0u;
            (uint inputTick, Vector3 pos, float yaw) = GetSnapshot(entity);
            ActionPredictionToken token = LocalCombatState.Instance.CreateActionPredictionToken(FixedActionIds.Dodge);
            conn.Reducers.StartDodge(
                effectiveInputTick,
                inputTick,
                pos.x,
                pos.y,
                pos.z,
                yaw,
                input.Move.y,
                input.Move.x,
                token.PredictedActionId,
                token.ClientActionSeq);
            ActionBarTrace.Trace("fixed action DODGE dispatched");
            return true;
        }

        private static bool TryStartParry(DbConnection conn)
        {
            PlayerEntity? entity = EntityRegistry.Instance?.LocalPlayerEntity;
            if (entity == null)
                return false;

            if (!entity.IsInCombat)
                entity.EnterCombatImmediate();

            (uint inputTick, Vector3 pos, float yaw) = GetSnapshot(entity);
            long nowMs = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            ActionPredictionToken token = LocalCombatState.Instance.CreateActionPredictionToken(FixedActionIds.Parry);
            entity.StartParry();
            LocalDefensePrediction.PredictParry(nowMs, token);
            conn.Reducers.StartParry(
                inputTick,
                pos.x,
                pos.y,
                pos.z,
                yaw,
                token.PredictedActionId,
                token.ClientActionSeq);
            ActionBarTrace.Trace(
                $"fixed action PARRY dispatched tick={inputTick} pos=({pos.x:F2},{pos.y:F2},{pos.z:F2}) yaw={yaw:F2}");
            return true;
        }

        private static bool TryStopParry(DbConnection conn, string reason)
        {
            PlayerEntity? entity = EntityRegistry.Instance?.LocalPlayerEntity;
            if (entity == null)
                return false;

            long nowMs = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (!LocalDefensePrediction.ShouldRequestParryStop(conn, entity, nowMs))
                return false;

            LocalDefensePrediction.RequestParryStop(entity);
            conn.Reducers.StopParry();
            ActionBarTrace.Trace($"fixed action PARRY stop dispatched ({reason})");
            return true;
        }

        public static void OnPredictedActionResultInsert(EventContext ctx, PredictedActionResult row)
        {
            _ = ctx;
            if (row.Family == PredictedActionFamily.Defense)
            {
                PlayerEntity? entity = EntityRegistry.Instance?.LocalPlayerEntity;
                if (entity != null)
                    LocalDefensePrediction.OnPredictedActionResult(row, entity);
            }
        }

        private static void TraceDisabledReason(string actionId, DbConnection conn)
        {
            string normalized = WireIdentifier.Normalize(actionId);
            if (!IsVisible(normalized, conn))
            {
                ActionBarTrace.Trace($"fixed action {normalized} rejected: not visible");
                return;
            }

            PlayerEntity? entity = EntityRegistry.Instance?.LocalPlayerEntity;
            if (entity == null)
            {
                ActionBarTrace.Trace($"fixed action {normalized} rejected: no local player entity");
                return;
            }
            if (!entity.IsAlive)
            {
                ActionBarTrace.Trace($"fixed action {normalized} rejected: local player dead");
                return;
            }
            if (SpellInputHandler.Instance?.IsAimActive == true)
            {
                ActionBarTrace.Trace($"fixed action {normalized} rejected: aim mode active");
                return;
            }

            long nowMs = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (LocalCombatState.Instance.ActiveCast is { } activeCast && nowMs < activeCast.endMs)
            {
                ActionBarTrace.Trace(
                    $"fixed action {normalized} rejected: active cast ends in {activeCast.endMs - nowMs}ms");
                return;
            }
            if (LocalCombatState.Instance.MovementAction is { } movementAction && nowMs < movementAction.recoveryUntilMs)
            {
                ActionBarTrace.Trace(
                    $"fixed action {normalized} rejected: movement action recovery ends in {movementAction.recoveryUntilMs - nowMs}ms");
                return;
            }

            if (string.Equals(normalized, FixedActionIds.Dodge, System.StringComparison.Ordinal)
                && (!LocalCombatState.Instance.FixedActionCharges.TryGetValue(normalized, out var charges)
                    || charges.CurrentCharges <= 0))
            {
                ActionBarTrace.Trace($"fixed action {normalized} rejected: no charges");
                return;
            }

            if (string.Equals(normalized, FixedActionIds.Parry, System.StringComparison.Ordinal))
            {
                if (LocalDefensePrediction.IsBlocking(conn, entity))
                {
                    ActionBarTrace.Trace($"fixed action {normalized} rejected: block active");
                    return;
                }
                if (LocalDefensePrediction.IsParrying(conn, entity))
                {
                    ActionBarTrace.Trace($"fixed action {normalized} rejected: parry already armed or cooling down");
                    return;
                }
            }

            if (LocalCombatState.Instance.SpellCooldowns.TryGetValue(normalized, out var cooldown)
                && cooldown.durationMs > 0
                && nowMs < cooldown.lastCastMs + cooldown.durationMs)
            {
                ActionBarTrace.Trace(
                    $"fixed action {normalized} rejected: cooldown ends in {cooldown.lastCastMs + cooldown.durationMs - nowMs}ms");
                return;
            }

            ActionBarTrace.Trace($"fixed action {normalized} rejected: local enable gate failed");
        }

        private static (uint inputTick, Vector3 pos, float yaw) GetSnapshot(PlayerEntity entity)
        {
            uint inputTick = 0;
            Vector3 pos = entity.GameObject.transform.position;
            float yaw = entity.GameObject.transform.eulerAngles.y * Mathf.Deg2Rad;

            LocalPlayerStateProvider? stateProvider = entity.GetLocalStateProvider();
            if (stateProvider != null && stateProvider.HasPredictedState)
            {
                inputTick = stateProvider.LastProcessedTick;
                pos = stateProvider.PredictedPosition;
                yaw = stateProvider.MovementFacingYaw;
            }

            return (inputTick, pos, yaw);
        }
    }
}
