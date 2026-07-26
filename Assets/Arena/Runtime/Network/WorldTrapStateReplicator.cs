#nullable enable

using Arena.Interaction;
using SpacetimeDB;
using SpacetimeDB.Types;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Arena.Network
{
    /// <summary>
    /// Mirrors scope-filtered authoritative trap rows into the stable-ID scene
    /// presentation registry. A row exists only while a trap is mid-cycle, so
    /// insert means "started firing", delete means "back to rest", and the only
    /// payload the presenter needs is the cycle anchor.
    /// </summary>
    public sealed class WorldTrapStateReplicator : MonoBehaviour
    {
        private static WorldTrapStateReplicator? _instance;
        private DbConnection? _connection;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (_instance != null || !ArenaRuntimeSceneGate.ShouldRunArenaRuntimeInActiveScene())
                return;

            var gameObject = new GameObject(nameof(WorldTrapStateReplicator));
            DontDestroyOnLoad(gameObject);
            _instance = gameObject.AddComponent<WorldTrapStateReplicator>();
        }

        private void Update()
        {
            DbConnection? connection = NetworkManager.Instance?.Conn;
            if (_connection != null && !ReferenceEquals(_connection, connection))
                Unsubscribe();
            if (_connection == null && connection != null)
                Subscribe(connection);
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
            connection.Db.WorldTrapState.OnInsert += OnInsert;
            connection.Db.WorldTrapState.OnUpdate += OnUpdate;
            connection.Db.WorldTrapState.OnDelete += OnDelete;
            foreach (WorldTrapState row in connection.Db.WorldTrapState.Iter())
                Apply(row);
        }

        private static void OnInsert(EventContext context, WorldTrapState row)
        {
            _ = context;
            Apply(row);
        }

        private static void OnUpdate(
            EventContext context,
            WorldTrapState oldRow,
            WorldTrapState row)
        {
            _ = context;
            _ = oldRow;
            Apply(row);
        }

        private static void OnDelete(EventContext context, WorldTrapState row)
        {
            _ = context;
            TrapRuntimeRegistry.ClearCycle(row.TrapDefinitionId);
        }

        private static void Apply(WorldTrapState row)
        {
            long cycleStartedAtMs = row.CycleStartedAt.MicrosecondsSinceUnixEpoch / 1000L;
            // A mid-cycle join lands on the correct frame instead of replaying
            // the strike, so the anchor doubles as the clock sample.
            ArenaServerClock.RecordObservedServerTimestampMicros(
                row.CycleStartedAt.MicrosecondsSinceUnixEpoch);
            TrapRuntimeRegistry.ApplyCycle(row.TrapDefinitionId, cycleStartedAtMs, row.Activation);
        }

        private void Unsubscribe()
        {
            if (_connection != null)
            {
                _connection.Db.WorldTrapState.OnInsert -= OnInsert;
                _connection.Db.WorldTrapState.OnUpdate -= OnUpdate;
                _connection.Db.WorldTrapState.OnDelete -= OnDelete;
            }
            _connection = null;
            TrapRuntimeRegistry.ClearAllCycles();
        }
    }
}
