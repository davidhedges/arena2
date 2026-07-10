#nullable enable

using Arena.Combat;
using TMPro;
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
        internal const float Width = 284f;
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
        private Image _border = null!;
        private TextMeshProUGUI _name = null!;
        private TextMeshProUGUI _subtitle = null!;
        private RectTransform _statsBlock = null!;
        private TextMeshProUGUI _description = null!;
        private TextMeshProUGUI _footnote = null!;

        private const float RowHeight = 19f;

        public Canvas Canvas { get; private set; } = null!;

        public void Initialize(Canvas canvas)
        {
            Canvas = canvas;
            _rect = gameObject.AddComponent<RectTransform>();
            _rect.pivot = new Vector2(0f, 1f);

            // Shadow + rounded backdrop live outside the layout flow.
            Image shadow = ArenaUiKit.AddShadow(_rect, spread: 20f, alpha: 0.6f);
            shadow.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;

            RectTransform backdrop = ArenaUiKit.MakeRect(_rect, "Backdrop");
            backdrop.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
            ArenaUiKit.Fill(backdrop);
            backdrop.SetSiblingIndex(1);
            Image backdropImage = backdrop.gameObject.AddComponent<Image>();
            backdropImage.raycastTarget = false;
            ArenaUiSprites.SurfaceSprite fill = ArenaUiSprites.WindowFill;
            ArenaUiKit.ApplySurface(backdropImage, fill, ArenaUiTheme.PanelStrong);
            if (!fill.Authored)
                ArenaUiKit.AddSheen(backdrop, 0.04f);
            _border = ArenaUiKit.AddBorder(backdrop, ArenaUiTheme.HairlineStrong, ArenaUiSprites.PanelRadius);

            VerticalLayoutGroup layout = gameObject.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(12, 12, 10, 10);
            layout.spacing = 4f;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            ContentSizeFitter fitter = gameObject.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            _rect.sizeDelta = new Vector2(TooltipPresenter.Width, 0f);

            _name = MakeLine("Name", 15f, ArenaUiTheme.Gold, ArenaUiTheme.TitleFont);
            _subtitle = MakeLine("Subtitle", ArenaUiTheme.SmallSize + 0.5f, ArenaUiTheme.MutedText, ArenaUiTheme.BodyFont);

            _statsBlock = ArenaUiKit.MakeRect(transform, "Stats");
            VerticalLayoutGroup statsLayout = _statsBlock.gameObject.AddComponent<VerticalLayoutGroup>();
            statsLayout.spacing = 1f;
            statsLayout.childControlWidth = true;
            statsLayout.childControlHeight = false;
            statsLayout.childForceExpandWidth = true;
            statsLayout.childForceExpandHeight = false;

            _description = MakeLine("Description", ArenaUiTheme.SmallSize + 0.5f, ArenaUiTheme.Text, ArenaUiTheme.BodyFont);
            _description.textWrappingMode = TextWrappingModes.Normal;
            _description.overflowMode = TextOverflowModes.Overflow;

            _footnote = MakeLine("Footnote", ArenaUiTheme.SmallSize - 0.5f, ArenaUiTheme.MutedText, ArenaUiTheme.BodyFont);
            _footnote.fontStyle = FontStyles.Italic;

            SetVisible(false);
        }

        public void Set(TooltipData data)
        {
            _name.text = data.Name;
            _name.color = data.NameColor ?? ArenaUiTheme.Gold;

            // Diablo-style rarity framing: the border ring takes the name color.
            Color borderColor = data.NameColor ?? ArenaUiTheme.HairlineStrong;
            borderColor.a = data.NameColor.HasValue ? 0.8f : ArenaUiTheme.HairlineStrong.a;
            _border.color = borderColor;

            SetLine(_subtitle, data.Subtitle);
            SetLine(_description, data.Description);
            SetLine(_footnote, data.Footnote);
            RebuildStats(data);

            transform.SetAsLastSibling();
            LayoutRebuilder.ForceRebuildLayoutImmediate(_rect);
        }

        private void RebuildStats(TooltipData data)
        {
            foreach (Transform child in _statsBlock)
            {
                // Deactivate before the deferred Destroy so the immediate layout
                // rebuild below doesn't measure stale rows.
                child.gameObject.SetActive(false);
                Destroy(child.gameObject);
            }

            bool hasStats = data.Stats is { Count: > 0 };
            _statsBlock.gameObject.SetActive(hasStats);
            if (!hasStats)
                return;

            foreach (TooltipStat stat in data.Stats!)
            {
                RectTransform row = ArenaUiKit.MakeRect(_statsBlock, "StatRow");
                LayoutElement element = row.gameObject.AddComponent<LayoutElement>();
                element.preferredHeight = RowHeight;
                element.minHeight = RowHeight;

                TextMeshProUGUI label = ArenaUiKit.MakeText(
                    row,
                    "Label",
                    stat.Label,
                    ArenaUiTheme.SmallSize,
                    ArenaUiTheme.MutedText);
                ArenaUiKit.Fill(label.rectTransform);

                TextMeshProUGUI value = ArenaUiKit.MakeText(
                    row,
                    "Value",
                    stat.Value,
                    ArenaUiTheme.SmallSize,
                    stat.Positive ? ArenaUiTheme.Success : ArenaUiTheme.Danger,
                    ArenaUiTheme.StrongFont,
                    TextAlignmentOptions.MidlineRight);
                ArenaUiKit.Fill(value.rectTransform);
            }
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

            Vector2 size = _rect.rect.size;
            Rect bounds = canvasRect.rect;
            local.x = Mathf.Clamp(local.x, bounds.xMin + 8f, bounds.xMax - size.x - 8f);
            local.y = Mathf.Clamp(local.y, bounds.yMin + size.y + 8f, bounds.yMax - 8f);
            _rect.anchoredPosition = local;
        }

        public void SetVisible(bool visible)
        {
            gameObject.SetActive(visible);
        }

        private TextMeshProUGUI MakeLine(string name, float size, Color color, TMP_FontAsset? font)
        {
            TextMeshProUGUI text = ArenaUiKit.MakeText(transform, name, string.Empty, size, color, font);
            text.overflowMode = TextOverflowModes.Ellipsis;
            return text;
        }

        private static void SetLine(TextMeshProUGUI line, string value)
        {
            bool has = !string.IsNullOrWhiteSpace(value);
            line.gameObject.SetActive(has);
            line.text = has ? value : string.Empty;
        }
    }
}
