#nullable enable

using System;
using Arena.Network;
using SpacetimeDB;
using SpacetimeDB.ClientApi;
using SpacetimeDB.Types;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Arena.Presentation.Dice
{
    /// <summary>
    /// Adapts the local player's authoritative ActiveDiceRoll row to the
    /// network-neutral visual presenter. This class never generates or alters
    /// a result.
    /// </summary>
    [DefaultExecutionOrder(80)]
    [DisallowMultipleComponent]
    public sealed class DiceRollNetworkBridge : MonoBehaviour
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private const string DiceOverlayLabSceneName = "DiceOverlayLab";
#endif

        private readonly struct RollKey : IEquatable<RollKey>
        {
            private readonly string _requestId;
            private readonly uint _dieSides;
            private readonly uint _value;
            private readonly long _createdAtMicros;

            public RollKey(ActiveDiceRoll row)
            {
                _requestId = row.RequestId;
                _dieSides = row.DieSides;
                _value = row.ResolvedValue;
                _createdAtMicros = row.CreatedAt.MicrosecondsSinceUnixEpoch;
            }

            public bool Equals(RollKey other) =>
                string.Equals(_requestId, other._requestId, StringComparison.Ordinal) &&
                _dieSides == other._dieSides &&
                _value == other._value &&
                _createdAtMicros == other._createdAtMicros;
        }

        private static DiceRollNetworkBridge? s_instance;

        private DbConnection? _connection;
        private RollKey? _lastPresented;
        private bool _ownsCurrentPresentation;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private DbConnection? _labConnection;
        private SubscriptionHandle? _labSubscription;
        private float _nextLabConnectAttemptAt;
#endif

        public static bool IsConnected => s_instance?._connection?.Identity != null;
        public static string Status { get; private set; } = "Waiting for server connection";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (s_instance != null || !ShouldBootstrapInActiveScene())
                return;

            GameObject host = new(nameof(DiceRollNetworkBridge));
            DontDestroyOnLoad(host);
            s_instance = host.AddComponent<DiceRollNetworkBridge>();
        }

        public static bool RequestPreview(string requestId, uint dieSides)
        {
            DbConnection? connection = s_instance?._connection;
            if (connection == null)
            {
                Status = "Server Roll unavailable: not connected";
                return false;
            }

            Status = $"Requesting d{dieSides} roll...";
            connection.Reducers.RequestDiceRollPreview(requestId, dieSides);
            return true;
        }

        public static bool DismissActiveRoll()
        {
            DiceOverlayPresenter.Instance?.Dismiss();
            if (s_instance != null)
                s_instance._ownsCurrentPresentation = false;

            DbConnection? connection = s_instance?._connection;
            if (connection == null)
            {
                Status = "Local overlay dismissed; server is not connected";
                return false;
            }

            Status = "Dismissing authoritative roll...";
            connection.Reducers.DismissDiceRoll();
            return true;
        }

        private void Awake()
        {
            if (s_instance != null && s_instance != this)
            {
                Destroy(gameObject);
                return;
            }

            s_instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void Update()
        {
            DbConnection? targetConnection = NetworkManager.Instance?.Conn;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (targetConnection == null && IsDiceOverlayLabActive())
            {
                EnsureLabConnection();
                _labConnection?.FrameTick();
                if (_labConnection?.Identity != null)
                    targetConnection = _labConnection;
            }
            else if (_labConnection != null)
            {
                ShutdownLabConnection();
            }
#endif

            BindConnection(targetConnection);
            if (_connection?.Identity is not { } localIdentity)
                return;

            ActiveDiceRoll? active = _connection.Db.ActiveDiceRoll.Owner.Find(localIdentity);
            if (active != null)
                TryPresent(active);
        }

        private void OnDestroy()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            ShutdownLabConnection();
#endif
            BindConnection(null);
            if (s_instance == this)
                s_instance = null;
        }

        private static bool ShouldBootstrapInActiveScene()
        {
            if (ArenaRuntimeSceneGate.ShouldRunArenaRuntimeInActiveScene())
                return true;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            return IsDiceOverlayLabActive();
#else
            return false;
#endif
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private static bool IsDiceOverlayLabActive()
        {
            Scene scene = SceneManager.GetActiveScene();
            return scene.IsValid() &&
                   scene.isLoaded &&
                   string.Equals(scene.name, DiceOverlayLabSceneName, StringComparison.Ordinal);
        }

        private void EnsureLabConnection()
        {
            if (_labConnection != null || Time.unscaledTime < _nextLabConnectAttemptAt)
                return;

            NetworkEnvironmentEndpoint endpoint =
                NetworkEnvironmentConfig.EndpointFor(NetworkEnvironmentKind.Local);
            Status = $"Connecting dice lab to {endpoint.ServerUri}/{endpoint.ModuleName}...";
            _labConnection = DbConnection.Builder()
                .WithUri(endpoint.ServerUri)
                .WithDatabaseName(endpoint.ModuleName)
                .WithToken(NetworkEnvironmentConfig.LoadAuthToken(endpoint))
                .OnConnect(OnLabConnected)
                .OnConnectError(OnLabConnectError)
                .OnDisconnect(OnLabDisconnected)
                .Build();
        }

        private void OnLabConnected(DbConnection connection, Identity identity, string token)
        {
            if (!ReferenceEquals(connection, _labConnection))
                return;

            NetworkEnvironmentEndpoint endpoint =
                NetworkEnvironmentConfig.EndpointFor(NetworkEnvironmentKind.Local);
            NetworkEnvironmentConfig.SaveAuthToken(endpoint, token);
            BindConnection(connection);
            Status = "Dice lab connected; subscribing to authoritative rolls...";
            _labSubscription = connection
                .SubscriptionBuilder()
                .OnApplied(OnLabSubscriptionApplied)
                .OnError(OnLabSubscriptionError)
                .Subscribe(new[]
                {
                    new QueryBuilder()
                        .From
                        .ActiveDiceRoll()
                        .Where(columns => columns.Owner.Eq(identity))
                        .ToSql()
                });
        }

        private void OnLabConnectError(Exception error)
        {
            _labConnection = null;
            _labSubscription = null;
            _nextLabConnectAttemptAt = Time.unscaledTime + 2f;
            Status = $"Dice lab connection failed: {error.Message}";
            Debug.LogWarning($"[DiceRollNetworkBridge] {Status}");
        }

        private void OnLabDisconnected(DbConnection connection, Exception? error)
        {
            if (!ReferenceEquals(connection, _labConnection))
                return;

            BindConnection(null);
            _labConnection = null;
            _labSubscription = null;
            _nextLabConnectAttemptAt = Time.unscaledTime + 2f;
            Status = error == null
                ? "Dice lab disconnected"
                : $"Dice lab disconnected: {error.Message}";
        }

        private void OnLabSubscriptionApplied(SubscriptionEventContext context)
        {
            if (_labConnection == null || !ReferenceEquals(context.Db, _labConnection.Db))
                return;

            Status = "Dice lab connected to local arena";
        }

        private void OnLabSubscriptionError(ErrorContext context, Exception error)
        {
            if (_labConnection == null || !ReferenceEquals(context.Db, _labConnection.Db))
                return;

            Status = $"Dice lab subscription failed: {error.Message}";
            Debug.LogWarning($"[DiceRollNetworkBridge] {Status}");
        }

        private void ShutdownLabConnection()
        {
            DbConnection? connection = _labConnection;
            _labConnection = null;
            _labSubscription = null;
            if (connection == null)
                return;

            if (ReferenceEquals(_connection, connection))
                BindConnection(null);

            try
            {
                connection.Disconnect();
            }
            catch (Exception error)
            {
                Debug.LogWarning(
                    $"[DiceRollNetworkBridge] Dice lab disconnect failed: {error.Message}");
            }
        }
#endif

        private void BindConnection(DbConnection? connection)
        {
            if (ReferenceEquals(_connection, connection))
                return;

            if (_connection != null)
            {
                _connection.Db.ActiveDiceRoll.OnInsert -= OnInsert;
                _connection.Db.ActiveDiceRoll.OnUpdate -= OnUpdate;
                _connection.Db.ActiveDiceRoll.OnDelete -= OnDelete;
                _connection.OnUnhandledReducerError -= OnUnhandledReducerError;
            }

            if (_ownsCurrentPresentation)
                DiceOverlayPresenter.Instance?.Dismiss();

            _connection = connection;
            _lastPresented = null;
            _ownsCurrentPresentation = false;
            Status = connection == null ? "Waiting for server connection" : "Connected";

            if (_connection == null)
                return;

            _connection.Db.ActiveDiceRoll.OnInsert += OnInsert;
            _connection.Db.ActiveDiceRoll.OnUpdate += OnUpdate;
            _connection.Db.ActiveDiceRoll.OnDelete += OnDelete;
            _connection.OnUnhandledReducerError += OnUnhandledReducerError;
        }

        private void OnInsert(EventContext context, ActiveDiceRoll row)
        {
            _ = context;
            TryPresent(row);
        }

        private void OnUpdate(EventContext context, ActiveDiceRoll oldRow, ActiveDiceRoll row)
        {
            _ = context;
            _ = oldRow;
            TryPresent(row);
        }

        private void OnDelete(EventContext context, ActiveDiceRoll row)
        {
            _ = context;
            if (!IsLocalOwner(row))
                return;

            RollKey removedKey = new(row);
            if (_lastPresented.HasValue && _lastPresented.Value.Equals(removedKey))
                _lastPresented = null;

            DiceOverlayPresenter? presenter = DiceOverlayPresenter.Instance;
            if (_ownsCurrentPresentation &&
                presenter != null &&
                string.Equals(
                    presenter.ActiveRequest.RequestId,
                    row.RequestId,
                    StringComparison.Ordinal))
            {
                presenter.Dismiss();
            }

            _ownsCurrentPresentation = false;
            Status = "Authoritative roll dismissed";
        }

        private void TryPresent(ActiveDiceRoll row)
        {
            if (!IsLocalOwner(row))
                return;

            RollKey key = new(row);
            if (_lastPresented.HasValue && _lastPresented.Value.Equals(key))
                return;

            DiceOverlayPresenter? presenter = DiceOverlayPresenter.Instance ??
                                              FindAnyObjectByType<DiceOverlayPresenter>();
            if (presenter == null)
                return;

            if (presenter.IsActive)
                presenter.Dismiss();

            _lastPresented = key;
            _ownsCurrentPresentation = presenter.Show(
                new ResolvedDiceRoll(
                    row.RequestId,
                    $"d{row.DieSides}",
                    checked((int)row.ResolvedValue)));
            Status = _ownsCurrentPresentation
                ? $"Showing authoritative d{row.DieSides} result {row.ResolvedValue}"
                : $"Authoritative d{row.DieSides} result {row.ResolvedValue} has no presentation asset";
        }

        private bool IsLocalOwner(ActiveDiceRoll row) =>
            _connection?.Identity is { } localIdentity && row.Owner == localIdentity;

        private static void OnUnhandledReducerError(ReducerEventContext context, Exception error)
        {
            string reducerName = context.Event.Reducer.GetType().Name;
            if (!string.Equals(
                    reducerName,
                    nameof(Reducer.RequestDiceRollPreview),
                    StringComparison.Ordinal) &&
                !string.Equals(
                    reducerName,
                    nameof(Reducer.DismissDiceRoll),
                    StringComparison.Ordinal))
            {
                return;
            }

            Status = $"Dice reducer rejected: {error.Message}";
            Debug.LogWarning($"[DiceRollNetworkBridge] {Status}");
        }
    }
}
