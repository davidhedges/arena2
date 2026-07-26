#nullable enable

using UnityEngine;

namespace Arena.Interaction
{
    public enum WorldPointerGestureResult
    {
        None = 0,
        Click = 1,
        Drag = 2,
        Consumed = 3,
    }

    public sealed class WorldPointerGestureClassifier
    {
        private readonly float _maxClickDuration;
        private readonly float _maxClickDistanceSq;

        private bool _active;
        private bool _consumed;
        private float _pressedAt;
        private Vector2 _pressedPosition;
        private float _maxDistanceSq;

        public WorldPointerGestureClassifier(float maxClickDuration, float maxClickDistancePixels)
        {
            _maxClickDuration = Mathf.Max(0f, maxClickDuration);
            float distance = Mathf.Max(0f, maxClickDistancePixels);
            _maxClickDistanceSq = distance * distance;
        }

        public bool IsActive => _active;

        public void Begin(Vector2 pointerPosition, float unscaledTime, bool consumed)
        {
            _active = true;
            _consumed = consumed;
            _pressedAt = unscaledTime;
            _pressedPosition = pointerPosition;
            _maxDistanceSq = 0f;
        }

        public void Track(Vector2 pointerPosition)
        {
            if (!_active)
                return;

            _maxDistanceSq = Mathf.Max(
                _maxDistanceSq,
                (pointerPosition - _pressedPosition).sqrMagnitude);
        }

        public void Consume()
        {
            if (_active)
                _consumed = true;
        }

        public void Cancel()
        {
            _active = false;
            _consumed = false;
            _maxDistanceSq = 0f;
        }

        public WorldPointerGestureResult Release(
            Vector2 pointerPosition,
            float unscaledTime,
            bool pointerBlocked)
        {
            if (!_active)
                return WorldPointerGestureResult.None;

            Track(pointerPosition);
            float duration = Mathf.Max(0f, unscaledTime - _pressedAt);
            bool consumed = _consumed || pointerBlocked;
            bool dragged = duration > _maxClickDuration || _maxDistanceSq > _maxClickDistanceSq;
            Cancel();

            if (consumed)
                return WorldPointerGestureResult.Consumed;
            return dragged ? WorldPointerGestureResult.Drag : WorldPointerGestureResult.Click;
        }
    }
}
