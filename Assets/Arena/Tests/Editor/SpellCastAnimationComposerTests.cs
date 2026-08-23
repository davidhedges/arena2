#nullable enable

using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Arena.Tests.Editor
{
    /// <summary>
    /// Behavioral coverage of the pure cast-animation composer: each archetype stitches the right
    /// clips into a WeaponSpellAnimationEntry (design doc §3). Runtime types live in Assembly-CSharp,
    /// which this editor test assembly can't reference statically, so it drives the composer via
    /// reflection (same pattern as SpellAnimationResolverTests / SpellVfxGeneratorTests).
    /// </summary>
    public sealed class SpellCastAnimationComposerTests
    {
        private static readonly Assembly Rt = AppDomain.CurrentDomain.Load("Assembly-CSharp");

        private static Type T(string name) => Rt.GetType($"Arena.Presentation.{name}", throwOnError: true)!;

        private static object Triple(AnimationClip? oneShot, AnimationClip? load, AnimationClip? cast)
        {
            Type t = T("SpellCastClipTriple");
            object boxed = Activator.CreateInstance(t)!;
            t.GetField("oneShot")!.SetValue(boxed, oneShot);
            t.GetField("load")!.SetValue(boxed, load);
            t.GetField("cast")!.SetValue(boxed, cast);
            return boxed;
        }

        private static object OneHandFamily(object leftTriple, object? rightTriple = null)
        {
            Type f = T("SpellCastAnimationFamily");
            object boxed = Activator.CreateInstance(f)!;
            f.GetField("baseName")!.SetValue(boxed, "TESTBASE");
            f.GetField("handStyle")!.SetValue(boxed, Enum.Parse(T("SpellCastHandStyle"), "OneHand"));
            f.GetField("left")!.SetValue(boxed, leftTriple);
            f.GetField("right")!.SetValue(boxed, rightTriple ?? Triple(null, null, null));
            f.GetField("twoHand")!.SetValue(boxed, Triple(null, null, null));
            return boxed;
        }

        private static object TwoHandFamily(object twoHandTriple)
        {
            Type f = T("SpellCastAnimationFamily");
            object boxed = Activator.CreateInstance(f)!;
            f.GetField("baseName")!.SetValue(boxed, "TESTBASE2H");
            f.GetField("handStyle")!.SetValue(boxed, Enum.Parse(T("SpellCastHandStyle"), "TwoHand"));
            f.GetField("left")!.SetValue(boxed, Triple(null, null, null));
            f.GetField("right")!.SetValue(boxed, Triple(null, null, null));
            f.GetField("twoHand")!.SetValue(boxed, twoHandTriple);
            return boxed;
        }

        private static (bool ok, object? entry) Compose(object family, string hand, string archetype)
        {
            MethodInfo m = T("SpellCastAnimationComposer").GetMethod("TryCompose")!;
            object?[] args =
            {
                "SPELL_TEST",
                family,
                Enum.Parse(T("SpellCastHand"), hand),
                Enum.Parse(T("SpellAnimationArchetype"), archetype),
                null,
            };
            bool ok = (bool)m.Invoke(null, args)!;
            return (ok, args[4]);
        }

        private static object? Field(object entry, string field) => T("WeaponSpellAnimationEntry").GetField(field)!.GetValue(entry);
        private static object? HoldField(object entry, string field)
        {
            object hold = T("WeaponSpellAnimationEntry").GetField("holdOverride")!.GetValue(entry)!;
            return T("SpellCastHoldProfile").GetField(field)!.GetValue(hold);
        }

        private static AnimationClip Clip(string name) => new AnimationClip { name = name };

        [Test]
        public void Instant_PlaysSnappyCastClip_ReleaseOnly_NoHold()
        {
            AnimationClip one = Clip("one"), load = Clip("load"), cast = Clip("cast");
            (bool ok, object? entry) = Compose(OneHandFamily(Triple(one, load, cast)), "Left", "Instant");

            Assert.That(ok, Is.True);
            Assert.That(Field(entry!, "clip"), Is.SameAs(cast), "instant uses the snappy Cast clip, not the slow one-shot");
            Assert.That(Field(entry!, "presentationMode")!.ToString(), Is.EqualTo("ReleaseOnly"));
            Assert.That(HoldField(entry!, "enter"), Is.Null, "instant has no hold enter");
        }

        [Test]
        public void Instant_NoCastClip_FallsBackToOneShot()
        {
            AnimationClip one = Clip("one");
            (bool ok, object? entry) = Compose(OneHandFamily(Triple(one, null, null)), "Left", "Instant");

            Assert.That(ok, Is.True);
            Assert.That(Field(entry!, "clip"), Is.SameAs(one));
        }

        [Test]
        public void Charged_StitchesOneShotToLoopToCast_HoldThenRelease()
        {
            AnimationClip one = Clip("one"), load = Clip("load"), cast = Clip("cast");
            (bool ok, object? entry) = Compose(OneHandFamily(Triple(one, load, cast)), "Left", "Charged");

            Assert.That(ok, Is.True);
            Assert.That(Field(entry!, "clip"), Is.SameAs(cast), "charged release = the Cast clip");
            Assert.That(HoldField(entry!, "enter"), Is.SameAs(one), "hold enter = the one-shot");
            Assert.That(HoldField(entry!, "idleLoop"), Is.SameAs(load), "hold loop = the Load clip");
            Assert.That(Field(entry!, "presentationMode")!.ToString(), Is.EqualTo("HoldThenRelease"));
        }

        [Test]
        public void Channel_StitchesOneShotToLoop_HoldOnly_NoRelease()
        {
            AnimationClip one = Clip("one"), load = Clip("load"), cast = Clip("cast");
            (bool ok, object? entry) = Compose(OneHandFamily(Triple(one, load, cast)), "Left", "Channel");

            Assert.That(ok, Is.True);
            Assert.That(Field(entry!, "clip"), Is.Null, "channel suppresses the release clip");
            Assert.That(HoldField(entry!, "enter"), Is.SameAs(one));
            Assert.That(HoldField(entry!, "idleLoop"), Is.SameAs(load));
            Assert.That(Field(entry!, "presentationMode")!.ToString(), Is.EqualTo("HoldOnly"));
        }

        [Test]
        public void TwoHandFamily_IgnoresHand_UsesBothHandsTriple()
        {
            AnimationClip one = Clip("2hone"), load = Clip("2hload"), cast = Clip("2hcast");
            // Request Left, but a TwoHand family must return its both-hands clips regardless.
            (bool ok, object? entry) = Compose(TwoHandFamily(Triple(one, load, cast)), "Left", "Instant");

            Assert.That(ok, Is.True);
            Assert.That(Field(entry!, "clip"), Is.SameAs(cast), "instant plays the Cast clip from the two-hand triple");
        }

        [Test]
        public void OneHandRight_UsesRightClipsAndRightGestureLayer()
        {
            AnimationClip one = Clip("right-one"), load = Clip("right-load"), cast = Clip("right-cast");
            (bool ok, object? entry) = Compose(
                OneHandFamily(Triple(null, null, null), Triple(one, load, cast)),
                "Right",
                "Instant");

            Assert.That(ok, Is.True);
            Assert.That(Field(entry!, "clip"), Is.SameAs(cast));
            Assert.That(Field(entry!, "playbackLayer")!.ToString(), Is.EqualTo("RightGesture"));
            Assert.That(Field(entry!, "castOrigin")!.ToString(), Is.EqualTo("RightHand"));
        }

        [Test]
        public void OneHandLeft_AuthorsLeftCastOrigin()
        {
            AnimationClip cast = Clip("left-cast");
            (bool ok, object? entry) = Compose(
                OneHandFamily(Triple(null, null, cast)),
                "Left",
                "Instant");

            Assert.That(ok, Is.True);
            Assert.That(Field(entry!, "castOrigin")!.ToString(), Is.EqualTo("LeftHand"));
        }

        [Test]
        public void OneHandRight_ChannelUsesLoopCapableRightGestureHold()
        {
            AnimationClip one = Clip("right-one"), load = Clip("right-load"), cast = Clip("right-cast");
            (bool ok, object? entry) = Compose(
                OneHandFamily(Triple(null, null, null), Triple(one, load, cast)),
                "Right",
                "Channel");

            Assert.That(ok, Is.True);
            Assert.That(HoldField(entry!, "enter"), Is.SameAs(one));
            Assert.That(HoldField(entry!, "idleLoop"), Is.SameAs(load));
            Assert.That(HoldField(entry!, "playbackLayer")!.ToString(), Is.EqualTo("RightGesture"));
        }

        [Test]
        public void Charged_MissingCastClip_ReturnsFalse()
        {
            // A base with no "- Cast" clip can't be a charged release.
            (bool ok, _) = Compose(OneHandFamily(Triple(Clip("one"), Clip("load"), null)), "Left", "Charged");
            Assert.That(ok, Is.False);
        }
    }
}
