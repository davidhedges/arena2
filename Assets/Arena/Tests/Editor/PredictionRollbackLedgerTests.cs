#nullable enable
using System;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using NUnit.Framework;

namespace Arena.EditModeTests
{
    /// <summary>
    /// Feel-audit F1: a server Rejected/StaleToken must restore every predicted
    /// side effect (per-action cooldown, GCD, resource reservation) recorded on
    /// the action's ledger, without touching state that authoritative rows or
    /// later legitimate predictions have since overwritten.
    /// </summary>
    public class PredictionRollbackLedgerTests
    {
        private const string CombatStateType = "Arena.Simulation.LocalCombatState";
        private const string ClockType = "Arena.Network.ArenaServerClock";
        private static readonly Assembly RuntimeAssembly = AppDomain.CurrentDomain.Load("Assembly-CSharp");

        private object _combat = null!;

        [SetUp]
        public void SetUp()
        {
            _combat = GetStaticProperty(CombatStateType, "Instance");
            InvokeInstance(_combat, "ResetForTests");
            InvokeClock("Reset");
        }

        [TearDown]
        public void TearDown()
        {
            InvokeInstance(_combat, "ResetForTests");
            InvokeClock("Reset");
        }

        [TestCase(-60_000L)]
        [TestCase(0L)]
        [TestCase(60_000L)]
        public void Prediction_AfterExpiredAuthoritativeCooldown_UsesServerTimeline(long offsetMs)
        {
            const long clientNowMs = 1_000_000L;
            long serverNowMs = clientNowMs + offsetMs;
            SetClockOffset(clientNowMs, offsetMs);
            InsertAuthoritativeCooldown("FIREBALL", serverNowMs - 9_000L, 8_000L);

            PredictActionStart("FIREBALL", 8_000L, false, 0L, clientNowMs);

            var cooldown = GetSpellCooldown("FIREBALL");
            Assert.That(cooldown.lastCastMs + cooldown.durationMs - serverNowMs, Is.EqualTo(8_000L),
                "an available action must predict its full cooldown on the authoritative timeline");
            Assert.That(RemainingCooldownMs("FIREBALL", clientNowMs), Is.EqualTo(8_000L));
            Assert.That(RemainingCooldownMs("FIREBALL", clientNowMs + 7_999L), Is.EqualTo(1L));
            Assert.That(RemainingCooldownMs("FIREBALL", clientNowMs + 8_000L), Is.Zero);
            Assert.That(RemainingCooldownMs("FIREBALL", clientNowMs + 8_001L), Is.Zero);
        }

        [TestCase(-60_000L)]
        [TestCase(0L)]
        [TestCase(60_000L)]
        public void AuthoritativeCooldown_InsertUpdateDelete_UsesServerTimeline(long offsetMs)
        {
            const long clientNowMs = 1_000_000L;
            long serverNowMs = clientNowMs + offsetMs;
            SetClockOffset(clientNowMs, offsetMs);
            Assert.That(RemainingCooldownMs("FIREBALL", clientNowMs), Is.Zero);

            object row = InsertAuthoritativeCooldown("FIREBALL", serverNowMs, 8_000L);
            Assert.That(RemainingCooldownMs("FIREBALL", clientNowMs), Is.EqualTo(8_000L));
            Assert.That(RemainingCooldownMs("FIREBALL", clientNowMs + 7_999L), Is.EqualTo(1L));
            Assert.That(RemainingCooldownMs("FIREBALL", clientNowMs + 8_000L), Is.Zero);
            Assert.That(RemainingCooldownMs("FIREBALL", clientNowMs + 8_001L), Is.Zero);

            object updated = CreateAuthoritativeCooldown("FIREBALL", serverNowMs + 8_000L, 4_000L);
            InvokeInstance(_combat, "OnSpellCooldownUpdate", null!, row, updated);
            Assert.That(RemainingCooldownMs("FIREBALL", clientNowMs + 8_000L), Is.EqualTo(4_000L));

            InvokeInstance(_combat, "OnSpellCooldownDelete", null!, updated);
            Assert.That(RemainingCooldownMs("FIREBALL", clientNowMs + 8_000L), Is.Zero);
        }

        [TestCase(-60_000L)]
        [TestCase(0L)]
        [TestCase(60_000L)]
        public void Rollback_RemovesPhantomCooldownAndGcd(long offsetMs)
        {
            const long nowMs = 1_000_000L;
            SetClockOffset(nowMs, offsetMs);
            object ledger = PredictActionStart("FIREBALL", 8_000L, true, 1_500L, nowMs);

            Assert.That(SpellCooldownsContains("FIREBALL"), Is.True, "press should predict the cooldown");
            Assert.That(IsGcdActive(nowMs + 100L), Is.True, "press should predict the GCD");

            RollbackPrediction(ledger);

            Assert.That(SpellCooldownsContains("FIREBALL"), Is.False,
                "rejected action must not leave a phantom cooldown entry");
            Assert.That(IsGcdActive(nowMs + 100L), Is.False,
                "rejected action must not leave a phantom GCD");
        }

        [TestCase(-60_000L)]
        [TestCase(0L)]
        [TestCase(60_000L)]
        public void Rollback_RestoresPriorCooldownEntry_AfterClockCorrection(long offsetMs)
        {
            const long nowMs = 1_000_000L;
            SetClockOffset(nowMs, offsetMs);
            long priorServerStartMs = nowMs + offsetMs - 6_000L;
            InsertAuthoritativeCooldown("FIREBALL", priorServerStartMs, 3_000L);

            object ledger = PredictActionStart("FIREBALL", 8_000L, false, 0L, nowMs);
            // A refined estimate must not change which entry the ledger owns.
            InvokeClock("Reset");
            SetClockOffset(nowMs, offsetMs + 200L);
            RollbackPrediction(ledger);

            (long lastCastMs, long durationMs) = GetSpellCooldown("FIREBALL");
            Assert.That(lastCastMs, Is.EqualTo(priorServerStartMs), "rollback must restore the prior entry");
            Assert.That(durationMs, Is.EqualTo(3_000L), "rollback must restore the prior entry");
        }

        [TestCase(-60_000L)]
        [TestCase(0L)]
        [TestCase(60_000L)]
        public void Rollback_LeavesNewerPredictionAlone(long offsetMs)
        {
            const long nowMs = 1_000_000L;
            SetClockOffset(nowMs, offsetMs);
            object ledger = PredictActionStart("FIREBALL", 8_000L, false, 0L, nowMs);

            // A later prediction replaces the entry before the rejection arrives.
            PredictActionStart("FIREBALL", 8_000L, false, 0L, nowMs + 5_000L);

            RollbackPrediction(ledger);

            (long lastCastMs, long durationMs) = GetSpellCooldown("FIREBALL");
            Assert.That(lastCastMs, Is.EqualTo(nowMs + offsetMs + 5_000L),
                "rollback must not clear an entry it did not write");
            Assert.That(durationMs, Is.EqualTo(8_000L));
            Assert.That(RemainingCooldownMs("FIREBALL", nowMs + 5_000L), Is.EqualTo(8_000L));
        }

        [TestCase(-60_000L)]
        [TestCase(0L)]
        [TestCase(60_000L)]
        public void Rollback_LeavesAuthoritativeUpdateAlone(long offsetMs)
        {
            const long nowMs = 1_000_000L;
            SetClockOffset(nowMs, offsetMs);
            object prior = InsertAuthoritativeCooldown("FIREBALL", nowMs + offsetMs - 9_000L, 8_000L);
            object ledger = PredictActionStart("FIREBALL", 8_000L, false, 0L, nowMs);
            object updated = CreateAuthoritativeCooldown("FIREBALL", nowMs + offsetMs + 100L, 8_000L);
            InvokeInstance(_combat, "OnSpellCooldownUpdate", null!, prior, updated);

            RollbackPrediction(ledger);

            Assert.That(RemainingCooldownMs("FIREBALL", nowMs + 100L), Is.EqualTo(8_000L),
                "rejection must preserve a newer authoritative cooldown");
        }

        [Test]
        public void Rollback_LeavesOverwrittenGcdAlone()
        {
            const long nowMs = 10_000L;
            object ledger = PredictActionStart("FIREBALL", 0L, true, 1_500L, nowMs);

            // A later, longer GCD prediction wins before the rejection arrives.
            InvokeInstance(_combat, "PredictGlobalCooldown", nowMs + 500L, 2_000L);

            RollbackPrediction(ledger);

            Assert.That(IsGcdActive(nowMs + 1_600L), Is.True,
                "rollback must not clear a GCD it did not set");
        }

        [Test]
        public void Rollback_FiresDenialCueWithActionKind()
        {
            const long nowMs = 10_000L;
            object ledger = PredictActionStart("FIREBALL", 8_000L, true, 1_500L, nowMs);

            _lastPredictionRejectedKind = null;
            EventInfo cueEvent = RequireType(CombatStateType).GetEvent("PredictionRejected")
                ?? throw new InvalidOperationException("PredictionRejected event missing");
            Delegate handler = CreatePredictionRejectedHandler(
                cueEvent.EventHandlerType ?? throw new InvalidOperationException("PredictionRejected event handler type missing"));
            cueEvent.AddEventHandler(null, handler);
            try
            {
                RollbackPrediction(ledger);
            }
            finally
            {
                cueEvent.RemoveEventHandler(null, handler);
            }

            Assert.That(_lastPredictionRejectedKind, Is.EqualTo("FIREBALL"), "denial cue must report the action kind");
        }

        [Test]
        public void PredictActionStart_WithNoPredictions_RollbackIsNoOp()
        {
            const long nowMs = 10_000L;
            InvokeInstance(_combat, "PredictSpellCooldown", "FROSTBOLT", 4_000L, 3_000L);

            object ledger = PredictActionStart("FIREBALL", 0L, false, 0L, nowMs);
            RollbackPrediction(ledger);

            Assert.That(SpellCooldownsContains("FROSTBOLT"), Is.True);
            Assert.That(SpellCooldownsContains("FIREBALL"), Is.False);
        }

        // ---------------------------------------------------------------
        // Reflection plumbing (test asmdef cannot reference Assembly-CSharp)
        // ---------------------------------------------------------------

        private string? _lastPredictionRejectedKind;

        private static void InvokeClock(string methodName, params object[] args)
            => RequireType(ClockType).GetMethod(methodName)!.Invoke(null, args);

        private static void SetClockOffset(long clientNowMs, long offsetMs)
            => InvokeClock("RecordReducerSampleMs", clientNowMs - 100L, clientNowMs + offsetMs - 50L, clientNowMs);

        private object InsertAuthoritativeCooldown(string kind, long serverLastCastMs, long durationMs)
        {
            object row = CreateAuthoritativeCooldown(kind, serverLastCastMs, durationMs);
            InvokeInstance(_combat, "Bind", row.GetType().GetField("Caster")!.GetValue(row)!);
            InvokeInstance(_combat, "OnSpellCooldownInsert", null!, row);
            return row;
        }

        private static object CreateAuthoritativeCooldown(string kind, long serverLastCastMs, long durationMs)
        {
            Type rowType = RequireType("SpacetimeDB.Types.SpellCooldown");
            object identity = Activator.CreateInstance(rowType.GetField("Caster")!.FieldType)!;
            object timestamp = Activator.CreateInstance(rowType.GetField("LastCastAt")!.FieldType, serverLastCastMs * 1000L)!;
            return Activator.CreateInstance(rowType, $"LOCAL:{kind}", identity, kind, timestamp, (ulong)durationMs)!;
        }

        private long RemainingCooldownMs(string kind, long clientNowMs)
            => (long)InvokeInstance(_combat, "GetSpellCooldownRemainingMs", kind, clientNowMs)!;

        private Delegate CreatePredictionRejectedHandler(Type eventHandlerType)
        {
            MethodInfo invoke = eventHandlerType.GetMethod("Invoke")
                ?? throw new InvalidOperationException($"{eventHandlerType.FullName}.Invoke missing");
            ParameterExpression[] parameters = invoke.GetParameters()
                .Select(parameter => Expression.Parameter(parameter.ParameterType, parameter.Name))
                .ToArray();
            if (parameters.Length == 0 || parameters[0].Type != typeof(string))
                throw new InvalidOperationException("PredictionRejected must pass action kind as the first string argument.");

            Expression assignKind = Expression.Assign(
                Expression.Field(Expression.Constant(this), nameof(_lastPredictionRejectedKind)),
                parameters[0]);
            return Expression.Lambda(eventHandlerType, Expression.Block(assignKind, Expression.Empty()), parameters)
                .Compile();
        }

        private object PredictActionStart(
            string actionKind,
            long cooldownDurationMs,
            bool usesGlobalCooldown,
            long gcdDurationMs,
            long nowMs)
        {
            MethodInfo method = RequireType(CombatStateType).GetMethod("PredictActionStart")
                ?? throw new InvalidOperationException("PredictActionStart missing");
            // Entity null → resource reservation is skipped; cooldown and GCD
            // capture/rollback are the paths under test here.
            return method.Invoke(
                _combat,
                new object?[] { null, actionKind, cooldownDurationMs, usesGlobalCooldown, gcdDurationMs, string.Empty, 0f, nowMs })
                ?? throw new InvalidOperationException("PredictActionStart returned null");
        }

        private void RollbackPrediction(object ledger)
        {
            MethodInfo method = RequireType(CombatStateType).GetMethod("RollbackPrediction")
                ?? throw new InvalidOperationException("RollbackPrediction missing");
            object rejectReason = Enum.Parse(method.GetParameters()[1].ParameterType, "Unspecified");
            method.Invoke(_combat, new[] { ledger, rejectReason });
        }

        private bool IsGcdActive(long nowMs)
            => (bool)(InvokeInstance(_combat, "IsGlobalCooldownActive", nowMs) ?? false);

        private object SpellCooldowns()
            => RequireType(CombatStateType).GetProperty("SpellCooldowns")!.GetValue(_combat)!;

        private bool SpellCooldownsContains(string kind)
        {
            object cooldowns = SpellCooldowns();
            return (bool)cooldowns.GetType().GetMethod("ContainsKey")!.Invoke(cooldowns, new object[] { kind })!;
        }

        private (long lastCastMs, long durationMs) GetSpellCooldown(string kind)
        {
            object cooldowns = SpellCooldowns();
            PropertyInfo indexer = cooldowns.GetType().GetProperty("Item")
                ?? throw new InvalidOperationException("SpellCooldowns indexer missing");
            object entry = indexer.GetValue(cooldowns, new object[] { kind })
                ?? throw new InvalidOperationException($"cooldown entry {kind} missing");
            Type entryType = entry.GetType();
            long lastCastMs = (long)entryType.GetField("Item1")!.GetValue(entry)!;
            long durationMs = (long)entryType.GetField("Item2")!.GetValue(entry)!;
            return (lastCastMs, durationMs);
        }

        private static Type RequireType(string typeName)
            => RuntimeAssembly.GetType(typeName, throwOnError: true)!;

        private static object GetStaticProperty(string typeName, string propertyName)
            => RequireType(typeName).GetProperty(propertyName, BindingFlags.Public | BindingFlags.Static)!
                   .GetValue(null)
               ?? throw new InvalidOperationException($"Static property {typeName}.{propertyName} returned null.");

        private static object? InvokeInstance(object target, string methodName, params object[] args)
        {
            MethodInfo method = target.GetType().GetMethod(
                    methodName,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new InvalidOperationException($"Method {methodName} missing on {target.GetType().FullName}");
            return method.Invoke(target, args);
        }
    }
}
