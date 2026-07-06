#nullable enable
using System;
using System.IO;
using UnityEngine;

namespace Arena.Editor
{
    /// <summary>
    /// Source of a clip-role classification. Reference is the strongest signal (the clip
    /// is referenced by a CombatAnimationSet at a known role-bearing field). Name is a
    /// fallback used when reference inference returns Unknown — based on file path /
    /// filename patterns common to the third-party packs in this project. Unknown means
    /// neither signal classified the clip.
    /// </summary>
    public enum CombatClipRoleSource
    {
        Unknown = 0,
        Reference,
        Name,
    }

    /// <summary>
    /// Conservative path/filename pattern matcher for AnimationClip paths under
    /// `Assets/Arena/Content/Animation/Extracted/`. Returns Unknown rather than guessing
    /// when patterns are ambiguous. Rules are ordered most-specific first; the first
    /// match wins.
    ///
    /// This is a fallback to <see cref="CombatClipRoleInferer"/>'s reference-based map.
    /// Reference inference is authoritative when present; this fills in clips that have
    /// been extracted but not yet wired into a CombatAnimationSet.
    /// </summary>
    public static class CombatClipRoleNameInference
    {
        public static CombatClipRole TryInferFromPath(string clipAssetPath)
        {
            if (string.IsNullOrEmpty(clipAssetPath))
                return CombatClipRole.Unknown;

            string fileNoExt = Path.GetFileNameWithoutExtension(clipAssetPath);
            string normalizedPath = clipAssetPath.Replace('\\', '/');
            string lowerName = fileNoExt.ToLowerInvariant();
            string lowerPath = normalizedPath.ToLowerInvariant();

            // 1. Locomotion folders — strongest path signal. Matches both numbered third-
            //    party convention (01_Idle, 03_Walk, ...) and human-readable folder names.
            if (PathContainsAny(lowerPath, sLocomotionPathTokens))
                return CombatClipRole.Locomotion;

            // 2. Cast hold (Kevin Iglesias). Path-confirmed to avoid matching unrelated
            //    "casting" mentions.
            if (lowerPath.Contains("spellcasting"))
            {
                if (lowerName.Contains("castingenter")) return CombatClipRole.SpellCastHoldEnter;
                if (lowerName.Contains("castingidle"))  return CombatClipRole.SpellCastHoldIdle;
                if (lowerName.Contains("castingexit"))  return CombatClipRole.SpellCastHoldExit;
            }

            // 3. Spell cast lifecycle (Kevin Iglesias MagicAttacks).
            if (lowerPath.Contains("magicattacks") || lowerName.Contains("magicattack"))
            {
                // Kevin Iglesias MagicAttacks split into unsuffixed enter clips,
                // "... - Load" hold loops, and "... - Cast" release clips.
                // Reference inference still wins when a set deliberately uses an
                // unsuffixed clip as a release, but unwired clips need the correct
                // stamper template so OnEnterComplete is available.
                if (EndsWithSuffix(lowerName, "load")) return CombatClipRole.SpellCastHoldIdle;
                if (EndsWithSuffix(lowerName, "cast")) return CombatClipRole.SpellRelease;
                if (lowerName.Contains("magicattack")) return CombatClipRole.SpellCastHoldEnter;
            }

            // 4. Block — check Block_Hit_Break before Block_Hit before generic suffixes.
            if (ContainsTokenStart(lowerName, "block"))
            {
                if (lowerName.Contains("hit_break") || lowerName.Contains("hitbreak"))
                    return CombatClipRole.BlockHitBreak;
                if (EndsWithSuffix(lowerName, "hit"))   return CombatClipRole.BlockHit;
                if (EndsWithSuffix(lowerName, "start")) return CombatClipRole.BlockStart;
                if (EndsWithSuffix(lowerName, "loop"))  return CombatClipRole.BlockLoop;
                if (EndsWithSuffix(lowerName, "end"))   return CombatClipRole.BlockEnd;
            }

            // 5. Parry.
            if (ContainsTokenStart(lowerName, "parry"))
            {
                if (EndsWithSuffix(lowerName, "hit"))   return CombatClipRole.ParryHit;
                if (EndsWithSuffix(lowerName, "start")) return CombatClipRole.ParryStart;
            }

            // 6. Knockdown / get up.
            if (lowerName.Contains("knock_down") || lowerName.Contains("knockdown"))
            {
                if (EndsWithSuffix(lowerName, "start")) return CombatClipRole.KnockdownStart;
                if (EndsWithSuffix(lowerName, "loop"))  return CombatClipRole.KnockdownLoop;
            }
            if (lowerName.Contains("get_up") || lowerName.Contains("getup"))
                return CombatClipRole.GetUp;

            // 7. Stun.
            if (ContainsTokenStart(lowerName, "stun"))
            {
                if (EndsWithSuffix(lowerName, "start")) return CombatClipRole.StunStart;
                if (EndsWithSuffix(lowerName, "loop"))  return CombatClipRole.StunLoop;
                if (EndsWithSuffix(lowerName, "end"))   return CombatClipRole.StunEnd;
            }

            // 8. Stagger.
            if (lowerName.Contains("stagger"))
                return CombatClipRole.Stagger;

            // 9. Hit reaction. Folder convention "08_Hit" is a strong hint; combined with
            //    a directional suffix it's near-certain. Filename starting with "hit_" is
            //    also reliable.
            if (lowerPath.Contains("/08_hit/") || ContainsTokenStart(lowerName, "hit"))
            {
                if (HasDirectionalSuffix(lowerName))
                    return CombatClipRole.HitReaction;
            }

            // 10. Death.
            if (lowerName.Contains("death") || lowerName.Equals("die"))
                return CombatClipRole.Death;

            // 11. Draw / sheath weapon.
            if (lowerName.Contains("draw_weapon") || lowerName.Contains("drawweapon")
                || lowerName.Contains("equip_weapon") || lowerName.Contains("equipweapon"))
                return CombatClipRole.DrawWeapon;
            if (lowerName.Contains("sheath_weapon") || lowerName.Contains("sheathweapon")
                || lowerName.Contains("unequip_weapon") || lowerName.Contains("unequipweapon"))
                return CombatClipRole.SheathWeapon;

            // 12. Phased melee — attack-context filename with phase suffix. Matches:
            //     Attack_*_Start/Loop/End, Combo_Attack_*_Start/Loop/End,
            //     Charge_*_Attack_Start/Loop/End, Aim_the_Target_Start/Loop/End,
            //     Buff_Air_Start/Loop/End (charged buffs).
            if (HasAttackContext(lowerName))
            {
                if (EndsWithSuffix(lowerName, "start")) return CombatClipRole.PhasedMeleeStart;
                if (EndsWithSuffix(lowerName, "loop"))  return CombatClipRole.PhasedMeleeLoop;
                if (EndsWithSuffix(lowerName, "end"))   return CombatClipRole.PhasedMeleeEnd;
                if (EndsWithSuffix(lowerName, "stop"))  return CombatClipRole.PhasedMeleeEnd;
            }

            // 13. Single melee strike — attack-context filename with no phase suffix.
            //     Excludes anything we already classified above.
            if (HasAttackContext(lowerName) && !HasPhaseSuffix(lowerName))
                return CombatClipRole.MeleeStrike;

            return CombatClipRole.Unknown;
        }

        // Path tokens that imply locomotion. Matches the third-party numbered folder
        // convention plus a few common alternates.
        private static readonly string[] sLocomotionPathTokens = new[]
        {
            "/01_idle/",
            "/03_walk/",
            "/04_run/",
            "/05_jump/",
            "/06_dodge/",
            "/07_roll/",
            "/09_turn/",
            "/idles/",
            "/locomotion/",
            "/freefall/",
            "/jumping/",
            "/walking/",
            "/running/",
            "/turning/",
            "/dodging/",
        };

        private static readonly string[] sAttackPrefixes = new[]
        {
            "attack_",
            "combo_attack",
            "charge_",
            "aim_the_target",
            "buff_air",
            "buff",
            "strike_",
            "swing_",
            "thrust_",
        };

        private static readonly string[] sPhaseSuffixes = new[]
        {
            "_start", "_loop", "_end", "_stop",
        };

        private static readonly string[] sDirectionalSuffixes = new[]
        {
            "_f", "_b", "_l", "_r",
            "_f_0", "_b_0",
            "_l_45", "_r_45", "_l_90", "_r_90", "_l_135", "_r_135", "_l_180", "_r_180",
        };

        private static bool PathContainsAny(string lowerPath, string[] tokens)
        {
            foreach (string t in tokens)
                if (lowerPath.Contains(t))
                    return true;
            return false;
        }

        // Loose "starts with token + delimiter" — accepts "block_start" but not "midblock".
        private static bool ContainsTokenStart(string lowerName, string token)
        {
            if (lowerName.StartsWith(token + "_", StringComparison.Ordinal)) return true;
            if (lowerName.StartsWith(token + "-", StringComparison.Ordinal)) return true;
            if (lowerName.StartsWith(token + " ", StringComparison.Ordinal)) return true;
            if (lowerName.Equals(token, StringComparison.Ordinal)) return true;
            // Also accept if the token sits between word boundaries inside the name.
            return lowerName.Contains("_" + token + "_")
                || lowerName.Contains("_" + token + ".")
                || lowerName.EndsWith("_" + token, StringComparison.Ordinal);
        }

        private static bool EndsWithSuffix(string lowerName, string suffix)
        {
            return lowerName.EndsWith("_" + suffix, StringComparison.Ordinal)
                || lowerName.EndsWith(" - " + suffix, StringComparison.Ordinal)
                || lowerName.EndsWith("-" + suffix, StringComparison.Ordinal);
        }

        private static bool HasDirectionalSuffix(string lowerName)
        {
            foreach (string s in sDirectionalSuffixes)
                if (lowerName.EndsWith(s, StringComparison.Ordinal))
                    return true;
            return false;
        }

        private static bool HasAttackContext(string lowerName)
        {
            foreach (string p in sAttackPrefixes)
                if (lowerName.StartsWith(p, StringComparison.Ordinal))
                    return true;
            return false;
        }

        private static bool HasPhaseSuffix(string lowerName)
        {
            foreach (string s in sPhaseSuffixes)
                if (lowerName.EndsWith(s, StringComparison.Ordinal))
                    return true;
            return false;
        }
    }
}
