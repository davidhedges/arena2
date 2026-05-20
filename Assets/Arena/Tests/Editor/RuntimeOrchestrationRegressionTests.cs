#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using NUnit.Framework;
using UnityEngine;

namespace Arena.Tests.Editor
{
    public sealed class RuntimeOrchestrationRegressionTests
    {
        private static readonly Assembly RuntimeAssembly = AppDomain.CurrentDomain.Load("Assembly-CSharp");
        private static readonly List<string> RehydrateOperations = new();

        [TearDown]
        public void TearDown()
        {
            ResetLocalCombatState();
            DestroyIfPresent("EntityRegistryTest");
            DestroyIfPresent("EntityRegistry");
            DestroyIfPresent("AnchorResolverAvatar");
            foreach (var player in UnityEngine.Object.FindObjectsByType<GameObject>(FindObjectsInactive.Exclude))
            {
                if (player != null && player.name.StartsWith("Player_", StringComparison.Ordinal))
                    UnityEngine.Object.DestroyImmediate(player);
            }
        }

        [Test]
        public void GameplayScope_FromPlayerWorld_MapsOpenWorldRows()
        {
            Type gameplayScopeType = RequireRuntimeType("Arena.Network.NetworkManager+GameplayScope");
            Type playerWorldType = RequireRuntimeType("SpacetimeDB.Types.PlayerWorld");
            object row = Activator.CreateInstance(playerWorldType, CreateIdentity(1), "OPEN", null, "Oasis_Day")!;

            object scope = RequireMethod(gameplayScopeType, "FromPlayerWorld", playerWorldType, typeof(string)).Invoke(null, new[] { row, null })!;

            Assert.That(scope.ToString(), Is.EqualTo("open-world Oasis_Day"));
        }

        [Test]
        public void GameplayScope_FromPlayerWorld_MapsInstanceRows()
        {
            Type gameplayScopeType = RequireRuntimeType("Arena.Network.NetworkManager+GameplayScope");
            Type playerWorldType = RequireRuntimeType("SpacetimeDB.Types.PlayerWorld");
            object row = Activator.CreateInstance(playerWorldType, CreateIdentity(1), "INSTANCE", 42UL, string.Empty)!;

            object scope = RequireMethod(gameplayScopeType, "FromPlayerWorld", playerWorldType, typeof(string)).Invoke(null, new[] { row, null })!;

            Assert.That(scope.ToString(), Is.EqualTo("instance 42"));
            Assert.That(RequireProperty(gameplayScopeType, "InstanceId").GetValue(scope), Is.EqualTo(42UL));
        }

        [Test]
        public void LocalWorldScopeResolver_MapsDeletedStateToNone()
        {
            Type resolverType = RequireRuntimeType("Arena.Entity.LocalWorldScopeResolver");
            Type playerWorldType = RequireRuntimeType("SpacetimeDB.Types.PlayerWorld");

            object scope = RequireMethod(resolverType, "Resolve", playerWorldType, typeof(string)).Invoke(null, new object?[] { null, "Oasis_Day" })!;

            Assert.That(scope.ToString(), Is.EqualTo("none"));
        }

        [Test]
        public void LocalWorldSceneDecider_PreservesTrainingExceptionAndTargetsExpectedScenes()
        {
            Type deciderType = RequireRuntimeType("Arena.Entity.LocalWorldSceneDecider");
            MethodInfo method = RequireMethod(deciderType, "DetermineTargetScene", typeof(string), typeof(ulong?), typeof(string), typeof(string));

            object? training = method.Invoke(null, new object?[] { "TrainingGround", 99UL, "Arena_VerdantStand_Blockout", "ArenaMatch" });
            object? characterCreation = method.Invoke(null, new object?[] { "CharacterCreation", null, "Arena_VerdantStand_Blockout", "ArenaMatch" });
            object? groundSlashDemo = method.Invoke(null, new object?[] { "VFXGraph_GroundSlash", null, "Arena_VerdantStand_Blockout", "ArenaMatch" });
            object? enterInstance = method.Invoke(null, new object?[] { "Arena_VerdantStand_Blockout", 99UL, "Arena_VerdantStand_Blockout", "ArenaMatch" });
            object? enterOpenWorld = method.Invoke(null, new object?[] { "ArenaMatch", null, "Arena_VerdantStand_Blockout", "ArenaMatch" });
            object? preserveLoadedOpenWorld = method.Invoke(null, new object?[] { "Oasis_Day", null, "Giant_Skeleton", "ArenaMatch" });

            Assert.That(training, Is.Null);
            Assert.That(characterCreation, Is.Null);
            Assert.That(groundSlashDemo, Is.Null);
            Assert.That(enterInstance, Is.EqualTo("ArenaMatch"));
            Assert.That(enterOpenWorld, Is.EqualTo("Arena_VerdantStand_Blockout"));
            Assert.That(preserveLoadedOpenWorld, Is.Null);
        }

        [Test]
        public void LocalWorldRuntimeCoordinator_PreferenceUpdateDoesNotLoadStaleAuthoritativeScene()
        {
            Type coordinatorType = RequireRuntimeType("Arena.Entity.LocalWorldRuntimeCoordinator");
            Type worldContextType = RequireRuntimeType("Arena.Input.LocalMovementWorldContext");
            Type playerWorldType = RequireRuntimeType("SpacetimeDB.Types.PlayerWorld");
            Type preferenceType = RequireRuntimeType("SpacetimeDB.Types.PlayerOpenWorldScene");
            object identity = CreateIdentity(1);
            object worldContext = Activator.CreateInstance(worldContextType)!;
            string activeScene = "Oasis_Day";
            var loadedScenes = new List<string>();
            object coordinator = CreateLocalWorldRuntimeCoordinator(
                coordinatorType,
                worldContext,
                () => activeScene,
                scene => loadedScenes.Add(scene));

            RequireMethod(coordinatorType, "SetLocalIdentity", identity.GetType()).Invoke(coordinator, new[] { identity });

            object currentWorld = Activator.CreateInstance(playerWorldType, identity, "OPEN", null, "Oasis_Day")!;
            RequireMethod(coordinatorType, "OnPlayerWorldInsert", playerWorldType).Invoke(coordinator, new[] { currentWorld });
            loadedScenes.Clear();

            activeScene = "Giant_Skeleton";
            object preferredScene = Activator.CreateInstance(preferenceType, identity, "Giant_Skeleton")!;
            RequireMethod(coordinatorType, "OnPlayerOpenWorldSceneUpdate", preferenceType).Invoke(coordinator, new[] { preferredScene });

            Assert.That(loadedScenes, Is.Empty);
        }

        [Test]
        public void LocalWorldRuntimeCoordinator_DoesNotLoadOpenWorldFromHub()
        {
            Type coordinatorType = RequireRuntimeType("Arena.Entity.LocalWorldRuntimeCoordinator");
            Type worldContextType = RequireRuntimeType("Arena.Input.LocalMovementWorldContext");
            Type playerWorldType = RequireRuntimeType("SpacetimeDB.Types.PlayerWorld");
            object identity = CreateIdentity(1);
            object worldContext = Activator.CreateInstance(worldContextType)!;
            var loadedScenes = new List<string>();
            object coordinator = CreateLocalWorldRuntimeCoordinator(
                coordinatorType,
                worldContext,
                () => "Hub",
                scene => loadedScenes.Add(scene));

            RequireMethod(coordinatorType, "SetLocalIdentity", identity.GetType()).Invoke(coordinator, new[] { identity });

            object currentWorld = Activator.CreateInstance(playerWorldType, identity, "OPEN", null, "Oasis_Day")!;
            RequireMethod(coordinatorType, "OnPlayerWorldInsert", playerWorldType).Invoke(coordinator, new[] { currentWorld });
            Assert.That(loadedScenes, Is.Empty);

            object requestedWorld = Activator.CreateInstance(playerWorldType, identity, "OPEN", null, "Giant_Skeleton")!;
            RequireMethod(coordinatorType, "OnPlayerWorldUpdate", playerWorldType).Invoke(coordinator, new[] { requestedWorld });
            Assert.That(loadedScenes, Is.Empty);
        }

        [Test]
        public void SpellCastPresentation_ReleaseStartUsesAuthoredOffsetInsideCastWindow()
        {
            Type controllerType = RequireRuntimeType("Arena.Presentation.SpellCastPresentationController");
            MethodInfo method = RequireMethod(controllerType, "ComputeReleaseStartMs", typeof(long), typeof(long), typeof(float));

            object? result = method.Invoke(null, new object[] { 1_000L, 2_200L, 0.3f });

            Assert.That(result, Is.EqualTo(1_900L));
        }

        [Test]
        public void SpellCastPresentation_ReleaseStartClampsAuthoredOffsetToScaledCastWindow()
        {
            Type controllerType = RequireRuntimeType("Arena.Presentation.SpellCastPresentationController");
            MethodInfo method = RequireMethod(controllerType, "ComputeReleaseStartMs", typeof(long), typeof(long), typeof(float));

            object? result = method.Invoke(null, new object[] { 1_000L, 1_200L, 0.75f });

            Assert.That(result, Is.EqualTo(1_000L));
        }

        [Test]
        public void SpellCastPresentation_ReleaseStartTreatsInvalidAuthoredOffsetsAsImmediateRelease()
        {
            Type controllerType = RequireRuntimeType("Arena.Presentation.SpellCastPresentationController");
            MethodInfo method = RequireMethod(controllerType, "ComputeReleaseStartMs", typeof(long), typeof(long), typeof(float));

            object? negative = method.Invoke(null, new object[] { 1_000L, 2_200L, -0.5f });
            object? nan = method.Invoke(null, new object[] { 1_000L, 2_200L, float.NaN });

            Assert.That(negative, Is.EqualTo(2_200L));
            Assert.That(nan, Is.EqualTo(2_200L));
        }

        [Test]
        public void GameplaySubscriptionPlanner_BuildsExpectedStaticAndLocalQueries()
        {
            Type plannerType = RequireRuntimeType("Arena.Network.GameplaySubscriptionPlanner");
            object localIdentity = CreateIdentity(1);

            string[] staticSql = (string[])RequireMethod(plannerType, "BuildStaticQuerySqls").Invoke(null, Array.Empty<object>())!;
            string[] localSql = (string[])RequireMethod(plannerType, "BuildLocalQuerySqls", localIdentity.GetType()).Invoke(null, new[] { localIdentity })!;

            Assert.That(staticSql, Has.Length.EqualTo(18));
            Assert.That(staticSql[0], Does.Contain("\"ability_catalog\""));
            Assert.That(staticSql[1], Does.Contain("\"action_presentation_catalog\""));
            Assert.That(staticSql[2], Does.Contain("\"combat_vfx_cue_catalog\""));
            Assert.That(staticSql[3], Does.Contain("\"combat_projectile_definition\""));
            Assert.That(staticSql[4], Does.Contain("\"combat_profile_catalog\""));
            Assert.That(staticSql[5], Does.Contain("\"combat_mode_catalog\""));
            Assert.That(staticSql[6], Does.Contain("\"fixed_action_binding_catalog\""));
            Assert.That(staticSql[7], Does.Contain("\"loadout_slot_catalog\""));
            Assert.That(staticSql[8], Does.Contain("\"spell_definition\""));
            Assert.That(staticSql[9], Does.Contain("\"melee_definition\""));
            Assert.That(staticSql[10], Does.Contain("\"melee_ability_catalog\""));
            Assert.That(staticSql[11], Does.Contain("\"melee_gap_close_catalog\""));
            Assert.That(staticSql[12], Does.Contain("\"melee_attack_modifier_catalog\""));
            Assert.That(staticSql[13], Does.Contain("\"auto_attack_catalog\""));
            Assert.That(staticSql[14], Does.Contain("\"resource_catalog\""));
            Assert.That(staticSql[15], Does.Contain("\"stat_scaling_catalog\""));
            Assert.That(staticSql[16], Does.Contain("\"arena_instance\""));
            Assert.That(staticSql[17], Does.Contain("\"class_catalog\""));

            Assert.That(localSql, Has.Length.EqualTo(12));
            Assert.That(localSql[0], Does.Contain("\"player_world\""));
            Assert.That(localSql[0], Does.Contain(localIdentity.ToString()!));
            Assert.That(localSql[1], Does.Contain("\"player_open_world_scene\""));
            Assert.That(localSql[2], Does.Contain("\"character_progression\""));
            Assert.That(localSql[3], Does.Contain("\"character_class_loadout_state\""));
            Assert.That(localSql[4], Does.Contain("\"character_appearance\""));
            Assert.That(localSql[5], Does.Contain("\"saved_spec\""));
            Assert.That(localSql[6], Does.Contain("\"saved_spec_stat_allocation\""));
            Assert.That(localSql[7], Does.Contain("\"saved_spec_slot_assignment\""));
            Assert.That(localSql[8], Does.Contain("\"global_cooldown\""));
            Assert.That(localSql[9], Does.Contain("\"spell_cooldown\""));
            Assert.That(localSql[10], Does.Contain("\"fixed_action_charge_state\""));
            Assert.That(localSql[11], Does.Contain("\"active_combat_mode\""));
        }

        [Test]
        public void GameplaySubscriptionPlanner_BuildsScopedOpenWorldQueriesWithoutMatchStats()
        {
            Type plannerType = RequireRuntimeType("Arena.Network.GameplaySubscriptionPlanner");
            Type gameplayScopeType = RequireRuntimeType("Arena.Network.NetworkManager+GameplayScope");
            object scope = RequireMethod(gameplayScopeType, "OpenWorld", typeof(string)).Invoke(null, new object[] { "Oasis_Day" })!;

            string[] sql = (string[])RequireMethod(plannerType, "BuildScopedQuerySqls", gameplayScopeType).Invoke(null, new[] { scope })!;
            string joined = string.Join("\n", sql);

            Assert.That(sql, Has.Length.EqualTo(14));
            Assert.That(joined, Does.Contain("\"player_world\".\"world_kind\" = 'OPEN'"));
            Assert.That(joined, Does.Contain("\"player_world\".\"open_world_scene_name\" = 'Oasis_Day'"));
            Assert.That(joined, Does.Contain(" JOIN "));
            Assert.That(joined, Does.Contain("\"character_appearance\""));
            Assert.That(joined, Does.Not.Contain(" IN (SELECT"));
            Assert.That(joined, Does.Not.Contain("\"match_participant_stats\""));
        }

        [Test]
        public void GameplaySubscriptionPlanner_BuildsScopedInstanceQueriesWithMatchStats()
        {
            Type plannerType = RequireRuntimeType("Arena.Network.GameplaySubscriptionPlanner");
            Type gameplayScopeType = RequireRuntimeType("Arena.Network.NetworkManager+GameplayScope");
            object scope = RequireMethod(gameplayScopeType, "Instance", typeof(ulong)).Invoke(null, new object[] { 42UL })!;

            string[] sql = (string[])RequireMethod(plannerType, "BuildScopedQuerySqls", gameplayScopeType).Invoke(null, new[] { scope })!;
            string joined = string.Join("\n", sql);

            Assert.That(sql, Has.Length.EqualTo(15));
            Assert.That(joined, Does.Contain("\"player_world\".\"world_kind\" = 'INSTANCE'"));
            Assert.That(joined, Does.Contain("\"player_world\".\"instance_id\" = 42"));
            Assert.That(joined, Does.Contain("\"character_appearance\""));
            Assert.That(joined, Does.Contain("\"match_participant_stats\""));
        }

        [Test]
        public void LocalCombatState_IgnoresRowsForNonLocalIdentity()
        {
            object state = GetLocalCombatState();
            Type stateType = state.GetType();
            object localId = CreateIdentity(1);
            object otherId = CreateIdentity(2);

            RequireMethod(stateType, "Bind", localId.GetType()).Invoke(state, new[] { localId });

            object spellCooldown = Activator.CreateInstance(
                RequireRuntimeType("SpacetimeDB.Types.SpellCooldown"),
                "OTHER:FIREBALL",
                otherId,
                "FIREBALL",
                CreateTimestamp(1_000L),
                900UL)!;
            object activeCast = Activator.CreateInstance(
                RequireRuntimeType("SpacetimeDB.Types.ActiveCast"),
                otherId,
                "cast-other",
                "WARRIOR_FIREBALL",
                "FIREBALL",
                string.Empty,
                1f,
                2f,
                3f,
                CreateTimestamp(2_000L),
                CreateTimestamp(3_500L),
                0U,
                0U,
                0U,
                string.Empty,
                0UL)!;

            RequireMethod(stateType, "OnSpellCooldownInsert", RequireRuntimeType("SpacetimeDB.Types.EventContext"), spellCooldown.GetType())
                .Invoke(state, new object?[] { null, spellCooldown });
            RequireMethod(stateType, "OnActiveCastInsert", RequireRuntimeType("SpacetimeDB.Types.EventContext"), activeCast.GetType())
                .Invoke(state, new object?[] { null, activeCast });

            Assert.That(GetCollectionCount(RequireProperty(stateType, "SpellCooldowns").GetValue(state)!), Is.EqualTo(0));
            Assert.That(RequireProperty(stateType, "ActiveCast").GetValue(state), Is.Null);
        }

        [Test]
        public void LocalCombatState_TracksAndClearsLocalCooldownAndCastRows()
        {
            object state = GetLocalCombatState();
            Type stateType = state.GetType();
            object localId = CreateIdentity(1);

            RequireMethod(stateType, "Bind", localId.GetType()).Invoke(state, new[] { localId });

            object spellCooldown = Activator.CreateInstance(
                RequireRuntimeType("SpacetimeDB.Types.SpellCooldown"),
                "LOCAL:FIREBALL",
                localId,
                "FIREBALL",
                CreateTimestamp(10_000L),
                1500UL)!;
            object activeCast = Activator.CreateInstance(
                RequireRuntimeType("SpacetimeDB.Types.ActiveCast"),
                localId,
                "cast-local",
                "WARRIOR_FIREBALL",
                "FIREBALL",
                "target",
                1f,
                0f,
                0f,
                CreateTimestamp(11_000L),
                CreateTimestamp(12_500L),
                0U,
                0U,
                0U,
                string.Empty,
                0UL)!;

            RequireMethod(stateType, "OnSpellCooldownInsert", RequireRuntimeType("SpacetimeDB.Types.EventContext"), spellCooldown.GetType())
                .Invoke(state, new object?[] { null, spellCooldown });
            RequireMethod(stateType, "OnActiveCastInsert", RequireRuntimeType("SpacetimeDB.Types.EventContext"), activeCast.GetType())
                .Invoke(state, new object?[] { null, activeCast });

            Assert.That(GetCollectionCount(RequireProperty(stateType, "SpellCooldowns").GetValue(state)!), Is.EqualTo(1));
            Assert.That(RequireProperty(stateType, "ActiveCast").GetValue(state), Is.Not.Null);

            RequireMethod(stateType, "OnSpellCooldownDelete", RequireRuntimeType("SpacetimeDB.Types.EventContext"), spellCooldown.GetType())
                .Invoke(state, new object?[] { null, spellCooldown });
            RequireMethod(stateType, "OnActiveCastDelete", RequireRuntimeType("SpacetimeDB.Types.EventContext"), activeCast.GetType())
                .Invoke(state, new object?[] { null, activeCast });

            Assert.That(GetCollectionCount(RequireProperty(stateType, "SpellCooldowns").GetValue(state)!), Is.EqualTo(0));
            Assert.That(RequireProperty(stateType, "ActiveCast").GetValue(state), Is.Null);
        }

        [Test]
        public void LocalCombatState_OptimisticGcdClearStillReconcilesFromServerRows()
        {
            object state = GetLocalCombatState();
            Type stateType = state.GetType();
            object localId = CreateIdentity(1);

            RequireMethod(stateType, "Bind", localId.GetType()).Invoke(state, new[] { localId });

            object firstGcd = Activator.CreateInstance(
                RequireRuntimeType("SpacetimeDB.Types.GlobalCooldown"),
                localId,
                CreateTimestamp(10_000L),
                1500UL)!;
            object secondGcd = Activator.CreateInstance(
                RequireRuntimeType("SpacetimeDB.Types.GlobalCooldown"),
                localId,
                CreateTimestamp(20_000L),
                900UL)!;

            RequireMethod(stateType, "OnGlobalCooldownInsert", RequireRuntimeType("SpacetimeDB.Types.EventContext"), firstGcd.GetType())
                .Invoke(state, new object?[] { null, firstGcd });
            Assert.That(RequireProperty(stateType, "GcdStartMs").GetValue(state), Is.EqualTo(10L));
            Assert.That(RequireProperty(stateType, "GcdDurationMs").GetValue(state), Is.EqualTo(1500L));

            RequireMethod(stateType, "ClearPredictedGlobalCooldown").Invoke(state, Array.Empty<object>());
            Assert.That(RequireProperty(stateType, "GcdStartMs").GetValue(state), Is.EqualTo(0L));
            Assert.That(RequireProperty(stateType, "GcdDurationMs").GetValue(state), Is.EqualTo(0L));

            RequireMethod(stateType, "OnGlobalCooldownUpdate", RequireRuntimeType("SpacetimeDB.Types.EventContext"), firstGcd.GetType(), secondGcd.GetType())
                .Invoke(state, new object?[] { null, firstGcd, secondGcd });
            Assert.That(RequireProperty(stateType, "GcdStartMs").GetValue(state), Is.EqualTo(20L));
            Assert.That(RequireProperty(stateType, "GcdDurationMs").GetValue(state), Is.EqualTo(900L));

            RequireMethod(stateType, "OnGlobalCooldownDelete", RequireRuntimeType("SpacetimeDB.Types.EventContext"), secondGcd.GetType())
                .Invoke(state, new object?[] { null, secondGcd });
            Assert.That(RequireProperty(stateType, "GcdStartMs").GetValue(state), Is.EqualTo(0L));
            Assert.That(RequireProperty(stateType, "GcdDurationMs").GetValue(state), Is.EqualTo(0L));
        }

        [Test]
        public void LocalCombatState_AuthoritativeActiveCastConfirmsPredictedCastBarWithoutRestarting()
        {
            object state = GetLocalCombatState();
            Type stateType = state.GetType();
            object localId = CreateIdentity(1);

            RequireMethod(stateType, "Bind", localId.GetType()).Invoke(state, new[] { localId });

            object token = RequireMethod(stateType, "CreateCastActionToken", typeof(string))
                .Invoke(state, new object[] { "ICICLE" })!;
            string predictedCastId = (string)RequireProperty(token.GetType(), "PredictedCastId").GetValue(token)!;
            ulong clientActionSeq = (ulong)RequireProperty(token.GetType(), "ClientActionSeq").GetValue(token)!;
            RequireMethod(stateType, "PredictCastBar", typeof(string), typeof(long), typeof(long), token.GetType())
                .Invoke(state, new object[] { "ICICLE", 1_000L, 1_000L, token });

            object activeCast = Activator.CreateInstance(
                RequireRuntimeType("SpacetimeDB.Types.ActiveCast"),
                localId,
                "cast-local",
                "WARRIOR_ICICLE",
                "ICICLE",
                "target",
                0f,
                0f,
                1f,
                CreateTimestamp(1_300_000L),
                CreateTimestamp(2_300_000L),
                50U,
                0U,
                0U,
                predictedCastId,
                clientActionSeq)!;

            RequireMethod(stateType, "OnActiveCastInsert", RequireRuntimeType("SpacetimeDB.Types.EventContext"), activeCast.GetType())
                .Invoke(state, new object?[] { null, activeCast });

            object? snapshot = RequireMethod(stateType, "CurrentCastBar", typeof(long))
                .Invoke(state, new object[] { 1_400L });
            Assert.That(snapshot, Is.Not.Null);

            Type snapshotType = snapshot!.GetType();
            Assert.That(RequireProperty(snapshotType, "StartMs").GetValue(snapshot), Is.EqualTo(1_000L));
            Assert.That(RequireProperty(snapshotType, "EndMs").GetValue(snapshot), Is.EqualTo(2_300L));
            Assert.That(RequireProperty(snapshotType, "Kind").GetValue(snapshot), Is.EqualTo("ICICLE"));

            object? expiredSnapshot = RequireMethod(stateType, "CurrentCastBar", typeof(long))
                .Invoke(state, new object[] { 2_301L });
            Assert.That(expiredSnapshot, Is.Null);
        }

        [Test]
        public void LocalCombatState_PredictedGcdSelfCancelPolicyOnlyAllowsNormalCastTimeSpells()
        {
            Type stateType = RequireRuntimeType("Arena.Simulation.LocalCombatState");
            MethodInfo policy = RequireMethod(
                stateType,
                "ShouldClearPredictedGlobalCooldownForSelfCancel",
                typeof(ulong),
                typeof(string),
                typeof(bool));

            Assert.That(policy.Invoke(null, new object[] { 1200UL, "PROJECTILE", false }), Is.EqualTo(true));
            Assert.That(policy.Invoke(null, new object[] { 0UL, "PROJECTILE", false }), Is.EqualTo(false));
            Assert.That(policy.Invoke(null, new object[] { 1200UL, "CHANNEL", false }), Is.EqualTo(false));
            Assert.That(policy.Invoke(null, new object[] { 1200UL, "INSTANT_BEAM", false }), Is.EqualTo(false));
            Assert.That(policy.Invoke(null, new object[] { 1200UL, "CHARGE", false }), Is.EqualTo(false));
            Assert.That(policy.Invoke(null, new object[] { 1200UL, "PROJECTILE", true }), Is.EqualTo(false));
        }

        [Test]
        public void ScopedPlayerCacheHydrator_TracksScopedCacheMembershipWithoutPlayerWorld()
        {
            Type hydratorType = RequireRuntimeType("Arena.Entity.ScopedPlayerCacheHydrator");
            Type snapshotType = RequireRuntimeType("Arena.Entity.ScopedPlayerCacheSnapshot");
            Type playerPhysicsType = RequireRuntimeType("SpacetimeDB.Types.PlayerPhysics");
            object trackedIdentity = CreateIdentity(1);
            object missingIdentity = CreateIdentity(2);
            object hydrator = Activator.CreateInstance(hydratorType)!;
            object snapshot = Activator.CreateInstance(snapshotType)!;

            SetField(
                snapshot,
                "PlayerPhysicsRows",
                CreateArray(playerPhysicsType, CreatePlayerPhysics(trackedIdentity, 0f, 0f, 0f)));

            bool tracked = (bool)RequireMethod(hydratorType, "IsIdentityTrackedInScopedCache", snapshotType, trackedIdentity.GetType())
                .Invoke(hydrator, new[] { snapshot, trackedIdentity })!;
            bool missing = (bool)RequireMethod(hydratorType, "IsIdentityTrackedInScopedCache", snapshotType, trackedIdentity.GetType())
                .Invoke(hydrator, new[] { snapshot, missingIdentity })!;

            Assert.That(tracked, Is.True);
            Assert.That(missing, Is.False);
        }

        [Test]
        public void ScopedPlayerCacheHydrator_RehydrateClearsBeforeSpawningPhysicsRows()
        {
            Type hydratorType = RequireRuntimeType("Arena.Entity.ScopedPlayerCacheHydrator");
            Type snapshotType = RequireRuntimeType("Arena.Entity.ScopedPlayerCacheSnapshot");
            Type playerPhysicsType = RequireRuntimeType("SpacetimeDB.Types.PlayerPhysics");
            object hydrator = Activator.CreateInstance(hydratorType)!;
            object snapshot = Activator.CreateInstance(snapshotType)!;

            object first = CreatePlayerPhysics(CreateIdentity(1), 1f, 2f, 3f);
            object second = CreatePlayerPhysics(CreateIdentity(2), 4f, 5f, 6f);
            SetField(snapshot, "PlayerPhysicsRows", CreateArray(playerPhysicsType, first, second));

            RehydrateOperations.Clear();
            Action clearAction = RecordClear;
            Delegate spawnAction = CreateGenericAction(playerPhysicsType, nameof(RecordSpawnGeneric));

            RequireMethod(hydratorType, "RehydratePlayersFromScopedCache", snapshotType, typeof(Action), spawnAction.GetType())
                .Invoke(hydrator, new object[] { snapshot, clearAction, spawnAction });

            Assert.That(RehydrateOperations, Is.EqualTo(new[]
            {
                "clear",
                $"spawn:{GetIdentityText(first, "Identity")}",
                $"spawn:{GetIdentityText(second, "Identity")}",
            }));
        }

        [Test]
        public void EntityRegistry_IsIdentityVisible_ReturnsTrueForLiveEntityWithoutConnection()
        {
            Type entityRegistryType = RequireRuntimeType("Arena.Entity.EntityRegistry");
            Type playerEntityType = RequireRuntimeType("Arena.Entity.PlayerEntity");
            object identity = CreateIdentity(1);
            GameObject go = new("EntityRegistryTest");
            Component registry = go.AddComponent(entityRegistryType);
            object playerEntity = FormatterServices.GetUninitializedObject(playerEntityType);

            AddToPrivateDictionary(registry, "_players", identity, playerEntity);

            bool visible = (bool)RequireMethod(entityRegistryType, "IsIdentityVisible", identity.GetType())
                .Invoke(registry, new[] { identity })!;

            Assert.That(visible, Is.True);
        }

        [Test]
        public void EntityRegistry_IsIdentityVisible_ReturnsFalseWithoutEntityOrScopedCache()
        {
            Type entityRegistryType = RequireRuntimeType("Arena.Entity.EntityRegistry");
            object identity = CreateIdentity(1);
            GameObject go = new("EntityRegistryTest");
            Component registry = go.AddComponent(entityRegistryType);

            bool visible = (bool)RequireMethod(entityRegistryType, "IsIdentityVisible", identity.GetType())
                .Invoke(registry, new[] { identity })!;

            Assert.That(visible, Is.False);
        }

        [Test]
        public void CombatVFXAnchorResolver_ResolvesWeaponMountThroughEntityRegistry()
        {
            Type entityRegistryType = RequireRuntimeType("Arena.Entity.EntityRegistry");
            Type playerEntityType = RequireRuntimeType("Arena.Entity.PlayerEntity");
            Type mountsType = RequireRuntimeType("Arena.Presentation.AvatarWeaponMounts");
            Type bindingType = RequireRuntimeType("Arena.Presentation.Appearance.RuntimeAvatarBinding");
            object identity = CreateIdentity(1);

            GameObject registryGo = new("EntityRegistryTest");
            Component registry = registryGo.AddComponent(entityRegistryType);
            object playerEntity = Activator.CreateInstance(playerEntityType, identity, false, null)!;

            GameObject avatarRoot = new("AnchorResolverAvatar");
            Component mounts = avatarRoot.AddComponent(mountsType);
            Transform mainHandMount = new GameObject("MainWeaponMount").transform;
            mainHandMount.position = new Vector3(3f, 4f, 5f);
            mainHandMount.SetParent(avatarRoot.transform, true);
            RequireMethod(mountsType, "SetOrReplaceMount", typeof(string), typeof(Transform))
                .Invoke(mounts, new object[] { "main_weapon_hand", mainHandMount });

            object binding = Activator.CreateInstance(
                bindingType,
                avatarRoot,
                null,
                mounts,
                Array.Empty<Renderer>(),
                "test-avatar")!;
            SetField(playerEntity, "_avatarBinding", binding);
            AddToPrivateDictionary(registry, "_players", identity, playerEntity);

            Vector3 resolved = ResolveCombatVfxAnchorPosition(
                identity,
                "WEAPON_MAIN_HAND",
                new Vector3(10f, 0f, 0f),
                new Vector3(0f, 0f, 10f));

            Assert.That(resolved, Is.EqualTo(mainHandMount.position));
        }

        [Test]
        public void CombatVFXAnchorResolver_UsesAuthoredFallbackPositionsWhenEntityMissing()
        {
            object identity = CreateIdentity(1);
            Vector3 origin = new(10f, 0f, 0f);
            Vector3 impact = new(0f, 0f, 10f);

            Assert.That(ResolveCombatVfxAnchorPosition(identity, "CASTER", origin, impact), Is.EqualTo(origin));
            Assert.That(ResolveCombatVfxAnchorPosition(identity, "WEAPON_MAIN_HAND", origin, impact), Is.EqualTo(origin));
            Assert.That(ResolveCombatVfxAnchorPosition(identity, "IMPACT_POINT", origin, impact), Is.EqualTo(impact));
            Assert.That(ResolveCombatVfxAnchorPosition(identity, "GROUND_UNDER_CASTER", origin, impact), Is.EqualTo(origin + Vector3.up * 0.03f));
        }

        [Test]
        public void CombatVfxCueResolver_AbilityOverrideReplacesOnlyMatchingAnchoredSlot()
        {
            object spellLeft = CreateCombatVfxCue(
                "spell-left",
                "SPELL",
                "FIREBALL",
                "SPELL_CAST",
                "LEFT_HAND",
                "SPELL_LEFT",
                "FOLLOW_ANCHOR",
                "ATTACHED",
                0,
                20U);
            object spellRight = CreateCombatVfxCue(
                "spell-right",
                "SPELL",
                "FIREBALL",
                "SPELL_CAST",
                "RIGHT_HAND",
                "SPELL_RIGHT",
                "FOLLOW_ANCHOR",
                "ATTACHED",
                0,
                30U);
            object abilityLeft = CreateCombatVfxCue(
                "ability-left",
                "ABILITY",
                "WARRIOR_FIREBALL",
                "SPELL_CAST",
                "LEFT_HAND",
                "ABILITY_LEFT",
                "FOLLOW_ANCHOR",
                "ATTACHED",
                0,
                10U);

            string[] vfxIds = ResolveCombatVfxCueIds(spellLeft, spellRight, abilityLeft);

            Assert.That(vfxIds, Is.EqualTo(new[] { "ABILITY_LEFT", "SPELL_RIGHT" }));
        }

        [Test]
        public void CombatVfxCueResolver_ProjectileBodyOverrideKeysBySequence()
        {
            object spellProjectile = CreateCombatVfxCue(
                "spell-projectile",
                "SPELL",
                "FIREBALL",
                "SPELL_RELEASE",
                "LEFT_HAND",
                "SPELL_PROJECTILE",
                "SPAWN_WORLD",
                "PROJECTILE_BODY",
                0,
                20U);
            object abilityProjectile = CreateCombatVfxCue(
                "ability-projectile",
                "ABILITY",
                "WARRIOR_FIREBALL",
                "SPELL_RELEASE",
                "RIGHT_HAND",
                "ABILITY_PROJECTILE",
                "SPAWN_WORLD",
                "PROJECTILE_BODY",
                0,
                10U);

            string[] vfxIds = ResolveCombatVfxCueIds(
                "SPELL_RELEASE",
                spellProjectile,
                abilityProjectile);

            Assert.That(vfxIds, Is.EqualTo(new[] { "ABILITY_PROJECTILE" }));
        }

        [Test]
        public void CombatVfxCueResolver_MatchesAbilityScopedMeleeImpactCue()
        {
            object earthshatterCue = CreateCombatVfxCue(
                "earthshatter-impact",
                "ABILITY",
                "WARRIOR_EARTHSHATTER",
                "MELEE_IMPACT",
                "GROUND_UNDER_TARGET",
                "VFX_FIRE_AREA_BURST_01_ARENA",
                "SPAWN_WORLD",
                "ONE_SHOT",
                0,
                10U);

            string[] vfxIds = ResolveCombatVfxCueIds(
                isSpell: false,
                trigger: "MELEE_IMPACT",
                spellId: string.Empty,
                abilityId: "WARRIOR_EARTHSHATTER",
                strikeId: "COMBO_ATTACK_3_4_AIR_TO_GROUND",
                hitIndex: 0,
                earthshatterCue);

            Assert.That(vfxIds, Is.EqualTo(new[] { "VFX_FIRE_AREA_BURST_01_ARENA" }));
        }

        private static object GetLocalCombatState()
        {
            Type stateType = RequireRuntimeType("Arena.Simulation.LocalCombatState");
            object state = RequireProperty(stateType, "Instance").GetValue(null)
                ?? throw new InvalidOperationException("LocalCombatState.Instance returned null.");
            RequireMethod(stateType, "ResetForTests").Invoke(state, Array.Empty<object>());
            return state;
        }

        private static void ResetLocalCombatState()
        {
            Type stateType = RuntimeAssembly.GetType("Arena.Simulation.LocalCombatState", throwOnError: false)!;
            if (stateType == null)
                return;

            object? state = stateType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            stateType.GetMethod("ResetForTests", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                ?.Invoke(state, Array.Empty<object>());
        }

        private static object CreateIdentity(int discriminator)
        {
            string hex = discriminator.ToString("x2").PadLeft(64, '0');
            Type identityType = RequireLoadedType("SpacetimeDB.Identity");
            return RequireMethod(identityType, "FromHexString", typeof(string)).Invoke(null, new object[] { hex })!;
        }

        private static object CreateTimestamp(long micros)
            => Activator.CreateInstance(RequireLoadedType("SpacetimeDB.Timestamp"), micros)
               ?? throw new InvalidOperationException("Failed to create Timestamp.");

        private static object CreatePlayerPhysics(object identity, float x, float y, float z)
        {
            Type playerPhysicsType = RequireRuntimeType("SpacetimeDB.Types.PlayerPhysics");
            return Activator.CreateInstance(
                       playerPhysicsType,
                       identity,
                       x,
                       y,
                       z,
                       0f,
                       0f,
                       0f,
                       0f,
                       true,
                       0U,
                       CreateTimestamp(0L))
                   ?? throw new InvalidOperationException("Failed to create PlayerPhysics.");
        }

        private static Array CreateArray(Type elementType, params object[] values)
        {
            Array array = Array.CreateInstance(elementType, values.Length);
            for (int i = 0; i < values.Length; i++)
                array.SetValue(values[i], i);
            return array;
        }

        private static object CreateLocalWorldRuntimeCoordinator(
            Type coordinatorType,
            object worldContext,
            Func<string> getActiveSceneName,
            Action<string> loadScene)
        {
            ConstructorInfo constructor = coordinatorType
                .GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Single(ctor => ctor.GetParameters().Length == 6);
            return constructor.Invoke(new object?[]
            {
                worldContext,
                null,
                null,
                getActiveSceneName,
                loadScene,
                "ArenaMatch",
            });
        }

        private static int GetCollectionCount(object value)
            => (int)(value.GetType().GetProperty("Count", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(value)
                ?? throw new InvalidOperationException($"Count property missing on {value.GetType().FullName}."));

        private static void AddToPrivateDictionary(Component target, string fieldName, object key, object value)
        {
            object dictionary = target.GetType()
                .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(target)!;
            dictionary.GetType().GetMethod("Add")!.Invoke(dictionary, new[] { key, value });
        }

        private static string GetIdentityText(object row, string fieldName)
            => row.GetType().GetField(fieldName)!.GetValue(row)!.ToString()!;

        private static void SetField(object target, string fieldName, object value)
            => target.GetType().GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(target, value);

        private static Vector3 ResolveCombatVfxAnchorPosition(
            object identity,
            string anchor,
            Vector3 origin,
            Vector3 point)
        {
            Type cueType = RequireRuntimeType("SpacetimeDB.Types.CombatVfxCueCatalog");
            Type factType = RequireRuntimeType("Arena.Presentation.CombatVfxAnchorFact");
            Type resolverType = RequireRuntimeType("Arena.Presentation.CombatVFXAnchorResolver");
            object cue = Activator.CreateInstance(
                cueType,
                "test-cue",
                "ABILITY",
                "TEST",
                "SPELL_CAST",
                -1,
                anchor,
                "TEST_TEMPLATE",
                "SPAWN_WORLD",
                "ONE_SHOT",
                "DURATION",
                0,
                0UL,
                1f,
                100UL,
                0U)!;
            object fact = Activator.CreateInstance(factType, identity, identity, origin, point)!;

            return (Vector3)RequireMethod(resolverType, "ResolvePosition", factType, cueType)
                .Invoke(null, new[] { fact, cue })!;
        }

        private static object CreateCombatVfxCue(
            string key,
            string ownerKind,
            string ownerId,
            string trigger,
            string anchor,
            string vfxId,
            string attachMode,
            string vfxRole,
            int projectileSequenceIndex,
            uint sortOrder)
        {
            Type cueType = RequireRuntimeType("SpacetimeDB.Types.CombatVfxCueCatalog");
            return Activator.CreateInstance(
                cueType,
                key,
                ownerKind,
                ownerId,
                trigger,
                -1,
                anchor,
                vfxId,
                attachMode,
                vfxRole,
                "DURATION",
                projectileSequenceIndex,
                0UL,
                1f,
                100UL,
                sortOrder)!;
        }

        private static string[] ResolveCombatVfxCueIds(params object[] cues)
            => ResolveCombatVfxCueIds("SPELL_CAST", cues);

        private static string[] ResolveCombatVfxCueIds(string trigger, params object[] cues)
            => ResolveCombatVfxCueIds(
                isSpell: true,
                trigger,
                spellId: "FIREBALL",
                abilityId: "WARRIOR_FIREBALL",
                strikeId: string.Empty,
                hitIndex: -1,
                cues);

        private static string[] ResolveCombatVfxCueIds(
            bool isSpell,
            string trigger,
            string spellId,
            string abilityId,
            string strikeId,
            int hitIndex,
            params object[] cues)
        {
            Type cueType = RequireRuntimeType("SpacetimeDB.Types.CombatVfxCueCatalog");
            Type factType = RequireRuntimeType("Arena.Presentation.CombatVfxResolutionFact");
            Type resolverType = RequireRuntimeType("Arena.Presentation.CombatVfxCueResolver");
            object fact = Activator.CreateInstance(
                factType,
                isSpell,
                trigger,
                spellId,
                abilityId,
                strikeId,
                hitIndex)!;
            Type listType = typeof(List<>).MakeGenericType(cueType);
            object output = Activator.CreateInstance(listType)!;

            RequireMethod(
                    resolverType,
                    "Resolve",
                    typeof(IEnumerable<>).MakeGenericType(cueType),
                    factType,
                    listType)
                .Invoke(null, new[] { CreateArray(cueType, cues), fact, output });

            var result = new List<string>();
            foreach (object cue in (IEnumerable)output)
                result.Add((string)cueType.GetField("VfxId")!.GetValue(cue)!);
            return result.ToArray();
        }

        private static Type RequireRuntimeType(string fullName)
            => RuntimeAssembly.GetType(fullName, throwOnError: true)
               ?? throw new InvalidOperationException($"Type {fullName} not found in Assembly-CSharp.");

        private static Type RequireLoadedType(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type? type = assembly.GetType(fullName, throwOnError: false);
                if (type != null)
                    return type;
            }

            throw new InvalidOperationException($"Type {fullName} not found in loaded assemblies.");
        }

        private static MethodInfo RequireMethod(Type type, string name, params Type[] parameterTypes)
            => type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static, null, parameterTypes, null)
               ?? throw new InvalidOperationException($"Method {type.FullName}.{name} not found.");

        private static PropertyInfo RequireProperty(Type type, string name)
            => type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
               ?? throw new InvalidOperationException($"Property {type.FullName}.{name} not found.");

        private static void DestroyIfPresent(string objectName)
        {
            GameObject? go = GameObject.Find(objectName);
            if (go != null)
                UnityEngine.Object.DestroyImmediate(go);
        }

        private static Delegate CreateGenericAction(Type argumentType, string methodName)
        {
            MethodInfo method = typeof(RuntimeOrchestrationRegressionTests)
                .GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic)!
                .MakeGenericMethod(argumentType);
            return Delegate.CreateDelegate(typeof(Action<>).MakeGenericType(argumentType), method);
        }

        private static void RecordClear()
        {
            RehydrateOperations.Add("clear");
        }

        private static void RecordSpawnGeneric<T>(T row)
        {
            object? value = row;
            if (value == null)
                throw new InvalidOperationException("Cannot record null spawn row.");

            RehydrateOperations.Add($"spawn:{GetIdentityText(value, "Identity")}");
        }
    }
}
