#nullable enable
using UnityEngine;

namespace Arena.Input
{
    /// <summary>
    /// TrainingGround uses authored scene geometry only.
    /// This disables the shared arena/open-world blocker set so melee testing happens on a clean flat lane.
    /// </summary>
    public sealed class TrainingGroundMovementEnvironment : IMovementEnvironment
    {
        private const float GroundY = 0.0f;

        public static TrainingGroundMovementEnvironment Shared { get; } = new();

        private TrainingGroundMovementEnvironment() { }

        public float SampleGroundHeight(float x, float z, float probeY)
        {
            return GroundY;
        }

        public Vector2 ResolveHorizontalCollision(
            float startX,
            float startZ,
            float desiredX,
            float desiredZ,
            float playerRadius,
            float playerHeight,
            float currentY)
        {
            return ActiveWorldObstacleRuntime.ResolveHorizontalCollision(
                startX,
                startZ,
                desiredX,
                desiredZ,
                playerRadius,
                playerHeight,
                currentY);
        }

        public Vector2 ResolveHorizontalCollision(
            float desiredX,
            float desiredZ,
            float playerRadius,
            float playerHeight,
            float currentY)
        {
            return ResolveHorizontalCollision(desiredX, desiredZ, desiredX, desiredZ, playerRadius, playerHeight, currentY);
        }
    }
}
