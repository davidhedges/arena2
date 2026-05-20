#nullable enable
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Arena.Presentation
{
    public readonly struct ArenaWeaponMountCalibrationEntry
    {
        public ArenaWeaponMountCalibrationEntry(
            string mountId,
            string markerName,
            Vector3 localPosition,
            Quaternion localRotation)
        {
            MountId = mountId;
            MarkerName = markerName;
            LocalPosition = localPosition;
            LocalRotation = localRotation;
        }

        public string MountId { get; }
        public string MarkerName { get; }
        public Vector3 LocalPosition { get; }
        public Quaternion LocalRotation { get; }
    }

    /// <summary>
    /// Recovered Arena-authored weapon mount marker offsets from Resources/PlayerArmature.
    /// Keep preview/generated avatars using the same calibration as the tuned runtime prefab.
    /// </summary>
    public static class ArenaWeaponMountCalibration
    {
        public static readonly ArenaWeaponMountCalibrationEntry MainHand = new(
            AvatarWeaponMounts.MainHandMountId,
            "Arena_Transferred_main_hand",
            new Vector3(-0.036166064f, 0.0062184604f, 0.014170539f),
            new Quaternion(0.27754498f, 0.39617962f, 0.7730576f, -0.41035676f));

        public static readonly ArenaWeaponMountCalibrationEntry OffHand = new(
            AvatarWeaponMounts.OffHandMountId,
            "Arena_Transferred_off_hand",
            new Vector3(0.036388822f, -0.00574835f, -0.013690099f),
            new Quaternion(0.24407613f, 0.41032636f, 0.77153635f, -0.4204648f));

        public static readonly ArenaWeaponMountCalibrationEntry MainSheath = new(
            AvatarWeaponMounts.MainStowedMountId,
            "Arena_Transferred_main_sheath",
            new Vector3(0.026243389f, -0.35161695f, -0.30412492f),
            new Quaternion(-0.07671611f, 0.071116015f, 0.64491343f, 0.75706273f));

        public static readonly ArenaWeaponMountCalibrationEntry OffSheath = new(
            AvatarWeaponMounts.OffStowedMountId,
            "Arena_Transferred_off_sheath",
            new Vector3(-0.015168376f, 0.038233593f, 0.09002265f),
            new Quaternion(-0.55886906f, -0.59340125f, -0.31553155f, 0.4857779f));

        public static readonly ArenaWeaponMountCalibrationEntry ArcherBowStowed = new(
            AvatarWeaponMounts.ArcherBowStowedMountId,
            "Arena_Archer_bow_stowed",
            new Vector3(0.03837f, 0.20708999f, 0.093219995f),
            new Quaternion(-0.37833667f, 0.60061145f, 0.64977986f, 0.27187023f));

        // Internal compatibility socket for GreatSwordAnimations clips. The public
        // mount id remains `greatsword_hand`; this name exists only so imported
        // animation curves can drive the socket they were authored against.
        public static readonly ArenaWeaponMountCalibrationEntry GreatswordAnimatedHandSocket = new(
            "greatsword_animation_socket",
            "weapon_r",
            new Vector3(0.11308882f, -0.046288498f, -0.010426121f),
            new Quaternion(-0.000009385194f, -0.7051718f, 0.70903647f, -0.0000002841481f));

        public static readonly ArenaWeaponMountCalibrationEntry GreatswordHand = new(
            AvatarWeaponMounts.GreatswordHandMountId,
            "Arena_Greatsword_hand",
            Vector3.zero,
            Quaternion.identity);

        // Recovered from GreatSwordAnimationPack/Prefabs/9CG_Great_Sword.prefab.
        // The pack's sheath/draw animations are authored against a `sword_holder`
        // bone parented to spine_03 with these exact local pose values.
        public static readonly ArenaWeaponMountCalibrationEntry GreatswordStowed = new(
            AvatarWeaponMounts.GreatswordStowedMountId,
            "Arena_Greatsword_stowed",
            new Vector3(-0.4225688f, 0.15770636f, 0.19300015f),
            new Quaternion(0.6628304f, -0.29997852f, 0.46795887f, 0.50168043f));

        public static readonly ArenaWeaponMountCalibrationEntry ArcherQuiverStowed = new(
            AvatarWeaponMounts.ArcherQuiverStowedMountId,
            "Arena_Archer_quiver_stowed",
            new Vector3(0.017869543f, 0.17381594f, -0.000010065557f),
            new Quaternion(0.5422092f, -0.45388243f, 0.54220915f, 0.45388243f));

        public static Transform? CreateOrUpdateMountChild(Transform? parent, ArenaWeaponMountCalibrationEntry entry)
        {
            if (parent == null)
                return null;

            Transform marker = parent.Find(entry.MarkerName);
            if (marker == null)
                marker = new GameObject(entry.MarkerName).transform;

            marker.SetParent(parent, false);
            marker.localPosition = entry.LocalPosition;
            marker.localRotation = entry.LocalRotation;
            marker.localScale = Vector3.one;
            return marker;
        }
    }

    [Serializable]
    public sealed class AvatarWeaponMountDefinition
    {
        public string mountId = string.Empty;
        public Transform? mount;
    }

    /// <summary>
    /// Owns authored weapon mount transforms for a specific visible avatar prefab.
    /// Different avatar bodies can expose the same semantic mount ids with different
    /// transforms without changing weapon code or weapon assets.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class AvatarWeaponMounts : MonoBehaviour
    {
        public const string MainHandMountId = "main_weapon_hand";
        public const string OffHandMountId = "off_weapon_hand";
        public const string MainStowedMountId = "main_weapon_stowed";
        public const string OffStowedMountId = "off_weapon_stowed";
        public const string MainBackMountId = "main_back";
        public const string OffBackMountId = "off_back";
        public const string MainHipMountId = "main_hip";
        public const string OffHipMountId = "off_hip";
        public const string MainSheathMountId = "main_sheath";
        public const string OffSheathMountId = "off_sheath";
        public const string GreatswordHandMountId = "greatsword_hand";
        public const string GreatswordStowedMountId = "greatsword_stowed";
        public const string ArcherBowHandMountId = "archer_bow_hand";
        public const string ArcherBowStowedMountId = "archer_bow_stowed";
        public const string ArcherQuiverStowedMountId = "archer_quiver_stowed";
        public const string ArcherQuiverBackMountId = "archer_quiver_back";

        public const string LegacyMainHandMountId = "main_hand";
        public const string LegacyOffHandMountId = "off_hand";
        public const string LegacyMainStowedMountId = "main_stowed";
        public const string LegacyOffStowedMountId = "off_stowed";

        private static readonly Dictionary<string, string[]> MountLookupFallbacks = new(StringComparer.Ordinal)
        {
            [MainHandMountId] = new[] { MainHandMountId, LegacyMainHandMountId },
            [LegacyMainHandMountId] = new[] { MainHandMountId, LegacyMainHandMountId },
            [OffHandMountId] = new[] { OffHandMountId, LegacyOffHandMountId },
            [LegacyOffHandMountId] = new[] { OffHandMountId, LegacyOffHandMountId },
            [MainStowedMountId] = new[] { MainStowedMountId, MainSheathMountId, LegacyMainStowedMountId },
            [MainSheathMountId] = new[] { MainStowedMountId, MainSheathMountId, LegacyMainStowedMountId },
            [LegacyMainStowedMountId] = new[] { MainStowedMountId, LegacyMainStowedMountId, MainSheathMountId },
            [OffStowedMountId] = new[] { OffStowedMountId, OffSheathMountId, LegacyOffStowedMountId },
            [OffSheathMountId] = new[] { OffStowedMountId, OffSheathMountId, LegacyOffStowedMountId },
            [LegacyOffStowedMountId] = new[] { OffStowedMountId, LegacyOffStowedMountId, OffSheathMountId },
            [GreatswordHandMountId] = new[] { GreatswordHandMountId, MainHandMountId, LegacyMainHandMountId },
            [GreatswordStowedMountId] = new[] { GreatswordStowedMountId, MainStowedMountId, MainSheathMountId, LegacyMainStowedMountId },
            [ArcherBowHandMountId] = new[] { ArcherBowHandMountId },
            [ArcherBowStowedMountId] = new[] { ArcherBowStowedMountId },
            [ArcherQuiverStowedMountId] = new[] { ArcherQuiverStowedMountId, ArcherQuiverBackMountId },
            [ArcherQuiverBackMountId] = new[] { ArcherQuiverStowedMountId, ArcherQuiverBackMountId },
        };

        [SerializeField] private List<AvatarWeaponMountDefinition> _mounts = new();

        private readonly Dictionary<string, Transform> _mountLookup = new(StringComparer.Ordinal);

        public IReadOnlyList<AvatarWeaponMountDefinition> MountDefinitions => _mounts;

        private void Awake()
        {
            RebuildLookup(logWarnings: true);
        }

        private void OnValidate()
        {
            RebuildLookup(logWarnings: true);
        }

        public bool TryGetMount(string mountId, out Transform mount)
        {
            if (_mountLookup.Count == 0)
                RebuildLookup(logWarnings: false);

            if (string.IsNullOrWhiteSpace(mountId))
            {
                mount = null!;
                return false;
            }

            foreach (string lookupId in EnumerateLookupIds(mountId))
            {
                if (_mountLookup.TryGetValue(lookupId, out var resolvedMount))
                {
                    mount = resolvedMount;
                    return true;
                }
            }

            mount = null!;
            return false;
        }

        public void SetOrReplaceMount(string mountId, Transform mount)
        {
            if (string.IsNullOrWhiteSpace(mountId) || mount == null)
                return;

            mountId = CanonicalizeMountId(mountId);

            for (int i = 0; i < _mounts.Count; i++)
            {
                AvatarWeaponMountDefinition definition = _mounts[i];
                if (definition != null && string.Equals(definition.mountId, mountId, StringComparison.Ordinal))
                {
                    definition.mount = mount;
                    RebuildLookup(logWarnings: false);
                    return;
                }
            }

            _mounts.Add(new AvatarWeaponMountDefinition
            {
                mountId = mountId,
                mount = mount,
            });
            RebuildLookup(logWarnings: false);
        }

        public static string CanonicalizeMountId(string mountId)
        {
            return mountId switch
            {
                LegacyMainHandMountId => MainHandMountId,
                LegacyOffHandMountId => OffHandMountId,
                MainSheathMountId => MainStowedMountId,
                LegacyMainStowedMountId => MainStowedMountId,
                OffSheathMountId => OffStowedMountId,
                LegacyOffStowedMountId => OffStowedMountId,
                ArcherQuiverBackMountId => ArcherQuiverStowedMountId,
                _ => mountId,
            };
        }

        private static IEnumerable<string> EnumerateLookupIds(string mountId)
        {
            if (MountLookupFallbacks.TryGetValue(mountId, out var fallbackIds))
            {
                for (int i = 0; i < fallbackIds.Length; i++)
                    yield return fallbackIds[i];
                yield break;
            }

            yield return mountId;
        }

        private void RebuildLookup(bool logWarnings)
        {
            _mountLookup.Clear();

            for (int i = 0; i < _mounts.Count; i++)
            {
                var definition = _mounts[i];
                if (definition == null)
                    continue;

                if (string.IsNullOrWhiteSpace(definition.mountId))
                {
                    if (logWarnings)
                        Debug.LogWarning($"[{nameof(AvatarWeaponMounts)}] {name} has a mount entry with an empty id.", this);
                    continue;
                }

                if (definition.mount == null)
                {
                    if (logWarnings)
                    {
                        Debug.LogWarning(
                            $"[{nameof(AvatarWeaponMounts)}] {name} mount '{definition.mountId}' has no Transform assigned.",
                            this);
                    }
                    continue;
                }

                if (_mountLookup.ContainsKey(definition.mountId))
                {
                    if (logWarnings)
                    {
                        Debug.LogWarning(
                            $"[{nameof(AvatarWeaponMounts)}] {name} has duplicate mount id '{definition.mountId}'.",
                            this);
                    }
                    continue;
                }

                _mountLookup.Add(definition.mountId, definition.mount);
            }
        }
    }
}
