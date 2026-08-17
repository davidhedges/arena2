#nullable enable
using System.Collections.Generic;
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
        public readonly float EnterCompleteNormalizedTime;
        public readonly float ExitBlendOutSeconds;
        public readonly float ExitDelaySeconds;

        public ActiveSpellCastHoldPresentation(
            string actionId,
            int enterBankSlot,
            int idleBankSlot,
            SpellPlaybackLayer playbackLayer,
            float enterCompleteNormalizedTime,
            float exitBlendOutSeconds,
            float exitDelaySeconds)
        {
            ActionId = actionId;
            EnterBankSlot = enterBankSlot;
            IdleBankSlot = idleBankSlot;
            PlaybackLayer = playbackLayer;
            EnterCompleteNormalizedTime = Mathf.Clamp01(enterCompleteNormalizedTime);
            ExitBlendOutSeconds = Mathf.Max(0f, exitBlendOutSeconds);
            ExitDelaySeconds = Mathf.Max(0f, exitDelaySeconds);
        }
    }

    /// <summary>
    /// Identity of an instant spell playing on the upper-body/left-gesture
    /// overlay layers (the moving-cast path), which records no
    /// <see cref="ActiveSpellPresentation"/>. Kept so a server rejection can
    /// cut the specific overlay state (netcode design review S2); consumers
    /// must verify the recorded state hash is still what the layer is playing
    /// — the record is never lifecycle-cleared, only overwritten.
    /// </summary>
    internal readonly struct ActiveOverlaySpellPresentation
    {
        public readonly string ActionId;
        public readonly int StateHash;
        public readonly bool UsesLeftGesture;

        public ActiveOverlaySpellPresentation(string actionId, int stateHash, bool usesLeftGesture)
        {
            ActionId = actionId;
            StateHash = stateHash;
            UsesLeftGesture = usesLeftGesture;
        }
    }

    internal sealed class CombatActionPlaybackController
    {
        public const float DefaultLowerBodyBlendOutSeconds = 0.12f;

        private readonly AnimationClip?[] _strikeBankClips = new AnimationClip?[CombatAnimationSet.AnimatorStrikeBankCount];
        private readonly AnimationClip?[] _spellBankClips = new AnimationClip?[CombatAnimationSet.AnimatorSpellBankCount];
        private readonly HashSet<string> _spellAnimationResolutionWarnings = new(System.StringComparer.Ordinal);
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
        private float _phasedMeleeAuthoredStartExitNormalizedTime = -1f;
        private float _phasedMeleeAuthoredLoopExitNormalizedTime = -1f;
        private float _phasedMeleeElapsedBeforePhase;
        private float _phasedMeleeCurrentPhaseLengthSeconds;
        private float _phasedMeleeTotalLengthSeconds;
        private bool _phasedMeleeSegmentEntered;
        private bool _phasedMeleeSpecialMovementDriven;
        private bool _phasedMeleeSpecialMovementArrivalDriven;
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
        public bool HasPhasedMeleeSegmentEntered => _phasedMeleeSegmentEntered;
        public bool IsPhasedMeleeSpecialMovementDriven => _phasedMeleeSpecialMovementDriven;
        public bool IsPhasedMeleeSpecialMovementArrivalDriven => _phasedMeleeSpecialMovementArrivalDriven;
        public bool IsPhasedMeleeSpecialMovementEndRequested => _phasedMeleeSpecialMovementEndRequested;
        public ActiveMeleePresentation? ActiveMeleePresentation { get; private set; }
        public bool ActiveMeleePresentationEntered { get; private set; }
        public ActiveSpellPresentation? ActiveSpellPresentation { get; private set; }
        public bool ActiveSpellPresentationEntered { get; private set; }
        public ActiveOverlaySpellPresentation? ActiveOverlaySpellPresentation { get; private set; }
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

        public static bool HasEnteredExpectedAnimatorState(
            int dispatchedFrame,
            int currentFrame,
            int expectedStateHash,
            int currentStateHash,
            bool isInTransition,
            int nextStateHash)
        {
            // Animator triggers are evaluated after script Update. During the dispatch
            // frame Unity can still report the outgoing presentation, including the same
            // banked state reused by the incoming action. Never attribute that state to
            // the new presentation until at least the following frame.
            if (currentFrame <= dispatchedFrame || expectedStateHash == 0)
                return false;

            return currentStateHash == expectedStateHash
                || (isInTransition && nextStateHash == expectedStateHash);
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

        public static void CrossFadeMeleeStrikeState(
            Animator animator,
            int meleeLayerIndex,
            int stateHash,
            float fixedTransitionDurationSeconds,
            float normalizedTime)
        {
            animator.SetLayerWeight(meleeLayerIndex, 1f);
            animator.CrossFadeInFixedTime(
                stateHash,
                Mathf.Max(0f, fixedTransitionDurationSeconds),
                meleeLayerIndex,
                Mathf.Clamp01(normalizedTime));
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

        // The animator layer the in-progress hold fade-out is blending. Masked holds
        // (UpperBody / LeftGesture) render on their own layer, not SpellAction, so the
        // fade must target whichever layer the hold actually played on.
        public int SpellCastHoldFadeOutLayerIndex { get; private set; }

        public void StartSpellCastHoldFadeOut(
            float nowSeconds,
            float blendOutSeconds,
            float delaySeconds,
            int layerIndex)
        {
            // Push the start point into the future by `delaySeconds`. ResolveLayerWeight
            // returns LayerWeightAtUnlock (1f) while elapsed < 0 because Mathf.Clamp01
            // clamps the negative blendT to 0, so the layer holds at full weight during
            // the delay and then blends to 0 over BlendOutSeconds.
            SpellCastHoldFadeOutLayerIndex = layerIndex;
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
            => BeginPhasedMelee(
                bankSlot,
                startClip,
                loopClip,
                endClip,
                releaseAfterStart,
                specialMovementDriven,
                specialMovementArrivalDriven: false);

        public void BeginPhasedMelee(
            int bankSlot,
            AnimationClip startClip,
            AnimationClip loopClip,
            AnimationClip endClip,
            bool releaseAfterStart,
            bool specialMovementDriven,
            bool specialMovementArrivalDriven)
        {
            _phasedMeleeBankSlot = bankSlot;
            _phasedMeleeStateHash = 0;
            _phasedMeleeStartClip = startClip;
            _phasedMeleeLoopClip = loopClip;
            _phasedMeleeEndClip = endClip;
            bool hasAuthoredStartExit = CombatAnimationEvents.TryGetEventNormalizedTime(
                startClip,
                CombatAnimationEvents.OnPhaseLoopReady,
                out _phasedMeleeAuthoredStartExitNormalizedTime);
            if (!hasAuthoredStartExit)
            {
                _phasedMeleeAuthoredStartExitNormalizedTime = -1f;
            }
            else
            {
                float safetyNormalizedTime = releaseAfterStart
                    ? CombatAnimationEvents.PhasedMeleeStartOnlyEndSafetyNormalizedTime
                    : CombatAnimationEvents.PhasedMeleeStartToLoopSafetyNormalizedTime;
                _phasedMeleeAuthoredStartExitNormalizedTime = Mathf.Min(
                    _phasedMeleeAuthoredStartExitNormalizedTime,
                    safetyNormalizedTime);
            }

            float startTimelineLengthSeconds = hasAuthoredStartExit
                ? Mathf.Max(0f, startClip.length) * _phasedMeleeAuthoredStartExitNormalizedTime
                : Mathf.Max(0f, startClip.length);
            bool hasAuthoredLoopExit = !releaseAfterStart
                && CombatAnimationEvents.TryGetEventNormalizedTime(
                    loopClip,
                    CombatAnimationEvents.OnPhaseLoopReady,
                    out _phasedMeleeAuthoredLoopExitNormalizedTime);
            if (!hasAuthoredLoopExit)
            {
                _phasedMeleeAuthoredLoopExitNormalizedTime = -1f;
            }
            else
            {
                _phasedMeleeAuthoredLoopExitNormalizedTime = Mathf.Min(
                    _phasedMeleeAuthoredLoopExitNormalizedTime,
                    CombatAnimationEvents.PhasedMeleeStartToLoopSafetyNormalizedTime);
            }

            float loopTimelineLengthSeconds = releaseAfterStart
                ? 0f
                : hasAuthoredLoopExit
                    ? Mathf.Max(0f, loopClip.length) * _phasedMeleeAuthoredLoopExitNormalizedTime
                    : Mathf.Max(0f, loopClip.length);
            _phasedMeleeElapsedBeforePhase = 0f;
            _phasedMeleeCurrentPhaseLengthSeconds = 0f;
            _phasedMeleeTotalLengthSeconds = startTimelineLengthSeconds
                + loopTimelineLengthSeconds
                + Mathf.Max(0f, endClip.length);
            _phasedMeleeSpecialMovementDriven = specialMovementDriven;
            _phasedMeleeSpecialMovementArrivalDriven =
                specialMovementDriven && specialMovementArrivalDriven;
            _phasedMeleeSpecialMovementEndRequested = false;
            _phasedMeleeUpperBodyMode = false;
            _phasedMeleeSegmentEntered = false;
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
            _phasedMeleeAuthoredStartExitNormalizedTime = -1f;
            _phasedMeleeAuthoredLoopExitNormalizedTime = -1f;
            _phasedMeleeElapsedBeforePhase = 0f;
            _phasedMeleeCurrentPhaseLengthSeconds = 0f;
            _phasedMeleeTotalLengthSeconds = 0f;
            _phasedMeleeSegmentEntered = false;
            _phasedMeleeSpecialMovementDriven = false;
            _phasedMeleeSpecialMovementArrivalDriven = false;
            _phasedMeleeSpecialMovementEndRequested = false;
            return wasActive;
        }

        /// <summary>
        /// Arrival-driven phased attacks resolve lower-body release against the current
        /// phase because their Loop duration is variable. End's authored marker is the
        /// sole timing authority; after it fires, the same End clip continues on the
        /// upper body while locomotion can reclaim the legs.
        /// </summary>
        public static bool CanReleaseMeleeLowerBody(
            bool activeIsPhased,
            bool phasedMeleeArrivalDriven,
            PhasedMeleePlaybackPhase phase,
            float phaseNormalizedTime,
            float authoredPhaseUnlockNormalizedTime)
        {
            if (!activeIsPhased || !phasedMeleeArrivalDriven)
                return true;

            return phase == PhasedMeleePlaybackPhase.End
                && phaseNormalizedTime >= Mathf.Clamp01(authoredPhaseUnlockNormalizedTime);
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
            _phasedMeleeSegmentEntered = false;
        }

        public void MarkPhasedMeleeSegmentEntered()
        {
            if (_phasedMeleeActive && _phasedMeleePhase != PhasedMeleePlaybackPhase.None)
                _phasedMeleeSegmentEntered = true;
        }

        public void ResetPhasedMeleeSegmentEntry() => _phasedMeleeSegmentEntered = false;

        public void EnterPhasedMeleeUpperBodyMode()
        {
            _phasedMeleeUpperBodyMode = true;
        }

        public void AddCompletedPhasedMeleePhaseSeconds(float normalizedTime)
        {
            _phasedMeleeElapsedBeforePhase +=
                Mathf.Max(0f, _phasedMeleeCurrentPhaseLengthSeconds) * Mathf.Clamp01(normalizedTime);
        }

        public float ResolvePhasedMeleeStartExitNormalizedTime(
            float startOnlyEndTriggerNormalizedTime,
            float segmentTransitionNormalizedTime)
        {
            if (_phasedMeleeAuthoredStartExitNormalizedTime >= 0f)
            {
                float safetyNormalizedTime = Mathf.Clamp01(
                    _phasedMeleeReleaseAfterStart
                        ? startOnlyEndTriggerNormalizedTime
                        : segmentTransitionNormalizedTime);
                return Mathf.Min(
                    Mathf.Clamp01(_phasedMeleeAuthoredStartExitNormalizedTime),
                    safetyNormalizedTime);
            }

            return Mathf.Clamp01(
                _phasedMeleeReleaseAfterStart
                    ? startOnlyEndTriggerNormalizedTime
                    : segmentTransitionNormalizedTime);
        }

        public float ResolvePhasedMeleeLoopExitNormalizedTime(float fallbackNormalizedTime)
        {
            if (_phasedMeleeAuthoredLoopExitNormalizedTime >= 0f)
            {
                return Mathf.Min(
                    Mathf.Clamp01(_phasedMeleeAuthoredLoopExitNormalizedTime),
                    Mathf.Clamp01(fallbackNormalizedTime));
            }

            return Mathf.Clamp01(fallbackNormalizedTime);
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

        /// <summary>
        /// Phase policy for special-movement-driven phased melee, shared by the
        /// authoritative flow and the predicted gap-close windup (feel audit F5).
        /// The caller's normalizedTime is the elapsed fraction of the current
        /// segment — for a predicted windup that clock starts at press, so by the
        /// time track sampling owns movement (~RTT later) playback resumes from
        /// the same offset instead of restarting. The loop holds until the
        /// special-movement row delete requests the end segment.
        /// </summary>
        public static bool TryResolveSpecialMovementDrivenPhasedTransition(
            PhasedMeleePlaybackPhase currentPhase,
            float normalizedTime,
            bool releaseAfterStart,
            bool endRequested,
            float startOnlyEndTriggerNormalizedTime,
            float segmentTransitionNormalizedTime,
            float endCompleteNormalizedTime,
            out PhasedMeleePlaybackPhase nextPhase,
            out bool shouldCancel)
        {
            nextPhase = PhasedMeleePlaybackPhase.None;
            shouldCancel = false;

            if (currentPhase == PhasedMeleePlaybackPhase.Start)
            {
                float startExitNormalizedTime = releaseAfterStart
                    ? startOnlyEndTriggerNormalizedTime
                    : segmentTransitionNormalizedTime;
                if (normalizedTime < startExitNormalizedTime)
                    return false;

                // Start/Loop/End sets must enter Loop at least once even if a very short
                // movement reaches its destination while Start is still playing. The
                // latched end request advances that entered Loop to End on the next pass.
                nextPhase = releaseAfterStart
                    ? PhasedMeleePlaybackPhase.End
                    : PhasedMeleePlaybackPhase.Loop;
                return true;
            }

            if (currentPhase == PhasedMeleePlaybackPhase.Loop)
            {
                if (!endRequested)
                    return false;

                nextPhase = PhasedMeleePlaybackPhase.End;
                return true;
            }

            if (currentPhase == PhasedMeleePlaybackPhase.End
                && normalizedTime >= endCompleteNormalizedTime)
            {
                shouldCancel = true;
                return true;
            }

            return false;
        }

        /// <summary>
        /// A predicted gap-close windup already occupies the phased melee slot
        /// when the server's COMBAT_CAST replay arrives, so an authoritative
        /// special-movement start for the same action must be ignored rather
        /// than replayed from zero (feel audit F5). Once the end segment has
        /// been requested the dash is over and a same-action start is a new
        /// action, not a duplicate.
        /// </summary>
        public static bool IsDuplicateAuthoritativeSpecialMovementMeleeStart(
            CombatAnimationAuthority incomingAuthority,
            bool incomingDrivesPhasesFromSpecialMovement,
            bool incomingMatchesActiveMeleeActionId,
            bool activePhasedMeleeIsSpecialMovementDriven,
            bool activePhasedMeleeEndRequested)
        {
            return incomingAuthority == CombatAnimationAuthority.Authoritative
                && incomingDrivesPhasesFromSpecialMovement
                && incomingMatchesActiveMeleeActionId
                && activePhasedMeleeIsSpecialMovementDriven
                && !activePhasedMeleeEndRequested;
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
                float startExitNormalizedTime = ResolvePhasedMeleeStartExitNormalizedTime(
                    startOnlyEndTriggerNormalizedTime,
                    segmentTransitionNormalizedTime);
                if (_phasedMeleeReleaseAfterStart)
                {
                    if (normalizedTime < startExitNormalizedTime)
                        return false;

                    nextPhase = PhasedMeleePlaybackPhase.End;
                    return true;
                }

                if (normalizedTime < startExitNormalizedTime)
                    return false;

                nextPhase = PhasedMeleePlaybackPhase.Loop;
                return true;
            }

            if (_phasedMeleePhase == PhasedMeleePlaybackPhase.Loop)
            {
                if (normalizedTime < ResolvePhasedMeleeLoopExitNormalizedTime(segmentTransitionNormalizedTime))
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
            bool isAutoAttackSequenceRestart,
            bool activeMeleeIsPhased,
            bool visualGateEvaluated,
            CombatVisualInterruptDecision visualDecision)
        {
            // Explicit melee combo follow-ups and auto-attack sequence steps may hand off
            // directly. An auto-attack reaches this branch only when the active presentation
            // also owns that auto-attack sequence, never when a skill or spell owns the pose.
            if (isMeleeActive && (isComboFollowUp || isAutoAttackSequenceRestart))
            {
                return activeMeleeIsPhased
                    ? CombatAnimationDecision.InterruptCurrentWithoutGhostAndPlay
                    : CombatAnimationDecision.HandoffComboFollowUpAndPlay;
            }

            if (incomingCategory == CombatAnimationCategory.AutoAttack && isHigherPriorityActive)
                return CombatAnimationDecision.DropAsLowerPriority;

            if (isSpellActive)
                return CombatAnimationDecision.InterruptCurrentWithoutGhostAndPlay;

            if (incomingCategory != CombatAnimationCategory.AutoAttack && isMeleeActive)
            {
                return visualGateEvaluated && visualDecision == CombatVisualInterruptDecision.InterruptCurrentWithoutGhost
                    ? CombatAnimationDecision.InterruptCurrentWithoutGhostAndPlay
                    : CombatAnimationDecision.InterruptCurrentAndPlay;
            }

            return CombatAnimationDecision.PlayNow;
        }

        public static bool HasTrackedHigherPriorityPresentation(
            bool hasActiveMeleePresentation,
            CombatAnimationCategory activeMeleeCategory,
            bool hasActiveSpellPresentation,
            bool hasActiveSpellCastHoldPresentation)
        {
            // Animator triggers are consumed after script Update. The tracked
            // presentation exists immediately, so it closes the one-frame
            // window where a due auto-attack could otherwise replace a skill
            // before the Animator reports that skill's state as active.
            return (hasActiveMeleePresentation
                    && activeMeleeCategory != CombatAnimationCategory.AutoAttack)
                || hasActiveSpellPresentation
                || hasActiveSpellCastHoldPresentation;
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

            // Auto-attack gameplay is independent of its presentation. A due swing may still
            // resolve, but its animation never replaces a higher-priority skill or spell.
            if (incomingCategory == CombatAnimationCategory.AutoAttack)
                return CombatVisualInterruptDecision.SuppressIncomingWithGhost;

            return activeElapsedSeconds >= activeVisualInterruptibleAtSeconds
                ? CombatVisualInterruptDecision.InterruptCurrentWithoutGhost
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
            SpellPlaybackLayer playbackLayer,
            float enterCompleteNormalizedTime,
            float exitBlendOutSeconds,
            float exitDelaySeconds)
        {
            ActiveSpellCastHoldPresentation = new ActiveSpellCastHoldPresentation(
                actionId,
                enterBankSlot,
                idleBankSlot,
                playbackLayer,
                enterCompleteNormalizedTime,
                exitBlendOutSeconds,
                exitDelaySeconds);
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

        public void SetActiveOverlaySpellPresentation(string actionId, int stateHash, bool usesLeftGesture)
        {
            ActiveOverlaySpellPresentation = new ActiveOverlaySpellPresentation(actionId, stateHash, usesLeftGesture);
        }

        public void ClearActiveOverlaySpellPresentation()
        {
            ActiveOverlaySpellPresentation = null;
        }

        /// <summary>
        /// Rejection-cut identity policy (netcode design review S2): a
        /// server-rejected action may only cut a presentation it can still be
        /// positively attributed to, so playback owned by a later press is
        /// never eaten by a stale rejection.
        /// </summary>
        public static bool ShouldCutRejectedActionPresentation(string? activeActionId, string rejectedActionId)
        {
            return !string.IsNullOrWhiteSpace(activeActionId)
                && !string.IsNullOrWhiteSpace(rejectedActionId)
                && string.Equals(activeActionId, rejectedActionId, System.StringComparison.OrdinalIgnoreCase);
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
            out WeaponSpellAnimationEntry spellEntry,
            out bool confirmedInstant)
        {
            spellEntry = default;
            confirmedInstant = false;
            if (overrideController == null || animationSet == null)
            {
                Debug.LogWarning(
                    $"[CombatActionPlaybackController] Spell '{spellKind}' could not bind because animator override controller or animation set is missing.");
                return false;
            }

            if (!SpellCastAnimationResolver.TryResolve(
                    animationSet,
                    spellKind,
                    out spellEntry,
                    out confirmedInstant))
            {
                if (SpellCastAnimationResolver.TryDescribeMappedResolutionFailure(animationSet, spellKind, out string reason))
                    WarnSpellAnimationResolutionFailure(animationSet, spellKind, reason);
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

        private void WarnSpellAnimationResolutionFailure(
            CombatAnimationSet animationSet,
            string spellKind,
            string reason)
        {
            string normalizedSpellKind = string.IsNullOrWhiteSpace(spellKind)
                ? "<missing>"
                : spellKind.Trim().ToUpperInvariant();
            string warningKey = $"{animationSet.name}:{normalizedSpellKind}:{reason}";
            if (!_spellAnimationResolutionWarnings.Add(warningKey))
                return;

            Debug.LogWarning(
                $"[CombatActionPlaybackController] Spell '{normalizedSpellKind}' has a SpellCastAnimationMap entry but could not resolve a runtime animation in set '{animationSet.name}': {reason}.");
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
