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
    /// Canonical combat-build editor. The screen edits the replicated DTO and
    /// submits one whole draft; the Hub reducer remains the validation authority.
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
        private HubCombatBuildEditorModel? _model;
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
            if (IsPickerOpen())
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
            {
                Debug.LogError("DisciplinesScreen: Disciplines.uxml is missing DisciplinesScreen.");
                return;
            }

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

            HubCombatBuildDraft? build = _hub?.CombatBuild;
            if (build != null && build.Revision != _loadedRevision)
                ReloadFromHub(force: true);
            else if (_model == null)
                ReloadFromHub(force: false);
            else
                Render();
        }

        private void ReloadFromHub(bool force)
        {
            HubCombatBuildDraft? build = _hub?.CombatBuild;
            if (build == null || _hub?.CombatBuildContract == null)
            {
                if (force)
                    _model = null;
                Render();
                return;
            }
            if (!force && _model != null)
                return;

            _model = new HubCombatBuildEditorModel(build);
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
            HubCombatBuildEditorModel? model = _model;
            HubCombatBuildContractSnapshot? contract = _hub?.CombatBuildContract;
            if (model == null || contract == null || _hub == null)
            {
                SetAllocation("—", "—", "—");
                SetStatus("Waiting for the canonical combat-build catalog and your saved build.");
                if (_saveButton != null)
                    _saveButton.SetEnabled(false);
                return;
            }

            SetAllocation(
                $"{model.ActiveCount} / {contract.MaximumActiveAbilities}",
                $"{model.CombinedAbilityCount} / {contract.CombinedAbilityBudget}",
                $"{model.SelectedDisciplineIds.Count} / {contract.MaximumSelectedDisciplines}");

            foreach (string disciplineId in model.SelectedDisciplineIds)
            {
                HubDisciplineSnapshot? discipline = _hub.Disciplines.FirstOrDefault(candidate =>
                    string.Equals(candidate.Id, disciplineId, StringComparison.Ordinal));
                EditableDisciplineConfiguration? configuration = model.FindConfiguration(disciplineId);
                if (discipline != null && configuration != null)
                    _cards.Add(BuildDisciplineCard(discipline, configuration, contract));
            }

            if (model.SelectedDisciplineIds.Count < contract.MaximumSelectedDisciplines)
                _cards.Add(BuildAddDisciplineCard());

            if (_saveButton != null)
                _saveButton.SetEnabled(_hub.IsReady && !_savePending);
            SetStatus(BuildStatus(contract));
        }

        private VisualElement BuildDisciplineCard(
            HubDisciplineSnapshot discipline,
            EditableDisciplineConfiguration configuration,
            HubCombatBuildContractSnapshot contract)
        {
            Color accent = DisciplineColor(discipline.Id);
            VisualElement card = new() { name = $"DisciplineCard_{discipline.Id}" };
            card.AddToClassList("discipline-card");
            card.style.borderTopColor = accent;

            VisualElement heading = new();
            heading.AddToClassList("discipline-card-heading");
            VisualElement identity = new();
            identity.AddToClassList("discipline-identity");
            VisualElement emblem = new();
            emblem.AddToClassList("discipline-emblem");
            ApplyIcon(emblem, ResolveDisciplineIcon(discipline.Id));
            identity.Add(emblem);
            VisualElement copy = new();
            copy.AddToClassList("discipline-copy");
            Label kicker = new(discipline.Id == "STAFF" ? "MAGICAL DISCIPLINE" : "COMBAT DISCIPLINE");
            kicker.AddToClassList("card-kicker");
            Label name = new(discipline.Name.ToUpperInvariant());
            name.AddToClassList("discipline-name");
            copy.Add(kicker);
            copy.Add(name);
            identity.Add(copy);
            heading.Add(identity);

            VisualElement controls = new();
            controls.AddToClassList("discipline-controls");
            bool starts = string.Equals(
                _model?.StartingDisciplineId,
                discipline.Id,
                StringComparison.Ordinal);
            Button start = new(() => ToggleStartingDiscipline(discipline.Id))
            {
                text = starts ? "◆ STARTING" : "SET STARTING",
                tooltip = starts
                    ? "Clear the optional starting discipline."
                    : "Use this discipline when combat begins.",
            };
            start.AddToClassList("card-control");
            start.EnableInClassList(SelectedClass, starts);
            controls.Add(start);
            Button remove = new(() => RemoveDiscipline(discipline.Id)) { text = "REMOVE" };
            remove.AddToClassList("card-control");
            remove.AddToClassList("remove-control");
            remove.SetEnabled((_model?.SelectedDisciplineIds.Count ?? 0) > contract.MinimumSelectedDisciplines);
            controls.Add(remove);
            heading.Add(controls);
            card.Add(heading);

            if (string.Equals(discipline.Id, "STAFF", StringComparison.Ordinal))
                card.Add(BuildStaffSchools(configuration, contract));

            card.Add(BuildActiveBar(discipline, configuration, contract));
            card.Add(BuildPassiveBar(discipline, configuration, contract));
            return card;
        }

        private VisualElement BuildStaffSchools(
            EditableDisciplineConfiguration configuration,
            HubCombatBuildContractSnapshot contract)
        {
            VisualElement section = new();
            section.AddToClassList("school-section");
            section.Add(SectionHeading(
                "SPELL SCHOOLS",
                $"{configuration.StaffSchoolIds.Count} / {contract.MaximumStaffSchoolsWhenSelected}"));
            VisualElement row = new();
            row.AddToClassList("school-row");
            foreach (HubSpellSchoolSnapshot school in _hub!.StaffSchools)
            {
                bool selected = configuration.StaffSchoolIds.Contains(school.Id, StringComparer.Ordinal);
                Button button = new(() => ToggleStaffSchool(school))
                {
                    text = school.Name.ToUpperInvariant(),
                    tooltip = selected ? $"Remove {school.Name}." : $"Add {school.Name}.",
                };
                button.AddToClassList("school-button");
                button.EnableInClassList(SelectedClass, selected);
                row.Add(button);
            }
            section.Add(row);
            return section;
        }

        private VisualElement BuildActiveBar(
            HubDisciplineSnapshot discipline,
            EditableDisciplineConfiguration configuration,
            HubCombatBuildContractSnapshot contract)
        {
            VisualElement section = new();
            section.AddToClassList("ability-section");
            section.Add(SectionHeading(
                "ACTIVE ABILITY ACTION BAR",
                $"{configuration.ActiveAssignments.Count} ASSIGNED"));
            VisualElement grid = new();
            grid.AddToClassList("active-slot-grid");
            bool canAddActive = _model != null
                && _model.ActiveCount < contract.MaximumActiveAbilities
                && _model.CombinedAbilityCount < contract.CombinedAbilityBudget;
            IReadOnlyList<string> visibleSlotIds = SelectVisibleActiveSlotIds(
                contract.ActionSlotIds,
                configuration.ActiveAssignments.Select(assignment => assignment.ActionSlot),
                canAddActive);
            foreach (string slotId in visibleSlotIds)
            {
                HubCombatBuildActionAssignment assignment = configuration.ActiveAssignments
                    .FirstOrDefault(candidate => string.Equals(
                    candidate.ActionSlot,
                    slotId,
                    StringComparison.Ordinal));
                string? abilityId = string.IsNullOrWhiteSpace(assignment.AbilityId)
                    ? null
                    : assignment.AbilityId;
                Button cell = BuildAbilityCell(abilityId, slotId);
                cell.clicked += () => OpenAbilityPicker(
                    discipline,
                    configuration,
                    "ACTIVE",
                    slotId,
                    passiveIndex: -1,
                    abilityId);
                grid.Add(cell);
            }
            section.Add(grid);
            return section;
        }

        internal static IReadOnlyList<string> SelectVisibleActiveSlotIds(
            IEnumerable<string> actionSlotIds,
            IEnumerable<string> assignedSlotIds,
            bool includeAvailableSlot)
        {
            HashSet<string> assigned = new(assignedSlotIds, StringComparer.Ordinal);
            string? available = includeAvailableSlot
                ? actionSlotIds.FirstOrDefault(slotId => !assigned.Contains(slotId))
                : null;
            return actionSlotIds
                .Where(slotId => assigned.Contains(slotId)
                    || string.Equals(slotId, available, StringComparison.Ordinal))
                .ToArray();
        }

        private VisualElement BuildPassiveBar(
            HubDisciplineSnapshot discipline,
            EditableDisciplineConfiguration configuration,
            HubCombatBuildContractSnapshot contract)
        {
            VisualElement section = new();
            section.AddToClassList("ability-section");
            section.Add(SectionHeading(
                "PASSIVE ABILITY ACTION BAR",
                $"{configuration.PassiveAbilityIds.Count} SELECTED"));
            VisualElement row = new();
            row.AddToClassList("passive-slot-row");
            int visibleCells = Math.Min(
                contract.CombinedAbilityBudget,
                Math.Max(4, configuration.PassiveAbilityIds.Count + 1));
            for (int index = 0; index < visibleCells; index++)
            {
                int passiveIndex = index;
                string? abilityId = index < configuration.PassiveAbilityIds.Count
                    ? configuration.PassiveAbilityIds[index]
                    : null;
                Button cell = BuildAbilityCell(abilityId, $"PASSIVE {index + 1}");
                cell.clicked += () => OpenAbilityPicker(
                    discipline,
                    configuration,
                    "PASSIVE",
                    actionSlot: null,
                    passiveIndex,
                    abilityId);
                row.Add(cell);
            }
            section.Add(row);
            return section;
        }

        private VisualElement BuildAddDisciplineCard()
        {
            Button add = new(OpenDisciplinePicker) { name = "AddDiscipline", text = "+" };
            add.AddToClassList("add-discipline-card");
            Label title = new("ADD DISCIPLINE") { pickingMode = PickingMode.Ignore };
            title.AddToClassList("add-discipline-title");
            Label copy = new("Choose another combat discipline for this build.")
            {
                pickingMode = PickingMode.Ignore,
            };
            copy.AddToClassList("add-discipline-copy");
            add.Add(title);
            add.Add(copy);
            return add;
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

        private Button BuildAbilityCell(string? abilityId, string slotLabel)
        {
            HubAbilitySnapshot? ability = FindAbility(abilityId);
            Button cell = new()
            {
                text = ability == null ? "+" : string.Empty,
                tooltip = ability == null
                    ? $"Assign {slotLabel}."
                    : $"{ability.Name}\n{ability.Description}\n{slotLabel}",
            };
            cell.AddToClassList("ability-cell");
            cell.EnableInClassList("is-filled", ability != null);
            if (ability != null)
                ApplyIcon(cell, ActionIconResolver.Resolve(ActionKinds.Ability, ability.Id));
            return cell;
        }

        private void OpenDisciplinePicker()
        {
            if (_model == null || _hub == null || _pickerOptions == null)
                return;

            OpenPicker("ADD A DISCIPLINE", "Dormant configurations restore their weapons, schools, and assigned abilities.");
            foreach (HubDisciplineSnapshot discipline in _hub.Disciplines.Where(candidate =>
                         !_model.IsSelected(candidate.Id)))
            {
                Button option = BuildPickerOption(
                    discipline.Name,
                    discipline.Id == "STAFF" ? "Choose 1–3 schools after adding." : "Weapon discipline",
                    ResolveDisciplineIcon(discipline.Id));
                option.clicked += () =>
                {
                    _model.AddDiscipline(discipline);
                    MarkDirtyAndRender();
                    ClosePicker();
                };
                _pickerOptions.Add(option);
            }
        }

        private void OpenAbilityPicker(
            HubDisciplineSnapshot discipline,
            EditableDisciplineConfiguration configuration,
            string selectionKind,
            string? actionSlot,
            int passiveIndex,
            string? currentAbilityId)
        {
            if (_hub == null || _model == null || _pickerOptions == null)
                return;

            string destination = selectionKind == "ACTIVE"
                ? actionSlot ?? "active slot"
                : $"passive position {passiveIndex + 1}";
            OpenPicker(
                $"{discipline.Name.ToUpperInvariant()} · {selectionKind}",
                $"Choose an ability for {destination}. The Hub validates the complete draft when saved.");

            if (!string.IsNullOrWhiteSpace(currentAbilityId))
            {
                Button clear = BuildPickerOption("Clear slot", destination, null);
                clear.AddToClassList("picker-option--clear");
                clear.clicked += () =>
                {
                    AssignAbility(discipline.Id, selectionKind, actionSlot, passiveIndex, null);
                    ClosePicker();
                };
                _pickerOptions.Add(clear);
            }

            HashSet<string> selectedSchools = new(configuration.StaffSchoolIds, StringComparer.Ordinal);
            IEnumerable<HubAbilitySnapshot> choices = _hub.Abilities.Where(ability =>
                string.Equals(ability.CombatDisciplineId, discipline.Id, StringComparison.Ordinal)
                && string.Equals(ability.SelectionKind, selectionKind, StringComparison.Ordinal)
                && (discipline.Id != "STAFF"
                    || ability.SpellSchoolId == null
                    || selectedSchools.Contains(ability.SpellSchoolId))
                && (!_model.ContainsAbility(ability.Id, currentAbilityId)
                    || string.Equals(ability.Id, currentAbilityId, StringComparison.Ordinal)));

            foreach (HubAbilitySnapshot ability in choices)
            {
                string meta = ability.SpellSchoolId == null
                    ? AbilityMeta(ability)
                    : $"{SchoolName(ability.SpellSchoolId)} · {AbilityMeta(ability)}";
                Button option = BuildPickerOption(
                    ability.Name,
                    meta,
                    ActionIconResolver.Resolve(ActionKinds.Ability, ability.Id));
                option.tooltip = ability.Description;
                option.EnableInClassList(SelectedClass, string.Equals(
                    ability.Id,
                    currentAbilityId,
                    StringComparison.Ordinal));
                option.clicked += () =>
                {
                    AssignAbility(discipline.Id, selectionKind, actionSlot, passiveIndex, ability.Id);
                    ClosePicker();
                };
                _pickerOptions.Add(option);
            }

            if (!_pickerOptions.Children().Any())
            {
                Label empty = new("No eligible abilities are available. For Staff, select a spell school first.");
                empty.AddToClassList("picker-empty");
                _pickerOptions.Add(empty);
            }
        }

        private void AssignAbility(
            string disciplineId,
            string selectionKind,
            string? actionSlot,
            int passiveIndex,
            string? abilityId)
        {
            if (_model == null)
                return;
            if (selectionKind == "ACTIVE" && actionSlot != null)
                _model.AssignActiveAbility(disciplineId, actionSlot, abilityId);
            else if (selectionKind == "PASSIVE")
                _model.AssignPassiveAbility(disciplineId, passiveIndex, abilityId);
            MarkDirtyAndRender();
        }

        private void ToggleStartingDiscipline(string disciplineId)
        {
            if (_model == null)
                return;
            _model.SetStartingDiscipline(string.Equals(
                _model.StartingDisciplineId,
                disciplineId,
                StringComparison.Ordinal)
                ? null
                : disciplineId);
            MarkDirtyAndRender();
        }

        private void RemoveDiscipline(string disciplineId)
        {
            if (_model?.RemoveDiscipline(disciplineId) != true)
                return;
            MarkDirtyAndRender();
        }

        private void ToggleStaffSchool(HubSpellSchoolSnapshot school)
        {
            HubCombatBuildContractSnapshot? contract = _hub?.CombatBuildContract;
            EditableDisciplineConfiguration? staff = _model?.FindConfiguration("STAFF");
            if (_model == null || contract == null || staff == null)
                return;

            bool selected = staff.StaffSchoolIds.Contains(school.Id, StringComparer.Ordinal);
            if (!selected && staff.StaffSchoolIds.Count >= contract.MaximumStaffSchoolsWhenSelected)
            {
                SetStatus($"Select at most {contract.MaximumStaffSchoolsWhenSelected} Staff schools.");
                return;
            }
            if (selected && StaffSchoolHasAssignedAbility(staff, school.Id))
            {
                SetStatus($"Clear assigned {school.Name} abilities before removing that school.");
                return;
            }

            _model.SetStaffSchoolSelected(school.Id, !selected);
            MarkDirtyAndRender();
        }

        private bool StaffSchoolHasAssignedAbility(
            EditableDisciplineConfiguration staff,
            string schoolId)
        {
            HashSet<string> assigned = new(
                staff.ActiveAssignments.Select(value => value.AbilityId)
                    .Concat(staff.PassiveAbilityIds),
                StringComparer.Ordinal);
            return _hub!.Abilities.Any(ability =>
                assigned.Contains(ability.Id)
                && string.Equals(ability.SpellSchoolId, schoolId, StringComparison.Ordinal));
        }

        private void SaveDraft()
        {
            if (_hub == null || _model == null || _savePending)
                return;

            _lastServerFailure = string.Empty;
            _savePending = _hub.SaveCombatBuild(_model.ToDraft());
            if (!_savePending)
                SetStatus("The Hub is not ready to save this build.");
            else
            {
                SetStatus("Saving the complete combat build…");
                _saveButton?.SetEnabled(false);
            }
        }

        private void OnCombatBuildSaved(bool committed, string reason)
        {
            _savePending = false;
            if (committed)
            {
                _dirty = false;
                _lastServerFailure = string.Empty;
                SetStatus("Build committed. Waiting for the new revision…");
            }
            else
            {
                // Preserve the reducer failure verbatim, including its stable
                // COMBAT_BUILD_* code. This screen never translates validation.
                _lastServerFailure = reason;
                Render();
            }
        }

        private string BuildStatus(HubCombatBuildContractSnapshot contract)
        {
            if (!string.IsNullOrWhiteSpace(_lastServerFailure))
                return HubCombatBuildSaveStatus.Rejected(_lastServerFailure);
            if (_savePending)
                return "Saving the complete combat build…";
            if (_model == null)
                return "Waiting for your combat build.";

            bool selectedCountsReady = _model.SelectedDisciplineIds.All(disciplineId =>
            {
                EditableDisciplineConfiguration? configuration = _model.FindConfiguration(disciplineId);
                return configuration != null
                       && configuration.ActiveAssignments.Count + configuration.PassiveAbilityIds.Count
                       >= contract.MinimumCountedAbilitiesPerSelectedDiscipline;
            });
            bool staffReady = !_model.IsSelected("STAFF")
                              || (_model.FindConfiguration("STAFF")?.StaffSchoolIds.Count ?? 0)
                              >= contract.MinimumStaffSchoolsWhenSelected;
            if (!selectedCountsReady || !staffReady)
                return "Draft incomplete — save to receive the Hub's authoritative validation result.";
            return _dirty ? "Unsaved changes. The Hub will validate the whole draft." : "Saved combat build.";
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
            _pickerTitle!.text = title;
            _pickerSubtitle!.text = subtitle;
            _pickerOptions.Clear();
            _picker.AddToClassList(OpenClass);
        }

        private void ClosePicker() => _picker?.RemoveFromClassList(OpenClass);

        private bool IsPickerOpen() => _picker?.ClassListContains(OpenClass) == true;

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
            Label titleLabel = new(title.ToUpperInvariant());
            titleLabel.AddToClassList("picker-option-title");
            Label metaLabel = new(meta);
            metaLabel.AddToClassList("picker-option-meta");
            copy.Add(titleLabel);
            copy.Add(metaLabel);
            option.Add(copy);
            return option;
        }

        private HubAbilitySnapshot? FindAbility(string? abilityId)
        {
            if (string.IsNullOrWhiteSpace(abilityId))
                return null;
            return _hub?.Abilities.FirstOrDefault(ability => string.Equals(
                ability.Id,
                abilityId,
                StringComparison.Ordinal));
        }

        private string SchoolName(string schoolId)
            => _hub?.StaffSchools.FirstOrDefault(school => string.Equals(
                school.Id,
                schoolId,
                StringComparison.Ordinal))?.Name ?? schoolId;

        private static string AbilityMeta(HubAbilitySnapshot ability)
        {
            if (string.IsNullOrWhiteSpace(ability.Resource) || ability.Cost <= 0f)
                return ability.SelectionKind;
            return $"{ability.SelectionKind} · {ability.Cost:0.#} {ability.Resource}";
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
        {
            return ActionIconResolver.Resolve(
                ActionKinds.CombatDisciplineSwitch,
                WireIdentifier.Normalize(disciplineId));
        }

        internal static Color DisciplineColor(string disciplineId)
        {
            return WireIdentifier.Normalize(disciplineId) switch
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
}
