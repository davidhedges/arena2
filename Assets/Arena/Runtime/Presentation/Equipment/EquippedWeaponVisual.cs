#nullable enable
using Arena.Presentation.Appearance;
using UnityEngine;

namespace Arena.Presentation
{
    public readonly struct EquippedWeaponVisual
    {
        public EquippedWeaponVisual(
            string roleId,
            string itemDefId,
            GameObject prefab,
            WeaponAppearancePlacementProfile placementProfile = WeaponAppearancePlacementProfile.LegacyAnimationBinding)
        {
            RoleId = roleId;
            ItemDefId = itemDefId;
            Prefab = prefab;
            PlacementProfile = placementProfile;
        }

        public string RoleId { get; }
        public string ItemDefId { get; }
        public GameObject Prefab { get; }
        public WeaponAppearancePlacementProfile PlacementProfile { get; }
    }

    public readonly struct WeaponAppearancePlacement
    {
        public WeaponAppearancePlacement(string mountId, Quaternion localRotation)
        {
            MountId = mountId;
            LocalPosition = Vector3.zero;
            LocalRotation = localRotation;
            LocalScaleMultiplier = Vector3.one;
        }

        public string MountId { get; }
        public Vector3 LocalPosition { get; }
        public Quaternion LocalRotation { get; }
        public Vector3 LocalScaleMultiplier { get; }
    }

    /// <summary>
    /// Resolves only opt-in appearance placement. A false result is the compatibility
    /// boundary that keeps the animation-set mount and offsets exactly as authored.
    /// </summary>
    public static class WeaponAppearancePlacementResolver
    {
        // The animation-pack dagger mesh points along local +Y. Composing the
        // original Arena hand calibration and Daggers binding into the N-Hance
        // socket frames makes that direction (-0.115694, 0.025350, -0.992961)
        // for the main hand and (0.124274, 0.036365, -0.991581) for the mirrored
        // off hand. Raw N-Hance dagger blades point along local +Z, so these are
        // the minimal from-to rotations that reproduce the pack's reverse grip
        // while retaining the native socket roll as closely as possible.
        private static readonly Quaternion NHanceDaggerMainReverseGrip = new(
            -0.213657289f,
            -0.975105758f,
            0f,
            0.059323895f);

        private static readonly Quaternion NHanceDaggerOffReverseGrip = new(
            -0.280249268f,
            0.957732224f,
            0f,
            0.064879383f);

        public static bool TryResolve(
            WeaponAppearancePlacementProfile profile,
            string? roleId,
            bool inCombat,
            out WeaponAppearancePlacement placement)
        {
            if (profile != WeaponAppearancePlacementProfile.NHanceNative)
            {
                placement = default;
                return false;
            }

            string mountId = roleId switch
            {
                "greatsword" => inCombat
                    ? AvatarWeaponMounts.NHanceGreatswordHandMountId
                    : AvatarWeaponMounts.NHanceBack2HLMountId,
                "staff" => inCombat
                    ? AvatarWeaponMounts.NHanceStaffHandMountId
                    : AvatarWeaponMounts.NHanceStaffStowedMountId,
                "sword" => inCombat
                    ? AvatarWeaponMounts.NHanceWeaponRMountId
                    : AvatarWeaponMounts.NHanceBackLMountId,
                "shield" => inCombat
                    ? AvatarWeaponMounts.NHanceShieldMountId
                    : AvatarWeaponMounts.NHanceBackRMountId,
                "dagger_main" => inCombat
                    ? AvatarWeaponMounts.NHanceWeaponRMountId
                    : AvatarWeaponMounts.NHanceHipRMountId,
                "dagger_off" => inCombat
                    ? AvatarWeaponMounts.NHanceWeaponLMountId
                    : AvatarWeaponMounts.NHanceHipLMountId,
                "bow_drawn" => AvatarWeaponMounts.NHanceWeaponLMountId,
                "bow_stowed" => AvatarWeaponMounts.NHanceBackBowMountId,
                "quiver" => AvatarWeaponMounts.NHanceQuiverMountId,
                _ => string.Empty,
            };

            if (string.IsNullOrEmpty(mountId))
            {
                placement = default;
                return false;
            }

            Quaternion localRotation = roleId switch
            {
                "dagger_main" when inCombat => NHanceDaggerMainReverseGrip,
                "dagger_off" when inCombat => NHanceDaggerOffReverseGrip,
                _ => Quaternion.identity,
            };

            placement = new WeaponAppearancePlacement(mountId, localRotation);
            return true;
        }
    }
}
