#nullable enable
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Arena.Match;
using Arena.Network;
using SpacetimeDB;
using SpacetimeDB.Types;

namespace Arena.UI
{
    /// <summary>
    /// Simple lobby screen: lists open arena instances, provides Create / Join / Start buttons.
    /// A small "Lobby" toggle button is shown in the top-left when eligible (connected, not in
    /// an active match). Clicking it opens/closes the panel. Panel starts closed.
    ///
    /// INVARIANT: No server writes except via button callbacks. Polls in Update().
    /// </summary>
    public class LobbyController : MonoBehaviour
    {
        public static LobbyController? Instance { get; private set; }

        private GameObject _root = null!;
        private GameObject _panel = null!;
        private Button _toggleButton = null!;
        private RectTransform _listRoot = null!;
        private Button _createButton = null!;
        private Button _startButton = null!;
        private Text _statusText = null!;
        private readonly List<GameObject> _instanceRows = new();
        private bool _panelOpen;

        // Change detection
        private int _lastRowCount;
        private string _lastPhaseHash = string.Empty;
        private ulong? _lastLocalInstanceId;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (Instance != null) return;
            if (!ArenaRuntimeSceneGate.ShouldRunArenaRuntimeInActiveScene())
                return;

            var go = new GameObject("LobbyController");
            DontDestroyOnLoad(go);
            go.AddComponent<LobbyController>();
        }

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            BuildUI();
        }

        private void BuildUI()
        {
            // Root canvas — full-screen, no background image.
            _root = new GameObject("LobbyRoot");
            _root.transform.SetParent(transform, false);
            var canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 10;
            _root.AddComponent<CanvasScaler>().uiScaleMode =
                CanvasScaler.ScaleMode.ScaleWithScreenSize;
            _root.AddComponent<GraphicRaycaster>();
            var rootRt = _root.GetComponent<RectTransform>();
            rootRt.anchorMin = Vector2.zero;
            rootRt.anchorMax = Vector2.one;
            rootRt.sizeDelta = Vector2.zero;

            // Toggle button — bottom-left corner, always visible when root is active.
            {
                var go = new GameObject("ToggleBtn");
                go.transform.SetParent(_root.transform, false);
                var rt = go.AddComponent<RectTransform>();
                rt.anchorMin = new Vector2(0f, 0f);
                rt.anchorMax = new Vector2(0f, 0f);
                rt.pivot = new Vector2(0f, 0f);
                rt.sizeDelta = new Vector2(90f, 32f);
                rt.anchoredPosition = new Vector2(10f, 10f);
                var img = go.AddComponent<Image>();
                img.color = new Color(0.22f, 0.38f, 0.68f, 1f);
                _toggleButton = go.AddComponent<Button>();
                var colors = _toggleButton.colors;
                colors.normalColor = img.color;
                colors.highlightedColor = new Color(0.3f, 0.5f, 0.82f, 1f);
                colors.pressedColor = new Color(0.15f, 0.28f, 0.5f, 1f);
                _toggleButton.colors = colors;
                _toggleButton.onClick.AddListener(OnTogglePressed);
                var txtGo = new GameObject("Label");
                txtGo.transform.SetParent(go.transform, false);
                var txtRt = txtGo.AddComponent<RectTransform>();
                txtRt.anchorMin = Vector2.zero;
                txtRt.anchorMax = Vector2.one;
                txtRt.sizeDelta = Vector2.zero;
                var txt = txtGo.AddComponent<Text>();
                txt.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                txt.fontSize = 14;
                txt.fontStyle = FontStyle.Bold;
                txt.alignment = TextAnchor.MiddleCenter;
                txt.color = Color.white;
                txt.text = "Lobby";
            }

            // Panel — anchored to top-center so it never overlaps the bottom HUD.
            _panel = new GameObject("Panel");
            _panel.transform.SetParent(_root.transform, false);
            var panelRt = _panel.AddComponent<RectTransform>();
            panelRt.anchorMin = new Vector2(0f, 0f);
            panelRt.anchorMax = new Vector2(0f, 0f);
            panelRt.pivot = new Vector2(0f, 0f);
            panelRt.sizeDelta = new Vector2(520f, 340f);
            panelRt.anchoredPosition = new Vector2(10f, 50f);
            _panel.AddComponent<Image>().color = new Color(0.08f, 0.09f, 0.11f, 0.97f);

            // Header
            var header = MakeLabel("Header", _panel.transform, "ARENA", 32,
                new Vector2(0.5f, 0.92f), new Vector2(460f, 46f));
            header.alignment = TextAnchor.MiddleCenter;

            // Sub-header
            _statusText = MakeLabel("Status", _panel.transform, "Connecting…", 14,
                new Vector2(0.5f, 0.82f), new Vector2(460f, 26f));
            _statusText.alignment = TextAnchor.MiddleCenter;
            _statusText.color = new Color(0.7f, 0.7f, 0.7f, 1f);

            // Instance list area
            var listBg = new GameObject("ListBg");
            listBg.transform.SetParent(_panel.transform, false);
            var listBgRt = listBg.AddComponent<RectTransform>();
            listBgRt.anchorMin = new Vector2(0.5f, 0.5f);
            listBgRt.anchorMax = new Vector2(0.5f, 0.5f);
            listBgRt.pivot = new Vector2(0.5f, 0.5f);
            listBgRt.sizeDelta = new Vector2(480f, 180f);
            listBgRt.anchoredPosition = Vector2.zero;
            listBg.AddComponent<Image>().color = new Color(0.06f, 0.07f, 0.09f, 0.9f);

            var listArea = new GameObject("ListArea");
            listArea.transform.SetParent(listBg.transform, false);
            _listRoot = listArea.AddComponent<RectTransform>();
            _listRoot.anchorMin = new Vector2(0f, 1f);
            _listRoot.anchorMax = new Vector2(1f, 1f);
            _listRoot.pivot = new Vector2(0.5f, 1f);
            _listRoot.sizeDelta = new Vector2(0f, 0f);
            _listRoot.anchoredPosition = new Vector2(0f, -4f);

            // Action buttons
            _createButton = MakeButton("CreateBtn", _panel.transform, "Create Match",
                new Vector2(0.3f, 0.1f), new Vector2(160f, 38f));
            _createButton.onClick.AddListener(OnCreatePressed);

            _startButton = MakeButton("StartBtn", _panel.transform, "Start Match",
                new Vector2(0.7f, 0.1f), new Vector2(160f, 38f));
            _startButton.onClick.AddListener(OnStartPressed);
            _startButton.gameObject.SetActive(false);

            _panel.SetActive(false);
            _root.SetActive(false);
        }

        private void Update()
        {
            var cache = MatchStateCache.Instance;
            var conn = NetworkManager.Instance?.Conn;

            bool eligible = NetworkManager.Instance?.IsConnected == true
                && (!cache.IsArenaMode || cache.Phase == MatchPhase.Waiting)
                && !ShouldSuppressInCurrentScene();

            _root.SetActive(eligible);
            if (!eligible)
            {
                _panelOpen = false;
                _panel.SetActive(false);
                return;
            }

            _panel.SetActive(_panelOpen);
            if (!_panelOpen) return;

            bool connected = conn != null;
            _createButton.interactable = connected && !cache.LocalInstanceId.HasValue;

            bool inWaitingInstance = cache.LocalInstanceId.HasValue && cache.Phase == MatchPhase.Waiting;
            _startButton.gameObject.SetActive(inWaitingInstance);
            if (inWaitingInstance)
                _startButton.interactable = connected;

            _statusText.text = cache.LocalInstanceId.HasValue
                ? $"In instance #{cache.LocalInstanceId.Value} — waiting for players…"
                : "No instance joined.";

            if (conn == null) return;
            RebuildIfChanged(conn, cache);
        }

        private static bool ShouldSuppressInCurrentScene()
        {
            string activeSceneName = SceneManager.GetActiveScene().name;
            return !ArenaRuntimeSceneGate.ShouldRunArenaRuntimeInActiveScene()
                   || string.Equals(activeSceneName, "Hub", System.StringComparison.Ordinal)
                   || string.Equals(activeSceneName, "CharacterCreation", System.StringComparison.Ordinal)
                   || string.Equals(activeSceneName, "CharacterCustomization", System.StringComparison.Ordinal);
        }

        private void OnTogglePressed() => _panelOpen = !_panelOpen;

        private void RebuildIfChanged(DbConnection conn, MatchStateCache cache)
        {
            var instances = new List<ArenaInstance>();
            foreach (var inst in conn.Db.ArenaInstance.Iter())
            {
                if (inst.IsPractice) continue;
                if (string.Equals(inst.Phase, "ENDED", System.StringComparison.OrdinalIgnoreCase)) continue;
                instances.Add(inst);
            }

            var sb = new System.Text.StringBuilder();
            foreach (var inst in instances)
                sb.Append($"{inst.Id}:{inst.Phase}:{inst.PlayerCount};");
            string hash = sb.ToString();

            if (instances.Count == _lastRowCount
                && hash == _lastPhaseHash
                && cache.LocalInstanceId == _lastLocalInstanceId)
            {
                return;
            }

            _lastRowCount = instances.Count;
            _lastPhaseHash = hash;
            _lastLocalInstanceId = cache.LocalInstanceId;

            foreach (var row in _instanceRows)
                Destroy(row);
            _instanceRows.Clear();

            if (instances.Count == 0)
            {
                var empty = MakeRowLabel("No open instances. Press Create Match to start one.", 0);
                _instanceRows.Add(empty);
                return;
            }

            for (int i = 0; i < instances.Count; i++)
            {
                var inst = instances[i];
                bool isLocal = cache.LocalInstanceId == inst.Id;
                _instanceRows.Add(BuildInstanceRow(inst, i, isLocal));
            }
        }

        private GameObject BuildInstanceRow(ArenaInstance inst, int index, bool isLocal)
        {
            var go = new GameObject($"InstRow_{inst.Id}");
            go.transform.SetParent(_listRoot, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.sizeDelta = new Vector2(0f, 36f);
            rt.anchoredPosition = new Vector2(0f, -index * 40f);

            go.AddComponent<Image>().color = isLocal
                ? new Color(0.15f, 0.22f, 0.15f, 0.9f)
                : new Color(0.13f, 0.14f, 0.16f, 0.9f);

            var label = new GameObject("Label");
            label.transform.SetParent(go.transform, false);
            var labelRt = label.AddComponent<RectTransform>();
            labelRt.anchorMin = new Vector2(0f, 0f);
            labelRt.anchorMax = new Vector2(0.72f, 1f);
            labelRt.sizeDelta = Vector2.zero;
            labelRt.anchoredPosition = new Vector2(8f, 0f);
            var labelTxt = label.AddComponent<Text>();
            labelTxt.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            labelTxt.fontSize = 15;
            labelTxt.alignment = TextAnchor.MiddleLeft;
            labelTxt.color = Color.white;
            labelTxt.text = $"#{inst.Id}  [{inst.PlayerCount}/{inst.MaxPlayers}]  {inst.Phase}";

            if (!isLocal)
            {
                bool canJoin = string.Equals(inst.Phase, "WAITING", System.StringComparison.OrdinalIgnoreCase)
                               && inst.PlayerCount < inst.MaxPlayers;
                var joinBtn = MakeSmallButton(go.transform, "Join", new Vector2(0.86f, 0.5f), new Vector2(72f, 28f));
                joinBtn.interactable = canJoin && NetworkManager.Instance?.IsConnected == true;
                var capturedId = inst.Id;
                joinBtn.onClick.AddListener(() => OnJoinPressed(capturedId));
            }

            return go;
        }

        private void OnCreatePressed()
        {
            var conn = NetworkManager.Instance?.Conn;
            if (conn == null) return;
            conn.Reducers.CreateInstance(2);
        }

        private void OnJoinPressed(ulong instanceId)
        {
            var conn = NetworkManager.Instance?.Conn;
            if (conn == null) return;
            conn.Reducers.JoinInstance(instanceId);
        }

        private void OnStartPressed()
        {
            var conn = NetworkManager.Instance?.Conn;
            var instanceId = MatchStateCache.Instance.LocalInstanceId;
            if (conn == null || !instanceId.HasValue) return;
            conn.Reducers.StartMatch(instanceId.Value);
        }

        // ── UI helpers ──────────────────────────────────────────────────────────

        private static Text MakeLabel(string name, Transform parent, string text, int fontSize,
            Vector2 anchor, Vector2 size)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = anchor;
            rt.anchorMax = anchor;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = size;
            rt.anchoredPosition = Vector2.zero;
            var txt = go.AddComponent<Text>();
            txt.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            txt.fontSize = fontSize;
            txt.fontStyle = FontStyle.Bold;
            txt.color = Color.white;
            txt.text = text;
            var sh = go.AddComponent<Shadow>();
            sh.effectColor = Color.black;
            sh.effectDistance = new Vector2(1, -1);
            return txt;
        }

        private static GameObject MakeRowLabel(string text, int index)
        {
            var go = new GameObject("EmptyRow");
            var txt = go.AddComponent<Text>();
            txt.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            txt.fontSize = 14;
            txt.color = new Color(0.6f, 0.6f, 0.6f, 1f);
            txt.alignment = TextAnchor.MiddleCenter;
            txt.text = text;
            return go;
        }

        private static Button MakeButton(string name, Transform parent, string label,
            Vector2 anchor, Vector2 size)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = anchor;
            rt.anchorMax = anchor;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = size;
            rt.anchoredPosition = Vector2.zero;
            var img = go.AddComponent<Image>();
            img.color = new Color(0.22f, 0.48f, 0.22f, 1f);
            var btn = go.AddComponent<Button>();
            var colors = btn.colors;
            colors.normalColor = img.color;
            colors.highlightedColor = new Color(0.3f, 0.6f, 0.3f, 1f);
            colors.pressedColor = new Color(0.15f, 0.35f, 0.15f, 1f);
            btn.colors = colors;
            var txtGo = new GameObject("Label");
            txtGo.transform.SetParent(go.transform, false);
            var txtRt = txtGo.AddComponent<RectTransform>();
            txtRt.anchorMin = Vector2.zero;
            txtRt.anchorMax = Vector2.one;
            txtRt.sizeDelta = Vector2.zero;
            var txt = txtGo.AddComponent<Text>();
            txt.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            txt.fontSize = 16;
            txt.fontStyle = FontStyle.Bold;
            txt.alignment = TextAnchor.MiddleCenter;
            txt.color = Color.white;
            txt.text = label;
            return btn;
        }

        private static Button MakeSmallButton(Transform parent, string label, Vector2 anchor, Vector2 size)
        {
            var go = new GameObject($"Btn_{label}");
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = anchor;
            rt.anchorMax = anchor;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = size;
            rt.anchoredPosition = Vector2.zero;
            var img = go.AddComponent<Image>();
            img.color = new Color(0.22f, 0.38f, 0.68f, 1f);
            var btn = go.AddComponent<Button>();
            var colors = btn.colors;
            colors.normalColor = img.color;
            colors.highlightedColor = new Color(0.3f, 0.5f, 0.82f, 1f);
            colors.pressedColor = new Color(0.15f, 0.28f, 0.5f, 1f);
            btn.colors = colors;
            var txtGo = new GameObject("Label");
            txtGo.transform.SetParent(go.transform, false);
            var txtRt = txtGo.AddComponent<RectTransform>();
            txtRt.anchorMin = Vector2.zero;
            txtRt.anchorMax = Vector2.one;
            txtRt.sizeDelta = Vector2.zero;
            var txt = txtGo.AddComponent<Text>();
            txt.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            txt.fontSize = 13;
            txt.fontStyle = FontStyle.Bold;
            txt.alignment = TextAnchor.MiddleCenter;
            txt.color = Color.white;
            txt.text = label;
            return btn;
        }
    }
}
