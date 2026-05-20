#nullable enable
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using Arena.Match;
using Arena.Entity;
using Arena.Network;
using SpacetimeDB;
using SpacetimeDB.Types;

namespace Arena.UI
{
    /// <summary>
    /// Full-screen overlay shown when an arena match ends.
    /// Displays the winner's name. Hidden at all other times.
    ///
    /// Refreshed by MatchController when match phase changes.
    ///
    /// INVARIANT: No network writes. Read-only presentation.
    /// </summary>
    public class MatchOverlay : MonoBehaviour
    {
        public static MatchOverlay? Instance { get; private set; }

        private GameObject _root = null!;
        private GameObject _panel = null!;
        private Text _line1 = null!;
        private Text _line2 = null!;
        private Text _header = null!;
        private RectTransform _statsRoot = null!;
        private Button _leaveButton = null!;
        private Button _playAgainButton = null!;
        private readonly List<GameObject> _statRows = new();

        // Countdown view
        private GameObject _countdownRoot = null!;
        private Text _countdownText = null!;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (Instance != null) return;
            var go = new GameObject("MatchOverlay");
            DontDestroyOnLoad(go);
            go.AddComponent<MatchOverlay>();
        }

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;

            var canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode   = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 20;

            gameObject.AddComponent<CanvasScaler>().uiScaleMode =
                CanvasScaler.ScaleMode.ScaleWithScreenSize;

            gameObject.AddComponent<GraphicRaycaster>();

            _root = new GameObject("Root");
            _root.transform.SetParent(transform, false);
            var rt = _root.AddComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.sizeDelta = Vector2.zero;

            var bg = _root.AddComponent<Image>();
            bg.color = new Color(0f, 0f, 0f, 0.55f);
            bg.raycastTarget = false;

            _panel = new GameObject("Panel");
            _panel.transform.SetParent(_root.transform, false);
            var panelRt = _panel.AddComponent<RectTransform>();
            panelRt.anchorMin = new Vector2(0.5f, 0.5f);
            panelRt.anchorMax = new Vector2(0.5f, 0.5f);
            panelRt.pivot = new Vector2(0.5f, 0.5f);
            panelRt.sizeDelta = new Vector2(720f, 460f);
            panelRt.anchoredPosition = Vector2.zero;

            var panelBg = _panel.AddComponent<Image>();
            panelBg.color = new Color(0.08f, 0.09f, 0.11f, 0.96f);
            panelBg.raycastTarget = true;

            _line1 = MakeLabel("Line1", 42, new Vector2(0.5f, 0.86f), _panel.transform, new Vector2(620f, 60f));
            _line2 = MakeLabel("Line2", 24, new Vector2(0.5f, 0.76f), _panel.transform, new Vector2(620f, 44f));
            _header = MakeLabel("Header", 18, new Vector2(0.5f, 0.64f), _panel.transform, new Vector2(620f, 30f));

            var stats = new GameObject("Stats");
            stats.transform.SetParent(_panel.transform, false);
            _statsRoot = stats.AddComponent<RectTransform>();
            _statsRoot.anchorMin = new Vector2(0.5f, 0.5f);
            _statsRoot.anchorMax = new Vector2(0.5f, 0.5f);
            _statsRoot.pivot = new Vector2(0.5f, 0.5f);
            _statsRoot.sizeDelta = new Vector2(620f, 180f);
            _statsRoot.anchoredPosition = new Vector2(0f, -10f);

            _leaveButton = MakeButton("LeaveButton", "Leave Match", new Vector2(0.5f, 0.12f));
            _leaveButton.transform.SetParent(_panel.transform, false);
            var buttonRt = (RectTransform)_leaveButton.transform;
            buttonRt.anchorMin = new Vector2(0.35f, 0.12f);
            buttonRt.anchorMax = new Vector2(0.35f, 0.12f);
            buttonRt.pivot = new Vector2(0.5f, 0.5f);
            buttonRt.sizeDelta = new Vector2(200f, 42f);
            buttonRt.anchoredPosition = Vector2.zero;
            _leaveButton.onClick.AddListener(OnLeaveMatchPressed);

            _playAgainButton = MakeButton("PlayAgainButton", "Play Again", new Vector2(0.65f, 0.12f));
            _playAgainButton.transform.SetParent(_panel.transform, false);
            var playAgainRt = (RectTransform)_playAgainButton.transform;
            playAgainRt.anchorMin = new Vector2(0.65f, 0.12f);
            playAgainRt.anchorMax = new Vector2(0.65f, 0.12f);
            playAgainRt.pivot = new Vector2(0.5f, 0.5f);
            playAgainRt.sizeDelta = new Vector2(200f, 42f);
            playAgainRt.anchoredPosition = Vector2.zero;
            _playAgainButton.onClick.AddListener(OnPlayAgainPressed);

            // Countdown overlay — shown during pre-match countdown, hidden otherwise.
            _countdownRoot = new GameObject("CountdownRoot");
            _countdownRoot.transform.SetParent(transform, false);
            var cdCanvas = _countdownRoot.AddComponent<Canvas>();
            cdCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            cdCanvas.sortingOrder = 19;
            _countdownRoot.AddComponent<CanvasScaler>().uiScaleMode =
                CanvasScaler.ScaleMode.ScaleWithScreenSize;
            _countdownText = MakeLabel("CountdownText", 120, new Vector2(0.5f, 0.5f), _countdownRoot.transform, new Vector2(400f, 200f));
            _countdownText.alignment = TextAnchor.MiddleCenter;
            _countdownText.color = Color.white;
            _countdownRoot.SetActive(false);

            _root.SetActive(false);
            Refresh(MatchStateCache.Instance);
        }

        public void Refresh(MatchStateCache cache)
        {
            // Countdown view: shown during pre-match countdown.
            if (cache.IsCountdown && cache.IsArenaMode)
            {
                _root.SetActive(false);
                _countdownRoot.SetActive(true);
                return;
            }
            _countdownRoot.SetActive(false);

            if (!cache.IsEnded || !cache.IsArenaMode)
            {
                _root.SetActive(false);
                return;
            }

            _line1.text = "MATCH OVER";

            string winnerName = "Unknown";
            if (cache.WinnerId.HasValue &&
                EntityRegistry.Instance != null &&
                EntityRegistry.Instance.TryGetEntity(cache.WinnerId.Value, out var winner))
            {
                winnerName = string.IsNullOrEmpty(winner.Username) ? "???" : winner.Username;
            }
            _line2.text = $"{winnerName} wins!";
            _header.text = $"{"Damage",10}  {"Kills",6}  {"HP",6}  {"Healing",8}";
            RebuildStats(cache);
            bool connected = NetworkManager.Instance?.IsConnected == true && cache.LocalInstanceId.HasValue;
            _leaveButton.interactable = connected;
            _playAgainButton.interactable = connected;

            _root.SetActive(true);
        }

        private void Update()
        {
            if (!_countdownRoot.activeSelf) return;
            var cache = MatchStateCache.Instance;
            if (!cache.IsCountdown || !cache.CountdownStartedAt.HasValue)
            {
                _countdownRoot.SetActive(false);
                return;
            }
            float elapsed = (float)(System.DateTime.UtcNow - cache.CountdownStartedAt.Value).TotalSeconds;
            float remaining = 3f - elapsed;
            if (remaining <= 0f)
            {
                _countdownText.text = "FIGHT!";
            }
            else
            {
                _countdownText.text = Mathf.CeilToInt(remaining).ToString();
            }
        }

        private void RebuildStats(MatchStateCache cache)
        {
            foreach (var row in _statRows)
                Destroy(row);
            _statRows.Clear();

            if (!cache.LocalInstanceId.HasValue)
                return;

            var rows = BuildRows(cache.LocalInstanceId.Value, cache.WinnerId);
            for (int i = 0; i < rows.Count; i++)
            {
                var row = MakeStatsRow(rows[i], i);
                _statRows.Add(row);
            }
        }

        private List<StatsRowData> BuildRows(ulong instanceId, Identity? winnerId)
        {
            var result = new List<StatsRowData>();
            var statsByPlayer = new Dictionary<Identity, MatchParticipantStats>();
            var conn = NetworkManager.Instance?.Conn;
            if (conn != null)
            {
                foreach (var stats in conn.Db.MatchParticipantStats.InstanceId.Filter(instanceId))
                    statsByPlayer[stats.PlayerId] = stats;
            }

            var seen = new HashSet<Identity>();
            if (EntityRegistry.Instance != null)
            {
                foreach (var entity in EntityRegistry.Instance.AllPlayers)
                {
                    seen.Add(entity.Identity);
                    statsByPlayer.TryGetValue(entity.Identity, out var stats);
                    result.Add(new StatsRowData(
                        string.IsNullOrEmpty(entity.Username) ? ShortIdentity(entity.Identity) : entity.Username,
                        stats?.DamageDone ?? 0,
                        stats?.Kills ?? 0,
                        stats?.HpRemaining ?? 0,
                        stats?.HealingDone ?? 0,
                        winnerId.HasValue && entity.Identity == winnerId.Value));
                }
            }

            foreach (var stats in statsByPlayer.Values)
            {
                if (seen.Contains(stats.PlayerId))
                    continue;

                result.Add(new StatsRowData(
                    ShortIdentity(stats.PlayerId),
                    stats.DamageDone,
                    stats.Kills,
                    stats.HpRemaining,
                    stats.HealingDone,
                    winnerId.HasValue && stats.PlayerId == winnerId.Value));
            }

            result.Sort((a, b) =>
            {
                int killCmp = b.Kills.CompareTo(a.Kills);
                if (killCmp != 0) return killCmp;
                int damageCmp = b.DamageDone.CompareTo(a.DamageDone);
                if (damageCmp != 0) return damageCmp;
                return string.CompareOrdinal(a.Name, b.Name);
            });

            return result;
        }

        private GameObject MakeStatsRow(StatsRowData data, int index)
        {
            var go = new GameObject($"Row_{index}");
            go.transform.SetParent(_statsRoot, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 1f);
            rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.sizeDelta = new Vector2(620f, 32f);
            rt.anchoredPosition = new Vector2(0f, -index * 36f);

            var bg = go.AddComponent<Image>();
            bg.color = data.IsWinner
                ? new Color(0.22f, 0.28f, 0.14f, 0.95f)
                : new Color(0.15f, 0.16f, 0.18f, 0.95f);
            bg.raycastTarget = false;

            var txt = MakeLabel(
                $"RowText_{index}",
                18,
                new Vector2(0.5f, 0.5f),
                go.transform,
                new Vector2(590f, 28f));
            txt.alignment = TextAnchor.MiddleLeft;
            txt.text = $"{data.Name,-14}  {data.DamageDone,10}  {data.Kills,6}  {data.HpRemaining,6}  {data.HealingDone,8}";
            txt.color = data.IsWinner ? new Color(1f, 0.95f, 0.7f) : Color.white;

            return go;
        }

        private void OnLeaveMatchPressed()
        {
            var conn = NetworkManager.Instance?.Conn;
            if (conn == null)
                return;

            _leaveButton.interactable = false;
            _playAgainButton.interactable = false;
            conn.Reducers.LeaveInstance();
        }

        private void OnPlayAgainPressed()
        {
            var conn = NetworkManager.Instance?.Conn;
            if (conn == null)
                return;

            _leaveButton.interactable = false;
            _playAgainButton.interactable = false;
            conn.Reducers.LeaveInstance();
            // LobbyController will show automatically once LocalInstanceId clears.
        }

        private static string ShortIdentity(Identity identity)
        {
            string text = identity.ToString();
            return text.Length > 8 ? $"Player_{text[..8]}" : $"Player_{text}";
        }

        private Text MakeLabel(string name, int fontSize, Vector2 anchorPos, Transform? parentOverride = null, Vector2? size = null)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parentOverride ?? _root.transform, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin        = anchorPos;
            rt.anchorMax        = anchorPos;
            rt.pivot            = new Vector2(0.5f, 0.5f);
            rt.sizeDelta        = size ?? new Vector2(600, 60);
            rt.anchoredPosition = Vector2.zero;

            var txt = go.AddComponent<Text>();
            txt.font      = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            txt.fontSize  = fontSize;
            txt.fontStyle = FontStyle.Bold;
            txt.alignment = TextAnchor.MiddleCenter;
            txt.color     = Color.white;

            var sh = go.AddComponent<Shadow>();
            sh.effectColor    = Color.black;
            sh.effectDistance = new Vector2(2, -2);

            return txt;
        }

        private Button MakeButton(string name, string label, Vector2 anchorPos)
        {
            var go = new GameObject(name);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = anchorPos;
            rt.anchorMax = anchorPos;
            rt.pivot = new Vector2(0.5f, 0.5f);

            var image = go.AddComponent<Image>();
            image.color = new Color(0.76f, 0.22f, 0.18f, 1f);

            var button = go.AddComponent<Button>();
            var colors = button.colors;
            colors.normalColor = image.color;
            colors.highlightedColor = new Color(0.88f, 0.3f, 0.24f, 1f);
            colors.pressedColor = new Color(0.58f, 0.15f, 0.12f, 1f);
            colors.selectedColor = colors.highlightedColor;
            button.colors = colors;

            var txt = MakeLabel($"{name}_Label", 18, new Vector2(0.5f, 0.5f), go.transform, new Vector2(180f, 28f));
            txt.text = label;
            txt.alignment = TextAnchor.MiddleCenter;
            txt.color = Color.white;

            return button;
        }

        private readonly struct StatsRowData
        {
            public StatsRowData(string name, int damageDone, int kills, int hpRemaining, int healingDone, bool isWinner)
            {
                Name = name;
                DamageDone = damageDone;
                Kills = kills;
                HpRemaining = hpRemaining;
                HealingDone = healingDone;
                IsWinner = isWinner;
            }

            public string Name { get; }
            public int DamageDone { get; }
            public int Kills { get; }
            public int HpRemaining { get; }
            public int HealingDone { get; }
            public bool IsWinner { get; }
        }
    }
}
