#nullable enable
using System;
using Arena.Combat;
using Arena.Presentation;
using Arena.Presentation.Targeting;
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
        private NpcInstance _instance;
        private NpcState? _state;
        private bool _isHighlighted;
        private bool _isSelected;
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
        public string Faction => _instance.Faction;
        private static readonly Color TargetIndicatorHostile = new(1f, 0.02f, 0.015f, 1f);
        private static readonly Color TargetIndicatorNeutral = new(1f, 0.82f, 0.18f, 1f);
        private static readonly Color TargetIndicatorParty = new(0.2f, 0.75f, 0.3f, 1f);

        public NpcEntity(NpcInstance instance, NpcPhysics? physics, NpcState? state, UnityEngine.Object prefabAsset)
        {
            Identity = instance.Identity;
            _instance = instance;
            _state = state;

            GameObject = InstantiateRoot(prefabAsset);
            GameObject.name = $"NPC_{SafeName(instance.DisplayName)}_{instance.Identity}";

            _nameTag = NameTag.Create(GameObject.transform, isLocalPlayer: false);
            _nameTag.SetName(instance.DisplayName);
            _worldHealthBar = WorldHealthBar.Create(GameObject.transform, isLocalPlayer: false);
            _animationController = NpcAnimationController.Attach(GameObject);
            _animationController.SetTemplate(instance.TemplateId);
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
        }

        public void ApplyPhysics(NpcPhysics physics)
        {
            if (IsDestroyed)
                return;

            GameObject.transform.SetPositionAndRotation(
                new Vector3(physics.PosX, physics.PosY, physics.PosZ),
                Quaternion.Euler(0f, physics.Yaw * Mathf.Rad2Deg, 0f));
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

        private static string SafeName(string value)
            => string.IsNullOrWhiteSpace(value)
                ? "Npc"
                : value.Trim().Replace(' ', '_');

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
