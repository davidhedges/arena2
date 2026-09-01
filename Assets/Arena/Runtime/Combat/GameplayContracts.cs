#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Arena.Entity;
using Arena.Input;
using Arena.Presentation;
using SpacetimeDB.Types;
using UnityEngine;

namespace Arena.Combat
{
    public static class SpellIds
    {
        public const string Fireball = "FIREBALL";
        public const string Icicle = "ICICLE";
        public const string IceSpikes = "ICE_SPIKES";
        public const string Meteor = "METEOR";
        public const string FrostNeedle = "FROST_NEEDLE";
        public const string InstantBeam = "INSTANT_BEAM";
        public const string Electrocute = "ELECTROCUTE";
        public const string FrostNova = "FROST_NOVA";
        public const string FrozenGrasp = "FROZEN_GRASP";
        public const string Negate = "NEGATE";
        public const string BlindingLight = "BLINDING_LIGHT";
        public const string Protection = "PROTECTION";
        public const string GlacialSpike = "GLACIAL_SPIKE";
        public const string GustOfWind = "GUST_OF_WIND";
        public const string Buffet = "BUFFET";
        public const string Momentum = "MOMENTUM";
        public const string Intimidate = "INTIMIDATE";
        public const string Enrage = "ENRAGE";
        public const string Shockwave = "SHOCKWAVE";

        public static bool UsesChargedReleasePresentation(string? spellId)
        {
            return string.Equals(spellId, InstantBeam, StringComparison.Ordinal)
                || string.Equals(spellId, Electrocute, StringComparison.Ordinal);
        }
    }

    public static class CombatEventTypes
    {
        public const string Cast = "COMBAT_CAST";
        public const string Release = "COMBAT_RELEASE";
        public const string Update = "COMBAT_UPDATE";
        public const string Contact = "COMBAT_CONTACT";
        public const string Impact = "COMBAT_IMPACT";
        public const string AreaImpact = "COMBAT_AREA_IMPACT";
        public const string Fizzle = "COMBAT_FIZZLE";
        public const string Miss = "COMBAT_MISS";
        public const string Block = "COMBAT_BLOCK";
        public const string Parry = "COMBAT_PARRY";
        public const string Evade = "COMBAT_EVADE";
        public const string StatusEnd = "COMBAT_STATUS_END";
    }

    public static class CombatEventSources
    {
        public const string Spell = "SPELL";
        public const string PlayerInput = "player_input";
        public const string QueuedFollowup = "queued_followup";
        public const string AutoAttack = "auto_attack";
        public const string Proc = "proc";
        public const string Practice = "practice";
        public const string NpcMelee = "NPC_MELEE";

        public static bool IsPredictedLocalMeleeSource(string? source)
        {
            return string.Equals(source, PlayerInput, StringComparison.Ordinal)
                || string.Equals(source, QueuedFollowup, StringComparison.Ordinal);
        }
    }

    public static class CombatEventScalarKinds
    {
        public const string None = "";
        public const string TravelDurationSeconds = "TRAVEL_DURATION_SECONDS";
        public const string BeamChargePct = "BEAM_CHARGE_PCT";
        public const string MeleeReleaseDelaySeconds = "MELEE_RELEASE_DELAY_SECONDS";
    }

    public static class CombatEventMetadataKinds
    {
        public const string None = "";
        public const string ConsumedMeleeModifier = "CONSUMED_MELEE_MODIFIER";
        public const string FlurryProc = "FLURRY_PROC";
        public const string RestlessBladesProc = "RESTLESS_BLADES_PROC";
    }

    public static class CombatModeIds
    {
        public const string ShortDraw = "SHORT_DRAW";
        public const string FullDraw = "FULL_DRAW";
        public const string Ready = "READY";
        public const string Stealthed = "STEALTHED";
    }

    public static class SpellDefinitionContracts
    {
        public const string BehaviorInstantBeam = "INSTANT_BEAM";
        public const string BehaviorChannel = "CHANNEL";
        public const string BehaviorEmanation = "EMANATION";
        public const string BehaviorImmolation = "IMMOLATION";
        public const string BehaviorChargedRelease = "CHARGE";
        public const string TargetingPoint = "POINT";
        public const string TargetingSelf = "SELF";

        public static bool UsesPerSecondResourceCost(SpellDefinition? definition)
        {
            if (definition == null)
                return false;

            return string.Equals(definition.Behavior, BehaviorChannel, StringComparison.Ordinal)
                || string.Equals(definition.Behavior, BehaviorEmanation, StringComparison.Ordinal);
        }

        public static bool CastsOnRelease(SpellDefinition? definition)
        {
            if (definition == null)
                return false;

            return BehaviorCastsOnRelease(definition.Behavior);
        }

        public static bool BehaviorCastsOnRelease(string? behavior)
        {
            return string.Equals(behavior, BehaviorInstantBeam, StringComparison.Ordinal)
                || string.Equals(behavior, BehaviorChannel, StringComparison.Ordinal)
                || string.Equals(behavior, BehaviorChargedRelease, StringComparison.Ordinal);
        }

        public static bool UsesPointTargeting(SpellDefinition? definition)
        {
            return definition != null
                && string.Equals(definition.Targeting, TargetingPoint, StringComparison.Ordinal);
        }

        public static bool UsesSelfTargeting(SpellDefinition? definition)
        {
            return definition != null
                && string.Equals(definition.Targeting, TargetingSelf, StringComparison.Ordinal);
        }

        public static bool ShowsChargePresentation(SpellDefinition? definition)
        {
            return CastsOnRelease(definition);
        }
    }

    public static class ActionBarSlotIds
    {
        public const int GridRows = 2;
        public const int GridColumns = 9;
        public const int DisciplineColumns = 3;

        public const string Slot00 = "COMBAT_ACTION_00";
        public const string Slot01 = "COMBAT_ACTION_01";
        public const string Slot02 = "COMBAT_ACTION_02";
        public const string Slot03 = "COMBAT_ACTION_03";
        public const string Slot04 = "COMBAT_ACTION_04";
        public const string Slot05 = "COMBAT_ACTION_05";
        public const string Slot06 = "COMBAT_ACTION_06";
        public const string Slot07 = "COMBAT_ACTION_07";
        public const string Slot08 = "COMBAT_ACTION_08";
        public const string Slot10 = "COMBAT_ACTION_09";
        public const string Slot11 = "COMBAT_ACTION_10";
        public const string Slot12 = "COMBAT_ACTION_11";
        public const string Slot13 = "COMBAT_ACTION_12";
        public const string Slot14 = "COMBAT_ACTION_13";
        public const string Slot15 = "COMBAT_ACTION_14";
        public const string Slot16 = "COMBAT_ACTION_15";
        public const string Slot17 = "COMBAT_ACTION_16";
        public const string Slot18 = "COMBAT_ACTION_17";
        public const string Discipline0 = "discipline_0";
        public const string Discipline1 = "discipline_1";
        public const string Discipline2 = "discipline_2";

        private static readonly string[] OrderedGrid =
        {
            Slot00,
            Slot01,
            Slot02,
            Slot03,
            Slot04,
            Slot05,
            Slot06,
            Slot07,
            Slot08,
            Slot10,
            Slot11,
            Slot12,
            Slot13,
            Slot14,
            Slot15,
            Slot16,
            Slot17,
            Slot18,
        };

        private static readonly string[] OrderedDisciplines =
        {
            Discipline0,
            Discipline1,
            Discipline2,
        };

        public static IReadOnlyList<string> GridOrdered => OrderedGrid;
        public static IReadOnlyList<string> DisciplineOrdered => OrderedDisciplines;

        public static string ForGridCell(int row, int col)
        {
            if (row < 0 || row >= GridRows)
                throw new ArgumentOutOfRangeException(nameof(row), row, "Action-bar grid row is out of range.");
            if (col < 0 || col >= GridColumns)
                throw new ArgumentOutOfRangeException(nameof(col), col, "Action-bar grid column is out of range.");

            return OrderedGrid[row * GridColumns + col];
        }
    }

    public static class FixedActionIds
    {
        public const string Dodge = "DODGE";
        public const string Parry = "PARRY";
    }

    public static class MovementActionKeymap
    {
        public const string DodgeKeyLabel = "Q";
        public const KeyCode DodgeKeyCode = KeyCode.Q;
    }

    public static class DefenseActionKeymap
    {
        public const string ParryKeyLabel = "V";
        public const KeyCode ParryKeyCode = KeyCode.V;
    }

    public static class CombatRuleIds
    {
        public const string DefaultGlobalCooldownMs = "DEFAULT_GLOBAL_COOLDOWN_MS";
    }

    public static class ActionKinds
    {
        public const string Ability = "ABILITY";
        public const string Fixed = "FIXED";
        public const string CombatDisciplineSwitch = "COMBAT_DISCIPLINE_SWITCH";
    }

    public static class AbilityKinds
    {
        public const string Melee = "MELEE";
        public const string Spell = "SPELL";
        public const string Movement = "MOVEMENT";
        public const string AutoAttackReplacement = "AUTO_ATTACK_REPLACEMENT";
        public const string CombatModeToggle = "COMBAT_MODE_TOGGLE";

        public static bool UsesRawActionId(string? abilityKind)
        {
            string normalized = WireIdentifier.Normalize(abilityKind);
            return string.Equals(normalized, Movement, StringComparison.Ordinal)
                || string.Equals(normalized, AutoAttackReplacement, StringComparison.Ordinal)
                || string.Equals(normalized, CombatModeToggle, StringComparison.Ordinal);
        }
    }

    public readonly struct ActionBarSlotBinding
    {
        public readonly string KeyLabel;
        public readonly KeyCode KeyCode;
        public readonly bool RequiresShift;
        public readonly string SlotId;
        public readonly int Row;
        public readonly int Col;

        public ActionBarSlotBinding(string keyLabel, KeyCode keyCode, bool requiresShift, string slotId, int row, int col)
        {
            KeyLabel = keyLabel;
            KeyCode = keyCode;
            RequiresShift = requiresShift;
            SlotId = slotId;
            Row = row;
            Col = col;
        }
    }

    public static class ActionBarKeymap
    {
        private static readonly ActionBarSlotBinding[] Bindings =
        {
            new("1", KeyCode.Alpha1, false, ActionBarSlotIds.Slot00, 0, 0),
            new("2", KeyCode.Alpha2, false, ActionBarSlotIds.Slot01, 0, 1),
            new("3", KeyCode.Alpha3, false, ActionBarSlotIds.Slot02, 0, 2),
            new("4", KeyCode.Alpha4, false, ActionBarSlotIds.Slot03, 0, 3),
            new("5", KeyCode.Alpha5, false, ActionBarSlotIds.Slot04, 0, 4),
            new("6", KeyCode.Alpha6, false, ActionBarSlotIds.Slot05, 0, 5),
            new("7", KeyCode.Alpha7, false, ActionBarSlotIds.Slot06, 0, 6),
            new("8", KeyCode.Alpha8, false, ActionBarSlotIds.Slot07, 0, 7),
            new("9", KeyCode.Alpha9, false, ActionBarSlotIds.Slot08, 0, 8),
            new("0", KeyCode.Alpha0, false, ActionBarSlotIds.Slot10, 1, 0),
            new("E", KeyCode.E, false, ActionBarSlotIds.Slot11, 1, 1),
            new("R", KeyCode.R, false, ActionBarSlotIds.Slot12, 1, 2),
            new("T", KeyCode.T, false, ActionBarSlotIds.Slot13, 1, 3),
            new("F", KeyCode.F, false, ActionBarSlotIds.Slot14, 1, 4),
            new("G", KeyCode.G, false, ActionBarSlotIds.Slot15, 1, 5),
            new("Z", KeyCode.Z, false, ActionBarSlotIds.Slot16, 1, 6),
            new("X", KeyCode.X, false, ActionBarSlotIds.Slot17, 1, 7),
            new("C", KeyCode.C, false, ActionBarSlotIds.Slot18, 1, 8),
        };

        public static IReadOnlyList<ActionBarSlotBinding> SelectableBindings => Bindings;

        public static string KeyLabelForCell(int row, int col)
        {
            foreach (ActionBarSlotBinding binding in Bindings)
            {
                if (binding.Row == row && binding.Col == col)
                    return binding.KeyLabel;
            }

            return string.Empty;
        }

        public static bool TryGetBindingForCell(int row, int col, out ActionBarSlotBinding binding)
        {
            foreach (ActionBarSlotBinding candidate in Bindings)
            {
                if (candidate.Row != row || candidate.Col != col)
                    continue;

                binding = candidate;
                return true;
            }

            binding = default;
            return false;
        }

        public static bool TryGetBindingForSlotId(
            string? slotId,
            out ActionBarSlotBinding binding)
        {
            string normalized = WireIdentifier.Normalize(slotId);
            foreach (ActionBarSlotBinding candidate in Bindings)
            {
                if (!string.Equals(
                        WireIdentifier.Normalize(candidate.SlotId),
                        normalized,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                binding = candidate;
                return true;
            }

            binding = default;
            return false;
        }
    }

    public static class DisciplineBarKeymap
    {
        private static readonly ActionBarSlotBinding[] Bindings =
        {
            new("F1", KeyCode.F1, false, ActionBarSlotIds.Discipline0, 0, 0),
            new("F2", KeyCode.F2, false, ActionBarSlotIds.Discipline1, 0, 1),
            new("F3", KeyCode.F3, false, ActionBarSlotIds.Discipline2, 0, 2),
        };

        public static IReadOnlyList<ActionBarSlotBinding> SelectableBindings => Bindings;
    }

    public static class WireIdentifier
    {
        public static string Normalize(string? value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : value.Trim().ToUpperInvariant().Replace('-', '_');
        }
    }

    public static class RuntimeCombatProfile
    {
        public static string ResolveForPlayer(DbConnection? conn, Player? player)
        {
            return player == null
                ? CombatProfileIds.Default
                : ResolveForOwner(conn, player.Identity);
        }

        public static string ResolveForOwner(DbConnection? conn, SpacetimeDB.Identity? owner)
        {
            if (conn == null || !owner.HasValue)
                return CombatProfileIds.Default;

            ActiveCombatBuildDiscipline? discipline =
                conn.Db.ActiveCombatBuildDiscipline.Owner.Find(owner.Value);
            return discipline == null || string.IsNullOrWhiteSpace(discipline.CombatDisciplineId)
                ? CombatProfileIds.Default
                : CombatProfileIds.Normalize(discipline.CombatDisciplineId);
        }

        public static string ResolveForAbility(DbConnection? conn, AbilityCatalog? ability)
        {
            return ability == null
                ? string.Empty
                : WireIdentifier.Normalize(ability.CombatDisciplineId);
        }

        public static string ResolveForEntity(DbConnection? conn, PlayerEntity? entity)
        {
            return entity == null
                ? CombatProfileIds.Default
                : ResolveForOwner(conn, entity.Identity);
        }
    }

    public readonly struct ActiveActionBarAction
    {
        public readonly string SlotId;
        public readonly string ActionKind;
        public readonly string ActionRefId;
        public readonly string AbilityId;
        public readonly string AbilityKind;
        public readonly string AuthoredActionId;
        public readonly string ActionId;
        public readonly string DisplayName;
        public readonly string ResourceKind;
        public readonly float ResourceCost;
        public readonly bool IsAvailable;

        public ActiveActionBarAction(
            string slotId,
            string abilityId,
            string authoredActionId,
            string actionId,
            string displayName,
            string resourceKind = "",
            float resourceCost = 0f,
            string abilityKind = "",
            bool isAvailable = true)
            : this(
                slotId,
                string.IsNullOrWhiteSpace(abilityId) ? string.Empty : ActionKinds.Ability,
                abilityId,
                abilityId,
                abilityKind,
                authoredActionId,
                actionId,
                displayName,
                resourceKind,
                resourceCost,
                isAvailable)
        {
        }

        public ActiveActionBarAction(
            string slotId,
            string actionKind,
            string actionRefId,
            string abilityId,
            string abilityKind,
            string authoredActionId,
            string actionId,
            string displayName,
            string resourceKind = "",
            float resourceCost = 0f,
            bool isAvailable = true)
        {
            SlotId = slotId;
            ActionKind = actionKind;
            ActionRefId = actionRefId;
            AbilityId = abilityId;
            AbilityKind = WireIdentifier.Normalize(abilityKind);
            AuthoredActionId = authoredActionId;
            ActionId = actionId;
            DisplayName = displayName;
            ResourceKind = resourceKind;
            ResourceCost = Mathf.Max(0f, resourceCost);
            IsAvailable = isAvailable;
        }

        public bool HasAssignedAction => !string.IsNullOrWhiteSpace(ActionId);
        public bool CanTrigger => HasAssignedAction && IsAvailable;
        public bool IsAbility => string.Equals(ActionKind, ActionKinds.Ability, StringComparison.Ordinal);
        public bool IsFixed => string.Equals(ActionKind, ActionKinds.Fixed, StringComparison.Ordinal);
        public bool IsCombatDisciplineSwitch =>
            string.Equals(ActionKind, ActionKinds.CombatDisciplineSwitch, StringComparison.Ordinal);
        public bool IsMeleeAbility => IsAbility && string.Equals(AbilityKind, AbilityKinds.Melee, StringComparison.Ordinal);
        public bool IsSpellAbility => IsAbility && string.Equals(AbilityKind, AbilityKinds.Spell, StringComparison.Ordinal);
        public bool IsMovementAbility => IsAbility && string.Equals(AbilityKind, AbilityKinds.Movement, StringComparison.Ordinal);
        public bool IsAutoAttackReplacementAbility =>
            IsAbility && string.Equals(AbilityKind, AbilityKinds.AutoAttackReplacement, StringComparison.Ordinal);
        public bool IsCombatModeToggleAbility =>
            IsAbility && string.Equals(AbilityKind, AbilityKinds.CombatModeToggle, StringComparison.Ordinal);
    }

    public static class ActiveActionBarResolver
    {
        private sealed class BoundActiveSelection
        {
            internal BoundActiveSelection(
                string slotId,
                string abilityId,
                string specializationId,
                string combatDisciplineId,
                bool isSpell,
                byte barOrder,
                int specializationSlot,
                uint catalogSortOrder)
            {
                SlotId = slotId;
                AbilityId = abilityId;
                SpecializationId = specializationId;
                CombatDisciplineId = combatDisciplineId;
                IsSpell = isSpell;
                BarOrder = barOrder;
                SpecializationSlot = specializationSlot;
                CatalogSortOrder = catalogSortOrder;
            }

            internal string SlotId { get; set; }
            internal string AbilityId { get; }
            internal string SpecializationId { get; }
            internal string CombatDisciplineId { get; }
            internal bool IsSpell { get; }
            internal byte BarOrder { get; }
            internal int SpecializationSlot { get; }
            internal uint CatalogSortOrder { get; }
        }

        public static ActiveActionBarAction ResolveCombatDisciplineSwitchAction(
            DbConnection? conn,
            SpacetimeDB.Identity? owner,
            string slotId)
        {
            if (conn == null || !owner.HasValue || string.IsNullOrWhiteSpace(slotId))
                return Empty(slotId);

            string normalizedSlotId = WireIdentifier.Normalize(slotId);
            var seenParents = new HashSet<string>(StringComparer.Ordinal);
            int parentSlot = 0;
            foreach (MatchSelectedSpecializationV2 selected in conn.Db
                         .MatchSelectedSpecializationV2.Owner.Filter(owner.Value)
                         .OrderBy(row => row.SlotIndex))
            {
                string parent = WireIdentifier.Normalize(selected.CombatDisciplineId);
                if (string.IsNullOrWhiteSpace(parent) || !seenParents.Add(parent))
                    continue;
                string selectedSlotId = WireIdentifier.Normalize(
                    $"discipline_{parentSlot++}");
                if (string.Equals(selectedSlotId, normalizedSlotId, StringComparison.Ordinal))
                    return ResolveCombatDisciplineSwitch(conn, slotId, parent);
            }

            return Empty(slotId);
        }

        public static ActiveActionBarAction ResolveActiveSelectableAction(
            DbConnection? conn,
            SpacetimeDB.Identity? owner,
            string slotId)
        {
            if (conn == null || !owner.HasValue || string.IsNullOrWhiteSpace(slotId))
                return Empty(slotId);

            string normalizedSlotId = WireIdentifier.Normalize(slotId);
            BoundActiveSelection? selection = BuildBoundActiveSelections(conn, owner.Value)
                .FirstOrDefault(row => string.Equals(
                    WireIdentifier.Normalize(row.SlotId),
                    normalizedSlotId,
                    StringComparison.Ordinal));
            if (selection == null)
                return Empty(slotId);

            string activeDisciplineId = ResolveActiveDisciplineId(conn, owner.Value);
            bool isApplicable = selection.IsSpell
                || (!string.Equals(activeDisciplineId, "STAFF", StringComparison.Ordinal)
                    && string.Equals(
                        selection.CombatDisciplineId,
                        activeDisciplineId,
                        StringComparison.Ordinal));
            if (!isApplicable)
                return Empty(slotId);

            return ResolveExactAbilityAssignment(
                conn,
                owner.Value,
                selection.SlotId,
                selection.AbilityId);
        }

        public static IReadOnlyList<ActiveActionBarAction> ResolveSpellBarActions(
            DbConnection? conn,
            SpacetimeDB.Identity? owner)
            => ResolveVisibleBarActions(conn, owner, spells: true);

        public static IReadOnlyList<ActiveActionBarAction> ResolveTechniqueBarActions(
            DbConnection? conn,
            SpacetimeDB.Identity? owner)
            => ResolveVisibleBarActions(conn, owner, spells: false);

        public static ActiveActionBarAction ResolveActiveSelectableActionForAction(
            DbConnection? conn,
            SpacetimeDB.Identity? owner,
            string actionId)
        {
            if (conn == null || !owner.HasValue || string.IsNullOrWhiteSpace(actionId))
                return Empty(string.Empty);

            foreach (string slotId in ActionBarSlotIds.GridOrdered)
            {
                ActiveActionBarAction resolved =
                    ResolveActiveSelectableAction(conn, owner, slotId);
                if (!resolved.HasAssignedAction)
                    continue;

                if (string.Equals(resolved.ActionId, actionId, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(
                        resolved.AuthoredActionId,
                        actionId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return resolved;
                }
            }

            return Empty(string.Empty);
        }

        public static string ResolveDisplayNameForAction(
            DbConnection? conn,
            SpacetimeDB.Identity? owner,
            string actionId,
            string fallbackDisplayName)
        {
            ActiveActionBarAction resolved =
                ResolveActiveSelectableActionForAction(conn, owner, actionId);
            return resolved.HasAssignedAction
                ? ActionPresentation.ResolveAbilityDisplayName(
                    conn,
                    resolved.AbilityId,
                    resolved.DisplayName)
                : fallbackDisplayName;
        }

        public static string ResolveActiveDisciplineId(
            DbConnection? conn,
            SpacetimeDB.Identity? owner)
        {
            if (conn == null || !owner.HasValue)
                return string.Empty;

            ActiveCombatBuildDiscipline? active =
                conn.Db.ActiveCombatBuildDiscipline.Owner.Find(owner.Value);
            if (active == null)
                return string.Empty;

            string activeDisciplineId = WireIdentifier.Normalize(active.CombatDisciplineId);
            if (string.IsNullOrWhiteSpace(activeDisciplineId))
                return string.Empty;

            foreach (MatchSelectedSpecializationV2 selected in conn.Db
                         .MatchSelectedSpecializationV2.Owner.Filter(owner.Value))
            {
                if (string.Equals(
                        WireIdentifier.Normalize(selected.CombatDisciplineId),
                        activeDisciplineId,
                        StringComparison.Ordinal))
                {
                    return activeDisciplineId;
                }
            }

            return string.Empty;
        }

        private static IReadOnlyList<ActiveActionBarAction> ResolveVisibleBarActions(
            DbConnection? conn,
            SpacetimeDB.Identity? owner,
            bool spells)
        {
            if (conn == null || !owner.HasValue)
                return Array.Empty<ActiveActionBarAction>();

            string activeDisciplineId = ResolveActiveDisciplineId(conn, owner.Value);
            if (!spells && (string.IsNullOrWhiteSpace(activeDisciplineId)
                            || string.Equals(activeDisciplineId, "STAFF", StringComparison.Ordinal)))
            {
                return Array.Empty<ActiveActionBarAction>();
            }

            return BuildBoundActiveSelections(conn, owner.Value)
                .Where(row => row.IsSpell == spells)
                .Where(row => spells || string.Equals(
                    row.CombatDisciplineId,
                    activeDisciplineId,
                    StringComparison.Ordinal))
                .Select(row => ResolveExactAbilityAssignment(
                    conn,
                    owner.Value,
                    row.SlotId,
                    row.AbilityId))
                .Where(row => row.HasAssignedAction)
                .ToArray();
        }

        private static IReadOnlyList<BoundActiveSelection> BuildBoundActiveSelections(
            DbConnection conn,
            SpacetimeDB.Identity owner)
        {
            Dictionary<string, int> specializationSlots = conn.Db
                .MatchSelectedSpecializationV2.Owner.Filter(owner)
                .GroupBy(row => WireIdentifier.Normalize(row.SpecializationId), StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.Min(row => (int)row.SlotIndex),
                    StringComparer.Ordinal);
            var rows = new List<BoundActiveSelection>();

            foreach (MatchSpellSelectionV2 selection in conn.Db
                         .MatchSpellSelectionV2.Owner.Filter(owner))
            {
                AddBoundSelection(rows, conn, specializationSlots, selection.AbilityId,
                    selection.SpecializationId, selection.CombatDisciplineId,
                    isSpell: true, barOrder: selection.BarOrder);
            }
            foreach (MatchTechniqueSelectionV2 selection in conn.Db
                         .MatchTechniqueSelectionV2.Owner.Filter(owner))
            {
                AddBoundSelection(rows, conn, specializationSlots, selection.AbilityId,
                    selection.SpecializationId, selection.CombatDisciplineId,
                    isSpell: false, barOrder: selection.BarOrder);
            }

            BoundActiveSelection[] ordered = rows
                .OrderBy(row => row.BarOrder)
                .ThenBy(row => row.SpecializationSlot)
                .ThenBy(row => row.IsSpell ? 0 : 1)
                .ThenBy(row => row.CatalogSortOrder)
                .ThenBy(row => row.AbilityId, StringComparer.Ordinal)
                .Take(ActionBarSlotIds.GridOrdered.Count)
                .ToArray();
            for (int index = 0; index < ordered.Length; index++)
                ordered[index].SlotId = ActionBarSlotIds.GridOrdered[index];
            return ordered;
        }

        private static void AddBoundSelection(
            ICollection<BoundActiveSelection> rows,
            DbConnection conn,
            IReadOnlyDictionary<string, int> specializationSlots,
            string abilityId,
            string specializationId,
            string combatDisciplineId,
            bool isSpell,
            byte barOrder)
        {
            string normalizedSpecializationId = WireIdentifier.Normalize(specializationId);
            if (!specializationSlots.TryGetValue(normalizedSpecializationId, out int slot))
                return;
            string normalizedAbilityId = WireIdentifier.Normalize(abilityId);
            uint sortOrder = conn.Db.AbilityCatalog.AbilityId.Find(normalizedAbilityId)?.SortOrder
                ?? uint.MaxValue;
            rows.Add(new BoundActiveSelection(
                string.Empty,
                normalizedAbilityId,
                normalizedSpecializationId,
                WireIdentifier.Normalize(combatDisciplineId),
                isSpell,
                barOrder,
                slot,
                sortOrder));
        }

        private static ActiveActionBarAction ResolveExactAbilityAssignment(
            DbConnection conn,
            SpacetimeDB.Identity owner,
            string slotId,
            string abilityId)
        {
            string normalizedAbilityId = WireIdentifier.Normalize(abilityId);
            AbilityCatalog? ability =
                conn.Db.AbilityCatalog.AbilityId.Find(normalizedAbilityId);
            if (ability == null)
                return Empty(slotId);

            string runtimeActionId = AbilityKinds.UsesRawActionId(ability.AbilityKind)
                ? WireIdentifier.Normalize(ability.ActionId)
                : CombatActionIds.ResolveRuntimeActionId(
                    conn,
                    RuntimeCombatProfile.ResolveForOwner(conn, owner),
                    ability.ActionId);
            string displayName = ActionPresentation.ResolveAbilityDisplayName(
                conn,
                ability.AbilityId,
                ability.DisplayName);

            return new ActiveActionBarAction(
                slotId,
                ActionKinds.Ability,
                normalizedAbilityId,
                normalizedAbilityId,
                ability.AbilityKind,
                ability.ActionId,
                runtimeActionId,
                displayName,
                ability.ResourceKind,
                ability.ResourceCost);
        }

        private static ActiveActionBarAction ResolveCombatDisciplineSwitch(
            DbConnection conn,
            string slotId,
            string disciplineId)
        {
            string normalizedDisciplineId = WireIdentifier.Normalize(disciplineId);
            if (string.IsNullOrWhiteSpace(normalizedDisciplineId))
                return Empty(slotId);

            ActionPresentationCatalog? presentation = ActionPresentation.FindPresentation(
                conn,
                ActionTooltipResolver.PresentationKindCombatDisciplineSwitch,
                normalizedDisciplineId);
            string displayName = string.IsNullOrWhiteSpace(presentation?.DisplayName)
                ? normalizedDisciplineId.Replace('_', ' ')
                : presentation.DisplayName;

            return new ActiveActionBarAction(
                slotId,
                ActionKinds.CombatDisciplineSwitch,
                normalizedDisciplineId,
                string.Empty,
                string.Empty,
                normalizedDisciplineId,
                normalizedDisciplineId,
                displayName,
                string.Empty,
                0f);
        }

        private static ActiveActionBarAction Empty(string slotId)
        {
            return new ActiveActionBarAction(
                slotId,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty);
        }
    }

    public static class MeleeGameplayResolver
    {
        public static MeleeAbilityCatalog? ResolveForAction(
            DbConnection? conn,
            SpacetimeDB.Identity? owner,
            string combatProfile,
            string actionId)
        {
            if (conn == null || !owner.HasValue || string.IsNullOrWhiteSpace(actionId))
                return null;

            string authoredActionId = CombatActionIds.ResolveAuthoredStrikeId(conn, combatProfile, actionId);
            ActiveActionBarAction direct =
                ActiveActionBarResolver.ResolveActiveSelectableActionForAction(conn, owner, authoredActionId);
            MeleeAbilityCatalog? directGameplay = ResolveForAbilityId(conn, direct.AbilityId);
            if (directGameplay != null)
                return directGameplay;

            string rootAuthoredActionId = FindComboRootAuthored(conn, combatProfile, actionId);
            if (string.IsNullOrWhiteSpace(rootAuthoredActionId)
                || string.Equals(rootAuthoredActionId, authoredActionId, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            ActiveActionBarAction root =
                ActiveActionBarResolver.ResolveActiveSelectableActionForAction(conn, owner, rootAuthoredActionId);
            AbilityCatalog? rootAbility = string.IsNullOrWhiteSpace(root.AbilityId)
                ? null
                : conn.Db.AbilityCatalog.AbilityId.Find(root.AbilityId);
            if (rootAbility == null || !string.Equals(rootAbility.AbilityKind, "MELEE", StringComparison.OrdinalIgnoreCase))
                return null;

            foreach (AbilityCatalog ability in conn.Db.AbilityCatalog.Iter())
            {
                if (!string.Equals(ability.AbilityKind, "MELEE", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!string.Equals(
                    RuntimeCombatProfile.ResolveForAbility(conn, ability),
                    RuntimeCombatProfile.ResolveForAbility(conn, rootAbility),
                    StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!string.Equals(ability.ActionId, authoredActionId, StringComparison.OrdinalIgnoreCase))
                    continue;

                return ResolveForAbilityId(conn, ability.AbilityId);
            }

            return null;
        }

        public static MeleeAbilityCatalog? ResolveForAbilityId(DbConnection? conn, string abilityId)
        {
            if (conn == null || string.IsNullOrWhiteSpace(abilityId))
                return null;

            AbilityCatalog? ability = conn.Db.AbilityCatalog.AbilityId.Find(abilityId);
            if (ability == null || !string.Equals(ability.AbilityKind, "MELEE", StringComparison.OrdinalIgnoreCase))
                return null;

            MeleeAbilityCatalog? gameplay = conn.Db.MeleeAbilityCatalog.AbilityId.Find(ability.AbilityId);
            if (gameplay == null)
                return null;

            return string.Equals(gameplay.ActionId, ability.ActionId, StringComparison.OrdinalIgnoreCase)
                ? gameplay
                : null;
        }

        public static string FindComboRootAuthored(DbConnection conn, string combatProfile, string actionId)
        {
            string current = CombatActionIds.ResolveRuntimeActionId(conn, combatProfile, actionId);
            for (int safety = 0; safety < 16; safety++)
            {
                MeleeDefinition? def = CombatActionIds.FindMeleeDefinition(conn, combatProfile, current);
                if (def == null || string.IsNullOrWhiteSpace(def.ComboFrom))
                    return CombatActionIds.ResolveAuthoredStrikeId(conn, combatProfile, current);

                current = def.ComboFrom.Trim();
            }

            return CombatActionIds.ResolveAuthoredStrikeId(conn, combatProfile, current);
        }
    }

    public static class MeleeAttackModifierResolver
    {
        public readonly struct ActiveModifierIdentity
        {
            public readonly string StatusKind;
            public readonly string StackGroup;

            public ActiveModifierIdentity(string statusKind, string stackGroup)
            {
                StatusKind = WireIdentifier.Normalize(statusKind);
                StackGroup = WireIdentifier.Normalize(stackGroup);
            }

            public bool HasValue =>
                !string.IsNullOrWhiteSpace(StatusKind) &&
                !string.IsNullOrWhiteSpace(StackGroup);
        }

        public static float ResolveEffectiveRange(
            DbConnection? conn,
            SpacetimeDB.Identity? owner,
            float baseRange,
            long nowMs,
            bool includeRangeBonus = true)
        {
            float modifierRange = ResolveActiveModifierGuideRange(conn, owner, nowMs);
            float range = modifierRange > 0f ? Mathf.Max(baseRange, modifierRange) : baseRange;
            return includeRangeBonus
                ? range + ResolveActiveModifierRangeBonus(conn, owner, nowMs)
                : range;
        }

        public static float ResolveActiveModifierRangeBonus(
            DbConnection? conn,
            SpacetimeDB.Identity? owner,
            long nowMs)
        {
            if (conn == null || !owner.HasValue)
                return 0f;

            float bonus = 0f;
            long nowMicros = nowMs * 1000L;
            foreach (StatusEffect effect in conn.Db.StatusEffect.Target.Filter(owner.Value))
            {
                if (!IsActive(effect, nowMicros))
                    continue;

                string effectKind = WireIdentifier.Normalize(effect.EffectKind);
                string stackGroup = WireIdentifier.Normalize(effect.StackGroup);
                if (string.IsNullOrWhiteSpace(effectKind) || string.IsNullOrWhiteSpace(stackGroup))
                    continue;

                foreach (MeleeAttackModifierCatalog modifier in conn.Db.MeleeAttackModifierCatalog.Iter())
                {
                    if (!string.Equals(WireIdentifier.Normalize(modifier.StatusKind), effectKind, StringComparison.Ordinal))
                        continue;
                    if (!string.Equals(WireIdentifier.Normalize(modifier.StackGroup), stackGroup, StringComparison.Ordinal))
                        continue;

                    bonus += Mathf.Max(0f, modifier.RangeBonus);
                    break;
                }
            }

            return bonus;
        }

        public static float ResolveActiveModifierGuideRange(
            DbConnection? conn,
            SpacetimeDB.Identity? owner,
            long nowMs)
        {
            if (conn == null || !owner.HasValue)
                return 0f;

            float range = 0f;
            long nowMicros = nowMs * 1000L;
            foreach (StatusEffect effect in conn.Db.StatusEffect.Target.Filter(owner.Value))
            {
                if (!IsActive(effect, nowMicros))
                    continue;

                string effectKind = WireIdentifier.Normalize(effect.EffectKind);
                string stackGroup = WireIdentifier.Normalize(effect.StackGroup);
                if (string.IsNullOrWhiteSpace(effectKind) || string.IsNullOrWhiteSpace(stackGroup))
                    continue;

                foreach (MeleeAttackModifierCatalog modifier in conn.Db.MeleeAttackModifierCatalog.Iter())
                {
                    if (!string.Equals(WireIdentifier.Normalize(modifier.StatusKind), effectKind, StringComparison.Ordinal))
                        continue;
                    if (!string.Equals(WireIdentifier.Normalize(modifier.StackGroup), stackGroup, StringComparison.Ordinal))
                        continue;

                    if (modifier.MinRange > 0f)
                        range = Mathf.Max(range, modifier.MinRange);
                }
            }

            return range;
        }

        public static ActiveModifierIdentity ResolveActiveModifierIdentity(
            DbConnection? conn,
            SpacetimeDB.Identity? owner,
            long nowMs)
        {
            if (conn == null || !owner.HasValue)
                return default;

            long nowMicros = nowMs * 1000L;
            foreach (StatusEffect effect in conn.Db.StatusEffect.Target.Filter(owner.Value))
            {
                if (!IsActive(effect, nowMicros))
                    continue;

                string effectKind = WireIdentifier.Normalize(effect.EffectKind);
                string stackGroup = WireIdentifier.Normalize(effect.StackGroup);
                if (string.IsNullOrWhiteSpace(effectKind) || string.IsNullOrWhiteSpace(stackGroup))
                    continue;

                foreach (MeleeAttackModifierCatalog modifier in conn.Db.MeleeAttackModifierCatalog.Iter())
                {
                    if (!string.Equals(WireIdentifier.Normalize(modifier.StatusKind), effectKind, StringComparison.Ordinal))
                        continue;
                    if (!string.Equals(WireIdentifier.Normalize(modifier.StackGroup), stackGroup, StringComparison.Ordinal))
                        continue;

                    return new ActiveModifierIdentity(effectKind, stackGroup);
                }
            }

            return default;
        }

        private static bool IsActive(StatusEffect effect, long nowMicros)
        {
            return effect.ExpiresAtMicros <= 0L || effect.ExpiresAtMicros > nowMicros;
        }
    }

    public static class CombatActionIds
    {
        private static readonly Dictionary<string, CombatAnimationSet?> CombatAnimationSetsByCombatProfile =
            new(StringComparer.OrdinalIgnoreCase);

        // Design-facing authored strike ids and runtime slot ids both still appear
        // at call sites. These helpers are the compatibility seam that normalizes
        // those references into the runtime action ids used by cooldown/combo
        // plumbing.
        public static string NormalizeRuntimeActionReference(string? actionId)
        {
            return string.IsNullOrWhiteSpace(actionId)
                ? string.Empty
                : actionId.Trim().ToLowerInvariant().Replace('-', '_');
        }

        public static MeleeDefinition? FindMeleeDefinition(DbConnection conn, string combatStyleId, string actionId)
        {
            string runtimeActionId = ResolveRuntimeActionId(conn, combatStyleId, actionId);
            string key = $"{CombatProfileIds.Normalize(combatStyleId)}:{runtimeActionId}";
            return conn.Db.MeleeDefinition.Key.Find(key);
        }

        public static string ResolveAuthoredStrikeId(
            DbConnection? conn,
            string combatStyleId,
            string? actionId)
        {
            string normalizedActionId = WireIdentifier.Normalize(actionId);
            if (string.IsNullOrWhiteSpace(normalizedActionId))
                return string.Empty;

            CombatAnimationSet? animationSet = ResolveAnimationSetForCombatProfile(combatStyleId);
            if (animationSet != null)
                return animationSet.ResolveAuthoredStrikeIdForRuntimeAction(normalizedActionId);

            return normalizedActionId;
        }

        public static string ResolveRuntimeActionId(
            DbConnection? conn,
            string combatStyleId,
            string? actionId)
        {
            string normalizedActionId = WireIdentifier.Normalize(actionId);
            if (string.IsNullOrWhiteSpace(normalizedActionId))
                return string.Empty;

            if (conn?.Db.SpellDefinition.Kind.Find(normalizedActionId) != null)
                return normalizedActionId;

            CombatAnimationSet? animationSet = ResolveAnimationSetForCombatProfile(combatStyleId);
            if (animationSet != null)
            {
                string resolvedRuntimeSlotId = animationSet.ResolveRuntimeSlotIdForStrikeReference(normalizedActionId);
                if (!string.IsNullOrWhiteSpace(resolvedRuntimeSlotId)
                    && !string.Equals(resolvedRuntimeSlotId, normalizedActionId, StringComparison.OrdinalIgnoreCase))
                {
                    return resolvedRuntimeSlotId;
                }
            }

            if (conn != null)
            {
                string normalizedCombatProfileId = WireIdentifier.Normalize(combatStyleId);
                foreach (MeleeDefinition definition in conn.Db.MeleeDefinition.Iter())
                {
                    if (!string.Equals(definition.CombatProfile, normalizedCombatProfileId, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (string.Equals(definition.Kind, normalizedActionId, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(definition.SlotId, normalizedActionId, StringComparison.OrdinalIgnoreCase))
                    {
                        return definition.Kind;
                    }
                }
            }

            return NormalizeRuntimeActionReference(actionId);
        }

        private static CombatAnimationSet? ResolveAnimationSetForCombatProfile(string? combatStyleId)
        {
            string normalizedCombatProfileId = CombatProfileIds.Normalize(combatStyleId);
            if (CombatAnimationSetsByCombatProfile.TryGetValue(normalizedCombatProfileId, out CombatAnimationSet? cached))
                return cached;

            CombatAnimationSet? loaded = CombatAnimationSetCatalog.Resolve(normalizedCombatProfileId);
            CombatAnimationSetsByCombatProfile[normalizedCombatProfileId] = loaded;
            return loaded;
        }
    }

    public static class ActionPresentation
    {
        public static string ResolveDisplayName(
            DbConnection? conn,
            SpacetimeDB.Identity? owner,
            string? actionId,
            string? fallbackDisplayName = null)
        {
            if (string.IsNullOrWhiteSpace(actionId))
                return fallbackDisplayName ?? string.Empty;

            string normalizedActionId = WireIdentifier.Normalize(actionId);
            string fallback = string.IsNullOrWhiteSpace(fallbackDisplayName)
                ? normalizedActionId
                : fallbackDisplayName.Trim();

            if (SoulstealerPresentation.IsSoulstealerAbility(normalizedActionId, normalizedActionId)
                && SoulstealerPresentation.HasStolenSoul(conn, owner))
            {
                return "Empower";
            }
            if (LightningReflexesPresentation.IsLightningReflexesAbility(normalizedActionId, normalizedActionId)
                && LightningReflexesPresentation.IsArmed(conn, owner))
            {
                return LightningReflexesPresentation.ArmedDisplayName;
            }

            ActionPresentationCatalog? fixedPresentation =
                FindPresentation(conn, ActionTooltipResolver.PresentationKindFixed, normalizedActionId);
            if (!string.IsNullOrWhiteSpace(fixedPresentation?.DisplayName))
                return fixedPresentation.DisplayName;

            if (conn != null && owner.HasValue)
            {
                string activeActionBarName = ActiveActionBarResolver.ResolveDisplayNameForAction(
                    conn,
                    owner,
                    normalizedActionId,
                    string.Empty);
                if (!string.IsNullOrWhiteSpace(activeActionBarName))
                    return activeActionBarName;
            }

            ActionPresentationCatalog? presentation = FindActionPresentation(conn, normalizedActionId);
            if (!string.IsNullOrWhiteSpace(presentation?.DisplayName))
                return presentation.DisplayName;

            return fallback;
        }

        public static string ResolveFixedDisplayName(DbConnection? conn, string? fixedActionId)
        {
            if (string.IsNullOrWhiteSpace(fixedActionId))
                return string.Empty;

            string normalizedActionId = WireIdentifier.Normalize(fixedActionId);
            ActionPresentationCatalog? presentation =
                FindPresentation(conn, ActionTooltipResolver.PresentationKindFixed, normalizedActionId);
            return string.IsNullOrWhiteSpace(presentation?.DisplayName)
                ? normalizedActionId
                : presentation.DisplayName;
        }

        public static string ResolveCompactLabel(
            DbConnection? conn,
            SpacetimeDB.Identity? owner,
            string? actionId,
            string? fallbackDisplayName = null)
        {
            string displayName = ResolveDisplayName(conn, owner, actionId, fallbackDisplayName);
            if (string.IsNullOrWhiteSpace(displayName))
                return string.Empty;

            string compact = displayName.Trim().ToUpperInvariant();
            return compact.Length <= 6 ? compact : compact.Substring(0, 6);
        }

        public static ActionPresentationCatalog? FindActionPresentation(
            DbConnection? conn,
            string? actionId)
        {
            if (conn == null || string.IsNullOrWhiteSpace(actionId))
                return null;

            string normalizedActionId = WireIdentifier.Normalize(actionId);
            return FindPresentation(conn, ActionTooltipResolver.PresentationKindSpell, normalizedActionId)
                ?? FindPresentation(conn, ActionTooltipResolver.PresentationKindFixed, normalizedActionId);
        }

        public static string ResolveAbilityDisplayName(
            DbConnection? conn,
            string? abilityId,
            string fallbackDisplayName)
        {
            ActionPresentationCatalog? presentation =
                FindPresentation(conn, ActionTooltipResolver.PresentationKindAbility, abilityId);
            return string.IsNullOrWhiteSpace(presentation?.DisplayName)
                ? fallbackDisplayName
                : presentation.DisplayName;
        }

        public static ActionPresentationCatalog? FindPresentation(
            DbConnection? conn,
            string presentationKind,
            string? presentationId)
        {
            if (conn == null || string.IsNullOrWhiteSpace(presentationId))
                return null;

            string key = $"{WireIdentifier.Normalize(presentationKind)}:{WireIdentifier.Normalize(presentationId)}";
            return conn.Db.ActionPresentationCatalog.Key.Find(key);
        }
    }

    public static class GameplayTuning
    {
        private const long FallbackDefaultGlobalCooldownMs = 1500L;
        private const long MaxDefaultGlobalCooldownMs = 60000L;

        public const float BaseMoveSpeed = MovementPrediction.MoveSpeed;
        public const float DefaultHitRadius = MovementPrediction.DefaultHitRadius;
        public const float DefaultHitHeight = MovementPrediction.DefaultHitHeight;

        public static long ResolveDefaultGlobalCooldownDurationMs(DbConnection? conn)
        {
            CombatRuleCatalog? row = conn?.Db.CombatRuleCatalog.CombatRuleId.Find(
                CombatRuleIds.DefaultGlobalCooldownMs);
            float value = row?.ScalarValue ?? FallbackDefaultGlobalCooldownMs;
            if (float.IsNaN(value) || float.IsInfinity(value) || value <= 0f)
                return FallbackDefaultGlobalCooldownMs;

            return (long)Mathf.Clamp(Mathf.Round(value), 1f, MaxDefaultGlobalCooldownMs);
        }
    }
}
