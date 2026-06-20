#nullable enable
using UnityEngine;
using Arena.Entity;
using Arena.Input;
using Arena.Simulation;
using Arena.Network;
using Arena.Combat;
using SpacetimeDB.Types;

namespace Arena.Debugging
{
    /// <summary>
    /// Displays key netcode metrics in an on-screen overlay.
    /// Toggle with backslash.
    /// </summary>
    public class NetcodeDebugOverlay : MonoBehaviour
    {
        private bool _visible;
        private GUIStyle? _style;
        private GUIStyle? _headerStyle;
        private const KeyCode ToggleKey = KeyCode.Backslash;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            var go = new GameObject("NetcodeDebugOverlay");
            DontDestroyOnLoad(go);
            go.AddComponent<NetcodeDebugOverlay>();
        }

        private void Update()
        {
            if (UnityEngine.Input.GetKeyDown(ToggleKey))
                _visible = !_visible;
        }

        private void OnGUI()
        {
            if (!_visible) return;

            _style ??= new GUIStyle(GUI.skin.label)
            {
                fontSize = 14,
                normal = { textColor = Color.white },
                richText = true,
            };
            _headerStyle ??= new GUIStyle(_style)
            {
                fontSize = 16,
                fontStyle = FontStyle.Bold,
            };

            var entity = EntityRegistry.Instance?.LocalPlayerEntity;
            if (entity == null) return;

            var netDriver = entity.GameObject.GetComponent<MovementNetDriver>();
            var predDriver = entity.GameObject.GetComponent<LocalMovementPredictionDriver>();
            var simState = entity.SimState;

            float x = 10, y = 10, lineHeight = 20;

            GUI.Label(new Rect(x, y, 400, lineHeight), @"Netcode Debug (\)", _headerStyle);
            y += lineHeight + 4;

            if (netDriver != null)
            {
                uint serverTick = simState.LastProcessedTick;
                uint? oldestPending = netDriver.OldestPendingTick;
                uint? newestPending = netDriver.NewestPendingTick;
                int pending = netDriver.PendingCommandCount;
                float tickLagMs = pending * MovementNetcodeConfig.FixedTickMilliseconds;

                GUI.Label(new Rect(x, y, 400, lineHeight), $"Server tick: {serverTick}", _style);
                y += lineHeight;
                GUI.Label(new Rect(x, y, 400, lineHeight), $"Estimated tick: {netDriver.EstimatedServerTick:F2}", _style);
                y += lineHeight;
                GUI.Label(new Rect(x, y, 400, lineHeight), $"Pending commands: {pending}  (max: {netDriver.MaxPendingCommandsObserved})", _style);
                y += lineHeight;
                GUI.Label(new Rect(x, y, 400, lineHeight), $"Tick lag: {tickLagMs:F0} ms  ({pending} ticks)", _style);
                y += lineHeight;
                GUI.Label(new Rect(x, y, 400, lineHeight), $"Sent: {netDriver.CommandsSent}  Acked: {netDriver.CommandsAcked}", _style);
                y += lineHeight;
                GUI.Label(new Rect(x, y, 400, lineHeight), $"Resyncs: {netDriver.ResyncCount}", _style);
                y += lineHeight;

                if (oldestPending.HasValue && newestPending.HasValue)
                {
                    GUI.Label(new Rect(x, y, 400, lineHeight), $"Pending range: [{oldestPending}..{newestPending}]", _style);
                    y += lineHeight;
                }
            }

            y += 4;

            Vector3 serverPos = simState.GetServerPosition();
            Vector3 predictedPos = predDriver != null
                ? predDriver.CurrentPredictedPosition
                : entity.GameObject.transform.position;
            float leadDistance = Vector3.Distance(serverPos, predictedPos);
            float yawDeltaDeg = Mathf.Abs(Mathf.DeltaAngle(
                simState.GetServerYawRadians() * Mathf.Rad2Deg,
                entity.GameObject.transform.eulerAngles.y));

            GUI.Label(new Rect(x, y, 400, lineHeight), $"Server pos: ({serverPos.x:F2}, {serverPos.y:F2}, {serverPos.z:F2})", _style);
            y += lineHeight;
            GUI.Label(new Rect(x, y, 400, lineHeight), $"Predicted pos: ({predictedPos.x:F2}, {predictedPos.y:F2}, {predictedPos.z:F2})", _style);
            y += lineHeight;
            string combatProfile = CombatProfileResolver.ResolveForEntity(NetworkManager.Instance?.Conn, entity);
            GUI.Label(new Rect(x, y, 400, lineHeight), $"Combat profile: {combatProfile}", _style);
            y += lineHeight;

            GUI.Label(new Rect(x, y, 400, lineHeight), $"Lead to latest snapshot: {leadDistance:F3} m", _style);
            y += lineHeight;
            GUI.Label(new Rect(x, y, 400, lineHeight), $"Yaw delta: {yawDeltaDeg:F1}°", _style);
            y += lineHeight;

            if (simState.TryGetRemoteObserverSample(Time.realtimeSinceStartup, out ClientSimulationState.RemoteObserverSample observerSample))
            {
                Vector3 observerPos = observerSample.Position;
                float localToObserverDistance = Vector3.Distance(predictedPos, observerPos);
                float observerToServerDistance = Vector3.Distance(observerPos, serverPos);

                GUI.Label(new Rect(x, y, 400, lineHeight), $"Observer pos: ({observerPos.x:F2}, {observerPos.y:F2}, {observerPos.z:F2})", _style);
                y += lineHeight;
                GUI.Label(
                    new Rect(x, y, 400, lineHeight),
                    $"Local -> observer: {localToObserverDistance:F3} m  (delay: {observerSample.InterpolationDelaySeconds * 1000.0f:F0} ms)",
                    _style);
                y += lineHeight;
                GUI.Label(
                    new Rect(x, y, 400, lineHeight),
                    $"Observer -> latest auth: {observerToServerDistance:F3} m  (extrap: {observerSample.ExtrapolationSeconds * 1000.0f:F0} ms)",
                    _style);
                y += lineHeight;
            }

            if (predDriver != null)
            {
                y += 4;
                GUI.Label(new Rect(x, y, 400, lineHeight), $"Local tick alpha: {predDriver.FixedTickAlpha:F2}", _style);
                y += lineHeight;
                GUI.Label(new Rect(x, y, 400, lineHeight), $"Predicted tick: {predDriver.CurrentPredictedTick}  (lead: {predDriver.CurrentTickLead})", _style);
                y += lineHeight;
                GUI.Label(new Rect(x, y, 400, lineHeight), $"Replay depth: {predDriver.LastReplayDepth}  (max: {predDriver.MaxReplayDepthObserved})", _style);
                y += lineHeight;
                GUI.Label(new Rect(x, y, 400, lineHeight), $"Correction error: {predDriver.LastCorrectionPositionError:F4} m  (max: {predDriver.MaxCorrectionPositionErrorObserved:F4})", _style);
                y += lineHeight;
                GUI.Label(new Rect(x, y, 400, lineHeight), $"Auth move ctx: blocked={simState.MovementBlocked} mult={simState.MoveSpeedMultiplier:F2} tick={simState.MovementContextTick}", _style);
                y += lineHeight;
                GUI.Label(new Rect(x, y, 400, lineHeight), $"Effective ctx: blocked={predDriver.EffectiveMovementBlocked} mult={predDriver.EffectiveMoveSpeedMultiplier:F2} tick={predDriver.EffectiveMovementContextTick}", _style);
                y += lineHeight;
                GUI.Label(new Rect(x, y, 400, lineHeight), $"Replay ctx fallback: {predDriver.LastReplayFallbackContextUses}  (total: {predDriver.TotalReplayFallbackContextUses})", _style);
                y += lineHeight;
            }

            var conn = NetworkManager.Instance?.Conn;
            if (conn != null)
            {
                var owner = conn.Identity;
                y += 8;
                GUI.Label(new Rect(x, y, 640, lineHeight), "Active Action Bar", _headerStyle);
                y += lineHeight + 2;
                foreach (string slotId in ActionBarSlotIds.GridOrdered)
                {
                    ActiveActionBarAction resolved =
                        ActiveActionBarResolver.ResolveActiveSelectableAction(conn, owner, slotId);
                    string line = resolved.HasAssignedAction
                        ? $"{slotId}: {resolved.AbilityId} | authored={resolved.AuthoredActionId} | runtime={resolved.ActionId}"
                        : $"{slotId}: <empty>";
                    GUI.Label(new Rect(x, y, 900, lineHeight), line, _style);
                    y += lineHeight;
                }

                if (owner.HasValue)
                {
                    PlayerState? playerState = conn.Db.PlayerState.PlayerId.Find(owner.Value);
                    if (playerState != null)
                    {
                        GUI.Label(new Rect(x, y, 900, lineHeight), $"Auth last strike: {playerState.LastStrikeId}", _style);
                        y += lineHeight;
                    }
                }
            }

            var traceLines = ActionBarTrace.Snapshot();
            if (traceLines.Count > 0)
            {
                y += 8;
                GUI.Label(new Rect(x, y, 640, lineHeight), "Action Bar Trace", _headerStyle);
                y += lineHeight + 2;
                foreach (string line in traceLines)
                {
                    GUI.Label(new Rect(x, y, 1200, lineHeight), line, _style);
                    y += lineHeight;
                }
            }
        }
    }
}
