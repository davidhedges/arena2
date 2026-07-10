#nullable enable

using System.Collections.Generic;
using UnityEngine;

namespace Arena.Combat
{
    public readonly struct TooltipStat
    {
        public readonly string Label;
        public readonly string Value;
        public readonly bool Positive;

        public TooltipStat(string label, string value, bool positive = true)
        {
            Label = label?.Trim() ?? string.Empty;
            Value = value?.Trim() ?? string.Empty;
            Positive = positive;
        }
    }

    public readonly struct TooltipData
    {
        public readonly string Name;
        public readonly string Subtitle;
        public readonly string Description;
        /// <summary>Optional name tint, e.g. item rarity.</summary>
        public readonly Color? NameColor;
        /// <summary>Optional label/value stat lines shown between subtitle and description.</summary>
        public readonly IReadOnlyList<TooltipStat>? Stats;
        /// <summary>Optional small muted bottom line, e.g. usage hints.</summary>
        public readonly string Footnote;

        public TooltipData(
            string name,
            string subtitle,
            string description = "",
            Color? nameColor = null,
            IReadOnlyList<TooltipStat>? stats = null,
            string footnote = "")
        {
            Name = name?.Trim() ?? string.Empty;
            Subtitle = subtitle?.Trim() ?? string.Empty;
            Description = description?.Trim() ?? string.Empty;
            NameColor = nameColor;
            Stats = stats;
            Footnote = footnote?.Trim() ?? string.Empty;
        }

        public bool IsValid => !string.IsNullOrWhiteSpace(Name);
    }
}
