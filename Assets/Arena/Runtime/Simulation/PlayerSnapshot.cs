using UnityEngine;

namespace Arena.Simulation
{
    /// <summary>
    /// Immutable snapshot of server-authoritative physics state for one player.
    /// Created from a PlayerPhysics row; stored in ClientSimulationState.
    /// </summary>
    public readonly struct PlayerSnapshot
    {
        public readonly Vector3 Position;
        public readonly Vector3 Velocity;
        public readonly float Yaw;          // radians, server convention
        public readonly bool Grounded;
        public readonly uint LastProcessedTick;
        public readonly float ReceivedTime; // Time.realtimeSinceStartup when stored

        public PlayerSnapshot(
            float posX,
            float posY,
            float posZ,
            float velX,
            float velY,
            float velZ,
            float yaw,
            bool grounded,
            uint lastProcessedTick)
            : this(posX, posY, posZ, velX, velY, velZ, yaw, grounded, lastProcessedTick,
                   Time.realtimeSinceStartup)
        {
        }

        /// <summary>Explicit receive time, for callers outside the Unity player loop (tests).</summary>
        public PlayerSnapshot(
            float posX,
            float posY,
            float posZ,
            float velX,
            float velY,
            float velZ,
            float yaw,
            bool grounded,
            uint lastProcessedTick,
            float receivedTime)
        {
            Position = new Vector3(posX, posY, posZ);
            Velocity = new Vector3(velX, velY, velZ);
            Yaw = yaw;
            Grounded = grounded;
            LastProcessedTick = lastProcessedTick;
            ReceivedTime = receivedTime;
        }
    }
}
