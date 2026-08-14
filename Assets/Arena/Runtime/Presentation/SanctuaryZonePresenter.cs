#nullable enable
using System.Collections.Generic;
using Arena.Input;
using Arena.Network;
using SpacetimeDB;
using SpacetimeDB.Types;
using UnityEngine;

namespace Arena.Presentation
{
    /// <summary>Spawns the authored Sanctuary VFX from authoritative replicated zones.</summary>
    public sealed class SanctuaryZonePresenter : MonoBehaviour
    {
        // The authored prefab was paired with Sanctuary's original 2m radius.
        // Scale from replicated gameplay data so the wall and indicator remain aligned.
        private const float PrefabReferenceRadiusMeters = 2f;
        private static SanctuaryZonePresenter? _instance;
        private readonly Dictionary<ulong, GameObject> _visuals = new();
        private DbConnection? _connection;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (_instance != null || !ArenaRuntimeSceneGate.ShouldRunArenaRuntimeInActiveScene())
                return;

            var go = new GameObject(nameof(SanctuaryZonePresenter));
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<SanctuaryZonePresenter>();
        }

        private void Update()
        {
            DbConnection? connection = NetworkManager.Instance?.Conn;
            if (_connection != null && !ReferenceEquals(_connection, connection))
                Unsubscribe();
            if (_connection != null || connection == null)
                return;

            _connection = connection;
            connection.Db.ActiveSanctuaryZone.OnInsert += OnInsert;
            connection.Db.ActiveSanctuaryZone.OnUpdate += OnUpdate;
            connection.Db.ActiveSanctuaryZone.OnDelete += OnDelete;
            foreach (ActiveSanctuaryZone row in connection.Db.ActiveSanctuaryZone.Iter())
                SpawnOrUpdate(row);
        }

        private void OnDestroy()
        {
            Unsubscribe();
            if (_instance == this)
                _instance = null;
        }

        private void OnInsert(EventContext context, ActiveSanctuaryZone row)
        {
            _ = context;
            SpawnOrUpdate(row);
        }

        private void OnUpdate(
            EventContext context,
            ActiveSanctuaryZone oldRow,
            ActiveSanctuaryZone row)
        {
            _ = context;
            _ = oldRow;
            SpawnOrUpdate(row);
        }

        private void OnDelete(EventContext context, ActiveSanctuaryZone row)
        {
            _ = context;
            Remove(row.ZoneId);
        }

        private void SpawnOrUpdate(ActiveSanctuaryZone row)
        {
            ActiveSanctuaryZoneRuntime.Upsert(row);
            Vector3 position = new(row.CenterX, row.CenterY, row.CenterZ);
            if (_visuals.TryGetValue(row.ZoneId, out GameObject existing))
            {
                ApplyVisualTransform(existing, position, row.Radius);
                return;
            }

            GameObject? prefab = Resources.Load<GameObject>(row.VisualResourcePath);
            if (prefab == null)
            {
                Debug.LogError(
                    $"Sanctuary {row.ZoneId} could not load Resources/{row.VisualResourcePath}.prefab");
                return;
            }

            GameObject visual = Instantiate(prefab, position, Quaternion.identity);
            visual.name = $"{row.SpellId}_Zone_{row.ZoneId}";
            ApplyVisualTransform(visual, position, row.Radius);
            foreach (Collider collider in visual.GetComponentsInChildren<Collider>(true))
                collider.enabled = false;
            _visuals[row.ZoneId] = visual;
        }

        private static void ApplyVisualTransform(GameObject visual, Vector3 position, float radius)
        {
            visual.transform.position = position;
            float scale = Mathf.Max(0f, radius) / PrefabReferenceRadiusMeters;
            visual.transform.localScale = Vector3.one * scale;
        }

        private void Remove(ulong zoneId)
        {
            ActiveSanctuaryZoneRuntime.Remove(zoneId);
            if (!_visuals.TryGetValue(zoneId, out GameObject visual))
                return;
            _visuals.Remove(zoneId);
            if (visual == null)
                return;
            Destroy(visual);
        }

        private void Unsubscribe()
        {
            if (_connection != null)
            {
                _connection.Db.ActiveSanctuaryZone.OnInsert -= OnInsert;
                _connection.Db.ActiveSanctuaryZone.OnUpdate -= OnUpdate;
                _connection.Db.ActiveSanctuaryZone.OnDelete -= OnDelete;
            }
            _connection = null;
            foreach (GameObject visual in _visuals.Values)
            {
                if (visual != null)
                    Destroy(visual);
            }
            _visuals.Clear();
            ActiveSanctuaryZoneRuntime.Clear();
        }
    }
}
