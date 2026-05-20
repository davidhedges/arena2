#nullable enable

using System;
using System.Reflection;
using NUnit.Framework;

namespace Arena.Tests.Editor
{
    public sealed class LocalSpellPresentationStateMachineTests
    {
        private static readonly Assembly RuntimeAssembly = AppDomain.CurrentDomain.Load("Assembly-CSharp");

        [Test]
        public void StaleToken_DoesNotClearAuthoritativeHold()
        {
            object machine = CreateStateMachine();
            Invoke(machine, "Predict", PredictInput("ICICLE", "p1", 1UL));
            Invoke(machine, "PredictedActionResultInserted", Result("c1", "p1", 1UL, "accepted"));
            Invoke(machine, "ActiveCastInserted", ActiveCast("c1", "ICICLE"));

            object command = Invoke(machine, "PredictedActionResultInserted", Result("c1", "p1", 1UL, "stale_token"));

            Assert.That(CommandKind(command), Is.EqualTo("None"));
            Assert.That(State(machine), Is.EqualTo("AuthoritativeHold"));
            Assert.That(ActiveCast(machine), Is.Not.Null);
        }

        [Test]
        public void CancelThenRecastSameKind_OldActiveCastDoesNotConsumeNewPrediction()
        {
            object machine = CreateStateMachine();
            Invoke(machine, "Predict", PredictInput("ICICLE", "p1", 1UL));
            Assert.That(CommandKind(Invoke(machine, "LocalCancel", Token("p1", 1UL, "ICICLE"))), Is.EqualTo("RequestCancel"));

            Assert.That(CommandKind(Invoke(machine, "Predict", PredictInput("ICICLE", "p2", 2UL))), Is.EqualTo("StartHold"));
            Assert.That(CommandKind(Invoke(machine, "ActiveCastInserted", ActiveCast("c1", "ICICLE"))), Is.EqualTo("None"));
            Assert.That(State(machine), Is.EqualTo("PendingCorrelation"));
            Assert.That(HasPendingPrediction(machine), Is.True);

            Assert.That(CommandKind(Invoke(machine, "PredictedActionResultInserted", Result("c1", "p1", 1UL, "canceled"))), Is.EqualTo("None"));
            Assert.That(State(machine), Is.EqualTo("PredictedHold"));
            Assert.That(HasPendingPrediction(machine), Is.True);

            Invoke(machine, "PredictedActionResultInserted", Result("c2", "p2", 2UL, "accepted"));
            object command = Invoke(machine, "ActiveCastInserted", ActiveCast("c2", "ICICLE"));

            Assert.That(CommandKind(command), Is.EqualTo("None"));
            Assert.That(State(machine), Is.EqualTo("AuthoritativeHold"));
            Assert.That(HasPendingPrediction(machine), Is.False);
        }

        [Test]
        public void HappyPath_ConfirmedPredictionReleasesWithoutDeleteCancel()
        {
            object machine = CreateStateMachine();
            Assert.That(CommandKind(Invoke(machine, "Predict", PredictInput("ICICLE", "p1", 1UL))), Is.EqualTo("StartHold"));
            Invoke(machine, "PredictedActionResultInserted", Result("c1", "p1", 1UL, "accepted"));
            Assert.That(CommandKind(Invoke(machine, "ActiveCastInserted", ActiveCast("c1", "ICICLE"))), Is.EqualTo("None"));

            object release = Invoke(machine, "ScheduledReleaseDue", 1500L);
            Assert.That(CommandKind(release), Is.EqualTo("RequestRelease"));
            Assert.That(State(machine), Is.EqualTo("Released"));

            object deleted = Invoke(machine, "ActiveCastDeleted", "c1", "ICICLE");
            Assert.That(CommandKind(deleted), Is.EqualTo("None"));
            Assert.That(State(machine), Is.EqualTo("Terminal"));
        }

        [Test]
        public void ConfirmedPredictionKeepsLocalStartWhenAuthoritativeStartIsDelayed()
        {
            object machine = CreateStateMachine();
            Invoke(machine, "Predict", PredictInput("ICICLE", "p1", 1UL));

            object command = Invoke(machine, "ActiveCastInserted", ActiveCast(
                "c1",
                "ICICLE",
                1300L,
                2300L,
                "p1",
                1UL));

            Assert.That(CommandKind(command), Is.EqualTo("None"));
            object active = ActiveCast(machine) ?? throw new InvalidOperationException("Expected active cast.");
            Assert.That(Field(active.GetType(), "StartedAtMs").GetValue(active), Is.EqualTo(1000L));
            Assert.That(Field(active.GetType(), "EndsAtMs").GetValue(active), Is.EqualTo(2300L));

            Invoke(machine, "ActiveCastUpdated", ActiveCast(
                "c1",
                "ICICLE",
                1400L,
                2400L,
                "p1",
                1UL));
            active = ActiveCast(machine) ?? throw new InvalidOperationException("Expected active cast.");
            Assert.That(Field(active.GetType(), "StartedAtMs").GetValue(active), Is.EqualTo(1000L));
            Assert.That(Field(active.GetType(), "EndsAtMs").GetValue(active), Is.EqualTo(2400L));
        }

        [Test]
        public void CancelTooLate_ClearsActiveWithoutCancelCommand()
        {
            object machine = CreateStateMachine();
            Invoke(machine, "Predict", PredictInput("ICICLE", "p1", 1UL));
            Invoke(machine, "PredictedActionResultInserted", Result("c1", "p1", 1UL, "accepted"));
            Invoke(machine, "ActiveCastInserted", ActiveCast("c1", "ICICLE"));

            object command = Invoke(machine, "PredictedActionResultInserted", Result("c1", "p1", 1UL, "cancel_too_late"));

            Assert.That(CommandKind(command), Is.EqualTo("None"));
            Assert.That(State(machine), Is.EqualTo("Terminal"));
            Assert.That(ActiveCast(machine), Is.Null);
        }

        private static object CreateStateMachine()
            => Activator.CreateInstance(RequireRuntimeType("Arena.Presentation.LocalSpellPresentationStateMachine"))!;

        private static object PredictInput(string actionId, string predictedCastId, ulong seq)
        {
            Type type = RequireRuntimeType("Arena.Presentation.LocalSpellPresentationPredictInput");
            return Activator.CreateInstance(
                type,
                actionId,
                1000L,
                1750L,
                "target",
                Point(),
                Token(predictedCastId, seq, actionId))!;
        }

        private static object ActiveCast(
            string castId,
            string actionId,
            long startedAtMs = 1000L,
            long endsAtMs = 2000L,
            string predictedCastId = "",
            ulong clientActionSeq = 0UL)
        {
            Type type = RequireRuntimeType("Arena.Presentation.LocalSpellPresentationActiveCast");
            return Activator.CreateInstance(
                type,
                castId,
                actionId,
                "target",
                Point(),
                startedAtMs,
                endsAtMs,
                predictedCastId,
                clientActionSeq)!;
        }

        private static object Result(string actionInstanceId, string predictedCastId, ulong seq, string result)
        {
            Type type = RequireRuntimeType("Arena.Presentation.LocalSpellPresentationResult");
            return Activator.CreateInstance(type, actionInstanceId, predictedCastId, seq, result)!;
        }

        private static object Token(string predictedCastId, ulong seq, string actionId)
        {
            Type type = RequireRuntimeType("Arena.Simulation.CastActionToken");
            return Activator.CreateInstance(type, predictedCastId, seq, actionId)!;
        }

        private static object Point()
        {
            Type type = RequireRuntimeType("Arena.Presentation.LocalSpellPresentationPoint");
            return Activator.CreateInstance(type, 1f, 2f, 3f)!;
        }

        private static object Invoke(object instance, string methodName, params object[] args)
        {
            Type[] parameterTypes = Array.ConvertAll(args, arg => arg.GetType());
            return RequireMethod(instance.GetType(), methodName, parameterTypes).Invoke(instance, args)!;
        }

        private static string CommandKind(object command)
            => Field(command.GetType(), "Kind").GetValue(command)!.ToString()!;

        private static string State(object machine)
            => Property(machine.GetType(), "State").GetValue(machine)!.ToString()!;

        private static bool HasPendingPrediction(object machine)
            => (bool)Property(machine.GetType(), "HasPendingPrediction").GetValue(machine)!;

        private static object? ActiveCast(object machine)
            => Property(machine.GetType(), "ActiveCast").GetValue(machine);

        private static Type RequireRuntimeType(string fullName)
            => RuntimeAssembly.GetType(fullName, throwOnError: true)
               ?? throw new InvalidOperationException($"Type {fullName} not found in Assembly-CSharp.");

        private static MethodInfo RequireMethod(Type type, string name, params Type[] parameterTypes)
            => type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, parameterTypes, null)
               ?? throw new InvalidOperationException($"Method {type.FullName}.{name} not found.");

        private static PropertyInfo Property(Type type, string name)
            => type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
               ?? throw new InvalidOperationException($"Property {type.FullName}.{name} not found.");

        private static FieldInfo Field(Type type, string name)
            => type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
               ?? throw new InvalidOperationException($"Field {type.FullName}.{name} not found.");
    }
}
