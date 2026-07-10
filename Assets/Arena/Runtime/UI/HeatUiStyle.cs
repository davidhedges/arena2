#nullable enable

using Michsky.UI.Heat;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Arena.UI
{
    /// <summary>
    /// Legacy styling shim. All values now delegate to <see cref="ArenaUiTheme"/>
    /// (sourced from the Heat UI Manager asset); prefer <see cref="ArenaUiKit"/>
    /// builders for new or migrated UI.
    /// </summary>
    internal static class HeatUiStyle
    {
        public static Color Panel => ArenaUiTheme.Panel;
        public static Color PanelStrong => ArenaUiTheme.PanelStrong;
        public static Color Header => ArenaUiTheme.Header;
        public static Color Row => ArenaUiTheme.Row;
        public static Color RowAlt => ArenaUiTheme.RowAlt;
        public static Color Learned => ArenaUiTheme.PositiveRow;
        public static Color CellEmpty => ArenaUiTheme.CellEmpty;
        public static Color CellFilled => ArenaUiTheme.CellFilled;
        public static Color Showcase => ArenaUiTheme.Showcase;
        public static Color Text => ArenaUiTheme.Text;
        public static Color MutedText => ArenaUiTheme.MutedText;
        public static Color Accent => ArenaUiTheme.Accent;
        public static Color AccentHot => ArenaUiTheme.AccentHot;
        public static Color Gold => ArenaUiTheme.Gold;
        public static Color Success => ArenaUiTheme.Success;
        public static Color Error => ArenaUiTheme.Danger;

        public static void StylePanel(GameObject go, Color? color = null, bool raycastTarget = true)
        {
            Image image = ArenaUiKit.EnsureComponent<Image>(go);
            image.color = color ?? Panel;
            image.raycastTarget = raycastTarget;

            Outline outline = ArenaUiKit.EnsureComponent<Outline>(go);
            outline.effectColor = new Color(1f, 1f, 1f, 0.14f);
            outline.effectDistance = new Vector2(1f, -1f);
        }

        public static void StyleHeader(GameObject go)
        {
            Image image = ArenaUiKit.EnsureComponent<Image>(go);
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
            Image image = ArenaUiKit.EnsureComponent<Image>(button.gameObject);
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

            ButtonManager heat = ArenaUiKit.EnsureComponent<ButtonManager>(button.gameObject);
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

            // Accent-filled buttons need the on-accent text color for contrast.
            Color labelColor = textColor ?? (fill == null ? ArenaUiTheme.OnAccent : Text);
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
            => ArenaUiTheme.TitleFont;

        public static Font ResolveLegacyFont()
            => Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
    }
}
