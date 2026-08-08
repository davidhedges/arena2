#nullable enable
using System;
using System.Collections.Generic;
using Arena.Presentation;
using SpacetimeDB.Types;
using UnityEngine;

namespace Arena.Presentation.Appearance
{
    [Serializable]
    public sealed class AvatarVfxSocketDefinition
    {
        public string socketId = string.Empty;
        public Transform? socket;
    }

    /// <summary>
    /// Semantic VFX sockets owned by a visible avatar. Runtime effects resolve these ids instead
    /// of depending on publisher-specific bone names or weapon-mount calibration.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class AvatarVfxSockets : MonoBehaviour
    {
        public const string BackSocketId = "back";
        public const string BackMarkerName = "ArenaVFX_Back";

        // Preserve the established Celestial Mantle placement while moving its parent from the
        // presentation root to an animated torso bone. The marker owns avatar calibration; the
        // VFX registry can therefore keep the prefab at socket-local zero.
        internal static readonly Vector3 BackReferenceLocalPosition = new(0f, 1.1f, -0.25f);

        [SerializeField] private List<AvatarVfxSocketDefinition> sockets = new();

        private readonly Dictionary<string, Transform> _socketLookup =
            new(StringComparer.Ordinal);

        public IReadOnlyList<AvatarVfxSocketDefinition> SocketDefinitions => sockets;

        private void Awake()
        {
            RebuildLookup();
        }

        private void OnValidate()
        {
            RebuildLookup();
        }

        public bool TryGetSocket(string socketId, out Transform socket)
        {
            if (_socketLookup.Count == 0)
                RebuildLookup();

            string normalized = NormalizeSocketId(socketId);
            if (!string.IsNullOrEmpty(normalized)
                && _socketLookup.TryGetValue(normalized, out Transform resolved))
            {
                socket = resolved;
                return true;
            }

            socket = null!;
            return false;
        }

        public void SetOrReplaceSocket(string socketId, Transform socket)
        {
            string normalized = NormalizeSocketId(socketId);
            if (string.IsNullOrEmpty(normalized) || socket == null)
                return;

            for (int i = 0; i < sockets.Count; i++)
            {
                AvatarVfxSocketDefinition definition = sockets[i];
                if (definition != null
                    && string.Equals(NormalizeSocketId(definition.socketId), normalized, StringComparison.Ordinal))
                {
                    definition.socketId = normalized;
                    definition.socket = socket;
                    RebuildLookup();
                    return;
                }
            }

            sockets.Add(new AvatarVfxSocketDefinition
            {
                socketId = normalized,
                socket = socket,
            });
            RebuildLookup();
        }

        public static AvatarVfxSockets EnsureOn(GameObject avatarRoot, Animator animator)
        {
            // Unity's fake-null makes `??` unusable on GetComponent results; compare with the
            // overloaded == so a missing component actually falls through to AddComponent.
            AvatarVfxSockets existing = avatarRoot.GetComponent<AvatarVfxSockets>();
            AvatarVfxSockets result = existing != null
                ? existing
                : avatarRoot.AddComponent<AvatarVfxSockets>();

            Transform? backParent = ResolveNamedDescendant(avatarRoot.transform, "spine_03")
                ?? ResolveHumanoidBone(animator, HumanBodyBones.UpperChest)
                ?? ResolveHumanoidBone(animator, HumanBodyBones.Chest)
                ?? ResolveHumanoidBone(animator, HumanBodyBones.Spine);
            if (backParent == null)
                return result;

            Transform? marker = backParent.Find(BackMarkerName);
            if (marker == null)
            {
                marker = new GameObject(BackMarkerName).transform;
                marker.SetParent(backParent, false);
                marker.position = avatarRoot.transform.TransformPoint(BackReferenceLocalPosition);
                marker.rotation = avatarRoot.transform.rotation;
                marker.localScale = Vector3.one;
            }

            result.SetOrReplaceSocket(BackSocketId, marker);
            return result;
        }

        private static Transform? ResolveHumanoidBone(Animator? animator, HumanBodyBones bone)
        {
            if (animator == null || !animator.isHuman)
                return null;

            try
            {
                return animator.GetBoneTransform(bone);
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        private static Transform? ResolveNamedDescendant(Transform root, string transformName)
        {
            Transform[] descendants = root.GetComponentsInChildren<Transform>(includeInactive: true);
            for (int i = 0; i < descendants.Length; i++)
            {
                if (string.Equals(descendants[i].name, transformName, StringComparison.Ordinal))
                    return descendants[i];
            }

            return null;
        }

        private void RebuildLookup()
        {
            _socketLookup.Clear();
            for (int i = 0; i < sockets.Count; i++)
            {
                AvatarVfxSocketDefinition definition = sockets[i];
                if (definition == null || definition.socket == null)
                    continue;

                string normalized = NormalizeSocketId(definition.socketId);
                if (!string.IsNullOrEmpty(normalized) && !_socketLookup.ContainsKey(normalized))
                    _socketLookup.Add(normalized, definition.socket);
            }
        }

        private static string NormalizeSocketId(string? value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : value.Trim().ToLowerInvariant();
        }
    }

    public sealed class RuntimeAvatarBinding
    {
        public RuntimeAvatarBinding(
            GameObject avatarRoot,
            Animator animator,
            AvatarWeaponMounts mounts,
            AvatarVfxSockets vfxSockets,
            Renderer[] renderers,
            string appearanceSignature)
        {
            AvatarRoot = avatarRoot;
            Animator = animator;
            Mounts = mounts;
            VfxSockets = vfxSockets;
            Renderers = renderers;
            AppearanceSignature = appearanceSignature;
        }

        public GameObject AvatarRoot { get; }
        public Animator Animator { get; }
        public AvatarWeaponMounts Mounts { get; }
        public AvatarVfxSockets VfxSockets { get; }
        public Renderer[] Renderers { get; internal set; }
        public string AppearanceSignature { get; }

        public Vector3? GetSocketPosition(HumanBodyBones bone)
        {
            Transform? socket = Animator != null ? Animator.GetBoneTransform(bone) : null;
            return socket != null ? socket.position : null;
        }

        public bool TryGetSocketTransform(HumanBodyBones bone, out Transform socket)
        {
            socket = Animator != null ? Animator.GetBoneTransform(bone) : null!;
            return socket != null;
        }

        public bool TryGetMount(string mountId, out Transform mount)
        {
            return Mounts.TryGetMount(mountId, out mount);
        }

        public bool TryGetVfxSocket(string socketId, out Transform socket)
        {
            return VfxSockets.TryGetSocket(socketId, out socket);
        }
    }

    [DisallowMultipleComponent]
    public sealed class RuntimeAvatarController : MonoBehaviour
    {
        [SerializeField] private Transform? visualRootParent;

        private Transform? _visualRoot;
        private GameObject? _activeAvatar;
        private RuntimeAvatarBinding? _currentBinding;
        private readonly Dictionary<string, string> _equippedArmorBySlot = new(System.StringComparer.Ordinal);
        private CharacterAppearanceSelection _currentSelection;
        private string _baseAppearanceSignature = string.Empty;
        private bool _hasCurrentSelection;
        private bool _hasEquipmentAppearanceOverride;
        private bool _legacyVisualsHidden;

        public Transform? VisualRoot => _visualRoot;
        public RuntimeAvatarBinding? CurrentBinding => _currentBinding;

        public void SetVisualRootParent(Transform? parent)
        {
            visualRootParent = parent;
            if (_visualRoot != null && parent != null && _visualRoot.parent != parent)
                _visualRoot.SetParent(parent, false);
        }

        public static string SignatureFor(CharacterAppearance row)
        {
            return string.Join("|",
                CharacterAppearanceIds.Normalize(row.RaceId),
                CharacterAppearanceIds.Normalize(row.SexId),
                CharacterAppearanceIds.Normalize(row.BodyId),
                CharacterAppearanceIds.Normalize(row.HeadId),
                CharacterAppearanceIds.Normalize(row.FaceId),
                CharacterAppearanceIds.Normalize(row.HairId),
                CharacterAppearanceIds.Normalize(row.EyesId),
                CharacterAppearanceIds.Normalize(row.OutfitId));
        }

        public static string SignatureFor(CharacterAppearanceSelection selection)
        {
            selection.NormalizeInPlace();
            return string.Join("|",
                selection.raceId,
                selection.sexId,
                selection.bodyId,
                selection.headId,
                selection.faceId,
                selection.hairId,
                selection.eyesId,
                selection.outfitId);
        }

        public bool Apply(CharacterAppearance row, out RuntimeAvatarBinding binding, out string error)
        {
            var selection = new CharacterAppearanceSelection
            {
                raceId = row.RaceId,
                sexId = row.SexId,
                bodyId = row.BodyId,
                headId = row.HeadId,
                faceId = row.FaceId,
                hairId = row.HairId,
                eyesId = row.EyesId,
                outfitId = row.OutfitId,
            };

            return Apply(selection, SignatureFor(row), out binding, out error);
        }

        public bool ApplyStarterDefault(out RuntimeAvatarBinding binding, out string error)
        {
            if (!CharacterAppearanceCatalogSet.TryLoadDefault(out CharacterAppearanceCatalogSet catalogs, out error))
            {
                binding = null!;
                return false;
            }

            CharacterAppearanceSelection selection =
                CharacterAppearanceSelection.DefaultHumanMale(CharacterAppearanceIds.DefaultOutfitId);
            return Apply(selection, SignatureFor(selection), out binding, out error, catalogs);
        }

        public bool Apply(
            CharacterAppearanceSelection selection,
            string appearanceSignature,
            out RuntimeAvatarBinding binding,
            out string error)
        {
            if (!CharacterAppearanceCatalogSet.TryLoadDefault(out CharacterAppearanceCatalogSet catalogs, out error))
            {
                binding = null!;
                return false;
            }

            return Apply(selection, appearanceSignature, out binding, out error, catalogs);
        }

        public bool SetEquipmentAppearanceOverride(
            IReadOnlyDictionary<string, string> equippedArmorBySlot,
            out RuntimeAvatarBinding binding,
            out string error)
        {
            _equippedArmorBySlot.Clear();
            foreach (var pair in equippedArmorBySlot)
            {
                string slotId = CharacterAppearanceIds.Normalize(pair.Key);
                if (string.IsNullOrWhiteSpace(slotId) || string.IsNullOrWhiteSpace(pair.Value))
                    continue;

                _equippedArmorBySlot[slotId] = CharacterAppearanceIds.Normalize(pair.Value);
            }

            _hasEquipmentAppearanceOverride = true;
            if (!_hasCurrentSelection)
            {
                binding = null!;
                error = string.Empty;
                return false;
            }

            if (!CharacterAppearanceCatalogSet.TryLoadDefault(out CharacterAppearanceCatalogSet catalogs, out error))
            {
                binding = null!;
                return false;
            }

            return Apply(_currentSelection, _baseAppearanceSignature, out binding, out error, catalogs);
        }

        public Renderer[] RefreshRenderers()
        {
            if (_currentBinding == null || _activeAvatar == null)
                return Array.Empty<Renderer>();

            Renderer[] renderers = CollectEnabledRenderers(_activeAvatar);
            _currentBinding.Renderers = renderers;
            return renderers;
        }

        public void Clear()
        {
            DestroyAvatar(_activeAvatar);
            _activeAvatar = null;
            _currentBinding = null;
        }

        private bool Apply(
            CharacterAppearanceSelection selection,
            string appearanceSignature,
            out RuntimeAvatarBinding binding,
            out string error,
            CharacterAppearanceCatalogSet catalogs)
        {
            selection.NormalizeInPlace();
            string baseAppearanceSignature = string.IsNullOrWhiteSpace(appearanceSignature)
                ? SignatureFor(selection)
                : appearanceSignature;
            _currentSelection = selection;
            _baseAppearanceSignature = baseAppearanceSignature;
            _hasCurrentSelection = true;
            string effectiveAppearanceSignature = BuildEffectiveAppearanceSignature(baseAppearanceSignature);
            IReadOnlyDictionary<string, string>? equippedArmorBySlot = _hasEquipmentAppearanceOverride
                ? _equippedArmorBySlot
                : null;

            bool applied = false;
            error = string.Empty;
            if (_activeAvatar != null)
            {
                applied = CharacterAvatarAssembler.TryApplyToExisting(
                    _activeAvatar,
                    selection,
                    catalogs,
                    out error,
                    equippedArmorBySlot);
            }

            if (!applied)
            {
                Transform root = GetOrCreateVisualRoot();
                if (!CharacterAvatarAssembler.TryAssemble(
                        selection,
                        catalogs,
                        root,
                        out GameObject avatar,
                        out error,
                        existingAvatar: _activeAvatar,
                        equippedArmorBySlot: equippedArmorBySlot))
                {
                    binding = null!;
                    return false;
                }

                _activeAvatar = avatar;
            }

            HideLegacySkinnedVisuals();
            if (!TryCreateBinding(effectiveAppearanceSignature, out binding, out error))
                return false;

            _currentBinding = binding;
            RefreshRenderers();
            return true;
        }

        private string BuildEffectiveAppearanceSignature(string baseAppearanceSignature)
        {
            if (!_hasEquipmentAppearanceOverride)
                return baseAppearanceSignature;

            var parts = new List<string>(_equippedArmorBySlot.Count);
            foreach (var pair in _equippedArmorBySlot)
                parts.Add($"{pair.Key}:{pair.Value}");
            parts.Sort(System.StringComparer.Ordinal);
            return $"{baseAppearanceSignature}|gear={string.Join(",", parts)}";
        }

        private Transform GetOrCreateVisualRoot()
        {
            if (_visualRoot != null)
                return _visualRoot;

            Transform parent = visualRootParent != null ? visualRootParent : transform;
            _visualRoot = new GameObject("RuntimeAppearanceRoot").transform;
            _visualRoot.SetParent(parent, false);
            _visualRoot.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
            _visualRoot.localScale = Vector3.one;
            return _visualRoot;
        }

        private bool TryCreateBinding(string appearanceSignature, out RuntimeAvatarBinding binding, out string error)
        {
            if (_activeAvatar == null)
            {
                binding = null!;
                error = "Runtime avatar has not been assembled.";
                return false;
            }

            Animator? animator = _activeAvatar.GetComponentInChildren<Animator>(includeInactive: true);
            if (animator == null)
            {
                binding = null!;
                error = $"Runtime avatar '{_activeAvatar.name}' is missing an Animator.";
                return false;
            }

            AvatarWeaponMounts? mounts = _activeAvatar.GetComponent<AvatarWeaponMounts>();
            if (mounts == null)
                mounts = _activeAvatar.GetComponentInChildren<AvatarWeaponMounts>(includeInactive: true);
            if (mounts == null)
            {
                binding = null!;
                error = $"Runtime avatar '{_activeAvatar.name}' is missing {nameof(AvatarWeaponMounts)}.";
                return false;
            }

            AvatarVfxSockets vfxSockets = AvatarVfxSockets.EnsureOn(_activeAvatar, animator);
            binding = new RuntimeAvatarBinding(
                _activeAvatar,
                animator,
                mounts,
                vfxSockets,
                CollectEnabledRenderers(_activeAvatar),
                appearanceSignature);
            error = string.Empty;
            return true;
        }

        private void HideLegacySkinnedVisuals()
        {
            if (_legacyVisualsHidden || _visualRoot == null)
                return;

            foreach (var renderer in gameObject.GetComponentsInChildren<SkinnedMeshRenderer>(includeInactive: true))
            {
                if (renderer == null || IsChildOf(renderer.transform, _visualRoot))
                    continue;

                renderer.enabled = false;
            }

            foreach (var animator in gameObject.GetComponentsInChildren<Animator>(includeInactive: true))
            {
                if (animator == null || IsChildOf(animator.transform, _visualRoot))
                    continue;

                animator.enabled = false;
            }

            _legacyVisualsHidden = true;
        }

        private static Renderer[] CollectEnabledRenderers(GameObject root)
        {
            var renderers = root.GetComponentsInChildren<Renderer>(includeInactive: true);
            var enabledRenderers = new List<Renderer>(renderers.Length);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer != null && renderer.enabled)
                    enabledRenderers.Add(renderer);
            }

            return enabledRenderers.ToArray();
        }

        private static bool IsChildOf(Transform child, Transform potentialParent)
        {
            Transform? current = child;
            while (current != null)
            {
                if (current == potentialParent)
                    return true;

                current = current.parent;
            }

            return false;
        }

        private static void DestroyAvatar(GameObject? avatar)
        {
            if (avatar == null)
                return;

            if (Application.isPlaying)
                Destroy(avatar);
            else
                DestroyImmediate(avatar);
        }
    }
}
