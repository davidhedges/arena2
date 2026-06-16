#nullable enable
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Arena.Entity;
using Arena.Input;
using Arena.Network;
using Arena.Presentation;

namespace Arena.Combat
{
    /// <summary>
        /// Scene-level singleton that tracks the local player's selected target.
        ///
        /// Selection methods:
        ///   Left-click                → screen-space hit test against networked player capsules
        ///   Right-click release       → select target and arm auto-attack
    ///   Tab                       → cycle through non-local players
    ///   Escape                    → clear selection and clear armed auto-attack
    ///
    /// INVARIANT: Selection stays local except for explicit auto-attack
    /// arm/clear reducer calls.
    /// </summary>
    public class TargetSelector : MonoBehaviour
    {
        public static TargetSelector Instance { get; private set; } = null!;

        private const float MaxTargetSelectDistance = 200f;
        private const float MinTargetScreenRadiusPixels = 14f;
        private const float MaxTargetScreenRadiusPixels = 80f;
        private const float TargetScreenPaddingPixels = 4f;
        private const float TargetDepthTieBreakScale = 0.0001f;

        public ICombatTargetEntity? SelectedTarget { get; private set; }
        public ICombatTargetEntity? HoveredTarget { get; private set; }

        private ICombatTargetEntity? _prevHovered;
        private ICombatTargetEntity? _prevSelected;

        /// <summary>
        /// Identity string expected by the CastRequest reducer's targetId argument.
        /// Empty string when no target is selected.
        /// </summary>
        public string SelectedTargetId => SelectedTarget?.TargetIdentity.ToString() ?? "";

        public bool SelectTarget(ICombatTargetEntity entity)
        {
            if (!CanSelect(entity))
                return false;

            SelectedTarget = entity;
            Debug.Log($"[TargetSelector] Target: {entity.DisplayName} ({entity.TargetIdentity})");
            return true;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            var go = new GameObject("TargetSelector");
            DontDestroyOnLoad(go);
            go.AddComponent<TargetSelector>();
        }

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
        }

        private void Update()
        {
            LocalPlayerInputSource? input = EntityRegistry.Instance?.LocalPlayerEntity?.GetLocalInputSource();
            if (input == null)
                return;
            bool aimActive = SpellInputHandler.Instance?.IsAimActive == true;

            if (input.LeftMousePressed)
                TrySelectTargetAtCursor(false);

            if (!aimActive && input.RightMouseReleased)
                TrySelectTargetAtCursor(true);

            if (input.TabPressed)
                CycleTarget();

            if (input.EscapePressed)
            {
                SelectedTarget = null;
                ClearAutoAttackOnServer();
            }

            // Hover highlighting
            if (!input.CursorLocked)
                UpdateHover(input.MousePosition);
            else
                HoveredTarget = null;

            // Update highlight visuals when hover/selection changes
            if (HoveredTarget != _prevHovered)
            {
                _prevHovered?.SetHighlight(false);
                HoveredTarget?.SetHighlight(true);
                _prevHovered = HoveredTarget;
            }
            if (SelectedTarget != _prevSelected)
            {
                _prevSelected?.SetSelected(false);
                SelectedTarget?.SetSelected(true);
                _prevSelected = SelectedTarget;
            }
            else
            {
                SelectedTarget?.RefreshTargetingPresentation();
            }
        }

        // -----------------------------------------------------------------------

        private void TrySelectTargetAtCursor(bool armAutoAttack)
        {
            ICombatTargetEntity? entity = ResolveTargetAtCursor();
            if (entity == null)
                return;

            if (armAutoAttack && !PartyRelationship.IsHostileToLocal(entity))
            {
                var relation = PartyRelationship.RelationToLocal(entity);
                Debug.Log(
                    $"[TargetSelector] Auto-attack rejected: target {entity.DisplayName} relation={relation} is not Hostile");
                return;
            }

            if (!SelectTarget(entity))
                return;

            if (armAutoAttack)
            {
                EntityRegistry.Instance?.LocalPlayerEntity?.SetInCombat(true);
                ArmAutoAttackOnServer(entity);
            }
        }

        private ICombatTargetEntity? ResolveTargetAtCursor()
        {
            var cam = Camera.main;
            if (cam == null) return null;
            LocalPlayerInputSource? input = EntityRegistry.Instance?.LocalPlayerEntity?.GetLocalInputSource();
            if (input == null) return null;

            return ResolveTargetAtScreenPoint(cam, input.MousePosition);
        }

        private static bool CanSelect(ICombatTargetEntity entity) => entity.IsAlive;

        private static void ArmAutoAttackOnServer(ICombatTargetEntity entity)
        {
            var conn = NetworkManager.Instance?.Conn;
            if (conn == null)
                return;

            conn.Reducers.ArmAutoAttackTarget(entity.TargetIdentity.ToString());
        }

        private static void ClearAutoAttackOnServer()
        {
            var conn = NetworkManager.Instance?.Conn;
            if (conn == null)
                return;

            conn.Reducers.ClearAutoAttackTarget();
        }

        private void UpdateHover(Vector2 mousePosition)
        {
            var cam = Camera.main;
            if (cam == null) { HoveredTarget = null; return; }

            HoveredTarget = ResolveTargetAtScreenPoint(cam, mousePosition);
        }

        private static ICombatTargetEntity? ResolveTargetAtScreenPoint(Camera cam, Vector2 screenPoint)
        {
            var registry = EntityRegistry.Instance;
            if (registry == null) return null;
            var local = registry.LocalPlayerEntity;

            ICombatTargetEntity? best = null;
            float bestScore = float.PositiveInfinity;
            foreach (var entity in registry.AllPlayers)
            {
                if (local != null && entity.Identity == local.Identity) continue; // skip self
                ConsiderTarget(entity, cam, screenPoint, ref best, ref bestScore);
            }
            foreach (var entity in registry.AllNpcs)
            {
                ConsiderTarget(entity, cam, screenPoint, ref best, ref bestScore);
            }

            return best;
        }

        private static void ConsiderTarget(
            ICombatTargetEntity entity,
            Camera cam,
            Vector2 screenPoint,
            ref ICombatTargetEntity? best,
            ref float bestScore)
        {
            if (!entity.IsAlive) return;
            if (!TryGetScreenCapsuleScore(cam, entity, screenPoint, out float score)) return;

            if (score < bestScore)
            {
                bestScore = score;
                best = entity;
            }
        }

        private static bool TryGetScreenCapsuleScore(
            Camera cam,
            ICombatTargetEntity entity,
            Vector2 screenPoint,
            out float score)
        {
            score = default;

            Vector3 basePosition = entity.GetRenderPosition();
            float radius = Mathf.Max(entity.HitRadius, MovementPrediction.DefaultHitRadius);
            float height = Mathf.Max(entity.HitHeight, radius * 2f);
            Vector3 bottom = basePosition + Vector3.up * radius;
            Vector3 top = basePosition + Vector3.up * Mathf.Max(radius, height - radius);
            Vector3 center = basePosition + Vector3.up * (height * 0.5f);

            Vector3 screenBottom = cam.WorldToScreenPoint(bottom);
            Vector3 screenTop = cam.WorldToScreenPoint(top);
            Vector3 screenCenter = cam.WorldToScreenPoint(center);
            if (screenBottom.z <= 0f || screenTop.z <= 0f || screenCenter.z <= 0f)
                return false;
            if (screenCenter.z > MaxTargetSelectDistance)
                return false;

            Vector3 screenRadiusPoint = cam.WorldToScreenPoint(center + cam.transform.right * radius);
            if (screenRadiusPoint.z <= 0f)
                return false;

            Vector2 capsuleStart = new(screenBottom.x, screenBottom.y);
            Vector2 capsuleEnd = new(screenTop.x, screenTop.y);
            float screenRadius = Mathf.Clamp(
                Vector2.Distance(
                    new Vector2(screenCenter.x, screenCenter.y),
                    new Vector2(screenRadiusPoint.x, screenRadiusPoint.y)),
                MinTargetScreenRadiusPixels,
                MaxTargetScreenRadiusPixels);
            float paddedRadius = screenRadius + TargetScreenPaddingPixels;
            float distance = DistanceToSegment(screenPoint, capsuleStart, capsuleEnd);
            if (distance > paddedRadius)
                return false;

            score = distance / paddedRadius + screenCenter.z * TargetDepthTieBreakScale;
            return true;
        }

        private static float DistanceToSegment(Vector2 point, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float lengthSq = ab.sqrMagnitude;
            if (lengthSq <= 0.0001f)
                return Vector2.Distance(point, a);

            float t = Mathf.Clamp01(Vector2.Dot(point - a, ab) / lengthSq);
            Vector2 closest = a + ab * t;
            return Vector2.Distance(point, closest);
        }

        private void CycleTarget()
        {
            var registry = EntityRegistry.Instance;
            if (registry == null) return;
            var local = registry.LocalPlayerEntity;

            var candidates = new List<ICombatTargetEntity>();
            foreach (var entity in registry.AllPlayers)
            {
                if (entity.IsAlive
                    && (local == null || entity.Identity != local.Identity)
                    && PartyRelationship.IsHostileToLocal(entity))
                    candidates.Add(entity);
            }
            foreach (var entity in registry.AllNpcs)
            {
                if (entity.IsAlive && PartyRelationship.IsHostileToLocal(entity))
                    candidates.Add(entity);
            }

            if (candidates.Count == 0) { SelectedTarget = null; return; }

            if (SelectedTarget == null || !candidates.Contains(SelectedTarget))
            {
                SelectedTarget = candidates[0];
            }
            else
            {
                int idx = candidates.IndexOf(SelectedTarget);
                SelectedTarget = candidates[(idx + 1) % candidates.Count];
            }

            Debug.Log($"[TargetSelector] Cycled -> {SelectedTarget.DisplayName}");
        }
    }
}
