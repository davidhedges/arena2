#nullable enable
using System;
using UnityEngine;
using SpacetimeDB;
using SpacetimeDB.Types;
using Arena.Entity;
using Arena.Match;
using Arena.Simulation;
using Arena.World;
using UnityEngine.SceneManagement;

namespace Arena.Network
{
    /// <summary>
    /// Manages the SpacetimeDB connection lifecycle and the runtime subscription boundary.
    ///
    /// Responsibilities:
    ///   1. Build and maintain `DbConnection`.
    ///   2. Own static, local-player, and visibility-scoped subscriptions.
    ///   3. Route row callbacks to runtime caches and entity systems.
    ///   4. Pump `DbConnection.FrameTick()` each Unity frame.
    ///
    /// Visibility is now enforced by the runtime subscription scope instead of by
    /// iterating full public tables and filtering rows locally afterward.
    ///
    /// Important: this is an architectural/runtime boundary only. Public tables are
    /// still readable by custom clients until the project adopts a server-enforced
    /// visibility layer such as SpacetimeDB RLS in a future pass.
    /// </summary>
    public class NetworkManager : MonoBehaviour
    {
        internal enum GameplayScopeKind
        {
            None,
            OpenWorld,
            Instance,
        }

        internal readonly struct GameplayScope : IEquatable<GameplayScope>
        {
            public static GameplayScope None => new(GameplayScopeKind.None, null, null);
            public static GameplayScope OpenWorld(string sceneName) => new(
                GameplayScopeKind.OpenWorld,
                null,
                OpenWorldTravelCatalog.IsRegisteredOpenWorldScene(sceneName)
                    ? sceneName
                    : OpenWorldTravelCatalog.DefaultSceneName);

            public GameplayScopeKind Kind { get; }
            public ulong? InstanceId { get; }
            public string? OpenWorldSceneName { get; }

            private GameplayScope(GameplayScopeKind kind, ulong? instanceId, string? openWorldSceneName)
            {
                Kind = kind;
                InstanceId = instanceId;
                OpenWorldSceneName = openWorldSceneName;
            }

            public static GameplayScope Instance(ulong instanceId)
                => new(GameplayScopeKind.Instance, instanceId, null);

            public static GameplayScope FromPlayerWorld(PlayerWorld row, string? openWorldSceneName)
            {
                if (string.Equals(row.WorldKind, "INSTANCE", StringComparison.OrdinalIgnoreCase)
                    && row.InstanceId.HasValue)
                {
                    return Instance(row.InstanceId.Value);
                }

                return OpenWorld(row.OpenWorldSceneName ?? openWorldSceneName ?? OpenWorldTravelCatalog.DefaultSceneName);
            }

            public bool Equals(GameplayScope other)
                => Kind == other.Kind
                   && InstanceId == other.InstanceId
                   && string.Equals(OpenWorldSceneName, other.OpenWorldSceneName, StringComparison.Ordinal);

            public override bool Equals(object? obj)
                => obj is GameplayScope other && Equals(other);

            public override int GetHashCode()
                => HashCode.Combine((int)Kind, InstanceId, OpenWorldSceneName);

            public override string ToString()
            {
                return Kind switch
                {
                    GameplayScopeKind.None => "none",
                    GameplayScopeKind.OpenWorld => $"open-world {OpenWorldSceneName ?? OpenWorldTravelCatalog.DefaultSceneName}",
                    GameplayScopeKind.Instance => $"instance {InstanceId.GetValueOrDefault()}",
                    _ => "unknown",
                };
            }
        }

        public static NetworkManager Instance { get; private set; } = null!;

        [Header("SpacetimeDB")]
        [Tooltip("When disabled, the runtime environment selector decides the endpoint.")]
        [SerializeField] private bool useSerializedEndpointOverride;
        [Tooltip("Only used when Use Serialized Endpoint Override is enabled.")]
        [SerializeField] private string serverUri = NetworkEnvironmentConfig.LocalServerUri;
        [Tooltip("Only used when Use Serialized Endpoint Override is enabled.")]
        [SerializeField] private string moduleName = NetworkEnvironmentConfig.DefaultModuleName;

        /// <summary>
        /// Cadence of the `ping_clock` RTT probe (feel audit F2b). Deliberately
        /// slow: each ping is one reducer call and one caller-only transaction
        /// update, and the clock estimator only needs fresh samples on the
        /// order of seconds. Gameplay must never key off raw RTT.
        /// </summary>
        private const float ClockPingIntervalSeconds = 2f;

        private DbConnection? _conn;
        private Identity _localIdentity;
        private bool _hasLocalIdentity;
        private bool _isProvisionedMatchConnection;
        // A disposable open world is provisioned exactly like a match but runs
        // the full server module, so it keeps the ordinary subscription plan
        // instead of the trimmed PvP one.
        private bool _provisionedConnectionHasFullSchema;
        private Identity _expectedProvisionedIdentity;
        private bool _hasExpectedProvisionedIdentity;
        private string _provisionedMatchId = string.Empty;
        private string _provisionedMatchBuildId = string.Empty;
        private bool _provisionedFailureReported;
        private int _connectionGeneration;
        private float _nextClockPingRealtime;
        private NetworkEnvironmentEndpoint _activeEndpoint = NetworkEnvironmentConfig.EndpointFor(NetworkEnvironmentKind.Local);

        private SubscriptionHandle? _staticSubscription;
        private SubscriptionHandle? _localSubscription;
        private SubscriptionHandle? _pvpInitialSubscription;
        private SubscriptionHandle? _scopedSubscription;

        private GameplayScope _requestedGameplayScope = GameplayScope.None;
        private GameplayScope _appliedGameplayScope = GameplayScope.None;
        private bool _scopeTransitionInFlight;
        private int _scopeTransitionGeneration;

        public bool IsConnected { get; private set; }
        public string? ContractCompatibilityError { get; private set; }
        internal bool IsProvisionedMatchConnection => _isProvisionedMatchConnection;
        internal Identity? LocalIdentity => _hasLocalIdentity ? _localIdentity : null;
        internal string ProvisionedMatchId => _provisionedMatchId;
        internal string ProvisionedMatchBuildId => _provisionedMatchBuildId;

        internal event Action<Identity>? ProvisionedMatchReady;
        internal event Action<string>? ProvisionedMatchFailed;
        internal event Action<string>? ProvisionedMatchDisconnected;

        // The transport is deliberately hidden until shared movement/collision
        // contracts verify. Reducer callers therefore fail closed during the
        // compatibility handshake as well as after a mismatch.
        public DbConnection? Conn => IsConnected ? _conn : null;
        internal NetworkEnvironmentEndpoint ActiveEndpoint => _activeEndpoint;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            Scene activeScene = SceneManager.GetActiveScene();
            if (!ShouldBootstrapForScene(activeScene.name, activeScene.path))
                return;

            EnsureInstance();
        }

        internal static NetworkManager EnsureInstance()
        {
            if (Instance != null)
                return Instance;

            var go = new GameObject("NetworkManager");
            DontDestroyOnLoad(go);
            return go.AddComponent<NetworkManager>();
        }

        internal static bool ShouldBootstrapForScene(string sceneName, string scenePath)
        {
            return ArenaRuntimeSceneGate.IsArenaRuntimeScene(sceneName, scenePath);
        }

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void Start()
        {
            // Hub owns its own small control-plane connection. The gameplay
            // connection is opened only after an assignment is ready.
            if (string.Equals(SceneManager.GetActiveScene().name, "Hub", StringComparison.Ordinal))
                return;

            ConnectToResolvedEndpoint();
        }

        internal void ReconnectToSelectedEnvironment()
        {
            useSerializedEndpointOverride = false;
            if (string.Equals(SceneManager.GetActiveScene().name, "Hub", StringComparison.Ordinal))
            {
                DisconnectCurrentConnection();
                HubNetworkManager.EnsureInstance().ReconnectToSelectedEnvironment();
                return;
            }

            ConnectToResolvedEndpoint();
        }

        private void ConnectToResolvedEndpoint()
        {
            Connect(NetworkEnvironmentConfig.ResolveEndpoint(
                useSerializedEndpointOverride,
                serverUri,
                moduleName),
                isProvisionedMatch: false,
                expectedIdentity: null,
                matchId: string.Empty,
                matchBuildId: string.Empty);
        }

        internal void ConnectToProvisionedMatch(
            string serverUri,
            string databaseIdentity,
            Identity expectedIdentity,
            string matchId,
            string matchBuildId,
            bool hasFullSchema = false)
        {
            Connect(
                new NetworkEnvironmentEndpoint(
                    NetworkEnvironmentKind.Custom,
                    hasFullSchema ? "Provisioned World" : "Provisioned Match",
                    serverUri,
                    databaseIdentity),
                isProvisionedMatch: true,
                expectedIdentity,
                matchId,
                matchBuildId,
                hasFullSchema);
        }

        internal void DisconnectProvisionedMatch()
        {
            if (_isProvisionedMatchConnection)
                DisconnectCurrentConnection();
        }

        private void Connect(
            NetworkEnvironmentEndpoint endpoint,
            bool isProvisionedMatch,
            Identity? expectedIdentity,
            string matchId,
            string matchBuildId,
            bool provisionedHasFullSchema = false)
        {
            DisconnectCurrentConnection();
            ContractCompatibilityError = null;

            _activeEndpoint = endpoint;
            _isProvisionedMatchConnection = isProvisionedMatch;
            _provisionedConnectionHasFullSchema = isProvisionedMatch && provisionedHasFullSchema;
            _hasExpectedProvisionedIdentity = expectedIdentity.HasValue;
            _expectedProvisionedIdentity = expectedIdentity.GetValueOrDefault();
            _provisionedMatchId = matchId;
            _provisionedMatchBuildId = matchBuildId;
            _provisionedFailureReported = false;
            if (isProvisionedMatch)
                MatchStartupTiming.Record("match_connect_started");
            Debug.LogWarning(
                $"[NetworkManager] Connecting to {endpoint.ServerUri} " +
                $"module={endpoint.ModuleName} env={endpoint.DisplayName}");

            string? token = NetworkEnvironmentConfig.LoadAuthToken(endpoint);

            int generation = ++_connectionGeneration;
            _conn = DbConnection.Builder()
                .WithUri(endpoint.ServerUri)
                .WithDatabaseName(endpoint.ModuleName)
                .WithToken(token)
                .OnConnect((conn, identity, issuedToken) =>
                    OnConnected(generation, conn, identity, issuedToken))
                .OnConnectError(error => OnConnectError(generation, error))
                .OnDisconnect((conn, error) => OnDisconnected(generation, conn, error))
                .Build();
        }

        private void OnConnected(
            int generation,
            DbConnection conn,
            Identity identity,
            string token)
        {
            if (generation != _connectionGeneration || !ReferenceEquals(conn, _conn))
                return;

            ArenaServerClock.Reset();
            Arena.Simulation.ServerTimeDelayBudget.Reset();
            IsConnected = false;
            _localIdentity = identity;
            _hasLocalIdentity = true;
            if (_isProvisionedMatchConnection
                && (!_hasExpectedProvisionedIdentity || identity != _expectedProvisionedIdentity))
            {
                FailProvisionedMatch(
                    "The match database authenticated a different player identity. "
                    + "The Hub identity was preserved and the handoff was cancelled.");
                return;
            }
            if (_isProvisionedMatchConnection)
                MatchStartupTiming.Record("match_transport_connected");
            if (!string.IsNullOrWhiteSpace(token))
                NetworkEnvironmentConfig.SaveAuthToken(_activeEndpoint, token);
            _requestedGameplayScope = GameplayScope.None;
            _appliedGameplayScope = GameplayScope.None;
            _scopeTransitionInFlight = false;
            _scopeTransitionGeneration = 0;

            Debug.Log($"[NetworkManager] Connected. Identity={identity}");

            var registry = EntityRegistry.Instance;
            registry.SetLocalIdentity(identity);
            NetworkCallbackBinder.BindRuntimeCallbacks(
                conn,
                registry,
                MatchStateCache.Instance,
                LocalCombatState.Instance,
                LocalInteractionState.Instance,
                identity);

            conn.Reducers.OnPingClock += HandlePingClockResult;
            _nextClockPingRealtime = 0f;

            if (_isProvisionedMatchConnection && !_provisionedConnectionHasFullSchema)
                SubscribePvpMatchInitialTables(conn, identity);
            else
                SubscribeStaticTables(conn);
        }

        private void SendClockPingIfDue()
        {
            var conn = _conn;
            if (conn == null || !IsConnected)
                return;

            float now = Time.realtimeSinceStartup;
            if (now < _nextClockPingRealtime)
                return;

            _nextClockPingRealtime = now + ClockPingIntervalSeconds;
            conn.Reducers.PingClock(
                (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                StandingViewReportMs(conn));
        }

        /// <summary>
        /// S9 standing view-delay report (E1): while an auto-attack target is
        /// armed, the server-time this client is rendering that target at —
        /// the same value a press on it would claim. 0 = no report, and the
        /// server drops any standing row. Server-initiated swings are the only
        /// consumer, and they exist only while a target is armed.
        /// </summary>
        private ulong StandingViewReportMs(DbConnection conn)
        {
            if (!_hasLocalIdentity)
                return 0UL;

            AutoAttackState? armed = conn.Db.AutoAttackState.Owner.Find(_localIdentity);
            if (armed == null)
                return 0UL;

            var registry = EntityRegistry.Instance;
            if (registry == null
                || !registry.TryGetCombatTarget(armed.Target, out ICombatTargetEntity target)
                || target.IsDestroyed)
            {
                return 0UL;
            }

            return Arena.Combat.AttackerViewTime.ViewServerTimeMsFor(target);
        }

        private void HandlePingClockResult(ReducerEventContext ctx, ulong clientSendMs, ulong viewServerTimeMs)
        {
            // Only our own probe carries a send time from our clock.
            if (!_hasLocalIdentity || ctx.Event.CallerIdentity != _localIdentity)
                return;

            ArenaServerClock.RecordReducerSampleMicros(
                (long)clientSendMs,
                ctx.Event.Timestamp.MicrosecondsSinceUnixEpoch,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }

        /// <summary>
        /// Adjust the runtime gameplay scope. Rows that no longer belong to the selected scope
        /// are removed from the local cache via subscription teardown.
        ///
        /// This improves runtime ownership and bandwidth. It is not yet a server-enforced read
        /// security boundary against custom clients.
        /// </summary>
        internal void SetGameplayScope(GameplayScope scope)
        {
            _requestedGameplayScope = scope;
            TryAdvanceGameplayScopeTransition();
        }

        private void SubscribeStaticTables(DbConnection conn)
        {
            string[] queries = GameplaySubscriptionPlanner.BuildStaticQuerySqls();
            _staticSubscription = conn
                .SubscriptionBuilder()
                .OnApplied(OnStaticSubscriptionApplied)
                .OnError(OnStaticSubscriptionError)
                .Subscribe(queries);
        }

        private void SubscribeLocalTables(DbConnection conn, Identity localIdentity)
        {
            string[] queries = GameplaySubscriptionPlanner.BuildLocalQuerySqls(localIdentity);
            _localSubscription = conn
                .SubscriptionBuilder()
                .OnApplied(OnLocalSubscriptionApplied)
                .OnError(OnLocalSubscriptionError)
                .Subscribe(queries);
        }

        private void SubscribePvpMatchInitialTables(DbConnection conn, Identity localIdentity)
        {
            MatchStartupTiming.Record("initial_subscription_started");
            string[] queries = GameplaySubscriptionPlanner.BuildPvpMatchInitialQuerySqls(localIdentity);
            _pvpInitialSubscription = conn
                .SubscriptionBuilder()
                .OnApplied(OnPvpMatchInitialSubscriptionApplied)
                .OnError(OnPvpMatchInitialSubscriptionError)
                .Subscribe(queries);
        }

        private void TryAdvanceGameplayScopeTransition()
        {
            var conn = _conn;
            if (conn == null || !_hasLocalIdentity || _scopeTransitionInFlight)
                return;
            // PlayerWorld snapshot callbacks arrive before the initial
            // subscription's OnApplied callback. A provisioned match defers
            // its separate visibility subscription until contracts and local
            // entry state have passed the one-round-trip readiness gate.
            if (_isProvisionedMatchConnection && !IsConnected)
                return;

            if (_scopedSubscription != null && _scopedSubscription.IsEnded)
            {
                _scopedSubscription = null;
                _appliedGameplayScope = GameplayScope.None;
            }

            if (_appliedGameplayScope.Equals(_requestedGameplayScope))
                return;

            if (_scopedSubscription != null)
            {
                if (!_scopedSubscription.IsActive)
                    return;

                _scopeTransitionInFlight = true;
                int generation = ++_scopeTransitionGeneration;
                GameplayScope removedScope = _appliedGameplayScope;
                _scopedSubscription.UnsubscribeThen(_ => OnScopedSubscriptionEnded(removedScope, generation));
                return;
            }

            if (_requestedGameplayScope.Kind == GameplayScopeKind.None)
            {
                _appliedGameplayScope = GameplayScope.None;
                Debug.Log("[NetworkManager] Gameplay scope cleared.");
                return;
            }

            int subscriptionGeneration = ++_scopeTransitionGeneration;
            GameplayScope scopeToApply = _requestedGameplayScope;
            _scopeTransitionInFlight = true;
            _scopedSubscription = SubscribeScopedTables(conn, scopeToApply, subscriptionGeneration);
        }

        private SubscriptionHandle SubscribeScopedTables(
            DbConnection conn,
            GameplayScope scope,
            int generation)
        {
            string[] queries = _isProvisionedMatchConnection && !_provisionedConnectionHasFullSchema
                ? GameplaySubscriptionPlanner.BuildPvpMatchScopedQuerySqls(scope)
                : GameplaySubscriptionPlanner.BuildScopedQuerySqls(scope);
            return conn
                .SubscriptionBuilder()
                .OnApplied(_ => OnScopedSubscriptionApplied(scope, generation))
                .OnError((ctx, error) => OnScopedSubscriptionError(scope, generation, error))
                .Subscribe(queries);
        }

        private void OnStaticSubscriptionApplied(SubscriptionEventContext ctx)
        {
            var conn = _conn;
            if (conn == null || !ReferenceEquals(ctx.Db, conn.Db))
                return;

            Debug.Log("[NetworkManager] Static subscription applied.");
            ContractVersionGuard.ValidationResult result = ContractVersionGuard.Validate(ctx.Db);
            if (!result.IsCompatible)
            {
                if (_isProvisionedMatchConnection)
                    FailProvisionedMatch(result.FailureMessage);
                else
                    FailContractCompatibility(result.FailureMessage);
                return;
            }

            if (!_hasLocalIdentity)
                return;

            ContractCompatibilityError = null;
            SubscribeLocalTables(conn, _localIdentity);
        }

        private void OnPvpMatchInitialSubscriptionApplied(SubscriptionEventContext ctx)
        {
            var conn = _conn;
            if (conn == null
                || !ReferenceEquals(ctx.Db, conn.Db)
                || !_isProvisionedMatchConnection
                || !_hasLocalIdentity)
            {
                return;
            }

            MatchStartupTiming.Record("initial_subscription_applied");
            ContractVersionGuard.ValidationResult result = ContractVersionGuard.ValidatePvpMatch(ctx.Db);
            MatchStartupTiming.Record(
                "pvp_contracts_validated",
                $"verified={result.Verified} missing={result.Missing} mismatches={result.Mismatches}");
            if (!result.IsCompatible)
            {
                FailProvisionedMatch(result.FailureMessage);
                return;
            }

            ContractCompatibilityError = null;
            IsConnected = true;
            Debug.Log("[NetworkManager] PvP initial subscription applied and contracts verified.");
            TryAdvanceGameplayScopeTransition();
            MatchStartupTiming.Record("initial_state_accepted");
            ProvisionedMatchReady?.Invoke(_localIdentity);
        }

        private void OnLocalSubscriptionApplied(SubscriptionEventContext ctx)
        {
            var conn = _conn;
            if (conn == null || !ReferenceEquals(ctx.Db, conn.Db) || !_hasLocalIdentity)
                return;

            IsConnected = true;
            Debug.Log("[NetworkManager] Local-player subscription applied.");
            if (!_isProvisionedMatchConnection)
                return;

            // A provisioned open world reaches readiness through the ordinary
            // static/local plan, so this is where its handoff completes.
            TryAdvanceGameplayScopeTransition();
            MatchStartupTiming.Record("initial_state_accepted");
            ProvisionedMatchReady?.Invoke(_localIdentity);
        }

        private void OnScopedSubscriptionApplied(GameplayScope scope, int generation)
        {
            if (generation != _scopeTransitionGeneration)
                return;

            _scopeTransitionInFlight = false;
            _appliedGameplayScope = scope;

            Debug.Log(
                $"[NetworkManager] Gameplay scope applied at runtime: {scope}. " +
                "This is a runtime visibility boundary, not a hard security boundary.");

            if (!_requestedGameplayScope.Equals(_appliedGameplayScope))
                TryAdvanceGameplayScopeTransition();
        }

        private void OnScopedSubscriptionEnded(GameplayScope removedScope, int generation)
        {
            if (generation != _scopeTransitionGeneration)
                return;

            _scopeTransitionInFlight = false;
            _scopedSubscription = null;
            _appliedGameplayScope = GameplayScope.None;

            Debug.Log($"[NetworkManager] Gameplay scope removed: {removedScope}");
            TryAdvanceGameplayScopeTransition();
        }

        private void OnStaticSubscriptionError(ErrorContext ctx, Exception e)
        {
            if (_conn == null || !ReferenceEquals(ctx.Db, _conn.Db))
                return;

            string message = $"Unable to verify shared-data contracts: {e.Message}";
            if (_isProvisionedMatchConnection)
                FailProvisionedMatch(message);
            else
                FailContractCompatibility(message);
        }

        private void FailContractCompatibility(string message)
        {
            IsConnected = false;
            ContractCompatibilityError = message;
            Debug.LogError($"[NetworkManager] Incompatible client/server contract. {message}");

            try
            {
                _conn?.Disconnect();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[NetworkManager] Disconnect after contract failure failed: {e.Message}");
            }
        }

        private void OnLocalSubscriptionError(ErrorContext ctx, Exception e)
        {
            Debug.LogError($"[NetworkManager] Local subscription error: {e.Message}");
        }

        private void OnPvpMatchInitialSubscriptionError(ErrorContext ctx, Exception e)
        {
            if (_conn == null || !ReferenceEquals(ctx.Db, _conn.Db))
                return;

            Debug.LogError($"[NetworkManager] PvP initial subscription error: {e.Message}");
            FailProvisionedMatch($"The match's initial subscription failed: {e.Message}");
        }

        private void OnScopedSubscriptionError(GameplayScope scope, int generation, Exception e)
        {
            if (generation != _scopeTransitionGeneration)
                return;

            _scopeTransitionInFlight = false;
            if (_scopedSubscription != null && _scopedSubscription.IsEnded)
            {
                _scopedSubscription = null;
                _appliedGameplayScope = GameplayScope.None;
            }

            Debug.LogError($"[NetworkManager] Scoped subscription error for {scope}: {e.Message}");
        }

        private void OnConnectError(int generation, Exception e)
        {
            if (generation != _connectionGeneration || _conn == null)
                return;

            Debug.LogError($"[NetworkManager] Connection error: {e.Message}");
            if (_isProvisionedMatchConnection)
                FailProvisionedMatch($"Unable to connect to the assigned match: {e.Message}");
        }

        private void OnDisconnected(int generation, DbConnection conn, Exception? e)
        {
            if (generation != _connectionGeneration || !ReferenceEquals(conn, _conn))
                return;

            bool wasProvisioned = _isProvisionedMatchConnection;
            bool failureAlreadyReported = _provisionedFailureReported;
            string disconnectReason = e?.Message ?? "the match transport closed";
            _conn = null;
            ArenaServerClock.Reset();
            Arena.Simulation.ServerTimeDelayBudget.Reset();
            Arena.Debugging.NetworkCallbackDelay.ResetForNetworkReconnect();
            EntityRegistry.Instance?.ClearForNetworkReconnect();
            MatchStateCache.Instance.ResetForNetworkReconnect();
            LocalCombatState.Instance.ResetForNetworkReconnect();
            LocalInteractionState.Instance.ResetForNetworkReconnect();
            ResetConnectionState();
            Debug.Log($"[NetworkManager] Disconnected: {e?.Message ?? "clean"}");
            if (wasProvisioned && !failureAlreadyReported)
                ProvisionedMatchDisconnected?.Invoke(disconnectReason);
        }

        private void FailProvisionedMatch(string message)
        {
            if (!_isProvisionedMatchConnection || _provisionedFailureReported)
                return;

            _provisionedFailureReported = true;
            IsConnected = false;
            ContractCompatibilityError = message;
            Debug.LogError($"[NetworkManager] Provisioned match handoff failed. {message}");
            ProvisionedMatchFailed?.Invoke(message);
            try
            {
                _conn?.Disconnect();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[NetworkManager] Match disconnect after handoff failure failed: {e.Message}");
            }
        }

        private void Update()
        {
            // Dispatches all pending network messages on the main thread.
            // This is what causes OnInsert/OnUpdate/OnDelete callbacks to fire.
            _conn?.FrameTick();

            // Dev-only receive-delay queue (feel audit F2c); no-op when empty.
            Arena.Debugging.NetworkCallbackDelay.Pump();

            SendClockPingIfDue();

            // Instance creation is initiated by the Hub; authoritative
            // PlayerWorld callbacks drive scene and subscription changes.
        }

        private void OnDestroy()
        {
            if (_conn != null)
                _conn.Disconnect();
        }

        private void DisconnectCurrentConnection()
        {
            var conn = _conn;
            _conn = null;
            _connectionGeneration++;
            ResetConnectionState();
            EntityRegistry.Instance?.ClearForNetworkReconnect();
            MatchStateCache.Instance.ResetForNetworkReconnect();
            LocalCombatState.Instance.ResetForNetworkReconnect();
            LocalInteractionState.Instance.ResetForNetworkReconnect();

            if (conn == null)
                return;

            try
            {
                conn.Disconnect();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[NetworkManager] Disconnect before reconnect failed: {e.Message}");
            }
        }

        private void ResetConnectionState()
        {
            IsConnected = false;
            _hasLocalIdentity = false;
            _staticSubscription = null;
            _localSubscription = null;
            _pvpInitialSubscription = null;
            _scopedSubscription = null;
            _requestedGameplayScope = GameplayScope.None;
            _appliedGameplayScope = GameplayScope.None;
            _scopeTransitionInFlight = false;
            _scopeTransitionGeneration = 0;
            _isProvisionedMatchConnection = false;
            _provisionedConnectionHasFullSchema = false;
            _hasExpectedProvisionedIdentity = false;
            _provisionedMatchId = string.Empty;
            _provisionedMatchBuildId = string.Empty;
            _provisionedFailureReported = false;
        }
    }
}
