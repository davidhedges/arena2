#nullable enable

using Arena.Debugging;
using Arena.Network;
using UnityEngine;
using UnityEngine.UI;

namespace Arena.UI
{
    /// <summary>
    /// Always-on connection feedback (feel audit F2 contract item 4): a small
    /// corner dot classified from data the client already collects
    /// (<see cref="ArenaServerClock"/> precise-RTT percentiles plus
    /// row-receipt staleness derived from
    /// <see cref="NetcodeReceiveCounters.TotalRows"/>), and a disconnect
    /// banner with a Reconnect button that promotes
    /// <c>NetworkManager.ReconnectToSelectedEnvironment()</c> — the
    /// environment overlay's existing action — to production UI. No new
    /// sampling, no new network traffic; detailed numbers stay in
    /// <c>NetcodeDebugOverlay</c>. Gameplay reads nothing from this.
    /// </summary>
    internal sealed class ConnectionStatusHud : MonoBehaviour
    {
        private const float RefreshIntervalSeconds = 0.25f;

        private static readonly Color GoodColor = new(0.25f, 0.8f, 0.35f);
        private static readonly Color DegradedColor = new(0.95f, 0.75f, 0.2f);
        private static readonly Color BadColor = new(0.9f, 0.25f, 0.2f);
        private static readonly Color DisconnectedColor = new(0.45f, 0.45f, 0.45f, 0.9f);

        private Image _dot = null!;
        private GameObject _banner = null!;
        private Text _bannerLabel = null!;
        private bool _hasEverConnected;
        private bool _wasConnected;
        private long _lastTotalRows = -1L;
        private float _lastRowChangeRealtime;
        private float _nextRefreshRealtime;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (FindAnyObjectByType<ConnectionStatusHud>() != null)
                return;
            if (!ArenaRuntimeSceneGate.ShouldRunArenaRuntimeInActiveScene())
                return;

            var go = new GameObject("ConnectionStatusHud");
            DontDestroyOnLoad(go);
            go.AddComponent<ConnectionStatusHud>();
        }

        private void Awake()
        {
            RuntimeUiEventSystem.Ensure();

            var canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 40;

            var scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            gameObject.AddComponent<GraphicRaycaster>();

            BuildDot();
            BuildBanner();
        }

        private void BuildDot()
        {
            var go = new GameObject("QualityDot");
            go.transform.SetParent(transform, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(1f, 0f);
            rt.anchorMax = new Vector2(1f, 0f);
            rt.pivot = new Vector2(1f, 0f);
            rt.sizeDelta = new Vector2(14f, 14f);
            rt.anchoredPosition = new Vector2(-18f, 18f);

            _dot = go.AddComponent<Image>();
            // Unity 6 no longer ships UI/Skin/Knob.psd. A null Image sprite
            // uses the built-in white texture and avoids an error on every
            // Play-mode startup; at 14 px the solid marker remains legible.
            _dot.sprite = null;
            _dot.color = DisconnectedColor;
            _dot.raycastTarget = false;
        }

        private void BuildBanner()
        {
            _banner = new GameObject("DisconnectBanner");
            _banner.transform.SetParent(transform, false);
            var rt = _banner.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 1f);
            rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.sizeDelta = new Vector2(480f, 54f);
            rt.anchoredPosition = new Vector2(0f, -18f);

            var bg = _banner.AddComponent<Image>();
            bg.color = new Color(0.08f, 0.03f, 0.03f, 0.85f);
            bg.raycastTarget = true;

            var outline = _banner.AddComponent<Outline>();
            outline.effectColor = BadColor;
            outline.effectDistance = new Vector2(1f, 1f);

            var labelGo = new GameObject("Text");
            labelGo.transform.SetParent(_banner.transform, false);
            var labelRt = labelGo.AddComponent<RectTransform>();
            labelRt.anchorMin = new Vector2(0f, 0f);
            labelRt.anchorMax = new Vector2(1f, 1f);
            labelRt.sizeDelta = new Vector2(-150f, 0f);
            labelRt.anchoredPosition = new Vector2(-65f, 0f);

            _bannerLabel = labelGo.AddComponent<Text>();
            _bannerLabel.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            _bannerLabel.fontSize = 18;
            _bannerLabel.fontStyle = FontStyle.Bold;
            _bannerLabel.alignment = TextAnchor.MiddleCenter;
            _bannerLabel.color = new Color(1f, 0.85f, 0.8f);
            _bannerLabel.text = "Disconnected from server";
            _bannerLabel.raycastTarget = false;

            var shadow = labelGo.AddComponent<Shadow>();
            shadow.effectColor = new Color(0f, 0f, 0f, 0.8f);
            shadow.effectDistance = new Vector2(1f, -1f);

            var buttonGo = new GameObject("Reconnect");
            buttonGo.transform.SetParent(_banner.transform, false);
            var buttonRt = buttonGo.AddComponent<RectTransform>();
            buttonRt.anchorMin = new Vector2(1f, 0.5f);
            buttonRt.anchorMax = new Vector2(1f, 0.5f);
            buttonRt.pivot = new Vector2(1f, 0.5f);
            buttonRt.sizeDelta = new Vector2(110f, 32f);
            buttonRt.anchoredPosition = new Vector2(-12f, 0f);

            var buttonImage = buttonGo.AddComponent<Image>();
            buttonImage.color = new Color(0.55f, 0.16f, 0.12f);
            buttonImage.raycastTarget = true;

            var button = buttonGo.AddComponent<Button>();
            var colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.15f, 1.15f, 1.15f, 1f);
            colors.pressedColor = new Color(0.78f, 0.78f, 0.78f, 1f);
            colors.disabledColor = new Color(0.35f, 0.35f, 0.35f, 0.55f);
            button.colors = colors;
            button.onClick.AddListener(OnReconnectClicked);

            var buttonLabelGo = new GameObject("Label");
            buttonLabelGo.transform.SetParent(buttonGo.transform, false);
            var buttonLabelRt = buttonLabelGo.AddComponent<RectTransform>();
            buttonLabelRt.anchorMin = Vector2.zero;
            buttonLabelRt.anchorMax = Vector2.one;
            buttonLabelRt.sizeDelta = Vector2.zero;
            buttonLabelRt.anchoredPosition = Vector2.zero;

            var buttonLabel = buttonLabelGo.AddComponent<Text>();
            buttonLabel.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            buttonLabel.fontSize = 14;
            buttonLabel.fontStyle = FontStyle.Bold;
            buttonLabel.alignment = TextAnchor.MiddleCenter;
            buttonLabel.color = Color.white;
            buttonLabel.text = "Reconnect";
            buttonLabel.raycastTarget = false;

            _banner.SetActive(false);
        }

        private static void OnReconnectClicked()
        {
            NetworkManager manager = NetworkManager.Instance;
            if (manager != null)
                manager.ReconnectToSelectedEnvironment();
        }

        private void Update()
        {
            float now = Time.realtimeSinceStartup;

            long totalRows = NetcodeReceiveCounters.TotalRows;
            if (totalRows != _lastTotalRows)
            {
                _lastTotalRows = totalRows;
                _lastRowChangeRealtime = now;
            }

            if (now < _nextRefreshRealtime)
                return;
            _nextRefreshRealtime = now + RefreshIntervalSeconds;

            NetworkManager manager = NetworkManager.Instance;
            bool connected = manager != null && manager.IsConnected;
            bool incompatible = manager != null
                                && !string.IsNullOrWhiteSpace(manager.ContractCompatibilityError);
            if (connected && !_wasConnected)
            {
                _hasEverConnected = true;
                // Fresh session: don't count pre-connect silence as staleness.
                _lastRowChangeRealtime = now;
            }
            _wasConnected = connected;

            bool bannerVisible = incompatible || (_hasEverConnected && !connected);
            if (_banner.activeSelf != bannerVisible)
                _banner.SetActive(bannerVisible);

            _bannerLabel.text = incompatible
                ? "Incompatible client/server data — update client or server"
                : "Disconnected from server";

            if (!connected)
            {
                _dot.color = incompatible ? BadColor : DisconnectedColor;
                return;
            }

            bool hasRtt = ArenaServerClock.TryGetRttStats(out _, out long p50Ms, out long p95Ms);
            double stalenessSeconds = now - _lastRowChangeRealtime;
            ConnectionQualityLevel level =
                ConnectionQualityModel.Classify(hasRtt, p50Ms, p95Ms, stalenessSeconds);
            _dot.color = level switch
            {
                ConnectionQualityLevel.Bad => BadColor,
                ConnectionQualityLevel.Degraded => DegradedColor,
                _ => GoodColor,
            };
        }
    }
}
