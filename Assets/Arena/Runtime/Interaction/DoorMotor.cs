#nullable enable

using System;
using UnityEngine;

namespace Arena.Interaction
{
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
            {
                Debug.Log(
                    $"[WorldInteraction] motor state ignored object='{name}' "
                    + $"authoring={_authoring != null} open={open} revision={revision} "
                    + $"appliedRevision={_appliedRevision}.",
                    this);
                return;
            }
            if (revision == _appliedRevision && open == _targetOpen)
            {
                Debug.Log(
                    $"[WorldInteraction] motor state unchanged object='{name}' "
                    + $"open={open} revision={revision}.",
                    this);
                return;
            }

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
            Debug.Log(
                $"[WorldInteraction] motor animation started object='{name}' "
                + $"open={open} revision={revision} leaves={leaves.Length} "
                + $"duration={_swingDurationSeconds:F2}.",
                this);
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
            Debug.Log(
                $"[WorldInteraction] motor snapped object='{name}' "
                + $"open={open} revision={revision} leaves={leaves.Length}.",
                this);
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
}
