#nullable enable

using System;
using System.Collections.Generic;
using UnityEngine;

namespace Arena.Interaction
{
    public interface IWorldInteractable
    {
        string StableInteractionId { get; }
        string InteractionVerb { get; }
        Vector3 InteractionPoint { get; }
        float MaxInteractionDistance { get; }

        bool CanInteractLocally(Vector3 actorPosition, out string denialReason);
        bool RequestInteraction();
    }

    public enum WorldInteractionCandidateKind
    {
        Prop = 0,
        CombatTarget = 1,
        CorpseLoot = 2,
    }

    public readonly struct WorldInteractionCandidate
    {
        public WorldInteractionCandidate(
            WorldInteractionCandidateKind kind,
            string stableId,
            string verb,
            Vector3 interactionPoint,
            float screenDepth,
            int priority,
            float maxInteractionDistance,
            Func<bool> dispatch)
        {
            Kind = kind;
            StableId = stableId ?? string.Empty;
            Verb = verb ?? string.Empty;
            InteractionPoint = interactionPoint;
            ScreenDepth = screenDepth;
            Priority = priority;
            MaxInteractionDistance = maxInteractionDistance;
            Dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
        }

        public WorldInteractionCandidateKind Kind { get; }
        public string StableId { get; }
        public string Verb { get; }
        public Vector3 InteractionPoint { get; }
        public float ScreenDepth { get; }
        public int Priority { get; }
        public float MaxInteractionDistance { get; }
        public Func<bool> Dispatch { get; }

        public bool IsWithinRange(Vector3 actorPosition)
        {
            if (MaxInteractionDistance <= 0f)
                return true;

            float maxDistanceSq = MaxInteractionDistance * MaxInteractionDistance;
            return (InteractionPoint - actorPosition).sqrMagnitude <= maxDistanceSq;
        }
    }

    public static class WorldInteractionArbitration
    {
        public const int PropPriority = 100;
        public const int CombatTargetPriority = 200;
        public const int CorpseLootPriority = 300;

        private const float DepthTieThreshold = 0.25f;

        public static bool TrySelectBest(
            IReadOnlyList<WorldInteractionCandidate> candidates,
            Vector3 actorPosition,
            out WorldInteractionCandidate selected)
        {
            selected = default;
            bool hasSelection = false;

            for (int i = 0; i < candidates.Count; i++)
            {
                WorldInteractionCandidate candidate = candidates[i];
                if (!candidate.IsWithinRange(actorPosition)
                    || !float.IsFinite(candidate.ScreenDepth)
                    || candidate.ScreenDepth < 0f)
                {
                    continue;
                }

                if (!hasSelection || IsBetter(candidate, selected))
                {
                    selected = candidate;
                    hasSelection = true;
                }
            }

            return hasSelection;
        }

        public static bool TryDispatchBest(
            IReadOnlyList<WorldInteractionCandidate> candidates,
            Vector3 actorPosition)
        {
            return TrySelectBest(candidates, actorPosition, out WorldInteractionCandidate selected)
                && selected.Dispatch();
        }

        private static bool IsBetter(
            WorldInteractionCandidate candidate,
            WorldInteractionCandidate current)
        {
            float depthDelta = candidate.ScreenDepth - current.ScreenDepth;
            if (Mathf.Abs(depthDelta) > DepthTieThreshold)
                return depthDelta < 0f;
            if (candidate.Priority != current.Priority)
                return candidate.Priority > current.Priority;
            if (!Mathf.Approximately(candidate.ScreenDepth, current.ScreenDepth))
                return candidate.ScreenDepth < current.ScreenDepth;

            return string.CompareOrdinal(candidate.StableId, current.StableId) < 0;
        }
    }

    [DisallowMultipleComponent]
    public sealed class WorldInteractionHitbox : MonoBehaviour
    {
        [SerializeField] private MonoBehaviour? _interactableSource;

        public IWorldInteractable? Interactable => _interactableSource as IWorldInteractable;

        public void Configure(MonoBehaviour interactableSource)
        {
            _interactableSource = interactableSource;
        }

        private void Reset()
        {
            ResolveFromParents();
        }

        private void OnValidate()
        {
            if (_interactableSource != null && _interactableSource is not IWorldInteractable)
            {
                Debug.LogError(
                    $"{name}: assigned source must implement {nameof(IWorldInteractable)}.",
                    this);
            }

            if (_interactableSource == null)
                ResolveFromParents();
        }

        private void ResolveFromParents()
        {
            MonoBehaviour[] behaviours = GetComponentsInParent<MonoBehaviour>(true);
            for (int i = 0; i < behaviours.Length; i++)
            {
                if (behaviours[i] is IWorldInteractable)
                {
                    _interactableSource = behaviours[i];
                    return;
                }
            }
        }
    }
}
