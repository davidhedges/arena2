#nullable enable
using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Arena.EditModeTests
{
    /// <summary>
    /// Feel-audit F3: pure-math coverage of RemotePresentationBuffer, the
    /// snapshot ring + sample/smooth/snap core extracted from
    /// ClientSimulationState and shared by remote players and NPCs. Time is
    /// caller-supplied, so every case runs deterministically off the Unity
    /// player loop (PlayerSnapshot's explicit-receivedTime constructor).
    /// </summary>
    public class RemotePresentationBufferTests
    {
        private const string BufferTypeName = "Arena.Simulation.RemotePresentationBuffer";
        private const string SnapshotTypeName = "Arena.Simulation.PlayerSnapshot";
        private static readonly Assembly RuntimeAssembly = AppDomain.CurrentDomain.Load("Assembly-CSharp");
        private static readonly Type BufferType = RuntimeAssembly.GetType(BufferTypeName)
            ?? throw new InvalidOperationException($"Missing runtime type {BufferTypeName}");
        private static readonly Type SnapshotType = RuntimeAssembly.GetType(SnapshotTypeName)
            ?? throw new InvalidOperationException($"Missing runtime type {SnapshotTypeName}");

        [Test]
        public void Sample_BetweenTwoSnapshots_InterpolatesLinearly()
        {
            object buffer = CreateBuffer();
            Push(buffer, Snapshot(0f, 0f, 0f, velX: 0f, yaw: 0f, receivedTime: 10.0f));
            Push(buffer, Snapshot(2f, 0f, 4f, velX: 0f, yaw: 0f, receivedTime: 10.1f));

            (Vector3 position, float yaw, string mode) = Sample(buffer, renderTime: 10.05f);

            Assert.That(mode, Is.EqualTo("Interpolation"));
            Assert.That(position.x, Is.EqualTo(1f).Within(1e-4f));
            Assert.That(position.y, Is.EqualTo(0f).Within(1e-4f));
            Assert.That(position.z, Is.EqualTo(2f).Within(1e-4f));
            Assert.That(yaw, Is.EqualTo(0f).Within(1e-4f));
            Assert.That(GetProp<float>(buffer, "LastExtrapolationSeconds"), Is.EqualTo(0f));
        }

        [Test]
        public void Sample_PastLatestSnapshot_ExtrapolatesByVelocityUpToCap()
        {
            object buffer = CreateBuffer();
            float cap = GetProp<float>(buffer, "MaxExtrapolationSeconds");
            Push(buffer, Snapshot(0f, 0f, 0f, velX: 0f, yaw: 0f, receivedTime: 10.0f));
            Push(buffer, Snapshot(2f, 0f, 0f, velX: 10f, yaw: 0f, receivedTime: 10.1f));

            // Render time is far past the latest snapshot: extrapolation must
            // advance along the snapshot velocity but stop at the cap.
            (Vector3 position, _, string mode) = Sample(buffer, renderTime: 11.0f);

            Assert.That(mode, Is.EqualTo("Extrapolation"));
            Assert.That(position.x, Is.EqualTo(2f + 10f * cap).Within(1e-4f));
            Assert.That(GetProp<float>(buffer, "LastExtrapolationSeconds"), Is.EqualTo(cap).Within(1e-5f));
        }

        [Test]
        public void Tick_TargetBeyondSnapThreshold_HardSnapsRenderPose()
        {
            object buffer = CreateBuffer();
            ForceRenderPose(buffer, Vector3.zero, 0f);
            Push(buffer, Snapshot(5f, 0f, 0f, velX: 0f, yaw: 0f, receivedTime: 10.0f));

            Tick(buffer, dt: 1f / 60f, now: 10.0f);

            Assert.That(GetProp<int>(buffer, "HardSnapCount"), Is.EqualTo(1));
            Assert.That(GetProp<int>(buffer, "SmoothUpdateCount"), Is.EqualTo(0));
            Assert.That(GetProp<float>(buffer, "LastPositionError"), Is.EqualTo(5f).Within(1e-4f));
            Assert.That(GetProp<Vector3>(buffer, "RenderPosition").x, Is.EqualTo(5f).Within(1e-4f));
        }

        [Test]
        public void Tick_TargetBelowSnapThreshold_SmoothsTowardTarget()
        {
            object buffer = CreateBuffer();
            ForceRenderPose(buffer, Vector3.zero, 0f);
            Push(buffer, Snapshot(1f, 0f, 0f, velX: 0f, yaw: 0f, receivedTime: 10.0f));

            const float dt = 1f / 60f;
            Tick(buffer, dt, now: 10.0f);

            float expectedLerp = Mathf.Min(1f, GetProp<float>(buffer, "SmoothingSpeed") * dt);
            Assert.That(GetProp<int>(buffer, "HardSnapCount"), Is.EqualTo(0));
            Assert.That(GetProp<int>(buffer, "SmoothUpdateCount"), Is.EqualTo(1));
            Assert.That(GetProp<float>(buffer, "LastPositionError"), Is.EqualTo(1f).Within(1e-4f));
            Assert.That(GetProp<Vector3>(buffer, "RenderPosition").x, Is.EqualTo(expectedLerp).Within(1e-4f));
        }

        [Test]
        public void Sample_ZeroVelocitySnapshots_HoldPositionWhileExtrapolating()
        {
            // NPC rows replicate no velocity, so NpcEntity pushes zero-velocity
            // snapshots: past the latest snapshot the pose must hold in place.
            object buffer = CreateBuffer();
            float cap = GetProp<float>(buffer, "MaxExtrapolationSeconds");
            Push(buffer, Snapshot(1f, 0f, 2f, velX: 0f, yaw: 0.5f, receivedTime: 10.0f));
            Push(buffer, Snapshot(3f, 0f, 4f, velX: 0f, yaw: 0.5f, receivedTime: 10.1f));

            (Vector3 position, float yaw, string mode) = Sample(buffer, renderTime: 10.5f);

            Assert.That(mode, Is.EqualTo("Extrapolation"));
            Assert.That(position.x, Is.EqualTo(3f).Within(1e-4f));
            Assert.That(position.y, Is.EqualTo(0f).Within(1e-4f));
            Assert.That(position.z, Is.EqualTo(4f).Within(1e-4f));
            Assert.That(yaw, Is.EqualTo(0.5f).Within(1e-4f));
            Assert.That(GetProp<float>(buffer, "LastExtrapolationSeconds"), Is.EqualTo(cap).Within(1e-5f));
        }

        private static object CreateBuffer()
            => Activator.CreateInstance(BufferType)!;

        private static object Snapshot(float posX, float posY, float posZ, float velX, float yaw, float receivedTime)
            => Activator.CreateInstance(
                SnapshotType, posX, posY, posZ, velX, 0f, 0f, yaw, true, 0u, receivedTime)!;

        private static void Push(object buffer, object snapshot)
        {
            MethodInfo method = BufferType.GetMethod("Push")
                ?? throw new InvalidOperationException("Missing RemotePresentationBuffer.Push");
            method.Invoke(buffer, new[] { snapshot });
        }

        private static void ForceRenderPose(object buffer, Vector3 position, float yawRadians)
        {
            MethodInfo method = BufferType.GetMethod("ForceRenderPose")
                ?? throw new InvalidOperationException("Missing RemotePresentationBuffer.ForceRenderPose");
            method.Invoke(buffer, new object[] { position, yawRadians });
        }

        private static void Tick(object buffer, float dt, float now)
        {
            MethodInfo method = BufferType.GetMethod("Tick")
                ?? throw new InvalidOperationException("Missing RemotePresentationBuffer.Tick");
            method.Invoke(buffer, new object[] { dt, now, Vector3.zero, 0f });
        }

        private static (Vector3 position, float yaw, string mode) Sample(object buffer, float renderTime)
        {
            MethodInfo method = BufferType.GetMethod("Sample")
                ?? throw new InvalidOperationException("Missing RemotePresentationBuffer.Sample");
            var args = new object?[] { renderTime, Vector3.zero, 0f, null, null, null };
            method.Invoke(buffer, args);
            return ((Vector3)args[3]!, (float)args[4]!, args[5]!.ToString()!);
        }

        private static T GetProp<T>(object buffer, string name)
        {
            PropertyInfo property = BufferType.GetProperty(name)
                ?? throw new InvalidOperationException($"Missing RemotePresentationBuffer.{name}");
            return (T)property.GetValue(buffer)!;
        }
    }
}
