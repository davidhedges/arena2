#nullable enable

using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Arena.UI
{
    /// <summary>
    /// Fades a border-ring glow in on pointer hover and out on exit.
    /// Purely decorative; never blocks raycasts or input.
    /// </summary>
    internal sealed class ArenaHoverGlow : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        private const float FadeSpeed = 14f;

        private Image? _glow;
        private float _maxAlpha;
        private float _target;
        private float _current;

        public void Bind(Image glow, float maxAlpha)
        {
            _glow = glow;
            _maxAlpha = maxAlpha;
            _current = 0f;
            _target = 0f;
            Apply();
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            if (TryGetComponent(out Selectable selectable) && !selectable.interactable)
                return;

            _target = _maxAlpha;
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            _target = 0f;
        }

        private void OnDisable()
        {
            _target = 0f;
            _current = 0f;
            Apply();
        }

        private void Update()
        {
            if (_glow == null || Mathf.Approximately(_current, _target))
                return;

            _current = Mathf.MoveTowards(_current, _target, Time.unscaledDeltaTime * FadeSpeed * _maxAlpha);
            Apply();
        }

        private void Apply()
        {
            if (_glow == null)
                return;

            Color color = _glow.color;
            color.a = _current;
            _glow.color = color;
        }
    }
}
