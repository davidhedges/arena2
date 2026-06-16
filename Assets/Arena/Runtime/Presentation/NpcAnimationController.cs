#nullable enable
using UnityEngine;

namespace Arena.Presentation
{
    public sealed class NpcAnimationController : MonoBehaviour
    {
        private const float CrossFadeDuration = 0.06f;
        private const float DefaultDeathHideDelay = 2.8f;
        private const float HitCooldownSeconds = 0.18f;
        private const float DefaultHitReturnDelay = 0.75f;
        private const string IdleStateName = "Idle01";
        private const string KoboldWarriorSwordShield = "KOBOLD_WARRIOR_RD_SWORD_SHIELD";
        private const string KoboldWarriorSpear = "KOBOLD_WARRIOR_GN_SPEAR";
        private const string KoboldThiefDualSword = "KOBOLD_THIEF_BK_DUAL_SWORD";
        private const string KoboldKnightSwordShield = "KOBOLD_KNIGHT_RD_SWORD_SHIELD";
        private static readonly string[] HitStateCandidates =
        {
            "Combat_1H_Hit",
            "Combat_2HL_Hit",
            "Combat_Unarmed_Hit",
            "Combat_Defend_Hit",
            "Spell_Unarmed_Hit",
        };

        private Animator? _animator;
        private string _templateId = string.Empty;
        private float _hideAt = -1f;
        private float _returnToIdleAt = -1f;
        private float _nextHitAllowedAt;
        private bool _dead;

        public static NpcAnimationController Attach(GameObject root)
        {
            var controller = root.GetComponent<NpcAnimationController>();
            if (controller == null)
                controller = root.AddComponent<NpcAnimationController>();
            controller.EnsureAnimator();
            return controller;
        }

        public void SetTemplate(string templateId)
        {
            _templateId = string.IsNullOrWhiteSpace(templateId)
                ? string.Empty
                : templateId.Trim().ToUpperInvariant();
        }

        public void Revive()
        {
            _dead = false;
            _hideAt = -1f;
            _returnToIdleAt = -1f;
            if (!gameObject.activeSelf)
                gameObject.SetActive(true);
        }

        public void PlayHit()
        {
            if (_dead || Time.time < _nextHitAllowedAt)
                return;

            _nextHitAllowedAt = Time.time + HitCooldownSeconds;
            if (TryCrossFade(HitStateCandidates, out string? stateName) && stateName != null)
                _returnToIdleAt = Time.time + ResolveClipLength(stateName, DefaultHitReturnDelay) + 0.05f;
        }

        public void PlayDeath()
        {
            if (_dead)
                return;

            _dead = true;
            _returnToIdleAt = -1f;
            if (!gameObject.activeSelf)
                gameObject.SetActive(true);

            TryCrossFade(new[] { "Death" }, out _);
            _hideAt = Time.time + ResolveClipLength("Death", DefaultDeathHideDelay) + 0.15f;
        }

        private void Update()
        {
            if (!_dead && _returnToIdleAt > 0f && Time.time >= _returnToIdleAt)
            {
                _returnToIdleAt = -1f;
                TryCrossFade(ReadyStateCandidatesForTemplate(), out _);
            }

            if (_hideAt > 0f && Time.time >= _hideAt)
            {
                _hideAt = -1f;
                gameObject.SetActive(false);
            }
        }

        private bool TryCrossFade(string[] stateNames, out string? selectedStateName)
        {
            selectedStateName = null;
            if (!EnsureAnimator())
                return false;

            for (int i = 0; i < stateNames.Length; i++)
            {
                if (TryFindStateHash(stateNames[i], out int layer, out int stateHash))
                {
                    _animator!.CrossFade(stateHash, CrossFadeDuration, layer);
                    selectedStateName = stateNames[i];
                    return true;
                }
            }

            return false;
        }

        private string[] ReadyStateCandidatesForTemplate()
        {
            return _templateId switch
            {
                KoboldWarriorSwordShield => new[] { "Combat_Defend_Ready", "Combat_1H_Ready", "Combat_Unarmed_Ready", IdleStateName },
                KoboldWarriorSpear => new[] { "Combat_2HL_Ready", "Combat_Unarmed_Ready", IdleStateName },
                KoboldThiefDualSword => new[] { "Combat_1H_Ready", "Combat_Unarmed_Ready", IdleStateName },
                KoboldKnightSwordShield => new[] { "Combat_Defend_Ready", "Combat_1H_Ready", "Combat_Unarmed_Ready", IdleStateName },
                _ => new[] { "Combat_Unarmed_Ready", IdleStateName },
            };
        }

        private bool TryFindStateHash(string stateName, out int layer, out int stateHash)
        {
            layer = 0;
            stateHash = 0;
            if (!EnsureAnimator())
                return false;

            int layerCount = Mathf.Max(1, _animator!.layerCount);
            for (int i = 0; i < layerCount; i++)
            {
                string layerName = _animator.GetLayerName(i);
                int fullPathHash = Animator.StringToHash($"{layerName}.{stateName}");
                if (_animator.HasState(i, fullPathHash))
                {
                    layer = i;
                    stateHash = fullPathHash;
                    return true;
                }

                int shortHash = Animator.StringToHash(stateName);
                if (_animator.HasState(i, shortHash))
                {
                    layer = i;
                    stateHash = shortHash;
                    return true;
                }
            }

            return false;
        }

        private float ResolveClipLength(string clipName, float fallback)
        {
            if (!EnsureAnimator() || _animator!.runtimeAnimatorController == null)
                return fallback;

            var clips = _animator.runtimeAnimatorController.animationClips;
            for (int i = 0; i < clips.Length; i++)
            {
                if (string.Equals(clips[i].name, clipName, System.StringComparison.Ordinal))
                    return Mathf.Clamp(clips[i].length, 0.5f, 6f);
            }

            return fallback;
        }

        private bool EnsureAnimator()
        {
            if (_animator != null)
                return true;

            _animator = GetComponentInChildren<Animator>(includeInactive: true);
            return _animator != null;
        }
    }
}
