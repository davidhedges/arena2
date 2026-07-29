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

        Transform? _cameraTarget;
        LocalPlayerInputSource? _input;
        LocalPlayerStateProvider? _stateProvider;
        CinemachineVirtualCameraBase? _camera;

        float _yaw;
        float _pitch;

        const float Threshold = 0.01f;

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
