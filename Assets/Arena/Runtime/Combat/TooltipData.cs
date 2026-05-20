#nullable enable

namespace Arena.Combat
{
    public readonly struct TooltipData
    {
        public readonly string Name;
        public readonly string Subtitle;
        public readonly string Description;

        public TooltipData(
            string name,
            string subtitle,
            string description = "")
        {
            Name = name?.Trim() ?? string.Empty;
            Subtitle = subtitle?.Trim() ?? string.Empty;
            Description = description?.Trim() ?? string.Empty;
        }

        public bool IsValid => !string.IsNullOrWhiteSpace(Name);
    }
}
