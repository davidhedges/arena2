#nullable enable

using UnityEngine;
using UnityEngine.EventSystems;

namespace Arena.Presentation
{
    [DisallowMultipleComponent]
    public sealed class HubShowcaseRotator : MonoBehaviour, IDragHandler
    {
        [SerializeField] private Transform? _target;
        [SerializeField] private float _degreesPerPixel = 0.4f;

        public void Configure(Transform target, float degreesPerPixel = 0.4f)
        {
            _target = target;
            _degreesPerPixel = Mathf.Max(0f, degreesPerPixel);
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (_target == null)
                return;

            _target.Rotate(Vector3.up, -eventData.delta.x * _degreesPerPixel, Space.World);
        }
    }
}
