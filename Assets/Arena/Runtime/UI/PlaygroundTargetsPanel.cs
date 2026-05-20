#nullable enable
using Arena.Network;
using UnityEngine;
using UnityEngine.UI;

namespace Arena.UI
{
    /// <summary>
    /// Playground-only target spawner for testing targeting rules and party frames.
    /// This is intentionally separate from normal HUD, party, and match UI.
    /// </summary>
    public sealed class PlaygroundTargetsPanel : MonoBehaviour
    {
        private const float Pad = 10f;
        private const float ButtonTopOffset = 130f;
        private const float MenuTopOffset = 160f;
        private const string KindHostile = "HOSTILE";
        private const string KindNeutral = "NEUTRAL";
        private const string KindPartyMember = "PARTY_MEMBER";

        private GameObject _menuRoot = null!;

        private void Awake()
        {
            Build();
        }

        private void Build()
        {
            var toggleButton = MakeHudButton(transform, "PlaygroundToggleButton", "PLAYGROUND",
                new Vector2(1f, 1f), new Vector2(1f, 1f),
                new Vector2(122f, 24f), new Vector2(-Pad, -ButtonTopOffset),
                new Color(0.16f, 0.10f, 0.24f, 0.94f));
            toggleButton.onClick.AddListener(ToggleMenu);

            _menuRoot = Panel("PlaygroundTargetsMenu", transform,
                new Vector2(1f, 1f), new Vector2(1f, 1f),
                new Vector2(190f, 150f), new Vector2(-Pad, -MenuTopOffset));
            Img(_menuRoot, new Color(0.025f, 0.025f, 0.035f, 0.92f));
            var outline = _menuRoot.AddComponent<Outline>();
            outline.effectColor = new Color(0.38f, 0.22f, 0.52f, 0.95f);
            outline.effectDistance = new Vector2(1f, 1f);

            var title = Label(_menuRoot.transform, "Title",
                new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                new Vector2(174f, 22f), new Vector2(0f, -8f),
                11, new Color(0.86f, 0.72f, 1f), TextAnchor.MiddleCenter);
            title.text = "PLAYGROUND TARGETS";
            title.fontStyle = FontStyle.Bold;

            BuildTargetButton("HostileButton", "HOSTILE", 0, () => SpawnTarget(KindHostile));
            BuildTargetButton("NeutralButton", "NEUTRAL", 1, () => SpawnTarget(KindNeutral));
            BuildTargetButton("PartyMemberButton", "PARTY MEMBER", 2, () => SpawnTarget(KindPartyMember));
            BuildTargetButton("ClearButton", "CLEAR", 3, ClearTargets);

            _menuRoot.SetActive(false);
        }

        private void BuildTargetButton(string name, string label, int row, UnityEngine.Events.UnityAction action)
        {
            var button = MakeHudButton(_menuRoot.transform, name, label,
                new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                new Vector2(160f, 24f), new Vector2(0f, -36f - row * 28f),
                row == 3
                    ? new Color(0.28f, 0.08f, 0.10f, 0.94f)
                    : new Color(0.12f, 0.13f, 0.18f, 0.94f));
            button.onClick.AddListener(action);
        }

        private void ToggleMenu()
        {
            _menuRoot.SetActive(!_menuRoot.activeSelf);
        }

        private static void SpawnTarget(string kind)
        {
            NetworkManager.Instance?.Conn?.Reducers.SpawnPlaygroundTarget(kind);
        }

        private static void ClearTargets()
        {
            NetworkManager.Instance?.Conn?.Reducers.DespawnAllPlaygroundTargets();
        }

        private static GameObject Panel(
            string name,
            Transform parent,
            Vector2 anchor,
            Vector2 pivot,
            Vector2 size,
            Vector2 offset)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = anchor;
            rt.anchorMax = anchor;
            rt.pivot = pivot;
            rt.sizeDelta = size;
            rt.anchoredPosition = offset;
            return go;
        }

        private static void Img(GameObject go, Color color)
        {
            var img = go.AddComponent<Image>();
            img.color = color;
            img.raycastTarget = false;
        }

        private static Text Label(
            Transform parent,
            string name,
            Vector2 anchor,
            Vector2 pivot,
            Vector2 size,
            Vector2 offset,
            int fontSize,
            Color color,
            TextAnchor alignment)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = anchor;
            rt.anchorMax = anchor;
            rt.pivot = pivot;
            rt.sizeDelta = size;
            rt.anchoredPosition = offset;

            var txt = go.AddComponent<Text>();
            txt.font = Font();
            txt.fontSize = fontSize;
            txt.alignment = alignment;
            txt.color = color;
            txt.raycastTarget = false;

            var shadow = go.AddComponent<Shadow>();
            shadow.effectColor = new Color(0f, 0f, 0f, 0.8f);
            shadow.effectDistance = new Vector2(1f, -1f);
            return txt;
        }

        private static Button MakeHudButton(
            Transform parent,
            string name,
            string text,
            Vector2 anchor,
            Vector2 pivot,
            Vector2 size,
            Vector2 offset,
            Color fill)
        {
            var go = Panel(name, parent, anchor, pivot, size, offset);
            var image = go.AddComponent<Image>();
            image.color = fill;
            image.raycastTarget = true;

            var button = go.AddComponent<Button>();
            var colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.15f, 1.15f, 1.15f, 1f);
            colors.pressedColor = new Color(0.78f, 0.78f, 0.78f, 1f);
            colors.disabledColor = new Color(0.35f, 0.35f, 0.35f, 0.55f);
            button.colors = colors;

            var label = Label(go.transform, "Label",
                Vector2.zero, Vector2.zero, Vector2.zero, Vector2.zero,
                10, Color.white, TextAnchor.MiddleCenter);
            var labelRect = label.GetComponent<RectTransform>();
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.sizeDelta = Vector2.zero;
            labelRect.anchoredPosition = Vector2.zero;
            label.fontStyle = FontStyle.Bold;
            label.text = text;
            label.resizeTextForBestFit = true;
            label.resizeTextMinSize = 7;
            label.resizeTextMaxSize = 10;

            return button;
        }

        private static Font Font() =>
            Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
    }
}
