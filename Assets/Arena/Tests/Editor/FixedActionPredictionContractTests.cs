#nullable enable

using System;
using System.Reflection;
using NUnit.Framework;

namespace Arena.Tests.Editor
{
    public sealed class FixedActionPredictionContractTests
    {
        private static readonly Assembly RuntimeAssembly = AppDomain.CurrentDomain.Load("Assembly-CSharp");

        [Test]
        public void RejectedDefenseResultClearsMatchingPredictedParry()
        {
            object token = Token("parry-token", 17UL, "PARRY");

            Assert.That(
                ShouldClearPredictedParryForResult(
                    DefenseResult(token, "Rejected"),
                    token,
                    predictedParryActive: true),
                Is.True);
        }

        [Test]
        public void AcceptedDefenseResultWaitsForAuthoritativeState()
        {
            object token = Token("parry-token", 17UL, "PARRY");

            Assert.That(
                ShouldClearPredictedParryForResult(
                    DefenseResult(token, "Accepted"),
                    token,
                    predictedParryActive: true),
                Is.False);
        }

        [Test]
        public void DefenseResultRequiresMatchingTokenAndActivePrediction()
        {
            object token = Token("parry-token", 17UL, "PARRY");
            object other = Token("other-token", 18UL, "PARRY");

            Assert.That(
                ShouldClearPredictedParryForResult(
                    DefenseResult(other, "Rejected"),
                    token,
                    predictedParryActive: true),
                Is.False);
            Assert.That(
                ShouldClearPredictedParryForResult(
                    DefenseResult(token, "Rejected"),
                    token,
                    predictedParryActive: false),
                Is.False);
        }

        [Test]
        public void AcceptedParryUsesLongSafetyTimeoutInsteadOfShortPredictionTimeout()
        {
            Assert.That(ShouldTimeoutPredictedParry(nowMs: 1149L, startedMs: 1000L, accepted: false), Is.False);
            Assert.That(ShouldTimeoutPredictedParry(nowMs: 1150L, startedMs: 1000L, accepted: false), Is.True);

            Assert.That(ShouldTimeoutPredictedParry(nowMs: 5999L, startedMs: 1000L, accepted: true), Is.False);
            Assert.That(ShouldTimeoutPredictedParry(nowMs: 6000L, startedMs: 1000L, accepted: true), Is.True);
        }

        private static object Token(string predictedActionId, ulong clientActionSeq, string kind)
        {
            Type tokenType = RuntimeType("Arena.Simulation.ActionPredictionToken");
            return Activator.CreateInstance(tokenType, predictedActionId, clientActionSeq, kind)!;
        }

        private static object DefenseResult(object token, string result)
        {
            Type resultType = RuntimeType("SpacetimeDB.Types.PredictedActionResult");
            Type familyType = RuntimeType("SpacetimeDB.Types.PredictedActionFamily");
            Type resultKindType = RuntimeType("SpacetimeDB.Types.ActionResultKind");

            object row = Activator.CreateInstance(resultType)!;
            Field(resultType, "Family").SetValue(row, Enum.Parse(familyType, "Defense"));
            Field(resultType, "PredictedActionId").SetValue(row, Property(token, "PredictedActionId").GetValue(token));
            Field(resultType, "ClientActionSeq").SetValue(row, Property(token, "ClientActionSeq").GetValue(token));
            Field(resultType, "Result").SetValue(row, Enum.Parse(resultKindType, result));
            return row;
        }

        private static bool ShouldClearPredictedParryForResult(
            object row,
            object token,
            bool predictedParryActive)
        {
            MethodInfo method = LocalDefensePredictionMethod(
                "ShouldClearPredictedParryForResult",
                RuntimeType("SpacetimeDB.Types.PredictedActionResult"),
                RuntimeType("Arena.Simulation.ActionPredictionToken"),
                typeof(bool));
            return (bool)method.Invoke(null, new object[] { row, token, predictedParryActive })!;
        }

        private static bool ShouldTimeoutPredictedParry(long nowMs, long startedMs, bool accepted)
        {
            MethodInfo method = LocalDefensePredictionMethod(
                "ShouldTimeoutPredictedParry",
                typeof(long),
                typeof(long),
                typeof(bool));
            return (bool)method.Invoke(null, new object[] { nowMs, startedMs, accepted })!;
        }

        private static MethodInfo LocalDefensePredictionMethod(string name, params Type[] parameters)
        {
            Type type = RuntimeType("Arena.Input.LocalDefensePrediction");
            return type.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic, null, parameters, null)
                ?? throw new InvalidOperationException($"{name} not found.");
        }

        private static Type RuntimeType(string fullName)
            => RuntimeAssembly.GetType(fullName)
               ?? throw new InvalidOperationException($"{fullName} not found in Assembly-CSharp.");

        private static FieldInfo Field(Type type, string name)
            => type.GetField(name, BindingFlags.Public | BindingFlags.Instance)
               ?? throw new InvalidOperationException($"{type.FullName}.{name} field not found.");

        private static PropertyInfo Property(object instance, string name)
            => instance.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
               ?? throw new InvalidOperationException($"{instance.GetType().FullName}.{name} property not found.");
    }
}
