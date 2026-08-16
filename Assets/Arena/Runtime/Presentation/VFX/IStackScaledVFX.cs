#nullable enable

namespace Arena.Presentation.VFX
{
    /// <summary>
    /// A scripted status visual that can absorb a replicated stack-count change in
    /// place. Without this the dispatcher can only rebuild the visual, which restarts
    /// the whole effect on every stack tick; implement it when a stack change should
    /// read as the effect growing rather than as a discrete event.
    /// </summary>
    internal interface IStackScaledVFX
    {
        /// <summary>
        /// Retunes the live visual to <paramref name="stacks"/>. Returns false when the
        /// change cannot be absorbed, which sends the caller back to a full rebuild.
        /// </summary>
        bool TrySetStackCount(uint stacks);
    }
}
