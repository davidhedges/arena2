#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Arena.Combat;
using Arena.Presentation;
using Arena.Presentation.VFX;
using UnityEditor;
using UnityEngine;

namespace Arena.Editor
{
    internal sealed partial class SpellAuthoringWindow : EditorWindow
    {
        private const string AnchorLeftHand = "LEFT_HAND";
        private const string AnchorRightHand = "RIGHT_HAND";
        private const string TriggerSpellCast = "SPELL_CAST";
        private const string AttachModeFollowAnchor = "FOLLOW_ANCHOR";
        private const string RoleAttached = "ATTACHED";
        private const string RoleProjectileBody = "PROJECTILE_BODY";
        private const string LifecycleDuration = "DURATION";
        private const string LifecycleUntilReleaseEvent = "UNTIL_RELEASE_EVENT";

        private readonly List<string> _loadErrors = new();
        private readonly Dictionary<string, CombatAnimationSet> _animationSetByProfile = new(StringComparer.Ordinal);
        private readonly List<AbilityDefinition> _spellAbilities = new();
        private readonly List<CombatVfxCueDefinition> _selectedAbilityCues = new();
        private readonly List<string> _knownTemplateIds = new();
        private readonly List<string> _knownDisciplineIds = new();
        private bool _knownTemplateIdsLoaded;

        private ProgressionCatalogDocument? _catalog;
        private SpellCastAnimationMap? _spellAnimationMap;
        private SpellCastAnimationCatalog? _spellAnimationCatalog;
        private CombatAnimationSet[] _animationSets = Array.Empty<CombatAnimationSet>();
        private bool _animationSetsLoaded;
        private Vector2 _scroll;
        private int _selectedSpellIndex;
        private string _draftAbilityId = "SPELL_NEW_SPELL";
        private string _draftSpellId = "NEW_SPELL";
        private string _draftDisciplineId = "ARCANA";
        private string _draftCombatProfileId = string.Empty;
        private string _draftDisplayName = "New Spell";
        private string _draftCastVfxId = "VFX_CAST_HAND_01";
        private string _draftProjectileVfxId = "VFX_PROJECTILE_01";
        private string _draftImpactVfxId = "VFX_HIT_01";
        private bool _includeFallbackCastHandCue = true;
        private SpellCastOrigin _draftFallbackCastOrigin = SpellCastOrigin.LeftHand;
        private string _generatedSnippet = string.Empty;

        [MenuItem("Arena/Spell Authoring/Open Spell Authoring", false, 490)]
        public static void Open()
        {
            var window = GetWindow<SpellAuthoringWindow>("Spell Authoring");
            window.minSize = new Vector2(680f, 620f);
        }

        private void OnEnable()
        {
            Load();
        }

        private void OnDisable() => DestroyCastAnimationPreview();

        private void OnDestroy() => DestroyCastAnimationPreview();

        private void OnFocus() => InvalidateGeneratedCueCache();

        private void OnProjectChange() => InvalidateGeneratedCueCache();

        private void OnGUI()
        {
            DrawToolbar();

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            if (_loadErrors.Count > 0)
            {
                foreach (string error in _loadErrors)
                    EditorGUILayout.HelpBox(error, MessageType.Error);
            }

            if (_catalog == null)
            {
                EditorGUILayout.EndScrollView();
                return;
            }

            DrawSelectedSpellAudit();
            EditorGUILayout.Space(12f);
            DrawSnippetGenerator();
            EditorGUILayout.EndScrollView();
        }

        private void DrawToolbar()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                if (GUILayout.Button("Reload", EditorStyles.toolbarButton, GUILayout.Width(80f)))
                    Load();
                if (GUILayout.Button("Validate Combat VFX", EditorStyles.toolbarButton, GUILayout.Width(150f)))
                    CombatVFXAuthoringValidator.ValidateFromMenu();
                if (GUILayout.Button("Select VFX Registry", EditorStyles.toolbarButton, GUILayout.Width(145f)))
                    Selection.activeObject = CombatVFXRegistry.LoadShared();
                GUILayout.FlexibleSpace();
            }
        }

        private void DrawSelectedSpellAudit()
        {
            EditorGUILayout.LabelField("Existing Spell Audit", EditorStyles.boldLabel);
            if (_spellAbilities.Count == 0)
            {
                EditorGUILayout.HelpBox("No SPELL abilities found in progression_catalog.shared.json.", MessageType.Warning);
                return;
            }

            string[] labels = _spellAbilities
                .Select(ability => $"{Normalize(ability.ability_id)} -> {Normalize(ability.action_id)}")
                .ToArray();
            _selectedSpellIndex = Mathf.Clamp(_selectedSpellIndex, 0, _spellAbilities.Count - 1);
            int newSelectedIndex = EditorGUILayout.Popup("Spell Ability", _selectedSpellIndex, labels);
            if (newSelectedIndex != _selectedSpellIndex)
            {
                _selectedSpellIndex = newSelectedIndex;
                CopySelectedAbilityToDraft();
            }

            AbilityDefinition selected = _spellAbilities[_selectedSpellIndex];
            string abilityId = Normalize(selected.ability_id);
            string spellId = Normalize(selected.action_id);
            string disciplineId = Normalize(selected.discipline_id);
            string combatProfileId = Normalize(selected.combat_profile_id);
            string deliveryKind = Normalize(selected.gameplay.delivery.kind);
            SpellAnimationArchetype archetype = SpellAnimationArchetypes.Derive(
                (ulong)Math.Max(0, selected.gameplay.cast_time_ms),
                deliveryKind);
            _selectedAbilityCues.Clear();
            _selectedAbilityCues.AddRange(_catalog!.combat_vfx_cues.Where(cue =>
                (string.Equals(Normalize(cue.owner_kind), "ABILITY", StringComparison.Ordinal)
                    && string.Equals(Normalize(cue.owner_id), abilityId, StringComparison.Ordinal))
                || (string.Equals(Normalize(cue.owner_kind), "SPELL", StringComparison.Ordinal)
                    && string.Equals(Normalize(cue.owner_id), spellId, StringComparison.Ordinal))));

            bool hasResolvedAnimation = false;
            WeaponSpellAnimationEntry resolvedAnimation = default;

            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.TextField("Ability Id", abilityId);
                EditorGUILayout.TextField("Spell Id", spellId);
                EditorGUILayout.TextField("Discipline", disciplineId);
                EditorGUILayout.TextField("Combat Profile", combatProfileId);
                EditorGUILayout.TextField("Display Name", selected.display_name);
                EditorGUILayout.IntField("Cast Time Ms", selected.gameplay.cast_time_ms);
                EditorGUILayout.TextField("Delivery", deliveryKind);
            }

            DrawCastAnimationPicker(spellId, archetype);

            if (string.IsNullOrWhiteSpace(combatProfileId)
                && SpellCastAnimationResolver.TryResolve(
                    null,
                    spellId,
                    archetype,
                    out WeaponSpellAnimationEntry sharedEntry))
            {
                hasResolvedAnimation = true;
                resolvedAnimation = sharedEntry;
            }

            if (string.IsNullOrWhiteSpace(combatProfileId))
            {
                SpellCastAnimationMap? map = _spellAnimationMap;
                if (map != null && map.TryGetEntry(spellId, out SpellCastAnimationMap.Entry mapEntry))
                {
                    if (mapEntry.assignmentKind == SpellCastAnimationAssignmentKind.NoAnimation)
                    {
                        EditorGUILayout.HelpBox(
                            $"Profile-less shared spell '{abilityId}' is explicitly classified as requiring no cast animation.",
                            MessageType.Info);
                    }
                    else
                    {
                        string assignment = mapEntry.assignmentKind switch
                        {
                            SpellCastAnimationAssignmentKind.Catalog => $"catalog recipe '{Normalize(mapEntry.animationId)}'",
                            SpellCastAnimationAssignmentKind.Fixed => "Fixed (independent of combat set)",
                            _ => $"legacy motion {mapEntry.motion}",
                        };
                        EditorGUILayout.HelpBox(
                            $"Profile-less shared spell '{abilityId}' is classified as {assignment}. Its global recipe applies across combat sets unless the active set has an explicit spell override.",
                            MessageType.Info);
                    }
                }
                else
                {
                    EditorGUILayout.HelpBox(
                        $"Profile-less shared spell '{abilityId}' has no SpellCastAnimationMap classification.",
                        MessageType.Warning);
                }
            }
            else
            {
                string resolutionKey =
                    $"Arena.SpellAuthoring.CombatSetResolution.Visible.V2.{spellId}.{combatProfileId}";
                bool showCombatSetResolution = SessionState.GetBool(resolutionKey, false);
                showCombatSetResolution = EditorGUILayout.Foldout(
                    showCombatSetResolution,
                    "Combat Set Resolution",
                    true,
                    EditorStyles.foldoutHeader);
                SessionState.SetBool(resolutionKey, showCombatSetResolution);
                if (showCombatSetResolution)
                {
                    EnsureAnimationSetsLoaded();
                    if (!_animationSetByProfile.TryGetValue(
                            combatProfileId,
                            out CombatAnimationSet animationSet))
                    {
                        EditorGUILayout.HelpBox(
                            $"No CombatAnimationSet found for combat profile '{combatProfileId}'.",
                            MessageType.Error);
                    }
                    else
                    {
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            EditorGUILayout.ObjectField(
                                "Animation Set",
                                animationSet,
                                typeof(CombatAnimationSet),
                                false);
                            if (GUILayout.Button("Select", GUILayout.Width(70f)))
                                Selection.activeObject = animationSet;
                        }

                        if (TryResolveSpellAnimationEntry(
                                animationSet,
                                spellId,
                                out WeaponSpellAnimationEntry entry))
                        {
                            hasResolvedAnimation = true;
                            resolvedAnimation = entry;
                            if (entry.ResolveClip() != null)
                            {
                                EditorGUILayout.HelpBox(
                                    $"Animation resolves for '{spellId}'. Cast clip assigned.",
                                    MessageType.Info);
                            }
                            else
                            {
                                EditorGUILayout.HelpBox(
                                    $"Animation resolves for '{spellId}', but no cast/release clip is assigned yet.",
                                    MessageType.Warning);
                            }
                        }
                        else
                        {
                            EditorGUILayout.HelpBox(
                                $"The cast animation assignment for '{spellId}' does not resolve in '{animationSet.name}'. Check its global recipe and optional set override.",
                                MessageType.Warning);
                            if (GUILayout.Button("Select Spell Cast Map", GUILayout.Width(180f)))
                                Selection.activeObject = _spellAnimationMap;
                        }
                    }
                }
            }

            string vfxAuditKey = $"Arena.SpellAuthoring.VfxAudit.Visible.V2.{abilityId}";
            bool showVfxAudit = SessionState.GetBool(vfxAuditKey, false);
            showVfxAudit = EditorGUILayout.Foldout(
                showVfxAudit,
                "VFX Audit",
                true,
                EditorStyles.foldoutHeader);
            SessionState.SetBool(vfxAuditKey, showVfxAudit);
            if (showVfxAudit)
            {
                if (!hasResolvedAnimation
                    && !string.IsNullOrWhiteSpace(combatProfileId))
                {
                    EnsureAnimationSetsLoaded();
                    if (_animationSetByProfile.TryGetValue(
                            combatProfileId,
                            out CombatAnimationSet animationSet)
                        && TryResolveSpellAnimationEntry(
                            animationSet,
                            spellId,
                            out WeaponSpellAnimationEntry entry))
                    {
                        hasResolvedAnimation = true;
                        resolvedAnimation = entry;
                    }
                }

                DrawCueAudit(
                    abilityId,
                    deliveryKind,
                    selected.gameplay.cast_time_ms,
                    hasResolvedAnimation,
                    resolvedAnimation);
                EditorGUILayout.Space(12f);
                DrawGeneratedCuePreview(selected, abilityId, hasResolvedAnimation, resolvedAnimation);
            }
        }

        private void DrawCastAnimationPicker(string spellId, SpellAnimationArchetype archetype)
        {
            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("Cast Animation", EditorStyles.boldLabel);
            if (_spellAnimationMap == null || _spellAnimationCatalog == null)
            {
                EditorGUILayout.HelpBox(
                    "SpellCastAnimationMap or SpellCastAnimationCatalog is missing.",
                    MessageType.Error);
                return;
            }

            var recipes = _spellAnimationCatalog.Recipes
                .OrderBy(recipe => recipe.CategoryOrDefault, StringComparer.OrdinalIgnoreCase)
                .ThenBy(recipe => recipe.DisplayNameOrId, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (recipes.Length == 0)
            {
                EditorGUILayout.HelpBox("The shared cast animation catalog has no recipes.", MessageType.Warning);
                return;
            }

            bool hasMapEntry = _spellAnimationMap.TryGetEntry(spellId, out SpellCastAnimationMap.Entry mapEntry);
            string currentAnimationId = hasMapEntry
                && mapEntry.assignmentKind == SpellCastAnimationAssignmentKind.Catalog
                    ? Normalize(mapEntry.animationId)
                    : string.Empty;
            int currentRecipeIndex = Array.FindIndex(
                recipes,
                recipe => string.Equals(recipe.AnimationIdOrEmpty, currentAnimationId, StringComparison.Ordinal));

            string inheritedLabel = hasMapEntry
                ? mapEntry.assignmentKind switch
                {
                    SpellCastAnimationAssignmentKind.LegacyMotion => $"Legacy / {mapEntry.motion}",
                    SpellCastAnimationAssignmentKind.Fixed => "Inline fixed presentation",
                    SpellCastAnimationAssignmentKind.NoAnimation => "No animation",
                    SpellCastAnimationAssignmentKind.Catalog => $"Missing recipe / {currentAnimationId}",
                    _ => "Unmapped",
                }
                : "Unmapped";
            var recipeLabels = new string[recipes.Length];
            for (int index = 0; index < recipes.Length; index++)
            {
                SpellCastAnimationRecipe recipe = recipes[index];
                recipeLabels[index] = recipe.IsCompatibleWith(archetype)
                    ? recipe.PickerLabel
                    : $"{recipe.PickerLabel} (not tagged for {archetype})";
            }

            string previewRecipeKey = $"Arena.SpellAuthoring.CastPreview.Recipe.{spellId}";
            string previewRecipeId = SessionState.GetString(
                previewRecipeKey,
                currentAnimationId);
            int previewRecipeIndex = Array.FindIndex(
                recipes,
                recipe => string.Equals(
                    recipe.AnimationIdOrEmpty,
                    previewRecipeId,
                    StringComparison.Ordinal));
            if (previewRecipeIndex < 0 || previewRecipeIndex >= recipes.Length)
                previewRecipeIndex = currentRecipeIndex >= 0 ? currentRecipeIndex : 0;
            int selectedPreviewRecipeIndex = EditorGUILayout.Popup(
                "Preview Recipe",
                previewRecipeIndex,
                recipeLabels);
            if (selectedPreviewRecipeIndex != previewRecipeIndex)
            {
                previewRecipeIndex = selectedPreviewRecipeIndex;
                SessionState.SetString(
                    previewRecipeKey,
                    recipes[previewRecipeIndex].AnimationIdOrEmpty);
                ResetCastAnimationPreview();
            }

            SpellCastAnimationRecipe previewRecipe = recipes[previewRecipeIndex];
            bool previewIsCompatible = previewRecipe.IsCompatibleWith(archetype);
            string globalLabel = currentRecipeIndex >= 0
                ? recipes[currentRecipeIndex].PickerLabel
                : inheritedLabel;
            EditorGUILayout.LabelField("Assigned Globally", globalLabel);
            SpellCastOrigin selectedOrigin = (SpellCastOrigin)EditorGUILayout.EnumPopup(
                new GUIContent(
                    "Animation Cast Origin",
                    "Natural emission hand owned by this shared animation recipe. A mirrored CombatAnimationSet override swaps this hand together with the humanoid motion."),
                previewRecipe.castOrigin);
            if (selectedOrigin != previewRecipe.castOrigin)
            {
                Undo.RecordObject(
                    _spellAnimationCatalog,
                    $"Set {previewRecipe.AnimationIdOrEmpty} cast origin");
                if (_spellAnimationCatalog.EditorSetCastOrigin(
                        previewRecipe.AnimationIdOrEmpty,
                        selectedOrigin))
                {
                    previewRecipe.castOrigin = selectedOrigin;
                    recipes[previewRecipeIndex] = previewRecipe;
                    EditorUtility.SetDirty(_spellAnimationCatalog);
                    AssetDatabase.SaveAssets();
                    SpellCastAnimationResolver.InvalidateCache();
                }
            }
            EditorGUILayout.LabelField(
                "",
                "Recipe origin changes save immediately.",
                EditorStyles.miniLabel);

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(
                           !previewIsCompatible || previewRecipeIndex == currentRecipeIndex))
                {
                    if (GUILayout.Button("Assign Preview Globally"))
                    {
                        SpellCastAnimationRecipe selectedRecipe = recipes[previewRecipeIndex];
                        Undo.RecordObject(_spellAnimationMap, $"Set {spellId} cast animation");
                        _spellAnimationMap.EditorSetCatalogAssignment(
                            spellId,
                            selectedRecipe.AnimationIdOrEmpty);
                        EditorUtility.SetDirty(_spellAnimationMap);
                        AssetDatabase.SaveAssets();
                        SpellCastAnimationResolver.InvalidateCache();
                        currentRecipeIndex = previewRecipeIndex;
                        globalLabel = selectedRecipe.PickerLabel;
                    }
                }

                if (currentRecipeIndex >= 0 && previewRecipeIndex != currentRecipeIndex
                    && GUILayout.Button("Preview Assigned", GUILayout.Width(130f)))
                {
                    previewRecipeIndex = currentRecipeIndex;
                    SessionState.SetString(
                        previewRecipeKey,
                        recipes[previewRecipeIndex].AnimationIdOrEmpty);
                    previewRecipe = recipes[previewRecipeIndex];
                    previewIsCompatible = previewRecipe.IsCompatibleWith(archetype);
                    ResetCastAnimationPreview();
                }
            }

            if (!previewIsCompatible)
            {
                EditorGUILayout.HelpBox(
                    $"This recipe can be previewed, but it is not tagged for the spell's {archetype} lifecycle and cannot be assigned here.",
                    MessageType.Warning);
            }

            EditorGUILayout.HelpBox(
                $"{globalLabel} is the default for every CombatAnimationSet. Add only the exceptions below.",
                MessageType.None);
            DrawCastAnimationPreview(spellId, previewRecipe);

            string foldoutKey = $"Arena.SpellAuthoring.CastOverrides.{spellId}";
            bool showOverrides = SessionState.GetBool(foldoutKey, false);
            showOverrides = EditorGUILayout.Foldout(
                showOverrides,
                "CombatAnimationSet Overrides",
                true,
                EditorStyles.foldoutHeader);
            SessionState.SetBool(foldoutKey, showOverrides);
            if (!showOverrides)
                return;

            EnsureAnimationSetsLoaded();
            using (new EditorGUI.IndentLevelScope())
            {
                foreach (CombatAnimationSet animationSet in _animationSets)
                {
                    bool hasOverride = animationSet.TryGetSpellCastAnimationOverride(
                        spellId,
                        out SpellCastAnimationOverride animationOverride);
                    string overrideId = hasOverride
                        ? animationOverride.AnimationIdOrEmpty
                        : string.Empty;
                    bool mirrored = hasOverride && animationOverride.mirrorPresentation;
                    int overrideRecipeIndex = overrideId.Length != 0
                        ? Array.FindIndex(
                            recipes,
                            recipe => string.Equals(recipe.AnimationIdOrEmpty, overrideId, StringComparison.Ordinal))
                        : -1;
                    var overrideLabels = new string[recipeLabels.Length + 1];
                    overrideLabels[0] = $"Use Global / {globalLabel}";
                    Array.Copy(recipeLabels, 0, overrideLabels, 1, recipes.Length);
                    int overridePopupIndex = overrideRecipeIndex >= 0 ? overrideRecipeIndex + 1 : 0;
                    int selectedOverrideIndex;
                    bool selectedMirrored;
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.PrefixLabel(animationSet.name);
                        selectedOverrideIndex = EditorGUILayout.Popup(
                            overridePopupIndex,
                            overrideLabels);
                        selectedMirrored = GUILayout.Toggle(
                            mirrored,
                            new GUIContent(
                                "Mirror",
                                "Mirrors the complete humanoid cast and swaps the recipe's authored Left/Right cast origin."),
                            GUILayout.Width(62f));
                    }
                    if (selectedOverrideIndex != overridePopupIndex
                        || selectedMirrored != mirrored)
                    {
                        string selectedOverrideId = selectedOverrideIndex == 0
                            ? string.Empty
                            : recipes[selectedOverrideIndex - 1].AnimationIdOrEmpty;
                        SetCombatAnimationOverride(
                            animationSet,
                            spellId,
                            selectedOverrideId,
                            selectedMirrored);
                    }

                    int effectiveRecipeIndex = selectedOverrideIndex > 0
                        ? selectedOverrideIndex - 1
                        : currentRecipeIndex;
                    if (selectedMirrored
                        && effectiveRecipeIndex >= 0
                        && recipes[effectiveRecipeIndex].castOrigin == SpellCastOrigin.UseVfxCue)
                    {
                        EditorGUILayout.HelpBox(
                            $"{animationSet.name} mirrors the body, but {recipes[effectiveRecipeIndex].PickerLabel} has no animation-owned cast origin yet. Set its Animation Cast Origin above so the launch hand mirrors too.",
                            MessageType.Warning);
                    }
                }
            }
        }

        private static void SetCombatAnimationOverride(
            CombatAnimationSet animationSet,
            string spellId,
            string animationId,
            bool mirrorPresentation)
        {
            string normalizedSpellId = Normalize(spellId);
            string normalizedAnimationId = Normalize(animationId);
            var overrides = new List<SpellCastAnimationOverride>(
                animationSet.spellCastAnimationOverrides ?? Array.Empty<SpellCastAnimationOverride>());
            int existingIndex = overrides.FindIndex(candidate =>
                string.Equals(candidate.SpellIdOrEmpty, normalizedSpellId, StringComparison.Ordinal));

            Undo.RecordObject(animationSet, $"Override {normalizedSpellId} cast animation");
            CombatAnimationSetProtection.MarkTrustedMutation(animationSet, "spell-cast-animation-override");
            if (normalizedAnimationId.Length == 0 && !mirrorPresentation)
            {
                if (existingIndex >= 0)
                    overrides.RemoveAt(existingIndex);
            }
            else
            {
                var replacement = new SpellCastAnimationOverride
                {
                    spellId = normalizedSpellId,
                    animationId = normalizedAnimationId,
                    mirrorPresentation = mirrorPresentation,
                };
                if (existingIndex >= 0)
                    overrides[existingIndex] = replacement;
                else
                    overrides.Add(replacement);
            }

            animationSet.spellCastAnimationOverrides = overrides.ToArray();
            EditorUtility.SetDirty(animationSet);
            AssetDatabase.SaveAssets();
            SpellCastAnimationResolver.InvalidateCache();
        }

        private void DrawCueAudit(
            string abilityId,
            string deliveryKind,
            int castTimeMs,
            bool hasResolvedAnimation,
            WeaponSpellAnimationEntry resolvedAnimation)
        {
            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("Authored VFX Cues", EditorStyles.boldLabel);
            string expectedHandAnchor = string.Empty;
            string expectedHandReason = string.Empty;
            bool hasExpectedHand = hasResolvedAnimation
                && TryInferSpellPresentationHand(resolvedAnimation, out expectedHandAnchor, out expectedHandReason);
            if (hasExpectedHand)
                EditorGUILayout.LabelField("Animation Hand", $"{expectedHandAnchor} ({expectedHandReason})");

            if (_selectedAbilityCues.Count == 0)
            {
                EditorGUILayout.HelpBox("No ability-scoped or spell-scoped combat_vfx_cues rows exist for this spell.", MessageType.Warning);
            }
            else
            {
                foreach (CombatVfxCueDefinition cue in _selectedAbilityCues.OrderBy(cue => cue.sort_order))
                {
                    string ownerKind = Normalize(cue.owner_kind);
                    string ownerId = Normalize(cue.owner_id);
                    string trigger = Normalize(cue.trigger);
                    string anchor = Normalize(cue.anchor);
                    string attachMode = Normalize(cue.attach_mode);
                    string role = EffectiveRole(cue.vfx_role);
                    string lifecycle = EffectiveLifecycle(cue.lifecycle);
                    string sequence = role == RoleProjectileBody || role == SpellVfxGenerator.RoleProjectileTrail
                        ? $" | sequence={cue.projectile_sequence_index}"
                        : string.Empty;
                    string templateStatus = CombatVFXTemplateRegistry.CanResolveTemplate(cue.vfx_id)
                        ? "resolved"
                        : "missing template";
                    EditorGUILayout.LabelField(
                        $"{ownerKind}:{ownerId} | {trigger} | {role} | {anchor} | {attachMode} | {Normalize(cue.vfx_id)} | {lifecycle} | duration={cue.duration_ms}ms{sequence} | sort={cue.sort_order} | {templateStatus}");

                    if (TryBuildCastTimeHandGlowWarning(cue, castTimeMs, out string castCueWarning))
                        EditorGUILayout.HelpBox(castCueWarning, MessageType.Warning);
                    if (hasExpectedHand && TryBuildAnimationHandWarning(cue, expectedHandAnchor, expectedHandReason, out string handWarning))
                        EditorGUILayout.HelpBox(handWarning, MessageType.Warning);
                }
            }

            if (string.Equals(deliveryKind, "PROJECTILE", StringComparison.Ordinal)
                && !_selectedAbilityCues.Any(cue =>
                    string.Equals(Normalize(cue.owner_kind), "ABILITY", StringComparison.Ordinal)
                    && string.Equals(Normalize(cue.owner_id), abilityId, StringComparison.Ordinal)
                    && string.Equals(Normalize(cue.vfx_role), RoleProjectileBody, StringComparison.Ordinal)))
            {
                EditorGUILayout.HelpBox("Projectile spell has no ability-scoped PROJECTILE_BODY cue. It may still resolve through a SPELL fallback, but new spell authoring should add one explicit body cue.", MessageType.Warning);
            }
        }

        private static bool TryBuildCastTimeHandGlowWarning(
            CombatVfxCueDefinition cue,
            int castTimeMs,
            out string warning)
        {
            warning = string.Empty;
            if (castTimeMs <= 0 || !IsHandAttachedSpellCastCue(cue))
                return false;

            string lifecycle = EffectiveLifecycle(cue.lifecycle);
            if (string.Equals(lifecycle, LifecycleUntilReleaseEvent, StringComparison.Ordinal))
                return false;

            string durationDetail = string.Equals(lifecycle, LifecycleDuration, StringComparison.Ordinal)
                ? $" duration_ms={cue.duration_ms}"
                : string.Empty;
            warning = $"Cast-time hand glow should use {LifecycleUntilReleaseEvent} and duration_ms=0. This spell casts for {castTimeMs}ms, but this cue uses {lifecycle}{durationDetail}.";
            return true;
        }

        private static bool TryBuildAnimationHandWarning(
            CombatVfxCueDefinition cue,
            string expectedHandAnchor,
            string expectedHandReason,
            out string warning)
        {
            warning = string.Empty;
            if (!IsHandAttachedSpellCastCue(cue))
                return false;

            string anchor = Normalize(cue.anchor);
            if (string.Equals(anchor, expectedHandAnchor, StringComparison.Ordinal))
                return false;

            warning = $"Authored cue fallback uses {anchor}, but runtime will use {expectedHandAnchor} because {expectedHandReason}.";
            return true;
        }

        private static bool IsHandAttachedSpellCastCue(CombatVfxCueDefinition cue)
        {
            string anchor = Normalize(cue.anchor);
            return string.Equals(Normalize(cue.trigger), TriggerSpellCast, StringComparison.Ordinal)
                && string.Equals(Normalize(cue.attach_mode), AttachModeFollowAnchor, StringComparison.Ordinal)
                && string.Equals(EffectiveRole(cue.vfx_role), RoleAttached, StringComparison.Ordinal)
                && (string.Equals(anchor, AnchorLeftHand, StringComparison.Ordinal)
                    || string.Equals(anchor, AnchorRightHand, StringComparison.Ordinal));
        }

        private static string EffectiveRole(string role)
        {
            string normalized = Normalize(role);
            return string.IsNullOrWhiteSpace(normalized) ? "ONE_SHOT" : normalized;
        }

        private static string EffectiveLifecycle(string lifecycle)
        {
            string normalized = Normalize(lifecycle);
            return string.IsNullOrWhiteSpace(normalized) ? LifecycleDuration : normalized;
        }

        private static bool TryInferSpellPresentationHand(
            WeaponSpellAnimationEntry entry,
            out string expectedHandAnchor,
            out string reason)
        {
            if (entry.EffectiveCastOrigin == SpellCastOrigin.LeftHand)
            {
                expectedHandAnchor = AnchorLeftHand;
                reason = entry.mirrorPresentation
                    ? "the animation recipe's right-hand origin is mirrored"
                    : "the animation recipe authors a left-hand origin";
                return true;
            }

            if (entry.EffectiveCastOrigin == SpellCastOrigin.RightHand)
            {
                expectedHandAnchor = AnchorRightHand;
                reason = entry.mirrorPresentation
                    ? "the animation recipe's left-hand origin is mirrored"
                    : "the animation recipe authors a right-hand origin";
                return true;
            }

            if (entry.playbackLayer == SpellPlaybackLayer.LeftGesture)
            {
                expectedHandAnchor = AnchorLeftHand;
                reason = "playbackLayer is LeftGesture";
                return true;
            }

            if (entry.playbackLayer == SpellPlaybackLayer.RightGesture)
            {
                expectedHandAnchor = AnchorRightHand;
                reason = "playbackLayer is RightGesture";
                return true;
            }

            if (TryInferSpellPresentationHand(entry.ResolveClip(), "cast clip", out expectedHandAnchor, out reason))
                return true;

            expectedHandAnchor = string.Empty;
            reason = string.Empty;
            return false;
        }

        private static bool TryInferSpellPresentationHand(
            AnimationClip? clip,
            string label,
            out string expectedHandAnchor,
            out string reason)
        {
            if (clip != null)
            {
                if (TryInferHandFromText(clip.name, out expectedHandAnchor))
                {
                    reason = $"{label} name contains a clear hand token";
                    return true;
                }

                string path = AssetDatabase.GetAssetPath(clip);
                if (TryInferHandFromText(path, out expectedHandAnchor))
                {
                    reason = $"{label} asset path contains a clear hand token";
                    return true;
                }
            }

            expectedHandAnchor = string.Empty;
            reason = string.Empty;
            return false;
        }

        private static bool TryInferHandFromText(string value, out string expectedHandAnchor)
        {
            if (ContainsSideSuffixToken(value, "_L"))
            {
                expectedHandAnchor = AnchorLeftHand;
                return true;
            }
            if (ContainsSideSuffixToken(value, "_R"))
            {
                expectedHandAnchor = AnchorRightHand;
                return true;
            }

            expectedHandAnchor = string.Empty;
            return false;
        }

        private static bool ContainsSideSuffixToken(string value, string suffix)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            string upper = value.ToUpperInvariant();
            int index = upper.IndexOf(suffix, StringComparison.Ordinal);
            while (index >= 0)
            {
                int afterIndex = index + suffix.Length;
                if (afterIndex >= upper.Length || !char.IsLetterOrDigit(upper[afterIndex]))
                    return true;

                index = upper.IndexOf(suffix, index + 1, StringComparison.Ordinal);
            }

            return false;
        }

        private void DrawSnippetGenerator()
        {
            EditorGUILayout.LabelField("Spell Authoring Snippet Generator", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "This first-pass tool does not write progression_catalog.shared.json. It generates snippets so the catalog remains hand-reviewable until a tested JSON writer exists.",
                MessageType.Info);
            EditorGUILayout.HelpBox(
                "The hand below is only the legacy VFX fallback written into a new-spell snippet. Runtime uses the assigned animation recipe's Cast Origin, including any CombatAnimationSet mirror.",
                MessageType.None);

            if (!_knownTemplateIdsLoaded)
            {
                EditorGUILayout.HelpBox(
                    "VFX template choices are loaded on demand so opening Spell Authoring stays fast. VFX ids remain directly editable below.",
                    MessageType.None);
                if (GUILayout.Button("Load VFX Template Choices", GUILayout.Width(190f)))
                    LoadKnownTemplateIds();
            }

            _draftAbilityId = NormalizeEditorText(EditorGUILayout.TextField("Ability Id", _draftAbilityId));
            _draftSpellId = NormalizeEditorText(EditorGUILayout.TextField("Spell Id", _draftSpellId));
            if (_knownDisciplineIds.Count > 0)
            {
                int disciplineIndex = Mathf.Max(0, _knownDisciplineIds.FindIndex(id =>
                    string.Equals(id, Normalize(_draftDisciplineId), StringComparison.Ordinal)));
                disciplineIndex = EditorGUILayout.Popup(
                    "Discipline",
                    disciplineIndex,
                    _knownDisciplineIds.ToArray());
                _draftDisciplineId = _knownDisciplineIds[disciplineIndex];
            }
            else
            {
                _draftDisciplineId = NormalizeEditorText(
                    EditorGUILayout.TextField("Discipline", _draftDisciplineId));
            }
            _draftCombatProfileId = NormalizeEditorText(EditorGUILayout.TextField("Combat Profile Id", _draftCombatProfileId));
            _draftDisplayName = EditorGUILayout.TextField("Display Name", _draftDisplayName);
            _draftCastVfxId = DrawVfxTemplateField("Cast VFX Id", _draftCastVfxId);
            _draftProjectileVfxId = DrawVfxTemplateField("Projectile VFX Id", _draftProjectileVfxId);
            _draftImpactVfxId = DrawVfxTemplateField("Impact VFX Id", _draftImpactVfxId);
            _includeFallbackCastHandCue = EditorGUILayout.Toggle(
                "Include Fallback Cast Cue",
                _includeFallbackCastHandCue);
            int fallbackHandIndex = _draftFallbackCastOrigin == SpellCastOrigin.RightHand ? 1 : 0;
            fallbackHandIndex = EditorGUILayout.Popup(
                "Legacy VFX Fallback Hand",
                fallbackHandIndex,
                new[] { "Left Hand", "Right Hand" });
            _draftFallbackCastOrigin = fallbackHandIndex == 1
                ? SpellCastOrigin.RightHand
                : SpellCastOrigin.LeftHand;

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Use Selected Ability", GUILayout.Width(150f)))
                    CopySelectedAbilityToDraft();
                if (GUILayout.Button("Generate Projectile Snippet", GUILayout.Width(190f)))
                    _generatedSnippet = BuildProjectileSnippet();
                using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(_generatedSnippet)))
                {
                    if (GUILayout.Button("Copy Snippet", GUILayout.Width(110f)))
                        EditorGUIUtility.systemCopyBuffer = _generatedSnippet;
                }
            }

            if (!string.IsNullOrWhiteSpace(_generatedSnippet))
            {
                EditorGUILayout.LabelField("Generated JSON", EditorStyles.boldLabel);
                _generatedSnippet = EditorGUILayout.TextArea(_generatedSnippet, GUILayout.MinHeight(260f));
            }
        }

        private string DrawVfxTemplateField(string label, string currentValue)
        {
            string normalized = NormalizeEditorText(currentValue);
            if (_knownTemplateIds.Count == 0)
                return NormalizeEditorText(EditorGUILayout.TextField(label, normalized));

            using (new EditorGUILayout.HorizontalScope())
            {
                int currentIndex = _knownTemplateIds.FindIndex(id => string.Equals(id, normalized, StringComparison.Ordinal));
                string[] options = new string[_knownTemplateIds.Count + 1];
                options[0] = "<custom>";
                for (int index = 0; index < _knownTemplateIds.Count; index++)
                    options[index + 1] = _knownTemplateIds[index];

                int popupIndex = currentIndex >= 0 ? currentIndex + 1 : 0;
                int selectedIndex = EditorGUILayout.Popup(label, popupIndex, options, GUILayout.MinWidth(300f));
                if (selectedIndex > 0 && selectedIndex <= _knownTemplateIds.Count)
                    normalized = _knownTemplateIds[selectedIndex - 1];

                normalized = NormalizeEditorText(EditorGUILayout.TextField(normalized, GUILayout.MinWidth(190f)));
            }

            return normalized;
        }

        private void LoadKnownTemplateIds()
        {
            _knownTemplateIds.Clear();
            CombatVFXRegistry? registry = CombatVFXRegistry.LoadShared();
            if (registry != null)
            {
                _knownTemplateIds.AddRange(registry.Entries
                    .Select(entry => Normalize(entry.vfxId))
                    .Where(id => !string.IsNullOrWhiteSpace(id)));
            }

            _knownTemplateIds.AddRange(CombatVFXTemplateRegistry.KnownScriptedTemplateIds
                .Select(Normalize)
                .Where(id => !string.IsNullOrWhiteSpace(id)));
            _knownTemplateIds.Sort(StringComparer.Ordinal);
            for (int index = _knownTemplateIds.Count - 1; index > 0; index--)
            {
                if (string.Equals(
                        _knownTemplateIds[index],
                        _knownTemplateIds[index - 1],
                        StringComparison.Ordinal))
                {
                    _knownTemplateIds.RemoveAt(index);
                }
            }

            _knownTemplateIdsLoaded = true;
        }

        private void EnsureAnimationSetsLoaded()
        {
            if (_animationSetsLoaded)
                return;

            _animationSets = SpellPresentationEditorData.LoadCombatAnimationSets()
                .OrderBy(animationSet => animationSet.name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            _animationSetByProfile.Clear();
            foreach (CombatAnimationSet animationSet in _animationSets)
            {
                string profileId = Normalize(animationSet.CombatProfileIdOrDefault);
                if (!string.IsNullOrWhiteSpace(profileId)
                    && !_animationSetByProfile.ContainsKey(profileId))
                {
                    _animationSetByProfile.Add(profileId, animationSet);
                }
            }

            _animationSetsLoaded = true;
        }

        private static bool TryResolveSpellAnimationEntry(
            CombatAnimationSet animationSet,
            string spellId,
            out WeaponSpellAnimationEntry entry)
        {
            return SpellCastAnimationResolver.TryResolve(animationSet, spellId, out entry);
        }

        private void CopySelectedAbilityToDraft()
        {
            if (_spellAbilities.Count == 0)
                return;

            AbilityDefinition selected = _spellAbilities[Mathf.Clamp(_selectedSpellIndex, 0, _spellAbilities.Count - 1)];
            _draftAbilityId = Normalize(selected.ability_id);
            _draftSpellId = Normalize(selected.action_id);
            _draftDisciplineId = Normalize(selected.discipline_id);
            _draftCombatProfileId = Normalize(selected.combat_profile_id);
            _draftDisplayName = selected.display_name;
        }

        private string BuildProjectileSnippet()
        {
            var builder = new StringBuilder();
            builder.AppendLine("\"abilities\": [");
            builder.AppendLine("  {");
            builder.AppendLine($"    \"ability_id\": \"{Normalize(_draftAbilityId)}\",");
            builder.AppendLine("    \"actor_scope\": \"PLAYER\",");
            builder.AppendLine($"    \"discipline_id\": \"{Normalize(_draftDisciplineId)}\",");
            builder.AppendLine($"    \"combat_profile_id\": \"{Normalize(_draftCombatProfileId)}\",");
            builder.AppendLine($"    \"action_id\": \"{Normalize(_draftSpellId)}\",");
            builder.AppendLine($"    \"display_name\": \"{EscapeJson(_draftDisplayName)}\",");
            builder.AppendLine("    \"resource_kind\": \"MANA\",");
            builder.AppendLine("    \"sort_order\": 1000,");
            builder.AppendLine("    \"gameplay\": {");
            builder.AppendLine("      \"kind\": \"SPELL\",");
            builder.AppendLine("      \"cooldown_ms\": 450,");
            builder.AppendLine("      \"uses_global_cooldown\": true,");
            builder.AppendLine("      \"cast_time_ms\": 0,");
            builder.AppendLine("      \"cast_mobility\": \"MOBILE\",");
            builder.AppendLine("      \"targeting\": \"TARGET\",");
            builder.AppendLine("      \"requires_target\": true,");
            builder.AppendLine("      \"resource_cost\": 0.0,");
            builder.AppendLine("      \"arms_auto_attack_on_cast\": true,");
            builder.AppendLine("      \"delivery\": {");
            builder.AppendLine("        \"kind\": \"PROJECTILE\",");
            builder.AppendLine("        \"speed\": 24.0,");
            builder.AppendLine("        \"max_distance\": 30.0,");
            builder.AppendLine("        \"damage\": 30,");
            builder.AppendLine("        \"spawn_forward\": 1.0,");
            builder.AppendLine("        \"spawn_height\": 1.2,");
            builder.AppendLine("        \"turn_rate\": 3.0,");
            builder.AppendLine("        \"update_interval_seconds\": 0.05,");
            builder.AppendLine("        \"radius\": 0.6,");
            builder.AppendLine("        \"block_behavior\": \"BLOCKABLE\",");
            builder.AppendLine("        \"parry_behavior\": \"PARRYABLE\",");
            builder.AppendLine("        \"homing_window_seconds\": 0.10,");
            builder.AppendLine("        \"impact_effects\": []");
            builder.AppendLine("      }");
            builder.AppendLine("    }");
            builder.AppendLine("  }");
            builder.AppendLine("],");
            builder.AppendLine("\"combat_vfx_cues\": [");

            int sort = 10;
            bool wroteCue = false;
            string fallbackHandAnchor = _draftFallbackCastOrigin == SpellCastOrigin.RightHand
                ? AnchorRightHand
                : AnchorLeftHand;
            if (_includeFallbackCastHandCue)
                AppendCue(builder, ref wroteCue, "SPELL_CAST", fallbackHandAnchor, _draftCastVfxId, "FOLLOW_ANCHOR", "ATTACHED", "UNTIL_RELEASE_EVENT", 0, 0, sort);
            sort += 10;
            AppendCue(builder, ref wroteCue, "SPELL_RELEASE", fallbackHandAnchor, _draftProjectileVfxId, "SPAWN_WORLD", "PROJECTILE_BODY", "UNTIL_TERMINAL_EVENT", 0, 0, sort);
            sort += 10;
            AppendCue(builder, ref wroteCue, "SPELL_IMPACT", "IMPACT_POINT", _draftImpactVfxId, "SPAWN_WORLD", "ONE_SHOT", "DURATION", 0, 1200, sort);

            builder.AppendLine();
            builder.AppendLine("]");
            return builder.ToString();
        }

        private void AppendCue(
            StringBuilder builder,
            ref bool wroteCue,
            string trigger,
            string anchor,
            string vfxId,
            string attachMode,
            string role,
            string lifecycle,
            int projectileSequenceIndex,
            int durationMs,
            int sortOrder)
        {
            if (wroteCue)
                builder.AppendLine(",");
            wroteCue = true;

            builder.AppendLine("  {");
            builder.AppendLine("    \"owner_kind\": \"ABILITY\",");
            builder.AppendLine($"    \"owner_id\": \"{Normalize(_draftAbilityId)}\",");
            builder.AppendLine($"    \"trigger\": \"{trigger}\",");
            builder.AppendLine($"    \"anchor\": \"{anchor}\",");
            builder.AppendLine($"    \"vfx_id\": \"{Normalize(vfxId)}\",");
            builder.AppendLine($"    \"attach_mode\": \"{attachMode}\",");
            builder.AppendLine($"    \"vfx_role\": \"{role}\",");
            builder.AppendLine($"    \"lifecycle\": \"{lifecycle}\",");
            if (role == "PROJECTILE_BODY")
                builder.AppendLine($"    \"projectile_sequence_index\": {projectileSequenceIndex},");
            if (durationMs > 0)
                builder.AppendLine($"    \"duration_ms\": {durationMs},");
            builder.AppendLine($"    \"sort_order\": {sortOrder}");
            builder.Append("  }");
        }

        private void Load()
        {
            DestroyCastAnimationPreview();
            InvalidateGeneratedCueCache();
            _loadErrors.Clear();
            _animationSetByProfile.Clear();
            _spellAbilities.Clear();
            _selectedAbilityCues.Clear();
            _knownTemplateIds.Clear();
            _knownTemplateIdsLoaded = false;
            _knownDisciplineIds.Clear();
            _spellAnimationMap = SpellPresentationEditorData.FindFirstAsset<SpellCastAnimationMap>();
            _spellAnimationCatalog = SpellPresentationEditorData.FindFirstAsset<SpellCastAnimationCatalog>();
            _animationSets = Array.Empty<CombatAnimationSet>();
            _animationSetsLoaded = false;

            string absolutePath = SpellPresentationEditorData.AbsoluteProgressionCatalogPath;
            if (!File.Exists(absolutePath))
            {
                _loadErrors.Add($"Progression catalog not found at '{SpellPresentationEditorData.ProgressionCatalogPath}'.");
                _catalog = null;
                return;
            }

            try
            {
                _catalog = JsonUtility.FromJson<ProgressionCatalogDocument>(File.ReadAllText(absolutePath));
            }
            catch (Exception ex)
            {
                _loadErrors.Add($"Failed to parse '{SpellPresentationEditorData.ProgressionCatalogPath}': {ex.Message}");
                _catalog = null;
                return;
            }

            if (_catalog == null)
            {
                _loadErrors.Add($"Failed to parse '{SpellPresentationEditorData.ProgressionCatalogPath}'.");
                return;
            }

            _knownDisciplineIds.AddRange(_catalog.combat_disciplines
                .OrderBy(discipline => discipline.sort_order)
                .ThenBy(discipline => Normalize(discipline.discipline_id), StringComparer.Ordinal)
                .Select(discipline => Normalize(discipline.discipline_id))
                .Where(id => !string.IsNullOrWhiteSpace(id)));

            _spellAbilities.AddRange(_catalog.abilities
                .Where(ability => string.Equals(Normalize(ability.gameplay.kind), "SPELL", StringComparison.Ordinal))
                .OrderBy(
                    ability => Normalize(ability.ability_id),
                    StringComparer.Ordinal));

            _selectedSpellIndex = Mathf.Clamp(_selectedSpellIndex, 0, Math.Max(0, _spellAbilities.Count - 1));
            if (_spellAbilities.Count > 0)
                CopySelectedAbilityToDraft();
        }

        private static string Normalize(string value)
        {
            return WireIdentifier.Normalize(value);
        }

        private static string NormalizeEditorText(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToUpperInvariant();
        }

        private static string EscapeJson(string value)
        {
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"", StringComparison.Ordinal);
        }

        [Serializable]
        private sealed class ProgressionCatalogDocument
        {
            public List<CombatDisciplineDefinition> combat_disciplines = new();
            public List<AbilityDefinition> abilities = new();
            public List<CombatVfxCueDefinition> combat_vfx_cues = new();
        }

        [Serializable]
        private sealed class CombatDisciplineDefinition
        {
            public string discipline_id = string.Empty;
            public int sort_order = 0;
        }

        [Serializable]
        private sealed class AbilityDefinition
        {
            public string ability_id = string.Empty;
            public string discipline_id = string.Empty;
            public string combat_profile_id = string.Empty;
            public string action_id = string.Empty;
            public string display_name = string.Empty;
            public int sort_order = 0;
            public GameplayDefinition gameplay = new();
        }

        [Serializable]
        private sealed class GameplayDefinition
        {
            public string kind = string.Empty;
            public int cast_time_ms = 0;
            public string targeting = string.Empty;
            public DeliveryDefinition delivery = new();
        }

        [Serializable]
        private sealed class DeliveryDefinition
        {
            public string kind = string.Empty;
            // School tint inputs (design doc §2.3: SCHOOL = vfx_school ?? damage_type ?? profile_default).
            // vfx_school lets presentation schools remain distinct from combat damage types.
            public string vfx_school = string.Empty;
            public string damage_type = string.Empty;
            // Archetype-derivation signals (design doc B.9). Nested objects are left un-initialised so an
            // absent JSON key stays null; presence is also double-guarded by a positive/non-empty proxy
            // field so it is robust whether or not JsonUtility instantiates an absent object.
            public int impact_delay_ms = 0;
            public SkyOriginDefinition? sky_origin;
            public ShapeDefinition? shape;
            public ProjectileDefinition? projectile;
        }

        [Serializable]
        private sealed class SkyOriginDefinition
        {
            public float height = 0f;
        }

        [Serializable]
        private sealed class ShapeDefinition
        {
            public string kind = string.Empty;
        }

        [Serializable]
        private sealed class ProjectileDefinition
        {
            public float speed = 0f;
        }

        [Serializable]
        private sealed class CombatVfxCueDefinition
        {
            public string owner_kind = string.Empty;
            public string owner_id = string.Empty;
            public string slot = string.Empty;
            public string trigger = string.Empty;
            public string anchor = string.Empty;
            public string vfx_id = string.Empty;
            public string attach_mode = string.Empty;
            public string vfx_role = string.Empty;
            public string lifecycle = string.Empty;
            public int projectile_sequence_index = 0;
            public int duration_ms = 0;
            public int sort_order = 0;
        }
    }
}
