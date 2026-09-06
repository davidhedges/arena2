#nullable enable
using System;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Arena.Editor
{
    internal sealed class BuildBlockingEditModeTestGate : IPreprocessBuildWithReport
    {
        private static readonly string[] RequiredNamespaces = { "Arena.Tests.Editor", "Arena.EditModeTests" };
        public int callbackOrder => 0;

        public void OnPreprocessBuild(BuildReport report)
        {
            try { RunAndThrowOnFailure(); }
            catch (Exception error) { throw new BuildFailedException(error.Message); }
        }

        [MenuItem("Arena/Validate Build-Blocking EditMode Tests")]
        private static void ValidateBuildBlockingEditModeTests() => RunAndThrowOnFailure();
        public static void ValidateBuildBlockingEditModeTestsBatch() => RunAndThrowOnFailure();
        public static void ValidateCharacterAppearanceEditModeTestsBatch() =>
            RunAndThrowOnFailure("Arena.Tests.Editor.CharacterAppearanceCatalogTests");
        public static void ValidateDungeonLabPhaseEContracts() =>
            RunAndThrowOnFailure("Arena.Tests.Editor.DungeonLabPhaseEContractsTests");

        private static void RunAndThrowOnFailure(string? testName = null)
        {
            var result = EditModeVerification.Run(string.IsNullOrWhiteSpace(testName)
                ? RequiredNamespaces : new[] { testName! });
            string[] errors = result.Errors();
            if (errors.Length != 0)
                throw new InvalidOperationException("Build blocked: EditMode verification failed.\n" + string.Join("\n", errors));
            Debug.Log($"[BuildBlockingEditModeTestGate] Passed {result.Passed} named EditMode tests.");
        }
    }
}
