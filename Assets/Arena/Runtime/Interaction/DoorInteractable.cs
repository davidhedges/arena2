#nullable enable

using System;
using System.Collections.Generic;
using Arena.Input;
using UnityEngine;

namespace Arena.Interaction
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(DoorAuthoring), typeof(DoorMotor))]
    public sealed class DoorInteractable :
        MonoBehaviour,
        IWorldInteractable,
        IWorldInteractionHighlightSource
    {
        [SerializeField] private DoorAuthoring? _authoring;
        [SerializeField] private DoorMotor? _motor;

        private bool _knownOpen;
        private ulong _knownRevision;

        public string StableInteractionId => _authoring?.DoorDefinitionId ?? string.Empty;
        public string InteractionVerb => _knownOpen ? "CLOSE" : "OPEN";
        public Vector3 InteractionPoint => _authoring?.InteractionPoint ?? transform.position;
        public float MaxInteractionDistance => _authoring?.MaxInteractionDistance ?? 0f;
        public bool KnownOpen => _knownOpen;
        public ulong KnownRevision => _knownRevision;

        public void Configure(DoorAuthoring authoring, DoorMotor motor)
        {
            _authoring = authoring;
            _motor = motor;
            _knownOpen = authoring.DefaultOpen;
        }

        private void Awake()
        {
            _authoring ??= GetComponent<DoorAuthoring>();
            _motor ??= GetComponent<DoorMotor>();
            _knownOpen = _authoring?.DefaultOpen ?? true;
            Debug.Log(
                $"[WorldInteraction] door awake object='{name}' "
                + $"id='{StableInteractionId}' authoring={_authoring != null} "
                + $"motor={_motor != null} defaultOpen={_knownOpen}.",
                this);
        }

        private void OnEnable()
        {
            DoorRuntimeRegistry.Register(this);
            Debug.Log(
                $"[WorldInteraction] door enabled id='{StableInteractionId}' "
                + $"production={_authoring?.ProductionEnabled == true} "
                + $"maxRange={MaxInteractionDistance:F2}.",
                this);
            if (WorldDoorCollisionRuntime.TryGetEffectiveState(
                    StableInteractionId,
                    out bool open,
                    out ulong revision))
            {
                ApplyAuthoritativeState(open, revision, animate: false);
            }
        }

        private void OnDisable()
        {
            DoorRuntimeRegistry.Unregister(this);
        }

        public bool CanInteractLocally(Vector3 actorPosition, out string denialReason)
        {
            if (_authoring == null || !_authoring.ProductionEnabled)
            {
                denialReason = "Interaction is not enabled for this door.";
                return false;
            }
            if (string.IsNullOrWhiteSpace(_authoring.DoorDefinitionId))
            {
                denialReason = "Door has no stable definition ID.";
                return false;
            }
            if ((InteractionPoint - actorPosition).sqrMagnitude
                > MaxInteractionDistance * MaxInteractionDistance)
            {
                denialReason = "Too far away.";
                return false;
            }

            denialReason = string.Empty;
            return true;
        }

        public bool RequestInteraction()
        {
            bool desiredOpen = !_knownOpen;
            bool accepted = _authoring != null
                && _authoring.ProductionEnabled
                && DoorInteractionRequests.Sink?.RequestDoorState(
                    this,
                    desiredOpen,
                    _knownRevision) == true;
            Debug.Log(
                $"[WorldInteraction] door request id='{StableInteractionId}' "
                + $"desiredOpen={desiredOpen} observedRevision={_knownRevision} "
                + $"production={_authoring?.ProductionEnabled == true} "
                + $"sink={DoorInteractionRequests.Sink?.GetType().Name ?? "<null>"} "
                + $"accepted={accepted}.",
                this);
            return accepted;
        }

        public void ApplyAuthoritativeState(bool open, ulong revision, bool animate)
        {
            if (revision < _knownRevision)
            {
                Debug.Log(
                    $"[WorldInteraction] door state ignored id='{StableInteractionId}' "
                    + $"open={open} revision={revision} knownRevision={_knownRevision}.",
                    this);
                return;
            }

            bool previousOpen = _knownOpen;
            ulong previousRevision = _knownRevision;
            _knownOpen = open;
            _knownRevision = revision;
            Debug.Log(
                $"[WorldInteraction] door state apply id='{StableInteractionId}' "
                + $"open={previousOpen}->{open} revision={previousRevision}->{revision} "
                + $"animate={animate} motor={_motor != null}.",
                this);
            _motor?.ApplyAuthoritativeState(open, revision, animate);
        }

        public void ResetToAuthoredDefault()
        {
            bool defaultOpen = _authoring?.DefaultOpen ?? true;
            _knownOpen = defaultOpen;
            _knownRevision = 0UL;
            _motor?.SnapToState(defaultOpen, 0UL);
        }

        public void CollectHighlightRenderers(List<Renderer> renderers)
        {
            if (renderers == null)
                throw new ArgumentNullException(nameof(renderers));

            _authoring ??= GetComponent<DoorAuthoring>();
            if (_authoring == null)
                return;

            var seen = new HashSet<Renderer>();
            foreach (DoorAuthoring.LeafPose pose in _authoring.Leaves)
            {
                Transform? leaf = pose.Leaf;
                if (leaf == null)
                    continue;

                foreach (Renderer renderer in leaf.GetComponentsInChildren<Renderer>(true))
                {
                    if (renderer != null && seen.Add(renderer))
                        renderers.Add(renderer);
                }
            }
        }
    }
}
