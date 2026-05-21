#nullable enable

#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Arena.Combat;
using Arena.Network;
using Arena.Presentation;
using SpacetimeDB;
using SpacetimeDB.Types;
using UnityEngine;
using UnityEngine.Profiling;

namespace Arena.Debugging
{
    /// <summary>
    /// Development-only control surface for the isolated projectile load harness.
    /// Toggle with '=' in play mode.
    /// </summary>
    internal sealed class ProjectileLoadHarnessOverlay : MonoBehaviour
    {
        private const KeyCode ToggleKey = KeyCode.Equals;
        private const string HarnessActionPrefix = "PROJECTILE_LOAD_HARNESS";
        private const string PresentationModeServerSim = "server_sim";
        private const string PresentationModeFullClient = "full_client";
        private static bool s_suppressHarnessPresentation = true;
        private IDisposable? _combatPresentationSuppressor;
        private IDisposable? _projectilePresentationSuppressor;

        private static readonly string[] Scenarios =
        {
            "baseline_linear_arrows",
            "linear_spell_projectiles",
            "homing_projectiles",
            "orbit_projectiles",
            "boomerang_projectiles",
            "mixed_realistic",
            "mixed_worst_case_dense_targets",
            "moving_lane_homing_boomerang",
        };

        private bool _visible;
        private int _scenarioIndex = 5;
        private string _projectileCountText = "300";
        private string _targetCountText = "32";
        private string _seedText = "1";
        private string _durationSecondsText = "10";
        private string _status = "Idle.";
        private readonly HashSet<string> _observedActiveProjectiles = new(StringComparer.Ordinal);
        private DbConnection? _subscribedConnection;
        private long _eventTotal;
        private long _releaseTotal;
        private long _updateTotal;
        private long _contactTotal;
        private long _terminalTotal;
        private long _blockParryTotal;
        private int _peakObservedActiveProjectiles;
        private int _eventsThisWindow;
        private int _updatesThisWindow;
        private int _contactsThisWindow;
        private float _eventsPerSecond;
        private float _updatesPerSecond;
        private float _contactsPerSecond;
        private float _rateWindowStartedAt = -1f;
        private int _runFrameCount;
        private float _runFrameTotalMs;
        private float _runFrameWorstMs;
        private float _recentFrameWorstMs;
        private float _recentFrameWorstMsThisWindow;
        private string _lastRunScenario = "<none>";
        private float _lastRunStartedAt = -1f;
        private float _autoCleanupAt = -1f;
        private float _pendingAutoCaptureAt = -1f;
        private readonly List<string> _capturedSummaries = new();

        private static bool ShouldSuppressProjectilePresentation(ProjectilePresentationEvent row)
            => s_suppressHarnessPresentation && IsHarnessProjectileEvent(row);

        private static bool ShouldSuppressCombatPresentation(CombatEvent row)
            => s_suppressHarnessPresentation
                && !string.IsNullOrWhiteSpace(row.ActionInstanceId)
                && row.ActionInstanceId.StartsWith(HarnessActionPrefix, StringComparison.Ordinal);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            var go = new GameObject("ProjectileLoadHarnessOverlay");
            DontDestroyOnLoad(go);
            go.AddComponent<ProjectileLoadHarnessOverlay>();
        }

        private void Awake()
        {
            _combatPresentationSuppressor = CombatPresentationDebugGate.RegisterCombatEventSuppressor(ShouldSuppressCombatPresentation);
            _projectilePresentationSuppressor = CombatPresentationDebugGate.RegisterProjectileEventSuppressor(ShouldSuppressProjectilePresentation);
        }

        private void Update()
        {
            if (UnityEngine.Input.GetKeyDown(ToggleKey) || UnityEngine.Input.GetKeyDown(KeyCode.KeypadEquals))
                _visible = !_visible;

            SubscribeToConnection(NetworkManager.Instance?.Conn);
            UpdateReadoutTiming();
            HandleAutoCleanup();
        }

        private void OnGUI()
        {
            if (!_visible)
                return;

            NetworkManager? manager = NetworkManager.Instance;
            bool connected = manager is { IsConnected: true, Conn: not null };
            bool harnessReducersAvailable = connected && HasHarnessReducers(manager);
            CombatProjectileTickMetrics? serverMetrics = manager?.Conn?.Db.CombatProjectileTickMetrics.Key.Find("latest");

            GUILayout.BeginArea(new Rect(10f, 10f, 940f, 600f), "Projectile Load Harness (=)", GUI.skin.window);
            GUILayout.Label("Development-only server load harness. Not gameplay simulation.");
            GUILayout.Label(manager == null
                ? "NetworkManager: not initialized"
                : $"Connected: {manager.IsConnected}");

            GUILayout.BeginHorizontal();
            GUILayout.BeginVertical(GUILayout.Width(330f));
            GUILayout.Space(8f);
            GUILayout.Label("Scenario");
            _scenarioIndex = GUILayout.SelectionGrid(_scenarioIndex, Scenarios, 1, GUILayout.Width(320f));

            GUILayout.Space(8f);
            DrawTextField("Projectiles", ref _projectileCountText);
            DrawTextField("Targets", ref _targetCountText);
            DrawTextField("Seed", ref _seedText);
            DrawTextField("Duration(s)", ref _durationSecondsText);

            GUILayout.Space(8f);
            GUILayout.Label("Client Mode");
            s_suppressHarnessPresentation = GUILayout.Toggle(
                s_suppressHarnessPresentation,
                "Server sim (suppress harness VFX)");
            if (!s_suppressHarnessPresentation)
                GUILayout.Label("Full client presentation enabled.");

            GUILayout.Space(8f);
            GUILayout.BeginHorizontal();
            DrawPresetButton("Small", "100", "16", "1", 5);
            DrawPresetButton("Medium", "300", "32", "1", 5);
            DrawPresetButton("Dense", "1000", "96", "1", 6);
            DrawPresetButton("Moving", "300", "48", "7", 7);
            GUILayout.EndHorizontal();

            GUILayout.Space(8f);
            bool previousEnabled = GUI.enabled;
            GUI.enabled = harnessReducersAvailable;
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Run"))
                RunHarness(manager);
            if (GUILayout.Button("Cleanup"))
                CleanupHarness(manager);
            GUILayout.EndHorizontal();
            GUI.enabled = previousEnabled;

            if (!connected)
                GUILayout.Label("Connect to SpacetimeDB before running the harness.");
            else if (!harnessReducersAvailable)
                GUILayout.Label("Projectile load harness reducers are not available on this server build.");

            GUILayout.EndVertical();

            GUILayout.Space(14f);
            GUILayout.BeginVertical(GUILayout.Width(490f));
            GUILayout.Label("Readout");
            GUILayout.Label($"Last run: {_lastRunScenario}  elapsed: {ElapsedRunSeconds():F1}s");
            GUILayout.Label($"Client mode: {CurrentPresentationMode()}");
            if (_autoCleanupAt > 0f)
                GUILayout.Label($"Auto capture/cleanup in: {Mathf.Max(0f, _autoCleanupAt - Time.realtimeSinceStartup):F1}s");
            if (_pendingAutoCaptureAt > 0f)
                GUILayout.Label($"Auto summary capture in: {Mathf.Max(0f, _pendingAutoCaptureAt - Time.realtimeSinceStartup):F1}s");
            GUILayout.Label($"Observed active projectiles: {_observedActiveProjectiles.Count}  peak: {_peakObservedActiveProjectiles}");
            GUILayout.Label(
                $"Harness events/sec: {_eventsPerSecond:F1}  updates/sec: {_updatesPerSecond:F1}  contacts/sec: {_contactsPerSecond:F1}");
            GUILayout.Label(
                $"Totals: events={_eventTotal} release={_releaseTotal} update={_updateTotal} contact={_contactTotal} terminal={_terminalTotal} block/parry={_blockParryTotal}");
            GUILayout.Label(
                $"Client visuals active: {CombatProjectileVisualController.DebugActiveProjectileVisualCount}  started={CombatProjectileVisualController.DebugStartedProjectileVisualCount}  updated={CombatProjectileVisualController.DebugUpdatedProjectileVisualCount}");
            GUILayout.Label(
                $"Visual terminals={CombatProjectileVisualController.DebugTerminalProjectileVisualCount}  auto-disposed={CombatProjectileVisualController.DebugAutoDisposedProjectileVisualCount}  missing-prefabs={CombatProjectileVisualController.DebugMissingProjectilePrefabCount}");
            GUILayout.Label(
                $"Frame run: avg={RunAverageFrameMs():F1}ms worst={_runFrameWorstMs:F1}ms  recent-worst={_recentFrameWorstMs:F1}ms  alloc={Profiler.GetTotalAllocatedMemoryLong() / (1024L * 1024L)} MB");
            GUILayout.Space(6f);
            GUILayout.Label("Server Projectile Tick");
            if (serverMetrics == null)
            {
                GUILayout.Label("Waiting for server metrics row.");
            }
            else
            {
                GUILayout.Label(
                    $"active={serverMetrics.ActiveProjectileCount} rows={serverMetrics.RowsUpdated} scans={serverMetrics.CollisionCandidateScans} world={serverMetrics.WorldCollisionQueries} contacts={serverMetrics.ContactsResolved}");
                GUILayout.Label(
                    $"run peaks: active={serverMetrics.PeakActiveProjectileCount} rows={serverMetrics.PeakRowsUpdated} scans={serverMetrics.PeakCollisionCandidateScans} contacts={serverMetrics.PeakContactsResolved}");
                GUILayout.Label(
                    $"run totals: rows={serverMetrics.TotalRowsUpdated} scans={serverMetrics.TotalCollisionCandidateScans} world={serverMetrics.TotalWorldCollisionQueries} contacts={serverMetrics.TotalContactsResolved}");
                GUILayout.Label(
                    $"world broadphase: candidates={serverMetrics.WorldGameplayBroadphaseCandidates} narrowphase={serverMetrics.WorldGameplayNarrowphaseTests} " +
                    $"fallbacks={serverMetrics.WorldGameplayFullScanFallbacks} ({RatioPercent(serverMetrics.WorldGameplayFullScanFallbacks, serverMetrics.WorldCollisionQueries):F1}%) " +
                    $"open-world points={serverMetrics.OpenWorldGeometryPointChecks}");
                GUILayout.Label(
                    $"events: update={serverMetrics.UpdateEventsEmitted} contact={serverMetrics.ContactEventsEmitted} terminal={serverMetrics.TerminalEventsEmitted} block/parry={serverMetrics.BlockParryEventsEmitted}");
                GUILayout.Label(
                    $"motion: linear={serverMetrics.LinearProjectileCount} homing={serverMetrics.HomingProjectileCount} orbit={serverMetrics.OrbitProjectileCount} boomerang={serverMetrics.BoomerangProjectileCount}");
                GUILayout.Label("tick time: unavailable inside SpacetimeDB reducer");
            }

            GUILayout.Space(6f);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Capture Summary"))
                CaptureSummary(serverMetrics);
            if (GUILayout.Button("Copy Latest"))
                CopyLatestSummary(serverMetrics);
            if (GUILayout.Button("Clear Summaries"))
                _capturedSummaries.Clear();
            GUILayout.EndHorizontal();
            GUILayout.Label($"Captured summaries: {_capturedSummaries.Count}");
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();

            GUILayout.Space(6f);
            GUILayout.Label(_status);
            GUILayout.EndArea();
        }

        private static void DrawTextField(string label, ref string value)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(82f));
            value = GUILayout.TextField(value, GUILayout.Width(120f));
            GUILayout.EndHorizontal();
        }

        private void DrawPresetButton(string label, string projectiles, string targets, string seed, int scenarioIndex)
        {
            if (!GUILayout.Button(label))
                return;

            _projectileCountText = projectiles;
            _targetCountText = targets;
            _seedText = seed;
            _scenarioIndex = Mathf.Clamp(scenarioIndex, 0, Scenarios.Length - 1);
        }

        private void RunHarness(NetworkManager? manager)
        {
            if (manager?.Conn == null)
            {
                _status = "Run skipped: no active connection.";
                return;
            }

            if (!TryParseUInt(_projectileCountText, "projectile count", out uint projectileCount)
                || !TryParseUInt(_targetCountText, "target count", out uint targetCount)
                || !TryParseULong(_seedText, "seed", out ulong seed)
                || !TryParseNonNegativeFloat(_durationSecondsText, "duration", out float durationSeconds))
            {
                return;
            }

            string scenario = Scenarios[Mathf.Clamp(_scenarioIndex, 0, Scenarios.Length - 1)];
            ResetReadoutForRun(scenario);
            _autoCleanupAt = durationSeconds > 0f
                ? Time.realtimeSinceStartup + durationSeconds
                : -1f;
            if (!TryInvokeRunHarness(manager, scenario, projectileCount, targetCount, seed))
            {
                _autoCleanupAt = -1f;
                _status = "Run skipped: projectile load harness reducer is unavailable on this server build.";
                return;
            }

            _status = durationSeconds > 0f
                ? $"Requested {scenario}: {projectileCount} projectiles, {targetCount} targets, seed {seed}, duration {durationSeconds:F1}s, mode {CurrentPresentationMode()}."
                : $"Requested {scenario}: {projectileCount} projectiles, {targetCount} targets, seed {seed}, mode {CurrentPresentationMode()}.";
        }

        private void CleanupHarness(NetworkManager? manager)
        {
            if (manager?.Conn == null)
            {
                _status = "Cleanup skipped: no active connection.";
                return;
            }

            if (!TryInvokeCleanupHarness(manager))
            {
                _status = "Cleanup skipped: projectile load harness reducer is unavailable on this server build.";
                return;
            }

            _autoCleanupAt = -1f;
            _pendingAutoCaptureAt = -1f;
            _observedActiveProjectiles.Clear();
            _status = "Requested harness cleanup.";
        }

        private void HandleAutoCleanup()
        {
            float now = Time.realtimeSinceStartup;
            DbConnection? conn = NetworkManager.Instance?.Conn;
            if (_autoCleanupAt > 0f && now >= _autoCleanupAt)
            {
                _autoCleanupAt = -1f;
                if (conn == null)
                {
                    _status = "Auto cleanup skipped: no active connection.";
                    return;
                }

                if (!TryInvokeCleanupHarness(NetworkManager.Instance))
                {
                    _status = "Auto cleanup skipped: projectile load harness reducer is unavailable on this server build.";
                    return;
                }

                _pendingAutoCaptureAt = now + 0.25f;
                _status = "Auto requested cleanup; waiting for terminal events before capture.";
            }

            if (_pendingAutoCaptureAt > 0f && now >= _pendingAutoCaptureAt)
            {
                _pendingAutoCaptureAt = -1f;
                CaptureSummary(conn?.Db.CombatProjectileTickMetrics.Key.Find("latest"));
                _observedActiveProjectiles.Clear();
                _status = $"Auto captured summary #{_capturedSummaries.Count} after cleanup.";
            }
        }

        private void SubscribeToConnection(DbConnection? conn)
        {
            if (ReferenceEquals(_subscribedConnection, conn))
                return;

            if (_subscribedConnection != null)
                _subscribedConnection.Db.ProjectilePresentationEvent.OnInsert -= OnProjectilePresentationEventInsert;

            _subscribedConnection = conn;

            if (_subscribedConnection != null)
                _subscribedConnection.Db.ProjectilePresentationEvent.OnInsert += OnProjectilePresentationEventInsert;
        }

        private void OnDestroy()
        {
            SubscribeToConnection(null);
            _combatPresentationSuppressor?.Dispose();
            _combatPresentationSuppressor = null;
            _projectilePresentationSuppressor?.Dispose();
            _projectilePresentationSuppressor = null;
        }

        private static bool HasHarnessReducers(NetworkManager? manager)
        {
            object? reducers = manager?.Conn?.Reducers;
            return reducers != null
                && FindRunHarnessReducer(reducers) != null
                && FindCleanupHarnessReducer(reducers) != null;
        }

        private static bool TryInvokeRunHarness(
            NetworkManager? manager,
            string scenario,
            uint projectileCount,
            uint targetCount,
            ulong seed)
        {
            object? reducers = manager?.Conn?.Reducers;
            MethodInfo? method = reducers != null ? FindRunHarnessReducer(reducers) : null;
            if (reducers == null || method == null)
                return false;

            method.Invoke(reducers, new object[] { scenario, projectileCount, targetCount, seed });
            return true;
        }

        private static bool TryInvokeCleanupHarness(NetworkManager? manager)
        {
            object? reducers = manager?.Conn?.Reducers;
            MethodInfo? method = reducers != null ? FindCleanupHarnessReducer(reducers) : null;
            if (reducers == null || method == null)
                return false;

            method.Invoke(reducers, Array.Empty<object>());
            return true;
        }

        private static MethodInfo? FindRunHarnessReducer(object reducers)
        {
            return reducers
                .GetType()
                .GetMethod(
                    "RunProjectileLoadHarness",
                    new[] { typeof(string), typeof(uint), typeof(uint), typeof(ulong) });
        }

        private static MethodInfo? FindCleanupHarnessReducer(object reducers)
        {
            return reducers
                .GetType()
                .GetMethod("CleanupProjectileLoadHarness", Type.EmptyTypes);
        }

        private void OnProjectilePresentationEventInsert(EventContext ctx, ProjectilePresentationEvent row)
        {
            _ = ctx;
            if (!IsHarnessProjectileEvent(row))
                return;

            _eventTotal++;
            _eventsThisWindow++;

            string projectileKey = string.IsNullOrWhiteSpace(row.ProjectileInstanceId)
                ? row.ActionInstanceId
                : row.ProjectileInstanceId;

            switch (row.EventType)
            {
                case CombatEventTypes.Cast:
                case CombatEventTypes.Release:
                    _releaseTotal++;
                    _observedActiveProjectiles.Add(projectileKey);
                    break;
                case CombatEventTypes.Update:
                    _updateTotal++;
                    _updatesThisWindow++;
                    _observedActiveProjectiles.Add(projectileKey);
                    break;
                case CombatEventTypes.Contact:
                    _contactTotal++;
                    _contactsThisWindow++;
                    break;
                case CombatEventTypes.Block:
                case CombatEventTypes.Parry:
                    _blockParryTotal++;
                    _terminalTotal++;
                    _observedActiveProjectiles.Remove(projectileKey);
                    break;
                case CombatEventTypes.Impact:
                case CombatEventTypes.Fizzle:
                    _terminalTotal++;
                    _observedActiveProjectiles.Remove(projectileKey);
                    break;
            }

            _peakObservedActiveProjectiles = Mathf.Max(
                _peakObservedActiveProjectiles,
                _observedActiveProjectiles.Count);
        }

        private static bool IsHarnessProjectileEvent(ProjectilePresentationEvent row)
        {
            return !string.IsNullOrWhiteSpace(row.ProjectileInstanceId)
                && !string.IsNullOrWhiteSpace(row.ActionInstanceId)
                && row.ActionInstanceId.StartsWith(HarnessActionPrefix, StringComparison.Ordinal);
        }

        private static string CurrentPresentationMode()
            => s_suppressHarnessPresentation ? PresentationModeServerSim : PresentationModeFullClient;

        private void UpdateReadoutTiming()
        {
            float frameMs = Time.unscaledDeltaTime * 1000f;
            if (_lastRunStartedAt > 0f)
            {
                _runFrameCount++;
                _runFrameTotalMs += frameMs;
                _runFrameWorstMs = Mathf.Max(_runFrameWorstMs, frameMs);
            }
            _recentFrameWorstMsThisWindow = Mathf.Max(_recentFrameWorstMsThisWindow, frameMs);

            float now = Time.realtimeSinceStartup;
            if (_rateWindowStartedAt < 0f)
                _rateWindowStartedAt = now;

            float elapsed = now - _rateWindowStartedAt;
            if (elapsed < 1f)
                return;

            _eventsPerSecond = _eventsThisWindow / elapsed;
            _updatesPerSecond = _updatesThisWindow / elapsed;
            _contactsPerSecond = _contactsThisWindow / elapsed;
            _eventsThisWindow = 0;
            _updatesThisWindow = 0;
            _contactsThisWindow = 0;
            _recentFrameWorstMs = _recentFrameWorstMsThisWindow;
            _recentFrameWorstMsThisWindow = frameMs;
            _rateWindowStartedAt = now;
        }

        private void ResetReadoutForRun(string scenario)
        {
            _lastRunScenario = scenario;
            _lastRunStartedAt = Time.realtimeSinceStartup;
            _observedActiveProjectiles.Clear();
            _eventTotal = 0;
            _releaseTotal = 0;
            _updateTotal = 0;
            _contactTotal = 0;
            _terminalTotal = 0;
            _blockParryTotal = 0;
            _peakObservedActiveProjectiles = 0;
            _eventsThisWindow = 0;
            _updatesThisWindow = 0;
            _contactsThisWindow = 0;
            _eventsPerSecond = 0f;
            _updatesPerSecond = 0f;
            _contactsPerSecond = 0f;
            _runFrameCount = 0;
            _runFrameTotalMs = 0f;
            _runFrameWorstMs = 0f;
            _recentFrameWorstMs = 0f;
            _recentFrameWorstMsThisWindow = 0f;
            _rateWindowStartedAt = Time.realtimeSinceStartup;
        }

        private void CaptureSummary(CombatProjectileTickMetrics? serverMetrics)
        {
            string summary = BuildSummary(serverMetrics);
            _capturedSummaries.Add(summary);
            _status = $"Captured summary #{_capturedSummaries.Count}.";
        }

        private void CopyLatestSummary(CombatProjectileTickMetrics? serverMetrics)
        {
            string summary = _capturedSummaries.Count > 0
                ? BuildComparisonSummary()
                : BuildSummary(serverMetrics);
            GUIUtility.systemCopyBuffer = summary;
            _status = _capturedSummaries.Count > 0
                ? $"Copied {_capturedSummaries.Count} captured summaries."
                : "Copied current summary.";
        }

        private string BuildComparisonSummary()
        {
            var builder = new StringBuilder();
            builder.AppendLine("# Projectile Load Harness Summaries");
            builder.AppendLine();
            for (int i = 0; i < _capturedSummaries.Count; i++)
            {
                builder.AppendLine($"## Run {i + 1}");
                builder.AppendLine();
                builder.AppendLine(_capturedSummaries[i]);
            }
            return builder.ToString();
        }

        private string BuildSummary(CombatProjectileTickMetrics? serverMetrics)
        {
            var builder = new StringBuilder();
            builder.AppendLine($"captured_at: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            builder.AppendLine($"scenario: {_lastRunScenario}");
            builder.AppendLine($"client_presentation_mode: {CurrentPresentationMode()}");
            builder.AppendLine($"elapsed_seconds: {ElapsedRunSeconds():F1}");
            builder.AppendLine($"observed_active_projectiles: {_observedActiveProjectiles.Count}");
            builder.AppendLine($"peak_observed_projectiles: {_peakObservedActiveProjectiles}");
            builder.AppendLine($"client_events_total: {_eventTotal}");
            builder.AppendLine($"client_events_release: {_releaseTotal}");
            builder.AppendLine($"client_events_update: {_updateTotal}");
            builder.AppendLine($"client_events_contact: {_contactTotal}");
            builder.AppendLine($"client_events_terminal: {_terminalTotal}");
            builder.AppendLine($"client_events_block_parry: {_blockParryTotal}");
            builder.AppendLine($"client_event_rate_per_sec: {_eventsPerSecond:F1}");
            builder.AppendLine($"client_update_rate_per_sec: {_updatesPerSecond:F1}");
            builder.AppendLine($"client_contact_rate_per_sec: {_contactsPerSecond:F1}");
            builder.AppendLine($"client_visuals_active: {CombatProjectileVisualController.DebugActiveProjectileVisualCount}");
            builder.AppendLine($"client_visuals_started: {CombatProjectileVisualController.DebugStartedProjectileVisualCount}");
            builder.AppendLine($"client_visuals_updated: {CombatProjectileVisualController.DebugUpdatedProjectileVisualCount}");
            builder.AppendLine($"client_visuals_terminal: {CombatProjectileVisualController.DebugTerminalProjectileVisualCount}");
            builder.AppendLine($"client_visuals_auto_disposed: {CombatProjectileVisualController.DebugAutoDisposedProjectileVisualCount}");
            builder.AppendLine($"client_missing_prefabs: {CombatProjectileVisualController.DebugMissingProjectilePrefabCount}");
            builder.AppendLine($"client_frame_run_avg_ms: {RunAverageFrameMs():F1}");
            builder.AppendLine($"client_frame_run_worst_ms: {_runFrameWorstMs:F1}");
            builder.AppendLine($"client_frame_recent_worst_ms: {_recentFrameWorstMs:F1}");
            builder.AppendLine($"client_allocated_mb: {Profiler.GetTotalAllocatedMemoryLong() / (1024L * 1024L)}");

            if (serverMetrics == null)
            {
                builder.AppendLine("server_metrics: unavailable");
                return builder.ToString();
            }

            builder.AppendLine($"server_active_projectiles: {serverMetrics.ActiveProjectileCount}");
            builder.AppendLine($"server_rows_updated: {serverMetrics.RowsUpdated}");
            builder.AppendLine($"server_collision_candidate_scans: {serverMetrics.CollisionCandidateScans}");
            builder.AppendLine($"server_world_collision_queries: {serverMetrics.WorldCollisionQueries}");
            builder.AppendLine($"server_world_gameplay_broadphase_candidates: {serverMetrics.WorldGameplayBroadphaseCandidates}");
            builder.AppendLine($"server_world_gameplay_narrowphase_tests: {serverMetrics.WorldGameplayNarrowphaseTests}");
            builder.AppendLine($"server_world_gameplay_full_scan_fallbacks: {serverMetrics.WorldGameplayFullScanFallbacks}");
            builder.AppendLine($"server_world_gameplay_full_scan_fallback_ratio_pct: {RatioPercent(serverMetrics.WorldGameplayFullScanFallbacks, serverMetrics.WorldCollisionQueries):F1}");
            builder.AppendLine($"server_open_world_geometry_point_checks: {serverMetrics.OpenWorldGeometryPointChecks}");
            builder.AppendLine($"server_contacts_resolved: {serverMetrics.ContactsResolved}");
            builder.AppendLine($"server_sample_sequence: {serverMetrics.SampleSequence}");
            builder.AppendLine($"server_peak_active_projectiles: {serverMetrics.PeakActiveProjectileCount}");
            builder.AppendLine($"server_peak_rows_updated: {serverMetrics.PeakRowsUpdated}");
            builder.AppendLine($"server_peak_collision_candidate_scans: {serverMetrics.PeakCollisionCandidateScans}");
            builder.AppendLine($"server_peak_world_collision_queries: {serverMetrics.PeakWorldCollisionQueries}");
            builder.AppendLine($"server_peak_world_gameplay_broadphase_candidates: {serverMetrics.PeakWorldGameplayBroadphaseCandidates}");
            builder.AppendLine($"server_peak_world_gameplay_narrowphase_tests: {serverMetrics.PeakWorldGameplayNarrowphaseTests}");
            builder.AppendLine($"server_peak_world_gameplay_full_scan_fallbacks: {serverMetrics.PeakWorldGameplayFullScanFallbacks}");
            builder.AppendLine($"server_peak_open_world_geometry_point_checks: {serverMetrics.PeakOpenWorldGeometryPointChecks}");
            builder.AppendLine($"server_peak_contacts_resolved: {serverMetrics.PeakContactsResolved}");
            builder.AppendLine($"server_total_rows_updated: {serverMetrics.TotalRowsUpdated}");
            builder.AppendLine($"server_total_collision_candidate_scans: {serverMetrics.TotalCollisionCandidateScans}");
            builder.AppendLine($"server_total_world_collision_queries: {serverMetrics.TotalWorldCollisionQueries}");
            builder.AppendLine($"server_total_world_gameplay_broadphase_candidates: {serverMetrics.TotalWorldGameplayBroadphaseCandidates}");
            builder.AppendLine($"server_total_world_gameplay_narrowphase_tests: {serverMetrics.TotalWorldGameplayNarrowphaseTests}");
            builder.AppendLine($"server_total_world_gameplay_full_scan_fallbacks: {serverMetrics.TotalWorldGameplayFullScanFallbacks}");
            builder.AppendLine($"server_total_world_gameplay_full_scan_fallback_ratio_pct: {RatioPercent(serverMetrics.TotalWorldGameplayFullScanFallbacks, serverMetrics.TotalWorldCollisionQueries):F1}");
            builder.AppendLine($"server_total_open_world_geometry_point_checks: {serverMetrics.TotalOpenWorldGeometryPointChecks}");
            builder.AppendLine($"server_total_contacts_resolved: {serverMetrics.TotalContactsResolved}");
            builder.AppendLine($"server_total_update_events: {serverMetrics.TotalUpdateEventsEmitted}");
            builder.AppendLine($"server_total_contact_events: {serverMetrics.TotalContactEventsEmitted}");
            builder.AppendLine($"server_total_terminal_events: {serverMetrics.TotalTerminalEventsEmitted}");
            builder.AppendLine($"server_total_block_parry_events: {serverMetrics.TotalBlockParryEventsEmitted}");
            builder.AppendLine($"server_update_events: {serverMetrics.UpdateEventsEmitted}");
            builder.AppendLine($"server_contact_events: {serverMetrics.ContactEventsEmitted}");
            builder.AppendLine($"server_terminal_events: {serverMetrics.TerminalEventsEmitted}");
            builder.AppendLine($"server_block_parry_events: {serverMetrics.BlockParryEventsEmitted}");
            builder.AppendLine($"server_linear_projectiles: {serverMetrics.LinearProjectileCount}");
            builder.AppendLine($"server_homing_projectiles: {serverMetrics.HomingProjectileCount}");
            builder.AppendLine($"server_orbit_projectiles: {serverMetrics.OrbitProjectileCount}");
            builder.AppendLine($"server_boomerang_projectiles: {serverMetrics.BoomerangProjectileCount}");
            builder.AppendLine("server_tick_ms: unavailable_inside_reducer");
            builder.AppendLine("server_worst_tick_ms: unavailable_inside_reducer");
            return builder.ToString();
        }

        private float ElapsedRunSeconds()
        {
            return _lastRunStartedAt < 0f
                ? 0f
                : Mathf.Max(0f, Time.realtimeSinceStartup - _lastRunStartedAt);
        }

        private float RunAverageFrameMs()
        {
            return _runFrameCount <= 0 ? 0f : _runFrameTotalMs / _runFrameCount;
        }

        private static float RatioPercent(ulong numerator, ulong denominator)
        {
            return denominator == 0UL ? 0f : (float)(100.0 * numerator / denominator);
        }

        private bool TryParseUInt(string text, string label, out uint value)
        {
            if (uint.TryParse(text, out value))
                return true;

            _status = $"Invalid {label}: {text}";
            return false;
        }

        private bool TryParseULong(string text, string label, out ulong value)
        {
            if (ulong.TryParse(text, out value))
                return true;

            _status = $"Invalid {label}: {text}";
            return false;
        }

        private bool TryParseNonNegativeFloat(string text, string label, out float value)
        {
            if (float.TryParse(text, out value) && value >= 0f)
                return true;

            _status = $"Invalid {label}: {text}";
            return false;
        }
    }
}
#endif
