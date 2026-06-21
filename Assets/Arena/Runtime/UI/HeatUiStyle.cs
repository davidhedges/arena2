#nullable enable

using Michsky.UI.Heat;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Arena.UI
{
    internal static class HeatUiStyle
    {
        public static readonly Color Panel = new(0.045f, 0.049f, 0.058f, 0.96f);
        public static readonly Color PanelStrong = new(0.06f, 0.066f, 0.078f, 0.98f);
        public static readonly Color Header = new(0.075f, 0.083f, 0.098f, 0.98f);
        public static readonly Color Row = new(0.085f, 0.093f, 0.108f, 0.96f);
        public static readonly Color RowAlt = new(0.102f, 0.112f, 0.128f, 0.96f);
        public static readonly Color Learned = new(0.105f, 0.155f, 0.128f, 0.96f);
        public static readonly Color CellEmpty = new(0.022f, 0.026f, 0.032f, 0.94f);
        public static readonly Color CellFilled = new(0.135f, 0.15f, 0.17f, 0.98f);
        public static readonly Color Showcase = new(0.024f, 0.029f, 0.036f, 0.76f);
        public static readonly Color Text = new(0.94f, 0.96f, 0.98f, 1f);
        public static readonly Color MutedText = new(0.66f, 0.70f, 0.76f, 1f);
        public static readonly Color Accent = new(0.84f, 0.13f, 0.09f, 1f);
        public static readonly Color AccentHot = new(0.96f, 0.20f, 0.13f, 1f);
        public static readonly Color Gold = new(0.96f, 0.73f, 0.26f, 1f);
        public static readonly Color Success = new(0.56f, 0.86f, 0.62f, 1f);
        public static readonly Color Error = new(1f, 0.42f, 0.34f, 1f);

        public static void StylePanel(GameObject go, Color? color = null, bool raycastTarget = true)
        {
            Image image = go.GetComponent<Image>() ?? go.AddComponent<Image>();
            image.color = color ?? Panel;
            image.raycastTarget = raycastTarget;

            Outline outline = go.GetComponent<Outline>() ?? go.AddComponent<Outline>();
            outline.effectColor = new Color(1f, 1f, 1f, 0.14f);
            outline.effectDistance = new Vector2(1f, -1f);
        }

        public static void StyleHeader(GameObject go)
        {
            Image image = go.GetComponent<Image>() ?? go.AddComponent<Image>();
            image.color = Header;
            image.raycastTarget = false;
        }

        public static RectTransform AddAccentBar(Transform parent, string name, Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax)
        {
            GameObject go = new(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.transform.SetParent(parent, false);
            RectTransform rt = (RectTransform)go.transform;
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.offsetMin = offsetMin;
            rt.offsetMax = offsetMax;
            Image image = go.GetComponent<Image>();
            image.color = Accent;
            image.raycastTarget = false;
            return rt;
        }

        public static void StyleButton(Button button, string text, Color? fill = null, Color? textColor = null)
        {
            Color normal = fill ?? Accent;
            Image image = button.GetComponent<Image>() ?? button.gameObject.AddComponent<Image>();
            image.color = normal;
            image.raycastTarget = true;

            ColorBlock colors = button.colors;
            colors.normalColor = normal;
            colors.highlightedColor = Tint(normal, 1.18f);
            colors.pressedColor = Tint(normal, 0.72f);
            colors.selectedColor = colors.highlightedColor;
            colors.disabledColor = new Color(0.36f, 0.36f, 0.36f, 0.55f);
            colors.colorMultiplier = 1f;
            button.colors = colors;
            button.transition = Selectable.Transition.ColorTint;

            ButtonManager heat = button.GetComponent<ButtonManager>() ?? button.gameObject.AddComponent<ButtonManager>();
            heat.buttonText = text;
            heat.textSize = 13f;
            heat.enableText = false;
            heat.enableIcon = false;
            heat.useCustomContent = true;
            heat.autoFitContent = false;
            heat.useLocalization = false;
            heat.useSounds = false;
            heat.checkForDoubleClick = false;
            heat.bypassUpdateOnEnable = true;
            heat.useUINavigation = false;
            heat.isInteractable = button.interactable;

            Color labelColor = textColor ?? Text;
            foreach (TextMeshProUGUI label in button.GetComponentsInChildren<TextMeshProUGUI>(true))
            {
                label.text = text;
                label.color = labelColor;
                label.fontStyle = FontStyles.Bold;
            }

            foreach (Text label in button.GetComponentsInChildren<Text>(true))
            {
                label.text = text;
                label.color = labelColor;
                label.fontStyle = FontStyle.Bold;
            }
        }

        public static void SyncButtonInteractable(Button button)
        {
            if (button.TryGetComponent(out ButtonManager heat))
            {
                heat.isInteractable = button.interactable;
                heat.Interactable(button.interactable);
            }
        }

        private static Color Tint(Color color, float multiplier)
            => new(
                Mathf.Clamp01(color.r * multiplier),
                Mathf.Clamp01(color.g * multiplier),
                Mathf.Clamp01(color.b * multiplier),
                color.a);

        public static TMP_FontAsset? ResolveFont()
            => Resources.Load<TMP_FontAsset>("Fonts/SDF/RobotoCondensed-Bold SDF");

        public static Font ResolveLegacyFont()
            => Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
    }
}
