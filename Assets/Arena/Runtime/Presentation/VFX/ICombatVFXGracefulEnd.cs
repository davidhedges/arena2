#nullable enable

namespace Arena.Presentation.VFX
{
    /// <summary>
    /// Lets a state-bound prefab finish a brief authored exit instead of being
    /// destroyed on the same frame as its authoritative owner row.
    /// </summary>
    public interface ICombatVFXGracefulEnd
    {
        /// <summary>
        /// Returns true when the component accepted responsibility for destroying
        /// its prefab instance after the exit presentation completes.
        /// </summary>
        bool BeginGracefulEnd();
    }
}
