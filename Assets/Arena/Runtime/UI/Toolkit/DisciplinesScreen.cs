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
        private ScrollView? _cards;
        private VisualElement? _picker;
        private ScrollView? _pickerOptions;
        private Label? _pickerTitle;
        private Label? _pickerSubtitle;
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
                ClosePicker();
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

            _cards = _root.Q<ScrollView>("DisciplineCards");
            _picker = _root.Q<VisualElement>("PickerOverlay");
            _pickerOptions = _root.Q<ScrollView>("PickerOptions");
            _pickerTitle = _root.Q<Label>("PickerTitle");
            _pickerSubtitle = _root.Q<Label>("PickerSubtitle");
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
                    _model = null;
                Render();
                return;
            }
            if (!force && _model != null)
                return;

            _model = new CombatBuildV2EditorModel(build, catalog, contract);
            _loadedRevision = build.Revision;
            _dirty = false;
            _savePending = false;
            _lastServerFailure = string.Empty;
            Render();
        }

        private void Render()
        {
            if (_cards == null)
                return;
            _cards.Clear();
            if (_model == null || _hub?.CombatBuildContract == null || _hub.CombatBuildCatalog == null)
            {
                SetAllocation("—", "—", "—");
                SetStatus("Waiting for the Combat Build v2 catalog and your saved build.");
                _saveButton?.SetEnabled(false);
                return;
            }

            CombatBuildV2ContractModel contract = _hub.CombatBuildContract;
            CombatBuildV2CatalogModel catalog = _hub.CombatBuildCatalog;
            SetAllocation(
                $"{_model.SelectedActiveCount} ACTIVE",
                _model.FeatureCapacityText,
                $"{_model.SelectedSpecializationIds.Count} / {contract.MaximumSelectedSpecializations} FORMS · SCHOOLS");

            foreach (string specializationId in _model.SelectedSpecializationIds)
            {
                CombatSpecializationDefinitionV2Model? definition =
                    catalog.FindSpecialization(specializationId);
                if (definition != null)
                    _cards.Add(BuildSpecializationCard(definition));
            }
            _cards.Add(BuildTraitCard(catalog));
            if (_model.SelectedSpecializationIds.Count < contract.MaximumSelectedSpecializations)
                _cards.Add(BuildAddSpecializationCard());

            _saveButton?.SetEnabled(_hub.IsReady && !_savePending && _model.CanSubmit);
            SetStatus(BuildStatus());
        }

        private VisualElement BuildSpecializationCard(
            CombatSpecializationDefinitionV2Model definition)
        {
            VisualElement card = new() { name = $"SpecializationCard_{definition.SpecializationId}" };
            card.AddToClassList("discipline-card");
            card.style.borderTopColor = DisciplineColor(definition.CombatDisciplineId);

            VisualElement heading = new();
            heading.AddToClassList("discipline-card-heading");
            VisualElement copy = new();
            copy.AddToClassList("discipline-copy");
            string kind = definition.SpecializationKind == CombatSpecializationKindV2.School
                ? "SPELLCASTING SCHOOL"
                : "WEAPON FORM";
            Label kicker = new($"{kind} · {definition.CombatDisciplineId}");
            kicker.AddToClassList("card-kicker");
            Label name = new(definition.DisplayName.ToUpperInvariant());
            name.AddToClassList("discipline-name");
            copy.Add(kicker);
            copy.Add(name);
            heading.Add(copy);

            VisualElement controls = new();
            controls.AddToClassList("discipline-controls");
            bool starts = string.Equals(
                _model!.StartingDisciplineId,
                definition.CombatDisciplineId,
                StringComparison.Ordinal);
            Button start = new(() => ToggleStartingDiscipline(definition.CombatDisciplineId))
            {
                text = starts ? "◆ STARTING" : "SET STARTING",
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
                card.Add(SectionHeading("BAR ORDER", "GLOBAL SPELL · MERGED TECHNIQUE"));
                for (int index = 0; index < selectedActive.Length; index++)
                {
                    int targetIndex = index;
                    string abilityId = selectedActive[index];
                    VisualElement orderRow = new();
                    orderRow.AddToClassList("school-row");
                    orderRow.Add(new Label(
                        _hub!.CombatBuildCatalog!.FindFeature(abilityId)?.DisplayName ?? abilityId));
                    Button up = new(() => MoveActive(abilityId, targetIndex - 1)) { text = "↑" };
                    up.SetEnabled(index > 0);
                    Button down = new(() => MoveActive(abilityId, targetIndex + 1)) { text = "↓" };
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
                text = feature.DisplayName.ToUpperInvariant(),
                tooltip = string.IsNullOrWhiteSpace(feature.ResourceKind) || feature.ResourceCost <= 0f
                    ? kind
                    : $"{kind} · {feature.ResourceCost:0.#} {feature.ResourceKind}",
            };
            button.AddToClassList("ability-cell");
            button.EnableInClassList(SelectedClass, selected);
            button.EnableInClassList("is-filled", selected);
            ApplyIcon(button, ActionIconResolver.Resolve(ActionKinds.Ability, feature.AbilityId));
            return button;
        }

        private VisualElement BuildTraitCard(CombatBuildV2CatalogModel catalog)
        {
            VisualElement card = new();
            card.AddToClassList("discipline-card");
            card.Add(SectionHeading("CHARACTER TRAITS", _model!.TraitCapacityText));
            VisualElement row = new();
            row.AddToClassList("school-row");
            foreach (CombatTraitDefinitionV2Model trait in catalog.Traits
                         .OrderBy(value => value.SortOrder))
            {
                bool selected = _model.SelectedTraitIds.Contains(
                    trait.AbilityId,
                    StringComparer.Ordinal);
                Button button = new(() => ToggleTrait(trait))
                {
                    text = trait.DisplayName.ToUpperInvariant(),
                    tooltip = trait.AbilityId == "MASTERY"
                        ? "10% bonus outgoing damage while the build uses one parent Discipline."
                        : trait.AbilityId,
                };
                button.AddToClassList("school-button");
                button.EnableInClassList(SelectedClass, selected);
                row.Add(button);
            }
            card.Add(row);
            return card;
        }

        private VisualElement BuildAddSpecializationCard()
        {
            Button add = new(OpenSpecializationPicker) { text = "+" };
            add.AddToClassList("add-discipline-card");
            Label title = new("ADD FORM OR SCHOOL") { pickingMode = PickingMode.Ignore };
            title.AddToClassList("add-discipline-title");
            add.Add(title);
            add.Add(new Label("Up to three top-level choices; repeated parent weapons share one bar.")
            {
                pickingMode = PickingMode.Ignore,
            });
            return add;
        }

        private void OpenSpecializationPicker()
        {
            if (_model == null || _pickerOptions == null)
                return;
            OpenPicker("ADD A FORM OR SCHOOL", "Each selection consumes one of the three top-level slots.");
            foreach (CombatSpecializationDefinitionV2Model option in _model.SpecializationPickerOptions())
            {
                string meta = option.SpecializationKind == CombatSpecializationKindV2.School
                    ? $"SCHOOL · {option.CombatDisciplineId}"
                    : $"FORM · {option.CombatDisciplineId}";
                Button button = BuildPickerOption(
                    option.DisplayName,
                    meta,
                    ResolveDisciplineIcon(option.CombatDisciplineId));
                button.clicked += () =>
                {
                    if (_model.AddSpecialization(option.SpecializationId))
                    {
                        EnsureWeaponConfiguration(option.CombatDisciplineId);
                        MarkDirtyAndRender();
                    }
                    ClosePicker();
                };
                _pickerOptions.Add(button);
            }
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
                MarkDirtyAndRender();
        }

        private void SaveDraft()
        {
            if (_hub == null || _model == null || _savePending || !_model.CanSubmit)
                return;
            _lastServerFailure = string.Empty;
            _savePending = _hub.SaveCombatBuild(_model.ToDraft());
            SetStatus(_savePending
                ? "Saving the complete Combat Build v2 draft…"
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
                return "Saving the complete Combat Build v2 draft…";
            IReadOnlyList<string> issues = _model?.LocalSubmissionIssues()
                ?? Array.Empty<string>();
            if (issues.Count > 0)
                return string.Join("  ", issues);
            return _dirty ? "Unsaved changes." : "Saved Combat Build v2.";
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

        private void ClosePicker() => _picker?.RemoveFromClassList(OpenClass);

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

        private static Button BuildPickerOption(string title, string meta, Sprite? icon)
        {
            Button option = new();
            option.AddToClassList("picker-option");
            VisualElement iconElement = new() { pickingMode = PickingMode.Ignore };
            iconElement.AddToClassList("picker-option-icon");
            ApplyIcon(iconElement, icon);
            option.Add(iconElement);
            VisualElement copy = new() { pickingMode = PickingMode.Ignore };
            copy.AddToClassList("picker-option-copy");
            copy.Add(new Label(title.ToUpperInvariant()));
            copy.Add(new Label(meta));
            option.Add(copy);
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

        internal static Sprite? ResolveDisciplineIcon(string disciplineId)
            => ActionIconResolver.Resolve(
                ActionKinds.CombatDisciplineSwitch,
                WireIdentifier.Normalize(disciplineId));

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
