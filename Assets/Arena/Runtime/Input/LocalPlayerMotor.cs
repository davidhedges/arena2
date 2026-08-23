#nullable enable
using System;
using Arena.Combat;
using Arena.Network;
using Arena.Simulation;
using Arena.Entity;
using UnityEngine;

namespace Arena.Input
{
    /// <summary>
    /// Samples local movement intent and jump edges for the predicted local
    /// player. Transform authority lives in LocalMovementPredictionDriver.
    /// </summary>
    public class LocalPlayerMotor : MonoBehaviour
    {
        public readonly struct IntentSample
        {
            public IntentSample(float forward, float strafe, float yaw, bool jump)
            {
                Forward = forward;
                Strafe = strafe;
                Yaw = yaw;
                Jump = jump;
            }

            public float Forward { get; }
            public float Strafe { get; }
            public float Yaw { get; }
            public bool Jump { get; }
        }

        private LocalPlayerInputSource? _input;
        private LocalPlayerStateProvider? _stateProvider;
        private bool _predictedAuthority;
        private float _hitRadius = MovementPrediction.DefaultHitRadius;
        private float _hitHeight = MovementPrediction.DefaultHitHeight;

        private bool _grounded;
        private bool _jumpQueuedLocal;
        private float _intentForward;
        private float _intentStrafe;
        private float _intentYaw;
        private bool _keyboardTurningActive;
        private bool _cameraAlignActive;
        private bool _hasImposedFacing;

        private const float TurnInputThreshold = 0.1f;
        private const float LookTurnInputThreshold = 0.01f;
        private const float KeyboardTurnSpeedDegreesPerSecond = 120f;
        private const float MovingKeyboardTurnSpeedDegreesPerSecond = 72f;
        private const float CameraAlignTurnSpeedDegreesPerSecond = 360f;

        public bool IsGrounded => _grounded;
        public Vector3 Position => transform.position;
        public float CurrentIntentForward => _intentForward;
        public float CurrentIntentStrafe => _intentStrafe;
        public float CurrentIntentYaw => _intentYaw;
        public bool IsFacingUnderDirectInputControl => _keyboardTurningActive || _cameraAlignActive;
        public bool UsesDirectionalLocomotion => _cameraAlignActive || (_stateProvider != null && _stateProvider.HasAimYawOverride);
        public float HitRadius => _hitRadius;
        public float HitHeight => _hitHeight;

        public void Initialize(LocalPlayerInputSource input, float hitRadius, float hitHeight)
        {
            _input = input;
            SetHitCapsule(hitRadius, hitHeight);
            _intentYaw = transform.eulerAngles.y * Mathf.Deg2Rad;
        }

        public void SetHitCapsule(float hitRadius, float hitHeight)
        {
            _hitRadius = Mathf.Max(hitRadius, 0.1f);
            _hitHeight = Mathf.Max(hitHeight, _hitRadius * 2.0f);
        }

        public float GetMovementYaw()
        {
            return _intentYaw;
        }

        public void FaceYawImmediately(float yawRadians)
        {
            if (float.IsNaN(yawRadians) || float.IsInfinity(yawRadians))
                return;

            _intentYaw = NormalizeRadians(yawRadians);
        }

        /// <summary>
        /// Adopts an ability-owned facing and HOLDS it until the player asks to
        /// turn. Used at a special-movement boundary, where the server owns the
        /// yaw: without the hold the next sampled intent immediately overwrites
        /// the arrival facing with the camera yaw (camera-align mode) or with a
        /// pre-movement yaw the keyboard turn branch is still accumulating
        /// from, which is why a gap closer only sometimes landed facing its
        /// target. The hold releases on the first real turn input, so it never
        /// fights the player for control.
        /// </summary>
        public void ImposeFacingYaw(float yawRadians)
        {
            if (float.IsNaN(yawRadians) || float.IsInfinity(yawRadians))
                return;

            _intentYaw = NormalizeRadians(yawRadians);
            _hasImposedFacing = true;
        }

        public void EnablePredictedAuthority(LocalPlayerStateProvider stateProvider)
        {
            _stateProvider = stateProvider;
            _predictedAuthority = true;
            _hasImposedFacing = false;
            _intentYaw = CurrentFacingYaw;
        }

        // Drops the rate-matching jump-press latch held between Update
        // (frame rate) and SampleIntentForPredictionTick (fixed tick rate).
        // Any context that needs to dispose of buffered input (special
        // movement exit, hard-CC start, etc.) calls this to prevent a
        // pre-window press from leaking into the next sampled command.
        public void ClearBufferedJumpInput()
        {
            _jumpQueuedLocal = false;
        }

        public IntentSample SampleIntentForPredictionTick()
        {
            bool jump = _grounded && _jumpQueuedLocal;
            if (jump)
            {
                _jumpQueuedLocal = false;
                _grounded = false;
            }

            return new IntentSample(_intentForward, _intentStrafe, _intentYaw, jump);
        }

        private void Update()
        {
            if (_input == null) return;

            UpdateStaticYaw();
            CaptureJumpInput();
            if (!_predictedAuthority)
                return;

            UpdateIntent();
            SuppressPredictedCastBarOnMovementIntent();
            SyncFromPredictedState();
        }

        private void UpdateStaticYaw()
        {
            if (_stateProvider == null) return;

            var mainCam = Camera.main;
            if (mainCam != null && !_stateProvider.HasCameraYaw)
                _stateProvider.SetCameraYaw(mainCam.transform.eulerAngles.y * Mathf.Deg2Rad);
        }

        private void CaptureJumpInput()
        {
            if (_input != null && _input.JumpPressed)
            {
                if (_grounded)
                    _jumpQueuedLocal = true;
            }
        }

        private void SyncFromPredictedState()
        {
            if (_stateProvider == null || !_stateProvider.HasPredictedState)
                return;

            _grounded = _stateProvider.PredictedGrounded;
        }

        private void UpdateIntent()
        {
            if (_input == null)
            {
                _intentForward = 0.0f;
                _intentStrafe = 0.0f;
                _keyboardTurningActive = false;
                _cameraAlignActive = false;
                SetIntentYaw(CurrentFacingYaw);
                return;
            }

            Vector2 move = Vector2.ClampMagnitude(_input.Move, 1.0f);
            Vector2 keyboardTurnAxes = ResolveKeyboardTurnAxes(_input.RawMove);
            _keyboardTurningActive = false;
            _cameraAlignActive = false;
            ReleaseImposedFacingOnTurnInput(keyboardTurnAxes);

            if (_stateProvider != null && _stateProvider.HasAimYawOverride)
            {
                _intentForward = move.y;
                _intentStrafe = move.x;
                SetIntentYaw(CurrentFacingYaw);
                return;
            }

            if (_input.RightMouseHeld)
            {
                _intentForward = move.y;
                _intentStrafe = move.x;
                _cameraAlignActive = true;
                SetIntentYaw(CurrentCameraYaw);
                return;
            }

            if (move.sqrMagnitude <= 0.0001f)
            {
                _intentForward = 0.0f;
                _intentStrafe = 0.0f;
                SetIntentYaw(CurrentFacingYaw);
                return;
            }

            // In keyboard-facing mode A/D turns rather than strafes. Preserve the raw W/S
            // axis so W+A/W+D remains full-speed forward movement; using the normalized
            // directional vector here would reduce W from 1 to sqrt(0.5) before discarding
            // the lateral component.
            _intentForward = keyboardTurnAxes.y;
            _intentStrafe = 0.0f;

            if (Mathf.Abs(keyboardTurnAxes.x) > TurnInputThreshold)
            {
                _keyboardTurningActive = true;
                float turnSpeedDegreesPerSecond = Mathf.Abs(keyboardTurnAxes.y) > TurnInputThreshold
                    ? MovingKeyboardTurnSpeedDegreesPerSecond
                    : KeyboardTurnSpeedDegreesPerSecond;
                float yawDelta = keyboardTurnAxes.x * turnSpeedDegreesPerSecond * Mathf.Deg2Rad * Time.deltaTime;
                _intentYaw = NormalizeRadians(_intentYaw + yawDelta);
            }
            else
            {
                SetIntentYaw(CurrentFacingYaw);
            }
        }

        private void SetIntentYaw(float yaw)
        {
            if (_hasImposedFacing)
                return;

            _intentYaw = yaw;
        }

        private void ReleaseImposedFacingOnTurnInput(Vector2 keyboardTurnAxes)
        {
            if (!_hasImposedFacing || _input == null)
                return;

            bool aiming = _stateProvider != null && _stateProvider.HasAimYawOverride;
            if (aiming
                || _input.Look.sqrMagnitude >= LookTurnInputThreshold
                || Mathf.Abs(keyboardTurnAxes.x) > TurnInputThreshold)
            {
                _hasImposedFacing = false;
            }
        }

        private static Vector2 ResolveKeyboardTurnAxes(Vector2 rawMove)
        {
            return new Vector2(
                Mathf.Clamp(rawMove.x, -1f, 1f),
                Mathf.Clamp(rawMove.y, -1f, 1f));
        }

        private void SuppressPredictedCastBarOnMovementIntent()
        {
            if (_input == null)
                return;

            if (_input.Move.sqrMagnitude <= 0.0001f && !_input.JumpPressed)
                return;

            var conn = NetworkManager.Instance?.Conn;
            if (conn == null)
                return;

            CastCancelSnapshot? cancel = LocalCombatState.Instance.SuppressCastBarForLocalInterrupt();
            if (!cancel.HasValue)
                return;

            EntityRegistry.Instance?.LocalPlayerEntity?.CancelLocalSpellCastHold(cancel.Value.Token);
            if (ShouldClearPredictedGcdForSelfCancel(conn, cancel.Value))
                LocalCombatState.Instance.ClearPredictedGlobalCooldown();

            conn.Reducers.CancelActiveCastRequest(
                cancel.Value.Token.PredictedCastId,
                cancel.Value.Token.ClientActionSeq,
                _input.JumpPressed ? "jump" : "movement",
                cancel.Value.ObservedRemainingMs);
        }

        private static bool ShouldClearPredictedGcdForSelfCancel(
            SpacetimeDB.Types.DbConnection conn,
            CastCancelSnapshot cancel)
        {
            string actionId = WireIdentifier.Normalize(cancel.Token.Kind);
            if (string.IsNullOrWhiteSpace(actionId) || IsMovementDeliveryAction(conn, actionId))
                return false;

            SpacetimeDB.Types.SpellDefinition? definition = conn.Db.SpellDefinition.Kind.Find(actionId);
            if (definition == null || definition.CastTimeMs == 0UL)
                return false;

            return LocalCombatState.ShouldClearPredictedGlobalCooldownForSelfCancel(
                definition.CastTimeMs,
                definition.Behavior,
                isMovementDeliveryAction: false);
        }

        private static bool IsMovementDeliveryAction(SpacetimeDB.Types.DbConnection conn, string actionId)
        {
            foreach (SpacetimeDB.Types.AbilityCatalog ability in conn.Db.AbilityCatalog.Iter())
            {
                if (string.Equals(WireIdentifier.Normalize(ability.ActionId), actionId, StringComparison.Ordinal)
                    && string.Equals(WireIdentifier.Normalize(ability.AbilityKind), AbilityKinds.Movement, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        private static float NormalizeRadians(float angle)
        {
            return Mathf.Repeat(angle + Mathf.PI, Mathf.PI * 2.0f) - Mathf.PI;
        }

        private float CurrentFacingYaw
        {
            get
            {
                if (_stateProvider != null)
                {
                    if (_stateProvider.HasAimYawOverride)
                        return _stateProvider.AimYaw;
                    if (_stateProvider.HasPredictedState)
                        return _stateProvider.PredictedFacingYaw;
                }

                return transform.eulerAngles.y * Mathf.Deg2Rad;
            }
        }

        private float CurrentCameraYaw
        {
            get
            {
                if (_stateProvider != null && _stateProvider.HasCameraYaw)
                    return _stateProvider.CameraYaw;

                var mainCam = Camera.main;
                return mainCam != null
                    ? mainCam.transform.eulerAngles.y * Mathf.Deg2Rad
                    : transform.eulerAngles.y * Mathf.Deg2Rad;
            }
        }
    }
}
