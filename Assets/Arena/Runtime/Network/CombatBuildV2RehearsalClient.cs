#nullable enable

using System;
using System.Linq;
using SpacetimeDB;
using UnityEngine;
using RehearsalBuildRow = Arena.RehearsalHubV2Db.MyCombatBuildV2;
using RehearsalConnection = Arena.RehearsalHubV2Db.DbConnection;
using RehearsalContractRow = Arena.RehearsalHubV2Db.CombatBuildV2ContractDefinition;
using RehearsalErrorContext = Arena.RehearsalHubV2Db.ErrorContext;
using RehearsalReducerContext = Arena.RehearsalHubV2Db.ReducerEventContext;
using RehearsalSubscriptionContext = Arena.RehearsalHubV2Db.SubscriptionEventContext;
using RehearsalSubscriptionHandle = Arena.RehearsalHubV2Db.SubscriptionHandle;

namespace Arena.Network
{
    internal readonly struct CombatBuildV2RehearsalEndpoint
    {
        private const string AllowedDatabasePrefix = "arena-cbv2-p6-";

        private CombatBuildV2RehearsalEndpoint(string serverUri, string databaseName)
        {
            ServerUri = serverUri;
            DatabaseName = databaseName;
        }

        internal string ServerUri { get; }
        internal string DatabaseName { get; }

        internal static bool TryCreate(
            string serverUri,
            string databaseName,
            out CombatBuildV2RehearsalEndpoint endpoint,
            out string error)
        {
            endpoint = default;
            if (!Application.isEditor && !Debug.isDebugBuild)
            {
                error = "Combat Build v2 rehearsal is available only in editor/development builds.";
                return false;
            }
            if (!Uri.TryCreate(serverUri, UriKind.Absolute, out Uri? uri) || !uri.IsLoopback)
            {
                error = "Combat Build v2 rehearsal requires an explicit loopback server URI.";
                return false;
            }
            if (string.IsNullOrWhiteSpace(databaseName)
                || !databaseName.StartsWith(AllowedDatabasePrefix, StringComparison.Ordinal))
            {
                error = $"Combat Build v2 rehearsal database must begin with '{AllowedDatabasePrefix}'.";
                return false;
            }

            endpoint = new CombatBuildV2RehearsalEndpoint(serverUri, databaseName);
            error = string.Empty;
            return true;
        }
    }

    /// <summary>
    /// Explicit developer-only bridge to the disposable Hub v2 rehearsal.
    /// There is no runtime bootstrap and the canonical Hub manager never calls
    /// this type. Bindings are generated from hub-v2-rehearsal, not authored.
    /// </summary>
    internal sealed class CombatBuildV2RehearsalClient : IDisposable
    {
        internal event Action? Changed;
        internal event Action<CombatBuildV2SaveResult>? SaveCompleted;

        private RehearsalConnection? _connection;
        private RehearsalSubscriptionHandle? _subscription;
        private Identity _identity;
        private bool _hasIdentity;

        internal bool IsReady { get; private set; }
        internal string LastTransportError { get; private set; } = string.Empty;
        internal CombatBuildV2DraftModel? Draft { get; private set; }
        internal CombatBuildV2ContractModel? Contract { get; private set; }
        internal CombatBuildV2CatalogModel? Catalog { get; private set; }

        internal void Connect(CombatBuildV2RehearsalEndpoint endpoint)
        {
            DisposeConnection();
            LastTransportError = string.Empty;
            _connection = RehearsalConnection.Builder()
                .WithUri(endpoint.ServerUri)
                .WithDatabaseName(endpoint.DatabaseName)
                .WithToken(null)
                .OnConnect(OnConnected)
                .OnConnectError(error => SetTransportError(error.Message))
                .OnDisconnect((_, error) => SetTransportError(
                    error?.Message ?? "Combat Build v2 rehearsal disconnected."))
                .Build();
        }

        internal void FrameTick() => _connection?.FrameTick();

        internal bool Save(CombatBuildV2DraftModel draft)
        {
            if (!IsReady || _connection == null)
                return false;
            _connection.Reducers.SaveCombatBuildV2(ToGenerated(draft));
            return true;
        }

        public void Dispose() => DisposeConnection();

        private void OnConnected(
            RehearsalConnection connection,
            Identity identity,
            string _)
        {
            if (!ReferenceEquals(connection, _connection))
                return;
            _identity = identity;
            _hasIdentity = true;
            connection.Reducers.OnSaveCombatBuildV2 += OnSaveResult;
            BindRefreshCallbacks(connection);
            _subscription = connection
                .SubscriptionBuilder()
                .OnApplied(OnSubscriptionApplied)
                .OnError(OnSubscriptionError)
                .Subscribe(new[]
                {
                    new Arena.RehearsalHubV2Db.QueryBuilder().From.MyCombatBuildV2().ToSql(),
                    new Arena.RehearsalHubV2Db.QueryBuilder().From
                        .CombatBuildV2ContractDefinition().ToSql(),
                    new Arena.RehearsalHubV2Db.QueryBuilder().From
                        .CombatSpecializationDefinitionV2().ToSql(),
                    new Arena.RehearsalHubV2Db.QueryBuilder().From
                        .CombatFeatureDefinitionV2().ToSql(),
                    new Arena.RehearsalHubV2Db.QueryBuilder().From
                        .CombatTraitDefinitionV2().ToSql(),
                });
        }

        private void OnSubscriptionApplied(RehearsalSubscriptionContext _)
        {
            RefreshFromCache();
            IsReady = Draft != null && Contract != null && Catalog != null;
            Changed?.Invoke();
        }

        private void OnSubscriptionError(RehearsalErrorContext _, Exception error)
            => SetTransportError(error.Message);

        private void OnSaveResult(
            RehearsalReducerContext context,
            Arena.RehearsalHubV2Db.CombatBuildV2DraftInput _)
        {
            if (!_hasIdentity || context.Event.CallerIdentity != _identity)
                return;
            CombatBuildV2SaveResult result = context.Event.Status switch
            {
                Status.Committed => CombatBuildV2SaveResult.Accepted(),
                Status.Failed(var failure) => CombatBuildV2SaveResult.Rejected(failure),
                Status.OutOfEnergy(var _) => CombatBuildV2SaveResult.Rejected("OUT_OF_ENERGY"),
                _ => CombatBuildV2SaveResult.Rejected("SAVE_NOT_COMMITTED"),
            };
            SaveCompleted?.Invoke(result);
        }

        private void BindRefreshCallbacks(RehearsalConnection connection)
        {
            connection.Db.MyCombatBuildV2.OnInsert += (_, __) => RefreshAndNotify();
            connection.Db.MyCombatBuildV2.OnUpdate += (_, __, ___) => RefreshAndNotify();
            connection.Db.MyCombatBuildV2.OnDelete += (_, __) => RefreshAndNotify();
            connection.Db.CombatBuildV2ContractDefinition.OnInsert += (_, __) => RefreshAndNotify();
            connection.Db.CombatBuildV2ContractDefinition.OnUpdate += (_, __, ___) => RefreshAndNotify();
            connection.Db.CombatBuildV2ContractDefinition.OnDelete += (_, __) => RefreshAndNotify();
            connection.Db.CombatSpecializationDefinitionV2.OnInsert += (_, __) => RefreshAndNotify();
            connection.Db.CombatSpecializationDefinitionV2.OnUpdate += (_, __, ___) => RefreshAndNotify();
            connection.Db.CombatSpecializationDefinitionV2.OnDelete += (_, __) => RefreshAndNotify();
            connection.Db.CombatFeatureDefinitionV2.OnInsert += (_, __) => RefreshAndNotify();
            connection.Db.CombatFeatureDefinitionV2.OnUpdate += (_, __, ___) => RefreshAndNotify();
            connection.Db.CombatFeatureDefinitionV2.OnDelete += (_, __) => RefreshAndNotify();
            connection.Db.CombatTraitDefinitionV2.OnInsert += (_, __) => RefreshAndNotify();
            connection.Db.CombatTraitDefinitionV2.OnUpdate += (_, __, ___) => RefreshAndNotify();
            connection.Db.CombatTraitDefinitionV2.OnDelete += (_, __) => RefreshAndNotify();
        }

        private void RefreshAndNotify()
        {
            RefreshFromCache();
            Changed?.Invoke();
        }

        private void RefreshFromCache()
        {
            RehearsalConnection? connection = _connection;
            if (connection == null)
                return;

            RehearsalBuildRow? build = connection.Db.MyCombatBuildV2.Iter().FirstOrDefault();
            Draft = build == null ? null : FromGenerated(build);
            RehearsalContractRow? contract = connection.Db
                .CombatBuildV2ContractDefinition.Iter().FirstOrDefault();
            Contract = contract == null
                ? null
                : new CombatBuildV2ContractModel(
                    contract.SchemaVersion,
                    checked((int)contract.MinimumSelectedSpecializations),
                    checked((int)contract.MaximumSelectedSpecializations),
                    checked((int)contract.GlobalFeatureCapacity),
                    checked((int)contract.TraitCapacity),
                    contract.DirectActionInputIds);
            Catalog = new CombatBuildV2CatalogModel(
                connection.Db.CombatSpecializationDefinitionV2.Iter().Select(row =>
                    new CombatSpecializationDefinitionV2Model(
                        row.SpecializationId,
                        row.CombatDisciplineId,
                        string.Equals(row.SpecializationKind, "SCHOOL", StringComparison.Ordinal)
                            ? CombatSpecializationKindV2.School
                            : CombatSpecializationKindV2.Form,
                        row.DisplayName,
                        row.SortOrder)),
                connection.Db.CombatFeatureDefinitionV2.Iter().Select(row =>
                    new CombatFeatureDefinitionV2Model(
                        row.AbilityId,
                        row.SpecializationId,
                        row.CombatDisciplineId,
                        ParseLoadoutKind(row.LoadoutKind),
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
        }

        private static CombatFeatureLoadoutKindV2 ParseLoadoutKind(string value)
            => value switch
            {
                "TECHNIQUE" => CombatFeatureLoadoutKindV2.Technique,
                "SPELL" => CombatFeatureLoadoutKindV2.Spell,
                "PERK" => CombatFeatureLoadoutKindV2.Perk,
                _ => throw new InvalidOperationException($"Unknown v2 loadout kind '{value}'."),
            };

        private static CombatBuildV2DraftModel FromGenerated(RehearsalBuildRow row)
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

        private static Arena.RehearsalHubV2Db.CombatBuildV2DraftInput ToGenerated(
            CombatBuildV2DraftModel draft)
            => new(
                draft.SchemaVersion,
                draft.Revision,
                draft.StartingDisciplineId,
                draft.SelectedSpecializations.Select(row =>
                    new Arena.RehearsalHubV2Db.SelectedSpecializationV2Input(
                        row.SlotIndex,
                        row.SpecializationId)).ToList(),
                draft.DormantSpecializations.ToList(),
                draft.DisciplineConfigurations.Select(row =>
                    new Arena.RehearsalHubV2Db.DisciplineConfigurationV2Input(
                        row.CombatDisciplineId,
                        row.MainHandItemDefId,
                        row.MainHandColorId,
                        row.OffHandItemDefId,
                        row.OffHandColorId)).ToList(),
                draft.SelectedFeatures.Select(row =>
                    new Arena.RehearsalHubV2Db.CombatFeatureSelectionV2Input(
                        row.SpecializationId,
                        row.AbilityId,
                        row.PreferredBarOrder)).ToList(),
                draft.SelectedTraits.ToList());

        private void SetTransportError(string message)
        {
            IsReady = false;
            LastTransportError = message;
            Changed?.Invoke();
        }

        private void DisposeConnection()
        {
            if (_connection != null)
            {
                _connection.Reducers.OnSaveCombatBuildV2 -= OnSaveResult;
                _connection.Disconnect();
            }
            _connection = null;
            _subscription = null;
            _hasIdentity = false;
            IsReady = false;
            Draft = null;
            Contract = null;
            Catalog = null;
        }
    }
}
