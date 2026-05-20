#nullable enable
using System;
using System.Collections.Generic;
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
        public const string InstantBeam = "INSTANT_BEAM";
        public const string Electrocute = "ELECTROCUTE";
        public const string FrostNova = "FROST_NOVA";
        public const string Negate = "NEGATE";
        public const string Momentum = "MOMENTUM";
        public const string GiantSwing = "GIANT_SWING";
        public const string Intimidate = "INTIMIDATE";
        public const string Enrage = "ENRAGE";

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
        public const string Block = "COMBAT_BLOCK";
        public const string Parry = "COMBAT_PARRY";
    }

    public static class CombatEventSources
    {
        public const string Spell = "SPELL";
        public const string PlayerInput = "player_input";
        public const string QueuedFollowup = "queued_followup";
        public const string AutoAttack = "auto_attack";
        public const string Practice = "practice";

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
    }

    public static class CombatModeIds
    {
        public const string ShortDraw = "SHORT_DRAW";
        public const string FullDraw = "FULL_DRAW";
    }

    public static class SpellDefinitionContracts
    {
        public const string BehaviorInstantBeam = "INSTANT_BEAM";
        public const string BehaviorChannel = "CHANNEL";
        public const string BehaviorChargedRelease = "CHARGE";
        public const string TargetingPoint = "POINT";
        public const string TargetingSelf = "SELF";

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

    public static class SelectableLoadoutSlotIds
    {
        public const int GridRows = 3;
        public const int GridColumns = 9;

        public const string Slot00 = "slot_0_0";
        public const string Slot01 = "slot_0_1";
        public const string Slot02 = "slot_0_2";
        public const string Slot03 = "slot_0_3";
        public const string Slot04 = "slot_0_4";
        public const string Slot05 = "slot_0_5";
        public const string Slot06 = "slot_0_6";
        public const string Slot07 = "slot_0_7";
        public const string Slot08 = "slot_0_8";
        public const string Slot10 = "slot_1_0";
        public const string Slot11 = "slot_1_1";
        public const string Slot12 = "slot_1_2";
        public const string Slot13 = "slot_1_3";
        public const string Slot14 = "slot_1_4";
        public const string Slot15 = "slot_1_5";
        public const string Slot16 = "slot_1_6";
        public const string Slot17 = "slot_1_7";
        public const string Slot18 = "slot_1_8";
        public const string Slot20 = "slot_2_0";
        public const string Slot21 = "slot_2_1";
        public const string Slot22 = "slot_2_2";
        public const string Slot23 = "slot_2_3";
        public const string Slot24 = "slot_2_4";
        public const string Slot25 = "slot_2_5";
        public const string Slot26 = "slot_2_6";
        public const string Slot27 = "slot_2_7";
        public const string Slot28 = "slot_2_8";

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
            Slot20,
            Slot21,
            Slot22,
            Slot23,
            Slot24,
            Slot25,
            Slot26,
            Slot27,
            Slot28,
        };

        public static IReadOnlyList<string> GridOrdered => OrderedGrid;

        public static string ForGridCell(int row, int col)
        {
            if (row < 0 || row >= GridRows)
                throw new ArgumentOutOfRangeException(nameof(row), row, "Loadout grid row is out of range.");
            if (col < 0 || col >= GridColumns)
                throw new ArgumentOutOfRangeException(nameof(col), col, "Loadout grid column is out of range.");

            return $"slot_{row}_{col}";
        }
    }

    public static class FixedActionIds
    {
        public const string Dodge = "DODGE";
        public const string Parry = "PARRY";
    }

    public static class CombatRuleIds
    {
        public const string DefaultGlobalCooldownMs = "DEFAULT_GLOBAL_COOLDOWN_MS";
    }

    public static class ActionKinds
    {
        public const string Ability = "ABILITY";
        public const string Fixed = "FIXED";
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
            new("1", KeyCode.Alpha1, false, SelectableLoadoutSlotIds.Slot00, 0, 0),
            new("2", KeyCode.Alpha2, false, SelectableLoadoutSlotIds.Slot01, 0, 1),
            new("3", KeyCode.Alpha3, false, SelectableLoadoutSlotIds.Slot02, 0, 2),
            new("4", KeyCode.Alpha4, false, SelectableLoadoutSlotIds.Slot03, 0, 3),
            new("5", KeyCode.Alpha5, false, SelectableLoadoutSlotIds.Slot04, 0, 4),
            new("6", KeyCode.Alpha6, false, SelectableLoadoutSlotIds.Slot05, 0, 5),
            new("7", KeyCode.Alpha7, false, SelectableLoadoutSlotIds.Slot06, 0, 6),
            new("8", KeyCode.Alpha8, false, SelectableLoadoutSlotIds.Slot07, 0, 7),
            new("9", KeyCode.Alpha9, false, SelectableLoadoutSlotIds.Slot08, 0, 8),
            new("Q", KeyCode.Q, false, SelectableLoadoutSlotIds.Slot10, 1, 0),
            new("E", KeyCode.E, false, SelectableLoadoutSlotIds.Slot11, 1, 1),
            new("R", KeyCode.R, false, SelectableLoadoutSlotIds.Slot12, 1, 2),
            new("T", KeyCode.T, false, SelectableLoadoutSlotIds.Slot13, 1, 3),
            new("F", KeyCode.F, false, SelectableLoadoutSlotIds.Slot14, 1, 4),
            new("G", KeyCode.G, false, SelectableLoadoutSlotIds.Slot15, 1, 5),
            new("Z", KeyCode.Z, false, SelectableLoadoutSlotIds.Slot16, 1, 6),
            new("X", KeyCode.X, false, SelectableLoadoutSlotIds.Slot17, 1, 7),
            new("C", KeyCode.C, false, SelectableLoadoutSlotIds.Slot18, 1, 8),
            new("S+1", KeyCode.Alpha1, true, SelectableLoadoutSlotIds.Slot20, 2, 0),
            new("S+2", KeyCode.Alpha2, true, SelectableLoadoutSlotIds.Slot21, 2, 1),
            new("S+3", KeyCode.Alpha3, true, SelectableLoadoutSlotIds.Slot22, 2, 2),
            new("S+4", KeyCode.Alpha4, true, SelectableLoadoutSlotIds.Slot23, 2, 3),
            new("S+5", KeyCode.Alpha5, true, SelectableLoadoutSlotIds.Slot24, 2, 4),
            new("S+6", KeyCode.Alpha6, true, SelectableLoadoutSlotIds.Slot25, 2, 5),
            new("S+7", KeyCode.Alpha7, true, SelectableLoadoutSlotIds.Slot26, 2, 6),
            new("S+8", KeyCode.Alpha8, true, SelectableLoadoutSlotIds.Slot27, 2, 7),
            new("S+9", KeyCode.Alpha9, true, SelectableLoadoutSlotIds.Slot28, 2, 8),
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

    public static class ClassIds
    {
        public const string Warrior = "WARRIOR";
        public const string Paladin = "PALADIN";
        public const string Ranger = "RANGER";

        public static string Canonicalize(string? classId)
        {
            string normalized = WireIdentifier.Normalize(classId);
            return normalized switch
            {
                "WARRIOR" => Warrior,
                "PALADIN" => Paladin,
                "ARCHER" => Ranger,
                _ => normalized,
            };
        }
    }

    public static class CombatProfileResolver
    {
        public static string ResolveForClass(DbConnection? conn, string? classId)
        {
            if (conn == null)
                return CombatProfileIds.Default;

            string canonicalClassId = ClassIds.Canonicalize(classId);
            if (string.IsNullOrWhiteSpace(canonicalClassId))
                return CombatProfileIds.Default;

            ClassCatalog? classRow = conn.Db.ClassCatalog.ClassId.Find(canonicalClassId);
            return classRow == null
                ? CombatProfileIds.Default
                : CombatProfileIds.Normalize(classRow.DefaultCombatProfileId);
        }

        public static string ResolveForPlayer(DbConnection? conn, Player? player)
        {
            if (player == null)
                return CombatProfileIds.Default;

            if (conn != null
                && string.IsNullOrWhiteSpace(player.ClassId)
                && conn.Db.PlayerState.PlayerId.Find(player.Identity)?.IsDummy == true)
            {
                return CombatProfileIds.Default;
            }

            return ResolveForClass(conn, player.ClassId);
        }

        public static string ResolveForOwner(DbConnection? conn, SpacetimeDB.Identity? owner)
        {
            if (conn == null || !owner.HasValue)
                return CombatProfileIds.Default;

            CharacterProgression? progression = conn.Db.CharacterProgression.Owner.Find(owner.Value);
            if (progression != null)
                return ResolveForClass(conn, progression.ClassId);

            Player? player = conn.Db.Player.Identity.Find(owner.Value);
            return player == null
                ? CombatProfileIds.Default
                : ResolveForPlayer(conn, player);
        }

        public static string ResolveForEntity(DbConnection? conn, PlayerEntity? entity)
        {
            return entity == null
                ? CombatProfileIds.Default
                : ResolveForOwner(conn, entity.Identity);
        }
    }

    public readonly struct ActiveSelectableLoadoutAction
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

        public ActiveSelectableLoadoutAction(
            string slotId,
            string abilityId,
            string authoredActionId,
            string actionId,
            string displayName,
            string resourceKind = "",
            float resourceCost = 0f,
            string abilityKind = "")
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
                resourceCost)
        {
        }

        public ActiveSelectableLoadoutAction(
            string slotId,
            string actionKind,
            string actionRefId,
            string abilityId,
            string abilityKind,
            string authoredActionId,
            string actionId,
            string displayName,
            string resourceKind = "",
            float resourceCost = 0f)
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
        }

        public bool HasAssignedAction => !string.IsNullOrWhiteSpace(ActionId);
        public bool IsAbility => string.Equals(ActionKind, ActionKinds.Ability, StringComparison.Ordinal);
        public bool IsFixed => string.Equals(ActionKind, ActionKinds.Fixed, StringComparison.Ordinal);
        public bool IsMeleeAbility => IsAbility && string.Equals(AbilityKind, AbilityKinds.Melee, StringComparison.Ordinal);
        public bool IsSpellAbility => IsAbility && string.Equals(AbilityKind, AbilityKinds.Spell, StringComparison.Ordinal);
        public bool IsMovementAbility => IsAbility && string.Equals(AbilityKind, AbilityKinds.Movement, StringComparison.Ordinal);
        public bool IsAutoAttackReplacementAbility =>
            IsAbility && string.Equals(AbilityKind, AbilityKinds.AutoAttackReplacement, StringComparison.Ordinal);
        public bool IsCombatModeToggleAbility =>
            IsAbility && string.Equals(AbilityKind, AbilityKinds.CombatModeToggle, StringComparison.Ordinal);
    }

    public static class ActiveLoadoutResolver
    {
        public static bool TryResolveActiveSpec(
            DbConnection? conn,
            SpacetimeDB.Identity? owner,
            out string classId,
            out string activeSpecId)
        {
            classId = string.Empty;
            activeSpecId = string.Empty;

            if (conn == null || !owner.HasValue)
                return false;

            CharacterProgression? progression = conn.Db.CharacterProgression.Owner.Find(owner.Value);
            if (progression == null || string.IsNullOrWhiteSpace(progression.ClassId))
                return false;

            classId = ClassIds.Canonicalize(progression.ClassId);
            if (string.IsNullOrWhiteSpace(classId))
                return false;

            foreach (CharacterClassLoadoutState state in conn.Db.CharacterClassLoadoutState.Owner.Filter(owner.Value))
            {
                if (!string.Equals(ClassIds.Canonicalize(state.ClassId), classId, StringComparison.Ordinal))
                    continue;
                if (string.IsNullOrWhiteSpace(state.ActiveSpecId))
                    return false;

                activeSpecId = state.ActiveSpecId;
                return true;
            }

            return false;
        }

        public static ActiveSelectableLoadoutAction ResolveActiveSelectableAction(
            DbConnection? conn,
            SpacetimeDB.Identity? owner,
            string slotId)
        {
            if (conn == null || !owner.HasValue || string.IsNullOrWhiteSpace(slotId))
                return new ActiveSelectableLoadoutAction(slotId, string.Empty, string.Empty, string.Empty, string.Empty);

            string normalizedSlotId = WireIdentifier.Normalize(slotId);

            if (!TryResolveActiveSpec(conn, owner, out string classId, out string activeSpecId))
                return new ActiveSelectableLoadoutAction(slotId, string.Empty, string.Empty, string.Empty, string.Empty);

            SavedSpecSlotAssignment? assignment = null;
            foreach (SavedSpecSlotAssignment row in conn.Db.SavedSpecSlotAssignment.SpecId.Filter(activeSpecId))
            {
                if (!string.Equals(row.SlotId, normalizedSlotId, StringComparison.OrdinalIgnoreCase))
                    continue;

                assignment = row;
                break;
            }

            return ResolveSelectableActionFromAssignment(
                conn,
                owner,
                classId,
                slotId,
                assignment);
        }

        public static ActiveSelectableLoadoutAction ResolveSelectableActionFromAssignment(
            DbConnection? conn,
            SpacetimeDB.Identity? owner,
            string classId,
            string slotId,
            SavedSpecSlotAssignment? assignment)
        {
            if (conn == null || string.IsNullOrWhiteSpace(slotId))
                return new ActiveSelectableLoadoutAction(slotId, string.Empty, string.Empty, string.Empty, string.Empty);

            string normalizedSlotId = WireIdentifier.Normalize(slotId);
            LoadoutSlotCatalog? slot = conn.Db.LoadoutSlotCatalog.SlotId.Find(normalizedSlotId);
            if (slot == null || assignment == null)
                return new ActiveSelectableLoadoutAction(slotId, string.Empty, string.Empty, string.Empty, string.Empty);

            string actionKind = WireIdentifier.Normalize(assignment.ActionKind);
            string actionRefId = WireIdentifier.Normalize(assignment.ActionId);
            if (string.IsNullOrWhiteSpace(actionKind) && !string.IsNullOrWhiteSpace(assignment.AbilityId))
            {
                actionKind = ActionKinds.Ability;
                actionRefId = WireIdentifier.Normalize(assignment.AbilityId);
            }

            if (string.Equals(actionKind, ActionKinds.Fixed, StringComparison.Ordinal))
            {
                string fixedDisplayName = ActionPresentation.ResolveFixedDisplayName(conn, actionRefId);
                AbilityCatalog? fixedAbility = FixedActionAbilityResolver.ResolveAbility(conn, owner, actionRefId);
                return new ActiveSelectableLoadoutAction(
                    slotId,
                    ActionKinds.Fixed,
                    actionRefId,
                    fixedAbility?.AbilityId ?? string.Empty,
                    fixedAbility?.AbilityKind ?? string.Empty,
                    actionRefId,
                    actionRefId,
                    fixedDisplayName,
                    fixedAbility?.ResourceKind ?? string.Empty,
                    fixedAbility?.ResourceCost ?? 0f);
            }

            if (!string.Equals(actionKind, ActionKinds.Ability, StringComparison.Ordinal))
            {
                return new ActiveSelectableLoadoutAction(
                    slotId,
                    actionKind,
                    actionRefId,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty);
            }

            AbilityCatalog? ability = conn.Db.AbilityCatalog.AbilityId.Find(actionRefId);
            if (ability == null)
                return new ActiveSelectableLoadoutAction(
                    slotId,
                    actionKind,
                    actionRefId,
                    actionRefId,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    actionRefId);

            string runtimeActionId = AbilityKinds.UsesRawActionId(ability.AbilityKind)
                ? WireIdentifier.Normalize(ability.ActionId)
                : CombatActionIds.ResolveRuntimeActionId(
                    conn,
                    CombatProfileResolver.ResolveForClass(conn, classId),
                    ability.ActionId);
            string displayName = ActionPresentation.ResolveAbilityDisplayName(
                conn,
                ability.AbilityId,
                ability.DisplayName);

            return new ActiveSelectableLoadoutAction(
                slotId,
                ActionKinds.Ability,
                ability.AbilityId,
                ability.AbilityId,
                ability.AbilityKind,
                ability.ActionId,
                runtimeActionId,
                displayName,
                ability.ResourceKind,
                ability.ResourceCost);
        }

        public static ActiveSelectableLoadoutAction ResolveActiveSelectableActionForAction(
            DbConnection? conn,
            SpacetimeDB.Identity? owner,
            string actionId)
        {
            if (conn == null || !owner.HasValue || string.IsNullOrWhiteSpace(actionId))
                return new ActiveSelectableLoadoutAction(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);

            foreach (string slotId in SelectableLoadoutSlotIds.GridOrdered)
            {
                ActiveSelectableLoadoutAction resolved = ResolveActiveSelectableAction(conn, owner, slotId);
                if (!resolved.HasAssignedAction)
                    continue;

                if (string.Equals(resolved.ActionId, actionId, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(resolved.AuthoredActionId, actionId, StringComparison.OrdinalIgnoreCase))
                    return resolved;
            }

            return new ActiveSelectableLoadoutAction(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);
        }

        public static string ResolveDisplayNameForAction(
            DbConnection? conn,
            SpacetimeDB.Identity? owner,
            string actionId,
            string fallbackDisplayName)
        {
            if (conn == null || !owner.HasValue || string.IsNullOrWhiteSpace(actionId))
                return fallbackDisplayName;

            foreach (string slotId in SelectableLoadoutSlotIds.GridOrdered)
            {
                ActiveSelectableLoadoutAction resolved = ResolveActiveSelectableAction(conn, owner, slotId);
                if (!resolved.HasAssignedAction)
                    continue;

                if (string.Equals(resolved.ActionId, actionId, StringComparison.OrdinalIgnoreCase))
                    return ActionPresentation.ResolveAbilityDisplayName(
                        conn,
                        resolved.AbilityId,
                        resolved.DisplayName);
            }

            return fallbackDisplayName;
        }
    }

    public static class FixedActionAbilityResolver
    {
        public static AbilityCatalog? ResolveAbility(
            DbConnection? conn,
            SpacetimeDB.Identity? owner,
            string fixedActionId)
        {
            if (conn == null || !owner.HasValue || string.IsNullOrWhiteSpace(fixedActionId))
                return null;

            CharacterProgression? progression = conn.Db.CharacterProgression.Owner.Find(owner.Value);
            if (progression == null)
                return null;

            string key = $"{WireIdentifier.Normalize(progression.ClassId)}:{WireIdentifier.Normalize(fixedActionId)}";
            FixedActionBindingCatalog? binding = conn.Db.FixedActionBindingCatalog.Key.Find(key);
            return binding == null
                ? null
                : conn.Db.AbilityCatalog.AbilityId.Find(binding.AbilityId);
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
            ActiveSelectableLoadoutAction direct =
                ActiveLoadoutResolver.ResolveActiveSelectableActionForAction(conn, owner, authoredActionId);
            MeleeAbilityCatalog? directGameplay = ResolveForAbilityId(conn, direct.AbilityId);
            if (directGameplay != null)
                return directGameplay;

            string rootAuthoredActionId = FindComboRootAuthored(conn, combatProfile, actionId);
            if (string.IsNullOrWhiteSpace(rootAuthoredActionId)
                || string.Equals(rootAuthoredActionId, authoredActionId, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            ActiveSelectableLoadoutAction root =
                ActiveLoadoutResolver.ResolveActiveSelectableActionForAction(conn, owner, rootAuthoredActionId);
            AbilityCatalog? rootAbility = string.IsNullOrWhiteSpace(root.AbilityId)
                ? null
                : conn.Db.AbilityCatalog.AbilityId.Find(root.AbilityId);
            if (rootAbility == null || !string.Equals(rootAbility.AbilityKind, "MELEE", StringComparison.OrdinalIgnoreCase))
                return null;

            foreach (AbilityCatalog ability in conn.Db.AbilityCatalog.Iter())
            {
                if (!string.Equals(ability.AbilityKind, "MELEE", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!string.Equals(ability.ClassId, rootAbility.ClassId, StringComparison.OrdinalIgnoreCase))
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
            long nowMs)
        {
            float modifierRange = ResolveActiveModifierGuideRange(conn, owner, nowMs);
            return modifierRange > 0f ? Mathf.Max(baseRange, modifierRange) : baseRange;
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

            ActionPresentationCatalog? fixedPresentation =
                FindPresentation(conn, ActionTooltipResolver.PresentationKindFixed, normalizedActionId);
            if (!string.IsNullOrWhiteSpace(fixedPresentation?.DisplayName))
                return fixedPresentation.DisplayName;

            if (conn != null && owner.HasValue)
            {
                string activeLoadoutName = ActiveLoadoutResolver.ResolveDisplayNameForAction(
                    conn,
                    owner,
                    normalizedActionId,
                    string.Empty);
                if (!string.IsNullOrWhiteSpace(activeLoadoutName))
                    return activeLoadoutName;
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
