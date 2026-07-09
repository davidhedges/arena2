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

        private ProgressionCatalogDocument? _catalog;
        private Vector2 _scroll;
        private int _selectedSpellIndex;
        private string _draftAbilityId = "WARRIOR_NEW_SPELL";
        private string _draftSpellId = "NEW_SPELL";
        private string _draftCombatProfileId = "TWO_HANDED_SWORD";
        private string _draftDisplayName = "New Spell";
        private string _draftCastVfxId = "VFX_CAST_HAND_01";
        private string _draftProjectileVfxId = "VFX_PROJECTILE_01";
        private string _draftImpactVfxId = "VFX_HIT_01";
        private bool _includeLeftHandCastCue = true;
        private bool _includeRightHandCastCue;
        private string _generatedSnippet = string.Empty;

        [MenuItem("Arena/Spell Authoring/Open Spell Authoring", false, 490)]
        public static void Open()
        {
            var window = GetWindow<SpellAuthoringWindow>("Spell Authoring");
            window.minSize = new Vector2(680f, 620f);
            window.Load();
        }

        private void OnEnable()
        {
            Load();
        }

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
            string combatProfileId = Normalize(selected.combat_profile_id);
            string deliveryKind = Normalize(selected.gameplay.delivery.kind);
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
                EditorGUILayout.TextField("Combat Profile", combatProfileId);
                EditorGUILayout.TextField("Display Name", selected.display_name);
                EditorGUILayout.IntField("Cast Time Ms", selected.gameplay.cast_time_ms);
                EditorGUILayout.TextField("Delivery", deliveryKind);
            }

            if (string.IsNullOrWhiteSpace(combatProfileId))
            {
                // Profile-less shared SPELL_* abilities (combat_profile_id: "") are expected, not an error:
                // they have no per-weapon CombatAnimationSet and resolve via the weapon-agnostic archetype
                // template; their VFX cues resolve by ability id (design doc §6.10). No animation-set lookup
                // to do, so fall through to the cue audit / generator preview below.
                EditorGUILayout.HelpBox(
                    $"Ability '{abilityId}' is a profile-less shared spell (no combat_profile_id). It has no "
                    + "per-weapon animation set — animation resolves via the weapon-agnostic archetype template, "
                    + "and cues resolve by ability id. Expected for shared SPELL_* elementals (design doc §6.10).",
                    MessageType.Info);
            }
            else if (!_animationSetByProfile.TryGetValue(combatProfileId, out CombatAnimationSet animationSet))
            {
                EditorGUILayout.HelpBox($"No CombatAnimationSet found for combat profile '{combatProfileId}'.", MessageType.Error);
            }
            else
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.ObjectField("Animation Set", animationSet, typeof(CombatAnimationSet), false);
                    if (GUILayout.Button("Select", GUILayout.Width(70f)))
                        Selection.activeObject = animationSet;
                }

                if (TryResolveSpellAnimationEntry(animationSet, spellId, out WeaponSpellAnimationEntry entry))
                {
                    hasResolvedAnimation = true;
                    resolvedAnimation = entry;
                    if (entry.ResolveClip(grounded: true) != null)
                    {
                        EditorGUILayout.HelpBox($"Animation resolves for '{spellId}'. Ground clip assigned.", MessageType.Info);
                    }
                    else
                    {
                        EditorGUILayout.HelpBox($"Animation resolves for '{spellId}', but no ground or fallback clip is assigned yet.", MessageType.Warning);
                    }
                }
                else
                {
                    EditorGUILayout.HelpBox($"No explicit or map-composed spell animation resolves for '{spellId}'. Add it under Spell Actions > Spells or SpellCastAnimationMap.", MessageType.Warning);
                    if (GUILayout.Button("Add Missing Animation Entry", GUILayout.Width(210f)))
                        AddMissingSpellAnimationEntry(animationSet, spellId);
                }
            }

            DrawCueAudit(abilityId, deliveryKind, selected.gameplay.cast_time_ms, hasResolvedAnimation, resolvedAnimation);
            EditorGUILayout.Space(12f);
            DrawGeneratedCuePreview(selected, abilityId, hasResolvedAnimation, resolvedAnimation);
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
                    string sequence = role == RoleProjectileBody
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

            warning = $"Cast hand cue uses {anchor}, but the CombatAnimationSet implies {expectedHandAnchor} because {expectedHandReason}.";
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
            if (entry.playbackLayer == SpellPlaybackLayer.LeftGesture)
            {
                expectedHandAnchor = AnchorLeftHand;
                reason = "playbackLayer is LeftGesture";
                return true;
            }

            if (TryInferSpellPresentationHand(entry.ResolveClip(grounded: true), "ground clip", out expectedHandAnchor, out reason))
                return true;
            if (TryInferSpellPresentationHand(entry.ResolveClip(grounded: false), "air clip", out expectedHandAnchor, out reason))
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

            _draftAbilityId = NormalizeEditorText(EditorGUILayout.TextField("Ability Id", _draftAbilityId));
            _draftSpellId = NormalizeEditorText(EditorGUILayout.TextField("Spell Id", _draftSpellId));
            _draftCombatProfileId = NormalizeEditorText(EditorGUILayout.TextField("Combat Profile Id", _draftCombatProfileId));
            _draftDisplayName = EditorGUILayout.TextField("Display Name", _draftDisplayName);
            _draftCastVfxId = DrawVfxTemplateField("Cast VFX Id", _draftCastVfxId);
            _draftProjectileVfxId = DrawVfxTemplateField("Projectile VFX Id", _draftProjectileVfxId);
            _draftImpactVfxId = DrawVfxTemplateField("Impact VFX Id", _draftImpactVfxId);
            _includeLeftHandCastCue = EditorGUILayout.Toggle("Left Hand Cast Cue", _includeLeftHandCastCue);
            _includeRightHandCastCue = EditorGUILayout.Toggle("Right Hand Cast Cue", _includeRightHandCastCue);

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

        private static bool TryResolveSpellAnimationEntry(
            CombatAnimationSet animationSet,
            string spellId,
            out WeaponSpellAnimationEntry entry)
        {
            return SpellCastAnimationResolver.TryResolve(animationSet, spellId, out entry);
        }

        private static void AddMissingSpellAnimationEntry(CombatAnimationSet animationSet, string spellId)
        {
            string normalizedSpellId = Normalize(spellId);
            if (string.IsNullOrWhiteSpace(normalizedSpellId))
                return;

            List<WeaponSpellAnimationEntry> spells = animationSet.spells?.ToList() ?? new List<WeaponSpellAnimationEntry>();
            if (spells.Any(entry => string.Equals(entry.SpellIdOrEmpty, normalizedSpellId, StringComparison.Ordinal)))
                return;

            Undo.RecordObject(animationSet, "Add Spell Animation Entry");
            spells.Add(new WeaponSpellAnimationEntry
            {
                spellId = normalizedSpellId,
                requiresCombatStance = true,
                combatEntryMode = CombatEntryMode.AnimatedAfterCast,
                playbackLayer = SpellPlaybackLayer.UpperBody,
                groundEffectTime = 0f,
                airEffectTime = 0f,
                lowerBodyUnlockAtSeconds = 0f,
                lowerBodyBlendOutSeconds = 0f,
                visualInterruptibleAtSeconds = 0f,
            });
            animationSet.spells = spells
                .OrderBy(entry => entry.SpellIdOrEmpty, StringComparer.Ordinal)
                .ToArray();
            EditorUtility.SetDirty(animationSet);
            AssetDatabase.SaveAssetIfDirty(animationSet);
            Selection.activeObject = animationSet;
        }

        private void CopySelectedAbilityToDraft()
        {
            if (_spellAbilities.Count == 0)
                return;

            AbilityDefinition selected = _spellAbilities[Mathf.Clamp(_selectedSpellIndex, 0, _spellAbilities.Count - 1)];
            _draftAbilityId = Normalize(selected.ability_id);
            _draftSpellId = Normalize(selected.action_id);
            _draftCombatProfileId = Normalize(selected.combat_profile_id);
            _draftDisplayName = selected.display_name;
        }

        private string BuildProjectileSnippet()
        {
            var builder = new StringBuilder();
            builder.AppendLine("\"abilities\": [");
            builder.AppendLine("  {");
            builder.AppendLine($"    \"ability_id\": \"{Normalize(_draftAbilityId)}\",");
            builder.AppendLine($"    \"combat_profile_id\": \"{Normalize(_draftCombatProfileId)}\",");
            builder.AppendLine($"    \"action_id\": \"{Normalize(_draftSpellId)}\",");
            builder.AppendLine($"    \"display_name\": \"{EscapeJson(_draftDisplayName)}\",");
            builder.AppendLine("    \"resource_kind\": \"RAGE\",");
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
            if (_includeLeftHandCastCue)
                AppendCue(builder, ref wroteCue, "SPELL_CAST", "LEFT_HAND", _draftCastVfxId, "FOLLOW_ANCHOR", "ATTACHED", "UNTIL_RELEASE_EVENT", 0, 0, sort);
            sort += 10;
            if (_includeRightHandCastCue)
                AppendCue(builder, ref wroteCue, "SPELL_CAST", "RIGHT_HAND", _draftCastVfxId, "FOLLOW_ANCHOR", "ATTACHED", "UNTIL_RELEASE_EVENT", 0, 0, sort);
            sort += 10;
            AppendCue(builder, ref wroteCue, "SPELL_RELEASE", "RIGHT_HAND", _draftProjectileVfxId, "SPAWN_WORLD", "PROJECTILE_BODY", "UNTIL_TERMINAL_EVENT", 0, 0, sort);
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
            _loadErrors.Clear();
            _animationSetByProfile.Clear();
            _spellAbilities.Clear();
            _selectedAbilityCues.Clear();
            _knownTemplateIds.Clear();

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

            foreach (CombatAnimationSet animationSet in SpellPresentationEditorData.LoadCombatAnimationSets())
            {
                string profileId = Normalize(animationSet.CombatProfileIdOrDefault);
                if (!string.IsNullOrWhiteSpace(profileId) && !_animationSetByProfile.ContainsKey(profileId))
                    _animationSetByProfile.Add(profileId, animationSet);
            }

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
                if (string.Equals(_knownTemplateIds[index], _knownTemplateIds[index - 1], StringComparison.Ordinal))
                    _knownTemplateIds.RemoveAt(index);
            }

            _spellAbilities.AddRange(_catalog.abilities
                .Where(ability => string.Equals(Normalize(ability.gameplay.kind), "SPELL", StringComparison.Ordinal))
                .OrderBy(ability => ability.sort_order)
                .ThenBy(ability => Normalize(ability.ability_id)));

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
            public List<AbilityDefinition> abilities = new();
            public List<CombatVfxCueDefinition> combat_vfx_cues = new();
        }

        [Serializable]
        private sealed class AbilityDefinition
        {
            public string ability_id = string.Empty;
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
            // vfx_school is the planned override field — not authored anywhere in the catalog yet, so it
            // stays empty and damage_type wins today.
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
