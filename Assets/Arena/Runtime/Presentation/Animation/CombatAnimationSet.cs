#nullable enable
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;
using Arena.Combat;

namespace Arena.Presentation
{
    public static class CombatAnimationEvents
    {
        public const string OnReleaseFrame = "OnReleaseFrame";
        public const string OnInstantCastStart = "OnInstantCastStart";
        public const string OnDodgeStart = "OnDodgeStart";
        public const string OnDodgeTravelEnd = "OnDodgeTravelEnd";
        public const string OnEnterComplete = "OnEnterComplete";
        public const string OnHoldFadeStart = "OnHoldFadeStart";
        public const string OnHoldFadeEnd = "OnHoldFadeEnd";
        public const string OnLowerBodyUnlock = "OnLowerBodyUnlock";
        public const string OnVisualInterruptible = "OnVisualInterruptible";
        public const string OnStrikeHit = "OnStrikeHit";
        public const string OnPhaseLoopReady = "OnPhaseLoopReady";
        public const string OnGroundedFrame = "OnGroundedFrame";
        public const string OnParryWindowStart = "OnParryWindowStart";
        public const string OnParryWindowEnd = "OnParryWindowEnd";
        public const string OnBlockReady = "OnBlockReady";
        public const string OnWeaponHandoff = "OnWeaponHandoff";
        public const float PhasedMeleeStartOnlyEndSafetyNormalizedTime = 0.82f;
        public const float PhasedMeleeStartToLoopSafetyNormalizedTime = 0.84f;

        public static bool TryGetEventTime(AnimationClip? clip, string functionName, out float seconds)
        {
            seconds = 0f;
            if (clip == null || string.IsNullOrWhiteSpace(functionName))
                return false;

            AnimationEvent[] events = clip.events;
            if (events == null || events.Length == 0)
                return false;

            bool found = false;
            for (int i = 0; i < events.Length; i++)
            {
                AnimationEvent animationEvent = events[i];
                if (!string.Equals(animationEvent.functionName, functionName, StringComparison.Ordinal))
                    continue;

                float eventSeconds = Mathf.Clamp(animationEvent.time, 0f, Mathf.Max(0f, clip.length));
                if (!found || eventSeconds < seconds)
                    seconds = eventSeconds;
                found = true;
            }

            return found;
        }

        public static float GetEventTimeOrFallback(AnimationClip? clip, string functionName, float fallbackSeconds)
            => TryGetEventTime(clip, functionName, out float seconds) ? seconds : fallbackSeconds;

        public static bool TryGetEventNormalizedTime(
            AnimationClip? clip,
            string functionName,
            out float normalizedTime)
        {
            normalizedTime = 0f;
            if (clip == null
                || clip.length <= 0.001f
                || !TryGetEventTime(clip, functionName, out float seconds))
            {
                return false;
            }

            normalizedTime = Mathf.Clamp01(seconds / clip.length);
            return true;
        }

        public static float GetRequiredEventTimeOrFallback(
            AnimationClip? clip,
            string functionName,
            float fallbackSeconds,
            string context)
        {
            if (TryGetEventTime(clip, functionName, out float seconds))
                return seconds;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            string clipName = clip != null ? clip.name : "<missing clip>";
            Debug.LogError(
                $"[CombatAnimationEvents] Missing required animation event '{functionName}' on {clipName} for {context}. Using conservative fallback {fallbackSeconds:0.000}s.");
#endif
            return Mathf.Max(0f, fallbackSeconds);
        }

        public static bool AppendEventTimes(
            AnimationClip? clip,
            string functionName,
            List<float> destination,
            float offsetSeconds = 0f,
            float maxLocalTimeSeconds = float.PositiveInfinity)
        {
            if (clip == null || destination == null || string.IsNullOrWhiteSpace(functionName))
                return false;

            AnimationEvent[] events = clip.events;
            if (events == null || events.Length == 0)
                return false;

            int initialCount = destination.Count;
            float maxClipTime = Mathf.Max(0f, clip.length);
            float maxAcceptedTime = Mathf.Min(maxClipTime, Mathf.Max(0f, maxLocalTimeSeconds));
            for (int i = 0; i < events.Length; i++)
            {
                AnimationEvent animationEvent = events[i];
                if (!string.Equals(animationEvent.functionName, functionName, StringComparison.Ordinal))
                    continue;

                float eventTime = Mathf.Clamp(animationEvent.time, 0f, maxClipTime);
                if (eventTime > maxAcceptedTime)
                    continue;

                destination.Add(Mathf.Max(0f, offsetSeconds) + eventTime);
            }

            return destination.Count > initialCount;
        }
    }

    /// <summary>
    /// 8-direction clip set using compass naming (N/NE/E/SE/S/SW/W/NW).
    /// One instance per locomotion tier (walk, walkCombat, run, runCombat).
    /// </summary>
    [Serializable]
    public struct DirectionalClipSet
    {
        public AnimationClip? n;
        public AnimationClip? ne;
        public AnimationClip? e;
        public AnimationClip? se;
        public AnimationClip? s;
        public AnimationClip? sw;
        public AnimationClip? w;
        public AnimationClip? nw;

        public bool HasAny =>
            n != null || ne != null || e != null || se != null ||
            s != null || sw != null || w != null || nw != null;
    }

    /// <summary>
    /// 4 cardinal directions plus a center/default clip.
    /// Used for jump start/land where the pack only provides neutral + cardinal variants.
    /// </summary>
    [Serializable]
    public struct CardinalClipSet
    {
        public AnimationClip? center;
        public AnimationClip? n;
        public AnimationClip? e;
        public AnimationClip? s;
        public AnimationClip? w;
    }

    public readonly struct HitReactionClipSet
    {
        public HitReactionClipSet(
            AnimationClip? forward,
            AnimationClip? back,
            AnimationClip? left,
            AnimationClip? right)
        {
            Forward = forward;
            Back = back;
            Left = left;
            Right = right;
        }

        public AnimationClip? Forward { get; }
        public AnimationClip? Back { get; }
        public AnimationClip? Left { get; }
        public AnimationClip? Right { get; }

        public bool IsComplete => Forward != null && Back != null && Left != null && Right != null;
    }

    [Serializable]
    public sealed class CombatAnimationLocomotionModeOverride
    {
        [Tooltip("CombatModeCatalog mode_id this locomotion override applies to.")]
        public string modeId = "";

        [Header("Idle")]
        public AnimationClip? locomotionIdle;
        public AnimationClip? locomotionIdleCombat;

        [Header("Directional")]
        public DirectionalClipSet walk;
        public DirectionalClipSet walkCombat;
        public DirectionalClipSet run;
        public DirectionalClipSet runCombat;

        [Header("Directional Stops")]
        public DirectionalClipSet walkStop;
        public DirectionalClipSet walkStopCombat;
        public DirectionalClipSet runStop;
        public DirectionalClipSet runStopCombat;

        [Header("Turns")]
        public AnimationClip? turn90L;
        public AnimationClip? turn90R;
        public AnimationClip? turn180L;
        public AnimationClip? turn180R;
        public AnimationClip? turn90CombatL;
        public AnimationClip? turn90CombatR;
        public AnimationClip? turn180CombatL;
        public AnimationClip? turn180CombatR;

        public string ModeIdOrEmpty => string.IsNullOrWhiteSpace(modeId)
            ? string.Empty
            : modeId.Trim().ToUpperInvariant();

        public bool HasAny =>
            locomotionIdle != null || locomotionIdleCombat != null ||
            walk.HasAny || walkCombat.HasAny || run.HasAny || runCombat.HasAny ||
            walkStop.HasAny || walkStopCombat.HasAny || runStop.HasAny || runStopCombat.HasAny ||
            turn90L != null || turn90R != null || turn180L != null || turn180R != null ||
            turn90CombatL != null || turn90CombatR != null || turn180CombatL != null || turn180CombatR != null;
    }

    [Serializable]
    public enum WeaponVisualAttachmentMode
    {
        Mount = 0,
        AvatarRoot = 1,
    }

    [Serializable]
    public enum WeaponVisualVisibility
    {
        Both = 0,
        CombatOnly = 1,
        StowedOnly = 2,
    }

    [Serializable]
    public struct WeaponVisualBoneRemap
    {
        public string localBoneName;
        public string mountId;
        public bool rootBone;
    }

    [Serializable]
    public struct WeaponVisualBinding
    {
        [Tooltip("Stable identifier for logs/debugging, e.g. sword or shield")]
        public string itemId;
        public GameObject? prefab;
        [Tooltip("Mount id resolved by AvatarWeaponMounts while this item is drawn")]
        [FormerlySerializedAs("combatMountId")]
        public string drawnMountId;
        [Tooltip("Mount id resolved by AvatarWeaponMounts while this item is stowed.")]
        [FormerlySerializedAs("sheathMountId")]
        public string stowedMountId;
        [Tooltip("Optional local position override applied after attaching to the drawn mount.")]
        [FormerlySerializedAs("combatLocalPosition")]
        public Vector3 drawnLocalPosition;
        [Tooltip("Optional local rotation override applied after attaching to the drawn mount.")]
        [FormerlySerializedAs("combatLocalRotation")]
        public Quaternion drawnLocalRotation;
        [Tooltip("Optional local position override applied after attaching to the stowed mount.")]
        [FormerlySerializedAs("sheathLocalPosition")]
        public Vector3 stowedLocalPosition;
        [Tooltip("Optional local rotation override applied after attaching to the stowed mount.")]
        [FormerlySerializedAs("sheathLocalRotation")]
        public Quaternion stowedLocalRotation;
        [Tooltip("Per-item draw handoff. Negative values use the set default.")]
        public float drawHandoffTimeOverride;
        [Tooltip("Per-item sheathe handoff. Negative values use the set default.")]
        public float sheathHandoffTimeOverride;
        [Tooltip("How this visual is parented. AvatarRoot is for pack-authored skinned props whose bones are driven by avatar animation paths.")]
        public WeaponVisualAttachmentMode attachmentMode;
        [Tooltip("Controls whether this visual is shown in both weapon states or only while drawn/stowed.")]
        public WeaponVisualVisibility visibility;
        [Tooltip("Optional remap from prefab-local skinned bones to avatar weapon mount transforms.")]
        public WeaponVisualBoneRemap[] skinnedBoneRemaps;

        public float ResolveDrawHandoffTime(float fallback)
        {
            return drawHandoffTimeOverride >= 0f
                ? Mathf.Clamp01(drawHandoffTimeOverride)
                : Mathf.Clamp01(fallback);
        }

        public float ResolveSheathHandoffTime(float fallback)
        {
            return sheathHandoffTimeOverride >= 0f
                ? Mathf.Clamp01(sheathHandoffTimeOverride)
                : Mathf.Clamp01(fallback);
        }

        public Vector3 ResolveLocalPosition(bool inCombat)
            => inCombat ? drawnLocalPosition : stowedLocalPosition;

        public Quaternion ResolveLocalRotation(bool inCombat)
        {
            Quaternion value = inCombat ? drawnLocalRotation : stowedLocalRotation;
            return value.x == 0f && value.y == 0f && value.z == 0f && value.w == 0f
                ? Quaternion.identity
                : value;
        }

        public WeaponVisualBoneRemap[] SkinnedBoneRemapsOrEmpty => skinnedBoneRemaps ?? Array.Empty<WeaponVisualBoneRemap>();
    }

    [Serializable]
    public sealed class WeaponPresentationProfile
    {
        internal const float DefaultDrawWeaponHandoffTime = 0.45f;
        internal const float DefaultSheathWeaponHandoffTime = 0.7f;

        [Header("Draw / Sheath")]
        public AnimationClip? drawWeapon;
        public AnimationClip? sheathWeapon;

        [Header("Weapon Visual Handoff (normalized 0-1 within transition clip)")]
        [Tooltip("Normalized point in the draw transition when the weapon should visually snap from stowed mount to combat mount.")]
        [Range(0f, 1f)] public float drawWeaponHandoffTime = DefaultDrawWeaponHandoffTime;
        [Tooltip("Normalized point in the sheath transition when the weapon should visually snap from combat mount to stowed mount.")]
        [Range(0f, 1f)] public float sheathWeaponHandoffTime = DefaultSheathWeaponHandoffTime;

        [Header("Weapon Visuals")]
        public WeaponVisualBinding[] visuals = Array.Empty<WeaponVisualBinding>();

        public AnimationClip? DrawWeapon => drawWeapon;
        public AnimationClip? SheathWeapon => sheathWeapon;
        public float DrawWeaponHandoffTime => Mathf.Clamp01(drawWeaponHandoffTime);
        public float SheathWeaponHandoffTime => Mathf.Clamp01(sheathWeaponHandoffTime);
        public WeaponVisualBinding[] VisualsOrEmpty => visuals ?? Array.Empty<WeaponVisualBinding>();

    }

    [Serializable]
    public enum AerialExecutionMode
    {
        GroundedOnly = 0,
        GroundedOrAirborne = 1,
        AirborneOnly = 2,
    }

    [Serializable]
    public enum AirborneTargetingMode
    {
        AnyTarget = 0,
        GroundedTargetOnly = 1,
    }

    [Serializable]
    public struct WeaponStrikeHitWindowAuthoring
    {
        [Tooltip("Normalized point in the attack timeline where this hit lands. 0 = attack start, 1 = attack end.")]
        [Range(0f, 1f)] public float timeNormalized;

        public int ImpactDelayMs(float timingReferenceLengthSeconds)
        {
            if (timingReferenceLengthSeconds <= 0f)
                return 0;

            return Mathf.Max(0, Mathf.RoundToInt(timingReferenceLengthSeconds * Mathf.Clamp01(timeNormalized) * 1000f));
        }
    }

    [Serializable]
    public struct WeaponStrikeCombatAuthoring
    {
        [Tooltip("Design-facing authored strike id. Player-facing abilities should point at this id.")]
        public string id;
        [Tooltip("Internal runtime action id used for cooldown/combo plumbing. Leave blank unless you intentionally need to override the generated runtime mapping. Do not point abilities at this.")]
        public string slotId;
        [HideInInspector] [Range(0f, 1f)] public float impactNormalized;
        [Tooltip("Compatibility mirror of OnStrikeHit animation events. Use Arena/Animation/Event Stamper to author hit timing; editor synchronization keeps this array aligned for legacy tools and tests.")]
        public WeaponStrikeHitWindowAuthoring[] hitWindows;
        [Tooltip("Extra time after the last hit before another root action should be allowed, in milliseconds.")]
        public float recoveryMs;
        [HideInInspector]
        public bool isGapCloser;

        [Tooltip("Authored id of the strike this one follows. Empty = no combo requirement (usable any time).")]
        public string comboFrom;
        [Tooltip("Milliseconds after the predecessor starts when this follow-up should chain. Pressing during this time buffers the follow-up; tuning this effectively sets the strike portion length before settling.")]
        [FormerlySerializedAs("comboWindowMs")]
        public float comboOpenMs;
        [Tooltip("Optional extra late leniency after the combo chain point. Pressing during this period still chains immediately. Ignored when Combo From is empty.")]
        public float comboGraceMs;
        [Tooltip("Whether this authored attack is usable only while grounded, only while airborne, or in either state.")]
        public AerialExecutionMode aerialExecutionMode;
        [Tooltip("Release a server-authoritative projectile from each hit window instead of resolving direct melee impact.")]
        public bool usesProjectileDelivery;
        [Tooltip("Projectile catalog id. Leave blank to use ARROW_STANDARD.")]
        public string projectileId;
        [Tooltip("Override projectile speed in meters/second. 0 uses the projectile catalog value.")]
        public float projectileSpeed;
        [Tooltip("Override projectile max travel distance. 0 uses the projectile catalog value.")]
        public float projectileMaxDistance;
        [Tooltip("Override projectile collision radius. 0 uses the projectile catalog value.")]
        public float projectileRadius;
        [Tooltip("Override projectile spawn offset forward from server position. 0 uses the projectile catalog value.")]
        public float projectileSpawnForward;
        [Tooltip("Override projectile spawn height above server position. 0 uses the projectile catalog value.")]
        public float projectileSpawnHeight;
        [Tooltip("Override target aim height as a fraction of target capsule height. 0 uses the projectile catalog value.")]
        public float projectileAimHeightScale;
        [Tooltip("Require line of sight to the selected target before the release is accepted.")]
        public bool projectileRequiresInitialLineOfSight;
        [Tooltip("Override projectile update event interval in seconds. 0 uses the projectile catalog value.")]
        public float projectileUpdateIntervalSeconds;

        public static WeaponStrikeCombatAuthoring CreateDefault(
            string id,
            float recoveryMs = 250f)
        {
            return new WeaponStrikeCombatAuthoring
            {
                id = id,
                slotId = CombatActionIds.NormalizeRuntimeActionReference(id),
                impactNormalized = 0.5f,
                hitWindows = new[]
                {
                    new WeaponStrikeHitWindowAuthoring
                    {
                        timeNormalized = 0.5f,
                    },
                },
                recoveryMs = recoveryMs,
                isGapCloser = false,
                aerialExecutionMode = AerialExecutionMode.GroundedOnly,
                usesProjectileDelivery = false,
                projectileId = string.Empty,
                projectileRequiresInitialLineOfSight = true,
            };
        }

        public float ClipLengthSeconds(AnimationClip? clip) => clip != null ? clip.length : 0f;

        public WeaponStrikeHitWindowAuthoring[] GetResolvedHitWindows()
        {
            if (hitWindows != null && hitWindows.Length > 0)
            {
                var resolved = (WeaponStrikeHitWindowAuthoring[])hitWindows.Clone();
                Array.Sort(resolved, (a, b) => a.timeNormalized.CompareTo(b.timeNormalized));
                return resolved;
            }

            return new[]
            {
                new WeaponStrikeHitWindowAuthoring
                {
                    timeNormalized = Mathf.Clamp01(impactNormalized),
                },
            };
        }

        public int AuthoredHitWindowCount => hitWindows?.Length ?? 0;
        public int ResolvedHitWindowCount => GetResolvedHitWindows().Length;
        public int FirstImpactDelayMs(float timingReferenceLengthSeconds)
        {
            var hitWindowsResolved = GetResolvedHitWindows();
            return hitWindowsResolved.Length > 0 ? hitWindowsResolved[0].ImpactDelayMs(timingReferenceLengthSeconds) : 0;
        }

        public int RecoveryMsInt => Mathf.Max(0, Mathf.RoundToInt(recoveryMs));
        public int ComboOpenMsInt => Mathf.Max(0, Mathf.RoundToInt(comboOpenMs));
        public int ComboGraceMsInt => Mathf.Max(0, Mathf.RoundToInt(comboGraceMs));
        public string AuthoredStrikeIdOrDefault => string.IsNullOrWhiteSpace(id)
            ? RuntimeSlotIdOrDefault
            : id.Trim();
        public string ComboFromOrEmpty => string.IsNullOrWhiteSpace(comboFrom) ? string.Empty : comboFrom.Trim();
        public string RuntimeSlotIdOrDefault => CombatActionIds.NormalizeRuntimeActionReference(
            string.IsNullOrWhiteSpace(slotId) ? id : slotId);
        public string SlotIdOrDefault => RuntimeSlotIdOrDefault;
        public string ComboFromSlotIdOrEmpty => string.IsNullOrWhiteSpace(comboFrom)
            ? string.Empty
            : CombatActionIds.NormalizeRuntimeActionReference(comboFrom);
        public bool RequiresTarget => true;
        public string ProjectileIdOrDefault => string.IsNullOrWhiteSpace(projectileId)
            ? "ARROW_STANDARD"
            : projectileId.Trim();
        public float ProjectileSpeedExportValue => PositiveProjectileExportOverride(projectileSpeed);
        public float ProjectileMaxDistanceExportValue => PositiveProjectileExportOverride(projectileMaxDistance);
        public float ProjectileRadiusExportValue => PositiveProjectileExportOverride(projectileRadius);
        public float ProjectileSpawnForwardExportValue => PositiveProjectileExportOverride(projectileSpawnForward);
        public float ProjectileSpawnHeightExportValue => PositiveProjectileExportOverride(projectileSpawnHeight);
        public float ProjectileAimHeightScaleExportValue => PositiveProjectileExportOverride(projectileAimHeightScale);
        public float ProjectileUpdateIntervalSecondsExportValue => PositiveProjectileExportOverride(projectileUpdateIntervalSeconds);

        private static float PositiveProjectileExportOverride(float value)
            => !float.IsNaN(value) && !float.IsInfinity(value) && value > 0f ? value : 0f;
    }

    [Serializable]
    public enum SpellPlaybackLayer
    {
        UpperBodyWhileMoving = 0,
        FullBody = 1,
        UpperBody = 2,
        LeftGesture = 3,
        RightGesture = 4,
    }

    [Serializable]
    public enum CombatEntryMode
    {
        None = 0,
        Immediate = 1,
        AnimatedAfterCast = 2,
        ImmediateForFullBodyAnimatedAfterUpperBody = 3,
    }

    [Serializable]
    public enum SpellAnimationPresentationMode
    {
        ReleaseOnly = 0,
        HoldThenRelease = 1,
        HoldOnly = 2,
        HoldWithPulse = 3,
    }

    /// <summary>
    /// Natural spell-emission origin authored by a cast animation. UseVfxCue preserves the
    /// concrete LEFT_HAND/RIGHT_HAND anchor already present in legacy VFX cue data.
    /// </summary>
    [Serializable]
    public enum SpellCastOrigin
    {
        UseVfxCue = 0,
        LeftHand = 1,
        RightHand = 2,
    }

    [Serializable]
    public struct SpellCastHoldProfile
    {
        [Tooltip("Non-looping clip played when an accepted cast-time spell begins.")]
        public AnimationClip? enter;
        [Tooltip("Looping clip held while the cast waits for its scheduled release animation.")]
        public AnimationClip? idleLoop;
        [Tooltip("Optional non-looping exit played when the hold or channel ends without a cast/release clip, including cancellation.")]
        public AnimationClip? exit;
        [Tooltip("How the cast hold should present relative to locomotion. Do not use a full-body stationary hold for mobile casts unless gameplay prevents movement.")]
        public SpellPlaybackLayer playbackLayer;
        [Tooltip("Seconds to hold the cast pose at full layer weight after release fires. Use this to keep legs/torso aligned with the spell motion until the release animation is mostly done. 0 = blend immediately.")]
        [Min(0f)] public float exitDelaySeconds;
        [Tooltip("Seconds the layer weight takes to blend from 1 to 0 once the hold delay elapses. 0 = snap.")]
        [Min(0f)] public float exitBlendOutSeconds;

        public bool HasAny => enter != null || idleLoop != null || exit != null;
        public bool IsPlayable => enter != null && idleLoop != null;
        public AnimationClip? EnterOrIdle => enter ?? idleLoop;
        public AnimationClip? IdleOrEnter => idleLoop ?? enter;
        public bool HasExit => exit != null;
        public float ResolveExitDelaySeconds(float fallback) => exitDelaySeconds > 0f ? exitDelaySeconds : fallback;
        public float ResolveExitBlendOutSeconds(float fallback) => exitBlendOutSeconds > 0f ? exitBlendOutSeconds : fallback;
        public float ResolveEnterCompleteNormalizedTime(float fallback)
        {
            AnimationClip? clip = EnterOrIdle;
            if (clip == null || clip.length <= 0.001f)
                return Mathf.Clamp01(fallback);

            float fallbackSeconds = Mathf.Clamp01(fallback) * clip.length;
            float eventSeconds = CombatAnimationEvents.GetEventTimeOrFallback(
                clip,
                CombatAnimationEvents.OnEnterComplete,
                fallbackSeconds);
            return Mathf.Clamp01(eventSeconds / clip.length);
        }
    }

    [Serializable]
    public struct WeaponSpellAnimationEntry
    {
        [Tooltip("Runtime spell/action id this animation entry responds to. This is for actual spell/buff runtime actions, not authored melee strike ids.")]
        public string spellId;
        [FormerlySerializedAs("ground")]
        [Tooltip("Movement-state-independent cast/release clip. The same presentation is used on the ground and in the air.")]
        public AnimationClip? clip;
        [Tooltip("For HoldWithPulse, the authored transition that returns from Clip to the hold loop.")]
        public AnimationClip? returnToHold;
        [Tooltip("Whether this spell should request combat stance before or after playback.")]
        public bool requiresCombatStance;
        [Tooltip("How combat stance is entered when this spell requires it.")]
        public CombatEntryMode combatEntryMode;
        [Tooltip("ReleaseOnly plays the clip. HoldThenRelease holds then releases. HoldOnly holds until exit. HoldWithPulse temporarily plays Clip/Return To Hold while preserving the hold.")]
        public SpellAnimationPresentationMode presentationMode;
        [Tooltip("Optional per-spell cast/channel hold presentation. Leave empty to use the animation set's Default Spell Cast Hold.")]
        public SpellCastHoldProfile holdOverride;
        [Tooltip("How this spell should present relative to locomotion. UpperBodyWhileMoving preserves locomotion only while moving; FullBody always uses the spell-action layer; UpperBody always uses the upper-body layer; LeftGesture/RightGesture use single masked pelvis/spine/casting-arm overlays.")]
        [FormerlySerializedAs("movingPlaybackMode")]
        public SpellPlaybackLayer playbackLayer;
        [Tooltip("Natural hand the authored animation emits from. Use VFX Cue preserves legacy cue-authored handedness.")]
        public SpellCastOrigin castOrigin;
        [Tooltip("Mirror the complete humanoid spell presentation. The effective cast origin is mirrored with it.")]
        public bool mirrorPresentation;
        [Tooltip("Obsolete serialized compatibility field. Runtime reads OnLowerBodyUnlock from the selected clip instead.")]
        public float lowerBodyUnlockAtSeconds;
        [Tooltip("Obsolete serialized compatibility field. Runtime uses the default lower-body blend-out duration.")]
        public float lowerBodyBlendOutSeconds;
        [Tooltip("Obsolete serialized compatibility field. Runtime reads OnVisualInterruptible from the selected clip instead.")]
        public float visualInterruptibleAtSeconds;
        [Tooltip("Optional temporary weapon/shield visual driven by animation-authored prop sockets until the spell projectile releases.")]
        public SpellAnimatedPropHandoff animatedProp;

        public string SpellIdOrEmpty => string.IsNullOrWhiteSpace(spellId)
            ? string.Empty
            : spellId.Trim().ToUpperInvariant();

        public bool HasAny => clip != null;
        public bool UsesUpperBodyWhileMoving => playbackLayer == SpellPlaybackLayer.UpperBodyWhileMoving;
        public bool UsesUpperBody => playbackLayer == SpellPlaybackLayer.UpperBody;
        public bool UsesLeftGesture => playbackLayer == SpellPlaybackLayer.LeftGesture;
        public bool UsesRightGesture => playbackLayer == SpellPlaybackLayer.RightGesture;
        public bool HasAnimatedPropHandoff => animatedProp.enabled;
        public bool HasAuthoredCastOrigin => castOrigin == SpellCastOrigin.LeftHand
            || castOrigin == SpellCastOrigin.RightHand;
        public SpellCastOrigin EffectiveCastOrigin => mirrorPresentation
            ? castOrigin switch
            {
                SpellCastOrigin.LeftHand => SpellCastOrigin.RightHand,
                SpellCastOrigin.RightHand => SpellCastOrigin.LeftHand,
                _ => SpellCastOrigin.UseVfxCue,
            }
            : castOrigin;
        public bool UsesHoldPresentation => presentationMode == SpellAnimationPresentationMode.HoldThenRelease
            || presentationMode == SpellAnimationPresentationMode.HoldOnly
            || presentationMode == SpellAnimationPresentationMode.HoldWithPulse;
        public bool PlaysReleasePresentation => presentationMode != SpellAnimationPresentationMode.HoldOnly
            && presentationMode != SpellAnimationPresentationMode.HoldWithPulse;
        public bool PlaysHoldPulsePresentation => presentationMode == SpellAnimationPresentationMode.HoldWithPulse;
        public bool HasAnyPresentation => HasAny || UsesHoldPresentation || holdOverride.HasAny;

        public bool TryResolveHoldProfile(
            SpellCastHoldProfile defaultProfile,
            out SpellCastHoldProfile profile)
        {
            if (holdOverride.IsPlayable)
            {
                profile = holdOverride;
                return true;
            }

            profile = defaultProfile;
            return profile.IsPlayable;
        }

        public bool ResolveUsesOverlayPlayback(float locomotionRawMagnitude, float movementThreshold)
        {
            return playbackLayer == SpellPlaybackLayer.UpperBody
                || playbackLayer == SpellPlaybackLayer.LeftGesture
                || playbackLayer == SpellPlaybackLayer.RightGesture
                || (locomotionRawMagnitude >= movementThreshold
                    && playbackLayer == SpellPlaybackLayer.UpperBodyWhileMoving);
        }

        public bool ShouldEnterCombatImmediately(bool useOverlayPlayback)
        {
            if (!requiresCombatStance)
                return false;

            return combatEntryMode == CombatEntryMode.Immediate
                || (combatEntryMode == CombatEntryMode.ImmediateForFullBodyAnimatedAfterUpperBody
                    && !useOverlayPlayback);
        }

        public bool ShouldEnterCombatAfterCastStarts(bool useOverlayPlayback)
        {
            if (!requiresCombatStance)
                return false;

            return combatEntryMode == CombatEntryMode.AnimatedAfterCast
                || (combatEntryMode == CombatEntryMode.ImmediateForFullBodyAnimatedAfterUpperBody
                    && useOverlayPlayback);
        }

        public AnimationClip? ResolveClip() => clip;

        public float ResolveEffectTime()
            => ResolveReleaseTimeNormalized();

        public float ResolveReleaseTimeNormalized()
        {
            AnimationClip? resolvedClip = ResolveClip();
            if (resolvedClip == null || resolvedClip.length <= 0.001f)
                return 0f;

            return Mathf.Clamp01(ResolveReleaseOffsetSeconds() / resolvedClip.length);
        }

        public float ResolveTimingReferenceLengthSeconds()
        {
            AnimationClip? resolvedClip = ResolveClip();
            return resolvedClip != null ? resolvedClip.length : 0f;
        }

        public float ResolveReleaseOffsetSeconds()
        {
            AnimationClip? resolvedClip = ResolveClip();
            return CombatAnimationEvents.GetRequiredEventTimeOrFallback(
                resolvedClip,
                CombatAnimationEvents.OnReleaseFrame,
                fallbackSeconds: 0f,
                context: $"spell '{SpellIdOrEmpty}' release alignment");
        }

        /// <summary>
        /// Resolves the optional clip-authored start offset for a gameplay-confirmed instant cast.
        /// Charged/channel releases deliberately return zero even when they share a release clip
        /// carrying the marker. The marker is clamped to OnReleaseFrame so playback never skips the
        /// visible hand-release pose.
        /// </summary>
        public float ResolveInstantCastStartupTrimSeconds(bool confirmedInstant)
        {
            if (!confirmedInstant)
                return 0f;

            AnimationClip? resolvedClip = ResolveClip();
            if (resolvedClip == null
                || !CombatAnimationEvents.TryGetEventTime(
                    resolvedClip,
                    CombatAnimationEvents.OnInstantCastStart,
                    out float authoredTrimSeconds))
            {
                return 0f;
            }

            float releaseOffsetSeconds = ResolveReleaseOffsetSeconds();
            return Mathf.Clamp(
                authoredTrimSeconds,
                0f,
                Mathf.Min(Mathf.Max(0f, resolvedClip.length), releaseOffsetSeconds));
        }

        /// <summary>
        /// Resolves how long an animation-driven prop remains attached after playback begins.
        /// Startup trim and remote catch-up both advance the playback start, so neither should
        /// delay the handoff beyond the still-visible portion of the release gesture.
        /// </summary>
        public float ResolveReleaseDelayAfterPlaybackStartSeconds(
            float playbackStartOffsetSeconds)
        {
            return Mathf.Max(
                0f,
                ResolveReleaseOffsetSeconds()
                - Mathf.Max(0f, playbackStartOffsetSeconds));
        }

        public float ResolveLowerBodyUnlockAtSeconds()
        {
            AnimationClip? resolvedClip = ResolveClip();
            float fallback = resolvedClip != null ? resolvedClip.length : 0f;
            return CombatAnimationEvents.GetRequiredEventTimeOrFallback(
                resolvedClip,
                CombatAnimationEvents.OnLowerBodyUnlock,
                fallback,
                $"spell '{SpellIdOrEmpty}' lower-body unlock");
        }

        public float ResolveLowerBodyBlendOutSeconds(float defaultBlendOutSeconds)
        {
            return Mathf.Max(0f, defaultBlendOutSeconds);
        }

        public float ResolveVisualInterruptibleAtSeconds()
        {
            AnimationClip? resolvedClip = ResolveClip();
            float fallback = resolvedClip != null ? resolvedClip.length : 0f;
            return CombatAnimationEvents.GetRequiredEventTimeOrFallback(
                resolvedClip,
                CombatAnimationEvents.OnVisualInterruptible,
                fallback,
                $"spell '{SpellIdOrEmpty}' visual interrupt");
        }
    }

    /// <summary>
    /// Maps one semantic spell motion to the cast-animation family used by this combat set. The
    /// family supplies the cast-hand and Instant/Charged/Channel variants through the shared
    /// SpellCastAnimationLibrary and composer.
    /// </summary>
    [Serializable]
    public struct SpellCastMotionBinding
    {
        public SpellCastMotion motion;
        [Tooltip("Family base name from SpellCastAnimationLibrary, e.g. MagicAttackCall1H01.")]
        public string familyBaseName;

        public string FamilyBaseNameOrEmpty => string.IsNullOrWhiteSpace(familyBaseName)
            ? string.Empty
            : familyBaseName.Trim();
    }

    /// <summary>
    /// Optional escape hatch for a spell whose globally selected cast recipe does not fit one
    /// combat pose. The override still points into the shared SpellCastAnimationCatalog; it does
    /// not duplicate clips or recipe timing on the animation set.
    /// </summary>
    [Serializable]
    public struct SpellCastAnimationOverride
    {
        [Tooltip("Runtime spell/action id, e.g. FLAMING_ORB.")]
        public string spellId;
        [Tooltip("Recipe id from SpellCastAnimationCatalog.")]
        public string animationId;
        [Tooltip("Mirror the selected recipe for this combat animation set. Its authored cast origin mirrors with the body.")]
        public bool mirrorPresentation;

        public string SpellIdOrEmpty => string.IsNullOrWhiteSpace(spellId)
            ? string.Empty
            : spellId.Trim().ToUpperInvariant();

        public string AnimationIdOrEmpty => string.IsNullOrWhiteSpace(animationId)
            ? string.Empty
            : animationId.Trim().ToUpperInvariant();

        public bool HasAny => AnimationIdOrEmpty.Length != 0 || mirrorPresentation;
    }

    [Serializable]
    public struct SpellAnimatedPropHandoff
    {
        [Tooltip("Enable a temporary duplicate of an equipped visual for this spell animation.")]
        public bool enabled;
        [Tooltip("Weapon presentation item id to duplicate, e.g. sword or shield.")]
        public string itemId;
        [Tooltip("Avatar-relative transform path animated by the imported clip, e.g. root/.../hand_r/Sword.")]
        public string animatedSocketPath;
        [Tooltip("Hide the normal equipped visual while the temporary animated duplicate is active.")]
        public bool hideEquippedVisual;
        [Tooltip("Local position applied under the animated socket.")]
        public Vector3 localPosition;
        [Tooltip("Local rotation applied under the animated socket. Zero quaternion resolves to identity.")]
        public Quaternion localRotation;
        [Tooltip("Local scale multiplier for the temporary duplicate. 0 resolves to 1.")]
        public float localScale;
        [Tooltip("Safety timeout after release in seconds if no projectile handoff arrives.")]
        public float maxLifetimeSeconds;

        public string ItemIdOrEmpty => string.IsNullOrWhiteSpace(itemId)
            ? string.Empty
            : itemId.Trim();

        public string AnimatedSocketPathOrEmpty => string.IsNullOrWhiteSpace(animatedSocketPath)
            ? string.Empty
            : animatedSocketPath.Trim();

        public Quaternion ResolveLocalRotation()
            => localRotation.x == 0f && localRotation.y == 0f && localRotation.z == 0f && localRotation.w == 0f
                ? Quaternion.identity
                : localRotation;

        public float ResolveLocalScale() => localScale > 0f ? localScale : 1f;
        public float ResolveMaxLifetimeSeconds() => maxLifetimeSeconds > 0f ? maxLifetimeSeconds : 2f;
    }

    [Serializable]
    public struct StatusReactionAnimationEntry
    {
        [Tooltip("Runtime status kind this reaction responds to, e.g. STUN or INTIMIDATED.")]
        public string statusKind;
        [Tooltip("Loop clip used for the duration of the status.")]
        public AnimationClip? loop;

        public string StatusKindOrEmpty => string.IsNullOrWhiteSpace(statusKind)
            ? string.Empty
            : statusKind.Trim().ToUpperInvariant();

        public bool HasAny => loop != null;
    }

    [Serializable]
    public enum WeaponPresentationEffectSourceKind
    {
        ConsumedMeleeModifier = 0,
    }

    [Serializable]
    public enum WeaponPresentationEffectTarget
    {
        MainHand = 0,
        AllEquipped = 1,
        ItemId = 2,
    }

    [Serializable]
    public struct WeaponPresentationEffectEntry
    {
        [Tooltip("Authoritative combat fact that triggers this visual effect.")]
        public WeaponPresentationEffectSourceKind sourceKind;
        [Tooltip("Status kind required for ConsumedMeleeModifier triggers, e.g. MELEE_ATTACK_MODIFIER.")]
        public string statusKind;
        [Tooltip("Status stack group required for ConsumedMeleeModifier triggers.")]
        public string stackGroup;
        [Tooltip("Which spawned weapon visual receives this presentation effect.")]
        public WeaponPresentationEffectTarget target;
        [Tooltip("Used when Target is ItemId.")]
        public string itemId;
        [Tooltip("Transient visual scale multiplier applied to the target weapon visual.")]
        public float scaleMultiplier;
        [Tooltip("Seconds to scale from base size to multiplier.")]
        public float scaleInSeconds;
        [Tooltip("Seconds to scale from multiplier back to base size.")]
        public float scaleOutSeconds;

        public bool MatchesConsumedMeleeModifier(string consumedStatusKind, string consumedStackGroup)
        {
            if (sourceKind != WeaponPresentationEffectSourceKind.ConsumedMeleeModifier)
                return false;

            string normalizedStatusKind = WireIdentifier.Normalize(statusKind);
            string normalizedStackGroup = WireIdentifier.Normalize(stackGroup);
            return !string.IsNullOrWhiteSpace(normalizedStatusKind)
                && !string.IsNullOrWhiteSpace(normalizedStackGroup)
                && string.Equals(normalizedStatusKind, WireIdentifier.Normalize(consumedStatusKind), StringComparison.Ordinal)
                && string.Equals(normalizedStackGroup, WireIdentifier.Normalize(consumedStackGroup), StringComparison.Ordinal);
        }

        public float ScaleMultiplierOrDefault => scaleMultiplier > 0f ? scaleMultiplier : 1f;
        public float ScaleInSecondsOrDefault => Mathf.Max(0f, scaleInSeconds);
        public float ScaleOutSecondsOrDefault => Mathf.Max(0f, scaleOutSeconds);
    }

    [Serializable]
    public struct WeaponPhasedActionClipSet
    {
        [Tooltip("Startup clip played once when phased melee presentation begins.")]
        public AnimationClip? start;
        [Tooltip("Loop clip repeated while phased melee presentation remains active.")]
        public AnimationClip? loop;
        [Tooltip("Exit clip played once when phased melee presentation is released or finishes.")]
        public AnimationClip? end;

        public bool HasAny => start != null || loop != null || end != null;
        public int ClipCount => (start != null ? 1 : 0) + (loop != null ? 1 : 0) + (end != null ? 1 : 0);
        public bool IsComplete => start != null && loop != null && end != null;
        public bool IsPlayable => ClipCount >= 2;

        public bool TryResolvePlayback(out ResolvedWeaponPhasedActionClipSet resolved)
        {
            if (start != null && loop != null && end != null)
            {
                resolved = new ResolvedWeaponPhasedActionClipSet(start, loop, end, false);
                return true;
            }

            if (start != null && end != null)
            {
                resolved = new ResolvedWeaponPhasedActionClipSet(start, start, end, true);
                return true;
            }

            if (start != null && loop != null)
            {
                resolved = new ResolvedWeaponPhasedActionClipSet(start, loop, loop, false);
                return true;
            }

            if (loop != null && end != null)
            {
                resolved = new ResolvedWeaponPhasedActionClipSet(loop, loop, end, false);
                return true;
            }

            resolved = default;
            return false;
        }
    }

    public readonly struct ResolvedWeaponPhasedActionClipSet
    {
        public ResolvedWeaponPhasedActionClipSet(
            AnimationClip start,
            AnimationClip loop,
            AnimationClip end,
            bool releaseAfterStart)
        {
            Start = start;
            Loop = loop;
            End = end;
            ReleaseAfterStart = releaseAfterStart;
        }

        public AnimationClip Start { get; }
        public AnimationClip Loop { get; }
        public AnimationClip End { get; }
        public bool ReleaseAfterStart { get; }

        public float ResolveOpeningTailSeconds(float requestedTailSeconds)
        {
            if (!ReleaseAfterStart || requestedTailSeconds <= 0f)
                return 0f;

            return Mathf.Min(Mathf.Max(0f, requestedTailSeconds), Mathf.Max(0f, Start.length));
        }

        public float ResolveOpeningStartNormalizedTime(float requestedTailSeconds)
        {
            float startLengthSeconds = Mathf.Max(0f, Start.length);
            float tailSeconds = ResolveOpeningTailSeconds(requestedTailSeconds);
            return tailSeconds > 0f && startLengthSeconds > 0.001f
                ? Mathf.Clamp01(1f - tailSeconds / startLengthSeconds)
                : 0f;
        }

        public float ResolveStartTimelineLengthSeconds(float requestedTailSeconds)
        {
            float tailSeconds = ResolveOpeningTailSeconds(requestedTailSeconds);
            return tailSeconds > 0f ? tailSeconds : ResolveStartTimelineLengthSeconds();
        }

        public float ResolveOpeningPlaybackStartSeconds(float requestedTailSeconds)
        {
            float tailSeconds = ResolveOpeningTailSeconds(requestedTailSeconds);
            return tailSeconds > 0f
                ? Mathf.Max(0f, Start.length - tailSeconds)
                : 0f;
        }

        public float ResolveOpeningPlaybackEndSeconds(float requestedTailSeconds)
        {
            return ResolveOpeningTailSeconds(requestedTailSeconds) > 0f
                ? Mathf.Max(0f, Start.length)
                : ResolveStartTimelineLengthSeconds();
        }

        public float ResolveStartTimelineLengthSeconds()
        {
            float startLengthSeconds = Mathf.Max(0f, Start.length);
            if (!CombatAnimationEvents.TryGetEventNormalizedTime(
                    Start,
                    CombatAnimationEvents.OnPhaseLoopReady,
                    out float normalizedTime))
            {
                return startLengthSeconds;
            }

            float safetyNormalizedTime = ReleaseAfterStart
                ? CombatAnimationEvents.PhasedMeleeStartOnlyEndSafetyNormalizedTime
                : CombatAnimationEvents.PhasedMeleeStartToLoopSafetyNormalizedTime;
            return startLengthSeconds * Mathf.Min(normalizedTime, safetyNormalizedTime);
        }

        public float ResolveLoopTimelineLengthSeconds()
        {
            if (ReleaseAfterStart)
                return 0f;

            float loopLengthSeconds = Mathf.Max(0f, Loop.length);
            if (!CombatAnimationEvents.TryGetEventNormalizedTime(
                    Loop,
                    CombatAnimationEvents.OnPhaseLoopReady,
                    out float normalizedTime))
            {
                return loopLengthSeconds;
            }

            return loopLengthSeconds * Mathf.Min(
                normalizedTime,
                CombatAnimationEvents.PhasedMeleeStartToLoopSafetyNormalizedTime);
        }
    }

    [Serializable]
    public struct WeaponPhasedActionEntry
    {
        [FormerlySerializedAs("actionId")]
        [Tooltip("Design-facing authored strike id this phased melee presentation responds to.")]
        public string authoredActionId;
        [Tooltip("When true, this phased melee action holds its loop segment until the matching authoritative special movement ends.")]
        public bool drivePhasesFromSpecialMovement;
        [Tooltip("When true, casts inside impact reach skip Start and scale Loop by cast distance; casts beyond impact reach use Start and hold Loop for the gap close.")]
        public bool scaleGapClosePhasesFromImpactReach;
        [Tooltip("For a two-clip phased action, play only this many seconds from the tail of the opening clip before End. Zero preserves the authored opening marker.")]
        [Min(0f)] public float phasedOpeningTailSeconds;
        [Tooltip("When true, this phased melee action holds its loop segment until the matching authoritative combat action releases or fizzles.")]
        public bool drivePhasesFromCombatLifecycle;
        [Tooltip("Grounded phased clips for this authored strike. Leave empty to fall back to Air if that set is complete.")]
        public WeaponPhasedActionClipSet ground;
        [Tooltip("Airborne phased clips for this authored strike. Leave empty to fall back to Ground if that set is complete.")]
        public WeaponPhasedActionClipSet air;

        public string AuthoredActionIdOrEmpty => WireIdentifier.Normalize(authoredActionId);

        public bool TryResolveClipSet(bool grounded, out ResolvedWeaponPhasedActionClipSet clipSet)
        {
            WeaponPhasedActionClipSet preferred = grounded ? ground : air;
            if (preferred.TryResolvePlayback(out clipSet))
            {
                return true;
            }

            WeaponPhasedActionClipSet fallback = grounded ? air : ground;
            if (fallback.TryResolvePlayback(out clipSet))
            {
                return true;
            }

            clipSet = default;
            return false;
        }
    }

    [Serializable]
    public enum WeaponMeleePresentationMode
    {
        SingleClip = 0,
        Phased = 1,
    }

    [Serializable]
    public struct WeaponMeleeAttackAuthoring
    {
        [Tooltip("Animation clip played for this authored melee attack.")]
        public AnimationClip? clip;
        [Tooltip("Combat tuning and authored identity for this melee attack.")]
        public WeaponStrikeCombatAuthoring combat;
        [Tooltip("How this attack is presented. Single Clip plays the Clip field directly. Phased stitches start/loop/end clips together.")]
        public WeaponMeleePresentationMode presentationMode;
        [Tooltip("Seconds skipped from the beginning of a single-clip melee presentation. OnStrikeHit remains stamped on the physical contact pose; exported gameplay hit times subtract this trim. Phased melee does not support startup trim.")]
        [Min(0f)] public float startupTrimSeconds;
        [Tooltip("Obsolete serialized compatibility field. Runtime reads OnLowerBodyUnlock from the selected clip or phased segment instead.")]
        public float lowerBodyUnlockAtSeconds;
        [Tooltip("Obsolete serialized compatibility field. Runtime uses the default lower-body blend-out duration.")]
        public float lowerBodyBlendOutSeconds;
        [Tooltip("Obsolete serialized compatibility field. Runtime reads OnVisualInterruptible from the selected clip or phased segment instead.")]
        public float visualInterruptibleAtSeconds;
        [Tooltip("Grounded phased clips for this attack when Presentation Mode is Phased.")]
        public WeaponPhasedActionClipSet phasedGround;
        [Tooltip("Airborne phased clips for this attack when Presentation Mode is Phased.")]
        public WeaponPhasedActionClipSet phasedAir;
        [Tooltip("Only for phased movement-coupled attacks. When enabled, Start plays once, Loop holds while special movement is active, and End plays when special movement ends.")]
        public bool drivePhasesFromSpecialMovement;
        [Tooltip("Opt-in phased gap-close timing. Inside impact reach, skip Start and play a cast-distance-scaled part of Loop. Beyond impact reach, play Start and hold Loop until movement ends.")]
        public bool scaleGapClosePhasesFromImpactReach;
        [Tooltip("For a two-clip phased action, play only this many seconds from the tail of the opening clip before End. Zero preserves the authored opening marker.")]
        [Min(0f)] public float phasedOpeningTailSeconds;
        [Tooltip("Only for phased channeled attacks. When enabled, Start plays once, Loop holds while the authoritative combat action is active, and End plays when that action releases or fizzles.")]
        public bool drivePhasesFromCombatLifecycle;
        [Tooltip("Deterministic bindings from semantic slots on the selected animation clips to registered VFX ids. Timing and transforms stay on the shared clip track.")]
        public List<CombatAnimationVfxBinding> animationVfxBindings;

        public bool UsesPhasedPresentation => presentationMode == WeaponMeleePresentationMode.Phased;

        public float ResolveStartupTrimSeconds()
        {
            if (UsesPhasedPresentation || clip == null || startupTrimSeconds <= 0f)
                return 0f;

            if (!TryGetStrikeHitEventTimesSeconds(out float[] eventTimesSeconds)
                || eventTimesSeconds.Length == 0)
            {
                return 0f;
            }

            return Mathf.Clamp(startupTrimSeconds, 0f, eventTimesSeconds[0]);
        }

        public float ResolveStartupTrimNormalized()
        {
            float clipLengthSeconds = clip != null ? Mathf.Max(0f, clip.length) : 0f;
            return clipLengthSeconds > 0.001f
                ? Mathf.Clamp01(ResolveStartupTrimSeconds() / clipLengthSeconds)
                : 0f;
        }

        public bool TryBuildPhasedActionEntry(out WeaponPhasedActionEntry entry)
        {
            if (!UsesPhasedPresentation)
            {
                entry = default;
                return false;
            }

            entry = new WeaponPhasedActionEntry
            {
                authoredActionId = combat.AuthoredStrikeIdOrDefault,
                drivePhasesFromSpecialMovement = drivePhasesFromSpecialMovement,
                scaleGapClosePhasesFromImpactReach = scaleGapClosePhasesFromImpactReach,
                phasedOpeningTailSeconds = Mathf.Max(0f, phasedOpeningTailSeconds),
                drivePhasesFromCombatLifecycle = drivePhasesFromCombatLifecycle,
                ground = phasedGround,
                air = phasedAir,
            };
            return entry.ground.IsPlayable || entry.air.IsPlayable;
        }

        public float ResolveTimingReferenceLengthSeconds()
        {
            if (!UsesPhasedPresentation)
                return clip != null ? clip.length : 0f;

            float clipLength = clip != null ? clip.length : 0f;
            float groundLength = ResolvePhasedClipSetLengthSeconds(phasedGround);
            float airLength = ResolvePhasedClipSetLengthSeconds(phasedAir);
            return Mathf.Max(clipLength, groundLength, airLength);
        }

        public float ResolveVisualInterruptibleAtSeconds()
        {
            return ResolveSingleClipEventTimeOrFallback(
                CombatAnimationEvents.OnVisualInterruptible,
                "visual interrupt");
        }

        public float ResolveLowerBodyUnlockAtSeconds()
        {
            return ResolveSingleClipEventTimeOrFallback(
                CombatAnimationEvents.OnLowerBodyUnlock,
                "lower-body unlock");
        }

        public float ResolveLowerBodyBlendOutSeconds(float defaultBlendOutSeconds)
        {
            return Mathf.Max(0f, defaultBlendOutSeconds);
        }

        public float ResolveVisualInterruptibleAtSeconds(bool grounded)
        {
            return ResolveMeleeEventTimeOrFallback(
                grounded,
                CombatAnimationEvents.OnVisualInterruptible,
                "visual interrupt");
        }

        public float ResolveLowerBodyUnlockAtSeconds(bool grounded)
        {
            return ResolveMeleeEventTimeOrFallback(
                grounded,
                CombatAnimationEvents.OnLowerBodyUnlock,
                "lower-body unlock");
        }

        private float ResolveSingleClipEventTimeOrFallback(string eventName, string timingLabel)
        {
            float fallback = ResolveTimingReferenceLengthSeconds();
            return CombatAnimationEvents.GetRequiredEventTimeOrFallback(
                clip,
                eventName,
                fallback,
                $"melee '{combat.AuthoredStrikeIdOrDefault}' {timingLabel}");
        }

        private float ResolveMeleeEventTimeOrFallback(bool grounded, string eventName, string timingLabel)
        {
            float fallback = ResolveTimingReferenceLengthSeconds();
            if (!UsesPhasedPresentation)
                return ResolveSingleClipEventTimeOrFallback(eventName, timingLabel);

            if (TryResolvePhasedEventTime(grounded, eventName, out float eventTime))
                return eventTime;

            CombatAnimationEvents.GetRequiredEventTimeOrFallback(
                null,
                eventName,
                fallback,
                $"phased melee '{combat.AuthoredStrikeIdOrDefault}' {timingLabel}");
            return fallback;
        }

        private bool TryResolvePhasedEventTime(bool grounded, string eventName, out float eventTime)
        {
            WeaponPhasedActionClipSet preferred = grounded ? phasedGround : phasedAir;
            if (TryResolvePhasedEventTime(preferred, eventName, out eventTime))
                return true;

            WeaponPhasedActionClipSet fallback = grounded ? phasedAir : phasedGround;
            return TryResolvePhasedEventTime(fallback, eventName, out eventTime);
        }

        private bool TryResolvePhasedEventTime(
            WeaponPhasedActionClipSet clipSet,
            string eventName,
            out float eventTime)
        {
            eventTime = 0f;
            if (!clipSet.TryResolvePlayback(out ResolvedWeaponPhasedActionClipSet resolved))
                return false;

            bool found = false;
            float offset = 0f;
            float startTimelineLengthSeconds =
                resolved.ResolveStartTimelineLengthSeconds(phasedOpeningTailSeconds);
            float startPlaybackOffsetSeconds =
                resolved.ResolveOpeningPlaybackStartSeconds(phasedOpeningTailSeconds);
            float startPlaybackEndSeconds =
                resolved.ResolveOpeningPlaybackEndSeconds(phasedOpeningTailSeconds);
            if (TryGetEventTimeInPlaybackWindow(
                    resolved.Start,
                    eventName,
                    startPlaybackOffsetSeconds,
                    startPlaybackEndSeconds,
                    out float startTime))
            {
                eventTime = startTime;
                found = true;
            }

            offset += startTimelineLengthSeconds;
            if (!resolved.ReleaseAfterStart)
            {
                if (CombatAnimationEvents.TryGetEventTime(resolved.Loop, eventName, out float loopTime)
                    && (!found || offset + loopTime < eventTime))
                {
                    eventTime = offset + loopTime;
                    found = true;
                }

                offset += resolved.ResolveLoopTimelineLengthSeconds();
            }

            if (CombatAnimationEvents.TryGetEventTime(resolved.End, eventName, out float endTime)
                && (!found || offset + endTime < eventTime))
            {
                eventTime = offset + endTime;
                found = true;
            }

            return found;
        }

        private static bool TryGetEventTimeInPlaybackWindow(
            AnimationClip clip,
            string eventName,
            float localStartSeconds,
            float localEndSeconds,
            out float playbackTimeSeconds)
        {
            playbackTimeSeconds = 0f;
            var localTimes = new List<float>();
            CombatAnimationEvents.AppendEventTimes(
                clip,
                eventName,
                localTimes,
                offsetSeconds: 0f,
                maxLocalTimeSeconds: localEndSeconds);

            bool found = false;
            float earliest = float.PositiveInfinity;
            for (int index = 0; index < localTimes.Count; index++)
            {
                float localTime = localTimes[index];
                if (localTime + 0.0001f < localStartSeconds)
                    continue;

                earliest = Mathf.Min(earliest, localTime - localStartSeconds);
                found = true;
            }

            if (found)
                playbackTimeSeconds = Mathf.Max(0f, earliest);
            return found;
        }

        public bool TryGetStrikeHitEventTimesSeconds(out float[] eventTimesSeconds)
        {
            var times = new List<float>();
            if (UsesPhasedPresentation)
            {
                if (!AppendPhasedStrikeHitEventTimes(phasedGround, times)
                    && !AppendPhasedStrikeHitEventTimes(phasedAir, times))
                {
                    CombatAnimationEvents.AppendEventTimes(clip, CombatAnimationEvents.OnStrikeHit, times);
                }
            }
            else
            {
                CombatAnimationEvents.AppendEventTimes(clip, CombatAnimationEvents.OnStrikeHit, times);
            }

            if (times.Count == 0)
            {
                eventTimesSeconds = Array.Empty<float>();
                return false;
            }

            times.Sort();
            eventTimesSeconds = times.ToArray();
            return true;
        }

        public bool TryGetEffectiveStrikeHitTimesSeconds(out float[] eventTimesSeconds)
        {
            if (!TryGetStrikeHitEventTimesSeconds(out float[] authoredEventTimesSeconds))
            {
                eventTimesSeconds = Array.Empty<float>();
                return false;
            }

            float startupTrim = ResolveStartupTrimSeconds();
            eventTimesSeconds = new float[authoredEventTimesSeconds.Length];
            for (int index = 0; index < authoredEventTimesSeconds.Length; index++)
                eventTimesSeconds[index] = Mathf.Max(0f, authoredEventTimesSeconds[index] - startupTrim);
            return true;
        }

        public bool TryBuildPhasedGapCloseTiming(out MeleeManifestPhasedGapCloseTiming timing)
        {
            timing = null!;
            if (!UsesPhasedPresentation
                || !drivePhasesFromSpecialMovement
                || !scaleGapClosePhasesFromImpactReach
                || !TryResolvePreferredPhasedClipSet(out ResolvedWeaponPhasedActionClipSet resolved))
            {
                return false;
            }

            timing = new MeleeManifestPhasedGapCloseTiming
            {
                start_duration_ms = Mathf.Max(
                    0,
                    Mathf.RoundToInt(
                        resolved.ResolveStartTimelineLengthSeconds(phasedOpeningTailSeconds) * 1000f)),
                loop_duration_ms = Mathf.Max(
                    0,
                    Mathf.RoundToInt(resolved.ResolveLoopTimelineLengthSeconds() * 1000f)),
            };
            return true;
        }

        public bool TryBuildPhasedGapCloseHitWindows(out MeleeManifestHitWindow[] hitWindows)
        {
            hitWindows = Array.Empty<MeleeManifestHitWindow>();
            if (!TryBuildPhasedGapCloseTiming(out _)
                || !TryResolvePreferredPhasedClipSet(out ResolvedWeaponPhasedActionClipSet resolved))
            {
                return false;
            }

            var windows = new List<MeleeManifestHitWindow>();
            float startTimelineLengthSeconds =
                resolved.ResolveStartTimelineLengthSeconds(phasedOpeningTailSeconds);
            float startPlaybackOffsetSeconds =
                resolved.ResolveOpeningPlaybackStartSeconds(phasedOpeningTailSeconds);
            float startPlaybackEndSeconds =
                resolved.ResolveOpeningPlaybackEndSeconds(phasedOpeningTailSeconds);
            AppendPhasedGapCloseHitWindows(
                resolved.Start,
                "START",
                timelineOffsetSeconds: 0f,
                localStartTimeSeconds: startPlaybackOffsetSeconds,
                maxLocalTimeSeconds: startPlaybackEndSeconds,
                windows);

            float loopOffsetSeconds = startTimelineLengthSeconds;
            if (!resolved.ReleaseAfterStart)
            {
                AppendPhasedGapCloseHitWindows(
                    resolved.Loop,
                    "LOOP",
                    loopOffsetSeconds,
                    localStartTimeSeconds: 0f,
                    maxLocalTimeSeconds: float.PositiveInfinity,
                    windows);
            }

            float endOffsetSeconds = loopOffsetSeconds
                + (resolved.ReleaseAfterStart ? 0f : resolved.ResolveLoopTimelineLengthSeconds());
            AppendPhasedGapCloseHitWindows(
                resolved.End,
                "END",
                endOffsetSeconds,
                localStartTimeSeconds: 0f,
                maxLocalTimeSeconds: float.PositiveInfinity,
                windows);

            if (windows.Count == 0)
                return false;

            windows.Sort((left, right) => left.impact_delay_ms.CompareTo(right.impact_delay_ms));
            hitWindows = windows.ToArray();
            return true;
        }

        private bool TryResolvePreferredPhasedClipSet(
            out ResolvedWeaponPhasedActionClipSet resolved)
        {
            if (phasedGround.TryResolvePlayback(out resolved))
                return true;
            return phasedAir.TryResolvePlayback(out resolved);
        }

        private static void AppendPhasedGapCloseHitWindows(
            AnimationClip clip,
            string phase,
            float timelineOffsetSeconds,
            float localStartTimeSeconds,
            float maxLocalTimeSeconds,
            List<MeleeManifestHitWindow> destination)
        {
            var localTimes = new List<float>();
            CombatAnimationEvents.AppendEventTimes(
                clip,
                CombatAnimationEvents.OnStrikeHit,
                localTimes,
                offsetSeconds: 0f,
                maxLocalTimeSeconds: maxLocalTimeSeconds);
            for (int index = 0; index < localTimes.Count; index++)
            {
                if (localTimes[index] + 0.0001f < localStartTimeSeconds)
                    continue;

                float localTimeSeconds = Mathf.Max(0f, localTimes[index] - localStartTimeSeconds);
                destination.Add(new MeleeManifestHitWindow
                {
                    impact_delay_ms = Mathf.Max(
                        0,
                        Mathf.RoundToInt((timelineOffsetSeconds + localTimeSeconds) * 1000f)),
                    impact_phase = phase,
                    phase_delay_ms = Mathf.Max(0, Mathf.RoundToInt(localTimeSeconds * 1000f)),
                });
            }
        }

        public bool TryBuildHitWindowMirrorFromEvents(
            out WeaponStrikeHitWindowAuthoring[] mirroredHitWindows)
        {
            mirroredHitWindows = Array.Empty<WeaponStrikeHitWindowAuthoring>();
            if (!TryGetEffectiveStrikeHitTimesSeconds(out float[] eventTimesSeconds))
                return false;

            float timingReferenceLengthSeconds = ResolveTimingReferenceLengthSeconds();
            if (timingReferenceLengthSeconds <= 0f)
                return false;

            mirroredHitWindows = new WeaponStrikeHitWindowAuthoring[eventTimesSeconds.Length];
            for (int i = 0; i < eventTimesSeconds.Length; i++)
            {
                mirroredHitWindows[i] = new WeaponStrikeHitWindowAuthoring
                {
                    timeNormalized = Mathf.Clamp01(
                        eventTimesSeconds[i] / timingReferenceLengthSeconds),
                };
            }

            return true;
        }

        public bool ReferencesClip(AnimationClip? candidate)
        {
            if (candidate == null)
                return false;

            if (ReferenceEquals(clip, candidate))
                return true;

            return ReferenceEquals(phasedGround.start, candidate)
                || ReferenceEquals(phasedGround.loop, candidate)
                || ReferenceEquals(phasedGround.end, candidate)
                || ReferenceEquals(phasedAir.start, candidate)
                || ReferenceEquals(phasedAir.loop, candidate)
                || ReferenceEquals(phasedAir.end, candidate);
        }

        private bool AppendPhasedStrikeHitEventTimes(
            WeaponPhasedActionClipSet clipSet,
            List<float> destination)
        {
            if (!clipSet.TryResolvePlayback(out ResolvedWeaponPhasedActionClipSet resolved))
                return false;

            int initialCount = destination.Count;
            float startTimelineLengthSeconds =
                resolved.ResolveStartTimelineLengthSeconds(phasedOpeningTailSeconds);
            float startPlaybackOffsetSeconds =
                resolved.ResolveOpeningPlaybackStartSeconds(phasedOpeningTailSeconds);
            float startPlaybackEndSeconds =
                resolved.ResolveOpeningPlaybackEndSeconds(phasedOpeningTailSeconds);
            AppendClipEventTimesInPlaybackWindow(
                resolved.Start,
                CombatAnimationEvents.OnStrikeHit,
                destination,
                timelineOffsetSeconds: 0f,
                localStartTimeSeconds: startPlaybackOffsetSeconds,
                maxLocalTimeSeconds: startPlaybackEndSeconds);

            float loopPhaseOffsetSeconds = startTimelineLengthSeconds;
            if (!resolved.ReleaseAfterStart)
            {
                CombatAnimationEvents.AppendEventTimes(
                    resolved.Loop,
                    CombatAnimationEvents.OnStrikeHit,
                    destination,
                    loopPhaseOffsetSeconds);
            }

            float endPhaseOffsetSeconds = loopPhaseOffsetSeconds;
            if (!resolved.ReleaseAfterStart)
                endPhaseOffsetSeconds += resolved.ResolveLoopTimelineLengthSeconds();

            CombatAnimationEvents.AppendEventTimes(
                resolved.End,
                CombatAnimationEvents.OnStrikeHit,
                destination,
                endPhaseOffsetSeconds);

            return destination.Count > initialCount;
        }

        private static void AppendClipEventTimesInPlaybackWindow(
            AnimationClip clip,
            string eventName,
            List<float> destination,
            float timelineOffsetSeconds,
            float localStartTimeSeconds,
            float maxLocalTimeSeconds)
        {
            var localTimes = new List<float>();
            CombatAnimationEvents.AppendEventTimes(
                clip,
                eventName,
                localTimes,
                offsetSeconds: 0f,
                maxLocalTimeSeconds: maxLocalTimeSeconds);
            for (int index = 0; index < localTimes.Count; index++)
            {
                if (localTimes[index] + 0.0001f < localStartTimeSeconds)
                    continue;

                destination.Add(
                    timelineOffsetSeconds
                    + Mathf.Max(0f, localTimes[index] - localStartTimeSeconds));
            }
        }

        private float ResolvePhasedClipSetLengthSeconds(WeaponPhasedActionClipSet clipSet)
        {
            if (!clipSet.TryResolvePlayback(out ResolvedWeaponPhasedActionClipSet resolved))
                return 0f;

            float total = resolved.ResolveStartTimelineLengthSeconds(phasedOpeningTailSeconds);
            if (!resolved.ReleaseAfterStart)
                total += resolved.ResolveLoopTimelineLengthSeconds();
            total += Mathf.Max(0f, resolved.End.length);
            return total;
        }
    }

    [CreateAssetMenu(menuName = "Arena/Animation Set")]
    public class CombatAnimationSet : ScriptableObject
    {
        public const int AnimatorStrikeBankCount = 4;
        public const int AnimatorSpellBankCount = 4;

        [Header("Animation Set Identity")]
        [Tooltip("Stable id for this class/combat-profile animation set.")]
        public string animationSetId = "";
        [Tooltip("Gameplay combat profile this animation set exports for.")]
        public string combatProfileId = "";

        [Header("Weapon Presentation")]
        public WeaponPresentationProfile weaponPresentation = new();

        [Header("Locomotion — Idle")]
        public AnimationClip? locomotionIdle;
        public AnimationClip? locomotionIdleCombat;

        [Header("Locomotion — Directional (8 compass directions)")]
        public DirectionalClipSet walk;
        public DirectionalClipSet walkCombat;
        public DirectionalClipSet run;
        public DirectionalClipSet runCombat;

        [Header("Locomotion — Directional Stops (8 compass directions)")]
        public DirectionalClipSet walkStop;
        public DirectionalClipSet walkStopCombat;
        public DirectionalClipSet runStop;
        public DirectionalClipSet runStopCombat;

        [Header("Turns")]
        public AnimationClip? turn90L;
        public AnimationClip? turn90R;
        public AnimationClip? turn180L;
        public AnimationClip? turn180R;
        public AnimationClip? turn90CombatL;
        public AnimationClip? turn90CombatR;
        public AnimationClip? turn180CombatL;
        public AnimationClip? turn180CombatR;

        [Header("Combat Mode Locomotion")]
        [Tooltip("Optional locomotion slot overrides keyed by CombatModeCatalog mode_id. Used for stance modes such as dagger stealth.")]
        public CombatAnimationLocomotionModeOverride[] locomotionModeOverrides = Array.Empty<CombatAnimationLocomotionModeOverride>();

        [Header("Airborne")]
        public CardinalClipSet jumpStart;
        public CardinalClipSet jumpStartCombat;
        public AnimationClip? freeFall;
        public AnimationClip? freeFallCombat;
        public CardinalClipSet jumpLand;
        public CardinalClipSet jumpLandCombat;

        [Header("Combat Stance Transitions")]
        public AnimationClip? enterCombatIdle;
        public AnimationClip? enterCombatWalk;
        public AnimationClip? enterCombatRun;
        public AnimationClip? exitCombatIdle;
        public AnimationClip? exitCombatWalk;
        public AnimationClip? exitCombatRun;

        [Header("Spell Cast Motions")]
        [Tooltip("Legacy migration bindings keyed by semantic spell cast motion. New spell assignments should select a shared catalog recipe instead.")]
        public SpellCastMotionBinding[] spellCastMotionBindings = Array.Empty<SpellCastMotionBinding>();

        [Header("Spell Cast Overrides")]
        [Tooltip("Optional per-spell recipe and mirror exceptions for shared catalog animations that do not fit this combat pose. Leave empty to use each spell's global unmirrored selection.")]
        public SpellCastAnimationOverride[] spellCastAnimationOverrides = Array.Empty<SpellCastAnimationOverride>();

        [Header("Spell Cast Hold")]
        [Tooltip("Default enter/idle presentation for cast-time spells. Release clips come from the resolved motion family or fixed spell assignment.")]
        public SpellCastHoldProfile defaultSpellCastHold;

        [Tooltip("Which hand this weapon set uses when a spell resolves to a one-handed cast family. Two-handed families always use both hands regardless. Direct2H falls back to Direct1H with this hand when the set has no Direct2H binding. See docs/spell-cast-animation-stitching-2026-07-09.md.")]
        public SpellCastHand oneHandedCastHand = SpellCastHand.Left;

        /// <summary>The one-handed cast hand for family resolution (Left/Right only; TwoHand collapses to Left).</summary>
        public SpellCastHand OneHandedCastHand => oneHandedCastHand == SpellCastHand.Right ? SpellCastHand.Right : SpellCastHand.Left;

        [Header("Status Reactions")]
        [Tooltip("Looping status reaction animations keyed by runtime status kind.")]
        public StatusReactionAnimationEntry[] statusReactions = Array.Empty<StatusReactionAnimationEntry>();

        [Header("Weapon Presentation Effects")]
        [Tooltip("Presentation-only weapon visual effects keyed by authoritative combat facts.")]
        public WeaponPresentationEffectEntry[] weaponPresentationEffects = Array.Empty<WeaponPresentationEffectEntry>();

        [Header("Melee Attacks")]
        [Tooltip("Authored melee attacks for this animation set.")]
        public List<WeaponMeleeAttackAuthoring> meleeAttacks = new();

        [Header("Animation VFX Slots")]
        [Tooltip("Reusable semantic VFX timing and transform tracks keyed by animation clip and slot id. Attacks decide whether and which VFX fills each slot.")]
        public List<CombatAnimationVfxTrack> animationVfxTracks = new();

        [Header("Auto Attack")]
        [Tooltip("Design-facing authored strike id used for this combat profile's intrinsic repeating auto-attack. Defaults to Strike 1 if left empty.")]
        public string autoAttackAuthoredStrikeId = "";
        [Tooltip("Ordered authored strike ids played during one intrinsic auto-attack charge. Most combat sets use one entry; multi-strike sets author each swing here in order.")]
        public List<string> autoAttackVisualSequenceActionIds = new();
        [Min(0)]
        [Tooltip("Delay in milliseconds between consecutive strikes in this auto-attack sequence. This does not change the normal auto-attack charge/cooldown interval.")]
        public int autoAttackSequenceIntervalMs;

        [Header("Dodge Actions")]
        public DirectionalClipSet dodge;
        public DirectionalClipSet dodgeCombat;
        public DirectionalClipSet airDodge;
        public DirectionalClipSet airDodgeCombat;

        [Header("Defensive Actions - Directional Walk Block")]
        public DirectionalClipSet blockWalkStart;
        public DirectionalClipSet blockWalkLoop;
        public DirectionalClipSet blockWalkEnd;

        [Header("Defensive Actions - Stationary")]
        public AnimationClip? parryStart;
        public AnimationClip? parryHit;
        public AnimationClip? blockStart;
        public AnimationClip? blockLoop;
        public AnimationClip? blockEnd;
        public AnimationClip? blockHit;
        public AnimationClip? blockHitBreak;

        [Header("Hit Reactions - Grounded Out Of Combat")]
        public AnimationClip? hitF;
        public AnimationClip? hitB;
        public AnimationClip? hitL;
        public AnimationClip? hitR;

        [Header("Hit Reactions - Grounded Combat")]
        public AnimationClip? hitCombatF;
        public AnimationClip? hitCombatB;
        public AnimationClip? hitCombatL;
        public AnimationClip? hitCombatR;

        [Header("Hit Reactions - Airborne Out Of Combat")]
        public AnimationClip? airHitF;
        public AnimationClip? airHitB;
        public AnimationClip? airHitL;
        public AnimationClip? airHitR;

        [Header("Hit Reactions - Airborne Combat")]
        public AnimationClip? airHitCombatF;
        public AnimationClip? airHitCombatB;
        public AnimationClip? airHitCombatL;
        public AnimationClip? airHitCombatR;

        [Header("Stagger Reactions (4 directional large hits)")]
        public AnimationClip? staggerF;
        public AnimationClip? staggerB;
        public AnimationClip? staggerL;
        public AnimationClip? staggerR;

        public AnimationClip? knockdownStart;
        public AnimationClip? knockdownLoop;
        public AnimationClip? getUp;

        [Header("Stun")]
        [Tooltip("Authored stun entry clip. Current runtime stun presentation is loop-only.")]
        public AnimationClip? stunStart;
        public AnimationClip? stunLoop;
        [Tooltip("Authored stun recovery clip. Current runtime stun presentation is loop-only.")]
        public AnimationClip? stunEnd;

        [Header("Death")]
        public AnimationClip? death;

        public string AnimationSetIdOrDefault => string.IsNullOrWhiteSpace(animationSetId)
            ? string.Empty
            : WireIdentifier.Normalize(animationSetId);

        public string CombatProfileIdOrDefault => string.IsNullOrWhiteSpace(combatProfileId)
            ? string.Empty
            : WireIdentifier.Normalize(combatProfileId);

        public bool EnsureAnimationSetIdentityInitialized()
        {
            bool changed = false;
            if (!string.IsNullOrWhiteSpace(combatProfileId))
            {
                string normalized = WireIdentifier.Normalize(combatProfileId);
                if (!string.Equals(combatProfileId, normalized, StringComparison.Ordinal))
                {
                    combatProfileId = normalized;
                    changed = true;
                }
            }

            if (!string.IsNullOrWhiteSpace(animationSetId))
            {
                string normalized = WireIdentifier.Normalize(animationSetId);
                if (!string.Equals(animationSetId, normalized, StringComparison.Ordinal))
                {
                    animationSetId = normalized;
                    changed = true;
                }
            }

            return changed;
        }

        public WeaponPresentationProfile WeaponPresentation
        {
            get
            {
                EnsureWeaponPresentationProfileInitialized();
                return weaponPresentation;
            }
        }

        public AnimationClip? DrawWeaponClip => WeaponPresentation.DrawWeapon;
        public AnimationClip? SheathWeaponClip => WeaponPresentation.SheathWeapon;
        public float DrawWeaponHandoffTime => WeaponPresentation.DrawWeaponHandoffTime;
        public float SheathWeaponHandoffTime => WeaponPresentation.SheathWeaponHandoffTime;
        public WeaponVisualBinding[] VisualBindings => WeaponPresentation.VisualsOrEmpty;
        public CombatAnimationLocomotionModeOverride[] LocomotionModeOverridesOrEmpty =>
            locomotionModeOverrides ?? Array.Empty<CombatAnimationLocomotionModeOverride>();

        public bool TryGetLocomotionModeOverride(
            string? modeId,
            out CombatAnimationLocomotionModeOverride? modeOverride)
        {
            modeOverride = null;
            if (string.IsNullOrWhiteSpace(modeId))
                return false;

            string normalizedModeId = modeId.Trim().ToUpperInvariant();
            foreach (CombatAnimationLocomotionModeOverride candidate in LocomotionModeOverridesOrEmpty)
            {
                if (candidate == null || !candidate.HasAny)
                    continue;

                if (string.Equals(candidate.ModeIdOrEmpty, normalizedModeId, StringComparison.Ordinal))
                {
                    modeOverride = candidate;
                    return true;
                }
            }

            return false;
        }

        public bool EnsureWeaponPresentationProfileInitialized()
        {
            if (weaponPresentation == null)
            {
                weaponPresentation = new WeaponPresentationProfile();
                return true;
            }

            return false;
        }

        private void OnValidate()
        {
            EnsureAnimationSetIdentityInitialized();
            EnsureWeaponPresentationProfileInitialized();
            SpellCastAnimationResolver.InvalidateCache();
        }

        public int MeleeAttackCount
        {
            get
            {
                EnsureMeleeAttackListInitialized();
                return meleeAttacks.Count;
            }
        }

        public bool EnsureMeleeAttackListInitialized()
        {
            if (meleeAttacks != null)
                return false;

            meleeAttacks = new List<WeaponMeleeAttackAuthoring>();
            return true;
        }

        public void EnsureMeleeAttackListSize(int desiredCount)
        {
            EnsureMeleeAttackListInitialized();
            if (desiredCount < 0)
                desiredCount = 0;

            while (meleeAttacks.Count < desiredCount)
            {
                int strikeIndex = meleeAttacks.Count + 1;
                meleeAttacks.Add(CreateDefaultMeleeAttack(strikeIndex));
            }

            while (meleeAttacks.Count > desiredCount)
                meleeAttacks.RemoveAt(meleeAttacks.Count - 1);
        }

        public AnimationClip? GetStrikeClip(int strikeIndex)
        {
            if (strikeIndex <= 0)
                return null;

            EnsureMeleeAttackListInitialized();
            int zeroBasedIndex = strikeIndex - 1;
            if (zeroBasedIndex >= 0 && zeroBasedIndex < meleeAttacks.Count)
                return meleeAttacks[zeroBasedIndex].clip;
            return null;
        }

        public IReadOnlyList<CombatAnimationVfxBinding> GetStrikeAnimationVfxBindings(int strikeIndex)
        {
            if (strikeIndex <= 0)
                return Array.Empty<CombatAnimationVfxBinding>();

            EnsureMeleeAttackListInitialized();
            int zeroBasedIndex = strikeIndex - 1;
            if (zeroBasedIndex < 0 || zeroBasedIndex >= meleeAttacks.Count)
                return Array.Empty<CombatAnimationVfxBinding>();

            List<CombatAnimationVfxBinding>? bindings = meleeAttacks[zeroBasedIndex].animationVfxBindings;
            return bindings != null
                ? bindings
                : Array.Empty<CombatAnimationVfxBinding>();
        }

        public IEnumerable<CombatAnimationVfxTrack> GetAnimationVfxTracks(AnimationClip? clip)
        {
            if (clip == null || animationVfxTracks == null)
                yield break;

            for (int index = 0; index < animationVfxTracks.Count; index++)
            {
                CombatAnimationVfxTrack? track = animationVfxTracks[index];
                if (track != null && ReferenceEquals(track.clip, clip))
                    yield return track;
            }
        }

        public bool TryGetAnimationVfxTrack(
            AnimationClip? clip,
            string slotId,
            out CombatAnimationVfxTrack track)
        {
            string normalizedSlotId = CombatAnimationVfxTrack.NormalizeSlotId(slotId);
            if (clip != null && !string.IsNullOrEmpty(normalizedSlotId) && animationVfxTracks != null)
            {
                for (int index = 0; index < animationVfxTracks.Count; index++)
                {
                    CombatAnimationVfxTrack? candidate = animationVfxTracks[index];
                    if (candidate != null
                        && ReferenceEquals(candidate.clip, clip)
                        && string.Equals(candidate.NormalizedSlotId, normalizedSlotId, StringComparison.Ordinal))
                    {
                        track = candidate;
                        return true;
                    }
                }
            }

            track = null!;
            return false;
        }

        public float GetStrikeTimingReferenceLengthSeconds(int strikeIndex)
        {
            if (strikeIndex <= 0)
                return 0f;

            EnsureMeleeAttackListInitialized();
            int zeroBasedIndex = strikeIndex - 1;
            if (zeroBasedIndex >= 0 && zeroBasedIndex < meleeAttacks.Count)
                return meleeAttacks[zeroBasedIndex].ResolveTimingReferenceLengthSeconds();
            return 0f;
        }

        public float GetStrikeStartupTrimSeconds(int strikeIndex)
        {
            if (strikeIndex <= 0)
                return 0f;

            EnsureMeleeAttackListInitialized();
            int zeroBasedIndex = strikeIndex - 1;
            return zeroBasedIndex >= 0 && zeroBasedIndex < meleeAttacks.Count
                ? meleeAttacks[zeroBasedIndex].ResolveStartupTrimSeconds()
                : 0f;
        }

        public float GetStrikeStartupTrimNormalized(int strikeIndex)
        {
            if (strikeIndex <= 0)
                return 0f;

            EnsureMeleeAttackListInitialized();
            int zeroBasedIndex = strikeIndex - 1;
            return zeroBasedIndex >= 0 && zeroBasedIndex < meleeAttacks.Count
                ? meleeAttacks[zeroBasedIndex].ResolveStartupTrimNormalized()
                : 0f;
        }

        public float GetStrikeFirstHitWindowSeconds(int strikeIndex)
            => TryGetStrikeFirstHitWindowSeconds(strikeIndex, out float seconds) ? seconds : 0f;

        public bool TryGetStrikeFirstHitWindowSeconds(int strikeIndex, out float seconds)
        {
            seconds = 0f;
            if (strikeIndex <= 0)
                return false;

            EnsureMeleeAttackListInitialized();
            int zeroBasedIndex = strikeIndex - 1;
            if (zeroBasedIndex < 0 || zeroBasedIndex >= meleeAttacks.Count)
                return false;

            float timingReferenceLengthSeconds = meleeAttacks[zeroBasedIndex].ResolveTimingReferenceLengthSeconds();
            if (timingReferenceLengthSeconds <= 0f)
                return false;

            if (meleeAttacks[zeroBasedIndex].TryGetEffectiveStrikeHitTimesSeconds(out float[] eventTimesSeconds)
                && eventTimesSeconds.Length > 0)
            {
                seconds = eventTimesSeconds[0];
                return true;
            }

            if (meleeAttacks[zeroBasedIndex].combat.ResolvedHitWindowCount <= 0)
                return false;

            seconds = meleeAttacks[zeroBasedIndex].combat.FirstImpactDelayMs(timingReferenceLengthSeconds) / 1000f;
            return true;
        }

        public float GetVisualInterruptibleAtSeconds(int strikeIndex, bool grounded)
        {
            if (strikeIndex <= 0)
                return 0f;

            EnsureMeleeAttackListInitialized();
            int zeroBasedIndex = strikeIndex - 1;
            if (zeroBasedIndex >= 0 && zeroBasedIndex < meleeAttacks.Count)
                return meleeAttacks[zeroBasedIndex].ResolveVisualInterruptibleAtSeconds(grounded);
            return 0f;
        }

        public float GetLowerBodyUnlockAtSeconds(int strikeIndex, bool grounded)
        {
            if (strikeIndex <= 0)
                return 0f;

            EnsureMeleeAttackListInitialized();
            int zeroBasedIndex = strikeIndex - 1;
            if (zeroBasedIndex >= 0 && zeroBasedIndex < meleeAttacks.Count)
                return meleeAttacks[zeroBasedIndex].ResolveLowerBodyUnlockAtSeconds(grounded);
            return 0f;
        }

        public float GetLowerBodyBlendOutSeconds(int strikeIndex, float defaultBlendOutSeconds)
        {
            if (strikeIndex <= 0)
                return Mathf.Max(0f, defaultBlendOutSeconds);

            EnsureMeleeAttackListInitialized();
            int zeroBasedIndex = strikeIndex - 1;
            if (zeroBasedIndex >= 0 && zeroBasedIndex < meleeAttacks.Count)
                return meleeAttacks[zeroBasedIndex].ResolveLowerBodyBlendOutSeconds(defaultBlendOutSeconds);
            return Mathf.Max(0f, defaultBlendOutSeconds);
        }

        public int GetStrikeIndexForActionId(string actionId)
        {
            if (string.IsNullOrWhiteSpace(actionId))
                return 0;

            string normalizedActionId = actionId.Trim();
            string normalizedSlotId = CombatActionIds.NormalizeRuntimeActionReference(actionId);
            if (MatchesAutoAttackActionId(normalizedActionId, normalizedSlotId))
                return AutoAttackVisualSourceStrikeIndex;
            return GetRegularStrikeIndexForActionReference(normalizedActionId, normalizedSlotId);
        }

        private int GetRegularStrikeIndexForActionReference(string normalizedActionId, string normalizedSlotId)
        {
            for (int strikeIndex = 1; strikeIndex <= MeleeAttackCount; strikeIndex++)
            {
                var combat = GetStrikeCombat(strikeIndex);
                if (string.Equals(combat.RuntimeSlotIdOrDefault, normalizedSlotId, StringComparison.Ordinal))
                    return strikeIndex;
                if (string.Equals(combat.AuthoredStrikeIdOrDefault, normalizedActionId, StringComparison.OrdinalIgnoreCase))
                    return strikeIndex;
            }

            return 0;
        }

        public string ResolveRuntimeSlotIdForStrikeReference(string strikeReference)
        {
            if (string.IsNullOrWhiteSpace(strikeReference))
                return string.Empty;

            string normalizedReference = strikeReference.Trim();
            if (string.Equals(
                    AutoAttackAuthoredStrikeIdOrDefault,
                    normalizedReference,
                    StringComparison.OrdinalIgnoreCase))
            {
                return CombatActionIds.NormalizeRuntimeActionReference(AutoAttackAuthoredStrikeIdOrDefault);
            }

            for (int strikeIndex = 1; strikeIndex <= MeleeAttackCount; strikeIndex++)
            {
                var combat = GetStrikeCombat(strikeIndex);
                if (string.Equals(combat.AuthoredStrikeIdOrDefault, normalizedReference, StringComparison.OrdinalIgnoreCase))
                    return combat.RuntimeSlotIdOrDefault;
            }

            return CombatActionIds.NormalizeRuntimeActionReference(normalizedReference);
        }

        public string ResolveAuthoredStrikeIdForRuntimeAction(string actionId)
        {
            string normalizedActionId = string.IsNullOrWhiteSpace(actionId) ? string.Empty : actionId.Trim();
            string normalizedSlotId = CombatActionIds.NormalizeRuntimeActionReference(actionId);
            if (MatchesAutoAttackActionId(normalizedActionId, normalizedSlotId))
                return AutoAttackAuthoredStrikeIdOrDefault;

            int strikeIndex = GetStrikeIndexForActionId(actionId);
            if (strikeIndex <= 0)
                return string.IsNullOrWhiteSpace(actionId) ? string.Empty : actionId.Trim();

            return GetStrikeCombat(strikeIndex).AuthoredStrikeIdOrDefault;
        }

        public bool TryGetSpellCastFamily(SpellCastMotion motion, out string familyBaseName)
            => TryGetSpellCastFamily(motion, out familyBaseName, out _);

        public bool TryGetSpellCastFamily(
            SpellCastMotion motion,
            out string familyBaseName,
            out SpellCastMotion resolvedMotion)
        {
            if (TryGetExactSpellCastFamily(motion, out familyBaseName))
            {
                resolvedMotion = motion;
                return true;
            }

            // A Direct2H classification expresses the spell's preferred gesture. Weapon sets that
            // cannot free both hands deliberately fall back to their authored Direct1H family and
            // oneHandedCastHand instead of failing animation resolution.
            if (motion == SpellCastMotion.Direct2H
                && TryGetExactSpellCastFamily(SpellCastMotion.Direct1H, out familyBaseName))
            {
                resolvedMotion = SpellCastMotion.Direct1H;
                return true;
            }

            familyBaseName = string.Empty;
            resolvedMotion = SpellCastMotion.None;
            return false;
        }

        public bool TryGetSpellCastAnimationOverride(
            string spellId,
            out SpellCastAnimationOverride animationOverride)
        {
            string normalizedSpellId = string.IsNullOrWhiteSpace(spellId)
                ? string.Empty
                : spellId.Trim().ToUpperInvariant();
            if (normalizedSpellId.Length != 0 && spellCastAnimationOverrides != null)
            {
                for (int index = 0; index < spellCastAnimationOverrides.Length; index++)
                {
                    SpellCastAnimationOverride candidate = spellCastAnimationOverrides[index];
                    if (!string.Equals(candidate.SpellIdOrEmpty, normalizedSpellId, StringComparison.Ordinal)
                        || !candidate.HasAny)
                    {
                        continue;
                    }

                    animationOverride = candidate;
                    return true;
                }
            }

            animationOverride = default;
            return false;
        }

        public bool TryGetSpellCastAnimationOverride(string spellId, out string animationId)
        {
            if (TryGetSpellCastAnimationOverride(
                    spellId,
                    out SpellCastAnimationOverride animationOverride)
                && animationOverride.AnimationIdOrEmpty.Length != 0)
            {
                animationId = animationOverride.AnimationIdOrEmpty;
                return true;
            }

            animationId = string.Empty;
            return false;
        }

        private bool TryGetExactSpellCastFamily(SpellCastMotion motion, out string familyBaseName)
        {
            if (motion != SpellCastMotion.None && spellCastMotionBindings != null)
            {
                for (int index = 0; index < spellCastMotionBindings.Length; index++)
                {
                    SpellCastMotionBinding candidate = spellCastMotionBindings[index];
                    if (candidate.motion != motion || candidate.FamilyBaseNameOrEmpty.Length == 0)
                        continue;

                    familyBaseName = candidate.FamilyBaseNameOrEmpty;
                    return true;
                }
            }

            familyBaseName = string.Empty;
            return false;
        }

        public bool TryGetDefaultSpellCastHoldProfile(out SpellCastHoldProfile profile)
        {
            profile = defaultSpellCastHold;
            return profile.IsPlayable;
        }

        public bool TryGetSpellCastHoldProfile(string spellId, out SpellCastHoldProfile profile)
        {
            SpellCastHoldProfile defaultProfile = defaultSpellCastHold;
            if (SpellCastAnimationResolver.TryResolve(this, spellId, out WeaponSpellAnimationEntry entry))
            {
                // A catalog recipe owns its complete presentation lifecycle. In particular,
                // ReleaseOnly must not inherit this combat set's legacy default cast hold.
                if (!entry.UsesHoldPresentation)
                {
                    profile = default;
                    return false;
                }

                if (entry.TryResolveHoldProfile(defaultProfile, out profile))
                    return true;
            }

            profile = defaultProfile;
            return profile.IsPlayable;
        }

        public bool TryGetStatusReaction(string statusKind, out StatusReactionAnimationEntry entry)
        {
            string normalizedStatusKind = string.IsNullOrWhiteSpace(statusKind)
                ? string.Empty
                : statusKind.Trim().ToUpperInvariant();

            if (!string.IsNullOrEmpty(normalizedStatusKind) && statusReactions != null)
            {
                for (int index = 0; index < statusReactions.Length; index++)
                {
                    StatusReactionAnimationEntry candidate = statusReactions[index];
                    if (!string.Equals(candidate.StatusKindOrEmpty, normalizedStatusKind, StringComparison.Ordinal))
                        continue;

                    entry = candidate;
                    return candidate.HasAny;
                }
            }

            entry = default;
            return false;
        }

        public HitReactionClipSet ResolveHitReactionClips(bool grounded, bool inCombat)
        {
            if (grounded)
            {
                return inCombat
                    ? new HitReactionClipSet(hitCombatF, hitCombatB, hitCombatL, hitCombatR)
                    : new HitReactionClipSet(hitF, hitB, hitL, hitR);
            }

            return inCombat
                ? new HitReactionClipSet(airHitCombatF, airHitCombatB, airHitCombatL, airHitCombatR)
                : new HitReactionClipSet(airHitF, airHitB, airHitL, airHitR);
        }

        public IEnumerable<WeaponPresentationEffectEntry> MatchingConsumedMeleeModifierEffects(
            string consumedStatusKind,
            string consumedStackGroup)
        {
            if (weaponPresentationEffects == null || string.IsNullOrWhiteSpace(consumedStatusKind) || string.IsNullOrWhiteSpace(consumedStackGroup))
                yield break;

            for (int index = 0; index < weaponPresentationEffects.Length; index++)
            {
                WeaponPresentationEffectEntry entry = weaponPresentationEffects[index];
                if (entry.MatchesConsumedMeleeModifier(consumedStatusKind, consumedStackGroup))
                    yield return entry;
            }
        }

        public bool TryGetPhasedMeleeEntry(string actionId, out WeaponPhasedActionEntry entry)
        {
            string normalizedActionId = WireIdentifier.Normalize(
                ResolveAuthoredStrikeIdForRuntimeAction(actionId));

            EnsureMeleeAttackListInitialized();
            if (!string.IsNullOrEmpty(normalizedActionId))
            {
                for (int index = 0; index < meleeAttacks.Count; index++)
                {
                    WeaponMeleeAttackAuthoring attack = meleeAttacks[index];
                    if (!string.Equals(
                            WireIdentifier.Normalize(attack.combat.AuthoredStrikeIdOrDefault),
                            normalizedActionId,
                            StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (attack.TryBuildPhasedActionEntry(out entry))
                        return true;
                    break;
                }
            }

            entry = default;
            return false;
        }

        public WeaponStrikeCombatAuthoring GetStrikeCombat(int strikeIndex)
        {
            if (strikeIndex <= 0)
                throw new ArgumentOutOfRangeException(nameof(strikeIndex));

            EnsureMeleeAttackListInitialized();
            int zeroBasedIndex = strikeIndex - 1;
            if (zeroBasedIndex >= 0 && zeroBasedIndex < meleeAttacks.Count)
                return meleeAttacks[zeroBasedIndex].combat;
            throw new ArgumentOutOfRangeException(nameof(strikeIndex));
        }

        public void SetStrikeCombat(int strikeIndex, WeaponStrikeCombatAuthoring value)
        {
            if (strikeIndex <= 0)
                throw new ArgumentOutOfRangeException(nameof(strikeIndex));

            EnsureMeleeAttackListInitialized();
            int zeroBasedIndex = strikeIndex - 1;
            if (zeroBasedIndex < 0 || zeroBasedIndex >= meleeAttacks.Count)
                throw new ArgumentOutOfRangeException(nameof(strikeIndex));

            WeaponMeleeAttackAuthoring authored = meleeAttacks[zeroBasedIndex];
            authored.combat = value;
            meleeAttacks[zeroBasedIndex] = authored;
        }

        public MeleeManifestDocument BuildMeleeExport()
        {
            return new MeleeManifestDocument
            {
                profiles = new[]
                {
                    new MeleeManifestProfile
                    {
                        combat_profile = CombatProfileIdOrDefault,
                        stagger_duration_f_ms = RequiredClipDurationMs(staggerF, nameof(staggerF)),
                        stagger_duration_b_ms = RequiredClipDurationMs(staggerB, nameof(staggerB)),
                        stagger_duration_l_ms = RequiredClipDurationMs(staggerL, nameof(staggerL)),
                        stagger_duration_r_ms = RequiredClipDurationMs(staggerR, nameof(staggerR)),
                        auto_attack_strike_id = AutoAttackAuthoredStrikeIdOrDefault,
                        auto_attack_sequence = AutoAttackVisualSequenceActionIds,
                        auto_attack_sequence_interval_ms = Mathf.Max(0, autoAttackSequenceIntervalMs),
                        strikes = BuildExportStrikes(),
                    },
                },
            };
        }

        private int RequiredClipDurationMs(AnimationClip? clip, string fieldName)
        {
            if (clip == null)
                throw new InvalidOperationException($"{name} cannot export melee manifest: {fieldName} is missing.");
            return Mathf.Max(1, Mathf.RoundToInt(clip.length * 1000f));
        }

        private MeleeManifestStrike[] BuildExportStrikes()
        {
            var strikes = new List<MeleeManifestStrike>(MeleeAttackCount + 1);
            EnsureMeleeAttackListInitialized();
            for (int strikeIndex = 1; strikeIndex <= MeleeAttackCount; strikeIndex++)
                strikes.Add(BuildExportStrike(meleeAttacks[strikeIndex - 1]));

            if (!ContainsAuthoredStrikeId(AutoAttackAuthoredStrikeIdOrDefault))
                strikes.Add(BuildExportAutoAttackStrike());

            return strikes.ToArray();
        }

        private MeleeManifestStrike BuildExportAutoAttackStrike()
        {
            int visualSourceStrikeIndex = AutoAttackVisualSourceStrikeIndex;
            EnsureMeleeAttackListInitialized();
            var exported = BuildExportStrike(meleeAttacks[visualSourceStrikeIndex - 1]);
            exported.id = AutoAttackAuthoredStrikeIdOrDefault;
            exported.slot_id = CombatActionIds.NormalizeRuntimeActionReference(AutoAttackAuthoredStrikeIdOrDefault);
            exported.combo_from = string.Empty;
            return exported;
        }

        private MeleeManifestStrike BuildExportStrike(WeaponMeleeAttackAuthoring attack)
        {
            WeaponStrikeCombatAuthoring strike = attack.combat;
            float timingReferenceLengthSeconds = attack.ResolveTimingReferenceLengthSeconds();
            return new MeleeManifestStrike
            {
                id = strike.AuthoredStrikeIdOrDefault,
                slot_id = strike.RuntimeSlotIdOrDefault,
                hit_windows = BuildExportHitWindows(timingReferenceLengthSeconds, attack),
                recovery_ms = strike.RecoveryMsInt,
                is_gap_closer = false,
                combo_from = ResolveRuntimeSlotIdForStrikeReference(strike.ComboFromOrEmpty),
                combo_open_ms = strike.ComboOpenMsInt,
                combo_grace_ms = strike.ComboGraceMsInt,
                aerial_execution_mode = strike.aerialExecutionMode switch
                {
                    AerialExecutionMode.GroundedOrAirborne => "GROUNDED_OR_AIRBORNE",
                    AerialExecutionMode.AirborneOnly => "AIRBORNE_ONLY",
                    _ => "GROUNDED_ONLY",
                },
                projectile = strike.usesProjectileDelivery
                    ? new MeleeManifestProjectile
                    {
                        projectile_id = strike.ProjectileIdOrDefault,
                        speed = strike.ProjectileSpeedExportValue,
                        max_distance = strike.ProjectileMaxDistanceExportValue,
                        radius = strike.ProjectileRadiusExportValue,
                        spawn_forward = strike.ProjectileSpawnForwardExportValue,
                        spawn_height = strike.ProjectileSpawnHeightExportValue,
                        aim_height_scale = strike.ProjectileAimHeightScaleExportValue,
                        requires_initial_line_of_sight = strike.projectileRequiresInitialLineOfSight,
                        update_interval_seconds = strike.ProjectileUpdateIntervalSecondsExportValue,
                    }
                    : null,
                phased_gap_close_timing = attack.TryBuildPhasedGapCloseTiming(out var phasedTiming)
                    ? phasedTiming
                    : null,
            };
        }

        private static MeleeManifestHitWindow[] BuildExportHitWindows(
            float timingReferenceLengthSeconds,
            WeaponMeleeAttackAuthoring attack)
        {
            if (attack.TryBuildPhasedGapCloseHitWindows(out MeleeManifestHitWindow[] phasedHitWindows))
                return phasedHitWindows;

            if (attack.TryGetEffectiveStrikeHitTimesSeconds(out float[] eventTimesSeconds))
            {
                var eventExported = new MeleeManifestHitWindow[eventTimesSeconds.Length];
                for (int i = 0; i < eventTimesSeconds.Length; i++)
                {
                    eventExported[i] = new MeleeManifestHitWindow
                    {
                        impact_delay_ms = Mathf.Max(0, Mathf.RoundToInt(eventTimesSeconds[i] * 1000f)),
                    };
                }

                return eventExported;
            }

            var hitWindows = attack.combat.GetResolvedHitWindows();
            var exported = new MeleeManifestHitWindow[hitWindows.Length];
            for (int i = 0; i < hitWindows.Length; i++)
            {
                exported[i] = new MeleeManifestHitWindow
                {
                    impact_delay_ms = hitWindows[i].ImpactDelayMs(timingReferenceLengthSeconds),
                };
            }

            return exported;
        }

        public string AutoAttackAuthoredStrikeIdOrDefault => string.IsNullOrWhiteSpace(autoAttackAuthoredStrikeId)
            ? "AUTO_ATTACK_1"
            : autoAttackAuthoredStrikeId.Trim();

        public string[] AutoAttackVisualSequenceActionIds
        {
            get
            {
                if (autoAttackVisualSequenceActionIds != null && autoAttackVisualSequenceActionIds.Count > 0)
                {
                    var resolved = new List<string>(autoAttackVisualSequenceActionIds.Count);
                    for (int index = 0; index < autoAttackVisualSequenceActionIds.Count; index++)
                    {
                        string actionId = autoAttackVisualSequenceActionIds[index]?.Trim() ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(actionId))
                            resolved.Add(actionId);
                    }

                    if (resolved.Count > 0)
                        return resolved.ToArray();
                }

                return new[] { GetDefaultAutoAttackVisualSourceActionId() };
            }
        }

        public string AutoAttackVisualSourceActionIdOrDefault => AutoAttackVisualSequenceActionIds[0];

        private int AutoAttackVisualSourceStrikeIndex
        {
            get
            {
                string sourceActionId = AutoAttackVisualSourceActionIdOrDefault;
                for (int strikeIndex = 1; strikeIndex <= MeleeAttackCount; strikeIndex++)
                {
                    var combat = GetStrikeCombat(strikeIndex);
                    if (string.Equals(combat.AuthoredStrikeIdOrDefault, sourceActionId, StringComparison.OrdinalIgnoreCase))
                        return strikeIndex;
                    if (string.Equals(combat.RuntimeSlotIdOrDefault, sourceActionId, StringComparison.Ordinal))
                        return strikeIndex;
                }

                return 1;
            }
        }

        public bool IsAutoAttackVisualSourceStrike(int strikeIndex)
            => strikeIndex == AutoAttackVisualSourceStrikeIndex;

        public bool IsAutoAttackVisualSequenceTransition(int activeStrikeIndex, string incomingActionId)
        {
            if (!TryGetAutoAttackVisualSequencePosition(activeStrikeIndex, out int activePosition)
                || !TryGetAutoAttackVisualSequencePosition(incomingActionId, out int incomingPosition))
            {
                return false;
            }

            return incomingPosition == activePosition + 1;
        }

        public bool IsAutoAttackVisualSequenceRestart(int activeStrikeIndex, string incomingActionId)
        {
            if (!TryGetAutoAttackVisualSequencePosition(activeStrikeIndex, out int activePosition)
                || !TryGetAutoAttackVisualSequencePosition(incomingActionId, out int incomingPosition))
            {
                return false;
            }

            return activePosition > 0 && incomingPosition == 0;
        }

        public bool IsAutoAttackVisualSequenceContinuation(string actionId)
            => TryGetAutoAttackVisualSequencePosition(actionId, out int position) && position > 0;

        private bool TryGetAutoAttackVisualSequencePosition(int strikeIndex, out int position)
        {
            if (strikeIndex <= 0)
            {
                position = -1;
                return false;
            }

            string[] sequence = AutoAttackVisualSequenceActionIds;
            for (int sequenceIndex = 0; sequenceIndex < sequence.Length; sequenceIndex++)
            {
                if (ResolveAutoAttackVisualSequenceStrikeIndex(sequence[sequenceIndex]) == strikeIndex)
                {
                    position = sequenceIndex;
                    return true;
                }
            }

            position = -1;
            return false;
        }

        private bool TryGetAutoAttackVisualSequencePosition(string actionId, out int position)
        {
            if (string.IsNullOrWhiteSpace(actionId))
            {
                position = -1;
                return false;
            }

            string normalizedActionId = actionId.Trim();
            string normalizedSlotId = CombatActionIds.NormalizeRuntimeActionReference(actionId);
            if (MatchesAutoAttackActionId(normalizedActionId, normalizedSlotId))
            {
                position = 0;
                return true;
            }

            int strikeIndex = GetRegularStrikeIndexForActionReference(normalizedActionId, normalizedSlotId);
            return TryGetAutoAttackVisualSequencePosition(strikeIndex, out position);
        }

        private int ResolveAutoAttackVisualSequenceStrikeIndex(string actionId)
        {
            string normalizedActionId = actionId?.Trim() ?? string.Empty;
            string normalizedSlotId = CombatActionIds.NormalizeRuntimeActionReference(normalizedActionId);
            return GetRegularStrikeIndexForActionReference(normalizedActionId, normalizedSlotId);
        }

        private bool ContainsAuthoredStrikeId(string authoredStrikeId)
        {
            if (string.IsNullOrWhiteSpace(authoredStrikeId))
                return false;

            for (int strikeIndex = 1; strikeIndex <= MeleeAttackCount; strikeIndex++)
            {
                if (string.Equals(
                        GetStrikeCombat(strikeIndex).AuthoredStrikeIdOrDefault,
                        authoredStrikeId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private bool MatchesAutoAttackActionId(string normalizedActionId, string normalizedSlotId)
        {
            if (string.IsNullOrWhiteSpace(AutoAttackAuthoredStrikeIdOrDefault))
                return false;

            return string.Equals(
                       AutoAttackAuthoredStrikeIdOrDefault,
                       normalizedActionId,
                       StringComparison.OrdinalIgnoreCase)
                   || string.Equals(
                       CombatActionIds.NormalizeRuntimeActionReference(AutoAttackAuthoredStrikeIdOrDefault),
                       normalizedSlotId,
                       StringComparison.Ordinal);
        }

        private static WeaponMeleeAttackAuthoring CreateDefaultMeleeAttack(int strikeIndex)
        {
            string authoredId = $"MELEE_ATTACK_{strikeIndex}";

            return new WeaponMeleeAttackAuthoring
            {
                clip = null,
                combat = WeaponStrikeCombatAuthoring.CreateDefault(authoredId),
                presentationMode = WeaponMeleePresentationMode.SingleClip,
                startupTrimSeconds = 0f,
                lowerBodyUnlockAtSeconds = 0f,
                lowerBodyBlendOutSeconds = 0f,
                visualInterruptibleAtSeconds = 0f,
                phasedGround = default,
                phasedAir = default,
                animationVfxBindings = new List<CombatAnimationVfxBinding>(),
            };
        }

        private string GetDefaultAutoAttackVisualSourceActionId()
        {
            if (MeleeAttackCount <= 0)
                return AutoAttackAuthoredStrikeIdOrDefault;

            try
            {
                return GetStrikeCombat(1).AuthoredStrikeIdOrDefault;
            }
            catch (ArgumentOutOfRangeException)
            {
                return AutoAttackAuthoredStrikeIdOrDefault;
            }
        }

    }

    [CreateAssetMenu(menuName = "Arena/Shared Action Profile")]
    public class SharedActionProfile : ScriptableObject
    {
        [Header("Shared Dodge Actions")]
        public DirectionalClipSet dodge;
        public DirectionalClipSet airDodge;

    }
}
