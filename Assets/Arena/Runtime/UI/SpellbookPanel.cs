#nullable enable

using System;
using System.Collections.Generic;
using Arena.Combat;
using Arena.Network;
using SpacetimeDB.Types;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Arena.UI
{
    [DefaultExecutionOrder(66)]
    public sealed class SpellbookPanel : MonoBehaviour, IEscapeCloseable
    {
        private const float RefreshIntervalSeconds = 0.20f;
        private static readonly Color PanelColor = HeatUiStyle.Panel;
        private static readonly Color HeaderColor = HeatUiStyle.Header;
        private static readonly Color RowColor = HeatUiStyle.Row;
        private static readonly Color Gold = HeatUiStyle.Gold;

        private static SpellbookPanel? s_instance;

        private Canvas? _canvas;
        private GameObject? _panelRoot;
        private RectTransform? _rowRoot;
        private TextMeshProUGUI? _titleText;
        private TextMeshProUGUI? _statusText;
        private string _itemInstanceId = string.Empty;
        private string _fallbackTitle = "Spellbook";
        private string _lastSignature = string.Empty;
        private float _nextRefreshTime;
        private bool _isOpen;

        public int EscapeClosePriority => 95;
        public bool IsEscapeCloseable => _isOpen;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            EnsureInstance();
        }

        public static void Open(string itemInstanceId, string fallbackTitle)
        {
            EnsureInstance().OpenInternal(itemInstanceId, fallbackTitle);
        }

        private static SpellbookPanel EnsureInstance()
        {
            if (s_instance != null)
                return s_instance;

            GameObject go = new("SpellbookPanel");
            DontDestroyOnLoad(go);
            s_instance = go.AddComponent<SpellbookPanel>();
            return s_instance;
        }

        private void Awake()
        {
            s_instance = this;
            RuntimeUiEventSystem.Ensure();

            _canvas = gameObject.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 36;

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
        }

        private void OnDisable()
        {
            RuntimeUiEscapeRouter.Unregister(this);
        }

        private void OnDestroy()
        {
            if (ReferenceEquals(s_instance, this))
                s_instance = null;
            RuntimeUiEscapeRouter.Unregister(this);
        }

        private void Update()
        {
            if (!_isOpen)
                return;

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

        private void OpenInternal(string itemInstanceId, string fallbackTitle)
        {
            _itemInstanceId = itemInstanceId ?? string.Empty;
            _fallbackTitle = string.IsNullOrWhiteSpace(fallbackTitle) ? "Spellbook" : fallbackTitle.Trim();
            _lastSignature = string.Empty;
            _nextRefreshTime = 0f;
            SetOpen(true);
        }

        private void SetOpen(bool open)
        {
            _isOpen = open;
            if (_panelRoot != null)
                _panelRoot.SetActive(open);
            if (open)
            {
                RuntimeUiLayer.BringToFront(_canvas);
                Refresh();
            }
        }

        private void BuildPanel()
        {
            _panelRoot = new GameObject("SpellbookRoot", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            _panelRoot.transform.SetParent(transform, false);
            RectTransform rootRt = (RectTransform)_panelRoot.transform;
            rootRt.anchorMin = new Vector2(0.5f, 0.5f);
            rootRt.anchorMax = new Vector2(0.5f, 0.5f);
            rootRt.pivot = new Vector2(0.5f, 0.5f);
            rootRt.sizeDelta = new Vector2(560f, 620f);
            rootRt.anchoredPosition = new Vector2(360f, 0f);

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

            _titleText = MakeLabel("Title", header, "Spellbook", 24f, TextAlignmentOptions.MidlineLeft, Color.white);
            SetRect(_titleText.rectTransform, new Vector2(22f, -52f), new Vector2(360f, 42f), new Vector2(0f, 1f), new Vector2(0f, 1f));

            Button close = MakeButton("CloseButton", header, "Close", new Color(0.16f, 0.17f, 0.19f, 0.96f), Color.white);
            SetRect((RectTransform)close.transform, new Vector2(-94f, -49f), new Vector2(72f, 34f), new Vector2(1f, 1f), new Vector2(1f, 1f));
            close.onClick.AddListener(() => SetOpen(false));

            RectTransform scrollRoot = new GameObject("SpellScroll", typeof(RectTransform), typeof(ScrollRect)).GetComponent<RectTransform>();
            scrollRoot.SetParent(_panelRoot.transform, false);
            scrollRoot.anchorMin = new Vector2(0f, 0f);
            scrollRoot.anchorMax = new Vector2(1f, 1f);
            scrollRoot.offsetMin = new Vector2(22f, 58f);
            scrollRoot.offsetMax = new Vector2(-22f, -84f);

            RectTransform viewport = new GameObject("Viewport", typeof(RectTransform), typeof(RectMask2D)).GetComponent<RectTransform>();
            viewport.SetParent(scrollRoot, false);
            viewport.anchorMin = Vector2.zero;
            viewport.anchorMax = Vector2.one;
            viewport.offsetMin = Vector2.zero;
            viewport.offsetMax = Vector2.zero;

            _rowRoot = new GameObject("Rows", typeof(RectTransform)).GetComponent<RectTransform>();
            _rowRoot.SetParent(viewport, false);
            _rowRoot.anchorMin = new Vector2(0f, 1f);
            _rowRoot.anchorMax = new Vector2(1f, 1f);
            _rowRoot.pivot = new Vector2(0.5f, 1f);
            _rowRoot.anchoredPosition = Vector2.zero;
            _rowRoot.sizeDelta = Vector2.zero;

            ScrollRect scrollRect = scrollRoot.GetComponent<ScrollRect>();
            scrollRect.viewport = viewport;
            scrollRect.content = _rowRoot;
            scrollRect.horizontal = false;
            scrollRect.vertical = true;
            scrollRect.movementType = ScrollRect.MovementType.Clamped;
            scrollRect.scrollSensitivity = 24f;

            _statusText = MakeLabel("Status", _panelRoot.transform, string.Empty, 13f, TextAlignmentOptions.MidlineLeft, HeatUiStyle.MutedText);
            SetRect(_statusText.rectTransform, new Vector2(22f, 18f), new Vector2(420f, 28f), new Vector2(0f, 0f), new Vector2(0f, 0f));
        }

        private void Refresh()
        {
            if (_rowRoot == null)
                return;

            DbConnection? conn = NetworkManager.Instance?.Conn;
            if (conn == null)
            {
                SetTitle(_fallbackTitle);
                RebuildRows(Array.Empty<ItemSpell>(), conn);
                SetStatus("NO CONNECTION");
                return;
            }

            ItemInstance? item = string.IsNullOrWhiteSpace(_itemInstanceId)
                ? null
                : conn.Db.ItemInstance.ItemInstanceId.Find(_itemInstanceId);
            ItemDefinition? definition = item == null
                ? null
                : conn.Db.ItemDefinition.ItemDefId.Find(item.ItemDefId);
            string title = string.IsNullOrWhiteSpace(definition?.DisplayName)
                ? _fallbackTitle
                : definition.DisplayName;

            List<ItemSpell> spells = ReadSpells(conn, _itemInstanceId);
            string signature = BuildSignature(title, spells, conn);
            if (signature == _lastSignature)
                return;

            _lastSignature = signature;
            SetTitle(title);
            RebuildRows(spells, conn);
            SetStatus(spells.Count == 0 ? "No spells" : $"{spells.Count} spells");
        }

        private void RebuildRows(IReadOnlyList<ItemSpell> spells, DbConnection? conn)
        {
            if (_rowRoot == null)
                return;

            for (int i = _rowRoot.childCount - 1; i >= 0; i--)
                Destroy(_rowRoot.GetChild(i).gameObject);

            if (spells.Count == 0)
            {
                _rowRoot.sizeDelta = Vector2.zero;
                TextMeshProUGUI empty = MakeLabel("Empty", _rowRoot, "No spells", 15f, TextAlignmentOptions.Center, HeatUiStyle.MutedText);
                SetRect(empty.rectTransform, Vector2.zero, new Vector2(360f, 44f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));
                return;
            }

            const float rowHeight = 58f;
            const float rowGap = 8f;
            float contentHeight = spells.Count * rowHeight + Math.Max(0, spells.Count - 1) * rowGap;
            _rowRoot.sizeDelta = new Vector2(0f, contentHeight);
            for (int i = 0; i < spells.Count; i++)
            {
                ItemSpell itemSpell = spells[i];
                string spellId = WireIdentifier.Normalize(itemSpell.SpellId);
                SpellDefinition? spell = string.IsNullOrWhiteSpace(spellId)
                    ? null
                    : conn?.Db.SpellDefinition.Kind.Find(spellId);
                RectTransform row = AddBlock($"Spell_{i}", _rowRoot, i % 2 == 0 ? RowColor : HeatUiStyle.RowAlt);
                row.anchorMin = new Vector2(0f, 1f);
                row.anchorMax = new Vector2(1f, 1f);
                row.pivot = new Vector2(0.5f, 1f);
                row.offsetMin = new Vector2(0f, -((i + 1) * rowHeight + i * rowGap));
                row.offsetMax = new Vector2(0f, -(i * (rowHeight + rowGap)));

                AddSpellIcon(row, conn, spellId);

                TextMeshProUGUI name = MakeLabel("Name", row, DisplayNameForSpell(conn, spellId), 15f, TextAlignmentOptions.MidlineLeft, Color.white);
                SetRect(name.rectTransform, new Vector2(86f, -10f), new Vector2(330f, 24f), new Vector2(0f, 1f), new Vector2(0f, 1f));

                TextMeshProUGUI meta = MakeLabel("Meta", row, MetadataForSpell(spell), 12f, TextAlignmentOptions.MidlineLeft, HeatUiStyle.MutedText);
                SetRect(meta.rectTransform, new Vector2(86f, -34f), new Vector2(390f, 18f), new Vector2(0f, 1f), new Vector2(0f, 1f));
            }
        }

        private static void AddSpellIcon(RectTransform row, DbConnection? conn, string spellId)
        {
            RectTransform frame = AddBlock("IconFrame", row, HeatUiStyle.CellEmpty);
            SetRect(frame, new Vector2(16f, -7f), new Vector2(44f, 44f), new Vector2(0f, 1f), new Vector2(0f, 1f));

            Sprite? iconSprite = ResolveSpellIcon(conn, spellId);
            if (iconSprite != null)
            {
                GameObject iconGo = new("Icon", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
                iconGo.transform.SetParent(frame, false);
                RectTransform iconRt = (RectTransform)iconGo.transform;
                iconRt.anchorMin = Vector2.zero;
                iconRt.anchorMax = Vector2.one;
                iconRt.offsetMin = new Vector2(3f, 3f);
                iconRt.offsetMax = new Vector2(-3f, -3f);
                Image icon = iconGo.GetComponent<Image>();
                icon.sprite = iconSprite;
                icon.color = Color.white;
                icon.preserveAspect = true;
                icon.raycastTarget = false;
                return;
            }

            TextMeshProUGUI fallback = MakeLabel("IconFallback", frame, IconFallbackText(spellId), 11f, TextAlignmentOptions.Center, Gold);
            fallback.fontStyle = FontStyles.Bold;
            fallback.rectTransform.anchorMin = Vector2.zero;
            fallback.rectTransform.anchorMax = Vector2.one;
            fallback.rectTransform.offsetMin = Vector2.zero;
            fallback.rectTransform.offsetMax = Vector2.zero;
        }

        private static Sprite? ResolveSpellIcon(DbConnection? conn, string spellId)
        {
            string normalizedSpellId = WireIdentifier.Normalize(spellId);
            if (string.IsNullOrWhiteSpace(normalizedSpellId))
                return null;

            if (conn != null)
            {
                foreach (AbilityCatalog ability in conn.Db.AbilityCatalog.Iter())
                {
                    if (!string.Equals(WireIdentifier.Normalize(ability.ActionId), normalizedSpellId, StringComparison.Ordinal))
                        continue;

                    Sprite? abilityIcon = ActionIconResolver.Resolve(ActionKinds.Ability, ability.AbilityId);
                    if (abilityIcon != null)
                        return abilityIcon;
                }
            }

            return ActionIconResolver.Resolve(ActionKinds.Ability, normalizedSpellId);
        }

        private static string IconFallbackText(string spellId)
        {
            string normalized = WireIdentifier.Normalize(spellId);
            if (string.IsNullOrWhiteSpace(normalized))
                return "?";

            string[] parts = normalized.Split('_', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 1)
                return parts[0].Length <= 2 ? parts[0] : parts[0][..2];

            string first = parts[0].Length == 0 ? string.Empty : parts[0][..1];
            string second = parts[1].Length == 0 ? string.Empty : parts[1][..1];
            string fallback = $"{first}{second}";
            return string.IsNullOrWhiteSpace(fallback) ? "?" : fallback;
        }

        private static List<ItemSpell> ReadSpells(DbConnection conn, string itemInstanceId)
        {
            List<ItemSpell> spells = new();
            if (string.IsNullOrWhiteSpace(itemInstanceId))
                return spells;

            foreach (ItemSpell spell in conn.Db.ItemSpell.ItemInstanceId.Filter(itemInstanceId))
                spells.Add(spell);

            spells.Sort((left, right) =>
            {
                int sort = left.SlotIndex.CompareTo(right.SlotIndex);
                return sort != 0
                    ? sort
                    : string.Compare(left.Key, right.Key, StringComparison.Ordinal);
            });
            return spells;
        }

        private static string BuildSignature(string title, IReadOnlyList<ItemSpell> spells, DbConnection conn)
        {
            List<string> parts = new() { title };
            foreach (ItemSpell spell in spells)
            {
                string spellId = WireIdentifier.Normalize(spell.SpellId);
                SpellDefinition? definition = string.IsNullOrWhiteSpace(spellId)
                    ? null
                    : conn.Db.SpellDefinition.Kind.Find(spellId);
                parts.Add($"{spell.SlotIndex}:{spellId}:{MetadataForSpell(definition)}");
            }
            return string.Join("|", parts);
        }

        private static string DisplayNameForSpell(DbConnection? conn, string spellId)
        {
            if (string.IsNullOrWhiteSpace(spellId))
                return "Unknown Spell";

            return ActionPresentation.ResolveDisplayName(conn, conn?.Identity, spellId, spellId);
        }

        private static string MetadataForSpell(SpellDefinition? spell)
        {
            if (spell == null)
                return "Unknown";

            List<string> parts = new();
            float cost = Math.Max(0f, spell.PrimaryResourceCost);
            parts.Add(cost > 0.0001f ? $"{cost:0.#} Mana" : "Free");
            if (spell.CooldownMs > 0)
                parts.Add($"{spell.CooldownMs / 1000f:0.#}s");

            string targeting = WireIdentifier.Normalize(spell.Targeting);
            if (!string.IsNullOrWhiteSpace(targeting))
                parts.Add(targeting);

            return string.Join(" - ", parts);
        }

        private void SetTitle(string title)
        {
            if (_titleText != null)
                _titleText.text = string.IsNullOrWhiteSpace(title) ? "Spellbook" : title;
        }

        private void SetStatus(string status)
        {
            if (_statusText != null)
                _statusText.text = status;
        }

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

        private static TMP_FontAsset? ResolveFont()
        {
            return TMP_Settings.defaultFontAsset
                ?? Resources.Load<TMP_FontAsset>("Fonts & Materials/LiberationSans SDF");
        }
    }
}
