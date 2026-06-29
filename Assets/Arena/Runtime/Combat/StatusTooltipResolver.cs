#nullable enable

using System;
using System.Collections.Generic;
using System.Text;
using SpacetimeDB.Types;

namespace Arena.Combat
{
    public static class StatusTooltipResolver
    {
        public static TooltipData Resolve(DbConnection? conn, StatusEffect status)
        {
            ActionPresentationCatalog? presentation =
                ActionPresentation.FindPresentation(
                    conn,
                    ActionTooltipResolver.PresentationKindStatus,
                    status.StackGroup)
                ?? ActionPresentation.FindPresentation(
                    conn,
                    ActionTooltipResolver.PresentationKindStatus,
                    BaseStackGroup(status.StackGroup))
                ?? ActionPresentation.FindPresentation(
                    conn,
                    ActionTooltipResolver.PresentationKindStatus,
                    status.EffectKind);

            string name = !string.IsNullOrWhiteSpace(presentation?.DisplayName)
                ? presentation.DisplayName
                : TitleCaseStatusKind(status.EffectKind);

            return new TooltipData(
                name,
                FormatSubtitle(status),
                !string.IsNullOrWhiteSpace(presentation?.Description)
                    ? presentation.Description
                    : ResolveFallbackDescription(status));
        }

        private static string FormatSubtitle(StatusEffect status)
        {
            List<string> parts = new();
            parts.Add(FormatPolarity(status.Polarity));

            if (status.Stacks > 1)
                parts.Add($"{status.Stacks} stacks");

            string remaining = FormatRemainingDuration(status.ExpiresAtMicros);
            if (!string.IsNullOrWhiteSpace(remaining))
                parts.Add(remaining);

            return string.Join(" \u00b7 ", parts);
        }

        private static string FormatPolarity(string polarity)
        {
            string normalized = WireIdentifier.Normalize(polarity);
            return normalized switch
            {
                "BUFF" => "Buff",
                "DEBUFF" => "Debuff",
                _ => TitleCaseStatusKind(polarity),
            };
        }

        private static string BaseStackGroup(string stackGroup)
        {
            string normalized = WireIdentifier.Normalize(stackGroup);
            int suffixIndex = normalized.IndexOf(':');
            return suffixIndex > 0 ? normalized.Substring(0, suffixIndex) : string.Empty;
        }

        private static string FormatRemainingDuration(long expiresAtMicros)
        {
            if (expiresAtMicros <= 0)
                return string.Empty;

            long nowMicros = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;
            float seconds = Math.Max(0f, (expiresAtMicros - nowMicros) / 1_000_000f);
            if (seconds >= 1f)
                return $"{Math.Ceiling(seconds):0}s";
            if (seconds > 0.05f)
                return $"{seconds:0.0}s";
            return "0s";
        }

        private static string ResolveFallbackDescription(StatusEffect status)
        {
            string kind = WireIdentifier.Normalize(status.EffectKind);
            uint stacks = Math.Max(status.Stacks, 1u);
            return kind switch
            {
                "ROOT" => "Prevents movement.",
                "STUN" => "Prevents movement and actions.",
                "FREEZE" => "Prevents movement and actions.",
                "INTIMIDATED" => "Prevents movement and actions.",
                "FEAR" => "Prevents movement and actions.",
                "STAGGER" => "Interrupts actions and briefly shoves the target.",
                "KNOCKDOWN" => "Knocks the target down and prevents actions.",
                "SLOW" => $"Reduces movement speed by {FormatPercent(status.SlowPct * stacks)}.",
                "DOT" => FormatPeriodicDescription("Deals", status.TickAmount, stacks, status.TickIntervalMs),
                "HOT" => FormatPeriodicDescription("Restores", status.TickAmount, stacks, status.TickIntervalMs),
                "MOVE_SLOW_IMMUNITY" => "Prevents movement slows from reducing speed.",
                "DAMAGE_AMP" => $"Increases damage dealt by {FormatPercent(status.ModifierScalar)}.",
                "DIRECT_DAMAGE_AMP" => $"Increases direct damage dealt by {FormatPercent(status.ModifierScalar * stacks)}.",
                "DAMAGE_TAKEN_REDUCTION" => $"Reduces incoming damage by {FormatPercent(status.ModifierScalar * stacks)}.",
                "HEALING_TAKEN_REDUCTION" => $"Reduces healing received by {FormatPercent(status.ModifierScalar * stacks)}.",
                "MELEE_ATTACK_MODIFIER" => "Modifies the next melee attack.",
                "TARGETED_ABILITY_AVOIDANCE" => "Causes hostile targeted abilities to miss.",
                "ATTACK_SPEED" => $"Modifies attack speed by {FormatSignedPercent(status.ModifierScalar)}.",
                "CAST_SPEED" => $"Increases cast speed by {FormatPercent(status.ModifierScalar)}.",
                _ => string.Empty,
            };
        }

        private static string FormatPeriodicDescription(
            string verb,
            int tickAmount,
            uint stacks,
            ulong tickIntervalMs)
        {
            int amount = Math.Max(0, tickAmount) * (int)Math.Max(stacks, 1u);
            float seconds = Math.Max(0.001f, tickIntervalMs / 1000f);
            return $"{verb} {amount} every {seconds:0.#}s.";
        }

        private static string FormatPercent(float value)
        {
            float percent = Math.Min(Math.Max(value, 0f), 99f) * 100f;
            return $"{percent:0.#}%";
        }

        private static string FormatSignedPercent(float value)
        {
            float percent = value * 100f;
            return percent >= 0f ? $"+{percent:0.#}%" : $"{percent:0.#}%";
        }

        private static string TitleCaseStatusKind(string value)
        {
            string normalized = WireIdentifier.Normalize(value);
            if (string.IsNullOrWhiteSpace(normalized))
                return string.Empty;

            string[] words = normalized.Split('_', StringSplitOptions.RemoveEmptyEntries);
            StringBuilder builder = new();
            for (int i = 0; i < words.Length; i++)
            {
                if (i > 0)
                    builder.Append(' ');

                string word = words[i].ToLowerInvariant();
                builder.Append(char.ToUpperInvariant(word[0]));
                if (word.Length > 1)
                    builder.Append(word.Substring(1));
            }

            return builder.ToString();
        }
    }
}
