#nullable enable
using SpacetimeDB;
using UnityEngine;

namespace Arena.Presentation
{
    internal readonly struct CombatVFXTemplateContext
    {
        public readonly string CueKey;
        public readonly string ActionInstanceId;
        public readonly string ActionKind;
        public readonly string AbilityId;
        public readonly string Trigger;
        public readonly Identity Caster;
        public readonly Identity Hit;
        public readonly Vector3 Origin;
        public readonly Vector3 Direction;
        public readonly Vector3 Point;
        public readonly float Speed;
        public readonly float MaxDistance;
        public readonly string ScalarKind;
        public readonly float ScalarValue;
        public readonly uint SequenceIndex;
        public readonly uint SequenceCount;
        public readonly Transform? FollowAnchor;

        public CombatVFXTemplateContext(
            string cueKey,
            string actionInstanceId,
            string actionKind,
            string abilityId,
            string trigger,
            Identity caster,
            Identity hit,
            Vector3 origin,
            Vector3 direction,
            Vector3 point,
            float speed,
            float maxDistance,
            string scalarKind,
            float scalarValue,
            uint sequenceIndex,
            uint sequenceCount,
            Transform? followAnchor = null)
        {
            CueKey = cueKey;
            ActionInstanceId = actionInstanceId;
            ActionKind = actionKind;
            AbilityId = abilityId;
            Trigger = trigger;
            Caster = caster;
            Hit = hit;
            Origin = origin;
            Direction = direction;
            Point = point;
            Speed = speed;
            MaxDistance = maxDistance;
            ScalarKind = scalarKind;
            ScalarValue = scalarValue;
            SequenceIndex = sequenceIndex;
            SequenceCount = sequenceCount;
            FollowAnchor = followAnchor;
        }
    }
}
