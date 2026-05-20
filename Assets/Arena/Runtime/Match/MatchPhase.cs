namespace Arena.Match
{
    /// <summary>
    /// Placeholder until the server-side MatchPhase enum is added to the Rust module
    /// and bindings are regenerated.
    ///
    /// Migration: when SpacetimeDB.Types.MatchPhase appears in the generated bindings,
    ///   1. Delete this file.
    ///   2. In MatchStateCache.cs, change "Arena.Match.MatchPhase" references to
    ///      "SpacetimeDB.Types.MatchPhase" (add the using, remove the local one).
    ///   3. MatchController.cs needs the same using update.
    ///
    /// Variant names must stay in sync with the Rust enum variants.
    /// </summary>
    public enum MatchPhase
    {
        Waiting,
        Countdown,
        InProgress,
        Ended,
    }
}
