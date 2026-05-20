#nullable enable

using Arena.Combat;
using UnityEngine;
using UnityEngine.EventSystems;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif
using UnityEngine.UI;

namespace Arena.UI
{
    public sealed class TooltipTarget : MonoBehaviour,
        IPointerEnterHandler,
        IPointerExitHandler,
        IPointerMoveHandler
    {
        private Canvas? _canvas;
        private bool _pollHover;
        private bool _hovering;

        public TooltipData Tooltip { get; private set; }

        public void Configure(Canvas canvas, TooltipData tooltip, bool pollHover = false)
        {
            TooltipPresenter.EnsureEventSystem();
            _canvas = canvas;
            _pollHover = pollHover;
            Tooltip = tooltip;

            if (!tooltip.IsValid)
            {
                _hovering = false;
                TooltipPresenter.Hide(this);
            }
            else
            {
                TooltipPresenter.Refresh(this, tooltip);
            }
        }

        private void Update()
        {
            if (!_pollHover || _canvas == null || !Tooltip.IsValid)
                return;

            RectTransform? rect = transform as RectTransform;
            if (rect == null)
                return;

            Vector2 pointerPosition = ReadPointerPosition();
            Camera? camera = _canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : _canvas.worldCamera;
            bool contains = RectTransformUtility.RectangleContainsScreenPoint(rect, pointerPosition, camera);

            if (contains)
            {
                if (!_hovering)
                {
                    _hovering = true;
                    TooltipPresenter.Show(_canvas, this, Tooltip, pointerPosition);
                }
                else
                {
                    TooltipPresenter.Move(_canvas, this, pointerPosition);
                }

                return;
            }

            if (!_hovering)
                return;

            _hovering = false;
            TooltipPresenter.Hide(this);
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            if (_canvas == null || !Tooltip.IsValid)
                return;

            _hovering = true;
            TooltipPresenter.Show(_canvas, this, Tooltip, eventData.position);
        }

        public void OnPointerMove(PointerEventData eventData)
        {
            if (_canvas == null || !Tooltip.IsValid)
                return;

            TooltipPresenter.Move(_canvas, this, eventData.position);
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            _hovering = false;
            TooltipPresenter.Hide(this);
        }

        public void OnDisable()
        {
            _hovering = false;
            TooltipPresenter.Hide(this);
        }

        private static Vector2 ReadPointerPosition()
        {
#if ENABLE_INPUT_SYSTEM
            if (Mouse.current != null)
                return Mouse.current.position.ReadValue();
#endif
            return UnityEngine.Input.mousePosition;
        }
    }

    public static class TooltipPresenter
    {
        internal const float Width = 260f;
        internal const float BaseHeight = 76f;
        internal const float DescriptionHeight = 52f;
        internal const float BorderThickness = 2f;
        private static readonly Vector2 CursorOffset = new(18f, 18f);

        private static TooltipView? _view;
        private static TooltipTarget? _owner;

        public static void Show(
            Canvas canvas,
            TooltipTarget owner,
            TooltipData data,
            Vector2 screenPosition)
        {
            if (!data.IsValid)
            {
                Hide(owner);
                return;
            }

            EnsureEventSystem();
            TooltipView view = ResolveView(canvas);
            _owner = owner;
            view.Set(data);
            view.SetVisible(true);
            Move(canvas, owner, screenPosition);
        }

        public static void Move(Canvas canvas, TooltipTarget owner, Vector2 screenPosition)
        {
            if (_view == null || _owner != owner)
                return;

            _view.Place(canvas, screenPosition + CursorOffset);
        }

        public static void Hide(TooltipTarget owner)
        {
            if (_owner != owner)
                return;

            _owner = null;
            _view?.SetVisible(false);
        }

        public static void Refresh(TooltipTarget owner, TooltipData data)
        {
            if (_owner != owner || _view == null || !data.IsValid)
                return;

            _view.Set(data);
        }

        private static TooltipView ResolveView(Canvas canvas)
        {
            if (_view != null && _view.Canvas == canvas)
                return _view;

            if (_view != null)
                UnityEngine.Object.Destroy(_view.gameObject);

            GameObject go = new("Tooltip");
            go.transform.SetParent(canvas.transform, false);
            _view = go.AddComponent<TooltipView>();
            _view.Initialize(canvas);
            return _view;
        }

        public static void EnsureEventSystem()
        {
            RuntimeUiEventSystem.Ensure();
        }
    }

    internal sealed class TooltipView : MonoBehaviour
    {
        private RectTransform _rect = null!;
        private RectTransform _content = null!;
        private Text _name = null!;
        private Text _subtitle = null!;
        private Text _description = null!;

        public Canvas Canvas { get; private set; } = null!;

        public void Initialize(Canvas canvas)
        {
            Canvas = canvas;
            _rect = gameObject.AddComponent<RectTransform>();
            _rect.anchorMin = new Vector2(0.5f, 0.5f);
            _rect.anchorMax = new Vector2(0.5f, 0.5f);
            _rect.pivot = new Vector2(0f, 1f);
            _rect.sizeDelta = OuterSize(TooltipPresenter.BaseHeight);

            Image border = gameObject.AddComponent<Image>();
            border.color = new Color(1f, 0.82f, 0.38f, 0.58f);
            border.raycastTarget = false;

            _content = AddContentPanel(new Color(0.035f, 0.035f, 0.04f, 0.96f));

            _name = MakeText("Name", _content, 16, FontStyle.Bold, new Color(1f, 0.86f, 0.45f));
            SetRect(_name.rectTransform, new Vector2(12f, -10f), new Vector2(TooltipPresenter.Width - 24f, 24f));

            _subtitle = MakeText("Subtitle", _content, 13, FontStyle.Normal, new Color(0.82f, 0.88f, 1f));
            SetRect(_subtitle.rectTransform, new Vector2(12f, -38f), new Vector2(TooltipPresenter.Width - 24f, 20f));

            _description = MakeText("Description", _content, 12, FontStyle.Normal, new Color(0.82f, 0.82f, 0.82f));
            _description.horizontalOverflow = HorizontalWrapMode.Wrap;
            _description.verticalOverflow = VerticalWrapMode.Truncate;
            SetRect(_description.rectTransform, new Vector2(12f, -66f), new Vector2(TooltipPresenter.Width - 24f, 48f));

            SetVisible(false);
        }

        public void Set(TooltipData data)
        {
            _name.text = data.Name;
            _subtitle.text = data.Subtitle;
            bool hasDescription = !string.IsNullOrWhiteSpace(data.Description);
            _description.gameObject.SetActive(hasDescription);
            _description.text = hasDescription ? data.Description : string.Empty;
            float contentHeight = hasDescription
                ? TooltipPresenter.BaseHeight + TooltipPresenter.DescriptionHeight
                : TooltipPresenter.BaseHeight;
            _rect.sizeDelta = OuterSize(contentHeight);
            transform.SetAsLastSibling();
        }

        public void Place(Canvas canvas, Vector2 screenPosition)
        {
            RectTransform canvasRect = (RectTransform)canvas.transform;
            Camera? camera = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                canvasRect,
                screenPosition,
                camera,
                out Vector2 local);

            Vector2 size = _rect.sizeDelta;
            Rect bounds = canvasRect.rect;
            local.x = Mathf.Clamp(local.x, bounds.xMin + 8f, bounds.xMax - size.x - 8f);
            local.y = Mathf.Clamp(local.y, bounds.yMin + size.y + 8f, bounds.yMax - 8f);
            _rect.anchoredPosition = local;
        }

        public void SetVisible(bool visible)
        {
            gameObject.SetActive(visible);
        }

        private Text MakeText(string name, Transform parent, int fontSize, FontStyle style, Color color)
        {
            GameObject go = new(name);
            go.transform.SetParent(parent, false);
            Text text = go.AddComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.fontSize = fontSize;
            text.fontStyle = style;
            text.color = color;
            text.alignment = TextAnchor.UpperLeft;
            text.raycastTarget = false;
            return text;
        }

        private RectTransform AddContentPanel(Color color)
        {
            GameObject go = new("Content");
            go.transform.SetParent(transform, false);
            RectTransform rect = go.AddComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(TooltipPresenter.BorderThickness, TooltipPresenter.BorderThickness);
            rect.offsetMax = new Vector2(-TooltipPresenter.BorderThickness, -TooltipPresenter.BorderThickness);

            Image image = go.AddComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
            return rect;
        }

        private static Vector2 OuterSize(float contentHeight)
        {
            float border = TooltipPresenter.BorderThickness * 2f;
            return new Vector2(TooltipPresenter.Width + border, contentHeight + border);
        }

        private static void SetRect(RectTransform rect, Vector2 topLeft, Vector2 size)
        {
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = topLeft;
            rect.sizeDelta = size;
        }

    }
}
