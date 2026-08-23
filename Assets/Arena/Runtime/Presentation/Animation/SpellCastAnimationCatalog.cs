#nullable enable
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Arena.Presentation
{
    [Flags]
    public enum SpellAnimationArchetypeMask
    {
        None = 0,
        Instant = 1 << 0,
        Charged = 1 << 1,
        Channel = 1 << 2,
        All = Instant | Charged | Channel,
    }

    /// <summary>
    /// A reusable, combat-set-independent cast presentation. A recipe owns its exact phase shape;
    /// gameplay archetype is compatibility metadata rather than a switch that rewrites the clips.
    /// This lets single-shot and Start/Loop/End packs coexist in the same picker.
    /// </summary>
    [Serializable]
    public struct SpellCastAnimationRecipe
    {
        [Tooltip("Stable selection id, e.g. MAGE_PROJECTILE_CAST_02.")]
        public string animationId;
        [Tooltip("Human-facing picker label, e.g. Projectile Cast 2.")]
        public string displayName;
        [Tooltip("Picker group, e.g. Mage Pack/Projectile or Legacy.")]
        public string category;
        [TextArea]
        [Tooltip("Optional authoring note about the source clip or intended motion.")]
        public string notes;
        [Tooltip("Spell archetypes this recipe is intended for. None is treated as All for migration compatibility.")]
        public SpellAnimationArchetypeMask compatibleArchetypes;

        [Header("Presentation")]
        [Tooltip("The cast/release/end clip. This clip is movement-state-independent.")]
        public AnimationClip? clip;
        [Tooltip("For HoldWithPulse, the authored transition that returns from Clip to the hold loop.")]
        public AnimationClip? returnToHold;
        [Tooltip("ReleaseOnly uses Clip alone. HoldThenRelease plays Enter/Loop then Clip. HoldOnly plays Enter/Loop until exit. HoldWithPulse temporarily plays Clip/Return To Hold while keeping the hold active.")]
        public SpellAnimationPresentationMode presentationMode;
        [Tooltip("For ReleaseOnly recipes, choose the preparation shown only when the spell has cast time. Default uses the catalog's shared Aim Target lead-in; None goes directly to release; Custom uses the profile below.")]
        public SpellCastLeadInPolicy castLeadInPolicy;
        [Tooltip("Used by ReleaseOnly only when Cast Lead-In is Custom. Enter and Loop are required; Exit is used on cancellation before release begins.")]
        public SpellCastHoldProfile customCastLeadIn;
        [Tooltip("Optional authored Start/Loop/Exit sequence. Start and Loop are required for every hold mode; Exit is used when a hold/channel closes without a release clip.")]
        public SpellCastHoldProfile hold;
        [Tooltip("Whether selecting this recipe should request combat stance.")]
        public bool requiresCombatStance;
        [Tooltip("How combat stance is entered when requested.")]
        public CombatEntryMode combatEntryMode;
        [Tooltip("The animator layer used by the cast/release clip.")]
        public SpellPlaybackLayer playbackLayer;
        [Tooltip("Natural spell-emission hand for this animation. CombatAnimationSet mirroring swaps this hand with the humanoid motion. Use VFX Cue preserves legacy cue-authored handedness.")]
        public SpellCastOrigin castOrigin;
        [Tooltip("Optional temporary weapon/shield visual driven by the recipe.")]
        public SpellAnimatedPropHandoff animatedProp;

        public string AnimationIdOrEmpty => Normalize(animationId);
        public string DisplayNameOrId => string.IsNullOrWhiteSpace(displayName)
            ? AnimationIdOrEmpty
            : displayName.Trim();
        public string CategoryOrDefault => string.IsNullOrWhiteSpace(category)
            ? "Other"
            : category.Trim();
        public string PickerLabel => $"{CategoryOrDefault}/{DisplayNameOrId}";

        public bool IsCompatibleWith(SpellAnimationArchetype archetype)
        {
            SpellAnimationArchetypeMask mask = compatibleArchetypes == SpellAnimationArchetypeMask.None
                ? SpellAnimationArchetypeMask.All
                : compatibleArchetypes;
            SpellAnimationArchetypeMask requested = archetype switch
            {
                SpellAnimationArchetype.Instant => SpellAnimationArchetypeMask.Instant,
                SpellAnimationArchetype.Charged => SpellAnimationArchetypeMask.Charged,
                SpellAnimationArchetype.Channel => SpellAnimationArchetypeMask.Channel,
                _ => SpellAnimationArchetypeMask.None,
            };
            return (mask & requested) != 0;
        }

        public bool TryBuild(string spellId, out WeaponSpellAnimationEntry entry)
            => TryBuild(spellId, default, out entry);

        public bool TryBuild(
            string spellId,
            SpellCastHoldProfile defaultCastTimeLeadIn,
            out WeaponSpellAnimationEntry entry)
        {
            entry = new WeaponSpellAnimationEntry
            {
                spellId = Normalize(spellId),
                clip = clip,
                returnToHold = returnToHold,
                requiresCombatStance = requiresCombatStance,
                combatEntryMode = combatEntryMode,
                presentationMode = presentationMode,
                holdOverride = hold,
                castTimeLeadIn = ResolveCastTimeLeadIn(defaultCastTimeLeadIn),
                playbackLayer = playbackLayer,
                castOrigin = castOrigin,
                animatedProp = animatedProp,
            };

            if (entry.SpellIdOrEmpty.Length == 0)
                return false;

            return presentationMode switch
            {
                SpellAnimationPresentationMode.ReleaseOnly => clip != null,
                SpellAnimationPresentationMode.HoldThenRelease => clip != null && hold.IsPlayable,
                SpellAnimationPresentationMode.HoldOnly => hold.IsPlayable,
                SpellAnimationPresentationMode.HoldWithPulse =>
                    clip != null && returnToHold != null && hold.IsPlayable,
                _ => false,
            };
        }

        public SpellCastHoldProfile ResolveCastTimeLeadIn(
            SpellCastHoldProfile defaultCastTimeLeadIn)
        {
            if (presentationMode != SpellAnimationPresentationMode.ReleaseOnly)
                return default;

            return castLeadInPolicy switch
            {
                SpellCastLeadInPolicy.Default => defaultCastTimeLeadIn,
                SpellCastLeadInPolicy.Custom => customCastLeadIn,
                _ => default,
            };
        }

        private static string Normalize(string? value)
            => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToUpperInvariant();
    }

    /// <summary>
    /// Shared source of spell-cast choices. Spell mappings and combat-set overrides store only a
    /// recipe id, so the same handpicked animation can be reused without duplicating clip graphs.
    /// </summary>
    [CreateAssetMenu(menuName = "Arena/Spell Cast Animation Catalog", fileName = "SpellCastAnimationCatalog")]
    public sealed class SpellCastAnimationCatalog : ScriptableObject
    {
        [SerializeField]
        [Tooltip("Shared preparation for cast-time ReleaseOnly recipes whose Cast Lead-In is Default. Aim_The_Target Start/Loop/End is the project default.")]
        private SpellCastHoldProfile defaultCastLeadIn;
        [SerializeField] private List<SpellCastAnimationRecipe> recipes = new();
        [NonSerialized] private Dictionary<string, SpellCastAnimationRecipe>? _recipeById;

        public IReadOnlyList<SpellCastAnimationRecipe> Recipes => recipes;
        public SpellCastHoldProfile DefaultCastLeadIn => defaultCastLeadIn;

        public bool TryGetRecipe(string animationId, out SpellCastAnimationRecipe recipe)
        {
            string key = Normalize(animationId);
            if (key.Length != 0 && RecipeById.TryGetValue(key, out recipe))
                return true;

            recipe = default;
            return false;
        }

        public bool TryBuildRecipe(
            string animationId,
            string spellId,
            out WeaponSpellAnimationEntry entry)
        {
            if (TryGetRecipe(animationId, out SpellCastAnimationRecipe recipe))
                return recipe.TryBuild(spellId, defaultCastLeadIn, out entry);

            entry = default;
            return false;
        }

        public SpellCastHoldProfile ResolveCastTimeLeadIn(
            in SpellCastAnimationRecipe recipe)
            => recipe.ResolveCastTimeLeadIn(defaultCastLeadIn);

        private Dictionary<string, SpellCastAnimationRecipe> RecipeById
        {
            get
            {
                if (_recipeById != null)
                    return _recipeById;

                _recipeById = new Dictionary<string, SpellCastAnimationRecipe>(StringComparer.Ordinal);
                for (int index = 0; index < recipes.Count; index++)
                {
                    SpellCastAnimationRecipe candidate = recipes[index];
                    string id = candidate.AnimationIdOrEmpty;
                    if (id.Length == 0 || _recipeById.ContainsKey(id))
                        continue;

                    _recipeById.Add(id, candidate);
                }

                return _recipeById;
            }
        }

        private void OnEnable() => _recipeById = null;

        private void OnValidate()
        {
            _recipeById = null;
            SpellCastAnimationResolver.InvalidateCache();
        }

#if UNITY_EDITOR
        public void EditorReplaceRecipes(List<SpellCastAnimationRecipe> replacements)
        {
            recipes = replacements ?? new List<SpellCastAnimationRecipe>();
            _recipeById = null;
        }

        public bool EditorSetCastOrigin(string animationId, SpellCastOrigin castOrigin)
        {
            string normalizedAnimationId = Normalize(animationId);
            if (normalizedAnimationId.Length == 0)
                return false;

            for (int index = 0; index < recipes.Count; index++)
            {
                SpellCastAnimationRecipe recipe = recipes[index];
                if (!string.Equals(recipe.AnimationIdOrEmpty, normalizedAnimationId, StringComparison.Ordinal))
                    continue;

                if (recipe.castOrigin == castOrigin)
                    return false;

                recipe.castOrigin = castOrigin;
                recipes[index] = recipe;
                _recipeById = null;
                SpellCastAnimationResolver.InvalidateCache();
                return true;
            }

            return false;
        }

        public bool EditorSetCastLeadIn(
            string animationId,
            SpellCastLeadInPolicy policy,
            SpellCastHoldProfile customProfile)
        {
            string normalizedAnimationId = Normalize(animationId);
            if (normalizedAnimationId.Length == 0)
                return false;

            for (int index = 0; index < recipes.Count; index++)
            {
                SpellCastAnimationRecipe recipe = recipes[index];
                if (!string.Equals(recipe.AnimationIdOrEmpty, normalizedAnimationId, StringComparison.Ordinal))
                    continue;

                recipe.castLeadInPolicy = policy;
                recipe.customCastLeadIn = customProfile;
                recipes[index] = recipe;
                _recipeById = null;
                SpellCastAnimationResolver.InvalidateCache();
                return true;
            }

            return false;
        }
#endif

        private static string Normalize(string? value)
            => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToUpperInvariant();
    }
}
