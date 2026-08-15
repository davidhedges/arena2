#nullable enable

using System;
using System.Collections.Generic;
using System.Text;
using SpacetimeDB.Types;

namespace Arena.Combat
{
    public static class StatusTooltipResolver
    {
        public static TooltipData Resolve(
            DbConnection? conn,
            StatusEffect status,
            bool isRimed = false)
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

            string description = !string.IsNullOrWhiteSpace(presentation?.Description)
                ? presentation.Description
                : ResolveFallbackDescription(status);
            if (string.Equals(
                    WireIdentifier.Normalize(status.EffectKind),
                    "TEMPORARY_HITPOINTS",
                    StringComparison.Ordinal)
                && status.AbsorbCap > 0)
            {
                string remaining = $"{Math.Max(status.AbsorbAmount, 0)} of {status.AbsorbCap} absorb remaining.";
                description = string.IsNullOrWhiteSpace(description)
                    ? remaining
                    : $"{description} {remaining}";
            }
            if (isRimed)
            {
                const string protection = "Rimed: cannot be removed by abilities; expires naturally.";
                description = string.IsNullOrWhiteSpace(description)
                    ? protection
                    : $"{description} {protection}";
            }

            return new TooltipData(
                name,
                FormatSubtitle(status, isRimed),
                description);
        }

        private static string FormatSubtitle(StatusEffect status, bool isRimed)
        {
            List<string> parts = new();
            parts.Add(FormatPolarity(status.Polarity));

            if (isRimed)
                parts.Add("Rimed");

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
                "CONFUSION" => "Prevents actions and control of movement. The target wanders nearby until damaged.",
                "STAGGER" => "Interrupts actions and briefly shoves the target.",
                "KNOCKDOWN" => "Knocks the target down and prevents actions.",
                "SLOW" => $"Reduces movement speed by {FormatPercent(status.SlowPct * stacks)}.",
                "DOT" => FormatPeriodicDescription("Deals", status.TickAmount, stacks, status.TickIntervalMs),
                "HOT" => FormatPeriodicDescription("Restores", status.TickAmount, stacks, status.TickIntervalMs),
                "MOVE_SLOW_IMMUNITY" => "Prevents movement slows from reducing speed.",
                "MOVEMENT_IMPAIRING_IMMUNITY" => "Prevents slows, roots, and knockbacks.",
                "STUN_IMMUNITY" => "Prevents stuns.",
                "SILENCE" => "Prevents spell casting.",
                "KNOCKBACK_RESISTANCE" => $"Reduces knockback distance by {FormatPercent(status.ModifierScalar * stacks)}.",
                "DAMAGE_AMP" => $"Increases damage dealt by {FormatPercent(status.ModifierScalar)}.",
                "DIRECT_DAMAGE_AMP" => $"Increases direct damage dealt by {FormatPercent(status.ModifierScalar * stacks)}.",
                "DAMAGE_TAKEN_REDUCTION" => $"Reduces incoming damage by {FormatPercent(status.ModifierScalar * stacks)}.",
                "TEMPORARY_HITPOINTS" => $"Absorbs up to {Math.Max(status.AbsorbCap, 0)} incoming damage.",
                "HEALING_TAKEN_REDUCTION" => $"Reduces healing received by {FormatPercent(status.ModifierScalar * stacks)}.",
                "DAMAGE_DEALT_REDUCTION" => $"Reduces damage dealt by {FormatPercent(status.ModifierScalar * stacks)}.",
                "MELEE_ATTACK_MODIFIER" => "Modifies the next melee attack.",
                "TARGETED_ABILITY_AVOIDANCE" => "Causes hostile targeted abilities to miss.",
                "MIRROR_IMAGE" => "Each image independently has a 25% chance to intercept a single-target attack. Damaging area attacks destroy every image without preventing damage.",
                "GIGANTISM" => "Increases size to 150%, physical damage by 20%, and non-gap-closer melee attack range by 1.5 meters.",
                "FLURRY" => "Auto attacks can trigger additional ghostly attacks. The flat chance is 15% at a 3.5-second cadence, decreases for faster attacks, and can chain.",
                "VERDANT_SPIRITS" => "Holds one or two nature spirits bestowed by their living origin. Restores 1 health per spirit each second.",
                "ATTACK_SPEED" => $"Modifies attack speed by {FormatSignedPercent(status.ModifierScalar)}.",
                "CAST_SPEED" => $"Increases cast speed by {FormatPercent(status.ModifierScalar)}.",
                "RECKONING" => "Retaliates when the mark expires based on damage its caster takes.",
                "DAMAGE_REDIRECT" => $"Redirects {FormatPercent(status.ModifierScalar)} of incoming damage to the caster.",
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
