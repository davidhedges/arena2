#nullable enable
using System;
using System.Reflection;
using NUnit.Framework;

namespace Arena.EditModeTests
{
    /// <summary>
    /// Feel-audit F1 step 4 + F2 contract item 4: the denial toast and the
    /// connection-quality dot. Covers the pure pieces only — the
    /// reason → display-text mapping (every ActionRejectReason variant must
    /// render honest non-empty text), the toast's single-slot
    /// rate-limit/expiry bookkeeping (time injected), and the
    /// RTT/staleness → Good/Degraded/Bad classification (calibrated against
    /// docs/latency-testing.md Profiles A and B).
    /// </summary>
    public class ConnectionFeedbackHudTests
    {
        private static readonly Assembly RuntimeAssembly = AppDomain.CurrentDomain.Load("Assembly-CSharp");

        private static readonly Type DenialTextType = RequireType("Arena.UI.ActionDenialText");
        private static readonly Type ToastModelType = RequireType("Arena.UI.ActionDenialToastModel");
        private static readonly Type QualityModelType = RequireType("Arena.UI.ConnectionQualityModel");
        private static readonly Type RejectReasonType = RequireType("SpacetimeDB.Types.ActionRejectReason");

        private static Type RequireType(string name)
            => RuntimeAssembly.GetType(name)
               ?? throw new InvalidOperationException($"Missing runtime type {name}");

        // ---------------------------------------------------------------
        // Reason → display text (F1 step 4)
        // ---------------------------------------------------------------

        [Test]
        public void DenialText_CoversEveryRejectReason_WithNonEmptyText()
        {
            foreach (object reason in Enum.GetValues(RejectReasonType))
            {
                string text = DenialTextFor(reason);
                Assert.That(string.IsNullOrWhiteSpace(text), Is.False,
                    $"ActionRejectReason.{reason} has no display text");
            }
        }

        [Test]
        public void DenialText_MapsKnownReasons_ToExpectedStrings()
        {
            Assert.That(DenialTextFor(Reason("OutOfRange")), Is.EqualTo("Out of range"));
            Assert.That(DenialTextFor(Reason("OnCooldown")), Is.EqualTo("On cooldown"));
            Assert.That(DenialTextFor(Reason("InsufficientResource")), Is.EqualTo("Not enough resource"));
            Assert.That(DenialTextFor(Reason("LineOfSightBlocked")), Is.EqualTo("No line of sight"));
        }

        // ---------------------------------------------------------------
        // Toast bookkeeping (F1 step 4)
        // ---------------------------------------------------------------

        [Test]
        public void Toast_AutoDismissWindow_IsAtMost1500Ms()
        {
            Assert.That(Const<double>(ToastModelType, "DisplaySeconds"),
                Is.LessThanOrEqualTo(1.5));
        }

        [Test]
        public void Toast_ShowsThenExpires()
        {
            var toast = new Toast();
            Assert.That(toast.TryShow("Out of range", 10.0), Is.True);
            Assert.That(toast.Tick(10.5), Is.EqualTo("Out of range"));
            Assert.That(toast.IsVisible(10.5), Is.True);

            double displaySeconds = Const<double>(ToastModelType, "DisplaySeconds");
            Assert.That(toast.IsVisible(10.0 + displaySeconds + 0.01), Is.False);
            Assert.That(toast.Tick(10.0 + displaySeconds + 0.01), Is.Null,
                "an expired toast must clear its text");
        }

        [Test]
        public void Toast_MashedButton_DoesNotStackOrRefreshInsideRearmWindow()
        {
            var toast = new Toast();
            Assert.That(toast.TryShow("On cooldown", 10.0), Is.True);

            // Mash inside the rearm window: dropped, original expiry keeps.
            Assert.That(toast.TryShow("On cooldown", 10.1), Is.False);
            Assert.That(toast.TryShow("Out of range", 10.2), Is.False,
                "a different reason inside the rearm window is dropped too");
            Assert.That(toast.Tick(10.2), Is.EqualTo("On cooldown"));

            double displaySeconds = Const<double>(ToastModelType, "DisplaySeconds");
            Assert.That(toast.IsVisible(10.0 + displaySeconds + 0.01), Is.False,
                "dropped shows must not have extended the original expiry");
        }

        [Test]
        public void Toast_ReshowAfterRearm_ReplacesTextAndRefreshesExpiry()
        {
            var toast = new Toast();
            double rearmSeconds = Const<double>(ToastModelType, "MinRearmSeconds");
            double displaySeconds = Const<double>(ToastModelType, "DisplaySeconds");

            Assert.That(toast.TryShow("On cooldown", 10.0), Is.True);
            double reshowAt = 10.0 + rearmSeconds + 0.01;
            Assert.That(toast.TryShow("Out of range", reshowAt), Is.True);
            Assert.That(toast.Tick(reshowAt), Is.EqualTo("Out of range"),
                "single-slot toast replaces text instead of stacking");
            Assert.That(toast.IsVisible(10.0 + displaySeconds + 0.01), Is.True,
                "accepted re-show must refresh the expiry");
            Assert.That(toast.IsVisible(reshowAt + displaySeconds + 0.01), Is.False);
        }

        [Test]
        public void Toast_EmptyText_IsRejected()
        {
            var toast = new Toast();
            Assert.That(toast.TryShow(string.Empty, 10.0), Is.False);
            Assert.That(toast.IsVisible(10.0), Is.False);
        }

        // ---------------------------------------------------------------
        // RTT/staleness classification (F2 contract item 4)
        // ---------------------------------------------------------------

        [Test]
        public void Quality_LocalDevConditions_ReadGood()
        {
            Assert.That(Classify(true, 5L, 12L, 0.1), Is.EqualTo("Good"));
        }

        [Test]
        public void Quality_NoRttStatsYet_FreshRows_ReadGood()
        {
            Assert.That(Classify(false, 0L, 0L, 0.1), Is.EqualTo("Good"));
        }

        [Test]
        public void Quality_ProfileA_ReadsDegraded()
        {
            // docs/latency-testing.md Profile A: ~100 ms added RTT, +30 ms jitter.
            Assert.That(Classify(true, 105L, 150L, 0.1), Is.EqualTo("Degraded"));
        }

        [Test]
        public void Quality_ProfileB_ReadsBad()
        {
            // docs/latency-testing.md Profile B: ~200 ms added RTT, +60 ms jitter.
            Assert.That(Classify(true, 210L, 280L, 0.1), Is.EqualTo("Bad"));
        }

        [Test]
        public void Quality_JitterAlone_EscalatesViaP95()
        {
            Assert.That(Classify(true, 40L, 200L, 0.1), Is.EqualTo("Degraded"));
            Assert.That(Classify(true, 40L, 400L, 0.1), Is.EqualTo("Bad"));
        }

        [Test]
        public void Quality_RowStaleness_EscalatesWithoutRttStats()
        {
            Assert.That(Classify(false, 0L, 0L, 2.0), Is.EqualTo("Degraded"));
            Assert.That(Classify(false, 0L, 0L, 5.0), Is.EqualTo("Bad"));
        }

        [Test]
        public void Quality_RowStaleness_OverridesGoodRtt()
        {
            Assert.That(Classify(true, 5L, 12L, 5.0), Is.EqualTo("Bad"));
        }

        // ---------------------------------------------------------------
        // Reflection plumbing
        // ---------------------------------------------------------------

        private static string DenialTextFor(object reason)
        {
            MethodInfo method = DenialTextType.GetMethod("For", BindingFlags.Public | BindingFlags.Static)
                ?? throw new InvalidOperationException("ActionDenialText.For not found");
            return (string)method.Invoke(null, new[] { reason })!;
        }

        private static object Reason(string name) => Enum.Parse(RejectReasonType, name);

        private static string Classify(bool hasRttStats, long p50Ms, long p95Ms, double stalenessSeconds)
        {
            MethodInfo method = QualityModelType.GetMethod("Classify", BindingFlags.Public | BindingFlags.Static)
                ?? throw new InvalidOperationException("ConnectionQualityModel.Classify not found");
            object result = method.Invoke(null, new object[] { hasRttStats, p50Ms, p95Ms, stalenessSeconds })!;
            return result.ToString();
        }

        private static T Const<T>(Type type, string name)
        {
            FieldInfo field = type.GetField(name, BindingFlags.Public | BindingFlags.Static)
                ?? throw new InvalidOperationException($"{type.Name}.{name} not found");
            return (T)field.GetRawConstantValue()!;
        }

        private sealed class Toast
        {
            private readonly object _instance =
                Activator.CreateInstance(ToastModelType)
                ?? throw new InvalidOperationException("Could not construct ActionDenialToastModel");

            public bool TryShow(string text, double now)
                => (bool)Call("TryShow", text, now)!;

            public string? Tick(double now)
                => (string?)Call("Tick", now);

            public bool IsVisible(double now)
                => (bool)Call("IsVisible", now)!;

            private object? Call(string name, params object[] args)
            {
                MethodInfo method = ToastModelType.GetMethod(name, BindingFlags.Public | BindingFlags.Instance)
                    ?? throw new InvalidOperationException($"ActionDenialToastModel.{name} not found");
                return method.Invoke(_instance, args);
            }
        }
    }
}
