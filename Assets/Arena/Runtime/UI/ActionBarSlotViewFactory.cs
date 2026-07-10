#nullable enable

using System;
using Arena.Combat;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Arena.UI
{
    internal static class ActionBarSlotViewFactory
    {
        private static readonly Color TransparentSlotInput = new(1f, 1f, 1f, 0f);

        public static GameObject Create(
            Transform parent,
            string name,
            string label,
            string keyLabel,
            Color fill,
            Color textColor,
            Sprite? iconSprite,
            Canvas canvas,
            string? slotId = null,
            Func<ActionBarDragPayload?>? payloadProvider = null,
            Action<ActionBarDragPayload, string?>? onDrop = null,
            Action? onClick = null,
            TooltipData tooltipData = default)
        {
            GameObject go = InstantiateSlot(parent, name);
            ConfigureBaseVisual(go, label, keyLabel, fill, textColor, iconSprite);

            TooltipTarget tooltip = ArenaUiKit.EnsureComponent<TooltipTarget>(go);
            tooltip.Configure(canvas, tooltipData);

            if (!string.IsNullOrWhiteSpace(slotId))
            {
                ActionBarDropSlot dropSlot = ArenaUiKit.EnsureComponent<ActionBarDropSlot>(go);
                dropSlot.Configure(canvas, slotId);
            }

            if (payloadProvider != null || onClick != null)
            {
                ActionBarDragSource dragSource = ArenaUiKit.EnsureComponent<ActionBarDragSource>(go);
                dragSource.Configure(canvas, payloadProvider, onDrop, onClick);
                ConfigureButtonFeedback(go);
            }

            return go;
        }

        private static GameObject InstantiateSlot(Transform parent, string name)
        {
            GameObject? prefab = Resources.Load<GameObject>(ActionBarLayout.SlotPrefabResourcePath);
            GameObject go = prefab != null
                ? UnityEngine.Object.Instantiate(prefab, parent, false)
                : new GameObject(name);

            go.name = name;
            if (prefab == null)
                go.transform.SetParent(parent, false);
            return go;
        }

        private static void ConfigureBaseVisual(
            GameObject go,
            string label,
            string keyLabel,
            Color fill,
            Color textColor,
            Sprite? iconSprite)
        {
            Image image = ArenaUiKit.EnsureComponent<Image>(go);
            image.color = HasPrefabFrame(go) ? TransparentSlotInput : fill;
            image.raycastTarget = true;

            RectTransform rt = (RectTransform)go.transform;
            rt.sizeDelta = ActionBarLayout.SlotVector;

            if (iconSprite != null)
            {
                GameObject iconGo = new("Icon");
                iconGo.transform.SetParent(go.transform, false);
                RectTransform iconRt = iconGo.AddComponent<RectTransform>();
                iconRt.anchorMin = Vector2.zero;
                iconRt.anchorMax = Vector2.one;
                iconRt.offsetMin = Vector2.zero;
                iconRt.offsetMax = Vector2.zero;

                Image icon = iconGo.AddComponent<Image>();
                icon.sprite = iconSprite;
                icon.color = Color.white;
                icon.preserveAspect = true;
                icon.raycastTarget = false;
            }

            TextMeshProUGUI labelText = MakeLabel("Text", go.transform, label, iconSprite == null ? 10f : 9f, TextAlignmentOptions.Center, textColor);
            Stretch(labelText.rectTransform, new Vector2(5f, 5f), new Vector2(-5f, -5f));
            if (iconSprite != null && !string.IsNullOrWhiteSpace(label))
            {
                Shadow shadow = labelText.gameObject.AddComponent<Shadow>();
                shadow.effectColor = new Color(0f, 0f, 0f, 0.82f);
                shadow.effectDistance = new Vector2(1f, -1f);
            }

            if (!string.IsNullOrWhiteSpace(keyLabel))
            {
                TextMeshProUGUI keyText = MakeLabel("Keybind", go.transform, keyLabel, 10f, TextAlignmentOptions.TopLeft, new Color(0.72f, 0.75f, 0.80f));
                keyText.rectTransform.anchorMin = new Vector2(0f, 1f);
                keyText.rectTransform.anchorMax = new Vector2(0f, 1f);
                keyText.rectTransform.pivot = new Vector2(0f, 1f);
                keyText.rectTransform.anchoredPosition = new Vector2(4f, -4f);
                keyText.rectTransform.sizeDelta = new Vector2(48f, 18f);
            }
        }

        private static TextMeshProUGUI MakeLabel(
            string name,
            Transform parent,
            string text,
            float fontSize,
            TextAlignmentOptions alignment,
            Color color)
        {
            GameObject go = new(name);
            go.transform.SetParent(parent, false);
            RectTransform rt = go.AddComponent<RectTransform>();
            TextMeshProUGUI label = go.AddComponent<TextMeshProUGUI>();
            label.font = TMP_Settings.defaultFontAsset
                ?? Resources.Load<TMP_FontAsset>("Fonts & Materials/LiberationSans SDF");
            label.fontSize = fontSize;
            label.alignment = alignment;
            label.color = color;
            label.text = text;
            label.textWrappingMode = TextWrappingModes.Normal;
            label.overflowMode = TextOverflowModes.Ellipsis;
            label.raycastTarget = false;
            return label;
        }

        private static void Stretch(RectTransform rt, Vector2 offsetMin, Vector2 offsetMax)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = offsetMin;
            rt.offsetMax = offsetMax;
        }

        private static bool HasPrefabFrame(GameObject slot)
            => slot.transform.Find("Frame") != null;

        private static void ConfigureButtonFeedback(GameObject cell)
        {
            Button button = ArenaUiKit.EnsureComponent<Button>(cell);
            button.onClick.RemoveAllListeners();
            ColorBlock colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1f, 1f, 1f, 0.86f);
            colors.selectedColor = colors.highlightedColor;
            colors.pressedColor = new Color(0.78f, 0.78f, 0.78f, 1f);
            colors.disabledColor = new Color(1f, 1f, 1f, 0.38f);
            colors.colorMultiplier = 1f;
            button.colors = colors;
        }
    }
}
