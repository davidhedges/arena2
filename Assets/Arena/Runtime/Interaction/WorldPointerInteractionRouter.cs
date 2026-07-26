#nullable enable

using System;
using System.Collections.Generic;
using Arena.Combat;
using Arena.Entity;
using Arena.Input;
using Arena.UI;
using UnityEngine;

namespace Arena.Interaction
{
    [DefaultExecutionOrder(-900)]
    public sealed class WorldPointerInteractionRouter : MonoBehaviour
    {
        private const float MaxClickDurationSeconds = 0.28f;
        private const float MaxClickDistancePixels = 8f;
        private const int RaycastCapacity = 64;

        private static readonly RaycastHit[] RaycastHits = new RaycastHit[RaycastCapacity];
        private static readonly RaycastHitDistanceComparer HitComparer = new();

        private readonly List<WorldInteractionCandidate> _candidates = new(3);
        private WorldPointerGestureClassifier _gesture = null!;

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
            if (Instance == this)
                Instance = null;
        }

        private void Update()
        {
            if (!ArenaRuntimeSceneGate.ShouldRunArenaRuntimeInActiveScene())
            {
                _gesture.Cancel();
                return;
            }

            PlayerEntity? localPlayer = EntityRegistry.Instance?.LocalPlayerEntity;
            LocalPlayerInputSource? input = localPlayer?.GetLocalInputSource();
            if (localPlayer == null || input == null)
            {
                _gesture.Cancel();
                return;
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
            if (TryGetPropCandidate(camera, screenPosition, localPlayer, out WorldInteractionCandidate prop))
                _candidates.Add(prop);

            WorldInteractionArbitration.TryDispatchBest(
                _candidates,
                localPlayer.GetRenderPosition());
        }

        private static bool TryGetPropCandidate(
            Camera camera,
            Vector2 screenPosition,
            PlayerEntity localPlayer,
            out WorldInteractionCandidate candidate)
        {
            candidate = default;
            Ray ray = camera.ScreenPointToRay(screenPosition);
            int hitCount = Physics.RaycastNonAlloc(
                ray,
                RaycastHits,
                Mathf.Infinity,
                Physics.DefaultRaycastLayers,
                QueryTriggerInteraction.Collide);
            if (hitCount <= 0)
                return false;

            Array.Sort(RaycastHits, 0, hitCount, HitComparer);
            for (int i = 0; i < hitCount; i++)
            {
                RaycastHit hit = RaycastHits[i];
                Collider collider = hit.collider;
                if (collider == null)
                    continue;
                if (collider.transform.IsChildOf(localPlayer.GameObject.transform))
                    continue;

                WorldInteractionHitbox? hitbox = collider.GetComponentInParent<WorldInteractionHitbox>();
                IWorldInteractable? interactable = hitbox?.Interactable;
                if (interactable != null)
                {
                    Vector3 actorPosition = localPlayer.GetRenderPosition();
                    if (!interactable.CanInteractLocally(actorPosition, out _))
                        return false;

                    Vector3 interactionPoint = interactable.InteractionPoint;
                    float screenDepth = camera.WorldToScreenPoint(interactionPoint).z;
                    candidate = new WorldInteractionCandidate(
                        WorldInteractionCandidateKind.Prop,
                        interactable.StableInteractionId,
                        interactable.InteractionVerb,
                        interactionPoint,
                        screenDepth,
                        WorldInteractionArbitration.PropPriority,
                        interactable.MaxInteractionDistance,
                        interactable.RequestInteraction);
                    return true;
                }

                if (!collider.isTrigger)
                    return false;
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
