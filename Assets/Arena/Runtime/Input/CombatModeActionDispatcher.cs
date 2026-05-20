#nullable enable

using System;
using Arena.Combat;
using Arena.Debugging;
using SpacetimeDB;
using SpacetimeDB.Types;

namespace Arena.Input
{
    public static class CombatModeActionDispatcher
    {
        public static bool TryTrigger(DbConnection? conn, ActiveSelectableLoadoutAction action)
        {
            if (conn == null || !conn.Identity.HasValue || !action.IsCombatModeToggleAbility)
                return false;

            Identity owner = conn.Identity.Value;
            string combatProfile = CombatProfileResolver.ResolveForOwner(conn, owner);
            if (string.IsNullOrWhiteSpace(combatProfile))
                return false;

            CombatModeCatalog[] modes = ResolveModesForProfile(conn, combatProfile);
            if (modes.Length == 0)
            {
                LoadoutActionTrace.Trace(
                    $"combat mode toggle rejected ability={action.AbilityId} profile={combatProfile} reason=no_modes");
                return false;
            }

            string currentMode = ResolveCurrentMode(conn, owner, combatProfile, modes);
            string nextMode = ResolveNextMode(currentMode, modes);
            if (string.IsNullOrWhiteSpace(nextMode))
                return false;

            conn.Reducers.SetCombatMode(nextMode);
            LoadoutActionTrace.Trace(
                $"combat mode toggle dispatched ability={action.AbilityId} profile={combatProfile} {currentMode}->{nextMode}");
            return true;
        }

        private static CombatModeCatalog[] ResolveModesForProfile(DbConnection conn, string combatProfile)
        {
            var modes = new System.Collections.Generic.List<CombatModeCatalog>();
            foreach (CombatModeCatalog row in conn.Db.CombatModeCatalog.Iter())
            {
                if (string.Equals(
                        WireIdentifier.Normalize(row.CombatProfileId),
                        combatProfile,
                        StringComparison.Ordinal))
                {
                    modes.Add(row);
                }
            }

            modes.Sort((left, right) =>
            {
                int sort = left.SortOrder.CompareTo(right.SortOrder);
                return sort != 0
                    ? sort
                    : string.Compare(
                        WireIdentifier.Normalize(left.ModeId),
                        WireIdentifier.Normalize(right.ModeId),
                        StringComparison.Ordinal);
            });
            return modes.ToArray();
        }

        private static string ResolveCurrentMode(
            DbConnection conn,
            Identity owner,
            string combatProfile,
            CombatModeCatalog[] modes)
        {
            ActiveCombatMode? active = conn.Db.ActiveCombatMode.Owner.Find(owner);
            if (active != null
                && string.Equals(WireIdentifier.Normalize(active.CombatProfileId), combatProfile, StringComparison.Ordinal)
                && ContainsMode(modes, active.ModeId))
            {
                return WireIdentifier.Normalize(active.ModeId);
            }

            foreach (CombatModeCatalog mode in modes)
            {
                if (mode.IsDefault)
                    return WireIdentifier.Normalize(mode.ModeId);
            }

            return WireIdentifier.Normalize(modes[0].ModeId);
        }

        private static string ResolveNextMode(string currentMode, CombatModeCatalog[] modes)
        {
            if (modes.Length == 0)
                return string.Empty;
            if (modes.Length == 1)
                return WireIdentifier.Normalize(modes[0].ModeId);

            string normalizedCurrent = WireIdentifier.Normalize(currentMode);
            for (int index = 0; index < modes.Length; index++)
            {
                if (!string.Equals(WireIdentifier.Normalize(modes[index].ModeId), normalizedCurrent, StringComparison.Ordinal))
                    continue;

                int nextIndex = (index + 1) % modes.Length;
                return WireIdentifier.Normalize(modes[nextIndex].ModeId);
            }

            return WireIdentifier.Normalize(modes[0].ModeId);
        }

        private static bool ContainsMode(CombatModeCatalog[] modes, string modeId)
        {
            string normalizedMode = WireIdentifier.Normalize(modeId);
            foreach (CombatModeCatalog mode in modes)
            {
                if (string.Equals(WireIdentifier.Normalize(mode.ModeId), normalizedMode, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }
    }
}
