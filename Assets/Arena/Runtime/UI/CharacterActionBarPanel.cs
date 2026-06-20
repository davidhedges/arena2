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
        private const string ActionBarActionTag = "ACTION_BAR_ACTION";

        private static readonly Color PanelColor = new(0.055f, 0.06f, 0.068f, 0.96f);
        private static readonly Color HeaderColor = new(0.09f, 0.105f, 0.12f, 0.98f);
        private static readonly Color Gold = new(0.96f, 0.73f, 0.26f, 1f);
        private static readonly Color EmptySlotColor = new(0.04f, 0.05f, 0.06f, 0.92f);

        private Canvas? _canvas;
        private GameObject? _root;
        private RectTransform? _availableRoot;
        private RectTransform? _barRoot;
        private TextMeshProUGUI? _title;
        private TextMeshProUGUI? _spellSlots;
        private TextMeshProUGUI? _status;
        private DbConnection? _subscribedConnection;
        private readonly List<GameObject> _availableCells = new();
        private readonly List<GameObject> _barCells = new();
        private AvailableAction _selectedAction;
        private string _lastSignature = string.Empty;
        private string _lastError = string.Empty;
        private float _errorUntilTime;
        private float _nextRefreshTime;
        private bool _isOpen;

        public int EscapeClosePriority => 89;
        public bool IsEscapeCloseable => _isOpen;

        private readonly struct AvailableAction
        {
            public readonly string ActionKind;
            public readonly string ActionId;
            public readonly string AbilityId;
            public readonly string DisplayName;
            public readonly uint SortOrder;
            public readonly bool IsFixed;

            public AvailableAction(
                string actionKind,
                string actionId,
                string abilityId,
                string displayName,
                uint sortOrder,
                bool isFixed)
            {
                ActionKind = WireIdentifier.Normalize(actionKind);
                ActionId = WireIdentifier.Normalize(actionId);
                AbilityId = WireIdentifier.Normalize(abilityId);
                DisplayName = displayName;
                SortOrder = sortOrder;
                IsFixed = isFixed;
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

            GameObject go = new("CharacterActionBarPanel");
            DontDestroyOnLoad(go);
            go.AddComponent<CharacterActionBarPanel>();
        }

        private void Awake()
        {
            RuntimeUiEventSystem.Ensure();
            BuildUi();
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

        private void SetOpen(bool open)
        {
            _isOpen = open;
            if (_root != null)
                _root.SetActive(open);
            if (open)
            {
                _lastSignature = string.Empty;
                _nextRefreshTime = 0f;
                Refresh();
            }
        }

        private void BuildUi()
        {
            _canvas = gameObject.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 34;

            CanvasScaler scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            gameObject.AddComponent<GraphicRaycaster>();

            _root = new GameObject("CharacterActionBarRoot", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            _root.transform.SetParent(transform, false);
            RectTransform rootRt = (RectTransform)_root.transform;
            rootRt.anchorMin = new Vector2(0.5f, 0.5f);
            rootRt.anchorMax = new Vector2(0.5f, 0.5f);
            rootRt.pivot = new Vector2(0.5f, 0.5f);
            rootRt.sizeDelta = new Vector2(1240f, 720f);
            rootRt.anchoredPosition = Vector2.zero;
            _root.GetComponent<Image>().color = PanelColor;

            RectTransform header = AddBlock("Header", _root.transform, HeaderColor);
            header.anchorMin = new Vector2(0f, 1f);
            header.anchorMax = new Vector2(1f, 1f);
            header.pivot = new Vector2(0.5f, 1f);
            header.offsetMin = new Vector2(0f, -64f);
            header.offsetMax = Vector2.zero;

            _title = MakeLabel("Title", header, "Action Bar", 24f, TextAlignmentOptions.MidlineLeft, Color.white);
            SetRect(_title.rectTransform, new Vector2(22f, -52f), new Vector2(460f, 42f), new Vector2(0f, 1f), new Vector2(0f, 1f));

            _spellSlots = MakeLabel("SpellSlots", header, string.Empty, 15f, TextAlignmentOptions.MidlineLeft, new Color(0.74f, 0.82f, 0.88f));
            SetRect(_spellSlots.rectTransform, new Vector2(492f, -48f), new Vector2(260f, 34f), new Vector2(0f, 1f), new Vector2(0f, 1f));

            TextMeshProUGUI hint = MakeLabel("Hint", header, "J", 15f, TextAlignmentOptions.MidlineRight, new Color(0.72f, 0.75f, 0.80f));
            SetRect(hint.rectTransform, new Vector2(-180f, -48f), new Vector2(120f, 34f), new Vector2(1f, 1f), new Vector2(1f, 1f));

            Button close = MakeButton("CloseButton", header, "Close", new Color(0.16f, 0.17f, 0.19f, 0.96f), Color.white);
            SetRect((RectTransform)close.transform, new Vector2(-94f, -49f), new Vector2(72f, 34f), new Vector2(1f, 1f), new Vector2(1f, 1f));
            close.onClick.AddListener(() => SetOpen(false));

            _availableRoot = new GameObject("AvailableActions", typeof(RectTransform)).GetComponent<RectTransform>();
            _availableRoot.SetParent(_root.transform, false);
            _availableRoot.anchorMin = new Vector2(0f, 0f);
            _availableRoot.anchorMax = new Vector2(0f, 1f);
            _availableRoot.pivot = new Vector2(0f, 1f);
            _availableRoot.anchoredPosition = new Vector2(28f, -92f);
            _availableRoot.sizeDelta = new Vector2(536f, 570f);

            _barRoot = new GameObject("ActionBarGrid", typeof(RectTransform)).GetComponent<RectTransform>();
            _barRoot.SetParent(_root.transform, false);
            _barRoot.anchorMin = new Vector2(1f, 0f);
            _barRoot.anchorMax = new Vector2(1f, 0f);
            _barRoot.pivot = new Vector2(1f, 0f);
            _barRoot.anchoredPosition = new Vector2(-28f, 86f);
            _barRoot.sizeDelta = ActionBarLayout.GridSize;

            _status = MakeLabel("Status", _root.transform, string.Empty, 13f, TextAlignmentOptions.MidlineLeft, new Color(1f, 0.52f, 0.45f));
            SetRect(_status.rectTransform, new Vector2(28f, 22f), new Vector2(720f, 28f), new Vector2(0f, 0f), new Vector2(0f, 0f));
        }

        private void Refresh()
        {
            if (_canvas == null || _availableRoot == null || _barRoot == null)
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
            List<AvailableAction> actions = BuildAvailableActions(conn, owner.Value, combatProfile);
            string signature = BuildSignature(conn, owner.Value, combatProfile, actions, _selectedAction, assignedSpellSlots, spellSlotCapacity);
            if (signature == _lastSignature)
                return;

            _lastSignature = signature;
            Rebuild(actions, conn, owner);
            if (_title != null)
                _title.text = string.IsNullOrWhiteSpace(combatProfile)
                    ? "Action Bar"
                    : $"Action Bar | {combatProfile}";
            if (_spellSlots != null)
                _spellSlots.text = $"Spell slots {assignedSpellSlots}/{spellSlotCapacity}";
        }

        private void Rebuild(IReadOnlyList<AvailableAction> actions, DbConnection? conn, Identity? owner)
        {
            Clear(_availableCells);
            Clear(_barCells);

            if (_canvas == null || _availableRoot == null || _barRoot == null)
                return;

            for (int i = 0; i < actions.Count; i++)
            {
                AvailableAction action = actions[i];
                int row = i / AvailableColumns;
                int col = i % AvailableColumns;
                Vector2 position = new(
                    col * (ActionBarLayout.SlotSize + AvailableGap),
                    -(row * (ActionBarLayout.SlotSize + AvailableGap)));
                Sprite? iconSprite = ActionIconResolver.ResolveForAvailableAction(action.ActionKind, action.ActionId, action.AbilityId);
                TooltipData tooltip = action.IsFixed
                    ? ActionTooltipResolver.ResolveForFixedAction(conn, action.ActionId)
                    : ActionTooltipResolver.ResolveForAbility(conn, owner, conn?.Db.AbilityCatalog.AbilityId.Find(action.AbilityId));
                GameObject cell = ActionBarSlotViewFactory.Create(
                    _availableRoot,
                    $"Available_{action.ActionKind}_{action.ActionId}",
                    iconSprite == null ? action.DisplayName : string.Empty,
                    string.Empty,
                    action.IsFixed ? FixedActionColor(action.ActionId) : AbilityColor(action.AbilityId),
                    Color.white,
                    iconSprite,
                    _canvas,
                    payloadProvider: () => action.ToDragPayload(),
                    onDrop: HandleActionDrop,
                    onClick: () =>
                    {
                        _selectedAction = action;
                        _lastSignature = string.Empty;
                    },
                    tooltipData: tooltip);
                SetRect((RectTransform)cell.transform, position, ActionBarLayout.SlotVector, new Vector2(0f, 1f), new Vector2(0f, 1f));

                Outline outline = cell.AddComponent<Outline>();
                outline.effectColor = SameAction(_selectedAction, action) ? Gold : new Color(1f, 1f, 1f, 0.08f);
                outline.effectDistance = SameAction(_selectedAction, action) ? new Vector2(2f, -2f) : new Vector2(1f, -1f);
                _availableCells.Add(cell);
            }

            for (int row = 0; row < ActionBarLayout.Rows; row++)
            {
                for (int col = 0; col < ActionBarLayout.Columns; col++)
                {
                    Vector2 position = ActionBarLayout.CellPosition(row, col);
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
                    Sprite? iconSprite = hasAction ? ActionIconResolver.ResolveForAction(resolved) : null;
                    TooltipData tooltip = hasAction
                        ? ActionTooltipResolver.ResolveForActionRef(conn, owner, resolved)
                        : default;
                    GameObject cell = ActionBarSlotViewFactory.Create(
                        _barRoot,
                        $"ActionBar_{row}_{col}",
                        iconSprite == null && hasAction ? displayName : string.Empty,
                        keyLabel,
                        hasAction ? (resolved.IsFixed ? FixedActionColor(actionId) : AbilityColor(abilityId)) : EmptySlotColor,
                        hasAction ? Color.white : new Color(1f, 1f, 1f, 0.36f),
                        iconSprite,
                        _canvas,
                        slotId,
                        () => ActionBarDragPayload.From(resolved, slotId),
                        HandleActionDrop,
                        () => AssignSelectedActionToSlot(slotId),
                        tooltipData: tooltip);
                    SetRect((RectTransform)cell.transform, position, ActionBarLayout.SlotVector, new Vector2(0f, 0f), new Vector2(0f, 0f));
                    _barCells.Add(cell);
                }
            }
        }

        private void AssignSelectedActionToSlot(string slotId)
        {
            if (!_selectedAction.HasValue || string.IsNullOrWhiteSpace(slotId))
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

        private void HandleActionDrop(ActionBarDragPayload payload, string? targetSlotId)
        {
            DbConnection? conn = NetworkManager.Instance?.Conn;
            if (conn == null)
                return;

            if (!CanApplyPayloadToSlot(conn, payload, targetSlotId))
                return;

            ActionBarDropApplier.ApplyDrop(conn, payload, targetSlotId);
            _lastSignature = string.Empty;
            _nextRefreshTime = 0f;
        }

        private List<AvailableAction> BuildAvailableActions(DbConnection conn, Identity owner, string combatProfile)
        {
            string normalizedProfile = WireIdentifier.Normalize(combatProfile);
            List<AvailableAction> actions = conn.Db.AbilityCatalog.Iter()
                .Where(ability => string.Equals(CombatProfileResolver.ResolveForAbility(conn, ability), normalizedProfile, StringComparison.OrdinalIgnoreCase))
                .Where(ability => HasAbilityTag(ability, ActionBarActionTag))
                .Where(ability => SpellbookResolver.AbilityIsKnownIfSpell(conn, owner, ability))
                .Select(ability => new AvailableAction(
                    ActionKinds.Ability,
                    ability.AbilityId,
                    ability.AbilityId,
                    ActionPresentation.ResolveAbilityDisplayName(conn, ability.AbilityId, ability.DisplayName),
                    ability.SortOrder,
                    isFixed: false))
                .ToList();

            foreach (AvailableAction fixedAction in BuildFixedActions(conn))
                actions.Add(fixedAction);

            return actions
                .OrderBy(action => action.SortOrder)
                .ThenBy(action => action.IsFixed ? 1 : 0)
                .ThenBy(action => action.DisplayName, StringComparer.Ordinal)
                .ToList();
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

                fixedActions[fixedActionId] = new AvailableAction(
                    ActionKinds.Fixed,
                    fixedActionId,
                    string.Empty,
                    ActionPresentation.ResolveFixedDisplayName(conn, fixedActionId),
                    presentation.SortOrder,
                    isFixed: true);
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
            int spellSlotCapacity)
        {
            StringBuilder sb = new();
            sb.Append(combatProfile).Append('|');
            sb.Append("spell_slots:").Append(assignedSpellSlots).Append('/').Append(spellSlotCapacity).Append('|');
            foreach (CharacterActionBarAssignment assignment in conn.Db.CharacterActionBarAssignment.Owner.Filter(owner)
                         .Where(row => ActionBarAssignmentScope.MatchesCombatProfile(row, combatProfile))
                         .OrderBy(row => row.SlotId, StringComparer.Ordinal))
                sb.Append(assignment.CombatProfileId).Append(':')
                    .Append(assignment.SlotId).Append(':')
                    .Append(assignment.ActionKind).Append(':')
                    .Append(assignment.ActionId).Append(':')
                    .Append(assignment.AbilityId).Append(';');
            sb.Append('|');
            foreach (AvailableAction action in actions)
                sb.Append(action.ActionKind).Append(':')
                    .Append(action.ActionId).Append(':')
                    .Append(action.AbilityId).Append(':')
                    .Append(action.DisplayName).Append(':')
                    .Append(action.SortOrder).Append(';');
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
            _status.color = error ? new Color(1f, 0.52f, 0.45f) : new Color(0.74f, 0.82f, 0.72f);
            _status.text = message;
            _errorUntilTime = Time.unscaledTime + 4f;
        }

        private bool CanApplyPayloadToSlot(DbConnection conn, ActionBarDragPayload payload, string? targetSlotId)
        {
            if (!payload.HasValue || !PayloadIsSpell(conn, payload))
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

        private static bool PayloadIsSpell(DbConnection conn, ActionBarDragPayload payload)
        {
            if (!string.Equals(payload.ActionKind, ActionKinds.Ability, StringComparison.Ordinal))
                return false;

            AbilityCatalog? ability = conn.Db.AbilityCatalog.AbilityId.Find(WireIdentifier.Normalize(payload.ActionId));
            return string.Equals(WireIdentifier.Normalize(ability?.AbilityKind), AbilityKinds.Spell, StringComparison.Ordinal);
        }

        private static bool HasAbilityTag(AbilityCatalog ability, string tag)
        {
            string normalizedTag = WireIdentifier.Normalize(tag);
            return ability.AbilityTags
                .Split('|')
                .Any(value => string.Equals(WireIdentifier.Normalize(value), normalizedTag, StringComparison.Ordinal));
        }

        private static bool SameAction(AvailableAction left, AvailableAction right)
            => left.HasValue
                && right.HasValue
                && string.Equals(left.ActionKind, right.ActionKind, StringComparison.Ordinal)
                && string.Equals(left.ActionId, right.ActionId, StringComparison.Ordinal);

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
            label.font = TMP_Settings.defaultFontAsset
                ?? Resources.Load<TMP_FontAsset>("Fonts & Materials/LiberationSans SDF");
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
