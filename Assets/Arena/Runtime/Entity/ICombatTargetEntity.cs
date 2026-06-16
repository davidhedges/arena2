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

        Transform GetPresentationRoot();
        Vector3 GetRenderPosition();
        void SetHighlight(bool highlighted);
        void SetSelected(bool selected);
        void RefreshTargetingPresentation();
    }
}
