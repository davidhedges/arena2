#nullable enable

using Michsky.UI.Heat;
using TMPro;
using UnityEngine;

namespace Arena.UI
{
    /// <summary>
    /// Single source of truth for menu/interface styling. The canonical palette
    /// lives here (versioned, first-party) and is stamped into the Heat UI
    /// Manager asset at startup so baked Heat scenes and procedural windows share
    /// one palette; fonts come from the Heat asset. Surface tiers derive from the
    /// background color so a re-skin (edit the Canon* values) re-tints everything.
    /// </summary>
    internal static class ArenaUiTheme
    {
        private static UIManager? s_manager;
        private static bool s_managerResolved;

        private static UIManager? Manager
        {
            get
            {
                if (!s_managerResolved)
                {
                    s_manager = Resources.Load<UIManager>("Heat UI Manager");
                    s_managerResolved = true;
                }

                return s_manager;
            }
        }

        // Canonical arena palette. The Heat package directory is git-ignored, so
        // the vendor asset cannot be the durable source of truth; these values
        // are stamped into the loaded UIManager at startup and double as
        // fallbacks if the asset fails to load. Re-skin by editing these.
        private static readonly Color CanonAccent = new(0.92f, 0.72f, 0.25f, 1f);
        private static readonly Color CanonOnAccent = new(0.08f, 0.065f, 0.04f, 1f);
        private static readonly Color CanonText = new(0.94f, 0.95f, 0.97f, 1f);
        private static readonly Color CanonDanger = new(0.90f, 0.25f, 0.20f, 1f);
        private static readonly Color CanonBackground = new(0.055f, 0.058f, 0.068f, 1f);
        private static readonly Color CanonRarityRare = new(0.42f, 0.62f, 0.96f, 1f);
        private static readonly Color CanonRarityLegendary = new(1f, 0.55f, 0.20f, 1f);

        /// <summary>
        /// Writes the arena palette into the loaded Heat UIManager instance so
        /// every Heat consumer component (baked Hub/CharacterCreation UI) renders
        /// the arena theme even if the git-ignored vendor asset reverts to stock
        /// values on a package re-import.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void StampVendorTheme()
        {
            UIManager? manager = Manager;
            if (manager == null)
                return;

            manager.accentColor = CanonAccent;
            manager.accentColorInvert = CanonOnAccent;
            manager.primaryColor = CanonText;
            manager.secondaryColor = CanonBackground;
            manager.negativeColor = CanonDanger;
            manager.backgroundColor = CanonBackground;
            manager.commonColor = Color.white;
            manager.rareColor = CanonRarityRare;
            manager.legendaryColor = CanonRarityLegendary;
        }

        public static Color Accent => Opaque(Manager != null ? Manager.accentColor : CanonAccent);
        public static Color OnAccent => Opaque(Manager != null ? Manager.accentColorInvert : CanonOnAccent);
        public static Color Text => Opaque(Manager != null ? Manager.primaryColor : CanonText);
        // Heat's secondaryColor is a dark surface tone, not a muted-text color,
        // so muted text derives from the primary text color instead.
        public static Color MutedText => Opaque(Color.Lerp(Text, Background, 0.34f));
        public static Color Danger => Opaque(Manager != null ? Manager.negativeColor : CanonDanger);
        public static Color Background => Opaque(Manager != null ? Manager.backgroundColor : CanonBackground);

        public static Color AccentHot => Lift(Accent, 0.18f);
        public static Color AccentDim => Fade(Accent, 0.55f);
        public static Color Gold => Accent;
        public static Color Success => new(0.56f, 0.86f, 0.62f, 1f);

        // Surface tiers, darkest to brightest. Derived from Background so a
        // re-skin of the Heat asset re-tints every window automatically.
        public static Color Veil => Fade(Sink(Background, 0.55f), 0.72f);
        public static Color CellEmpty => Fade(Sink(Background, 0.45f), 0.94f);
        public static Color Showcase => Fade(Sink(Background, 0.40f), 0.78f);
        public static Color Panel => Fade(Sink(Background, 0.18f), 0.97f);
        public static Color PanelStrong => Fade(Sink(Background, 0.06f), 0.985f);
        public static Color Header => Fade(Lift(Background, 0.045f), 0.99f);
        public static Color Row => Fade(Lift(Background, 0.075f), 0.97f);
        public static Color RowAlt => Fade(Lift(Background, 0.115f), 0.97f);
        public static Color CellFilled => Fade(Lift(Background, 0.16f), 0.98f);
        public static Color Hairline => Fade(Color.white, 0.13f);
        public static Color HairlineStrong => Fade(Color.white, 0.24f);

        /// <summary>Tinted row for "owned/learned" states.</summary>
        public static Color PositiveRow => Fade(Color.Lerp(Lift(Background, 0.075f), Success, 0.16f), 0.97f);

        // Item/spell rarity ramp. Common/rare/legendary honor the Heat asset's
        // achievement rarity colors; the rest are theme extensions.
        public static Color RarityCommon => Opaque(Manager != null ? Manager.commonColor : Color.white);
        public static Color RarityUncommon => new(0.44f, 0.82f, 0.42f, 1f);
        public static Color RarityRare => Opaque(Manager != null ? Manager.rareColor : CanonRarityRare);
        public static Color RarityEpic => new(0.72f, 0.46f, 0.92f, 1f);
        public static Color RarityLegendary => Opaque(Manager != null ? Manager.legendaryColor : CanonRarityLegendary);

        /// <summary>Resolves a wire rarity id ("RARE", "epic", …) to its display color.</summary>
        public static Color RarityColor(string? rarity)
        {
            string normalized = string.IsNullOrWhiteSpace(rarity)
                ? string.Empty
                : rarity.Trim().Replace('-', '_').ToUpperInvariant();
            return normalized switch
            {
                "UNCOMMON" => RarityUncommon,
                "RARE" => RarityRare,
                "EPIC" => RarityEpic,
                "LEGENDARY" => RarityLegendary,
                "COMMON" => RarityCommon,
                _ => Text,
            };
        }

        public static TMP_FontAsset? TitleFont => Manager != null && Manager.fontBold != null ? Manager.fontBold : LegacySdfFont();
        public static TMP_FontAsset? StrongFont => Manager != null && Manager.fontSemiBold != null ? Manager.fontSemiBold : LegacySdfFont();
        public static TMP_FontAsset? BodyFont => Manager != null && Manager.fontMedium != null ? Manager.fontMedium : LegacySdfFont();

        // Type scale + shared metrics (canvas-space units at 1920x1080 reference).
        public const float WindowTitleSize = 19f;
        public const float SectionSize = 14f;
        public const float BodySize = 13f;
        public const float SmallSize = 11.5f;
        public const float HeaderHeight = 44f;
        public const float FooterHeight = 52f;
        public const float ButtonHeight = 34f;
        public const float ContentPadding = 14f;
        public const float AccentBarThickness = 2f;

        private static TMP_FontAsset? LegacySdfFont()
            => Resources.Load<TMP_FontAsset>("Fonts/SDF/RobotoCondensed-Bold SDF");

        private static Color Lift(Color color, float amount)
            => Preserve(Color.Lerp(color, Color.white, amount), color.a);

        private static Color Sink(Color color, float amount)
            => Preserve(Color.Lerp(color, Color.black, amount), color.a);

        private static Color Fade(Color color, float alpha)
            => new(color.r, color.g, color.b, alpha);

        private static Color Opaque(Color color)
            => new(color.r, color.g, color.b, 1f);

        private static Color Preserve(Color color, float alpha)
            => new(color.r, color.g, color.b, alpha);
    }
}
