#nullable enable

using Arena.World;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Arena.UI
{
    public sealed class OpenWorldTravelOverlay : MonoBehaviour
    {
        private const string HubSceneName = "Hub";

        private GameObject _root = null!;
        private Text _label = null!;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (FindAnyObjectByType<OpenWorldTravelOverlay>() != null)
                return;

            var go = new GameObject("OpenWorldTravelOverlay");
            DontDestroyOnLoad(go);
            go.AddComponent<OpenWorldTravelOverlay>();
        }

        private void Awake()
        {
            RuntimeUiEventSystem.Ensure();
            BuildUi();
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            RuntimeUiEventSystem.Ensure();
        }

        private void Update()
        {
            string activeSceneName = SceneManager.GetActiveScene().name;
            bool show = OpenWorldTravelCatalog.IsRegisteredOpenWorldScene(activeSceneName);
            _root.SetActive(show);
            if (!show)
                return;

            _label.text = OpenWorldTravelCatalog.DisplayNameForScene(activeSceneName);
        }

        private void BuildUi()
        {
            _root = new GameObject("OpenWorldTravelRoot");
            _root.transform.SetParent(transform, false);

            var canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 26;

            var scaler = _root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            _root.AddComponent<GraphicRaycaster>();

            var panel = new GameObject("TravelPanel");
            panel.transform.SetParent(_root.transform, false);
            var panelRt = panel.AddComponent<RectTransform>();
            panelRt.anchorMin = new Vector2(1f, 1f);
            panelRt.anchorMax = new Vector2(1f, 1f);
            panelRt.pivot = new Vector2(1f, 1f);
            panelRt.anchoredPosition = new Vector2(-24f, -24f);
            panelRt.sizeDelta = new Vector2(252f, 92f);
            panel.AddComponent<Image>().color = HeatUiStyle.Panel;
            HeatUiStyle.StylePanel(panel);
            HeatUiStyle.AddAccentBar(
                panel.transform,
                "Accent",
                new Vector2(0f, 0f),
                new Vector2(0f, 1f),
                Vector2.zero,
                new Vector2(3f, 0f));

            _label = MakeLabel("CurrentWorld", panel.transform, 14, TextAnchor.MiddleLeft);
            _label.color = HeatUiStyle.Text;
            SetRect(_label.rectTransform, new Vector2(18f, 52f), new Vector2(216f, 20f));

            var hubButton = MakeButton("HubButton", panel.transform, "Return to Hub");
            SetRect((RectTransform)hubButton.transform, new Vector2(18f, 12f), new Vector2(216f, 30f));
            hubButton.onClick.AddListener(ReturnToHub);

            _root.SetActive(false);
        }

        private static void ReturnToHub()
        {
            SceneManager.LoadScene(HubSceneName);
        }

        private static Text MakeLabel(string name, Transform parent, int fontSize, TextAnchor alignment)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var text = go.AddComponent<Text>();
            text.font = HeatUiStyle.ResolveLegacyFont();
            text.fontSize = fontSize;
            text.alignment = alignment;
            text.color = Color.white;
            return text;
        }

        private static Button MakeButton(string name, Transform parent, string text)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var image = go.AddComponent<Image>();
            image.color = HeatUiStyle.Accent;
            var button = go.AddComponent<Button>();
            var colors = button.colors;
            colors.normalColor = image.color;
            colors.highlightedColor = HeatUiStyle.AccentHot;
            colors.pressedColor = new Color(0.56f, 0.08f, 0.06f, 1f);
            button.colors = colors;
            HeatUiStyle.StyleButton(button, text, HeatUiStyle.Accent, Color.white);

            Text label = MakeLabel("Text", go.transform, 13, TextAnchor.MiddleCenter);
            label.text = text;
            SetStretch(label.rectTransform);
            return button;
        }

        private static void SetRect(RectTransform rt, Vector2 anchoredPosition, Vector2 size)
        {
            rt.anchorMin = new Vector2(0f, 0f);
            rt.anchorMax = new Vector2(0f, 0f);
            rt.pivot = new Vector2(0f, 0f);
            rt.anchoredPosition = anchoredPosition;
            rt.sizeDelta = size;
        }

        private static void SetStretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }
    }
}
