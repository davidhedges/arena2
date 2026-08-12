#nullable enable

using Arena.Combat;
using Arena.Network;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace Arena.UI
{
    /// <summary>
    /// Approved PvP Hub presentation. The visual source of truth is the browser
    /// prototype at docs/ui-prototypes/hub; Hub.uxml/.uss are its one-way
    /// translation. The camera layer contains only the uploaded background and
    /// the live local avatar assembled by HubController.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-20)]
    public sealed class HubScreen : MonoBehaviour, IEscapeCloseable
    {
        private const string HubSceneName = "Hub";
        private const string BackgroundResourcePath = "Hub/Hub_background";
        private const string BackgroundObjectName = "HubBackgroundPlane";
        private const string OpenClass = "is-open";
        private const string SelectedClass = "is-selected";
        private const string SearchingClass = "is-searching";
        private const float BackgroundDistance = 30f;
        private const float DataRefreshInterval = 0.25f;

        private enum MatchFormat
        {
            TwoVersusTwo,
            ThreeVersusThree,
            TenVersusTen,
        }

        private PanelSettings? _panelSettings;
        private VisualElement? _root;
        private VisualElement? _showcase;
        private Label? _playerName;
        private VisualElement? _loadoutPrimaryRow;
        private VisualElement? _loadoutSecondary1Row;
        private VisualElement? _loadoutSecondary2Row;
        private Label? _loadoutPrimaryName;
        private Label? _loadoutSecondary1Name;
        private Label? _loadoutSecondary2Name;
        private Label? _loadoutPrimaryGlyph;
        private Label? _loadoutSecondary1Glyph;
        private Label? _loadoutSecondary2Glyph;
        private Label? _partyCount;
        private Button? _practiceButton;
        private Button? _navDisciplines;
        private Button? _navEquipment;
        private Button? _queueButton;
        private Label? _queueName;
        private Button? _findMatchButton;
        private Label? _findMatchTitle;
        private Label? _findMatchSubtitle;
        private VisualElement? _matchOverlay;
        private Button? _overlayScrim;
        private Button? _dialogClose;
        private Label? _dialogQueueName;
        private Button? _format2v2;
        private Button? _format3v3;
        private Button? _format10v10;
        private Label? _format2v2Name;
        private Label? _format3v3Name;
        private Label? _format10v10Name;
        private Label? _format3v3Type;
        private Label? _format10v10Type;
        private Label? _selectionValue;
        private Button? _queueConfirm;
        private Label? _queueConfirmTitle;
        private Label? _queueConfirmSubtitle;
        private HubController? _hubController;
        private DisciplinesScreen? _disciplinesScreen;
        private EquipmentScreen? _equipmentScreen;
        private Camera? _hubCamera;
        private GameObject? _backgroundPlane;
        private Material? _backgroundMaterial;
        private Texture2D? _backgroundTexture;
        private bool _draggingShowcase;
        private int _dragPointerId = -1;
        private Vector2 _lastPointerPosition;
        private float _nextDataRefresh;
        private MatchFormat _selectedFormat = MatchFormat.TwoVersusTwo;
        private bool _matchOverlayOpen;
        private MatchHandoffCoordinator? _matchHandoff;

        private bool IsMatchRequestPending => _matchHandoff?.IsMatchRequestPending == true;

        public int EscapeClosePriority => 130;
        public bool IsEscapeCloseable => _matchOverlayOpen;

        private void Awake()
        {
            if (!IsActiveHubScene())
            {
                enabled = false;
                return;
            }

            RuntimeUiEventSystem.Ensure();
            _matchHandoff = MatchHandoffCoordinator.EnsureInstance();
            HideLegacyHubCanvas();
            PrepareCameraLayer();
            BuildUi();
            RefreshBoundData();
        }

        private void OnEnable() => RuntimeUiEscapeRouter.Register(this);

        private void OnDisable() => RuntimeUiEscapeRouter.Unregister(this);

        private void OnDestroy()
        {
            UnbindShowcaseDrag();
            UnbindMatchmakingControls();
            if (_practiceButton != null)
                _practiceButton.clicked -= OpenPracticeMenu;
            if (_navDisciplines != null)
                _navDisciplines.clicked -= OpenDisciplines;
            if (_navEquipment != null)
                _navEquipment.clicked -= OpenEquipment;
            if (_disciplinesScreen != null)
            {
                _disciplinesScreen.Closed -= OnDisciplinesClosed;
                _disciplinesScreen.EquipmentRequested -= OpenEquipment;
                Destroy(_disciplinesScreen.gameObject);
            }
            if (_equipmentScreen != null)
            {
                _equipmentScreen.Closed -= OnEquipmentClosed;
                _equipmentScreen.DisciplinesRequested -= OpenDisciplines;
                Destroy(_equipmentScreen.gameObject);
            }
            if (_panelSettings != null)
                Destroy(_panelSettings);
            if (_backgroundMaterial != null)
                Destroy(_backgroundMaterial);
        }

        private void Update()
        {
            _matchHandoff ??= MatchHandoffCoordinator.Instance;
            if (!IsActiveHubScene() || Time.unscaledTime < _nextDataRefresh)
                return;

            _nextDataRefresh = Time.unscaledTime + DataRefreshInterval;
            RefreshBoundData();
            RefreshMatchmakingPresentation();
        }

        private void LateUpdate()
        {
            if (IsActiveHubScene())
                LayoutBackgroundPlane();
        }

        private bool IsActiveHubScene()
            => string.Equals(
                SceneManager.GetActiveScene().name,
                HubSceneName,
                System.StringComparison.Ordinal);

        private void HideLegacyHubCanvas()
        {
            Transform? legacyCanvas = transform.Find("HubCanvas");
            if (legacyCanvas != null)
                legacyCanvas.gameObject.SetActive(false);
        }

        private void PrepareCameraLayer()
        {
            Transform? stage = transform.Find("StageRoot");
            if (stage == null)
            {
                Debug.LogError("HubScreen: HubSceneRoot is missing StageRoot.");
                return;
            }

            // The old authored room was a procedural stand-in. Keep only the
            // live showcase anchor and its useful portrait lighting.
            foreach (Transform child in stage)
            {
                bool keep =
                    string.Equals(child.name, "ShowcaseAnchor", System.StringComparison.Ordinal) ||
                    string.Equals(child.name, "KeyLight", System.StringComparison.Ordinal) ||
                    string.Equals(child.name, "ColdFill", System.StringComparison.Ordinal) ||
                    string.Equals(child.name, "RedRim", System.StringComparison.Ordinal) ||
                    string.Equals(child.name, BackgroundObjectName, System.StringComparison.Ordinal);
                child.gameObject.SetActive(keep);
            }

            _hubCamera = Camera.main ?? FindAnyObjectByType<Camera>();
            _backgroundTexture = Resources.Load<Texture2D>(BackgroundResourcePath);
            if (_backgroundTexture == null)
            {
                Debug.LogError(
                    $"HubScreen: missing Resources texture '{BackgroundResourcePath}'. " +
                    "The approved Hub background cannot be shown.");
                return;
            }

            Transform? existing = stage.Find(BackgroundObjectName);
            _backgroundPlane = existing != null
                ? existing.gameObject
                : GameObject.CreatePrimitive(PrimitiveType.Quad);
            _backgroundPlane.name = BackgroundObjectName;
            _backgroundPlane.transform.SetParent(stage, true);
            _backgroundPlane.SetActive(true);

            Collider? collider = _backgroundPlane.GetComponent<Collider>();
            if (collider != null)
                Destroy(collider);

            MeshRenderer renderer = _backgroundPlane.GetComponent<MeshRenderer>();
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = LightProbeUsage.Off;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            renderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;

            Shader? shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Texture");
            if (shader == null)
            {
                Debug.LogError("HubScreen: no unlit shader is available for Hub_background.");
                return;
            }

            _backgroundMaterial = new Material(shader)
            {
                name = "HubBackgroundRuntimeMaterial",
                mainTexture = _backgroundTexture,
            };
            if (_backgroundMaterial.HasProperty("_BaseMap"))
                _backgroundMaterial.SetTexture("_BaseMap", _backgroundTexture);
            if (_backgroundMaterial.HasProperty("_BaseColor"))
                _backgroundMaterial.SetColor("_BaseColor", Color.white);
            if (_backgroundMaterial.HasProperty("_Cull"))
                _backgroundMaterial.SetFloat("_Cull", (float)CullMode.Off);
            renderer.sharedMaterial = _backgroundMaterial;

            LayoutBackgroundPlane();
        }

        private void LayoutBackgroundPlane()
        {
            if (_backgroundPlane == null || _hubCamera == null || _backgroundTexture == null)
                return;

            Transform cameraTransform = _hubCamera.transform;
            _backgroundPlane.transform.position =
                cameraTransform.position + cameraTransform.forward * BackgroundDistance;
            _backgroundPlane.transform.rotation = cameraTransform.rotation;

            float height = _hubCamera.orthographic
                ? _hubCamera.orthographicSize * 2f
                : 2f * BackgroundDistance * Mathf.Tan(_hubCamera.fieldOfView * 0.5f * Mathf.Deg2Rad);
            float screenAspect = Mathf.Max(0.01f, _hubCamera.aspect);
            float width = height * screenAspect;
            _backgroundPlane.transform.localScale = new Vector3(width, height, 1f);

            if (_backgroundMaterial == null)
                return;

            float imageAspect = (float)_backgroundTexture.width / _backgroundTexture.height;
            Vector2 textureScale = Vector2.one;
            Vector2 textureOffset = Vector2.zero;
            if (screenAspect > imageAspect)
            {
                textureScale.y = imageAspect / screenAspect;
                textureOffset.y = (1f - textureScale.y) * 0.5f;
            }
            else
            {
                textureScale.x = screenAspect / imageAspect;
                textureOffset.x = (1f - textureScale.x) * 0.5f;
            }

            _backgroundMaterial.mainTextureScale = textureScale;
            _backgroundMaterial.mainTextureOffset = textureOffset;
            if (_backgroundMaterial.HasProperty("_BaseMap"))
            {
                _backgroundMaterial.SetTextureScale("_BaseMap", textureScale);
                _backgroundMaterial.SetTextureOffset("_BaseMap", textureOffset);
            }
        }

        private void BuildUi()
        {
            UIDocument document = ArenaPanel.CreateDocument(gameObject, "UI/Toolkit/Hub", 20f);
            _panelSettings = document.panelSettings;
            _root = document.rootVisualElement.Q<VisualElement>("HubScreen");
            if (_root == null)
            {
                Debug.LogError("HubScreen: Hub.uxml did not load or is missing HubScreen.");
                return;
            }

            _showcase = _root.Q<VisualElement>("PlayerShowcase");
            _playerName = _root.Q<Label>("PlayerName");
            _loadoutPrimaryRow = _root.Q<VisualElement>("LoadoutPrimaryRow");
            _loadoutSecondary1Row = _root.Q<VisualElement>("LoadoutSecondary1Row");
            _loadoutSecondary2Row = _root.Q<VisualElement>("LoadoutSecondary2Row");
            _loadoutPrimaryName = _root.Q<Label>("LoadoutPrimaryName");
            _loadoutSecondary1Name = _root.Q<Label>("LoadoutSecondary1Name");
            _loadoutSecondary2Name = _root.Q<Label>("LoadoutSecondary2Name");
            _loadoutPrimaryGlyph = _root.Q<Label>("LoadoutPrimaryGlyph");
            _loadoutSecondary1Glyph = _root.Q<Label>("LoadoutSecondary1Glyph");
            _loadoutSecondary2Glyph = _root.Q<Label>("LoadoutSecondary2Glyph");
            _partyCount = _root.Q<Label>("PartyCount");
            _practiceButton = _root.Q<Button>("PracticeButton");
            _navDisciplines = _root.Q<Button>("NavDisciplines");
            _navEquipment = _root.Q<Button>("NavEquipment");
            _queueButton = _root.Q<Button>("QueueButton");
            _queueName = _root.Q<Label>("QueueName");
            _findMatchButton = _root.Q<Button>("FindMatchButton");
            _findMatchTitle = _root.Q<Label>("FindMatchTitle");
            _findMatchSubtitle = _root.Q<Label>("FindMatchSubtitle");
            _matchOverlay = _root.Q<VisualElement>("MatchOverlay");
            _overlayScrim = _root.Q<Button>("OverlayScrim");
            _dialogClose = _root.Q<Button>("DialogClose");
            _dialogQueueName = _root.Q<Label>("DialogQueueName");
            _format2v2 = _root.Q<Button>("Format2v2");
            _format3v3 = _root.Q<Button>("Format3v3");
            _format10v10 = _root.Q<Button>("Format10v10");
            _format2v2Name = _format2v2?.Q<Label>(className: "format-name");
            _format3v3Name = _format3v3?.Q<Label>(className: "format-name");
            _format10v10Name = _format10v10?.Q<Label>(className: "format-name");
            _format3v3Type = _format3v3?.Q<Label>(className: "format-type");
            _format10v10Type = _format10v10?.Q<Label>(className: "format-type");
            _selectionValue = _root.Q<Label>("SelectionValue");
            _queueConfirm = _root.Q<Button>("QueueConfirm");
            _queueConfirmTitle = _queueConfirm?.Q<Label>(className: "queue-confirm-title");
            _queueConfirmSubtitle = _root.Q<Label>("QueueConfirmSubtitle");
            _hubController = GetComponent<HubController>();

            BindShowcaseDrag();
            BindMatchmakingControls();

            _disciplinesScreen = DisciplinesScreen.Ensure(transform);
            _disciplinesScreen.Closed += OnDisciplinesClosed;
            _disciplinesScreen.EquipmentRequested += OpenEquipment;
            _equipmentScreen = EquipmentScreen.Ensure(transform);
            _equipmentScreen.Closed += OnEquipmentClosed;
            _equipmentScreen.DisciplinesRequested += OpenDisciplines;
            if (_practiceButton != null)
                _practiceButton.clicked += OpenPracticeMenu;
            if (_navDisciplines != null)
                _navDisciplines.clicked += OpenDisciplines;
            if (_navEquipment != null)
                _navEquipment.clicked += OpenEquipment;

            Button? settingsButton = _root.Q<Button>("SettingsButton");
            if (settingsButton != null)
                settingsButton.clicked += SystemMenuScreen.OpenFromEscape;
        }

        private void BindMatchmakingControls()
        {
            if (_findMatchButton != null)
                _findMatchButton.clicked += OnFindMatchClicked;
            if (_overlayScrim != null)
                _overlayScrim.clicked += CloseMatchOverlay;
            if (_dialogClose != null)
                _dialogClose.clicked += CloseMatchOverlay;
            if (_format2v2 != null)
                _format2v2.clicked += Select2v2;
            if (_queueConfirm != null)
                _queueConfirm.clicked += ConfirmMatchSearch;

            RefreshMatchmakingPresentation();
        }

        private void UnbindMatchmakingControls()
        {
            if (_findMatchButton != null)
                _findMatchButton.clicked -= OnFindMatchClicked;
            if (_overlayScrim != null)
                _overlayScrim.clicked -= CloseMatchOverlay;
            if (_dialogClose != null)
                _dialogClose.clicked -= CloseMatchOverlay;
            if (_format2v2 != null)
                _format2v2.clicked -= Select2v2;
            if (_queueConfirm != null)
                _queueConfirm.clicked -= ConfirmMatchSearch;
        }

        private void OnFindMatchClicked()
        {
            if (IsMatchRequestPending)
                return;

            OpenMatchOverlay();
        }

        private void OpenMatchOverlay()
        {
            if (_matchOverlay == null)
                return;

            _matchOverlayOpen = true;
            _matchOverlay.AddToClassList(OpenClass);
            RefreshMatchmakingPresentation();

            Button? selectedButton = _selectedFormat switch
            {
                MatchFormat.TwoVersusTwo => _format2v2,
                MatchFormat.TenVersusTen => _format10v10,
                _ => _format3v3,
            };
            selectedButton?.Focus();
        }

        private void CloseMatchOverlay()
        {
            if (!_matchOverlayOpen || IsMatchRequestPending)
                return;

            _matchOverlayOpen = false;
            _matchOverlay?.RemoveFromClassList(OpenClass);
            _findMatchButton?.Focus();
        }

        public bool TryCloseForEscape()
        {
            if (!_matchOverlayOpen || IsMatchRequestPending)
                return false;

            CloseMatchOverlay();
            return true;
        }

        private void Select2v2() => SelectMatchFormat(MatchFormat.TwoVersusTwo);

        private void SelectMatchFormat(MatchFormat format)
        {
            if (format != MatchFormat.TwoVersusTwo || IsMatchRequestPending)
                return;

            _selectedFormat = format;
            RefreshMatchmakingPresentation();
        }

        private void ConfirmMatchSearch()
        {
            _matchHandoff ??= MatchHandoffCoordinator.EnsureInstance();
            if (IsMatchRequestPending)
                return;
            _matchHandoff.RequestUnranked2V2BotMatch();
            RefreshMatchmakingPresentation();
        }

        private void RefreshMatchmakingPresentation()
        {
            const string queueLabel = "UNRANKED";
            const string queueTitle = "Unranked";
            string formatLabel = GetMatchFormatLabel(_selectedFormat);
            bool pending = IsMatchRequestPending;
            bool canRequest = _matchHandoff?.CanRequestMatch == true;
            string handoffStatus = _matchHandoff?.StatusMessage ?? "CONNECTING TO HUB…";

            if (_queueName != null)
                _queueName.text = queueLabel;
            if (_queueButton != null)
            {
                _queueButton.tooltip = "The first playable queue is unranked.";
                _queueButton.SetEnabled(false);
            }

            if (_findMatchButton != null)
            {
                _findMatchButton.EnableInClassList(SearchingClass, pending);
                _findMatchButton.SetEnabled(!pending && canRequest);
            }
            if (_findMatchTitle != null)
                _findMatchTitle.text = pending ? "STARTING MATCH…" : "PLAY 2V2";
            if (_findMatchSubtitle != null)
                _findMatchSubtitle.text = $"{queueLabel} BOT MATCH";

            if (_dialogQueueName != null)
                _dialogQueueName.text = queueTitle;
            if (_selectionValue != null)
                _selectionValue.text = $"{queueLabel} · {formatLabel}";
            if (_queueConfirmSubtitle != null)
                _queueConfirmSubtitle.text = string.IsNullOrWhiteSpace(handoffStatus)
                    ? "YOU + AN ALLY DUMMY VS TWO ENEMY DUMMIES"
                    : handoffStatus;
            if (_queueConfirmTitle != null)
            {
                _queueConfirmTitle.text = pending
                    ? "STARTING MATCH…"
                    : "START 2V2 BOT MATCH";
            }
            if (_queueConfirm != null)
            {
                _queueConfirm.SetEnabled(
                    !pending && canRequest);
            }

            if (_format2v2 != null)
            {
                if (_format2v2Name != null)
                    _format2v2Name.text = "2V2";
                _format2v2.SetEnabled(!pending);
            }
            if (_format3v3 != null)
            {
                if (_format3v3Name != null)
                    _format3v3Name.text = "3V3";
                if (_format3v3Type != null)
                    _format3v3Type.text = "COMING SOON";
                _format3v3.tooltip = "3v3 is not part of this first playable slice.";
                _format3v3.SetEnabled(false);
            }
            if (_format10v10 != null)
            {
                if (_format10v10Name != null)
                    _format10v10Name.text = "10V10";
                if (_format10v10Type != null)
                    _format10v10Type.text = "COMING SOON";
                _format10v10.tooltip = "10v10 is not part of this first playable slice.";
                _format10v10.SetEnabled(false);
            }

            _format2v2?.EnableInClassList(
                SelectedClass,
                _selectedFormat == MatchFormat.TwoVersusTwo);
            _format3v3?.EnableInClassList(
                SelectedClass,
                _selectedFormat == MatchFormat.ThreeVersusThree);
            _format10v10?.EnableInClassList(
                SelectedClass,
                _selectedFormat == MatchFormat.TenVersusTen);
        }

        private static string GetMatchFormatLabel(MatchFormat format)
            => format switch
            {
                MatchFormat.TwoVersusTwo => "2V2",
                MatchFormat.TenVersusTen => "10V10",
                _ => "3V3",
            };

        private void OpenPracticeMenu()
        {
            _hubController?.OpenPracticeMenu();
        }

        private void OpenDisciplines()
        {
            if (_root == null || _disciplinesScreen == null)
                return;

            _root.style.display = DisplayStyle.None;
            _disciplinesScreen.Open();
        }

        private void OnDisciplinesClosed()
        {
            if (_root != null)
                _root.style.display = DisplayStyle.Flex;
            _hubController?.RefreshShowcaseLoadout();
            RefreshBoundData();
        }

        private void OpenEquipment()
        {
            if (_root == null || _equipmentScreen == null)
                return;

            _root.style.display = DisplayStyle.None;
            _equipmentScreen.Open();
        }

        private void OnEquipmentClosed()
        {
            if (_root != null)
                _root.style.display = DisplayStyle.Flex;
            _hubController?.RefreshShowcaseLoadout();
            RefreshBoundData();
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

        private void RefreshBoundData()
        {
            HubNetworkManager? hub = HubNetworkManager.Instance;
            HubPlayerSnapshot? hubPlayer = hub?.Player;
            if (_playerName != null
                && hubPlayer.HasValue
                && !string.IsNullOrWhiteSpace(hubPlayer.Value.DisplayName))
            {
                _playerName.text = hubPlayer.Value.DisplayName.Trim().ToUpperInvariant();
            }

            RefreshDisciplineLoadout(hub);

            if (_partyCount != null)
                _partyCount.text = "1 / 4";
        }

        private void RefreshDisciplineLoadout(HubNetworkManager? hub)
        {
            HubLoadoutSnapshot? loadout = hub?.Loadout;
            if (loadout.HasValue && !string.IsNullOrWhiteSpace(loadout.Value.PrimaryDisciplineId))
            {
                HubLoadoutSnapshot saved = loadout.Value;
                BindDisciplineRow(
                    hub,
                    _loadoutPrimaryRow,
                    _loadoutPrimaryName,
                    _loadoutPrimaryGlyph,
                    saved.PrimaryDisciplineId,
                    visibleWhenEmpty: true);
                BindDisciplineRow(
                    hub,
                    _loadoutSecondary1Row,
                    _loadoutSecondary1Name,
                    _loadoutSecondary1Glyph,
                    saved.SecondaryDisciplineId1,
                    visibleWhenEmpty: false);
                BindDisciplineRow(
                    hub,
                    _loadoutSecondary2Row,
                    _loadoutSecondary2Name,
                    _loadoutSecondary2Glyph,
                    saved.SecondaryDisciplineId2,
                    visibleWhenEmpty: false);
                return;
            }

            BindDisciplineRow(
                hub,
                _loadoutPrimaryRow,
                _loadoutPrimaryName,
                _loadoutPrimaryGlyph,
                null,
                visibleWhenEmpty: true);
            SetRowVisible(_loadoutSecondary1Row, false);
            SetRowVisible(_loadoutSecondary2Row, false);
        }

        private static void BindDisciplineRow(
            HubNetworkManager? hub,
            VisualElement? row,
            Label? name,
            Label? glyph,
            string? disciplineId,
            bool visibleWhenEmpty)
        {
            string normalizedId = WireIdentifier.Normalize(disciplineId);
            bool hasDiscipline = !string.IsNullOrWhiteSpace(normalizedId);
            SetRowVisible(row, hasDiscipline || visibleWhenEmpty);
            if (!hasDiscipline)
            {
                if (name != null)
                    name.text = "UNSELECTED";
                if (glyph != null)
                    glyph.text = "—";
                return;
            }

            HubDisciplineSnapshot? discipline = hub?.Disciplines.FirstOrDefault(candidate =>
                string.Equals(WireIdentifier.Normalize(candidate.Id), normalizedId, System.StringComparison.Ordinal));
            string displayName = !string.IsNullOrWhiteSpace(discipline?.Name)
                ? discipline.Name.Trim().ToUpperInvariant()
                : normalizedId.Replace('_', ' ');
            if (name != null)
                name.text = displayName;
            if (glyph != null)
                glyph.text = displayName.Substring(0, 1);
        }

        private static void SetRowVisible(VisualElement? row, bool visible)
        {
            if (row != null)
                row.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
        }

    }
}
