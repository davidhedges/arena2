#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace Arena.Tests.Editor
{
    public sealed class UiInputContractTests
    {
        private const string RuntimeUiEventSystemPath = "Assets/Arena/Runtime/UI/RuntimeUiEventSystem.cs";
        private const string ActionBarInputDispatcherPath = "Assets/Arena/Runtime/Input/ActionBarInputDispatcher.cs";
        private const string SpellInputHandlerPath = "Assets/Arena/Runtime/Input/SpellInputHandler.cs";
        private const string GameplayContractsPath = "Assets/Arena/Runtime/Combat/GameplayContracts.cs";
        private const string GameplaySubscriptionPlannerPath = "Assets/Arena/Runtime/Network/GameplaySubscriptionPlanner.cs";
        private const string FixedActionDispatcherPath = "Assets/Arena/Runtime/Input/FixedActionDispatcher.cs";
        private const string HudControllerPath = "Assets/Arena/Runtime/UI/HUDController.cs";
        private const string HubControllerPath = "Assets/Arena/Runtime/UI/HubController.cs";
        private const string CharacterCreationControllerPath = "Assets/Arena/Runtime/UI/CharacterCreationController.cs";
        private const string ActionTooltipResolverPath = "Assets/Arena/Runtime/Combat/ActionTooltipResolver.cs";
        private const string SpellCatalogPanelPath = "Assets/Arena/Runtime/UI/SpellCatalogPanel.cs";
        private const string CharacterActionBarPanelPath = "Assets/Arena/Runtime/UI/CharacterActionBarPanel.cs";
        private const string ActionBarSlotViewFactoryPath = "Assets/Arena/Runtime/UI/ActionBarSlotViewFactory.cs";
        private const string ActionBarDragDropPath = "Assets/Arena/Runtime/UI/ActionBarDragDrop.cs";
        private const string ActionBarLayoutPath = "Assets/Arena/Runtime/UI/ActionBarLayout.cs";
        private const string TooltipPath = "Assets/Arena/Runtime/UI/Tooltip.cs";
        private const string ActionBarSlotPrefabAssetPath = "Assets/Arena/Resources/UI/ActionBar/ActionBarSlot.prefab";
        private const string ActionBarSlotTextureAssetPath = "Assets/Arena/Resources/UI/ActionBar/slot.png";
        private const string UnitFrameTextureAssetPath = "Assets/Arena/Resources/UI/UnitFrame/UnitFrame.png";
        private const string CombatVfxRegistryPath = "Assets/Arena/Resources/CombatVFX/CombatVFXRegistry.asset";
        private const string CombatVfxRegistrySourcePath = "Assets/Arena/Runtime/Presentation/VFX/CombatVFXRegistry.cs";
        private const string CombatVfxRegistryEditorPath = "Assets/Arena/Editor/CombatVFXRegistryEditor.cs";
        private const string CombatVfxTemplateRegistryPath = "Assets/Arena/Runtime/Presentation/CombatVFXTemplateRegistry.cs";
        private const string NegateVfxPath = "Assets/Arena/Runtime/Presentation/VFX/NegateVFX.cs";
        private const string BeamVfxPath = "Assets/Arena/Runtime/Presentation/VFX/BeamVFX.cs";
        private const string FrostNovaPrefabMetaPath = "Assets/Arena/Resources/CombatVFX/Area/VFX_DebuffAoE02_Ice_Arena.prefab.meta";
        private const string LightningPrefabMetaPath = "Assets/Arena/Resources/CombatVFX/Area/VFX_Lightning01_Arena.prefab.meta";
        private const string MeteorPrefabMetaPath = "Assets/Arena/Resources/CombatVFX/Area/VFX_SingleComet01_Fire_Arena.prefab.meta";
        private const string MeteorHeadPrefabMetaPath = "Assets/Arena/Resources/CombatVFX/Projectiles/VFX_Projectile_Comet_Orange_Arena.prefab.meta";
        private const string ServerSpellsPath = "server/src/spells/mod.rs";
        private const string ServerProgressionPath = "server/src/progression.rs";
        private const string ServerInventoryPath = "server/src/inventory.rs";
        private const string ServerAppearancePath = "server/src/appearance.rs";
        private const string ServerPlayerPath = "server/src/player.rs";
        private const string EntityRegistryPath = "Assets/Arena/Runtime/Entity/EntityRegistry.cs";

        [Test]
        public void RuntimeUiCode_UsesSharedInputSystemEventBootstrap()
        {
            string[] runtimeUiFiles = Directory
                .EnumerateFiles("Assets/Arena/Runtime/UI", "*.cs", SearchOption.AllDirectories)
                .Select(NormalizePath)
                .ToArray();

            var legacyUsages = FindFilesContaining(runtimeUiFiles, "StandaloneInputModule").ToArray();
            Assert.That(
                legacyUsages,
                Is.Empty,
                "Runtime UI must not use StandaloneInputModule. This project uses Unity's new Input System.");

            var directInputModuleUsages = FindFilesContaining(runtimeUiFiles, "InputSystemUIInputModule")
                .Where(path => !string.Equals(path, RuntimeUiEventSystemPath, StringComparison.Ordinal))
                .ToArray();
            Assert.That(
                directInputModuleUsages,
                Is.Empty,
                $"Runtime UI must use {RuntimeUiEventSystemPath} instead of configuring InputSystemUIInputModule inline.");
        }

        [Test]
        public void SpellInputCode_UsesReplicatedAimRadius()
        {
            string source = File.ReadAllText(SpellInputHandlerPath);

            Assert.That(source, Does.Not.Contain("MeteorRadius"));
            Assert.That(source, Does.Contain("HasAimRadius"));
            Assert.That(source, Does.Contain("AimRadius"));
            Assert.That(source, Does.Contain("MaxDistance"));
            Assert.That(source, Does.Contain("IsAimPointWithinMaxDistance"));
        }

        [Test]
        public void SpellVfxCode_DoesNotShadowReplicatedSpellGeometry()
        {
            string negate = File.ReadAllText(NegateVfxPath);
            Assert.That(negate, Does.Not.Contain("FollowForwardOffset"));
            Assert.That(negate, Does.Not.Contain("FollowHeightOffset"));
            Assert.That(negate, Does.Contain("castEvent.OriginY"));
            Assert.That(negate, Does.Contain("castEvent.MaxDistance"));

            string beam = File.ReadAllText(BeamVfxPath);
            Assert.That(beam, Does.Not.Contain("MaxLength"));
            Assert.That(beam, Does.Contain("castEvent.MaxDistance"));

            string templateRegistry = File.ReadAllText(CombatVfxTemplateRegistryPath);
            Assert.That(templateRegistry, Does.Not.Contain("VFX_METEOR_01"));
            Assert.That(templateRegistry, Does.Not.Contain("MeteorVFX"));
        }

        [Test]
        public void FrostNovaVfx_UsesRegistryPrefabTemplate()
        {
            string templateRegistry = File.ReadAllText(CombatVfxTemplateRegistryPath);
            Assert.That(templateRegistry, Does.Not.Contain("VFX_FROST_NOVA_01"));
            Assert.That(templateRegistry, Does.Not.Contain("FrostNovaVFX"));

            string prefabGuid = File.ReadLines(FrostNovaPrefabMetaPath)
                .First(line => line.StartsWith("guid: ", StringComparison.Ordinal))
                .Substring("guid: ".Length)
                .Trim();

            string registry = File.ReadAllText(CombatVfxRegistryPath);
            Assert.That(registry, Does.Contain("vfxId: VFX_FROST_NOVA_01"));
            Assert.That(registry, Does.Contain($"guid: {prefabGuid}"));
        }

        [Test]
        public void MeteorVfx_UsesRegistryPrefabTemplate()
        {
            string templateRegistry = File.ReadAllText(CombatVfxTemplateRegistryPath);
            Assert.That(templateRegistry, Does.Not.Contain("VFX_METEOR_01"));
            Assert.That(templateRegistry, Does.Not.Contain("MeteorVFX"));

            string prefabGuid = File.ReadLines(MeteorPrefabMetaPath)
                .First(line => line.StartsWith("guid: ", StringComparison.Ordinal))
                .Substring("guid: ".Length)
                .Trim();
            string headPrefabGuid = File.ReadLines(MeteorHeadPrefabMetaPath)
                .First(line => line.StartsWith("guid: ", StringComparison.Ordinal))
                .Substring("guid: ".Length)
                .Trim();

            string registry = File.ReadAllText(CombatVfxRegistryPath);
            Assert.That(registry, Does.Contain("vfxId: VFX_METEOR_01"));
            Assert.That(registry, Does.Contain($"guid: {prefabGuid}"));
            Assert.That(registry, Does.Contain("vfxId: VFX_METEOR_HEAD_01"));
            Assert.That(registry, Does.Contain($"guid: {headPrefabGuid}"));
        }

        [Test]
        public void LightningVfx_UsesRegistryPrefabTemplate()
        {
            string templateRegistry = File.ReadAllText(CombatVfxTemplateRegistryPath);
            Assert.That(templateRegistry, Does.Not.Contain("VFX_LIGHTNING_01"));
            Assert.That(templateRegistry, Does.Not.Contain("LightningVFX"));

            string prefabGuid = File.ReadLines(LightningPrefabMetaPath)
                .First(line => line.StartsWith("guid: ", StringComparison.Ordinal))
                .Substring("guid: ".Length)
                .Trim();

            string registry = File.ReadAllText(CombatVfxRegistryPath);
            Assert.That(registry, Does.Contain("vfxId: VFX_LIGHTNING_01"));
            Assert.That(registry, Does.Contain($"guid: {prefabGuid}"));
        }

        [Test]
        public void CombatVfxRegistry_HasExplicitCacheDiagnostics()
        {
            string registrySource = File.ReadAllText(CombatVfxRegistrySourcePath);
            Assert.That(registrySource, Does.Contain("public static CombatVFXRegistry? ReloadShared()"));
            Assert.That(registrySource, Does.Contain("public void InvalidateIndex()"));
            Assert.That(registrySource, Does.Contain("private void OnEnable()"));

            string editorSource = File.ReadAllText(CombatVfxRegistryEditorPath);
            Assert.That(editorSource, Does.Contain("[CustomEditor(typeof(CombatVFXRegistry))]"));
            Assert.That(editorSource, Does.Contain("Clear Runtime Cache"));
            Assert.That(editorSource, Does.Contain("Log Resolved VFX Bindings"));
        }

        [Test]
        public void ActionBarSlotIds_UseGridCoordinates()
        {
            string contracts = File.ReadAllText(GameplayContractsPath);
            Assert.That(contracts, Does.Contain("slot_0_0"));
            Assert.That(contracts, Does.Contain("slot_0_7"));
            Assert.That(contracts, Does.Not.Contain("Bottom01"));
            Assert.That(contracts, Does.Not.Contain("BottomRowOrdered"));
        }

        [Test]
        public void FixedActionInputs_UseSharedDispatcher()
        {
            string spellInput = File.ReadAllText(SpellInputHandlerPath);
            Assert.That(spellInput, Does.Not.Contain("ActionBarKeymap.SelectableBindings"));
            Assert.That(spellInput, Does.Contain("FixedActionDispatcher.ProcessMovementBindings(conn, input)"));

            string actionBarDispatcher = File.ReadAllText(ActionBarInputDispatcherPath);
            Assert.That(actionBarDispatcher, Does.Contain("ActionBarKeymap.SelectableBindings"));
            Assert.That(actionBarDispatcher, Does.Contain("FixedActionDispatcher.TryTrigger(action, conn)"));
            Assert.That(actionBarDispatcher, Does.Contain("action.IsFixed"));

            string dispatcher = File.ReadAllText(FixedActionDispatcherPath);
            Assert.That(dispatcher, Does.Contain("ProcessMovementBindings(DbConnection conn, LocalPlayerInputSource input)"));
            Assert.That(dispatcher, Does.Contain("MovementActionKeymap.DodgeKeyCode"));
            Assert.That(dispatcher, Does.Contain("TryTrigger(ActiveActionBarAction action, DbConnection? conn)"));
            Assert.That(dispatcher, Does.Contain("StartDodge("));
            Assert.That(dispatcher, Does.Contain("CreateActionPredictionToken(FixedActionIds.Dodge)"));
            Assert.That(dispatcher, Does.Contain("CreateActionPredictionToken(FixedActionIds.Parry)"));
            Assert.That(dispatcher, Does.Contain("LocalDefensePrediction.PredictParry(nowMs, token)"));
            Assert.That(dispatcher, Does.Contain("token.PredictedActionId"));
            Assert.That(dispatcher, Does.Contain("token.ClientActionSeq"));
            Assert.That(dispatcher, Does.Not.Contain("TryCastCharge("));
            Assert.That(dispatcher, Does.Not.Contain("StartCharge("));
        }

        [Test]
        public void ActionBarDispatch_UsesResolvedAbilityKind()
        {
            string actionBarDispatcher = File.ReadAllText(ActionBarInputDispatcherPath);
            Assert.That(actionBarDispatcher, Does.Contain("ActiveActionBarAction action"));
            Assert.That(actionBarDispatcher, Does.Contain("action.IsMeleeAbility"));
            Assert.That(actionBarDispatcher, Does.Contain("action.IsSpellAbility"));
            Assert.That(actionBarDispatcher, Does.Contain("action.IsMovementAbility"));

            string spellInput = File.ReadAllText(SpellInputHandlerPath);
            Assert.That(spellInput, Does.Not.Contain("TryTriggerActionFromBar(SpacetimeDB.Types.DbConnection conn, string actionId)"));
            Assert.That(spellInput, Does.Not.Contain("ActionBarKeymap.SelectableBindings"));

            string contracts = File.ReadAllText(GameplayContractsPath);
            Assert.That(contracts, Does.Contain("!string.Equals(actionKind, ActionKinds.Ability"));
        }

        [Test]
        public void ActiveActionBarResolver_MapsRuntimeActionsThroughEquipmentResolvedProfile()
        {
            string contracts = File.ReadAllText(GameplayContractsPath);
            Assert.That(contracts, Does.Contain("conn.Db.CharacterActionBarAssignment.Owner.Filter(owner.Value)"));
            Assert.That(contracts, Does.Contain("CombatProfileResolver.ResolveForOwner(conn, owner)"));
            Assert.That(contracts, Does.Contain("ActionBarAssignmentScope.MatchesCombatProfile"));
            Assert.That(contracts, Does.Contain("SpellSlotResolver.IsSpellAssignmentEnabled"));
            Assert.That(contracts, Does.Not.Contain("TryResolveActiveSpec"));
            Assert.That(contracts, Does.Not.Contain("ResolveSelectableActionFromAssignment"));
            Assert.That(contracts, Does.Not.Contain("CharacterClassLoadoutState"));
            Assert.That(contracts, Does.Not.Contain("SavedSpecSlotAssignment"));
            Assert.That(
                contracts,
                Does.Not.Contain("ResolveForClass"),
                "Action bar runtime action resolution must follow equipped gear, not the old class profile.");
            Assert.That(contracts, Does.Not.Contain("ClassCatalog"));
            Assert.That(
                contracts,
                Does.Not.Contain("conn.Db.SavedSpecSlotAssignment.SpecId.Filter(activeSpecId)"),
                "Active runtime action resolution must not depend on saved spec/class spec state.");
        }

        [Test]
        public void CharacterActionBarAbilityEligibility_UsesCombatProfileBoundary()
        {
            string contracts = File.ReadAllText(GameplayContractsPath);
            Assert.That(contracts, Does.Contain("AbilityMatchesOwner"));
            Assert.That(contracts, Does.Contain("ResolveForAbility"));

            string panel = File.ReadAllText(CharacterActionBarPanelPath);
            Assert.That(panel, Does.Contain("CombatProfileResolver.ResolveForOwner(conn, localIdentity.Value)"));
            Assert.That(panel, Does.Contain("CombatProfileResolver.ResolveForAbility(conn, ability)"));
            Assert.That(
                panel,
                Does.Not.Contain("ClassIds"),
                "Available action-bar abilities must be filtered by resolved combat profile rather than class.");
        }

        [Test]
        public void LearnedSpellbook_GatesSpellCatalogAndCasting()
        {
            string spells = File.ReadAllText(ServerSpellsPath);
            Assert.That(spells, Does.Contain("pub struct PlayerKnownSpell"));
            Assert.That(spells, Does.Contain("pub fn learn_spell"));
            Assert.That(spells, Does.Contain("player_knows_spell(ctx, ctx.sender(), kind.as_str())"));

            string progression = File.ReadAllText(ServerProgressionPath);
            Assert.That(progression, Does.Not.Contain("backfill_known_spell_rows_from_saved_specs"));
            Assert.That(progression, Does.Contain("spell ability '{}' requires learned spell '{}'"));
            Assert.That(progression, Does.Contain("require_available_spell_slot_for_assignment"));
            Assert.That(progression, Does.Contain("equipment_spell_slot_capacity_for_owner"));

            string inventory = File.ReadAllText(ServerInventoryPath);
            Assert.That(inventory, Does.Contain("MODIFIER_SPELL_SLOT"));
            Assert.That(inventory, Does.Contain("ARMOR_KIND_CLOTH"));
            Assert.That(inventory, Does.Contain("equipment_spell_slot_capacity_for_owner"));

            string contracts = File.ReadAllText(GameplayContractsPath);
            Assert.That(contracts, Does.Contain("SpellbookResolver"));
            Assert.That(contracts, Does.Contain("PlayerKnownSpell"));
            Assert.That(contracts, Does.Contain("SpellSlotResolver"));
            Assert.That(contracts, Does.Contain("definition.ArmorKind"));

            string spellCatalog = File.ReadAllText(SpellCatalogPanelPath);
            Assert.That(spellCatalog, Does.Contain("RuntimeInitializeOnLoadMethod"));
            Assert.That(spellCatalog, Does.Contain("KeyCode.K"));
            Assert.That(spellCatalog, Does.Contain("conn.Db.SpellDefinition.Iter()"));
            Assert.That(spellCatalog, Does.Contain("conn.Db.PlayerKnownSpell.Owner"));
            Assert.That(spellCatalog, Does.Contain("conn.Reducers.LearnSpell"));
            Assert.That(spellCatalog, Does.Contain("RuntimeUiEscapeRouter.Register"));

            string panel = File.ReadAllText(CharacterActionBarPanelPath);
            Assert.That(panel, Does.Not.Contain("conn.Reducers.LearnSpell"));
        }

        [Test]
        public void SpellCastRequests_UseNextMovementContextTick()
        {
            string spellInput = File.ReadAllText(SpellInputHandlerPath);

            Assert.That(spellInput, Does.Contain("LocalMovementPredictionDriver"));
            Assert.That(spellInput, Does.Contain("NextMovementContextProposalTick"));
            Assert.That(
                spellInput,
                Does.Not.Contain("NewestPendingTick"),
                "Stationary cast requests must not stamp the last pending movement tick; frame-perfect movement-cancel/recast can otherwise leave a later voluntary movement tick that cancels the new cast.");
        }

        [Test]
        public void FixedActions_ResolveFromPresentationCatalog()
        {
            string planner = File.ReadAllText(GameplaySubscriptionPlannerPath);
            Assert.That(planner, Does.Not.Contain("FixedActionBindingCatalog"));

            string panel = File.ReadAllText(CharacterActionBarPanelPath);
            Assert.That(panel, Does.Contain("PresentationKindFixed"));
            Assert.That(panel, Does.Contain("IsActionBarVisible(fixedActionId, conn)"));
            Assert.That(panel, Does.Not.Contain("FixedActionBindingCatalog"));
        }

        [Test]
        public void ThirdRowSelectableSlots_UseShiftNumberBindings()
        {
            string contracts = File.ReadAllText(GameplayContractsPath);
            Assert.That(contracts, Does.Contain("new(\"S+3\", KeyCode.Alpha3, true, ActionBarSlotIds.Slot22"));
            Assert.That(contracts, Does.Contain("new(\"S+9\", KeyCode.Alpha9, true, ActionBarSlotIds.Slot28"));
            Assert.That(contracts, Does.Contain("new(\"0\", KeyCode.Alpha0, false, ActionBarSlotIds.Slot10"));
            Assert.That(contracts, Does.Contain("DodgeKeyLabel = \"Q\""));
            Assert.That(contracts, Does.Contain("DodgeKeyCode = KeyCode.Q"));
            Assert.That(contracts, Does.Not.Contain("new(\"Q\", KeyCode.Q, false"));

            string hud = File.ReadAllText(HudControllerPath);
            Assert.That(hud, Does.Contain("ActionBarKeymap.KeyLabelForCell"));

            string panel = File.ReadAllText(CharacterActionBarPanelPath);
            Assert.That(panel, Does.Contain("ActionBarKeymap.KeyLabelForCell"));
        }

        [Test]
        public void Hud_RendersFixedActionGridSlots()
        {
            string hud = File.ReadAllText(HudControllerPath);
            Assert.That(hud, Does.Contain("ActionBarKeymap.SelectableBindings"));
            Assert.That(hud, Does.Contain("ActionTooltipResolver.ResolveForActionRef"));
            Assert.That(hud, Does.Contain("TooltipTarget"));
            Assert.That(hud, Does.Contain("ActionBarInputDispatcher.TryTrigger"));
            Assert.That(hud, Does.Contain("RenderFixedActionState"));
        }

        [Test]
        public void CharacterActionBarPanel_RendersFixedActionGridSlots()
        {
            string panel = File.ReadAllText(CharacterActionBarPanelPath);
            Assert.That(panel, Does.Contain("ActionBarKeymap.TryGetBindingForCell"));
            Assert.That(panel, Does.Contain("ActionTooltipResolver.ResolveForActionRef"));
            Assert.That(panel, Does.Contain("TooltipTarget"));
            Assert.That(panel, Does.Contain("FixedActionColor"));
            Assert.That(panel, Does.Contain("resolved.IsFixed"));
        }

        [Test]
        public void CharacterActionBarPanel_RendersCatalogBackedActionLibrary()
        {
            string panel = File.ReadAllText(CharacterActionBarPanelPath);
            Assert.That(panel, Does.Contain("\"ActionLibraryPanel\""));
            Assert.That(panel, Does.Contain("\"AvailableActionsRoot\""));
            Assert.That(panel, Does.Contain("HasAbilityTag(ability, ActionBarActionTag)"));
            Assert.That(panel, Does.Contain("ActionTooltipResolver.ResolveForAbility"));

            string dragDrop = File.ReadAllText(ActionBarDragDropPath);
            Assert.That(dragDrop, Does.Contain("conn.Reducers.AssignCharacterActionBarAbilityToSlot"));
            Assert.That(dragDrop, Does.Contain("conn.Reducers.AssignCharacterActionBarSlot"));
        }

        [Test]
        public void ActionBars_UseSharedDragDropForPanelAndHud()
        {
            string dragDrop = File.ReadAllText(ActionBarDragDropPath);
            Assert.That(dragDrop, Does.Contain("ActionBarDragPayload"));
            Assert.That(dragDrop, Does.Contain("ActionBarDropSlot"));
            Assert.That(dragDrop, Does.Contain("ActionBarDropApplier"));
            Assert.That(dragDrop, Does.Contain("FindNearestSlot"));
            Assert.That(dragDrop, Does.Contain("FindCharacterActionBarAssignment"));
            Assert.That(dragDrop, Does.Contain("ActionBarAssignmentScope.MatchesCombatProfile"));
            Assert.That(dragDrop, Does.Contain("From(ActiveActionBarAction action, string sourceSlotId)"));
            Assert.That(dragDrop, Does.Contain("CancelActiveDrag"));
            Assert.That(dragDrop, Does.Contain("ClearCharacterActionBarSlot"));
            Assert.That(dragDrop, Does.Contain("AssignCharacterActionBarAbilityToSlot"));
            Assert.That(dragDrop, Does.Contain("AssignCharacterActionBarSlot"));
            Assert.That(dragDrop, Does.Contain("PayloadIsSpell"));
            Assert.That(dragDrop, Does.Not.Contain("ClearSavedSpecSlot"));
            Assert.That(dragDrop, Does.Not.Contain("AssignSavedSpecAbilityToSlot"));
            Assert.That(dragDrop, Does.Not.Contain("AssignSavedSpecActionToSlot"));

            string panel = File.ReadAllText(CharacterActionBarPanelPath);
            Assert.That(panel, Does.Contain("ActionBarDragSource"));
            Assert.That(panel, Does.Contain("ActionBarDropApplier.ApplyDrop"));

            string hud = File.ReadAllText(HudControllerPath);
            Assert.That(hud, Does.Contain("ActionBarDragSource"));
            Assert.That(hud, Does.Contain("ActionBarDropSlot"));
            Assert.That(hud, Does.Contain("ActionBarDropApplier.ApplyDrop"));
            Assert.That(hud, Does.Not.Contain("if (!ActiveActionBarResolver.TryResolveActiveSpec"));
        }

        [Test]
        public void CharacterActionBarPanel_UsesSharedSlotPresentationAndClasslessAssignments()
        {
            string panel = File.ReadAllText(CharacterActionBarPanelPath);
            Assert.That(panel, Does.Contain("RuntimeInitializeOnLoadMethod"));
            Assert.That(panel, Does.Contain("KeyCode.J"));
            Assert.That(panel, Does.Contain("ActionBarSlotViewFactory.Create"));
            Assert.That(panel, Does.Contain("ActionBarLayout.GridSize"));
            Assert.That(panel, Does.Contain("ActiveActionBarResolver.ResolveActiveSelectableAction"));
            Assert.That(panel, Does.Contain("SpellbookResolver.AbilityIsKnownIfSpell"));
            Assert.That(panel, Does.Contain("SpellSlotResolver.Capacity"));
            Assert.That(panel, Does.Contain("SpellSlotResolver.AssignedSpellCount"));
            Assert.That(panel, Does.Contain("ActionBarAssignmentScope.MatchesCombatProfile"));
            Assert.That(panel, Does.Contain("CanApplyPayloadToSlot"));
            Assert.That(panel, Does.Contain("Spell slots"));
            Assert.That(panel, Does.Contain("ActionBarDropApplier.ApplyDrop(conn,"));
            Assert.That(panel, Does.Contain("RuntimeUiEscapeRouter.Register"));

            string factory = File.ReadAllText(ActionBarSlotViewFactoryPath);
            Assert.That(factory, Does.Contain("Resources.Load<GameObject>(ActionBarLayout.SlotPrefabResourcePath)"));
            Assert.That(factory, Does.Contain("HasPrefabFrame"));
            Assert.That(factory, Does.Not.Contain("new GameObject(\"Frame\""));
        }

        [Test]
        public void CharacterBootstrap_IsClasslessAndGearResolved()
        {
            string progression = File.ReadAllText(ServerProgressionPath);
            Assert.That(progression, Does.Contain("equipment_combat_profile_id_for_owner"));
            Assert.That(progression, Does.Contain("primary_resource_kind_for_owner"));
            Assert.That(progression, Does.Contain("ensure_default_character_action_bar_assignments"));
            Assert.That(progression, Does.Not.Contain("pub struct CharacterProgression"));
            Assert.That(progression, Does.Not.Contain("switch_loadout_class"));
            Assert.That(progression, Does.Not.Contain("CLASSLESS_CHARACTER_ID"));
            Assert.That(progression, Does.Not.Contain("pub struct SavedSpec"));
            Assert.That(progression, Does.Not.Contain("pub fn create_saved_spec"));
            Assert.That(progression, Does.Not.Contain("pub struct CharacterClassLoadoutState"));

            string progressionCatalog = File.ReadAllText("server/src/progression_catalog.shared.json");
            Assert.That(progressionCatalog, Does.Not.Contain("\"class_id\""));
            Assert.That(progressionCatalog, Does.Not.Contain("\"classes\""));
            Assert.That(progressionCatalog, Does.Not.Contain("\"max_saved_specs\""));

            string inventory = File.ReadAllText(ServerInventoryPath);
            Assert.That(inventory, Does.Contain("BASELINE_STARTER_WEAPONS"));
            Assert.That(inventory, Does.Contain("starter_equipment(EQUIP_SLOT_MAIN_HAND, \"TRAINING_TWO_HAND_SWORD\")"));
            Assert.That(inventory, Does.Contain("\"NEWBIE_TWO_HAND_SWORD_01\""));
            Assert.That(inventory, Does.Contain("\"NEWBIE_ONE_HAND_SWORD_01\""));
            Assert.That(inventory, Does.Contain("\"NEWBIE_TWO_HAND_AXE_01\""));
            Assert.That(inventory, Does.Contain("\"NEWBIE_ONE_HAND_AXE_02\""));
            Assert.That(inventory, Does.Not.Contain("\"NEWBIE_ONE_HAND_AXE_01\""));
            Assert.That(inventory, Does.Contain("\"NEWBIE_DAGGER_PAIR_01\""));
            Assert.That(inventory, Does.Contain("\"NEWBIE_STAFF_01\""));
            Assert.That(inventory, Does.Contain("\"NEWBIE_SHIELD_01\""));
            Assert.That(inventory, Does.Contain("\"NEWBIE_BOW_01\""));
            Assert.That(inventory, Does.Not.Contain("starter_equipment(EQUIP_SLOT_OFF_HAND"));
            Assert.That(inventory, Does.Not.Contain("starter_weapon_equipment_for_class"));
            Assert.That(inventory, Does.Not.Contain("starter_armor_equipment_for_class"));

            string appearance = File.ReadAllText(ServerAppearancePath);
            Assert.That(appearance, Does.Contain("DEFAULT_STARTER_OUTFIT_ID"));
            Assert.That(appearance, Does.Contain("HUMAN_MALE_PEASANT_STARTER"));
            Assert.That(appearance, Does.Not.Contain("HUMAN_MALE_WARRIOR_STARTER"));
            Assert.That(appearance, Does.Not.Contain("switch_loadout_class(ctx"));
            Assert.That(appearance, Does.Not.Contain("default_outfit_id_for_class"));

            string player = File.ReadAllText(ServerPlayerPath);
            Assert.That(player, Does.Not.Contain("class_id"));

            string creation = File.ReadAllText(CharacterCreationControllerPath);
            Assert.That(creation, Does.Contain("TryApplyStarterDefault"));
            Assert.That(creation, Does.Not.Contain("DefaultPreviewClassId"));
            Assert.That(creation, Does.Contain("_warriorButton.interactable = false"));
            Assert.That(creation, Does.Not.Contain("conn.Reducers.CreateOrUpdateCharacter(\n                _selectedClassId"));

            string tooltip = File.ReadAllText(ActionTooltipResolverPath);
            Assert.That(tooltip, Does.Contain("CombatProfileResolver.ResolveForOwner"));
            Assert.That(tooltip, Does.Not.Contain("ClassCatalog"));

            string entityRegistry = File.ReadAllText(EntityRegistryPath);
            Assert.That(entityRegistry, Does.Contain("WeaponVisualRoleIdsForKind"));
            Assert.That(entityRegistry, Does.Not.Contain("TryAddWeaponVisualIdsForItemDefinition"));

            string catalogBuilder = File.ReadAllText("Assets/Arena/Editor/CharacterAppearanceCatalogBuilder.cs");
            Assert.That(catalogBuilder, Does.Contain("BuildWeaponVisualEntries"));
            Assert.That(catalogBuilder, Does.Contain("NEWBIE_TWO_HAND_SWORD_01"));
            Assert.That(catalogBuilder, Does.Contain("NEWBIE_ONE_HAND_SWORD_01"));
            Assert.That(catalogBuilder, Does.Contain("NEWBIE_TWO_HAND_AXE_01"));
            Assert.That(catalogBuilder, Does.Contain("NEWBIE_ONE_HAND_AXE_02"));
            Assert.That(catalogBuilder, Does.Contain("NEWBIE_DAGGER_PAIR_01"));
            Assert.That(catalogBuilder, Does.Contain("dagger_main"));
            Assert.That(catalogBuilder, Does.Contain("dagger_off"));
            Assert.That(catalogBuilder, Does.Contain("NEWBIE_STAFF_01"));
            Assert.That(catalogBuilder, Does.Contain("NEWBIE_SHIELD_01"));
            Assert.That(catalogBuilder, Does.Contain("NEWBIE_BOW_01"));

            string daggersAnimationSet = File.ReadAllText("Assets/Arena/Resources/CombatAnimationSets/Daggers.asset");
            Assert.That(daggersAnimationSet, Does.Contain("combatProfileId: DAGGERS"));
            string staffAnimationSet = File.ReadAllText("Assets/Arena/Resources/CombatAnimationSets/Staff.asset");
            Assert.That(staffAnimationSet, Does.Contain("combatProfileId: STAFF"));

            string hub = File.ReadAllText(HubControllerPath);
            Assert.That(hub, Does.Contain("bool showStage = activeHub && HubViewState.Current == HubViewScreen.Play"));
            Assert.That(hub, Does.Contain("if (showStage)"));
        }

        [Test]
        public void LegacyClassAndSpecUi_IsRetiredFromClientSurface()
        {
            Assert.That(File.Exists("Assets/Arena/Runtime/UI/LoadoutController.cs"), Is.False);
            Assert.That(File.Exists("Assets/Arena/Runtime/UI/LoadoutController.cs.meta"), Is.False);

            string project = File.ReadAllText("Assembly-CSharp.csproj");
            Assert.That(project, Does.Not.Contain("Assets/Arena/Runtime/UI/LoadoutController.cs"));

            string hub = File.ReadAllText(HubControllerPath);
            Assert.That(hub, Does.Contain("_equipmentButton.gameObject.SetActive(false)"));
            Assert.That(hub, Does.Not.Contain("SavedSpec"));
            Assert.That(hub, Does.Not.Contain("LoadoutController.Instance"));
            Assert.That(hub, Does.Not.Contain("HubViewScreen.Loadout"));

            string planner = File.ReadAllText(GameplaySubscriptionPlannerPath);
            Assert.That(planner, Does.Not.Contain("CharacterClassLoadoutState"));
            Assert.That(planner, Does.Not.Contain("SavedSpec"));
            Assert.That(planner, Does.Not.Contain("SavedSpecSlotAssignment"));
            Assert.That(planner, Does.Not.Contain("SavedSpecStatAllocation"));
        }

        [Test]
        public void ActionBarSlots_UseSharedPrefabFrame()
        {
            Assert.That(File.Exists(ActionBarSlotPrefabAssetPath), Is.True);
            Assert.That(File.Exists(ActionBarSlotTextureAssetPath), Is.True);
            byte[] textureBytes = File.ReadAllBytes(ActionBarSlotTextureAssetPath);
            Assert.That(textureBytes[25], Is.EqualTo(6), "slot.png must be imported from an RGBA PNG so the frame background stays transparent.");

            string prefab = File.ReadAllText(ActionBarSlotPrefabAssetPath);
            Assert.That(prefab, Does.Contain("m_Name: Frame"));
            Assert.That(prefab, Does.Contain("m_SizeDelta: {x: 68, y: 68}"));
            Assert.That(prefab, Does.Contain("m_Sprite: {fileID: 21300000"));

            string layout = File.ReadAllText(ActionBarLayoutPath);
            Assert.That(layout, Does.Contain("public const float SlotSize = 68f"));
            Assert.That(layout, Does.Contain("public const float Gap = 4f"));
            Assert.That(layout, Does.Contain("public const string SlotPrefabResourcePath"));
            Assert.That(layout, Does.Contain("public static Vector2 CellPosition"));
            Assert.That(layout, Does.Contain("public static Vector2 ActionCellPosition"));
            Assert.That(layout, Does.Contain("public static Vector2 SpellbookCellPosition"));
            Assert.That(layout, Does.Contain("public static Vector2 CenteredOffset"));

            string hud = File.ReadAllText(HudControllerPath);
            Assert.That(hud, Does.Contain("ActionBarLayout.GridSize"));
            Assert.That(hud, Does.Contain("Resources.Load<GameObject>(ActionBarLayout.SlotPrefabResourcePath)"));

            string panel = File.ReadAllText(CharacterActionBarPanelPath);
            Assert.That(panel, Does.Contain("ActionBarLayout.CenteredOffset"));
            Assert.That(panel, Does.Contain("ActionBarLayout.ActionCellPosition"));
            Assert.That(panel, Does.Contain("ActionBarLayout.SpellbookCellPosition"));
            Assert.That(panel, Does.Contain("ActionBarSlotViewFactory.Create"));
        }

        [Test]
        public void HudUnitFrames_UseSharedMirroredUnitFrameSprite()
        {
            Assert.That(File.Exists(UnitFrameTextureAssetPath), Is.True);
            byte[] textureBytes = File.ReadAllBytes(UnitFrameTextureAssetPath);
            Assert.That(textureBytes[25], Is.EqualTo(6), "UnitFrame.png must be RGBA so the background remains transparent.");

            string hud = File.ReadAllText(HudControllerPath);
            Assert.That(hud, Does.Contain("UnitFrameSpritePath = \"UI/UnitFrame/UnitFrame\""));
            Assert.That(hud, Does.Contain("UnitFrameHealthShape"));
            Assert.That(hud, Does.Contain("UnitFrameResourceShape"));
            Assert.That(hud, Does.Contain("AddUnitFrameArt(frame.transform, mirrored: false)"));
            Assert.That(hud, Does.Contain("AddUnitFrameArt(_targetRoot.transform, mirrored: true)"));
            Assert.That(hud, Does.Contain("BuildUnitFrameBarFill"));
            Assert.That(hud, Does.Contain("UnitFrameBarSprite"));
            Assert.That(hud, Does.Contain("IsInsidePolygon"));
            Assert.That(hud, Does.Contain("Image.Type.Filled"));
            Assert.That(hud, Does.Contain("art.transform.SetAsFirstSibling()"));
            Assert.That(hud, Does.Contain("go.transform.SetAsLastSibling()"));
            Assert.That(hud, Does.Contain("MirroredUnitFrameShape"));
            Assert.That(hud, Does.Contain("SetUnitFrameBarFill"));
            Assert.That(hud, Does.Contain("fillFromRight: true"));
            Assert.That(hud, Does.Contain("_targetPrimaryFill"));
        }

        [Test]
        public void BuffAndDebuffIcons_UseSharedTooltipPresenter()
        {
            string hud = File.ReadAllText(HudControllerPath);
            Assert.That(hud, Does.Contain("StatusTooltipResolver.Resolve"));
            Assert.That(hud, Does.Contain("TooltipTarget"));
            Assert.That(hud, Does.Contain("IsBuffPolarity"));

            string tooltip = File.ReadAllText(TooltipPath);
            Assert.That(tooltip, Does.Contain("public sealed class TooltipTarget"));
            Assert.That(tooltip, Does.Contain("public static class TooltipPresenter"));
            Assert.That(tooltip, Does.Not.Contain("ActionTooltipPresenter"));
            Assert.That(tooltip, Does.Not.Contain("StatusTooltipPresenter"));
        }

        private static IEnumerable<string> FindFilesContaining(IEnumerable<string> paths, string needle)
        {
            foreach (string path in paths)
            {
                string source = File.ReadAllText(path);
                if (source.Contains(needle, StringComparison.Ordinal))
                    yield return path;
            }
        }

        private static string NormalizePath(string path)
        {
            return path.Replace('\\', '/');
        }
    }
}
