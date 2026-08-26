#nullable enable
using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Arena.EditModeTests
{
    /// <summary>
    /// Feel-audit F3/F4 + design-review S1: pure-math coverage of
    /// RemotePresentationBuffer, the snapshot ring + sample/smooth/snap core
    /// extracted from ClientSimulationState and shared by remote players and
    /// NPCs. Time is caller-supplied, so every case runs deterministically
    /// off the Unity player loop (PlayerSnapshot's explicit-receivedTime
    /// constructor). F4 adds the server-time timeline: bursty arrival times
    /// with uniform server times must sample uniform motion, and the arrival
    /// timeline stays the automatic fallback when the server clock or a
    /// snapshot's ServerTimeMs is missing. S1 adds the idle-aware sample
    /// taxonomy: every non-seeding Tick sample classifies as interpolated /
    /// extrapolating (within cap) / starved (past cap, delivery late) /
    /// settled (past cap, entity authoritatively at rest), and a settled
    /// entity's reported buffer depth pins at the cap boundary instead of
    /// diving unboundedly negative.
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

        [SetUp]
        [TearDown]
        public void ResetServerTimelineToggle()
        {
            SetServerTimelineEnabled(true);
        }

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

        [Test]
        public void SampleServerTime_BurstyArrivalsUniformServerTimes_SamplesUniformMotion()
        {
            // Four snapshots one server tick (33 ms) apart, delivered as a
            // burst of three then a 98 ms gap — the SpacetimeDB
            // transaction-batch shape that warps the arrival timeline.
            object buffer = CreateBuffer();
            Push(buffer, Snapshot(0f, 0f, 0f, velX: 0f, yaw: 0f, receivedTime: 10.000f, serverTimeMs: 1000L));
            Push(buffer, Snapshot(1f, 0f, 0f, velX: 0f, yaw: 0f, receivedTime: 10.001f, serverTimeMs: 1033L));
            Push(buffer, Snapshot(2f, 0f, 0f, velX: 0f, yaw: 0f, receivedTime: 10.002f, serverTimeMs: 1066L));
            Push(buffer, Snapshot(3f, 0f, 0f, velX: 0f, yaw: 0f, receivedTime: 10.100f, serverTimeMs: 1099L));

            // Three render times one tick apart on each timeline.
            float serverX0 = SampleServerTime(buffer, 1001L).position.x;
            float serverX1 = SampleServerTime(buffer, 1034L).position.x;
            float serverX2 = SampleServerTime(buffer, 1067L).position.x;
            float arrivalX0 = Sample(buffer, renderTime: 10.001f).position.x;
            float arrivalX1 = Sample(buffer, renderTime: 10.034f).position.x;
            float arrivalX2 = Sample(buffer, renderTime: 10.067f).position.x;

            float serverStep0 = serverX1 - serverX0;
            float serverStep1 = serverX2 - serverX1;
            float arrivalStep0 = arrivalX1 - arrivalX0;
            float arrivalStep1 = arrivalX2 - arrivalX1;

            // Server-time path: uniform motion (one unit per tick).
            Assert.That(serverStep0, Is.EqualTo(1f).Within(1e-3f));
            Assert.That(serverStep1, Is.EqualTo(1f).Within(1e-3f));
            // Arrival path: the burst compresses and the gap stretches motion.
            Assert.That(Mathf.Abs(arrivalStep0 - arrivalStep1), Is.GreaterThan(0.5f));
        }

        [Test]
        public void Tick_ServerClockAndServerTimes_UsesServerTimeline()
        {
            object buffer = CreateBuffer();
            long delayMs = GetProp<long>(buffer, "ServerTimeDelayMs");
            Push(buffer, Snapshot(0f, 0f, 0f, velX: 0f, yaw: 0f, receivedTime: 10.0f, serverTimeMs: 1000L));
            Push(buffer, Snapshot(1f, 0f, 0f, velX: 0f, yaw: 0f, receivedTime: 10.1f, serverTimeMs: 1033L));

            // renderServerTime = 1133 - 100 = 1033: exactly the newest snapshot.
            Tick(buffer, dt: 1f / 60f, now: 10.1f, serverNowMs: 1033L + delayMs);

            Assert.That(GetProp<bool>(buffer, "LastTickUsedServerTimeline"), Is.True);
            Assert.That(GetProp<float>(buffer, "LastEffectiveDelayMs"), Is.EqualTo(delayMs).Within(1e-3f));
            Assert.That(GetProp<float>(buffer, "LastBufferAheadTicks"), Is.EqualTo(0f).Within(1e-3f));
            Assert.That(GetProp<Vector3>(buffer, "RenderPosition").x, Is.GreaterThan(0f));
        }

        [Test]
        public void Tick_NoServerClockEstimate_FallsBackToArrivalTimeline()
        {
            object buffer = CreateBuffer();
            Push(buffer, Snapshot(0f, 0f, 0f, velX: 0f, yaw: 0f, receivedTime: 10.0f, serverTimeMs: 1000L));
            Push(buffer, Snapshot(1f, 0f, 0f, velX: 0f, yaw: 0f, receivedTime: 10.1f, serverTimeMs: 1033L));

            Tick(buffer, dt: 1f / 60f, now: 10.1f, serverNowMs: null);

            float arrivalDelayMs = GetProp<float>(buffer, "InterpolationDelaySeconds") * 1000f;
            Assert.That(GetProp<bool>(buffer, "LastTickUsedServerTimeline"), Is.False);
            Assert.That(GetProp<float>(buffer, "LastEffectiveDelayMs"), Is.EqualTo(arrivalDelayMs).Within(1e-3f));
        }

        [Test]
        public void Tick_SnapshotWithoutServerTime_FallsBackToArrivalTimeline()
        {
            object buffer = CreateBuffer();
            Push(buffer, Snapshot(0f, 0f, 0f, velX: 0f, yaw: 0f, receivedTime: 10.0f, serverTimeMs: 1000L));
            // Pre-F4-shaped snapshot (e.g. the special-movement seed): no ServerTimeMs.
            Push(buffer, Snapshot(1f, 0f, 0f, velX: 0f, yaw: 0f, receivedTime: 10.1f));

            Tick(buffer, dt: 1f / 60f, now: 10.1f, serverNowMs: 1133L);

            Assert.That(GetProp<bool>(buffer, "LastTickUsedServerTimeline"), Is.False);
        }

        [Test]
        public void Tick_ToggleDisabled_FallsBackToArrivalTimeline()
        {
            object buffer = CreateBuffer();
            Push(buffer, Snapshot(0f, 0f, 0f, velX: 0f, yaw: 0f, receivedTime: 10.0f, serverTimeMs: 1000L));
            Push(buffer, Snapshot(1f, 0f, 0f, velX: 0f, yaw: 0f, receivedTime: 10.1f, serverTimeMs: 1033L));

            SetServerTimelineEnabled(false);
            Tick(buffer, dt: 1f / 60f, now: 10.1f, serverNowMs: 1133L);

            Assert.That(GetProp<bool>(buffer, "LastTickUsedServerTimeline"), Is.False);
        }

        [Test]
        public void QuantizeServerTimeMicros_RoundsToFixedTickGrid()
        {
            MethodInfo method = BufferType.GetMethod("QuantizeServerTimeMicros")
                ?? throw new InvalidOperationException("Missing RemotePresentationBuffer.QuantizeServerTimeMicros");

            Assert.That((long)method.Invoke(null, new object[] { 990_000L })!, Is.EqualTo(990L));
            Assert.That((long)method.Invoke(null, new object[] { 1_005_000L })!, Is.EqualTo(990L));
            Assert.That((long)method.Invoke(null, new object[] { 1_010_000L })!, Is.EqualTo(1023L));
        }

        [Test]
        public void ClassifySample_CoversAllFourStates()
        {
            const float cap = 0.066f;

            Assert.That(Classify("Interpolation", 0f, cap, "StopsWhenIdle", true),
                Is.EqualTo("Interpolated"));
            Assert.That(Classify("Extrapolation", 0.03f, cap, "StopsWhenIdle", true),
                Is.EqualTo("Extrapolating"));
            Assert.That(Classify("Extrapolation", 8f, cap, "EveryTick", true),
                Is.EqualTo("Starved"));
            Assert.That(Classify("Extrapolation", 8f, cap, "StopsWhenIdle", true),
                Is.EqualTo("Settled"));
        }

        [Test]
        public void ClassifySample_AtCapBoundary_CountsAsPastCap()
        {
            const float cap = 0.066f;

            // Just under the cap is still extrapolating; exactly at the cap
            // the pose has spent its full budget and counts as past it.
            Assert.That(Classify("Extrapolation", cap - 1e-4f, cap, "StopsWhenIdle", true),
                Is.EqualTo("Extrapolating"));
            Assert.That(Classify("Extrapolation", cap, cap, "StopsWhenIdle", true),
                Is.EqualTo("Settled"));
            Assert.That(Classify("Extrapolation", cap, cap, "EveryTick", true),
                Is.EqualTo("Starved"));
        }

        [Test]
        public void ClassifySample_PlayerPastCap_IsStarvedRegardlessOfGlobalFlow()
        {
            // PlayerPhysics rows are written every tick while connected, so a
            // remote player past the cap always means delivery is late.
            const float cap = 0.066f;

            Assert.That(Classify("Extrapolation", 1f, cap, "EveryTick", true), Is.EqualTo("Starved"));
            Assert.That(Classify("Extrapolation", 1f, cap, "EveryTick", false), Is.EqualTo("Starved"));
        }

        [Test]
        public void ClassifySample_NpcPastCap_GlobalFlowFlipsSettledVsStarved()
        {
            // NpcPhysics rows legitimately stop when the NPC is idle: past the
            // cap the NPC is settled while global row delivery is healthy and
            // starved once it has stalled.
            const float cap = 0.066f;

            Assert.That(Classify("Extrapolation", 1f, cap, "StopsWhenIdle", true), Is.EqualTo("Settled"));
            Assert.That(Classify("Extrapolation", 1f, cap, "StopsWhenIdle", false), Is.EqualTo("Starved"));
        }

        [Test]
        public void ReportableBufferAheadTicks_SettledPinsAtCapBoundary_OthersReportRaw()
        {
            // cap 0.066 s / 0.033 s per tick = exactly 2 ticks of budget: a
            // settled entity's depth pins at −2 instead of the raw dive
            // (observed live: −241 on an idle kobold); a starved entity keeps
            // the raw dive — that dive is the lateness signal.
            const float cap = 0.066f;

            Assert.That(ReportableDepth(-241f, "Settled", cap), Is.EqualTo(-2f).Within(1e-3f));
            Assert.That(ReportableDepth(-241f, "Starved", cap), Is.EqualTo(-241f));
            Assert.That(ReportableDepth(-1.5f, "Extrapolating", cap), Is.EqualTo(-1.5f));
            Assert.That(ReportableDepth(3f, "Interpolated", cap), Is.EqualTo(3f));
        }

        [Test]
        public void Tick_NpcIdlePastCap_CountsSettledAndPinsBufferDepth()
        {
            object buffer = CreateBuffer(cadence: "StopsWhenIdle");
            Push(buffer, Snapshot(1f, 0f, 2f, velX: 0f, yaw: 0f, receivedTime: 10.000f, serverTimeMs: 1000L));
            Push(buffer, Snapshot(1f, 0f, 2f, velX: 0f, yaw: 0f, receivedTime: 10.033f, serverTimeMs: 1033L));

            // Server clock 8 s past the newest row — an idle NPC, rows long
            // since stopped — while global row delivery is healthy.
            Tick(buffer, dt: 1f / 60f, now: 18.0f, serverNowMs: 9133L, rowFlowHealthy: true);

            Assert.That(GetProp<int>(buffer, "SettledSampleCount"), Is.EqualTo(1));
            Assert.That(GetProp<int>(buffer, "StarvedSampleCount"), Is.EqualTo(0));
            Assert.That(GetProp<int>(buffer, "ExtrapolationSampleCount"), Is.EqualTo(0));
            Assert.That(LastSampleClassName(buffer), Is.EqualTo("Settled"));
            // Raw depth would be (1033 − 9033) / 33 ≈ −242 ticks; settled pins at −2.
            Assert.That(GetProp<float>(buffer, "LastBufferAheadTicks"), Is.EqualTo(-2f).Within(1e-3f));

            // Same idle NPC once global delivery stalls: now it is starved and
            // the depth reports the raw dive.
            Tick(buffer, dt: 1f / 60f, now: 18.0f, serverNowMs: 9133L, rowFlowHealthy: false);

            Assert.That(GetProp<int>(buffer, "StarvedSampleCount"), Is.EqualTo(1));
            Assert.That(LastSampleClassName(buffer), Is.EqualTo("Starved"));
            Assert.That(GetProp<float>(buffer, "LastBufferAheadTicks"), Is.LessThan(-100f));
        }

        [Test]
        public void Tick_PlayerPastCap_CountsStarvedEvenWithHealthyGlobalFlow()
        {
            object buffer = CreateBuffer(cadence: "EveryTick");
            Push(buffer, Snapshot(1f, 0f, 2f, velX: 0f, yaw: 0f, receivedTime: 10.000f, serverTimeMs: 1000L));
            Push(buffer, Snapshot(1f, 0f, 2f, velX: 0f, yaw: 0f, receivedTime: 10.033f, serverTimeMs: 1033L));

            Tick(buffer, dt: 1f / 60f, now: 18.0f, serverNowMs: 9133L, rowFlowHealthy: true);

            Assert.That(GetProp<int>(buffer, "StarvedSampleCount"), Is.EqualTo(1));
            Assert.That(GetProp<int>(buffer, "SettledSampleCount"), Is.EqualTo(0));
            Assert.That(LastSampleClassName(buffer), Is.EqualTo("Starved"));
        }

        [Test]
        public void Tick_WithinCap_CountsExtrapolatingNotStarved()
        {
            object buffer = CreateBuffer(cadence: "StopsWhenIdle");
            Push(buffer, Snapshot(1f, 0f, 2f, velX: 0f, yaw: 0f, receivedTime: 10.000f, serverTimeMs: 1000L));
            Push(buffer, Snapshot(1f, 0f, 2f, velX: 0f, yaw: 0f, receivedTime: 10.033f, serverTimeMs: 1033L));

            // Render point 33 ms past the newest row: within the 66 ms cap.
            Tick(buffer, dt: 1f / 60f, now: 10.2f, serverNowMs: 1166L, rowFlowHealthy: true);

            Assert.That(GetProp<int>(buffer, "ExtrapolationSampleCount"), Is.EqualTo(1));
            Assert.That(GetProp<int>(buffer, "StarvedSampleCount"), Is.EqualTo(0));
            Assert.That(GetProp<int>(buffer, "SettledSampleCount"), Is.EqualTo(0));
            Assert.That(LastSampleClassName(buffer), Is.EqualTo("Extrapolating"));
        }

        private static object CreateBuffer(string cadence = "EveryTick")
            => Activator.CreateInstance(BufferType, NestedEnumValue("SourceRowCadence", cadence))!;

        private static object NestedEnumValue(string enumName, string valueName)
        {
            Type enumType = BufferType.GetNestedType(enumName)
                ?? throw new InvalidOperationException($"Missing RemotePresentationBuffer.{enumName}");
            return Enum.Parse(enumType, valueName);
        }

        private static object Snapshot(float posX, float posY, float posZ, float velX, float yaw, float receivedTime)
            => Activator.CreateInstance(
                SnapshotType, posX, posY, posZ, velX, 0f, 0f, yaw, true, 0u, receivedTime)!;

        private static object Snapshot(
            float posX, float posY, float posZ, float velX, float yaw, float receivedTime, long serverTimeMs)
            => Activator.CreateInstance(
                SnapshotType, posX, posY, posZ, velX, 0f, 0f, yaw, true, 0u, receivedTime,
                serverTimeMs, true, 0)!;

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

        private static void Tick(object buffer, float dt, float now, long? serverNowMs = null, bool rowFlowHealthy = true)
        {
            MethodInfo method = BufferType.GetMethod("Tick")
                ?? throw new InvalidOperationException("Missing RemotePresentationBuffer.Tick");
            method.Invoke(buffer, new object?[] { dt, now, serverNowMs, Vector3.zero, 0f, rowFlowHealthy });
        }

        private static string Classify(
            string mode, float rawExtrapolationSeconds, float cap, string cadence, bool rowFlowHealthy)
        {
            MethodInfo method = BufferType.GetMethod("ClassifySample", BindingFlags.Public | BindingFlags.Static)
                ?? throw new InvalidOperationException("Missing RemotePresentationBuffer.ClassifySample");
            return method.Invoke(null, new object[]
            {
                NestedEnumValue("SampleMode", mode),
                rawExtrapolationSeconds,
                cap,
                NestedEnumValue("SourceRowCadence", cadence),
                rowFlowHealthy,
            })!.ToString()!;
        }

        private static float ReportableDepth(float bufferAheadTicks, string sampleClass, float cap)
        {
            MethodInfo method = BufferType.GetMethod("ReportableBufferAheadTicks", BindingFlags.Public | BindingFlags.Static)
                ?? throw new InvalidOperationException("Missing RemotePresentationBuffer.ReportableBufferAheadTicks");
            return (float)method.Invoke(null, new object[]
            {
                bufferAheadTicks,
                NestedEnumValue("SampleClass", sampleClass),
                cap,
            })!;
        }

        private static (Vector3 position, float yaw, string mode) Sample(object buffer, float renderTime)
        {
            MethodInfo method = BufferType.GetMethod("Sample")
                ?? throw new InvalidOperationException("Missing RemotePresentationBuffer.Sample");
            var args = new object?[] { renderTime, Vector3.zero, 0f, null, null, null };
            method.Invoke(buffer, args);
            return ((Vector3)args[3]!, (float)args[4]!, args[5]!.ToString()!);
        }

        private static (Vector3 position, float yaw, string mode) SampleServerTime(object buffer, long renderServerTimeMs)
        {
            MethodInfo method = BufferType.GetMethod("SampleServerTime")
                ?? throw new InvalidOperationException("Missing RemotePresentationBuffer.SampleServerTime");
            var args = new object?[] { renderServerTimeMs, Vector3.zero, 0f, null, null, null };
            method.Invoke(buffer, args);
            return ((Vector3)args[3]!, (float)args[4]!, args[5]!.ToString()!);
        }

        private static void SetServerTimelineEnabled(bool enabled)
        {
            PropertyInfo property = BufferType.GetProperty(
                "ServerTimeTimelineEnabled", BindingFlags.Public | BindingFlags.Static)
                ?? throw new InvalidOperationException("Missing RemotePresentationBuffer.ServerTimeTimelineEnabled");
            property.SetValue(null, enabled);
        }

        private static T GetProp<T>(object buffer, string name)
        {
            PropertyInfo property = BufferType.GetProperty(name)
                ?? throw new InvalidOperationException($"Missing RemotePresentationBuffer.{name}");
            return (T)property.GetValue(buffer)!;
        }

        private static string? LastSampleClassName(object buffer)
        {
            PropertyInfo property = BufferType.GetProperty("LastTickSampleClass")
                ?? throw new InvalidOperationException("Missing RemotePresentationBuffer.LastTickSampleClass");
            return property.GetValue(buffer)?.ToString();
        }
    }
}
