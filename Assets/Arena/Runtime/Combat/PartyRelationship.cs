#nullable enable

using System;
using SpacetimeDB;
using SpacetimeDB.Types;
using Arena.Entity;
using Arena.Match;
using Arena.Network;

namespace Arena.Combat
{
    public enum ClientCombatRelation
    {
        Self,
        PartyAlly,
        Neutral,
        Hostile,
    }

    public static class PartyRelationship
    {
        public const string TargetAudienceSelfOnly = "SELF_ONLY";
        public const string TargetAudienceHostile = "HOSTILE";
        public const string TargetAudiencePartyOrSelf = "PARTY_OR_SELF";
        public const string TargetAudienceAssistable = "ASSISTABLE";
        public const string TargetAudienceAny = "ANY";
        public const string PlaygroundKindHostile = "HOSTILE";
        public const string PlaygroundKindNeutral = "NEUTRAL";
        public const string PlaygroundKindPartyMember = "PARTY_MEMBER";

        public static ClientCombatRelation RelationToLocal(ICombatTargetEntity? target)
        {
            var local = EntityRegistry.Instance?.LocalPlayerEntity;
            if (local == null || target == null)
                return ClientCombatRelation.Neutral;

            bool targetIsDummy = target is PlayerEntity player && player.IsDummy;
            return Relation(local.Identity, target.TargetIdentity, targetIsDummy);
        }

        public static ClientCombatRelation RelationToLocal(PlayerEntity? target)
        {
            var local = EntityRegistry.Instance?.LocalPlayerEntity;
            if (local == null || target == null)
                return ClientCombatRelation.Neutral;

            return Relation(local.Identity, target.Identity, target.IsDummy);
        }

        public static ClientCombatRelation Relation(Identity source, Identity target, bool targetIsDummy)
        {
            if (source == target)
                return ClientCombatRelation.Self;

            // Roster teams are authoritative inside the bot match. Resolve
            // them before the generic dummy/arena rules so the ally dummy is
            // assistable and only the opposing team is hostile.
            if (TryMatchRelation(source, target, out var matchRelation))
                return matchRelation;

            // Playground-only override for targeting and party-frame testing. This is
            // not a general faction, NPC, or bot relationship system.
            if (TryPlaygroundRelation(source, target, out var playgroundRelation))
                return playgroundRelation;

            if (TryNpcRelation(target, out var npcRelation))
                return npcRelation;

            if (targetIsDummy)
                return ClientCombatRelation.Hostile;

            var match = MatchStateCache.Instance;
            if (match.IsArenaMode && (match.IsCountdown || match.IsInProgress))
                return ClientCombatRelation.Hostile;

            if (ArePartyMembers(source, target))
                return ClientCombatRelation.PartyAlly;

            return ClientCombatRelation.Neutral;
        }

        public static bool IsHostileToLocal(PlayerEntity? target)
            => RelationToLocal(target) == ClientCombatRelation.Hostile;

        public static bool IsHostileToLocal(ICombatTargetEntity? target)
            => RelationToLocal(target) == ClientCombatRelation.Hostile;

        public static bool IsPartyAllyToLocal(PlayerEntity? target)
            => RelationToLocal(target) == ClientCombatRelation.PartyAlly;

        public static bool CanInvite(PlayerEntity? target)
        {
            var local = EntityRegistry.Instance?.LocalPlayerEntity;
            if (local == null || target == null || !target.IsAlive || target.IsDummy)
                return false;
            if (local.Identity == target.Identity)
                return false;
            if (MatchStateCache.Instance.IsArenaMode)
                return false;
            return !ArePartyMembers(local.Identity, target.Identity)
                   && GetPartyId(target.Identity) == null;
        }

        public static bool TargetAudienceAllowsLocal(PlayerEntity? target, string? targetAudience)
        {
            var relation = RelationToLocal(target);
            return TargetAudienceAllowsRelation(relation, targetAudience);
        }

        public static bool TargetAudienceAllowsLocal(ICombatTargetEntity? target, string? targetAudience)
        {
            var relation = RelationToLocal(target);
            return TargetAudienceAllowsRelation(relation, targetAudience);
        }

        public static bool TargetAudienceAllowsSelf(string? targetAudience)
            => TargetAudienceAllowsRelation(ClientCombatRelation.Self, targetAudience);

        private static bool TargetAudienceAllowsRelation(ClientCombatRelation relation, string? targetAudience)
        {
            return NormalizeAudience(targetAudience) switch
            {
                TargetAudienceSelfOnly => relation == ClientCombatRelation.Self,
                TargetAudienceHostile => relation == ClientCombatRelation.Hostile,
                TargetAudiencePartyOrSelf => relation is ClientCombatRelation.Self or ClientCombatRelation.PartyAlly,
                TargetAudienceAssistable => relation is ClientCombatRelation.Self or ClientCombatRelation.PartyAlly or ClientCombatRelation.Neutral,
                TargetAudienceAny => true,
                _ => false,
            };
        }

        public static ulong? LocalPartyId()
        {
            var local = EntityRegistry.Instance?.LocalPlayerEntity;
            return local == null ? null : GetPartyId(local.Identity);
        }

        public static ulong? GetPartyId(Identity identity)
        {
            var conn = NetworkManager.Instance?.Conn;
            return conn?.Db.PartyMember.Member.Find(identity)?.PartyId;
        }

        public static bool ArePartyMembers(Identity a, Identity b)
        {
            ulong? partyA = GetPartyId(a);
            return partyA.HasValue && GetPartyId(b) == partyA.Value;
        }

        public static Party? LocalParty()
        {
            var conn = NetworkManager.Instance?.Conn;
            ulong? partyId = LocalPartyId();
            return conn != null && partyId.HasValue
                ? conn.Db.Party.PartyId.Find(partyId.Value)
                : null;
        }

        private static string NormalizeAudience(string? value)
            => string.IsNullOrWhiteSpace(value)
                ? TargetAudienceHostile
                : value.Trim().ToUpperInvariant();

        private static bool TryPlaygroundRelation(
            Identity source,
            Identity target,
            out ClientCombatRelation relation)
        {
            relation = ClientCombatRelation.Neutral;
            var conn = NetworkManager.Instance?.Conn;
            PlaygroundTarget? playground = conn?.Db.PlaygroundTarget.Identity.Find(target);
            if (playground == null)
                return false;

            string kind = NormalizePlaygroundKind(playground.Kind);
            if (source == playground.Owner)
            {
                relation = kind switch
                {
                    PlaygroundKindHostile => ClientCombatRelation.Hostile,
                    PlaygroundKindPartyMember => ClientCombatRelation.PartyAlly,
                    _ => ClientCombatRelation.Neutral,
                };
                return true;
            }

            relation = kind == PlaygroundKindPartyMember && ArePartyMembers(source, target)
                ? ClientCombatRelation.PartyAlly
                : ClientCombatRelation.Neutral;
            return true;
        }

        private static bool TryMatchRelation(
            Identity source,
            Identity target,
            out ClientCombatRelation relation)
        {
            relation = ClientCombatRelation.Neutral;
            var conn = NetworkManager.Instance?.Conn;
            MatchParticipant? sourceParticipant = conn?.Db.MatchParticipant.Identity.Find(source);
            MatchParticipant? targetParticipant = conn?.Db.MatchParticipant.Identity.Find(target);
            if (sourceParticipant == null
                || targetParticipant == null
                || sourceParticipant.InstanceId != targetParticipant.InstanceId)
            {
                return false;
            }

            relation = sourceParticipant.TeamId == targetParticipant.TeamId
                ? ClientCombatRelation.PartyAlly
                : ClientCombatRelation.Hostile;
            return true;
        }

        private static bool TryNpcRelation(Identity target, out ClientCombatRelation relation)
        {
            relation = ClientCombatRelation.Neutral;
            var conn = NetworkManager.Instance?.Conn;
            NpcInstance? npc = conn?.Db.NpcInstance.Identity.Find(target);
            if (npc == null)
                return false;

            relation = NormalizePlaygroundKind(npc.Faction) switch
            {
                PlaygroundKindHostile => ClientCombatRelation.Hostile,
                PlaygroundKindPartyMember or "FRIENDLY" => ClientCombatRelation.PartyAlly,
                _ => ClientCombatRelation.Neutral,
            };
            return true;
        }

        private static string NormalizePlaygroundKind(string? value)
            => string.IsNullOrWhiteSpace(value)
                ? PlaygroundKindNeutral
                : value.Trim().ToUpperInvariant();
    }
}
