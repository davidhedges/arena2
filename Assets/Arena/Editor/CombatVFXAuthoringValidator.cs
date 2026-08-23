#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Arena.Combat;
using Arena.Presentation;
using Arena.Presentation.VFX;
using UnityEditor;
using UnityEngine;

namespace Arena.Editor
{
    internal static class CombatVFXAuthoringValidator
    {
        private const float ReleaseTimingToleranceSeconds = 0.05f;
        private const string AnchorLeftHand = "LEFT_HAND";
        private const string AnchorRightHand = "RIGHT_HAND";
        private const string AnchorTargetBack = "TARGET_BACK";
        private const string AnchorWeaponMainHand = "WEAPON_MAIN_HAND";
        private const string AnchorWeaponOffHand = "WEAPON_OFF_HAND";
        private const string AnchorWeaponBladeStart = "WEAPON_BLADE_START";
        private const string AnchorWeaponBladeEnd = "WEAPON_BLADE_END";
        private const string TriggerSpellCast = "SPELL_CAST";
        private const string AttachModeFollowAnchor = "FOLLOW_ANCHOR";
        private const string RoleAttached = "ATTACHED";
        private const string RoleProjectileBody = "PROJECTILE_BODY";
        private const string RoleProjectileTrail = "PROJECTILE_TRAIL";
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
                        || string.Equals(role, RoleProjectileTrail, StringComparison.Ordinal)
                        || string.Equals(role, RoleTravelBody, StringComparison.Ordinal))
                    && validatedTravelTemplateIds.Add(vfxId))
                {
                    ValidateTravelTemplateIsVisualOnly(vfxId, errors);
                }
                if (!string.Equals(role, RoleProjectileBody, StringComparison.Ordinal)
                    && !string.Equals(role, RoleProjectileTrail, StringComparison.Ordinal)
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
            ValidateSpellCastAnimationMap(catalog, errors);
            ValidateCueAnchorContract(catalog, errors);
            ValidateAnimationVfxSlots(errors);
            ValidateGreatswordCombatAnimationEvents(errors);

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
            HashSet<string> actionBarDefaultAbilityIds = BuildCombatProfileActionBarDefaultAbilityIds(catalog);
            Dictionary<string, CombatAnimationSet> animationSetByProfile = LoadAnimationSetsByProfile(errors);

            foreach (AbilityDefinition ability in catalog.abilities)
            {
                string abilityId = WireIdentifier.Normalize(ability.ability_id);
                string actionId = WireIdentifier.Normalize(ability.action_id);
                if (!string.Equals(WireIdentifier.Normalize(ability.gameplay.kind), "SPELL", StringComparison.Ordinal))
                    continue;
                if (!IsSelectableAbility(ability, actionBarDefaultAbilityIds))
                    continue;

                string combatProfileId = WireIdentifier.Normalize(ability.combat_profile_id);
                if (string.IsNullOrWhiteSpace(combatProfileId))
                    continue;

                if (!animationSetByProfile.TryGetValue(combatProfileId, out CombatAnimationSet animationSet))
                {
                    errors.Add($"spell ability '{abilityId}' requires combat profile '{combatProfileId}', but no CombatAnimationSet asset resolves for that profile.");
                    continue;
                }

                SpellAnimationArchetype archetype = DeriveSpellAnimationArchetype(ability);
                if (SpellCastAnimationResolver.IsExplicitlyNoAnimation(actionId))
                    continue;

                if (!SpellCastAnimationResolver.TryResolve(animationSet, actionId, archetype, out WeaponSpellAnimationEntry entry))
                {
                    errors.Add($"spell ability '{abilityId}' action '{actionId}' is selectable but its cast-motion/fixed assignment does not resolve in CombatAnimationSet '{animationSet.name}'.");
                    continue;
                }

                ValidateSpellCueHandAnchors(catalog, errors, abilityId, actionId, animationSet, entry);

                if (archetype == SpellAnimationArchetype.Instant && entry.PlaysReleasePresentation)
                    ValidateInstantCastStartupTrim(errors, abilityId, actionId, animationSet, entry);

                int castTimeMs = ability.gameplay.cast_time_ms;
                if (castTimeMs <= 0)
                    continue;

                SpellCastHoldProfile defaultHold = animationSet.defaultSpellCastHold;
                if (entry.UsesHoldPresentation
                    && !entry.TryResolveHoldProfile(defaultHold, out _))
                {
                    errors.Add($"spell ability '{abilityId}' action '{actionId}' uses {entry.presentationMode}, but neither its recipe nor CombatAnimationSet '{animationSet.name}' supplies a playable hold profile.");
                }

                if (entry.PlaysReleasePresentation)
                    ValidateReleaseTiming(errors, abilityId, actionId, animationSet, entry, castTimeMs);
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
            // Animation-owned origins deliberately supersede concrete LEFT_HAND/RIGHT_HAND cue
            // anchors at runtime, including per-set mirroring. The cue remains a compatibility
            // fallback for casters without a resolved player animation set.
            if (entry.HasAuthoredCastOrigin)
                return;

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

        private static void ValidateReleaseTiming(
            List<string> errors,
            string abilityId,
            string actionId,
            CombatAnimationSet animationSet,
            WeaponSpellAnimationEntry entry,
            int castTimeMs)
        {
            AnimationClip? clip = entry.ResolveClip();
            if (clip == null)
                return;

            if (!TryGetEventTime(clip, CombatAnimationEvents.OnReleaseFrame, out float releaseOffsetSeconds))
            {
                errors.Add(
                    $"spell ability '{abilityId}' action '{actionId}' release clip '{ClipLabel(clip)}' in CombatAnimationSet '{animationSet.name}' is missing required event {CombatAnimationEvents.OnReleaseFrame}.");
                return;
            }

            float castSeconds = castTimeMs / 1000f;
            if (releaseOffsetSeconds <= castSeconds + ReleaseTimingToleranceSeconds)
                return;

            errors.Add(
                $"spell ability '{abilityId}' action '{actionId}' release offset in CombatAnimationSet '{animationSet.name}' is {releaseOffsetSeconds:0.000}s, but gameplay.cast_time_ms is {castSeconds:0.000}s. The release offset must fit inside the cast time. Tolerance is {ReleaseTimingToleranceSeconds:0.000}s.");
        }

        private static void ValidateInstantCastStartupTrim(
            List<string> errors,
            string abilityId,
            string actionId,
            CombatAnimationSet animationSet,
            WeaponSpellAnimationEntry entry)
        {
            var clips = new List<(AnimationClip Clip, string Label)>();
            AddUniqueClip(clips, entry.clip, "cast");
            foreach ((AnimationClip clip, string label) in clips)
            {
                AnimationEvent[] trimEvents = clip.events
                    .Where(animationEvent => string.Equals(
                        animationEvent.functionName,
                        CombatAnimationEvents.OnInstantCastStart,
                        StringComparison.Ordinal))
                    .OrderBy(animationEvent => animationEvent.time)
                    .ToArray();
                if (trimEvents.Length == 0)
                    continue;

                string context =
                    $"instant spell ability '{abilityId}' action '{actionId}' {label} release clip '{ClipLabel(clip)}' in CombatAnimationSet '{animationSet.name}'";
                if (trimEvents.Length > 1)
                {
                    errors.Add(
                        $"{context} has {trimEvents.Length} {CombatAnimationEvents.OnInstantCastStart} events; author exactly one startup marker.");
                }

                if (!TryGetEventTime(clip, CombatAnimationEvents.OnReleaseFrame, out float releaseOffsetSeconds))
                {
                    errors.Add(
                        $"{context} uses {CombatAnimationEvents.OnInstantCastStart} but is missing required event {CombatAnimationEvents.OnReleaseFrame}.");
                    continue;
                }

                float trimSeconds = trimEvents[0].time;
                if (trimSeconds > releaseOffsetSeconds + 0.0001f)
                {
                    errors.Add(
                        $"{context} starts at {trimSeconds:0.000}s, after {CombatAnimationEvents.OnReleaseFrame} at {releaseOffsetSeconds:0.000}s. Instant startup trim must not skip the visible release pose.");
                }
            }
        }

        private static void ValidateGreatswordCombatAnimationEvents(List<string> errors)
        {
            foreach (CombatAnimationSet animationSet in Resources.LoadAll<CombatAnimationSet>("CombatAnimationSets"))
            {
                if (!string.Equals(animationSet.CombatProfileIdOrDefault, "TWO_HANDED_SWORD", StringComparison.Ordinal))
                    continue;

                ValidateGreatswordMeleeEvents(animationSet, errors);
                ValidateGreatswordStaggerEvents(animationSet, errors);
            }
        }

        private static void ValidateAnimationVfxSlots(List<string> errors)
        {
            foreach (CombatAnimationSet animationSet in Resources.LoadAll<CombatAnimationSet>("CombatAnimationSets"))
            {
                var trackKeys = new HashSet<string>(StringComparer.Ordinal);
                if (animationSet.animationVfxTracks != null)
                {
                    for (int trackIndex = 0; trackIndex < animationSet.animationVfxTracks.Count; trackIndex++)
                    {
                        CombatAnimationVfxTrack? track = animationSet.animationVfxTracks[trackIndex];
                        string context = $"CombatAnimationSet '{animationSet.name}' animation VFX track {trackIndex + 1}";
                        if (track == null)
                        {
                            errors.Add($"{context} is null.");
                            continue;
                        }

                        if (track.clip == null)
                        {
                            errors.Add($"{context} has no clip.");
                            continue;
                        }

                        string slotId = track.NormalizedSlotId;
                        if (string.IsNullOrEmpty(slotId))
                            errors.Add($"{context} on clip '{ClipLabel(track.clip)}' has an empty Slot Id.");
                        else if (!trackKeys.Add(
                            $"{AssetDatabase.GetAssetPath(track.clip)}:{track.clip.name}:{slotId}"))
                            errors.Add($"{context} duplicates Clip + Slot Id '{track.clip.name} + {slotId}'.");

                        if (track.startTimeSeconds < 0f
                            || track.startTimeSeconds > track.clip.length + 0.0001f)
                        {
                            errors.Add(
                                $"{context} start time {track.startTimeSeconds:0.000}s is outside clip '{track.clip.name}' ({track.clip.length:0.000}s).");
                        }

                        if (track.endTimeSeconds > track.clip.length + 0.0001f)
                        {
                            errors.Add(
                                $"{context} end time {track.endTimeSeconds:0.000}s exceeds clip '{track.clip.name}' ({track.clip.length:0.000}s).");
                        }

                        if (Mathf.Approximately(track.localScale.x, 0f)
                            || Mathf.Approximately(track.localScale.y, 0f)
                            || Mathf.Approximately(track.localScale.z, 0f))
                        {
                            errors.Add($"{context} local scale components must be non-zero.");
                        }

                        ValidateAnimationVfxBladeMarker(animationSet, track, errors);
                    }
                }

                if (animationSet.meleeAttacks == null)
                    continue;

                for (int attackIndex = 0; attackIndex < animationSet.meleeAttacks.Count; attackIndex++)
                {
                    WeaponMeleeAttackAuthoring attack = animationSet.meleeAttacks[attackIndex];
                    if (attack.animationVfxBindings == null)
                        continue;

                    string attackId = attack.combat.AuthoredStrikeIdOrDefault;
                    var boundSlots = new HashSet<string>(StringComparer.Ordinal);
                    for (int bindingIndex = 0; bindingIndex < attack.animationVfxBindings.Count; bindingIndex++)
                    {
                        CombatAnimationVfxBinding binding = attack.animationVfxBindings[bindingIndex];
                        string slotId = binding.NormalizedSlotId;
                        string vfxId = binding.NormalizedVfxId;
                        string context =
                            $"CombatAnimationSet '{animationSet.name}' melee '{attackId}' animation VFX binding {bindingIndex + 1}";
                        if (string.IsNullOrEmpty(slotId))
                        {
                            errors.Add($"{context} has an empty Slot Id.");
                            continue;
                        }

                        if (!boundSlots.Add(slotId))
                            errors.Add($"{context} duplicates Slot Id '{slotId}'.");
                        if (string.IsNullOrEmpty(vfxId))
                        {
                            errors.Add($"{context} has an empty VFX Id. Remove the authored binding to disable the slot.");
                            continue;
                        }

                        if (CombatVFXTemplateRegistry.IsScriptedTemplate(vfxId))
                        {
                            errors.Add($"{context} references scripted template '{vfxId}'; animation VFX slots require a registry prefab.");
                            continue;
                        }

                        CombatVFXRegistry.Template? template = CombatVFXTemplateRegistry.ResolveTemplate(vfxId);
                        if (template == null)
                        {
                            errors.Add($"{context} references unresolved registry prefab '{vfxId}'.");
                            continue;
                        }

                        bool foundMatchingTrack = false;
                        foreach (CombatAnimationVfxTrack track in animationSet.animationVfxTracks ?? new List<CombatAnimationVfxTrack>())
                        {
                            if (track == null
                                || !string.Equals(track.NormalizedSlotId, slotId, StringComparison.Ordinal)
                                || !AttackReferencesClip(attack, track.clip))
                            {
                                continue;
                            }

                            foundMatchingTrack = true;
                            if (!track.HasFiniteWindow && PrefabHasLoopingParticleSystem(template.Prefab))
                            {
                                errors.Add(
                                    $"{context} fills natural-lifetime track '{slotId}' with looping prefab '{vfxId}'. Author an End Time so interruption and normal completion cannot leak the effect.");
                            }
                        }

                        if (!foundMatchingTrack)
                        {
                            errors.Add(
                                $"{context} has no matching Clip + Slot Id track on this attack's selected animation clips.");
                        }
                    }
                }
            }
        }

        private static bool AttackReferencesClip(
            WeaponMeleeAttackAuthoring attack,
            AnimationClip? clip)
        {
            if (clip == null)
                return false;
            if (ReferenceEquals(attack.clip, clip))
                return true;

            return attack.UsesPhasedPresentation
                && (ReferenceEquals(attack.phasedGround.start, clip)
                    || ReferenceEquals(attack.phasedGround.loop, clip)
                    || ReferenceEquals(attack.phasedGround.end, clip)
                    || ReferenceEquals(attack.phasedAir.start, clip)
                    || ReferenceEquals(attack.phasedAir.loop, clip)
                    || ReferenceEquals(attack.phasedAir.end, clip));
        }

        private static bool PrefabHasLoopingParticleSystem(GameObject prefab)
        {
            ParticleSystem[] systems = prefab.GetComponentsInChildren<ParticleSystem>(true);
            for (int index = 0; index < systems.Length; index++)
            {
                if (systems[index].main.loop)
                    return true;
            }

            return false;
        }

        private static void ValidateAnimationVfxBladeMarker(
            CombatAnimationSet animationSet,
            CombatAnimationVfxTrack track,
            List<string> errors)
        {
            string markerName = track.anchor switch
            {
                CombatAnimationVfxAnchor.MainWeaponBladeStart =>
                    CombatAnimationVfxAnchorUtility.BladeStartMarkerName,
                CombatAnimationVfxAnchor.MainWeaponBladeEnd =>
                    CombatAnimationVfxAnchorUtility.BladeEndMarkerName,
                _ => string.Empty,
            };
            if (string.IsNullOrEmpty(markerName))
                return;

            bool foundMainHandVisual = false;
            foreach (WeaponVisualBinding binding in animationSet.VisualBindings)
            {
                if (binding.prefab == null
                    || !string.Equals(
                        AvatarWeaponMounts.CanonicalizeMountId(binding.drawnMountId),
                        AvatarWeaponMounts.MainHandMountId,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                foundMainHandVisual = true;
                if (FindDescendant(binding.prefab.transform, markerName) == null)
                {
                    errors.Add(
                        $"CombatAnimationSet '{animationSet.name}' animation VFX track '{track.NormalizedSlotId}' uses {track.anchor}, but main-hand visual '{binding.prefab.name}' is missing '{markerName}'.");
                }
            }

            if (!foundMainHandVisual)
            {
                errors.Add(
                    $"CombatAnimationSet '{animationSet.name}' animation VFX track '{track.NormalizedSlotId}' uses {track.anchor}, but the set has no authored main-hand visual to provide '{markerName}'.");
            }
        }

        private static void ValidateGreatswordMeleeEvents(
            CombatAnimationSet animationSet,
            List<string> errors)
        {
            if (animationSet.meleeAttacks == null)
                return;

            for (int index = 0; index < animationSet.meleeAttacks.Count; index++)
            {
                WeaponMeleeAttackAuthoring attack = animationSet.meleeAttacks[index];
                string strikeLabel = string.IsNullOrWhiteSpace(attack.combat.AuthoredStrikeIdOrDefault)
                    ? $"Strike {index + 1}"
                    : attack.combat.AuthoredStrikeIdOrDefault;

                if (!attack.UsesPhasedPresentation)
                {
                    AnimationClip? clip = attack.clip;
                    if (clip == null)
                        continue;

                    string context = $"CombatAnimationSet '{animationSet.name}' melee '{strikeLabel}' clip";
                    RequireClipEvent(errors, clip, CombatAnimationEvents.OnStrikeHit, context);
                    RequireClipEvent(errors, clip, CombatAnimationEvents.OnLowerBodyUnlock, context);
                    RequireClipEvent(errors, clip, CombatAnimationEvents.OnVisualInterruptible, context);
                    RejectDeprecatedLowerBodyBlendEnd(errors, clip, context);
                    continue;
                }

                ValidateGreatswordPhasedMeleeEvents(
                    animationSet,
                    attack.phasedGround,
                    $"{strikeLabel} ground phased",
                    errors);
                ValidateGreatswordPhasedMeleeEvents(
                    animationSet,
                    attack.phasedAir,
                    $"{strikeLabel} air phased",
                    errors);
            }
        }

        private static void ValidateGreatswordPhasedMeleeEvents(
            CombatAnimationSet animationSet,
            WeaponPhasedActionClipSet clipSet,
            string label,
            List<string> errors)
        {
            if (!clipSet.HasAny)
                return;

            var clips = new List<(AnimationClip Clip, string Label)>();
            AddUniqueClip(clips, clipSet.start, "start");
            AddUniqueClip(clips, clipSet.loop, "loop");
            AddUniqueClip(clips, clipSet.end, "end");
            foreach ((AnimationClip clip, string segmentLabel) in clips)
            {
                string context = $"CombatAnimationSet '{animationSet.name}' melee '{label}' {segmentLabel} clip";
                RejectDeprecatedLowerBodyBlendEnd(errors, clip, context);
            }

            if (clipSet.end == null)
                return;

            string endContext = $"CombatAnimationSet '{animationSet.name}' melee '{label}' end clip";
            RequireClipEvent(errors, clipSet.end, CombatAnimationEvents.OnLowerBodyUnlock, endContext);
            RequireClipEvent(errors, clipSet.end, CombatAnimationEvents.OnVisualInterruptible, endContext);
        }

        private static void ValidateGreatswordStaggerEvents(
            CombatAnimationSet animationSet,
            List<string> errors)
        {
            ValidateStaggerClip(animationSet, animationSet.staggerF, "staggerF", errors);
            ValidateStaggerClip(animationSet, animationSet.staggerB, "staggerB", errors);
            ValidateStaggerClip(animationSet, animationSet.staggerL, "staggerL", errors);
            ValidateStaggerClip(animationSet, animationSet.staggerR, "staggerR", errors);
        }

        private static void ValidateStaggerClip(
            CombatAnimationSet animationSet,
            AnimationClip? clip,
            string fieldName,
            List<string> errors)
        {
            if (clip == null)
                return;

            string context = $"CombatAnimationSet '{animationSet.name}' {fieldName}";
            if (TryGetEventTime(clip, CombatAnimationEvents.OnLowerBodyUnlock, out _))
            {
                errors.Add(
                    $"{context} clip '{ClipLabel(clip)}' must not author {CombatAnimationEvents.OnLowerBodyUnlock}; stagger remains full-body.");
            }

            RejectDeprecatedLowerBodyBlendEnd(errors, clip, context);
        }

        private static void RequireClipEvent(
            List<string> errors,
            AnimationClip clip,
            string eventName,
            string context)
        {
            if (TryGetEventTime(clip, eventName, out _))
                return;

            errors.Add($"{context} '{ClipLabel(clip)}' is missing required event {eventName}.");
        }

        private static void RejectDeprecatedLowerBodyBlendEnd(
            List<string> errors,
            AnimationClip clip,
            string context)
        {
            const string deprecatedEventName = "OnLowerBodyBlendEnd";
            if (!TryGetEventTime(clip, deprecatedEventName, out _))
                return;

            errors.Add(
                $"{context} '{ClipLabel(clip)}' authors deprecated event {deprecatedEventName}. Runtime ignores it; use {CombatAnimationEvents.OnLowerBodyUnlock} plus the default lower-body blend-out.");
        }

        private static bool TryGetEventTime(AnimationClip clip, string eventName, out float seconds)
            => CombatAnimationEvents.TryGetEventTime(clip, eventName, out seconds);

        private static void AddUniqueClip(
            List<(AnimationClip Clip, string Label)> clips,
            AnimationClip? clip,
            string label)
        {
            if (clip == null)
                return;

            for (int index = 0; index < clips.Count; index++)
            {
                if (clips[index].Clip == clip)
                    return;
            }

            clips.Add((clip, label));
        }

        private static string ClipLabel(AnimationClip clip)
        {
            string path = AssetDatabase.GetAssetPath(clip);
            return string.IsNullOrWhiteSpace(path) ? clip.name : path;
        }

        private static HashSet<string> BuildCombatProfileActionBarDefaultAbilityIds(ProgressionCatalogDocument catalog)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            foreach (CombatProfileActionBarDefaultDefinition assignment in catalog.combat_profile_action_bar_defaults)
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

        private static void ValidateSpellCastAnimationMap(
            ProgressionCatalogDocument catalog,
            List<string> errors)
        {
            SpellCastAnimationMap? map = LoadFirstAsset<SpellCastAnimationMap>();
            if (map == null)
                return;

            SpellCastAnimationLibrary? library = LoadFirstAsset<SpellCastAnimationLibrary>();
            if (library == null)
                errors.Add("SpellCastAnimationMap exists, but no SpellCastAnimationLibrary asset resolves.");
            SpellCastAnimationCatalog? animationCatalog = LoadFirstAsset<SpellCastAnimationCatalog>();
            if (animationCatalog == null)
                errors.Add("SpellCastAnimationMap exists, but no SpellCastAnimationCatalog asset resolves.");

            var spellByActionId = new Dictionary<string, AbilityDefinition>(StringComparer.Ordinal);
            foreach (AbilityDefinition ability in catalog.abilities)
            {
                if (!string.Equals(WireIdentifier.Normalize(ability.gameplay.kind), "SPELL", StringComparison.Ordinal))
                    continue;

                string actionId = WireIdentifier.Normalize(ability.action_id);
                if (!string.IsNullOrWhiteSpace(actionId))
                    spellByActionId[actionId] = ability;
            }

            CombatAnimationSet[] animationSets = Resources.LoadAll<CombatAnimationSet>("CombatAnimationSets");

            if (animationCatalog != null)
            {
                var seenRecipeIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (SpellCastAnimationRecipe recipe in animationCatalog.Recipes)
                {
                    string recipeId = recipe.AnimationIdOrEmpty;
                    if (recipeId.Length == 0)
                    {
                        errors.Add("SpellCastAnimationCatalog has a recipe with no animationId.");
                        continue;
                    }
                    if (!seenRecipeIds.Add(recipeId))
                        errors.Add($"SpellCastAnimationCatalog has duplicate recipe id '{recipeId}'.");
                    if (!recipe.TryBuild("VALIDATION_SPELL", out _))
                        errors.Add($"SpellCastAnimationCatalog recipe '{recipeId}' has no playable {recipe.presentationMode} presentation.");
                }
            }

            foreach (CombatAnimationSet animationSet in animationSets)
            {
                var seenMotions = new HashSet<SpellCastMotion>();
                foreach (SpellCastMotionBinding binding in animationSet.spellCastMotionBindings ?? Array.Empty<SpellCastMotionBinding>())
                {
                    if (binding.motion == SpellCastMotion.None)
                    {
                        errors.Add($"CombatAnimationSet '{animationSet.name}' has a spell cast motion binding with motion None.");
                        continue;
                    }
                    if (!seenMotions.Add(binding.motion))
                        errors.Add($"CombatAnimationSet '{animationSet.name}' has duplicate bindings for spell cast motion '{binding.motion}'.");
                    if (binding.FamilyBaseNameOrEmpty.Length == 0)
                    {
                        errors.Add($"CombatAnimationSet '{animationSet.name}' motion '{binding.motion}' has no family base name.");
                        continue;
                    }
                    if (string.Equals(
                            binding.FamilyBaseNameOrEmpty,
                            "MagicAttackDirect2H01",
                            StringComparison.Ordinal))
                    {
                        errors.Add($"CombatAnimationSet '{animationSet.name}' uses forbidden cast family 'MagicAttackDirect2H01'. Use Direct2H02 or a Direct1H fallback.");
                        continue;
                    }
                    if (library != null && !library.TryGetFamily(binding.FamilyBaseNameOrEmpty, out _))
                    {
                        errors.Add($"CombatAnimationSet '{animationSet.name}' motion '{binding.motion}' references family '{binding.FamilyBaseNameOrEmpty}', but SpellCastAnimationLibrary has no matching family.");
                    }
                }

                var seenOverrideSpellIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (SpellCastAnimationOverride animationOverride in animationSet.spellCastAnimationOverrides ?? Array.Empty<SpellCastAnimationOverride>())
                {
                    string overrideSpellId = animationOverride.SpellIdOrEmpty;
                    string overrideAnimationId = animationOverride.AnimationIdOrEmpty;
                    if (overrideSpellId.Length == 0)
                    {
                        errors.Add($"CombatAnimationSet '{animationSet.name}' has a spell animation override with no spellId.");
                        continue;
                    }
                    if (!seenOverrideSpellIds.Add(overrideSpellId))
                        errors.Add($"CombatAnimationSet '{animationSet.name}' has duplicate animation overrides for spell '{overrideSpellId}'.");
                    if (!spellByActionId.TryGetValue(overrideSpellId, out AbilityDefinition overrideAbility))
                    {
                        errors.Add($"CombatAnimationSet '{animationSet.name}' override for spell '{overrideSpellId}' has no matching SPELL ability in progression_catalog.shared.json.");
                        continue;
                    }
                    if (!map.TryGetEntry(overrideSpellId, out SpellCastAnimationMap.Entry overrideMapEntry))
                    {
                        errors.Add($"CombatAnimationSet '{animationSet.name}' override for spell '{overrideSpellId}' has no SpellCastAnimationMap entry.");
                        continue;
                    }
                    if (overrideMapEntry.assignmentKind == SpellCastAnimationAssignmentKind.NoAnimation)
                    {
                        errors.Add($"CombatAnimationSet '{animationSet.name}' overrides spell '{overrideSpellId}', but its global mapping explicitly disables animation.");
                        continue;
                    }
                    string resolvedOverrideAnimationId = overrideAnimationId;
                    if (resolvedOverrideAnimationId.Length == 0)
                    {
                        if (!animationOverride.mirrorPresentation)
                        {
                            errors.Add($"CombatAnimationSet '{animationSet.name}' override for spell '{overrideSpellId}' has no animationId or mirror setting.");
                            continue;
                        }

                        SpellAnimationArchetype inheritedArchetype = DeriveSpellAnimationArchetype(overrideAbility);
                        if (!SpellCastAnimationResolver.TryResolve(
                                animationSet,
                                overrideSpellId,
                                inheritedArchetype,
                                out WeaponSpellAnimationEntry inheritedEntry))
                        {
                            errors.Add($"CombatAnimationSet '{animationSet.name}' mirror-only override for spell '{overrideSpellId}' did not resolve its global presentation.");
                            continue;
                        }
                        if (!inheritedEntry.HasAuthoredCastOrigin)
                        {
                            errors.Add($"CombatAnimationSet '{animationSet.name}' mirrors the global presentation for spell '{overrideSpellId}', but that presentation still uses the legacy VFX cue origin. Author a Left Hand or Right Hand animation origin before mirroring it.");
                        }
                        continue;
                    }
                    if (animationCatalog == null
                        || !animationCatalog.TryGetRecipe(resolvedOverrideAnimationId, out SpellCastAnimationRecipe overrideRecipe))
                    {
                        errors.Add($"CombatAnimationSet '{animationSet.name}' override for spell '{overrideSpellId}' references missing catalog recipe '{resolvedOverrideAnimationId}'.");
                        continue;
                    }

                    if (animationOverride.mirrorPresentation
                        && overrideRecipe.castOrigin == SpellCastOrigin.UseVfxCue)
                    {
                        errors.Add($"CombatAnimationSet '{animationSet.name}' mirrors recipe '{resolvedOverrideAnimationId}' for spell '{overrideSpellId}', but the recipe still uses the legacy VFX cue origin. Author Left Hand or Right Hand on the recipe so the launch origin mirrors with the body.");
                    }

                    SpellAnimationArchetype overrideArchetype = DeriveSpellAnimationArchetype(overrideAbility);
                    if (!overrideRecipe.IsCompatibleWith(overrideArchetype))
                    {
                        errors.Add($"CombatAnimationSet '{animationSet.name}' override for spell '{overrideSpellId}' selects recipe '{resolvedOverrideAnimationId}', which is not marked compatible with {overrideArchetype}.");
                    }
                    if (!SpellCastAnimationResolver.TryResolve(animationSet, overrideSpellId, overrideArchetype, out _))
                    {
                        errors.Add($"CombatAnimationSet '{animationSet.name}' override for spell '{overrideSpellId}' did not resolve recipe '{resolvedOverrideAnimationId}'.");
                    }
                }
            }

            var seenSpellIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (SpellCastAnimationMap.Entry entry in map.Entries)
            {
                string spellId = WireIdentifier.Normalize(entry.spellId);
                if (string.IsNullOrWhiteSpace(spellId))
                {
                    errors.Add("SpellCastAnimationMap has an entry with no spellId.");
                    continue;
                }
                if (!seenSpellIds.Add(spellId))
                {
                    errors.Add($"SpellCastAnimationMap has duplicate entries for spell '{spellId}'.");
                    continue;
                }

                if (!spellByActionId.TryGetValue(spellId, out AbilityDefinition ability))
                {
                    errors.Add($"SpellCastAnimationMap entry for spell '{spellId}' has no matching SPELL ability in progression_catalog.shared.json.");
                    continue;
                }

                SpellAnimationArchetype archetype = DeriveSpellAnimationArchetype(ability);
                if (entry.assignmentKind == SpellCastAnimationAssignmentKind.NoAnimation)
                {
                    if (!string.IsNullOrWhiteSpace(entry.animationId))
                        errors.Add($"SpellCastAnimationMap no-animation entry for spell '{spellId}' contains an unused animationId; clear it.");
                    if (entry.motion != SpellCastMotion.None)
                        errors.Add($"SpellCastAnimationMap no-animation entry for spell '{spellId}' must leave motion as None.");
                    if (entry.fixedAnimation.HasAnyPresentation)
                        errors.Add($"SpellCastAnimationMap no-animation entry for spell '{spellId}' contains an unused fixed presentation; clear it.");
                    if (entry.playbackLayer != SpellCastLayerOverride.Auto
                        || entry.combatEntryMode != SpellCastEntryModeOverride.Auto
                        || entry.animatedProp.enabled)
                    {
                        errors.Add($"SpellCastAnimationMap no-animation entry for spell '{spellId}' contains unused playback overrides; clear them.");
                    }
                    continue;
                }

                if (entry.assignmentKind == SpellCastAnimationAssignmentKind.Fixed)
                {
                    if (!string.IsNullOrWhiteSpace(entry.animationId))
                        errors.Add($"SpellCastAnimationMap fixed entry for spell '{spellId}' contains an unused animationId; clear it.");
                    if (entry.motion != SpellCastMotion.None)
                        errors.Add($"SpellCastAnimationMap fixed entry for spell '{spellId}' must leave motion as None.");
                    if (!entry.fixedAnimation.HasAnyPresentation)
                    {
                        errors.Add($"SpellCastAnimationMap fixed entry for spell '{spellId}' has no playable presentation.");
                        continue;
                    }
                    if (!SpellCastAnimationResolver.TryResolve(null, spellId, archetype, out _))
                        errors.Add($"SpellCastAnimationMap fixed entry for spell '{spellId}' did not resolve independently of a CombatAnimationSet.");
                    continue;
                }

                if (entry.assignmentKind == SpellCastAnimationAssignmentKind.Catalog)
                {
                    string animationId = WireIdentifier.Normalize(entry.animationId);
                    if (entry.motion != SpellCastMotion.None)
                        errors.Add($"SpellCastAnimationMap catalog entry for spell '{spellId}' must leave legacy motion as None.");
                    if (entry.fixedAnimation.HasAnyPresentation)
                        errors.Add($"SpellCastAnimationMap catalog entry for spell '{spellId}' contains an unused fixed presentation; clear it.");
                    if (animationId.Length == 0)
                    {
                        errors.Add($"SpellCastAnimationMap catalog entry for spell '{spellId}' has no animationId.");
                        continue;
                    }
                    if (animationCatalog == null || !animationCatalog.TryGetRecipe(animationId, out SpellCastAnimationRecipe recipe))
                    {
                        errors.Add($"SpellCastAnimationMap catalog entry for spell '{spellId}' references missing recipe '{animationId}'.");
                        continue;
                    }
                    if (!recipe.IsCompatibleWith(archetype))
                        errors.Add($"SpellCastAnimationMap spell '{spellId}' resolves authored gameplay as {archetype}, but catalog recipe '{animationId}' is not marked compatible.");

                    foreach (CombatAnimationSet animationSet in animationSets)
                    {
                        if (!SpellCastAnimationResolver.TryResolve(animationSet, spellId, archetype, out _))
                            errors.Add($"SpellCastAnimationMap catalog entry for spell '{spellId}' did not resolve in CombatAnimationSet '{animationSet.name}'. Check its optional spell override.");
                    }
                    continue;
                }

                if (entry.motion == SpellCastMotion.None)
                {
                    errors.Add($"SpellCastAnimationMap legacy-motion entry for spell '{spellId}' has motion None.");
                    continue;
                }
                if (!string.IsNullOrWhiteSpace(entry.animationId))
                    errors.Add($"SpellCastAnimationMap legacy-motion entry for spell '{spellId}' contains an unused animationId; clear it.");
                if (entry.fixedAnimation.HasAnyPresentation)
                    errors.Add($"SpellCastAnimationMap legacy-motion entry for spell '{spellId}' also contains a fixed presentation; clear the unused fixed data.");

                foreach (CombatAnimationSet animationSet in animationSets)
                {
                    if (animationSet.TryGetSpellCastAnimationOverride(spellId, out string _))
                        continue;

                    if (!animationSet.TryGetSpellCastFamily(entry.motion, out string familyBaseName))
                    {
                        errors.Add($"SpellCastAnimationMap spell '{spellId}' uses motion '{entry.motion}', but CombatAnimationSet '{animationSet.name}' has no binding for it.");
                        continue;
                    }
                    if (library == null || !library.TryGetFamily(familyBaseName, out SpellCastAnimationFamily family))
                        continue;
                    SpellCastHand hand = animationSet.OneHandedCastHand;
                    if (SpellCastAnimationComposer.TryCompose(spellId, family, hand, archetype, out _))
                        continue;

                    errors.Add(
                        $"SpellCastAnimationMap spell '{spellId}' uses motion '{entry.motion}' and resolves authored gameplay as {archetype}, but CombatAnimationSet '{animationSet.name}' family '{familyBaseName}' has no playable clips for hand '{hand}'.");
                }
            }
        }

        private static SpellAnimationArchetype DeriveSpellAnimationArchetype(AbilityDefinition ability)
        {
            ulong castTimeMs = (ulong)Math.Max(0, ability.gameplay.cast_time_ms);
            string deliveryKind = WireIdentifier.Normalize(ability.gameplay.delivery.kind);
            return SpellAnimationArchetypes.Derive(castTimeMs, deliveryKind);
        }

        private static T? LoadFirstAsset<T>() where T : UnityEngine.Object
        {
            string[] guids = AssetDatabase.FindAssets($"t:{typeof(T).Name}");
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                T? asset = AssetDatabase.LoadAssetAtPath<T>(path);
                if (asset != null)
                    return asset;
            }

            return null;
        }

        private static bool IsSelectableAbility(
            AbilityDefinition ability,
            HashSet<string> actionBarDefaultAbilityIds)
        {
            string abilityId = WireIdentifier.Normalize(ability.ability_id);
            if (actionBarDefaultAbilityIds.Contains(abilityId))
                return true;

            foreach (string tag in ability.ability_tags)
            {
                if (string.Equals(WireIdentifier.Normalize(tag), "ACTION_BAR_ACTION", StringComparison.Ordinal))
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
            bool requiresTargetBack = false;
            bool requiresMainWeapon = false;
            bool requiresOffWeapon = false;
            bool requiresBladeMarkers = false;

            foreach (CombatVfxCueDefinition cue in catalog.combat_vfx_cues)
            {
                string anchor = WireIdentifier.Normalize(cue.anchor);
                requiresLeftHand |= string.Equals(anchor, AnchorLeftHand, StringComparison.Ordinal);
                requiresRightHand |= string.Equals(anchor, AnchorRightHand, StringComparison.Ordinal);
                requiresTargetBack |= string.Equals(anchor, AnchorTargetBack, StringComparison.Ordinal);
                requiresMainWeapon |= string.Equals(anchor, AnchorWeaponMainHand, StringComparison.Ordinal)
                    || string.Equals(anchor, AnchorWeaponBladeStart, StringComparison.Ordinal)
                    || string.Equals(anchor, AnchorWeaponBladeEnd, StringComparison.Ordinal);
                requiresOffWeapon |= string.Equals(anchor, AnchorWeaponOffHand, StringComparison.Ordinal);
                requiresBladeMarkers |= string.Equals(anchor, AnchorWeaponBladeStart, StringComparison.Ordinal)
                    || string.Equals(anchor, AnchorWeaponBladeEnd, StringComparison.Ordinal);
            }

            if (!requiresLeftHand
                && !requiresRightHand
                && !requiresTargetBack
                && !requiresMainWeapon
                && !requiresOffWeapon
                && !requiresBladeMarkers)
            {
                return;
            }

            GameObject? runtimeAvatarPrefab = RuntimeAvatarPrefabResolver.LoadRuntimePlayerPrefab();
            if (runtimeAvatarPrefab == null)
            {
                errors.Add("combat_vfx_cues require skeletal or weapon anchors, but no runtime player prefab resolves from Resources.");
                return;
            }

            Animator? animator = runtimeAvatarPrefab.GetComponentInChildren<Animator>(true);
            if ((requiresLeftHand || requiresRightHand || requiresTargetBack) && animator == null)
            {
                errors.Add($"runtime avatar prefab '{runtimeAvatarPrefab.name}' has no Animator, but combat_vfx_cues require skeletal anchors.");
            }
            else if (animator != null)
            {
                if (requiresLeftHand && !TryGetHumanoidBone(animator, HumanBodyBones.LeftHand, out _))
                    errors.Add($"runtime avatar prefab '{runtimeAvatarPrefab.name}' does not resolve humanoid bone LeftHand required by combat_vfx_cues.");
                if (requiresRightHand && !TryGetHumanoidBone(animator, HumanBodyBones.RightHand, out _))
                    errors.Add($"runtime avatar prefab '{runtimeAvatarPrefab.name}' does not resolve humanoid bone RightHand required by combat_vfx_cues.");
                if (requiresTargetBack
                    && FindDescendant(runtimeAvatarPrefab.transform, "spine_03") == null
                    && !TryGetHumanoidBone(animator, HumanBodyBones.UpperChest, out _)
                    && !TryGetHumanoidBone(animator, HumanBodyBones.Chest, out _)
                    && !TryGetHumanoidBone(animator, HumanBodyBones.Spine, out _))
                {
                    errors.Add($"runtime avatar prefab '{runtimeAvatarPrefab.name}' does not resolve a torso bone required by TARGET_BACK combat_vfx_cues.");
                }
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
            string absolutePath = SpellPresentationEditorData.AbsoluteProgressionCatalogPath;
            if (!File.Exists(absolutePath))
            {
                errors.Add($"Progression catalog not found at '{SpellPresentationEditorData.ProgressionCatalogPath}'.");
                return null;
            }

            try
            {
                string json = File.ReadAllText(absolutePath);
                return JsonUtility.FromJson<ProgressionCatalogDocument>(json);
            }
            catch (Exception ex)
            {
                errors.Add($"Failed to parse '{SpellPresentationEditorData.ProgressionCatalogPath}': {ex.Message}");
                return null;
            }
        }

        [Serializable]
        private sealed class ProgressionCatalogDocument
        {
            public List<AbilityDefinition> abilities = new();
            public List<CombatVfxCueDefinition> combat_vfx_cues = new();
            public List<CombatProfileActionBarDefaultDefinition> combat_profile_action_bar_defaults = new();
        }

        [Serializable]
        private sealed class AbilityDefinition
        {
            public string ability_id = string.Empty;
            public string combat_profile_id = string.Empty;
            public string action_id = string.Empty;
            public List<string> ability_tags = new();
            public GameplayDefinition gameplay = new();
        }

        [Serializable]
        private sealed class GameplayDefinition
        {
            public string kind = string.Empty;
            public int cast_time_ms = 0;
            public DeliveryDefinition delivery = new();
        }

        [Serializable]
        private sealed class DeliveryDefinition
        {
            public string kind = string.Empty;
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
        private sealed class CombatProfileActionBarDefaultDefinition
        {
            public string ability_id = string.Empty;
        }
    }
}
