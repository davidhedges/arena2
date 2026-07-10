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
        private const float RowHeight = 58f;
        private const float RowGap = 8f;

        private static SpellbookPanel? s_instance;

        private Canvas? _canvas;
        private ArenaWindow? _window;
        private RectTransform? _rowRoot;
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
            if (!ArenaRuntimeSceneGate.ShouldRunArenaRuntimeInActiveScene())
                return;

            EnsureInstance();
        }

        public static void Open(string itemInstanceId, string fallbackTitle)
        {
            if (!ArenaRuntimeSceneGate.ShouldRunArenaRuntimeInActiveScene())
                return;

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
            _canvas = ArenaUiKit.MakeOverlayCanvas(gameObject, 36);
            BuildPanel();
            SetOpen(false, instant: true);
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
            if (!ArenaRuntimeSceneGate.ShouldRunArenaRuntimeInActiveScene())
            {
                if (_isOpen)
                    SetOpen(false, instant: true);
                return;
            }

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

        private void SetOpen(bool open, bool instant = false)
        {
            _isOpen = open;
            _window?.SetVisible(open, instant);
            if (open)
            {
                RuntimeUiLayer.BringToFront(_canvas);
                Refresh();
            }
        }

        private void BuildPanel()
        {
            _window = ArenaWindow.Create(transform, "SpellbookWindow", "Spellbook", new Vector2(560f, 620f));
            _window.Rect.anchoredPosition = new Vector2(360f, 0f);
            _window.CloseRequested += () => SetOpen(false);

            _rowRoot = ArenaUiKit.MakeScrollView(_window.Content, "SpellScroll", out ScrollRect scrollRect);
            ArenaUiKit.Fill((RectTransform)scrollRect.transform);
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
                _rowRoot.sizeDelta = new Vector2(0f, 44f);
                TextMeshProUGUI empty = ArenaUiKit.MakeText(
                    _rowRoot,
                    "Empty",
                    "No spells",
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
                ItemSpell itemSpell = spells[i];
                string spellId = WireIdentifier.Normalize(itemSpell.SpellId);
                SpellDefinition? spell = string.IsNullOrWhiteSpace(spellId)
                    ? null
                    : conn?.Db.SpellDefinition.Kind.Find(spellId);
                RectTransform row = ArenaUiKit.MakePanel(
                    _rowRoot,
                    $"Spell_{i}",
                    i % 2 == 0 ? ArenaUiTheme.Row : ArenaUiTheme.RowAlt);
                ArenaUiKit.SetAnchors(
                    row,
                    new Vector2(0f, 1f),
                    new Vector2(1f, 1f),
                    new Vector2(0f, -((i + 1) * RowHeight + i * RowGap)),
                    new Vector2(0f, -(i * (RowHeight + RowGap))));

                AddSpellIcon(row, conn, spellId);

                TextMeshProUGUI name = ArenaUiKit.MakeText(
                    row,
                    "Name",
                    DisplayNameForSpell(conn, spellId),
                    15f,
                    ArenaUiTheme.Text,
                    ArenaUiTheme.StrongFont);
                ArenaUiKit.PlaceTopLeft(name.rectTransform, new Vector2(64f, 10f), new Vector2(330f, 24f));

                TextMeshProUGUI meta = ArenaUiKit.MakeText(
                    row,
                    "Meta",
                    MetadataForSpell(conn, spellId, spell),
                    ArenaUiTheme.SmallSize,
                    ArenaUiTheme.MutedText);
                ArenaUiKit.PlaceTopLeft(meta.rectTransform, new Vector2(64f, 34f), new Vector2(390f, 18f));
            }
        }

        private static void AddSpellIcon(RectTransform row, DbConnection? conn, string spellId)
        {
            RectTransform frame = ArenaUiKit.MakePanel(
                row,
                "IconFrame",
                ArenaUiTheme.CellEmpty,
                raycastTarget: false,
                hairline: true);
            ArenaUiKit.PlaceTopLeft(frame, new Vector2(10f, 7f), new Vector2(44f, 44f));

            Sprite? iconSprite = ResolveSpellIcon(conn, spellId);
            if (iconSprite != null)
            {
                RectTransform iconRt = ArenaUiKit.MakeRect(frame, "Icon");
                ArenaUiKit.Fill(iconRt, new Vector2(3f, 3f));
                Image icon = iconRt.gameObject.AddComponent<Image>();
                icon.sprite = iconSprite;
                icon.color = Color.white;
                icon.preserveAspect = true;
                icon.raycastTarget = false;
                return;
            }

            TextMeshProUGUI fallback = ArenaUiKit.MakeText(
                frame,
                "IconFallback",
                IconFallbackText(spellId),
                11f,
                ArenaUiTheme.Gold,
                ArenaUiTheme.StrongFont,
                TextAlignmentOptions.Center);
            fallback.fontStyle = FontStyles.Bold;
            ArenaUiKit.Fill(fallback.rectTransform);
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
                parts.Add($"{spell.SlotIndex}:{spellId}:{MetadataForSpell(conn, spellId, definition)}");
            }
            return string.Join("|", parts);
        }

        private static string DisplayNameForSpell(DbConnection? conn, string spellId)
        {
            if (string.IsNullOrWhiteSpace(spellId))
                return "Unknown Spell";

            return ActionPresentation.ResolveDisplayName(conn, conn?.Identity, spellId, spellId);
        }

        private static string MetadataForSpell(DbConnection? conn, string spellId, SpellDefinition? spell)
        {
            if (spell == null)
                return "Unknown";

            AbilityCatalog? ability = FindAbilityForSpell(conn, spellId);
            List<string> parts = new();
            float cost = Math.Max(0f, spell.PrimaryResourceCost);
            parts.Add(cost > 0.0001f ? FormatSpellResourceCost(conn, ability, spell, cost) : "Free");
            if (spell.CooldownMs > 0)
                parts.Add($"{spell.CooldownMs / 1000f:0.#}s");

            string targeting = WireIdentifier.Normalize(spell.Targeting);
            if (!string.IsNullOrWhiteSpace(targeting))
                parts.Add(targeting);

            return string.Join(" - ", parts);
        }

        private static AbilityCatalog? FindAbilityForSpell(DbConnection? conn, string spellId)
        {
            string normalizedSpellId = WireIdentifier.Normalize(spellId);
            if (conn == null || string.IsNullOrWhiteSpace(normalizedSpellId))
                return null;

            foreach (AbilityCatalog ability in conn.Db.AbilityCatalog.Iter())
            {
                if (!string.Equals(WireIdentifier.Normalize(ability.ActionId), normalizedSpellId, StringComparison.Ordinal))
                    continue;
                if (string.Equals(WireIdentifier.Normalize(ability.AbilityKind), AbilityKinds.Spell, StringComparison.Ordinal))
                    return ability;
            }

            return null;
        }

        private static string FormatSpellResourceCost(DbConnection? conn, AbilityCatalog? ability, SpellDefinition spell, float cost)
        {
            string resource = ResolveResourceDisplayName(conn, ability?.ResourceKind);
            string suffix = string.Equals(spell.Behavior, SpellDefinitionContracts.BehaviorChannel, StringComparison.Ordinal)
                ? "/s"
                : string.Empty;
            return $"{cost:0.#} {resource}{suffix}";
        }

        private static string ResolveResourceDisplayName(DbConnection? conn, string? resourceKind)
        {
            string normalized = WireIdentifier.Normalize(resourceKind);
            if (string.IsNullOrWhiteSpace(normalized))
                normalized = "MANA";

            ResourceCatalog? resource = conn?.Db.ResourceCatalog.ResourceKind.Find(normalized);
            return string.IsNullOrWhiteSpace(resource?.DisplayName)
                ? normalized
                : resource.DisplayName;
        }

        private void SetTitle(string title)
        {
            _window?.SetTitle(string.IsNullOrWhiteSpace(title) ? "Spellbook" : title);
        }

        private void SetStatus(string status)
        {
            _window?.SetSubtitle(status);
        }
    }
}
