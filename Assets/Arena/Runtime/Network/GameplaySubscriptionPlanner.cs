#nullable enable

using System;
using System.Collections.Generic;
using SpacetimeDB;
using SpacetimeDB.Types;

namespace Arena.Network
{
    internal static class GameplaySubscriptionPlanner
    {
        internal static string[] BuildStaticQuerySqls()
        {
            return new[]
            {
                new QueryBuilder().From.AbilityCatalog().ToSql(),
                new QueryBuilder().From.ActionPresentationCatalog().ToSql(),
                new QueryBuilder().From.CombatVfxCueCatalog().ToSql(),
                new QueryBuilder().From.CombatProjectileDefinition().ToSql(),
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                new QueryBuilder().From.CombatProjectileTickMetrics().ToSql(),
#endif
                new QueryBuilder().From.CombatProfileCatalog().ToSql(),
                new QueryBuilder().From.CombatDisciplineCatalog().ToSql(),
                new QueryBuilder().From.CombatModeCatalog().ToSql(),
                new QueryBuilder().From.ActionBarSlotCatalog().ToSql(),
                new QueryBuilder().From.ItemDefinition().ToSql(),
                new QueryBuilder().From.ArmorSetDefinition().ToSql(),
                new QueryBuilder().From.ItemAffixDefinition().ToSql(),
                new QueryBuilder().From.SpellDefinition().ToSql(),
                new QueryBuilder().From.MeleeDefinition().ToSql(),
                new QueryBuilder().From.MeleeAbilityCatalog().ToSql(),
                new QueryBuilder().From.MeleeGapCloseCatalog().ToSql(),
                new QueryBuilder().From.MeleeAttackModifierCatalog().ToSql(),
                new QueryBuilder().From.AutoAttackCatalog().ToSql(),
                new QueryBuilder().From.CombatRuleCatalog().ToSql(),
                new QueryBuilder().From.ResourceCatalog().ToSql(),
                new QueryBuilder().From.StatScalingCatalog().ToSql(),
                new QueryBuilder().From.NpcTemplateCatalog().ToSql(),
                new QueryBuilder().From.NpcVisualCatalog().ToSql(),
                new QueryBuilder().From.ArenaInstance().ToSql(),
                new QueryBuilder().From.ContractVersion().ToSql(),
                new QueryBuilder().From.Party().ToSql(),
                new QueryBuilder().From.PartyMember().ToSql(),
                new QueryBuilder().From.PlaygroundTarget().ToSql(),
            };
        }

        internal static string[] BuildPvpMatchStaticQuerySqls()
        {
            // Disposable PvP databases have no party/playground state, world
            // content, or NPC actors. Keep this list explicit so adding a
            // table to the all-mode plan cannot silently enlarge match entry.
            return new[]
            {
                new QueryBuilder().From.AbilityCatalog().ToSql(),
                new QueryBuilder().From.ActionPresentationCatalog().ToSql(),
                new QueryBuilder().From.CombatVfxCueCatalog().ToSql(),
                new QueryBuilder().From.CombatProjectileDefinition().ToSql(),
                new QueryBuilder().From.CombatProfileCatalog().ToSql(),
                new QueryBuilder().From.CombatDisciplineCatalog().ToSql(),
                new QueryBuilder().From.CombatModeCatalog().ToSql(),
                new QueryBuilder().From.ActionBarSlotCatalog().ToSql(),
                new QueryBuilder().From.ItemDefinition().ToSql(),
                new QueryBuilder().From.ArmorSetDefinition().ToSql(),
                new QueryBuilder().From.ItemAffixDefinition().ToSql(),
                new QueryBuilder().From.SpellDefinition().ToSql(),
                new QueryBuilder().From.MeleeDefinition().ToSql(),
                new QueryBuilder().From.MeleeAbilityCatalog().ToSql(),
                new QueryBuilder().From.MeleeGapCloseCatalog().ToSql(),
                new QueryBuilder().From.MeleeAttackModifierCatalog().ToSql(),
                new QueryBuilder().From.AutoAttackCatalog().ToSql(),
                new QueryBuilder().From.CombatRuleCatalog().ToSql(),
                new QueryBuilder().From.ResourceCatalog().ToSql(),
                new QueryBuilder().From.StatScalingCatalog().ToSql(),
                new QueryBuilder().From.ContractVersion()
                    .Where(c => c.Key.Eq("map_data/arena_map_01.layout.shared.json"))
                    .ToSql(),
                new QueryBuilder().From.ContractVersion()
                    .Where(c => c.Key.Eq("map_data/arena_map_01.collision.shared.json"))
                    .ToSql(),
                new QueryBuilder().From.ContractVersion()
                    .Where(c => c.Key.Eq("map_data/arena_map_01.query_collision.shared.json"))
                    .ToSql(),
            };
        }

        internal static string[] BuildLocalQuerySqls(Identity localIdentity)
        {
            QueryBuilder qb = new();
            // Owner-key columns hold the server's lowercase identity_key()
            // hex; raw Identity.ToString() is uppercase and matches nothing.
            string localIdentityKey = OwnerKeys.For(localIdentity);
            return new[]
            {
                qb.From.PlayerWorld().Where(c => c.Identity.Eq(localIdentity)).ToSql(),
                new QueryBuilder().From.PlayerOpenWorldScene().Where(c => c.Identity.Eq(localIdentity)).ToSql(),
                new QueryBuilder().From.CharacterAppearance().Where(c => c.Owner.Eq(localIdentity)).ToSql(),
                new QueryBuilder().From.PlayerKnownSpell().Where(c => c.Owner.Eq(localIdentity)).ToSql(),
                new QueryBuilder().From.GlobalCooldown().Where(c => c.Caster.Eq(localIdentity)).ToSql(),
                new QueryBuilder().From.SpellCooldown().Where(c => c.Caster.Eq(localIdentity)).ToSql(),
                new QueryBuilder().From.RecallSlot().Where(c => c.Owner.Eq(localIdentity)).ToSql(),
                new QueryBuilder().From.CapacitorState().Where(c => c.Owner.Eq(localIdentity)).ToSql(),
                new QueryBuilder().From.PredictedActionResult().Where(c => c.Owner.Eq(localIdentity)).ToSql(),
                new QueryBuilder().From.ActiveDiceRoll().Where(c => c.Owner.Eq(localIdentity)).ToSql(),
                new QueryBuilder().From.FixedActionChargeState().Where(c => c.Owner.Eq(localIdentity)).ToSql(),
                new QueryBuilder().From.ActiveCombatDiscipline().Where(c => c.Owner.Eq(localIdentity)).ToSql(),
                new QueryBuilder().From.MatchCombatBuild().Where(c => c.Owner.Eq(localIdentity)).ToSql(),
                new QueryBuilder().From.MatchCombatBuildDiscipline().Where(c => c.Owner.Eq(localIdentity)).ToSql(),
                new QueryBuilder().From.MatchDisciplineConfiguration().Where(c => c.Owner.Eq(localIdentity)).ToSql(),
                new QueryBuilder().From.MatchStaffSchoolSelection().Where(c => c.Owner.Eq(localIdentity)).ToSql(),
                new QueryBuilder().From.MatchDisciplineActionBarAssignment().Where(c => c.Owner.Eq(localIdentity)).ToSql(),
                new QueryBuilder().From.MatchDisciplinePassiveSelection().Where(c => c.Owner.Eq(localIdentity)).ToSql(),
                new QueryBuilder().From.ActiveCombatMode().Where(c => c.Owner.Eq(localIdentity)).ToSql(),
                // Local swing scheduling (netcode design review S6): the
                // client schedules its own auto-attack presentation at
                // next_swing_at; only the owner's row replicates.
                new QueryBuilder().From.AutoAttackState().Where(c => c.Owner.Eq(localIdentity)).ToSql(),
                new QueryBuilder().From.PartyInvite().Where(c => c.Invitee.Eq(localIdentity)).ToSql(),
                new QueryBuilder().From.EquipmentLoadout().Where(c => c.Owner.Eq(localIdentity)).ToSql(),
                new QueryBuilder().From.PlayerEquipmentPresentation().Where(c => c.Owner.Eq(localIdentity)).ToSql(),
                new QueryBuilder().From.ActiveArmorSet().Where(c => c.Owner.Eq(localIdentity)).ToSql(),
                new QueryBuilder().From.SurvivalRun().Where(c => c.Owner.Eq(localIdentity)).ToSql(),
                new QueryBuilder().From.SurvivalResult().Where(c => c.Owner.Eq(localIdentity)).ToSql(),
                new QueryBuilder().From.SurvivalScore().Where(c => c.Owner.Eq(localIdentity)).ToSql(),
                // Inventory rows are owner-key filtered (netcode audit R4): the
                // client receives its own containers/slots/items plus unowned
                // world-loot rows. Corpse containers carry their killer/party
                // reservation owner and are replicated by dedicated scoped
                // corpse queries below. Other players'
                // inventories are never replicated. The parallel nullable
                // Identity columns cannot be compared to raw identity literals
                // in subscription SQL.
                new QueryBuilder().From.InventoryContainer().Where(c => c.OwnerKey.Eq(localIdentityKey)).ToSql(),
                new QueryBuilder().From.InventoryContainer().Where(c => c.OwnerKey.Eq(localIdentityKey))
                    .RightSemijoin(new QueryBuilder().From.InventorySlot(), (container, slot) => container.ContainerId.Eq(slot.ContainerId))
                    .ToSql(),
                new QueryBuilder().From.ItemInstance().Where(c => c.CurrentOwnerKey.Eq(localIdentityKey)).ToSql(),
                new QueryBuilder().From.ItemInstance().Where(c => c.CurrentOwnerKey.Eq(localIdentityKey))
                    .RightSemijoin(new QueryBuilder().From.ItemSpell(), (item, spell) => item.ItemInstanceId.Eq(spell.ItemInstanceId))
                    .ToSql(),
                new QueryBuilder().From.ItemInstance().Where(c => c.CurrentOwnerKey.Eq(localIdentityKey))
                    .RightSemijoin(new QueryBuilder().From.ItemAffixInstance(), (item, affix) => item.ItemInstanceId.Eq(affix.ItemInstanceId))
                    .ToSql(),
                // World-loot item rows cannot be world-scoped in one
                // subscription (container -> slot -> item is a three-table
                // chain), so unowned items and their spell/affix children are
                // replicated globally. Loot expires, so this set stays small.
                new QueryBuilder().From.ItemInstance().Where(c => c.CurrentOwnerKey.Eq(string.Empty)).ToSql(),
                new QueryBuilder().From.ItemInstance().Where(c => c.CurrentOwnerKey.Eq(string.Empty))
                    .RightSemijoin(new QueryBuilder().From.ItemSpell(), (item, spell) => item.ItemInstanceId.Eq(spell.ItemInstanceId))
                    .ToSql(),
                new QueryBuilder().From.ItemInstance().Where(c => c.CurrentOwnerKey.Eq(string.Empty))
                    .RightSemijoin(new QueryBuilder().From.ItemAffixInstance(), (item, affix) => item.ItemInstanceId.Eq(affix.ItemInstanceId))
                    .ToSql(),
            };
        }

        internal static string[] BuildPvpMatchLocalQuerySqls(Identity localIdentity)
        {
            string localIdentityKey = OwnerKeys.For(localIdentity);
            return new[]
            {
                new QueryBuilder().From.PlayerWorld()
                    .Where(c => c.Identity.Eq(localIdentity))
                    .ToSql(),
                // The local PlayerWorld row carries the assigned instance id,
                // allowing entry to receive exactly its ArenaInstance row
                // without subscribing to every arena in a generic database.
                new QueryBuilder().From.PlayerWorld()
                    .Where(c => c.Identity.Eq(localIdentity))
                    .RightSemijoin(
                        new QueryBuilder().From.ArenaInstance(),
                        (world, arena) => world.InstanceScopeId.Eq(arena.Id))
                    .ToSql(),
                new QueryBuilder().From.CharacterAppearance()
                    .Where(c => c.Owner.Eq(localIdentity))
                    .ToSql(),
                new QueryBuilder().From.GlobalCooldown()
                    .Where(c => c.Caster.Eq(localIdentity))
                    .ToSql(),
                new QueryBuilder().From.SpellCooldown()
                    .Where(c => c.Caster.Eq(localIdentity))
                    .ToSql(),
                new QueryBuilder().From.CapacitorState()
                    .Where(c => c.Owner.Eq(localIdentity))
                    .ToSql(),
                new QueryBuilder().From.PredictedActionResult()
                    .Where(c => c.Owner.Eq(localIdentity))
                    .ToSql(),
                new QueryBuilder().From.FixedActionChargeState()
                    .Where(c => c.Owner.Eq(localIdentity))
                    .ToSql(),
                new QueryBuilder().From.ActiveCombatDiscipline()
                    .Where(c => c.Owner.Eq(localIdentity))
                    .ToSql(),
                new QueryBuilder().From.MatchCombatBuild()
                    .Where(c => c.Owner.Eq(localIdentity))
                    .ToSql(),
                new QueryBuilder().From.MatchCombatBuildDiscipline()
                    .Where(c => c.Owner.Eq(localIdentity))
                    .ToSql(),
                new QueryBuilder().From.MatchDisciplineConfiguration()
                    .Where(c => c.Owner.Eq(localIdentity))
                    .ToSql(),
                new QueryBuilder().From.MatchStaffSchoolSelection()
                    .Where(c => c.Owner.Eq(localIdentity))
                    .ToSql(),
                new QueryBuilder().From.MatchDisciplineActionBarAssignment()
                    .Where(c => c.Owner.Eq(localIdentity))
                    .ToSql(),
                new QueryBuilder().From.MatchDisciplinePassiveSelection()
                    .Where(c => c.Owner.Eq(localIdentity))
                    .ToSql(),
                new QueryBuilder().From.ActiveCombatMode()
                    .Where(c => c.Owner.Eq(localIdentity))
                    .ToSql(),
                new QueryBuilder().From.AutoAttackState()
                    .Where(c => c.Owner.Eq(localIdentity))
                    .ToSql(),
                new QueryBuilder().From.EquipmentLoadout()
                    .Where(c => c.Owner.Eq(localIdentity))
                    .ToSql(),
                new QueryBuilder().From.PlayerEquipmentPresentation()
                    .Where(c => c.Owner.Eq(localIdentity))
                    .ToSql(),
                new QueryBuilder().From.ActiveArmorSet()
                    .Where(c => c.Owner.Eq(localIdentity))
                    .ToSql(),
                // Combat/action-bar resolution needs the local player's item
                // aggregate. Containers, slots, and unowned world loot do not
                // participate in a disposable arena match.
                new QueryBuilder().From.ItemInstance()
                    .Where(c => c.CurrentOwnerKey.Eq(localIdentityKey))
                    .ToSql(),
                new QueryBuilder().From.ItemInstance()
                    .Where(c => c.CurrentOwnerKey.Eq(localIdentityKey))
                    .RightSemijoin(
                        new QueryBuilder().From.ItemSpell(),
                        (item, spell) => item.ItemInstanceId.Eq(spell.ItemInstanceId))
                    .ToSql(),
                new QueryBuilder().From.ItemInstance()
                    .Where(c => c.CurrentOwnerKey.Eq(localIdentityKey))
                    .RightSemijoin(
                        new QueryBuilder().From.ItemAffixInstance(),
                        (item, affix) => item.ItemInstanceId.Eq(affix.ItemInstanceId))
                    .ToSql(),
            };
        }

        internal static string[] BuildPvpMatchInitialQuerySqls(Identity localIdentity)
        {
            string[] staticQueries = BuildPvpMatchStaticQuerySqls();
            string[] localQueries = BuildPvpMatchLocalQuerySqls(localIdentity);
            var queries = new List<string>(staticQueries.Length + localQueries.Length);
            queries.AddRange(staticQueries);
            queries.AddRange(localQueries);
            return queries.ToArray();
        }

        internal static string[] BuildScopedQuerySqls(NetworkManager.GameplayScope scope)
        {
            if (scope.Kind == NetworkManager.GameplayScopeKind.None)
                throw new InvalidOperationException("Scoped query plan requested for GameplayScope.None.");

            var queries = new List<string>
            {
                BuildScopedPlayerQuery(new QueryBuilder(), scope),
                BuildScopedCharacterAppearanceQuery(new QueryBuilder(), scope),
                BuildScopedPlayerEquipmentPresentationQuery(new QueryBuilder(), scope),
                BuildScopedPlayerPhysicsQuery(new QueryBuilder(), scope),
                BuildScopedPlayerStateQuery(new QueryBuilder(), scope),
                BuildScopedActiveCombatModeQuery(new QueryBuilder(), scope),
                BuildScopedNpcInstanceQuery(new QueryBuilder(), scope),
                BuildScopedNpcPhysicsQuery(new QueryBuilder(), scope),
                BuildScopedNpcStateQuery(new QueryBuilder(), scope),
                BuildScopedCombatEngagementQuery(new QueryBuilder(), scope),
                BuildScopedPlayerResourceQuery(new QueryBuilder(), scope),
                BuildScopedDefenseStateQuery(new QueryBuilder(), scope),
                BuildScopedActiveCastQuery(new QueryBuilder(), scope),
                BuildScopedActiveWorldInteractionQuery(new QueryBuilder(), scope),
                BuildScopedActiveRadialEffectQuery(new QueryBuilder(), scope),
                BuildScopedActivePersistentAreaQuery(new QueryBuilder(), scope),
                BuildScopedActiveWorldObstacleQuery(new QueryBuilder(), scope),
                BuildScopedActiveSanctuaryZoneQuery(new QueryBuilder(), scope),
                BuildScopedActiveNecroPrisonQuery(new QueryBuilder(), scope),
                BuildScopedWorldDoorStateQuery(new QueryBuilder(), scope),
                BuildScopedWorldTrapStateQuery(new QueryBuilder(), scope),
                BuildScopedMovementActionStateQuery(new QueryBuilder(), scope),
                BuildScopedSpecialMovementRuntimeQuery(new QueryBuilder(), scope),
                BuildScopedLingeringShadeStateQuery(new QueryBuilder(), scope),
                BuildScopedStatusEffectQuery(new QueryBuilder(), scope),
                BuildScopedNpcStatusEffectQuery(new QueryBuilder(), scope),
                BuildScopedPlayerSourceCombatEffectEventQuery(new QueryBuilder(), scope),
                BuildScopedPlayerTargetCombatEffectEventQuery(new QueryBuilder(), scope),
                BuildScopedNpcSourceCombatEffectEventQuery(new QueryBuilder(), scope),
                BuildScopedNpcTargetCombatEffectEventQuery(new QueryBuilder(), scope),
                BuildScopedCombatEventQuery(new QueryBuilder(), scope),
                BuildScopedNpcCombatEventQuery(new QueryBuilder(), scope),
                BuildScopedPlayerTargetCombatEventQuery(new QueryBuilder(), scope),
                BuildScopedProjectilePresentationEventQuery(new QueryBuilder(), scope),
                BuildScopedNpcProjectilePresentationEventQuery(new QueryBuilder(), scope),
                BuildScopedPlayerEventQuery(new QueryBuilder(), scope),
                BuildScopedLootContainerQuery(new QueryBuilder(), scope),
                BuildScopedLootSlotQuery(new QueryBuilder(), scope),
                BuildScopedCorpseLootContainerQuery(new QueryBuilder(), scope),
                BuildScopedCorpseLootSlotQuery(new QueryBuilder(), scope),
            };

            if (scope.Kind == NetworkManager.GameplayScopeKind.Instance)
            {
                ulong instanceId = scope.InstanceId.GetValueOrDefault();
                queries.Add(new QueryBuilder().From.ArenaMatch()
                    .Where(c => c.InstanceId.Eq(instanceId))
                    .ToSql());
                queries.Add(new QueryBuilder().From.MatchParticipant()
                    .Where(c => c.InstanceId.Eq(instanceId))
                    .ToSql());
                queries.Add(BuildScopedMatchParticipantStatsQuery(new QueryBuilder(), scope).ToSql());
                queries.Add(new QueryBuilder().From.SurvivalShopOffer()
                    .Where(c => c.ArenaId.Eq(instanceId))
                    .ToSql());
            }

            return queries.ToArray();
        }

        internal static string[] BuildPvpMatchScopedQuerySqls(NetworkManager.GameplayScope scope)
        {
            return WithoutTables(
                BuildScopedQuerySqls(scope),
                "active_world_interaction",
                "world_door_state",
                "world_trap_state",
                "survival_shop_offer",
                "inventory_container",
                "inventory_slot");
        }

        private static string[] WithoutTables(string[] queries, params string[] unavailableTables)
        {
            var filtered = new List<string>(queries.Length);
            foreach (string query in queries)
            {
                bool unavailable = false;
                foreach (string table in unavailableTables)
                {
                    if (query.IndexOf($"\"{table}\"", StringComparison.Ordinal) >= 0)
                    {
                        unavailable = true;
                        break;
                    }
                }

                if (!unavailable)
                    filtered.Add(query);
            }

            return filtered.ToArray();
        }

        private static string BuildScopedLootContainerQuery(QueryBuilder qb, NetworkManager.GameplayScope scope)
        {
            return scope.Kind switch
            {
                NetworkManager.GameplayScopeKind.OpenWorld => qb
                    .From
                    .InventoryContainer()
                    .Where(c => c.OwnerKey.Eq(string.Empty))
                    .Where(c => c.WorldKind.Eq("OPEN"))
                    .Where(c => c.OpenWorldSceneName.Eq(OpenWorldSceneName(scope)))
                    .ToSql(),
                NetworkManager.GameplayScopeKind.Instance => qb
                    .From
                    .InventoryContainer()
                    .Where(c => c.OwnerKey.Eq(string.Empty))
                    .Where(c => c.WorldKind.Eq("INSTANCE"))
                    .Where(c => c.InstanceScopeId.Eq(scope.InstanceId.GetValueOrDefault()))
                    .ToSql(),
                _ => throw new InvalidOperationException("Scoped loot-container query requested for GameplayScope.None"),
            };
        }

        private static string BuildScopedLootSlotQuery(QueryBuilder qb, NetworkManager.GameplayScope scope)
        {
            return scope.Kind switch
            {
                NetworkManager.GameplayScopeKind.OpenWorld => qb
                    .From
                    .InventoryContainer()
                    .Where(c => c.OwnerKey.Eq(string.Empty))
                    .Where(c => c.WorldKind.Eq("OPEN"))
                    .Where(c => c.OpenWorldSceneName.Eq(OpenWorldSceneName(scope)))
                    .RightSemijoin(qb.From.InventorySlot(), (container, slot) => container.ContainerId.Eq(slot.ContainerId))
                    .ToSql(),
                NetworkManager.GameplayScopeKind.Instance => qb
                    .From
                    .InventoryContainer()
                    .Where(c => c.OwnerKey.Eq(string.Empty))
                    .Where(c => c.WorldKind.Eq("INSTANCE"))
                    .Where(c => c.InstanceScopeId.Eq(scope.InstanceId.GetValueOrDefault()))
                    .RightSemijoin(qb.From.InventorySlot(), (container, slot) => container.ContainerId.Eq(slot.ContainerId))
                    .ToSql(),
                _ => throw new InvalidOperationException("Scoped loot-slot query requested for GameplayScope.None"),
            };
        }

        private static string BuildScopedCorpseLootContainerQuery(QueryBuilder qb, NetworkManager.GameplayScope scope)
        {
            return scope.Kind switch
            {
                NetworkManager.GameplayScopeKind.OpenWorld => qb
                    .From
                    .InventoryContainer()
                    .Where(c => c.ContainerKind.Eq("CORPSE"))
                    .Where(c => c.WorldKind.Eq("OPEN"))
                    .Where(c => c.OpenWorldSceneName.Eq(OpenWorldSceneName(scope)))
                    .ToSql(),
                NetworkManager.GameplayScopeKind.Instance => qb
                    .From
                    .InventoryContainer()
                    .Where(c => c.ContainerKind.Eq("CORPSE"))
                    .Where(c => c.WorldKind.Eq("INSTANCE"))
                    .Where(c => c.InstanceScopeId.Eq(scope.InstanceId.GetValueOrDefault()))
                    .ToSql(),
                _ => throw new InvalidOperationException("Scoped corpse-container query requested for GameplayScope.None"),
            };
        }

        private static string BuildScopedCorpseLootSlotQuery(QueryBuilder qb, NetworkManager.GameplayScope scope)
        {
            return scope.Kind switch
            {
                NetworkManager.GameplayScopeKind.OpenWorld => qb
                    .From
                    .InventoryContainer()
                    .Where(c => c.ContainerKind.Eq("CORPSE"))
                    .Where(c => c.WorldKind.Eq("OPEN"))
                    .Where(c => c.OpenWorldSceneName.Eq(OpenWorldSceneName(scope)))
                    .RightSemijoin(qb.From.InventorySlot(), (container, slot) => container.ContainerId.Eq(slot.ContainerId))
                    .ToSql(),
                NetworkManager.GameplayScopeKind.Instance => qb
                    .From
                    .InventoryContainer()
                    .Where(c => c.ContainerKind.Eq("CORPSE"))
                    .Where(c => c.WorldKind.Eq("INSTANCE"))
                    .Where(c => c.InstanceScopeId.Eq(scope.InstanceId.GetValueOrDefault()))
                    .RightSemijoin(qb.From.InventorySlot(), (container, slot) => container.ContainerId.Eq(slot.ContainerId))
                    .ToSql(),
                _ => throw new InvalidOperationException("Scoped corpse-slot query requested for GameplayScope.None"),
            };
        }

        private static string BuildScopedPlayerQuery(QueryBuilder qb, NetworkManager.GameplayScope scope)
        {
            return scope.Kind switch
            {
                NetworkManager.GameplayScopeKind.OpenWorld => qb
                    .From
                    .PlayerWorld()
                    .Where(c => c.WorldKind.Eq("OPEN"))
                    .Where(c => c.OpenWorldSceneName.Eq(OpenWorldSceneName(scope)))
                    .RightSemijoin(qb.From.Player(), (world, player) => world.Identity.Eq(player.Identity))
                    .ToSql(),
                NetworkManager.GameplayScopeKind.Instance => qb
                    .From
                    .PlayerWorld()
                    .Where(c => c.WorldKind.Eq("INSTANCE"))
                    .Where(c => c.InstanceScopeId.Eq(scope.InstanceId.GetValueOrDefault()))
                    .RightSemijoin(qb.From.Player(), (world, player) => world.Identity.Eq(player.Identity))
                    .ToSql(),
                _ => throw new InvalidOperationException("Scoped player query requested for GameplayScope.None"),
            };
        }

        private static string BuildScopedCharacterAppearanceQuery(QueryBuilder qb, NetworkManager.GameplayScope scope)
        {
            return scope.Kind switch
            {
                NetworkManager.GameplayScopeKind.OpenWorld => qb
                    .From
                    .PlayerWorld()
                    .Where(c => c.WorldKind.Eq("OPEN"))
                    .Where(c => c.OpenWorldSceneName.Eq(OpenWorldSceneName(scope)))
                    .RightSemijoin(qb.From.CharacterAppearance(), (world, appearance) => world.Identity.Eq(appearance.Owner))
                    .ToSql(),
                NetworkManager.GameplayScopeKind.Instance => qb
                    .From
                    .PlayerWorld()
                    .Where(c => c.WorldKind.Eq("INSTANCE"))
                    .Where(c => c.InstanceScopeId.Eq(scope.InstanceId.GetValueOrDefault()))
                    .RightSemijoin(qb.From.CharacterAppearance(), (world, appearance) => world.Identity.Eq(appearance.Owner))
                    .ToSql(),
                _ => throw new InvalidOperationException("Scoped character-appearance query requested for GameplayScope.None"),
            };
        }

        private static string BuildScopedPlayerEquipmentPresentationQuery(QueryBuilder qb, NetworkManager.GameplayScope scope)
        {
            return scope.Kind switch
            {
                NetworkManager.GameplayScopeKind.OpenWorld => qb
                    .From
                    .PlayerWorld()
                    .Where(c => c.WorldKind.Eq("OPEN"))
                    .Where(c => c.OpenWorldSceneName.Eq(OpenWorldSceneName(scope)))
                    .RightSemijoin(qb.From.PlayerEquipmentPresentation(), (world, equipment) => world.Identity.Eq(equipment.Owner))
                    .ToSql(),
                NetworkManager.GameplayScopeKind.Instance => qb
                    .From
                    .PlayerWorld()
                    .Where(c => c.WorldKind.Eq("INSTANCE"))
                    .Where(c => c.InstanceScopeId.Eq(scope.InstanceId.GetValueOrDefault()))
                    .RightSemijoin(qb.From.PlayerEquipmentPresentation(), (world, equipment) => world.Identity.Eq(equipment.Owner))
                    .ToSql(),
                _ => throw new InvalidOperationException("Scoped player-equipment-presentation query requested for GameplayScope.None"),
            };
        }

        private static string BuildScopedPlayerPhysicsQuery(QueryBuilder qb, NetworkManager.GameplayScope scope)
        {
            return scope.Kind switch
            {
                NetworkManager.GameplayScopeKind.OpenWorld => qb
                    .From
                    .PlayerWorld()
                    .Where(c => c.WorldKind.Eq("OPEN"))
                    .Where(c => c.OpenWorldSceneName.Eq(OpenWorldSceneName(scope)))
                    .RightSemijoin(qb.From.PlayerPhysics(), (world, physics) => world.Identity.Eq(physics.Identity))
                    .ToSql(),
                NetworkManager.GameplayScopeKind.Instance => qb
                    .From
                    .PlayerWorld()
                    .Where(c => c.WorldKind.Eq("INSTANCE"))
                    .Where(c => c.InstanceScopeId.Eq(scope.InstanceId.GetValueOrDefault()))
                    .RightSemijoin(qb.From.PlayerPhysics(), (world, physics) => world.Identity.Eq(physics.Identity))
                    .ToSql(),
                _ => throw new InvalidOperationException("Scoped player-physics query requested for GameplayScope.None"),
            };
        }

        private static string BuildScopedPlayerStateQuery(QueryBuilder qb, NetworkManager.GameplayScope scope)
        {
            return scope.Kind switch
            {
                NetworkManager.GameplayScopeKind.OpenWorld => qb
                    .From
                    .PlayerWorld()
                    .Where(c => c.WorldKind.Eq("OPEN"))
                    .Where(c => c.OpenWorldSceneName.Eq(OpenWorldSceneName(scope)))
                    .RightSemijoin(qb.From.PlayerState(), (world, state) => world.Identity.Eq(state.PlayerId))
                    .ToSql(),
                NetworkManager.GameplayScopeKind.Instance => qb
                    .From
                    .PlayerWorld()
                    .Where(c => c.WorldKind.Eq("INSTANCE"))
                    .Where(c => c.InstanceScopeId.Eq(scope.InstanceId.GetValueOrDefault()))
                    .RightSemijoin(qb.From.PlayerState(), (world, state) => world.Identity.Eq(state.PlayerId))
                    .ToSql(),
                _ => throw new InvalidOperationException("Scoped player-state query requested for GameplayScope.None"),
            };
        }

        private static string BuildScopedActiveCombatModeQuery(QueryBuilder qb, NetworkManager.GameplayScope scope)
        {
            return scope.Kind switch
            {
                NetworkManager.GameplayScopeKind.OpenWorld => qb
                    .From
                    .PlayerWorld()
                    .Where(c => c.WorldKind.Eq("OPEN"))
                    .Where(c => c.OpenWorldSceneName.Eq(OpenWorldSceneName(scope)))
                    .RightSemijoin(qb.From.ActiveCombatMode(), (world, mode) => world.Identity.Eq(mode.Owner))
                    .ToSql(),
                NetworkManager.GameplayScopeKind.Instance => qb
                    .From
                    .PlayerWorld()
                    .Where(c => c.WorldKind.Eq("INSTANCE"))
                    .Where(c => c.InstanceScopeId.Eq(scope.InstanceId.GetValueOrDefault()))
                    .RightSemijoin(qb.From.ActiveCombatMode(), (world, mode) => world.Identity.Eq(mode.Owner))
                    .ToSql(),
                _ => throw new InvalidOperationException("Scoped active-combat-mode query requested for GameplayScope.None"),
            };
        }

        private static string BuildScopedNpcInstanceQuery(QueryBuilder qb, NetworkManager.GameplayScope scope)
        {
            return scope.Kind switch
            {
                NetworkManager.GameplayScopeKind.OpenWorld => qb
                    .From
                    .NpcInstance()
                    .Where(c => c.WorldKind.Eq("OPEN"))
                    .Where(c => c.OpenWorldSceneName.Eq(OpenWorldSceneName(scope)))
                    .ToSql(),
                NetworkManager.GameplayScopeKind.Instance => qb
                    .From
                    .NpcInstance()
                    .Where(c => c.WorldKind.Eq("INSTANCE"))
                    .Where(c => c.InstanceScopeId.Eq(scope.InstanceId.GetValueOrDefault()))
                    .ToSql(),
                _ => throw new InvalidOperationException("Scoped NPC instance query requested for GameplayScope.None"),
            };
        }

        private static string BuildScopedNpcPhysicsQuery(QueryBuilder qb, NetworkManager.GameplayScope scope)
        {
            return scope.Kind switch
            {
                NetworkManager.GameplayScopeKind.OpenWorld => qb
                    .From
                    .NpcInstance()
                    .Where(c => c.WorldKind.Eq("OPEN"))
                    .Where(c => c.OpenWorldSceneName.Eq(OpenWorldSceneName(scope)))
                    .RightSemijoin(qb.From.NpcPhysics(), (npc, physics) => npc.Identity.Eq(physics.Identity))
                    .ToSql(),
                NetworkManager.GameplayScopeKind.Instance => qb
                    .From
                    .NpcInstance()
                    .Where(c => c.WorldKind.Eq("INSTANCE"))
                    .Where(c => c.InstanceScopeId.Eq(scope.InstanceId.GetValueOrDefault()))
                    .RightSemijoin(qb.From.NpcPhysics(), (npc, physics) => npc.Identity.Eq(physics.Identity))
                    .ToSql(),
                _ => throw new InvalidOperationException("Scoped NPC physics query requested for GameplayScope.None"),
            };
        }

        private static string BuildScopedNpcStateQuery(QueryBuilder qb, NetworkManager.GameplayScope scope)
        {
            return scope.Kind switch
            {
                NetworkManager.GameplayScopeKind.OpenWorld => qb
                    .From
                    .NpcInstance()
                    .Where(c => c.WorldKind.Eq("OPEN"))
                    .Where(c => c.OpenWorldSceneName.Eq(OpenWorldSceneName(scope)))
                    .RightSemijoin(qb.From.NpcState(), (npc, state) => npc.Identity.Eq(state.Identity))
                    .ToSql(),
                NetworkManager.GameplayScopeKind.Instance => qb
                    .From
                    .NpcInstance()
                    .Where(c => c.WorldKind.Eq("INSTANCE"))
                    .Where(c => c.InstanceScopeId.Eq(scope.InstanceId.GetValueOrDefault()))
                    .RightSemijoin(qb.From.NpcState(), (npc, state) => npc.Identity.Eq(state.Identity))
                    .ToSql(),
                _ => throw new InvalidOperationException("Scoped NPC state query requested for GameplayScope.None"),
            };
        }

        private static string BuildScopedPlayerResourceQuery(QueryBuilder qb, NetworkManager.GameplayScope scope)
        {
            return scope.Kind switch
            {
                NetworkManager.GameplayScopeKind.OpenWorld => qb
                    .From
                    .PlayerWorld()
                    .Where(c => c.WorldKind.Eq("OPEN"))
                    .Where(c => c.OpenWorldSceneName.Eq(OpenWorldSceneName(scope)))
                    .RightSemijoin(qb.From.PlayerResource(), (world, resource) => world.Identity.Eq(resource.Owner))
                    .ToSql(),
                NetworkManager.GameplayScopeKind.Instance => qb
                    .From
                    .PlayerWorld()
                    .Where(c => c.WorldKind.Eq("INSTANCE"))
                    .Where(c => c.InstanceScopeId.Eq(scope.InstanceId.GetValueOrDefault()))
                    .RightSemijoin(qb.From.PlayerResource(), (world, resource) => world.Identity.Eq(resource.Owner))
                    .ToSql(),
                _ => throw new InvalidOperationException("Scoped player-resource query requested for GameplayScope.None"),
            };
        }

        private static string BuildScopedDefenseStateQuery(QueryBuilder qb, NetworkManager.GameplayScope scope)
        {
            return scope.Kind switch
            {
                NetworkManager.GameplayScopeKind.OpenWorld => qb
                    .From
                    .PlayerWorld()
                    .Where(c => c.WorldKind.Eq("OPEN"))
                    .Where(c => c.OpenWorldSceneName.Eq(OpenWorldSceneName(scope)))
                    .RightSemijoin(qb.From.DefenseState(), (world, defense) => world.Identity.Eq(defense.Owner))
                    .ToSql(),
                NetworkManager.GameplayScopeKind.Instance => qb
                    .From
                    .PlayerWorld()
                    .Where(c => c.WorldKind.Eq("INSTANCE"))
                    .Where(c => c.InstanceScopeId.Eq(scope.InstanceId.GetValueOrDefault()))
                    .RightSemijoin(qb.From.DefenseState(), (world, defense) => world.Identity.Eq(defense.Owner))
                    .ToSql(),
                _ => throw new InvalidOperationException("Scoped defense-state query requested for GameplayScope.None"),
            };
        }

        private static string BuildScopedActiveCastQuery(QueryBuilder qb, NetworkManager.GameplayScope scope)
        {
            return scope.Kind switch
            {
                NetworkManager.GameplayScopeKind.OpenWorld => qb
                    .From
                    .PlayerWorld()
                    .Where(c => c.WorldKind.Eq("OPEN"))
                    .Where(c => c.OpenWorldSceneName.Eq(OpenWorldSceneName(scope)))
                    .RightSemijoin(qb.From.ActiveCast(), (world, cast) => world.Identity.Eq(cast.Caster))
                    .ToSql(),
                NetworkManager.GameplayScopeKind.Instance => qb
                    .From
                    .PlayerWorld()
                    .Where(c => c.WorldKind.Eq("INSTANCE"))
                    .Where(c => c.InstanceScopeId.Eq(scope.InstanceId.GetValueOrDefault()))
                    .RightSemijoin(qb.From.ActiveCast(), (world, cast) => world.Identity.Eq(cast.Caster))
                    .ToSql(),
                _ => throw new InvalidOperationException("Scoped active-cast query requested for GameplayScope.None"),
            };
        }

        private static string BuildScopedActiveRadialEffectQuery(QueryBuilder qb, NetworkManager.GameplayScope scope)
        {
            return scope.Kind switch
            {
                NetworkManager.GameplayScopeKind.OpenWorld => qb
                    .From
                    .PlayerWorld()
                    .Where(c => c.WorldKind.Eq("OPEN"))
                    .Where(c => c.OpenWorldSceneName.Eq(OpenWorldSceneName(scope)))
                    .RightSemijoin(qb.From.ActiveRadialEffect(), (world, effect) => world.Identity.Eq(effect.Owner))
                    .ToSql(),
                NetworkManager.GameplayScopeKind.Instance => qb
                    .From
                    .PlayerWorld()
                    .Where(c => c.WorldKind.Eq("INSTANCE"))
                    .Where(c => c.InstanceScopeId.Eq(scope.InstanceId.GetValueOrDefault()))
                    .RightSemijoin(qb.From.ActiveRadialEffect(), (world, effect) => world.Identity.Eq(effect.Owner))
                    .ToSql(),
                _ => throw new InvalidOperationException("Scoped active-radial-effect query requested for GameplayScope.None"),
            };
        }

        private static string BuildScopedActivePersistentAreaQuery(QueryBuilder qb, NetworkManager.GameplayScope scope)
        {
            return scope.Kind switch
            {
                NetworkManager.GameplayScopeKind.OpenWorld => qb
                    .From
                    .PlayerWorld()
                    .Where(c => c.WorldKind.Eq("OPEN"))
                    .Where(c => c.OpenWorldSceneName.Eq(OpenWorldSceneName(scope)))
                    .RightSemijoin(qb.From.ActivePersistentArea(), (world, area) => world.Identity.Eq(area.Caster))
                    .ToSql(),
                NetworkManager.GameplayScopeKind.Instance => qb
                    .From
                    .PlayerWorld()
                    .Where(c => c.WorldKind.Eq("INSTANCE"))
                    .Where(c => c.InstanceScopeId.Eq(scope.InstanceId.GetValueOrDefault()))
                    .RightSemijoin(qb.From.ActivePersistentArea(), (world, area) => world.Identity.Eq(area.Caster))
                    .ToSql(),
                _ => throw new InvalidOperationException("Scoped active-persistent-area query requested for GameplayScope.None"),
            };
        }

        private static string BuildScopedActiveWorldInteractionQuery(
            QueryBuilder qb,
            NetworkManager.GameplayScope scope)
        {
            return scope.Kind switch
            {
                NetworkManager.GameplayScopeKind.OpenWorld => qb
                    .From
                    .ActiveWorldInteraction()
                    .Where(c => c.WorldKind.Eq("OPEN"))
                    .Where(c => c.OpenWorldSceneName.Eq(OpenWorldSceneName(scope)))
                    .ToSql(),
                NetworkManager.GameplayScopeKind.Instance => qb
                    .From
                    .ActiveWorldInteraction()
                    .Where(c => c.WorldKind.Eq("INSTANCE"))
                    .Where(c => c.InstanceScopeId.Eq(scope.InstanceId.GetValueOrDefault()))
                    .ToSql(),
                _ => throw new InvalidOperationException(
                    "Scoped active-world-interaction query requested for GameplayScope.None"),
            };
        }

        private static string BuildScopedActiveWorldObstacleQuery(QueryBuilder qb, NetworkManager.GameplayScope scope)
        {
            return scope.Kind switch
            {
                NetworkManager.GameplayScopeKind.OpenWorld => qb
                    .From
                    .ActiveWorldObstacle()
                    .Where(c => c.WorldKind.Eq("OPEN"))
                    .Where(c => c.OpenWorldSceneName.Eq(OpenWorldSceneName(scope)))
                    .ToSql(),
                NetworkManager.GameplayScopeKind.Instance => qb
                    .From
                    .ActiveWorldObstacle()
                    .Where(c => c.WorldKind.Eq("INSTANCE"))
                    .Where(c => c.InstanceScopeId.Eq(scope.InstanceId.GetValueOrDefault()))
                    .ToSql(),
                _ => throw new InvalidOperationException("Scoped active-world-obstacle query requested for GameplayScope.None"),
            };
        }

        private static string BuildScopedWorldDoorStateQuery(
            QueryBuilder qb,
            NetworkManager.GameplayScope scope)
        {
            return scope.Kind switch
            {
                NetworkManager.GameplayScopeKind.OpenWorld => qb
                    .From
                    .WorldDoorState()
                    .Where(c => c.WorldKind.Eq("OPEN"))
                    .Where(c => c.OpenWorldSceneName.Eq(OpenWorldSceneName(scope)))
                    .ToSql(),
                NetworkManager.GameplayScopeKind.Instance => qb
                    .From
                    .WorldDoorState()
                    .Where(c => c.WorldKind.Eq("INSTANCE"))
                    .Where(c => c.InstanceScopeId.Eq(scope.InstanceId.GetValueOrDefault()))
                    .ToSql(),
                _ => throw new InvalidOperationException(
                    "Scoped world-door-state query requested for GameplayScope.None"),
            };
        }

        private static string BuildScopedActiveSanctuaryZoneQuery(
            QueryBuilder qb,
            NetworkManager.GameplayScope scope)
        {
            return scope.Kind switch
            {
                NetworkManager.GameplayScopeKind.OpenWorld => qb
                    .From
                    .ActiveSanctuaryZone()
                    .Where(c => c.WorldKind.Eq("OPEN"))
                    .Where(c => c.OpenWorldSceneName.Eq(OpenWorldSceneName(scope)))
                    .ToSql(),
                NetworkManager.GameplayScopeKind.Instance => qb
                    .From
                    .ActiveSanctuaryZone()
                    .Where(c => c.WorldKind.Eq("INSTANCE"))
                    .Where(c => c.InstanceScopeId.Eq(scope.InstanceId.GetValueOrDefault()))
                    .ToSql(),
                _ => throw new InvalidOperationException(
                    "Scoped active-sanctuary-zone query requested for GameplayScope.None"),
            };
        }

        private static string BuildScopedActiveNecroPrisonQuery(
            QueryBuilder qb,
            NetworkManager.GameplayScope scope)
        {
            return scope.Kind switch
            {
                NetworkManager.GameplayScopeKind.OpenWorld => qb
                    .From
                    .ActiveNecroPrison()
                    .Where(c => c.WorldKind.Eq("OPEN"))
                    .Where(c => c.OpenWorldSceneName.Eq(OpenWorldSceneName(scope)))
                    .ToSql(),
                NetworkManager.GameplayScopeKind.Instance => qb
                    .From
                    .ActiveNecroPrison()
                    .Where(c => c.WorldKind.Eq("INSTANCE"))
                    .Where(c => c.InstanceScopeId.Eq(scope.InstanceId.GetValueOrDefault()))
                    .ToSql(),
                _ => throw new InvalidOperationException(
                    "Scoped active-necro-prison query requested for GameplayScope.None"),
            };
        }

        private static string BuildScopedWorldTrapStateQuery(
            QueryBuilder qb,
            NetworkManager.GameplayScope scope)
        {
            return scope.Kind switch
            {
                NetworkManager.GameplayScopeKind.OpenWorld => qb
                    .From
                    .WorldTrapState()
                    .Where(c => c.WorldKind.Eq("OPEN"))
                    .Where(c => c.OpenWorldSceneName.Eq(OpenWorldSceneName(scope)))
                    .ToSql(),
                NetworkManager.GameplayScopeKind.Instance => qb
                    .From
                    .WorldTrapState()
                    .Where(c => c.WorldKind.Eq("INSTANCE"))
                    .Where(c => c.InstanceScopeId.Eq(scope.InstanceId.GetValueOrDefault()))
                    .ToSql(),
                _ => throw new InvalidOperationException(
                    "Scoped world-trap-state query requested for GameplayScope.None"),
            };
        }

        private static string BuildScopedSpecialMovementRuntimeQuery(QueryBuilder qb, NetworkManager.GameplayScope scope)
        {
            return scope.Kind switch
            {
                NetworkManager.GameplayScopeKind.OpenWorld => qb
                    .From
                    .PlayerWorld()
                    .Where(c => c.WorldKind.Eq("OPEN"))
                    .Where(c => c.OpenWorldSceneName.Eq(OpenWorldSceneName(scope)))
                    .RightSemijoin(qb.From.SpecialMovementRuntime(), (world, runtime) => world.Identity.Eq(runtime.Owner))
                    .ToSql(),
                NetworkManager.GameplayScopeKind.Instance => qb
                    .From
                    .PlayerWorld()
                    .Where(c => c.WorldKind.Eq("INSTANCE"))
                    .Where(c => c.InstanceScopeId.Eq(scope.InstanceId.GetValueOrDefault()))
                    .RightSemijoin(qb.From.SpecialMovementRuntime(), (world, runtime) => world.Identity.Eq(runtime.Owner))
                    .ToSql(),
                _ => throw new InvalidOperationException("Scoped special-movement query requested for GameplayScope.None"),
            };
        }

        private static string BuildScopedLingeringShadeStateQuery(QueryBuilder qb, NetworkManager.GameplayScope scope)
        {
            return scope.Kind switch
            {
                NetworkManager.GameplayScopeKind.OpenWorld => qb
                    .From
                    .LingeringShadeState()
                    .Where(c => c.WorldKind.Eq("OPEN"))
                    .Where(c => c.OpenWorldSceneName.Eq(OpenWorldSceneName(scope)))
                    .ToSql(),
                NetworkManager.GameplayScopeKind.Instance => qb
                    .From
                    .LingeringShadeState()
                    .Where(c => c.WorldKind.Eq("INSTANCE"))
                    .Where(c => c.InstanceScopeId.Eq(scope.InstanceId.GetValueOrDefault()))
                    .ToSql(),
                _ => throw new InvalidOperationException("Scoped lingering-shade query requested for GameplayScope.None"),
            };
        }

        private static string BuildScopedMovementActionStateQuery(QueryBuilder qb, NetworkManager.GameplayScope scope)
        {
            return scope.Kind switch
            {
                NetworkManager.GameplayScopeKind.OpenWorld => qb
                    .From
                    .PlayerWorld()
                    .Where(c => c.WorldKind.Eq("OPEN"))
                    .Where(c => c.OpenWorldSceneName.Eq(OpenWorldSceneName(scope)))
                    .RightSemijoin(qb.From.MovementActionState(), (world, action) => world.Identity.Eq(action.Owner))
                    .ToSql(),
                NetworkManager.GameplayScopeKind.Instance => qb
                    .From
                    .PlayerWorld()
                    .Where(c => c.WorldKind.Eq("INSTANCE"))
                    .Where(c => c.InstanceScopeId.Eq(scope.InstanceId.GetValueOrDefault()))
                    .RightSemijoin(qb.From.MovementActionState(), (world, action) => world.Identity.Eq(action.Owner))
                    .ToSql(),
                _ => throw new InvalidOperationException("Scoped movement-action query requested for GameplayScope.None"),
            };
        }

        private static string BuildScopedStatusEffectQuery(QueryBuilder qb, NetworkManager.GameplayScope scope)
        {
            return scope.Kind switch
            {
                NetworkManager.GameplayScopeKind.OpenWorld => qb
                    .From
                    .PlayerWorld()
                    .Where(c => c.WorldKind.Eq("OPEN"))
                    .Where(c => c.OpenWorldSceneName.Eq(OpenWorldSceneName(scope)))
                    .RightSemijoin(qb.From.StatusEffect(), (world, effect) => world.Identity.Eq(effect.Target))
                    .ToSql(),
                NetworkManager.GameplayScopeKind.Instance => qb
                    .From
                    .PlayerWorld()
                    .Where(c => c.WorldKind.Eq("INSTANCE"))
                    .Where(c => c.InstanceScopeId.Eq(scope.InstanceId.GetValueOrDefault()))
                    .RightSemijoin(qb.From.StatusEffect(), (world, effect) => world.Identity.Eq(effect.Target))
                    .ToSql(),
                _ => throw new InvalidOperationException("Scoped status-effect query requested for GameplayScope.None"),
            };
        }

        private static string BuildScopedNpcStatusEffectQuery(QueryBuilder qb, NetworkManager.GameplayScope scope)
        {
            return scope.Kind switch
            {
                NetworkManager.GameplayScopeKind.OpenWorld => qb
                    .From
                    .NpcInstance()
                    .Where(c => c.WorldKind.Eq("OPEN"))
                    .Where(c => c.OpenWorldSceneName.Eq(OpenWorldSceneName(scope)))
                    .RightSemijoin(qb.From.StatusEffect(), (npc, effect) => npc.Identity.Eq(effect.Target))
                    .ToSql(),
                NetworkManager.GameplayScopeKind.Instance => qb
                    .From
                    .NpcInstance()
                    .Where(c => c.WorldKind.Eq("INSTANCE"))
                    .Where(c => c.InstanceScopeId.Eq(scope.InstanceId.GetValueOrDefault()))
                    .RightSemijoin(qb.From.StatusEffect(), (npc, effect) => npc.Identity.Eq(effect.Target))
                    .ToSql(),
                _ => throw new InvalidOperationException("Scoped NPC status-effect query requested for GameplayScope.None"),
            };
        }

        private static string BuildScopedCombatEngagementQuery(QueryBuilder qb, NetworkManager.GameplayScope scope)
        {
            return scope.Kind switch
            {
                NetworkManager.GameplayScopeKind.OpenWorld => qb
                    .From
                    .PlayerWorld()
                    .Where(c => c.WorldKind.Eq("OPEN"))
                    .Where(c => c.OpenWorldSceneName.Eq(OpenWorldSceneName(scope)))
                    .RightSemijoin(qb.From.CombatEngagement(), (world, engagement) => world.Identity.Eq(engagement.Owner))
                    .ToSql(),
                NetworkManager.GameplayScopeKind.Instance => qb
                    .From
                    .PlayerWorld()
                    .Where(c => c.WorldKind.Eq("INSTANCE"))
                    .Where(c => c.InstanceScopeId.Eq(scope.InstanceId.GetValueOrDefault()))
                    .RightSemijoin(qb.From.CombatEngagement(), (world, engagement) => world.Identity.Eq(engagement.Owner))
                    .ToSql(),
                _ => throw new InvalidOperationException("Scoped combat-engagement query requested for GameplayScope.None"),
            };
        }

        private static string BuildScopedCombatEventQuery(QueryBuilder qb, NetworkManager.GameplayScope scope)
        {
            return scope.Kind switch
            {
                NetworkManager.GameplayScopeKind.OpenWorld => qb
                    .From
                    .PlayerWorld()
                    .Where(c => c.WorldKind.Eq("OPEN"))
                    .Where(c => c.OpenWorldSceneName.Eq(OpenWorldSceneName(scope)))
                    .RightSemijoin(qb.From.CombatEvent(), (world, spellEvent) => world.Identity.Eq(spellEvent.Caster))
                    .ToSql(),
                NetworkManager.GameplayScopeKind.Instance => qb
                    .From
                    .PlayerWorld()
                    .Where(c => c.WorldKind.Eq("INSTANCE"))
                    .Where(c => c.InstanceScopeId.Eq(scope.InstanceId.GetValueOrDefault()))
                    .RightSemijoin(qb.From.CombatEvent(), (world, spellEvent) => world.Identity.Eq(spellEvent.Caster))
                    .ToSql(),
                _ => throw new InvalidOperationException("Scoped spell-event query requested for GameplayScope.None"),
            };
        }

        private static string BuildScopedPlayerSourceCombatEffectEventQuery(QueryBuilder qb, NetworkManager.GameplayScope scope)
        {
            return scope.Kind switch
            {
                NetworkManager.GameplayScopeKind.OpenWorld => qb
                    .From
                    .PlayerWorld()
                    .Where(c => c.WorldKind.Eq("OPEN"))
                    .Where(c => c.OpenWorldSceneName.Eq(OpenWorldSceneName(scope)))
                    .RightSemijoin(qb.From.CombatEffectEvent(), (world, effect) => world.Identity.Eq(effect.Source))
                    .ToSql(),
                NetworkManager.GameplayScopeKind.Instance => qb
                    .From
                    .PlayerWorld()
                    .Where(c => c.WorldKind.Eq("INSTANCE"))
                    .Where(c => c.InstanceScopeId.Eq(scope.InstanceId.GetValueOrDefault()))
                    .RightSemijoin(qb.From.CombatEffectEvent(), (world, effect) => world.Identity.Eq(effect.Source))
                    .ToSql(),
                _ => throw new InvalidOperationException("Scoped player-source combat-effect query requested for GameplayScope.None"),
            };
        }

        private static string BuildScopedPlayerTargetCombatEffectEventQuery(QueryBuilder qb, NetworkManager.GameplayScope scope)
        {
            return scope.Kind switch
            {
                NetworkManager.GameplayScopeKind.OpenWorld => qb
                    .From
                    .PlayerWorld()
                    .Where(c => c.WorldKind.Eq("OPEN"))
                    .Where(c => c.OpenWorldSceneName.Eq(OpenWorldSceneName(scope)))
                    .RightSemijoin(qb.From.CombatEffectEvent(), (world, effect) => world.Identity.Eq(effect.Target))
                    .ToSql(),
                NetworkManager.GameplayScopeKind.Instance => qb
                    .From
                    .PlayerWorld()
                    .Where(c => c.WorldKind.Eq("INSTANCE"))
                    .Where(c => c.InstanceScopeId.Eq(scope.InstanceId.GetValueOrDefault()))
                    .RightSemijoin(qb.From.CombatEffectEvent(), (world, effect) => world.Identity.Eq(effect.Target))
                    .ToSql(),
                _ => throw new InvalidOperationException("Scoped player-target combat-effect query requested for GameplayScope.None"),
            };
        }

        private static string BuildScopedNpcSourceCombatEffectEventQuery(QueryBuilder qb, NetworkManager.GameplayScope scope)
        {
            return scope.Kind switch
            {
                NetworkManager.GameplayScopeKind.OpenWorld => qb
                    .From
                    .NpcInstance()
                    .Where(c => c.WorldKind.Eq("OPEN"))
                    .Where(c => c.OpenWorldSceneName.Eq(OpenWorldSceneName(scope)))
                    .RightSemijoin(qb.From.CombatEffectEvent(), (npc, effect) => npc.Identity.Eq(effect.Source))
                    .ToSql(),
                NetworkManager.GameplayScopeKind.Instance => qb
                    .From
                    .NpcInstance()
                    .Where(c => c.WorldKind.Eq("INSTANCE"))
                    .Where(c => c.InstanceScopeId.Eq(scope.InstanceId.GetValueOrDefault()))
                    .RightSemijoin(qb.From.CombatEffectEvent(), (npc, effect) => npc.Identity.Eq(effect.Source))
                    .ToSql(),
                _ => throw new InvalidOperationException("Scoped NPC-source combat-effect query requested for GameplayScope.None"),
            };
        }

        private static string BuildScopedNpcTargetCombatEffectEventQuery(QueryBuilder qb, NetworkManager.GameplayScope scope)
        {
            return scope.Kind switch
            {
                NetworkManager.GameplayScopeKind.OpenWorld => qb
                    .From
                    .NpcInstance()
                    .Where(c => c.WorldKind.Eq("OPEN"))
                    .Where(c => c.OpenWorldSceneName.Eq(OpenWorldSceneName(scope)))
                    .RightSemijoin(qb.From.CombatEffectEvent(), (npc, effect) => npc.Identity.Eq(effect.Target))
                    .ToSql(),
                NetworkManager.GameplayScopeKind.Instance => qb
                    .From
                    .NpcInstance()
                    .Where(c => c.WorldKind.Eq("INSTANCE"))
                    .Where(c => c.InstanceScopeId.Eq(scope.InstanceId.GetValueOrDefault()))
                    .RightSemijoin(qb.From.CombatEffectEvent(), (npc, effect) => npc.Identity.Eq(effect.Target))
                    .ToSql(),
                _ => throw new InvalidOperationException("Scoped NPC-target combat-effect query requested for GameplayScope.None"),
            };
        }

        private static string BuildScopedNpcCombatEventQuery(QueryBuilder qb, NetworkManager.GameplayScope scope)
        {
            return scope.Kind switch
            {
                NetworkManager.GameplayScopeKind.OpenWorld => qb
                    .From
                    .NpcInstance()
                    .Where(c => c.WorldKind.Eq("OPEN"))
                    .Where(c => c.OpenWorldSceneName.Eq(OpenWorldSceneName(scope)))
                    .RightSemijoin(qb.From.CombatEvent(), (npc, combatEvent) => npc.Identity.Eq(combatEvent.Caster))
                    .ToSql(),
                NetworkManager.GameplayScopeKind.Instance => qb
                    .From
                    .NpcInstance()
                    .Where(c => c.WorldKind.Eq("INSTANCE"))
                    .Where(c => c.InstanceScopeId.Eq(scope.InstanceId.GetValueOrDefault()))
                    .RightSemijoin(qb.From.CombatEvent(), (npc, combatEvent) => npc.Identity.Eq(combatEvent.Caster))
                    .ToSql(),
                _ => throw new InvalidOperationException("Scoped NPC combat-event query requested for GameplayScope.None"),
            };
        }

        private static string BuildScopedPlayerTargetCombatEventQuery(
            QueryBuilder qb,
            NetworkManager.GameplayScope scope)
        {
            return scope.Kind switch
            {
                NetworkManager.GameplayScopeKind.OpenWorld => qb
                    .From
                    .PlayerWorld()
                    .Where(c => c.WorldKind.Eq("OPEN"))
                    .Where(c => c.OpenWorldSceneName.Eq(OpenWorldSceneName(scope)))
                    .RightSemijoin(
                        qb.From.CombatEvent(),
                        (world, combatEvent) => world.Identity.Eq(combatEvent.Hit))
                    .ToSql(),
                NetworkManager.GameplayScopeKind.Instance => qb
                    .From
                    .PlayerWorld()
                    .Where(c => c.WorldKind.Eq("INSTANCE"))
                    .Where(c => c.InstanceScopeId.Eq(scope.InstanceId.GetValueOrDefault()))
                    .RightSemijoin(
                        qb.From.CombatEvent(),
                        (world, combatEvent) => world.Identity.Eq(combatEvent.Hit))
                    .ToSql(),
                _ => throw new InvalidOperationException(
                    "Scoped player-target combat-event query requested for GameplayScope.None"),
            };
        }

        private static string BuildScopedProjectilePresentationEventQuery(QueryBuilder qb, NetworkManager.GameplayScope scope)
        {
            return scope.Kind switch
            {
                NetworkManager.GameplayScopeKind.OpenWorld => qb
                    .From
                    .PlayerWorld()
                    .Where(c => c.WorldKind.Eq("OPEN"))
                    .Where(c => c.OpenWorldSceneName.Eq(OpenWorldSceneName(scope)))
                    .RightSemijoin(qb.From.ProjectilePresentationEvent(), (world, projectileEvent) => world.Identity.Eq(projectileEvent.Caster))
                    .ToSql(),
                NetworkManager.GameplayScopeKind.Instance => qb
                    .From
                    .PlayerWorld()
                    .Where(c => c.WorldKind.Eq("INSTANCE"))
                    .Where(c => c.InstanceScopeId.Eq(scope.InstanceId.GetValueOrDefault()))
                    .RightSemijoin(qb.From.ProjectilePresentationEvent(), (world, projectileEvent) => world.Identity.Eq(projectileEvent.Caster))
                    .ToSql(),
                _ => throw new InvalidOperationException("Scoped projectile-presentation-event query requested for GameplayScope.None"),
            };
        }

        private static string BuildScopedNpcProjectilePresentationEventQuery(QueryBuilder qb, NetworkManager.GameplayScope scope)
        {
            return scope.Kind switch
            {
                NetworkManager.GameplayScopeKind.OpenWorld => qb
                    .From
                    .NpcInstance()
                    .Where(c => c.WorldKind.Eq("OPEN"))
                    .Where(c => c.OpenWorldSceneName.Eq(OpenWorldSceneName(scope)))
                    .RightSemijoin(qb.From.ProjectilePresentationEvent(), (npc, projectileEvent) => npc.Identity.Eq(projectileEvent.Caster))
                    .ToSql(),
                NetworkManager.GameplayScopeKind.Instance => qb
                    .From
                    .NpcInstance()
                    .Where(c => c.WorldKind.Eq("INSTANCE"))
                    .Where(c => c.InstanceScopeId.Eq(scope.InstanceId.GetValueOrDefault()))
                    .RightSemijoin(qb.From.ProjectilePresentationEvent(), (npc, projectileEvent) => npc.Identity.Eq(projectileEvent.Caster))
                    .ToSql(),
                _ => throw new InvalidOperationException("Scoped NPC projectile-presentation-event query requested for GameplayScope.None"),
            };
        }

        private static string BuildScopedPlayerEventQuery(QueryBuilder qb, NetworkManager.GameplayScope scope)
        {
            return scope.Kind switch
            {
                NetworkManager.GameplayScopeKind.OpenWorld => qb
                    .From
                    .PlayerWorld()
                    .Where(c => c.WorldKind.Eq("OPEN"))
                    .Where(c => c.OpenWorldSceneName.Eq(OpenWorldSceneName(scope)))
                    .RightSemijoin(qb.From.PlayerEvent(), (world, playerEvent) => world.Identity.Eq(playerEvent.PlayerId))
                    .ToSql(),
                NetworkManager.GameplayScopeKind.Instance => qb
                    .From
                    .PlayerWorld()
                    .Where(c => c.WorldKind.Eq("INSTANCE"))
                    .Where(c => c.InstanceScopeId.Eq(scope.InstanceId.GetValueOrDefault()))
                    .RightSemijoin(qb.From.PlayerEvent(), (world, playerEvent) => world.Identity.Eq(playerEvent.PlayerId))
                    .ToSql(),
                _ => throw new InvalidOperationException("Scoped player-event query requested for GameplayScope.None"),
            };
        }

        private static string OpenWorldSceneName(NetworkManager.GameplayScope scope)
            => scope.OpenWorldSceneName ?? Arena.World.OpenWorldTravelCatalog.DefaultSceneName;

        private static IQuery<MatchParticipantStats> BuildScopedMatchParticipantStatsQuery(QueryBuilder qb, NetworkManager.GameplayScope scope)
        {
            if (scope.Kind != NetworkManager.GameplayScopeKind.Instance || !scope.InstanceId.HasValue)
                throw new InvalidOperationException("Instance-scoped match stats requested outside an instance scope.");

            return qb
                .From
                .MatchParticipantStats()
                .Where(c => c.InstanceId.Eq(scope.InstanceId.Value));
        }
    }
}
