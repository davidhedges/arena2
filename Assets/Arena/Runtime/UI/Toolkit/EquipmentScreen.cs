#nullable enable

using Arena.Combat;
using Arena.Network;
using SpacetimeDB;
using SpacetimeDB.Types;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace Arena.UI
{
    /// <summary>
    /// UI Toolkit translation of docs/ui-prototypes/equipment. Armor choices
    /// are previews until the authoritative whole-set reducer commits.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class EquipmentScreen : MonoBehaviour, IEscapeCloseable
    {
        private const string RuntimeObjectName = "EquipmentScreenRuntime";
        private const string OpenClass = "is-open";
        private const float CatalogRefreshInterval = 0.5f;

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

        private readonly List<ArmorSetDefinition> _sets = new();
        private PanelSettings? _panelSettings;
        private VisualElement? _root;
        private ScrollView? _setList;
        private VisualElement? _showcase;
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
        private HubController? _hubController;
        private DbConnection? _connection;
        private string _tier = "LIGHT";
        private string _selectedSetId = string.Empty;
        private string _activeSetId = string.Empty;
        private string _pendingSetId = string.Empty;
        private string _lastPreviewSetId = string.Empty;
        private bool _equipPending;
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
        }

        private void OnEnable() => RuntimeUiEscapeRouter.Register(this);

        private void OnDisable() => RuntimeUiEscapeRouter.Unregister(this);

        private void OnDestroy()
        {
            UnbindShowcaseDrag();
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
            _showcase = _root.Q<VisualElement>("PlayerShowcase");
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

            BindButton("TierLight", () => SelectTier("LIGHT"));
            BindButton("TierMedium", () => SelectTier("MEDIUM"));
            BindButton("TierHeavy", () => SelectTier("HEAVY"));
            BindButton("PreviousSet", () => CycleSet(-1));
            BindButton("NextSet", () => CycleSet(1));
            BindButton("EquipButton", EquipSelectedSet);
            BindButton("BackButton", Close);
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

        private void EnsureConnection(DbConnection? connection)
        {
            if (ReferenceEquals(connection, _connection))
                return;

            if (_connection != null)
                _connection.Reducers.OnEquipArmorSet -= OnEquipArmorSet;

            _connection = connection;
            _equipPending = false;
            _pendingSetId = string.Empty;
            if (_connection != null)
                _connection.Reducers.OnEquipArmorSet += OnEquipArmorSet;
        }

        private void RefreshCatalog(bool forceSelectionFromActive = false)
        {
            DbConnection? connection = NetworkManager.Instance?.Conn;
            EnsureConnection(connection);
            if (connection == null)
            {
                RenderWaitingForCatalog();
                return;
            }

            _sets.Clear();
            _sets.AddRange(connection.Db.ArmorSetDefinition.Iter()
                .OrderBy(row => row.SortOrder)
                .ThenBy(row => row.ArmorSetId, StringComparer.Ordinal));

            ActiveArmorSet? active = connection.Identity.HasValue
                ? connection.Db.ActiveArmorSet.Owner.Find(connection.Identity.Value)
                : null;
            _activeSetId = WireIdentifier.Normalize(active?.ArmorSetId);

            if (_sets.Count == 0)
            {
                RenderWaitingForCatalog();
                return;
            }

            bool missingSelection = FindSet(_selectedSetId) == null;
            if ((forceSelectionFromActive || missingSelection) && FindSet(_activeSetId) is ArmorSetDefinition activeSet)
            {
                _selectedSetId = WireIdentifier.Normalize(activeSet.ArmorSetId);
                _tier = WireIdentifier.Normalize(activeSet.ArmorTier);
            }
            else if (missingSelection)
            {
                ArmorSetDefinition first = _sets[0];
                _selectedSetId = WireIdentifier.Normalize(first.ArmorSetId);
                _tier = WireIdentifier.Normalize(first.ArmorTier);
            }

            RenderAll();
        }

        private void RenderWaitingForCatalog()
        {
            _setList?.Clear();
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
        }

        private void RenderAll()
        {
            RenderTiers();
            RenderSetList();
            RenderDetails();
            ApplyShowcasePreview();
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

            List<ArmorSetDefinition> visibleSets = SetsForTier();
            _setList.Clear();
            if (_setCount != null)
                _setCount.text = $"{visibleSets.Count} {(visibleSets.Count == 1 ? "SET" : "SETS")}";

            foreach (ArmorSetDefinition set in visibleSets)
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
            ArmorSetDefinition? set = FindSet(_selectedSetId);
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
            ArmorSetDefinition? first = _sets.FirstOrDefault(set =>
                WireIdentifier.Normalize(set.ArmorTier) == normalized);
            if (first == null)
                return;

            _tier = normalized;
            _selectedSetId = WireIdentifier.Normalize(first.ArmorSetId);
            RenderAll();
        }

        private void SelectSet(string setId)
        {
            ArmorSetDefinition? set = FindSet(setId);
            if (set == null)
                return;

            _selectedSetId = WireIdentifier.Normalize(set.ArmorSetId);
            _tier = WireIdentifier.Normalize(set.ArmorTier);
            RenderAll();
        }

        private void CycleSet(int direction)
        {
            List<ArmorSetDefinition> visibleSets = SetsForTier();
            if (visibleSets.Count == 0)
                return;

            int currentIndex = visibleSets.FindIndex(set =>
                WireIdentifier.Normalize(set.ArmorSetId) == _selectedSetId);
            if (currentIndex < 0)
                currentIndex = 0;
            int nextIndex = (currentIndex + direction + visibleSets.Count) % visibleSets.Count;
            SelectSet(visibleSets[nextIndex].ArmorSetId);
        }

        private List<ArmorSetDefinition> SetsForTier()
        {
            return _sets.Where(set => WireIdentifier.Normalize(set.ArmorTier) == _tier).ToList();
        }

        private ArmorSetDefinition? FindSet(string? setId)
        {
            string normalized = WireIdentifier.Normalize(setId);
            return _sets.FirstOrDefault(set =>
                WireIdentifier.Normalize(set.ArmorSetId) == normalized);
        }

        private void EquipSelectedSet()
        {
            ArmorSetDefinition? selected = FindSet(_selectedSetId);
            if (selected == null)
                return;
            if (_selectedSetId == _activeSetId)
            {
                ShowToast($"{DisplayName(selected)} is already equipped.");
                return;
            }

            EnsureConnection(NetworkManager.Instance?.Conn);
            if (_connection == null || !_connection.Identity.HasValue || _equipPending)
            {
                ShowToast("Connect to equip this armor set.");
                return;
            }

            _pendingSetId = _selectedSetId;
            _equipPending = true;
            RenderDetails();
            _connection.Reducers.EquipArmorSet(_pendingSetId);
        }

        private void OnEquipArmorSet(ReducerEventContext context, string armorSetId)
        {
            string normalizedSetId = WireIdentifier.Normalize(armorSetId);
            if (_connection == null
                || !_connection.Identity.HasValue
                || context.Event.CallerIdentity != _connection.Identity.Value
                || !_equipPending
                || normalizedSetId != _pendingSetId)
            {
                return;
            }

            _equipPending = false;
            _pendingSetId = string.Empty;
            if (context.Event.Status is Status.Committed)
            {
                _activeSetId = normalizedSetId;
                _nextCatalogRefresh = 0f;
                RenderAll();
                ArmorSetDefinition? equipped = FindSet(normalizedSetId);
                ShowToast($"{(equipped == null ? "Armor set" : DisplayName(equipped))} equipped as a complete set.");
                return;
            }

            string reason = context.Event.Status switch
            {
                Status.Failed(var failure) => failure,
                Status.OutOfEnergy(var _) => "server was out of reducer energy",
                _ => "server did not commit the armor set",
            };
            Debug.LogError($"[{nameof(EquipmentScreen)}] Equipping armor set failed: {reason}");
            RenderDetails();
            ShowToast($"Could not equip armor set: {reason}");
        }

        private void ApplyShowcasePreview()
        {
            if (!_open || _selectedSetId == _lastPreviewSetId)
                return;

            ArmorSetDefinition? selected = FindSet(_selectedSetId);
            if (selected == null)
                return;

            _lastPreviewSetId = _selectedSetId;
            _hubController?.SetShowcaseArmorPreview(PresentationFor(selected).ArmorBySlot);
        }

        private void ClearShowcasePreview()
        {
            _lastPreviewSetId = string.Empty;
            _hubController?.SetShowcaseArmorPreview(null);
        }

        private static SetPresentation PresentationFor(ArmorSetDefinition set)
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

        private static string DisplayName(ArmorSetDefinition set)
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
