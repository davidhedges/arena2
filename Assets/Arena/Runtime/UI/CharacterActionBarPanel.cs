#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Arena.Combat;
using Arena.Network;
using SpacetimeDB;
using SpacetimeDB.Types;
using TMPro;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif
using UnityEngine.UI;

namespace Arena.UI
{
    [DefaultExecutionOrder(66)]
    public sealed class CharacterActionBarPanel : MonoBehaviour, IEscapeCloseable
    {
        private const float RefreshIntervalSeconds = 0.20f;
        private const int AvailableColumns = 7;
        private const float AvailableGap = 8f;
        private const float AvailableSectionHeaderHeight = 28f;
        private const float AvailableSectionGap = 14f;
        private const string ActionBarActionTag = "ACTION_BAR_ACTION";
        private const string AllWeaponsFilterKey = "ALL_WEAPONS";
        private const string SpellsFilterKey = "SPELLS";
        private const string SpellbookDropSlotPrefix = "SPELLBOOK_";
        private const uint SpellsCategorySortOrder = uint.MaxValue - 3;

        private const float AvailableWidth = 536f;
        private const float AvailableHeight = 524f;
        private static readonly Vector2 WindowSize = new(1240f, 720f);

        // Disabled action cells sit between the empty and enabled surfaces.
        private static Color DisabledActionColor => ArenaUiTheme.CellFilled;
        private static Color DisabledActionTextColor => ArenaUiTheme.MutedText;

        private Canvas? _canvas;
        private ArenaWindow? _window;
        private RectTransform? _availableRoot;
        private RectTransform? _availableContent;
        private RectTransform? _barRoot;
        private TextMeshProUGUI? _status;
        private Button? _weaponFilterButton;
        private TextMeshProUGUI? _weaponFilterLabel;
        private RectTransform? _weaponFilterMenu;
        private DbConnection? _subscribedConnection;
        private readonly List<GameObject> _availableCells = new();
        private readonly List<GameObject> _barCells = new();
        private readonly List<GameObject> _spellbookCells = new();
        private readonly List<GameObject> _weaponFilterMenuCells = new();
        private readonly List<WeaponFilterOption> _weaponFilterOptions = new();
        private AvailableAction _selectedAction;
        private string _lastSignature = string.Empty;
        private string _lastError = string.Empty;
        private string _weaponFilterKey = AllWeaponsFilterKey;
        private float _errorUntilTime;
        private float _nextRefreshTime;
        private bool _isOpen;
        private bool _weaponFilterMenuOpen;

        public int EscapeClosePriority => 89;
        public bool IsEscapeCloseable => _isOpen;

        private readonly struct AvailableAction
        {
            public readonly string ActionKind;
            public readonly string ActionId;
            public readonly string AbilityId;
            public readonly string DisplayName;
            public readonly string CategoryKey;
            public readonly string CategoryTitle;
            public readonly uint CategorySortOrder;
            public readonly uint SortOrder;
            public readonly bool IsFixed;
            public readonly bool IsUsable;
            public readonly string DisabledReason;

            public AvailableAction(
                string actionKind,
                string actionId,
                string abilityId,
                string displayName,
                string categoryKey,
                string categoryTitle,
                uint categorySortOrder,
                uint sortOrder,
                bool isFixed,
                bool isUsable,
                string disabledReason = "")
            {
                ActionKind = WireIdentifier.Normalize(actionKind);
                ActionId = WireIdentifier.Normalize(actionId);
                AbilityId = WireIdentifier.Normalize(abilityId);
                DisplayName = displayName;
                CategoryKey = WireIdentifier.Normalize(categoryKey);
                CategoryTitle = string.IsNullOrWhiteSpace(categoryTitle) ? "General" : categoryTitle;
                CategorySortOrder = categorySortOrder;
                SortOrder = sortOrder;
                IsFixed = isFixed;
                IsUsable = isUsable;
                DisabledReason = disabledReason;
            }

            public bool HasValue => !string.IsNullOrWhiteSpace(ActionKind)
                && !string.IsNullOrWhiteSpace(ActionId);

            public ActionBarDragPayload ToDragPayload(string sourceSlotId = "")
                => new(ActionKind, ActionId, AbilityId, DisplayName, sourceSlotId);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (FindAnyObjectByType<CharacterActionBarPanel>() != null)
                return;
            if (!ArenaRuntimeSceneGate.ShouldRunArenaRuntimeInActiveScene())
                return;

            GameObject go = new("CharacterActionBarPanel");
            DontDestroyOnLoad(go);
            go.AddComponent<CharacterActionBarPanel>();
        }

        private void Awake()
        {
            RuntimeUiEventSystem.Ensure();
            BuildUi();
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

            if (WasTogglePressed())
                SetOpen(!_isOpen);

            if (!_isOpen)
                return;

            if (_status != null && _errorUntilTime > 0f && Time.unscaledTime >= _errorUntilTime)
            {
                _lastError = string.Empty;
                _status.text = string.Empty;
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
            if (!_isOpen)
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

        private void BuildUi()
        {
            GameObject canvasGo = new("CharacterActionBarCanvas");
            canvasGo.transform.SetParent(transform, false);
            _canvas = ArenaUiKit.MakeOverlayCanvas(canvasGo, 34);

            _window = ArenaWindow.Create(
                canvasGo.transform,
                "CharacterActionBarRoot",
                "Action Bar",
                WindowSize);
            _window.CloseRequested += () => SetOpen(false);

            RectTransform content = _window.Content;

            ArenaButtonHandle filterButton = ArenaUiKit.MakeButton(
                content,
                "WeaponFilterButton",
                "All Actions",
                ArenaButtonStyle.Secondary,
                ToggleWeaponFilterMenu);
            SetRect(
                filterButton.Rect,
                Vector2.zero,
                new Vector2(AvailableWidth, ArenaUiTheme.ButtonHeight),
                new Vector2(0f, 1f),
                new Vector2(0f, 1f));
            _weaponFilterButton = filterButton.Button;
            _weaponFilterLabel = filterButton.Label;

            _availableRoot = ArenaUiKit.MakeRect(content, "AvailableActions");
            SetRect(
                _availableRoot,
                new Vector2(0f, -(ArenaUiTheme.ButtonHeight + 8f)),
                new Vector2(AvailableWidth, AvailableHeight),
                new Vector2(0f, 1f),
                new Vector2(0f, 1f));
            Image availableMaskImage = _availableRoot.gameObject.AddComponent<Image>();
            availableMaskImage.color = Color.clear;
            _availableRoot.gameObject.AddComponent<RectMask2D>();
            ScrollRect availableScroll = _availableRoot.gameObject.AddComponent<ScrollRect>();
            availableScroll.horizontal = false;
            availableScroll.vertical = true;
            availableScroll.movementType = ScrollRect.MovementType.Clamped;
            availableScroll.scrollSensitivity = 42f;

            _availableContent = ArenaUiKit.MakeRect(_availableRoot, "AvailableActionsContent");
            _availableContent.anchorMin = new Vector2(0f, 1f);
            _availableContent.anchorMax = new Vector2(0f, 1f);
            _availableContent.pivot = new Vector2(0f, 1f);
            _availableContent.anchoredPosition = Vector2.zero;
            _availableContent.sizeDelta = _availableRoot.sizeDelta;
            availableScroll.viewport = _availableRoot;
            availableScroll.content = _availableContent;

            _weaponFilterMenu = ArenaUiKit.MakePanel(
                content,
                "WeaponFilterMenu",
                ArenaUiTheme.PanelStrong,
                hairline: true);
            SetRect(
                _weaponFilterMenu,
                new Vector2(0f, -(ArenaUiTheme.ButtonHeight + 2f)),
                new Vector2(AvailableWidth, 0f),
                new Vector2(0f, 1f),
                new Vector2(0f, 1f));
            _weaponFilterMenu.gameObject.SetActive(false);

            _barRoot = ArenaUiKit.MakeRect(content, "ActionBarGrid");
            _barRoot.anchorMin = new Vector2(1f, 0f);
            _barRoot.anchorMax = new Vector2(1f, 0f);
            _barRoot.pivot = new Vector2(1f, 0f);
            _barRoot.anchoredPosition = new Vector2(0f, 72f);
            _barRoot.sizeDelta = ActionBarLayout.GridSize;

            _status = ArenaUiKit.MakeText(
                content,
                "Status",
                string.Empty,
                ArenaUiTheme.BodySize,
                ArenaUiTheme.Danger);
            SetRect(
                _status.rectTransform,
                Vector2.zero,
                new Vector2(720f, 28f),
                Vector2.zero,
                Vector2.zero);

            TextMeshProUGUI hint = ArenaUiKit.MakeText(
                content,
                "Hint",
                "J",
                ArenaUiTheme.BodySize,
                ArenaUiTheme.MutedText,
                alignment: TextAlignmentOptions.MidlineRight);
            SetRect(
                hint.rectTransform,
                Vector2.zero,
                new Vector2(120f, 28f),
                new Vector2(1f, 0f),
                new Vector2(1f, 0f));
        }

        private void Refresh()
        {
            if (_canvas == null || _availableRoot == null || _availableContent == null || _barRoot == null)
                return;

            DbConnection? conn = NetworkManager.Instance?.Conn;
            Identity? owner = conn?.Identity;
            if (conn == null || !owner.HasValue)
            {
                Rebuild(Array.Empty<AvailableAction>(), conn, owner);
                SetStatus("NO CONNECTION", false);
                return;
            }

            string combatProfile = CombatProfileResolver.ResolveForOwner(conn, owner.Value);
            int spellSlotCapacity = SpellSlotResolver.Capacity(conn, owner.Value);
            int assignedSpellSlots = SpellSlotResolver.AssignedSpellCount(conn, owner.Value);
            UpdateWeaponFilterOptions(conn);
            List<AvailableAction> actions = BuildAvailableActions(conn, owner.Value, combatProfile, _weaponFilterKey);
            string signature = BuildSignature(conn, owner.Value, combatProfile, actions, _selectedAction, assignedSpellSlots, spellSlotCapacity, _weaponFilterKey);
            if (signature == _lastSignature)
                return;

            _lastSignature = signature;
            Rebuild(actions, conn, owner);
            _window?.SetTitle(string.IsNullOrWhiteSpace(combatProfile)
                ? "Action Bar"
                : $"Action Bar | {combatProfile}");
            _window?.SetSubtitle($"Spell slots {assignedSpellSlots}/{spellSlotCapacity}");
        }

        private void Rebuild(IReadOnlyList<AvailableAction> actions, DbConnection? conn, Identity? owner)
        {
            Clear(_availableCells);
            Clear(_barCells);
            Clear(_spellbookCells);

            if (_canvas == null || _availableRoot == null || _availableContent == null || _barRoot == null)
                return;

            float cursorY = 0f;
            string categoryKey = string.Empty;
            int indexInCategory = 0;
            for (int i = 0; i < actions.Count; i++)
            {
                AvailableAction action = actions[i];
                if (!string.Equals(categoryKey, action.CategoryKey, StringComparison.Ordinal))
                {
                    if (!string.IsNullOrWhiteSpace(categoryKey))
                        cursorY += AvailableSectionGap;

                    categoryKey = action.CategoryKey;
                    indexInCategory = 0;
                    float availableWidth = _availableRoot.rect.width > 0f
                        ? _availableRoot.rect.width
                        : _availableRoot.sizeDelta.x;
                    GameObject header = CreateAvailableSectionHeader(_availableContent, action.CategoryTitle);
                    SetRect(
                        (RectTransform)header.transform,
                        new Vector2(0f, -cursorY),
                        new Vector2(availableWidth, AvailableSectionHeaderHeight),
                        new Vector2(0f, 1f),
                        new Vector2(0f, 1f));
                    _availableCells.Add(header);
                    cursorY += AvailableSectionHeaderHeight;
                }

                int row = indexInCategory / AvailableColumns;
                int col = indexInCategory % AvailableColumns;
                Vector2 position = new(
                    col * (ActionBarLayout.SlotSize + AvailableGap),
                    -(cursorY + row * (ActionBarLayout.SlotSize + AvailableGap)));
                bool capacitorCharged = CapacitorPresentation.IsCapacitorAbility(action.AbilityId, action.ActionId)
                    && CapacitorPresentation.IsCharged(conn, owner);
                Sprite? iconSprite = ActionIconResolver.ResolveForAvailableAction(
                    conn,
                    owner,
                    action.ActionKind,
                    action.ActionId,
                    action.AbilityId);
                TooltipData tooltip = action.IsFixed
                    ? ActionTooltipResolver.ResolveForFixedAction(conn, action.ActionId)
                    : ActionTooltipResolver.ResolveForAbility(conn, owner, conn?.Db.AbilityCatalog.AbilityId.Find(action.AbilityId));
                bool canAssignToSpellbook = AvailableActionCanAssignToSpellbook(action);
                bool canInteract = action.IsUsable || canAssignToSpellbook;
                if (!action.IsUsable && canAssignToSpellbook)
                    tooltip = SpellbookAssignableTooltip(tooltip, action);
                else if (!action.IsUsable)
                    tooltip = DisabledTooltip(tooltip, action);
                GameObject cell = ActionBarSlotViewFactory.Create(
                    _availableContent,
                    $"Available_{action.ActionKind}_{action.ActionId}",
                    iconSprite == null ? action.DisplayName : string.Empty,
                    string.Empty,
                    canInteract
                        ? action.IsFixed ? FixedActionColor(action.ActionId) : AbilityColor(action.AbilityId)
                        : DisabledActionColor,
                    canInteract ? Color.white : DisabledActionTextColor,
                    iconSprite,
                    _canvas,
                    payloadProvider: canInteract ? () => action.ToDragPayload() : null,
                    onDrop: canInteract ? HandleActionDrop : null,
                    onClick: action.IsUsable ? () =>
                    {
                        _selectedAction = action;
                        _lastSignature = string.Empty;
                    } : null,
                    tooltipData: tooltip);
                ActionBarSlotViewFactory.SetReadyBorder(cell, capacitorCharged);
                if (!canInteract && iconSprite != null)
                    TintIcon(cell, DisabledActionTextColor);
                SetRect((RectTransform)cell.transform, position, ActionBarLayout.SlotVector, new Vector2(0f, 1f), new Vector2(0f, 1f));

                Outline outline = cell.AddComponent<Outline>();
                outline.effectColor = SameAction(_selectedAction, action) ? ArenaUiTheme.Gold : ArenaUiTheme.Hairline;
                outline.effectDistance = SameAction(_selectedAction, action) ? new Vector2(2f, -2f) : new Vector2(1f, -1f);
                _availableCells.Add(cell);
                indexInCategory++;

                bool endsCategory = i + 1 >= actions.Count
                    || !string.Equals(action.CategoryKey, actions[i + 1].CategoryKey, StringComparison.Ordinal);
                if (endsCategory)
                {
                    int rows = Mathf.CeilToInt(indexInCategory / (float)AvailableColumns);
                    cursorY += rows * ActionBarLayout.SlotSize
                        + Mathf.Max(0, rows - 1) * AvailableGap;
                }
            }

            float contentWidth = _availableRoot.rect.width > 0f
                ? _availableRoot.rect.width
                : _availableRoot.sizeDelta.x;
            float contentHeight = _availableRoot.rect.height > 0f
                ? _availableRoot.rect.height
                : _availableRoot.sizeDelta.y;
            _availableContent.sizeDelta = new Vector2(
                contentWidth,
                Mathf.Max(contentHeight, cursorY));

            BuildSpellbookRow(conn, owner);

            for (int row = 0; row < ActionBarLayout.VisibleActionRows; row++)
            {
                for (int col = 0; col < ActionBarLayout.Columns; col++)
                {
                    Vector2 position = ActionBarLayout.ActionCellPosition(row, col);
                    string keyLabel = ActionBarKeymap.KeyLabelForCell(row, col);
                    ActiveActionBarAction resolved = default;
                    string slotId = string.Empty;
                    if (ActionBarKeymap.TryGetBindingForCell(row, col, out ActionBarSlotBinding binding))
                    {
                        slotId = binding.SlotId;
                        resolved = ActiveActionBarResolver.ResolveActiveSelectableAction(conn, owner, binding.SlotId);
                        keyLabel = binding.KeyLabel;
                    }

                    bool hasAction = resolved.HasAssignedAction;
                    string displayName = resolved.DisplayName ?? string.Empty;
                    string actionId = resolved.ActionId ?? string.Empty;
                    string abilityId = resolved.AbilityId ?? string.Empty;
                    bool capacitorCharged = hasAction
                        && CapacitorPresentation.IsCapacitorAction(resolved)
                        && CapacitorPresentation.IsCharged(conn, owner);
                    Sprite? iconSprite = hasAction ? ActionIconResolver.ResolveForAction(conn, owner, resolved) : null;
                    TooltipData tooltip = hasAction
                        ? ActionTooltipResolver.ResolveForActionRef(conn, owner, resolved)
                        : default;
                    GameObject cell = ActionBarSlotViewFactory.Create(
                        _barRoot,
                        $"ActionBar_{row}_{col}",
                        iconSprite == null && hasAction ? displayName : string.Empty,
                        keyLabel,
                        hasAction ? (resolved.IsFixed ? FixedActionColor(actionId) : AbilityColor(abilityId)) : ArenaUiTheme.CellEmpty,
                        hasAction ? Color.white : ArenaUiTheme.MutedText,
                        iconSprite,
                        _canvas,
                        tooltipData: tooltip);
                    ActionBarSlotViewFactory.SetReadyBorder(cell, capacitorCharged);
                    SetRect((RectTransform)cell.transform, position, ActionBarLayout.SlotVector, new Vector2(0f, 0f), new Vector2(0f, 0f));
                    _barCells.Add(cell);
                }
            }
        }

        private void BuildSpellbookRow(DbConnection? conn, Identity? owner)
        {
            if (_canvas == null || _barRoot == null)
                return;

            List<ItemSpell> spells = ReadEquippedSpellbookSpells(conn, owner);
            for (int col = 0; col < ActionBarLayout.Columns; col++)
            {
                ItemSpell? itemSpell = FindSpellbookSlot(spells, col);
                string spellId = WireIdentifier.Normalize(itemSpell?.SpellId);
                AbilityCatalog? ability = FindAbilityForSpell(conn, spellId);
                bool hasSpell = !string.IsNullOrWhiteSpace(spellId);
                bool capacitorCharged = hasSpell
                    && CapacitorPresentation.IsCapacitorAbility(ability?.AbilityId ?? string.Empty, spellId)
                    && CapacitorPresentation.IsCharged(conn, owner);
                string displayName = hasSpell
                    ? ActionPresentation.ResolveDisplayName(conn, owner, spellId, spellId)
                    : string.Empty;
                Sprite? iconSprite = hasSpell
                    ? ability == null
                        ? ActionIconResolver.Resolve(ActionKinds.Ability, spellId)
                        : ActionIconResolver.Resolve(
                            ActionKinds.Ability,
                            CapacitorPresentation.ResolvePresentationId(conn, owner, ability.AbilityId, spellId))
                    : null;
                TooltipData tooltip = hasSpell
                    ? ability == null
                        ? new TooltipData(displayName, "Spellbook", SpellbookSpellMetadata(conn, spellId))
                        : ActionTooltipResolver.ResolveForAbility(conn, owner, ability)
                    : default;
                GameObject cell = ActionBarSlotViewFactory.Create(
                    _barRoot,
                    $"Spellbook_{col}",
                    iconSprite == null && hasSpell ? displayName : string.Empty,
                    SpellbookKeymap.KeyLabelForIndex(col),
                    hasSpell ? AbilityColor(ability?.AbilityId ?? spellId) : ArenaUiTheme.CellEmpty,
                    hasSpell ? Color.white : ArenaUiTheme.MutedText,
                    iconSprite,
                    _canvas,
                    SpellbookDropSlotId(col),
                    tooltipData: tooltip);
                ActionBarSlotViewFactory.SetReadyBorder(cell, capacitorCharged);
                SetRect((RectTransform)cell.transform, ActionBarLayout.SpellbookCellPosition(col), ActionBarLayout.SlotVector, new Vector2(0f, 0f), new Vector2(0f, 0f));
                _spellbookCells.Add(cell);
            }
        }

        private void AssignSelectedActionToSlot(string slotId)
        {
            if (!_selectedAction.HasValue || !_selectedAction.IsUsable || string.IsNullOrWhiteSpace(slotId))
                return;

            DbConnection? conn = NetworkManager.Instance?.Conn;
            if (conn == null)
                return;

            if (!CanApplyPayloadToSlot(conn, _selectedAction.ToDragPayload(), slotId))
                return;

            ActionBarDropApplier.ApplyDrop(conn, _selectedAction.ToDragPayload(), slotId);
            _lastSignature = string.Empty;
            _nextRefreshTime = 0f;
        }

        private static List<ItemSpell> ReadEquippedSpellbookSpells(DbConnection? conn, Identity? owner)
        {
            List<ItemSpell> spells = new();
            if (conn == null || !owner.HasValue)
                return spells;

            EquipmentLoadout? loadout = conn.Db.EquipmentLoadout.Owner.Find(owner.Value);
            if (loadout == null || string.IsNullOrWhiteSpace(loadout.SpellbookItemId))
                return spells;

            foreach (ItemSpell spell in conn.Db.ItemSpell.ItemInstanceId.Filter(loadout.SpellbookItemId))
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

        private static ItemSpell? FindSpellbookSlot(List<ItemSpell> spells, int slotIndex)
        {
            foreach (ItemSpell spell in spells)
            {
                if (spell.SlotIndex == (uint)slotIndex)
                    return spell;
            }

            return null;
        }

        private static AbilityCatalog? FindAbilityForSpell(DbConnection? conn, string spellId)
        {
            if (conn == null || string.IsNullOrWhiteSpace(spellId))
                return null;

            string normalizedSpellId = WireIdentifier.Normalize(spellId);
            foreach (AbilityCatalog ability in conn.Db.AbilityCatalog.Iter())
            {
                if (!string.Equals(WireIdentifier.Normalize(ability.AbilityKind), AbilityKinds.Spell, StringComparison.Ordinal))
                    continue;
                if (string.Equals(WireIdentifier.Normalize(ability.ActionId), normalizedSpellId, StringComparison.Ordinal))
                    return ability;
            }

            return null;
        }

        private static string SpellbookSpellMetadata(DbConnection? conn, string spellId)
        {
            SpellDefinition? spell = conn?.Db.SpellDefinition.Kind.Find(WireIdentifier.Normalize(spellId));
            if (spell == null)
                return string.Empty;

            AbilityCatalog? ability = FindAbilityForSpell(conn, spellId);
            List<string> parts = new();
            if (spell.PrimaryResourceCost > 0.0001f)
                parts.Add(FormatSpellResourceCost(conn, ability, spell));
            if (spell.CooldownMs > 0)
                parts.Add($"{spell.CooldownMs / 1000f:0.#}s");
            if (!string.IsNullOrWhiteSpace(spell.Targeting))
                parts.Add(WireIdentifier.Normalize(spell.Targeting));

            return string.Join(" - ", parts);
        }

        private static string FormatSpellResourceCost(DbConnection? conn, AbilityCatalog? ability, SpellDefinition spell)
        {
            string resource = ResolveResourceDisplayName(conn, ability?.ResourceKind);
            string suffix = SpellDefinitionContracts.UsesPerSecondResourceCost(spell)
                ? "/s"
                : string.Empty;
            return $"{spell.PrimaryResourceCost:0.#} {resource}{suffix}";
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

        private void HandleActionDrop(ActionBarDragPayload payload, string? targetSlotId)
        {
            DbConnection? conn = NetworkManager.Instance?.Conn;
            if (conn == null)
                return;

            if (TryHandleSpellbookDrop(conn, payload, targetSlotId))
                return;

            if (!CanApplyPayloadToSlot(conn, payload, targetSlotId))
                return;

            ActionBarDropApplier.ApplyDrop(conn, payload, targetSlotId);
            _lastSignature = string.Empty;
            _nextRefreshTime = 0f;
        }

        private bool TryHandleSpellbookDrop(DbConnection conn, ActionBarDragPayload payload, string? targetSlotId)
        {
            if (!TryParseSpellbookDropSlot(targetSlotId, out uint slotIndex))
                return false;

            if (!TryResolveSpellIdForPayload(conn, payload, out string spellId))
            {
                SetStatus("Drop an authored spell here", true);
                return true;
            }

            conn.Reducers.AssignEquippedSpellbookSpell(slotIndex, spellId);
            _lastSignature = string.Empty;
            _nextRefreshTime = 0f;
            SetStatus($"Spellbook slot {slotIndex + 1}: {payload.DisplayName}", false);
            return true;
        }

        private List<AvailableAction> BuildAvailableActions(DbConnection conn, Identity owner, string combatProfile, string weaponFilterKey)
        {
            string normalizedProfile = WireIdentifier.Normalize(combatProfile);
            List<AvailableAction> actions = conn.Db.AbilityCatalog.Iter()
                .Where(ability => !string.IsNullOrWhiteSpace(WireIdentifier.Normalize(ability.AbilityId)))
                .Select(ability =>
                {
                    AbilityCategory category = CategoryForAbility(conn, ability);
                    return new AvailableAction(
                        ActionKinds.Ability,
                        ability.AbilityId,
                        ability.AbilityId,
                        ActionPresentation.ResolveAbilityDisplayName(conn, ability.AbilityId, ability.DisplayName),
                        category.Key,
                        category.Title,
                        category.SortOrder,
                        ability.SortOrder,
                        isFixed: false,
                        isUsable: AbilityIsUsableForPanel(conn, owner, ability, normalizedProfile),
                        disabledReason: DisabledReasonForAbility(conn, owner, ability, normalizedProfile));
                })
                .ToList();

            foreach (AvailableAction fixedAction in BuildFixedActions(conn))
                actions.Add(fixedAction);

            return actions
                .Where(action => MatchesWeaponFilter(action, weaponFilterKey))
                .OrderBy(action => action.CategorySortOrder)
                .ThenBy(action => action.CategoryTitle, StringComparer.Ordinal)
                .ThenBy(action => action.SortOrder)
                .ThenBy(action => action.IsFixed ? 1 : 0)
                .ThenBy(action => action.DisplayName, StringComparer.Ordinal)
                .ToList();
        }

        private static bool AbilityIsUsableForPanel(
            DbConnection conn,
            Identity owner,
            AbilityCatalog ability,
            string normalizedProfile)
        {
            if (!AbilityTagCodec.HasTag(ability.AbilityTags, ActionBarActionTag))
                return false;

            string abilityProfile = CombatProfileResolver.ResolveForAbility(conn, ability);
            if (!string.IsNullOrWhiteSpace(abilityProfile))
                return string.Equals(abilityProfile, normalizedProfile, StringComparison.OrdinalIgnoreCase);

            return string.Equals(WireIdentifier.Normalize(ability.AbilityKind), AbilityKinds.Spell, StringComparison.Ordinal)
                && (DisciplineAbilitySelectionResolver.IsSelected(conn, owner, ability.AbilityId)
                    || SpellbookResolver.AbilityIsKnownIfSpell(conn, owner, ability));
        }

        private static string DisabledReasonForAbility(
            DbConnection conn,
            Identity owner,
            AbilityCatalog ability,
            string normalizedProfile)
        {
            if (AbilityIsUsableForPanel(conn, owner, ability, normalizedProfile))
                return string.Empty;

            if (!AbilityTagCodec.HasTag(ability.AbilityTags, ActionBarActionTag))
                return "Not assignable";

            string abilityProfile = CombatProfileResolver.ResolveForAbility(conn, ability);
            if (!string.IsNullOrWhiteSpace(abilityProfile)
                && !string.Equals(abilityProfile, normalizedProfile, StringComparison.OrdinalIgnoreCase))
            {
                return $"Requires {abilityProfile}";
            }

            if (IsSpellAbility(ability))
                return "Not selected, learned, or equipped";

            return "Unavailable";
        }

        private static IEnumerable<AvailableAction> BuildFixedActions(DbConnection conn)
        {
            Dictionary<string, AvailableAction> fixedActions = new(StringComparer.Ordinal);

            foreach (ActionPresentationCatalog presentation in conn.Db.ActionPresentationCatalog.Iter())
            {
                if (!string.Equals(WireIdentifier.Normalize(presentation.PresentationKind), ActionTooltipResolver.PresentationKindFixed, StringComparison.Ordinal))
                    continue;

                string fixedActionId = WireIdentifier.Normalize(presentation.PresentationId);
                if (string.IsNullOrWhiteSpace(fixedActionId))
                    continue;
                if (!Arena.Input.FixedActionDispatcher.IsActionBarVisible(fixedActionId, conn))
                    continue;

                fixedActions[fixedActionId] = new AvailableAction(
                    ActionKinds.Fixed,
                    fixedActionId,
                    string.Empty,
                    ActionPresentation.ResolveFixedDisplayName(conn, fixedActionId),
                    "UTILITY",
                    "Utility",
                    uint.MaxValue,
                    presentation.SortOrder,
                    isFixed: true,
                    isUsable: true);
            }

            return fixedActions.Values;
        }

        private static string BuildSignature(
            DbConnection conn,
            Identity owner,
            string combatProfile,
            IReadOnlyList<AvailableAction> actions,
            AvailableAction selectedAction,
            int assignedSpellSlots,
            int spellSlotCapacity,
            string weaponFilterKey)
        {
            StringBuilder sb = new();
            sb.Append(combatProfile).Append('|');
            sb.Append("weapon_filter:").Append(weaponFilterKey).Append('|');
            sb.Append("spell_slots:").Append(assignedSpellSlots).Append('/').Append(spellSlotCapacity).Append('|');
            sb.Append("capacitor_charges:").Append(CapacitorPresentation.ChargeCount(conn, owner)).Append('|');
            sb.Append("soul_stolen:").Append(SoulstealerPresentation.HasStolenSoul(conn, owner)).Append('|');
            sb.Append("discipline_abilities:");
            foreach (CharacterDisciplineAbilitySelection selection in
                     conn.Db.CharacterDisciplineAbilitySelection.Owner.Filter(owner)
                         .OrderBy(row => row.SortOrder)
                         .ThenBy(row => row.AbilityId, StringComparer.Ordinal))
            {
                sb.Append(selection.SortOrder).Append(':')
                    .Append(selection.DisciplineId).Append(':')
                    .Append(selection.AbilityId).Append(';');
            }
            sb.Append("|spellbook:");
            foreach (ItemSpell spell in ReadEquippedSpellbookSpells(conn, owner))
                sb.Append(spell.SlotIndex).Append(':')
                    .Append(WireIdentifier.Normalize(spell.SpellId)).Append(';');
            sb.Append('|');
            foreach (AvailableAction action in actions)
                sb.Append(action.ActionKind).Append(':')
                    .Append(action.ActionId).Append(':')
                    .Append(action.AbilityId).Append(':')
                    .Append(action.CategoryKey).Append(':')
                    .Append(action.CategoryTitle).Append(':')
                    .Append(action.CategorySortOrder).Append(':')
                    .Append(action.DisplayName).Append(':')
                    .Append(action.SortOrder).Append(':')
                    .Append(action.IsUsable).Append(':')
                    .Append(action.DisabledReason).Append(';');
            sb.Append('|')
                .Append(selectedAction.ActionKind).Append(':')
                .Append(selectedAction.ActionId);
            return sb.ToString();
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
            if (ctx.Event.Reducer is not Reducer.AssignCharacterActionBarAbilityToSlot
                && ctx.Event.Reducer is not Reducer.AssignCharacterActionBarSlot
                && ctx.Event.Reducer is not Reducer.AssignEquippedSpellbookSpell
                && ctx.Event.Reducer is not Reducer.ClearCharacterActionBarSlot)
            {
                return;
            }

            SetStatus(error.Message, true);
        }

        private void SetStatus(string message, bool error)
        {
            if (_status == null)
                return;

            _lastError = error ? message : string.Empty;
            _status.color = error ? ArenaUiTheme.Danger : ArenaUiTheme.Success;
            _status.text = message;
            _errorUntilTime = Time.unscaledTime + 4f;
        }

        private bool CanApplyPayloadToSlot(DbConnection conn, ActionBarDragPayload payload, string? targetSlotId)
        {
            if (!payload.HasValue || !PayloadRequiresSpellSlot(conn, payload))
                return true;

            if (string.IsNullOrWhiteSpace(WireIdentifier.Normalize(targetSlotId)))
                return true;

            Identity? owner = conn.Identity;
            if (!owner.HasValue)
                return false;

            string excludedSlotId = payload.HasSourceSlot
                ? payload.SourceSlotId
                : WireIdentifier.Normalize(targetSlotId);
            int capacity = SpellSlotResolver.Capacity(conn, owner.Value);
            int used = SpellSlotResolver.AssignedSpellCount(conn, owner.Value, excludedSlotId);
            if (used < capacity)
                return true;

            SetStatus($"No spell slot available ({used}/{capacity})", true);
            return false;
        }

        private static bool PayloadRequiresSpellSlot(DbConnection conn, ActionBarDragPayload payload)
        {
            if (!string.Equals(payload.ActionKind, ActionKinds.Ability, StringComparison.Ordinal))
                return false;

            AbilityCatalog? ability = conn.Db.AbilityCatalog.AbilityId.Find(WireIdentifier.Normalize(payload.ActionId));
            if (!string.Equals(WireIdentifier.Normalize(ability?.AbilityKind), AbilityKinds.Spell, StringComparison.Ordinal))
                return false;

            return !CombatProfileResolver.AbilityMatchesOwner(conn, conn.Identity, ability);
        }

        private static bool AvailableActionCanAssignToSpellbook(AvailableAction action)
            => string.Equals(action.ActionKind, ActionKinds.Ability, StringComparison.Ordinal)
                && string.Equals(action.CategoryKey, SpellsFilterKey, StringComparison.Ordinal);

        private static string SpellbookDropSlotId(int col) => $"{SpellbookDropSlotPrefix}{col}";

        private static bool TryParseSpellbookDropSlot(string? slotId, out uint slotIndex)
        {
            slotIndex = 0;
            string normalized = WireIdentifier.Normalize(slotId);
            if (!normalized.StartsWith(SpellbookDropSlotPrefix, StringComparison.Ordinal))
                return false;

            return uint.TryParse(normalized[SpellbookDropSlotPrefix.Length..], out slotIndex);
        }

        private static bool TryResolveSpellIdForPayload(
            DbConnection conn,
            ActionBarDragPayload payload,
            out string spellId)
        {
            spellId = string.Empty;
            if (!payload.HasValue || !string.Equals(payload.ActionKind, ActionKinds.Ability, StringComparison.Ordinal))
                return false;

            AbilityCatalog? ability = conn.Db.AbilityCatalog.AbilityId.Find(WireIdentifier.Normalize(payload.ActionId))
                ?? conn.Db.AbilityCatalog.AbilityId.Find(WireIdentifier.Normalize(payload.AbilityId));
            if (!string.Equals(WireIdentifier.Normalize(ability?.AbilityKind), AbilityKinds.Spell, StringComparison.Ordinal))
                return false;

            spellId = WireIdentifier.Normalize(ability?.ActionId);
            return !string.IsNullOrWhiteSpace(spellId);
        }

        private static bool SameAction(AvailableAction left, AvailableAction right)
            => left.HasValue
                && right.HasValue
                && string.Equals(left.ActionKind, right.ActionKind, StringComparison.Ordinal)
                && string.Equals(left.ActionId, right.ActionId, StringComparison.Ordinal);

        private static TooltipData DisabledTooltip(TooltipData baseTooltip, AvailableAction action)
        {
            string name = baseTooltip.IsValid ? baseTooltip.Name : action.DisplayName;
            string subtitle = string.IsNullOrWhiteSpace(action.DisabledReason)
                ? baseTooltip.Subtitle
                : action.DisabledReason;
            return new TooltipData(name, subtitle, baseTooltip.Description);
        }

        private static TooltipData SpellbookAssignableTooltip(TooltipData baseTooltip, AvailableAction action)
        {
            string name = baseTooltip.IsValid ? baseTooltip.Name : action.DisplayName;
            string description = baseTooltip.Description;
            if (string.IsNullOrWhiteSpace(description))
                description = action.DisabledReason;
            return new TooltipData(name, "Drop into spellbook", description);
        }

        private static bool MatchesWeaponFilter(AvailableAction action, string weaponFilterKey)
        {
            string normalizedFilter = WireIdentifier.Normalize(weaponFilterKey);
            return string.IsNullOrWhiteSpace(normalizedFilter)
                || string.Equals(normalizedFilter, AllWeaponsFilterKey, StringComparison.Ordinal)
                || string.Equals(action.CategoryKey, SpellsFilterKey, StringComparison.Ordinal)
                || string.Equals(action.CategoryKey, normalizedFilter, StringComparison.Ordinal);
        }

        private static bool IsSpellAbility(AbilityCatalog ability)
            => string.Equals(WireIdentifier.Normalize(ability.AbilityKind), AbilityKinds.Spell, StringComparison.Ordinal);

        private readonly struct WeaponFilterOption
        {
            public readonly string Key;
            public readonly string Title;
            public readonly uint SortOrder;

            public WeaponFilterOption(string key, string title, uint sortOrder)
            {
                Key = WireIdentifier.Normalize(key);
                Title = string.IsNullOrWhiteSpace(title) ? "All Actions" : title;
                SortOrder = sortOrder;
            }
        }

        private readonly struct AbilityCategory
        {
            public readonly string Key;
            public readonly string Title;
            public readonly uint SortOrder;

            public AbilityCategory(string key, string title, uint sortOrder)
            {
                Key = key;
                Title = title;
                SortOrder = sortOrder;
            }
        }

        private static AbilityCategory CategoryForAbility(DbConnection conn, AbilityCatalog ability)
        {
            string profileId = CombatProfileResolver.ResolveForAbility(conn, ability);
            if (string.IsNullOrWhiteSpace(profileId) && IsSpellAbility(ability))
                return new AbilityCategory(SpellsFilterKey, "Spells", SpellsCategorySortOrder);

            if (string.IsNullOrWhiteSpace(profileId))
                return new AbilityCategory("GENERAL", "General", uint.MaxValue - 1);

            return CategoryForProfile(conn, profileId);
        }

        private static AbilityCategory CategoryForProfile(DbConnection conn, string profileId)
        {
            CombatProfileCatalog? profile = conn.Db.CombatProfileCatalog.CombatProfileId.Find(profileId);
            CombatDisciplineCatalog? discipline = conn.Db.CombatDisciplineCatalog.CombatProfileId
                .Filter(profileId)
                .OrderBy(row => row.SortOrder)
                .ThenBy(row => row.DisplayName, StringComparer.Ordinal)
                .FirstOrDefault();

            string profileName = string.IsNullOrWhiteSpace(profile?.DisplayName)
                ? HumanizeIdentifier(profileId)
                : profile.DisplayName;
            string title = string.IsNullOrWhiteSpace(discipline?.DisplayName)
                ? profileName
                : $"{discipline.DisplayName} / {profileName}";
            uint sortOrder = discipline?.SortOrder ?? profile?.SortOrder ?? uint.MaxValue - 2;
            return new AbilityCategory(profileId, title, sortOrder);
        }

        private void UpdateWeaponFilterOptions(DbConnection conn)
        {
            List<WeaponFilterOption> options = BuildWeaponFilterOptions(conn);
            bool changed = options.Count != _weaponFilterOptions.Count;
            if (!changed)
            {
                for (int i = 0; i < options.Count; i++)
                {
                    if (!string.Equals(options[i].Key, _weaponFilterOptions[i].Key, StringComparison.Ordinal)
                        || !string.Equals(options[i].Title, _weaponFilterOptions[i].Title, StringComparison.Ordinal)
                        || options[i].SortOrder != _weaponFilterOptions[i].SortOrder)
                    {
                        changed = true;
                        break;
                    }
                }
            }

            if (changed)
            {
                _weaponFilterOptions.Clear();
                _weaponFilterOptions.AddRange(options);
            }

            if (_weaponFilterOptions.All(option => !string.Equals(option.Key, _weaponFilterKey, StringComparison.Ordinal)))
                _weaponFilterKey = AllWeaponsFilterKey;

            UpdateWeaponFilterLabel();
            if (_weaponFilterMenuOpen)
                RebuildWeaponFilterMenu();
        }

        private static List<WeaponFilterOption> BuildWeaponFilterOptions(DbConnection conn)
        {
            List<WeaponFilterOption> options = new()
            {
                new WeaponFilterOption(AllWeaponsFilterKey, "All Actions", 0),
            };

            bool hasSpells = conn.Db.AbilityCatalog.Iter().Any(ability =>
                string.IsNullOrWhiteSpace(CombatProfileResolver.ResolveForAbility(conn, ability))
                && IsSpellAbility(ability)
                && AbilityTagCodec.HasTag(ability.AbilityTags, ActionBarActionTag));
            if (hasSpells)
                options.Add(new WeaponFilterOption(SpellsFilterKey, "Spells", SpellsCategorySortOrder));

            foreach (CombatProfileCatalog profile in conn.Db.CombatProfileCatalog.Iter()
                         .OrderBy(row => row.SortOrder)
                         .ThenBy(row => row.DisplayName, StringComparer.Ordinal))
            {
                AbilityCategory category = CategoryForProfile(conn, WireIdentifier.Normalize(profile.CombatProfileId));
                options.Add(new WeaponFilterOption(category.Key, category.Title, category.SortOrder));
            }

            return options
                .GroupBy(option => option.Key, StringComparer.Ordinal)
                .Select(group => group.OrderBy(option => option.SortOrder).First())
                .OrderBy(option => option.SortOrder)
                .ThenBy(option => option.Title, StringComparer.Ordinal)
                .ToList();
        }

        private void ToggleWeaponFilterMenu()
        {
            if (_weaponFilterMenu == null)
                return;

            _weaponFilterMenuOpen = !_weaponFilterMenuOpen;
            if (_weaponFilterMenuOpen)
            {
                RebuildWeaponFilterMenu();
                _weaponFilterMenu.SetAsLastSibling();
            }

            _weaponFilterMenu.gameObject.SetActive(_weaponFilterMenuOpen);
        }

        private void SelectWeaponFilter(string key)
        {
            _weaponFilterKey = WireIdentifier.Normalize(key);
            _weaponFilterMenuOpen = false;
            if (_weaponFilterMenu != null)
                _weaponFilterMenu.gameObject.SetActive(false);
            if (_availableContent != null)
                _availableContent.anchoredPosition = Vector2.zero;
            UpdateWeaponFilterLabel();
            _lastSignature = string.Empty;
            _nextRefreshTime = 0f;
            Refresh();
        }

        private void UpdateWeaponFilterLabel()
        {
            if (_weaponFilterLabel == null)
                return;

            WeaponFilterOption selected = _weaponFilterOptions.FirstOrDefault(
                option => string.Equals(option.Key, _weaponFilterKey, StringComparison.Ordinal));
            _weaponFilterLabel.text = string.IsNullOrWhiteSpace(selected.Title)
                ? "All Actions"
                : selected.Title;
        }

        private void RebuildWeaponFilterMenu()
        {
            Clear(_weaponFilterMenuCells);
            if (_weaponFilterMenu == null)
                return;

            const float optionHeight = ArenaUiTheme.ButtonHeight;
            float width = _weaponFilterMenu.rect.width > 0f
                ? _weaponFilterMenu.rect.width
                : _weaponFilterMenu.sizeDelta.x;
            _weaponFilterMenu.sizeDelta = new Vector2(width, optionHeight * _weaponFilterOptions.Count);

            for (int i = 0; i < _weaponFilterOptions.Count; i++)
            {
                WeaponFilterOption option = _weaponFilterOptions[i];
                bool selected = string.Equals(option.Key, _weaponFilterKey, StringComparison.Ordinal);
                string selectedKey = option.Key;
                ArenaButtonHandle button = ArenaUiKit.MakeButton(
                    _weaponFilterMenu,
                    $"WeaponFilterOption_{option.Key}",
                    option.Title,
                    ArenaButtonStyle.Secondary,
                    () => SelectWeaponFilter(selectedKey));
                button.Background.color = selected
                    ? Color.Lerp(ArenaUiTheme.RowAlt, ArenaUiTheme.Accent, 0.2f)
                    : ArenaUiTheme.Row;
                button.Label.color = selected ? ArenaUiTheme.Gold : ArenaUiTheme.Text;
                SetRect(
                    button.Rect,
                    new Vector2(0f, -(i * optionHeight)),
                    new Vector2(width, optionHeight),
                    new Vector2(0f, 1f),
                    new Vector2(0f, 1f));
                _weaponFilterMenuCells.Add(button.GameObject);
            }
        }

        private GameObject CreateAvailableSectionHeader(Transform parent, string title)
        {
            RectTransform header = ArenaUiKit.MakeRect(parent, "AvailableSectionHeader");

            TextMeshProUGUI label = ArenaUiKit.MakeSectionLabel(header, "Text", title);
            ArenaUiKit.SetAnchors(
                label.rectTransform,
                Vector2.zero,
                Vector2.one,
                new Vector2(2f, 5f),
                Vector2.zero);

            RectTransform divider = ArenaUiKit.MakeDivider(header);
            ArenaUiKit.SetAnchors(
                divider,
                Vector2.zero,
                new Vector2(1f, 0f),
                new Vector2(0f, 2f),
                new Vector2(0f, 3f));

            return header.gameObject;
        }

        private static string HumanizeIdentifier(string value)
        {
            string normalized = WireIdentifier.Normalize(value);
            if (string.IsNullOrWhiteSpace(normalized))
                return "General";

            return string.Join(
                " ",
                normalized
                    .Split('_')
                    .Where(part => !string.IsNullOrWhiteSpace(part))
                    .Select(part => part.Length == 1
                        ? part
                        : string.Concat(part[..1], part[1..].ToLowerInvariant())));
        }

        private static void TintIcon(GameObject cell, Color color)
        {
            Transform iconTransform = cell.transform.Find("Icon");
            Image? icon = iconTransform == null ? null : iconTransform.GetComponent<Image>();
            if (icon != null)
                icon.color = color;
        }

        private static Color AbilityColor(string abilityId)
        {
            int hash = WireIdentifier.Normalize(abilityId).GetHashCode();
            float hue = Mathf.Abs(hash % 360) / 360f;
            return Color.HSVToRGB(hue, 0.44f, 0.50f);
        }

        private static Color FixedActionColor(string actionId)
        {
            return WireIdentifier.Normalize(actionId) switch
            {
                FixedActionIds.Dodge => new Color(0.20f, 0.32f, 0.48f, 0.96f),
                FixedActionIds.Parry => new Color(0.44f, 0.36f, 0.16f, 0.96f),
                _ => new Color(0.24f, 0.26f, 0.30f, 0.96f),
            };
        }

        private static void Clear(List<GameObject> objects)
        {
            foreach (GameObject obj in objects)
                Destroy(obj);
            objects.Clear();
        }

        private static void SetRect(RectTransform rt, Vector2 anchoredPosition, Vector2 size, Vector2 anchorMin, Vector2 anchorMax)
        {
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.pivot = anchorMin;
            rt.anchoredPosition = anchoredPosition;
            rt.sizeDelta = size;
        }

        private static bool WasTogglePressed()
        {
#if ENABLE_INPUT_SYSTEM
            if (Keyboard.current != null)
                return Keyboard.current.jKey.wasPressedThisFrame;
#endif
            return UnityEngine.Input.GetKeyDown(KeyCode.J);
        }

    }
}
