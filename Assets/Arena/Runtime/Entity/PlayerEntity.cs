#nullable enable
using System.Collections.Generic;
using UnityEngine;
using Arena.Simulation;
using Arena.Presentation;
using Arena.Presentation.Appearance;
using Arena.Presentation.Targeting;
using Arena.Presentation.VFX;
using Arena.Input;
using Arena.Combat;
using SpacetimeDB;
using SpacetimeDB.Types;

namespace Arena.Entity
{
    /// <summary>
    /// Runtime record for one player.
    /// Owns the character GameObject and the ClientSimulationState.
    ///
    /// Uses a player avatar prefab for the character model. Optional Starter Assets
    /// prefab components are used for authored presentation and camera defaults only.
    /// Runtime player input/movement is owned by Arena components.
    ///
    /// INVARIANT: Created and destroyed only by EntityRegistry.
    ///            Does NOT touch the network.
    /// </summary>
    public class PlayerEntity : ICombatTargetEntity
    {
        public readonly Identity Identity;
        public readonly ClientSimulationState SimState;
        public readonly GameObject GameObject;
        public readonly PlayerView View;
        public bool IsDestroyed => GameObject == null;
        public bool IsLocalPlayer { get; }

        public string Username { get; private set; } = string.Empty;
        public string CombatProfile { get; private set; } = CombatProfileIds.Default;
        public bool IsAlive { get; private set; } = true;
        public bool IsEliminated { get; private set; }
        public bool IsDummy { get; private set; }
        public int Hp { get; private set; }
        public int MaxHp { get; private set; }
        public string PrimaryResourceKind { get; private set; } = string.Empty;
        public float CurrentPrimaryResource { get; private set; }
        public float MaxPrimaryResource { get; private set; }
        public string StaminaResourceKind { get; private set; } = "STAMINA";
        public float CurrentStamina { get; private set; }
        public float MaxStamina { get; private set; }
        public string ManaResourceKind { get; private set; } = "MANA";
        public float CurrentMana { get; private set; }
        public float MaxMana { get; private set; }
        public Timestamp RespawnAt { get; private set; }
        public bool GameplayInCombat { get; private set; }
        public Timestamp GameplayCombatExpiresAt { get; private set; }
        public string GameplayCombatReason { get; private set; } = string.Empty;
        public Identity TargetIdentity => Identity;
        public GameObject TargetGameObject => GameObject;
        public float HitRadius => SimState.HitRadius;
        public float HitHeight => SimState.HitHeight;
        public string DisplayName => string.IsNullOrWhiteSpace(Username) ? Identity.ToString() : Username;
        public float PresentationEffectiveDelayMs =>
            IsLocalPlayer ? 0f : SimState.RemoteEffectiveDelayMsForDebug;

        private readonly Dictionary<string, int> _effectCounts = new();
        private readonly Transform? _presentationRoot;
        private GameObject? _soulstealerCasterVfx;
        private Renderer[]? _renderers;
        private Color _baseColor;
        private bool _isHighlighted;
        private bool _isSelected;
        private NameTag _nameTag = null!;
        private WorldHealthBar _worldHpBar = null!;
        private SelectedTargetIndicator? _selectedTargetIndicator;
        private PlayerAnimator? _animator;
        private RuntimeAvatarController? _avatarController;
        private RuntimeAvatarBinding? _avatarBinding;
        private WeaponAttachmentController? _weaponAttachments;
        private StealthVisualController? _stealthVisual;
        private PresentationScaleController? _presentationScale;
        private readonly int _spawnFrame;
        private LocalPlayerStateProvider? _stateProvider;
        private CombatAnimationSet? _combatAnimationSet;
        private string _combatAnimationModeId = string.Empty;
        private bool _combatAnimationModeInitialized;
        private readonly Dictionary<string, EquippedWeaponVisual> _equippedWeaponVisualsByRole = new(System.StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _equippedArmorItemDefIdsBySlot = new(System.StringComparer.Ordinal);
        private SharedActionProfile? _sharedActionProfile;
        private SpellCastPresentationController? _spellCastPresentation;
        private string _appliedAppearanceSignature = string.Empty;
        private bool _starterDefaultAvatarApplied;
        private bool _hasExplicitAppearance;
        private static readonly Color TargetIndicatorHostile = new(1f, 0.02f, 0.015f, 1f);
        private static readonly Color TargetIndicatorNeutral = new(1f, 0.82f, 0.18f, 1f);
        private static readonly Color TargetIndicatorParty = new(0.2f, 0.75f, 0.3f, 1f);
        private const float GigantismPresentationScale = 1.5f;

        public PlayerEntity(Identity identity, bool isLocalPlayer, GameObject? prefab)
        {
            Identity       = identity;
            _spawnFrame    = Time.frameCount;
            SimState       = new ClientSimulationState();
            SimState.SetIsLocalPlayer(isLocalPlayer);
            IsLocalPlayer = isLocalPlayer;

            if (prefab != null)
            {
                GameObject = Object.Instantiate(prefab);
                _presentationRoot = isLocalPlayer ? CreateLocalPresentationRoot(GameObject.transform) : null;

                if (!isLocalPlayer)
                {
                    // Remote players: remove controller components, keep mesh + animator.
                    DisablePlayerInput(GameObject);
                    // CharacterController must be destroyed after ThirdPersonController
                    // (which has [RequireComponent(typeof(CharacterController))]).
                    DestroyComponent<CharacterController>(GameObject);
                }

                _renderers = GameObject.GetComponentsInChildren<Renderer>();

                // Set up animation.
                _animator = GameObject.GetComponent<PlayerAnimator>();
                if (_animator == null)
                    _animator = GameObject.AddComponent<PlayerAnimator>();
                _animator.Initialize(SimState, isLocalPlayer, _presentationRoot);

                _avatarController = GameObject.GetComponent<RuntimeAvatarController>();
                if (_avatarController == null)
                    _avatarController = GameObject.AddComponent<RuntimeAvatarController>();
                _avatarController.SetVisualRootParent(_presentationRoot ?? GameObject.transform);

                _weaponAttachments = GameObject.GetComponent<WeaponAttachmentController>();
                if (_weaponAttachments == null)
                    _weaponAttachments = GameObject.AddComponent<WeaponAttachmentController>();
                _weaponAttachments.Initialize();

                _stateProvider = GameObject.GetComponent<LocalPlayerStateProvider>();
            }
            else
            {
                // Fallback: capsule primitive (works without any prefab).
                GameObject = UnityEngine.GameObject.CreatePrimitive(PrimitiveType.Capsule);
                Object.Destroy(GameObject.GetComponent<Collider>());
                _presentationRoot = null;
                _renderers = GameObject.GetComponentsInChildren<Renderer>();
                _avatarController = GameObject.GetComponent<RuntimeAvatarController>();
                if (_avatarController == null)
                    _avatarController = GameObject.AddComponent<RuntimeAvatarController>();
                _avatarController.SetVisualRootParent(GameObject.transform);
            }

            _stealthVisual = GameObject.GetComponent<StealthVisualController>();
            if (_stealthVisual == null)
                _stealthVisual = GameObject.AddComponent<StealthVisualController>();
            _stealthVisual.MaterialsRestored += RefreshEffectTint;

            _presentationScale = GameObject.GetComponent<PresentationScaleController>();
            if (_presentationScale == null)
                _presentationScale = GameObject.AddComponent<PresentationScaleController>();

            GameObject.name = $"Player_{identity}";

            _baseColor = Color.white;
            ApplyColorToRenderers(_baseColor);

            View = GameObject.AddComponent<PlayerView>();
            View.Initialize(SimState, isLocalPlayer);

            _spellCastPresentation = GameObject.GetComponent<SpellCastPresentationController>();
            if (_spellCastPresentation == null)
                _spellCastPresentation = GameObject.AddComponent<SpellCastPresentationController>();
            _spellCastPresentation.Initialize(this, isLocalPlayer);

            Transform presentationParent = _presentationRoot ?? GameObject.transform;
            _nameTag    = NameTag.Create(presentationParent, isLocalPlayer);
            _worldHpBar = WorldHealthBar.Create(presentationParent, isLocalPlayer);
            ApplyStarterDefaultAppearanceIfNeeded(force: true);
        }

        private static void DestroyComponent<T>(GameObject go) where T : Component
        {
            var comp = go.GetComponent<T>();
            if (comp != null) Object.Destroy(comp);
        }

        internal static void DisablePlayerInput(GameObject? go)
        {
            if (go == null) return;

            StarterAssetsRuntimeStripper.StripFrom(go);
        }

        public void SetUsername(string username)
        {
            Username = username;
            if (IsDestroyed) return;

            if (!string.IsNullOrEmpty(username))
            {
                GameObject.name = $"Player_{username}";
                _nameTag.SetName(username);
            }
        }

        public void ApplyStatusEffect(string effectKind)
        {
            _effectCounts[effectKind] = (_effectCounts.TryGetValue(effectKind, out var n) ? n : 0) + 1;
            RefreshGigantismScale();
            RefreshWeaponStatusVisibility();
            RefreshEffectTint();
            RefreshSoulstealerCasterVfx();
        }

        public void RemoveStatusEffect(string effectKind)
        {
            if (_effectCounts.TryGetValue(effectKind, out var n) && n > 1)
                _effectCounts[effectKind] = n - 1;
            else
                _effectCounts.Remove(effectKind);
            RefreshGigantismScale();
            RefreshWeaponStatusVisibility();
            RefreshEffectTint();
            RefreshSoulstealerCasterVfx();
        }

        private void RefreshGigantismScale()
        {
            if (_presentationScale == null)
                return;

            _presentationScale.SetTarget(_avatarController != null ? _avatarController.VisualRoot : null);

            bool active = false;
            foreach (var effect in _effectCounts)
            {
                if (effect.Value > 0
                    && string.Equals(effect.Key, "GIGANTISM", System.StringComparison.OrdinalIgnoreCase))
                {
                    active = true;
                    break;
                }
            }

            // Statuses hydrated on the spawn frame belong to an entity we are seeing for the
            // first time: it should appear already sized rather than grow in front of us.
            float scale = active ? GigantismPresentationScale : 1f;
            _presentationScale.SetScale(scale, immediate: Time.frameCount <= _spawnFrame);
        }

        private void RefreshSoulstealerCasterVfx()
        {
            bool hasStolenSoul = false;
            foreach (var effect in _effectCounts)
            {
                if (effect.Value > 0
                    && string.Equals(effect.Key, "SOUL_STOLEN", System.StringComparison.OrdinalIgnoreCase))
                {
                    hasStolenSoul = true;
                    break;
                }
            }

            if (!hasStolenSoul)
            {
                if (_soulstealerCasterVfx != null)
                    Object.Destroy(_soulstealerCasterVfx);
                _soulstealerCasterVfx = null;
                return;
            }
            if (_soulstealerCasterVfx != null || IsDestroyed)
                return;

            CombatVFXRegistry.Template? template =
                CombatVFXTemplateRegistry.ResolveTemplate("VFX_SOULSTEALER_CASTER_01");
            if (template?.Prefab == null)
                return;

            Transform parent = _presentationRoot ?? GameObject.transform;
            _soulstealerCasterVfx = Object.Instantiate(template.Prefab, parent, false);
            _soulstealerCasterVfx.name = "Soulstealer_StolenSoulVFX";
            _soulstealerCasterVfx.transform.SetLocalPositionAndRotation(
                template.LocalPositionOffset,
                template.LocalRotation);
            VFXUtils.ApplyPrefabPresentationScale(_soulstealerCasterVfx, template.Scale);
            foreach (ParticleSystem particles in
                     _soulstealerCasterVfx.GetComponentsInChildren<ParticleSystem>(true))
            {
                ParticleSystem.MainModule main = particles.main;
                main.loop = true;
                particles.Play(true);
            }
        }

        private void RefreshWeaponStatusVisibility()
        {
            bool disarmed = false;
            foreach (var effect in _effectCounts)
            {
                if (effect.Value > 0
                    && string.Equals(effect.Key, "DISARM", System.StringComparison.OrdinalIgnoreCase))
                {
                    disarmed = true;
                    break;
                }
            }
            _weaponAttachments?.SetExternallyHidden(disarmed);
        }

        private static readonly Dictionary<string, Color> EffectColors = new()
        {
            { "root",       new Color(0.3f, 0.5f, 1.0f) },
            { "ROOT",       new Color(0.3f, 0.5f, 1.0f) },
            { "slow",       new Color(0.4f, 0.9f, 0.9f) },
            { "SLOW",       new Color(0.4f, 0.9f, 0.9f) },
            { "stun",       new Color(1.0f, 0.9f, 0.2f) },
            { "STUN",       new Color(1.0f, 0.9f, 0.2f) },
            { "freeze",     new Color(0.35f, 0.85f, 1.0f) },
            { "FREEZE",     new Color(0.35f, 0.85f, 1.0f) },
            { "INTIMIDATED", new Color(0.75f, 0.35f, 1.0f) },
            { "FEAR",        new Color(0.75f, 0.35f, 1.0f) },
            { "CONFUSION",   new Color(0.9f, 0.35f, 1.0f) },
            { "KNOCKDOWN",  new Color(0.95f, 0.55f, 0.2f) },
            { "burn",       new Color(1.0f, 0.4f, 0.1f) },
            { "DOT",        new Color(1.0f, 0.4f, 0.1f) },
            { "shock",      new Color(0.9f, 1.0f, 0.3f) },
            { "MOVE_SLOW_IMMUNITY", new Color(1.0f, 0.8f, 0.25f) },
            { "MOVEMENT_IMPAIRING_IMMUNITY", new Color(1.0f, 0.88f, 0.45f) },
            { "STUN_IMMUNITY", new Color(0.45f, 0.88f, 1.0f) },
            { "SILENCE", new Color(0.65f, 0.35f, 1.0f) },
            { "DAMAGE_TAKEN_REDUCTION", new Color(1.0f, 0.72f, 0.18f) },
            { "TARGETED_ABILITY_AVOIDANCE", new Color(0.65f, 0.95f, 1.0f) },
            { "MIRROR_IMAGE", new Color(0.55f, 0.45f, 1.0f) },
            { "GIGANTISM", new Color(0.45f, 0.9f, 0.55f) },
            { "FLURRY", new Color(0.5f, 0.9f, 1.0f) },
            { "RECKONING", new Color(1.0f, 0.72f, 0.18f) },
            { "DAMAGE_REDIRECT", new Color(1.0f, 0.86f, 0.45f) },
        };

        private void RefreshEffectTint()
        {
            Color color;
            if (_effectCounts.Count > 0)
            {
                color = new Color(1f, 0.5f, 0.2f);
                foreach (var kind in _effectCounts.Keys)
                {
                    if (EffectColors.TryGetValue(kind, out var c)) color = c;
                    break;
                }
            }
            else
            {
                color = _baseColor;
            }

            if (_isSelected)
                color = Color.Lerp(color, Color.yellow, 0.35f);
            else if (_isHighlighted)
                color = Color.Lerp(color, Color.white, 0.3f);

            ApplyColorToRenderers(color);
            _stealthVisual?.ReapplyOpacity();
        }

        private void ApplyColorToRenderers(Color color)
        {
            if (_renderers == null) return;
            foreach (var r in _renderers)
            {
                if (r != null)
                {
                    foreach (var mat in r.materials)
                        mat.color = color;
                }
            }
        }

        public void SetState(PlayerState state)
        {
            bool wasDead = !IsAlive;
            IsAlive      = state.Alive;
            IsEliminated = state.Eliminated;
            IsDummy      = state.IsDummy;
            Hp           = state.Hp;
            MaxHp        = state.MaxHp;
            RespawnAt    = state.RespawnAt;
            SimState.SetMovementContext(
                state.MovementBlocked,
                state.MoveSpeedMultiplier,
                state.HitRadius,
                state.HitHeight,
                state.MovementContextTick);

            if (IsDestroyed) return;

            var localMotor = GameObject.GetComponent<LocalPlayerMotor>();
            localMotor?.SetHitCapsule(state.HitRadius, state.HitHeight);

            // Renderers stay on through death so the death animation plays.
            // Only suppress them when the player is permanently eliminated.
            if (_avatarController?.CurrentBinding != null)
                _renderers = _avatarController.RefreshRenderers();
            if (_renderers != null)
            {
                foreach (var r in _renderers)
                    if (r != null) r.enabled = !state.Eliminated;
            }

            _worldHpBar.SetHealth(state.Hp, state.MaxHp);
            _animator?.SetDead(!state.Alive);
            if (IsDummy && !string.IsNullOrWhiteSpace(CombatProfile))
                SetInCombat(true);

            RefreshSelectedTargetIndicator();
        }

        public void RequestCombatAnimation(in CombatAnimationRequest request) => _animator?.RequestCombatAnimation(request);
        public void PlayAutoAttackGhost(string actionId, long startedAtMs, Vector3 facingTargetPoint)
            => _animator?.PlayAutoAttackGhost(actionId, startedAtMs, facingTargetPoint);
        public void BeginWorldInteractionAnimation(ActiveWorldInteraction row)
            => _animator?.BeginWorldInteractionAnimation(row);
        public void EndWorldInteractionAnimation(string actionInstanceId, bool completed)
            => _animator?.EndWorldInteractionAnimation(actionInstanceId, completed);
        public void PredictSpellCastHold(string spellActionId, long localStartedAtMs, string targetId, Vector3? aimPoint, CastActionToken token)
            => _spellCastPresentation?.PredictLocalCastHold(spellActionId, localStartedAtMs, targetId, aimPoint, token);
        public void CancelLocalSpellCastHold(CastActionToken token)
            => _spellCastPresentation?.CancelLocalCastHold(token);
        public void OnActiveCastInsert(ActiveCast row, ulong castTimeMs) => _spellCastPresentation?.OnActiveCastInsert(row, castTimeMs);
        public void OnActiveCastUpdate(ActiveCast row, ulong castTimeMs) => _spellCastPresentation?.OnActiveCastUpdate(row, castTimeMs);
        public void OnActiveCastDelete(ActiveCast row) => _spellCastPresentation?.OnActiveCastDelete(row);
        public void OnPredictedActionResultInsert(PredictedActionResult row) => _spellCastPresentation?.OnPredictedActionResultInsert(row);
        public void OnSpellCombatRelease(CombatEvent row) => _spellCastPresentation?.OnCombatRelease(row);
        public bool UsesSpellCastHoldPresentation(string spellActionId)
        {
            return _combatAnimationSet != null
                && SpellCastAnimationResolver.TryResolve(_combatAnimationSet, spellActionId, out WeaponSpellAnimationEntry entry)
                && entry.UsesHoldPresentation;
        }

        public bool PlaysSpellReleasePresentation(string spellActionId)
        {
            if (SpellCastAnimationResolver.IsExplicitlyNoAnimation(spellActionId))
                return false;

            return _combatAnimationSet == null
                || !SpellCastAnimationResolver.TryResolve(_combatAnimationSet, spellActionId, out WeaponSpellAnimationEntry entry)
                || entry.PlaysReleasePresentation;
        }

        public bool TryResolveSpellReleaseOffsetSeconds(string spellActionId, out float releaseOffsetSeconds)
        {
            releaseOffsetSeconds = 0f;
            if (_combatAnimationSet == null
                || !SpellCastAnimationResolver.TryResolve(_combatAnimationSet, spellActionId, out WeaponSpellAnimationEntry entry)
                || !entry.PlaysReleasePresentation
                || entry.ResolveClip(grounded: true) == null)
            {
                return false;
            }

            releaseOffsetSeconds = entry.ResolveReleaseOffsetSeconds(grounded: true);
            return true;
        }
        public void StartParry() => _animator?.StartParry();
        public void TriggerParryHit() => _animator?.TriggerParryHit();
        public void SetParryArmed(bool armed) => _animator?.SetParryArmed(armed);
        public void SetBlocking(bool isBlocking) => _animator?.SetBlocking(isBlocking);
        public void TriggerBlockHit() => _animator?.TriggerBlockHit();
        public void TriggerDodge(MovementActionState row) => _animator?.TriggerDodge(row);
        public void SetSpecialMovementRuntime(SpecialMovementRuntime row) => SimState.SetSpecialMovementRuntime(row);
        public void SetLingeringShadeState(LingeringShadeState row) => _animator?.SetLingeringShadeState(row);
        public void ClearLingeringShadeState() => _animator?.ClearLingeringShadeState();
        public void ClearSpecialMovementRuntime()
        {
            SimState.ClearSpecialMovementRuntime();
            _animator?.RequestSpecialMovementDrivenPhasedMeleeEnd();
        }

        /// <summary>
        /// Unwinds a predicted gap-close windup after a prediction timeout
        /// (feel audit F5): the held loop resolves through the end segment.
        /// Server rejections do NOT come here — reject = interrupt, never
        /// completion (netcode design review S2); they route through
        /// <see cref="CutRejectedActionPresentation"/> instead. When an
        /// authoritative special movement is live its row delete owns the end
        /// request.
        /// </summary>
        public void RollbackPredictedGapCloseWindup()
        {
            if (SimState.TryGetSpecialMovementTrack(out _))
                return;
            _animator?.RequestSpecialMovementDrivenPhasedMeleeEnd();
        }

        public bool RequestCombatLifecycleDrivenPhasedMeleeEnd(string actionId)
        {
            return _animator?.RequestCombatLifecycleDrivenPhasedMeleeEnd(actionId) == true;
        }

        public bool CancelPhasedMeleeAction(string actionId)
        {
            return _animator?.CancelPhasedMeleeAction(actionId) == true;
        }

        /// <summary>
        /// Cuts the predicted presentation of a server-rejected action
        /// (netcode design review S2) via the shared preemption primitives —
        /// no windup playing to completion, no forced end segment. Scoped by
        /// action identity downstream; skipped entirely while an authoritative
        /// special movement is live, because that row's delete owns its own
        /// presentation end (same guard as the gap-close rollback).
        /// </summary>
        public void CutRejectedActionPresentation(string rejectedActionId)
        {
            if (SimState.TryGetSpecialMovementTrack(out _))
                return;
            _animator?.CutRejectedActionPresentation(rejectedActionId);
        }
        public void SetMovementActionState(MovementActionState row) => SimState.SetMovementActionState(row);
        public void ClearMovementActionState() => SimState.ClearMovementActionState();
        public void SetKnockedDown(bool isKnockedDown) => _animator?.SetKnockedDown(isKnockedDown);
        public void SetHardCrowdControl(string? statusKind) => _animator?.SetHardCrowdControl(statusKind);
        public void TriggerHit(Vector3 hitDirection)
        {
            if (IsDestroyed) return;
            _animator?.TriggerHit(hitDirection, GameObject.transform.forward);
        }

        public void TriggerStagger(Vector3 hitDirection)
        {
            if (IsDestroyed) return;
            _animator?.TriggerStagger(hitDirection, GameObject.transform.forward);
        }
        public void SetCombatAnimationSet(CombatAnimationSet set)
        {
            _combatAnimationSet = set;
            _animator?.ApplyAnimationSet(set);
            _animator?.ApplyCombatLocomotionMode(_combatAnimationModeId);
            _weaponAttachments?.ApplyAnimationSet(set, _equippedWeaponVisualsByRole);
            RefreshStealthVisualTargets();
        }

        public bool UsesCombatAnimationSet(CombatAnimationSet set)
        {
            return ReferenceEquals(_combatAnimationSet, set);
        }

        public void SetCombatAnimationMode(string? modeId)
        {
            string normalizedModeId = string.IsNullOrWhiteSpace(modeId)
                ? string.Empty
                : modeId.Trim().ToUpperInvariant();
            if (_combatAnimationModeInitialized
                && string.Equals(_combatAnimationModeId, normalizedModeId, System.StringComparison.Ordinal))
                return;

            _combatAnimationModeId = normalizedModeId;
            _combatAnimationModeInitialized = true;
            _animator?.ApplyCombatLocomotionMode(_combatAnimationModeId);
            _stealthVisual?.SetStealthed(string.Equals(
                _combatAnimationModeId,
                CombatModeIds.Stealthed,
                System.StringComparison.Ordinal));
        }

        public void SetEquippedWeaponVisuals(IEnumerable<EquippedWeaponVisual> visuals)
        {
            _equippedWeaponVisualsByRole.Clear();
            foreach (EquippedWeaponVisual visual in visuals)
            {
                if (string.IsNullOrWhiteSpace(visual.RoleId) || string.IsNullOrWhiteSpace(visual.ItemDefId) || visual.Prefab == null)
                    continue;

                _equippedWeaponVisualsByRole[visual.RoleId.Trim()] = visual;
            }

            if (_combatAnimationSet != null)
                _weaponAttachments?.ApplyAnimationSet(_combatAnimationSet, _equippedWeaponVisualsByRole);
            RefreshStealthVisualTargets();
        }

        public void SetEquippedArmorItemDefIdsBySlot(Dictionary<string, string> itemDefIdsBySlot)
        {
            _equippedArmorItemDefIdsBySlot.Clear();
            foreach (var pair in itemDefIdsBySlot)
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value))
                    continue;

                _equippedArmorItemDefIdsBySlot[pair.Key.Trim().ToUpperInvariant()] = pair.Value.Trim().ToUpperInvariant();
            }

            if (_avatarController == null)
                return;

            if (!_avatarController.SetEquipmentAppearanceOverride(
                    _equippedArmorItemDefIdsBySlot,
                    out RuntimeAvatarBinding binding,
                    out string error))
            {
                if (!string.IsNullOrWhiteSpace(error))
                    Debug.LogWarning($"[{nameof(PlayerEntity)}] Failed to apply equipment appearance to '{GameObject.name}': {error}");
                return;
            }

            BindRuntimeAvatar(binding);
        }

        public void ApplyAppearance(CharacterAppearance row)
        {
            if (IsDestroyed)
                return;

            string appearanceSignature = RuntimeAvatarController.SignatureFor(row);
            if (string.Equals(_appliedAppearanceSignature, appearanceSignature, System.StringComparison.Ordinal))
            {
                _hasExplicitAppearance = true;
                _starterDefaultAvatarApplied = false;
                return;
            }

            if (_avatarController == null)
            {
                _avatarController = GameObject.GetComponent<RuntimeAvatarController>();
                if (_avatarController == null)
                    _avatarController = GameObject.AddComponent<RuntimeAvatarController>();
                _avatarController.SetVisualRootParent(_presentationRoot ?? GameObject.transform);
            }
            _avatarController.SetEquipmentAppearanceOverride(
                _equippedArmorItemDefIdsBySlot,
                out _,
                out _);

            if (!_avatarController.Apply(row, out RuntimeAvatarBinding binding, out string error))
            {
                Debug.LogWarning($"[{nameof(PlayerEntity)}] Failed to apply appearance to '{GameObject.name}': {error}");
                return;
            }

            _hasExplicitAppearance = true;
            _starterDefaultAvatarApplied = false;
            BindRuntimeAvatar(binding);
            _appliedAppearanceSignature = binding.AppearanceSignature;
        }

        private void ApplyStarterDefaultAppearanceIfNeeded(bool force = false)
        {
            if (IsDestroyed || _hasExplicitAppearance)
                return;

            if (!force && _avatarBinding != null && _starterDefaultAvatarApplied)
            {
                return;
            }

            if (_avatarController == null)
            {
                _avatarController = GameObject.GetComponent<RuntimeAvatarController>();
                if (_avatarController == null)
                    _avatarController = GameObject.AddComponent<RuntimeAvatarController>();
                _avatarController.SetVisualRootParent(_presentationRoot ?? GameObject.transform);
            }
            _avatarController.SetEquipmentAppearanceOverride(
                _equippedArmorItemDefIdsBySlot,
                out _,
                out _);

            if (!_avatarController.ApplyStarterDefault(out RuntimeAvatarBinding binding, out string error))
            {
                Debug.LogWarning($"[{nameof(PlayerEntity)}] Failed to apply starter default appearance to '{GameObject.name}': {error}");
                return;
            }

            _starterDefaultAvatarApplied = true;
            BindRuntimeAvatar(binding);
            _appliedAppearanceSignature = binding.AppearanceSignature;
        }

        private void BindRuntimeAvatar(RuntimeAvatarBinding binding)
        {
            _weaponAttachments?.ClearVisuals();
            _avatarBinding = binding;
            _renderers = binding.Renderers;
            _animator?.BindAnimator(binding.Animator);
            _weaponAttachments?.BindMounts(binding.Mounts);
            if (_combatAnimationSet != null)
                SetCombatAnimationSet(_combatAnimationSet);
            if (_sharedActionProfile != null)
                SetSharedActionProfile(_sharedActionProfile);
            RefreshGigantismScale();
            RefreshEffectTint();
            RefreshStealthVisualTargets();
        }

        private void RefreshStealthVisualTargets()
        {
            if (_stealthVisual == null)
                return;

            var renderers = new List<Renderer>();
            if (_renderers != null)
                renderers.AddRange(_renderers);
            _weaponAttachments?.AppendVisualRenderers(renderers);
            _stealthVisual.RefreshRenderers(renderers);
        }

        public void SetSharedActionProfile(SharedActionProfile profile)
        {
            _sharedActionProfile = profile;
            _animator?.ApplySharedActionProfile(profile);
        }

        public void SetCombatProfile(string combatProfile)
        {
            CombatProfile = CombatProfileIds.Normalize(combatProfile);
            if (IsDummy)
                SetInCombat(true);
        }

        public void SetPrimaryResource(string kind, float current, float max)
        {
            SetResource(kind, current, max);
        }

        public void SetResource(string kind, float current, float max)
        {
            string normalizedKind = string.IsNullOrWhiteSpace(kind)
                ? string.Empty
                : kind.Trim().ToUpperInvariant();
            float normalizedCurrent = Mathf.Max(0f, current);
            float normalizedMax = Mathf.Max(0f, max);

            if (normalizedKind == "MANA")
            {
                ManaResourceKind = normalizedKind;
                CurrentMana = normalizedCurrent;
                MaxMana = normalizedMax;
                return;
            }

            if (normalizedKind == "STAMINA")
            {
                StaminaResourceKind = normalizedKind;
                CurrentStamina = normalizedCurrent;
                MaxStamina = normalizedMax;
            }

            PrimaryResourceKind = string.IsNullOrWhiteSpace(kind)
                ? string.Empty
                : kind.Trim().ToUpperInvariant();
            CurrentPrimaryResource = normalizedCurrent;
            MaxPrimaryResource = normalizedMax;
        }

        public void ClearPrimaryResource()
        {
            PrimaryResourceKind = string.Empty;
            CurrentPrimaryResource = 0f;
            MaxPrimaryResource = 0f;
            StaminaResourceKind = "STAMINA";
            CurrentStamina = 0f;
            MaxStamina = 0f;
            ManaResourceKind = "MANA";
            CurrentMana = 0f;
            MaxMana = 0f;
        }

        public void ClearResource(string kind)
        {
            string normalizedKind = string.IsNullOrWhiteSpace(kind)
                ? string.Empty
                : kind.Trim().ToUpperInvariant();
            if (normalizedKind == "MANA")
            {
                CurrentMana = 0f;
                MaxMana = 0f;
                return;
            }
            if (normalizedKind == "STAMINA")
            {
                CurrentStamina = 0f;
                MaxStamina = 0f;
                PrimaryResourceKind = string.Empty;
                CurrentPrimaryResource = 0f;
                MaxPrimaryResource = 0f;
            }
        }

        public void SetInCombat(bool inCombat)
        {
            _animator?.SetInCombat(inCombat);
        }

        public bool IsInCombat => _animator != null && _animator.IsInCombat;

        public void EnterCombatImmediate() => _animator?.EnterCombatImmediate();
        public void ExitCombatImmediate() => _animator?.ExitCombatImmediate();

        public void SetGameplayCombatEngagement(CombatEngagement row)
        {
            GameplayInCombat = true;
            GameplayCombatExpiresAt = row.ExpiresAt;
            GameplayCombatReason = row.Reason ?? string.Empty;
        }

        public void ClearGameplayCombatEngagement()
        {
            GameplayInCombat = false;
            GameplayCombatExpiresAt = default;
            GameplayCombatReason = string.Empty;
        }

        public Vector3? GetSocketPosition(HumanBodyBones bone)
        {
            return _avatarBinding?.GetSocketPosition(bone);
        }

        public bool TryGetSocketTransform(HumanBodyBones bone, out Transform socket)
        {
            if (_avatarBinding != null && _avatarBinding.TryGetSocketTransform(bone, out socket))
                return true;

            socket = null!;
            return false;
        }

        public bool TryGetWeaponMount(string mountId, out Transform mount)
        {
            if (_avatarBinding != null && _avatarBinding.TryGetMount(mountId, out mount))
                return true;

            mount = null!;
            return false;
        }

        public bool TryGetVfxSocket(string socketId, out Transform socket)
        {
            if (_avatarBinding != null && _avatarBinding.TryGetVfxSocket(socketId, out socket))
                return true;

            socket = null!;
            return false;
        }

        public LocalPlayerStateProvider? GetLocalStateProvider()
        {
            if (IsDestroyed) return null;
            return _stateProvider ?? GameObject.GetComponent<LocalPlayerStateProvider>();
        }

        public LocalPlayerInputSource? GetLocalInputSource()
        {
            if (IsDestroyed) return null;
            return GameObject.GetComponent<LocalPlayerInputSource>();
        }

        public Transform GetPresentationRoot()
        {
            return _presentationRoot ?? GameObject.transform;
        }

        public Vector3 GetRenderPosition()
        {
            return SimState.GetRenderPosition();
        }

        public void SetHighlight(bool highlighted)
        {
            _isHighlighted = highlighted;
            RefreshEffectTint();
        }

        public void SetSelected(bool selected)
        {
            _isSelected = selected;
            RefreshEffectTint();
            RefreshSelectedTargetIndicator();
        }

        public void RefreshTargetingPresentation()
        {
            RefreshSelectedTargetIndicator();
        }

        private void RefreshSelectedTargetIndicator()
        {
            bool shouldShow = _isSelected && !IsLocalPlayer && IsAlive && !IsEliminated && !IsDestroyed;
            if (shouldShow && _selectedTargetIndicator == null)
                _selectedTargetIndicator = GameObject.AddComponent<SelectedTargetIndicator>();

            _selectedTargetIndicator?.SetColor(ResolveSelectedTargetIndicatorColor());
            _selectedTargetIndicator?.SetVisible(shouldShow);
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

        public void Destroy()
        {
            if (IsDestroyed) return;

            if (_stealthVisual != null)
                _stealthVisual.MaterialsRestored -= RefreshEffectTint;
            DisablePlayerInput(GameObject);
            Object.Destroy(GameObject);
        }

        private static Transform CreateLocalPresentationRoot(Transform simulationRoot)
        {
            var presentationRoot = new GameObject("LocalPresentationRoot").transform;
            presentationRoot.SetParent(simulationRoot, false);
            presentationRoot.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
            presentationRoot.localScale = Vector3.one;

            var children = new List<Transform>();
            foreach (Transform child in simulationRoot)
            {
                if (child == presentationRoot)
                    continue;

                children.Add(child);
            }

            foreach (Transform child in children)
                child.SetParent(presentationRoot, true);

            return presentationRoot;
        }
    }
}
