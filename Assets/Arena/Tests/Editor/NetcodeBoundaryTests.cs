#nullable enable

using System;
using System.Collections;
using System.Reflection;
using System.Runtime.Serialization;
using NUnit.Framework;

namespace Arena.EditModeTests
{
    // These tests exercise managed correlation and callback gates without a scene,
    // GameObjects, a transport, or a running Unity Editor.
    public sealed class NetcodeBoundaryTests
    {
        private static readonly Assembly Runtime = AppDomain.CurrentDomain.Load("Assembly-CSharp");
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;

        [Test]
        public void RepeatedFireball_UnknownActionCannotConsumeAnotherCast()
        {
            object dispatcher = Dispatcher();
            AddPrediction(dispatcher, "second:2", long.MaxValue);
            Assert.That(Resolve(dispatcher, "unknown-action", "FIREBALL"), Is.False);
        }

        [Test]
        public void RepeatedFireball_AlreadyAdoptedActionCannotConsumeNextCast()
        {
            object dispatcher = Dispatcher();
            Tokens(dispatcher)["first-action"] = "first:1";
            AddPrediction(dispatcher, "second:2", long.MaxValue);
            Assert.That(Resolve(dispatcher, "first-action", "FIREBALL"), Is.False);
        }

        [Test]
        public void RepeatedFireball_ExpiredExactPredictionCannotFallThroughToNextCast()
        {
            object dispatcher = Dispatcher();
            Tokens(dispatcher)["first-action"] = "first:1";
            AddPrediction(dispatcher, "first:1", 0L);
            AddPrediction(dispatcher, "second:2", long.MaxValue);
            Assert.That(Resolve(dispatcher, "first-action", "FIREBALL"), Is.False);
        }

        [Test]
        public void RepeatedFireball_AcceptanceSelectsExactTokenDespiteInsertionOrder()
        {
            object dispatcher = Dispatcher();
            AddPrediction(dispatcher, "first:1", long.MaxValue);
            AddPrediction(dispatcher, "second:2", long.MaxValue);
            Call(dispatcher, "OnPredictedActionResultInsert", null, Acceptance("second-action", "second", 2));
            Assert.That(Resolve(dispatcher, "second-action", "FIREBALL", out object? pending), Is.True);
            Assert.That(pending!.GetType().GetProperty("TokenKey")!.GetValue(pending), Is.EqualTo("second:2"));
        }

        [Test]
        public void RepeatedFireball_CachedAcceptanceWorksBeforeItsCallback()
        {
            object dispatcher = Dispatcher();
            AddPrediction(dispatcher, "first:1", long.MaxValue);
            AddPrediction(dispatcher, "second:2", long.MaxValue);
            Array rows = Array.CreateInstance(Type("SpacetimeDB.Types.PredictedActionResult"), 2);
            rows.SetValue(Acceptance("first-action", "first", 1), 0);
            rows.SetValue(Acceptance("second-action", "second", 2), 1);
            Call(dispatcher, "RecordPredictedSpellVfxAcceptance", "second-action", rows);
            Assert.That(Resolve(dispatcher, "second-action", "FIREBALL", out object? pending), Is.True);
            Assert.That(pending!.GetType().GetProperty("TokenKey")!.GetValue(pending), Is.EqualTo("second:2"));
            Assert.That(Tokens(dispatcher).Contains("first-action"), Is.False);
            Assert.That(Resolve(dispatcher, "second-action", "FROSTBOLT"), Is.False);
        }

        [Test]
        public void RepeatedFireball_RejectedCachedResultCannotClaimPrediction()
        {
            object dispatcher = Dispatcher();
            AddPrediction(dispatcher, "first:1", long.MaxValue);
            object row = Acceptance("first-action", "first", 1);
            Set(row, "Result", Enum.Parse(Type("SpacetimeDB.Types.ActionResultKind"), "Rejected"));
            Array rows = Array.CreateInstance(row.GetType(), 1);
            rows.SetValue(row, 0);
            Call(dispatcher, "RecordPredictedSpellVfxAcceptance", "first-action", rows);
            Assert.That(Resolve(dispatcher, "first-action", "FIREBALL"), Is.False);
        }

        // Server epoch values deliberately lie decades either side of the PC's
        // current clock. Only server-to-server deadline ordering is meaningful.
        [TestCase(946684800000000L)]
        [TestCase(4102444800000000L)]
        public void Assignment_ServerClockOffsetDoesNotInvalidateReadyAssignment(long serverNow)
            => Assert.That(ValidateAssignment(serverNow, serverNow + 30_000_000L, "READY"), Is.True);

        [TestCase(null)]
        [TestCase(999_999L)]
        [TestCase(1_000_000L)]
        public void Assignment_MissingOrNonFutureServerDeadlineIsInvalid(long? expires)
            => Assert.That(ValidateAssignment(1_000_000L, expires, "READY"), Is.False);

        [Test]
        public void Assignment_ServerClosedTicketCannotBeRejoined()
            => Assert.That(ValidateAssignment(1_000_000L, 31_000_000L, "CLOSED"), Is.False);

        [Test]
        public void Assignment_ClockFixPreservesCredentialClusterGuard()
            => Assert.That(ValidateAssignment(1_000_000L, 31_000_000L, "READY", "ws://127.0.0.1:4000"), Is.False);

        private static bool ValidateAssignment(long serverNow, long? expires, string status, string serverUri = "ws://127.0.0.1:3000")
        {
            object snapshot = Activator.CreateInstance(Type("Arena.Network.HubMatchStatusSnapshot"), Instance, null,
                new object?[] { "ticket", "UNRANKED", "2V2", status, null, "match", serverUri,
                    new string('a', 64), "build", "ARENA_MAP_01", serverNow - 1000L, serverNow,
                    expires ?? 0L, serverNow, expires }, null)!;
            object?[] args = { snapshot, "ws://127.0.0.1:3000", null, null };
            return (bool)Type("Arena.Network.MatchAssignmentValidator").GetMethod("TryValidate", Static)!.Invoke(null, args)!;
        }

        [TestCase("OnScopedSubscriptionApplied")]
        [TestCase("OnScopedSubscriptionEnded")]
        [TestCase("OnScopedSubscriptionError")]
        public void Reconnect_DelayedScopeCallbackCannotMutateNewTransition(string callback)
        {
            object manager = Bare("Arena.Network.NetworkManager");
            Set(manager, "_scopeTransitionGeneration", 1);
            Call(manager, "ResetConnectionState");
            Set(manager, "_scopeTransitionGeneration", (int)Get(manager, "_scopeTransitionGeneration")! + 1);
            Assert.That(Get(manager, "_scopeTransitionGeneration"), Is.Not.EqualTo(1),
                "A replacement connection must not reuse the old callback's generation.");
            Set(manager, "_scopeTransitionInFlight", true);
            object oldScope = Type("Arena.Network.NetworkManager+GameplayScope").GetMethod("Instance", Static)!.Invoke(null, new object[] { 7UL })!;
            object?[] args = callback == "OnScopedSubscriptionError"
                ? new object?[] { oldScope, 1, new Exception("old connection") }
                : new object?[] { oldScope, 1 };
            Call(manager, callback, args);
            Assert.That(Get(manager, "_scopeTransitionInFlight"), Is.True);
            Assert.That(Get(manager, "_scopedSubscription"), Is.Null);
        }

        private static object Dispatcher()
        {
            object dispatcher = Bare("Arena.Presentation.CombatVFXDispatcher");
            foreach (string name in new[] { "_pendingSpellVfxByToken", "_spellVfxTokenByActionInstance" })
            {
                FieldInfo field = dispatcher.GetType().GetField(name, Instance)!;
                field.SetValue(dispatcher, Activator.CreateInstance(field.FieldType));
            }
            return dispatcher;
        }

        private static void AddPrediction(object dispatcher, string token, long expires)
        {
            Type pending = dispatcher.GetType().GetNestedType("PendingPredictedSpellVfx", BindingFlags.NonPublic)!;
            object value = Activator.CreateInstance(pending, Instance, null,
                new object[] { token, "FIREBALL", "predicted-" + token, "projectile-" + token, true, false, expires }, null)!;
            ((IDictionary)Get(dispatcher, "_pendingSpellVfxByToken")!)[token] = value;
        }

        private static object Acceptance(string action, string token, ulong seq)
        {
            object row = Activator.CreateInstance(Type("SpacetimeDB.Types.PredictedActionResult"))!;
            Set(row, "Family", Enum.Parse(Type("SpacetimeDB.Types.PredictedActionFamily"), "SpellCast"));
            Set(row, "Result", Enum.Parse(Type("SpacetimeDB.Types.ActionResultKind"), "Accepted"));
            Set(row, "ActionInstanceId", action);
            Set(row, "PredictedActionId", token);
            Set(row, "ClientActionSeq", seq);
            return row;
        }

        private static IDictionary Tokens(object dispatcher) => (IDictionary)Get(dispatcher, "_spellVfxTokenByActionInstance")!;
        private static bool Resolve(object dispatcher, string action, string spell) => Resolve(dispatcher, action, spell, out _);
        private static bool Resolve(object dispatcher, string action, string spell, out object? pending)
        {
            object?[] args = { action, spell, null };
            bool found = (bool)Call(dispatcher, "TryResolvePredictedSpellVfx", args)!;
            pending = args[2];
            return found;
        }

        private static Type Type(string name) => Runtime.GetType(name, true)!;
        private static object Bare(string name) => FormatterServices.GetUninitializedObject(Type(name));
        private static object? Get(object instance, string name) => instance.GetType().GetField(name, Instance)!.GetValue(instance);
        private static void Set(object instance, string name, object? value) => instance.GetType().GetField(name, Instance)!.SetValue(instance, value);
        private static object? Call(object instance, string name, params object?[] args) => instance.GetType().GetMethod(name, Instance)!.Invoke(instance, args);
    }
}
