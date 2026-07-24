#nullable enable

using System;
using Arena.Network;
using SpacetimeDB.ClientApi;
using SpacetimeDB.Types;
using UnityEngine;

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

        public static bool IsConnected => NetworkManager.Instance?.Conn != null;
        public static string Status { get; private set; } = "Waiting for server connection";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (s_instance != null || !ArenaRuntimeSceneGate.ShouldRunArenaRuntimeInActiveScene())
                return;

            GameObject host = new(nameof(DiceRollNetworkBridge));
            DontDestroyOnLoad(host);
            s_instance = host.AddComponent<DiceRollNetworkBridge>();
        }

        public static bool RequestPreview(string requestId, uint dieSides)
        {
            DbConnection? connection = NetworkManager.Instance?.Conn;
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

            DbConnection? connection = NetworkManager.Instance?.Conn;
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
            BindConnection(NetworkManager.Instance?.Conn);
            if (_connection?.Identity is not { } localIdentity)
                return;

            ActiveDiceRoll? active = _connection.Db.ActiveDiceRoll.Owner.Find(localIdentity);
            if (active != null)
                TryPresent(active);
        }

        private void OnDestroy()
        {
            BindConnection(null);
            if (s_instance == this)
                s_instance = null;
        }

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
