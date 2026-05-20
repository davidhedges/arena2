#nullable enable
using UnityEngine;

namespace Arena.Presentation.VFX
{
    /// <summary>
    /// Interface for all spell visual effects.
    /// Mirrors client/src/spells/spell_vfx.ts.
    /// </summary>
    public interface ISpellVFX
    {
        /// <summary>Returns false when the VFX is complete and should be disposed.</summary>
        bool Tick(float dt);
        void OnUpdate(Vector3 position, Vector3 direction, float speed);
        void OnImpact(Vector3 point);
        void OnFizzle(Vector3 point);
        void Dispose();
    }
}
