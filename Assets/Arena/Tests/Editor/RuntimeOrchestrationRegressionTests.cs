#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
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
        public void NetworkCredentials_AreClusterScopedAcrossHubAndMatchSpellings()
        {
            Type configType = RequireRuntimeType("Arena.Network.NetworkEnvironmentConfig");
            MethodInfo scopeForServer = RequireMethod(
                configType,
                "CredentialScopeForServer",
                typeof(string));

            string localhostWs = (string)scopeForServer.Invoke(null, new object[]
            {
                "ws://localhost:3000",
            })!;
            string loopbackHttp = (string)scopeForServer.Invoke(null, new object[]
            {
                "http://127.0.0.1:3000",
            })!;
            string otherPort = (string)scopeForServer.Invoke(null, new object[]
            {
                "ws://localhost:3001",
            })!;

            Assert.That(localhostWs, Is.EqualTo(loopbackHttp));
            Assert.That(otherPort, Is.Not.EqualTo(localhostWs));
        }

        [Test]
        public void UnityMatchHandoff_ValidatesAssignmentBeforeSendingHostToken()
        {
            string environment = File.ReadAllText(
                "Assets/Arena/Runtime/Network/NetworkEnvironmentConfig.cs");
            string hub = File.ReadAllText(
                "Assets/Arena/Runtime/Network/HubNetworkManager.cs");
            string handoff = File.ReadAllText(
                "Assets/Arena/Runtime/Network/MatchHandoffCoordinator.cs");
            string match = File.ReadAllText(
                "Assets/Arena/Runtime/Network/NetworkManager.cs");

            Assert.That(environment, Does.Contain("cluster|{CredentialScopeForServer(endpoint.ServerUri)}"));
            Assert.That(environment, Does.Contain("LegacyCredentialAccounts(endpoint)"));
            Assert.That(hub, Does.Contain("From.MyHubPlayer().ToSql()"));
            Assert.That(hub, Does.Contain("From.MyMatchStatus().ToSql()"));
            Assert.That(hub, Does.Contain("Guid.NewGuid().ToString(\"N\")"));
            Assert.That(handoff, Does.Contain("MatchAssignmentValidator.TryValidate("));
            Assert.That(handoff, Does.Contain("CredentialScopeForServer(status.ServerUri)"));
            Assert.That(handoff, Does.Contain("_hub.DisconnectForMatchHandoff()"));
            Assert.That(match, Does.Contain("identity != _expectedProvisionedIdentity"));
            Assert.That(match, Does.Contain("ProvisionedMatchReady?.Invoke(_localIdentity)"));
        }

        [Test]
        public void MatchStartupTiming_CoversRequestProvisioningConnectionAndSceneLoad()
        {
            string hub = File.ReadAllText(
                "Assets/Arena/Runtime/Network/HubNetworkManager.cs");
            string handoff = File.ReadAllText(
                "Assets/Arena/Runtime/Network/MatchHandoffCoordinator.cs");
            string match = File.ReadAllText(
                "Assets/Arena/Runtime/Network/NetworkManager.cs");

            Assert.That(hub, Does.Contain("row.CreatedAt.MicrosecondsSinceUnixEpoch"));
            Assert.That(hub, Does.Contain("row.ReadyAt?.MicrosecondsSinceUnixEpoch"));
            Assert.That(handoff, Does.Contain("[MatchStartupTiming]"));
            Assert.That(handoff, Does.Contain("BeginRequest()"));
            Assert.That(handoff, Does.Contain("ObserveHubStatus(status)"));
            Assert.That(handoff, Does.Contain("CompleteSceneLoad()"));
            Assert.That(match, Does.Contain("Record(\"match_transport_connected\")"));
            Assert.That(match, Does.Contain("Record(\"initial_subscription_started\")"));
            Assert.That(match, Does.Contain("Record(\"initial_subscription_applied\")"));
            Assert.That(match, Does.Contain("\"pvp_contracts_validated\""));
            Assert.That(match, Does.Contain("Record(\"initial_state_accepted\")"));
        }

        [Test]
        public void GameplayScope_FromPlayerWorld_MapsOpenWorldRows()
        {
            Type gameplayScopeType = RequireRuntimeType("Arena.Network.NetworkManager+GameplayScope");
            Type playerWorldType = RequireRuntimeType("SpacetimeDB.Types.PlayerWorld");
            object row = Activator.CreateInstance(playerWorldType, CreateIdentity(1), "OPEN", null, 0UL, "Oasis_Day")!;

            object scope = RequireMethod(gameplayScopeType, "FromPlayerWorld", playerWorldType, typeof(string)).Invoke(null, new[] { row, null })!;

            Assert.That(scope.ToString(), Is.EqualTo("open-world Oasis_Day"));
        }

        [Test]
        public void GameplayScope_FromPlayerWorld_MapsInstanceRows()
        {
            Type gameplayScopeType = RequireRuntimeType("Arena.Network.NetworkManager+GameplayScope");
            Type playerWorldType = RequireRuntimeType("SpacetimeDB.Types.PlayerWorld");
            object row = Activator.CreateInstance(playerWorldType, CreateIdentity(1), "INSTANCE", 42UL, 42UL, string.Empty)!;

            object scope = RequireMethod(gameplayScopeType, "FromPlayerWorld", playerWorldType, typeof(string)).Invoke(null, new[] { row, null })!;

            Assert.That(scope.ToString(), Is.EqualTo("instance 42"));
            Assert.That(RequireProperty(gameplayScopeType, "InstanceId").GetValue(scope), Is.EqualTo(42UL));
        }

        [Test]
        public void GameplaySubscriptionPlanner_InstanceQueriesUseNonNullableScopeKeys()
        {
            Type plannerType = RequireRuntimeType("Arena.Network.GameplaySubscriptionPlanner");
            Type gameplayScopeType = RequireRuntimeType("Arena.Network.NetworkManager+GameplayScope");
            Type playerWorldType = RequireRuntimeType("SpacetimeDB.Types.PlayerWorld");
            object row = Activator.CreateInstance(
                playerWorldType,
                CreateIdentity(1),
                "INSTANCE",
                42UL,
                42UL,
                string.Empty)!;
            object scope = RequireMethod(gameplayScopeType, "FromPlayerWorld", playerWorldType, typeof(string))
                .Invoke(null, new[] { row, null })!;

            string[] scopedSql = (string[])RequireMethod(plannerType, "BuildScopedQuerySqls", gameplayScopeType)
                .Invoke(null, new[] { scope })!;
            string sql = string.Join("\n", scopedSql);

            foreach (string table in new[]
                     {
                         "player_world",
                         "npc_instance",
                         "inventory_container",
                         "active_world_interaction",
                         "active_world_obstacle",
                         "world_door_state",
                         "world_trap_state",
                     })
            {
                Assert.That(sql, Does.Contain($"\"{table}\".\"instance_scope_id\" = 42"));
                Assert.That(sql, Does.Not.Contain($"\"{table}\".\"instance_id\" = 42"));
            }

            foreach (string table in new[]
                     {
                         "arena_match",
                         "match_participant",
                         "match_participant_stats",
                     })
            {
                Assert.That(sql, Does.Contain($"\"{table}\".\"instance_id\" = 42"));
            }
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
            MethodInfo method = RequireMethod(
                deciderType,
                "DetermineTargetScene",
                typeof(string),
                typeof(ulong?),
                typeof(string),
                typeof(string),
                typeof(string),
                typeof(string),
                typeof(string));

            object? training = method.Invoke(null, new object?[] { "TrainingGround", 99UL, "SURVIVAL", "ARENA_MAP_01", "Arena_VerdantStand_Blockout", "Arena_Map_01", "Arena_Map_01" });
            object? characterCreation = method.Invoke(null, new object?[] { "CharacterCreation", null, null, null, "Arena_VerdantStand_Blockout", "Arena_Map_01", "Arena_Map_01" });
            object? groundSlashDemo = method.Invoke(null, new object?[] { "VFXGraph_GroundSlash", null, null, null, "Arena_VerdantStand_Blockout", "Arena_Map_01", "Arena_Map_01" });
            object? pilotoHolyDemo = method.Invoke(null, new object?[] { "Holy & Paladin Spells Bundle", null, null, null, "Arena_VerdantStand_Blockout", "Arena_Map_01", "Arena_Map_01" });
            object? unknownInstanceKind = method.Invoke(null, new object?[] { "Arena_VerdantStand_Blockout", 99UL, null, "ARENA_MAP_01", "Arena_VerdantStand_Blockout", "Arena_Map_01", "Arena_Map_01" });
            object? enterArenaInstance = method.Invoke(null, new object?[] { "Arena_VerdantStand_Blockout", 99UL, "ARENA", "ARENA_MAP_01", "Arena_VerdantStand_Blockout", "Arena_Map_01", "Arena_Map_01" });
            object? enterSurvivalInstance = method.Invoke(null, new object?[] { "Arena_VerdantStand_Blockout", 99UL, "SURVIVAL", "ARENA_MAP_01", "Arena_VerdantStand_Blockout", "Arena_Map_01", "Arena_Map_01" });
            object? enterSurvivalFromHub = method.Invoke(null, new object?[] { "Hub", 99UL, "SURVIVAL", "ARENA_MAP_01", "Arena_VerdantStand_Blockout", "Arena_Map_01", "Arena_Map_01" });
            object? preserveHubForOpenWorld = method.Invoke(null, new object?[] { "Hub", null, null, null, "Arena_VerdantStand_Blockout", "Arena_Map_01", "Arena_Map_01" });
            object? preserveLoadedSurvival = method.Invoke(null, new object?[] { "Arena_Map_01", 99UL, "SURVIVAL", "ARENA_MAP_01", "Arena_VerdantStand_Blockout", "Arena_Map_01", "Arena_Map_01" });
            object? enterOpenWorld = method.Invoke(null, new object?[] { "Arena_Map_01", null, null, null, "Arena_VerdantStand_Blockout", "Arena_Map_01", "Arena_Map_01" });
            object? preserveLoadedOpenWorld = method.Invoke(null, new object?[] { "Oasis_Day", null, null, null, "Golden_Valley_Sunny", "Arena_Map_01", "Arena_Map_01" });

            Assert.That(training, Is.Null);
            Assert.That(characterCreation, Is.Null);
            Assert.That(groundSlashDemo, Is.Null);
            Assert.That(pilotoHolyDemo, Is.Null);
            Assert.That(unknownInstanceKind, Is.Null);
            Assert.That(enterArenaInstance, Is.EqualTo("Arena_Map_01"));
            Assert.That(enterSurvivalInstance, Is.EqualTo("Arena_Map_01"));
            Assert.That(enterSurvivalFromHub, Is.EqualTo("Arena_Map_01"));
            Assert.That(preserveHubForOpenWorld, Is.Null);
            Assert.That(preserveLoadedSurvival, Is.Null);
            Assert.That(enterOpenWorld, Is.EqualTo("Arena_VerdantStand_Blockout"));
            Assert.That(preserveLoadedOpenWorld, Is.Null);
        }

        [Test]
        public void LocalWorldRuntimeCoordinator_WaitsForKindThenLoadsSharedArenaMap()
        {
            Type coordinatorType = RequireRuntimeType("Arena.Entity.LocalWorldRuntimeCoordinator");
            Type worldContextType = RequireRuntimeType("Arena.Input.LocalMovementWorldContext");
            Type playerWorldType = RequireRuntimeType("SpacetimeDB.Types.PlayerWorld");
            Type arenaInstanceType = RequireRuntimeType("SpacetimeDB.Types.ArenaInstance");
            object identity = CreateIdentity(1);
            object worldContext = Activator.CreateInstance(worldContextType)!;
            var loadedScenes = new List<string>();
            object coordinator = CreateLocalWorldRuntimeCoordinator(
                coordinatorType,
                worldContext,
                () => "Hub",
                scene => loadedScenes.Add(scene));
            RequireMethod(coordinatorType, "SetLocalIdentity", identity.GetType()).Invoke(coordinator, new[] { identity });

            object currentWorld = Activator.CreateInstance(
                playerWorldType,
                identity,
                "INSTANCE",
                42UL,
                42UL,
                string.Empty)!;
            RequireMethod(coordinatorType, "OnPlayerWorldInsert", playerWorldType).Invoke(coordinator, new[] { currentWorld });
            Assert.That(loadedScenes, Is.Empty, "The generic match scene must not load while the instance kind is unknown.");

            object arenaInstance = Activator.CreateInstance(arenaInstanceType)!;
            SetField(arenaInstance, "Id", 42UL);
            SetField(arenaInstance, "Seed", 7UL);
            SetField(arenaInstance, "InstanceKind", "SURVIVAL");
            SetField(arenaInstance, "MapId", "ARENA_MAP_01");
            RequireMethod(coordinatorType, "OnArenaInstanceInsert", arenaInstanceType).Invoke(coordinator, new[] { arenaInstance });

            Assert.That(loadedScenes, Is.EqualTo(new[] { "Arena_Map_01" }));
        }

        [Test]
        public void RuntimeSceneTransitionQueue_WaitsOneFrameAndCoalescesAuthoritativeRequests()
        {
            Type stateType = RequireRuntimeType("Arena.Entity.DeferredSceneTransitionState");
            object state = Activator.CreateInstance(stateType, nonPublic: true)!;
            MethodInfo request = RequireMethod(stateType, "Request", typeof(string), typeof(int));
            MethodInfo tryDequeue = RequireMethod(
                stateType,
                "TryDequeue",
                typeof(int),
                typeof(bool),
                typeof(string).MakeByRefType());

            request.Invoke(state, new object[] { "Arena_Map_01", 100 });
            var sameFrame = new object?[] { 100, false, null };
            Assert.That(tryDequeue.Invoke(state, sameFrame), Is.False);

            request.Invoke(state, new object[] { "Oasis_Day", 100 });
            var blockedByInFlightLoad = new object?[] { 101, true, null };
            Assert.That(tryDequeue.Invoke(state, blockedByInFlightLoad), Is.False);

            var followingFrame = new object?[] { 101, false, null };
            Assert.That(tryDequeue.Invoke(state, followingFrame), Is.True);
            Assert.That(followingFrame[2], Is.EqualTo("Oasis_Day"));

            var consumed = new object?[] { 102, false, null };
            Assert.That(tryDequeue.Invoke(state, consumed), Is.False);

            string coordinatorSource = File.ReadAllText(
                "Assets/Arena/Runtime/Entity/LocalWorldRuntimeCoordinator.cs");
            Assert.That(
                coordinatorSource,
                Does.Contain("requestSceneLoad ?? RuntimeSceneTransitionQueue.Request"));
            Assert.That(coordinatorSource, Does.Not.Contain("loadScene ?? SceneManager.LoadScene"));

            string queueSource = File.ReadAllText(
                "Assets/Arena/Runtime/Entity/RuntimeSceneTransitionQueue.cs");
            Assert.That(queueSource, Does.Contain("SceneManager.LoadSceneAsync"));
            Assert.That(queueSource, Does.Not.Contain("SceneManager.LoadScene("));
        }

        [Test]
        public void MatchStateCache_RecognizesSurvivalRegardlessOfSubscriptionArrivalOrder()
        {
            Type cacheType = RequireRuntimeType("Arena.Match.MatchStateCache");
            Type arenaInstanceType = RequireRuntimeType("SpacetimeDB.Types.ArenaInstance");
            MethodInfo insert = cacheType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Single(method => method.Name == "OnArenaInstanceInsert");
            MethodInfo setLocalWorld = RequireMethod(cacheType, "OnLocalPlayerWorldUpdate", typeof(ulong?));
            PropertyInfo isSurvival = RequireProperty(cacheType, "IsSurvivalMode");

            object arenaInstance = Activator.CreateInstance(arenaInstanceType)!;
            SetField(arenaInstance, "Id", 42UL);
            SetField(arenaInstance, "InstanceKind", "SURVIVAL");
            SetField(arenaInstance, "Phase", "WAITING");

            object instanceFirst = Activator.CreateInstance(cacheType)!;
            insert.Invoke(instanceFirst, new object?[] { null, arenaInstance });
            Assert.That(isSurvival.GetValue(instanceFirst), Is.False);
            setLocalWorld.Invoke(instanceFirst, new object?[] { 42UL });
            Assert.That(isSurvival.GetValue(instanceFirst), Is.True);

            object playerWorldFirst = Activator.CreateInstance(cacheType)!;
            setLocalWorld.Invoke(playerWorldFirst, new object?[] { 42UL });
            insert.Invoke(playerWorldFirst, new object?[] { null, arenaInstance });
            Assert.That(isSurvival.GetValue(playerWorldFirst), Is.True);
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

            object currentWorld = Activator.CreateInstance(playerWorldType, identity, "OPEN", null, 0UL, "Oasis_Day")!;
            RequireMethod(coordinatorType, "OnPlayerWorldInsert", playerWorldType).Invoke(coordinator, new[] { currentWorld });
            loadedScenes.Clear();

            activeScene = "Golden_Valley_Sunny";
            object preferredScene = Activator.CreateInstance(preferenceType, identity, "Golden_Valley_Sunny")!;
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

            object currentWorld = Activator.CreateInstance(playerWorldType, identity, "OPEN", null, 0UL, "Oasis_Day")!;
            RequireMethod(coordinatorType, "OnPlayerWorldInsert", playerWorldType).Invoke(coordinator, new[] { currentWorld });
            Assert.That(loadedScenes, Is.Empty);

            object requestedWorld = Activator.CreateInstance(playerWorldType, identity, "OPEN", null, 0UL, "Golden_Valley_Sunny")!;
            RequireMethod(coordinatorType, "OnPlayerWorldUpdate", playerWorldType).Invoke(coordinator, new[] { requestedWorld });
            Assert.That(loadedScenes, Is.Empty);
        }

        [Test]
        public void NetworkManager_DoesNotBootstrapForImportedVfxDemoScenes()
        {
            Type managerType = RequireRuntimeType("Arena.Network.NetworkManager");
            Type sceneGateType = RequireRuntimeType("Arena.ArenaRuntimeSceneGate");
            MethodInfo method = RequireMethod(managerType, "ShouldBootstrapForScene", typeof(string), typeof(string));
            MethodInfo sceneGateMethod = RequireMethod(sceneGateType, "IsArenaRuntimeScene", typeof(string), typeof(string));

            object? pilotoHolyDemo = method.Invoke(null, new object[]
            {
                "Holy & Paladin Spells Bundle",
                "Assets/ThirdParty/AssetStore/VFX/Piloto Studio/Elemental VFX Mega Bundle/Holy/Holy & Paladin Spells Bundle.unity",
            });
            object? pilotoFrostDemo = method.Invoke(null, new object[]
            {
                "Frost & Ice Spells Bundle",
                "Assets/ThirdParty/AssetStore/VFX/Piloto Studio/Elemental VFX Mega Bundle/Frost/Frost & Ice Spells Bundle.unity",
            });
            object? arenaMap01 = method.Invoke(null, new object[]
            {
                "Arena_Map_01",
                "Assets/Arena/Content/Scenes/Arena_Map_01.unity",
            });
            object? openWorldSceneInPlayerBuild = method.Invoke(null, new object[]
            {
                "Oasis_Day",
                string.Empty,
            });
            object? sceneGatePilotoHolyDemo = sceneGateMethod.Invoke(null, new object[]
            {
                "Holy & Paladin Spells Bundle",
                "Assets/ThirdParty/AssetStore/VFX/Piloto Studio/Elemental VFX Mega Bundle/Holy/Holy & Paladin Spells Bundle.unity",
            });
            object? sceneGateArenaMap01 = sceneGateMethod.Invoke(null, new object[]
            {
                "Arena_Map_01",
                "Assets/Arena/Content/Scenes/Arena_Map_01.unity",
            });
            object? sceneGateRetiredArenaMatch = sceneGateMethod.Invoke(null, new object[]
            {
                "ArenaMatch",
                string.Empty,
            });
            object? sceneGateRetiredSurvivalArena = sceneGateMethod.Invoke(null, new object[]
            {
                "SurvivalArena",
                string.Empty,
            });

            Assert.That(pilotoHolyDemo, Is.False);
            Assert.That(pilotoFrostDemo, Is.False);
            Assert.That(arenaMap01, Is.True);
            Assert.That(openWorldSceneInPlayerBuild, Is.True);
            Assert.That(sceneGatePilotoHolyDemo, Is.False);
            Assert.That(sceneGateArenaMap01, Is.True);
            Assert.That(sceneGateRetiredArenaMatch, Is.False);
            Assert.That(sceneGateRetiredSurvivalArena, Is.False);
        }

        [Test]
        public void ArenaMap01_IsBuildRegisteredWithFourAuthoredEntrancesAndLaptopSafeLighting()
        {
            const string scenePath = "Assets/Arena/Content/Scenes/Arena_Map_01.unity";
            Assert.That(File.Exists(scenePath), Is.True);
            Assert.That(File.Exists("Assets/Arena/Content/Scenes/ArenaMatch.unity"), Is.False);
            Assert.That(File.Exists("Assets/Arena/Content/Scenes/SurvivalArena.unity"), Is.False);

            string buildSettings = File.ReadAllText("ProjectSettings/EditorBuildSettings.asset");
            Assert.That(buildSettings, Does.Contain($"path: {scenePath}"));

            string scene = File.ReadAllText(scenePath);
            foreach (string side in new[] { "North", "East", "South", "West" })
                Assert.That(scene, Does.Contain($"m_Name: Level Entrance {side}"));

            Assert.That(scene, Does.Not.Contain("m_Shadows:\n    m_Type: 1"));
            Assert.That(scene, Does.Not.Contain("m_Shadows:\n    m_Type: 2"));
            Assert.That(CountOccurrences(scene, "  m_Enabled: 0\n"), Is.EqualTo(56));
            Assert.That(
                CountOccurrences(scene, "guid: 8ab8340ffd685fb479daac555f516852"),
                Is.EqualTo(56));

            string lightingBudget = File.ReadAllText(
                "Assets/Arena/Runtime/World/ArenaMap01LightingBudget.cs");
            Assert.That(
                lightingBudget,
                Does.Contain("ArenaGraphicsSettings.EffectsAnimationUpdatesPerSecond"));
            Assert.That(lightingBudget, Does.Contain("ArenaLightShadowQuality.Hero"));
            Assert.That(lightingBudget, Does.Contain("LightShadows.Soft"));

            string mapCatalog = File.ReadAllText(
                "Assets/Arena/Runtime/World/ArenaMapCatalog.cs");
            Assert.That(mapCatalog, Does.Contain("ArenaMap01Id = \"ARENA_MAP_01\""));
            Assert.That(mapCatalog, Does.Contain("ArenaMap01SceneName = \"Arena_Map_01\""));

            string serverLayout = File.ReadAllText("server/src/map_data/arena_map_01.layout.shared.json");
            string clientLayout = File.ReadAllText(
                "Assets/Arena/Resources/SharedData/Maps/arena_map_01.layout.shared.json");
            Assert.That(clientLayout, Is.EqualTo(serverLayout));
            Assert.That(serverLayout, Does.Contain("\"boundary_shape\": \"aabb\""));
            Assert.That(serverLayout, Does.Contain("\"ruin_wall_segments\": []"));
            Assert.That(serverLayout, Does.Contain("\"platforms\": []"));
            Assert.That(serverLayout, Does.Contain("\"ramps\": []"));
            Assert.That(serverLayout, Does.Contain("\"pillar_count\": 0"));

            string serverCollision = File.ReadAllText(
                "server/src/map_data/arena_map_01.collision.shared.json");
            string clientCollision = File.ReadAllText(
                "Assets/Arena/Resources/SharedData/Maps/arena_map_01.collision.shared.json");
            string serverQueryCollision = File.ReadAllText(
                "server/src/map_data/arena_map_01.query_collision.shared.json");
            string clientQueryCollision = File.ReadAllText(
                "Assets/Arena/Resources/SharedData/Maps/arena_map_01.query_collision.shared.json");
            Assert.That(clientCollision, Is.EqualTo(serverCollision));
            Assert.That(clientQueryCollision, Is.EqualTo(serverQueryCollision));
            Assert.That(serverCollision, Does.Contain("\"boxes\": []"));
            Assert.That(serverQueryCollision, Does.Contain("\"boxes\": []"));
            Assert.That(serverCollision, Does.Contain("\"source_revision\": \"83d8801b"));
            Assert.That(File.Exists("server/src/arena_layout.shared.json"), Is.False);
            Assert.That(File.Exists("server/src/gameplay_collision.shared.json"), Is.False);
            Assert.That(File.Exists("server/src/gameplay_query_collision.shared.json"), Is.False);
            Assert.That(
                File.Exists("Assets/Arena/Resources/SharedData/arena_layout.shared.json"),
                Is.False);
            Assert.That(
                File.Exists("Assets/Arena/Resources/SharedData/gameplay_collision.shared.json"),
                Is.False);
            Assert.That(
                File.Exists("Assets/Arena/Resources/SharedData/gameplay_query_collision.shared.json"),
                Is.False);
        }

        [Test]
        public void GraphicsMenu_DefaultsToLaptopSafeValuesAndExposesGlobalControls()
        {
            Type graphicsSettings = RequireRuntimeType("Arena.Graphics.ArenaGraphicsSettings");
            Assert.That(
                graphicsSettings.GetField(
                        "DefaultFrameLimit",
                        BindingFlags.Static | BindingFlags.NonPublic)!
                    .GetRawConstantValue(),
                Is.EqualTo(60));
            Assert.That(
                graphicsSettings.GetField(
                        "LaptopTextureMipmapLimit",
                        BindingFlags.Static | BindingFlags.NonPublic)!
                    .GetRawConstantValue(),
                Is.EqualTo(1));
            Assert.That(
                graphicsSettings.GetField(
                        "LowEffectsAnimationUpdatesPerSecond",
                        BindingFlags.Static | BindingFlags.NonPublic)!
                    .GetRawConstantValue(),
                Is.EqualTo(15f));

            string settingsSource = File.ReadAllText(
                "Assets/Arena/Runtime/Graphics/ArenaGraphicsSettings.cs");
            Assert.That(settingsSource, Does.Contain("ArenaTextureQuality.Laptop"));
            Assert.That(settingsSource, Does.Contain("ArenaEffectsQuality.Low"));
            Assert.That(settingsSource, Does.Contain("ArenaLightShadowQuality.Off"));
            Assert.That(
                settingsSource,
                Does.Contain("QualitySettings.globalTextureMipmapLimit"));

            string menu = File.ReadAllText("Assets/Arena/Resources/UI/Toolkit/SystemMenu.uxml");
            foreach (string control in new[]
                     {
                         "FrameLimitButton",
                         "TextureQualityButton",
                         "EffectsQualityButton",
                         "LightShadowsButton",
                     })
            {
                Assert.That(menu, Does.Contain($"name=\"{control}\""));
            }
        }

        [Test]
        public void SurvivalUi_UsesBoundedRefreshCadence()
        {
            Type survivalHud = RequireRuntimeType("Arena.UI.SurvivalHud");
            Type survivalShop = RequireRuntimeType("Arena.UI.SurvivalShopScreen");

            Assert.That(
                survivalHud.GetField(
                        "RefreshIntervalSeconds",
                        BindingFlags.Static | BindingFlags.NonPublic)!
                    .GetRawConstantValue(),
                Is.EqualTo(0.10f));
            Assert.That(
                survivalShop.GetField(
                        "RefreshIntervalSeconds",
                        BindingFlags.Static | BindingFlags.NonPublic)!
                    .GetRawConstantValue(),
                Is.EqualTo(0.20f));

            string hudSource = File.ReadAllText("Assets/Arena/Runtime/UI/SurvivalHud.cs");
            string shopSource = File.ReadAllText("Assets/Arena/Runtime/UI/Toolkit/SurvivalShopScreen.cs");
            Assert.That(hudSource, Does.Contain("now < _nextRefreshTime"));
            Assert.That(hudSource, Does.Contain("SetTextIfChanged"));
            Assert.That(shopSource, Does.Contain("now < _nextRefreshTime"));
        }

        [Test]
        public void VerboseRuntimeTraces_AreExplicitOptIn()
        {
            Type playerAnimator = RequireRuntimeType("Arena.Presentation.PlayerAnimator");
            Type pointerRouter = RequireRuntimeType("Arena.Interaction.WorldPointerInteractionRouter");

            Assert.That(
                playerAnimator.GetField(
                        "VerboseTraceSymbol",
                        BindingFlags.Static | BindingFlags.NonPublic)!
                    .GetRawConstantValue(),
                Is.EqualTo("ARENA_VERBOSE_RUNTIME_TRACES"));
            Assert.That(
                pointerRouter.GetField(
                        "VerboseTraceSymbol",
                        BindingFlags.Static | BindingFlags.NonPublic)!
                    .GetRawConstantValue(),
                Is.EqualTo("ARENA_VERBOSE_RUNTIME_TRACES"));

            string animatorSource = File.ReadAllText("Assets/Arena/Runtime/Presentation/PlayerAnimator.cs");
            string pointerSource = File.ReadAllText(
                "Assets/Arena/Runtime/Interaction/WorldPointerInteractionRouter.cs");
            Assert.That(
                CountOccurrences(
                    animatorSource,
                    "[System.Diagnostics.Conditional(VerboseTraceSymbol)]"),
                Is.EqualTo(5));
            Assert.That(
                CountOccurrences(
                    pointerSource,
                    "[System.Diagnostics.Conditional(VerboseTraceSymbol)]"),
                Is.EqualTo(4));
            Assert.That(animatorSource, Does.Not.Contain("[Conditional(\"UNITY_EDITOR\")]"));
            Assert.That(animatorSource, Does.Not.Contain("[Conditional(\"DEVELOPMENT_BUILD\")]"));
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

            string staticSqlText = string.Join("\n", staticSql);
            string localSqlText = string.Join("\n", localSql);
            string localIdentityKey = localIdentity.ToString()!;
            Assert.That(staticSqlText, Does.Contain("\"ability_catalog\""));
            Assert.That(staticSqlText, Does.Contain("\"action_presentation_catalog\""));
            Assert.That(staticSqlText, Does.Contain("\"combat_vfx_cue_catalog\""));
            Assert.That(staticSqlText, Does.Contain("\"combat_projectile_definition\""));
            Assert.That(staticSqlText, Does.Contain("\"combat_profile_catalog\""));
            Assert.That(staticSqlText, Does.Contain("\"combat_mode_catalog\""));
            Assert.That(staticSqlText, Does.Contain("\"action_bar_slot_catalog\""));
            Assert.That(staticSqlText, Does.Contain("\"armor_set_definition\""));
            Assert.That(staticSqlText, Does.Contain("\"spell_definition\""));
            Assert.That(staticSqlText, Does.Contain("\"melee_definition\""));
            Assert.That(staticSqlText, Does.Contain("\"melee_ability_catalog\""));
            Assert.That(staticSqlText, Does.Contain("\"melee_gap_close_catalog\""));
            Assert.That(staticSqlText, Does.Contain("\"melee_attack_modifier_catalog\""));
            Assert.That(staticSqlText, Does.Contain("\"auto_attack_catalog\""));
            Assert.That(staticSqlText, Does.Contain("\"combat_rule_catalog\""));
            Assert.That(staticSqlText, Does.Contain("\"resource_catalog\""));
            Assert.That(staticSqlText, Does.Contain("\"stat_scaling_catalog\""));
            Assert.That(staticSqlText, Does.Contain("\"npc_template_catalog\""));
            Assert.That(staticSqlText, Does.Contain("\"npc_visual_catalog\""));
            Assert.That(staticSqlText, Does.Contain("\"arena_instance\""));
            Assert.That(staticSqlText, Does.Not.Contain("\"fixed_action_binding_catalog\""));
            Assert.That(staticSqlText, Does.Not.Contain("\"class_catalog\""));

            Assert.That(localSql, Has.Length.EqualTo(28));
            Assert.That(localSql[0], Does.Contain("\"player_world\""));
            Assert.That(localSql[0], Does.Contain(localIdentityKey));
            Assert.That(localSqlText, Does.Contain("\"player_open_world_scene\""));
            Assert.That(localSqlText, Does.Contain("\"character_action_bar_assignment\""));
            Assert.That(localSqlText, Does.Contain("\"character_appearance\""));
            Assert.That(localSqlText, Does.Contain("\"player_known_spell\""));
            Assert.That(localSqlText, Does.Contain("\"global_cooldown\""));
            Assert.That(localSqlText, Does.Contain("\"spell_cooldown\""));
            Assert.That(localSqlText, Does.Contain("\"recall_slot\""));
            Assert.That(localSqlText, Does.Contain("\"predicted_action_result\""));
            Assert.That(localSqlText, Does.Contain("\"fixed_action_charge_state\""));
            Assert.That(localSqlText, Does.Contain("\"active_combat_discipline\""));
            Assert.That(localSqlText, Does.Contain("\"character_discipline_loadout\""));
            Assert.That(localSqlText, Does.Contain("\"character_discipline_ability_selection\""));
            Assert.That(localSqlText, Does.Contain("\"character_combat_discipline_weapon_loadout\""));
            Assert.That(localSqlText, Does.Contain("\"active_combat_mode\""));
            Assert.That(localSqlText, Does.Contain("\"auto_attack_state\""));
            Assert.That(localSqlText, Does.Contain("\"party_invite\""));
            Assert.That(localSqlText, Does.Contain("\"equipment_loadout\""));
            Assert.That(localSqlText, Does.Contain("\"player_equipment_presentation\""));
            Assert.That(localSqlText, Does.Contain("\"active_armor_set\""));
            Assert.That(localSqlText, Does.Contain("\"inventory_container\""));
            Assert.That(localSqlText, Does.Contain("\"inventory_slot\""));
            Assert.That(localSqlText, Does.Contain("\"item_instance\""));
            Assert.That(localSqlText, Does.Contain("\"item_spell\""));
            Assert.That(localSqlText, Does.Contain("\"item_affix_instance\""));
            Assert.That(localSqlText, Does.Contain($"\"inventory_container\".\"owner_key\" = '{localIdentityKey}'"));
            Assert.That(localSqlText, Does.Contain($"\"item_instance\".\"current_owner_key\" = '{localIdentityKey}'"));
            Assert.That(localSqlText, Does.Not.Contain("\"inventory_container\".\"owner\" = 0x"));
            Assert.That(localSqlText, Does.Not.Contain("\"item_instance\".\"current_owner\" = 0x"));
            Assert.That(localSqlText, Does.Not.Contain("\"character_progression\""));
        }

        [Test]
        public void GameplaySubscriptionPlanner_PvpMatchQueriesReferenceOnlyDedicatedSchemaTables()
        {
            Type plannerType = RequireRuntimeType("Arena.Network.GameplaySubscriptionPlanner");
            Type gameplayScopeType = RequireRuntimeType("Arena.Network.NetworkManager+GameplayScope");
            Type playerWorldType = RequireRuntimeType("SpacetimeDB.Types.PlayerWorld");
            object localIdentity = CreateIdentity(1);
            object row = Activator.CreateInstance(
                playerWorldType,
                localIdentity,
                "INSTANCE",
                42UL,
                42UL,
                string.Empty)!;
            object scope = RequireMethod(gameplayScopeType, "FromPlayerWorld", playerWorldType, typeof(string))
                .Invoke(null, new[] { row, null })!;

            string[] initialSql = (string[])RequireMethod(
                    plannerType,
                    "BuildPvpMatchInitialQuerySqls",
                    localIdentity.GetType())
                .Invoke(null, new[] { localIdentity })!;
            string[] scopedSql = (string[])RequireMethod(
                    plannerType,
                    "BuildPvpMatchScopedQuerySqls",
                    gameplayScopeType)
                .Invoke(null, new[] { scope })!;

            string sql = string.Join("\n", initialSql.Concat(scopedSql));
            string initialSqlText = string.Join("\n", initialSql);
            Assert.That(initialSql, Has.Length.EqualTo(44));
            foreach (string unavailableTable in new[]
                     {
                         "party",
                         "party_member",
                         "party_invite",
                         "playground_target",
                         "player_open_world_scene",
                         "active_dice_roll",
                         "survival_run",
                         "survival_result",
                         "survival_score",
                         "survival_shop_offer",
                         "recall_slot",
                         "inventory_container",
                         "inventory_slot",
                         "combat_projectile_tick_metrics",
                         "npc_template_catalog",
                         "npc_visual_catalog",
                         "active_world_interaction",
                         "world_door_state",
                         "world_trap_state",
                     })
            {
                Assert.That(sql, Does.Not.Contain($"\"{unavailableTable}\""));
            }

            Assert.That(sql, Does.Contain("\"arena_match\""));
            Assert.That(sql, Does.Contain("\"match_participant\""));
            Assert.That(sql, Does.Contain("\"active_world_obstacle\""));
            Assert.That(sql, Does.Contain("\"contract_version\""));
            Assert.That(sql, Does.Contain("\"arena_instance\""));
            Assert.That(sql, Does.Contain("\"character_action_bar_assignment\""));
            Assert.That(sql, Does.Contain("\"equipment_loadout\""));
            Assert.That(sql, Does.Contain("\"item_instance\""));
            Assert.That(sql, Does.Contain("\"item_spell\""));
            Assert.That(sql, Does.Contain("\"item_affix_instance\""));
            Assert.That(initialSqlText, Does.Contain(
                $"\"item_instance\".\"current_owner_key\" = '{localIdentity.ToString()!.ToLowerInvariant()}'"));
            Assert.That(initialSqlText, Does.Not.Contain(
                "\"item_instance\".\"current_owner_key\" = ''"));
        }

        [Test]
        public void NetworkManager_ProvisionedPvpUsesOneInitialSubscriptionBeforeScopedVisibility()
        {
            string source = File.ReadAllText(
                "Assets/Arena/Runtime/Network/NetworkManager.cs");

            Assert.That(source, Does.Contain("SubscribePvpMatchInitialTables(conn, identity);"));
            Assert.That(source, Does.Contain("BuildPvpMatchInitialQuerySqls(localIdentity)"));
            Assert.That(source, Does.Contain(".OnApplied(OnPvpMatchInitialSubscriptionApplied)"));
            Assert.That(source, Does.Contain("ContractVersionGuard.ValidatePvpMatch(ctx.Db)"));
            Assert.That(source, Does.Contain("if (_isProvisionedMatchConnection && !IsConnected)"));
            Assert.That(source, Does.Contain("BuildPvpMatchScopedQuerySqls(scope)"));

            int initialMethodStart = source.IndexOf(
                "private void SubscribePvpMatchInitialTables",
                StringComparison.Ordinal);
            int scopeMethodStart = source.IndexOf(
                "private void TryAdvanceGameplayScopeTransition",
                StringComparison.Ordinal);
            Assert.That(initialMethodStart, Is.GreaterThanOrEqualTo(0));
            Assert.That(scopeMethodStart, Is.GreaterThan(initialMethodStart));
            string initialMethod = source.Substring(
                initialMethodStart,
                scopeMethodStart - initialMethodStart);
            Assert.That(
                initialMethod.Split(new[] { ".Subscribe(" }, StringSplitOptions.None).Length - 1,
                Is.EqualTo(1));
        }

        [Test]
        public void ContractVersionGuard_PvpValidationLoadsOnlyArenaPredictionContracts()
        {
            string source = File.ReadAllText(
                "Assets/Arena/Runtime/Network/ContractVersionGuard.cs");
            int pvpStart = source.IndexOf(
                "private static IEnumerable<(string serverKey, TextAsset? asset)> ClientPvpSharedFiles()",
                StringComparison.Ordinal);
            int genericStart = source.IndexOf(
                "private static IEnumerable<(string serverKey, TextAsset? asset)> ClientSharedFiles()",
                StringComparison.Ordinal);

            Assert.That(pvpStart, Is.GreaterThanOrEqualTo(0));
            Assert.That(genericStart, Is.GreaterThan(pvpStart));
            string pvpMethod = source.Substring(pvpStart, genericStart - pvpStart);
            Assert.That(pvpMethod, Does.Contain("ArenaMap01LayoutResourcePath"));
            Assert.That(pvpMethod, Does.Contain("ArenaMap01MovementCollisionResourcePath"));
            Assert.That(pvpMethod, Does.Contain("ArenaMap01QueryCollisionResourcePath"));
            Assert.That(pvpMethod, Does.Not.Contain("Resources.LoadAll"));
            Assert.That(pvpMethod, Does.Not.Contain("Resources.Load<TextAsset>(\"SharedData/Worlds"));
            Assert.That(pvpMethod, Does.Not.Contain("Resources.Load<TextAsset>(\"SharedData/WorldInteractions"));
        }

        [Test]
        public void GameplaySubscriptionPlanner_ScopesProjectilePresentationEventsWithVisiblePlayersAndNpcs()
        {
            Type plannerType = RequireRuntimeType("Arena.Network.GameplaySubscriptionPlanner");
            Type gameplayScopeType = RequireRuntimeType("Arena.Network.NetworkManager+GameplayScope");
            Type playerWorldType = RequireRuntimeType("SpacetimeDB.Types.PlayerWorld");
            object row = Activator.CreateInstance(playerWorldType, CreateIdentity(1), "OPEN", null, 0UL, "Oasis_Day")!;
            object scope = RequireMethod(gameplayScopeType, "FromPlayerWorld", playerWorldType, typeof(string))
                .Invoke(null, new[] { row, null })!;

            string[] scopedSql = (string[])RequireMethod(plannerType, "BuildScopedQuerySqls", gameplayScopeType)
                .Invoke(null, new[] { scope })!;
            string scopedSqlText = string.Join("\n", scopedSql);

            Assert.That(scopedSqlText, Does.Contain("\"projectile_presentation_event\""));
            Assert.That(scopedSqlText, Does.Contain("\"player_world\".\"identity\" = \"projectile_presentation_event\".\"caster\""));
            Assert.That(scopedSqlText, Does.Contain("\"npc_instance\".\"identity\" = \"projectile_presentation_event\".\"caster\""));
        }

        [Test]
        public void GameplaySubscriptionPlanner_ScopesCombatEventsByVisiblePlayerTarget()
        {
            Type plannerType = RequireRuntimeType("Arena.Network.GameplaySubscriptionPlanner");
            Type gameplayScopeType = RequireRuntimeType("Arena.Network.NetworkManager+GameplayScope");
            Type playerWorldType = RequireRuntimeType("SpacetimeDB.Types.PlayerWorld");
            object row = Activator.CreateInstance(
                playerWorldType,
                CreateIdentity(1),
                "OPEN",
                null,
                0UL,
                "RandomDungeon")!;
            object scope = RequireMethod(
                    gameplayScopeType,
                    "FromPlayerWorld",
                    playerWorldType,
                    typeof(string))
                .Invoke(null, new[] { row, null })!;

            string[] scopedSql = (string[])RequireMethod(
                    plannerType,
                    "BuildScopedQuerySqls",
                    gameplayScopeType)
                .Invoke(null, new[] { scope })!;
            string scopedSqlText = string.Join("\n", scopedSql);

            Assert.That(scopedSqlText, Does.Contain(
                "\"player_world\".\"identity\" = \"combat_event\".\"hit\""));
        }

        [Test]
        public void GameplaySubscriptionPlanner_ScopesActiveRadialEffectsToVisibleOwners()
        {
            Type plannerType = RequireRuntimeType("Arena.Network.GameplaySubscriptionPlanner");
            Type gameplayScopeType = RequireRuntimeType("Arena.Network.NetworkManager+GameplayScope");
            Type playerWorldType = RequireRuntimeType("SpacetimeDB.Types.PlayerWorld");
            object row = Activator.CreateInstance(playerWorldType, CreateIdentity(1), "OPEN", null, 0UL, "Oasis_Day")!;
            object scope = RequireMethod(gameplayScopeType, "FromPlayerWorld", playerWorldType, typeof(string))
                .Invoke(null, new[] { row, null })!;

            string[] scopedSql = (string[])RequireMethod(plannerType, "BuildScopedQuerySqls", gameplayScopeType)
                .Invoke(null, new[] { scope })!;
            string scopedSqlText = string.Join("\n", scopedSql);

            Assert.That(scopedSqlText, Does.Contain("\"active_radial_effect\""));
            Assert.That(scopedSqlText, Does.Contain("\"player_world\".\"identity\" = \"active_radial_effect\".\"owner\""));
        }

        [Test]
        public void GameplaySubscriptionPlanner_ScopesActiveWorldObstaclesToTheVisibleWorld()
        {
            Type plannerType = RequireRuntimeType("Arena.Network.GameplaySubscriptionPlanner");
            Type gameplayScopeType = RequireRuntimeType("Arena.Network.NetworkManager+GameplayScope");
            Type playerWorldType = RequireRuntimeType("SpacetimeDB.Types.PlayerWorld");
            object row = Activator.CreateInstance(playerWorldType, CreateIdentity(1), "OPEN", null, 0UL, "Oasis_Day")!;
            object scope = RequireMethod(gameplayScopeType, "FromPlayerWorld", playerWorldType, typeof(string))
                .Invoke(null, new[] { row, null })!;

            string[] scopedSql = (string[])RequireMethod(plannerType, "BuildScopedQuerySqls", gameplayScopeType)
                .Invoke(null, new[] { scope })!;
            string scopedSqlText = string.Join("\n", scopedSql);

            Assert.That(scopedSqlText, Does.Contain("\"active_world_obstacle\""));
            Assert.That(scopedSqlText, Does.Contain("\"active_world_obstacle\".\"world_kind\" = 'OPEN'"));
            Assert.That(scopedSqlText, Does.Contain("\"active_world_obstacle\".\"open_world_scene_name\" = 'Oasis_Day'"));
        }

        [Test]
        public void GameplaySubscriptionPlanner_ScopesLingeringShadesByTheirCapturedWorld()
        {
            Type plannerType = RequireRuntimeType("Arena.Network.GameplaySubscriptionPlanner");
            Type gameplayScopeType = RequireRuntimeType("Arena.Network.NetworkManager+GameplayScope");
            Type playerWorldType = RequireRuntimeType("SpacetimeDB.Types.PlayerWorld");
            object row = Activator.CreateInstance(playerWorldType, CreateIdentity(1), "OPEN", null, 0UL, "Oasis_Day")!;
            object scope = RequireMethod(gameplayScopeType, "FromPlayerWorld", playerWorldType, typeof(string))
                .Invoke(null, new[] { row, null })!;

            string[] scopedSql = (string[])RequireMethod(plannerType, "BuildScopedQuerySqls", gameplayScopeType)
                .Invoke(null, new[] { scope })!;
            string scopedSqlText = string.Join("\n", scopedSql);

            Assert.That(scopedSqlText, Does.Contain("\"lingering_shade_state\""));
            Assert.That(scopedSqlText, Does.Contain("\"lingering_shade_state\".\"world_kind\" = 'OPEN'"));
            Assert.That(scopedSqlText, Does.Contain(
                "\"lingering_shade_state\".\"open_world_scene_name\" = 'Oasis_Day'"));
        }

        [Test]
        public void LingeringShadeReturnVfx_UsesNormalAbilityCueResolutionForBothEndpoints()
        {
            object departure = CreateCombatVfxCue(
                "shade-departure",
                "ABILITY",
                "SUBTLETY_LINGERING_SHADE",
                "SPECIAL_MOVEMENT_START",
                "ORIGIN",
                "VFX_LINGERING_SHADE_RETURN_01",
                "SPAWN_WORLD",
                "ONE_SHOT",
                0,
                8);
            object arrival = CreateCombatVfxCue(
                "shade-arrival",
                "ABILITY",
                "SUBTLETY_LINGERING_SHADE",
                "SPECIAL_MOVEMENT_ARRIVAL",
                "IMPACT_POINT",
                "VFX_LINGERING_SHADE_RETURN_01",
                "SPAWN_WORLD",
                "ONE_SHOT",
                0,
                9);

            Assert.That(
                ResolveCombatVfxCueIds(
                    false,
                    "SPECIAL_MOVEMENT_START",
                    string.Empty,
                    "SUBTLETY_LINGERING_SHADE",
                    string.Empty,
                    -1,
                    departure,
                    arrival),
                Is.EqualTo(new[] { "VFX_LINGERING_SHADE_RETURN_01" }));
            Assert.That(
                ResolveCombatVfxCueIds(
                    false,
                    "SPECIAL_MOVEMENT_ARRIVAL",
                    string.Empty,
                    "SUBTLETY_LINGERING_SHADE",
                    string.Empty,
                    -1,
                    departure,
                    arrival),
                Is.EqualTo(new[] { "VFX_LINGERING_SHADE_RETURN_01" }));
        }

        [Test]
        public void SpecialMovementVfxEndpoint_RotatesModelChestOffsetAtAuthoritativePosition()
        {
            Type dispatcherType = RequireRuntimeType("Arena.Presentation.CombatVFXDispatcher");
            Vector3 position = (Vector3)RequireMethod(
                    dispatcherType,
                    "ResolveSpecialMovementEndpoint",
                    typeof(Vector3),
                    typeof(Quaternion),
                    typeof(Vector3))
                .Invoke(
                    null,
                    new object[]
                    {
                        new Vector3(10f, 2f, 30f),
                        Quaternion.Euler(0f, 90f, 0f),
                        new Vector3(0f, 1.2f, 0.25f),
                    })!;

            Assert.That(position.x, Is.EqualTo(10.25f).Within(0.0001f));
            Assert.That(position.y, Is.EqualTo(3.2f).Within(0.0001f));
            Assert.That(position.z, Is.EqualTo(30f).Within(0.0001f));
        }

        [Test]
        public void GameplaySubscriptionPlanner_ScopesDoorAndInteractionRowsToTheVisibleWorld()
        {
            Type plannerType = RequireRuntimeType("Arena.Network.GameplaySubscriptionPlanner");
            Type gameplayScopeType = RequireRuntimeType("Arena.Network.NetworkManager+GameplayScope");
            Type playerWorldType = RequireRuntimeType("SpacetimeDB.Types.PlayerWorld");
            object row = Activator.CreateInstance(
                playerWorldType,
                CreateIdentity(1),
                "OPEN",
                null,
                0UL,
                "RandomDungeon")!;
            object scope = RequireMethod(
                    gameplayScopeType,
                    "FromPlayerWorld",
                    playerWorldType,
                    typeof(string))
                .Invoke(null, new[] { row, null })!;

            string[] scopedSql = (string[])RequireMethod(
                    plannerType,
                    "BuildScopedQuerySqls",
                    gameplayScopeType)
                .Invoke(null, new[] { scope })!;
            string scopedSqlText = string.Join("\n", scopedSql);

            Assert.That(scopedSqlText, Does.Contain("\"world_door_state\""));
            Assert.That(scopedSqlText, Does.Contain(
                "\"world_door_state\".\"open_world_scene_name\" = 'RandomDungeon'"));
            Assert.That(scopedSqlText, Does.Contain("\"active_world_interaction\""));
            Assert.That(scopedSqlText, Does.Contain(
                "\"active_world_interaction\".\"open_world_scene_name\" = 'RandomDungeon'"));
        }

        [Test]
        public void ActiveWorldObstacleRuntime_BlocksPredictedMovementAndSight()
        {
            Type runtimeType = RequireRuntimeType("Arena.Input.ActiveWorldObstacleRuntime");
            Type obstacleType = RequireRuntimeType("SpacetimeDB.Types.ActiveWorldObstacle");
            object obstacle = Activator.CreateInstance(obstacleType)!;
            obstacleType.GetField("ObstacleId")!.SetValue(obstacle, 1UL);
            obstacleType.GetField("CenterX")!.SetValue(obstacle, 0f);
            obstacleType.GetField("CenterY")!.SetValue(obstacle, 3.5f);
            obstacleType.GetField("CenterZ")!.SetValue(obstacle, 0f);
            obstacleType.GetField("Yaw")!.SetValue(obstacle, 0f);
            obstacleType.GetField("HalfWidth")!.SetValue(obstacle, 1f);
            obstacleType.GetField("HalfHeight")!.SetValue(obstacle, 3.5f);
            obstacleType.GetField("HalfDepth")!.SetValue(obstacle, 1.25f);
            obstacleType.GetField("CollisionRotationX")!.SetValue(obstacle, 0f);
            obstacleType.GetField("CollisionRotationY")!.SetValue(obstacle, 0f);
            obstacleType.GetField("CollisionRotationZ")!.SetValue(obstacle, 0f);
            obstacleType.GetField("CollisionRotationW")!.SetValue(obstacle, 1f);

            RequireMethod(runtimeType, "Clear").Invoke(null, null);
            try
            {
                RequireMethod(runtimeType, "Upsert", obstacleType).Invoke(null, new[] { obstacle });
                var resolved = (Vector2)RequireMethod(
                        runtimeType,
                        "ResolveHorizontalCollision",
                        typeof(float),
                        typeof(float),
                        typeof(float),
                        typeof(float),
                        typeof(float),
                        typeof(float),
                        typeof(float))
                    .Invoke(null, new object[] { 0f, -4f, 0f, 4f, 0.25f, 1.8f, 0f })!;
                Assert.That(resolved.y, Is.InRange(-1.52f, -1.49f));

                object[] lineArgs =
                {
                    new Vector3(0f, 1.5f, -4f),
                    new Vector3(0f, 1.5f, 4f),
                    0.05f,
                    0f,
                };
                bool hit = (bool)RequireMethod(
                        runtimeType,
                        "TryFindFirstLineHitDistance",
                        typeof(Vector3),
                        typeof(Vector3),
                        typeof(float),
                        typeof(float).MakeByRefType())
                    .Invoke(null, lineArgs)!;
                Assert.That(hit, Is.True);
                Assert.That((float)lineArgs[3], Is.LessThan(4f));
            }
            finally
            {
                RequireMethod(runtimeType, "Clear").Invoke(null, null);
            }
        }

        [Test]
        public void CombatVfxGroundFollow_UsesAnchorXZWithoutInheritingJumpHeight()
        {
            Type registryType = RequireRuntimeType("Arena.Presentation.CombatVFXLifecycleRegistry");
            MethodInfo resolve = RequireMethod(
                registryType,
                "ResolveGroundFollowPosition",
                typeof(Vector3),
                typeof(Vector3),
                typeof(Vector3),
                typeof(bool),
                typeof(float));
            var current = new Vector3(1f, 7f, 2f);
            var jumpingAnchor = new Vector3(10f, 20f, 30f);
            var offset = new Vector3(0.25f, 0.5f, -0.75f);

            var grounded = (Vector3)resolve.Invoke(
                null,
                new object[] { current, jumpingAnchor, offset, true, 3f })!;
            Assert.That(grounded.x, Is.EqualTo(10.25f).Within(0.0001f));
            Assert.That(grounded.y, Is.EqualTo(3.53f).Within(0.0001f));
            Assert.That(grounded.z, Is.EqualTo(29.25f).Within(0.0001f));

            var fallback = (Vector3)resolve.Invoke(
                null,
                new object[] { current, jumpingAnchor, offset, false, 0f })!;
            Assert.That(fallback.y, Is.EqualTo(current.y).Within(0.0001f));
        }

        [Test]
        public void GameplaySubscriptionPlanner_ScopesCombatEffectsByVisibleSourceAndTarget()
        {
            Type plannerType = RequireRuntimeType("Arena.Network.GameplaySubscriptionPlanner");
            Type gameplayScopeType = RequireRuntimeType("Arena.Network.NetworkManager+GameplayScope");
            Type playerWorldType = RequireRuntimeType("SpacetimeDB.Types.PlayerWorld");
            object row = Activator.CreateInstance(playerWorldType, CreateIdentity(1), "OPEN", null, 0UL, "Oasis_Day")!;
            object scope = RequireMethod(gameplayScopeType, "FromPlayerWorld", playerWorldType, typeof(string))
                .Invoke(null, new[] { row, null })!;

            string[] scopedSql = (string[])RequireMethod(plannerType, "BuildScopedQuerySqls", gameplayScopeType)
                .Invoke(null, new[] { scope })!;
            string scopedSqlText = string.Join("\n", scopedSql);

            Assert.That(scopedSqlText, Does.Contain("\"combat_effect_event\""));
            Assert.That(scopedSqlText, Does.Contain("\"player_world\".\"identity\" = \"combat_effect_event\".\"source\""));
            Assert.That(scopedSqlText, Does.Contain("\"player_world\".\"identity\" = \"combat_effect_event\".\"target\""));
            Assert.That(scopedSqlText, Does.Contain("\"npc_instance\".\"identity\" = \"combat_effect_event\".\"source\""));
            Assert.That(scopedSqlText, Does.Contain("\"npc_instance\".\"identity\" = \"combat_effect_event\".\"target\""));
            Assert.That(scopedSqlText, Does.Contain("\"inventory_container\".\"container_kind\" = 'CORPSE'"));
        }

        [Test]
        public void ContractVersionValidation_FailsClosedForMissingOrMismatchedStamps()
        {
            Type resultType = RequireRuntimeType("Arena.Network.ContractVersionGuard+ValidationResult");

            object compatible = Activator.CreateInstance(
                resultType,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                args: new object[] { 3, 0, 0 },
                culture: null)!;
            object missing = Activator.CreateInstance(
                resultType,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                args: new object[] { 2, 1, 0 },
                culture: null)!;
            object mismatched = Activator.CreateInstance(
                resultType,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                args: new object[] { 2, 0, 1 },
                culture: null)!;
            object empty = Activator.CreateInstance(
                resultType,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                args: new object[] { 0, 0, 0 },
                culture: null)!;

            Assert.That(RequireProperty(resultType, "IsCompatible").GetValue(compatible), Is.True);
            Assert.That(RequireProperty(resultType, "IsCompatible").GetValue(missing), Is.False);
            Assert.That(RequireProperty(resultType, "IsCompatible").GetValue(mismatched), Is.False);
            Assert.That(RequireProperty(resultType, "IsCompatible").GetValue(empty), Is.False);
        }

        [Test]
        public void ContractVersionGuard_MapsWorldInteractionProfilesToServerWorldDataKey()
        {
            Type guardType = RequireRuntimeType("Arena.Network.ContractVersionGuard");
            var mappings = (IEnumerable)RequireMethod(guardType, "ClientSharedFiles")
                .Invoke(null, null)!;

            string? serverKey = null;
            foreach (object mapping in mappings)
            {
                Type mappingType = mapping.GetType();
                var asset = (TextAsset)mappingType.GetField("Item2")!.GetValue(mapping)!;
                if (asset.name == "world_interaction_profiles.shared")
                {
                    serverKey = (string)mappingType.GetField("Item1")!.GetValue(mapping)!;
                    break;
                }
            }

            Assert.That(
                serverKey,
                Is.EqualTo("world_data/world_interaction_profiles.shared.json"));
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
                "SPELL_FIREBALL",
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
                "SPELL_FIREBALL",
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
                "SPELL_ICICLE",
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
        public void AvatarVfxSockets_BackSocketPreservesPlacementAndInheritsTorsoRotation()
        {
            Type socketsType = RequireRuntimeType("Arena.Presentation.Appearance.AvatarVfxSockets");
            var root = new GameObject("AnchorResolverAvatar");
            root.transform.SetPositionAndRotation(new Vector3(3f, 0f, 5f), Quaternion.Euler(0f, 35f, 0f));
            Animator animator = root.AddComponent<Animator>();
            var spine = new GameObject("spine_03");
            spine.transform.SetParent(root.transform, false);
            spine.transform.localPosition = new Vector3(0f, 1.45f, 0f);

            Component sockets = (Component)RequireMethod(
                    socketsType,
                    "EnsureOn",
                    typeof(GameObject),
                    typeof(Animator))
                .Invoke(null, new object[] { root, animator })!;
            object?[] resolveArgs = { "back", null };
            Assert.That(
                RequireMethod(socketsType, "TryGetSocket", typeof(string), typeof(Transform).MakeByRefType())
                    .Invoke(sockets, resolveArgs),
                Is.EqualTo(true));

            Transform socket = (Transform)resolveArgs[1]!;
            Vector3 expectedPosition = root.transform.TransformPoint(new Vector3(0f, 1.1f, -0.25f));
            Assert.That(Vector3.Distance(socket.position, expectedPosition), Is.LessThan(0.0001f));
            Assert.That(Quaternion.Angle(socket.rotation, root.transform.rotation), Is.LessThan(0.001f));

            Quaternion initialRotation = socket.rotation;
            spine.transform.localRotation = Quaternion.Euler(25f, 0f, 0f);
            Assert.That(Quaternion.Angle(socket.rotation, initialRotation), Is.GreaterThan(20f));
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
                       false,
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
                .Single(ctor => ctor.GetParameters().Length == 7);
            return constructor.Invoke(new object?[]
            {
                worldContext,
                null,
                null,
                getActiveSceneName,
                loadScene,
                "Arena_Map_01",
                "Arena_Map_01",
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
                abilityId: "SPELL_FIREBALL",
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

        private static int CountOccurrences(string value, string needle)
        {
            int count = 0;
            int start = 0;
            while ((start = value.IndexOf(needle, start, StringComparison.Ordinal)) >= 0)
            {
                count++;
                start += needle.Length;
            }

            return count;
        }

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
