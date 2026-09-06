#nullable enable
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Arena.Editor
{
    /// <summary>Named native regression baseline for the approved compatibility cleanup.</summary>
    public static class CompatibilityCleanupVerification
    {
        [Serializable]
        private sealed class Result
        {
            public string capturedUtc = DateTime.UtcNow.ToString("O");
            public string unityVersion = Application.unityVersion;
            public bool completed;
            public int passed, failed;
            public string[] requested = Array.Empty<string>();
            public string[] executed = Array.Empty<string>();
            public string[] errors = Array.Empty<string>();
        }

        private static readonly string[] Fixtures =
        {
            "Arena.Tests.Editor.EditModeVerificationTests",
            "Arena.Tests.Editor.UiInputContractTests",
            "Arena.Tests.Editor.CombatBuildUnityPlumbingTests",
            "Arena.Tests.Editor.CombatAnimationVisualInterruptTests",
            "Arena.Tests.Editor.CombatVfxCueResolverTests",
            "Arena.Tests.Editor.CombatProjectilePredictionTests",
            "Arena.Tests.Editor.ProjectileVfxPoolingTests",
            "Arena.Tests.Editor.SpellAnimationResolverTests",
            "Arena.Tests.Editor.DungeonLabDeterminismTests",
            "Arena.Tests.Editor.DungeonLabRouteTopologyLoaderTests",
            "Arena.Tests.Editor.DungeonLabRecipeWorkflowTests",
            "Arena.Tests.Editor.MovementRegressionTests",
            "Arena.Tests.Editor.FixedActionPredictionContractTests",
            "Arena.Tests.Editor.RuntimeOrchestrationRegressionTests",
        };

        [MenuItem("Arena/Validate Compatibility Cleanup")]
        public static void Run()
        {
            if (Application.isBatchMode || EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Run in the normal Unity Editor outside Play Mode.");
            string[] args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, "-arenaVerificationOutput");
            if (index == args.Length - 1) throw new ArgumentException("-arenaVerificationOutput requires a path.");
            string output = Path.GetFullPath(index >= 0 ? args[index + 1] : "Logs/CompatibilityCleanupVerification");
            int filterIndex = Array.IndexOf(args, "-arenaVerificationFilter");
            if (filterIndex == args.Length - 1) throw new ArgumentException("-arenaVerificationFilter requires test names.");
            string[] fixtures = filterIndex >= 0 ? args[filterIndex + 1].Split(';') : Fixtures;
            foreach (string fixture in fixtures)
                if (!fixture.StartsWith("Arena.Tests.Editor", StringComparison.Ordinal)
                    && !fixture.StartsWith("Arena.EditModeTests", StringComparison.Ordinal))
                    throw new ArgumentException("Select only first-party verification tests.");
            Directory.CreateDirectory(output);
            var evidence = EditModeVerification.Run(fixtures, Path.Combine(output, "tests.xml"));
            var result = new Result
            {
                completed = evidence.Completed, passed = evidence.Passed, failed = evidence.Failed,
                requested = fixtures, executed = evidence.ExecutedNames, errors = evidence.Errors(),
            };
            File.WriteAllText(Path.Combine(output, "results.json"), JsonUtility.ToJson(result, true));
            if (result.errors.Length != 0)
                throw new InvalidOperationException("Compatibility verification failed:\n" + string.Join("\n", result.errors));
            Debug.Log($"[CompatibilityCleanupVerification] {result.passed} named native tests passed. Evidence: {output}");
        }

        // Unity -executeMethod may exit zero after a logged exception. This entry point supplies an explicit exit code.
        public static void RunFromCommandLine()
        {
            if (Application.isBatchMode) throw new InvalidOperationException("Normal Unity Editor only.");
            try { Run(); EditorApplication.Exit(0); }
            catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(1); }
        }
    }
}
