#nullable enable
using System;
using UnityEngine;

namespace Arena.Presentation
{
    internal sealed class CombatStatusReactionController
    {
        private static readonly int TriggerHitFHash      = Animator.StringToHash("TriggerHitF");
        private static readonly int TriggerHitBHash      = Animator.StringToHash("TriggerHitB");
        private static readonly int TriggerHitLHash      = Animator.StringToHash("TriggerHitL");
        private static readonly int TriggerHitRHash      = Animator.StringToHash("TriggerHitR");
        private static readonly int TriggerStaggerFHash  = Animator.StringToHash("TriggerStaggerF");
        private static readonly int TriggerStaggerBHash  = Animator.StringToHash("TriggerStaggerB");
        private static readonly int TriggerStaggerLHash  = Animator.StringToHash("TriggerStaggerL");
        private static readonly int TriggerStaggerRHash  = Animator.StringToHash("TriggerStaggerR");
        private static readonly int IsKnockedDownHash    = Animator.StringToHash("IsKnockedDown");
        private static readonly int TriggerKnockdownHash = Animator.StringToHash("TriggerKnockdown");
        private static readonly int TriggerGetUpHash     = Animator.StringToHash("TriggerGetUp");
        private static readonly int HardCrowdControlBoolHash = Animator.StringToHash("IsHardCrowdControlled");
        private static readonly int TriggerHardCrowdControlHash = Animator.StringToHash("TriggerHardCrowdControl");
        private static readonly int HitReactionEmptyStateHash = Animator.StringToHash("Empty");

        private const int HitReactionLayerIndex = 2;
        private const string HardCrowdControlLoopSlotName = "slot_hard_crowd_control_loop";

        private readonly Func<bool> _isCurrentlyGrounded;
        private readonly Action<bool, bool> _applyHitClipOverrides;
        private readonly Action _clearInterruptiblePresentationForStagger;

        private Animator? _animator;
        private AnimatorOverrideController? _overrideController;
        private CombatAnimationSet? _animationSet;
        private AnimationClip? _defaultHardCrowdControlLoopClip;
        private string _hardCrowdControlStatusKind = string.Empty;

        public CombatStatusReactionController(
            Func<bool> isCurrentlyGrounded,
            Action<bool, bool> applyHitClipOverrides,
            Action clearInterruptiblePresentationForStagger)
        {
            _isCurrentlyGrounded = isCurrentlyGrounded;
            _applyHitClipOverrides = applyHitClipOverrides;
            _clearInterruptiblePresentationForStagger = clearInterruptiblePresentationForStagger;
        }

        public void Bind(Animator? animator, AnimatorOverrideController? overrideController)
        {
            _animator = animator;
            _overrideController = overrideController;
        }

        public void ApplyAnimationSet(CombatAnimationSet set)
        {
            _animationSet = set;
            _defaultHardCrowdControlLoopClip = set.stunLoop;
        }

        public void SetKnockedDown(bool isKnockedDown)
        {
            if (_animator == null || _overrideController == null) return;

            bool alreadyKnockedDown = _animator.GetBool(IsKnockedDownHash);
            _animator.SetBool(IsKnockedDownHash, isKnockedDown);

            if (isKnockedDown)
            {
                if (!alreadyKnockedDown)
                {
                    ClearHitReactionPresentation();
                    ClearHardCrowdControlPresentation();
                    _animator.ResetTrigger(TriggerGetUpHash);
                    _animator.SetTrigger(TriggerKnockdownHash);
                }
                return;
            }

            if (alreadyKnockedDown)
            {
                _animator.ResetTrigger(TriggerKnockdownHash);
                _animator.SetTrigger(TriggerGetUpHash);
            }
        }

        public void SetHardCrowdControl(string? statusKind)
        {
            if (_animator == null || _overrideController == null) return;

            bool isActive = !string.IsNullOrWhiteSpace(statusKind);
            string normalizedStatusKind = isActive
                ? statusKind!.Trim().ToUpperInvariant()
                : string.Empty;

            if (isActive && _animator.GetBool(IsKnockedDownHash))
            {
                _hardCrowdControlStatusKind = normalizedStatusKind;
                ApplyHardCrowdControlLoopOverride(normalizedStatusKind);
                ClearHardCrowdControlPresentation();
                return;
            }

            bool alreadyHardCrowdControlled = _animator.GetBool(HardCrowdControlBoolHash);
            bool statusKindChanged = !string.Equals(
                _hardCrowdControlStatusKind,
                normalizedStatusKind,
                StringComparison.Ordinal);

            if (isActive)
            {
                _hardCrowdControlStatusKind = normalizedStatusKind;
                ApplyHardCrowdControlLoopOverride(normalizedStatusKind);
                _animator.SetBool(HardCrowdControlBoolHash, true);

                if (!alreadyHardCrowdControlled || statusKindChanged)
                {
                    ClearHitReactionPresentation();
                    _animator.ResetTrigger(TriggerHardCrowdControlHash);
                    _animator.SetTrigger(TriggerHardCrowdControlHash);
                }
                return;
            }

            _hardCrowdControlStatusKind = string.Empty;
            RestoreDefaultHardCrowdControlLoopOverride();
            _animator.SetBool(HardCrowdControlBoolHash, false);
            if (alreadyHardCrowdControlled)
                _animator.ResetTrigger(TriggerHardCrowdControlHash);
        }

        public void TriggerHit(Vector3 hitDirection, Vector3 characterForward)
        {
            if (_animator == null || _overrideController == null) return;
            if (_animator.GetBool(HardCrowdControlBoolHash) || _animator.GetBool(IsKnockedDownHash))
                return;

            _applyHitClipOverrides(_isCurrentlyGrounded(), true);
            _animator.SetTrigger(ResolveDirectionalHitTrigger(
                hitDirection,
                characterForward,
                TriggerHitFHash,
                TriggerHitBHash,
                TriggerHitLHash,
                TriggerHitRHash));
        }

        public void TriggerStagger(Vector3 hitDirection, Vector3 characterForward)
        {
            if (_animator == null || _overrideController == null) return;
            _clearInterruptiblePresentationForStagger();
            _animator.SetTrigger(ResolveDirectionalHitTrigger(
                hitDirection,
                characterForward,
                TriggerStaggerFHash,
                TriggerStaggerBHash,
                TriggerStaggerLHash,
                TriggerStaggerRHash));
        }

        public void ClearForNonDeath()
        {
            ClearHitReactionPresentation();
            _hardCrowdControlStatusKind = string.Empty;
            RestoreDefaultHardCrowdControlLoopOverride();
            ClearHardCrowdControlPresentation();
            ClearKnockdownPresentation();
        }

        private void ApplyHardCrowdControlLoopOverride(string statusKind)
        {
            if (_overrideController == null)
                return;

            AnimationClip? desiredClip = ResolveHardCrowdControlLoopClip(statusKind);
            if (desiredClip == null)
                return;

            _overrideController[HardCrowdControlLoopSlotName] = desiredClip;
        }

        private AnimationClip? ResolveHardCrowdControlLoopClip(string statusKind)
        {
            if (_animationSet != null
                && _animationSet.TryGetStatusReaction(statusKind, out StatusReactionAnimationEntry reaction)
                && reaction.loop != null)
            {
                return reaction.loop;
            }

            if (string.Equals(statusKind, "STUN", StringComparison.Ordinal)
                || string.Equals(statusKind, "FREEZE", StringComparison.Ordinal))
                return _defaultHardCrowdControlLoopClip ?? _animationSet?.stunLoop;

            Debug.LogWarning(
                $"[CombatStatusReactionController] Status '{statusKind}' has no status reaction animation in animation set '{_animationSet?.name ?? "<none>"}'.");
            return _defaultHardCrowdControlLoopClip ?? _animationSet?.stunLoop;
        }

        private void RestoreDefaultHardCrowdControlLoopOverride()
        {
            if (_overrideController == null)
                return;

            AnimationClip? defaultClip = _defaultHardCrowdControlLoopClip ?? _animationSet?.stunLoop;
            if (defaultClip != null)
                _overrideController[HardCrowdControlLoopSlotName] = defaultClip;
        }

        private void ResetHitTriggers()
        {
            if (_animator == null)
                return;

            _animator.ResetTrigger(TriggerHitFHash);
            _animator.ResetTrigger(TriggerHitBHash);
            _animator.ResetTrigger(TriggerHitLHash);
            _animator.ResetTrigger(TriggerHitRHash);
        }

        private void ClearHitReactionPresentation()
        {
            if (_animator == null)
                return;

            ResetHitTriggers();
            _animator.Play(HitReactionEmptyStateHash, HitReactionLayerIndex, 0f);
        }

        private void ClearHardCrowdControlPresentation()
        {
            if (_animator == null)
                return;

            _animator.SetBool(HardCrowdControlBoolHash, false);
            _animator.ResetTrigger(TriggerHardCrowdControlHash);
        }

        private void ClearKnockdownPresentation()
        {
            if (_animator == null)
                return;

            _animator.SetBool(IsKnockedDownHash, false);
            _animator.ResetTrigger(TriggerKnockdownHash);
            _animator.ResetTrigger(TriggerGetUpHash);
        }

        private static int ResolveDirectionalHitTrigger(
            Vector3 hitDirection,
            Vector3 characterForward,
            int forwardTrigger,
            int backTrigger,
            int leftTrigger,
            int rightTrigger)
        {
            if (hitDirection.sqrMagnitude <= 0.0001f)
                hitDirection = characterForward;
            if (characterForward.sqrMagnitude <= 0.0001f)
                characterForward = Vector3.forward;

            float angle = Vector3.SignedAngle(characterForward.normalized, hitDirection.normalized, Vector3.up);
            return angle switch
            {
                < -135f or > 135f => backTrigger,
                < -45f            => leftTrigger,
                < 45f             => forwardTrigger,
                _                 => rightTrigger,
            };
        }
    }
}
