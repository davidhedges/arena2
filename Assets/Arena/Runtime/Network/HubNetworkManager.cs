#nullable enable

using System;
using SpacetimeDB;
using UnityEngine;
using UnityEngine.SceneManagement;
using HubConnection = Arena.HubDb.DbConnection;
using HubEventContext = Arena.HubDb.EventContext;
using HubErrorContext = Arena.HubDb.ErrorContext;
using HubReducerEventContext = Arena.HubDb.ReducerEventContext;
using HubSubscriptionEventContext = Arena.HubDb.SubscriptionEventContext;
using HubSubscriptionHandle = Arena.HubDb.SubscriptionHandle;
using HubPlayerRow = Arena.HubDb.MyHubPlayer;
using HubMatchStatusRow = Arena.HubDb.MyMatchStatus;

namespace Arena.Network
{
    internal enum HubConnectionState
    {
        Disconnected,
        Connecting,
        Subscribing,
        Ready,
        Error,
    }

    /// <summary>
    /// Generated Hub rows stop at this boundary. Hub screens consume these
    /// tiny snapshots and never acquire the Hub DbConnection or schema types.
    /// </summary>
    internal readonly struct HubPlayerSnapshot
    {
        internal HubPlayerSnapshot(Identity identity, string displayName)
        {
            Identity = identity;
            DisplayName = displayName;
        }

        internal Identity Identity { get; }
        internal string DisplayName { get; }
    }

    internal readonly struct HubMatchStatusSnapshot
    {
        internal HubMatchStatusSnapshot(
            string ticketId,
            string status,
            string? failureCode,
            string? matchId,
            string? serverUri,
            string? databaseIdentity,
            string? matchBuildId,
            string? mapId,
            long createdAtMicros,
            long updatedAtMicros,
            long ticketExpiresAtMicros,
            long? readyAtMicros,
            long? assignmentExpiresAtMicros)
        {
            TicketId = ticketId;
            Status = status;
            FailureCode = failureCode;
            MatchId = matchId;
            ServerUri = serverUri;
            DatabaseIdentity = databaseIdentity;
            MatchBuildId = matchBuildId;
            MapId = mapId;
            CreatedAtMicros = createdAtMicros;
            UpdatedAtMicros = updatedAtMicros;
            TicketExpiresAtMicros = ticketExpiresAtMicros;
            ReadyAtMicros = readyAtMicros;
            AssignmentExpiresAtMicros = assignmentExpiresAtMicros;
        }

        internal string TicketId { get; }
        internal string Status { get; }
        internal string? FailureCode { get; }
        internal string? MatchId { get; }
        internal string? ServerUri { get; }
        internal string? DatabaseIdentity { get; }
        internal string? MatchBuildId { get; }
        internal string? MapId { get; }
        internal long CreatedAtMicros { get; }
        internal long UpdatedAtMicros { get; }
        internal long TicketExpiresAtMicros { get; }
        internal long? ReadyAtMicros { get; }
        internal long? AssignmentExpiresAtMicros { get; }

        internal bool IsActive
            => string.Equals(Status, "PENDING", StringComparison.Ordinal)
               || string.Equals(Status, "CLAIMED", StringComparison.Ordinal)
               || string.Equals(Status, "PROVISIONING", StringComparison.Ordinal)
               || string.Equals(Status, "READY", StringComparison.Ordinal);
    }

    /// <summary>
    /// Persistent-control-plane connection. It subscribes only to the two
    /// caller-filtered Hub views and owns no gameplay cache or simulation work.
    /// </summary>
    [DefaultExecutionOrder(-70)]
    internal sealed class HubNetworkManager : MonoBehaviour
    {
        private const float RetryDelaySeconds = 2f;

        internal static HubNetworkManager? Instance { get; private set; }

        internal event Action? Changed;
        internal event Action? Ready;
        internal event Action<string>? UnexpectedDisconnect;

        private HubConnection? _conn;
        private HubSubscriptionHandle? _subscription;
        private NetworkEnvironmentEndpoint _activeEndpoint =
            NetworkEnvironmentConfig.HubEndpointFor(NetworkEnvironmentKind.Local);
        private Identity _identity;
        private bool _hasIdentity;
        private bool _maintainConnection;
        private bool _intentionalDisconnect;
        private float _nextRetryRealtime;
        private int _connectionGeneration;
        private string? _activeClientRequestId;
        private bool _requestAwaitingConfirmation;
        private HubPlayerSnapshot? _player;
        private HubMatchStatusSnapshot? _matchStatus;

        internal HubConnectionState State { get; private set; } = HubConnectionState.Disconnected;
        internal string LastError { get; private set; } = string.Empty;
        internal bool IsReady => State == HubConnectionState.Ready && _conn != null && _hasIdentity;
        internal Identity? Identity => _hasIdentity ? _identity : null;
        internal NetworkEnvironmentEndpoint ActiveEndpoint => _activeEndpoint;
        internal HubPlayerSnapshot? Player => _player;
        internal HubMatchStatusSnapshot? MatchStatus => _matchStatus;
        internal bool HasActiveMatchRequest
            => _requestAwaitingConfirmation || (_matchStatus?.IsActive ?? false);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (!string.Equals(SceneManager.GetActiveScene().name, "Hub", StringComparison.Ordinal))
                return;

            EnsureInstance().ConnectToSelectedEnvironment();
        }

        internal static HubNetworkManager EnsureInstance()
        {
            if (Instance != null)
                return Instance;

            var go = new GameObject(nameof(HubNetworkManager));
            DontDestroyOnLoad(go);
            return go.AddComponent<HubNetworkManager>();
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
        }

        internal void ConnectToSelectedEnvironment()
        {
            _maintainConnection = true;
            _activeEndpoint = NetworkEnvironmentConfig.CurrentHubEndpoint;
            if (_conn != null || State == HubConnectionState.Connecting || State == HubConnectionState.Subscribing)
                return;

            ConnectNow();
        }

        internal void ReconnectToSelectedEnvironment()
        {
            _maintainConnection = true;
            _intentionalDisconnect = true;
            DisposeConnection();
            _activeEndpoint = NetworkEnvironmentConfig.CurrentHubEndpoint;
            State = HubConnectionState.Disconnected;
            ConnectNow();
        }

        internal bool RequestUnranked2V2BotMatch()
        {
            if (!IsReady || _conn == null)
            {
                SetError("The Hub is still connecting. Try again in a moment.", disconnect: false);
                return false;
            }

            if (HasActiveMatchRequest)
                return false;

            _activeClientRequestId = Guid.NewGuid().ToString("N");
            _requestAwaitingConfirmation = true;
            LastError = string.Empty;
            NotifyChanged();
            _conn.Reducers.RequestUnranked2V2BotMatch(_activeClientRequestId);
            return true;
        }

        internal void CancelCurrentTicket()
        {
            if (!IsReady || _conn == null || !_matchStatus.HasValue)
                return;

            string ticketId = _matchStatus.Value.TicketId;
            if (!string.IsNullOrWhiteSpace(ticketId))
                _conn.Reducers.CancelMatchTicket(ticketId);
        }

        internal void DisconnectForMatchHandoff()
        {
            _maintainConnection = false;
            _intentionalDisconnect = true;
            DisposeConnection();
            State = HubConnectionState.Disconnected;
            NotifyChanged();
        }

        private void ConnectNow()
        {
            DisposeConnection();
            _intentionalDisconnect = false;
            LastError = string.Empty;
            State = HubConnectionState.Connecting;
            NotifyChanged();

            int generation = ++_connectionGeneration;
            string? token = NetworkEnvironmentConfig.LoadAuthToken(_activeEndpoint);
            Debug.Log(
                $"[HubNetworkManager] Connecting to {_activeEndpoint.ServerUri} "
                + $"database={_activeEndpoint.ModuleName}.");
            _conn = HubConnection.Builder()
                .WithUri(_activeEndpoint.ServerUri)
                .WithDatabaseName(_activeEndpoint.ModuleName)
                .WithToken(token)
                .OnConnect((conn, identity, issuedToken) =>
                    OnConnected(generation, conn, identity, issuedToken))
                .OnConnectError(error => OnConnectError(generation, error))
                .OnDisconnect(OnDisconnected)
                .Build();
        }

        private void OnConnected(
            int generation,
            HubConnection conn,
            Identity identity,
            string token)
        {
            if (generation != _connectionGeneration || !ReferenceEquals(conn, _conn))
                return;

            _identity = identity;
            _hasIdentity = true;
            if (!string.IsNullOrWhiteSpace(token))
                NetworkEnvironmentConfig.SaveAuthToken(_activeEndpoint, token);

            BindRows(conn);
            conn.Reducers.OnRequestUnranked2V2BotMatch += OnRequestMatchResult;
            State = HubConnectionState.Subscribing;
            NotifyChanged();
            _subscription = conn
                .SubscriptionBuilder()
                .OnApplied(OnSubscriptionApplied)
                .OnError(OnSubscriptionError)
                .Subscribe(new[]
                {
                    new Arena.HubDb.QueryBuilder().From.MyHubPlayer().ToSql(),
                    new Arena.HubDb.QueryBuilder().From.MyMatchStatus().ToSql(),
                });
        }

        private void OnSubscriptionApplied(HubSubscriptionEventContext context)
        {
            if (_conn == null || !ReferenceEquals(context.Db, _conn.Db))
                return;

            RefreshSnapshotsFromCache(_conn);
            State = HubConnectionState.Ready;
            LastError = string.Empty;
            NotifyChanged();
            Ready?.Invoke();

            // A transport loss between send and reducer confirmation is safe
            // to retry because the request ID is deliberately stable.
            if (_requestAwaitingConfirmation
                && !string.IsNullOrWhiteSpace(_activeClientRequestId)
                && !_matchStatus.HasValue)
            {
                _conn.Reducers.RequestUnranked2V2BotMatch(_activeClientRequestId);
            }
        }

        private void OnSubscriptionError(HubErrorContext context, Exception error)
        {
            if (_conn == null || !ReferenceEquals(context.Db, _conn.Db))
                return;

            SetError($"Hub subscription failed: {error.Message}", disconnect: true);
        }

        private void OnConnectError(int generation, Exception error)
        {
            if (generation != _connectionGeneration)
                return;

            SetError($"Unable to connect to the Hub: {error.Message}", disconnect: true);
        }

        private void OnDisconnected(HubConnection conn, Exception? error)
        {
            if (!ReferenceEquals(conn, _conn))
                return;

            bool expected = _intentionalDisconnect || !_maintainConnection;
            UnbindRows(conn);
            _conn = null;
            _subscription = null;
            _hasIdentity = false;
            _player = null;
            _matchStatus = null;
            State = expected ? HubConnectionState.Disconnected : HubConnectionState.Error;
            if (!expected)
            {
                LastError = $"Hub connection lost: {error?.Message ?? "transport closed"}";
                _nextRetryRealtime = Time.unscaledTime + RetryDelaySeconds;
            }
            NotifyChanged();
            if (!expected)
                UnexpectedDisconnect?.Invoke(LastError);
        }

        private void BindRows(HubConnection conn)
        {
            conn.Db.MyHubPlayer.OnInsert += OnHubPlayerInsert;
            conn.Db.MyHubPlayer.OnUpdate += OnHubPlayerUpdate;
            conn.Db.MyHubPlayer.OnDelete += OnHubPlayerDelete;
            conn.Db.MyMatchStatus.OnInsert += OnMatchStatusInsert;
            conn.Db.MyMatchStatus.OnUpdate += OnMatchStatusUpdate;
            conn.Db.MyMatchStatus.OnDelete += OnMatchStatusDelete;
        }

        private void UnbindRows(HubConnection conn)
        {
            conn.Db.MyHubPlayer.OnInsert -= OnHubPlayerInsert;
            conn.Db.MyHubPlayer.OnUpdate -= OnHubPlayerUpdate;
            conn.Db.MyHubPlayer.OnDelete -= OnHubPlayerDelete;
            conn.Db.MyMatchStatus.OnInsert -= OnMatchStatusInsert;
            conn.Db.MyMatchStatus.OnUpdate -= OnMatchStatusUpdate;
            conn.Db.MyMatchStatus.OnDelete -= OnMatchStatusDelete;
            conn.Reducers.OnRequestUnranked2V2BotMatch -= OnRequestMatchResult;
        }

        private void OnHubPlayerInsert(HubEventContext _, HubPlayerRow row) => ApplyPlayer(row);
        private void OnHubPlayerUpdate(HubEventContext _, HubPlayerRow __, HubPlayerRow row) => ApplyPlayer(row);

        private void OnHubPlayerDelete(HubEventContext _, HubPlayerRow __)
        {
            _player = null;
            NotifyChanged();
        }

        private void OnMatchStatusInsert(HubEventContext _, HubMatchStatusRow row) => ApplyMatchStatus(row);
        private void OnMatchStatusUpdate(HubEventContext _, HubMatchStatusRow __, HubMatchStatusRow row) => ApplyMatchStatus(row);

        private void OnMatchStatusDelete(HubEventContext _, HubMatchStatusRow __)
        {
            _matchStatus = null;
            _activeClientRequestId = null;
            _requestAwaitingConfirmation = false;
            NotifyChanged();
        }

        private void ApplyPlayer(HubPlayerRow row)
        {
            _player = new HubPlayerSnapshot(row.Identity, row.DisplayName);
            NotifyChanged();
        }

        private void ApplyMatchStatus(HubMatchStatusRow row)
        {
            _matchStatus = new HubMatchStatusSnapshot(
                row.TicketId,
                row.Status,
                row.FailureCode,
                row.MatchId,
                row.ServerUri,
                row.DatabaseIdentity,
                row.MatchBuildId,
                row.MapId,
                row.CreatedAt.MicrosecondsSinceUnixEpoch,
                row.UpdatedAt.MicrosecondsSinceUnixEpoch,
                row.ExpiresAt.MicrosecondsSinceUnixEpoch,
                row.ReadyAt?.MicrosecondsSinceUnixEpoch,
                row.AssignmentExpiresAt?.MicrosecondsSinceUnixEpoch);
            _requestAwaitingConfirmation = false;
            if (!string.Equals(row.Status, "FAILED", StringComparison.Ordinal))
                LastError = string.Empty;
            NotifyChanged();
        }

        private void RefreshSnapshotsFromCache(HubConnection conn)
        {
            _player = null;
            foreach (HubPlayerRow row in conn.Db.MyHubPlayer.Iter())
            {
                _player = new HubPlayerSnapshot(row.Identity, row.DisplayName);
                break;
            }

            _matchStatus = null;
            foreach (HubMatchStatusRow row in conn.Db.MyMatchStatus.Iter())
            {
                _matchStatus = new HubMatchStatusSnapshot(
                    row.TicketId,
                    row.Status,
                    row.FailureCode,
                    row.MatchId,
                    row.ServerUri,
                    row.DatabaseIdentity,
                    row.MatchBuildId,
                    row.MapId,
                    row.CreatedAt.MicrosecondsSinceUnixEpoch,
                    row.UpdatedAt.MicrosecondsSinceUnixEpoch,
                    row.ExpiresAt.MicrosecondsSinceUnixEpoch,
                    row.ReadyAt?.MicrosecondsSinceUnixEpoch,
                    row.AssignmentExpiresAt?.MicrosecondsSinceUnixEpoch);
                _requestAwaitingConfirmation = false;
                break;
            }
        }

        private void OnRequestMatchResult(HubReducerEventContext context, string clientRequestId)
        {
            if (!_hasIdentity
                || context.Event.CallerIdentity != _identity
                || !string.Equals(clientRequestId, _activeClientRequestId, StringComparison.Ordinal))
            {
                return;
            }

            if (context.Event.Status is Status.Committed)
            {
                // Keep the UI pending until the caller-filtered status view
                // confirms the ticket. If transport drops in this gap, the
                // same stable request ID is safe to resend.
                NotifyChanged();
                return;
            }

            _requestAwaitingConfirmation = false;
            LastError = context.Event.Status switch
            {
                Status.Failed(var failure) => failure,
                Status.OutOfEnergy(var _) => "The Hub was temporarily out of reducer energy.",
                _ => "The Hub did not commit the match request.",
            };
            NotifyChanged();
        }

        private void SetError(string message, bool disconnect)
        {
            LastError = message;
            State = HubConnectionState.Error;
            _nextRetryRealtime = Time.unscaledTime + RetryDelaySeconds;
            if (disconnect)
                DisposeConnection();
            NotifyChanged();
        }

        private void DisposeConnection()
        {
            HubConnection? conn = _conn;
            _conn = null;
            _subscription = null;
            _hasIdentity = false;
            _player = null;
            _matchStatus = null;
            if (conn == null)
                return;

            UnbindRows(conn);
            try
            {
                conn.Disconnect();
            }
            catch (Exception error)
            {
                Debug.LogWarning($"[HubNetworkManager] Disconnect failed: {error.Message}");
            }
        }

        private void Update()
        {
            try
            {
                _conn?.FrameTick();
            }
            catch (Exception error)
            {
                SetError($"Hub connection failed while receiving data: {error.Message}", disconnect: true);
            }

            if (_maintainConnection
                && _conn == null
                && State == HubConnectionState.Error
                && Time.unscaledTime >= _nextRetryRealtime)
            {
                ConnectNow();
            }
        }

        private void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
            _maintainConnection = false;
            DisposeConnection();
        }

        private void NotifyChanged() => Changed?.Invoke();
    }
}
