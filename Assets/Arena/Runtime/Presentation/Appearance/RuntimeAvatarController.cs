#nullable enable
using System;
using System.Collections.Generic;
using Arena.Presentation;
using SpacetimeDB.Types;
using UnityEngine;

namespace Arena.Presentation.Appearance
{
    public sealed class RuntimeAvatarBinding
    {
        public RuntimeAvatarBinding(
            GameObject avatarRoot,
            Animator animator,
            AvatarWeaponMounts mounts,
            Renderer[] renderers,
            string appearanceSignature)
        {
            AvatarRoot = avatarRoot;
            Animator = animator;
            Mounts = mounts;
            Renderers = renderers;
            AppearanceSignature = appearanceSignature;
        }

        public GameObject AvatarRoot { get; }
        public Animator Animator { get; }
        public AvatarWeaponMounts Mounts { get; }
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
    }

    [DisallowMultipleComponent]
    public sealed class RuntimeAvatarController : MonoBehaviour
    {
        [SerializeField] private Transform? visualRootParent;

        private Transform? _visualRoot;
        private GameObject? _activeAvatar;
        private RuntimeAvatarBinding? _currentBinding;
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

        public bool ApplyClassDefault(string? classId, out RuntimeAvatarBinding binding, out string error)
        {
            if (!CharacterAppearanceCatalogSet.TryLoadDefault(out CharacterAppearanceCatalogSet catalogs, out error))
            {
                binding = null!;
                return false;
            }

            string outfitId = CharacterAppearanceIds.DefaultOutfitId;
            if (!catalogs.ClassOutfitCatalog.TryGetDefaultOutfitId(
                    classId,
                    CharacterAppearanceIds.DefaultRaceId,
                    CharacterAppearanceIds.DefaultSexId,
                    out outfitId))
            {
                outfitId = CharacterAppearanceIds.DefaultOutfitId;
            }

            CharacterAppearanceSelection selection = CharacterAppearanceSelection.DefaultHumanMale(outfitId);
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
            appearanceSignature = string.IsNullOrWhiteSpace(appearanceSignature)
                ? SignatureFor(selection)
                : appearanceSignature;

            bool applied = false;
            error = string.Empty;
            if (_activeAvatar != null)
                applied = CharacterAvatarAssembler.TryApplyToExisting(_activeAvatar, selection, catalogs, out error);

            if (!applied)
            {
                Transform root = GetOrCreateVisualRoot();
                if (!CharacterAvatarAssembler.TryAssemble(
                        selection,
                        catalogs,
                        root,
                        out GameObject avatar,
                        out error,
                        existingAvatar: _activeAvatar))
                {
                    binding = null!;
                    return false;
                }

                _activeAvatar = avatar;
            }

            HideLegacySkinnedVisuals();
            if (!TryCreateBinding(appearanceSignature, out binding, out error))
                return false;

            _currentBinding = binding;
            RefreshRenderers();
            return true;
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

            binding = new RuntimeAvatarBinding(
                _activeAvatar,
                animator,
                mounts,
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
