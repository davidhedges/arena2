#nullable enable

using System;
using Arena.Network;
using SpacetimeDB.Types;
using UnityEngine;

namespace Arena.Presentation
{
    /// <summary>
    /// Presentation-only idle shadow at the authoritative Lingering Shade
    /// anchor. It reuses the auto-attack ghost's visual-only clone path, while
    /// owning only its distinct lifetime and solid-black material treatment.
    /// </summary>
    public sealed class LingeringShadeGhostLayer : MonoBehaviour
    {
        [SerializeField] [Range(0.05f, 1f)] private float alpha = 0.62f;
        [SerializeField] private Color tint = new(0.008f, 0.005f, 0.012f, 1f);

        private readonly AnimatedAutoAttackGhostLayer.GhostActor _actor = new();
        private Animator? _sourceAnimator;
        private bool _hasAnchor;
        private Vector3 _position;
        private float _facingYaw;
        private long _expiresAtMs;

        public void SetSource(Animator? sourceAnimator)
        {
            _sourceAnimator = sourceAnimator;
            InvalidateVisualClone();
        }

        public void Show(LingeringShadeState row)
        {
            _hasAnchor = true;
            _position = new Vector3(row.PosX, row.PosY, row.PosZ);
            _facingYaw = row.FacingYaw;
            _expiresAtMs = row.ExpiresAt.MicrosecondsSinceUnixEpoch / 1000L;
            ShowCurrentAnchor();
        }

        public void Clear()
        {
            _hasAnchor = false;
            _expiresAtMs = 0L;
            _actor.Hide();
        }

        public void InvalidateVisualClone()
        {
            _actor.Destroy();
            if (_hasAnchor && RemainingMilliseconds() > 0L)
                ShowCurrentAnchor();
        }

        private void LateUpdate()
        {
            if (_hasAnchor && RemainingMilliseconds() <= 0L)
                Clear();
        }

        private void OnEnable()
        {
            if (_hasAnchor && RemainingMilliseconds() > 0L)
                ShowCurrentAnchor();
        }

        private void OnDisable()
        {
            _actor.Destroy();
        }

        private void OnDestroy()
        {
            _hasAnchor = false;
            _actor.Destroy();
        }

        private void ShowCurrentAnchor()
        {
            if (!isActiveAndEnabled
                || _sourceAnimator == null
                || _sourceAnimator.runtimeAnimatorController == null
                || RemainingMilliseconds() <= 0L)
            {
                return;
            }

            Quaternion facing = Quaternion.Euler(0f, _facingYaw * Mathf.Rad2Deg, 0f);
            if (_actor.PrepareCombatIdleFromSource(
                    _sourceAnimator,
                    _position,
                    facing,
                    tint,
                    alpha))
            {
                _actor.PlayCombatIdle();
            }
        }

        private long RemainingMilliseconds()
        {
            long nowMs = ArenaServerClock.HasEstimate
                ? ArenaServerClock.ServerNowMs
                : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return Math.Max(0L, _expiresAtMs - nowMs);
        }
    }
}
