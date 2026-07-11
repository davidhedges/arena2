#nullable enable
using System;
using System.Collections.Generic;
using Arena.Entity;
using UnityEngine;

namespace Arena.Presentation
{
    public sealed class NpcAnimationController : MonoBehaviour
    {
        private const float CrossFadeDuration = 0.06f;
        private const float HitCooldownSeconds = 0.18f;
        private const float DefaultHitReturnDelay = 0.75f;
        private const float LocomotionTimeoutSeconds = 0.2f;
        private const float RunSpeedThreshold = 2f;
        private const string IdleStateName = "Idle01";
        private const string WalkForwardStateName = "Walk_Forward";
        private const string RunForwardStateName = "Run_Forward";
        private const string KoboldWarriorSwordShield = "KOBOLD_WARRIOR_RD_SWORD_SHIELD";
        private const string KoboldWarriorSpear = "KOBOLD_WARRIOR_GN_SPEAR";
        private const string KoboldThiefDualSword = "KOBOLD_THIEF_BK_DUAL_SWORD";
        private const string KoboldKnightSwordShield = "KOBOLD_KNIGHT_RD_SWORD_SHIELD";
        private static readonly string[] HitStateCandidates =
        {
            "Combat_1H_Hit",
            "Combat_2HL_Hit",
            "Combat_Unarmed_Hit",
            "Combat_Defend_Hit",
            "Spell_Unarmed_Hit",
        };

        private Animator? _animator;
        private NpcVisualProfile? _visualProfile;
        private string _templateId = string.Empty;
        private float _hideAt = -1f;
        private float _returnToIdleAt = -1f;
        private float _locomotionTimeoutAt = -1f;
        private float _nextHitAllowedAt;
        private bool _dead;
        private bool _locomotionActive;
        private string _activeStateName = string.Empty;
        private string _locomotionStateName = string.Empty;
        private string _hardCrowdControlStatusKind = string.Empty;
        private RuntimeAnimatorController? _statusReactionBaseController;
        private AnimatorOverrideController? _statusReactionOverrideController;
        private bool _hardCrowdControlPoseFrozen;
        private float _animatorSpeedBeforeHardCrowdControl = 1f;
        private readonly Dictionary<string, NpcStatusReactionEntry> _statusReactions = new(StringComparer.Ordinal);

        public static NpcAnimationController Attach(GameObject root)
        {
            var controller = root.GetComponent<NpcAnimationController>();
            if (controller == null)
                controller = root.AddComponent<NpcAnimationController>();
            controller.EnsureAnimator();
            return controller;
        }

        public void SetTemplate(string templateId)
        {
            _templateId = string.IsNullOrWhiteSpace(templateId)
                ? string.Empty
                : templateId.Trim().ToUpperInvariant();
        }

        public void SetVisualProfile(NpcVisualProfile? profile)
        {
            if (_visualProfile == profile)
                return;

            RestoreFrozenHardCrowdControlPose();
            _visualProfile = profile;
            _animator = null;
            EnsureAnimator();
        }

        public void SetStatusReactions(IEnumerable<NpcStatusReactionEntry>? reactions)
        {
            _statusReactions.Clear();
            if (reactions == null)
                return;

            foreach (NpcStatusReactionEntry reaction in reactions)
            {
                if (reaction == null || !reaction.HasAny)
                    continue;

                string key = reaction.StatusKindOrEmpty;
                if (!string.IsNullOrEmpty(key))
                    _statusReactions[key] = reaction;
            }
        }

        public void SetHardCrowdControl(string? statusKind)
        {
            string normalized = NormalizeStatusKind(statusKind);
            if (string.Equals(_hardCrowdControlStatusKind, normalized, StringComparison.Ordinal))
                return;

            if (string.IsNullOrEmpty(normalized))
            {
                ClearHardCrowdControl();
                return;
            }

            _hardCrowdControlStatusKind = normalized;
            RestoreFrozenHardCrowdControlPose();
            _returnToIdleAt = -1f;
            _locomotionActive = false;
            _locomotionTimeoutAt = -1f;
            _locomotionStateName = string.Empty;

            if (!_dead && gameObject.activeSelf && TryPlayStatusReaction(normalized))
                return;

            RestoreStatusReactionController();
            if (!_dead && gameObject.activeSelf && TryFreezeHardCrowdControlPose())
                return;
            if (!_dead && gameObject.activeSelf)
                TryCrossFade(ReadyStateCandidatesForTemplate(), out _);
        }

        public void Revive()
        {
            _dead = false;
            _hideAt = -1f;
            _returnToIdleAt = -1f;
            _locomotionTimeoutAt = -1f;
            _hardCrowdControlStatusKind = string.Empty;
            RestoreFrozenHardCrowdControlPose();
            RestoreStatusReactionController();
            _locomotionActive = false;
            if (!gameObject.activeSelf)
                gameObject.SetActive(true);
        }

        public void SetLocomotionSpeed(float horizontalSpeed, bool forceRun)
        {
            if (_dead || HasHardCrowdControl)
                return;

            if (horizontalSpeed <= 0.05f)
            {
                StopLocomotion();
                return;
            }

            _locomotionActive = true;
            _locomotionTimeoutAt = Time.time + LocomotionTimeoutSeconds;
            _locomotionStateName = forceRun || horizontalSpeed >= RunSpeedThreshold
                ? FirstOrDefault(_visualProfile?.Animations.run, RunForwardStateName)
                : FirstOrDefault(_visualProfile?.Animations.walk, WalkForwardStateName);

            if (_returnToIdleAt <= 0f)
                PlayLocomotion();
        }

        public void StopLocomotion()
        {
            if (HasHardCrowdControl)
                return;

            if (!_locomotionActive)
                return;

            _locomotionActive = false;
            _locomotionTimeoutAt = -1f;
            _locomotionStateName = string.Empty;

            if (!_dead && _returnToIdleAt <= 0f)
                TryCrossFade(ReadyStateCandidatesForTemplate(), out _);
        }

        public void PlayHit()
        {
            if (_dead || HasHardCrowdControl || Time.time < _nextHitAllowedAt)
                return;

            _nextHitAllowedAt = Time.time + HitCooldownSeconds;
            if (TryCrossFade(ProfileCandidates(_visualProfile?.Animations.hit, HitStateCandidates), out string? stateName) && stateName != null)
                _returnToIdleAt = Time.time + ResolveClipLength(stateName, DefaultHitReturnDelay) + 0.05f;
        }

        public void PlayAttack(string? abilityId = null)
        {
            if (_dead || HasHardCrowdControl)
                return;

            if (TryCrossFade(AttackStateCandidatesForTemplate(abilityId), out string? stateName) && stateName != null)
                _returnToIdleAt = Time.time + ResolveClipLength(stateName, DefaultHitReturnDelay) + 0.05f;
        }

        public void RequestCombatAnimation(in CombatAnimationRequest request)
        {
            switch (request.Category)
            {
                case CombatAnimationCategory.AutoAttack:
                case CombatAnimationCategory.MeleeSkill:
                    PlayAttack(request.ActionId);
                    break;
                case CombatAnimationCategory.Spell:
                    PlaySpell(request.SpellPhase);
                    break;
            }
        }

        private void PlaySpell(CombatSpellAnimationPhase phase)
        {
            if (_dead || HasHardCrowdControl || _visualProfile == null)
                return;

            List<string> states = phase switch
            {
                CombatSpellAnimationPhase.HoldStart => _visualProfile.Animations.spellCastStart,
                CombatSpellAnimationPhase.Cancel => _visualProfile.Animations.spellCancel,
                _ => _visualProfile.Animations.spellRelease,
            };
            if (states.Count == 0)
            {
                if (phase != CombatSpellAnimationPhase.HoldStart)
                    TryCrossFade(ReadyStateCandidatesForTemplate(), out _);
                return;
            }
            if (TryCrossFade(ProfileCandidates(states, Array.Empty<string>()), out string? stateName)
                && stateName != null)
            {
                _returnToIdleAt = phase == CombatSpellAnimationPhase.HoldStart
                    ? -1f
                    : Time.time + ResolveClipLength(stateName, DefaultHitReturnDelay) + 0.05f;
            }
        }

        public void PlayDeath()
        {
            if (_dead)
                return;

            _dead = true;
            _returnToIdleAt = -1f;
            _hardCrowdControlStatusKind = string.Empty;
            RestoreFrozenHardCrowdControlPose();
            RestoreStatusReactionController();
            if (!gameObject.activeSelf)
                gameObject.SetActive(true);

            TryCrossFade(ProfileCandidates(_visualProfile?.Animations.death, new[] { "Death" }), out _);
            _hideAt = -1f;
        }

        private void Update()
        {
            if (HasHardCrowdControl)
                return;

            if (!_dead && _returnToIdleAt > 0f && Time.time >= _returnToIdleAt)
            {
                _returnToIdleAt = -1f;
                if (_locomotionActive)
                    PlayLocomotion();
                else
                    TryCrossFade(ReadyStateCandidatesForTemplate(), out _);
            }

            if (!_dead && _locomotionActive && _locomotionTimeoutAt > 0f && Time.time >= _locomotionTimeoutAt)
            {
                _locomotionActive = false;
                _locomotionTimeoutAt = -1f;
                _locomotionStateName = string.Empty;
                if (_returnToIdleAt <= 0f)
                    TryCrossFade(ReadyStateCandidatesForTemplate(), out _);
            }

            if (_hideAt > 0f && Time.time >= _hideAt)
            {
                _hideAt = -1f;
                gameObject.SetActive(false);
            }
        }

        private bool TryCrossFade(string[] stateNames, out string? selectedStateName)
        {
            selectedStateName = null;
            if (!EnsureAnimator())
                return false;

            for (int i = 0; i < stateNames.Length; i++)
            {
                if (TryFindStateHash(stateNames[i], out int layer, out int stateHash))
                {
                    _animator!.CrossFade(stateHash, CrossFadeDuration, layer);
                    selectedStateName = stateNames[i];
                    return true;
                }
            }

            return false;
        }

        private void PlayLocomotion()
        {
            if (HasHardCrowdControl || !_locomotionActive || string.IsNullOrEmpty(_locomotionStateName))
                return;

            if (_activeStateName == _locomotionStateName)
                return;

            string configuredRun = FirstOrDefault(_visualProfile?.Animations.run, RunForwardStateName);
            string fallback = string.Equals(_locomotionStateName, configuredRun, StringComparison.Ordinal)
                ? FirstOrDefault(_visualProfile?.Animations.walk, WalkForwardStateName)
                : configuredRun;
            string idle = FirstOrDefault(_visualProfile?.Animations.idle, IdleStateName);
            TryCrossFade(new[] { _locomotionStateName, fallback, idle }, out _);
        }

        private bool TryPlayStatusReaction(string statusKind)
        {
            if (!_statusReactions.TryGetValue(statusKind, out NpcStatusReactionEntry reaction)
                || reaction.loop == null
                || !EnsureAnimator())
            {
                return false;
            }

            if (reaction.requireHumanoidAvatar && !HasValidHumanoidAvatar())
                return false;

            RestoreStatusReactionController();
            string idle = FirstOrDefault(_visualProfile?.Animations.idle, IdleStateName);
            AnimationClip? slotClip = ResolveControllerClip(idle);
            RuntimeAnimatorController? baseController = _animator!.runtimeAnimatorController;
            if (slotClip == null || baseController == null)
                return false;

            _statusReactionBaseController = baseController;
            _statusReactionOverrideController = new AnimatorOverrideController(baseController);
            _statusReactionOverrideController[slotClip.name] = reaction.loop;
            _animator.runtimeAnimatorController = _statusReactionOverrideController;
            _activeStateName = string.Empty;
            return TryCrossFade(new[] { idle }, out _);
        }

        private void ClearHardCrowdControl()
        {
            _hardCrowdControlStatusKind = string.Empty;
            RestoreFrozenHardCrowdControlPose();
            RestoreStatusReactionController();

            if (_dead || !gameObject.activeSelf)
                return;

            if (_locomotionActive)
                PlayLocomotion();
            else
                TryCrossFade(ReadyStateCandidatesForTemplate(), out _);
        }

        private bool TryFreezeHardCrowdControlPose()
        {
            if (_visualProfile?.HardCrowdControlFallbackPolicy
                    != NpcHardCrowdControlFallbackPolicy.FreezeCurrentPose
                || !EnsureAnimator())
            {
                return false;
            }

            if (!_hardCrowdControlPoseFrozen)
                _animatorSpeedBeforeHardCrowdControl = _animator!.speed;
            _animator!.speed = 0f;
            _hardCrowdControlPoseFrozen = true;
            return true;
        }

        private void RestoreFrozenHardCrowdControlPose()
        {
            if (!_hardCrowdControlPoseFrozen)
                return;

            if (_animator != null)
                _animator.speed = _animatorSpeedBeforeHardCrowdControl;
            _hardCrowdControlPoseFrozen = false;
            _animatorSpeedBeforeHardCrowdControl = 1f;
        }

        private void RestoreStatusReactionController()
        {
            if (_animator != null && _statusReactionBaseController != null)
                _animator.runtimeAnimatorController = _statusReactionBaseController;

            _statusReactionOverrideController = null;
            _statusReactionBaseController = null;
            _activeStateName = string.Empty;
        }

        private bool HasValidHumanoidAvatar()
        {
            if (!EnsureAnimator())
                return false;

            Avatar? avatar = _animator!.avatar;
            return avatar != null && avatar.isValid && avatar.isHuman;
        }

        private string[] ReadyStateCandidatesForTemplate()
        {
            if (_visualProfile != null)
                return _visualProfile.Animations.ready.Count > 0
                    ? _visualProfile.Animations.ready.ToArray()
                    : ProfileCandidates(_visualProfile.Animations.idle, new[] { IdleStateName });

            return _templateId switch
            {
                KoboldWarriorSwordShield => new[] { "Combat_Defend_Ready", "Combat_1H_Ready", "Combat_Unarmed_Ready", IdleStateName },
                KoboldWarriorSpear => new[] { "Combat_2HL_Ready", "Combat_Unarmed_Ready", IdleStateName },
                KoboldThiefDualSword => new[] { "Combat_1H_Ready", "Combat_Unarmed_Ready", IdleStateName },
                KoboldKnightSwordShield => new[] { "Combat_Defend_Ready", "Combat_1H_Ready", "Combat_Unarmed_Ready", IdleStateName },
                _ => new[] { "Combat_Unarmed_Ready", IdleStateName },
            };
        }

        private string[] AttackStateCandidatesForTemplate(string? abilityId)
        {
            if (_visualProfile != null
                && _visualProfile.TryGetActionAnimationStates(abilityId, out IReadOnlyList<string> states))
            {
                var candidates = new string[states.Count];
                for (int i = 0; i < states.Count; i++)
                    candidates[i] = states[i];
                return candidates;
            }

            if (_visualProfile != null && _visualProfile.Animations.basicAttack.Count > 0)
                return _visualProfile.Animations.basicAttack.ToArray();

            return _templateId switch
            {
                KoboldWarriorSwordShield => new[] { "Combat_1H_Attack", "Combat_Defend_Attack", "Combat_Unarmed_Attack" },
                KoboldWarriorSpear => new[] { "Combat_2HL_Attack", "Combat_2HL_Attack01", "Combat_Unarmed_Attack" },
                KoboldThiefDualSword => new[] { "Combat_1H_AttackDual", "Combat_1H_Attack", "Combat_Unarmed_Attack" },
                KoboldKnightSwordShield => new[] { "Combat_1H_Attack", "Combat_Defend_Attack", "Combat_Unarmed_Attack" },
                _ => new[] { "Combat_Unarmed_Attack" },
            };
        }

        private bool TryFindStateHash(string stateName, out int layer, out int stateHash)
        {
            layer = 0;
            stateHash = 0;
            if (!EnsureAnimator())
                return false;

            int layerCount = Mathf.Max(1, _animator!.layerCount);
            for (int i = 0; i < layerCount; i++)
            {
                string layerName = _animator.GetLayerName(i);
                int fullPathHash = Animator.StringToHash($"{layerName}.{stateName}");
                if (_animator.HasState(i, fullPathHash))
                {
                    layer = i;
                    stateHash = fullPathHash;
                    _activeStateName = stateName;
                    return true;
                }

                int shortHash = Animator.StringToHash(stateName);
                if (_animator.HasState(i, shortHash))
                {
                    layer = i;
                    stateHash = shortHash;
                    _activeStateName = stateName;
                    return true;
                }
            }

            return false;
        }

        private float ResolveClipLength(string clipName, float fallback)
        {
            if (!EnsureAnimator() || _animator!.runtimeAnimatorController == null)
                return fallback;

            var clips = _animator.runtimeAnimatorController.animationClips;
            for (int i = 0; i < clips.Length; i++)
            {
                if (string.Equals(clips[i].name, clipName, System.StringComparison.Ordinal))
                    return Mathf.Clamp(clips[i].length, 0.5f, 6f);
            }

            return fallback;
        }

        private AnimationClip? ResolveControllerClip(string clipName)
        {
            if (!EnsureAnimator() || _animator!.runtimeAnimatorController == null)
                return null;

            AnimationClip[] clips = _animator.runtimeAnimatorController.animationClips;
            for (int i = 0; i < clips.Length; i++)
            {
                if (clips[i] != null && string.Equals(clips[i].name, clipName, StringComparison.Ordinal))
                    return clips[i];
            }

            return clips.Length > 0 ? clips[0] : null;
        }

        private bool HasHardCrowdControl => !string.IsNullOrEmpty(_hardCrowdControlStatusKind);

        private static string NormalizeStatusKind(string? value)
            => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToUpperInvariant();

        private bool EnsureAnimator()
        {
            if (_animator != null)
                return true;

            if (_visualProfile != null)
            {
                if (_visualProfile.TryResolvePrimaryAnimator(gameObject, out Animator profileAnimator))
                {
                    _animator = profileAnimator;
                    Animator[] animators = GetComponentsInChildren<Animator>(includeInactive: true);
                    for (int i = 0; i < animators.Length; i++)
                        animators[i].enabled = ReferenceEquals(animators[i], profileAnimator);
                    _animator.applyRootMotion = false;
                }
            }
            else
            {
                _animator = GetComponentInChildren<Animator>(includeInactive: true);
                if (_animator != null)
                    _animator.applyRootMotion = false;
            }
            return _animator != null;
        }

        private static string FirstOrDefault(List<string>? values, string fallback)
            => values != null && values.Count > 0 && !string.IsNullOrWhiteSpace(values[0])
                ? values[0].Trim()
                : fallback;

        private static string[] ProfileCandidates(List<string>? values, string[] fallback)
            => values != null && values.Count > 0 ? values.ToArray() : fallback;
    }
}
