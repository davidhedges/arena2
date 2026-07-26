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
using UnityEngine.SceneManagement;

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
        private string _lastRuntimeState = string.Empty;
        private int _lastHitboxCount = -1;

        public static WorldPointerInteractionRouter? Instance { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            bool gateOpen = ArenaRuntimeSceneGate.ShouldRunArenaRuntimeInActiveScene();
            WorldPointerInteractionRouter? existing =
                FindAnyObjectByType<WorldPointerInteractionRouter>();
            Debug.Log(
                $"[WorldInteraction] router bootstrap scene="
                + $"'{SceneManager.GetActiveScene().path}' gate={gateOpen} "
                + $"existing={existing != null}.");
            if (!gateOpen || existing != null)
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
            Debug.Log(
                $"[WorldInteraction] router awake object='{name}'.",
                this);
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
                TraceRuntimeState("scene gate disabled");
                _gesture.Cancel();
                SetHoveredHitbox(null);
                return;
            }

            PlayerEntity? localPlayer = EntityRegistry.Instance?.LocalPlayerEntity;
            LocalPlayerInputSource? input = localPlayer?.GetLocalInputSource();
            if (localPlayer == null || input == null)
            {
                TraceRuntimeState(
                    $"waiting for runtime context: player={localPlayer != null}, "
                    + $"input={input != null}");
                _gesture.Cancel();
                SetHoveredHitbox(null);
                return;
            }

            TraceRuntimeState(
                $"ready: scene='{SceneManager.GetActiveScene().path}', "
                + $"camera={Camera.main != null}, cursorLocked={input.CursorLocked}");
            int hitboxCount = WorldInteractionHitbox.ActiveHitboxes.Count;
            if (_lastHitboxCount != hitboxCount)
            {
                _lastHitboxCount = hitboxCount;
                Debug.Log(
                    $"[WorldInteraction] active hitbox count={hitboxCount}.",
                    this);
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

                Debug.Log(
                    $"[WorldInteraction] right press pointer={Format(input.MousePosition)} "
                    + $"consumed={consumed} cursorLocked={input.CursorLocked}.",
                    this);
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
            Debug.Log(
                $"[WorldInteraction] right release pointer={Format(input.MousePosition)} "
                + $"blockedByUi={blockedOnRelease} result={result}.",
                this);
            if (result == WorldPointerGestureResult.Click)
                DispatchAtPointer(localPlayer, input.MousePosition);
        }

        private void DispatchAtPointer(PlayerEntity localPlayer, Vector2 screenPosition)
        {
            Camera? camera = Camera.main;
            if (camera == null)
            {
                Debug.LogWarning(
                    "[WorldInteraction] click rejected: Camera.main is null.",
                    this);
                return;
            }

            CollectCandidates(
                camera,
                screenPosition,
                localPlayer,
                out _,
                out string propDenialReason,
                out PropScanDiagnostics diagnostics);
            Debug.Log(
                $"[WorldInteraction] click scan pointer={Format(screenPosition)} "
                + $"candidates={_candidates.Count}; {diagnostics}.",
                this);
            if (WorldInteractionArbitration.TrySelectBest(
                    _candidates,
                    ResolveLocalActorPosition(localPlayer),
                    out WorldInteractionCandidate selected))
            {
                bool dispatched = selected.Dispatch();
                Debug.Log(
                    $"[WorldInteraction] dispatch kind={selected.Kind} "
                    + $"id='{selected.StableId}' verb='{selected.Verb}' "
                    + $"maxRange={selected.MaxInteractionDistance:F2} "
                    + $"accepted={dispatched}.",
                    this);
            }
            else if (!string.IsNullOrWhiteSpace(propDenialReason))
            {
                Debug.Log(
                    $"[WorldInteraction] click denied: {propDenialReason}",
                    this);
                LocalInteractionState.ReportDenial(propDenialReason);
            }
            else
            {
                Debug.Log(
                    "[WorldInteraction] click produced no selectable candidate.",
                    this);
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
                out _,
                out _);
            bool hasSelectedCandidate = WorldInteractionArbitration.TrySelectBest(
                _candidates,
                ResolveLocalActorPosition(localPlayer),
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
            out string propDenialReason,
            out PropScanDiagnostics propDiagnostics)
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
                    out propDenialReason,
                    out propDiagnostics))
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
            out string denialReason,
            out PropScanDiagnostics diagnostics)
        {
            candidate = default;
            selectedHitbox = null;
            denialReason = string.Empty;
            diagnostics = default;
            IWorldInteractable? selectedInteractable = null;
            float bestScore = float.PositiveInfinity;
            string bestDenialReason = string.Empty;
            float bestDenialScore = float.PositiveInfinity;
            WorldInteractionHitbox? bestDeniedHitbox = null;
            Vector3 actorPosition = ResolveLocalActorPosition(localPlayer);

            IReadOnlyList<WorldInteractionHitbox> hitboxes =
                WorldInteractionHitbox.ActiveHitboxes;
            diagnostics.Registered = hitboxes.Count;
            for (int i = 0; i < hitboxes.Count; i++)
            {
                WorldInteractionHitbox hitbox = hitboxes[i];
                if (hitbox == null || !hitbox.isActiveAndEnabled)
                {
                    diagnostics.Inactive++;
                    continue;
                }
                Collider? collider = hitbox.TargetCollider;
                IWorldInteractable? interactable = hitbox.Interactable;
                if (collider == null || !collider.enabled)
                {
                    diagnostics.MissingCollider++;
                    continue;
                }
                if (interactable == null)
                {
                    diagnostics.MissingSource++;
                    continue;
                }
                if (collider.transform.IsChildOf(localPlayer.GameObject.transform))
                {
                    diagnostics.Self++;
                    continue;
                }
                if (!TryProjectBounds(
                        camera,
                        collider.bounds,
                        out Rect screenBounds,
                        out float boundsDepth))
                {
                    diagnostics.ProjectionFailed++;
                    diagnostics.RecordProjectionFailure(
                        interactable.StableInteractionId,
                        collider.bounds);
                    continue;
                }
                if (!WorldInteractionScreenTargeting.TryScore(
                        screenBounds,
                        screenPosition,
                        PropScreenPaddingPixels,
                        boundsDepth,
                        out float score))
                {
                    diagnostics.ScreenMiss++;
                    diagnostics.RecordScreenMiss(
                        interactable.StableInteractionId,
                        DistanceOutside(screenBounds, screenPosition),
                        screenBounds);
                    continue;
                }
                if (IsOccluded(
                        camera,
                        screenPosition,
                        localPlayer,
                        hitbox,
                        collider.bounds))
                {
                    diagnostics.Occluded++;
                    continue;
                }

                float hoverRange = interactable.MaxInteractionDistance > 0f
                    ? interactable.MaxInteractionDistance + HoverRangeSlackMeters
                    : 0f;
                float actorDistance =
                    Vector3.Distance(interactable.InteractionPoint, actorPosition);
                if (hoverRange > 0f && actorDistance > hoverRange)
                {
                    diagnostics.BeyondHoverRange++;
                    diagnostics.RecordRangeReject(
                        interactable.StableInteractionId,
                        actorDistance,
                        interactable.MaxInteractionDistance,
                        hoverRange,
                        actorPosition,
                        interactable.InteractionPoint);
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
                    diagnostics.Denied++;
                    diagnostics.RecordDenial(
                        interactable.StableInteractionId,
                        localDenialReason,
                        actorDistance);
                    continue;
                }

                diagnostics.Viable++;
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

            string previous = Describe(_hoveredHitbox);
            _hoveredHitbox?.SetHovered(false);
            _hoveredHitbox = hitbox;
            _hoveredHitbox?.SetHovered(true);
            Debug.Log(
                $"[WorldInteraction] hover {previous} -> {Describe(_hoveredHitbox)}; "
                + $"highlightSlots={_hoveredHitbox?.HighlightSlotCount ?? 0}.",
                this);
        }

        private void TraceRuntimeState(string state)
        {
            if (string.Equals(_lastRuntimeState, state, StringComparison.Ordinal))
                return;

            _lastRuntimeState = state;
            Debug.Log($"[WorldInteraction] router state: {state}.", this);
        }

        private static string Describe(WorldInteractionHitbox? hitbox)
        {
            if (hitbox == null)
                return "<none>";

            IWorldInteractable? interactable = hitbox.Interactable;
            return interactable == null
                ? $"'{hitbox.name}'(missing source)"
                : $"'{interactable.StableInteractionId}'";
        }

        private static string Format(Vector2 position)
            => $"({position.x:F0},{position.y:F0})";

        private static Vector3 ResolveLocalActorPosition(PlayerEntity localPlayer)
        {
            LocalPlayerStateProvider? stateProvider =
                localPlayer.GetLocalStateProvider();
            if (stateProvider != null && stateProvider.HasPredictedState)
                return stateProvider.PredictedPosition;

            return localPlayer.GameObject.transform.position;
        }

        private static float DistanceOutside(Rect bounds, Vector2 position)
        {
            float outsideX = Mathf.Max(
                bounds.xMin - position.x,
                position.x - bounds.xMax,
                0f);
            float outsideY = Mathf.Max(
                bounds.yMin - position.y,
                position.y - bounds.yMax,
                0f);
            return Mathf.Sqrt(outsideX * outsideX + outsideY * outsideY);
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

        private struct PropScanDiagnostics
        {
            public int Registered;
            public int Inactive;
            public int MissingCollider;
            public int MissingSource;
            public int Self;
            public int ProjectionFailed;
            public int ScreenMiss;
            public int Occluded;
            public int BeyondHoverRange;
            public int Denied;
            public int Viable;
            private string? _closestScreenMiss;
            private float _closestScreenMissPixels;
            private string? _projectionFailure;
            private string? _closestRangeReject;
            private float _closestRangeExcess;
            private string? _denial;

            public void RecordScreenMiss(
                string stableId,
                float distancePixels,
                Rect screenBounds)
            {
                if (_closestScreenMiss != null
                    && distancePixels >= _closestScreenMissPixels)
                {
                    return;
                }

                _closestScreenMissPixels = distancePixels;
                _closestScreenMiss =
                    $"closestScreenMiss=id:'{stableId}', outside:{distancePixels:F1}px, "
                    + $"bounds:({screenBounds.xMin:F0},{screenBounds.yMin:F0})"
                    + $"-({screenBounds.xMax:F0},{screenBounds.yMax:F0})";
            }

            public void RecordProjectionFailure(string stableId, Bounds bounds)
            {
                _projectionFailure ??=
                    $"projectionFailure=id:'{stableId}', center:{Format(bounds.center)}";
            }

            public void RecordRangeReject(
                string stableId,
                float actorDistance,
                float interactionRange,
                float hoverRange,
                Vector3 actorPosition,
                Vector3 interactionPoint)
            {
                float excess = actorDistance - hoverRange;
                if (_closestRangeReject != null && excess >= _closestRangeExcess)
                    return;

                _closestRangeExcess = excess;
                _closestRangeReject =
                    $"rangeReject=id:'{stableId}', distance:{actorDistance:F2}m, "
                    + $"interactionRange:{interactionRange:F2}m, hoverRange:{hoverRange:F2}m, "
                    + $"actor:{Format(actorPosition)}, anchor:{Format(interactionPoint)}";
            }

            public void RecordDenial(
                string stableId,
                string reason,
                float actorDistance)
            {
                _denial ??=
                    $"denial=id:'{stableId}', distance:{actorDistance:F2}m, "
                    + $"reason:'{reason}'";
            }

            public override readonly string ToString()
                => $"props registered={Registered}, inactive={Inactive}, "
                    + $"missingCollider={MissingCollider}, missingSource={MissingSource}, "
                    + $"self={Self}, projectionFailed={ProjectionFailed}, "
                    + $"screenMiss={ScreenMiss}, occluded={Occluded}, "
                    + $"beyondHoverRange={BeyondHoverRange}, denied={Denied}, "
                    + $"viable={Viable}; "
                    + $"{_closestRangeReject ?? "rangeReject:<none>"}; "
                    + $"{_closestScreenMiss ?? "closestScreenMiss:<none>"}; "
                    + $"{_projectionFailure ?? "projectionFailure:<none>"}; "
                    + $"{_denial ?? "denial:<none>"}";

            private static string Format(Vector3 position)
                => $"({position.x:F2},{position.y:F2},{position.z:F2})";
        }
    }
}
