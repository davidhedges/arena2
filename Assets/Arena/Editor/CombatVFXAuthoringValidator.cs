#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using Arena.Combat;
using Arena.Presentation;
using Arena.Presentation.VFX;
using UnityEditor;
using UnityEngine;

namespace Arena.Editor
{
    internal static class CombatVFXAuthoringValidator
    {
        private const string ProgressionCatalogPath = "server/src/progression_catalog.shared.json";
        private const float ReleaseTimingToleranceSeconds = 0.05f;
        private const string AnchorLeftHand = "LEFT_HAND";
        private const string AnchorRightHand = "RIGHT_HAND";
        private const string AnchorWeaponMainHand = "WEAPON_MAIN_HAND";
        private const string AnchorWeaponOffHand = "WEAPON_OFF_HAND";
        private const string AnchorWeaponBladeStart = "WEAPON_BLADE_START";
        private const string AnchorWeaponBladeEnd = "WEAPON_BLADE_END";
        private const string TriggerSpellCast = "SPELL_CAST";
        private const string AttachModeFollowAnchor = "FOLLOW_ANCHOR";
        private const string RoleAttached = "ATTACHED";
        private const string RoleProjectileBody = "PROJECTILE_BODY";
        private const string RoleTravelBody = "TRAVEL_BODY";
        private const string LifecycleDuration = "DURATION";
        private const string LifecycleUntilReleaseEvent = "UNTIL_RELEASE_EVENT";
        private const string LifecycleUntilTerminalEvent = "UNTIL_TERMINAL_EVENT";

        [MenuItem("Arena/Combat VFX/Validate Authoring", false, 500)]
        public static void ValidateFromMenu()
        {
            List<string> errors = Validate();
            if (errors.Count == 0)
            {
                Debug.Log("Combat VFX authoring validation passed.");
                EditorUtility.DisplayDialog("Combat VFX Authoring", "Validation passed.", "OK");
                return;
            }

            string summary = string.Join("\n", errors);
            Debug.LogError($"Combat VFX authoring validation failed:\n{summary}");
            EditorUtility.DisplayDialog(
                "Combat VFX Authoring",
                $"Validation failed with {errors.Count} error(s). See Console for details.",
                "OK");
        }

        public static List<string> Validate()
        {
            var errors = new List<string>();
            CombatVFXRegistry? registry = CombatVFXRegistry.LoadShared();
            if (registry == null)
            {
                errors.Add("CombatVFXRegistry asset is missing from Resources/CombatVFX/CombatVFXRegistry.");
            }
            else
            {
                registry.CollectAuthoringErrors(errors);
                ValidateRegistryDoesNotShadowScriptedTemplates(registry, errors);
            }

            ProgressionCatalogDocument? catalog = LoadProgressionCatalog(errors);
            if (catalog == null)
                return errors;

            var unresolvedTemplateIds = new SortedSet<string>(StringComparer.Ordinal);
            var validatedTravelTemplateIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (CombatVfxCueDefinition cue in catalog.combat_vfx_cues)
            {
                string vfxId = WireIdentifier.Normalize(cue.vfx_id);
                if (string.IsNullOrWhiteSpace(vfxId))
                    continue;

                if (!Mathf.Approximately(cue.scale, 0f))
                    errors.Add($"combat_vfx_cues entry '{vfxId}' authors scale in progression_catalog.shared.json; prefab scale belongs in CombatVFXRegistry.");
                if (!CombatVFXTemplateRegistry.CanResolveTemplate(vfxId))
                    unresolvedTemplateIds.Add(vfxId);
                string role = WireIdentifier.Normalize(cue.vfx_role);
                if ((string.Equals(role, RoleProjectileBody, StringComparison.Ordinal)
                        || string.Equals(role, RoleTravelBody, StringComparison.Ordinal))
                    && validatedTravelTemplateIds.Add(vfxId))
                {
                    ValidateTravelTemplateIsVisualOnly(vfxId, errors);
                }
                if (!string.Equals(role, RoleProjectileBody, StringComparison.Ordinal)
                    && !string.Equals(role, RoleTravelBody, StringComparison.Ordinal)
                    && string.Equals(WireIdentifier.Normalize(cue.lifecycle), LifecycleUntilTerminalEvent, StringComparison.Ordinal)
                    && !CombatVFXTemplateRegistry.IsScriptedTemplate(vfxId))
                {
                    errors.Add($"combat_vfx_cues entry '{vfxId}' uses {LifecycleUntilTerminalEvent}, but prefab templates are duration-based. Use a scripted template or a finite DURATION lifecycle.");
                }
                if (string.Equals(WireIdentifier.Normalize(cue.lifecycle), LifecycleUntilReleaseEvent, StringComparison.Ordinal)
                    && !string.Equals(WireIdentifier.Normalize(cue.trigger), TriggerSpellCast, StringComparison.Ordinal))
                {
                    errors.Add($"combat_vfx_cues entry '{vfxId}' uses {LifecycleUntilReleaseEvent}, but that lifecycle is only valid for SPELL_CAST cues.");
                }
            }

            foreach (string vfxId in unresolvedTemplateIds)
                errors.Add($"combat_vfx_cues references vfx_id '{vfxId}', but no prefab or scripted template resolves it.");

            ValidateCastTimeHandCueLifecycles(catalog, errors);
            ValidateSpellAnimationTiming(catalog, errors);
            ValidateCueAnchorContract(catalog, errors);

            return errors;
        }

        private static void ValidateCastTimeHandCueLifecycles(
            ProgressionCatalogDocument catalog,
            List<string> errors)
        {
            var castTimeMsByAbilityId = new Dictionary<string, int>(StringComparer.Ordinal);
            var castTimeMsBySpellId = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (AbilityDefinition ability in catalog.abilities)
            {
                if (!string.Equals(WireIdentifier.Normalize(ability.gameplay.kind), "SPELL", StringComparison.Ordinal))
                    continue;

                int castTimeMs = ability.gameplay.cast_time_ms;
                if (castTimeMs <= 0)
                    continue;

                string abilityId = WireIdentifier.Normalize(ability.ability_id);
                string spellId = WireIdentifier.Normalize(ability.action_id);
                if (!string.IsNullOrWhiteSpace(abilityId))
                    castTimeMsByAbilityId[abilityId] = castTimeMs;
                if (!string.IsNullOrWhiteSpace(spellId)
                    && (!castTimeMsBySpellId.TryGetValue(spellId, out int existingCastTimeMs)
                        || castTimeMs > existingCastTimeMs))
                {
                    castTimeMsBySpellId[spellId] = castTimeMs;
                }
            }

            foreach (CombatVfxCueDefinition cue in catalog.combat_vfx_cues)
            {
                if (!TryResolveCastTimeMs(cue, castTimeMsByAbilityId, castTimeMsBySpellId, out int castTimeMs))
                    continue;
                if (!IsHandAttachedSpellCastCue(cue))
                    continue;

                string lifecycle = EffectiveLifecycle(cue.lifecycle);
                if (string.Equals(lifecycle, LifecycleUntilReleaseEvent, StringComparison.Ordinal))
                    continue;

                string owner = $"{WireIdentifier.Normalize(cue.owner_kind)}:{WireIdentifier.Normalize(cue.owner_id)}";
                string durationDetail = string.Equals(lifecycle, LifecycleDuration, StringComparison.Ordinal)
                    ? $" duration_ms {cue.duration_ms}"
                    : string.Empty;
                errors.Add(
                    $"combat_vfx_cues entry '{cue.vfx_id}' is a hand-attached SPELL_CAST cue for cast-time spell owner '{owner}' (cast_time_ms {castTimeMs}) but uses lifecycle '{lifecycle}'{durationDetail}. Use {LifecycleUntilReleaseEvent} with duration_ms 0 so the hand effect persists until the release event.");
            }
        }

        private static void ValidateTravelTemplateIsVisualOnly(string vfxId, List<string> errors)
        {
            GameObject? prefab = CombatVFXTemplateRegistry.ResolvePrefab(vfxId);
            if (prefab == null)
                return;

            MonoBehaviour? behaviour = prefab.GetComponentInChildren<MonoBehaviour>(true);
            if (behaviour == null)
                return;

            errors.Add(
                $"combat_vfx_cues entry '{vfxId}' uses a projectile/travel VFX template with MonoBehaviour '{behaviour.GetType().Name}' on '{TransformPath(prefab.transform, behaviour.transform)}'. Projectile/travel VFX templates must be visual-only because Arena owns transform and lifetime.");
        }

        private static string TransformPath(Transform root, Transform transform)
        {
            if (transform == root)
                return root.name;

            var parts = new Stack<string>();
            Transform? current = transform;
            while (current != null)
            {
                parts.Push(current.name);
                if (current == root)
                    break;
                current = current.parent;
            }

            return string.Join("/", parts);
        }

        private static bool TryResolveCastTimeMs(
            CombatVfxCueDefinition cue,
            Dictionary<string, int> castTimeMsByAbilityId,
            Dictionary<string, int> castTimeMsBySpellId,
            out int castTimeMs)
        {
            string ownerKind = WireIdentifier.Normalize(cue.owner_kind);
            string ownerId = WireIdentifier.Normalize(cue.owner_id);
            if (string.Equals(ownerKind, "ABILITY", StringComparison.Ordinal))
                return castTimeMsByAbilityId.TryGetValue(ownerId, out castTimeMs);
            if (string.Equals(ownerKind, "SPELL", StringComparison.Ordinal))
                return castTimeMsBySpellId.TryGetValue(ownerId, out castTimeMs);

            castTimeMs = 0;
            return false;
        }

        private static bool IsHandAttachedSpellCastCue(CombatVfxCueDefinition cue)
        {
            string anchor = WireIdentifier.Normalize(cue.anchor);
            return string.Equals(WireIdentifier.Normalize(cue.trigger), TriggerSpellCast, StringComparison.Ordinal)
                && string.Equals(WireIdentifier.Normalize(cue.attach_mode), AttachModeFollowAnchor, StringComparison.Ordinal)
                && string.Equals(WireIdentifier.Normalize(cue.vfx_role), RoleAttached, StringComparison.Ordinal)
                && (string.Equals(anchor, AnchorLeftHand, StringComparison.Ordinal)
                    || string.Equals(anchor, AnchorRightHand, StringComparison.Ordinal));
        }

        private static string EffectiveLifecycle(string lifecycle)
        {
            string normalized = WireIdentifier.Normalize(lifecycle);
            return string.IsNullOrWhiteSpace(normalized) ? LifecycleDuration : normalized;
        }

        private static void ValidateSpellAnimationTiming(
            ProgressionCatalogDocument catalog,
            List<string> errors)
        {
            Dictionary<string, string> combatProfileByClass = BuildCombatProfileByClass(catalog);
            HashSet<string> defaultLoadoutAbilityIds = BuildDefaultLoadoutAbilityIds(catalog);
            Dictionary<string, CombatAnimationSet> animationSetByProfile = LoadAnimationSetsByProfile(errors);

            foreach (AbilityDefinition ability in catalog.abilities)
            {
                string abilityId = WireIdentifier.Normalize(ability.ability_id);
                string actionId = WireIdentifier.Normalize(ability.action_id);
                if (!string.Equals(WireIdentifier.Normalize(ability.gameplay.kind), "SPELL", StringComparison.Ordinal))
                    continue;
                if (!IsSelectableAbility(ability, defaultLoadoutAbilityIds))
                    continue;

                string classId = WireIdentifier.Normalize(ability.class_id);
                if (!combatProfileByClass.TryGetValue(classId, out string combatProfileId)
                    || string.IsNullOrWhiteSpace(combatProfileId))
                {
                    errors.Add($"spell ability '{abilityId}' class '{classId}' does not resolve to a default combat profile.");
                    continue;
                }

                if (!animationSetByProfile.TryGetValue(combatProfileId, out CombatAnimationSet animationSet))
                {
                    errors.Add($"spell ability '{abilityId}' requires combat profile '{combatProfileId}', but no CombatAnimationSet asset resolves for that profile.");
                    continue;
                }

                if (!animationSet.TryGetSpellAnimation(actionId, out WeaponSpellAnimationEntry entry))
                {
                    errors.Add($"spell ability '{abilityId}' action '{actionId}' is selectable but CombatAnimationSet '{animationSet.name}' has no matching spell animation entry.");
                    continue;
                }

                ValidateSpellCueHandAnchors(catalog, errors, abilityId, actionId, animationSet, entry);

                int castTimeMs = ability.gameplay.cast_time_ms;
                if (castTimeMs <= 0)
                    continue;

                if (!animationSet.TryGetDefaultSpellCastHoldProfile(out _))
                {
                    errors.Add($"spell ability '{abilityId}' action '{actionId}' has cast_time_ms {castTimeMs}, but CombatAnimationSet '{animationSet.name}' has no playable default spell cast hold profile.");
                }

                ValidateReleaseTiming(errors, abilityId, actionId, animationSet, entry, castTimeMs, grounded: true);
                ValidateReleaseTiming(errors, abilityId, actionId, animationSet, entry, castTimeMs, grounded: false);
            }
        }

        private static void ValidateSpellCueHandAnchors(
            ProgressionCatalogDocument catalog,
            List<string> errors,
            string abilityId,
            string actionId,
            CombatAnimationSet animationSet,
            WeaponSpellAnimationEntry entry)
        {
            if (!TryInferSpellPresentationHand(entry, out string expectedHandAnchor, out string reason))
                return;

            foreach (CombatVfxCueDefinition cue in catalog.combat_vfx_cues)
            {
                string ownerKind = WireIdentifier.Normalize(cue.owner_kind);
                string ownerId = WireIdentifier.Normalize(cue.owner_id);
                bool matchesSelectedSpell = string.Equals(ownerKind, "ABILITY", StringComparison.Ordinal)
                    && string.Equals(ownerId, abilityId, StringComparison.Ordinal);
                matchesSelectedSpell |= string.Equals(ownerKind, "SPELL", StringComparison.Ordinal)
                    && string.Equals(ownerId, actionId, StringComparison.Ordinal);
                if (!matchesSelectedSpell || !IsHandAttachedSpellCastCue(cue))
                    continue;

                string anchor = WireIdentifier.Normalize(cue.anchor);
                if (string.Equals(anchor, expectedHandAnchor, StringComparison.Ordinal))
                    continue;

                errors.Add(
                    $"spell ability '{abilityId}' action '{actionId}' CombatAnimationSet '{animationSet.name}' implies cast hand '{expectedHandAnchor}' because {reason}, but combat_vfx_cues entry '{cue.vfx_id}' uses anchor '{anchor}'.");
            }
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

        private static void ValidateReleaseTiming(
            List<string> errors,
            string abilityId,
            string actionId,
            CombatAnimationSet animationSet,
            WeaponSpellAnimationEntry entry,
            int castTimeMs,
            bool grounded)
        {
            AnimationClip? clip = entry.ResolveClip(grounded);
            if (clip == null)
                return;

            float castSeconds = castTimeMs / 1000f;
            float releaseOffsetSeconds = entry.ResolveReleaseOffsetSeconds(grounded);
            if (releaseOffsetSeconds <= castSeconds + ReleaseTimingToleranceSeconds)
                return;

            string stance = grounded ? "ground" : "air";
            errors.Add(
                $"spell ability '{abilityId}' action '{actionId}' {stance} release offset in CombatAnimationSet '{animationSet.name}' is {releaseOffsetSeconds:0.000}s, but gameplay.cast_time_ms is {castSeconds:0.000}s. The release offset must fit inside the cast time. Tolerance is {ReleaseTimingToleranceSeconds:0.000}s.");
        }

        private static Dictionary<string, string> BuildCombatProfileByClass(ProgressionCatalogDocument catalog)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (ClassDefinition definition in catalog.classes)
            {
                string classId = WireIdentifier.Normalize(definition.class_id);
                if (string.IsNullOrWhiteSpace(classId))
                    continue;

                result[classId] = WireIdentifier.Normalize(definition.default_combat_profile_id);
            }

            return result;
        }

        private static HashSet<string> BuildDefaultLoadoutAbilityIds(ProgressionCatalogDocument catalog)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            foreach (DefaultLoadoutAssignmentDefinition assignment in catalog.default_loadout_assignments)
            {
                string abilityId = WireIdentifier.Normalize(assignment.ability_id);
                if (!string.IsNullOrWhiteSpace(abilityId))
                    result.Add(abilityId);
            }

            return result;
        }

        private static Dictionary<string, CombatAnimationSet> LoadAnimationSetsByProfile(List<string> errors)
        {
            var result = new Dictionary<string, CombatAnimationSet>(StringComparer.Ordinal);
            foreach (CombatAnimationSet animationSet in Resources.LoadAll<CombatAnimationSet>("CombatAnimationSets"))
            {
                string combatProfileId = WireIdentifier.Normalize(animationSet.CombatProfileIdOrDefault);
                if (string.IsNullOrWhiteSpace(combatProfileId))
                {
                    errors.Add($"CombatAnimationSet asset '{animationSet.name}' has no combat profile id.");
                    continue;
                }

                if (result.ContainsKey(combatProfileId))
                    errors.Add($"Multiple CombatAnimationSet assets resolve to combat profile '{combatProfileId}'.");
                else
                    result.Add(combatProfileId, animationSet);
            }

            return result;
        }

        private static bool IsSelectableAbility(
            AbilityDefinition ability,
            HashSet<string> defaultLoadoutAbilityIds)
        {
            string abilityId = WireIdentifier.Normalize(ability.ability_id);
            if (defaultLoadoutAbilityIds.Contains(abilityId))
                return true;
            if (!string.IsNullOrWhiteSpace(WireIdentifier.Normalize(ability.fixed_action_id)))
                return true;

            foreach (string tag in ability.ability_tags)
            {
                if (string.Equals(WireIdentifier.Normalize(tag), "LOADOUT_ACTION", StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        private static void ValidateCueAnchorContract(
            ProgressionCatalogDocument catalog,
            List<string> errors)
        {
            bool requiresLeftHand = false;
            bool requiresRightHand = false;
            bool requiresMainWeapon = false;
            bool requiresOffWeapon = false;
            bool requiresBladeMarkers = false;

            foreach (CombatVfxCueDefinition cue in catalog.combat_vfx_cues)
            {
                string anchor = WireIdentifier.Normalize(cue.anchor);
                requiresLeftHand |= string.Equals(anchor, AnchorLeftHand, StringComparison.Ordinal);
                requiresRightHand |= string.Equals(anchor, AnchorRightHand, StringComparison.Ordinal);
                requiresMainWeapon |= string.Equals(anchor, AnchorWeaponMainHand, StringComparison.Ordinal)
                    || string.Equals(anchor, AnchorWeaponBladeStart, StringComparison.Ordinal)
                    || string.Equals(anchor, AnchorWeaponBladeEnd, StringComparison.Ordinal);
                requiresOffWeapon |= string.Equals(anchor, AnchorWeaponOffHand, StringComparison.Ordinal);
                requiresBladeMarkers |= string.Equals(anchor, AnchorWeaponBladeStart, StringComparison.Ordinal)
                    || string.Equals(anchor, AnchorWeaponBladeEnd, StringComparison.Ordinal);
            }

            if (!requiresLeftHand
                && !requiresRightHand
                && !requiresMainWeapon
                && !requiresOffWeapon
                && !requiresBladeMarkers)
            {
                return;
            }

            GameObject? runtimeAvatarPrefab = RuntimeAvatarPrefabResolver.LoadRuntimePlayerPrefab();
            if (runtimeAvatarPrefab == null)
            {
                errors.Add("combat_vfx_cues require hand or weapon anchors, but no runtime player prefab resolves from Resources.");
                return;
            }

            Animator? animator = runtimeAvatarPrefab.GetComponentInChildren<Animator>(true);
            if ((requiresLeftHand || requiresRightHand) && animator == null)
            {
                errors.Add($"runtime avatar prefab '{runtimeAvatarPrefab.name}' has no Animator, but combat_vfx_cues require hand anchors.");
            }
            else if (animator != null)
            {
                if (requiresLeftHand && !TryGetHumanoidBone(animator, HumanBodyBones.LeftHand, out _))
                    errors.Add($"runtime avatar prefab '{runtimeAvatarPrefab.name}' does not resolve humanoid bone LeftHand required by combat_vfx_cues.");
                if (requiresRightHand && !TryGetHumanoidBone(animator, HumanBodyBones.RightHand, out _))
                    errors.Add($"runtime avatar prefab '{runtimeAvatarPrefab.name}' does not resolve humanoid bone RightHand required by combat_vfx_cues.");
            }

            AvatarWeaponMounts? mounts = runtimeAvatarPrefab.GetComponentInChildren<AvatarWeaponMounts>(true);
            if ((requiresMainWeapon || requiresOffWeapon) && mounts == null)
            {
                errors.Add($"runtime avatar prefab '{runtimeAvatarPrefab.name}' has no {nameof(AvatarWeaponMounts)}, but combat_vfx_cues require weapon anchors.");
                return;
            }

            if (mounts != null)
            {
                if (requiresMainWeapon && !mounts.TryGetMount(AvatarWeaponMounts.MainHandMountId, out _))
                    errors.Add($"runtime avatar prefab '{runtimeAvatarPrefab.name}' does not resolve weapon mount '{AvatarWeaponMounts.MainHandMountId}' required by combat_vfx_cues.");
                if (requiresOffWeapon && !mounts.TryGetMount(AvatarWeaponMounts.OffHandMountId, out _))
                    errors.Add($"runtime avatar prefab '{runtimeAvatarPrefab.name}' does not resolve weapon mount '{AvatarWeaponMounts.OffHandMountId}' required by combat_vfx_cues.");
            }

            if (requiresBladeMarkers)
            {
                foreach (CombatAnimationSet animationSet in Resources.LoadAll<CombatAnimationSet>("CombatAnimationSets"))
                    ValidateBladeMarkers(animationSet, errors);
            }
        }

        private static bool TryGetHumanoidBone(
            Animator animator,
            HumanBodyBones bone,
            out Transform transform)
        {
            transform = null!;
            try
            {
                transform = animator.GetBoneTransform(bone);
                return transform != null;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        private static void ValidateBladeMarkers(CombatAnimationSet animationSet, List<string> errors)
        {
            foreach (WeaponVisualBinding binding in animationSet.VisualBindings)
            {
                if (binding.prefab == null)
                    continue;

                string drawnMountId = AvatarWeaponMounts.CanonicalizeMountId(binding.drawnMountId);
                if (!string.Equals(drawnMountId, AvatarWeaponMounts.MainHandMountId, StringComparison.Ordinal))
                    continue;

                if (FindDescendant(binding.prefab.transform, "ArenaVFX_BladeStart") == null)
                    errors.Add($"CombatAnimationSet '{animationSet.name}' visual '{binding.prefab.name}' is drawn on '{AvatarWeaponMounts.MainHandMountId}' but is missing blade VFX marker 'ArenaVFX_BladeStart'.");
                if (FindDescendant(binding.prefab.transform, "ArenaVFX_BladeEnd") == null)
                    errors.Add($"CombatAnimationSet '{animationSet.name}' visual '{binding.prefab.name}' is drawn on '{AvatarWeaponMounts.MainHandMountId}' but is missing blade VFX marker 'ArenaVFX_BladeEnd'.");
            }
        }

        private static Transform? FindDescendant(Transform root, string name)
        {
            if (string.Equals(root.name, name, StringComparison.Ordinal))
                return root;

            for (int index = 0; index < root.childCount; index++)
            {
                Transform? result = FindDescendant(root.GetChild(index), name);
                if (result != null)
                    return result;
            }

            return null;
        }

        private static void ValidateRegistryDoesNotShadowScriptedTemplates(
            CombatVFXRegistry registry,
            List<string> errors)
        {
            var scriptedIds = new HashSet<string>(
                CombatVFXTemplateRegistry.KnownScriptedTemplateIds,
                StringComparer.Ordinal);
            foreach (CombatVFXRegistry.Entry entry in registry.Entries)
            {
                string vfxId = WireIdentifier.Normalize(entry.vfxId);
                if (string.IsNullOrWhiteSpace(vfxId))
                    continue;

                if (scriptedIds.Contains(vfxId))
                    errors.Add($"CombatVFXRegistry entry '{entry.vfxId}' shadows scripted combat VFX template '{vfxId}'. Use either a prefab registry entry or a scripted template id, not both.");
            }
        }

        private static ProgressionCatalogDocument? LoadProgressionCatalog(List<string> errors)
        {
            string absolutePath = Path.Combine(Directory.GetCurrentDirectory(), ProgressionCatalogPath);
            if (!File.Exists(absolutePath))
            {
                errors.Add($"Progression catalog not found at '{ProgressionCatalogPath}'.");
                return null;
            }

            try
            {
                string json = File.ReadAllText(absolutePath);
                return JsonUtility.FromJson<ProgressionCatalogDocument>(json);
            }
            catch (Exception ex)
            {
                errors.Add($"Failed to parse '{ProgressionCatalogPath}': {ex.Message}");
                return null;
            }
        }

        [Serializable]
        private sealed class ProgressionCatalogDocument
        {
            public List<ClassDefinition> classes = new();
            public List<AbilityDefinition> abilities = new();
            public List<CombatVfxCueDefinition> combat_vfx_cues = new();
            public List<DefaultLoadoutAssignmentDefinition> default_loadout_assignments = new();
        }

        [Serializable]
        private sealed class ClassDefinition
        {
            public string class_id = string.Empty;
            public string default_combat_profile_id = string.Empty;
        }

        [Serializable]
        private sealed class AbilityDefinition
        {
            public string ability_id = string.Empty;
            public string class_id = string.Empty;
            public string action_id = string.Empty;
            public string fixed_action_id = string.Empty;
            public List<string> ability_tags = new();
            public GameplayDefinition gameplay = new();
        }

        [Serializable]
        private sealed class GameplayDefinition
        {
            public string kind = string.Empty;
            public int cast_time_ms = 0;
        }

        [Serializable]
        private sealed class CombatVfxCueDefinition
        {
            public string owner_kind = string.Empty;
            public string owner_id = string.Empty;
            public string vfx_id = string.Empty;
            public string trigger = string.Empty;
            public string anchor = string.Empty;
            public string attach_mode = string.Empty;
            public string lifecycle = string.Empty;
            public string vfx_role = string.Empty;
            public int duration_ms = 0;
            public float scale;
        }

        [Serializable]
        private sealed class DefaultLoadoutAssignmentDefinition
        {
            public string ability_id = string.Empty;
        }
    }
}
