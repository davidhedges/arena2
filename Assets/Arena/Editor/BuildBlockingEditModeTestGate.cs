#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace Arena.Editor
{
    internal sealed class BuildBlockingEditModeTestGate : IPreprocessBuildWithReport
    {
        private const string BuildBlockingTestAssembly = "Arena.EditModeTests";

        public int callbackOrder => 0;

        public void OnPreprocessBuild(BuildReport report)
        {
            EditModeTestRunResult result = RunBuildBlockingEditModeTests();
            if (!result.Completed)
            {
                throw new BuildFailedException(
                    $"Build blocked: edit-mode test run '{BuildBlockingTestAssembly}' did not complete.");
            }

            if (result.HasFailures)
            {
                throw new BuildFailedException(BuildFailureMessage(result));
            }
        }

        [MenuItem("Arena/Validate Build-Blocking EditMode Tests")]
        private static void ValidateBuildBlockingEditModeTests()
        {
            RunAndThrowOnFailure();
        }

        public static void ValidateBuildBlockingEditModeTestsBatch()
        {
            RunAndThrowOnFailure();
        }

        public static void ValidateCharacterAppearanceEditModeTestsBatch()
        {
            RunAndThrowOnFailure("Arena.Tests.Editor.CharacterAppearanceCatalogTests");
        }

        private static void RunAndThrowOnFailure(string? testName = null)
        {
            EditModeTestRunResult result = RunBuildBlockingEditModeTests(testName);
            if (!result.Completed)
            {
                throw new InvalidOperationException(
                    $"Edit-mode test run '{BuildBlockingTestAssembly}' did not complete.");
            }

            if (result.HasFailures)
            {
                throw new InvalidOperationException(BuildFailureMessage(result));
            }

            Debug.Log(
                $"[BuildBlockingEditModeTestGate] Passed {result.PassCount} build-blocking edit-mode tests in assembly '{BuildBlockingTestAssembly}'.");
        }

        private static EditModeTestRunResult RunBuildBlockingEditModeTests(string? testName = null)
        {
            var runner = ScriptableObject.CreateInstance<TestRunnerApi>();
            var callback = new BuildBlockingCallbacks();
            var filter = new Filter
            {
                testMode = TestMode.EditMode,
                assemblyNames = new[] { BuildBlockingTestAssembly },
            };
            if (!string.IsNullOrWhiteSpace(testName))
                filter.testNames = new[] { testName };

            runner.RegisterCallbacks(callback);
            try
            {
                runner.Execute(new ExecutionSettings(filter)
                {
                    runSynchronously = true,
                });
            }
            finally
            {
                runner.UnregisterCallbacks(callback);
                ScriptableObject.DestroyImmediate(runner);
            }

            return callback.Result;
        }

        private static string BuildFailureMessage(EditModeTestRunResult result)
        {
            var message = new StringBuilder();
            message.AppendLine(
                $"Build blocked: {result.FailCount} edit-mode test(s) failed in assembly '{BuildBlockingTestAssembly}'.");

            foreach (string failure in result.Failures)
                message.AppendLine($"- {failure}");

            return message.ToString().TrimEnd();
        }

        private sealed class BuildBlockingCallbacks : ICallbacks
        {
            private readonly List<string> _failures = new();

            public EditModeTestRunResult Result { get; private set; } = EditModeTestRunResult.Incomplete;

            public void RunStarted(ITestAdaptor testsToRun)
            {
            }

            public void RunFinished(ITestResultAdaptor result)
            {
                _failures.Clear();
                AppendLeafFailures(result, _failures);

                Result = new EditModeTestRunResult(
                    completed: true,
                    passCount: result.PassCount,
                    failCount: result.FailCount,
                    failures: _failures.ToArray());
            }

            public void TestStarted(ITestAdaptor test)
            {
            }

            public void TestFinished(ITestResultAdaptor result)
            {
            }

            private static void AppendLeafFailures(ITestResultAdaptor result, List<string> failures)
            {
                if (result.HasChildren)
                {
                    foreach (ITestResultAdaptor child in result.Children)
                        AppendLeafFailures(child, failures);
                    return;
                }

                if (result.TestStatus != TestStatus.Failed)
                    return;

                string detail = string.IsNullOrWhiteSpace(result.Message)
                    ? result.FullName
                    : $"{result.FullName}: {result.Message}";
                failures.Add(detail);
            }
        }

        private readonly struct EditModeTestRunResult
        {
            public static readonly EditModeTestRunResult Incomplete = new(
                completed: false,
                passCount: 0,
                failCount: 0,
                failures: Array.Empty<string>());

            public EditModeTestRunResult(bool completed, int passCount, int failCount, string[] failures)
            {
                Completed = completed;
                PassCount = passCount;
                FailCount = failCount;
                Failures = failures;
            }

            public bool Completed { get; }
            public int PassCount { get; }
            public int FailCount { get; }
            public string[] Failures { get; }
            public bool HasFailures => FailCount > 0 || Failures.Length > 0;
        }
    }
}
