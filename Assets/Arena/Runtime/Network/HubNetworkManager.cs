#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using SpacetimeDB;
using UnityEngine;
using UnityEngine.SceneManagement;
using HubConnection = Arena.HubDb.DbConnection;
using HubEventContext = Arena.HubDb.EventContext;
using HubErrorContext = Arena.HubDb.ErrorContext;
using HubReducerEventContext = Arena.HubDb.ReducerEventContext;
using HubSubscriptionEventContext = Arena.HubDb.SubscriptionEventContext;
using HubSubscriptionHandle = Arena.HubDb.SubscriptionHandle;
using HubPlayerRow = Arena.HubDb.MyHubPlayer;
using HubMatchStatusRow = Arena.HubDb.MyMatchStatus;
using HubArmorSelectionRow = Arena.HubDb.MyHubArmorSelection;
using HubCombatBuildRow = Arena.HubDb.MyCombatBuild;
using HubCombatBuildContractRow = Arena.HubDb.HubCombatBuildContractDefinition;
using HubDisciplineRow = Arena.HubDb.HubCombatBuildDisciplineDefinition;
using HubSchoolRow = Arena.HubDb.HubSpellSchoolDefinition;
using HubAbilityRow = Arena.HubDb.HubCombatBuildAbilityDefinition;
using HubArmorSetRow = Arena.HubDb.HubArmorSetDefinition;
using HubWeaponRow = Arena.HubDb.HubWeaponDefinition;
using HubWeaponColorRow = Arena.HubDb.HubWeaponColorDefinition;

namespace Arena.Network
{
    internal enum HubConnectionState
    {
        Disconnected,
        Connecting,
        Subscribing,
        Ready,
        Error,
    }

    /// <summary>
    /// Generated Hub rows stop at this boundary. Hub screens consume these
    /// tiny snapshots and never acquire the Hub DbConnection or schema types.
    /// </summary>
    internal readonly struct HubPlayerSnapshot
    {
        internal HubPlayerSnapshot(Identity identity, string displayName)
        {
            Identity = identity;
            DisplayName = displayName;
        }

        internal Identity Identity { get; }
        internal string DisplayName { get; }
    }

    internal readonly struct HubMatchStatusSnapshot
    {
        internal HubMatchStatusSnapshot(
            string ticketId,
            string queueKind,
            string format,
            string status,
            string? failureCode,
            string? matchId,
            string? serverUri,
            string? databaseIdentity,
            string? matchBuildId,
            string? mapId,
            long createdAtMicros,
            long updatedAtMicros,
            long ticketExpiresAtMicros,
            long? readyAtMicros,
            long? assignmentExpiresAtMicros)
        {
            TicketId = ticketId;
            QueueKind = queueKind;
            Format = format;
            Status = status;
            FailureCode = failureCode;
            MatchId = matchId;
            ServerUri = serverUri;
            DatabaseIdentity = databaseIdentity;
            MatchBuildId = matchBuildId;
            MapId = mapId;
            CreatedAtMicros = createdAtMicros;
            UpdatedAtMicros = updatedAtMicros;
            TicketExpiresAtMicros = ticketExpiresAtMicros;
            ReadyAtMicros = readyAtMicros;
            AssignmentExpiresAtMicros = assignmentExpiresAtMicros;
        }

        internal string TicketId { get; }

        /// UNRANKED for a PvP match, OPEN_WORLD for a disposable world. The
        /// ticket's <see cref="Format"/> then names the destination scene.
        internal string QueueKind { get; }
        internal string Format { get; }
        internal string Status { get; }
        internal string? FailureCode { get; }
        internal string? MatchId { get; }
        internal string? ServerUri { get; }
        internal string? DatabaseIdentity { get; }
        internal string? MatchBuildId { get; }
        internal string? MapId { get; }
        internal long CreatedAtMicros { get; }
        internal long UpdatedAtMicros { get; }
        internal long TicketExpiresAtMicros { get; }
        internal long? ReadyAtMicros { get; }
        internal long? AssignmentExpiresAtMicros { get; }

        internal bool IsOpenWorld
            => string.Equals(QueueKind, "OPEN_WORLD", StringComparison.Ordinal);

        internal bool IsActive
            => string.Equals(Status, "PENDING", StringComparison.Ordinal)
               || string.Equals(Status, "CLAIMED", StringComparison.Ordinal)
               || string.Equals(Status, "PROVISIONING", StringComparison.Ordinal)
               || string.Equals(Status, "READY", StringComparison.Ordinal);
    }

    internal sealed class HubDisciplineSnapshot
    {
        internal HubDisciplineSnapshot(
            string id,
            string name,
            uint sortOrder,
            HubCombatBuildWeapon starterWeapon)
        {
            Id = id;
            Name = name;
            SortOrder = sortOrder;
            StarterWeapon = starterWeapon;
        }

        internal string Id { get; }
        internal string Name { get; }
        internal string CombatDisciplineId => Id;
        internal uint SortOrder { get; }
        internal HubCombatBuildWeapon StarterWeapon { get; }
    }

    internal sealed class HubSpellSchoolSnapshot
    {
        internal HubSpellSchoolSnapshot(string id, string name, uint sortOrder)
        {
            Id = id;
            Name = name;
            SortOrder = sortOrder;
        }

        internal string Id { get; }
        internal string Name { get; }
        internal uint SortOrder { get; }
    }

    internal sealed class HubCombatBuildContractSnapshot
    {
        internal HubCombatBuildContractSnapshot(HubCombatBuildContractRow row)
        {
            SchemaVersion = row.SchemaVersion;
            MinimumSelectedDisciplines = checked((int)row.MinimumSelectedDisciplines);
            MaximumSelectedDisciplines = checked((int)row.MaximumSelectedDisciplines);
            MinimumStaffSchoolsWhenSelected = checked((int)row.MinimumStaffSchoolsWhenSelected);
            MaximumStaffSchoolsWhenSelected = checked((int)row.MaximumStaffSchoolsWhenSelected);
            CombinedAbilityBudget = checked((int)row.CombinedAbilityBudget);
            MaximumActiveAbilities = checked((int)row.MaximumActiveAbilities);
            MinimumCountedAbilitiesPerSelectedDiscipline =
                checked((int)row.MinimumCountedAbilitiesPerSelectedDiscipline);
            ActionSlotIds = row.ActionSlotIds.ToArray();
        }

        internal uint SchemaVersion { get; }
        internal int MinimumSelectedDisciplines { get; }
        internal int MaximumSelectedDisciplines { get; }
        internal int MinimumStaffSchoolsWhenSelected { get; }
        internal int MaximumStaffSchoolsWhenSelected { get; }
        internal int CombinedAbilityBudget { get; }
        internal int MaximumActiveAbilities { get; }
        internal int MinimumCountedAbilitiesPerSelectedDiscipline { get; }
        internal IReadOnlyList<string> ActionSlotIds { get; }
    }

    internal sealed class HubAbilitySnapshot
    {
        internal HubAbilitySnapshot(
            string id,
            string combatDisciplineId,
            string? spellSchoolId,
            string selectionKind,
            string name,
            string resource,
            float cost,
            string description,
            uint sortOrder)
        {
            Id = id;
            CombatDisciplineId = combatDisciplineId;
            SpellSchoolId = spellSchoolId;
            SelectionKind = selectionKind;
            Name = name;
            Resource = resource;
            Cost = cost;
            Description = description;
            SortOrder = sortOrder;
        }

        internal string Id { get; }
        internal string CombatDisciplineId { get; }
        internal string? SpellSchoolId { get; }
        internal string SelectionKind { get; }
        internal string Name { get; }
        internal string Resource { get; }
        internal float Cost { get; }
        internal string Description { get; }
        internal uint SortOrder { get; }
    }

    internal sealed class HubArmorSetSnapshot
    {
        internal HubArmorSetSnapshot(
            string id,
            string name,
            string tier,
            float physicalResistance,
            float magicalResistance,
            float moveSpeedModifier,
            float castSpeedModifier,
            uint pieceCount,
            uint sortOrder)
        {
            Id = id;
            Name = name;
            Tier = tier;
            PhysicalResistance = physicalResistance;
            MagicalResistance = magicalResistance;
            MoveSpeedModifier = moveSpeedModifier;
            CastSpeedModifier = castSpeedModifier;
            PieceCount = pieceCount;
            SortOrder = sortOrder;
        }

        internal string Id { get; }
        internal string Name { get; }
        internal string Tier { get; }
        internal string ArmorSetId => Id;
        internal string DisplayName => Name;
        internal string ArmorTier => Tier;
        internal float PhysicalResistance { get; }
        internal float MagicalResistance { get; }
        internal float MoveSpeedModifier { get; }
        internal float CastSpeedModifier { get; }
        internal uint PieceCount { get; }
        internal uint SortOrder { get; }
    }

    internal sealed class HubWeaponSnapshot
    {
        internal HubWeaponSnapshot(
            string itemDefId,
            string displayName,
            string iconId,
            string weaponKind,
            string handRequirement,
            string equipSlot,
            string combatDisciplineId,
            uint sortOrder)
        {
            ItemDefId = itemDefId;
            DisplayName = displayName;
            IconId = iconId;
            WeaponKind = weaponKind;
            HandRequirement = handRequirement;
            EquipSlot = equipSlot;
            CombatDisciplineId = combatDisciplineId;
            SortOrder = sortOrder;
        }

        internal string ItemDefId { get; }
        internal string DisplayName { get; }
        internal string IconId { get; }
        internal string WeaponKind { get; }
        internal string HandRequirement { get; }
        internal string EquipSlot { get; }
        internal string CombatDisciplineId { get; }
        internal uint SortOrder { get; }
    }

    internal sealed class HubWeaponColorSnapshot
    {
        internal HubWeaponColorSnapshot(
            string itemDefId,
            string colorId,
            string displayName,
            string colorHex,
            uint sortOrder)
        {
            ItemDefId = itemDefId;
            ColorId = colorId;
            DisplayName = displayName;
            ColorHex = colorHex;
            SortOrder = sortOrder;
        }

        internal string ItemDefId { get; }
        internal string ColorId { get; }
        internal string DisplayName { get; }
        internal string ColorHex { get; }
        internal uint SortOrder { get; }
    }

    internal readonly struct HubArmorLoadoutSnapshot
    {
        internal HubArmorLoadoutSnapshot(string armorSetId, ulong revision)
        {
            ArmorSetId = armorSetId;
            Revision = revision;
        }

        internal string ArmorSetId { get; }
        internal ulong Revision { get; }
    }

    /// <summary>
    /// Persistent-control-plane connection. It subscribes only to the two
    /// caller-filtered Hub views and owns no gameplay cache or simulation work.
    /// </summary>
    [DefaultExecutionOrder(-70)]
    internal sealed class HubNetworkManager : MonoBehaviour
    {
        private const float RetryDelaySeconds = 2f;

        internal static HubNetworkManager? Instance { get; private set; }

        internal event Action? Changed;
        internal event Action? Ready;
        internal event Action<string>? UnexpectedDisconnect;
        internal event Action<bool, string>? CombatBuildSaveCompleted;
        internal event Action<bool, string>? ArmorSetSaveCompleted;

        private HubConnection? _conn;
        private HubSubscriptionHandle? _subscription;
        private NetworkEnvironmentEndpoint _activeEndpoint =
            NetworkEnvironmentConfig.HubEndpointFor(NetworkEnvironmentKind.Local);
        private Identity _identity;
        private bool _hasIdentity;
        private bool _maintainConnection;
        private bool _intentionalDisconnect;
        private float _nextRetryRealtime;
        private int _connectionGeneration;
        private string? _activeClientRequestId;
        private string? _pendingOpenWorldDestination;
        private bool _requestAwaitingConfirmation;
        private HubPlayerSnapshot? _player;
        private HubMatchStatusSnapshot? _matchStatus;
        private HubArmorLoadoutSnapshot? _armorLoadout;
        private HubCombatBuildDraft? _combatBuild;
        private HubCombatBuildContractSnapshot? _combatBuildContract;
        private IReadOnlyList<HubDisciplineSnapshot> _disciplines = Array.Empty<HubDisciplineSnapshot>();
        private IReadOnlyList<HubSpellSchoolSnapshot> _staffSchools = Array.Empty<HubSpellSchoolSnapshot>();
        private IReadOnlyList<HubAbilitySnapshot> _abilities = Array.Empty<HubAbilitySnapshot>();
        private IReadOnlyList<HubArmorSetSnapshot> _armorSets = Array.Empty<HubArmorSetSnapshot>();
        private IReadOnlyList<HubWeaponSnapshot> _weapons = Array.Empty<HubWeaponSnapshot>();
        private IReadOnlyList<HubWeaponColorSnapshot> _weaponColors = Array.Empty<HubWeaponColorSnapshot>();

        internal HubConnectionState State { get; private set; } = HubConnectionState.Disconnected;
        internal string LastError { get; private set; } = string.Empty;
        internal bool IsReady => State == HubConnectionState.Ready && _conn != null && _hasIdentity;
        internal Identity? Identity => _hasIdentity ? _identity : null;
        internal NetworkEnvironmentEndpoint ActiveEndpoint => _activeEndpoint;
        internal HubPlayerSnapshot? Player => _player;
        internal HubMatchStatusSnapshot? MatchStatus => _matchStatus;
        internal HubArmorLoadoutSnapshot? ArmorLoadout => _armorLoadout;
        internal HubCombatBuildDraft? CombatBuild => _combatBuild;
        internal HubCombatBuildContractSnapshot? CombatBuildContract => _combatBuildContract;
        internal IReadOnlyList<HubDisciplineSnapshot> Disciplines => _disciplines;
        internal IReadOnlyList<HubSpellSchoolSnapshot> StaffSchools => _staffSchools;
        internal IReadOnlyList<HubAbilitySnapshot> Abilities => _abilities;
        internal IReadOnlyList<HubArmorSetSnapshot> ArmorSets => _armorSets;
        internal IReadOnlyList<HubWeaponSnapshot> Weapons => _weapons;
        internal IReadOnlyList<HubWeaponColorSnapshot> WeaponColors => _weaponColors;
        internal bool HasActiveMatchRequest
            => _requestAwaitingConfirmation || (_matchStatus?.IsActive ?? false);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (!string.Equals(SceneManager.GetActiveScene().name, "Hub", StringComparison.Ordinal))
                return;

            EnsureInstance().ConnectToSelectedEnvironment();
        }

        internal static HubNetworkManager EnsureInstance()
        {
            if (Instance != null)
                return Instance;

            var go = new GameObject(nameof(HubNetworkManager));
            DontDestroyOnLoad(go);
            return go.AddComponent<HubNetworkManager>();
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        internal void ConnectToSelectedEnvironment()
        {
            _maintainConnection = true;
            _activeEndpoint = NetworkEnvironmentConfig.CurrentHubEndpoint;
            if (_conn != null || State == HubConnectionState.Connecting || State == HubConnectionState.Subscribing)
                return;

            ConnectNow();
        }

        internal void ReconnectToSelectedEnvironment()
        {
            _maintainConnection = true;
            _intentionalDisconnect = true;
            DisposeConnection();
            _activeEndpoint = NetworkEnvironmentConfig.CurrentHubEndpoint;
            State = HubConnectionState.Disconnected;
            ConnectNow();
        }

        internal bool RequestUnranked2V2BotMatch()
        {
            if (BeginTicketRequest() is not string clientRequestId)
                return false;

            _pendingOpenWorldDestination = null;
            _conn!.Reducers.RequestUnranked2V2BotMatch(clientRequestId);
            return true;
        }

        /// Requests a disposable open-world instance. Open worlds ride the same
        /// ticket pipeline as matches, so everything after this call — lease,
        /// assignment, handoff, disposal — is the path PvP already uses.
        internal bool RequestOpenWorldInstance(string destination)
        {
            if (string.IsNullOrWhiteSpace(destination))
                return false;
            if (BeginTicketRequest() is not string clientRequestId)
                return false;

            _pendingOpenWorldDestination = destination;
            _conn!.Reducers.RequestOpenWorldInstance(clientRequestId, destination);
            return true;
        }

        /// Returns the stable client request id to send, or null when the Hub
        /// cannot take a new ticket right now.
        private string? BeginTicketRequest()
        {
            if (!IsReady || _conn == null)
            {
                SetError("The Hub is still connecting. Try again in a moment.", disconnect: false);
                return null;
            }

            if (HasActiveMatchRequest)
                return null;

            string clientRequestId = Guid.NewGuid().ToString("N");
            _activeClientRequestId = clientRequestId;
            _requestAwaitingConfirmation = true;
            LastError = string.Empty;
            NotifyChanged();
            return clientRequestId;
        }

        internal bool SaveArmorSet(string armorSetId)
        {
            if (!IsReady || _conn == null)
                return false;

            _conn.Reducers.SaveHubArmorSet(armorSetId);
            return true;
        }

        internal bool SaveCombatBuild(HubCombatBuildDraft draft)
        {
            if (!IsReady || _conn == null)
                return false;

            _conn.Reducers.SaveCombatBuild(draft.ToReducerInput());
            return true;
        }

        internal void CancelCurrentTicket()
        {
            if (!IsReady || _conn == null || !_matchStatus.HasValue)
                return;

            string ticketId = _matchStatus.Value.TicketId;
            if (!string.IsNullOrWhiteSpace(ticketId))
                _conn.Reducers.CancelMatchTicket(ticketId);
        }

        internal void DisconnectForMatchHandoff()
        {
            _maintainConnection = false;
            _intentionalDisconnect = true;
            DisposeConnection();
            State = HubConnectionState.Disconnected;
            NotifyChanged();
        }

        private void ConnectNow()
        {
            DisposeConnection();
            _intentionalDisconnect = false;
            LastError = string.Empty;
            State = HubConnectionState.Connecting;
            NotifyChanged();

            int generation = ++_connectionGeneration;
            string? token = NetworkEnvironmentConfig.LoadAuthToken(_activeEndpoint);
            Debug.Log(
                $"[HubNetworkManager] Connecting to {_activeEndpoint.ServerUri} "
                + $"database={_activeEndpoint.ModuleName}.");
            _conn = HubConnection.Builder()
                .WithUri(_activeEndpoint.ServerUri)
                .WithDatabaseName(_activeEndpoint.ModuleName)
                .WithToken(token)
                .OnConnect((conn, identity, issuedToken) =>
                    OnConnected(generation, conn, identity, issuedToken))
                .OnConnectError(error => OnConnectError(generation, error))
                .OnDisconnect(OnDisconnected)
                .Build();
        }

        private void OnConnected(
            int generation,
            HubConnection conn,
            Identity identity,
            string token)
        {
            if (generation != _connectionGeneration || !ReferenceEquals(conn, _conn))
                return;

            _identity = identity;
            _hasIdentity = true;
            if (!string.IsNullOrWhiteSpace(token))
                NetworkEnvironmentConfig.SaveAuthToken(_activeEndpoint, token);

            BindRows(conn);
            conn.Reducers.OnRequestUnranked2V2BotMatch += OnRequestMatchResult;
            conn.Reducers.OnRequestOpenWorldInstance += OnRequestOpenWorldInstanceResult;
            conn.Reducers.OnSaveHubArmorSet += OnSaveArmorSetResult;
            conn.Reducers.OnSaveCombatBuild += OnSaveCombatBuildResult;
            State = HubConnectionState.Subscribing;
            NotifyChanged();
            _subscription = conn
                .SubscriptionBuilder()
                .OnApplied(OnSubscriptionApplied)
                .OnError(OnSubscriptionError)
                .Subscribe(new[]
                {
                    new Arena.HubDb.QueryBuilder().From.MyHubPlayer().ToSql(),
                    new Arena.HubDb.QueryBuilder().From.MyMatchStatus().ToSql(),
                    new Arena.HubDb.QueryBuilder().From.MyHubArmorSelection().ToSql(),
                    new Arena.HubDb.QueryBuilder().From.MyCombatBuild().ToSql(),
                    new Arena.HubDb.QueryBuilder().From.HubCombatBuildContractDefinition().ToSql(),
                    new Arena.HubDb.QueryBuilder().From.HubCombatBuildDisciplineDefinition().ToSql(),
                    new Arena.HubDb.QueryBuilder().From.HubSpellSchoolDefinition().ToSql(),
                    new Arena.HubDb.QueryBuilder().From.HubCombatBuildAbilityDefinition().ToSql(),
                    new Arena.HubDb.QueryBuilder().From.HubArmorSetDefinition().ToSql(),
                    new Arena.HubDb.QueryBuilder().From.HubWeaponDefinition().ToSql(),
                    new Arena.HubDb.QueryBuilder().From.HubWeaponColorDefinition().ToSql(),
                });
        }

        private void OnSubscriptionApplied(HubSubscriptionEventContext context)
        {
            if (_conn == null || !ReferenceEquals(context.Db, _conn.Db))
                return;

            RefreshSnapshotsFromCache(_conn);
            State = HubConnectionState.Ready;
            LastError = string.Empty;
            NotifyChanged();
            Ready?.Invoke();

            // A transport loss between send and reducer confirmation is safe
            // to retry because the request ID is deliberately stable.
            if (_requestAwaitingConfirmation
                && !string.IsNullOrWhiteSpace(_activeClientRequestId)
                && !_matchStatus.HasValue)
            {
                if (_pendingOpenWorldDestination != null)
                {
                    _conn.Reducers.RequestOpenWorldInstance(
                        _activeClientRequestId,
                        _pendingOpenWorldDestination);
                }
                else
                {
                    _conn.Reducers.RequestUnranked2V2BotMatch(_activeClientRequestId);
                }
            }
        }

        private void OnSubscriptionError(HubErrorContext context, Exception error)
        {
            if (_conn == null || !ReferenceEquals(context.Db, _conn.Db))
                return;

            SetError($"Hub subscription failed: {error.Message}", disconnect: true);
        }

        private void OnConnectError(int generation, Exception error)
        {
            if (generation != _connectionGeneration)
                return;

            SetError($"Unable to connect to the Hub: {error.Message}", disconnect: true);
        }

        private void OnDisconnected(HubConnection conn, Exception? error)
        {
            if (!ReferenceEquals(conn, _conn))
                return;

            bool expected = _intentionalDisconnect || !_maintainConnection;
            UnbindRows(conn);
            _conn = null;
            _subscription = null;
            _hasIdentity = false;
            _player = null;
            _matchStatus = null;
            ClearLoadoutSnapshots();
            State = expected ? HubConnectionState.Disconnected : HubConnectionState.Error;
            if (!expected)
            {
                LastError = $"Hub connection lost: {error?.Message ?? "transport closed"}";
                _nextRetryRealtime = Time.unscaledTime + RetryDelaySeconds;
            }
            NotifyChanged();
            if (!expected)
                UnexpectedDisconnect?.Invoke(LastError);
        }

        private void BindRows(HubConnection conn)
        {
            conn.Db.MyHubPlayer.OnInsert += OnHubPlayerInsert;
            conn.Db.MyHubPlayer.OnUpdate += OnHubPlayerUpdate;
            conn.Db.MyHubPlayer.OnDelete += OnHubPlayerDelete;
            conn.Db.MyMatchStatus.OnInsert += OnMatchStatusInsert;
            conn.Db.MyMatchStatus.OnUpdate += OnMatchStatusUpdate;
            conn.Db.MyMatchStatus.OnDelete += OnMatchStatusDelete;
            conn.Db.MyHubArmorSelection.OnInsert += OnArmorSelectionInsert;
            conn.Db.MyHubArmorSelection.OnUpdate += OnArmorSelectionUpdate;
            conn.Db.MyHubArmorSelection.OnDelete += OnArmorSelectionDelete;
            conn.Db.MyCombatBuild.OnInsert += OnCombatBuildInsert;
            conn.Db.MyCombatBuild.OnUpdate += OnCombatBuildUpdate;
            conn.Db.MyCombatBuild.OnDelete += OnCombatBuildDelete;
            conn.Db.HubCombatBuildContractDefinition.OnInsert += OnCombatBuildContractInsert;
            conn.Db.HubCombatBuildContractDefinition.OnUpdate += OnCombatBuildContractUpdate;
            conn.Db.HubCombatBuildContractDefinition.OnDelete += OnCombatBuildContractDelete;
            conn.Db.HubCombatBuildDisciplineDefinition.OnInsert += OnDisciplineInsert;
            conn.Db.HubCombatBuildDisciplineDefinition.OnUpdate += OnDisciplineUpdate;
            conn.Db.HubCombatBuildDisciplineDefinition.OnDelete += OnDisciplineDelete;
            conn.Db.HubSpellSchoolDefinition.OnInsert += OnSchoolInsert;
            conn.Db.HubSpellSchoolDefinition.OnUpdate += OnSchoolUpdate;
            conn.Db.HubSpellSchoolDefinition.OnDelete += OnSchoolDelete;
            conn.Db.HubCombatBuildAbilityDefinition.OnInsert += OnAbilityInsert;
            conn.Db.HubCombatBuildAbilityDefinition.OnUpdate += OnAbilityUpdate;
            conn.Db.HubCombatBuildAbilityDefinition.OnDelete += OnAbilityDelete;
            conn.Db.HubArmorSetDefinition.OnInsert += OnArmorSetInsert;
            conn.Db.HubArmorSetDefinition.OnUpdate += OnArmorSetUpdate;
            conn.Db.HubArmorSetDefinition.OnDelete += OnArmorSetDelete;
            conn.Db.HubWeaponDefinition.OnInsert += OnWeaponInsert;
            conn.Db.HubWeaponDefinition.OnUpdate += OnWeaponUpdate;
            conn.Db.HubWeaponDefinition.OnDelete += OnWeaponDelete;
            conn.Db.HubWeaponColorDefinition.OnInsert += OnWeaponColorInsert;
            conn.Db.HubWeaponColorDefinition.OnUpdate += OnWeaponColorUpdate;
            conn.Db.HubWeaponColorDefinition.OnDelete += OnWeaponColorDelete;
        }

        private void UnbindRows(HubConnection conn)
        {
            conn.Db.MyHubPlayer.OnInsert -= OnHubPlayerInsert;
            conn.Db.MyHubPlayer.OnUpdate -= OnHubPlayerUpdate;
            conn.Db.MyHubPlayer.OnDelete -= OnHubPlayerDelete;
            conn.Db.MyMatchStatus.OnInsert -= OnMatchStatusInsert;
            conn.Db.MyMatchStatus.OnUpdate -= OnMatchStatusUpdate;
            conn.Db.MyMatchStatus.OnDelete -= OnMatchStatusDelete;
            conn.Db.MyHubArmorSelection.OnInsert -= OnArmorSelectionInsert;
            conn.Db.MyHubArmorSelection.OnUpdate -= OnArmorSelectionUpdate;
            conn.Db.MyHubArmorSelection.OnDelete -= OnArmorSelectionDelete;
            conn.Db.MyCombatBuild.OnInsert -= OnCombatBuildInsert;
            conn.Db.MyCombatBuild.OnUpdate -= OnCombatBuildUpdate;
            conn.Db.MyCombatBuild.OnDelete -= OnCombatBuildDelete;
            conn.Db.HubCombatBuildContractDefinition.OnInsert -= OnCombatBuildContractInsert;
            conn.Db.HubCombatBuildContractDefinition.OnUpdate -= OnCombatBuildContractUpdate;
            conn.Db.HubCombatBuildContractDefinition.OnDelete -= OnCombatBuildContractDelete;
            conn.Db.HubCombatBuildDisciplineDefinition.OnInsert -= OnDisciplineInsert;
            conn.Db.HubCombatBuildDisciplineDefinition.OnUpdate -= OnDisciplineUpdate;
            conn.Db.HubCombatBuildDisciplineDefinition.OnDelete -= OnDisciplineDelete;
            conn.Db.HubSpellSchoolDefinition.OnInsert -= OnSchoolInsert;
            conn.Db.HubSpellSchoolDefinition.OnUpdate -= OnSchoolUpdate;
            conn.Db.HubSpellSchoolDefinition.OnDelete -= OnSchoolDelete;
            conn.Db.HubCombatBuildAbilityDefinition.OnInsert -= OnAbilityInsert;
            conn.Db.HubCombatBuildAbilityDefinition.OnUpdate -= OnAbilityUpdate;
            conn.Db.HubCombatBuildAbilityDefinition.OnDelete -= OnAbilityDelete;
            conn.Db.HubArmorSetDefinition.OnInsert -= OnArmorSetInsert;
            conn.Db.HubArmorSetDefinition.OnUpdate -= OnArmorSetUpdate;
            conn.Db.HubArmorSetDefinition.OnDelete -= OnArmorSetDelete;
            conn.Db.HubWeaponDefinition.OnInsert -= OnWeaponInsert;
            conn.Db.HubWeaponDefinition.OnUpdate -= OnWeaponUpdate;
            conn.Db.HubWeaponDefinition.OnDelete -= OnWeaponDelete;
            conn.Db.HubWeaponColorDefinition.OnInsert -= OnWeaponColorInsert;
            conn.Db.HubWeaponColorDefinition.OnUpdate -= OnWeaponColorUpdate;
            conn.Db.HubWeaponColorDefinition.OnDelete -= OnWeaponColorDelete;
            conn.Reducers.OnRequestUnranked2V2BotMatch -= OnRequestMatchResult;
            conn.Reducers.OnRequestOpenWorldInstance -= OnRequestOpenWorldInstanceResult;
            conn.Reducers.OnSaveHubArmorSet -= OnSaveArmorSetResult;
            conn.Reducers.OnSaveCombatBuild -= OnSaveCombatBuildResult;
        }

        private void OnHubPlayerInsert(HubEventContext _, HubPlayerRow row) => ApplyPlayer(row);
        private void OnHubPlayerUpdate(HubEventContext _, HubPlayerRow __, HubPlayerRow row) => ApplyPlayer(row);

        private void OnHubPlayerDelete(HubEventContext _, HubPlayerRow __)
        {
            _player = null;
            NotifyChanged();
        }

        private void OnMatchStatusInsert(HubEventContext _, HubMatchStatusRow row) => ApplyMatchStatus(row);
        private void OnMatchStatusUpdate(HubEventContext _, HubMatchStatusRow __, HubMatchStatusRow row) => ApplyMatchStatus(row);

        private void OnMatchStatusDelete(HubEventContext _, HubMatchStatusRow __)
        {
            _matchStatus = null;
            _activeClientRequestId = null;
            _pendingOpenWorldDestination = null;
            _requestAwaitingConfirmation = false;
            NotifyChanged();
        }

        private void OnArmorSelectionInsert(HubEventContext _, HubArmorSelectionRow row) => ApplyArmorSelection(row);
        private void OnArmorSelectionUpdate(HubEventContext _, HubArmorSelectionRow __, HubArmorSelectionRow row) => ApplyArmorSelection(row);

        private void OnArmorSelectionDelete(HubEventContext context, HubArmorSelectionRow __)
        {
            // Views do not expose a primary key in the generated client bindings.
            // Updating the backing row is therefore delivered as an insert of the
            // replacement followed by a delete of the old projection. The cache is
            // already fully updated before callbacks run, so retain any replacement
            // instead of treating the delete callback as proof that the view is empty.
            foreach (HubArmorSelectionRow row in context.Db.MyHubArmorSelection.Iter())
            {
                ApplyArmorSelection(row);
                return;
            }

            _armorLoadout = null;
            NotifyChanged();
        }

        private void OnCombatBuildInsert(HubEventContext _, HubCombatBuildRow row) => ApplyCombatBuild(row);
        private void OnCombatBuildUpdate(HubEventContext _, HubCombatBuildRow __, HubCombatBuildRow row) => ApplyCombatBuild(row);

        private void OnCombatBuildDelete(HubEventContext context, HubCombatBuildRow __)
        {
            foreach (HubCombatBuildRow row in context.Db.MyCombatBuild.Iter())
            {
                ApplyCombatBuild(row);
                return;
            }

            _combatBuild = null;
            NotifyChanged();
        }

        private void OnCombatBuildContractInsert(HubEventContext _, HubCombatBuildContractRow __)
            => RefreshCatalogSnapshots();
        private void OnCombatBuildContractUpdate(
            HubEventContext _,
            HubCombatBuildContractRow __,
            HubCombatBuildContractRow ___)
            => RefreshCatalogSnapshots();
        private void OnCombatBuildContractDelete(HubEventContext _, HubCombatBuildContractRow __)
            => RefreshCatalogSnapshots();
        private void OnDisciplineInsert(HubEventContext _, HubDisciplineRow __) => RefreshCatalogSnapshots();
        private void OnDisciplineUpdate(HubEventContext _, HubDisciplineRow __, HubDisciplineRow ___) => RefreshCatalogSnapshots();
        private void OnDisciplineDelete(HubEventContext _, HubDisciplineRow __) => RefreshCatalogSnapshots();
        private void OnSchoolInsert(HubEventContext _, HubSchoolRow __) => RefreshCatalogSnapshots();
        private void OnSchoolUpdate(HubEventContext _, HubSchoolRow __, HubSchoolRow ___) => RefreshCatalogSnapshots();
        private void OnSchoolDelete(HubEventContext _, HubSchoolRow __) => RefreshCatalogSnapshots();
        private void OnAbilityInsert(HubEventContext _, HubAbilityRow __) => RefreshCatalogSnapshots();
        private void OnAbilityUpdate(HubEventContext _, HubAbilityRow __, HubAbilityRow ___) => RefreshCatalogSnapshots();
        private void OnAbilityDelete(HubEventContext _, HubAbilityRow __) => RefreshCatalogSnapshots();
        private void OnArmorSetInsert(HubEventContext _, HubArmorSetRow __) => RefreshCatalogSnapshots();
        private void OnArmorSetUpdate(HubEventContext _, HubArmorSetRow __, HubArmorSetRow ___) => RefreshCatalogSnapshots();
        private void OnArmorSetDelete(HubEventContext _, HubArmorSetRow __) => RefreshCatalogSnapshots();
        private void OnWeaponInsert(HubEventContext _, HubWeaponRow __) => RefreshCatalogSnapshots();
        private void OnWeaponUpdate(HubEventContext _, HubWeaponRow __, HubWeaponRow ___) => RefreshCatalogSnapshots();
        private void OnWeaponDelete(HubEventContext _, HubWeaponRow __) => RefreshCatalogSnapshots();
        private void OnWeaponColorInsert(HubEventContext _, HubWeaponColorRow __) => RefreshCatalogSnapshots();
        private void OnWeaponColorUpdate(HubEventContext _, HubWeaponColorRow __, HubWeaponColorRow ___) => RefreshCatalogSnapshots();
        private void OnWeaponColorDelete(HubEventContext _, HubWeaponColorRow __) => RefreshCatalogSnapshots();

        private void ApplyPlayer(HubPlayerRow row)
        {
            _player = new HubPlayerSnapshot(row.Identity, row.DisplayName);
            NotifyChanged();
        }

        private void ApplyMatchStatus(HubMatchStatusRow row)
        {
            _matchStatus = new HubMatchStatusSnapshot(
                row.TicketId,
                row.QueueKind,
                row.Format,
                row.Status,
                row.FailureCode,
                row.MatchId,
                row.ServerUri,
                row.DatabaseIdentity,
                row.MatchBuildId,
                row.MapId,
                row.CreatedAt.MicrosecondsSinceUnixEpoch,
                row.UpdatedAt.MicrosecondsSinceUnixEpoch,
                row.ExpiresAt.MicrosecondsSinceUnixEpoch,
                row.ReadyAt?.MicrosecondsSinceUnixEpoch,
                row.AssignmentExpiresAt?.MicrosecondsSinceUnixEpoch);
            _requestAwaitingConfirmation = false;
            if (!string.Equals(row.Status, "FAILED", StringComparison.Ordinal))
                LastError = string.Empty;
            NotifyChanged();
        }

        private void ApplyArmorSelection(HubArmorSelectionRow row)
        {
            _armorLoadout = new HubArmorLoadoutSnapshot(
                row.ArmorSetId,
                row.Revision);
            NotifyChanged();
        }

        private void ApplyCombatBuild(HubCombatBuildRow row)
        {
            _combatBuild = HubCombatBuildDraft.FromRow(row);
            NotifyChanged();
        }

        private void RefreshCatalogSnapshots()
        {
            HubConnection? conn = _conn;
            if (conn == null)
                return;

            _combatBuildContract = conn.Db.HubCombatBuildContractDefinition.Iter()
                .Select(row => new HubCombatBuildContractSnapshot(row))
                .FirstOrDefault();
            _disciplines = conn.Db.HubCombatBuildDisciplineDefinition.Iter()
                .OrderBy(row => row.SortOrder)
                .ThenBy(row => row.CombatDisciplineId, StringComparer.Ordinal)
                .Select(row => new HubDisciplineSnapshot(
                    row.CombatDisciplineId,
                    row.DisplayName,
                    row.SortOrder,
                    new HubCombatBuildWeapon(
                        row.StarterMainHandItemDefId,
                        row.StarterMainHandColorId,
                        row.StarterOffHandItemDefId,
                        row.StarterOffHandColorId)))
                .ToArray();
            _staffSchools = conn.Db.HubSpellSchoolDefinition.Iter()
                .OrderBy(row => row.SortOrder)
                .ThenBy(row => row.SpellSchoolId, StringComparer.Ordinal)
                .Select(row => new HubSpellSchoolSnapshot(
                    row.SpellSchoolId,
                    row.DisplayName,
                    row.SortOrder))
                .ToArray();
            _abilities = conn.Db.HubCombatBuildAbilityDefinition.Iter()
                .OrderBy(row => row.SortOrder)
                .ThenBy(row => row.AbilityId, StringComparer.Ordinal)
                .Select(row => new HubAbilitySnapshot(
                    row.AbilityId,
                    row.CombatDisciplineId,
                    row.SpellSchoolId,
                    row.SelectionKind,
                    row.DisplayName,
                    row.ResourceKind,
                    row.ResourceCost,
                    row.Description,
                    row.SortOrder))
                .ToArray();
            _armorSets = conn.Db.HubArmorSetDefinition.Iter()
                .OrderBy(row => row.SortOrder)
                .ThenBy(row => row.ArmorSetId, StringComparer.Ordinal)
                .Select(row => new HubArmorSetSnapshot(
                    row.ArmorSetId,
                    row.DisplayName,
                    row.ArmorTier,
                    row.PhysicalResistance,
                    row.MagicalResistance,
                    row.MoveSpeedModifier,
                    row.CastSpeedModifier,
                    row.PieceCount,
                    row.SortOrder))
                .ToArray();
            _weapons = conn.Db.HubWeaponDefinition.Iter()
                .OrderBy(row => row.SortOrder)
                .ThenBy(row => row.ItemDefId, StringComparer.Ordinal)
                .Select(row => new HubWeaponSnapshot(
                    row.ItemDefId,
                    row.DisplayName,
                    row.IconId,
                    row.WeaponKind,
                    row.HandRequirement,
                    row.EquipSlot,
                    row.CombatDisciplineId,
                    row.SortOrder))
                .ToArray();
            _weaponColors = conn.Db.HubWeaponColorDefinition.Iter()
                .OrderBy(row => row.ItemDefId, StringComparer.Ordinal)
                .ThenBy(row => row.SortOrder)
                .ThenBy(row => row.ColorId, StringComparer.Ordinal)
                .Select(row => new HubWeaponColorSnapshot(
                    row.ItemDefId,
                    row.ColorId,
                    row.DisplayName,
                    row.ColorHex,
                    row.SortOrder))
                .ToArray();
            NotifyChanged();
        }

        private void RefreshSnapshotsFromCache(HubConnection conn)
        {
            _player = null;
            foreach (HubPlayerRow row in conn.Db.MyHubPlayer.Iter())
            {
                _player = new HubPlayerSnapshot(row.Identity, row.DisplayName);
                break;
            }

            _matchStatus = null;
            foreach (HubMatchStatusRow row in conn.Db.MyMatchStatus.Iter())
            {
                _matchStatus = new HubMatchStatusSnapshot(
                    row.TicketId,
                    row.QueueKind,
                    row.Format,
                    row.Status,
                    row.FailureCode,
                    row.MatchId,
                    row.ServerUri,
                    row.DatabaseIdentity,
                    row.MatchBuildId,
                    row.MapId,
                    row.CreatedAt.MicrosecondsSinceUnixEpoch,
                    row.UpdatedAt.MicrosecondsSinceUnixEpoch,
                    row.ExpiresAt.MicrosecondsSinceUnixEpoch,
                    row.ReadyAt?.MicrosecondsSinceUnixEpoch,
                    row.AssignmentExpiresAt?.MicrosecondsSinceUnixEpoch);
                _requestAwaitingConfirmation = false;
                break;
            }

            _armorLoadout = null;
            foreach (HubArmorSelectionRow row in conn.Db.MyHubArmorSelection.Iter())
            {
                _armorLoadout = new HubArmorLoadoutSnapshot(
                    row.ArmorSetId,
                    row.Revision);
                break;
            }

            _combatBuild = null;
            foreach (HubCombatBuildRow row in conn.Db.MyCombatBuild.Iter())
            {
                _combatBuild = HubCombatBuildDraft.FromRow(row);
                break;
            }
            RefreshCatalogSnapshots();
        }

        private void OnRequestOpenWorldInstanceResult(
            HubReducerEventContext context,
            string clientRequestId,
            string destination)
            => OnRequestMatchResult(context, clientRequestId);

        private void OnRequestMatchResult(HubReducerEventContext context, string clientRequestId)
        {
            if (!_hasIdentity
                || context.Event.CallerIdentity != _identity
                || !string.Equals(clientRequestId, _activeClientRequestId, StringComparison.Ordinal))
            {
                return;
            }

            if (context.Event.Status is Status.Committed)
            {
                // Keep the UI pending until the caller-filtered status view
                // confirms the ticket. If transport drops in this gap, the
                // same stable request ID is safe to resend.
                NotifyChanged();
                return;
            }

            _requestAwaitingConfirmation = false;
            LastError = context.Event.Status switch
            {
                Status.Failed(var failure) => failure,
                Status.OutOfEnergy(var _) => "The Hub was temporarily out of reducer energy.",
                _ => "The Hub did not commit the match request.",
            };
            NotifyChanged();
        }

        private void OnSaveArmorSetResult(HubReducerEventContext context, string armorSetId)
        {
            if (!_hasIdentity || context.Event.CallerIdentity != _identity)
                return;

            bool committed = context.Event.Status is Status.Committed;
            ArmorSetSaveCompleted?.Invoke(
                committed,
                ReducerFailureMessage(context.Event.Status, "The Hub did not save the armor set."));
        }

        private void OnSaveCombatBuildResult(
            HubReducerEventContext context,
            Arena.HubDb.CombatBuildDraftInput _)
        {
            if (!_hasIdentity || context.Event.CallerIdentity != _identity)
                return;

            bool committed = context.Event.Status is Status.Committed;
            CombatBuildSaveCompleted?.Invoke(
                committed,
                ReducerFailureMessage(context.Event.Status, "The Hub did not save the combat build."));
        }

        private static string ReducerFailureMessage(Status status, string fallback)
        {
            return status switch
            {
                Status.Committed => string.Empty,
                Status.Failed(var failure) => failure,
                Status.OutOfEnergy(var _) => "The Hub was temporarily out of reducer energy.",
                _ => fallback,
            };
        }

        private void SetError(string message, bool disconnect)
        {
            LastError = message;
            State = HubConnectionState.Error;
            _nextRetryRealtime = Time.unscaledTime + RetryDelaySeconds;
            if (disconnect)
                DisposeConnection();
            NotifyChanged();
        }

        private void DisposeConnection()
        {
            HubConnection? conn = _conn;
            _conn = null;
            _subscription = null;
            _hasIdentity = false;
            _player = null;
            _matchStatus = null;
            ClearLoadoutSnapshots();
            if (conn == null)
                return;

            UnbindRows(conn);
            try
            {
                conn.Disconnect();
            }
            catch (Exception error)
            {
                Debug.LogWarning($"[HubNetworkManager] Disconnect failed: {error.Message}");
            }
        }

        private void ClearLoadoutSnapshots()
        {
            _armorLoadout = null;
            _combatBuild = null;
            _combatBuildContract = null;
            _disciplines = Array.Empty<HubDisciplineSnapshot>();
            _staffSchools = Array.Empty<HubSpellSchoolSnapshot>();
            _abilities = Array.Empty<HubAbilitySnapshot>();
            _armorSets = Array.Empty<HubArmorSetSnapshot>();
            _weapons = Array.Empty<HubWeaponSnapshot>();
            _weaponColors = Array.Empty<HubWeaponColorSnapshot>();
        }

        private void Update()
        {
            try
            {
                _conn?.FrameTick();
            }
            catch (Exception error)
            {
                SetError($"Hub connection failed while receiving data: {error.Message}", disconnect: true);
            }

            if (_maintainConnection
                && _conn == null
                && State == HubConnectionState.Error
                && Time.unscaledTime >= _nextRetryRealtime)
            {
                ConnectNow();
            }
        }

        private void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
            _maintainConnection = false;
            DisposeConnection();
        }

        private void NotifyChanged() => Changed?.Invoke();
    }
}
