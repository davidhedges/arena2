#nullable enable
using System;
using Arena.Combat;
using Arena.Entity;
using SpacetimeDB;
using SpacetimeDB.Types;
using UnityEngine;

namespace Arena.Presentation
{
    internal static class CombatVFXAnchorResolver
    {
        private const string AnchorCaster = "CASTER";
        private const string AnchorTarget = "TARGET";
        private const string AnchorOrigin = "ORIGIN";
        private const string AnchorAreaOrigin = "AREA_ORIGIN";
        private const string AnchorImpactPoint = "IMPACT_POINT";
        private const string AnchorGroundUnderCaster = "GROUND_UNDER_CASTER";
        private const string AnchorGroundUnderTarget = "GROUND_UNDER_TARGET";
        private const string AnchorLeftHand = "LEFT_HAND";
        private const string AnchorRightHand = "RIGHT_HAND";
        private const string AnchorWeaponMainHand = "WEAPON_MAIN_HAND";
        private const string AnchorWeaponOffHand = "WEAPON_OFF_HAND";
        private const string AnchorWeaponBladeStart = "WEAPON_BLADE_START";
        private const string AnchorWeaponBladeEnd = "WEAPON_BLADE_END";
        private const string BladeStartMarkerName = "ArenaVFX_BladeStart";
        private const string BladeEndMarkerName = "ArenaVFX_BladeEnd";
        private const float GroundYOffset = 0.03f;

        internal static Vector3 ResolvePosition(CombatVfxAnchorFact fact, CombatVfxCueCatalog cue)
        {
            string anchor = WireIdentifier.Normalize(cue.Anchor);
            if (string.Equals(anchor, AnchorOrigin, StringComparison.Ordinal))
                return fact.Origin;
            if (string.Equals(anchor, AnchorAreaOrigin, StringComparison.Ordinal))
                return fact.Origin + Vector3.up * GroundYOffset;
            if (string.Equals(anchor, AnchorImpactPoint, StringComparison.Ordinal))
                return fact.Point;
            if (string.Equals(anchor, AnchorGroundUnderTarget, StringComparison.Ordinal))
                return fact.Point + Vector3.up * GroundYOffset;
            if (string.Equals(anchor, AnchorGroundUnderCaster, StringComparison.Ordinal))
                return fact.Origin + Vector3.up * GroundYOffset;
            if (TryResolveTransform(fact, anchor, out Transform transform))
                return transform.position;

            return FallbackPosition(fact, anchor);
        }

        internal static Transform? ResolveFollowAnchor(CombatVfxAnchorFact fact, CombatVfxCueCatalog cue)
        {
            string anchor = WireIdentifier.Normalize(cue.Anchor);
            return TryResolveTransform(fact, anchor, out Transform transform) ? transform : null;
        }

        private static Vector3 FallbackPosition(CombatVfxAnchorFact fact, string anchor)
        {
            if (string.Equals(anchor, AnchorCaster, StringComparison.Ordinal)
                || string.Equals(anchor, AnchorLeftHand, StringComparison.Ordinal)
                || string.Equals(anchor, AnchorRightHand, StringComparison.Ordinal)
                || string.Equals(anchor, AnchorWeaponMainHand, StringComparison.Ordinal)
                || string.Equals(anchor, AnchorWeaponOffHand, StringComparison.Ordinal)
                || string.Equals(anchor, AnchorWeaponBladeStart, StringComparison.Ordinal)
                || string.Equals(anchor, AnchorWeaponBladeEnd, StringComparison.Ordinal))
            {
                return fact.Origin;
            }

            return fact.Point;
        }

        private static bool TryResolveTransform(
            CombatVfxAnchorFact fact,
            string anchor,
            out Transform transform)
        {
            transform = null!;

            if (string.Equals(anchor, AnchorCaster, StringComparison.Ordinal))
                return TryResolveEntityTransform(fact.Caster, out transform);
            if (string.Equals(anchor, AnchorTarget, StringComparison.Ordinal))
                return TryResolveEntityTransform(fact.Hit, out transform);
            if (string.Equals(anchor, AnchorLeftHand, StringComparison.Ordinal))
                return TryResolveHumanoidBone(fact.Caster, HumanBodyBones.LeftHand, out transform);
            if (string.Equals(anchor, AnchorRightHand, StringComparison.Ordinal))
                return TryResolveHumanoidBone(fact.Caster, HumanBodyBones.RightHand, out transform);
            if (string.Equals(anchor, AnchorWeaponMainHand, StringComparison.Ordinal))
                return TryResolveWeaponMount(fact.Caster, AvatarWeaponMounts.MainHandMountId, out transform);
            if (string.Equals(anchor, AnchorWeaponOffHand, StringComparison.Ordinal))
                return TryResolveWeaponMount(fact.Caster, AvatarWeaponMounts.OffHandMountId, out transform);
            if (string.Equals(anchor, AnchorWeaponBladeStart, StringComparison.Ordinal))
                return TryResolveWeaponBladeMarker(fact.Caster, AvatarWeaponMounts.MainHandMountId, BladeStartMarkerName, out transform);
            if (string.Equals(anchor, AnchorWeaponBladeEnd, StringComparison.Ordinal))
                return TryResolveWeaponBladeMarker(fact.Caster, AvatarWeaponMounts.MainHandMountId, BladeEndMarkerName, out transform);

            return false;
        }

        private static bool TryResolveEntityTransform(Identity identity, out Transform transform)
        {
            if (EntityRegistry.Instance != null
                && EntityRegistry.Instance.TryGetEntity(identity, out PlayerEntity entity))
            {
                transform = entity.GetPresentationRoot();
                return transform != null;
            }

            transform = null!;
            return false;
        }

        private static bool TryResolveHumanoidBone(Identity identity, HumanBodyBones bone, out Transform transform)
        {
            if (EntityRegistry.Instance != null
                && EntityRegistry.Instance.TryGetEntity(identity, out PlayerEntity entity)
                && entity.TryGetSocketTransform(bone, out transform))
            {
                return true;
            }

            transform = null!;
            return false;
        }

        private static bool TryResolveWeaponMount(Identity identity, string mountId, out Transform transform)
        {
            if (EntityRegistry.Instance != null
                && EntityRegistry.Instance.TryGetEntity(identity, out PlayerEntity entity)
                && entity.TryGetWeaponMount(mountId, out transform))
            {
                return true;
            }

            transform = null!;
            return false;
        }

        private static bool TryResolveWeaponBladeMarker(
            Identity identity,
            string mountId,
            string markerName,
            out Transform transform)
        {
            transform = null!;
            if (EntityRegistry.Instance == null
                || !EntityRegistry.Instance.TryGetEntity(identity, out PlayerEntity entity))
            {
                return false;
            }

            WeaponAttachmentController? attachments = entity.GameObject.GetComponent<WeaponAttachmentController>();
            if (attachments == null || !attachments.TryGetVisibleVisualForMount(mountId, out Transform weaponRoot))
                return false;

            return TryFindDescendant(weaponRoot, markerName, out transform);
        }

        private static bool TryFindDescendant(Transform root, string name, out Transform result)
        {
            if (string.Equals(root.name, name, StringComparison.Ordinal))
            {
                result = root;
                return true;
            }

            for (int i = 0; i < root.childCount; i++)
            {
                if (TryFindDescendant(root.GetChild(i), name, out result))
                    return true;
            }

            result = null!;
            return false;
        }
    }

    internal readonly struct CombatVfxAnchorFact
    {
        public readonly Identity Caster;
        public readonly Identity Hit;
        public readonly Vector3 Origin;
        public readonly Vector3 Point;

        public CombatVfxAnchorFact(Identity caster, Identity hit, Vector3 origin, Vector3 point)
        {
            Caster = caster;
            Hit = hit;
            Origin = origin;
            Point = point;
        }
    }
}
