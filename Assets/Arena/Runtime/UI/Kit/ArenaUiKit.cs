#nullable enable

using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace Arena.UI
{
    internal enum ArenaButtonStyle
    {
        /// <summary>Accent-filled call to action.</summary>
        Primary,
        /// <summary>Neutral raised surface for regular actions.</summary>
        Secondary,
        /// <summary>Hairline-outlined low-emphasis action.</summary>
        Ghost,
        /// <summary>Destructive/negative action.</summary>
        Danger,
    }

    internal readonly struct ArenaButtonHandle
    {
        public readonly Button Button;
        public readonly Image Background;
        public readonly TextMeshProUGUI Label;

        public ArenaButtonHandle(Button button, Image background, TextMeshProUGUI label)
        {
            Button = button;
            Background = background;
            Label = label;
        }

        public GameObject GameObject => Button.gameObject;
        public RectTransform Rect => (RectTransform)Button.transform;

        public void SetLabel(string text) => Label.text = text;

        public void SetInteractable(bool interactable)
        {
            Button.interactable = interactable;
            Label.alpha = interactable ? 1f : 0.45f;
        }
    }

    /// <summary>
    /// Procedural widget builders for menu/interface construction. Everything is
    /// styled from <see cref="ArenaUiTheme"/> so all windows read as one system.
    /// </summary>
    internal static class ArenaUiKit
    {
        /// <summary>
        /// Get-or-add that is safe against Unity's editor fake-null: GetComponent
        /// on a missing component returns a non-C#-null wrapper, so `??`/`?.`
        /// must never be used on UnityEngine.Object results.
        /// </summary>
        public static T EnsureComponent<T>(GameObject go) where T : Component
            => go.TryGetComponent(out T existing) ? existing : go.AddComponent<T>();

        public static Canvas MakeOverlayCanvas(GameObject host, int sortingOrder)
        {
            Canvas canvas = EnsureComponent<Canvas>(host);
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = sortingOrder;

            CanvasScaler scaler = EnsureComponent<CanvasScaler>(host);
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            if (host.GetComponent<GraphicRaycaster>() == null)
                host.AddComponent<GraphicRaycaster>();

            RuntimeUiEventSystem.Ensure();
            return canvas;
        }

        public static RectTransform MakeRect(Transform parent, string name)
        {
            GameObject go = new(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        public static RectTransform MakePanel(
            Transform parent,
            string name,
            Color? color = null,
            bool raycastTarget = true,
            bool hairline = false,
            float cornerRadius = 0f)
        {
            RectTransform rect = MakeRect(parent, name);
            Image image = rect.gameObject.AddComponent<Image>();
            image.color = color ?? ArenaUiTheme.Panel;
            image.raycastTarget = raycastTarget;
            if (cornerRadius > 0f)
                ApplyRounding(image, cornerRadius);

            if (hairline)
            {
                if (cornerRadius > 0f)
                    AddBorder(rect, ArenaUiTheme.Hairline, cornerRadius);
                else
                    AddHairline(rect.gameObject);
            }

            return rect;
        }

        /// <summary>Assigns the rounded 9-slice sprite matching the requested radius.</summary>
        public static void ApplyRounding(Image image, float cornerRadius)
        {
            image.sprite = cornerRadius > ArenaUiSprites.SmallRadius + 2f
                ? ArenaUiSprites.Panel
                : ArenaUiSprites.PanelSmall;
            image.type = Image.Type.Sliced;
        }

        /// <summary>
        /// Applies an authored-or-procedural surface. Authored art draws in its
        /// own colors unless <paramref name="tintAuthored"/> (for state-tinted
        /// surfaces like buttons, authored neutral/desaturated).
        /// </summary>
        public static void ApplySurface(
            Image image,
            ArenaUiSprites.SurfaceSprite surface,
            Color themeColor,
            bool tintAuthored = false)
        {
            image.sprite = surface.Sprite;
            image.type = surface.Sprite != null ? Image.Type.Sliced : Image.Type.Simple;
            image.color = surface.Authored && !tintAuthored ? Color.white : themeColor;
        }

        /// <summary>Rounded border ring drawn over the target rect. Never blocks raycasts.</summary>
        public static Image AddBorder(RectTransform target, Color color, float cornerRadius)
        {
            RectTransform rect = MakeRect(target, "Border");
            Fill(rect);
            Image ring = rect.gameObject.AddComponent<Image>();
            ring.sprite = cornerRadius > ArenaUiSprites.SmallRadius + 2f
                ? ArenaUiSprites.Ring
                : ArenaUiSprites.RingSmall;
            ring.type = Image.Type.Sliced;
            ring.color = color;
            ring.raycastTarget = false;
            return ring;
        }

        /// <summary>Soft drop shadow behind the target rect (added as first child).</summary>
        public static Image AddShadow(RectTransform target, float spread = 26f, float alpha = 0.5f)
        {
            RectTransform rect = MakeRect(target, "Shadow");
            rect.SetAsFirstSibling();
            SetAnchors(rect, Vector2.zero, Vector2.one, new Vector2(-spread, -spread - 6f), new Vector2(spread, spread - 6f));
            Image shadow = rect.gameObject.AddComponent<Image>();
            shadow.sprite = ArenaUiSprites.Shadow;
            shadow.type = Image.Type.Sliced;
            shadow.color = new Color(0f, 0f, 0f, alpha);
            shadow.raycastTarget = false;
            return shadow;
        }

        /// <summary>Subtle top-lit gradient overlay (light falls from the top edge).</summary>
        public static Image AddSheen(RectTransform target, float alpha = 0.055f)
        {
            RectTransform rect = MakeRect(target, "Sheen");
            Fill(rect);
            Image sheen = rect.gameObject.AddComponent<Image>();
            sheen.sprite = ArenaUiSprites.Sheen;
            sheen.type = Image.Type.Simple;
            sheen.color = new Color(1f, 1f, 1f, alpha);
            sheen.raycastTarget = false;
            return sheen;
        }

        public static Outline AddHairline(GameObject go, Color? color = null)
        {
            Outline outline = EnsureComponent<Outline>(go);
            outline.effectColor = color ?? ArenaUiTheme.Hairline;
            outline.effectDistance = new Vector2(1f, -1f);
            return outline;
        }

        public static TextMeshProUGUI MakeText(
            Transform parent,
            string name,
            string text,
            float size,
            Color? color = null,
            TMP_FontAsset? font = null,
            TextAlignmentOptions alignment = TextAlignmentOptions.MidlineLeft)
        {
            RectTransform rect = MakeRect(parent, name);
            TextMeshProUGUI label = rect.gameObject.AddComponent<TextMeshProUGUI>();
            TMP_FontAsset? resolved = font ?? ArenaUiTheme.BodyFont;
            if (resolved != null)
                label.font = resolved;
            label.text = text;
            label.fontSize = size;
            label.color = color ?? ArenaUiTheme.Text;
            label.alignment = alignment;
            label.raycastTarget = false;
            label.textWrappingMode = TextWrappingModes.NoWrap;
            label.overflowMode = TextOverflowModes.Ellipsis;
            return label;
        }

        public static TextMeshProUGUI MakeTitle(Transform parent, string name, string text)
            => MakeText(parent, name, text, ArenaUiTheme.WindowTitleSize, ArenaUiTheme.Text, ArenaUiTheme.TitleFont);

        public static TextMeshProUGUI MakeSectionLabel(Transform parent, string name, string text)
        {
            TextMeshProUGUI label = MakeText(
                parent,
                name,
                text.ToUpperInvariant(),
                ArenaUiTheme.SectionSize,
                ArenaUiTheme.MutedText,
                ArenaUiTheme.StrongFont);
            label.characterSpacing = 6f;
            return label;
        }

        public static RectTransform MakeDivider(Transform parent, string name = "Divider")
        {
            RectTransform rect = MakeRect(parent, name);
            Image image = rect.gameObject.AddComponent<Image>();
            ApplySurface(image, ArenaUiSprites.Divider, ArenaUiTheme.Hairline);
            image.raycastTarget = false;
            return rect;
        }

        public static ArenaButtonHandle MakeButton(
            Transform parent,
            string name,
            string label,
            ArenaButtonStyle style = ArenaButtonStyle.Secondary,
            UnityAction? onClick = null,
            float textSize = ArenaUiTheme.BodySize)
        {
            RectTransform rect = MakeRect(parent, name);
            Image background = rect.gameObject.AddComponent<Image>();
            background.raycastTarget = true;
            ArenaUiSprites.SurfaceSprite buttonFill = ArenaUiSprites.ButtonFill;
            if (buttonFill.Authored)
            {
                // Authored neutral plate; ApplyButtonStyle tints it per style.
                background.sprite = buttonFill.Sprite;
                background.type = Image.Type.Sliced;
            }
            else
            {
                ApplyRounding(background, ArenaUiSprites.SmallRadius);
            }

            Button button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = background;
            if (onClick != null)
                button.onClick.AddListener(onClick);

            if (style != ArenaButtonStyle.Ghost && !buttonFill.Authored)
                AddSheen(rect, 0.09f);

            TextMeshProUGUI text = MakeText(
                rect,
                "Label",
                label,
                textSize,
                font: ArenaUiTheme.StrongFont,
                alignment: TextAlignmentOptions.Center);
            Fill(text.rectTransform, new Vector2(8f, 2f));
            text.fontStyle = FontStyles.Bold;

            ApplyButtonStyle(button, background, text, style);

            // Hover glow: accent ring for neutral styles, white for filled ones.
            Color glowColor = style switch
            {
                ArenaButtonStyle.Primary => Color.white,
                ArenaButtonStyle.Danger => Color.white,
                _ => ArenaUiTheme.Accent,
            };
            Image glow = AddBorder(rect, glowColor, ArenaUiSprites.SmallRadius);
            ArenaUiSprites.SurfaceSprite glowSurface = ArenaUiSprites.ButtonGlow;
            if (glowSurface.Authored)
            {
                glow.sprite = glowSurface.Sprite;
                glow.type = Image.Type.Sliced;
            }
            glow.name = "HoverGlow";
            ArenaHoverGlow hover = rect.gameObject.AddComponent<ArenaHoverGlow>();
            hover.Bind(glow, 0.7f);

            return new ArenaButtonHandle(button, background, text);
        }

        public static void ApplyButtonStyle(
            Button button,
            Image background,
            TextMeshProUGUI label,
            ArenaButtonStyle style)
        {
            Color fill;
            Color textColor;
            switch (style)
            {
                case ArenaButtonStyle.Primary:
                    fill = ArenaUiTheme.Accent;
                    textColor = ArenaUiTheme.OnAccent;
                    break;
                case ArenaButtonStyle.Danger:
                    fill = ArenaUiTheme.Danger;
                    textColor = ArenaUiTheme.Text;
                    break;
                case ArenaButtonStyle.Ghost:
                    fill = new Color(1f, 1f, 1f, 0.04f);
                    textColor = ArenaUiTheme.MutedText;
                    if (button.transform.Find("Border") == null)
                        AddBorder((RectTransform)button.transform, ArenaUiTheme.HairlineStrong, ArenaUiSprites.SmallRadius);
                    break;
                default:
                    fill = ArenaUiTheme.RowAlt;
                    textColor = ArenaUiTheme.Text;
                    break;
            }

            background.color = fill;
            label.color = textColor;

            ColorBlock colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = style == ArenaButtonStyle.Ghost
                ? new Color(1.6f, 1.6f, 1.6f, 2.4f)
                : new Color(1.14f, 1.14f, 1.14f, 1f);
            colors.pressedColor = new Color(0.78f, 0.78f, 0.78f, 1f);
            colors.selectedColor = colors.highlightedColor;
            colors.disabledColor = new Color(0.45f, 0.45f, 0.45f, 0.6f);
            colors.colorMultiplier = 1f;
            colors.fadeDuration = 0.08f;
            button.colors = colors;
            button.transition = Selectable.Transition.ColorTint;
        }

        /// <summary>Vertical scroll region. Returns the content rect (top-anchored, stretch-width).</summary>
        public static RectTransform MakeScrollView(
            RectTransform parent,
            string name,
            out ScrollRect scrollRect)
        {
            RectTransform root = MakeRect(parent, name);
            scrollRect = root.gameObject.AddComponent<ScrollRect>();
            scrollRect.horizontal = false;
            scrollRect.movementType = ScrollRect.MovementType.Clamped;
            scrollRect.scrollSensitivity = 28f;

            RectTransform viewport = MakeRect(root, "Viewport");
            Fill(viewport);
            viewport.gameObject.AddComponent<RectMask2D>();
            Image viewportImage = viewport.gameObject.AddComponent<Image>();
            viewportImage.color = Color.clear;
            viewportImage.raycastTarget = true;

            RectTransform content = MakeRect(viewport, "Content");
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.offsetMin = Vector2.zero;
            content.offsetMax = Vector2.zero;

            scrollRect.viewport = viewport;
            scrollRect.content = content;
            return content;
        }

        public static void Fill(RectTransform rect, Vector2? padding = null)
        {
            Vector2 pad = padding ?? Vector2.zero;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = pad;
            rect.offsetMax = -pad;
        }

        public static void SetAnchors(
            RectTransform rect,
            Vector2 anchorMin,
            Vector2 anchorMax,
            Vector2 offsetMin,
            Vector2 offsetMax)
        {
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.offsetMin = offsetMin;
            rect.offsetMax = offsetMax;
        }

        public static void PlaceTopLeft(RectTransform rect, Vector2 topLeft, Vector2 size)
        {
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(topLeft.x, -topLeft.y);
            rect.sizeDelta = size;
        }
    }
}
