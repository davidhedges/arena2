#nullable enable
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Arena.Presentation
{
    /// <summary>
    /// The hand-style a cast-animation base belongs to. One-handed bases carry Left/Right variants
    /// (Kevin Iglesias `…1H…_L`/`_R`, `Ground…_L`/`_R`); two-handed bases are a single both-hands
    /// motion with no hand suffix (`…2H…`, `Omni…`, `Special…`). 1H and 2H are genuinely different
    /// animations, not L/R swaps — see docs/spell-cast-animation-stitching-2026-07-09.md §4.
    /// </summary>
    public enum SpellCastHandStyle
    {
        OneHand = 0,
        TwoHand = 1,
    }

    /// <summary>Which concrete hand a resolved cast uses.</summary>
    public enum SpellCastHand
    {
        Left = 0,
        Right = 1,
        TwoHand = 2,
    }

    /// <summary>
    /// The three clips of one base cast animation (Kevin Iglesias naming convention):
    /// <c>oneShot</c> = <c>"{Base}.anim"</c> (the full instant cast, and the wind-up/enter for held
    /// casts), <c>load</c> = <c>"{Base} - Load.anim"</c> (the sustained charge/channel loop),
    /// <c>cast</c> = <c>"{Base} - Cast.anim"</c> (the release finish).
    /// </summary>
    [Serializable]
    public struct SpellCastClipTriple
    {
        public AnimationClip? oneShot;
        public AnimationClip? load;
        public AnimationClip? cast;

        public bool HasOneShot => oneShot != null;
        /// <summary>Enough to drive a held cast (enter → loop): a wind-up and a loop.</summary>
        public bool HasHold => oneShot != null && load != null;
        public bool HasCast => cast != null;
        public bool IsEmpty => oneShot == null && load == null && cast == null;
    }

    /// <summary>
    /// One base cast animation and its variants. A <see cref="SpellCastHandStyle.OneHand"/> family
    /// fills <see cref="left"/>/<see cref="right"/>; a <see cref="SpellCastHandStyle.TwoHand"/>
    /// family fills <see cref="twoHand"/>. Populated by the editor auto-scan tool
    /// (<c>SpellCastAnimationLibraryBuilder</c>) from the MagicAttacks pack.
    /// </summary>
    [Serializable]
    public struct SpellCastAnimationFamily
    {
        [Tooltip("Base animation name with no HumanM@ prefix, no hand suffix, no variant suffix — e.g. MagicAttackCall1H01.")]
        public string baseName;
        public SpellCastHandStyle handStyle;
        [Tooltip("OneHand: clips whose filename ends in _L before the variant suffix.")]
        public SpellCastClipTriple left;
        [Tooltip("OneHand: clips whose filename ends in _R before the variant suffix.")]
        public SpellCastClipTriple right;
        [Tooltip("TwoHand: the single both-hands clip triple (no hand suffix).")]
        public SpellCastClipTriple twoHand;

        public string BaseNameOrEmpty => string.IsNullOrWhiteSpace(baseName) ? string.Empty : baseName.Trim();

        /// <summary>
        /// The clip triple for a concrete cast hand. A OneHand family resolves Left/Right (and TwoHand
        /// falls back to Left, its only sensible single-hand); a TwoHand family always returns its
        /// both-hands triple. Returns false only when the requested variant is empty.
        /// </summary>
        public bool TryGetTriple(SpellCastHand hand, out SpellCastClipTriple triple)
        {
            triple = handStyle == SpellCastHandStyle.TwoHand
                ? twoHand
                : hand == SpellCastHand.Right ? right : left;
            return !triple.IsEmpty;
        }
    }

    /// <summary>
    /// Auto-generated library of cast-animation families (one per base), scanned from the Kevin
    /// Iglesias MagicAttacks pack by <c>SpellCastAnimationLibraryBuilder</c>. Lives in Resources so
    /// the resolver can look a base up at runtime without a per-reference asset. The library holds
    /// the real clip references, so the clips land in the build (Content/ clips are not
    /// Resources-loadable). Authoring surface: docs/spell-cast-animation-stitching-2026-07-09.md §5.
    /// </summary>
    [CreateAssetMenu(menuName = "Arena/Spell Cast Animation Library", fileName = "SpellCastAnimationLibrary")]
    public sealed class SpellCastAnimationLibrary : ScriptableObject
    {
        [SerializeField] private List<SpellCastAnimationFamily> families = new();

        public IReadOnlyList<SpellCastAnimationFamily> Families => families;

        public bool TryGetFamily(string baseName, out SpellCastAnimationFamily family)
        {
            string key = string.IsNullOrWhiteSpace(baseName) ? string.Empty : baseName.Trim();
            if (key.Length != 0)
            {
                for (int i = 0; i < families.Count; i++)
                {
                    if (string.Equals(families[i].BaseNameOrEmpty, key, StringComparison.OrdinalIgnoreCase))
                    {
                        family = families[i];
                        return true;
                    }
                }
            }

            family = default;
            return false;
        }

        private void OnValidate() => SpellCastAnimationResolver.InvalidateCache();

#if UNITY_EDITOR
        /// <summary>Editor-only: replace the whole family list from an auto-scan pass.</summary>
        public void EditorReplaceFamilies(List<SpellCastAnimationFamily> scanned)
        {
            families = scanned ?? new List<SpellCastAnimationFamily>();
        }
#endif
    }
}
