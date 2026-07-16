#nullable enable

using System;
using System.Collections.Generic;
using System.Text;
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
        private const float LootPickScreenRadius = 82f;
        private const float RefreshIntervalSeconds = 0.12f;
        private const float StatsColumnWidth = 200f;
        private const float WindowGap = 12f;
        private const int DollColumns = 5;
        private const int DollRows = 7;

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
            new("SPELLBOOK", "Spellbook", 4, 6),
        };

        private Canvas? _canvas;
        private ArenaWindow? _characterWindow;
        private ArenaWindow? _inventoryWindow;
        private ArenaWindow? _lootWindow;
        private RectTransform? _inventoryGrid;
        private RectTransform? _lootGrid;
        private RectTransform? _statsList;
        // Sentinel forces the first refresh to build the (possibly empty) list.
        private string _statsSignature = "\0unset";
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
            if (!ArenaRuntimeSceneGate.ShouldRunArenaRuntimeInActiveScene())
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
            if (!ArenaRuntimeSceneGate.ShouldRunArenaRuntimeInActiveScene())
            {
                if (_inventoryOpen)
                    SetInventoryOpen(false, instant: true);
                _lootWindow?.SetVisible(false, instant: true);
                return;
            }

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

        private void SetInventoryOpen(bool open, bool instant = false)
        {
            _inventoryOpen = open;
            _characterWindow?.SetVisible(open, instant);
            _inventoryWindow?.SetVisible(open, instant);

            if (open)
                RuntimeUiLayer.BringToFront(_canvas);

            if (!open)
                CloseLootPanel(instant);

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

        private void CloseLootPanel(bool instant = false)
        {
            _openLootContainerId = null;
            _pendingLootNpc = null;
            _lootWindow?.SetVisible(false, instant);
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
            _characterWindow?.SetVisible(true);
            _inventoryWindow?.SetVisible(true);
            _lootWindow?.SetVisible(true);
            RuntimeUiLayer.BringToFront(_canvas);

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
                _lootWindow?.SetVisible(true);
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
                _lootWindow?.SetVisible(false, instant: true);
                return;
            }

            InventoryContainer? bag = FindPlayerBag(conn);
            if (_inventoryOpen)
                RefreshEquipmentPanel(conn);
            if (_inventoryOpen && bag != null && _inventoryGrid != null)
                PopulateGrid(conn, _inventoryGrid, bag, "Inventory");
            else if (_inventoryOpen && _inventoryGrid != null)
                PopulateEmptyGrid(_inventoryGrid, "Inventory", 10, 4);

            if (_lootGrid == null || _lootWindow == null)
                return;

            InventoryContainer? loot = FindContainer(conn, _openLootContainerId);
            bool showLoot = _inventoryOpen && (loot != null || _pendingLootNpc != null);
            _lootWindow.SetVisible(showLoot);
            if (!showLoot)
                return;

            if (loot != null)
            {
                _lootWindow.SetTitle(string.Equals(loot.ContainerKind, "CORPSE", StringComparison.OrdinalIgnoreCase) ? "Corpse" : "Loot");
                PopulateGrid(conn, _lootGrid, loot, "Loot");
            }
            else
            {
                _lootWindow.SetTitle("Loot");
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
            ResizeWindowForGrid(grid, width, height);

            Dictionary<(uint X, uint Y), InventorySlot> slots = new();
            foreach (InventorySlot slot in conn.Db.InventorySlot.Iter())
            {
                if (string.Equals(slot.ContainerId, container.ContainerId, StringComparison.Ordinal))
                    slots[(slot.X, slot.Y)] = slot;
            }

            int occupied = 0;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int index = y * width + x;
                    InventoryGridCell cell = grid.GetChild(index).GetComponent<InventoryGridCell>();
                    slots.TryGetValue(((uint)x, (uint)y), out InventorySlot? slot);
                    ItemInstance? item = slot == null ? null : conn.Db.ItemInstance.ItemInstanceId.Find(slot.ItemInstanceId);
                    ItemDefinition? definition = item == null ? null : conn.Db.ItemDefinition.ItemDefId.Find(item.ItemDefId);
                    if (item != null)
                        occupied++;
                    cell.Configure(this, context, container.ContainerId, (uint)x, (uint)y, item, definition);
                }
            }

            ArenaWindow? window = grid.GetComponentInParent<ArenaWindow>();
            window?.SetSubtitle($"{occupied}/{total} slots");
        }

        private void PopulateEmptyGrid(RectTransform grid, string context, int width, int height)
        {
            int safeWidth = Mathf.Max(1, width);
            int safeHeight = Mathf.Max(1, height);
            int total = safeWidth * safeHeight;

            EnsureGridLayout(grid, safeWidth);
            EnsureCellCount(grid, total);
            ResizeWindowForGrid(grid, safeWidth, safeHeight);

            for (int y = 0; y < safeHeight; y++)
            {
                for (int x = 0; x < safeWidth; x++)
                {
                    int index = y * safeWidth + x;
                    InventoryGridCell cell = grid.GetChild(index).GetComponent<InventoryGridCell>();
                    cell.Configure(this, context, string.Empty, (uint)x, (uint)y, null, null);
                }
            }

            ArenaWindow? window = grid.GetComponentInParent<ArenaWindow>();
            window?.SetSubtitle(string.Empty);
        }

        private void RefreshEquipmentPanel(DbConnection conn)
        {
            EquipmentLoadout? loadout = FindEquipmentLoadout(conn);
            int equipped = 0;
            List<(ItemInstance Item, ItemDefinition? Definition)> equippedItems = new();
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
                if (item != null)
                {
                    equipped++;
                    equippedItems.Add((item, definition));
                }

                cell.Configure(this, spec.SlotId, spec.Label, item, definition);
            }

            _characterWindow?.SetSubtitle(equipped == 0 ? string.Empty : $"{equipped} equipped");
            RefreshCharacterStats(conn, equippedItems);
        }

        /// <summary>
        /// Aggregates affix bonuses (plus armor resistance) across equipped items
        /// into the character window's attributes column.
        /// </summary>
        private void RefreshCharacterStats(DbConnection conn, List<(ItemInstance Item, ItemDefinition? Definition)> equippedItems)
        {
            if (_statsList == null)
                return;

            Dictionary<string, float> totals = new(StringComparer.Ordinal);
            Dictionary<string, string> labels = new(StringComparer.Ordinal);

            foreach ((ItemInstance item, ItemDefinition? definition) in equippedItems)
            {
                if (definition != null && definition.PhysicalResistance > 0.0001f)
                {
                    Accumulate(totals, labels, "PHYSICAL_RESISTANCE", "Physical Resistance", definition.PhysicalResistance);
                }

                foreach (ItemAffixInstance affix in conn.Db.ItemAffixInstance.ItemInstanceId.Filter(item.ItemInstanceId))
                {
                    string kind = string.IsNullOrWhiteSpace(affix.ModifierKind)
                        ? string.Empty
                        : affix.ModifierKind.Trim().Replace('-', '_').ToUpperInvariant();
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
            foreach (Transform child in _statsList)
            {
                child.gameObject.SetActive(false);
                Destroy(child.gameObject);
            }

            if (kinds.Count == 0)
            {
                TextMeshProUGUI empty = ArenaUiKit.MakeText(
                    _statsList,
                    "Empty",
                    "No bonuses from equipment.",
                    ArenaUiTheme.SmallSize,
                    ArenaUiTheme.MutedText);
                AddStatRowLayout(empty.rectTransform, 20f);
                return;
            }

            foreach (string kind in kinds)
            {
                RectTransform row = ArenaUiKit.MakeRect(_statsList, "StatRow");
                AddStatRowLayout(row, 21f);

                TextMeshProUGUI label = ArenaUiKit.MakeText(
                    row,
                    "Label",
                    labels[kind],
                    ArenaUiTheme.SmallSize,
                    ArenaUiTheme.MutedText);
                ArenaUiKit.Fill(label.rectTransform);

                TextMeshProUGUI value = ArenaUiKit.MakeText(
                    row,
                    "Value",
                    FormatAggregateValue(kind, totals[kind]),
                    ArenaUiTheme.SmallSize,
                    ArenaUiTheme.Success,
                    ArenaUiTheme.StrongFont,
                    TextAlignmentOptions.MidlineRight);
                ArenaUiKit.Fill(value.rectTransform);
            }
        }

        private static void AddStatRowLayout(RectTransform rect, float height)
        {
            LayoutElement element = rect.gameObject.AddComponent<LayoutElement>();
            element.preferredHeight = height;
            element.minHeight = height;
        }

        private static void Accumulate(
            Dictionary<string, float> totals,
            Dictionary<string, string> labels,
            string kind,
            string label,
            float value)
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

        /// <summary>Formats a summed bonus without repeating the stat name inside the value.</summary>
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

            if (IsConsumable(cell.Definition))
            {
                conn.Reducers.ConsumeItem(cell.ItemInstanceId);
                return;
            }

            if (cell.Definition != null && TryResolveEquipSlot(conn, cell.Definition, out string targetSlot))
                conn.Reducers.EquipItem(cell.ItemInstanceId, targetSlot);
        }

        private void HandlePrimaryClick(InventoryGridCell cell)
        {
            if (!cell.HasItem || !IsSpellbook(cell.Definition))
                return;

            SpellbookPanel.Open(cell.ItemInstanceId, cell.Definition?.DisplayName ?? "Spellbook");
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

        private void HandlePrimaryClick(EquipmentSlotCell cell)
        {
            if (!cell.HasItem || !IsSpellbook(cell.Definition))
                return;

            SpellbookPanel.Open(cell.ItemInstanceId, cell.Definition?.DisplayName ?? "Spellbook");
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
                "SPELLBOOK" => loadout.SpellbookItemId,
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

        private static TooltipData BuildItemTooltip(
            DbConnection? conn,
            ItemInstance? item,
            ItemDefinition? definition,
            string footnote)
        {
            if (item == null)
                return default;

            string name = !string.IsNullOrWhiteSpace(definition?.DisplayName)
                ? definition.DisplayName
                : item.ItemInstanceId;
            string subtitle = BuildItemSubtitle(definition);
            string description = BuildItemDescription(conn, item, definition);
            List<TooltipStat> stats = BuildItemTooltipStats(conn, item, definition);
            return new TooltipData(
                name,
                subtitle,
                description,
                ArenaUiTheme.RarityColor(definition?.Rarity),
                stats.Count > 0 ? stats : null,
                footnote);
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
                AppendConsumableTooltipPart(parts, definition);
                AppendLabeledTooltipPart(parts, "Weapon", definition.WeaponKind);
                AppendLabeledTooltipPart(parts, "Profile", definition.CombatProfileId);
                if (definition.UniqueEquipped)
                    parts.Add("Unique-equipped");
            }

            AppendSpellbookTooltipParts(conn, item.ItemInstanceId, parts);

            return string.Join("\n", parts);
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

        private static List<TooltipStat> BuildItemTooltipStats(
            DbConnection? conn,
            ItemInstance item,
            ItemDefinition? definition)
        {
            List<TooltipStat> stats = new();
            if (definition != null && definition.PhysicalResistance > 0.0001f)
                stats.Add(new TooltipStat("Physical Resistance", FormatAggregateValue("PHYSICAL_RESISTANCE", definition.PhysicalResistance)));

            if (conn == null || string.IsNullOrWhiteSpace(item.ItemInstanceId))
                return stats;

            List<ItemAffixInstance> affixes = new();
            foreach (ItemAffixInstance affix in conn.Db.ItemAffixInstance.ItemInstanceId.Filter(item.ItemInstanceId))
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
                string kind = string.IsNullOrWhiteSpace(affix.ModifierKind)
                    ? string.Empty
                    : affix.ModifierKind.Trim().Replace('-', '_').ToUpperInvariant();
                string label = IsAllocatedStatModifier(kind)
                    ? FormatTooltipTitle(kind)
                    : ResolveAffixLabel(conn, affix, kind);
                if (string.IsNullOrWhiteSpace(label))
                    continue;

                stats.Add(new TooltipStat(label, FormatAggregateValue(kind, affix.Value)));
            }

            return stats;
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
                return sort != 0
                    ? sort
                    : string.Compare(left.Key, right.Key, StringComparison.Ordinal);
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

        private static bool IsSpellbook(ItemDefinition? definition)
            => string.Equals(
                WireIdentifier.Normalize(definition?.ItemKind),
                "SPELLBOOK",
                StringComparison.Ordinal);

        private static bool IsConsumable(ItemDefinition? definition)
            => string.Equals(
                WireIdentifier.Normalize(definition?.ItemKind),
                "CONSUMABLE",
                StringComparison.Ordinal);

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

        private static bool IsAllocatedStatModifier(string modifierKind)
        {
            string normalized = string.IsNullOrWhiteSpace(modifierKind)
                ? string.Empty
                : modifierKind.Trim().Replace('-', '_').ToUpperInvariant();
            return normalized is "MIGHT" or "INSIGHT" or "FINESSE" or "FORTITUDE";
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

        private static string FormatTooltipTitle(string value)
        {
            string formatted = FormatTooltipValue(value);
            if (string.IsNullOrWhiteSpace(formatted))
                return string.Empty;

            return char.ToUpperInvariant(formatted[0]) + formatted[1..];
        }

        private void BuildUi()
        {
            GameObject canvasGo = new("InventoryCanvas");
            canvasGo.transform.SetParent(transform, false);
            _canvas = ArenaUiKit.MakeOverlayCanvas(canvasGo, 42);

            Vector2 inventorySize = WindowSizeForGrid(10, 4);
            Vector2 characterSize = WindowSizeForGrid(DollColumns, DollRows) + new Vector2(StatsColumnWidth + WindowGap, 0f);
            Vector2 lootSize = WindowSizeForGrid(4, 4);

            _inventoryWindow = CreateGridWindow(
                canvasGo.transform,
                "InventoryWindow",
                "Inventory",
                inventorySize,
                new Vector2(-28f, 0f),
                out _inventoryGrid);

            _characterWindow = CreateCharacterWindow(
                canvasGo.transform,
                characterSize,
                new Vector2(-28f - inventorySize.x - WindowGap, 0f));

            _lootWindow = CreateGridWindow(
                canvasGo.transform,
                "LootWindow",
                "Loot",
                lootSize,
                new Vector2(-28f - inventorySize.x - WindowGap - characterSize.x - WindowGap, 48f),
                out _lootGrid);
            _lootWindow.SetSubtitle(string.Empty);

            _inventoryWindow.CloseRequested += () => SetInventoryOpen(false);
            _characterWindow.CloseRequested += () => SetInventoryOpen(false);
            _lootWindow.CloseRequested += () => CloseLootPanel();

            _inventoryWindow.SetVisible(false, instant: true);
            _characterWindow.SetVisible(false, instant: true);
            _lootWindow.SetVisible(false, instant: true);
        }

        private static ArenaWindow CreateGridWindow(
            Transform parent,
            string name,
            string title,
            Vector2 size,
            Vector2 anchoredPosition,
            out RectTransform grid)
        {
            ArenaWindow window = ArenaWindow.Create(parent, name, title, size);
            AnchorRightMiddle(window.Rect, anchoredPosition);

            grid = ArenaUiKit.MakeRect(window.Content, "Grid");
            ArenaUiKit.Fill(grid);
            EnsureGridLayout(grid, 1);
            return window;
        }

        private ArenaWindow CreateCharacterWindow(Transform parent, Vector2 size, Vector2 anchoredPosition)
        {
            ArenaWindow window = ArenaWindow.Create(parent, "CharacterWindow", "Character", size);
            AnchorRightMiddle(window.Rect, anchoredPosition);

            RectTransform body = window.Content;

            // Showcase backdrop behind the central doll column.
            RectTransform showcase = ArenaUiKit.MakePanel(body, "Showcase", ArenaUiTheme.Showcase, raycastTarget: false, hairline: true, cornerRadius: ArenaUiSprites.SmallRadius);
            showcase.anchorMin = new Vector2(0f, 1f);
            showcase.anchorMax = new Vector2(0f, 1f);
            showcase.pivot = new Vector2(0f, 1f);
            showcase.anchoredPosition = new Vector2(ActionBarLayout.Pitch, -ActionBarLayout.Pitch * 2f);
            showcase.sizeDelta = new Vector2(
                ActionBarLayout.SlotSize * 3f + ActionBarLayout.Gap * 2f,
                ActionBarLayout.SlotSize * 3f + ActionBarLayout.Gap * 2f);

            _equipmentSlots.Clear();
            foreach (EquipmentSlotSpec spec in EquipmentSlots)
            {
                EquipmentSlotCell slot = CreateEquipmentSlot(body, spec);
                _equipmentSlots[spec.SlotId] = slot;
            }

            // Attributes column to the right of the doll.
            float dollWidth = DollColumns * CellSize + (DollColumns - 1) * CellSpacing;
            RectTransform statsColumn = ArenaUiKit.MakeRect(body, "Attributes");
            ArenaUiKit.SetAnchors(
                statsColumn,
                new Vector2(0f, 0f),
                new Vector2(0f, 1f),
                new Vector2(dollWidth + WindowGap, 0f),
                new Vector2(dollWidth + WindowGap + StatsColumnWidth, 0f));

            TextMeshProUGUI heading = ArenaUiKit.MakeSectionLabel(statsColumn, "Heading", "Attributes");
            ArenaUiKit.SetAnchors(
                heading.rectTransform,
                new Vector2(0f, 1f),
                new Vector2(1f, 1f),
                new Vector2(0f, -22f),
                Vector2.zero);

            RectTransform divider = ArenaUiKit.MakeDivider(statsColumn);
            ArenaUiKit.SetAnchors(
                divider,
                new Vector2(0f, 1f),
                new Vector2(1f, 1f),
                new Vector2(0f, -28f),
                new Vector2(0f, -27f));

            _statsList = ArenaUiKit.MakeRect(statsColumn, "List");
            ArenaUiKit.SetAnchors(
                _statsList,
                Vector2.zero,
                Vector2.one,
                Vector2.zero,
                new Vector2(0f, -34f));
            VerticalLayoutGroup layout = _statsList.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 2f;
            layout.childControlWidth = true;
            layout.childControlHeight = false;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            layout.childAlignment = TextAnchor.UpperLeft;

            return window;
        }

        private static void AnchorRightMiddle(RectTransform rect, Vector2 anchoredPosition)
        {
            rect.anchorMin = new Vector2(1f, 0.5f);
            rect.anchorMax = new Vector2(1f, 0.5f);
            rect.pivot = new Vector2(1f, 0.5f);
            rect.anchoredPosition = anchoredPosition;
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
            background.color = ArenaUiTheme.CellEmpty;
            ArenaUiKit.ApplyRounding(background, ArenaUiSprites.SmallRadius);

            GameObject labelGo = new("Label");
            labelGo.transform.SetParent(cellGo.transform, false);
            RectTransform labelRt = labelGo.AddComponent<RectTransform>();
            labelRt.anchorMin = Vector2.zero;
            labelRt.anchorMax = Vector2.one;
            labelRt.offsetMin = new Vector2(4f, 4f);
            labelRt.offsetMax = new Vector2(-4f, -4f);

            TextMeshProUGUI label = labelGo.AddComponent<TextMeshProUGUI>();
            TMP_FontAsset? font = ArenaUiTheme.BodyFont;
            if (font != null)
                label.font = font;
            label.fontSize = 10f;
            label.color = ArenaUiTheme.MutedText;
            label.alignment = TextAlignmentOptions.Center;
            label.textWrappingMode = TextWrappingModes.Normal;
            label.overflowMode = TextOverflowModes.Ellipsis;
            label.raycastTarget = false;

            GameObject iconGo = new("Icon");
            iconGo.transform.SetParent(cellGo.transform, false);
            RectTransform iconRt = iconGo.AddComponent<RectTransform>();
            iconRt.anchorMin = Vector2.zero;
            iconRt.anchorMax = Vector2.one;
            iconRt.offsetMin = Vector2.one * ActionBarLayout.IconInset;
            iconRt.offsetMax = Vector2.one * -ActionBarLayout.IconInset;

            Image icon = iconGo.AddComponent<Image>();
            icon.color = Color.white;
            icon.preserveAspect = true;
            icon.raycastTarget = false;
            icon.enabled = false;

            labelGo.transform.SetAsLastSibling();
            Image border = ArenaUiKit.AddBorder(rt, ArenaUiTheme.Hairline, ArenaUiSprites.SmallRadius);
            ArenaUiKit.ApplySurface(border, ArenaUiSprites.SlotFrame, ArenaUiTheme.Hairline);
            border.rectTransform.SetAsFirstSibling();

            return cellGo.AddComponent<EquipmentSlotCell>().Initialize(background, icon, label, border);
        }

        private static void EnsureGridLayout(RectTransform grid, int columns)
        {
            GridLayoutGroup layout = ArenaUiKit.EnsureComponent<GridLayoutGroup>(grid.gameObject);
            layout.cellSize = ActionBarLayout.SlotVector;
            layout.spacing = ActionBarLayout.GapVector;
            layout.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            layout.constraintCount = Mathf.Max(1, columns);
            layout.childAlignment = TextAnchor.UpperLeft;
        }

        private static void ResizeWindowForGrid(RectTransform grid, int columns, int rows)
        {
            ArenaWindow? window = grid.GetComponentInParent<ArenaWindow>(true);
            if (window == null)
                return;

            window.Rect.sizeDelta = WindowSizeForGrid(columns, rows);
        }

        private static Vector2 WindowSizeForGrid(int columns, int rows)
        {
            int safeColumns = Mathf.Max(1, columns);
            int safeRows = Mathf.Max(1, rows);
            return new Vector2(
                safeColumns * CellSize + (safeColumns - 1) * CellSpacing + ArenaUiTheme.ContentPadding * 2f,
                safeRows * CellSize + (safeRows - 1) * CellSpacing
                + ArenaUiTheme.ContentPadding * 2f
                + ArenaUiTheme.HeaderHeight
                + ArenaUiTheme.AccentBarThickness);
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
            background.color = ArenaUiTheme.CellEmpty;
            ArenaUiKit.ApplyRounding(background, ArenaUiSprites.SmallRadius);

            GameObject iconGo = new("Icon");
            iconGo.transform.SetParent(cellGo.transform, false);
            RectTransform iconRt = iconGo.AddComponent<RectTransform>();
            iconRt.anchorMin = Vector2.zero;
            iconRt.anchorMax = Vector2.one;
            iconRt.offsetMin = Vector2.one * ActionBarLayout.IconInset;
            iconRt.offsetMax = Vector2.one * -ActionBarLayout.IconInset;

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
            TMP_FontAsset? cellFont = ArenaUiTheme.BodyFont;
            if (cellFont != null)
                label.font = cellFont;
            label.fontSize = 10f;
            label.color = ArenaUiTheme.Text;
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
            TMP_FontAsset? quantityFont = ArenaUiTheme.StrongFont;
            if (quantityFont != null)
                quantity.font = quantityFont;
            quantity.fontSize = 10f;
            quantity.fontStyle = FontStyles.Bold;
            quantity.color = ArenaUiTheme.Text;
            quantity.alignment = TextAlignmentOptions.BottomRight;
            quantity.textWrappingMode = TextWrappingModes.NoWrap;
            quantity.raycastTarget = false;

            Image border = ArenaUiKit.AddBorder(rt, ArenaUiTheme.Hairline, ArenaUiSprites.SmallRadius);
            ArenaUiKit.ApplySurface(border, ArenaUiSprites.SlotFrame, ArenaUiTheme.Hairline);
            border.rectTransform.SetAsFirstSibling();
            cellGo.AddComponent<InventoryGridCell>().Initialize(background, icon, label, quantity, border);
        }

        private RectTransform CreateDragGhost(string displayName, string iconId)
        {
            GameObject go = new("InventoryDragGhost");
            go.transform.SetParent(_canvas!.transform, false);
            go.transform.SetAsLastSibling();

            RectTransform rt = go.AddComponent<RectTransform>();
            rt.sizeDelta = new Vector2(132f, 42f);

            Image image = go.AddComponent<Image>();
            image.color = ArenaUiTheme.PanelStrong;
            image.raycastTarget = false;
            ArenaUiKit.ApplyRounding(image, ArenaUiSprites.SmallRadius);
            ArenaUiKit.AddBorder(rt, ArenaUiTheme.HairlineStrong, ArenaUiSprites.SmallRadius);

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

            TextMeshProUGUI label = ArenaUiKit.MakeText(
                go.transform,
                "Text",
                displayName,
                12f,
                ArenaUiTheme.Text,
                alignment: iconSprite == null ? TextAlignmentOptions.Center : TextAlignmentOptions.MidlineLeft);
            RectTransform labelRt = label.rectTransform;
            labelRt.anchorMin = Vector2.zero;
            labelRt.anchorMax = Vector2.one;
            labelRt.offsetMin = new Vector2(iconSprite == null ? 8f : 50f, 4f);
            labelRt.offsetMax = new Vector2(-8f, -4f);

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
            private Image? _border;
            private TooltipTarget? _tooltip;
            private string _emptyLabel = string.Empty;

            public string SlotId { get; private set; } = string.Empty;
            public string ItemInstanceId { get; private set; } = string.Empty;
            public ItemDefinition? Definition { get; private set; }
            public bool HasItem => !string.IsNullOrWhiteSpace(ItemInstanceId);

            public EquipmentSlotCell Initialize(Image background, Image icon, TextMeshProUGUI label, Image border)
            {
                _background = background;
                _icon = icon;
                _label = label;
                _border = border;
                _tooltip = ArenaUiKit.EnsureComponent<TooltipTarget>(gameObject);
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
                    _background.color = item == null ? ArenaUiTheme.CellEmpty : ArenaUiTheme.CellFilled;

                if (_border != null)
                    _border.color = RarityOutline(item, definition);

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
                        BuildItemTooltip(
                            NetworkManager.Instance?.Conn,
                            item,
                            definition,
                            item == null ? string.Empty : "Right-click to unequip - Drag to bag"),
                        pollHover: true);
            }

            public void OnPointerClick(PointerEventData eventData)
            {
                if (eventData.button == PointerEventData.InputButton.Left)
                {
                    _controller?.HandlePrimaryClick(this);
                    return;
                }

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

        private static Color RarityOutline(ItemInstance? item, ItemDefinition? definition)
        {
            bool authoredFrame = ArenaUiSprites.SlotFrame.Authored;
            if (item == null || definition == null || string.IsNullOrWhiteSpace(definition.Rarity))
                return authoredFrame ? Color.white : ArenaUiTheme.Hairline;

            string normalized = definition.Rarity.Trim().ToUpperInvariant();
            if (normalized is "COMMON" or "")
                return authoredFrame ? Color.white : ArenaUiTheme.HairlineStrong;

            // Authored frames are painted art: tint them with the rarity color.
            Color color = ArenaUiTheme.RarityColor(definition.Rarity);
            color.a = authoredFrame ? 1f : 0.85f;
            return color;
        }

        private sealed class InventoryGridCell : MonoBehaviour, IInventoryDropTarget, IPointerClickHandler, IBeginDragHandler, IDragHandler, IEndDragHandler
        {
            private InventoryController? _controller;
            private Image? _background;
            private Image? _icon;
            private TextMeshProUGUI? _label;
            private TextMeshProUGUI? _quantity;
            private Image? _border;
            private TooltipTarget? _tooltip;

            public string Context { get; private set; } = string.Empty;
            public string ContainerId { get; private set; } = string.Empty;
            public string ItemInstanceId { get; private set; } = string.Empty;
            public uint X { get; private set; }
            public uint Y { get; private set; }
            public uint ItemQuantity { get; private set; }
            public ItemDefinition? Definition { get; private set; }
            public bool HasItem => !string.IsNullOrWhiteSpace(ItemInstanceId);

            public void Initialize(Image background, Image icon, TextMeshProUGUI label, TextMeshProUGUI quantity, Image border)
            {
                _background = background;
                _icon = icon;
                _label = label;
                _quantity = quantity;
                _border = border;
                _tooltip = ArenaUiKit.EnsureComponent<TooltipTarget>(gameObject);
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
                    _background.color = item == null ? ArenaUiTheme.CellEmpty : ArenaUiTheme.CellFilled;

                if (_border != null)
                    _border.color = RarityOutline(item, definition);

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
                        BuildItemTooltip(
                            NetworkManager.Instance?.Conn,
                            item,
                            definition,
                            item == null ? string.Empty : BuildCellFootnote(context, definition)),
                        pollHover: true);
            }

            public void OnPointerClick(PointerEventData eventData)
            {
                if (eventData.button == PointerEventData.InputButton.Left)
                {
                    _controller?.HandlePrimaryClick(this);
                    return;
                }

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
