#nullable enable
using System.Collections.Generic;
using Arena.Input;
using Arena.Network;
using SpacetimeDB;
using SpacetimeDB.Types;
using UnityEngine;

namespace Arena.Presentation
{
    /// <summary>Spawns collider-bearing world-object visuals from authoritative obstacle rows.</summary>
    public sealed class WorldObstaclePresenter : MonoBehaviour
    {
        private static WorldObstaclePresenter? _instance;
        private readonly Dictionary<ulong, GameObject> _visuals = new();
        private DbConnection? _connection;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (_instance != null || !ArenaRuntimeSceneGate.ShouldRunArenaRuntimeInActiveScene())
                return;

            var go = new GameObject(nameof(WorldObstaclePresenter));
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<WorldObstaclePresenter>();
        }

        private void Update()
        {
            DbConnection? connection = NetworkManager.Instance?.Conn;
            if (_connection != null && !ReferenceEquals(_connection, connection))
                Unsubscribe();
            if (_connection != null || connection == null)
                return;

            _connection = connection;
            connection.Db.ActiveWorldObstacle.OnInsert += OnInsert;
            connection.Db.ActiveWorldObstacle.OnUpdate += OnUpdate;
            connection.Db.ActiveWorldObstacle.OnDelete += OnDelete;
            foreach (ActiveWorldObstacle row in connection.Db.ActiveWorldObstacle.Iter())
                SpawnOrUpdate(row);
        }

        private void OnDestroy()
        {
            Unsubscribe();
            if (_instance == this)
                _instance = null;
        }

        private void OnInsert(EventContext context, ActiveWorldObstacle row)
        {
            _ = context;
            SpawnOrUpdate(row);
        }

        private void OnUpdate(EventContext context, ActiveWorldObstacle oldRow, ActiveWorldObstacle row)
        {
            _ = context;
            _ = oldRow;
            SpawnOrUpdate(row);
        }

        private void OnDelete(EventContext context, ActiveWorldObstacle row)
        {
            _ = context;
            Remove(row.ObstacleId);
        }

        private void SpawnOrUpdate(ActiveWorldObstacle row)
        {
            ActiveWorldObstacleRuntime.Upsert(row);
            if (_visuals.TryGetValue(row.ObstacleId, out GameObject existing))
            {
                existing.transform.SetPositionAndRotation(
                    new Vector3(row.RootX, row.RootY, row.RootZ),
                    Quaternion.Euler(0f, row.Yaw * Mathf.Rad2Deg, 0f));
                return;
            }

            GameObject? prefab = Resources.Load<GameObject>(row.VisualResourcePath);
            if (prefab == null)
            {
                Debug.LogError(
                    $"World obstacle {row.ObstacleId} could not load Resources/{row.VisualResourcePath}.prefab");
                return;
            }

            GameObject visual = Instantiate(
                prefab,
                new Vector3(row.RootX, row.RootY, row.RootZ),
                Quaternion.Euler(0f, row.Yaw * Mathf.Rad2Deg, 0f));
            visual.name = $"{row.SpellId}_Obstacle_{row.ObstacleId}";
            DisablePrefabColliders(visual);
            _visuals[row.ObstacleId] = visual;
            StartCoroutine(
                CombatVFXLifecycleRegistry.DestroyWhenParticleSystemsFinish(
                    visual,
                    row.VisualResourcePath));
        }

        private static void DisablePrefabColliders(GameObject visual)
        {
            foreach (Collider collider in visual.GetComponentsInChildren<Collider>(true))
                collider.enabled = false;
        }

        private void Remove(ulong obstacleId)
        {
            ActiveWorldObstacleRuntime.Remove(obstacleId);
            _visuals.Remove(obstacleId);
        }

        private void Unsubscribe()
        {
            if (_connection != null)
            {
                _connection.Db.ActiveWorldObstacle.OnInsert -= OnInsert;
                _connection.Db.ActiveWorldObstacle.OnUpdate -= OnUpdate;
                _connection.Db.ActiveWorldObstacle.OnDelete -= OnDelete;
            }
            _connection = null;
            foreach (GameObject visual in _visuals.Values)
            {
                if (visual != null)
                    Destroy(visual);
            }
            _visuals.Clear();
            ActiveWorldObstacleRuntime.Clear();
        }
    }
}
