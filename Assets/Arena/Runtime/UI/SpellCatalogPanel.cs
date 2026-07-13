#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Arena.Combat;
using Arena.Network;
using SpacetimeDB.Types;
using TMPro;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif
using UnityEngine.UI;

namespace Arena.UI
{
    [DefaultExecutionOrder(65)]
    public sealed class SpellCatalogPanel : MonoBehaviour, IEscapeCloseable
    {
        private const float RefreshIntervalSeconds = 0.20f;
        private const float RowHeight = 58f;
        private const float RowGap = 8f;

        private Canvas? _canvas;
        private ArenaWindow? _window;
        private RectTransform? _rowRoot;
        private TextMeshProUGUI? _statusText;
        private float _nextRefreshTime;
        private bool _isOpen;
        private string _lastSignature = string.Empty;
        private string _lastError = string.Empty;
        private float _errorUntilTime;
        private DbConnection? _subscribedConnection;

        public int EscapeClosePriority => 90;
        public bool IsEscapeCloseable => _isOpen;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (!ArenaRuntimeSceneGate.ShouldRunArenaRuntimeInActiveScene())
                return;

            GameObject go = new("SpellCatalogPanel");
            DontDestroyOnLoad(go);
            go.AddComponent<SpellCatalogPanel>();
        }

        private void Awake()
        {
            _canvas = ArenaUiKit.MakeOverlayCanvas(gameObject, 35);
            BuildPanel();
            SetOpen(false, instant: true);
        }

        private void OnEnable()
        {
            RuntimeUiEscapeRouter.Register(this);
            TrySubscribeToReducerErrors();
        }

        private void OnDisable()
        {
            RuntimeUiEscapeRouter.Unregister(this);
            UnsubscribeFromReducerErrors();
        }

        private void OnDestroy()
        {
            RuntimeUiEscapeRouter.Unregister(this);
            UnsubscribeFromReducerErrors();
        }

        private void Update()
        {
            if (!ArenaRuntimeSceneGate.ShouldRunArenaRuntimeInActiveScene())
            {
                if (_isOpen)
                    SetOpen(false, instant: true);
                return;
            }

            TrySubscribeToReducerErrors();

            if (WasSpellCatalogTogglePressed())
                SetOpen(!_isOpen);

            if (!_isOpen)
                return;

            if (_statusText != null && _errorUntilTime > 0f && Time.unscaledTime >= _errorUntilTime)
            {
                _statusText.text = string.Empty;
                _errorUntilTime = 0f;
            }

            if (Time.unscaledTime >= _nextRefreshTime)
            {
                _nextRefreshTime = Time.unscaledTime + RefreshIntervalSeconds;
                Refresh();
            }
        }

        public bool TryCloseForEscape()
        {
            if (!IsEscapeCloseable)
                return false;

            SetOpen(false);
            return true;
        }

        private void SetOpen(bool open, bool instant = false)
        {
            _isOpen = open;
            _window?.SetVisible(open, instant);
            if (open)
            {
                RuntimeUiLayer.BringToFront(_canvas);
                _lastSignature = string.Empty;
                _nextRefreshTime = 0f;
                Refresh();
            }
        }

        private void BuildPanel()
        {
            _window = ArenaWindow.Create(transform, "SpellCatalogWindow", "Spells", new Vector2(780f, 620f));
            _window.SetSubtitle("K");
            _window.CloseRequested += () => SetOpen(false);

            RectTransform footer = _window.AddFooter();
            _statusText = ArenaUiKit.MakeText(
                footer,
                "Status",
                string.Empty,
                ArenaUiTheme.BodySize,
                ArenaUiTheme.Danger);
            ArenaUiKit.Fill(_statusText.rectTransform, new Vector2(ArenaUiTheme.ContentPadding, 0f));

            _rowRoot = ArenaUiKit.MakeScrollView(_window.Content, "SpellScroll", out ScrollRect scrollRect);
            ArenaUiKit.Fill((RectTransform)scrollRect.transform);
        }

        private void Refresh()
        {
            if (_rowRoot == null)
                return;

            DbConnection? conn = NetworkManager.Instance?.Conn;
            if (conn == null || !conn.Identity.HasValue)
            {
                RebuildRows(Array.Empty<SpellDefinition>(), new HashSet<string>(StringComparer.Ordinal));
                SetStatus("NO CONNECTION", false);
                return;
            }

            List<SpellDefinition> spells = conn.Db.SpellDefinition.Iter()
                .OrderBy(spell => spell.Kind, StringComparer.Ordinal)
                .ToList();
            HashSet<string> known = conn.Db.PlayerKnownSpell.Owner
                .Filter(conn.Identity.Value)
                .Select(row => WireIdentifier.Normalize(row.SpellId))
                .ToHashSet(StringComparer.Ordinal);
            EquipmentLoadout? loadout = conn.Db.EquipmentLoadout.Owner.Find(conn.Identity.Value);
            if (loadout != null && !string.IsNullOrWhiteSpace(loadout.SpellbookItemId))
            {
                foreach (ItemSpell itemSpell in conn.Db.ItemSpell.ItemInstanceId.Filter(loadout.SpellbookItemId))
                {
                    string spellId = WireIdentifier.Normalize(itemSpell.SpellId);
                    if (!string.IsNullOrWhiteSpace(spellId))
                        known.Add(spellId);
                }
            }

            string signature = BuildSignature(spells, known);
            if (signature == _lastSignature)
                return;

            _lastSignature = signature;
            RebuildRows(spells, known);
        }

        private void RebuildRows(IReadOnlyList<SpellDefinition> spells, HashSet<string> known)
        {
            if (_rowRoot == null)
                return;

            for (int i = _rowRoot.childCount - 1; i >= 0; i--)
                Destroy(_rowRoot.GetChild(i).gameObject);

            if (spells.Count == 0)
            {
                _rowRoot.sizeDelta = new Vector2(0f, 44f);
                TextMeshProUGUI empty = ArenaUiKit.MakeText(
                    _rowRoot,
                    "Empty",
                    "No spell definitions available.",
                    15f,
                    ArenaUiTheme.MutedText,
                    alignment: TextAlignmentOptions.Center);
                ArenaUiKit.Fill(empty.rectTransform);
                return;
            }

            float contentHeight = spells.Count * RowHeight + Math.Max(0, spells.Count - 1) * RowGap;
            _rowRoot.sizeDelta = new Vector2(0f, contentHeight);
            for (int i = 0; i < spells.Count; i++)
            {
                SpellDefinition spell = spells[i];
                string spellId = WireIdentifier.Normalize(spell.Kind);
                bool learned = known.Contains(spellId);
                RectTransform row = ArenaUiKit.MakePanel(
                    _rowRoot,
                    $"Spell_{spellId}",
                    learned ? ArenaUiTheme.PositiveRow : (i % 2 == 0 ? ArenaUiTheme.Row : ArenaUiTheme.RowAlt));
                ArenaUiKit.SetAnchors(
                    row,
                    new Vector2(0f, 1f),
                    new Vector2(1f, 1f),
                    new Vector2(0f, -((i + 1) * RowHeight + i * RowGap)),
                    new Vector2(0f, -(i * (RowHeight + RowGap))));

                TextMeshProUGUI name = ArenaUiKit.MakeText(
                    row,
                    "Name",
                    DisplayNameForSpell(spell),
                    15f,
                    ArenaUiTheme.Text,
                    ArenaUiTheme.StrongFont);
                ArenaUiKit.PlaceTopLeft(name.rectTransform, new Vector2(16f, 10f), new Vector2(330f, 24f));

                TextMeshProUGUI meta = ArenaUiKit.MakeText(
                    row,
                    "Meta",
                    MetadataForSpell(spell),
                    ArenaUiTheme.SmallSize,
                    ArenaUiTheme.MutedText);
                ArenaUiKit.PlaceTopLeft(meta.rectTransform, new Vector2(16f, 34f), new Vector2(470f, 18f));

                TextMeshProUGUI state = ArenaUiKit.MakeText(
                    row,
                    "State",
                    learned ? "Learned" : "Available",
                    ArenaUiTheme.BodySize,
                    learned ? ArenaUiTheme.Gold : ArenaUiTheme.Text,
                    alignment: TextAlignmentOptions.MidlineRight);
                ArenaUiKit.SetAnchors(
                    state.rectTransform,
                    new Vector2(1f, 0.5f),
                    new Vector2(1f, 0.5f),
                    Vector2.zero,
                    Vector2.zero);
                state.rectTransform.pivot = new Vector2(1f, 0.5f);
                state.rectTransform.anchoredPosition = new Vector2(-112f, 0f);
                state.rectTransform.sizeDelta = new Vector2(120f, 28f);

                string capturedSpellId = spellId;
                ArenaButtonHandle learn = ArenaUiKit.MakeButton(
                    row,
                    "LearnButton",
                    learned ? "Known" : "Learn",
                    learned ? ArenaButtonStyle.Secondary : ArenaButtonStyle.Primary,
                    () => Learn(capturedSpellId));
                ArenaUiKit.SetAnchors(
                    learn.Rect,
                    new Vector2(1f, 0.5f),
                    new Vector2(1f, 0.5f),
                    Vector2.zero,
                    Vector2.zero);
                learn.Rect.pivot = new Vector2(1f, 0.5f);
                learn.Rect.anchoredPosition = new Vector2(-16f, 0f);
                learn.Rect.sizeDelta = new Vector2(84f, ArenaUiTheme.ButtonHeight);
                learn.SetInteractable(!learned);
            }
        }

        private void Learn(string spellId)
        {
            DbConnection? conn = NetworkManager.Instance?.Conn;
            if (conn == null)
            {
                SetStatus("NO CONNECTION", true);
                return;
            }

            conn.Reducers.LearnSpell(spellId);
            SetStatus($"Learning {spellId}", false);
            _nextRefreshTime = 0f;
        }

        private void TrySubscribeToReducerErrors()
        {
            DbConnection? conn = NetworkManager.Instance?.Conn;
            if (conn == null || ReferenceEquals(conn, _subscribedConnection))
                return;

            UnsubscribeFromReducerErrors();
            _subscribedConnection = conn;
            _subscribedConnection.OnUnhandledReducerError += OnUnhandledReducerError;
        }

        private void UnsubscribeFromReducerErrors()
        {
            if (_subscribedConnection == null)
                return;

            _subscribedConnection.OnUnhandledReducerError -= OnUnhandledReducerError;
            _subscribedConnection = null;
        }

        private void OnUnhandledReducerError(ReducerEventContext ctx, Exception error)
        {
            if (ctx.Event.Reducer is not Reducer.LearnSpell)
                return;

            SetStatus(error.Message, true);
        }

        private void SetStatus(string message, bool error)
        {
            if (_statusText == null)
                return;

            _lastError = error ? message : string.Empty;
            _statusText.color = error ? ArenaUiTheme.Danger : ArenaUiTheme.Success;
            _statusText.text = message;
            _errorUntilTime = Time.unscaledTime + 4f;
        }

        private static string BuildSignature(IEnumerable<SpellDefinition> spells, HashSet<string> known)
        {
            System.Text.StringBuilder sb = new();
            foreach (SpellDefinition spell in spells)
            {
                string spellId = WireIdentifier.Normalize(spell.Kind);
                sb.Append(spellId).Append(':')
                    .Append(spell.CooldownMs).Append(':')
                    .Append(spell.CastTimeMs).Append(':')
                    .Append(spell.PrimaryResourceCost).Append(':')
                    .Append(known.Contains(spellId) ? '1' : '0').Append(';');
            }
            return sb.ToString();
        }

        private static string DisplayNameForSpell(SpellDefinition spell)
        {
            string normalized = WireIdentifier.Normalize(spell.Kind);
            return string.Join(" ", normalized.Split('_').Where(part => part.Length > 0).Select(Capitalize));
        }

        private static string MetadataForSpell(SpellDefinition spell)
        {
            string targeting = WireIdentifier.Normalize(spell.Targeting);
            string behavior = WireIdentifier.Normalize(spell.Behavior);
            float cost = Math.Max(0f, spell.PrimaryResourceCost);
            string costLabel = SpellDefinitionContracts.UsesPerSecondResourceCost(spell)
                ? $"{cost:0.#}/s"
                : $"{cost:0.#}";
            return $"{targeting} | {behavior} | Cost {costLabel}";
        }

        private static string Capitalize(string value)
            => value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..].ToLowerInvariant();

        private static bool WasSpellCatalogTogglePressed()
        {
#if ENABLE_INPUT_SYSTEM
            if (Keyboard.current != null)
                return Keyboard.current.kKey.wasPressedThisFrame;
#endif
            return UnityEngine.Input.GetKeyDown(KeyCode.K);
        }
    }
}
