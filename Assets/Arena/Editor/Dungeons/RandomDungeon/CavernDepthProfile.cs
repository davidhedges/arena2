#nullable enable

using System;
using System.Globalization;
using UnityEditor;
using UnityEngine;

namespace DungeonLab.Editor
{
    /// <summary>
    /// The one descent curve. Silhouette tinting, the underglow light, scene
    /// ambient, fog and the camera clear colour all read this struct, so
    /// retuning the whole descent means editing the endpoints and the exponent
    /// here rather than five builders.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT stored on <c>DungeonGenerationProfile</c>. That asset
    /// feeds <c>DungeonGenerationSettings</c> and its digest, which is logged
    /// into GENERATION_SUMMARY and carried in the seed reports. Adding envelope
    /// fields there would rebaseline the seed corpus for a change that cannot
    /// affect a single plan decision. The envelope is downstream of the plan,
    /// and its inputs stay downstream too.
    /// </remarks>
    internal readonly struct CavernDepthProfile
    {
        internal const string FloorEnvironmentVariable = "ARENA_DUNGEON_FLOOR";
        internal const string FloorCountEnvironmentVariable = "ARENA_DUNGEON_FLOOR_COUNT";

        internal const string FloorEditorPreferenceKey = "Arena.DungeonLab.DescentFloor";
        internal const string FloorCountEditorPreferenceKey = "Arena.DungeonLab.DescentFloorCount";

        // Flat early, hard late. A linear ramp is imperceptible: the player
        // judges each floor against the one before it, so most of the change
        // has to land in the last two floors to read as an approach.
        private const float GlowExponent = 2.5f;

        // Shallow endpoint (floor 0) -> deep endpoint (bottom floor).
        // Ambient shifts cold-to-warm WITHOUT gaining luminance. Raising it
        // with the glow flattens the bottom floor; the scariest version of the
        // lava is high contrast, not high exposure.
        //
        // MEASURED CONSTRAINT, not the target value: the generator places no
        // lights at all — the dungeon kit's torch and candle props are geometry
        // only. Until interior light sources exist, scene ambient is the sole
        // illumination on the upper floors, so the shallow endpoint is held
        // well above where a cavern actually wants it. Bring it down toward
        // ~0.06 luminance in the same change that lands torch lights; the deep
        // endpoint is already free of this, because the underglow lights it.
        private static readonly Color ShallowAmbient = new(0.150f, 0.183f, 0.232f);
        private static readonly Color DeepAmbient = new(0.155f, 0.088f, 0.062f);

        // LINEAR values, and the project renders in linear colour space. These
        // were originally authored as if they were display values, which put the
        // void at roughly 76/255 on screen — a mid-tone brown. Everything in the
        // backdrop hazes toward this colour, so nothing in the scene could be
        // darker than it and the cavern read as a hazy sunset with no shadow
        // anywhere. A void that displays near 28/255 restores the range the
        // underglow needs to be bright AGAINST something.
        private static readonly Color ShallowFog = new(0.0055f, 0.0072f, 0.0105f);
        private static readonly Color DeepFog = new(0.0110f, 0.0048f, 0.0034f);

        private static readonly Color ShallowGlow = new(0.30f, 0.035f, 0.008f);
        private static readonly Color DeepGlow = new(1.00f, 0.45f, 0.16f);

        // A distant shaft's worth of cool downlight up top, effectively gone by
        // the bottom, where the lava is meant to be the only source.
        private static readonly Color ShallowSun = new(0.62f, 0.74f, 0.88f);
        private static readonly Color DeepSun = new(0.55f, 0.44f, 0.40f);

        private const float ShallowSunIntensity = 0.12f;
        private const float DeepSunIntensity = 0.02f;

        // Apparent proximity, not brightness, is what reads as approaching a
        // fixed thing. Intensity follows from the distance closing.
        private const float ShallowGlowDistance = 520f;
        private const float DeepGlowDistance = 26f;

        private const float ShallowGlowIntensity = 0.04f;
        private const float DeepGlowIntensity = 2.0f;

        // Haze thickens with depth: the bottom is the biggest space in the run
        // and should still feel close and choking.
        private const float ShallowFogStart = 14f;
        private const float DeepFogStart = 7f;
        private const float ShallowFogEnd = 46f;
        private const float DeepFogEnd = 27f;

        /// <summary>0 at the top floor, 1 at the lava floor.</summary>
        internal float Depth01 { get; }

        /// <summary>The curved descent value everything hot is driven by.</summary>
        internal float Glow { get; }

        /// <summary>Metres below the lowest floor at which the lava reads.</summary>
        internal float GlowDistance { get; }

        internal Color GlowColor { get; }
        internal float GlowIntensity { get; }

        internal Color Ambient { get; }
        internal Color FogColor { get; }
        internal float FogStart { get; }
        internal float FogEnd { get; }

        internal Color SunColor { get; }
        internal float SunIntensity { get; }

        /// <summary>
        /// Camera clear colour. Equal to <see cref="FogColor"/> by contract:
        /// distant geometry must fade into the void rather than into a
        /// brighter value, which is the signature of outdoor haze.
        /// </summary>
        internal Color Background => FogColor;

        private CavernDepthProfile(float depth01)
        {
            Depth01 = Mathf.Clamp01(depth01);
            Glow = Mathf.Pow(Depth01, GlowExponent);

            GlowDistance = Mathf.Lerp(ShallowGlowDistance, DeepGlowDistance, Glow);
            GlowColor = Color.Lerp(ShallowGlow, DeepGlow, Glow);
            GlowIntensity = Mathf.Lerp(ShallowGlowIntensity, DeepGlowIntensity, Glow);

            Ambient = Color.Lerp(ShallowAmbient, DeepAmbient, Glow);
            FogColor = Color.Lerp(ShallowFog, DeepFog, Glow);
            FogStart = Mathf.Lerp(ShallowFogStart, DeepFogStart, Glow);
            FogEnd = Mathf.Lerp(ShallowFogEnd, DeepFogEnd, Glow);

            SunColor = Color.Lerp(ShallowSun, DeepSun, Glow);
            SunIntensity = Mathf.Lerp(ShallowSunIntensity, DeepSunIntensity, Glow);
        }

        internal static CavernDepthProfile Evaluate(float depth01) => new(depth01);

        /// <summary>
        /// Resolves which floor of a descent this build represents.
        /// </summary>
        /// <remarks>
        /// There is no descent system yet — one generation still makes one
        /// scene. This is wired as a first-class input now so the envelope is
        /// already correct on the day a run of floors exists, and so any floor
        /// of a hypothetical run can be rendered and reviewed today.
        ///
        /// Environment beats the per-user editor choice, which beats floor 0 of
        /// a one-floor run — the same precedence as
        /// <c>DungeonLabGenerator.ResolveRequestedDensityLevel</c>. The editor
        /// preference exists because a running editor never sees a variable
        /// exported after it launched, which would make the deep floors
        /// reviewable only by restarting Unity.
        /// </remarks>
        internal static CavernDepthProfile ResolveRequested()
        {
            int floorCount = ReadEnvironmentInteger(
                FloorCountEnvironmentVariable,
                EditorPrefs.GetInt(FloorCountEditorPreferenceKey, 1),
                minimum: 1);
            int floorIndex = ReadEnvironmentInteger(
                FloorEnvironmentVariable,
                EditorPrefs.GetInt(FloorEditorPreferenceKey, 0),
                minimum: 0);
            if (floorIndex >= floorCount)
            {
                throw new InvalidOperationException(
                    $"[CAVERN_ENVELOPE] {FloorEnvironmentVariable}={floorIndex} is not a floor of a " +
                    $"{floorCount}-floor descent. Expected 0..{floorCount - 1}.");
            }

            float depth01 = floorCount <= 1 ? 0f : floorIndex / (float)(floorCount - 1);
            return Evaluate(depth01);
        }

        private static int ReadEnvironmentInteger(string variable, int fallback, int minimum)
        {
            string? configured = Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrWhiteSpace(configured))
                return fallback;

            if (!int.TryParse(configured.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ||
                parsed < minimum)
            {
                throw new InvalidOperationException(
                    $"[CAVERN_ENVELOPE] {variable}='{configured}' is not an integer >= {minimum}.");
            }

            return parsed;
        }

        internal string Summary =>
            $"depth01={Depth01.ToString("0.###", CultureInfo.InvariantCulture)}; " +
            $"glow={Glow.ToString("0.###", CultureInfo.InvariantCulture)}; " +
            $"glowDistance={GlowDistance.ToString("0.#", CultureInfo.InvariantCulture)}m; " +
            $"glowIntensity={GlowIntensity.ToString("0.###", CultureInfo.InvariantCulture)}; " +
            $"fog={FogStart.ToString("0.#", CultureInfo.InvariantCulture)}..{FogEnd.ToString("0.#", CultureInfo.InvariantCulture)}m";
    }
}
