#nullable enable
using UnityEngine;
using Unity.Cinemachine;
using Arena.Simulation;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Arena.Presentation
{
    /// <summary>
    /// Points the scene's Cinemachine camera at the local player's PlayerCameraRoot.
    /// Called once by EntityRegistry.SetupLocalPlayer.
    ///
    /// Uses CinemachineVirtualCameraBase (the common base for both CinemachineCamera
    /// and the deprecated CinemachineVirtualCamera) so it works regardless of which
    /// Cinemachine generation the scene prefab uses.
    /// </summary>
    public static class LocalPlayerCamera
    {
        public static void SetTarget(Transform playerTransform)
        {
            var cameraRoot = FindChildRecursive(playerTransform, "PlayerCameraRoot");
            var target = cameraRoot != null ? cameraRoot : playerTransform;

            if (!RequireSceneMainCameraBrain())
            {
                AbortMissingCameraRig();
                return;
            }

            var cam = FindSceneFollowCamera();
            if (cam != null)
            {
                cam.Follow = target;
                cam.LookAt = target;
                Debug.Log($"[LocalPlayerCamera] Set Follow+LookAt on {cam.GetType().Name} → {target.name}");
            }
            else
            {
                AbortMissingCameraRig();
            }
        }

        private static CinemachineVirtualCameraBase? FindSceneFollowCamera()
        {
            return Object.FindAnyObjectByType<CinemachineVirtualCameraBase>();
        }

        private static bool RequireSceneMainCameraBrain()
        {
            var mainCamera = Camera.main;
            if (mainCamera == null)
                return false;

            return mainCamera.GetComponent<CinemachineBrain>() != null;
        }

        private static void AbortMissingCameraRig()
        {
            Debug.LogError(
                "[LocalPlayerCamera] Missing authored gameplay camera rig. " +
                "Open-world gameplay scenes must include a tagged MainCamera with CinemachineBrain " +
                "and a PlayerFollowCamera/Cinemachine camera. Runtime camera provisioning is disabled.");

#if UNITY_EDITOR
            if (Application.isPlaying)
                EditorApplication.isPlaying = false;
#else
            Application.Quit(1);
#endif
        }

        private static Transform? FindChildRecursive(Transform root, string childName)
        {
            if (root.name == childName)
                return root;

            foreach (Transform child in root)
            {
                var found = FindChildRecursive(child, childName);
                if (found != null)
                    return found;
            }

            return null;
        }
    }

    /// <summary>
    /// Correction presentation for the local player (design review §4, S5).
    ///
    /// Pre-S5 this smoothed the whole presentation root behind the sim root
    /// with a 60 ms position half-life — tuned for rare mispredicts, it
    /// turned a steady reconcile-error stream into continuous elastic
    /// yanking (and dragged the sprinting camera ~0.5 m behind the sim).
    ///
    /// Now the position path spends an explicit budget: normal motion passes
    /// through 1:1, and only reconcile discontinuities (reported by
    /// LocalMovementPredictionDriver) enter a correction offset that decays
    /// at a capped rate; a displacement at/above the snap threshold is shown
    /// once, honestly. The numbers are starting points to be tuned against
    /// the S1/S5 CSV instrumentation, not authored truth.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public sealed class LocalPresentationDriver : MonoBehaviour
    {
        // Reconcile displacements below this are absorbed and decayed;
        // at/above it the presentation snaps once with the sim root.
        private const float CorrectionSnapThresholdMeters = 0.30f;
        // Capped decay rate for the absorbed offset (the review's "cm/s").
        private const float CorrectionDecayMetersPerSecond = 0.5f;
        private const float RotationHalfLifeSeconds = 0.05f;
        private const float HardSnapAngleDegrees = 60.0f;

        private ClientSimulationState? _simState;
        private Transform? _presentationRoot;
        private Vector3 _correctionOffset;
        private Quaternion _smoothedRotation;
        private bool _initialized;

        // S5 evidence counters.
        public int CorrectionSnapCount { get; private set; }
        public float CorrectionAbsorbedMeters { get; private set; }
        public float CurrentCorrectionOffsetMeters => _correctionOffset.magnitude;

        public void Initialize(ClientSimulationState simState, Transform presentationRoot)
        {
            _simState = simState;
            _presentationRoot = presentationRoot;
            _correctionOffset = Vector3.zero;
            _smoothedRotation = presentationRoot.rotation;
            _initialized = true;
        }

        /// <summary>
        /// Called by LocalMovementPredictionDriver when a reconcile moves the
        /// predicted head state by <paramref name="displacement"/> (new minus
        /// old). Small: absorbed into the decaying offset so the on-screen
        /// pose stays continuous. Large: shown once (offset cleared).
        /// </summary>
        public void NotifyReconcileDisplacement(Vector3 displacement)
        {
            float magnitude = displacement.magnitude;
            if (magnitude <= 0.0f)
                return;

            if (magnitude >= CorrectionSnapThresholdMeters)
            {
                _correctionOffset = Vector3.zero;
                CorrectionSnapCount++;
                return;
            }

            _correctionOffset -= displacement;
            CorrectionAbsorbedMeters += magnitude;
            _correctionOffset = Vector3.ClampMagnitude(
                _correctionOffset,
                CorrectionSnapThresholdMeters);
        }

        private void LateUpdate()
        {
            if (_presentationRoot == null)
                return;

            Vector3 targetPosition = transform.position;
            Quaternion targetRotation = transform.rotation;

            if (!_initialized)
            {
                _correctionOffset = Vector3.zero;
                _smoothedRotation = targetRotation;
                _initialized = true;
            }

            if (_simState != null && _simState.TryGetSpecialMovementTrack(out _))
            {
                // Special movement is already sampled deterministically on the
                // simulation root; present it verbatim.
                _correctionOffset = Vector3.zero;
                _smoothedRotation = targetRotation;
                _presentationRoot.SetPositionAndRotation(targetPosition, targetRotation);
                return;
            }

            // Decay the correction offset at a capped rate.
            float offsetMagnitude = _correctionOffset.magnitude;
            if (offsetMagnitude > 0.0f)
            {
                float decayed = Mathf.Max(
                    0.0f,
                    offsetMagnitude - CorrectionDecayMetersPerSecond * Time.deltaTime);
                _correctionOffset = decayed <= 0.0001f
                    ? Vector3.zero
                    : _correctionOffset * (decayed / offsetMagnitude);
            }

            float rotationError = Quaternion.Angle(_smoothedRotation, targetRotation);
            _smoothedRotation = rotationError >= HardSnapAngleDegrees
                ? targetRotation
                : Quaternion.Slerp(
                    _smoothedRotation,
                    targetRotation,
                    SmoothingAlpha(RotationHalfLifeSeconds, Time.deltaTime));

            _presentationRoot.SetPositionAndRotation(
                targetPosition + _correctionOffset,
                _smoothedRotation);
        }

        private static float SmoothingAlpha(float halfLifeSeconds, float dt)
        {
            if (halfLifeSeconds <= 0.0001f)
                return 1.0f;

            return 1.0f - Mathf.Exp(-Mathf.Log(2.0f) * dt / halfLifeSeconds);
        }
    }
}
