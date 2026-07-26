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

}
