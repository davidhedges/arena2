#nullable enable
using UnityEngine;
using Arena.Input;

namespace Arena.Presentation
{
    /// <summary>
    /// Presentation-only sampling of a jump. Never changes movement state or input.
    /// </summary>
    public static class JumpAnimationPrediction
    {
        public const float LookAheadSeconds = 0.4f;

        public static PredictedMovementState Interpolate(
            in PredictedMovementState older, in PredictedMovementState newer, float alpha)
        {
            float t = Mathf.Clamp01(alpha);
            bool grounded = newer.Grounded && (older.Grounded || t >= 1f);
            Vector3 velocity = Vector3.Lerp(older.Velocity, newer.Velocity, t);
            // The jump impulse is instantaneous; interpolating it from zero
            // invents an apex at the beginning of takeoff. At touchdown the
            // root is still descending until interpolation reaches the floor.
            if (older.Grounded && !newer.Grounded)
                velocity.y = newer.Velocity.y;
            else if (!older.Grounded && newer.Grounded && !grounded)
                velocity.y = (newer.Position.y - older.Position.y) / MovementNetcodeConfig.FixedTickSeconds;
            return new PredictedMovementState(
                Vector3.Lerp(older.Position, newer.Position, t), velocity,
                Mathf.LerpAngle(older.FacingYaw * Mathf.Rad2Deg, newer.FacingYaw * Mathf.Rad2Deg, t) * Mathf.Deg2Rad,
                grounded, newer.LastProcessedTick);
        }

        public static bool TryFindLanding(
            in PredictedMovementState state, in MovementStepContext context,
            IMovementEnvironment environment, float horizonSeconds, out float seconds)
        {
            seconds = 0f;
            if (state.Grounded) return true;
            if (state.Velocity.y > 0f || horizonSeconds <= 0f) return false;
            var projected = state;
            float dt = MovementNetcodeConfig.FixedTickSeconds;
            int steps = Mathf.CeilToInt(Mathf.Min(horizonSeconds, LookAheadSeconds) / dt);
            var command = new MovementCommand(state.LastProcessedTick, 0f, 0f, state.FacingYaw, false);
            for (int i = 0; i < steps; i++)
            {
                // Use the real terrain, ledge and wall solver. In particular,
                // do not project onto the height at the character's current X/Z.
                projected = MovementPrediction.Step(projected, command, context, environment, dt);
                if (!projected.Grounded) continue;
                seconds = (i + 1) * dt;
                return seconds <= horizonSeconds;
            }
            return false;
        }
    }

    /// <summary>
    /// Motion-time parameters for existing takeoff and landing clips. The
    /// anticipation approaches the authored contact pose; only real contact
    /// releases the compression/recovery portion of the landing animation.
    /// </summary>
    public sealed class JumpAnimationPlayback
    {
        public const float BlendSeconds = 0.08f;
        // A normal jump reaches the floor within this forecast window at its
        // apex. Longer drops keep the sustained falling animation.
        private const float ShortJumpLandingSeconds = JumpAnimationPrediction.LookAheadSeconds;
        private const float ContactHoldSeconds = 0.012f;
        private float _takeoffVelocity;
        private bool _wasGrounded = true;
        private bool _jumped;
        private bool _hasLanded;
        private float _recoverySeconds;
        private float _landingApproachSeconds;

        public float JumpPhase { get; private set; }
        public float LandingPhase { get; private set; }
        public bool IsPreparingLanding { get; private set; }
        public bool IsFalling { get; private set; }
        public bool JumpStarted { get; private set; }
        public bool LandingStarted { get; private set; }
        public bool LandingCancelled { get; private set; }
        public bool CanRecoverWhileMoving => _hasLanded && _recoverySeconds >= 0.12f;

        public void Reset(bool grounded)
        {
            _wasGrounded = grounded;
            _jumped = false;
            _hasLanded = false;
            _takeoffVelocity = 0f;
            _recoverySeconds = 0f;
            _landingApproachSeconds = 0f;
            JumpPhase = LandingPhase = 0f;
            IsFalling = IsPreparingLanding = JumpStarted = LandingStarted = LandingCancelled = false;
        }

        public void Tick(bool grounded, float verticalVelocity, float? landingInSeconds,
            float landingClipSeconds, float contactSeconds, float dt)
        {
            dt = Mathf.Max(0f, dt);
            float length = Mathf.Max(landingClipSeconds, 0.001f);
            float contact = Mathf.Clamp(contactSeconds, 0f, length);
            bool leftGround = _wasGrounded && !grounded;
            JumpStarted = leftGround && verticalVelocity > 0.1f;
            LandingStarted = LandingCancelled = false;

            if (leftGround)
            {
                _jumped = JumpStarted;
                IsFalling = false;
                _hasLanded = false;
                _recoverySeconds = 0f;
                // Preserve the outgoing landing pose while a re-jump blends
                // away from it; the next approach will reset its own phase.
                JumpPhase = 0f;
                IsPreparingLanding = false;
                _takeoffVelocity = Mathf.Max(0.1f, verticalVelocity);
            }

            if (!grounded)
            {
                _hasLanded = false;
                _takeoffVelocity = Mathf.Max(_takeoffVelocity, verticalVelocity);
                JumpPhase = Mathf.Clamp(Mathf.Max(JumpPhase,
                    1f - verticalVelocity / Mathf.Max(0.1f, _takeoffVelocity)), 0f, 0.999f);

                bool descending = verticalVelocity <= 0f;
                bool shortJump = _jumped && !IsFalling && landingInSeconds.HasValue
                    && landingInSeconds.Value <= ShortJumpLandingSeconds;
                // Once a drop needs the falling pose, keep that classification
                // until contact. Its eventual approach uses normal fall-to-land
                // timing instead of restarting a short jump near the floor.
                if (descending && !shortJump) IsFalling = true;
                bool approaching = descending && landingInSeconds.HasValue
                    && (shortJump || landingInSeconds.Value <= contact + BlendSeconds);
                if (approaching)
                {
                    LandingStarted = !IsPreparingLanding;
                    if (LandingStarted)
                    {
                        LandingPhase = 0f;
                        _landingApproachSeconds = shortJump ? Mathf.Max(0.001f, landingInSeconds!.Value) : 0f;
                    }
                    IsPreparingLanding = true;
                    float beforeContact = Mathf.Max(0f, contact - ContactHoldSeconds) / length;
                    float remaining = Mathf.Max(0f, landingInSeconds!.Value);
                    // Short jumps blend straight out of takeoff at the apex.
                    // Fit the authored pre-contact motion to that descent so
                    // skipping InAir doesn't simply freeze the apex pose.
                    float approachTime = _landingApproachSeconds > 0f
                        ? contact * (1f - remaining / _landingApproachSeconds) : contact - remaining;
                    float desired = Mathf.Clamp(approachTime / length, 0f, beforeContact);
                    // Small estimate changes may pause the approach, but must
                    // never scrub a visible pose backwards or plant in the air.
                    LandingPhase = Mathf.Min(beforeContact, Mathf.Max(LandingPhase, desired));
                }
                else if (IsPreparingLanding)
                {
                    // The destination disappeared (e.g. travelling beyond a
                    // ledge). Return to falling instead of holding a landing pose.
                    LandingCancelled = true;
                    IsPreparingLanding = false;
                }
            }
            else if (!_wasGrounded)
            {
                LandingStarted = !IsPreparingLanding;
                IsPreparingLanding = true;
                _hasLanded = true;
                _recoverySeconds = 0f;
                LandingPhase = Mathf.Clamp(contact / length, 0f, 0.999f);
            }
            else if (_hasLanded)
            {
                _recoverySeconds += dt;
                LandingPhase = Mathf.Min(0.999f, (contact + _recoverySeconds) / length);
            }
            if (grounded) IsFalling = false;
            _wasGrounded = grounded;
        }
    }
}
