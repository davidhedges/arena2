#nullable enable

using System;
using System.Collections.Generic;
using Arena.Combat;
using Arena.Entity;
using Arena.Network;
using SpacetimeDB;
using SpacetimeDB.Types;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif
using UnityEngine.UI;

namespace Arena.UI
{
    [DefaultExecutionOrder(60)]
    public sealed class InventoryController : MonoBehaviour, IEscapeCloseable
    {
        private const float CellSize = ActionBarLayout.SlotSize;
        private const float CellSpacing = ActionBarLayout.Gap;
        private const float PanelHorizontalPadding = 28f;
        private const float PanelVerticalPadding = 66f;
        private const float LootPickScreenRadius = 82f;
        private const float RefreshIntervalSeconds = 0.12f;

        private static readonly Color PanelColor = new(0.055f, 0.06f, 0.068f, 0.96f);
        private static readonly Color HeaderColor = new(0.09f, 0.105f, 0.12f, 0.98f);
        private static readonly Color EmptyCellColor = new(0.018f, 0.022f, 0.027f, 0.94f);
        private static readonly Color FilledCellColor = new(0.16f, 0.18f, 0.2f, 0.98f);
        private static readonly Color ShowcaseColor = new(0.035f, 0.04f, 0.048f, 0.74f);

        private static readonly EquipmentSlotSpec[] EquipmentSlots =
        {
            new("HEAD", "Head", 2, 0),
            new("SHOULDER", "Shoulder", 0, 1),
            new("AMULET", "Amulet", 2, 1),
            new("CAPE", "Cape", 4, 1),
            new("CHEST", "Chest", 2, 2),
            new("GLOVES", "Gloves", 0, 3),
            new("LEGS", "Legs", 2, 3),
            new("BOOTS", "Boots", 2, 4),
            new("RING_1", "Ring", 0, 5),
            new("RING_2", "Ring", 4, 5),
            new("MAIN_HAND", "Weapon", 1, 6),
            new("OFF_HAND", "Offhand", 3, 6),
        };

        private Canvas? _canvas;
        private RectTransform? _equipmentPanel;
        private RectTransform? _inventoryPanel;
        private RectTransform? _inventoryGrid;
        private RectTransform? _lootPanel;
        private RectTransform? _lootGrid;
        private TextMeshProUGUI? _lootTitle;
        private readonly Dictionary<string, EquipmentSlotCell> _equipmentSlots = new(StringComparer.Ordinal);
        private bool _inventoryOpen;
        private string? _openLootContainerId;
        private Identity? _pendingLootNpc;
        private float _pendingLootUntil;
        private float _nextRefreshTime;
        private DragPayload? _activeDrag;
        private RectTransform? _dragGhost;

        public int EscapeClosePriority => 100;
        public bool IsEscapeCloseable => _inventoryOpen;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (FindAnyObjectByType<InventoryController>() != null)
                return;

            GameObject go = new("InventoryController");
            DontDestroyOnLoad(go);
            go.AddComponent<InventoryController>();
        }

        private void Awake()
        {
            RuntimeUiEventSystem.Ensure();
            BuildUi();
        }

        private void OnEnable()
        {
            RuntimeUiEscapeRouter.Register(this);
        }

        private void OnDisable()
        {
            RuntimeUiEscapeRouter.Unregister(this);
        }

        private void Update()
        {
            if (WasInventoryTogglePressed())
                SetInventoryOpen(!_inventoryOpen);

            if (WasRightMousePressed() && !IsPointerOverUi())
                TryOpenLootUnderCursor();

            ResolvePendingLootContainer();

            if (Time.unscaledTime >= _nextRefreshTime && _activeDrag == null)
            {
                _nextRefreshTime = Time.unscaledTime + RefreshIntervalSeconds;
                RefreshVisibleGrids();
            }
        }

        private void SetInventoryOpen(bool open)
        {
            _inventoryOpen = open;
            if (_equipmentPanel != null)
                _equipmentPanel.gameObject.SetActive(open);
            if (_inventoryPanel != null)
                _inventoryPanel.gameObject.SetActive(open);

            if (!open)
                CloseLootPanel();

            RefreshVisibleGrids();
        }

        public bool TryCloseForEscape()
        {
            if (!_inventoryOpen)
                return false;

            _activeDrag = null;
            DestroyDragGhost();
            SetInventoryOpen(false);
            return true;
        }

        private void CloseLootPanel()
        {
            _openLootContainerId = null;
            _pendingLootNpc = null;
            if (_lootPanel != null)
                _lootPanel.gameObject.SetActive(false);
        }

        private void TryOpenLootUnderCursor()
        {
            DbConnection? conn = NetworkManager.Instance?.Conn;
            if (conn == null)
                return;

            NpcEntity? npc = FindDeadNpcUnderCursor();
            if (npc == null)
                return;

            _inventoryOpen = true;
            if (_equipmentPanel != null)
                _equipmentPanel.gameObject.SetActive(true);
            if (_inventoryPanel != null)
                _inventoryPanel.gameObject.SetActive(true);
            if (_lootPanel != null)
                _lootPanel.gameObject.SetActive(true);

            _pendingLootNpc = npc.Identity;
            _pendingLootUntil = Time.unscaledTime + 1.5f;
            conn.Reducers.OpenLootNpc(npc.Identity);
            ResolvePendingLootContainer();
            RefreshVisibleGrids();
        }

        private NpcEntity? FindDeadNpcUnderCursor()
        {
            EntityRegistry? registry = EntityRegistry.Instance;
            Camera? camera = Camera.main;
            if (registry == null || camera == null)
                return null;

            Vector2 mousePosition = ReadMousePosition();
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
                if (_lootPanel != null)
                    _lootPanel.gameObject.SetActive(true);
                return;
            }

            if (Time.unscaledTime > _pendingLootUntil)
                _pendingLootNpc = null;
        }

        private void RefreshVisibleGrids()
        {
            DbConnection? conn = NetworkManager.Instance?.Conn;
            if (conn == null)
            {
                if (_inventoryOpen && _inventoryGrid != null)
                    PopulateEmptyGrid(_inventoryGrid, "Inventory", 10, 4);
                if (_lootPanel != null)
                    _lootPanel.gameObject.SetActive(false);
                return;
            }

            InventoryContainer? bag = FindPlayerBag(conn);
            if (_inventoryOpen)
                RefreshEquipmentPanel(conn);
            if (_inventoryOpen && bag != null && _inventoryGrid != null)
                PopulateGrid(conn, _inventoryGrid, bag, "Inventory");
            else if (_inventoryOpen && _inventoryGrid != null)
                PopulateEmptyGrid(_inventoryGrid, "Inventory", 10, 4);

            if (_lootGrid == null || _lootPanel == null)
                return;

            InventoryContainer? loot = FindContainer(conn, _openLootContainerId);
            bool showLoot = _inventoryOpen && (loot != null || _pendingLootNpc != null);
            _lootPanel.gameObject.SetActive(showLoot);
            if (!showLoot)
                return;

            if (loot != null)
            {
                if (_lootTitle != null)
                    _lootTitle.text = string.Equals(loot.ContainerKind, "CORPSE", StringComparison.OrdinalIgnoreCase) ? "Corpse" : "Loot";
                PopulateGrid(conn, _lootGrid, loot, "Loot");
            }
            else
            {
                if (_lootTitle != null)
                    _lootTitle.text = "Loot";
                PopulateEmptyGrid(_lootGrid, "Loot", 4, 4);
            }
        }

        private void PopulateGrid(DbConnection conn, RectTransform grid, InventoryContainer container, string context)
        {
            int width = Mathf.Max(1, (int)container.Width);
            int height = Mathf.Max(1, (int)container.Height);
            int total = width * height;

            EnsureGridLayout(grid, width);
            EnsureCellCount(grid, total);
            ResizePanelForGrid(grid, width, height);

            Dictionary<(uint X, uint Y), InventorySlot> slots = new();
            foreach (InventorySlot slot in conn.Db.InventorySlot.Iter())
            {
                if (string.Equals(slot.ContainerId, container.ContainerId, StringComparison.Ordinal))
                    slots[(slot.X, slot.Y)] = slot;
            }

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int index = y * width + x;
                    InventoryGridCell cell = grid.GetChild(index).GetComponent<InventoryGridCell>();
                    slots.TryGetValue(((uint)x, (uint)y), out InventorySlot? slot);
                    ItemInstance? item = slot == null ? null : conn.Db.ItemInstance.ItemInstanceId.Find(slot.ItemInstanceId);
                    ItemDefinition? definition = item == null ? null : conn.Db.ItemDefinition.ItemDefId.Find(item.ItemDefId);
                    cell.Configure(this, context, container.ContainerId, (uint)x, (uint)y, item, definition);
                }
            }
        }

        private void PopulateEmptyGrid(RectTransform grid, string context, int width, int height)
        {
            int safeWidth = Mathf.Max(1, width);
            int safeHeight = Mathf.Max(1, height);
            int total = safeWidth * safeHeight;

            EnsureGridLayout(grid, safeWidth);
            EnsureCellCount(grid, total);
            ResizePanelForGrid(grid, safeWidth, safeHeight);

            for (int y = 0; y < safeHeight; y++)
            {
                for (int x = 0; x < safeWidth; x++)
                {
                    int index = y * safeWidth + x;
                    InventoryGridCell cell = grid.GetChild(index).GetComponent<InventoryGridCell>();
                    cell.Configure(this, context, string.Empty, (uint)x, (uint)y, null, null);
                }
            }
        }

        private void RefreshEquipmentPanel(DbConnection conn)
        {
            EquipmentLoadout? loadout = FindEquipmentLoadout(conn);
            foreach (EquipmentSlotSpec spec in EquipmentSlots)
            {
                if (!_equipmentSlots.TryGetValue(spec.SlotId, out EquipmentSlotCell? cell))
                    continue;

                string? itemId = loadout == null ? null : EquipmentItemId(loadout, spec.SlotId);
                ItemInstance? item = string.IsNullOrWhiteSpace(itemId)
                    ? null
                    : conn.Db.ItemInstance.ItemInstanceId.Find(itemId);
                ItemDefinition? definition = item == null
                    ? null
                    : conn.Db.ItemDefinition.ItemDefId.Find(item.ItemDefId);
                cell.Configure(this, spec.SlotId, spec.Label, item, definition);
            }
        }

        private void HandleRightClick(InventoryGridCell cell)
        {
            if (!cell.HasItem)
                return;

            DbConnection? conn = NetworkManager.Instance?.Conn;
            if (conn == null)
                return;

            if (string.Equals(cell.Context, "Loot", StringComparison.Ordinal))
            {
                conn.Reducers.QuickLoot(cell.ContainerId, cell.ItemInstanceId);
                return;
            }

            if (cell.Definition != null && TryResolveEquipSlot(conn, cell.Definition, out string targetSlot))
                conn.Reducers.EquipItem(cell.ItemInstanceId, targetSlot);
        }

        private void HandleRightClick(EquipmentSlotCell cell)
        {
            if (!cell.HasItem)
                return;

            DbConnection? conn = NetworkManager.Instance?.Conn;
            if (conn == null)
                return;

            InventoryContainer? bag = FindPlayerBag(conn);
            if (bag == null)
                return;

            uint itemWidth = cell.Definition?.Width ?? 1;
            uint itemHeight = cell.Definition?.Height ?? 1;
            if (TryFindFirstFreePosition(conn, bag, itemWidth, itemHeight, out uint x, out uint y))
                conn.Reducers.UnequipItem(cell.SlotId, bag.ContainerId, x, y);
        }

        private void HandleDrop(DragPayload payload, IInventoryDropTarget target)
        {
            if (target is EquipmentSlotCell equipmentTarget)
            {
                HandleEquipmentDrop(payload, equipmentTarget);
                return;
            }

            if (target is InventoryGridCell gridTarget)
                HandleGridDrop(payload, gridTarget);
        }

        private void HandleEquipmentDrop(DragPayload payload, EquipmentSlotCell target)
        {
            if (!payload.HasValue || payload.IsFromEquipment)
                return;

            if (!string.Equals(payload.SourceContext, "Inventory", StringComparison.Ordinal))
                return;

            DbConnection? conn = NetworkManager.Instance?.Conn;
            if (conn == null)
                return;

            conn.Reducers.EquipItem(payload.ItemInstanceId, target.SlotId);
        }

        private void HandleGridDrop(DragPayload payload, InventoryGridCell target)
        {
            if (string.IsNullOrWhiteSpace(payload.ItemInstanceId)
                || string.IsNullOrWhiteSpace(target.ContainerId))
                return;

            DbConnection? conn = NetworkManager.Instance?.Conn;
            if (conn == null)
                return;

            if (payload.IsFromEquipment)
            {
                if (!target.HasItem && string.Equals(target.Context, "Inventory", StringComparison.Ordinal))
                    conn.Reducers.UnequipItem(payload.SourceEquipmentSlot, target.ContainerId, target.X, target.Y);
                return;
            }

            if (string.IsNullOrWhiteSpace(payload.SourceContainerId))
                return;

            if (target.HasItem && !string.Equals(target.ItemInstanceId, payload.ItemInstanceId, StringComparison.Ordinal))
            {
                conn.Reducers.MergeStack(payload.ItemInstanceId, target.ItemInstanceId);
                return;
            }

            conn.Reducers.MoveItem(
                payload.SourceContainerId,
                payload.ItemInstanceId,
                target.ContainerId,
                target.X,
                target.Y,
                payload.Quantity);
        }

        private void BeginDrag(DragPayload payload, Vector2 screenPosition)
        {
            if (_canvas == null || !payload.HasValue)
                return;

            _activeDrag = payload;
            _dragGhost = CreateDragGhost(payload.DisplayName, payload.IconId);
            MoveDragGhost(screenPosition);
        }

        private void MoveDrag(Vector2 screenPosition)
        {
            MoveDragGhost(screenPosition);
        }

        private void EndDrag(PointerEventData eventData)
        {
            DragPayload? payload = _activeDrag;
            DestroyDragGhost();
            _activeDrag = null;

            if (payload == null)
                return;

            IInventoryDropTarget? target = FindDropTarget(eventData);
            if (target != null)
                HandleDrop(payload.Value, target);

            RefreshVisibleGrids();
        }

        private IInventoryDropTarget? FindDropTarget(PointerEventData eventData)
        {
            List<RaycastResult> results = new();
            EventSystem.current?.RaycastAll(eventData, results);
            foreach (RaycastResult result in results)
            {
                InventoryGridCell? cell = result.gameObject.GetComponentInParent<InventoryGridCell>();
                if (cell != null)
                    return cell;
                EquipmentSlotCell? equipmentSlot = result.gameObject.GetComponentInParent<EquipmentSlotCell>();
                if (equipmentSlot != null)
                    return equipmentSlot;
            }

            return null;
        }

        private bool TryResolveEquipSlot(DbConnection conn, ItemDefinition definition, out string targetSlot)
        {
            targetSlot = string.Empty;
            string equipSlot = Normalize(definition.EquipSlot);
            if (string.IsNullOrEmpty(equipSlot))
                return false;

            if (string.Equals(definition.ItemKind, "WEAPON", StringComparison.OrdinalIgnoreCase))
            {
                targetSlot = string.Equals(definition.WeaponKind, "SHIELD", StringComparison.OrdinalIgnoreCase)
                    ? "OFF_HAND"
                    : "MAIN_HAND";
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

        private EquipmentLoadout? FindEquipmentLoadout(DbConnection conn)
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
                "CAPE" => loadout.CapeItemId,
                "CHEST" => loadout.ChestItemId,
                "LEGS" => loadout.LegsItemId,
                "BOOTS" => loadout.BootsItemId,
                "GLOVES" => loadout.GlovesItemId,
                "RING_1" => loadout.Ring1ItemId,
                "RING_2" => loadout.Ring2ItemId,
                "AMULET" => loadout.AmuletItemId,
                "MAIN_HAND" => loadout.MainHandItemId,
                "OFF_HAND" => loadout.OffHandItemId,
                _ => null,
            };
        }

        private static bool TryFindFirstFreePosition(
            DbConnection conn,
            InventoryContainer container,
            uint itemWidth,
            uint itemHeight,
            out uint x,
            out uint y)
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

        private static bool HasGridSpace(
            DbConnection conn,
            string containerId,
            uint x,
            uint y,
            uint width,
            uint height)
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

        private static bool RectanglesOverlap(
            uint ax,
            uint ay,
            uint aw,
            uint ah,
            uint bx,
            uint by,
            uint bw,
            uint bh)
        {
            return ax < bx + bw && ax + aw > bx && ay < by + bh && ay + ah > by;
        }

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
            if (string.IsNullOrWhiteSpace(containerId))
                return null;

            return conn.Db.InventoryContainer.ContainerId.Find(containerId);
        }

        private static TooltipData BuildItemTooltip(DbConnection? conn, ItemInstance? item, ItemDefinition? definition)
        {
            if (item == null)
                return default;

            string name = !string.IsNullOrWhiteSpace(definition?.DisplayName)
                ? definition.DisplayName
                : item.ItemInstanceId;
            string subtitle = BuildItemSubtitle(definition);
            string description = BuildItemDescription(conn, item, definition);
            return new TooltipData(name, subtitle, description);
        }

        private static string BuildItemSubtitle(ItemDefinition? definition)
        {
            if (definition == null)
                return "Unknown item";

            List<string> parts = new();
            AppendNormalizedTooltipPart(parts, definition.Rarity);
            AppendNormalizedTooltipPart(parts, definition.ItemKind);
            AppendNormalizedTooltipPart(parts, definition.ArmorKind);
            AppendNormalizedTooltipPart(parts, definition.EquipSlot);
            return string.Join(" - ", parts);
        }

        private static string BuildItemDescription(DbConnection? conn, ItemInstance item, ItemDefinition? definition)
        {
            List<string> parts = new();
            if (item.Quantity > 1)
                parts.Add($"Stack: {item.Quantity}");

            if (definition != null)
            {
                AppendLabeledTooltipPart(parts, "Weapon", definition.WeaponKind);
                AppendLabeledTooltipPart(parts, "Profile", definition.CombatProfileId);
                if (definition.PhysicalResistance > 0.0001f)
                    parts.Add($"Physical Resistance: {FormatAffixValue("PHYSICAL_RESISTANCE", definition.PhysicalResistance)}");
                if (definition.UniqueEquipped)
                    parts.Add("Unique-equipped");
            }

            AppendAffixTooltipParts(conn, item.ItemInstanceId, parts);

            return string.Join("\n", parts);
        }

        private static void AppendAffixTooltipParts(DbConnection? conn, string itemInstanceId, List<string> parts)
        {
            if (conn == null || string.IsNullOrWhiteSpace(itemInstanceId))
                return;

            List<ItemAffixInstance> affixes = new();
            foreach (ItemAffixInstance affix in conn.Db.ItemAffixInstance.ItemInstanceId.Filter(itemInstanceId))
                affixes.Add(affix);
            affixes.Sort((left, right) =>
            {
                int sort = left.SortOrder.CompareTo(right.SortOrder);
                return sort != 0
                    ? sort
                    : string.Compare(left.Key, right.Key, StringComparison.Ordinal);
            });

            foreach (ItemAffixInstance affix in affixes)
            {
                ItemAffixDefinition? definition = conn.Db.ItemAffixDefinition.AffixId.Find(affix.AffixId);
                string label = !string.IsNullOrWhiteSpace(definition?.DisplayName)
                    ? definition.DisplayName
                    : FormatTooltipValue(affix.ModifierKind);
                if (string.IsNullOrWhiteSpace(label))
                    continue;
                parts.Add($"{label}: {FormatAffixValue(affix.ModifierKind, affix.Value)}");
            }
        }

        private static string FormatAffixValue(string modifierKind, float value)
        {
            string normalized = string.IsNullOrWhiteSpace(modifierKind)
                ? string.Empty
                : modifierKind.Trim().Replace('-', '_').ToUpperInvariant();

            return normalized switch
            {
                "MANA_REGEN" or "HEALTH_REGEN" => $"+{value:0.##}/s",
                "AWARENESS" or "LIGHT" => $"+{value:0.##}",
                "SPELL_SLOT" => $"+{Mathf.Max(0, Mathf.RoundToInt(value))}",
                _ => $"+{value * 100f:0.#}%"
            };
        }

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
        {
            return string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : value.Trim().Replace('_', ' ').ToLowerInvariant();
        }

        private void BuildUi()
        {
            GameObject canvasGo = new("InventoryCanvas");
            canvasGo.transform.SetParent(transform, false);
            _canvas = canvasGo.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 42;
            canvasGo.AddComponent<GraphicRaycaster>();

            CanvasScaler scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            _inventoryPanel = CreatePanel(canvasGo.transform, "InventoryPanel", "Inventory", PanelSizeForGrid(10, 4), new Vector2(1f, 0.5f), new Vector2(-28f, 0f), out _inventoryGrid, out _);
            _equipmentPanel = CreateEquipmentPanel(canvasGo.transform, new Vector2(1f, 0.5f), new Vector2(-780f, 0f));
            _lootPanel = CreatePanel(canvasGo.transform, "LootPanel", "Loot", PanelSizeForGrid(4, 4), new Vector2(1f, 0.5f), new Vector2(-1188f, 48f), out _lootGrid, out _lootTitle);
            _equipmentPanel.gameObject.SetActive(false);
            _inventoryPanel.gameObject.SetActive(false);
            _lootPanel.gameObject.SetActive(false);
        }

        private RectTransform CreateEquipmentPanel(Transform parent, Vector2 anchor, Vector2 anchoredPosition)
        {
            RectTransform panel = CreatePanel(
                parent,
                "EquipmentPanel",
                "Character",
                PanelSizeForGrid(5, 7),
                anchor,
                anchoredPosition,
                out RectTransform body,
                out _);
            UnityEngine.Object.Destroy(body.GetComponent<GridLayoutGroup>());

            GameObject showcaseGo = new("Showcase");
            showcaseGo.transform.SetParent(body, false);
            RectTransform showcase = showcaseGo.AddComponent<RectTransform>();
            showcase.anchorMin = new Vector2(0f, 1f);
            showcase.anchorMax = new Vector2(0f, 1f);
            showcase.pivot = new Vector2(0f, 1f);
            showcase.anchoredPosition = new Vector2(ActionBarLayout.Pitch, -ActionBarLayout.Pitch * 2f);
            showcase.sizeDelta = new Vector2(
                ActionBarLayout.SlotSize * 3f + ActionBarLayout.Gap * 2f,
                ActionBarLayout.SlotSize * 3f + ActionBarLayout.Gap * 2f);

            Image showcaseImage = showcaseGo.AddComponent<Image>();
            showcaseImage.color = ShowcaseColor;
            showcaseImage.raycastTarget = false;

            Outline showcaseOutline = showcaseGo.AddComponent<Outline>();
            showcaseOutline.effectColor = new Color(1f, 1f, 1f, 0.08f);
            showcaseOutline.effectDistance = new Vector2(1f, -1f);

            _equipmentSlots.Clear();
            foreach (EquipmentSlotSpec spec in EquipmentSlots)
            {
                EquipmentSlotCell slot = CreateEquipmentSlot(body, spec);
                _equipmentSlots[spec.SlotId] = slot;
            }

            return panel;
        }

        private EquipmentSlotCell CreateEquipmentSlot(RectTransform parent, EquipmentSlotSpec spec)
        {
            GameObject cellGo = new($"Slot_{spec.SlotId}");
            cellGo.transform.SetParent(parent, false);

            RectTransform rt = cellGo.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(
                spec.Column * ActionBarLayout.Pitch,
                -spec.Row * ActionBarLayout.Pitch);
            rt.sizeDelta = ActionBarLayout.SlotVector;

            Image background = cellGo.AddComponent<Image>();
            background.color = EmptyCellColor;

            Outline outline = cellGo.AddComponent<Outline>();
            outline.effectColor = new Color(1f, 1f, 1f, 0.12f);
            outline.effectDistance = new Vector2(1f, -1f);

            GameObject labelGo = new("Label");
            labelGo.transform.SetParent(cellGo.transform, false);
            RectTransform labelRt = labelGo.AddComponent<RectTransform>();
            labelRt.anchorMin = Vector2.zero;
            labelRt.anchorMax = Vector2.one;
            labelRt.offsetMin = new Vector2(4f, 4f);
            labelRt.offsetMax = new Vector2(-4f, -4f);

            TextMeshProUGUI label = labelGo.AddComponent<TextMeshProUGUI>();
            label.font = ResolveFont();
            label.fontSize = 10f;
            label.color = Color.white;
            label.alignment = TextAlignmentOptions.Center;
            label.textWrappingMode = TextWrappingModes.Normal;
            label.overflowMode = TextOverflowModes.Ellipsis;
            label.raycastTarget = false;

            GameObject iconGo = new("Icon");
            iconGo.transform.SetParent(cellGo.transform, false);
            RectTransform iconRt = iconGo.AddComponent<RectTransform>();
            iconRt.anchorMin = Vector2.zero;
            iconRt.anchorMax = Vector2.one;
            iconRt.offsetMin = Vector2.zero;
            iconRt.offsetMax = Vector2.zero;

            Image icon = iconGo.AddComponent<Image>();
            icon.color = Color.white;
            icon.preserveAspect = true;
            icon.raycastTarget = false;
            icon.enabled = false;

            labelGo.transform.SetAsLastSibling();

            return cellGo.AddComponent<EquipmentSlotCell>().Initialize(background, icon, label);
        }

        private static RectTransform CreatePanel(
            Transform parent,
            string name,
            string title,
            Vector2 size,
            Vector2 anchor,
            Vector2 anchoredPosition,
            out RectTransform grid,
            out TextMeshProUGUI titleText)
        {
            GameObject panelGo = new(name);
            panelGo.transform.SetParent(parent, false);
            RectTransform panel = panelGo.AddComponent<RectTransform>();
            panel.anchorMin = anchor;
            panel.anchorMax = anchor;
            panel.pivot = new Vector2(1f, 0.5f);
            panel.anchoredPosition = anchoredPosition;
            panel.sizeDelta = size;

            Image panelImage = panelGo.AddComponent<Image>();
            panelImage.color = PanelColor;

            Outline outline = panelGo.AddComponent<Outline>();
            outline.effectColor = new Color(1f, 1f, 1f, 0.18f);
            outline.effectDistance = new Vector2(1f, -1f);

            GameObject headerGo = new("Header");
            headerGo.transform.SetParent(panel, false);
            RectTransform header = headerGo.AddComponent<RectTransform>();
            header.anchorMin = new Vector2(0f, 1f);
            header.anchorMax = new Vector2(1f, 1f);
            header.pivot = new Vector2(0.5f, 1f);
            header.sizeDelta = new Vector2(0f, 38f);
            header.anchoredPosition = Vector2.zero;

            Image headerImage = headerGo.AddComponent<Image>();
            headerImage.color = HeaderColor;

            GameObject titleGo = new("Title");
            titleGo.transform.SetParent(header, false);
            RectTransform titleRt = titleGo.AddComponent<RectTransform>();
            titleRt.anchorMin = Vector2.zero;
            titleRt.anchorMax = Vector2.one;
            titleRt.offsetMin = new Vector2(12f, 0f);
            titleRt.offsetMax = new Vector2(-12f, 0f);
            titleText = titleGo.AddComponent<TextMeshProUGUI>();
            titleText.font = ResolveFont();
            titleText.fontSize = 16f;
            titleText.fontStyle = FontStyles.Bold;
            titleText.color = Color.white;
            titleText.alignment = TextAlignmentOptions.MidlineLeft;
            titleText.textWrappingMode = TextWrappingModes.NoWrap;
            titleText.text = title;

            GameObject gridGo = new("Grid");
            gridGo.transform.SetParent(panel, false);
            grid = gridGo.AddComponent<RectTransform>();
            grid.anchorMin = new Vector2(0f, 0f);
            grid.anchorMax = new Vector2(1f, 1f);
            grid.offsetMin = new Vector2(14f, 14f);
            grid.offsetMax = new Vector2(-14f, -52f);
            EnsureGridLayout(grid, 1);

            return panel;
        }

        private static void EnsureGridLayout(RectTransform grid, int columns)
        {
            GridLayoutGroup layout = grid.GetComponent<GridLayoutGroup>() ?? grid.gameObject.AddComponent<GridLayoutGroup>();
            layout.cellSize = ActionBarLayout.SlotVector;
            layout.spacing = ActionBarLayout.GapVector;
            layout.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            layout.constraintCount = Mathf.Max(1, columns);
            layout.childAlignment = TextAnchor.UpperLeft;
        }

        private static void ResizePanelForGrid(RectTransform grid, int columns, int rows)
        {
            if (grid.parent is not RectTransform panel)
                return;

            panel.sizeDelta = PanelSizeForGrid(columns, rows);
        }

        private static Vector2 PanelSizeForGrid(int columns, int rows)
        {
            int safeColumns = Mathf.Max(1, columns);
            int safeRows = Mathf.Max(1, rows);
            return new Vector2(
                safeColumns * CellSize + (safeColumns - 1) * CellSpacing + PanelHorizontalPadding,
                safeRows * CellSize + (safeRows - 1) * CellSpacing + PanelVerticalPadding);
        }

        private static void EnsureCellCount(RectTransform grid, int total)
        {
            while (grid.childCount < total)
                CreateCell(grid);

            for (int i = 0; i < grid.childCount; i++)
                grid.GetChild(i).gameObject.SetActive(i < total);
        }

        private static void CreateCell(RectTransform grid)
        {
            GameObject cellGo = new("Cell");
            cellGo.transform.SetParent(grid, false);

            RectTransform rt = cellGo.AddComponent<RectTransform>();
            rt.sizeDelta = ActionBarLayout.SlotVector;

            Image background = cellGo.AddComponent<Image>();
            background.color = EmptyCellColor;

            Outline outline = cellGo.AddComponent<Outline>();
            outline.effectColor = new Color(1f, 1f, 1f, 0.12f);
            outline.effectDistance = new Vector2(1f, -1f);

            GameObject iconGo = new("Icon");
            iconGo.transform.SetParent(cellGo.transform, false);
            RectTransform iconRt = iconGo.AddComponent<RectTransform>();
            iconRt.anchorMin = Vector2.zero;
            iconRt.anchorMax = Vector2.one;
            iconRt.offsetMin = Vector2.zero;
            iconRt.offsetMax = Vector2.zero;

            Image icon = iconGo.AddComponent<Image>();
            icon.color = Color.white;
            icon.preserveAspect = true;
            icon.raycastTarget = false;
            icon.enabled = false;

            GameObject labelGo = new("Label");
            labelGo.transform.SetParent(cellGo.transform, false);
            RectTransform labelRt = labelGo.AddComponent<RectTransform>();
            labelRt.anchorMin = Vector2.zero;
            labelRt.anchorMax = Vector2.one;
            labelRt.offsetMin = new Vector2(4f, 4f);
            labelRt.offsetMax = new Vector2(-4f, -4f);
            TextMeshProUGUI label = labelGo.AddComponent<TextMeshProUGUI>();
            label.font = ResolveFont();
            label.fontSize = 10f;
            label.color = Color.white;
            label.alignment = TextAlignmentOptions.Center;
            label.textWrappingMode = TextWrappingModes.Normal;
            label.overflowMode = TextOverflowModes.Ellipsis;
            label.raycastTarget = false;

            GameObject quantityGo = new("Quantity");
            quantityGo.transform.SetParent(cellGo.transform, false);
            RectTransform quantityRt = quantityGo.AddComponent<RectTransform>();
            quantityRt.anchorMin = Vector2.zero;
            quantityRt.anchorMax = Vector2.one;
            quantityRt.offsetMin = new Vector2(3f, 2f);
            quantityRt.offsetMax = new Vector2(-4f, -2f);
            TextMeshProUGUI quantity = quantityGo.AddComponent<TextMeshProUGUI>();
            quantity.font = ResolveFont();
            quantity.fontSize = 10f;
            quantity.fontStyle = FontStyles.Bold;
            quantity.color = Color.white;
            quantity.alignment = TextAlignmentOptions.BottomRight;
            quantity.textWrappingMode = TextWrappingModes.NoWrap;
            quantity.raycastTarget = false;

            cellGo.AddComponent<InventoryGridCell>().Initialize(background, icon, label, quantity);
        }

        private RectTransform CreateDragGhost(string displayName, string iconId)
        {
            GameObject go = new("InventoryDragGhost");
            go.transform.SetParent(_canvas!.transform, false);
            go.transform.SetAsLastSibling();

            RectTransform rt = go.AddComponent<RectTransform>();
            rt.sizeDelta = new Vector2(132f, 42f);

            Image image = go.AddComponent<Image>();
            image.color = new Color(0.025f, 0.03f, 0.036f, 0.92f);
            image.raycastTarget = false;

            Outline outline = go.AddComponent<Outline>();
            outline.effectColor = new Color(1f, 1f, 1f, 0.22f);
            outline.effectDistance = new Vector2(1f, -1f);

            Sprite? iconSprite = ItemIconResolver.Resolve(iconId);
            if (iconSprite != null)
            {
                GameObject iconGo = new("Icon");
                iconGo.transform.SetParent(go.transform, false);
                RectTransform iconRt = iconGo.AddComponent<RectTransform>();
                iconRt.anchorMin = new Vector2(0f, 0.5f);
                iconRt.anchorMax = new Vector2(0f, 0.5f);
                iconRt.pivot = new Vector2(0f, 0.5f);
                iconRt.anchoredPosition = new Vector2(3f, 0f);
                iconRt.sizeDelta = new Vector2(42f, 42f);

                Image icon = iconGo.AddComponent<Image>();
                icon.sprite = iconSprite;
                icon.color = Color.white;
                icon.preserveAspect = true;
                icon.raycastTarget = false;
            }

            GameObject labelGo = new("Text");
            labelGo.transform.SetParent(go.transform, false);
            RectTransform labelRt = labelGo.AddComponent<RectTransform>();
            labelRt.anchorMin = Vector2.zero;
            labelRt.anchorMax = Vector2.one;
            labelRt.offsetMin = new Vector2(iconSprite == null ? 8f : 50f, 4f);
            labelRt.offsetMax = new Vector2(-8f, -4f);

            TextMeshProUGUI label = labelGo.AddComponent<TextMeshProUGUI>();
            label.font = ResolveFont();
            label.fontSize = 12f;
            label.alignment = iconSprite == null ? TextAlignmentOptions.Center : TextAlignmentOptions.MidlineLeft;
            label.color = Color.white;
            label.textWrappingMode = TextWrappingModes.NoWrap;
            label.overflowMode = TextOverflowModes.Ellipsis;
            label.raycastTarget = false;
            label.text = displayName;

            return rt;
        }

        private void MoveDragGhost(Vector2 screenPosition)
        {
            if (_canvas == null || _dragGhost == null)
                return;

            RectTransform canvasRect = (RectTransform)_canvas.transform;
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    canvasRect,
                    screenPosition,
                    _canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : _canvas.worldCamera,
                    out Vector2 localPoint))
            {
                _dragGhost.anchoredPosition = localPoint + new Vector2(18f, -18f);
            }
        }

        private void DestroyDragGhost()
        {
            if (_dragGhost != null)
                Destroy(_dragGhost.gameObject);
            _dragGhost = null;
        }

        private static bool WasInventoryTogglePressed()
        {
#if ENABLE_INPUT_SYSTEM
            if (Keyboard.current != null)
                return Keyboard.current.iKey.wasPressedThisFrame;
#endif
            return UnityEngine.Input.GetKeyDown(KeyCode.I);
        }

        private static bool WasRightMousePressed()
        {
#if ENABLE_INPUT_SYSTEM
            if (Mouse.current != null)
                return Mouse.current.rightButton.wasPressedThisFrame;
#endif
            return UnityEngine.Input.GetMouseButtonDown(1);
        }

        private static Vector2 ReadMousePosition()
        {
#if ENABLE_INPUT_SYSTEM
            if (Mouse.current != null)
                return Mouse.current.position.ReadValue();
#endif
            return UnityEngine.Input.mousePosition;
        }

        private static bool IsPointerOverUi()
        {
            return EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();
        }

        private static TMP_FontAsset? ResolveFont()
        {
            return TMP_Settings.defaultFontAsset
                ?? Resources.Load<TMP_FontAsset>("Fonts & Materials/LiberationSans SDF");
        }

        private static string Normalize(string value)
            => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToUpperInvariant();

        private interface IInventoryDropTarget
        {
        }

        private readonly struct EquipmentSlotSpec
        {
            public readonly string SlotId;
            public readonly string Label;
            public readonly int Column;
            public readonly int Row;

            public EquipmentSlotSpec(string slotId, string label, int column, int row)
            {
                SlotId = slotId;
                Label = label;
                Column = column;
                Row = row;
            }
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

            public DragPayload(
                string sourceContext,
                string sourceContainerId,
                string sourceEquipmentSlot,
                string itemInstanceId,
                uint quantity,
                string displayName,
                string iconId)
            {
                SourceContext = sourceContext;
                SourceContainerId = sourceContainerId;
                SourceEquipmentSlot = sourceEquipmentSlot;
                ItemInstanceId = itemInstanceId;
                Quantity = quantity;
                DisplayName = displayName;
                IconId = iconId;
            }

            public bool HasValue => !string.IsNullOrWhiteSpace(ItemInstanceId)
                && (!string.IsNullOrWhiteSpace(SourceContainerId)
                    || !string.IsNullOrWhiteSpace(SourceEquipmentSlot));

            public bool IsFromEquipment => !string.IsNullOrWhiteSpace(SourceEquipmentSlot);

            public static DragPayload FromContainer(
                string sourceContext,
                string sourceContainerId,
                string itemInstanceId,
                uint quantity,
                string displayName,
                string iconId)
            {
                return new DragPayload(sourceContext, sourceContainerId, string.Empty, itemInstanceId, quantity, displayName, iconId);
            }

            public static DragPayload FromEquipment(string sourceSlot, string itemInstanceId, string displayName, string iconId)
            {
                return new DragPayload("Equipment", string.Empty, sourceSlot, itemInstanceId, 1, displayName, iconId);
            }
        }

        private sealed class EquipmentSlotCell : MonoBehaviour, IInventoryDropTarget, IPointerClickHandler, IBeginDragHandler, IDragHandler, IEndDragHandler
        {
            private InventoryController? _controller;
            private Image? _background;
            private Image? _icon;
            private TextMeshProUGUI? _label;
            private TooltipTarget? _tooltip;
            private string _emptyLabel = string.Empty;

            public string SlotId { get; private set; } = string.Empty;
            public string ItemInstanceId { get; private set; } = string.Empty;
            public ItemDefinition? Definition { get; private set; }
            public bool HasItem => !string.IsNullOrWhiteSpace(ItemInstanceId);

            public EquipmentSlotCell Initialize(Image background, Image icon, TextMeshProUGUI label)
            {
                _background = background;
                _icon = icon;
                _label = label;
                _tooltip = gameObject.GetComponent<TooltipTarget>() ?? gameObject.AddComponent<TooltipTarget>();
                return this;
            }

            public void Configure(
                InventoryController controller,
                string slotId,
                string emptyLabel,
                ItemInstance? item,
                ItemDefinition? definition)
            {
                _controller = controller;
                SlotId = slotId;
                _emptyLabel = emptyLabel;
                ItemInstanceId = item?.ItemInstanceId ?? string.Empty;
                Definition = definition;

                if (_background != null)
                    _background.color = item == null ? EmptyCellColor : FilledCellColor;

                Sprite? iconSprite = item == null ? null : ItemIconResolver.Resolve(definition);
                if (_icon != null)
                {
                    _icon.sprite = iconSprite;
                    _icon.enabled = iconSprite != null;
                }

                if (_label != null)
                    _label.text = item == null || iconSprite == null ? (definition?.DisplayName ?? _emptyLabel) : string.Empty;

                if (_tooltip != null && controller._canvas != null)
                    _tooltip.Configure(
                        controller._canvas,
                        BuildItemTooltip(NetworkManager.Instance?.Conn, item, definition),
                        pollHover: true);
            }

            public void OnPointerClick(PointerEventData eventData)
            {
                if (eventData.button != PointerEventData.InputButton.Right)
                    return;

                _controller?.HandleRightClick(this);
            }

            public void OnBeginDrag(PointerEventData eventData)
            {
                if (eventData.button != PointerEventData.InputButton.Left || !HasItem || _controller == null)
                    return;

                string displayName = Definition?.DisplayName ?? ItemInstanceId;
                string iconId = Definition?.IconId ?? string.Empty;
                _controller.BeginDrag(DragPayload.FromEquipment(SlotId, ItemInstanceId, displayName, iconId), eventData.position);
            }

            public void OnDrag(PointerEventData eventData)
            {
                _controller?.MoveDrag(eventData.position);
            }

            public void OnEndDrag(PointerEventData eventData)
            {
                _controller?.EndDrag(eventData);
            }
        }

        private sealed class InventoryGridCell : MonoBehaviour, IInventoryDropTarget, IPointerClickHandler, IBeginDragHandler, IDragHandler, IEndDragHandler
        {
            private InventoryController? _controller;
            private Image? _background;
            private Image? _icon;
            private TextMeshProUGUI? _label;
            private TextMeshProUGUI? _quantity;
            private TooltipTarget? _tooltip;

            public string Context { get; private set; } = string.Empty;
            public string ContainerId { get; private set; } = string.Empty;
            public string ItemInstanceId { get; private set; } = string.Empty;
            public uint X { get; private set; }
            public uint Y { get; private set; }
            public uint ItemQuantity { get; private set; }
            public ItemDefinition? Definition { get; private set; }
            public bool HasItem => !string.IsNullOrWhiteSpace(ItemInstanceId);

            public void Initialize(Image background, Image icon, TextMeshProUGUI label, TextMeshProUGUI quantity)
            {
                _background = background;
                _icon = icon;
                _label = label;
                _quantity = quantity;
                _tooltip = gameObject.GetComponent<TooltipTarget>() ?? gameObject.AddComponent<TooltipTarget>();
            }

            public void Configure(
                InventoryController controller,
                string context,
                string containerId,
                uint x,
                uint y,
                ItemInstance? item,
                ItemDefinition? definition)
            {
                _controller = controller;
                Context = context;
                ContainerId = containerId;
                X = x;
                Y = y;
                ItemInstanceId = item?.ItemInstanceId ?? string.Empty;
                ItemQuantity = item?.Quantity ?? 0;
                Definition = definition;

                if (_background != null)
                    _background.color = item == null ? EmptyCellColor : FilledCellColor;

                Sprite? iconSprite = item == null ? null : ItemIconResolver.Resolve(definition);
                if (_icon != null)
                {
                    _icon.sprite = iconSprite;
                    _icon.enabled = iconSprite != null;
                }

                if (_label != null)
                    _label.text = item != null && iconSprite == null ? (definition?.DisplayName ?? string.Empty) : string.Empty;

                if (_quantity != null)
                    _quantity.text = item != null && item.Quantity > 1 ? item.Quantity.ToString() : string.Empty;

                if (_tooltip != null && controller._canvas != null)
                    _tooltip.Configure(
                        controller._canvas,
                        BuildItemTooltip(NetworkManager.Instance?.Conn, item, definition),
                        pollHover: true);
            }

            public void OnPointerClick(PointerEventData eventData)
            {
                if (eventData.button != PointerEventData.InputButton.Right)
                    return;

                _controller?.HandleRightClick(this);
            }

            public void OnBeginDrag(PointerEventData eventData)
            {
                if (eventData.button != PointerEventData.InputButton.Left || !HasItem || _controller == null)
                    return;

                string displayName = Definition?.DisplayName ?? ItemInstanceId;
                string iconId = Definition?.IconId ?? string.Empty;
                _controller.BeginDrag(DragPayload.FromContainer(Context, ContainerId, ItemInstanceId, ItemQuantity, displayName, iconId), eventData.position);
            }

            public void OnDrag(PointerEventData eventData)
            {
                _controller?.MoveDrag(eventData.position);
            }

            public void OnEndDrag(PointerEventData eventData)
            {
                _controller?.EndDrag(eventData);
            }
        }
    }
}
