#nullable enable

using System;

namespace Arena.Combat
{
    public enum ParryBehavior
    {
        Parryable,
        Unparryable,
    }

    public enum BlockBehavior
    {
        Blockable,
        Unblockable,
    }

    public enum GlobalCooldownBehavior
    {
        UsesGlobalCooldown,
        OffGlobalCooldown,
    }

    public enum AerialExecutionMode
    {
        GroundedOnly,
        GroundedOrAirborne,
        AirborneOnly,
    }

    public enum AirborneTargetingMode
    {
        AnyTarget,
        GroundedTargetOnly,
    }

    [Serializable]
    public sealed class MeleeManifestHitWindow
    {
        public int impact_delay_ms;
        public string impact_phase = "";
        public int phase_delay_ms;
    }

    [Serializable]
    public sealed class MeleeManifestPhasedGapCloseTiming
    {
        public int start_duration_ms;
        public int loop_duration_ms;
    }

    [Serializable]
    public sealed class MeleeManifestProjectile
    {
        public string projectile_id = "ARROW_STANDARD";
        public float speed;
        public float max_distance;
        public float radius;
        public float spawn_forward;
        public float spawn_height;
        public float aim_height_scale;
        public bool requires_initial_line_of_sight = true;
        public float update_interval_seconds;
    }

    [Serializable]
    public sealed class MeleeManifestStrike
    {
        public string id = "";
        public string slot_id = "";
        public int startup_trim_ms;
        public MeleeManifestHitWindow[] hit_windows = Array.Empty<MeleeManifestHitWindow>();
        public int recovery_ms;
        public bool is_gap_closer;
        public string combo_from = "";
        public int combo_open_ms;
        public int combo_grace_ms;
        public string aerial_execution_mode = "";
        public MeleeManifestProjectile? projectile;
        public MeleeManifestPhasedGapCloseTiming? phased_gap_close_timing;
    }

    [Serializable]
    public sealed class MeleeManifestProfile
    {
        public string combat_profile = "";
        public int stagger_duration_f_ms;
        public int stagger_duration_b_ms;
        public int stagger_duration_l_ms;
        public int stagger_duration_r_ms;
        public string auto_attack_strike_id = "";
        public string[] auto_attack_sequence = Array.Empty<string>();
        public int auto_attack_sequence_interval_ms;
        public MeleeManifestStrike[] strikes = Array.Empty<MeleeManifestStrike>();
    }

    [Serializable]
    public sealed class MeleeManifestDocument
    {
        public MeleeManifestProfile[] profiles = Array.Empty<MeleeManifestProfile>();
    }
}
