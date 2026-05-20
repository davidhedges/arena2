#nullable enable
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
        private bool _subscribed;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            var go = new GameObject("FloatingCombatText");
            DontDestroyOnLoad(go);
            go.AddComponent<FloatingCombatText>();
        }

        private void Update()
        {
            if (_subscribed) return;
            var conn = NetworkManager.Instance?.Conn;
            if (conn == null) return;
            _subscribed = true;
            conn.Db.PlayerEvent.OnInsert += OnPlayerEvent;
            conn.Db.CombatEffectEvent.OnInsert += OnCombatEffectEvent;
        }

        private void OnCombatEffectEvent(EventContext ctx, CombatEffectEvent row)
        {
            if (row.FinalAmount <= 0) return;

            var registry = EntityRegistry.Instance;
            if (registry == null) return;
            bool relevant = registry.IsIdentityVisible(row.Source) || registry.IsIdentityVisible(row.Target);
            if (!relevant) return;

            if (!registry.TryGetEntity(row.Target, out PlayerEntity targetEntity)) return;

            bool isHeal = string.Equals(row.EffectType, "HEAL", System.StringComparison.OrdinalIgnoreCase);
            string prefix = isHeal ? "+" : "";
            string suffix = row.WasCritical ? "!" : "";
            Color color = isHeal
                ? (row.WasCritical ? new Color(0.35f, 1f, 0.45f) : new Color(0.55f, 1f, 0.65f))
                : (row.WasCritical ? new Color(1f, 0.52f, 0.18f) : new Color(1f, 0.95f, 0.65f));

            var pos = targetEntity.GameObject.transform.position + Vector3.up * 2.0f;
            SpawnFloatingText(pos, $"{prefix}{row.FinalAmount}{suffix}", color, row.WasCritical ? 34 : 28);
        }

        private void OnPlayerEvent(EventContext ctx, PlayerEvent row)
        {
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

        private static void SpawnFloatingText(Vector3 position, string text, Color color, int fontSize = 28)
        {
            var go = new GameObject($"FCT_{text}");
            go.transform.position = position;

            var tm = go.AddComponent<TextMesh>();
            tm.text = text;
            tm.fontSize = fontSize;
            tm.characterSize = 0.08f;
            tm.alignment = TextAlignment.Center;
            tm.anchor = TextAnchor.MiddleCenter;
            tm.color = color;

            go.AddComponent<FloatingTextAnimation>();
        }
    }

    /// <summary>
    /// Animates a floating text: rises and fades over 1.5 seconds, then self-destructs.
    /// </summary>
    internal class FloatingTextAnimation : MonoBehaviour
    {
        private const float Duration = 1.5f;
        private const float RiseSpeed = 1.5f;
        private float _age;
        private TextMesh? _tm;

        private void Awake()
        {
            _tm = GetComponent<TextMesh>();
        }

        private void Update()
        {
            _age += Time.deltaTime;
            if (_age >= Duration)
            {
                Destroy(gameObject);
                return;
            }

            // Rise
            transform.position += Vector3.up * (RiseSpeed * Time.deltaTime);

            // Billboard toward camera
            var cam = Camera.main;
            if (cam != null)
            {
                transform.LookAt(cam.transform.position);
                transform.Rotate(0f, 180f, 0f);
            }

            // Fade
            if (_tm != null)
            {
                var c = _tm.color;
                c.a = Mathf.Lerp(1f, 0f, _age / Duration);
                _tm.color = c;
            }
        }
    }
}
