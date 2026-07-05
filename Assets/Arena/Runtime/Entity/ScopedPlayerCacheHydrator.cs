#nullable enable

using System;
using System.Linq;
using SpacetimeDB;
using SpacetimeDB.Types;

namespace Arena.Entity
{
    internal sealed class ScopedPlayerCacheSnapshot
    {
        public PlayerPhysics[] PlayerPhysicsRows = Array.Empty<PlayerPhysics>();
        public Player[] PlayerRows = Array.Empty<Player>();
        public CharacterAppearance[] CharacterAppearanceRows = Array.Empty<CharacterAppearance>();
        public EquipmentLoadout[] EquipmentLoadoutRows = Array.Empty<EquipmentLoadout>();
        public PlayerEquipmentPresentation[] PlayerEquipmentPresentationRows = Array.Empty<PlayerEquipmentPresentation>();
        public PlayerState[] PlayerStateRows = Array.Empty<PlayerState>();
        public CombatEngagement[] CombatEngagementRows = Array.Empty<CombatEngagement>();
        public PlayerResource[] PlayerResourceRows = Array.Empty<PlayerResource>();
        public DefenseState[] DefenseStateRows = Array.Empty<DefenseState>();
        public StatusEffect[] StatusEffectRows = Array.Empty<StatusEffect>();
        public ActiveCast[] ActiveCastRows = Array.Empty<ActiveCast>();
        public MovementActionState[] MovementActionStateRows = Array.Empty<MovementActionState>();
        public SpecialMovementRuntime[] SpecialMovementRuntimeRows = Array.Empty<SpecialMovementRuntime>();
    }

    internal interface IScopedPlayerCacheSink
    {
        void ApplyUsername(Player row);
        void ApplyCharacterAppearance(CharacterAppearance row);
        void ApplyEquipmentLoadout(EquipmentLoadout row);
        void ApplyPlayerEquipmentPresentation(PlayerEquipmentPresentation row);
        void ApplyState(PlayerState row);
        void ApplyCombatEngagement(CombatEngagement row);
        void ApplyPlayerResource(PlayerResource row);
        void ApplyDefenseState(DefenseState row, bool allowParryTrigger);
        void ApplyStatusEffect(StatusEffect row);
        void RefreshStatusPresentation(Identity target);
        void ApplyActiveCast(ActiveCast row);
        void ApplyMovementActionState(MovementActionState row);
        void ApplySpecialMovementRuntime(SpecialMovementRuntime row);
    }

    internal sealed class ScopedPlayerCacheHydrator
    {
        internal ScopedPlayerCacheSnapshot Capture(DbConnection conn)
        {
            return new ScopedPlayerCacheSnapshot
            {
                PlayerPhysicsRows = conn.Db.PlayerPhysics.Iter().ToArray(),
                PlayerRows = conn.Db.Player.Iter().ToArray(),
                CharacterAppearanceRows = conn.Db.CharacterAppearance.Iter().ToArray(),
                EquipmentLoadoutRows = conn.Db.EquipmentLoadout.Iter().ToArray(),
                PlayerEquipmentPresentationRows = conn.Db.PlayerEquipmentPresentation.Iter().ToArray(),
                PlayerStateRows = conn.Db.PlayerState.Iter().ToArray(),
                CombatEngagementRows = conn.Db.CombatEngagement.Iter().ToArray(),
                PlayerResourceRows = conn.Db.PlayerResource.Iter().ToArray(),
                DefenseStateRows = conn.Db.DefenseState.Iter().ToArray(),
                StatusEffectRows = conn.Db.StatusEffect.Iter().ToArray(),
                ActiveCastRows = conn.Db.ActiveCast.Iter().ToArray(),
                MovementActionStateRows = conn.Db.MovementActionState.Iter().ToArray(),
                SpecialMovementRuntimeRows = conn.Db.SpecialMovementRuntime.Iter().ToArray(),
            };
        }

        internal bool IsIdentityTrackedInScopedCache(ScopedPlayerCacheSnapshot snapshot, Identity identity)
        {
            return snapshot.PlayerPhysicsRows.Any(row => row.Identity == identity)
                || snapshot.PlayerRows.Any(row => row.Identity == identity)
                || snapshot.CharacterAppearanceRows.Any(row => row.Owner == identity)
                || snapshot.PlayerEquipmentPresentationRows.Any(row => row.Owner == identity)
                || snapshot.PlayerStateRows.Any(row => row.PlayerId == identity);
        }

        internal void RehydratePlayersFromScopedCache(
            ScopedPlayerCacheSnapshot snapshot,
            Action clearAllPlayers,
            Action<PlayerPhysics> spawnOrUpdatePlayer)
        {
            clearAllPlayers();

            foreach (var physics in snapshot.PlayerPhysicsRows)
                spawnOrUpdatePlayer(physics);
        }

        internal void ApplyCachedRowsForPlayer(
            ScopedPlayerCacheSnapshot snapshot,
            Identity identity,
            IScopedPlayerCacheSink sink)
        {
            var player = snapshot.PlayerRows.FirstOrDefault(row => row.Identity == identity);
            if (player != null)
                sink.ApplyUsername(player);

            var appearance = snapshot.CharacterAppearanceRows.FirstOrDefault(row => row.Owner == identity);
            if (appearance != null)
                sink.ApplyCharacterAppearance(appearance);

            var equipment = snapshot.EquipmentLoadoutRows.FirstOrDefault(row => row.Owner == identity);
            if (equipment != null)
                sink.ApplyEquipmentLoadout(equipment);

            var equipmentPresentation = snapshot.PlayerEquipmentPresentationRows.FirstOrDefault(row => row.Owner == identity);
            if (equipmentPresentation != null)
                sink.ApplyPlayerEquipmentPresentation(equipmentPresentation);

            var state = snapshot.PlayerStateRows.FirstOrDefault(row => row.PlayerId == identity);
            if (state != null)
                sink.ApplyState(state);

            var engagement = snapshot.CombatEngagementRows.FirstOrDefault(row => row.Owner == identity);
            if (engagement != null)
                sink.ApplyCombatEngagement(engagement);

            foreach (var resource in snapshot.PlayerResourceRows.Where(row => row.Owner == identity))
                sink.ApplyPlayerResource(resource);

            var defense = snapshot.DefenseStateRows.FirstOrDefault(row => row.Owner == identity);
            if (defense != null)
                sink.ApplyDefenseState(defense, true);

            foreach (var effect in snapshot.StatusEffectRows.Where(row => row.Target == identity))
                sink.ApplyStatusEffect(effect);
            sink.RefreshStatusPresentation(identity);

            var cast = snapshot.ActiveCastRows.FirstOrDefault(row => row.Caster == identity);
            if (cast != null)
                sink.ApplyActiveCast(cast);

            var movementAction = snapshot.MovementActionStateRows.FirstOrDefault(row => row.Owner == identity);
            if (movementAction != null)
                sink.ApplyMovementActionState(movementAction);

            var runtime = snapshot.SpecialMovementRuntimeRows.FirstOrDefault(row => row.Owner == identity);
            if (runtime != null)
                sink.ApplySpecialMovementRuntime(runtime);
        }
    }
}
