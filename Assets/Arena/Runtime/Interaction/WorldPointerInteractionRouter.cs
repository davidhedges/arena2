#nullable enable

using System;
using System.Collections.Generic;
using Arena.Combat;
using Arena.Entity;
using Arena.Input;
using Arena.Network;
using Arena.Simulation;
using Arena.UI;
using UnityEngine;

namespace Arena.Interaction
{
    [DefaultExecutionOrder(-900)]
    public sealed class WorldPointerInteractionRouter : MonoBehaviour
    {
        private const float MaxClickDurationSeconds = 0.35f;
        private const float MaxClickDistancePixels = 12f;
        private const float PropScreenPaddingPixels = 40f;
        private const float HoverRangeSlackMeters = 2f;
        private const float OcclusionToleranceMeters = 0.2f;
        private const int RaycastCapacity = 64;

        private static readonly RaycastHit[] RaycastHits = new RaycastHit[RaycastCapacity];
        private static readonly RaycastHitDistanceComparer HitComparer = new();

        private readonly List<WorldInteractionCandidate> _candidates = new(3);
        private WorldPointerGestureClassifier _gesture = null!;
        private WorldInteractionHitbox? _hoveredHitbox;

        public static WorldPointerInteractionRouter? Instance { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (!ArenaRuntimeSceneGate.ShouldRunArenaRuntimeInActiveScene()
                || FindAnyObjectByType<WorldPointerInteractionRouter>() != null)
            {
                return;
            }

            GameObject host = new(nameof(WorldPointerInteractionRouter));
            DontDestroyOnLoad(host);
            host.AddComponent<WorldPointerInteractionRouter>();
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            _gesture = new WorldPointerGestureClassifier(
                MaxClickDurationSeconds,
                MaxClickDistancePixels);
        }

        private void OnDestroy()
        {
            SetHoveredHitbox(null);
            if (Instance == this)
                Instance = null;
        }

        private void Update()
        {
            if (!ArenaRuntimeSceneGate.ShouldRunArenaRuntimeInActiveScene())
            {
                _gesture.Cancel();
                SetHoveredHitbox(null);
                return;
            }

            PlayerEntity? localPlayer = EntityRegistry.Instance?.LocalPlayerEntity;
            LocalPlayerInputSource? input = localPlayer?.GetLocalInputSource();
            if (localPlayer == null || input == null)
            {
                _gesture.Cancel();
                SetHoveredHitbox(null);
                return;
            }

            if (!input.CursorLocked)
                UpdateHover(localPlayer, input.MousePosition);
            else
                SetHoveredHitbox(null);

            if (input.EscapePressed
                && LocalInteractionState.Instance.Active != null
                && SpellInputHandler.Instance?.IsAimActive != true
                && !RuntimeUiEscapeRouter.EscapeConsumedThisFrame)
            {
                if (!RuntimeUiEscapeRouter.TryCloseTopmost()
                    && DoorInteractionNetworkBridge.TryCancelActiveInteraction())
                {
                    RuntimeUiEscapeRouter.ConsumeEscapeThisFrame();
                }
            }

            if (input.RightMousePressed)
            {
                bool consumed = RuntimeUiPointerBlocker.IsPointerOverUi(input.MousePosition);
                if (!consumed)
                    consumed = SpellInputHandler.Instance?.TryCancelAimFromSecondaryWorldAction() == true;

                _gesture.Begin(input.MousePosition, Time.unscaledTime, consumed);
            }

            if (_gesture.IsActive && input.RightMouseHeld)
                _gesture.Track(input.MousePosition);

            if (!input.RightMouseReleased)
                return;

            bool blockedOnRelease = RuntimeUiPointerBlocker.IsPointerOverUi(input.MousePosition);
            WorldPointerGestureResult result = _gesture.Release(
                input.MousePosition,
                Time.unscaledTime,
                blockedOnRelease);
            if (result == WorldPointerGestureResult.Click)
                DispatchAtPointer(localPlayer, input.MousePosition);
        }

        private void DispatchAtPointer(PlayerEntity localPlayer, Vector2 screenPosition)
        {
            Camera? camera = Camera.main;
            if (camera == null)
                return;

            CollectCandidates(
                camera,
                screenPosition,
                localPlayer,
                out _,
                out string propDenialReason);
            if (_candidates.Count > 0)
            {
                WorldInteractionArbitration.TryDispatchBest(
                    _candidates,
                    localPlayer.GetRenderPosition());
            }
            else if (!string.IsNullOrWhiteSpace(propDenialReason))
            {
                LocalInteractionState.ReportDenial(propDenialReason);
            }
        }

        private void UpdateHover(PlayerEntity localPlayer, Vector2 screenPosition)
        {
            Camera? camera = Camera.main;
            if (camera == null
                || RuntimeUiPointerBlocker.IsPointerOverUi(screenPosition)
                || SpellInputHandler.Instance?.IsAimActive == true)
            {
                SetHoveredHitbox(null);
                return;
            }

            CollectCandidates(
                camera,
                screenPosition,
                localPlayer,
                out WorldInteractionHitbox? propHitbox,
                out _);
            bool hasSelectedCandidate = WorldInteractionArbitration.TrySelectBest(
                _candidates,
                localPlayer.GetRenderPosition(),
                out WorldInteractionCandidate selected);
            if (propHitbox != null
                && (!hasSelectedCandidate
                    || selected.Kind == WorldInteractionCandidateKind.Prop))
            {
                SetHoveredHitbox(propHitbox);
            }
            else
            {
                SetHoveredHitbox(null);
            }
        }

        private void CollectCandidates(
            Camera camera,
            Vector2 screenPosition,
            PlayerEntity localPlayer,
            out WorldInteractionHitbox? propHitbox,
            out string propDenialReason)
        {
            _candidates.Clear();
            if (InventoryScreen.TryGetLootCandidate(camera, screenPosition, out WorldInteractionCandidate loot))
                _candidates.Add(loot);
            if (TargetSelector.Instance != null
                && TargetSelector.Instance.TryGetSecondaryWorldCandidate(
                    camera,
                    screenPosition,
                    out WorldInteractionCandidate combat))
            {
                _candidates.Add(combat);
            }
            if (TryGetPropCandidate(
                    camera,
                    screenPosition,
                    localPlayer,
                    out WorldInteractionCandidate prop,
                    out propHitbox,
                    out propDenialReason))
            {
                _candidates.Add(prop);
            }
        }

        private static bool TryGetPropCandidate(
            Camera camera,
            Vector2 screenPosition,
            PlayerEntity localPlayer,
            out WorldInteractionCandidate candidate,
            out WorldInteractionHitbox? selectedHitbox,
            out string denialReason)
        {
            candidate = default;
            selectedHitbox = null;
            denialReason = string.Empty;
            IWorldInteractable? selectedInteractable = null;
            float bestScore = float.PositiveInfinity;
            string bestDenialReason = string.Empty;
            float bestDenialScore = float.PositiveInfinity;
            WorldInteractionHitbox? bestDeniedHitbox = null;
            Vector3 actorPosition = localPlayer.GetRenderPosition();

            IReadOnlyList<WorldInteractionHitbox> hitboxes =
                WorldInteractionHitbox.ActiveHitboxes;
            for (int i = 0; i < hitboxes.Count; i++)
            {
                WorldInteractionHitbox hitbox = hitboxes[i];
                if (hitbox == null || !hitbox.isActiveAndEnabled)
                    continue;
                Collider? collider = hitbox.TargetCollider;
                IWorldInteractable? interactable = hitbox.Interactable;
                if (collider == null
                    || !collider.enabled
                    || interactable == null
                    || collider.transform.IsChildOf(localPlayer.GameObject.transform)
                    || !TryProjectBounds(
                        camera,
                        collider.bounds,
                        out Rect screenBounds,
                        out float boundsDepth)
                    || !WorldInteractionScreenTargeting.TryScore(
                        screenBounds,
                        screenPosition,
                        PropScreenPaddingPixels,
                        boundsDepth,
                        out float score)
                    || IsOccluded(
                        camera,
                        screenPosition,
                        localPlayer,
                        hitbox,
                        collider.bounds))
                {
                    continue;
                }

                float hoverRange = interactable.MaxInteractionDistance > 0f
                    ? interactable.MaxInteractionDistance + HoverRangeSlackMeters
                    : 0f;
                if (hoverRange > 0f
                    && (interactable.InteractionPoint - actorPosition).sqrMagnitude
                    > hoverRange * hoverRange)
                {
                    continue;
                }

                if (!interactable.CanInteractLocally(
                        actorPosition,
                        out string localDenialReason))
                {
                    if (score < bestDenialScore)
                    {
                        bestDenialScore = score;
                        bestDenialReason = localDenialReason;
                        bestDeniedHitbox = hitbox;
                    }
                    continue;
                }

                if (score > bestScore
                    || Mathf.Approximately(score, bestScore)
                    && selectedInteractable != null
                    && string.CompareOrdinal(
                        interactable.StableInteractionId,
                        selectedInteractable.StableInteractionId) >= 0)
                {
                    continue;
                }

                bestScore = score;
                selectedInteractable = interactable;
                selectedHitbox = hitbox;
            }

            if (selectedInteractable == null)
            {
                selectedHitbox = bestDeniedHitbox;
                denialReason = bestDenialReason;
                return false;
            }

            Vector3 interactionPoint = selectedInteractable.InteractionPoint;
            float screenDepth = camera.WorldToScreenPoint(interactionPoint).z;
            candidate = new WorldInteractionCandidate(
                WorldInteractionCandidateKind.Prop,
                selectedInteractable.StableInteractionId,
                selectedInteractable.InteractionVerb,
                interactionPoint,
                screenDepth,
                WorldInteractionArbitration.PropPriority,
                selectedInteractable.MaxInteractionDistance,
                selectedInteractable.RequestInteraction);
            return true;
        }

        private void SetHoveredHitbox(WorldInteractionHitbox? hitbox)
        {
            if (_hoveredHitbox == hitbox)
                return;

            _hoveredHitbox?.SetHovered(false);
            _hoveredHitbox = hitbox;
            _hoveredHitbox?.SetHovered(true);
        }

        private static bool TryProjectBounds(
            Camera camera,
            Bounds bounds,
            out Rect screenBounds,
            out float screenDepth)
        {
            screenBounds = default;
            screenDepth = camera.WorldToScreenPoint(bounds.center).z;
            if (screenDepth <= 0f)
                return false;

            Vector3 min = bounds.min;
            Vector3 max = bounds.max;
            float minX = float.PositiveInfinity;
            float minY = float.PositiveInfinity;
            float maxX = float.NegativeInfinity;
            float maxY = float.NegativeInfinity;
            for (int x = 0; x < 2; x++)
            for (int y = 0; y < 2; y++)
            for (int z = 0; z < 2; z++)
            {
                Vector3 world = new(
                    x == 0 ? min.x : max.x,
                    y == 0 ? min.y : max.y,
                    z == 0 ? min.z : max.z);
                Vector3 screen = camera.WorldToScreenPoint(world);
                if (screen.z <= 0f)
                    return false;

                minX = Mathf.Min(minX, screen.x);
                minY = Mathf.Min(minY, screen.y);
                maxX = Mathf.Max(maxX, screen.x);
                maxY = Mathf.Max(maxY, screen.y);
            }

            screenBounds = Rect.MinMaxRect(minX, minY, maxX, maxY);
            return screenBounds.width > 0.5f && screenBounds.height > 0.5f;
        }

        private static bool IsOccluded(
            Camera camera,
            Vector2 screenPosition,
            PlayerEntity localPlayer,
            WorldInteractionHitbox hitbox,
            Bounds targetBounds)
        {
            Ray ray = camera.ScreenPointToRay(screenPosition);
            float targetDistance;
            if (!targetBounds.IntersectRay(ray, out targetDistance))
            {
                targetDistance = Vector3.Dot(
                        targetBounds.center - ray.origin,
                        ray.direction)
                    - targetBounds.extents.magnitude;
            }
            if (targetDistance <= 0f)
                return true;

            int hitCount = Physics.RaycastNonAlloc(
                ray,
                RaycastHits,
                targetDistance + targetBounds.extents.magnitude,
                Physics.DefaultRaycastLayers,
                QueryTriggerInteraction.Collide);
            Array.Sort(RaycastHits, 0, hitCount, HitComparer);
            Transform? interactableRoot = hitbox.InteractableRoot;
            for (int i = 0; i < hitCount; i++)
            {
                Collider collider = RaycastHits[i].collider;
                if (collider == null
                    || collider.transform.IsChildOf(localPlayer.GameObject.transform)
                    || collider.isTrigger)
                {
                    continue;
                }

                if (interactableRoot != null
                    && (collider.transform == interactableRoot
                        || collider.transform.IsChildOf(interactableRoot)))
                {
                    return false;
                }

                return RaycastHits[i].distance + OcclusionToleranceMeters
                    < targetDistance;
            }

            return false;
        }

        private sealed class RaycastHitDistanceComparer : IComparer<RaycastHit>
        {
            public int Compare(RaycastHit x, RaycastHit y)
                => x.distance.CompareTo(y.distance);
        }
    }
}
