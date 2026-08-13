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
    /// Equipment loadout UI. Armor and discipline-bound weapon choices remain
    /// previews until their authoritative Hub reducers commit.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class EquipmentScreen : MonoBehaviour, IEscapeCloseable
    {
        private const string RuntimeObjectName = "EquipmentScreenRuntime";
        private const string OpenClass = "is-open";
        private const float CatalogRefreshInterval = 0.5f;

        private enum EquipmentMode
        {
            Armor,
            Weapons,
        }

        private sealed class SetPresentation
        {
            public SetPresentation(
                string glyph,
                string headline,
                string flavor,
                IReadOnlyDictionary<string, string> armorBySlot)
            {
                Glyph = glyph;
                Headline = headline;
                Flavor = flavor;
                ArmorBySlot = armorBySlot;
            }

            public string Glyph { get; }
            public string Headline { get; }
            public string Flavor { get; }
            public IReadOnlyDictionary<string, string> ArmorBySlot { get; }
        }

        private static readonly IReadOnlyDictionary<string, SetPresentation> Presentations =
            new Dictionary<string, SetPresentation>(StringComparer.Ordinal)
            {
                ["PEASANT"] = new(
                    "◇",
                    "UNRESTRICTED MOBILITY",
                    "Simple clothing that leaves movement and spellcasting completely unimpeded.",
                    ArmorPieces(
                        ("CHEST", "PEASANT_TUNIC"),
                        ("LEGS", "PEASANT_TROUSERS"),
                        ("BOOTS", "PEASANT_BOOTS"),
                        ("GLOVES", "PEASANT_GLOVES"))),
                ["APPRENTICE"] = new(
                    "✧",
                    "UNRESTRICTED MOBILITY",
                    "Cloth vestments for combatants who value speed and unhindered spellwork.",
                    ArmorPieces(
                        ("HEAD", "APPRENTICE_HOOD"),
                        ("SHOULDER", "APPRENTICE_MANTLE"),
                        ("CAPE", "APPRENTICE_CLOAK"),
                        ("CHEST", "APPRENTICE_ROBE"),
                        ("LEGS", "APPRENTICE_TROUSERS"),
                        ("BOOTS", "APPRENTICE_BOOTS"),
                        ("GLOVES", "APPRENTICE_GLOVES"))),
                ["LEATHER"] = new(
                    "◆",
                    "BALANCED PROTECTION",
                    "Supple layered leather that balances reliable protection with full mobility.",
                    ArmorPieces(
                        ("HEAD", "LEATHER_HELM"),
                        ("SHOULDER", "LEATHER_SHOULDERS"),
                        ("CAPE", "LEATHER_CAPE"),
                        ("CHEST", "LEATHER_CHESTPIECE"),
                        ("LEGS", "LEATHER_LEGGINGS"),
                        ("BOOTS", "LEATHER_BOOTS"),
                        ("GLOVES", "LEATHER_GLOVES"))),
                ["IRON"] = new(
                    "⬟",
                    "MAXIMUM PROTECTION",
                    "Battle-worn iron plate built to absorb punishing blows and hostile magic.",
                    ArmorPieces(
                        ("HEAD", "IRON_HELM"),
                        ("SHOULDER", "IRON_SHOULDERS"),
                        ("CAPE", "TRAVELER_CAPE"),
                        ("CHEST", "IRON_CHESTPLATE"),
                        ("LEGS", "IRON_LEGGINGS"),
                        ("BOOTS", "IRON_BOOTS"),
                        ("GLOVES", "IRON_GLOVES"))),
                ["GILDED"] = new(
                    "⬢",
                    "MAXIMUM PROTECTION",
                    "Ornate plate built to absorb punishing blows and hostile magic.",
                    ArmorPieces(
                        ("HEAD", "GILDED_HELM"),
                        ("SHOULDER", "GILDED_SHOULDERS"),
                        ("CAPE", "GILDED_CAPE"),
                        ("CHEST", "GILDED_CHESTPLATE"),
                        ("LEGS", "GILDED_LEGGINGS"),
                        ("BOOTS", "GILDED_BOOTS"),
                        ("GLOVES", "GILDED_GLOVES"))),
            };

        private readonly List<HubArmorSetSnapshot> _sets = new();
        private readonly List<HubWeaponSnapshot> _weapons = new();
        private PanelSettings? _panelSettings;
        private VisualElement? _root;
        private ScrollView? _setList;
        private ScrollView? _mainWeaponList;
        private ScrollView? _offHandWeaponList;
        private VisualElement? _showcase;
        private VisualElement? _armorSelectionPanel;
        private VisualElement? _weaponSelectionPanel;
        private VisualElement? _armorDetailsPanel;
        private VisualElement? _weaponDetailsPanel;
        private VisualElement? _offHandSection;
        private VisualElement? _weaponOffRow;
        private VisualElement? _weaponDetailsIcon;
        private VisualElement? _mainWeaponColors;
        private VisualElement? _offHandWeaponColors;
        private VisualElement? _offHandWeaponColorSection;
        private Label? _pageKicker;
        private Label? _pageTitle;
        private Label? _pageSubtitle;
        private Label? _setCount;
        private Label? _showcaseTier;
        private Label? _showcaseName;
        private Label? _detailsName;
        private Label? _detailsFlavor;
        private Label? _summaryGlyph;
        private Label? _summaryTier;
        private Label? _summaryHeadline;
        private Label? _equippedChip;
        private Label? _physicalResistance;
        private Label? _magicalResistance;
        private VisualElement? _moveSpeedRow;
        private VisualElement? _castSpeedRow;
        private Label? _moveSpeed;
        private Label? _castSpeed;
        private Label? _noTradeoffs;
        private Label? _pieceCount;
        private Button? _equipButton;
        private Label? _toast;
        private Label? _weaponDisciplineName;
        private Label? _weaponCount;
        private Label? _weaponRuleNote;
        private Label? _weaponDetailsName;
        private Label? _weaponDetailsFlavor;
        private Label? _weaponDetailsDiscipline;
        private Label? _weaponDetailsKind;
        private Label? _weaponMainName;
        private Label? _weaponOffName;
        private Label? _weaponDetailsRule;
        private Label? _weaponEquippedChip;
        private Button? _equipWeaponButton;
        private HubController? _hubController;
        private HubNetworkManager? _hubNetwork;
        private string _tier = "LIGHT";
        private string _selectedSetId = string.Empty;
        private string _activeSetId = string.Empty;
        private string _pendingSetId = string.Empty;
        private string _lastPreviewSetId = string.Empty;
        private string _primaryDisciplineId = string.Empty;
        private string _selectedMainHandId = string.Empty;
        private string _selectedOffHandId = string.Empty;
        private string _selectedMainHandColorId = string.Empty;
        private string _selectedOffHandColorId = string.Empty;
        private string _activeMainHandId = string.Empty;
        private string _activeOffHandId = string.Empty;
        private string _activeMainHandColorId = string.Empty;
        private string _activeOffHandColorId = string.Empty;
        private string _pendingMainHandId = string.Empty;
        private string _pendingOffHandId = string.Empty;
        private string _pendingMainHandColorId = string.Empty;
        private string _pendingOffHandColorId = string.Empty;
        private string _lastWeaponPreviewSignature = string.Empty;
        private bool _equipPending;
        private bool _weaponEquipPending;
        private EquipmentMode _mode = EquipmentMode.Armor;
        private bool _open;
        private bool _draggingShowcase;
        private int _dragPointerId = -1;
        private Vector2 _lastPointerPosition;
        private float _nextCatalogRefresh;
        private int _toastGeneration;

        public event Action? Closed;
        public event Action? DisciplinesRequested;

        public int EscapeClosePriority => 116;
        public bool IsEscapeCloseable => _open;

        public static EquipmentScreen Ensure(Transform parent)
        {
            EquipmentScreen? screen = FindObjectsByType<EquipmentScreen>(FindObjectsInactive.Include)
                .FirstOrDefault(candidate => candidate.gameObject.scene == parent.gameObject.scene);
            if (screen != null && screen.transform.parent == null)
                return screen;

            if (screen != null)
                Destroy(screen.gameObject);

            GameObject host = new(RuntimeObjectName);
            SceneManager.MoveGameObjectToScene(host, parent.gameObject.scene);
            return host.AddComponent<EquipmentScreen>();
        }

        private static IReadOnlyDictionary<string, string> ArmorPieces(
            params (string SlotId, string ItemDefId)[] pieces)
        {
            return pieces.ToDictionary(
                piece => piece.SlotId,
                piece => piece.ItemDefId,
                StringComparer.Ordinal);
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
            UnbindShowcaseDrag();
            BindHub(null);
            if (_panelSettings != null)
                Destroy(_panelSettings);
        }

        private void Update()
        {
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
            RefreshCatalog(forceSelectionFromActive: true);
        }

        public void Close()
        {
            if (!_open)
                return;

            _open = false;
            _draggingShowcase = false;
            _dragPointerId = -1;
            _root?.RemoveFromClassList(OpenClass);
            ClearShowcasePreview();
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
            UIDocument document = ArenaPanel.CreateDocument(gameObject, "UI/Toolkit/Equipment", 21f);
            _panelSettings = document.panelSettings;
            _root = document.rootVisualElement.Q<VisualElement>("EquipmentScreen");
            if (_root == null)
            {
                Debug.LogError("EquipmentScreen: Equipment.uxml is missing EquipmentScreen.");
                return;
            }

            _setList = _root.Q<ScrollView>("SetList");
            _mainWeaponList = _root.Q<ScrollView>("MainWeaponList");
            _offHandWeaponList = _root.Q<ScrollView>("OffHandWeaponList");
            _showcase = _root.Q<VisualElement>("PlayerShowcase");
            _armorSelectionPanel = _root.Q<VisualElement>("ArmorSelectionPanel");
            _weaponSelectionPanel = _root.Q<VisualElement>("WeaponSelectionPanel");
            _armorDetailsPanel = _root.Q<VisualElement>("ArmorDetailsPanel");
            _weaponDetailsPanel = _root.Q<VisualElement>("WeaponDetailsPanel");
            _offHandSection = _root.Q<VisualElement>("OffHandSection");
            _weaponOffRow = _root.Q<VisualElement>("WeaponOffRow");
            _weaponDetailsIcon = _root.Q<VisualElement>("WeaponDetailsIcon");
            _mainWeaponColors = _root.Q<VisualElement>("MainWeaponColors");
            _offHandWeaponColors = _root.Q<VisualElement>("OffHandWeaponColors");
            _offHandWeaponColorSection = _root.Q<VisualElement>("OffHandWeaponColorSection");
            _pageKicker = _root.Q<Label>("PageKicker");
            _pageTitle = _root.Q<Label>("PageTitle");
            _pageSubtitle = _root.Q<Label>("PageSubtitle");
            _setCount = _root.Q<Label>("SetCount");
            _showcaseTier = _root.Q<Label>("ShowcaseTier");
            _showcaseName = _root.Q<Label>("ShowcaseName");
            _detailsName = _root.Q<Label>("DetailsName");
            _detailsFlavor = _root.Q<Label>("DetailsFlavor");
            _summaryGlyph = _root.Q<Label>("SummaryGlyph");
            _summaryTier = _root.Q<Label>("SummaryTier");
            _summaryHeadline = _root.Q<Label>("SummaryHeadline");
            _equippedChip = _root.Q<Label>("EquippedChip");
            _physicalResistance = _root.Q<Label>("PhysicalResistance");
            _magicalResistance = _root.Q<Label>("MagicalResistance");
            _moveSpeedRow = _root.Q<VisualElement>("MoveSpeedRow");
            _castSpeedRow = _root.Q<VisualElement>("CastSpeedRow");
            _moveSpeed = _root.Q<Label>("MoveSpeed");
            _castSpeed = _root.Q<Label>("CastSpeed");
            _noTradeoffs = _root.Q<Label>("NoTradeoffs");
            _pieceCount = _root.Q<Label>("PieceCount");
            _equipButton = _root.Q<Button>("EquipButton");
            _toast = _root.Q<Label>("Toast");
            _weaponDisciplineName = _root.Q<Label>("WeaponDisciplineName");
            _weaponCount = _root.Q<Label>("WeaponCount");
            _weaponRuleNote = _root.Q<Label>("WeaponRuleNote");
            _weaponDetailsName = _root.Q<Label>("WeaponDetailsName");
            _weaponDetailsFlavor = _root.Q<Label>("WeaponDetailsFlavor");
            _weaponDetailsDiscipline = _root.Q<Label>("WeaponDetailsDiscipline");
            _weaponDetailsKind = _root.Q<Label>("WeaponDetailsKind");
            _weaponMainName = _root.Q<Label>("WeaponMainName");
            _weaponOffName = _root.Q<Label>("WeaponOffName");
            _weaponDetailsRule = _root.Q<Label>("WeaponDetailsRule");
            _weaponEquippedChip = _root.Q<Label>("WeaponEquippedChip");
            _equipWeaponButton = _root.Q<Button>("EquipWeaponButton");

            BindButton("TierLight", () => SelectTier("LIGHT"));
            BindButton("TierMedium", () => SelectTier("MEDIUM"));
            BindButton("TierHeavy", () => SelectTier("HEAVY"));
            BindButton("PreviousSet", () => CycleSet(-1));
            BindButton("NextSet", () => CycleSet(1));
            BindButton("EquipButton", EquipSelectedSet);
            BindButton("EquipWeaponButton", EquipSelectedWeapons);
            BindButton("BackButton", Close);
            BindButton("WeaponBackButton", Close);
            BindButton("ArmorMode", () => SelectMode(EquipmentMode.Armor));
            BindButton("WeaponsMode", () => SelectMode(EquipmentMode.Weapons));
            BindButton("NavPlay", Close);
            BindButton("NavPlayTab", Close);
            BindButton("NavDisciplines", RequestDisciplines);

            Button? settings = _root.Q<Button>("SettingsButton");
            if (settings != null)
                settings.clicked += SystemMenuScreen.OpenFromEscape;

            _hubController = FindObjectsByType<HubController>(FindObjectsInactive.Include)
                .FirstOrDefault(candidate => candidate.gameObject.scene == gameObject.scene);
            BindShowcaseDrag();
        }

        private void BindButton(string name, Action callback)
        {
            Button? button = _root?.Q<Button>(name);
            if (button != null)
                button.clicked += callback;
        }

        private void RequestDisciplines()
        {
            Close();
            DisciplinesRequested?.Invoke();
        }

        private void BindHub(HubNetworkManager? hub)
        {
            if (ReferenceEquals(hub, _hubNetwork))
                return;

            if (_hubNetwork != null)
            {
                _hubNetwork.ArmorSetSaveCompleted -= OnArmorSetSaved;
                _hubNetwork.WeaponLoadoutSaveCompleted -= OnWeaponLoadoutSaved;
            }

            _hubNetwork = hub;
            _equipPending = false;
            _pendingSetId = string.Empty;
            _weaponEquipPending = false;
            _pendingMainHandId = string.Empty;
            _pendingOffHandId = string.Empty;
            _pendingMainHandColorId = string.Empty;
            _pendingOffHandColorId = string.Empty;
            if (_hubNetwork != null)
            {
                _hubNetwork.ArmorSetSaveCompleted += OnArmorSetSaved;
                _hubNetwork.WeaponLoadoutSaveCompleted += OnWeaponLoadoutSaved;
            }
        }

        private void RefreshCatalog(bool forceSelectionFromActive = false)
        {
            HubNetworkManager? hub = _hubNetwork;
            if (hub == null || !hub.IsReady)
            {
                RenderWaitingForCatalog();
                return;
            }

            _sets.Clear();
            _sets.AddRange(hub.ArmorSets
                .OrderBy(row => row.SortOrder)
                .ThenBy(row => row.ArmorSetId, StringComparer.Ordinal));
            _weapons.Clear();
            _weapons.AddRange(hub.Weapons
                .OrderBy(row => row.SortOrder)
                .ThenBy(row => row.ItemDefId, StringComparer.Ordinal));

            _activeSetId = WireIdentifier.Normalize(hub.Loadout?.ArmorSetId);
            _primaryDisciplineId = WireIdentifier.Normalize(hub.Loadout?.PrimaryDisciplineId);
            _activeMainHandId = WireIdentifier.Normalize(hub.Loadout?.MainHandItemDefId);
            _activeOffHandId = WireIdentifier.Normalize(hub.Loadout?.OffHandItemDefId);
            _activeMainHandColorId = WireIdentifier.Normalize(hub.Loadout?.MainHandColorId);
            _activeOffHandColorId = WireIdentifier.Normalize(hub.Loadout?.OffHandColorId);

            if (_sets.Count == 0)
            {
                RenderWaitingForCatalog();
                return;
            }

            bool missingSelection = FindSet(_selectedSetId) == null;
            if ((forceSelectionFromActive || missingSelection) && FindSet(_activeSetId) is HubArmorSetSnapshot activeSet)
            {
                _selectedSetId = WireIdentifier.Normalize(activeSet.ArmorSetId);
                _tier = WireIdentifier.Normalize(activeSet.ArmorTier);
            }
            else if (missingSelection)
            {
                HubArmorSetSnapshot first = _sets[0];
                _selectedSetId = WireIdentifier.Normalize(first.ArmorSetId);
                _tier = WireIdentifier.Normalize(first.ArmorTier);
            }

            NormalizeWeaponSelection(forceSelectionFromActive);

            RenderAll();
        }

        private void RenderWaitingForCatalog()
        {
            RenderMode();
            _setList?.Clear();
            _mainWeaponList?.Clear();
            _offHandWeaponList?.Clear();
            if (_setCount != null)
                _setCount.text = "WAITING";
            if (_detailsName != null)
                _detailsName.text = "ARMOR CATALOG";
            if (_detailsFlavor != null)
                _detailsFlavor.text = "Connecting to the authoritative equipment catalog…";
            if (_equipButton != null)
            {
                _equipButton.SetEnabled(false);
                _equipButton.text = "CONNECTING…";
            }
            SetText(_weaponDisciplineName, "CONNECTING");
            SetText(_weaponCount, "WAITING");
            SetText(_weaponDetailsName, "WEAPON CATALOG");
            SetText(_weaponDetailsFlavor, "Connecting to the authoritative weapon catalog…");
            if (_equipWeaponButton != null)
            {
                _equipWeaponButton.SetEnabled(false);
                _equipWeaponButton.text = "CONNECTING…";
            }
        }

        private void RenderAll()
        {
            RenderMode();
            if (_mode == EquipmentMode.Armor)
            {
                RenderTiers();
                RenderSetList();
                RenderDetails();
            }
            else
            {
                RenderWeaponLists();
                RenderWeaponDetails();
            }
            ApplyShowcasePreview();
        }

        private void SelectMode(EquipmentMode mode)
        {
            if (_mode == mode)
                return;

            _mode = mode;
            _lastPreviewSetId = string.Empty;
            _lastWeaponPreviewSignature = string.Empty;
            if (_mode == EquipmentMode.Armor)
                _hubController?.SetShowcaseWeaponPreview(null, null, null, null);
            else
                _hubController?.SetShowcaseArmorPreview(null);
            RenderAll();
        }

        private void RenderMode()
        {
            bool weapons = _mode == EquipmentMode.Weapons;
            _armorSelectionPanel?.EnableInClassList("equipment-panel-hidden", weapons);
            _armorDetailsPanel?.EnableInClassList("equipment-panel-hidden", weapons);
            _weaponSelectionPanel?.EnableInClassList("equipment-panel-hidden", !weapons);
            _weaponDetailsPanel?.EnableInClassList("equipment-panel-hidden", !weapons);
            SetSelectedClass("ArmorMode", !weapons);
            SetSelectedClass("WeaponsMode", weapons);

            SetText(_pageKicker, weapons ? "WEAPON LOADOUT" : "ARMOR LOADOUT");
            SetText(_pageTitle, weapons ? "CHOOSE YOUR WEAPONS" : "CHOOSE YOUR ARMOR");
            SetText(
                _pageSubtitle,
                weapons
                    ? "Your primary discipline determines the weapons available to equip."
                    : "Balance protection and mobility. Armor is equipped as a complete set.");
        }

        private void RenderTiers()
        {
            SetSelectedClass("TierLight", _tier == "LIGHT");
            SetSelectedClass("TierMedium", _tier == "MEDIUM");
            SetSelectedClass("TierHeavy", _tier == "HEAVY");
        }

        private void SetSelectedClass(string elementName, bool selected)
        {
            _root?.Q<Button>(elementName)?.EnableInClassList("is-selected", selected);
        }

        private void RenderSetList()
        {
            if (_setList == null)
                return;

            List<HubArmorSetSnapshot> visibleSets = SetsForTier();
            _setList.Clear();
            if (_setCount != null)
                _setCount.text = $"{visibleSets.Count} {(visibleSets.Count == 1 ? "SET" : "SETS")}";

            foreach (HubArmorSetSnapshot set in visibleSets)
            {
                string setId = WireIdentifier.Normalize(set.ArmorSetId);
                SetPresentation presentation = PresentationFor(set);
                Button card = new() { name = $"ArmorSet_{setId}" };
                card.AddToClassList("set-card");
                card.EnableInClassList("is-selected", setId == _selectedSetId);

                VisualElement sigil = new();
                sigil.AddToClassList("set-sigil");
                Label glyph = new(presentation.Glyph);
                glyph.AddToClassList("set-sigil-glyph");
                sigil.Add(glyph);

                VisualElement copy = new();
                copy.AddToClassList("set-copy");
                Label meta = new($"{WireIdentifier.Normalize(set.ArmorTier)} ARMOR · {set.PieceCount} PIECES");
                meta.AddToClassList("set-meta");
                Label name = new(DisplayName(set).ToUpperInvariant());
                name.AddToClassList("set-name");
                Label effects = new($"{Percent(set.PhysicalResistance, false)} physical · {Percent(set.MagicalResistance, false)} magical");
                effects.AddToClassList("set-effects");
                copy.Add(meta);
                copy.Add(name);
                copy.Add(effects);

                Label check = new(setId == _activeSetId ? "✓" : "›");
                check.AddToClassList("set-check");
                card.Add(sigil);
                card.Add(copy);
                card.Add(check);

                string capturedId = setId;
                card.clicked += () => SelectSet(capturedId);
                _setList.Add(card);
            }
        }

        private void RenderDetails()
        {
            HubArmorSetSnapshot? set = FindSet(_selectedSetId);
            if (set == null)
                return;

            string setId = WireIdentifier.Normalize(set.ArmorSetId);
            string tier = WireIdentifier.Normalize(set.ArmorTier);
            string displayName = DisplayName(set).ToUpperInvariant();
            SetPresentation presentation = PresentationFor(set);
            bool equipped = setId == _activeSetId;
            bool hasMovePenalty = set.MoveSpeedModifier < -0.0001f;
            bool hasCastPenalty = set.CastSpeedModifier < -0.0001f;

            SetText(_showcaseTier, $"{tier} ARMOR");
            SetText(_showcaseName, displayName);
            SetText(_detailsName, displayName);
            SetText(_detailsFlavor, presentation.Flavor);
            SetText(_summaryGlyph, presentation.Glyph);
            SetText(_summaryTier, $"{tier} ARMOR");
            SetText(_summaryHeadline, presentation.Headline);
            SetText(_physicalResistance, Percent(set.PhysicalResistance, true));
            SetText(_magicalResistance, Percent(set.MagicalResistance, true));
            SetText(_moveSpeed, Percent(set.MoveSpeedModifier, false));
            SetText(_castSpeed, Percent(set.CastSpeedModifier, false));
            SetText(_pieceCount, $"{set.PieceCount} / {set.PieceCount} PIECES");

            _moveSpeedRow?.EnableInClassList("is-hidden", !hasMovePenalty);
            _castSpeedRow?.EnableInClassList("is-hidden", !hasCastPenalty);
            _noTradeoffs?.EnableInClassList("is-visible", !hasMovePenalty && !hasCastPenalty);
            _equippedChip?.EnableInClassList("is-hidden", !equipped);

            if (_equipButton != null)
            {
                _equipButton.EnableInClassList("is-equipped", equipped);
                _equipButton.EnableInClassList("is-pending", _equipPending);
                _equipButton.SetEnabled(!_equipPending);
                _equipButton.text = _equipPending
                    ? "◆  EQUIPPING…  ◆"
                    : equipped
                        ? "◆  EQUIPPED  ◆"
                        : "◆  EQUIP COMPLETE SET  ◆";
            }
        }

        private static void SetText(Label? label, string value)
        {
            if (label != null)
                label.text = value;
        }

        private void SelectTier(string tier)
        {
            string normalized = WireIdentifier.Normalize(tier);
            HubArmorSetSnapshot? first = _sets.FirstOrDefault(set =>
                WireIdentifier.Normalize(set.ArmorTier) == normalized);
            if (first == null)
                return;

            _tier = normalized;
            _selectedSetId = WireIdentifier.Normalize(first.ArmorSetId);
            RenderAll();
        }

        private void SelectSet(string setId)
        {
            HubArmorSetSnapshot? set = FindSet(setId);
            if (set == null)
                return;

            _selectedSetId = WireIdentifier.Normalize(set.ArmorSetId);
            _tier = WireIdentifier.Normalize(set.ArmorTier);
            RenderAll();
        }

        private void CycleSet(int direction)
        {
            if (_mode == EquipmentMode.Weapons)
            {
                CycleMainWeapon(direction);
                return;
            }

            List<HubArmorSetSnapshot> visibleSets = SetsForTier();
            if (visibleSets.Count == 0)
                return;

            int currentIndex = visibleSets.FindIndex(set =>
                WireIdentifier.Normalize(set.ArmorSetId) == _selectedSetId);
            if (currentIndex < 0)
                currentIndex = 0;
            int nextIndex = (currentIndex + direction + visibleSets.Count) % visibleSets.Count;
            SelectSet(visibleSets[nextIndex].ArmorSetId);
        }

        private void CycleMainWeapon(int direction)
        {
            List<HubWeaponSnapshot> mains = WeaponsForSlot("MAIN_HAND");
            if (mains.Count == 0)
                return;

            int currentIndex = mains.FindIndex(row =>
                WireIdentifier.Normalize(row.ItemDefId) == _selectedMainHandId);
            if (currentIndex < 0)
                currentIndex = 0;
            int nextIndex = (currentIndex + direction + mains.Count) % mains.Count;
            SelectMainWeapon(WireIdentifier.Normalize(mains[nextIndex].ItemDefId));
        }

        private List<HubArmorSetSnapshot> SetsForTier()
        {
            return _sets.Where(set => WireIdentifier.Normalize(set.ArmorTier) == _tier).ToList();
        }

        private HubArmorSetSnapshot? FindSet(string? setId)
        {
            string normalized = WireIdentifier.Normalize(setId);
            return _sets.FirstOrDefault(set =>
                WireIdentifier.Normalize(set.ArmorSetId) == normalized);
        }

        private void NormalizeWeaponSelection(bool forceSelectionFromActive)
        {
            List<HubWeaponSnapshot> mains = WeaponsForSlot("MAIN_HAND");
            List<HubWeaponSnapshot> offHands = WeaponsForSlot("OFF_HAND");

            bool selectedMainAllowed = mains.Any(row =>
                WireIdentifier.Normalize(row.ItemDefId) == _selectedMainHandId);
            if (forceSelectionFromActive || !selectedMainAllowed)
            {
                _selectedMainHandId = mains.Any(row =>
                        WireIdentifier.Normalize(row.ItemDefId) == _activeMainHandId)
                    ? _activeMainHandId
                    : mains.FirstOrDefault()?.ItemDefId ?? string.Empty;
                _selectedMainHandId = WireIdentifier.Normalize(_selectedMainHandId);
            }

            bool selectedOffAllowed = offHands.Any(row =>
                WireIdentifier.Normalize(row.ItemDefId) == _selectedOffHandId);
            if (forceSelectionFromActive || !selectedOffAllowed)
            {
                _selectedOffHandId = offHands.Any(row =>
                        WireIdentifier.Normalize(row.ItemDefId) == _activeOffHandId)
                    ? _activeOffHandId
                    : offHands.FirstOrDefault()?.ItemDefId ?? string.Empty;
                _selectedOffHandId = WireIdentifier.Normalize(_selectedOffHandId);
            }

            if (!string.Equals(_primaryDisciplineId, "ZEAL", StringComparison.Ordinal))
            {
                _selectedOffHandId = string.Empty;
                _selectedOffHandColorId = string.Empty;
            }

            _selectedMainHandColorId = NormalizeSelectedColor(
                _selectedMainHandId,
                _selectedMainHandColorId,
                _selectedMainHandId == _activeMainHandId ? _activeMainHandColorId : string.Empty,
                forceSelectionFromActive);
            if (string.Equals(_primaryDisciplineId, "ZEAL", StringComparison.Ordinal))
            {
                _selectedOffHandColorId = NormalizeSelectedColor(
                    _selectedOffHandId,
                    _selectedOffHandColorId,
                    _selectedOffHandId == _activeOffHandId ? _activeOffHandColorId : string.Empty,
                    forceSelectionFromActive);
            }
        }

        private string NormalizeSelectedColor(
            string itemDefId,
            string selectedColorId,
            string preferredColorId,
            bool forcePreferred)
        {
            List<HubWeaponColorSnapshot> colors = ColorsForWeapon(itemDefId);
            if (colors.Count == 0)
                return string.Empty;
            string preferred = WireIdentifier.Normalize(preferredColorId);
            if ((!forcePreferred || !string.IsNullOrWhiteSpace(preferred))
                && colors.Any(color => WireIdentifier.Normalize(color.ColorId) == preferred))
            {
                return preferred;
            }
            string selected = WireIdentifier.Normalize(selectedColorId);
            if (!forcePreferred
                && colors.Any(color => WireIdentifier.Normalize(color.ColorId) == selected))
            {
                return selected;
            }
            return WireIdentifier.Normalize(colors[0].ColorId);
        }

        private List<HubWeaponColorSnapshot> ColorsForWeapon(string? itemDefId)
        {
            string normalized = WireIdentifier.Normalize(itemDefId);
            return (_hubNetwork?.WeaponColors ?? Array.Empty<HubWeaponColorSnapshot>())
                .Where(color => WireIdentifier.Normalize(color.ItemDefId) == normalized)
                .OrderBy(color => color.SortOrder)
                .ThenBy(color => color.ColorId, StringComparer.Ordinal)
                .ToList();
        }

        private List<HubWeaponSnapshot> WeaponsForSlot(string slotId)
        {
            return _weapons.Where(row =>
                    WireIdentifier.Normalize(row.PrimaryDisciplineId) == _primaryDisciplineId
                    && WireIdentifier.Normalize(row.EquipSlot) == slotId)
                .ToList();
        }

        private HubWeaponSnapshot? FindWeapon(string? itemDefId)
        {
            string normalized = WireIdentifier.Normalize(itemDefId);
            return _weapons.FirstOrDefault(row =>
                WireIdentifier.Normalize(row.ItemDefId) == normalized);
        }

        private void RenderWeaponLists()
        {
            List<HubWeaponSnapshot> mains = WeaponsForSlot("MAIN_HAND");
            List<HubWeaponSnapshot> offHands = WeaponsForSlot("OFF_HAND");
            bool requiresOffHand = string.Equals(_primaryDisciplineId, "ZEAL", StringComparison.Ordinal);
            _offHandSection?.EnableInClassList("equipment-panel-hidden", !requiresOffHand);
            _weaponOffRow?.EnableInClassList("equipment-panel-hidden", !requiresOffHand);

            string disciplineName = DisciplineDisplayName();
            SetText(_weaponDisciplineName, string.IsNullOrWhiteSpace(disciplineName) ? "NO PRIMARY DISCIPLINE" : disciplineName.ToUpperInvariant());
            SetText(_weaponCount, $"{mains.Count + offHands.Count} AVAILABLE");
            SetText(_weaponRuleNote, RuleForPrimaryDiscipline());

            PopulateWeaponList(_mainWeaponList, mains, _selectedMainHandId, SelectMainWeapon);
            PopulateWeaponList(_offHandWeaponList, offHands, _selectedOffHandId, SelectOffHandWeapon);
        }

        private void PopulateWeaponList(
            ScrollView? list,
            IReadOnlyList<HubWeaponSnapshot> weapons,
            string selectedId,
            Action<string> select)
        {
            if (list == null)
                return;

            list.Clear();
            if (weapons.Count == 0)
            {
                Label empty = new("No weapons are available for this slot.");
                empty.AddToClassList("note-body");
                list.Add(empty);
                return;
            }

            foreach (HubWeaponSnapshot weapon in weapons)
            {
                string itemDefId = WireIdentifier.Normalize(weapon.ItemDefId);
                Button card = new() { name = $"Weapon_{itemDefId}" };
                card.AddToClassList("weapon-card");
                card.EnableInClassList("is-selected", itemDefId == selectedId);

                VisualElement icon = new();
                icon.AddToClassList("weapon-card-icon");
                SetWeaponIcon(icon, weapon.IconId);

                VisualElement copy = new();
                copy.AddToClassList("weapon-card-copy");
                Label name = new(WeaponDisplayName(weapon).ToUpperInvariant());
                name.AddToClassList("weapon-card-name");
                Label meta = new($"{FriendlyWeaponKind(weapon.WeaponKind)} · {FriendlyHandRequirement(weapon.HandRequirement)}");
                meta.AddToClassList("weapon-card-meta");
                copy.Add(name);
                copy.Add(meta);

                Label check = new(itemDefId == selectedId ? "◆" : "›");
                check.AddToClassList("weapon-card-check");
                card.Add(icon);
                card.Add(copy);
                card.Add(check);
                string capturedId = itemDefId;
                card.clicked += () => select(capturedId);
                list.Add(card);
            }
        }

        private void SelectMainWeapon(string itemDefId)
        {
            if (!WeaponsForSlot("MAIN_HAND").Any(row =>
                    WireIdentifier.Normalize(row.ItemDefId) == itemDefId))
                return;

            _selectedMainHandId = itemDefId;
            _selectedMainHandColorId = NormalizeSelectedColor(
                itemDefId,
                string.Empty,
                itemDefId == _activeMainHandId ? _activeMainHandColorId : string.Empty,
                forcePreferred: true);
            RenderAll();
        }

        private void SelectOffHandWeapon(string itemDefId)
        {
            if (!WeaponsForSlot("OFF_HAND").Any(row =>
                    WireIdentifier.Normalize(row.ItemDefId) == itemDefId))
                return;

            _selectedOffHandId = itemDefId;
            _selectedOffHandColorId = NormalizeSelectedColor(
                itemDefId,
                string.Empty,
                itemDefId == _activeOffHandId ? _activeOffHandColorId : string.Empty,
                forcePreferred: true);
            RenderAll();
        }

        private void RenderWeaponDetails()
        {
            HubWeaponSnapshot? main = FindWeapon(_selectedMainHandId);
            HubWeaponSnapshot? offHand = FindWeapon(_selectedOffHandId);
            bool requiresOffHand = string.Equals(_primaryDisciplineId, "ZEAL", StringComparison.Ordinal);
            bool valid = main != null
                && !string.IsNullOrWhiteSpace(_selectedMainHandColorId)
                && (!requiresOffHand
                    || (offHand != null && !string.IsNullOrWhiteSpace(_selectedOffHandColorId)));
            bool equipped = valid
                && _selectedMainHandId == _activeMainHandId
                && _selectedOffHandId == _activeOffHandId
                && _selectedMainHandColorId == _activeMainHandColorId
                && _selectedOffHandColorId == _activeOffHandColorId;

            SetText(_weaponDetailsName, main == null ? "NO WEAPON AVAILABLE" : WeaponDisplayName(main).ToUpperInvariant());
            SetText(
                _weaponDetailsFlavor,
                main == null
                    ? "Choose Subtlety, War, Zeal, or Precision as your primary discipline to unlock its arsenal."
                    : $"A curated {FriendlyWeaponKind(main.WeaponKind).ToLowerInvariant()} loadout with complete Arena animation and attachment support.");
            SetText(_weaponDetailsDiscipline, $"{DisciplineDisplayName().ToUpperInvariant()} PRIMARY");
            SetText(_weaponDetailsKind, main == null ? "NO WEAPON TYPE" : FriendlyWeaponKind(main.WeaponKind).ToUpperInvariant());
            SetText(
                _weaponMainName,
                main == null ? "None selected" : $"{WeaponDisplayName(main)} · {ColorDisplayName(main.ItemDefId, _selectedMainHandColorId)}");
            SetText(
                _weaponOffName,
                offHand == null ? "None selected" : $"{WeaponDisplayName(offHand)} · {ColorDisplayName(offHand.ItemDefId, _selectedOffHandColorId)}");
            SetText(_weaponDetailsRule, RuleForPrimaryDiscipline());
            _weaponEquippedChip?.EnableInClassList("is-hidden", !equipped);
            SetWeaponIcon(_weaponDetailsIcon, main?.IconId ?? string.Empty);
            PopulateColorSelector(
                _mainWeaponColors,
                _selectedMainHandId,
                _selectedMainHandColorId,
                colorId =>
                {
                    _selectedMainHandColorId = colorId;
                    RenderAll();
                });
            _offHandWeaponColorSection?.EnableInClassList("equipment-panel-hidden", !requiresOffHand);
            PopulateColorSelector(
                _offHandWeaponColors,
                _selectedOffHandId,
                _selectedOffHandColorId,
                colorId =>
                {
                    _selectedOffHandColorId = colorId;
                    RenderAll();
                });

            if (_equipWeaponButton != null)
            {
                _equipWeaponButton.EnableInClassList("is-equipped", equipped);
                _equipWeaponButton.EnableInClassList("is-pending", _weaponEquipPending);
                _equipWeaponButton.SetEnabled(valid && !_weaponEquipPending);
                _equipWeaponButton.text = _weaponEquipPending
                    ? "◆  EQUIPPING…  ◆"
                    : equipped
                        ? "◆  EQUIPPED  ◆"
                        : "◆  EQUIP WEAPONS  ◆";
            }
        }

        private void PopulateColorSelector(
            VisualElement? container,
            string itemDefId,
            string selectedColorId,
            Action<string> select)
        {
            if (container == null)
                return;
            container.Clear();
            foreach (HubWeaponColorSnapshot color in ColorsForWeapon(itemDefId))
            {
                string colorId = WireIdentifier.Normalize(color.ColorId);
                Button swatch = new() { name = $"WeaponColor_{itemDefId}_{colorId}", tooltip = color.DisplayName };
                swatch.AddToClassList("weapon-color-button");
                swatch.EnableInClassList("is-selected", colorId == selectedColorId);
                if (ColorUtility.TryParseHtmlString(color.ColorHex, out Color parsed))
                    swatch.style.backgroundColor = parsed;
                swatch.clicked += () => select(colorId);
                container.Add(swatch);
            }
        }

        private string ColorDisplayName(string itemDefId, string colorId)
        {
            HubWeaponColorSnapshot? color = ColorsForWeapon(itemDefId).FirstOrDefault(candidate =>
                WireIdentifier.Normalize(candidate.ColorId) == WireIdentifier.Normalize(colorId));
            return color?.DisplayName?.Trim() ?? WireIdentifier.Normalize(colorId);
        }

        private static void SetWeaponIcon(VisualElement? element, string iconId)
        {
            if (element == null)
                return;

            Sprite? sprite = ItemIconResolver.Resolve(iconId);
            if (sprite == null)
                element.style.backgroundImage = StyleKeyword.None;
            else
                element.style.backgroundImage = new StyleBackground(sprite);
        }

        private string DisciplineDisplayName()
        {
            HubDisciplineSnapshot? discipline = _hubNetwork?.Disciplines.FirstOrDefault(row =>
                WireIdentifier.Normalize(row.Id) == _primaryDisciplineId);
            return discipline?.Name?.Trim()
                ?? _primaryDisciplineId.Replace('_', ' ');
        }

        private string RuleForPrimaryDiscipline()
        {
            return _primaryDisciplineId switch
            {
                "SUBTLETY" => "Subtlety equips paired daggers.",
                "WAR" => "War equips two-handed swords, axes, hammers, and polearms; staves and bows are excluded.",
                "ZEAL" => "Zeal equips a one-handed sword, axe, hammer, or fist weapon with a shield.",
                "PRECISION" => "Precision equips bows.",
                _ => "Choose a supported primary discipline to select weapons.",
            };
        }

        private static string WeaponDisplayName(HubWeaponSnapshot weapon)
        {
            return string.IsNullOrWhiteSpace(weapon.DisplayName)
                ? WireIdentifier.Normalize(weapon.ItemDefId).Replace('_', ' ')
                : weapon.DisplayName.Trim();
        }

        private static string FriendlyWeaponKind(string weaponKind)
        {
            return WireIdentifier.Normalize(weaponKind) switch
            {
                "DAGGER_PAIR" => "Paired Daggers",
                "TWO_HAND_SWORD" => "Two-Handed Sword",
                "TWO_HAND_AXE" => "Two-Handed Axe",
                "TWO_HAND_HAMMER" => "Two-Handed Hammer",
                "POLEARM" => "Polearm",
                "ONE_HAND_SWORD" => "One-Handed Sword",
                "ONE_HAND_AXE" => "One-Handed Axe",
                "ONE_HAND_HAMMER" => "One-Handed Hammer",
                "ONE_HAND_FIST" => "Fist Weapon",
                "SHIELD" => "Shield",
                "BOW" => "Bow",
                string value => value.Replace('_', ' '),
            };
        }

        private static string FriendlyHandRequirement(string handRequirement)
        {
            return WireIdentifier.Normalize(handRequirement) switch
            {
                "TWO_HAND" => "Two Hands",
                "ONE_HAND" => "One Hand",
                "OFF_HAND" => "Off Hand",
                string value => value.Replace('_', ' '),
            };
        }

        private void EquipSelectedSet()
        {
            HubArmorSetSnapshot? selected = FindSet(_selectedSetId);
            if (selected == null)
                return;
            if (_selectedSetId == _activeSetId)
            {
                ShowToast($"{DisplayName(selected)} is already equipped.");
                return;
            }

            HubNetworkManager? hub = _hubNetwork;
            if (hub == null || !hub.IsReady || _equipPending)
            {
                ShowToast("Connect to equip this armor set.");
                return;
            }

            _pendingSetId = _selectedSetId;
            _equipPending = true;
            RenderDetails();
            if (!hub.SaveArmorSet(_pendingSetId))
            {
                _equipPending = false;
                _pendingSetId = string.Empty;
                RenderDetails();
                ShowToast("Connect to equip this armor set.");
            }
        }

        private void OnArmorSetSaved(bool success, string reason)
        {
            if (!_equipPending)
                return;

            string normalizedSetId = _pendingSetId;
            _equipPending = false;
            _pendingSetId = string.Empty;
            if (success)
            {
                _activeSetId = normalizedSetId;
                _nextCatalogRefresh = 0f;
                RenderAll();
                HubArmorSetSnapshot? equipped = FindSet(normalizedSetId);
                ShowToast($"{(equipped == null ? "Armor set" : DisplayName(equipped))} equipped as a complete set.");
                return;
            }

            Debug.LogError($"[{nameof(EquipmentScreen)}] Equipping armor set failed: {reason}");
            RenderDetails();
            ShowToast($"Could not equip armor set: {reason}");
        }

        private void EquipSelectedWeapons()
        {
            HubWeaponSnapshot? main = FindWeapon(_selectedMainHandId);
            bool requiresOffHand = string.Equals(_primaryDisciplineId, "ZEAL", StringComparison.Ordinal);
            HubWeaponSnapshot? offHand = FindWeapon(_selectedOffHandId);
            if (main == null || (requiresOffHand && offHand == null))
            {
                ShowToast("Choose a complete weapon loadout first.");
                return;
            }

            if (_selectedMainHandId == _activeMainHandId
                && _selectedOffHandId == _activeOffHandId
                && _selectedMainHandColorId == _activeMainHandColorId
                && _selectedOffHandColorId == _activeOffHandColorId)
            {
                ShowToast("That weapon loadout is already equipped.");
                return;
            }

            HubNetworkManager? hub = _hubNetwork;
            if (hub == null || !hub.IsReady || _weaponEquipPending)
            {
                ShowToast("Connect to equip this weapon loadout.");
                return;
            }

            _pendingMainHandId = _selectedMainHandId;
            _pendingOffHandId = requiresOffHand ? _selectedOffHandId : string.Empty;
            _pendingMainHandColorId = _selectedMainHandColorId;
            _pendingOffHandColorId = requiresOffHand ? _selectedOffHandColorId : string.Empty;
            _weaponEquipPending = true;
            RenderWeaponDetails();
            if (!hub.SaveWeaponLoadout(
                    _pendingMainHandId,
                    _pendingMainHandColorId,
                    _pendingOffHandId,
                    _pendingOffHandColorId))
            {
                _weaponEquipPending = false;
                _pendingMainHandId = string.Empty;
                _pendingOffHandId = string.Empty;
                _pendingMainHandColorId = string.Empty;
                _pendingOffHandColorId = string.Empty;
                RenderWeaponDetails();
                ShowToast("Connect to equip this weapon loadout.");
            }
        }

        private void OnWeaponLoadoutSaved(bool success, string reason)
        {
            if (!_weaponEquipPending)
                return;

            string mainHandId = _pendingMainHandId;
            string offHandId = _pendingOffHandId;
            string mainHandColorId = _pendingMainHandColorId;
            string offHandColorId = _pendingOffHandColorId;
            _weaponEquipPending = false;
            _pendingMainHandId = string.Empty;
            _pendingOffHandId = string.Empty;
            _pendingMainHandColorId = string.Empty;
            _pendingOffHandColorId = string.Empty;
            if (success)
            {
                _activeMainHandId = mainHandId;
                _activeOffHandId = offHandId;
                _activeMainHandColorId = mainHandColorId;
                _activeOffHandColorId = offHandColorId;
                _nextCatalogRefresh = 0f;
                RenderAll();
                ShowToast("Weapon loadout equipped.");
                return;
            }

            Debug.LogError($"[{nameof(EquipmentScreen)}] Equipping weapon loadout failed: {reason}");
            RenderWeaponDetails();
            ShowToast($"Could not equip weapon loadout: {reason}");
        }

        private void ApplyShowcasePreview()
        {
            if (!_open)
                return;

            if (_mode == EquipmentMode.Weapons)
            {
                string signature = $"{_selectedMainHandId}|{_selectedMainHandColorId}|{_selectedOffHandId}|{_selectedOffHandColorId}";
                if (signature == _lastWeaponPreviewSignature)
                    return;

                _lastWeaponPreviewSignature = signature;
                _hubController?.SetShowcaseWeaponPreview(
                    _selectedMainHandId,
                    _selectedMainHandColorId,
                    _selectedOffHandId,
                    _selectedOffHandColorId);
                return;
            }

            if (_selectedSetId == _lastPreviewSetId)
                return;

            HubArmorSetSnapshot? selected = FindSet(_selectedSetId);
            if (selected == null)
                return;

            _lastPreviewSetId = _selectedSetId;
            _hubController?.SetShowcaseArmorPreview(PresentationFor(selected).ArmorBySlot);
        }

        private void ClearShowcasePreview()
        {
            _lastPreviewSetId = string.Empty;
            _lastWeaponPreviewSignature = string.Empty;
            _hubController?.SetShowcaseArmorPreview(null);
            _hubController?.SetShowcaseWeaponPreview(null, null, null, null);
        }

        private static SetPresentation PresentationFor(HubArmorSetSnapshot set)
        {
            string normalized = WireIdentifier.Normalize(set.ArmorSetId);
            if (Presentations.TryGetValue(normalized, out SetPresentation? presentation))
                return presentation;

            string tier = WireIdentifier.Normalize(set.ArmorTier);
            return tier switch
            {
                "HEAVY" => new SetPresentation(
                    "⬟",
                    "MAXIMUM PROTECTION",
                    "Complete plate armor built for maximum physical and magical protection.",
                    CompleteArmorPieces(normalized)),
                "MEDIUM" => new SetPresentation(
                    "◆",
                    "BALANCED PROTECTION",
                    "Complete layered armor that provides reliable protection without mobility penalties.",
                    CompleteArmorPieces(normalized)),
                _ => new SetPresentation(
                    "◇",
                    "UNRESTRICTED MOBILITY",
                    "Complete light armor that leaves movement and spellcasting unimpeded.",
                    CompleteArmorPieces(normalized)),
            };
        }

        private static IReadOnlyDictionary<string, string> CompleteArmorPieces(string armorSetId)
        {
            return ArmorPieces(
                ("HEAD", $"ARMOR_SET_{armorSetId}_HEAD"),
                ("SHOULDER", $"ARMOR_SET_{armorSetId}_SHOULDER"),
                ("CAPE", $"ARMOR_SET_{armorSetId}_CAPE"),
                ("CHEST", $"ARMOR_SET_{armorSetId}_CHEST"),
                ("LEGS", $"ARMOR_SET_{armorSetId}_LEGS"),
                ("BOOTS", $"ARMOR_SET_{armorSetId}_BOOTS"),
                ("GLOVES", $"ARMOR_SET_{armorSetId}_GLOVES"));
        }

        internal static IReadOnlyDictionary<string, string> ArmorAppearanceFor(string? armorSetId)
        {
            string normalized = WireIdentifier.Normalize(armorSetId);
            if (string.IsNullOrWhiteSpace(normalized))
                return new Dictionary<string, string>(StringComparer.Ordinal);

            return Presentations.TryGetValue(normalized, out SetPresentation? presentation)
                ? presentation.ArmorBySlot
                : CompleteArmorPieces(normalized);
        }

        private static string DisplayName(HubArmorSetSnapshot set)
        {
            return string.IsNullOrWhiteSpace(set.DisplayName)
                ? WireIdentifier.Normalize(set.ArmorSetId).Replace('_', ' ')
                : set.DisplayName.Trim();
        }

        private static string Percent(float value, bool positivePrefix)
        {
            int percent = Mathf.RoundToInt(value * 100f);
            if (percent == 0)
                return "0%";
            if (percent > 0)
                return $"{(positivePrefix ? "+" : string.Empty)}{percent}%";
            return $"−{Mathf.Abs(percent)}%";
        }

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

        private void BindShowcaseDrag()
        {
            if (_showcase == null)
                return;

            _showcase.RegisterCallback<PointerDownEvent>(OnShowcasePointerDown);
            _showcase.RegisterCallback<PointerMoveEvent>(OnShowcasePointerMove);
            _showcase.RegisterCallback<PointerUpEvent>(OnShowcasePointerUp);
            _showcase.RegisterCallback<PointerCaptureOutEvent>(OnShowcasePointerCaptureOut);
        }

        private void UnbindShowcaseDrag()
        {
            if (_showcase == null)
                return;

            _showcase.UnregisterCallback<PointerDownEvent>(OnShowcasePointerDown);
            _showcase.UnregisterCallback<PointerMoveEvent>(OnShowcasePointerMove);
            _showcase.UnregisterCallback<PointerUpEvent>(OnShowcasePointerUp);
            _showcase.UnregisterCallback<PointerCaptureOutEvent>(OnShowcasePointerCaptureOut);
        }

        private void OnShowcasePointerDown(PointerDownEvent evt)
        {
            if (_showcase == null || evt.button != 0)
                return;
            if (evt.target is VisualElement target
                && (target is Button || target.GetFirstAncestorOfType<Button>() != null))
            {
                return;
            }

            _draggingShowcase = true;
            _dragPointerId = evt.pointerId;
            _lastPointerPosition = new Vector2(evt.position.x, evt.position.y);
            _showcase.CapturePointer(evt.pointerId);
            evt.StopPropagation();
        }

        private void OnShowcasePointerMove(PointerMoveEvent evt)
        {
            if (!_draggingShowcase || evt.pointerId != _dragPointerId || _hubController == null)
                return;

            Vector2 position = new(evt.position.x, evt.position.y);
            float deltaX = position.x - _lastPointerPosition.x;
            _lastPointerPosition = position;
            _hubController.RotateShowcaseFromPointerDelta(deltaX);
            evt.StopPropagation();
        }

        private void OnShowcasePointerUp(PointerUpEvent evt)
        {
            if (!_draggingShowcase || evt.pointerId != _dragPointerId)
                return;

            _draggingShowcase = false;
            _dragPointerId = -1;
            _showcase?.ReleasePointer(evt.pointerId);
            evt.StopPropagation();
        }

        private void OnShowcasePointerCaptureOut(PointerCaptureOutEvent evt)
        {
            _draggingShowcase = false;
            _dragPointerId = -1;
        }
    }
}
