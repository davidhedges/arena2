#nullable enable

using System;
using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Arena.UI
{
    /// <summary>
    /// Shared window chrome for every menu/interface panel: backdrop, accent bar,
    /// header with title/subtitle and close button, padded content area, optional
    /// footer, and a fast fade/scale open-close transition. Visual construction
    /// only — owners keep their data binding and open/close policy.
    /// </summary>
    internal sealed class ArenaWindow : MonoBehaviour
    {
        private RectTransform _rect = null!;
        private CanvasGroup _group = null!;
        private TextMeshProUGUI _title = null!;
        private TextMeshProUGUI _subtitle = null!;
        private RectTransform _content = null!;
        private RectTransform? _footer;
        private RectTransform? _heroFrame;
        private Coroutine? _transition;
        private bool _visible;

        private const float TransitionSeconds = 0.12f;

        /// <summary>Raised when the header close button is pressed.</summary>
        public event Action? CloseRequested;

        public RectTransform Rect => _rect;
        public RectTransform Content => _content;
        public bool IsVisible => _visible;

        public static ArenaWindow Create(
            Transform parent,
            string name,
            string title,
            Vector2 size,
            bool showCloseButton = true)
        {
            RectTransform rect = ArenaUiKit.MakeRect(parent, name);
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = size;

            ArenaWindow window = rect.gameObject.AddComponent<ArenaWindow>();
            window.Build(title, showCloseButton);
            return window;
        }

        private const float ChromeInset = 3f;

        private void Build(string title, bool showCloseButton)
        {
            _rect = (RectTransform)transform;
            _group = gameObject.AddComponent<CanvasGroup>();

            ArenaUiKit.AddShadow(_rect);

            RectTransform backdrop = ArenaUiKit.MakeRect(_rect, "Backdrop");
            ArenaUiKit.Fill(backdrop);
            Image backdropImage = backdrop.gameObject.AddComponent<Image>();
            backdropImage.raycastTarget = true;
            ArenaUiSprites.SurfaceSprite fill = ArenaUiSprites.WindowFill;
            ArenaUiKit.ApplySurface(backdropImage, fill, ArenaUiTheme.PanelStrong);
            if (!fill.Authored)
                ArenaUiKit.AddSheen(backdrop, 0.035f);
            ArenaUiSprites.SurfaceSprite heroCorner = ArenaUiSprites.HeroCorner;
            if (!heroCorner.Authored)
            {
                // No composed hero frame available: fall back to the flat ring
                // (or a single 9-sliced window_frame override if present).
                Image frame = ArenaUiKit.AddBorder(backdrop, ArenaUiTheme.HairlineStrong, ArenaUiSprites.PanelRadius);
                ArenaUiKit.ApplySurface(frame, ArenaUiSprites.WindowFrame, ArenaUiTheme.HairlineStrong);
            }

            RectTransform header = ArenaUiKit.MakeRect(_rect, "Header");
            Image headerImage = header.gameObject.AddComponent<Image>();
            headerImage.raycastTarget = true;
            ArenaUiSprites.SurfaceSprite headerPlate = ArenaUiSprites.HeaderPlate;
            if (headerPlate.Authored)
                ArenaUiKit.ApplySurface(headerImage, headerPlate, ArenaUiTheme.Header);
            else
            {
                headerImage.color = ArenaUiTheme.Header;
                ArenaUiKit.ApplyRounding(headerImage, ArenaUiSprites.SmallRadius);
            }
            ArenaUiKit.SetAnchors(
                header,
                new Vector2(0f, 1f),
                new Vector2(1f, 1f),
                new Vector2(ChromeInset, -ArenaUiTheme.HeaderHeight),
                new Vector2(-ChromeInset, -ChromeInset));
            if (!headerPlate.Authored)
                ArenaUiKit.AddSheen(header, 0.05f);

            // Accent underline between header and content.
            RectTransform accent = ArenaUiKit.MakeRect(_rect, "AccentBar");
            Image accentImage = accent.gameObject.AddComponent<Image>();
            accentImage.color = ArenaUiTheme.Accent;
            accentImage.raycastTarget = false;
            ArenaUiKit.SetAnchors(
                accent,
                new Vector2(0f, 1f),
                new Vector2(1f, 1f),
                new Vector2(12f, -(ArenaUiTheme.HeaderHeight + ArenaUiTheme.AccentBarThickness)),
                new Vector2(-12f, -ArenaUiTheme.HeaderHeight));

            _title = ArenaUiKit.MakeTitle(header, "Title", title);
            ArenaUiKit.SetAnchors(
                _title.rectTransform,
                new Vector2(0f, 0f),
                new Vector2(0.62f, 1f),
                new Vector2(ArenaUiTheme.ContentPadding + 2f, 0f),
                Vector2.zero);

            _subtitle = ArenaUiKit.MakeText(
                header,
                "Subtitle",
                string.Empty,
                ArenaUiTheme.SmallSize,
                ArenaUiTheme.MutedText,
                alignment: TextAlignmentOptions.MidlineRight);
            ArenaUiKit.SetAnchors(
                _subtitle.rectTransform,
                new Vector2(0.4f, 0f),
                new Vector2(1f, 1f),
                Vector2.zero,
                new Vector2(-(ArenaUiTheme.HeaderHeight + 8f), 0f));

            if (showCloseButton)
                BuildCloseButton(header);

            _content = ArenaUiKit.MakeRect(_rect, "Content");
            ArenaUiKit.SetAnchors(
                _content,
                Vector2.zero,
                Vector2.one,
                new Vector2(ArenaUiTheme.ContentPadding, ArenaUiTheme.ContentPadding),
                new Vector2(
                    -ArenaUiTheme.ContentPadding,
                    -(ArenaUiTheme.AccentBarThickness + ArenaUiTheme.HeaderHeight + ArenaUiTheme.ContentPadding)));

            if (heroCorner.Authored)
                BuildHeroFrame(heroCorner);

            _visible = gameObject.activeSelf;
        }

        private const float HeroCornerSize = 88f;
        private const float HeroRailThickness = 9f;
        private const float HeroRailThicknessAuthored = 18f;
        private const float HeroRailInset = HeroCornerSize - 8f;
        private static readonly Color HeroSteel = new(0.30f, 0.30f, 0.33f, 1f);

        /// <summary>
        /// Composed Hero Frame: the authored bottom-left corner mirrored to all
        /// four corners, with rails between them (authored when available,
        /// procedural steel + brass otherwise). Decorative only — no raycasts.
        /// </summary>
        private void BuildHeroFrame(ArenaUiSprites.SurfaceSprite corner)
        {
            _heroFrame = ArenaUiKit.MakeRect(_rect, "HeroFrame");
            ArenaUiKit.Fill(_heroFrame);

            BuildHeroRail(horizontal: true, nearEdge: true);
            BuildHeroRail(horizontal: true, nearEdge: false);
            BuildHeroRail(horizontal: false, nearEdge: true);
            BuildHeroRail(horizontal: false, nearEdge: false);

            MakeHeroCorner(corner, new Vector2(0f, 0f), new Vector3(1f, 1f, 1f));
            MakeHeroCorner(corner, new Vector2(0f, 1f), new Vector3(1f, -1f, 1f));
            MakeHeroCorner(corner, new Vector2(1f, 1f), new Vector3(-1f, -1f, 1f));
            MakeHeroCorner(corner, new Vector2(1f, 0f), new Vector3(-1f, 1f, 1f));
        }

        private void MakeHeroCorner(ArenaUiSprites.SurfaceSprite corner, Vector2 anchor, Vector3 scale)
        {
            RectTransform rect = ArenaUiKit.MakeRect(_heroFrame!, "Corner");
            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(HeroCornerSize, HeroCornerSize);
            rect.anchoredPosition = new Vector2(
                (anchor.x < 0.5f ? 1f : -1f) * HeroCornerSize * 0.5f,
                (anchor.y < 0.5f ? 1f : -1f) * HeroCornerSize * 0.5f);
            rect.localScale = scale;

            Image image = rect.gameObject.AddComponent<Image>();
            image.sprite = corner.Sprite;
            image.type = Image.Type.Simple;
            image.preserveAspect = true;
            image.color = Color.white;
            image.raycastTarget = false;
        }

        /// <summary>
        /// One frame edge between two corners. horizontal+nearEdge = top.
        /// Authored rails are top-native (h, brass lip up) / left-native (v,
        /// brass lip left); the far edges mirror them.
        /// </summary>
        private void BuildHeroRail(bool horizontal, bool nearEdge)
        {
            ArenaUiSprites.SurfaceSprite authored = horizontal ? ArenaUiSprites.HeroRailH : ArenaUiSprites.HeroRailV;
            float thickness = authored.Authored ? HeroRailThicknessAuthored : HeroRailThickness;

            RectTransform rail = ArenaUiKit.MakeRect(_heroFrame!, horizontal ? (nearEdge ? "RailTop" : "RailBottom") : (nearEdge ? "RailLeft" : "RailRight"));
            if (horizontal)
            {
                float y = nearEdge ? 1f : 0f;
                ArenaUiKit.SetAnchors(
                    rail,
                    new Vector2(0f, y),
                    new Vector2(1f, y),
                    new Vector2(HeroRailInset, nearEdge ? -thickness : 0f),
                    new Vector2(-HeroRailInset, nearEdge ? 0f : thickness));
            }
            else
            {
                float x = nearEdge ? 0f : 1f;
                ArenaUiKit.SetAnchors(
                    rail,
                    new Vector2(x, 0f),
                    new Vector2(x, 1f),
                    new Vector2(nearEdge ? 0f : -thickness, HeroRailInset),
                    new Vector2(nearEdge ? thickness : 0f, -HeroRailInset));
            }

            if (authored.Authored)
            {
                Image image = rail.gameObject.AddComponent<Image>();
                image.sprite = authored.Sprite;
                image.type = Image.Type.Simple;
                image.color = Color.white;
                image.raycastTarget = false;
                if (!nearEdge)
                    rail.localScale = horizontal ? new Vector3(1f, -1f, 1f) : new Vector3(-1f, 1f, 1f);
                return;
            }

            // Procedural fallback echoing the corner: brass lip on the outer
            // edge, dark steel bar inside.
            RectTransform brass = ArenaUiKit.MakeRect(rail, "Brass");
            Image brassImage = brass.gameObject.AddComponent<Image>();
            brassImage.color = ArenaUiTheme.Accent;
            brassImage.raycastTarget = false;

            RectTransform steel = ArenaUiKit.MakeRect(rail, "Steel");
            Image steelImage = steel.gameObject.AddComponent<Image>();
            steelImage.color = HeroSteel;
            steelImage.raycastTarget = false;

            const float lip = 3f;
            if (horizontal)
            {
                if (nearEdge)
                {
                    ArenaUiKit.SetAnchors(brass, new Vector2(0f, 1f), Vector2.one, new Vector2(0f, -lip), Vector2.zero);
                    ArenaUiKit.SetAnchors(steel, Vector2.zero, Vector2.one, Vector2.zero, new Vector2(0f, -lip));
                }
                else
                {
                    ArenaUiKit.SetAnchors(brass, Vector2.zero, new Vector2(1f, 0f), Vector2.zero, new Vector2(0f, lip));
                    ArenaUiKit.SetAnchors(steel, Vector2.zero, Vector2.one, new Vector2(0f, lip), Vector2.zero);
                }
            }
            else
            {
                if (nearEdge)
                {
                    ArenaUiKit.SetAnchors(brass, Vector2.zero, new Vector2(0f, 1f), Vector2.zero, new Vector2(lip, 0f));
                    ArenaUiKit.SetAnchors(steel, Vector2.zero, Vector2.one, new Vector2(lip, 0f), Vector2.zero);
                }
                else
                {
                    ArenaUiKit.SetAnchors(brass, new Vector2(1f, 0f), Vector2.one, new Vector2(-lip, 0f), Vector2.zero);
                    ArenaUiKit.SetAnchors(steel, Vector2.zero, Vector2.one, Vector2.zero, new Vector2(-lip, 0f));
                }
            }
        }

        private void BuildCloseButton(RectTransform header)
        {
            ArenaButtonHandle close = ArenaUiKit.MakeButton(
                header,
                "CloseButton",
                "×",
                ArenaButtonStyle.Ghost,
                () => CloseRequested?.Invoke(),
                textSize: 20f);
            ArenaUiKit.SetAnchors(
                close.Rect,
                new Vector2(1f, 0.5f),
                new Vector2(1f, 0.5f),
                Vector2.zero,
                Vector2.zero);
            close.Rect.pivot = new Vector2(1f, 0.5f);
            close.Rect.anchoredPosition = new Vector2(-6f, 0f);
            close.Rect.sizeDelta = new Vector2(32f, 32f);
        }

        /// <summary>
        /// Reserves a footer band; content shrinks to sit above it. Call once
        /// during construction, before populating content.
        /// </summary>
        public RectTransform AddFooter()
        {
            if (_footer != null)
                return _footer;

            _footer = ArenaUiKit.MakeRect(_rect, "Footer");
            Image image = _footer.gameObject.AddComponent<Image>();
            image.raycastTarget = true;
            ArenaUiSprites.SurfaceSprite footerPlate = ArenaUiSprites.HeaderPlate;
            if (footerPlate.Authored)
                ArenaUiKit.ApplySurface(image, footerPlate, ArenaUiTheme.Header);
            else
            {
                image.color = ArenaUiTheme.Header;
                ArenaUiKit.ApplyRounding(image, ArenaUiSprites.SmallRadius);
            }
            ArenaUiKit.SetAnchors(
                _footer,
                Vector2.zero,
                new Vector2(1f, 0f),
                new Vector2(ChromeInset, ChromeInset),
                new Vector2(-ChromeInset, ArenaUiTheme.FooterHeight));

            _content.offsetMin = new Vector2(
                ArenaUiTheme.ContentPadding,
                ArenaUiTheme.FooterHeight + ArenaUiTheme.ContentPadding);
            // Keep the composed frame above the footer plate.
            _heroFrame?.SetAsLastSibling();
            return _footer;
        }

        public void SetTitle(string title) => _title.text = title;

        public void SetSubtitle(string subtitle) => _subtitle.text = subtitle;

        /// <summary>Shows/hides with the shared transition. Instant variant for startup.</summary>
        public void SetVisible(bool visible, bool instant = false)
        {
            if (_visible == visible && !instant)
                return;

            _visible = visible;
            if (_transition != null)
            {
                StopCoroutine(_transition);
                _transition = null;
            }

            if (instant || !gameObject.activeInHierarchy && !visible)
            {
                gameObject.SetActive(visible);
                _group.alpha = visible ? 1f : 0f;
                _rect.localScale = Vector3.one;
                return;
            }

            gameObject.SetActive(true);
            if (!gameObject.activeInHierarchy)
            {
                // Parent hierarchy is inactive; coroutines can't run, so snap.
                _group.alpha = visible ? 1f : 0f;
                _rect.localScale = Vector3.one;
                gameObject.SetActive(visible);
                return;
            }

            _transition = StartCoroutine(Transition(visible));
        }

        private IEnumerator Transition(bool visible)
        {
            float from = _group.alpha;
            float to = visible ? 1f : 0f;
            float fromScale = _rect.localScale.x;
            float toScale = visible ? 1f : 0.965f;
            if (visible && from <= 0.01f)
                fromScale = 0.965f;

            float elapsed = 0f;
            while (elapsed < TransitionSeconds)
            {
                elapsed += Time.unscaledDeltaTime;
                float t = Mathf.Clamp01(elapsed / TransitionSeconds);
                t = 1f - (1f - t) * (1f - t);
                _group.alpha = Mathf.Lerp(from, to, t);
                float scale = Mathf.Lerp(fromScale, toScale, t);
                _rect.localScale = new Vector3(scale, scale, 1f);
                yield return null;
            }

            _group.alpha = to;
            _rect.localScale = new Vector3(toScale, toScale, 1f);
            _transition = null;
            if (!visible)
                gameObject.SetActive(false);
        }
    }
}
