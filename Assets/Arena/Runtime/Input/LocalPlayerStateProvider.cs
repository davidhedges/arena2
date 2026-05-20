#nullable enable
using UnityEngine;

namespace Arena.Input
{
    /// <summary>
    /// Instance-scoped local player state used by future camera, spell, and
    /// presentation systems during the movement migration.
    /// </summary>
    public sealed class LocalPlayerStateProvider : MonoBehaviour
    {
        private PredictedMovementState _predictedState;
        private bool _hasPredictedState;
        private float _cameraYaw;
        private bool _hasCameraYaw;
        private bool _aimYawOverridden;
        private float _aimYaw;

        public bool HasPredictedState => _hasPredictedState;
        public Vector3 PredictedPosition => _predictedState.Position;
        public Vector3 PredictedVelocity => _predictedState.Velocity;
        public float PredictedFacingYaw => _predictedState.FacingYaw;
        public bool PredictedGrounded => _predictedState.Grounded;
        public uint LastProcessedTick => _predictedState.LastProcessedTick;
        public bool HasCameraYaw => _hasCameraYaw;
        public float CameraYaw => _cameraYaw;
        public bool HasAimYawOverride => _aimYawOverridden;
        public float AimYaw => _aimYaw;
        public float MovementFacingYaw => _aimYawOverridden ? _aimYaw : _predictedState.FacingYaw;

        public void SetPredictedState(in PredictedMovementState state)
        {
            _predictedState = state;
            _hasPredictedState = true;
        }

        public void ClearPredictedState()
        {
            _predictedState = default;
            _hasPredictedState = false;
        }

        public void SetCameraYaw(float yawRadians)
        {
            _cameraYaw = yawRadians;
            _hasCameraYaw = true;
        }

        public void SetAimYaw(float yawRadians)
        {
            _aimYaw = yawRadians;
            _aimYawOverridden = true;
        }

        public void ClearAimYaw()
        {
            _aimYawOverridden = false;
        }
    }
}
