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
        private static readonly Color PanelColor = HeatUiStyle.Panel;
        private static readonly Color HeaderColor = HeatUiStyle.Header;
        private static readonly Color RowColor = HeatUiStyle.Row;
        private static readonly Color LearnedColor = HeatUiStyle.Learned;
        private static readonly Color Gold = HeatUiStyle.Gold;

        private Canvas? _canvas;
        private GameObject? _panelRoot;
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
            RuntimeUiEventSystem.Ensure();

            _canvas = gameObject.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 35;

            CanvasScaler scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            gameObject.AddComponent<GraphicRaycaster>();
            BuildPanel();
            SetOpen(false);
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
                SetOpen(false);
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

        private void SetOpen(bool open)
        {
            _isOpen = open;
            if (_panelRoot != null)
                _panelRoot.SetActive(open);
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
            _panelRoot = new GameObject("SpellCatalogRoot", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            _panelRoot.transform.SetParent(transform, false);
            RectTransform rootRt = (RectTransform)_panelRoot.transform;
            rootRt.anchorMin = new Vector2(0.5f, 0.5f);
            rootRt.anchorMax = new Vector2(0.5f, 0.5f);
            rootRt.pivot = new Vector2(0.5f, 0.5f);
            rootRt.sizeDelta = new Vector2(780f, 620f);
            rootRt.anchoredPosition = Vector2.zero;

            Image panelImage = _panelRoot.GetComponent<Image>();
            panelImage.color = PanelColor;
            HeatUiStyle.StylePanel(_panelRoot);
            HeatUiStyle.AddAccentBar(
                _panelRoot.transform,
                "Accent",
                new Vector2(0f, 0f),
                new Vector2(0f, 1f),
                Vector2.zero,
                new Vector2(3f, 0f));

            RectTransform header = AddBlock("Header", _panelRoot.transform, HeaderColor);
            HeatUiStyle.StyleHeader(header.gameObject);
            header.anchorMin = new Vector2(0f, 1f);
            header.anchorMax = new Vector2(1f, 1f);
            header.pivot = new Vector2(0.5f, 1f);
            header.offsetMin = new Vector2(0f, -64f);
            header.offsetMax = Vector2.zero;

            TextMeshProUGUI title = MakeLabel("Title", header, "Spells", 24f, TextAlignmentOptions.MidlineLeft, Color.white);
            SetRect(title.rectTransform, new Vector2(22f, -52f), new Vector2(300f, 42f), new Vector2(0f, 1f), new Vector2(0f, 1f));

            TextMeshProUGUI hint = MakeLabel("Hint", header, "K", 15f, TextAlignmentOptions.MidlineRight, HeatUiStyle.MutedText);
            SetRect(hint.rectTransform, new Vector2(-180f, -48f), new Vector2(120f, 34f), new Vector2(1f, 1f), new Vector2(1f, 1f));

            Button close = MakeButton("CloseButton", header, "Close", new Color(0.16f, 0.17f, 0.19f, 0.96f), Color.white);
            SetRect((RectTransform)close.transform, new Vector2(-94f, -49f), new Vector2(72f, 34f), new Vector2(1f, 1f), new Vector2(1f, 1f));
            close.onClick.AddListener(() => SetOpen(false));

            _rowRoot = new GameObject("Rows", typeof(RectTransform)).GetComponent<RectTransform>();
            _rowRoot.SetParent(_panelRoot.transform, false);
            _rowRoot.anchorMin = new Vector2(0f, 0f);
            _rowRoot.anchorMax = new Vector2(1f, 1f);
            _rowRoot.offsetMin = new Vector2(22f, 58f);
            _rowRoot.offsetMax = new Vector2(-22f, -84f);

            _statusText = MakeLabel("Status", _panelRoot.transform, string.Empty, 13f, TextAlignmentOptions.MidlineLeft, HeatUiStyle.Error);
            SetRect(_statusText.rectTransform, new Vector2(22f, 18f), new Vector2(500f, 28f), new Vector2(0f, 0f), new Vector2(0f, 0f));
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
                TextMeshProUGUI empty = MakeLabel("Empty", _rowRoot, "No spell definitions available.", 15f, TextAlignmentOptions.Center, HeatUiStyle.MutedText);
                SetRect(empty.rectTransform, Vector2.zero, new Vector2(600f, 44f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));
                return;
            }

            const float rowHeight = 58f;
            const float rowGap = 8f;
            for (int i = 0; i < spells.Count; i++)
            {
                SpellDefinition spell = spells[i];
                string spellId = WireIdentifier.Normalize(spell.Kind);
                bool learned = known.Contains(spellId);
                RectTransform row = AddBlock($"Spell_{spellId}", _rowRoot, learned ? LearnedColor : (i % 2 == 0 ? RowColor : HeatUiStyle.RowAlt));
                row.anchorMin = new Vector2(0f, 1f);
                row.anchorMax = new Vector2(1f, 1f);
                row.pivot = new Vector2(0.5f, 1f);
                row.offsetMin = new Vector2(0f, -((i + 1) * rowHeight + i * rowGap));
                row.offsetMax = new Vector2(0f, -(i * (rowHeight + rowGap)));

                TextMeshProUGUI name = MakeLabel("Name", row, DisplayNameForSpell(spell), 15f, TextAlignmentOptions.MidlineLeft, Color.white);
                SetRect(name.rectTransform, new Vector2(16f, -34f), new Vector2(280f, 24f), new Vector2(0f, 1f), new Vector2(0f, 1f));

                TextMeshProUGUI meta = MakeLabel("Meta", row, MetadataForSpell(spell), 12f, TextAlignmentOptions.MidlineLeft, HeatUiStyle.MutedText);
                SetRect(meta.rectTransform, new Vector2(16f, -54f), new Vector2(470f, 18f), new Vector2(0f, 1f), new Vector2(0f, 1f));

                TextMeshProUGUI state = MakeLabel("State", row, learned ? "Learned" : "Available", 13f, TextAlignmentOptions.MidlineRight, learned ? Gold : HeatUiStyle.Text);
                SetRect(state.rectTransform, new Vector2(-210f, -41f), new Vector2(92f, 28f), new Vector2(1f, 1f), new Vector2(1f, 1f));

                Button learn = MakeButton("LearnButton", row, learned ? "Known" : "Learn", learned ? HeatUiStyle.CellFilled : HeatUiStyle.Accent, Color.white);
                SetRect((RectTransform)learn.transform, new Vector2(-102f, -44f), new Vector2(84f, 34f), new Vector2(1f, 1f), new Vector2(1f, 1f));
                learn.interactable = !learned;
                HeatUiStyle.SyncButtonInteractable(learn);
                string capturedSpellId = spellId;
                learn.onClick.AddListener(() => Learn(capturedSpellId));
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
            _statusText.color = error ? HeatUiStyle.Error : HeatUiStyle.Success;
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
            return $"{targeting} | {behavior} | Cost {cost:0.#}";
        }

        private static string Capitalize(string value)
            => value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..].ToLowerInvariant();

        private static RectTransform AddBlock(string name, Transform parent, Color color)
        {
            GameObject go = new(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.transform.SetParent(parent, false);
            Image image = go.GetComponent<Image>();
            image.color = color;
            return (RectTransform)go.transform;
        }

        private static TextMeshProUGUI MakeLabel(string name, Transform parent, string text, float fontSize, TextAlignmentOptions alignment, Color color)
        {
            GameObject go = new(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            TextMeshProUGUI label = go.AddComponent<TextMeshProUGUI>();
            label.font = ResolveFont();
            label.fontSize = fontSize;
            label.alignment = alignment;
            label.color = color;
            label.text = text;
            label.textWrappingMode = TextWrappingModes.NoWrap;
            label.overflowMode = TextOverflowModes.Ellipsis;
            label.raycastTarget = false;
            return label;
        }

        private static Button MakeButton(string name, Transform parent, string text, Color fill, Color textColor)
        {
            GameObject go = new(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            Image image = go.GetComponent<Image>();
            image.color = fill;
            Button button = go.GetComponent<Button>();
            HeatUiStyle.StyleButton(button, text, fill, textColor);

            TextMeshProUGUI label = MakeLabel("Text", go.transform, text, 13f, TextAlignmentOptions.Center, textColor);
            label.rectTransform.anchorMin = Vector2.zero;
            label.rectTransform.anchorMax = Vector2.one;
            label.rectTransform.offsetMin = Vector2.zero;
            label.rectTransform.offsetMax = Vector2.zero;
            return button;
        }

        private static void SetRect(RectTransform rt, Vector2 anchoredPosition, Vector2 size, Vector2 anchorMin, Vector2 anchorMax)
        {
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.pivot = anchorMin;
            rt.anchoredPosition = anchoredPosition;
            rt.sizeDelta = size;
        }

        private static bool WasSpellCatalogTogglePressed()
        {
#if ENABLE_INPUT_SYSTEM
            if (Keyboard.current != null)
                return Keyboard.current.kKey.wasPressedThisFrame;
#endif
            return UnityEngine.Input.GetKeyDown(KeyCode.K);
        }

        private static TMP_FontAsset? ResolveFont()
        {
            return TMP_Settings.defaultFontAsset
                ?? Resources.Load<TMP_FontAsset>("Fonts & Materials/LiberationSans SDF");
        }
    }
}
