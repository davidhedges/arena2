#nullable enable

using Arena.Combat;
using Arena.Input;
using Arena.Network;
using Arena.Presentation;
using Arena.Presentation.Appearance;
using Arena.World;
using Michsky.UI.Heat;
using System.Collections.Generic;
using System.Linq;
using SpacetimeDB;
using SpacetimeDB.Types;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Arena.UI
{
    [ExecuteAlways]
    public sealed class HubController : MonoBehaviour, IEscapeCloseable
    {
        private const string HubSceneName = "Hub";
        private const string SurvivalButtonName = "Mode_Survival";
        private const string SurvivalDisplayName = "Survival Mode";
        private const float ShowcaseCameraFacingYaw = 180f;
        private const float ShowcaseDegreesPerPixel = 0.4f;

        // All hub styling flows through the shared theme so the baked scene and
        // the procedural windows read as one system.
        private static Color Accent => ArenaUiTheme.Accent;
        private static Color OnAccent => ArenaUiTheme.OnAccent;
        private static Color PanelColor => ArenaUiTheme.Panel;
        private static Color PanelStrongColor => ArenaUiTheme.PanelStrong;
        private static Color ButtonColor => ArenaUiTheme.RowAlt;
        private static Color ButtonHoverColor => ArenaUiTheme.CellFilled;
        private static Color MutedTextColor => ArenaUiTheme.MutedText;
        private static readonly Color Transparent = new(0f, 0f, 0f, 0f);

        private Transform? _root;
        private GameObject? _hubCanvas;
        private GameObject? _homeRoot;
        private GameObject? _stageRoot;
        private GameObject? _travelMenu;
        private GameObject? _showcaseAvatar;
        private RuntimeAvatarController? _showcaseAvatarController;
        private RuntimeAvatarBinding? _showcaseAvatarBinding;
        private WeaponAttachmentController? _showcaseWeaponAttachments;
        private Button? _playButton;
        private Button? _equipmentButton;
        private Button? _ctaButton;
        private TMP_Text? _ctaLabel;
        private TMP_Text? _ctaSubtitle;
        private TMP_Text? _destinationValue;
        private DbConnection? _travelConnection;
        private string? _pendingTravelScene;
        private bool _pendingSurvivalStart;
        private bool _wired;
        private string _lastShowcaseCombatSignature = string.Empty;
        private string _lastShowcaseAppearanceSignature = string.Empty;
        private string _lastFailedShowcaseAppearanceSignature = string.Empty;
        private Dictionary<string, string>? _showcaseArmorPreviewBySlot;
        private bool _hasShowcaseWeaponPreview;
        private string _showcaseMainHandPreviewId = string.Empty;
        private string _showcaseMainHandPreviewColorId = string.Empty;
        private string _showcaseOffHandPreviewId = string.Empty;
        private string _showcaseOffHandPreviewColorId = string.Empty;

        private readonly struct ShowcaseWeaponSelection
        {
            public ShowcaseWeaponSelection(
                string? mainHandItemDefId,
                string? mainHandColorId,
                string? offHandItemDefId,
                string? offHandColorId)
            {
                MainHandItemDefId = CharacterAppearanceIds.Normalize(mainHandItemDefId);
                MainHandColorId = CharacterAppearanceIds.Normalize(mainHandColorId);
                OffHandItemDefId = CharacterAppearanceIds.Normalize(offHandItemDefId);
                OffHandColorId = CharacterAppearanceIds.Normalize(offHandColorId);
            }

            public string MainHandItemDefId { get; }
            public string MainHandColorId { get; }
            public string OffHandItemDefId { get; }
            public string OffHandColorId { get; }
        }

        private readonly struct HubLayoutMetrics
        {
            public HubLayoutMetrics(float width, float height)
            {
                Width = width;
                Height = height;
                Margin = Mathf.Clamp(width * 0.028f, 34f, 64f);
                Gap = Mathf.Clamp(width * 0.010f, 14f, 22f);
                TopBarHeight = Mathf.Clamp(height * 0.082f, 82f, 104f);
                RightPanelWidth = Mathf.Clamp(width * 0.30f, 500f, 680f);
                PanelInset = Mathf.Clamp(width * 0.017f, 28f, 38f);
                CtaHeight = Mathf.Clamp(height * 0.20f, 196f, 252f);
                CtaButtonHeight = Mathf.Clamp(height * 0.074f, 74f, 90f);
                DestinationButtonHeight = Mathf.Clamp(height * 0.043f, 42f, 50f);
                DestinationButtonGap = Mathf.Clamp(height * 0.009f, 8f, 12f);
            }

            public float Width { get; }
            public float Height { get; }
            public float Margin { get; }
            public float Gap { get; }
            public float TopBarHeight { get; }
            public float RightPanelWidth { get; }
            public float PanelInset { get; }
            public float CtaHeight { get; }
            public float CtaButtonHeight { get; }
            public float DestinationButtonHeight { get; }
            public float DestinationButtonGap { get; }
        }

        public int EscapeClosePriority => IsTravelMenuOpen ? 80 : 30;
        public bool IsEscapeCloseable => IsTravelMenuOpen;

        private bool IsTravelMenuOpen => Application.isPlaying
            && _travelMenu != null
            && _travelMenu.activeSelf;

        private void OnEnable()
        {
            if (Application.isPlaying)
            {
                RuntimeUiEscapeRouter.Register(this);
                if (GetComponent<HubScreen>() == null)
                    gameObject.AddComponent<HubScreen>();
            }
            RemoveGeneratedCombinedCharacterPreview();
            Resolve();
            WireButtons();
            EnsureTravelConnection();
            ApplyState();
        }

        private void OnDisable()
        {
            if (Application.isPlaying)
                RuntimeUiEscapeRouter.Unregister(this);
            SubscribeToTravelConnection(null);
        }

        private void Update()
        {
            RemoveGeneratedCombinedCharacterPreview();
            Resolve();
            if (!_wired)
                WireButtons();
            EnsureTravelConnection();
            ApplyState();
        }

        private static void RemoveGeneratedCombinedCharacterPreview()
        {
            Scene activeScene = SceneManager.GetActiveScene();
            if (!activeScene.IsValid() || !activeScene.isLoaded)
                return;

            if (!string.Equals(activeScene.name, HubSceneName, System.StringComparison.Ordinal))
                return;

            foreach (GameObject rootObject in activeScene.GetRootGameObjects())
            {
                if (string.Equals(rootObject.name, "Combined Character", System.StringComparison.Ordinal))
                    DestroySceneObject(rootObject);

                if (!string.Equals(rootObject.name, "HubSceneRoot", System.StringComparison.Ordinal))
                    continue;

                Transform? staleShowcaseModel =
                    rootObject.transform.Find("StageRoot/ShowcaseAnchor/HubShowcaseAvatar/RuntimeAvatarModel");
                if (staleShowcaseModel != null)
                    DestroySceneObject(staleShowcaseModel.gameObject);
            }
        }

        private static void DestroySceneObject(GameObject gameObject)
        {
            if (Application.isPlaying)
                Destroy(gameObject);
            else
                DestroyImmediate(gameObject);
        }

        private void Resolve()
        {
            _root = transform;
            _hubCanvas = _root.Find("HubCanvas")?.gameObject;
            _homeRoot = _root.Find("HubCanvas/HomeRoot")?.gameObject;
            _stageRoot = _root.Find("StageRoot")?.gameObject;
            _travelMenu = _root.Find("HubCanvas/HomeRoot/TravelMenu")?.gameObject;
            _showcaseAvatar = _root.Find("StageRoot/ShowcaseAnchor/HubShowcaseAvatar")?.gameObject;
            FaceShowcaseTowardCamera(_showcaseAvatar?.transform);
            FaceShowcaseTowardCamera(_showcaseAvatarController?.VisualRoot);
            if (Application.isPlaying)
                StarterAssetsRuntimeStripper.StripFrom(_showcaseAvatar);
            _playButton = _root.Find("HubCanvas/TopBar/NavRow/PlayButton")?.GetComponent<Button>();
            _equipmentButton = _root.Find("HubCanvas/TopBar/NavRow/EquipmentButton")?.GetComponent<Button>();
            _ctaButton = _root.Find("HubCanvas/HomeRoot/CtaPanel/PlayButton")?.GetComponent<Button>();
            _ctaLabel = _root.Find("HubCanvas/HomeRoot/CtaPanel/PlayButton/Label")?.GetComponent<TMP_Text>();
            _ctaSubtitle = _root.Find("HubCanvas/HomeRoot/CtaPanel/PlayButton/SubLabel")?.GetComponent<TMP_Text>();
            SuppressDeprecatedHomeSections();
            RestoreRankAndQuestSections();
            EnsureHeatHubLayout();
            EnsureTravelDestinationButtons();
        }

        private void WireButtons()
        {
            if (_playButton != null)
            {
                _playButton.onClick.RemoveAllListeners();
                _playButton.onClick.AddListener(() =>
                {
                    HubViewState.Show(HubViewScreen.Play);
                    SetTravelMenuOpen(false);
                    ApplyState();
                });
            }

            if (_equipmentButton != null)
            {
                _equipmentButton.onClick.RemoveAllListeners();
                _equipmentButton.interactable = false;
                _equipmentButton.gameObject.SetActive(false);
            }

            if (_ctaButton != null)
            {
                _ctaButton.onClick.RemoveAllListeners();
                _ctaButton.onClick.AddListener(() =>
                {
                    if (_travelMenu != null && Application.isPlaying)
                        SetTravelMenuOpen(!_travelMenu.activeSelf);
                });
            }

            Transform? destinations = _root?.Find("HubCanvas/HomeRoot/TravelMenu/DestinationButtons");
            if (destinations != null)
            {
                EnsureTravelDestinationButtons();
                foreach (Transform child in destinations)
                {
                    Button? destinationButton = child.GetComponent<Button>();
                    if (destinationButton == null)
                        continue;

                    if (string.Equals(child.name, SurvivalButtonName, System.StringComparison.Ordinal))
                    {
                        destinationButton.onClick.RemoveAllListeners();
                        destinationButton.onClick.AddListener(RequestSurvival);
                        continue;
                    }

                    string sceneName = child.name.StartsWith("Travel_", System.StringComparison.Ordinal)
                        ? child.name.Substring("Travel_".Length)
                        : string.Empty;

                    destinationButton.onClick.RemoveAllListeners();
                    destinationButton.onClick.AddListener(() =>
                    {
                        if (string.IsNullOrWhiteSpace(sceneName))
                            return;

                        RequestTravel(sceneName);
                    });
                }
            }

            _wired = _playButton != null && _ctaButton != null;
        }

        /// <summary>
        /// Opens the retained destination menu from the current PvP hub's
        /// Practice action. The rest of the retired hub canvas stays hidden.
        /// </summary>
        public void OpenPracticeMenu()
        {
            if (!Application.isPlaying)
                return;

            Resolve();
            SetTravelMenuOpen(true, bringToFront: true);
        }

        private void EnsureTravelConnection()
        {
            SubscribeToTravelConnection(NetworkManager.Instance?.Conn);
        }

        private void SubscribeToTravelConnection(DbConnection? conn)
        {
            if (ReferenceEquals(conn, _travelConnection))
                return;

            if (_travelConnection != null)
            {
                _travelConnection.Reducers.OnSetOpenWorldScene -= OnSetOpenWorldScene;
                _travelConnection.Reducers.OnStartSurvivalRun -= OnStartSurvivalRun;
            }

            _travelConnection = conn;
            if (_pendingTravelScene != null || _pendingSurvivalStart)
            {
                _pendingTravelScene = null;
                _pendingSurvivalStart = false;
                SetTravelButtonsInteractable(true);
            }

            if (conn != null)
            {
                conn.Reducers.OnSetOpenWorldScene += OnSetOpenWorldScene;
                conn.Reducers.OnStartSurvivalRun += OnStartSurvivalRun;
            }
        }

        private void RequestTravel(string sceneName)
        {
            if (_pendingTravelScene != null || _pendingSurvivalStart)
                return;
            if (!OpenWorldTravelCatalog.IsRegisteredOpenWorldScene(sceneName))
            {
                Debug.LogError($"[{nameof(HubController)}] Refusing unknown open-world destination '{sceneName}'.");
                return;
            }

            EnsureTravelConnection();
            if (_travelConnection == null)
            {
                Debug.LogWarning($"[{nameof(HubController)}] Cannot travel while disconnected.");
                return;
            }

            _pendingTravelScene = sceneName;
            SetTravelButtonsInteractable(false);
            _travelConnection.Reducers.SetOpenWorldScene(sceneName);
        }

        private void RequestSurvival()
        {
            if (_pendingTravelScene != null || _pendingSurvivalStart)
                return;

            EnsureTravelConnection();
            if (_travelConnection == null)
            {
                Debug.LogWarning($"[{nameof(HubController)}] Cannot start survival while disconnected.");
                return;
            }

            _pendingSurvivalStart = true;
            SetTravelButtonsInteractable(false);
            _travelConnection.Reducers.StartSurvivalRun();
        }

        private void OnSetOpenWorldScene(ReducerEventContext ctx, string sceneName)
        {
            if (_travelConnection == null
                || !_travelConnection.Identity.HasValue
                || ctx.Event.CallerIdentity != _travelConnection.Identity.Value
                || !string.Equals(_pendingTravelScene, sceneName, System.StringComparison.Ordinal))
            {
                return;
            }

            if (ctx.Event.Status is Status.Committed)
            {
                _pendingTravelScene = null;
                OpenWorldTravelCatalog.SetCurrentScene(sceneName);
                SceneManager.LoadScene(sceneName);
                return;
            }

            string reason = ctx.Event.Status switch
            {
                Status.Failed(var failure) => failure,
                Status.OutOfEnergy(var _) => "server was out of reducer energy",
                _ => "server did not commit the travel request",
            };
            Debug.LogError($"[{nameof(HubController)}] Travel to '{sceneName}' failed: {reason}");
            _pendingTravelScene = null;
            SetTravelButtonsInteractable(true);
        }

        private void OnStartSurvivalRun(ReducerEventContext ctx)
        {
            if (_travelConnection == null
                || !_travelConnection.Identity.HasValue
                || ctx.Event.CallerIdentity != _travelConnection.Identity.Value
                || !_pendingSurvivalStart)
            {
                return;
            }

            if (ctx.Event.Status is Status.Committed)
            {
                SetTravelMenuOpen(false);
                return;
            }

            string reason = ctx.Event.Status switch
            {
                Status.Failed(var failure) => failure,
                Status.OutOfEnergy(var _) => "server was out of reducer energy",
                _ => "server did not commit the survival request",
            };
            Debug.LogError($"[{nameof(HubController)}] Starting survival failed: {reason}");
            _pendingSurvivalStart = false;
            SetTravelButtonsInteractable(true);
        }

        private void SetTravelButtonsInteractable(bool interactable)
        {
            Transform? destinations = _root?.Find("HubCanvas/HomeRoot/TravelMenu/DestinationButtons");
            if (destinations == null)
                return;

            foreach (Transform child in destinations)
            {
                Button? destinationButton = child.GetComponent<Button>();
                if (destinationButton != null)
                    destinationButton.interactable = interactable;
            }
        }

        private void ApplyState()
        {
            bool activeHub = string.Equals(SceneManager.GetActiveScene().name, HubSceneName, System.StringComparison.Ordinal);
            bool showHome = activeHub && HubViewState.Current == HubViewScreen.Play;
            bool showStage = activeHub && HubViewState.Current == HubViewScreen.Play;

            if (_homeRoot != null)
                _homeRoot.SetActive(showHome);
            if (_stageRoot != null)
                _stageRoot.SetActive(showStage);
            if (_travelMenu != null && (!showHome || !Application.isPlaying))
                SetTravelMenuOpen(false);

            if (Application.isPlaying && GetComponent<HubScreen>() != null)
                SyncToolkitTravelOverlay(IsTravelMenuOpen, bringToFront: false);

            ApplyNavVisual(_playButton, HubViewState.Current == HubViewScreen.Play);

            if (_ctaSubtitle != null)
                _ctaSubtitle.text = OpenWorldTravelCatalog.CurrentDisplayName.ToUpperInvariant();
            if (_destinationValue != null)
                _destinationValue.text = OpenWorldTravelCatalog.CurrentDisplayName;
            if (_ctaLabel != null)
                _ctaLabel.text = "ENTER WORLD";

            if (showStage)
            {
                string combatProfile = ResolveCombatProfile();
                ApplyShowcaseAppearance();
                ApplyCombatProfile(combatProfile);
            }
        }

        public bool TryCloseForEscape()
        {
            Resolve();
            if (IsTravelMenuOpen)
            {
                SetTravelMenuOpen(false);
                return true;
            }

            return false;
        }

        private void SetTravelMenuOpen(bool open, bool bringToFront = false)
        {
            if (_travelMenu == null)
                return;

            _travelMenu.SetActive(open);
            if (GetComponent<HubScreen>() != null)
                SyncToolkitTravelOverlay(open, bringToFront);
        }

        private void SyncToolkitTravelOverlay(bool open, bool bringToFront)
        {
            if (_hubCanvas == null || _homeRoot == null || _travelMenu == null)
                return;

            if (!open)
            {
                _hubCanvas.SetActive(false);
                return;
            }

            // HubScreen replaces the old presentation, but the authored travel
            // menu and its server-authoritative callbacks remain the canonical
            // way to enter the legacy scenes. Expose only that retained menu.
            _hubCanvas.SetActive(true);
            foreach (Transform child in _hubCanvas.transform)
                child.gameObject.SetActive(child.gameObject == _homeRoot);

            _homeRoot.SetActive(true);
            foreach (Transform child in _homeRoot.transform)
                child.gameObject.SetActive(child.gameObject == _travelMenu);

            _travelMenu.SetActive(true);
            if (bringToFront)
                RuntimeUiLayer.BringToFront(_hubCanvas.GetComponent<Canvas>());
        }

        private string ResolveCombatProfile()
        {
            if (Application.isPlaying)
            {
                HubNetworkManager? hub = HubNetworkManager.Instance;
                HubLoadoutSnapshot? loadout = hub?.Loadout;
                if (hub != null && loadout.HasValue)
                {
                    string primaryId = WireIdentifier.Normalize(loadout.Value.PrimaryDisciplineId);
                    HubDisciplineSnapshot? discipline = hub.Disciplines.FirstOrDefault(candidate =>
                        string.Equals(
                            WireIdentifier.Normalize(candidate.Id),
                            primaryId,
                            System.StringComparison.Ordinal));
                    if (!string.IsNullOrWhiteSpace(discipline?.CombatProfileId))
                        return discipline.CombatProfileId;
                }

                DbConnection? conn = NetworkManager.Instance?.Conn;
                Identity? identity = conn?.Identity;
                if (conn != null && identity.HasValue)
                    return CombatProfileResolver.ResolveForOwner(conn, identity.Value);
            }

            return CombatProfileIds.SwordAndShield;
        }

        private void ApplyCombatProfile(string combatProfile)
        {
            combatProfile = CombatProfileIds.Normalize(combatProfile);
            ShowcaseWeaponSelection selection = ResolveShowcaseWeaponSelection();
            string signature = BuildShowcaseCombatSignature(
                combatProfile,
                selection.MainHandItemDefId,
                selection.MainHandColorId,
                selection.OffHandItemDefId,
                selection.OffHandColorId);
            if (string.Equals(_lastShowcaseCombatSignature, signature, System.StringComparison.Ordinal))
                return;

            if (!Application.isPlaying || _showcaseAvatar == null || _showcaseAvatarBinding == null)
                return;

            CombatAnimationSet? animationSet = CombatAnimationSetCatalog.Resolve(combatProfile);
            if (animationSet == null)
                return;

            Transform host = GetShowcaseHost();
            WeaponAttachmentController? attachments = _showcaseWeaponAttachments;
            if (attachments == null)
            {
                attachments = host.GetComponent<WeaponAttachmentController>();
                if (attachments == null)
                    attachments = host.gameObject.AddComponent<WeaponAttachmentController>();
                _showcaseWeaponAttachments = attachments;
            }

            attachments.BindMounts(_showcaseAvatarBinding.Mounts);
            IReadOnlyDictionary<string, EquippedWeaponVisual>? weaponVisuals =
                ResolveShowcaseWeaponVisuals(selection);
            attachments.ApplyAnimationSet(animationSet, weaponVisuals);
            attachments.SetInCombat(true);
            ApplyShowcaseLoop(_showcaseAvatarBinding.AvatarRoot, animationSet);
            _lastShowcaseCombatSignature = signature;
        }

        private ShowcaseWeaponSelection ResolveShowcaseWeaponSelection()
        {
            HubLoadoutSnapshot? loadout = HubNetworkManager.Instance?.Loadout;
            return _hasShowcaseWeaponPreview
                ? new ShowcaseWeaponSelection(
                    _showcaseMainHandPreviewId,
                    _showcaseMainHandPreviewColorId,
                    _showcaseOffHandPreviewId,
                    _showcaseOffHandPreviewColorId)
                : new ShowcaseWeaponSelection(
                    loadout?.MainHandItemDefId,
                    loadout?.MainHandColorId,
                    loadout?.OffHandItemDefId,
                    loadout?.OffHandColorId);
        }

        private IReadOnlyDictionary<string, EquippedWeaponVisual>? ResolveShowcaseWeaponVisuals(
            ShowcaseWeaponSelection selection)
        {
            HubNetworkManager? hub = HubNetworkManager.Instance;
            if (hub == null
                || (string.IsNullOrWhiteSpace(selection.MainHandItemDefId)
                    && string.IsNullOrWhiteSpace(selection.OffHandItemDefId)))
            {
                return null;
            }

            if (!CharacterAppearanceCatalogSet.TryLoadDefault(out CharacterAppearanceCatalogSet catalogs, out _)
                || catalogs.EquipmentAppearanceCatalog == null)
            {
                return null;
            }

            var visuals = new Dictionary<string, EquippedWeaponVisual>(System.StringComparer.Ordinal);
            AddShowcaseWeaponVisuals(
                hub,
                catalogs.EquipmentAppearanceCatalog,
                selection.MainHandItemDefId,
                selection.MainHandColorId,
                visuals);
            AddShowcaseWeaponVisuals(
                hub,
                catalogs.EquipmentAppearanceCatalog,
                selection.OffHandItemDefId,
                selection.OffHandColorId,
                visuals);
            return visuals.Count == 0 ? null : visuals;
        }

        private static void AddShowcaseWeaponVisuals(
            HubNetworkManager hub,
            EquipmentAppearanceCatalog catalog,
            string itemDefId,
            string colorId,
            IDictionary<string, EquippedWeaponVisual> visuals)
        {
            string normalizedItemDefId = CharacterAppearanceIds.Normalize(itemDefId);
            HubWeaponSnapshot? weapon = hub.Weapons.FirstOrDefault(candidate =>
                string.Equals(
                    CharacterAppearanceIds.Normalize(candidate.ItemDefId),
                    normalizedItemDefId,
                    System.StringComparison.Ordinal));
            if (weapon == null)
                return;

            foreach (string roleId in EquipmentAppearanceCatalog.WeaponVisualRoleIdsForKind(weapon.WeaponKind))
            {
                if (!catalog.TryGetWeaponVisual(
                        weapon.ItemDefId,
                        colorId,
                        roleId,
                        CharacterAppearanceIds.RaceHuman,
                        CharacterAppearanceIds.SexMale,
                        out EquipmentAppearanceCatalog.WeaponVisualEntry entry)
                    || entry.prefab == null)
                {
                    continue;
                }

                visuals[roleId] = new EquippedWeaponVisual(
                    roleId,
                    weapon.ItemDefId,
                    entry.prefab,
                    entry.placementProfile);
            }
        }

        private void ApplyShowcaseAppearance()
        {
            if (!Application.isPlaying || _showcaseAvatar == null)
                return;

            CharacterAppearance? appearance = ResolveLocalAppearance();
            string baseSignature = appearance != null
                ? RuntimeAvatarController.SignatureFor(appearance)
                : "STARTER_DEFAULT|HUMAN|MALE|GEAR";
            IReadOnlyDictionary<string, string> armorBySlot =
                _showcaseArmorPreviewBySlot ?? ResolveLocalArmorAppearance();
            string signature = BuildShowcaseAppearanceSignature(baseSignature, armorBySlot);
            if (string.Equals(_lastShowcaseAppearanceSignature, signature, System.StringComparison.Ordinal) &&
                _showcaseAvatarBinding != null)
            {
                return;
            }
            if (string.Equals(_lastFailedShowcaseAppearanceSignature, signature, System.StringComparison.Ordinal) &&
                _showcaseAvatarBinding == null)
            {
                return;
            }

            Transform host = GetShowcaseHost();
            _showcaseAvatar.SetActive(false);

            if (_showcaseAvatarController == null)
            {
                _showcaseAvatarController = host.GetComponent<RuntimeAvatarController>();
                if (_showcaseAvatarController == null)
                    _showcaseAvatarController = host.gameObject.AddComponent<RuntimeAvatarController>();
                _showcaseAvatarController.SetVisualRootParent(host);
            }

            _showcaseWeaponAttachments?.ClearVisuals();
            RuntimeAvatarBinding binding;
            string error;
            bool applied = appearance != null
                ? _showcaseAvatarController.Apply(appearance, out binding, out error)
                : _showcaseAvatarController.ApplyStarterDefault(out binding, out error);
            if (applied)
                applied = _showcaseAvatarController.SetEquipmentAppearanceOverride(
                    armorBySlot,
                    out binding,
                    out error);
            if (applied)
            {
                FaceShowcaseTowardCamera(_showcaseAvatarController.VisualRoot);
                _showcaseAvatarBinding = binding;
                _lastShowcaseAppearanceSignature = signature;
                _lastFailedShowcaseAppearanceSignature = string.Empty;
                _lastShowcaseCombatSignature = string.Empty;
                StarterAssetsRuntimeStripper.StripFrom(binding.AvatarRoot);
                return;
            }

            Debug.LogWarning($"[{nameof(HubController)}] Failed to assemble showcase avatar: {error}");
            _lastFailedShowcaseAppearanceSignature = signature;
            if (_showcaseAvatarBinding == null)
            {
                _lastShowcaseAppearanceSignature = string.Empty;
                _showcaseAvatar.SetActive(true);
            }
        }

        private static void FaceShowcaseTowardCamera(Transform? target)
        {
            if (target != null)
                target.localRotation = Quaternion.Euler(0f, ShowcaseCameraFacingYaw, 0f);
        }

        internal void SetShowcaseArmorPreview(IReadOnlyDictionary<string, string>? armorBySlot)
        {
            if (armorBySlot == null)
            {
                _showcaseArmorPreviewBySlot = null;
            }
            else
            {
                _showcaseArmorPreviewBySlot = armorBySlot
                    .Where(pair => !string.IsNullOrWhiteSpace(pair.Key)
                        && !string.IsNullOrWhiteSpace(pair.Value))
                    .ToDictionary(
                        pair => CharacterAppearanceIds.Normalize(pair.Key),
                        pair => CharacterAppearanceIds.Normalize(pair.Value),
                        System.StringComparer.Ordinal);
            }

            _lastShowcaseAppearanceSignature = string.Empty;
            _lastFailedShowcaseAppearanceSignature = string.Empty;
            ApplyShowcaseAppearance();
        }

        internal void SetShowcaseWeaponPreview(
            string? mainHandItemDefId,
            string? mainHandColorId,
            string? offHandItemDefId,
            string? offHandColorId)
        {
            _hasShowcaseWeaponPreview = mainHandItemDefId != null || offHandItemDefId != null;
            _showcaseMainHandPreviewId = mainHandItemDefId?.Trim() ?? string.Empty;
            _showcaseMainHandPreviewColorId = mainHandColorId?.Trim() ?? string.Empty;
            _showcaseOffHandPreviewId = offHandItemDefId?.Trim() ?? string.Empty;
            _showcaseOffHandPreviewColorId = offHandColorId?.Trim() ?? string.Empty;
            _lastShowcaseCombatSignature = string.Empty;
            ApplyState();
        }

        internal void RefreshShowcaseLoadout()
        {
            _lastShowcaseCombatSignature = string.Empty;
            ApplyState();
        }

        private static string BuildShowcaseCombatSignature(
            string? combatProfile,
            string? mainHandItemDefId,
            string? mainHandColorId,
            string? offHandItemDefId,
            string? offHandColorId)
        {
            return string.Join(
                "|",
                CombatProfileIds.Normalize(combatProfile),
                CharacterAppearanceIds.Normalize(mainHandItemDefId),
                CharacterAppearanceIds.Normalize(mainHandColorId),
                CharacterAppearanceIds.Normalize(offHandItemDefId),
                CharacterAppearanceIds.Normalize(offHandColorId));
        }

        internal void RotateShowcaseFromPointerDelta(float deltaX)
        {
            Transform? showcaseAnchor = _root?.Find("StageRoot/ShowcaseAnchor");
            if (showcaseAnchor == null)
                return;

            showcaseAnchor.Rotate(
                Vector3.up,
                -deltaX * ShowcaseDegreesPerPixel,
                Space.World);
        }

        private static IReadOnlyDictionary<string, string> ResolveLocalArmorAppearance()
        {
            HubLoadoutSnapshot? hubLoadout = HubNetworkManager.Instance?.Loadout;
            if (hubLoadout.HasValue && !string.IsNullOrWhiteSpace(hubLoadout.Value.ArmorSetId))
                return EquipmentScreen.ArmorAppearanceFor(hubLoadout.Value.ArmorSetId);

            var armorBySlot = new Dictionary<string, string>(System.StringComparer.Ordinal);
            DbConnection? connection = NetworkManager.Instance?.Conn;
            Identity? identity = connection?.Identity;
            if (connection == null || !identity.HasValue)
                return armorBySlot;

            PlayerEquipmentPresentation? presentation =
                connection.Db.PlayerEquipmentPresentation.Owner.Find(identity.Value);
            if (presentation == null)
                return armorBySlot;

            AddArmorPiece(armorBySlot, "HEAD", presentation.HeadItemDefId);
            AddArmorPiece(armorBySlot, "SHOULDER", presentation.ShoulderItemDefId);
            AddArmorPiece(armorBySlot, "CAPE", presentation.CapeItemDefId);
            AddArmorPiece(armorBySlot, "CHEST", presentation.ChestItemDefId);
            AddArmorPiece(armorBySlot, "LEGS", presentation.LegsItemDefId);
            AddArmorPiece(armorBySlot, "BOOTS", presentation.BootsItemDefId);
            AddArmorPiece(armorBySlot, "GLOVES", presentation.GlovesItemDefId);
            return armorBySlot;
        }

        private static void AddArmorPiece(
            IDictionary<string, string> armorBySlot,
            string slotId,
            string? itemDefId)
        {
            if (!string.IsNullOrWhiteSpace(itemDefId))
                armorBySlot[slotId] = CharacterAppearanceIds.Normalize(itemDefId);
        }

        private static string BuildShowcaseAppearanceSignature(
            string baseSignature,
            IReadOnlyDictionary<string, string> armorBySlot)
        {
            List<string> parts = armorBySlot
                .Select(pair =>
                    $"{CharacterAppearanceIds.Normalize(pair.Key)}:{CharacterAppearanceIds.Normalize(pair.Value)}")
                .OrderBy(part => part, System.StringComparer.Ordinal)
                .ToList();
            return $"{baseSignature}|gear={string.Join(",", parts)}";
        }

        private Transform GetShowcaseHost()
        {
            if (_showcaseAvatar != null && _showcaseAvatar.transform.parent != null)
                return _showcaseAvatar.transform.parent;

            return _showcaseAvatar != null ? _showcaseAvatar.transform : transform;
        }

        private static CharacterAppearance? ResolveLocalAppearance()
        {
            DbConnection? conn = NetworkManager.Instance?.Conn;
            Identity? identity = conn?.Identity;
            if (conn == null || !identity.HasValue)
                return null;

            return conn.Db.CharacterAppearance.Owner.Filter(identity.Value).FirstOrDefault();
        }

        private void EnsureTravelDestinationButtons()
        {
            if (_travelMenu == null)
                return;

            RectTransform? menuRect = _travelMenu.GetComponent<RectTransform>();
            Transform? existingList = _travelMenu.transform.Find("DestinationButtons");
            RectTransform list = existingList as RectTransform ?? CreateRect(_travelMenu.transform, "DestinationButtons");

            HubLayoutMetrics metrics = GetLayoutMetrics();
            OpenWorldTravelCatalog.Destination[] destinations = OpenWorldTravelCatalog.All;
            float buttonHeight = metrics.DestinationButtonHeight;
            float buttonGap = metrics.DestinationButtonGap;
            float buttonStep = buttonHeight + buttonGap;
            float listHeight = (destinations.Length + 1) * buttonStep;
            float menuHeight = Mathf.Max(320f, metrics.PanelInset * 2f + 46f + listHeight);
            float listWidth = metrics.RightPanelWidth - metrics.PanelInset * 2f;

            if (menuRect != null)
            {
                menuRect.sizeDelta = new Vector2(metrics.RightPanelWidth, menuHeight);
                menuRect.anchoredPosition = new Vector2(-metrics.Margin, metrics.Margin + metrics.CtaHeight + metrics.Gap);
            }
            RectTransform? menuAccent = _travelMenu.transform.Find("HeatTravelAccent") as RectTransform;
            if (menuAccent != null)
                SetTopLeft(menuAccent, Vector2.zero, new Vector2(5f, menuHeight));

            SetTopLeft(list, new Vector2(metrics.PanelInset, -metrics.PanelInset - 58f), new Vector2(listWidth, listHeight));

            var destinationNames = new HashSet<string>(destinations.Select(destination => $"Travel_{destination.SceneName}"), System.StringComparer.Ordinal)
            {
                SurvivalButtonName,
            };
            for (int i = list.childCount - 1; i >= 0; i--)
            {
                Transform child = list.GetChild(i);
                if (!destinationNames.Contains(child.name))
                {
                    if (Application.isPlaying)
                        Destroy(child.gameObject);
                    else
                        DestroyImmediate(child.gameObject);
                }
            }

            Transform? survivalChild = list.Find(SurvivalButtonName);
            RectTransform survivalRect = survivalChild as RectTransform ?? CreateRect(list, SurvivalButtonName);
            survivalRect.SetSiblingIndex(0);
            SetTopLeft(survivalRect, Vector2.zero, new Vector2(listWidth, buttonHeight));
            ConfigureDestinationButton(
                survivalRect,
                _pendingSurvivalStart ? "Entering Survival..." : SurvivalDisplayName,
                listWidth,
                buttonHeight,
                Color.Lerp(ButtonColor, Accent, 0.18f),
                "MODE");

            for (int i = 0; i < destinations.Length; i++)
            {
                OpenWorldTravelCatalog.Destination destination = destinations[i];
                string buttonName = $"Travel_{destination.SceneName}";
                Transform? child = list.Find(buttonName);
                RectTransform buttonRect = child as RectTransform ?? CreateRect(list, buttonName);
                buttonRect.SetSiblingIndex(i + 1);
                SetTopLeft(buttonRect, new Vector2(0f, -(i + 1) * buttonStep), new Vector2(listWidth, buttonHeight));
                ConfigureDestinationButton(buttonRect, destination.DisplayName, listWidth, buttonHeight, ButtonColor);
            }
        }

        private void ConfigureDestinationButton(
            RectTransform buttonRect,
            string displayName,
            float listWidth,
            float buttonHeight,
            Color background,
            string? tag = null)
        {
            Image image = ArenaUiKit.EnsureComponent<Image>(buttonRect.gameObject);
            image.color = background;

            Button button = ArenaUiKit.EnsureComponent<Button>(buttonRect.gameObject);
            button.interactable = _pendingTravelScene == null && !_pendingSurvivalStart;
            ColorBlock colors = button.colors;
            colors.normalColor = image.color;
            colors.highlightedColor = ButtonHoverColor;
            colors.selectedColor = colors.highlightedColor;
            colors.pressedColor = Color.Lerp(image.color, Color.black, 0.24f);
            colors.disabledColor = new Color(0.05f, 0.055f, 0.065f, 0.52f);
            button.colors = colors;
            AttachHeatButton(button, displayName);

            RectTransform accent = EnsureImage(buttonRect, "Accent", Accent).rectTransform;
            SetAnchored(accent, new Vector2(0f, 0.5f), Vector2.zero, new Vector2(4f, buttonHeight - 14f), new Vector2(0f, 0.5f));
            RectTransform chevron = EnsureText(buttonRect, "Chevron", ">", 20, FontStyles.Bold, TextAlignmentOptions.Center, MutedTextColor).rectTransform;
            SetAnchored(chevron, new Vector2(1f, 0.5f), new Vector2(-22f, 0f), new Vector2(20f, 22f), new Vector2(0.5f, 0.5f));

            float tagWidth = string.IsNullOrWhiteSpace(tag) ? 0f : 54f;
            TMP_Text label = EnsureText(buttonRect, "Label", displayName, 15, FontStyles.Bold, TextAlignmentOptions.Left, ArenaUiTheme.Text);
            SetAnchored(label.rectTransform, new Vector2(0f, 0.5f), new Vector2(22f, 0f), new Vector2(listWidth - 70f - tagWidth, 22f), new Vector2(0f, 0.5f));

            Transform? existingTag = buttonRect.Find("Tag");
            if (!string.IsNullOrWhiteSpace(tag))
            {
                TMP_Text tagLabel = EnsureText(buttonRect, "Tag", tag, 10, FontStyles.Bold, TextAlignmentOptions.Center, Accent);
                SetAnchored(tagLabel.rectTransform, new Vector2(1f, 0.5f), new Vector2(-66f, 0f), new Vector2(48f, 18f), new Vector2(0.5f, 0.5f));
            }
            else if (existingTag != null)
            {
                DestroySceneObject(existingTag.gameObject);
            }
        }

        private static void ApplyShowcaseLoop(GameObject showcaseAvatar, CombatAnimationSet animationSet)
        {
            AnimationClip? loopClip = GetShowcaseLoopClip(animationSet);
            Animator? animator = showcaseAvatar.GetComponentInChildren<Animator>(true);
            if (loopClip == null || animator == null)
                return;

            HubShowcaseAnimationPlayer player =
                ArenaUiKit.EnsureComponent<HubShowcaseAnimationPlayer>(showcaseAvatar);
            player.Configure(animator, loopClip, startTime: GetShowcasePoseSampleTime(loopClip));
        }

        private static AnimationClip? GetShowcaseLoopClip(CombatAnimationSet animationSet)
        {
            return animationSet.locomotionIdleCombat ??
                animationSet.enterCombatIdle ??
                animationSet.locomotionIdle ??
                animationSet.DrawWeaponClip;
        }

        private static float GetShowcasePoseSampleTime(AnimationClip poseClip)
        {
            return poseClip.length > 0.001f
                ? Mathf.Min(poseClip.length * 0.35f, poseClip.length - 0.001f)
                : 0f;
        }

        private static void ApplyNavVisual(Button? button, bool active)
        {
            if (button == null)
                return;

            Image? image = button.GetComponent<Image>();
            TMP_Text? label = button.GetComponentInChildren<TMP_Text>(true);
            if (image != null)
                image.color = active ? Accent : Transparent;
            if (label != null)
                label.color = active ? OnAccent : new Color(1f, 1f, 1f, 0.82f);
        }

        private static RectTransform CreateRect(Transform parent, string name)
        {
            GameObject go = new(name);
            go.transform.SetParent(parent, false);
            return go.AddComponent<RectTransform>();
        }

        private static TMP_Text CreateText(Transform parent, string name, string text, float fontSize, FontStyles style, TextAlignmentOptions alignment, Color color)
        {
            RectTransform rect = CreateRect(parent, name);
            TextMeshProUGUI label = rect.gameObject.AddComponent<TextMeshProUGUI>();
            label.font = ArenaUiTheme.StrongFont ?? TMP_Settings.defaultFontAsset;
            label.text = text;
            label.fontSize = fontSize;
            label.fontStyle = style;
            label.alignment = alignment;
            label.color = color;
            label.raycastTarget = false;
            label.textWrappingMode = TextWrappingModes.NoWrap;
            return label;
        }

        private void EnsureHeatHubLayout()
        {
            if (_root == null)
                return;

            Transform? home = _root.Find("HubCanvas/HomeRoot");
            if (home == null)
                return;

            RectTransform backdrop = EnsureImage(home, "HeatBackdrop", new Color(0.006f, 0.007f, 0.010f, 0.38f)).rectTransform;
            SetStretch(backdrop);
            backdrop.SetAsFirstSibling();
            Image backdropImage = backdrop.GetComponent<Image>();
            backdropImage.raycastTarget = false;

            HubLayoutMetrics metrics = GetLayoutMetrics();
            SuppressTopBar();
            StyleCtaPanel(metrics);
            StyleTravelMenu(metrics);
        }

        private HubLayoutMetrics GetLayoutMetrics()
        {
            RectTransform? home = _root?.Find("HubCanvas/HomeRoot") as RectTransform;
            RectTransform? canvas = _root?.Find("HubCanvas") as RectTransform;
            float width = home != null && home.rect.width > 100f ? home.rect.width : canvas != null ? canvas.rect.width : Screen.width;
            float height = home != null && home.rect.height > 100f ? home.rect.height : canvas != null ? canvas.rect.height : Screen.height;

            if (width < 100f)
                width = 1920f;
            if (height < 100f)
                height = 1080f;

            return new HubLayoutMetrics(width, height);
        }

        private void SuppressTopBar()
        {
            if (_root == null)
                return;

            GameObject? topBar = _root.Find("HubCanvas/TopBar")?.gameObject;
            if (topBar == null)
                return;

            topBar.SetActive(false);
        }

        private void StyleCtaPanel(HubLayoutMetrics metrics)
        {
            if (_root == null)
                return;

            RectTransform? ctaPanel = _root.Find("HubCanvas/HomeRoot/CtaPanel") as RectTransform;
            if (ctaPanel == null)
                return;

            ctaPanel.anchorMin = new Vector2(1f, 0f);
            ctaPanel.anchorMax = new Vector2(1f, 0f);
            ctaPanel.pivot = new Vector2(1f, 0f);
            ctaPanel.anchoredPosition = new Vector2(-metrics.Margin, metrics.Margin);
            ctaPanel.sizeDelta = new Vector2(metrics.RightPanelWidth, metrics.CtaHeight);

            Image panel = ArenaUiKit.EnsureComponent<Image>(ctaPanel.gameObject);
            panel.color = PanelColor;
            panel.raycastTarget = false;

            RectTransform accent = EnsureImage(ctaPanel, "HeatCtaAccent", Accent).rectTransform;
            SetTopLeft(accent, Vector2.zero, new Vector2(5f, metrics.CtaHeight));
            accent.GetComponent<Image>().raycastTarget = false;

            TMP_Text eyebrow = EnsureText(ctaPanel, "HeatCtaEyebrow", "CURRENT DESTINATION", 12, FontStyles.Bold, TextAlignmentOptions.Left, MutedTextColor);
            SetTopLeft(eyebrow.rectTransform, new Vector2(metrics.PanelInset, -metrics.PanelInset), new Vector2(280f, 20f));

            _destinationValue = EnsureText(ctaPanel, "HeatDestinationValue", OpenWorldTravelCatalog.CurrentDisplayName, 30, FontStyles.Bold, TextAlignmentOptions.Left, ArenaUiTheme.Text);
            SetTopLeft(_destinationValue.rectTransform, new Vector2(metrics.PanelInset, -metrics.PanelInset - 30f), new Vector2(metrics.RightPanelWidth - metrics.PanelInset * 2f, 38f));

            if (_ctaButton != null)
            {
                RectTransform buttonRect = _ctaButton.GetComponent<RectTransform>();
                SetBottomLeft(buttonRect, new Vector2(metrics.PanelInset, metrics.PanelInset), new Vector2(metrics.RightPanelWidth - metrics.PanelInset * 2f, metrics.CtaButtonHeight));
                StyleActionButton(_ctaButton, Accent, OnAccent, "ENTER WORLD", metrics);
            }
        }

        private void StyleTravelMenu(HubLayoutMetrics metrics)
        {
            if (_travelMenu == null)
                return;

            RectTransform? menuRect = _travelMenu.GetComponent<RectTransform>();
            if (menuRect == null)
                return;

            menuRect.anchorMin = new Vector2(1f, 0f);
            menuRect.anchorMax = new Vector2(1f, 0f);
            menuRect.pivot = new Vector2(1f, 0f);
            menuRect.anchoredPosition = new Vector2(-metrics.Margin, metrics.Margin + metrics.CtaHeight + metrics.Gap);
            menuRect.sizeDelta = new Vector2(metrics.RightPanelWidth, Mathf.Max(menuRect.sizeDelta.y, 320f));

            Image panel = ArenaUiKit.EnsureComponent<Image>(_travelMenu);
            panel.color = PanelStrongColor;
            panel.raycastTarget = false;

            RectTransform accent = EnsureImage(_travelMenu.transform, "HeatTravelAccent", Accent).rectTransform;
            SetTopLeft(accent, new Vector2(0f, 0f), new Vector2(4f, menuRect.sizeDelta.y));
            accent.GetComponent<Image>().raycastTarget = false;

            TMP_Text title = EnsureText(_travelMenu.transform, "Title", "SELECT DESTINATION", 17, FontStyles.Bold, TextAlignmentOptions.Left, ArenaUiTheme.Text);
            SetTopLeft(title.rectTransform, new Vector2(metrics.PanelInset, -metrics.PanelInset), new Vector2(320f, 24f));

            TMP_Text subtitle = EnsureText(_travelMenu.transform, "HeatTravelSubtitle", "MODES & OPEN WORLD", 11, FontStyles.Bold, TextAlignmentOptions.Left, MutedTextColor);
            SetTopLeft(subtitle.rectTransform, new Vector2(metrics.PanelInset, -metrics.PanelInset - 24f), new Vector2(180f, 18f));
        }

        private static void StyleActionButton(Button button, Color background, Color labelColor, string heatLabel, HubLayoutMetrics metrics)
        {
            Image image = ArenaUiKit.EnsureComponent<Image>(button.gameObject);
            image.color = background;

            ColorBlock colors = button.colors;
            colors.normalColor = background;
            colors.highlightedColor = Color.Lerp(background, Color.white, 0.10f);
            colors.selectedColor = colors.highlightedColor;
            colors.pressedColor = Color.Lerp(background, Color.black, 0.18f);
            colors.disabledColor = new Color(background.r, background.g, background.b, 0.42f);
            button.colors = colors;

            AttachHeatButton(button, heatLabel);

            TMP_Text? label = button.transform.Find("Label")?.GetComponent<TMP_Text>();
            if (label != null)
            {
                label.text = heatLabel;
                label.fontSize = 22;
                label.fontStyle = FontStyles.Bold;
                label.color = labelColor;
                SetAnchored(label.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0f, 12f), new Vector2(metrics.RightPanelWidth - metrics.PanelInset * 2f - 24f, 30f), new Vector2(0.5f, 0.5f));
            }

            TMP_Text? subLabel = button.transform.Find("SubLabel")?.GetComponent<TMP_Text>();
            if (subLabel != null)
            {
                subLabel.fontSize = 12;
                subLabel.fontStyle = FontStyles.Bold;
                subLabel.color = new Color(1f, 1f, 1f, 0.74f);
                SetAnchored(subLabel.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0f, -16f), new Vector2(metrics.RightPanelWidth - metrics.PanelInset * 2f - 24f, 20f), new Vector2(0.5f, 0.5f));
            }
        }

        private static void AttachHeatButton(Button button, string text)
        {
            ButtonManager heatButton = ArenaUiKit.EnsureComponent<ButtonManager>(button.gameObject);
            heatButton.buttonText = text;
            heatButton.useCustomContent = true;
            heatButton.enableText = false;
            heatButton.enableIcon = false;
            heatButton.autoFitContent = false;
            heatButton.useLocalization = false;
            heatButton.useSounds = false;
            heatButton.checkForDoubleClick = false;
            heatButton.bypassUpdateOnEnable = true;
            heatButton.isInteractable = button.interactable;
            heatButton.useUINavigation = true;
            heatButton.navigationMode = Navigation.Mode.Automatic;
            button.transition = Selectable.Transition.ColorTint;
        }

        private static Image EnsureImage(Transform parent, string name, Color color)
        {
            Transform? existing = parent.Find(name);
            RectTransform rect = existing as RectTransform ?? CreateRect(parent, name);
            Image image = ArenaUiKit.EnsureComponent<Image>(rect.gameObject);
            image.color = color;
            return image;
        }

        private static TMP_Text EnsureText(Transform parent, string name, string text, float fontSize, FontStyles style, TextAlignmentOptions alignment, Color color)
        {
            TMP_Text? label = parent.Find(name)?.GetComponent<TMP_Text>();
            if (label == null)
                label = CreateText(parent, name, text, fontSize, style, alignment, color);

            label.text = text;
            label.fontSize = fontSize;
            label.fontStyle = style;
            label.alignment = alignment;
            label.color = color;
            label.raycastTarget = false;
            label.textWrappingMode = TextWrappingModes.NoWrap;
            return label;
        }

        private void SuppressDeprecatedHomeSections()
        {
            SetActive("HubCanvas/HomeRoot/IdentityPanel");
            SetInactive("HubCanvas/HomeRoot/IdentityPanel/GearEyebrow");
            SetInactive("HubCanvas/HomeRoot/IdentityPanel/GearName");
            SetInactive("HubCanvas/HomeRoot/IdentityPanel/GearMeta");
            SetInactive("HubCanvas/HomeRoot/IdentityPanel/IdentityBlurb");
            SetInactive("HubCanvas/HomeRoot/EquipmentPanel");
            SetInactive("HubCanvas/TopBar/NavRow/EquipmentButton");
            SetInactive("HubCanvas/HomeRoot/EquipmentPanel/AllocatedStatsRoot");
            SetInactive("HubCanvas/HomeRoot/EquipmentPanel/ProfessionLabel");
            SetInactive("HubCanvas/HomeRoot/EquipmentPanel/ProfessionCard");
            SetInactive("HubCanvas/HomeRoot/EquipmentPanel/TrinketLabel");
            SetInactive("HubCanvas/HomeRoot/EquipmentPanel/TrinketIcon");
            SetInactive("HubCanvas/HomeRoot/EquipmentPanel/TrinketName");
            SetInactiveByName("StoreButton");
            SetInactiveByName("SeasonButton");
            SetInactiveByName("AllocatedStatsLabel");
            SetInactiveByName("MenuText");
        }

        private void RestoreRankAndQuestSections()
        {
            SetActiveByName("ChallengeLabel");
            SetActiveByName("ChallengeLabel_0");
            SetActiveByName("ChallengeLabel_1");
            SetActiveByName("ChallengeLabel_2");
            SetActiveByName("ChallengeProgress_0");
            SetActiveByName("ChallengeProgress_1");
            SetActiveByName("ChallengeProgress_2");
            SetActiveByName("ChallengeReward_0");
            SetActiveByName("ChallengeReward_1");
            SetActiveByName("ChallengeReward_2");
            SetActiveByName("RankLabel");
            SetActiveByName("RankValue");
            SetActiveByName("RankProgress");
            SetActiveByName("Fill");
            SetActiveByName("ColdFill");
            SetActiveByName("RedRim");
            SetActiveByName("ViewAllButton");
        }

        private void SetInactive(string path)
        {
            GameObject? section = _root?.Find(path)?.gameObject;
            if (section != null)
                section.SetActive(false);
        }

        private void SetActive(string path)
        {
            GameObject? section = _root?.Find(path)?.gameObject;
            if (section != null)
                section.SetActive(true);
        }

        private void SetInactiveByName(string name)
        {
            if (_root == null)
                return;

            foreach (Transform child in _root.GetComponentsInChildren<Transform>(true))
            {
                if (string.Equals(child.name, name, System.StringComparison.Ordinal))
                    child.gameObject.SetActive(false);
            }
        }

        private void SetActiveByName(string name)
        {
            if (_root == null)
                return;

            foreach (Transform child in _root.GetComponentsInChildren<Transform>(true))
            {
                if (string.Equals(child.name, name, System.StringComparison.Ordinal))
                    child.gameObject.SetActive(true);
            }
        }

        private static void SetTopLeft(RectTransform rect, Vector2 position, Vector2 size)
        {
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
        }

        private static void SetBottomLeft(RectTransform rect, Vector2 position, Vector2 size)
        {
            rect.anchorMin = new Vector2(0f, 0f);
            rect.anchorMax = new Vector2(0f, 0f);
            rect.pivot = new Vector2(0f, 0f);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
        }

        private static void SetStretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = Vector2.zero;
        }

        private static void SetAnchored(RectTransform rect, Vector2 anchor, Vector2 position, Vector2 size, Vector2? pivot = null)
        {
            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            rect.pivot = pivot ?? anchor;
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
        }

    }
}
