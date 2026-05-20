#nullable enable

using System;
using System.Collections.Generic;
using Arena.Combat;
using SpacetimeDB.Types;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Arena.UI
{
    public readonly struct LoadoutActionDragPayload
    {
        public readonly string ActionKind;
        public readonly string ActionId;
        public readonly string AbilityId;
        public readonly string DisplayName;
        public readonly string SourceSlotId;

        public LoadoutActionDragPayload(
            string actionKind,
            string actionId,
            string abilityId,
            string displayName,
            string sourceSlotId = "")
        {
            ActionKind = WireIdentifier.Normalize(actionKind);
            ActionId = WireIdentifier.Normalize(actionId);
            AbilityId = WireIdentifier.Normalize(abilityId);
            DisplayName = displayName;
            SourceSlotId = WireIdentifier.Normalize(sourceSlotId);
        }

        public bool HasValue => !string.IsNullOrWhiteSpace(ActionKind)
            && !string.IsNullOrWhiteSpace(ActionId);

        public bool HasSourceSlot => !string.IsNullOrWhiteSpace(SourceSlotId);

        public static LoadoutActionDragPayload? From(ActiveSelectableLoadoutAction action, string sourceSlotId)
        {
            if (!action.HasAssignedAction)
                return null;

            return new LoadoutActionDragPayload(
                action.ActionKind ?? string.Empty,
                action.ActionRefId ?? string.Empty,
                action.AbilityId ?? string.Empty,
                action.DisplayName ?? string.Empty,
                sourceSlotId);
        }
    }

    public sealed class LoadoutActionDropSlot : MonoBehaviour
    {
        public Canvas? Canvas { get; private set; }
        public string SlotId { get; private set; } = string.Empty;
        public RectTransform RectTransform => (RectTransform)transform;

        private bool _registered;

        public void Configure(Canvas canvas, string slotId)
        {
            string normalizedSlotId = WireIdentifier.Normalize(slotId);
            if (_registered && (!ReferenceEquals(Canvas, canvas) || !string.Equals(SlotId, normalizedSlotId, StringComparison.Ordinal)))
            {
                LoadoutActionDropRegistry.Unregister(this);
                _registered = false;
            }

            Canvas = canvas;
            SlotId = normalizedSlotId;
            RegisterIfReady();
        }

        private void OnEnable()
        {
            RegisterIfReady();
        }

        private void OnDisable()
        {
            if (!_registered)
                return;

            LoadoutActionDropRegistry.Unregister(this);
            _registered = false;
        }

        private void OnDestroy()
        {
            OnDisable();
        }

        private void RegisterIfReady()
        {
            if (_registered || Canvas == null || string.IsNullOrWhiteSpace(SlotId) || !isActiveAndEnabled)
                return;

            LoadoutActionDropRegistry.Register(this);
            _registered = true;
        }
    }

    public sealed class LoadoutActionDragSource : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler, IPointerClickHandler
    {
        private const float SnapPadding = 34f;
        private const float GhostWidth = 128f;
        private const float GhostHeight = 44f;

        private Canvas? _canvas;
        private Func<LoadoutActionDragPayload?>? _payloadProvider;
        private Action<LoadoutActionDragPayload, string?>? _onDrop;
        private Action? _onClick;
        private Action<LoadoutActionDragPayload>? _onDragStarted;
        private LoadoutActionDragPayload _activePayload;
        private RectTransform? _ghost;
        private bool _dragging;
        private float _suppressClickUntil;

        public void Configure(
            Canvas canvas,
            Func<LoadoutActionDragPayload?>? payloadProvider,
            Action<LoadoutActionDragPayload, string?>? onDrop,
            Action? onClick = null,
            Action<LoadoutActionDragPayload>? onDragStarted = null)
        {
            _canvas = canvas;
            _payloadProvider = payloadProvider;
            _onDrop = onDrop;
            _onClick = onClick;
            _onDragStarted = onDragStarted;
        }

        public static bool ShouldSuppressClick(GameObject source)
        {
            LoadoutActionDragSource? dragSource = source.GetComponent<LoadoutActionDragSource>();
            return dragSource != null && Time.unscaledTime <= dragSource._suppressClickUntil;
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            if (eventData.button != PointerEventData.InputButton.Left
                || eventData.dragging
                || Time.unscaledTime <= _suppressClickUntil)
                return;

            _onClick?.Invoke();
        }

        public void OnBeginDrag(PointerEventData eventData)
        {
            if (eventData.button != PointerEventData.InputButton.Left || _canvas == null || _payloadProvider == null)
                return;

            LoadoutActionDragPayload? payload = _payloadProvider.Invoke();
            if (payload == null || !payload.Value.HasValue)
                return;

            _activePayload = payload.Value;
            _dragging = true;
            _onDragStarted?.Invoke(_activePayload);
            _ghost = CreateGhost(_canvas, _activePayload.DisplayName);
            MoveGhost(eventData.position);
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (!_dragging)
                return;

            MoveGhost(eventData.position);
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            if (!_dragging)
                return;

            _dragging = false;
            DestroyGhost();
            _suppressClickUntil = Time.unscaledTime + 0.08f;
            string? targetSlotId = _canvas == null
                ? null
                : LoadoutActionDropRegistry.FindNearestSlot(_canvas, eventData.position, SnapPadding)?.SlotId;
            _onDrop?.Invoke(_activePayload, targetSlotId);
            _activePayload = default;
        }

        private void OnDisable()
        {
            CancelActiveDrag();
        }

        private void OnDestroy()
        {
            CancelActiveDrag();
        }

        private void CancelActiveDrag()
        {
            if (!_dragging && _ghost == null)
                return;

            _dragging = false;
            DestroyGhost();
            _activePayload = default;
        }

        private static RectTransform CreateGhost(Canvas canvas, string displayName)
        {
            GameObject go = new("LoadoutActionDragGhost");
            go.transform.SetParent(canvas.transform, false);
            go.transform.SetAsLastSibling();

            RectTransform rt = go.AddComponent<RectTransform>();
            rt.sizeDelta = new Vector2(GhostWidth, GhostHeight);

            Image image = go.AddComponent<Image>();
            image.color = new Color(0.04f, 0.05f, 0.06f, 0.92f);
            image.raycastTarget = false;

            Outline outline = go.AddComponent<Outline>();
            outline.effectColor = new Color(1f, 1f, 1f, 0.22f);
            outline.effectDistance = new Vector2(1f, -1f);

            GameObject labelGo = new("Text");
            labelGo.transform.SetParent(go.transform, false);
            RectTransform labelRt = labelGo.AddComponent<RectTransform>();
            labelRt.anchorMin = Vector2.zero;
            labelRt.anchorMax = Vector2.one;
            labelRt.offsetMin = new Vector2(8f, 4f);
            labelRt.offsetMax = new Vector2(-8f, -4f);
            TextMeshProUGUI label = labelGo.AddComponent<TextMeshProUGUI>();
            label.font = TMP_Settings.defaultFontAsset
                ?? Resources.Load<TMP_FontAsset>("Fonts & Materials/LiberationSans SDF");
            label.fontSize = 12;
            label.alignment = TextAlignmentOptions.Center;
            label.color = Color.white;
            label.textWrappingMode = TextWrappingModes.Normal;
            label.raycastTarget = false;
            label.text = displayName;

            return rt;
        }

        private void MoveGhost(Vector2 screenPosition)
        {
            if (_canvas == null || _ghost == null)
                return;

            RectTransform canvasRect = (RectTransform)_canvas.transform;
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    canvasRect,
                    screenPosition,
                    _canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : _canvas.worldCamera,
                    out Vector2 localPoint))
            {
                _ghost.anchoredPosition = localPoint + new Vector2(16f, -16f);
            }
        }

        private void DestroyGhost()
        {
            if (_ghost == null)
                return;

            Destroy(_ghost.gameObject);
            _ghost = null;
        }
    }

    public static class LoadoutActionDropApplier
    {
        public static void ApplyDrop(DbConnection conn, string specId, LoadoutActionDragPayload payload, string? targetSlotId)
        {
            if (!payload.HasValue || string.IsNullOrWhiteSpace(specId))
                return;

            string normalizedTargetSlotId = WireIdentifier.Normalize(targetSlotId);
            if (string.IsNullOrWhiteSpace(normalizedTargetSlotId))
            {
                if (payload.HasSourceSlot)
                    conn.Reducers.ClearSavedSpecSlot(specId, payload.SourceSlotId);
                return;
            }

            if (payload.HasSourceSlot && string.Equals(payload.SourceSlotId, normalizedTargetSlotId, StringComparison.Ordinal))
                return;

            SavedSpecSlotAssignment? targetAssignment = FindAssignment(conn, specId, normalizedTargetSlotId);
            if (payload.HasSourceSlot && targetAssignment != null)
            {
                AssignExistingAssignmentToSlot(conn, specId, targetAssignment, payload.SourceSlotId);
                AssignPayloadToSlot(conn, specId, payload, normalizedTargetSlotId);
                return;
            }

            AssignPayloadToSlot(conn, specId, payload, normalizedTargetSlotId);
            if (payload.HasSourceSlot)
                conn.Reducers.ClearSavedSpecSlot(specId, payload.SourceSlotId);
        }

        private static SavedSpecSlotAssignment? FindAssignment(DbConnection conn, string specId, string slotId)
        {
            foreach (SavedSpecSlotAssignment assignment in conn.Db.SavedSpecSlotAssignment.SpecId.Filter(specId))
            {
                if (string.Equals(WireIdentifier.Normalize(assignment.SlotId), slotId, StringComparison.Ordinal))
                    return assignment;
            }

            return null;
        }

        private static void AssignExistingAssignmentToSlot(
            DbConnection conn,
            string specId,
            SavedSpecSlotAssignment assignment,
            string targetSlotId)
        {
            string actionKind = WireIdentifier.Normalize(assignment.ActionKind);
            string actionId = WireIdentifier.Normalize(assignment.ActionId);
            if (string.IsNullOrWhiteSpace(actionKind) && !string.IsNullOrWhiteSpace(assignment.AbilityId))
            {
                actionKind = ActionKinds.Ability;
                actionId = WireIdentifier.Normalize(assignment.AbilityId);
            }

            if (string.Equals(actionKind, ActionKinds.Ability, StringComparison.Ordinal))
            {
                conn.Reducers.AssignSavedSpecAbilityToSlot(specId, targetSlotId, actionId);
                return;
            }

            conn.Reducers.AssignSavedSpecActionToSlot(specId, targetSlotId, actionKind, actionId);
        }

        private static void AssignPayloadToSlot(
            DbConnection conn,
            string specId,
            LoadoutActionDragPayload payload,
            string targetSlotId)
        {
            if (string.Equals(payload.ActionKind, ActionKinds.Ability, StringComparison.Ordinal))
            {
                conn.Reducers.AssignSavedSpecAbilityToSlot(specId, targetSlotId, payload.ActionId);
                return;
            }

            conn.Reducers.AssignSavedSpecActionToSlot(specId, targetSlotId, payload.ActionKind, payload.ActionId);
        }
    }

    internal static class LoadoutActionDropRegistry
    {
        private static readonly List<LoadoutActionDropSlot> Slots = new();

        public static void Register(LoadoutActionDropSlot slot)
        {
            if (!Slots.Contains(slot))
                Slots.Add(slot);
        }

        public static void Unregister(LoadoutActionDropSlot slot)
        {
            Slots.Remove(slot);
        }

        public static LoadoutActionDropSlot? FindNearestSlot(Canvas canvas, Vector2 screenPosition, float snapPadding)
        {
            LoadoutActionDropSlot? best = null;
            float bestDistance = float.MaxValue;
            Camera? camera = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;

            for (int i = Slots.Count - 1; i >= 0; i--)
            {
                LoadoutActionDropSlot slot = Slots[i];
                if (slot == null)
                {
                    Slots.RemoveAt(i);
                    continue;
                }

                if (!slot.isActiveAndEnabled || slot.Canvas != canvas)
                    continue;

                if (!TryGetInflatedScreenRect(slot.RectTransform, camera, snapPadding, out Rect rect))
                    continue;

                if (!rect.Contains(screenPosition))
                    continue;

                float distance = Vector2.SqrMagnitude(screenPosition - rect.center);
                if (distance >= bestDistance)
                    continue;

                bestDistance = distance;
                best = slot;
            }

            return best;
        }

        private static bool TryGetInflatedScreenRect(
            RectTransform rectTransform,
            Camera? camera,
            float padding,
            out Rect rect)
        {
            Vector3[] corners = new Vector3[4];
            rectTransform.GetWorldCorners(corners);
            Vector2 min = RectTransformUtility.WorldToScreenPoint(camera, corners[0]);
            Vector2 max = min;

            for (int i = 1; i < corners.Length; i++)
            {
                Vector2 point = RectTransformUtility.WorldToScreenPoint(camera, corners[i]);
                min = Vector2.Min(min, point);
                max = Vector2.Max(max, point);
            }

            rect = Rect.MinMaxRect(
                min.x - padding,
                min.y - padding,
                max.x + padding,
                max.y + padding);
            return rect.width > 0f && rect.height > 0f;
        }
    }
}
