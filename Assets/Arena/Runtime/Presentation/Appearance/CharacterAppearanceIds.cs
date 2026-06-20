#nullable enable
using System;

namespace Arena.Presentation.Appearance
{
    public static class CharacterAppearanceIds
    {
        public const string RaceHuman = "HUMAN";
        public const string RaceOrc = "ORC";
        public const string RaceDwarf = "DWARF";

        public const string SexMale = "MALE";
        public const string SexFemale = "FEMALE";

        public const string DefaultRaceId = RaceHuman;
        public const string DefaultSexId = SexMale;
        public const string DefaultBodyId = "HUMAN_MALE_BODY_01";
        public const string DefaultHeadId = "HUMAN_MALE_HEAD_01_A";
        public const string DefaultEyesId = "HUMAN_EYES_BLUE";
        public const string DefaultHairId = "";
        public const string DefaultFaceId = "";
        public const string DefaultOutfitId = "HUMAN_MALE_PEASANT_STARTER";

        public static string Normalize(string? value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : value.Trim().ToUpperInvariant();
        }

        public static bool IsDefaultSupportedRaceSex(string? raceId, string? sexId)
        {
            return string.Equals(Normalize(raceId), RaceHuman, StringComparison.Ordinal)
                && string.Equals(Normalize(sexId), SexMale, StringComparison.Ordinal);
        }
    }
}
