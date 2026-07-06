#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Arena.Presentation;
using UnityEditor;
using UnityEngine;

namespace Arena.Editor
{
    /// <summary>
    /// One observation of an AnimationClip being referenced by a CombatAnimationSet.
    /// A clip may have multiple observations (referenced by multiple sets, or in multiple
    /// roles within the same set). The authoring tool surfaces conflicts so the user can
    /// resolve them — usually a clip should map to one role, but the same clip may be
    /// reused across sets in the same role.
    /// </summary>
    public readonly struct CombatClipRoleObservation
    {
        public readonly CombatClipRole Role;
        public readonly string SourceAssetPath;
        public readonly string ReferencePath;

        public CombatClipRoleObservation(CombatClipRole role, string sourceAssetPath, string referencePath)
        {
            Role = role;
            SourceAssetPath = sourceAssetPath;
            ReferencePath = referencePath;
        }

        public override string ToString() => $"{Role} ← {SourceAssetPath} {ReferencePath}";
    }

    /// <summary>
    /// Walks every CombatAnimationSet asset and builds a map from AnimationClip → list of
    /// role observations. Pure inference; never writes events.
    /// </summary>
    public static class CombatClipRoleInferer
    {
        [MenuItem("Arena/Animation/Print Combat Clip Roles")]
        public static void PrintCombatClipRolesFromMenu()
        {
            Dictionary<AnimationClip, List<CombatClipRoleObservation>> map = BuildClipRoleMap();
            LogSummary(map);
        }

        public static Dictionary<AnimationClip, List<CombatClipRoleObservation>> BuildClipRoleMap()
        {
            Dictionary<AnimationClip, List<CombatClipRoleObservation>> map = new();
            string[] setGuids = AssetDatabase.FindAssets("t:CombatAnimationSet");

            foreach (string guid in setGuids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                CombatAnimationSet? set = AssetDatabase.LoadAssetAtPath<CombatAnimationSet>(path);
                if (set == null)
                    continue;

                ObserveSet(set, path, map);
            }

            return map;
        }

        private static void ObserveSet(
            CombatAnimationSet set,
            string assetPath,
            Dictionary<AnimationClip, List<CombatClipRoleObservation>> map)
        {
            // Spell cast hold profile. SpellCastHoldExit has no field on the profile right
            // now; the role enum value is retained for forward-compat if an exit clip is
            // re-introduced, but no observations are recorded for it today.
            Add(map, set.defaultSpellCastHold.enter, CombatClipRole.SpellCastHoldEnter, assetPath, ".defaultSpellCastHold.enter");
            Add(map, set.defaultSpellCastHold.idleLoop, CombatClipRole.SpellCastHoldIdle, assetPath, ".defaultSpellCastHold.idleLoop");

            // Spell release clips
            for (int i = 0; i < (set.spells?.Length ?? 0); i++)
            {
                WeaponSpellAnimationEntry entry = set.spells![i];
                if (entry.PlaysReleasePresentation)
                {
                    Add(map, entry.ground, CombatClipRole.SpellRelease, assetPath, $".spells[{i}].ground ({entry.SpellIdOrEmpty})");
                    Add(map, entry.air, CombatClipRole.SpellRelease, assetPath, $".spells[{i}].air ({entry.SpellIdOrEmpty})");
                }
                Add(map, entry.holdOverride.enter, CombatClipRole.SpellCastHoldEnter, assetPath, $".spells[{i}].holdOverride.enter ({entry.SpellIdOrEmpty})");
                Add(map, entry.holdOverride.idleLoop, CombatClipRole.SpellCastHoldIdle, assetPath, $".spells[{i}].holdOverride.idleLoop ({entry.SpellIdOrEmpty})");
            }

            // Melee attacks
            if (set.meleeAttacks != null)
            {
                for (int i = 0; i < set.meleeAttacks.Count; i++)
                {
                    WeaponMeleeAttackAuthoring atk = set.meleeAttacks[i];
                    string id = atk.combat.AuthoredStrikeIdOrDefault;
                    Add(map, atk.clip, CombatClipRole.MeleeStrike, assetPath, $".meleeAttacks[{i}].clip ({id})");

                    Add(map, atk.phasedGround.start, CombatClipRole.PhasedMeleeStart, assetPath, $".meleeAttacks[{i}].phasedGround.start ({id})");
                    Add(map, atk.phasedGround.loop, CombatClipRole.PhasedMeleeLoop, assetPath, $".meleeAttacks[{i}].phasedGround.loop ({id})");
                    Add(map, atk.phasedGround.end, CombatClipRole.PhasedMeleeEnd, assetPath, $".meleeAttacks[{i}].phasedGround.end ({id})");

                    Add(map, atk.phasedAir.start, CombatClipRole.PhasedMeleeStart, assetPath, $".meleeAttacks[{i}].phasedAir.start ({id})");
                    Add(map, atk.phasedAir.loop, CombatClipRole.PhasedMeleeLoop, assetPath, $".meleeAttacks[{i}].phasedAir.loop ({id})");
                    Add(map, atk.phasedAir.end, CombatClipRole.PhasedMeleeEnd, assetPath, $".meleeAttacks[{i}].phasedAir.end ({id})");
                }
            }

            // Status reactions
            for (int i = 0; i < (set.statusReactions?.Length ?? 0); i++)
            {
                StatusReactionAnimationEntry r = set.statusReactions![i];
                Add(map, r.loop, CombatClipRole.StatusReactionLoop, assetPath, $".statusReactions[{i}].loop ({r.StatusKindOrEmpty})");
            }

            // Defensive — stationary
            Add(map, set.parryStart, CombatClipRole.ParryStart, assetPath, ".parryStart");
            Add(map, set.parryHit, CombatClipRole.ParryHit, assetPath, ".parryHit");
            Add(map, set.blockStart, CombatClipRole.BlockStart, assetPath, ".blockStart");
            Add(map, set.blockLoop, CombatClipRole.BlockLoop, assetPath, ".blockLoop");
            Add(map, set.blockEnd, CombatClipRole.BlockEnd, assetPath, ".blockEnd");
            Add(map, set.blockHit, CombatClipRole.BlockHit, assetPath, ".blockHit");
            Add(map, set.blockHitBreak, CombatClipRole.BlockHitBreak, assetPath, ".blockHitBreak");

            // Hit reactions / stagger
            Add(map, set.hitF, CombatClipRole.HitReaction, assetPath, ".hitF");
            Add(map, set.hitB, CombatClipRole.HitReaction, assetPath, ".hitB");
            Add(map, set.hitL, CombatClipRole.HitReaction, assetPath, ".hitL");
            Add(map, set.hitR, CombatClipRole.HitReaction, assetPath, ".hitR");
            Add(map, set.hitCombatF, CombatClipRole.HitReaction, assetPath, ".hitCombatF");
            Add(map, set.hitCombatB, CombatClipRole.HitReaction, assetPath, ".hitCombatB");
            Add(map, set.hitCombatL, CombatClipRole.HitReaction, assetPath, ".hitCombatL");
            Add(map, set.hitCombatR, CombatClipRole.HitReaction, assetPath, ".hitCombatR");
            Add(map, set.airHitF, CombatClipRole.HitReaction, assetPath, ".airHitF");
            Add(map, set.airHitB, CombatClipRole.HitReaction, assetPath, ".airHitB");
            Add(map, set.airHitL, CombatClipRole.HitReaction, assetPath, ".airHitL");
            Add(map, set.airHitR, CombatClipRole.HitReaction, assetPath, ".airHitR");
            Add(map, set.airHitCombatF, CombatClipRole.HitReaction, assetPath, ".airHitCombatF");
            Add(map, set.airHitCombatB, CombatClipRole.HitReaction, assetPath, ".airHitCombatB");
            Add(map, set.airHitCombatL, CombatClipRole.HitReaction, assetPath, ".airHitCombatL");
            Add(map, set.airHitCombatR, CombatClipRole.HitReaction, assetPath, ".airHitCombatR");

            Add(map, set.staggerF, CombatClipRole.Stagger, assetPath, ".staggerF");
            Add(map, set.staggerB, CombatClipRole.Stagger, assetPath, ".staggerB");
            Add(map, set.staggerL, CombatClipRole.Stagger, assetPath, ".staggerL");
            Add(map, set.staggerR, CombatClipRole.Stagger, assetPath, ".staggerR");

            // Knockdown / get up / stun
            Add(map, set.knockdownStart, CombatClipRole.KnockdownStart, assetPath, ".knockdownStart");
            Add(map, set.knockdownLoop, CombatClipRole.KnockdownLoop, assetPath, ".knockdownLoop");
            Add(map, set.getUp, CombatClipRole.GetUp, assetPath, ".getUp");
            Add(map, set.stunStart, CombatClipRole.StunStart, assetPath, ".stunStart");
            Add(map, set.stunLoop, CombatClipRole.StunLoop, assetPath, ".stunLoop");
            Add(map, set.stunEnd, CombatClipRole.StunEnd, assetPath, ".stunEnd");

            // Death
            Add(map, set.death, CombatClipRole.Death, assetPath, ".death");

            // Weapon transitions
            WeaponPresentationProfile? wp = set.WeaponPresentation;
            if (wp != null)
            {
                Add(map, wp.DrawWeapon, CombatClipRole.DrawWeapon, assetPath, ".weaponPresentation.drawWeapon");
                Add(map, wp.SheathWeapon, CombatClipRole.SheathWeapon, assetPath, ".weaponPresentation.sheathWeapon");
            }
        }

        private static void Add(
            Dictionary<AnimationClip, List<CombatClipRoleObservation>> map,
            AnimationClip? clip,
            CombatClipRole role,
            string assetPath,
            string referencePath)
        {
            if (clip == null)
                return;

            if (!map.TryGetValue(clip, out List<CombatClipRoleObservation>? list))
            {
                list = new List<CombatClipRoleObservation>();
                map[clip] = list;
            }
            list.Add(new CombatClipRoleObservation(role, assetPath, referencePath));
        }

        private static void LogSummary(Dictionary<AnimationClip, List<CombatClipRoleObservation>> map)
        {
            // Per-role count of distinct clips
            Dictionary<CombatClipRole, HashSet<AnimationClip>> distinctByRole = new();
            int conflictCount = 0;

            foreach (KeyValuePair<AnimationClip, List<CombatClipRoleObservation>> kvp in map)
            {
                AnimationClip clip = kvp.Key;
                List<CombatClipRoleObservation> obs = kvp.Value;
                HashSet<CombatClipRole> roles = new(obs.Select(o => o.Role));

                if (roles.Count > 1)
                {
                    conflictCount++;
                    StringBuilder sb = new();
                    sb.Append($"[CombatClipRoles] CONFLICT: {AssetPath(clip)} mapped to {roles.Count} roles: ");
                    sb.Append(string.Join(", ", roles));
                    foreach (CombatClipRoleObservation o in obs)
                        sb.Append($"\n    - {o.Role} via {o.SourceAssetPath} {o.ReferencePath}");
                    Debug.LogWarning(sb.ToString());
                }

                foreach (CombatClipRole role in roles)
                {
                    if (!distinctByRole.TryGetValue(role, out HashSet<AnimationClip>? set))
                    {
                        set = new HashSet<AnimationClip>();
                        distinctByRole[role] = set;
                    }
                    set.Add(clip);
                }
            }

            StringBuilder summary = new();
            summary.AppendLine("[CombatClipRoles] Summary (distinct clip count per role):");
            foreach (CombatClipRole role in System.Enum.GetValues(typeof(CombatClipRole)))
            {
                int count = distinctByRole.TryGetValue(role, out HashSet<AnimationClip>? clips) ? clips.Count : 0;
                if (count > 0)
                    summary.AppendLine($"    {role}: {count}");
            }
            summary.AppendLine($"    Total distinct clips referenced: {map.Count}");
            summary.AppendLine($"    Conflicts: {conflictCount}");
            Debug.Log(summary.ToString());
        }

        private static string AssetPath(AnimationClip clip)
        {
            string path = AssetDatabase.GetAssetPath(clip);
            return string.IsNullOrEmpty(path) ? clip.name : path;
        }
    }
}
