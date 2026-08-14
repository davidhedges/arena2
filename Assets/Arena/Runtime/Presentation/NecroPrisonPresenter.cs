#nullable enable
using System.Collections.Generic;
using Arena.Input;
using Arena.Network;
using SpacetimeDB;
using SpacetimeDB.Types;
using UnityEngine;

namespace Arena.Presentation
{
    /// <summary>
    /// Reconstructs active Necro Prison visuals from authoritative rows and
    /// plays the authored dissipate effect only when a replicated prison ends.
    /// </summary>
    public sealed class NecroPrisonPresenter : MonoBehaviour
    {
        // The authored prefab's outer triangle reaches roughly five meters.
        private const float PrefabReferenceRadiusMeters = 5f;
        private const float DissipateVisualLifetimeSeconds = 6f;
        private static NecroPrisonPresenter? _instance;
        private readonly Dictionary<ulong, GameObject> _visuals = new();
        private DbConnection? _connection;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (_instance != null || !ArenaRuntimeSceneGate.ShouldRunArenaRuntimeInActiveScene())
                return;

            var go = new GameObject(nameof(NecroPrisonPresenter));
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<NecroPrisonPresenter>();
        }

        private void Update()
        {
            DbConnection? connection = NetworkManager.Instance?.Conn;
            if (_connection != null && !ReferenceEquals(_connection, connection))
                Unsubscribe();
            if (_connection != null || connection == null)
                return;

            _connection = connection;
            connection.Db.ActiveNecroPrison.OnInsert += OnInsert;
            connection.Db.ActiveNecroPrison.OnUpdate += OnUpdate;
            connection.Db.ActiveNecroPrison.OnDelete += OnDelete;
            foreach (ActiveNecroPrison row in connection.Db.ActiveNecroPrison.Iter())
                SpawnOrUpdate(row);
        }

        private void OnDestroy()
        {
            Unsubscribe();
            if (_instance == this)
                _instance = null;
        }

        private void OnInsert(EventContext context, ActiveNecroPrison row)
        {
            _ = context;
            SpawnOrUpdate(row);
        }

        private void OnUpdate(
            EventContext context,
            ActiveNecroPrison oldRow,
            ActiveNecroPrison row)
        {
            _ = context;
            _ = oldRow;
            SpawnOrUpdate(row);
        }

        private void OnDelete(EventContext context, ActiveNecroPrison row)
        {
            _ = context;
            Remove(row.PrisonId);
            SpawnDissipateVisual(row);
        }

        private void SpawnOrUpdate(ActiveNecroPrison row)
        {
            ActiveNecroPrisonRuntime.Upsert(row);
            Vector3 position = new(row.CenterX, row.CenterY, row.CenterZ);
            if (_visuals.TryGetValue(row.PrisonId, out GameObject existing))
            {
                ApplyVisualTransform(existing, position, row.FacingYaw, row.Radius);
                return;
            }

            GameObject? prefab = Resources.Load<GameObject>(row.VisualResourcePath);
            if (prefab == null)
            {
                Debug.LogError(
                    $"Necro Prison {row.PrisonId} could not load Resources/{row.VisualResourcePath}.prefab");
                return;
            }

            GameObject visual = Instantiate(prefab, position, Quaternion.identity);
            visual.name = $"{row.SpellId}_Prison_{row.PrisonId}";
            ApplyVisualTransform(visual, position, row.FacingYaw, row.Radius);
            DisableColliders(visual);
            _visuals[row.PrisonId] = visual;
        }

        private void SpawnDissipateVisual(ActiveNecroPrison row)
        {
            GameObject? prefab = Resources.Load<GameObject>(row.DissipateVisualResourcePath);
            if (prefab == null)
            {
                Debug.LogError(
                    $"Necro Prison {row.PrisonId} could not load Resources/{row.DissipateVisualResourcePath}.prefab");
                return;
            }

            Vector3 position = new(row.CenterX, row.CenterY, row.CenterZ);
            GameObject visual = Instantiate(prefab, position, Quaternion.identity);
            visual.name = $"{row.SpellId}_Dissipate_{row.PrisonId}";
            ApplyVisualTransform(visual, position, row.FacingYaw, row.Radius);
            DisableColliders(visual);
            Destroy(visual, DissipateVisualLifetimeSeconds);
        }

        private static void ApplyVisualTransform(
            GameObject visual,
            Vector3 position,
            float facingYaw,
            float radius)
        {
            visual.transform.SetPositionAndRotation(
                position,
                Quaternion.Euler(0f, facingYaw * Mathf.Rad2Deg, 0f));
            float scale = Mathf.Max(0f, radius) / PrefabReferenceRadiusMeters;
            visual.transform.localScale = Vector3.one * scale;
        }

        private static void DisableColliders(GameObject visual)
        {
            foreach (Collider collider in visual.GetComponentsInChildren<Collider>(true))
                collider.enabled = false;
        }

        private void Remove(ulong prisonId)
        {
            ActiveNecroPrisonRuntime.Remove(prisonId);
            if (!_visuals.TryGetValue(prisonId, out GameObject visual))
                return;
            _visuals.Remove(prisonId);
            if (visual != null)
                Destroy(visual);
        }

        private void Unsubscribe()
        {
            if (_connection != null)
            {
                _connection.Db.ActiveNecroPrison.OnInsert -= OnInsert;
                _connection.Db.ActiveNecroPrison.OnUpdate -= OnUpdate;
                _connection.Db.ActiveNecroPrison.OnDelete -= OnDelete;
            }
            _connection = null;
            foreach (GameObject visual in _visuals.Values)
            {
                if (visual != null)
                    Destroy(visual);
            }
            _visuals.Clear();
            ActiveNecroPrisonRuntime.Clear();
        }
    }
}
