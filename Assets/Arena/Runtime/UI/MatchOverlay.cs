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
    /// Displays the local result and authoritative match stats.
    ///
    /// Refreshed by MatchController when match phase changes.
    ///
    /// Legacy/direct instances return through leave_instance. Disposable PvP
    /// matches return by disconnecting so their database can be reclaimed.
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
        private readonly List<GameObject> _statRows = new();
        private DbConnection? _leaveConnection;
        private bool _returnPending;
        private bool _handoffReturnPending;
        private string _returnError = string.Empty;

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
                "Return to Hub",
                ArenaButtonStyle.Primary,
                OnLeaveMatchPressed);
            RectTransform leaveRect = _leaveButton.Rect;
            leaveRect.anchorMin = new Vector2(0.5f, 0.5f);
            leaveRect.anchorMax = new Vector2(0.5f, 0.5f);
            leaveRect.pivot = new Vector2(0.5f, 0.5f);
            leaveRect.sizeDelta = new Vector2(200f, ArenaUiTheme.ButtonHeight);
            leaveRect.anchoredPosition = Vector2.zero;

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
            BindLeaveConnection(NetworkManager.Instance?.Conn);
            Refresh(MatchStateCache.Instance);
        }

        private void OnDestroy()
        {
            BindLeaveConnection(null);
            if (Instance == this)
                Instance = null;
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

            _winnerLine.text = ResolveOutcomeLabel(cache);
            if (!string.IsNullOrWhiteSpace(_returnError))
                _winnerLine.text += $"\n{_returnError}";
            RebuildStats(cache);
            bool connected = NetworkManager.Instance?.IsConnected == true && cache.LocalInstanceId.HasValue;
            _leaveButton.SetInteractable(connected && !_returnPending && !_handoffReturnPending);

            _root.SetActive(true);
        }

        private void Update()
        {
            BindLeaveConnection(NetworkManager.Instance?.Conn);
            if (string.Equals(
                    UnityEngine.SceneManagement.SceneManager.GetActiveScene().name,
                    "Hub",
                    System.StringComparison.Ordinal))
            {
                _handoffReturnPending = false;
            }
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

            var rows = BuildRows(
                cache.LocalInstanceId.Value,
                cache.WinnerId,
                cache.WinnerTeamId);
            for (int i = 0; i < rows.Count; i++)
            {
                var row = MakeStatsRow(rows[i], i);
                _statRows.Add(row);
            }
        }

        private List<StatsRowData> BuildRows(
            ulong instanceId,
            Identity? winnerId,
            byte? winnerTeamId)
        {
            var result = new List<StatsRowData>();
            var statsByPlayer = new Dictionary<Identity, MatchParticipantStats>();
            var conn = NetworkManager.Instance?.Conn;
            if (conn != null)
            {
                foreach (var stats in conn.Db.MatchParticipantStats.InstanceId.Filter(instanceId))
                    statsByPlayer[stats.PlayerId] = stats;
            }

            if (conn != null)
            {
                var roster = new List<MatchParticipant>(
                    conn.Db.MatchParticipant.InstanceId.Filter(instanceId));
                if (roster.Count > 0)
                {
                    foreach (var participant in roster)
                    {
                        statsByPlayer.TryGetValue(participant.Identity, out var stats);
                        string name = ResolvePlayerName(conn, participant.Identity);
                        result.Add(new StatsRowData(
                            $"TEAM {participant.TeamId + 1}  ·  {name}",
                            stats?.DamageDone ?? 0,
                            stats?.Kills ?? 0,
                            stats?.HpRemaining ?? 0,
                            stats?.HealingDone ?? 0,
                            winnerTeamId.HasValue && participant.TeamId == winnerTeamId.Value,
                            conn.Identity.HasValue && participant.Identity == conn.Identity.Value,
                            participant.TeamId,
                            participant.TeamSlot));
                    }

                    result.Sort((a, b) =>
                    {
                        int teamCmp = a.TeamId.CompareTo(b.TeamId);
                        return teamCmp != 0 ? teamCmp : a.TeamSlot.CompareTo(b.TeamSlot);
                    });
                    return result;
                }
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
                        conn?.Identity.HasValue == true && entity.Identity == conn.Identity.Value,
                        byte.MaxValue,
                        byte.MaxValue));
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
                    conn?.Identity.HasValue == true && stats.PlayerId == conn.Identity.Value,
                    byte.MaxValue,
                    byte.MaxValue));
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
            BindLeaveConnection(NetworkManager.Instance?.Conn);
            if (_leaveConnection == null
                || !_leaveConnection.Identity.HasValue
                || _returnPending
                || _handoffReturnPending)
                return;

            MatchHandoffCoordinator? handoff = MatchHandoffCoordinator.Instance;
            if (handoff != null && handoff.ReturnToHub())
            {
                _handoffReturnPending = true;
                _returnError = string.Empty;
                _leaveButton.SetInteractable(false);
                return;
            }

            _returnPending = true;
            _returnError = string.Empty;
            _leaveButton.SetInteractable(false);
            RuntimeSceneTransitionQueue.BeginExplicitHubReturn();
            _leaveConnection.Reducers.LeaveInstance();
        }

        private void BindLeaveConnection(DbConnection? connection)
        {
            if (ReferenceEquals(_leaveConnection, connection))
                return;

            if (_leaveConnection != null)
                _leaveConnection.Reducers.OnLeaveInstance -= OnLeaveInstance;
            if (_returnPending)
                RuntimeSceneTransitionQueue.CancelExplicitHubReturn();
            _leaveConnection = connection;
            _returnPending = false;
            if (_leaveConnection != null)
                _leaveConnection.Reducers.OnLeaveInstance += OnLeaveInstance;
        }

        private void OnLeaveInstance(ReducerEventContext context)
        {
            if (_leaveConnection == null
                || !_leaveConnection.Identity.HasValue
                || context.Event.CallerIdentity != _leaveConnection.Identity.Value
                || !_returnPending)
            {
                return;
            }

            if (context.Event.Status is Status.Committed)
            {
                RuntimeSceneTransitionQueue.RequestExplicitHubReturn();
                return;
            }

            _returnPending = false;
            RuntimeSceneTransitionQueue.CancelExplicitHubReturn();
            _returnError = context.Event.Status switch
            {
                Status.Failed(var failure) => $"Return failed: {failure}",
                Status.OutOfEnergy(var _) => "Return failed: server was out of reducer energy.",
                _ => "Return failed: the server did not commit the request.",
            };
            Refresh(MatchStateCache.Instance);
        }

        private static string ResolveOutcomeLabel(MatchStateCache cache)
        {
            var conn = NetworkManager.Instance?.Conn;
            if (cache.IsTeamMatch)
            {
                if (!cache.WinnerTeamId.HasValue)
                    return "DRAW";

                MatchParticipant? localParticipant = conn?.Identity.HasValue == true
                    ? conn.Db.MatchParticipant.Identity.Find(conn.Identity.Value)
                    : null;
                if (localParticipant == null)
                    return $"TEAM {cache.WinnerTeamId.Value + 1} WINS";

                return localParticipant.TeamId == cache.WinnerTeamId.Value
                    ? "VICTORY"
                    : "DEFEAT";
            }

            if (!cache.WinnerId.HasValue)
                return "DRAW";
            return conn?.Identity.HasValue == true && cache.WinnerId.Value == conn.Identity.Value
                ? "VICTORY"
                : "DEFEAT";
        }

        private static string ResolvePlayerName(DbConnection conn, Identity identity)
        {
            if (EntityRegistry.Instance != null
                && EntityRegistry.Instance.TryGetEntity(identity, out var entity)
                && !string.IsNullOrWhiteSpace(entity.Username))
            {
                return entity.Username;
            }

            Player? player = conn.Db.Player.Identity.Find(identity);
            return string.IsNullOrWhiteSpace(player?.Username)
                ? ShortIdentity(identity)
                : player.Username;
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
                bool isLocal,
                byte teamId,
                byte teamSlot)
            {
                Name = name;
                DamageDone = damageDone;
                Kills = kills;
                HpRemaining = hpRemaining;
                HealingDone = healingDone;
                IsWinner = isWinner;
                IsLocal = isLocal;
                TeamId = teamId;
                TeamSlot = teamSlot;
            }

            public string Name { get; }
            public int DamageDone { get; }
            public int Kills { get; }
            public int HpRemaining { get; }
            public int HealingDone { get; }
            public bool IsWinner { get; }
            public bool IsLocal { get; }
            public byte TeamId { get; }
            public byte TeamSlot { get; }
        }
    }
}
