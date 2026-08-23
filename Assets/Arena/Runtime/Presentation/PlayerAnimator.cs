#nullable enable
using System;
using System.Collections.Generic;
using UnityEngine;
using Arena.Combat;
using Arena.Interaction;
using Arena.Network;
using Arena.Simulation;
using Arena.Input;
using SpacetimeDB.Types;

namespace Arena.Presentation
{
    /// <summary>
    /// Receives authored combat AnimationEvents so Unity does not emit "no receiver"
    /// warnings. Gameplay reads these events as clip timing data through
    /// CombatAnimationEvents; these callbacks are intentionally no-ops.
    /// </summary>
    public sealed class CombatAnimationEventReceiver : MonoBehaviour
    {
        public static void EnsureOn(Animator? animator)
        {
            if (animator == null)
                return;

            if (animator.GetComponent<CombatAnimationEventReceiver>() == null)
                animator.gameObject.AddComponent<CombatAnimationEventReceiver>();
        }

        public void OnReleaseFrame() { }
        public void OnInstantCastStart() { }
        public void OnDodgeStart() { }
        public void OnDodgeTravelEnd() { }
        public void OnEnterComplete() { }
        public void OnHoldFadeStart() { }
        public void OnHoldFadeEnd() { }
        public void OnLowerBodyUnlock() { }
        public void OnVisualInterruptible() { }
        public void OnStrikeHit() { }
        public void OnPhaseLoopReady() { }
        public void OnGroundedFrame() { }
        public void OnParryWindowStart() { }
        public void OnParryWindowEnd() { }
        public void OnBlockReady() { }
        public void OnWeaponHandoff() { }
    }

    /// <summary>
    /// Drives the character Animator using the canonical Arena_Character controller.
    ///
    /// Clip variation per animation set is handled via AnimatorOverrideController -
    /// the state machine topology never changes, only the clips playing in each slot.
    ///
    /// INVARIANT: Presentation only. No network access. Never gates input.
    /// Input gating lives in SpellInputHandler / LocalCombatState exclusively.
    /// </summary>
    public class PlayerAnimator : MonoBehaviour
    {
        // Add ARENA_VERBOSE_RUNTIME_TRACES to Scripting Define Symbols only
        // while actively diagnosing combat or interaction presentation. Using
        // Conditional removes both the log call and interpolated-string work
        // from ordinary Editor and development builds.
        private const string VerboseTraceSymbol = "ARENA_VERBOSE_RUNTIME_TRACES";

        // Fixed canonical parameter contract. These hashes are computed once at class load.
        // Never look up parameters by string at runtime — if the controller is missing a
        // parameter, the Set* call silently no-ops, which is intentional and safe.
        private static readonly int VelocityXHash        = Animator.StringToHash("VelocityX");
        private static readonly int VelocityZHash        = Animator.StringToHash("VelocityZ");
        private static readonly int StopXHash            = Animator.StringToHash("StopX");
        private static readonly int StopZHash            = Animator.StringToHash("StopZ");
        private static readonly int JumpXHash            = Animator.StringToHash("JumpX");
        private static readonly int JumpZHash            = Animator.StringToHash("JumpZ");
        private static readonly int TurnAngleHash        = Animator.StringToHash("TurnAngle");
        private static readonly int MotionSpeedHash      = Animator.StringToHash("MotionSpeed");
        private static readonly int GroundedHash         = Animator.StringToHash("Grounded");
        private static readonly int JumpHash             = Animator.StringToHash("Jump");
        private static readonly int FreeFallHash         = Animator.StringToHash("FreeFall");
        private static readonly int IsDeadHash           = Animator.StringToHash("IsDead");
        private static readonly int InCombatHash         = Animator.StringToHash("InCombat");
        private static readonly int TriggerStrike1Hash   = Animator.StringToHash("TriggerStrike1");
        private static readonly int TriggerStrike2Hash   = Animator.StringToHash("TriggerStrike2");
        private static readonly int TriggerStrike3Hash   = Animator.StringToHash("TriggerStrike3");
        private static readonly int TriggerStrike4Hash   = Animator.StringToHash("TriggerStrike4");
        private static readonly int TriggerSpellAction1Hash = Animator.StringToHash("TriggerSpellAction1");
        private static readonly int TriggerSpellAction2Hash = Animator.StringToHash("TriggerSpellAction2");
        private static readonly int TriggerSpellAction3Hash = Animator.StringToHash("TriggerSpellAction3");
        private static readonly int TriggerSpellAction4Hash = Animator.StringToHash("TriggerSpellAction4");
        private static readonly int MirrorSpellActionHash = Animator.StringToHash("MirrorSpellAction");
        private static readonly int TriggerParryHitHash  = Animator.StringToHash("TriggerParryHit");
        private static readonly int IsBlockingHash       = Animator.StringToHash("IsBlocking");
        private static readonly int TriggerBlockStartHash = Animator.StringToHash("TriggerBlockStart");
        private static readonly int TriggerBlockHitHash  = Animator.StringToHash("TriggerBlockHit");
        private static readonly int BlockStartStateHash  = Animator.StringToHash("BlockStart");
        private static readonly int BlockLoopStateHash   = Animator.StringToHash("BlockLoop");
        private static readonly int BlockEndStateHash    = Animator.StringToHash("BlockEnd");
        private static readonly int BlockHitStateHash    = Animator.StringToHash("BlockHit");
        private static readonly int Strike1StateHash = Animator.StringToHash("Strike1");
        private static readonly int Strike2StateHash = Animator.StringToHash("Strike2");
        private static readonly int Strike3StateHash = Animator.StringToHash("Strike3");
        private static readonly int Strike4StateHash = Animator.StringToHash("Strike4");
        private static readonly int MeleeAttackStrike1FullPathHash = Animator.StringToHash("MeleeAttack.Strike1");
        private static readonly int MeleeAttackStrike2FullPathHash = Animator.StringToHash("MeleeAttack.Strike2");
        private static readonly int MeleeAttackStrike3FullPathHash = Animator.StringToHash("MeleeAttack.Strike3");
        private static readonly int MeleeAttackStrike4FullPathHash = Animator.StringToHash("MeleeAttack.Strike4");
        private static readonly int TriggerDodgeHash     = Animator.StringToHash("TriggerDodge");
        private static readonly int DodgeXHash           = Animator.StringToHash("DodgeX");
        private static readonly int DodgeZHash           = Animator.StringToHash("DodgeZ");
        private static readonly int DodgePhaseHash       = Animator.StringToHash("DodgePhase");
        private static readonly int TriggerWalkStopHash  = Animator.StringToHash("TriggerWalkStop");
        private static readonly int TriggerRunStopHash   = Animator.StringToHash("TriggerRunStop");
        private static readonly int TriggerTurnHash      = Animator.StringToHash("TriggerTurn");
        private static readonly int EnterCombatIdleStateHash = Animator.StringToHash("EnterCombatIdle");
        private static readonly int EnterCombatWalkStateHash = Animator.StringToHash("EnterCombatWalk");
        private static readonly int EnterCombatRunStateHash = Animator.StringToHash("EnterCombatRun");
        private static readonly int ExitCombatIdleStateHash = Animator.StringToHash("ExitCombatIdle");
        private static readonly int ExitCombatWalkStateHash = Animator.StringToHash("ExitCombatWalk");
        private static readonly int ExitCombatRunStateHash = Animator.StringToHash("ExitCombatRun");
        private static readonly int DrawWeaponStateHash  = Animator.StringToHash("DrawWeapon");
        private static readonly int SheathWeaponStateHash = Animator.StringToHash("SheathWeapon");
        private static readonly int UpperBodyEmptyStateHash = Animator.StringToHash("Empty");
        private static readonly int UpperBodyDrawWeaponStateHash = Animator.StringToHash("UpperBodyDrawWeapon");
        private static readonly int UpperBodySheathWeaponStateHash = Animator.StringToHash("UpperBodySheathWeapon");
        private static readonly int UpperBodyBlockStartStateHash = Animator.StringToHash("UpperBodyBlockStart");
        private static readonly int UpperBodyBlockLoopStateHash = Animator.StringToHash("UpperBodyBlockLoop");
        private static readonly int UpperBodyBlockEndStateHash = Animator.StringToHash("UpperBodyBlockEnd");
        private static readonly int UpperBodyBlockHitStateHash = Animator.StringToHash("UpperBodyBlockHit");
        private static readonly int UpperBodySpellAction1StateHash = Animator.StringToHash("UpperBodySpellAction1");
        private static readonly int UpperBodySpellAction2StateHash = Animator.StringToHash("UpperBodySpellAction2");
        private static readonly int UpperBodySpellAction3StateHash = Animator.StringToHash("UpperBodySpellAction3");
        private static readonly int UpperBodySpellAction4StateHash = Animator.StringToHash("UpperBodySpellAction4");
        private static readonly int LeftGestureSpellAction1StateHash = Animator.StringToHash("LeftGestureSpellAction1");
        private static readonly int LeftGestureSpellAction2StateHash = Animator.StringToHash("LeftGestureSpellAction2");
        private static readonly int LeftGestureSpellAction3StateHash = Animator.StringToHash("LeftGestureSpellAction3");
        private static readonly int LeftGestureSpellAction4StateHash = Animator.StringToHash("LeftGestureSpellAction4");
        // Loop-capable left-gesture hold states (no exit transition). The shared
        // LeftGestureSpellAction* states auto-exit to Empty at 0.9 (they double as
        // one-shot masked spell releases), so a charged/channel hold routed through
        // them stops after ~0.9 of the loop. These dedicated states carry no exit
        // transition, letting a looping Load clip loop for the charge/channel while
        // the LeftGesture mask keeps the weapon-bearing right arm on its base pose.
        private static readonly int LeftGestureSpellCastHoldAction1StateHash = Animator.StringToHash("LeftGestureSpellCastHoldAction1");
        private static readonly int LeftGestureSpellCastHoldAction2StateHash = Animator.StringToHash("LeftGestureSpellCastHoldAction2");
        private static readonly int LeftGestureSpellCastHoldAction3StateHash = Animator.StringToHash("LeftGestureSpellCastHoldAction3");
        private static readonly int LeftGestureSpellCastHoldAction4StateHash = Animator.StringToHash("LeftGestureSpellCastHoldAction4");
        private static readonly int RightGestureSpellAction1StateHash = Animator.StringToHash("RightGestureSpellAction1");
        private static readonly int RightGestureSpellAction2StateHash = Animator.StringToHash("RightGestureSpellAction2");
        private static readonly int RightGestureSpellAction3StateHash = Animator.StringToHash("RightGestureSpellAction3");
        private static readonly int RightGestureSpellAction4StateHash = Animator.StringToHash("RightGestureSpellAction4");
        private static readonly int RightGestureSpellCastHoldAction1StateHash = Animator.StringToHash("RightGestureSpellCastHoldAction1");
        private static readonly int RightGestureSpellCastHoldAction2StateHash = Animator.StringToHash("RightGestureSpellCastHoldAction2");
        private static readonly int RightGestureSpellCastHoldAction3StateHash = Animator.StringToHash("RightGestureSpellCastHoldAction3");
        private static readonly int RightGestureSpellCastHoldAction4StateHash = Animator.StringToHash("RightGestureSpellCastHoldAction4");
        private static readonly int UpperBodyRecoveryAction1StateHash = Animator.StringToHash("UpperBodyRecoveryAction1");
        private static readonly int SpellAction1StateHash = Animator.StringToHash("SpellAction1");
        private static readonly int SpellAction2StateHash = Animator.StringToHash("SpellAction2");
        private static readonly int SpellAction3StateHash = Animator.StringToHash("SpellAction3");
        private static readonly int SpellAction4StateHash = Animator.StringToHash("SpellAction4");
        private static readonly int SpellCastHoldAction1StateHash = Animator.StringToHash("SpellCastHoldAction1");
        private static readonly int SpellCastHoldAction2StateHash = Animator.StringToHash("SpellCastHoldAction2");
        private static readonly int SpellCastHoldAction3StateHash = Animator.StringToHash("SpellCastHoldAction3");
        private static readonly int SpellCastHoldAction4StateHash = Animator.StringToHash("SpellCastHoldAction4");
        // Dedicated non-looping-exit upper-body hold states. The shared
        // UpperBodySpellAction* states auto-exit to Empty at 0.9 (they double as
        // one-shot overlay spell releases), so a masked hold routed through them
        // stops after ~0.9 of the idle clip. These states carry no exit
        // transition, letting a looping idle clip loop for the channel duration.
        private static readonly int UpperBodySpellCastHoldAction1StateHash = Animator.StringToHash("UpperBodySpellCastHoldAction1");
        private static readonly int UpperBodySpellCastHoldAction2StateHash = Animator.StringToHash("UpperBodySpellCastHoldAction2");
        private static readonly int UpperBodySpellCastHoldAction3StateHash = Animator.StringToHash("UpperBodySpellCastHoldAction3");
        private static readonly int UpperBodySpellCastHoldAction4StateHash = Animator.StringToHash("UpperBodySpellCastHoldAction4");
        private static readonly int SpellCastHoldAction1FullPathHash = Animator.StringToHash("SpellAction.SpellCastHoldAction1");
        private static readonly int SpellCastHoldAction2FullPathHash = Animator.StringToHash("SpellAction.SpellCastHoldAction2");
        private static readonly int SpellCastHoldAction3FullPathHash = Animator.StringToHash("SpellAction.SpellCastHoldAction3");
        private static readonly int SpellCastHoldAction4FullPathHash = Animator.StringToHash("SpellAction.SpellCastHoldAction4");
        private static readonly int JumpStartStateHash   = Animator.StringToHash("JumpStart");
        private static readonly int JumpStartCombatStateHash = Animator.StringToHash("JumpStartCombat");
        private static readonly int JumpLandStateHash    = Animator.StringToHash("JumpLand");
        private static readonly int JumpLandCombatStateHash = Animator.StringToHash("JumpLandCombat");
        private static readonly int DodgeStateHash       = Animator.StringToHash("Dodge");

        private static readonly int IdleWalkRunBlendStateHash = Animator.StringToHash("Idle Walk Run Blend");
        private static readonly int IdleCombatStateHash       = Animator.StringToHash("IdleCombat");

        // Client presentation normalization follows the authoritative server move speed.
        private const float BaseRunSpeed = GameplayTuning.BaseMoveSpeed;
        private const float MovementDeadZone = 0.05f;
        private const float StopTriggerThreshold = 0.1f;
        private const float RunStopThreshold = 0.75f;
        private const float StopTriggerCooldownSeconds = 0.2f;
        private const float RejumpCrossFadeDurationSeconds = 0.04f;
        private const float LocomotionRecoveryCrossFadeDurationSeconds = 0.32f;
        private const float DodgeRecoveryPlaybackSpeed = 1f;
        private const float SpellCastHoldEnterToIdleNormalizedTime = 0.85f;
        private const float SpellCastHoldEnterCrossFadeDurationSeconds = 0.22f;
        private const float SpellCastHoldPhaseCrossFadeDurationSeconds = 0.15f;
        private const float SpellCastHoldExitCrossFadeDurationSeconds = 0.28f;
        private const float SpellHoldPulseAdvanceNormalizedTime = 0.9f;
        // Hold the cast pose for this long after release fires before blending out.
        // Keeps the legs/torso aligned with the spell motion until the release animation
        // is mostly done, instead of snapping back to idle combat mid-cast.
        private const float SpellCastHoldExitDelaySeconds = 0.35f;
        // Strike bank states transition back to Empty at 0.9 normalized time.
        // Segmented/phased melee advances slightly before that so start/loop/end
        // segments never fall through to Empty between runtime-controlled plays.
        private const float PhasedMeleeStartOnlyEndTriggerNormalizedTime =
            CombatAnimationEvents.PhasedMeleeStartOnlyEndSafetyNormalizedTime;
        private const float PhasedMeleeSegmentTransitionNormalizedTime =
            CombatAnimationEvents.PhasedMeleeStartToLoopSafetyNormalizedTime;
        private const float PhasedMeleeEndCompleteNormalizedTime = 0.88f;
        private const float PhasedMeleeLoopReplayNormalizedTime = 0.8f;
        /// Safety ceiling on re-arming a held phased loop. The authored end
        /// signal is what should stop it; this only guarantees a lost or
        /// mismatched end event cannot strand the animation forever, which is
        /// what the banked state's own 0.9 exit used to do by accident. Well
        /// above the longest authored channel (Rapid Fire, 5s).
        private const float PhasedMeleeHeldLoopMaxSeconds = 15f;
        private const float SpecialMovementArrivalEndCrossFadeDurationSeconds = 0.08f;
        private const float LandingRecoveryMinNormalizedTime = 0.16f;
        private const float WeaponTransitionRecoveryMinNormalizedTime = 0.18f;
        private const int BaseLayerIndex = 0;
        private const int UpperBodyLayerIndex = 1;
        private const int MeleeAttackLayerIndex = 3;
        private const int SpellActionLayerIndex = 4;
        private const int LeftGestureLayerIndex = 5;
        private const int RightGestureLayerIndex = 6;
        private const string UpperBodyRecoverySlotName = "slot_upper_body_recovery_1";
        private const string WorldInteractionSlotName = "slot_spell_4";
        private static readonly int MeleeAttackEmptyStateHash = Animator.StringToHash("Empty");
        private static readonly int SpellActionEmptyStateHash = Animator.StringToHash("Empty");

        private Animator? _animator;
        private AnimatorOverrideController? _overrideController;
        private ClientSimulationState? _simState;
        private Transform? _motionSource;
        private LocalPlayerMotor? _localPlayerMotor;
        private WeaponAttachmentController? _weaponAttachments;
        private MeleeAnimationGhostLayer? _meleeGhostLayer;
        private AnimatedAutoAttackGhostLayer? _animatedAutoAttackGhostLayer;
        private LingeringShadeGhostLayer? _lingeringShadeGhostLayer;
        private CombatAnimationVfxPlayer? _meleeAnimationVfxPlayer;
        private int _animatedAutoAttackGhostVisualVersion = -1;
        private CombatAnimationSet? _animationSet;
        private SharedActionProfile? _sharedActionProfile;
        private bool _isLocalPlayer;
        private bool _wasGrounded = true;
        private Vector3 _prevPosition;
        private float _smoothVelX;
        private float _smoothVelZ;
        private bool _inCombat;
        private LocalPlayerStateProvider? _stateProvider;
        private float _lastMoveVelX;
        private float _lastMoveVelZ;
        private bool _lastMoveWasRun;
        private bool _wasMoving;
        private float _lastFacingYawDegrees;
        private float _stopTriggerCooldown;
        private bool _isDead;
        private bool _hitUsesAirVariant;
        private bool _hitUsesCombatVariant;
        private bool _blockingPresentationActive;
        private bool _parryArmedPresentationActive;
        private ActiveMovementActionPresentation? _activeMovementPresentation;
        private float _activeDodgeStartNormalized;
        private float _activeDodgeTravelEndNormalized = -1f;
        private float _activeDodgeClipLengthSeconds;
        private int _pendingWeaponHandoffLayerIndex = -1;
        private int _pendingWeaponHandoffStateHash;
        private bool _pendingWeaponHandoffTargetInCombat;
        private bool _weaponHandoffStateEntered;
        private float _latestLocomotionRawMagnitude;
        private readonly CombatAnimationSetBinder _animationSetBinder = new();
        private readonly CombatActionPlaybackController _actionPlayback = new();
        private CombatStatusReactionController? _statusReactionController;
        private ActiveWorldInteractionPresentation? _worldInteractionPresentation;
        private PendingSpellHoldPulse? _pendingSpellHoldPulse;
        private AnimationClip? _worldInteractionPriorSlotClip;
        private AnimationClip? _worldInteractionAppliedClip;
        private float _worldInteractionReleaseAt;
        private int _activeMeleePresentationDispatchedFrame = -1;
        private int _activeSpellPresentationDispatchedFrame = -1;
        private int _phasedMeleeSegmentDispatchedFrame = -1;
        private bool _combatAnimationTraceAwaitingMeleeEntry;
        private string _combatAnimationTraceActionId = string.Empty;
        private CombatAnimationCategory _combatAnimationTraceCategory;
        private int _combatAnimationTraceRequestedFrame = -1;
        private int _combatAnimationTraceExpectedStateHash;
        private int _combatAnimationTraceObservationUntilFrame = -1;
        private int _combatAnimationTraceLastCurrentStateHash = int.MinValue;
        private int _combatAnimationTraceLastNextStateHash = int.MinValue;

        private CombatStatusReactionController StatusReactionController =>
            _statusReactionController ??= new CombatStatusReactionController(
                IsCurrentlyGrounded,
                ApplyHitClipOverrides,
                ClearPresentationForForcedStatus,
                ClearPresentationForStagger,
                CutRejectedActionPresentationScoped);

        private CombatAnimationVfxPlayer MeleeAnimationVfxPlayer =>
            _meleeAnimationVfxPlayer ??= new CombatAnimationVfxPlayer(
                this,
                () => _animator,
                () => _motionSource ?? transform,
                ResolveWeaponAttachments);

        private readonly struct ActiveMovementActionPresentation
        {
            public ActiveMovementActionPresentation(
                string kind,
                long startedAtMs,
                long activeUntilMs,
                long recoveryUntilMs)
            {
                Kind = kind;
                StartedAtMs = startedAtMs;
                ActiveUntilMs = activeUntilMs;
                RecoveryUntilMs = recoveryUntilMs;
            }

            public string Kind { get; }
            public long StartedAtMs { get; }
            public long ActiveUntilMs { get; }
            public long RecoveryUntilMs { get; }
            public bool IsDodge => string.Equals(Kind, "DODGE", StringComparison.OrdinalIgnoreCase);
        }

        private enum WorldInteractionPlaybackPhase
        {
            Start = 0,
            Loop = 1,
            Release = 2,
        }

        private sealed class ActiveWorldInteractionPresentation
        {
            public string ActionInstanceId = string.Empty;
            public long StartedAtMs;
            public long CompletesAtMs;
            public WorldInteractionAnimationProfile Profile = null!;
            public WorldInteractionPlaybackPhase Phase;
            public Transform? FacingTransform;
            public Quaternion PriorFacingLocalRotation;
        }

        private enum SpellHoldPulsePhase
        {
            Attack = 0,
            ReturnToHold = 1,
        }

        private sealed class PendingSpellHoldPulse
        {
            public string ActionId = string.Empty;
            public WeaponSpellAnimationEntry Entry;
            public AnimationClip ReturnToHold = null!;
            public SpellHoldPulsePhase Phase;
            public float AdvanceAtSeconds;
        }

        private enum CombatStanceTransitionBand
        {
            Idle = 0,
            Walk = 1,
            Run = 2,
        }

        private readonly struct LocomotionSample
        {
            public readonly float velocityX;
            public readonly float velocityZ;
            public readonly float rawMagnitude;
            public readonly float smoothMagnitude;
            public readonly float facingYawDegrees;

            public LocomotionSample(
                float velocityX,
                float velocityZ,
                float rawMagnitude,
                float smoothMagnitude,
                float facingYawDegrees)
            {
                this.velocityX = velocityX;
                this.velocityZ = velocityZ;
                this.rawMagnitude = rawMagnitude;
                this.smoothMagnitude = smoothMagnitude;
                this.facingYawDegrees = facingYawDegrees;
            }

            public bool IsMoving => rawMagnitude >= StopTriggerThreshold;
        }

        public void Initialize(
            ClientSimulationState simState,
            bool isLocalPlayer,
            Transform? motionSource = null,
            Animator? animatorOverride = null)
        {
            _simState      = simState;
            _isLocalPlayer = isLocalPlayer;
            _motionSource  = motionSource;
            _animator      = animatorOverride != null ? animatorOverride : GetComponentInChildren<Animator>();
            if (_animator != null)
            {
                // Gameplay/root movement is driven externally by prediction or authoritative
                // snapshots. Leaving clip root motion enabled can visually drag the mesh away
                // from the true gameplay root, which is especially noticeable on authored
                // attack and special-movement clips.
                _animator.applyRootMotion = false;
                CombatAnimationEventReceiver.EnsureOn(_animator);
                EnsureOverrideController();
                StatusReactionController.Bind(_animator, _overrideController);
            }
            _prevPosition  = (_motionSource ?? transform).position;
            _stateProvider = GetComponent<LocalPlayerStateProvider>();
            _localPlayerMotor = GetComponent<LocalPlayerMotor>();
            _weaponAttachments = GetComponent<WeaponAttachmentController>();
            _meleeGhostLayer = GetComponent<MeleeAnimationGhostLayer>();
            if (_meleeGhostLayer == null)
                _meleeGhostLayer = gameObject.AddComponent<MeleeAnimationGhostLayer>();
            _meleeGhostLayer.SetSourceRoot(_motionSource);
            _animatedAutoAttackGhostLayer = GetComponent<AnimatedAutoAttackGhostLayer>();
            if (_animatedAutoAttackGhostLayer == null)
                _animatedAutoAttackGhostLayer = gameObject.AddComponent<AnimatedAutoAttackGhostLayer>();
            _animatedAutoAttackGhostLayer.SetSource(_animator, _motionSource);
            _lingeringShadeGhostLayer = GetComponent<LingeringShadeGhostLayer>();
            if (_lingeringShadeGhostLayer == null)
                _lingeringShadeGhostLayer = gameObject.AddComponent<LingeringShadeGhostLayer>();
            _lingeringShadeGhostLayer.SetSource(_animator);
            _animatedAutoAttackGhostVisualVersion = -1;
            _lastFacingYawDegrees = GetFacingYawDegrees();
        }

        public void BindAnimator(Animator animator)
        {
            if (animator == null)
                return;

            _animator = animator;
            _animator.applyRootMotion = false;
            CombatAnimationEventReceiver.EnsureOn(_animator);
            EnsureOverrideController();
            StatusReactionController.Bind(_animator, _overrideController);

            if (_animationSet != null)
                ApplyAnimationSet(_animationSet);
            if (_sharedActionProfile != null)
                ApplySharedActionProfile(_sharedActionProfile);

            _weaponAttachments ??= GetComponent<WeaponAttachmentController>();
            _meleeGhostLayer?.SetSourceRoot(_motionSource);
            _animatedAutoAttackGhostLayer?.SetSource(_animator, _motionSource);
            _lingeringShadeGhostLayer?.SetSource(_animator);
            _animatedAutoAttackGhostVisualVersion = -1;

            if (_overrideController == null)
                return;

            _animator.SetBool(InCombatHash, _inCombat);
            _animator.SetBool(IsDeadHash, _isDead);
            _animator.SetBool(GroundedHash, IsCurrentlyGrounded());
            _animator.SetBool(IsBlockingHash, _blockingPresentationActive || _parryArmedPresentationActive);
        }

        /// <summary>
        /// Swaps the clips inside the canonical controller for this animation set.
        /// Safe to call any time after Initialize — does not reset animator state.
        /// Null clip fields on the set fall through to the base controller's slot clips.
        /// </summary>
        public void ApplyAnimationSet(CombatAnimationSet set)
        {
            _meleeAnimationVfxPlayer?.Clear();
            _animationSet = set;
            if (_animator == null) return;
            EnsureOverrideController();

            StatusReactionController.Bind(_animator, _overrideController);
            StatusReactionController.ApplyAnimationSet(set);
            _animationSetBinder.Bind(set, _overrideController!);
            ApplySharedActionOverrides(IsCurrentlyGrounded(), forceDodge: true);
            ApplyHitClipOverrides(IsCurrentlyGrounded(), force: true);

            _actionPlayback.ResetBanks(set);
            CancelPhasedMeleePlayback();
            _animatedAutoAttackGhostLayer?.InvalidateVisualClone();
            _lingeringShadeGhostLayer?.InvalidateVisualClone();
            TraceCombatAnimation(
                $"animation-set-applied id={set.AnimationSetIdOrDefault} strikes={set.MeleeAttackCount}");
        }

        internal void ApplyCombatLocomotionMode(string? modeId)
        {
            if (_animationSet == null || _animator == null)
                return;

            EnsureOverrideController();
            if (_overrideController == null)
                return;

            _animationSetBinder.ApplyLocomotionMode(_animationSet, modeId, _overrideController);
        }

        public bool IsInCombat => _inCombat;

        public void EnterCombatImmediate()
        {
            SetCombatVisualImmediate(true);
        }

        public void ExitCombatImmediate()
        {
            SetCombatVisualImmediate(false);
        }

        private void SetCombatVisualImmediate(bool inCombat)
        {
            _inCombat = inCombat;
            ClearPendingWeaponHandoff();

            if (_weaponAttachments == null)
                _weaponAttachments = GetComponent<WeaponAttachmentController>();
            _weaponAttachments?.SetInCombat(inCombat);

            if (_animator == null)
                return;

            _animator.SetBool(InCombatHash, inCombat);
            if (!CanDriveAnimatorState())
                return;

            int targetStateHash = inCombat ? IdleCombatStateHash : IdleWalkRunBlendStateHash;
            _animator.Play(targetStateHash, BaseLayerIndex, 0f);
            PlayUpperBodyState(UpperBodyEmptyStateHash, 0f);
        }

        /// <summary>
        /// Draws or sheathes weapons and sets the InCombat animator parameter.
        /// </summary>
        public void SetInCombat(bool inCombat)
        {
            _inCombat = inCombat;
            _animator?.SetBool(InCombatHash, inCombat);

            if (_weaponAttachments == null)
                _weaponAttachments = GetComponent<WeaponAttachmentController>();

            if (_weaponAttachments == null)
                return;

            if (_animationSet == null || _animator == null)
            {
                ClearPendingWeaponHandoff();
                _weaponAttachments.SetInCombat(inCombat);
                return;
            }

            if (!CanDriveAnimatorState())
            {
                ClearPendingWeaponHandoff();
                _weaponAttachments.SetInCombat(inCombat);
                return;
            }

            if (HasPendingWeaponHandoffFor(inCombat))
                return;

            if (inCombat)
            {
                if (_weaponAttachments.IsInCombatVisual)
                {
                    ClearPendingWeaponHandoff();
                    _weaponAttachments.SetInCombat(true);
                    return;
                }

                if (TryResolveCombatStanceTransitionStateHash(
                        enteringCombat: true,
                        (int)ResolveCombatStanceTransitionBand(),
                        _animationSet.enterCombatIdle != null,
                        _animationSet.enterCombatWalk != null,
                        _animationSet.enterCombatRun != null,
                        out int baseStateHash))
                {
                    BeginWeaponHandoff(
                        targetInCombat: true,
                        layerIndex: BaseLayerIndex,
                        stateHash: baseStateHash);
                    _animator.CrossFadeInFixedTime(baseStateHash, LocomotionRecoveryCrossFadeDurationSeconds, BaseLayerIndex, 0f);
                    return;
                }

                if (_animationSet.DrawWeaponClip == null)
                {
                    ClearPendingWeaponHandoff();
                    _weaponAttachments.SetInCombat(true);
                    return;
                }

                BeginWeaponHandoff(
                    targetInCombat: true,
                    layerIndex: UpperBodyLayerIndex,
                    stateHash: UpperBodyDrawWeaponStateHash);
                _animator.CrossFadeInFixedTime(IdleCombatStateHash, LocomotionRecoveryCrossFadeDurationSeconds, BaseLayerIndex, 0f);
                PlayUpperBodyState(UpperBodyDrawWeaponStateHash, 0f);
                return;
            }

            if (!_weaponAttachments.IsInCombatVisual)
            {
                ClearPendingWeaponHandoff();
                _weaponAttachments.SetInCombat(false);
                return;
            }

            if (TryResolveCombatStanceTransitionStateHash(
                    enteringCombat: false,
                    (int)ResolveCombatStanceTransitionBand(),
                    _animationSet.exitCombatIdle != null,
                    _animationSet.exitCombatWalk != null,
                    _animationSet.exitCombatRun != null,
                    out int exitStateHash))
            {
                BeginWeaponHandoff(
                    targetInCombat: false,
                    layerIndex: BaseLayerIndex,
                    stateHash: exitStateHash);
                _animator.CrossFadeInFixedTime(exitStateHash, LocomotionRecoveryCrossFadeDurationSeconds, BaseLayerIndex, 0f);
                return;
            }

            if (_animationSet.SheathWeaponClip == null)
            {
                ClearPendingWeaponHandoff();
                _weaponAttachments.SetInCombat(false);
                return;
            }

            BeginWeaponHandoff(
                targetInCombat: false,
                layerIndex: UpperBodyLayerIndex,
                stateHash: UpperBodySheathWeaponStateHash);
            _animator.CrossFadeInFixedTime(IdleWalkRunBlendStateHash, LocomotionRecoveryCrossFadeDurationSeconds, BaseLayerIndex, 0f);
            PlayUpperBodyState(UpperBodySheathWeaponStateHash, 0f);
        }

        public void SetDead(bool isDead)
        {
            bool wasDead = _isDead;
            _isDead = isDead;
            if (_animator != null && _overrideController != null)
            {
                if (isDead && !wasDead)
                    ClearNonDeathPresentation();

                _animator.SetBool(IsDeadHash, isDead);

                if (!isDead && wasDead)
                    RestoreAlivePresentationAfterDeath();
            }
        }

        private void RestoreAlivePresentationAfterDeath()
        {
            if (!CanDriveAnimatorState())
                return;

            ClearNonDeathPresentation();
            int targetStateHash = _inCombat ? IdleCombatStateHash : IdleWalkRunBlendStateHash;
            _animator!.Play(targetStateHash, BaseLayerIndex, 0f);
            _animator.Update(0f);
        }

        public void RequestCombatAnimation(in CombatAnimationRequest request)
        {
            ClearWorldInteractionAnimation();
            if (_animator == null || _overrideController == null)
            {
                TraceCombatAnimation(
                    $"request-dropped action={request.ActionId} category={request.Category} " +
                    $"reason=animator-or-override-missing");
                return;
            }

            if (request.Category == CombatAnimationCategory.Spell
                && request.SpellPhase == CombatSpellAnimationPhase.Release
                && TryPlaySpellCastHoldPulse(request))
            {
                TraceCombatAnimation(
                    $"request-complete action={request.ActionId} category={request.Category} " +
                    "mode=hold-pulse");
                return;
            }

            CombatAnimationDecision decision = DecideCombatAnimationRequest(request);
            TraceCombatAnimation(
                $"request action={request.ActionId} category={request.Category} authority={request.Authority} " +
                $"source={request.Source ?? "<none>"} decision={decision} " +
                $"tracked={DescribeTrackedMeleePresentation()} layer={DescribeMeleeLayer()}");
            if (decision == CombatAnimationDecision.IgnoreAsDuplicate)
                return;

            CombatPreemptionMode preemptionMode = CombatActionPlaybackController.ResolvePreemptionMode(
                decision,
                request.Category);

            switch (preemptionMode)
            {
                case CombatPreemptionMode.SuppressIncomingWithGhost:
                    CaptureSuppressedAutoAttackGhost(request);
                    return;
                case CombatPreemptionMode.InterruptWithGhost:
                    PreemptLowerPriorityPresentationFor(captureGhost: true);
                    TraceCombatAnimation(
                        $"preempt action={request.ActionId} mode={preemptionMode} layer-after={DescribeMeleeLayer()}");
                    break;
                case CombatPreemptionMode.InterruptWithoutGhost:
                    PreemptLowerPriorityPresentationFor(captureGhost: false);
                    TraceCombatAnimation(
                        $"preempt action={request.ActionId} mode={preemptionMode} layer-after={DescribeMeleeLayer()}");
                    break;
                case CombatPreemptionMode.HandoffComboFollowUp:
                    break;
                case CombatPreemptionMode.None:
                default:
                    break;
            }

            switch (request.Category)
            {
                case CombatAnimationCategory.Spell:
                    PlaySpellAnimation(request);
                    break;
                case CombatAnimationCategory.AutoAttack:
                case CombatAnimationCategory.MeleeSkill:
                    PlayMeleeAnimation(request);
                    break;
                default:
                    break;
            }

            TraceCombatAnimation(
                $"request-complete action={request.ActionId} category={request.Category} " +
                $"tracked={DescribeTrackedMeleePresentation()} layer={DescribeMeleeLayer()}");
        }

        public void PlayAutoAttackGhost(string actionId, long startedAtMs, Vector3 facingTargetPoint)
        {
            if (string.IsNullOrWhiteSpace(actionId))
                return;

            var request = CombatAnimationRequest.Authoritative(
                actionId,
                CombatAnimationCategory.AutoAttack,
                startedAtMs,
                CombatEventSources.AutoAttack,
                facingTargetPoint);
            CaptureSuppressedAutoAttackGhost(request);
        }

        public void BeginWorldInteractionAnimation(ActiveWorldInteraction row)
        {
            if (_isDead
                || _animator == null
                || string.IsNullOrWhiteSpace(row.AnimationProfileId)
                || !WorldInteractionAnimationProfileCatalog.TryResolve(
                    row.AnimationProfileId,
                    out WorldInteractionAnimationProfile profile))
            {
                return;
            }

            if (string.Equals(
                    _worldInteractionPresentation?.ActionInstanceId,
                    row.ActionInstanceId,
                    StringComparison.Ordinal))
            {
                return;
            }

            ClearWorldInteractionAnimation();
            EnsureOverrideController();
            if (_overrideController == null || !CanDriveAnimatorState())
                return;

            ClearCombatActionPresentation(
                captureMeleeGhost: false,
                softSpellHoldClear: false);

            long startedAtMs = row.StartedAt.MicrosecondsSinceUnixEpoch / 1000L;
            long completesAtMs = row.CompletesAt.MicrosecondsSinceUnixEpoch / 1000L;
            var active = new ActiveWorldInteractionPresentation
            {
                ActionInstanceId = row.ActionInstanceId,
                StartedAtMs = startedAtMs,
                CompletesAtMs = completesAtMs,
                Profile = profile,
                Phase = WorldInteractionPlaybackPhase.Start,
            };

            _worldInteractionPriorSlotClip =
                _overrideController[WorldInteractionSlotName];
            _worldInteractionPresentation = active;

            if (profile.FaceTarget)
            {
                ApplyWorldInteractionFacing(
                    active,
                    new Vector3(
                        row.InteractionAnchorX,
                        row.InteractionAnchorY,
                        row.InteractionAnchorZ));
            }

            long nowMs = ArenaServerClock.HasEstimate
                ? ArenaServerClock.ServerNowMs
                : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            PlayWorldInteractionAtAuthoritativePhase(nowMs);
        }

        public void EndWorldInteractionAnimation(
            string actionInstanceId,
            bool completed)
        {
            ActiveWorldInteractionPresentation? active =
                _worldInteractionPresentation;
            if (active == null
                || !string.Equals(
                    active.ActionInstanceId,
                    actionInstanceId,
                    StringComparison.Ordinal))
            {
                return;
            }

            AnimationClip? releaseClip = completed
                ? active.Profile.EndClip
                : active.Profile.CancelClip ?? active.Profile.EndClip;
            if (releaseClip == null || !CanDriveAnimatorState())
            {
                ClearWorldInteractionAnimation();
                return;
            }

            active.Phase = WorldInteractionPlaybackPhase.Release;
            PlayWorldInteractionClip(releaseClip, loop: false, normalizedTime: 0f);
            _worldInteractionReleaseAt =
                Time.time + Mathf.Max(0.01f, releaseClip.length);
        }

        private void PlayWorldInteractionAtAuthoritativePhase(long serverNowMs)
        {
            ActiveWorldInteractionPresentation? active =
                _worldInteractionPresentation;
            if (active == null)
                return;

            AnimationClip? start = active.Profile.StartClip;
            long startLengthMs = start != null
                ? Math.Max(1L, (long)Math.Round(start.length * 1000.0))
                : 0L;
            AnimationClip? loop = active.Profile.LoopClip;
            long loopLengthMs = loop != null
                ? Math.Max(1L, (long)Math.Round(loop.length * 1000.0))
                : 0L;
            WorldInteractionAnimationSample sample =
                WorldInteractionAnimationTiming.Resolve(
                    serverNowMs,
                    active.StartedAtMs,
                    active.CompletesAtMs,
                    startLengthMs,
                    loopLengthMs);
            if (sample.Phase == WorldInteractionAnimationPhase.Start
                && start != null)
            {
                active.Phase = WorldInteractionPlaybackPhase.Start;
                PlayWorldInteractionClip(
                    start,
                    loop: false,
                    sample.NormalizedTime);
                return;
            }

            if (sample.Phase == WorldInteractionAnimationPhase.Loop
                && loop != null)
            {
                active.Phase = WorldInteractionPlaybackPhase.Loop;
                PlayWorldInteractionClip(
                    loop,
                    loop: true,
                    sample.NormalizedTime);
                return;
            }
        }

        private void UpdateWorldInteractionAnimation()
        {
            ActiveWorldInteractionPresentation? active =
                _worldInteractionPresentation;
            if (active == null)
                return;

            if (active.Phase == WorldInteractionPlaybackPhase.Release)
            {
                if (Time.time >= _worldInteractionReleaseAt)
                    ClearWorldInteractionAnimation();
                return;
            }

            if (active.Phase != WorldInteractionPlaybackPhase.Start)
                return;

            AnimationClip? start = active.Profile.StartClip;
            if (start == null || active.Profile.LoopClip == null)
                return;

            long nowMs = ArenaServerClock.HasEstimate
                ? ArenaServerClock.ServerNowMs
                : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long startLengthMs =
                Math.Max(1L, (long)Math.Round(start.length * 1000.0));
            if (nowMs - active.StartedAtMs >= startLengthMs)
                PlayWorldInteractionAtAuthoritativePhase(nowMs);
        }

        private void PlayWorldInteractionClip(
            AnimationClip clip,
            bool loop,
            float normalizedTime)
        {
            if (_animator == null
                || _overrideController == null
                || !CanDriveAnimatorState())
            {
                return;
            }

            _overrideController[WorldInteractionSlotName] = clip;
            _worldInteractionAppliedClip = clip;
            _animator.SetBool(MirrorSpellActionHash, false);
            bool upperBody =
                _worldInteractionPresentation?.Profile.BodyMode
                    == InteractionAnimationBodyMode.UpperBody;
            int layerIndex = upperBody ? UpperBodyLayerIndex : SpellActionLayerIndex;
            int stateHash = upperBody
                ? (loop
                    ? UpperBodySpellCastHoldAction4StateHash
                    : UpperBodySpellAction4StateHash)
                : (loop
                    ? SpellCastHoldAction4StateHash
                    : SpellAction4StateHash);
            _animator.SetLayerWeight(layerIndex, 1f);
            _animator.Play(stateHash, layerIndex, Mathf.Clamp01(normalizedTime));
        }

        private void ApplyWorldInteractionFacing(
            ActiveWorldInteractionPresentation active,
            Vector3 target)
        {
            if (_animator == null)
                return;

            Vector3 direction = target - transform.position;
            direction.y = 0f;
            if (direction.sqrMagnitude <= 0.0001f)
                return;

            Transform facingTransform = _motionSource ?? _animator.transform;
            active.FacingTransform = facingTransform;
            active.PriorFacingLocalRotation = facingTransform.localRotation;
            Quaternion visualOffset =
                Quaternion.Inverse(transform.rotation) * facingTransform.rotation;
            Quaternion desiredActorRotation =
                Quaternion.LookRotation(direction.normalized, Vector3.up);
            facingTransform.rotation = desiredActorRotation * visualOffset;
        }

        private void ClearWorldInteractionAnimation()
        {
            ActiveWorldInteractionPresentation? active =
                _worldInteractionPresentation;
            if (active == null)
                return;

            if (active.FacingTransform != null)
                active.FacingTransform.localRotation = active.PriorFacingLocalRotation;

            if (_animator != null && CanDriveAnimatorState())
            {
                bool upperBody =
                    active.Profile.BodyMode == InteractionAnimationBodyMode.UpperBody;
                int layerIndex =
                    upperBody ? UpperBodyLayerIndex : SpellActionLayerIndex;
                _animator.Play(
                    upperBody ? UpperBodyEmptyStateHash : SpellActionEmptyStateHash,
                    layerIndex,
                    0f);
            }

            if (_overrideController != null
                && ReferenceEquals(
                    _overrideController[WorldInteractionSlotName],
                    _worldInteractionAppliedClip))
            {
                _overrideController[WorldInteractionSlotName] =
                    _worldInteractionPriorSlotClip;
            }

            _worldInteractionPresentation = null;
            _worldInteractionPriorSlotClip = null;
            _worldInteractionAppliedClip = null;
            _worldInteractionReleaseAt = 0f;
        }

        private CombatAnimationDecision DecideCombatAnimationRequest(in CombatAnimationRequest request)
        {
            // Predicted gap-close windups (feel audit F5) hold the phased slot
            // until the server's special-movement replay arrives; that replay is
            // a duplicate of the already-playing predicted start.
            if (_isLocalPlayer
                && _actionPlayback.IsPhasedMeleeActive
                && _actionPlayback.ActiveMeleePresentation.HasValue
                && CombatActionPlaybackController.IsDuplicateAuthoritativeSpecialMovementMeleeStart(
                    request.Authority,
                    request.DrivePhasesFromSpecialMovement,
                    string.Equals(
                        _actionPlayback.ActiveMeleePresentation.GetValueOrDefault().ActionId,
                        request.ActionId,
                        StringComparison.OrdinalIgnoreCase),
                    _actionPlayback.IsPhasedMeleeSpecialMovementDriven,
                    _actionPlayback.IsPhasedMeleeSpecialMovementEndRequested))
            {
                return CombatAnimationDecision.IgnoreAsDuplicate;
            }

            bool isHigherPriority = IsHigherPriorityCombatPresentationActive();
            bool isMeleeActive = IsMeleePresentationStateActive();
            bool isSpellActive = IsAnySpellPresentationStateActive();
            bool isComboFollowUp = false;
            bool isAutoAttackSequenceRestart = false;
            bool activeMeleeIsPhased = false;

            if (isMeleeActive)
            {
                ActiveMeleePresentation active = _actionPlayback.ActiveMeleePresentation.GetValueOrDefault();
                bool ownsAutoAttackSequence = request.Category == CombatAnimationCategory.AutoAttack
                    && active.Category == CombatAnimationCategory.AutoAttack
                    && active.StrikeIndex > 0
                    && _animationSet != null;
                if (ownsAutoAttackSequence)
                {
                    isComboFollowUp = _animationSet!.IsAutoAttackVisualSequenceTransition(
                        active.StrikeIndex,
                        request.ActionId);
                    isAutoAttackSequenceRestart = !isComboFollowUp
                        && _animationSet.IsAutoAttackVisualSequenceRestart(
                            active.StrikeIndex,
                            request.ActionId);
                }
                else if (request.Category != CombatAnimationCategory.AutoAttack)
                {
                    isComboFollowUp = IsComboFollowUpOfActiveMelee(request);
                }

                if (isComboFollowUp || isAutoAttackSequenceRestart)
                {
                    activeMeleeIsPhased = _actionPlayback.ActiveMeleePresentation.HasValue && _actionPlayback.ActiveMeleePresentation.Value.IsPhased;
                }
            }

            bool shouldEvaluateVisualGate =
                isSpellActive
                || (request.Category != CombatAnimationCategory.AutoAttack && isMeleeActive && !isComboFollowUp);
            bool gateEvaluated = false;
            CombatVisualInterruptDecision visualDecision = CombatVisualInterruptDecision.PreserveExistingBehavior;
            if (shouldEvaluateVisualGate)
            {
                gateEvaluated = TryDecideVisualInterruptForActive(
                    request.Category,
                    out visualDecision);
            }

            return CombatActionPlaybackController.DecideCombatAnimationRequest(
                request.Category,
                isHigherPriority,
                isSpellActive,
                isMeleeActive,
                isComboFollowUp,
                isAutoAttackSequenceRestart,
                activeMeleeIsPhased,
                gateEvaluated,
                visualDecision);
        }

        private bool IsComboFollowUpOfActiveMelee(in CombatAnimationRequest request)
        {
            if (_animationSet == null || !_actionPlayback.ActiveMeleePresentation.HasValue)
                return false;

            ActiveMeleePresentation active = _actionPlayback.ActiveMeleePresentation.GetValueOrDefault();
            if (active.StrikeIndex <= 0)
                return false;

            int incomingStrikeIndex = _animationSet.GetStrikeIndexForActionId(request.ActionId);
            if (incomingStrikeIndex <= 0)
                return false;

            return IsComboFollowUp(
                _animationSet.GetStrikeCombat(active.StrikeIndex),
                _animationSet.GetStrikeCombat(incomingStrikeIndex));
        }

        private static bool IsComboFollowUp(
            WeaponStrikeCombatAuthoring activeCombat,
            WeaponStrikeCombatAuthoring incomingCombat)
        {
            string comboFrom = incomingCombat.ComboFromOrEmpty;
            if (string.IsNullOrEmpty(comboFrom))
                return false;

            return string.Equals(
                comboFrom,
                activeCombat.AuthoredStrikeIdOrDefault,
                StringComparison.OrdinalIgnoreCase);
        }

        private bool TryDecideVisualInterruptForActive(
            CombatAnimationCategory incomingCategory,
            out CombatVisualInterruptDecision decision)
        {
            decision = CombatVisualInterruptDecision.PreserveExistingBehavior;
            if (_actionPlayback.ActiveMeleePresentation.HasValue
                && _actionPlayback.ActiveMeleePresentationEntered
                && IsMeleePresentationStateActive())
                return TryDecideVisualInterruptForActiveMelee(incomingCategory, out decision);
            if (_actionPlayback.ActiveSpellPresentation.HasValue
                && _actionPlayback.ActiveSpellPresentationEntered
                && IsActiveSpellPresentationStateActive())
                return TryDecideVisualInterruptForActiveSpell(incomingCategory, out decision);
            if (_actionPlayback.ActiveSpellCastHoldPresentation.HasValue && IsActiveSpellCastHoldStateActive())
            {
                decision = CombatVisualInterruptDecision.InterruptCurrentWithoutGhost;
                return true;
            }

            return false;
        }

        private bool TryDecideVisualInterruptForActiveMelee(
            CombatAnimationCategory incomingCategory,
            out CombatVisualInterruptDecision decision)
        {
            decision = CombatVisualInterruptDecision.PreserveExistingBehavior;

            ActiveMeleePresentation active = _actionPlayback.ActiveMeleePresentation.GetValueOrDefault();
            bool hasAnimatorTiming = TryGetActiveMeleePresentationTiming(
                active,
                out float elapsedSeconds,
                out float stateLengthSeconds,
                out _);
            if (!hasAnimatorTiming)
            {
                elapsedSeconds = 0f;
                stateLengthSeconds = 0f;
            }

            float visualInterruptibleAtSeconds = CombatActionPlaybackController.ResolvePlaybackThresholdSeconds(
                active.VisualInterruptibleAtSeconds,
                stateLengthSeconds);

            decision = CombatActionPlaybackController.DecideVisualInterrupt(
                active.Category,
                incomingCategory,
                active.IsPhased,
                elapsedSeconds,
                visualInterruptibleAtSeconds);
            return true;
        }

        private bool TryDecideVisualInterruptForActiveSpell(
            CombatAnimationCategory incomingCategory,
            out CombatVisualInterruptDecision decision)
        {
            decision = CombatVisualInterruptDecision.PreserveExistingBehavior;

            ActiveSpellPresentation active = _actionPlayback.ActiveSpellPresentation.GetValueOrDefault();
            bool hasAnimatorTiming = TryGetActiveSpellActionTiming(
                active,
                out float elapsedSeconds,
                out float stateLengthSeconds);
            if (!hasAnimatorTiming)
            {
                elapsedSeconds = 0f;
                stateLengthSeconds = 0f;
            }

            float visualInterruptibleAtSeconds = CombatActionPlaybackController.ResolvePlaybackThresholdSeconds(
                active.VisualInterruptibleAtSeconds,
                stateLengthSeconds);

            decision = CombatActionPlaybackController.DecideVisualInterrupt(
                CombatAnimationCategory.Spell,
                incomingCategory,
                activeIsPhased: false,
                elapsedSeconds,
                visualInterruptibleAtSeconds);
            return true;
        }

        private bool TryGetActiveMeleePresentationTiming(
            ActiveMeleePresentation active,
            out float elapsedSeconds,
            out float stateLengthSeconds,
            out float normalizedTime)
        {
            elapsedSeconds = 0f;
            stateLengthSeconds = 0f;
            normalizedTime = 0f;
            if (_animator == null)
                return false;

            if (active.IsPhased)
            {
                bool gotPhasedTiming =
                    TryGetPhasedMeleePresentationTiming(out elapsedSeconds, out stateLengthSeconds);
                normalizedTime = GetPhasedMeleeCurrentNormalizedTime();
                return gotPhasedTiming;
            }

            int layerIndex = MeleeAttackLayerIndex;
            AnimatorStateInfo current = _animator.GetCurrentAnimatorStateInfo(layerIndex);
            if (IsAnimatorStateForActiveMeleePresentation(current, active))
            {
                normalizedTime = Mathf.Max(0f, current.normalizedTime);
                stateLengthSeconds = ResolveActiveMeleePlayedLength(active, current.length);
                elapsedSeconds = Mathf.Max(0f, stateLengthSeconds * normalizedTime);
                return true;
            }

            if (_animator.IsInTransition(layerIndex))
            {
                AnimatorStateInfo next = _animator.GetNextAnimatorStateInfo(layerIndex);
                if (IsAnimatorStateForActiveMeleePresentation(next, active))
                {
                    normalizedTime = Mathf.Max(0f, next.normalizedTime);
                    stateLengthSeconds = ResolveActiveMeleePlayedLength(active, next.length);
                    elapsedSeconds = Mathf.Max(0f, stateLengthSeconds * normalizedTime);
                    return true;
                }
            }

            return false;
        }

        private static float ResolveActiveMeleePlayedLength(
            ActiveMeleePresentation active,
            float fallbackStateLengthSeconds)
        {
            return active.PlayedLengthSeconds > 0f
                ? active.PlayedLengthSeconds
                : Mathf.Max(0f, fallbackStateLengthSeconds);
        }

        private bool IsAnimatorStateForActiveMeleePresentation(
            AnimatorStateInfo state,
            ActiveMeleePresentation active)
        {
            int expectedStateHash = ResolveActiveMeleeStateHash(active);
            return expectedStateHash != 0 && state.shortNameHash == expectedStateHash;
        }

        private int ResolveActiveMeleeStateHash(ActiveMeleePresentation active)
        {
            if (active.IsPhased)
                return _actionPlayback.PhasedMeleeStateHash;

            if (active.StrikeIndex <= 0)
                return 0;

            return ResolveStrikeStateHash(ResolveStrikeBankSlot(active.StrikeIndex));
        }

        private void UpdateMeleeLowerBodyUnlock()
        {
            if (_animator == null || !_actionPlayback.ActiveMeleePresentation.HasValue || !_actionPlayback.ActiveMeleePresentationEntered)
                return;

            ActiveMeleePresentation active = _actionPlayback.ActiveMeleePresentation.GetValueOrDefault();
            if (active.StrikeIndex <= 0)
                return;

            if (!TryGetActiveMeleePresentationTiming(
                    active,
                    out float elapsedSeconds,
                    out float stateLengthSeconds,
                    out float normalizedTime))
                return;

            float lowerBodyUnlockAtSeconds = CombatActionPlaybackController.ResolvePlaybackThresholdSeconds(
                active.LowerBodyUnlockAtSeconds,
                stateLengthSeconds);

            if (!_actionPlayback.IsMeleeLowerBodyUnlocked)
            {
                bool isArrivalDrivenPhasedMelee = active.IsPhased
                    && _actionPlayback.IsPhasedMeleeSpecialMovementArrivalDriven;
                if (isArrivalDrivenPhasedMelee)
                {
                    if (!CombatActionPlaybackController.CanReleaseMeleeLowerBody(
                            activeIsPhased: true,
                            phasedMeleeArrivalDriven: true,
                            phase: _actionPlayback.PhasedMeleePhase,
                            phaseNormalizedTime: normalizedTime,
                            authoredPhaseUnlockNormalizedTime: ResolveCurrentPhasedMeleeLowerBodyUnlockNormalizedTime()))
                    {
                        return;
                    }
                }
                else if (elapsedSeconds < lowerBodyUnlockAtSeconds)
                    return;
                if (!ShouldReleaseLowerBodyToLocomotion())
                    return;

                if (!TryStartMeleeUpperBodyRecovery(active, normalizedTime))
                    return;

                _actionPlayback.MarkMeleeLowerBodyUnlocked(
                    Time.time,
                    active.LowerBodyBlendOutSeconds,
                    _animator.GetLayerWeight(MeleeAttackLayerIndex));
            }

            float nextWeight = _actionPlayback.ResolveMeleeLowerBodyLayerWeight(Time.time);
            _animator.SetLayerWeight(MeleeAttackLayerIndex, nextWeight);
        }

        private bool ShouldReleaseLowerBodyToLocomotion()
        {
            return _latestLocomotionRawMagnitude >= StopTriggerThreshold;
        }

        private float ResolveCurrentPhasedMeleeLowerBodyUnlockNormalizedTime()
        {
            AnimationClip? clip = GetCurrentPhasedMeleeClip();
            return clip != null
                && CombatAnimationEvents.TryGetEventNormalizedTime(
                    clip,
                    CombatAnimationEvents.OnLowerBodyUnlock,
                    out float normalizedTime)
                ? normalizedTime
                : 1f;
        }

        private bool TryStartMeleeUpperBodyRecovery(ActiveMeleePresentation active, float normalizedTime)
        {
            if (_animator == null || _overrideController == null || _animationSet == null)
                return false;

            AnimationClip? desiredClip = active.IsPhased
                ? GetCurrentPhasedMeleeClip()
                : _animationSet.GetStrikeClip(active.StrikeIndex);
            if (desiredClip == null)
                return false;

            _overrideController[UpperBodyRecoverySlotName] = desiredClip;
            PlayUpperBodyState(UpperBodyRecoveryAction1StateHash, normalizedTime);
            if (active.IsPhased)
            {
                _actionPlayback.EnterPhasedMeleeUpperBodyMode();
                _actionPlayback.ResetPhasedMeleeSegmentEntry();
                _phasedMeleeSegmentDispatchedFrame = Time.frameCount;
            }
            return true;
        }

        private void ResetMeleeLowerBodyUnlockState(bool resetLayerWeight, bool clearUpperBodyRecovery)
        {
            bool shouldClearUpperBodyRecovery = _actionPlayback.ResetMeleeLowerBodyUnlock(clearUpperBodyRecovery);

            if (_animator == null)
                return;

            if (resetLayerWeight)
                _animator.SetLayerWeight(MeleeAttackLayerIndex, 1f);
            if (shouldClearUpperBodyRecovery)
                PlayUpperBodyState(UpperBodyEmptyStateHash, 0f);
        }

        private void SetActiveSpellPresentation(
            string actionId,
            int bankSlot,
            WeaponSpellAnimationEntry spellEntry)
        {
            _actionPlayback.SetActiveSpellPresentation(
                actionId,
                bankSlot,
                spellEntry);
            _activeSpellPresentationDispatchedFrame = Time.frameCount;
            ResetSpellLowerBodyUnlockState(resetLayerWeight: true, clearUpperBodySpell: false);
        }

        private void UpdateSpellLowerBodyUnlock()
        {
            if (_animator == null || !_actionPlayback.ActiveSpellPresentation.HasValue || !_actionPlayback.ActiveSpellPresentationEntered)
                return;

            ActiveSpellPresentation active = _actionPlayback.ActiveSpellPresentation.GetValueOrDefault();
            if (!_actionPlayback.IsSpellLowerBodyUnlocked)
            {
                if (!TryGetActiveSpellActionTiming(active, out float elapsedSeconds, out float stateLengthSeconds))
                    return;

                float lowerBodyUnlockAtSeconds = CombatActionPlaybackController.ResolvePlaybackThresholdSeconds(
                    active.LowerBodyUnlockAtSeconds,
                    stateLengthSeconds);
                if (elapsedSeconds < lowerBodyUnlockAtSeconds)
                    return;
                if (!ShouldReleaseLowerBodyToLocomotion())
                    return;

                float normalizedTime = stateLengthSeconds > 0f
                    ? Mathf.Clamp01(elapsedSeconds / stateLengthSeconds)
                    : 0f;
                PlayUpperBodyState(ResolveUpperBodySpellStateHash(active.BankSlot), normalizedTime);
                _actionPlayback.MarkSpellLowerBodyUnlocked(
                    Time.time,
                    active.LowerBodyBlendOutSeconds,
                    _animator.GetLayerWeight(SpellActionLayerIndex));
            }

            float nextWeight = _actionPlayback.ResolveSpellLowerBodyLayerWeight(Time.time);
            _animator.SetLayerWeight(SpellActionLayerIndex, nextWeight);
        }

        private bool TryGetActiveSpellActionTiming(
            ActiveSpellPresentation active,
            out float elapsedSeconds,
            out float stateLengthSeconds)
        {
            elapsedSeconds = 0f;
            stateLengthSeconds = 0f;
            if (_animator == null)
                return false;

            int expectedStateHash = ResolveSpellActionStateHash(active.BankSlot);
            AnimatorStateInfo current = _animator.GetCurrentAnimatorStateInfo(SpellActionLayerIndex);
            if (current.shortNameHash == expectedStateHash)
            {
                stateLengthSeconds = Mathf.Max(0f, current.length);
                elapsedSeconds = Mathf.Max(0f, stateLengthSeconds * current.normalizedTime);
                return true;
            }

            if (_animator.IsInTransition(SpellActionLayerIndex))
            {
                AnimatorStateInfo next = _animator.GetNextAnimatorStateInfo(SpellActionLayerIndex);
                if (next.shortNameHash == expectedStateHash)
                {
                    stateLengthSeconds = Mathf.Max(0f, next.length);
                    elapsedSeconds = Mathf.Max(0f, stateLengthSeconds * next.normalizedTime);
                    return true;
                }
            }

            if (_actionPlayback.IsSpellLowerBodyUnlocked)
            {
                int upperBodyStateHash = ResolveUpperBodySpellStateHash(active.BankSlot);
                AnimatorStateInfo upperCurrent = _animator.GetCurrentAnimatorStateInfo(UpperBodyLayerIndex);
                if (upperCurrent.shortNameHash == upperBodyStateHash)
                {
                    stateLengthSeconds = Mathf.Max(0f, upperCurrent.length);
                    elapsedSeconds = Mathf.Max(0f, stateLengthSeconds * upperCurrent.normalizedTime);
                    return true;
                }

                if (_animator.IsInTransition(UpperBodyLayerIndex))
                {
                    AnimatorStateInfo upperNext = _animator.GetNextAnimatorStateInfo(UpperBodyLayerIndex);
                    if (upperNext.shortNameHash == upperBodyStateHash)
                    {
                        stateLengthSeconds = Mathf.Max(0f, upperNext.length);
                        elapsedSeconds = Mathf.Max(0f, stateLengthSeconds * upperNext.normalizedTime);
                        return true;
                    }
                }
            }

            return false;
        }

        private bool IsActiveSpellPresentationStateActive()
        {
            if (_animator == null || !_actionPlayback.ActiveSpellPresentation.HasValue)
                return false;

            ActiveSpellPresentation active = _actionPlayback.ActiveSpellPresentation.Value;
            int fullBodyStateHash = ResolveSpellActionStateHash(active.BankSlot);
            int upperBodyStateHash = ResolveUpperBodySpellStateHash(active.BankSlot);

            if (_animator.GetCurrentAnimatorStateInfo(SpellActionLayerIndex).shortNameHash == fullBodyStateHash)
                return true;
            if (_animator.IsInTransition(SpellActionLayerIndex)
                && _animator.GetNextAnimatorStateInfo(SpellActionLayerIndex).shortNameHash == fullBodyStateHash)
                return true;
            if (_actionPlayback.IsSpellLowerBodyUnlocked
                && _animator.GetCurrentAnimatorStateInfo(UpperBodyLayerIndex).shortNameHash == upperBodyStateHash)
                return true;
            if (_actionPlayback.IsSpellLowerBodyUnlocked
                && _animator.IsInTransition(UpperBodyLayerIndex)
                && _animator.GetNextAnimatorStateInfo(UpperBodyLayerIndex).shortNameHash == upperBodyStateHash)
                return true;

            return false;
        }

        private bool HasActiveMeleePresentationEnteredExpectedState()
        {
            if (!_actionPlayback.ActiveMeleePresentation.HasValue)
                return false;

            ActiveMeleePresentation active =
                _actionPlayback.ActiveMeleePresentation.GetValueOrDefault();
            return HasEnteredExpectedStateOnLayer(
                _activeMeleePresentationDispatchedFrame,
                MeleeAttackLayerIndex,
                ResolveActiveMeleeStateHash(active));
        }

        private bool HasActiveSpellPresentationEnteredExpectedState()
        {
            if (!_actionPlayback.ActiveSpellPresentation.HasValue)
                return false;

            ActiveSpellPresentation active =
                _actionPlayback.ActiveSpellPresentation.GetValueOrDefault();
            return HasEnteredExpectedStateOnLayer(
                _activeSpellPresentationDispatchedFrame,
                SpellActionLayerIndex,
                ResolveSpellActionStateHash(active.BankSlot));
        }

        private bool HasEnteredExpectedStateOnLayer(
            int dispatchedFrame,
            int layerIndex,
            int expectedStateHash)
        {
            if (_animator == null)
                return false;

            AnimatorStateInfo current = _animator.GetCurrentAnimatorStateInfo(layerIndex);
            bool isInTransition = _animator.IsInTransition(layerIndex);
            int nextStateHash = isInTransition
                ? _animator.GetNextAnimatorStateInfo(layerIndex).shortNameHash
                : 0;
            return CombatActionPlaybackController.HasEnteredExpectedAnimatorState(
                dispatchedFrame,
                Time.frameCount,
                expectedStateHash,
                current.shortNameHash,
                isInTransition,
                nextStateHash);
        }

        private bool IsAnySpellPresentationStateActive()
        {
            return IsActiveSpellPresentationStateActive() || IsActiveSpellCastHoldStateActive();
        }

        private bool IsActiveSpellCastHoldStateActive()
        {
            if (_animator == null || !_actionPlayback.ActiveSpellCastHoldPresentation.HasValue)
                return false;

            ActiveSpellCastHoldPresentation active = _actionPlayback.ActiveSpellCastHoldPresentation.Value;
            int enterStateHash = ResolveSpellCastHoldStateHash(active.PlaybackLayer, active.EnterBankSlot);
            int idleStateHash = ResolveSpellCastHoldStateHash(active.PlaybackLayer, active.IdleBankSlot);
            int layerIndex = ResolveSpellCastHoldLayerIndex(active.PlaybackLayer);

            AnimatorStateInfo current = _animator.GetCurrentAnimatorStateInfo(layerIndex);
            if (current.shortNameHash == enterStateHash || current.shortNameHash == idleStateHash)
                return true;

            if (_animator.IsInTransition(layerIndex))
            {
                AnimatorStateInfo next = _animator.GetNextAnimatorStateInfo(layerIndex);
                if (next.shortNameHash == enterStateHash || next.shortNameHash == idleStateHash)
                    return true;
            }

            return false;
        }

        private void ClearActiveSpellPresentation(bool resetLayerWeight, bool clearUpperBodySpell)
        {
            ResetPendingSpellActionTriggers();
            _actionPlayback.ClearActiveSpellPresentation();
            _activeSpellPresentationDispatchedFrame = -1;
            ResetSpellLowerBodyUnlockState(resetLayerWeight, clearUpperBodySpell);
            if (clearUpperBodySpell)
            {
                ClearLeftGestureSpellPresentation();
                ClearRightGestureSpellPresentation();
            }
        }

        private void ClearActiveSpellCastHoldPresentation(
            bool clearAnimatorState,
            bool softFullBodyClear = false)
        {
            // Capture the layer/exit settings before nulling so the fade-out targets the
            // layer the hold actually rendered on (masked holds live on UpperBody, not
            // SpellAction) using the spell's authored exit timing.
            ActiveSpellCastHoldPresentation? clearedPresentation = _actionPlayback.ActiveSpellCastHoldPresentation;
            bool hadActivePresentation = _actionPlayback.ClearActiveSpellCastHoldPresentation();
            if (_animator == null || !clearAnimatorState)
                return;

            if (softFullBodyClear && hadActivePresentation && clearedPresentation.HasValue)
            {
                // Fade the hold layer's weight from 1 to 0 over the exit duration so the
                // held pose blends back to the base pose instead of snapping to animator
                // default values (which is what CrossFading state-to-Empty produces when
                // the destination state has no motion clip).
                ActiveSpellCastHoldPresentation cleared = clearedPresentation.Value;
                int fadeLayerIndex = ResolveSpellCastHoldLayerIndex(cleared.PlaybackLayer);
                _actionPlayback.StartSpellCastHoldFadeOut(
                    Time.time,
                    cleared.ExitBlendOutSeconds,
                    cleared.ExitDelaySeconds,
                    fadeLayerIndex);
                // Clear the unused gesture layer. Each clear refuses to stomp the layer the fade
                // now owns, so a masked gesture hold blends out instead of snapping.
                ClearLeftGestureSpellPresentation();
                ClearRightGestureSpellPresentation();
                return;
            }

            _actionPlayback.ResetSpellCastHoldFadeOut();
            _animator.SetLayerWeight(SpellActionLayerIndex, 1f);
            _animator.SetLayerWeight(UpperBodyLayerIndex, 1f);
            _animator.Play(SpellActionEmptyStateHash, SpellActionLayerIndex, 0f);
            PlayUpperBodyState(UpperBodyEmptyStateHash, 0f);
            ClearLeftGestureSpellPresentation();
            ClearRightGestureSpellPresentation();
        }

        private void UpdateSpellCastHoldFadeOut()
        {
            if (_animator == null || !_actionPlayback.IsSpellCastHoldFadeOutActive)
                return;

            int layerIndex = _actionPlayback.SpellCastHoldFadeOutLayerIndex;
            float weight = _actionPlayback.ResolveSpellCastHoldFadeOutLayerWeight(Time.time);
            _animator.SetLayerWeight(layerIndex, weight);
            if (weight > 0f)
                return;

            // Fade complete: park the layer on Empty and restore full weight for reuse.
            // Every hold-capable layer uses a state literally named "Empty".
            _animator.Play(UpperBodyEmptyStateHash, layerIndex, 0f);
            _animator.SetLayerWeight(layerIndex, 1f);
            _actionPlayback.ResetSpellCastHoldFadeOut();
        }

        private void ResetSpellLowerBodyUnlockState(bool resetLayerWeight, bool clearUpperBodySpell)
        {
            bool shouldClearUpperBodySpell = _actionPlayback.ResetSpellLowerBodyUnlock(clearUpperBodySpell);

            if (_animator == null)
                return;

            if (resetLayerWeight)
                _animator.SetLayerWeight(SpellActionLayerIndex, 1f);
            if (shouldClearUpperBodySpell)
                PlayUpperBodyState(UpperBodyEmptyStateHash, 0f);
        }

        private void PreemptLowerPriorityPresentationFor(bool captureGhost)
        {
            ClearCombatActionPresentation(captureGhost, softSpellHoldClear: true);
        }

        private void CaptureSuppressedAutoAttackGhost(in CombatAnimationRequest request)
        {
            if (!CombatActionPlaybackController.CanCaptureSuppressedAutoAttackGhost(
                    request.Category,
                    request.HasFacingTargetPoint,
                    out _))
            {
                return;
            }

            if (_animationSet != null && _animatedAutoAttackGhostLayer != null)
            {
                SyncAnimatedAutoAttackGhostVisuals();

                int strikeIndex = _animationSet.GetStrikeIndexForActionId(request.ActionId);
                if (strikeIndex > 0)
                {
                    int bankSlot = ResolveStrikeBankSlot(strikeIndex);
                    AnimationClip? strikeClip = _animationSet.GetStrikeClip(strikeIndex);
                    bool canUseControllerDefaultClip = strikeClip == null
                        && strikeIndex <= CombatAnimationSet.AnimatorStrikeBankCount;
                    if ((strikeClip != null || canUseControllerDefaultClip)
                        && _animatedAutoAttackGhostLayer.PlayStrikeGhost(bankSlot, strikeClip, request.FacingTargetPoint))
                    {
                        return;
                    }
                }
            }

            _meleeGhostLayer?.CaptureFrozenPoseFacing(request.FacingTargetPoint);
        }

        private void SyncAnimatedAutoAttackGhostVisuals()
        {
            if (_animatedAutoAttackGhostLayer == null)
                return;

            if (_weaponAttachments == null)
                _weaponAttachments = GetComponent<WeaponAttachmentController>();

            int visualVersion = _weaponAttachments != null
                ? _weaponAttachments.VisualVersion
                : 0;
            if (visualVersion == _animatedAutoAttackGhostVisualVersion)
                return;

            _animatedAutoAttackGhostLayer.InvalidateVisualClone();
            _lingeringShadeGhostLayer?.InvalidateVisualClone();
            _animatedAutoAttackGhostVisualVersion = visualVersion;
        }

        public void SetLingeringShadeState(LingeringShadeState row)
        {
            SyncAnimatedAutoAttackGhostVisuals();
            _lingeringShadeGhostLayer?.Show(row);
        }

        public void ClearLingeringShadeState()
        {
            _lingeringShadeGhostLayer?.Clear();
        }

        private void PlaySpellAnimation(in CombatAnimationRequest request)
        {
            switch (request.SpellPhase)
            {
                case CombatSpellAnimationPhase.HoldStart:
                    PlaySpellCastHold(request);
                    return;
                case CombatSpellAnimationPhase.Cancel:
                    if (_weaponAttachments == null)
                        _weaponAttachments = GetComponent<WeaponAttachmentController>();
                    _weaponAttachments?.ReleaseTemporaryAnimatedProp(request.ActionId);
                    if (TryPlaySpellCastHoldExit(request))
                        return;
                    // The preempt path (RequestCombatAnimation -> InterruptWithoutGhost)
                    // already started the smooth hold-exit fade before this ran. Don't
                    // hard-clear over it — that would snap the layer to Empty instantly.
                    if (_actionPlayback.IsSpellCastHoldFadeOutActive
                        && !_actionPlayback.ActiveSpellCastHoldPresentation.HasValue)
                        return;
                    // A natural channel/hold end (button release, no preempt) reaches here with the
                    // hold still active. Exit through the same smooth blend-out the preempt path uses
                    // (softFullBodyClear -> StartSpellCastHoldFadeOut) instead of snapping to Empty.
                    ClearActiveSpellCastHoldPresentation(clearAnimatorState: true, softFullBodyClear: true);
                    return;
                case CombatSpellAnimationPhase.Release:
                default:
                    // The preempt path may have already cleared ActiveSpellCastHoldPresentation
                    // and started a soft fade-out before this switch ran. Treat an active fade
                    // as evidence of a hold-to-release transition so the overlay branch does not
                    // snap the SpellAction layer to Empty mid-fade.
                    bool releasedFromCastHold = _actionPlayback.ActiveSpellCastHoldPresentation.HasValue
                        || _actionPlayback.IsSpellCastHoldFadeOutActive;
                    PlaySpellReleaseAnimation(request, preserveFullBodyHoldBlendOut: releasedFromCastHold);
                    return;
            }
        }

        private void PlaySpellReleaseAnimation(
            in CombatAnimationRequest request,
            bool preserveFullBodyHoldBlendOut)
        {
            PlaySpellAnimation(request, preserveFullBodyHoldBlendOut);
        }

        private bool TryPlaySpellCastHoldExit(in CombatAnimationRequest request)
        {
            if (_animator == null || _overrideController == null || _animationSet == null)
                return false;

            _pendingSpellHoldPulse = null;

            if (!SpellCastAnimationResolver.TryResolve(
                    _animationSet,
                    request.ActionId,
                    out WeaponSpellAnimationEntry spellEntry)
                || !spellEntry.CanPlayRequestedHold
                || !_animationSet.TryGetSpellCastHoldProfile(
                    request.ActionId,
                    out SpellCastHoldProfile holdProfile)
                || holdProfile.exit == null)
            {
                return false;
            }

            AnimationClip exitClip = holdProfile.exit;
            int bankSlot = ResolveNextSpellBankSlot();
            if (!_actionPlayback.TryBindSpellBankClip(
                    _overrideController,
                    bankSlot,
                    exitClip))
            {
                return false;
            }

            // The hold has already been softly preempted before Cancel is dispatched. Reuse the
            // normal one-shot playback path for the authored recovery, but do not let a channel
            // exit masquerade as a gameplay release or restart a temporary spell prop.
            spellEntry.clip = exitClip;
            spellEntry.presentationMode = SpellAnimationPresentationMode.ReleaseOnly;
            spellEntry.playbackLayer = holdProfile.playbackLayer;
            spellEntry.animatedProp = default;
            PlayResolvedSpellAnimation(
                request.ActionId,
                bankSlot,
                spellEntry,
                exitClip,
                normalizedStart: 0f,
                confirmedInstant: false,
                preserveFullBodyHoldBlendOut: _actionPlayback.IsSpellCastHoldFadeOutActive);
            return true;
        }

        private bool TryPlaySpellCastHoldPulse(in CombatAnimationRequest request)
        {
            if (_animator == null || _overrideController == null || _animationSet == null)
                return false;

            if (_pendingSpellHoldPulse != null
                && string.Equals(
                    _pendingSpellHoldPulse.ActionId,
                    request.ActionId,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (_actionPlayback.ActiveSpellCastHoldPresentation is not { } activeHold
                || !string.Equals(
                    activeHold.ActionId,
                    request.ActionId,
                    StringComparison.OrdinalIgnoreCase)
                || !SpellCastAnimationResolver.TryResolve(
                    _animationSet,
                    request.ActionId,
                    out WeaponSpellAnimationEntry spellEntry)
                || !spellEntry.PlaysHoldPulsePresentation
                || spellEntry.ResolveClip() is not { } attackClip
                || spellEntry.returnToHold == null)
            {
                return false;
            }

            int bankSlot = ResolveNextSpellBankSlot();
            if (!_actionPlayback.TryBindSpellBankClip(
                    _overrideController,
                    bankSlot,
                    attackClip))
            {
                return false;
            }

            PlayResolvedSpellAnimation(
                request.ActionId,
                bankSlot,
                spellEntry,
                attackClip,
                normalizedStart: 0f,
                confirmedInstant: false,
                preserveFullBodyHoldBlendOut: false);
            _pendingSpellHoldPulse = new PendingSpellHoldPulse
            {
                ActionId = request.ActionId,
                Entry = spellEntry,
                ReturnToHold = spellEntry.returnToHold,
                Phase = SpellHoldPulsePhase.Attack,
                AdvanceAtSeconds = Time.time + Mathf.Max(
                    0.01f,
                    attackClip.length * SpellHoldPulseAdvanceNormalizedTime),
            };
            return true;
        }

        private void UpdateSpellCastHoldPulse()
        {
            PendingSpellHoldPulse? pending = _pendingSpellHoldPulse;
            if (pending == null || Time.time < pending.AdvanceAtSeconds)
                return;

            if (_animator == null
                || _overrideController == null
                || _actionPlayback.ActiveSpellCastHoldPresentation is not { } activeHold
                || !string.Equals(
                    activeHold.ActionId,
                    pending.ActionId,
                    StringComparison.OrdinalIgnoreCase))
            {
                _pendingSpellHoldPulse = null;
                return;
            }

            if (pending.Phase == SpellHoldPulsePhase.Attack)
            {
                int bankSlot = ResolveNextSpellBankSlot();
                if (!_actionPlayback.TryBindSpellBankClip(
                        _overrideController,
                        bankSlot,
                        pending.ReturnToHold))
                {
                    _pendingSpellHoldPulse = null;
                    return;
                }

                WeaponSpellAnimationEntry returnEntry = pending.Entry;
                returnEntry.clip = pending.ReturnToHold;
                returnEntry.returnToHold = null;
                returnEntry.animatedProp = default;
                PlayResolvedSpellAnimation(
                    pending.ActionId,
                    bankSlot,
                    returnEntry,
                    pending.ReturnToHold,
                    normalizedStart: 0f,
                    confirmedInstant: false,
                    preserveFullBodyHoldBlendOut: false);
                pending.Phase = SpellHoldPulsePhase.ReturnToHold;
                pending.AdvanceAtSeconds = Time.time + Mathf.Max(
                    0.01f,
                    pending.ReturnToHold.length * SpellHoldPulseAdvanceNormalizedTime);
                return;
            }

            ClearActiveSpellPresentation(resetLayerWeight: true, clearUpperBodySpell: false);
            _actionPlayback.ClearActiveOverlaySpellPresentation();
            _animator.Play(SpellActionEmptyStateHash, SpellActionLayerIndex, 0f);
            if (!_animationSet!.TryGetSpellCastHoldProfile(
                    pending.ActionId,
                    out SpellCastHoldProfile holdProfile)
                || holdProfile.IdleOrEnter == null
                || !_actionPlayback.TryBindSpellBankClip(
                    _overrideController,
                    activeHold.IdleBankSlot,
                    holdProfile.IdleOrEnter))
            {
                _pendingSpellHoldPulse = null;
                ClearActiveSpellCastHoldPresentation(
                    clearAnimatorState: true,
                    softFullBodyClear: true);
                return;
            }
            PlaySpellCastHoldState(
                activeHold.PlaybackLayer,
                activeHold.IdleBankSlot,
                0f,
                SpellCastHoldPhaseCrossFadeDurationSeconds);
            _pendingSpellHoldPulse = null;
        }

        private void PlaySpellCastHold(in CombatAnimationRequest request)
        {
            if (_animator == null || _overrideController == null || _animationSet == null)
                return;

            _pendingSpellHoldPulse = null;

            bool hasResolvedEntry = SpellCastAnimationResolver.TryResolve(
                _animationSet,
                request.ActionId,
                out WeaponSpellAnimationEntry entry);
            if (SpellCastAnimationResolver.IsExplicitlyNoAnimation(request.ActionId)
                || (hasResolvedEntry && !entry.CanPlayRequestedHold))
            {
                return;
            }

            if (!_animationSet.TryGetSpellCastHoldProfile(request.ActionId, out SpellCastHoldProfile holdProfile))
            {
                Debug.LogWarning(
                    $"[PlayerAnimator] Spell '{request.ActionId}' requested a hold, but animation set '{_animationSet.name}' has no playable spell cast hold profile.");
                return;
            }

            AnimationClip? enterClip = holdProfile.EnterOrIdle;
            AnimationClip? idleClip = holdProfile.IdleOrEnter;
            if (enterClip == null || idleClip == null)
                return;

            _animator.SetBool(
                MirrorSpellActionHash,
                hasResolvedEntry && entry.mirrorPresentation);

            bool needsCombatVisualStance = !_inCombat || (_weaponAttachments != null && !_weaponAttachments.IsInCombatVisual);
            if (needsCombatVisualStance)
            {
                EnterCombatImmediate();
            }

            int enterBankSlot = ResolveNextSpellBankSlot();
            int idleBankSlot = ResolveNextSpellBankSlot();
            if (!_actionPlayback.TryBindSpellBankClip(_overrideController, enterBankSlot, enterClip)
                || !_actionPlayback.TryBindSpellBankClip(_overrideController, idleBankSlot, idleClip))
            {
                return;
            }

            ClearActiveSpellPresentation(resetLayerWeight: true, clearUpperBodySpell: true);
            ClearActiveSpellCastHoldPresentation(clearAnimatorState: true);
            ClearMeleePresentationForUpperBodySpell();

            float enterCompleteNt = holdProfile.ResolveEnterCompleteNormalizedTime(SpellCastHoldEnterToIdleNormalizedTime);
            float exitBlendOut = holdProfile.ResolveExitBlendOutSeconds(SpellCastHoldExitCrossFadeDurationSeconds);
            float exitDelay = holdProfile.ResolveExitDelaySeconds(SpellCastHoldExitDelaySeconds);
            bool usesUpperBodyWhileMoving =
                holdProfile.playbackLayer == SpellPlaybackLayer.UpperBodyWhileMoving;
            SpellPlaybackLayer activePlaybackLayer = ResolveSpellCastHoldPlaybackLayer(
                holdProfile.playbackLayer,
                _latestLocomotionRawMagnitude);

            // Starting a fresh hold cancels any exit fade left running from a prior one.
            _actionPlayback.ResetSpellCastHoldFadeOut();
            _actionPlayback.SetActiveSpellCastHoldPresentation(
                request.ActionId,
                enterBankSlot,
                idleBankSlot,
                activePlaybackLayer,
                usesUpperBodyWhileMoving,
                enterCompleteNt,
                exitBlendOut,
                exitDelay);

            PlaySpellCastHoldState(
                activePlaybackLayer,
                enterBankSlot,
                0f,
                SpellCastHoldEnterCrossFadeDurationSeconds);
        }

        private void PlaySpellCastHoldState(
            SpellPlaybackLayer playbackLayer,
            int bankSlot,
            float normalizedTime,
            float transitionDurationSeconds)
        {
            // Detection (UpdateSpellCastHoldPlayback / IsActiveSpellCastHoldStateActive)
            // resolves the same way, so keep playback aligned by sharing the resolver.
            int stateHash = ResolveSpellCastHoldStateHash(playbackLayer, bankSlot);

            switch (playbackLayer)
            {
                case SpellPlaybackLayer.LeftGesture:
                    PlayUpperBodyState(UpperBodyEmptyStateHash, 0f);
                    ClearRightGestureSpellPresentation();
                    PlayLeftGestureState(stateHash, normalizedTime);
                    break;
                case SpellPlaybackLayer.RightGesture:
                    PlayUpperBodyState(UpperBodyEmptyStateHash, 0f);
                    ClearLeftGestureSpellPresentation();
                    PlayRightGestureState(stateHash, normalizedTime);
                    break;
                case SpellPlaybackLayer.UpperBody:
                case SpellPlaybackLayer.UpperBodyWhileMoving:
                    ClearLeftGestureSpellPresentation();
                    ClearRightGestureSpellPresentation();
                    // Restore full weight in case a prior hold's exit fade left the
                    // UpperBody layer partway blended out.
                    _animator!.SetLayerWeight(UpperBodyLayerIndex, 1f);
                    PlayUpperBodyState(stateHash, normalizedTime);
                    break;
                case SpellPlaybackLayer.FullBody:
                default:
                    ClearLeftGestureSpellPresentation();
                    ClearRightGestureSpellPresentation();
                    _animator!.SetLayerWeight(SpellActionLayerIndex, 1f);
                    int fullPathHash = ResolveSpellCastHoldActionFullPathHash(bankSlot);
                    _animator.CrossFadeInFixedTime(
                        fullPathHash,
                        transitionDurationSeconds,
                        SpellActionLayerIndex,
                        Mathf.Clamp01(normalizedTime));
                    break;
            }
        }

        private static int ResolveSpellCastHoldLayerIndex(SpellPlaybackLayer playbackLayer)
        {
            return playbackLayer switch
            {
                SpellPlaybackLayer.LeftGesture => LeftGestureLayerIndex,
                SpellPlaybackLayer.RightGesture => RightGestureLayerIndex,
                SpellPlaybackLayer.UpperBody => UpperBodyLayerIndex,
                SpellPlaybackLayer.UpperBodyWhileMoving => UpperBodyLayerIndex,
                _ => SpellActionLayerIndex,
            };
        }

        private static int ResolveSpellCastHoldStateHash(SpellPlaybackLayer playbackLayer, int bankSlot)
        {
            return playbackLayer switch
            {
                SpellPlaybackLayer.LeftGesture => ResolveLeftGestureSpellCastHoldStateHash(bankSlot),
                SpellPlaybackLayer.RightGesture => ResolveRightGestureSpellCastHoldStateHash(bankSlot),
                SpellPlaybackLayer.UpperBody => ResolveUpperBodySpellCastHoldStateHash(bankSlot),
                SpellPlaybackLayer.UpperBodyWhileMoving => ResolveUpperBodySpellCastHoldStateHash(bankSlot),
                _ => ResolveSpellCastHoldActionStateHash(bankSlot),
            };
        }

        private void UpdateSpellCastHoldPlayback()
        {
            if (_animator == null || !_actionPlayback.ActiveSpellCastHoldPresentation.HasValue)
                return;

            ActiveSpellCastHoldPresentation active = _actionPlayback.ActiveSpellCastHoldPresentation.Value;
            if (TryUpdateSpellCastHoldLocomotionLayer(active))
                active = _actionPlayback.ActiveSpellCastHoldPresentation!.Value;
            if (_actionPlayback.SpellCastHoldPhase != SpellCastHoldPlaybackPhase.Enter)
            {
                return;
            }

            int layerIndex = ResolveSpellCastHoldLayerIndex(active.PlaybackLayer);
            int enterStateHash = ResolveSpellCastHoldStateHash(active.PlaybackLayer, active.EnterBankSlot);
            AnimatorStateInfo current = _animator.GetCurrentAnimatorStateInfo(layerIndex);
            bool inEnter = current.shortNameHash == enterStateHash;
            if (!inEnter && _animator.IsInTransition(layerIndex))
            {
                AnimatorStateInfo next = _animator.GetNextAnimatorStateInfo(layerIndex);
                inEnter = next.shortNameHash == enterStateHash;
                current = next;
            }

            if (!inEnter || current.normalizedTime < active.EnterCompleteNormalizedTime)
                return;

            _actionPlayback.SetSpellCastHoldPhase(SpellCastHoldPlaybackPhase.Idle);
            PlaySpellCastHoldState(
                active.PlaybackLayer,
                active.IdleBankSlot,
                0f,
                SpellCastHoldPhaseCrossFadeDurationSeconds);
        }

        private bool TryUpdateSpellCastHoldLocomotionLayer(
            ActiveSpellCastHoldPresentation active)
        {
            if (_animator == null
                || !active.UsesUpperBodyWhileMoving
                || _pendingSpellHoldPulse != null)
            {
                return false;
            }

            SpellPlaybackLayer desiredLayer = ResolveSpellCastHoldPlaybackLayer(
                SpellPlaybackLayer.UpperBodyWhileMoving,
                _latestLocomotionRawMagnitude);
            if (desiredLayer == active.PlaybackLayer)
                return false;

            int currentLayerIndex = ResolveSpellCastHoldLayerIndex(active.PlaybackLayer);
            int currentBankSlot = _actionPlayback.SpellCastHoldPhase == SpellCastHoldPlaybackPhase.Idle
                ? active.IdleBankSlot
                : active.EnterBankSlot;
            int currentStateHash = ResolveSpellCastHoldStateHash(
                active.PlaybackLayer,
                currentBankSlot);
            AnimatorStateInfo state = _animator.GetCurrentAnimatorStateInfo(currentLayerIndex);
            if (state.shortNameHash != currentStateHash)
            {
                if (!_animator.IsInTransition(currentLayerIndex))
                    return false;

                AnimatorStateInfo next = _animator.GetNextAnimatorStateInfo(currentLayerIndex);
                if (next.shortNameHash != currentStateHash)
                    return false;
                state = next;
            }

            float normalizedTime = _actionPlayback.SpellCastHoldPhase == SpellCastHoldPlaybackPhase.Enter
                ? Mathf.Clamp01(state.normalizedTime)
                : Mathf.Repeat(state.normalizedTime, 1f);
            if (active.PlaybackLayer == SpellPlaybackLayer.FullBody)
                _animator.Play(SpellActionEmptyStateHash, SpellActionLayerIndex, 0f);
            else
                PlayUpperBodyState(UpperBodyEmptyStateHash, 0f);

            _actionPlayback.SetSpellCastHoldPlaybackLayer(desiredLayer);
            PlaySpellCastHoldState(
                desiredLayer,
                currentBankSlot,
                normalizedTime,
                SpellCastHoldPhaseCrossFadeDurationSeconds);
            return true;
        }

        internal static SpellPlaybackLayer ResolveSpellCastHoldPlaybackLayer(
            SpellPlaybackLayer requestedLayer,
            float locomotionRawMagnitude)
        {
            if (requestedLayer != SpellPlaybackLayer.UpperBodyWhileMoving)
                return requestedLayer;

            return locomotionRawMagnitude >= StopTriggerThreshold
                ? SpellPlaybackLayer.UpperBody
                : SpellPlaybackLayer.FullBody;
        }

        private void PlaySpellAnimation(in CombatAnimationRequest request, bool preserveFullBodyHoldBlendOut = false)
        {
            string spellKind = request.ActionId;
            int bankSlot = ResolveNextSpellBankSlot();
            if (!TryBindSpellClip(
                    spellKind,
                    bankSlot,
                    out WeaponSpellAnimationEntry spellEntry,
                    out bool confirmedInstant))
                return;

            AnimationClip? spellClip = spellEntry.ResolveClip();
            if (spellClip == null)
                return;

            float normalizedStart = ResolveSpellReleaseStartNormalizedTime(
                request,
                spellEntry,
                spellClip,
                confirmedInstant);
            PlayResolvedSpellAnimation(
                spellKind,
                bankSlot,
                spellEntry,
                spellClip,
                normalizedStart,
                confirmedInstant,
                preserveFullBodyHoldBlendOut);
        }

        private void PlayResolvedSpellAnimation(
            string spellKind,
            int bankSlot,
            WeaponSpellAnimationEntry spellEntry,
            AnimationClip spellClip,
            float normalizedStart,
            bool confirmedInstant,
            bool preserveFullBodyHoldBlendOut)
        {
            _animator!.SetBool(MirrorSpellActionHash, spellEntry.mirrorPresentation);
            BeginAnimatedSpellPropHandoff(
                spellKind,
                spellEntry,
                // Keep legacy prop timing byte-for-byte for every other archetype.
                confirmedInstant
                    ? normalizedStart * Mathf.Max(0f, spellClip.length)
                    : 0f);

            bool useOverlaySpellPlayback = spellEntry.ResolveUsesOverlayPlayback(
                _latestLocomotionRawMagnitude,
                StopTriggerThreshold);
            bool needsCombatVisualStance = !_inCombat || (_weaponAttachments != null && !_weaponAttachments.IsInCombatVisual);
            if (needsCombatVisualStance && spellEntry.ShouldEnterCombatImmediately(useOverlaySpellPlayback))
            {
                EnterCombatImmediate();
                needsCombatVisualStance = false;
            }

            if (useOverlaySpellPlayback)
            {
                ClearActiveSpellPresentation(resetLayerWeight: true, clearUpperBodySpell: false);
                if (!preserveFullBodyHoldBlendOut)
                    _animator!.Play(SpellActionEmptyStateHash, SpellActionLayerIndex, 0f);

                ClearMeleePresentationForUpperBodySpell();
                if (spellEntry.UsesLeftGesture)
                {
                    PlayUpperBodyState(UpperBodyEmptyStateHash, 0f);
                    ClearRightGestureSpellPresentation();
                    PlayLeftGestureState(ResolveLeftGestureSpellStateHash(bankSlot), normalizedStart);
                    _actionPlayback.SetActiveOverlaySpellPresentation(
                        spellKind,
                        ResolveLeftGestureSpellStateHash(bankSlot),
                        SpellPlaybackLayer.LeftGesture);
                }
                else if (spellEntry.UsesRightGesture)
                {
                    PlayUpperBodyState(UpperBodyEmptyStateHash, 0f);
                    ClearLeftGestureSpellPresentation();
                    PlayRightGestureState(ResolveRightGestureSpellStateHash(bankSlot), normalizedStart);
                    _actionPlayback.SetActiveOverlaySpellPresentation(
                        spellKind,
                        ResolveRightGestureSpellStateHash(bankSlot),
                        SpellPlaybackLayer.RightGesture);
                }
                else
                {
                    ClearLeftGestureSpellPresentation();
                    ClearRightGestureSpellPresentation();
                    PlayUpperBodyState(ResolveUpperBodySpellStateHash(bankSlot), normalizedStart);
                    _actionPlayback.SetActiveOverlaySpellPresentation(
                        spellKind,
                        ResolveUpperBodySpellStateHash(bankSlot),
                        SpellPlaybackLayer.UpperBody);
                }
                // A charged hold's exit fade blends its hold layer 1->0. When the release
                // renders on that same overlay layer (masked gesture, or UpperBody while
                // moving), the fade would drag the release out from under itself. The release
                // now drives that layer at full weight and auto-exits on its own, so cancel
                // the fade. Full-body/off-layer releases keep the fade (it blends the leftover
                // hold overlay away while the release plays elsewhere).
                if (_actionPlayback.IsSpellCastHoldFadeOutActive)
                {
                    int releaseLayerIndex = ResolveOverlaySpellLayerIndex(spellEntry.playbackLayer);
                    if (_actionPlayback.SpellCastHoldFadeOutLayerIndex == releaseLayerIndex)
                    {
                        _actionPlayback.ResetSpellCastHoldFadeOut();
                        _animator!.SetLayerWeight(releaseLayerIndex, 1f);
                    }
                }
                if (needsCombatVisualStance && spellEntry.ShouldEnterCombatAfterCastStarts(useOverlaySpellPlayback))
                    SetInCombat(true);
                return;
            }

            SetActiveSpellPresentation(spellKind, bankSlot, spellEntry);
            ClearLeftGestureSpellPresentation();
            ClearRightGestureSpellPresentation();
            int triggerHash = ResolveSpellActionTriggerHash(bankSlot);
            CombatActionPlaybackController.PlayFullBodySpellAction(
                _animator!,
                SpellActionLayerIndex,
                triggerHash,
                ResolveSpellActionStateHash(bankSlot),
                normalizedStart);
            if (needsCombatVisualStance && spellEntry.ShouldEnterCombatAfterCastStarts(useOverlaySpellPlayback))
                SetInCombat(true);
        }

        private float ResolveSpellReleaseStartNormalizedTime(
            in CombatAnimationRequest request,
            WeaponSpellAnimationEntry spellEntry,
            AnimationClip spellClip,
            bool confirmedInstant)
        {
            float clipLengthSeconds = Mathf.Max(0f, spellClip.length);
            float authoredReleasePointSeconds = spellEntry.ResolveReleaseOffsetSeconds();
            float startupTrimSeconds = Mathf.Max(
                spellEntry.ResolveInstantCastStartupTrimSeconds(confirmedInstant),
                request.SpellPlaybackStartOffsetSeconds);
            float effectiveReleasePointSeconds = Mathf.Max(
                0f,
                authoredReleasePointSeconds - startupTrimSeconds);

            // request.StartedAtMs is the scheduled release-animation start, not the
            // gameplay cast start. If the ActiveCast row arrives late or cast speed
            // clamps the start to the beginning of the cast, reuse the remote catch-up
            // policy but cap it before the authored release point so the hand-release
            // pose is not skipped.
            return CombatAnimationRemoteTiming.TryResolveStartNormalizedTime(
                request,
                _isLocalPlayer,
                clipLengthSeconds,
                clipLengthSeconds,
                startupTrimSeconds,
                effectiveReleasePointSeconds,
                out float normalizedStart,
                out _)
                    ? normalizedStart
                    : clipLengthSeconds > 0.001f
                        ? Mathf.Clamp01(startupTrimSeconds / clipLengthSeconds)
                        : 0f;
        }

        private void BeginAnimatedSpellPropHandoff(
            string spellKind,
            WeaponSpellAnimationEntry spellEntry,
            float playbackStartOffsetSeconds)
        {
            if (!spellEntry.HasAnimatedPropHandoff)
                return;

            if (_weaponAttachments == null)
                _weaponAttachments = GetComponent<WeaponAttachmentController>();
            if (_weaponAttachments == null)
                return;

            _weaponAttachments.BeginTemporaryAnimatedProp(
                spellKind,
                spellEntry.animatedProp,
                spellEntry.ResolveReleaseDelayAfterPlaybackStartSeconds(playbackStartOffsetSeconds));
        }

        private static int ResolveSpellActionStateHash(int bankSlot)
        {
            return CombatActionPlaybackController.ResolveBankedAnimatorHash(
                bankSlot,
                SpellAction1StateHash,
                SpellAction2StateHash,
                SpellAction3StateHash,
                SpellAction4StateHash);
        }

        private static int ResolveSpellCastHoldActionStateHash(int bankSlot)
        {
            return CombatActionPlaybackController.ResolveBankedAnimatorHash(
                bankSlot,
                SpellCastHoldAction1StateHash,
                SpellCastHoldAction2StateHash,
                SpellCastHoldAction3StateHash,
                SpellCastHoldAction4StateHash);
        }

        private static int ResolveSpellCastHoldActionFullPathHash(int bankSlot)
        {
            return CombatActionPlaybackController.ResolveBankedAnimatorHash(
                bankSlot,
                SpellCastHoldAction1FullPathHash,
                SpellCastHoldAction2FullPathHash,
                SpellCastHoldAction3FullPathHash,
                SpellCastHoldAction4FullPathHash);
        }

        private static int ResolveUpperBodySpellStateHash(int bankSlot)
        {
            return CombatActionPlaybackController.ResolveBankedAnimatorHash(
                bankSlot,
                UpperBodySpellAction1StateHash,
                UpperBodySpellAction2StateHash,
                UpperBodySpellAction3StateHash,
                UpperBodySpellAction4StateHash);
        }

        private static int ResolveUpperBodySpellCastHoldStateHash(int bankSlot)
        {
            return CombatActionPlaybackController.ResolveBankedAnimatorHash(
                bankSlot,
                UpperBodySpellCastHoldAction1StateHash,
                UpperBodySpellCastHoldAction2StateHash,
                UpperBodySpellCastHoldAction3StateHash,
                UpperBodySpellCastHoldAction4StateHash);
        }

        private static int ResolveLeftGestureSpellStateHash(int bankSlot)
        {
            return CombatActionPlaybackController.ResolveBankedAnimatorHash(
                bankSlot,
                LeftGestureSpellAction1StateHash,
                LeftGestureSpellAction2StateHash,
                LeftGestureSpellAction3StateHash,
                LeftGestureSpellAction4StateHash);
        }

        private static int ResolveLeftGestureSpellCastHoldStateHash(int bankSlot)
        {
            return CombatActionPlaybackController.ResolveBankedAnimatorHash(
                bankSlot,
                LeftGestureSpellCastHoldAction1StateHash,
                LeftGestureSpellCastHoldAction2StateHash,
                LeftGestureSpellCastHoldAction3StateHash,
                LeftGestureSpellCastHoldAction4StateHash);
        }

        private static int ResolveRightGestureSpellStateHash(int bankSlot)
        {
            return CombatActionPlaybackController.ResolveBankedAnimatorHash(
                bankSlot,
                RightGestureSpellAction1StateHash,
                RightGestureSpellAction2StateHash,
                RightGestureSpellAction3StateHash,
                RightGestureSpellAction4StateHash);
        }

        private static int ResolveRightGestureSpellCastHoldStateHash(int bankSlot)
        {
            return CombatActionPlaybackController.ResolveBankedAnimatorHash(
                bankSlot,
                RightGestureSpellCastHoldAction1StateHash,
                RightGestureSpellCastHoldAction2StateHash,
                RightGestureSpellCastHoldAction3StateHash,
                RightGestureSpellCastHoldAction4StateHash);
        }

        private static int ResolveSpellActionTriggerHash(int bankSlot)
        {
            return CombatActionPlaybackController.ResolveBankedAnimatorHash(
                bankSlot,
                TriggerSpellAction1Hash,
                TriggerSpellAction2Hash,
                TriggerSpellAction3Hash,
                TriggerSpellAction4Hash);
        }

        /// <summary>
        /// Fires a melee strike animation. strikeIndex is 1-based within the authored
        /// combat profile. The controller keeps four reusable strike states, so higher
        /// strike ids hot-swap the needed clip into that bank before triggering it.
        /// </summary>
        private bool TriggerStrike(int strikeIndex)
        {
            if (_animator == null || _overrideController == null)
            {
                TraceCombatAnimation(
                    $"strike-trigger-failed action={_combatAnimationTraceActionId} strike={strikeIndex} " +
                    "reason=animator-or-override-missing");
                return false;
            }

            int bankSlot = ResolveStrikeBankSlot(strikeIndex);
            if (!TryBindStrikeClip(strikeIndex, bankSlot))
            {
                TraceCombatAnimation(
                    $"strike-trigger-failed action={_combatAnimationTraceActionId} strike={strikeIndex} " +
                    $"bank={bankSlot} reason=clip-bind-failed");
                return false;
            }

            ResetMeleeLowerBodyUnlockState(resetLayerWeight: true, clearUpperBodyRecovery: true);
            int hash = ResolveStrikeTriggerHash(bankSlot);
            CombatActionPlaybackController.TriggerMeleeStrike(_animator, hash);
            TraceCombatAnimation(
                $"strike-trigger-sent action={_combatAnimationTraceActionId} strike={strikeIndex} " +
                $"bank={bankSlot} trigger={DescribeTriggerHash(hash)} layer={DescribeMeleeLayer()}");
            return true;
        }

        private bool PlayStrikeAtNormalizedTime(int strikeIndex, float normalizedTime)
        {
            if (_animator == null || _overrideController == null)
                return false;

            int bankSlot = ResolveStrikeBankSlot(strikeIndex);
            if (!TryBindStrikeClip(strikeIndex, bankSlot))
                return false;

            int stateHash = ResolveStrikeStateHash(bankSlot);
            if (stateHash == 0)
                return false;
            int fullPathStateHash = ResolveStrikeFullPathStateHash(bankSlot);

            ResetMeleeLowerBodyUnlockState(resetLayerWeight: true, clearUpperBodyRecovery: true);
            CombatActionPlaybackController.PlayMeleeStrikeState(
                _animator,
                MeleeAttackLayerIndex,
                fullPathStateHash != 0 ? fullPathStateHash : stateHash,
                normalizedTime);
            return true;
        }

        private void PlayMeleeAnimation(in CombatAnimationRequest request)
        {
            bool grounded = IsCurrentlyGrounded();
            EnsureCombatVisualForMeleePresentation(
                suppressAnimatorState: IsSpecialMovementDrivenPhasedMeleeRequest(request, grounded));

            string actionId = request.ActionId;
            int strikeIndex = _animationSet?.GetStrikeIndexForActionId(actionId) ?? 0;
            ArmMeleeEntryTrace(request, strikeIndex);

            if (TryTriggerPhasedMeleeAction(request, grounded))
            {
                TriggerWeaponPresentationEffects(request, strikeIndex);
                SetActiveMeleePresentation(request, strikeIndex, isPhased: true, grounded: grounded);
                TraceCombatAnimation(
                    $"melee-play-started action={actionId} strike={strikeIndex} phased=true " +
                    $"tracked={DescribeTrackedMeleePresentation()} layer={DescribeMeleeLayer()}");
                return;
            }

            if (strikeIndex <= 0)
            {
                FailMeleeEntryTrace("action-not-found-in-animation-set");
                return;
            }

            float startupTrimSeconds = _animationSet?.GetStrikeStartupTrimSeconds(strikeIndex) ?? 0f;
            float appliedCatchupSeconds = 0f;
            bool playedWithRemoteCatchup = TryPlayRemoteCatchupStrike(
                request,
                strikeIndex,
                startupTrimSeconds,
                out appliedCatchupSeconds);
            if (!playedWithRemoteCatchup)
            {
                bool playedFromAuthoredStart = startupTrimSeconds > 0.001f
                    ? PlayStrikeAtNormalizedTime(
                        strikeIndex,
                        _animationSet?.GetStrikeStartupTrimNormalized(strikeIndex) ?? 0f)
                    : TriggerStrike(strikeIndex);
                if (!playedFromAuthoredStart)
                {
                    FailMeleeEntryTrace("animator-playback-dispatch-failed");
                    return;
                }
            }

            TriggerWeaponPresentationEffects(request, strikeIndex);
            SetActiveMeleePresentation(
                request,
                strikeIndex,
                isPhased: false,
                grounded: grounded,
                appliedCatchupSeconds: appliedCatchupSeconds);
            TraceCombatAnimation(
                $"melee-play-started action={actionId} strike={strikeIndex} phased=false " +
                $"remoteCatchup={playedWithRemoteCatchup} tracked={DescribeTrackedMeleePresentation()} " +
                $"layer={DescribeMeleeLayer()}");
        }

        private bool IsSpecialMovementDrivenPhasedMeleeRequest(in CombatAnimationRequest request, bool grounded)
        {
            if (_animationSet == null || !request.DrivePhasesFromSpecialMovement)
                return false;

            return _animationSet.TryGetPhasedMeleeEntry(request.ActionId, out WeaponPhasedActionEntry entry)
                && entry.drivePhasesFromSpecialMovement
                && entry.TryResolveClipSet(grounded, out _);
        }

        private void EnsureCombatVisualForMeleePresentation(bool suppressAnimatorState)
        {
            bool needsCombatVisualStance = !_inCombat || (_weaponAttachments != null && !_weaponAttachments.IsInCombatVisual);
            if (!needsCombatVisualStance)
                return;

            if (suppressAnimatorState)
                SetCombatVisualFlagsForMeleePresentation();
            else
                EnterCombatImmediate();
        }

        private void SetCombatVisualFlagsForMeleePresentation()
        {
            _inCombat = true;
            ClearPendingWeaponHandoff();

            if (_weaponAttachments == null)
                _weaponAttachments = GetComponent<WeaponAttachmentController>();
            _weaponAttachments?.SetInCombat(true);
            // Do not set InCombatHash here. Movement-driven phased attacks must not
            // let the base layer enter a combat-stance transition before the phased set.
        }

        private bool TryPlayRemoteCatchupStrike(
            in CombatAnimationRequest request,
            int strikeIndex,
            float startupTrimSeconds,
            out float appliedCatchupSeconds)
        {
            appliedCatchupSeconds = 0f;
            if (_animationSet == null)
                return false;

            AnimationClip? playedClip = _animationSet.GetStrikeClip(strikeIndex);
            if (playedClip == null)
                return false;

            if (!CombatAnimationRemoteTiming.TryResolveStartNormalizedTime(
                    request,
                    _isLocalPlayer,
                    _animationSet.GetStrikeTimingReferenceLengthSeconds(strikeIndex),
                    playedClip.length,
                    startupTrimSeconds,
                    _animationSet.GetStrikeFirstHitWindowSeconds(strikeIndex),
                    out float normalizedStart,
                    out appliedCatchupSeconds))
            {
                return false;
            }

            return PlayStrikeAtNormalizedTime(strikeIndex, normalizedStart);
        }

        private void SetActiveMeleePresentation(
            in CombatAnimationRequest request,
            int strikeIndex,
            bool isPhased,
            bool grounded,
            float appliedCatchupSeconds = 0f)
        {
            _actionPlayback.SetActiveMeleePresentation(
                request,
                strikeIndex,
                isPhased,
                _animationSet,
                grounded,
                appliedCatchupSeconds);
            if (_animationSet != null)
            {
                MeleeAnimationVfxPlayer.Begin(
                    _animationSet,
                    strikeIndex,
                    request.AnimationVfxBindings);
            }
            _activeMeleePresentationDispatchedFrame = Time.frameCount;
            ResetMeleeLowerBodyUnlockState(resetLayerWeight: true, clearUpperBodyRecovery: false);
        }

        private void ClearActiveMeleePresentation()
        {
            _meleeAnimationVfxPlayer?.Clear();
            ResetPendingMeleeActionTriggers();
            bool wasPhased = _actionPlayback.ClearActiveMeleePresentation();
            _activeMeleePresentationDispatchedFrame = -1;
            if (wasPhased)
                CancelPhasedMeleePlayback(clearActivePresentation: false);
            ResetMeleeLowerBodyUnlockState(resetLayerWeight: true, clearUpperBodyRecovery: true);
        }

        private void TriggerWeaponPresentationEffects(in CombatAnimationRequest request, int strikeIndex)
        {
            if (!request.HasConsumedModifier || _animationSet == null)
                return;

            if (_weaponAttachments == null)
                _weaponAttachments = GetComponent<WeaponAttachmentController>();
            if (_weaponAttachments == null)
                return;

            float totalDuration = strikeIndex > 0
                ? Mathf.Max(
                    0f,
                    _animationSet.GetStrikeTimingReferenceLengthSeconds(strikeIndex)
                    - _animationSet.GetStrikeStartupTrimSeconds(strikeIndex))
                : 0f;
            if (totalDuration <= 0.01f)
                totalDuration = 0.45f;

            foreach (WeaponPresentationEffectEntry effect in _animationSet.MatchingConsumedMeleeModifierEffects(
                         request.ConsumedModifierStatusKind,
                         request.ConsumedModifierStackGroup))
            {
                float scaleIn = effect.ScaleInSecondsOrDefault;
                float scaleOut = effect.ScaleOutSecondsOrDefault;
                float hold = Mathf.Max(0f, totalDuration - scaleIn - scaleOut);
                _weaponAttachments.PlayScalePulse(
                    effect.target,
                    effect.itemId,
                    effect.ScaleMultiplierOrDefault,
                    scaleIn,
                    hold,
                    scaleOut);
            }
        }

        private bool IsHigherPriorityCombatPresentationActive()
        {
            bool hasActiveMeleePresentation = _actionPlayback.ActiveMeleePresentation.HasValue;
            CombatAnimationCategory activeMeleeCategory = hasActiveMeleePresentation
                ? _actionPlayback.ActiveMeleePresentation.GetValueOrDefault().Category
                : CombatAnimationCategory.AutoAttack;
            if (CombatActionPlaybackController.HasTrackedHigherPriorityPresentation(
                    hasActiveMeleePresentation,
                    activeMeleeCategory,
                    _actionPlayback.ActiveSpellPresentation.HasValue,
                    _actionPlayback.ActiveSpellCastHoldPresentation.HasValue))
            {
                return true;
            }

            if (_animator == null)
            {
                return false;
            }

            return IsSkillPresentationStateActive(BaseLayerIndex, includeStrikeStates: false)
                || IsStrikePresentationStateActiveOnMeleeLayer()
                || IsSkillPresentationStateActive(UpperBodyLayerIndex, includeStrikeStates: false)
                || IsSkillPresentationStateActive(SpellActionLayerIndex, includeStrikeStates: false)
                || IsSkillPresentationStateActive(LeftGestureLayerIndex, includeStrikeStates: false)
                || IsSkillPresentationStateActive(RightGestureLayerIndex, includeStrikeStates: false);
        }

        private void PreemptMeleeAnimationIfActive(bool captureGhost = true)
        {
            if (_animator == null)
                return;

            if (!IsMeleePresentationStateActive())
            {
                ClearActiveMeleePresentation();
                return;
            }

            if (captureGhost)
            {
                _meleeGhostLayer?.CaptureFrozenPose();
            }

            if (_actionPlayback.IsPhasedMeleeActive)
                CancelPhasedMeleePlayback(clearActivePresentation: false);

            if (IsStrikePresentationStateActiveOnMeleeLayer())
                _animator.Play(MeleeAttackEmptyStateHash, MeleeAttackLayerIndex, 0f);

            ClearActiveMeleePresentation();
        }

        private bool IsMeleePresentationStateActive()
        {
            if (_animator == null)
                return false;

            return IsPhasedMeleePresentationStateActive()
                || IsStrikePresentationStateActiveOnMeleeLayer()
                || IsUpperBodyMeleeRecoveryStateActive();
        }

        private bool IsPhasedMeleePresentationStateActive()
        {
            if (_animator == null || !_actionPlayback.IsPhasedMeleeActive)
                return false;

            return _actionPlayback.IsPhasedMeleeUpperBodyMode
                ? IsUpperBodyMeleeRecoveryStateActive()
                : IsStrikePresentationStateActiveOnMeleeLayer();
        }

        private bool IsStrikePresentationStateActiveOnMeleeLayer()
        {
            if (_animator == null)
                return false;

            return IsStrikeState(_animator.GetCurrentAnimatorStateInfo(MeleeAttackLayerIndex).shortNameHash)
                || (_animator.IsInTransition(MeleeAttackLayerIndex)
                    && IsStrikeState(_animator.GetNextAnimatorStateInfo(MeleeAttackLayerIndex).shortNameHash));
        }

        private bool IsUpperBodyMeleeRecoveryStateActive()
        {
            if (_animator == null || !_actionPlayback.IsMeleeUpperBodyRecoveryActive)
                return false;

            return _animator.GetCurrentAnimatorStateInfo(UpperBodyLayerIndex).shortNameHash == UpperBodyRecoveryAction1StateHash
                || (_animator.IsInTransition(UpperBodyLayerIndex)
                    && _animator.GetNextAnimatorStateInfo(UpperBodyLayerIndex).shortNameHash == UpperBodyRecoveryAction1StateHash);
        }

        private bool IsSkillPresentationStateActive(int layerIndex, bool includeStrikeStates)
        {
            if (_animator == null)
                return false;

            if (IsMatchingSkillPresentationState(_animator.GetCurrentAnimatorStateInfo(layerIndex), includeStrikeStates))
                return true;

            if (_animator.IsInTransition(layerIndex)
                && IsMatchingSkillPresentationState(_animator.GetNextAnimatorStateInfo(layerIndex), includeStrikeStates))
            {
                return true;
            }

            return false;
        }

        private static bool IsMatchingSkillPresentationState(AnimatorStateInfo state, bool includeStrikeStates)
        {
            return state.shortNameHash == UpperBodySpellAction1StateHash
                || state.shortNameHash == UpperBodySpellAction2StateHash
                || state.shortNameHash == UpperBodySpellAction3StateHash
                || state.shortNameHash == UpperBodySpellAction4StateHash
                || state.shortNameHash == LeftGestureSpellAction1StateHash
                || state.shortNameHash == LeftGestureSpellAction2StateHash
                || state.shortNameHash == LeftGestureSpellAction3StateHash
                || state.shortNameHash == LeftGestureSpellAction4StateHash
                || state.shortNameHash == RightGestureSpellAction1StateHash
                || state.shortNameHash == RightGestureSpellAction2StateHash
                || state.shortNameHash == RightGestureSpellAction3StateHash
                || state.shortNameHash == RightGestureSpellAction4StateHash
                || state.shortNameHash == UpperBodyRecoveryAction1StateHash
                || state.shortNameHash == SpellAction1StateHash
                || state.shortNameHash == SpellAction2StateHash
                || state.shortNameHash == SpellAction3StateHash
                || state.shortNameHash == SpellAction4StateHash
                || state.shortNameHash == SpellCastHoldAction1StateHash
                || state.shortNameHash == SpellCastHoldAction2StateHash
                || state.shortNameHash == SpellCastHoldAction3StateHash
                || state.shortNameHash == SpellCastHoldAction4StateHash
                || (includeStrikeStates && IsStrikeState(state.shortNameHash));
        }

        private static bool IsStrikeState(int shortNameHash)
        {
            return shortNameHash == Strike1StateHash
                || shortNameHash == Strike2StateHash
                || shortNameHash == Strike3StateHash
                || shortNameHash == Strike4StateHash;
        }

        private void ClearMeleePresentationForUpperBodySpell()
        {
            if (_animator == null)
                return;

            if (IsStrikePresentationStateActiveOnMeleeLayer())
                _animator.Play(MeleeAttackEmptyStateHash, MeleeAttackLayerIndex, 0f);

            if (_actionPlayback.IsPhasedMeleeActive)
                CancelPhasedMeleePlayback(clearActivePresentation: false);

            ClearActiveMeleePresentation();
        }

        public void ApplySharedActionProfile(SharedActionProfile profile)
        {
            EnsureOverrideController();
            _sharedActionProfile = profile;
            ApplySharedActionOverrides(IsCurrentlyGrounded(), forceDodge: true);
        }

        private void ApplySharedActionOverrides(bool grounded, bool forceDodge = false)
        {
            if (_overrideController == null || _sharedActionProfile == null)
                return;

            ApplyDodgeClipOverrides(grounded, forceDodge);
        }

        private static int ResolveStrikeBankSlot(int strikeIndex)
        {
            return CombatActionPlaybackController.ResolveStrikeBankSlot(strikeIndex);
        }

        private static int ResolveStrikeStateHash(int bankSlot)
        {
            return CombatActionPlaybackController.ResolveBankedAnimatorHash(
                bankSlot,
                Strike1StateHash,
                Strike2StateHash,
                Strike3StateHash,
                Strike4StateHash);
        }

        private static int ResolveStrikeFullPathStateHash(int bankSlot)
        {
            return CombatActionPlaybackController.ResolveBankedAnimatorHash(
                bankSlot,
                MeleeAttackStrike1FullPathHash,
                MeleeAttackStrike2FullPathHash,
                MeleeAttackStrike3FullPathHash,
                MeleeAttackStrike4FullPathHash);
        }

        private static int ResolveStrikeTriggerHash(int bankSlot)
        {
            return CombatActionPlaybackController.ResolveBankedAnimatorHash(
                bankSlot,
                TriggerStrike1Hash,
                TriggerStrike2Hash,
                TriggerStrike3Hash,
                TriggerStrike4Hash);
        }

        private bool TryBindStrikeClip(int strikeIndex, int bankSlot)
        {
            bool bound = _actionPlayback.TryBindStrikeClip(
                _overrideController,
                _animationSet,
                strikeIndex,
                bankSlot);
            TraceStrikeBinding(strikeIndex, bankSlot, bound);
            return bound;
        }

        [System.Diagnostics.Conditional(VerboseTraceSymbol)]
        private void TraceStrikeBinding(int strikeIndex, int bankSlot, bool bound)
        {
            if (!_isLocalPlayer)
                return;

            AnimationClip? desired = _animationSet?.GetStrikeClip(strikeIndex);
            AnimationClip? applied = _overrideController?[$"slot_strike_{bankSlot}"];
            TraceCombatAnimation(
                $"strike-bind action={_combatAnimationTraceActionId} strike={strikeIndex} bank={bankSlot} " +
                $"bound={bound} desired={(desired != null ? desired.name : "<controller-default>")} " +
                $"applied={(applied != null ? applied.name : "<null>")}");
        }

        [System.Diagnostics.Conditional(VerboseTraceSymbol)]
        private void ArmMeleeEntryTrace(in CombatAnimationRequest request, int strikeIndex)
        {
            if (!_isLocalPlayer)
                return;

            if (_combatAnimationTraceAwaitingMeleeEntry)
            {
                TraceCombatAnimation(
                    $"entry-superseded previousAction={_combatAnimationTraceActionId} " +
                    $"previousCategory={_combatAnimationTraceCategory} beforeStateEntry=true " +
                    $"newAction={request.ActionId} newCategory={request.Category}");
            }

            int bankSlot = strikeIndex > 0 ? ResolveStrikeBankSlot(strikeIndex) : 0;
            _combatAnimationTraceAwaitingMeleeEntry = strikeIndex > 0;
            _combatAnimationTraceActionId = request.ActionId;
            _combatAnimationTraceCategory = request.Category;
            _combatAnimationTraceRequestedFrame = Time.frameCount;
            _combatAnimationTraceExpectedStateHash = bankSlot > 0
                ? ResolveStrikeStateHash(bankSlot)
                : 0;
            _combatAnimationTraceObservationUntilFrame = Time.frameCount + 8;
            _combatAnimationTraceLastCurrentStateHash = int.MinValue;
            _combatAnimationTraceLastNextStateHash = int.MinValue;

            AnimationClip? clip = strikeIndex > 0
                ? _animationSet?.GetStrikeClip(strikeIndex)
                : null;
            TraceCombatAnimation(
                $"melee-resolved action={request.ActionId} category={request.Category} strike={strikeIndex} " +
                $"bank={bankSlot} expected={DescribeStateHash(_combatAnimationTraceExpectedStateHash)} " +
                $"clip={(clip != null ? clip.name : "<phased-or-null>")}");
        }

        [System.Diagnostics.Conditional(VerboseTraceSymbol)]
        private void FailMeleeEntryTrace(string reason)
        {
            if (!_isLocalPlayer)
                return;

            TraceCombatAnimation(
                $"melee-play-failed action={_combatAnimationTraceActionId} " +
                $"category={_combatAnimationTraceCategory} reason={reason} layer={DescribeMeleeLayer()}");
            _combatAnimationTraceAwaitingMeleeEntry = false;
        }

        [System.Diagnostics.Conditional(VerboseTraceSymbol)]
        private void TracePendingMeleeEntry()
        {
            if (!_isLocalPlayer || !_combatAnimationTraceAwaitingMeleeEntry || _animator == null)
                return;

            AnimatorStateInfo current = _animator.GetCurrentAnimatorStateInfo(MeleeAttackLayerIndex);
            bool inTransition = _animator.IsInTransition(MeleeAttackLayerIndex);
            AnimatorStateInfo next = inTransition
                ? _animator.GetNextAnimatorStateInfo(MeleeAttackLayerIndex)
                : default;
            int currentHash = current.shortNameHash;
            int nextHash = inTransition ? next.shortNameHash : 0;
            bool enteredExpectedState =
                currentHash == _combatAnimationTraceExpectedStateHash
                || (inTransition && nextHash == _combatAnimationTraceExpectedStateHash);
            bool stateChanged =
                currentHash != _combatAnimationTraceLastCurrentStateHash
                || nextHash != _combatAnimationTraceLastNextStateHash;
            bool observationExpired = Time.frameCount >= _combatAnimationTraceObservationUntilFrame;

            if (stateChanged || enteredExpectedState || observationExpired)
            {
                TraceCombatAnimation(
                    $"entry-observation action={_combatAnimationTraceActionId} " +
                    $"category={_combatAnimationTraceCategory} requestedFrame={_combatAnimationTraceRequestedFrame} " +
                    $"expected={DescribeStateHash(_combatAnimationTraceExpectedStateHash)} " +
                    $"entered={enteredExpectedState} expired={observationExpired} layer={DescribeMeleeLayer()} " +
                    $"tracked={DescribeTrackedMeleePresentation()}");
            }

            _combatAnimationTraceLastCurrentStateHash = currentHash;
            _combatAnimationTraceLastNextStateHash = nextHash;
            if (enteredExpectedState || observationExpired)
                _combatAnimationTraceAwaitingMeleeEntry = false;
        }

        [System.Diagnostics.Conditional(VerboseTraceSymbol)]
        private void TraceCombatAnimation(string message)
        {
            if (!_isLocalPlayer)
                return;

            Debug.Log($"[CombatAnimTrace] frame={Time.frameCount} {message}", this);
        }

        private string DescribeTrackedMeleePresentation()
        {
            if (!_actionPlayback.ActiveMeleePresentation.HasValue)
                return "<none>";

            ActiveMeleePresentation active =
                _actionPlayback.ActiveMeleePresentation.GetValueOrDefault();
            return $"{active.ActionId}:{active.Category}:strike={active.StrikeIndex}:" +
                   $"entered={_actionPlayback.ActiveMeleePresentationEntered}";
        }

        private string DescribeMeleeLayer()
        {
            if (_animator == null)
                return "<animator-null>";

            AnimatorStateInfo current =
                _animator.GetCurrentAnimatorStateInfo(MeleeAttackLayerIndex);
            if (!_animator.IsInTransition(MeleeAttackLayerIndex))
            {
                return $"current={DescribeStateHash(current.shortNameHash)}@{current.normalizedTime:F3} " +
                       $"transition=false weight={_animator.GetLayerWeight(MeleeAttackLayerIndex):F2}";
            }

            AnimatorStateInfo next =
                _animator.GetNextAnimatorStateInfo(MeleeAttackLayerIndex);
            return $"current={DescribeStateHash(current.shortNameHash)}@{current.normalizedTime:F3} " +
                   $"next={DescribeStateHash(next.shortNameHash)}@{next.normalizedTime:F3} " +
                   $"transition=true weight={_animator.GetLayerWeight(MeleeAttackLayerIndex):F2}";
        }

        private static string DescribeStateHash(int stateHash)
        {
            if (stateHash == 0)
                return "<none>";
            if (stateHash == MeleeAttackEmptyStateHash)
                return "Empty";
            if (stateHash == Strike1StateHash)
                return "Strike1";
            if (stateHash == Strike2StateHash)
                return "Strike2";
            if (stateHash == Strike3StateHash)
                return "Strike3";
            if (stateHash == Strike4StateHash)
                return "Strike4";
            if (stateHash == UpperBodyRecoveryAction1StateHash)
                return "UpperBodyRecoveryAction1";
            return stateHash.ToString();
        }

        private static string DescribeTriggerHash(int triggerHash)
        {
            if (triggerHash == TriggerStrike1Hash)
                return "TriggerStrike1";
            if (triggerHash == TriggerStrike2Hash)
                return "TriggerStrike2";
            if (triggerHash == TriggerStrike3Hash)
                return "TriggerStrike3";
            if (triggerHash == TriggerStrike4Hash)
                return "TriggerStrike4";
            return triggerHash.ToString();
        }

        private int ResolveNextSpellBankSlot()
        {
            return _actionPlayback.ResolveNextSpellBankSlot();
        }

        private bool TryBindSpellClip(
            string spellKind,
            int bankSlot,
            out WeaponSpellAnimationEntry spellEntry,
            out bool confirmedInstant)
        {
            return _actionPlayback.TryBindSpellClip(
                _overrideController,
                _animationSet,
                spellKind,
                bankSlot,
                out spellEntry,
                out confirmedInstant);
        }

        public void StartParry()
        {
            if (_animator == null || _overrideController == null) return;
            SetParryArmed(true);
        }

        public void TriggerParryHit()
        {
            if (_animator == null || _overrideController == null) return;
            SetParryArmed(false);
            _animator.ResetTrigger(TriggerParryHitHash);
            _animator.SetTrigger(TriggerParryHitHash);
        }

        public void SetParryArmed(bool armed)
        {
            if (_animator == null || _overrideController == null) return;
            if (armed == _parryArmedPresentationActive)
            {
                _animator.SetBool(IsBlockingHash, _blockingPresentationActive || _parryArmedPresentationActive);
                return;
            }

            if (armed)
            {
                ArmParryPresentation();
            }
            else
            {
                DisarmParryPresentation();
            }
        }

        private void ArmParryPresentation()
        {
            _parryArmedPresentationActive = true;
            _animator!.SetBool(IsBlockingHash, true);
            PlayUpperBodyState(UpperBodyBlockStartStateHash, 0f);
        }

        private void DisarmParryPresentation()
        {
            _parryArmedPresentationActive = false;
            _animator!.SetBool(IsBlockingHash, _blockingPresentationActive);
            if (!_blockingPresentationActive)
                PlayUpperBodyState(UpperBodyEmptyStateHash, 0f);
        }

        public void SetBlocking(bool isBlocking)
        {
            if (_animator == null || _overrideController == null) return;
            bool wasBlocking = _blockingPresentationActive;
            _blockingPresentationActive = isBlocking;
            _animator.SetBool(IsBlockingHash, _blockingPresentationActive || _parryArmedPresentationActive);
            if (isBlocking && !wasBlocking)
            {
                PlayUpperBodyState(UpperBodyEmptyStateHash, 0f);
                if (ShouldUseMovingBlockLoopTransition(
                        IsCurrentlyGrounded(),
                        _latestLocomotionRawMagnitude,
                        _animationSet?.blockWalkLoop.HasAny == true))
                {
                    _animator.CrossFadeInFixedTime(BlockLoopStateHash, LocomotionRecoveryCrossFadeDurationSeconds, BaseLayerIndex, 0f);
                    return;
                }

                _animator.ResetTrigger(TriggerBlockStartHash);
                _animator.SetTrigger(TriggerBlockStartHash);
            }
            else if (!isBlocking && wasBlocking && !_parryArmedPresentationActive)
            {
                PlayUpperBodyState(UpperBodyEmptyStateHash, 0f);
                if (ShouldUseMovingBlockLoopTransition(
                        IsCurrentlyGrounded(),
                        _latestLocomotionRawMagnitude,
                        _animationSet?.blockWalkLoop.HasAny == true))
                {
                    int targetStateHash = _inCombat ? IdleCombatStateHash : IdleWalkRunBlendStateHash;
                    _animator.CrossFadeInFixedTime(targetStateHash, LocomotionRecoveryCrossFadeDurationSeconds, BaseLayerIndex, 0f);
                }
            }
        }

        public void TriggerBlockHit()
        {
            if (_animator == null || _overrideController == null) return;
            SetBlocking(false);
            _animator.ResetTrigger(TriggerBlockHitHash);
            _animator.SetTrigger(TriggerBlockHitHash);
        }

        public void TriggerDodge(MovementActionState movementAction)
        {
            ClearWorldInteractionAnimation();
            _activeMovementPresentation = new ActiveMovementActionPresentation(
                movementAction.Kind,
                movementAction.StartedAt.MicrosecondsSinceUnixEpoch / 1000L,
                movementAction.ActiveUntil.MicrosecondsSinceUnixEpoch / 1000L,
                movementAction.RecoveryUntil.MicrosecondsSinceUnixEpoch / 1000L);
            TriggerDodge(movementAction.DirX, movementAction.DirZ, movementAction.FacingYawStart);
        }

        private void TriggerDodge(float dirX, float dirZ, float facingYawRadians)
        {
            if (_animator == null || _overrideController == null) return;
            EnsureSharedActionProfileLoaded();
            DirectionalClipSet dodgeClips = ApplyDodgeClipOverrides(_wasGrounded, force: true);

            float forwardX = Mathf.Sin(facingYawRadians);
            float forwardZ = Mathf.Cos(facingYawRadians);
            float rightX = Mathf.Cos(facingYawRadians);
            float rightZ = -Mathf.Sin(facingYawRadians);
            float localX = dirX * rightX + dirZ * rightZ;
            float localZ = dirX * forwardX + dirZ * forwardZ;
            if (Mathf.Abs(localX) < MovementDeadZone && Mathf.Abs(localZ) < MovementDeadZone)
            {
                if (_latestLocomotionRawMagnitude >= MovementDeadZone)
                {
                    localX = _lastMoveVelX;
                    localZ = _lastMoveVelZ;
                }
                else
                {
                    localX = 0f;
                    localZ = -1f;
                }
            }

            float localMagnitude = Mathf.Sqrt(localX * localX + localZ * localZ);
            if (localMagnitude >= MovementDeadZone)
            {
                localX /= localMagnitude;
                localZ /= localMagnitude;
            }
            else
            {
                localX = 0f;
                localZ = -1f;
            }

            ResolveActiveDodgeTiming(dodgeClips, localX, localZ);
            _animator.SetFloat(DodgeXHash, Mathf.Clamp(localX, -1f, 1f));
            _animator.SetFloat(DodgeZHash, Mathf.Clamp(localZ, -1f, 1f));
            UpdateDodgePlaybackPhase();
            _animator.ResetTrigger(TriggerDodgeHash);
            _animator.SetTrigger(TriggerDodgeHash);
        }

        private DirectionalClipSet ApplyDodgeClipOverrides(bool grounded, bool force = false)
        {
            if (_overrideController == null)
                return default;

            EnsureSharedActionProfileLoaded();
            bool useCombatVariant = _inCombat;
            DirectionalClipSet desired = grounded
                ? (useCombatVariant
                    ? (_animationSet?.dodgeCombat.HasAny == true ? _animationSet.dodgeCombat : _sharedActionProfile?.dodge ?? _animationSet?.dodge ?? default)
                    : (_animationSet?.dodge.HasAny == true ? _animationSet.dodge : _sharedActionProfile?.dodge ?? _animationSet?.dodgeCombat ?? default))
                : (useCombatVariant
                    ? (_animationSet?.airDodgeCombat.HasAny == true ? _animationSet.airDodgeCombat : _sharedActionProfile?.airDodge ?? _animationSet?.airDodge ?? _animationSet?.dodgeCombat ?? default)
                    : (_animationSet?.airDodge.HasAny == true ? _animationSet.airDodge : _sharedActionProfile?.airDodge ?? _animationSet?.dodge ?? _animationSet?.airDodgeCombat ?? default));

            if (!force && !desired.HasAny)
                return desired;

            _animationSetBinder.ApplyDirectionalOverrideSet(_overrideController, "slot_dodge", desired);
            return desired;
        }

        private void ResolveActiveDodgeTiming(
            DirectionalClipSet dodgeClips,
            float localX,
            float localZ)
        {
            AnimationClip? clip = ResolveDirectionalDodgeClip(dodgeClips, localX, localZ);
            _activeDodgeClipLengthSeconds = clip != null
                ? Mathf.Max(0f, clip.length)
                : 0f;
            _activeDodgeStartNormalized =
                CombatAnimationEvents.TryGetEventNormalizedTime(
                    clip,
                    CombatAnimationEvents.OnDodgeStart,
                    out float startNormalized)
                    ? startNormalized
                    : 0f;

            _activeDodgeTravelEndNormalized =
                CombatAnimationEvents.TryGetEventNormalizedTime(
                    clip,
                    CombatAnimationEvents.OnDodgeTravelEnd,
                    out float travelEndNormalized)
                    ? Mathf.Clamp(travelEndNormalized, _activeDodgeStartNormalized, 1f)
                    : -1f;
        }

        private static AnimationClip? ResolveDirectionalDodgeClip(
            DirectionalClipSet clips,
            float localX,
            float localZ)
        {
            int octant = Mathf.RoundToInt(
                Mathf.Atan2(localX, localZ) * Mathf.Rad2Deg / 45f);
            octant = (octant % 8 + 8) % 8;

            AnimationClip? selected = octant switch
            {
                0 => clips.n,
                1 => clips.ne,
                2 => clips.e,
                3 => clips.se,
                4 => clips.s,
                5 => clips.sw,
                6 => clips.w,
                _ => clips.nw,
            };

            return selected
                ?? clips.n
                ?? clips.ne
                ?? clips.e
                ?? clips.se
                ?? clips.s
                ?? clips.sw
                ?? clips.w
                ?? clips.nw;
        }

        private bool IsCurrentlyGrounded()
        {
            bool grounded = _simState?.HasState == true ? _simState.IsGrounded : true;
            LocalPlayerMotor? motor = ResolveLocalPlayerMotor();
            if (motor != null)
            {
                grounded = motor.IsGrounded;
            }

            return grounded;
        }

        private LocalPlayerMotor? ResolveLocalPlayerMotor()
        {
            if (!_isLocalPlayer)
                return null;

            _localPlayerMotor ??= GetComponent<LocalPlayerMotor>();
            return _localPlayerMotor;
        }

        private void ApplyHitClipOverrides(bool grounded, bool force = false)
        {
            if (_overrideController == null)
                return;
            CombatAnimationSet? animationSet = _animationSet;
            if (animationSet == null)
                return;

            bool useAirVariant = !grounded;
            if (!force && _hitUsesAirVariant == useAirVariant)
            {
                if (_hitUsesCombatVariant == _inCombat)
                    return;
            }

            _hitUsesAirVariant = useAirVariant;
            _hitUsesCombatVariant = _inCombat;
            _animationSetBinder.ApplyHitClipOverrides(_overrideController, animationSet, grounded, _inCombat);
        }

        private bool TryTriggerPhasedMeleeAction(in CombatAnimationRequest request, bool grounded)
        {
            if (_animator == null || _overrideController == null || _animationSet == null)
                return false;

            if (!_animationSet.TryGetPhasedMeleeEntry(request.ActionId, out WeaponPhasedActionEntry phasedMeleeEntry))
                return false;

            if (!phasedMeleeEntry.TryResolveClipSet(grounded, out ResolvedWeaponPhasedActionClipSet clipSet))
                return false;

            int strikeIndex = _animationSet.GetStrikeIndexForActionId(request.ActionId);
            if (strikeIndex <= 0)
                return false;

            bool drivesPhasesFromSpecialMovement =
                phasedMeleeEntry.drivePhasesFromSpecialMovement
                && request.DrivePhasesFromSpecialMovement;
            bool scalesGapClosePhasesFromImpactReach =
                phasedMeleeEntry.drivePhasesFromSpecialMovement
                && phasedMeleeEntry.scaleGapClosePhasesFromImpactReach
                && request.ScaleGapClosePhasesFromImpactReach;
            bool usesTimeDrivenImpactReachScaling = scalesGapClosePhasesFromImpactReach
                && !drivesPhasesFromSpecialMovement;
            bool startsAtLoop = usesTimeDrivenImpactReachScaling
                && !request.GapCloseUsedMovementAtCast
                && !clipSet.ReleaseAfterStart;
            float requestedLoopExitNormalizedTime = usesTimeDrivenImpactReachScaling
                ? ResolveImpactReachScaledLoopExitNormalizedTime(request, clipSet, startsAtLoop)
                : -1f;
            float phasedOpeningTailSeconds =
                clipSet.ResolveOpeningTailSeconds(phasedMeleeEntry.phasedOpeningTailSeconds);
            float startPlaybackNormalizedTime = phasedOpeningTailSeconds > 0f
                ? clipSet.ResolveOpeningStartNormalizedTime(phasedOpeningTailSeconds)
                : 0f;
            float requestedStartExitNormalizedTime = phasedOpeningTailSeconds > 0f
                ? 1f
                : -1f;
            _actionPlayback.BeginPhasedMelee(
                ResolveStrikeBankSlot(strikeIndex),
                clipSet.Start,
                clipSet.Loop,
                clipSet.End,
                clipSet.ReleaseAfterStart,
                specialMovementDriven: drivesPhasesFromSpecialMovement
                    || phasedMeleeEntry.drivePhasesFromCombatLifecycle,
                specialMovementArrivalDriven: drivesPhasesFromSpecialMovement,
                startsAtLoop: startsAtLoop,
                requestedLoopExitNormalizedTime: requestedLoopExitNormalizedTime,
                startPlaybackNormalizedTime: startPlaybackNormalizedTime,
                requestedStartExitNormalizedTime: requestedStartExitNormalizedTime);
            PlayUpperBodyState(UpperBodyEmptyStateHash, 0f);
            ResetMeleeLowerBodyUnlockState(resetLayerWeight: true, clearUpperBodyRecovery: true);
            return PlayPhasedMeleeSegment(
                startsAtLoop ? PhasedMeleePlaybackPhase.Loop : PhasedMeleePlaybackPhase.Start,
                startsAtLoop ? 0f : startPlaybackNormalizedTime);
        }

        private static float ResolveImpactReachScaledLoopExitNormalizedTime(
            in CombatAnimationRequest request,
            ResolvedWeaponPhasedActionClipSet clipSet,
            bool startsAtLoop)
        {
            float loopClipLengthSeconds = Mathf.Max(0f, clipSet.Loop.length);
            if (loopClipLengthSeconds <= 0.001f)
                return 0f;

            if (request.AuthoritativeImpactDelaySeconds >= 0f)
            {
                var endHitTimes = new List<float>();
                CombatAnimationEvents.AppendEventTimes(
                    clipSet.End,
                    CombatAnimationEvents.OnStrikeHit,
                    endHitTimes);
                if (endHitTimes.Count > 0)
                {
                    float lastEndHitSeconds = 0f;
                    for (int index = 0; index < endHitTimes.Count; index++)
                        lastEndHitSeconds = Mathf.Max(lastEndHitSeconds, endHitTimes[index]);
                    float preEndSeconds = Mathf.Max(
                        0f,
                        request.AuthoritativeImpactDelaySeconds - lastEndHitSeconds);
                    float startSeconds = startsAtLoop
                        ? 0f
                        : clipSet.ResolveStartTimelineLengthSeconds();
                    return Mathf.Clamp01(
                        Mathf.Max(0f, preEndSeconds - startSeconds) / loopClipLengthSeconds);
                }
            }

            float authoredLoopExitNormalizedTime = Mathf.Clamp01(
                clipSet.ResolveLoopTimelineLengthSeconds() / loopClipLengthSeconds);
            return authoredLoopExitNormalizedTime * request.GapCloseLoopScale;
        }

        private void UpdatePhasedMeleePlayback()
        {
            if (!_actionPlayback.IsPhasedMeleeActive || _animator == null)
                return;

            if (!_actionPlayback.HasPhasedMeleeSegmentEntered)
            {
                if (!TryMarkPhasedMeleeSegmentEntered())
                    return;
            }

            if (_actionPlayback.ActiveMeleePresentation.HasValue
                && _actionPlayback.ActiveMeleePresentation.GetValueOrDefault().IsPhased
                && !_actionPlayback.ActiveMeleePresentationEntered)
            {
                return;
            }

            if (!TryGetPhasedMeleePhaseNormalizedTime(out float normalizedTime))
            {
                TraceCombatAnimation(
                    $"phased-segment-lost phase={_actionPlayback.PhasedMeleePhase} " +
                    $"dispatchedFrame={_phasedMeleeSegmentDispatchedFrame} layer={DescribeMeleeLayer()}");
                CancelPhasedMeleePlayback();
                return;
            }

            if (_actionPlayback.IsPhasedMeleeSpecialMovementDriven)
            {
                UpdateSpecialMovementDrivenPhasedMeleePlayback(normalizedTime);
                return;
            }

            if (!_actionPlayback.TryResolvePhasedMeleeTransition(
                    normalizedTime,
                    PhasedMeleeStartOnlyEndTriggerNormalizedTime,
                    PhasedMeleeSegmentTransitionNormalizedTime,
                    PhasedMeleeEndCompleteNormalizedTime,
                    out PhasedMeleePlaybackPhase nextPhase,
                    out bool shouldCancel))
            {
                return;
            }

            if (shouldCancel)
            {
                CancelPhasedMeleePlayback();
                return;
            }

            AdvancePhasedMeleeSegment(nextPhase);
        }

        private void AdvancePhasedMeleeSegment(
            PhasedMeleePlaybackPhase nextPhase,
            bool blendFromPreviousSegment = false)
        {
            if (TryGetPhasedMeleePhaseNormalizedTime(out float normalizedTime))
                _actionPlayback.AddCompletedPhasedMeleePhaseSeconds(normalizedTime);
            if (!PlayPhasedMeleeSegment(nextPhase, 0f, blendFromPreviousSegment))
            {
                CancelPhasedMeleePlayback();
                return;
            }

        }

        public void RequestSpecialMovementDrivenPhasedMeleeEnd()
        {
            if (_animator == null || !_actionPlayback.RequestPhasedMeleeSpecialMovementEnd())
                return;

            if (_actionPlayback.PhasedMeleePhase == PhasedMeleePlaybackPhase.Loop
                && _actionPlayback.HasPhasedMeleeSegmentEntered)
            {
                AdvancePhasedMeleeSegment(
                    PhasedMeleePlaybackPhase.End,
                    blendFromPreviousSegment: _actionPlayback.IsPhasedMeleeSpecialMovementArrivalDriven);
            }
        }

        public bool RequestCombatLifecycleDrivenPhasedMeleeEnd(string actionId)
        {
            if (_animator == null
                || _animationSet == null
                || !_actionPlayback.ActiveMeleePresentation.HasValue)
            {
                return false;
            }

            if (!ActivePhasedMeleeMatches(actionId)
                || !_animationSet.TryGetPhasedMeleeEntry(actionId, out WeaponPhasedActionEntry entry)
                || !entry.drivePhasesFromCombatLifecycle
                || !_actionPlayback.RequestPhasedMeleeSpecialMovementEnd())
            {
                return false;
            }

            if (_actionPlayback.PhasedMeleePhase == PhasedMeleePlaybackPhase.Loop)
                AdvancePhasedMeleeSegment(PhasedMeleePlaybackPhase.End);
            return true;
        }

        /// The local predicted request stores the runtime slot id
        /// (MeleeInputHandler dispatches PredictedMeleeSkill(slotId)), while
        /// authoritative combat events carry the authored strike id. Compare
        /// them in one id-space or the local player's own channel end never
        /// matches its own presentation.
        private bool HasHeldPhasedMeleeLoopOutlivedItsCeiling(float normalizedTime)
        {
            return _actionPlayback.TryGetPhasedMeleePresentationTiming(
                       normalizedTime,
                       out float elapsedSeconds,
                       out _)
                   && elapsedSeconds >= PhasedMeleeHeldLoopMaxSeconds;
        }

        private bool ActivePhasedMeleeMatches(string actionId)
        {
            if (_animationSet == null || !_actionPlayback.ActiveMeleePresentation.HasValue)
                return false;

            ActiveMeleePresentation active = _actionPlayback.ActiveMeleePresentation.GetValueOrDefault();
            string activeAuthored = WireIdentifier.Normalize(
                _animationSet.ResolveAuthoredStrikeIdForRuntimeAction(active.ActionId));
            string incomingAuthored = WireIdentifier.Normalize(
                _animationSet.ResolveAuthoredStrikeIdForRuntimeAction(actionId));
            return !string.IsNullOrEmpty(activeAuthored)
                && string.Equals(activeAuthored, incomingAuthored, StringComparison.Ordinal);
        }

        public bool CancelPhasedMeleeAction(string actionId)
        {
            if (!_actionPlayback.IsPhasedMeleeActive
                || !_actionPlayback.ActiveMeleePresentation.HasValue)
            {
                return false;
            }

            ActiveMeleePresentation active = _actionPlayback.ActiveMeleePresentation.GetValueOrDefault();
            if (!active.IsPhased || !ActivePhasedMeleeMatches(actionId))
                return false;

            CancelPhasedMeleePlayback();
            return true;
        }

        private void UpdateSpecialMovementDrivenPhasedMeleePlayback(float normalizedTime)
        {
            if (_actionPlayback.PhasedMeleePhase == PhasedMeleePlaybackPhase.Loop
                && !_actionPlayback.IsPhasedMeleeSpecialMovementEndRequested
                && normalizedTime >= PhasedMeleeLoopReplayNormalizedTime
                && !HasHeldPhasedMeleeLoopOutlivedItsCeiling(normalizedTime))
            {
                // Banked strike states auto-exit at 0.9 (Arena_Character.controller),
                // and nothing else re-enters Loop while an external signal is holding
                // it open. Re-arm the authored Loop before that controller transition
                // can expose Empty. This applies to every held loop, not just an
                // arrival-driven dash: a combat-lifecycle channel holds Loop for as
                // long as the player keeps firing, which is far longer than a dash.
                AdvancePhasedMeleeSegment(PhasedMeleePlaybackPhase.Loop);
                return;
            }

            float startExitNormalizedTime = _actionPlayback.ResolvePhasedMeleeStartExitNormalizedTime(
                PhasedMeleeStartOnlyEndTriggerNormalizedTime,
                PhasedMeleeSegmentTransitionNormalizedTime);
            if (!CombatActionPlaybackController.TryResolveSpecialMovementDrivenPhasedTransition(
                    _actionPlayback.PhasedMeleePhase,
                    normalizedTime,
                    _actionPlayback.PhasedMeleeReleaseAfterStart,
                    _actionPlayback.IsPhasedMeleeSpecialMovementEndRequested,
                    startExitNormalizedTime,
                    startExitNormalizedTime,
                    PhasedMeleeEndCompleteNormalizedTime,
                    out PhasedMeleePlaybackPhase nextPhase,
                    out bool shouldCancel))
            {
                return;
            }

            if (shouldCancel)
            {
                CancelPhasedMeleePlayback();
                return;
            }

            AdvancePhasedMeleeSegment(
                nextPhase,
                blendFromPreviousSegment: nextPhase == PhasedMeleePlaybackPhase.End
                    && _actionPlayback.IsPhasedMeleeSpecialMovementArrivalDriven);
        }

        private bool PlayPhasedMeleeSegment(
            PhasedMeleePlaybackPhase phase,
            float normalizedTime,
            bool blendFromPreviousSegment = false)
        {
            if (_animator == null || _overrideController == null)
                return false;

            AnimationClip? clip = _actionPlayback.GetPhasedMeleeClip(phase);
            if (clip == null)
                return false;

            normalizedTime = Mathf.Clamp01(normalizedTime);

            if (_actionPlayback.IsPhasedMeleeUpperBodyMode)
            {
                _actionPlayback.SetPhasedMeleeSegment(
                    phase,
                    stateHash: 0,
                    clip.length,
                    normalizedTime);
                _overrideController[UpperBodyRecoverySlotName] = clip;
                PlayUpperBodyState(UpperBodyRecoveryAction1StateHash, normalizedTime);
                _phasedMeleeSegmentDispatchedFrame = Time.frameCount;
                TraceCombatAnimation(
                    $"phased-segment-dispatched phase={phase} clip={clip.name} " +
                    $"layer=upper-body normalized={normalizedTime:F3}");
                return true;
            }

            if (_actionPlayback.PhasedMeleeBankSlot <= 0)
                return false;

            if (!CombatActionPlaybackController.TryResolvePhasedMeleeLayerRoute(
                    _actionPlayback.PhasedMeleeBankSlot,
                    phase,
                    Strike1StateHash,
                    Strike2StateHash,
                    Strike3StateHash,
                    Strike4StateHash,
                    out int segmentBankSlot,
                    out int segmentStateHash))
            {
                return false;
            }

            _actionPlayback.SetPhasedMeleeSegment(
                phase,
                segmentStateHash,
                clip.length,
                normalizedTime);
            _actionPlayback.OverrideStrikeBankSlot(_overrideController, segmentBankSlot, clip);
            int fullPathStateHash = ResolveStrikeFullPathStateHash(segmentBankSlot);
            bool hasFullPathState = fullPathStateHash != 0 && _animator.HasState(MeleeAttackLayerIndex, fullPathStateHash);
            int playStateHash = hasFullPathState ? fullPathStateHash : segmentStateHash;
            if (blendFromPreviousSegment)
            {
                CombatActionPlaybackController.CrossFadeMeleeStrikeState(
                    _animator,
                    MeleeAttackLayerIndex,
                    playStateHash,
                    SpecialMovementArrivalEndCrossFadeDurationSeconds,
                    normalizedTime);
            }
            else
            {
                CombatActionPlaybackController.PlayMeleeStrikeState(
                    _animator,
                    MeleeAttackLayerIndex,
                    playStateHash,
                    normalizedTime);
            }
            _phasedMeleeSegmentDispatchedFrame = Time.frameCount;
            TraceCombatAnimation(
                $"phased-segment-dispatched phase={phase} clip={clip.name} " +
                $"state={DescribeStateHash(playStateHash)} blend={blendFromPreviousSegment} " +
                $"normalized={normalizedTime:F3}");
            return true;
        }

        private bool TryMarkPhasedMeleeSegmentEntered()
        {
            int layerIndex = _actionPlayback.IsPhasedMeleeUpperBodyMode
                ? UpperBodyLayerIndex
                : MeleeAttackLayerIndex;
            int expectedStateHash = _actionPlayback.IsPhasedMeleeUpperBodyMode
                ? UpperBodyRecoveryAction1StateHash
                : _actionPlayback.PhasedMeleeStateHash;
            if (!HasEnteredExpectedStateOnLayer(
                    _phasedMeleeSegmentDispatchedFrame,
                    layerIndex,
                    expectedStateHash))
            {
                return false;
            }

            _actionPlayback.MarkPhasedMeleeSegmentEntered();
            TraceCombatAnimation(
                $"phased-segment-entered phase={_actionPlayback.PhasedMeleePhase} " +
                $"dispatchedFrame={_phasedMeleeSegmentDispatchedFrame} currentFrame={Time.frameCount}");
            return true;
        }

        private bool TryGetPhasedMeleePresentationTiming(out float elapsedSeconds, out float stateLengthSeconds)
        {
            elapsedSeconds = 0f;
            stateLengthSeconds = Mathf.Max(0f, _actionPlayback.PhasedMeleeTotalLengthSeconds);
            if (!_actionPlayback.IsPhasedMeleeActive)
                return false;

            if (!TryGetPhasedMeleePhaseNormalizedTime(out float normalizedTime))
                return false;

            return _actionPlayback.TryGetPhasedMeleePresentationTiming(
                normalizedTime,
                out elapsedSeconds,
                out stateLengthSeconds);
        }

        private float GetPhasedMeleeCurrentNormalizedTime()
        {
            return TryGetPhasedMeleePhaseNormalizedTime(out float normalizedTime)
                ? Mathf.Clamp01(normalizedTime)
                : 0f;
        }

        private bool TryGetPhasedMeleePhaseNormalizedTime(out float normalizedTime)
        {
            normalizedTime = 0f;
            if (_animator == null || !_actionPlayback.IsPhasedMeleeActive)
                return false;

            int layerIndex = _actionPlayback.IsPhasedMeleeUpperBodyMode ? UpperBodyLayerIndex : MeleeAttackLayerIndex;
            int expectedStateHash = _actionPlayback.IsPhasedMeleeUpperBodyMode
                ? UpperBodyRecoveryAction1StateHash
                : _actionPlayback.PhasedMeleeStateHash;
            if (expectedStateHash == 0)
                return false;

            AnimatorStateInfo current = _animator.GetCurrentAnimatorStateInfo(layerIndex);
            if (current.shortNameHash == expectedStateHash)
            {
                normalizedTime = current.normalizedTime;
                return true;
            }

            if (_animator.IsInTransition(layerIndex))
            {
                AnimatorStateInfo next = _animator.GetNextAnimatorStateInfo(layerIndex);
                if (next.shortNameHash == expectedStateHash)
                {
                    normalizedTime = next.normalizedTime;
                    return true;
                }
            }

            return false;
        }

        private AnimationClip? GetCurrentPhasedMeleeClip() => _actionPlayback.GetCurrentPhasedMeleeClip();

        private void CancelPhasedMeleePlayback(bool clearActivePresentation = true)
        {
            bool wasPhasedActive = _actionPlayback.CancelPhasedMelee();
            _phasedMeleeSegmentDispatchedFrame = -1;
            if (clearActivePresentation
                && _actionPlayback.ActiveMeleePresentation.HasValue
                && _actionPlayback.ActiveMeleePresentation.Value.IsPhased
                && (_actionPlayback.ActiveBaseCombatAnimationCategory == CombatAnimationCategory.MeleeSkill
                    || _actionPlayback.ActiveBaseCombatAnimationCategory == CombatAnimationCategory.AutoAttack))
            {
                ClearActiveMeleePresentation();
            }
            if (wasPhasedActive)
                _animator?.Play(MeleeAttackEmptyStateHash, MeleeAttackLayerIndex, 0f);
        }

        private void EnsureOverrideController()
        {
            if (_animator == null)
                return;

            if (_overrideController != null)
            {
                if (!ReferenceEquals(_animator.runtimeAnimatorController, _overrideController))
                    _animator.runtimeAnimatorController = _overrideController;
                return;
            }

            // Wrap the base controller exactly once. Reassigning runtimeAnimatorController
            // resets the entire state machine, so this must never happen after init.
            _overrideController = new AnimatorOverrideController(_animator.runtimeAnimatorController);
            _animator.runtimeAnimatorController = _overrideController;
        }

        private void EnsureSharedActionProfileLoaded()
        {
            if (_sharedActionProfile != null)
                return;

            _sharedActionProfile = Resources.Load<SharedActionProfile>("ActionProfiles/SharedActions");
            if (_sharedActionProfile == null)
            {
                if (_animationSet?.dodge.HasAny != true && _animationSet?.airDodge.HasAny != true)
                {
                    Debug.LogWarning("[PlayerAnimator] Shared action profile 'ActionProfiles/SharedActions' was not found, and the active animation set has no dodge clips. Dodge will use controller defaults.");
                }
                return;
            }

            if (_overrideController != null)
            {
                ApplySharedActionOverrides(_wasGrounded, forceDodge: true);
            }
        }

        public void SetKnockedDown(bool isKnockedDown)
        {
            if (isKnockedDown)
                ClearWorldInteractionAnimation();
            StatusReactionController.Bind(_animator, _overrideController);
            StatusReactionController.SetKnockedDown(isKnockedDown);
        }

        public void SetHardCrowdControl(string? statusKind)
        {
            if (!string.IsNullOrWhiteSpace(statusKind))
                ClearWorldInteractionAnimation();
            StatusReactionController.Bind(_animator, _overrideController);
            StatusReactionController.SetHardCrowdControl(statusKind);
        }

        /// <summary>
        /// Fires a directional hit-reaction trigger based on the world-space direction
        /// FROM which the hit arrived. hitDirection should be normalized.
        /// </summary>
        public void TriggerHit(Vector3 hitDirection, Vector3 characterForward)
        {
            ClearWorldInteractionAnimation();
            StatusReactionController.Bind(_animator, _overrideController);
            StatusReactionController.TriggerHit(hitDirection, characterForward);
        }

        private void ClearNonDeathPresentation()
        {
            if (_animator == null)
                return;

            ClearWorldInteractionAnimation();
            StatusReactionController.Bind(_animator, _overrideController);
            StatusReactionController.ClearForNonDeath();
            ClearCombatActionPresentation(captureMeleeGhost: false, softSpellHoldClear: false);
            ClearDefensivePresentation();
        }

        public void TriggerStagger(Vector3 hitDirection, Vector3 characterForward)
        {
            ClearWorldInteractionAnimation();
            StatusReactionController.Bind(_animator, _overrideController);
            StatusReactionController.TriggerStagger(hitDirection, characterForward);
        }

        /// <summary>
        /// Cross-system cancellation for the denial reaction (netcode design
        /// review S2): cuts the predicted presentation of a server-rejected
        /// action. Reaction policy lives in
        /// <see cref="CombatStatusReactionController.TriggerPredictionRejected"/>;
        /// the identity gate lives in
        /// <see cref="CombatActionPlaybackController.ShouldCutRejectedActionPresentation"/>.
        /// </summary>
        public void CutRejectedActionPresentation(string rejectedActionId)
        {
            StatusReactionController.Bind(_animator, _overrideController);
            StatusReactionController.TriggerPredictionRejected(rejectedActionId);
        }

        /// <summary>
        /// The cut itself, composed exclusively from the existing
        /// interrupt/empty-state primitives and scoped by action identity so a
        /// rejection never eats playback owned by a later press. Invoked only
        /// through <see cref="CombatStatusReactionController"/>.
        /// </summary>
        private void CutRejectedActionPresentationScoped(string rejectedActionId)
        {
            if (_animator == null)
                return;

            if (_actionPlayback.ActiveMeleePresentation.HasValue
                && CombatActionPlaybackController.ShouldCutRejectedActionPresentation(
                    _actionPlayback.ActiveMeleePresentation.GetValueOrDefault().ActionId,
                    rejectedActionId))
            {
                // PreemptMeleeAnimationIfActive snaps the melee layer, but an
                // upper-body-mode phased windup plays on the upper-body slot.
                bool wasUpperBodyPhased = _actionPlayback.IsPhasedMeleeActive
                    && _actionPlayback.IsPhasedMeleeUpperBodyMode;
                PreemptMeleeAnimationIfActive(captureGhost: false);
                if (wasUpperBodyPhased)
                    PlayUpperBodyState(UpperBodyEmptyStateHash, 0f);
            }

            if (_actionPlayback.ActiveSpellPresentation.HasValue
                && CombatActionPlaybackController.ShouldCutRejectedActionPresentation(
                    _actionPlayback.ActiveSpellPresentation.GetValueOrDefault().ActionId,
                    rejectedActionId))
            {
                if (_weaponAttachments == null)
                    _weaponAttachments = GetComponent<WeaponAttachmentController>();
                _weaponAttachments?.ReleaseTemporaryAnimatedProp(rejectedActionId);
                ClearActiveSpellPresentation(resetLayerWeight: true, clearUpperBodySpell: true);
                _animator.Play(SpellActionEmptyStateHash, SpellActionLayerIndex, 0f);
            }

            if (_actionPlayback.ActiveOverlaySpellPresentation is { } overlay
                && CombatActionPlaybackController.ShouldCutRejectedActionPresentation(
                    overlay.ActionId,
                    rejectedActionId))
            {
                // The overlay record is never lifecycle-cleared, so only cut
                // while the recorded state is still what the layer is playing.
                int layerIndex = ResolveOverlaySpellLayerIndex(overlay.PlaybackLayer);
                bool statePlaying = _animator.GetCurrentAnimatorStateInfo(layerIndex).shortNameHash == overlay.StateHash
                    || (_animator.IsInTransition(layerIndex)
                        && _animator.GetNextAnimatorStateInfo(layerIndex).shortNameHash == overlay.StateHash);
                if (statePlaying)
                {
                    if (_weaponAttachments == null)
                        _weaponAttachments = GetComponent<WeaponAttachmentController>();
                    _weaponAttachments?.ReleaseTemporaryAnimatedProp(rejectedActionId);
                    _actionPlayback.ClearActiveOverlaySpellPresentation();
                    ClearOverlaySpellPresentation(overlay.PlaybackLayer);
                }
            }
        }

        private void ClearPresentationForStagger()
        {
            if (_animator == null)
                return;

            ClearCombatActionPresentation(captureMeleeGhost: true, softSpellHoldClear: false);
            ClearDefensivePresentation();
        }

        private void ClearPresentationForForcedStatus()
        {
            if (_animator == null)
                return;

            ClearCombatActionPresentation(captureMeleeGhost: false, softSpellHoldClear: false);
            ClearDefensivePresentation();
        }

        private void ClearCombatActionPresentation(bool captureMeleeGhost, bool softSpellHoldClear)
        {
            if (_animator == null)
                return;

            _pendingSpellHoldPulse = null;
            PreemptMeleeAnimationIfActive(captureMeleeGhost);
            CancelPhasedMeleePlayback();

            bool hadCastHold = _actionPlayback.ActiveSpellCastHoldPresentation.HasValue;
            // Start a requested smooth hold exit before clearing overlay state. The existing
            // layer guards then leave the fading layer alone until it reaches Empty.
            ClearActiveSpellCastHoldPresentation(
                clearAnimatorState: true,
                softFullBodyClear: softSpellHoldClear && hadCastHold);
            ClearActiveSpellPresentation(resetLayerWeight: true, clearUpperBodySpell: true);
            _actionPlayback.ClearActiveOverlaySpellPresentation();

            _animator.Play(MeleeAttackEmptyStateHash, MeleeAttackLayerIndex, 0f);
            if (!softSpellHoldClear || !hadCastHold)
                _animator.Play(SpellActionEmptyStateHash, SpellActionLayerIndex, 0f);
            PlayUpperBodyState(UpperBodyEmptyStateHash, 0f);
            ClearLeftGestureSpellPresentation();
            ClearRightGestureSpellPresentation();
        }

        private void ClearDefensivePresentation()
        {
            if (_animator == null)
                return;

            _blockingPresentationActive = false;
            _parryArmedPresentationActive = false;
            _animator.SetBool(IsBlockingHash, false);
            _animator.ResetTrigger(TriggerBlockStartHash);
            _animator.ResetTrigger(TriggerBlockHitHash);
            _animator.ResetTrigger(TriggerParryHitHash);
        }

        private void ResetPendingMeleeActionTriggers()
        {
            if (_animator == null)
                return;

            _animator.ResetTrigger(TriggerStrike1Hash);
            _animator.ResetTrigger(TriggerStrike2Hash);
            _animator.ResetTrigger(TriggerStrike3Hash);
            _animator.ResetTrigger(TriggerStrike4Hash);
        }

        private void ResetPendingSpellActionTriggers()
        {
            if (_animator == null)
                return;

            _animator.ResetTrigger(TriggerSpellAction1Hash);
            _animator.ResetTrigger(TriggerSpellAction2Hash);
            _animator.ResetTrigger(TriggerSpellAction3Hash);
            _animator.ResetTrigger(TriggerSpellAction4Hash);
        }

        private void Update()
        {
            if (_animator == null || _simState == null || !_simState.HasState) return;
            Animator animator = _animator;
            UpdateDodgePlaybackPhase();

            bool grounded = _simState.IsGrounded;
            LocalPlayerMotor? motor = ResolveLocalPlayerMotor();
            if (motor != null)
                grounded = motor.IsGrounded;

            bool presentationGrounded = ResolvePresentationGrounded(grounded);
            LocomotionSample locomotion = UpdateLocomotion();
            UpdateMovementOneShots(locomotion, presentationGrounded);
            UpdateWeaponVisualHandoff();
            UpdateWorldInteractionAnimation();
            UpdatePhasedMeleePlayback();
            UpdateSpellCastHoldPlayback();
            UpdateSpellCastHoldPulse();
            UpdateSpellCastHoldFadeOut();
            UpdateMeleeLowerBodyUnlock();
            UpdateSpellLowerBodyUnlock();
            TracePendingMeleeEntry();
            // Latch the "entered animator state" flag the first frame after a presentation
            // is set. Without this latch the cleanup below races the animator: SetTrigger
            // is called during another script's Update, but the animator doesn't process
            // the trigger until after every Update has run, so a same-frame cleanup would
            // observe Empty on the melee layer and discard the freshly-cached identity
            // before the strike ever begins.
            if (_actionPlayback.ActiveMeleePresentation.HasValue
                && !_actionPlayback.ActiveMeleePresentationEntered
                && HasActiveMeleePresentationEnteredExpectedState())
            {
                _actionPlayback.MarkActiveMeleePresentationEntered();
            }
            if (_actionPlayback.ActiveSpellPresentation.HasValue
                && !_actionPlayback.ActiveSpellPresentationEntered
                && HasActiveSpellPresentationEnteredExpectedState())
            {
                _actionPlayback.MarkActiveSpellPresentationEntered();
            }

            UpdateMeleeAnimationVfx();

            if (_actionPlayback.ActiveBaseCombatAnimationCategory == CombatAnimationCategory.AutoAttack
                && _actionPlayback.ActiveMeleePresentationEntered
                && !IsMeleePresentationStateActive())
            {
                ClearActiveMeleePresentation();
            }
            else if (_actionPlayback.ActiveBaseCombatAnimationCategory == CombatAnimationCategory.MeleeSkill
                && _actionPlayback.ActiveMeleePresentationEntered
                && !IsMeleePresentationStateActive()
                && !_actionPlayback.IsPhasedMeleeActive)
            {
                ClearActiveMeleePresentation();
            }
            if (_actionPlayback.ActiveSpellPresentationEntered && !IsActiveSpellPresentationStateActive())
            {
                bool clearCastHoldAfterOverlayRelease = _actionPlayback.ActiveSpellCastHoldPresentation.HasValue;
                ClearActiveSpellPresentation(resetLayerWeight: true, clearUpperBodySpell: true);
                if (clearCastHoldAfterOverlayRelease)
                    ClearActiveSpellCastHoldPresentation(clearAnimatorState: true, softFullBodyClear: true);
            }
            RecoverLocomotionFromTransientStates(locomotion, presentationGrounded);

            animator.SetBool(GroundedHash, presentationGrounded);
            animator.SetBool(JumpHash, _wasGrounded && !presentationGrounded);
            animator.SetBool(FreeFallHash, !presentationGrounded);
            _wasGrounded = presentationGrounded;
        }

        private void OnDisable()
        {
            _meleeAnimationVfxPlayer?.Clear();
        }

        private void UpdateMeleeAnimationVfx()
        {
            if (_meleeAnimationVfxPlayer == null
                || !_actionPlayback.ActiveMeleePresentation.HasValue
                || !_actionPlayback.ActiveMeleePresentationEntered)
            {
                return;
            }

            ActiveMeleePresentation active =
                _actionPlayback.ActiveMeleePresentation.GetValueOrDefault();
            AnimationClip? clip;
            float normalizedTime;
            if (active.IsPhased)
            {
                clip = GetCurrentPhasedMeleeClip();
                normalizedTime = GetPhasedMeleeCurrentNormalizedTime();
            }
            else
            {
                clip = _animationSet?.GetStrikeClip(active.StrikeIndex);
                if (!TryGetActiveMeleePresentationTiming(
                        active,
                        out _,
                        out _,
                        out normalizedTime))
                {
                    return;
                }
            }

            if (clip == null)
                return;

            float sampledNormalizedTime = Mathf.Max(0f, normalizedTime);
            _meleeAnimationVfxPlayer.Update(
                clip,
                sampledNormalizedTime * Mathf.Max(0f, clip.length));
        }

        private WeaponAttachmentController? ResolveWeaponAttachments()
        {
            if (_weaponAttachments == null)
                _weaponAttachments = GetComponent<WeaponAttachmentController>();
            return _weaponAttachments;
        }

        private static bool ResolvePresentationGrounded(bool gameplayGrounded) => gameplayGrounded;

        private void BeginWeaponHandoff(
            bool targetInCombat,
            int layerIndex,
            int stateHash)
        {
            _pendingWeaponHandoffTargetInCombat = targetInCombat;
            _pendingWeaponHandoffLayerIndex = layerIndex;
            _pendingWeaponHandoffStateHash = stateHash;
            _weaponHandoffStateEntered = false;
        }

        private void ClearPendingWeaponHandoff()
        {
            _pendingWeaponHandoffLayerIndex = -1;
            _pendingWeaponHandoffStateHash = 0;
            _pendingWeaponHandoffTargetInCombat = false;
            _weaponHandoffStateEntered = false;
        }

        private bool HasPendingWeaponHandoffFor(bool targetInCombat)
        {
            return _pendingWeaponHandoffLayerIndex >= 0
                && _pendingWeaponHandoffTargetInCombat == targetInCombat;
        }

        private void UpdateWeaponVisualHandoff()
        {
            if (_pendingWeaponHandoffLayerIndex < 0 || _animator == null)
                return;

            if (_weaponAttachments == null)
                _weaponAttachments = GetComponent<WeaponAttachmentController>();
            if (_weaponAttachments == null || _animationSet == null)
                return;

            int layerIndex = _pendingWeaponHandoffLayerIndex;
            AnimatorStateInfo state = _animator.GetCurrentAnimatorStateInfo(layerIndex);
            if (state.shortNameHash != _pendingWeaponHandoffStateHash)
            {
                if (!_weaponHandoffStateEntered)
                    return;

                if (!_animator.IsInTransition(layerIndex))
                {
                    bool handoffTargetInCombat = _pendingWeaponHandoffTargetInCombat;
                    _weaponAttachments.SetInCombat(handoffTargetInCombat);
                    ClearPendingWeaponHandoff();
                }
                return;
            }

            _weaponHandoffStateEntered = true;
            float normalizedTime = Mathf.Clamp01(state.normalizedTime);
            bool targetInCombat = _pendingWeaponHandoffTargetInCombat;
            if (_weaponAttachments.ApplyTransitionProgress(targetInCombat, normalizedTime))
            {
                ClearPendingWeaponHandoff();
            }
        }

        private void RecoverLocomotionFromTransientStates(LocomotionSample locomotion, bool grounded)
        {
            if (_animator == null || _animator.IsInTransition(0))
                return;

            AnimatorStateInfo state = _animator.GetCurrentAnimatorStateInfo(0);
            if (!TryResolveLocomotionRecovery(
                    state.shortNameHash,
                    _inCombat,
                    grounded,
                    locomotion.IsMoving,
                    ResolveMovementActionLocomotionUnlockGate(state.shortNameHash),
                    ResolveDrawRecoveryGate(),
                    ResolveSheathRecoveryGate(),
                    out int targetStateHash,
                    out float minNormalizedTime))
            {
                return;
            }

            if (state.normalizedTime < minNormalizedTime)
                return;

            _animator.CrossFadeInFixedTime(targetStateHash, LocomotionRecoveryCrossFadeDurationSeconds, 0, 0f);
            if (state.shortNameHash == DodgeStateHash)
                _activeMovementPresentation = null;
        }

        private void PlayUpperBodyState(int stateHash, float normalizedTime)
        {
            if (!CanDriveAnimatorState())
                return;

            if (TryHandleSpellCastHoldFadeOutBeforeLayerPlay(UpperBodyLayerIndex, stateHash, UpperBodyEmptyStateHash))
                return;

            _animator!.Play(stateHash, UpperBodyLayerIndex, normalizedTime);
        }

        private void PlayLeftGestureState(int stateHash, float normalizedTime)
        {
            if (!CanDriveAnimatorState())
                return;

            CancelSpellCastHoldFadeOutForLayer(LeftGestureLayerIndex);
            _animator!.SetLayerWeight(LeftGestureLayerIndex, 1f);
            _animator!.Play(stateHash, LeftGestureLayerIndex, normalizedTime);
        }

        private void PlayRightGestureState(int stateHash, float normalizedTime)
        {
            if (!CanDriveAnimatorState())
                return;

            CancelSpellCastHoldFadeOutForLayer(RightGestureLayerIndex);
            _animator!.SetLayerWeight(RightGestureLayerIndex, 1f);
            _animator!.Play(stateHash, RightGestureLayerIndex, normalizedTime);
        }

        private bool TryHandleSpellCastHoldFadeOutBeforeLayerPlay(
            int layerIndex,
            int stateHash,
            int emptyStateHash)
        {
            if (!_actionPlayback.IsSpellCastHoldFadeOutActive
                || _actionPlayback.SpellCastHoldFadeOutLayerIndex != layerIndex)
            {
                return false;
            }

            // Empty-state cleanup should not stomp a hold fade that is smoothing this layer back
            // to neutral. The fade parks the layer on Empty when it completes.
            if (stateHash == emptyStateHash)
                return true;

            CancelSpellCastHoldFadeOutForLayer(layerIndex);
            return false;
        }

        private void CancelSpellCastHoldFadeOutForLayer(int layerIndex)
        {
            if (!_actionPlayback.IsSpellCastHoldFadeOutActive
                || _actionPlayback.SpellCastHoldFadeOutLayerIndex != layerIndex)
            {
                return;
            }

            _actionPlayback.ResetSpellCastHoldFadeOut();
            _animator!.SetLayerWeight(layerIndex, 1f);
        }

        private void ClearLeftGestureSpellPresentation()
        {
            if (!CanDriveAnimatorState())
                return;

            // Never hard-play Empty on the LeftGesture layer while a masked cast hold is blending
            // it out — the hold fade owns the layer weight and parks it on Empty when the blend
            // completes. Stomping here (e.g. from the preempt/clear cleanup) would snap the pose
            // the fade is meant to smooth. This is the single guard for that; callers don't repeat it.
            if (_actionPlayback.IsSpellCastHoldFadeOutActive
                && _actionPlayback.SpellCastHoldFadeOutLayerIndex == LeftGestureLayerIndex)
                return;

            _animator!.Play(UpperBodyEmptyStateHash, LeftGestureLayerIndex, 0f);
        }

        private void ClearRightGestureSpellPresentation()
        {
            if (!CanDriveAnimatorState())
                return;

            if (_actionPlayback.IsSpellCastHoldFadeOutActive
                && _actionPlayback.SpellCastHoldFadeOutLayerIndex == RightGestureLayerIndex)
                return;

            _animator!.Play(UpperBodyEmptyStateHash, RightGestureLayerIndex, 0f);
        }

        private static int ResolveOverlaySpellLayerIndex(SpellPlaybackLayer playbackLayer)
        {
            return playbackLayer switch
            {
                SpellPlaybackLayer.LeftGesture => LeftGestureLayerIndex,
                SpellPlaybackLayer.RightGesture => RightGestureLayerIndex,
                _ => UpperBodyLayerIndex,
            };
        }

        private void ClearOverlaySpellPresentation(SpellPlaybackLayer playbackLayer)
        {
            switch (playbackLayer)
            {
                case SpellPlaybackLayer.LeftGesture:
                    ClearLeftGestureSpellPresentation();
                    break;
                case SpellPlaybackLayer.RightGesture:
                    ClearRightGestureSpellPresentation();
                    break;
                default:
                    PlayUpperBodyState(UpperBodyEmptyStateHash, 0f);
                    break;
            }
        }

        private bool CanDriveAnimatorState()
        {
            return _animator != null
                && _animator.isActiveAndEnabled
                && _animator.gameObject.activeInHierarchy;
        }

        private CombatStanceTransitionBand ResolveCombatStanceTransitionBand()
        {
            if (!IsCurrentlyGrounded() || _latestLocomotionRawMagnitude < StopTriggerThreshold)
                return CombatStanceTransitionBand.Idle;

            return _latestLocomotionRawMagnitude >= RunStopThreshold
                ? CombatStanceTransitionBand.Run
                : CombatStanceTransitionBand.Walk;
        }

        private static bool TryResolveCombatStanceTransitionStateHash(
            bool enteringCombat,
            int locomotionBandIndex,
            bool hasIdleTransition,
            bool hasWalkTransition,
            bool hasRunTransition,
            out int stateHash)
        {
            stateHash = 0;
            CombatStanceTransitionBand band = (CombatStanceTransitionBand)Mathf.Clamp(
                locomotionBandIndex,
                (int)CombatStanceTransitionBand.Idle,
                (int)CombatStanceTransitionBand.Run);

            switch (band)
            {
                case CombatStanceTransitionBand.Idle when hasIdleTransition:
                    stateHash = enteringCombat ? EnterCombatIdleStateHash : ExitCombatIdleStateHash;
                    return true;
                case CombatStanceTransitionBand.Walk when hasWalkTransition:
                    stateHash = enteringCombat ? EnterCombatWalkStateHash : ExitCombatWalkStateHash;
                    return true;
                case CombatStanceTransitionBand.Run when hasRunTransition:
                    stateHash = enteringCombat ? EnterCombatRunStateHash : ExitCombatRunStateHash;
                    return true;
                default:
                    return false;
            }
        }

        private float ResolveDrawRecoveryGate()
        {
            if (_pendingWeaponHandoffLayerIndex == BaseLayerIndex && _pendingWeaponHandoffStateHash == DrawWeaponStateHash)
                return 1.01f;

            return WeaponTransitionRecoveryMinNormalizedTime;
        }

        private float ResolveSheathRecoveryGate()
        {
            if (_pendingWeaponHandoffLayerIndex == BaseLayerIndex && _pendingWeaponHandoffStateHash == SheathWeaponStateHash)
                return 1.01f;

            return WeaponTransitionRecoveryMinNormalizedTime;
        }

        private float ResolveMovementActionLocomotionUnlockGate(int stateHash)
        {
            if (stateHash != DodgeStateHash)
                return 1.01f;

            if (!_activeMovementPresentation.HasValue || !_activeMovementPresentation.Value.IsDodge)
                return 1.01f;

            long nowMs = ResolveAuthoritativePresentationNowMs();
            ActiveMovementActionPresentation active = _activeMovementPresentation.Value;
            if (nowMs < active.RecoveryUntilMs)
                return 1.01f;

            return 0f;
        }

        private void UpdateDodgePlaybackPhase()
        {
            if (_animator == null
                || !_activeMovementPresentation.HasValue
                || !_activeMovementPresentation.Value.IsDodge)
            {
                return;
            }

            ActiveMovementActionPresentation active = _activeMovementPresentation.Value;
            _animator.SetFloat(
                DodgePhaseHash,
                ResolveMovementActionPhase(
                    ResolveAuthoritativePresentationNowMs(),
                    active.StartedAtMs,
                    active.ActiveUntilMs,
                    active.RecoveryUntilMs,
                    _activeDodgeStartNormalized,
                    _activeDodgeTravelEndNormalized,
                    _activeDodgeClipLengthSeconds));
        }

        private static long ResolveAuthoritativePresentationNowMs()
        {
            return ArenaServerClock.HasEstimate
                ? ArenaServerClock.ServerNowMs
                : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        private static float ResolveMovementActionPhase(
            long nowMs,
            long startedAtMs,
            long activeUntilMs,
            long recoveryUntilMs,
            float startNormalized,
            float authoredTravelEndNormalized,
            float clipLengthSeconds)
        {
            long resolvedActiveUntilMs = Math.Max(startedAtMs, activeUntilMs);
            long resolvedRecoveryUntilMs = Math.Max(resolvedActiveUntilMs, recoveryUntilMs);
            if (resolvedRecoveryUntilMs <= startedAtMs)
                return 1f;

            float resolvedStart = Mathf.Clamp01(startNormalized);
            bool hasRecovery = resolvedRecoveryUntilMs > resolvedActiveUntilMs;
            float resolvedTravelEnd;
            if (!hasRecovery)
            {
                resolvedTravelEnd = 1f;
            }
            else if (!float.IsNaN(authoredTravelEndNormalized)
                     && !float.IsInfinity(authoredTravelEndNormalized)
                     && authoredTravelEndNormalized >= 0f)
            {
                resolvedTravelEnd = Mathf.Clamp(
                    authoredTravelEndNormalized,
                    resolvedStart,
                    1f);
            }
            else
            {
                double activeDurationMs = resolvedActiveUntilMs - startedAtMs;
                double totalDurationMs = resolvedRecoveryUntilMs - startedAtMs;
                float activeShare = totalDurationMs > 0d
                    ? Mathf.Clamp01((float)(activeDurationMs / totalDurationMs))
                    : 1f;
                resolvedTravelEnd = Mathf.Lerp(resolvedStart, 1f, activeShare);
            }

            if (nowMs <= startedAtMs)
                return resolvedStart;

            if (nowMs <= resolvedActiveUntilMs)
            {
                long activeDurationMs = resolvedActiveUntilMs - startedAtMs;
                if (activeDurationMs <= 0L)
                    return resolvedTravelEnd;

                float activeProgress = Mathf.Clamp01(
                    (float)((double)(nowMs - startedAtMs) / activeDurationMs));
                return Mathf.Lerp(resolvedStart, resolvedTravelEnd, activeProgress);
            }

            float resolvedClipLengthSeconds =
                !float.IsNaN(clipLengthSeconds) && !float.IsInfinity(clipLengthSeconds)
                    ? Mathf.Max(0f, clipLengthSeconds)
                    : 0f;
            if (resolvedClipLengthSeconds <= 0.0001f)
                return 1f;

            double recoveryElapsedSeconds =
                (nowMs - resolvedActiveUntilMs) / 1000d;
            float normalizedRecoveryAdvance = (float)(
                recoveryElapsedSeconds
                * DodgeRecoveryPlaybackSpeed
                / resolvedClipLengthSeconds);
            return Mathf.Clamp01(
                resolvedTravelEnd + normalizedRecoveryAdvance);
        }

        private LocomotionSample UpdateLocomotion()
        {
            // Lazy-resolve: LocalPlayerStateProvider is added after PlayerAnimator.Initialize()
            // by SetupLocalPlayer, so GetComponent at Initialize time returns null.
            if (_isLocalPlayer && _stateProvider == null)
                _stateProvider = GetComponent<LocalPlayerStateProvider>();

            Vector3 worldVel = GetWorldVelocity();

            // Build facing basis from predicted yaw for local player;
            // transform.forward/right for remote (driven by server state).
            Vector3 fwd, right;
            if (_isLocalPlayer && _stateProvider != null && _stateProvider.HasPredictedState)
            {
                float yaw = _stateProvider.PredictedFacingYaw;
                fwd   = new Vector3(Mathf.Sin(yaw), 0f,  Mathf.Cos(yaw));
                right = new Vector3(Mathf.Cos(yaw), 0f, -Mathf.Sin(yaw));
            }
            else
            {
                fwd   = transform.forward;
                right = transform.right;
            }

            float targetVelX = Mathf.Clamp(Vector3.Dot(worldVel, right) / BaseRunSpeed, -1f, 1f);
            float targetVelZ = Mathf.Clamp(Vector3.Dot(worldVel, fwd)   / BaseRunSpeed, -1f, 1f);
            float rawMagnitude = new Vector2(targetVelX, targetVelZ).magnitude;
            float facingYawDegrees = Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;

            bool useForwardOnlyLocomotion = _isLocalPlayer &&
                _localPlayerMotor != null &&
                !_localPlayerMotor.UsesDirectionalLocomotion;
            if (useForwardOnlyLocomotion)
            {
                targetVelX = 0f;
                rawMagnitude = Mathf.Abs(targetVelZ);
            }

            // Dead zone: suppress blend tree "swimming" at near-zero velocity
            if (rawMagnitude < MovementDeadZone)
                targetVelX = targetVelZ = 0f;

            // k=10 → ~100ms to settle. Snappy without jitter.
            float t = 1f - Mathf.Exp(-10f * Time.deltaTime);
            _smoothVelX = Mathf.Lerp(_smoothVelX, targetVelX, t);
            _smoothVelZ = Mathf.Lerp(_smoothVelZ, targetVelZ, t);

            float smoothMag = new Vector2(_smoothVelX, _smoothVelZ).magnitude;
            _latestLocomotionRawMagnitude = rawMagnitude;

            _animator!.SetFloat(VelocityXHash,   _smoothVelX);
            _animator!.SetFloat(VelocityZHash,   _smoothVelZ);
            _animator!.SetFloat(MotionSpeedHash, smoothMag);

            return new LocomotionSample(targetVelX, targetVelZ, rawMagnitude, smoothMag, facingYawDegrees);
        }

        private Vector3 GetWorldVelocity()
        {
            // Always advance _prevPosition so remote player fallback stays current.
            Vector3 pos = (_motionSource ?? transform).position;
            Vector3 posDelta = (pos - _prevPosition) / Mathf.Max(Time.deltaTime, 0.001f);
            _prevPosition = pos;
            posDelta.y = 0f;

            // Local player: use prediction system velocity — avoids reconciliation spikes.
            // Note: _stateProvider already resolved by UpdateLocomotion before this is called.
            if (_isLocalPlayer && _stateProvider != null && _stateProvider.HasPredictedState)
            {
                var vel = _stateProvider.PredictedVelocity;
                vel.y = 0f;
                return vel;
            }

            // Remote player: position delta (server-interpolated, smooth enough).
            return posDelta;
        }

        private void UpdateMovementOneShots(LocomotionSample locomotion, bool grounded)
        {
            if (_animator == null) return;

            bool jumpStarted = _wasGrounded && !grounded;
            bool landed = !_wasGrounded && grounded;
            Vector2 jumpLatchDirection = ResolveJumpLatchDirection(locomotion);

            if (locomotion.IsMoving)
            {
                _lastMoveVelX = locomotion.velocityX;
                _lastMoveVelZ = locomotion.velocityZ;
                _lastMoveWasRun = locomotion.rawMagnitude >= RunStopThreshold;
            }

            if (jumpStarted)
            {
                LatchJumpDirection(jumpLatchDirection);
                ForceDirectionalJumpRestartIfNeeded();
            }

            if (landed)
            {
                LatchJumpDirection(jumpLatchDirection);
            }

            _wasMoving = locomotion.IsMoving;
            _lastFacingYawDegrees = locomotion.facingYawDegrees;
        }

        private Vector2 ResolveJumpLatchDirection(LocomotionSample locomotion)
        {
            if (_isLocalPlayer && _localPlayerMotor != null)
            {
                Vector2 intentDirection = new(_localPlayerMotor.CurrentIntentStrafe, _localPlayerMotor.CurrentIntentForward);
                if (intentDirection.sqrMagnitude > 0.0001f)
                    return intentDirection;
            }

            return new Vector2(locomotion.velocityX, locomotion.velocityZ);
        }

        private void LatchJumpDirection(Vector2 direction)
        {
            if (_animator == null) return;
            Vector2 latched = SnapToCardinalOrCenter(direction, Vector2.zero);
            _animator.SetFloat(JumpXHash, latched.x);
            _animator.SetFloat(JumpZHash, latched.y);
        }

        private void ForceDirectionalJumpRestartIfNeeded()
        {
            if (_animator == null || _overrideController == null)
                return;

            if (!IsLandingStateActive())
                return;

            int targetStateHash = _inCombat ? JumpStartCombatStateHash : JumpStartStateHash;
            _animator.CrossFadeInFixedTime(targetStateHash, RejumpCrossFadeDurationSeconds, 0, 0f);
        }

        private bool IsLandingStateActive()
        {
            if (_animator == null)
                return false;

            AnimatorStateInfo current = _animator.GetCurrentAnimatorStateInfo(0);
            if (IsLandingState(current.shortNameHash))
                return true;

            if (_animator.IsInTransition(0))
            {
                AnimatorStateInfo next = _animator.GetNextAnimatorStateInfo(0);
                if (IsLandingState(next.shortNameHash))
                    return true;
            }

            return false;
        }

        private static bool IsLandingState(int shortNameHash)
        {
            return shortNameHash == JumpLandStateHash || shortNameHash == JumpLandCombatStateHash;
        }

        private static bool TryResolveLocomotionRecovery(
            int stateHash,
            bool inCombat,
            bool grounded,
            bool isMoving,
            float movementActionLocomotionUnlockGate,
            float drawRecoveryGate,
            float sheathRecoveryGate,
            out int targetStateHash,
            out float minNormalizedTime)
        {
            targetStateHash = 0;
            minNormalizedTime = 0f;

            if (!grounded || !isMoving)
                return false;

            if (stateHash == JumpLandStateHash || stateHash == JumpLandCombatStateHash)
            {
                targetStateHash = inCombat ? IdleCombatStateHash : IdleWalkRunBlendStateHash;
                minNormalizedTime = LandingRecoveryMinNormalizedTime;
                return true;
            }

            if (stateHash == DodgeStateHash)
            {
                targetStateHash = inCombat ? IdleCombatStateHash : IdleWalkRunBlendStateHash;
                minNormalizedTime = Mathf.Clamp01(movementActionLocomotionUnlockGate);
                return true;
            }

            if (stateHash == DrawWeaponStateHash)
            {
                targetStateHash = IdleCombatStateHash;
                minNormalizedTime = Mathf.Clamp01(drawRecoveryGate);
                return true;
            }

            if (stateHash == SheathWeaponStateHash)
            {
                targetStateHash = IdleWalkRunBlendStateHash;
                minNormalizedTime = Mathf.Clamp01(sheathRecoveryGate);
                return true;
            }

            return false;
        }
        private static bool ShouldUseMovingBlockLoopTransition(
            bool grounded,
            float locomotionRawMagnitude,
            bool hasDirectionalBlockLoop)
        {
            return grounded
                && hasDirectionalBlockLoop
                && locomotionRawMagnitude >= StopTriggerThreshold;
        }

        private static Vector2 SnapToCardinalOrCenter(Vector2 value, Vector2 fallback)
        {
            if (value.sqrMagnitude < 0.0001f)
                value = fallback;

            if (value.sqrMagnitude < 0.04f)
                return Vector2.zero;

            if (Mathf.Abs(value.x) >= Mathf.Abs(value.y))
                return new Vector2(Mathf.Sign(value.x), 0f);

            return new Vector2(0f, Mathf.Sign(value.y));
        }

        private static Vector2 SnapToCompass(Vector2 value)
        {
            if (value.sqrMagnitude < 0.0001f)
                return Vector2.up;

            Vector2 normalized = value.normalized;
            Vector2[] directions =
            {
                Vector2.up,
                new Vector2(Mathf.Sqrt(0.5f), Mathf.Sqrt(0.5f)),
                Vector2.right,
                new Vector2(Mathf.Sqrt(0.5f), -Mathf.Sqrt(0.5f)),
                Vector2.down,
                new Vector2(-Mathf.Sqrt(0.5f), -Mathf.Sqrt(0.5f)),
                Vector2.left,
                new Vector2(-Mathf.Sqrt(0.5f), Mathf.Sqrt(0.5f)),
            };

            float bestDot = float.NegativeInfinity;
            Vector2 best = Vector2.up;
            for (int i = 0; i < directions.Length; i++)
            {
                float dot = Vector2.Dot(normalized, directions[i]);
                if (dot > bestDot)
                {
                    bestDot = dot;
                    best = directions[i];
                }
            }

            return best;
        }

        private float GetFacingYawDegrees()
        {
            if (_isLocalPlayer && _stateProvider != null && _stateProvider.HasPredictedState)
                return _stateProvider.PredictedFacingYaw * Mathf.Rad2Deg;

            return transform.eulerAngles.y;
        }
    }

}
