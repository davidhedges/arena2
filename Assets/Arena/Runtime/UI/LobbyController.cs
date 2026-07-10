#nullable enable
using System.Collections.Generic;
using TMPro;
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
    /// A small "Lobby" toggle button is shown in the bottom-left when eligible (connected, not in
    /// an active match). Clicking it opens/closes the panel. Panel starts closed.
    ///
    /// INVARIANT: No server writes except via button callbacks. Polls in Update().
    /// </summary>
    public class LobbyController : MonoBehaviour
    {
        private const float RowHeight = 36f;
        private const float RowPitch = 40f;

        public static LobbyController? Instance { get; private set; }

        private GameObject _root = null!;
        private ArenaWindow _window = null!;
        private RectTransform _listContent = null!;
        private ArenaButtonHandle _createButton;
        private ArenaButtonHandle _startButton;
        private TextMeshProUGUI _statusText = null!;
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
            ArenaUiKit.MakeOverlayCanvas(_root, 10);

            // Toggle button — bottom-left corner, always visible when root is active.
            ArenaButtonHandle toggle = ArenaUiKit.MakeButton(
                _root.transform,
                "ToggleBtn",
                "Lobby",
                ArenaButtonStyle.Ghost,
                OnTogglePressed);
            RectTransform toggleRect = toggle.Rect;
            toggleRect.anchorMin = Vector2.zero;
            toggleRect.anchorMax = Vector2.zero;
            toggleRect.pivot = Vector2.zero;
            toggleRect.sizeDelta = new Vector2(90f, 32f);
            toggleRect.anchoredPosition = new Vector2(10f, 10f);

            // Instance browser window — anchored bottom-left, above the toggle button,
            // so it never overlaps the bottom HUD.
            _window = ArenaWindow.Create(_root.transform, "LobbyWindow", "Lobby", new Vector2(520f, 340f));
            RectTransform windowRect = _window.Rect;
            windowRect.anchorMin = Vector2.zero;
            windowRect.anchorMax = Vector2.zero;
            windowRect.pivot = Vector2.zero;
            windowRect.anchoredPosition = new Vector2(10f, 50f);
            _window.CloseRequested += () => _panelOpen = false;

            RectTransform footer = _window.AddFooter();

            // Status line above the instance list.
            _statusText = ArenaUiKit.MakeText(
                _window.Content,
                "Status",
                "Connecting…",
                ArenaUiTheme.BodySize,
                ArenaUiTheme.MutedText);
            ArenaUiKit.SetAnchors(
                _statusText.rectTransform,
                new Vector2(0f, 1f),
                new Vector2(1f, 1f),
                new Vector2(0f, -22f),
                Vector2.zero);

            RectTransform divider = ArenaUiKit.MakeDivider(_window.Content);
            ArenaUiKit.SetAnchors(
                divider,
                new Vector2(0f, 1f),
                new Vector2(1f, 1f),
                new Vector2(0f, -30f),
                new Vector2(0f, -29f));

            // Instance list — scrolls when it outgrows the window.
            _listContent = ArenaUiKit.MakeScrollView(_window.Content, "InstanceList", out ScrollRect scrollRect);
            ArenaUiKit.SetAnchors(
                (RectTransform)scrollRect.transform,
                Vector2.zero,
                Vector2.one,
                Vector2.zero,
                new Vector2(0f, -38f));

            // Action buttons in the window footer.
            _createButton = ArenaUiKit.MakeButton(
                footer,
                "CreateBtn",
                "Create Match",
                ArenaButtonStyle.Primary,
                OnCreatePressed);
            RectTransform createRect = _createButton.Rect;
            createRect.anchorMin = new Vector2(0f, 0.5f);
            createRect.anchorMax = new Vector2(0f, 0.5f);
            createRect.pivot = new Vector2(0f, 0.5f);
            createRect.sizeDelta = new Vector2(160f, ArenaUiTheme.ButtonHeight);
            createRect.anchoredPosition = new Vector2(ArenaUiTheme.ContentPadding, 0f);

            _startButton = ArenaUiKit.MakeButton(
                footer,
                "StartBtn",
                "Start Match",
                ArenaButtonStyle.Primary,
                OnStartPressed);
            RectTransform startRect = _startButton.Rect;
            startRect.anchorMin = new Vector2(1f, 0.5f);
            startRect.anchorMax = new Vector2(1f, 0.5f);
            startRect.pivot = new Vector2(1f, 0.5f);
            startRect.sizeDelta = new Vector2(160f, ArenaUiTheme.ButtonHeight);
            startRect.anchoredPosition = new Vector2(-ArenaUiTheme.ContentPadding, 0f);
            _startButton.GameObject.SetActive(false);

            _window.SetVisible(false, instant: true);
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
                _window.SetVisible(false, instant: true);
                return;
            }

            _window.SetVisible(_panelOpen);
            if (!_panelOpen) return;

            bool connected = conn != null;
            _createButton.SetInteractable(connected && !cache.LocalInstanceId.HasValue);

            bool inWaitingInstance = cache.LocalInstanceId.HasValue && cache.Phase == MatchPhase.Waiting;
            _startButton.GameObject.SetActive(inWaitingInstance);
            if (inWaitingInstance)
                _startButton.SetInteractable(connected);

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

            _window.SetSubtitle(instances.Count == 1 ? "1 instance" : $"{instances.Count} instances");

            foreach (var row in _instanceRows)
                Destroy(row);
            _instanceRows.Clear();

            _listContent.sizeDelta = new Vector2(0f, Mathf.Max(1, instances.Count) * RowPitch);

            if (instances.Count == 0)
            {
                TextMeshProUGUI empty = ArenaUiKit.MakeText(
                    _listContent,
                    "EmptyRow",
                    "No open instances. Press Create Match to start one.",
                    ArenaUiTheme.SmallSize,
                    ArenaUiTheme.MutedText,
                    alignment: TextAlignmentOptions.Center);
                ArenaUiKit.SetAnchors(
                    empty.rectTransform,
                    new Vector2(0f, 1f),
                    new Vector2(1f, 1f),
                    new Vector2(0f, -RowHeight),
                    Vector2.zero);
                _instanceRows.Add(empty.gameObject);
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
            Color rowColor = isLocal
                ? ArenaUiTheme.PositiveRow
                : (index % 2 == 0 ? ArenaUiTheme.Row : ArenaUiTheme.RowAlt);
            RectTransform row = ArenaUiKit.MakePanel(_listContent, $"InstRow_{inst.Id}", rowColor);
            row.anchorMin = new Vector2(0f, 1f);
            row.anchorMax = new Vector2(1f, 1f);
            row.pivot = new Vector2(0.5f, 1f);
            row.sizeDelta = new Vector2(0f, RowHeight);
            row.anchoredPosition = new Vector2(0f, -index * RowPitch);

            TextMeshProUGUI label = ArenaUiKit.MakeText(
                row,
                "Label",
                $"#{inst.Id}  [{inst.PlayerCount}/{inst.MaxPlayers}]  {inst.Phase}",
                ArenaUiTheme.BodySize);
            ArenaUiKit.SetAnchors(
                label.rectTransform,
                Vector2.zero,
                new Vector2(0.72f, 1f),
                new Vector2(10f, 0f),
                Vector2.zero);

            if (!isLocal)
            {
                bool canJoin = string.Equals(inst.Phase, "WAITING", System.StringComparison.OrdinalIgnoreCase)
                               && inst.PlayerCount < inst.MaxPlayers;
                var capturedId = inst.Id;
                ArenaButtonHandle joinButton = ArenaUiKit.MakeButton(
                    row,
                    "JoinBtn",
                    "Join",
                    ArenaButtonStyle.Secondary,
                    () => OnJoinPressed(capturedId),
                    textSize: ArenaUiTheme.SmallSize);
                RectTransform joinRect = joinButton.Rect;
                joinRect.anchorMin = new Vector2(1f, 0.5f);
                joinRect.anchorMax = new Vector2(1f, 0.5f);
                joinRect.pivot = new Vector2(1f, 0.5f);
                joinRect.sizeDelta = new Vector2(72f, 28f);
                joinRect.anchoredPosition = new Vector2(-6f, 0f);
                joinButton.SetInteractable(canJoin && NetworkManager.Instance?.IsConnected == true);
            }

            return row.gameObject;
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
    }
}
