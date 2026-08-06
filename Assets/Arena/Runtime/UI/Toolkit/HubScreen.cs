#nullable enable

using Arena.Combat;
using Arena.Network;
using SpacetimeDB;
using SpacetimeDB.Types;
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
    public sealed class HubScreen : MonoBehaviour
    {
        private const string HubSceneName = "Hub";
        private const string BackgroundResourcePath = "Hub/Hub_background";
        private const string BackgroundObjectName = "HubBackgroundPlane";
        private const float BackgroundDistance = 30f;
        private const float DataRefreshInterval = 0.25f;

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

        private void Awake()
        {
            if (!IsActiveHubScene())
            {
                enabled = false;
                return;
            }

            RuntimeUiEventSystem.Ensure();
            HideLegacyHubCanvas();
            PrepareCameraLayer();
            BuildUi();
            RefreshBoundData();
        }

        private void OnDestroy()
        {
            UnbindShowcaseDrag();
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
            if (!IsActiveHubScene() || Time.unscaledTime < _nextDataRefresh)
                return;

            _nextDataRefresh = Time.unscaledTime + DataRefreshInterval;
            RefreshBoundData();
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
            _hubController = GetComponent<HubController>();

            BindShowcaseDrag();

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
            DbConnection? conn = NetworkManager.Instance?.Conn;
            Identity? identity = conn?.Identity;
            if (conn == null || !identity.HasValue)
                return;

            Player? player = conn.Db.Player.Identity.Find(identity.Value);
            if (_playerName != null && !string.IsNullOrWhiteSpace(player?.Username))
                _playerName.text = player.Username.Trim().ToUpperInvariant();

            RefreshDisciplineLoadout(conn, identity.Value);

            if (_partyCount != null)
                _partyCount.text = $"{ResolvePartyCount(conn, identity.Value)} / 4";
        }

        private void RefreshDisciplineLoadout(DbConnection conn, Identity identity)
        {
            CharacterDisciplineLoadout? loadout = conn.Db.CharacterDisciplineLoadout.Owner.Find(identity);
            if (loadout != null && !string.IsNullOrWhiteSpace(loadout.PrimaryDisciplineId))
            {
                BindDisciplineRow(
                    conn,
                    _loadoutPrimaryRow,
                    _loadoutPrimaryName,
                    _loadoutPrimaryGlyph,
                    loadout.PrimaryDisciplineId,
                    visibleWhenEmpty: true);
                BindDisciplineRow(
                    conn,
                    _loadoutSecondary1Row,
                    _loadoutSecondary1Name,
                    _loadoutSecondary1Glyph,
                    loadout.SecondaryDisciplineId1,
                    visibleWhenEmpty: false);
                BindDisciplineRow(
                    conn,
                    _loadoutSecondary2Row,
                    _loadoutSecondary2Name,
                    _loadoutSecondary2Glyph,
                    loadout.SecondaryDisciplineId2,
                    visibleWhenEmpty: false);
                return;
            }

            ActiveCombatDiscipline? active = conn.Db.ActiveCombatDiscipline.Owner.Find(identity);
            BindDisciplineRow(
                conn,
                _loadoutPrimaryRow,
                _loadoutPrimaryName,
                _loadoutPrimaryGlyph,
                active?.DisciplineId,
                visibleWhenEmpty: true);
            SetRowVisible(_loadoutSecondary1Row, false);
            SetRowVisible(_loadoutSecondary2Row, false);
        }

        private static void BindDisciplineRow(
            DbConnection conn,
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

            CombatDisciplineCatalog? discipline =
                conn.Db.CombatDisciplineCatalog.DisciplineId.Find(normalizedId);
            string displayName = !string.IsNullOrWhiteSpace(discipline?.DisplayName)
                ? discipline.DisplayName.Trim().ToUpperInvariant()
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

        private static int ResolvePartyCount(DbConnection conn, Identity identity)
        {
            PartyMember? membership = conn.Db.PartyMember.Member.Find(identity);
            if (membership == null)
                return 1;

            return Mathf.Clamp(
                conn.Db.PartyMember.PartyId.Filter(membership.PartyId).Count(),
                1,
                4);
        }
    }
}
