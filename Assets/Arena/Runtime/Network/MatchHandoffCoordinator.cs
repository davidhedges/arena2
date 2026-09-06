#nullable enable

using System;
using System.Globalization;
using SpacetimeDB;
using UnityEngine;
using UnityEngine.SceneManagement;
using Arena.Entity;
using Arena.World;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Arena.Network
{
    internal enum MatchHandoffState
    {
        ConnectingToHub,
        HubReady,
        WaitingForAssignment,
        ConnectingToMatch,
        InMatch,
        ReturningToHub,
    }

    internal readonly struct ValidatedMatchAssignment
    {
        internal ValidatedMatchAssignment(
            string ticketId,
            string matchId,
            string serverUri,
            string databaseIdentity,
            string matchBuildId,
            string mapId,
            string sceneName,
            bool isOpenWorld)
        {
            TicketId = ticketId;
            MatchId = matchId;
            ServerUri = serverUri;
            DatabaseIdentity = databaseIdentity;
            MatchBuildId = matchBuildId;
            MapId = mapId;
            SceneName = sceneName;
            IsOpenWorld = isOpenWorld;
        }

        internal string TicketId { get; }
        internal string MatchId { get; }
        internal string ServerUri { get; }
        internal string DatabaseIdentity { get; }
        internal string MatchBuildId { get; }
        internal string MapId { get; }
        internal string SceneName { get; }

        /// A disposable open world runs the full server module, so it uses the
        /// ordinary gameplay subscription plan rather than the trimmed PvP one.
        internal bool IsOpenWorld { get; }
    }

    internal static class MatchAssignmentValidator
    {
        internal static bool TryValidate(
            HubMatchStatusSnapshot status,
            string hubServerUri,
            out ValidatedMatchAssignment assignment,
            out string error)
        {
            assignment = default;
            if (!string.Equals(status.Status, "READY", StringComparison.Ordinal))
            {
                error = "The Hub assignment is not ready.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(status.TicketId)
                || string.IsNullOrWhiteSpace(status.MatchId)
                || string.IsNullOrWhiteSpace(status.MatchBuildId)
                || string.IsNullOrWhiteSpace(status.MapId)
                || string.IsNullOrWhiteSpace(status.ServerUri)
                || string.IsNullOrWhiteSpace(status.DatabaseIdentity))
            {
                error = "The Hub returned an incomplete match assignment.";
                return false;
            }

            // A match is assigned an authored arena map; an open world is
            // assigned an authored scene. Both arrive in the same column, so
            // the ticket's queue kind chooses the catalog that must accept it.
            string mapId = status.MapId.Trim();
            string sceneName;
            if (status.IsOpenWorld)
            {
                if (!OpenWorldTravelCatalog.IsRegisteredOpenWorldScene(mapId))
                {
                    error = $"The Hub assigned an unknown open-world destination ({mapId}).";
                    return false;
                }
                sceneName = mapId;
            }
            else if (!ArenaMapCatalog.TryResolveSceneName(mapId, out sceneName))
            {
                error = $"The Hub assigned an unsupported arena map ({mapId}).";
                return false;
            }

            if (!Uri.TryCreate(status.ServerUri, UriKind.Absolute, out Uri? uri)
                || (!string.Equals(uri.Scheme, "ws", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(uri.Scheme, "wss", StringComparison.OrdinalIgnoreCase))
                || !string.IsNullOrEmpty(uri.UserInfo)
                || (!string.IsNullOrEmpty(uri.AbsolutePath) && uri.AbsolutePath != "/"))
            {
                error = "The Hub returned an invalid match server address.";
                return false;
            }

            if (!string.Equals(
                    NetworkEnvironmentConfig.CredentialScopeForServer(status.ServerUri),
                    NetworkEnvironmentConfig.CredentialScopeForServer(hubServerUri),
                    StringComparison.Ordinal))
            {
                error = "The match assignment points at a different SpacetimeDB cluster.";
                return false;
            }

            string databaseIdentity = status.DatabaseIdentity.Trim();
            if (databaseIdentity.Length != 64 || !IsLowerOrUpperHex(databaseIdentity))
            {
                error = "The Hub returned an invalid match database identity.";
                return false;
            }

            // Both timestamps originate at the Hub. Do not compare a server
            // deadline to this PC's UTC clock (or a previous match's estimate).
            // Actual expiry is enforced by Hub maintenance and match admission.
            if (!status.AssignmentExpiresAtMicros.HasValue
                || status.AssignmentExpiresAtMicros.Value <= status.UpdatedAtMicros)
            {
                error = "The Hub returned an invalid match assignment deadline.";
                return false;
            }

            assignment = new ValidatedMatchAssignment(
                status.TicketId.Trim(),
                status.MatchId.Trim(),
                status.ServerUri.Trim(),
                databaseIdentity.ToLowerInvariant(),
                status.MatchBuildId.Trim(),
                status.IsOpenWorld ? mapId : mapId.ToUpperInvariant(),
                sceneName,
                status.IsOpenWorld);
            error = string.Empty;
            return true;
        }

        private static bool IsLowerOrUpperHex(string value)
        {
            foreach (char c in value)
            {
                if (!((c >= '0' && c <= '9')
                      || (c >= 'a' && c <= 'f')
                      || (c >= 'A' && c <= 'F')))
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>
    /// One monotonic, correlated trace from the Hub match button through the
    /// loaded match scene. Server-owned Hub timestamps are reported separately
    /// so client/server clock skew cannot corrupt client elapsed durations.
    /// </summary>
    internal static class MatchStartupTiming
    {
        private static bool s_active;
        private static long s_startedTimestamp;
        private static long s_previousTimestamp;
        private static string s_ticketId = "-";
        private static string s_matchId = "-";
        private static string s_lastHubStatusKey = string.Empty;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset()
        {
            s_active = false;
            s_startedTimestamp = 0;
            s_previousTimestamp = 0;
            s_ticketId = "-";
            s_matchId = "-";
            s_lastHubStatusKey = string.Empty;
        }

        internal static void BeginRequest()
        {
            BeginTrace();
            Record("request_sent");
        }

        internal static void ObserveHubStatus(HubMatchStatusSnapshot status)
        {
            if (!s_active)
            {
                if (!status.IsActive)
                    return;

                BeginTrace();
                Record("trace_recovered", "origin=existing_hub_status");
            }

            s_ticketId = Token(status.TicketId);
            if (!string.IsNullOrWhiteSpace(status.MatchId))
                s_matchId = Token(status.MatchId!);

            string statusKey = $"{s_ticketId}|{status.Status}";
            if (string.Equals(statusKey, s_lastHubStatusKey, StringComparison.Ordinal))
                return;
            s_lastHubStatusKey = statusKey;

            double serverStatusMs = Math.Max(
                0d,
                (status.UpdatedAtMicros - status.CreatedAtMicros) / 1000d);
            string details = $"server_status_ms={FormatMilliseconds(serverStatusMs)}";
            if (status.ReadyAtMicros.HasValue)
            {
                double serverReadyMs = Math.Max(
                    0d,
                    (status.ReadyAtMicros.Value - status.CreatedAtMicros) / 1000d);
                details += $" server_ready_ms={FormatMilliseconds(serverReadyMs)}";
            }

            Record($"hub_{status.Status.ToLowerInvariant()}", details);
        }

        internal static void Record(string stage, string details = "")
        {
            if (!s_active)
                return;

            long now = Stopwatch.GetTimestamp();
            double elapsedMs = ToMilliseconds(now - s_startedTimestamp);
            double deltaMs = ToMilliseconds(now - s_previousTimestamp);
            s_previousTimestamp = now;
            string suffix = string.IsNullOrWhiteSpace(details) ? string.Empty : $" {details}";
            Debug.Log(
                $"[MatchStartupTiming] stage={Token(stage)} "
                + $"elapsed_ms={FormatMilliseconds(elapsedMs)} "
                + $"delta_ms={FormatMilliseconds(deltaMs)} "
                + $"ticket={s_ticketId} match={s_matchId}{suffix}");
        }

        internal static void Fail(string reasonCode)
        {
            if (!s_active)
                return;

            Record("startup_failed", $"reason={Token(reasonCode)}");
            s_active = false;
        }

        internal static void CompleteSceneLoad()
        {
            if (!s_active)
                return;

            Record("scene_loaded");
            s_active = false;
        }

        private static void BeginTrace()
        {
            s_active = true;
            s_startedTimestamp = Stopwatch.GetTimestamp();
            s_previousTimestamp = s_startedTimestamp;
            s_ticketId = "-";
            s_matchId = "-";
            s_lastHubStatusKey = string.Empty;
        }

        private static double ToMilliseconds(long timestampDelta)
            => timestampDelta * 1000d / Stopwatch.Frequency;

        private static string FormatMilliseconds(double value)
            => value.ToString("F1", CultureInfo.InvariantCulture);

        private static string Token(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "-";

            return value.Trim()
                .Replace(' ', '_')
                .Replace('\t', '_')
                .Replace('\r', '_')
                .Replace('\n', '_');
        }
    }

    /// <summary>
    /// Owns the brief Hub/match overlap. No UI or generated database type is
    /// allowed to decide when credentials, callbacks, caches, or scenes move.
    /// </summary>
    [DefaultExecutionOrder(-60)]
    internal sealed class MatchHandoffCoordinator : MonoBehaviour
    {
        private const float MatchConnectTimeoutSeconds = 20f;
        private const float AssignmentWaitTimeoutSeconds = 45f;

        internal static MatchHandoffCoordinator? Instance { get; private set; }

        internal event Action? Changed;

        private HubNetworkManager _hub = null!;
        private NetworkManager _match = null!;
        private float _deadlineRealtime;
        private string? _activeTicketId;
        private string? _ignoredTicketId;
        private string _activeMatchSceneName = ArenaMapCatalog.DefaultSceneName;

        internal MatchHandoffState State { get; private set; } = MatchHandoffState.ConnectingToHub;
        internal string LastError { get; private set; } = string.Empty;
        internal bool IsMatchRequestPending
            => State == MatchHandoffState.WaitingForAssignment
               || State == MatchHandoffState.ConnectingToMatch;
        internal bool CanRequestMatch
            => State == MatchHandoffState.HubReady
               && _hub.IsReady
               && !_hub.HasActiveMatchRequest;

        internal string StatusMessage
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(LastError))
                    return LastError;

                return State switch
                {
                    MatchHandoffState.ConnectingToHub => "CONNECTING TO HUB…",
                    MatchHandoffState.WaitingForAssignment => ResolveQueueStatus(),
                    MatchHandoffState.ConnectingToMatch => "JOINING MATCH…",
                    MatchHandoffState.ReturningToHub => "RETURNING TO HUB…",
                    _ => string.Empty,
                };
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (!string.Equals(SceneManager.GetActiveScene().name, "Hub", StringComparison.Ordinal))
                return;

            EnsureInstance();
        }

        internal static MatchHandoffCoordinator EnsureInstance()
        {
            if (Instance != null)
                return Instance;

            var go = new GameObject(nameof(MatchHandoffCoordinator));
            DontDestroyOnLoad(go);
            return go.AddComponent<MatchHandoffCoordinator>();
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            DontDestroyOnLoad(gameObject);
            _hub = HubNetworkManager.EnsureInstance();
            _match = NetworkManager.EnsureInstance();
            _hub.Changed += OnHubChanged;
            _hub.Ready += OnHubReady;
            _hub.UnexpectedDisconnect += OnHubUnexpectedDisconnect;
            _match.ProvisionedMatchReady += OnMatchReady;
            _match.ProvisionedMatchFailed += OnMatchFailed;
            _match.ProvisionedMatchDisconnected += OnMatchDisconnected;
            SceneManager.sceneLoaded += OnSceneLoaded;
            _hub.ConnectToSelectedEnvironment();
            ReconcileHubState();
        }

        internal bool RequestUnranked2V2BotMatch()
            => RequestTicket(
                () => _hub.RequestUnranked2V2BotMatch(),
                ArenaMapCatalog.DefaultSceneName,
                "The Hub did not accept the match request.");

        /// Travels to a disposable open world. The Hub is the only connection
        /// open while the Hub scene is active, so travel must be requested
        /// here rather than on the gameplay connection
        /// (docs/open-world-disposable-instances-2026-08-18.md section 1).
        internal bool RequestOpenWorldInstance(string destination)
        {
            if (!OpenWorldTravelCatalog.IsRegisteredOpenWorldScene(destination))
            {
                LastError = $"Unknown open-world destination '{destination}'.";
                NotifyChanged();
                return false;
            }

            return RequestTicket(
                () => _hub.RequestOpenWorldInstance(destination),
                destination,
                "The Hub did not accept the travel request.");
        }

        private bool RequestTicket(
            Func<bool> submit,
            string expectedSceneName,
            string rejectionMessage)
        {
            if (!CanRequestMatch)
            {
                LastError = _hub.IsReady
                    ? "A match request is already in progress."
                    : "The Hub is still connecting. Try again in a moment.";
                NotifyChanged();
                return false;
            }

            LastError = string.Empty;
            _activeTicketId = null;
            _ignoredTicketId = null;
            _activeMatchSceneName = expectedSceneName;
            _deadlineRealtime = Time.unscaledTime + AssignmentWaitTimeoutSeconds;
            State = MatchHandoffState.WaitingForAssignment;
            MatchStartupTiming.BeginRequest();
            bool submitted = submit();
            if (!submitted)
            {
                State = MatchHandoffState.HubReady;
                LastError = string.IsNullOrWhiteSpace(_hub.LastError)
                    ? rejectionMessage
                    : _hub.LastError;
                MatchStartupTiming.Fail("request_rejected");
            }
            NotifyChanged();
            return submitted;
        }

        internal bool ReturnToHub()
        {
            if (!_match.IsProvisionedMatchConnection
                || State == MatchHandoffState.ReturningToHub)
            {
                return false;
            }

            BeginReturnToHub(string.Empty);
            return true;
        }

        private void OnHubReady()
        {
            if (State == MatchHandoffState.ReturningToHub)
            {
                CancelIgnoredTicketIfVisible();
                State = MatchHandoffState.HubReady;
                NotifyChanged();
                return;
            }

            if (State == MatchHandoffState.ConnectingToHub
                && string.IsNullOrWhiteSpace(_activeTicketId))
            {
                LastError = string.Empty;
            }

            ReconcileHubState();
        }

        private void OnHubChanged()
        {
            if (State == MatchHandoffState.InMatch)
                return;

            ReconcileHubState();
        }

        private void ReconcileHubState()
        {
            if (!_hub.IsReady)
            {
                if (State != MatchHandoffState.ConnectingToMatch
                    && State != MatchHandoffState.ReturningToHub)
                {
                    State = MatchHandoffState.ConnectingToHub;
                    NotifyChanged();
                }
                return;
            }

            HubMatchStatusSnapshot? optionalStatus = _hub.MatchStatus;
            if (!optionalStatus.HasValue)
            {
                if (State == MatchHandoffState.WaitingForAssignment
                    && !_hub.HasActiveMatchRequest
                    && !string.IsNullOrWhiteSpace(_hub.LastError))
                {
                    LastError = _hub.LastError;
                }
                State = State == MatchHandoffState.ConnectingToMatch
                    ? State
                    : MatchHandoffState.HubReady;
                NotifyChanged();
                return;
            }

            HubMatchStatusSnapshot status = optionalStatus.Value;
            if (string.Equals(status.TicketId, _ignoredTicketId, StringComparison.Ordinal))
            {
                if (status.IsActive)
                    _hub.CancelCurrentTicket();
                else
                    _ignoredTicketId = null;
                State = MatchHandoffState.HubReady;
                NotifyChanged();
                return;
            }

            MatchStartupTiming.ObserveHubStatus(status);

            if (string.Equals(status.Status, "FAILED", StringComparison.Ordinal))
            {
                LastError = string.Equals(
                        status.FailureCode,
                        "ARTIFACT_STALE",
                        StringComparison.Ordinal)
                    ? "Local match build is stale. Run ops/setup-local-multiplayer.sh setup."
                    : string.IsNullOrWhiteSpace(status.FailureCode)
                        ? "Match provisioning failed."
                        : $"Match provisioning failed ({status.FailureCode}).";
                MatchStartupTiming.Fail("hub_provisioning_failed");
                State = MatchHandoffState.HubReady;
                NotifyChanged();
                return;
            }

            if (string.Equals(status.Status, "CLOSED", StringComparison.Ordinal))
            {
                MatchStartupTiming.Fail("hub_ticket_closed");
                _activeTicketId = null;
                LastError = string.Empty;
                State = MatchHandoffState.HubReady;
                NotifyChanged();
                return;
            }

            if (!string.Equals(status.Status, "READY", StringComparison.Ordinal))
            {
                _activeTicketId = status.TicketId;
                LastError = string.Empty;
                if (State != MatchHandoffState.WaitingForAssignment)
                    _deadlineRealtime = Time.unscaledTime + AssignmentWaitTimeoutSeconds;
                State = MatchHandoffState.WaitingForAssignment;
                NotifyChanged();
                return;
            }

            if (State == MatchHandoffState.ConnectingToMatch
                && string.Equals(_activeTicketId, status.TicketId, StringComparison.Ordinal))
            {
                return;
            }

            NetworkEnvironmentEndpoint hubEndpoint = NetworkEnvironmentConfig.CurrentHubEndpoint;
            if (!MatchAssignmentValidator.TryValidate(
                    status,
                    hubEndpoint.ServerUri,
                    out ValidatedMatchAssignment assignment,
                    out string validationError))
            {
                RollBackInHub(validationError, status.TicketId);
                return;
            }

            Identity? hubIdentity = _hub.Identity;
            if (!hubIdentity.HasValue)
            {
                RollBackInHub("The Hub identity disappeared during match handoff.", status.TicketId);
                return;
            }

            _activeTicketId = assignment.TicketId;
            _activeMatchSceneName = assignment.SceneName;
            State = MatchHandoffState.ConnectingToMatch;
            LastError = string.Empty;
            _deadlineRealtime = Time.unscaledTime + MatchConnectTimeoutSeconds;
            MatchStartupTiming.Record("assignment_validated");
            NotifyChanged();
            _match.ConnectToProvisionedMatch(
                assignment.ServerUri,
                assignment.DatabaseIdentity,
                hubIdentity.Value,
                assignment.MatchId,
                assignment.MatchBuildId,
                assignment.IsOpenWorld);
        }

        private void OnMatchReady(Identity identity)
        {
            if (State != MatchHandoffState.ConnectingToMatch)
                return;

            Identity? hubIdentity = _hub.Identity;
            if (!hubIdentity.HasValue || identity != hubIdentity.Value)
            {
                BeginReturnToHub("The Hub and match identities did not match.");
                return;
            }

            State = MatchHandoffState.InMatch;
            LastError = string.Empty;
            MatchStartupTiming.Record("match_initial_state_ready");
            _hub.DisconnectForMatchHandoff();
            NotifyChanged();

            // The local PlayerWorld callback normally requests this scene in
            // the same FrameTick. This idempotent request also covers an
            // out-of-order callback while preserving the subscription gate.
            MatchStartupTiming.Record("scene_requested");
            RuntimeSceneTransitionQueue.Request(_activeMatchSceneName);
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode _)
        {
            if (State == MatchHandoffState.InMatch
                && string.Equals(scene.name, _activeMatchSceneName, StringComparison.Ordinal))
            {
                MatchStartupTiming.CompleteSceneLoad();
            }
        }

        private void OnMatchFailed(string error)
        {
            if (State == MatchHandoffState.ConnectingToMatch
                || State == MatchHandoffState.InMatch)
            {
                MatchStartupTiming.Fail("match_connection_failed");
                BeginReturnToHub(error);
            }
        }

        private void OnMatchDisconnected(string reason)
        {
            if (State == MatchHandoffState.ConnectingToMatch
                || State == MatchHandoffState.InMatch)
            {
                MatchStartupTiming.Fail("match_disconnected");
                BeginReturnToHub($"Match connection lost: {reason}");
            }
        }

        private void OnHubUnexpectedDisconnect(string error)
        {
            if (State == MatchHandoffState.ConnectingToMatch)
            {
                BeginReturnToHub(error);
                return;
            }

            if (State != MatchHandoffState.InMatch)
            {
                LastError = error;
                State = MatchHandoffState.ConnectingToHub;
                NotifyChanged();
            }
        }

        private void BeginReturnToHub(string error)
        {
            if (State == MatchHandoffState.ReturningToHub)
                return;

            MatchStartupTiming.Fail(
                string.IsNullOrWhiteSpace(error) ? "return_to_hub" : "handoff_aborted");
            if (!string.IsNullOrWhiteSpace(_activeTicketId))
                _ignoredTicketId = _activeTicketId;
            LastError = error;
            State = MatchHandoffState.ReturningToHub;
            RuntimeSceneTransitionQueue.BeginExplicitHubReturn();
            _match.DisconnectProvisionedMatch();
            _hub.ConnectToSelectedEnvironment();
            RuntimeSceneTransitionQueue.RequestExplicitHubReturn();
            NotifyChanged();
        }

        private void RollBackInHub(string error, string ticketId)
        {
            MatchStartupTiming.Fail("hub_handoff_rollback");
            _ignoredTicketId = ticketId;
            _activeTicketId = ticketId;
            LastError = error;
            _match.DisconnectProvisionedMatch();
            _hub.CancelCurrentTicket();
            State = MatchHandoffState.HubReady;
            NotifyChanged();
        }

        private void CancelIgnoredTicketIfVisible()
        {
            HubMatchStatusSnapshot? status = _hub.MatchStatus;
            if (status.HasValue
                && string.Equals(status.Value.TicketId, _ignoredTicketId, StringComparison.Ordinal)
                && status.Value.IsActive)
            {
                _hub.CancelCurrentTicket();
            }
        }

        private string ResolveQueueStatus()
        {
            HubMatchStatusSnapshot? snapshot = _hub.MatchStatus;
            string? status = snapshot?.Status;
            if (snapshot?.IsOpenWorld == true)
            {
                return status switch
                {
                    "CLAIMED" => "CLAIMING WORLD…",
                    "PROVISIONING" => "BUILDING WORLD…",
                    "READY" => "WORLD READY…",
                    _ => "REQUESTING WORLD…",
                };
            }

            return status switch
            {
                "CLAIMED" => "MATCH CLAIMED…",
                "PROVISIONING" => "BUILDING MATCH…",
                "READY" => "MATCH READY…",
                _ => "FINDING MATCH…",
            };
        }

        private void Update()
        {
            if (State == MatchHandoffState.WaitingForAssignment
                && Time.unscaledTime >= _deadlineRealtime)
            {
                string ticketId = _hub.MatchStatus?.TicketId ?? _activeTicketId ?? string.Empty;
                RollBackInHub("Match provisioning timed out. Please try again.", ticketId);
            }
            else if (State == MatchHandoffState.ConnectingToMatch
                     && Time.unscaledTime >= _deadlineRealtime)
            {
                BeginReturnToHub("Connecting to the assigned match timed out.");
            }
        }

        private void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
            if (_hub != null)
            {
                _hub.Changed -= OnHubChanged;
                _hub.Ready -= OnHubReady;
                _hub.UnexpectedDisconnect -= OnHubUnexpectedDisconnect;
            }
            if (_match != null)
            {
                _match.ProvisionedMatchReady -= OnMatchReady;
                _match.ProvisionedMatchFailed -= OnMatchFailed;
                _match.ProvisionedMatchDisconnected -= OnMatchDisconnected;
            }
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        private void NotifyChanged() => Changed?.Invoke();
    }
}
