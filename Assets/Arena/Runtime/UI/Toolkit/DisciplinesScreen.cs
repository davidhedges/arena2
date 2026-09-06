#nullable enable

using Arena.Combat;
using Arena.Network;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace Arena.UI
{
    /// <summary>
    /// Canonical Form/School + Feature + Trait editor. It edits one
    /// transport-neutral v2 aggregate and submits one revision-checked save.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class DisciplinesScreen : MonoBehaviour, IEscapeCloseable
    {
        private const string RuntimeObjectName = "DisciplinesScreenRuntime";
        private const string OpenClass = "is-open";
        private const string SelectedClass = "is-selected";

        private PanelSettings? _panelSettings;
        private VisualElement? _root;
        private VisualElement? _cards;
        private VisualElement? _traitOptions;
        private ScrollView? _editor;
        private VisualElement? _saveSummary;
        private VisualElement? _picker;
        private VisualElement? _pickerOptions;
        private Label? _pickerTitle;
        private Label? _pickerSubtitle;
        private Label? _pickerDisciplineStep;
        private Label? _pickerSpecializationStep;
        private Label? _pickerHint;
        private Button? _pickerBack;
        private string _pickerDisciplineId = string.Empty;
        private Label? _activeAllocation;
        private Label? _totalAllocation;
        private Label? _disciplineAllocation;
        private Label? _saveStatus;
        private Button? _saveButton;
        private HubNetworkManager? _hub;
        private CombatBuildV2EditorModel? _model;
        private ulong _loadedRevision;
        private string _lastServerFailure = string.Empty;
        private bool _open;
        private bool _dirty;
        private bool _savePending;
        private string _focusedSpecializationId = string.Empty;

        public event Action? Closed;
        public event Action? EquipmentRequested;

        public int EscapeClosePriority => 115;
        public bool IsEscapeCloseable => _open;

        public static DisciplinesScreen Ensure(Transform parent)
        {
            DisciplinesScreen? screen = FindObjectsByType<DisciplinesScreen>(
                    FindObjectsInactive.Include,
                    FindObjectsSortMode.None)
                .FirstOrDefault(candidate => candidate.gameObject.scene == parent.gameObject.scene);
            if (screen != null && screen.transform.parent == null)
                return screen;
            if (screen != null)
                Destroy(screen.gameObject);

            GameObject host = new(RuntimeObjectName);
            SceneManager.MoveGameObjectToScene(host, parent.gameObject.scene);
            return host.AddComponent<DisciplinesScreen>();
        }

        private void Awake()
        {
            RuntimeUiEventSystem.Ensure();
            BuildUi();
            BindHub(HubNetworkManager.EnsureInstance());
        }

        private void OnEnable() => RuntimeUiEscapeRouter.Register(this);
        private void OnDisable() => RuntimeUiEscapeRouter.Unregister(this);

        private void OnDestroy()
        {
            BindHub(null);
            if (_panelSettings != null)
                Destroy(_panelSettings);
        }

        public void Open()
        {
            if (_root == null)
                return;
            _open = true;
            if (_panelSettings != null)
                _panelSettings.sortingOrder = RuntimeUiLayer.NextSortingOrder();
            _root.AddToClassList(OpenClass);
            ReloadFromHub(force: true);
        }

        public void Close()
        {
            if (!_open)
                return;
            ClosePicker();
            _open = false;
            _root?.RemoveFromClassList(OpenClass);
            Closed?.Invoke();
        }

        public bool TryCloseForEscape()
        {
            if (!_open)
                return false;
            if (_picker?.ClassListContains(OpenClass) == true)
            {
                BackInPicker();
                return true;
            }
            Close();
            return true;
        }

        private void BuildUi()
        {
            UIDocument document = ArenaPanel.CreateDocument(gameObject, "UI/Toolkit/Disciplines", 22f);
            _panelSettings = document.panelSettings;
            _root = document.rootVisualElement.Q<VisualElement>("DisciplinesScreen");
            if (_root == null)
                return;

            _cards = _root.Q<VisualElement>("DisciplineCards");
            _traitOptions = _root.Q<VisualElement>("TraitOptions");
            _editor = _root.Q<ScrollView>("SpecializationEditor");
            _saveSummary = _root.Q<VisualElement>("SaveSummary");
            _picker = _root.Q<VisualElement>("PickerOverlay");
            _pickerOptions = _root.Q<VisualElement>("PickerOptions");
            _pickerTitle = _root.Q<Label>("PickerTitle");
            _pickerSubtitle = _root.Q<Label>("PickerSubtitle");
            _pickerDisciplineStep = _root.Q<Label>("PickerDisciplineStep");
            _pickerSpecializationStep = _root.Q<Label>("PickerSpecializationStep");
            _pickerHint = _root.Q<Label>("PickerHint");
            _pickerBack = _root.Q<Button>("PickerBack");
            _activeAllocation = _root.Q<Label>("ActiveAllocation");
            _totalAllocation = _root.Q<Label>("TotalAllocation");
            _disciplineAllocation = _root.Q<Label>("DisciplineAllocation");
            _saveStatus = _root.Q<Label>("SaveStatus");
            _saveButton = _root.Q<Button>("SaveBuild");

            BindButton("SaveBuild", SaveDraft);
            BindButton("BackButton", Close);
            BindButton("NavPlay", Close);
            BindButton("NavPlayTab", Close);
            BindButton("NavEquipment", RequestEquipment);
            BindButton("PickerClose", ClosePicker);
            BindButton("PickerScrim", ClosePicker);
            BindButton("PickerBack", BackInPicker);
            Button? settings = _root.Q<Button>("SettingsButton");
            if (settings != null)
                settings.clicked += SystemMenuScreen.OpenFromEscape;
        }

        private void BindButton(string name, Action callback)
        {
            Button? button = _root?.Q<Button>(name);
            if (button != null)
                button.clicked += callback;
        }

        private void RequestEquipment()
        {
            Close();
            EquipmentRequested?.Invoke();
        }

        private void BindHub(HubNetworkManager? hub)
        {
            if (ReferenceEquals(_hub, hub))
                return;
            if (_hub != null)
            {
                _hub.Changed -= OnHubChanged;
                _hub.CombatBuildSaveCompleted -= OnCombatBuildSaved;
            }
            _hub = hub;
            if (_hub != null)
            {
                _hub.Changed += OnHubChanged;
                _hub.CombatBuildSaveCompleted += OnCombatBuildSaved;
            }
        }

        private void OnHubChanged()
        {
            if (!_open)
                return;
            CombatBuildV2DraftModel? build = _hub?.CombatBuild;
            if (build != null && build.Revision != _loadedRevision)
                ReloadFromHub(force: true);
            else if (_model == null)
                ReloadFromHub(force: false);
            else
                Render();
        }

        private void ReloadFromHub(bool force)
        {
            CombatBuildV2DraftModel? build = _hub?.CombatBuild;
            CombatBuildV2ContractModel? contract = _hub?.CombatBuildContract;
            CombatBuildV2CatalogModel? catalog = _hub?.CombatBuildCatalog;
            if (build == null || contract == null || catalog == null)
            {
                if (force)
                {
                    ClosePicker();
                    _model = null;
                }
                Render();
                return;
            }
            if (!force && _model != null)
                return;

            ClosePicker();
            _model = new CombatBuildV2EditorModel(build, catalog, contract);
            _loadedRevision = build.Revision;
            _dirty = false;
            _savePending = false;
            _lastServerFailure = string.Empty;
            _focusedSpecializationId = _model.SelectedSpecializationIds.FirstOrDefault()
                ?? string.Empty;
            Render();
        }

        private void Render()
        {
            if (_cards == null || _traitOptions == null || _editor == null || _saveSummary == null)
                return;
            _cards.Clear();
            _traitOptions.Clear();
            _editor.Clear();
            _saveSummary.Clear();
            if (_model == null || _hub?.CombatBuildContract == null || _hub.CombatBuildCatalog == null)
            {
                SetAllocation("—", "—", "—");
                _cards.Add(BuildWaitingState("WAITING FOR LOADOUT"));
                _editor.Add(BuildWaitingState("COMBAT BUILD DATA IS NOT AVAILABLE YET"));
                SetStatus("Connecting to your saved loadout…");
                _saveButton?.SetEnabled(false);
                return;
            }

            CombatBuildV2ContractModel contract = _hub.CombatBuildContract;
            CombatBuildV2CatalogModel catalog = _hub.CombatBuildCatalog;
            SetAllocation(
                _model.SelectedActiveCount.ToString(),
                _model.FeatureCapacityText.Replace(" FEATURES", string.Empty),
                $"{_model.SelectedSpecializationIds.Count} / {contract.MaximumSelectedSpecializations}");

            if (!_model.SelectedSpecializationIds.Contains(
                    _focusedSpecializationId,
                    StringComparer.Ordinal))
            {
                _focusedSpecializationId = _model.SelectedSpecializationIds.FirstOrDefault()
                    ?? string.Empty;
            }

            for (int index = 0; index < _model.SelectedSpecializationIds.Count; index++)
            {
                string specializationId = _model.SelectedSpecializationIds[index];
                CombatSpecializationDefinitionV2Model? definition =
                    catalog.FindSpecialization(specializationId);
                if (definition != null)
                    _cards.Add(BuildSpecializationSummary(definition, index));
            }
            for (int index = _model.SelectedSpecializationIds.Count;
                 index < contract.MaximumSelectedSpecializations;
                 index++)
            {
                _cards.Add(BuildAddSpecializationCard(index));
            }
            MarkLastChild(_cards);

            BuildTraitOptions(catalog);

            CombatSpecializationDefinitionV2Model? focused =
                catalog.FindSpecialization(_focusedSpecializationId);
            if (focused != null)
                _editor.Add(BuildSpecializationCard(focused));
            else
                _editor.Add(BuildEmptyEditor());

            BuildSaveSummary(catalog, contract);

            _saveButton?.SetEnabled(_hub.IsReady && !_savePending && _model.CanSubmit);
            SetStatus(BuildStatus());
        }

        private Button BuildSpecializationSummary(
            CombatSpecializationDefinitionV2Model definition,
            int slotIndex)
        {
            bool focused = string.Equals(
                _focusedSpecializationId,
                definition.SpecializationId,
                StringComparison.Ordinal);
            bool starts = string.Equals(
                _model!.StartingDisciplineId,
                definition.CombatDisciplineId,
                StringComparison.Ordinal);
            Color accent = DisciplineColor(definition.CombatDisciplineId);
            Button card = new(() => FocusSpecialization(definition.SpecializationId))
            {
                tooltip = $"Configure {definition.DisplayName}",
            };
            card.AddToClassList("discipline-summary-card");
            card.EnableInClassList(SelectedClass, focused);
            card.style.borderTopColor = accent;

            VisualElement identity = new() { pickingMode = PickingMode.Ignore };
            identity.AddToClassList("summary-identity");
            VisualElement slot = new();
            slot.AddToClassList("summary-slot");
            ApplyBorderColor(slot, accent);
            Label slotNumber = new((slotIndex + 1).ToString());
            slotNumber.AddToClassList("summary-slot-number");
            slot.Add(slotNumber);
            identity.Add(slot);

            VisualElement icon = new();
            icon.AddToClassList("summary-discipline-icon");
            ApplyIcon(icon, ResolveSpecializationIcon(definition.SpecializationId));
            identity.Add(icon);

            VisualElement copy = new();
            copy.AddToClassList("summary-copy");
            Label parent = new(DisciplineDisplayName(definition.CombatDisciplineId));
            parent.AddToClassList("summary-discipline-name");
            copy.Add(parent);
            Label specialization = new(definition.DisplayName.ToUpperInvariant());
            specialization.AddToClassList("summary-specialization-name");
            specialization.style.color = accent;
            copy.Add(specialization);
            identity.Add(copy);
            if (starts)
            {
                Label starting = new("STARTING");
                starting.AddToClassList("starting-chip");
                identity.Add(starting);
            }
            card.Add(identity);

            IReadOnlyList<CombatFeatureDefinitionV2Model> features =
                _model.FeaturePickerOptions(definition.SpecializationId);
            CombatFeatureDefinitionV2Model[] selected = features
                .Where(row => _model.IsFeatureSelected(row.AbilityId))
                .ToArray();
            VisualElement featureHeading = new() { pickingMode = PickingMode.Ignore };
            featureHeading.AddToClassList("summary-feature-heading");
            Label heading = new("SELECTED FEATURES");
            heading.AddToClassList("summary-feature-label");
            Label count = new(selected.Length.ToString());
            count.AddToClassList("summary-feature-count");
            featureHeading.Add(heading);
            featureHeading.Add(count);
            card.Add(featureHeading);

            VisualElement featureRow = new() { pickingMode = PickingMode.Ignore };
            featureRow.AddToClassList("summary-feature-row");
            if (selected.Length == 0)
            {
                Label empty = new("No features selected");
                empty.AddToClassList("summary-feature-empty");
                featureRow.Add(empty);
            }
            else
            {
                const int visibleLimit = 10;
                foreach (CombatFeatureDefinitionV2Model feature in selected.Take(visibleLimit))
                {
                    VisualElement featureIcon = new();
                    featureIcon.AddToClassList("summary-feature-icon");
                    featureIcon.tooltip = feature.DisplayName;
                    ApplyIcon(featureIcon, ActionIconResolver.Resolve(ActionKinds.Ability, feature.AbilityId));
                    featureRow.Add(featureIcon);
                }
                if (selected.Length > visibleLimit)
                {
                    Label overflow = new($"+{selected.Length - visibleLimit}");
                    overflow.AddToClassList("summary-feature-overflow");
                    featureRow.Add(overflow);
                }
            }
            card.Add(featureRow);
            return card;
        }

        private VisualElement BuildSpecializationCard(
            CombatSpecializationDefinitionV2Model definition)
        {
            VisualElement card = new() { name = $"SpecializationCard_{definition.SpecializationId}" };
            card.AddToClassList("discipline-card");
            Color accent = DisciplineColor(definition.CombatDisciplineId);

            VisualElement heading = new();
            heading.AddToClassList("discipline-card-heading");
            VisualElement identity = new();
            identity.AddToClassList("discipline-identity");
            VisualElement icon = new();
            icon.AddToClassList("discipline-emblem");
            ApplyIcon(icon, ResolveSpecializationIcon(definition.SpecializationId));
            ApplyBorderColor(icon, accent);
            identity.Add(icon);
            VisualElement copy = new();
            copy.AddToClassList("discipline-copy");
            Label kicker = new(DisciplineDisplayName(definition.CombatDisciplineId));
            kicker.AddToClassList("card-kicker");
            Label name = new(definition.DisplayName.ToUpperInvariant());
            name.AddToClassList("discipline-name");
            name.style.color = accent;
            copy.Add(kicker);
            copy.Add(name);
            identity.Add(copy);
            heading.Add(identity);

            VisualElement controls = new();
            controls.AddToClassList("discipline-controls");
            bool starts = string.Equals(
                _model!.StartingDisciplineId,
                definition.CombatDisciplineId,
                StringComparison.Ordinal);
            Button start = new(() => ToggleStartingDiscipline(definition.CombatDisciplineId))
            {
                text = starts ? "STARTING" : "SET STARTING",
            };
            start.AddToClassList("card-control");
            start.EnableInClassList(SelectedClass, starts);
            controls.Add(start);
            Button equipment = new(RequestEquipment) { text = "EDIT WEAPON" };
            equipment.AddToClassList("card-control");
            controls.Add(equipment);
            Button remove = new(() => RemoveSpecialization(definition.SpecializationId))
            {
                text = "REMOVE",
            };
            remove.AddToClassList("card-control");
            remove.AddToClassList("remove-control");
            remove.SetEnabled(_model.SelectedSpecializationIds.Count > 1);
            controls.Add(remove);
            heading.Add(controls);
            card.Add(heading);

            IReadOnlyList<CombatFeatureDefinitionV2Model> features =
                _model.FeaturePickerOptions(definition.SpecializationId);
            Label help = new("Choose techniques, spells, and perks. Feature capacity is shared across your entire build.");
            help.AddToClassList("feature-help");
            card.Add(help);
            card.Add(SectionHeading(
                "TECHNIQUES · SPELLS · PERKS",
                $"{features.Count(row => _model.IsFeatureSelected(row.AbilityId))} SELECTED"));
            VisualElement featureGrid = new();
            featureGrid.AddToClassList("active-slot-grid");
            foreach (CombatFeatureDefinitionV2Model feature in features)
                featureGrid.Add(BuildFeatureButton(feature));
            card.Add(featureGrid);

            CombatBuildV2DraftModel orderedDraft = _model.ToDraft();
            string[] selectedActive = features
                .Where(row => row.IsActive && _model.IsFeatureSelected(row.AbilityId))
                .OrderBy(row => orderedDraft.SelectedFeatures
                    .First(selection => selection.AbilityId == row.AbilityId)
                    .PreferredBarOrder ?? byte.MaxValue)
                .Select(row => row.AbilityId)
                .ToArray();
            if (selectedActive.Length > 1)
            {
                card.Add(SectionHeading("BAR ORDER", "USE THE ARROWS TO REORDER"));
                for (int index = 0; index < selectedActive.Length; index++)
                {
                    int targetIndex = index;
                    string abilityId = selectedActive[index];
                    VisualElement orderRow = new();
                    orderRow.AddToClassList("order-row");
                    Label orderNumber = new((index + 1).ToString());
                    orderNumber.AddToClassList("order-number");
                    orderRow.Add(orderNumber);
                    Label orderName = new(
                        _hub!.CombatBuildCatalog!.FindFeature(abilityId)?.DisplayName ?? abilityId);
                    orderName.AddToClassList("order-name");
                    orderRow.Add(orderName);
                    Button up = new(() => MoveActive(abilityId, targetIndex - 1)) { text = "↑" };
                    up.AddToClassList("order-control");
                    up.SetEnabled(index > 0);
                    Button down = new(() => MoveActive(abilityId, targetIndex + 1)) { text = "↓" };
                    down.AddToClassList("order-control");
                    down.SetEnabled(index + 1 < selectedActive.Length);
                    orderRow.Add(up);
                    orderRow.Add(down);
                    card.Add(orderRow);
                }
            }
            return card;
        }

        private Button BuildFeatureButton(CombatFeatureDefinitionV2Model feature)
        {
            bool selected = _model!.IsFeatureSelected(feature.AbilityId);
            string kind = feature.LoadoutKind.ToString().ToUpperInvariant();
            Button button = new(() => ToggleFeature(feature))
            {
                tooltip = string.IsNullOrWhiteSpace(feature.ResourceKind) || feature.ResourceCost <= 0f
                    ? kind
                    : $"{kind} · {feature.ResourceCost:0.#} {feature.ResourceKind}",
            };
            button.AddToClassList("ability-cell");
            button.EnableInClassList(SelectedClass, selected);
            button.EnableInClassList("is-filled", selected);
            VisualElement icon = new() { pickingMode = PickingMode.Ignore };
            icon.AddToClassList("ability-icon");
            ApplyIcon(icon, ActionIconResolver.Resolve(ActionKinds.Ability, feature.AbilityId));
            button.Add(icon);
            Label kindLabel = new(kind) { pickingMode = PickingMode.Ignore };
            kindLabel.AddToClassList("ability-kind");
            button.Add(kindLabel);
            Label name = new(feature.DisplayName.ToUpperInvariant()) { pickingMode = PickingMode.Ignore };
            name.AddToClassList("ability-name");
            button.Add(name);
            Label state = new(selected ? "EQUIPPED" : "SELECT") { pickingMode = PickingMode.Ignore };
            state.AddToClassList("ability-state");
            button.Add(state);
            return button;
        }

        private void BuildTraitOptions(CombatBuildV2CatalogModel catalog)
        {
            foreach (CombatTraitDefinitionV2Model trait in catalog.Traits
                         .OrderBy(value => value.SortOrder))
            {
                bool selected = _model.SelectedTraitIds.Contains(
                    trait.AbilityId,
                    StringComparer.Ordinal);
                Button button = new(() => ToggleTrait(trait))
                {
                    tooltip = trait.AbilityId == "MASTERY"
                        ? "10% bonus outgoing damage while the build uses one parent Discipline."
                        : trait.AbilityId,
                };
                button.AddToClassList("trait-button");
                button.EnableInClassList(SelectedClass, selected);
                VisualElement sigil = new() { pickingMode = PickingMode.Ignore };
                sigil.AddToClassList("trait-sigil");
                Label glyph = new("+");
                glyph.AddToClassList("trait-sigil-glyph");
                sigil.Add(glyph);
                button.Add(sigil);
                VisualElement copy = new() { pickingMode = PickingMode.Ignore };
                copy.AddToClassList("trait-copy");
                Label name = new(trait.DisplayName.ToUpperInvariant());
                name.AddToClassList("trait-name");
                copy.Add(name);
                Label description = new(trait.AbilityId == "MASTERY"
                    ? "10% outgoing damage with one parent Discipline"
                    : trait.AbilityId);
                description.AddToClassList("trait-description");
                copy.Add(description);
                button.Add(copy);
                Label state = new(selected ? "EQUIPPED" : "AVAILABLE")
                {
                    pickingMode = PickingMode.Ignore,
                };
                state.AddToClassList("trait-state");
                button.Add(state);
                _traitOptions!.Add(button);
            }
            Label capacity = new(_model!.TraitCapacityText) { pickingMode = PickingMode.Ignore };
            capacity.AddToClassList("trait-capacity");
            _traitOptions!.Add(capacity);
        }

        private Button BuildAddSpecializationCard(int slotIndex)
        {
            Button add = new(OpenSpecializationPicker);
            add.AddToClassList("add-discipline-card");
            VisualElement slot = new() { pickingMode = PickingMode.Ignore };
            slot.AddToClassList("empty-slot");
            Label slotNumber = new((slotIndex + 1).ToString());
            slotNumber.AddToClassList("empty-slot-number");
            slot.Add(slotNumber);
            add.Add(slot);
            Label plus = new("+") { pickingMode = PickingMode.Ignore };
            plus.AddToClassList("add-discipline-plus");
            add.Add(plus);
            Label title = new("ADD FORM OR SCHOOL") { pickingMode = PickingMode.Ignore };
            title.AddToClassList("add-discipline-title");
            add.Add(title);
            Label copy = new("Choose a weapon or spellcasting first.")
            {
                pickingMode = PickingMode.Ignore,
            };
            copy.AddToClassList("add-discipline-copy");
            add.Add(copy);
            return add;
        }

        private void OpenSpecializationPicker()
        {
            if (_model == null || _pickerOptions == null)
                return;
            _pickerDisciplineId = string.Empty;
            OpenPicker("Choose your discipline", "Start with a weapon or spellcasting. Then find your path.");
            SetPickerStep(false);
            List<Button> choices = new();
            foreach (var group in _model.SpecializationPickerOptions()
                         .GroupBy(row => row.CombatDisciplineId, StringComparer.Ordinal)
                         .OrderBy(group => group.All(row => row.SpecializationKind == CombatSpecializationKindV2.School)))
            {
                CombatSpecializationDefinitionV2Model[] options = group.ToArray();
                string parentId = group.Key;
                Button button = BuildPickerOption(
                    PickerDisciplineName(parentId),
                    ResolvePickerDisciplineIcon(parentId) ?? ResolveSpecializationIcon(options[0].SpecializationId),
                    DisciplineColor(parentId));
                button.name = $"PickerDiscipline_{parentId}";
                button.clicked += () => OpenDisciplineSpecializations(parentId);
                choices.Add(button);
            }
            _pickerOptions.Add(BuildPickerConstellation(choices));
            if (choices.Count == 0)
                _pickerOptions.Add(BuildWaitingState("All available forms and schools are already in your loadout."));
            FocusPickerOption(choices.FirstOrDefault());
        }

        private void OpenDisciplineSpecializations(string parentId)
        {
            if (_model == null || _pickerOptions == null)
                return;
            CombatSpecializationDefinitionV2Model[] options = _model.SpecializationPickerOptions()
                .Where(row => string.Equals(row.CombatDisciplineId, parentId, StringComparison.Ordinal))
                .ToArray();
            if (options.Length == 0)
            {
                OpenSpecializationPicker();
                return;
            }
            _pickerDisciplineId = parentId;
            bool schools = options.All(row => row.SpecializationKind == CombatSpecializationKindV2.School);
            OpenPicker(schools ? "Choose your school" : "Choose your form",
                $"Explore {PickerDisciplineName(parentId).ToLowerInvariant()}. Select a path to add it to your loadout.");
            SetPickerStep(true);
            List<Button> choices = new();
            foreach (CombatSpecializationDefinitionV2Model option in options)
            {
                Button button = BuildPickerOption(option.DisplayName,
                    ResolveSpecializationIcon(option.SpecializationId), DisciplineColor(parentId));
                button.name = $"PickerSpecialization_{option.SpecializationId}";
                button.clicked += () =>
                {
                    if (_model?.AddSpecialization(option.SpecializationId) == true)
                    {
                        EnsureWeaponConfiguration(option.CombatDisciplineId);
                        _focusedSpecializationId = option.SpecializationId;
                        MarkDirtyAndRender();
                        ClosePicker();
                        _cards?.Q<Button>(className: SelectedClass)?.Focus();
                    }
                };
                choices.Add(button);
            }
            _pickerOptions.Add(BuildPickerConstellation(choices));
            FocusPickerOption(choices.FirstOrDefault());
        }

        private static VisualElement BuildPickerConstellation(IReadOnlyList<Button> choices)
        {
            VisualElement constellation = new();
            constellation.AddToClassList("picker-constellation");
            VisualElement orbit = new() { pickingMode = PickingMode.Ignore };
            orbit.AddToClassList("picker-orbit");
            VisualElement innerOrbit = new() { pickingMode = PickingMode.Ignore };
            innerOrbit.AddToClassList("picker-orbit-inner");
            orbit.Add(innerOrbit);
            VisualElement compass = new() { pickingMode = PickingMode.Ignore };
            compass.AddToClassList("picker-compass");
            orbit.Add(compass);
            constellation.Add(orbit);
            foreach (Button choice in choices)
                constellation.Add(choice);

            // Keep every crest visible and its label upright as the panel resizes.
            constellation.RegisterCallback<GeometryChangedEvent>(_ =>
            {
                float width = constellation.contentRect.width;
                float height = constellation.contentRect.height;
                if (width <= 0f || height <= 0f || choices.Count == 0)
                    return;
                float scale = Mathf.Min(1f, width / 860f, height / 440f);
                float iconSize = (choices.Count <= 3 ? 128f : 104f) * scale;
                float choiceWidth = 168f * scale;
                float labelHeight = 46f * scale;
                float centerX = width * 0.5f;
                float centerY = (height - labelHeight) * 0.5f;
                float radiusX = (width - choiceWidth) * 0.49f;
                float radiusY = (height - iconSize - labelHeight) * 0.48f;
                orbit.style.left = centerX - radiusX;
                orbit.style.top = centerY - radiusY;
                orbit.style.width = radiusX * 2f;
                orbit.style.height = radiusY * 2f;
                for (int index = 0; index < choices.Count; index++)
                {
                    // One or two remaining paths stay centered, without empty positions.
                    float angle = choices.Count <= 2 ? Mathf.PI - index * Mathf.PI :
                        -Mathf.PI * 0.5f + index * Mathf.PI * 2f / choices.Count;
                    if (choices.Count == 6)
                        angle += Mathf.PI / 6f;
                    float x = choices.Count == 1 ? centerX : centerX + Mathf.Cos(angle) * radiusX;
                    float y = choices.Count <= 2 ? centerY : centerY + Mathf.Sin(angle) * radiusY;
                    Button choice = choices[index];
                    choice.style.left = x - choiceWidth * 0.5f;
                    choice.style.top = y - iconSize * 0.5f;
                    choice.style.width = choiceWidth;
                    VisualElement crest = choice.Q<VisualElement>(className: "picker-crest");
                    crest.style.width = iconSize;
                    crest.style.height = iconSize;
                    Label name = choice.Q<Label>();
                    name.style.fontSize = 15f * scale;
                    name.style.marginTop = 14f * scale;
                    name.style.minHeight = 32f * scale;
                }
            });
            return constellation;
        }

        private void SetPickerStep(bool choosingSpecialization)
        {
            _pickerDisciplineStep?.EnableInClassList("is-current", !choosingSpecialization);
            _pickerDisciplineStep?.EnableInClassList("is-complete", choosingSpecialization);
            if (_pickerDisciplineStep != null)
                _pickerDisciplineStep.text = choosingSpecialization
                    ? $"01  {PickerDisciplineName(_pickerDisciplineId)}"
                    : "01  WEAPON / SPELLCASTING";
            _pickerSpecializationStep?.EnableInClassList("is-current", choosingSpecialization);
            if (_pickerBack != null)
                _pickerBack.text = choosingSpecialization ? "‹  BACK TO DISCIPLINES" : "CANCEL";
            if (_pickerHint != null)
                _pickerHint.text = choosingSpecialization
                    ? "Add a path, then choose its abilities."
                    : "Choose a discipline to explore its forms or schools.";
        }

        private void BackInPicker()
        {
            if (string.IsNullOrEmpty(_pickerDisciplineId))
            {
                ClosePicker();
                return;
            }
            string parentId = _pickerDisciplineId;
            OpenSpecializationPicker();
            FocusPickerOption(_pickerOptions?.Q<Button>($"PickerDiscipline_{parentId}"));
        }

        private void FocusPickerOption(Button? button)
        {
            button?.schedule.Execute(() =>
            {
                if (_picker?.ClassListContains(OpenClass) == true && button.panel != null)
                    button.Focus();
            });
        }

        private void EnsureWeaponConfiguration(string parentId)
        {
            if (_model == null || _hub == null
                || _model.FindDisciplineConfiguration(parentId) != null)
            {
                return;
            }
            HubWeaponSnapshot? main = _hub.Weapons
                .Where(row => string.Equals(row.CombatDisciplineId, parentId, StringComparison.Ordinal))
                .Where(row => string.Equals(row.EquipSlot, "MAIN_HAND", StringComparison.Ordinal))
                .OrderBy(row => row.SortOrder)
                .FirstOrDefault();
            HubWeaponSnapshot? off = _hub.Weapons
                .Where(row => string.Equals(row.CombatDisciplineId, parentId, StringComparison.Ordinal))
                .Where(row => string.Equals(row.EquipSlot, "OFF_HAND", StringComparison.Ordinal))
                .OrderBy(row => row.SortOrder)
                .FirstOrDefault();
            if (main == null)
                return;
            _model.SetDisciplineConfiguration(new CombatBuildV2DisciplineConfigurationModel(
                parentId,
                main.ItemDefId,
                string.Empty,
                off?.ItemDefId ?? string.Empty,
                string.Empty));
        }

        private void ToggleFeature(CombatFeatureDefinitionV2Model feature)
        {
            bool selected = _model?.IsFeatureSelected(feature.AbilityId) == true;
            if (_model?.SetFeatureSelected(feature.AbilityId, !selected) == true)
                MarkDirtyAndRender();
            else if (!selected)
                SetStatus("The global 18-Feature capacity is full.");
        }

        private void ToggleTrait(CombatTraitDefinitionV2Model trait)
        {
            bool selected = _model?.SelectedTraitIds.Contains(
                trait.AbilityId,
                StringComparer.Ordinal) == true;
            if (_model?.SetTraitSelected(trait.AbilityId, !selected) == true)
                MarkDirtyAndRender();
            else if (!selected)
                SetStatus("The three-Trait capacity is full.");
        }

        private void MoveActive(string abilityId, int destinationIndex)
        {
            if (_model?.MoveActiveFeature(abilityId, destinationIndex) == true)
                MarkDirtyAndRender();
        }

        private void ToggleStartingDiscipline(string parentId)
        {
            if (_model == null)
                return;
            _model.SetStartingDiscipline(string.Equals(
                _model.StartingDisciplineId,
                parentId,
                StringComparison.Ordinal) ? null : parentId);
            MarkDirtyAndRender();
        }

        private void RemoveSpecialization(string specializationId)
        {
            if (_model?.RemoveSpecialization(specializationId) == true)
            {
                if (string.Equals(
                        _focusedSpecializationId,
                        specializationId,
                        StringComparison.Ordinal))
                {
                    _focusedSpecializationId = string.Empty;
                }
                MarkDirtyAndRender();
            }
        }

        private void FocusSpecialization(string specializationId)
        {
            if (string.Equals(
                    _focusedSpecializationId,
                    specializationId,
                    StringComparison.Ordinal))
            {
                return;
            }
            _focusedSpecializationId = specializationId;
            Render();
        }

        private void SaveDraft()
        {
            if (_hub == null || _model == null || _savePending || !_model.CanSubmit)
                return;
            _lastServerFailure = string.Empty;
            _savePending = _hub.SaveCombatBuild(_model.ToDraft());
            SetStatus(_savePending
                ? "Saving your loadout…"
                : "The Hub is not ready to save this build.");
            _saveButton?.SetEnabled(false);
        }

        private void OnCombatBuildSaved(bool committed, string reason)
        {
            _savePending = false;
            if (committed)
            {
                _dirty = false;
                _lastServerFailure = string.Empty;
                // The reducer commit is the authoritative save result. The
                // projected revision can arrive before or after this callback,
                // so always re-render to restore the controls and report the
                // completed save instead of leaving the UI in a waiting state.
                Render();
                SetStatus("Build saved.");
            }
            else
            {
                _lastServerFailure = reason;
                Render();
            }
        }

        private string BuildStatus()
        {
            if (!string.IsNullOrWhiteSpace(_lastServerFailure))
                return HubCombatBuildSaveStatus.Rejected(_lastServerFailure);
            if (_savePending)
                return "Saving your loadout…";
            IReadOnlyList<string> issues = _model?.LocalSubmissionIssues()
                ?? Array.Empty<string>();
            if (issues.Count > 0)
                return string.Join("  ", issues);
            return _dirty ? "Unsaved changes." : "All changes saved.";
        }

        private void MarkDirtyAndRender()
        {
            _dirty = true;
            _lastServerFailure = string.Empty;
            Render();
        }

        private void OpenPicker(string title, string subtitle)
        {
            if (_picker == null || _pickerOptions == null)
                return;
            if (_pickerTitle != null)
                _pickerTitle.text = title;
            if (_pickerSubtitle != null)
                _pickerSubtitle.text = subtitle;
            _pickerOptions.Clear();
            _picker.AddToClassList(OpenClass);
        }

        private void ClosePicker()
        {
            _picker?.RemoveFromClassList(OpenClass);
            _pickerDisciplineId = string.Empty;
        }

        private static VisualElement SectionHeading(string title, string counter)
        {
            VisualElement heading = new();
            heading.AddToClassList("ability-section-heading");
            Label titleLabel = new(title);
            titleLabel.AddToClassList("ability-section-title");
            Label counterLabel = new(counter);
            counterLabel.AddToClassList("ability-section-counter");
            heading.Add(titleLabel);
            heading.Add(counterLabel);
            return heading;
        }

        private VisualElement BuildEmptyEditor()
        {
            VisualElement empty = new();
            empty.AddToClassList("empty-editor");
            Label glyph = new("+");
            glyph.AddToClassList("empty-editor-glyph");
            empty.Add(glyph);
            Label title = new("CHOOSE A FORM OR SCHOOL");
            title.AddToClassList("empty-editor-title");
            empty.Add(title);
            Label copy = new("Your selected path will appear here for configuration.");
            copy.AddToClassList("empty-editor-copy");
            empty.Add(copy);
            Button add = new(OpenSpecializationPicker) { text = "+  ADD TO LOADOUT" };
            add.AddToClassList("empty-editor-action");
            empty.Add(add);
            return empty;
        }

        private static VisualElement BuildWaitingState(string message)
        {
            Label waiting = new(message);
            waiting.AddToClassList("waiting-state");
            return waiting;
        }

        private void BuildSaveSummary(
            CombatBuildV2CatalogModel catalog,
            CombatBuildV2ContractModel contract)
        {
            for (int index = 0; index < _model!.SelectedSpecializationIds.Count; index++)
            {
                string specializationId = _model.SelectedSpecializationIds[index];
                CombatSpecializationDefinitionV2Model? definition =
                    catalog.FindSpecialization(specializationId);
                if (definition == null)
                    continue;
                int selectedCount = _model.FeaturePickerOptions(specializationId)
                    .Count(row => _model.IsFeatureSelected(row.AbilityId));
                VisualElement row = new();
                row.AddToClassList("save-summary-row");
                Label slot = new((index + 1).ToString());
                slot.AddToClassList("save-summary-slot");
                row.Add(slot);
                VisualElement icon = new() { pickingMode = PickingMode.Ignore };
                icon.AddToClassList("save-summary-icon");
                ApplyIcon(icon, ResolveSpecializationIcon(specializationId));
                row.Add(icon);
                VisualElement copy = new();
                copy.AddToClassList("save-summary-copy");
                Label name = new(definition.DisplayName.ToUpperInvariant());
                name.AddToClassList("save-summary-name");
                copy.Add(name);
                Label parent = new(DisciplineDisplayName(definition.CombatDisciplineId));
                parent.AddToClassList("save-summary-parent");
                copy.Add(parent);
                row.Add(copy);
                Label features = new($"{selectedCount} FEATURES");
                features.AddToClassList("save-summary-count");
                row.Add(features);
                _saveSummary!.Add(row);
            }

            VisualElement totals = new();
            totals.AddToClassList("save-summary-totals");
            totals.Add(SummaryMetric(
                "FORMS · SCHOOLS",
                $"{_model.SelectedSpecializationIds.Count} / {contract.MaximumSelectedSpecializations}"));
            totals.Add(SummaryMetric("FEATURES", _model.FeatureCapacityText.Replace(" FEATURES", string.Empty)));
            totals.Add(SummaryMetric("TRAITS", _model.TraitCapacityText.Replace(" TRAITS", string.Empty)));
            _saveSummary!.Add(totals);
        }

        private static VisualElement SummaryMetric(string label, string value)
        {
            VisualElement metric = new();
            metric.AddToClassList("save-metric");
            Label metricLabel = new(label);
            metricLabel.AddToClassList("save-metric-label");
            metric.Add(metricLabel);
            Label metricValue = new(value);
            metricValue.AddToClassList("save-metric-value");
            metric.Add(metricValue);
            return metric;
        }

        private static void MarkLastChild(VisualElement parent)
        {
            if (parent.childCount > 0)
                parent[parent.childCount - 1].AddToClassList("is-last");
        }

        private static Button BuildPickerOption(string title, Sprite? icon, Color accent)
        {
            Button option = new();
            option.AddToClassList("picker-option");
            VisualElement crest = new() { pickingMode = PickingMode.Ignore };
            crest.AddToClassList("picker-crest");
            VisualElement aura = new() { pickingMode = PickingMode.Ignore };
            aura.AddToClassList("picker-crest-aura");
            aura.style.backgroundColor = new Color(accent.r, accent.g, accent.b, 0.12f);
            ApplyBorderColor(aura, new Color(accent.r, accent.g, accent.b, 0.55f));
            crest.Add(aura);
            VisualElement iconElement = new() { pickingMode = PickingMode.Ignore };
            iconElement.AddToClassList("picker-option-icon");
            ApplyIcon(iconElement, icon);
            crest.Add(iconElement);
            option.Add(crest);
            Label name = new(title.ToUpperInvariant()) { pickingMode = PickingMode.Ignore };
            name.AddToClassList("picker-option-name");
            option.Add(name);
            return option;
        }

        private void SetAllocation(string active, string total, string disciplines)
        {
            if (_activeAllocation != null)
                _activeAllocation.text = active;
            if (_totalAllocation != null)
                _totalAllocation.text = total;
            if (_disciplineAllocation != null)
                _disciplineAllocation.text = disciplines;
        }

        private void SetStatus(string message)
        {
            if (_saveStatus != null)
                _saveStatus.text = message;
        }

        private static void ApplyIcon(VisualElement element, Sprite? sprite)
        {
            element.style.backgroundImage = sprite == null
                ? StyleKeyword.None
                : new StyleBackground(sprite);
        }

        private static void ApplyBorderColor(VisualElement element, Color color)
        {
            element.style.borderTopColor = color;
            element.style.borderRightColor = color;
            element.style.borderBottomColor = color;
            element.style.borderLeftColor = color;
        }

        private static string DisciplineDisplayName(string disciplineId)
            => WireIdentifier.Normalize(disciplineId) switch
            {
                "DAGGERS" => "DAGGERS",
                "TWO_HANDED_SWORD" => "GREATSWORD",
                "SWORD_AND_SHIELD" => "SHIELD",
                "ARCHER_BOW" => "BOW",
                "STAFF" => "SPELLCASTING",
                string normalized => normalized.Replace('_', ' '),
            };

        internal static Sprite? ResolveSpecializationIcon(string specializationId)
            => ActionIconResolver.Resolve(
                "SPECIALIZATION",
                WireIdentifier.Normalize(specializationId));

        private static string PickerDisciplineName(string disciplineId)
            => disciplineId == "SWORD_AND_SHIELD" ? "SWORD & SHIELD" : DisciplineDisplayName(disciplineId);

        private static Sprite? ResolvePickerDisciplineIcon(string disciplineId)
            => disciplineId switch
            {
                "DAGGERS" => ItemIconResolver.Resolve("training_dagger_pair"),
                "TWO_HANDED_SWORD" => ItemIconResolver.Resolve("training_two_hand_sword"),
                "SWORD_AND_SHIELD" => ItemIconResolver.Resolve("training_sword_and_shield"),
                "ARCHER_BOW" => ItemIconResolver.Resolve("training_bow"),
                "STAFF" => ActionIconResolver.Resolve("COMBAT_DISCIPLINE_SWITCH", "ARCANA"),
                _ => null,
            };

        internal static Color DisciplineColor(string disciplineId)
            => WireIdentifier.Normalize(disciplineId) switch
            {
                "DAGGERS" => new Color32(159, 120, 194, 255),
                "TWO_HANDED_SWORD" => new Color32(213, 161, 72, 255),
                "SWORD_AND_SHIELD" => new Color32(216, 179, 90, 255),
                "ARCHER_BOW" => new Color32(111, 159, 105, 255),
                "STAFF" => new Color32(111, 131, 196, 255),
                _ => new Color32(217, 181, 106, 255),
            };
    }
}
