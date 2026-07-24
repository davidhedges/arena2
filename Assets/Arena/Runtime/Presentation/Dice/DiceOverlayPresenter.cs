#nullable enable
using System;
using System.Collections.Generic;
using Arena.UI;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;
using UiImage = UnityEngine.UIElements.Image;

namespace Arena.Presentation.Dice
{
    public enum DicePresentationState
    {
        Inactive,
        Anticipation,
        Tumbling,
        Settling,
        Held
    }

    [DefaultExecutionOrder(70)]
    [DisallowMultipleComponent]
    public sealed class DiceOverlayPresenter : MonoBehaviour
    {
        private const string CatalogResourcePath = "Dice/DefaultDiceSet";
        private const string OverlayResourcePath = "UI/Toolkit/DiceOverlay";
        private const string OverlayLayerName = "DiceOverlay3D";
        private const float CameraFieldOfView = 30f;
        private const float MinimumDepth = 5.3f;
        private const float DepthTravelUnits = 1.15f;
        private const int MinimumRenderDimension = 320;
        private const int MaximumRenderDimension = 2048;
        private const int MaterialResizeThreshold = 64;

        private static readonly Vector3 IsolatedWorldOrigin = new(10000f, 10000f, 10000f);
        private static readonly Rect FullRegion = new(0f, 0f, 1f, 1f);
        private static DiceOverlayPresenter? s_instance;

        private DiceSetCatalog? _catalog;
        private PanelSettings? _panelSettings;
        private VisualElement? _overlayRoot;
        private UiImage? _viewport;
        private GameObject? _renderRoot;
        private Camera? _overlayCamera;
        private RenderTexture? _renderTexture;
        private GameObject? _diceObject;
        private Transform? _diceTransform;
        private DiceFaceLabel[] _labels = Array.Empty<DiceFaceLabel>();
        private Material? _resinMaterialInstance;
        private Material? _numeralMaterialInstance;
        private DiceDefinition? _activeDefinition;
        private DiceMotionProfile? _activeProfile;
        private DiceFace? _activeFace;
        private ResolvedDiceRoll _activeRequest;
        private Rect _presentationRegion = FullRegion;
        private Quaternion _entryRotation = Quaternion.identity;
        private Quaternion _finalRotation = Quaternion.identity;
        private Vector3 _finalPosition;
        private float _baseDepth = MinimumDepth;
        private float _elapsed;
        private bool _runtimeOwned;
        private bool _initialized;
        private int _overlayLayer = -1;

        public static DiceOverlayPresenter? Instance => s_instance;
        public DicePresentationState State { get; private set; } = DicePresentationState.Inactive;
        public bool IsActive => State != DicePresentationState.Inactive;
        public bool IsMoving =>
            State == DicePresentationState.Anticipation ||
            State == DicePresentationState.Tumbling ||
            State == DicePresentationState.Settling;
        public ResolvedDiceRoll ActiveRequest => _activeRequest;
        public IReadOnlyList<DiceMotionProfile> MotionProfiles =>
            _catalog != null ? _catalog.MotionProfiles : Array.Empty<DiceMotionProfile>();

        public event Action<DicePresentationState>? StateChanged;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (FindAnyObjectByType<DiceOverlayPresenter>() != null)
                return;
            if (!ArenaRuntimeSceneGate.ShouldRunArenaRuntimeInActiveScene())
                return;

            GameObject host = new("DiceOverlayPresenter");
            DiceOverlayPresenter presenter = host.AddComponent<DiceOverlayPresenter>();
            presenter._runtimeOwned = true;
            DontDestroyOnLoad(host);
        }

        private void Awake()
        {
            if (s_instance != null && s_instance != this)
            {
                Destroy(gameObject);
                return;
            }

            s_instance = this;
            RuntimeUiEventSystem.Ensure();
            Initialize();
        }

        private void OnDestroy()
        {
            if (s_instance == this)
                s_instance = null;

            if (_viewport != null)
                _viewport.image = null;
            if (_overlayCamera != null)
                _overlayCamera.targetTexture = null;
            ReleaseRenderTexture();
            if (_resinMaterialInstance != null)
                Destroy(_resinMaterialInstance);
            if (_numeralMaterialInstance != null)
                Destroy(_numeralMaterialInstance);
            if (_panelSettings != null)
                Destroy(_panelSettings);
        }

        private void Update()
        {
            if (!_initialized)
                return;

            if (_runtimeOwned && !ArenaRuntimeSceneGate.ShouldRunArenaRuntimeInActiveScene())
            {
                if (IsActive)
                    Dismiss();
                return;
            }

            EnsureRenderTexture(force: false);
            if (!IsMoving || _activeProfile == null || _diceTransform == null || _activeFace == null)
                return;

            _elapsed += Time.unscaledDeltaTime;
            EvaluateMotion(_elapsed);
            UpdateLabelVisibility();
        }

        public bool Show(
            ResolvedDiceRoll resolvedRoll,
            DiceMotionProfile? motionOverride = null,
            Rect? normalizedRegion = null)
        {
            if (!_initialized || _catalog == null || _diceObject == null || _diceTransform == null)
                return false;
            if (IsActive)
                return false;
            if (string.IsNullOrWhiteSpace(resolvedRoll.RequestId) ||
                string.IsNullOrWhiteSpace(resolvedRoll.DieId))
            {
                Debug.LogError("[DiceOverlayPresenter] A resolved roll requires request and die identifiers.");
                return false;
            }
            if (!_catalog.TryGetDefinition(resolvedRoll.DieId, out DiceDefinition definition) ||
                !definition.TryGetFace(resolvedRoll.Value, out DiceFace face))
            {
                Debug.LogError(
                    $"[DiceOverlayPresenter] Result {resolvedRoll.Value} is not valid for '{resolvedRoll.DieId}'.");
                return false;
            }
            if (_catalog.MotionProfiles.Count == 0)
            {
                Debug.LogError("[DiceOverlayPresenter] The dice catalog has no motion profiles.");
                return false;
            }

            _activeRequest = resolvedRoll;
            _activeDefinition = definition;
            _activeFace = face;
            _activeProfile = motionOverride != null
                ? motionOverride
                : _catalog.MotionProfiles[(int)(StableHash(resolvedRoll.RequestId) % _catalog.MotionProfiles.Count)];

            if (normalizedRegion.HasValue)
                SetPresentationRegion(normalizedRegion.Value);
            else
                SetPresentationRegion(FullRegion);

            EnsureRenderTexture(force: true);
            CalculateFinalPose();
            uint cosmeticHash = StableHash(resolvedRoll.RequestId);
            Vector3 variation = new(
                HashRange(cosmeticHash, 0, -12f, 12f),
                HashRange(cosmeticHash, 8, -16f, 16f),
                HashRange(cosmeticHash, 16, -10f, 10f));
            _entryRotation = Quaternion.Euler(_activeProfile.EntryEuler + variation);
            _elapsed = 0f;

            _diceObject.SetActive(true);
            if (_overlayCamera != null)
                _overlayCamera.enabled = true;
            if (_overlayRoot != null)
                _overlayRoot.style.display = DisplayStyle.Flex;
            if (_panelSettings != null)
                _panelSettings.sortingOrder = RuntimeUiLayer.NextSortingOrder();

            SetMovingPicking(enabled: true);
            EvaluateMotion(0f);
            UpdateLabelVisibility();
            return true;
        }

        public void SkipToResult()
        {
            if (!IsMoving)
                return;

            ApplyFinalPose();
            EnterHeldState();
        }

        public void Dismiss()
        {
            if (!IsActive)
                return;

            if (_diceObject != null)
                _diceObject.SetActive(false);
            if (_overlayCamera != null)
                _overlayCamera.enabled = false;
            if (_overlayRoot != null)
                _overlayRoot.style.display = DisplayStyle.None;
            SetMovingPicking(enabled: false);

            _activeDefinition = null;
            _activeProfile = null;
            _activeFace = null;
            _elapsed = 0f;
            SetState(DicePresentationState.Inactive);
        }

        public void SetPresentationRegion(Rect normalizedRegion)
        {
            float width = Mathf.Clamp(normalizedRegion.width, 0.12f, 1f);
            float height = Mathf.Clamp(normalizedRegion.height, 0.12f, 1f);
            float x = Mathf.Clamp(normalizedRegion.x, 0f, 1f - width);
            float y = Mathf.Clamp(normalizedRegion.y, 0f, 1f - height);
            _presentationRegion = new Rect(x, y, width, height);

            if (_viewport != null)
            {
                _viewport.style.left = Length.Percent(x * 100f);
                _viewport.style.top = Length.Percent((1f - y - height) * 100f);
                _viewport.style.width = Length.Percent(width * 100f);
                _viewport.style.height = Length.Percent(height * 100f);
            }

            EnsureRenderTexture(force: true);
            if (IsActive)
            {
                CalculateFinalPose();
                if (State == DicePresentationState.Held)
                    ApplyFinalPose();
            }
        }

        private void Initialize()
        {
            _catalog = Resources.Load<DiceSetCatalog>(CatalogResourcePath);
            if (_catalog == null)
            {
                Debug.LogError(
                    $"[DiceOverlayPresenter] Missing Resources/{CatalogResourcePath}. " +
                    "Rebuild the d20 overlay assets from the Arena/Dice menu.");
                return;
            }

            _overlayLayer = LayerMask.NameToLayer(OverlayLayerName);
            if (_overlayLayer < 0)
            {
                Debug.LogError($"[DiceOverlayPresenter] Missing Unity layer '{OverlayLayerName}'.");
                return;
            }

            BuildUi();
            BuildRenderRig();
            WarmD20();
            EnsureRenderTexture(force: true);
            _initialized = _overlayRoot != null &&
                           _viewport != null &&
                           _overlayCamera != null &&
                           _diceObject != null;
        }

        private void BuildUi()
        {
            UIDocument document = ArenaPanel.CreateDocument(
                gameObject,
                OverlayResourcePath,
                RuntimeUiLayer.NextSortingOrder());
            _panelSettings = document.panelSettings;
            _overlayRoot = document.rootVisualElement.Q<VisualElement>("DiceOverlay");
            _viewport = document.rootVisualElement.Q<UiImage>("DiceViewport");
            if (_overlayRoot == null || _viewport == null)
            {
                Debug.LogError("[DiceOverlayPresenter] DiceOverlay.uxml is missing its required elements.");
                return;
            }

            _overlayRoot.style.display = DisplayStyle.None;
            _overlayRoot.pickingMode = PickingMode.Ignore;
            _viewport.scaleMode = ScaleMode.ScaleToFit;
            _viewport.pickingMode = PickingMode.Ignore;
            _viewport.RegisterCallback<PointerDownEvent>(OnViewportPointerDown);
            SetPresentationRegion(FullRegion);
        }

        private void BuildRenderRig()
        {
            _renderRoot = new GameObject("DiceOverlayRenderRig");
            _renderRoot.transform.SetParent(transform, false);
            _renderRoot.transform.position = IsolatedWorldOrigin;
            SetLayerRecursively(_renderRoot, _overlayLayer);

            GameObject cameraObject = new("DiceOverlayCamera");
            cameraObject.transform.SetParent(_renderRoot.transform, false);
            cameraObject.transform.localPosition = new Vector3(0f, 0f, -MinimumDepth);
            cameraObject.transform.localRotation = Quaternion.identity;
            _overlayCamera = cameraObject.AddComponent<Camera>();
            _overlayCamera.clearFlags = CameraClearFlags.SolidColor;
            _overlayCamera.backgroundColor = new Color(0f, 0f, 0f, 0f);
            _overlayCamera.cullingMask = 1 << _overlayLayer;
            _overlayCamera.fieldOfView = CameraFieldOfView;
            _overlayCamera.nearClipPlane = 0.1f;
            _overlayCamera.farClipPlane = 50f;
            _overlayCamera.allowHDR = true;
            _overlayCamera.allowMSAA = true;
            _overlayCamera.useOcclusionCulling = false;
            _overlayCamera.enabled = false;

            CreateOverlayLight(
                "DiceWarmKey",
                new Vector3(-2.6f, 2.4f, -3.4f),
                new Color(1f, 0.72f, 0.54f),
                12f,
                12f,
                48f);
            CreateOverlayLight(
                "DiceCoolFill",
                new Vector3(2.8f, 0.5f, -2.5f),
                new Color(0.40f, 0.56f, 1f),
                5.5f,
                12f,
                52f);
            CreateOverlayLight(
                "DiceEmberRim",
                new Vector3(0.6f, 2.2f, 3.2f),
                new Color(1f, 0.12f, 0.025f),
                10f,
                12f,
                46f);
        }

        private void WarmD20()
        {
            if (_catalog == null ||
                !_catalog.TryGetDefinition("d20", out DiceDefinition definition) ||
                definition.VisualPrefab == null)
            {
                Debug.LogError("[DiceOverlayPresenter] The default catalog has no usable d20.");
                return;
            }
            if (_catalog.ResinMaterial == null || _catalog.NumeralMaterial == null)
            {
                Debug.LogError("[DiceOverlayPresenter] The default catalog is missing dice materials.");
                return;
            }

            _resinMaterialInstance = new Material(_catalog.ResinMaterial)
            {
                name = "DiceOverlay Resin Instance"
            };
            _numeralMaterialInstance = new Material(_catalog.NumeralMaterial)
            {
                name = "DiceOverlay Numeral Instance"
            };

            _diceObject = Instantiate(definition.VisualPrefab, _renderRoot!.transform);
            _diceObject.name = "WarmedD20";
            _diceTransform = _diceObject.transform;
            _diceTransform.localPosition = Vector3.zero;
            _diceTransform.localRotation = Quaternion.identity;
            _diceTransform.localScale = Vector3.one * definition.PresentationScale;
            SetLayerRecursively(_diceObject, _overlayLayer);

            MeshRenderer? bodyRenderer = _diceObject.GetComponent<MeshRenderer>();
            if (bodyRenderer != null)
                bodyRenderer.sharedMaterial = _resinMaterialInstance;
            _labels = _diceObject.GetComponentsInChildren<DiceFaceLabel>(includeInactive: true);
            for (int i = 0; i < _labels.Length; i++)
                _labels[i].Text.fontSharedMaterial = _numeralMaterialInstance;
            _diceObject.SetActive(false);
        }

        private void CreateOverlayLight(
            string name,
            Vector3 localPosition,
            Color color,
            float intensity,
            float range,
            float spotAngle)
        {
            GameObject lightObject = new(name);
            lightObject.transform.SetParent(_renderRoot!.transform, false);
            lightObject.transform.localPosition = localPosition;
            lightObject.transform.localRotation =
                Quaternion.LookRotation(-localPosition.normalized, Vector3.up);
            Light light = lightObject.AddComponent<Light>();
            light.type = LightType.Spot;
            light.color = color;
            light.intensity = intensity;
            light.range = range;
            light.spotAngle = spotAngle;
            light.innerSpotAngle = spotAngle * 0.58f;
            light.shadows = LightShadows.Soft;
            light.cullingMask = 1 << _overlayLayer;
            lightObject.layer = _overlayLayer;
        }

        private void EvaluateMotion(float elapsed)
        {
            if (_activeProfile == null || _diceTransform == null || _activeDefinition == null)
                return;

            float duration = Mathf.Max(0.01f, _activeProfile.TotalDuration);
            float normalizedTime = Mathf.Clamp01(elapsed / duration);
            float pathTime = _activeProfile.EvaluatePositionTime(normalizedTime);
            _diceTransform.position = PositionForProfile(_activeProfile, pathTime);
            _diceTransform.localScale =
                Vector3.one * (_activeDefinition.PresentationScale *
                               _activeProfile.EvaluateScale(pathTime));

            float spinAngle = 360f * _activeProfile.TurnCount * normalizedTime;
            Vector3 spinAxis = _activeProfile.SpinAxis;
            Vector3 secondaryAxis = Vector3.Cross(spinAxis, Vector3.up);
            if (secondaryAxis.sqrMagnitude < 0.01f)
                secondaryAxis = Vector3.right;
            Quaternion freeRotation =
                Quaternion.AngleAxis(spinAngle, spinAxis) *
                Quaternion.AngleAxis(spinAngle * 0.23f, secondaryAxis.normalized) *
                _entryRotation;

            float settleTime = Mathf.InverseLerp(_activeProfile.SettleStart, 1f, normalizedTime);
            float settleWeight = _activeProfile.EvaluateSettle(settleTime);
            _diceTransform.rotation = Quaternion.Slerp(freeRotation, _finalRotation, settleWeight);

            if (elapsed < _activeProfile.AnticipationDuration)
                SetState(DicePresentationState.Anticipation);
            else if (normalizedTime < _activeProfile.SettleStart)
                SetState(DicePresentationState.Tumbling);
            else
                SetState(DicePresentationState.Settling);

            if (normalizedTime < 1f)
                return;

            ApplyFinalPose();
            EnterHeldState();
        }

        private Vector3 PositionForProfile(DiceMotionProfile profile, float normalizedTime)
        {
            if (_overlayCamera == null)
                return _finalPosition;

            float viewportX = Mathf.Clamp01(0.5f + profile.EvaluateHorizontal(normalizedTime));
            float viewportY = Mathf.Clamp01(0.5f + profile.EvaluateVertical(normalizedTime));
            float depth = Mathf.Max(1f, _baseDepth + profile.EvaluateDepth(normalizedTime) * DepthTravelUnits);
            return _overlayCamera.ViewportToWorldPoint(new Vector3(viewportX, viewportY, depth));
        }

        private void CalculateFinalPose()
        {
            if (_overlayCamera == null || _activeFace == null)
                return;

            _baseDepth = CalculateBaseDepth();
            _finalPosition = _overlayCamera.ViewportToWorldPoint(new Vector3(0.5f, 0.5f, _baseDepth));
            _finalRotation = DicePoseSolver.FaceTowardCamera(
                _activeFace,
                _overlayCamera.transform.position - _finalPosition,
                _overlayCamera.transform.up);
        }

        private float CalculateBaseDepth()
        {
            float aspect = _overlayCamera != null ? Mathf.Max(0.2f, _overlayCamera.aspect) : 1f;
            float tangent = Mathf.Tan(CameraFieldOfView * 0.5f * Mathf.Deg2Rad);
            const float framedRadius = 1.42f;
            float verticalDepth = framedRadius / tangent;
            float horizontalDepth = framedRadius / (tangent * aspect);
            return Mathf.Max(MinimumDepth, verticalDepth, horizontalDepth);
        }

        private void ApplyFinalPose()
        {
            if (_diceTransform == null || _activeDefinition == null)
                return;

            _diceTransform.position = _finalPosition;
            _diceTransform.rotation = _finalRotation;
            _diceTransform.localScale = Vector3.one * _activeDefinition.PresentationScale;
            UpdateLabelVisibility();
        }

        private void EnterHeldState()
        {
            SetMovingPicking(enabled: false);
            SetState(DicePresentationState.Held);
        }

        private void SetState(DicePresentationState state)
        {
            if (State == state)
                return;

            State = state;
            StateChanged?.Invoke(state);
        }

        private void SetMovingPicking(bool enabled)
        {
            if (_viewport != null)
                _viewport.pickingMode = enabled ? PickingMode.Position : PickingMode.Ignore;
        }

        private void OnViewportPointerDown(PointerDownEvent evt)
        {
            if (!IsMoving)
                return;

            SkipToResult();
            evt.StopPropagation();
        }

        private void UpdateLabelVisibility()
        {
            if (_diceTransform == null || _overlayCamera == null)
                return;

            Vector3 towardCamera =
                (_overlayCamera.transform.position - _diceTransform.position).normalized;
            for (int i = 0; i < _labels.Length; i++)
            {
                DiceFaceLabel label = _labels[i];
                Vector3 worldNormal = _diceTransform.TransformDirection(label.OutwardNormal).normalized;
                label.Text.renderer.enabled = Vector3.Dot(worldNormal, towardCamera) > 0.02f;
            }
        }

        private void EnsureRenderTexture(bool force)
        {
            if (_overlayCamera == null || _viewport == null)
                return;

            float rawWidth = Mathf.Max(1f, Screen.width * _presentationRegion.width);
            float rawHeight = Mathf.Max(1f, Screen.height * _presentationRegion.height);
            float resizeScale = 1f;
            float largestDimension = Mathf.Max(rawWidth, rawHeight);
            if (largestDimension > MaximumRenderDimension)
                resizeScale = MaximumRenderDimension / largestDimension;
            float smallestScaledDimension = Mathf.Min(rawWidth, rawHeight) * resizeScale;
            if (smallestScaledDimension < MinimumRenderDimension)
                resizeScale *= MinimumRenderDimension / smallestScaledDimension;

            int desiredWidth = Mathf.Clamp(
                Mathf.RoundToInt(rawWidth * resizeScale),
                MinimumRenderDimension,
                MaximumRenderDimension);
            int desiredHeight = Mathf.Clamp(
                Mathf.RoundToInt(rawHeight * resizeScale),
                MinimumRenderDimension,
                MaximumRenderDimension);
            if (_renderTexture != null &&
                ((_renderTexture.width == desiredWidth && _renderTexture.height == desiredHeight) ||
                 (!force &&
                  Mathf.Abs(_renderTexture.width - desiredWidth) < MaterialResizeThreshold &&
                  Mathf.Abs(_renderTexture.height - desiredHeight) < MaterialResizeThreshold)))
            {
                return;
            }

            ReleaseRenderTexture();
            _renderTexture = new RenderTexture(
                desiredWidth,
                desiredHeight,
                24,
                RenderTextureFormat.ARGBHalf,
                RenderTextureReadWrite.Default)
            {
                name = $"DiceOverlay_{desiredWidth}x{desiredHeight}",
                antiAliasing = 4,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                useMipMap = false,
                autoGenerateMips = false
            };
            _renderTexture.Create();
            _overlayCamera.targetTexture = _renderTexture;
            _overlayCamera.aspect = desiredWidth / (float)desiredHeight;
            _viewport.image = _renderTexture;

            if (IsActive)
            {
                CalculateFinalPose();
                if (State == DicePresentationState.Held)
                    ApplyFinalPose();
            }
        }

        private void ReleaseRenderTexture()
        {
            if (_renderTexture == null)
                return;

            if (_overlayCamera != null && _overlayCamera.targetTexture == _renderTexture)
                _overlayCamera.targetTexture = null;
            _renderTexture.Release();
            Destroy(_renderTexture);
            _renderTexture = null;
        }

        private static void SetLayerRecursively(GameObject root, int layer)
        {
            root.layer = layer;
            Transform transform = root.transform;
            for (int i = 0; i < transform.childCount; i++)
                SetLayerRecursively(transform.GetChild(i).gameObject, layer);
        }

        private static uint StableHash(string value)
        {
            const uint offset = 2166136261u;
            const uint prime = 16777619u;
            uint hash = offset;
            for (int i = 0; i < value.Length; i++)
            {
                char character = value[i];
                hash ^= (byte)(character & 0xff);
                hash *= prime;
                hash ^= (byte)(character >> 8);
                hash *= prime;
            }
            return hash;
        }

        private static float HashRange(uint hash, int shift, float minimum, float maximum)
        {
            float normalized = ((hash >> shift) & 0xffu) / 255f;
            return Mathf.Lerp(minimum, maximum, normalized);
        }
    }
}
