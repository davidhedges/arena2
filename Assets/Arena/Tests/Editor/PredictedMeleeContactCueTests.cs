#nullable enable
using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Arena.EditModeTests
{
    /// <summary>
    /// Feel-audit F5 (slice 2): predicted melee contact cues.
    ///
    /// Advisory hit test math: MeleeStrikeGeometry is the range/facing math
    /// shared by the press-time gate and the advisory test at the authored
    /// first hit window — boundary semantics must match the press gate's
    /// strict "&gt; max" / "&lt; min" rejections, and the facing arc must keep
    /// its permissive degenerate-vector fallbacks.
    ///
    /// Correlation bookkeeping: a fired cue is confirmed by any same-target
    /// authoritative contact-family row inside the 500 ms window and counted
    /// as a false positive on timeout or rejection. Suppression: only the
    /// authoritative impact row that duplicates the predicted presentation
    /// (impact trigger, predicted hit index, same target) is suppressed —
    /// once — so the cue never double-plays; block/parry always play.
    /// </summary>
    public class PredictedMeleeContactCueTests
    {
        private const string GeometryTypeName = "Arena.Combat.MeleeStrikeGeometry";
        private const string LedgerTypeName = "Arena.Presentation.PredictedMeleeContactCueLedger";
        private const string SuppressDecisionName = "SuppressCue";
        private const string PlayDecisionName = "PlayCue";

        private static readonly Assembly RuntimeAssembly = AppDomain.CurrentDomain.Load("Assembly-CSharp");
        private static readonly Type GeometryType = RequireRuntimeType(GeometryTypeName);
        private static readonly Type LedgerType = RequireRuntimeType(LedgerTypeName);

        // -------------------------------------------------------------------
        // Advisory hit test math (range + facing + target radius)
        // -------------------------------------------------------------------

        [Test]
        public void HorizontalDistance_IgnoresHeight()
        {
            float distance = HorizontalDistance(new Vector3(0f, 0f, 0f), new Vector3(3f, 25f, 4f));

            Assert.That(distance, Is.EqualTo(5f).Within(0.0001f));
        }

        [Test]
        public void RangeGate_PassesAtExactMaximumBoundary()
        {
            // Press gate rejects only "> max": range 2 + radius 0.5 → 2.5 passes.
            Assert.That(PassesRangeGate(2.5f, 2f, 0f, 0.5f), Is.True);
        }

        [Test]
        public void RangeGate_RejectsBeyondMaximum()
        {
            Assert.That(PassesRangeGate(2.51f, 2f, 0f, 0.5f), Is.False);
        }

        [Test]
        public void RangeGate_TargetRadiusExtendsReach()
        {
            Assert.That(PassesRangeGate(2.4f, 2f, 0f, 0f), Is.False);
            Assert.That(PassesRangeGate(2.4f, 2f, 0f, 0.5f), Is.True);
        }

        [Test]
        public void RangeGate_RejectsInsideMinimumRange()
        {
            // Press gate rejects only "< min": min 1 + radius 0.5 → 1.5 passes, 1.49 fails.
            Assert.That(PassesRangeGate(1.5f, 5f, 1f, 0.5f), Is.True);
            Assert.That(PassesRangeGate(1.49f, 5f, 1f, 0.5f), Is.False);
        }

        [Test]
        public void RangeGate_NoMinimumRange_AllowsPointBlank()
        {
            Assert.That(PassesRangeGate(0f, 2f, 0f, 0.5f), Is.True);
        }

        [Test]
        public void FacingArc_TargetInFront_Passes()
        {
            Assert.That(
                IsWithinFacingArc(Vector3.forward, Vector3.zero, new Vector3(0.2f, 0f, 3f)),
                Is.True);
        }

        [Test]
        public void FacingArc_TargetBehind_Fails()
        {
            Assert.That(
                IsWithinFacingArc(Vector3.forward, Vector3.zero, new Vector3(0f, 0f, -3f)),
                Is.False);
        }

        [Test]
        public void FacingArc_TargetExactlyPerpendicular_Passes()
        {
            // dot == 0 sits on the ">= 0" boundary and passes, like the press gate.
            Assert.That(
                IsWithinFacingArc(Vector3.forward, Vector3.zero, new Vector3(3f, 0f, 0f)),
                Is.True);
        }

        [Test]
        public void FacingArc_DegenerateDeltaOrForward_Passes()
        {
            Assert.That(
                IsWithinFacingArc(Vector3.forward, Vector3.zero, new Vector3(0f, 2f, 0f)),
                Is.True,
                "target directly above (zero horizontal delta) must pass");
            Assert.That(
                IsWithinFacingArc(Vector3.up, Vector3.zero, new Vector3(0f, 0f, -3f)),
                Is.True,
                "zero horizontal forward must pass");
        }

        // -------------------------------------------------------------------
        // False-positive correlation: cue → authoritative impact vs timeout
        // -------------------------------------------------------------------

        [Test]
        public void FiredCue_MatchedByImpactWithinWindow_IsNotAFalsePositive()
        {
            var ledger = new Ledger();
            ledger.Schedule("tok", "target", fireAtMs: 100, predictedHitIndex: 0);
            ledger.MapAcceptedActionInstance("inst", "tok");
            Assert.That(ledger.TryBeginFiring("tok"), Is.True);
            ledger.RecordCueFired("tok", nowMs: 100);

            string decision = ledger.OnAuthoritativeContact("inst", "target", isImpact: true, hitIndex: 0, nowMs: 300);

            Assert.That(decision, Is.EqualTo(SuppressDecisionName));
            ledger.Tick(nowMs: 10_000);
            Assert.That(ledger.CuesFired, Is.EqualTo(1));
            Assert.That(ledger.MatchedCues, Is.EqualTo(1));
            Assert.That(ledger.FalsePositives, Is.EqualTo(0));
            Assert.That(ledger.SuppressedAuthoritativeCues, Is.EqualTo(1));
        }

        [Test]
        public void FiredCue_NoAuthoritativeImpactWithinWindow_CountsFalsePositiveOnTimeout()
        {
            var ledger = new Ledger();
            ledger.Schedule("tok", "target", fireAtMs: 100, predictedHitIndex: 0);
            ledger.MapAcceptedActionInstance("inst", "tok");
            ledger.RecordCueFired("tok", nowMs: 100);

            ledger.Tick(nowMs: 100 + 500 + 1);

            Assert.That(ledger.FalsePositives, Is.EqualTo(1));
            Assert.That(ledger.MatchedCues, Is.EqualTo(0));

            // A straggler impact after the timeout plays normally and cannot
            // resurrect the consumed entry.
            string decision = ledger.OnAuthoritativeContact("inst", "target", isImpact: true, hitIndex: 0, nowMs: 700);
            Assert.That(decision, Is.EqualTo(PlayDecisionName));
            Assert.That(ledger.FalsePositives, Is.EqualTo(1));
        }

        [Test]
        public void FiredCue_ThenRejectedPress_CountsFalsePositiveImmediately()
        {
            var ledger = new Ledger();
            ledger.Schedule("tok", "target", fireAtMs: 100, predictedHitIndex: 0);
            ledger.RecordCueFired("tok", nowMs: 100);

            ledger.OnPredictionRejected("tok");

            Assert.That(ledger.FalsePositives, Is.EqualTo(1));
        }

        [Test]
        public void ScheduledAdvisory_RejectedBeforeFiring_IsCanceledWithoutCounting()
        {
            var ledger = new Ledger();
            ledger.Schedule("tok", "target", fireAtMs: 100, predictedHitIndex: 0);

            ledger.OnPredictionRejected("tok");

            Assert.That(ledger.TryBeginFiring("tok"), Is.False);
            Assert.That(ledger.FalsePositives, Is.EqualTo(0));
            Assert.That(ledger.CuesFired, Is.EqualTo(0));
        }

        [Test]
        public void AuthoritativeImpactBeforeAdvisoryFires_PlaysAndCancelsTheScheduledCue()
        {
            var ledger = new Ledger();
            ledger.Schedule("tok", "target", fireAtMs: 500, predictedHitIndex: 0);
            ledger.MapAcceptedActionInstance("inst", "tok");

            string decision = ledger.OnAuthoritativeContact("inst", "target", isImpact: true, hitIndex: 0, nowMs: 200);

            Assert.That(decision, Is.EqualTo(PlayDecisionName), "authoritative cue plays — nothing predicted yet");
            Assert.That(ledger.TryBeginFiring("tok"), Is.False, "predicted cue must not fire after the authoritative one played");
            Assert.That(ledger.SuppressedAuthoritativeCues, Is.EqualTo(0));
        }

        [Test]
        public void FiredCue_BlockWithinWindow_MatchesWithoutSuppression()
        {
            var ledger = new Ledger();
            ledger.Schedule("tok", "target", fireAtMs: 100, predictedHitIndex: 0);
            ledger.MapAcceptedActionInstance("inst", "tok");
            ledger.RecordCueFired("tok", nowMs: 100);

            string decision = ledger.OnAuthoritativeContact("inst", "target", isImpact: false, hitIndex: 0, nowMs: 300);

            Assert.That(decision, Is.EqualTo(PlayDecisionName), "block/parry cues are different presentation and always play");
            ledger.Tick(nowMs: 10_000);
            Assert.That(ledger.MatchedCues, Is.EqualTo(1));
            Assert.That(ledger.FalsePositives, Is.EqualTo(0), "contact happened — the cue was not a lie");
            Assert.That(ledger.SuppressedAuthoritativeCues, Is.EqualTo(0));
        }

        [Test]
        public void FiredCue_ImpactOnDifferentTarget_PlaysAndKeepsWaiting()
        {
            var ledger = new Ledger();
            ledger.Schedule("tok", "predicted_target", fireAtMs: 100, predictedHitIndex: 0);
            ledger.MapAcceptedActionInstance("inst", "tok");
            ledger.RecordCueFired("tok", nowMs: 100);

            string otherTarget = ledger.OnAuthoritativeContact("inst", "other_target", isImpact: true, hitIndex: 0, nowMs: 200);
            string ourTarget = ledger.OnAuthoritativeContact("inst", "predicted_target", isImpact: true, hitIndex: 0, nowMs: 250);

            Assert.That(otherTarget, Is.EqualTo(PlayDecisionName), "another target's impact is not our duplicate");
            Assert.That(ourTarget, Is.EqualTo(SuppressDecisionName));
            Assert.That(ledger.MatchedCues, Is.EqualTo(1));
        }

        [Test]
        public void FiredCue_ImpactAtOtherHitIndex_MatchesButPlays()
        {
            var ledger = new Ledger();
            ledger.Schedule("tok", "target", fireAtMs: 100, predictedHitIndex: 0);
            ledger.MapAcceptedActionInstance("inst", "tok");
            ledger.RecordCueFired("tok", nowMs: 100);

            string decision = ledger.OnAuthoritativeContact("inst", "target", isImpact: true, hitIndex: 1, nowMs: 300);

            Assert.That(decision, Is.EqualTo(PlayDecisionName), "a later hit's cue is not the predicted first-hit duplicate");
            Assert.That(ledger.MatchedCues, Is.EqualTo(1));
            Assert.That(ledger.SuppressedAuthoritativeCues, Is.EqualTo(0));
        }

        // -------------------------------------------------------------------
        // Suppression: authoritative cue after predicted cue must not double-play
        // -------------------------------------------------------------------

        [Test]
        public void SuppressionConsumesTheEntry_SecondImpactRowPlaysNormally()
        {
            var ledger = new Ledger();
            ledger.Schedule("tok", "target", fireAtMs: 100, predictedHitIndex: 0);
            ledger.MapAcceptedActionInstance("inst", "tok");
            ledger.RecordCueFired("tok", nowMs: 100);

            string first = ledger.OnAuthoritativeContact("inst", "target", isImpact: true, hitIndex: 0, nowMs: 200);
            string second = ledger.OnAuthoritativeContact("inst", "target", isImpact: true, hitIndex: 0, nowMs: 210);

            Assert.That(first, Is.EqualTo(SuppressDecisionName));
            Assert.That(second, Is.EqualTo(PlayDecisionName), "suppression happens exactly once");
            Assert.That(ledger.SuppressedAuthoritativeCues, Is.EqualTo(1));
        }

        [Test]
        public void UnknownActionInstance_AlwaysPlays()
        {
            var ledger = new Ledger();
            ledger.Schedule("tok", "target", fireAtMs: 100, predictedHitIndex: 0);
            ledger.RecordCueFired("tok", nowMs: 100);

            string decision = ledger.OnAuthoritativeContact("unmapped", "target", isImpact: true, hitIndex: 0, nowMs: 200);

            Assert.That(decision, Is.EqualTo(PlayDecisionName));
        }

        [Test]
        public void MappingForUnknownToken_IsIgnored()
        {
            var ledger = new Ledger();

            ledger.MapAcceptedActionInstance("inst", "never_scheduled");
            string decision = ledger.OnAuthoritativeContact("inst", "target", isImpact: true, hitIndex: 0, nowMs: 200);

            Assert.That(decision, Is.EqualTo(PlayDecisionName));
        }

        // -------------------------------------------------------------------
        // Reflection plumbing (Assembly-CSharp types are not referenceable
        // from the test assembly; same pattern as the other feel-audit tests)
        // -------------------------------------------------------------------

        private static float HorizontalDistance(Vector3 a, Vector3 b)
            => (float)InvokeGeometry("HorizontalDistance", a, b);

        private static bool PassesRangeGate(
            float horizontalDistance,
            float strikeRange,
            float minimumRange,
            float targetRadius)
            => (bool)InvokeGeometry("PassesRangeGate", horizontalDistance, strikeRange, minimumRange, targetRadius);

        private static bool IsWithinFacingArc(Vector3 forward, Vector3 attackerPosition, Vector3 targetPosition)
            => (bool)InvokeGeometry("IsWithinFacingArc", forward, attackerPosition, targetPosition);

        private static object InvokeGeometry(string methodName, params object[] args)
        {
            MethodInfo method = GeometryType.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static)
                ?? throw new InvalidOperationException($"Missing {GeometryTypeName}.{methodName}");
            return method.Invoke(null, args)
                ?? throw new InvalidOperationException($"{methodName} returned null");
        }

        private sealed class Ledger
        {
            private readonly object _instance =
                Activator.CreateInstance(LedgerType, nonPublic: true)
                ?? throw new InvalidOperationException($"Could not construct {LedgerTypeName}");

            public int CuesFired => GetCounter("CuesFired");
            public int MatchedCues => GetCounter("MatchedCues");
            public int FalsePositives => GetCounter("FalsePositives");
            public int SuppressedAuthoritativeCues => GetCounter("SuppressedAuthoritativeCues");

            public void Schedule(string tokenKey, string targetKey, long fireAtMs, int predictedHitIndex)
                => Invoke("Schedule", tokenKey, targetKey, fireAtMs, predictedHitIndex);

            public void MapAcceptedActionInstance(string actionInstanceId, string tokenKey)
                => Invoke("MapAcceptedActionInstance", actionInstanceId, tokenKey);

            public void OnPredictionRejected(string tokenKey)
                => Invoke("OnPredictionRejected", tokenKey);

            public bool TryBeginFiring(string tokenKey)
                => (bool)Invoke("TryBeginFiring", tokenKey)!;

            public void RecordCueFired(string tokenKey, long nowMs)
                => Invoke("RecordCueFired", tokenKey, nowMs);

            public string OnAuthoritativeContact(
                string actionInstanceId,
                string targetKey,
                bool isImpact,
                int hitIndex,
                long nowMs)
                => Invoke("OnAuthoritativeContact", actionInstanceId, targetKey, isImpact, hitIndex, nowMs)!
                    .ToString()!;

            public void Tick(long nowMs)
                => Invoke("Tick", nowMs);

            private object? Invoke(string methodName, params object[] args)
            {
                MethodInfo method = LedgerType.GetMethod(
                        methodName,
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    ?? throw new InvalidOperationException($"Missing {LedgerTypeName}.{methodName}");
                return method.Invoke(_instance, args);
            }

            private int GetCounter(string propertyName)
            {
                PropertyInfo property = LedgerType.GetProperty(
                        propertyName,
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    ?? throw new InvalidOperationException($"Missing {LedgerTypeName}.{propertyName}");
                return (int)property.GetValue(_instance)!;
            }
        }

        private static Type RequireRuntimeType(string typeName)
        {
            return RuntimeAssembly.GetType(typeName)
                ?? throw new InvalidOperationException($"Missing runtime type {typeName}");
        }
    }
}
