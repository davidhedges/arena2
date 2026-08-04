#nullable enable

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Arena.World
{
    /// <summary>
    /// Applies Survival's laptop-safe entrance-light budget without removing
    /// any fixtures, light color/range, or fire particle effects.
    /// </summary>
    internal sealed class SurvivalLightingBudget : MonoBehaviour
    {
        private const string SceneName = "SurvivalArena";
        private const string EntranceRootPrefix = "Level Entrance ";
        private const string AnimatedFireLightName = "pointlight_fire_dungeon";

        // TODO(graphics-quality-menu): fold this into an Effects Animation
        // quality preset. 15 Hz retains the original subtle fire variation
        // without evaluating 56 Animator graphs every rendered frame.
        private const float FlickerUpdatesPerSecond = 15f;
        private const float FlickerUpdateInterval = 1f / FlickerUpdatesPerSecond;

        // The source pack's 1.5167-second ANI_pointlight_fire curve, sampled by
        // one controller and then shared by every matching entrance light.
        private static readonly AnimationCurve FireFlicker = new(
            new Keyframe(0f, 12.627122f, 0f, 0f),
            new Keyframe(0.06666667f, 12.880248f, 0f, 0f),
            new Keyframe(0.16666667f, 12.576495f, 0f, 0f),
            new Keyframe(0.28333333f, 12.930874f, 0f, 0f),
            new Keyframe(0.36666667f, 12.677747f, 0f, 0f),
            new Keyframe(0.6333333f, 13.251021f, 0f, 0f),
            new Keyframe(0.7f, 12.627122f, 0f, 0f),
            new Keyframe(0.8333333f, 12.880248f, 0f, 0f),
            new Keyframe(0.93333334f, 12.677747f, 0f, 0f),
            new Keyframe(1.1f, 12.930874f, 0f, 0f),
            new Keyframe(1.1833333f, 12.677747f, 0f, 0f),
            new Keyframe(1.3166667f, 12.880248f, 0f, 0f),
            new Keyframe(1.5166667f, 12.627122f, 0f, 0f))
        {
            preWrapMode = WrapMode.Loop,
            postWrapMode = WrapMode.Loop,
        };

        private readonly List<Light> _animatedFireLights = new(64);
        private float _nextFlickerUpdateAt;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (!string.Equals(SceneManager.GetActiveScene().name, SceneName, StringComparison.Ordinal))
                return;
            if (FindAnyObjectByType<SurvivalLightingBudget>() != null)
                return;

            new GameObject(nameof(SurvivalLightingBudget)).AddComponent<SurvivalLightingBudget>();
        }

        private void Awake()
        {
            Light[] lights = FindObjectsByType<Light>(FindObjectsInactive.Include);
            foreach (Light light in lights)
            {
                if (light.gameObject.scene != gameObject.scene ||
                    light.type != LightType.Point ||
                    !IsInsideEntrance(light.transform))
                {
                    continue;
                }

                // TODO(graphics-quality-menu): "Additional Light Shadows" should
                // default Off/Balanced. A future High setting should enable only
                // a curated handful of hero lights, not all sixty point lights.
                light.shadows = LightShadows.None;

                if (!string.Equals(light.gameObject.name, AnimatedFireLightName, StringComparison.Ordinal))
                    continue;

                Animator? animator = light.GetComponent<Animator>();
                if (animator != null)
                    animator.enabled = false;
                _animatedFireLights.Add(light);
            }

            ApplyFlicker(Time.unscaledTime);
        }

        private void Update()
        {
            float now = Time.unscaledTime;
            if (now < _nextFlickerUpdateAt)
                return;

            ApplyFlicker(now);
        }

        private void ApplyFlicker(float now)
        {
            _nextFlickerUpdateAt = now + FlickerUpdateInterval;
            float intensity = FireFlicker.Evaluate(now);
            foreach (Light light in _animatedFireLights)
                if (light != null)
                    light.intensity = intensity;
        }

        private static bool IsInsideEntrance(Transform transform)
        {
            for (Transform? current = transform; current != null; current = current.parent)
                if (current.name.StartsWith(EntranceRootPrefix, StringComparison.Ordinal))
                    return true;

            return false;
        }
    }
}
