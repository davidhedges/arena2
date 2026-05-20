#nullable enable
using UnityEngine;

namespace Arena.Input
{
    /// <summary>
    /// Raw movement command used by local prediction and authoritative replay.
    /// This mirrors the server-side intent contract.
    /// </summary>
    public readonly struct MovementCommand
    {
        public MovementCommand(uint inputTick, float forward, float strafe, float facingYaw, bool jumpPressed)
        {
            InputTick = inputTick;
            Forward = forward;
            Strafe = strafe;
            FacingYaw = facingYaw;
            JumpPressed = jumpPressed;
        }

        public uint InputTick { get; }
        public float Forward { get; }
        public float Strafe { get; }
        public float FacingYaw { get; }
        public bool JumpPressed { get; }
    }

    /// <summary>
    /// Explicit movement state required to continue deterministic simulation.
    /// </summary>
    public readonly struct PredictedMovementState
    {
        public PredictedMovementState(
            Vector3 position,
            Vector3 velocity,
            float facingYaw,
            bool grounded,
            uint lastProcessedTick)
        {
            Position = position;
            Velocity = velocity;
            FacingYaw = facingYaw;
            Grounded = grounded;
            LastProcessedTick = lastProcessedTick;
        }

        public Vector3 Position { get; }
        public Vector3 Velocity { get; }
        public float FacingYaw { get; }
        public bool Grounded { get; }
        public uint LastProcessedTick { get; }
    }

    /// <summary>
    /// Runtime inputs that affect the movement step but are not part of the raw
    /// client command payload.
    /// </summary>
    public readonly struct MovementStepContext
    {
        public MovementStepContext(bool movementBlocked, float moveSpeedMultiplier, float hitRadius, float hitHeight)
        {
            MovementBlocked = movementBlocked;
            MoveSpeedMultiplier = moveSpeedMultiplier;
            HitRadius = hitRadius;
            HitHeight = hitHeight;
        }

        public bool MovementBlocked { get; }
        public float MoveSpeedMultiplier { get; }
        public float HitRadius { get; }
        public float HitHeight { get; }
    }

    /// <summary>
    /// Environment queries used by prediction. Implementations must match the
    /// authoritative server movement environment closely enough for replay to remain stable.
    /// </summary>
    public interface IMovementEnvironment
    {
        float SampleGroundHeight(float x, float z, float probeY);
        Vector2 ResolveHorizontalCollision(float desiredX, float desiredZ, float playerRadius, float playerHeight, float currentY);
    }

    /// <summary>
    /// Pure movement step mirroring the server kinematic simulation.
    /// The caller owns tick scheduling and replay order.
    /// </summary>
    public static class MovementPrediction
    {
        public const float ZeroInputEpsilon = 0.0001f;
        public const float DefaultHitRadius = 0.28f;
        public const float DefaultHitHeight = 1.8f;
        public const float MoveSpeed = 7.0f;
        public const float JumpVelocity = 8.0f;
        public const float Gravity = -25.0f;
        public const float MaxGroundedSnapDown = 0.75f;
        private const float MaxHorizontalCollisionStep = 0.10f;

        public static PredictedMovementState Step(
            in PredictedMovementState oldState,
            in MovementCommand command,
            in MovementStepContext context,
            IMovementEnvironment environment,
            float dt)
        {
            Vector3 position = oldState.Position;
            Vector3 velocity = oldState.Velocity;
            float facingYaw = command.FacingYaw;
            bool grounded = oldState.Grounded;

            float playerRadius = Mathf.Max(context.HitRadius, 0.1f);
            float playerHeight = Mathf.Max(context.HitHeight, 0.5f);

            if (grounded)
            {
                // Pre-step stabilization matches the server's snap to sampled ground.
                position.y = environment.SampleGroundHeight(position.x, position.z, position.y);
            }

            if (grounded)
            {
                if (!context.MovementBlocked)
                {
                    float effectiveMultiplier = context.MoveSpeedMultiplier;

                    Vector2 horizontalVelocity = VelocityFromIntent(
                        command.Forward,
                        command.Strafe,
                        command.FacingYaw,
                        MoveSpeed * effectiveMultiplier);
                    velocity.x = horizontalVelocity.x;
                    velocity.z = horizontalVelocity.y;
                }

                velocity.y = 0.0f;

                if (command.JumpPressed && !context.MovementBlocked)
                {
                    grounded = false;
                    velocity.y = JumpVelocity;
                }
            }
            else
            {
                // Airborne horizontal velocity remains preserved.
                velocity.y += Gravity * dt;
            }

            if (context.MovementBlocked)
            {
                velocity.x = 0.0f;
                velocity.z = 0.0f;
            }

            Vector2 resolvedHorizontal = ResolveHorizontalMotion(
                environment,
                position.x,
                position.z,
                velocity.x * dt,
                velocity.z * dt,
                playerRadius,
                playerHeight,
                position.y);

            position.x = resolvedHorizontal.x;
            position.z = resolvedHorizontal.y;

            if (grounded)
            {
                float groundY = environment.SampleGroundHeight(position.x, position.z, position.y);
                float dropToGround = position.y - groundY;
                if (dropToGround <= MaxGroundedSnapDown)
                {
                    position.y = groundY;
                }
                else
                {
                    grounded = false;
                    velocity.y = Gravity * dt;
                    position.y += velocity.y * dt;
                }
            }
            else
            {
                float previousY = position.y;
                position.y += velocity.y * dt;
                float groundY = environment.SampleGroundHeight(position.x, position.z, previousY);
                if (position.y <= groundY && velocity.y <= 0.0f)
                {
                    position.y = groundY;
                    velocity.y = 0.0f;
                    grounded = true;
                }
            }

            return new PredictedMovementState(
                position,
                velocity,
                facingYaw,
                grounded,
                command.InputTick);
        }

        public static Vector2 VelocityFromIntent(float forward, float strafe, float yaw, float speed)
        {
            if (Mathf.Abs(forward) <= ZeroInputEpsilon && Mathf.Abs(strafe) <= ZeroInputEpsilon)
                return Vector2.zero;

            float sinYaw = Mathf.Sin(yaw);
            float cosYaw = Mathf.Cos(yaw);
            float dirX = forward * sinYaw + strafe * cosYaw;
            float dirZ = forward * cosYaw - strafe * sinYaw;

            Vector2 velocity = new(dirX, dirZ);
            if (velocity.sqrMagnitude > 1.0f)
                velocity.Normalize();

            return velocity * speed;
        }

        private static Vector2 ResolveHorizontalMotion(
            IMovementEnvironment environment,
            float startX,
            float startZ,
            float deltaX,
            float deltaZ,
            float playerRadius,
            float playerHeight,
            float currentY)
        {
            float maxDelta = Mathf.Max(Mathf.Abs(deltaX), Mathf.Abs(deltaZ));
            int steps = Mathf.Max(1, Mathf.CeilToInt(maxDelta / MaxHorizontalCollisionStep));
            float stepX = deltaX / steps;
            float stepZ = deltaZ / steps;
            float x = startX;
            float z = startZ;

            for (int i = 0; i < steps; i++)
            {
                Vector2 resolved = environment.ResolveHorizontalCollision(
                    x + stepX,
                    z + stepZ,
                    playerRadius,
                    playerHeight,
                    currentY);
                x = resolved.x;
                z = resolved.y;
            }

            return new Vector2(x, z);
        }
    }
}
