#nullable enable
using UnityEngine;
using Arena.Input;
using Unity.Cinemachine;

namespace Arena.Presentation
{
    /// <summary>
    /// Handles mouse-driven camera orbit rotation for the local player.
    /// Extracted from ThirdPersonController.CameraRotation().
    ///
    /// Rotates the CinemachineCameraTarget (PlayerCameraRoot) based on mouse input.
    /// Cinemachine follows this target for the third-person camera.
    ///
    /// INVARIANT: Only attached to the local player. One instance at a time.
    /// </summary>
    public class CameraOrbitController : MonoBehaviour
    {
        [Header("Cinemachine")]
        public float TopClamp = 70f;
        public float BottomClamp = -30f;
        public float CameraAngleOverride = 0f;
        public float CameraSensitivity = 3f;
        [Header("Zoom")]
        public float ZoomSensitivity = 0.75f;
        public float MinCameraDistance = 2.0f;
        public float MaxCameraDistance = 16.0f;
        [Header("Obstruction")]
        [Tooltip("World layers that can pull the camera closer to keep the character visible.")]
        public LayerMask CameraCollisionLayers = DefaultCameraCollisionMask;
        [Range(0.01f, 1f)]
        public float CameraCollisionRadius = 0.2f;
        [Min(0f)]
        [Tooltip("How gradually the camera returns to the player's selected distance after an obstruction clears.")]
        public float ObstructionReturnDamping = 0.35f;

        Transform? _cameraTarget;
        LocalPlayerInputSource? _input;
        LocalPlayerStateProvider? _stateProvider;
        CinemachineVirtualCameraBase? _camera;

        float _yaw;
        float _pitch;

        const float Threshold = 0.01f;
        const string PlayerTag = "Player";
        // ProjectSettings/TagManager.asset:
        // Default = 0, GameplayCollision = 6, GameplayQueryCollision = 7.
        const int DefaultCameraCollisionMask = (1 << 0) | (1 << 6) | (1 << 7);

        public void Initialize(
            Transform cameraTarget,
            LocalPlayerInputSource input,
            LocalPlayerStateProvider? stateProvider = null)
        {
            _cameraTarget = cameraTarget;
            _input = input;
            _stateProvider = stateProvider;
            _yaw = cameraTarget.rotation.eulerAngles.y;
            _pitch = 0f;
            RefreshCameraReference();
        }

        public void SetTarget(Transform newTarget)
        {
            _cameraTarget = newTarget;
            _yaw = newTarget.rotation.eulerAngles.y;
            _pitch = 0f;
            RefreshCameraReference();
        }

        public void AlignBehind(float facingYawRadians)
        {
            if (float.IsNaN(facingYawRadians) || float.IsInfinity(facingYawRadians))
                return;

            _yaw = ClampAngle(facingYawRadians * Mathf.Rad2Deg, float.MinValue, float.MaxValue);
            _stateProvider?.SetCameraYaw(_yaw * Mathf.Deg2Rad);

            if (_cameraTarget != null)
            {
                _cameraTarget.rotation = Quaternion.Euler(
                    _pitch + CameraAngleOverride,
                    _yaw,
                    0f);
            }
        }

        void LateUpdate()
        {
            if (_cameraTarget == null || _input == null) return;

            if (_camera == null)
                RefreshCameraReference();

            ApplyZoom(_input.ScrollDelta.y);

            if (_input.Look.sqrMagnitude >= Threshold)
            {
                _yaw += _input.Look.x * CameraSensitivity;
                _pitch += _input.Look.y * CameraSensitivity;
            }

            _yaw = ClampAngle(_yaw, float.MinValue, float.MaxValue);
            _pitch = ClampAngle(_pitch, BottomClamp, TopClamp);

            _stateProvider?.SetCameraYaw(_yaw * Mathf.Deg2Rad);

            _cameraTarget.rotation = Quaternion.Euler(
                _pitch + CameraAngleOverride, _yaw, 0f);
        }

        void RefreshCameraReference()
        {
            _camera = Object.FindAnyObjectByType<CinemachineVirtualCameraBase>();
            if (_camera != null)
                ConfigureObstructionHandling(_camera);
        }

        void ConfigureObstructionHandling(CinemachineVirtualCameraBase camera)
        {
            CinemachineThirdPersonFollow? thirdPersonFollow = null;
            if (camera is CinemachineCamera cinemachineCamera)
            {
                thirdPersonFollow = cinemachineCamera.GetCinemachineComponent(
                    CinemachineCore.Stage.Body) as CinemachineThirdPersonFollow;
            }

            thirdPersonFollow ??= camera.GetComponentInChildren<CinemachineThirdPersonFollow>(true);
            if (thirdPersonFollow != null)
            {
                var obstacleSettings = thirdPersonFollow.AvoidObstacles;
                obstacleSettings.Enabled = true;
                obstacleSettings.CollisionFilter = CameraCollisionLayers;
                obstacleSettings.IgnoreTag = PlayerTag;
                obstacleSettings.CameraRadius = Mathf.Max(0.01f, CameraCollisionRadius);
                obstacleSettings.DampingIntoCollision = 0f;
                obstacleSettings.DampingFromCollision = Mathf.Max(0f, ObstructionReturnDamping);
                thirdPersonFollow.AvoidObstacles = obstacleSettings;
                return;
            }

            // Open-world scenes still author the Cinemachine 2-compatible body.
            // Configure it by name so this controller remains compatible while
            // those scene rigs are migrated independently.
            var legacyThirdPersonFollow =
                FindComponentInChildrenByName(camera.gameObject, "Cinemachine3rdPersonFollow");
            if (legacyThirdPersonFollow == null)
                return;

            SetPublicField(legacyThirdPersonFollow, "CameraCollisionFilter", CameraCollisionLayers);
            SetPublicField(legacyThirdPersonFollow, "IgnoreTag", PlayerTag);
            SetPublicField(
                legacyThirdPersonFollow,
                "CameraRadius",
                Mathf.Max(0.01f, CameraCollisionRadius));
            SetPublicField(legacyThirdPersonFollow, "DampingIntoCollision", 0f);
            SetPublicField(
                legacyThirdPersonFollow,
                "DampingFromCollision",
                Mathf.Max(0f, ObstructionReturnDamping));
        }

        void ApplyZoom(float scrollY)
        {
            if (Mathf.Abs(scrollY) < Threshold)
                return;

            if (_camera == null)
                RefreshCameraReference();

            if (_camera == null)
                return;

            if (TrySetCinemachineCameraDistance(_camera, scrollY))
                return;

            var mainCamera = Camera.main;
            if (mainCamera == null || _cameraTarget == null)
                return;

            Vector3 offset = mainCamera.transform.position - _cameraTarget.position;
            float distance = offset.magnitude;
            if (distance <= 0.0001f)
                return;

            float nextDistance = Mathf.Clamp(
                distance - scrollY * ZoomSensitivity,
                MinCameraDistance,
                MaxCameraDistance);
            mainCamera.transform.position = _cameraTarget.position + offset.normalized * nextDistance;
        }

        bool TrySetCinemachineCameraDistance(CinemachineVirtualCameraBase camera, float scrollY)
        {
            float requestedDistanceDelta = scrollY * ZoomSensitivity;

            if (camera is CinemachineCamera cinemachineCamera)
            {
                if (cinemachineCamera.GetCinemachineComponent(CinemachineCore.Stage.Body) is CinemachineThirdPersonFollow thirdPersonFollow)
                {
                    thirdPersonFollow.CameraDistance = Mathf.Clamp(
                        thirdPersonFollow.CameraDistance - requestedDistanceDelta,
                        MinCameraDistance,
                        MaxCameraDistance);
                    return true;
                }
            }

            var thirdPersonFollowInChildren = camera.GetComponentInChildren<CinemachineThirdPersonFollow>(true);
            if (thirdPersonFollowInChildren != null)
            {
                thirdPersonFollowInChildren.CameraDistance = Mathf.Clamp(
                    thirdPersonFollowInChildren.CameraDistance - requestedDistanceDelta,
                    MinCameraDistance,
                    MaxCameraDistance);
                return true;
            }

            var thirdPersonFollowComponent = FindComponentInChildrenByName(camera.gameObject, "Cinemachine3rdPersonFollow");
            if (thirdPersonFollowComponent != null)
            {
                var cameraDistanceProperty = thirdPersonFollowComponent.GetType().GetField("CameraDistance");
                if (cameraDistanceProperty != null)
                {
                    float currentDistance = (float)cameraDistanceProperty.GetValue(thirdPersonFollowComponent)!;
                    cameraDistanceProperty.SetValue(
                        thirdPersonFollowComponent,
                        Mathf.Clamp(
                            currentDistance - requestedDistanceDelta,
                            MinCameraDistance,
                            MaxCameraDistance));
                    return true;
                }
            }

            var framingTransposer = FindComponentInChildrenByName(camera.gameObject, "CinemachineFramingTransposer");
            if (framingTransposer != null)
            {
                var cameraDistanceField = framingTransposer.GetType().GetField("m_CameraDistance");
                if (cameraDistanceField != null)
                {
                    float currentDistance = (float)cameraDistanceField.GetValue(framingTransposer)!;
                    cameraDistanceField.SetValue(
                        framingTransposer,
                        Mathf.Clamp(
                            currentDistance - requestedDistanceDelta,
                            MinCameraDistance,
                            MaxCameraDistance));
                    return true;
                }
            }

            return false;
        }

        static void SetPublicField(Component component, string fieldName, object value)
        {
            component.GetType().GetField(fieldName)?.SetValue(component, value);
        }

        static Component? FindComponentInChildrenByName(GameObject root, string typeName)
        {
            foreach (var component in root.GetComponentsInChildren<Component>(true))
            {
                if (component != null && component.GetType().Name == typeName)
                    return component;
            }

            return null;
        }

        static float ClampAngle(float angle, float min, float max)
        {
            if (angle < -360f) angle += 360f;
            if (angle > 360f) angle -= 360f;
            return Mathf.Clamp(angle, min, max);
        }
    }
}
