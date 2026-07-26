#nullable enable

using System;
using Arena.Input;
using SpacetimeDB;
using SpacetimeDB.Types;
using UnityEngine;

namespace Arena.Network
{
    /// <summary>
    /// Mirrors scope-filtered authoritative door rows into the local prediction
    /// cache. Visual door binding is layered onto the same callbacks in Slice 4.
    /// </summary>
    public sealed class WorldDoorStateReplicator : MonoBehaviour
    {
        private static WorldDoorStateReplicator? _instance;
        private DbConnection? _connection;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (_instance != null || !ArenaRuntimeSceneGate.ShouldRunArenaRuntimeInActiveScene())
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
            foreach (WorldDoorState row in connection.Db.WorldDoorState.Iter())
                WorldDoorCollisionRuntime.Upsert(row);
        }

        private static void OnInsert(EventContext context, WorldDoorState row)
        {
            _ = context;
            WorldDoorCollisionRuntime.Upsert(row);
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
        }

        private static void OnDelete(EventContext context, WorldDoorState row)
        {
            _ = context;
            WorldDoorCollisionRuntime.Remove(row);
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
        }
    }
}
