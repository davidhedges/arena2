#nullable enable

namespace Arena.Presentation
{
    // Presentation-only slot identifiers for shared spell animation entries.
    // These are not gameplay contracts and should stay out of shared combat ids.
    // Follow-up when the first real spellcasting class lands:
    // - move spell-cast presentation to explicit editor-authored entries
    // - keep these ids presentation-local only
    // - avoid reintroducing them into gameplay/shared contract code
    public static class PresentationActionSlotIds
    {
        public const string CastForward = "cast_forward";
        public const string CastUp = "cast_up";
    }
}
