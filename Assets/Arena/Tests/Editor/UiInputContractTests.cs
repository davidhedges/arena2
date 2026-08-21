#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Arena.Tests.Editor
{
    public sealed class UiInputContractTests
    {
        private const string RuntimeUiEventSystemPath = "Assets/Arena/Runtime/UI/RuntimeUiEventSystem.cs";
        private const string RuntimeUiEscapeRouterPath = "Assets/Arena/Runtime/UI/RuntimeUiEscapeRouter.cs";
        private const string ActionBarInputDispatcherPath = "Assets/Arena/Runtime/Input/ActionBarInputDispatcher.cs";
        private const string ActionBarTracePath = "Assets/Arena/Runtime/Debug/ActionBarTrace.cs";
        private const string SpellInputHandlerPath = "Assets/Arena/Runtime/Input/SpellInputHandler.cs";
        private const string MeleeInputHandlerPath = "Assets/Arena/Runtime/Input/MeleeInputHandler.cs";
        private const string GameplayContractsPath = "Assets/Arena/Runtime/Combat/GameplayContracts.cs";
        private const string GameplaySubscriptionPlannerPath = "Assets/Arena/Runtime/Network/GameplaySubscriptionPlanner.cs";
        private const string FixedActionDispatcherPath = "Assets/Arena/Runtime/Input/FixedActionDispatcher.cs";
        private const string LocalPlayerInputSourcePath = "Assets/Arena/Runtime/Input/LocalPlayerInputSource.cs";
        private const string HudControllerPath = "Assets/Arena/Runtime/UI/HUDController.cs";
        private const string StatusTooltipResolverPath = "Assets/Arena/Runtime/Combat/StatusTooltipResolver.cs";
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
        private const string BlizzardVfxPrefabPath = "Assets/Arena/Resources/CombatVFX/playground/Icicle_Rain 1.prefab";
        private const string CombatVfxRegistrySourcePath = "Assets/Arena/Runtime/Presentation/VFX/CombatVFXRegistry.cs";
        private const string CombatPresentationWarmupPath = "Assets/Arena/Runtime/Presentation/CombatPresentationWarmup.cs";
        private const string CombatProfileIdsPath = "Assets/Arena/Runtime/Presentation/Animation/CombatProfileIds.cs";
        private const string CombatVfxRegistryEditorPath = "Assets/Arena/Editor/CombatVFXRegistryEditor.cs";
        private const string CombatVfxTemplateRegistryPath = "Assets/Arena/Runtime/Presentation/CombatVFXTemplateRegistry.cs";
        private const string NegateVfxPath = "Assets/Arena/Runtime/Presentation/VFX/NegateVFX.cs";
        private const string BeamVfxPath = "Assets/Arena/Runtime/Presentation/VFX/BeamVFX.cs";
        private const string FrostNovaPrefabMetaPath = "Assets/Arena/Resources/CombatVFX/Area/VFX_DebuffAoE02_Ice_Arena.prefab.meta";
        private const string NovaCastPrefabMetaPath = "Assets/Arena/Resources/CombatVFX/playground/Arcane Explosion.prefab.meta";
        private const string NovaHitPrefabMetaPath = "Assets/ThirdParty/AssetStore/VFX/Piloto Studio/Super Realistic FX Bundle/ARPG Realistic Essentials Fire/Prefabs/Melee/Green_Fire/Hit_Nova_Light_green.prefab.meta";
        private const string BuffetHitPrefabMetaPath = "Assets/Arena/Resources/CombatVFX/Hits/Air/1) Wind Blast 1.prefab.meta";
        private const string PrimalFourElementsForwardPrefabMetaPath = "Assets/Arena/Resources/CombatVFX/playground/Realistic Druid 1/ARPG_Druid_Four_Elements_Forward.prefab.meta";
        private const string VerdantSpiritsPrefabMetaPath = "Assets/Arena/Resources/CombatVFX/playground/Realistic Druid 1/ARPG_Druid_Nature_Spirits.prefab.meta";
        private const string VerdantSpiritsVfxPath = "Assets/Arena/Runtime/Presentation/VFX/VerdantSpiritsVFX.cs";
        private const string LingeringShadeReturnPrefabMetaPath = "Assets/Arena/Resources/CombatVFX/playground/Realistic Ink Spells 1/shadow_in.prefab.meta";
        private const string LightningPrefabMetaPath = "Assets/Arena/Resources/CombatVFX/Area/Electric/8) Vertical Lightning blue 1.prefab.meta";
        private const string NegateArcaneShockPrefabMetaPath = "Assets/Arena/Resources/CombatVFX/playground/ARPG Realistic Arcane 1/Simple/ARPG_Arcane_Shock_Calling.prefab.meta";
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
        private const string ConnectionStatusHudPath = "Assets/Arena/Runtime/UI/ConnectionStatusHud.cs";
        private const string NhAvatarPath = "Assets/ThirdParty/AssetStore/Characters/StylizedCharacter/Scripts/NHAvatar.cs";

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
        public void PlayModeBootstrap_AvoidsRemovedSkinSpriteAndMissingAvatarControllerErrors()
        {
            string connectionHud = File.ReadAllText(ConnectionStatusHudPath);
            Assert.That(connectionHud, Does.Not.Contain("UI/Skin/Knob.psd\");"));
            Assert.That(connectionHud, Does.Contain("_dot.sprite = null;"));

            string avatar = File.ReadAllText(NhAvatarPath);
            Assert.That(
                avatar,
                Does.Contain("_animator == null || _animator.runtimeAnimatorController == null"));
            Assert.That(avatar, Does.Contain("_animationClips = new List<AnimationClip>();"));
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
        public void MeleeInputCode_GapCloseMaximumRangeDenialUsesExistingFeedbackPath()
        {
            string source = File.ReadAllText(MeleeInputHandlerPath);

            Assert.That(source, Does.Contain("NotifyGapCloseMaximumRangeDenial(gapClose, slotId, pressedActionId);"));
            Assert.That(source, Does.Contain("LocalCombatState.NotifyLocalAdvisoryDenial("));
            Assert.That(source, Does.Contain("ActionRejectReason.OutOfRange"));
        }

        [Test]
        public void HudActionBar_GapCloseRangeGrayOutStaysAdvisory()
        {
            string source = File.ReadAllText(HudControllerPath);

            Assert.That(source, Does.Contain("IsGapCloseRangeBlocked("));
            Assert.That(source, Does.Contain("melee.MinimumRange"));
            Assert.That(source, Does.Contain("MeleeStrikeGeometry.PassesRangeGate("));
            Assert.That(source, Does.Contain("isVisible ? () => TriggerActionRef(conn, resolved) : null"));
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
        public void NegateVfx_UsesAuthoredArcaneShockPrefab()
        {
            string templateRegistry = File.ReadAllText(CombatVfxTemplateRegistryPath);
            Assert.That(templateRegistry, Does.Not.Contain("VFX_NEGATE_ARCANE_SHOCK_01"));

            string prefabGuid = File.ReadLines(NegateArcaneShockPrefabMetaPath)
                .First(line => line.StartsWith("guid: ", StringComparison.Ordinal))
                .Substring("guid: ".Length)
                .Trim();

            string registry = File.ReadAllText(CombatVfxRegistryPath);
            Assert.That(registry, Does.Contain("vfxId: VFX_NEGATE_ARCANE_SHOCK_01"));
            Assert.That(registry, Does.Contain($"guid: {prefabGuid}"));
        }

        [Test]
        public void NovaVfx_UsesAuthoredCastAndTargetHitPrefabs()
        {
            string castPrefabGuid = File.ReadLines(NovaCastPrefabMetaPath)
                .First(line => line.StartsWith("guid: ", StringComparison.Ordinal))
                .Substring("guid: ".Length)
                .Trim();
            string hitPrefabGuid = File.ReadLines(NovaHitPrefabMetaPath)
                .First(line => line.StartsWith("guid: ", StringComparison.Ordinal))
                .Substring("guid: ".Length)
                .Trim();

            string registry = File.ReadAllText(CombatVfxRegistryPath);
            Assert.That(registry, Does.Contain("vfxId: VFX_NOVA_CAST_01"));
            Assert.That(registry, Does.Contain($"guid: {castPrefabGuid}"));
            Assert.That(registry, Does.Contain("vfxId: VFX_NOVA_HIT_01"));
            Assert.That(registry, Does.Contain($"guid: {hitPrefabGuid}"));
        }

        [Test]
        public void BuffetVfx_UsesAuthoredAirHitPrefab()
        {
            string hitPrefabGuid = File.ReadLines(BuffetHitPrefabMetaPath)
                .First(line => line.StartsWith("guid: ", StringComparison.Ordinal))
                .Substring("guid: ".Length)
                .Trim();

            string registry = File.ReadAllText(CombatVfxRegistryPath);
            Assert.That(registry, Does.Contain("vfxId: VFX_BUFFET_IMPACT_01"));
            Assert.That(registry, Does.Contain($"guid: {hitPrefabGuid}"));
        }

        [Test]
        public void PrimalBlastVfx_UsesRequestedFourElementsForwardPrefab()
        {
            string prefabGuid = File.ReadLines(PrimalFourElementsForwardPrefabMetaPath)
                .First(line => line.StartsWith("guid: ", StringComparison.Ordinal))
                .Substring("guid: ".Length)
                .Trim();

            string registry = File.ReadAllText(CombatVfxRegistryPath);
            Assert.That(registry, Does.Contain("vfxId: VFX_PRIMAL_FOUR_ELEMENTS_FORWARD_01"));
            Assert.That(registry, Does.Contain($"guid: {prefabGuid}"));
        }

        [Test]
        public void VerdantSpiritsVfx_UsesRequestedNatureSpiritsPrefabAndStackDrivenBursts()
        {
            string prefabGuid = File.ReadLines(VerdantSpiritsPrefabMetaPath)
                .First(line => line.StartsWith("guid: ", StringComparison.Ordinal))
                .Substring("guid: ".Length)
                .Trim();

            string registry = File.ReadAllText(CombatVfxRegistryPath);
            Assert.That(registry, Does.Contain("vfxId: VFX_VERDANT_SPIRITS_ACTIVE_01"));
            Assert.That(registry, Does.Contain($"guid: {prefabGuid}"));

            string templates = File.ReadAllText(CombatVfxTemplateRegistryPath);
            Assert.That(templates, Does.Contain("VfxVerdantSpiritsActive"));
            Assert.That(templates, Does.Contain("new VerdantSpiritsVFX(context)"));

            string visual = File.ReadAllText(VerdantSpiritsVfxPath);
            Assert.That(visual, Does.Contain("context.SequenceCount"));
            Assert.That(visual, Does.Contain("main.loop = true"));
            Assert.That(visual, Does.Contain("main.startLifetime.constantMax"));
            Assert.That(visual, Does.Contain("Mathf.Approximately(count.constant, MaxSpirits)"));
            Assert.That(visual, Does.Contain("emission.SetBursts(bursts)"));
            Assert.That(visual, Does.Contain("ParticleSystemStopBehavior.StopEmittingAndClear"));
        }

        [Test]
        public void LingeringShadeReturnVfx_UsesAuthoredShadowInPrefab()
        {
            string prefabGuid = File.ReadLines(LingeringShadeReturnPrefabMetaPath)
                .First(line => line.StartsWith("guid: ", StringComparison.Ordinal))
                .Substring("guid: ".Length)
                .Trim();

            string registry = File.ReadAllText(CombatVfxRegistryPath);
            Assert.That(registry, Does.Contain("vfxId: VFX_LINGERING_SHADE_RETURN_01"));
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
        public void CombatPresentationWarmup_MovesFirstUseLoadsOutOfCombatDispatch()
        {
            string warmup = File.ReadAllText(CombatPresentationWarmupPath);
            Assert.That(warmup, Does.Contain("LoadAndRetainPresentationAssets();"));
            Assert.That(warmup, Does.Contain("Resources.LoadAll<CombatAnimationSet>(\"CombatAnimationSets\")"));
            Assert.That(warmup, Does.Contain("WarmRegisteredVfxPrefabs"));
            Assert.That(warmup, Does.Contain("clip.SampleAnimation(avatar, 0f)"));
            Assert.That(warmup, Does.Contain("animator.Update(0f)"));
            Assert.That(warmup, Does.Contain("CoreUtils.GetDefaultDepthOnlyFormat()"));
            Assert.That(warmup, Does.Not.Contain("new RenderTexture(16, 16, 0"));
            Assert.That(warmup, Does.Contain("renderSubmissionEnabled = false"));
            Assert.That(warmup, Does.Contain("RenderPipeline.SubmitRenderRequest(camera, request)"));
            Assert.That(warmup, Does.Contain("DontDestroyOnLoad(gameObject)"));

            string catalog = File.ReadAllText(CombatProfileIdsPath);
            Assert.That(catalog, Does.Not.Contain("Resources.LoadAll<CombatAnimationSet>"));
            Assert.That(catalog, Does.Contain("CombatProfileIds.Daggers"));
            Assert.That(catalog, Does.Contain("CombatProfileIds.Staff"));
        }

        [Test]
        public void CombatPresentationWarmup_CollectsNestedPhasedAnimationClips()
        {
            var start = new AnimationClip { name = "WarmupStart" };
            var loop = new AnimationClip { name = "WarmupLoop" };
            var end = new AnimationClip { name = "WarmupEnd" };
            Assembly runtimeAssembly = AppDomain.CurrentDomain.Load("Assembly-CSharp");
            Type setType = runtimeAssembly.GetType("Arena.Presentation.CombatAnimationSet", true)!;
            Type attackType = runtimeAssembly.GetType("Arena.Presentation.WeaponMeleeAttackAuthoring", true)!;
            Type phasedType = runtimeAssembly.GetType("Arena.Presentation.WeaponPhasedActionClipSet", true)!;
            Type modeType = runtimeAssembly.GetType("Arena.Presentation.WeaponMeleePresentationMode", true)!;
            ScriptableObject set = ScriptableObject.CreateInstance(setType);
            object attack = Activator.CreateInstance(attackType)!;
            object phased = Activator.CreateInstance(phasedType)!;

            phasedType.GetField("start")!.SetValue(phased, start);
            phasedType.GetField("loop")!.SetValue(phased, loop);
            phasedType.GetField("end")!.SetValue(phased, end);
            attackType.GetField("presentationMode")!.SetValue(attack, Enum.Parse(modeType, "Phased"));
            attackType.GetField("phasedGround")!.SetValue(attack, phased);
            var attacks = (System.Collections.IList)setType.GetField("meleeAttacks")!.GetValue(set)!;
            attacks.Add(attack);

            try
            {
                Type warmupType = runtimeAssembly.GetType(
                    "Arena.Presentation.CombatPresentationWarmup",
                    throwOnError: true)!;
                MethodInfo collect = warmupType.GetMethod(
                    "CollectAnimationClipsForWarmup",
                    BindingFlags.Static | BindingFlags.NonPublic)!;
                var clips = new HashSet<AnimationClip>();

                collect.Invoke(null, new object?[] { set, clips, null });

                Assert.That(clips, Does.Contain(start));
                Assert.That(clips, Does.Contain(loop));
                Assert.That(clips, Does.Contain(end));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(set);
                UnityEngine.Object.DestroyImmediate(start);
                UnityEngine.Object.DestroyImmediate(loop);
                UnityEngine.Object.DestroyImmediate(end);
            }
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
        public void SelectableMeleeActivation_EmitsPressRejectionAndReducerResultDiagnostics()
        {
            string meleeInput = File.ReadAllText(MeleeInputHandlerPath);
            Assert.That(meleeInput, Does.Contain("RejectLocalMeleeAction("));
            foreach (string reason in new[]
                     {
                         "Dead",
                         "InvalidInput",
                         "InvalidTarget",
                         "NotFacingTarget",
                         "OnGlobalCooldown",
                         "OnCooldown",
                         "InsufficientResource",
                         "OutOfRange",
                         "LineOfSightBlocked",
                     })
            {
                Assert.That(
                    meleeInput,
                    Does.Contain($"ActionRejectReason.{reason}"),
                    $"local melee rejection '{reason}' must be identifiable in diagnostics");
            }
            Assert.That(meleeInput, Does.Contain("sending MeleeAttack action="));
            Assert.That(meleeInput, Does.Contain("MeleeAttack result="));

            string actionBarDispatcher = File.ReadAllText(ActionBarInputDispatcherPath);
            Assert.That(actionBarDispatcher, Does.Contain("LogDispatchSnapshot(action, conn, keyLabel, slotId)"));
            Assert.That(actionBarDispatcher, Does.Contain("press input="));
            Assert.That(actionBarDispatcher, Does.Contain("reason=missing_melee_definition"));

            string trace = File.ReadAllText(ActionBarTracePath);
            Assert.That(trace, Does.Contain("[ArenaActionDiagnostic]"));
            Assert.That(trace, Does.Contain("Debug.LogWarning"));
        }

        [Test]
        public void ActiveActionBarResolver_DerivesVisibleSlotsFromOrderedDisciplineSelections()
        {
            string contracts = File.ReadAllText(GameplayContractsPath);
            Assert.That(contracts, Does.Contain("TryGetDisciplineSelectionIndex"));
            Assert.That(contracts, Does.Contain("ResolveAbilityIdForActionBarSlot"));
            Assert.That(contracts, Does.Contain("conn.Db.CharacterDisciplineAbilitySelection.Owner.Filter(owner.Value)"));
            Assert.That(contracts, Does.Contain("selection.SortOrder == (uint)selectionIndex"));
            Assert.That(contracts, Does.Contain("CombatProfileResolver.ResolveForOwner(conn, owner)"));
            Assert.That(contracts, Does.Contain("SpellSlotResolver.IsSpellAssignmentEnabled"));
            Assert.That(contracts, Does.Not.Contain("TryResolveActiveSpec"));
            Assert.That(contracts, Does.Not.Contain("ResolveSelectableActionFromAssignment"));
            Assert.That(contracts, Does.Not.Contain("CharacterClassLoadoutState"));
            Assert.That(contracts, Does.Not.Contain("SavedSpecSlotAssignment"));
            Assert.That(
                contracts,
                Does.Not.Contain("ResolveForClass"),
                "Action bar runtime availability must follow equipped gear, not the old class profile.");
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
        public void FriendlyTargetedSpells_DefaultToSelfWhenSelectionIsOutsideAudience()
        {
            string spellInput = File.ReadAllText(SpellInputHandlerPath);

            Assert.That(
                spellInput,
                Does.Contain("!PartyRelationship.TargetAudienceAllowsLocal("),
                "A selected hostile must not override the self fallback for a friendly targeted spell.");
            Assert.That(
                spellInput,
                Does.Contain("ICombatTargetEntity? losTarget = resolvedTarget;"),
                "The advisory LOS check must use the self-substituted target, not the incompatible selection.");
            Assert.That(
                spellInput,
                Does.Not.Contain("ICombatTargetEntity? losTarget = TargetSelector.Instance?.SelectedTarget;"));
        }

        [Test]
        public void RecallDispatch_UsesTheStoredSpellTargetingContract()
        {
            string spellInput = File.ReadAllText(SpellInputHandlerPath);

            Assert.That(spellInput, Does.Contain("ResolveRecallTargetingDefinition"));
            Assert.That(spellInput, Does.Contain("conn.Db.RecallSlot.Owner.Find(owner)"));
            Assert.That(spellInput, Does.Contain("StartAimMode(spellId, targetingDef.Kind, aimRadius)"));
            Assert.That(spellInput, Does.Contain("TryCastTargeted(conn, spellId, targetingDef)"));
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
            Assert.That(panel, Does.Contain("conn.Db.AbilityCatalog.Iter()"));
            Assert.That(
                panel,
                Does.Contain("AbilityTagCodec.HasTag(ability.AbilityTags, ActionBarActionTag)"));
            Assert.That(panel, Does.Contain("ActionTooltipResolver.ResolveForAbility"));
            Assert.That(panel, Does.Contain("SpellsFilterKey"));
            Assert.That(panel, Does.Contain("AbilityIsKnownIfSpell"));
            Assert.That(panel, Does.Contain("new AbilityCategory(SpellsFilterKey, \"Spells\""));
            Assert.That(panel, Does.Contain("string.Equals(action.CategoryKey, SpellsFilterKey"));

            string serverPlayer = File.ReadAllText(ServerPlayerPath);
            Assert.That(serverPlayer, Does.Contain("crate::progression::sync_progression_catalogs(ctx);"));
            Assert.That(serverPlayer, Does.Contain("crate::spells::sync_spell_definitions(ctx);"));

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
            Assert.That(panel, Does.Contain("CharacterDisciplineAbilitySelection.Owner.Filter(owner)"));
            Assert.That(panel, Does.Not.Contain("() => ActionBarDragPayload.From(resolved, slotId)"));
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
            Assert.That(factory, Does.Contain("ActionBarLayout.IconInset"));
            Assert.That(factory, Does.Not.Contain("new GameObject(\"Frame\""));

            string layout = File.ReadAllText(ActionBarLayoutPath);
            Assert.That(layout, Does.Contain("public const float IconInset = 6f;"));

            string hud = File.ReadAllText(HudControllerPath);
            Assert.That(hud, Does.Contain("ActionBarLayout.IconInset"));

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
        public void EquipmentScreen_UsesAuthoritativeWholeSetSelectionAndLiveShowcase()
        {
            string screen = File.ReadAllText("Assets/Arena/Runtime/UI/Toolkit/EquipmentScreen.cs");
            string hubScreen = File.ReadAllText("Assets/Arena/Runtime/UI/Toolkit/HubScreen.cs");
            string hubNetwork = File.ReadAllText("Assets/Arena/Runtime/Network/HubNetworkManager.cs");
            string uxml = File.ReadAllText("Assets/Arena/Resources/UI/Toolkit/Equipment.uxml");
            string planner = File.ReadAllText(GameplaySubscriptionPlannerPath);
            string hub = File.ReadAllText(HubControllerPath);
            string entityRegistry = File.ReadAllText(EntityRegistryPath);
            string serverInventory = File.ReadAllText(ServerInventoryPath);
            string avatarAssembler = File.ReadAllText(
                "Assets/Arena/Runtime/Presentation/Appearance/CharacterAvatarAssembler.cs");
            string hubBuilder = File.ReadAllText("Assets/Arena/Editor/HubSceneAuthoringBuilder.cs");

            Assert.That(screen, Does.Contain("hub.SaveArmorSet"));
            Assert.That(screen, Does.Contain("OnArmorSetSaved"));
            Assert.That(screen, Does.Contain("hub.SaveWeaponLoadout"));
            Assert.That(screen, Does.Contain("OnWeaponLoadoutSaved"));
            Assert.That(screen, Does.Not.Contain("NetworkManager.Instance?.Conn"));
            Assert.That(screen, Does.Contain("SetShowcaseArmorPreview"));
            Assert.That(screen, Does.Contain("CompleteArmorPieces"));
            Assert.That(hubNetwork, Does.Contain("From.HubArmorSetDefinition().ToSql()"));
            Assert.That(hubNetwork, Does.Contain("From.HubWeaponDefinition().ToSql()"));
            Assert.That(hubNetwork, Does.Contain("context.Db.MyHubLoadout.Iter()"));
            Assert.That(hubNetwork, Does.Not.Contain("ApplyCommittedArmorSet("));
            Assert.That(uxml, Does.Contain("name=\"TierLight\""));
            Assert.That(uxml, Does.Contain("name=\"TierMedium\""));
            Assert.That(uxml, Does.Contain("name=\"TierHeavy\""));
            Assert.That(uxml, Does.Contain("name=\"PlayerShowcase\""));
            Assert.That(uxml, Does.Contain("<ui:ScrollView name=\"SetList\""));
            Assert.That(uxml, Does.Contain("<ui:ScrollView name=\"MainWeaponList\""));
            Assert.That(uxml, Does.Contain("<ui:ScrollView name=\"OffHandWeaponList\""));
            Assert.That(uxml, Does.Contain("name=\"WeaponsMode\""));
            Assert.That(planner, Does.Contain("From.ArmorSetDefinition()"));
            Assert.That(planner, Does.Contain("From.ActiveArmorSet()"));
            Assert.That(planner, Does.Contain("From.PlayerEquipmentPresentation()"));
            Assert.That(hub, Does.Contain("ResolveLocalArmorAppearance"));
            Assert.That(hub, Does.Contain("EquipmentScreen.ArmorAppearanceFor"));
            Assert.That(hub, Does.Contain("SetShowcaseWeaponPreview"));
            Assert.That(hub, Does.Contain("ResolveShowcaseWeaponVisuals"));
            Assert.That(hubScreen, Does.Contain("_hubController?.RefreshShowcaseLoadout()"));
            Assert.That(hub, Does.Contain("ShowcaseCameraFacingYaw = 180f"));
            Assert.That(hubBuilder, Does.Contain("ShowcaseDefaultYaw = 180f"));
            Assert.That(hub, Does.Contain("FaceShowcaseTowardCamera(_showcaseAvatarController.VisualRoot)"));
            Assert.That(screen, Does.Contain("_hubController.RotateShowcaseFromPointerDelta(deltaX)"));
            Assert.That(hubScreen, Does.Contain("_hubController.RotateShowcaseFromPointerDelta(deltaX)"));
            Assert.That(hub, Does.Contain("internal void RotateShowcaseFromPointerDelta(float deltaX)"));
            Assert.That(serverInventory, Does.Contain("upsert_active_armor_set(ctx, owner, spec)"));
            Assert.That(serverInventory, Does.Contain("sync_equipment_presentation_for_owner(ctx, owner)"));
            Assert.That(entityRegistry, Does.Contain("ApplyOwnerArmorPresentation(owner, presentation)"));
            Assert.That(entityRegistry, Does.Contain("entity.SetEquippedArmorItemDefIdsBySlot"));
            Assert.That(avatarAssembler, Does.Contain("for (int i = 0; i < ArmorEquipmentSlots.Length; i++)"));
        }

        [Test]
        public void HubShowcase_CombatCacheIncludesWeaponModelsAndColors()
        {
            string hub = File.ReadAllText(HubControllerPath);

            Assert.That(hub, Does.Contain("_lastShowcaseCombatSignature"));
            Assert.That(hub, Does.Contain("BuildShowcaseCombatSignature("));
            Assert.That(hub, Does.Contain("selection.MainHandItemDefId"));
            Assert.That(hub, Does.Contain("selection.MainHandColorId"));
            Assert.That(hub, Does.Contain("selection.OffHandItemDefId"));
            Assert.That(hub, Does.Contain("selection.OffHandColorId"));
            Assert.That(hub, Does.Not.Contain("_lastCombatProfile"));
        }

        [Test]
        public void HubDestinationMenu_ExposesServerAuthoritativeSurvivalEntry()
        {
            string hub = File.ReadAllText(HubControllerPath);
            Assert.That(hub, Does.Contain("SurvivalButtonName = \"Mode_Survival\""));
            Assert.That(hub, Does.Contain("SurvivalDisplayName = \"Survival Mode\""));
            Assert.That(hub, Does.Contain("destinationButton.onClick.AddListener(RequestSurvival)"));
            Assert.That(hub, Does.Contain("_travelConnection.Reducers.StartSurvivalRun()"));
            Assert.That(hub, Does.Contain("OnStartSurvivalRun += OnStartSurvivalRun"));
            Assert.That(hub, Does.Not.Contain("SceneManager.LoadScene(\"Arena_Map_01\")"));

            string builder = File.ReadAllText("Assets/Arena/Editor/HubSceneAuthoringBuilder.cs");
            Assert.That(builder, Does.Contain("\"Mode_Survival\""));
            Assert.That(builder, Does.Contain("\"Survival Mode\""));
        }

        [Test]
        public void HubMatchmakingControls_ExposeApprovedUnranked2v2BotMatch()
        {
            string screen = File.ReadAllText("Assets/Arena/Runtime/UI/Toolkit/HubScreen.cs");
            string uxml = File.ReadAllText("Assets/Arena/Resources/UI/Toolkit/Hub.uxml");
            string uss = File.ReadAllText("Assets/Arena/Resources/UI/Toolkit/Hub.uss");

            Assert.That(uxml, Does.Contain("name=\"QueueButton\""));
            Assert.That(uxml, Does.Contain("name=\"MatchOverlay\""));
            Assert.That(uxml, Does.Contain("name=\"Format2v2\""));
            Assert.That(uxml, Does.Contain("name=\"Format3v3\""));
            Assert.That(uxml, Does.Contain("name=\"Format10v10\""));
            Assert.That(uxml, Does.Contain("name=\"QueueConfirm\""));
            Assert.That(uxml, Does.Not.Contain("format-emblem"));

            Assert.That(uss, Does.Contain(".match-overlay.is-open"));
            Assert.That(uss, Does.Contain(".format-option.is-selected"));
            Assert.That(uss, Does.Contain(".match-button.is-searching"));
            Assert.That(uss, Does.Contain(".hub-screen .queue-summary"));
            Assert.That(uss, Does.Contain(".hub-screen .match-button"));
            Assert.That(uss, Does.Contain(".hub-screen .practice-button"));
            Assert.That(uss, Does.Contain("-unity-text-align: middle-left"));

            Assert.That(screen, Does.Contain("_findMatchButton.clicked += OnFindMatchClicked"));
            Assert.That(screen, Does.Contain("_queueConfirm.clicked += ConfirmMatchSearch"));
            Assert.That(screen, Does.Contain("MatchHandoffCoordinator.EnsureInstance()"));
            Assert.That(screen, Does.Contain("RequestUnranked2V2BotMatch()"));
            Assert.That(screen, Does.Contain("HubNetworkManager? hub = HubNetworkManager.Instance"));
            Assert.That(screen, Does.Contain("HubPlayerSnapshot? hubPlayer = hub?.Player"));
            Assert.That(screen, Does.Not.Contain("Reducers.StartUnranked2V2BotMatch()"));
            Assert.That(screen, Does.Not.Contain("OnStartUnranked2V2BotMatch"));
            Assert.That(screen, Does.Contain("START 2V2 BOT MATCH"));
            Assert.That(screen, Does.Contain("_format3v3Type.text = \"COMING SOON\""));
            Assert.That(screen, Does.Contain("_format10v10Type.text = \"COMING SOON\""));
            Assert.That(screen, Does.Contain("_format3v3.SetEnabled(false)"));
            Assert.That(screen, Does.Contain("_format10v10.SetEnabled(false)"));
            Assert.That(screen, Does.Not.Contain("ToggleQueueMode"));
            Assert.That(screen, Does.Contain("public bool TryCloseForEscape()"));
        }

        [Test]
        public void BotMatchReturn_UsesExplicitHubGuardAndNoInGameLobby()
        {
            string overlay = File.ReadAllText("Assets/Arena/Runtime/UI/MatchOverlay.cs");
            string queue = File.ReadAllText("Assets/Arena/Runtime/Entity/RuntimeSceneTransitionQueue.cs");
            string coordinator = File.ReadAllText("Assets/Arena/Runtime/Entity/LocalWorldRuntimeCoordinator.cs");

            Assert.That(File.Exists("Assets/Arena/Runtime/UI/LobbyController.cs"), Is.False);
            Assert.That(overlay, Does.Contain("handoff.ReturnToHub()"));
            Assert.That(overlay, Does.Contain("RuntimeSceneTransitionQueue.BeginExplicitHubReturn()"));
            Assert.That(overlay, Does.Contain("RuntimeSceneTransitionQueue.RequestExplicitHubReturn()"));
            Assert.That(overlay, Does.Contain("RuntimeSceneTransitionQueue.CancelExplicitHubReturn()"));
            Assert.That(overlay, Does.Contain("Status.Failed(var failure)"));
            Assert.That(overlay, Does.Not.Contain("Play Again"));
            Assert.That(queue, Does.Contain("s_explicitHubReturnPending"));
            Assert.That(queue, Does.Contain("Request(\"Hub\")"));
            Assert.That(coordinator, Does.Contain("IsExplicitHubReturnPending"));
            Assert.That(coordinator, Does.Contain("&& !row.InstanceId.HasValue"));

            string handoff = File.ReadAllText(
                "Assets/Arena/Runtime/Network/MatchHandoffCoordinator.cs");
            Assert.That(handoff, Does.Contain("DisconnectForMatchHandoff()"));
            Assert.That(handoff, Does.Contain("DisconnectProvisionedMatch()"));
            Assert.That(handoff, Does.Contain("RequestExplicitHubReturn()"));
        }

        [Test]
        public void HubPracticeButton_OpensRetainedDestinationMenuWithoutRestoringLegacyHub()
        {
            string screen = File.ReadAllText("Assets/Arena/Runtime/UI/Toolkit/HubScreen.cs");
            Assert.That(screen, Does.Contain("_root.Q<Button>(\"PracticeButton\")"));
            Assert.That(screen, Does.Contain("_practiceButton.clicked += OpenPracticeMenu"));
            Assert.That(screen, Does.Contain("_hubController?.OpenPracticeMenu()"));

            string hub = File.ReadAllText(HubControllerPath);
            Assert.That(hub, Does.Contain("public void OpenPracticeMenu()"));
            Assert.That(hub, Does.Contain("SetTravelMenuOpen(true, bringToFront: true)"));
            Assert.That(hub, Does.Contain("child.gameObject.SetActive(child.gameObject == _travelMenu)"));
            Assert.That(hub, Does.Contain("RuntimeUiLayer.BringToFront"));
            Assert.That(hub, Does.Contain("RuntimeUiEscapeRouter.Register(this)"));
            Assert.That(hub, Does.Contain("public bool TryCloseForEscape()"));

            string escapeRouter = File.ReadAllText(RuntimeUiEscapeRouterPath);
            Assert.That(escapeRouter, Does.Contain("class RuntimeUiEscapeInputDriver"));
            Assert.That(escapeRouter, Does.Contain("Keyboard.current?.escapeKey.wasPressedThisFrame"));
            Assert.That(escapeRouter, Does.Contain("RuntimeUiEscapeRouter.TryCloseTopmost()"));
        }

        [Test]
        public void SurvivalShop_ClipsAndScrollsOffersWithinItsFrame()
        {
            string uxml = File.ReadAllText("Assets/Arena/Resources/UI/Toolkit/SurvivalShop.uxml");
            Assert.That(uxml, Does.Contain("<ui:ScrollView name=\"OfferScroll\""));
            Assert.That(uxml, Does.Contain("horizontal-scroller-visibility=\"Hidden\""));

            string uss = File.ReadAllText("Assets/Arena/Resources/UI/Toolkit/SurvivalShop.uss");
            Assert.That(uss, Does.Contain(".offer-scroll"));
            Assert.That(uss, Does.Contain(".offer-scroll .unity-scroll-view__content-viewport"));
            Assert.That(uss, Does.Contain(".offer-card"));
            Assert.That(uss, Does.Contain("overflow: hidden;"));
            Assert.That(uss, Does.Contain("white-space: normal;"));
        }

        [Test]
        public void SurvivalShop_ReservesEnoughHeightAndASeparatePriceRowInOfferCards()
        {
            string uss = File.ReadAllText("Assets/Arena/Resources/UI/Toolkit/SurvivalShop.uss");

            Assert.That(uss, Does.Contain("height: 200px;"));
            Assert.That(uss, Does.Contain(".offer-detail"));
            Assert.That(uss, Does.Contain("flex-grow: 1;"));
            Assert.That(uss, Does.Contain(".offer-price"));
            Assert.That(uss, Does.Contain("margin-top: 6px;"));
            Assert.That(uss, Does.Not.Contain("margin-top: auto;"));
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

        [Test]
        public void RimedDebuffs_UseAnIcyPaneAndExplainAbilityRemovalProtection()
        {
            string hud = File.ReadAllText(HudControllerPath);
            Assert.That(hud, Does.Contain("HasActiveRime(_tmpDebuff)"));
            Assert.That(hud, Does.Contain("new GameObject(\"RimedPane\")"));
            Assert.That(hud, Does.Contain("rimeLabel.text = \"RIMED\""));

            string tooltip = File.ReadAllText(StatusTooltipResolverPath);
            Assert.That(tooltip, Does.Contain("Rimed: cannot be removed by abilities; expires naturally."));
        }

        [Test]
        public void PrimalStatusTooltips_ExposeAdaptationTypeAndPermanentOvergrowth()
        {
            string tooltip = File.ReadAllText(StatusTooltipResolverPath);

            Assert.That(tooltip, Does.Contain("stackGroup, \"ADAPTATION\""));
            Assert.That(tooltip, Does.Contain("TitleCaseStatusKind(status.DamageType)"));
            Assert.That(tooltip, Does.Contain("stackGroup, \"OVERGROWTH\""));
            Assert.That(tooltip, Does.Contain("parts.Add(\"Permanent\")"));
        }

        [Test]
        public void EscalatingDotTooltips_ShowResolvedDamageAndNextStackDecay()
        {
            string tooltip = File.ReadAllText(StatusTooltipResolverPath);
            Assert.That(tooltip, Does.Contain("ADD_STACK_ESCALATING_DECAY"));
            Assert.That(tooltip, Does.Contain("EscalatingDotDamageBonusBpsPerStackPair = 3_000L"));
            Assert.That(tooltip, Does.Contain("One stack is lost when the timer expires"));

            string hud = File.ReadAllText(HudControllerPath);
            Assert.That(hud, Does.Contain("{se.StackPolicy}"));
            Assert.That(hud, Does.Contain("{se.TickAmount}"));
        }

        [Test]
        public void Blizzard_UsesTheAuthoredIcicleRainAreaPrefab()
        {
            Assert.That(File.Exists(BlizzardVfxPrefabPath), Is.True);

            string prefabMeta = File.ReadAllText(BlizzardVfxPrefabPath + ".meta");
            Assert.That(prefabMeta, Does.Contain("guid: 257b0b8c164454f4ebe7b7b4b6c045db"));

            string registry = File.ReadAllText(CombatVfxRegistryPath);
            Assert.That(registry, Does.Contain("vfxId: VFX_BLIZZARD_AREA_01"));
            Assert.That(registry, Does.Contain("guid: 257b0b8c164454f4ebe7b7b4b6c045db"));
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
