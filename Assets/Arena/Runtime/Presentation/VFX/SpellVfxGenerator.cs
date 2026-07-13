#nullable enable
using System.Collections.Generic;

namespace Arena.Presentation
{
    // Pure core of the spell VFX cue generator — the C# counterpart of server/src/vfx_generation.rs
    // (design doc: docs/spell-presentation-dry-redesign-2026-07-07.md, Appendix B). Decision 10
    // makes C# the authoritative derivation for the Unity editor generator tool; the Rust module
    // retires to the server-side validator's Class-A rule source. The two must agree, so this is a
    // faithful mirror: same archetypes, same Appendix-B wiring, same Class-A field-relation checker.
    //
    // This file is the wiring core only. Integration — reading the catalog into SpellDeliveryFacts,
    // applying per-school VFX sets + per-slot legacy overrides, and materialising cues into
    // progression_catalog.shared.json — layers on top of it in the editor tool and is not here.
    //
    // Animation mode is NOT re-derived here: it is the existing SpellAnimationArchetype
    // (Instant/Charged/Channel) from SpellAnimationArchetypes.Derive, per decision 10 (mode
    // derivation collapses to one C# function). Wire()/RequestedSlots() take it as the mode input.

    /// <summary>
    /// The VFX "shape" of a spell's cue set — which slots fire and how they wire. Derived from
    /// <c>delivery.kind</c> + <c>targeting</c> + a couple of delivery sub-fields. Every
    /// <c>delivery.kind</c> maps to one of these (design doc B.9).
    /// </summary>
    public enum SpellVfxArchetype
    {
        Projectile = 0,
        SkyDrop = 1,
        GroundAoe = 2,
        SelfNova = 3,
        Beam = 4,
        TargetHit = 5,
        SelfFx = 6,
        Aura = 7,
        Emanation = 8,
    }

    /// <summary>
    /// The gameplay facts the VFX archetype derivation reads. String fields are expected already
    /// normalised (uppercased, <c>-</c>→<c>_</c>) — the editor tool normalises catalog values at
    /// the read boundary (via <c>WireIdentifier.Normalize</c>), exactly as the Rust core assumes
    /// server-normalised strings.
    /// </summary>
    public readonly struct SpellDeliveryFacts
    {
        public SpellDeliveryFacts(
            string kind,
            string targeting,
            bool hasSkyOrigin = false,
            bool firesProjectiles = false,
            bool deferred = false)
        {
            Kind = kind;
            Targeting = targeting;
            HasSkyOrigin = hasSkyOrigin;
            FiresProjectiles = firesProjectiles;
            Deferred = deferred;
        }

        /// <summary>Normalised <c>delivery.kind</c> (PROJECTILE / AREA / CHANNEL / INSTANT_BEAM / DIRECT_TARGET / APPLY_STATUS / ...).</summary>
        public string Kind { get; }
        /// <summary>Normalised <c>targeting</c> (SELF / TARGET / POINT).</summary>
        public string Targeting { get; }
        /// <summary><c>delivery.sky_origin</c> present/true — a falling body (METEOR).</summary>
        public bool HasSkyOrigin { get; }
        /// <summary>A channel that launches projectile bodies (MAGIC_MISSILE / FROZEN_SPLINTERS) rather than a sustained beam.</summary>
        public bool FiresProjectiles { get; }
        /// <summary>The area resolves on a delay (<c>impact_delay_ms</c> / deferred area) → AREA_IMPACT rather than at SPELL_RELEASE.</summary>
        public bool Deferred { get; }
    }

    /// <summary>The author-time slot key a generated/overridden cue carries (design doc §3.4).</summary>
    public enum SpellVfxSlot
    {
        CastGlow = 0,
        Muzzle = 1,
        ProjectileBody = 2,
        TravelBody = 3,
        Impact = 4,
        Burst = 5,
        Beam = 6,
        SelfFlash = 7,
        /// <summary>Brief one-shot burst at the caster's feet — the common aura visual.</summary>
        AuraGround = 8,
        /// <summary>Optional visual that shares the projectile body's runtime root and lifetime.</summary>
        ProjectileTrail = 9,
        /// <summary>
        /// A cast-time visual attached to the caster presentation root. Unlike the hand-specific
        /// <see cref="CastGlow"/>, this surrounds the character and may be authored more than once.
        /// </summary>
        CharacterFx = 10,
        /// <summary>Persistent ground-following field backed by an ActiveRadialEffect row.</summary>
        PersistentField = 11,
    }

    /// <summary>
    /// Anchor category. <see cref="Hand"/> is abstract (the concrete cast hand LEFT/RIGHT is
    /// inferred later, E7); the rest map straight to catalog anchor identifiers via
    /// <see cref="SpellVfxGenerator.AnchorToCatalog"/>.
    /// </summary>
    public enum CueAnchor
    {
        Hand = 0,
        Caster = 1,
        CasterOverhead = 2,
        GroundUnderCaster = 3,
        ImpactPoint = 4,
        AreaOrigin = 5,
        Target = 6,
        Origin = 7,
    }

    /// <summary>
    /// How the concrete <c>duration_ms</c> is chosen. <see cref="Zero"/> → 0;
    /// <see cref="PalettePositive"/> → the palette's positive duration (validators require
    /// <c>&gt; 0</c> for ONE_SHOT + DURATION).
    /// </summary>
    public enum CueDurationPolicy
    {
        Zero = 0,
        PalettePositive = 1,
    }

    /// <summary>
    /// The Appendix-B wiring for one cue slot: everything the generator decides that the palette
    /// (look) does not. The palette then supplies <c>vfx_id</c> and, for a <see cref="CueDurationPolicy.PalettePositive"/>
    /// slot, the concrete duration.
    /// </summary>
    public readonly struct CueWiring
    {
        public CueWiring(
            string trigger,
            CueAnchor anchor,
            string attachMode,
            string vfxRole,
            string lifecycle,
            CueDurationPolicy duration,
            int? projectileSequenceIndex)
        {
            Trigger = trigger;
            Anchor = anchor;
            AttachMode = attachMode;
            VfxRole = vfxRole;
            Lifecycle = lifecycle;
            Duration = duration;
            ProjectileSequenceIndex = projectileSequenceIndex;
        }

        public string Trigger { get; }
        public CueAnchor Anchor { get; }
        public string AttachMode { get; }
        public string VfxRole { get; }
        public string Lifecycle { get; }
        public CueDurationPolicy Duration { get; }
        public int? ProjectileSequenceIndex { get; }
    }

    /// <summary>
    /// The normalised single-cue fields the Class-A field-relation rules read. Both consumers build
    /// one: the generator from a <see cref="CueWiring"/>, the (future) editor validator from a
    /// catalog row. <see cref="Role"/> / <see cref="Lifecycle"/> must already be the *effective*
    /// values (<c>""</c> normalised to <c>ONE_SHOT</c> / <c>DURATION</c>) so both callers apply
    /// byte-identical predicates — mirrors the Rust <c>CueFields</c>.
    /// </summary>
    public readonly struct CueFields
    {
        public CueFields(
            string trigger,
            string anchor,
            string attachMode,
            string role,
            string lifecycle,
            bool durationIsZero,
            bool chargedCast)
        {
            Trigger = trigger;
            Anchor = anchor;
            AttachMode = attachMode;
            Role = role;
            Lifecycle = lifecycle;
            DurationIsZero = durationIsZero;
            ChargedCast = chargedCast;
        }

        public string Trigger { get; }
        public string Anchor { get; }
        public string AttachMode { get; }
        /// <summary>Effective <c>vfx_role</c> (never <c>""</c>).</summary>
        public string Role { get; }
        /// <summary>Effective <c>lifecycle</c> (never <c>""</c>).</summary>
        public string Lifecycle { get; }
        public bool DurationIsZero { get; }
        /// <summary>The owning spell is charged (<c>cast_time_ms &gt; 0</c>) — arms the hand-glow rule (Rule 11).</summary>
        public bool ChargedCast { get; }
    }

    /// <summary>
    /// A single-cue Class-A field-relation violation. Rendered to a message by each consumer, so
    /// the (future) editor validator can keep its own error text and the generator keeps a terse
    /// diagnostic — mirrors the Rust <c>CueFieldViolation</c>.
    /// </summary>
    public enum CueFieldViolation
    {
        /// <summary>Rule 9 — <c>UNTIL_RELEASE_EVENT</c> only on <c>SPELL_CAST</c>.</summary>
        UntilReleaseEventOffCast,
        /// <summary>Rule 10a — <c>PARTICLE_SYSTEM</c> only for <c>ONE_SHOT</c>.</summary>
        ParticleSystemBadRole,
        /// <summary>Rule 10b — <c>PARTICLE_SYSTEM</c> requires <c>duration_ms == 0</c>.</summary>
        ParticleSystemNonZeroDuration,
        /// <summary>Rule 11 — hand-attached cast-time <c>SPELL_CAST</c> cue must be <c>UNTIL_RELEASE_EVENT</c>.</summary>
        CastTimeHandGlowNotUntilRelease,
        /// <summary>Rule 12a — <c>PROJECTILE_BODY</c> only on <c>SPELL_RELEASE</c>.</summary>
        ProjectileBodyOffRelease,
        /// <summary>Rule 12b — <c>PROJECTILE_BODY</c> must not <c>FOLLOW_ANCHOR</c>.</summary>
        ProjectileBodyFollowAnchor,
        ProjectileTrailOffRelease,
        ProjectileTrailFollowAnchor,
        ProjectileTrailBadLifecycle,
        ProjectileTrailNonZeroDuration,
        /// <summary>Rule 13a — <c>TRAVEL_BODY</c> only on <c>SPELL_RELEASE</c>.</summary>
        TravelBodyOffRelease,
        /// <summary>Rule 13b — <c>TRAVEL_BODY</c> must not <c>FOLLOW_ANCHOR</c>.</summary>
        TravelBodyFollowAnchor,
        /// <summary>Rule 13c — <c>TRAVEL_BODY</c> must use <c>UNTIL_TERMINAL_EVENT</c>.</summary>
        TravelBodyBadLifecycle,
        /// <summary>Rule 13d — <c>TRAVEL_BODY</c> must set <c>duration_ms == 0</c>.</summary>
        TravelBodyNonZeroDuration,
        /// <summary>Rule 14 — <c>ONE_SHOT</c> + <c>DURATION</c> needs a positive <c>duration_ms</c>.</summary>
        OneShotDurationZero,
        /// <summary>Rule 15 — <c>TARGET</c> anchor invalid on <c>SPELL_CAST</c> / <c>SPELL_RELEASE</c>.</summary>
        TargetAnchorPreImpact,
    }

    public static class CueFieldViolations
    {
        /// <summary>
        /// A terse, context-free description — used by the generator's self-check diagnostics. The
        /// editor validator can render its own richer error text from the same variants. Mirrors
        /// the Rust <c>CueFieldViolation::describe</c>.
        /// </summary>
        public static string Describe(this CueFieldViolation violation)
            => violation switch
            {
                CueFieldViolation.UntilReleaseEventOffCast => "UNTIL_RELEASE_EVENT on non-SPELL_CAST trigger",
                CueFieldViolation.ParticleSystemBadRole => "PARTICLE_SYSTEM requires ONE_SHOT role",
                CueFieldViolation.ParticleSystemNonZeroDuration => "PARTICLE_SYSTEM requires duration 0",
                CueFieldViolation.CastTimeHandGlowNotUntilRelease => "cast-time hand SPELL_CAST cue must be UNTIL_RELEASE_EVENT",
                CueFieldViolation.ProjectileBodyOffRelease => "PROJECTILE_BODY outside SPELL_RELEASE",
                CueFieldViolation.ProjectileBodyFollowAnchor => "PROJECTILE_BODY must not FOLLOW_ANCHOR",
                CueFieldViolation.ProjectileTrailOffRelease => "PROJECTILE_TRAIL outside SPELL_RELEASE",
                CueFieldViolation.ProjectileTrailFollowAnchor => "PROJECTILE_TRAIL must not FOLLOW_ANCHOR",
                CueFieldViolation.ProjectileTrailBadLifecycle => "PROJECTILE_TRAIL must use UNTIL_TERMINAL_EVENT",
                CueFieldViolation.ProjectileTrailNonZeroDuration => "PROJECTILE_TRAIL must set duration 0",
                CueFieldViolation.TravelBodyOffRelease => "TRAVEL_BODY outside SPELL_RELEASE",
                CueFieldViolation.TravelBodyFollowAnchor => "TRAVEL_BODY must not FOLLOW_ANCHOR",
                CueFieldViolation.TravelBodyBadLifecycle => "TRAVEL_BODY must use UNTIL_TERMINAL_EVENT",
                CueFieldViolation.TravelBodyNonZeroDuration => "TRAVEL_BODY must set duration 0",
                CueFieldViolation.OneShotDurationZero => "ONE_SHOT DURATION must define positive duration_ms",
                CueFieldViolation.TargetAnchorPreImpact => "TARGET anchor invalid on cast/release trigger",
                _ => violation.ToString(),
            };
    }

    /// <summary>
    /// The pure spell-VFX cue generator core: archetype derivation, requested slots, the Appendix-B
    /// per-slot wiring, and the shared Class-A field-relation checker. Correct-by-construction:
    /// every wiring <see cref="Wire"/> emits passes <see cref="ValidateWiring"/> (see the EditMode
    /// test <c>SpellVfxGeneratorTests</c>). Mirrors server/src/vfx_generation.rs.
    /// </summary>
    public static class SpellVfxGenerator
    {
        // ---- cue vocabulary (design doc §1.3). Public so the editor tool can reuse it when it
        //      materialises concrete catalog rows from a CueWiring. ----
        public const string TriggerSpellCast = "SPELL_CAST";
        public const string TriggerSpellRelease = "SPELL_RELEASE";
        public const string TriggerSpellImpact = "SPELL_IMPACT";
        public const string TriggerAreaImpact = "AREA_IMPACT";
        public const string TriggerEmanationActive = "EMANATION_ACTIVE";

        public const string AttachSpawnWorld = "SPAWN_WORLD";
        public const string AttachFollowAnchor = "FOLLOW_ANCHOR";
        public const string AttachFollowGroundPosition = "FOLLOW_GROUND_POSITION";

        public const string RoleOneShot = "ONE_SHOT";
        public const string RoleAttached = "ATTACHED";
        public const string RoleProjectileBody = "PROJECTILE_BODY";
        public const string RoleProjectileTrail = "PROJECTILE_TRAIL";
        public const string RoleTravelBody = "TRAVEL_BODY";

        public const string LifecycleDuration = "DURATION";
        public const string LifecycleParticleSystem = "PARTICLE_SYSTEM";
        public const string LifecycleUntilReleaseEvent = "UNTIL_RELEASE_EVENT";
        public const string LifecycleUntilTerminalEvent = "UNTIL_TERMINAL_EVENT";
        public const string LifecycleUntilCastEnd = "UNTIL_CAST_END";
        public const string LifecycleUntilRadialEffectEnd = "UNTIL_RADIAL_EFFECT_END";

        public const string AnchorLeftHand = "LEFT_HAND";
        public const string AnchorRightHand = "RIGHT_HAND";
        public const string AnchorCaster = "CASTER";
        public const string AnchorCasterOverhead = "CASTER_OVERHEAD";
        public const string AnchorGroundUnderCaster = "GROUND_UNDER_CASTER";
        public const string AnchorImpactPoint = "IMPACT_POINT";
        public const string AnchorAreaOrigin = "AREA_ORIGIN";
        public const string AnchorTarget = "TARGET";
        public const string AnchorOrigin = "ORIGIN";

        private const string DeliveryProjectile = "PROJECTILE";
        private const string DeliveryChannel = "CHANNEL";
        private const string DeliveryInstantBeam = "INSTANT_BEAM";
        private const string DeliveryDirectTarget = "DIRECT_TARGET";
        private const string DeliveryArea = "AREA";
        private const string DeliveryApplyStatus = "APPLY_STATUS";
        private const string DeliveryRemoveStatus = "REMOVE_STATUS";
        private const string DeliveryConsumeStatus = "CONSUME_STATUS";
        private const string DeliverySelfResource = "SELF_RESOURCE";
        private const string DeliveryAura = "AURA";
        private const string DeliveryEmanation = "EMANATION";

        private const string TargetingSelf = "SELF";
        private const string TargetingTarget = "TARGET";

        /// <summary>
        /// Returns <c>null</c> for kinds we do not generate VFX for (anything unknown). Every known
        /// <c>delivery.kind</c> maps to an archetype — all spell types are covered (design doc B.9).
        /// </summary>
        public static SpellVfxArchetype? DeriveArchetype(in SpellDeliveryFacts facts)
        {
            switch (facts.Kind)
            {
                case DeliveryProjectile:
                    return SpellVfxArchetype.Projectile;
                case DeliveryChannel:
                    return facts.FiresProjectiles ? SpellVfxArchetype.Projectile : SpellVfxArchetype.Beam;
                case DeliveryInstantBeam:
                    return SpellVfxArchetype.Beam;
                case DeliveryDirectTarget:
                    return SpellVfxArchetype.TargetHit;
                case DeliveryArea:
                    if (facts.HasSkyOrigin)
                        return SpellVfxArchetype.SkyDrop;
                    return facts.Targeting == TargetingSelf
                        ? SpellVfxArchetype.SelfNova
                        : SpellVfxArchetype.GroundAoe;
                case DeliveryApplyStatus:
                case DeliveryRemoveStatus:
                case DeliveryConsumeStatus:
                case DeliverySelfResource:
                    return facts.Targeting == TargetingTarget
                        ? SpellVfxArchetype.TargetHit
                        : SpellVfxArchetype.SelfFx;
                case DeliveryAura:
                    return SpellVfxArchetype.Aura;
                case DeliveryEmanation:
                    return SpellVfxArchetype.Emanation;
                default:
                    return null;
            }
        }

        /// <summary>
        /// The slots an archetype supports. Optional slots are still listed here; the palette/override
        /// layer omits them when no look is authored. Every spell archetype supports a hand cast glow
        /// and one or more caster-attached character effects. Projectile spells additionally support
        /// an optional release muzzle and an optional trail.
        /// </summary>
        public static IReadOnlyList<SpellVfxSlot> RequestedSlots(SpellVfxArchetype archetype, SpellAnimationArchetype mode)
        {
            switch (archetype)
            {
                case SpellVfxArchetype.Projectile:
                    return new[]
                    {
                        SpellVfxSlot.CastGlow,
                        SpellVfxSlot.CharacterFx,
                        SpellVfxSlot.Muzzle,
                        SpellVfxSlot.ProjectileBody,
                        SpellVfxSlot.ProjectileTrail,
                        SpellVfxSlot.Impact,
                    };
                case SpellVfxArchetype.SkyDrop:
                    return new[]
                    {
                        SpellVfxSlot.CastGlow,
                        SpellVfxSlot.CharacterFx,
                        SpellVfxSlot.TravelBody,
                        SpellVfxSlot.Impact,
                    };
                case SpellVfxArchetype.GroundAoe:
                    return new[] { SpellVfxSlot.CastGlow, SpellVfxSlot.CharacterFx, SpellVfxSlot.Impact };
                case SpellVfxArchetype.SelfNova:
                    return new[] { SpellVfxSlot.CastGlow, SpellVfxSlot.CharacterFx, SpellVfxSlot.Burst };
                case SpellVfxArchetype.Beam:
                    return new[] { SpellVfxSlot.CastGlow, SpellVfxSlot.CharacterFx, SpellVfxSlot.Beam };
                case SpellVfxArchetype.TargetHit:
                    return new[] { SpellVfxSlot.CastGlow, SpellVfxSlot.CharacterFx, SpellVfxSlot.Impact };
                case SpellVfxArchetype.SelfFx:
                    return new[] { SpellVfxSlot.CastGlow, SpellVfxSlot.CharacterFx, SpellVfxSlot.SelfFlash };
                // Aura buffs persist, but their aura_ground visual is only a brief ground flourish.
                case SpellVfxArchetype.Aura:
                    return new[] { SpellVfxSlot.CastGlow, SpellVfxSlot.CharacterFx, SpellVfxSlot.AuraGround };
                case SpellVfxArchetype.Emanation:
                    return new[] { SpellVfxSlot.CastGlow, SpellVfxSlot.CharacterFx, SpellVfxSlot.PersistentField };
                default:
                    return System.Array.Empty<SpellVfxSlot>();
            }
        }

        /// <summary>
        /// The Appendix-B wiring for one <c>(archetype, slot)</c> given the animation
        /// <paramref name="mode"/>, whether the palette entry is a self-terminating particle system,
        /// and whether the delivery is deferred.
        /// </summary>
        public static CueWiring Wire(
            SpellVfxArchetype archetype,
            SpellVfxSlot slot,
            SpellAnimationArchetype mode,
            bool selfTerminating,
            bool deferred)
        {
            string oneShotLifecycle = selfTerminating ? LifecycleParticleSystem : LifecycleDuration;
            CueDurationPolicy oneShotDuration = selfTerminating
                ? CueDurationPolicy.Zero
                : CueDurationPolicy.PalettePositive;

            switch (slot)
            {
                // B.1
                case SpellVfxSlot.CastGlow:
                    return new CueWiring(
                        trigger: TriggerSpellCast,
                        anchor: CueAnchor.Hand,
                        attachMode: AttachFollowAnchor,
                        vfxRole: RoleAttached,
                        lifecycle: mode switch
                        {
                            SpellAnimationArchetype.Instant => LifecycleDuration,
                            SpellAnimationArchetype.Charged => LifecycleUntilReleaseEvent,
                            SpellAnimationArchetype.Channel => LifecycleUntilCastEnd,
                            _ => LifecycleUntilCastEnd,
                        },
                        duration: mode == SpellAnimationArchetype.Instant
                            ? CueDurationPolicy.PalettePositive
                            : CueDurationPolicy.Zero,
                        projectileSequenceIndex: null);

                // B.1b — the body-root counterpart to cast_glow. It deliberately shares the cast
                // lifecycle, so instant effects play for their authored duration, charged effects end
                // on release, and channel effects end with the authoritative ActiveCast.
                case SpellVfxSlot.CharacterFx:
                    return new CueWiring(
                        trigger: TriggerSpellCast,
                        anchor: CueAnchor.Caster,
                        attachMode: AttachFollowAnchor,
                        vfxRole: RoleAttached,
                        lifecycle: mode switch
                        {
                            SpellAnimationArchetype.Instant => LifecycleDuration,
                            SpellAnimationArchetype.Charged => LifecycleUntilReleaseEvent,
                            SpellAnimationArchetype.Channel => LifecycleUntilCastEnd,
                            _ => LifecycleUntilCastEnd,
                        },
                        duration: mode == SpellAnimationArchetype.Instant
                            ? CueDurationPolicy.PalettePositive
                            : CueDurationPolicy.Zero,
                        projectileSequenceIndex: null);

                // B.2
                case SpellVfxSlot.Muzzle:
                    return new CueWiring(
                        trigger: TriggerSpellRelease,
                        anchor: CueAnchor.Hand,
                        attachMode: AttachSpawnWorld,
                        vfxRole: RoleOneShot,
                        lifecycle: oneShotLifecycle,
                        duration: oneShotDuration,
                        projectileSequenceIndex: null);

                // B.3
                case SpellVfxSlot.ProjectileBody:
                    return new CueWiring(
                        trigger: TriggerSpellRelease,
                        anchor: CueAnchor.Hand,
                        attachMode: AttachSpawnWorld,
                        vfxRole: RoleProjectileBody,
                        lifecycle: LifecycleUntilTerminalEvent,
                        duration: CueDurationPolicy.Zero,
                        projectileSequenceIndex: 0);

                case SpellVfxSlot.ProjectileTrail:
                    return new CueWiring(
                        trigger: TriggerSpellRelease,
                        anchor: CueAnchor.Hand,
                        attachMode: AttachSpawnWorld,
                        vfxRole: RoleProjectileTrail,
                        lifecycle: LifecycleUntilTerminalEvent,
                        duration: CueDurationPolicy.Zero,
                        projectileSequenceIndex: 0);

                // B.4
                case SpellVfxSlot.TravelBody:
                    return new CueWiring(
                        trigger: TriggerSpellRelease,
                        anchor: CueAnchor.Origin,
                        attachMode: AttachSpawnWorld,
                        vfxRole: RoleTravelBody,
                        lifecycle: LifecycleUntilTerminalEvent,
                        duration: CueDurationPolicy.Zero,
                        projectileSequenceIndex: null);

                // B.5
                case SpellVfxSlot.Impact:
                {
                    string impactTrigger;
                    CueAnchor impactAnchor;
                    switch (archetype)
                    {
                        case SpellVfxArchetype.TargetHit:
                            impactTrigger = TriggerSpellImpact;
                            impactAnchor = CueAnchor.Target;
                            break;
                        case SpellVfxArchetype.GroundAoe when deferred:
                            impactTrigger = TriggerAreaImpact;
                            impactAnchor = CueAnchor.AreaOrigin;
                            break;
                        case SpellVfxArchetype.GroundAoe:
                            impactTrigger = TriggerSpellRelease;
                            impactAnchor = CueAnchor.ImpactPoint;
                            break;
                        default:
                            // Projectile / SkyDrop resolve their impact on the projectile/body landing.
                            impactTrigger = TriggerSpellImpact;
                            impactAnchor = CueAnchor.ImpactPoint;
                            break;
                    }
                    return new CueWiring(
                        trigger: impactTrigger,
                        anchor: impactAnchor,
                        attachMode: AttachSpawnWorld,
                        vfxRole: RoleOneShot,
                        lifecycle: oneShotLifecycle,
                        duration: oneShotDuration,
                        projectileSequenceIndex: null);
                }

                // B.6
                case SpellVfxSlot.Burst:
                    return new CueWiring(
                        trigger: deferred ? TriggerAreaImpact : TriggerSpellRelease,
                        anchor: deferred ? CueAnchor.AreaOrigin : CueAnchor.Caster,
                        attachMode: AttachSpawnWorld,
                        vfxRole: RoleOneShot,
                        lifecycle: oneShotLifecycle,
                        duration: oneShotDuration,
                        projectileSequenceIndex: null);

                // B.7
                case SpellVfxSlot.Beam:
                    return new CueWiring(
                        trigger: TriggerSpellRelease,
                        anchor: CueAnchor.Hand,
                        attachMode: AttachFollowAnchor,
                        vfxRole: RoleAttached,
                        lifecycle: mode == SpellAnimationArchetype.Channel
                            ? LifecycleUntilCastEnd
                            : LifecycleDuration,
                        duration: mode == SpellAnimationArchetype.Channel
                            ? CueDurationPolicy.Zero
                            : CueDurationPolicy.PalettePositive,
                        projectileSequenceIndex: null);

                // B.8
                case SpellVfxSlot.SelfFlash:
                    return new CueWiring(
                        trigger: TriggerSpellRelease,
                        anchor: CueAnchor.Caster,
                        attachMode: AttachSpawnWorld,
                        vfxRole: RoleOneShot,
                        lifecycle: oneShotLifecycle,
                        duration: oneShotDuration,
                        projectileSequenceIndex: null);

                // B.8 — the common aura visual: a one-shot flourish at the caster's feet that
                // follows them while it plays (an aura is attached to the caster, not planted in the
                // world). FOLLOW_ANCHOR requires a real transform, so anchor on CASTER (the
                // presentation root), not GROUND_UNDER_CASTER (a computed point). Ground-pivoted prefab.
                case SpellVfxSlot.AuraGround:
                    return new CueWiring(
                        trigger: TriggerSpellRelease,
                        anchor: CueAnchor.Caster,
                        attachMode: AttachFollowAnchor,
                        vfxRole: RoleOneShot,
                        lifecycle: oneShotLifecycle,
                        duration: oneShotDuration,
                        projectileSequenceIndex: null);

                case SpellVfxSlot.PersistentField:
                    return new CueWiring(
                        trigger: TriggerEmanationActive,
                        anchor: CueAnchor.Caster,
                        attachMode: AttachFollowGroundPosition,
                        vfxRole: RoleAttached,
                        lifecycle: LifecycleUntilRadialEffectEnd,
                        duration: CueDurationPolicy.Zero,
                        projectileSequenceIndex: null);

                default:
                    // Unreachable for the enum's defined members; keeps the switch total.
                    return new CueWiring(
                        trigger: TriggerSpellRelease,
                        anchor: CueAnchor.Caster,
                        attachMode: AttachSpawnWorld,
                        vfxRole: RoleOneShot,
                        lifecycle: oneShotLifecycle,
                        duration: oneShotDuration,
                        projectileSequenceIndex: null);
            }
        }

        /// <summary>
        /// The catalog anchor identifier a <see cref="CueAnchor"/> resolves to for validation.
        /// <see cref="CueAnchor.Hand"/> is abstract (the concrete cast hand is inferred later, E7);
        /// it maps to a hand anchor so the hand-relative field rules (Rule 11 / 15) evaluate
        /// identically to an already-resolved LEFT/RIGHT_HAND cue. Mirrors the Rust <c>as_catalog_str</c>.
        /// </summary>
        public static string AnchorToCatalog(CueAnchor anchor)
            => anchor switch
            {
                CueAnchor.Hand => AnchorLeftHand,
                CueAnchor.Caster => AnchorCaster,
                CueAnchor.CasterOverhead => AnchorCasterOverhead,
                CueAnchor.GroundUnderCaster => AnchorGroundUnderCaster,
                CueAnchor.ImpactPoint => AnchorImpactPoint,
                CueAnchor.AreaOrigin => AnchorAreaOrigin,
                CueAnchor.Target => AnchorTarget,
                CueAnchor.Origin => AnchorOrigin,
                _ => AnchorCaster,
            };

        /// <summary>
        /// The single source of truth for the Class-A single-cue field-relation rules (Appendix A).
        /// Both the generator's <see cref="ValidateWiring"/> and the (future) editor validator call
        /// this, so the rules can never silently diverge — mirrors the Rust
        /// <c>check_cue_field_rules</c>, the same shared checker the server contract runs. Rules
        /// that need catalog context (enum allow-lists, owner resolution, projectile ownership/count,
        /// <c>start_delay_ms</c>, <c>hit_index</c>) stay with the server validator — not shareable here.
        /// </summary>
        public static IReadOnlyList<CueFieldViolation> CheckCueFieldRules(in CueFields f)
        {
            var violations = new List<CueFieldViolation>();

            // Rule 9 — UNTIL_RELEASE_EVENT only on SPELL_CAST.
            if (f.Lifecycle == LifecycleUntilReleaseEvent && f.Trigger != TriggerSpellCast)
                violations.Add(CueFieldViolation.UntilReleaseEventOffCast);

            // Rule 10 — PARTICLE_SYSTEM only for ONE_SHOT prefab cues, duration 0.
            if (f.Lifecycle == LifecycleParticleSystem)
            {
                if (f.Role != RoleOneShot)
                    violations.Add(CueFieldViolation.ParticleSystemBadRole);
                if (!f.DurationIsZero)
                    violations.Add(CueFieldViolation.ParticleSystemNonZeroDuration);
            }

            // Rule 11 — hand-attached cast-time SPELL_CAST cue must use UNTIL_RELEASE_EVENT.
            if (f.ChargedCast
                && f.Trigger == TriggerSpellCast
                && f.AttachMode == AttachFollowAnchor
                && f.Role == RoleAttached
                && IsHandAnchor(f.Anchor)
                && f.Lifecycle != LifecycleUntilReleaseEvent)
            {
                violations.Add(CueFieldViolation.CastTimeHandGlowNotUntilRelease);
            }

            // Rule 12 — PROJECTILE_BODY field legality (owner/index/start_delay are context, server-side).
            if (f.Role == RoleProjectileBody)
            {
                if (f.Trigger != TriggerSpellRelease)
                    violations.Add(CueFieldViolation.ProjectileBodyOffRelease);
                if (f.AttachMode == AttachFollowAnchor)
                    violations.Add(CueFieldViolation.ProjectileBodyFollowAnchor);
            }

            if (f.Role == RoleProjectileTrail)
            {
                if (f.Trigger != TriggerSpellRelease)
                    violations.Add(CueFieldViolation.ProjectileTrailOffRelease);
                if (f.AttachMode == AttachFollowAnchor)
                    violations.Add(CueFieldViolation.ProjectileTrailFollowAnchor);
                if (f.Lifecycle != LifecycleUntilTerminalEvent)
                    violations.Add(CueFieldViolation.ProjectileTrailBadLifecycle);
                if (!f.DurationIsZero)
                    violations.Add(CueFieldViolation.ProjectileTrailNonZeroDuration);
            }

            // Rule 13 — TRAVEL_BODY.
            if (f.Role == RoleTravelBody)
            {
                if (f.Trigger != TriggerSpellRelease)
                    violations.Add(CueFieldViolation.TravelBodyOffRelease);
                if (f.AttachMode == AttachFollowAnchor)
                    violations.Add(CueFieldViolation.TravelBodyFollowAnchor);
                if (f.Lifecycle != LifecycleUntilTerminalEvent)
                    violations.Add(CueFieldViolation.TravelBodyBadLifecycle);
                if (!f.DurationIsZero)
                    violations.Add(CueFieldViolation.TravelBodyNonZeroDuration);
            }

            // Rule 14 — ONE_SHOT + DURATION needs a positive duration.
            if (f.Role == RoleOneShot && f.Lifecycle == LifecycleDuration && f.DurationIsZero)
                violations.Add(CueFieldViolation.OneShotDurationZero);

            // Rule 15 — TARGET anchor only valid post-impact.
            if (f.Anchor == AnchorTarget
                && (f.Trigger == TriggerSpellCast || f.Trigger == TriggerSpellRelease))
            {
                violations.Add(CueFieldViolation.TargetAnchorPreImpact);
            }

            return violations;
        }

        /// <summary>
        /// Proves a wiring <see cref="Wire"/> emits is correct-by-construction against the Class-A
        /// rules — delegates to the shared <see cref="CheckCueFieldRules"/> (the same rule set the
        /// server validator runs). Returns <c>null</c> when legal, else the first violation's
        /// description. Mirrors the Rust <c>validate_wiring</c>.
        /// </summary>
        public static string? ValidateWiring(in CueWiring w, SpellAnimationArchetype mode)
        {
            // A generated CueWiring always carries explicit, non-empty role/lifecycle, so its
            // effective values equal the raw ones. Rule 11 arms on the charged mode (== cast_time > 0).
            var violations = CheckCueFieldRules(new CueFields(
                trigger: w.Trigger,
                anchor: AnchorToCatalog(w.Anchor),
                attachMode: w.AttachMode,
                role: w.VfxRole,
                lifecycle: w.Lifecycle,
                durationIsZero: w.Duration == CueDurationPolicy.Zero,
                chargedCast: mode == SpellAnimationArchetype.Charged));
            if (violations.Count > 0)
                return violations[0].Describe();

            // Generator-local completeness invariant: a PROJECTILE_BODY wiring must carry a sequence
            // index. (The server instead derives/defaults it from the catalog row — not a shared rule.)
            if (w.VfxRole == RoleProjectileBody && w.ProjectileSequenceIndex == null)
                return "PROJECTILE_BODY needs a projectile_sequence_index";
            if (w.VfxRole == RoleProjectileTrail && w.ProjectileSequenceIndex == null)
                return "PROJECTILE_TRAIL needs a projectile_sequence_index";

            return null;
        }

        private static bool IsHandAnchor(string anchor)
            => anchor == AnchorLeftHand || anchor == AnchorRightHand;
    }
}
