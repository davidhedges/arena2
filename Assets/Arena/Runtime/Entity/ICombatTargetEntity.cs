#nullable enable
using SpacetimeDB;
using UnityEngine;

namespace Arena.Entity
{
    public interface ICombatTargetEntity
    {
        Identity TargetIdentity { get; }
        GameObject TargetGameObject { get; }
        bool IsDestroyed { get; }
        bool IsAlive { get; }
        int Hp { get; }
        int MaxHp { get; }
        float HitRadius { get; }
        float HitHeight { get; }
        string DisplayName { get; }

        /// <summary>
        /// The presentation delay this entity's rendered pose is currently
        /// paying, in ms (S7 budget or interpolation delay). 0 for the local
        /// player. Feeds the S8 attacker-view report on combat presses.
        /// </summary>
        float PresentationEffectiveDelayMs { get; }

        Transform GetPresentationRoot();
        Vector3 GetRenderPosition();
        void SetHighlight(bool highlighted);
        void SetSelected(bool selected);
        void RefreshTargetingPresentation();
    }
}
