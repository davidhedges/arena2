#nullable enable

using System;
using System.Collections.Generic;
using Arena.Input;
using UnityEngine;

namespace Arena.Interaction
{
    [DisallowMultipleComponent]
    public sealed class DoorAuthoring : MonoBehaviour
    {
        [Serializable]
        public sealed class LeafPose
        {
            [SerializeField] private Transform? _leaf;
            [SerializeField] private Quaternion _closedLocalRotation = Quaternion.identity;
            [SerializeField] private Quaternion _openLocalRotation = Quaternion.identity;

            public Transform? Leaf => _leaf;
            public Quaternion ClosedLocalRotation => _closedLocalRotation;
            public Quaternion OpenLocalRotation => _openLocalRotation;

            public LeafPose(
                Transform leaf,
                Quaternion closedLocalRotation,
                Quaternion openLocalRotation)
            {
                _leaf = leaf;
                _closedLocalRotation = closedLocalRotation;
                _openLocalRotation = openLocalRotation;
            }
        }

        [SerializeField] private string _doorDefinitionId = string.Empty;
        [SerializeField] private string _worldDefinitionKey = "RANDOM_DUNGEON";
        [SerializeField] private bool _templateOnly;
        [SerializeField] private bool _productionEnabled;
        [SerializeField] private bool _defaultOpen = true;
        [SerializeField, Min(1)] private int _definitionVersion = 1;
        [SerializeField] private string _openInteractionProfileId = "WORLD_DOOR_INSTANT";
        [SerializeField] private string _closeInteractionProfileId = "WORLD_DOOR_INSTANT";
        [SerializeField] private Vector3 _interactionAnchorLocal = new(0f, 1.25f, 0f);
        [SerializeField, Min(0.1f)] private float _maxInteractionDistance = 3f;
        [SerializeField] private Vector3 _closedBlockerCenterLocal = new(0f, 1.5f, 0f);
        [SerializeField] private Vector3 _closedBlockerSize = new(3f, 3f, 0.35f);
        [SerializeField] private float _closedBlockerLocalYaw;
        [SerializeField] private LeafPose[] _leaves = Array.Empty<LeafPose>();

        public string DoorDefinitionId => NormalizeId(_doorDefinitionId);
        public string WorldDefinitionKey => NormalizeId(_worldDefinitionKey);
        public bool TemplateOnly => _templateOnly;
        public bool ProductionEnabled => _productionEnabled;
        public bool DefaultOpen => _defaultOpen;
        public int DefinitionVersion => Mathf.Max(1, _definitionVersion);
        public string OpenInteractionProfileId => NormalizeId(_openInteractionProfileId);
        public string CloseInteractionProfileId => NormalizeId(_closeInteractionProfileId);
        public Vector3 InteractionPoint => transform.TransformPoint(_interactionAnchorLocal);
        public float MaxInteractionDistance => Mathf.Max(0.1f, _maxInteractionDistance);
        public Vector3 ClosedBlockerCenter => transform.TransformPoint(_closedBlockerCenterLocal);
        public Vector3 ClosedBlockerSize => Vector3.Scale(
            Abs(_closedBlockerSize),
            Abs(transform.lossyScale));
        public float ClosedBlockerYaw =>
            Mathf.Repeat(transform.eulerAngles.y + _closedBlockerLocalYaw, 360f);
        public LeafPose[] Leaves => _leaves;

        public void Configure(
            string doorDefinitionId,
            string worldDefinitionKey,
            bool templateOnly,
            bool productionEnabled,
            bool defaultOpen,
            int definitionVersion,
            string openInteractionProfileId,
            string closeInteractionProfileId,
            Vector3 interactionAnchorLocal,
            float maxInteractionDistance,
            Vector3 closedBlockerCenterLocal,
            Vector3 closedBlockerSize,
            float closedBlockerLocalYaw,
            LeafPose[] leaves)
        {
            _doorDefinitionId = NormalizeId(doorDefinitionId);
            _worldDefinitionKey = NormalizeId(worldDefinitionKey);
            _templateOnly = templateOnly;
            _productionEnabled = productionEnabled;
            _defaultOpen = defaultOpen;
            _definitionVersion = Mathf.Max(1, definitionVersion);
            _openInteractionProfileId = NormalizeId(openInteractionProfileId);
            _closeInteractionProfileId = NormalizeId(closeInteractionProfileId);
            _interactionAnchorLocal = interactionAnchorLocal;
            _maxInteractionDistance = Mathf.Max(0.1f, maxInteractionDistance);
            _closedBlockerCenterLocal = closedBlockerCenterLocal;
            _closedBlockerSize = Abs(closedBlockerSize);
            _closedBlockerLocalYaw = Mathf.Repeat(closedBlockerLocalYaw, 360f);
            _leaves = leaves ?? Array.Empty<LeafPose>();
        }

        public void SetProductionEnabled(bool enabled)
        {
            _productionEnabled = enabled;
        }

        private void OnValidate()
        {
            _doorDefinitionId = NormalizeId(_doorDefinitionId);
            _worldDefinitionKey = NormalizeId(_worldDefinitionKey);
            _openInteractionProfileId = NormalizeId(_openInteractionProfileId);
            _closeInteractionProfileId = NormalizeId(_closeInteractionProfileId);
            _definitionVersion = Mathf.Max(1, _definitionVersion);
            _maxInteractionDistance = Mathf.Max(0.1f, _maxInteractionDistance);
            _closedBlockerSize = Abs(_closedBlockerSize);
            _leaves ??= Array.Empty<LeafPose>();
        }

        private void OnDrawGizmosSelected()
        {
            Matrix4x4 previous = Gizmos.matrix;
            Gizmos.color = new Color(0.9f, 0.15f, 0.05f, 0.28f);
            Gizmos.matrix = Matrix4x4.TRS(
                ClosedBlockerCenter,
                Quaternion.Euler(0f, ClosedBlockerYaw, 0f),
                Vector3.one);
            Gizmos.DrawCube(Vector3.zero, ClosedBlockerSize);
            Gizmos.color = Color.yellow;
            Gizmos.matrix = Matrix4x4.identity;
            Gizmos.DrawWireSphere(InteractionPoint, 0.12f);
            Gizmos.matrix = previous;
        }

        private static Vector3 Abs(Vector3 value)
            => new(Mathf.Abs(value.x), Mathf.Abs(value.y), Mathf.Abs(value.z));

        private static string NormalizeId(string value)
            => string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : value.Trim().ToUpperInvariant();
    }

    [DisallowMultipleComponent]
    [RequireComponent(typeof(DoorAuthoring))]
    public sealed class DoorMotor : MonoBehaviour
    {
        [SerializeField] private DoorAuthoring? _authoring;
        [SerializeField, Min(0.01f)] private float _swingDurationSeconds = 0.55f;
        [SerializeField] private AnimationCurve _easing =
            AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

        private Quaternion[] _startRotations = Array.Empty<Quaternion>();
        private Quaternion[] _targetRotations = Array.Empty<Quaternion>();
        private float _transitionStartedAt;
        private ulong _appliedRevision;
        private bool _targetOpen;
        private bool _moving;

        public bool TargetOpen => _targetOpen;
        public ulong AppliedRevision => _appliedRevision;
        public bool IsMoving => _moving;

        public void Configure(DoorAuthoring authoring)
        {
            _authoring = authoring;
            EnsureBuffers();
        }

        private void Awake()
        {
            _authoring ??= GetComponent<DoorAuthoring>();
            EnsureBuffers();
            if (_authoring != null)
                SnapToState(_authoring.DefaultOpen, 0);
        }

        private void Update()
        {
            if (!_moving || _authoring == null)
                return;

            float duration = Mathf.Max(0.01f, _swingDurationSeconds);
            float t = Mathf.Clamp01((Time.time - _transitionStartedAt) / duration);
            float eased = _easing == null ? t : _easing.Evaluate(t);
            DoorAuthoring.LeafPose[] leaves = _authoring.Leaves;
            for (int i = 0; i < leaves.Length; i++)
            {
                Transform? leaf = leaves[i].Leaf;
                if (leaf != null)
                    leaf.localRotation = Quaternion.Slerp(_startRotations[i], _targetRotations[i], eased);
            }

            _moving = t < 1f;
        }

        public void ApplyAuthoritativeState(bool open, ulong revision, bool animate)
        {
            if (_authoring == null || revision < _appliedRevision)
                return;
            if (revision == _appliedRevision && open == _targetOpen)
                return;

            if (!animate)
            {
                SnapToState(open, revision);
                return;
            }

            EnsureBuffers();
            DoorAuthoring.LeafPose[] leaves = _authoring.Leaves;
            for (int i = 0; i < leaves.Length; i++)
            {
                Transform? leaf = leaves[i].Leaf;
                _startRotations[i] = leaf != null ? leaf.localRotation : Quaternion.identity;
                _targetRotations[i] = open
                    ? leaves[i].OpenLocalRotation
                    : leaves[i].ClosedLocalRotation;
            }

            _targetOpen = open;
            _appliedRevision = revision;
            _transitionStartedAt = Time.time;
            _moving = true;
        }

        public void SnapToState(bool open, ulong revision)
        {
            if (_authoring == null)
                return;

            EnsureBuffers();
            DoorAuthoring.LeafPose[] leaves = _authoring.Leaves;
            for (int i = 0; i < leaves.Length; i++)
            {
                Quaternion rotation = open
                    ? leaves[i].OpenLocalRotation
                    : leaves[i].ClosedLocalRotation;
                if (leaves[i].Leaf != null)
                    leaves[i].Leaf!.localRotation = rotation;
                _startRotations[i] = rotation;
                _targetRotations[i] = rotation;
            }

            _targetOpen = open;
            _appliedRevision = revision;
            _moving = false;
        }

        private void EnsureBuffers()
        {
            int count = _authoring?.Leaves.Length ?? 0;
            if (_startRotations.Length == count)
                return;

            _startRotations = new Quaternion[count];
            _targetRotations = new Quaternion[count];
        }
    }

    public interface IDoorInteractionRequestSink
    {
        bool RequestDoorState(DoorInteractable door, bool desiredOpen, ulong observedRevision);
    }

    public static class DoorInteractionRequests
    {
        public static IDoorInteractionRequestSink? Sink { get; set; }
    }

    /// <summary>
    /// Stable-ID lookup for scene-authored door presentations. Replication
    /// updates this registry; gameplay collision remains in
    /// <see cref="WorldDoorCollisionRuntime"/>.
    /// </summary>
    public static class DoorRuntimeRegistry
    {
        private static readonly Dictionary<string, List<DoorInteractable>> Doors =
            new(StringComparer.Ordinal);

        public static void Register(DoorInteractable door)
        {
            string id = door.StableInteractionId;
            if (string.IsNullOrWhiteSpace(id))
                return;

            if (!Doors.TryGetValue(id, out List<DoorInteractable>? instances))
            {
                instances = new List<DoorInteractable>();
                Doors.Add(id, instances);
            }
            if (!instances.Contains(door))
                instances.Add(door);
        }

        public static void Unregister(DoorInteractable door)
        {
            string id = door.StableInteractionId;
            if (string.IsNullOrWhiteSpace(id)
                || !Doors.TryGetValue(id, out List<DoorInteractable>? instances))
            {
                return;
            }

            instances.Remove(door);
            if (instances.Count == 0)
                Doors.Remove(id);
        }

        public static void Apply(
            string doorDefinitionId,
            bool open,
            ulong revision,
            bool animate)
        {
            string id = NormalizeId(doorDefinitionId);
            if (!Doors.TryGetValue(id, out List<DoorInteractable>? instances))
                return;

            for (int i = instances.Count - 1; i >= 0; i--)
            {
                DoorInteractable? door = instances[i];
                if (door == null)
                {
                    instances.RemoveAt(i);
                    continue;
                }
                door.ApplyAuthoritativeState(open, revision, animate);
            }

            if (instances.Count == 0)
                Doors.Remove(id);
        }

        public static void ResetToAuthoredDefaults()
        {
            foreach (List<DoorInteractable> instances in Doors.Values)
            {
                for (int i = instances.Count - 1; i >= 0; i--)
                {
                    DoorInteractable? door = instances[i];
                    if (door == null)
                        instances.RemoveAt(i);
                    else
                        door.ResetToAuthoredDefault();
                }
            }
        }

        public static void ResetToAuthoredDefault(string doorDefinitionId)
        {
            string id = NormalizeId(doorDefinitionId);
            if (!Doors.TryGetValue(id, out List<DoorInteractable>? instances))
                return;

            for (int i = instances.Count - 1; i >= 0; i--)
            {
                DoorInteractable? door = instances[i];
                if (door == null)
                    instances.RemoveAt(i);
                else
                    door.ResetToAuthoredDefault();
            }
        }

        internal static void ClearForTests() => Doors.Clear();

        private static string NormalizeId(string value)
            => string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : value.Trim().ToUpperInvariant();
    }

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
        }

        private void OnEnable()
        {
            DoorRuntimeRegistry.Register(this);
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
            return _authoring != null
                && _authoring.ProductionEnabled
                && DoorInteractionRequests.Sink?.RequestDoorState(
                    this,
                    !_knownOpen,
                    _knownRevision) == true;
        }

        public void ApplyAuthoritativeState(bool open, ulong revision, bool animate)
        {
            if (revision < _knownRevision)
                return;

            _knownOpen = open;
            _knownRevision = revision;
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
