#nullable enable

using System;
using System.Collections.Generic;
using System.Text;
using Arena.Combat;
using Arena.Entity;
using Arena.Interaction;
using Arena.Network;
using SpacetimeDB;
using SpacetimeDB.Types;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif
using UnityEngine.UIElements;

namespace Arena.UI
{
    /// <summary>
    /// Inventory + Character/Equipment + Loot windows (UI Toolkit). Visual
    /// source of truth: docs/ui-prototypes/inventory translated to
    /// Inventory.uxml/.uss; behavior ported from the retired uGUI
    /// InventoryController (drag/equip/loot reducers, stats aggregation,
    /// tooltips). Grid windows size themselves from server container data.
    /// </summary>
    [DefaultExecutionOrder(60)]
    public sealed class InventoryScreen : MonoBehaviour, IEscapeCloseable
    {
        private const float RefreshIntervalSeconds = 0.12f;
        private const float LootPickScreenRadius = 82f;
        private const int CellSize = 68;
        private const float DragThresholdSq = 36f;
        private const string OpenClass = "is-open";

        // Owner decisions 2026-07-17: no CAPE, no OFF_HAND (sword & shield is
        // one item). Do not copy the old 13-slot array.
        private static readonly string[] EquipmentSlotIds =
        {
            "HEAD", "SHOULDER", "AMULET", "CHEST", "GLOVES", "LEGS", "BOOTS",
            "RING_1", "RING_2", "MAIN_HAND", "SPELLBOOK",
        };

        private static InventoryScreen? s_instance;

        private PanelSettings? _panelSettings;
        private VisualElement? _root;
        private VisualElement? _charWindow;
        private VisualElement? _invWindow;
        private VisualElement? _lootWindow;
        private Label? _lootTitle;
        private VisualElement? _statsList;
        private VisualElement? _tooltip;
        private Label? _ttName;
        private Label? _ttRarity;
        private Label? _ttLine;
        private VisualElement? _ttStats;
        private Label? _ttFootnote;
        private VisualElement? _dragGhost;
        private VisualElement? _dragGhostIcon;
        private Label? _dragGhostLabel;

        private sealed class GridView
        {
            public string Context = string.Empty;
            public string ContainerId = string.Empty;
            public int Cols;
            public int Rows;
            public VisualElement Window = null!;
            public VisualElement Grid = null!;
            public VisualElement Cells = null!;
            public VisualElement Items = null!;
            public readonly List<VisualElement> ItemPool = new();
            public readonly Dictionary<(int X, int Y), ItemRef> Anchors = new();
        }

        private sealed class ItemRef
        {
            public string Context = string.Empty;
            public string ContainerId = string.Empty;
            public uint X;
            public uint Y;
            public string ItemInstanceId = string.Empty;
            public uint Quantity;
            public ItemDefinition? Definition;
        }

        private sealed class SlotState
        {
            public string SlotId = string.Empty;
            public string ItemInstanceId = string.Empty;
            public ItemDefinition? Definition;
            public bool HasItem => !string.IsNullOrWhiteSpace(ItemInstanceId);
        }

        private readonly struct DragPayload
        {
            public readonly string SourceContext;
            public readonly string SourceContainerId;
            public readonly string SourceEquipmentSlot;
            public readonly string ItemInstanceId;
            public readonly uint Quantity;
            public readonly string DisplayName;
            public readonly string IconId;

            public DragPayload(string context, string containerId, string equipmentSlot,
                string itemInstanceId, uint quantity, string displayName, string iconId)
            {
                SourceContext = context;
                SourceContainerId = containerId;
                SourceEquipmentSlot = equipmentSlot;
                ItemInstanceId = itemInstanceId;
                Quantity = quantity;
                DisplayName = displayName;
                IconId = iconId;
            }

            public bool IsFromEquipment => !string.IsNullOrWhiteSpace(SourceEquipmentSlot);
        }

        private sealed class DragState
        {
            public DragPayload Payload;
            public VisualElement Source = null!;
            public int PointerId;
            public Vector2 Origin;
            public bool Started;
        }

        private GridView? _bagView;
        private GridView? _lootView;
        private readonly Dictionary<string, VisualElement> _equipSlots = new(StringComparer.Ordinal);
        private bool _open;
        private bool _lootVisible;
        private string? _openLootContainerId;
        private Identity? _pendingLootNpc;
        private float _pendingLootUntil;
        private float _nextRefreshTime;
        private DragState? _drag;
        private string _statsSignature = "\0unset";

        public int EscapeClosePriority => 100;
        public bool IsEscapeCloseable => _open;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (FindAnyObjectByType<InventoryScreen>() != null)
                return;
            if (!ArenaRuntimeSceneGate.ShouldRunArenaRuntimeInActiveScene())
                return;

            GameObject go = new("InventoryScreen");
            DontDestroyOnLoad(go);
            go.AddComponent<InventoryScreen>();
        }

        private void Awake()
        {
            s_instance = this;
            RuntimeUiEventSystem.Ensure();
            BuildUi();
        }

        private void OnEnable() => RuntimeUiEscapeRouter.Register(this);

        private void OnDisable() => RuntimeUiEscapeRouter.Unregister(this);

        private void OnDestroy()
        {
            if (s_instance == this)
                s_instance = null;
            if (_panelSettings != null)
                Destroy(_panelSettings);
        }

        private void Update()
        {
            if (!ArenaRuntimeSceneGate.ShouldRunArenaRuntimeInActiveScene())
            {
                if (_open)
                    SetOpen(false, instant: true);
                return;
            }

            if (WasInventoryTogglePressed())
                SetOpen(!_open);

            ResolvePendingLootContainer();

            if (Time.unscaledTime >= _nextRefreshTime && _drag == null)
            {
                _nextRefreshTime = Time.unscaledTime + RefreshIntervalSeconds;
                if (_open)
                    RefreshAll();
            }
        }

        public bool TryCloseForEscape()
        {
            if (!_open)
                return false;

            CancelDrag();
            SetOpen(false);
            return true;
        }

        private void SetOpen(bool open, bool instant = false)
        {
            if (_root == null || _charWindow == null || _invWindow == null)
                return;

            _open = open;
            HideTooltip();
            if (open)
            {
                if (_panelSettings != null)
                    _panelSettings.sortingOrder = RuntimeUiLayer.NextSortingOrder();
                RefreshAll();

                _root.AddToClassList(OpenClass);
                if (instant)
                {
                    _charWindow.AddToClassList(OpenClass);
                    _invWindow.AddToClassList(OpenClass);
                }
                else
                {
                    _root.schedule.Execute(() =>
                    {
                        if (!_open)
                            return;
                        _charWindow.AddToClassList(OpenClass);
                        _invWindow.AddToClassList(OpenClass);
                    }).ExecuteLater(16);
                }
            }
            else
            {
                CancelDrag();
                SetLootVisible(false);
                _openLootContainerId = null;
                _pendingLootNpc = null;
                _charWindow.RemoveFromClassList(OpenClass);
                _invWindow.RemoveFromClassList(OpenClass);
                if (instant)
                    _root.RemoveFromClassList(OpenClass);
                else
                    _root.schedule.Execute(() =>
                    {
                        if (!_open)
                            _root.RemoveFromClassList(OpenClass);
                    }).ExecuteLater(130);
            }
        }

        private void SetLootVisible(bool visible)
        {
            if (_lootWindow == null)
                return;

            _lootVisible = visible;
            _lootWindow.EnableInClassList(OpenClass, visible);
        }

        // ---- construction --------------------------------------------------

        private void BuildUi()
        {
            UIDocument document = ArenaPanel.CreateDocument(gameObject, "UI/Toolkit/Inventory", 42f);
            _panelSettings = document.panelSettings;

            VisualElement tree = document.rootVisualElement;
            _root = tree.Q<VisualElement>("InventoryScreen");
            if (_root == null)
            {
                Debug.LogError("InventoryScreen: Inventory.uxml did not load or is missing its root element.");
                return;
            }

            _charWindow = _root.Q<VisualElement>("CharacterWindow");
            _invWindow = _root.Q<VisualElement>("InventoryWindow");
            _lootWindow = _root.Q<VisualElement>("LootWindow");
            _lootTitle = _root.Q<Label>("LootTitle");
            _statsList = _root.Q<VisualElement>("StatsList");
            _tooltip = _root.Q<VisualElement>("ItemTooltip");
            _ttName = _root.Q<Label>("TtName");
            _ttRarity = _root.Q<Label>("TtRarity");
            _ttLine = _root.Q<Label>("TtLine");
            _ttStats = _root.Q<VisualElement>("TtStats");
            _ttFootnote = _root.Q<Label>("TtFootnote");
            _dragGhost = _root.Q<VisualElement>("DragGhost");
            _dragGhostIcon = _root.Q<VisualElement>("DragGhostIcon");
            _dragGhostLabel = _root.Q<Label>("DragGhostLabel");
            _tooltip!.style.display = DisplayStyle.None;
            _dragGhost!.style.display = DisplayStyle.None;

            _bagView = BuildGridView("Inventory", _invWindow!, "InventoryGrid", "InventoryCells", "InventoryItems");
            _lootView = BuildGridView("Loot", _lootWindow!, "LootGrid", "LootCells", "LootItems");

            _equipSlots.Clear();
            foreach (string slotId in EquipmentSlotIds)
            {
                VisualElement? slot = _root.Q<VisualElement>($"Slot_{slotId}");
                if (slot == null)
                {
                    Debug.LogError($"InventoryScreen: Inventory.uxml is missing Slot_{slotId}.");
                    continue;
                }

                slot.userData = new SlotState { SlotId = slotId };
                RegisterEquipSlotCallbacks(slot);
                _equipSlots[slotId] = slot;
            }
        }

        private GridView BuildGridView(string context, VisualElement window, string gridName, string cellsName, string itemsName)
        {
            GridView view = new()
            {
                Context = context,
                Window = window,
                Grid = window.Q<VisualElement>(gridName)!,
                Cells = window.Q<VisualElement>(cellsName)!,
                Items = window.Q<VisualElement>(itemsName)!,
            };
            return view;
        }

        // ---- refresh from server state --------------------------------------

        private void RefreshAll()
        {
            DbConnection? conn = NetworkManager.Instance?.Conn;
            if (_bagView == null || _lootView == null)
                return;

            if (conn == null)
            {
                PopulateGrid(null, _bagView, null, 10, 4);
                SetLootVisible(false);
                RefreshEquipment(null);
                return;
            }

            InventoryContainer? bag = FindPlayerBag(conn);
            if (bag != null)
                PopulateGrid(conn, _bagView, bag, (int)bag.Width, (int)bag.Height);
            else
                PopulateGrid(conn, _bagView, null, 10, 4);

            RefreshEquipment(conn);

            InventoryContainer? loot = FindContainer(conn, _openLootContainerId);
            bool showLoot = loot != null || _pendingLootNpc != null;
            SetLootVisible(showLoot);
            if (loot != null)
            {
                if (_lootTitle != null)
                    _lootTitle.text = string.Equals(loot.ContainerKind, "CORPSE", StringComparison.OrdinalIgnoreCase) ? "CORPSE" : "LOOT";
                PopulateGrid(conn, _lootView, loot, (int)loot.Width, (int)loot.Height);
            }
            else if (showLoot)
            {
                if (_lootTitle != null)
                    _lootTitle.text = "LOOT";
                PopulateGrid(conn, _lootView, null, 4, 4);
            }
        }

        private void PopulateGrid(DbConnection? conn, GridView view, InventoryContainer? container, int cols, int rows)
        {
            cols = Mathf.Max(1, cols);
            rows = Mathf.Max(1, rows);
            view.ContainerId = container?.ContainerId ?? string.Empty;
            if (view.Cols != cols || view.Rows != rows)
                SizeGridWindow(view, cols, rows);

            view.Anchors.Clear();
            int used = 0;
            if (conn != null && container != null)
            {
                foreach (InventorySlot slot in conn.Db.InventorySlot.Iter())
                {
                    if (!string.Equals(slot.ContainerId, container.ContainerId, StringComparison.Ordinal))
                        continue;

                    ItemInstance? item = conn.Db.ItemInstance.ItemInstanceId.Find(slot.ItemInstanceId);
                    if (item == null)
                        continue;

                    ItemDefinition? definition = conn.Db.ItemDefinition.ItemDefId.Find(item.ItemDefId);
                    ItemRef itemRef = new()
                    {
                        Context = view.Context,
                        ContainerId = container.ContainerId,
                        X = slot.X,
                        Y = slot.Y,
                        ItemInstanceId = item.ItemInstanceId,
                        Quantity = item.Quantity,
                        Definition = definition,
                    };
                    view.Anchors[((int)slot.X, (int)slot.Y)] = itemRef;

                    VisualElement element = used < view.ItemPool.Count ? view.ItemPool[used] : CreateGridItem(view);
                    used++;
                    ConfigureGridItem(element, itemRef, Mathf.Max(1, (int)slot.Width), Mathf.Max(1, (int)slot.Height));
                }
            }

            for (int i = used; i < view.ItemPool.Count; i++)
                view.ItemPool[i].style.display = DisplayStyle.None;
        }

        private void SizeGridWindow(GridView view, int cols, int rows)
        {
            view.Cols = cols;
            view.Rows = rows;

            int gridWidth = cols * CellSize;
            int gridHeight = rows * CellSize;
            view.Grid.style.width = gridWidth;
            view.Grid.style.height = gridHeight;
            view.Cells.style.width = cols * CellSize;

            // Preserve the existing outer window dimensions after removing the
            // grid's old 10px perimeter strips.
            view.Window.style.width = gridWidth + 62;
            view.Window.style.height = 44 + 12 + gridHeight + 36;

            int total = cols * rows;
            while (view.Cells.childCount < total)
            {
                VisualElement cell = new();
                cell.AddToClassList("grid-cell");
                cell.pickingMode = PickingMode.Ignore;
                view.Cells.Add(cell);
            }
            for (int i = 0; i < view.Cells.childCount; i++)
            {
                VisualElement cell = view.Cells[i];
                bool visible = i < total;
                cell.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
                cell.EnableInClassList("grid-cell--right", visible && i % cols == cols - 1);
                cell.EnableInClassList("grid-cell--bottom", visible && i / cols == rows - 1);
            }
        }

        private VisualElement CreateGridItem(GridView view)
        {
            VisualElement item = new();
            item.AddToClassList("grid-item");

            VisualElement icon = new();
            icon.AddToClassList("item-icon");
            icon.pickingMode = PickingMode.Ignore;
            item.Add(icon);

            VisualElement glow = new();
            glow.AddToClassList("slot-glow");
            glow.pickingMode = PickingMode.Ignore;
            item.Add(glow);

            Label qty = new();
            qty.AddToClassList("qty-badge");
            qty.pickingMode = PickingMode.Ignore;
            item.Add(qty);

            RegisterItemCallbacks(item);
            view.Items.Add(item);
            view.ItemPool.Add(item);
            return item;
        }

        private void ConfigureGridItem(VisualElement element, ItemRef itemRef, int width, int height)
        {
            element.userData = itemRef;
            element.style.display = DisplayStyle.Flex;
            element.style.left = (int)itemRef.X * CellSize;
            element.style.top = (int)itemRef.Y * CellSize;
            element.style.width = width * CellSize;
            element.style.height = height * CellSize;

            VisualElement icon = element[0];
            VisualElement glow = element[1];
            Label qty = (Label)element[2];

            Sprite? sprite = ItemIconResolver.Resolve(itemRef.Definition);
            icon.style.display = sprite != null ? DisplayStyle.Flex : DisplayStyle.None;
            if (sprite != null)
                icon.style.backgroundImage = new StyleBackground(sprite);

            ApplyRarityGlow(glow, itemRef.Definition, hasItem: true);
            qty.text = itemRef.Quantity > 1 ? itemRef.Quantity.ToString() : string.Empty;
        }

        private static void ApplyRarityGlow(VisualElement glow, ItemDefinition? definition, bool hasItem)
        {
            foreach (string rarity in new[] { "common", "uncommon", "rare", "epic", "legendary", "red" })
                glow.RemoveFromClassList($"rarity-{rarity}");

            if (!hasItem)
            {
                glow.style.display = DisplayStyle.None;
                return;
            }

            glow.style.display = DisplayStyle.Flex;
            string normalized = Normalize(definition?.Rarity ?? string.Empty).ToLowerInvariant();
            glow.AddToClassList(normalized switch
            {
                "uncommon" => "rarity-uncommon",
                "rare" => "rarity-rare",
                "epic" => "rarity-epic",
                "legendary" => "rarity-legendary",
                _ => "rarity-common",
            });
        }

        private void RefreshEquipment(DbConnection? conn)
        {
            EquipmentLoadout? loadout = conn == null ? null : FindEquipmentLoadout(conn);
            List<(ItemInstance Item, ItemDefinition? Definition)> equippedItems = new();

            foreach (string slotId in EquipmentSlotIds)
            {
                if (!_equipSlots.TryGetValue(slotId, out VisualElement? slot))
                    continue;

                SlotState state = (SlotState)slot.userData;
                string? itemId = loadout == null ? null : EquipmentItemId(loadout, slotId);
                ItemInstance? item = conn == null || string.IsNullOrWhiteSpace(itemId)
                    ? null
                    : conn.Db.ItemInstance.ItemInstanceId.Find(itemId);
                ItemDefinition? definition = item == null || conn == null
                    ? null
                    : conn.Db.ItemDefinition.ItemDefId.Find(item.ItemDefId);
                if (item != null)
                    equippedItems.Add((item, definition));

                state.ItemInstanceId = item?.ItemInstanceId ?? string.Empty;
                state.Definition = definition;

                VisualElement icon = slot.Q<VisualElement>(className: "item-icon")!;
                VisualElement glow = slot.Q<VisualElement>(className: "slot-glow")!;
                Label label = slot.Q<Label>(className: "slot-label")!;

                Sprite? sprite = item == null ? null : ItemIconResolver.Resolve(definition);
                icon.style.display = sprite != null ? DisplayStyle.Flex : DisplayStyle.None;
                if (sprite != null)
                    icon.style.backgroundImage = new StyleBackground(sprite);
                ApplyRarityGlow(glow, definition, item != null);
                label.style.display = item == null ? DisplayStyle.Flex : DisplayStyle.None;
            }

            if (conn != null)
                RefreshCharacterStats(conn, equippedItems);
        }

        private void RefreshCharacterStats(DbConnection conn, List<(ItemInstance Item, ItemDefinition? Definition)> equippedItems)
        {
            if (_statsList == null)
                return;

            Dictionary<string, float> totals = new(StringComparer.Ordinal);
            Dictionary<string, string> labels = new(StringComparer.Ordinal);

            foreach ((ItemInstance item, ItemDefinition? definition) in equippedItems)
            {
                if (definition != null && definition.PhysicalResistance > 0.0001f)
                    Accumulate(totals, labels, "PHYSICAL_RESISTANCE", "Physical Resistance", definition.PhysicalResistance);

                foreach (ItemAffixInstance affix in conn.Db.ItemAffixInstance.ItemInstanceId.Filter(item.ItemInstanceId))
                {
                    string kind = NormalizeModifier(affix.ModifierKind);
                    if (string.IsNullOrEmpty(kind))
                        continue;

                    string label = IsAllocatedStatModifier(kind)
                        ? FormatTooltipTitle(kind)
                        : ResolveAffixLabel(conn, affix, kind);
                    Accumulate(totals, labels, kind, label, affix.Value);
                }
            }

            List<string> kinds = new(totals.Keys);
            kinds.Sort(StringComparer.Ordinal);

            StringBuilder signature = new();
            foreach (string kind in kinds)
                signature.Append(kind).Append('=').Append(totals[kind].ToString("0.###")).Append(';');

            string next = signature.ToString();
            if (string.Equals(next, _statsSignature, StringComparison.Ordinal))
                return;

            _statsSignature = next;
            _statsList.Clear();

            if (kinds.Count == 0)
            {
                Label empty = new("No bonuses from equipment.");
                empty.AddToClassList("stat-empty");
                _statsList.Add(empty);
                return;
            }

            foreach (string kind in kinds)
            {
                VisualElement row = new();
                row.AddToClassList("stat-row");
                Label label = new(labels[kind]);
                label.AddToClassList("stat-label");
                Label value = new(FormatAggregateValue(kind, totals[kind]));
                value.AddToClassList("stat-value");
                row.Add(label);
                row.Add(value);
                _statsList.Add(row);
            }
        }

        // ---- pointer interaction --------------------------------------------

        private void RegisterItemCallbacks(VisualElement item)
        {
            item.RegisterCallback<PointerDownEvent>(evt => OnItemPointerDown(item, evt));
            item.RegisterCallback<PointerMoveEvent>(evt => OnDragPointerMove(item, evt));
            item.RegisterCallback<PointerUpEvent>(evt => OnItemPointerUp(item, evt));
            item.RegisterCallback<PointerEnterEvent>(evt => OnItemHoverEnter(item, evt));
            item.RegisterCallback<PointerLeaveEvent>(_ => HideTooltip());
        }

        private void RegisterEquipSlotCallbacks(VisualElement slot)
        {
            slot.RegisterCallback<PointerDownEvent>(evt => OnSlotPointerDown(slot, evt));
            slot.RegisterCallback<PointerMoveEvent>(evt => OnDragPointerMove(slot, evt));
            slot.RegisterCallback<PointerUpEvent>(evt => OnSlotPointerUp(slot, evt));
            slot.RegisterCallback<PointerEnterEvent>(evt => OnSlotHoverEnter(slot, evt));
            slot.RegisterCallback<PointerLeaveEvent>(_ => HideTooltip());
        }

        private void OnItemPointerDown(VisualElement element, PointerDownEvent evt)
        {
            if (element.userData is not ItemRef itemRef)
                return;

            if (evt.button == 1)
            {
                HandleItemRightClick(itemRef);
                evt.StopPropagation();
                return;
            }

            if (evt.button != 0)
                return;

            _drag = new DragState
            {
                Payload = new DragPayload(itemRef.Context, itemRef.ContainerId, string.Empty,
                    itemRef.ItemInstanceId, itemRef.Quantity,
                    itemRef.Definition?.DisplayName ?? itemRef.ItemInstanceId,
                    itemRef.Definition?.IconId ?? string.Empty),
                Source = element,
                PointerId = evt.pointerId,
                Origin = evt.position,
            };
            element.CapturePointer(evt.pointerId);
            evt.StopPropagation();
        }

        private void OnSlotPointerDown(VisualElement slot, PointerDownEvent evt)
        {
            SlotState state = (SlotState)slot.userData;
            if (evt.button == 1)
            {
                HandleEquipmentRightClick(state);
                evt.StopPropagation();
                return;
            }

            if (evt.button != 0 || !state.HasItem)
                return;

            _drag = new DragState
            {
                Payload = new DragPayload("Equipment", string.Empty, state.SlotId,
                    state.ItemInstanceId, 1,
                    state.Definition?.DisplayName ?? state.ItemInstanceId,
                    state.Definition?.IconId ?? string.Empty),
                Source = slot,
                PointerId = evt.pointerId,
                Origin = evt.position,
            };
            slot.CapturePointer(evt.pointerId);
            evt.StopPropagation();
        }

        private void OnDragPointerMove(VisualElement element, PointerMoveEvent evt)
        {
            if (_drag == null || _drag.Source != element || !element.HasPointerCapture(_drag.PointerId))
                return;

            if (!_drag.Started)
            {
                if (((Vector2)evt.position - _drag.Origin).sqrMagnitude < DragThresholdSq)
                    return;

                _drag.Started = true;
                HideTooltip();
                ShowDragGhost(_drag.Payload);
            }

            MoveDragGhost(evt.position);
        }

        private void OnItemPointerUp(VisualElement element, PointerUpEvent evt)
        {
            if (_drag == null || _drag.Source != element)
                return;

            element.ReleasePointer(_drag.PointerId);
            DragState drag = _drag;
            _drag = null;
            HideDragGhost();

            if (drag.Started)
            {
                ResolveDrop(drag.Payload, evt.position);
                RefreshAll();
                return;
            }

            if (evt.button == 0 && element.userData is ItemRef itemRef && IsSpellbook(itemRef.Definition))
                SpellbookPanel.Open(itemRef.ItemInstanceId, itemRef.Definition?.DisplayName ?? "Spellbook");
        }

        private void OnSlotPointerUp(VisualElement slot, PointerUpEvent evt)
        {
            if (_drag == null || _drag.Source != slot)
                return;

            slot.ReleasePointer(_drag.PointerId);
            DragState drag = _drag;
            _drag = null;
            HideDragGhost();

            SlotState state = (SlotState)slot.userData;
            if (drag.Started)
            {
                ResolveDrop(drag.Payload, evt.position);
                RefreshAll();
                return;
            }

            if (evt.button == 0 && state.HasItem && IsSpellbook(state.Definition))
                SpellbookPanel.Open(state.ItemInstanceId, state.Definition?.DisplayName ?? "Spellbook");
        }

        private void CancelDrag()
        {
            if (_drag != null && _drag.Source.HasPointerCapture(_drag.PointerId))
                _drag.Source.ReleasePointer(_drag.PointerId);
            _drag = null;
            HideDragGhost();
        }

        // ---- drop resolution -------------------------------------------------

        private void ResolveDrop(DragPayload payload, Vector2 position)
        {
            DbConnection? conn = NetworkManager.Instance?.Conn;
            if (conn == null)
                return;

            foreach ((string slotId, VisualElement slot) in _equipSlots)
            {
                if (!slot.worldBound.Contains(position))
                    continue;

                if (!payload.IsFromEquipment
                    && string.Equals(payload.SourceContext, "Inventory", StringComparison.Ordinal))
                    conn.Reducers.EquipItem(payload.ItemInstanceId, slotId);
                return;
            }

            foreach (GridView? view in new[] { _bagView, _lootView })
            {
                if (view == null || string.IsNullOrEmpty(view.ContainerId))
                    continue;
                if (view == _lootView && !_lootVisible)
                    continue;
                if (!view.Cells.worldBound.Contains(position))
                    continue;

                Vector2 local = position - view.Cells.worldBound.position;
                int x = Mathf.Clamp((int)(local.x / CellSize), 0, view.Cols - 1);
                int y = Mathf.Clamp((int)(local.y / CellSize), 0, view.Rows - 1);
                HandleGridDrop(conn, payload, view, x, y);
                return;
            }
        }

        private void HandleGridDrop(DbConnection conn, DragPayload payload, GridView view, int x, int y)
        {
            view.Anchors.TryGetValue((x, y), out ItemRef? occupant);

            if (payload.IsFromEquipment)
            {
                if (occupant == null && string.Equals(view.Context, "Inventory", StringComparison.Ordinal))
                    conn.Reducers.UnequipItem(payload.SourceEquipmentSlot, view.ContainerId, (uint)x, (uint)y);
                return;
            }

            if (string.IsNullOrWhiteSpace(payload.SourceContainerId))
                return;

            if (occupant != null && !string.Equals(occupant.ItemInstanceId, payload.ItemInstanceId, StringComparison.Ordinal))
            {
                conn.Reducers.MergeStack(payload.ItemInstanceId, occupant.ItemInstanceId);
                return;
            }

            conn.Reducers.MoveItem(payload.SourceContainerId, payload.ItemInstanceId, view.ContainerId, (uint)x, (uint)y, payload.Quantity);
        }

        // ---- click actions ----------------------------------------------------

        private void HandleItemRightClick(ItemRef itemRef)
        {
            DbConnection? conn = NetworkManager.Instance?.Conn;
            if (conn == null)
                return;

            if (string.Equals(itemRef.Context, "Loot", StringComparison.Ordinal))
            {
                conn.Reducers.QuickLoot(itemRef.ContainerId, itemRef.ItemInstanceId);
                return;
            }

            if (IsConsumable(itemRef.Definition))
            {
                conn.Reducers.ConsumeItem(itemRef.ItemInstanceId);
                return;
            }

            if (itemRef.Definition != null && TryResolveEquipSlot(conn, itemRef.Definition, out string targetSlot))
                conn.Reducers.EquipItem(itemRef.ItemInstanceId, targetSlot);
        }

        private void HandleEquipmentRightClick(SlotState state)
        {
            if (!state.HasItem)
                return;

            DbConnection? conn = NetworkManager.Instance?.Conn;
            if (conn == null)
                return;

            InventoryContainer? bag = FindPlayerBag(conn);
            if (bag == null)
                return;

            uint itemWidth = state.Definition?.Width ?? 1;
            uint itemHeight = state.Definition?.Height ?? 1;
            if (TryFindFirstFreePosition(conn, bag, itemWidth, itemHeight, out uint x, out uint y))
                conn.Reducers.UnequipItem(state.SlotId, bag.ContainerId, x, y);
        }

        private bool TryResolveEquipSlot(DbConnection conn, ItemDefinition definition, out string targetSlot)
        {
            targetSlot = string.Empty;
            string equipSlot = Normalize(definition.EquipSlot);
            if (string.IsNullOrEmpty(equipSlot))
                return false;

            if (string.Equals(definition.ItemKind, "WEAPON", StringComparison.OrdinalIgnoreCase))
            {
                // Sword & shield is one equippable item: every weapon is main hand.
                targetSlot = "MAIN_HAND";
                return true;
            }

            if (equipSlot == "RING")
            {
                EquipmentLoadout? equipment = FindEquipmentLoadout(conn);
                targetSlot = equipment == null || string.IsNullOrWhiteSpace(equipment.Ring1ItemId) ? "RING_1" : "RING_2";
                return true;
            }

            targetSlot = equipSlot;
            return true;
        }

        // ---- loot -------------------------------------------------------------

        public static bool TryGetLootCandidate(
            Camera camera,
            Vector2 screenPosition,
            out WorldInteractionCandidate candidate)
        {
            candidate = default;
            InventoryScreen? screen = s_instance;
            if (screen == null)
                return false;

            NpcEntity? npc = FindDeadNpcUnderCursor(camera, screenPosition);
            if (npc == null)
                return false;

            Vector3 interactionPoint = npc.GetRenderPosition()
                + Vector3.up * Mathf.Clamp(npc.HitHeight * 0.45f, 0.35f, 1.4f);
            float screenDepth = camera.WorldToScreenPoint(interactionPoint).z;
            candidate = new WorldInteractionCandidate(
                WorldInteractionCandidateKind.CorpseLoot,
                npc.Identity.ToString(),
                "LOOT",
                interactionPoint,
                screenDepth,
                WorldInteractionArbitration.CorpseLootPriority,
                0f,
                () => screen.TryOpenLoot(npc));
            return true;
        }

        private bool TryOpenLoot(NpcEntity npc)
        {
            DbConnection? conn = NetworkManager.Instance?.Conn;
            if (conn == null || npc.IsAlive || npc.IsDestroyed)
                return false;

            if (!_open)
                SetOpen(true);
            _pendingLootNpc = npc.Identity;
            _pendingLootUntil = Time.unscaledTime + 1.5f;
            conn.Reducers.OpenLootNpc(npc.Identity);
            ResolvePendingLootContainer();
            RefreshAll();
            return true;
        }

        private static NpcEntity? FindDeadNpcUnderCursor(
            Camera camera,
            Vector2 mousePosition)
        {
            EntityRegistry? registry = EntityRegistry.Instance;
            if (registry == null)
                return null;

            NpcEntity? best = null;
            float bestDistance = float.MaxValue;

            foreach (NpcEntity npc in registry.AllNpcs)
            {
                if (npc.IsAlive || npc.IsDestroyed)
                    continue;

                Vector3 world = npc.GetRenderPosition() + Vector3.up * Mathf.Clamp(npc.HitHeight * 0.45f, 0.35f, 1.4f);
                Vector3 screen = camera.WorldToScreenPoint(world);
                if (screen.z <= 0f)
                    continue;

                float distance = Vector2.Distance(mousePosition, new Vector2(screen.x, screen.y));
                if (distance > LootPickScreenRadius || distance >= bestDistance)
                    continue;

                best = npc;
                bestDistance = distance;
            }

            return best;
        }

        private void ResolvePendingLootContainer()
        {
            if (_pendingLootNpc == null)
                return;

            DbConnection? conn = NetworkManager.Instance?.Conn;
            if (conn == null)
                return;

            InventoryContainer? container = FindLootContainerForNpc(conn, _pendingLootNpc.Value);
            if (container != null)
            {
                _openLootContainerId = container.ContainerId;
                _pendingLootNpc = null;
                return;
            }

            if (Time.unscaledTime > _pendingLootUntil)
                _pendingLootNpc = null;
        }

        // ---- tooltip -----------------------------------------------------------

        private void OnItemHoverEnter(VisualElement element, PointerEnterEvent evt)
        {
            if (_drag != null || element.userData is not ItemRef itemRef)
                return;

            ShowTooltip(itemRef.ItemInstanceId, itemRef.Definition,
                BuildCellFootnote(itemRef.Context, itemRef.Definition), evt.position);
        }

        private void OnSlotHoverEnter(VisualElement slot, PointerEnterEvent evt)
        {
            SlotState state = (SlotState)slot.userData;
            if (_drag != null || !state.HasItem)
                return;

            ShowTooltip(state.ItemInstanceId, state.Definition, "Right-click to unequip - Drag to bag", evt.position);
        }

        private void ShowTooltip(string itemInstanceId, ItemDefinition? definition, string footnote, Vector2 position)
        {
            DbConnection? conn = NetworkManager.Instance?.Conn;
            ItemInstance? item = conn?.Db.ItemInstance.ItemInstanceId.Find(itemInstanceId);
            if (_tooltip == null || _root == null || item == null)
                return;

            if (_ttName != null)
                _ttName.text = !string.IsNullOrWhiteSpace(definition?.DisplayName) ? definition.DisplayName : itemInstanceId;

            string rarity = Normalize(definition?.Rarity ?? string.Empty);
            if (_ttRarity != null)
            {
                foreach (string r in new[] { "common", "uncommon", "rare", "epic", "legendary" })
                    _ttRarity.RemoveFromClassList($"tt-rarity--{r}");
                _ttRarity.text = FormatTooltipTitle(rarity);
                string rarityClass = rarity.ToLowerInvariant() switch
                {
                    "uncommon" => "uncommon",
                    "rare" => "rare",
                    "epic" => "epic",
                    "legendary" => "legendary",
                    _ => "common",
                };
                _ttRarity.AddToClassList($"tt-rarity--{rarityClass}");
                _ttRarity.style.display = string.IsNullOrEmpty(rarity) ? DisplayStyle.None : DisplayStyle.Flex;
            }

            if (_ttLine != null)
            {
                string line = BuildItemLine(conn, item, definition);
                _ttLine.text = line;
                _ttLine.style.display = string.IsNullOrEmpty(line) ? DisplayStyle.None : DisplayStyle.Flex;
            }

            if (_ttStats != null)
            {
                _ttStats.Clear();
                foreach ((string label, string value) in BuildItemStats(conn, item, definition))
                {
                    VisualElement row = new();
                    row.AddToClassList("tt-stat-row");
                    Label statLabel = new(label);
                    statLabel.AddToClassList("tt-stat-label");
                    Label statValue = new(value);
                    statValue.AddToClassList("tt-stat-value");
                    row.Add(statLabel);
                    row.Add(statValue);
                    _ttStats.Add(row);
                }
            }

            if (_ttFootnote != null)
            {
                _ttFootnote.text = footnote;
                _ttFootnote.style.display = string.IsNullOrEmpty(footnote) ? DisplayStyle.None : DisplayStyle.Flex;
            }

            _tooltip.style.display = DisplayStyle.Flex;
            MoveTooltip(position);
        }

        private void MoveTooltip(Vector2 position)
        {
            if (_tooltip == null || _root == null)
                return;

            const float estimatedWidth = 320f;
            float x = Mathf.Min(position.x + 22f, _root.resolvedStyle.width - estimatedWidth);
            float y = Mathf.Min(position.y + 18f, _root.resolvedStyle.height - 240f);
            _tooltip.style.left = Mathf.Max(0f, x);
            _tooltip.style.top = Mathf.Max(0f, y);
        }

        private void HideTooltip()
        {
            if (_tooltip != null)
                _tooltip.style.display = DisplayStyle.None;
        }

        private static string BuildItemLine(DbConnection? conn, ItemInstance item, ItemDefinition? definition)
        {
            List<string> parts = new();
            if (definition != null)
            {
                List<string> kindParts = new();
                AppendNormalizedTooltipPart(kindParts, definition.ItemKind);
                AppendNormalizedTooltipPart(kindParts, definition.ArmorKind);
                AppendNormalizedTooltipPart(kindParts, definition.EquipSlot);
                if (kindParts.Count > 0)
                    parts.Add(string.Join(" - ", kindParts));
            }

            if (item.Quantity > 1)
                parts.Add($"Stack: {item.Quantity}");

            if (definition != null)
            {
                AppendConsumableTooltipPart(parts, definition);
                AppendLabeledTooltipPart(parts, "Weapon", definition.WeaponKind);
                if (definition.UniqueEquipped)
                    parts.Add("Unique-equipped");
            }

            AppendSpellbookTooltipParts(conn, item.ItemInstanceId, parts);
            return string.Join("\n", parts);
        }

        private static List<(string Label, string Value)> BuildItemStats(DbConnection? conn, ItemInstance item, ItemDefinition? definition)
        {
            List<(string, string)> stats = new();
            if (definition != null && definition.PhysicalResistance > 0.0001f)
                stats.Add(("Physical Resistance", FormatAggregateValue("PHYSICAL_RESISTANCE", definition.PhysicalResistance)));

            if (conn == null || string.IsNullOrWhiteSpace(item.ItemInstanceId))
                return stats;

            List<ItemAffixInstance> affixes = new();
            foreach (ItemAffixInstance affix in conn.Db.ItemAffixInstance.ItemInstanceId.Filter(item.ItemInstanceId))
                affixes.Add(affix);
            affixes.Sort((left, right) =>
            {
                int sort = left.SortOrder.CompareTo(right.SortOrder);
                return sort != 0 ? sort : string.Compare(left.Key, right.Key, StringComparison.Ordinal);
            });

            foreach (ItemAffixInstance affix in affixes)
            {
                string kind = NormalizeModifier(affix.ModifierKind);
                string label = IsAllocatedStatModifier(kind)
                    ? FormatTooltipTitle(kind)
                    : ResolveAffixLabel(conn, affix, kind);
                if (string.IsNullOrWhiteSpace(label))
                    continue;

                stats.Add((label, FormatAggregateValue(kind, affix.Value)));
            }

            return stats;
        }

        private static void AppendConsumableTooltipPart(List<string> parts, ItemDefinition definition)
        {
            if (!IsConsumable(definition) || definition.ConsumableAmount <= 0f)
                return;

            string amount = definition.ConsumableAmount.ToString("0.##");
            switch (Normalize(definition.ConsumableEffectKind))
            {
                case "RESTORE_HEALTH":
                    parts.Add($"Restores {amount} health.");
                    break;
                case "RESTORE_RESOURCE":
                    string resource = FormatTooltipValue(definition.ConsumableResourceKind);
                    if (!string.IsNullOrWhiteSpace(resource))
                        parts.Add($"Restores {amount} {resource}.");
                    break;
            }
        }

        private static void AppendSpellbookTooltipParts(DbConnection? conn, string itemInstanceId, List<string> parts)
        {
            if (conn == null || string.IsNullOrWhiteSpace(itemInstanceId))
                return;

            List<ItemSpell> spells = new();
            foreach (ItemSpell spell in conn.Db.ItemSpell.ItemInstanceId.Filter(itemInstanceId))
                spells.Add(spell);
            if (spells.Count == 0)
                return;

            spells.Sort((left, right) =>
            {
                int sort = left.SlotIndex.CompareTo(right.SlotIndex);
                return sort != 0 ? sort : string.Compare(left.Key, right.Key, StringComparison.Ordinal);
            });

            parts.Add($"Spells: {spells.Count}");
            foreach (ItemSpell spell in spells)
            {
                string spellId = WireIdentifier.Normalize(spell.SpellId);
                if (string.IsNullOrWhiteSpace(spellId))
                    continue;

                string displayName = ActionPresentation.ResolveDisplayName(conn, conn.Identity, spellId, spellId);
                parts.Add($"- {displayName}");
            }
        }

        private static string BuildCellFootnote(string context, ItemDefinition? definition)
        {
            if (string.Equals(context, "Loot", StringComparison.Ordinal))
                return "Right-click to take";

            if (IsSpellbook(definition))
                return "Left-click to open - Right-click to equip";

            if (IsConsumable(definition))
                return "Right-click to consume";

            bool equipable = !string.IsNullOrWhiteSpace(definition?.EquipSlot);
            return equipable ? "Right-click to equip - Drag to move" : "Drag to move";
        }

        // ---- drag ghost ---------------------------------------------------------

        private void ShowDragGhost(DragPayload payload)
        {
            if (_dragGhost == null)
                return;

            Sprite? sprite = ItemIconResolver.Resolve(payload.IconId);
            if (_dragGhostIcon != null)
            {
                _dragGhostIcon.style.display = sprite != null ? DisplayStyle.Flex : DisplayStyle.None;
                if (sprite != null)
                    _dragGhostIcon.style.backgroundImage = new StyleBackground(sprite);
            }
            if (_dragGhostLabel != null)
                _dragGhostLabel.text = payload.DisplayName;

            _dragGhost.style.display = DisplayStyle.Flex;
        }

        private void MoveDragGhost(Vector2 position)
        {
            if (_dragGhost == null)
                return;

            _dragGhost.style.left = position.x + 18f;
            _dragGhost.style.top = position.y + 18f;
        }

        private void HideDragGhost()
        {
            if (_dragGhost != null)
                _dragGhost.style.display = DisplayStyle.None;
        }

        // ---- shared queries/util (ported from InventoryController) --------------

        private static void Accumulate(Dictionary<string, float> totals, Dictionary<string, string> labels,
            string kind, string label, float value)
        {
            totals.TryGetValue(kind, out float current);
            totals[kind] = current + value;
            if (!labels.ContainsKey(kind) && !string.IsNullOrWhiteSpace(label))
                labels[kind] = label;
        }

        private static string ResolveAffixLabel(DbConnection conn, ItemAffixInstance affix, string normalizedKind)
        {
            ItemAffixDefinition? definition = conn.Db.ItemAffixDefinition.AffixId.Find(affix.AffixId);
            return !string.IsNullOrWhiteSpace(definition?.DisplayName)
                ? definition.DisplayName
                : FormatTooltipTitle(normalizedKind);
        }

        private static string FormatAggregateValue(string normalizedKind, float value)
        {
            return normalizedKind switch
            {
                "MANA_REGEN" or "HEALTH_REGEN" => $"+{value:0.##}/s",
                "AWARENESS" or "LIGHT" => $"+{value:0.##}",
                "SPELL_SLOT" or "MIGHT" or "INSIGHT" or "FINESSE" or "FORTITUDE" =>
                    $"+{Mathf.Max(0, Mathf.RoundToInt(value))}",
                _ => $"+{value * 100f:0.#}%",
            };
        }

        private static bool TryFindFirstFreePosition(DbConnection conn, InventoryContainer container,
            uint itemWidth, uint itemHeight, out uint x, out uint y)
        {
            uint width = Math.Max(1, itemWidth);
            uint height = Math.Max(1, itemHeight);
            for (uint row = 0; row + height <= container.Height; row++)
            {
                for (uint col = 0; col + width <= container.Width; col++)
                {
                    if (HasGridSpace(conn, container.ContainerId, col, row, width, height))
                    {
                        x = col;
                        y = row;
                        return true;
                    }
                }
            }

            x = 0;
            y = 0;
            return false;
        }

        private static bool HasGridSpace(DbConnection conn, string containerId, uint x, uint y, uint width, uint height)
        {
            foreach (InventorySlot slot in conn.Db.InventorySlot.Iter())
            {
                if (!string.Equals(slot.ContainerId, containerId, StringComparison.Ordinal))
                    continue;

                if (RectanglesOverlap(x, y, width, height, slot.X, slot.Y, slot.Width, slot.Height))
                    return false;
            }

            return true;
        }

        private static bool RectanglesOverlap(uint ax, uint ay, uint aw, uint ah, uint bx, uint by, uint bw, uint bh)
            => ax < bx + bw && ax + aw > bx && ay < by + bh && ay + ah > by;

        private static InventoryContainer? FindPlayerBag(DbConnection conn)
        {
            string ownerKey = conn.Identity.ToString();
            foreach (InventoryContainer container in conn.Db.InventoryContainer.Iter())
            {
                if (string.Equals(container.ContainerKind, "PLAYER_BAG", StringComparison.OrdinalIgnoreCase)
                    && (container.Owner == conn.Identity
                        || string.Equals(container.OwnerKey, ownerKey, StringComparison.OrdinalIgnoreCase)))
                    return container;
            }

            return null;
        }

        private static InventoryContainer? FindLootContainerForNpc(DbConnection conn, Identity npcIdentity)
        {
            string anchorKey = npcIdentity.ToString();
            foreach (InventoryContainer container in conn.Db.InventoryContainer.Iter())
            {
                if (string.Equals(container.ContainerKind, "CORPSE", StringComparison.OrdinalIgnoreCase)
                    && (container.AnchorIdentity == npcIdentity
                        || string.Equals(container.AnchorKey, anchorKey, StringComparison.OrdinalIgnoreCase)))
                    return container;
            }

            return null;
        }

        private static InventoryContainer? FindContainer(DbConnection conn, string? containerId)
        {
            return string.IsNullOrWhiteSpace(containerId)
                ? null
                : conn.Db.InventoryContainer.ContainerId.Find(containerId);
        }

        private static EquipmentLoadout? FindEquipmentLoadout(DbConnection conn)
        {
            foreach (EquipmentLoadout loadout in conn.Db.EquipmentLoadout.Iter())
            {
                if (loadout.Owner == conn.Identity)
                    return loadout;
            }

            return null;
        }

        private static string? EquipmentItemId(EquipmentLoadout loadout, string slotId)
        {
            return Normalize(slotId) switch
            {
                "HEAD" => loadout.HeadItemId,
                "SHOULDER" => loadout.ShoulderItemId,
                "CHEST" => loadout.ChestItemId,
                "LEGS" => loadout.LegsItemId,
                "BOOTS" => loadout.BootsItemId,
                "GLOVES" => loadout.GlovesItemId,
                "RING_1" => loadout.Ring1ItemId,
                "RING_2" => loadout.Ring2ItemId,
                "AMULET" => loadout.AmuletItemId,
                "MAIN_HAND" => loadout.MainHandItemId,
                "SPELLBOOK" => loadout.SpellbookItemId,
                _ => null,
            };
        }

        private static bool IsSpellbook(ItemDefinition? definition)
            => string.Equals(WireIdentifier.Normalize(definition?.ItemKind), "SPELLBOOK", StringComparison.Ordinal);

        private static bool IsConsumable(ItemDefinition? definition)
            => string.Equals(WireIdentifier.Normalize(definition?.ItemKind), "CONSUMABLE", StringComparison.Ordinal);

        private static bool IsAllocatedStatModifier(string modifierKind)
            => NormalizeModifier(modifierKind) is "MIGHT" or "INSIGHT" or "FINESSE" or "FORTITUDE";

        private static string NormalizeModifier(string value)
            => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().Replace('-', '_').ToUpperInvariant();

        private static void AppendNormalizedTooltipPart(List<string> parts, string value)
        {
            string formatted = FormatTooltipValue(value);
            if (!string.IsNullOrWhiteSpace(formatted))
                parts.Add(formatted);
        }

        private static void AppendLabeledTooltipPart(List<string> parts, string label, string value)
        {
            string formatted = FormatTooltipValue(value);
            if (!string.IsNullOrWhiteSpace(formatted))
                parts.Add($"{label}: {formatted}");
        }

        private static string FormatTooltipValue(string value)
            => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().Replace('_', ' ').ToLowerInvariant();

        private static string FormatTooltipTitle(string value)
        {
            string formatted = FormatTooltipValue(value);
            return string.IsNullOrWhiteSpace(formatted)
                ? string.Empty
                : char.ToUpperInvariant(formatted[0]) + formatted[1..];
        }

        private static string Normalize(string value)
            => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToUpperInvariant();

        // ---- input ---------------------------------------------------------------

        private static bool WasInventoryTogglePressed()
        {
#if ENABLE_INPUT_SYSTEM
            if (Keyboard.current != null)
                return Keyboard.current.iKey.wasPressedThisFrame;
#endif
            return UnityEngine.Input.GetKeyDown(KeyCode.I);
        }

    }
}
