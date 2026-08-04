#nullable enable

using System.Collections.Generic;

namespace Arena.UI
{
    /// <summary>
    /// Pure validation shared by the Disciplines UI and edit-mode tests.
    /// Persistence is intentionally outside this presentation slice.
    /// </summary>
    public static class DisciplineLoadoutRules
    {
        public const int PrimaryAbilityMinimum = 8;
        public const int SecondaryAbilityMinimum = 1;
        public const int SecondaryDisciplineMaximum = 2;
        public const int AbilityPointBudget = 25;

        public static bool CanBePrimary(int availableAbilityCount)
            => availableAbilityCount >= PrimaryAbilityMinimum;

        public static bool CanAddSecondary(int selectedSecondaryCount)
            => selectedSecondaryCount < SecondaryDisciplineMaximum;

        public static bool IsValid(int primaryAbilityCount, IReadOnlyList<int> secondaryAbilityCounts)
        {
            if (primaryAbilityCount < PrimaryAbilityMinimum
                || secondaryAbilityCounts.Count > SecondaryDisciplineMaximum)
            {
                return false;
            }

            for (int i = 0; i < secondaryAbilityCounts.Count; i++)
            {
                if (secondaryAbilityCounts[i] < SecondaryAbilityMinimum)
                    return false;
            }

            return true;
        }

        public static int RemainingPoints(IEnumerable<int> allocations)
        {
            int spent = 0;
            foreach (int allocation in allocations)
                spent += allocation > 0 ? allocation : 0;
            return System.Math.Max(0, AbilityPointBudget - spent);
        }
    }
}
