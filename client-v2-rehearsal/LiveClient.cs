#nullable enable

using System;
using System.Linq;
using System.Threading;
using Arena.Network;
using Arena.UI;
using SpacetimeDB;
using Db = Arena.RehearsalHubV2Db;

internal static class CombatBuildV2LiveClient
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    internal static void Run(string serverUri, string databaseName)
    {
        if (!Uri.TryCreate(serverUri, UriKind.Absolute, out Uri? uri) || !uri.IsLoopback)
            throw new InvalidOperationException("live rehearsal requires a loopback server URI");
        if (!databaseName.StartsWith("arena-cbv2-p6-", StringComparison.Ordinal))
            throw new InvalidOperationException("live rehearsal requires an arena-cbv2-p6 database");

        Db.DbConnection? connection = null;
        Db.SubscriptionHandle? subscription = null;
        Exception? callbackError = null;
        bool subscriptionApplied = false;
        bool saveCommitted = false;
        ulong initialRevision = 0;

        connection = Db.DbConnection.Builder()
            .WithUri(serverUri)
            .WithDatabaseName(databaseName)
            .WithToken(null)
            .OnConnect((connected, _, _) =>
            {
                connected.Reducers.OnSaveCombatBuildV2 += (context, _) =>
                {
                    if (context.Event.Status is Status.Committed)
                        saveCommitted = true;
                    else if (context.Event.Status is Status.Failed(var reason))
                        callbackError = new InvalidOperationException(reason);
                    else
                        callbackError = new InvalidOperationException(
                            $"save did not commit: {context.Event.Status}");
                };
                subscription = connected.SubscriptionBuilder()
                    .OnApplied(_ => subscriptionApplied = true)
                    .OnError((_, error) => callbackError = error)
                    .AddQuery(query => query.From.MyCombatBuildV2())
                    .AddQuery(query => query.From.CombatBuildV2ContractDefinition())
                    .AddQuery(query => query.From.CombatSpecializationDefinitionV2())
                    .AddQuery(query => query.From.CombatFeatureDefinitionV2())
                    .AddQuery(query => query.From.CombatTraitDefinitionV2())
                    .Subscribe();
            })
            .OnConnectError(error => callbackError = error)
            .OnDisconnect((_, error) =>
            {
                if (error != null)
                    callbackError = error;
            })
            .Build();

        try
        {
            PumpUntil(connection, () => subscriptionApplied, () => callbackError);
            Require(subscription != null, "subscription handle was not retained");

            Db.MyCombatBuildV2 initial = connection.Db.MyCombatBuildV2.Iter().Single();
            initialRevision = initial.Revision;
            Require(connection.Db.CombatBuildV2ContractDefinition.Iter().Count() == 1,
                "contract subscription is incomplete");
            Require(connection.Db.CombatSpecializationDefinitionV2.Iter().Count() == 18,
                "specialization subscription is incomplete");
            Require(connection.Db.CombatFeatureDefinitionV2.Iter().Count() == 208,
                "feature subscription is incomplete");
            Require(connection.Db.CombatTraitDefinitionV2.Iter().Count() == 1,
                "Trait subscription is incomplete");

            connection.Reducers.SaveCombatBuildV2(MixedDraft(initialRevision));
            PumpUntil(
                connection,
                () => saveCommitted
                    && connection.Db.MyCombatBuildV2.Iter().Single().Revision > initialRevision,
                () => callbackError);

            Db.MyCombatBuildV2 reloaded = connection.Db.MyCombatBuildV2.Iter().Single();
            Require(reloaded.SelectedSpecializations.Select(row => row.SpecializationId)
                    .SequenceEqual(new[] { "DAGGERS_BLADEDANCER", "RUIN" }),
                "saved Form/School selections did not reload");
            Require(reloaded.SelectedFeatures.Select(row => row.AbilityId)
                    .OrderBy(id => id, StringComparer.Ordinal)
                    .SequenceEqual(new[]
                    {
                        "DAGGER_QUICK_CUT",
                        "RUIN_FLAMING_WEAPON",
                        "SPELL_FIREBALL",
                    }),
                "saved feature selections did not reload");

            CombatBuildV2DraftModel draft = MapDraft(reloaded);
            CombatBuildV2CatalogModel catalog = MapCatalog(connection);
            CombatBuildV2ContractModel contract = MapContract(connection);
            CombatBuildV2HudModel daggers = CombatBuildV2HudModel.Create(
                draft, catalog, contract, "DAGGERS");
            CombatBuildV2HudModel staff = CombatBuildV2HudModel.Create(
                draft, catalog, contract, "STAFF");
            Require(daggers.TechniqueBarVisible && daggers.TechniqueSlots.Count == 1,
                "Dagger Technique bar did not project from reloaded state");
            Require(staff.SpellBarVisible && staff.SpellSlots.Count == 1,
                "global Spell bar did not project under Staff");
            Require(!staff.TechniqueBarVisible && staff.TechniqueSlots.Count == 0,
                "Staff exposed a Technique bar");
            Require(daggers.SpellSlots[0].InputActionId == staff.SpellSlots[0].InputActionId,
                "Spell input changed across the live weapon switch projection");
            Require(daggers.ActivePerkAbilityIds.SequenceEqual(new[] { "RUIN_FLAMING_WEAPON" })
                    && staff.ActivePerkAbilityIds.SequenceEqual(new[] { "RUIN_FLAMING_WEAPON" }),
                "selected Perk was not active across live weapon projections");

            Console.WriteLine(
                $"PHASE6_LIVE_CLIENT_PASS revision={reloaded.Revision} "
                + $"specializations={reloaded.SelectedSpecializations.Count} "
                + $"features={reloaded.SelectedFeatures.Count}");
        }
        finally
        {
            connection.Disconnect();
        }
    }

    private static void PumpUntil(
        Db.DbConnection connection,
        Func<bool> complete,
        Func<Exception?> error)
    {
        DateTime deadline = DateTime.UtcNow + Timeout;
        while (!complete())
        {
            connection.FrameTick();
            Exception? failure = error();
            if (failure != null)
                throw new InvalidOperationException("live client callback failed", failure);
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("timed out waiting for live client state");
            Thread.Sleep(10);
        }
        connection.FrameTick();
    }

    private static Db.CombatBuildV2DraftInput MixedDraft(ulong revision)
        => new(
            2,
            revision,
            "DAGGERS",
            new()
            {
                new(0, "DAGGERS_BLADEDANCER"),
                new(1, "RUIN"),
            },
            new(),
            new()
            {
                new("DAGGERS", "TRAINING_DAGGER_PAIR", "", "", ""),
                new("STAFF", "NEWBIE_STAFF_01", "", "", ""),
            },
            new()
            {
                new("DAGGERS_BLADEDANCER", "DAGGER_QUICK_CUT", 0),
                new("RUIN", "SPELL_FIREBALL", 0),
                new("RUIN", "RUIN_FLAMING_WEAPON", null),
            },
            new() { "MASTERY" });

    private static CombatBuildV2DraftModel MapDraft(Db.MyCombatBuildV2 row)
        => new(
            row.SchemaVersion,
            row.Revision,
            row.StartingDisciplineId,
            row.SelectedSpecializations.Select(selected =>
                new CombatBuildV2SelectedSpecializationModel(
                    selected.SlotIndex,
                    selected.SpecializationId)),
            row.DormantSpecializations,
            row.DisciplineConfigurations.Select(configuration =>
                new CombatBuildV2DisciplineConfigurationModel(
                    configuration.CombatDisciplineId,
                    configuration.MainHandItemDefId,
                    configuration.MainHandColorId,
                    configuration.OffHandItemDefId,
                    configuration.OffHandColorId)),
            row.SelectedFeatures.Select(feature =>
                new CombatBuildV2FeatureSelectionModel(
                    feature.SpecializationId,
                    feature.AbilityId,
                    feature.PreferredBarOrder)),
            row.SelectedTraits);

    private static CombatBuildV2CatalogModel MapCatalog(Db.DbConnection connection)
        => new(
            connection.Db.CombatSpecializationDefinitionV2.Iter().Select(row =>
                new CombatSpecializationDefinitionV2Model(
                    row.SpecializationId,
                    row.CombatDisciplineId,
                    row.SpecializationKind == "SCHOOL"
                        ? CombatSpecializationKindV2.School
                        : CombatSpecializationKindV2.Form,
                    row.DisplayName,
                    row.SortOrder)),
            connection.Db.CombatFeatureDefinitionV2.Iter().Select(row =>
                new CombatFeatureDefinitionV2Model(
                    row.AbilityId,
                    row.SpecializationId,
                    row.CombatDisciplineId,
                    row.LoadoutKind switch
                    {
                        "TECHNIQUE" => CombatFeatureLoadoutKindV2.Technique,
                        "SPELL" => CombatFeatureLoadoutKindV2.Spell,
                        "PERK" => CombatFeatureLoadoutKindV2.Perk,
                        _ => throw new InvalidOperationException(
                            $"unknown loadout kind '{row.LoadoutKind}'"),
                    },
                    row.DisplayName,
                    row.ResourceKind,
                    row.ResourceCost,
                    row.SortOrder)),
            connection.Db.CombatTraitDefinitionV2.Iter().Select(row =>
                new CombatTraitDefinitionV2Model(
                    row.AbilityId,
                    row.DisplayName,
                    row.ModifierScalar,
                    row.SortOrder)));

    private static CombatBuildV2ContractModel MapContract(Db.DbConnection connection)
    {
        Db.CombatBuildV2ContractDefinition row = connection.Db
            .CombatBuildV2ContractDefinition.Iter().Single();
        return new CombatBuildV2ContractModel(
            row.SchemaVersion,
            checked((int)row.MinimumSelectedSpecializations),
            checked((int)row.MaximumSelectedSpecializations),
            checked((int)row.GlobalFeatureCapacity),
            checked((int)row.TraitCapacity),
            row.DirectActionInputIds);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
