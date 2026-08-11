#nullable enable

using System;
using System.Collections.Generic;
using Arena.Graphics;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Arena.World
{
    /// <summary>
    /// Applies Arena_Map_01's laptop-safe entrance-light budget without removing
    /// any fixtures, light color/range, or fire particle effects.
    /// </summary>
    internal sealed class ArenaMap01LightingBudget : MonoBehaviour
    {
        private const string EntranceRootPrefix = "Level Entrance ";
        private const string AnimatedFireLightName = "pointlight_fire_dungeon";

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

        private readonly List<Light> _entrancePointLights = new(64);
        private readonly List<Light> _animatedFireLights = new(64);
        private readonly Dictionary<Transform, Light> _heroShadowLightsByEntrance = new(4);
        private float _nextFlickerUpdateAt;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (!string.Equals(
                    SceneManager.GetActiveScene().name,
                    ArenaMapCatalog.ArenaMap01SceneName,
                    StringComparison.Ordinal))
                return;
            if (FindAnyObjectByType<ArenaMap01LightingBudget>() != null)
                return;

            new GameObject(nameof(ArenaMap01LightingBudget)).AddComponent<ArenaMap01LightingBudget>();
        }

        private void Awake()
        {
            Light[] lights = FindObjectsByType<Light>(FindObjectsInactive.Include);
            foreach (Light light in lights)
            {
                Transform? entrance = FindEntranceRoot(light.transform);
                if (light.gameObject.scene != gameObject.scene
                    || light.type != LightType.Point
                    || entrance == null)
                {
                    continue;
                }

                _entrancePointLights.Add(light);
                light.shadows = LightShadows.None;
                if (!_heroShadowLightsByEntrance.TryGetValue(entrance, out Light existing)
                    || (light.transform.position - entrance.position).sqrMagnitude
                    < (existing.transform.position - entrance.position).sqrMagnitude)
                {
                    _heroShadowLightsByEntrance[entrance] = light;
                }

                if (!string.Equals(light.gameObject.name, AnimatedFireLightName, StringComparison.Ordinal))
                    continue;

                Animator? animator = light.GetComponent<Animator>();
                if (animator != null)
                    animator.enabled = false;
                _animatedFireLights.Add(light);
            }

            ArenaGraphicsSettings.Changed += OnGraphicsSettingsChanged;
            ApplyLightShadowQuality();
            ApplyFlicker(Time.unscaledTime);
        }

        private void OnDestroy()
        {
            ArenaGraphicsSettings.Changed -= OnGraphicsSettingsChanged;
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
            _nextFlickerUpdateAt = now + 1f / ArenaGraphicsSettings.EffectsAnimationUpdatesPerSecond;
            float intensity = FireFlicker.Evaluate(now);
            foreach (Light light in _animatedFireLights)
                if (light != null)
                    light.intensity = intensity;
        }

        private void OnGraphicsSettingsChanged()
        {
            _nextFlickerUpdateAt = 0f;
            ApplyLightShadowQuality();
        }

        private void ApplyLightShadowQuality()
        {
            foreach (Light light in _entrancePointLights)
                if (light != null)
                    light.shadows = LightShadows.None;

            if (ArenaGraphicsSettings.LightShadowQuality != ArenaLightShadowQuality.Hero)
                return;

            // One representative point light per authored entrance keeps High
            // mode useful without restoring all 56 six-face point shadows.
            foreach (Light light in _heroShadowLightsByEntrance.Values)
                if (light != null)
                    light.shadows = LightShadows.Soft;
        }

        private static Transform? FindEntranceRoot(Transform transform)
        {
            for (Transform? current = transform; current != null; current = current.parent)
                if (current.name.StartsWith(EntranceRootPrefix, StringComparison.Ordinal))
                    return current;

            return null;
        }
    }
}
