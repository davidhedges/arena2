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
    ///   Left-click                → raycast against player capsules
    ///   Right-click release       → raycast, select target, and arm auto-attack
    ///   Tab                       → cycle through non-local players
    ///   Escape                    → clear selection and clear armed auto-attack
    ///
    /// INVARIANT: Selection stays local except for explicit auto-attack
    /// arm/clear reducer calls.
    /// </summary>
    public class TargetSelector : MonoBehaviour
    {
        public static TargetSelector Instance { get; private set; } = null!;

        public PlayerEntity? SelectedTarget { get; private set; }
        public PlayerEntity? HoveredTarget { get; private set; }

        private PlayerEntity? _prevHovered;
        private PlayerEntity? _prevSelected;

        /// <summary>
        /// Identity string expected by the CastRequest reducer's targetId argument.
        /// Empty string when no target is selected.
        /// </summary>
        public string SelectedTargetId => SelectedTarget?.Identity.ToString() ?? "";

        public bool SelectTarget(PlayerEntity entity)
        {
            if (!CanSelect(entity))
                return false;

            SelectedTarget = entity;
            Debug.Log($"[TargetSelector] Target: {entity.Username} ({entity.Identity})");
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
            PlayerEntity? entity = ResolveTargetAtCursor();
            if (entity == null)
                return;

            if (armAutoAttack && !PartyRelationship.IsHostileToLocal(entity))
            {
                var relation = PartyRelationship.RelationToLocal(entity);
                Debug.Log(
                    $"[TargetSelector] Auto-attack rejected: target {entity.Username} relation={relation} is not Hostile");
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

        private PlayerEntity? ResolveTargetAtCursor()
        {
            var cam = Camera.main;
            if (cam == null) return null;
            LocalPlayerInputSource? input = EntityRegistry.Instance?.LocalPlayerEntity?.GetLocalInputSource();
            if (input == null) return null;

            var ray = cam.ScreenPointToRay(input.MousePosition);
            if (!Physics.Raycast(ray, out var hit, 200f)) return null;

            // Check the hit collider (or its parent) for a PlayerView component.
            var view = hit.collider.GetComponent<PlayerView>()
                    ?? hit.collider.GetComponentInParent<PlayerView>();
            if (view == null) return null;

            var registry = EntityRegistry.Instance;
            if (registry == null) return null;
            var local = registry.LocalPlayerEntity;

            foreach (var entity in registry.AllPlayers)
            {
                if (entity.View != view) continue;
                if (local != null && entity.Identity == local.Identity) continue; // skip self
                if (!entity.IsAlive) continue;

                return entity;
            }

            return null;
        }

        private static bool CanSelect(PlayerEntity entity) => entity.IsAlive;

        private static void ArmAutoAttackOnServer(PlayerEntity entity)
        {
            var conn = NetworkManager.Instance?.Conn;
            if (conn == null)
                return;

            conn.Reducers.ArmAutoAttackTarget(entity.Identity.ToString());
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

            var ray = cam.ScreenPointToRay(mousePosition);
            if (!Physics.Raycast(ray, out var hit, 200f)) { HoveredTarget = null; return; }

            var view = hit.collider.GetComponent<PlayerView>()
                    ?? hit.collider.GetComponentInParent<PlayerView>();
            if (view == null) { HoveredTarget = null; return; }

            var registry = EntityRegistry.Instance;
            if (registry == null) { HoveredTarget = null; return; }
            var local = registry.LocalPlayerEntity;

            foreach (var entity in registry.AllPlayers)
            {
                if (entity.View != view) continue;
                if (local != null && entity.Identity == local.Identity) continue;
                if (!entity.IsAlive) continue;
                HoveredTarget = entity;
                return;
            }
            HoveredTarget = null;
        }

        private void CycleTarget()
        {
            var registry = EntityRegistry.Instance;
            if (registry == null) return;
            var local = registry.LocalPlayerEntity;

            var candidates = registry.AllPlayers
                .Where(e => e.IsAlive
                            && (local == null || e.Identity != local.Identity)
                            && PartyRelationship.IsHostileToLocal(e))
                .ToList();

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

            Debug.Log($"[TargetSelector] Cycled → {SelectedTarget.Username}");
        }
    }
}
