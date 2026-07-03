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
        private static readonly Assembly RuntimeAssembly = AppDomain.CurrentDomain.Load("Assembly-CSharp");

        private object _combat = null!;

        [SetUp]
        public void SetUp()
        {
            _combat = GetStaticProperty(CombatStateType, "Instance");
            InvokeInstance(_combat, "ResetForTests");
        }

        [TearDown]
        public void TearDown()
        {
            InvokeInstance(_combat, "ResetForTests");
        }

        [Test]
        public void Rollback_RemovesPhantomCooldownAndGcd()
        {
            const long nowMs = 10_000L;
            object ledger = PredictActionStart("FIREBALL", 8_000L, true, 1_500L, nowMs);

            Assert.That(SpellCooldownsContains("FIREBALL"), Is.True, "press should predict the cooldown");
            Assert.That(IsGcdActive(nowMs + 100L), Is.True, "press should predict the GCD");

            RollbackPrediction(ledger);

            Assert.That(SpellCooldownsContains("FIREBALL"), Is.False,
                "rejected action must not leave a phantom cooldown entry");
            Assert.That(IsGcdActive(nowMs + 100L), Is.False,
                "rejected action must not leave a phantom GCD");
        }

        [Test]
        public void Rollback_RestoresPriorCooldownEntry()
        {
            const long nowMs = 10_000L;
            // An earlier (authoritative or predicted) cooldown entry exists.
            InvokeInstance(_combat, "PredictSpellCooldown", "FIREBALL", 4_000L, 3_000L);

            object ledger = PredictActionStart("FIREBALL", 8_000L, false, 0L, nowMs);
            RollbackPrediction(ledger);

            (long lastCastMs, long durationMs) = GetSpellCooldown("FIREBALL");
            Assert.That(lastCastMs, Is.EqualTo(4_000L), "rollback must restore the prior entry");
            Assert.That(durationMs, Is.EqualTo(3_000L), "rollback must restore the prior entry");
        }

        [Test]
        public void Rollback_LeavesOverwrittenCooldownAlone()
        {
            const long nowMs = 10_000L;
            object ledger = PredictActionStart("FIREBALL", 8_000L, false, 0L, nowMs);

            // A later legitimate prediction (or authoritative row) replaces the
            // entry before the rejection arrives.
            InvokeInstance(_combat, "PredictSpellCooldown", "FIREBALL", nowMs + 5_000L, 8_000L);

            RollbackPrediction(ledger);

            (long lastCastMs, long durationMs) = GetSpellCooldown("FIREBALL");
            Assert.That(lastCastMs, Is.EqualTo(nowMs + 5_000L),
                "rollback must not clear an entry it did not write");
            Assert.That(durationMs, Is.EqualTo(8_000L));
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
