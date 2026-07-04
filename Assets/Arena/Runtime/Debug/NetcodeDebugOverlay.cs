#nullable enable
using System.Collections.Generic;
using UnityEngine;
using Arena.Entity;
using Arena.Input;
using Arena.Presentation;
using Arena.Simulation;
using Arena.Network;
using Arena.Combat;
using SpacetimeDB.Types;

namespace Arena.Debugging
{
    /// <summary>
    /// Displays key netcode metrics in an on-screen overlay.
    /// Toggle with backslash. While visible, semicolon A/B-toggles the
    /// F4 server-time remote timeline (RemotePresentationBuffer), quote
    /// toggles the F5 predicted melee contact cue
    /// (PredictedMeleeContactCueController), and period starts/aborts the
    /// scripted F4 A/B leg run (S7). (Right/left bracket are taken:
    /// NetworkEnvironmentOverlay and LineOfSightDebugGuide.)
    /// </summary>
    public class NetcodeDebugOverlay : MonoBehaviour
    {
        private bool _visible;
        private GUIStyle? _style;
        private GUIStyle? _headerStyle;
        private const KeyCode ToggleKey = KeyCode.Backslash;
        private const KeyCode ServerTimelineToggleKey = KeyCode.Semicolon;
        private const KeyCode PredictedContactCueToggleKey = KeyCode.Quote;
        private const KeyCode AutoAttackSchedulerToggleKey = KeyCode.LeftBracket;
        private const KeyCode AbScriptedRunKey = KeyCode.Period;

        // Scripted F4 A/B run (S7): the rerun-2 protocol as one keypress —
        // 60 s arrival-timeline warmup (discardable leg 0), then
        // ON/OFF/ON/OFF 80 s legs, flipping ServerTimeTimelineEnabled at
        // each boundary so the AB CSV legs need no stopwatch. Legs keep
        // ticking while the overlay is hidden; only the start/abort key
        // needs it visible. ARENA_S7_AB_AUTORUN=1 starts the run on its own
        // once a precise clock sample and an NPC exist for ~10 s (headless
        // acceptance runs).
        private static readonly (bool TimelineOn, float Seconds)[] AbRunLegs =
        {
            (false, 60f), (true, 80f), (false, 80f), (true, 80f), (false, 80f),
        };
        private int _abRunLegIndex = -1;
        private float _abRunLegEndsAt;
        private bool _abRunCompleted;
        /// <summary>
        /// Scripted-run leg the session is currently inside (-1 outside a
        /// run). Logged as the AB CSV's s7_run_leg column so the analyzer
        /// identifies legs by marker instead of inferring them from timeline
        /// flips — pre-run idle time and post-run tails stop looking like
        /// legs (the 2026-07-04 shaped session lost its warmup discard to a
        /// 189 s pre-run idle stretch).
        /// </summary>
        internal static int CurrentScriptedLegIndex { get; private set; } = -1;
        private readonly bool _abAutorunArmed =
            System.Environment.GetEnvironmentVariable("ARENA_S7_AB_AUTORUN") == "1";
        private bool _abAutorunConsumed;
        private float _abAutorunEligibleSince = -1f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (!ArenaRuntimeSceneGate.ShouldRunArenaRuntimeInActiveScene())
                return;

            var go = new GameObject("NetcodeDebugOverlay");
            DontDestroyOnLoad(go);
            go.AddComponent<NetcodeDebugOverlay>();
        }

        private void Update()
        {
            if (!ArenaRuntimeSceneGate.ShouldRunArenaRuntimeInActiveScene())
            {
                _visible = false;
                return;
            }

            if (UnityEngine.Input.GetKeyDown(ToggleKey))
                _visible = !_visible;

            if (_visible && UnityEngine.Input.GetKeyDown(ServerTimelineToggleKey))
                RemotePresentationBuffer.ServerTimeTimelineEnabled =
                    !RemotePresentationBuffer.ServerTimeTimelineEnabled;

            if (_visible && UnityEngine.Input.GetKeyDown(PredictedContactCueToggleKey))
                PredictedMeleeContactCueController.DebugEnabled =
                    !PredictedMeleeContactCueController.DebugEnabled;

            if (_visible && UnityEngine.Input.GetKeyDown(AutoAttackSchedulerToggleKey))
                AutoAttackSwingScheduler.DebugEnabled =
                    !AutoAttackSwingScheduler.DebugEnabled;

            if (_visible && UnityEngine.Input.GetKeyDown(AbScriptedRunKey))
            {
                if (_abRunLegIndex >= 0)
                    AbortAbRun();
                else
                    StartAbRun();
            }

            TickAbRun();
        }

        private void StartAbRun()
        {
            _abRunCompleted = false;
            _abRunLegIndex = 0;
            ApplyAbRunLeg();
        }

        private void AbortAbRun()
        {
            _abRunLegIndex = -1;
            CurrentScriptedLegIndex = -1;
            RemotePresentationBuffer.ServerTimeTimelineEnabled = true;
        }

        private void ApplyAbRunLeg()
        {
            (bool timelineOn, float seconds) = AbRunLegs[_abRunLegIndex];
            RemotePresentationBuffer.ServerTimeTimelineEnabled = timelineOn;
            CurrentScriptedLegIndex = _abRunLegIndex;
            _abRunLegEndsAt = Time.realtimeSinceStartup + seconds;
        }

        private void TickAbRun()
        {
            if (_abAutorunArmed && !_abAutorunConsumed && _abRunLegIndex < 0)
            {
                bool eligible = ArenaServerClock.HasPreciseSample && AnyNpcPresent();
                if (!eligible)
                {
                    _abAutorunEligibleSince = -1f;
                }
                else if (_abAutorunEligibleSince < 0f)
                {
                    _abAutorunEligibleSince = Time.realtimeSinceStartup;
                }
                else if (Time.realtimeSinceStartup - _abAutorunEligibleSince >= 10f)
                {
                    _abAutorunConsumed = true;
                    Debug.Log("[NetcodeDebugOverlay] ARENA_S7_AB_AUTORUN: starting scripted F4 A/B run");
                    StartAbRun();
                }
            }

            if (_abRunLegIndex < 0 || Time.realtimeSinceStartup < _abRunLegEndsAt)
                return;

            _abRunLegIndex++;
            if (_abRunLegIndex >= AbRunLegs.Length)
            {
                _abRunLegIndex = -1;
                CurrentScriptedLegIndex = -1;
                _abRunCompleted = true;
                RemotePresentationBuffer.ServerTimeTimelineEnabled = true;
                Debug.Log(
                    "[NetcodeDebugOverlay] Scripted F4 A/B run COMPLETE — score with ops/analyze-remote-presentation-ab.py");
                return;
            }

            ApplyAbRunLeg();
        }

        private static bool AnyNpcPresent()
        {
            var registry = EntityRegistry.Instance;
            if (registry == null)
                return false;

            foreach (var _ in registry.AllNpcs)
                return true;
            return false;
        }

        private void OnGUI()
        {
            if (!_visible || !ArenaRuntimeSceneGate.ShouldRunArenaRuntimeInActiveScene()) return;

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

            float x = 10, y = 10, lineHeight = 20;

            GUI.Label(new Rect(x, y, 400, lineHeight), @"Netcode Debug (\)", _headerStyle);
            y += lineHeight + 4;

            y = DrawClockSection(x, y, lineHeight);

            var entity = EntityRegistry.Instance?.LocalPlayerEntity;
            if (entity == null) return;

            var netDriver = entity.GameObject.GetComponent<MovementNetDriver>();
            var predDriver = entity.GameObject.GetComponent<LocalMovementPredictionDriver>();
            var simState = entity.SimState;

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

                var lead = netDriver.LeadController;
                if (lead != null)
                {
                    float fallbackRatio = lead.AckTicksObserved > 0
                        ? (float)lead.SendCoveredFallbackAcks / lead.AckTicksObserved
                        : 0f;
                    string estimateSource = simState.LastTickEstimateUsedPreciseClock
                        ? "precise"
                        : "arrival";
                    GUI.Label(
                        new Rect(x, y, 640, lineHeight),
                        $"Input lead (S5): {lead.LeadTicks} ticks  occ: {lead.LastAckBufferedCommands}  (+{lead.LeadRaises}/-{lead.LeadLowers})  est: {estimateSource}",
                        _style);
                    y += lineHeight;
                    GUI.Label(
                        new Rect(x, y, 640, lineHeight),
                        $"Fallback acks: {lead.SendCoveredFallbackAcks}/{lead.AckTicksObserved}  ({fallbackRatio:P1})",
                        _style);
                    y += lineHeight;
                }

                if (predDriver != null)
                {
                    GUI.Label(
                        new Rect(x, y, 640, lineHeight),
                        $"Authoring: injected {predDriver.InjectedCommands}  skipped {predDriver.SkippedAuthoringSlots}  jumps {predDriver.JumpsConfirmed}/{predDriver.JumpsPredicted} (lost: {predDriver.JumpsLost})",
                        _style);
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

            y = DrawRemotePresentationSection(x, y, lineHeight);
            y = DrawPredictedActionResultSection(x, y, lineHeight);
            y = DrawPredictedContactCueSection(x, y, lineHeight);
            y = DrawAutoAttackScheduleSection(x, y, lineHeight);
            y = DrawReceiveRateSection(x, y, lineHeight);

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
            DrawTraceSection(x, ref y, lineHeight, traceLines);
        }

        /// <summary>
        /// Server-clock estimate and ping_clock RTT statistics (feel audit
        /// F2b). "observed-only" means no reducer round-trip sample has been
        /// accepted yet and the offset is the one-way monotonic-max estimate.
        /// </summary>
        private float DrawClockSection(float x, float y, float lineHeight)
        {
            if (ArenaServerClock.TryGetRttStats(out long lastRtt, out long p50Rtt, out long p95Rtt))
            {
                GUI.Label(
                    new Rect(x, y, 640, lineHeight),
                    $"RTT: {lastRtt} ms  (p50: {p50Rtt} ms, p95: {p95Rtt} ms)",
                    _style);
                y += lineHeight;
            }

            if (ArenaServerClock.HasEstimate)
            {
                string source = ArenaServerClock.HasPreciseSample ? "precise" : "observed-only";
                GUI.Label(
                    new Rect(x, y, 640, lineHeight),
                    $"Clock offset: {ArenaServerClock.EstimatedServerMinusClientMs:+0;-0} ms  ({source})",
                    _style);
                y += lineHeight;
            }

            return y;
        }

        /// <summary>
        /// Aggregate remote-presentation counters (feel audit F2a/F3/F4,
        /// design review S1): hard snaps, four-way sample taxonomy
        /// (interpolated / extrapolating / starved / settled) with the late
        /// ratio = (extrap + starved) / non-settled samples, last/max
        /// position error, active timeline + effective delay + buffer depth —
        /// one aggregate over remote players (ClientSimulationState) and one
        /// over NPCs (RemotePresentationBuffer).
        /// </summary>
        private float DrawRemotePresentationSection(float x, float y, float lineHeight)
        {
            var registry = EntityRegistry.Instance;
            if (registry == null)
                return y;

            int remoteCount = 0;
            int hardSnaps = 0;
            long interpSamples = 0;
            long extrapSamples = 0;
            long starvedSamples = 0;
            long settledSamples = 0;
            float lastError = 0f;
            float maxError = 0f;
            int playersOnServerTimeline = 0;
            float playerDelayMsSum = 0f;
            float playerDepthTicksSum = 0f;
            foreach (var player in registry.AllPlayers)
            {
                if (player == registry.LocalPlayerEntity)
                    continue;

                remoteCount++;
                var sim = player.SimState;
                hardSnaps += sim.RemoteHardSnapCount;
                interpSamples += sim.RemoteInterpolationSampleCount;
                extrapSamples += sim.RemoteExtrapolationSampleCount;
                starvedSamples += sim.RemoteStarvedSampleCount;
                settledSamples += sim.RemoteSettledSampleCount;
                lastError = Mathf.Max(lastError, sim.LastRemotePositionError);
                maxError = Mathf.Max(maxError, sim.MaxRemotePositionErrorObserved);
                if (sim.RemoteUsedServerTimelineForDebug)
                    playersOnServerTimeline++;
                playerDelayMsSum += sim.RemoteEffectiveDelayMsForDebug;
                playerDepthTicksSum += sim.RemoteBufferAheadTicksForDebug;
            }

            int npcCount = 0;
            int npcHardSnaps = 0;
            long npcInterpSamples = 0;
            long npcExtrapSamples = 0;
            long npcStarvedSamples = 0;
            long npcSettledSamples = 0;
            float npcLastError = 0f;
            float npcMaxError = 0f;
            int npcsOnServerTimeline = 0;
            int npcsSettledNow = 0;
            float npcDelayMsSum = 0f;
            float npcDepthTicksSum = 0f;
            foreach (var npc in registry.AllNpcs)
            {
                npcCount++;
                npcHardSnaps += npc.PresentationHardSnapCount;
                npcInterpSamples += npc.PresentationInterpolationSampleCount;
                npcExtrapSamples += npc.PresentationExtrapolationSampleCount;
                npcStarvedSamples += npc.PresentationStarvedSampleCount;
                npcSettledSamples += npc.PresentationSettledSampleCount;
                npcLastError = Mathf.Max(npcLastError, npc.PresentationLastPositionError);
                npcMaxError = Mathf.Max(npcMaxError, npc.PresentationMaxPositionErrorObserved);
                if (npc.PresentationUsedServerTimeline)
                    npcsOnServerTimeline++;
                if (npc.PresentationLastSampleClass == RemotePresentationBuffer.SampleClass.Settled)
                    npcsSettledNow++;
                npcDelayMsSum += npc.PresentationEffectiveDelayMs;
                npcDepthTicksSum += npc.PresentationBufferAheadTicks;
            }

            if (remoteCount == 0 && npcCount == 0)
                return y;

            y += 8;
            GUI.Label(
                new Rect(x, y, 640, lineHeight),
                $"Remote Presentation ({remoteCount} players, {npcCount} NPCs)",
                _headerStyle);
            y += lineHeight + 2;

            string abState = RemotePresentationBuffer.ServerTimeTimelineEnabled ? "ON" : "OFF";
            GUI.Label(
                new Rect(x, y, 640, lineHeight),
                $"Server-time timeline (; to A/B): {abState} — active on {playersOnServerTimeline}/{remoteCount} players, {npcsOnServerTimeline}/{npcCount} NPCs",
                _style);
            y += lineHeight;

            string budgetLine = ServerTimeDelayBudget.AdaptationActive
                ? $"S7 delay budget: {ServerTimeDelayBudget.LastAppliedBudgetMs:F0} ms  (target {ServerTimeDelayBudget.LastTargetBudgetMs:F0} = lateness p95 {ServerTimeDelayBudget.LastLatenessP95Ms:F0} ms + 1 tick, clamp 66..200; n={ServerTimeDelayBudget.WindowSampleCount})"
                : $"S7 delay budget: holding {ServerTimeDelayBudget.LastAppliedBudgetMs:F0} ms  (warming — {ServerTimeDelayBudget.WindowSampleCount} lateness samples)";
            GUI.Label(new Rect(x, y, 900, lineHeight), budgetLine, _style);
            y += lineHeight;

            string runLine;
            if (_abRunLegIndex >= 0)
            {
                (bool legOn, float _) = AbRunLegs[_abRunLegIndex];
                float left = Mathf.Max(0f, _abRunLegEndsAt - Time.realtimeSinceStartup);
                runLine = $"Scripted A/B (. to abort): leg {_abRunLegIndex}/{AbRunLegs.Length - 1} "
                    + $"{(legOn ? "ON" : "OFF")}{(_abRunLegIndex == 0 ? " (warmup)" : "")} — {left:F0}s left";
            }
            else
            {
                runLine = _abRunCompleted
                    ? "Scripted A/B: COMPLETE — score with ops/analyze-remote-presentation-ab.py"
                    : "Scripted A/B (. to start): 60s OFF warmup, then ON/OFF/ON/OFF 80s legs";
            }
            GUI.Label(new Rect(x, y, 900, lineHeight), runLine, _style);
            y += lineHeight;

            if (remoteCount > 0)
            {
                long nonSettled = interpSamples + extrapSamples + starvedSamples;
                float lateRatio = nonSettled > 0 ? (float)(extrapSamples + starvedSamples) / nonSettled : 0f;
                GUI.Label(new Rect(x, y, 640, lineHeight), $"Hard snaps: {hardSnaps}", _style);
                y += lineHeight;
                GUI.Label(
                    new Rect(x, y, 900, lineHeight),
                    $"Samples: interp {interpSamples} / extrap {extrapSamples} / starved {starvedSamples} / settled {settledSamples}  (late ratio: {lateRatio:P1})",
                    _style);
                y += lineHeight;
                GUI.Label(
                    new Rect(x, y, 640, lineHeight),
                    $"Remote pos error: {lastError:F3} m  (max: {maxError:F3} m)",
                    _style);
                y += lineHeight;
                GUI.Label(
                    new Rect(x, y, 640, lineHeight),
                    $"Buffer depth: {playerDepthTicksSum / remoteCount:F1} ticks avg  (delay: {playerDelayMsSum / remoteCount:F0} ms avg)",
                    _style);
                y += lineHeight;
            }

            if (npcCount > 0)
            {
                long npcNonSettled = npcInterpSamples + npcExtrapSamples + npcStarvedSamples;
                float npcLateRatio = npcNonSettled > 0 ? (float)(npcExtrapSamples + npcStarvedSamples) / npcNonSettled : 0f;
                GUI.Label(new Rect(x, y, 640, lineHeight), $"NPC hard snaps: {npcHardSnaps}", _style);
                y += lineHeight;
                GUI.Label(
                    new Rect(x, y, 900, lineHeight),
                    $"NPC samples: interp {npcInterpSamples} / extrap {npcExtrapSamples} / starved {npcStarvedSamples} / settled {npcSettledSamples}  (late ratio: {npcLateRatio:P1})",
                    _style);
                y += lineHeight;
                GUI.Label(
                    new Rect(x, y, 640, lineHeight),
                    $"NPC pos error: {npcLastError:F3} m  (max: {npcMaxError:F3} m)",
                    _style);
                y += lineHeight;
                GUI.Label(
                    new Rect(x, y, 640, lineHeight),
                    $"NPC buffer depth: {npcDepthTicksSum / npcCount:F1} ticks avg  (delay: {npcDelayMsSum / npcCount:F0} ms avg, settled now: {npcsSettledNow}/{npcCount})",
                    _style);
                y += lineHeight;
            }

            return y;
        }

        /// <summary>
        /// PredictedActionResult rows by result kind. A rising rejected count is
        /// the early-warning signal for client/server validation drift.
        /// </summary>
        private float DrawPredictedActionResultSection(float x, float y, float lineHeight)
        {
            var resultsByKind = NetcodeReceiveCounters.PredictedResultsByKind;
            if (resultsByKind.Count == 0)
                return y;

            var builder = new System.Text.StringBuilder("Predicted results:");
            foreach (var pair in resultsByKind)
                builder.Append($" {pair.Key}={pair.Value}");
            if (NetcodeReceiveCounters.LastRejection.Length > 0)
                builder.Append($" lastReject={NetcodeReceiveCounters.LastRejection}");

            y += 8;
            GUI.Label(new Rect(x, y, 900, lineHeight), builder.ToString(), _style);
            y += lineHeight;
            return y;
        }

        /// <summary>
        /// Predicted melee contact cue instrumentation (feel audit F5
        /// slice 2). falsePos = cue fired but no matching authoritative
        /// impact within 500 ms — the number that decides whether the cue
        /// reads as a lie and gets tuned down or killed.
        /// </summary>
        private float DrawPredictedContactCueSection(float x, float y, float lineHeight)
        {
            if (!PredictedMeleeContactCueController.FeatureCompiledIn)
                return y;

            string state = PredictedMeleeContactCueController.IsActive ? "ON" : "OFF";
            GUI.Label(
                new Rect(x, y, 900, lineHeight),
                $"Predicted contact cues (' to toggle): {state}  fired={PredictedMeleeContactCueController.CuesFired}"
                + $" matched={PredictedMeleeContactCueController.MatchedCues}"
                + $" falsePos={PredictedMeleeContactCueController.FalsePositives}"
                + $" suppressedAuth={PredictedMeleeContactCueController.SuppressedAuthoritativeCues}",
                _style);
            y += lineHeight;
            GUI.Label(
                new Rect(x, y, 900, lineHeight),
                $"  auto share: fired={PredictedMeleeContactCueController.AutoCuesFired}"
                + $" matched={PredictedMeleeContactCueController.AutoMatchedCues}"
                + $" falsePos={PredictedMeleeContactCueController.AutoFalsePositives}"
                + $" suppressedAuth={PredictedMeleeContactCueController.AutoSuppressedAuthoritativeCues}",
                _style);
            y += lineHeight;
            return y;
        }

        /// <summary>
        /// Local auto-attack swing scheduling (netcode design review S6).
        /// startErr = local fire time vs the converted next_swing_at schedule;
        /// castAlign = estimated server-time of the local fire vs the
        /// authoritative CAST timestamp (expected within one tick).
        /// suppCast/fired is the duplicate-suppression rate; expired = local
        /// swing with no CAST inside the window (the swing-was-a-lie count).
        /// </summary>
        private float DrawAutoAttackScheduleSection(float x, float y, float lineHeight)
        {
            string state = AutoAttackSwingScheduler.DebugEnabled ? "ON" : "OFF";
            GUI.Label(
                new Rect(x, y, 1100, lineHeight),
                $"S6 auto swing sched ([ to toggle): {state}  fired={AutoAttackSwingScheduler.SwingsFired}"
                + $" suppCast={AutoAttackSwingScheduler.SuppressedCasts}"
                + $" unpred={AutoAttackSwingScheduler.UnpredictedCasts}"
                + $" held={AutoAttackSwingScheduler.SwingsHeldByMirror}"
                + $" late={AutoAttackSwingScheduler.SwingsMissedLate}"
                + $" expired={AutoAttackSwingScheduler.ExpiredWithoutCast}"
                + $" mismatch={AutoAttackSwingScheduler.MismatchedCasts}"
                + $" startErr={AutoAttackSwingScheduler.StartErrorLastMs}/{AutoAttackSwingScheduler.StartErrorMaxMs}ms"
                + $" castAlign={AutoAttackSwingScheduler.CastAlignLastMs}/{AutoAttackSwingScheduler.CastAlignMaxAbsMs}ms",
                _style);
            y += lineHeight;
            return y;
        }

        /// <summary>
        /// Per-table row-callback rates over the counter window (rows/sec).
        /// </summary>
        private float DrawReceiveRateSection(float x, float y, float lineHeight)
        {
            var rates = NetcodeReceiveCounters.WindowRates(Time.realtimeSinceStartup);
            if (rates.Count == 0)
                return y;

            var ordered = new List<KeyValuePair<string, float>>(rates);
            ordered.Sort((a, b) => b.Value.CompareTo(a.Value));

            y += 8;
            GUI.Label(
                new Rect(x, y, 640, lineHeight),
                $"Row Callbacks (rows/sec, {NetcodeReceiveCounters.WindowSeconds:F0}s window, total {NetcodeReceiveCounters.TotalRows})",
                _headerStyle);
            y += lineHeight + 2;

            const int maxTableLines = 8;
            for (int i = 0; i < ordered.Count && i < maxTableLines; i++)
            {
                GUI.Label(new Rect(x, y, 640, lineHeight), $"{ordered[i].Key}: {ordered[i].Value:F1}", _style);
                y += lineHeight;
            }

            return y;
        }

        private void DrawTraceSection(float x, ref float y, float lineHeight, System.Collections.Generic.IReadOnlyList<string> traceLines)
        {
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
