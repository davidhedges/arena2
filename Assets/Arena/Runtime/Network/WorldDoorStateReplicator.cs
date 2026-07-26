#nullable enable

using System;
using Arena.Input;
using Arena.Interaction;
using SpacetimeDB;
using SpacetimeDB.Types;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Arena.Network
{
    /// <summary>
    /// Mirrors scope-filtered authoritative door rows into the local prediction
    /// cache and stable-ID scene presentation registry.
    /// </summary>
    public sealed class WorldDoorStateReplicator : MonoBehaviour
    {
        private static WorldDoorStateReplicator? _instance;
        private DbConnection? _connection;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            bool gateOpen = ArenaRuntimeSceneGate.ShouldRunArenaRuntimeInActiveScene();
            Debug.Log(
                $"[WorldInteraction] state replicator bootstrap scene="
                + $"'{SceneManager.GetActiveScene().path}' gate={gateOpen} "
                + $"existing={_instance != null}.");
            if (_instance != null || !gateOpen)
                return;

            var gameObject = new GameObject(nameof(WorldDoorStateReplicator));
            DontDestroyOnLoad(gameObject);
            _instance = gameObject.AddComponent<WorldDoorStateReplicator>();
        }

        private void Update()
        {
            DbConnection? connection = NetworkManager.Instance?.Conn;
            if (_connection != null && !ReferenceEquals(_connection, connection))
                Unsubscribe();
            if (_connection == null && connection != null)
                Subscribe(connection);

            UpdateLocalScope(connection);
        }

        private void OnDestroy()
        {
            Unsubscribe();
            if (_instance == this)
                _instance = null;
        }

        private void Subscribe(DbConnection connection)
        {
            _connection = connection;
            connection.Db.WorldDoorState.OnInsert += OnInsert;
            connection.Db.WorldDoorState.OnUpdate += OnUpdate;
            connection.Db.WorldDoorState.OnDelete += OnDelete;
            int snapshotCount = 0;
            foreach (WorldDoorState row in connection.Db.WorldDoorState.Iter())
            {
                snapshotCount++;
                WorldDoorCollisionRuntime.Upsert(row);
                DoorRuntimeRegistry.Apply(
                    row.DoorDefinitionId,
                    row.IsOpen,
                    row.Revision,
                    animate: false);
            }
            Debug.Log(
                $"[WorldInteraction] state replicator subscribed; "
                + $"snapshotRows={snapshotCount}.",
                this);
        }

        private static void OnInsert(EventContext context, WorldDoorState row)
        {
            bool animate =
                ShouldAnimateReplicatedInsert(
                    context.Event
                        is SpacetimeDB.Event<SpacetimeDB.Types.Reducer>.SubscribeApplied,
                    row);
            ArenaServerClock.RecordObservedServerTimestampMicros(
                row.UpdatedAt.MicrosecondsSinceUnixEpoch);
            WorldDoorCollisionRuntime.Upsert(row);
            DoorRuntimeRegistry.Apply(
                row.DoorDefinitionId,
                row.IsOpen,
                row.Revision,
                animate);
            Debug.Log(
                $"[WorldInteraction] state insert id='{row.DoorDefinitionId}' "
                + $"open={row.IsOpen} revision={row.Revision} animate={animate}.");
        }

        private static void OnUpdate(
            EventContext context,
            WorldDoorState oldRow,
            WorldDoorState row)
        {
            _ = context;
            if (!string.Equals(
                    oldRow.DoorDefinitionId,
                    row.DoorDefinitionId,
                    StringComparison.Ordinal))
            {
                WorldDoorCollisionRuntime.Remove(oldRow);
            }
            WorldDoorCollisionRuntime.Upsert(row);
            DoorRuntimeRegistry.Apply(
                row.DoorDefinitionId,
                row.IsOpen,
                row.Revision,
                animate: true);
            Debug.Log(
                $"[WorldInteraction] state update id='{row.DoorDefinitionId}' "
                + $"open={oldRow.IsOpen}->{row.IsOpen} "
                + $"revision={oldRow.Revision}->{row.Revision}.");
        }

        private static void OnDelete(EventContext context, WorldDoorState row)
        {
            _ = context;
            WorldDoorCollisionRuntime.Remove(row);
            DoorRuntimeRegistry.ResetToAuthoredDefault(row.DoorDefinitionId);
        }

        public static bool ShouldAnimateReplicatedInsert(
            bool subscriptionSnapshot,
            WorldDoorState row,
            long? serverNowMs = null)
        {
            if (subscriptionSnapshot)
                return false;

            long updatedMs = row.UpdatedAt.MicrosecondsSinceUnixEpoch / 1000L;
            if (updatedMs <= 0L)
                return false;

            long nowMs = serverNowMs
                ?? (ArenaServerClock.HasEstimate
                    ? ArenaServerClock.ServerNowMs
                    : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            long ageMs = nowMs - updatedMs;
            return ageMs >= -250L && ageMs <= 1000L;
        }

        private static void UpdateLocalScope(DbConnection? connection)
        {
            Identity? identity = connection?.Identity;
            if (connection == null || !identity.HasValue)
            {
                WorldDoorCollisionRuntime.SetScope(null, null);
                return;
            }

            PlayerWorld? world = connection.Db.PlayerWorld.Identity.Find(identity.Value);
            WorldDoorCollisionRuntime.SetScope(
                world?.WorldKind,
                world?.OpenWorldSceneName);
        }

        private void Unsubscribe()
        {
            if (_connection != null)
            {
                _connection.Db.WorldDoorState.OnInsert -= OnInsert;
                _connection.Db.WorldDoorState.OnUpdate -= OnUpdate;
                _connection.Db.WorldDoorState.OnDelete -= OnDelete;
            }
            _connection = null;
            WorldDoorCollisionRuntime.Clear();
            DoorRuntimeRegistry.ResetToAuthoredDefaults();
        }
    }
}
