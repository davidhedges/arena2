#nullable enable

using System;
using UnityEngine;

namespace Arena.Graphics
{
    internal enum ArenaTextureQuality
    {
        Laptop = 0,
        Full = 1,
    }

    internal enum ArenaEffectsQuality
    {
        Low = 0,
        High = 1,
    }

    internal enum ArenaLightShadowQuality
    {
        Off = 0,
        Hero = 1,
    }

    /// <summary>
    /// Global client graphics preferences. Defaults preserve the current
    /// laptop-safe presentation and are intentionally independent of game mode.
    /// </summary>
    internal static class ArenaGraphicsSettings
    {
        private const string FrameLimitPrefKey = "arena.settings.graphics.frameLimit";
        private const string TextureQualityPrefKey = "arena.settings.graphics.textureQuality";
        private const string EffectsQualityPrefKey = "arena.settings.graphics.effectsQuality";
        private const string LightShadowQualityPrefKey = "arena.settings.graphics.lightShadows";

        internal const int DefaultFrameLimit = 30;
        internal const int LaptopTextureMipmapLimit = 1;
        internal const float LowEffectsAnimationUpdatesPerSecond = 15f;
        internal const float HighEffectsAnimationUpdatesPerSecond = 60f;

        private static bool s_loaded;
        private static int s_frameLimit = DefaultFrameLimit;
        private static ArenaTextureQuality s_textureQuality = ArenaTextureQuality.Laptop;
        private static ArenaEffectsQuality s_effectsQuality = ArenaEffectsQuality.Low;
        private static ArenaLightShadowQuality s_lightShadowQuality = ArenaLightShadowQuality.Off;

        internal static event Action? Changed;

        internal static int FrameLimit
        {
            get
            {
                EnsureLoaded();
                return s_frameLimit;
            }
        }

        internal static ArenaTextureQuality TextureQuality
        {
            get
            {
                EnsureLoaded();
                return s_textureQuality;
            }
        }

        internal static ArenaEffectsQuality EffectsQuality
        {
            get
            {
                EnsureLoaded();
                return s_effectsQuality;
            }
        }

        internal static ArenaLightShadowQuality LightShadowQuality
        {
            get
            {
                EnsureLoaded();
                return s_lightShadowQuality;
            }
        }

        internal static float EffectsAnimationUpdatesPerSecond
            => EffectsQuality == ArenaEffectsQuality.Low
                ? LowEffectsAnimationUpdatesPerSecond
                : HighEffectsAnimationUpdatesPerSecond;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            s_loaded = false;
            s_frameLimit = DefaultFrameLimit;
            s_textureQuality = ArenaTextureQuality.Laptop;
            s_effectsQuality = ArenaEffectsQuality.Low;
            s_lightShadowQuality = ArenaLightShadowQuality.Off;
            Changed = null;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void ApplySavedSettingsBeforeSceneLoad()
        {
            EnsureLoaded();
        }

        internal static void EnsureLoaded()
        {
            if (s_loaded)
                return;

            s_loaded = true;
            s_frameLimit = NormalizeFrameLimit(
                PlayerPrefs.GetInt(FrameLimitPrefKey, DefaultFrameLimit));
            s_textureQuality = PlayerPrefs.GetInt(
                    TextureQualityPrefKey,
                    (int)ArenaTextureQuality.Laptop) == (int)ArenaTextureQuality.Full
                ? ArenaTextureQuality.Full
                : ArenaTextureQuality.Laptop;
            s_effectsQuality = PlayerPrefs.GetInt(
                    EffectsQualityPrefKey,
                    (int)ArenaEffectsQuality.Low) == (int)ArenaEffectsQuality.High
                ? ArenaEffectsQuality.High
                : ArenaEffectsQuality.Low;
            s_lightShadowQuality = PlayerPrefs.GetInt(
                    LightShadowQualityPrefKey,
                    (int)ArenaLightShadowQuality.Off) == (int)ArenaLightShadowQuality.Hero
                ? ArenaLightShadowQuality.Hero
                : ArenaLightShadowQuality.Off;

            ApplyRuntimeValues();
        }

        internal static void SetFrameLimit(int frameLimit)
        {
            EnsureLoaded();
            int normalized = NormalizeFrameLimit(frameLimit);
            if (s_frameLimit == normalized)
                return;

            s_frameLimit = normalized;
            Application.targetFrameRate = s_frameLimit;
            Persist(FrameLimitPrefKey, s_frameLimit);
            Changed?.Invoke();
        }

        internal static void SetTextureQuality(ArenaTextureQuality quality)
        {
            EnsureLoaded();
            if (s_textureQuality == quality)
                return;

            s_textureQuality = quality;
            ApplyTextureQuality();
            Persist(TextureQualityPrefKey, (int)s_textureQuality);
            Changed?.Invoke();
        }

        internal static void SetEffectsQuality(ArenaEffectsQuality quality)
        {
            EnsureLoaded();
            if (s_effectsQuality == quality)
                return;

            s_effectsQuality = quality;
            Persist(EffectsQualityPrefKey, (int)s_effectsQuality);
            Changed?.Invoke();
        }

        internal static void SetLightShadowQuality(ArenaLightShadowQuality quality)
        {
            EnsureLoaded();
            if (s_lightShadowQuality == quality)
                return;

            s_lightShadowQuality = quality;
            Persist(LightShadowQualityPrefKey, (int)s_lightShadowQuality);
            Changed?.Invoke();
        }

        private static void ApplyRuntimeValues()
        {
            Application.targetFrameRate = s_frameLimit;
            ApplyTextureQuality();
        }

        private static void ApplyTextureQuality()
        {
            QualitySettings.globalTextureMipmapLimit =
                s_textureQuality == ArenaTextureQuality.Laptop
                    ? LaptopTextureMipmapLimit
                    : 0;
        }

        private static int NormalizeFrameLimit(int value)
            => value is 30 or 60 or 120 or -1 ? value : DefaultFrameLimit;

        private static void Persist(string key, int value)
        {
            PlayerPrefs.SetInt(key, value);
            PlayerPrefs.Save();
        }
    }
}
