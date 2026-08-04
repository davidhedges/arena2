#nullable enable

using System;
using System.Linq;
using Arena.Match;
using Arena.Network;
using SpacetimeDB.ClientApi;
using SpacetimeDB.Types;
using TMPro;
using UnityEngine;

namespace Arena.UI
{
    /// <summary>
    /// Owner-only presentation of the public survival run/result rows. Timers
    /// are visual estimates against server time; reducers remain authoritative.
    /// </summary>
    public sealed class SurvivalHud : MonoBehaviour
    {
        private const string IntermissionPhase = "INTERMISSION";
        private const string ActivePhase = "ACTIVE";
        private const string BossPhase = "BOSS";
        private const float RefreshIntervalSeconds = 0.10f;

        private GameObject _runRoot = null!;
        private TextMeshProUGUI _phaseText = null!;
        private TextMeshProUGUI _statsText = null!;
        private TextMeshProUGUI _timerText = null!;
        private ArenaButtonHandle _readyButton;

        private GameObject _resultRoot = null!;
        private TextMeshProUGUI _resultText = null!;
        private ArenaButtonHandle _dismissButton;

        private float _readyCooldownUntil;
        private float _dismissCooldownUntil;
        private float _nextRefreshTime;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (FindAnyObjectByType<SurvivalHud>() != null)
                return;

            var host = new GameObject("SurvivalHud");
            DontDestroyOnLoad(host);
            host.AddComponent<SurvivalHud>();
        }

        private void Awake()
        {
            ArenaUiKit.MakeOverlayCanvas(gameObject, 31);
            BuildRunPanel();
            BuildResultPanel();
            _runRoot.SetActive(false);
            _resultRoot.SetActive(false);
        }

        private void BuildRunPanel()
        {
            RectTransform panel = ArenaUiKit.MakePanel(
                transform,
                "RunPanel",
                ArenaUiTheme.PanelStrong,
                hairline: true,
                cornerRadius: ArenaUiSprites.SmallRadius);
            panel.anchorMin = new Vector2(0.5f, 1f);
            panel.anchorMax = new Vector2(0.5f, 1f);
            panel.pivot = new Vector2(0.5f, 1f);
            panel.anchoredPosition = new Vector2(0f, -22f);
            panel.sizeDelta = new Vector2(660f, 126f);
            _runRoot = panel.gameObject;

            _phaseText = ArenaUiKit.MakeText(
                panel,
                "Phase",
                string.Empty,
                ArenaUiTheme.SectionSize,
                ArenaUiTheme.Accent,
                ArenaUiTheme.StrongFont,
                TextAlignmentOptions.MidlineLeft);
            ArenaUiKit.SetAnchors(
                _phaseText.rectTransform,
                new Vector2(0f, 1f),
                new Vector2(0.7f, 1f),
                new Vector2(22f, -42f),
                new Vector2(0f, -12f));

            _statsText = ArenaUiKit.MakeText(
                panel,
                "Stats",
                string.Empty,
                ArenaUiTheme.BodySize,
                ArenaUiTheme.Text,
                ArenaUiTheme.BodyFont,
                TextAlignmentOptions.MidlineLeft);
            ArenaUiKit.SetAnchors(
                _statsText.rectTransform,
                new Vector2(0f, 0f),
                new Vector2(0.72f, 0f),
                new Vector2(22f, 18f),
                new Vector2(0f, 50f));

            _timerText = ArenaUiKit.MakeText(
                panel,
                "Timer",
                string.Empty,
                34f,
                ArenaUiTheme.Text,
                ArenaUiTheme.TitleFont,
                TextAlignmentOptions.Center);
            ArenaUiKit.SetAnchors(
                _timerText.rectTransform,
                new Vector2(0.7f, 0.5f),
                new Vector2(1f, 1f),
                new Vector2(0f, 0f),
                new Vector2(-18f, -8f));

            _readyButton = ArenaUiKit.MakeButton(
                panel,
                "ReadyButton",
                "START ROUND",
                ArenaButtonStyle.Primary,
                ReadyForRound);
            ArenaUiKit.SetAnchors(
                _readyButton.Rect,
                new Vector2(0.72f, 0f),
                new Vector2(1f, 0f),
                new Vector2(0f, 14f),
                new Vector2(-18f, 54f));
        }

        private void BuildResultPanel()
        {
            RectTransform panel = ArenaUiKit.MakePanel(
                transform,
                "ResultPanel",
                ArenaUiTheme.PanelStrong,
                hairline: true,
                cornerRadius: ArenaUiSprites.PanelRadius);
            panel.anchorMin = new Vector2(0.5f, 0.5f);
            panel.anchorMax = new Vector2(0.5f, 0.5f);
            panel.pivot = new Vector2(0.5f, 0.5f);
            panel.anchoredPosition = Vector2.zero;
            panel.sizeDelta = new Vector2(540f, 260f);
            _resultRoot = panel.gameObject;

            TextMeshProUGUI title = ArenaUiKit.MakeText(
                panel,
                "Title",
                "SURVIVAL RUN ENDED",
                ArenaUiTheme.WindowTitleSize,
                ArenaUiTheme.Text,
                ArenaUiTheme.TitleFont,
                TextAlignmentOptions.Center);
            ArenaUiKit.SetAnchors(
                title.rectTransform,
                new Vector2(0f, 1f),
                new Vector2(1f, 1f),
                new Vector2(20f, -66f),
                new Vector2(-20f, -18f));

            _resultText = ArenaUiKit.MakeText(
                panel,
                "Summary",
                string.Empty,
                ArenaUiTheme.BodySize,
                ArenaUiTheme.Text,
                ArenaUiTheme.StrongFont,
                TextAlignmentOptions.Center);
            ArenaUiKit.SetAnchors(
                _resultText.rectTransform,
                new Vector2(0f, 0f),
                new Vector2(1f, 1f),
                new Vector2(24f, 78f),
                new Vector2(-24f, -76f));

            _dismissButton = ArenaUiKit.MakeButton(
                panel,
                "DismissButton",
                "CLOSE",
                ArenaButtonStyle.Primary,
                DismissResult);
            ArenaUiKit.SetAnchors(
                _dismissButton.Rect,
                new Vector2(0.5f, 0f),
                new Vector2(0.5f, 0f),
                new Vector2(-110f, 18f),
                new Vector2(110f, 62f));
        }

        private void Update()
        {
            DbConnection? conn = NetworkManager.Instance?.Conn;
            if (!ArenaRuntimeSceneGate.ShouldRunArenaRuntimeInActiveScene()
                || conn == null
                || !conn.Identity.HasValue)
            {
                HideAll();
                _nextRefreshTime = 0f;
                return;
            }

            float now = Time.unscaledTime;
            if (now < _nextRefreshTime)
                return;
            _nextRefreshTime = now + RefreshIntervalSeconds;

            SurvivalRun? run = conn.Db.SurvivalRun.Owner.Filter(conn.Identity.Value).FirstOrDefault();
            if (run != null && MatchStateCache.Instance.IsSurvivalMode)
            {
                RefreshRun(run, conn);
                SetActiveIfChanged(_runRoot, true);
                SetActiveIfChanged(_resultRoot, false);
                return;
            }

            SurvivalResult? result = conn.Db.SurvivalResult.Owner.Find(conn.Identity.Value);
            if (result != null)
            {
                SurvivalScore? score = conn.Db.SurvivalScore.Owner.Find(conn.Identity.Value);
                RefreshResult(result, score, conn);
                SetActiveIfChanged(_runRoot, false);
                SetActiveIfChanged(_resultRoot, true);
                return;
            }

            HideAll();
        }

        private void RefreshRun(SurvivalRun run, DbConnection conn)
        {
            bool intermission = string.Equals(run.Phase, IntermissionPhase, StringComparison.Ordinal);
            bool boss = string.Equals(run.Phase, BossPhase, StringComparison.Ordinal);
            uint displayedRound = run.Round == 0 ? 1u : run.Round;
            SetTextIfChanged(_phaseText, intermission
                ? $"PREPARE FOR ROUND {displayedRound}"
                : boss
                    ? $"BOSS ROUND {displayedRound}"
                    : $"ROUND {displayedRound}");
            SetTextIfChanged(
                _statsText,
                $"GOLD  {run.Gold:N0}     KILLS  {run.Kills:N0}     ALIVE  {run.TotalAlive:N0}");

            if (string.Equals(run.Phase, ActivePhase, StringComparison.Ordinal))
            {
                long endMs = run.RoundEndsAt.MicrosecondsSinceUnixEpoch / 1000L;
                long nowMs = ArenaServerClock.HasEstimate
                    ? ArenaServerClock.ServerNowMs
                    : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                long remainingSeconds = Math.Max(0L, (long)Math.Ceiling((endMs - nowMs) / 1000.0));
                SetTextIfChanged(
                    _timerText,
                    $"{remainingSeconds / 60:00}:{remainingSeconds % 60:00}");
            }
            else if (boss)
            {
                SetTextIfChanged(_timerText, "DEFEAT BOSS");
            }
            else
            {
                SetTextIfChanged(_timerText, "READY");
            }

            SetActiveIfChanged(_readyButton.GameObject, intermission);
            SetTextIfChanged(_readyButton.Label, $"START ROUND {displayedRound}");
            SetInteractableIfChanged(
                _readyButton,
                intermission && conn.Identity.HasValue && Time.unscaledTime >= _readyCooldownUntil);
        }

        private void RefreshResult(SurvivalResult result, SurvivalScore? score, DbConnection conn)
        {
            string bestLine = score == null
                ? string.Empty
                : $"\nBEST ROUND  {score.BestRound:N0}     RUNS  {score.RunsPlayed:N0}";
            SetTextIfChanged(
                _resultText,
                $"ROUND REACHED  {result.RoundReached:N0}\n" +
                $"KILLS  {result.Kills:N0}     GOLD EARNED  {result.GoldEarned:N0}" +
                bestLine);
            SetInteractableIfChanged(
                _dismissButton,
                conn.Identity.HasValue && Time.unscaledTime >= _dismissCooldownUntil);
        }

        private void ReadyForRound()
        {
            DbConnection? conn = NetworkManager.Instance?.Conn;
            if (conn == null || Time.unscaledTime < _readyCooldownUntil)
                return;
            _readyCooldownUntil = Time.unscaledTime + 1f;
            conn.Reducers.ReadyForNextSurvivalRound();
        }

        private void DismissResult()
        {
            DbConnection? conn = NetworkManager.Instance?.Conn;
            if (conn == null || Time.unscaledTime < _dismissCooldownUntil)
                return;
            _dismissCooldownUntil = Time.unscaledTime + 1f;
            conn.Reducers.DismissSurvivalResult();
        }

        private void HideAll()
        {
            SetActiveIfChanged(_runRoot, false);
            SetActiveIfChanged(_resultRoot, false);
        }

        private static void SetTextIfChanged(TextMeshProUGUI text, string value)
        {
            if (!string.Equals(text.text, value, StringComparison.Ordinal))
                text.text = value;
        }

        private static void SetInteractableIfChanged(ArenaButtonHandle button, bool interactable)
        {
            if (button.Button.interactable != interactable)
                button.SetInteractable(interactable);
        }

        private static void SetActiveIfChanged(GameObject target, bool active)
        {
            if (target.activeSelf != active)
                target.SetActive(active);
        }
    }
}
