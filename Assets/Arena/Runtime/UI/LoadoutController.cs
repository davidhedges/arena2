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
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Arena.UI
{
    public sealed class LoadoutController : MonoBehaviour
    {
        private const string SceneName = "Hub";
        private const int StatPointBudget = 100;
        private static readonly string[] StatKinds = { "MIGHT", "INSIGHT", "FINESSE", "QUICKNESS", "FORTITUDE" };
        private static readonly Color Gold = new(0.96f, 0.73f, 0.26f, 1f);
        private static readonly Color PanelFill = new(0.035f, 0.04f, 0.05f, 0.86f);
        private static readonly Color PanelStroke = new(1f, 1f, 1f, 0.10f);
        private static readonly Color TransparentSlotInput = new(1f, 1f, 1f, 0f);
        private const string EffectDamageMultiplierPerPoint = "DAMAGE_MULTIPLIER_PER_POINT";
        private const string EffectCritChancePerPoint = "CRIT_CHANCE_PER_POINT";
        private const string EffectMaxHpPerPoint = "MAX_HP_PER_POINT";
        private const string EffectRunSpeedMultiplierPerPoint = "RUN_SPEED_MULTIPLIER_PER_POINT";
        private const string EffectCastSpeedMultiplierPerPoint = "CAST_SPEED_MULTIPLIER_PER_POINT";
        private const string RuleBaseCritChance = "BASE_CRIT_CHANCE";
        private const string RuleMaxCritChance = "MAX_CRIT_CHANCE";

        public static LoadoutController? Instance { get; private set; }
        public string? SelectedSpecId => _selectedSpecId;

        private GameObject _root = null!;
        private TMP_Text _classTitle = null!;
        private TMP_Text _classMeta = null!;
        private TMP_Text _pointsText = null!;
        private TMP_Text _statusText = null!;
        private TMP_Text _errorText = null!;
        private Button _resetButton = null!;
        private Button _saveButton = null!;
        private RectTransform _classMenuRoot = null!;
        private RectTransform _statsRoot = null!;
        private RectTransform _modifiersRoot = null!;
        private RectTransform _availableActionsRoot = null!;
        private RectTransform _abilityGridRoot = null!;
        private Canvas _canvas = null!;

        private readonly List<GameObject> _classRows = new();
        private readonly List<GameObject> _statRows = new();
        private readonly List<GameObject> _modifierRows = new();
        private readonly List<GameObject> _availableActionCells = new();
        private readonly List<GameObject> _abilityCells = new();

        private DbConnection? _subscribedConnection;
        private string? _selectedSpecId;
        private AvailableLoadoutAction _selectedAvailableAction;
        private string _lastSignature = string.Empty;
        private string _lastClassMenuSignature = string.Empty;
        private string _lastError = string.Empty;
        private float _errorUntilTime;

        private const int AvailableActionColumns = 9;
        private const float AvailableActionSlotWidth = 112f;
        private const float AvailableActionSlotHeight = 58f;
        private const float AvailableActionGap = 8f;
        private const float AvailableActionsRootHeight = 500f;
        private const string LoadoutActionTag = "LOADOUT_ACTION";

        private readonly struct AvailableLoadoutAction
        {
            public readonly string ActionKind;
            public readonly string ActionId;
            public readonly string AbilityId;
            public readonly string ClassId;
            public readonly string DisplayName;
            public readonly uint SortOrder;
            public readonly bool IsFixed;

            public AvailableLoadoutAction(
                string actionKind,
                string actionId,
                string abilityId,
                string classId,
                string displayName,
                uint sortOrder,
                bool isFixed)
            {
                ActionKind = WireIdentifier.Normalize(actionKind);
                ActionId = WireIdentifier.Normalize(actionId);
                AbilityId = WireIdentifier.Normalize(abilityId);
                ClassId = ClassIds.Canonicalize(classId);
                DisplayName = displayName;
                SortOrder = sortOrder;
                IsFixed = isFixed;
            }

            public bool HasValue => !string.IsNullOrWhiteSpace(ActionKind)
                && !string.IsNullOrWhiteSpace(ActionId);

            public LoadoutActionDragPayload ToDragPayload(string sourceSlotId = "")
            {
                return new LoadoutActionDragPayload(
                    ActionKind,
                    ActionId,
                    AbilityId,
                    DisplayName,
                    sourceSlotId);
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (Instance != null)
                return;

            var go = new GameObject("LoadoutController");
            DontDestroyOnLoad(go);
            go.AddComponent<LoadoutController>();
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            BuildUi();
        }

        private void OnEnable()
        {
            TrySubscribeToReducerErrors();
        }

        private void OnDisable()
        {
            UnsubscribeFromReducerErrors();
        }

        private void Update()
        {
            TrySubscribeToReducerErrors();

            bool show = string.Equals(SceneManager.GetActiveScene().name, SceneName, StringComparison.Ordinal)
                && HubViewState.Current == HubViewScreen.Loadout;
            _root.SetActive(show);
            if (!show)
                return;

            DbConnection? conn = NetworkManager.Instance?.Conn;
            if (conn == null || NetworkManager.Instance?.IsConnected != true)
            {
                ShowConnectingState();
                return;
            }

            Refresh(conn);

            if (_errorUntilTime > 0f && Time.unscaledTime > _errorUntilTime)
            {
                _lastError = string.Empty;
                _errorUntilTime = 0f;
                _errorText.text = string.Empty;
            }
        }

        private void BuildUi()
        {
            _root = new GameObject("LoadoutRoot");
            _root.transform.SetParent(transform, false);

            _canvas = _root.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 25;

            CanvasScaler scaler = _root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            _root.AddComponent<GraphicRaycaster>();
            RectTransform rootRt = _root.GetComponent<RectTransform>();
            SetStretch(rootRt);

            RectTransform classMenu = CreatePanel("ClassMenuPanel", _root.transform, new Vector2(28f, 108f), new Vector2(300f, 850f), new Vector2(0f, 1f));
            TMP_Text classMenuTitle = MakeLabel("ClassMenuTitle", classMenu, 18, TextAnchor.MiddleLeft, Color.white);
            classMenuTitle.text = "CLASSES";
            SetRectFromTop(classMenuTitle.rectTransform, new Vector2(24f, 34f), new Vector2(220f, 28f));
            _classMenuRoot = CreateRoot("ClassMenuRoot", classMenu, new Vector2(24f, 82f), new Vector2(252f, 720f));

            RectTransform right = CreatePanel("StatAllocationPanel", _root.transform, new Vector2(-28f, 108f), new Vector2(360f, 850f), new Vector2(1f, 1f));
            _classTitle = MakeLabel("ClassTitle", right, 34, TextAnchor.MiddleLeft, Color.white);
            SetRectFromTop(_classTitle.rectTransform, new Vector2(28f, 42f), new Vector2(304f, 42f));
            _classMeta = MakeLabel("ClassMeta", right, 15, TextAnchor.MiddleLeft, new Color(0.72f, 0.75f, 0.80f));
            SetRectFromTop(_classMeta.rectTransform, new Vector2(28f, 84f), new Vector2(304f, 22f));

            TMP_Text pointsLabel = MakeLabel("PointsLabel", right, 13, TextAnchor.MiddleLeft, new Color(0.72f, 0.75f, 0.80f));
            pointsLabel.text = "ABILITY POINTS";
            SetRectFromTop(pointsLabel.rectTransform, new Vector2(28f, 128f), new Vector2(160f, 22f));
            _pointsText = MakeLabel("PointsValue", right, 22, TextAnchor.MiddleRight, Color.white);
            SetRectFromTop(_pointsText.rectTransform, new Vector2(242f, 124f), new Vector2(80f, 28f));

            _statsRoot = CreateRoot("StatsRoot", right, new Vector2(28f, 170f), new Vector2(330f, 280f));
            TMP_Text modifierTitle = MakeLabel("ModifierTitle", right, 13, TextAnchor.MiddleLeft, new Color(0.72f, 0.75f, 0.80f));
            modifierTitle.text = "STAT MODIFIERS";
            SetRectFromTop(modifierTitle.rectTransform, new Vector2(28f, 468f), new Vector2(180f, 22f));
            _modifiersRoot = CreateRoot("ModifiersRoot", right, new Vector2(28f, 498f), new Vector2(304f, 210f));
            _resetButton = MakeButton("ResetPointsButton", right, "RESET POINTS", new Color(0.12f, 0.13f, 0.15f, 0.96f), Color.white);
            SetRectFromTop((RectTransform)_resetButton.transform, new Vector2(28f, 782f), new Vector2(304f, 42f));
            _resetButton.onClick.AddListener(ResetPoints);

            RectTransform actionLibrary = CreatePanel("ActionLibraryPanel", _root.transform, new Vector2(420f, 372f), new Vector2(1080f, 586f), new Vector2(0f, 0f));
            TMP_Text libraryTitle = MakeLabel("ActionLibraryTitle", actionLibrary, 16, TextAnchor.MiddleLeft, Color.white);
            libraryTitle.text = "ACTION LIBRARY";
            SetRectFromTop(libraryTitle.rectTransform, new Vector2(24f, 28f), new Vector2(220f, 24f));
            _availableActionsRoot = CreateRoot("AvailableActionsRoot", actionLibrary, new Vector2(24f, 62f), new Vector2(1032f, 500f));

            Vector2 actionBarPanelSize = new(1080f, 320f);
            Vector2 actionBarGridSize = ActionBarLayout.GridSize;
            Vector2 actionBarGridOffset = ActionBarLayout.CenteredOffset(actionBarPanelSize);
            actionBarGridOffset.y = 58f;

            RectTransform bottom = CreatePanel("AbilityGridPanel", _root.transform, new Vector2(420f, 28f), actionBarPanelSize, new Vector2(0f, 0f));
            TMP_Text abilityTitle = MakeLabel("AbilityTitle", bottom, 16, TextAnchor.MiddleLeft, Color.white);
            abilityTitle.text = "ACTION BAR";
            SetRectFromBottom(abilityTitle.rectTransform, new Vector2(24f, actionBarPanelSize.y - 40f), new Vector2(160f, 24f));
            _abilityGridRoot = CreateRootFromBottom("AbilityGridRoot", bottom, actionBarGridOffset, actionBarGridSize);
            _statusText = MakeLabel("StatusText", bottom, 12, TextAnchor.MiddleLeft, new Color(0.72f, 0.75f, 0.80f));
            SetRectFromBottom(_statusText.rectTransform, new Vector2(24f, 18f), new Vector2(680f, 22f));
            _errorText = MakeLabel("ErrorText", bottom, 12, TextAnchor.MiddleLeft, new Color(1f, 0.50f, 0.45f));
            SetRectFromBottom(_errorText.rectTransform, new Vector2(24f, -4f), new Vector2(680f, 22f));
            _saveButton = MakeButton("SaveLoadoutButton", bottom, "SAVE LOADOUT", new Color(0.72f, 0.08f, 0.04f, 0.96f), Color.white);
            SetRectFromBottom((RectTransform)_saveButton.transform, new Vector2(902f, 18f), new Vector2(154f, 44f));
            _saveButton.onClick.AddListener(SaveSelectedSpec);

            _root.SetActive(false);
        }

        private void ShowConnectingState()
        {
            _classTitle.text = "LOADOUT";
            _classMeta.text = "CONNECTING";
            _pointsText.text = "-";
            _statusText.text = "Waiting for local connection and progression subscriptions.";
            _errorText.text = string.Empty;
            _resetButton.interactable = false;
            _saveButton.interactable = false;
            ClearRows(_classRows);
            ClearRows(_statRows);
            ClearRows(_modifierRows);
            ClearRows(_availableActionCells);
            ClearRows(_abilityCells);
            _lastSignature = string.Empty;
        }

        private void Refresh(DbConnection conn)
        {
            Identity? localIdentity = conn.Identity;
            if (!localIdentity.HasValue)
            {
                ShowWaitingState("Waiting for local identity.");
                return;
            }

            CharacterProgression? progression = conn.Db.CharacterProgression.Owner.Find(localIdentity.Value);
            if (progression == null)
            {
                ShowWaitingState("Waiting for progression rows.");
                return;
            }

            List<ClassCatalog> orderedClasses = OrderedClasses(conn);
            string currentClassId = ClassIds.Canonicalize(progression.ClassId);
            string activeSpecId = ActiveLoadoutResolver.TryResolveActiveSpec(
                conn,
                localIdentity.Value,
                out string resolvedClassId,
                out string resolvedActiveSpecId)
                && string.Equals(resolvedClassId, currentClassId, StringComparison.Ordinal)
                    ? resolvedActiveSpecId
                    : string.Empty;

            List<SavedSpec> specs = conn.Db.SavedSpec.Owner
                .Filter(localIdentity.Value)
                .Where(spec => string.Equals(ClassIds.Canonicalize(spec.ClassId), currentClassId, StringComparison.Ordinal))
                .OrderBy(spec => spec.CreatedAt.MicrosecondsSinceUnixEpoch)
                .ThenBy(spec => spec.Name, StringComparer.Ordinal)
                .ToList();

            if (specs.Count == 0)
            {
                ShowNoSpecsState(conn, orderedClasses, progression.ClassId);
                return;
            }

            if (_selectedSpecId == null || specs.All(spec => !string.Equals(spec.SpecId, _selectedSpecId, StringComparison.Ordinal)))
                _selectedSpecId = specs.FirstOrDefault(spec => string.Equals(spec.SpecId, activeSpecId, StringComparison.Ordinal))?.SpecId
                    ?? specs[0].SpecId;

            SavedSpec selectedSpec = specs.First(spec => string.Equals(spec.SpecId, _selectedSpecId, StringComparison.Ordinal));
            List<SavedSpecStatAllocation> allocations = conn.Db.SavedSpecStatAllocation.SpecId
                .Filter(selectedSpec.SpecId)
                .OrderBy(allocation => allocation.StatKind, StringComparer.Ordinal)
                .ToList();
            List<SavedSpecSlotAssignment> assignments = conn.Db.SavedSpecSlotAssignment.SpecId
                .Filter(selectedSpec.SpecId)
                .OrderBy(assignment => assignment.SlotId, StringComparer.Ordinal)
                .ToList();
            List<AbilityCatalog> catalogAbilities = conn.Db.AbilityCatalog.Iter()
                .Where(ability => string.Equals(ClassIds.Canonicalize(ability.ClassId), currentClassId, StringComparison.Ordinal))
                .OrderBy(ability => ability.SortOrder)
                .ThenBy(ability => ability.DisplayName, StringComparer.Ordinal)
                .ToList();
            List<LoadoutSlotCatalog> slots = conn.Db.LoadoutSlotCatalog.Iter()
                .OrderBy(slot => slot.UiRow)
                .ThenBy(slot => slot.UiCol)
                .ThenBy(slot => slot.SortOrder)
                .ToList();
            List<ActionPresentationCatalog> actionPresentations = conn.Db.ActionPresentationCatalog.Iter()
                .OrderBy(presentation => presentation.SortOrder)
                .ThenBy(presentation => presentation.Key, StringComparer.Ordinal)
                .ToList();
            List<FixedActionBindingCatalog> fixedActionBindings = conn.Db.FixedActionBindingCatalog.Iter()
                .Where(binding => string.Equals(ClassIds.Canonicalize(binding.ClassId), currentClassId, StringComparison.Ordinal))
                .OrderBy(binding => binding.SortOrder)
                .ThenBy(binding => binding.FixedActionId, StringComparer.Ordinal)
                .ToList();

            if (_selectedAvailableAction.HasValue
                && !_selectedAvailableAction.IsFixed
                && !string.Equals(_selectedAvailableAction.ClassId, currentClassId, StringComparison.Ordinal))
            {
                _selectedAvailableAction = default;
            }

            string classDisplay = ResolveClassDisplayName(conn, progression.ClassId);
            string combatProfile = CombatProfileResolver.ResolveForClass(conn, progression.ClassId);
            string combatProfileDisplay = ResolveCombatProfileDisplayName(conn, combatProfile);
            int totalAllocated = allocations.Sum(allocation => (int)allocation.AllocatedPoints);
            int remainingPoints = Math.Max(0, StatPointBudget - totalAllocated);

            _classTitle.text = classDisplay.ToUpperInvariant();
            _classMeta.text = $"MELEE | {combatProfileDisplay.ToUpperInvariant()}";
            _pointsText.text = remainingPoints.ToString();
            _statusText.text = $"Active spec: {selectedSpec.Name}";
            _errorText.text = _lastError;
            _resetButton.interactable = totalAllocated > 0;
            _saveButton.interactable = true;

            string signature = BuildSignature(progression, activeSpecId, selectedSpec, orderedClasses, allocations, assignments, catalogAbilities, actionPresentations, fixedActionBindings, slots, remainingPoints, _selectedAvailableAction);
            if (signature == _lastSignature)
                return;

            _lastSignature = signature;
            RebuildClassMenu(conn, orderedClasses, progression.ClassId);
            RebuildStatRows(conn, selectedSpec, allocations, remainingPoints);
            RebuildModifierRows(conn, allocations, progression.ClassId);
            RebuildAvailableActionGrid(conn, localIdentity.Value, selectedSpec, currentClassId, catalogAbilities, actionPresentations, fixedActionBindings);
            RebuildAbilityGrid(conn, localIdentity.Value, selectedSpec, assignments);
        }

        private void ShowNoSpecsState(DbConnection conn, List<ClassCatalog> orderedClasses, string classId)
        {
            string classDisplay = ResolveClassDisplayName(conn, classId);
            string combatProfile = CombatProfileResolver.ResolveForClass(conn, classId);
            string combatProfileDisplay = ResolveCombatProfileDisplayName(conn, combatProfile);

            _classTitle.text = classDisplay.ToUpperInvariant();
            _classMeta.text = $"MELEE | {combatProfileDisplay.ToUpperInvariant()}";
            _pointsText.text = "-";
            _statusText.text = "No saved specs are visible for this class yet.";
            _errorText.text = _lastError;
            _resetButton.interactable = false;
            _saveButton.interactable = false;
            RebuildClassMenu(conn, orderedClasses, classId);
            ClearRows(_statRows);
            ClearRows(_modifierRows);
            ClearRows(_availableActionCells);
            ClearRows(_abilityCells);
            _lastSignature = string.Empty;
        }

        private void ShowWaitingState(string message)
        {
            _classTitle.text = "LOADOUT";
            _classMeta.text = "WAITING";
            _pointsText.text = "-";
            _statusText.text = message;
            _resetButton.interactable = false;
            _saveButton.interactable = false;
            ClearRows(_classRows);
            ClearRows(_statRows);
            ClearRows(_modifierRows);
            ClearRows(_availableActionCells);
            ClearRows(_abilityCells);
            _lastSignature = string.Empty;
            _lastClassMenuSignature = string.Empty;
        }

        private void RebuildAvailableActionGrid(
            DbConnection conn,
            Identity owner,
            SavedSpec selectedSpec,
            string classId,
            List<AbilityCatalog> catalogAbilities,
            List<ActionPresentationCatalog> actionPresentations,
            List<FixedActionBindingCatalog> fixedActionBindings)
        {
            ClearRows(_availableActionCells);

            List<AvailableLoadoutAction> actions = BuildAvailableActions(
                conn,
                classId,
                catalogAbilities,
                actionPresentations,
                fixedActionBindings);

            for (int i = 0; i < actions.Count; i++)
            {
                AvailableLoadoutAction action = actions[i];
                bool selected = SameActionRef(_selectedAvailableAction, action);
                Color fill = action.IsFixed
                    ? FixedActionColor(action.ActionId)
                    : AbilityColor(action.AbilityId);
                Color textColor = Color.white;
                Vector2 position = AvailableActionCellPosition(i);

                GameObject cell = CreateAbilityCell(
                    $"AvailableAction_{action.ActionKind}_{action.ActionId}",
                    _availableActionsRoot,
                    action.DisplayName,
                    fill,
                    textColor);
                SetRect((RectTransform)cell.transform, position, new Vector2(AvailableActionSlotWidth, AvailableActionSlotHeight));

                Outline outline = cell.gameObject.AddComponent<Outline>();
                outline.effectColor = selected ? Gold : new Color(1f, 1f, 1f, 0.12f);
                outline.effectDistance = selected ? new Vector2(2f, -2f) : new Vector2(1f, -1f);

                TMP_Text label = cell.GetComponentInChildren<TMP_Text>();
                label.fontSize = 11;
                label.textWrappingMode = TextWrappingModes.Normal;

                TooltipData tooltipData = action.IsFixed
                    ? ActionTooltipResolver.ResolveForFixedAction(conn, action.ActionId)
                    : ActionTooltipResolver.ResolveForAbility(conn, owner, conn.Db.AbilityCatalog.AbilityId.Find(action.AbilityId));
                TooltipTarget tooltip = cell.GetComponent<TooltipTarget>()
                    ?? cell.AddComponent<TooltipTarget>();
                tooltip.Configure(_canvas, tooltipData);

                ConfigureCellDragSource(
                    cell,
                    _canvas,
                    () => action.ToDragPayload(),
                    (payload, targetSlotId) => ApplyLoadoutActionDrop(conn, selectedSpec, payload, targetSlotId),
                    () =>
                    {
                        _selectedAvailableAction = action;
                        _lastSignature = string.Empty;
                    });

                _availableActionCells.Add(cell.gameObject);
            }
        }

        private void RebuildStatRows(DbConnection conn, SavedSpec spec, List<SavedSpecStatAllocation> allocations, int remainingPoints)
        {
            ClearRows(_statRows);

            for (int i = 0; i < StatKinds.Length; i++)
            {
                string statKind = StatKinds[i];
                uint currentValue = StatValue(allocations, statKind);
                Color color = StatColor(statKind);
                GameObject row = CreateRow(_statsRoot, $"StatRow_{statKind}", new Vector2(0f, -i * 50f), new Vector2(330f, 44f), new Color(0f, 0f, 0f, 0f));

                TMP_Text glyph = MakeLabel("Glyph", row.transform, 18, TextAnchor.MiddleCenter, color);
                glyph.text = StatGlyph(statKind);
                SetRect(glyph.rectTransform, new Vector2(0f, 10f), new Vector2(28f, 24f));

                TMP_Text label = MakeLabel("Label", row.transform, 13, TextAnchor.MiddleLeft, Color.white);
                label.text = PrettyStatName(statKind).ToUpperInvariant();
                SetRect(label.rectTransform, new Vector2(38f, 12f), new Vector2(92f, 22f));

                BuildMiniBar(row.transform, new Vector2(122f, 19f), 86f, Mathf.Clamp01(currentValue / 30f), color);

                TMP_Text value = MakeLabel("Value", row.transform, 18, TextAnchor.MiddleRight, Color.white);
                value.text = currentValue.ToString();
                SetRect(value.rectTransform, new Vector2(212f, 9f), new Vector2(42f, 26f));

                Button minus = MakeButton("MinusButton", row.transform, "-", new Color(0.10f, 0.11f, 0.13f, 0.96f), Color.white);
                SetRect((RectTransform)minus.transform, new Vector2(260f, 6f), new Vector2(32f, 32f));
                minus.interactable = currentValue > 0;
                minus.onClick.AddListener(() => conn.Reducers.SetSavedSpecStatAllocation(spec.SpecId, statKind, currentValue - 1));

                Button plus = MakeButton("PlusButton", row.transform, "+", new Color(0.10f, 0.11f, 0.13f, 0.96f), Color.white);
                SetRect((RectTransform)plus.transform, new Vector2(300f, 6f), new Vector2(32f, 32f));
                plus.interactable = remainingPoints > 0;
                plus.onClick.AddListener(() => conn.Reducers.SetSavedSpecStatAllocation(spec.SpecId, statKind, currentValue + 1));

                _statRows.Add(row);
            }
        }

        private void RebuildModifierRows(DbConnection conn, IEnumerable<SavedSpecStatAllocation> allocations, string classId)
        {
            ClearRows(_modifierRows);
            List<(string Label, string Value)> modifiers = BuildModifierList(conn, allocations, classId);

            for (int i = 0; i < modifiers.Count; i++)
            {
                (string labelText, string valueText) = modifiers[i];
                GameObject row = CreateRow(_modifiersRoot, $"ModifierRow_{i}", new Vector2(0f, -i * 22f), new Vector2(304f, 20f), new Color(0f, 0f, 0f, 0f));
                TMP_Text label = MakeLabel("Label", row.transform, 12, TextAnchor.MiddleLeft, new Color(0.84f, 0.86f, 0.90f));
                label.text = labelText;
                SetRect(label.rectTransform, new Vector2(0f, 0f), new Vector2(208f, 20f));
                TMP_Text value = MakeLabel("Value", row.transform, 12, TextAnchor.MiddleRight, Gold);
                value.text = valueText;
                SetRect(value.rectTransform, new Vector2(218f, 0f), new Vector2(84f, 20f));
                _modifierRows.Add(row);
            }
        }

        private void RebuildAbilityGrid(
            DbConnection conn,
            Identity owner,
            SavedSpec selectedSpec,
            List<SavedSpecSlotAssignment> assignments)
        {
            ClearRows(_abilityCells);

            Dictionary<string, SavedSpecSlotAssignment> assignmentsBySlot = assignments
                .GroupBy(assignment => WireIdentifier.Normalize(assignment.SlotId))
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

            int requiredRows = ActionBarLayout.Rows;
            for (int row = 0; row < requiredRows; row++)
            {
                for (int col = 0; col < ActionBarLayout.Columns; col++)
                {
                    Vector2 cellPosition = ActionBarLayout.CellPosition(row, col);
                    string labelText = string.Empty;
                    string keyText = LoadoutGridKeyLabel(row, col);
                    Color fill = new(0.04f, 0.05f, 0.06f, 0.92f);
                    Color textColor = new(1f, 1f, 1f, 0.36f);
                    TooltipData tooltipData = default;
                    ActiveSelectableLoadoutAction resolved = default;

                    if (ActionBarKeymap.TryGetBindingForCell(row, col, out ActionBarSlotBinding binding))
                    {
                        assignmentsBySlot.TryGetValue(
                            WireIdentifier.Normalize(binding.SlotId),
                            out SavedSpecSlotAssignment? assignment);
                        resolved = ActiveLoadoutResolver.ResolveSelectableActionFromAssignment(
                            conn,
                            owner,
                            selectedSpec.ClassId,
                            binding.SlotId,
                            assignment);
                        if (resolved.HasAssignedAction)
                        {
                            labelText = resolved.DisplayName;
                            fill = resolved.IsFixed
                                ? FixedActionColor(resolved.ActionId)
                                : AbilityColor(resolved.AbilityId);
                            textColor = Color.white;
                            tooltipData = ActionTooltipResolver.ResolveForActionRef(conn, owner, resolved);
                        }
                    }

                    GameObject cell = CreateAbilityCell(
                        $"AbilityCell_{row}_{col}",
                        _abilityGridRoot,
                        labelText,
                        fill,
                        textColor);
                    SetRect((RectTransform)cell.transform, cellPosition, ActionBarLayout.SlotVector);

                    if (!HasPrefabFrame(cell))
                    {
                        Outline outline = cell.gameObject.AddComponent<Outline>();
                        outline.effectColor = resolved.HasAssignedAction ? new Color(1f, 1f, 1f, 0.16f) : new Color(1f, 1f, 1f, 0.06f);
                        outline.effectDistance = new Vector2(1f, -1f);
                    }

                    TMP_Text label = cell.GetComponentInChildren<TMP_Text>();
                    label.fontSize = resolved.HasAssignedAction ? 10 : 24;
                    label.textWrappingMode = TextWrappingModes.Normal;

                    TMP_Text keybind = MakeLabel("Keybind", cell.transform, 10, TextAnchor.UpperLeft, new Color(0.72f, 0.75f, 0.80f));
                    keybind.text = keyText;
                    SetRect(keybind.rectTransform, new Vector2(4f, ActionBarLayout.SlotSize - 20f), new Vector2(48f, 18f));

                    TooltipTarget tooltip = cell.GetComponent<TooltipTarget>()
                        ?? cell.AddComponent<TooltipTarget>();
                    tooltip.Configure(_canvas, tooltipData);

                    if (ActionBarKeymap.TryGetBindingForCell(row, col, out ActionBarSlotBinding assignmentBinding))
                    {
                        LoadoutActionDropSlot dropSlot = cell.GetComponent<LoadoutActionDropSlot>()
                            ?? cell.AddComponent<LoadoutActionDropSlot>();
                        dropSlot.Configure(_canvas, assignmentBinding.SlotId);

                        ActiveSelectableLoadoutAction dragResolved = resolved;
                        string dragSlotId = assignmentBinding.SlotId;
                        ConfigureCellDragSource(
                            cell,
                            _canvas,
                            () => LoadoutActionDragPayload.From(dragResolved, dragSlotId),
                            (payload, targetSlotId) => ApplyLoadoutActionDrop(conn, selectedSpec, payload, targetSlotId),
                            () => AssignSelectedActionToSlot(conn, selectedSpec, dragSlotId));
                    }

                    _abilityCells.Add(cell.gameObject);
                }
            }
        }

        private void AssignSelectedActionToSlot(DbConnection conn, SavedSpec selectedSpec, string slotId)
        {
            if (!_selectedAvailableAction.HasValue)
                return;

            LoadoutActionDropApplier.ApplyDrop(conn, selectedSpec.SpecId, _selectedAvailableAction.ToDragPayload(), slotId);
            _lastSignature = string.Empty;
        }

        private void ApplyLoadoutActionDrop(
            DbConnection conn,
            SavedSpec selectedSpec,
            LoadoutActionDragPayload payload,
            string? targetSlotId)
        {
            LoadoutActionDropApplier.ApplyDrop(conn, selectedSpec.SpecId, payload, targetSlotId);
            _lastSignature = string.Empty;
        }

        private void ResetPoints()
        {
            DbConnection? conn = NetworkManager.Instance?.Conn;
            if (conn == null || string.IsNullOrWhiteSpace(_selectedSpecId))
                return;

            foreach (string statKind in StatKinds)
                conn.Reducers.SetSavedSpecStatAllocation(_selectedSpecId, statKind, 0);
        }

        private void SaveSelectedSpec()
        {
            DbConnection? conn = NetworkManager.Instance?.Conn;
            if (conn == null || string.IsNullOrWhiteSpace(_selectedSpecId))
                return;

            conn.Reducers.ActivateSavedSpec(_selectedSpecId);
        }

        private static string LoadoutGridKeyLabel(int row, int col)
        {
            return ActionBarKeymap.KeyLabelForCell(row, col);
        }

        private void RebuildClassMenu(DbConnection conn, List<ClassCatalog> orderedClasses, string currentClassId)
        {
            string menuSignature = BuildClassMenuSignature(orderedClasses, currentClassId);
            if (string.Equals(menuSignature, _lastClassMenuSignature, StringComparison.Ordinal))
                return;

            _lastClassMenuSignature = menuSignature;
            ClearRows(_classRows);

            if (orderedClasses.Count == 0)
            {
                TMP_Text empty = MakeLabel("NoClasses", _classMenuRoot, 13, TextAnchor.MiddleLeft, new Color(0.72f, 0.75f, 0.80f));
                empty.text = "NO AUTHORED CLASSES";
                SetRectFromTop(empty.rectTransform, Vector2.zero, new Vector2(252f, 24f));
                _classRows.Add(empty.gameObject);
                return;
            }

            string normalizedCurrent = ClassIds.Canonicalize(currentClassId);
            for (int i = 0; i < orderedClasses.Count; i++)
            {
                ClassCatalog classRow = orderedClasses[i];
                string classId = classRow.ClassId;
                bool selected = string.Equals(ClassIds.Canonicalize(classId), normalizedCurrent, StringComparison.Ordinal);
                Color fill = selected
                    ? new Color(0.72f, 0.08f, 0.04f, 0.96f)
                    : new Color(0.06f, 0.07f, 0.09f, 0.86f);
                Color textColor = selected ? Color.white : new Color(0.88f, 0.90f, 0.94f);
                Button button = MakeButton($"Class_{ClassIds.Canonicalize(classId)}", _classMenuRoot, classRow.DisplayName.ToUpperInvariant(), fill, textColor);
                SetRectFromTop((RectTransform)button.transform, new Vector2(0f, i * 54f), new Vector2(252f, 44f));
                button.interactable = !selected;
                if (selected)
                {
                    ColorBlock colors = button.colors;
                    colors.disabledColor = fill;
                    button.colors = colors;
                }

                Outline outline = button.gameObject.AddComponent<Outline>();
                outline.effectColor = selected ? new Color(0.96f, 0.73f, 0.26f, 0.34f) : new Color(1f, 1f, 1f, 0.06f);
                outline.effectDistance = new Vector2(1f, -1f);

                TMP_Text label = button.GetComponentInChildren<TMP_Text>();
                label.alignment = TextAlignmentOptions.Left;
                label.rectTransform.offsetMin = new Vector2(16f, 0f);
                label.rectTransform.offsetMax = new Vector2(-16f, 0f);

                if (!selected)
                {
                    string targetClassId = classId;
                    button.onClick.AddListener(() =>
                    {
                        _selectedSpecId = null;
                        _selectedAvailableAction = default;
                        _lastSignature = string.Empty;
                        _lastClassMenuSignature = string.Empty;
                        conn.Reducers.SwitchLoadoutClass(targetClassId);
                    });
                }

                _classRows.Add(button.gameObject);
            }
        }

        private static string BuildClassMenuSignature(List<ClassCatalog> orderedClasses, string currentClassId)
        {
            var sb = new StringBuilder();
            sb.Append(ClassIds.Canonicalize(currentClassId)).Append('|');
            foreach (ClassCatalog classRow in orderedClasses)
                sb.Append(classRow.ClassId).Append(':')
                    .Append(classRow.DisplayName).Append(':')
                    .Append(classRow.SortOrder).Append(';');
            return sb.ToString();
        }

        private void TrySubscribeToReducerErrors()
        {
            DbConnection? conn = NetworkManager.Instance?.Conn;
            if (conn == null || ReferenceEquals(_subscribedConnection, conn))
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
            if (!IsLoadoutReducer(ctx.Event.Reducer))
                return;

            _lastError = error.Message;
            _errorUntilTime = Time.unscaledTime + 6f;
            _errorText.text = _lastError;
            Debug.LogWarning($"[LoadoutController] Reducer rejected: {error.Message}");
        }

        private static bool IsLoadoutReducer(Reducer reducer)
        {
            return reducer is Reducer.SwitchLoadoutClass
                or Reducer.SetSavedSpecStatAllocation
                or Reducer.AssignSavedSpecAbilityToSlot
                or Reducer.AssignSavedSpecActionToSlot
                or Reducer.ClearSavedSpecSlot
                or Reducer.ActivateSavedSpec;
        }

        private static List<(string Label, string Value)> BuildModifierList(
            DbConnection conn,
            IEnumerable<SavedSpecStatAllocation> allocations,
            string classId)
        {
            uint might = StatValue(allocations, "MIGHT");
            uint insight = StatValue(allocations, "INSIGHT");
            uint finesse = StatValue(allocations, "FINESSE");
            uint quickness = StatValue(allocations, "QUICKNESS");
            uint fortitude = StatValue(allocations, "FORTITUDE");
            ResourceCatalog? primaryResource = ResolvePrimaryResourceCatalog(conn, classId);
            float critChance = Mathf.Clamp(
                ResolveCombatRule(conn, RuleBaseCritChance, 0.05f)
                    + finesse * ResolveStatScaling(conn, EffectCritChancePerPoint),
                0f,
                ResolveCombatRule(conn, RuleMaxCritChance, 1f));

            List<(string Label, string Value)> modifiers = new()
            {
                ("Damage", FormatPercent(might * ResolveStatScaling(conn, EffectDamageMultiplierPerPoint))),
                ("Critical Chance", FormatTotalPercent(critChance)),
                ("Attack Speed", FormatPercent(quickness * ResolveStatScaling(conn, EffectCastSpeedMultiplierPerPoint))),
                ("Movement Speed", FormatPercent(quickness * ResolveStatScaling(conn, EffectRunSpeedMultiplierPerPoint))),
                ("Health", $"+{fortitude * ResolveStatScaling(conn, EffectMaxHpPerPoint):0.#}"),
            };

            if (primaryResource != null)
            {
                modifiers.Add(($"{primaryResource.DisplayName} Max", $"+{primaryResource.MaxPerInsight * insight:0.#}"));
                modifiers.Add(($"{primaryResource.DisplayName} Gain", FormatPercent(primaryResource.GainMultiplierPerInsight * insight)));
            }

            return modifiers;
        }

        private static ResourceCatalog? ResolvePrimaryResourceCatalog(DbConnection conn, string classId)
        {
            ClassCatalog? classRow = conn.Db.ClassCatalog.ClassId.Find(classId);
            if (classRow == null || string.IsNullOrWhiteSpace(classRow.PrimaryResourceKind))
                return null;

            return conn.Db.ResourceCatalog.ResourceKind.Find(classRow.PrimaryResourceKind);
        }

        private static float ResolveStatScaling(DbConnection conn, string effectKind)
        {
            foreach (StatScalingCatalog row in conn.Db.StatScalingCatalog.Iter().OrderBy(entry => entry.SortOrder))
            {
                if (string.Equals(row.EffectKind, effectKind, StringComparison.OrdinalIgnoreCase))
                    return row.ScalarValue;
            }

            return 0f;
        }

        private static float ResolveCombatRule(DbConnection conn, string ruleId, float fallback)
        {
            CombatRuleCatalog? row = conn.Db.CombatRuleCatalog.CombatRuleId.Find(ruleId);
            return row?.ScalarValue ?? fallback;
        }

        private static uint StatValue(IEnumerable<SavedSpecStatAllocation> allocations, string statKind)
        {
            return allocations
                .Where(allocation => string.Equals(allocation.StatKind, statKind, StringComparison.OrdinalIgnoreCase))
                .Select(allocation => allocation.AllocatedPoints)
                .FirstOrDefault();
        }

        private static List<ClassCatalog> OrderedClasses(DbConnection conn)
        {
            return conn.Db.ClassCatalog.Iter()
                .OrderBy(classRow => classRow.SortOrder)
                .ThenBy(classRow => classRow.DisplayName, StringComparer.Ordinal)
                .ToList();
        }

        private static string ResolveClassDisplayName(DbConnection conn, string classId)
        {
            ClassCatalog? classRow = conn.Db.ClassCatalog.ClassId.Find(classId);
            return string.IsNullOrWhiteSpace(classRow?.DisplayName) ? classId : classRow.DisplayName;
        }

        private static string ResolveCombatProfileDisplayName(DbConnection conn, string combatProfileId)
        {
            CombatProfileCatalog? profile = conn.Db.CombatProfileCatalog.CombatProfileId.Find(combatProfileId);
            return string.IsNullOrWhiteSpace(profile?.DisplayName) ? combatProfileId : profile.DisplayName;
        }

        private static string BuildSignature(
            CharacterProgression progression,
            string activeSpecId,
            SavedSpec selectedSpec,
            List<ClassCatalog> classes,
            List<SavedSpecStatAllocation> allocations,
            List<SavedSpecSlotAssignment> assignments,
            List<AbilityCatalog> abilities,
            List<ActionPresentationCatalog> actionPresentations,
            List<FixedActionBindingCatalog> fixedActionBindings,
            List<LoadoutSlotCatalog> slots,
            int remainingPoints,
            AvailableLoadoutAction selectedAction)
        {
            var sb = new StringBuilder();
            sb.Append(progression.ClassId).Append('|')
                .Append(activeSpecId).Append('|')
                .Append(selectedSpec.SpecId).Append('|')
                .Append(selectedSpec.Name).Append('|')
                .Append(remainingPoints).Append('|');
            foreach (ClassCatalog classRow in classes)
                sb.Append(classRow.ClassId).Append(':')
                    .Append(classRow.DisplayName).Append(':')
                    .Append(classRow.SortOrder).Append(';');
            sb.Append('|');
            foreach (SavedSpecStatAllocation allocation in allocations)
                sb.Append(allocation.StatKind).Append(':').Append(allocation.AllocatedPoints).Append(';');
            sb.Append('|');
            foreach (SavedSpecSlotAssignment assignment in assignments)
                sb.Append(assignment.SlotId).Append(':')
                    .Append(assignment.ActionKind).Append(':')
                    .Append(assignment.ActionId).Append(':')
                    .Append(assignment.AbilityId).Append(';');
            sb.Append('|');
            foreach (AbilityCatalog ability in abilities)
                sb.Append(ability.AbilityId).Append(':')
                    .Append(ability.AbilityKind).Append(':')
                    .Append(ability.ActionId).Append(':')
                    .Append(ability.DisplayName).Append(':')
                    .Append(ability.FixedActionId).Append(':')
                    .Append(ability.ResourceKind).Append(':')
                    .Append(ability.ResourceCost).Append(':')
                    .Append(ability.AbilityTags).Append(':')
                    .Append(ability.SortOrder).Append(';');
            sb.Append('|');
            foreach (ActionPresentationCatalog presentation in actionPresentations)
                sb.Append(presentation.Key).Append(':')
                    .Append(presentation.PresentationKind).Append(':')
                    .Append(presentation.PresentationId).Append(':')
                    .Append(presentation.DisplayName).Append(':')
                    .Append(presentation.Description).Append(':')
                    .Append(presentation.SortOrder).Append(';');
            sb.Append('|');
            foreach (FixedActionBindingCatalog binding in fixedActionBindings)
                sb.Append(binding.Key).Append(':')
                    .Append(binding.ClassId).Append(':')
                    .Append(binding.FixedActionId).Append(':')
                    .Append(binding.AbilityId).Append(':')
                    .Append(binding.SortOrder).Append(';');
            sb.Append('|');
            foreach (LoadoutSlotCatalog slot in slots)
                sb.Append(slot.SlotId).Append(':').Append(slot.UiRow).Append(':').Append(slot.UiCol).Append(';');
            sb.Append('|')
                .Append(selectedAction.ActionKind).Append(':')
                .Append(selectedAction.ActionId).Append(':')
                .Append(selectedAction.AbilityId);
            return sb.ToString();
        }

        private static List<AvailableLoadoutAction> BuildAvailableActions(
            DbConnection conn,
            string classId,
            List<AbilityCatalog> catalogAbilities,
            List<ActionPresentationCatalog> actionPresentations,
            List<FixedActionBindingCatalog> fixedActionBindings)
        {
            string normalizedClassId = ClassIds.Canonicalize(classId);
            List<AvailableLoadoutAction> actions = catalogAbilities
                .Where(ability => string.Equals(ClassIds.Canonicalize(ability.ClassId), normalizedClassId, StringComparison.Ordinal))
                .Where(ability => HasAbilityTag(ability, LoadoutActionTag))
                .Select(ability => new AvailableLoadoutAction(
                    ActionKinds.Ability,
                    ability.AbilityId,
                    ability.AbilityId,
                    ability.ClassId,
                    ActionPresentation.ResolveAbilityDisplayName(conn, ability.AbilityId, ability.DisplayName),
                    ability.SortOrder,
                    isFixed: false))
                .ToList();

            Dictionary<string, AvailableLoadoutAction> fixedActions = new(StringComparer.Ordinal);
            foreach (ActionPresentationCatalog presentation in actionPresentations)
            {
                if (!string.Equals(WireIdentifier.Normalize(presentation.PresentationKind), ActionTooltipResolver.PresentationKindFixed, StringComparison.Ordinal))
                    continue;

                string fixedActionId = WireIdentifier.Normalize(presentation.PresentationId);
                if (string.IsNullOrWhiteSpace(fixedActionId))
                    continue;

                fixedActions[fixedActionId] = new AvailableLoadoutAction(
                    ActionKinds.Fixed,
                    fixedActionId,
                    string.Empty,
                    normalizedClassId,
                    ActionPresentation.ResolveFixedDisplayName(conn, fixedActionId),
                    presentation.SortOrder,
                    isFixed: true);
            }

            foreach (FixedActionBindingCatalog binding in fixedActionBindings)
            {
                if (!string.Equals(ClassIds.Canonicalize(binding.ClassId), normalizedClassId, StringComparison.Ordinal))
                    continue;

                string fixedActionId = WireIdentifier.Normalize(binding.FixedActionId);
                if (string.IsNullOrWhiteSpace(fixedActionId))
                    continue;

                uint sortOrder = fixedActions.TryGetValue(fixedActionId, out AvailableLoadoutAction existing)
                    ? Math.Min(existing.SortOrder, binding.SortOrder)
                    : binding.SortOrder;
                fixedActions[fixedActionId] = new AvailableLoadoutAction(
                    ActionKinds.Fixed,
                    fixedActionId,
                    binding.AbilityId,
                    normalizedClassId,
                    ActionPresentation.ResolveFixedDisplayName(conn, fixedActionId),
                    sortOrder,
                    isFixed: true);
            }

            actions.AddRange(fixedActions.Values);
            return actions
                .OrderBy(action => action.SortOrder)
                .ThenBy(action => action.IsFixed ? 1 : 0)
                .ThenBy(action => action.DisplayName, StringComparer.Ordinal)
                .ToList();
        }

        private static bool HasAbilityTag(AbilityCatalog ability, string tag)
        {
            string normalizedTag = WireIdentifier.Normalize(tag);
            return ability.AbilityTags
                .Split('|')
                .Any(value => string.Equals(WireIdentifier.Normalize(value), normalizedTag, StringComparison.Ordinal));
        }

        private static bool SameActionRef(AvailableLoadoutAction left, AvailableLoadoutAction right)
        {
            return left.HasValue
                && right.HasValue
                && string.Equals(left.ActionKind, right.ActionKind, StringComparison.Ordinal)
                && string.Equals(left.ActionId, right.ActionId, StringComparison.Ordinal);
        }

        private static Vector2 AvailableActionCellPosition(int index)
        {
            int row = index / AvailableActionColumns;
            int col = index % AvailableActionColumns;
            return new Vector2(
                col * (AvailableActionSlotWidth + AvailableActionGap),
                AvailableActionsRootHeight - AvailableActionSlotHeight - row * (AvailableActionSlotHeight + AvailableActionGap));
        }

        private static RectTransform CreatePanel(string name, Transform parent, Vector2 offset, Vector2 size, Vector2 anchor)
        {
            GameObject go = new(name);
            go.transform.SetParent(parent, false);
            RectTransform rt = go.AddComponent<RectTransform>();
            rt.anchorMin = anchor;
            rt.anchorMax = anchor;
            rt.pivot = anchor;
            rt.anchoredPosition = new Vector2(offset.x, anchor.y > 0.5f ? -offset.y : offset.y);
            rt.sizeDelta = size;
            Image image = go.AddComponent<Image>();
            image.color = PanelFill;
            Outline outline = go.AddComponent<Outline>();
            outline.effectColor = PanelStroke;
            outline.effectDistance = new Vector2(1f, -1f);
            return rt;
        }

        private static RectTransform CreateRoot(string name, Transform parent, Vector2 topLeftOffset, Vector2 size)
        {
            GameObject go = new(name);
            go.transform.SetParent(parent, false);
            RectTransform rt = go.AddComponent<RectTransform>();
            SetRectFromTop(rt, topLeftOffset, size);
            return rt;
        }

        private static RectTransform CreateRootFromBottom(string name, Transform parent, Vector2 bottomLeftOffset, Vector2 size)
        {
            GameObject go = new(name);
            go.transform.SetParent(parent, false);
            RectTransform rt = go.AddComponent<RectTransform>();
            SetRectFromBottom(rt, bottomLeftOffset, size);
            return rt;
        }

        private static GameObject CreateRow(Transform parent, string name, Vector2 anchoredPosition, Vector2 size, Color fill)
        {
            GameObject row = new(name);
            row.transform.SetParent(parent, false);
            RectTransform rt = row.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = anchoredPosition;
            rt.sizeDelta = size;
            row.AddComponent<Image>().color = fill;
            return row;
        }

        private static void BuildMiniBar(Transform parent, Vector2 position, float width, float fillPercent, Color color)
        {
            GameObject bar = new("MiniBar");
            bar.transform.SetParent(parent, false);
            RectTransform rt = bar.AddComponent<RectTransform>();
            SetRect(rt, position, new Vector2(width, 4f));
            bar.AddComponent<Image>().color = new Color(1f, 1f, 1f, 0.12f);

            GameObject fill = new("Fill");
            fill.transform.SetParent(bar.transform, false);
            RectTransform fillRt = fill.AddComponent<RectTransform>();
            fillRt.anchorMin = new Vector2(0f, 0f);
            fillRt.anchorMax = new Vector2(0f, 1f);
            fillRt.pivot = new Vector2(0f, 0.5f);
            fillRt.anchoredPosition = Vector2.zero;
            fillRt.sizeDelta = new Vector2(width * fillPercent, 0f);
            fill.AddComponent<Image>().color = color;
        }

        private static void ClearRows(List<GameObject> rows)
        {
            foreach (GameObject row in rows)
                Destroy(row);
            rows.Clear();
        }

        private static TMP_Text MakeLabel(string name, Transform parent, int fontSize, TextAnchor alignment, Color color)
        {
            GameObject go = new(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<RectTransform>();
            TextMeshProUGUI text = go.AddComponent<TextMeshProUGUI>();
            text.font = ResolveFontAsset();
            text.fontSize = fontSize;
            text.alignment = ConvertAlignment(alignment);
            text.color = color;
            text.raycastTarget = false;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.richText = false;
            return text;
        }

        private static Button MakeButton(string name, Transform parent, string text, Color fill, Color textColor)
        {
            GameObject go = new(name);
            go.transform.SetParent(parent, false);
            Image image = go.AddComponent<Image>();
            image.color = fill;
            Button button = go.AddComponent<Button>();
            ColorBlock colors = button.colors;
            colors.normalColor = fill;
            colors.highlightedColor = Color.Lerp(fill, Color.white, 0.12f);
            colors.selectedColor = colors.highlightedColor;
            colors.pressedColor = Color.Lerp(fill, Color.black, 0.25f);
            colors.disabledColor = new Color(0.10f, 0.10f, 0.11f, 0.58f);
            button.colors = colors;

            TMP_Text label = MakeLabel("Text", go.transform, 13, TextAnchor.MiddleCenter, textColor);
            label.text = text;
            SetStretch(label.rectTransform);
            return button;
        }

        private static GameObject CreateAbilityCell(string name, Transform parent, string text, Color fill, Color textColor)
        {
            GameObject go = CreateActionBarSlot(name, parent);
            Image image = go.GetComponent<Image>() ?? go.AddComponent<Image>();
            image.color = HasPrefabFrame(go) ? TransparentSlotInput : fill;
            image.raycastTarget = true;

            TMP_Text label = MakeLabel("Text", go.transform, 10, TextAnchor.MiddleCenter, textColor);
            label.text = text;
            SetStretch(label.rectTransform);
            label.rectTransform.offsetMin = new Vector2(5f, 5f);
            label.rectTransform.offsetMax = new Vector2(-5f, -5f);
            return go;
        }

        private static void ConfigureCellDragSource(
            GameObject cell,
            Canvas canvas,
            Func<LoadoutActionDragPayload?> payloadProvider,
            Action<LoadoutActionDragPayload, string?> onDrop,
            Action? onClick = null)
        {
            LoadoutActionDragSource dragSource = cell.GetComponent<LoadoutActionDragSource>()
                ?? cell.AddComponent<LoadoutActionDragSource>();
            dragSource.Configure(canvas, payloadProvider, onDrop, onClick);
            ConfigureCellButtonFeedback(cell);
        }

        private static void ConfigureCellButtonFeedback(GameObject cell)
        {
            Button button = cell.GetComponent<Button>() ?? cell.AddComponent<Button>();
            button.onClick.RemoveAllListeners();
            ColorBlock colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1f, 1f, 1f, 0.86f);
            colors.selectedColor = colors.highlightedColor;
            colors.pressedColor = new Color(0.78f, 0.78f, 0.78f, 1f);
            colors.disabledColor = new Color(1f, 1f, 1f, 0.38f);
            colors.colorMultiplier = 1f;
            button.colors = colors;
        }

        private static GameObject CreateActionBarSlot(string name, Transform parent)
        {
            GameObject? prefab = Resources.Load<GameObject>(ActionBarLayout.SlotPrefabResourcePath);
            GameObject go = prefab != null
                ? UnityEngine.Object.Instantiate(prefab, parent, false)
                : new GameObject(name);

            go.name = name;
            if (prefab == null)
                go.transform.SetParent(parent, false);

            return go;
        }

        private static bool HasPrefabFrame(GameObject slot)
        {
            return slot.transform.Find("Frame") != null;
        }

        private static TMP_FontAsset ResolveFontAsset()
        {
            return TMP_Settings.defaultFontAsset
                ?? Resources.Load<TMP_FontAsset>("Fonts & Materials/LiberationSans SDF");
        }

        private static TextAlignmentOptions ConvertAlignment(TextAnchor alignment)
        {
            return alignment switch
            {
                TextAnchor.UpperLeft => TextAlignmentOptions.TopLeft,
                TextAnchor.UpperCenter => TextAlignmentOptions.Top,
                TextAnchor.UpperRight => TextAlignmentOptions.TopRight,
                TextAnchor.MiddleLeft => TextAlignmentOptions.Left,
                TextAnchor.MiddleCenter => TextAlignmentOptions.Center,
                TextAnchor.MiddleRight => TextAlignmentOptions.Right,
                TextAnchor.LowerLeft => TextAlignmentOptions.BottomLeft,
                TextAnchor.LowerCenter => TextAlignmentOptions.Bottom,
                TextAnchor.LowerRight => TextAlignmentOptions.BottomRight,
                _ => TextAlignmentOptions.Left,
            };
        }

        private static string PrettyStatName(string statKind)
        {
            return statKind switch
            {
                "MIGHT" => "Might",
                "INSIGHT" => "Insight",
                "FINESSE" => "Finesse",
                "QUICKNESS" => "Quickness",
                "FORTITUDE" => "Fortitude",
                _ => statKind,
            };
        }

        private static Color StatColor(string statKind)
        {
            return statKind switch
            {
                "MIGHT" => new Color(1f, 0.34f, 0.18f),
                "INSIGHT" => new Color(0.74f, 0.28f, 1f),
                "FINESSE" => new Color(0.50f, 1f, 0.36f),
                "QUICKNESS" => new Color(0.28f, 0.72f, 1f),
                "FORTITUDE" => new Color(1f, 0.78f, 0.24f),
                _ => Color.white,
            };
        }

        private static string StatGlyph(string statKind)
        {
            return statKind switch
            {
                "MIGHT" => "*",
                "INSIGHT" => "^",
                "FINESSE" => "+",
                "QUICKNESS" => "v",
                "FORTITUDE" => "#",
                _ => ".",
            };
        }

        private static Color AbilityColor(string abilityId)
        {
            int hash = Mathf.Abs(abilityId.GetHashCode());
            Color[] colors =
            {
                new(0.72f, 0.10f, 0.04f, 0.95f),
                new(0.45f, 0.06f, 0.75f, 0.95f),
                new(0.11f, 0.35f, 0.62f, 0.95f),
                new(0.40f, 0.22f, 0.08f, 0.95f),
            };
            return colors[hash % colors.Length];
        }

        private static Color FixedActionColor(string actionId)
        {
            return WireIdentifier.Normalize(actionId) switch
            {
                FixedActionIds.Dodge => new Color(0.16f, 0.36f, 0.34f, 0.95f),
                FixedActionIds.Parry => new Color(0.50f, 0.42f, 0.16f, 0.95f),
                _ => new Color(0.58f, 0.20f, 0.08f, 0.95f),
            };
        }

        private static string FormatPercent(float value)
        {
            return $"+{value * 100f:0.#}%";
        }

        private static string FormatTotalPercent(float value)
        {
            return $"{value * 100f:0.#}%";
        }

        private static void SetRect(RectTransform rt, Vector2 anchoredPosition, Vector2 size)
        {
            rt.anchorMin = new Vector2(0f, 0f);
            rt.anchorMax = new Vector2(0f, 0f);
            rt.pivot = new Vector2(0f, 0f);
            rt.anchoredPosition = anchoredPosition;
            rt.sizeDelta = size;
        }

        private static void SetRectFromTop(RectTransform rt, Vector2 topLeftOffset, Vector2 size)
        {
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(topLeftOffset.x, -topLeftOffset.y);
            rt.sizeDelta = size;
        }

        private static void SetRectFromBottom(RectTransform rt, Vector2 bottomLeftOffset, Vector2 size)
        {
            rt.anchorMin = new Vector2(0f, 0f);
            rt.anchorMax = new Vector2(0f, 0f);
            rt.pivot = new Vector2(0f, 0f);
            rt.anchoredPosition = bottomLeftOffset;
            rt.sizeDelta = size;
        }

        private static void SetStretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }
    }
}
