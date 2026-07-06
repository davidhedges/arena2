#nullable enable

using System;
using System.Reflection;
using NUnit.Framework;

namespace Arena.Tests.Editor
{
    public sealed class CombatClipRoleNameInferenceTests
    {
        private static readonly Assembly EditorAssembly = AppDomain.CurrentDomain.Load("Assembly-CSharp-Editor");
        private static readonly Type RoleInferenceType = EditorAssembly.GetType("Arena.Editor.CombatClipRoleNameInference")
            ?? throw new InvalidOperationException("Missing Arena.Editor.CombatClipRoleNameInference.");
        private static readonly MethodInfo InferFromPathMethod = RoleInferenceType.GetMethod(
                "TryInferFromPath",
                BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("Missing CombatClipRoleNameInference.TryInferFromPath.");

        private const string MagicAttackPath =
            "Assets/Arena/Content/Animation/Extracted/KevinIglesias/Human Animations/Animations/Male/Combat/Spellcasting/MagicAttacks/Call";

        [Test]
        public void KevinIglesiasMagicAttack_UnsuffixedClipInfersHoldEnter()
        {
            string role = InferRoleName(
                $"{MagicAttackPath}/HumanM@MagicAttackCall1H02_L.anim");

            Assert.That(role, Is.EqualTo("SpellCastHoldEnter"));
        }

        [Test]
        public void KevinIglesiasMagicAttack_LoadClipInfersHoldIdle()
        {
            string role = InferRoleName(
                $"{MagicAttackPath}/HumanM@MagicAttackCall1H02_L - Load.anim");

            Assert.That(role, Is.EqualTo("SpellCastHoldIdle"));
        }

        [Test]
        public void KevinIglesiasMagicAttack_CastClipInfersRelease()
        {
            string role = InferRoleName(
                $"{MagicAttackPath}/HumanM@MagicAttackCall1H02_L - Cast.anim");

            Assert.That(role, Is.EqualTo("SpellRelease"));
        }

        private static string InferRoleName(string path)
            => InferFromPathMethod.Invoke(null, new object[] { path })?.ToString()
                ?? throw new InvalidOperationException("Role inference returned null.");
    }
}
