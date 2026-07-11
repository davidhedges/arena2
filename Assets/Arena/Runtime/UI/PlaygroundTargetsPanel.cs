#nullable enable
using System;
using System.Collections.Generic;
using System.Reflection;
using Arena.Entity;
using Arena.Network;
using Arena.Presentation;
using SpacetimeDB.Types;
using UnityEngine;
using UnityEngine.UI;

namespace Arena.UI
{
    /// <summary>
    /// Playground-only target spawner for testing targeting rules and party frames.
    /// This is intentionally separate from normal HUD, party, and match UI.
    /// </summary>
    public sealed class PlaygroundTargetsPanel : MonoBehaviour, IEscapeCloseable
    {
        private const float Pad = 10f;
        private const float ButtonTopOffset = 130f;
        private const float MenuTopOffset = 160f;
        private const string KindHostile = "HOSTILE";
        private const string KindNeutral = "NEUTRAL";
        private const string KindPartyMember = "PARTY_MEMBER";
        private const string FactionHostile = "HOSTILE";
        private const string FactionNeutral = "NEUTRAL";
        private const string FactionFriendly = "FRIENDLY";
        private const string KoboldWarriorRed = "KOBOLD_WARRIOR_RD_SWORD_SHIELD";
        private const string KoboldWarriorGreen = "KOBOLD_WARRIOR_GN_SPEAR";
        private const string KoboldThiefBlack = "KOBOLD_THIEF_BK_DUAL_SWORD";

        private GameObject _menuRoot = null!;
        private GameObject _meshEffectMenuRoot = null!;
        private Text _statusText = null!;
        private DbConnection? _subscribedConnection;
        private GameObject? _selectedMeshEffectPrefab;
        private GameObject? _meshEffectInstance;
        private Transform? _boundWeaponVisual;
        private Renderer? _meshEffectTargetRenderer;
        private Material[] _originalWeaponMaterials = Array.Empty<Material>();
        private WeaponAttachmentController? _boundWeaponAttachments;
        private int _boundWeaponVisualVersion = -1;
        private float _statusUntilTime;

        public int EscapeClosePriority => 40;
        public bool IsEscapeCloseable =>
            (_meshEffectMenuRoot != null && _meshEffectMenuRoot.activeSelf)
            || (_menuRoot != null && _menuRoot.activeSelf);

        private void Awake()
        {
            Build();
        }

        private void Build()
        {
            var toggleButton = MakeHudButton(transform, "PlaygroundToggleButton", "PLAYGROUND",
                new Vector2(1f, 1f), new Vector2(1f, 1f),
                new Vector2(122f, 24f), new Vector2(-Pad, -ButtonTopOffset),
                HeatUiStyle.PanelStrong);
            toggleButton.onClick.AddListener(ToggleMenu);

            _menuRoot = Panel("PlaygroundTargetsMenu", transform,
                new Vector2(1f, 1f), new Vector2(1f, 1f),
                new Vector2(206f, 296f), new Vector2(-Pad, -MenuTopOffset));
            Img(_menuRoot, HeatUiStyle.Panel);
            HeatUiStyle.StylePanel(_menuRoot, raycastTarget: false);
            HeatUiStyle.AddAccentBar(
                _menuRoot.transform,
                "Accent",
                new Vector2(0f, 0f),
                new Vector2(0f, 1f),
                Vector2.zero,
                new Vector2(3f, 0f));

            var title = Label(_menuRoot.transform, "Title",
                new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                new Vector2(190f, 22f), new Vector2(0f, -8f),
                11, HeatUiStyle.Text, TextAnchor.MiddleCenter);
            title.text = "PLAYGROUND";
            title.fontStyle = FontStyle.Bold;

            BuildTargetButton("HostileButton", "PLAYER HOSTILE", 0, false, () => SpawnTarget(KindHostile));
            BuildTargetButton("NeutralButton", "PLAYER NEUTRAL", 1, false, () => SpawnTarget(KindNeutral));
            BuildTargetButton("PartyMemberButton", "PLAYER FRIENDLY", 2, false, () => SpawnTarget(KindPartyMember));
            BuildTargetButton("NpcHostileButton", "KOBOLD HOSTILE", 3, false, () => SpawnNpc(KoboldWarriorRed, FactionHostile, "KOBOLD HOSTILE"));
            BuildTargetButton("NpcNeutralButton", "KOBOLD NEUTRAL", 4, false, () => SpawnNpc(KoboldWarriorGreen, FactionNeutral, "KOBOLD NEUTRAL"));
            BuildTargetButton("NpcFriendlyButton", "KOBOLD FRIENDLY", 5, false, () => SpawnNpc(KoboldThiefBlack, FactionFriendly, "KOBOLD FRIENDLY"));
            BuildTargetButton("WeaponMeshEffectsButton", "WEAPON MESH FX", 6, false, ToggleMeshEffectMenu);
            BuildTargetButton("ClearButton", "CLEAR TARGETS", 7, true, ClearTargets);

            _statusText = Label(_menuRoot.transform, "Status",
                new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                new Vector2(190f, 18f), new Vector2(0f, -266f),
                9, new Color(0.75f, 0.80f, 0.90f), TextAnchor.MiddleCenter);
            _statusText.text = string.Empty;
            _statusText.resizeTextForBestFit = true;
            _statusText.resizeTextMinSize = 6;
            _statusText.resizeTextMaxSize = 9;

            BuildMeshEffectMenu();
            _menuRoot.SetActive(false);
        }

        private void BuildMeshEffectMenu()
        {
            _meshEffectMenuRoot = Panel("PlaygroundWeaponMeshEffectsMenu", transform,
                new Vector2(1f, 1f), new Vector2(1f, 1f),
                new Vector2(226f, 360f), new Vector2(-226f, -MenuTopOffset));
            Img(_meshEffectMenuRoot, HeatUiStyle.Panel);
            HeatUiStyle.StylePanel(_meshEffectMenuRoot, raycastTarget: false);
            HeatUiStyle.AddAccentBar(
                _meshEffectMenuRoot.transform,
                "Accent",
                new Vector2(1f, 0f),
                new Vector2(1f, 1f),
                Vector2.zero,
                new Vector2(3f, 0f));

            Text title = Label(_meshEffectMenuRoot.transform, "Title",
                new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                new Vector2(210f, 22f), new Vector2(0f, -8f),
                11, HeatUiStyle.Text, TextAnchor.MiddleCenter);
            title.text = "WEAPON MESH FX";
            title.fontStyle = FontStyle.Bold;

            PlaygroundWeaponMeshEffectCatalog? catalog =
                Resources.Load<PlaygroundWeaponMeshEffectCatalog>(PlaygroundWeaponMeshEffectCatalog.DefaultResourcePath);

            RectTransform menuRect = (RectTransform)_meshEffectMenuRoot.transform;
            RectTransform content = ArenaUiKit.MakeScrollView(menuRect, "EffectScroll", out ScrollRect scrollRect);
            RectTransform scrollRoot = (RectTransform)scrollRect.transform;
            scrollRoot.anchorMin = new Vector2(0f, 0f);
            scrollRoot.anchorMax = new Vector2(1f, 1f);
            scrollRoot.offsetMin = new Vector2(10f, 10f);
            scrollRoot.offsetMax = new Vector2(-10f, -36f);

            int row = 0;
            BuildMeshEffectButton(content, "ClearEffectButton", "NONE", row++, true, ClearSelectedWeaponMeshEffect);

            if (catalog != null)
            {
                for (int i = 0; i < catalog.Entries.Count; i++)
                {
                    PlaygroundWeaponMeshEffectCatalog.Entry entry = catalog.Entries[i];
                    if (entry == null || entry.prefab == null)
                        continue;

                    GameObject prefab = entry.prefab;
                    string label = string.IsNullOrWhiteSpace(entry.label)
                        ? prefab.name.Replace("MeshFX_", string.Empty).ToUpperInvariant()
                        : entry.label.Trim().ToUpperInvariant();
                    BuildMeshEffectButton(
                        content,
                        $"Effect_{prefab.name}",
                        label,
                        row++,
                        false,
                        () => SelectWeaponMeshEffect(prefab, label));
                }
            }

            content.sizeDelta = new Vector2(0f, row * 28f);
            _meshEffectMenuRoot.SetActive(false);
        }

        private static void BuildMeshEffectButton(
            Transform parent,
            string name,
            string label,
            int row,
            bool destructive,
            UnityEngine.Events.UnityAction action)
        {
            Button button = MakeHudButton(parent, name, label,
                new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                new Vector2(190f, 24f), new Vector2(0f, -row * 28f),
                destructive
                    ? new Color(0.42f, 0.08f, 0.07f, 0.96f)
                    : HeatUiStyle.RowAlt);
            button.onClick.AddListener(action);
        }

        private void BuildTargetButton(string name, string label, int row, bool destructive, UnityEngine.Events.UnityAction action)
        {
            var button = MakeHudButton(_menuRoot.transform, name, label,
                new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                new Vector2(176f, 24f), new Vector2(0f, -36f - row * 28f),
                destructive
                    ? new Color(0.42f, 0.08f, 0.07f, 0.96f)
                    : HeatUiStyle.RowAlt);
            button.onClick.AddListener(action);
        }

        private void ToggleMenu()
        {
            bool show = !_menuRoot.activeSelf;
            _menuRoot.SetActive(show);
            if (!show)
                _meshEffectMenuRoot.SetActive(false);
        }

        private void ToggleMeshEffectMenu()
        {
            _meshEffectMenuRoot.SetActive(!_meshEffectMenuRoot.activeSelf);
        }

        private void OnEnable()
        {
            RuntimeUiEscapeRouter.Register(this);
            TrySubscribeToReducerErrors();
        }

        private void Update()
        {
            TrySubscribeToReducerErrors();
            RefreshSelectedWeaponMeshEffectBinding();
            if (_statusText != null && _statusUntilTime > 0f && Time.unscaledTime >= _statusUntilTime)
            {
                _statusText.text = string.Empty;
                _statusUntilTime = 0f;
            }
        }

        private void OnDisable()
        {
            RuntimeUiEscapeRouter.Unregister(this);
            UnsubscribeFromReducerErrors();
        }

        private void OnDestroy()
        {
            RuntimeUiEscapeRouter.Unregister(this);
            UnsubscribeFromReducerErrors();
            ClearWeaponMeshEffect(clearSelection: true);
        }

        public bool TryCloseForEscape()
        {
            if (!IsEscapeCloseable)
                return false;

            if (_meshEffectMenuRoot.activeSelf)
            {
                _meshEffectMenuRoot.SetActive(false);
                return true;
            }

            _menuRoot.SetActive(false);
            return true;
        }

        private void SelectWeaponMeshEffect(GameObject prefab, string label)
        {
            _selectedMeshEffectPrefab = prefab;
            if (TryAttachSelectedWeaponMeshEffect())
                SetStatus($"{label} APPLIED", false);
            else
                SetStatus("NO EQUIPPED WEAPON", true);
        }

        private void ClearSelectedWeaponMeshEffect()
        {
            ClearWeaponMeshEffect(clearSelection: true);
            SetStatus("WEAPON FX CLEARED", false);
        }

        private void RefreshSelectedWeaponMeshEffectBinding()
        {
            if (_selectedMeshEffectPrefab == null)
                return;

            WeaponAttachmentController? attachments = ResolveLocalWeaponAttachments();
            if (attachments == null)
                return;

            bool versionChanged = !ReferenceEquals(attachments, _boundWeaponAttachments)
                || attachments.VisualVersion != _boundWeaponVisualVersion;
            if (!versionChanged && _meshEffectInstance != null)
                return;

            if (attachments.TryGetPrimaryVisibleVisual(out Transform visual)
                && ReferenceEquals(visual, _boundWeaponVisual)
                && _meshEffectInstance != null)
            {
                _boundWeaponAttachments = attachments;
                _boundWeaponVisualVersion = attachments.VisualVersion;
                return;
            }

            TryAttachSelectedWeaponMeshEffect();
        }

        private bool TryAttachSelectedWeaponMeshEffect()
        {
            GameObject? prefab = _selectedMeshEffectPrefab;
            WeaponAttachmentController? attachments = ResolveLocalWeaponAttachments();
            if (prefab == null
                || attachments == null
                || !attachments.TryGetPrimaryVisibleVisual(out Transform weaponVisual)
                || !TryFindWeaponRenderer(weaponVisual, out Renderer targetRenderer))
            {
                ClearWeaponMeshEffect(clearSelection: false);
                return false;
            }

            ClearWeaponMeshEffect(clearSelection: false);

            _originalWeaponMaterials = targetRenderer.sharedMaterials;
            GameObject instance = Instantiate(prefab, weaponVisual, worldPositionStays: false);
            instance.name = $"Playground_{prefab.name}";
            instance.SetActive(false);
            instance.transform.localPosition = Vector3.zero;
            instance.transform.localRotation = Quaternion.identity;
            instance.transform.localScale = Vector3.one;

            if (!TryAssignOverlayTarget(instance, targetRenderer))
            {
                DestroyUnityObject(instance);
                _originalWeaponMaterials = Array.Empty<Material>();
                return false;
            }

            _meshEffectInstance = instance;
            _meshEffectTargetRenderer = targetRenderer;
            _boundWeaponVisual = weaponVisual;
            _boundWeaponAttachments = attachments;
            _boundWeaponVisualVersion = attachments.VisualVersion;
            instance.SetActive(true);
            return true;
        }

        private static WeaponAttachmentController? ResolveLocalWeaponAttachments()
        {
            PlayerEntity? local = EntityRegistry.Instance?.LocalPlayerEntity;
            return local?.GameObject.GetComponent<WeaponAttachmentController>();
        }

        private static bool TryFindWeaponRenderer(Transform weaponVisual, out Renderer renderer)
        {
            Renderer[] candidates = weaponVisual.GetComponentsInChildren<Renderer>(includeInactive: true);
            for (int i = 0; i < candidates.Length; i++)
            {
                Renderer candidate = candidates[i];
                if (candidate is MeshRenderer or SkinnedMeshRenderer)
                {
                    renderer = candidate;
                    return true;
                }
            }

            renderer = null!;
            return false;
        }

        private static bool TryAssignOverlayTarget(GameObject effectRoot, Renderer targetRenderer)
        {
            MonoBehaviour[] behaviours = effectRoot.GetComponentsInChildren<MonoBehaviour>(includeInactive: true);
            for (int i = 0; i < behaviours.Length; i++)
            {
                MonoBehaviour behaviour = behaviours[i];
                if (behaviour == null)
                    continue;

                FieldInfo? field = behaviour.GetType().GetField(
                    "targetRenderer",
                    BindingFlags.Instance | BindingFlags.Public);
                if (field == null || !typeof(Renderer).IsAssignableFrom(field.FieldType))
                    continue;

                field.SetValue(behaviour, targetRenderer);
                return true;
            }

            return false;
        }

        private void ClearWeaponMeshEffect(bool clearSelection)
        {
            if (_meshEffectInstance != null)
                _meshEffectInstance.SetActive(false);

            RestoreOriginalWeaponMaterials();

            if (_meshEffectInstance != null)
                DestroyUnityObject(_meshEffectInstance);

            _meshEffectInstance = null;
            _meshEffectTargetRenderer = null;
            _boundWeaponVisual = null;
            _boundWeaponAttachments = null;
            _boundWeaponVisualVersion = -1;
            _originalWeaponMaterials = Array.Empty<Material>();
            if (clearSelection)
                _selectedMeshEffectPrefab = null;
        }

        private void RestoreOriginalWeaponMaterials()
        {
            Renderer? target = _meshEffectTargetRenderer;
            if (target == null)
                return;

            Material[] current = target.sharedMaterials;
            var originals = new HashSet<Material>(_originalWeaponMaterials);
            target.sharedMaterials = _originalWeaponMaterials;

            for (int i = 0; i < current.Length; i++)
            {
                Material material = current[i];
                if (material != null
                    && !originals.Contains(material)
                    && material.name.EndsWith("(Runtime)", StringComparison.Ordinal))
                {
                    DestroyUnityObject(material);
                }
            }
        }

        private static void DestroyUnityObject(UnityEngine.Object value)
        {
            if (Application.isPlaying)
                Destroy(value);
            else
                DestroyImmediate(value);
        }

        private void SpawnTarget(string kind)
        {
            var conn = NetworkManager.Instance?.Conn;
            if (conn == null)
            {
                SetStatus("NO CONNECTION", true);
                return;
            }

            try
            {
                conn.Reducers.SpawnPlaygroundTarget(kind);
                SetStatus($"{LabelForKind(kind)} SENT", false);
            }
            catch (Exception error)
            {
                SetStatus("SPAWN FAILED", true);
                Debug.LogWarning($"[{nameof(PlaygroundTargetsPanel)}] Spawn request failed locally: {error.Message}");
            }
        }

        private void SpawnNpc(string templateId, string faction, string label)
        {
            var conn = NetworkManager.Instance?.Conn;
            if (conn == null)
            {
                SetStatus("NO CONNECTION", true);
                return;
            }

            try
            {
                conn.Reducers.SpawnNpc(templateId, templateId, faction);
                SetStatus($"{label} SENT", false);
            }
            catch (Exception error)
            {
                SetStatus("SPAWN FAILED", true);
                Debug.LogWarning($"[{nameof(PlaygroundTargetsPanel)}] NPC spawn request failed locally: {error.Message}");
            }
        }

        private void ClearTargets()
        {
            var conn = NetworkManager.Instance?.Conn;
            if (conn == null)
            {
                SetStatus("NO CONNECTION", true);
                return;
            }

            try
            {
                conn.Reducers.DespawnAllPlaygroundTargets();
                conn.Reducers.DespawnAllNpcs();
                SetStatus("CLEAR SENT", false);
            }
            catch (Exception error)
            {
                SetStatus("CLEAR FAILED", true);
                Debug.LogWarning($"[{nameof(PlaygroundTargetsPanel)}] Clear request failed locally: {error.Message}");
            }
        }

        private void TrySubscribeToReducerErrors()
        {
            DbConnection? conn = NetworkManager.Instance?.Conn;
            if (conn == null || ReferenceEquals(_subscribedConnection, conn))
                return;

            UnsubscribeFromReducerErrors();
            _subscribedConnection = conn;
            _subscribedConnection.OnUnhandledReducerError += OnUnhandledReducerError;
        }

        private void UnsubscribeFromReducerErrors()
        {
            if (_subscribedConnection == null)
                return;

            _subscribedConnection.OnUnhandledReducerError -= OnUnhandledReducerError;
            _subscribedConnection = null;
        }

        private void OnUnhandledReducerError(ReducerEventContext ctx, Exception error)
        {
            if (ctx.Event.Reducer is not Reducer.SpawnPlaygroundTarget
                and not Reducer.DespawnAllPlaygroundTargets
                and not Reducer.DespawnPlaygroundTarget
                and not Reducer.SpawnNpc
                and not Reducer.DespawnAllNpcs
                and not Reducer.DespawnNpc)
            {
                return;
            }

            SetStatus(error.Message, true);
            Debug.LogWarning($"[{nameof(PlaygroundTargetsPanel)}] Playground reducer rejected: {error.Message}");
        }

        private void SetStatus(string message, bool error)
        {
            if (_statusText == null)
                return;

            _statusText.color = error
                ? HeatUiStyle.Error
                : HeatUiStyle.Success;
            _statusText.text = message;
            _statusUntilTime = Time.unscaledTime + 4f;
        }

        private static string LabelForKind(string kind)
        {
            return kind switch
            {
                KindHostile => "PLAYER HOSTILE",
                KindNeutral => "PLAYER NEUTRAL",
                KindPartyMember => "PLAYER FRIENDLY",
                _ => "SPAWN",
            };
        }

        private static GameObject Panel(
            string name,
            Transform parent,
            Vector2 anchor,
            Vector2 pivot,
            Vector2 size,
            Vector2 offset)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = anchor;
            rt.anchorMax = anchor;
            rt.pivot = pivot;
            rt.sizeDelta = size;
            rt.anchoredPosition = offset;
            return go;
        }

        private static void Img(GameObject go, Color color)
        {
            var img = go.AddComponent<Image>();
            img.color = color;
            img.raycastTarget = false;
        }

        private static Text Label(
            Transform parent,
            string name,
            Vector2 anchor,
            Vector2 pivot,
            Vector2 size,
            Vector2 offset,
            int fontSize,
            Color color,
            TextAnchor alignment)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = anchor;
            rt.anchorMax = anchor;
            rt.pivot = pivot;
            rt.sizeDelta = size;
            rt.anchoredPosition = offset;

            var txt = go.AddComponent<Text>();
            txt.font = Font();
            txt.fontSize = fontSize;
            txt.alignment = alignment;
            txt.color = color;
            txt.raycastTarget = false;

            var shadow = go.AddComponent<Shadow>();
            shadow.effectColor = new Color(0f, 0f, 0f, 0.8f);
            shadow.effectDistance = new Vector2(1f, -1f);
            return txt;
        }

        private static Button MakeHudButton(
            Transform parent,
            string name,
            string text,
            Vector2 anchor,
            Vector2 pivot,
            Vector2 size,
            Vector2 offset,
            Color fill)
        {
            var go = Panel(name, parent, anchor, pivot, size, offset);
            var image = go.AddComponent<Image>();
            image.color = fill;
            image.raycastTarget = true;

            var button = go.AddComponent<Button>();
            var colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.15f, 1.15f, 1.15f, 1f);
            colors.pressedColor = new Color(0.78f, 0.78f, 0.78f, 1f);
            colors.disabledColor = new Color(0.35f, 0.35f, 0.35f, 0.55f);
            button.colors = colors;
            HeatUiStyle.StyleButton(button, text, fill, Color.white);

            var label = Label(go.transform, "Label",
                Vector2.zero, Vector2.zero, Vector2.zero, Vector2.zero,
                10, Color.white, TextAnchor.MiddleCenter);
            var labelRect = label.GetComponent<RectTransform>();
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.sizeDelta = Vector2.zero;
            labelRect.anchoredPosition = Vector2.zero;
            label.fontStyle = FontStyle.Bold;
            label.text = text;
            label.resizeTextForBestFit = true;
            label.resizeTextMinSize = 7;
            label.resizeTextMaxSize = 10;

            return button;
        }

        private static Font Font() =>
            HeatUiStyle.ResolveLegacyFont();
    }
}
