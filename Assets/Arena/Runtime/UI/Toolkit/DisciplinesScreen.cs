#nullable enable

using Arena.Combat;
using Arena.Network;
using SpacetimeDB;
using SpacetimeDB.Types;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace Arena.UI
{
    /// <summary>
    /// UI Toolkit translation of docs/ui-prototypes/disciplines. The screen
    /// reads replicated discipline/ability catalogs and saves the selected
    /// primary discipline, secondary disciplines, and action-bar abilities
    /// authoritatively. Stat allocation remains provisional for this screen.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class DisciplinesScreen : MonoBehaviour, IEscapeCloseable
    {
        private const string RuntimeObjectName = "DisciplinesScreenRuntime";
        private const string OpenClass = "is-open";
        private const float CatalogRefreshInterval = 0.5f;

        private sealed class DisciplineView
        {
            public string Id = string.Empty;
            public string Name = string.Empty;
            public string Kind = string.Empty;
            public string Description = string.Empty;
            public uint SortOrder;
            public Color Color;
            public Sprite? Icon;
            public readonly List<AbilityView> Abilities = new();
        }

        private sealed class AbilityView
        {
            public string Id = string.Empty;
            public string Name = string.Empty;
            public string Resource = string.Empty;
            public string Description = string.Empty;
            public float Cost;
            public uint SortOrder;
            public bool IsPassive;
            public Sprite? Icon;
        }

        private sealed class StatView
        {
            public StatView(string id, string name, string glyph, int initialValue)
            {
                Id = id;
                Name = name;
                Glyph = glyph;
                InitialValue = initialValue;
            }

            public string Id { get; }
            public string Name { get; }
            public string Glyph { get; }
            public int InitialValue { get; }
        }

        private static readonly StatView[] StatDefinitions =
        {
            new("MIGHT", "Might", "M", 6),
            new("INSIGHT", "Insight", "◈", 5),
            new("FINESSE", "Finesse", "⌁", 5),
            new("QUICKNESS", "Quickness", "↯", 4),
            new("FORTITUDE", "Fortitude", "✦", 5),
        };

        private readonly List<DisciplineView> _disciplines = new();
        private readonly Dictionary<string, HashSet<string>> _selectedAbilities =
            new(StringComparer.Ordinal);
        private readonly List<string> _secondaryIds = new();
        private readonly Dictionary<string, int> _stats = new(StringComparer.Ordinal);

        private PanelSettings? _panelSettings;
        private VisualElement? _root;
        private VisualElement? _primaryCard;
        private VisualElement? _primaryIcon;
        private Label? _primaryName;
        private Label? _primaryDescription;
        private Label? _secondaryCount;
        private VisualElement? _secondaryDisciplineGrid;
        private Label? _secondaryHelp;
        private Label? _pointsRemaining;
        private VisualElement? _pointRuleFill;
        private VisualElement? _statList;
        private Label? _pointsAllocated;
        private Label? _catalogSource;
        private Label? _primaryAbilityHeading;
        private Label? _primaryAbilityCounter;
        private ScrollView? _primaryAbilityGrid;
        private Label? _secondaryAbilityCounter;
        private VisualElement? _secondaryAbilityGroups;
        private VisualElement? _summaryPrimaryIcon;
        private Label? _summaryPrimaryName;
        private Label? _summaryPrimaryKind;
        private VisualElement? _summarySecondaryList;
        private VisualElement? _requirementList;
        private Label? _summaryPrimaryAbilities;
        private Label? _summarySecondaryAbilities;
        private Label? _summaryDisciplines;
        private Label? _summaryPoints;
        private Label? _saveStatus;
        private Button? _saveButton;
        private VisualElement? _tooltip;
        private Label? _tooltipName;
        private Label? _tooltipMeta;
        private Label? _tooltipDescription;
        private Label? _toast;

        private string _primaryId = string.Empty;
        private string _catalogSignature = string.Empty;
        private string _authoritativeLoadoutSignature = string.Empty;
        private string _savedLoadoutSnapshot = string.Empty;
        private string _pendingLoadoutSnapshot = string.Empty;
        private DbConnection? _connection;
        private bool _savePending;
        private bool _open;
        private float _nextCatalogRefresh;
        private int _toastGeneration;

        public event Action? Closed;
        public event Action? EquipmentRequested;

        public int EscapeClosePriority => 115;
        public bool IsEscapeCloseable => _open;

        public static DisciplinesScreen Ensure(Transform parent)
        {
            DisciplinesScreen? screen = FindObjectsByType<DisciplinesScreen>(FindObjectsInactive.Include)
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
            foreach (StatView definition in StatDefinitions)
                _stats[definition.Id] = definition.InitialValue;

            RuntimeUiEventSystem.Ensure();
            BuildUi();
        }

        private void OnEnable() => RuntimeUiEscapeRouter.Register(this);

        private void OnDisable() => RuntimeUiEscapeRouter.Unregister(this);

        private void OnDestroy()
        {
            EnsureConnection(null);
            if (_panelSettings != null)
                Destroy(_panelSettings);
        }

        private void Update()
        {
            EnsureConnection(NetworkManager.Instance?.Conn);
            if (!_open || Time.unscaledTime < _nextCatalogRefresh)
                return;

            _nextCatalogRefresh = Time.unscaledTime + CatalogRefreshInterval;
            RefreshCatalog();
        }

        public void Open()
        {
            if (_root == null)
                return;

            _open = true;
            if (_panelSettings != null)
                _panelSettings.sortingOrder = RuntimeUiLayer.NextSortingOrder();
            _root.AddToClassList(OpenClass);
            _nextCatalogRefresh = 0f;
            EnsureConnection(NetworkManager.Instance?.Conn);
            RefreshCatalog(forceRender: true);
        }

        public void Close()
        {
            if (!_open)
                return;

            _open = false;
            HideTooltip();
            _root?.RemoveFromClassList(OpenClass);
            Closed?.Invoke();
        }

        public bool TryCloseForEscape()
        {
            if (!_open)
                return false;

            Close();
            return true;
        }

        private void BuildUi()
        {
            UIDocument document = ArenaPanel.CreateDocument(gameObject, "UI/Toolkit/Disciplines", 21f);
            _panelSettings = document.panelSettings;
            _root = document.rootVisualElement.Q<VisualElement>("DisciplinesScreen");
            if (_root == null)
            {
                Debug.LogError("DisciplinesScreen: Disciplines.uxml is missing DisciplinesScreen.");
                return;
            }

            _primaryCard = _root.Q<VisualElement>("PrimaryCard");
            _primaryIcon = _root.Q<VisualElement>("PrimaryIcon");
            _primaryName = _root.Q<Label>("PrimaryName");
            _primaryDescription = _root.Q<Label>("PrimaryDescription");
            _secondaryCount = _root.Q<Label>("SecondaryCount");
            _secondaryDisciplineGrid = _root.Q<VisualElement>("SecondaryDisciplineGrid");
            _secondaryHelp = _root.Q<Label>("SecondaryHelp");
            _pointsRemaining = _root.Q<Label>("PointsRemaining");
            _pointRuleFill = _root.Q<VisualElement>("PointRuleFill");
            _statList = _root.Q<VisualElement>("StatList");
            _pointsAllocated = _root.Q<Label>("PointsAllocated");
            _catalogSource = _root.Q<Label>("CatalogSource");
            _primaryAbilityHeading = _root.Q<Label>("PrimaryAbilityHeading");
            _primaryAbilityCounter = _root.Q<Label>("PrimaryAbilityCounter");
            _primaryAbilityGrid = _root.Q<ScrollView>("PrimaryAbilityGrid");
            _secondaryAbilityCounter = _root.Q<Label>("SecondaryAbilityCounter");
            _secondaryAbilityGroups = _root.Q<VisualElement>("SecondaryAbilityGroups");
            _summaryPrimaryIcon = _root.Q<VisualElement>("SummaryPrimaryIcon");
            _summaryPrimaryName = _root.Q<Label>("SummaryPrimaryName");
            _summaryPrimaryKind = _root.Q<Label>("SummaryPrimaryKind");
            _summarySecondaryList = _root.Q<VisualElement>("SummarySecondaryList");
            _requirementList = _root.Q<VisualElement>("RequirementList");
            _summaryPrimaryAbilities = _root.Q<Label>("SummaryPrimaryAbilities");
            _summarySecondaryAbilities = _root.Q<Label>("SummarySecondaryAbilities");
            _summaryDisciplines = _root.Q<Label>("SummaryDisciplines");
            _summaryPoints = _root.Q<Label>("SummaryPoints");
            _saveStatus = _root.Q<Label>("SaveStatus");
            _saveButton = _root.Q<Button>("SaveLoadout");
            _tooltip = _root.Q<VisualElement>("AbilityTooltip");
            _tooltipName = _root.Q<Label>("TooltipName");
            _tooltipMeta = _root.Q<Label>("TooltipMeta");
            _tooltipDescription = _root.Q<Label>("TooltipDescription");
            _toast = _root.Q<Label>("Toast");

            BindButton("PreviousPrimary", () => CyclePrimary(-1));
            BindButton("NextPrimary", () => CyclePrimary(1));
            BindButton("ResetPoints", ResetPoints);
            BindButton("SaveLoadout", SaveDraft);
            BindButton("BackButton", Close);
            BindButton("NavPlay", Close);
            BindButton("NavPlayTab", Close);
            BindButton("NavEquipment", RequestEquipment);

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

        private void EnsureConnection(DbConnection? conn)
        {
            if (ReferenceEquals(conn, _connection))
                return;

            if (_connection != null)
                _connection.Reducers.OnSaveCharacterDisciplineLoadout -= OnSaveCharacterDisciplineLoadout;

            _connection = conn;
            _savePending = false;
            _pendingLoadoutSnapshot = string.Empty;
            _catalogSignature = string.Empty;
            _authoritativeLoadoutSignature = string.Empty;
            _savedLoadoutSnapshot = string.Empty;

            if (_connection != null)
                _connection.Reducers.OnSaveCharacterDisciplineLoadout += OnSaveCharacterDisciplineLoadout;
        }

        private void RefreshCatalog(bool forceRender = false)
        {
            DbConnection? conn = NetworkManager.Instance?.Conn;
            EnsureConnection(conn);
            if (conn == null)
            {
                if (_disciplines.Count == 0)
                    RenderWaitingForCatalog();
                return;
            }

            List<CombatDisciplineCatalog> disciplineRows = conn.Db.CombatDisciplineCatalog.Iter()
                .OrderBy(row => row.SortOrder)
                .ThenBy(row => row.DisciplineId, StringComparer.Ordinal)
                .ToList();
            List<AbilityCatalog> abilityRows = conn.Db.AbilityCatalog.Iter()
                .Where(row => string.Equals(WireIdentifier.Normalize(row.ActorScope), "PLAYER", StringComparison.Ordinal))
                .Where(row => HasAbilityTag(row.AbilityTags, "ACTION_BAR_ACTION")
                    || HasAbilityTag(row.AbilityTags, "PASSIVE"))
                .OrderBy(row => row.SortOrder)
                .ThenBy(row => row.AbilityId, StringComparer.Ordinal)
                .ToList();

            CharacterDisciplineLoadout? loadout = conn.Identity.HasValue
                ? conn.Db.CharacterDisciplineLoadout.Owner.Find(conn.Identity.Value)
                : null;
            List<CharacterDisciplineAbilitySelection> selectedAbilityRows = conn.Identity.HasValue
                ? conn.Db.CharacterDisciplineAbilitySelection.Owner.Filter(conn.Identity.Value)
                    .OrderBy(row => row.SortOrder)
                    .ThenBy(row => row.AbilityId, StringComparer.Ordinal)
                    .ToList()
                : new List<CharacterDisciplineAbilitySelection>();
            string authoritativeLoadoutSignature = BuildLoadoutSnapshot(loadout, selectedAbilityRows);
            bool authoritativeLoadoutChanged = !string.Equals(
                authoritativeLoadoutSignature,
                _authoritativeLoadoutSignature,
                StringComparison.Ordinal);

            string signature = BuildCatalogSignature(
                disciplineRows,
                abilityRows,
                authoritativeLoadoutSignature);
            if (!forceRender && string.Equals(signature, _catalogSignature, StringComparison.Ordinal))
                return;

            Dictionary<string, string> descriptions = conn.Db.ActionPresentationCatalog.Iter()
                .Where(row => string.Equals(WireIdentifier.Normalize(row.PresentationKind), ActionKinds.Ability, StringComparison.Ordinal))
                .GroupBy(row => WireIdentifier.Normalize(row.PresentationId), StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First().Description ?? string.Empty, StringComparer.Ordinal);

            _disciplines.Clear();
            foreach (CombatDisciplineCatalog row in disciplineRows)
            {
                string id = WireIdentifier.Normalize(row.DisciplineId);
                DisciplineView discipline = new()
                {
                    Id = id,
                    Name = string.IsNullOrWhiteSpace(row.DisplayName) ? FormatIdentifier(id) : row.DisplayName.Trim(),
                    Kind = WireIdentifier.Normalize(row.DisciplineKind),
                    Description = DisciplineDescription(id),
                    SortOrder = row.SortOrder,
                    Color = DisciplineColor(id),
                    Icon = ResolveDisciplineIcon(id),
                };

                foreach (AbilityCatalog abilityRow in abilityRows.Where(ability =>
                             string.Equals(WireIdentifier.Normalize(ability.DisciplineId), id, StringComparison.Ordinal)))
                {
                    string abilityId = WireIdentifier.Normalize(abilityRow.AbilityId);
                    discipline.Abilities.Add(new AbilityView
                    {
                        Id = abilityId,
                        Name = string.IsNullOrWhiteSpace(abilityRow.DisplayName)
                            ? FormatIdentifier(abilityId)
                            : abilityRow.DisplayName.Trim(),
                        Resource = WireIdentifier.Normalize(abilityRow.ResourceKind),
                        Cost = abilityRow.ResourceCost,
                        SortOrder = abilityRow.SortOrder,
                        IsPassive = HasAbilityTag(abilityRow.AbilityTags, "PASSIVE"),
                        Description = descriptions.TryGetValue(abilityId, out string? description)
                            && !string.IsNullOrWhiteSpace(description)
                                ? description.Trim()
                                : "Select this ability for your saved discipline loadout.",
                        Icon = ActionIconResolver.Resolve(ActionKinds.Ability, abilityId),
                    });
                }

                _disciplines.Add(discipline);
            }

            _catalogSignature = signature;
            _authoritativeLoadoutSignature = authoritativeLoadoutSignature;
            NormalizeDraft(conn, loadout, selectedAbilityRows, authoritativeLoadoutChanged);
            RenderAll();
        }

        private static string BuildCatalogSignature(
            IEnumerable<CombatDisciplineCatalog> disciplines,
            IEnumerable<AbilityCatalog> abilities,
            string authoritativeLoadoutSignature)
        {
            StringBuilder builder = new();
            foreach (CombatDisciplineCatalog discipline in disciplines)
                builder.Append(discipline.DisciplineId).Append(':').Append(discipline.SortOrder).Append(';');
            builder.Append('|');
            foreach (AbilityCatalog ability in abilities)
                builder.Append(ability.DisciplineId).Append(':').Append(ability.AbilityId).Append(':').Append(ability.SortOrder).Append(';');
            builder.Append('|').Append(authoritativeLoadoutSignature);
            return builder.ToString();
        }

        private void NormalizeDraft(
            DbConnection conn,
            CharacterDisciplineLoadout? loadout,
            IReadOnlyList<CharacterDisciplineAbilitySelection> selectedAbilityRows,
            bool authoritativeLoadoutChanged)
        {
            foreach (DisciplineView discipline in _disciplines)
            {
                if (!_selectedAbilities.TryGetValue(discipline.Id, out HashSet<string>? selected))
                {
                    selected = new HashSet<string>(StringComparer.Ordinal);
                    _selectedAbilities[discipline.Id] = selected;
                }

                HashSet<string> available = discipline.Abilities.Select(ability => ability.Id)
                    .ToHashSet(StringComparer.Ordinal);
                selected.RemoveWhere(id => !available.Contains(id));
            }

            if (loadout != null && authoritativeLoadoutChanged)
            {
                _primaryId = WireIdentifier.Normalize(loadout.PrimaryDisciplineId);
                _secondaryIds.Clear();
                AddAuthoritativeSecondary(loadout.SecondaryDisciplineId1);
                AddAuthoritativeSecondary(loadout.SecondaryDisciplineId2);
                foreach (HashSet<string> selected in _selectedAbilities.Values)
                    selected.Clear();
                foreach (CharacterDisciplineAbilitySelection selection in selectedAbilityRows)
                {
                    string disciplineId = WireIdentifier.Normalize(selection.DisciplineId);
                    string abilityId = WireIdentifier.Normalize(selection.AbilityId);
                    if (FindDiscipline(disciplineId)?.Abilities.Any(ability => ability.Id == abilityId) == true)
                        SelectedSet(disciplineId).Add(abilityId);
                }
                _savedLoadoutSnapshot = BuildLoadoutSnapshot(loadout, selectedAbilityRows);
                _savePending = false;
                _pendingLoadoutSnapshot = string.Empty;
            }

            if (string.IsNullOrWhiteSpace(_primaryId)
                || FindDiscipline(_primaryId) is not { } current
                || !DisciplineLoadoutRules.CanBePrimary(current.Abilities.Count))
            {
                string preferred = string.Empty;
                if (conn.Identity.HasValue)
                {
                    ActiveCombatDiscipline? active = conn.Db.ActiveCombatDiscipline.Owner.Find(conn.Identity.Value);
                    preferred = WireIdentifier.Normalize(active?.DisciplineId);
                }

                DisciplineView? next = FindDiscipline(preferred);
                if (next == null || !DisciplineLoadoutRules.CanBePrimary(next.Abilities.Count))
                    next = FindDiscipline("WAR");
                if (next == null || !DisciplineLoadoutRules.CanBePrimary(next.Abilities.Count))
                    next = _disciplines.FirstOrDefault(discipline =>
                        DisciplineLoadoutRules.CanBePrimary(discipline.Abilities.Count));
                _primaryId = next?.Id ?? string.Empty;

                if (next != null && SelectedSet(next.Id).Count == 0)
                {
                    foreach (AbilityView ability in next.Abilities
                                 .Where(ability => !ability.IsPassive)
                                 .Take(DisciplineLoadoutRules.PrimaryAbilityMinimum))
                        SelectedSet(next.Id).Add(ability.Id);
                }
            }

            _secondaryIds.RemoveAll(id => id == _primaryId || FindDiscipline(id) == null);
            if (loadout != null && authoritativeLoadoutChanged && selectedAbilityRows.Count == 0)
                SeedMinimumAbilitySelections();
            if (loadout == null && _secondaryIds.Count == 0)
            {
                foreach (string preferred in new[] { "SUBTLETY", "RUIN" })
                {
                    DisciplineView? discipline = FindDiscipline(preferred);
                    if (discipline == null || discipline.Id == _primaryId || discipline.Abilities.Count == 0)
                        continue;
                    _secondaryIds.Add(discipline.Id);
                    AbilityView? first = discipline.Abilities.FirstOrDefault(ability => !ability.IsPassive);
                    if (first != null)
                        SelectedSet(discipline.Id).Add(first.Id);
                    if (_secondaryIds.Count >= DisciplineLoadoutRules.SecondaryDisciplineMaximum)
                        break;
                }
            }
        }

        private void SeedMinimumAbilitySelections()
        {
            DisciplineView? primary = FindDiscipline(_primaryId);
            if (primary != null && SelectedSet(primary.Id).Count == 0)
            {
                foreach (AbilityView ability in primary.Abilities
                             .Where(ability => !ability.IsPassive)
                             .Take(DisciplineLoadoutRules.PrimaryAbilityMinimum))
                {
                    SelectedSet(primary.Id).Add(ability.Id);
                }
            }

            foreach (string secondaryId in _secondaryIds)
            {
                DisciplineView? secondary = FindDiscipline(secondaryId);
                if (secondary == null || SelectedSet(secondaryId).Count > 0)
                    continue;
                AbilityView? first = secondary.Abilities.FirstOrDefault(ability => !ability.IsPassive);
                if (first != null)
                    SelectedSet(secondaryId).Add(first.Id);
            }
        }

        private void AddAuthoritativeSecondary(string? disciplineId)
        {
            string normalized = WireIdentifier.Normalize(disciplineId);
            if (string.IsNullOrEmpty(normalized)
                || normalized == _primaryId
                || _secondaryIds.Contains(normalized, StringComparer.Ordinal)
                || FindDiscipline(normalized) == null)
            {
                return;
            }
            _secondaryIds.Add(normalized);
        }

        private void RenderWaitingForCatalog()
        {
            if (_catalogSource != null)
                _catalogSource.text = "◆ WAITING FOR PROGRESSION CATALOG";
            if (_primaryName != null)
                _primaryName.text = "UNAVAILABLE";
            if (_primaryDescription != null)
                _primaryDescription.text = "Connect to load the canonical discipline and ability catalogs.";
            _secondaryDisciplineGrid?.Clear();
            _primaryAbilityGrid?.Clear();
            _secondaryAbilityGroups?.Clear();
            if (_saveStatus != null)
            {
                _saveStatus.text = "!  Progression catalog unavailable";
                _saveStatus.AddToClassList("is-incomplete");
            }
            _saveButton?.SetEnabled(false);
            RenderStats();
        }

        private void RenderAll()
        {
            HideTooltip();
            RenderPrimaryPicker();
            RenderSecondaryPicker();
            RenderStats();
            RenderPrimaryAbilities();
            RenderSecondaryAbilities();
            RenderSummary();
            if (_catalogSource != null)
                _catalogSource.text = "◆ LIVE PROGRESSION CATALOG";
        }

        private void RenderPrimaryPicker()
        {
            DisciplineView? discipline = FindDiscipline(_primaryId);
            if (discipline == null)
                return;

            SetBackground(_primaryIcon, discipline.Icon);
            if (_primaryName != null)
                _primaryName.text = discipline.Name.ToUpperInvariant();
            if (_primaryDescription != null)
                _primaryDescription.text = discipline.Description;
            if (_primaryCard != null)
                ApplyBorderColor(_primaryCard, discipline.Color);
        }

        private void RenderSecondaryPicker()
        {
            if (_secondaryDisciplineGrid == null)
                return;

            _secondaryDisciplineGrid.Clear();
            bool atLimit = _secondaryIds.Count >= DisciplineLoadoutRules.SecondaryDisciplineMaximum;
            foreach (DisciplineView discipline in _disciplines.Where(item => item.Id != _primaryId))
            {
                bool selected = _secondaryIds.Contains(discipline.Id);
                Button button = new();
                button.AddToClassList("discipline-option");
                button.EnableInClassList("is-selected", selected);
                button.EnableInClassList("is-disabled", atLimit && !selected);
                ApplyBorderColor(button, selected ? discipline.Color : new Color(0.42f, 0.44f, 0.48f, 0.25f));

                VisualElement icon = new() { pickingMode = PickingMode.Ignore };
                icon.AddToClassList("discipline-option-icon");
                SetBackground(icon, discipline.Icon);
                button.Add(icon);

                Label name = new(discipline.Name.ToUpperInvariant()) { pickingMode = PickingMode.Ignore };
                name.AddToClassList("discipline-option-name");
                button.Add(name);

                Label check = new("✓") { pickingMode = PickingMode.Ignore };
                check.AddToClassList("discipline-check");
                button.Add(check);
                string capturedId = discipline.Id;
                button.clicked += () => ToggleSecondary(capturedId);
                _secondaryDisciplineGrid.Add(button);
            }

            if (_secondaryCount != null)
                _secondaryCount.text = $"{_secondaryIds.Count} / {DisciplineLoadoutRules.SecondaryDisciplineMaximum} ACTIVE";
        }

        private void RenderStats()
        {
            if (_statList == null)
                return;

            _statList.Clear();
            int remaining = RemainingPoints();
            foreach (StatView definition in StatDefinitions)
            {
                VisualElement row = new();
                row.AddToClassList("stat-row");

                Label glyph = new(definition.Glyph) { pickingMode = PickingMode.Ignore };
                glyph.AddToClassList("stat-glyph");
                row.Add(glyph);

                Label name = new(definition.Name.ToUpperInvariant()) { pickingMode = PickingMode.Ignore };
                name.AddToClassList("stat-name");
                row.Add(name);

                Button minus = new(() => ChangeStat(definition.Id, -1)) { text = "−" };
                minus.AddToClassList("stat-step");
                minus.SetEnabled(_stats[definition.Id] > 0);
                row.Add(minus);

                Label value = new(_stats[definition.Id].ToString()) { pickingMode = PickingMode.Ignore };
                value.AddToClassList("stat-value");
                row.Add(value);

                Button plus = new(() => ChangeStat(definition.Id, 1)) { text = "+" };
                plus.AddToClassList("stat-step");
                plus.SetEnabled(remaining > 0);
                row.Add(plus);
                _statList.Add(row);
            }

            int allocated = AllocatedPoints();
            if (_pointsRemaining != null)
                _pointsRemaining.text = remaining.ToString();
            if (_pointsAllocated != null)
                _pointsAllocated.text = $"{allocated} / {DisciplineLoadoutRules.AbilityPointBudget} ALLOCATED";
            if (_pointRuleFill != null)
            {
                float percent = DisciplineLoadoutRules.AbilityPointBudget == 0
                    ? 0f
                    : allocated * 100f / DisciplineLoadoutRules.AbilityPointBudget;
                _pointRuleFill.style.width = new Length(percent, LengthUnit.Percent);
            }
        }

        private void RenderPrimaryAbilities()
        {
            DisciplineView? discipline = FindDiscipline(_primaryId);
            if (discipline == null || _primaryAbilityGrid == null)
                return;

            _primaryAbilityGrid.Clear();
            foreach (AbilityView ability in discipline.Abilities)
                _primaryAbilityGrid.Add(CreateAbilityTile(ability, discipline));

            int count = SelectedCount(discipline.Id);
            if (_primaryAbilityHeading != null)
                _primaryAbilityHeading.text = $"{discipline.Name.ToUpperInvariant()} · PRIMARY";
            if (_primaryAbilityCounter != null)
            {
                _primaryAbilityCounter.text = $"{count} / {DisciplineLoadoutRules.PrimaryAbilityMinimum} MIN";
                _primaryAbilityCounter.EnableInClassList(
                    "is-incomplete",
                    count < DisciplineLoadoutRules.PrimaryAbilityMinimum);
            }
        }

        private void RenderSecondaryAbilities()
        {
            if (_secondaryAbilityGroups == null)
                return;

            _secondaryAbilityGroups.Clear();
            if (_secondaryIds.Count == 0)
            {
                Label empty = new("NO SECONDARY DISCIPLINES ACTIVE\nChoose up to two supporting paths from the left panel.");
                empty.AddToClassList("secondary-empty");
                _secondaryAbilityGroups.Add(empty);
            }

            for (int index = 0; index < _secondaryIds.Count; index++)
            {
                DisciplineView? discipline = FindDiscipline(_secondaryIds[index]);
                if (discipline == null)
                    continue;

                VisualElement group = new();
                group.AddToClassList("secondary-group");
                if (index > 0)
                    group.AddToClassList("secondary-group--offset");

                VisualElement heading = new();
                heading.AddToClassList("secondary-group-heading");
                VisualElement icon = new() { pickingMode = PickingMode.Ignore };
                icon.AddToClassList("secondary-group-icon");
                SetBackground(icon, discipline.Icon);
                heading.Add(icon);

                Label name = new(discipline.Name.ToUpperInvariant()) { pickingMode = PickingMode.Ignore };
                name.AddToClassList("secondary-group-name");
                name.style.color = discipline.Color;
                heading.Add(name);

                int count = SelectedCount(discipline.Id);
                Label countLabel = new($"{count} / {DisciplineLoadoutRules.SecondaryAbilityMinimum} MIN")
                {
                    pickingMode = PickingMode.Ignore,
                };
                countLabel.AddToClassList("secondary-group-count");
                countLabel.EnableInClassList("is-incomplete", count < DisciplineLoadoutRules.SecondaryAbilityMinimum);
                heading.Add(countLabel);
                group.Add(heading);

                ScrollView grid = new(ScrollViewMode.Vertical)
                {
                    horizontalScrollerVisibility = ScrollerVisibility.Hidden,
                    verticalScrollerVisibility = ScrollerVisibility.Auto,
                };
                grid.AddToClassList("ability-grid");
                grid.AddToClassList("ability-grid--secondary");
                foreach (AbilityView ability in discipline.Abilities)
                    grid.Add(CreateAbilityTile(ability, discipline));
                group.Add(grid);
                _secondaryAbilityGroups.Add(group);
            }

            if (_secondaryAbilityCounter != null)
                _secondaryAbilityCounter.text = $"{TotalSecondaryAbilityCount()} SELECTED";
        }

        private Button CreateAbilityTile(AbilityView ability, DisciplineView discipline)
        {
            bool selected = ability.IsPassive || SelectedSet(discipline.Id).Contains(ability.Id);
            Button button = new();
            button.AddToClassList("ability-tile");
            button.EnableInClassList("is-selected", selected);
            button.EnableInClassList("is-passive", ability.IsPassive);

            VisualElement art = new() { pickingMode = PickingMode.Ignore };
            art.AddToClassList("ability-art");
            SetBackground(art, ability.Icon ?? discipline.Icon);
            button.Add(art);

            Label name = new(ability.IsPassive ? $"{ability.Name} · PASSIVE" : ability.Name)
            {
                pickingMode = PickingMode.Ignore,
            };
            name.AddToClassList("ability-name");
            button.Add(name);

            Label check = new(ability.IsPassive ? "◆" : "✓") { pickingMode = PickingMode.Ignore };
            check.AddToClassList("ability-check");
            button.Add(check);

            if (!ability.IsPassive)
            {
                string disciplineId = discipline.Id;
                string abilityId = ability.Id;
                button.clicked += () => ToggleAbility(disciplineId, abilityId);
            }
            button.RegisterCallback<PointerEnterEvent>(evt => ShowTooltip(evt.position, ability, discipline));
            button.RegisterCallback<PointerMoveEvent>(evt => MoveTooltip(evt.position));
            button.RegisterCallback<PointerLeaveEvent>(_ => HideTooltip());
            return button;
        }

        private void RenderSummary()
        {
            DisciplineView? primary = FindDiscipline(_primaryId);
            if (primary == null)
                return;

            SetBackground(_summaryPrimaryIcon, primary.Icon);
            if (_summaryPrimaryIcon != null)
                ApplyBorderColor(_summaryPrimaryIcon, primary.Color);
            if (_summaryPrimaryName != null)
                _summaryPrimaryName.text = primary.Name.ToUpperInvariant();
            if (_summaryPrimaryKind != null)
                _summaryPrimaryKind.text = primary.Kind == "WEAPON"
                    ? "WEAPON DISCIPLINE"
                    : "SPELL-SCHOOL DISCIPLINE";

            _summarySecondaryList?.Clear();
            if (_summarySecondaryList != null)
            {
                if (_secondaryIds.Count == 0)
                {
                    Label empty = new("No supporting discipline selected");
                    empty.AddToClassList("summary-secondary-empty");
                    _summarySecondaryList.Add(empty);
                }
                else
                {
                    foreach (string id in _secondaryIds)
                    {
                        DisciplineView? discipline = FindDiscipline(id);
                        if (discipline == null)
                            continue;

                        VisualElement row = new();
                        row.AddToClassList("summary-secondary-row");
                        VisualElement icon = new() { pickingMode = PickingMode.Ignore };
                        icon.AddToClassList("summary-secondary-icon");
                        SetBackground(icon, discipline.Icon);
                        row.Add(icon);
                        Label name = new(discipline.Name.ToUpperInvariant()) { pickingMode = PickingMode.Ignore };
                        name.AddToClassList("summary-secondary-name");
                        name.style.color = discipline.Color;
                        row.Add(name);
                        Label count = new($"{SelectedCount(id)} abilities") { pickingMode = PickingMode.Ignore };
                        count.AddToClassList("summary-secondary-abilities");
                        row.Add(count);
                        _summarySecondaryList.Add(row);
                    }
                }
            }

            int primaryCount = SelectedCount(primary.Id);
            List<int> secondaryCounts = _secondaryIds.Select(SelectedCount).ToList();
            bool valid = DisciplineLoadoutRules.IsValid(primaryCount, secondaryCounts);
            int validSecondaries = secondaryCounts.Count(count => count >= DisciplineLoadoutRules.SecondaryAbilityMinimum);

            _requirementList?.Clear();
            if (_requirementList != null)
            {
                _requirementList.Add(CreateRequirementRow(
                    primaryCount >= DisciplineLoadoutRules.PrimaryAbilityMinimum,
                    $"{primary.Name} primary abilities: {primaryCount} / {DisciplineLoadoutRules.PrimaryAbilityMinimum}"));
                _requirementList.Add(CreateRequirementRow(
                    validSecondaries == secondaryCounts.Count,
                    secondaryCounts.Count == 0
                        ? "Secondary disciplines are optional"
                        : $"Secondary minima met: {validSecondaries} / {secondaryCounts.Count}"));
                _requirementList.Add(CreateRequirementRow(
                    _secondaryIds.Count <= DisciplineLoadoutRules.SecondaryDisciplineMaximum,
                    $"Secondary disciplines: {_secondaryIds.Count} / {DisciplineLoadoutRules.SecondaryDisciplineMaximum} maximum"));
            }

            if (_summaryPrimaryAbilities != null)
                _summaryPrimaryAbilities.text = primaryCount.ToString();
            if (_summarySecondaryAbilities != null)
                _summarySecondaryAbilities.text = TotalSecondaryAbilityCount().ToString();
            if (_summaryDisciplines != null)
                _summaryDisciplines.text = $"{1 + _secondaryIds.Count} / 3";
            if (_summaryPoints != null)
                _summaryPoints.text = $"{AllocatedPoints()} / {DisciplineLoadoutRules.AbilityPointBudget}";

            if (_saveStatus != null)
            {
                _saveStatus.EnableInClassList("is-incomplete", !valid);
                if (primaryCount < DisciplineLoadoutRules.PrimaryAbilityMinimum)
                {
                    _saveStatus.text = $"!  Select {DisciplineLoadoutRules.PrimaryAbilityMinimum - primaryCount} more {primary.Name} abilities";
                }
                else if (validSecondaries != secondaryCounts.Count)
                {
                    _saveStatus.text = "!  Every secondary needs at least one ability";
                }
                else if (_savePending)
                {
                    _saveStatus.text = "◆  Saving discipline loadout…";
                }
                else if (!string.IsNullOrEmpty(_savedLoadoutSnapshot)
                         && string.Equals(
                             _savedLoadoutSnapshot,
                             BuildDraftLoadoutSnapshot(),
                             StringComparison.Ordinal))
                {
                    _saveStatus.text = "✓  Disciplines and abilities saved · stats are session-only";
                }
                else if (RemainingPoints() > 0)
                {
                    _saveStatus.text = $"✓  Requirements met · {RemainingPoints()} points unspent";
                }
                else
                {
                    _saveStatus.text = "✓  All requirements met";
                }
            }

            _saveButton?.SetEnabled(valid && !_savePending && _connection != null);
        }

        private static VisualElement CreateRequirementRow(bool complete, string copy)
        {
            VisualElement row = new();
            row.AddToClassList("requirement-row");
            row.EnableInClassList("is-incomplete", !complete);
            Label icon = new(complete ? "✓" : "!") { pickingMode = PickingMode.Ignore };
            icon.AddToClassList("requirement-icon");
            row.Add(icon);
            row.Add(new Label(copy) { pickingMode = PickingMode.Ignore });
            return row;
        }

        private void CyclePrimary(int direction)
        {
            List<DisciplineView> eligible = _disciplines
                .Where(discipline => DisciplineLoadoutRules.CanBePrimary(discipline.Abilities.Count))
                .ToList();
            if (eligible.Count == 0)
            {
                ShowToast("No discipline abilities are currently available.");
                return;
            }

            int index = eligible.FindIndex(discipline => discipline.Id == _primaryId);
            index = index < 0 ? 0 : (index + direction + eligible.Count) % eligible.Count;
            _primaryId = eligible[index].Id;
            _secondaryIds.Remove(_primaryId);
            RenderAll();
        }

        private void ToggleSecondary(string disciplineId)
        {
            if (_secondaryIds.Remove(disciplineId))
            {
                SetSecondaryHelp("Choose up to two. Each active secondary requires at least one ability.", warning: false);
                RenderAll();
                return;
            }

            if (!DisciplineLoadoutRules.CanAddSecondary(_secondaryIds.Count))
            {
                SetSecondaryHelp("Two secondary disciplines are already active. Remove one to change it.", warning: true);
                ShowToast("Maximum of two secondary disciplines.");
                return;
            }

            _secondaryIds.Add(disciplineId);
            SetSecondaryHelp("Choose at least one ability from the newly active discipline.", warning: SelectedCount(disciplineId) == 0);
            RenderAll();
        }

        private void ToggleAbility(string disciplineId, string abilityId)
        {
            HashSet<string> selected = SelectedSet(disciplineId);
            if (!selected.Add(abilityId))
                selected.Remove(abilityId);
            RenderAll();
        }

        private void ChangeStat(string statId, int amount)
        {
            if (amount > 0 && RemainingPoints() <= 0)
                return;
            _stats[statId] = Mathf.Max(0, _stats[statId] + amount);
            RenderStats();
            RenderSummary();
        }

        private void ResetPoints()
        {
            foreach (StatView definition in StatDefinitions)
                _stats[definition.Id] = 0;
            RenderStats();
            RenderSummary();
            ShowToast("Ability point allocations reset. 25 points remain available.");
        }

        private void SaveDraft()
        {
            DisciplineView? primary = FindDiscipline(_primaryId);
            if (primary == null)
                return;

            List<int> secondaryCounts = _secondaryIds.Select(SelectedCount).ToList();
            if (!DisciplineLoadoutRules.IsValid(SelectedCount(primary.Id), secondaryCounts))
                return;

            EnsureConnection(NetworkManager.Instance?.Conn);
            if (_connection == null || !_connection.Identity.HasValue || _savePending)
            {
                ShowToast("Connect to save your discipline loadout.");
                return;
            }

            string secondary1 = _secondaryIds.ElementAtOrDefault(0) ?? string.Empty;
            string secondary2 = _secondaryIds.ElementAtOrDefault(1) ?? string.Empty;
            List<string> selectedAbilityIds = BuildSelectedAbilityIds();
            _pendingLoadoutSnapshot = BuildLoadoutSnapshot(
                _primaryId,
                secondary1,
                secondary2,
                selectedAbilityIds);
            _savePending = true;
            RenderSummary();
            _connection.Reducers.SaveCharacterDisciplineLoadout(
                _primaryId,
                secondary1,
                secondary2,
                selectedAbilityIds);
        }

        private void OnSaveCharacterDisciplineLoadout(
            ReducerEventContext ctx,
            string primaryDisciplineId,
            string secondaryDisciplineId1,
            string secondaryDisciplineId2,
            List<string> selectedAbilityIds)
        {
            if (_connection == null
                || !_connection.Identity.HasValue
                || ctx.Event.CallerIdentity != _connection.Identity.Value
                || !_savePending
                || !string.Equals(
                    _pendingLoadoutSnapshot,
                    BuildLoadoutSnapshot(
                        primaryDisciplineId,
                        secondaryDisciplineId1,
                        secondaryDisciplineId2,
                        selectedAbilityIds),
                    StringComparison.Ordinal))
            {
                return;
            }

            _savePending = false;
            if (ctx.Event.Status is Status.Committed)
            {
                _savedLoadoutSnapshot = _pendingLoadoutSnapshot;
                _pendingLoadoutSnapshot = string.Empty;
                _catalogSignature = string.Empty;
                _nextCatalogRefresh = 0f;
                RenderSummary();
                ShowToast("Discipline loadout saved. Your abilities will carry into gameplay.");
                return;
            }

            string reason = ctx.Event.Status switch
            {
                Status.Failed(var failure) => failure,
                Status.OutOfEnergy(var _) => "server was out of reducer energy",
                _ => "server did not commit the discipline loadout",
            };
            _pendingLoadoutSnapshot = string.Empty;
            Debug.LogError($"[{nameof(DisciplinesScreen)}] Saving discipline loadout failed: {reason}");
            RenderSummary();
            ShowToast($"Could not save discipline loadout: {reason}");
        }

        private List<string> BuildSelectedAbilityIds()
        {
            List<string> selectedAbilityIds = new();
            IEnumerable<string> disciplineIds = new[] { _primaryId }.Concat(_secondaryIds);
            foreach (string disciplineId in disciplineIds)
            {
                DisciplineView? discipline = FindDiscipline(disciplineId);
                if (discipline == null)
                    continue;
                HashSet<string> selected = SelectedSet(discipline.Id);
                selectedAbilityIds.AddRange(discipline.Abilities
                    .Where(ability => !ability.IsPassive && selected.Contains(ability.Id))
                    .Select(ability => ability.Id));
            }
            return selectedAbilityIds;
        }

        private string BuildDraftLoadoutSnapshot()
        {
            return BuildLoadoutSnapshot(
                _primaryId,
                _secondaryIds.ElementAtOrDefault(0),
                _secondaryIds.ElementAtOrDefault(1),
                BuildSelectedAbilityIds());
        }

        private static string BuildLoadoutSnapshot(
            CharacterDisciplineLoadout? loadout,
            IEnumerable<CharacterDisciplineAbilitySelection> selectedAbilities)
        {
            return loadout == null
                ? "NO_AUTHORITATIVE_LOADOUT"
                : BuildLoadoutSnapshot(
                    loadout.PrimaryDisciplineId,
                    loadout.SecondaryDisciplineId1,
                    loadout.SecondaryDisciplineId2,
                    selectedAbilities
                        .OrderBy(row => row.SortOrder)
                        .ThenBy(row => row.AbilityId, StringComparer.Ordinal)
                        .Select(row => row.AbilityId));
        }

        private static string BuildLoadoutSnapshot(
            string? primaryDisciplineId,
            string? secondaryDisciplineId1,
            string? secondaryDisciplineId2,
            IEnumerable<string> selectedAbilityIds)
        {
            return string.Join(
                "|",
                WireIdentifier.Normalize(primaryDisciplineId),
                WireIdentifier.Normalize(secondaryDisciplineId1),
                WireIdentifier.Normalize(secondaryDisciplineId2),
                string.Join(",", selectedAbilityIds.Select(WireIdentifier.Normalize)));
        }

        private void SetSecondaryHelp(string copy, bool warning)
        {
            if (_secondaryHelp == null)
                return;
            _secondaryHelp.text = copy;
            _secondaryHelp.EnableInClassList("is-warning", warning);
        }

        private void ShowTooltip(Vector3 position, AbilityView ability, DisciplineView discipline)
        {
            if (_tooltip == null || _tooltipName == null || _tooltipMeta == null || _tooltipDescription == null)
                return;

            _tooltipName.text = ability.Name.ToUpperInvariant();
            _tooltipMeta.text = ability.IsPassive
                ? $"{discipline.Name.ToUpperInvariant()} PASSIVE ABILITY"
                : string.IsNullOrWhiteSpace(ability.Resource)
                ? $"{discipline.Name.ToUpperInvariant()} ABILITY"
                : $"{ability.Resource} · {ability.Cost:0.#} COST";
            _tooltipDescription.text = ability.Description;
            _tooltip.AddToClassList("is-visible");
            MoveTooltip(position);
        }

        private void MoveTooltip(Vector3 position)
        {
            if (_tooltip == null || !_tooltip.ClassListContains("is-visible"))
                return;
            _tooltip.style.left = Mathf.Clamp(position.x + 18f, 16f, 1630f);
            _tooltip.style.top = Mathf.Clamp(position.y + 18f, 108f, 930f);
        }

        private void HideTooltip() => _tooltip?.RemoveFromClassList("is-visible");

        private void ShowToast(string message)
        {
            if (_toast == null)
                return;

            int generation = ++_toastGeneration;
            _toast.text = message;
            _toast.AddToClassList("is-visible");
            _toast.schedule.Execute(() =>
            {
                if (generation == _toastGeneration)
                    _toast.RemoveFromClassList("is-visible");
            }).ExecuteLater(2400);
        }

        private DisciplineView? FindDiscipline(string? id)
        {
            string normalized = WireIdentifier.Normalize(id);
            return _disciplines.FirstOrDefault(discipline => discipline.Id == normalized);
        }

        private HashSet<string> SelectedSet(string disciplineId)
        {
            if (_selectedAbilities.TryGetValue(disciplineId, out HashSet<string>? selected))
                return selected;
            selected = new HashSet<string>(StringComparer.Ordinal);
            _selectedAbilities[disciplineId] = selected;
            return selected;
        }

        private int SelectedCount(string disciplineId) => SelectedSet(disciplineId).Count;

        private int TotalSecondaryAbilityCount() => _secondaryIds.Sum(SelectedCount);

        private int AllocatedPoints() => _stats.Values.Sum(value => Mathf.Max(0, value));

        private int RemainingPoints() => DisciplineLoadoutRules.RemainingPoints(_stats.Values);

        private static void SetBackground(VisualElement? element, Sprite? sprite)
        {
            if (element == null)
                return;
            if (sprite != null)
                element.style.backgroundImage = new StyleBackground(sprite);
            else
                element.style.backgroundImage = StyleKeyword.None;
        }

        private static void ApplyBorderColor(VisualElement element, Color color)
        {
            element.style.borderTopColor = color;
            element.style.borderRightColor = color;
            element.style.borderBottomColor = color;
            element.style.borderLeftColor = color;
        }

        private static Sprite? ResolveDisciplineIcon(string disciplineId)
        {
            Sprite? switchIcon = ActionIconResolver.Resolve(ActionKinds.CombatDisciplineSwitch, disciplineId);
            if (switchIcon != null)
                return switchIcon;

            string representative = disciplineId switch
            {
                "BLIGHT" => "SPELL_NECROTIC_AURA",
                "RUIN" => "SPELL_FIREBALL",
                "DIVINITY" => "SPELL_CELESTIAL_MANTLE",
                "PRIMAL" => "SPELL_GUST_OF_WIND",
                _ => string.Empty,
            };
            return string.IsNullOrEmpty(representative)
                ? null
                : ActionIconResolver.Resolve(ActionKinds.Ability, representative);
        }

        private static Color DisciplineColor(string disciplineId) => disciplineId switch
        {
            "SUBTLETY" => new Color32(159, 120, 194, 255),
            "WAR" => new Color32(213, 161, 72, 255),
            "ZEAL" => new Color32(216, 179, 90, 255),
            "PRECISION" => new Color32(111, 159, 105, 255),
            "BLIGHT" => new Color32(121, 151, 96, 255),
            "RUIN" => new Color32(189, 106, 76, 255),
            "DIVINITY" => new Color32(216, 199, 138, 255),
            "ARCANA" => new Color32(111, 131, 196, 255),
            "PRIMAL" => new Color32(105, 161, 160, 255),
            _ => new Color32(217, 181, 106, 255),
        };

        private static string DisciplineDescription(string disciplineId) => disciplineId switch
        {
            "SUBTLETY" => "Precision, mobility, and lethal dagger openings.",
            "WAR" => "Relentless pressure with a greatsword.",
            "ZEAL" => "Shielded resolve, sacred force, and protection.",
            "PRECISION" => "Measured bow attacks and evasive control.",
            "BLIGHT" => "Necromancy, shadow, and corrosive affliction.",
            "RUIN" => "Fire, frost, and lightning shaped for destruction.",
            "DIVINITY" => "Holy restoration, protection, and radiant judgment.",
            "ARCANA" => "Pure magic, control, and staff technique.",
            "PRIMAL" => "Wind and the unyielding force of the natural world.",
            _ => "A canonical combat discipline.",
        };

        private static string FormatIdentifier(string identifier)
        {
            return string.Join(" ", WireIdentifier.Normalize(identifier)
                .Split('_', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Length > 1
                    ? char.ToUpperInvariant(part[0]) + part.Substring(1).ToLowerInvariant()
                    : part.ToUpperInvariant()));
        }

        private static bool HasAbilityTag(string? encodedTags, string tag)
        {
            return (encodedTags ?? string.Empty)
                .Split('|', StringSplitOptions.RemoveEmptyEntries)
                .Any(value => string.Equals(
                    WireIdentifier.Normalize(value),
                    WireIdentifier.Normalize(tag),
                    StringComparison.Ordinal));
        }
    }
}
