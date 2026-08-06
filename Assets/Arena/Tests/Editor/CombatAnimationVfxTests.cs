#nullable enable
using System;
using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Arena.Tests.Editor
{
    public sealed class CombatAnimationVfxTests
    {
        [Test]
        public void AnimationVfxTrack_IsSharedByClipAndSemanticSlot()
        {
            Type setType = RequireRuntimeType("Arena.Presentation.CombatAnimationSet");
            Type trackType = RequireRuntimeType("Arena.Presentation.CombatAnimationVfxTrack");
            Type attackType = RequireRuntimeType("Arena.Presentation.WeaponMeleeAttackAuthoring");
            Type bindingType = RequireRuntimeType("Arena.Presentation.CombatAnimationVfxBinding");

            ScriptableObject set = ScriptableObject.CreateInstance(setType);
            var clip = new AnimationClip { name = "SharedSlash" };
            object track = Activator.CreateInstance(trackType)!;
            trackType.GetField("clip")!.SetValue(track, clip);
            trackType.GetField("slotId")!.SetValue(track, " slash_primary ");
            trackType.GetField("startTimeSeconds")!.SetValue(track, 0.2f);
            ((IList)setType.GetField("animationVfxTracks")!.GetValue(set)!).Add(track);

            IList attacks = (IList)setType.GetField("meleeAttacks")!.GetValue(set)!;
            attacks.Clear();
            attacks.Add(BuildAttack(attackType, bindingType, clip, "VFX_SLASH_A"));
            attacks.Add(BuildAttack(attackType, bindingType, clip, null));

            MethodInfo tryGetTrack = setType.GetMethod("TryGetAnimationVfxTrack")!;
            object?[] arguments = { clip, "SLASH_PRIMARY", null };
            Assert.That((bool)tryGetTrack.Invoke(set, arguments)!, Is.True);
            Assert.That(arguments[2], Is.SameAs(track));

            MethodInfo getBindings = setType.GetMethod("GetStrikeAnimationVfxBindings")!;
            Assert.That(((ICollection)getBindings.Invoke(set, new object[] { 1 })!).Count, Is.EqualTo(1));
            Assert.That(((ICollection)getBindings.Invoke(set, new object[] { 2 })!).Count, Is.Zero);

            UnityEngine.Object.DestroyImmediate(clip);
            UnityEngine.Object.DestroyImmediate(set);
        }

        [Test]
        public void CombatAnimationRequest_CopiesExplicitRuntimeBindings()
        {
            Type bindingType = RequireRuntimeType("Arena.Presentation.CombatAnimationVfxBinding");
            Type requestType = RequireRuntimeType("Arena.Presentation.CombatAnimationRequest");
            Array source = Array.CreateInstance(bindingType, 1);
            source.SetValue(Activator.CreateInstance(
                bindingType,
                "slash_primary",
                "vfx_runtime_slash"), 0);

            MethodInfo factory = RequirePredictedMeleeFactory(requestType);
            object request = factory.Invoke(
                null,
                new object?[] { "ATTACK_A", 100L, "PLAYER_INPUT", null, null, false, source })!;
            source.SetValue(Activator.CreateInstance(bindingType, "CHANGED", "CHANGED"), 0);

            Array copied = (Array)requestType.GetField("AnimationVfxBindings")!.GetValue(request)!;
            Assert.That(copied, Has.Length.EqualTo(1));
            object copiedBinding = copied.GetValue(0)!;
            Assert.That(
                bindingType.GetProperty("NormalizedSlotId")!.GetValue(copiedBinding),
                Is.EqualTo("SLASH_PRIMARY"));
            Assert.That(
                bindingType.GetProperty("NormalizedVfxId")!.GetValue(copiedBinding),
                Is.EqualTo("VFX_RUNTIME_SLASH"));

            Array empty = Array.CreateInstance(bindingType, 0);
            object disabled = factory.Invoke(
                null,
                new object?[] { "ATTACK_A", 100L, "PLAYER_INPUT", null, null, false, empty })!;
            Array disabledBindings =
                (Array)requestType.GetField("AnimationVfxBindings")!.GetValue(disabled)!;
            Assert.That(disabledBindings, Is.Empty);
        }

        private static object BuildAttack(
            Type attackType,
            Type bindingType,
            AnimationClip clip,
            string? vfxId)
        {
            object attack = Activator.CreateInstance(attackType)!;
            attackType.GetField("clip")!.SetValue(attack, clip);
            Type bindingListType = typeof(System.Collections.Generic.List<>).MakeGenericType(bindingType);
            IList bindings = (IList)Activator.CreateInstance(bindingListType)!;
            if (!string.IsNullOrEmpty(vfxId))
            {
                bindings.Add(Activator.CreateInstance(
                    bindingType,
                    "SLASH_PRIMARY",
                    vfxId));
            }

            attackType.GetField("animationVfxBindings")!.SetValue(attack, bindings);
            return attack;
        }

        private static MethodInfo RequirePredictedMeleeFactory(Type requestType)
        {
            foreach (MethodInfo method in requestType.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (method.Name == "PredictedMeleeSkill"
                    && method.GetParameters().Length == 7)
                {
                    return method;
                }
            }

            throw new MissingMethodException(requestType.FullName, "PredictedMeleeSkill");
        }

        private static Type RequireRuntimeType(string fullName)
        {
            Type? type = Type.GetType($"{fullName}, Assembly-CSharp", throwOnError: false);
            return type ?? throw new TypeLoadException($"Runtime type '{fullName}' was not found.");
        }
    }
}
