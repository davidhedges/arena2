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
        private const string MeleeInputHandlerPath = "Assets/Arena/Runtime/Input/MeleeInputHandler.cs";
        private const string GameplayContractsPath = "Assets/Arena/Runtime/Combat/GameplayContracts.cs";
        private const string GameplaySubscriptionPlannerPath = "Assets/Arena/Runtime/Network/GameplaySubscriptionPlanner.cs";
        private const string FixedActionDispatcherPath = "Assets/Arena/Runtime/Input/FixedActionDispatcher.cs";
        private const string LocalPlayerInputSourcePath = "Assets/Arena/Runtime/Input/LocalPlayerInputSource.cs";
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
        private const string NetworkCallbackBinderPath = "Assets/Arena/Runtime/Network/NetworkCallbackBinder.cs";
        private const string PlayerEntityPath = "Assets/Arena/Runtime/Entity/PlayerEntity.cs";
        private const string CombatAnimationSetBinderPath = "Assets/Arena/Runtime/Presentation/Animation/CombatAnimationSetBinder.cs";

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
        public void SpellInputCode_AllowsAuthoredSpellResources()
        {
            string source = File.ReadAllText(SpellInputHandlerPath);

            Assert.That(source, Does.Contain("EffectiveCurrentResource(entity, requiredKind)"));
            Assert.That(source, Does.Not.Contain("spell rejected: {spellId} requires {requiredKind}, active resource is {entity.PrimaryResourceKind}"));
        }

        [Test]
        public void MeleeInputCode_AllowsAuthoredActionResources()
        {
            string source = File.ReadAllText(MeleeInputHandlerPath);

            Assert.That(source, Does.Contain("EffectiveCurrentResource(entity, requiredKind)"));
            Assert.That(source, Does.Not.Contain("melee rejected: {actionId} requires {requiredKind}, active resource is {entity.PrimaryResourceKind}"));
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
            Assert.That(panel, Does.Contain("CombatProfileResolver.ResolveForOwner(conn, owner.Value)"));
            Assert.That(panel, Does.Contain("CombatProfileResolver.ResolveForAbility(conn, ability)"));
            Assert.That(
                panel,
                Does.Not.Contain("ClassIds"),
                "Available action-bar abilities must be filtered by resolved combat profile rather than class.");
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
        public void SpellbookSlots_HaveRealSharedKeybinds()
        {
            string contracts = File.ReadAllText(GameplayContractsPath);
            Assert.That(contracts, Does.Contain("public static class SpellbookKeymap"));
            Assert.That(contracts, Does.Contain("new(\"S+0\", KeyCode.Alpha0, true, 0)"));
            Assert.That(contracts, Does.Contain("new(\"S+G\", KeyCode.G, true, 5)"));
            Assert.That(contracts, Does.Contain("new(\"S+C\", KeyCode.C, true, 8)"));
            Assert.That(contracts, Does.Contain("ResolveEquippedSpellbookAction"));
            Assert.That(contracts, Does.Contain("ItemSpell.ItemInstanceId.Filter(loadout.SpellbookItemId)"));
            Assert.That(contracts, Does.Contain("itemSpell.SlotIndex != slotIndex"));

            string actionBarDispatcher = File.ReadAllText(ActionBarInputDispatcherPath);
            Assert.That(actionBarDispatcher, Does.Contain("SpellbookKeymap.SelectableBindings"));
            Assert.That(actionBarDispatcher, Does.Contain("ActiveActionBarResolver.ResolveEquippedSpellbookAction"));
            Assert.That(actionBarDispatcher, Does.Contain("ReleaseCastRequest("));

            string hud = File.ReadAllText(HudControllerPath);
            Assert.That(hud, Does.Contain("SpellbookKeymap.KeyLabelForIndex"));
            Assert.That(hud, Does.Contain("FindSpellbookSlot(spells, col)"));
            Assert.That(hud, Does.Not.Contain("$\"Spellbook_{col + 1}\""));

            string panel = File.ReadAllText(CharacterActionBarPanelPath);
            Assert.That(panel, Does.Contain("SpellbookKeymap.KeyLabelForIndex"));
            Assert.That(panel, Does.Contain("FindSpellbookSlot(spells, col)"));

            string inputSource = File.ReadAllText(LocalPlayerInputSourcePath);
            Assert.That(inputSource, Does.Contain("case KeyCode.G:"));
            Assert.That(inputSource, Does.Contain("button = keyboard.gKey;"));
        }

        [Test]
        public void AuthoredHoldChannels_ForwardZeroCastTimeActiveCasts()
        {
            string registry = File.ReadAllText(EntityRegistryPath);
            Assert.That(registry, Does.Contain("ShouldForwardSpellActiveCast"));
            Assert.That(registry, Does.Contain("castTimeMs > 0UL || entity.UsesSpellCastHoldPresentation(spellActionId)"));
            Assert.That(registry, Does.Contain("entity.OnActiveCastInsert(row, castTimeMs)"));
            Assert.That(registry, Does.Contain("entity.OnActiveCastUpdate(newRow, castTimeMs)"));
            Assert.That(
                registry,
                Does.Not.Contain("castTimeMs > 0UL\n                && TryGetLivePlayer"),
                "Zero-cast-time channel spells with authored hold presentation must reach SpellCastPresentationController.");
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
        public void CharacterActionBarPanel_RendersCatalogBackedActionLibrary()
        {
            string panel = File.ReadAllText(CharacterActionBarPanelPath);
            Assert.That(panel, Does.Contain("\"CharacterActionBarRoot\""));
            Assert.That(panel, Does.Contain("\"AvailableActions\""));
            Assert.That(panel, Does.Contain("HasAbilityTag(ability, ActionBarActionTag)"));
            Assert.That(panel, Does.Contain("ActionTooltipResolver.ResolveForAbility"));
            Assert.That(panel, Does.Contain("SpellsFilterKey"));
            Assert.That(panel, Does.Contain("AbilityIsKnownIfSpell"));
            Assert.That(panel, Does.Contain("new AbilityCategory(SpellsFilterKey, \"Spells\""));
            Assert.That(panel, Does.Contain("string.Equals(action.CategoryKey, SpellsFilterKey"));

            string dragDrop = File.ReadAllText(ActionBarDragDropPath);
            Assert.That(dragDrop, Does.Contain("conn.Reducers.AssignCharacterActionBarAbilityToSlot"));
            Assert.That(dragDrop, Does.Contain("conn.Reducers.AssignCharacterActionBarSlot"));
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
            Assert.That(panel, Does.Contain("SpellbookDropSlotPrefix"));
            Assert.That(panel, Does.Contain("TryHandleSpellbookDrop"));
            Assert.That(panel, Does.Contain("conn.Reducers.AssignEquippedSpellbookSpell"));
            Assert.That(panel, Does.Contain("RuntimeUiEscapeRouter.Register"));

            string factory = File.ReadAllText(ActionBarSlotViewFactoryPath);
            Assert.That(factory, Does.Contain("Resources.Load<GameObject>(ActionBarLayout.SlotPrefabResourcePath)"));
            Assert.That(factory, Does.Contain("HasPrefabFrame"));
            Assert.That(factory, Does.Not.Contain("new GameObject(\"Frame\""));
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
