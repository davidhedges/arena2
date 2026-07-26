#nullable enable

using System;

namespace Arena.Simulation
{
    public enum TimedActionPresentationStyle
    {
        CombatCast = 0,
        WorldInteraction = 1,
    }

    public readonly struct TimedActionPresentationSnapshot
    {
        public TimedActionPresentationSnapshot(
            string actionId,
            long startMs,
            long endMs,
            string label,
            TimedActionPresentationStyle style)
        {
            ActionId = actionId ?? string.Empty;
            StartMs = startMs;
            EndMs = endMs;
            Label = label ?? string.Empty;
            Style = style;
        }

        public string ActionId { get; }
        public long StartMs { get; }
        public long EndMs { get; }
        public string Label { get; }
        public TimedActionPresentationStyle Style { get; }
    }

    public interface ITimedActionPresentationSource
    {
        TimedActionPresentationSnapshot? CurrentTimedAction(long nowMs);
    }

    public static class TimedActionPresentation
    {
        /// <summary>
        /// The server forbids overlap. Choosing the newest start makes the HUD
        /// deterministic during the brief callback-order window in which an
        /// old action's delete and a new action's insert cross in flight.
        /// </summary>
        public static TimedActionPresentationSnapshot? Select(
            TimedActionPresentationSnapshot? combat,
            TimedActionPresentationSnapshot? interaction)
        {
            if (!combat.HasValue)
                return interaction;
            if (!interaction.HasValue)
                return combat;

            TimedActionPresentationSnapshot combatValue = combat.Value;
            TimedActionPresentationSnapshot interactionValue = interaction.Value;
            return interactionValue.StartMs > combatValue.StartMs
                ? interactionValue
                : combatValue;
        }

        public static string DisplayLabelFromKey(string labelKey, string fallback)
        {
            string value = string.IsNullOrWhiteSpace(labelKey) ? fallback : labelKey;
            return string.IsNullOrWhiteSpace(value)
                ? "INTERACTING"
                : value.Trim().Replace('_', ' ').ToUpperInvariant();
        }
    }
}
