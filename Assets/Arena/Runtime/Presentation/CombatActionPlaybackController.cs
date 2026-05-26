#nullable enable
using UnityEngine;

namespace Arena.Presentation
{
    internal enum CombatVisualInterruptDecision
    {
        PreserveExistingBehavior = 0,
        InterruptCurrentWithGhost = 1,
        InterruptCurrentWithoutGhost = 2,
        SuppressIncomingWithGhost = 3,
    }

    internal enum CombatAnimationDecision
    {
        PlayNow = 0,
        InterruptCurrentAndPlay = 1,
        IgnoreAsDuplicate = 2,
        DropAsLowerPriority = 3,
        InterruptCurrentWithoutGhostAndPlay = 4,
        HandoffComboFollowUpAndPlay = 5,
    }

    internal enum PhasedMeleePlaybackPhase
    {
        None = 0,
        Start = 1,
        Loop = 2,
        End = 3,
    }

    internal enum SpellCastHoldPlaybackPhase
    {
        None = 0,
        Enter = 1,
        Idle = 2,
    }

    internal enum CombatPreemptionMode
    {
        None = 0,
        SuppressIncomingWithGhost = 1,
        InterruptWithGhost = 2,
        InterruptWithoutGhost = 3,
        HandoffComboFollowUp = 4,
    }

    internal readonly struct ActiveMeleePresentation
    {
        public readonly string ActionId;
        public readonly CombatAnimationCategory Category;
        public readonly int StrikeIndex;
        public readonly float VisualInterruptibleAtSeconds;
        public readonly float LowerBodyUnlockAtSeconds;
        public readonly float LowerBodyBlendOutSeconds;
        public readonly float PlayedLengthSeconds;
        public readonly float AppliedCatchupSeconds;
        public readonly bool IsPhased;

        public ActiveMeleePresentation(
            string actionId,
            CombatAnimationCategory category,
            int strikeIndex,
            float visualInterruptibleAtSeconds,
            float lowerBodyUnlockAtSeconds,
            float lowerBodyBlendOutSeconds,
            float playedLengthSeconds,
            float appliedCatchupSeconds,
            bool isPhased)
        {
            ActionId = actionId;
            Category = category;
            StrikeIndex = strikeIndex;
            VisualInterruptibleAtSeconds = visualInterruptibleAtSeconds;
            LowerBodyUnlockAtSeconds = lowerBodyUnlockAtSeconds;
            LowerBodyBlendOutSeconds = lowerBodyBlendOutSeconds;
            PlayedLengthSeconds = playedLengthSeconds;
            AppliedCatchupSeconds = appliedCatchupSeconds;
            IsPhased = isPhased;
        }
    }

    internal readonly struct ActiveSpellPresentation
    {
        public readonly string ActionId;
        public readonly int BankSlot;
        public readonly float LowerBodyUnlockAtSeconds;
        public readonly float LowerBodyBlendOutSeconds;
        public readonly float VisualInterruptibleAtSeconds;

        public ActiveSpellPresentation(
            string actionId,
            int bankSlot,
            float lowerBodyUnlockAtSeconds,
            float lowerBodyBlendOutSeconds,
            float visualInterruptibleAtSeconds)
        {
            ActionId = actionId;
            BankSlot = bankSlot;
            LowerBodyUnlockAtSeconds = lowerBodyUnlockAtSeconds;
            LowerBodyBlendOutSeconds = lowerBodyBlendOutSeconds;
            VisualInterruptibleAtSeconds = visualInterruptibleAtSeconds;
        }
    }

    internal readonly struct ActiveSpellCastHoldPresentation
    {
        public readonly string ActionId;
        public readonly int EnterBankSlot;
        public readonly int IdleBankSlot;
        public readonly SpellPlaybackLayer PlaybackLayer;

        public ActiveSpellCastHoldPresentation(
            string actionId,
            int enterBankSlot,
            int idleBankSlot,
            SpellPlaybackLayer playbackLayer)
        {
            ActionId = actionId;
            EnterBankSlot = enterBankSlot;
            IdleBankSlot = idleBankSlot;
            PlaybackLayer = playbackLayer;
        }
    }

    internal sealed class CombatActionPlaybackController
    {
        public const float DefaultLowerBodyBlendOutSeconds = 0.12f;

        private readonly AnimationClip?[] _strikeBankClips = new AnimationClip?[CombatAnimationSet.AnimatorStrikeBankCount];
        private readonly AnimationClip?[] _spellBankClips = new AnimationClip?[CombatAnimationSet.AnimatorSpellBankCount];
        private LowerBodyUnlockPlaybackState _meleeLowerBodyUnlock;
        private LowerBodyUnlockPlaybackState _spellLowerBodyUnlock;
        private LowerBodyUnlockPlaybackState _spellCastHoldFadeOut;
        private bool _meleeUpperBodyRecoveryActive;
        private bool _phasedMeleeActive;
        private bool _phasedMeleeReleaseAfterStart;
        private bool _phasedMeleeUpperBodyMode;
        private int _phasedMeleeBankSlot;
        private int _phasedMeleeStateHash;
        private AnimationClip? _phasedMeleeStartClip;
        private AnimationClip? _phasedMeleeLoopClip;
        private AnimationClip? _phasedMeleeEndClip;
        private PhasedMeleePlaybackPhase _phasedMeleePhase = PhasedMeleePlaybackPhase.None;
        private float _phasedMeleeElapsedBeforePhase;
        private float _phasedMeleeCurrentPhaseLengthSeconds;
        private float _phasedMeleeTotalLengthSeconds;
        private bool _phasedMeleeSpecialMovementDriven;
        private bool _phasedMeleeSpecialMovementEndRequested;
        private SpellCastHoldPlaybackPhase _spellCastHoldPhase = SpellCastHoldPlaybackPhase.None;
        private int _nextSpellBankSlot = 1;

        public CombatAnimationCategory? ActiveBaseCombatAnimationCategory { get; set; }
        public bool IsMeleeLowerBodyUnlocked => _meleeLowerBodyUnlock.Unlocked;
        public bool IsMeleeUpperBodyRecoveryActive => _meleeUpperBodyRecoveryActive;
        public bool IsSpellLowerBodyUnlocked => _spellLowerBodyUnlock.Unlocked;
        public float MeleeLowerBodyBlendOutSeconds => _meleeLowerBodyUnlock.BlendOutSeconds;
        public float SpellLowerBodyBlendOutSeconds => _spellLowerBodyUnlock.BlendOutSeconds;
        public bool IsPhasedMeleeActive => _phasedMeleeActive;
        public bool PhasedMeleeReleaseAfterStart => _phasedMeleeReleaseAfterStart;
        public bool IsPhasedMeleeUpperBodyMode => _phasedMeleeUpperBodyMode;
        public int PhasedMeleeBankSlot => _phasedMeleeBankSlot;
        public int PhasedMeleeStateHash => _phasedMeleeStateHash;
        public PhasedMeleePlaybackPhase PhasedMeleePhase => _phasedMeleePhase;
        public float PhasedMeleeTotalLengthSeconds => _phasedMeleeTotalLengthSeconds;
        public bool IsPhasedMeleeSpecialMovementDriven => _phasedMeleeSpecialMovementDriven;
        public bool IsPhasedMeleeSpecialMovementEndRequested => _phasedMeleeSpecialMovementEndRequested;
        public ActiveMeleePresentation? ActiveMeleePresentation { get; private set; }
        public bool ActiveMeleePresentationEntered { get; private set; }
        public ActiveSpellPresentation? ActiveSpellPresentation { get; private set; }
        public bool ActiveSpellPresentationEntered { get; private set; }
        public ActiveSpellCastHoldPresentation? ActiveSpellCastHoldPresentation { get; private set; }
        public SpellCastHoldPlaybackPhase SpellCastHoldPhase => _spellCastHoldPhase;

        public void ResetBanks(CombatAnimationSet set)
        {
            for (int i = 0; i < CombatAnimationSet.AnimatorStrikeBankCount; i++)
                _strikeBankClips[i] = set.GetStrikeClip(i + 1);
            ClearSpellBank();
        }

        public void ClearSpellBank()
        {
            for (int i = 0; i < CombatAnimationSet.AnimatorSpellBankCount; i++)
                _spellBankClips[i] = null;
            _nextSpellBankSlot = 1;
            ClearActiveSpellCastHoldPresentation();
        }

        public static int ResolveStrikeBankSlot(int strikeIndex)
        {
            if (strikeIndex <= 0)
                return 1;

            return ((strikeIndex - 1) % CombatAnimationSet.AnimatorStrikeBankCount) + 1;
        }

        public int ResolveNextSpellBankSlot()
        {
            if (_nextSpellBankSlot < 1 || _nextSpellBankSlot > CombatAnimationSet.AnimatorSpellBankCount)
                _nextSpellBankSlot = 1;

            int bankSlot = _nextSpellBankSlot;
            _nextSpellBankSlot = (_nextSpellBankSlot % CombatAnimationSet.AnimatorSpellBankCount) + 1;
            return bankSlot;
        }

        public static int ResolveBankedAnimatorHash(
            int bankSlot,
            int slot1Hash,
            int slot2Hash,
            int slot3Hash,
            int slot4Hash)
        {
            return bankSlot switch
            {
                1 => slot1Hash,
                2 => slot2Hash,
                3 => slot3Hash,
                4 => slot4Hash,
                _ => slot1Hash,
            };
        }

        public static int ResolvePhasedMeleeBankSlot(int startBankSlot, PhasedMeleePlaybackPhase phase)
        {
            int zeroBasedStart = Mathf.Clamp(startBankSlot - 1, 0, CombatAnimationSet.AnimatorStrikeBankCount - 1);
            int phaseOffset = phase switch
            {
                PhasedMeleePlaybackPhase.Start => 0,
                PhasedMeleePlaybackPhase.Loop => 1,
                PhasedMeleePlaybackPhase.End => 2,
                _ => 0,
            };

            return ((zeroBasedStart + phaseOffset) % CombatAnimationSet.AnimatorStrikeBankCount) + 1;
        }

        public static bool TryResolvePhasedMeleeLayerRoute(
            int startBankSlot,
            PhasedMeleePlaybackPhase phase,
            int strike1StateHash,
            int strike2StateHash,
            int strike3StateHash,
            int strike4StateHash,
            out int segmentBankSlot,
            out int segmentStateHash)
        {
            segmentBankSlot = ResolvePhasedMeleeBankSlot(startBankSlot, phase);
            segmentStateHash = ResolveBankedAnimatorHash(
                segmentBankSlot,
                strike1StateHash,
                strike2StateHash,
                strike3StateHash,
                strike4StateHash);
            return segmentStateHash != 0;
        }

        public bool TryBindStrikeClip(
            AnimatorOverrideController? overrideController,
            CombatAnimationSet? animationSet,
            int strikeIndex,
            int bankSlot)
        {
            if (overrideController == null)
                return false;

            AnimationClip? desiredClip = animationSet?.GetStrikeClip(strikeIndex);
            if (desiredClip == null)
                return strikeIndex <= CombatAnimationSet.AnimatorStrikeBankCount;

            int bankIndex = Mathf.Clamp(bankSlot - 1, 0, CombatAnimationSet.AnimatorStrikeBankCount - 1);
            if (_strikeBankClips[bankIndex] == desiredClip)
                return true;

            overrideController[ResolveStrikeSlotName(bankSlot)] = desiredClip;
            _strikeBankClips[bankIndex] = desiredClip;
            return true;
        }

        public void OverrideStrikeBankSlot(AnimatorOverrideController overrideController, int bankSlot, AnimationClip clip)
        {
            overrideController[ResolveStrikeSlotName(bankSlot)] = clip;
            int bankIndex = Mathf.Clamp(bankSlot - 1, 0, CombatAnimationSet.AnimatorStrikeBankCount - 1);
            _strikeBankClips[bankIndex] = clip;
        }

        public static void PlayFullBodySpellAction(
            Animator animator,
            int spellActionLayerIndex,
            int triggerHash)
            => PlayFullBodySpellAction(animator, spellActionLayerIndex, triggerHash, 0, 0f);

        public static void PlayFullBodySpellAction(
            Animator animator,
            int spellActionLayerIndex,
            int triggerHash,
            int stateHash,
            float normalizedStart)
        {
            animator.ResetTrigger(triggerHash);
            animator.SetLayerWeight(spellActionLayerIndex, 1f);
            if (stateHash != 0 && normalizedStart > 0.001f)
            {
                animator.Play(stateHash, spellActionLayerIndex, Mathf.Clamp01(normalizedStart));
                return;
            }

            animator.SetTrigger(triggerHash);
        }

        public static void TriggerMeleeStrike(Animator animator, int triggerHash)
        {
            animator.SetTrigger(triggerHash);
        }

        public static void PlayMeleeStrikeState(
            Animator animator,
            int meleeLayerIndex,
            int stateHash,
            float normalizedTime)
        {
            animator.SetLayerWeight(meleeLayerIndex, 1f);
            animator.Play(stateHash, meleeLayerIndex, Mathf.Clamp01(normalizedTime));
        }

        public bool ResetMeleeLowerBodyUnlock(bool clearUpperBodyRecovery)
        {
            bool shouldClearUpperBodyRecovery = clearUpperBodyRecovery && _meleeUpperBodyRecoveryActive;
            _meleeLowerBodyUnlock.Reset();
            _meleeUpperBodyRecoveryActive = false;
            return shouldClearUpperBodyRecovery;
        }

        public bool ResetSpellLowerBodyUnlock(bool clearUpperBodySpell)
        {
            bool shouldClearUpperBodySpell = clearUpperBodySpell && _spellLowerBodyUnlock.Unlocked;
            _spellLowerBodyUnlock.Reset();
            return shouldClearUpperBodySpell;
        }

        public void MarkMeleeLowerBodyUnlocked(
            float nowSeconds,
            float blendOutSeconds,
            float layerWeightAtUnlock)
        {
            _meleeLowerBodyUnlock.MarkUnlocked(nowSeconds, blendOutSeconds, layerWeightAtUnlock);
            _meleeUpperBodyRecoveryActive = true;
        }

        public void MarkSpellLowerBodyUnlocked(
            float nowSeconds,
            float blendOutSeconds,
            float layerWeightAtUnlock)
        {
            _spellLowerBodyUnlock.MarkUnlocked(nowSeconds, blendOutSeconds, layerWeightAtUnlock);
        }

        public float ResolveMeleeLowerBodyLayerWeight(float nowSeconds) =>
            _meleeLowerBodyUnlock.ResolveLayerWeight(nowSeconds);

        public float ResolveSpellLowerBodyLayerWeight(float nowSeconds) =>
            _spellLowerBodyUnlock.ResolveLayerWeight(nowSeconds);

        public bool IsSpellCastHoldFadeOutActive => _spellCastHoldFadeOut.Unlocked;

        public void StartSpellCastHoldFadeOut(float nowSeconds, float blendOutSeconds, float delaySeconds = 0f)
        {
            // Push the start point into the future by `delaySeconds`. ResolveLayerWeight
            // returns LayerWeightAtUnlock (1f) while elapsed < 0 because Mathf.Clamp01
            // clamps the negative blendT to 0, so the layer holds at full weight during
            // the delay and then blends to 0 over BlendOutSeconds.
            _spellCastHoldFadeOut.MarkUnlocked(nowSeconds + Mathf.Max(0f, delaySeconds), blendOutSeconds, 1f);
        }

        public float ResolveSpellCastHoldFadeOutLayerWeight(float nowSeconds) =>
            _spellCastHoldFadeOut.ResolveLayerWeight(nowSeconds);

        public void ResetSpellCastHoldFadeOut() => _spellCastHoldFadeOut.Reset();

        public void BeginPhasedMelee(
            int bankSlot,
            AnimationClip startClip,
            AnimationClip loopClip,
            AnimationClip endClip,
            bool releaseAfterStart,
            bool specialMovementDriven = false)
        {
            _phasedMeleeBankSlot = bankSlot;
            _phasedMeleeStateHash = 0;
            _phasedMeleeStartClip = startClip;
            _phasedMeleeLoopClip = loopClip;
            _phasedMeleeEndClip = endClip;
            _phasedMeleeElapsedBeforePhase = 0f;
            _phasedMeleeCurrentPhaseLengthSeconds = 0f;
            _phasedMeleeTotalLengthSeconds = Mathf.Max(0f, startClip.length)
                + (releaseAfterStart ? 0f : Mathf.Max(0f, loopClip.length))
                + Mathf.Max(0f, endClip.length);
            _phasedMeleeSpecialMovementDriven = specialMovementDriven;
            _phasedMeleeSpecialMovementEndRequested = false;
            _phasedMeleeUpperBodyMode = false;
            _phasedMeleeActive = true;
            _phasedMeleeReleaseAfterStart = releaseAfterStart;
            _phasedMeleePhase = PhasedMeleePlaybackPhase.None;
        }

        public bool RequestPhasedMeleeSpecialMovementEnd()
        {
            if (!_phasedMeleeActive || !_phasedMeleeSpecialMovementDriven)
                return false;

            _phasedMeleeSpecialMovementEndRequested = true;
            return true;
        }

        public bool CancelPhasedMelee()
        {
            bool wasActive = _phasedMeleeActive;
            _phasedMeleeActive = false;
            _phasedMeleeReleaseAfterStart = false;
            _phasedMeleeUpperBodyMode = false;
            _phasedMeleeBankSlot = 0;
            _phasedMeleeStateHash = 0;
            _phasedMeleeStartClip = null;
            _phasedMeleeLoopClip = null;
            _phasedMeleeEndClip = null;
            _phasedMeleePhase = PhasedMeleePlaybackPhase.None;
            _phasedMeleeElapsedBeforePhase = 0f;
            _phasedMeleeCurrentPhaseLengthSeconds = 0f;
            _phasedMeleeTotalLengthSeconds = 0f;
            _phasedMeleeSpecialMovementDriven = false;
            _phasedMeleeSpecialMovementEndRequested = false;
            return wasActive;
        }

        public AnimationClip? GetCurrentPhasedMeleeClip() => GetPhasedMeleeClip(_phasedMeleePhase);

        public AnimationClip? GetPhasedMeleeClip(PhasedMeleePlaybackPhase phase)
        {
            return phase switch
            {
                PhasedMeleePlaybackPhase.Start => _phasedMeleeStartClip,
                PhasedMeleePlaybackPhase.Loop => _phasedMeleeLoopClip,
                PhasedMeleePlaybackPhase.End => _phasedMeleeEndClip,
                _ => null,
            };
        }

        public void SetPhasedMeleeSegment(
            PhasedMeleePlaybackPhase phase,
            int stateHash,
            float phaseLengthSeconds)
        {
            _phasedMeleePhase = phase;
            _phasedMeleeStateHash = stateHash;
            _phasedMeleeCurrentPhaseLengthSeconds = Mathf.Max(0f, phaseLengthSeconds);
        }

        public void EnterPhasedMeleeUpperBodyMode()
        {
            _phasedMeleeUpperBodyMode = true;
        }

        public void AddCompletedPhasedMeleePhaseSeconds(float normalizedTime)
        {
            _phasedMeleeElapsedBeforePhase +=
                Mathf.Max(0f, _phasedMeleeCurrentPhaseLengthSeconds) * Mathf.Clamp01(normalizedTime);
        }

        public bool TryGetPhasedMeleePresentationTiming(
            float normalizedTime,
            out float elapsedSeconds,
            out float stateLengthSeconds)
        {
            elapsedSeconds = 0f;
            stateLengthSeconds = Mathf.Max(0f, _phasedMeleeTotalLengthSeconds);
            if (!_phasedMeleeActive)
                return false;

            elapsedSeconds = _phasedMeleeElapsedBeforePhase
                + Mathf.Max(0f, _phasedMeleeCurrentPhaseLengthSeconds) * Mathf.Max(0f, normalizedTime);
            return true;
        }

        public bool TryResolvePhasedMeleeTransition(
            float normalizedTime,
            float startOnlyEndTriggerNormalizedTime,
            float segmentTransitionNormalizedTime,
            float endCompleteNormalizedTime,
            out PhasedMeleePlaybackPhase nextPhase,
            out bool shouldCancel)
        {
            nextPhase = PhasedMeleePlaybackPhase.None;
            shouldCancel = false;
            if (!_phasedMeleeActive)
                return false;

            if (_phasedMeleePhase == PhasedMeleePlaybackPhase.Start)
            {
                if (_phasedMeleeReleaseAfterStart)
                {
                    if (normalizedTime < startOnlyEndTriggerNormalizedTime)
                        return false;

                    nextPhase = PhasedMeleePlaybackPhase.End;
                    return true;
                }

                if (normalizedTime < segmentTransitionNormalizedTime)
                    return false;

                nextPhase = PhasedMeleePlaybackPhase.Loop;
                return true;
            }

            if (_phasedMeleePhase == PhasedMeleePlaybackPhase.Loop)
            {
                if (normalizedTime < segmentTransitionNormalizedTime)
                    return false;

                nextPhase = PhasedMeleePlaybackPhase.End;
                return true;
            }

            if (_phasedMeleePhase == PhasedMeleePlaybackPhase.End
                && normalizedTime >= endCompleteNormalizedTime)
            {
                shouldCancel = true;
                return true;
            }

            return false;
        }

        public bool ClearActiveBaseCombatAnimationCategoryIf(CombatAnimationCategory category)
        {
            if (ActiveBaseCombatAnimationCategory != category)
                return false;

            ActiveBaseCombatAnimationCategory = null;
            return true;
        }

        public bool ClearActiveMeleeBaseCategory()
        {
            if (ActiveBaseCombatAnimationCategory != CombatAnimationCategory.MeleeSkill
                && ActiveBaseCombatAnimationCategory != CombatAnimationCategory.AutoAttack)
            {
                return false;
            }

            ActiveBaseCombatAnimationCategory = null;
            return true;
        }

        public static CombatAnimationDecision DecideCombatAnimationRequest(
            CombatAnimationCategory incomingCategory,
            bool isHigherPriorityActive,
            bool isSpellActive,
            bool isMeleeActive,
            bool isComboFollowUp,
            bool activeMeleeIsPhased,
            bool visualGateEvaluated,
            CombatVisualInterruptDecision visualDecision)
        {
            if (incomingCategory == CombatAnimationCategory.AutoAttack && isHigherPriorityActive)
            {
                return visualGateEvaluated && visualDecision == CombatVisualInterruptDecision.InterruptCurrentWithoutGhost
                    ? CombatAnimationDecision.InterruptCurrentWithoutGhostAndPlay
                    : CombatAnimationDecision.DropAsLowerPriority;
            }

            if (isSpellActive)
                return CombatAnimationDecision.InterruptCurrentWithoutGhostAndPlay;

            if (incomingCategory != CombatAnimationCategory.AutoAttack && isMeleeActive)
            {
                if (isComboFollowUp)
                {
                    return activeMeleeIsPhased
                        ? CombatAnimationDecision.InterruptCurrentWithoutGhostAndPlay
                        : CombatAnimationDecision.HandoffComboFollowUpAndPlay;
                }

                return visualGateEvaluated && visualDecision == CombatVisualInterruptDecision.InterruptCurrentWithoutGhost
                    ? CombatAnimationDecision.InterruptCurrentWithoutGhostAndPlay
                    : CombatAnimationDecision.InterruptCurrentAndPlay;
            }

            return CombatAnimationDecision.PlayNow;
        }

        public static CombatPreemptionMode ResolvePreemptionMode(
            CombatAnimationDecision decision,
            CombatAnimationCategory incomingCategory)
        {
            return decision switch
            {
                CombatAnimationDecision.DropAsLowerPriority => CombatPreemptionMode.SuppressIncomingWithGhost,
                CombatAnimationDecision.InterruptCurrentAndPlay
                    when incomingCategory != CombatAnimationCategory.AutoAttack => CombatPreemptionMode.InterruptWithGhost,
                CombatAnimationDecision.InterruptCurrentWithoutGhostAndPlay => CombatPreemptionMode.InterruptWithoutGhost,
                CombatAnimationDecision.HandoffComboFollowUpAndPlay => CombatPreemptionMode.HandoffComboFollowUp,
                _ => CombatPreemptionMode.None,
            };
        }

        public static bool CanCaptureSuppressedAutoAttackGhost(
            CombatAnimationCategory incomingCategory,
            bool hasFacingTargetPoint,
            out string skipReason)
        {
            skipReason = string.Empty;
            if (incomingCategory != CombatAnimationCategory.AutoAttack)
            {
                skipReason = "wrong-category";
                return false;
            }

            if (!hasFacingTargetPoint)
            {
                skipReason = "no-facing-target";
                return false;
            }

            return true;
        }

        public static CombatVisualInterruptDecision DecideVisualInterrupt(
            CombatAnimationCategory activeCategory,
            CombatAnimationCategory incomingCategory,
            bool activeIsPhased,
            float activeElapsedSeconds,
            float activeVisualInterruptibleAtSeconds)
        {
            _ = activeCategory;
            _ = activeIsPhased;

            if (activeElapsedSeconds >= activeVisualInterruptibleAtSeconds)
                return CombatVisualInterruptDecision.InterruptCurrentWithoutGhost;

            return incomingCategory == CombatAnimationCategory.AutoAttack
                ? CombatVisualInterruptDecision.SuppressIncomingWithGhost
                : CombatVisualInterruptDecision.InterruptCurrentWithGhost;
        }

        public static float ResolvePlaybackThresholdSeconds(
            float authoredThresholdSeconds,
            float fallbackStateLengthSeconds)
        {
            return authoredThresholdSeconds <= 0f && fallbackStateLengthSeconds > 0f
                ? fallbackStateLengthSeconds
                : authoredThresholdSeconds;
        }

        public static float ResolvePlayedMeleeLengthSeconds(
            CombatAnimationSet? animationSet,
            int strikeIndex,
            bool isPhased,
            float phasedMeleeTotalLengthSeconds)
        {
            if (isPhased)
                return Mathf.Max(0f, phasedMeleeTotalLengthSeconds);

            AnimationClip? clip = animationSet?.GetStrikeClip(strikeIndex);
            return clip != null ? Mathf.Max(0f, clip.length) : 0f;
        }

        public static float ScaleAuthoredMeleeSeconds(
            float authoredSeconds,
            float timingReferenceLengthSeconds,
            float playedLengthSeconds)
        {
            if (authoredSeconds <= 0f
                || timingReferenceLengthSeconds <= 0.001f
                || playedLengthSeconds <= 0.001f)
            {
                return authoredSeconds;
            }

            return authoredSeconds * (playedLengthSeconds / timingReferenceLengthSeconds);
        }

        public static ActiveSpellPresentation CreateSpellPresentation(
            string actionId,
            int bankSlot,
            WeaponSpellAnimationEntry spellEntry,
            bool grounded)
        {
            return new ActiveSpellPresentation(
                actionId,
                bankSlot,
                spellEntry.ResolveLowerBodyUnlockAtSeconds(grounded),
                spellEntry.ResolveLowerBodyBlendOutSeconds(DefaultLowerBodyBlendOutSeconds),
                spellEntry.ResolveVisualInterruptibleAtSeconds(grounded));
        }

        public ActiveMeleePresentation CreateMeleePresentation(
            CombatAnimationRequest request,
            int strikeIndex,
            bool isPhased,
            CombatAnimationSet? animationSet,
            bool grounded,
            float appliedCatchupSeconds = 0f)
        {
            CombatAnimationCategory category = CombatAnimationRequest.ResolveMeleeCategory(request.Source);
            float playedLengthSeconds = ResolvePlayedMeleeLengthSeconds(
                animationSet,
                strikeIndex,
                isPhased,
                PhasedMeleeTotalLengthSeconds);
            float visualInterruptibleAtSeconds = strikeIndex > 0
                ? animationSet?.GetVisualInterruptibleAtSeconds(strikeIndex, grounded) ?? 0f
                : 0f;
            float lowerBodyUnlockAtSeconds = strikeIndex > 0
                ? animationSet?.GetLowerBodyUnlockAtSeconds(strikeIndex, grounded) ?? 0f
                : 0f;
            float lowerBodyBlendOutSeconds = strikeIndex > 0
                ? animationSet?.GetLowerBodyBlendOutSeconds(strikeIndex, DefaultLowerBodyBlendOutSeconds) ?? DefaultLowerBodyBlendOutSeconds
                : DefaultLowerBodyBlendOutSeconds;

            ActiveBaseCombatAnimationCategory = category;
            return new ActiveMeleePresentation(
                request.ActionId,
                category,
                strikeIndex,
                visualInterruptibleAtSeconds,
                lowerBodyUnlockAtSeconds,
                lowerBodyBlendOutSeconds,
                playedLengthSeconds,
                appliedCatchupSeconds,
                isPhased);
        }

        public void SetActiveSpellPresentation(
            string actionId,
            int bankSlot,
            WeaponSpellAnimationEntry spellEntry,
            bool grounded)
        {
            ActiveSpellPresentation = CreateSpellPresentation(
                actionId,
                bankSlot,
                spellEntry,
                grounded);
            ActiveSpellPresentationEntered = false;
        }

        public void SetActiveSpellCastHoldPresentation(
            string actionId,
            int enterBankSlot,
            int idleBankSlot,
            SpellPlaybackLayer playbackLayer)
        {
            ActiveSpellCastHoldPresentation = new ActiveSpellCastHoldPresentation(
                actionId,
                enterBankSlot,
                idleBankSlot,
                playbackLayer);
            _spellCastHoldPhase = SpellCastHoldPlaybackPhase.Enter;
        }

        public void SetSpellCastHoldPhase(SpellCastHoldPlaybackPhase phase)
        {
            if (ActiveSpellCastHoldPresentation.HasValue)
                _spellCastHoldPhase = phase;
        }

        public bool ClearActiveSpellCastHoldPresentation()
        {
            bool wasActive = ActiveSpellCastHoldPresentation.HasValue;
            ActiveSpellCastHoldPresentation = null;
            _spellCastHoldPhase = SpellCastHoldPlaybackPhase.None;
            return wasActive;
        }

        public void ClearActiveSpellPresentation()
        {
            ActiveSpellPresentation = null;
            ActiveSpellPresentationEntered = false;
        }

        public void MarkActiveSpellPresentationEntered()
        {
            if (ActiveSpellPresentation.HasValue)
                ActiveSpellPresentationEntered = true;
        }

        public void SetActiveMeleePresentation(
            CombatAnimationRequest request,
            int strikeIndex,
            bool isPhased,
            CombatAnimationSet? animationSet,
            bool grounded,
            float appliedCatchupSeconds = 0f)
        {
            ActiveMeleePresentation = CreateMeleePresentation(
                request,
                strikeIndex,
                isPhased,
                animationSet,
                grounded,
                appliedCatchupSeconds);
            ActiveMeleePresentationEntered = false;
        }

        public bool ClearActiveMeleePresentation()
        {
            bool wasPhased = ActiveMeleePresentation.HasValue && ActiveMeleePresentation.Value.IsPhased;
            ClearActiveMeleeBaseCategory();
            ActiveMeleePresentation = null;
            ActiveMeleePresentationEntered = false;
            return wasPhased;
        }

        public void MarkActiveMeleePresentationEntered()
        {
            if (ActiveMeleePresentation.HasValue)
                ActiveMeleePresentationEntered = true;
        }

        public static string DescribeVisualInterruptDecision(CombatVisualInterruptDecision decision)
        {
            return decision switch
            {
                CombatVisualInterruptDecision.InterruptCurrentWithoutGhost => "dispose",
                CombatVisualInterruptDecision.InterruptCurrentWithGhost => "ghost",
                CombatVisualInterruptDecision.SuppressIncomingWithGhost => "suppress-ghost",
                _ => "preserve",
            };
        }

        public bool TryBindSpellClip(
            AnimatorOverrideController? overrideController,
            CombatAnimationSet? animationSet,
            string spellKind,
            bool grounded,
            int bankSlot,
            out WeaponSpellAnimationEntry spellEntry)
        {
            spellEntry = default;
            if (overrideController == null || animationSet == null)
            {
                Debug.LogWarning(
                    $"[CombatActionPlaybackController] Spell '{spellKind}' could not bind because animator override controller or animation set is missing.");
                return false;
            }

            if (!animationSet.TryGetSpellAnimation(spellKind, out spellEntry))
            {
                Debug.LogWarning(
                    $"[CombatActionPlaybackController] Spell '{spellKind}' has no spell animation entry in animation set '{animationSet.name}'.");
                return false;
            }

            AnimationClip? desiredClip = spellEntry.ResolveClip(grounded);
            if (desiredClip == null)
            {
                Debug.LogWarning(
                    $"[CombatActionPlaybackController] Spell '{spellKind}' resolved no {(grounded ? "ground" : "air")} clip in animation set '{animationSet.name}'.");
                return false;
            }

            int bankIndex = Mathf.Clamp(bankSlot - 1, 0, CombatAnimationSet.AnimatorSpellBankCount - 1);
            if (_spellBankClips[bankIndex] == desiredClip)
                return true;

            overrideController[$"slot_spell_{bankSlot}"] = desiredClip;
            _spellBankClips[bankIndex] = desiredClip;
            return true;
        }

        public bool TryBindSpellBankClip(
            AnimatorOverrideController? overrideController,
            int bankSlot,
            AnimationClip? desiredClip)
        {
            if (overrideController == null || desiredClip == null)
                return false;

            int bankIndex = Mathf.Clamp(bankSlot - 1, 0, CombatAnimationSet.AnimatorSpellBankCount - 1);
            if (_spellBankClips[bankIndex] == desiredClip)
                return true;

            overrideController[$"slot_spell_{bankSlot}"] = desiredClip;
            _spellBankClips[bankIndex] = desiredClip;
            return true;
        }

        private static string ResolveStrikeSlotName(int bankSlot) => $"slot_strike_{bankSlot}";

        private struct LowerBodyUnlockPlaybackState
        {
            public bool Unlocked;
            public float StartedAtSeconds;
            public float BlendOutSeconds;
            public float LayerWeightAtUnlock;

            public void MarkUnlocked(
                float nowSeconds,
                float blendOutSeconds,
                float layerWeightAtUnlock)
            {
                Unlocked = true;
                StartedAtSeconds = nowSeconds;
                BlendOutSeconds = Mathf.Max(0f, blendOutSeconds);
                LayerWeightAtUnlock = layerWeightAtUnlock;
            }

            public float ResolveLayerWeight(float nowSeconds)
            {
                if (!Unlocked)
                    return 1f;
                if (BlendOutSeconds <= 0f)
                    return 0f;

                float blendT = Mathf.Clamp01((nowSeconds - StartedAtSeconds) / BlendOutSeconds);
                return Mathf.Lerp(LayerWeightAtUnlock, 0f, blendT);
            }

            public void Reset()
            {
                Unlocked = false;
                StartedAtSeconds = 0f;
                BlendOutSeconds = 0f;
                LayerWeightAtUnlock = 1f;
            }
        }
    }
}
