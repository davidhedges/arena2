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
    /// Keeps the local visual hierarchy and camera anchor slightly smoothed
    /// behind the simulation root so tiny corrections do not read as jitter.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public sealed class LocalPresentationDriver : MonoBehaviour
    {
        private const float PositionHalfLifeSeconds = 0.06f;
        private const float RotationHalfLifeSeconds = 0.05f;
        private const float HardSnapDistance = 2.0f;
        private const float HardSnapAngleDegrees = 60.0f;

        private ClientSimulationState? _simState;
        private Transform? _presentationRoot;
        private Vector3 _smoothedPosition;
        private Quaternion _smoothedRotation;
        private bool _initialized;

        public void Initialize(ClientSimulationState simState, Transform presentationRoot)
        {
            _simState = simState;
            _presentationRoot = presentationRoot;
            _smoothedPosition = presentationRoot.position;
            _smoothedRotation = presentationRoot.rotation;
            _initialized = true;
        }

        private void LateUpdate()
        {
            if (_presentationRoot == null)
                return;

            Vector3 targetPosition = transform.position;
            Quaternion targetRotation = transform.rotation;

            if (!_initialized)
            {
                _smoothedPosition = targetPosition;
                _smoothedRotation = targetRotation;
                _initialized = true;
            }

            if (_simState != null && _simState.TryGetSpecialMovementTrack(out _))
            {
                // Special movement is already sampled deterministically on the simulation root.
                // Additional local presentation smoothing introduces a large steady-state lag,
                // which then trips the hard-snap threshold and reads as rubberbanding.
                _smoothedPosition = targetPosition;
                _smoothedRotation = targetRotation;
                _presentationRoot.SetPositionAndRotation(_smoothedPosition, _smoothedRotation);
                return;
            }

            float positionError = Vector3.Distance(_smoothedPosition, targetPosition);
            float rotationError = Quaternion.Angle(_smoothedRotation, targetRotation);
            if (positionError >= HardSnapDistance || rotationError >= HardSnapAngleDegrees)
            {
                _smoothedPosition = targetPosition;
                _smoothedRotation = targetRotation;
            }
            else
            {
                _smoothedPosition = Vector3.Lerp(
                    _smoothedPosition,
                    targetPosition,
                    SmoothingAlpha(PositionHalfLifeSeconds, Time.deltaTime));
                _smoothedRotation = Quaternion.Slerp(
                    _smoothedRotation,
                    targetRotation,
                    SmoothingAlpha(RotationHalfLifeSeconds, Time.deltaTime));
            }

            _presentationRoot.SetPositionAndRotation(_smoothedPosition, _smoothedRotation);
        }

        private static float SmoothingAlpha(float halfLifeSeconds, float dt)
        {
            if (halfLifeSeconds <= 0.0001f)
                return 1.0f;

            return 1.0f - Mathf.Exp(-Mathf.Log(2.0f) * dt / halfLifeSeconds);
        }
    }
}
