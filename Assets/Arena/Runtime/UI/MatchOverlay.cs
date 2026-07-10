#nullable enable
using System.Collections.Generic;
using TMPro;
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

        private const float StatsRowHeight = 32f;
        private const float StatsRowPitch = 36f;
        private const float ColHealingWidth = 92f;
        private const float ColHpWidth = 60f;
        private const float ColKillsWidth = 60f;
        private const float ColDamageWidth = 96f;
        private const float ColGap = 10f;

        // Right-edge offsets for the right-aligned numeric columns.
        private const float HealingFromRight = 8f;
        private const float HpFromRight = HealingFromRight + ColHealingWidth + ColGap;
        private const float KillsFromRight = HpFromRight + ColHpWidth + ColGap;
        private const float DamageFromRight = KillsFromRight + ColKillsWidth + ColGap;
        private const float NameRightInset = DamageFromRight + ColDamageWidth + ColGap;

        private GameObject _root = null!;
        private ArenaWindow _window = null!;
        private TextMeshProUGUI _winnerLine = null!;
        private RectTransform _statsRoot = null!;
        private ArenaButtonHandle _leaveButton;
        private ArenaButtonHandle _playAgainButton;
        private readonly List<GameObject> _statRows = new();

        // Countdown view
        private GameObject _countdownRoot = null!;
        private TextMeshProUGUI _countdownText = null!;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (Instance != null) return;
            if (!ArenaRuntimeSceneGate.ShouldRunArenaRuntimeInActiveScene())
                return;

            var go = new GameObject("MatchOverlay");
            DontDestroyOnLoad(go);
            go.AddComponent<MatchOverlay>();
        }

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;

            ArenaUiKit.MakeOverlayCanvas(gameObject, 20);

            // Full-screen dim veil behind the match-over window.
            RectTransform veil = ArenaUiKit.MakePanel(transform, "Root", ArenaUiTheme.Veil, raycastTarget: false);
            ArenaUiKit.Fill(veil);
            _root = veil.gameObject;

            _window = ArenaWindow.Create(
                _root.transform,
                "MatchOverWindow",
                "Match Over",
                new Vector2(720f, 460f),
                showCloseButton: false);
            RectTransform footer = _window.AddFooter();

            _winnerLine = ArenaUiKit.MakeText(
                _window.Content,
                "Winner",
                string.Empty,
                20f,
                ArenaUiTheme.Text,
                ArenaUiTheme.TitleFont,
                TextAlignmentOptions.Center);
            ArenaUiKit.SetAnchors(
                _winnerLine.rectTransform,
                new Vector2(0f, 1f),
                new Vector2(1f, 1f),
                new Vector2(0f, -36f),
                Vector2.zero);

            BuildStatsHeader();

            _statsRoot = ArenaUiKit.MakeRect(_window.Content, "Stats");
            ArenaUiKit.SetAnchors(
                _statsRoot,
                Vector2.zero,
                Vector2.one,
                Vector2.zero,
                new Vector2(0f, -78f));

            _leaveButton = ArenaUiKit.MakeButton(
                footer,
                "LeaveButton",
                "Leave Match",
                ArenaButtonStyle.Danger,
                OnLeaveMatchPressed);
            RectTransform leaveRect = _leaveButton.Rect;
            leaveRect.anchorMin = new Vector2(0.5f, 0.5f);
            leaveRect.anchorMax = new Vector2(0.5f, 0.5f);
            leaveRect.pivot = new Vector2(1f, 0.5f);
            leaveRect.sizeDelta = new Vector2(200f, ArenaUiTheme.ButtonHeight);
            leaveRect.anchoredPosition = new Vector2(-8f, 0f);

            _playAgainButton = ArenaUiKit.MakeButton(
                footer,
                "PlayAgainButton",
                "Play Again",
                ArenaButtonStyle.Primary,
                OnPlayAgainPressed);
            RectTransform playAgainRect = _playAgainButton.Rect;
            playAgainRect.anchorMin = new Vector2(0.5f, 0.5f);
            playAgainRect.anchorMax = new Vector2(0.5f, 0.5f);
            playAgainRect.pivot = new Vector2(0f, 0.5f);
            playAgainRect.sizeDelta = new Vector2(200f, ArenaUiTheme.ButtonHeight);
            playAgainRect.anchoredPosition = new Vector2(8f, 0f);

            // Countdown overlay — shown during pre-match countdown, hidden otherwise.
            _countdownRoot = new GameObject("CountdownRoot");
            _countdownRoot.transform.SetParent(transform, false);
            var cdCanvas = _countdownRoot.AddComponent<Canvas>();
            cdCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            cdCanvas.sortingOrder = 19;
            CanvasScaler countdownScaler = _countdownRoot.AddComponent<CanvasScaler>();
            countdownScaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            countdownScaler.referenceResolution = new Vector2(1920f, 1080f);
            countdownScaler.matchWidthOrHeight = 0.5f;
            _countdownText = ArenaUiKit.MakeText(
                _countdownRoot.transform,
                "CountdownText",
                string.Empty,
                120f,
                ArenaUiTheme.Text,
                ArenaUiTheme.TitleFont,
                TextAlignmentOptions.Center);
            _countdownText.rectTransform.sizeDelta = new Vector2(800f, 220f);
            _countdownText.overflowMode = TextOverflowModes.Overflow;
            _countdownRoot.SetActive(false);

            _root.SetActive(false);
            Refresh(MatchStateCache.Instance);
        }

        private void BuildStatsHeader()
        {
            RectTransform header = ArenaUiKit.MakeRect(_window.Content, "StatsHeader");
            ArenaUiKit.SetAnchors(
                header,
                new Vector2(0f, 1f),
                new Vector2(1f, 1f),
                new Vector2(0f, -68f),
                new Vector2(0f, -46f));

            TextMeshProUGUI player = MakeHeaderCell(header, "Player", rightAligned: false);
            ArenaUiKit.SetAnchors(
                player.rectTransform,
                Vector2.zero,
                Vector2.one,
                new Vector2(10f, 0f),
                new Vector2(-NameRightInset, 0f));

            PlaceRightColumn(MakeHeaderCell(header, "Damage", rightAligned: true).rectTransform, DamageFromRight, ColDamageWidth);
            PlaceRightColumn(MakeHeaderCell(header, "Kills", rightAligned: true).rectTransform, KillsFromRight, ColKillsWidth);
            PlaceRightColumn(MakeHeaderCell(header, "HP", rightAligned: true).rectTransform, HpFromRight, ColHpWidth);
            PlaceRightColumn(MakeHeaderCell(header, "Healing", rightAligned: true).rectTransform, HealingFromRight, ColHealingWidth);

            RectTransform divider = ArenaUiKit.MakeDivider(_window.Content);
            ArenaUiKit.SetAnchors(
                divider,
                new Vector2(0f, 1f),
                new Vector2(1f, 1f),
                new Vector2(0f, -71f),
                new Vector2(0f, -70f));
        }

        private static TextMeshProUGUI MakeHeaderCell(RectTransform parent, string text, bool rightAligned)
        {
            TextMeshProUGUI label = ArenaUiKit.MakeText(
                parent,
                $"Header_{text}",
                text.ToUpperInvariant(),
                ArenaUiTheme.SmallSize,
                ArenaUiTheme.MutedText,
                ArenaUiTheme.StrongFont,
                rightAligned ? TextAlignmentOptions.MidlineRight : TextAlignmentOptions.MidlineLeft);
            label.characterSpacing = 6f;
            return label;
        }

        private static void PlaceRightColumn(RectTransform rect, float fromRight, float width)
            => ArenaUiKit.SetAnchors(
                rect,
                new Vector2(1f, 0f),
                new Vector2(1f, 1f),
                new Vector2(-(fromRight + width), 0f),
                new Vector2(-fromRight, 0f));

        public void Refresh(MatchStateCache cache)
        {
            if (!ArenaRuntimeSceneGate.ShouldRunArenaRuntimeInActiveScene())
            {
                _root.SetActive(false);
                _countdownRoot.SetActive(false);
                return;
            }

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

            string winnerName = "Unknown";
            if (cache.WinnerId.HasValue &&
                EntityRegistry.Instance != null &&
                EntityRegistry.Instance.TryGetEntity(cache.WinnerId.Value, out var winner))
            {
                winnerName = string.IsNullOrEmpty(winner.Username) ? "???" : winner.Username;
            }
            _winnerLine.text = $"{winnerName} wins!";
            RebuildStats(cache);
            bool connected = NetworkManager.Instance?.IsConnected == true && cache.LocalInstanceId.HasValue;
            _leaveButton.SetInteractable(connected);
            _playAgainButton.SetInteractable(connected);

            _root.SetActive(true);
        }

        private void Update()
        {
            if (!ArenaRuntimeSceneGate.ShouldRunArenaRuntimeInActiveScene())
            {
                _root.SetActive(false);
                _countdownRoot.SetActive(false);
                return;
            }

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
                _countdownText.color = ArenaUiTheme.Accent;
            }
            else
            {
                _countdownText.text = Mathf.CeilToInt(remaining).ToString();
                _countdownText.color = ArenaUiTheme.Text;
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
                        winnerId.HasValue && entity.Identity == winnerId.Value,
                        conn != null && entity.Identity == conn.Identity));
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
                    winnerId.HasValue && stats.PlayerId == winnerId.Value,
                    conn != null && stats.PlayerId == conn.Identity));
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
            Color rowColor = data.IsLocal
                ? ArenaUiTheme.PositiveRow
                : (index % 2 == 0 ? ArenaUiTheme.Row : ArenaUiTheme.RowAlt);
            RectTransform row = ArenaUiKit.MakePanel(_statsRoot, $"Row_{index}", rowColor, raycastTarget: false);
            row.anchorMin = new Vector2(0f, 1f);
            row.anchorMax = new Vector2(1f, 1f);
            row.pivot = new Vector2(0.5f, 1f);
            row.sizeDelta = new Vector2(0f, StatsRowHeight);
            row.anchoredPosition = new Vector2(0f, -index * StatsRowPitch);

            Color textColor = data.IsWinner ? ArenaUiTheme.Gold : ArenaUiTheme.Text;
            TextMeshProUGUI name = ArenaUiKit.MakeText(
                row,
                "Name",
                data.Name,
                ArenaUiTheme.BodySize,
                textColor,
                data.IsWinner ? ArenaUiTheme.StrongFont : ArenaUiTheme.BodyFont);
            ArenaUiKit.SetAnchors(
                name.rectTransform,
                Vector2.zero,
                Vector2.one,
                new Vector2(10f, 0f),
                new Vector2(-NameRightInset, 0f));

            MakeNumberCell(row, "Damage", data.DamageDone, DamageFromRight, ColDamageWidth, textColor);
            MakeNumberCell(row, "Kills", data.Kills, KillsFromRight, ColKillsWidth, textColor);
            MakeNumberCell(row, "Hp", data.HpRemaining, HpFromRight, ColHpWidth, textColor);
            MakeNumberCell(row, "Healing", data.HealingDone, HealingFromRight, ColHealingWidth, textColor);

            return row.gameObject;
        }

        private static void MakeNumberCell(
            RectTransform row,
            string name,
            int value,
            float fromRight,
            float width,
            Color color)
        {
            TextMeshProUGUI label = ArenaUiKit.MakeText(
                row,
                name,
                value.ToString(),
                ArenaUiTheme.BodySize,
                color,
                alignment: TextAlignmentOptions.MidlineRight);
            PlaceRightColumn(label.rectTransform, fromRight, width);
        }

        private void OnLeaveMatchPressed()
        {
            var conn = NetworkManager.Instance?.Conn;
            if (conn == null)
                return;

            _leaveButton.SetInteractable(false);
            _playAgainButton.SetInteractable(false);
            conn.Reducers.LeaveInstance();
        }

        private void OnPlayAgainPressed()
        {
            var conn = NetworkManager.Instance?.Conn;
            if (conn == null)
                return;

            _leaveButton.SetInteractable(false);
            _playAgainButton.SetInteractable(false);
            conn.Reducers.LeaveInstance();
            // LobbyController will show automatically once LocalInstanceId clears.
        }

        private static string ShortIdentity(Identity identity)
        {
            string text = identity.ToString();
            return text.Length > 8 ? $"Player_{text[..8]}" : $"Player_{text}";
        }

        private readonly struct StatsRowData
        {
            public StatsRowData(
                string name,
                int damageDone,
                int kills,
                int hpRemaining,
                int healingDone,
                bool isWinner,
                bool isLocal)
            {
                Name = name;
                DamageDone = damageDone;
                Kills = kills;
                HpRemaining = hpRemaining;
                HealingDone = healingDone;
                IsWinner = isWinner;
                IsLocal = isLocal;
            }

            public string Name { get; }
            public int DamageDone { get; }
            public int Kills { get; }
            public int HpRemaining { get; }
            public int HealingDone { get; }
            public bool IsWinner { get; }
            public bool IsLocal { get; }
        }
    }
}
