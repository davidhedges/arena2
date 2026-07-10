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
        private float _nextClockPingRealtime;
        private NetworkEnvironmentEndpoint _activeEndpoint = NetworkEnvironmentConfig.EndpointFor(NetworkEnvironmentKind.Local);

        private SubscriptionHandle? _staticSubscription;
        private SubscriptionHandle? _localSubscription;
        private SubscriptionHandle? _scopedSubscription;

        private GameplayScope _requestedGameplayScope = GameplayScope.None;
        private GameplayScope _appliedGameplayScope = GameplayScope.None;
        private bool _scopeTransitionInFlight;
        private int _scopeTransitionGeneration;

        public bool IsConnected { get; private set; }
        public string? ContractCompatibilityError { get; private set; }

        // The transport is deliberately hidden until shared movement/collision
        // contracts verify. Reducer callers therefore fail closed during the
        // compatibility handshake as well as after a mismatch.
        public DbConnection? Conn => IsConnected ? _conn : null;
        internal NetworkEnvironmentEndpoint ActiveEndpoint => _activeEndpoint;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (Instance != null) return;

            Scene activeScene = SceneManager.GetActiveScene();
            if (!ShouldBootstrapForScene(activeScene.name, activeScene.path))
                return;

            var go = new GameObject("NetworkManager");
            DontDestroyOnLoad(go);
            go.AddComponent<NetworkManager>();
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
            ConnectToResolvedEndpoint();
        }

        internal void ReconnectToSelectedEnvironment()
        {
            useSerializedEndpointOverride = false;
            ConnectToResolvedEndpoint();
        }

        private void ConnectToResolvedEndpoint()
        {
            Connect(NetworkEnvironmentConfig.ResolveEndpoint(
                useSerializedEndpointOverride,
                serverUri,
                moduleName));
        }

        private void Connect(NetworkEnvironmentEndpoint endpoint)
        {
            DisconnectCurrentConnection();
            ContractCompatibilityError = null;

            _activeEndpoint = endpoint;
            Debug.LogWarning(
                $"[NetworkManager] Connecting to {endpoint.ServerUri} " +
                $"module={endpoint.ModuleName} env={endpoint.DisplayName}");

            string? token = NetworkEnvironmentConfig.LoadAuthToken(endpoint);

            _conn = DbConnection.Builder()
                .WithUri(endpoint.ServerUri)
                .WithDatabaseName(endpoint.ModuleName)
                .WithToken(token)
                .OnConnect(OnConnected)
                .OnConnectError(OnConnectError)
                .OnDisconnect(OnDisconnected)
                .Build();
        }

        private void OnConnected(DbConnection conn, Identity identity, string token)
        {
            if (!ReferenceEquals(conn, _conn))
                return;

            ArenaServerClock.Reset();
            Arena.Simulation.ServerTimeDelayBudget.Reset();
            IsConnected = false;
            _localIdentity = identity;
            _hasLocalIdentity = true;
            if (!string.IsNullOrWhiteSpace(token))
                NetworkEnvironmentConfig.SaveAuthToken(_activeEndpoint, token);
            _requestedGameplayScope = GameplayScope.None;
            _appliedGameplayScope = GameplayScope.None;
            _scopeTransitionInFlight = false;
            _scopeTransitionGeneration = 0;

            Debug.Log($"[NetworkManager] Connected. Identity={identity}");

            var registry = EntityRegistry.Instance;
            registry.SetLocalIdentity(identity);
            NetworkCallbackBinder.BindRuntimeCallbacks(conn, registry, MatchStateCache.Instance, LocalCombatState.Instance, identity);

            conn.Reducers.OnPingClock += HandlePingClockResult;
            _nextClockPingRealtime = 0f;

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
            _staticSubscription = conn
                .SubscriptionBuilder()
                .OnApplied(OnStaticSubscriptionApplied)
                .OnError(OnStaticSubscriptionError)
                .Subscribe(GameplaySubscriptionPlanner.BuildStaticQuerySqls());
        }

        private void SubscribeLocalTables(DbConnection conn, Identity localIdentity)
        {
            _localSubscription = conn
                .SubscriptionBuilder()
                .OnApplied(OnLocalSubscriptionApplied)
                .OnError(OnLocalSubscriptionError)
                .Subscribe(GameplaySubscriptionPlanner.BuildLocalQuerySqls(localIdentity));
        }

        private void TryAdvanceGameplayScopeTransition()
        {
            var conn = _conn;
            if (conn == null || !_hasLocalIdentity || _scopeTransitionInFlight)
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
            return conn
                .SubscriptionBuilder()
                .OnApplied(_ => OnScopedSubscriptionApplied(scope, generation))
                .OnError((ctx, error) => OnScopedSubscriptionError(scope, generation, error))
                .Subscribe(GameplaySubscriptionPlanner.BuildScopedQuerySqls(scope));
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
                FailContractCompatibility(result.FailureMessage);
                return;
            }

            if (!_hasLocalIdentity)
                return;

            ContractCompatibilityError = null;
            IsConnected = true;
            SubscribeLocalTables(conn, _localIdentity);
        }

        private void OnLocalSubscriptionApplied(SubscriptionEventContext ctx)
        {
            Debug.Log("[NetworkManager] Local-player subscription applied.");
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

            FailContractCompatibility($"Unable to verify shared-data contracts: {e.Message}");
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

        private void OnConnectError(Exception e)
        {
            Debug.LogError($"[NetworkManager] Connection error: {e.Message}");
        }

        private void OnDisconnected(DbConnection conn, Exception? e)
        {
            if (!ReferenceEquals(conn, _conn))
                return;

            ArenaServerClock.Reset();
            Arena.Simulation.ServerTimeDelayBudget.Reset();
            Arena.Debugging.NetworkCallbackDelay.ResetForNetworkReconnect();
            ResetConnectionState();
            Debug.Log($"[NetworkManager] Disconnected: {e?.Message ?? "clean"}");
        }

        private void Update()
        {
            // Dispatches all pending network messages on the main thread.
            // This is what causes OnInsert/OnUpdate/OnDelete callbacks to fire.
            _conn?.FrameTick();

            // Dev-only receive-delay queue (feel audit F2c); no-op when empty.
            Arena.Debugging.NetworkCallbackDelay.Pump();

            SendClockPingIfDue();

            // Instance management is handled by LobbyController UI.
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
            ResetConnectionState();
            EntityRegistry.Instance?.ClearForNetworkReconnect();
            MatchStateCache.Instance.ResetForNetworkReconnect();
            LocalCombatState.Instance.ResetForNetworkReconnect();

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
            _scopedSubscription = null;
            _requestedGameplayScope = GameplayScope.None;
            _appliedGameplayScope = GameplayScope.None;
            _scopeTransitionInFlight = false;
            _scopeTransitionGeneration = 0;
        }
    }
}
