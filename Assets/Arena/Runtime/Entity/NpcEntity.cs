#nullable enable
using System;
using System.Collections.Generic;
using Arena.Combat;
using Arena.Debugging;
using Arena.Network;
using Arena.Presentation;
using Arena.Presentation.Targeting;
using Arena.Simulation;
using SpacetimeDB;
using SpacetimeDB.Types;
using UnityEngine;

namespace Arena.Entity
{
    public sealed class NpcEntity : ICombatTargetEntity
    {
        public readonly Identity Identity;
        public readonly GameObject GameObject;

        private readonly NameTag _nameTag;
        private readonly WorldHealthBar _worldHealthBar;
        private readonly NpcAnimationController _animationController;
        private readonly Dictionary<string, int> _effectCounts = new(StringComparer.OrdinalIgnoreCase);
        private readonly Renderer[] _renderers;
        private readonly Dictionary<Material, Color> _baseMaterialColors = new();
        // StopsWhenIdle: NpcPhysics has no heartbeat — chase writes per chase
        // tick, facing writes only on yaw change, nothing writes while the
        // NPC is stationary — so no rows past the extrapolation cap means
        // settled while global row delivery is healthy.
        private readonly RemotePresentationBuffer _presentation =
            new(RemotePresentationBuffer.SourceRowCadence.StopsWhenIdle);
        private NpcInstance _instance;
        private NpcState? _state;
        private bool _isHighlighted;
        private bool _isSelected;
        private bool _hasPhysicsSample;
        private Vector3 _lastAuthoritativePosition;
        private float _lastAuthoritativeYawRadians;
        private bool _hasRenderedSample;
        private Vector3 _lastRenderedPosition;
        private SelectedTargetIndicator? _selectedTargetIndicator;

        public bool IsDestroyed => GameObject == null;
        public bool IsAlive => _state?.Alive ?? true;
        public int Hp => _state?.Hp ?? 0;
        public int MaxHp => Mathf.Max(_state?.MaxHp ?? 1, 1);
        public float HitRadius => Mathf.Max(_state?.HitRadius ?? 0.45f, 0.1f);
        public float HitHeight => Mathf.Max(_state?.HitHeight ?? 1.35f, HitRadius * 2f);
        public Identity TargetIdentity => Identity;
        public GameObject TargetGameObject => GameObject;
        public string DisplayName => _instance.DisplayName;
        public string TemplateId => _instance.TemplateId;
        public string VisualId => _instance.VisualId;
        public string Faction => _instance.Faction;
        public int PresentationHardSnapCount => _presentation.HardSnapCount;
        public int PresentationInterpolationSampleCount => _presentation.InterpolationSampleCount;
        public int PresentationExtrapolationSampleCount => _presentation.ExtrapolationSampleCount;
        public int PresentationStarvedSampleCount => _presentation.StarvedSampleCount;
        public int PresentationSettledSampleCount => _presentation.SettledSampleCount;
        public RemotePresentationBuffer.SampleClass? PresentationLastSampleClass => _presentation.LastTickSampleClass;
        public float PresentationLastPositionError => _presentation.LastPositionError;
        public float PresentationMaxPositionErrorObserved => _presentation.MaxPositionErrorObserved;
        public bool PresentationUsedServerTimeline => _presentation.LastTickUsedServerTimeline;
        public float PresentationEffectiveDelayMs => _presentation.LastEffectiveDelayMs;
        public float PresentationBufferAheadTicks => _presentation.LastBufferAheadTicks;
        private static readonly Color TargetIndicatorHostile = new(1f, 0.02f, 0.015f, 1f);
        private static readonly Color TargetIndicatorNeutral = new(1f, 0.82f, 0.18f, 1f);
        private static readonly Color TargetIndicatorParty = new(0.2f, 0.75f, 0.3f, 1f);
        private static readonly string[] HardCrowdControlAnimationPriority =
        {
            "STUN",
            "FREEZE",
            "FEAR",
            "INTIMIDATED",
        };

        public NpcEntity(NpcInstance instance, NpcPhysics? physics, NpcState? state, UnityEngine.Object prefabAsset)
        {
            Identity = instance.Identity;
            _instance = instance;
            _state = state;

            GameObject = InstantiateRoot(prefabAsset);
            GameObject.name = $"NPC_{SafeName(instance.DisplayName)}_{instance.Identity}";
            _renderers = GameObject.GetComponentsInChildren<Renderer>(includeInactive: true);
            CaptureBaseMaterialColors();

            _nameTag = NameTag.Create(GameObject.transform, isLocalPlayer: false);
            _nameTag.SetName(instance.DisplayName);
            _worldHealthBar = WorldHealthBar.Create(GameObject.transform, isLocalPlayer: false);
            _animationController = NpcAnimationController.Attach(GameObject);
            _animationController.SetTemplate(instance.TemplateId);
            ConfigureAnimationProfile(instance.VisualId);
            if (state != null)
                _worldHealthBar.SetHealth(state.Hp, state.MaxHp);

            if (physics != null)
                ApplyPhysics(physics);
        }

        public void ApplyInstance(NpcInstance instance)
        {
            _instance = instance;
            if (IsDestroyed)
                return;

            GameObject.name = $"NPC_{SafeName(instance.DisplayName)}_{instance.Identity}";
            _nameTag.SetName(instance.DisplayName);
            _animationController.SetTemplate(instance.TemplateId);
            ConfigureAnimationProfile(instance.VisualId);
            RefreshHardCrowdControlAnimation();
        }

        public void ApplyStatusEffect(string effectKind)
        {
            string normalized = NormalizeEffectKind(effectKind);
            if (string.IsNullOrEmpty(normalized))
                return;

            _effectCounts[normalized] = (_effectCounts.TryGetValue(normalized, out int count) ? count : 0) + 1;
            RefreshEffectTint();
            RefreshHardCrowdControlAnimation();
        }

        public void RemoveStatusEffect(string effectKind)
        {
            string normalized = NormalizeEffectKind(effectKind);
            if (string.IsNullOrEmpty(normalized))
                return;

            if (_effectCounts.TryGetValue(normalized, out int count) && count > 1)
                _effectCounts[normalized] = count - 1;
            else
                _effectCounts.Remove(normalized);
            RefreshEffectTint();
            RefreshHardCrowdControlAnimation();
        }

        /// <summary>
        /// Buffers an authoritative NpcPhysics row for interpolated rendering.
        /// NPC velocity is not replicated, so snapshots carry zero velocity and
        /// the buffer's capped extrapolation degrades to position-hold.
        /// </summary>
        public void ApplyPhysics(NpcPhysics physics)
        {
            if (IsDestroyed)
                return;

            Vector3 nextPosition = new(physics.PosX, physics.PosY, physics.PosZ);
            _lastAuthoritativePosition = nextPosition;
            _lastAuthoritativeYawRadians = physics.Yaw;
            _presentation.Push(new PlayerSnapshot(
                physics.PosX,
                physics.PosY,
                physics.PosZ,
                0f,
                0f,
                0f,
                physics.Yaw,
                grounded: true,
                lastProcessedTick: 0u,
                receivedTime: Time.realtimeSinceStartup,
                serverTimeMs: RemotePresentationBuffer.QuantizeServerTimeMicros(
                    physics.UpdatedAt.MicrosecondsSinceUnixEpoch)));

            if (!_hasPhysicsSample)
            {
                _presentation.ForceRenderPose(nextPosition, physics.Yaw);
                GameObject.transform.SetPositionAndRotation(
                    nextPosition,
                    Quaternion.Euler(0f, physics.Yaw * Mathf.Rad2Deg, 0f));
                _hasPhysicsSample = true;
            }
        }

        /// <summary>
        /// Called once per frame by EntityRegistry: applies the interpolated
        /// render pose and feeds locomotion speed from the rendered delta.
        /// </summary>
        public void TickPresentation(float dt)
        {
            if (IsDestroyed || !_hasPhysicsSample)
                return;

            int hardSnapsBefore = _presentation.HardSnapCount;
            // F4 warmup gate (S5): server-time timeline only once the clock
            // has a precise sample — see ClientSimulationState.Tick.
            _presentation.Tick(
                dt,
                Time.realtimeSinceStartup,
                ArenaServerClock.HasPreciseSample ? ArenaServerClock.ServerNowMs : (long?)null,
                _lastAuthoritativePosition,
                _lastAuthoritativeYawRadians,
                NetcodeReceiveCounters.RowDeliveryFresh(Time.realtimeSinceStartup));

            Vector3 renderPosition = _presentation.RenderPosition;
            GameObject.transform.SetPositionAndRotation(
                renderPosition,
                Quaternion.Euler(0f, _presentation.RenderYawRadians * Mathf.Rad2Deg, 0f));

            bool hardSnapped = _presentation.HardSnapCount != hardSnapsBefore;
            if (_hasRenderedSample && IsAlive && !hardSnapped && dt > 0f)
            {
                Vector2 previousHorizontal = new(_lastRenderedPosition.x, _lastRenderedPosition.z);
                Vector2 nextHorizontal = new(renderPosition.x, renderPosition.z);
                float horizontalSpeed = Vector2.Distance(previousHorizontal, nextHorizontal) / dt;
                _animationController.SetLocomotionSpeed(horizontalSpeed, IsCombatFaction());
            }

            _hasRenderedSample = true;
            _lastRenderedPosition = renderPosition;
        }

        public void ApplyState(NpcState state)
        {
            NpcState? previous = _state;
            _state = state;
            if (IsDestroyed)
                return;

            _worldHealthBar.SetHealth(state.Hp, state.MaxHp);
            RefreshTargetingPresentation();
            if (state.Alive)
            {
                _animationController.Revive();
                RefreshHardCrowdControlAnimation();
                if (previous != null && previous.Alive && state.Hp < previous.Hp)
                    _animationController.PlayHit();
            }
            else if (previous == null || previous.Alive)
            {
                _animationController.PlayDeath();
            }
        }

        public Transform GetPresentationRoot()
        {
            return GameObject.transform;
        }

        public Vector3 GetRenderPosition()
        {
            return GameObject.transform.position;
        }

        public void SetHighlight(bool highlighted)
        {
            _isHighlighted = highlighted;
            RefreshSelectedTargetIndicator();
        }

        public void SetSelected(bool selected)
        {
            _isSelected = selected;
            RefreshSelectedTargetIndicator();
        }

        public void RefreshTargetingPresentation()
        {
            RefreshSelectedTargetIndicator();
        }

        public void PlayAttack()
        {
            if (!IsDestroyed && IsAlive)
                _animationController.PlayAttack();
        }

        public void Destroy()
        {
            if (!IsDestroyed)
                UnityEngine.Object.Destroy(GameObject);
        }

        private void RefreshSelectedTargetIndicator()
        {
            bool shouldShow = (_isSelected || _isHighlighted) && IsAlive && !IsDestroyed;
            if (shouldShow && _selectedTargetIndicator == null)
                _selectedTargetIndicator = GameObject.AddComponent<SelectedTargetIndicator>();

            _selectedTargetIndicator?.SetColor(ResolveSelectedTargetIndicatorColor());
            _selectedTargetIndicator?.SetVisible(shouldShow);
            RefreshEffectTint();
        }

        private Color ResolveSelectedTargetIndicatorColor()
        {
            return PartyRelationship.RelationToLocal(this) switch
            {
                ClientCombatRelation.PartyAlly or ClientCombatRelation.Self => TargetIndicatorParty,
                ClientCombatRelation.Neutral => TargetIndicatorNeutral,
                _ => TargetIndicatorHostile,
            };
        }

        private static readonly Dictionary<string, Color> EffectColors = new(StringComparer.OrdinalIgnoreCase)
        {
            { "ROOT", new Color(0.3f, 0.5f, 1.0f) },
            { "SLOW", new Color(0.4f, 0.9f, 0.9f) },
            { "STUN", new Color(1.0f, 0.9f, 0.2f) },
            { "FREEZE", new Color(0.35f, 0.85f, 1.0f) },
            { "INTIMIDATED", new Color(0.75f, 0.35f, 1.0f) },
            { "FEAR", new Color(0.75f, 0.35f, 1.0f) },
            { "STAGGER", new Color(0.95f, 0.55f, 0.2f) },
            { "KNOCKDOWN", new Color(0.95f, 0.55f, 0.2f) },
            { "DOT", new Color(1.0f, 0.4f, 0.1f) },
            { "DAMAGE_TAKEN_REDUCTION", new Color(1.0f, 0.72f, 0.18f) },
            { "TARGETED_ABILITY_AVOIDANCE", new Color(0.65f, 0.95f, 1.0f) },
        };

        private void CaptureBaseMaterialColors()
        {
            foreach (Renderer renderer in _renderers)
            {
                if (renderer == null)
                    continue;

                foreach (Material material in renderer.materials)
                {
                    if (material != null && material.HasProperty("_Color") && !_baseMaterialColors.ContainsKey(material))
                        _baseMaterialColors.Add(material, material.color);
                }
            }
        }

        private void RefreshEffectTint()
        {
            Color? statusColor = null;
            foreach (string effectKind in _effectCounts.Keys)
            {
                statusColor = EffectColors.TryGetValue(effectKind, out Color color)
                    ? color
                    : new Color(1f, 0.5f, 0.2f);
                break;
            }

            foreach (Renderer renderer in _renderers)
            {
                if (renderer == null)
                    continue;

                foreach (Material material in renderer.materials)
                {
                    if (material == null || !material.HasProperty("_Color"))
                        continue;

                    Color color = statusColor ?? (_baseMaterialColors.TryGetValue(material, out Color baseColor)
                        ? baseColor
                        : Color.white);
                    if (_isSelected)
                        color = Color.Lerp(color, Color.yellow, 0.35f);
                    else if (_isHighlighted)
                        color = Color.Lerp(color, Color.white, 0.3f);
                    material.color = color;
                }
            }
        }

        private void ConfigureAnimationProfile(string visualId)
        {
            if (NpcVisualCatalog.TryLoadDefault(out NpcVisualCatalog catalog, out _)
                && catalog.TryGetEntry(visualId, out NpcVisualCatalogEntry entry))
            {
                _animationController.SetVisualProfile(entry.profile);
                _animationController.SetStatusReactions(entry.profile != null
                    ? entry.profile.StatusReactions
                    : entry.statusReactions);
                return;
            }

            _animationController.SetVisualProfile(null);
            _animationController.SetStatusReactions(null);
        }

        private void RefreshHardCrowdControlAnimation()
        {
            foreach (string effectKind in HardCrowdControlAnimationPriority)
            {
                if (_effectCounts.TryGetValue(effectKind, out int count) && count > 0)
                {
                    _animationController.SetHardCrowdControl(effectKind);
                    return;
                }
            }

            _animationController.SetHardCrowdControl(null);
        }

        private bool IsCombatFaction()
            => string.Equals(_instance.Faction, "HOSTILE", StringComparison.OrdinalIgnoreCase);

        private static string SafeName(string value)
            => string.IsNullOrWhiteSpace(value)
                ? "Npc"
                : value.Trim().Replace(' ', '_');

        private static string NormalizeEffectKind(string? value)
            => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToUpperInvariant();

        private static GameObject InstantiateRoot(UnityEngine.Object prefabAsset)
        {
            UnityEngine.Object instance = UnityEngine.Object.Instantiate(prefabAsset);
            if (instance is GameObject go)
                return go;

            if (instance is Component component)
                return component.transform.root != null
                    ? component.transform.root.gameObject
                    : component.gameObject;

            UnityEngine.Object.Destroy(instance);
            throw new InvalidCastException(
                $"NPC visual asset '{prefabAsset.name}' instantiated as unsupported Unity object type '{instance.GetType().Name}'.");
        }
    }
}
