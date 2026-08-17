#nullable enable
using System.Collections.Generic;
using UnityEngine;
using SpacetimeDB;
using SpacetimeDB.Types;
using Arena.Combat;
using Arena.Network;
using Arena.Entity;

namespace Arena.Presentation
{
    /// <summary>
    /// Subscribes to PlayerEvent table and spawns floating text at event positions.
    /// Self-bootstrapping singleton.
    /// </summary>
    public class FloatingCombatText : MonoBehaviour
    {
        private const int MaxInactiveInstances = 64;

        private DbConnection? _subscribedConnection;
        private readonly Stack<FloatingTextAnimation> _inactive = new();
        private Camera? _mainCamera;

        internal Camera? MainCamera => _mainCamera;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (!ArenaRuntimeSceneGate.ShouldRunArenaRuntimeInActiveScene())
                return;
            if (FindAnyObjectByType<FloatingCombatText>() != null)
                return;

            var go = new GameObject("FloatingCombatText");
            DontDestroyOnLoad(go);
            go.AddComponent<FloatingCombatText>();
        }

        private void Update()
        {
            if (!ArenaRuntimeSceneGate.ShouldRunArenaRuntimeInActiveScene())
                return;

            if (_mainCamera == null || !_mainCamera.isActiveAndEnabled)
                _mainCamera = Camera.main;

            var conn = NetworkManager.Instance?.Conn;
            SubscribeToConnection(conn);
        }

        private void OnDestroy()
        {
            SubscribeToConnection(null);
        }

        private void SubscribeToConnection(DbConnection? conn)
        {
            if (ReferenceEquals(conn, _subscribedConnection))
                return;

            if (_subscribedConnection != null)
            {
                _subscribedConnection.Db.PlayerEvent.OnInsert -= OnPlayerEvent;
                _subscribedConnection.Db.CombatEffectEvent.OnInsert -= OnCombatEffectEvent;
                _subscribedConnection.Db.CombatEvent.OnInsert -= OnCombatEvent;
            }

            _subscribedConnection = conn;
            if (conn == null)
                return;

            conn.Db.PlayerEvent.OnInsert += OnPlayerEvent;
            conn.Db.CombatEffectEvent.OnInsert += OnCombatEffectEvent;
            conn.Db.CombatEvent.OnInsert += OnCombatEvent;
        }

        private void OnCombatEvent(EventContext ctx, CombatEvent row)
        {
            if (!ArenaRuntimeSceneGate.ShouldRunArenaRuntimeInActiveScene()
                || !string.Equals(row.EventType, CombatEventTypes.Evade, System.StringComparison.Ordinal))
                return;

            SpawnEvadeText(row.Caster, row.Hit);
        }

        private void OnCombatEffectEvent(EventContext ctx, CombatEffectEvent row)
        {
            if (!ArenaRuntimeSceneGate.ShouldRunArenaRuntimeInActiveScene())
                return;

            if (string.Equals(row.EffectType, "EVADE", System.StringComparison.OrdinalIgnoreCase))
            {
                SpawnEvadeText(row.Source, row.Target);
                return;
            }

            if (row.FinalAmount <= 0) return;

            var registry = EntityRegistry.Instance;
            if (registry == null) return;
            bool relevant = registry.IsIdentityVisible(row.Source) || registry.IsIdentityVisible(row.Target);
            if (!relevant) return;

            if (!registry.TryGetCombatTarget(row.Target, out ICombatTargetEntity targetEntity)) return;

            bool isHeal = string.Equals(row.EffectType, "HEAL", System.StringComparison.OrdinalIgnoreCase);
            string prefix = isHeal ? "+" : "";
            string suffix = row.WasCritical ? "!" : "";
            Color color = isHeal
                ? (row.WasCritical ? new Color(0.35f, 1f, 0.45f) : new Color(0.55f, 1f, 0.65f))
                : (row.WasCritical ? new Color(1f, 0.52f, 0.18f) : new Color(1f, 0.95f, 0.65f));

            var pos = targetEntity.GetPresentationRoot().position + Vector3.up * targetEntity.HitHeight;
            SpawnFloatingText(pos, $"{prefix}{row.FinalAmount}{suffix}", color, row.WasCritical ? 34 : 28);
        }

        private void SpawnEvadeText(Identity source, Identity target)
        {
            var registry = EntityRegistry.Instance;
            if (registry == null
                || (!registry.IsIdentityVisible(source) && !registry.IsIdentityVisible(target))
                || !registry.TryGetCombatTarget(target, out ICombatTargetEntity targetEntity))
                return;

            var pos = targetEntity.GetPresentationRoot().position + Vector3.up * targetEntity.HitHeight;
            SpawnFloatingText(pos, "Evade", new Color(0.55f, 0.9f, 1f), 28);
        }

        private void OnPlayerEvent(EventContext ctx, PlayerEvent row)
        {
            if (!ArenaRuntimeSceneGate.ShouldRunArenaRuntimeInActiveScene())
                return;

            if (EntityRegistry.Instance == null || !EntityRegistry.Instance.IsIdentityVisible(row.PlayerId))
                return;

            var pos = new Vector3(row.PosX, row.PosY + 2.5f, row.PosZ);
            string text;
            Color color;

            switch (row.EventType)
            {
                case "PLAYER_DIED":
                    text = "KILLED";
                    color = new Color(1f, 0.2f, 0.2f);
                    break;
                case "PLAYER_RESPAWNED":
                    text = "RESPAWNED";
                    color = new Color(0.3f, 1f, 0.4f);
                    break;
                case "PLAYER_SPAWNED":
                    text = "JOINED";
                    color = new Color(0.8f, 0.8f, 0.8f);
                    break;
                default:
                    return;
            }

            SpawnFloatingText(pos, text, color);
        }

        private void SpawnFloatingText(Vector3 position, string text, Color color, int fontSize = 28)
        {
            FloatingTextAnimation animation = _inactive.Count > 0
                ? _inactive.Pop()
                : CreateInstance();
            animation.Play(position, text, color, fontSize);
        }

        private FloatingTextAnimation CreateInstance()
        {
            var go = new GameObject("FloatingCombatTextItem");
            go.SetActive(false);
            go.transform.SetParent(transform, false);

            var tm = go.AddComponent<TextMesh>();
            tm.characterSize = 0.08f;
            tm.alignment = TextAlignment.Center;
            tm.anchor = TextAnchor.MiddleCenter;

            var animation = go.AddComponent<FloatingTextAnimation>();
            animation.Initialize(this, tm);
            return animation;
        }

        internal void ReturnToPool(FloatingTextAnimation animation)
        {
            animation.Deactivate();
            if (_inactive.Count >= MaxInactiveInstances)
            {
                Destroy(animation.gameObject);
                return;
            }

            _inactive.Push(animation);
        }
    }

    /// <summary>
    /// Animates a floating text: rises and fades over 1.5 seconds, then returns to its owner pool.
    /// </summary>
    internal class FloatingTextAnimation : MonoBehaviour
    {
        private const float Duration = 1.5f;
        private const float RiseSpeed = 1.5f;

        private FloatingCombatText? _owner;
        private TextMesh? _textMesh;
        private Color _baseColor;
        private float _age;
        private bool _isPlaying;

        internal void Initialize(FloatingCombatText owner, TextMesh textMesh)
        {
            _owner = owner;
            _textMesh = textMesh;
        }

        internal void Play(Vector3 position, string text, Color color, int fontSize)
        {
            _age = 0f;
            _baseColor = color;
            _isPlaying = true;
            transform.SetPositionAndRotation(position, Quaternion.identity);

            if (_textMesh != null)
            {
                _textMesh.text = text;
                _textMesh.fontSize = fontSize;
                _textMesh.color = color;
            }

            gameObject.SetActive(true);
        }

        internal void Deactivate()
        {
            _isPlaying = false;
            gameObject.SetActive(false);
        }

        private void Update()
        {
            if (!_isPlaying)
                return;

            _age += Time.deltaTime;
            if (_age >= Duration)
            {
                if (_owner != null)
                    _owner.ReturnToPool(this);
                else
                    Destroy(gameObject);
                return;
            }

            // Rise
            transform.position += Vector3.up * (RiseSpeed * Time.deltaTime);

            // Billboard toward camera
            Camera? cam = _owner != null ? _owner.MainCamera : null;
            if (cam != null)
            {
                transform.LookAt(cam.transform.position);
                transform.Rotate(0f, 180f, 0f);
            }

            // Fade
            if (_textMesh != null)
            {
                Color color = _baseColor;
                color.a = Mathf.Lerp(_baseColor.a, 0f, _age / Duration);
                _textMesh.color = color;
            }
        }
    }
}
