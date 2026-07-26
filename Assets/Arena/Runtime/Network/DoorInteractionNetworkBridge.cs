#nullable enable

using Arena.Input;
using Arena.Interaction;
using Arena.Simulation;
using SpacetimeDB;
using SpacetimeDB.Types;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Arena.Network
{
    /// <summary>
    /// Thin transport adapter between concrete door requests and generated
    /// reducers. It predicts neither target state nor completion.
    /// </summary>
    public sealed class DoorInteractionNetworkBridge :
        MonoBehaviour,
        IDoorInteractionRequestSink
    {
        private static DoorInteractionNetworkBridge? _instance;
        private DbConnection? _connection;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            bool gateOpen = ArenaRuntimeSceneGate.ShouldRunArenaRuntimeInActiveScene();
            Debug.Log(
                $"[WorldInteraction] network bridge bootstrap scene="
                + $"'{SceneManager.GetActiveScene().path}' gate={gateOpen} "
                + $"existing={_instance != null}.");
            if (_instance != null
                || !gateOpen)
            {
                return;
            }

            var gameObject = new GameObject(nameof(DoorInteractionNetworkBridge));
            DontDestroyOnLoad(gameObject);
            _instance = gameObject.AddComponent<DoorInteractionNetworkBridge>();
        }

        private void OnEnable()
        {
            DoorInteractionRequests.Sink = this;
            Debug.Log("[WorldInteraction] network bridge enabled; request sink assigned.", this);
        }

        private void Update()
        {
            DbConnection? connection = NetworkManager.Instance?.Conn;
            if (_connection != null && !ReferenceEquals(_connection, connection))
                Detach();
            if (_connection == null && connection != null)
                Attach(connection);

            if (!ReferenceEquals(DoorInteractionRequests.Sink, this))
                DoorInteractionRequests.Sink = this;
        }

        private void OnDisable()
        {
            if (ReferenceEquals(DoorInteractionRequests.Sink, this))
                DoorInteractionRequests.Sink = null;
            Detach();
        }

        private void OnDestroy()
        {
            if (_instance == this)
                _instance = null;
        }

        public bool RequestDoorState(
            DoorInteractable door,
            bool desiredOpen,
            ulong observedRevision)
        {
            DbConnection? connection = NetworkManager.Instance?.Conn;
            if (connection == null || !connection.Identity.HasValue)
            {
                Debug.LogWarning(
                    $"[WorldInteraction] network request rejected id="
                    + $"'{door.StableInteractionId}': no connected identity.",
                    this);
                LocalInteractionState.ReportDenial("Not connected");
                return false;
            }

            if (!ReferenceEquals(_connection, connection))
            {
                Detach();
                Attach(connection);
            }

            connection.Reducers.BeginWorldDoorAction(
                door.StableInteractionId,
                desiredOpen,
                observedRevision);
            Debug.Log(
                $"[WorldInteraction] reducer sent BeginWorldDoorAction "
                + $"id='{door.StableInteractionId}' desiredOpen={desiredOpen} "
                + $"observedRevision={observedRevision}.",
                this);
            return true;
        }

        public static bool TryCancelActiveInteraction()
        {
            DbConnection? connection = NetworkManager.Instance?.Conn;
            ActiveWorldInteraction? active = LocalInteractionState.Instance.Active;
            if (connection == null || active == null)
                return false;

            connection.Reducers.CancelWorldInteraction(active.ActionInstanceId);
            return true;
        }

        private void Attach(DbConnection connection)
        {
            _connection = connection;
            connection.Reducers.OnBeginWorldDoorAction += OnBeginWorldDoorAction;
            connection.Reducers.OnCancelWorldInteraction += OnCancelWorldInteraction;
            Debug.Log(
                $"[WorldInteraction] network bridge attached "
                + $"identity={connection.Identity.HasValue}.",
                this);
        }

        private void Detach()
        {
            if (_connection != null)
            {
                _connection.Reducers.OnBeginWorldDoorAction -= OnBeginWorldDoorAction;
                _connection.Reducers.OnCancelWorldInteraction -= OnCancelWorldInteraction;
            }
            _connection = null;
        }

        private void OnBeginWorldDoorAction(
            ReducerEventContext context,
            string doorDefinitionId,
            bool desiredOpen,
            ulong observedRevision)
        {
            Debug.Log(
                $"[WorldInteraction] reducer result BeginWorldDoorAction "
                + $"id='{doorDefinitionId}' desiredOpen={desiredOpen} "
                + $"observedRevision={observedRevision} status={context.Event.Status}.",
                this);
            ReportLocalFailure(context);
        }

        private void OnCancelWorldInteraction(
            ReducerEventContext context,
            string actionInstanceId)
        {
            _ = actionInstanceId;
            ReportLocalFailure(context);
        }

        private void ReportLocalFailure(ReducerEventContext context)
        {
            if (_connection == null
                || !_connection.Identity.HasValue
                || context.Event.CallerIdentity != _connection.Identity.Value
                || context.Event.Status is Status.Committed)
            {
                return;
            }

            string reason = context.Event.Status switch
            {
                Status.Failed(var failure) => failure,
                Status.OutOfEnergy(var _) => "Interaction service unavailable",
                _ => "Interaction failed",
            };
            LocalInteractionState.ReportDenial(reason);
        }
    }
}
