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
        private const string FixedActionDispatcherPath = "Assets/Arena/Runtime/Input/FixedActionDispatcher.cs";
        private const string HudControllerPath = "Assets/Arena/Runtime/UI/HUDController.cs";
        private const string HubControllerPath = "Assets/Arena/Runtime/UI/HubController.cs";
        private const string LoadoutControllerPath = "Assets/Arena/Runtime/UI/LoadoutController.cs";
        private const string LoadoutActionDragDropPath = "Assets/Arena/Runtime/UI/LoadoutActionDragDrop.cs";
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

            string actionBarDispatcher = File.ReadAllText(ActionBarInputDispatcherPath);
            Assert.That(actionBarDispatcher, Does.Contain("ActionBarKeymap.SelectableBindings"));
            Assert.That(actionBarDispatcher, Does.Contain("FixedActionDispatcher.TryTrigger(action, conn)"));
            Assert.That(actionBarDispatcher, Does.Contain("action.IsFixed"));

            string dispatcher = File.ReadAllText(FixedActionDispatcherPath);
            Assert.That(dispatcher, Does.Contain("TryTrigger(ActiveSelectableLoadoutAction action, DbConnection? conn)"));
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
            Assert.That(actionBarDispatcher, Does.Contain("ActiveSelectableLoadoutAction action"));
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
        public void FixedActionBindings_AreSubscribedForClientResolution()
        {
            string planner = File.ReadAllText("Assets/Arena/Runtime/Network/GameplaySubscriptionPlanner.cs");
            Assert.That(planner, Does.Contain("FixedActionBindingCatalog"));
        }

        [Test]
        public void ThirdRowSelectableSlots_UseShiftNumberBindings()
        {
            string contracts = File.ReadAllText(GameplayContractsPath);
            Assert.That(contracts, Does.Contain("new(\"S+3\", KeyCode.Alpha3, true, SelectableLoadoutSlotIds.Slot22"));
            Assert.That(contracts, Does.Contain("new(\"S+9\", KeyCode.Alpha9, true, SelectableLoadoutSlotIds.Slot28"));

            string hud = File.ReadAllText(HudControllerPath);
            Assert.That(hud, Does.Contain("ActionBarKeymap.KeyLabelForCell"));

            string loadout = File.ReadAllText(LoadoutControllerPath);
            Assert.That(loadout, Does.Contain("ActionBarKeymap.KeyLabelForCell"));
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
        public void LoadoutScreen_RendersFixedActionGridSlots()
        {
            string loadout = File.ReadAllText(LoadoutControllerPath);
            Assert.That(loadout, Does.Contain("ActionBarKeymap.TryGetBindingForCell"));
            Assert.That(loadout, Does.Contain("ActionTooltipResolver.ResolveForActionRef"));
            Assert.That(loadout, Does.Contain("TooltipTarget"));
            Assert.That(loadout, Does.Contain("FixedActionColor"));
            Assert.That(loadout, Does.Contain("resolved.IsFixed"));
        }

        [Test]
        public void LoadoutScreen_RendersCatalogBackedActionLibrary()
        {
            string loadout = File.ReadAllText(LoadoutControllerPath);
            Assert.That(loadout, Does.Contain("\"ActionLibraryPanel\""));
            Assert.That(loadout, Does.Contain("\"AvailableActionsRoot\""));
            Assert.That(loadout, Does.Contain("HasAbilityTag(ability, LoadoutActionTag)"));
            Assert.That(loadout, Does.Contain("ActionTooltipResolver.ResolveForAbility"));
            Assert.That(loadout, Does.Contain("conn.Reducers.AssignSavedSpecAbilityToSlot"));
            Assert.That(loadout, Does.Contain("conn.Reducers.AssignSavedSpecActionToSlot"));
        }

        [Test]
        public void ActionBars_UseSharedDragDropForLoadoutAndHud()
        {
            string dragDrop = File.ReadAllText(LoadoutActionDragDropPath);
            Assert.That(dragDrop, Does.Contain("LoadoutActionDragPayload"));
            Assert.That(dragDrop, Does.Contain("LoadoutActionDropSlot"));
            Assert.That(dragDrop, Does.Contain("LoadoutActionDropApplier"));
            Assert.That(dragDrop, Does.Contain("FindNearestSlot"));
            Assert.That(dragDrop, Does.Contain("From(ActiveSelectableLoadoutAction action, string sourceSlotId)"));
            Assert.That(dragDrop, Does.Contain("CancelActiveDrag"));
            Assert.That(dragDrop, Does.Contain("ClearSavedSpecSlot"));
            Assert.That(dragDrop, Does.Contain("AssignSavedSpecAbilityToSlot"));
            Assert.That(dragDrop, Does.Contain("AssignSavedSpecActionToSlot"));

            string loadout = File.ReadAllText(LoadoutControllerPath);
            Assert.That(loadout, Does.Contain("LoadoutActionDragSource"));
            Assert.That(loadout, Does.Contain("LoadoutActionDropApplier.ApplyDrop"));

            string hud = File.ReadAllText(HudControllerPath);
            Assert.That(hud, Does.Contain("LoadoutActionDragSource"));
            Assert.That(hud, Does.Contain("LoadoutActionDropSlot"));
            Assert.That(hud, Does.Contain("LoadoutActionDropApplier.ApplyDrop"));
            Assert.That(hud, Does.Contain("ActiveLoadoutResolver.TryResolveActiveSpec"));
        }

        [Test]
        public void LoadoutScreen_UsesClassMenuAndRightSideStats()
        {
            string loadout = File.ReadAllText(LoadoutControllerPath);
            Assert.That(loadout, Does.Contain("\"ClassMenuPanel\""));
            Assert.That(loadout, Does.Contain("\"ClassMenuRoot\""));
            Assert.That(loadout, Does.Contain("RebuildClassMenu"));
            Assert.That(loadout, Does.Contain("conn.Reducers.SwitchLoadoutClass(targetClassId)"));
            Assert.That(loadout, Does.Contain("CreatePanel(\"StatAllocationPanel\", _root.transform, new Vector2(-28f, 108f)"));
            Assert.That(loadout, Does.Not.Contain("ProfessionNames"));
            Assert.That(loadout, Does.Not.Contain("\"ProfessionPanel\""));
            Assert.That(loadout, Does.Not.Contain("RebuildProfessionRows"));

            string hub = File.ReadAllText(HubControllerPath);
            Assert.That(hub, Does.Contain("bool showStage = activeHub && HubViewState.Current == HubViewScreen.Play"));
            Assert.That(hub, Does.Contain("if (showStage)"));
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
            Assert.That(layout, Does.Contain("public static Vector2 CenteredOffset"));

            string hud = File.ReadAllText(HudControllerPath);
            Assert.That(hud, Does.Contain("ActionBarLayout.GridSize"));
            Assert.That(hud, Does.Contain("Resources.Load<GameObject>(ActionBarLayout.SlotPrefabResourcePath)"));

            string loadout = File.ReadAllText(LoadoutControllerPath);
            Assert.That(loadout, Does.Contain("ActionBarLayout.CenteredOffset"));
            Assert.That(loadout, Does.Contain("ActionBarLayout.CellPosition"));
            Assert.That(loadout, Does.Contain("Resources.Load<GameObject>(ActionBarLayout.SlotPrefabResourcePath)"));
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
